using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using LibraDex;
using LibraDex.Layouts;
using LibraDex.Views;
using Microsoft.Data.Sqlite;

internal static partial class RawHarness
{
    /// <summary>
    /// Measures public condition-builder string prefix counting over a real `VS8`-backed `LibraDexStringScalar8Index`.<br/>
    /// The benchmark intentionally routes through `.StartsWith(...).EndCondition.Count(...)` so it exercises the condition materializer, `Prefix` primitive, and shape-specific prefix count planner instead of the raw var-key range API.<br/>
    /// Seed keys use a path-like repeated-prefix layout to create the multi-byte router shapes that motivated the prefix extent planner work.<br/>
    /// </summary>
    /// <param name="args">Harness command arguments.<br/></param>
    /// <returns>Zero when the measured count matches the seeded expectation; otherwise an exception is thrown.<br/></returns>
    private static int RunStringPrefixCountProof(string[] args)
    {
        int itemCount = GetIntOption(args, "--items", 250_000);
        int iterations = GetIntOption(args, "--iterations", 25_000);
        int warmupIterations = GetIntOption(args, "--warmup-iterations", 3);
        int prefixGroupCount = GetIntOption(args, "--prefix-groups", 512);
        int targetGroup = GetIntOption(args, "--target-group", Math.Max(0, prefixGroupCount / 2));
        string rootPrefix = GetOption(args, "--root-prefix", @"C:\Windows");

        if (itemCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(args), itemCount, "String prefix count proof item count must be positive.");
        }

        if (iterations <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(args), iterations, "String prefix count proof iterations must be positive.");
        }

        if (warmupIterations < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(args), warmupIterations, "String prefix count proof warmup iterations cannot be negative.");
        }

        if (prefixGroupCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(args), prefixGroupCount, "String prefix count proof prefix group count must be positive.");
        }

        if ((uint)targetGroup >= (uint)prefixGroupCount)
        {
            throw new ArgumentOutOfRangeException(nameof(args), targetGroup, "String prefix count proof target group must be inside the prefix group count.");
        }

        using Catalog catalog = Catalog.CreateMemory();
        CatalogIdentityGroupIndexes group = catalog.Indexes["string-prefix-count-proof"];
        using LibraDexStringScalar8Index pathIndex = group["path"].String.Create(StringKeys.Exact);

        string targetPrefix = CreateStringPrefixCountProofPrefix(rootPrefix, targetGroup);
        long expectedCount = 0;
        for (int i = 0; i < itemCount; i++)
        {
            int groupOrdinal = i % prefixGroupCount;
            string key = CreateStringPrefixCountProofKey(rootPrefix, groupOrdinal, i);
            _ = pathIndex.Insert(key, (ulong)(i + 1));
            if (groupOrdinal == targetGroup)
            {
                expectedCount++;
            }
        }

        LibraDexConditionEndCondition condition = LibraDexCondition
            .ForGroup("string-prefix-count-proof")
            .Index("path")
            .AsString
            .StartsWith(targetPrefix)
            .EndCondition;
        Func<string, IIndex> resolver = name => name == "path" ? pathIndex : throw new KeyNotFoundException(name);

        long warmupCount = 0;
        for (int i = 0; i < warmupIterations; i++)
        {
            warmupCount = condition.Count(resolver, IdentityDeduplication.Preserve);
        }

        if (warmupIterations == 0)
        {
            warmupCount = condition.Count(resolver, IdentityDeduplication.Preserve);
        }

        if (warmupCount != expectedCount)
        {
            throw new InvalidDataException($"String prefix count proof warmup returned {warmupCount}; expected {expectedCount}.");
        }

        long checksum = 0;
        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            checksum += condition.Count(resolver, IdentityDeduplication.Preserve);
        }

        watch.Stop();
        long expectedChecksum = expectedCount * iterations;
        if (checksum != expectedChecksum)
        {
            throw new InvalidDataException($"String prefix count proof checksum returned {checksum}; expected {expectedChecksum}.");
        }

        double elapsedMs = watch.Elapsed.TotalMilliseconds;
        double opsPerSecond = iterations / Math.Max(watch.Elapsed.TotalSeconds, 0.000001D);
#if LIBRADEX_PREFIX_COUNT_TELEMETRY
        VarKeyScalar8PrefixCountTelemetry telemetry = pathIndex.LastExactPrefixCountTelemetry;
        Console.WriteLine(
            "string-prefix-count-proof ok " +
            $"items={itemCount} prefixGroups={prefixGroupCount} targetGroup={targetGroup} " +
            $"expected={expectedCount} warmup={warmupCount} iterations={iterations} elapsedMs={elapsedMs:F3} opsPerSecond={opsPerSecond:F2} checksum={checksum} " +
            $"routers={telemetry.RoutersVisited} oneByteRouters={telemetry.OneByteRoutersVisited} multiByteRouters={telemetry.MultiByteRoutersVisited} " +
            $"multiByteRoutes={telemetry.MultiByteRoutesVisited} stemMatched={telemetry.MultiByteRoutesStemMatched} containedRoutes={telemetry.MultiByteRoutesContained} ambiguousRoutes={telemetry.MultiByteRoutesAmbiguous} " +
            $"targets={telemetry.TargetsVisited} metadataTargets={telemetry.TargetsCountedByMetadata} narrowShelves={telemetry.ShelfTargetsCountedNarrow} narrowTerminalRoots={telemetry.TerminalRootsCountedNarrow} " +
            $"containedRouterScans={telemetry.ContainedRouterTargetScans} fallbackToRange={telemetry.FallbackToRangeCount}");
#else
        Console.WriteLine(
            "string-prefix-count-proof ok " +
            $"items={itemCount} prefixGroups={prefixGroupCount} targetGroup={targetGroup} " +
            $"expected={expectedCount} warmup={warmupCount} iterations={iterations} elapsedMs={elapsedMs:F3} opsPerSecond={opsPerSecond:F2} checksum={checksum}");
#endif
        return 0;
    }

    /// <summary>
    /// Creates one deterministic path-like prefix for a string prefix count proof group.<br/>
    /// The repeated root and fixed-width group segment encourage compressed multi-byte routers while keeping the expected count simple integer math.<br/>
    /// </summary>
    /// <param name="rootPrefix">The caller-selected path root.<br/></param>
    /// <param name="groupOrdinal">The prefix group ordinal.<br/></param>
    /// <returns>The string prefix used for both seeding and querying one group.<br/></returns>
    private static string CreateStringPrefixCountProofPrefix(string rootPrefix, int groupOrdinal)
    {
        return FormattableString.Invariant($@"{rootPrefix}\System32\DriverStore\FileRepository\pkg-{groupOrdinal:D5}");
    }

    /// <summary>
    /// Creates one deterministic path-like key for the public string prefix count proof.<br/>
    /// The key starts with a group prefix and appends a unique file component so prefix counts can validate against a known per-group cardinality.<br/>
    /// </summary>
    /// <param name="rootPrefix">The caller-selected path root.<br/></param>
    /// <param name="groupOrdinal">The prefix group ordinal.<br/></param>
    /// <param name="itemOrdinal">The unique item ordinal.<br/></param>
    /// <returns>A deterministic string key for insertion into the proof index.<br/></returns>
    private static string CreateStringPrefixCountProofKey(string rootPrefix, int groupOrdinal, int itemOrdinal)
    {
        return FormattableString.Invariant($@"{CreateStringPrefixCountProofPrefix(rootPrefix, groupOrdinal)}\amd64_component_{itemOrdinal:D8}.dll");
    }

    private static byte[] CreateVarKeyRepackHarnessKey(int ordinal)
    {
        byte[] key = new byte[512];
        key[0] = checked((byte)(0x20 + ordinal));
        for (int i = 1; i < key.Length; i++)
        {
            key[i] = checked((byte)('A' + (ordinal % 26)));
        }

        return key;
    }


    private static VarIdentityBufferPerfSample MeasureVarIdentityBufferPerfSample(
        string shape,
        int expectedCount,
        int iterations,
        Func<IDisposable> openReader,
        Func<LibraDexVarIdentityBuffer> readBuffer)
    {
        long checksum = 0;
        long payloadBytes = 0;
        long totalIdentities = (long)expectedCount * iterations;

        Stopwatch cursorWatch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            using IDisposable disposable = openReader();
            int count = 0;
            if (disposable is Scalar8VarIdentityRangeReader sv8Reader)
            {
                while (sv8Reader.MoveNext())
                {
                    ReadOnlySpan<byte> identity = sv8Reader.CurrentIdentity;
                    checksum += ChecksumBytes(identity);
                    payloadBytes += identity.Length;
                    count++;
                }
            }
            else if (disposable is Scalar16VarIdentityRangeReader sv16Reader)
            {
                while (sv16Reader.MoveNext())
                {
                    ReadOnlySpan<byte> identity = sv16Reader.CurrentIdentity;
                    checksum += ChecksumBytes(identity);
                    payloadBytes += identity.Length;
                    count++;
                }
            }
            else if (disposable is VarKeyVarIdentityRangeReader vvReader)
            {
                while (vvReader.MoveNext())
                {
                    ReadOnlySpan<byte> identity = vvReader.CurrentIdentity;
                    checksum += ChecksumBytes(identity);
                    payloadBytes += identity.Length;
                    count++;
                }
            }
            else
            {
                throw new InvalidDataException($"Unsupported var-identity cursor type for {shape}.");
            }

            if (count != expectedCount)
            {
                throw new InvalidDataException($"{shape} cursor read returned {count}; expected {expectedCount}.");
            }
        }

        cursorWatch.Stop();

        Stopwatch bufferBuildWatch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            using LibraDexVarIdentityBuffer buffer = readBuffer();
            if (buffer.Count != expectedCount)
            {
                throw new InvalidDataException($"{shape} identity buffer returned {buffer.Count}; expected {expectedCount}.");
            }

            checksum += buffer.Count;
            payloadBytes += buffer.PayloadLength;
        }

        bufferBuildWatch.Stop();

        Stopwatch bufferIterateWatch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            using LibraDexVarIdentityBuffer buffer = readBuffer();
            long localPayloadBytes = 0;
            for (int j = 0; j < buffer.Count; j++)
            {
                ReadOnlySpan<byte> identity = buffer[j];
                checksum += ChecksumBytes(identity);
                localPayloadBytes += identity.Length;
            }

            if (buffer.Count != expectedCount)
            {
                throw new InvalidDataException($"{shape} identity buffer iterate returned {buffer.Count}; expected {expectedCount}.");
            }

            payloadBytes += localPayloadBytes;
        }

        bufferIterateWatch.Stop();

        return new VarIdentityBufferPerfSample(
            expectedCount,
            cursorWatch.Elapsed.TotalNanoseconds / totalIdentities,
            bufferBuildWatch.Elapsed.TotalNanoseconds / totalIdentities,
            bufferIterateWatch.Elapsed.TotalNanoseconds / totalIdentities,
            payloadBytes,
            checksum);
    }


    private static VarIdentityBufferPerfSample SelectMedianVarIdentityBufferPerfSample(ReadOnlySpan<VarIdentityBufferPerfSample> samples)
    {
        VarIdentityBufferPerfSample[] sorted = samples.ToArray();
        Array.Sort(sorted, static (left, right) => left.BufferBuildAndIterateNsPerIdentity.CompareTo(right.BufferBuildAndIterateNsPerIdentity));
        return sorted[sorted.Length / 2];
    }


    private static void PrintVarIdentityBufferPerfRows(IReadOnlyList<VarIdentityBufferPerfRow> rows)
    {
        Console.WriteLine("shape | range | cursor ns/id | buffer build ns/id | buffer build+iterate ns/id | payload bytes | checksum");
        foreach (VarIdentityBufferPerfRow row in rows)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{row.Shape} | {row.RangeCount} | {row.CursorNsPerIdentity:F2} | {row.BufferBuildNsPerIdentity:F2} | {row.BufferBuildAndIterateNsPerIdentity:F2} | {row.PayloadBytes} | {row.Checksum}"));
        }
    }


    private readonly record struct VarIdentityBufferPerfSample(
        int RangeCount,
        double CursorNsPerIdentity,
        double BufferBuildNsPerIdentity,
        double BufferBuildAndIterateNsPerIdentity,
        long PayloadBytes,
        long Checksum);


    private readonly record struct VarIdentityBufferPerfRow(
        string Shape,
        int RangeCount,
        double CursorNsPerIdentity,
        double BufferBuildNsPerIdentity,
        double BufferBuildAndIterateNsPerIdentity,
        long PayloadBytes,
        long Checksum);


    /// <summary>
    /// Prints one walked-write attribution row in a routed SQLite comparison output.<br/>
    /// The method keeps unit conversion and percent math centralized so all buckets use the same denominator.<br/>
    /// </summary>
    /// <param name="name">The attribution bucket name.</param>
    /// <param name="ticks">The elapsed Stopwatch ticks attributed to the bucket.</param>
    /// <param name="totalTicks">The sum of all attributed Stopwatch ticks.</param>
    /// <param name="ticksToNsPerItem">The precomputed Stopwatch-tick to nanoseconds-per-written-item multiplier.</param>
    private static void PrintVarIdentityAttributionRow(
        string name,
        long ticks,
        long totalTicks,
        double ticksToNsPerItem)
    {
        double percent = totalTicks == 0 ? 0 : ticks * 100d / totalTicks;
        Console.WriteLine(FormattableString.Invariant($"| {name} | {ticks} | {ticks * ticksToNsPerItem:F2} | {percent:F2} |"));
    }


    /// <summary>
    /// Creates the `SV8` shelf profile requested by a routed SQLite comparison run.<br/>
    /// The harness keeps this selection explicit so shelf-size experiments do not change production defaults while write/read parity is being measured.<br/>
    /// </summary>
    /// <param name="initialShelfKiB">The initial shelf extent size in KiB; supported values are 16, 32, 64, and 128.</param>
    /// <param name="maxIdentityLength">The maximum raw identity byte length allowed by the generated comparison workload.</param>
    /// <returns>The `SV8` profile used to create the comparison index.</returns>
    private static Scalar8VarIdentityProfile CreateScalar8VarIdentityComparisonProfile(int initialShelfKiB, int maxIdentityLength)
    {
        return initialShelfKiB switch
        {
            16 => Scalar8VarIdentityProfile.Create(Scalar8VarIdentityProfile.Default16KiB.ShelfExtentSize, maxIdentityLength),
            32 => Scalar8VarIdentityProfile.Create(Scalar8VarIdentityProfile.Default32KiB.ShelfExtentSize, maxIdentityLength),
            64 => Scalar8VarIdentityProfile.Create(Scalar8VarIdentityProfile.Default64KiB.ShelfExtentSize, maxIdentityLength),
            128 => Scalar8VarIdentityProfile.Create(Scalar8VarIdentityProfile.Default128KiB.ShelfExtentSize, maxIdentityLength),
            _ => throw new ArgumentOutOfRangeException(nameof(initialShelfKiB), initialShelfKiB, "SV8 comparison initial shelf KiB must be 16, 32, 64, or 128.")
        };
    }


    /// <summary>
    /// Creates the `SV16` shelf profile requested by a routed SQLite comparison run.<br/>
    /// The harness keeps this selection explicit so shelf-size experiments do not change production defaults while write/read parity is being measured.<br/>
    /// </summary>
    /// <param name="initialShelfKiB">The initial shelf extent size in KiB; supported values are 16, 32, 64, and 128.</param>
    /// <param name="maxIdentityLength">The maximum raw identity byte length allowed by the generated comparison workload.</param>
    /// <returns>The `SV16` profile used to create the comparison index.</returns>
    private static Scalar16VarIdentityProfile CreateScalar16VarIdentityComparisonProfile(int initialShelfKiB, int maxIdentityLength)
    {
        return initialShelfKiB switch
        {
            16 => Scalar16VarIdentityProfile.Create(Scalar16VarIdentityProfile.Default16KiB.ShelfExtentSize, maxIdentityLength),
            32 => Scalar16VarIdentityProfile.Create(Scalar16VarIdentityProfile.Default32KiB.ShelfExtentSize, maxIdentityLength),
            64 => Scalar16VarIdentityProfile.Create(Scalar16VarIdentityProfile.Default64KiB.ShelfExtentSize, maxIdentityLength),
            128 => Scalar16VarIdentityProfile.Create(Scalar16VarIdentityProfile.Default128KiB.ShelfExtentSize, maxIdentityLength),
            _ => throw new ArgumentOutOfRangeException(nameof(initialShelfKiB), initialShelfKiB, "SV16 comparison initial shelf KiB must be 16, 32, 64, or 128.")
        };
    }


    private enum VarIdentityIterationMode
    {
        Identities = 0,
        Keys = 1,
        Tuples = 2
    }


    private enum Scalar8VarIdentityReadPattern
    {
        Range = 0,
        ExactKeys = 1,
        RotatingRange = 2
    }


    /// <summary>
    /// Adds the current `SV16` reader row payload requested by the iteration mode to the comparison checksum.<br/>
    /// The helper intentionally reads only the fields implied by the mode so the harness measures identities, keys, and full tuples as distinct access patterns.<br/>
    /// </summary>
    /// <param name="reader">The positioned `SV16` range reader.</param>
    /// <param name="iterationMode">The comparison iteration payload mode.</param>
    /// <returns>The checksum contribution for the current row.</returns>
    private static long ChecksumScalar16VarIdentityCurrent(Scalar16VarIdentityRangeReader reader, VarIdentityIterationMode iterationMode)
    {
        return iterationMode switch
        {
            VarIdentityIterationMode.Keys => ChecksumScalar16Key(reader.CurrentEncodedKeyHigh, reader.CurrentEncodedKeyLow),
            VarIdentityIterationMode.Tuples => ChecksumScalar16Key(reader.CurrentEncodedKeyHigh, reader.CurrentEncodedKeyLow) + ChecksumBytes(reader.CurrentIdentity),
            _ => ChecksumBytes(reader.CurrentIdentity)
        };
    }


    /// <summary>
    /// Adds the current SQLite `SV16` row payload requested by the iteration mode to the comparison checksum.<br/>
    /// The selected column positions match <see cref="CreateSqliteVarIdentityRangeReadCommandText(VarIdentityIterationMode)"/>.<br/>
    /// </summary>
    /// <param name="reader">The positioned SQLite data reader.</param>
    /// <param name="iterationMode">The comparison iteration payload mode.</param>
    /// <returns>The checksum contribution for the current row.</returns>
    private static long ChecksumSqliteScalar16VarIdentityCurrent(SqliteDataReader reader, VarIdentityIterationMode iterationMode)
    {
        return iterationMode switch
        {
            VarIdentityIterationMode.Keys => ChecksumBytes((byte[])reader.GetValue(0)),
            VarIdentityIterationMode.Tuples => ChecksumBytes((byte[])reader.GetValue(0)) + ChecksumBytes((byte[])reader.GetValue(1)),
            _ => ChecksumBytes((byte[])reader.GetValue(0))
        };
    }


    /// <summary>
    /// Creates the generated `SV8` write order used by routed and SQLite comparison rows.<br/>
    /// `natural` preserves generator order, `key-major` groups all duplicate-key identities together, and `tuple-major` sorts by the full generated tuple.<br/>
    /// </summary>
    /// <param name="itemCount">The total generated item count.</param>
    /// <param name="duplicateModulo">The generated scalar-key modulo.</param>
    /// <param name="order">The requested order name.</param>
    /// <param name="identityLength">The generated identity length used when tuple sorting is requested.</param>
    /// <param name="fixedIdentityLength">Whether identities are generated at exactly <paramref name="identityLength"/> bytes.</param>
    /// <param name="keyDistribution">The scalar-key distribution used by the comparison.</param>
    /// <returns>The generated source indexes in write order.</returns>
    private static int[] CreateScalar8VarIdentityWriteOrder(
        int itemCount,
        int duplicateModulo,
        string order,
        int identityLength,
        bool fixedIdentityLength,
        string keyDistribution)
    {
        int[] result = new int[itemCount];
        if (order.Equals("tuple-major", StringComparison.OrdinalIgnoreCase))
        {
            for (int i = 0; i < itemCount; i++)
            {
                result[i] = i;
            }

            Array.Sort(result, (left, right) =>
            {
                ulong leftKey = CreateScalar8VarIdentityKey(left, duplicateModulo, keyDistribution);
                ulong rightKey = CreateScalar8VarIdentityKey(right, duplicateModulo, keyDistribution);
                int keyComparison = leftKey.CompareTo(rightKey);
                if (keyComparison != 0)
                {
                    return keyComparison;
                }

                return Scalar8VarIdentityLayout.CompareIdentityBytes(
                    CreateScalar8VarIdentity(left, identityLength, fixedIdentityLength),
                    CreateScalar8VarIdentity(right, identityLength, fixedIdentityLength));
            });
            return result;
        }

        if (order.Equals("key-major", StringComparison.OrdinalIgnoreCase))
        {
            int cursor = 0;
            for (int key = 0; key < duplicateModulo; key++)
            {
                for (int sourceIndex = key; sourceIndex < itemCount; sourceIndex += duplicateModulo)
                {
                    result[cursor++] = sourceIndex;
                }
            }

            return result;
        }

        for (int i = 0; i < itemCount; i++)
        {
            result[i] = i;
        }

        return result;
    }


    private static int CompareScalar16VarIdentityKey(ulong leftHigh, ulong leftLow, ulong rightHigh, ulong rightLow)
    {
        int high = leftHigh.CompareTo(rightHigh);
        return high != 0 ? high : leftLow.CompareTo(rightLow);
    }


    private static int[] CreateScalar16VarIdentityWriteOrder(
        int itemCount,
        int duplicateModulo,
        string order,
        int identityLength,
        bool fixedIdentityLength,
        string keyDistribution)
    {
        int[] result = new int[itemCount];
        for (int i = 0; i < itemCount; i++)
        {
            result[i] = i;
        }

        if (order.Equals("key-major", StringComparison.OrdinalIgnoreCase) ||
            order.Equals("tuple-major", StringComparison.OrdinalIgnoreCase))
        {
            Array.Sort(result, (left, right) =>
            {
                CreateScalar16VarIdentityKey(left, duplicateModulo, keyDistribution, out ulong leftHigh, out ulong leftLow);
                CreateScalar16VarIdentityKey(right, duplicateModulo, keyDistribution, out ulong rightHigh, out ulong rightLow);
                int keyComparison = CompareScalar16VarIdentityKey(leftHigh, leftLow, rightHigh, rightLow);
                if (keyComparison != 0 || order.Equals("key-major", StringComparison.OrdinalIgnoreCase))
                {
                    return keyComparison;
                }

                return Scalar16VarIdentityLayout.CompareIdentityBytes(
                    CreateScalar8VarIdentity(left, identityLength, fixedIdentityLength),
                    CreateScalar8VarIdentity(right, identityLength, fixedIdentityLength));
            });
        }

        return result;
    }


    private static VarKeyScalar8ComparisonShelf[] BuildVarKeyScalar8ComparisonShelves(byte[][] keys, ulong[] identities, int prefixCount)
    {
        List<VarKeyScalar8ComparisonShelf> shelves = [];
        for (int prefix = 0; prefix < prefixCount; prefix++)
        {
            List<byte[]> prefixKeys = [];
            List<ulong> prefixIdentities = [];
            for (int i = 0; i < keys.Length; i++)
            {
                if (keys[i][0] == prefix)
                {
                    prefixKeys.Add(keys[i]);
                    prefixIdentities.Add(identities[i]);
                }
            }

            if (prefixKeys.Count == 0)
            {
                continue;
            }

            byte[][] keyArray = prefixKeys.ToArray();
            ulong[] identityArray = prefixIdentities.ToArray();
            VarKeyScalar8Profile profile = SelectVarKeyScalar8BuildProfile(keyArray, identityArray);
            if (!VarKeyScalar8.TryBuildFromSorted(keyArray, identityArray, profile, out byte[] shelfBytes))
            {
                throw new InvalidDataException($"VS8 comparison could not build prefix {prefix} shelf at {profile.ShelfExtentSize} bytes.");
            }

            VarKeyScalar8ReadOnly readOnly = new(shelfBytes, profile);
            if (!readOnly.IsValid)
            {
                throw new InvalidDataException($"VS8 comparison built an invalid prefix {prefix} shelf.");
            }

            shelves.Add(new VarKeyScalar8ComparisonShelf(prefix, profile, readOnly));
        }

        return shelves.ToArray();
    }


    private static byte[][] CreateSortedVarKeyScalar8Keys(int itemCount, int keyLength, int prefixCount)
    {
        byte[][] keys = new byte[itemCount][];
        for (int i = 0; i < itemCount; i++)
        {
            keys[i] = CreateVarKeyScalar8Key(i, keyLength, prefixCount);
        }

        Array.Sort(keys, static (left, right) => left.AsSpan().SequenceCompareTo(right));
        return keys;
    }


    private static ulong CreateScalar8VarIdentityKey(int index, int duplicateModulo)
    {
        return Scalar8Scalar8Layout.EncodeUnsignedScalar8((ulong)(index % duplicateModulo));
    }


    private static byte[] CreateScalar8VarIdentity(int index, int identityLength)
    {
        return CreateScalar8VarIdentity(index, identityLength, fixedLength: false);
    }


    /// <summary>
    /// Creates a generated raw `SV8` identity payload.<br/>
    /// Variable-length mode preserves the original stress pattern; fixed-length mode keeps tiny mirror-test identities unique by writing the source index into the payload tail.<br/>
    /// </summary>
    /// <param name="index">The generated source index.</param>
    /// <param name="identityLength">The requested maximum or fixed identity length.</param>
    /// <param name="fixedLength">Whether to produce exactly <paramref name="identityLength"/> bytes.</param>
    /// <returns>The generated raw identity bytes.</returns>
    private static byte[] CreateScalar8VarIdentity(int index, int identityLength, bool fixedLength)
    {
        if (fixedLength)
        {
            byte[] fixedIdentity = new byte[identityLength];
            for (int i = 0; i < fixedIdentity.Length; i++)
            {
                fixedIdentity[i] = (byte)((index * 193 + i * 29) & 0xFF);
            }

            int bytesToWrite = Math.Min(sizeof(int), fixedIdentity.Length);
            int firstIndexByte = fixedIdentity.Length - bytesToWrite;
            for (int i = 0; i < bytesToWrite; i++)
            {
                int shift = (bytesToWrite - 1 - i) * 8;
                fixedIdentity[firstIndexByte + i] = (byte)(index >> shift);
            }

            return fixedIdentity;
        }

        string prefix = string.Create(CultureInfo.InvariantCulture, $"item/{index:X8}/");
        int lengthVariation = index % Math.Min(identityLength, 11);
        byte[] identity = new byte[identityLength - lengthVariation];
        byte[] prefixBytes = Encoding.ASCII.GetBytes(prefix);
        prefixBytes.AsSpan(0, Math.Min(prefixBytes.Length, identity.Length)).CopyTo(identity);
        for (int i = prefixBytes.Length; i < identity.Length; i++)
        {
            identity[i] = (byte)((index * 193 + i * 29) & 0xFF);
        }

        return identity;
    }


    private static int CountScalar8VarIdentityKeyRange(int itemCount, int duplicateModulo, int lowerKey, int upperKey)
    {
        int count = 0;
        for (int i = 0; i < itemCount; i++)
        {
            int key = i % duplicateModulo;
            if (key >= lowerKey && key <= upperKey)
            {
                count++;
            }
        }

        return count;
    }


    private static int CountScalar8VarIdentityExactKeys(int itemCount, int duplicateModulo, int firstKey, int keyCount)
    {
        int count = 0;
        int limit = firstKey + keyCount;
        for (int i = 0; i < itemCount; i++)
        {
            int key = i % duplicateModulo;
            if (key >= firstKey && key < limit)
            {
                count++;
            }
        }

        return count;
    }


    private static int CountScalar8VarIdentityRotatingRanges(int itemCount, int duplicateModulo, int rangeKeyCount, int windowCount, int windowStep)
    {
        int count = 0;
        int maxStart = duplicateModulo - rangeKeyCount;
        int startModulo = maxStart + 1;
        for (int windowIndex = 0; windowIndex < windowCount; windowIndex++)
        {
            int lowerKey = checked((windowIndex * windowStep) % startModulo);
            count += CountScalar8VarIdentityKeyRange(itemCount, duplicateModulo, lowerKey, lowerKey + rangeKeyCount - 1);
        }

        return count;
    }


    private static byte[] CreateVarKeyScalar8Key(int index, int keyLength, int prefixCount)
    {
        byte[] key = new byte[keyLength];
        int prefix = index % prefixCount;
        key[0] = (byte)prefix;
        if (keyLength > 1)
        {
            key[1] = (byte)(index / Math.Max(prefixCount, 1));
        }

        if (keyLength > 2)
        {
            key[2] = (byte)(index >> 8);
        }

        if (keyLength > 3)
        {
            key[3] = (byte)index;
        }

        for (int i = 4; i < key.Length; i++)
        {
            key[i] = (byte)((index * 131 + i * 17) & 0xFF);
        }

        return key;
    }


    private static byte[] CreateVarKeyScalar8PrefixLower(int prefix, int keyLength)
    {
        byte[] key = new byte[keyLength];
        key[0] = (byte)prefix;
        return key;
    }


    private static byte[] CreateVarKeyScalar8PrefixUpper(int prefix, int keyLength)
    {
        byte[] key = new byte[keyLength];
        key[0] = (byte)prefix;
        key.AsSpan(1).Fill(0xFF);
        return key;
    }


    private static byte[] CreateVarKeyScalar8LongSharedPrefixKey(int index, int keyLength, int sharedPrefixLength)
    {
        byte[] key = new byte[keyLength];
        key.AsSpan(0, Math.Min(sharedPrefixLength, keyLength)).Fill(0x35);
        if (keyLength > sharedPrefixLength)
        {
            key[sharedPrefixLength] = (byte)(index >> 8);
        }

        if (keyLength > sharedPrefixLength + 1)
        {
            key[sharedPrefixLength + 1] = (byte)index;
        }

        for (int i = sharedPrefixLength + 2; i < key.Length; i++)
        {
            key[i] = (byte)((index * 197 + i * 23) & 0xFF);
        }

        return key;
    }


    private static byte[] CreateVarKeyScalar8HierarchicalKey(int index, int sharedPrefixLength, int branchCount, int maxKeyLength)
    {
        string sharedPrefix = CreateVarKeyScalar8HierarchicalSharedPrefix(sharedPrefixLength);
        int branch = index % branchCount;
        int subBranch = (index / Math.Max(branchCount, 1)) % Math.Max(branchCount, 1);
        string path = string.Create(
            CultureInfo.InvariantCulture,
            $"{sharedPrefix}/dept{branch:X2}/zone{subBranch:X2}/file{index:X8}");
        byte[] key = Encoding.ASCII.GetBytes(path);
        if (key.Length > maxKeyLength)
        {
            throw new ArgumentOutOfRangeException(nameof(maxKeyLength), maxKeyLength, $"Generated hierarchical key length {key.Length} exceeds the requested maximum.");
        }

        return key;
    }


    private static byte[] CreateVarKeyScalar8HierarchicalRangeLower(int sharedPrefixLength)
    {
        return Encoding.ASCII.GetBytes(CreateVarKeyScalar8HierarchicalSharedPrefix(sharedPrefixLength));
    }


    private static byte[] CreateVarKeyScalar8HierarchicalRangeUpper(int sharedPrefixLength)
    {
        byte[] lower = CreateVarKeyScalar8HierarchicalRangeLower(sharedPrefixLength);
        byte[] upper = new byte[lower.Length + 1];
        lower.CopyTo(upper, 0);
        upper[^1] = 0xFF;
        return upper;
    }


    private static ulong CreateVarKeyScalar8Identity(int index)
    {
        return unchecked(0x8100_0000_0000_0000UL | (uint)index);
    }


    private static void CreateVarKeyScalar16Identity(int index, out ulong identityHigh, out ulong identityLow)
    {
        identityHigh = unchecked(0xA100_0000_0000_0000UL | (uint)(index / 1024));
        identityLow = unchecked(0xB100_0000_0000_0000UL | (uint)index);
    }


    private static int CompareVarKeyScalar16Tuple(
        ReadOnlySpan<byte> leftKey,
        ulong leftIdentityHigh,
        ulong leftIdentityLow,
        ReadOnlySpan<byte> rightKey,
        ulong rightIdentityHigh,
        ulong rightIdentityLow)
    {
        int keyComparison = leftKey.SequenceCompareTo(rightKey);
        if (keyComparison != 0)
        {
            return keyComparison;
        }

        if (leftIdentityHigh < rightIdentityHigh)
        {
            return -1;
        }

        if (leftIdentityHigh > rightIdentityHigh)
        {
            return 1;
        }

        if (leftIdentityLow < rightIdentityLow)
        {
            return -1;
        }

        return leftIdentityLow > rightIdentityLow ? 1 : 0;
    }


    private static int CountVarKeyScalar8PrefixRange(int itemCount, int prefixCount, int lowerPrefix, int upperPrefix)
    {
        int count = 0;
        for (int i = 0; i < itemCount; i++)
        {
            int prefix = i % prefixCount;
            if (prefix >= lowerPrefix && prefix <= upperPrefix)
            {
                count++;
            }
        }

        return count;
    }


    private static VarKeyScalar8Profile SelectVarKeyScalar8BuildProfile(ReadOnlySpan<byte[]> keys, ReadOnlySpan<ulong> identities)
    {
        VarKeyScalar8Profile profile = VarKeyScalar8Profile.DefaultInitial;
        while (!VarKeyScalar8.TryBuildFromSorted(keys, identities, profile, out _))
        {
            VarKeyScalar8Profile next = profile.NextGrowthClass();
            if (next.ShelfExtentSize == profile.ShelfExtentSize)
            {
                throw new InvalidDataException("VS8 comparison input does not fit in the maximum first-pass shelf profile.");
            }

            profile = next;
        }

        return profile;
    }


    /// <summary>
    /// Selects the smallest `VS16` shelf profile that can hold an already-sorted key and identity batch.<br/>
    /// This mirrors the `VS8` comparison helper and changes only the widened identity lanes, preserving the clone-first benchmark contract.<br/>
    /// The helper throws when even the maximum configured `VS16` shelf extent cannot hold the supplied prefix group, which means the comparison input needs more routing fanout rather than silent truncation.<br/>
    /// </summary>
    /// <param name="keys">The sorted raw key byte arrays.</param>
    /// <param name="identityHighs">The sorted high identity lanes aligned with <paramref name="keys"/>.</param>
    /// <param name="identityLows">The sorted low identity lanes aligned with <paramref name="keys"/>.</param>
    /// <returns>The smallest `VS16` profile that can build the supplied shelf.</returns>
    private static VarKeyScalar16Profile SelectVarKeyScalar16BuildProfile(ReadOnlySpan<byte[]> keys, ReadOnlySpan<ulong> identityHighs, ReadOnlySpan<ulong> identityLows)
    {
        VarKeyScalar16Profile profile = VarKeyScalar16Profile.DefaultInitial;
        while (!VarKeyScalar16.TryBuildFromSorted(keys, identityHighs, identityLows, profile, out _))
        {
            VarKeyScalar16Profile next = profile.NextGrowthClass();
            if (next.ShelfExtentSize == profile.ShelfExtentSize)
            {
                throw new InvalidDataException("VS16 comparison input does not fit in the maximum first-pass shelf profile.");
            }

            profile = next;
        }

        return profile;
    }


    /// <summary>
    /// Selects the smallest `VV` shelf profile that can hold an already-sorted key and identity batch.<br/>
    /// This mirrors the `VS8` and `VS16` comparison helpers while preserving byte-ordinal identity ordering for duplicate-key stability.<br/>
    /// The helper throws when even the maximum configured `VV` shelf extent cannot hold the supplied prefix group, which means the comparison input needs more routing fanout rather than silent truncation.<br/>
    /// </summary>
    /// <param name="keys">The sorted raw key byte arrays.</param>
    /// <param name="identities">The sorted raw identity byte arrays aligned with <paramref name="keys"/>.</param>
    /// <returns>The smallest `VV` profile that can build the supplied shelf.</returns>
    private static VarKeyVarIdentityProfile SelectVarKeyVarIdentityBuildProfile(
        ReadOnlySpan<byte[]> keys,
        ReadOnlySpan<byte[]> identities,
        VarKeyVarIdentityProfile initialProfile)
    {
        VarKeyVarIdentityProfile profile = initialProfile;
        while (!VarKeyVarIdentity.TryBuildFromSorted(keys, identities, profile, out _))
        {
            VarKeyVarIdentityProfile next = profile.NextGrowthClass();
            if (next.ShelfExtentSize == profile.ShelfExtentSize)
            {
                throw new InvalidDataException("VV comparison input does not fit in the maximum first-pass shelf profile.");
            }

            profile = next;
        }

        return profile;
    }


    private static VarKeyScalar8ReadMeasurement MeasureVarKeyScalar8RangeReads(VarKeyScalar8ComparisonShelf[] shelves, int keyLength, int prefixCount, int itemCount, int iterations)
    {
        byte[] lower = CreateVarKeyScalar8PrefixLower(0, keyLength);
        byte[] upper = CreateVarKeyScalar8PrefixUpper(Math.Min(2, prefixCount - 1), keyLength);
        int expected = CountVarKeyScalar8PrefixRange(itemCount, prefixCount, 0, Math.Min(2, prefixCount - 1));
        ulong[] identities = new ulong[itemCount];
        long checksum = 0;
        long total = 0;
        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            int count = 0;
            for (int shelfIndex = 0; shelfIndex < shelves.Length; shelfIndex++)
            {
                VarKeyScalar8ComparisonShelf shelf = shelves[shelfIndex];
                if (shelf.Prefix > Math.Min(2, prefixCount - 1))
                {
                    continue;
                }

                count += shelf.ReadOnly.CopyIdentitiesInKeyRange(lower, upper, identities.AsSpan(count));
            }

            if (count != expected)
            {
                throw new InvalidDataException($"VS8 read comparison expected {expected} identities, got {count}.");
            }

            total += count;
            for (int j = 0; j < count; j++)
            {
                checksum += unchecked((long)identities[j]);
            }
        }

        watch.Stop();
        return CreateVarKeyScalar8ReadMeasurement(iterations, expected, total, watch.Elapsed, checksum);
    }


    /// <summary>
    /// Adds the current `VS8` reader row payload requested by the iteration mode to the comparison checksum.<br/>
    /// The helper intentionally reads only the fields implied by the mode so the harness measures identities, keys, and full tuples as distinct access patterns.<br/>
    /// </summary>
    /// <param name="reader">The positioned `VS8` range reader.</param>
    /// <param name="iterationMode">The comparison iteration payload mode.</param>
    /// <returns>The checksum contribution for the current row.</returns>
    private static long ChecksumVarKeyScalar8Current(VarKeyScalar8RangeReader reader, VarIdentityIterationMode iterationMode)
    {
        return iterationMode switch
        {
            VarIdentityIterationMode.Keys => ChecksumBytes(reader.CurrentKey),
            VarIdentityIterationMode.Tuples => ChecksumBytes(reader.CurrentKey) + unchecked((long)reader.CurrentEncodedIdentity),
            _ => unchecked((long)reader.CurrentEncodedIdentity)
        };
    }


    /// <summary>
    /// Adds the current `VS16` reader row payload requested by the iteration mode to the comparison checksum.<br/>
    /// The helper reads both identity halves in one call for identity and tuple modes, keeping the measurement close to the fixed-width shelf primitive.<br/>
    /// </summary>
    /// <param name="reader">The positioned `VS16` range reader.</param>
    /// <param name="iterationMode">The comparison iteration payload mode.</param>
    /// <returns>The checksum contribution for the current row.</returns>
    private static long ChecksumVarKeyScalar16Current(VarKeyScalar16RangeReader reader, VarIdentityIterationMode iterationMode)
    {
        if (iterationMode == VarIdentityIterationMode.Keys)
        {
            return ChecksumBytes(reader.CurrentKey);
        }

        reader.ReadCurrentIdentity(out ulong identityHigh, out ulong identityLow);
        long identityChecksum = unchecked((long)identityHigh) + unchecked((long)identityLow);
        return iterationMode == VarIdentityIterationMode.Tuples
            ? ChecksumBytes(reader.CurrentKey) + identityChecksum
            : identityChecksum;
    }


    /// <summary>
    /// Adds the current `VV` reader row payload requested by the iteration mode to the comparison checksum.<br/>
    /// The helper intentionally reads only the requested spans so identity-only iteration does not touch the key payload and key-only iteration does not touch the identity payload.<br/>
    /// </summary>
    /// <param name="reader">The positioned `VV` range reader.</param>
    /// <param name="iterationMode">The comparison iteration payload mode.</param>
    /// <returns>The checksum contribution for the current row.</returns>
    private static long ChecksumVarKeyVarIdentityCurrent(VarKeyVarIdentityRangeReader reader, VarIdentityIterationMode iterationMode)
    {
        return iterationMode switch
        {
            VarIdentityIterationMode.Keys => ChecksumBytes(reader.CurrentKey),
            VarIdentityIterationMode.Tuples => ChecksumBytes(reader.CurrentKey) + ChecksumBytes(reader.CurrentIdentity),
            _ => ChecksumBytes(reader.CurrentIdentity)
        };
    }


    /// <summary>
    /// Adds the current SQLite `VS8` row payload requested by the iteration mode to the comparison checksum.<br/>
    /// The selected column positions match <see cref="CreateSqliteVarIdentityRangeReadCommandText(VarIdentityIterationMode)"/>.<br/>
    /// </summary>
    /// <param name="reader">The positioned SQLite reader.</param>
    /// <param name="iterationMode">The comparison iteration payload mode.</param>
    /// <returns>The checksum contribution for the current row.</returns>
    private static long ChecksumSqliteVarKeyScalar8Current(SqliteDataReader reader, VarIdentityIterationMode iterationMode)
    {
        return iterationMode switch
        {
            VarIdentityIterationMode.Keys => ChecksumBytes((byte[])reader.GetValue(0)),
            VarIdentityIterationMode.Tuples => ChecksumBytes((byte[])reader.GetValue(0)) + unchecked((long)DecodeSqliteSortableUnsignedScalar8(reader.GetInt64(1))),
            _ => unchecked((long)DecodeSqliteSortableUnsignedScalar8(reader.GetInt64(0)))
        };
    }


    /// <summary>
    /// Adds the current SQLite `VS16` row payload requested by the iteration mode to the comparison checksum.<br/>
    /// The helper decodes the 16-byte identity BLOB as two big-endian halves to match the LibraDex checksum semantics.<br/>
    /// </summary>
    /// <param name="reader">The positioned SQLite reader.</param>
    /// <param name="iterationMode">The comparison iteration payload mode.</param>
    /// <returns>The checksum contribution for the current row.</returns>
    private static long ChecksumSqliteVarKeyScalar16Current(SqliteDataReader reader, VarIdentityIterationMode iterationMode)
    {
        if (iterationMode == VarIdentityIterationMode.Keys)
        {
            return ChecksumBytes((byte[])reader.GetValue(0));
        }

        int identityOrdinal = iterationMode == VarIdentityIterationMode.Tuples ? 1 : 0;
        byte[] identity = (byte[])reader.GetValue(identityOrdinal);
        long identityChecksum =
            unchecked((long)BinaryPrimitives.ReadUInt64BigEndian(identity.AsSpan(0, 8))) +
            unchecked((long)BinaryPrimitives.ReadUInt64BigEndian(identity.AsSpan(8, 8)));
        return iterationMode == VarIdentityIterationMode.Tuples
            ? ChecksumBytes((byte[])reader.GetValue(0)) + identityChecksum
            : identityChecksum;
    }


    /// <summary>
    /// Adds the current SQLite `VV` row payload requested by the iteration mode to the comparison checksum.<br/>
    /// The selected column positions match <see cref="CreateSqliteVarIdentityRangeReadCommandText(VarIdentityIterationMode)"/>.<br/>
    /// </summary>
    /// <param name="reader">The positioned SQLite reader.</param>
    /// <param name="iterationMode">The comparison iteration payload mode.</param>
    /// <returns>The checksum contribution for the current row.</returns>
    private static long ChecksumSqliteVarKeyVarIdentityCurrent(SqliteDataReader reader, VarIdentityIterationMode iterationMode)
    {
        return iterationMode switch
        {
            VarIdentityIterationMode.Keys => ChecksumBytes((byte[])reader.GetValue(0)),
            VarIdentityIterationMode.Tuples => ChecksumBytes((byte[])reader.GetValue(0)) + ChecksumBytes((byte[])reader.GetValue(1)),
            _ => ChecksumBytes((byte[])reader.GetValue(0))
        };
    }


    private static VarKeyScalar8ReadMeasurement MeasureVarKeyScalar8ExplicitRangeReads(
        LibraDexFileSession session,
        long rootOffset,
        int maxKeyLength,
        ReadOnlySpan<byte> lower,
        ReadOnlySpan<byte> upper,
        int expected,
        int iterations)
    {
        ulong[] identities = new ulong[expected];
        long checksum = 0;
        long total = 0;
        _ = session.GetAndResetReadTelemetry();
        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            int count = session.ReadVarKeyScalar8IdentityRange(rootOffset, maxKeyLength, lower, upper, identities);
            if (count != expected)
            {
                throw new InvalidDataException($"VS8 explicit range expected {expected} identities, got {count}.");
            }

            total += count;
            for (int j = 0; j < count; j++)
            {
                checksum += unchecked((long)identities[j]);
            }
        }

        watch.Stop();
        DataKernelReadTelemetry telemetry = session.GetAndResetReadTelemetry();
        (int cacheArenaCount, long cacheBytes) = session.GetRouterArenaReadCacheStatsForValidation();
        return CreateVarKeyScalar8ReadMeasurement(iterations, expected, total, watch.Elapsed, checksum, telemetry, cacheArenaCount, cacheBytes);
    }


    /// <summary>
    /// Measures routed `VS16` explicit raw-key range reads through an opened session.<br/>
    /// This helper is used by hierarchical/path-like comparisons where the range is not a first-byte prefix range.<br/>
    /// The checksum folds both identity halves so high/low preservation is validated while measuring the same range-scoop path.<br/>
    /// </summary>
    /// <param name="session">The opened LibraDex session.</param>
    /// <param name="rootOffset">The root router offset for the `VS16` index.</param>
    /// <param name="maxKeyLength">The maximum raw key length accepted by the index.</param>
    /// <param name="lower">The inclusive lower raw key.</param>
    /// <param name="upper">The inclusive upper raw key.</param>
    /// <param name="expected">The expected number of identities per iteration.</param>
    /// <param name="iterations">The number of measured range reads.</param>
    /// <returns>The routed range-read measurement row.</returns>
    private static VarKeyScalar8ReadMeasurement MeasureVarKeyScalar16ExplicitRangeReads(
        LibraDexFileSession session,
        long rootOffset,
        int maxKeyLength,
        ReadOnlySpan<byte> lower,
        ReadOnlySpan<byte> upper,
        int expected,
        int iterations)
    {
        ulong[] identityHighs = new ulong[expected];
        ulong[] identityLows = new ulong[expected];
        long checksum = 0;
        long total = 0;
        _ = session.GetAndResetReadTelemetry();
        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            int count = session.ReadVarKeyScalar16IdentityRange(rootOffset, maxKeyLength, lower, upper, identityHighs, identityLows);
            if (count != expected)
            {
                throw new InvalidDataException($"VS16 explicit range expected {expected} identities, got {count}.");
            }

            total += count;
            for (int j = 0; j < count; j++)
            {
                checksum += unchecked((long)identityHighs[j]);
                checksum += unchecked((long)identityLows[j]);
            }
        }

        watch.Stop();
        DataKernelReadTelemetry telemetry = session.GetAndResetReadTelemetry();
        (int cacheArenaCount, long cacheBytes) = session.GetRouterArenaReadCacheStatsForValidation();
        return CreateVarKeyScalar8ReadMeasurement(iterations, expected, total, watch.Elapsed, checksum, telemetry, cacheArenaCount, cacheBytes);
    }


    /// <summary>
    /// Measures routed `VV` prefix-range reads through an opened session.<br/>
    /// The helper folds raw identity bytes into the checksum so reads validate both count and payload preservation while reporting logical identity throughput.<br/>
    /// </summary>
    /// <param name="session">The opened LibraDex session.</param>
    /// <param name="rootOffset">The root router offset for the `VV` index.</param>
    /// <param name="keyLength">The generated key length in bytes.</param>
    /// <param name="prefixCount">The number of generated first-byte prefixes.</param>
    /// <param name="itemCount">The total generated identity count.</param>
    /// <param name="iterations">The number of measured range reads.</param>
    /// <param name="maxRouterHops">The maximum number of router pages to follow during each measured routed read.</param>
    /// <returns>The routed range-read measurement row.</returns>
    private static VarKeyScalar8ReadMeasurement MeasureVarKeyVarIdentityRoutedRangeReads(
        LibraDexFileSession session,
        long rootOffset,
        int keyLength,
        int prefixCount,
        int itemCount,
        int iterations,
        VarIdentityIterationMode iterationMode,
        int maxRouterHops = 8)
    {
        byte[] lower = CreateVarKeyScalar8PrefixLower(0, keyLength);
        byte[] upper = CreateVarKeyScalar8PrefixUpper(Math.Min(2, prefixCount - 1), keyLength);
        int expected = CountVarKeyScalar8PrefixRange(itemCount, prefixCount, 0, Math.Min(2, prefixCount - 1));
        long checksum = 0;
        long total = 0;
        _ = session.GetAndResetReadTelemetry();
        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            int count = 0;
            long iterationChecksum = 0;
            using VarKeyVarIdentityRangeReader reader = session.OpenVarKeyVarIdentityRangeReader(rootOffset, maxKeyLength: 1024, maxIdentityLength: 1024, lower, upper, maxRouterHops);
            while (reader.MoveNext())
            {
                count++;
                iterationChecksum += ChecksumVarKeyVarIdentityCurrent(reader, iterationMode);
            }

            if (count != expected)
            {
                throw new InvalidDataException($"VV range expected {expected} identities, got {count}.");
            }

            total += count;
            checksum += iterationChecksum;
        }

        watch.Stop();
        DataKernelReadTelemetry telemetry = session.GetAndResetReadTelemetry();
        (int cacheArenaCount, long cacheBytes) = session.GetRouterArenaReadCacheStatsForValidation();
        return CreateVarKeyScalar8ReadMeasurement(iterations, expected, total, watch.Elapsed, checksum, telemetry, cacheArenaCount, cacheBytes);
    }


    private static VarKeyScalar8RouterShapeStats MeasureVarKeyScalar8RouterShapeStats(LibraDexFileSession session, long rootOffset)
    {
        VarKeyScalar8RouterShapeStatsBuilder builder = default;
        using RouteVisitedOffsetSet visitedRouters = RouteVisitedOffsetSet.Rent();
        using RouteVisitedOffsetSet visitedShelves = RouteVisitedOffsetSet.Rent();
        VisitVarKeyScalar8RouterShape(session, rootOffset, depth: 0, visitedRouters, visitedShelves, ref builder);
        return builder.ToStats();
    }


    private static void VisitVarKeyScalar8RouterShape(
        LibraDexFileSession session,
        long routerOffset,
        int depth,
        RouteVisitedOffsetSet visitedRouters,
        RouteVisitedOffsetSet visitedShelves,
        ref VarKeyScalar8RouterShapeStatsBuilder builder)
    {
        if (!visitedRouters.Add(routerOffset))
        {
            return;
        }

        byte[] routerBytes = new byte[RouterLayout.Size];
        session.ReadRouterPageForRangeScan(routerOffset, routerBytes);
        RouterReader reader = new(routerBytes);
        builder.RouterCount++;
        if (reader.HasDirectIndex)
        {
            builder.DirectRouterCount++;
        }

        if (reader.PrefixByteCount > 1)
        {
            builder.MultiByteRouterCount++;
        }

        for (int routeIndex = 0; routeIndex < reader.RouteCount; routeIndex++)
        {
            long targetOffset = reader.GetRouteTargetAt(routeIndex);
            if (targetOffset == 0)
            {
                continue;
            }

            if (TryVisitVarKeyScalar8ChildRouter(session, targetOffset, depth + 1, visitedRouters, visitedShelves, ref builder))
            {
                continue;
            }

            if (visitedShelves.Add(targetOffset))
            {
                builder.ShelfCount++;
                builder.LeafDepthTotal += depth + 1;
                builder.MaxRouteDepth = Math.Max(builder.MaxRouteDepth, depth + 1);
            }
        }
    }


    private static bool TryVisitVarKeyScalar8ChildRouter(
        LibraDexFileSession session,
        long targetOffset,
        int depth,
        RouteVisitedOffsetSet visitedRouters,
        RouteVisitedOffsetSet visitedShelves,
        ref VarKeyScalar8RouterShapeStatsBuilder builder)
    {
        try
        {
            byte[] routerBytes = new byte[RouterLayout.Size];
            session.ReadRouterPageForRangeScan(targetOffset, routerBytes);
        }
        catch (InvalidDataException)
        {
            return false;
        }

        VisitVarKeyScalar8RouterShape(session, targetOffset, depth, visitedRouters, visitedShelves, ref builder);
        return true;
    }


    /// <summary>
    /// Measures the bytes reachable from a `VS8` root router by summing distinct router pages and distinct shelf extents.<br/>
    /// This is a cold accounting helper for optimizer economics; it intentionally follows the currently reachable route graph rather than the whole file.<br/>
    /// Callers can subtract one router page when they need the reclaimable subtree below an unchanged root router.<br/>
    /// </summary>
    /// <param name="session">The opened or active session containing the route graph.</param>
    /// <param name="rootOffset">The root router offset to traverse.</param>
    /// <returns>The total bytes occupied by reachable router pages and `VS8` shelf extents.</returns>
    private static long MeasureVarKeyScalar8ReachableStorageBytes(LibraDexFileSession session, long rootOffset)
    {
        using RouteVisitedOffsetSet visitedRouters = RouteVisitedOffsetSet.Rent();
        using RouteVisitedOffsetSet visitedShelves = RouteVisitedOffsetSet.Rent();
        return MeasureVarKeyScalar8ReachableStorageBytes(session, rootOffset, visitedRouters, visitedShelves);
    }


    private static long MeasureVarKeyScalar8ReachableStorageBytes(
        LibraDexFileSession session,
        long routerOffset,
        RouteVisitedOffsetSet visitedRouters,
        RouteVisitedOffsetSet visitedShelves)
    {
        if (!visitedRouters.Add(routerOffset))
        {
            return 0;
        }

        long total = RouterLayout.Size;
        byte[] routerBytes = new byte[RouterLayout.Size];
        session.ReadRouterPageForRangeScan(routerOffset, routerBytes);
        RouterReader reader = new(routerBytes);
        if (!reader.IsValid)
        {
            throw new InvalidDataException("The VS8 reachable-storage traversal read an invalid router.");
        }

        for (int routeIndex = 0; routeIndex < reader.RouteCount; routeIndex++)
        {
            long targetOffset = reader.GetRouteTargetAt(routeIndex);
            if (targetOffset == 0)
            {
                continue;
            }

            VarKeyScalar8RouteTargetKind kind = session.ClassifyVarKeyScalar8RouteTarget(targetOffset);
            if (kind == VarKeyScalar8RouteTargetKind.Router)
            {
                total += MeasureVarKeyScalar8ReachableStorageBytes(session, targetOffset, visitedRouters, visitedShelves);
                continue;
            }

            if (kind == VarKeyScalar8RouteTargetKind.Shelf)
            {
                if (visitedShelves.Add(targetOffset))
                {
                    total += session.ReadVarKeyScalar8ShelfExtentSizeForValidation(targetOffset);
                }

                continue;
            }

            throw new InvalidDataException("The VS8 reachable-storage traversal found an unsupported route target.");
        }

        return total;
    }


    /// <summary>
    /// Measures the bytes reachable from a `VS16` root router by summing distinct router pages and distinct shelf extents.<br/>
    /// This is the widened-identity counterpart to the `VS8` optimizer economics helper and follows only the currently reachable route graph.<br/>
    /// Callers can subtract one router page when they need the reclaimable subtree below an unchanged root router.<br/>
    /// </summary>
    /// <param name="session">The opened or active session containing the route graph.</param>
    /// <param name="rootOffset">The root router offset to traverse.</param>
    /// <returns>The total bytes occupied by reachable router pages and `VS16` shelf extents.</returns>
    private static long MeasureVarKeyScalar16ReachableStorageBytes(LibraDexFileSession session, long rootOffset)
    {
        using RouteVisitedOffsetSet visitedRouters = RouteVisitedOffsetSet.Rent();
        using RouteVisitedOffsetSet visitedShelves = RouteVisitedOffsetSet.Rent();
        return MeasureVarKeyScalar16ReachableStorageBytes(session, rootOffset, visitedRouters, visitedShelves);
    }


    private static long MeasureVarKeyScalar16ReachableStorageBytes(
        LibraDexFileSession session,
        long routerOffset,
        RouteVisitedOffsetSet visitedRouters,
        RouteVisitedOffsetSet visitedShelves)
    {
        if (!visitedRouters.Add(routerOffset))
        {
            return 0;
        }

        long total = RouterLayout.Size;
        byte[] routerBytes = new byte[RouterLayout.Size];
        session.ReadRouterPageForRangeScan(routerOffset, routerBytes);
        RouterReader reader = new(routerBytes);
        if (!reader.IsValid)
        {
            throw new InvalidDataException("The VS16 reachable-storage traversal read an invalid router.");
        }

        for (int routeIndex = 0; routeIndex < reader.RouteCount; routeIndex++)
        {
            long targetOffset = reader.GetRouteTargetAt(routeIndex);
            if (targetOffset == 0)
            {
                continue;
            }

            VarKeyScalar16RouteTargetKind kind = session.ClassifyVarKeyScalar16RouteTarget(targetOffset);
            if (kind == VarKeyScalar16RouteTargetKind.Router)
            {
                total += MeasureVarKeyScalar16ReachableStorageBytes(session, targetOffset, visitedRouters, visitedShelves);
                continue;
            }

            if (kind == VarKeyScalar16RouteTargetKind.Shelf)
            {
                if (visitedShelves.Add(targetOffset))
                {
                    total += session.ReadVarKeyScalar16ShelfExtentSizeForValidation(targetOffset);
                }

                continue;
            }

            throw new InvalidDataException("The VS16 reachable-storage traversal found an unsupported route target.");
        }

        return total;
    }


    private static VarKeyScalar8HierarchicalBulkBuildResult CreateVarKeyScalar8HierarchicalBulkTree(
        LibraDexFileSession session,
        long rootOffset,
        ReadOnlySpan<byte[]> keys,
        int maxKeyLength,
        int requestedRouteCount)
    {
        (byte rootPrefix, long childRouterOffset, VarKeyScalar8HierarchicalBulkBuildResult result) = CreateVarKeyScalar8HierarchicalBulkSubtree(
            session,
            keys,
            maxKeyLength,
            requestedRouteCount);
        _ = session.UpdateRouterRoute(rootOffset, rootPrefix, rootPrefix, rootPrefix, childRouterOffset);
        return result;
    }


    private static (byte RootPrefix, long TargetOffset, VarKeyScalar8HierarchicalBulkBuildResult Build) CreateVarKeyScalar8HierarchicalBulkSubtree(
        LibraDexFileSession session,
        ReadOnlySpan<byte[]> keys,
        int maxKeyLength,
        int requestedRouteCount)
    {
        VarLenOptimizerReplacementSubtree subtree = session.CreateVarKeyScalar8OptimizerReplacementSubtree(keys, maxKeyLength, requestedRouteCount);
        VarKeyScalar8HierarchicalBulkBuildResult result = new(
            subtree.Build.FanoutDepth,
            subtree.Build.RouteCount,
            subtree.Build.RouterCount,
            subtree.Build.ShelfCount,
            subtree.Build.MaxPlannerDepth);
        return (subtree.RootPrefix, subtree.TargetOffset, result);
    }


    /// <summary>
    /// Builds one recursive `VS8` bulk node for a sorted contiguous item range.<br/>
    /// The method is a cold construction primitive: it first tries to pack the whole range into one shelf, and only creates a compressed multi-byte router when the range no longer fits the maximum shelf profile.<br/>
    /// Child targets are created before the parent router so route slots can persist final target offsets without later rewrites.<br/>
    /// </summary>
    /// <param name="session">The file session receiving shelves and routers.</param>
    /// <param name="items">The globally sorted key/identity pairs.</param>
    /// <param name="start">The inclusive start item index.</param>
    /// <param name="end">The exclusive end item index.</param>
    /// <param name="keyDepth">The first key byte depth this node may consume.</param>
    /// <param name="maxKeyLength">The maximum raw key length supported by the target profile.</param>
    /// <param name="requestedRouteCount">The preferred route count cap for each compressed router.</param>
    /// <param name="state">The accumulated cold-planner shape telemetry.</param>
    /// <returns>The created shelf or router file offset for this node.</returns>
    private static long CreateVarKeyScalar8HierarchicalBulkNode(
        LibraDexFileSession session,
        List<(byte[] Key, ulong Identity)> items,
        int start,
        int end,
        int keyDepth,
        int maxKeyLength,
        int requestedRouteCount,
        ref VarKeyScalar8HierarchicalBulkBuildState state)
    {
        if (TryBuildVarKeyScalar8BulkShelf(items, start, end, maxKeyLength, out VarKeyScalar8Profile profile, out byte[] shelfBytes))
        {
            (long shelfOffset, _) = session.CreateVarKeyScalar8Shelf(profile, shelfBytes);
            state.ShelfCount++;
            state.MaxPlannerDepth = Math.Max(state.MaxPlannerDepth, keyDepth);
            return shelfOffset;
        }

        int fanoutDepth = FindVarKeyScalar8FirstDivergenceDepth(items, start, end, keyDepth, maxKeyLength);
        if (state.FirstFanoutDepth == 0)
        {
            state.FirstFanoutDepth = fanoutDepth;
        }

        byte[] stem = CreateVarKeyScalar8BulkStem(items[start].Key, keyDepth, fanoutDepth);
        byte[] distinctFanoutBytes = CreateVarKeyScalar8DistinctFanoutBytes(items, start, end, fanoutDepth);
        int maxFittingRouteCount = CalculateVarKeyScalar8MaxBulkRouteCount(checked((byte)(fanoutDepth - keyDepth + 1)));
        int estimatedLeafCapacity = EstimateVarKeyScalar8BulkLeafCapacity(items, start, end, maxKeyLength);
        int minimumUsefulRouteCount = Math.Max(2, (end - start + estimatedLeafCapacity - 1) / estimatedLeafCapacity);
        int routeCount = Math.Min(Math.Min(Math.Min(Math.Max(1, requestedRouteCount), minimumUsefulRouteCount), distinctFanoutBytes.Length), maxFittingRouteCount);
        if (routeCount < distinctFanoutBytes.Length &&
            !DoVarKeyScalar8BulkRouteGroupsFitLeaves(items, start, end, fanoutDepth, distinctFanoutBytes, routeCount, maxKeyLength))
        {
            routeCount = Math.Min(distinctFanoutBytes.Length, maxFittingRouteCount);
        }

        if (routeCount <= 0)
        {
            throw new InvalidDataException("The hierarchical bulk route planner could not fit a compressed multi-byte route.");
        }

        RouterMultiByteRouteSnapshot[] routes = new RouterMultiByteRouteSnapshot[routeCount];
        int itemStart = start;
        for (int routeIndex = 0; routeIndex < routeCount; routeIndex++)
        {
            int firstFanoutIndex = routeIndex * distinctFanoutBytes.Length / routeCount;
            int nextFanoutIndex = (routeIndex + 1) * distinctFanoutBytes.Length / routeCount;
            byte startByte = distinctFanoutBytes[firstFanoutIndex];
            byte endByte = routeIndex == routeCount - 1
                ? byte.MaxValue
                : checked((byte)(distinctFanoutBytes[nextFanoutIndex] - 1));
            int itemEnd = itemStart;
            while (itemEnd < end && GetVarKeyScalar8KeyByteOrZero(items[itemEnd].Key, fanoutDepth) <= endByte)
            {
                itemEnd++;
            }

            if (itemEnd == itemStart)
            {
                throw new InvalidDataException("The hierarchical bulk route planner produced an empty route.");
            }

            long targetOffset = CreateVarKeyScalar8HierarchicalBulkNode(
                session,
                items,
                itemStart,
                itemEnd,
                checked(fanoutDepth + 1),
                maxKeyLength,
                requestedRouteCount,
                ref state);
            routes[routeIndex] = new RouterMultiByteRouteSnapshot(stem, startByte, endByte, targetOffset);
            itemStart = itemEnd;
        }

        if (itemStart != end)
        {
            throw new InvalidDataException("The hierarchical bulk route planner did not consume every item in the current node.");
        }

        (RouterSnapshot router, _) = session.CreateCompressedMultiByteRouter(
            checked((byte)(fanoutDepth - keyDepth + 1)),
            checked((ushort)keyDepth),
            maxRouteCount: checked((ushort)routes.Length),
            allocationClassId: 0,
            routes);
        state.RouterCount++;
        state.RouteCount += routes.Length;
        state.MaxPlannerDepth = Math.Max(state.MaxPlannerDepth, fanoutDepth + 1);
        return router.Offset;
    }


    private static int FindVarKeyScalar8FirstDivergenceDepth(
        List<(byte[] Key, ulong Identity)> items,
        int startDepth,
        int maxKeyLength)
    {
        for (int depth = startDepth; depth < maxKeyLength; depth++)
        {
            byte first = GetVarKeyScalar8KeyByteOrZero(items[0].Key, depth);
            for (int i = 1; i < items.Count; i++)
            {
                if (GetVarKeyScalar8KeyByteOrZero(items[i].Key, depth) != first)
                {
                    return depth;
                }
            }
        }

        throw new InvalidDataException("The hierarchical bulk keys do not diverge within the maximum key length.");
    }


    /// <summary>
    /// Finds the first key byte depth where a sorted subrange diverges.<br/>
    /// This overload supports recursive cold bulk planning, where each child node has already consumed the parent route bytes and starts searching at a deeper byte position.<br/>
    /// </summary>
    /// <param name="items">The globally sorted key/identity pairs.</param>
    /// <param name="start">The inclusive start item index.</param>
    /// <param name="end">The exclusive end item index.</param>
    /// <param name="startDepth">The first key byte depth to test.</param>
    /// <param name="maxKeyLength">The maximum supported key byte length.</param>
    /// <returns>The first divergent byte depth.</returns>
    private static int FindVarKeyScalar8FirstDivergenceDepth(
        List<(byte[] Key, ulong Identity)> items,
        int start,
        int end,
        int startDepth,
        int maxKeyLength)
    {
        for (int depth = startDepth; depth < maxKeyLength; depth++)
        {
            byte first = GetVarKeyScalar8KeyByteOrZero(items[start].Key, depth);
            for (int i = start + 1; i < end; i++)
            {
                if (GetVarKeyScalar8KeyByteOrZero(items[i].Key, depth) != first)
                {
                    return depth;
                }
            }
        }

        throw new InvalidDataException("The hierarchical bulk keys do not diverge within the maximum key length.");
    }


    private static byte[] CreateVarKeyScalar8BulkStem(byte[] representativeKey, int keyDepth, int fanoutDepth)
    {
        byte[] stem = new byte[fanoutDepth - keyDepth];
        for (int i = 0; i < stem.Length; i++)
        {
            stem[i] = GetVarKeyScalar8KeyByteOrZero(representativeKey, keyDepth + i);
        }

        return stem;
    }


    private static byte[] CreateVarKeyScalar8DistinctFanoutBytes(
        List<(byte[] Key, ulong Identity)> items,
        int fanoutDepth)
    {
        List<byte> distinct = [];
        byte? previous = null;
        for (int i = 0; i < items.Count; i++)
        {
            byte current = GetVarKeyScalar8KeyByteOrZero(items[i].Key, fanoutDepth);
            if (previous is null || current != previous.Value)
            {
                distinct.Add(current);
                previous = current;
            }
        }

        return distinct.ToArray();
    }


    /// <summary>
    /// Creates the distinct sorted fanout bytes for a recursive item subrange.<br/>
    /// The caller uses these bytes to divide one cold-planned compressed router into non-overlapping final-byte ranges.<br/>
    /// </summary>
    /// <param name="items">The globally sorted key/identity pairs.</param>
    /// <param name="start">The inclusive start item index.</param>
    /// <param name="end">The exclusive end item index.</param>
    /// <param name="fanoutDepth">The key byte depth that becomes the final consumed byte for the current router.</param>
    /// <returns>The distinct fanout bytes in sorted order.</returns>
    private static byte[] CreateVarKeyScalar8DistinctFanoutBytes(
        List<(byte[] Key, ulong Identity)> items,
        int start,
        int end,
        int fanoutDepth)
    {
        List<byte> distinct = [];
        byte? previous = null;
        for (int i = start; i < end; i++)
        {
            byte current = GetVarKeyScalar8KeyByteOrZero(items[i].Key, fanoutDepth);
            if (previous is null || current != previous.Value)
            {
                distinct.Add(current);
                previous = current;
            }
        }

        return distinct.ToArray();
    }


    /// <summary>
    /// Estimates how many sorted `VS8` items from the current range fit in one maximum-size shelf.<br/>
    /// This cold planner helper intentionally measures with the real shelf builder instead of guessing from average key length, because varlen record overhead and slot reserve policy are part of the physical shape being planned.<br/>
    /// </summary>
    /// <param name="items">The globally sorted key/identity pairs.</param>
    /// <param name="start">The inclusive start item index.</param>
    /// <param name="end">The exclusive end item index.</param>
    /// <param name="maxKeyLength">The maximum raw key length supported by the target profile.</param>
    /// <returns>The estimated maximum leaf item count, never less than one.</returns>
    private static int EstimateVarKeyScalar8BulkLeafCapacity(
        List<(byte[] Key, ulong Identity)> items,
        int start,
        int end,
        int maxKeyLength)
    {
        int low = 1;
        int high = end - start;
        int best = 1;
        while (low <= high)
        {
            int mid = low + ((high - low) / 2);
            if (TryBuildVarKeyScalar8BulkShelf(items, start, start + mid, maxKeyLength, out _, out _))
            {
                best = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return best;
    }


    /// <summary>
    /// Tests whether a compressed router grouping can terminate every route group as a shelf leaf.<br/>
    /// Grouping multiple final-byte fanout values is safe only for leaf targets, because a recursive child router would otherwise route on deeper bytes across a range whose primary full-key ordering is still dominated by the grouped parent byte.<br/>
    /// </summary>
    /// <param name="items">The globally sorted key/identity pairs.</param>
    /// <param name="start">The inclusive start item index.</param>
    /// <param name="end">The exclusive end item index.</param>
    /// <param name="fanoutDepth">The final consumed key byte depth for the candidate router.</param>
    /// <param name="distinctFanoutBytes">The sorted distinct fanout bytes for the current item range.</param>
    /// <param name="routeCount">The candidate route count.</param>
    /// <param name="maxKeyLength">The maximum raw key length supported by the target profile.</param>
    /// <returns>True when every candidate route group fits in one shelf; otherwise, false.</returns>
    private static bool DoVarKeyScalar8BulkRouteGroupsFitLeaves(
        List<(byte[] Key, ulong Identity)> items,
        int start,
        int end,
        int fanoutDepth,
        byte[] distinctFanoutBytes,
        int routeCount,
        int maxKeyLength)
    {
        int itemStart = start;
        for (int routeIndex = 0; routeIndex < routeCount; routeIndex++)
        {
            int firstFanoutIndex = routeIndex * distinctFanoutBytes.Length / routeCount;
            int nextFanoutIndex = (routeIndex + 1) * distinctFanoutBytes.Length / routeCount;
            byte endByte = routeIndex == routeCount - 1
                ? byte.MaxValue
                : checked((byte)(distinctFanoutBytes[nextFanoutIndex] - 1));
            int itemEnd = itemStart;
            while (itemEnd < end && GetVarKeyScalar8KeyByteOrZero(items[itemEnd].Key, fanoutDepth) <= endByte)
            {
                itemEnd++;
            }

            if (itemEnd == itemStart ||
                !TryBuildVarKeyScalar8BulkShelf(items, itemStart, itemEnd, maxKeyLength, out _, out _))
            {
                return false;
            }

            itemStart = itemEnd;
        }

        return itemStart == end;
    }


    private static void BuildVarKeyScalar8BulkShelf(
        List<(byte[] Key, ulong Identity)> items,
        int start,
        int end,
        out VarKeyScalar8Profile profile,
        out byte[] shelfBytes)
    {
        byte[][] shelfKeys = new byte[end - start][];
        ulong[] identities = new ulong[end - start];
        for (int i = start; i < end; i++)
        {
            shelfKeys[i - start] = items[i].Key;
            identities[i - start] = items[i].Identity;
        }

        profile = SelectVarKeyScalar8BuildProfile(shelfKeys, identities);
        if (!VarKeyScalar8.TryBuildFromSorted(shelfKeys, identities, profile, out shelfBytes))
        {
            throw new InvalidDataException("The hierarchical bulk shelf failed to build after profile selection.");
        }
    }


    /// <summary>
    /// Tries to build one `VS8` bulk shelf for a sorted item subrange.<br/>
    /// The method is used by the recursive bulk planner as the leaf test before it decides to introduce another compressed router level.<br/>
    /// </summary>
    /// <param name="items">The globally sorted key/identity pairs.</param>
    /// <param name="start">The inclusive start item index.</param>
    /// <param name="end">The exclusive end item index.</param>
    /// <param name="maxKeyLength">The maximum raw key length supported by the target profile.</param>
    /// <param name="profile">The selected shelf profile when the range fits.</param>
    /// <param name="shelfBytes">The built shelf bytes when the range fits.</param>
    /// <returns>True when the range fits in one configured shelf; otherwise, false.</returns>
    private static bool TryBuildVarKeyScalar8BulkShelf(
        List<(byte[] Key, ulong Identity)> items,
        int start,
        int end,
        int maxKeyLength,
        out VarKeyScalar8Profile profile,
        out byte[] shelfBytes)
    {
        byte[][] shelfKeys = new byte[end - start][];
        ulong[] identities = new ulong[end - start];
        for (int i = start; i < end; i++)
        {
            shelfKeys[i - start] = items[i].Key;
            identities[i - start] = items[i].Identity;
        }

        profile = VarKeyScalar8Profile.DefaultInitial;
        if (profile.MaxKeyLength != maxKeyLength)
        {
            profile = VarKeyScalar8Profile.Create(profile.ShelfExtentSize, maxKeyLength);
        }

        while (!VarKeyScalar8.TryBuildFromSorted(shelfKeys, identities, profile, out shelfBytes))
        {
            VarKeyScalar8Profile next = profile.NextGrowthClass();
            if (next.ShelfExtentSize == profile.ShelfExtentSize)
            {
                shelfBytes = [];
                return false;
            }

            profile = next.MaxKeyLength == maxKeyLength
                ? next
                : VarKeyScalar8Profile.Create(next.ShelfExtentSize, maxKeyLength);
        }

        return true;
    }


    private static byte GetVarKeyScalar8KeyByteOrZero(byte[] key, int depth)
    {
        return depth >= 0 && depth < key.Length
            ? key[depth]
            : (byte)0;
    }


    private static VarKeyScalar16HierarchicalBulkBuildResult CreateVarKeyScalar16HierarchicalBulkTree(
        LibraDexFileSession session,
        long rootOffset,
        ReadOnlySpan<byte[]> keys,
        int maxKeyLength,
        int requestedRouteCount)
    {
        (byte rootPrefix, long childRouterOffset, VarKeyScalar16HierarchicalBulkBuildResult result) = CreateVarKeyScalar16HierarchicalBulkSubtree(
            session,
            keys,
            maxKeyLength,
            requestedRouteCount);
        _ = session.UpdateRouterRoute(rootOffset, rootPrefix, rootPrefix, rootPrefix, childRouterOffset);
        return result;
    }


    private static (byte RootPrefix, long TargetOffset, VarKeyScalar16HierarchicalBulkBuildResult Build) CreateVarKeyScalar16HierarchicalBulkSubtree(
        LibraDexFileSession session,
        ReadOnlySpan<byte[]> keys,
        int maxKeyLength,
        int requestedRouteCount)
    {
        VarLenOptimizerReplacementSubtree subtree = session.CreateVarKeyScalar16OptimizerReplacementSubtree(keys, maxKeyLength, requestedRouteCount);
        VarKeyScalar16HierarchicalBulkBuildResult result = new(
            subtree.Build.FanoutDepth,
            subtree.Build.RouteCount,
            subtree.Build.RouterCount,
            subtree.Build.ShelfCount,
            subtree.Build.MaxPlannerDepth);
        return (subtree.RootPrefix, subtree.TargetOffset, result);
    }


    /// <summary>
    /// Builds one recursive `VS16` bulk node for a sorted contiguous item range.<br/>
    /// The method is a cold construction primitive: it first tries to pack the whole range into one shelf, and only creates a compressed multi-byte router when the range no longer fits the maximum shelf profile.<br/>
    /// Child targets are created before the parent router so route slots can persist final target offsets without later rewrites.<br/>
    /// </summary>
    /// <param name="session">The file session receiving shelves and routers.</param>
    /// <param name="items">The globally sorted key/identity pairs.</param>
    /// <param name="start">The inclusive start item index.</param>
    /// <param name="end">The exclusive end item index.</param>
    /// <param name="keyDepth">The first key byte depth this node may consume.</param>
    /// <param name="maxKeyLength">The maximum raw key length supported by the target profile.</param>
    /// <param name="requestedRouteCount">The preferred route count cap for each compressed router.</param>
    /// <param name="state">The accumulated cold-planner shape telemetry.</param>
    /// <returns>The created shelf or router file offset for this node.</returns>
    private static long CreateVarKeyScalar16HierarchicalBulkNode(
        LibraDexFileSession session,
        List<(byte[] Key, ulong IdentityHigh, ulong IdentityLow)> items,
        int start,
        int end,
        int keyDepth,
        int maxKeyLength,
        int requestedRouteCount,
        ref VarKeyScalar16HierarchicalBulkBuildState state)
    {
        if (TryBuildVarKeyScalar16BulkShelf(items, start, end, maxKeyLength, out VarKeyScalar16Profile profile, out byte[] shelfBytes))
        {
            (long shelfOffset, _) = session.CreateVarKeyScalar16Shelf(profile, shelfBytes);
            state.ShelfCount++;
            state.MaxPlannerDepth = Math.Max(state.MaxPlannerDepth, keyDepth);
            return shelfOffset;
        }

        int fanoutDepth = FindVarKeyScalar16FirstDivergenceDepth(items, start, end, keyDepth, maxKeyLength);
        if (state.FirstFanoutDepth == 0)
        {
            state.FirstFanoutDepth = fanoutDepth;
        }

        byte[] stem = CreateVarKeyScalar16BulkStem(items[start].Key, keyDepth, fanoutDepth);
        byte[] distinctFanoutBytes = CreateVarKeyScalar16DistinctFanoutBytes(items, start, end, fanoutDepth);
        int maxFittingRouteCount = CalculateVarKeyScalar16MaxBulkRouteCount(checked((byte)(fanoutDepth - keyDepth + 1)));
        int estimatedLeafCapacity = EstimateVarKeyScalar16BulkLeafCapacity(items, start, end, maxKeyLength);
        int minimumUsefulRouteCount = Math.Max(2, (end - start + estimatedLeafCapacity - 1) / estimatedLeafCapacity);
        int routeCount = Math.Min(Math.Min(Math.Min(Math.Max(1, requestedRouteCount), minimumUsefulRouteCount), distinctFanoutBytes.Length), maxFittingRouteCount);
        if (routeCount < distinctFanoutBytes.Length &&
            !DoVarKeyScalar16BulkRouteGroupsFitLeaves(items, start, end, fanoutDepth, distinctFanoutBytes, routeCount, maxKeyLength))
        {
            routeCount = Math.Min(distinctFanoutBytes.Length, maxFittingRouteCount);
        }

        if (routeCount <= 0)
        {
            throw new InvalidDataException("The hierarchical bulk route planner could not fit a compressed multi-byte route.");
        }

        RouterMultiByteRouteSnapshot[] routes = new RouterMultiByteRouteSnapshot[routeCount];
        int itemStart = start;
        for (int routeIndex = 0; routeIndex < routeCount; routeIndex++)
        {
            int firstFanoutIndex = routeIndex * distinctFanoutBytes.Length / routeCount;
            int nextFanoutIndex = (routeIndex + 1) * distinctFanoutBytes.Length / routeCount;
            byte startByte = distinctFanoutBytes[firstFanoutIndex];
            byte endByte = routeIndex == routeCount - 1
                ? byte.MaxValue
                : checked((byte)(distinctFanoutBytes[nextFanoutIndex] - 1));
            int itemEnd = itemStart;
            while (itemEnd < end && GetVarKeyScalar16KeyByteOrZero(items[itemEnd].Key, fanoutDepth) <= endByte)
            {
                itemEnd++;
            }

            if (itemEnd == itemStart)
            {
                throw new InvalidDataException("The hierarchical bulk route planner produced an empty route.");
            }

            long targetOffset = CreateVarKeyScalar16HierarchicalBulkNode(
                session,
                items,
                itemStart,
                itemEnd,
                checked(fanoutDepth + 1),
                maxKeyLength,
                requestedRouteCount,
                ref state);
            routes[routeIndex] = new RouterMultiByteRouteSnapshot(stem, startByte, endByte, targetOffset);
            itemStart = itemEnd;
        }

        if (itemStart != end)
        {
            throw new InvalidDataException("The hierarchical bulk route planner did not consume every item in the current node.");
        }

        (RouterSnapshot router, _) = session.CreateCompressedMultiByteRouter(
            checked((byte)(fanoutDepth - keyDepth + 1)),
            checked((ushort)keyDepth),
            maxRouteCount: checked((ushort)routes.Length),
            allocationClassId: 0,
            routes);
        state.RouterCount++;
        state.RouteCount += routes.Length;
        state.MaxPlannerDepth = Math.Max(state.MaxPlannerDepth, fanoutDepth + 1);
        return router.Offset;
    }


    private static int FindVarKeyScalar16FirstDivergenceDepth(
        List<(byte[] Key, ulong IdentityHigh, ulong IdentityLow)> items,
        int startDepth,
        int maxKeyLength)
    {
        for (int depth = startDepth; depth < maxKeyLength; depth++)
        {
            byte first = GetVarKeyScalar16KeyByteOrZero(items[0].Key, depth);
            for (int i = 1; i < items.Count; i++)
            {
                if (GetVarKeyScalar16KeyByteOrZero(items[i].Key, depth) != first)
                {
                    return depth;
                }
            }
        }

        throw new InvalidDataException("The hierarchical bulk keys do not diverge within the maximum key length.");
    }


    /// <summary>
    /// Finds the first key byte depth where a sorted subrange diverges.<br/>
    /// This overload supports recursive cold bulk planning, where each child node has already consumed the parent route bytes and starts searching at a deeper byte position.<br/>
    /// </summary>
    /// <param name="items">The globally sorted key/identity pairs.</param>
    /// <param name="start">The inclusive start item index.</param>
    /// <param name="end">The exclusive end item index.</param>
    /// <param name="startDepth">The first key byte depth to test.</param>
    /// <param name="maxKeyLength">The maximum supported key byte length.</param>
    /// <returns>The first divergent byte depth.</returns>
    private static int FindVarKeyScalar16FirstDivergenceDepth(
        List<(byte[] Key, ulong IdentityHigh, ulong IdentityLow)> items,
        int start,
        int end,
        int startDepth,
        int maxKeyLength)
    {
        for (int depth = startDepth; depth < maxKeyLength; depth++)
        {
            byte first = GetVarKeyScalar16KeyByteOrZero(items[start].Key, depth);
            for (int i = start + 1; i < end; i++)
            {
                if (GetVarKeyScalar16KeyByteOrZero(items[i].Key, depth) != first)
                {
                    return depth;
                }
            }
        }

        throw new InvalidDataException("The hierarchical bulk keys do not diverge within the maximum key length.");
    }


    private static byte[] CreateVarKeyScalar16BulkStem(byte[] representativeKey, int keyDepth, int fanoutDepth)
    {
        byte[] stem = new byte[fanoutDepth - keyDepth];
        for (int i = 0; i < stem.Length; i++)
        {
            stem[i] = GetVarKeyScalar16KeyByteOrZero(representativeKey, keyDepth + i);
        }

        return stem;
    }


    private static byte[] CreateVarKeyScalar16DistinctFanoutBytes(
        List<(byte[] Key, ulong IdentityHigh, ulong IdentityLow)> items,
        int fanoutDepth)
    {
        List<byte> distinct = [];
        byte? previous = null;
        for (int i = 0; i < items.Count; i++)
        {
            byte current = GetVarKeyScalar16KeyByteOrZero(items[i].Key, fanoutDepth);
            if (previous is null || current != previous.Value)
            {
                distinct.Add(current);
                previous = current;
            }
        }

        return distinct.ToArray();
    }


    /// <summary>
    /// Creates the distinct sorted fanout bytes for a recursive item subrange.<br/>
    /// The caller uses these bytes to divide one cold-planned compressed router into non-overlapping final-byte ranges.<br/>
    /// </summary>
    /// <param name="items">The globally sorted key/identity pairs.</param>
    /// <param name="start">The inclusive start item index.</param>
    /// <param name="end">The exclusive end item index.</param>
    /// <param name="fanoutDepth">The key byte depth that becomes the final consumed byte for the current router.</param>
    /// <returns>The distinct fanout bytes in sorted order.</returns>
    private static byte[] CreateVarKeyScalar16DistinctFanoutBytes(
        List<(byte[] Key, ulong IdentityHigh, ulong IdentityLow)> items,
        int start,
        int end,
        int fanoutDepth)
    {
        List<byte> distinct = [];
        byte? previous = null;
        for (int i = start; i < end; i++)
        {
            byte current = GetVarKeyScalar16KeyByteOrZero(items[i].Key, fanoutDepth);
            if (previous is null || current != previous.Value)
            {
                distinct.Add(current);
                previous = current;
            }
        }

        return distinct.ToArray();
    }


    /// <summary>
    /// Estimates how many sorted `VS16` items from the current range fit in one maximum-size shelf.<br/>
    /// This cold planner helper intentionally measures with the real shelf builder instead of guessing from average key length, because varlen record overhead and slot reserve policy are part of the physical shape being planned.<br/>
    /// </summary>
    /// <param name="items">The globally sorted key/identity pairs.</param>
    /// <param name="start">The inclusive start item index.</param>
    /// <param name="end">The exclusive end item index.</param>
    /// <param name="maxKeyLength">The maximum raw key length supported by the target profile.</param>
    /// <returns>The estimated maximum leaf item count, never less than one.</returns>
    private static int EstimateVarKeyScalar16BulkLeafCapacity(
        List<(byte[] Key, ulong IdentityHigh, ulong IdentityLow)> items,
        int start,
        int end,
        int maxKeyLength)
    {
        int low = 1;
        int high = end - start;
        int best = 1;
        while (low <= high)
        {
            int mid = low + ((high - low) / 2);
            if (TryBuildVarKeyScalar16BulkShelf(items, start, start + mid, maxKeyLength, out _, out _))
            {
                best = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return best;
    }


    /// <summary>
    /// Tests whether a compressed router grouping can terminate every route group as a shelf leaf.<br/>
    /// Grouping multiple final-byte fanout values is safe only for leaf targets, because a recursive child router would otherwise route on deeper bytes across a range whose primary full-key ordering is still dominated by the grouped parent byte.<br/>
    /// </summary>
    /// <param name="items">The globally sorted key/identity pairs.</param>
    /// <param name="start">The inclusive start item index.</param>
    /// <param name="end">The exclusive end item index.</param>
    /// <param name="fanoutDepth">The final consumed key byte depth for the candidate router.</param>
    /// <param name="distinctFanoutBytes">The sorted distinct fanout bytes for the current item range.</param>
    /// <param name="routeCount">The candidate route count.</param>
    /// <param name="maxKeyLength">The maximum raw key length supported by the target profile.</param>
    /// <returns>True when every candidate route group fits in one shelf; otherwise, false.</returns>
    private static bool DoVarKeyScalar16BulkRouteGroupsFitLeaves(
        List<(byte[] Key, ulong IdentityHigh, ulong IdentityLow)> items,
        int start,
        int end,
        int fanoutDepth,
        byte[] distinctFanoutBytes,
        int routeCount,
        int maxKeyLength)
    {
        int itemStart = start;
        for (int routeIndex = 0; routeIndex < routeCount; routeIndex++)
        {
            int firstFanoutIndex = routeIndex * distinctFanoutBytes.Length / routeCount;
            int nextFanoutIndex = (routeIndex + 1) * distinctFanoutBytes.Length / routeCount;
            byte endByte = routeIndex == routeCount - 1
                ? byte.MaxValue
                : checked((byte)(distinctFanoutBytes[nextFanoutIndex] - 1));
            int itemEnd = itemStart;
            while (itemEnd < end && GetVarKeyScalar16KeyByteOrZero(items[itemEnd].Key, fanoutDepth) <= endByte)
            {
                itemEnd++;
            }

            if (itemEnd == itemStart ||
                !TryBuildVarKeyScalar16BulkShelf(items, itemStart, itemEnd, maxKeyLength, out _, out _))
            {
                return false;
            }

            itemStart = itemEnd;
        }

        return itemStart == end;
    }


    private static void BuildVarKeyScalar16BulkShelf(
        List<(byte[] Key, ulong IdentityHigh, ulong IdentityLow)> items,
        int start,
        int end,
        out VarKeyScalar16Profile profile,
        out byte[] shelfBytes)
    {
        byte[][] shelfKeys = new byte[end - start][];
        ulong[] identityHighs = new ulong[end - start];
        ulong[] identityLows = new ulong[end - start];
        for (int i = start; i < end; i++)
        {
            shelfKeys[i - start] = items[i].Key;
            identityHighs[i - start] = items[i].IdentityHigh;
            identityLows[i - start] = items[i].IdentityLow;
        }

        profile = SelectVarKeyScalar16BuildProfile(shelfKeys, identityHighs, identityLows);
        if (!VarKeyScalar16.TryBuildFromSorted(shelfKeys, identityHighs, identityLows, profile, out shelfBytes))
        {
            throw new InvalidDataException("The hierarchical bulk shelf failed to build after profile selection.");
        }
    }


    /// <summary>
    /// Tries to build one `VS16` bulk shelf for a sorted item subrange.<br/>
    /// The method is used by the recursive bulk planner as the leaf test before it decides to introduce another compressed router level.<br/>
    /// </summary>
    /// <param name="items">The globally sorted key/identity pairs.</param>
    /// <param name="start">The inclusive start item index.</param>
    /// <param name="end">The exclusive end item index.</param>
    /// <param name="maxKeyLength">The maximum raw key length supported by the target profile.</param>
    /// <param name="profile">The selected shelf profile when the range fits.</param>
    /// <param name="shelfBytes">The built shelf bytes when the range fits.</param>
    /// <returns>True when the range fits in one configured shelf; otherwise, false.</returns>
    private static bool TryBuildVarKeyScalar16BulkShelf(
        List<(byte[] Key, ulong IdentityHigh, ulong IdentityLow)> items,
        int start,
        int end,
        int maxKeyLength,
        out VarKeyScalar16Profile profile,
        out byte[] shelfBytes)
    {
        byte[][] shelfKeys = new byte[end - start][];
        ulong[] identityHighs = new ulong[end - start];
        ulong[] identityLows = new ulong[end - start];
        for (int i = start; i < end; i++)
        {
            shelfKeys[i - start] = items[i].Key;
            identityHighs[i - start] = items[i].IdentityHigh;
            identityLows[i - start] = items[i].IdentityLow;
        }

        profile = VarKeyScalar16Profile.DefaultInitial;
        if (profile.MaxKeyLength != maxKeyLength)
        {
            profile = VarKeyScalar16Profile.Create(profile.ShelfExtentSize, maxKeyLength);
        }

        while (!VarKeyScalar16.TryBuildFromSorted(shelfKeys, identityHighs, identityLows, profile, out shelfBytes))
        {
            VarKeyScalar16Profile next = profile.NextGrowthClass();
            if (next.ShelfExtentSize == profile.ShelfExtentSize)
            {
                shelfBytes = [];
                return false;
            }

            profile = next.MaxKeyLength == maxKeyLength
                ? next
                : VarKeyScalar16Profile.Create(next.ShelfExtentSize, maxKeyLength);
        }

        return true;
    }


    private static byte GetVarKeyScalar16KeyByteOrZero(byte[] key, int depth)
    {
        return depth >= 0 && depth < key.Length
            ? key[depth]
            : (byte)0;
    }


    private static VarKeyScalar8ReadMeasurement CreateVarKeyScalar8ReadMeasurement(
        int iterations,
        int rangeIdentityCount,
        long totalIdentities,
        TimeSpan elapsed,
        long checksum,
        DataKernelReadTelemetry readTelemetry = default,
        int routerArenaCacheCount = 0,
        long routerArenaCacheBytes = 0)
    {
        double identitiesPerSecond = totalIdentities / Math.Max(elapsed.TotalSeconds, 0.000001);
        double nsPerIdentity = elapsed.TotalMilliseconds * 1_000_000 / Math.Max(totalIdentities, 1);
        return new VarKeyScalar8ReadMeasurement(iterations, rangeIdentityCount, identitiesPerSecond, nsPerIdentity, checksum, readTelemetry, routerArenaCacheCount, routerArenaCacheBytes);
    }


    private static void InitializeSqliteVarKeyScalar8Schema(SqliteConnection connection)
    {
        ExecuteSqliteScalar8Scalar8NonQuery(
            connection,
            "CREATE TABLE items(k BLOB NOT NULL, i INTEGER NOT NULL, PRIMARY KEY(k, i)) WITHOUT ROWID;");
    }


    /// <summary>
    /// Creates the SQLite `VS16` comparison table and index shape.<br/>
    /// The key remains variable-length raw bytes, while the identity is constrained to a 16-byte BLOB so SQLite compares the same ordered identity bytes as the LibraDex `VS16` shelf.<br/>
    /// `WITHOUT ROWID` keeps the primary-key B-tree as the table itself, matching the existing `VS8` comparison policy.<br/>
    /// </summary>
    /// <param name="connection">The open SQLite connection.</param>
    private static void InitializeSqliteVarKeyScalar16Schema(SqliteConnection connection)
    {
        ExecuteSqliteScalar8Scalar8NonQuery(
            connection,
            "CREATE TABLE items(k BLOB NOT NULL, i BLOB NOT NULL CHECK(length(i)=16), PRIMARY KEY(k, i)) WITHOUT ROWID;");
    }


    /// <summary>
    /// Creates the SQLite `VV` comparison table and index shape.<br/>
    /// Both key and identity are persisted as raw BLOB values so SQLite compares the same byte-ordinal tuple as the LibraDex `VV` shelf.<br/>
    /// `WITHOUT ROWID` keeps the primary-key B-tree as the table itself, matching the existing varlen comparison policy.<br/>
    /// </summary>
    /// <param name="connection">The open SQLite connection.</param>
    private static void InitializeSqliteVarKeyVarIdentitySchema(SqliteConnection connection)
    {
        ExecuteSqliteScalar8Scalar8NonQuery(
            connection,
            "CREATE TABLE items(k BLOB NOT NULL, i BLOB NOT NULL, PRIMARY KEY(k, i)) WITHOUT ROWID;");
    }


    /// <summary>
    /// Creates the SQLite `SV8` comparison table and index shape.<br/>
    /// The key is a sortable scalar integer and the identity is raw variable-length bytes, matching the `Scalar8VarIdentity` tuple order of key first and identity bytes second.<br/>
    /// `WITHOUT ROWID` keeps the primary-key B-tree as the table itself so the row is comparable with the other SQLite index comparisons.<br/>
    /// </summary>
    /// <param name="connection">The open SQLite connection.</param>
    private static void InitializeSqliteScalar8VarIdentitySchema(SqliteConnection connection)
    {
        ExecuteSqliteScalar8Scalar8NonQuery(
            connection,
            "CREATE TABLE items(k INTEGER NOT NULL, i BLOB NOT NULL, PRIMARY KEY(k, i)) WITHOUT ROWID;");
    }


    /// <summary>
    /// Measures routed `SV8` scalar-key range reads through an opened session.<br/>
    /// The helper folds raw identity bytes into the checksum so reads validate both count and payload preservation while reporting logical identity throughput.<br/>
    /// </summary>
    /// <param name="session">The opened LibraDex session.</param>
    /// <param name="rootOffset">The root router offset for the `SV8` index.</param>
    /// <param name="maxIdentityLength">The maximum raw identity length accepted by the index.</param>
    /// <param name="duplicateModulo">The generated scalar-key modulo.</param>
    /// <param name="lowerKey">The inclusive lower generated scalar key.</param>
    /// <param name="upperKey">The inclusive upper generated scalar key.</param>
    /// <param name="itemCount">The total generated item count.</param>
    /// <param name="iterations">The number of measured range reads.</param>
    /// <returns>The routed range-read measurement row.</returns>
    private static VarKeyScalar8ReadMeasurement MeasureScalar8VarIdentityRangeReads(
        LibraDexFileSession session,
        long rootOffset,
        int maxIdentityLength,
        int duplicateModulo,
        string keyDistribution,
        int lowerKey,
        int upperKey,
        int itemCount,
        int iterations,
        VarIdentityIterationMode iterationMode)
    {
        int expected = CountScalar8VarIdentityKeyRange(itemCount, duplicateModulo, lowerKey, upperKey);
        ulong lower = Scalar8Scalar8Layout.EncodeUnsignedScalar8(CreateScalar8VarIdentitySqliteKey(lowerKey, duplicateModulo, keyDistribution));
        ulong upper = Scalar8Scalar8Layout.EncodeUnsignedScalar8(CreateScalar8VarIdentitySqliteKey(upperKey, duplicateModulo, keyDistribution));
        long checksum = 0;
        long total = 0;
        _ = session.GetAndResetReadTelemetry();
        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            using Scalar8VarIdentityRangeReader reader = session.OpenScalar8VarIdentityRangeReader(rootOffset, maxIdentityLength, lower, upper);
            int rangeCount = 0;
            while (reader.MoveNext())
            {
                checksum += ChecksumScalar8VarIdentityCurrent(reader, iterationMode);
                rangeCount++;
            }

            if (rangeCount != expected)
            {
                throw new InvalidDataException($"SV8 range expected {expected} identities, got {rangeCount}.");
            }

            total += rangeCount;
        }

        watch.Stop();
        DataKernelReadTelemetry telemetry = session.GetAndResetReadTelemetry();
        (int cacheArenaCount, long cacheBytes) = session.GetRouterArenaReadCacheStatsForValidation();
        return CreateVarKeyScalar8ReadMeasurement(iterations, expected, total, watch.Elapsed, checksum, telemetry, cacheArenaCount, cacheBytes);
    }


    /// <summary>
    /// Measures routed `SV8` exact-key reads for one or more generated scalar keys.<br/>
    /// Each key is probed independently with equal lower/upper bounds so the measurement represents "return identities for these specific keys" instead of a contiguous BETWEEN scan.<br/>
    /// </summary>
    /// <param name="session">The opened LibraDex session.</param>
    /// <param name="rootOffset">The root router offset for the `SV8` index.</param>
    /// <param name="maxIdentityLength">The maximum raw identity length accepted by the index.</param>
    /// <param name="duplicateModulo">The generated scalar-key modulo.</param>
    /// <param name="keyDistribution">The requested key distribution.</param>
    /// <param name="firstKey">The first generated scalar key to probe.</param>
    /// <param name="keyCount">The number of generated scalar keys to probe.</param>
    /// <param name="itemCount">The total generated item count.</param>
    /// <param name="iterations">The number of measured probe passes.</param>
    /// <param name="iterationMode">The payload shape to read from each matching row.</param>
    /// <returns>The routed exact-key read measurement row.</returns>
    private static VarKeyScalar8ReadMeasurement MeasureScalar8VarIdentityExactKeyReads(
        LibraDexFileSession session,
        long rootOffset,
        int maxIdentityLength,
        int duplicateModulo,
        string keyDistribution,
        int firstKey,
        int keyCount,
        int itemCount,
        int iterations,
        VarIdentityIterationMode iterationMode)
    {
        int expected = CountScalar8VarIdentityExactKeys(itemCount, duplicateModulo, firstKey, keyCount);
        bool diagnoseMissing = Environment.GetEnvironmentVariable("LIBRADEX_DIAG_SV8_EXACT_MISSING") == "1";
        bool diagnoseRoutes = Environment.GetEnvironmentVariable("LIBRADEX_DIAG_SV8_EXACT_ROUTES") == "1";
        long checksum = 0;
        long total = 0;
        _ = session.GetAndResetReadTelemetry();
        Stopwatch watch = Stopwatch.StartNew();
        using Scalar8VarIdentityRangeReader reader = new();
        for (int i = 0; i < iterations; i++)
        {
            int iterationCount = 0;
            for (int keyIndex = 0; keyIndex < keyCount; keyIndex++)
            {
                int key = firstKey + keyIndex;
                int expectedForKey = CountScalar8VarIdentityKeyRange(itemCount, duplicateModulo, key, key);
                ulong encodedKey = Scalar8Scalar8Layout.EncodeUnsignedScalar8(CreateScalar8VarIdentitySqliteKey(key, duplicateModulo, keyDistribution));
                reader.Reset(session, rootOffset, maxIdentityLength, encodedKey, encodedKey);
                int count = 0;
                HashSet<int>? seenIndices = diagnoseMissing && i == 0 ? [] : null;
                while (reader.MoveNext())
                {
                    checksum += ChecksumScalar8VarIdentityCurrent(reader, iterationMode);
                    if (seenIndices is not null && TryParseScalar8VarIdentityIndex(reader.CurrentIdentity, out int parsedIndex))
                    {
                        seenIndices.Add(parsedIndex);
                    }

                    count++;
                }

                if (count != expectedForKey)
                {
                    string routeDiagnostic = diagnoseRoutes
                        ? Environment.NewLine + session.DescribeScalar8VarIdentityExactKeyRoutes(rootOffset, maxIdentityLength, encodedKey, maxRows: 0)
                        : string.Empty;
                    if (seenIndices is not null)
                    {
                        string missing = DescribeMissingScalar8VarIdentityIndices(itemCount, duplicateModulo, key, seenIndices, maxCount: 32);
                        throw new InvalidDataException($"SV8 exact-key read expected {expectedForKey} identities for key {key}, got {count}. Missing generated indices: {missing}.{routeDiagnostic}");
                    }

                    throw new InvalidDataException($"SV8 exact-key read expected {expectedForKey} identities for key {key}, got {count}.{routeDiagnostic}");
                }

                iterationCount += count;
            }

            if (iterationCount != expected)
            {
                throw new InvalidDataException($"SV8 exact-key pass expected {expected} identities, got {iterationCount}.");
            }

            total += iterationCount;
        }

        watch.Stop();
        DataKernelReadTelemetry telemetry = session.GetAndResetReadTelemetry();
        (int cacheArenaCount, long cacheBytes) = session.GetRouterArenaReadCacheStatsForValidation();
        return CreateVarKeyScalar8ReadMeasurement(iterations, expected, total, watch.Elapsed, checksum, telemetry, cacheArenaCount, cacheBytes);
    }


    /// <summary>
    /// Parses the generated source index from an `SV8` variable-identity payload produced by <see cref="CreateScalar8VarIdentity(int, int, bool)"/>.<br/>
    /// The diagnostic parser recognizes the ASCII `item/XXXXXXXX/` prefix used by variable-length comparison workloads and intentionally ignores fixed-length payloads.<br/>
    /// </summary>
    /// <param name="identity">The raw identity bytes returned by the reader.<br/></param>
    /// <param name="index">Receives the parsed generated source index when the identity has the expected prefix.<br/></param>
    /// <returns><see langword="true"/> when the source index was parsed.</returns>
    private static bool TryParseScalar8VarIdentityIndex(ReadOnlySpan<byte> identity, out int index)
    {
        index = 0;
        if (identity.Length < 14 ||
            identity[0] != (byte)'i' ||
            identity[1] != (byte)'t' ||
            identity[2] != (byte)'e' ||
            identity[3] != (byte)'m' ||
            identity[4] != (byte)'/')
        {
            return false;
        }

        int value = 0;
        for (int i = 5; i < 13; i++)
        {
            byte b = identity[i];
            int nibble;
            if (b >= (byte)'0' && b <= (byte)'9')
            {
                nibble = b - (byte)'0';
            }
            else if (b >= (byte)'A' && b <= (byte)'F')
            {
                nibble = b - (byte)'A' + 10;
            }
            else
            {
                return false;
            }

            value = (value << 4) | nibble;
        }

        if (identity[13] != (byte)'/')
        {
            return false;
        }

        index = value;
        return true;
    }


    /// <summary>
    /// Builds a compact missing-index list for an exact-key diagnostic failure.<br/>
    /// </summary>
    /// <param name="itemCount">The total generated item count.<br/></param>
    /// <param name="duplicateModulo">The scalar-key modulo used by the generator.<br/></param>
    /// <param name="key">The generated scalar key being diagnosed.<br/></param>
    /// <param name="seenIndices">The generated source indices returned by the reader.<br/></param>
    /// <param name="maxCount">The maximum number of missing indices to include in the string.<br/></param>
    /// <returns>A comma-separated missing-index list with an overflow marker when needed.<br/></returns>
    private static string DescribeMissingScalar8VarIdentityIndices(int itemCount, int duplicateModulo, int key, HashSet<int> seenIndices, int maxCount)
    {
        StringBuilder builder = new();
        int emitted = 0;
        int missing = 0;
        for (int i = key; i < itemCount; i += duplicateModulo)
        {
            if (seenIndices.Contains(i))
            {
                continue;
            }

            missing++;
            if (emitted < maxCount)
            {
                if (emitted != 0)
                {
                    builder.Append(", ");
                }

                builder.Append(i.ToString(CultureInfo.InvariantCulture));
                emitted++;
            }
        }

        if (missing > emitted)
        {
            if (builder.Length != 0)
            {
                builder.Append(", ");
            }

            builder.Append('+');
            builder.Append((missing - emitted).ToString(CultureInfo.InvariantCulture));
            builder.Append(" more");
        }

        return builder.Length == 0 ? "none parsed" : builder.ToString();
    }


    /// <summary>
    /// Measures routed `SV8` rotating range reads across a fixed set of generated scalar windows.<br/>
    /// Each measured pass reads multiple same-width contiguous ranges so the row represents sustained range-query throughput instead of one repeatedly hot lower/upper window.<br/>
    /// </summary>
    /// <param name="session">The opened LibraDex session.</param>
    /// <param name="rootOffset">The root router offset for the `SV8` index.</param>
    /// <param name="maxIdentityLength">The maximum raw identity length accepted by the index.</param>
    /// <param name="duplicateModulo">The generated scalar-key modulo.</param>
    /// <param name="keyDistribution">The requested key distribution.</param>
    /// <param name="rangeKeyCount">The number of adjacent generated scalar keys per window.</param>
    /// <param name="windowCount">The number of windows to read per measured pass.</param>
    /// <param name="windowStep">The generated-key step between window starts.</param>
    /// <param name="itemCount">The total generated item count.</param>
    /// <param name="iterations">The number of measured read passes.</param>
    /// <param name="iterationMode">The payload shape to read from each matching row.</param>
    /// <returns>The routed rotating-range read measurement row.</returns>
    private static VarKeyScalar8ReadMeasurement MeasureScalar8VarIdentityRotatingRangeReads(
        LibraDexFileSession session,
        long rootOffset,
        int maxIdentityLength,
        int duplicateModulo,
        string keyDistribution,
        int rangeKeyCount,
        int windowCount,
        int windowStep,
        int itemCount,
        int iterations,
        VarIdentityIterationMode iterationMode)
    {
        int expected = CountScalar8VarIdentityRotatingRanges(itemCount, duplicateModulo, rangeKeyCount, windowCount, windowStep);
        int maxStart = duplicateModulo - rangeKeyCount;
        int startModulo = maxStart + 1;
        long checksum = 0;
        long total = 0;
        _ = session.GetAndResetReadTelemetry();
        Stopwatch watch = Stopwatch.StartNew();
        using Scalar8VarIdentityRangeReader reader = new();
        for (int i = 0; i < iterations; i++)
        {
            int iterationCount = 0;
            for (int windowIndex = 0; windowIndex < windowCount; windowIndex++)
            {
                int lowerKey = checked((windowIndex * windowStep) % startModulo);
                int upperKey = lowerKey + rangeKeyCount - 1;
                int expectedForWindow = CountScalar8VarIdentityKeyRange(itemCount, duplicateModulo, lowerKey, upperKey);
                ulong lower = Scalar8Scalar8Layout.EncodeUnsignedScalar8(CreateScalar8VarIdentitySqliteKey(lowerKey, duplicateModulo, keyDistribution));
                ulong upper = Scalar8Scalar8Layout.EncodeUnsignedScalar8(CreateScalar8VarIdentitySqliteKey(upperKey, duplicateModulo, keyDistribution));
                reader.Reset(session, rootOffset, maxIdentityLength, lower, upper);
                int count = 0;
                while (reader.MoveNext())
                {
                    checksum += ChecksumScalar8VarIdentityCurrent(reader, iterationMode);
                    count++;
                }

                if (count != expectedForWindow)
                {
                    throw new InvalidDataException($"SV8 rotating range expected {expectedForWindow} identities for keys {lowerKey}-{upperKey}, got {count}.");
                }

                iterationCount += count;
            }

            if (iterationCount != expected)
            {
                throw new InvalidDataException($"SV8 rotating range pass expected {expected} identities, got {iterationCount}.");
            }

            total += iterationCount;
        }

        watch.Stop();
        DataKernelReadTelemetry telemetry = session.GetAndResetReadTelemetry();
        (int cacheArenaCount, long cacheBytes) = session.GetRouterArenaReadCacheStatsForValidation();
        return CreateVarKeyScalar8ReadMeasurement(iterations, expected, total, watch.Elapsed, checksum, telemetry, cacheArenaCount, cacheBytes);
    }


    /// <summary>
    /// Adds the current `SV8` reader row payload requested by the iteration mode to the comparison checksum.<br/>
    /// The helper avoids touching identity bytes during key-only measurements and avoids touching key fields during identity-only measurements.<br/>
    /// </summary>
    /// <param name="reader">The positioned `SV8` range reader.</param>
    /// <param name="iterationMode">The comparison iteration payload mode.</param>
    /// <returns>The checksum contribution for the current row.</returns>
    private static long ChecksumScalar8VarIdentityCurrent(Scalar8VarIdentityRangeReader reader, VarIdentityIterationMode iterationMode)
    {
        return iterationMode switch
        {
            VarIdentityIterationMode.Keys => ChecksumScalar8(reader.CurrentEncodedKey),
            VarIdentityIterationMode.Tuples => ChecksumScalar8(reader.CurrentEncodedKey) + ChecksumBytes(reader.CurrentIdentity),
            _ => ChecksumBytes(reader.CurrentIdentity)
        };
    }


    /// <summary>
    /// Adds the current SQLite `SV8` row payload requested by the iteration mode to the comparison checksum.<br/>
    /// The selected column positions match <see cref="CreateSqliteVarIdentityRangeReadCommandText(VarIdentityIterationMode)"/>.<br/>
    /// </summary>
    /// <param name="reader">The positioned SQLite data reader.</param>
    /// <param name="iterationMode">The comparison iteration payload mode.</param>
    /// <returns>The checksum contribution for the current row.</returns>
    private static long ChecksumSqliteScalar8VarIdentityCurrent(SqliteDataReader reader, VarIdentityIterationMode iterationMode)
    {
        return iterationMode switch
        {
            VarIdentityIterationMode.Keys => ChecksumScalar8(unchecked((ulong)reader.GetInt64(0))),
            VarIdentityIterationMode.Tuples => ChecksumScalar8(unchecked((ulong)reader.GetInt64(0))) + ChecksumBytes((byte[])reader.GetValue(1)),
            _ => ChecksumBytes((byte[])reader.GetValue(0))
        };
    }


    private readonly record struct VarKeyScalar8ReadMeasurement(
        int Iterations,
        int RangeIdentityCount,
        double IdentitiesPerSecond,
        double NsPerIdentity,
        long Checksum,
        DataKernelReadTelemetry ReadTelemetry,
        int RouterArenaCacheCount,
        long RouterArenaCacheBytes);


    private readonly record struct VarKeyScalar8RouterShapeStats(
        int RouterCount,
        int DirectRouterCount,
        int MultiByteRouterCount,
        int ShelfCount,
        int MaxRouteDepth,
        double AverageRouteDepth);


    private readonly record struct VarKeyScalar8HierarchicalBulkBuildResult(
        int FanoutDepth,
        int RouteCount,
        int RouterCount,
        int ShelfCount,
        int MaxPlannerDepth);


    private readonly record struct VarKeyScalar16HierarchicalBulkBuildResult(
        int FanoutDepth,
        int RouteCount,
        int RouterCount,
        int ShelfCount,
        int MaxPlannerDepth);


    /// <summary>
    /// Aggregates cheap structural facts emitted by walked `VS8` inserts for cold-route optimization analysis.<br/>
    /// The harness uses this as scaffolding only: it proves whether normal walked writes expose enough signal to identify deeper-than-needed route structures before any optimizer rewrite policy is added.<br/>
    /// </summary>
    private struct VarKeyScalar8WalkedColdTelemetry
    {
        public long InsertCount;
        public long NoSplitCount;
        public long GrowCount;
        public long TransformCount;
        public long TargetRouterDepthTotal;
        public int MaxTargetRouterDepth;
        public long TransformRouterDepthTotal;
        public int MaxTransformRouterDepth;
        public long TransformShelfItemTotal;
        public int MaxTransformShelfItems;

        /// <summary>
        /// Gets the average router key depth reached by walked inserts before shelf mutation.<br/>
        /// This is a byte-depth signal, not a wall-clock timing value, and is intended to reveal long shared-prefix routing pressure.<br/>
        /// </summary>
        public readonly double AverageTargetRouterDepth => InsertCount == 0 ? 0 : TargetRouterDepthTotal / (double)InsertCount;

        /// <summary>
        /// Gets the average router key depth selected when a full shelf transformed into a child router.<br/>
        /// A high value relative to the bulk-planned route shape indicates that normal mutation is discovering useful fanout too late.<br/>
        /// </summary>
        public readonly double AverageTransformRouterDepth => TransformCount == 0 ? 0 : TransformRouterDepthTotal / (double)TransformCount;

        /// <summary>
        /// Gets the average number of shelf items present when a transform split occurred.<br/>
        /// This helps distinguish depth pressure from small accidental splits by showing whether transforms happen at full, high-density shelves.<br/>
        /// </summary>
        public readonly double AverageTransformShelfItems => TransformCount == 0 ? 0 : TransformShelfItemTotal / (double)TransformCount;

        /// <summary>
        /// Adds one walked `VS8` insert result to the cold telemetry totals.<br/>
        /// The method records only scalar counters already produced by the insert path, so the harness can evaluate optimizer candidates without extra key scans in the write loop.<br/>
        /// </summary>
        /// <param name="result">The walked insert result to aggregate.</param>
        public void Add(VarKeyScalar8RoutedInsertResult result)
        {
            InsertCount++;
            TargetRouterDepthTotal += result.TargetRouterDepth;
            MaxTargetRouterDepth = Math.Max(MaxTargetRouterDepth, result.TargetRouterDepth);
            switch (result.Kind)
            {
                case VarKeyScalar8RoutedInsertKind.WalkedNoSplit:
                    NoSplitCount++;
                    break;
                case VarKeyScalar8RoutedInsertKind.WalkedGrow:
                    GrowCount++;
                    break;
                case VarKeyScalar8RoutedInsertKind.WalkedShelfTransformSplit:
                    TransformCount++;
                    TransformRouterDepthTotal += result.StructuralRouterDepth;
                    MaxTransformRouterDepth = Math.Max(MaxTransformRouterDepth, result.StructuralRouterDepth);
                    TransformShelfItemTotal += result.TargetShelfItemCount;
                    MaxTransformShelfItems = Math.Max(MaxTransformShelfItems, result.TargetShelfItemCount);
                    break;
            }
        }
    }


    /// <summary>
    /// Tracks when a cheap depth-plus-shelf-size heuristic would enqueue a cold `VS8` MB-router optimization candidate.<br/>
    /// This is intentionally a harness-only study helper: it uses facts already emitted by walked inserts and a compact prefix key derived from the inserted raw key.<br/>
    /// The goal is to measure whether low item-count thresholds would identify hierarchical route pressure early enough to be useful before implementing an optimizer queue.<br/>
    /// </summary>
    private sealed class VarKeyScalar8ColdCandidateTelemetry
    {
        private readonly int depthThreshold;
        private readonly int shelfItemThreshold;
        private readonly int hitThreshold;
        private readonly Dictionary<string, int> hitsByPrefix = [];
        private readonly HashSet<string> triggeredPrefixes = [];

        public VarKeyScalar8ColdCandidateTelemetry(int depthThreshold, int shelfItemThreshold, int hitThreshold)
        {
            this.depthThreshold = depthThreshold;
            this.shelfItemThreshold = shelfItemThreshold;
            this.hitThreshold = hitThreshold;
        }

        public int ObservationCount { get; private set; }

        public int DistinctPrefixCount => hitsByPrefix.Count;

        public int FirstObservationAt { get; private set; }

        public int FirstTriggerAt { get; private set; }

        public int MaxHits { get; private set; }

        public string FirstTriggeredPrefix { get; private set; } = "";

        /// <summary>
        /// Adds one walked insert result to the candidate study.<br/>
        /// Candidate observations require route depth and landing shelf size to cross the configured thresholds; repeated hits are grouped by the bytes consumed up to the observed target depth.<br/>
        /// </summary>
        /// <param name="insertOrdinal">The one-based insert ordinal.</param>
        /// <param name="key">The raw key used by the insert.</param>
        /// <param name="result">The walked insert result that carries route-depth and shelf-size facts.</param>
        public bool Add(int insertOrdinal, byte[] key, VarKeyScalar8RoutedInsertResult result)
        {
            if (result.TargetRouterDepth < depthThreshold ||
                result.TargetShelfItemCount < shelfItemThreshold)
            {
                return false;
            }

            ObservationCount++;
            if (FirstObservationAt == 0)
            {
                FirstObservationAt = insertOrdinal;
            }

            string prefix = CreatePrefixKey(key, result.TargetRouterDepth);
            int hits = hitsByPrefix.TryGetValue(prefix, out int existingHits)
                ? existingHits + 1
                : 1;
            hitsByPrefix[prefix] = hits;
            MaxHits = Math.Max(MaxHits, hits);
            if (FirstTriggerAt == 0 && hits >= hitThreshold)
            {
                FirstTriggerAt = insertOrdinal;
                FirstTriggeredPrefix = prefix;
            }

            if (hits >= hitThreshold && triggeredPrefixes.Add(prefix))
            {
                return true;
            }

            return false;
        }

        private static string CreatePrefixKey(byte[] key, int depth)
        {
            int length = Math.Min(Math.Max(depth, 0), key.Length);
            StringBuilder builder = new(length * 2);
            for (int i = 0; i < length; i++)
            {
                builder.Append(key[i].ToString("X2", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }
    }


    /// <summary>
    /// Aggregates cheap structural facts emitted by walked `VS16` inserts for cold-route optimization analysis.<br/>
    /// This mirrors the `VS8` telemetry path so the widened identity shape can be compared without changing the routing workload.<br/>
    /// </summary>
    private struct VarKeyScalar16WalkedColdTelemetry
    {
        public long InsertCount;
        public long NoSplitCount;
        public long GrowCount;
        public long TransformCount;
        public long TargetRouterDepthTotal;
        public int MaxTargetRouterDepth;
        public long TransformRouterDepthTotal;
        public int MaxTransformRouterDepth;
        public long TransformShelfItemTotal;
        public int MaxTransformShelfItems;

        public readonly double AverageTargetRouterDepth => InsertCount == 0 ? 0 : TargetRouterDepthTotal / (double)InsertCount;

        public readonly double AverageTransformRouterDepth => TransformCount == 0 ? 0 : TransformRouterDepthTotal / (double)TransformCount;

        public readonly double AverageTransformShelfItems => TransformCount == 0 ? 0 : TransformShelfItemTotal / (double)TransformCount;

        /// <summary>
        /// Adds one walked `VS16` insert result to the cold telemetry totals.<br/>
        /// The method records only scalar counters already produced by the insert path, so the harness can evaluate optimizer candidates without extra key scans in the write loop.<br/>
        /// </summary>
        /// <param name="result">The walked insert result to aggregate.</param>
        public void Add(VarKeyScalar16RoutedInsertResult result)
        {
            InsertCount++;
            TargetRouterDepthTotal += result.TargetRouterDepth;
            MaxTargetRouterDepth = Math.Max(MaxTargetRouterDepth, result.TargetRouterDepth);
            switch (result.Kind)
            {
                case VarKeyScalar16RoutedInsertKind.WalkedNoSplit:
                    NoSplitCount++;
                    break;
                case VarKeyScalar16RoutedInsertKind.WalkedGrow:
                    GrowCount++;
                    break;
                case VarKeyScalar16RoutedInsertKind.WalkedShelfTransformSplit:
                    TransformCount++;
                    TransformRouterDepthTotal += result.StructuralRouterDepth;
                    MaxTransformRouterDepth = Math.Max(MaxTransformRouterDepth, result.StructuralRouterDepth);
                    TransformShelfItemTotal += result.TargetShelfItemCount;
                    MaxTransformShelfItems = Math.Max(MaxTransformShelfItems, result.TargetShelfItemCount);
                    break;
            }
        }
    }


    /// <summary>
    /// Tracks when a cheap depth-plus-shelf-size heuristic would enqueue a cold `VS16` MB-router optimization candidate.<br/>
    /// It intentionally follows the same prefix grouping policy as `VS8` so the only comparison difference is identity width.<br/>
    /// </summary>
    private sealed class VarKeyScalar16ColdCandidateTelemetry
    {
        private readonly int depthThreshold;
        private readonly int shelfItemThreshold;
        private readonly int hitThreshold;
        private readonly Dictionary<string, int> hitsByPrefix = [];
        private readonly HashSet<string> triggeredPrefixes = [];

        public VarKeyScalar16ColdCandidateTelemetry(int depthThreshold, int shelfItemThreshold, int hitThreshold)
        {
            this.depthThreshold = depthThreshold;
            this.shelfItemThreshold = shelfItemThreshold;
            this.hitThreshold = hitThreshold;
        }

        public int ObservationCount { get; private set; }

        public int DistinctPrefixCount => hitsByPrefix.Count;

        public int FirstObservationAt { get; private set; }

        public int FirstTriggerAt { get; private set; }

        public int MaxHits { get; private set; }

        public string FirstTriggeredPrefix { get; private set; } = "";

        /// <summary>
        /// Adds one walked insert result to the candidate study.<br/>
        /// Candidate observations require route depth and landing shelf size to cross the configured thresholds; repeated hits are grouped by the bytes consumed up to the observed target depth.<br/>
        /// </summary>
        /// <param name="insertOrdinal">The one-based insert ordinal.</param>
        /// <param name="key">The raw key used by the insert.</param>
        /// <param name="result">The walked insert result that carries route-depth and shelf-size facts.</param>
        /// <returns>True when this observation crosses the hit threshold for a not-yet-triggered prefix.</returns>
        public bool Add(int insertOrdinal, byte[] key, VarKeyScalar16RoutedInsertResult result)
        {
            if (result.TargetRouterDepth < depthThreshold ||
                result.TargetShelfItemCount < shelfItemThreshold)
            {
                return false;
            }

            ObservationCount++;
            if (FirstObservationAt == 0)
            {
                FirstObservationAt = insertOrdinal;
            }

            string prefix = CreatePrefixKey(key, result.TargetRouterDepth);
            int hits = hitsByPrefix.TryGetValue(prefix, out int existingHits)
                ? existingHits + 1
                : 1;
            hitsByPrefix[prefix] = hits;
            MaxHits = Math.Max(MaxHits, hits);
            if (FirstTriggerAt == 0 && hits >= hitThreshold)
            {
                FirstTriggerAt = insertOrdinal;
                FirstTriggeredPrefix = prefix;
            }

            if (hits >= hitThreshold && triggeredPrefixes.Add(prefix))
            {
                return true;
            }

            return false;
        }

        private static string CreatePrefixKey(byte[] key, int depth)
        {
            int length = Math.Min(Math.Max(depth, 0), key.Length);
            StringBuilder builder = new(length * 2);
            for (int i = 0; i < length; i++)
            {
                builder.Append(key[i].ToString("X2", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }
    }


    private readonly record struct VarLenOptimizerModeShapeResult(
        int FirstEnqueueAt,
        int CandidateCount,
        VarLenOptimizerMaintenanceResult Maintenance,
        VarKeyScalar8RouterShapeStats RouterStats,
        long Checksum);


    private struct VarKeyScalar8HierarchicalBulkBuildState
    {
        public int FirstFanoutDepth;
        public int RouteCount;
        public int RouterCount;
        public int ShelfCount;
        public int MaxPlannerDepth;
    }


    private struct VarKeyScalar16HierarchicalBulkBuildState
    {
        public int FirstFanoutDepth;
        public int RouteCount;
        public int RouterCount;
        public int ShelfCount;
        public int MaxPlannerDepth;
    }


    private struct VarKeyScalar8RouterShapeStatsBuilder
    {
        public int RouterCount;
        public int DirectRouterCount;
        public int MultiByteRouterCount;
        public int ShelfCount;
        public int MaxRouteDepth;
        public long LeafDepthTotal;

        public readonly VarKeyScalar8RouterShapeStats ToStats()
        {
            return new VarKeyScalar8RouterShapeStats(
                RouterCount,
                DirectRouterCount,
                MultiByteRouterCount,
                ShelfCount,
                MaxRouteDepth,
                ShelfCount == 0 ? 0 : LeafDepthTotal / (double)ShelfCount);
        }
    }


    private readonly record struct VarKeyScalar8ComparisonShelf(
        int Prefix,
        VarKeyScalar8Profile Profile,
        VarKeyScalar8ReadOnly ReadOnly);


    /// <summary>
    /// Validates automatic `VS8` optimizer maintenance using the runtime handle lifecycle rather than loose root/policy arguments.<br/>
    /// The handle owns root offset, key limit, optimizer fanout, and policy; the session resolves optimizer state from that handle and only drains at the policy-approved between-action boundary.<br/>
    /// </summary>
    private static VarLenOptimizerModeShapeResult ValidateVarKeyScalar8OptimizerLifecycleAutomaticMode(
        string path,
        DataKernelOptions options,
        byte[][] keys,
        int maxKeyLength,
        int branchCount,
        VarLenOptimizerMaintenancePolicy policy,
        byte[] lower,
        byte[] upper)
    {
        VarKeyScalar8IndexHandle handle;
        VarLenRouteOptimizerState optimizerState;
        VarLenOptimizerMaintenanceResult maintenance = default;
        using (LibraDexFileSession session = LibraDexFileSession.Initialize(path, options, CreateDesignPerfMetadata(827), DataKernelTelemetryOptions.EnabledOptions))
        {
            (handle, _, _) = session.CreateVarKeyScalar8RootRouterIndex(CreateHarnessSlot(0, "vs8life", 0), maxKeyLength, branchCount, policy);
            optimizerState = session.GetOrCreateVarLenRouteOptimizerState(handle);
            _ = session.CreateVarKeyScalar8ShelfAndLinkRootRoute(handle.RootRouterOffset, keys[0][0], VarKeyScalar8Profile.DefaultInitial);
            LibraDexFileSessionDurabilityBatch batch = session.BeginDurabilityBatch();
            try
            {
                for (int i = 0; i < keys.Length; i++)
                {
                    VarKeyScalar8RoutedInsertResult result = session.InsertWalkedRoutedVarKeyScalar8(handle.RootRouterOffset, handle.MaxKeyLength, keys[i], CreateVarKeyScalar8Identity(i), allowDuplicateKeys: true, maxRouterHops: 16);
                    if (result.InsertResult != VarKeyScalar8InsertResult.Inserted)
                    {
                        throw new InvalidDataException($"Expected lifecycle optimizer VS8 insert {i}, got {result.Kind}/{result.InsertResult}.");
                    }

                    bool queued = session.ObserveVarKeyScalar8OptimizerWrite(handle, i + 1, keys[i], result);
                    if (queued)
                    {
                        _ = batch.Commit();
                        batch.Dispose();
                        VarLenOptimizerMaintenanceResult pendingMaintenance = session.OptimizeVarKeyScalar8BetweenHotActions(handle, queued, maintenance.PublishedCount, keys.AsSpan(0, i + 1));
                        if (pendingMaintenance.AttemptedCount > 0 || pendingMaintenance.PublishedCount > 0)
                        {
                            maintenance = pendingMaintenance;
                        }

                        batch = session.BeginDurabilityBatch();
                    }
                }

                _ = batch.Commit();
            }
            finally
            {
                batch.Dispose();
            }
        }

        return ReadVarKeyScalar8OptimizerModeResult(path, options, handle.RootRouterOffset, handle.MaxKeyLength, lower, upper, keys.Length, optimizerState.FirstEnqueueAt, optimizerState.CandidateCount, maintenance);
    }


    /// <summary>
    /// Validates automatic `VS16` optimizer maintenance using the runtime handle lifecycle rather than loose root/policy arguments.<br/>
    /// This mirrors the `VS8` lifecycle proof while preserving the wider identity write path.<br/>
    /// </summary>
    private static VarLenOptimizerModeShapeResult ValidateVarKeyScalar16OptimizerLifecycleAutomaticMode(
        string path,
        DataKernelOptions options,
        byte[][] keys,
        int maxKeyLength,
        int branchCount,
        VarLenOptimizerMaintenancePolicy policy,
        byte[] lower,
        byte[] upper)
    {
        VarKeyScalar16IndexHandle handle;
        VarLenRouteOptimizerState optimizerState;
        VarLenOptimizerMaintenanceResult maintenance = default;
        using (LibraDexFileSession session = LibraDexFileSession.Initialize(path, options, CreateDesignPerfMetadata(828), DataKernelTelemetryOptions.EnabledOptions))
        {
            (handle, _, _) = session.CreateVarKeyScalar16RootRouterIndex(CreateHarnessSlot(0, "vs16life", 0), maxKeyLength, branchCount, policy);
            optimizerState = session.GetOrCreateVarLenRouteOptimizerState(handle);
            _ = session.CreateVarKeyScalar16ShelfAndLinkRootRoute(handle.RootRouterOffset, keys[0][0], VarKeyScalar16Profile.DefaultInitial);
            LibraDexFileSessionDurabilityBatch batch = session.BeginDurabilityBatch();
            try
            {
                for (int i = 0; i < keys.Length; i++)
                {
                    CreateVarKeyScalar16Identity(i, out ulong identityHigh, out ulong identityLow);
                    VarKeyScalar16RoutedInsertResult result = session.InsertWalkedRoutedVarKeyScalar16(handle.RootRouterOffset, handle.MaxKeyLength, keys[i], identityHigh, identityLow, allowDuplicateKeys: true, maxRouterHops: 16);
                    if (result.InsertResult != VarKeyScalar16InsertResult.Inserted)
                    {
                        throw new InvalidDataException($"Expected lifecycle optimizer VS16 insert {i}, got {result.Kind}/{result.InsertResult}.");
                    }

                    bool queued = session.ObserveVarKeyScalar16OptimizerWrite(handle, i + 1, keys[i], result);
                    if (queued)
                    {
                        _ = batch.Commit();
                        batch.Dispose();
                        VarLenOptimizerMaintenanceResult pendingMaintenance = session.OptimizeVarKeyScalar16BetweenHotActions(handle, queued, maintenance.PublishedCount, keys.AsSpan(0, i + 1));
                        if (pendingMaintenance.AttemptedCount > 0 || pendingMaintenance.PublishedCount > 0)
                        {
                            maintenance = pendingMaintenance;
                        }

                        batch = session.BeginDurabilityBatch();
                    }
                }

                _ = batch.Commit();
            }
            finally
            {
                batch.Dispose();
            }
        }

        return ReadVarKeyScalar16OptimizerModeResult(path, options, handle.RootRouterOffset, handle.MaxKeyLength, lower, upper, keys.Length, optimizerState.FirstEnqueueAt, optimizerState.CandidateCount, maintenance);
    }


    /// <summary>
    /// Validates the explicit on-demand optimizer mode for `VS8`.<br/>
    /// The write path records queue evidence during normal walked inserts, then maintenance is invoked after commit by a direct caller action.<br/>
    /// This models a future developer-facing "optimize now" operation without promoting the API in this slice.<br/>
    /// </summary>
    /// <param name="path">The target index file path.</param>
    /// <param name="options">The DataKernel options used for the file session.</param>
    /// <param name="keys">The deterministic key set to insert and optimize.</param>
    /// <param name="maxKeyLength">The maximum key length for the `VS8` profile.</param>
    /// <param name="branchCount">The requested compressed-route fanout cap.</param>
    /// <param name="policy">The optimizer policy used for observation thresholds and on-demand scheduling.</param>
    /// <param name="lower">The inclusive lower range key used for validation reads.</param>
    /// <param name="upper">The inclusive upper range key used for validation reads.</param>
    /// <returns>The optimizer mode validation result.</returns>
    private static VarLenOptimizerModeShapeResult ValidateVarKeyScalar8OptimizerOnDemandMode(
        string path,
        DataKernelOptions options,
        byte[][] keys,
        int maxKeyLength,
        int branchCount,
        VarLenOptimizerMaintenancePolicy policy,
        byte[] lower,
        byte[] upper)
    {
        VarKeyScalar8IndexHandle handle;
        VarLenOptimizerMaintenanceResult maintenance;
        VarLenRouteOptimizerState optimizerState;
        using (LibraDexFileSession session = LibraDexFileSession.Initialize(path, options, CreateDesignPerfMetadata(823), DataKernelTelemetryOptions.EnabledOptions))
        {
            (handle, _, _) = session.CreateVarKeyScalar8RootRouterIndex(CreateHarnessSlot(0, "vs8optd", 0), maxKeyLength, branchCount, policy);
            optimizerState = session.GetOrCreateVarLenRouteOptimizerState(handle);
            _ = session.CreateVarKeyScalar8ShelfAndLinkRootRoute(handle.RootRouterOffset, keys[0][0], VarKeyScalar8Profile.DefaultInitial);
            using (LibraDexFileSessionDurabilityBatch batch = session.BeginDurabilityBatch())
            {
                for (int i = 0; i < keys.Length; i++)
                {
                    VarKeyScalar8RoutedInsertResult result = session.InsertWalkedRoutedVarKeyScalar8(handle.RootRouterOffset, handle.MaxKeyLength, keys[i], CreateVarKeyScalar8Identity(i), allowDuplicateKeys: true, maxRouterHops: 16);
                    if (result.InsertResult != VarKeyScalar8InsertResult.Inserted)
                    {
                        throw new InvalidDataException($"Expected on-demand optimizer VS8 insert {i}, got {result.Kind}/{result.InsertResult}.");
                    }

                    _ = session.ObserveVarKeyScalar8OptimizerWrite(handle, i + 1, keys[i], result);
                }

                _ = batch.Commit();
            }

            maintenance = session.OptimizeVarKeyScalar8OnDemand(handle, keys);
        }

        return ReadVarKeyScalar8OptimizerModeResult(path, options, handle.RootRouterOffset, handle.MaxKeyLength, lower, upper, keys.Length, optimizerState.FirstEnqueueAt, optimizerState.CandidateCount, maintenance);
    }


    /// <summary>
    /// Validates the between-batch automatic optimizer mode for `VS8`.<br/>
    /// The write loop commits the active hot batch as soon as the queue first emits a candidate, runs one bounded maintenance pass, then resumes normal walked inserts in a fresh batch.<br/>
    /// This models automatic idle/between-action optimization without requiring a background thread in the harness.<br/>
    /// </summary>
    /// <param name="path">The target index file path.</param>
    /// <param name="options">The DataKernel options used for the file session.</param>
    /// <param name="keys">The deterministic key set to insert and optimize.</param>
    /// <param name="maxKeyLength">The maximum key length for the `VS8` profile.</param>
    /// <param name="branchCount">The requested compressed-route fanout cap.</param>
    /// <param name="policy">The optimizer policy used for observation thresholds and automatic between-action scheduling.</param>
    /// <param name="lower">The inclusive lower range key used for validation reads.</param>
    /// <param name="upper">The inclusive upper range key used for validation reads.</param>
    /// <returns>The optimizer mode validation result.</returns>
    private static VarLenOptimizerModeShapeResult ValidateVarKeyScalar8OptimizerAutomaticMode(
        string path,
        DataKernelOptions options,
        byte[][] keys,
        int maxKeyLength,
        int branchCount,
        VarLenOptimizerMaintenancePolicy policy,
        byte[] lower,
        byte[] upper)
    {
        VarKeyScalar8IndexHandle handle;
        VarLenRouteOptimizerState optimizerState;
        VarLenOptimizerMaintenanceResult maintenance = default;
        using (LibraDexFileSession session = LibraDexFileSession.Initialize(path, options, CreateDesignPerfMetadata(824), DataKernelTelemetryOptions.EnabledOptions))
        {
            (handle, _, _) = session.CreateVarKeyScalar8RootRouterIndex(CreateHarnessSlot(0, "vs8opta", 0), maxKeyLength, branchCount, policy);
            optimizerState = session.GetOrCreateVarLenRouteOptimizerState(handle);
            _ = session.CreateVarKeyScalar8ShelfAndLinkRootRoute(handle.RootRouterOffset, keys[0][0], VarKeyScalar8Profile.DefaultInitial);
            LibraDexFileSessionDurabilityBatch batch = session.BeginDurabilityBatch();
            try
            {
                for (int i = 0; i < keys.Length; i++)
                {
                    VarKeyScalar8RoutedInsertResult result = session.InsertWalkedRoutedVarKeyScalar8(handle.RootRouterOffset, handle.MaxKeyLength, keys[i], CreateVarKeyScalar8Identity(i), allowDuplicateKeys: true, maxRouterHops: 16);
                    if (result.InsertResult != VarKeyScalar8InsertResult.Inserted)
                    {
                        throw new InvalidDataException($"Expected automatic optimizer VS8 insert {i}, got {result.Kind}/{result.InsertResult}.");
                    }

                    bool queued = session.ObserveVarKeyScalar8OptimizerWrite(handle, i + 1, keys[i], result);
                    if (queued)
                    {
                        _ = batch.Commit();
                        batch.Dispose();
                        VarLenOptimizerMaintenanceResult pendingMaintenance = session.OptimizeVarKeyScalar8BetweenHotActions(handle, queued, maintenance.PublishedCount, keys.AsSpan(0, i + 1));
                        if (pendingMaintenance.AttemptedCount > 0 || pendingMaintenance.PublishedCount > 0)
                        {
                            maintenance = pendingMaintenance;
                        }

                        batch = session.BeginDurabilityBatch();
                    }
                }

                _ = batch.Commit();
            }
            finally
            {
                batch.Dispose();
            }

            if (maintenance.PublishedCount == 0)
            {
                throw new InvalidDataException("The automatic VS8 optimizer mode did not publish a queued candidate.");
            }

        }

        return ReadVarKeyScalar8OptimizerModeResult(path, options, handle.RootRouterOffset, handle.MaxKeyLength, lower, upper, keys.Length, optimizerState.FirstEnqueueAt, optimizerState.CandidateCount, maintenance);
    }


    /// <summary>
    /// Reads and validates a completed `VS8` optimizer-mode file.<br/>
    /// The method confirms the range result count and that the optimized route graph now contains a compressed router with shallow leaf depth.<br/>
    /// </summary>
    /// <param name="path">The target index file path.</param>
    /// <param name="options">The DataKernel options used for the file session.</param>
    /// <param name="rootOffset">The root router offset to validate.</param>
    /// <param name="maxKeyLength">The maximum key length for the `VS8` profile.</param>
    /// <param name="lower">The inclusive lower range key used for validation reads.</param>
    /// <param name="upper">The inclusive upper range key used for validation reads.</param>
    /// <param name="expectedCount">The expected identity count.</param>
    /// <param name="firstEnqueueAt">The first queued optimizer candidate ordinal.</param>
    /// <param name="maintenance">The maintenance result to include in the returned row.</param>
    /// <returns>The optimizer mode validation result.</returns>
    private static VarLenOptimizerModeShapeResult ReadVarKeyScalar8OptimizerModeResult(
        string path,
        DataKernelOptions options,
        long rootOffset,
        int maxKeyLength,
        byte[] lower,
        byte[] upper,
        int expectedCount,
        int firstEnqueueAt,
        int candidateCount,
        VarLenOptimizerMaintenanceResult maintenance)
    {
        if (maintenance.PublishedCount != 1)
        {
            throw new InvalidDataException("The VS8 optimizer mode expected exactly one published maintenance candidate.");
        }

        using LibraDexFileSession reopened = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions);
        VarKeyScalar8RouterShapeStats stats = MeasureVarKeyScalar8RouterShapeStats(reopened, rootOffset);
        if (stats.MultiByteRouterCount <= 0 || stats.MaxRouteDepth > 2)
        {
            throw new InvalidDataException($"The VS8 optimizer mode produced an unexpected route shape: mbRouters={stats.MultiByteRouterCount}, maxDepth={stats.MaxRouteDepth}.");
        }

        ulong[] identities = new ulong[expectedCount];
        int copied = reopened.ReadVarKeyScalar8IdentityRange(rootOffset, maxKeyLength, lower, upper, identities);
        if (copied != expectedCount)
        {
            throw new InvalidDataException($"Reopened optimizer-mode VS8 range returned {copied} identities; expected {expectedCount}.");
        }

        long checksum = 0;
        for (int i = 0; i < copied; i++)
        {
            checksum += unchecked((long)identities[i]);
        }

        return new VarLenOptimizerModeShapeResult(firstEnqueueAt, candidateCount, maintenance, stats, checksum);
    }


    /// <summary>
    /// Validates the explicit on-demand optimizer mode for `VS16`.<br/>
    /// The write path records queue evidence during normal walked inserts, then maintenance is invoked after commit by a direct caller action.<br/>
    /// This models a future developer-facing "optimize now" operation without promoting the API in this slice.<br/>
    /// </summary>
    /// <param name="path">The target index file path.</param>
    /// <param name="options">The DataKernel options used for the file session.</param>
    /// <param name="keys">The deterministic key set to insert and optimize.</param>
    /// <param name="maxKeyLength">The maximum key length for the `VS16` profile.</param>
    /// <param name="branchCount">The requested compressed-route fanout cap.</param>
    /// <param name="policy">The optimizer policy used for observation thresholds and on-demand scheduling.</param>
    /// <param name="lower">The inclusive lower range key used for validation reads.</param>
    /// <param name="upper">The inclusive upper range key used for validation reads.</param>
    /// <returns>The optimizer mode validation result.</returns>
    private static VarLenOptimizerModeShapeResult ValidateVarKeyScalar16OptimizerOnDemandMode(
        string path,
        DataKernelOptions options,
        byte[][] keys,
        int maxKeyLength,
        int branchCount,
        VarLenOptimizerMaintenancePolicy policy,
        byte[] lower,
        byte[] upper)
    {
        VarKeyScalar16IndexHandle handle;
        VarLenOptimizerMaintenanceResult maintenance;
        VarLenRouteOptimizerState optimizerState;
        using (LibraDexFileSession session = LibraDexFileSession.Initialize(path, options, CreateDesignPerfMetadata(825), DataKernelTelemetryOptions.EnabledOptions))
        {
            (handle, _, _) = session.CreateVarKeyScalar16RootRouterIndex(CreateHarnessSlot(0, "vs16optd", 0), maxKeyLength, branchCount, policy);
            optimizerState = session.GetOrCreateVarLenRouteOptimizerState(handle);
            _ = session.CreateVarKeyScalar16ShelfAndLinkRootRoute(handle.RootRouterOffset, keys[0][0], VarKeyScalar16Profile.DefaultInitial);
            using (LibraDexFileSessionDurabilityBatch batch = session.BeginDurabilityBatch())
            {
                for (int i = 0; i < keys.Length; i++)
                {
                    CreateVarKeyScalar16Identity(i, out ulong identityHigh, out ulong identityLow);
                    VarKeyScalar16RoutedInsertResult result = session.InsertWalkedRoutedVarKeyScalar16(handle.RootRouterOffset, handle.MaxKeyLength, keys[i], identityHigh, identityLow, allowDuplicateKeys: true, maxRouterHops: 16);
                    if (result.InsertResult != VarKeyScalar16InsertResult.Inserted)
                    {
                        throw new InvalidDataException($"Expected on-demand optimizer VS16 insert {i}, got {result.Kind}/{result.InsertResult}.");
                    }

                    _ = session.ObserveVarKeyScalar16OptimizerWrite(handle, i + 1, keys[i], result);
                }

                _ = batch.Commit();
            }

            maintenance = session.OptimizeVarKeyScalar16OnDemand(handle, keys);
        }

        return ReadVarKeyScalar16OptimizerModeResult(path, options, handle.RootRouterOffset, handle.MaxKeyLength, lower, upper, keys.Length, optimizerState.FirstEnqueueAt, optimizerState.CandidateCount, maintenance);
    }


    /// <summary>
    /// Validates the between-batch automatic optimizer mode for `VS16`.<br/>
    /// The write loop commits the active hot batch as soon as the queue first emits a candidate, runs one bounded maintenance pass, then resumes normal walked inserts in a fresh batch.<br/>
    /// This models automatic idle/between-action optimization without requiring a background thread in the harness.<br/>
    /// </summary>
    /// <param name="path">The target index file path.</param>
    /// <param name="options">The DataKernel options used for the file session.</param>
    /// <param name="keys">The deterministic key set to insert and optimize.</param>
    /// <param name="maxKeyLength">The maximum key length for the `VS16` profile.</param>
    /// <param name="branchCount">The requested compressed-route fanout cap.</param>
    /// <param name="policy">The optimizer policy used for observation thresholds and automatic between-action scheduling.</param>
    /// <param name="lower">The inclusive lower range key used for validation reads.</param>
    /// <param name="upper">The inclusive upper range key used for validation reads.</param>
    /// <returns>The optimizer mode validation result.</returns>
    private static VarLenOptimizerModeShapeResult ValidateVarKeyScalar16OptimizerAutomaticMode(
        string path,
        DataKernelOptions options,
        byte[][] keys,
        int maxKeyLength,
        int branchCount,
        VarLenOptimizerMaintenancePolicy policy,
        byte[] lower,
        byte[] upper)
    {
        VarKeyScalar16IndexHandle handle;
        VarLenRouteOptimizerState optimizerState;
        VarLenOptimizerMaintenanceResult maintenance = default;
        using (LibraDexFileSession session = LibraDexFileSession.Initialize(path, options, CreateDesignPerfMetadata(826), DataKernelTelemetryOptions.EnabledOptions))
        {
            (handle, _, _) = session.CreateVarKeyScalar16RootRouterIndex(CreateHarnessSlot(0, "vs16opta", 0), maxKeyLength, branchCount, policy);
            optimizerState = session.GetOrCreateVarLenRouteOptimizerState(handle);
            _ = session.CreateVarKeyScalar16ShelfAndLinkRootRoute(handle.RootRouterOffset, keys[0][0], VarKeyScalar16Profile.DefaultInitial);
            LibraDexFileSessionDurabilityBatch batch = session.BeginDurabilityBatch();
            try
            {
                for (int i = 0; i < keys.Length; i++)
                {
                    CreateVarKeyScalar16Identity(i, out ulong identityHigh, out ulong identityLow);
                    VarKeyScalar16RoutedInsertResult result = session.InsertWalkedRoutedVarKeyScalar16(handle.RootRouterOffset, handle.MaxKeyLength, keys[i], identityHigh, identityLow, allowDuplicateKeys: true, maxRouterHops: 16);
                    if (result.InsertResult != VarKeyScalar16InsertResult.Inserted)
                    {
                        throw new InvalidDataException($"Expected automatic optimizer VS16 insert {i}, got {result.Kind}/{result.InsertResult}.");
                    }

                    bool queued = session.ObserveVarKeyScalar16OptimizerWrite(handle, i + 1, keys[i], result);
                    if (queued)
                    {
                        _ = batch.Commit();
                        batch.Dispose();
                        VarLenOptimizerMaintenanceResult pendingMaintenance = session.OptimizeVarKeyScalar16BetweenHotActions(handle, queued, maintenance.PublishedCount, keys.AsSpan(0, i + 1));
                        if (pendingMaintenance.AttemptedCount > 0 || pendingMaintenance.PublishedCount > 0)
                        {
                            maintenance = pendingMaintenance;
                        }

                        batch = session.BeginDurabilityBatch();
                    }
                }

                _ = batch.Commit();
            }
            finally
            {
                batch.Dispose();
            }

            if (maintenance.PublishedCount == 0)
            {
                throw new InvalidDataException("The automatic VS16 optimizer mode did not publish a queued candidate.");
            }

        }

        return ReadVarKeyScalar16OptimizerModeResult(path, options, handle.RootRouterOffset, handle.MaxKeyLength, lower, upper, keys.Length, optimizerState.FirstEnqueueAt, optimizerState.CandidateCount, maintenance);
    }


    /// <summary>
    /// Reads and validates a completed `VS16` optimizer-mode file.<br/>
    /// The method confirms the range result count and that the optimized route graph now contains a compressed router with shallow leaf depth.<br/>
    /// </summary>
    /// <param name="path">The target index file path.</param>
    /// <param name="options">The DataKernel options used for the file session.</param>
    /// <param name="rootOffset">The root router offset to validate.</param>
    /// <param name="maxKeyLength">The maximum key length for the `VS16` profile.</param>
    /// <param name="lower">The inclusive lower range key used for validation reads.</param>
    /// <param name="upper">The inclusive upper range key used for validation reads.</param>
    /// <param name="expectedCount">The expected identity count.</param>
    /// <param name="firstEnqueueAt">The first queued optimizer candidate ordinal.</param>
    /// <param name="maintenance">The maintenance result to include in the returned row.</param>
    /// <returns>The optimizer mode validation result.</returns>
    private static VarLenOptimizerModeShapeResult ReadVarKeyScalar16OptimizerModeResult(
        string path,
        DataKernelOptions options,
        long rootOffset,
        int maxKeyLength,
        byte[] lower,
        byte[] upper,
        int expectedCount,
        int firstEnqueueAt,
        int candidateCount,
        VarLenOptimizerMaintenanceResult maintenance)
    {
        if (maintenance.PublishedCount != 1)
        {
            throw new InvalidDataException("The VS16 optimizer mode expected exactly one published maintenance candidate.");
        }

        using LibraDexFileSession reopened = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions);
        VarKeyScalar8RouterShapeStats stats = MeasureVarKeyScalar8RouterShapeStats(reopened, rootOffset);
        if (stats.MultiByteRouterCount <= 0 || stats.MaxRouteDepth > 2)
        {
            throw new InvalidDataException($"The VS16 optimizer mode produced an unexpected route shape: mbRouters={stats.MultiByteRouterCount}, maxDepth={stats.MaxRouteDepth}.");
        }

        ulong[] identityHighs = new ulong[expectedCount];
        ulong[] identityLows = new ulong[expectedCount];
        int copied = reopened.ReadVarKeyScalar16IdentityRange(rootOffset, maxKeyLength, lower, upper, identityHighs, identityLows);
        if (copied != expectedCount)
        {
            throw new InvalidDataException($"Reopened optimizer-mode VS16 range returned {copied} identities; expected {expectedCount}.");
        }

        long checksum = 0;
        for (int i = 0; i < copied; i++)
        {
            checksum += unchecked((long)identityHighs[i]);
            checksum += unchecked((long)identityLows[i]);
        }

        return new VarLenOptimizerModeShapeResult(firstEnqueueAt, candidateCount, maintenance, stats, checksum);
    }


    /// <summary>
    /// Holds one parsed repeated-sample result from a hierarchical varlen comparison.<br/>
    /// The record stores only the ratios and route-shape facts needed by the median wrapper, leaving detailed per-run telemetry in the underlying command output path.<br/>
    /// </summary>
    private readonly record struct VarLenHierarchicalMedianSample(
        string Shape,
        int SampleIndex,
        int ItemCount,
        double EarlyReadVsSqlite,
        double EarlyWriteVsSqlite,
        double ColdReadVsSqlite,
        double ColdWriteVsSqlite,
        int FirstEnqueueAt,
        string CandidatePrefix,
        int EarlyRouters,
        int EarlyMultiByteRouters,
        int EarlyMaxDepth,
        int ColdRouters,
        int ColdMultiByteRouters,
        int ColdMaxDepth,
        double EarlyWriteItemsPerSecond,
        double ColdWriteItemsPerSecond,
        double SqliteWriteItemsPerSecond,
        double EarlyPreTriggerRouteWalkMs,
        double EarlyPreTriggerShelfReadMs,
        double EarlyPreTriggerMutationMs,
        double EarlyPreTriggerStageMs,
        double EarlyPreTriggerStructuralMs,
        double EarlyPostTriggerRouteWalkMs,
        double EarlyPostTriggerShelfReadMs,
        double EarlyPostTriggerMutationMs,
        double EarlyPostTriggerStageMs,
        double EarlyPostTriggerStructuralMs,
        double EarlyPreTriggerCommitMs,
        double EarlyOptimizeReachableMs,
        double EarlyOptimizeSubtreeBuildMs,
        double EarlyOptimizePublishMs,
        double EarlyOptimizeCommitMs,
        double EarlyPostTriggerCommitMs,
        int EarlyOptimizeTriggerAt,
        double EarlyPreTriggerInsertMs,
        double EarlyPostTriggerInsertMs);


    /// <summary>
    /// Identifies the floating-point ratios available from repeated hierarchical varlen comparison samples.<br/>
    /// Explicit enum selection keeps median/min/max helpers allocation-light and avoids delegate capture in this utility path.<br/>
    /// </summary>
    private enum VarLenHierarchicalMetric
    {
        EarlyReadVsSqlite,
        EarlyWriteVsSqlite,
        ColdReadVsSqlite,
        ColdWriteVsSqlite,
        EarlyWriteItemsPerSecond,
        ColdWriteItemsPerSecond,
        SqliteWriteItemsPerSecond,
        EarlyPreTriggerRouteWalkMs,
        EarlyPreTriggerShelfReadMs,
        EarlyPreTriggerMutationMs,
        EarlyPreTriggerStageMs,
        EarlyPreTriggerStructuralMs,
        EarlyPostTriggerRouteWalkMs,
        EarlyPostTriggerShelfReadMs,
        EarlyPostTriggerMutationMs,
        EarlyPostTriggerStageMs,
        EarlyPostTriggerStructuralMs,
        EarlyPreTriggerCommitMs,
        EarlyOptimizeReachableMs,
        EarlyOptimizeSubtreeBuildMs,
        EarlyOptimizePublishMs,
        EarlyOptimizeCommitMs,
        EarlyPostTriggerCommitMs,
        EarlyPreTriggerInsertMs,
        EarlyPostTriggerInsertMs,
        EarlyPreTriggerInsertItemsPerSecond,
        EarlyPostTriggerInsertItemsPerSecond,
        ProductionHotWriteMs,
        ProductionHotWriteItemsPerSecond,
        OptimizerMaintenanceMs
    }


    /// <summary>
    /// Identifies integer route-shape metrics available from repeated hierarchical varlen comparison samples.<br/>
    /// The current median summary uses only the first optimizer enqueue position, but the enum leaves the helper easy to extend.<br/>
    /// </summary>
    private enum VarLenHierarchicalIntMetric
    {
        FirstEnqueueAt,
        EarlyOptimizeTriggerAt
    }


    /// <summary>
    /// Selects the median value for one repeated varlen comparison metric.<br/>
    /// Values are sorted by the selected ratio, and the upper-middle value is returned for even sample counts to match the harness's existing median-sample convention.<br/>
    /// </summary>
    /// <param name="samples">The collected samples.</param>
    /// <param name="metric">The metric to select.</param>
    /// <returns>The median metric value.</returns>
    private static double MedianVarLenMetric(ReadOnlySpan<VarLenHierarchicalMedianSample> samples, VarLenHierarchicalMetric metric)
    {
        double[] values = new double[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            values[i] = SelectVarLenMetric(samples[i], metric);
        }

        Array.Sort(values);
        return values[values.Length / 2];
    }


    /// <summary>
    /// Finds the minimum and maximum values for one repeated varlen comparison metric.<br/>
    /// This gives a small stability signal beside the median without printing every captured sample row.<br/>
    /// </summary>
    /// <param name="samples">The collected samples.</param>
    /// <param name="metric">The metric to inspect.</param>
    /// <returns>The minimum and maximum values for the selected metric.</returns>
    private static (double Min, double Max) MinMaxVarLenMetric(ReadOnlySpan<VarLenHierarchicalMedianSample> samples, VarLenHierarchicalMetric metric)
    {
        double min = double.PositiveInfinity;
        double max = double.NegativeInfinity;
        for (int i = 0; i < samples.Length; i++)
        {
            double value = SelectVarLenMetric(samples[i], metric);
            min = Math.Min(min, value);
            max = Math.Max(max, value);
        }

        return (min, max);
    }


    /// <summary>
    /// Finds the minimum and maximum values for one integer route-shape metric.<br/>
    /// The median report currently uses this for enqueue position stability across deterministic repeated samples.<br/>
    /// </summary>
    /// <param name="samples">The collected samples.</param>
    /// <param name="metric">The integer metric to inspect.</param>
    /// <returns>The minimum and maximum values for the selected metric.</returns>
    private static (int Min, int Max) MinMaxVarLenIntMetric(ReadOnlySpan<VarLenHierarchicalMedianSample> samples, VarLenHierarchicalIntMetric metric)
    {
        int min = int.MaxValue;
        int max = int.MinValue;
        for (int i = 0; i < samples.Length; i++)
        {
            int value = SelectVarLenIntMetric(samples[i], metric);
            min = Math.Min(min, value);
            max = Math.Max(max, value);
        }

        return (min, max);
    }


    /// <summary>
    /// Selects the median value for one integer varlen comparison metric.<br/>
    /// The upper-middle value is returned for even sample counts to match the floating-point median helper used by this report.<br/>
    /// </summary>
    /// <param name="samples">The collected samples.</param>
    /// <param name="metric">The integer metric to select.</param>
    /// <returns>The median integer metric value.</returns>
    private static int MedianVarLenIntMetric(ReadOnlySpan<VarLenHierarchicalMedianSample> samples, VarLenHierarchicalIntMetric metric)
    {
        int[] values = new int[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            values[i] = SelectVarLenIntMetric(samples[i], metric);
        }

        Array.Sort(values);
        return values[values.Length / 2];
    }


    /// <summary>
    /// Selects a ratio metric from one parsed varlen comparison sample.<br/>
    /// Keeping selection explicit avoids using delegates in this harness utility and makes the metric mapping readable at the call site.<br/>
    /// </summary>
    /// <param name="sample">The parsed comparison sample.</param>
    /// <param name="metric">The metric to select.</param>
    /// <returns>The selected ratio value.</returns>
    private static double SelectVarLenMetric(VarLenHierarchicalMedianSample sample, VarLenHierarchicalMetric metric)
    {
        return metric switch
        {
            VarLenHierarchicalMetric.EarlyReadVsSqlite => sample.EarlyReadVsSqlite,
            VarLenHierarchicalMetric.EarlyWriteVsSqlite => sample.EarlyWriteVsSqlite,
            VarLenHierarchicalMetric.ColdReadVsSqlite => sample.ColdReadVsSqlite,
            VarLenHierarchicalMetric.ColdWriteVsSqlite => sample.ColdWriteVsSqlite,
            VarLenHierarchicalMetric.EarlyWriteItemsPerSecond => sample.EarlyWriteItemsPerSecond,
            VarLenHierarchicalMetric.ColdWriteItemsPerSecond => sample.ColdWriteItemsPerSecond,
            VarLenHierarchicalMetric.SqliteWriteItemsPerSecond => sample.SqliteWriteItemsPerSecond,
            VarLenHierarchicalMetric.EarlyPreTriggerRouteWalkMs => sample.EarlyPreTriggerRouteWalkMs,
            VarLenHierarchicalMetric.EarlyPreTriggerShelfReadMs => sample.EarlyPreTriggerShelfReadMs,
            VarLenHierarchicalMetric.EarlyPreTriggerMutationMs => sample.EarlyPreTriggerMutationMs,
            VarLenHierarchicalMetric.EarlyPreTriggerStageMs => sample.EarlyPreTriggerStageMs,
            VarLenHierarchicalMetric.EarlyPreTriggerStructuralMs => sample.EarlyPreTriggerStructuralMs,
            VarLenHierarchicalMetric.EarlyPostTriggerRouteWalkMs => sample.EarlyPostTriggerRouteWalkMs,
            VarLenHierarchicalMetric.EarlyPostTriggerShelfReadMs => sample.EarlyPostTriggerShelfReadMs,
            VarLenHierarchicalMetric.EarlyPostTriggerMutationMs => sample.EarlyPostTriggerMutationMs,
            VarLenHierarchicalMetric.EarlyPostTriggerStageMs => sample.EarlyPostTriggerStageMs,
            VarLenHierarchicalMetric.EarlyPostTriggerStructuralMs => sample.EarlyPostTriggerStructuralMs,
            VarLenHierarchicalMetric.EarlyPreTriggerCommitMs => sample.EarlyPreTriggerCommitMs,
            VarLenHierarchicalMetric.EarlyOptimizeReachableMs => sample.EarlyOptimizeReachableMs,
            VarLenHierarchicalMetric.EarlyOptimizeSubtreeBuildMs => sample.EarlyOptimizeSubtreeBuildMs,
            VarLenHierarchicalMetric.EarlyOptimizePublishMs => sample.EarlyOptimizePublishMs,
            VarLenHierarchicalMetric.EarlyOptimizeCommitMs => sample.EarlyOptimizeCommitMs,
            VarLenHierarchicalMetric.EarlyPostTriggerCommitMs => sample.EarlyPostTriggerCommitMs,
            VarLenHierarchicalMetric.EarlyPreTriggerInsertItemsPerSecond => sample.EarlyOptimizeTriggerAt * 1000d / Math.Max(sample.EarlyPreTriggerInsertMs, 0.000001),
            VarLenHierarchicalMetric.EarlyPostTriggerInsertItemsPerSecond => (sample.ItemCount - sample.EarlyOptimizeTriggerAt) * 1000d / Math.Max(sample.EarlyPostTriggerInsertMs, 0.000001),
            VarLenHierarchicalMetric.EarlyPreTriggerInsertMs => sample.EarlyPreTriggerInsertMs,
            VarLenHierarchicalMetric.EarlyPostTriggerInsertMs => sample.EarlyPostTriggerInsertMs,
            VarLenHierarchicalMetric.ProductionHotWriteMs => sample.EarlyPreTriggerInsertMs + sample.EarlyPostTriggerInsertMs + sample.EarlyPreTriggerCommitMs + sample.EarlyPostTriggerCommitMs,
            VarLenHierarchicalMetric.ProductionHotWriteItemsPerSecond => sample.ItemCount * 1000d / Math.Max(sample.EarlyPreTriggerInsertMs + sample.EarlyPostTriggerInsertMs + sample.EarlyPreTriggerCommitMs + sample.EarlyPostTriggerCommitMs, 0.000001),
            VarLenHierarchicalMetric.OptimizerMaintenanceMs => sample.EarlyOptimizeReachableMs + sample.EarlyOptimizeSubtreeBuildMs + sample.EarlyOptimizePublishMs + sample.EarlyOptimizeCommitMs,
            _ => throw new ArgumentOutOfRangeException(nameof(metric))
        };
    }


    /// <summary>
    /// Selects an integer route-shape metric from one parsed varlen comparison sample.<br/>
    /// The helper mirrors the ratio selector so summary calculations do not need reflection, delegates, or ad hoc lambda captures.<br/>
    /// </summary>
    /// <param name="sample">The parsed comparison sample.</param>
    /// <param name="metric">The metric to select.</param>
    /// <returns>The selected integer value.</returns>
    private static int SelectVarLenIntMetric(VarLenHierarchicalMedianSample sample, VarLenHierarchicalIntMetric metric)
    {
        return metric switch
        {
            VarLenHierarchicalIntMetric.FirstEnqueueAt => sample.FirstEnqueueAt,
            VarLenHierarchicalIntMetric.EarlyOptimizeTriggerAt => sample.EarlyOptimizeTriggerAt,
            _ => throw new ArgumentOutOfRangeException(nameof(metric))
        };
    }


    /// <summary>
    /// Measures one `VS8` walked write case using the requested insertion order and developer write intent.<br/>
    /// The method creates one file, runs all inserts inside one LibraDex durability batch, reopens the result for route-shape diagnostics, and returns write-only timing plus structural counters.<br/>
    /// </summary>
    /// <param name="path">The target LibraDex file path.</param>
    /// <param name="keys">The generated raw byte keys.</param>
    /// <param name="order">The insertion order expressed as indexes into <paramref name="keys"/>.</param>
    /// <param name="maxKeyLength">The maximum raw key length accepted by the VS8 profile family.</param>
    /// <param name="writeIntent">The developer write-intent hint to apply to the durability batch.</param>
    /// <param name="label">The row label to report.</param>
    /// <returns>The measured write-matrix row.</returns>
    private static VarKeyScalar8WriteMatrixResult MeasureVarKeyScalar8WalkedWriteIntent(
        string path,
        byte[][] keys,
        ReadOnlySpan<int> order,
        int maxKeyLength,
        LibraDexWriteIntent writeIntent,
        string label)
    {
        File.Delete(path);
        DataKernelOptions options = CreateDesignPerfOptions();
        long growCount = 0;
        long transformCount = 0;
        long rootOffset;
        VarKeyScalar8WalkedColdTelemetry coldTelemetry = default;
        VarKeyScalar8WalkedWriteAttribution attribution = default;
        DataKernelCommitTelemetry commitTelemetry = default;
        TimeSpan commitElapsed = TimeSpan.Zero;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch watch = Stopwatch.StartNew();
        using (LibraDexFileSession session = LibraDexFileSession.Initialize(path, options, CreateDesignPerfMetadata(917), DataKernelTelemetryOptions.EnabledOptions))
        {
            (RouterSnapshot root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, label, 0));
            rootOffset = root.Offset;
            _ = session.CreateVarKeyScalar8ShelfAndLinkRootRoute(rootOffset, keys[order[0]][0], VarKeyScalar8Profile.DefaultInitial);
            using LibraDexFileSessionDurabilityBatch durabilityBatch = session.BeginDurabilityBatch(writeIntent);
            for (int i = 0; i < order.Length; i++)
            {
                int sourceIndex = order[i];
                VarKeyScalar8RoutedInsertResult result = session.InsertWalkedRoutedVarKeyScalar8(rootOffset, maxKeyLength, keys[sourceIndex], CreateVarKeyScalar8Identity(sourceIndex), allowDuplicateKeys: true, maxRouterHops: 16);
                attribution += result.Attribution;
                coldTelemetry.Add(result);
                if (result.InsertResult != VarKeyScalar8InsertResult.Inserted)
                {
                    throw new InvalidDataException($"Expected {label} VS8 insert {i}, got {result.Kind}/{result.InsertResult}.");
                }

                if (result.Kind == VarKeyScalar8RoutedInsertKind.WalkedGrow)
                {
                    growCount++;
                }

                if (result.Kind == VarKeyScalar8RoutedInsertKind.WalkedShelfTransformSplit)
                {
                    transformCount++;
                }
            }

            Stopwatch commitWatch = Stopwatch.StartNew();
            (commitTelemetry, _, _) = durabilityBatch.Commit();
            commitWatch.Stop();
            commitElapsed = commitWatch.Elapsed;
        }

        watch.Stop();
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        using LibraDexFileSession reopened = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions);
        VarKeyScalar8RouterShapeStats stats = MeasureVarKeyScalar8RouterShapeStats(reopened, rootOffset);
        return VarKeyScalar8WriteMatrixResult.ForLibraDex(
            label,
            DescribeVarKeyScalar8WriteOrder(order, keys),
            DescribeWriteIntent(writeIntent),
            order.Length,
            watch.Elapsed,
            new FileInfo(path).Length,
            growCount,
            transformCount,
            stats,
            coldTelemetry,
            attribution,
            commitElapsed,
            commitTelemetry,
            allocatedBytes);
    }


    /// <summary>
    /// Measures one `VS8` bulk-tree construction row for the same generated hierarchical key set.<br/>
    /// This gives the matrix a planned-build case alongside walked mutation so bulk index creation is not inferred from incremental insert timing.<br/>
    /// </summary>
    /// <param name="path">The target LibraDex file path.</param>
    /// <param name="keys">The generated raw byte keys.</param>
    /// <param name="maxKeyLength">The maximum raw key length accepted by the VS8 profile family.</param>
    /// <param name="branchCount">The requested bulk fanout count.</param>
    /// <param name="label">The row label to report.</param>
    /// <returns>The measured write-matrix row.</returns>
    private static VarKeyScalar8WriteMatrixResult MeasureVarKeyScalar8BulkWrite(
        string path,
        byte[][] keys,
        int maxKeyLength,
        int branchCount,
        string label)
    {
        File.Delete(path);
        DataKernelOptions options = CreateDesignPerfOptions();
        long rootOffset;
        DataKernelCommitTelemetry commitTelemetry = default;
        TimeSpan commitElapsed = TimeSpan.Zero;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch watch = Stopwatch.StartNew();
        using (LibraDexFileSession session = LibraDexFileSession.Initialize(path, options, CreateDesignPerfMetadata(918), DataKernelTelemetryOptions.EnabledOptions))
        {
            (RouterSnapshot root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, label, 0));
            rootOffset = root.Offset;
            using LibraDexFileSessionDurabilityBatch durabilityBatch = session.BeginDurabilityBatch(new LibraDexWriteIntent(
                Order: LibraDexWriteOrder.Sorted,
                Volume: LibraDexWriteVolume.Thousands,
                Locality: LibraDexWriteLocality.Clustered,
                Priority: LibraDexWritePriority.WriteSpeed));
            _ = CreateVarKeyScalar8HierarchicalBulkTree(session, rootOffset, keys, maxKeyLength, branchCount);
            Stopwatch commitWatch = Stopwatch.StartNew();
            (commitTelemetry, _, _) = durabilityBatch.Commit();
            commitWatch.Stop();
            commitElapsed = commitWatch.Elapsed;
        }

        watch.Stop();
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        using LibraDexFileSession reopened = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions);
        VarKeyScalar8RouterShapeStats stats = MeasureVarKeyScalar8RouterShapeStats(reopened, rootOffset);
        return VarKeyScalar8WriteMatrixResult.ForLibraDex(
            label,
            "bulk",
            "Sorted/Clustered/WriteSpeed",
            keys.Length,
            watch.Elapsed,
            new FileInfo(path).Length,
            growths: 0,
            transforms: 0,
            stats,
            default,
            default,
            commitElapsed,
            commitTelemetry,
            allocatedBytes);
    }


    /// <summary>
    /// Creates a deterministic lexical insertion order for generated VS8 keys.<br/>
    /// Sorting indexes instead of key arrays preserves the original identity source index for correctness checks and SQLite parity rows.<br/>
    /// </summary>
    /// <param name="keys">The generated raw byte keys.</param>
    /// <returns>An order array sorted by raw key bytes.</returns>
    private static int[] CreateSortedVarKeyScalar8Order(byte[][] keys)
    {
        int[] order = CreateSequentialOrder(keys.Length);
        Array.Sort(order, (left, right) => keys[left].AsSpan().SequenceCompareTo(keys[right]));
        return order;
    }


    /// <summary>
    /// Prints one VS8 write-intent matrix row using invariant numeric formatting.<br/>
    /// SQLite rows use zero structural counters because their comparable route shape is intentionally opaque to this harness.<br/>
    /// </summary>
    /// <param name="result">The measured write-matrix row.</param>
    /// <param name="itemCount">The logical item count used for bytes-per-item reporting.</param>
    private static void PrintVarKeyScalar8WriteMatrixResult(VarKeyScalar8WriteMatrixResult result, int itemCount)
    {
        Console.WriteLine(FormattableString.Invariant(
            $"| {result.Label} | {result.Order} | {result.Intent} | {result.ItemsPerSecond:F2} | {result.Elapsed.TotalMilliseconds:F2} | {result.AllocatedBytes / 1024.0:F2} | {TicksToMilliseconds(result.Attribution.RouteWalkTicks):F2} | {TicksToMilliseconds(result.Attribution.ShelfReadTicks):F2} | {TicksToMilliseconds(result.Attribution.MutationTicks):F2} | {TicksToMilliseconds(result.Attribution.StageTicks):F2} | {TicksToMilliseconds(result.Attribution.StructuralTicks):F2} | {result.CommitElapsed.TotalMilliseconds:F2} | {result.Commit.WriteCallCount} | {result.Commit.BytesWritten} | {result.Bytes} | {(double)result.Bytes / itemCount:F2} | {result.Growths} | {result.Transforms} | {result.ColdTelemetry.AverageTargetRouterDepth:F2} | {result.ColdTelemetry.MaxTargetRouterDepth} | {result.ColdTelemetry.AverageTransformRouterDepth:F2} | {result.ColdTelemetry.MaxTransformRouterDepth} | {result.ColdTelemetry.AverageTransformShelfItems:F2} | {result.ColdTelemetry.MaxTransformShelfItems} | {result.RouterCount} | {result.MultiByteRouterCount} | {result.ShelfCount} | {result.AverageRouteDepth:F2} | {result.MaxRouteDepth} |"));
    }


    /// <summary>
    /// Holds one write-intent matrix measurement row.<br/>
    /// LibraDex rows include route-shape counters; SQLite rows keep those counters at zero because the physical B-tree shape is outside the harness contract.<br/>
    /// </summary>
    /// <param name="Label">The benchmark row label.</param>
    /// <param name="Order">The harness insertion-order label.</param>
    /// <param name="Intent">The developer write-intent label.</param>
    /// <param name="ItemsPerSecond">The measured logical write throughput.</param>
    /// <param name="Elapsed">The elapsed write time.</param>
    /// <param name="Bytes">The resulting backing storage bytes.</param>
    /// <param name="Growths">The number of walked shelf growth events.</param>
    /// <param name="Transforms">The number of walked shelf-to-router transform events.</param>
    /// <param name="RouterCount">The number of routed LibraDex routers after the write.</param>
    /// <param name="MultiByteRouterCount">The number of multi-byte LibraDex routers after the write.</param>
    /// <param name="ShelfCount">The number of reached LibraDex shelves after the write.</param>
    /// <param name="AverageRouteDepth">The average routed leaf depth after the write.</param>
    /// <param name="MaxRouteDepth">The maximum routed leaf depth after the write.</param>
    /// <param name="ColdTelemetry">The walked structural telemetry captured during LibraDex write loops.</param>
    /// <param name="Attribution">The walked-write attribution totals for LibraDex walked rows.</param>
    /// <param name="CommitElapsed">The wall-clock elapsed time spent in the final commit or transaction commit phase.</param>
    /// <param name="Commit">The DataKernel commit telemetry for LibraDex rows.</param>
    /// <param name="AllocatedBytes">The bytes allocated on the current managed thread during the measured write phase.</param>
    private readonly record struct VarKeyScalar8WriteMatrixResult(
        string Label,
        string Order,
        string Intent,
        double ItemsPerSecond,
        TimeSpan Elapsed,
        long Bytes,
        long Growths,
        long Transforms,
        long RouterCount,
        long MultiByteRouterCount,
        long ShelfCount,
        double AverageRouteDepth,
        int MaxRouteDepth,
        VarKeyScalar8WalkedColdTelemetry ColdTelemetry,
        VarKeyScalar8WalkedWriteAttribution Attribution,
        TimeSpan CommitElapsed,
        DataKernelCommitTelemetry Commit,
        long AllocatedBytes)
    {
        /// <summary>
        /// Creates a LibraDex matrix row from elapsed write time and route-shape diagnostics.<br/>
        /// The helper centralizes throughput calculation so row construction remains consistent across walked and bulk cases.<br/>
        /// </summary>
        /// <param name="label">The benchmark row label.</param>
        /// <param name="order">The harness insertion-order label.</param>
        /// <param name="intent">The developer write-intent label.</param>
        /// <param name="itemCount">The logical item count written.</param>
        /// <param name="elapsed">The elapsed write time.</param>
        /// <param name="bytes">The resulting backing storage bytes.</param>
        /// <param name="growths">The number of walked shelf growth events.</param>
        /// <param name="transforms">The number of walked shelf-to-router transform events.</param>
        /// <param name="stats">The route-shape diagnostics after reopen.</param>
        /// <param name="coldTelemetry">The walked structural telemetry captured during LibraDex write loops.</param>
        /// <param name="attribution">The elapsed-time attribution totals captured during walked writes.</param>
        /// <param name="commitElapsed">The measured final commit elapsed time.</param>
        /// <param name="commit">The final DataKernel commit telemetry.</param>
        /// <param name="allocatedBytes">The bytes allocated on the current managed thread during the measured row.</param>
        /// <returns>A populated LibraDex matrix row.</returns>
        public static VarKeyScalar8WriteMatrixResult ForLibraDex(
            string label,
            string order,
            string intent,
            int itemCount,
            TimeSpan elapsed,
            long bytes,
            long growths,
            long transforms,
            VarKeyScalar8RouterShapeStats stats,
            VarKeyScalar8WalkedColdTelemetry coldTelemetry,
            VarKeyScalar8WalkedWriteAttribution attribution,
            TimeSpan commitElapsed,
            DataKernelCommitTelemetry commit,
            long allocatedBytes)
        {
            return new VarKeyScalar8WriteMatrixResult(
                label,
                order,
                intent,
                itemCount / Math.Max(elapsed.TotalSeconds, 0.000001),
                elapsed,
                bytes,
                growths,
                transforms,
                stats.RouterCount,
                stats.MultiByteRouterCount,
                stats.ShelfCount,
                stats.AverageRouteDepth,
                stats.MaxRouteDepth,
                coldTelemetry,
                attribution,
                commitElapsed,
                commit,
                allocatedBytes);
        }

        /// <summary>
        /// Creates a SQLite matrix row from elapsed transaction write time.<br/>
        /// Structural counters are zero because SQLite route internals are deliberately not part of this LibraDex harness row.<br/>
        /// </summary>
        /// <param name="label">The benchmark row label.</param>
        /// <param name="order">The harness insertion-order label.</param>
        /// <param name="itemCount">The logical item count written.</param>
        /// <param name="elapsed">The elapsed write time.</param>
        /// <param name="bytes">The resulting SQLite storage bytes.</param>
        /// <returns>A populated SQLite matrix row.</returns>
        public static VarKeyScalar8WriteMatrixResult ForSqlite(
            string label,
            string order,
            int itemCount,
            TimeSpan elapsed,
            long bytes,
            TimeSpan commitElapsed,
            long allocatedBytes)
        {
            return new VarKeyScalar8WriteMatrixResult(
                label,
                order,
                "transaction",
                itemCount / Math.Max(elapsed.TotalSeconds, 0.000001),
                elapsed,
                bytes,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                default,
                default,
                commitElapsed,
                default,
                allocatedBytes);
        }
    }


    /// <summary>
    /// Builds one sorted precompacted `VS8` shelf for a generated first-byte prefix.<br/>
    /// The helper keeps bulk-build comparison honest by using the same raw keys and identities as routed incremental and SQLite rows, then writing the final shelf image once.<br/>
    /// </summary>
    /// <param name="itemCount">The total generated item count.</param>
    /// <param name="keyLength">The generated key length in bytes.</param>
    /// <param name="prefixCount">The number of generated first-byte prefixes.</param>
    /// <param name="prefix">The prefix to materialize.</param>
    /// <param name="profile">The selected shelf profile that can contain the prefix rows.</param>
    /// <param name="shelfBytes">The prebuilt shelf bytes.</param>
    private static void CreateVarKeyScalar8PrefixShelf(
        int itemCount,
        int keyLength,
        int prefixCount,
        int prefix,
        out VarKeyScalar8Profile profile,
        out byte[] shelfBytes)
    {
        List<(byte[] Key, ulong Identity)> items = [];
        for (int i = 0; i < itemCount; i++)
        {
            if (i % prefixCount == prefix)
            {
                items.Add((CreateVarKeyScalar8Key(i, keyLength, prefixCount), CreateVarKeyScalar8Identity(i)));
            }
        }

        items.Sort(static (left, right) =>
        {
            int keyComparison = left.Key.AsSpan().SequenceCompareTo(right.Key);
            if (keyComparison != 0)
            {
                return keyComparison;
            }

            return left.Identity.CompareTo(right.Identity);
        });

        byte[][] keys = new byte[items.Count][];
        ulong[] identities = new ulong[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            keys[i] = items[i].Key;
            identities[i] = items[i].Identity;
        }

        profile = SelectVarKeyScalar8BuildProfile(keys, identities);
        if (!VarKeyScalar8.TryBuildFromSorted(keys, identities, profile, out shelfBytes))
        {
            throw new InvalidDataException($"VS8 routed bulk build could not build prefix {prefix} shelf at {profile.ShelfExtentSize} bytes.");
        }
    }


    /// <summary>
    /// Builds one sorted precompacted `VS16` shelf for a generated first-byte prefix.<br/>
    /// This is the fixed-identity-width counterpart to `CreateVarKeyScalar8PrefixShelf`, keeping key generation and sorted tuple ordering identical while widening only the identity payload.<br/>
    /// The resulting bytes are used by the routed bulk-build comparison row so the measured difference from `VS8` is shelf capacity and identity width, not construction policy.<br/>
    /// </summary>
    /// <param name="itemCount">The total generated item count.</param>
    /// <param name="keyLength">The generated key length in bytes.</param>
    /// <param name="prefixCount">The number of generated first-byte prefixes.</param>
    /// <param name="prefix">The prefix to materialize.</param>
    /// <param name="profile">The selected shelf profile that can contain the prefix rows.</param>
    /// <param name="shelfBytes">The prebuilt shelf bytes.</param>
    private static void CreateVarKeyScalar16PrefixShelf(
        int itemCount,
        int keyLength,
        int prefixCount,
        int prefix,
        out VarKeyScalar16Profile profile,
        out byte[] shelfBytes)
    {
        List<(byte[] Key, ulong IdentityHigh, ulong IdentityLow)> items = [];
        for (int i = 0; i < itemCount; i++)
        {
            if (i % prefixCount == prefix)
            {
                CreateVarKeyScalar16Identity(i, out ulong identityHigh, out ulong identityLow);
                items.Add((CreateVarKeyScalar8Key(i, keyLength, prefixCount), identityHigh, identityLow));
            }
        }

        items.Sort(static (left, right) =>
        {
            int keyComparison = left.Key.AsSpan().SequenceCompareTo(right.Key);
            if (keyComparison != 0)
            {
                return keyComparison;
            }

            int highComparison = left.IdentityHigh.CompareTo(right.IdentityHigh);
            return highComparison != 0
                ? highComparison
                : left.IdentityLow.CompareTo(right.IdentityLow);
        });

        byte[][] keys = new byte[items.Count][];
        ulong[] identityHighs = new ulong[items.Count];
        ulong[] identityLows = new ulong[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            keys[i] = items[i].Key;
            identityHighs[i] = items[i].IdentityHigh;
            identityLows[i] = items[i].IdentityLow;
        }

        profile = SelectVarKeyScalar16BuildProfile(keys, identityHighs, identityLows);
        if (!VarKeyScalar16.TryBuildFromSorted(keys, identityHighs, identityLows, profile, out shelfBytes))
        {
            throw new InvalidDataException($"VS16 routed bulk build could not build prefix {prefix} shelf at {profile.ShelfExtentSize} bytes.");
        }
    }


    /// <summary>
    /// Builds one sorted precompacted `VV` shelf for a generated first-byte prefix.<br/>
    /// The helper uses the same generated raw keys and raw identities as the routed incremental and SQLite rows, then writes the final shelf image once.<br/>
    /// This isolates bulk construction potential from walked insert costs without introducing split or optimizer behavior into the first `VV` comparison.<br/>
    /// </summary>
    /// <param name="itemCount">The total generated item count.</param>
    /// <param name="keyLength">The generated key length in bytes.</param>
    /// <param name="identityLength">The generated identity length in bytes.</param>
    /// <param name="prefixCount">The number of generated first-byte prefixes.</param>
    /// <param name="prefix">The prefix to materialize.</param>
    /// <param name="profile">The selected shelf profile that can contain the prefix rows.</param>
    /// <param name="shelfBytes">The prebuilt shelf bytes.</param>
    private static void CreateVarKeyVarIdentityPrefixShelf(
        int itemCount,
        int keyLength,
        int identityLength,
        int prefixCount,
        int prefix,
        VarKeyVarIdentityProfile initialProfile,
        out VarKeyVarIdentityProfile profile,
        out byte[] shelfBytes)
    {
        List<(byte[] Key, byte[] Identity)> items = [];
        for (int i = 0; i < itemCount; i++)
        {
            if (i % prefixCount == prefix)
            {
                items.Add((CreateVarKeyScalar8Key(i, keyLength, prefixCount), CreateScalar8VarIdentity(i, identityLength)));
            }
        }

        items.Sort(static (left, right) =>
        {
            int keyComparison = left.Key.AsSpan().SequenceCompareTo(right.Key);
            if (keyComparison != 0)
            {
                return keyComparison;
            }

            return VarKeyVarIdentityLayout.CompareIdentityBytes(left.Identity, right.Identity);
        });

        byte[][] keys = new byte[items.Count][];
        byte[][] identities = new byte[items.Count][];
        for (int i = 0; i < items.Count; i++)
        {
            keys[i] = items[i].Key;
            identities[i] = items[i].Identity;
        }

        profile = SelectVarKeyVarIdentityBuildProfile(keys, identities, initialProfile);
        if (!VarKeyVarIdentity.TryBuildFromSorted(keys, identities, profile, out shelfBytes))
        {
            throw new InvalidDataException($"VV routed bulk build could not build prefix {prefix} shelf at {profile.ShelfExtentSize} bytes.");
        }
    }


    /// <summary>
    /// Measures routed `VS8` prefix-range reads through an opened session.<br/>
    /// This exercises root-route enumeration, shelf byte loading, compact slot decoding, and cursor iteration for each iteration.<br/>
    /// </summary>
    /// <param name="session">The opened LibraDex session.</param>
    /// <param name="rootOffset">The root router offset for the `VS8` index.</param>
    /// <param name="keyLength">The generated key length in bytes.</param>
    /// <param name="prefixCount">The number of generated first-byte prefixes.</param>
    /// <param name="itemCount">The total generated identity count.</param>
    /// <param name="iterations">The number of measured range reads.</param>
    /// <param name="iterationMode">The comparison iteration payload mode.</param>
    /// <returns>The routed range-read measurement row.</returns>
    private static VarKeyScalar8ReadMeasurement MeasureVarKeyScalar8RoutedRangeReads(LibraDexFileSession session, long rootOffset, int keyLength, int prefixCount, int itemCount, int iterations, VarIdentityIterationMode iterationMode)
    {
        byte[] lower = CreateVarKeyScalar8PrefixLower(0, keyLength);
        byte[] upper = CreateVarKeyScalar8PrefixUpper(Math.Min(2, prefixCount - 1), keyLength);
        int expected = CountVarKeyScalar8PrefixRange(itemCount, prefixCount, 0, Math.Min(2, prefixCount - 1));
        long checksum = 0;
        long total = 0;
        _ = session.GetAndResetReadTelemetry();
        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            int count = 0;
            long iterationChecksum = 0;
            using VarKeyScalar8RangeReader reader = session.OpenVarKeyScalar8RangeReader(rootOffset, maxKeyLength: 1024, lower, upper);
            while (reader.MoveNext())
            {
                count++;
                iterationChecksum += ChecksumVarKeyScalar8Current(reader, iterationMode);
            }

            if (count != expected)
            {
                throw new InvalidDataException($"Routed VS8 read comparison expected {expected} identities, got {count}.");
            }

            total += count;
            checksum += iterationChecksum;
        }

        watch.Stop();
        DataKernelReadTelemetry telemetry = session.GetAndResetReadTelemetry();
        (int cacheArenaCount, long cacheBytes) = session.GetRouterArenaReadCacheStatsForValidation();
        return CreateVarKeyScalar8ReadMeasurement(iterations, expected, total, watch.Elapsed, checksum, telemetry, cacheArenaCount, cacheBytes);
    }


    /// <summary>
    /// Measures routed `VS16` prefix-range reads through an opened session.<br/>
    /// This intentionally matches the `VS8` routed read measurement and changes only the identity output lanes, so clone-fidelity regressions are visible in read telemetry and latency.<br/>
    /// The checksum folds both identity halves to catch unset or swapped high/low values without adding a separate validation pass inside the hot loop.<br/>
    /// </summary>
    /// <param name="session">The opened LibraDex session.</param>
    /// <param name="rootOffset">The root router offset for the `VS16` index.</param>
    /// <param name="keyLength">The generated key length in bytes.</param>
    /// <param name="prefixCount">The number of generated first-byte prefixes.</param>
    /// <param name="itemCount">The total generated identity count.</param>
    /// <param name="iterations">The number of measured range reads.</param>
    /// <param name="iterationMode">The comparison iteration payload mode.</param>
    /// <returns>The routed range-read measurement row.</returns>
    private static VarKeyScalar8ReadMeasurement MeasureVarKeyScalar16RoutedRangeReads(LibraDexFileSession session, long rootOffset, int keyLength, int prefixCount, int itemCount, int iterations, VarIdentityIterationMode iterationMode)
    {
        byte[] lower = CreateVarKeyScalar8PrefixLower(0, keyLength);
        byte[] upper = CreateVarKeyScalar8PrefixUpper(Math.Min(2, prefixCount - 1), keyLength);
        int expected = CountVarKeyScalar8PrefixRange(itemCount, prefixCount, 0, Math.Min(2, prefixCount - 1));
        long checksum = 0;
        long total = 0;
        _ = session.GetAndResetReadTelemetry();
        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            int count = 0;
            long iterationChecksum = 0;
            using VarKeyScalar16RangeReader reader = session.OpenVarKeyScalar16RangeReader(rootOffset, maxKeyLength: 1024, lower, upper);
            while (reader.MoveNext())
            {
                count++;
                iterationChecksum += ChecksumVarKeyScalar16Current(reader, iterationMode);
            }

            if (count != expected)
            {
                throw new InvalidDataException($"Routed VS16 read comparison expected {expected} identities, got {count}.");
            }

            total += count;
            checksum += iterationChecksum;
        }

        watch.Stop();
        DataKernelReadTelemetry telemetry = session.GetAndResetReadTelemetry();
        (int cacheArenaCount, long cacheBytes) = session.GetRouterArenaReadCacheStatsForValidation();
        return CreateVarKeyScalar8ReadMeasurement(iterations, expected, total, watch.Elapsed, checksum, telemetry, cacheArenaCount, cacheBytes);
    }


    /// <summary>
    /// Validates range-count traversal through compressed multi-byte var-key routers for `VS8`, `VS16`, and `VV` shapes.<br/>
    /// The command manually builds one root route to one compressed router whose child routes each point at sorted shelves, then compares shape-specific range-count primitives against normal range-reader counts over an edge-spanning range.<br/>
    /// </summary>
    /// <param name="args">The harness command-line arguments.<br/></param>
    /// <returns>Zero when all compressed-router range-count primitives match their reader parity counts.<br/></returns>
    private static int RunVarLenMultiByteRangeCountSanity(string[] args)
    {
        string path = GetOption(args, "--path", Path.Combine(@"T:\LibraDex", "varlen-mb-range-count-sanity.lbdx"));
        int groupCount = GetIntOption(args, "--groups", 16);
        int itemsPerGroup = GetIntOption(args, "--items-per-group", 96);
        int identityLength = GetIntOption(args, "--identity-length", 32);
        if (groupCount <= 2 ||
            groupCount > 128 ||
            itemsPerGroup <= 0 ||
            identityLength <= 0 ||
            identityLength > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(args), "Multi-byte range-count sanity requires groups 3-128, positive items per group, and identity length 1-1024.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.Delete(path);
        DataKernelOptions options = CreateDesignPerfOptions();
        byte[][] keys = CreateVarLenMultiByteRangeCountKeys(groupCount, itemsPerGroup);
        int lowerGroup = Math.Min(2, groupCount - 2);
        int upperGroup = Math.Max(lowerGroup + 1, groupCount - 3);
        byte[] lowerKey = CreateVarLenMultiByteRangeCountKey(lowerGroup, Math.Min(7, itemsPerGroup - 1));
        byte[] upperKey = CreateVarLenMultiByteRangeCountKey(upperGroup, Math.Max(0, itemsPerGroup - 11));
        long expected = CountVarLenMultiByteRangeCountExpected(keys, lowerKey, upperKey);
        long vs8RootOffset;
        long vs16RootOffset;
        long vvRootOffset;

        using (LibraDexFileSession session = LibraDexFileSession.Initialize(path, options, CreateDesignPerfMetadata(9217), DataKernelTelemetryOptions.EnabledOptions))
        {
            (RouterSnapshot vs8Root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "vmbvs8", 0));
            vs8RootOffset = vs8Root.Offset;
            CreateVarKeyScalar8MultiByteRangeCountTree(session, vs8RootOffset, groupCount, itemsPerGroup);

            (RouterSnapshot vs16Root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(1, "vmbvs16", 0));
            vs16RootOffset = vs16Root.Offset;
            CreateVarKeyScalar16MultiByteRangeCountTree(session, vs16RootOffset, groupCount, itemsPerGroup);

            (RouterSnapshot vvRoot, _) = session.CreateRootRouterIndex(CreateHarnessSlot(2, "vmbvv", 0));
            vvRootOffset = vvRoot.Offset;
            CreateVarKeyVarIdentityMultiByteRangeCountTree(session, vvRootOffset, groupCount, itemsPerGroup, identityLength);
        }

        using LibraDexFileSession reopened = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions);
        ValidateVarLenMultiByteRangeCountShape("VS8", expected, reopened.CountVarKeyScalar8IdentityRange(vs8RootOffset, 1024, lowerKey, upperKey), CountVarKeyScalar8RangeReader(reopened, vs8RootOffset, lowerKey, upperKey));
        ValidateVarLenMultiByteRangeCountShape("VS16", expected, reopened.CountVarKeyScalar16IdentityRange(vs16RootOffset, 1024, lowerKey, upperKey), CountVarKeyScalar16RangeReader(reopened, vs16RootOffset, lowerKey, upperKey));
        ValidateVarLenMultiByteRangeCountShape("VV", expected, reopened.CountVarKeyVarIdentityRange(vvRootOffset, 1024, 1024, lowerKey, upperKey), CountVarKeyVarIdentityRangeReader(reopened, vvRootOffset, lowerKey, upperKey));

        Console.WriteLine($"varlen-mb-range-count-sanity ok path={path} groups={groupCount} itemsPerGroup={itemsPerGroup} expected={expected}");
        return 0;
    }


    /// <summary>
    /// Measures compressed multi-byte var-key range-count primitives against ordinary range-reader counting for `VS8`, `VS16`, and `VV` shapes.<br/>
    /// The setup matches <see cref="RunVarLenMultiByteRangeCountSanity"/> so the measured path contains one compressed router with boundary and fully contained route targets.<br/>
    /// </summary>
    /// <param name="args">The harness command-line arguments.<br/></param>
    /// <returns>Zero when all measured loops return the expected checksum.<br/></returns>
    private static int RunVarLenMultiByteRangeCountPerf(string[] args)
    {
        string path = GetOption(args, "--path", Path.Combine(@"T:\LibraDex", "varlen-mb-range-count-perf.lbdx"));
        int groupCount = GetIntOption(args, "--groups", 64);
        int itemsPerGroup = GetIntOption(args, "--items-per-group", 512);
        int identityLength = GetIntOption(args, "--identity-length", 32);
        int iterations = GetIntOption(args, "--iterations", 10_000);
        int warmupIterations = GetIntOption(args, "--warmup-iterations", 5);
        if (groupCount <= 2 ||
            groupCount > 128 ||
            itemsPerGroup <= 0 ||
            identityLength <= 0 ||
            identityLength > 1024 ||
            iterations <= 0 ||
            warmupIterations < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(args), "Multi-byte range-count perf requires groups 3-128, positive items per group, identity length 1-1024, positive iterations, and non-negative warmups.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.Delete(path);
        DataKernelOptions options = CreateDesignPerfOptions();
        byte[][] keys = CreateVarLenMultiByteRangeCountKeys(groupCount, itemsPerGroup);
        int lowerGroup = Math.Min(2, groupCount - 2);
        int upperGroup = Math.Max(lowerGroup + 1, groupCount - 3);
        byte[] lowerKey = CreateVarLenMultiByteRangeCountKey(lowerGroup, Math.Min(7, itemsPerGroup - 1));
        byte[] upperKey = CreateVarLenMultiByteRangeCountKey(upperGroup, Math.Max(0, itemsPerGroup - 11));
        long expected = CountVarLenMultiByteRangeCountExpected(keys, lowerKey, upperKey);
        long vs8RootOffset;
        long vs16RootOffset;
        long vvRootOffset;

        using (LibraDexFileSession session = LibraDexFileSession.Initialize(path, options, CreateDesignPerfMetadata(9218), DataKernelTelemetryOptions.EnabledOptions))
        {
            (RouterSnapshot vs8Root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "vmpvs8", 0));
            vs8RootOffset = vs8Root.Offset;
            CreateVarKeyScalar8MultiByteRangeCountTree(session, vs8RootOffset, groupCount, itemsPerGroup);

            (RouterSnapshot vs16Root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(1, "vmpvs16", 0));
            vs16RootOffset = vs16Root.Offset;
            CreateVarKeyScalar16MultiByteRangeCountTree(session, vs16RootOffset, groupCount, itemsPerGroup);

            (RouterSnapshot vvRoot, _) = session.CreateRootRouterIndex(CreateHarnessSlot(2, "vmpvv", 0));
            vvRootOffset = vvRoot.Offset;
            CreateVarKeyVarIdentityMultiByteRangeCountTree(session, vvRootOffset, groupCount, itemsPerGroup, identityLength);
        }

        using LibraDexFileSession reopened = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions);
        VarLenMultiByteRangeCountPerfRow vs8Count = MeasureVarKeyScalar8MultiByteRangeCountPrimitive(reopened, vs8RootOffset, lowerKey, upperKey, expected, iterations, warmupIterations);
        VarLenMultiByteRangeCountPerfRow vs8Reader = MeasureVarKeyScalar8MultiByteRangeReaderCount(reopened, vs8RootOffset, lowerKey, upperKey, expected, iterations, warmupIterations);
        VarLenMultiByteRangeCountPerfRow vs16Count = MeasureVarKeyScalar16MultiByteRangeCountPrimitive(reopened, vs16RootOffset, lowerKey, upperKey, expected, iterations, warmupIterations);
        VarLenMultiByteRangeCountPerfRow vs16Reader = MeasureVarKeyScalar16MultiByteRangeReaderCount(reopened, vs16RootOffset, lowerKey, upperKey, expected, iterations, warmupIterations);
        VarLenMultiByteRangeCountPerfRow vvCount = MeasureVarKeyVarIdentityMultiByteRangeCountPrimitive(reopened, vvRootOffset, lowerKey, upperKey, expected, iterations, warmupIterations);
        VarLenMultiByteRangeCountPerfRow vvReader = MeasureVarKeyVarIdentityMultiByteRangeReaderCount(reopened, vvRootOffset, lowerKey, upperKey, expected, iterations, warmupIterations);

        Console.WriteLine(
            $"varlen-mb-range-count-perf ok path={path} groups={groupCount} itemsPerGroup={itemsPerGroup} expected={expected} iterations={iterations} warmups={warmupIterations}");
        PrintVarLenMultiByteRangeCountPerf("VS8", iterations, vs8Count, vs8Reader);
        PrintVarLenMultiByteRangeCountPerf("VS16", iterations, vs16Count, vs16Reader);
        PrintVarLenMultiByteRangeCountPerf("VV", iterations, vvCount, vvReader);
        return 0;
    }


    /// <summary>
    /// Holds one compressed-router range-count performance measurement row.<br/>
    /// The checksum is the repeated sum of observed counts and prevents dead-loop measurements from hiding an incorrect count path.<br/>
    /// </summary>
    /// <param name="Elapsed">The measured elapsed time.<br/></param>
    /// <param name="Checksum">The accumulated count checksum.<br/></param>
    private readonly record struct VarLenMultiByteRangeCountPerfRow(TimeSpan Elapsed, long Checksum);


    /// <summary>
    /// Measures the `VS8` compressed-router range-count primitive loop.<br/>
    /// This path should use the metadata-aware count planner, including compressed-router route classification and narrow boundary shelf counts.<br/>
    /// </summary>
    /// <param name="session">The opened file session.<br/></param>
    /// <param name="rootOffset">The `VS8` root router offset.<br/></param>
    /// <param name="lowerKey">The inclusive lower key.<br/></param>
    /// <param name="upperKey">The inclusive upper key.<br/></param>
    /// <param name="expected">The expected count per iteration.<br/></param>
    /// <param name="iterations">The measured iteration count.<br/></param>
    /// <param name="warmupIterations">The warmup iteration count.<br/></param>
    /// <returns>The measured elapsed time and checksum.<br/></returns>
    private static VarLenMultiByteRangeCountPerfRow MeasureVarKeyScalar8MultiByteRangeCountPrimitive(
        LibraDexFileSession session,
        long rootOffset,
        byte[] lowerKey,
        byte[] upperKey,
        long expected,
        int iterations,
        int warmupIterations)
    {
        return MeasureVarLenMultiByteRangeCountLoop(
            "VS8-count",
            expected,
            iterations,
            warmupIterations,
            () => session.CountVarKeyScalar8IdentityRange(rootOffset, 1024, lowerKey, upperKey));
    }


    /// <summary>
    /// Measures ordinary `VS8` range-reader count loops over the compressed-router proof range.<br/>
    /// This is the baseline for understanding how much work the count planner avoids relative to reader materialization.<br/>
    /// </summary>
    /// <param name="session">The opened file session.<br/></param>
    /// <param name="rootOffset">The `VS8` root router offset.<br/></param>
    /// <param name="lowerKey">The inclusive lower key.<br/></param>
    /// <param name="upperKey">The inclusive upper key.<br/></param>
    /// <param name="expected">The expected count per iteration.<br/></param>
    /// <param name="iterations">The measured iteration count.<br/></param>
    /// <param name="warmupIterations">The warmup iteration count.<br/></param>
    /// <returns>The measured elapsed time and checksum.<br/></returns>
    private static VarLenMultiByteRangeCountPerfRow MeasureVarKeyScalar8MultiByteRangeReaderCount(
        LibraDexFileSession session,
        long rootOffset,
        byte[] lowerKey,
        byte[] upperKey,
        long expected,
        int iterations,
        int warmupIterations)
    {
        return MeasureVarLenMultiByteRangeCountLoop(
            "VS8-reader",
            expected,
            iterations,
            warmupIterations,
            () => CountVarKeyScalar8RangeReader(session, rootOffset, lowerKey, upperKey));
    }


    /// <summary>
    /// Measures the `VS16` compressed-router range-count primitive loop.<br/>
    /// The measurement isolates widened identity metadata counting from range-reader shelf materialization.<br/>
    /// </summary>
    /// <param name="session">The opened file session.<br/></param>
    /// <param name="rootOffset">The `VS16` root router offset.<br/></param>
    /// <param name="lowerKey">The inclusive lower key.<br/></param>
    /// <param name="upperKey">The inclusive upper key.<br/></param>
    /// <param name="expected">The expected count per iteration.<br/></param>
    /// <param name="iterations">The measured iteration count.<br/></param>
    /// <param name="warmupIterations">The warmup iteration count.<br/></param>
    /// <returns>The measured elapsed time and checksum.<br/></returns>
    private static VarLenMultiByteRangeCountPerfRow MeasureVarKeyScalar16MultiByteRangeCountPrimitive(
        LibraDexFileSession session,
        long rootOffset,
        byte[] lowerKey,
        byte[] upperKey,
        long expected,
        int iterations,
        int warmupIterations)
    {
        return MeasureVarLenMultiByteRangeCountLoop(
            "VS16-count",
            expected,
            iterations,
            warmupIterations,
            () => session.CountVarKeyScalar16IdentityRange(rootOffset, 1024, lowerKey, upperKey));
    }


    /// <summary>
    /// Measures ordinary `VS16` range-reader count loops over the compressed-router proof range.<br/>
    /// The baseline includes ordinary range-reader setup and shelf range loading for the same physical tree.<br/>
    /// </summary>
    /// <param name="session">The opened file session.<br/></param>
    /// <param name="rootOffset">The `VS16` root router offset.<br/></param>
    /// <param name="lowerKey">The inclusive lower key.<br/></param>
    /// <param name="upperKey">The inclusive upper key.<br/></param>
    /// <param name="expected">The expected count per iteration.<br/></param>
    /// <param name="iterations">The measured iteration count.<br/></param>
    /// <param name="warmupIterations">The warmup iteration count.<br/></param>
    /// <returns>The measured elapsed time and checksum.<br/></returns>
    private static VarLenMultiByteRangeCountPerfRow MeasureVarKeyScalar16MultiByteRangeReaderCount(
        LibraDexFileSession session,
        long rootOffset,
        byte[] lowerKey,
        byte[] upperKey,
        long expected,
        int iterations,
        int warmupIterations)
    {
        return MeasureVarLenMultiByteRangeCountLoop(
            "VS16-reader",
            expected,
            iterations,
            warmupIterations,
            () => CountVarKeyScalar16RangeReader(session, rootOffset, lowerKey, upperKey));
    }


    /// <summary>
    /// Measures the `VV` compressed-router range-count primitive loop.<br/>
    /// This verifies the varlen-key/varlen-identity shape benefits from the same compressed-router metadata count planner.<br/>
    /// </summary>
    /// <param name="session">The opened file session.<br/></param>
    /// <param name="rootOffset">The `VV` root router offset.<br/></param>
    /// <param name="lowerKey">The inclusive lower key.<br/></param>
    /// <param name="upperKey">The inclusive upper key.<br/></param>
    /// <param name="expected">The expected count per iteration.<br/></param>
    /// <param name="iterations">The measured iteration count.<br/></param>
    /// <param name="warmupIterations">The warmup iteration count.<br/></param>
    /// <returns>The measured elapsed time and checksum.<br/></returns>
    private static VarLenMultiByteRangeCountPerfRow MeasureVarKeyVarIdentityMultiByteRangeCountPrimitive(
        LibraDexFileSession session,
        long rootOffset,
        byte[] lowerKey,
        byte[] upperKey,
        long expected,
        int iterations,
        int warmupIterations)
    {
        return MeasureVarLenMultiByteRangeCountLoop(
            "VV-count",
            expected,
            iterations,
            warmupIterations,
            () => session.CountVarKeyVarIdentityRange(rootOffset, 1024, 1024, lowerKey, upperKey));
    }


    /// <summary>
    /// Measures ordinary `VV` range-reader count loops over the compressed-router proof range.<br/>
    /// The result is the tuple-reader baseline for the same key range and physical compressed router shape.<br/>
    /// </summary>
    /// <param name="session">The opened file session.<br/></param>
    /// <param name="rootOffset">The `VV` root router offset.<br/></param>
    /// <param name="lowerKey">The inclusive lower key.<br/></param>
    /// <param name="upperKey">The inclusive upper key.<br/></param>
    /// <param name="expected">The expected count per iteration.<br/></param>
    /// <param name="iterations">The measured iteration count.<br/></param>
    /// <param name="warmupIterations">The warmup iteration count.<br/></param>
    /// <returns>The measured elapsed time and checksum.<br/></returns>
    private static VarLenMultiByteRangeCountPerfRow MeasureVarKeyVarIdentityMultiByteRangeReaderCount(
        LibraDexFileSession session,
        long rootOffset,
        byte[] lowerKey,
        byte[] upperKey,
        long expected,
        int iterations,
        int warmupIterations)
    {
        return MeasureVarLenMultiByteRangeCountLoop(
            "VV-reader",
            expected,
            iterations,
            warmupIterations,
            () => CountVarKeyVarIdentityRangeReader(session, rootOffset, lowerKey, upperKey));
    }


    /// <summary>
    /// Measures a repeated count-producing delegate with correctness checks before and during timing.<br/>
    /// Warmup calls must also return the expected value so cache warmup cannot mask an incorrect first-call path.<br/>
    /// </summary>
    /// <param name="label">The measurement label used in failure messages.<br/></param>
    /// <param name="expected">The expected count per call.<br/></param>
    /// <param name="iterations">The measured iteration count.<br/></param>
    /// <param name="warmupIterations">The warmup iteration count.<br/></param>
    /// <param name="count">The count-producing delegate to measure.<br/></param>
    /// <returns>The measured elapsed time and checksum.<br/></returns>
    private static VarLenMultiByteRangeCountPerfRow MeasureVarLenMultiByteRangeCountLoop(
        string label,
        long expected,
        int iterations,
        int warmupIterations,
        Func<long> count)
    {
        for (int i = 0; i < warmupIterations; i++)
        {
            long warmup = count();
            if (warmup != expected)
            {
                throw new InvalidDataException($"{label} warmup returned {warmup}; expected {expected}.");
            }
        }

        long checksum = 0;
        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            long value = count();
            if (value != expected)
            {
                throw new InvalidDataException($"{label} iteration {i} returned {value}; expected {expected}.");
            }

            checksum += value;
        }

        watch.Stop();
        return new VarLenMultiByteRangeCountPerfRow(watch.Elapsed, checksum);
    }


    /// <summary>
    /// Prints one shape's compressed-router range-count comparison row.<br/>
    /// The output reports raw elapsed times, operations per second, and the count-path speedup over range-reader counting.<br/>
    /// </summary>
    /// <param name="shape">The shape label being printed.<br/></param>
    /// <param name="iterations">The measured iteration count.<br/></param>
    /// <param name="count">The count-primitive measurement.<br/></param>
    /// <param name="reader">The range-reader count measurement.<br/></param>
    private static void PrintVarLenMultiByteRangeCountPerf(string shape, int iterations, VarLenMultiByteRangeCountPerfRow count, VarLenMultiByteRangeCountPerfRow reader)
    {
        double countMs = count.Elapsed.TotalMilliseconds;
        double readerMs = reader.Elapsed.TotalMilliseconds;
        double speedup = readerMs / Math.Max(countMs, 0.000001D);
        Console.WriteLine(
            $"{shape} countMs={countMs:F3} readerMs={readerMs:F3} speedup={speedup:F2}x " +
            $"countOpsPerSecond={iterations / Math.Max(count.Elapsed.TotalSeconds, 0.000001D):F2} readerOpsPerSecond={iterations / Math.Max(reader.Elapsed.TotalSeconds, 0.000001D):F2} " +
            $"countChecksum={count.Checksum} readerChecksum={reader.Checksum}");
    }


    /// <summary>
    /// Creates deterministic keys for the compressed-router range-count proof.<br/>
    /// All keys share byte zero and a long common stem, while byte ten is the group fanout byte used by the compressed router's final-byte route interval.<br/>
    /// </summary>
    /// <param name="groupCount">The number of compressed-router route groups.<br/></param>
    /// <param name="itemsPerGroup">The number of generated keys per group.<br/></param>
    /// <returns>All generated keys in group-major order.<br/></returns>
    private static byte[][] CreateVarLenMultiByteRangeCountKeys(int groupCount, int itemsPerGroup)
    {
        byte[][] keys = new byte[checked(groupCount * itemsPerGroup)][];
        int ordinal = 0;
        for (int group = 0; group < groupCount; group++)
        {
            for (int item = 0; item < itemsPerGroup; item++)
            {
                keys[ordinal++] = CreateVarLenMultiByteRangeCountKey(group, item);
            }
        }

        return keys;
    }


    /// <summary>
    /// Creates one deterministic key for the compressed-router range-count proof.<br/>
    /// The key is binary rather than textual so the test controls the exact root byte, compressed stem, group fanout byte, and item suffix without collation or encoding ambiguity.<br/>
    /// </summary>
    /// <param name="group">The compressed-router route group.<br/></param>
    /// <param name="item">The item ordinal within the group.<br/></param>
    /// <returns>The generated key bytes.<br/></returns>
    private static byte[] CreateVarLenMultiByteRangeCountKey(int group, int item)
    {
        byte[] key = new byte[16];
        key[0] = 0x43;
        key.AsSpan(1, 9).Fill(0x35);
        key[10] = (byte)group;
        BinaryPrimitives.WriteInt32BigEndian(key.AsSpan(11, sizeof(int)), item);
        key[15] = unchecked((byte)((group * 31 + item * 17) & 0xFF));
        return key;
    }


    /// <summary>
    /// Counts the generated compressed-router proof keys that fall inside an inclusive encoded-key range.<br/>
    /// This is the construction-side oracle; the command separately compares the production count primitive with the normal range reader for each physical shape.<br/>
    /// </summary>
    /// <param name="keys">The generated proof keys.<br/></param>
    /// <param name="lowerKey">The inclusive lower key.<br/></param>
    /// <param name="upperKey">The inclusive upper key.<br/></param>
    /// <returns>The number of generated keys inside the range.<br/></returns>
    private static long CountVarLenMultiByteRangeCountExpected(byte[][] keys, ReadOnlySpan<byte> lowerKey, ReadOnlySpan<byte> upperKey)
    {
        long count = 0;
        for (int i = 0; i < keys.Length; i++)
        {
            ReadOnlySpan<byte> key = keys[i];
            if (key.SequenceCompareTo(lowerKey) >= 0 &&
                key.SequenceCompareTo(upperKey) <= 0)
            {
                count++;
            }
        }

        return count;
    }


    /// <summary>
    /// Builds the `VS8` compressed-router proof tree under an existing root router.<br/>
    /// Each compressed route points at one sorted shelf so range-count traversal must classify the multi-byte route before deciding whether to use shelf metadata or boundary slot counts.<br/>
    /// </summary>
    /// <param name="session">The session receiving the proof tree.<br/></param>
    /// <param name="rootOffset">The root router offset to link.<br/></param>
    /// <param name="groupCount">The number of compressed-router route groups.<br/></param>
    /// <param name="itemsPerGroup">The number of generated keys per group.<br/></param>
    private static void CreateVarKeyScalar8MultiByteRangeCountTree(LibraDexFileSession session, long rootOffset, int groupCount, int itemsPerGroup)
    {
        RouterMultiByteRouteSnapshot[] routes = new RouterMultiByteRouteSnapshot[groupCount];
        byte[] stem = CreateVarLenMultiByteRangeCountStem();
        for (int group = 0; group < groupCount; group++)
        {
            byte[][] keys = new byte[itemsPerGroup][];
            ulong[] identities = new ulong[itemsPerGroup];
            for (int item = 0; item < itemsPerGroup; item++)
            {
                keys[item] = CreateVarLenMultiByteRangeCountKey(group, item);
                identities[item] = CreateVarKeyScalar8Identity((group * itemsPerGroup) + item);
            }

            VarKeyScalar8Profile profile = SelectVarKeyScalar8BuildProfile(keys, identities);
            if (!VarKeyScalar8.TryBuildFromSorted(keys, identities, profile, out byte[] shelfBytes))
            {
                throw new InvalidDataException("The VS8 multi-byte range-count proof shelf could not be built.");
            }

            (long shelfOffset, _) = session.CreateVarKeyScalar8Shelf(profile, shelfBytes);
            routes[group] = new RouterMultiByteRouteSnapshot(stem, (byte)group, (byte)group, shelfOffset);
        }

        LinkVarLenMultiByteRangeCountRouter(session, rootOffset, stem, routes);
    }


    /// <summary>
    /// Builds the `VS16` compressed-router proof tree under an existing root router.<br/>
    /// The key layout matches `VS8`; only the identity payload changes, which keeps range-count routing differences shape-specific.<br/>
    /// </summary>
    /// <param name="session">The session receiving the proof tree.<br/></param>
    /// <param name="rootOffset">The root router offset to link.<br/></param>
    /// <param name="groupCount">The number of compressed-router route groups.<br/></param>
    /// <param name="itemsPerGroup">The number of generated keys per group.<br/></param>
    private static void CreateVarKeyScalar16MultiByteRangeCountTree(LibraDexFileSession session, long rootOffset, int groupCount, int itemsPerGroup)
    {
        RouterMultiByteRouteSnapshot[] routes = new RouterMultiByteRouteSnapshot[groupCount];
        byte[] stem = CreateVarLenMultiByteRangeCountStem();
        for (int group = 0; group < groupCount; group++)
        {
            byte[][] keys = new byte[itemsPerGroup][];
            ulong[] identityHighs = new ulong[itemsPerGroup];
            ulong[] identityLows = new ulong[itemsPerGroup];
            for (int item = 0; item < itemsPerGroup; item++)
            {
                keys[item] = CreateVarLenMultiByteRangeCountKey(group, item);
                CreateVarKeyScalar16Identity((group * itemsPerGroup) + item, out identityHighs[item], out identityLows[item]);
            }

            VarKeyScalar16Profile profile = SelectVarKeyScalar16BuildProfile(keys, identityHighs, identityLows);
            if (!VarKeyScalar16.TryBuildFromSorted(keys, identityHighs, identityLows, profile, out byte[] shelfBytes))
            {
                throw new InvalidDataException("The VS16 multi-byte range-count proof shelf could not be built.");
            }

            (long shelfOffset, _) = session.CreateVarKeyScalar16Shelf(profile, shelfBytes);
            routes[group] = new RouterMultiByteRouteSnapshot(stem, (byte)group, (byte)group, shelfOffset);
        }

        LinkVarLenMultiByteRangeCountRouter(session, rootOffset, stem, routes);
    }


    /// <summary>
    /// Builds the `VV` compressed-router proof tree under an existing root router.<br/>
    /// Variable identities are generated deterministically per item, letting the same key-range oracle validate the varlen-key/varlen-identity count primitive.<br/>
    /// </summary>
    /// <param name="session">The session receiving the proof tree.<br/></param>
    /// <param name="rootOffset">The root router offset to link.<br/></param>
    /// <param name="groupCount">The number of compressed-router route groups.<br/></param>
    /// <param name="itemsPerGroup">The number of generated keys per group.<br/></param>
    /// <param name="identityLength">The generated variable identity length.<br/></param>
    private static void CreateVarKeyVarIdentityMultiByteRangeCountTree(LibraDexFileSession session, long rootOffset, int groupCount, int itemsPerGroup, int identityLength)
    {
        RouterMultiByteRouteSnapshot[] routes = new RouterMultiByteRouteSnapshot[groupCount];
        byte[] stem = CreateVarLenMultiByteRangeCountStem();
        for (int group = 0; group < groupCount; group++)
        {
            byte[][] keys = new byte[itemsPerGroup][];
            byte[][] identities = new byte[itemsPerGroup][];
            for (int item = 0; item < itemsPerGroup; item++)
            {
                int ordinal = (group * itemsPerGroup) + item;
                keys[item] = CreateVarLenMultiByteRangeCountKey(group, item);
                identities[item] = CreateScalar8VarIdentity(ordinal, identityLength);
            }

            VarKeyVarIdentityProfile profile = SelectVarKeyVarIdentityBuildProfile(keys, identities, VarKeyVarIdentityProfile.DefaultInitial);
            if (!VarKeyVarIdentity.TryBuildFromSorted(keys, identities, profile, out byte[] shelfBytes))
            {
                throw new InvalidDataException("The VV multi-byte range-count proof shelf could not be built.");
            }

            (long shelfOffset, _) = session.CreateVarKeyVarIdentityShelf(profile, shelfBytes);
            routes[group] = new RouterMultiByteRouteSnapshot(stem, (byte)group, (byte)group, shelfOffset);
        }

        LinkVarLenMultiByteRangeCountRouter(session, rootOffset, stem, routes);
    }


    /// <summary>
    /// Creates the common compressed-router stem used by the multi-byte range-count proof.<br/>
    /// The root router consumes byte zero, this stem consumes bytes one through nine, and the compressed route final byte consumes the group byte at depth ten.<br/>
    /// </summary>
    /// <returns>The shared compressed-router stem.<br/></returns>
    private static byte[] CreateVarLenMultiByteRangeCountStem()
    {
        byte[] stem = new byte[9];
        stem.AsSpan().Fill(0x35);
        return stem;
    }


    /// <summary>
    /// Creates and links the compressed proof router under byte-zero root route 0x43.<br/>
    /// The helper centralizes the physical router shape so `VS8`, `VS16`, and `VV` proofs differ only in shelf payload format.<br/>
    /// </summary>
    /// <param name="session">The session receiving the router.<br/></param>
    /// <param name="rootOffset">The root router offset to link.<br/></param>
    /// <param name="stem">The compressed-router stem bytes.<br/></param>
    /// <param name="routes">The compressed-router route snapshots.<br/></param>
    private static void LinkVarLenMultiByteRangeCountRouter(LibraDexFileSession session, long rootOffset, byte[] stem, RouterMultiByteRouteSnapshot[] routes)
    {
        (RouterSnapshot router, _) = session.CreateCompressedMultiByteRouter(
            checked((byte)(stem.Length + 1)),
            keyDepth: 1,
            maxRouteCount: checked((ushort)routes.Length),
            allocationClassId: 0,
            routes);
        _ = session.UpdateRouterRoute(rootOffset, 0x43, 0x43, 0x43, router.Offset);
    }


    /// <summary>
    /// Verifies one compressed-router range-count result against both generated expectation and range-reader parity.<br/>
    /// Separate expected and reader comparisons make failures distinguish construction mistakes from count-planner regressions.<br/>
    /// </summary>
    /// <param name="shape">The shape label being validated.<br/></param>
    /// <param name="expected">The construction-side expected count.<br/></param>
    /// <param name="rangeCount">The production range-count primitive result.<br/></param>
    /// <param name="readerCount">The normal range-reader result.<br/></param>
    private static void ValidateVarLenMultiByteRangeCountShape(string shape, long expected, long rangeCount, long readerCount)
    {
        if (rangeCount != expected)
        {
            throw new InvalidDataException($"{shape} compressed-router range count returned {rangeCount}; expected {expected}.");
        }

        if (readerCount != rangeCount)
        {
            throw new InvalidDataException($"{shape} compressed-router reader count returned {readerCount}; range count returned {rangeCount}.");
        }
    }


    /// <summary>
    /// Counts a `VS8` raw range reader over caller-supplied encoded bounds.<br/>
    /// This overload exists for proofs where the range is not the harness's ordinary first-byte prefix span.<br/>
    /// </summary>
    /// <param name="session">The opened file session.<br/></param>
    /// <param name="rootOffset">The root router offset for the `VS8` index.<br/></param>
    /// <param name="lowerKey">The inclusive lower key.<br/></param>
    /// <param name="upperKey">The inclusive upper key.<br/></param>
    /// <returns>The number of rows observed by the range reader.<br/></returns>
    private static long CountVarKeyScalar8RangeReader(LibraDexFileSession session, long rootOffset, ReadOnlySpan<byte> lowerKey, ReadOnlySpan<byte> upperKey)
    {
        using VarKeyScalar8RangeReader reader = session.OpenVarKeyScalar8RangeReader(rootOffset, 1024, lowerKey, upperKey);
        return reader.Count;
    }


    /// <summary>
    /// Counts a `VS16` raw range reader over caller-supplied encoded bounds.<br/>
    /// This keeps compressed-router count proof validation independent from the generated first-byte prefix helper ranges.<br/>
    /// </summary>
    /// <param name="session">The opened file session.<br/></param>
    /// <param name="rootOffset">The root router offset for the `VS16` index.<br/></param>
    /// <param name="lowerKey">The inclusive lower key.<br/></param>
    /// <param name="upperKey">The inclusive upper key.<br/></param>
    /// <returns>The number of rows observed by the range reader.<br/></returns>
    private static long CountVarKeyScalar16RangeReader(LibraDexFileSession session, long rootOffset, ReadOnlySpan<byte> lowerKey, ReadOnlySpan<byte> upperKey)
    {
        using VarKeyScalar16RangeReader reader = session.OpenVarKeyScalar16RangeReader(rootOffset, 1024, lowerKey, upperKey);
        return reader.Count;
    }


    /// <summary>
    /// Counts a `VV` raw range reader over caller-supplied encoded bounds.<br/>
    /// The proof uses this as the tuple-reader oracle for compressed-router range-count traversal.<br/>
    /// </summary>
    /// <param name="session">The opened file session.<br/></param>
    /// <param name="rootOffset">The root router offset for the `VV` index.<br/></param>
    /// <param name="lowerKey">The inclusive lower key.<br/></param>
    /// <param name="upperKey">The inclusive upper key.<br/></param>
    /// <returns>The number of rows observed by the range reader.<br/></returns>
    private static long CountVarKeyVarIdentityRangeReader(LibraDexFileSession session, long rootOffset, ReadOnlySpan<byte> lowerKey, ReadOnlySpan<byte> upperKey)
    {
        using VarKeyVarIdentityRangeReader reader = session.OpenVarKeyVarIdentityRangeReader(rootOffset, 1024, 1024, lowerKey, upperKey);
        return reader.Count;
    }


    /// <summary>
    /// Validates routed count-all metadata traversal for each variable-length index shelf family.<br/>
    /// The command builds `VS8`, `VS16`, `SV8`, `SV16`, and `VV` populations through their normal walked routed insert paths, reopens the file, and compares the count-specific primitive against a full-range reader count for each shape.<br/>
    /// It deliberately avoids delete/update paths so failures stay scoped to count-all route traversal, shelf-header item counts, and reader parity.<br/>
    /// </summary>
    /// <param name="args">The harness command-line arguments.<br/></param>
    /// <returns>Zero when all variable-length count-all primitives match reader enumeration.<br/></returns>
    private static int RunVarLenCountAllSanity(string[] args)
    {
        string path = GetOption(args, "--path", Path.Combine(@"T:\LibraDex", "varlen-count-all-sanity.lbdx"));
        int itemCount = GetIntOption(args, "--items", 512);
        int keyLength = GetIntOption(args, "--key-length", 48);
        int identityLength = GetIntOption(args, "--identity-length", 48);
        int prefixCount = GetIntOption(args, "--prefix-count", 16);
        int duplicateModulo = GetIntOption(args, "--duplicate-modulo", 16);
        if (itemCount <= 0 ||
            keyLength <= 0 ||
            keyLength > 1024 ||
            identityLength <= 0 ||
            identityLength > 1024 ||
            prefixCount <= 0 ||
            prefixCount > 256 ||
            duplicateModulo <= 0 ||
            duplicateModulo > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(args), "Varlen count-all sanity requires positive items, key length 1-1024, identity length 1-1024, prefix count 1-256, and duplicate modulo 1-256.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.Delete(path);
        DataKernelOptions options = CreateDesignPerfOptions();
        long vs8RootOffset;
        long vs16RootOffset;
        long sv8RootOffset;
        long sv16RootOffset;
        long vvRootOffset;
        using (LibraDexFileSession session = LibraDexFileSession.Initialize(path, options, CreateDesignPerfMetadata(9181), DataKernelTelemetryOptions.EnabledOptions))
        {
            (RouterSnapshot vs8Root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "vacvs8", 0));
            vs8RootOffset = vs8Root.Offset;
            for (int prefix = 0; prefix < prefixCount; prefix++)
            {
                _ = session.CreateVarKeyScalar8ShelfAndLinkRootRoute(vs8RootOffset, (byte)prefix, VarKeyScalar8Profile.DefaultInitial);
            }

            (RouterSnapshot vs16Root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(1, "vacvs16", 0));
            vs16RootOffset = vs16Root.Offset;
            for (int prefix = 0; prefix < prefixCount; prefix++)
            {
                _ = session.CreateVarKeyScalar16ShelfAndLinkRootRoute(vs16RootOffset, (byte)prefix, VarKeyScalar16Profile.DefaultInitial);
            }

            (RouterSnapshot sv8Root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(2, "vacsv8", 0));
            sv8RootOffset = sv8Root.Offset;
            _ = session.CreateScalar8VarIdentityShelfAndLinkRootRoute(sv8RootOffset, 0, Scalar8VarIdentityProfile.DefaultInitial);

            (RouterSnapshot sv16Root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(3, "vacsv16", 0));
            sv16RootOffset = sv16Root.Offset;
            _ = session.CreateScalar16VarIdentityShelfAndLinkRootRoute(sv16RootOffset, 0, Scalar16VarIdentityProfile.DefaultInitial);

            (RouterSnapshot vvRoot, _) = session.CreateRootRouterIndex(CreateHarnessSlot(4, "vacvv", 0));
            vvRootOffset = vvRoot.Offset;
            for (int prefix = 0; prefix < prefixCount; prefix++)
            {
                _ = session.CreateVarKeyVarIdentityShelfAndLinkRootRoute(vvRootOffset, (byte)prefix, VarKeyVarIdentityProfile.DefaultInitial);
            }

            using LibraDexFileSessionDurabilityBatch batch = session.BeginDurabilityBatch();
            for (int i = 0; i < itemCount; i++)
            {
                byte[] key = CreateVarKeyScalar8Key(i, keyLength, prefixCount);
                VarKeyScalar8RoutedInsertResult vs8Result = session.InsertWalkedRoutedVarKeyScalar8(vs8RootOffset, 1024, key, CreateVarKeyScalar8Identity(i), allowDuplicateKeys: true, maxRouterHops: 8);
                if (vs8Result.InsertResult != VarKeyScalar8InsertResult.Inserted)
                {
                    throw new InvalidDataException($"Expected count-all VS8 insert {i}, got {vs8Result.Kind}/{vs8Result.InsertResult}.");
                }

                CreateVarKeyScalar16Identity(i, out ulong identityHigh, out ulong identityLow);
                VarKeyScalar16RoutedInsertResult vs16Result = session.InsertWalkedRoutedVarKeyScalar16(vs16RootOffset, 1024, key, identityHigh, identityLow, allowDuplicateKeys: true, maxRouterHops: 8);
                if (vs16Result.InsertResult != VarKeyScalar16InsertResult.Inserted)
                {
                    throw new InvalidDataException($"Expected count-all VS16 insert {i}, got {vs16Result.Kind}/{vs16Result.InsertResult}.");
                }

                ulong sv8Key = CreateScalar8VarIdentityKey(i, duplicateModulo);
                byte[] identity = CreateScalar8VarIdentity(i, identityLength);
                Scalar8VarIdentityRoutedInsertResult sv8Result = session.InsertWalkedRoutedScalar8VarIdentity(sv8RootOffset, identityLength, sv8Key, identity, allowDuplicateKeys: true, maxRouterHops: 8);
                if (sv8Result.InsertResult != Scalar8VarIdentityInsertResult.Inserted)
                {
                    throw new InvalidDataException($"Expected count-all SV8 insert {i}, got {sv8Result.Kind}/{sv8Result.InsertResult}.");
                }

                CreateScalar16VarIdentityKey(i, duplicateModulo, "low", out ulong sv16KeyHigh, out ulong sv16KeyLow);
                Scalar16VarIdentityRoutedInsertResult sv16Result = session.InsertWalkedRoutedScalar16VarIdentity(sv16RootOffset, identityLength, sv16KeyHigh, sv16KeyLow, identity, allowDuplicateKeys: true, maxRouterHops: 16);
                if (sv16Result.InsertResult != Scalar16VarIdentityInsertResult.Inserted)
                {
                    throw new InvalidDataException($"Expected count-all SV16 insert {i}, got {sv16Result.Kind}/{sv16Result.InsertResult}.");
                }

                VarKeyVarIdentityRoutedInsertResult vvResult = session.InsertWalkedRoutedVarKeyVarIdentity(vvRootOffset, 1024, 1024, key, identity, allowDuplicateKeys: true, maxRouterHops: 8);
                if (vvResult.InsertResult != VarKeyVarIdentityInsertResult.Inserted)
                {
                    throw new InvalidDataException($"Expected count-all VV insert {i}, got {vvResult.Kind}/{vvResult.InsertResult}.");
                }
            }

            _ = batch.Commit();
        }

        using LibraDexFileSession reopened = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions);
        ValidateVarLenCountAllSanity("VS8", itemCount, reopened.CountVarKeyScalar8Identities(vs8RootOffset, 1024), CountVarKeyScalar8RangeReader(reopened, vs8RootOffset, keyLength, prefixCount));
        ValidateVarLenCountAllSanity("VS16", itemCount, reopened.CountVarKeyScalar16Identities(vs16RootOffset, 1024), CountVarKeyScalar16RangeReader(reopened, vs16RootOffset, keyLength, prefixCount));
        ValidateVarLenCountAllSanity("SV8", itemCount, reopened.CountScalar8VarIdentityIdentities(sv8RootOffset, identityLength), CountScalar8VarIdentityRangeReader(reopened, sv8RootOffset, identityLength, duplicateModulo));
        ValidateVarLenCountAllSanity("SV16", itemCount, reopened.CountScalar16VarIdentityIdentities(sv16RootOffset, identityLength), CountScalar16VarIdentityRangeReader(reopened, sv16RootOffset, identityLength, duplicateModulo));
        ValidateVarLenCountAllSanity("VV", itemCount, reopened.CountVarKeyVarIdentityIdentities(vvRootOffset, 1024, 1024), CountVarKeyVarIdentityRangeReader(reopened, vvRootOffset, keyLength, prefixCount));

        byte[] varLower = CreateVarKeyScalar8PrefixLower(0, keyLength);
        byte[] varUpper = CreateVarKeyScalar8PrefixUpper(prefixCount - 1, keyLength);
        ValidateVarLenRangeCountSanity("VS8", reopened.CountVarKeyScalar8IdentityRange(vs8RootOffset, 1024, varLower, varUpper), CountVarKeyScalar8RangeReader(reopened, vs8RootOffset, keyLength, prefixCount));
        ValidateVarLenRangeCountSanity("VS16", reopened.CountVarKeyScalar16IdentityRange(vs16RootOffset, 1024, varLower, varUpper), CountVarKeyScalar16RangeReader(reopened, vs16RootOffset, keyLength, prefixCount));
        ValidateVarLenRangeCountSanity("VV", reopened.CountVarKeyVarIdentityRange(vvRootOffset, 1024, 1024, varLower, varUpper), CountVarKeyVarIdentityRangeReader(reopened, vvRootOffset, keyLength, prefixCount));

        ValidateVarLenPrefixCountSanity(reopened, vs8RootOffset, vs16RootOffset, vvRootOffset, itemCount, keyLength, prefixCount, 0);
        ValidateVarLenPrefixCountSanity(reopened, vs8RootOffset, vs16RootOffset, vvRootOffset, itemCount, keyLength, prefixCount, prefixCount / 2);
        ValidateVarLenPrefixCountSanity(reopened, vs8RootOffset, vs16RootOffset, vvRootOffset, itemCount, keyLength, prefixCount, prefixCount - 1);

        ulong sv8Lower = CreateScalar8VarIdentityKey(0, duplicateModulo);
        ulong sv8Upper = CreateScalar8VarIdentityKey(duplicateModulo - 1, duplicateModulo);
        ValidateVarLenRangeCountSanity("SV8", reopened.CountScalar8VarIdentityRange(sv8RootOffset, identityLength, sv8Lower, sv8Upper), CountScalar8VarIdentityRangeReader(reopened, sv8RootOffset, identityLength, duplicateModulo));

        CreateScalar16VarIdentityKey(0, duplicateModulo, "low", out ulong sv16LowerHigh, out ulong sv16LowerLow);
        CreateScalar16VarIdentityKey(duplicateModulo - 1, duplicateModulo, "low", out ulong sv16UpperHigh, out ulong sv16UpperLow);
        ValidateVarLenRangeCountSanity("SV16", reopened.CountScalar16VarIdentityRange(sv16RootOffset, identityLength, sv16LowerHigh, sv16LowerLow, sv16UpperHigh, sv16UpperLow), CountScalar16VarIdentityRangeReader(reopened, sv16RootOffset, identityLength, duplicateModulo));

        Console.WriteLine($"varlen-count-all-sanity ok path={path} items={itemCount} keyLength={keyLength} identityLength={identityLength} prefixCount={prefixCount} duplicateModulo={duplicateModulo}");
        return 0;
    }


    /// <summary>
    /// Verifies one variable-length count-all result against its expected population and reader parity count.<br/>
    /// This keeps the command failure messages shape-specific without duplicating the same comparison policy across all five shapes.<br/>
    /// </summary>
    /// <param name="shape">The shape label being validated.<br/></param>
    /// <param name="expected">The generated population size expected for the count-all path.<br/></param>
    /// <param name="countAll">The count returned by the metadata traversal primitive.<br/></param>
    /// <param name="readerCount">The count returned by full-range reader enumeration.<br/></param>
    private static void ValidateVarLenCountAllSanity(string shape, long expected, long countAll, long readerCount)
    {
        if (countAll != expected)
        {
            throw new InvalidDataException($"{shape} count-all returned {countAll}; expected {expected}.");
        }

        if (readerCount != countAll)
        {
            throw new InvalidDataException($"{shape} reader count returned {readerCount}; count-all returned {countAll}.");
        }
    }


    /// <summary>
    /// Verifies one variable-length range-count result against the established range-reader count.<br/>
    /// The harness uses reader parity as the correctness oracle while the production range-count primitive is free to use shelf metadata for fully covered route targets.<br/>
    /// </summary>
    /// <param name="shape">The shape label being validated.<br/></param>
    /// <param name="rangeCount">The count returned by the metadata-aware range-count primitive.<br/></param>
    /// <param name="readerCount">The count returned by the corresponding range reader.<br/></param>
    private static void ValidateVarLenRangeCountSanity(string shape, long rangeCount, long readerCount)
    {
        if (rangeCount != readerCount)
        {
            throw new InvalidDataException($"{shape} range-count returned {rangeCount}; reader count returned {readerCount}.");
        }
    }


    /// <summary>
    /// Verifies variable-key prefix-count primitives for the three key-variable shapes that expose prefix dispatch.<br/>
    /// The generated data uses the first key byte as a stable prefix bucket, so the expected count can be computed directly while range-reader parity proves the same extent against normal ordered enumeration.<br/>
    /// </summary>
    /// <param name="session">The opened file session.<br/></param>
    /// <param name="vs8RootOffset">The root router offset for the `VS8` index.<br/></param>
    /// <param name="vs16RootOffset">The root router offset for the `VS16` index.<br/></param>
    /// <param name="vvRootOffset">The root router offset for the `VV` index.<br/></param>
    /// <param name="itemCount">The generated population size.<br/></param>
    /// <param name="keyLength">The generated key length.<br/></param>
    /// <param name="prefixCount">The number of first-byte prefix buckets.<br/></param>
    /// <param name="prefix">The first-byte prefix bucket to verify.<br/></param>
    private static void ValidateVarLenPrefixCountSanity(
        LibraDexFileSession session,
        long vs8RootOffset,
        long vs16RootOffset,
        long vvRootOffset,
        int itemCount,
        int keyLength,
        int prefixCount,
        int prefix)
    {
        long expected = CountVarKeyScalar8PrefixPopulation(itemCount, prefixCount, prefix);
        byte[] encodedPrefix = { (byte)prefix };
        byte[] lower = CreateVarKeyScalar8PrefixLower(prefix, keyLength);
        byte[] upper = CreateVarKeyScalar8PrefixUpper(prefix, keyLength);

        using VarKeyScalar8RangeReader vs8Reader = session.OpenVarKeyScalar8RangeReader(vs8RootOffset, 1024, lower, upper);
        ValidateVarLenPrefixCountSanity("VS8", prefix, expected, session.CountVarKeyScalar8IdentityPrefix(vs8RootOffset, 1024, encodedPrefix), vs8Reader.Count);

        using VarKeyScalar16RangeReader vs16Reader = session.OpenVarKeyScalar16RangeReader(vs16RootOffset, 1024, lower, upper);
        ValidateVarLenPrefixCountSanity("VS16", prefix, expected, session.CountVarKeyScalar16IdentityPrefix(vs16RootOffset, 1024, encodedPrefix), vs16Reader.Count);

        using VarKeyVarIdentityRangeReader vvReader = session.OpenVarKeyVarIdentityRangeReader(vvRootOffset, 1024, 1024, lower, upper);
        ValidateVarLenPrefixCountSanity("VV", prefix, expected, session.CountVarKeyVarIdentityPrefix(vvRootOffset, 1024, 1024, encodedPrefix), vvReader.Count);
    }


    /// <summary>
    /// Verifies one variable-key prefix-count result against both the generated bucket size and range-reader parity.<br/>
    /// Keeping the generated-count and reader-count checks separate makes failures identify whether routing skipped data or the deterministic setup changed unexpectedly.<br/>
    /// </summary>
    /// <param name="shape">The shape label being validated.<br/></param>
    /// <param name="prefix">The first-byte prefix bucket being validated.<br/></param>
    /// <param name="expected">The generated population count for the prefix bucket.<br/></param>
    /// <param name="prefixCount">The count returned by the prefix-count primitive.<br/></param>
    /// <param name="readerCount">The count returned by ordered range-reader enumeration over the same prefix bounds.<br/></param>
    private static void ValidateVarLenPrefixCountSanity(string shape, int prefix, long expected, long prefixCount, long readerCount)
    {
        if (prefixCount != expected)
        {
            throw new InvalidDataException($"{shape} prefix-count for prefix {prefix} returned {prefixCount}; expected {expected}.");
        }

        if (readerCount != prefixCount)
        {
            throw new InvalidDataException($"{shape} prefix reader count for prefix {prefix} returned {readerCount}; prefix-count returned {prefixCount}.");
        }
    }


    /// <summary>
    /// Computes the deterministic generated population for one first-byte variable-key prefix bucket.<br/>
    /// Generated keys assign `index % prefixCount` to byte zero, so this remains independent of shelf splits, route promotion, and router shape.<br/>
    /// </summary>
    /// <param name="itemCount">The generated population size.<br/></param>
    /// <param name="prefixCount">The number of first-byte prefix buckets.<br/></param>
    /// <param name="prefix">The first-byte prefix bucket being counted.<br/></param>
    /// <returns>The number of generated keys whose first byte equals <paramref name="prefix"/>.<br/></returns>
    private static long CountVarKeyScalar8PrefixPopulation(int itemCount, int prefixCount, int prefix)
    {
        if (prefix < 0 ||
            prefix >= prefixCount)
        {
            return 0;
        }

        if (itemCount <= prefix)
        {
            return 0;
        }

        return ((itemCount - 1 - prefix) / prefixCount) + 1;
    }


    /// <summary>
    /// Counts a full deterministic `VS8` population through the ordinary range reader.<br/>
    /// The lower and upper bounds cover every generated first-byte prefix, giving a reader parity count without using the count-all primitive.<br/>
    /// </summary>
    /// <param name="session">The opened file session.<br/></param>
    /// <param name="rootOffset">The root router offset for the `VS8` index.<br/></param>
    /// <param name="keyLength">The generated key length.<br/></param>
    /// <param name="prefixCount">The number of generated first-byte prefixes.<br/></param>
    /// <returns>The number of rows observed by the range reader.<br/></returns>
    private static long CountVarKeyScalar8RangeReader(LibraDexFileSession session, long rootOffset, int keyLength, int prefixCount)
    {
        byte[] lower = CreateVarKeyScalar8PrefixLower(0, keyLength);
        byte[] upper = CreateVarKeyScalar8PrefixUpper(prefixCount - 1, keyLength);
        using VarKeyScalar8RangeReader reader = session.OpenVarKeyScalar8RangeReader(rootOffset, 1024, lower, upper);
        return reader.Count;
    }


    /// <summary>
    /// Counts a full deterministic `VS16` population through the ordinary range reader.<br/>
    /// This is the 16-byte identity counterpart to the `VS8` reader parity helper.<br/>
    /// </summary>
    /// <param name="session">The opened file session.<br/></param>
    /// <param name="rootOffset">The root router offset for the `VS16` index.<br/></param>
    /// <param name="keyLength">The generated key length.<br/></param>
    /// <param name="prefixCount">The number of generated first-byte prefixes.<br/></param>
    /// <returns>The number of rows observed by the range reader.<br/></returns>
    private static long CountVarKeyScalar16RangeReader(LibraDexFileSession session, long rootOffset, int keyLength, int prefixCount)
    {
        byte[] lower = CreateVarKeyScalar8PrefixLower(0, keyLength);
        byte[] upper = CreateVarKeyScalar8PrefixUpper(prefixCount - 1, keyLength);
        using VarKeyScalar16RangeReader reader = session.OpenVarKeyScalar16RangeReader(rootOffset, 1024, lower, upper);
        return reader.Count;
    }


    /// <summary>
    /// Counts a full deterministic `SV8` population through the ordinary scalar-key range reader.<br/>
    /// The bounds cover the complete generated duplicate-key modulo so the reader count should equal the inserted population.<br/>
    /// </summary>
    /// <param name="session">The opened file session.<br/></param>
    /// <param name="rootOffset">The root router offset for the `SV8` index.<br/></param>
    /// <param name="identityLength">The maximum generated identity length.<br/></param>
    /// <param name="duplicateModulo">The scalar-key duplicate modulo used by generation.<br/></param>
    /// <returns>The number of rows observed by the range reader.<br/></returns>
    private static long CountScalar8VarIdentityRangeReader(LibraDexFileSession session, long rootOffset, int identityLength, int duplicateModulo)
    {
        ulong lower = CreateScalar8VarIdentityKey(0, duplicateModulo);
        ulong upper = CreateScalar8VarIdentityKey(duplicateModulo - 1, duplicateModulo);
        using Scalar8VarIdentityRangeReader reader = session.OpenScalar8VarIdentityRangeReader(rootOffset, identityLength, lower, upper);
        return reader.Count;
    }


    /// <summary>
    /// Counts a full deterministic `SV16` population through the ordinary scalar-key range reader.<br/>
    /// The low-distribution key generator keeps the range compact while still exercising the 16-byte scalar-key reader.<br/>
    /// </summary>
    /// <param name="session">The opened file session.<br/></param>
    /// <param name="rootOffset">The root router offset for the `SV16` index.<br/></param>
    /// <param name="identityLength">The maximum generated identity length.<br/></param>
    /// <param name="duplicateModulo">The scalar-key duplicate modulo used by generation.<br/></param>
    /// <returns>The number of rows observed by the range reader.<br/></returns>
    private static long CountScalar16VarIdentityRangeReader(LibraDexFileSession session, long rootOffset, int identityLength, int duplicateModulo)
    {
        CreateScalar16VarIdentityKey(0, duplicateModulo, "low", out ulong lowerHigh, out ulong lowerLow);
        CreateScalar16VarIdentityKey(duplicateModulo - 1, duplicateModulo, "low", out ulong upperHigh, out ulong upperLow);
        using Scalar16VarIdentityRangeReader reader = session.OpenScalar16VarIdentityRangeReader(rootOffset, identityLength, lowerHigh, lowerLow, upperHigh, upperLow);
        return reader.Count;
    }


    /// <summary>
    /// Counts a full deterministic `VV` population through the ordinary range reader.<br/>
    /// The lower and upper bounds cover every generated first-byte prefix, giving tuple-reader parity without using count-all metadata traversal.<br/>
    /// </summary>
    /// <param name="session">The opened file session.<br/></param>
    /// <param name="rootOffset">The root router offset for the `VV` index.<br/></param>
    /// <param name="keyLength">The generated key length.<br/></param>
    /// <param name="prefixCount">The number of generated first-byte prefixes.<br/></param>
    /// <returns>The number of rows observed by the range reader.<br/></returns>
    private static long CountVarKeyVarIdentityRangeReader(LibraDexFileSession session, long rootOffset, int keyLength, int prefixCount)
    {
        byte[] lower = CreateVarKeyScalar8PrefixLower(0, keyLength);
        byte[] upper = CreateVarKeyScalar8PrefixUpper(prefixCount - 1, keyLength);
        using VarKeyVarIdentityRangeReader reader = session.OpenVarKeyVarIdentityRangeReader(rootOffset, 1024, 1024, lower, upper);
        return reader.Count;
    }


    private static void FillPattern(Span<byte> buffer, int seed)
    {
        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] = unchecked((byte)((i * 31 + seed * 17) & 0xFF));
        }
    }


    private static void InsertFixedNVarIdentityRoutedItems(FixedNVarIdentityIndex index, int keySize, int itemCount)
    {
        for (int i = itemCount - 1; i >= 0; i--)
        {
            byte[] key = CreateFixedNTestKey(keySize, i);
            byte[] identity = Encoding.UTF8.GetBytes(FormattableString.Invariant($"identity-{i:D6}"));
            (FixedNVarIdentityInsertResult result, _) = index.Insert(key, identity, allowDuplicateKeys: true);
            if (result != FixedNVarIdentityInsertResult.Inserted)
            {
                throw new InvalidDataException($"FV routed insert {i} failed with {result}.");
            }
        }
    }


    private static void ValidateFixedNVarIdentityRoutedRange(FixedNVarIdentityIndex index, int keySize, int itemCount)
    {
        int lower = itemCount / 4;
        int upper = (itemCount * 3) / 4;
        IReadOnlyList<byte[]> identities = index.ReadIdentityRange(CreateFixedNTestKey(keySize, lower), CreateFixedNTestKey(keySize, upper));
        int expectedCount = upper - lower + 1;
        if (identities.Count != expectedCount)
        {
            throw new InvalidDataException($"FV routed range expected {expectedCount} identities, got {identities.Count}.");
        }

        for (int i = 0; i < identities.Count; i++)
        {
            string expected = FormattableString.Invariant($"identity-{lower + i:D6}");
            if (Encoding.UTF8.GetString(identities[i]) != expected)
            {
                throw new InvalidDataException($"FV routed range identity {i} was not sorted as expected.");
            }
        }
    }


    private static byte[] CreateFixedNTestKey(int keySize, int value)
    {
        byte[] key = new byte[keySize];
        BinaryPrimitives.WriteInt32BigEndian(key.AsSpan(keySize - sizeof(int), sizeof(int)), value);
        return key;
    }

}
