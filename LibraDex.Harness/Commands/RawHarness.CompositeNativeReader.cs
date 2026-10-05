using System.Text;
using LibraDex;

internal static partial class RawHarness
{
    /// <summary>
    /// Proves that a reopened durable composite index exposes typed scalar values plus borrowed UTF-8, raw-span, and raw-memory component views without hydrating its object router.<br/>
    /// The measured pass intentionally avoids string and byte-array materialization so its allocation bound detects accidental boxing or page-array allocation in the row path.<br/>
    /// </summary>
    /// <param name="args">The harness command-line arguments.<br/></param>
    /// <returns>Zero when direct decoding, borrowed views, reopen traversal, and the warm allocation bound all hold.<br/></returns>
    private static int RunCompositeNativeReaderSanity(string[] args)
    {
        int itemCount = GetIntOption(args, "--items", 4_096);
        if (itemCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(args), itemCount, "Composite native reader item count must be positive.");

        string path = GetOption(
            args,
            "--path",
            Path.Combine(Path.GetTempPath(), "LibraDex", $"composite-native-reader-{Guid.NewGuid():N}.lbdx"));
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        if (File.Exists(path))
            File.Delete(path);

        Dictionary<ulong, (string Name, int Age, byte[] Payload)> expected = new();
        int maxDepthItemCount = Math.Min(itemCount, 1_024);
        try
        {
            using (Catalog created = Catalog.Create(path, CatalogOptions.UInt64Identities))
            {
                IIndex raw = created.Indexes.IndexSet("people").Define("nameAgePayload")
                    .Composite<ulong>(C.Text("name"), C.Int32("age"), C.Binary("payload"))
                    .Create();
                LibraDexRoutedCompositeIndex index = raw as LibraDexRoutedCompositeIndex
                    ?? throw new InvalidDataException("Composite native reader fixture did not create a routed composite index.");
                LibraDexRoutedCompositeIndex maxDepthIndex = created.Indexes.IndexSet("people").Define("depth16")
                    .Composite<ulong>(
                        C.Int32("part1"), C.Int32("part2"), C.Int32("part3"), C.Int32("part4"),
                        C.Int32("part5"), C.Int32("part6"), C.Int32("part7"), C.Int32("part8"),
                        C.Int32("part9"), C.Int32("part10"), C.Int32("part11"), C.Int32("part12"),
                        C.Int32("part13"), C.Int32("part14"), C.Int32("part15"), C.Int32("part16"))
                    .Create() as LibraDexRoutedCompositeIndex
                    ?? throw new InvalidDataException("Composite native reader fixture did not create a 16-part routed composite index.");

                for (int i = 0; i < itemCount; i++)
                {
                    ulong identity = checked((ulong)i + 1UL);
                    string name = $"name-{i % 97:D2}";
                    int age = unchecked((i * 17) % 131) - 40;
                    byte[] payload = [(byte)(i >> 8), (byte)i, (byte)(i % 29), 0xD7];
                    ValidateGenericInsert(index.Insert(Key.Of(name, age, payload), identity), "composite native reader seed insert");
                    expected.Add(identity, (name, age, payload));
                }

                for (int i = 0; i < maxDepthItemCount; i++)
                {
                    ulong identity = checked((ulong)i + 1UL);
                    int value = checked(i * 17);
                    ValidateGenericInsert(
                        maxDepthIndex.Insert(
                            Key.Of(
                                value + 1, value + 2, value + 3, value + 4,
                                value + 5, value + 6, value + 7, value + 8,
                                value + 9, value + 10, value + 11, value + 12,
                                value + 13, value + 14, value + 15, value + 16),
                            identity),
                        "composite native reader 16-part seed insert");
                }
            }

            using Catalog reopened = Catalog.Open(path, CatalogOptions.UInt64Identities);
            CatalogIdentityGroupIndexes indexes = reopened.Indexes.IndexSet("people");
            ValidateCompositeNativeReaderRows(indexes, expected, itemCount);
            ValidateCompositeNativeReaderMaxDepth(indexes, maxDepthItemCount);
            ValidateCompositeNativeReaderAllocation(indexes, itemCount);
            Console.WriteLine($"composite-native-reader-sanity rows={itemCount:N0} depth16={maxDepthItemCount:N0} ok");
            return 0;
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    /// <summary>
    /// Validates direct typed values against the seed oracle and verifies both borrowed byte-view contracts for every returned tuple.<br/>
    /// </summary>
    /// <param name="indexes">The reopened catalog index set.<br/></param>
    /// <param name="expected">The identity-keyed seed oracle.<br/></param>
    /// <param name="itemCount">The expected tuple count.<br/></param>
    private static void ValidateCompositeNativeReaderRows(
        CatalogIdentityGroupIndexes indexes,
        IReadOnlyDictionary<ulong, (string Name, int Age, byte[] Payload)> expected,
        int itemCount)
    {
        long rows = 0;
        using LibraDexCompositeReader<string, int, byte[], ulong> reader =
            indexes.OpenCompositeReader<string, int, byte[], ulong>("nameAgePayload");
        while (reader.Read())
        {
            ulong identity = reader.Identity;
            if (!expected.TryGetValue(identity, out (string Name, int Age, byte[] Payload) row))
                throw new InvalidDataException("Composite native reader returned an identity outside the seed oracle.");
            if (reader.IsNull(0) || reader.Part1 != row.Name || reader.Part2 != row.Age || !reader.Part3.AsSpan().SequenceEqual(row.Payload))
                throw new InvalidDataException("Composite native reader typed values did not match the durable seed tuple.");

            ReadOnlySpan<byte> utf8 = reader.GetUtf8Span(0);
            ReadOnlyMemory<byte> utf8Memory = reader.GetUtf8Mem(0);
            if (!utf8.SequenceEqual(utf8Memory.Span) || !utf8.SequenceEqual(Encoding.UTF8.GetBytes(row.Name)))
                throw new InvalidDataException("Composite native reader UTF-8 view did not match the logical string payload.");

            ReadOnlySpan<byte> rawText = reader.GetRawSpan(0);
            ReadOnlyMemory<byte> rawTextMemory = reader.GetRawMem(0);
            ReadOnlySpan<byte> rawBinary = reader.GetRawSpan(2);
            if (rawText.Length <= utf8.Length || rawText[0] != 0 || !rawText.SequenceEqual(rawTextMemory.Span) ||
                rawBinary.Length != row.Payload.Length + 5 || rawBinary[0] != 0 || !rawBinary[5..].SequenceEqual(row.Payload))
            {
                throw new InvalidDataException("Composite native reader raw component views lost their persisted marker or framing contract.");
            }

            if (reader.GetIdentityRawSpan().Length != sizeof(ulong) ||
                !reader.GetIdentityRawSpan().SequenceEqual(reader.GetIdentityRawMem().Span))
            {
                throw new InvalidDataException("Composite native reader identity raw view was malformed.");
            }

            rows++;
        }

        if (rows != itemCount)
            throw new InvalidDataException($"Composite native reader returned {rows:N0} rows, expected {itemCount:N0}.");
    }

    /// <summary>
    /// Reopens and walks a persisted sixteen-part composite index through its statically declared reader shape.<br/>
    /// This is the maximum durable composite depth, so the check catches drift between the public reader family and routed-page plumbing.<br/>
    /// </summary>
    /// <param name="indexes">The reopened catalog index set.<br/></param>
    /// <param name="itemCount">The number of sixteen-part seed tuples expected from the reader.<br/></param>
    private static void ValidateCompositeNativeReaderMaxDepth(CatalogIdentityGroupIndexes indexes, int itemCount)
    {
        long rows = 0;
        using LibraDexCompositeReader<int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, ulong> reader =
            indexes.OpenCompositeReader<int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, ulong>("depth16");
        while (reader.Read())
        {
            ulong identity = reader.Identity;
            int value = checked(((int)identity - 1) * 17);
            if (reader.Part1 != value + 1 || reader.Part8 != value + 8 || reader.Part16 != value + 16)
                throw new InvalidDataException("The 16-part composite reader returned a value that did not match the durable seed tuple.");
            rows++;
        }

        if (rows != itemCount)
            throw new InvalidDataException($"The 16-part composite reader returned {rows:N0} rows, expected {itemCount:N0}.");
    }

    /// <summary>
    /// Warms direct-page buffers, then measures a second span-only pass so object boxing, array copies, and UTF-16 text conversion cannot hide in the iteration path.<br/>
    /// </summary>
    /// <param name="indexes">The reopened catalog index set.<br/></param>
    /// <param name="itemCount">The expected tuple count.<br/></param>
    private static void ValidateCompositeNativeReaderAllocation(CatalogIdentityGroupIndexes indexes, int itemCount)
    {
        using (LibraDexCompositeReader<string, int, byte[], ulong> warm =
            indexes.OpenCompositeReader<string, int, byte[], ulong>("nameAgePayload"))
        {
            while (warm.Read())
            {
                _ = warm.GetUtf8Span(0);
                _ = warm.Part2;
                _ = warm.Identity;
                _ = warm.GetRawMem(2);
            }
        }

        long rows = 0;
        ulong identityChecksum = 0;
        using LibraDexCompositeReader<string, int, byte[], ulong> measured =
            indexes.OpenCompositeReader<string, int, byte[], ulong>("nameAgePayload");
        long before = GC.GetAllocatedBytesForCurrentThread();
        while (measured.Read())
        {
            ReadOnlySpan<byte> name = measured.GetUtf8Span(0);
            ReadOnlyMemory<byte> payload = measured.GetRawMem(2);
            if (name.IsEmpty || payload.Length < 5 || measured.IsNull(0))
                throw new InvalidDataException("Composite native reader span-only measurement encountered an invalid row.");
            identityChecksum = unchecked(identityChecksum + measured.Identity + (uint)measured.Part2);
            rows++;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        if (rows != itemCount || allocated > 8_192)
        {
            throw new InvalidDataException(
                $"Composite native reader span-only pass returned {rows:N0} rows and allocated {allocated:N0} bytes; expected {itemCount:N0} rows with a bounded allocation under 8,192 bytes.");
        }
    }
}
