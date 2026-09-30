using System.Buffers.Binary;
using System.Numerics;
using LibraDex;
using LibraDex.Layouts;
using LibraDex.Views;

internal static partial class RawHarness
{
    /// <summary>
    /// Forces an `FS32-16` shelf transform behind an existing child router with one additional shared-stem byte before the split boundary.<br/>
    /// The proof requires only the observed `0x22` stem to continue from the transformed source offset, requires adjacent sibling routes to remain unset, and repeats those checks after reopen.<br/>
    /// It also walks and reads the inserted tuple's right shelf so an apparently sparse router cannot pass without preserving the full widened identity path.<br/>
    /// </summary>
    /// <param name="args">Harness arguments; `--path` selects the isolated catalog file.<br/></param>
    /// <returns>Zero when live routing, reopen routing, exact-stem ownership, and inserted-shelf validity all pass.<br/></returns>
    private static int RunFixed32Scalar16WalkedTransformSanity(string[] args)
    {
        string path = GetOption(args, "--path", Path.Combine(@"T:\LibraDex", "fs32-16-walked-transform-sanity.lbdx"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Delete(path);

        Fixed32Scalar16Profile profile = Fixed32Scalar16Profile.Default40KiB;
        int leftCount = profile.MaxItemCount / 2;
        int rightCount = profile.MaxItemCount - leftCount;
        ulong[] key0s = new ulong[profile.MaxItemCount];
        ulong[] key1s = new ulong[profile.MaxItemCount];
        ulong[] key2s = new ulong[profile.MaxItemCount];
        ulong[] key3s = new ulong[profile.MaxItemCount];
        ulong[] encodedIdentities = new ulong[profile.MaxItemCount];
        const ulong leftBase0 = 0x0000_2200_0000_0000UL;
        const ulong rightBase0 = 0x0000_2280_0000_0000UL;
        for (int i = 0; i < leftCount; i++)
        {
            key0s[i] = leftBase0;
            key3s[i] = (ulong)i;
            encodedIdentities[i] = (ulong)i;
        }

        for (int i = 0; i < rightCount; i++)
        {
            int target = leftCount + i;
            key0s[target] = rightBase0;
            key3s[target] = (ulong)i;
            encodedIdentities[target] = (ulong)i;
        }

        const ulong insertedKey0 = rightBase0;
        const ulong insertedKey1 = 0;
        const ulong insertedKey2 = 0;
        const ulong insertedKey3 = 10_000;
        const ulong insertedIdentity = 10_000;
        DataKernelOptions options = new(
            AppendBufferSize: DefaultAppendBufferSize,
            ReservedPrefixBytes: 0,
            FlushToDiskOnCommit: false,
            MaxCommitGapCoalesceBytes: 512);
        SuperblockDeveloperMetadata metadata = new(
            DevIdentity: "LibraDexFS3216P5",
            DevCustomText: "fs32-16 exact-stem walked transform",
            DevGuid: Guid.Parse("86b69b48-5b3c-4b39-9f37-888ecdef5618"),
            DevDate1UtcTicks: 1,
            DevDate2UtcTicks: 2,
            DevNumber: 3216);

        long rootOffset;
        long childRouterOffset;
        long sourceShelfOffset;
        long splitRouterOffset;
        long rightShelfOffset;
        using (LibraDexFileSession session = LibraDexFileSession.Initialize(path, options, metadata, DataKernelTelemetryOptions.EnabledOptions))
        {
            (RouterSnapshot root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "f3216p5", 0));
            rootOffset = root.Offset;
            (childRouterOffset, sourceShelfOffset, _) = session.CreateFixed32Scalar16ShelfBehindChildRouter(
                rootOffset,
                0x00,
                0x00,
                profile,
                key0s,
                key1s,
                key2s,
                key3s,
                encodedIdentities);

            (
                long transformedRouterOffset,
                _,
                rightShelfOffset,
                _,
                _,
                Fixed32Scalar16InsertResult insertResult,
                DataKernelCommitTelemetry commit) = session.SplitWalkedRoutedFixed32Scalar16ByShelfTransform(
                    rootOffset,
                    profile,
                    insertedKey0,
                    insertedKey1,
                    insertedKey2,
                    insertedKey3,
                    insertedIdentity,
                    maxRouterHops: 32);
            if (transformedRouterOffset != sourceShelfOffset ||
                rightShelfOffset == 0 ||
                insertResult != Fixed32Scalar16InsertResult.Inserted)
            {
                throw new InvalidDataException("FS32-16 depth-3 transform did not return the expected structural result.");
            }

            ValidateAllocatedWalkedTransformCommit(commit, profile.ShelfExtentSize, allocationHeaderPages: 1);

            if (session.FindRouterTarget(rootOffset, 0x00) != childRouterOffset ||
                session.FindRouterTarget(childRouterOffset, 0x00) != sourceShelfOffset)
            {
                throw new InvalidDataException("FS32-16 depth-3 transform changed an owning ancestor route.");
            }

            splitRouterOffset = ValidateFixed32Scalar16ExactStem(session, sourceShelfOffset);
            Fixed32Scalar16RouteTarget routed = session.WalkFixed32Scalar16RouteTarget(
                rootOffset,
                insertedKey0,
                insertedKey1,
                insertedKey2,
                insertedKey3,
                maxRouterHops: 32);
            if (routed.Kind != Fixed32Scalar16RouteTargetKind.Shelf || routed.Offset != rightShelfOffset || routed.RouterDepth != 3)
            {
                throw new InvalidDataException("FS32-16 depth-3 transform did not walk to the inserted tuple's right shelf.");
            }

            byte[] destination = new byte[profile.ShelfExtentSize];
            (long actualSplitRouterOffset, long actualShelfOffset) = session.ReadTwoLevelRoutedFixed32Scalar16Shelf(
                sourceShelfOffset,
                0x22,
                0x80,
                profile,
                destination);
            Fixed32Scalar16ReadOnly shelf = new(destination, profile);
            if (actualSplitRouterOffset != splitRouterOffset || actualShelfOffset != rightShelfOffset || !shelf.IsValid || shelf.ItemCount != rightCount + 1)
            {
                throw new InvalidDataException("FS32-16 depth-3 right shelf failed routed shape validation.");
            }
        }

        using (LibraDexFileSession reopened = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions))
        {
            if (reopened.FindRouterTarget(rootOffset, 0x00) != childRouterOffset ||
                reopened.FindRouterTarget(childRouterOffset, 0x00) != sourceShelfOffset ||
                ValidateFixed32Scalar16ExactStem(reopened, sourceShelfOffset) != splitRouterOffset)
            {
                throw new InvalidDataException("FS32-16 exact-stem routing changed after reopen.");
            }

            Fixed32Scalar16RouteTarget routed = reopened.WalkFixed32Scalar16RouteTarget(
                rootOffset,
                insertedKey0,
                insertedKey1,
                insertedKey2,
                insertedKey3,
                maxRouterHops: 32);
            if (routed.Kind != Fixed32Scalar16RouteTargetKind.Shelf || routed.Offset != rightShelfOffset || routed.RouterDepth != 3)
            {
                throw new InvalidDataException("FS32-16 inserted tuple route changed after reopen.");
            }
        }

        Console.WriteLine($"fs32-16-walked-transform-sanity ok path={path} root={rootOffset} child={childRouterOffset} transformed={sourceShelfOffset} split={splitRouterOffset} right={rightShelfOffset}");
        return 0;
    }

    /// <summary>
    /// Validates one transformed `FS32-16` source router as an exact-only depth-two stem.<br/>
    /// The observed `0x22` route must continue to the appended split router while both adjacent siblings remain unset.<br/>
    /// </summary>
    /// <param name="session">The live or reopened file session used to inspect persisted routes.<br/></param>
    /// <param name="sourceShelfOffset">The former shelf offset now containing the exact-stem router.<br/></param>
    /// <returns>The nonzero appended split-router offset selected by route `0x22`.<br/></returns>
    private static long ValidateFixed32Scalar16ExactStem(LibraDexFileSession session, long sourceShelfOffset)
    {
        long splitRouterOffset = session.FindRouterTarget(sourceShelfOffset, 0x22);
        if (splitRouterOffset == 0 ||
            session.FindRouterTarget(sourceShelfOffset, 0x21) != 0 ||
            session.FindRouterTarget(sourceShelfOffset, 0x23) != 0)
        {
            throw new InvalidDataException("FS32-16 transformed source did not preserve an exact-only `0x22` route stem.");
        }

        return splitRouterOffset;
    }

    /// <summary>
    /// Validates exact-stem route publication for the three variable-key physical families.<br/>
    /// Each scenario fills one shelf whose keys share eight leading bytes, forces the first live transform, walks every intermediate router, and requires adjacent sibling routes to remain unset.<br/>
    /// The same topology and complete inserted-row count are then verified after reopening the catalog.<br/>
    /// </summary>
    /// <param name="args">Harness arguments; `--directory`, `--key-length`, `--shared-prefix-length`, and `--max-inserts` tune the isolated proof.<br/></param>
    /// <returns>Zero when `VS8`, `VS16`, and `VV` all preserve exact-stem routing and reopen reachability.<br/></returns>
    private static int RunVarKeyCrossShapeExactStemSanity(string[] args)
    {
        string directory = GetOption(args, "--directory", Path.Combine(@"T:\LibraDex", "VarKeyExactStemSanity"));
        int keyLength = GetIntOption(args, "--key-length", 48);
        int sharedPrefixLength = GetIntOption(args, "--shared-prefix-length", 8);
        int maxInserts = GetIntOption(args, "--max-inserts", 10_000);
        if (keyLength <= sharedPrefixLength + 1 || keyLength > 1024 || sharedPrefixLength < 2 || sharedPrefixLength > 250 || maxInserts <= 0 || maxInserts > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(args), "Variable-key exact-stem sanity requires key length 3-1024, a shared prefix of at least two bytes that leaves two varying bytes, and 1-65535 maximum inserts.");
        }

        Directory.CreateDirectory(directory);
        string vs8Path = Path.Combine(directory, "vs8-exact-stem.lbdx");
        string vs16Path = Path.Combine(directory, "vs16-exact-stem.lbdx");
        string vvPath = Path.Combine(directory, "vv-exact-stem.lbdx");
        int vs8Count = ValidateVarKeyScalar8ExactStemScenario(vs8Path, keyLength, sharedPrefixLength, maxInserts);
        int vs16Count = ValidateVarKeyScalar16ExactStemScenario(vs16Path, keyLength, sharedPrefixLength, maxInserts);
        int vvCount = ValidateVarKeyVarIdentityExactStemScenario(vvPath, keyLength, sharedPrefixLength, maxInserts);

        Console.WriteLine($"var-key-exact-stem-sanity ok directory={directory} vs8={vs8Count} vs16={vs16Count} vv={vvCount} sharedPrefixLength={sharedPrefixLength}");
        return 0;
    }

    /// <summary>
    /// Forces and validates one long-shared-prefix `VS8` transform.<br/>
    /// The method stops at the first transform so later workload evolution cannot hide the topology produced by that individual mutation.<br/>
    /// </summary>
    /// <param name="path">Isolated catalog path used by the scenario.<br/></param>
    /// <param name="keyLength">Generated raw-key length.<br/></param>
    /// <param name="sharedPrefixLength">Count of leading `0x35` bytes shared by all generated keys.<br/></param>
    /// <param name="maxInserts">Maximum insert attempts allowed before declaring that no transform occurred.<br/></param>
    /// <returns>The number of rows inserted through the first transform.<br/></returns>
    private static int ValidateVarKeyScalar8ExactStemScenario(string path, int keyLength, int sharedPrefixLength, int maxInserts)
    {
        File.Delete(path);
        DataKernelOptions options = CreateDesignPerfOptions();
        long rootOffset;
        long sourceShelfOffset;
        int insertedCount = 0;
        using (LibraDexFileSession session = LibraDexFileSession.Initialize(path, options, CreateDesignPerfMetadata(8501), DataKernelTelemetryOptions.EnabledOptions))
        {
            (RouterSnapshot root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "vs8p5", 0));
            rootOffset = root.Offset;
            (sourceShelfOffset, _) = session.CreateVarKeyScalar8ShelfAndLinkRootRoute(rootOffset, 0x35, VarKeyScalar8Profile.DefaultInitial);
            for (int i = 0; i < maxInserts; i++)
            {
                byte[] key = CreateVarKeyScalar8LongSharedPrefixKey(i, keyLength, sharedPrefixLength);
                VarKeyScalar8RoutedInsertResult result = session.InsertWalkedRoutedVarKeyScalar8(rootOffset, 1024, key, CreateVarKeyScalar8Identity(i), allowDuplicateKeys: true, maxRouterHops: 32);
                if (result.InsertResult != VarKeyScalar8InsertResult.Inserted)
                {
                    throw new InvalidDataException($"VS8 exact-stem insert {i} failed with {result.Kind}/{result.InsertResult}.");
                }

                insertedCount++;
                if (result.Kind == VarKeyScalar8RoutedInsertKind.WalkedShelfTransformSplit)
                {
                    sourceShelfOffset = result.PrimaryOffset;
                    break;
                }
            }

            _ = ValidateVarKeyExactStemChain(session, sourceShelfOffset, sharedPrefixLength, 0x35, "VS8 live");
        }

        using (LibraDexFileSession reopened = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions))
        {
            _ = ValidateVarKeyExactStemChain(reopened, sourceShelfOffset, sharedPrefixLength, 0x35, "VS8 reopen");
            byte[] lower = CreateVarKeyScalar8LongSharedPrefixKey(0, keyLength, sharedPrefixLength);
            lower.AsSpan(sharedPrefixLength).Clear();
            byte[] upper = CreateVarKeyScalar8LongSharedPrefixKey(0, keyLength, sharedPrefixLength);
            upper.AsSpan(sharedPrefixLength).Fill(byte.MaxValue);
            ulong[] identities = new ulong[insertedCount];
            int copied = reopened.ReadVarKeyScalar8IdentityRange(rootOffset, 1024, lower, upper, identities);
            if (copied != insertedCount)
            {
                throw new InvalidDataException($"VS8 exact-stem reopen returned {copied} rows; expected {insertedCount}.");
            }
        }

        return insertedCount;
    }

    /// <summary>
    /// Forces and validates one long-shared-prefix `VS16` transform.<br/>
    /// The proof uses the widened identity shelf independently so shared key routing cannot inherit correctness merely from the `VS8` result.<br/>
    /// </summary>
    /// <param name="path">Isolated catalog path used by the scenario.<br/></param>
    /// <param name="keyLength">Generated raw-key length.<br/></param>
    /// <param name="sharedPrefixLength">Count of leading `0x35` bytes shared by all generated keys.<br/></param>
    /// <param name="maxInserts">Maximum insert attempts allowed before declaring that no transform occurred.<br/></param>
    /// <returns>The number of rows inserted through the first transform.<br/></returns>
    private static int ValidateVarKeyScalar16ExactStemScenario(string path, int keyLength, int sharedPrefixLength, int maxInserts)
    {
        File.Delete(path);
        DataKernelOptions options = CreateDesignPerfOptions();
        long rootOffset;
        long sourceShelfOffset;
        int insertedCount = 0;
        using (LibraDexFileSession session = LibraDexFileSession.Initialize(path, options, CreateDesignPerfMetadata(8516), DataKernelTelemetryOptions.EnabledOptions))
        {
            (RouterSnapshot root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "v16p5", 0));
            rootOffset = root.Offset;
            (sourceShelfOffset, _) = session.CreateVarKeyScalar16ShelfAndLinkRootRoute(rootOffset, 0x35, VarKeyScalar16Profile.DefaultInitial);
            for (int i = 0; i < maxInserts; i++)
            {
                byte[] key = CreateVarKeyScalar8LongSharedPrefixKey(i, keyLength, sharedPrefixLength);
                CreateVarKeyScalar16Identity(i, out ulong identityHigh, out ulong identityLow);
                VarKeyScalar16RoutedInsertResult result = session.InsertWalkedRoutedVarKeyScalar16(rootOffset, 1024, key, identityHigh, identityLow, allowDuplicateKeys: true, maxRouterHops: 32);
                if (result.InsertResult != VarKeyScalar16InsertResult.Inserted)
                {
                    throw new InvalidDataException($"VS16 exact-stem insert {i} failed with {result.Kind}/{result.InsertResult}.");
                }

                insertedCount++;
                if (result.Kind == VarKeyScalar16RoutedInsertKind.WalkedShelfTransformSplit)
                {
                    sourceShelfOffset = result.PrimaryOffset;
                    break;
                }
            }

            _ = ValidateVarKeyExactStemChain(session, sourceShelfOffset, sharedPrefixLength, 0x35, "VS16 live");
        }

        using (LibraDexFileSession reopened = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions))
        {
            _ = ValidateVarKeyExactStemChain(reopened, sourceShelfOffset, sharedPrefixLength, 0x35, "VS16 reopen");
            byte[] lower = CreateVarKeyScalar8LongSharedPrefixKey(0, keyLength, sharedPrefixLength);
            lower.AsSpan(sharedPrefixLength).Clear();
            byte[] upper = CreateVarKeyScalar8LongSharedPrefixKey(0, keyLength, sharedPrefixLength);
            upper.AsSpan(sharedPrefixLength).Fill(byte.MaxValue);
            ulong[] identityHighs = new ulong[insertedCount];
            ulong[] identityLows = new ulong[insertedCount];
            int copied = reopened.ReadVarKeyScalar16IdentityRange(rootOffset, 1024, lower, upper, identityHighs, identityLows);
            if (copied != insertedCount)
            {
                throw new InvalidDataException($"VS16 exact-stem reopen returned {copied} rows; expected {insertedCount}.");
            }
        }

        return insertedCount;
    }

    /// <summary>
    /// Forces and validates one long-shared-prefix `VV` transform.<br/>
    /// This covers the variable-key/variable-identity physical family with the same exact-stem and reopen requirements as both scalar-identity siblings.<br/>
    /// </summary>
    /// <param name="path">Isolated catalog path used by the scenario.<br/></param>
    /// <param name="keyLength">Generated raw-key length.<br/></param>
    /// <param name="sharedPrefixLength">Count of leading `0x35` bytes shared by all generated keys.<br/></param>
    /// <param name="maxInserts">Maximum insert attempts allowed before declaring that no transform occurred.<br/></param>
    /// <returns>The number of rows inserted through the first transform.<br/></returns>
    private static int ValidateVarKeyVarIdentityExactStemScenario(string path, int keyLength, int sharedPrefixLength, int maxInserts)
    {
        File.Delete(path);
        DataKernelOptions options = CreateDesignPerfOptions();
        long rootOffset;
        long sourceShelfOffset;
        int insertedCount = 0;
        using (LibraDexFileSession session = LibraDexFileSession.Initialize(path, options, CreateDesignPerfMetadata(8588), DataKernelTelemetryOptions.EnabledOptions))
        {
            (RouterSnapshot root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "vvp5", 0));
            rootOffset = root.Offset;
            (sourceShelfOffset, _) = session.CreateVarKeyVarIdentityShelfAndLinkRootRoute(rootOffset, 0x35, VarKeyVarIdentityProfile.DefaultInitial);
            for (int i = 0; i < maxInserts; i++)
            {
                byte[] key = CreateVarKeyScalar8LongSharedPrefixKey(i, keyLength, sharedPrefixLength);
                byte[] identity = CreateScalar8VarIdentity(i, 48);
                VarKeyVarIdentityRoutedInsertResult result = session.InsertWalkedRoutedVarKeyVarIdentity(rootOffset, 1024, 1024, key, identity, allowDuplicateKeys: true, maxRouterHops: 32);
                if (result.InsertResult != VarKeyVarIdentityInsertResult.Inserted)
                {
                    throw new InvalidDataException($"VV exact-stem insert {i} failed with {result.Kind}/{result.InsertResult}.");
                }

                insertedCount++;
                if (result.Kind == VarKeyVarIdentityRoutedInsertKind.WalkedShelfTransformSplit)
                {
                    sourceShelfOffset = result.SourceShelfOffset;
                    break;
                }
            }

            _ = ValidateVarKeyExactStemChain(session, sourceShelfOffset, sharedPrefixLength, 0x35, "VV live");
        }

        using (LibraDexFileSession reopened = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions))
        {
            _ = ValidateVarKeyExactStemChain(reopened, sourceShelfOffset, sharedPrefixLength, 0x35, "VV reopen");
            byte[] lower = CreateVarKeyScalar8LongSharedPrefixKey(0, keyLength, sharedPrefixLength);
            lower.AsSpan(sharedPrefixLength).Clear();
            byte[] upper = CreateVarKeyScalar8LongSharedPrefixKey(0, keyLength, sharedPrefixLength);
            upper.AsSpan(sharedPrefixLength).Fill(byte.MaxValue);
            using VarKeyVarIdentityRangeReader reader = reopened.OpenVarKeyVarIdentityRangeReader(rootOffset, 1024, 1024, lower, upper, maxRouterHops: 32);
            if (reader.Count != insertedCount)
            {
                throw new InvalidDataException($"VV exact-stem reopen returned {reader.Count} rows; expected {insertedCount}.");
            }
        }

        return insertedCount;
    }

    /// <summary>
    /// Walks every intermediate expanded router in a long shared-prefix variable-key transform.<br/>
    /// At each depth the one observed stem byte must be the only populated local route; both adjacent siblings must remain unset, preventing broad ownership from multiplying later writes.<br/>
    /// The returned router is the first divergence router immediately after the shared prefix.<br/>
    /// </summary>
    /// <param name="session">The live or reopened session used to read persisted router pages.<br/></param>
    /// <param name="sourceRouterOffset">The former shelf offset rewritten as the first router in the chain.<br/></param>
    /// <param name="sharedPrefixLength">Number of shared key bytes, including the byte consumed by the root router.<br/></param>
    /// <param name="stemByte">The exact repeated shared-prefix byte expected at each intermediate route.<br/></param>
    /// <param name="shape">Diagnostic shape label included in validation failures.<br/></param>
    /// <returns>The divergence-router offset at key depth <paramref name="sharedPrefixLength"/>.<br/></returns>
    private static long ValidateVarKeyExactStemChain(
        LibraDexFileSession session,
        long sourceRouterOffset,
        int sharedPrefixLength,
        byte stemByte,
        string shape)
    {
        long currentOffset = sourceRouterOffset;
        byte[] routerBytes = new byte[RouterLayout.Size];
        for (int expectedDepth = 1; expectedDepth < sharedPrefixLength; expectedDepth++)
        {
            session.ReadRouterPageForRangeScan(currentOffset, routerBytes);
            RouterReader router = new(routerBytes);
            if (!router.IsValid || !router.HasDirectIndex || router.PrefixByteCount != 1 || router.KeyDepth != expectedDepth)
            {
                throw new InvalidDataException($"{shape} exact-stem router at offset {currentOffset} has invalid shape/depth. valid={router.IsValid} direct={router.HasDirectIndex} prefixBytes={router.PrefixByteCount} depth={router.KeyDepth} expectedDepth={expectedDepth}.");
            }

            long nextOffset = router.FindTarget(stemByte);
            long lowerSibling = router.FindTarget(checked((byte)(stemByte - 1)));
            long upperSibling = router.FindTarget(checked((byte)(stemByte + 1)));
            if (nextOffset == 0 || lowerSibling != 0 || upperSibling != 0)
            {
                throw new InvalidDataException($"{shape} router depth {expectedDepth} did not preserve exact-only stem 0x{stemByte:X2}. next={nextOffset} lower={lowerSibling} upper={upperSibling}.");
            }

            currentOffset = nextOffset;
        }

        session.ReadRouterPageForRangeScan(currentOffset, routerBytes);
        RouterReader splitRouter = new(routerBytes);
        if (!splitRouter.IsValid || !splitRouter.HasDirectIndex || splitRouter.PrefixByteCount != 1 || splitRouter.KeyDepth != sharedPrefixLength)
        {
            throw new InvalidDataException($"{shape} divergence router at offset {currentOffset} has invalid shape/depth. valid={splitRouter.IsValid} direct={splitRouter.HasDirectIndex} prefixBytes={splitRouter.PrefixByteCount} depth={splitRouter.KeyDepth} expectedDepth={sharedPrefixLength}.");
        }

        return currentOffset;
    }

    /// <summary>
    /// Proves shelf-local exact deletion for the shared terminal variable-identity representation used by `SV8` and `VV`.<br/>
    /// Each scenario creates a many-shelf exhausted-key chain, removes complete middle, tail, and head shelves, then removes every survivor while requiring the closed-file length to remain constant.<br/>
    /// Live and reopened counts plus cleared root endpoints prove that local repacks and unlinks preserve the complete logical delete contract.<br/>
    /// </summary>
    /// <param name="args">Harness arguments; `--directory`, `--count`, and `--identity-length` tune the isolated files.<br/></param>
    /// <returns>Zero when both physical families pass local mutation, root/tail repair, file-growth, and reopen validation.<br/></returns>
    private static int RunTerminalVarIdentityDeleteLocalitySanity(string[] args)
    {
        string directory = GetOption(args, "--directory", Path.Combine(@"T:\LibraDex", "TerminalVarIdentityDeleteLocality"));
        int count = GetIntOption(args, "--count", 500);
        int identityLength = GetIntOption(args, "--identity-length", 256);
        if (count < 64 || identityLength < 16 || identityLength > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(args), "Terminal var-identity delete locality requires at least 64 identities and identity length 16-1024.");
        }

        Directory.CreateDirectory(directory);
        (int sv8Shelves, TimeSpan sv8Elapsed, long sv8Bytes, long sv8Allocated) = ValidateScalar8VarIdentityDeleteLocality(
            Path.Combine(directory, "sv8-terminal-delete-locality.lbdx"),
            count,
            identityLength);
        (int vvShelves, TimeSpan vvElapsed, long vvBytes, long vvAllocated) = ValidateVarKeyVarIdentityDeleteLocality(
            Path.Combine(directory, "vv-terminal-delete-locality.lbdx"),
            count,
            identityLength);

        Console.WriteLine("shape,items,terminal_shelves,delete_ms,file_bytes,allocated_bytes");
        Console.WriteLine($"SV8,{count},{sv8Shelves},{sv8Elapsed.TotalMilliseconds:F3},{sv8Bytes},{sv8Allocated}");
        Console.WriteLine($"VV,{count},{vvShelves},{vvElapsed.TotalMilliseconds:F3},{vvBytes},{vvAllocated}");
        Console.WriteLine("terminal-var-identity-delete-locality-sanity ok");
        return 0;
    }

    /// <summary>
    /// Builds one exhausted-key `SV8` chain and executes the shared terminal variable-identity local-delete plan.<br/>
    /// The generated identities are fixed-length and monotonically byte-ordered so every shelf boundary and expected survivor is deterministic.<br/>
    /// </summary>
    /// <param name="path">The isolated file-backed catalog path.<br/></param>
    /// <param name="count">The duplicate-key identity count.<br/></param>
    /// <param name="identityLength">The raw identity length in bytes.<br/></param>
    /// <returns>The initial terminal shelf count, delete elapsed time, stable file bytes, and managed allocation delta.<br/></returns>
    private static (int ShelfCount, TimeSpan Elapsed, long FileBytes, long AllocatedBytes) ValidateScalar8VarIdentityDeleteLocality(
        string path,
        int count,
        int identityLength)
    {
        File.Delete(path);
        DataKernelOptions options = CreateDesignPerfOptions();
        const ulong key = 0x4400_0000_0000_0000UL;
        byte[][] identities = CreateOrderedTerminalVarIdentities(count, identityLength);
        long rootRouterOffset;
        using (LibraDexFileSession session = LibraDexFileSession.Initialize(path, options, CreateDesignPerfMetadata(8608), DataKernelTelemetryOptions.EnabledOptions))
        {
            (RouterSnapshot root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "sv8delp5", 0));
            rootRouterOffset = root.Offset;
            _ = session.CreateScalar8VarIdentityShelfAndLinkRootRoute(rootRouterOffset, 0x44, Scalar8VarIdentityProfile.DefaultInitial);
            for (int i = 0; i < count; i++)
            {
                Scalar8VarIdentityRoutedInsertResult result = session.InsertWalkedRoutedScalar8VarIdentity(rootRouterOffset, identityLength, key, identities[i], allowDuplicateKeys: true, maxRouterHops: 32);
                if (result.InsertResult != Scalar8VarIdentityInsertResult.Inserted)
                {
                    throw new InvalidDataException($"SV8 terminal-delete setup insert {i} failed with {result.Kind}/{result.InsertResult}.");
                }
            }
        }

        long stableFileBytes = new FileInfo(path).Length;
        int shelfCount;
        TimeSpan elapsed;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        using (LibraDexFileSession session = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions))
        {
            Scalar8VarIdentityRoutePathTarget terminal = session.WalkScalar8VarIdentityRoutePathTarget(rootRouterOffset, key, maxRouterHops: 32);
            if (terminal.Target.Kind != Scalar8VarIdentityRouteTargetKind.TerminalVarIdentityRoot)
            {
                throw new InvalidDataException($"SV8 terminal-delete setup resolved {terminal.Target.Kind} instead of a terminal root.");
            }

            using (LibraDexFileSessionDurabilityBatch aborted = session.BeginDurabilityBatch())
            {
                if (!session.DeleteScalar8VarIdentityExactTuple(rootRouterOffset, identityLength, key, identities[count / 2], maxRouterHops: 32))
                {
                    throw new InvalidDataException("SV8 terminal-delete batch-abort probe did not stage the expected deletion.");
                }

                using Scalar8VarIdentityRangeReader stagedReader = session.OpenScalar8VarIdentityRangeReader(rootRouterOffset, identityLength, key, key);
                if (stagedReader.Count != count - 1)
                {
                    throw new InvalidDataException($"SV8 terminal-delete batch-abort staged count was {stagedReader.Count}; expected {count - 1}.");
                }

                _ = aborted.Abort();
            }

            using (Scalar8VarIdentityRangeReader restoredReader = session.OpenScalar8VarIdentityRangeReader(rootRouterOffset, identityLength, key, key))
            {
                if (restoredReader.Count != count)
                {
                    throw new InvalidDataException($"SV8 terminal-delete batch abort restored {restoredReader.Count} rows; expected {count}.");
                }
            }

            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            shelfCount = ExecuteTerminalVarIdentityDeletePlan(
                session,
                terminal.Target.Offset,
                path,
                stableFileBytes,
                identities,
                identity => session.DeleteScalar8VarIdentityExactTuple(rootRouterOffset, identityLength, key, identity, maxRouterHops: 32),
                () =>
                {
                    using Scalar8VarIdentityRangeReader reader = session.OpenScalar8VarIdentityRangeReader(rootRouterOffset, identityLength, key, key);
                    return reader.Count;
                },
                "SV8");
            watch.Stop();
            elapsed = watch.Elapsed;
        }

        ValidateEmptyScalar8VarIdentityTerminalAfterReopen(path, options, rootRouterOffset, key, identityLength, stableFileBytes);
        return (shelfCount, elapsed, stableFileBytes, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
    }

    /// <summary>
    /// Builds one exhausted-key `VV` chain and executes the shared terminal variable-identity local-delete plan.<br/>
    /// This independently verifies that the variable-key wrapper preserves the same local physical mutation contract as `SV8`.<br/>
    /// </summary>
    /// <param name="path">The isolated file-backed catalog path.<br/></param>
    /// <param name="count">The duplicate-key identity count.<br/></param>
    /// <param name="identityLength">The raw identity length in bytes.<br/></param>
    /// <returns>The initial terminal shelf count, delete elapsed time, stable file bytes, and managed allocation delta.<br/></returns>
    private static (int ShelfCount, TimeSpan Elapsed, long FileBytes, long AllocatedBytes) ValidateVarKeyVarIdentityDeleteLocality(
        string path,
        int count,
        int identityLength)
    {
        File.Delete(path);
        DataKernelOptions options = CreateDesignPerfOptions();
        byte[] key = new byte[48];
        key.AsSpan().Fill(0x44);
        byte[][] identities = CreateOrderedTerminalVarIdentities(count, identityLength);
        long rootRouterOffset;
        using (LibraDexFileSession session = LibraDexFileSession.Initialize(path, options, CreateDesignPerfMetadata(8688), DataKernelTelemetryOptions.EnabledOptions))
        {
            (RouterSnapshot root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "vvdelp5", 0));
            rootRouterOffset = root.Offset;
            _ = session.CreateVarKeyVarIdentityShelfAndLinkRootRoute(rootRouterOffset, 0x44, VarKeyVarIdentityProfile.DefaultInitial);
            for (int i = 0; i < count; i++)
            {
                VarKeyVarIdentityRoutedInsertResult result = session.InsertWalkedRoutedVarKeyVarIdentity(rootRouterOffset, 1024, 1024, key, identities[i], allowDuplicateKeys: true, maxRouterHops: 128);
                if (result.InsertResult != VarKeyVarIdentityInsertResult.Inserted)
                {
                    throw new InvalidDataException($"VV terminal-delete setup insert {i} failed with {result.Kind}/{result.InsertResult}.");
                }
            }
        }

        long stableFileBytes = new FileInfo(path).Length;
        int shelfCount;
        TimeSpan elapsed;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        using (LibraDexFileSession session = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions))
        {
            VarKeyVarIdentityRoutePathTarget terminal = session.WalkVarKeyVarIdentityRoutePathTarget(rootRouterOffset, key, maxRouterHops: 128);
            if (terminal.Target.Kind != VarKeyVarIdentityRouteTargetKind.TerminalVarIdentityRoot)
            {
                throw new InvalidDataException($"VV terminal-delete setup resolved {terminal.Target.Kind} instead of a terminal root.");
            }

            using (LibraDexFileSessionDurabilityBatch aborted = session.BeginDurabilityBatch())
            {
                if (!session.DeleteVarKeyVarIdentityExactTuple(rootRouterOffset, 1024, 1024, key, identities[count / 2], maxRouterHops: 128))
                {
                    throw new InvalidDataException("VV terminal-delete batch-abort probe did not stage the expected deletion.");
                }

                using VarKeyVarIdentityRangeReader stagedReader = session.OpenVarKeyVarIdentityRangeReader(rootRouterOffset, 1024, 1024, key, key, maxRouterHops: 128);
                if (stagedReader.Count != count - 1)
                {
                    throw new InvalidDataException($"VV terminal-delete batch-abort staged count was {stagedReader.Count}; expected {count - 1}.");
                }

                _ = aborted.Abort();
            }

            using (VarKeyVarIdentityRangeReader restoredReader = session.OpenVarKeyVarIdentityRangeReader(rootRouterOffset, 1024, 1024, key, key, maxRouterHops: 128))
            {
                if (restoredReader.Count != count)
                {
                    throw new InvalidDataException($"VV terminal-delete batch abort restored {restoredReader.Count} rows; expected {count}.");
                }
            }

            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            shelfCount = ExecuteTerminalVarIdentityDeletePlan(
                session,
                terminal.Target.Offset,
                path,
                stableFileBytes,
                identities,
                identity => session.DeleteVarKeyVarIdentityExactTuple(rootRouterOffset, 1024, 1024, key, identity, maxRouterHops: 128),
                () =>
                {
                    using VarKeyVarIdentityRangeReader reader = session.OpenVarKeyVarIdentityRangeReader(rootRouterOffset, 1024, 1024, key, key, maxRouterHops: 128);
                    return reader.Count;
                },
                "VV");
            watch.Stop();
            elapsed = watch.Elapsed;
        }

        ValidateEmptyVarKeyVarIdentityTerminalAfterReopen(path, options, rootRouterOffset, key, stableFileBytes);
        return (shelfCount, elapsed, stableFileBytes, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
    }

    /// <summary>
    /// Executes middle-, tail-, head-, and survivor-deletion phases over one terminal variable-identity chain.<br/>
    /// Capturing shelf membership before mutation ensures all three unlink positions are exercised deliberately; the final pass removes every remaining identity and validates cleared root endpoints.<br/>
    /// </summary>
    /// <param name="session">The open file session that owns the terminal chain.<br/></param>
    /// <param name="terminalRootOffset">The terminal identity root offset.<br/></param>
    /// <param name="path">The catalog path used for file-length checks.<br/></param>
    /// <param name="stableFileBytes">The closed-file length before deletion begins.<br/></param>
    /// <param name="allIdentities">Every inserted identity in sorted order.<br/></param>
    /// <param name="delete">The physical-family exact-delete callback.<br/></param>
    /// <param name="readCount">The physical-family exact-key count callback.<br/></param>
    /// <param name="shape">Diagnostic shape label.<br/></param>
    /// <returns>The number of terminal shelves present before deletion.<br/></returns>
    private static int ExecuteTerminalVarIdentityDeletePlan(
        LibraDexFileSession session,
        long terminalRootOffset,
        string path,
        long stableFileBytes,
        ReadOnlySpan<byte[]> allIdentities,
        Func<byte[], bool> delete,
        Func<int> readCount,
        string shape)
    {
        List<TerminalVarIdentityShelfSnapshot> shelves = ReadTerminalVarIdentityShelfSnapshots(session, terminalRootOffset);
        if (shelves.Count < 4)
        {
            throw new InvalidDataException($"{shape} terminal-delete locality requires at least four terminal shelves; observed {shelves.Count}.");
        }

        HashSet<ulong> deletedOrdinals = [];
        DeleteTerminalVarIdentityShelfGroup(shelves[shelves.Count / 2], delete, deletedOrdinals, shape, "middle");
        RequireStableTerminalDeleteFileLength(path, stableFileBytes, shape, "middle");
        DeleteTerminalVarIdentityShelfGroup(shelves[^1], delete, deletedOrdinals, shape, "tail");
        RequireStableTerminalDeleteFileLength(path, stableFileBytes, shape, "tail");
        DeleteTerminalVarIdentityShelfGroup(shelves[0], delete, deletedOrdinals, shape, "head");
        RequireStableTerminalDeleteFileLength(path, stableFileBytes, shape, "head");

        for (int i = 0; i < allIdentities.Length; i++)
        {
            ulong ordinal = ReadTerminalVarIdentityOrdinal(allIdentities[i]);
            if (deletedOrdinals.Add(ordinal) && !delete(allIdentities[i]))
            {
                throw new InvalidDataException($"{shape} terminal-delete survivor phase did not remove ordinal {ordinal}.");
            }
        }

        RequireStableTerminalDeleteFileLength(path, stableFileBytes, shape, "all");
        if (readCount() != 0)
        {
            throw new InvalidDataException($"{shape} terminal-delete locality left rows after deleting the complete identity set.");
        }

        byte[] rootBytes = session.ReadTerminalIdentityRootBytes(terminalRootOffset);
        if (TerminalIdentityRootLayout.ReadFirstShelfOffset(rootBytes) != 0 ||
            TerminalIdentityRootLayout.ReadTailShelfOffset(rootBytes) != 0)
        {
            throw new InvalidDataException($"{shape} terminal-delete locality did not clear both terminal root endpoints.");
        }

        return shelves.Count;
    }

    /// <summary>
    /// Deletes every identity captured in one original terminal shelf and records its deterministic ordinal.<br/>
    /// Removing the shelf's final identity forces the local unlink path for the caller-selected middle, tail, or head position.<br/>
    /// </summary>
    /// <param name="shelf">The original terminal shelf membership snapshot.<br/></param>
    /// <param name="delete">The exact-delete callback.<br/></param>
    /// <param name="deletedOrdinals">The set of identities already removed by directed phases.<br/></param>
    /// <param name="shape">Diagnostic shape label.<br/></param>
    /// <param name="phase">Directed unlink position label.<br/></param>
    private static void DeleteTerminalVarIdentityShelfGroup(
        TerminalVarIdentityShelfSnapshot shelf,
        Func<byte[], bool> delete,
        HashSet<ulong> deletedOrdinals,
        string shape,
        string phase)
    {
        foreach (byte[] identity in shelf.Identities)
        {
            ulong ordinal = ReadTerminalVarIdentityOrdinal(identity);
            if (!deletedOrdinals.Add(ordinal) || !delete(identity))
            {
                throw new InvalidDataException($"{shape} terminal-delete {phase} phase did not remove ordinal {ordinal} from shelf {shelf.Offset}.");
            }
        }
    }

    /// <summary>
    /// Reads the terminal variable-identity chain into a harness-only shelf-membership snapshot.<br/>
    /// The method validates every shelf, rejects cycles, and copies only live identities needed to direct subsequent unlink-position tests.<br/>
    /// </summary>
    /// <param name="session">The open session used to read terminal root and shelf bytes.<br/></param>
    /// <param name="terminalRootOffset">The terminal identity root offset.<br/></param>
    /// <returns>Terminal shelves in linked-list order.<br/></returns>
    private static List<TerminalVarIdentityShelfSnapshot> ReadTerminalVarIdentityShelfSnapshots(LibraDexFileSession session, long terminalRootOffset)
    {
        byte[] rootBytes = session.ReadTerminalIdentityRootBytes(terminalRootOffset);
        int shelfExtentSize = TerminalIdentityRootLayout.ReadShelfExtentSize(rootBytes);
        long shelfOffset = TerminalIdentityRootLayout.ReadFirstShelfOffset(rootBytes);
        HashSet<long> visited = [];
        List<TerminalVarIdentityShelfSnapshot> shelves = [];
        while (shelfOffset != 0)
        {
            if (!visited.Add(shelfOffset))
            {
                throw new InvalidDataException($"Terminal variable-identity chain contains a cycle at offset {shelfOffset}.");
            }

            byte[] shelfBytes = session.ReadTerminalVarIdentityShelfBytes(shelfOffset, shelfExtentSize);
            TerminalVarIdentityShelfLayout.Validate(shelfBytes, shelfExtentSize);
            int itemCount = TerminalVarIdentityShelfLayout.ReadItemCount(shelfBytes);
            byte[][] identities = new byte[itemCount][];
            for (int i = 0; i < itemCount; i++)
            {
                identities[i] = TerminalVarIdentityShelfLayout.ReadIdentityAt(shelfBytes, i).ToArray();
            }

            long nextOffset = TerminalVarIdentityShelfLayout.ReadNextShelfOffset(shelfBytes);
            shelves.Add(new TerminalVarIdentityShelfSnapshot(shelfOffset, nextOffset, identities));
            shelfOffset = nextOffset;
        }

        return shelves;
    }

    /// <summary>
    /// Creates fixed-length identities whose final eight bytes contain a one-based big-endian ordinal.<br/>
    /// Equal length and big-endian encoding make numeric and byte-ordinal order identical for the complete fixture.<br/>
    /// </summary>
    /// <param name="count">Number of identities to create.<br/></param>
    /// <param name="identityLength">Length of each identity in bytes.<br/></param>
    /// <returns>The ordered identity array.<br/></returns>
    private static byte[][] CreateOrderedTerminalVarIdentities(int count, int identityLength)
    {
        byte[][] identities = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            byte[] identity = new byte[identityLength];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(identity.AsSpan(identityLength - sizeof(ulong)), checked((ulong)i + 1UL));
            identities[i] = identity;
        }

        return identities;
    }

    /// <summary>
    /// Reads the deterministic one-based ordinal embedded in a locality-fixture identity.<br/>
    /// </summary>
    /// <param name="identity">The fixed-length identity bytes.<br/></param>
    /// <returns>The embedded one-based ordinal.<br/></returns>
    private static ulong ReadTerminalVarIdentityOrdinal(ReadOnlySpan<byte> identity)
        => System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(identity.Slice(identity.Length - sizeof(ulong)));

    /// <summary>
    /// Requires a local terminal delete phase to leave the catalog file length unchanged.<br/>
    /// Any growth indicates append/rematerialize behavior because the accepted local path rewrites only existing extents and endpoint metadata.<br/>
    /// </summary>
    /// <param name="path">The catalog path.<br/></param>
    /// <param name="expectedBytes">The stable pre-delete file length.<br/></param>
    /// <param name="shape">Diagnostic shape label.<br/></param>
    /// <param name="phase">Delete phase label.<br/></param>
    private static void RequireStableTerminalDeleteFileLength(string path, long expectedBytes, string shape, string phase)
    {
        long actualBytes = new FileInfo(path).Length;
        if (actualBytes != expectedBytes)
        {
            throw new InvalidDataException($"{shape} terminal-delete {phase} phase changed file length from {expectedBytes} to {actualBytes}.");
        }
    }

    /// <summary>
    /// Reopens an emptied `SV8` terminal route and validates zero rows, cleared endpoints, and stable physical bytes.<br/>
    /// </summary>
    /// <param name="path">The catalog path.<br/></param>
    /// <param name="options">The DataKernel options used to reopen it.<br/></param>
    /// <param name="rootRouterOffset">The `SV8` root router offset.<br/></param>
    /// <param name="key">The exhausted scalar key.<br/></param>
    /// <param name="identityLength">The configured maximum identity length.<br/></param>
    /// <param name="expectedFileBytes">The stable pre-delete file length.<br/></param>
    private static void ValidateEmptyScalar8VarIdentityTerminalAfterReopen(
        string path,
        DataKernelOptions options,
        long rootRouterOffset,
        ulong key,
        int identityLength,
        long expectedFileBytes)
    {
        RequireStableTerminalDeleteFileLength(path, expectedFileBytes, "SV8", "reopen");
        using LibraDexFileSession reopened = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions);
        Scalar8VarIdentityRoutePathTarget terminal = reopened.WalkScalar8VarIdentityRoutePathTarget(rootRouterOffset, key, maxRouterHops: 32);
        byte[] rootBytes = reopened.ReadTerminalIdentityRootBytes(terminal.Target.Offset);
        using Scalar8VarIdentityRangeReader reader = reopened.OpenScalar8VarIdentityRangeReader(rootRouterOffset, identityLength, key, key);
        if (reader.Count != 0 || TerminalIdentityRootLayout.ReadFirstShelfOffset(rootBytes) != 0 || TerminalIdentityRootLayout.ReadTailShelfOffset(rootBytes) != 0)
        {
            throw new InvalidDataException("SV8 terminal-delete reopen did not preserve the empty terminal root.");
        }
    }

    /// <summary>
    /// Reopens an emptied `VV` terminal route and validates zero rows, cleared endpoints, and stable physical bytes.<br/>
    /// </summary>
    /// <param name="path">The catalog path.<br/></param>
    /// <param name="options">The DataKernel options used to reopen it.<br/></param>
    /// <param name="rootRouterOffset">The `VV` root router offset.<br/></param>
    /// <param name="key">The exhausted variable key.<br/></param>
    /// <param name="expectedFileBytes">The stable pre-delete file length.<br/></param>
    private static void ValidateEmptyVarKeyVarIdentityTerminalAfterReopen(
        string path,
        DataKernelOptions options,
        long rootRouterOffset,
        byte[] key,
        long expectedFileBytes)
    {
        RequireStableTerminalDeleteFileLength(path, expectedFileBytes, "VV", "reopen");
        using LibraDexFileSession reopened = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions);
        VarKeyVarIdentityRoutePathTarget terminal = reopened.WalkVarKeyVarIdentityRoutePathTarget(rootRouterOffset, key, maxRouterHops: 128);
        byte[] rootBytes = reopened.ReadTerminalIdentityRootBytes(terminal.Target.Offset);
        using VarKeyVarIdentityRangeReader reader = reopened.OpenVarKeyVarIdentityRangeReader(rootRouterOffset, 1024, 1024, key, key, maxRouterHops: 128);
        if (reader.Count != 0 || TerminalIdentityRootLayout.ReadFirstShelfOffset(rootBytes) != 0 || TerminalIdentityRootLayout.ReadTailShelfOffset(rootBytes) != 0)
        {
            throw new InvalidDataException("VV terminal-delete reopen did not preserve the empty terminal root.");
        }
    }

    /// <summary>
    /// Builds every fixed scalar shelf shape through its public catalog facade, reopens the catalog, and requires exact proportional-storage accounting.<br/>
    /// Each shape receives the requested number of distinct tuples through one public batch so the proof exercises ordinary routed construction without mixing unrelated identity-group batch contracts.<br/>
    /// The release canary rejects unavailable whole-catalog reachability, tuple-count drift, component growth above 4 KiB per live tuple, whole-file growth above 128 MiB, or amplification above four times exact reachable topology.<br/>
    /// </summary>
    /// <param name="args">Harness arguments; `--path` selects the isolated catalog and `--items` selects tuples per shape.<br/></param>
    /// <returns>Zero when all six reopened fixed-shape components remain exactly accountable and proportional.<br/></returns>
    private static int RunFixedShapeFewThousandStorageSanity(string[] args)
    {
        string path = GetOption(
            args,
            "--path",
            Path.Combine(Path.GetTempPath(), "LibraDex", $"fixed-shape-few-thousand-{Guid.NewGuid():N}.lbdx"));
        int itemCount = GetIntOption(args, "--items", 4096);
        if (itemCount < 1000 || itemCount > 100_000)
            throw new ArgumentOutOfRangeException(nameof(args), itemCount, "Fixed-shape storage sanity items must be between 1,000 and 100,000 per shape.");

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        if (File.Exists(path))
            File.Delete(path);

        try
        {
            using (Catalog catalog = Catalog.Create(path))
            {
                using LibraDexIndex<long, long> ss88 = catalog.Indexes["fixed-storage"]["ss8-8"].Int64Keys<long>().Create();
                using LibraDexIndex<Int128, long> ss168 = catalog.Indexes["fixed-storage"]["ss16-8"].Int128Keys<long>().Create();
                using LibraDexIndex<long, Guid> ss816 = catalog.Indexes["fixed-storage"]["ss8-16"].Int64Keys<Guid>().Create();
                using LibraDexIndex<Int128, Guid> ss1616 = catalog.Indexes["fixed-storage"]["ss16-16"].Int128Keys<Guid>().Create();
                using LibraDexIndex<byte[], long> fs328 = catalog.Indexes["fixed-storage"]["fs32-8"].Blob
                    .Scalar<long>(LibraDexScalarWidth.Bytes32)
                    .Create();
                using LibraDexIndex<byte[], Guid> fs3216 = catalog.Indexes["fixed-storage"]["fs32-16"].Blob
                    .Scalar<Guid>(LibraDexScalarWidth.Bytes32)
                    .Create();

                LibraDexWriteIntent intent = new(
                    LibraDexWriteOrder.Sorted,
                    LibraDexWriteVolume.Thousands,
                    LibraDexWriteLocality.Broad,
                    LibraDexWritePriority.WriteSpeed);
                using (LibraDexBatch<long, long> batch = ss88.BeginBatch(intent))
                {
                    for (int i = 0; i < itemCount; i++)
                        ValidateGenericInsert(batch.Insert(i, 100_000L + i), $"fixed storage SS8-8 insert {i}");
                    RequireFixedStorageBatchCount(batch.Publish(), itemCount, "SS8-8");
                }

                using (LibraDexBatch<Int128, long> batch = ss168.BeginBatch(intent))
                {
                    for (int i = 0; i < itemCount; i++)
                        ValidateGenericInsert(batch.Insert((Int128)i << 64 | (uint)i, 200_000L + i), $"fixed storage SS16-8 insert {i}");
                    RequireFixedStorageBatchCount(batch.Publish(), itemCount, "SS16-8");
                }

                using (LibraDexBatch<long, Guid> batch = ss816.BeginBatch(intent))
                {
                    for (int i = 0; i < itemCount; i++)
                        ValidateGenericInsert(batch.Insert(i, CreateStableGuid(300_000 + i)), $"fixed storage SS8-16 insert {i}");
                    RequireFixedStorageBatchCount(batch.Publish(), itemCount, "SS8-16");
                }

                using (LibraDexBatch<Int128, Guid> batch = ss1616.BeginBatch(intent))
                {
                    for (int i = 0; i < itemCount; i++)
                        ValidateGenericInsert(batch.Insert((Int128)i << 64 | (uint)i, CreateStableGuid(400_000 + i)), $"fixed storage SS16-16 insert {i}");
                    RequireFixedStorageBatchCount(batch.Publish(), itemCount, "SS16-16");
                }

                using (LibraDexBatch<byte[], long> batch = fs328.BeginBatch(intent))
                {
                    for (int i = 0; i < itemCount; i++)
                        ValidateGenericInsert(batch.Insert(CreateFixedStorageKey(i, itemCount), 500_000L + i), $"fixed storage FS32-8 insert {i}");
                    RequireFixedStorageBatchCount(batch.Publish(), itemCount, "FS32-8");
                }

                using (LibraDexBatch<byte[], Guid> batch = fs3216.BeginBatch(intent))
                {
                    for (int i = 0; i < itemCount; i++)
                        ValidateGenericInsert(batch.Insert(CreateFixedStorageKey(i, itemCount), CreateStableGuid(600_000 + i)), $"fixed storage FS32-16 insert {i}");
                    RequireFixedStorageBatchCount(batch.Publish(), itemCount, "FS32-16");
                }
            }

            using Catalog reopened = Catalog.Open(path);
            LibraDexCatalogStorageAssessment storage = reopened.Maintenance.Assess().Storage;
            using LibraDexIndex<long, long> reopenedSS88 = reopened.Indexes["fixed-storage"]["ss8-8"].Int64Keys<long>().Open();
            using LibraDexIndex<Int128, long> reopenedSS168 = reopened.Indexes["fixed-storage"]["ss16-8"].Int128Keys<long>().Open();
            using LibraDexIndex<long, Guid> reopenedSS816 = reopened.Indexes["fixed-storage"]["ss8-16"].Int64Keys<Guid>().Open();
            using LibraDexIndex<Int128, Guid> reopenedSS1616 = reopened.Indexes["fixed-storage"]["ss16-16"].Int128Keys<Guid>().Open();
            using LibraDexIndex<byte[], long> reopenedFS328 = reopened.Indexes["fixed-storage"]["fs32-8"].Blob
                .Scalar<long>(LibraDexScalarWidth.Bytes32)
                .Open();
            using LibraDexIndex<byte[], Guid> reopenedFS3216 = reopened.Indexes["fixed-storage"]["fs32-16"].Blob
                .Scalar<Guid>(LibraDexScalarWidth.Bytes32)
                .Open();
            Dictionary<string, long> reopenedCounts = new(StringComparer.Ordinal)
            {
                ["ss8-8"] = ((IIndex)reopenedSS88).Count(),
                ["ss16-8"] = ((IIndex)reopenedSS168).Count(),
                ["ss8-16"] = ((IIndex)reopenedSS816).Count(),
                ["ss16-16"] = ((IIndex)reopenedSS1616).Count(),
                ["fs32-8"] = ((IIndex)reopenedFS328).Count(),
                ["fs32-16"] = ((IIndex)reopenedFS3216).Count()
            };
            if (!storage.IsComplete ||
                storage.UnsupportedIndexCount != 0 ||
                !storage.PhysicalBytes.HasValue ||
                !storage.UnreachableBytes.HasValue ||
                !storage.AmplificationRatio.HasValue ||
                storage.FixedTopologyComponents.Count != 6)
            {
                throw new InvalidDataException(
                    $"Fixed-shape storage assessment remained incomplete: complete={storage.IsComplete}, unsupported={storage.UnsupportedIndexCount}, components={storage.FixedTopologyComponents.Count}.");
            }

            const double maximumComponentBytesPerTuple = 4096d;
            foreach (LibraDexFixedTopologyStorageComponent component in storage.FixedTopologyComponents)
            {
                long reopenedCount = reopenedCounts[component.IndexName];
                if (component.TupleCount != itemCount ||
                    reopenedCount != itemCount ||
                    component.BytesPerLiveTuple > maximumComponentBytesPerTuple)
                {
                    throw new InvalidDataException(
                        $"Fixed-shape storage canary failed for {component.IndexName}: topologyTuples={component.TupleCount:n0}, reopenedTuples={reopenedCount:n0}, bytesPerTuple={component.BytesPerLiveTuple:F3}.");
                }
            }

            const long maximumFewThousandCatalogBytes = 128L * 1024 * 1024;
            const double maximumCatalogAmplification = 4d;
            if (storage.PhysicalBytes.Value > maximumFewThousandCatalogBytes ||
                storage.AmplificationRatio.Value > maximumCatalogAmplification)
            {
                throw new InvalidDataException(
                    $"Fixed-shape storage canary exceeded its catalog bound: physical={storage.PhysicalBytes.Value:n0}, reachable={storage.KnownReachableBytes:n0}, amplification={storage.AmplificationRatio.Value:F6}x.");
            }

            Console.WriteLine(
                $"fixed-shape-few-thousand-storage-sanity ok itemsPerShape={itemCount:n0} physical={storage.PhysicalBytes.Value:n0} reachable={storage.KnownReachableBytes:n0} unreachable={storage.UnreachableBytes.Value:n0} amplification={storage.AmplificationRatio.Value:F6}x");
            foreach (LibraDexFixedTopologyStorageComponent component in storage.FixedTopologyComponents.OrderBy(component => component.IndexName, StringComparer.Ordinal))
            {
                Console.WriteLine(
                    $"shape={component.IndexName} tuples={component.TupleCount:n0} reachable={component.ReachableBytes:n0} bytesPerTuple={component.BytesPerLiveTuple:F3} routers={component.RouterCount:n0} shelves={component.OrdinaryShelfCount:n0}");
            }

            ValidateFixedShapeAdversarialPatterns(path, GetOption(args, "--adversarial-shape", "all"));
            return 0;
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    /// <summary>
    /// Requires one public fixed-shape batch to publish exactly the tuples staged by its directed storage fixture.<br/>
    /// </summary>
    /// <param name="result">The public batch publication result.<br/></param>
    /// <param name="expectedCount">The exact staged tuple count.<br/></param>
    /// <param name="shapeName">The physical shape name used in diagnostic failures.<br/></param>
    private static void RequireFixedStorageBatchCount(
        LibraDexGenericBatchCommitResult result,
        int expectedCount,
        string shapeName)
    {
        if (result.InsertedCount != expectedCount)
        {
            throw new InvalidDataException(
                $"{shapeName} storage batch published {result.InsertedCount:n0} tuples instead of {expectedCount:n0}.");
        }
    }

    /// <summary>
    /// Creates one deterministic sortable 32-byte key whose leading stripe and trailing ordinal exercise routed fixed-key construction.<br/>
    /// </summary>
    /// <param name="ordinal">The non-negative logical tuple ordinal.<br/></param>
    /// <param name="itemCount">The complete fixture item count used to distribute keys monotonically across sixteen root stripes.<br/></param>
    /// <returns>A new fixed 32-byte key owned by the caller.<br/></returns>
    private static byte[] CreateFixedStorageKey(int ordinal, int itemCount)
    {
        byte[] key = new byte[32];
        key[0] = (byte)((long)ordinal * 16 / itemCount);
        BinaryPrimitives.WriteInt32BigEndian(key.AsSpan(28), ordinal);
        return key;
    }

    /// <summary>
    /// Exercises every public fixed scalar family under four deterministic hostile insertion orders with one concentrated duplicate-key half.<br/>
    /// Each pattern uses a separate catalog, validates exact key-then-identity cursor order live and reopened, and enforces complete proportional-storage accounting after reopen.<br/>
    /// </summary>
    /// <param name="basePath">The caller's isolated storage-gate path used only as a stem for disposable pattern catalogs.<br/></param>
    /// <param name="shapeFilter">`all` for the complete matrix or one physical shape label for an isolated diagnostic run.<br/></param>
    private static void ValidateFixedShapeAdversarialPatterns(string basePath, string shapeFilter)
    {
        const int itemCount = 4096;
        string[] supportedShapeFilters = ["all", "SS8-8", "SS16-8", "SS8-16", "SS16-16", "FS32-8", "FS32-16"];
        if (!supportedShapeFilters.Contains(shapeFilter, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentOutOfRangeException(nameof(shapeFilter), shapeFilter, "Unknown fixed adversarial shape filter.");
        string[] patterns = ["ascending", "descending", "alternating", "shuffled"];
        foreach (string pattern in patterns)
        {
            string patternPath = Path.Combine(
                Path.GetDirectoryName(basePath) ?? string.Empty,
                $"{Path.GetFileNameWithoutExtension(basePath)}-fixed-adversarial-{pattern}{Path.GetExtension(basePath)}");
            if (File.Exists(patternPath))
                File.Delete(patternPath);

            try
            {
                int[] order = CreateAdversarialOrdinalOrder(itemCount, pattern);
                using (Catalog catalog = Catalog.Create(patternPath))
                {
                    using LibraDexIndex<long, long> ss88 = catalog.Indexes["fixed-adversarial"]["ss8-8"].Int64Keys<long>().Create(IndexKeys.NonUnique);
                    using LibraDexIndex<Int128, long> ss168 = catalog.Indexes["fixed-adversarial"]["ss16-8"].Int128Keys<long>().Create(IndexKeys.NonUnique);
                    using LibraDexIndex<long, Guid> ss816 = catalog.Indexes["fixed-adversarial"]["ss8-16"].Int64Keys<Guid>().Create(IndexKeys.NonUnique);
                    using LibraDexIndex<Int128, Guid> ss1616 = catalog.Indexes["fixed-adversarial"]["ss16-16"].Int128Keys<Guid>().Create(IndexKeys.NonUnique);
                    using LibraDexIndex<byte[], long> fs328 = catalog.Indexes["fixed-adversarial"]["fs32-8"].Blob
                        .Scalar<long>(LibraDexScalarWidth.Bytes32)
                        .Create(IndexKeys.NonUnique);
                    using LibraDexIndex<byte[], Guid> fs3216 = catalog.Indexes["fixed-adversarial"]["fs32-16"].Blob
                        .Scalar<Guid>(LibraDexScalarWidth.Bytes32)
                        .Create(IndexKeys.NonUnique);
                    InsertFixedShapeAdversarialTuples(ss88, ss168, ss816, ss1616, fs328, fs3216, order, itemCount, pattern, shapeFilter);
                    ValidateFixedShapeAdversarialReaders(ss88, ss168, ss816, ss1616, fs328, fs3216, itemCount, $"{pattern} live", shapeFilter);
                }

                LibraDexCatalogStorageAssessment storage;
                {
                    using Catalog reopened = Catalog.Open(patternPath);
                    using LibraDexIndex<long, long> reopenedSS88 = reopened.Indexes["fixed-adversarial"]["ss8-8"].Int64Keys<long>().Open(IndexKeys.NonUnique);
                    using LibraDexIndex<Int128, long> reopenedSS168 = reopened.Indexes["fixed-adversarial"]["ss16-8"].Int128Keys<long>().Open(IndexKeys.NonUnique);
                    using LibraDexIndex<long, Guid> reopenedSS816 = reopened.Indexes["fixed-adversarial"]["ss8-16"].Int64Keys<Guid>().Open(IndexKeys.NonUnique);
                    using LibraDexIndex<Int128, Guid> reopenedSS1616 = reopened.Indexes["fixed-adversarial"]["ss16-16"].Int128Keys<Guid>().Open(IndexKeys.NonUnique);
                    using LibraDexIndex<byte[], long> reopenedFS328 = reopened.Indexes["fixed-adversarial"]["fs32-8"].Blob
                        .Scalar<long>(LibraDexScalarWidth.Bytes32)
                        .Open(IndexKeys.NonUnique);
                    using LibraDexIndex<byte[], Guid> reopenedFS3216 = reopened.Indexes["fixed-adversarial"]["fs32-16"].Blob
                        .Scalar<Guid>(LibraDexScalarWidth.Bytes32)
                        .Open(IndexKeys.NonUnique);
                    ValidateFixedShapeAdversarialReaders(
                        reopenedSS88,
                        reopenedSS168,
                        reopenedSS816,
                        reopenedSS1616,
                        reopenedFS328,
                        reopenedFS3216,
                        itemCount,
                        $"{pattern} reopened",
                        shapeFilter);

                    storage = reopened.Maintenance.Assess().Storage;
                }

                if (!storage.IsComplete ||
                    !storage.PhysicalBytes.HasValue ||
                    !storage.AmplificationRatio.HasValue ||
                    storage.UnsupportedIndexCount != 0 ||
                    storage.FixedTopologyComponents.Count != 6 ||
                    storage.FixedTopologyComponents.Any(component =>
                        component.TupleCount != (ShouldRunFixedAdversarialShape(shapeFilter, component.IndexName) ? itemCount : 0) ||
                        (component.TupleCount != 0 && component.BytesPerLiveTuple > 4096d)) ||
                    storage.PhysicalBytes.Value > 128L * 1024 * 1024 ||
                    storage.AmplificationRatio.Value > 4d)
                {
                    throw new InvalidDataException(
                        $"Fixed adversarial {pattern} storage gate failed: complete={storage.IsComplete}, unsupported={storage.UnsupportedIndexCount}, components={storage.FixedTopologyComponents.Count}, physical={storage.PhysicalBytes?.ToString("N0") ?? "n/a"}, amplification={storage.AmplificationRatio?.ToString("F6") ?? "n/a"}.");
                }

                int selectedShapeCount = supportedShapeFilters.Count(shape =>
                    !shape.Equals("all", StringComparison.OrdinalIgnoreCase) &&
                    ShouldRunFixedAdversarialShape(shapeFilter, shape));
                LibraDexCompactionResult compaction = Catalog.Compact(patternPath);
                if (compaction.LogicalIndexCount != 6 || compaction.TupleCount != (long)itemCount * selectedShapeCount)
                {
                    throw new InvalidDataException(
                        $"Fixed adversarial {pattern} compaction parity failed: indexes={compaction.LogicalIndexCount:n0}, tuples={compaction.TupleCount:n0}, expectedTuples={(long)itemCount * selectedShapeCount:n0}.");
                }

                Console.WriteLine(
                    $"fixed-shape-adversarial filter={shapeFilter} pattern={pattern} tuplesPerSelectedShape={itemCount:n0} physical={storage.PhysicalBytes.Value:n0} reachable={storage.KnownReachableBytes:n0} amplification={storage.AmplificationRatio.Value:F6}x compacted={compaction.CompactedBytes:n0}");
            }
            finally
            {
                if (File.Exists(patternPath))
                    File.Delete(patternPath);
            }
        }
    }

    /// <summary>
    /// Inserts one common logical tuple population into all six public fixed scalar shapes in the supplied adversarial order.<br/>
    /// The first half shares one exact key and receives distinct sortable identities; the second half supplies nearby broad keys so duplicate and neighboring routes coexist.<br/>
    /// </summary>
    /// <param name="ss88">The `SS8-8` target.<br/></param>
    /// <param name="ss168">The `SS16-8` target.<br/></param>
    /// <param name="ss816">The `SS8-16` target.<br/></param>
    /// <param name="ss1616">The `SS16-16` target.<br/></param>
    /// <param name="fs328">The `FS32-8` target.<br/></param>
    /// <param name="fs3216">The `FS32-16` target.<br/></param>
    /// <param name="order">The complete ordinal insertion permutation.<br/></param>
    /// <param name="itemCount">The exact tuple count per shape.<br/></param>
    /// <param name="pattern">The pattern label used in failures.<br/></param>
    /// <param name="shapeFilter">`all` or one shape label to execute in isolation.<br/></param>
    private static void InsertFixedShapeAdversarialTuples(
        LibraDexIndex<long, long> ss88,
        LibraDexIndex<Int128, long> ss168,
        LibraDexIndex<long, Guid> ss816,
        LibraDexIndex<Int128, Guid> ss1616,
        LibraDexIndex<byte[], long> fs328,
        LibraDexIndex<byte[], Guid> fs3216,
        IReadOnlyList<int> order,
        int itemCount,
        string pattern,
        string shapeFilter)
    {
        for (int i = 0; i < order.Count; i++)
        {
            int ordinal = order[i];
            long key8 = CreateFixedAdversarialScalar8Key(ordinal, itemCount);
            Int128 key16 = CreateFixedAdversarialScalar16Key(ordinal, itemCount);
            byte[] key32 = CreateFixedAdversarialBlobKey(ordinal, itemCount);
            long identity8 = 900_000L + ordinal;
            Guid identity16 = CreateStableGuid(1_000_000 + ordinal);
            if (ShouldRunFixedAdversarialShape(shapeFilter, "SS8-8"))
                ExecuteFixedAdversarialInsert(() => ValidateGenericInsert(ss88.Insert(key8, identity8), $"{pattern} SS8-8 insert {ordinal}"), pattern, "SS8-8", ordinal);
            if (ShouldRunFixedAdversarialShape(shapeFilter, "SS16-8"))
                ExecuteFixedAdversarialInsert(() => ValidateGenericInsert(ss168.Insert(key16, identity8), $"{pattern} SS16-8 insert {ordinal}"), pattern, "SS16-8", ordinal);
            if (ShouldRunFixedAdversarialShape(shapeFilter, "SS8-16"))
                ExecuteFixedAdversarialInsert(() => ValidateGenericInsert(ss816.Insert(key8, identity16), $"{pattern} SS8-16 insert {ordinal}"), pattern, "SS8-16", ordinal);
            if (ShouldRunFixedAdversarialShape(shapeFilter, "SS16-16"))
                ExecuteFixedAdversarialInsert(() => ValidateGenericInsert(ss1616.Insert(key16, identity16), $"{pattern} SS16-16 insert {ordinal}"), pattern, "SS16-16", ordinal);
            if (ShouldRunFixedAdversarialShape(shapeFilter, "FS32-8"))
                ExecuteFixedAdversarialInsert(() => ValidateGenericInsert(fs328.Insert(key32, identity8), $"{pattern} FS32-8 insert {ordinal}"), pattern, "FS32-8", ordinal);
            if (ShouldRunFixedAdversarialShape(shapeFilter, "FS32-16"))
                ExecuteFixedAdversarialInsert(() => ValidateGenericInsert(fs3216.Insert(key32, identity16), $"{pattern} FS32-16 insert {ordinal}"), pattern, "FS32-16", ordinal);
        }

        ValidateFixedTerminalExactDeleteAndRestore(ss168, ss816, ss1616, fs328, fs3216, itemCount, pattern, shapeFilter);
    }

    /// <summary>
    /// Deletes and restores one identity from every selected fixed-key duplicate terminal after the adversarial population is complete.<br/>
    /// Ordinal zero is guaranteed to belong to the concentrated duplicate half, so this proves public exact-delete routing against terminal storage without changing the final tuple population used by read, reopen, and storage gates.<br/>
    /// </summary>
    /// <param name="ss168">The `SS16-8` index participating when selected by the shape filter.<br/></param>
    /// <param name="ss816">The `SS8-16` index participating when selected by the shape filter.<br/></param>
    /// <param name="ss1616">The `SS16-16` index participating when selected by the shape filter.<br/></param>
    /// <param name="fs328">The `FS32-8` index participating when selected by the shape filter.<br/></param>
    /// <param name="fs3216">The `FS32-16` index participating when selected by the shape filter.<br/></param>
    /// <param name="itemCount">The complete adversarial population size.<br/></param>
    /// <param name="pattern">The insertion pattern used in failure diagnostics.<br/></param>
    /// <param name="shapeFilter">`all` or the one physical shape selected for isolation.<br/></param>
    private static void ValidateFixedTerminalExactDeleteAndRestore(
        LibraDexIndex<Int128, long> ss168,
        LibraDexIndex<long, Guid> ss816,
        LibraDexIndex<Int128, Guid> ss1616,
        LibraDexIndex<byte[], long> fs328,
        LibraDexIndex<byte[], Guid> fs3216,
        int itemCount,
        string pattern,
        string shapeFilter)
    {
        const int ordinal = 0;
        const long identity = 900_000L;
        Guid identity16 = CreateStableGuid(1_000_000 + ordinal);
        if (ShouldRunFixedAdversarialShape(shapeFilter, "SS16-8"))
        {
            Int128 key = CreateFixedAdversarialScalar16Key(ordinal, itemCount);
            ss168.Delete(key, identity);
            ValidateGenericInsert(ss168.Insert(key, identity), $"{pattern} SS16-8 terminal exact-delete restore");
        }

        if (ShouldRunFixedAdversarialShape(shapeFilter, "FS32-8"))
        {
            byte[] key = CreateFixedAdversarialBlobKey(ordinal, itemCount);
            fs328.Delete(key, identity);
            ValidateGenericInsert(fs328.Insert(key, identity), $"{pattern} FS32-8 terminal exact-delete restore");
        }

        if (ShouldRunFixedAdversarialShape(shapeFilter, "SS8-16"))
        {
            long key = CreateFixedAdversarialScalar8Key(ordinal, itemCount);
            ss816.Delete(key, identity16);
            ValidateGenericInsert(ss816.Insert(key, identity16), $"{pattern} SS8-16 terminal exact-delete restore");
        }

        if (ShouldRunFixedAdversarialShape(shapeFilter, "SS16-16"))
        {
            Int128 key = CreateFixedAdversarialScalar16Key(ordinal, itemCount);
            ss1616.Delete(key, identity16);
            ValidateGenericInsert(ss1616.Insert(key, identity16), $"{pattern} SS16-16 terminal exact-delete restore");
        }

        if (ShouldRunFixedAdversarialShape(shapeFilter, "FS32-16"))
        {
            byte[] key = CreateFixedAdversarialBlobKey(ordinal, itemCount);
            fs3216.Delete(key, identity16);
            ValidateGenericInsert(fs3216.Insert(key, identity16), $"{pattern} FS32-16 terminal exact-delete restore");
        }
    }

    /// <summary>
    /// Executes one directed fixed-family insertion and attaches the exact shape, pattern, and ordinal when a lower storage layer rejects it.<br/>
    /// Preserving the original exception as the inner cause keeps planner diagnostics and stack evidence available while making the public-readiness boundary immediately reproducible.<br/>
    /// </summary>
    /// <param name="insert">The complete public insertion and result-validation operation.<br/></param>
    /// <param name="pattern">The adversarial insertion pattern.<br/></param>
    /// <param name="shape">The physical shape label.<br/></param>
    /// <param name="ordinal">The logical tuple ordinal being inserted.<br/></param>
    private static void ExecuteFixedAdversarialInsert(Action insert, string pattern, string shape, int ordinal)
    {
        try
        {
            insert();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            throw new InvalidDataException(
                $"Fixed adversarial pattern={pattern} shape={shape} ordinal={ordinal:n0} failed during public insertion.",
                exception);
        }
    }

    /// <summary>
    /// Validates exact key-then-identity cursor order for all fixed scalar shapes against independently sorted logical ordinals.<br/>
    /// </summary>
    /// <param name="ss88">The `SS8-8` source.<br/></param>
    /// <param name="ss168">The `SS16-8` source.<br/></param>
    /// <param name="ss816">The `SS8-16` source.<br/></param>
    /// <param name="ss1616">The `SS16-16` source.<br/></param>
    /// <param name="fs328">The `FS32-8` source.<br/></param>
    /// <param name="fs3216">The `FS32-16` source.<br/></param>
    /// <param name="itemCount">The exact expected tuple count per shape.<br/></param>
    /// <param name="phase">The pattern/lifecycle label used in failures.<br/></param>
    /// <param name="shapeFilter">`all` or one shape label to validate in isolation.<br/></param>
    private static void ValidateFixedShapeAdversarialReaders(
        LibraDexIndex<long, long> ss88,
        LibraDexIndex<Int128, long> ss168,
        LibraDexIndex<long, Guid> ss816,
        LibraDexIndex<Int128, Guid> ss1616,
        LibraDexIndex<byte[], long> fs328,
        LibraDexIndex<byte[], Guid> fs3216,
        int itemCount,
        string phase,
        string shapeFilter)
    {
        if (ShouldRunFixedAdversarialShape(shapeFilter, "SS8-8"))
            RequireGenericOrderedTuples(ss88, itemCount, ordinal => CreateFixedAdversarialScalar8Key(ordinal, itemCount), ordinal => 900_000L + ordinal, static (left, right) => left.CompareTo(right), static (left, right) => left.CompareTo(right), $"{phase} SS8-8");
        if (ShouldRunFixedAdversarialShape(shapeFilter, "SS16-8"))
            RequireGenericOrderedTuples(ss168, itemCount, ordinal => CreateFixedAdversarialScalar16Key(ordinal, itemCount), ordinal => 900_000L + ordinal, static (left, right) => left.CompareTo(right), static (left, right) => left.CompareTo(right), $"{phase} SS16-8");
        if (ShouldRunFixedAdversarialShape(shapeFilter, "SS8-16"))
            RequireGenericOrderedTuples(ss816, itemCount, ordinal => CreateFixedAdversarialScalar8Key(ordinal, itemCount), ordinal => CreateStableGuid(1_000_000 + ordinal), static (left, right) => left.CompareTo(right), static (left, right) => left.CompareTo(right), $"{phase} SS8-16");
        if (ShouldRunFixedAdversarialShape(shapeFilter, "SS16-16"))
            RequireGenericOrderedTuples(ss1616, itemCount, ordinal => CreateFixedAdversarialScalar16Key(ordinal, itemCount), ordinal => CreateStableGuid(1_000_000 + ordinal), static (left, right) => left.CompareTo(right), static (left, right) => left.CompareTo(right), $"{phase} SS16-16");
        if (ShouldRunFixedAdversarialShape(shapeFilter, "FS32-8"))
            RequireGenericOrderedTuples(fs328, itemCount, ordinal => CreateFixedAdversarialBlobKey(ordinal, itemCount), ordinal => 900_000L + ordinal, static (left, right) => left.AsSpan().SequenceCompareTo(right), static (left, right) => left.CompareTo(right), $"{phase} FS32-8");
        if (ShouldRunFixedAdversarialShape(shapeFilter, "FS32-16"))
            RequireGenericOrderedTuples(fs3216, itemCount, ordinal => CreateFixedAdversarialBlobKey(ordinal, itemCount), ordinal => CreateStableGuid(1_000_000 + ordinal), static (left, right) => left.AsSpan().SequenceCompareTo(right), static (left, right) => left.CompareTo(right), $"{phase} FS32-16");
    }

    /// <summary>
    /// Determines whether one physical fixed shape participates in the current adversarial diagnostic.<br/>
    /// Catalog storage components use lowercase names while command filters use presentation labels, so comparison is deliberately case-insensitive.<br/>
    /// </summary>
    /// <param name="shapeFilter">`all` or one requested shape.<br/></param>
    /// <param name="shape">The candidate physical shape.<br/></param>
    /// <returns>True when the shape should be inserted, validated, and assigned the directed tuple count.<br/></returns>
    private static bool ShouldRunFixedAdversarialShape(string shapeFilter, string shape)
        => shapeFilter.Equals("all", StringComparison.OrdinalIgnoreCase) ||
           shapeFilter.Equals(shape, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Compares one public fixed-shape cursor with an independently sorted logical tuple population.<br/>
    /// The helper detects missing rows, unexpected rows, key inversions, and identity inversions within duplicate-key runs without relying on the index's own count or sorting implementation.<br/>
    /// </summary>
    /// <typeparam name="TKey">The public decoded key type.<br/></typeparam>
    /// <typeparam name="TIdentity">The public decoded identity type.<br/></typeparam>
    /// <param name="index">The live or reopened public index.<br/></param>
    /// <param name="itemCount">The exact expected tuple count.<br/></param>
    /// <param name="keyFactory">Creates the expected key for one logical ordinal.<br/></param>
    /// <param name="identityFactory">Creates the expected identity for one logical ordinal.<br/></param>
    /// <param name="compareKeys">Compares decoded keys in persisted sort order.<br/></param>
    /// <param name="compareIdentities">Compares decoded identities in persisted tie-break order.<br/></param>
    /// <param name="label">The shape/pattern/lifecycle label used in failures.<br/></param>
    private static void RequireGenericOrderedTuples<TKey, TIdentity>(
        LibraDexIndex<TKey, TIdentity> index,
        int itemCount,
        Func<int, TKey> keyFactory,
        Func<int, TIdentity> identityFactory,
        Comparison<TKey> compareKeys,
        Comparison<TIdentity> compareIdentities,
        string label)
    {
        int[] expectedOrdinals = Enumerable.Range(0, itemCount).ToArray();
        Array.Sort(expectedOrdinals, (left, right) =>
        {
            int keyComparison = compareKeys(keyFactory(left), keyFactory(right));
            return keyComparison != 0
                ? keyComparison
                : compareIdentities(identityFactory(left), identityFactory(right));
        });

        using LibraDexRangeReader<TKey, TIdentity> reader = index.OpenReader();
        for (int position = 0; position < expectedOrdinals.Length; position++)
        {
            if (!reader.TryReadNext(out TKey actualKey, out TIdentity actualIdentity))
                throw new InvalidDataException($"{label} ended at tuple {position:n0}/{itemCount:n0}.");
            int ordinal = expectedOrdinals[position];
            TKey expectedKey = keyFactory(ordinal);
            TIdentity expectedIdentity = identityFactory(ordinal);
            if (compareKeys(actualKey, expectedKey) != 0 || compareIdentities(actualIdentity, expectedIdentity) != 0)
            {
                throw new InvalidDataException(
                    $"{label} tuple {position:n0} did not match logical ordinal {ordinal:n0}.");
            }
        }

        if (reader.TryReadNext(out _, out _))
            throw new InvalidDataException($"{label} returned more than {itemCount:n0} tuples.");
    }

    /// <summary>
    /// Creates one deterministic insertion permutation for an adversarial fixed-family pattern.<br/>
    /// </summary>
    /// <param name="itemCount">The complete ordinal domain.<br/></param>
    /// <param name="pattern">One of `ascending`, `descending`, `alternating`, or `shuffled`.<br/></param>
    /// <returns>A complete permutation containing every ordinal exactly once.<br/></returns>
    private static int[] CreateAdversarialOrdinalOrder(int itemCount, string pattern)
    {
        int[] order = Enumerable.Range(0, itemCount).ToArray();
        if (pattern == "ascending")
            return order;
        if (pattern == "descending")
        {
            Array.Reverse(order);
            return order;
        }
        if (pattern == "alternating")
        {
            int low = 0;
            int high = itemCount - 1;
            for (int i = 0; i < order.Length; i++)
                order[i] = (i & 1) == 0 ? low++ : high--;
            return order;
        }
        if (pattern == "shuffled")
        {
            Random random = new(0x5EED_2026);
            for (int i = order.Length - 1; i > 0; i--)
            {
                int swapIndex = random.Next(i + 1);
                (order[i], order[swapIndex]) = (order[swapIndex], order[i]);
            }
            return order;
        }

        throw new ArgumentOutOfRangeException(nameof(pattern), pattern, "Unknown adversarial insertion pattern.");
    }

    /// <summary>
    /// Creates one scalar-8 adversarial key with a concentrated duplicate half and broad neighboring keys.<br/>
    /// </summary>
    /// <param name="ordinal">The logical tuple ordinal.<br/></param>
    /// <param name="itemCount">The complete fixture size.<br/></param>
    /// <returns>The sortable scalar-8 key.<br/></returns>
    private static long CreateFixedAdversarialScalar8Key(int ordinal, int itemCount)
    {
        if (ordinal < itemCount / 2)
            return 0x4400_0000_0000_005A;
        return checked(((long)ordinal * 16 / itemCount) << 56) | (uint)ordinal;
    }

    /// <summary>
    /// Creates one scalar-16 adversarial key with the same logical ordering as the scalar-8 fixture.<br/>
    /// </summary>
    /// <param name="ordinal">The logical tuple ordinal.<br/></param>
    /// <param name="itemCount">The complete fixture size.<br/></param>
    /// <returns>The sortable scalar-16 key.<br/></returns>
    private static Int128 CreateFixedAdversarialScalar16Key(int ordinal, int itemCount)
    {
        if (ordinal < itemCount / 2)
            return ((Int128)0x44 << 120) | 0x5A;
        return ((Int128)((long)ordinal * 16 / itemCount) << 120) | (uint)ordinal;
    }

    /// <summary>
    /// Creates one fixed-32 adversarial key with the same concentrated duplicate and neighboring-key distribution as the scalar fixtures.<br/>
    /// </summary>
    /// <param name="ordinal">The logical tuple ordinal.<br/></param>
    /// <param name="itemCount">The complete fixture size.<br/></param>
    /// <returns>A new 32-byte key owned by the caller.<br/></returns>
    private static byte[] CreateFixedAdversarialBlobKey(int ordinal, int itemCount)
    {
        byte[] key = new byte[32];
        if (ordinal < itemCount / 2)
        {
            key[0] = 0x44;
            key[^1] = 0x5A;
            return key;
        }

        key[0] = (byte)((long)ordinal * 16 / itemCount);
        BinaryPrimitives.WriteInt32BigEndian(key.AsSpan(28), ordinal);
        return key;
    }

    /// <summary>
    /// Builds all three programmable fixed-key shelf families through ordinary public BigInteger mutations and enforces proportional reopened storage.<br/>
    /// The fixture uses fixed-width 32-byte-magnitude contracts, distinct keys, scalar-8, scalar-16, and 24-byte raw identities, then compares public counts with exact topology counts.<br/>
    /// </summary>
    /// <param name="args">Harness arguments; `--path` selects the isolated catalog and `--items` selects tuples per shape.<br/></param>
    /// <returns>Zero when all three reopened FSN components remain exactly accountable and proportional.<br/></returns>
    private static int RunFixedNFewThousandStorageSanity(string[] args)
    {
        string path = GetOption(args, "--path", Path.Combine(Path.GetTempPath(), "LibraDex", $"fixedn-few-thousand-{Guid.NewGuid():N}.lbdx"));
        string aliasPressurePath = Path.Combine(
            Path.GetDirectoryName(path) ?? string.Empty,
            $"{Path.GetFileNameWithoutExtension(path)}-same-depth-alias{Path.GetExtension(path)}");
        int itemCount = GetIntOption(args, "--items", 4096);
        if (itemCount < 1000 || itemCount > 100_000)
            throw new ArgumentOutOfRangeException(nameof(args), itemCount, "Fixed-N storage sanity items must be between 1,000 and 100,000 per shape.");
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        if (File.Exists(path))
            File.Delete(path);

        try
        {
            using (Catalog catalog = Catalog.Create(path))
            {
                using LibraDexBigIntScalar8Index<long> fsn8 = catalog.Indexes["fixedn-storage"]["fsn-8"].BigIntKeys<long>(32).Create();
                using LibraDexBigIntScalar8Index<Guid> fsn16 = catalog.Indexes["fixedn-storage"]["fsn-16"].BigIntKeys<Guid>(32).Create();
                using LibraDexBigIntVarIdentityIndex fsnv = catalog.Indexes["fixedn-storage"]["fsn-v"].BigIntVarIdentityKeys(32, 64).Create();
                for (int i = 0; i < itemCount; i++)
                {
                    BigInteger key = new(i + 1);
                    ValidateGenericInsert(fsn8.Insert(key, 700_000L + i), $"FSN-8 storage insert {i}");
                    ValidateGenericInsert(fsn16.Insert(key, CreateStableGuid(800_000 + i)), $"FSN-16 storage insert {i}");
                    byte[] identity = new byte[24];
                    BinaryPrimitives.WriteInt32BigEndian(identity, i);
                    identity[23] = 0x5A;
                    ValidateGenericInsert(fsnv.Insert(key, identity), $"FSN-V storage insert {i}");
                }
            }

            using Catalog reopened = Catalog.Open(path);
            LibraDexCatalogStorageAssessment storage = reopened.Maintenance.Assess().Storage;
            using LibraDexBigIntScalar8Index<long> reopened8 = reopened.Indexes["fixedn-storage"]["fsn-8"].BigIntKeys<long>(32).Open();
            using LibraDexBigIntScalar8Index<Guid> reopened16 = reopened.Indexes["fixedn-storage"]["fsn-16"].BigIntKeys<Guid>(32).Open();
            using LibraDexBigIntVarIdentityIndex reopenedV = reopened.Indexes["fixedn-storage"]["fsn-v"].BigIntVarIdentityKeys(32, 64).Open();
            Dictionary<string, long> counts = new(StringComparer.Ordinal)
            {
                ["fsn-8"] = reopened8.Count(),
                ["fsn-16"] = reopened16.Count(),
                ["fsn-v"] = reopenedV.Count()
            };
            if (!storage.IsComplete || storage.UnsupportedIndexCount != 0 || !storage.PhysicalBytes.HasValue || !storage.UnreachableBytes.HasValue || !storage.AmplificationRatio.HasValue || storage.FixedTopologyComponents.Count != 3)
                throw new InvalidDataException($"Fixed-N storage assessment remained incomplete: complete={storage.IsComplete}, unsupported={storage.UnsupportedIndexCount}, components={storage.FixedTopologyComponents.Count}.");

            foreach (LibraDexFixedTopologyStorageComponent component in storage.FixedTopologyComponents)
            {
                long logicalCount = counts[component.IndexName];
                if (component.TupleCount != itemCount || logicalCount != itemCount || component.BytesPerLiveTuple > 4096d)
                    throw new InvalidDataException($"Fixed-N storage canary failed for {component.IndexName}: topologyTuples={component.TupleCount:n0}, reopenedTuples={logicalCount:n0}, bytesPerTuple={component.BytesPerLiveTuple:F3}.");
            }
            if (storage.PhysicalBytes.Value > 128L * 1024 * 1024 || storage.AmplificationRatio.Value > 4d)
                throw new InvalidDataException($"Fixed-N storage canary exceeded its catalog bound: physical={storage.PhysicalBytes.Value:n0}, reachable={storage.KnownReachableBytes:n0}, amplification={storage.AmplificationRatio.Value:F6}x.");

            Console.WriteLine($"fixedn-few-thousand-storage-sanity ok itemsPerShape={itemCount:n0} physical={storage.PhysicalBytes.Value:n0} reachable={storage.KnownReachableBytes:n0} unreachable={storage.UnreachableBytes.Value:n0} amplification={storage.AmplificationRatio.Value:F6}x");
            foreach (LibraDexFixedTopologyStorageComponent component in storage.FixedTopologyComponents.OrderBy(component => component.IndexName, StringComparer.Ordinal))
                Console.WriteLine($"shape={component.IndexName} tuples={component.TupleCount:n0} reachable={component.ReachableBytes:n0} bytesPerTuple={component.BytesPerLiveTuple:F3} routers={component.RouterCount:n0} shelves={component.OrdinaryShelfCount:n0}");
            ValidateFixedNSameDepthAliasPressure(aliasPressurePath);
            return 0;
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
            if (File.Exists(aliasPressurePath))
                File.Delete(aliasPressurePath);
        }
    }

    /// <summary>
    /// Forces `FSN-16` and fixed-key/variable-identity shelves through repeated same-depth splits beneath sparse parent aliases.<br/>
    /// The cyclic third key byte makes the first full shelf span all 256 parent prefixes, while the trailing ordinal keeps every tuple distinct and repeatedly refills both aliased descendants.<br/>
    /// This reproduces the ownership boundary that previously advanced to the next depth and rejected an otherwise valid shelf whose first difference remained at the parent depth.<br/>
    /// The validation checks full-range counts before close and after reopen so publication, route-cache invalidation, persistence, and alias-aware traversal are covered together.<br/>
    /// </summary>
    /// <param name="path">The isolated LibraDex file used only for the directed alias-pressure fixture.<br/></param>
    private static void ValidateFixedNSameDepthAliasPressure(string path)
    {
        const int itemCount = 4096;
        const int keySize = 35;
        if (File.Exists(path))
            File.Delete(path);

        DataKernelOptions options = new(
            AppendBufferSize: DefaultAppendBufferSize,
            ReservedPrefixBytes: 0,
            FlushToDiskOnCommit: false,
            MaxCommitGapCoalesceBytes: 512);
        SuperblockDeveloperMetadata metadata = new(
            DevIdentity: "LibraDexFixedNSameDepthAliasPressure",
            DevCustomText: "FSN-16 and FV sparse alias pressure",
            DevGuid: Guid.Parse("06a2e9bd-ae14-4a9a-ae07-7fbfd7594d0b"),
            DevDate1UtcTicks: 1,
            DevDate2UtcTicks: 2,
            DevNumber: 3);
        FixedNScalar16Profile fsn16Profile = FixedNScalar16Profile.Default64KiB(keySize);
        FixedNVarIdentityProfile fvProfile = FixedNVarIdentityProfile.Default64KiB(keySize, maxIdentityLength: 64);

        using (LibraDexFileSession session = LibraDexFileSession.Initialize(path, options, metadata, DataKernelTelemetryOptions.EnabledOptions))
        {
            (RouterSnapshot fsn16Root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "fsn16alias", 0));
            (RouterSnapshot fvRoot, _) = session.CreateRootRouterIndex(CreateHarnessSlot(1, "fvalias", 0));
            using FixedNScalar16Index fsn16 = new(session, new FixedNScalar16IndexHandle(fsn16Root.Offset, fsn16Profile, IsRouted: true), slotIndex: 0);
            using FixedNVarIdentityIndex fv = new(session, new FixedNVarIdentityIndexHandle(fvRoot.Offset, fvProfile, IsRouted: true), slotIndex: 1);
            for (int ordinal = 0; ordinal < itemCount; ordinal++)
            {
                byte[] key = CreateFixedNSameDepthAliasKey(keySize, ordinal);
                if (fsn16.Insert(key, CreateFixedNIdentity16(ordinal), allowDuplicateKeys: true).Result != FixedNScalarInsertResult.Inserted)
                    throw new InvalidDataException($"FSN-16 same-depth alias-pressure insert {ordinal:n0} failed.");

                byte[] identity = new byte[12];
                BinaryPrimitives.WriteInt32BigEndian(identity, ordinal);
                identity[^1] = 0xA5;
                if (fv.Insert(key, identity, allowDuplicateKeys: true).Result != FixedNVarIdentityInsertResult.Inserted)
                    throw new InvalidDataException($"FV same-depth alias-pressure insert {ordinal:n0} failed.");
            }

            ValidateFixedNSameDepthAliasCounts(fsn16, fv, keySize, itemCount, "live");
        }

        using (LibraDexFileSession reopened = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions))
        {
            ReadOnlySpan<IndexDirectorySlotSnapshot> slots = reopened.IndexDirectory.ActiveSlots;
            if (slots.Length != 2)
                throw new InvalidDataException($"Same-depth alias-pressure reopen found {slots.Length} active slots instead of two.");
            using FixedNScalar16Index fsn16 = new(reopened, new FixedNScalar16IndexHandle(slots[0].RootRouterOffset, fsn16Profile, IsRouted: true), slotIndex: 0);
            using FixedNVarIdentityIndex fv = new(reopened, new FixedNVarIdentityIndexHandle(slots[1].RootRouterOffset, fvProfile, IsRouted: true), slotIndex: 1);
            ValidateFixedNSameDepthAliasCounts(fsn16, fv, keySize, itemCount, "reopened");
        }

        Console.WriteLine($"fixedn-same-depth-alias-pressure ok tuplesPerShape={itemCount:n0}");
    }

    /// <summary>
    /// Creates one fixed-width key for the directed same-depth alias-pressure fixture.<br/>
    /// The first two bytes form a common stem, the third byte cycles through a coprime permutation of the complete byte domain, and the trailing ordinal guarantees uniqueness.<br/>
    /// </summary>
    /// <param name="keySize">The fixed encoded key width.<br/></param>
    /// <param name="ordinal">The zero-based tuple ordinal.<br/></param>
    /// <returns>A new sortable key owned by the caller.<br/></returns>
    private static byte[] CreateFixedNSameDepthAliasKey(int keySize, int ordinal)
    {
        byte[] key = new byte[keySize];
        key[0] = 0x31;
        key[1] = 0x73;
        key[2] = (byte)((ordinal * 73) & byte.MaxValue);
        BinaryPrimitives.WriteInt32BigEndian(key.AsSpan(keySize - sizeof(int)), ordinal);
        return key;
    }

    /// <summary>
    /// Confirms exact full-range tuple counts for both same-depth alias-pressure shapes.<br/>
    /// Count-only and materializing reads must agree so a route alias cannot hide, duplicate, or orphan a shelf.<br/>
    /// </summary>
    /// <param name="fsn16">The routed fixed-key/scalar-16 index to validate.<br/></param>
    /// <param name="fv">The routed fixed-key/variable-identity index to validate.<br/></param>
    /// <param name="keySize">The fixed encoded key width.<br/></param>
    /// <param name="expectedCount">The exact tuple count expected from each shape.<br/></param>
    /// <param name="phase">The lifecycle phase included in failure diagnostics.<br/></param>
    private static void ValidateFixedNSameDepthAliasCounts(
        FixedNScalar16Index fsn16,
        FixedNVarIdentityIndex fv,
        int keySize,
        int expectedCount,
        string phase)
    {
        byte[] lower = new byte[keySize];
        byte[] upper = new byte[keySize];
        upper.AsSpan().Fill(byte.MaxValue);
        long fsn16Count = fsn16.CountIdentityRange(lower, upper);
        int fsn16ReadCount = fsn16.ReadIdentityRange(lower, upper).Length / FixedNScalar16Layout.IdentitySize;
        long fvCount = fv.CountIdentityRange(lower, upper);
        int fvReadCount = fv.ReadIdentityRange(lower, upper).Count;
        if (fsn16Count != expectedCount || fsn16ReadCount != expectedCount || fvCount != expectedCount || fvReadCount != expectedCount)
        {
            throw new InvalidDataException(
                $"Same-depth alias-pressure {phase} parity failed: FSN-16 count={fsn16Count:n0}, read={fsn16ReadCount:n0}; FV count={fvCount:n0}, read={fvReadCount:n0}; expected={expectedCount:n0}.");
        }
    }

    /// <summary>
    /// Builds all four non-`VS8` variable shelf families through their public raw facades and enforces proportional reopened storage.<br/>
    /// Each fixture combines broad distinct keys with one concentrated duplicate-key half so ordinary routers, overflow chains, and the `VV`/`SV8` terminal-root paths are included in the release gate.<br/>
    /// The raw facades currently own separate files, so each file is assessed independently against exact topology plus superblock and directory reachability.<br/>
    /// </summary>
    /// <param name="args">Harness arguments; `--path` supplies the common isolated file-name stem and `--items` selects tuples per shape.<br/></param>
    /// <returns>Zero when `VS16`, `VV`, `SV8`, and `SV16` remain exactly accountable and proportional after reopen.<br/></returns>
    private static int RunVariableShapeFewThousandStorageSanity(string[] args)
    {
        string basePath = GetOption(
            args,
            "--path",
            Path.Combine(Path.GetTempPath(), "LibraDex", $"variable-shape-few-thousand-{Guid.NewGuid():N}.lbdx"));
        int itemCount = GetIntOption(args, "--items", 4096);
        if (itemCount < 1000 || itemCount > 100_000)
            throw new ArgumentOutOfRangeException(nameof(args), itemCount, "Variable-shape storage sanity items must be between 1,000 and 100,000 per shape.");

        string? directory = Path.GetDirectoryName(basePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        string baseName = Path.GetFileNameWithoutExtension(basePath);
        string extension = Path.GetExtension(basePath);
        string parent = Path.GetDirectoryName(basePath) ?? string.Empty;
        string vs16Path = Path.Combine(parent, $"{baseName}-vs16{extension}");
        string vvPath = Path.Combine(parent, $"{baseName}-vv{extension}");
        string sv8Path = Path.Combine(parent, $"{baseName}-sv8{extension}");
        string sv16Path = Path.Combine(parent, $"{baseName}-sv16{extension}");
        string[] paths = [vs16Path, vvPath, sv8Path, sv16Path];
        foreach (string path in paths)
        {
            if (File.Exists(path))
                File.Delete(path);
        }

        const int maxKeyLength = 32;
        const int maxIdentityLength = 64;
        DataKernelOptions options = CreateDesignPerfOptions();
        try
        {
            using (VarKeyScalar16Index index = Indexes.VS16.Create(path: vs16Path, maxKeyLength: maxKeyLength, name: "storage-vs16", options: options))
            {
                for (int i = 0; i < itemCount; i++)
                {
                    byte[] key = CreateVariableStorageKey(i, itemCount, duplicateHalf: true);
                    VarKeyScalar16InsertOutcome result = index.Insert(key, 0, checked((ulong)i + 1), allowDuplicateKeys: true);
                    if (!result.Inserted)
                        throw new InvalidDataException($"VS16 storage insert {i} failed: {result}.");
                }
            }

            using (VarKeyVarIdentityIndex index = Indexes.VV.Create(path: vvPath, maxKeyLength: maxKeyLength, maxIdentityLength: maxIdentityLength, name: "storage-vv", options: options))
            {
                for (int i = 0; i < itemCount; i++)
                {
                    VarKeyVarIdentityIndexInsertResult result = index.Insert(
                        CreateVariableStorageMixedKey(i, itemCount),
                        CreateVariableStorageIdentity(i),
                        allowDuplicateKeys: true);
                    if (!result.Inserted)
                        throw new InvalidDataException($"VV storage insert {i} failed: {result}.");
                }
            }

            using (Scalar8VarIdentityIndex index = Indexes.SV8.Create(path: sv8Path, maxIdentityLength: maxIdentityLength, name: "storage-sv8", options: options))
            {
                for (int i = 0; i < itemCount; i++)
                {
                    ulong key = i < itemCount / 2
                        ? 0x4400_0000_0000_0000UL
                        : ((ulong)((long)i * 16 / itemCount) << 56) | checked((uint)i);
                    Scalar8VarIdentityInsertOutcome result = index.Insert(key, CreateVariableStorageIdentity(i), allowDuplicateKeys: true);
                    if (!result.Inserted)
                        throw new InvalidDataException($"SV8 storage insert {i} failed: {result}.");
                }
            }

            using (Scalar16VarIdentityIndex index = Indexes.SV16.Create(path: sv16Path, maxIdentityLength: maxIdentityLength, name: "storage-sv16", options: options))
            {
                for (int i = 0; i < itemCount; i++)
                {
                    ulong keyHigh = i < itemCount / 2
                        ? 0x4400_0000_0000_0000UL
                        : (ulong)((long)i * 16 / itemCount) << 56;
                    ulong keyLow = i < itemCount / 2 ? 0 : checked((uint)i);
                    Scalar16VarIdentityInsertOutcome result = index.Insert(keyHigh, keyLow, CreateVariableStorageIdentity(i), allowDuplicateKeys: true);
                    if (!result.Inserted)
                        throw new InvalidDataException($"SV16 storage insert {i} failed: {result}.");
                }
            }

            VariableStorageCanaryRow[] rows =
            [
                AssessVariableStorageCanary(vs16Path, "VS16", itemCount, options, session => session.AssessVarKeyScalar16Topology(0, maxKeyLength, 0, 0)),
                AssessVariableStorageCanary(vvPath, "VV", itemCount, options, session => session.AssessVarKeyVarIdentityTopology(0, maxKeyLength, maxIdentityLength)),
                AssessVariableStorageCanary(sv8Path, "SV8", itemCount, options, session => session.AssessScalar8VarIdentityTopology(0, maxIdentityLength)),
                AssessVariableStorageCanary(sv16Path, "SV16", itemCount, options, session => session.AssessScalar16VarIdentityTopology(0, maxIdentityLength))
            ];

            ValidateVarKeyScalar16TerminalConsumers(vs16Path, itemCount, maxKeyLength, options);

            long totalPhysical = rows.Sum(row => row.PhysicalBytes);
            long totalReachable = rows.Sum(row => row.ReachableBytes);
            long totalUnreachable = rows.Sum(row => row.UnreachableBytes);
            Console.WriteLine($"variable-shape-few-thousand-storage-sanity ok itemsPerShape={itemCount:n0} physical={totalPhysical:n0} reachable={totalReachable:n0} unreachable={totalUnreachable:n0} amplification={(double)totalPhysical / totalReachable:F6}x");
            foreach (VariableStorageCanaryRow row in rows.OrderBy(row => row.Shape, StringComparer.Ordinal))
            {
                Console.WriteLine(
                    $"shape={row.Shape} tuples={row.TupleCount:n0} physical={row.PhysicalBytes:n0} reachable={row.ReachableBytes:n0} bytesPerTuple={row.BytesPerTuple:F3} amplification={row.Amplification:F6}x routers={row.RouterCount:n0} shelves={row.OrdinaryShelfCount:n0} overflow={row.OverflowShelfCount:n0} terminalRoots={row.TerminalRootCount:n0} terminalShelves={row.TerminalShelfCount:n0}");
            }
            return 0;
        }
        finally
        {
            foreach (string path in paths)
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }
    }

    /// <summary>
    /// Proves the `VS16` exact-key terminal representation through its public cursor/read/count surfaces and topology-aware delete fallbacks.<br/>
    /// The fixture reopens the generated file, validates every hot-key identity in order, deletes one middle identity locally, clears the remaining key through one range delete, and reopens again to prove durable emptiness.<br/>
    /// This runs after exact storage assessment so destructive validation cannot hide first-build reachability or amplification evidence.<br/>
    /// </summary>
    /// <param name="path">The isolated `VS16` canary file path.<br/></param>
    /// <param name="itemCount">The complete variable-shape fixture tuple count.<br/></param>
    /// <param name="maxKeyLength">The raw maximum key length used to create and reopen the index.<br/></param>
    /// <param name="options">The DataKernel options used by the fixture.<br/></param>
    private static void ValidateVarKeyScalar16TerminalConsumers(
        string path,
        int itemCount,
        int maxKeyLength,
        DataKernelOptions options)
    {
        int expectedTerminalCount = itemCount / 2;
        byte[] terminalKey = CreateVariableStorageKey(0, itemCount, duplicateHalf: true);
        using (VarKeyScalar16Index index = Indexes.VS16.Open(path, maxKeyLength: maxKeyLength, options: options))
        {
            LibraDexMaintenanceWalkResult maintenance = index.Session.OptimizeVarKeyScalar16Topology(
                index.RootRouterOffset,
                index.Handle.MaxKeyLength,
                requestedRouteCount: 16,
                maxWorkItems: null);
            if (!maintenance.Completed || maintenance.ConsideredCount != itemCount)
                throw new InvalidDataException($"VS16 terminal maintenance considered {maintenance.ConsideredCount:n0}/{itemCount:n0} tuples and reported {maintenance.IncompleteReasons}.");

            VariableTopologyAssessment optimizedTopology = index.Session.AssessVarKeyScalar16Topology(
                index.SlotIndex,
                index.Handle.MaxKeyLength,
                nullKeyRouteOffset: 0,
                emptyKeyRouteOffset: 0);
            long optimizedPhysicalBytes = new FileInfo(path).Length;
            long optimizedReachableBytes = checked(
                (long)SuperblockLayout.Size +
                index.Session.Superblock.IndexDirectoryLength +
                optimizedTopology.ReachableBytes);
            bool requiresTerminalRoot = expectedTerminalCount >= 4096;
            if (optimizedTopology.TupleCount != itemCount ||
                optimizedTopology.TerminalRootCount > 1 ||
                (requiresTerminalRoot && optimizedTopology.TerminalRootCount != 1) ||
                optimizedPhysicalBytes < optimizedReachableBytes ||
                optimizedPhysicalBytes > optimizedReachableBytes * 4)
            {
                throw new InvalidDataException(
                    $"VS16 optimized terminal topology is not exact and proportional: tuples={optimizedTopology.TupleCount:n0}/{itemCount:n0}, " +
                    $"terminalRoots={optimizedTopology.TerminalRootCount:n0}, physical={optimizedPhysicalBytes:n0}, reachable={optimizedReachableBytes:n0}.");
            }

            long count = index.CountIdentityRange(terminalKey, terminalKey);
            if (count != expectedTerminalCount)
                throw new InvalidDataException($"VS16 terminal range count returned {count:n0} instead of {expectedTerminalCount:n0}.");

            using (VarKeyScalar16Batch aborted = index.BeginBatch())
            {
                VarKeyScalar16InsertOutcome staged = aborted.Insert(
                    terminalKey,
                    encodedIdentityHigh: ulong.MaxValue,
                    encodedIdentityLow: ulong.MaxValue,
                    allowDuplicateKeys: true);
                if (!staged.Inserted)
                    throw new InvalidDataException($"VS16 terminal abort probe could not stage its unique identity: {staged}.");
                _ = aborted.Abort();
            }
            if (index.CountIdentityRange(terminalKey, terminalKey) != expectedTerminalCount)
                throw new InvalidDataException("VS16 terminal abort probe changed the visible exact-key count.");

            ulong[] identityHighs = new ulong[expectedTerminalCount];
            ulong[] identityLows = new ulong[expectedTerminalCount];
            int copied = index.ReadRange(terminalKey, terminalKey, identityHighs, identityLows);
            if (copied != expectedTerminalCount)
                throw new InvalidDataException($"VS16 terminal direct range read copied {copied:n0} identities instead of {expectedTerminalCount:n0}.");
            for (int i = 0; i < copied; i++)
            {
                if (identityHighs[i] != 0 || identityLows[i] != checked((ulong)i + 1))
                    throw new InvalidDataException($"VS16 terminal direct range identity {i:n0} is out of order.");
            }

            using (VarKeyScalar16RangeReader reader = index.OpenRangeReader(terminalKey, terminalKey))
            {
                if (reader.Count != expectedTerminalCount)
                    throw new InvalidDataException($"VS16 terminal cursor count returned {reader.Count:n0} instead of {expectedTerminalCount:n0}.");
                int ordinal = 0;
                while (reader.MoveNext())
                {
                    if (!reader.CurrentKey.SequenceEqual(terminalKey))
                        throw new InvalidDataException($"VS16 terminal cursor key {ordinal:n0} does not match the root-owned key.");
                    reader.ReadCurrentIdentity(out ulong identityHigh, out ulong identityLow);
                    if (identityHigh != 0 || identityLow != checked((ulong)ordinal + 1))
                        throw new InvalidDataException($"VS16 terminal cursor identity {ordinal:n0} is out of order.");
                    ordinal++;
                }
                if (ordinal != expectedTerminalCount)
                    throw new InvalidDataException($"VS16 terminal cursor visited {ordinal:n0} identities instead of {expectedTerminalCount:n0}.");
            }

            int deleteOrdinal = expectedTerminalCount / 2;
            if (!index.DeleteExactTuple(terminalKey, 0, checked((ulong)deleteOrdinal + 1)))
                throw new InvalidDataException("VS16 terminal exact delete did not remove the selected middle identity.");
            if (index.CountIdentityRange(terminalKey, terminalKey) != expectedTerminalCount - 1)
                throw new InvalidDataException("VS16 terminal exact delete did not preserve the expected survivor count.");
            long deleted = index.DeleteRange(terminalKey, terminalKey);
            if (deleted != expectedTerminalCount - 1)
                throw new InvalidDataException($"VS16 terminal range delete removed {deleted:n0} identities instead of {expectedTerminalCount - 1:n0}.");
        }

        using VarKeyScalar16Index reopened = Indexes.VS16.Open(path, maxKeyLength: maxKeyLength, options: options);
        if (reopened.CountIdentityRange(terminalKey, terminalKey) != 0)
            throw new InvalidDataException("VS16 terminal route was not durably empty after reopen.");
    }

    /// <summary>
    /// Reopens one raw variable-shape file, executes its exact topology walker, and enforces the public proportional-storage release bounds.<br/>
    /// </summary>
    /// <param name="path">The closed raw shape file.<br/></param>
    /// <param name="shape">The diagnostic physical shape name.<br/></param>
    /// <param name="expectedTupleCount">The exact number of inserted tuples.<br/></param>
    /// <param name="assess">The shape-specific topology walker.<br/></param>
    /// <returns>A disconnected canary row for aggregate reporting.<br/></returns>
    private static VariableStorageCanaryRow AssessVariableStorageCanary(
        string path,
        string shape,
        int expectedTupleCount,
        DataKernelOptions options,
        Func<LibraDexFileSession, VariableTopologyAssessment> assess)
    {
        using LibraDexFileSession session = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions);
        VariableTopologyAssessment topology = assess(session);
        long physicalBytes = new FileInfo(path).Length;
        long reachableBytes = checked((long)SuperblockLayout.Size + session.Superblock.IndexDirectoryLength + topology.ReachableBytes);
        if (topology.TupleCount != expectedTupleCount || physicalBytes < reachableBytes)
        {
            throw new InvalidDataException(
                $"{shape} storage topology mismatch: topologyTuples={topology.TupleCount:n0}, expectedTuples={expectedTupleCount:n0}, physical={physicalBytes:n0}, reachable={reachableBytes:n0}.");
        }

        double bytesPerTuple = (double)topology.ReachableBytes / topology.TupleCount;
        double amplification = (double)physicalBytes / reachableBytes;
        if (bytesPerTuple > 4096d || physicalBytes > 128L * 1024 * 1024 || amplification > 4d)
        {
            throw new InvalidDataException(
                $"{shape} storage canary exceeded its proportional bound: physical={physicalBytes:n0}, reachable={reachableBytes:n0}, bytesPerTuple={bytesPerTuple:F3}, amplification={amplification:F6}x.");
        }

        return new VariableStorageCanaryRow(
            shape,
            topology.TupleCount,
            physicalBytes,
            reachableBytes,
            physicalBytes - reachableBytes,
            bytesPerTuple,
            amplification,
            topology.RouterCount,
            topology.OrdinaryShelfCount,
            topology.OverflowShelfCount,
            topology.TerminalRootCount,
            topology.TerminalShelfCount);
    }

    /// <summary>
    /// Creates a deterministic variable key with a concentrated duplicate half and broadly striped remaining keys.<br/>
    /// </summary>
    /// <param name="ordinal">The zero-based tuple ordinal.<br/></param>
    /// <param name="itemCount">The complete fixture item count.<br/></param>
    /// <param name="duplicateHalf">Whether the first half should share one exact key.<br/></param>
    /// <returns>A new 16-byte sortable key.<br/></returns>
    private static byte[] CreateVariableStorageKey(int ordinal, int itemCount, bool duplicateHalf)
    {
        byte[] key = new byte[16];
        if (duplicateHalf && ordinal < itemCount / 2)
        {
            key[0] = 0x44;
            key[15] = 0x5A;
            return key;
        }

        key[0] = (byte)((long)ordinal * 16 / itemCount);
        BinaryPrimitives.WriteInt32BigEndian(key.AsSpan(12), ordinal);
        return key;
    }

    /// <summary>
    /// Creates an interleaved `VV` key stream whose even ordinals share one hot key while odd ordinals remain broadly striped.<br/>
    /// Interleaving forces duplicate-key terminal extraction from a mixed shelf instead of the simpler full same-key conversion path.<br/>
    /// </summary>
    /// <param name="ordinal">The zero-based tuple ordinal.<br/></param>
    /// <param name="itemCount">The complete fixture item count.<br/></param>
    /// <returns>A new 16-byte sortable key.<br/></returns>
    private static byte[] CreateVariableStorageMixedKey(int ordinal, int itemCount)
    {
        if ((ordinal & 1) == 0)
        {
            byte[] duplicate = new byte[16];
            duplicate[0] = 0x44;
            duplicate[15] = 0x5A;
            return duplicate;
        }

        byte[] key = new byte[16];
        key[0] = (byte)((long)ordinal * 16 / itemCount);
        BinaryPrimitives.WriteInt32BigEndian(key.AsSpan(12), ordinal);
        return key;
    }

    /// <summary>
    /// Creates one deterministic 56-byte variable identity whose final bytes preserve ordinal ordering.<br/>
    /// </summary>
    /// <param name="ordinal">The zero-based tuple ordinal.<br/></param>
    /// <returns>A new identity payload.<br/></returns>
    private static byte[] CreateVariableStorageIdentity(int ordinal)
    {
        byte[] identity = new byte[56];
        identity[0] = 0xA5;
        BinaryPrimitives.WriteInt32BigEndian(identity.AsSpan(52), ordinal);
        return identity;
    }

    private readonly record struct VariableStorageCanaryRow(
        string Shape,
        long TupleCount,
        long PhysicalBytes,
        long ReachableBytes,
        long UnreachableBytes,
        double BytesPerTuple,
        double Amplification,
        int RouterCount,
        int OrdinaryShelfCount,
        int OverflowShelfCount,
        int TerminalRootCount,
        int TerminalShelfCount);

    private readonly record struct TerminalVarIdentityShelfSnapshot(long Offset, long NextOffset, byte[][] Identities);
}
