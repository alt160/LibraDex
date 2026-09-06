using LibraDex;

internal static partial class RawHarness
{
    /// <summary>
    /// Proves durable allocator reuse across both one-index and complete-index-set catalog deactivation boundaries.<br/>
    /// The fixture closes and reopens after retirement before recreating equivalent fixed and variable-key shapes, then bounds EOF growth to metadata-only append fallback while verifying retained and rebuilt query semantics.<br/>
    /// </summary>
    /// <param name="args">Optional <c>--path</c> and <c>--items</c> harness arguments.<br/></param>
    /// <returns>Zero when drop publication, reopen reconstruction, extent reuse, and final query validation all succeed.<br/></returns>
    private static int RunCatalogAllocatorDropReuseSanity(string[] args)
    {
        string path = GetOption(
            args,
            "--path",
            Path.Combine(Path.GetTempPath(), "LibraDex", $"catalog-allocator-drop-reuse-{Guid.NewGuid():N}.lbdx"));
        int itemCount = GetIntOption(args, "--items", 6_000);
        if (itemCount < 1_000)
            throw new ArgumentOutOfRangeException(nameof(args), itemCount, "Allocator drop/reuse validation requires at least 1,000 items.");

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        if (File.Exists(path))
            File.Delete(path);

        try
        {
            long retiredReusableBytes = 0;
            long retiredUnreachableBytes = 0;
            using (Catalog catalog = Catalog.Create(path, CatalogOptions.UInt64Identities))
            {
                using LibraDexIndex<int, ulong> age = catalog.Indexes["people"]["age"].Int32Keys<ulong>().Create();
                using LibraDexStringScalar8Index name = catalog.Indexes["people"]["name"].String.Create(StringKeys.ExactAndFolded);
                using LibraDexIndex<long, ulong> scratch = catalog.Indexes["scratch"]["value"].Int64Keys<ulong>().Create();
                using LibraDexIndex<int, ulong> retained = catalog.Indexes["orders"]["number"].Int32Keys<ulong>().Create();
                for (int itemIndex = 0; itemIndex < itemCount; itemIndex++)
                {
                    ulong identity = checked((ulong)itemIndex + 1UL);
                    ValidateGenericInsert(age.Insert(itemIndex % 100, identity), $"allocator reuse age insert {itemIndex}");
                    ValidateGenericInsert(name.Insert(CreateAllocatorReuseName(itemIndex), identity), $"allocator reuse name insert {itemIndex}");
                    ValidateGenericInsert(scratch.Insert(itemIndex, identity), $"allocator reuse scratch insert {itemIndex}");
                }

                ValidateGenericInsert(retained.Insert(100, 9_000_001UL), "allocator reuse retained insert");
            }

            long initialPopulatedLength = new FileInfo(path).Length;
            using (Catalog catalog = Catalog.Open(path, CatalogOptions.UInt64Identities))
            {
                using LibraDexIndex<long, ulong> scratch = catalog.Indexes["scratch"]["value"].Int64Keys<ulong>().Open();
                LibraDexRangeReader<long, ulong> retainedReader = scratch.OpenRangeReader(long.MinValue, long.MaxValue);
                if (!retainedReader.MoveNext())
                    throw new InvalidDataException("The coherent-reader retirement fixture could not position on its first source row.");

                Task<bool> individualDrop = Task.Run(() => catalog.Indexes.Drop("scratch", "value"));
                if (individualDrop.Wait(TimeSpan.FromMilliseconds(150)))
                {
                    retainedReader.Dispose();
                    throw new InvalidDataException("Individual catalog retirement completed while a coherent file reader still retained the old generation.");
                }

                retainedReader.Dispose();
                if (!individualDrop.Wait(TimeSpan.FromSeconds(10)) || !individualDrop.GetAwaiter().GetResult())
                    throw new InvalidDataException("Individual catalog retirement did not complete after its coherent reader was released.");
                if (!catalog.Indexes.DropIndexSet("people"))
                    throw new InvalidDataException("Catalog index-set drop did not find the populated people set.");

                LibraDexCatalogStorageAssessment retiredStorage = catalog.Maintenance.Assess().Storage;
                if (!retiredStorage.IsComplete || !retiredStorage.UnreachableBytes.HasValue)
                    throw new InvalidDataException("Catalog retirement storage accounting was incomplete for supported fixture shapes.");
                if (retiredStorage.ReusableBytes <= 0)
                    throw new InvalidDataException("Catalog retirement did not expose allocator-owned payload as reusable capacity.");
                retiredReusableBytes = retiredStorage.ReusableBytes;
                retiredUnreachableBytes = retiredStorage.UnreachableBytes.Value;
            }

            long retiredLength = new FileInfo(path).Length;
            if (retiredLength != initialPopulatedLength)
            {
                throw new InvalidDataException(
                    $"Catalog retirement changed EOF from {initialPopulatedLength:N0} to {retiredLength:N0} bytes instead of rewriting only fixed allocation metadata.");
            }

            using (Catalog catalog = Catalog.Open(path, CatalogOptions.UInt64Identities))
            {
                if (catalog.Indexes.TryGetInfo("scratch", "value", out _) ||
                    catalog.Indexes.TryGetInfo("people", "age", out _) ||
                    catalog.Indexes.TryGetInfo("people", "name", out _))
                {
                    throw new InvalidDataException("Reopen restored one or more retired catalog definitions.");
                }

                using LibraDexIndex<int, ulong> age = catalog.Indexes["people"]["age"].Int32Keys<ulong>().Create();
                using LibraDexStringScalar8Index name = catalog.Indexes["people"]["name"].String.Create(StringKeys.ExactAndFolded);
                using LibraDexIndex<long, ulong> scratch = catalog.Indexes["scratch"]["value"].Int64Keys<ulong>().Create();
                for (int itemIndex = 0; itemIndex < itemCount; itemIndex++)
                {
                    ulong identity = checked((ulong)itemIndex + 1UL);
                    ValidateGenericInsert(age.Insert(itemIndex % 100, identity), $"allocator reuse rebuilt age insert {itemIndex}");
                    ValidateGenericInsert(name.Insert(CreateAllocatorReuseName(itemIndex), identity), $"allocator reuse rebuilt name insert {itemIndex}");
                    ValidateGenericInsert(scratch.Insert(itemIndex, identity), $"allocator reuse rebuilt scratch insert {itemIndex}");
                }
            }

            long rebuiltLength = new FileInfo(path).Length;
            long eofGrowth = rebuiltLength - initialPopulatedLength;
            const long MaximumMetadataOnlyGrowth = 64 * 1024;
            if (eofGrowth < 0 || eofGrowth > MaximumMetadataOnlyGrowth)
            {
                throw new InvalidDataException(
                    $"Equivalent catalog recreation grew EOF by {eofGrowth:N0} bytes; expected no more than {MaximumMetadataOnlyGrowth:N0} bytes of append-fallback metadata.");
            }

            using (Catalog reopened = Catalog.Open(path, CatalogOptions.UInt64Identities))
            {
                using LibraDexIndex<int, ulong> age = reopened.Indexes["people"]["age"].Int32Keys<ulong>().Open();
                using LibraDexStringScalar8Index name = reopened.Indexes["people"]["name"].String.Open();
                using LibraDexIndex<long, ulong> scratch = reopened.Indexes["scratch"]["value"].Int64Keys<ulong>().Open();
                using LibraDexIndex<int, ulong> retained = reopened.Indexes["orders"]["number"].Int32Keys<ulong>().Open();
                int expectedAgeCount = Enumerable.Range(0, itemCount).Count(value => value % 100 == 42);
                if (age.GetIdentities(age.Where.EqualTo(42).EndCondition).Count != expectedAgeCount ||
                    !name.Entries.Exists(CreateAllocatorReuseName(itemCount - 1), checked((ulong)itemCount)) ||
                    !scratch.GetIdentities(scratch.Where.EqualTo(itemCount - 1L).EndCondition).SequenceEqual(new[] { checked((ulong)itemCount) }) ||
                    !retained.GetIdentities(retained.Where.EqualTo(100).EndCondition).SequenceEqual(new[] { 9_000_001UL }))
                {
                    throw new InvalidDataException("Rebuilt or retained catalog query semantics failed after durable extent reuse.");
                }
            }

            Console.WriteLine(
                $"catalog-allocator-drop-reuse-sanity ok | items={itemCount:N0} | initial={initialPopulatedLength:N0} | retired={retiredLength:N0} | rebuilt={rebuiltLength:N0} | eof-growth={eofGrowth:N0} | retired-reusable={retiredReusableBytes:N0} | retired-unreachable={retiredUnreachableBytes:N0}");
            return 0;
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    /// <summary>
    /// Creates one deterministic variable UTF-8 key with enough prefix diversity to exercise VS8 routing and shelf reuse.<br/>
    /// The returned string is used only by the allocator lifecycle fixture and remains stable across initial and rebuilt populations.<br/>
    /// </summary>
    /// <param name="itemIndex">The zero-based fixture item ordinal.<br/></param>
    /// <returns>A deterministic case-preserving text key.<br/></returns>
    private static string CreateAllocatorReuseName(int itemIndex) =>
        $"person-{itemIndex % 29:D2}-branch-{itemIndex % 211:D3}-item-{itemIndex:D6}";
}
