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
    /// Measures public condition-builder ordered counting over a generic `SS8-8` scalar index.<br/>
    /// Keys are spread across high encoded root-prefix bytes so the benchmark exercises boundary range counts plus count-all metadata traversal for fully covered middle route targets.<br/>
    /// The command routes through `.AsUInt64.Between(...).EndCondition.Count(...)` to prove the public generic scalar aggregate dispatch, not the raw session API.<br/>
    /// </summary>
    /// <param name="args">Harness command arguments.<br/></param>
    /// <returns>Zero when the measured count matches the seeded expectation; otherwise an exception is thrown.<br/></returns>
    private static int RunGenericScalar8BetweenCountProof(string[] args)
    {
        int itemCount = GetIntOption(args, "--items", 250_000);
        int iterations = GetIntOption(args, "--iterations", 25_000);
        int warmupIterations = GetIntOption(args, "--warmup-iterations", 3);
        int prefixGroupCount = GetIntOption(args, "--prefix-groups", 256);
        int lowerPrefix = GetIntOption(args, "--lower-prefix", 64);
        int upperPrefix = GetIntOption(args, "--upper-prefix", 192);

        if (itemCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(args), itemCount, "Generic scalar-8 between count proof item count must be positive.");
        }

        if (iterations <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(args), iterations, "Generic scalar-8 between count proof iterations must be positive.");
        }

        if (warmupIterations < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(args), warmupIterations, "Generic scalar-8 between count proof warmup iterations cannot be negative.");
        }

        if (prefixGroupCount <= 0 || prefixGroupCount > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(args), prefixGroupCount, "Generic scalar-8 between count proof prefix group count must be from 1 through 256.");
        }

        if ((uint)lowerPrefix > byte.MaxValue || (uint)upperPrefix > byte.MaxValue || lowerPrefix > upperPrefix)
        {
            throw new ArgumentOutOfRangeException(nameof(args), $"{lowerPrefix}..{upperPrefix}", "Generic scalar-8 between count proof prefixes must be an increasing byte range.");
        }

        using Catalog catalog = Catalog.CreateMemory();
        using LibraDexIndex<ulong, ulong> index = catalog.Indexes["generic-scalar8-between-count-proof"]["value"].Scalar.Scalar<ulong, ulong>().Create();

        ulong lowerKey = CreateGenericScalar8BetweenCountProofKey(lowerPrefix, 0);
        ulong upperKey = CreateGenericScalar8BetweenCountProofKey(upperPrefix, uint.MaxValue);
        long expectedCount = 0;
        for (int i = 0; i < itemCount; i++)
        {
            int prefix = i % prefixGroupCount;
            ulong key = CreateGenericScalar8BetweenCountProofKey(prefix, (uint)i);
            _ = index.Insert(key, (ulong)(i + 1));
            if (key >= lowerKey && key <= upperKey)
            {
                expectedCount++;
            }
        }

        LibraDexConditionEndCondition condition = LibraDexCondition
            .ForGroup("generic-scalar8-between-count-proof")
            .Index("value")
            .AsUInt64
            .Between(lowerKey, upperKey)
            .EndCondition;
        Func<string, IIndex> resolver = name => name == "value" ? index : throw new KeyNotFoundException(name);

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
            throw new InvalidDataException($"Generic scalar-8 between count proof warmup returned {warmupCount}; expected {expectedCount}.");
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
            throw new InvalidDataException($"Generic scalar-8 between count proof checksum returned {checksum}; expected {expectedChecksum}.");
        }

        double elapsedMs = watch.Elapsed.TotalMilliseconds;
        double opsPerSecond = iterations / Math.Max(watch.Elapsed.TotalSeconds, 0.000001D);
        Console.WriteLine(
            "generic-scalar8-between-count-proof ok " +
            $"items={itemCount} prefixGroups={prefixGroupCount} lowerPrefix={lowerPrefix} upperPrefix={upperPrefix} " +
            $"expected={expectedCount} warmup={warmupCount} iterations={iterations} elapsedMs={elapsedMs:F3} opsPerSecond={opsPerSecond:F2} checksum={checksum}");
        return 0;
    }

    /// <summary>
    /// Creates one public UInt64 key whose encoded scalar-8 order preserves the supplied high route prefix.<br/>
    /// UInt64 keys are already sortable as-is, so the top byte directly selects the root-prefix route in `SS8-8` storage.<br/>
    /// </summary>
    /// <param name="prefix">The desired high route prefix byte.<br/></param>
    /// <param name="ordinal">The low key ordinal used to keep seeded keys unique.<br/></param>
    /// <returns>The public UInt64 key.</returns>
    private static ulong CreateGenericScalar8BetweenCountProofKey(int prefix, uint ordinal)
    {
        return ((ulong)(byte)prefix << 56) | ordinal;
    }

    /// <summary>
    /// Measures public condition-builder ordered counting over generic scalar indexes whose key and/or identity side uses 16-byte scalar lanes.<br/>
    /// The proof covers `SS16-8`, `SS8-16`, `SS16-16`, `FS32-8`, and `FS32-16`, exercising the direct count dispatch added after the original `SS8-8` proof.<br/>
    /// Keys are spread across high encoded root-prefix bytes so broad between-counts use boundary readers plus free route-target aggregate counts for middle prefixes.<br/>
    /// </summary>
    /// <param name="args">Harness command arguments.<br/></param>
    /// <returns>Zero when every measured count matches the seeded expectation; otherwise an exception is thrown.<br/></returns>
    private static int RunGenericScalarWideBetweenCountProof(string[] args)
    {
        int itemCount = GetIntOption(args, "--items", 150_000);
        int iterations = GetIntOption(args, "--iterations", 5_000);
        int warmupIterations = GetIntOption(args, "--warmup-iterations", 3);
        int prefixGroupCount = GetIntOption(args, "--prefix-groups", 256);
        int lowerPrefix = GetIntOption(args, "--lower-prefix", 64);
        int upperPrefix = GetIntOption(args, "--upper-prefix", 192);

        if (itemCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(args), itemCount, "Generic wide-scalar between count proof item count must be positive.");
        }

        if (iterations <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(args), iterations, "Generic wide-scalar between count proof iterations must be positive.");
        }

        if (warmupIterations < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(args), warmupIterations, "Generic wide-scalar between count proof warmup iterations cannot be negative.");
        }

        if (prefixGroupCount <= 0 || prefixGroupCount > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(args), prefixGroupCount, "Generic wide-scalar between count proof prefix group count must be from 1 through 256.");
        }

        if ((uint)lowerPrefix > byte.MaxValue || (uint)upperPrefix > byte.MaxValue || lowerPrefix > upperPrefix)
        {
            throw new ArgumentOutOfRangeException(nameof(args), $"{lowerPrefix}..{upperPrefix}", "Generic wide-scalar between count proof prefixes must be an increasing byte range.");
        }

        using Catalog catalog = Catalog.CreateMemory();
        using LibraDexIndex<UInt128, ulong> ss168 = catalog.Indexes["generic-wide-between-count-proof"]["ss168"].Scalar.Scalar<UInt128, ulong>().Create();
        using LibraDexIndex<ulong, UInt128> ss816 = catalog.Indexes["generic-wide-between-count-proof"]["ss816"].Scalar.Scalar<ulong, UInt128>().Create();
        using LibraDexIndex<UInt128, UInt128> ss1616 = catalog.Indexes["generic-wide-between-count-proof"]["ss1616"].Scalar.Scalar<UInt128, UInt128>().Create();
        using LibraDexIndex<byte[], ulong> fs328 = catalog.Indexes["generic-wide-between-count-proof"]["fs328"].Blob.Scalar<ulong>(LibraDexScalarWidth.Bytes32).Create();
        using LibraDexIndex<byte[], Guid> fs3216 = catalog.Indexes["generic-wide-between-count-proof"]["fs3216"].Blob.Scalar<Guid>(LibraDexScalarWidth.Bytes32).Create();

        UInt128 lowerKey16 = CreateGenericScalar16BetweenCountProofKey(lowerPrefix, 0);
        UInt128 upperKey16 = CreateGenericScalar16BetweenCountProofKey(upperPrefix, uint.MaxValue);
        ulong lowerKey8 = CreateGenericScalar8BetweenCountProofKey(lowerPrefix, 0);
        ulong upperKey8 = CreateGenericScalar8BetweenCountProofKey(upperPrefix, uint.MaxValue);
        byte[] lowerKey32 = CreateGenericFixed32BetweenCountProofKey(lowerPrefix, 0);
        byte[] upperKey32 = CreateGenericFixed32BetweenCountProofKey(upperPrefix, uint.MaxValue);
        long expectedCount = 0;
        for (int i = 0; i < itemCount; i++)
        {
            int prefix = i % prefixGroupCount;
            UInt128 key16 = CreateGenericScalar16BetweenCountProofKey(prefix, (uint)i);
            ulong key8 = CreateGenericScalar8BetweenCountProofKey(prefix, (uint)i);
            byte[] key32 = CreateGenericFixed32BetweenCountProofKey(prefix, (uint)i);
            UInt128 identity16 = CreateGenericScalar16BetweenCountProofIdentity(i);
            _ = ss168.Insert(key16, (ulong)(i + 1));
            _ = ss816.Insert(key8, identity16);
            _ = ss1616.Insert(key16, identity16);
            _ = fs328.Insert(key32, (ulong)(i + 1));
            _ = fs3216.Insert(key32, CreateGenericFixed32BetweenCountProofGuidIdentity(i));
            if (key8 >= lowerKey8 && key8 <= upperKey8)
            {
                expectedCount++;
            }
        }

        LibraDexConditionEndCondition ss168Condition = LibraDexCondition
            .ForGroup("generic-wide-between-count-proof")
            .Index("ss168")
            .AsUInt128
            .Between(lowerKey16, upperKey16)
            .EndCondition;
        LibraDexConditionEndCondition ss816Condition = LibraDexCondition
            .ForGroup("generic-wide-between-count-proof")
            .Index("ss816")
            .AsUInt64
            .Between(lowerKey8, upperKey8)
            .EndCondition;
        LibraDexConditionEndCondition ss1616Condition = LibraDexCondition
            .ForGroup("generic-wide-between-count-proof")
            .Index("ss1616")
            .AsUInt128
            .Between(lowerKey16, upperKey16)
            .EndCondition;
        LibraDexConditionEndCondition fs328Condition = LibraDexCondition
            .ForGroup("generic-wide-between-count-proof")
            .Index("fs328")
            .AsBinary
            .Between(lowerKey32, upperKey32)
            .EndCondition;
        LibraDexConditionEndCondition fs3216Condition = LibraDexCondition
            .ForGroup("generic-wide-between-count-proof")
            .Index("fs3216")
            .AsBinary
            .Between(lowerKey32, upperKey32)
            .EndCondition;
        Func<string, IIndex> resolver = name => name switch
        {
            "ss168" => ss168,
            "ss816" => ss816,
            "ss1616" => ss1616,
            "fs328" => fs328,
            "fs3216" => fs3216,
            _ => throw new KeyNotFoundException(name)
        };

        MeasureGenericWideBetweenCountProofShape("ss168", ss168Condition, resolver, expectedCount, iterations, warmupIterations, itemCount, prefixGroupCount, lowerPrefix, upperPrefix);
        MeasureGenericWideBetweenCountProofShape("ss816", ss816Condition, resolver, expectedCount, iterations, warmupIterations, itemCount, prefixGroupCount, lowerPrefix, upperPrefix);
        MeasureGenericWideBetweenCountProofShape("ss1616", ss1616Condition, resolver, expectedCount, iterations, warmupIterations, itemCount, prefixGroupCount, lowerPrefix, upperPrefix);
        MeasureGenericWideBetweenCountProofShape("fs328", fs328Condition, resolver, expectedCount, iterations, warmupIterations, itemCount, prefixGroupCount, lowerPrefix, upperPrefix);
        MeasureGenericWideBetweenCountProofShape("fs3216", fs3216Condition, resolver, expectedCount, iterations, warmupIterations, itemCount, prefixGroupCount, lowerPrefix, upperPrefix);
        return 0;
    }

    /// <summary>
    /// Measures one public wide-scalar condition count shape and validates its warmup and checksum against the seeded expectation.<br/>
    /// Keeping the measurement loop shared makes the three shape outputs directly comparable while leaving index creation shape-specific above.<br/>
    /// </summary>
    /// <param name="shapeName">The short physical shape name printed in harness output.<br/></param>
    /// <param name="condition">The public condition-builder count request.<br/></param>
    /// <param name="resolver">The public index resolver used by condition execution.<br/></param>
    /// <param name="expectedCount">The expected count for one condition execution.<br/></param>
    /// <param name="iterations">The measured iteration count.<br/></param>
    /// <param name="warmupIterations">The unmeasured warmup iteration count.<br/></param>
    /// <param name="itemCount">The seeded item count printed for context.<br/></param>
    /// <param name="prefixGroupCount">The seeded root-prefix group count printed for context.<br/></param>
    /// <param name="lowerPrefix">The lower included root prefix printed for context.<br/></param>
    /// <param name="upperPrefix">The upper included root prefix printed for context.<br/></param>
    private static void MeasureGenericWideBetweenCountProofShape(
        string shapeName,
        LibraDexConditionEndCondition condition,
        Func<string, IIndex> resolver,
        long expectedCount,
        int iterations,
        int warmupIterations,
        int itemCount,
        int prefixGroupCount,
        int lowerPrefix,
        int upperPrefix)
    {
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
            throw new InvalidDataException($"Generic wide-scalar between count proof {shapeName} warmup returned {warmupCount}; expected {expectedCount}.");
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
            throw new InvalidDataException($"Generic wide-scalar between count proof {shapeName} checksum returned {checksum}; expected {expectedChecksum}.");
        }

        double elapsedMs = watch.Elapsed.TotalMilliseconds;
        double opsPerSecond = iterations / Math.Max(watch.Elapsed.TotalSeconds, 0.000001D);
        Console.WriteLine(
            "generic-wide-between-count-proof ok " +
            $"shape={shapeName} items={itemCount} prefixGroups={prefixGroupCount} lowerPrefix={lowerPrefix} upperPrefix={upperPrefix} " +
            $"expected={expectedCount} warmup={warmupCount} iterations={iterations} elapsedMs={elapsedMs:F3} opsPerSecond={opsPerSecond:F2} checksum={checksum}");
    }

    /// <summary>
    /// Proves public condition-builder count fan-out for membership and condition-derived multi-range criteria.<br/>
    /// Membership uses duplicate operand keys to prove count semantics are set-based at the operand layer while preserving duplicate physical tuples under each selected key.<br/>
    /// Structured date month membership uses duplicate generated ranges to prove multi-range count merging avoids double counting before dispatching each final extent through the direct ordered count primitive.<br/>
    /// </summary>
    /// <param name="args">Harness command arguments.<br/></param>
    /// <returns>Zero when every count matches the expected physical tuple count; otherwise an exception is thrown.<br/></returns>
    private static int RunGenericCountFanoutProof(string[] args)
    {
        _ = args;
        using Catalog catalog = Catalog.CreateMemory();
        using LibraDexIndex<ulong, ulong> membership = catalog.Indexes["generic-count-fanout-proof"]["membership"].Scalar.Scalar<ulong, ulong>().Create(keys: IndexKeys.NonUnique);
        using LibraDexIndex<byte[], ulong> fixed32Membership = catalog.Indexes["generic-count-fanout-proof"]["fixed32Membership"].Blob.Scalar<ulong>(LibraDexScalarWidth.Bytes32).Create(keys: IndexKeys.NonUnique);
        LibraDexIndexShapeSpec createdShape = catalog.Indexes["generic-count-fanout-proof"]["created"].Shape.Date<DateTime, long>(
            DateKeys.ExactAndStructured,
            keys: IndexKeys.NonUnique);
        IIndex created = catalog.Indexes.Create(createdShape);

        _ = membership.Insert(10UL, 1001UL);
        _ = membership.Insert(10UL, 1002UL);
        _ = membership.Insert(20UL, 2001UL);
        _ = membership.Insert(30UL, 3001UL);

        byte[] fixedKeyA = CreateGenericFixed32BetweenCountProofKey(7, 1);
        byte[] fixedKeyB = CreateGenericFixed32BetweenCountProofKey(7, 2);
        byte[] fixedKeyC = CreateGenericFixed32BetweenCountProofKey(7, 3);
        _ = fixed32Membership.Insert(fixedKeyA, 7001UL);
        _ = fixed32Membership.Insert((byte[])fixedKeyA.Clone(), 7002UL);
        _ = fixed32Membership.Insert(fixedKeyB, 7003UL);
        _ = fixed32Membership.Insert(fixedKeyC, 7004UL);

        ValidateGenericInsert(created.Insert(new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc), 2026010108L), "generic count fanout date Jan first");
        ValidateGenericInsert(created.Insert(new DateTime(2026, 1, 20, 17, 0, 0, DateTimeKind.Utc), 2026012017L), "generic count fanout date Jan second");
        ValidateGenericInsert(created.Insert(new DateTime(2026, 2, 3, 9, 0, 0, DateTimeKind.Utc), 2026020309L), "generic count fanout date Feb first");
        ValidateGenericInsert(created.Insert(new DateTime(2026, 3, 7, 9, 0, 0, DateTimeKind.Utc), 2026030709L), "generic count fanout date Mar excluded");

        Func<string, IIndex> resolver = name => name switch
        {
            "membership" => membership,
            "fixed32Membership" => fixed32Membership,
            "created" => created,
            _ => throw new KeyNotFoundException(name)
        };

        LibraDexConditionEndCondition membershipCondition = LibraDexCondition
            .ForGroup("generic-count-fanout-proof")
            .Index("membership")
            .AsUInt64
            .InSet(new[] { 10UL, 10UL, 20UL })
            .EndCondition;
        LibraDexConditionEndCondition fixed32MembershipCondition = LibraDexCondition
            .ForGroup("generic-count-fanout-proof")
            .Index("fixed32Membership")
            .AsBinary
            .InSet(new byte[][] { (byte[])fixedKeyA.Clone(), (byte[])fixedKeyA.Clone(), (byte[])fixedKeyB.Clone() })
            .EndCondition;
        LibraDexConditionEndCondition multiRangeCondition = LibraDexCondition
            .ForGroup("generic-count-fanout-proof")
            .Index("created")
            .AsDate
            .YearInMonths(2026, 1, 1, 2)
            .EndCondition;

        long membershipCount = membershipCondition.Count(resolver, IdentityDeduplication.Preserve);
        long fixed32MembershipCount = fixed32MembershipCondition.Count(resolver, IdentityDeduplication.Preserve);
        long multiRangeCount = multiRangeCondition.Count(resolver, IdentityDeduplication.Preserve);
        if (membershipCount != 3)
        {
            throw new InvalidDataException($"Generic count fanout proof membership count returned {membershipCount}; expected 3.");
        }

        if (fixed32MembershipCount != 3)
        {
            throw new InvalidDataException($"Generic count fanout proof fixed32 membership count returned {fixed32MembershipCount}; expected 3.");
        }

        if (multiRangeCount != 3)
        {
            throw new InvalidDataException($"Generic count fanout proof multi-range count returned {multiRangeCount}; expected 3.");
        }

        Console.WriteLine($"generic-count-fanout-proof ok membership={membershipCount} fixed32Membership={fixed32MembershipCount} multiRange={multiRangeCount}");
        return 0;
    }

    /// <summary>
    /// Proves count-only fan-out normalization for public non-generic scalar facades.<br/>
    /// String and UInt64 membership are exercised through public condition-builder `.Count(...)`, while UInt64 and BigInteger multirange are exercised as internal primitive leaves because no ordinary public grammar emits those multirange payloads today.<br/>
    /// The expected counts verify duplicate operands and overlapping ranges are treated as set/range-union requests for count without materializing identities.<br/>
    /// </summary>
    /// <param name="args">Harness command arguments.<br/></param>
    /// <returns>Zero when every count matches the expected physical tuple count; otherwise an exception is thrown.<br/></returns>
    private static int RunNonGenericCountFanoutProof(string[] args)
    {
        _ = args;
        using Catalog catalog = Catalog.CreateMemory();
        using LibraDexStringScalar8Index code = catalog.Indexes["non-generic-count-fanout-proof"]["code"].String.Create(StringKeys.Exact);
        using LibraDexUInt64VarIdentityIndex size = catalog.Indexes["non-generic-count-fanout-proof"]["size"].UInt64VarIdentityKeys(maxIdentityBytes: 16).Create(keys: IndexKeys.NonUnique);
        using LibraDexBigIntScalar8Index<long> score = catalog.Indexes["non-generic-count-fanout-proof"]["score"].BigIntKeys<long>(maxBytes: 32).Create(keys: IndexKeys.NonUnique);
        using LibraDexBigIntVarIdentityIndex scoreVar = catalog.Indexes["non-generic-count-fanout-proof"]["scoreVar"].BigIntVarIdentityKeys(maxBytes: 32, maxIdentityBytes: 16).Create(keys: IndexKeys.NonUnique);

        ValidateGenericInsert(code.Insert("A", 101UL), "non-generic fanout string A first");
        ValidateGenericInsert(code.Insert("A", 102UL), "non-generic fanout string A second");
        ValidateGenericInsert(code.Insert("B", 201UL), "non-generic fanout string B");
        ValidateGenericInsert(code.Insert("C", 301UL), "non-generic fanout string C excluded");

        ValidateGenericInsert(size.Insert(10UL, new byte[] { 1, 0 }), "non-generic fanout uint64 10 first");
        ValidateGenericInsert(size.Insert(10UL, new byte[] { 1, 1 }), "non-generic fanout uint64 10 second");
        ValidateGenericInsert(size.Insert(20UL, new byte[] { 2, 0 }), "non-generic fanout uint64 20");
        ValidateGenericInsert(size.Insert(25UL, new byte[] { 2, 5 }), "non-generic fanout uint64 25");
        ValidateGenericInsert(size.Insert(30UL, new byte[] { 3, 0 }), "non-generic fanout uint64 30 excluded");

        ValidateGenericInsert(score.Insert(new BigInteger(10), 1001L), "non-generic fanout bigint scalar 10 first");
        ValidateGenericInsert(score.Insert(new BigInteger(10), 1002L), "non-generic fanout bigint scalar 10 second");
        ValidateGenericInsert(score.Insert(new BigInteger(20), 2001L), "non-generic fanout bigint scalar 20");
        ValidateGenericInsert(score.Insert(new BigInteger(25), 2501L), "non-generic fanout bigint scalar 25");
        ValidateGenericInsert(score.Insert(new BigInteger(30), 3001L), "non-generic fanout bigint scalar excluded");

        ValidateGenericInsert(scoreVar.Insert(new BigInteger(10), new byte[] { 10, 1 }), "non-generic fanout bigint var 10 first");
        ValidateGenericInsert(scoreVar.Insert(new BigInteger(10), new byte[] { 10, 2 }), "non-generic fanout bigint var 10 second");
        ValidateGenericInsert(scoreVar.Insert(new BigInteger(20), new byte[] { 20, 1 }), "non-generic fanout bigint var 20");
        ValidateGenericInsert(scoreVar.Insert(new BigInteger(25), new byte[] { 25, 1 }), "non-generic fanout bigint var 25");
        ValidateGenericInsert(scoreVar.Insert(new BigInteger(30), new byte[] { 30, 1 }), "non-generic fanout bigint var excluded");

        Func<string, IIndex> resolver = name => name switch
        {
            "code" => code,
            "size" => size,
            "score" => score,
            "scoreVar" => scoreVar,
            _ => throw new KeyNotFoundException(name)
        };

        long stringMembershipCount = LibraDexCondition
            .ForGroup("non-generic-count-fanout-proof")
            .Index("code")
            .AsString
            .InSet(new[] { "A", "A", "B" })
            .EndCondition
            .Count(resolver, IdentityDeduplication.Preserve);
        long uint64MembershipCount = LibraDexCondition
            .ForGroup("non-generic-count-fanout-proof")
            .Index("size")
            .AsUInt64
            .InSet(new[] { 10UL, 10UL, 20UL })
            .EndCondition
            .Count(resolver, IdentityDeduplication.Preserve);
        long bigIntMembershipCount = LibraDexCondition
            .ForGroup("non-generic-count-fanout-proof")
            .Index("score")
            .AsBigInteger
            .InSet(new[] { new BigInteger(10), new BigInteger(10), new BigInteger(20) })
            .EndCondition
            .Count(resolver, IdentityDeduplication.Preserve);

        LibraDexIdentityKeyRange[] overlappingRanges = new[]
        {
            new LibraDexIdentityKeyRange(new BigInteger(10), new BigInteger(20)),
            new LibraDexIdentityKeyRange(new BigInteger(15), new BigInteger(25))
        };
        LibraDexIdentityKeyRange[] overlappingUInt64Ranges = new[]
        {
            new LibraDexIdentityKeyRange(10UL, 20UL),
            new LibraDexIdentityKeyRange(15UL, 25UL)
        };
        IIdentityCriterion uint64MultiRange = LibraDexIdentityCriterion.Leaf(
            size,
            LibraDexCriteriaKind.MultiRange,
            new LibraDexQueryDiagnostics(LibraDexExecutionKind.FastPath),
            overlappingUInt64Ranges);
        IIdentityCriterion scalarMultiRange = LibraDexIdentityCriterion.Leaf(
            score,
            LibraDexCriteriaKind.MultiRange,
            new LibraDexQueryDiagnostics(LibraDexExecutionKind.FastPath),
            overlappingRanges);
        IIdentityCriterion varMultiRange = LibraDexIdentityCriterion.Leaf(
            scoreVar,
            LibraDexCriteriaKind.MultiRange,
            new LibraDexQueryDiagnostics(LibraDexExecutionKind.FastPath),
            overlappingRanges);
        long uint64MultiRangeCount = LibraDexIdentityExecutionPlanner.Count(uint64MultiRange, IdentityDeduplication.Preserve);
        long bigIntMultiRangeCount = LibraDexIdentityExecutionPlanner.Count(scalarMultiRange, IdentityDeduplication.Preserve);
        long bigIntVarMultiRangeCount = LibraDexIdentityExecutionPlanner.Count(varMultiRange, IdentityDeduplication.Preserve);

        if (stringMembershipCount != 3)
        {
            throw new InvalidDataException($"Non-generic count fanout proof string membership returned {stringMembershipCount}; expected 3.");
        }

        if (uint64MembershipCount != 3)
        {
            throw new InvalidDataException($"Non-generic count fanout proof UInt64 membership returned {uint64MembershipCount}; expected 3.");
        }

        if (bigIntMembershipCount != 3)
        {
            throw new InvalidDataException($"Non-generic count fanout proof BigInt membership returned {bigIntMembershipCount}; expected 3.");
        }

        if (uint64MultiRangeCount != 4)
        {
            throw new InvalidDataException($"Non-generic count fanout proof UInt64 var-identity multirange returned {uint64MultiRangeCount}; expected 4.");
        }

        if (bigIntMultiRangeCount != 4)
        {
            throw new InvalidDataException($"Non-generic count fanout proof BigInt multirange returned {bigIntMultiRangeCount}; expected 4.");
        }

        if (bigIntVarMultiRangeCount != 4)
        {
            throw new InvalidDataException($"Non-generic count fanout proof BigInt var-identity multirange returned {bigIntVarMultiRangeCount}; expected 4.");
        }

        Console.WriteLine(
            "non-generic-count-fanout-proof ok " +
            $"stringMembership={stringMembershipCount} uint64Membership={uint64MembershipCount} " +
            $"bigIntMembership={bigIntMembershipCount} uint64MultiRange={uint64MultiRangeCount} " +
            $"bigIntMultiRange={bigIntMultiRangeCount} bigIntVarMultiRange={bigIntVarMultiRangeCount}");
        return 0;
    }

    /// <summary>
    /// Creates one public UInt128 key whose encoded scalar-16 order preserves the supplied high route prefix.<br/>
    /// UInt128 keys are already sortable as an unsigned scalar, so the top byte directly selects the root-prefix route in scalar-16 storage.<br/>
    /// </summary>
    /// <param name="prefix">The desired high route prefix byte.<br/></param>
    /// <param name="ordinal">The low key ordinal used to keep seeded keys unique.<br/></param>
    /// <returns>The public UInt128 key.</returns>
    private static UInt128 CreateGenericScalar16BetweenCountProofKey(int prefix, uint ordinal)
    {
        UInt128 widened = ordinal;
        return ((UInt128)(byte)prefix << 120) | (widened << 88) | (widened << 32) | widened;
    }

    /// <summary>
    /// Creates a deterministic non-zero UInt128 identity for wide-identity count proof inserts.<br/>
    /// Count criteria do not decode identities, but varying both lanes keeps the seeded physical shape representative of real widened identities.<br/>
    /// </summary>
    /// <param name="ordinal">The deterministic item ordinal.<br/></param>
    /// <returns>The public UInt128 identity.</returns>
    private static UInt128 CreateGenericScalar16BetweenCountProofIdentity(int ordinal)
    {
        return ((UInt128)0xA5 << 120) | (uint)(ordinal + 1);
    }

    /// <summary>
    /// Creates one public 32-byte key whose encoded fixed32 order preserves the supplied high route prefix.<br/>
    /// The first byte selects the root route while early-lane ordinal entropy keeps inserts from requiring excessive deep routing for proof-sized datasets.<br/>
    /// </summary>
    /// <param name="prefix">The desired high route prefix byte.<br/></param>
    /// <param name="ordinal">The low key ordinal used to keep seeded keys unique.<br/></param>
    /// <returns>A 32-byte public key.</returns>
    private static byte[] CreateGenericFixed32BetweenCountProofKey(int prefix, uint ordinal)
    {
        byte[] key = new byte[32];
        key[0] = (byte)prefix;
        BinaryPrimitives.WriteUInt32BigEndian(key.AsSpan(4, sizeof(uint)), ordinal);
        BinaryPrimitives.WriteUInt32BigEndian(key.AsSpan(12, sizeof(uint)), ordinal * 17U);
        BinaryPrimitives.WriteUInt32BigEndian(key.AsSpan(20, sizeof(uint)), ordinal * 131U);
        BinaryPrimitives.WriteUInt32BigEndian(key.AsSpan(28, sizeof(uint)), ordinal);
        return key;
    }

    /// <summary>
    /// Creates a deterministic Guid identity for fixed32 widened-identity proof inserts.<br/>
    /// The exact identity value is not used by count criteria, but varying it keeps the physical `FS32-16` shape representative of real data.<br/>
    /// </summary>
    /// <param name="ordinal">The deterministic item ordinal.<br/></param>
    /// <returns>The public Guid identity.</returns>
    private static Guid CreateGenericFixed32BetweenCountProofGuidIdentity(int ordinal)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)ordinal);
        BinaryPrimitives.WriteUInt32BigEndian(bytes[4..], (uint)(ordinal * 17));
        BinaryPrimitives.WriteUInt32BigEndian(bytes[8..], (uint)(ordinal * 131));
        BinaryPrimitives.WriteUInt32BigEndian(bytes[12..], (uint)(ordinal + 1));
        return new Guid(bytes);
    }

    /// <summary>
    /// Creates the DataKernel options used by the 16-byte identity split-preservation regression.<br/>
    /// The options match normal harness routed-write behavior while keeping commit coalescing enabled for the batched insert loop.<br/>
    /// </summary>
    /// <returns>The DataKernel options for the split-preservation command.</returns>
    private static DataKernelOptions CreateIdentity16SplitPreservationOptions()
    {
        return new DataKernelOptions(
            AppendBufferSize: DefaultAppendBufferSize,
            ReservedPrefixBytes: 0,
            FlushToDiskOnCommit: false,
            MaxCommitGapCoalesceBytes: 512);
    }


    /// <summary>
    /// Creates a root-prefix-stable 8-byte key for split-preservation inserts.<br/>
    /// The top byte remains zero while the second byte rotates, forcing deeper route discrimination after the initial shelf fills.<br/>
    /// </summary>
    /// <param name="index">The deterministic item index.</param>
    /// <returns>The encoded 8-byte key.</returns>
    private static ulong CreateIdentity16SplitScalar8Key(int index)
    {
        return ((ulong)(index & 0xFF) << 48) | (ulong)(uint)index;
    }


    /// <summary>
    /// Creates a root-prefix-stable 16-byte key for split-preservation inserts.<br/>
    /// The high lane carries the routed prefix variation and the low lane keeps a unique tie-breaker for exact point reads.<br/>
    /// </summary>
    /// <param name="index">The deterministic item index.</param>
    /// <param name="keyHigh">The encoded high key lane.</param>
    /// <param name="keyLow">The encoded low key lane.</param>
    private static void CreateIdentity16SplitScalar16Key(int index, out ulong keyHigh, out ulong keyLow)
    {
        keyHigh = ((ulong)(index & 0xFF) << 48) | (ulong)(uint)(index / 256);
        keyLow = 0x5100_0000_0000_0000UL | (ulong)(uint)index;
    }


    /// <summary>
    /// Creates a root-prefix-stable 32-byte fixed key for split-preservation inserts.<br/>
    /// The first lane drives route discrimination and the remaining lanes make the tuple look like a realistic composite fixed key.<br/>
    /// </summary>
    /// <param name="index">The deterministic item index.</param>
    /// <param name="key0">The first fixed-key lane.</param>
    /// <param name="key1">The second fixed-key lane.</param>
    /// <param name="key2">The third fixed-key lane.</param>
    /// <param name="key3">The fourth fixed-key lane.</param>
    private static void CreateIdentity16SplitFixed32Key(int index, out ulong key0, out ulong key1, out ulong key2, out ulong key3)
    {
        key0 = ((ulong)(index & 0xFF) << 48) | (ulong)(uint)(index / 256);
        key1 = 0x3200_0000_0000_0000UL | (ulong)(uint)index;
        key2 = 0x6400_0000_0000_0000UL | ((ulong)(uint)index << 1);
        key3 = 0x9600_0000_0000_0000UL | ((ulong)(uint)index << 2);
    }


    /// <summary>
    /// Creates a deterministic non-zero 16-byte identity pair for split-preservation verification.<br/>
    /// Both lanes vary by item so a low-only split copy, swapped-lane copy, or zero-high regression is detected by point reads.<br/>
    /// </summary>
    /// <param name="index">The deterministic item index.</param>
    /// <param name="identityHigh">The expected high identity lane.</param>
    /// <param name="identityLow">The expected low identity lane.</param>
    private static void CreateIdentity16SplitIdentity(int index, out ulong identityHigh, out ulong identityLow)
    {
        unchecked
        {
            ulong widened = (uint)index;
            identityHigh = 0xA100_0000_0000_0000UL | (widened << 20) | (ulong)(uint)(index * 17);
            identityLow = 0xB200_0000_0000_0000UL | (widened << 24) | (ulong)(uint)(index * 131);
        }
    }


    /// <summary>
    /// Verifies one exact `SS8-16` point range read against an expected identity pair.<br/>
    /// The caller provides reusable shelf scratch and target-kind cache so the regression validates many keys without incidental allocation noise.<br/>
    /// </summary>
    /// <param name="session">The reopened file session.</param>
    /// <param name="rootRouterOffset">The root router offset for the tested index.</param>
    /// <param name="profile">The physical shelf profile.</param>
    /// <param name="key">The exact encoded key to read.</param>
    /// <param name="expectedHigh">The expected high identity lane.</param>
    /// <param name="expectedLow">The expected low identity lane.</param>
    /// <param name="shelfScratch">Reusable scratch sized to one profiled shelf.</param>
    /// <param name="targetKindCache">Reusable route target-kind cache.</param>
    private static void ValidateScalar8Scalar16IdentityPointRead(
        LibraDexFileSession session,
        long rootRouterOffset,
        Scalar8Scalar16Profile profile,
        ulong key,
        ulong expectedHigh,
        ulong expectedLow,
        byte[] shelfScratch,
        RouteTargetKindCache targetKindCache)
    {
        Span<ulong> highs = stackalloc ulong[2];
        Span<ulong> lows = stackalloc ulong[2];
        int count = session.ReadScalar8Scalar16IdentityRange(rootRouterOffset, profile, key, key, maxRouterHops: 8, highs, lows, shelfScratch, targetKindCache);
        ValidateIdentity16PointRead("SS8-16", key, count, highs[0], lows[0], expectedHigh, expectedLow);
    }


    /// <summary>
    /// Verifies one exact `SS16-16` point range read against an expected identity pair.<br/>
    /// The check exercises the routed range-read API after split-heavy mutation, not direct shelf inspection.<br/>
    /// </summary>
    /// <param name="session">The reopened file session.</param>
    /// <param name="rootRouterOffset">The root router offset for the tested index.</param>
    /// <param name="profile">The physical shelf profile.</param>
    /// <param name="keyHigh">The exact high key lane to read.</param>
    /// <param name="keyLow">The exact low key lane to read.</param>
    /// <param name="expectedHigh">The expected high identity lane.</param>
    /// <param name="expectedLow">The expected low identity lane.</param>
    /// <param name="shelfScratch">Reusable scratch sized to one profiled shelf.</param>
    /// <param name="targetKindCache">Reusable route target-kind cache.</param>
    private static void ValidateScalar16Scalar16IdentityPointRead(
        LibraDexFileSession session,
        long rootRouterOffset,
        Scalar16Scalar16Profile profile,
        ulong keyHigh,
        ulong keyLow,
        ulong expectedHigh,
        ulong expectedLow,
        byte[] shelfScratch,
        RouteTargetKindCache targetKindCache)
    {
        Span<ulong> highs = stackalloc ulong[2];
        Span<ulong> lows = stackalloc ulong[2];
        int count = session.ReadScalar16Scalar16IdentityRange(rootRouterOffset, profile, keyHigh, keyLow, keyHigh, keyLow, maxRouterHops: 8, highs, lows, shelfScratch, targetKindCache);
        ValidateIdentity16PointRead("SS16-16", keyLow, count, highs[0], lows[0], expectedHigh, expectedLow);
    }


    /// <summary>
    /// Verifies one exact `FS32-16` point range read against an expected identity pair.<br/>
    /// This is the direct regression for full-pair identity preservation through the fixed-32 walked split helper.<br/>
    /// </summary>
    /// <param name="session">The reopened file session.</param>
    /// <param name="rootRouterOffset">The root router offset for the tested index.</param>
    /// <param name="profile">The physical shelf profile.</param>
    /// <param name="key0">The first exact fixed-key lane to read.</param>
    /// <param name="key1">The second exact fixed-key lane to read.</param>
    /// <param name="key2">The third exact fixed-key lane to read.</param>
    /// <param name="key3">The fourth exact fixed-key lane to read.</param>
    /// <param name="expectedHigh">The expected high identity lane.</param>
    /// <param name="expectedLow">The expected low identity lane.</param>
    /// <param name="shelfScratch">Reusable scratch sized to one profiled shelf.</param>
    /// <param name="targetKindCache">Reusable route target-kind cache.</param>
    private static void ValidateFixed32Scalar16IdentityPointRead(
        LibraDexFileSession session,
        long rootRouterOffset,
        Fixed32Scalar16Profile profile,
        ulong key0,
        ulong key1,
        ulong key2,
        ulong key3,
        ulong expectedHigh,
        ulong expectedLow,
        byte[] shelfScratch,
        RouteTargetKindCache targetKindCache)
    {
        Span<ulong> highs = stackalloc ulong[2];
        Span<ulong> lows = stackalloc ulong[2];
        int count = session.ReadFixed32Scalar16IdentityRange(rootRouterOffset, profile, key0, key1, key2, key3, key0, key1, key2, key3, maxRouterHops: 32, highs, lows, shelfScratch, targetKindCache);
        ValidateIdentity16PointRead("FS32-16", key3, count, highs[0], lows[0], expectedHigh, expectedLow);
    }


    /// <summary>
    /// Compares one decoded 16-byte identity point-read result with the expected high/low lanes.<br/>
    /// A count other than one is treated as a route or duplicate-key failure, while lane drift is treated as split preservation failure.<br/>
    /// </summary>
    /// <param name="shape">The shape label for failure messages.</param>
    /// <param name="keyDiagnostic">A compact key lane included in failure messages.</param>
    /// <param name="count">The point-read identity count.</param>
    /// <param name="actualHigh">The actual high identity lane.</param>
    /// <param name="actualLow">The actual low identity lane.</param>
    /// <param name="expectedHigh">The expected high identity lane.</param>
    /// <param name="expectedLow">The expected low identity lane.</param>
    private static void ValidateIdentity16PointRead(
        string shape,
        ulong keyDiagnostic,
        int count,
        ulong actualHigh,
        ulong actualLow,
        ulong expectedHigh,
        ulong expectedLow)
    {
        if (count != 1)
        {
            throw new InvalidDataException($"{shape} identity split preservation expected one identity for key lane {keyDiagnostic}, got {count}.");
        }

        if (actualHigh != expectedHigh || actualLow != expectedLow)
        {
            throw new InvalidDataException($"{shape} identity split preservation drifted for key lane {keyDiagnostic}. Expected {expectedHigh:X16}/{expectedLow:X16}, actual {actualHigh:X16}/{actualLow:X16}.");
        }
    }


    /// <summary>
    /// Creates one direct shelf route plus two transformed-shelf router arenas for read-cache policy measurement.<br/>
    /// The setup intentionally mixes a route that cannot benefit from router arena caching with two routes that can.<br/>
    /// </summary>
    /// <param name="session">The active file session.</param>
    /// <param name="profile">The `SS8-8` shelf profile.</param>
    /// <returns>The root offset and encoded keys used by the perf scenarios.</returns>
    private static RouterArenaReadCachePerfSetup CreateScalar8Scalar8RouterArenaReadCachePerfSetup(
        LibraDexFileSession session,
        Scalar8Scalar8Profile profile)
    {
        CreateTransformSplitVectors(profile, out ulong[] keys, out ulong[] identities, out int _, out int _);
        (RouterSnapshot root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "ss8arcpf", 0));
        _ = session.CreateScalar8Scalar8ShelfAndLinkRootRoute(root.Offset, 0x7F, profile, itemCount: 512);

        _ = session.CreateScalar8Scalar8ShelfAndLinkRootRoutes(root.Offset, profile, keys, identities, [0x00]);
        const ulong firstArenaKey = 0x0080_0000_0000_0000UL + 10_000UL;
        _ = session.SplitRoutedScalar8Scalar8ByShelfTransform(root.Offset, 0x00, 0x80, profile, firstArenaKey, firstArenaKey);

        _ = session.CreateScalar8Scalar8ShelfAndLinkRootRoutes(root.Offset, profile, keys, identities, [0x01]);
        const ulong secondArenaKey = 0x0180_0000_0000_0000UL + 10_000UL;
        _ = session.SplitRoutedScalar8Scalar8ByShelfTransform(root.Offset, 0x01, 0x80, profile, secondArenaKey, secondArenaKey);

        const ulong directKey = 0x7F00_0000_0000_0080UL;
        _ = session.GetAndResetReadTelemetry();
        return new RouterArenaReadCachePerfSetup(root.Offset, firstArenaKey, secondArenaKey, directKey);
    }


    /// <summary>
    /// Validates that each batched route target resolved to the expected shelf and returns a compact checksum.<br/>
    /// </summary>
    /// <param name="targets">The route targets produced by one batch route policy.</param>
    /// <param name="expectedShelfOffset">The expected shelf offset for every target.</param>
    /// <returns>A checksum over the target offsets, depths, and kinds.</returns>
    private static long CheckScalar8Scalar8RouteTargets(
        ReadOnlySpan<Scalar8Scalar8RouteTarget> targets,
        long expectedShelfOffset)
    {
        long checksum = 0;
        for (int i = 0; i < targets.Length; i++)
        {
            Scalar8Scalar8RouteTarget target = targets[i];
            if (target.Kind != Scalar8Scalar8RouteTargetKind.Shelf || target.Offset != expectedShelfOffset || target.RouterDepth != 1)
            {
                throw new InvalidDataException($"Unexpected batched route target at index {i}: {target.Kind} offset {target.Offset} depth {target.RouterDepth}.");
            }

            checksum ^= target.Offset + target.RouterDepth + (int)target.Kind + i;
        }

        return checksum;
    }


    /// <summary>
    /// Validates batched point-lookup results and returns a compact checksum.<br/>
    /// The current vector uses keys whose first matching identity equals the encoded key, making misses or wrong shelf reads immediately visible.<br/>
    /// </summary>
    /// <param name="keys">The encoded keys that were looked up.</param>
    /// <param name="identities">The encoded identities returned by the lookup.</param>
    /// <param name="foundFlags">The found flags returned by the lookup.</param>
    /// <returns>A checksum over the found identities.</returns>
    private static long CheckScalar8Scalar8BatchLookup(
        ReadOnlySpan<ulong> keys,
        ReadOnlySpan<ulong> identities,
        ReadOnlySpan<byte> foundFlags)
    {
        long checksum = 0;
        for (int i = 0; i < keys.Length; i++)
        {
            if (foundFlags[i] != 1 || identities[i] != keys[i])
            {
                throw new InvalidDataException($"Unexpected batched lookup result at index {i}: found={foundFlags[i]} identity={identities[i]} key={keys[i]}.");
            }

            checksum += unchecked((long)((identities[i] >> 32) ^ identities[i] ^ (ulong)(i + 1)));
        }

        return checksum;
    }


    /// <summary>
    /// Creates deterministic encoded lookup keys that are known to live in the right shelf after the router-arena setup transform.<br/>
    /// The selected keys keep this first benchmark focused on same-shelf locality rather than miss behavior or cross-shelf reads.<br/>
    /// </summary>
    /// <param name="batchCount">The number of encoded keys to create.</param>
    /// <returns>The encoded lookup keys.</returns>
    private static ulong[] CreateScalar8Scalar8RightShelfLookupKeys(int batchCount)
    {
        const ulong rightPrefixBase = 0x0080_0000_0000_0000UL;
        ulong[] keys = new ulong[batchCount];
        for (int i = 0; i < keys.Length; i++)
        {
            keys[i] = rightPrefixBase + (ulong)i;
        }

        return keys;
    }


    /// <summary>
    /// Measures one routed batched point lookup scenario.<br/>
    /// The scratch arrays are reused across iterations to keep the measured operation close to the allocation-free library path.<br/>
    /// </summary>
    /// <param name="name">The scenario name.</param>
    /// <param name="session">The active file session.</param>
    /// <param name="rootRouterOffset">The root router offset.</param>
    /// <param name="profile">The `SS8-8` shelf profile.</param>
    /// <param name="keys">The encoded keys to look up on every iteration.</param>
    /// <param name="iterations">The number of batch lookups to perform.</param>
    /// <param name="readPolicy">The route-read policy to apply.</param>
    /// <param name="identities">The reusable identity result buffer.</param>
    /// <param name="foundFlags">The reusable found-flag result buffer.</param>
    /// <param name="routeTargets">The reusable route-target scratch buffer.</param>
    /// <param name="shelfBuffer">The reusable shelf read buffer.</param>
    /// <param name="validationKind">The compact result validator selector: `0` same-shelf, `1` same-parent cross-shelf, `2` generalized traversal.</param>
    /// <returns>The measured perf result.</returns>
    private static Scalar8Scalar8BatchLookupPerfResult MeasureScalar8Scalar8BatchLookupScenario(
        string name,
        LibraDexFileSession session,
        long rootRouterOffset,
        Scalar8Scalar8Profile profile,
        ReadOnlySpan<ulong> keys,
        int iterations,
        Scalar8Scalar8RouteReadPolicy readPolicy,
        Span<ulong> identities,
        Span<byte> foundFlags,
        Span<Scalar8Scalar8RouteTarget> routeTargets,
        Span<byte> shelfBuffer)
    {
        session.ClearRouterArenaReadCacheForValidation();
        _ = session.GetAndResetReadTelemetry();
        long checksum = 0;
        Stopwatch stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            session.LookupScalar8Scalar8Identities(
                rootRouterOffset,
                profile,
                keys,
                maxRouterHops: 8,
                readPolicy,
                identities,
                foundFlags,
                routeTargets,
                shelfBuffer);
            checksum += CheckScalar8Scalar8BatchLookup(keys, identities, foundFlags);
        }

        stopwatch.Stop();
        DataKernelReadTelemetry readTelemetry = session.GetAndResetReadTelemetry();
        return new Scalar8Scalar8BatchLookupPerfResult(name, iterations, keys.Length, stopwatch.Elapsed, readTelemetry.ReadCallCount, readTelemetry.BackingReadCallCount, readTelemetry.BytesRead, checksum);
    }


    /// <summary>
    /// Prints the `SS8-8` batched point lookup perf table to the console.<br/>
    /// </summary>
    /// <param name="results">The measured lookup results.</param>
    private static void PrintScalar8Scalar8BatchLookupPerf(ReadOnlySpan<Scalar8Scalar8BatchLookupPerfResult> results)
    {
        Console.WriteLine("| scenario | iterations | batch | elapsed ms | ns/key | reads | backing reads | bytes | bytes/key | checksum |");
        Console.WriteLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        for (int i = 0; i < results.Length; i++)
        {
            Scalar8Scalar8BatchLookupPerfResult result = results[i];
            double keyCount = (double)result.Iterations * result.BatchCount;
            double nsPerKey = result.Elapsed.TotalSeconds * 1_000_000_000d / keyCount;
            double bytesPerKey = result.BytesRead / keyCount;
            Console.WriteLine($"| {result.Name} | {result.Iterations} | {result.BatchCount} | {result.Elapsed.TotalMilliseconds:N3} | {nsPerKey:N2} | {result.ReadCallCount} | {result.BackingReadCallCount} | {result.BytesRead} | {bytesPerKey:N2} | {result.Checksum} |");
        }
    }


    private static FixedReaderPerfSample SelectMedianFixedReaderPerfSample(ReadOnlySpan<FixedReaderPerfSample> samples)
    {
        FixedReaderPerfSample[] sorted = samples.ToArray();
        Array.Sort(sorted, static (left, right) => left.CursorIdentityNsPerIdentity.CompareTo(right.CursorIdentityNsPerIdentity));
        return sorted[sorted.Length / 2];
    }


    private static byte[][] CreateFixedReaderByteKeys(int itemCount, int width)
    {
        byte[][] keys = new byte[itemCount][];
        int prefixStride = Math.Max(1, itemCount / 16);
        for (int i = 0; i < itemCount; i++)
        {
            byte[] key = new byte[width];
            key[0] = (byte)Math.Min(255, i / prefixStride);
            BinaryPrimitives.WriteUInt64BigEndian(key.AsSpan(width - sizeof(ulong), sizeof(ulong)), (ulong)i);
            keys[i] = key;
        }

        return keys;
    }


    private static long HashFixedReaderValue<T>(T value)
    {
        if (value is byte[] bytes)
        {
            long hash = bytes.Length;
            for (int i = 0; i < bytes.Length; i += 8)
            {
                hash = unchecked((hash * 397) ^ bytes[i]);
            }

            return hash;
        }

        return value?.GetHashCode() ?? 0;
    }


    private static void PrintFixedReaderPerfRows(IReadOnlyList<FixedReaderPerfRow> rows)
    {
        Console.WriteLine("shape | range | materialized ns/id | cursor id ns/id | cursor key ns/id | cursor tuple ns/id | desc tuple ns/id | count ns/call | skip ns/id");
        for (int i = 0; i < rows.Count; i++)
        {
            FixedReaderPerfRow row = rows[i];
            Console.WriteLine(FormattableString.Invariant($"{row.Shape} | {row.RangeCount} | {row.MaterializedNsPerIdentity:F2} | {row.CursorIdentityNsPerIdentity:F2} | {row.CursorKeyNsPerIdentity:F2} | {row.CursorTupleNsPerIdentity:F2} | {row.DescendingTupleNsPerIdentity:F2} | {row.CountNsPerCall:F2} | {row.SkipNsPerIdentity:F2}"));
        }
    }


    private readonly record struct FixedReaderPerfSample(
        double MaterializedNsPerIdentity,
        double CursorIdentityNsPerIdentity,
        double CursorKeyNsPerIdentity,
        double CursorTupleNsPerIdentity,
        double DescendingTupleNsPerIdentity,
        double CountNsPerCall,
        double SkipNsPerIdentity,
        long Checksum);


    private readonly record struct FixedReaderPerfRow(
        string Shape,
        int RangeCount,
        int Iterations,
        int RepeatCount,
        double MaterializedNsPerIdentity,
        double CursorIdentityNsPerIdentity,
        double CursorKeyNsPerIdentity,
        double CursorTupleNsPerIdentity,
        double DescendingTupleNsPerIdentity,
        double CountNsPerCall,
        double SkipNsPerIdentity,
        long Checksum);


    /// <summary>
    /// Validates one generated same-identities tuple for a fixed-shape multi-index reopen pass.<br/>
    /// The generic profile keeps the shared reopen loop type-safe while preserving each shape's direct shelf reader and tuple predicate.<br/>
    /// </summary>
    /// <typeparam name="TProfile">The fixed-shape shelf profile type.</typeparam>
    /// <param name="session">The reopened file session.</param>
    /// <param name="rootRouterOffset">The plan root-router offset.</param>
    /// <param name="profile">The fixed-shape shelf profile.</param>
    /// <param name="shelfCache">The validation shelf byte cache keyed by shelf file offset.</param>
    /// <param name="identity">The generated shared identity value.</param>
    private delegate void MultiIndexTupleValidator<TProfile>(
        LibraDexFileSession session,
        long rootRouterOffset,
        TProfile profile,
        Dictionary<long, byte[]> shelfCache,
        ulong identity);


    /// <summary>
    /// Validates one generated identity in the reopened `SS8-8` index.<br/>
    /// The key projection for this anchor index is identity-as-key, so the same encoded scalar must be present as both tuple key and tuple identity.<br/>
    /// </summary>
    /// <param name="session">The reopened file session.</param>
    /// <param name="rootRouterOffset">The `SS8-8` root-router offset.</param>
    /// <param name="profile">The `SS8-8` shelf profile.</param>
    /// <param name="shelfCache">The validation shelf byte cache keyed by shelf file offset.</param>
    /// <param name="identity">The generated shared identity value.</param>
    private static void ValidateMultiIndexScalar8Scalar8Tuple(
        LibraDexFileSession session,
        long rootRouterOffset,
        Scalar8Scalar8Profile profile,
        Dictionary<long, byte[]> shelfCache,
        ulong identity)
    {
        Scalar8Scalar8RoutePathTarget pathTarget = session.WalkScalar8Scalar8RoutePathTarget(
            rootRouterOffset,
            identity,
            maxRouterHops: 8,
            Scalar8Scalar8RouteReadPolicy.PreferArenaCache);
        if (pathTarget.Target.Kind != Scalar8Scalar8RouteTargetKind.Shelf)
        {
            throw new InvalidDataException("Multi-index same-identities SS8-8 route did not terminate at an SS8-8 shelf.");
        }

        if (!shelfCache.TryGetValue(pathTarget.Target.Offset, out byte[]? shelfBytes))
        {
            shelfBytes = session.ReadScalar8Scalar8ShelfBytesForBatch(pathTarget.Target.Offset, profile);
            shelfCache[pathTarget.Target.Offset] = shelfBytes;
        }

        Scalar8Scalar8ReadOnly shelf = new(shelfBytes, profile);
        if (!shelf.IsValid || !shelf.Contains(identity, identity))
        {
            throw new InvalidDataException("Multi-index same-identities SS8-8 validation could not find the expected identity tuple.");
        }
    }


    /// <summary>
    /// Validates one generated identity in the reopened `SS16-8` index.<br/>
    /// The widened key projection derives a deterministic low key half from the shared identity while preserving the same 8-byte identity payload.<br/>
    /// </summary>
    /// <param name="session">The reopened file session.</param>
    /// <param name="rootRouterOffset">The `SS16-8` root-router offset.</param>
    /// <param name="profile">The `SS16-8` shelf profile.</param>
    /// <param name="shelfCache">The validation shelf byte cache keyed by shelf file offset.</param>
    /// <param name="identity">The generated shared identity value.</param>
    private static void ValidateMultiIndexScalar16Scalar8Tuple(
        LibraDexFileSession session,
        long rootRouterOffset,
        Scalar16Scalar8Profile profile,
        Dictionary<long, byte[]> shelfCache,
        ulong identity)
    {
        ulong keyLow = CreateSqliteScalar16Scalar16LowHalf(identity);
        Scalar16Scalar8RoutePathTarget pathTarget = session.WalkScalar16Scalar8RoutePathTarget(
            rootRouterOffset,
            identity,
            keyLow,
            maxRouterHops: 8);
        if (pathTarget.Target.Kind != Scalar16Scalar8RouteTargetKind.Shelf)
        {
            throw new InvalidDataException("Multi-index same-identities SS16-8 route did not terminate at an SS16-8 shelf.");
        }

        if (!shelfCache.TryGetValue(pathTarget.Target.Offset, out byte[]? shelfBytes))
        {
            shelfBytes = session.ReadScalar16Scalar8ShelfBytesForBatch(pathTarget.Target.Offset, profile);
            shelfCache[pathTarget.Target.Offset] = shelfBytes;
        }

        Scalar16Scalar8ReadOnly shelf = new(shelfBytes, profile);
        if (!shelf.IsValid || !shelf.Contains(identity, keyLow, identity))
        {
            throw new InvalidDataException("Multi-index same-identities SS16-8 validation could not find the expected identity tuple.");
        }
    }


    /// <summary>
    /// Validates one generated identity in the reopened `SS8-16` index.<br/>
    /// Key high is the generated 8-byte identity and identity low is the deterministic widened half used by the write loop.<br/>
    /// </summary>
    /// <param name="session">The reopened file session.</param>
    /// <param name="rootRouterOffset">The `SS8-16` root-router offset.</param>
    /// <param name="profile">The `SS8-16` shelf profile.</param>
    /// <param name="shelfCache">The validation shelf byte cache keyed by shelf file offset.</param>
    /// <param name="identity">The generated shared identity value.</param>
    private static void ValidateMultiIndexScalar8Scalar16Tuple(
        LibraDexFileSession session,
        long rootRouterOffset,
        Scalar8Scalar16Profile profile,
        Dictionary<long, byte[]> shelfCache,
        ulong identity)
    {
        ulong identityLow = CreateSqliteScalar16Scalar16LowHalf(identity);
        Scalar8Scalar16RoutePathTarget pathTarget = session.WalkScalar8Scalar16RoutePathTarget(
            rootRouterOffset,
            identity,
            maxRouterHops: 8);
        if (pathTarget.Target.Kind != Scalar8Scalar16RouteTargetKind.Shelf)
        {
            throw new InvalidDataException("Eight-index same-identities SS8-16 route did not terminate at an SS8-16 shelf.");
        }

        if (!shelfCache.TryGetValue(pathTarget.Target.Offset, out byte[]? shelfBytes))
        {
            shelfBytes = session.ReadScalar8Scalar16ShelfBytesForBatch(pathTarget.Target.Offset, profile);
            shelfCache[pathTarget.Target.Offset] = shelfBytes;
        }

        Scalar8Scalar16ReadOnly shelf = new(shelfBytes, profile);
        if (!shelf.IsValid || !shelf.Contains(identity, identity, identityLow))
        {
            throw new InvalidDataException("Eight-index same-identities SS8-16 validation could not find the expected identity tuple.");
        }
    }


    /// <summary>
    /// Validates one generated identity in the reopened `SS16-16` index.<br/>
    /// The current write loop uses the same high/low pair for key and identity, which is sufficient for same-file multi-shape isolation proof.<br/>
    /// </summary>
    /// <param name="session">The reopened file session.</param>
    /// <param name="rootRouterOffset">The `SS16-16` root-router offset.</param>
    /// <param name="profile">The `SS16-16` shelf profile.</param>
    /// <param name="shelfCache">The validation shelf byte cache keyed by shelf file offset.</param>
    /// <param name="identity">The generated shared identity value.</param>
    private static void ValidateMultiIndexScalar16Scalar16Tuple(
        LibraDexFileSession session,
        long rootRouterOffset,
        Scalar16Scalar16Profile profile,
        Dictionary<long, byte[]> shelfCache,
        ulong identity)
    {
        ulong keyLow = CreateSqliteScalar16Scalar16LowHalf(identity);
        Scalar16Scalar16RoutePathTarget pathTarget = session.WalkScalar16Scalar16RoutePathTarget(
            rootRouterOffset,
            identity,
            keyLow,
            maxRouterHops: 8);
        if (pathTarget.Target.Kind != Scalar16Scalar16RouteTargetKind.Shelf)
        {
            throw new InvalidDataException("Eight-index same-identities SS16-16 route did not terminate at an SS16-16 shelf.");
        }

        if (!shelfCache.TryGetValue(pathTarget.Target.Offset, out byte[]? shelfBytes))
        {
            shelfBytes = session.ReadScalar16Scalar16ShelfBytesForBatch(pathTarget.Target.Offset, profile);
            shelfCache[pathTarget.Target.Offset] = shelfBytes;
        }

        Scalar16Scalar16ReadOnly shelf = new(shelfBytes, profile);
        if (!shelf.IsValid || !shelf.Contains(identity, keyLow, identity, keyLow))
        {
            throw new InvalidDataException("Eight-index same-identities SS16-16 validation could not find the expected identity tuple.");
        }
    }


    /// <summary>
    /// Executes `SS8-8` routed bulk writes against one already-created same-file plan root.<br/>
    /// This wraps the internal index handle with the existing public batch object without taking ownership of the shared session.<br/>
    /// </summary>
    /// <param name="session">The shared LibraDex file session.</param>
    /// <param name="plan">The `SS8-8` plan with an initialized handle.</param>
    /// <param name="batches">The generated batch count.</param>
    /// <param name="itemsPerBatch">The generated item count per batch.</param>
    /// <param name="prefixCount">The generated root-prefix count.</param>
    /// <param name="order">The deterministic item order vector.</param>
    /// <returns>The routed write telemetry.</returns>
    private static RoutedBulkWriteLoopTelemetry RunMultiIndexScalar8Scalar8Write(
        LibraDexFileSession session,
        MultiIndexSameIdentitiesPlan plan,
        int batches,
        int itemsPerBatch,
        int prefixCount,
        int[] order)
    {
        using Scalar8Scalar8Index index = new(session, plan.Scalar8Scalar8Handle, plan.SlotIndex, plan.Name, ownsSession: false);
        return RunScalar8Scalar8RoutedBulkWriteLoopBatchMajor(index, batches, itemsPerBatch, prefixCount, order, batchOffset: 0);
    }


    /// <summary>
    /// Runs the common generated-identity traversal for a fixed-shape eight-index reopen validation.<br/>
    /// Shape-specific tuple validation remains direct through the supplied typed validator so shelf reads and tuple predicates stay explicit.<br/>
    /// </summary>
    /// <typeparam name="TProfile">The fixed-shape shelf profile type.</typeparam>
    /// <param name="session">The reopened file session.</param>
    /// <param name="rootOffset">The plan root-router offset.</param>
    /// <param name="profile">The fixed-shape shelf profile.</param>
    /// <param name="batches">The generated batch count.</param>
    /// <param name="itemsPerBatch">The generated item count per batch.</param>
    /// <param name="prefixCount">The generated root-prefix count.</param>
    /// <param name="order">The deterministic item order vector.</param>
    /// <param name="validateTuple">The shape-specific tuple validator.</param>
    /// <returns>The validation counts.</returns>
    private static MultiIndexValidationCounts ValidateEightIndexReopen<TProfile>(
        LibraDexFileSession session,
        long rootOffset,
        TProfile profile,
        int batches,
        int itemsPerBatch,
        int prefixCount,
        int[] order,
        MultiIndexTupleValidator<TProfile> validateTuple)
    {
        Dictionary<long, byte[]> cache = [];
        int validated = 0;
        for (int batch = 0; batch < batches; batch++)
        {
            for (int i = 0; i < itemsPerBatch; i++)
            {
                ulong identity = CreateRoutedBulkWriteKey(batch, order[i], itemsPerBatch, prefixCount);
                validateTuple(session, rootOffset, profile, cache, identity);
                validated++;
            }
        }

        return new MultiIndexValidationCounts(cache.Count, 0, validated, 0);
    }


    /// <summary>
    /// Validates one reopened `SS8-8` plan by routing every generated identity to an `SS8-8` shelf and checking tuple presence.<br/>
    /// The returned counts use the generic validation-count record's first fields so the report path can stay shape-agnostic.<br/>
    /// </summary>
    /// <param name="session">The reopened file session.</param>
    /// <param name="rootOffset">The plan root-router offset.</param>
    /// <param name="profile">The `SS8-8` shelf profile.</param>
    /// <param name="batches">The generated batch count.</param>
    /// <param name="itemsPerBatch">The generated item count per batch.</param>
    /// <param name="prefixCount">The generated root-prefix count.</param>
    /// <param name="order">The deterministic item order vector.</param>
    /// <returns>The validation counts.</returns>
    private static MultiIndexValidationCounts ValidateEightIndexScalar8Scalar8Reopen(
        LibraDexFileSession session,
        long rootOffset,
        Scalar8Scalar8Profile profile,
        int batches,
        int itemsPerBatch,
        int prefixCount,
        int[] order)
    {
        return ValidateEightIndexReopen(session, rootOffset, profile, batches, itemsPerBatch, prefixCount, order, ValidateMultiIndexScalar8Scalar8Tuple);
    }


    /// <summary>
    /// Validates one reopened `SS16-8` plan by routing every generated identity to an `SS16-8` shelf and checking tuple presence.<br/>
    /// The widened key low half follows the same deterministic projection used by the routed write loop.<br/>
    /// </summary>
    /// <param name="session">The reopened file session.</param>
    /// <param name="rootOffset">The plan root-router offset.</param>
    /// <param name="profile">The `SS16-8` shelf profile.</param>
    /// <param name="batches">The generated batch count.</param>
    /// <param name="itemsPerBatch">The generated item count per batch.</param>
    /// <param name="prefixCount">The generated root-prefix count.</param>
    /// <param name="order">The deterministic item order vector.</param>
    /// <returns>The validation counts.</returns>
    private static MultiIndexValidationCounts ValidateEightIndexScalar16Scalar8Reopen(
        LibraDexFileSession session,
        long rootOffset,
        Scalar16Scalar8Profile profile,
        int batches,
        int itemsPerBatch,
        int prefixCount,
        int[] order)
    {
        return ValidateEightIndexReopen(session, rootOffset, profile, batches, itemsPerBatch, prefixCount, order, ValidateMultiIndexScalar16Scalar8Tuple);
    }


    /// <summary>
    /// Validates one reopened `SS8-16` plan by routing every generated key to an `SS8-16` shelf and checking the 16-byte identity tuple.<br/>
    /// Key high is the generated 8-byte identity and identity low is the deterministic widened half used by the write loop.<br/>
    /// </summary>
    /// <param name="session">The reopened file session.</param>
    /// <param name="rootOffset">The plan root-router offset.</param>
    /// <param name="profile">The `SS8-16` shelf profile.</param>
    /// <param name="batches">The generated batch count.</param>
    /// <param name="itemsPerBatch">The generated item count per batch.</param>
    /// <param name="prefixCount">The generated root-prefix count.</param>
    /// <param name="order">The deterministic item order vector.</param>
    /// <returns>The validation counts.</returns>
    private static MultiIndexValidationCounts ValidateEightIndexScalar8Scalar16Reopen(
        LibraDexFileSession session,
        long rootOffset,
        Scalar8Scalar16Profile profile,
        int batches,
        int itemsPerBatch,
        int prefixCount,
        int[] order)
    {
        return ValidateEightIndexReopen(session, rootOffset, profile, batches, itemsPerBatch, prefixCount, order, ValidateMultiIndexScalar8Scalar16Tuple);
    }


    /// <summary>
    /// Validates one reopened `SS16-16` plan by routing every generated widened key to an `SS16-16` shelf and checking the 16-byte identity tuple.<br/>
    /// The current write loop uses the same high/low pair for key and identity, which is sufficient for same-file multi-shape isolation proof.<br/>
    /// </summary>
    /// <param name="session">The reopened file session.</param>
    /// <param name="rootOffset">The plan root-router offset.</param>
    /// <param name="profile">The `SS16-16` shelf profile.</param>
    /// <param name="batches">The generated batch count.</param>
    /// <param name="itemsPerBatch">The generated item count per batch.</param>
    /// <param name="prefixCount">The generated root-prefix count.</param>
    /// <param name="order">The deterministic item order vector.</param>
    /// <returns>The validation counts.</returns>
    private static MultiIndexValidationCounts ValidateEightIndexScalar16Scalar16Reopen(
        LibraDexFileSession session,
        long rootOffset,
        Scalar16Scalar16Profile profile,
        int batches,
        int itemsPerBatch,
        int prefixCount,
        int[] order)
    {
        return ValidateEightIndexReopen(session, rootOffset, profile, batches, itemsPerBatch, prefixCount, order, ValidateMultiIndexScalar16Scalar16Tuple);
    }


    /// <summary>
    /// Measures one routed range/scoop scenario over the same encoded key range.<br/>
    /// Scratch buffers are reused across iterations so the measured operation stays close to the allocation-free library path.<br/>
    /// </summary>
    /// <param name="name">The scenario name.</param>
    /// <param name="session">The active file session.</param>
    /// <param name="rootRouterOffset">The root router offset.</param>
    /// <param name="profile">The `SS8-8` shelf profile.</param>
    /// <param name="lowerKey">The inclusive lower encoded key.</param>
    /// <param name="upperKey">The inclusive upper encoded key.</param>
    /// <param name="iterations">The number of range scoops to perform.</param>
    /// <param name="expectedCount">The expected identity count per range.</param>
    /// <param name="readPolicy">The route-read policy to apply.</param>
    /// <param name="identities">The reusable identity output buffer.</param>
    /// <param name="shelfBuffer">The reusable shelf read buffer.</param>
    /// <returns>The measured perf result.</returns>
    private static Scalar8Scalar8RangeScoopPerfResult MeasureScalar8Scalar8RangeScoopScenario(
        string name,
        LibraDexFileSession session,
        long rootRouterOffset,
        Scalar8Scalar8Profile profile,
        ulong lowerKey,
        ulong upperKey,
        int iterations,
        int expectedCount,
        Scalar8Scalar8RouteReadPolicy readPolicy,
        Span<ulong> identities,
        Span<byte> shelfBuffer,
        int validationKind)
    {
        session.ClearRouterArenaReadCacheForValidation();
        _ = session.GetAndResetReadTelemetry();
        long checksum = 0;
        long identityCount = 0;
        Stopwatch stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            int count = session.ScoopScalar8Scalar8IdentityRange(
                rootRouterOffset,
                profile,
                lowerKey,
                upperKey,
                maxRouterHops: 8,
                readPolicy,
                identities,
                shelfBuffer);
            checksum += validationKind switch
            {
                0 => CheckScalar8Scalar8RangeScoop(lowerKey, identities, count, expectedCount),
                1 => CheckScalar8Scalar8CrossShelfRangeScoop(identities, count),
                2 => CheckScalar8Scalar8TraversalRangeScoop(identities, count),
                3 => CheckScalar8Scalar8WideRangeScoop(lowerKey, upperKey, identities, count, expectedCount),
                _ => throw new InvalidOperationException($"Unsupported range scoop validation kind {validationKind}.")
            };
            identityCount += count;
        }

        stopwatch.Stop();
        DataKernelReadTelemetry readTelemetry = session.GetAndResetReadTelemetry();
        return new Scalar8Scalar8RangeScoopPerfResult(name, iterations, expectedCount, identityCount, stopwatch.Elapsed, readTelemetry.ReadCallCount, readTelemetry.BackingReadCallCount, readTelemetry.BytesRead, checksum);
    }


    /// <summary>
    /// Measures one routed range/scoop scenario through the coalesced multi-shelf scratch path.<br/>
    /// The scratch buffer is caller-owned and reused across iterations so the measured operation reflects the intended low-allocation library contract.<br/>
    /// This probe currently validates the wide direct-root vector, where returned identities are grouped by consecutive root prefixes.<br/>
    /// </summary>
    /// <param name="name">The scenario name.</param>
    /// <param name="session">The active file session.</param>
    /// <param name="rootRouterOffset">The root router offset.</param>
    /// <param name="profile">The `SS8-8` shelf profile.</param>
    /// <param name="lowerKey">The inclusive lower encoded key.</param>
    /// <param name="upperKey">The inclusive upper encoded key.</param>
    /// <param name="iterations">The number of range scoops to perform.</param>
    /// <param name="expectedCount">The expected identity count per range.</param>
    /// <param name="readPolicy">The route-read policy to apply.</param>
    /// <param name="identities">The reusable identity output buffer.</param>
    /// <param name="rangeScratch">The reusable multi-shelf range scratch buffer.</param>
    /// <returns>The measured perf result.</returns>
    private static Scalar8Scalar8RangeScoopPerfResult MeasureScalar8Scalar8RangeScoopCoalescedScenario(
        string name,
        LibraDexFileSession session,
        long rootRouterOffset,
        Scalar8Scalar8Profile profile,
        ulong lowerKey,
        ulong upperKey,
        int iterations,
        int expectedCount,
        Scalar8Scalar8RouteReadPolicy readPolicy,
        Span<ulong> identities,
        Span<byte> rangeScratch,
        int validationKind)
    {
        session.ClearRouterArenaReadCacheForValidation();
        _ = session.GetAndResetReadTelemetry();
        long checksum = 0;
        long identityCount = 0;
        Stopwatch stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            int count = session.ScoopScalar8Scalar8IdentityRangeCoalesced(
                rootRouterOffset,
                profile,
                lowerKey,
                upperKey,
                maxRouterHops: 8,
                readPolicy,
                identities,
                rangeScratch);
            checksum += validationKind switch
            {
                2 => CheckScalar8Scalar8TraversalRangeScoop(identities, count),
                3 => CheckScalar8Scalar8WideRangeScoop(lowerKey, upperKey, identities, count, expectedCount),
                _ => throw new InvalidOperationException($"Unsupported coalesced range scoop validation kind {validationKind}.")
            };
            identityCount += count;
        }

        stopwatch.Stop();
        DataKernelReadTelemetry readTelemetry = session.GetAndResetReadTelemetry();
        return new Scalar8Scalar8RangeScoopPerfResult(name, iterations, expectedCount, identityCount, stopwatch.Elapsed, readTelemetry.ReadCallCount, readTelemetry.BackingReadCallCount, readTelemetry.BytesRead, checksum);
    }


    /// <summary>
    /// Measures one routed range/scoop scenario through the first storage-facing range-read API shape.<br/>
    /// The API receives explicit options plus caller-owned shelf and range scratch, which lets the harness compare ergonomic dispatch cost against the lower-level primitives without changing allocation behavior.<br/>
    /// </summary>
    /// <param name="name">The scenario name.</param>
    /// <param name="session">The active file session.</param>
    /// <param name="rootRouterOffset">The root router offset.</param>
    /// <param name="profile">The `SS8-8` shelf profile.</param>
    /// <param name="lowerKey">The inclusive lower encoded key.</param>
    /// <param name="upperKey">The inclusive upper encoded key.</param>
    /// <param name="iterations">The number of range scoops to perform.</param>
    /// <param name="expectedCount">The expected identity count per range.</param>
    /// <param name="options">The range-read policy used by the API.</param>
    /// <param name="identities">The reusable identity output buffer.</param>
    /// <param name="shelfScratch">The reusable one-shelf scratch buffer.</param>
    /// <param name="rangeScratch">The reusable multi-shelf range scratch buffer.</param>
    /// <param name="validationKind">The validation vector kind to apply after each read.</param>
    /// <returns>The measured perf result.</returns>
    private static Scalar8Scalar8RangeScoopPerfResult MeasureScalar8Scalar8RangeReadApiScenario(
        string name,
        LibraDexFileSession session,
        long rootRouterOffset,
        Scalar8Scalar8Profile profile,
        ulong lowerKey,
        ulong upperKey,
        int iterations,
        int expectedCount,
        Scalar8Scalar8RangeReadOptions options,
        Span<ulong> identities,
        Span<byte> shelfScratch,
        Span<byte> rangeScratch,
        int validationKind)
    {
        session.ClearRouterArenaReadCacheForValidation();
        _ = session.GetAndResetReadTelemetry();
        long checksum = 0;
        long identityCount = 0;
        Scalar8Scalar8IndexHandle index = session.GetScalar8Scalar8IndexHandleByRootRouterOffset(rootRouterOffset);
        Scalar8Scalar8RangeReadScratch scratch = new(shelfScratch, rangeScratch);
        Stopwatch stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            Scalar8Scalar8RangeReadResult result = session.ReadEncodedScalar8Scalar8IdentityRange(
                index,
                lowerKey,
                upperKey,
                options,
                identities,
                scratch);
            int count = result.IdentityCount;
            checksum += validationKind switch
            {
                2 => CheckScalar8Scalar8TraversalRangeScoop(identities, count),
                3 => CheckScalar8Scalar8WideRangeScoop(lowerKey, upperKey, identities, count, expectedCount),
                _ => throw new InvalidOperationException($"Unsupported API range read validation kind {validationKind}.")
            };
            identityCount += count;
        }

        stopwatch.Stop();
        DataKernelReadTelemetry readTelemetry = session.GetAndResetReadTelemetry();
        return new Scalar8Scalar8RangeScoopPerfResult(name, iterations, expectedCount, identityCount, stopwatch.Elapsed, readTelemetry.ReadCallCount, readTelemetry.BackingReadCallCount, readTelemetry.BytesRead, checksum);
    }


    /// <summary>
    /// Measures one routed range/scoop scenario through the pooled range-read wrapper.<br/>
    /// This intentionally includes pool rent/return work on every measured operation so the result describes the low-friction wrapper, not the reusable-scratch hot path.<br/>
    /// </summary>
    /// <param name="name">The scenario name.</param>
    /// <param name="session">The active file session.</param>
    /// <param name="rootRouterOffset">The root router offset.</param>
    /// <param name="profile">The `SS8-8` shelf profile.</param>
    /// <param name="lowerKey">The inclusive lower encoded key.</param>
    /// <param name="upperKey">The inclusive upper encoded key.</param>
    /// <param name="iterations">The number of range reads to perform.</param>
    /// <param name="expectedCount">The expected identity count per range.</param>
    /// <param name="options">The pooled range-read policy.</param>
    /// <param name="identities">The reusable identity output buffer.</param>
    /// <param name="validationKind">The validation vector kind to apply after each read.</param>
    /// <returns>The measured perf result.</returns>
    private static Scalar8Scalar8RangeScoopPerfResult MeasureScalar8Scalar8RangeReadPooledScenario(
        string name,
        LibraDexFileSession session,
        long rootRouterOffset,
        Scalar8Scalar8Profile profile,
        ulong lowerKey,
        ulong upperKey,
        int iterations,
        int expectedCount,
        Scalar8Scalar8RangeReadOptions options,
        Span<ulong> identities,
        int validationKind)
    {
        session.ClearRouterArenaReadCacheForValidation();
        _ = session.GetAndResetReadTelemetry();
        long checksum = 0;
        long identityCount = 0;
        Scalar8Scalar8IndexHandle index = session.GetScalar8Scalar8IndexHandleByRootRouterOffset(rootRouterOffset);
        Stopwatch stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            Scalar8Scalar8RangeReadResult result = session.ReadEncodedScalar8Scalar8IdentityRangePooled(
                index,
                lowerKey,
                upperKey,
                options,
                identities);
            int count = result.IdentityCount;
            checksum += validationKind switch
            {
                2 => CheckScalar8Scalar8TraversalRangeScoop(identities, count),
                3 => CheckScalar8Scalar8WideRangeScoop(lowerKey, upperKey, identities, count, expectedCount),
                _ => throw new InvalidOperationException($"Unsupported pooled range read validation kind {validationKind}.")
            };
            identityCount += count;
        }

        stopwatch.Stop();
        DataKernelReadTelemetry readTelemetry = session.GetAndResetReadTelemetry();
        return new Scalar8Scalar8RangeScoopPerfResult(name, iterations, expectedCount, identityCount, stopwatch.Elapsed, readTelemetry.ReadCallCount, readTelemetry.BackingReadCallCount, readTelemetry.BytesRead, checksum);
    }


    /// <summary>
    /// Validates a routed range/scoop identity span and returns a compact checksum.<br/>
    /// The current vectors use identities that equal encoded keys, making slot-order or boundary mistakes visible.<br/>
    /// </summary>
    /// <param name="lowerKey">The expected first encoded identity.</param>
    /// <param name="identities">The returned encoded identity span.</param>
    /// <param name="actualCount">The number of identities returned by the scoop.</param>
    /// <param name="expectedCount">The expected number of identities.</param>
    /// <returns>A checksum over the returned identities.</returns>
    private static long CheckScalar8Scalar8RangeScoop(
        ulong lowerKey,
        ReadOnlySpan<ulong> identities,
        int actualCount,
        int expectedCount)
    {
        if (actualCount != expectedCount)
        {
            throw new InvalidDataException($"Unexpected range scoop count {actualCount}; expected {expectedCount}.");
        }

        long checksum = 0;
        for (int i = 0; i < actualCount; i++)
        {
            ulong expected = lowerKey + (ulong)i;
            if (identities[i] != expected)
            {
                throw new InvalidDataException($"Unexpected range scoop identity at index {i}: {identities[i]} expected {expected}.");
            }

            checksum += unchecked((long)((identities[i] >> 32) ^ identities[i] ^ (ulong)(i + 1)));
        }

        return checksum;
    }


    /// <summary>
    /// Validates the first cross-shelf range/scoop identity span and returns a compact checksum.<br/>
    /// The expected vector contains the tail of the transformed left shelf followed by the head of the transformed right shelf.<br/>
    /// </summary>
    /// <param name="identities">The returned encoded identity span.</param>
    /// <param name="actualCount">The number of identities returned by the scoop.</param>
    /// <returns>A checksum over the returned identities.</returns>
    private static long CheckScalar8Scalar8CrossShelfRangeScoop(
        ReadOnlySpan<ulong> identities,
        int actualCount)
    {
        const int leftTailStart = 900;
        const int leftTailCount = 9;
        const int rightHeadCount = 32;
        const ulong rightPrefixBase = 0x0080_0000_0000_0000UL;
        const int expectedCount = leftTailCount + rightHeadCount;
        if (actualCount != expectedCount)
        {
            throw new InvalidDataException($"Unexpected cross-shelf range scoop count {actualCount}; expected {expectedCount}.");
        }

        long checksum = 0;
        for (int i = 0; i < actualCount; i++)
        {
            ulong expected = i < leftTailCount
                ? (ulong)(leftTailStart + i)
                : rightPrefixBase + (ulong)(i - leftTailCount);
            if (identities[i] != expected)
            {
                throw new InvalidDataException($"Unexpected cross-shelf range scoop identity at index {i}: {identities[i]} expected {expected}.");
            }

            checksum += unchecked((long)((identities[i] >> 32) ^ identities[i] ^ (ulong)(i + 1)));
        }

        return checksum;
    }


    /// <summary>
    /// Validates the generalized router-traversal range/scoop identity span and returns a compact checksum.<br/>
    /// The expected vector contains the transformed right-shelf tail, its high outlier, then the direct root-prefix shelf head.<br/>
    /// </summary>
    /// <param name="identities">The returned encoded identity span.</param>
    /// <param name="actualCount">The number of identities returned by the scoop.</param>
    /// <returns>A checksum over the returned identities.</returns>
    private static long CheckScalar8Scalar8TraversalRangeScoop(
        ReadOnlySpan<ulong> identities,
        int actualCount)
    {
        const int rightTailStart = 900;
        const int rightTailCount = 9;
        const int directHeadCount = 32;
        const ulong rightPrefixBase = 0x0080_0000_0000_0000UL;
        const ulong directPrefixBase = 0x0100_0000_0000_0000UL;
        const int expectedCount = rightTailCount + 1 + directHeadCount;
        if (actualCount != expectedCount)
        {
            throw new InvalidDataException($"Unexpected traversal range scoop count {actualCount}; expected {expectedCount}.");
        }

        long checksum = 0;
        for (int i = 0; i < actualCount; i++)
        {
            ulong expected = i switch
            {
                < rightTailCount => rightPrefixBase + (ulong)(rightTailStart + i),
                rightTailCount => rightPrefixBase + 10_000UL,
                _ => directPrefixBase + (ulong)(i - rightTailCount - 1)
            };

            if (identities[i] != expected)
            {
                throw new InvalidDataException($"Unexpected traversal range scoop identity at index {i}: {identities[i]} expected {expected}.");
            }

            checksum += unchecked((long)((identities[i] >> 32) ^ identities[i] ^ (ulong)(i + 1)));
        }

        return checksum;
    }


    /// <summary>
    /// Validates a wide root-prefix range/scoop identity span and returns a compact checksum.<br/>
    /// The expected vector is grouped by consecutive root prefixes, with each shelf containing identities from prefix base plus local item ordinal.<br/>
    /// This makes accidental route skipping, duplicate shelf visits, and output-order regressions visible in the wide-range perf probe.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower encoded key used for the range.</param>
    /// <param name="upperKey">The inclusive upper encoded key used for the range.</param>
    /// <param name="identities">The returned encoded identity span.</param>
    /// <param name="actualCount">The number of identities returned by the scoop.</param>
    /// <param name="expectedCount">The expected number of identities.</param>
    /// <returns>A checksum over the returned identities.</returns>
    private static long CheckScalar8Scalar8WideRangeScoop(
        ulong lowerKey,
        ulong upperKey,
        ReadOnlySpan<ulong> identities,
        int actualCount,
        int expectedCount)
    {
        if (actualCount != expectedCount)
        {
            throw new InvalidDataException($"Unexpected wide range scoop count {actualCount}; expected {expectedCount}.");
        }

        int lowerPrefix = (int)(lowerKey >> 56);
        int upperPrefix = (int)(upperKey >> 56);
        int prefixCount = upperPrefix - lowerPrefix + 1;
        if (prefixCount <= 0 || expectedCount % prefixCount != 0)
        {
            throw new InvalidDataException("Wide range scoop expected count does not divide evenly across the prefix interval.");
        }

        int itemsPerPrefix = expectedCount / prefixCount;
        long checksum = 0;
        for (int i = 0; i < actualCount; i++)
        {
            int prefix = lowerPrefix + (i / itemsPerPrefix);
            int localIndex = i % itemsPerPrefix;
            ulong expected = ((ulong)(byte)prefix << 56) + (ulong)localIndex;
            if (identities[i] != expected)
            {
                throw new InvalidDataException($"Unexpected wide range scoop identity at index {i}: {identities[i]} expected {expected}.");
            }

            checksum += unchecked((long)((identities[i] >> 32) ^ identities[i] ^ (ulong)(i + 1)));
        }

        return checksum;
    }


    /// <summary>
    /// Prints the routed `SS8-8` range/scoop perf table to the console.<br/>
    /// </summary>
    /// <param name="results">The measured range/scoop results.</param>
    private static void PrintScalar8Scalar8RangeScoopPerf(ReadOnlySpan<Scalar8Scalar8RangeScoopPerfResult> results)
    {
        Console.WriteLine("| scenario | iterations | range | elapsed ms | ns/range | ns/identity | reads | backing reads | bytes | bytes/identity | checksum |");
        Console.WriteLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        for (int i = 0; i < results.Length; i++)
        {
            Scalar8Scalar8RangeScoopPerfResult result = results[i];
            double nsPerRange = result.Elapsed.TotalSeconds * 1_000_000_000d / result.Iterations;
            double nsPerIdentity = result.Elapsed.TotalSeconds * 1_000_000_000d / result.IdentityCount;
            double bytesPerIdentity = result.BytesRead / (double)result.IdentityCount;
            Console.WriteLine($"| {result.Name} | {result.Iterations} | {result.RangeLength} | {result.Elapsed.TotalMilliseconds:N3} | {nsPerRange:N2} | {nsPerIdentity:N2} | {result.ReadCallCount} | {result.BackingReadCallCount} | {result.BytesRead} | {bytesPerIdentity:N2} | {result.Checksum} |");
        }
    }


    /// <summary>
    /// Measures one named `SS8-8` design-performance scenario across repeated fresh-file samples.<br/>
    /// The fresh-file pattern keeps each mutation shape isolated and avoids depending on not-yet-implemented deeper split orchestration.<br/>
    /// </summary>
    /// <param name="name">The scenario name.</param>
    /// <param name="runs">The number of samples to collect.</param>
    /// <param name="runner">The scenario runner that returns one measured sample.</param>
    /// <param name="directory">The directory where scenario files are written.</param>
    /// <param name="profile">The `SS8-8` shelf profile.</param>
    /// <returns>The aggregated scenario result.</returns>
    private static Scalar8Scalar8DesignPerfResult MeasureScalar8Scalar8DesignScenario(
        string name,
        int runs,
        Scalar8Scalar8DesignScenarioRunner runner,
        string directory,
        Scalar8Scalar8Profile profile)
    {
        long elapsedTicks = 0;
        long readCalls = 0;
        long readBytes = 0;
        long writeCalls = 0;
        long writeBytes = 0;
        long setLengthCalls = 0;
        double[] elapsedMillisecondsSamples = new double[runs];
        for (int i = 0; i < runs; i++)
        {
            string safeName = name.Replace(' ', '-');
            string path = Path.Combine(directory, $"{safeName}-{i:D4}.lbdx");
            File.Delete(path);
            Scalar8Scalar8DesignPerfSample sample = runner(path, profile);
            elapsedTicks += sample.ElapsedTicks;
            elapsedMillisecondsSamples[i] = sample.ElapsedTicks * 1000.0 / Stopwatch.Frequency;
            readCalls += sample.ReadCallCount;
            readBytes += sample.BytesRead;
            writeCalls += sample.WriteCallCount;
            writeBytes += sample.BytesWritten;
            setLengthCalls += sample.SetLengthCallCount;
        }

        double elapsedMilliseconds = elapsedTicks * 1000.0 / Stopwatch.Frequency;
        Scalar8Scalar8MetricStats elapsedStats = ComputeStats(elapsedMillisecondsSamples);
        return new Scalar8Scalar8DesignPerfResult(
            name,
            runs,
            elapsedMilliseconds / runs,
            elapsedStats.Median,
            elapsedStats.Min,
            elapsedStats.Max,
            elapsedStats.StandardDeviation,
            readCalls / (double)runs,
            readBytes / (double)runs,
            writeCalls / (double)runs,
            writeBytes / (double)runs,
            setLengthCalls / (double)runs);
    }


    /// <summary>
    /// Measures one named `SS16-8` design-performance scenario across repeated fresh-file samples.<br/>
    /// The fresh-file pattern matches the `SS8-8` baseline command so doubled-key cost is visible without setup state bleed-through.<br/>
    /// </summary>
    /// <param name="name">The scenario name.</param>
    /// <param name="runs">The number of samples to collect.</param>
    /// <param name="runner">The scenario runner that returns one measured sample.</param>
    /// <param name="directory">The directory where scenario files are written.</param>
    /// <param name="profile">The `SS16-8` shelf profile.</param>
    /// <returns>The aggregated scenario result.</returns>
    private static Scalar8Scalar8DesignPerfResult MeasureScalar16Scalar8DesignScenario(
        string name,
        int runs,
        Scalar16Scalar8DesignScenarioRunner runner,
        string directory,
        Scalar16Scalar8Profile profile)
    {
        long elapsedTicks = 0;
        long readCalls = 0;
        long readBytes = 0;
        long writeCalls = 0;
        long writeBytes = 0;
        long setLengthCalls = 0;
        double[] elapsedMillisecondsSamples = new double[runs];
        for (int i = 0; i < runs; i++)
        {
            string safeName = name.Replace(' ', '-');
            string path = Path.Combine(directory, $"{safeName}-{i:D4}.lbdx");
            File.Delete(path);
            Scalar8Scalar8DesignPerfSample sample = runner(path, profile);
            elapsedTicks += sample.ElapsedTicks;
            elapsedMillisecondsSamples[i] = sample.ElapsedTicks * 1000.0 / Stopwatch.Frequency;
            readCalls += sample.ReadCallCount;
            readBytes += sample.BytesRead;
            writeCalls += sample.WriteCallCount;
            writeBytes += sample.BytesWritten;
            setLengthCalls += sample.SetLengthCallCount;
        }

        double elapsedMilliseconds = elapsedTicks * 1000.0 / Stopwatch.Frequency;
        Scalar8Scalar8MetricStats elapsedStats = ComputeStats(elapsedMillisecondsSamples);
        return new Scalar8Scalar8DesignPerfResult(
            name,
            runs,
            elapsedMilliseconds / runs,
            elapsedStats.Median,
            elapsedStats.Min,
            elapsedStats.Max,
            elapsedStats.StandardDeviation,
            readCalls / (double)runs,
            readBytes / (double)runs,
            writeCalls / (double)runs,
            writeBytes / (double)runs,
            setLengthCalls / (double)runs);
    }


    /// <summary>
    /// Measures one named `SS8-16` design-performance scenario across repeated fresh-file samples.<br/>
    /// The fresh-file pattern matches the existing shape baselines while keeping widened-identity results separate from widened-key results.<br/>
    /// </summary>
    /// <param name="name">The scenario name.</param>
    /// <param name="runs">The number of samples to collect.</param>
    /// <param name="runner">The scenario runner that returns one measured sample.</param>
    /// <param name="directory">The directory where scenario files are written.</param>
    /// <param name="profile">The `SS8-16` shelf profile.</param>
    /// <returns>The aggregated scenario result.</returns>
    private static Scalar8Scalar8DesignPerfResult MeasureScalar8Scalar16DesignScenario(
        string name,
        int runs,
        Scalar8Scalar16DesignScenarioRunner runner,
        string directory,
        Scalar8Scalar16Profile profile)
    {
        long elapsedTicks = 0;
        long readCalls = 0;
        long readBytes = 0;
        long writeCalls = 0;
        long writeBytes = 0;
        long setLengthCalls = 0;
        double[] elapsedMillisecondsSamples = new double[runs];
        for (int i = 0; i < runs; i++)
        {
            string safeName = name.Replace(' ', '-');
            string path = Path.Combine(directory, $"{safeName}-{i:D4}.lbdx");
            File.Delete(path);
            Scalar8Scalar8DesignPerfSample sample = runner(path, profile);
            elapsedTicks += sample.ElapsedTicks;
            elapsedMillisecondsSamples[i] = sample.ElapsedTicks * 1000.0 / Stopwatch.Frequency;
            readCalls += sample.ReadCallCount;
            readBytes += sample.BytesRead;
            writeCalls += sample.WriteCallCount;
            writeBytes += sample.BytesWritten;
            setLengthCalls += sample.SetLengthCallCount;
        }

        double elapsedMilliseconds = elapsedTicks * 1000.0 / Stopwatch.Frequency;
        Scalar8Scalar8MetricStats elapsedStats = ComputeStats(elapsedMillisecondsSamples);
        return new Scalar8Scalar8DesignPerfResult(
            name,
            runs,
            elapsedMilliseconds / runs,
            elapsedStats.Median,
            elapsedStats.Min,
            elapsedStats.Max,
            elapsedStats.StandardDeviation,
            readCalls / (double)runs,
            readBytes / (double)runs,
            writeCalls / (double)runs,
            writeBytes / (double)runs,
            setLengthCalls / (double)runs);
    }


    /// <summary>
    /// Measures one named `SS16-16` design-performance scenario across repeated fresh-file samples.<br/>
    /// The fresh-file pattern matches the existing widened-shape baselines while keeping fully widened tuple results separate.<br/>
    /// </summary>
    /// <param name="name">The scenario name.</param>
    /// <param name="runs">The number of samples to collect.</param>
    /// <param name="runner">The scenario runner that returns one measured sample.</param>
    /// <param name="directory">The directory where scenario files are written.</param>
    /// <param name="profile">The `SS16-16` shelf profile.</param>
    /// <returns>The aggregated scenario result.</returns>
    private static Scalar8Scalar8DesignPerfResult MeasureScalar16Scalar16DesignScenario(
        string name,
        int runs,
        Scalar16Scalar16DesignScenarioRunner runner,
        string directory,
        Scalar16Scalar16Profile profile)
    {
        long elapsedTicks = 0;
        long readCalls = 0;
        long readBytes = 0;
        long writeCalls = 0;
        long writeBytes = 0;
        long setLengthCalls = 0;
        double[] elapsedMillisecondsSamples = new double[runs];
        for (int i = 0; i < runs; i++)
        {
            string safeName = name.Replace(' ', '-');
            string path = Path.Combine(directory, $"{safeName}-{i:D4}.lbdx");
            File.Delete(path);
            Scalar8Scalar8DesignPerfSample sample = runner(path, profile);
            elapsedTicks += sample.ElapsedTicks;
            elapsedMillisecondsSamples[i] = sample.ElapsedTicks * 1000.0 / Stopwatch.Frequency;
            readCalls += sample.ReadCallCount;
            readBytes += sample.BytesRead;
            writeCalls += sample.WriteCallCount;
            writeBytes += sample.BytesWritten;
            setLengthCalls += sample.SetLengthCallCount;
        }

        double elapsedMilliseconds = elapsedTicks * 1000.0 / Stopwatch.Frequency;
        Scalar8Scalar8MetricStats elapsedStats = ComputeStats(elapsedMillisecondsSamples);
        return new Scalar8Scalar8DesignPerfResult(
            name,
            runs,
            elapsedMilliseconds / runs,
            elapsedStats.Median,
            elapsedStats.Min,
            elapsedStats.Max,
            elapsedStats.StandardDeviation,
            readCalls / (double)runs,
            readBytes / (double)runs,
            writeCalls / (double)runs,
            writeBytes / (double)runs,
            setLengthCalls / (double)runs);
    }


    /// <summary>
    /// Creates a compact measured sample from elapsed time, read telemetry, and commit telemetry.<br/>
    /// This keeps the scenario methods focused on setup and mutation path selection while preserving syscall-shape evidence.<br/>
    /// </summary>
    /// <param name="stopwatch">The stopwatch that measured only the mutation call.</param>
    /// <param name="readTelemetry">The read telemetry captured during the mutation.</param>
    /// <param name="commitTelemetry">The commit telemetry captured during the mutation.</param>
    /// <returns>The compact design-performance sample.</returns>
    private static Scalar8Scalar8DesignPerfSample CreateDesignPerfSample(
        Stopwatch stopwatch,
        DataKernelReadTelemetry readTelemetry,
        DataKernelCommitTelemetry commitTelemetry)
    {
        return new Scalar8Scalar8DesignPerfSample(
            stopwatch.ElapsedTicks,
            readTelemetry.ReadCallCount,
            readTelemetry.BytesRead,
            commitTelemetry.WriteCallCount,
            commitTelemetry.BytesWritten,
            commitTelemetry.SetLengthCallCount);
    }


    /// <summary>
    /// Creates one computed `SS8-8` growth/slack row from measured file lengths, structure counts, split counts, and commit telemetry.<br/>
    /// The calculation separates file-level growth from immediate post-split shelf free capacity so deadspace and useful slack are not conflated.<br/>
    /// </summary>
    /// <param name="name">The scenario name.</param>
    /// <param name="setupLength">The file length after deterministic setup and before the measured split.</param>
    /// <param name="finalLength">The file length after the measured split.</param>
    /// <param name="routerCount">The number of live router pages after the split.</param>
    /// <param name="shelfCount">The number of live shelf extents after the split.</param>
    /// <param name="leftItemCount">The item count in the left replacement shelf.</param>
    /// <param name="rightItemCount">The item count in the right replacement shelf.</param>
    /// <param name="profile">The `SS8-8` shelf profile.</param>
    /// <param name="commit">The split commit telemetry.</param>
    /// <returns>The computed growth/slack result.</returns>
    private static Scalar8Scalar8GrowthSlackResult CreateScalar8Scalar8GrowthSlackResult(
        string name,
        long setupLength,
        long finalLength,
        int routerCount,
        int shelfCount,
        int leftItemCount,
        int rightItemCount,
        Scalar8Scalar8Profile profile,
        DataKernelCommitTelemetry commit)
    {
        int totalItems = leftItemCount + rightItemCount;
        int shelfCapacity = shelfCount * profile.MaxItemCount;
        int freeItemSlots = shelfCapacity - totalItems;
        long shelfFreeCapacityBytes = ((long)freeItemSlots * (Scalar8Scalar8Layout.ItemSize + Scalar8Scalar8Layout.SlotSize)) + ((long)shelfCount * profile.UnusedTailBytes);
        long metadataBytes = SuperblockLayout.Size + IndexDirectoryLayout.Size;
        long routerBytes = (long)routerCount * RouterLayout.Size;
        long shelfBytes = (long)shelfCount * profile.ShelfExtentSize;
        long liveStructureBytes = metadataBytes + routerBytes + shelfBytes;
        long routerReservedSlackBytes = (long)routerCount * (RouterLayout.Size - RouterLayout.HeaderSize - (RouterLayout.MaxOneByteRouteCount * RouterLayout.RouteSlotSize));
        return new Scalar8Scalar8GrowthSlackResult(
            name,
            setupLength,
            finalLength,
            finalLength - setupLength,
            liveStructureBytes,
            finalLength - liveStructureBytes,
            routerCount,
            shelfCount,
            totalItems,
            shelfCapacity,
            totalItems * 100.0 / shelfCapacity,
            freeItemSlots,
            shelfFreeCapacityBytes,
            routerReservedSlackBytes,
            commit.WriteCallCount,
            commit.BytesWritten,
            commit.SetLengthCallCount);
    }


    /// <summary>
    /// Creates deterministic full-shelf `SS16-8` vectors for a root-prefix-visible split scenario.<br/>
    /// Half the keys remain under root prefix `0x00`; the other half start at root prefix `0x80`.<br/>
    /// </summary>
    /// <param name="profile">The `SS16-8` shelf profile.</param>
    /// <param name="encodedKeyHighs">Receives the encoded high key halves.</param>
    /// <param name="encodedKeyLows">Receives the encoded low key halves.</param>
    /// <param name="encodedIdentities">Receives the encoded identities.</param>
    /// <param name="leftCount">Receives the expected left split count before inserting.</param>
    /// <param name="rightCount">Receives the expected right split count before inserting.</param>
    private static void CreateScalar16Scalar8RootPrefixSplitVectors(
        Scalar16Scalar8Profile profile,
        out ulong[] encodedKeyHighs,
        out ulong[] encodedKeyLows,
        out ulong[] encodedIdentities,
        out int leftCount,
        out int rightCount)
    {
        leftCount = profile.MaxItemCount / 2;
        rightCount = profile.MaxItemCount - leftCount;
        encodedKeyHighs = new ulong[profile.MaxItemCount];
        encodedKeyLows = new ulong[profile.MaxItemCount];
        encodedIdentities = new ulong[profile.MaxItemCount];
        int index = 0;
        for (int i = 0; i < leftCount; i++)
        {
            encodedKeyHighs[index] = 0;
            encodedKeyLows[index] = (ulong)i;
            encodedIdentities[index] = Scalar16Scalar8Layout.EncodeUnsignedScalar8((ulong)i);
            index++;
        }

        const ulong rightPrefixBaseHigh = 0x8000_0000_0000_0000UL;
        for (int i = 0; i < rightCount; i++)
        {
            encodedKeyHighs[index] = rightPrefixBaseHigh;
            encodedKeyLows[index] = (ulong)i;
            encodedIdentities[index] = Scalar16Scalar8Layout.EncodeUnsignedScalar8((ulong)i);
            index++;
        }
    }


    /// <summary>
    /// Creates deterministic full-shelf `SS16-8` vectors for a same-root-prefix transform split scenario.<br/>
    /// All keys share root prefix `0x00`, while the right half starts at second byte prefix `0x80`.<br/>
    /// </summary>
    /// <param name="profile">The `SS16-8` shelf profile.</param>
    /// <param name="encodedKeyHighs">Receives the encoded high key halves.</param>
    /// <param name="encodedKeyLows">Receives the encoded low key halves.</param>
    /// <param name="encodedIdentities">Receives the encoded identities.</param>
    /// <param name="leftCount">Receives the expected left split count before inserting.</param>
    /// <param name="rightCount">Receives the expected right split count before inserting.</param>
    private static void CreateScalar16Scalar8TransformSplitVectors(
        Scalar16Scalar8Profile profile,
        out ulong[] encodedKeyHighs,
        out ulong[] encodedKeyLows,
        out ulong[] encodedIdentities,
        out int leftCount,
        out int rightCount)
    {
        leftCount = profile.MaxItemCount / 2;
        rightCount = profile.MaxItemCount - leftCount;
        encodedKeyHighs = new ulong[profile.MaxItemCount];
        encodedKeyLows = new ulong[profile.MaxItemCount];
        encodedIdentities = new ulong[profile.MaxItemCount];
        int index = 0;
        for (int i = 0; i < leftCount; i++)
        {
            encodedKeyHighs[index] = 0;
            encodedKeyLows[index] = (ulong)i;
            encodedIdentities[index] = Scalar16Scalar8Layout.EncodeUnsignedScalar8((ulong)i);
            index++;
        }

        const ulong secondByteRightBaseHigh = 0x0080_0000_0000_0000UL;
        for (int i = 0; i < rightCount; i++)
        {
            encodedKeyHighs[index] = secondByteRightBaseHigh;
            encodedKeyLows[index] = (ulong)i;
            encodedIdentities[index] = Scalar16Scalar8Layout.EncodeUnsignedScalar8((ulong)i);
            index++;
        }
    }


    /// <summary>
    /// Creates deterministic full-shelf `FS32-8` vectors for a same-root-prefix transform split scenario.<br/>
    /// All keys share root prefix `0x00`, while the right half starts at second byte prefix `0x80`.<br/>
    /// </summary>
    /// <param name="profile">The `FS32-8` shelf profile.</param>
    /// <param name="key0s">Receives the first encoded key lanes.</param>
    /// <param name="key1s">Receives the second encoded key lanes.</param>
    /// <param name="key2s">Receives the third encoded key lanes.</param>
    /// <param name="key3s">Receives the fourth encoded key lanes.</param>
    /// <param name="encodedIdentities">Receives the encoded identities.</param>
    /// <param name="leftCount">Receives the expected left split count before inserting.</param>
    /// <param name="rightCount">Receives the expected right split count before inserting.</param>
    private static void CreateFixed32Scalar8TransformSplitVectors(
        Fixed32Scalar8Profile profile,
        out ulong[] key0s,
        out ulong[] key1s,
        out ulong[] key2s,
        out ulong[] key3s,
        out ulong[] encodedIdentities,
        out int leftCount,
        out int rightCount)
    {
        leftCount = profile.MaxItemCount / 2;
        rightCount = profile.MaxItemCount - leftCount;
        key0s = new ulong[profile.MaxItemCount];
        key1s = new ulong[profile.MaxItemCount];
        key2s = new ulong[profile.MaxItemCount];
        key3s = new ulong[profile.MaxItemCount];
        encodedIdentities = new ulong[profile.MaxItemCount];
        int index = 0;
        for (int i = 0; i < leftCount; i++)
        {
            key0s[index] = 0;
            key1s[index] = 0;
            key2s[index] = 0;
            key3s[index] = (ulong)i;
            encodedIdentities[index] = Fixed32Scalar8Layout.EncodeUnsignedScalar8((ulong)i);
            index++;
        }

        const ulong secondByteRightBase0 = 0x0080_0000_0000_0000UL;
        for (int i = 0; i < rightCount; i++)
        {
            key0s[index] = secondByteRightBase0;
            key1s[index] = 0;
            key2s[index] = 0;
            key3s[index] = (ulong)i;
            encodedIdentities[index] = Fixed32Scalar8Layout.EncodeUnsignedScalar8((ulong)i);
            index++;
        }
    }


    /// <summary>
    /// Creates deterministic full-shelf `SS8-16` vectors for a same-root-prefix transform split scenario.<br/>
    /// All keys share root prefix `0x00`, while the right half starts at second byte prefix `0x80`.<br/>
    /// Identity high/low arrays are explicit so widened-identity tuple ordering stays independently covered.<br/>
    /// </summary>
    /// <param name="profile">The `SS8-16` shelf profile.</param>
    /// <param name="encodedKeys">Receives the encoded keys.</param>
    /// <param name="encodedIdentityHighs">Receives the encoded high identity halves.</param>
    /// <param name="encodedIdentityLows">Receives the encoded low identity halves.</param>
    /// <param name="leftCount">Receives the expected left split count before inserting.</param>
    /// <param name="rightCount">Receives the expected right split count before inserting.</param>
    private static void CreateScalar8Scalar16TransformSplitVectors(
        Scalar8Scalar16Profile profile,
        out ulong[] encodedKeys,
        out ulong[] encodedIdentityHighs,
        out ulong[] encodedIdentityLows,
        out int leftCount,
        out int rightCount)
    {
        leftCount = profile.MaxItemCount / 2;
        rightCount = profile.MaxItemCount - leftCount;
        encodedKeys = new ulong[profile.MaxItemCount];
        encodedIdentityHighs = new ulong[profile.MaxItemCount];
        encodedIdentityLows = new ulong[profile.MaxItemCount];
        int index = 0;
        for (int i = 0; i < leftCount; i++)
        {
            encodedKeys[index] = Scalar8Scalar16Layout.EncodeUnsignedScalar8((ulong)i);
            encodedIdentityHighs[index] = 0;
            encodedIdentityLows[index] = (ulong)i;
            index++;
        }

        const ulong secondByteRightBase = 0x0080_0000_0000_0000UL;
        for (int i = 0; i < rightCount; i++)
        {
            encodedKeys[index] = secondByteRightBase + (ulong)i;
            encodedIdentityHighs[index] = 0;
            encodedIdentityLows[index] = (ulong)i;
            index++;
        }
    }


    /// <summary>
    /// Creates a deterministic full-shelf `SS8-16` key set that shares root and child prefixes and can split at the third prefix byte.<br/>
    /// This mirrors the `SS8-8` walked transform vector while preserving widened-identity high/low tuple layout explicitly.<br/>
    /// </summary>
    /// <param name="profile">The `SS8-16` shelf profile that determines full capacity.</param>
    /// <param name="encodedKeys">Receives the generated encoded scalar keys.</param>
    /// <param name="encodedIdentityHighs">Receives the generated encoded high identity halves.</param>
    /// <param name="encodedIdentityLows">Receives the generated encoded low identity halves.</param>
    /// <param name="leftCount">The number of low-third-prefix tuples.</param>
    /// <param name="rightCount">The number of high-third-prefix tuples.</param>
    private static void CreateScalar8Scalar16DeeperTransformSplitVectors(
        Scalar8Scalar16Profile profile,
        out ulong[] encodedKeys,
        out ulong[] encodedIdentityHighs,
        out ulong[] encodedIdentityLows,
        out int leftCount,
        out int rightCount)
    {
        leftCount = profile.MaxItemCount / 2;
        rightCount = profile.MaxItemCount - leftCount;
        encodedKeys = new ulong[profile.MaxItemCount];
        encodedIdentityHighs = new ulong[profile.MaxItemCount];
        encodedIdentityLows = new ulong[profile.MaxItemCount];
        int index = 0;
        for (int i = 0; i < leftCount; i++)
        {
            encodedKeys[index] = Scalar8Scalar16Layout.EncodeUnsignedScalar8((ulong)i);
            encodedIdentityHighs[index] = 0;
            encodedIdentityLows[index] = (ulong)i;
            index++;
        }

        const ulong thirdByteRightBase = 0x0000_8000_0000_0000UL;
        for (int i = 0; i < rightCount; i++)
        {
            encodedKeys[index] = thirdByteRightBase + (ulong)i;
            encodedIdentityHighs[index] = 0;
            encodedIdentityLows[index] = (ulong)i;
            index++;
        }
    }


    /// <summary>
    /// Creates a deterministic full-shelf `SS16-8` key set that shares root and child prefixes and can split at the third prefix byte.<br/>
    /// This mirrors the `SS8-8` walked transform vector while preserving widened-key high/low tuple layout explicitly.<br/>
    /// </summary>
    /// <param name="profile">The `SS16-8` shelf profile that determines full capacity.</param>
    /// <param name="encodedKeyHighs">Receives the generated encoded high key halves.</param>
    /// <param name="encodedKeyLows">Receives the generated encoded low key halves.</param>
    /// <param name="encodedIdentities">Receives the generated encoded identities.</param>
    /// <param name="leftCount">The number of low-third-prefix tuples.</param>
    /// <param name="rightCount">The number of high-third-prefix tuples.</param>
    private static void CreateScalar16Scalar8DeeperTransformSplitVectors(
        Scalar16Scalar8Profile profile,
        out ulong[] encodedKeyHighs,
        out ulong[] encodedKeyLows,
        out ulong[] encodedIdentities,
        out int leftCount,
        out int rightCount)
    {
        leftCount = profile.MaxItemCount / 2;
        rightCount = profile.MaxItemCount - leftCount;
        encodedKeyHighs = new ulong[profile.MaxItemCount];
        encodedKeyLows = new ulong[profile.MaxItemCount];
        encodedIdentities = new ulong[profile.MaxItemCount];
        int index = 0;
        for (int i = 0; i < leftCount; i++)
        {
            encodedKeyHighs[index] = 0;
            encodedKeyLows[index] = (ulong)i;
            encodedIdentities[index] = Scalar16Scalar8Layout.EncodeUnsignedScalar8((ulong)i);
            index++;
        }

        const ulong thirdByteRightBaseHigh = 0x0000_8000_0000_0000UL;
        for (int i = 0; i < rightCount; i++)
        {
            encodedKeyHighs[index] = thirdByteRightBaseHigh;
            encodedKeyLows[index] = (ulong)i;
            encodedIdentities[index] = Scalar16Scalar8Layout.EncodeUnsignedScalar8((ulong)i);
            index++;
        }
    }


    /// <summary>
    /// Creates a deterministic full-shelf `FS32-8` key set that shares root and child prefixes and can split at the third prefix byte.<br/>
    /// This mirrors the `SS16-8` walked transform vector while preserving the fixed-32-byte key lane layout explicitly.<br/>
    /// </summary>
    /// <param name="profile">The `FS32-8` shelf profile that determines full capacity.</param>
    /// <param name="key0s">Receives the generated first key lanes.</param>
    /// <param name="key1s">Receives the generated second key lanes.</param>
    /// <param name="key2s">Receives the generated third key lanes.</param>
    /// <param name="key3s">Receives the generated fourth key lanes.</param>
    /// <param name="encodedIdentities">Receives the generated encoded identities.</param>
    /// <param name="leftCount">The number of low-third-prefix tuples.</param>
    /// <param name="rightCount">The number of high-third-prefix tuples.</param>
    private static void CreateFixed32Scalar8DeeperTransformSplitVectors(
        Fixed32Scalar8Profile profile,
        out ulong[] key0s,
        out ulong[] key1s,
        out ulong[] key2s,
        out ulong[] key3s,
        out ulong[] encodedIdentities,
        out int leftCount,
        out int rightCount)
    {
        leftCount = profile.MaxItemCount / 2;
        rightCount = profile.MaxItemCount - leftCount;
        key0s = new ulong[profile.MaxItemCount];
        key1s = new ulong[profile.MaxItemCount];
        key2s = new ulong[profile.MaxItemCount];
        key3s = new ulong[profile.MaxItemCount];
        encodedIdentities = new ulong[profile.MaxItemCount];
        int index = 0;
        for (int i = 0; i < leftCount; i++)
        {
            key0s[index] = 0;
            key1s[index] = 0;
            key2s[index] = 0;
            key3s[index] = (ulong)i;
            encodedIdentities[index] = Fixed32Scalar8Layout.EncodeUnsignedScalar8((ulong)i);
            index++;
        }

        const ulong thirdByteRightBase0 = 0x0000_8000_0000_0000UL;
        for (int i = 0; i < rightCount; i++)
        {
            key0s[index] = thirdByteRightBase0;
            key1s[index] = 0;
            key2s[index] = 0;
            key3s[index] = (ulong)i;
            encodedIdentities[index] = Fixed32Scalar8Layout.EncodeUnsignedScalar8((ulong)i);
            index++;
        }
    }


    /// <summary>
    /// Creates a deterministic full-shelf `SS16-16` key set that shares root and child prefixes and can split at the third prefix byte.<br/>
    /// This mirrors the walked transform vector while preserving widened-key and widened-identity high/low tuple layout explicitly.<br/>
    /// </summary>
    /// <param name="profile">The `SS16-16` shelf profile that determines full capacity.</param>
    /// <param name="encodedKeyHighs">Receives the generated encoded high key halves.</param>
    /// <param name="encodedKeyLows">Receives the generated encoded low key halves.</param>
    /// <param name="encodedIdentityHighs">Receives the generated encoded high identity halves.</param>
    /// <param name="encodedIdentityLows">Receives the generated encoded low identity halves.</param>
    /// <param name="leftCount">The number of low-third-prefix tuples.</param>
    /// <param name="rightCount">The number of high-third-prefix tuples.</param>
    private static void CreateScalar16Scalar16DeeperTransformSplitVectors(
        Scalar16Scalar16Profile profile,
        out ulong[] encodedKeyHighs,
        out ulong[] encodedKeyLows,
        out ulong[] encodedIdentityHighs,
        out ulong[] encodedIdentityLows,
        out int leftCount,
        out int rightCount)
    {
        leftCount = profile.MaxItemCount / 2;
        rightCount = profile.MaxItemCount - leftCount;
        encodedKeyHighs = new ulong[profile.MaxItemCount];
        encodedKeyLows = new ulong[profile.MaxItemCount];
        encodedIdentityHighs = new ulong[profile.MaxItemCount];
        encodedIdentityLows = new ulong[profile.MaxItemCount];
        int index = 0;
        for (int i = 0; i < leftCount; i++)
        {
            encodedKeyHighs[index] = 0;
            encodedKeyLows[index] = (ulong)i;
            encodedIdentityHighs[index] = 0;
            encodedIdentityLows[index] = (ulong)i;
            index++;
        }

        const ulong thirdByteRightBaseHigh = 0x0000_8000_0000_0000UL;
        for (int i = 0; i < rightCount; i++)
        {
            encodedKeyHighs[index] = thirdByteRightBaseHigh;
            encodedKeyLows[index] = (ulong)i;
            encodedIdentityHighs[index] = 0;
            encodedIdentityLows[index] = (ulong)i;
            index++;
        }
    }


    /// <summary>
    /// Prints the fixed `SS8-8` design-performance table header.<br/>
    /// </summary>
    private static void PrintScalar8Scalar8DesignPerfHeader()
    {
        Console.WriteLine("scenario                      avg ms  median     sd ms     reads  bytes/read  writes  bytes/write  setlen");
        Console.WriteLine("---------------------------- -------- -------- -------- -------- ----------- ------- ------------ -------");
    }


    /// <summary>
    /// Prints one `SS8-8` design-performance result row.<br/>
    /// The row keeps elapsed time near syscall-shape telemetry so regressions are easy to classify.<br/>
    /// </summary>
    /// <param name="result">The scenario result to print.</param>
    private static void PrintScalar8Scalar8DesignPerf(Scalar8Scalar8DesignPerfResult result)
    {
        Console.WriteLine(
            "{0,-28} {1,8:F4} {2,8:F4} {3,8:F4} {4,8:F2} {5,11:F0} {6,7:F2} {7,12:F0} {8,7:F2}",
            result.Name,
            result.AverageMilliseconds,
            result.MedianMilliseconds,
            result.StandardDeviationMilliseconds,
            result.AverageReadCalls,
            result.AverageBytesRead,
            result.AverageWriteCalls,
            result.AverageBytesWritten,
            result.AverageSetLengthCalls);
    }


    /// <summary>
    /// Prints one `SS16-16` shelf-size sweep result row.<br/>
    /// The row keeps the profile capacity beside the mutation telemetry so shelf-size tradeoffs stay visible.<br/>
    /// </summary>
    /// <param name="profile">The `SS16-16` shelf profile used by the measured row.</param>
    /// <param name="result">The measured mutation result.</param>
    private static void PrintScalar16Scalar16SizeSweep(Scalar16Scalar16Profile profile, Scalar8Scalar8DesignPerfResult result)
    {
        double writeBytesPerMaxItem = result.AverageBytesWritten / profile.MaxItemCount;
        Console.WriteLine(
            "{0,7:N0} {1,8:N0} {2,-28} {3,8:F4} {4,8:F4} {5,8:F2} {6,11:F0} {7,7:F2} {8,12:F0} {9,14:F2} {10,7:F2}",
            profile.ShelfExtentSize,
            profile.MaxItemCount,
            result.Name,
            result.AverageMilliseconds,
            result.StandardDeviationMilliseconds,
            result.AverageReadCalls,
            result.AverageBytesRead,
            result.AverageWriteCalls,
            result.AverageBytesWritten,
            writeBytesPerMaxItem,
            result.AverageSetLengthCalls);
    }


    /// <summary>
    /// Prints the fixed `SS16-8` versus `SS8-8` comparison-performance table header.<br/>
    /// </summary>
    private static void PrintScalar16Scalar8ComparisonPerfHeader()
    {
        Console.WriteLine("scenario                      ss8 med ss16 med   delta%  ss8 avg ss16 avg ss8 reads ss16 reads ss8 writes ss16 writes ss8 bytes/w ss16 bytes/w");
        Console.WriteLine("---------------------------- -------- -------- -------- -------- -------- --------- ---------- ---------- ----------- ----------- ------------");
    }


    /// <summary>
    /// Prints one comparison row for matching `SS8-8` and `SS16-8` design-performance scenarios.<br/>
    /// The delta is based on elapsed mutation time and keeps syscall-shape columns visible for classifying the cause.<br/>
    /// </summary>
    /// <param name="ss88">The `SS8-8` baseline row.</param>
    /// <param name="ss168">The `SS16-8` widened-key row.</param>
    private static void PrintScalar16Scalar8ComparisonPerf(Scalar8Scalar8DesignPerfResult ss88, Scalar8Scalar8DesignPerfResult ss168)
    {
        double deltaPercent = ss88.MedianMilliseconds == 0 ? 0 : ((ss168.MedianMilliseconds - ss88.MedianMilliseconds) / ss88.MedianMilliseconds) * 100.0;
        Console.WriteLine(
            "{0,-28} {1,8:F4} {2,8:F4} {3,8:F2} {4,7:F4} {5,8:F4} {6,9:F2} {7,10:F2} {8,10:F2} {9,11:F2} {10,11:F0} {11,12:F0}",
            ss168.Name,
            ss88.MedianMilliseconds,
            ss168.MedianMilliseconds,
            deltaPercent,
            ss88.AverageMilliseconds,
            ss168.AverageMilliseconds,
            ss88.AverageReadCalls,
            ss168.AverageReadCalls,
            ss88.AverageWriteCalls,
            ss168.AverageWriteCalls,
            ss88.AverageBytesWritten,
            ss168.AverageBytesWritten);
    }


    /// <summary>
    /// Prints the fixed `FS32-8` versus scalar-key comparison-performance table header.<br/>
    /// The table keeps elapsed mutation time, syscall counts, and write bytes together so widened-key CPU drift is not mistaken for physical IO drift.<br/>
    /// </summary>
    private static void PrintFixed32Scalar8ComparisonPerfHeader()
    {
        Console.WriteLine("scenario                      ss8 med ss16 med fs32 med fs32/ss8% fs32/ss16% ss8 reads ss16 reads fs32 reads ss8 writes ss16 writes fs32 writes fs32 bytes/w");
        Console.WriteLine("---------------------------- -------- -------- -------- --------- ---------- --------- ---------- ---------- ---------- ----------- ---------- ------------");
    }


    /// <summary>
    /// Prints one three-way comparison row for matching `SS8-8`, `SS16-8`, and `FS32-8` design-performance scenarios.<br/>
    /// Delta columns are based on elapsed mutation time; read/write columns show whether any elapsed change came from a different physical mutation shape.<br/>
    /// </summary>
    /// <param name="ss88">The `SS8-8` baseline row.</param>
    /// <param name="ss168">The `SS16-8` widened scalar-key row.</param>
    /// <param name="fs328">The `FS32-8` fixed-32-key row.</param>
    private static void PrintFixed32Scalar8ComparisonPerf(
        Scalar8Scalar8DesignPerfResult ss88,
        Scalar8Scalar8DesignPerfResult ss168,
        Scalar8Scalar8DesignPerfResult fs328)
    {
        double fs32VsSs8Percent = ss88.MedianMilliseconds == 0 ? 0 : ((fs328.MedianMilliseconds - ss88.MedianMilliseconds) / ss88.MedianMilliseconds) * 100.0;
        double fs32VsSs16Percent = ss168.MedianMilliseconds == 0 ? 0 : ((fs328.MedianMilliseconds - ss168.MedianMilliseconds) / ss168.MedianMilliseconds) * 100.0;
        Console.WriteLine(
            "{0,-28} {1,8:F4} {2,8:F4} {3,8:F4} {4,9:F2} {5,10:F2} {6,9:F2} {7,10:F2} {8,10:F2} {9,10:F2} {10,11:F2} {11,10:F2} {12,12:F0}",
            fs328.Name,
            ss88.MedianMilliseconds,
            ss168.MedianMilliseconds,
            fs328.MedianMilliseconds,
            fs32VsSs8Percent,
            fs32VsSs16Percent,
            ss88.AverageReadCalls,
            ss168.AverageReadCalls,
            fs328.AverageReadCalls,
            ss88.AverageWriteCalls,
            ss168.AverageWriteCalls,
            fs328.AverageWriteCalls,
            fs328.AverageBytesWritten);
    }


    /// <summary>
    /// Prints the fixed `SS8-8` growth/slack table header.<br/>
    /// </summary>
    private static void PrintScalar8Scalar8GrowthSlackHeader()
    {
        Console.WriteLine("scenario                 setup    final   growth liveBytes extentSlack routers shelves items capacity  occ% freeSlots freeBytes routerSlack writes writeBytes setlen");
        Console.WriteLine("---------------------- -------- -------- -------- -------- -------- ------- ------- ----- -------- ----- -------- --------- ----------- ------ ---------- ------");
    }


    /// <summary>
    /// Prints one `SS8-8` growth/slack result row.<br/>
    /// The row keeps file growth, logical live structure bytes, and immediate post-split shelf slack side by side.<br/>
    /// </summary>
    /// <param name="result">The growth/slack row to print.</param>
    private static void PrintScalar8Scalar8GrowthSlack(Scalar8Scalar8GrowthSlackResult result)
    {
        Console.WriteLine(
            "{0,-22} {1,8} {2,8} {3,8} {4,8} {5,8} {6,7} {7,7} {8,5} {9,8} {10,5:F1} {11,8} {12,9} {13,11} {14,6} {15,10} {16,6}",
            result.Name,
            result.SetupLength,
            result.FinalLength,
            result.GrowthBytes,
            result.LiveStructureBytes,
            result.ExtentSlackBytes,
            result.RouterCount,
            result.ShelfCount,
            result.ItemCount,
            result.ShelfCapacity,
            result.ShelfOccupancyPercent,
            result.FreeItemSlots,
            result.ShelfFreeCapacityBytes,
            result.RouterReservedSlackBytes,
            result.WriteCalls,
            result.BytesWritten,
            result.SetLengthCalls);
    }


    private static void ValidateScalar8Scalar8InsertOrder(Scalar8Scalar8Profile profile, ulong[] keys, ulong[] identities)
    {
        if (keys.Length != identities.Length)
        {
            throw new ArgumentException("Key and identity vectors must have the same length.", nameof(identities));
        }

        byte[] bytes = new byte[profile.ShelfExtentSize];
        Scalar8Scalar8 shelf = new(bytes, profile);
        shelf.Initialize();
        for (int i = 0; i < keys.Length; i++)
        {
            Scalar8Scalar8InsertResult result = shelf.Insert(
                Scalar8Scalar8Layout.EncodeUnsignedScalar8(keys[i]),
                Scalar8Scalar8Layout.EncodeUnsignedScalar8(identities[i]),
                allowDuplicateKeys: true);
            if (result != Scalar8Scalar8InsertResult.Inserted)
            {
                throw new InvalidDataException($"Expected SS8-8 insert, got {result}.");
            }
        }

        ValidateScalar8Scalar8Sorted(shelf.AsReadOnly());
        int lower = shelf.AsReadOnly().LowerBoundKey(Scalar8Scalar8Layout.EncodeUnsignedScalar8(2));
        if (keys.Length >= 3 && lower >= shelf.ItemCount)
        {
            throw new InvalidDataException("Expected SS8-8 lower-bound key to find an inserted range.");
        }
    }


    /// <summary>
    /// Validates sorted insert behavior for a primitive `SS16-8` shelf using caller-provided encoded key halves.<br/>
    /// This intentionally exercises high-half and low-half ordering so the widened key path is not only a padded `SS8-8` scenario.<br/>
    /// </summary>
    /// <param name="profile">The `SS16-8` shelf profile.</param>
    /// <param name="keyHighs">The encoded key high halves to insert.</param>
    /// <param name="keyLows">The encoded key low halves to insert.</param>
    /// <param name="identities">The encoded identity values to insert.</param>
    /// <exception cref="InvalidDataException">Thrown when insert or sorted validation fails.</exception>
    private static void ValidateScalar16Scalar8InsertOrder(
        Scalar16Scalar8Profile profile,
        ulong[] keyHighs,
        ulong[] keyLows,
        ulong[] identities)
    {
        if (keyHighs.Length != keyLows.Length || keyHighs.Length != identities.Length)
        {
            throw new ArgumentException("Key and identity vectors must have the same length.", nameof(identities));
        }

        byte[] bytes = new byte[profile.ShelfExtentSize];
        Scalar16Scalar8 shelf = new(bytes, profile);
        shelf.Initialize();
        for (int i = 0; i < keyHighs.Length; i++)
        {
            Scalar16Scalar8InsertResult result = shelf.Insert(
                keyHighs[i],
                keyLows[i],
                Scalar16Scalar8Layout.EncodeUnsignedScalar8(identities[i]),
                allowDuplicateKeys: true);
            if (result != Scalar16Scalar8InsertResult.Inserted)
            {
                throw new InvalidDataException($"Expected SS16-8 insert, got {result}.");
            }
        }

        ValidateScalar16Scalar8Sorted(shelf.AsReadOnly());
        int lower = shelf.AsReadOnly().LowerBoundKey(0, 2);
        if (keyHighs.Length >= 3 && lower >= shelf.ItemCount)
        {
            throw new InvalidDataException("Expected SS16-8 lower-bound key to find an inserted range.");
        }
    }


    /// <summary>
    /// Validates random primitive inserts for `SS16-8` while preserving deterministic input order.<br/>
    /// The generated keys deliberately vary both 64-bit key halves so tuple comparison cannot accidentally ignore the high half.<br/>
    /// </summary>
    /// <param name="profile">The `SS16-8` shelf profile.</param>
    /// <exception cref="InvalidDataException">Thrown when random inserts fail or produce unsorted shelf state.</exception>
    private static void ValidateScalar16Scalar8RandomInsertOrder(Scalar16Scalar8Profile profile)
    {
        byte[] bytes = new byte[profile.ShelfExtentSize];
        Scalar16Scalar8 shelf = new(bytes, profile);
        shelf.Initialize();
        Random random = new(1618);
        for (int i = 0; i < 512; i++)
        {
            ulong keyHigh = (ulong)random.Next(0, 4);
            ulong keyLow = (ulong)random.Next(0, 128);
            ulong identity = (ulong)i;
            Scalar16Scalar8InsertResult result = shelf.Insert(
                keyHigh,
                keyLow,
                Scalar16Scalar8Layout.EncodeUnsignedScalar8(identity),
                allowDuplicateKeys: true);
            if (result != Scalar16Scalar8InsertResult.Inserted)
            {
                throw new InvalidDataException($"Expected random SS16-8 insert, got {result}.");
            }
        }

        ValidateScalar16Scalar8Sorted(shelf.AsReadOnly());
    }


    /// <summary>
    /// Validates duplicate tuple and unique-key conflict behavior for the primitive `SS16-8` shelf.<br/>
    /// The checks mirror the `SS8-8` semantics while using a two-half key to keep the new shape independently covered.<br/>
    /// </summary>
    /// <param name="profile">The `SS16-8` shelf profile.</param>
    /// <exception cref="InvalidDataException">Thrown when duplicate or key-conflict behavior drifts.</exception>
    private static void ValidateScalar16Scalar8DuplicateBehavior(Scalar16Scalar8Profile profile)
    {
        byte[] bytes = new byte[profile.ShelfExtentSize];
        Scalar16Scalar8 shelf = new(bytes, profile);
        shelf.Initialize();
        const ulong keyHigh = 7;
        const ulong keyLow = 42;
        ulong identity = Scalar16Scalar8Layout.EncodeUnsignedScalar8(100);
        if (shelf.Insert(keyHigh, keyLow, identity, allowDuplicateKeys: true) != Scalar16Scalar8InsertResult.Inserted)
        {
            throw new InvalidDataException("Expected first SS16-8 duplicate test insert to succeed.");
        }

        if (shelf.Insert(keyHigh, keyLow, identity, allowDuplicateKeys: true) != Scalar16Scalar8InsertResult.AlreadyPresent)
        {
            throw new InvalidDataException("Expected existing SS16-8 tuple to be already present.");
        }

        if (shelf.ItemCount != 1)
        {
            throw new InvalidDataException("Already-present SS16-8 tuple mutated item count.");
        }

        if (shelf.Insert(keyHigh, keyLow, Scalar16Scalar8Layout.EncodeUnsignedScalar8(99), allowDuplicateKeys: true) != Scalar16Scalar8InsertResult.Inserted)
        {
            throw new InvalidDataException("Expected non-unique SS16-8 duplicate key with distinct identity to insert.");
        }

        if (shelf.Insert(keyHigh, keyLow, Scalar16Scalar8Layout.EncodeUnsignedScalar8(101), allowDuplicateKeys: false) != Scalar16Scalar8InsertResult.KeyConflict)
        {
            throw new InvalidDataException("Expected unique SS16-8 duplicate key to report key conflict.");
        }

        ValidateScalar16Scalar8Sorted(shelf.AsReadOnly());
    }


    /// <summary>
    /// Validates primitive `SS16-8` point and range-copy behavior over a shelf with duplicate key groups.<br/>
    /// The range covers one high-half partition so lower-bound and scan termination both exercise the widened key comparison.<br/>
    /// </summary>
    /// <param name="profile">The `SS16-8` shelf profile.</param>
    /// <exception cref="InvalidDataException">Thrown when point lookup or range-copy behavior is incorrect.</exception>
    private static void ValidateScalar16Scalar8RangeCopy(Scalar16Scalar8Profile profile)
    {
        byte[] bytes = new byte[profile.ShelfExtentSize];
        Scalar16Scalar8 shelf = new(bytes, profile);
        shelf.Initialize();
        for (int i = 0; i < 128; i++)
        {
            ulong keyHigh = (ulong)(i / 64);
            ulong keyLow = (ulong)(i % 64);
            ulong identity = Scalar16Scalar8Layout.EncodeUnsignedScalar8((ulong)(i * 10));
            Scalar16Scalar8InsertResult result = shelf.Insert(keyHigh, keyLow, identity, allowDuplicateKeys: true);
            if (result != Scalar16Scalar8InsertResult.Inserted)
            {
                throw new InvalidDataException($"Expected SS16-8 range setup insert at {i}, got {result}.");
            }
        }

        Scalar16Scalar8ReadOnly readOnly = shelf.AsReadOnly();
        if (!readOnly.TryFindFirstIdentity(1, 3, out ulong foundIdentity) || foundIdentity != 670)
        {
            throw new InvalidDataException("SS16-8 point lookup did not return the expected identity.");
        }

        ulong[] identities = new ulong[8];
        int copied = readOnly.CopyIdentitiesInKeyRange(1, 4, 1, 11, identities);
        if (copied != 8 || identities[0] != 680 || identities[7] != 750)
        {
            throw new InvalidDataException("SS16-8 range copy did not return the expected identity window.");
        }
    }


    /// <summary>
    /// Populates a primitive `SS16-8` shelf with deterministic out-of-order 16-byte keys for DataKernel roundtrip validation.<br/>
    /// The high half varies independently from the low half so readback checks cover the widened key ordering, not only low-half scalar order.<br/>
    /// </summary>
    /// <param name="shelf">The mutable `SS16-8` shelf to populate.</param>
    /// <param name="count">The number of tuples to insert.</param>
    /// <exception cref="InvalidDataException">Thrown when a setup insert fails.</exception>
    private static void PopulateScalar16Scalar8Shelf(ref Scalar16Scalar8 shelf, int count)
    {
        for (int i = 0; i < count; i++)
        {
            ulong keyHigh = (ulong)((i * 7) % 4);
            ulong keyLow = (ulong)((i * 37) % 512);
            ulong identity = (ulong)i;
            Scalar16Scalar8InsertResult result = shelf.Insert(
                keyHigh,
                keyLow,
                Scalar16Scalar8Layout.EncodeUnsignedScalar8(identity),
                allowDuplicateKeys: true);
            if (result != Scalar16Scalar8InsertResult.Inserted)
            {
                throw new InvalidDataException($"Expected SS16-8 populate insert at {i}, got {result}.");
            }
        }
    }


    /// <summary>
    /// Validates a deterministic `SS16-8` readback window after DataKernel persistence.<br/>
    /// This checks point lookup and bounded range copy over values inserted by <see cref="PopulateScalar16Scalar8Shelf(ref Scalar16Scalar8, int)"/>.<br/>
    /// </summary>
    /// <param name="shelf">The read-only `SS16-8` shelf projection read from storage.</param>
    /// <exception cref="InvalidDataException">Thrown when point or range readback validation fails.</exception>
    private static void ValidateScalar16Scalar8ReadbackWindow(Scalar16Scalar8ReadOnly shelf)
    {
        if (!shelf.TryFindFirstIdentity(1, 111, out ulong foundIdentity) || foundIdentity != 3)
        {
            throw new InvalidDataException("SS16-8 readback point lookup did not return the expected identity.");
        }

        ulong[] identities = new ulong[8];
        int copied = shelf.CopyIdentitiesInKeyRange(1, 111, 1, 111, identities);
        if (copied != 1 || identities[0] != 3)
        {
            throw new InvalidDataException("SS16-8 readback range copy did not return the expected identity.");
        }
    }


    /// <summary>
    /// Populates a primitive `SS8-16` shelf with deterministic out-of-order 8-byte keys and 16-byte identities for DataKernel roundtrip validation.<br/>
    /// The identity high half varies independently from the key so readback checks cover widened identity ordering, not only padded `SS8-8` identity values.<br/>
    /// </summary>
    /// <param name="shelf">The mutable `SS8-16` shelf to populate.</param>
    /// <param name="count">The number of tuples to insert.</param>
    /// <exception cref="InvalidDataException">Thrown when a setup insert fails.</exception>
    private static void PopulateScalar8Scalar16Shelf(ref Scalar8Scalar16 shelf, int count)
    {
        for (int i = 0; i < count; i++)
        {
            ulong key = (ulong)((i * 37) % 512);
            ulong identityHigh = (ulong)(i / 128);
            ulong identityLow = (ulong)i;
            Scalar8Scalar16InsertResult result = shelf.Insert(
                Scalar8Scalar16Layout.EncodeUnsignedScalar8(key),
                identityHigh,
                identityLow,
                allowDuplicateKeys: true);
            if (result != Scalar8Scalar16InsertResult.Inserted)
            {
                throw new InvalidDataException($"Expected SS8-16 populate insert at {i}, got {result}.");
            }
        }
    }


    /// <summary>
    /// Validates a deterministic `SS8-16` readback window after DataKernel persistence.<br/>
    /// This checks point lookup and bounded range copy over both identity halves inserted by <see cref="PopulateScalar8Scalar16Shelf(ref Scalar8Scalar16, int)"/>.<br/>
    /// </summary>
    /// <param name="shelf">The read-only `SS8-16` shelf projection read from storage.</param>
    /// <exception cref="InvalidDataException">Thrown when point or range readback validation fails.</exception>
    private static void ValidateScalar8Scalar16ReadbackWindow(Scalar8Scalar16ReadOnly shelf)
    {
        ulong encodedKey = Scalar8Scalar16Layout.EncodeUnsignedScalar8(111);
        if (!shelf.TryFindFirstIdentity(encodedKey, out ulong foundHigh, out ulong foundLow) || foundHigh != 0 || foundLow != 3)
        {
            throw new InvalidDataException("SS8-16 readback point lookup did not return the expected identity.");
        }

        ulong[] identityHighs = new ulong[8];
        ulong[] identityLows = new ulong[8];
        int copied = shelf.CopyIdentitiesInKeyRange(encodedKey, encodedKey, identityHighs, identityLows);
        if (copied != 1 || identityHighs[0] != 0 || identityLows[0] != 3)
        {
            throw new InvalidDataException("SS8-16 readback range copy did not return the expected identity.");
        }
    }


    /// <summary>
    /// Populates a primitive `SS16-16` shelf with deterministic out-of-order 16-byte keys and 16-byte identities for DataKernel roundtrip validation.<br/>
    /// Both key halves and identity halves vary independently enough to prove persisted bytes are ordered and read back through the widened tuple contract.<br/>
    /// </summary>
    /// <param name="shelf">The mutable `SS16-16` shelf to populate.</param>
    /// <param name="count">The number of tuples to insert.</param>
    /// <exception cref="InvalidDataException">Thrown when a setup insert fails.</exception>
    private static void PopulateScalar16Scalar16Shelf(ref Scalar16Scalar16 shelf, int count)
    {
        for (int i = 0; i < count; i++)
        {
            ulong keyHigh = (ulong)((i * 7) % 4);
            ulong keyLow = (ulong)((i * 37) % 512);
            ulong identityHigh = (ulong)(i / 128);
            ulong identityLow = (ulong)i;
            Scalar16Scalar16InsertResult result = shelf.Insert(
                keyHigh,
                keyLow,
                identityHigh,
                identityLow,
                allowDuplicateKeys: true);
            if (result != Scalar16Scalar16InsertResult.Inserted)
            {
                throw new InvalidDataException($"Expected SS16-16 populate insert at {i}, got {result}.");
            }
        }
    }


    /// <summary>
    /// Validates a deterministic `SS16-16` readback window after DataKernel persistence.<br/>
    /// This checks point lookup and bounded range copy over both widened scalar fields inserted by <see cref="PopulateScalar16Scalar16Shelf(ref Scalar16Scalar16, int)"/>.<br/>
    /// </summary>
    /// <param name="shelf">The read-only `SS16-16` shelf projection read from storage.</param>
    /// <exception cref="InvalidDataException">Thrown when point or range readback validation fails.</exception>
    private static void ValidateScalar16Scalar16ReadbackWindow(Scalar16Scalar16ReadOnly shelf)
    {
        if (!shelf.TryFindFirstIdentity(1, 111, out ulong foundHigh, out ulong foundLow) || foundHigh != 0 || foundLow != 3)
        {
            throw new InvalidDataException("SS16-16 readback point lookup did not return the expected identity.");
        }

        ulong[] identityHighs = new ulong[8];
        ulong[] identityLows = new ulong[8];
        int copied = shelf.CopyIdentitiesInKeyRange(1, 111, 1, 111, identityHighs, identityLows);
        if (copied != 1 || identityHighs[0] != 0 || identityLows[0] != 3)
        {
            throw new InvalidDataException("SS16-16 readback range copy did not return the expected identity.");
        }
    }


    /// <summary>
    /// Validates the primitive `SS16-8` full-shelf boundary for the requested shelf profile.<br/>
    /// This proves the derived max item count, slot region, and item region line up for the widened fixed-width payload.<br/>
    /// </summary>
    /// <param name="profile">The `SS16-8` shelf profile.</param>
    /// <exception cref="InvalidDataException">Thrown when the shelf fills incorrectly or fails to report full.</exception>
    private static void ValidateScalar16Scalar8FullBoundary(Scalar16Scalar8Profile profile)
    {
        byte[] bytes = new byte[profile.ShelfExtentSize];
        Scalar16Scalar8 shelf = new(bytes, profile);
        shelf.Initialize();
        for (int i = 0; i < profile.MaxItemCount; i++)
        {
            Scalar16Scalar8Layout.EncodeUnsignedScalar16FromUInt64((ulong)i, out ulong keyHigh, out ulong keyLow);
            Scalar16Scalar8InsertResult result = shelf.Insert(
                keyHigh,
                keyLow,
                Scalar16Scalar8Layout.EncodeUnsignedScalar8((ulong)i),
                allowDuplicateKeys: true);
            if (result != Scalar16Scalar8InsertResult.Inserted)
            {
                throw new InvalidDataException($"Expected SS16-8 fill insert at {i}, got {result}.");
            }
        }

        if (shelf.Insert(ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, allowDuplicateKeys: true) != Scalar16Scalar8InsertResult.Full)
        {
            throw new InvalidDataException("Expected full SS16-8 shelf to report full.");
        }

        ValidateScalar16Scalar8Sorted(shelf.AsReadOnly());
    }


    /// <summary>
    /// Validates that a primitive `SS16-8` shelf is sorted by `(keyHigh, keyLow, identity)`.<br/>
    /// This is the main regression check that widened-key tuple ordering is independent from `SS8-8` ordering.<br/>
    /// </summary>
    /// <param name="shelf">The read-only `SS16-8` shelf projection.</param>
    /// <exception cref="InvalidDataException">Thrown when validation fails or tuple order is not monotonic.</exception>
    private static void ValidateScalar16Scalar8Sorted(Scalar16Scalar8ReadOnly shelf)
    {
        if (!shelf.IsValid)
        {
            throw new InvalidDataException("SS16-8 shelf failed validation.");
        }

        ulong previousKeyHigh = 0;
        ulong previousKeyLow = 0;
        ulong previousIdentity = 0;
        for (int i = 0; i < shelf.ItemCount; i++)
        {
            ulong keyHigh = shelf.ReadKeyHighAt(i);
            ulong keyLow = shelf.ReadKeyLowAt(i);
            ulong identity = shelf.ReadIdentityAt(i);
            bool outOfOrder =
                keyHigh < previousKeyHigh ||
                (keyHigh == previousKeyHigh && keyLow < previousKeyLow) ||
                (keyHigh == previousKeyHigh && keyLow == previousKeyLow && identity < previousIdentity);
            if (i > 0 && outOfOrder)
            {
                throw new InvalidDataException("SS16-8 shelf is not sorted by (keyHigh, keyLow, identity).");
            }

            previousKeyHigh = keyHigh;
            previousKeyLow = keyLow;
            previousIdentity = identity;
        }
    }


    /// <summary>
    /// Validates sorted insert behavior for a primitive `FS32-8` shelf using deterministic out-of-order fixed 32-byte keys.<br/>
    /// The generated keys vary all four 64-bit key parts so the primitive check can catch a comparison path that ignores a later key part.<br/>
    /// </summary>
    /// <param name="profile">The `FS32-8` shelf profile.</param>
    /// <exception cref="InvalidDataException">Thrown when insert or sorted validation fails.</exception>
    private static void ValidateFixed32Scalar8InsertOrder(Fixed32Scalar8Profile profile)
    {
        byte[] bytes = new byte[profile.ShelfExtentSize];
        Fixed32Scalar8 shelf = new(bytes, profile);
        shelf.Initialize();
        for (int i = 0; i < 512; i++)
        {
            CreateFixed32Scalar8Key(i * 37, out ulong key0, out ulong key1, out ulong key2, out ulong key3);
            Fixed32Scalar8InsertResult result = shelf.Insert(
                key0,
                key1,
                key2,
                key3,
                Fixed32Scalar8Layout.EncodeUnsignedScalar8((ulong)i),
                allowDuplicateKeys: true);
            if (result != Fixed32Scalar8InsertResult.Inserted)
            {
                throw new InvalidDataException($"Expected FS32-8 insert at {i}, got {result}.");
            }
        }

        ValidateFixed32Scalar8Sorted(shelf.AsReadOnly());
        CreateFixed32Scalar8Key(74, out ulong lower0, out ulong lower1, out ulong lower2, out ulong lower3);
        int lower = shelf.AsReadOnly().LowerBoundKey(lower0, lower1, lower2, lower3);
        if (lower >= shelf.ItemCount)
        {
            throw new InvalidDataException("Expected FS32-8 lower-bound key to find an inserted range.");
        }
    }


    /// <summary>
    /// Validates duplicate tuple and unique-key conflict behavior for the primitive `FS32-8` shelf.<br/>
    /// This mirrors existing fixed scalar semantics while using a 32-byte fixed key as the key equality boundary.<br/>
    /// </summary>
    /// <param name="profile">The `FS32-8` shelf profile.</param>
    /// <exception cref="InvalidDataException">Thrown when duplicate or key-conflict behavior drifts.</exception>
    private static void ValidateFixed32Scalar8DuplicateBehavior(Fixed32Scalar8Profile profile)
    {
        byte[] bytes = new byte[profile.ShelfExtentSize];
        Fixed32Scalar8 shelf = new(bytes, profile);
        shelf.Initialize();
        CreateFixed32Scalar8Key(42, out ulong key0, out ulong key1, out ulong key2, out ulong key3);
        ulong identity = Fixed32Scalar8Layout.EncodeUnsignedScalar8(100);
        if (shelf.Insert(key0, key1, key2, key3, identity, allowDuplicateKeys: true) != Fixed32Scalar8InsertResult.Inserted)
        {
            throw new InvalidDataException("Expected first FS32-8 duplicate test insert to succeed.");
        }

        if (shelf.Insert(key0, key1, key2, key3, identity, allowDuplicateKeys: true) != Fixed32Scalar8InsertResult.AlreadyPresent)
        {
            throw new InvalidDataException("Expected existing FS32-8 tuple to be already present.");
        }

        if (shelf.ItemCount != 1)
        {
            throw new InvalidDataException("Already-present FS32-8 tuple mutated item count.");
        }

        if (shelf.Insert(key0, key1, key2, key3, Fixed32Scalar8Layout.EncodeUnsignedScalar8(99), allowDuplicateKeys: true) != Fixed32Scalar8InsertResult.Inserted)
        {
            throw new InvalidDataException("Expected non-unique FS32-8 duplicate key with distinct identity to insert.");
        }

        if (shelf.Insert(key0, key1, key2, key3, Fixed32Scalar8Layout.EncodeUnsignedScalar8(101), allowDuplicateKeys: false) != Fixed32Scalar8InsertResult.KeyConflict)
        {
            throw new InvalidDataException("Expected unique FS32-8 duplicate key to report key conflict.");
        }

        ValidateFixed32Scalar8Sorted(shelf.AsReadOnly());
    }


    /// <summary>
    /// Validates primitive `FS32-8` point and range-copy behavior over a shelf with deterministic fixed 32-byte keys.<br/>
    /// The range is intentionally narrow and same-prefix-like so later routed validation can reuse the same key generator.<br/>
    /// </summary>
    /// <param name="profile">The `FS32-8` shelf profile.</param>
    /// <exception cref="InvalidDataException">Thrown when point lookup or range-copy behavior is incorrect.</exception>
    private static void ValidateFixed32Scalar8RangeCopy(Fixed32Scalar8Profile profile)
    {
        byte[] bytes = new byte[profile.ShelfExtentSize];
        Fixed32Scalar8 shelf = new(bytes, profile);
        shelf.Initialize();
        for (int i = 0; i < 128; i++)
        {
            CreateFixed32Scalar8Key(i, out ulong key0, out ulong key1, out ulong key2, out ulong key3);
            Fixed32Scalar8InsertResult result = shelf.Insert(
                key0,
                key1,
                key2,
                key3,
                Fixed32Scalar8Layout.EncodeUnsignedScalar8((ulong)(i * 10)),
                allowDuplicateKeys: true);
            if (result != Fixed32Scalar8InsertResult.Inserted)
            {
                throw new InvalidDataException($"Expected FS32-8 range setup insert at {i}, got {result}.");
            }
        }

        Fixed32Scalar8ReadOnly readOnly = shelf.AsReadOnly();
        CreateFixed32Scalar8Key(67, out ulong point0, out ulong point1, out ulong point2, out ulong point3);
        if (!readOnly.TryFindFirstIdentity(point0, point1, point2, point3, out ulong foundIdentity) || foundIdentity != 670)
        {
            throw new InvalidDataException("FS32-8 point lookup did not return the expected identity.");
        }

        CreateFixed32Scalar8Key(68, out ulong lower0, out ulong lower1, out ulong lower2, out ulong lower3);
        CreateFixed32Scalar8Key(75, out ulong upper0, out ulong upper1, out ulong upper2, out ulong upper3);
        ulong[] identities = new ulong[8];
        int copied = readOnly.CopyIdentitiesInKeyRange(lower0, lower1, lower2, lower3, upper0, upper1, upper2, upper3, identities);
        if (copied != 8 || identities[0] != 680 || identities[7] != 750)
        {
            throw new InvalidDataException("FS32-8 range copy did not return the expected identity window.");
        }
    }


    /// <summary>
    /// Populates a primitive `FS32-8` shelf with deterministic out-of-order fixed 32-byte keys for DataKernel roundtrip validation.<br/>
    /// The key generator varies all four fixed key parts so readback checks cover the widened key ordering, not only a padded scalar suffix.<br/>
    /// </summary>
    /// <param name="shelf">The mutable `FS32-8` shelf to populate.</param>
    /// <param name="count">The number of tuples to insert.</param>
    /// <exception cref="InvalidDataException">Thrown when a setup insert fails.</exception>
    private static void PopulateFixed32Scalar8Shelf(ref Fixed32Scalar8 shelf, int count)
    {
        for (int i = 0; i < count; i++)
        {
            CreateFixed32Scalar8Key((i * 37) % 1024, out ulong key0, out ulong key1, out ulong key2, out ulong key3);
            Fixed32Scalar8InsertResult result = shelf.Insert(
                key0,
                key1,
                key2,
                key3,
                Fixed32Scalar8Layout.EncodeUnsignedScalar8((ulong)i),
                allowDuplicateKeys: true);
            if (result != Fixed32Scalar8InsertResult.Inserted)
            {
                throw new InvalidDataException($"Expected FS32-8 populate insert at {i}, got {result}.");
            }
        }
    }


    /// <summary>
    /// Validates a deterministic `FS32-8` readback window after DataKernel persistence.<br/>
    /// This checks point lookup and bounded range copy over values inserted by <see cref="PopulateFixed32Scalar8Shelf(ref Fixed32Scalar8, int)"/>.<br/>
    /// </summary>
    /// <param name="shelf">The read-only `FS32-8` shelf projection read from storage.</param>
    /// <exception cref="InvalidDataException">Thrown when point or range readback validation fails.</exception>
    private static void ValidateFixed32Scalar8ReadbackWindow(Fixed32Scalar8ReadOnly shelf)
    {
        CreateFixed32Scalar8Key(111, out ulong point0, out ulong point1, out ulong point2, out ulong point3);
        if (!shelf.TryFindFirstIdentity(point0, point1, point2, point3, out ulong foundIdentity) || foundIdentity != 3)
        {
            throw new InvalidDataException("FS32-8 readback point lookup did not return the expected identity.");
        }

        ulong[] identities = new ulong[8];
        int copied = shelf.CopyIdentitiesInKeyRange(point0, point1, point2, point3, point0, point1, point2, point3, identities);
        if (copied != 1 || identities[0] != 3)
        {
            throw new InvalidDataException("FS32-8 readback range copy did not return the expected identity.");
        }
    }


    /// <summary>
    /// Validates the primitive `FS32-8` full-shelf boundary for the requested shelf profile.<br/>
    /// This proves the derived max item count, slot region, and 40-byte item region line up for the fixed 32-byte key payload.<br/>
    /// </summary>
    /// <param name="profile">The `FS32-8` shelf profile.</param>
    /// <exception cref="InvalidDataException">Thrown when the shelf fills incorrectly or fails to report full.</exception>
    private static void ValidateFixed32Scalar8FullBoundary(Fixed32Scalar8Profile profile)
    {
        byte[] bytes = new byte[profile.ShelfExtentSize];
        Fixed32Scalar8 shelf = new(bytes, profile);
        shelf.Initialize();
        for (int i = 0; i < profile.MaxItemCount; i++)
        {
            Fixed32Scalar8Layout.EncodeFixed32FromUInt64((ulong)i, out ulong key0, out ulong key1, out ulong key2, out ulong key3);
            Fixed32Scalar8InsertResult result = shelf.Insert(
                key0,
                key1,
                key2,
                key3,
                Fixed32Scalar8Layout.EncodeUnsignedScalar8((ulong)i),
                allowDuplicateKeys: true);
            if (result != Fixed32Scalar8InsertResult.Inserted)
            {
                throw new InvalidDataException($"Expected FS32-8 fill insert at {i}, got {result}.");
            }
        }

        if (shelf.Insert(ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, allowDuplicateKeys: true) != Fixed32Scalar8InsertResult.Full)
        {
            throw new InvalidDataException("Expected full FS32-8 shelf to report full.");
        }

        ValidateFixed32Scalar8Sorted(shelf.AsReadOnly());
    }


    /// <summary>
    /// Validates that a primitive `FS32-8` shelf is sorted by `(key0, key1, key2, key3, identity)`.<br/>
    /// This is the main regression check that fixed 32-byte key tuple ordering is independent from the `SS16-8` two-part key path.<br/>
    /// </summary>
    /// <param name="shelf">The read-only `FS32-8` shelf projection.</param>
    /// <exception cref="InvalidDataException">Thrown when validation fails or tuple order is not monotonic.</exception>
    private static void ValidateFixed32Scalar8Sorted(Fixed32Scalar8ReadOnly shelf)
    {
        if (!shelf.IsValid)
        {
            throw new InvalidDataException("FS32-8 shelf failed validation.");
        }

        ulong previous0 = 0;
        ulong previous1 = 0;
        ulong previous2 = 0;
        ulong previous3 = 0;
        ulong previousIdentity = 0;
        for (int i = 0; i < shelf.ItemCount; i++)
        {
            ulong key0 = shelf.ReadKeyPart0At(i);
            ulong key1 = shelf.ReadKeyPart1At(i);
            ulong key2 = shelf.ReadKeyPart2At(i);
            ulong key3 = shelf.ReadKeyPart3At(i);
            ulong identity = shelf.ReadIdentityAt(i);
            bool outOfOrder =
                key0 < previous0 ||
                (key0 == previous0 && key1 < previous1) ||
                (key0 == previous0 && key1 == previous1 && key2 < previous2) ||
                (key0 == previous0 && key1 == previous1 && key2 == previous2 && key3 < previous3) ||
                (key0 == previous0 && key1 == previous1 && key2 == previous2 && key3 == previous3 && identity < previousIdentity);
            if (i > 0 && outOfOrder)
            {
                throw new InvalidDataException("FS32-8 shelf is not sorted by (key0, key1, key2, key3, identity).");
            }

            previous0 = key0;
            previous1 = key1;
            previous2 = key2;
            previous3 = key3;
            previousIdentity = identity;
        }
    }


    /// <summary>
    /// Creates a deterministic fixed 32-byte key from a compact ordinal for primitive shelf validation.<br/>
    /// The first three parts model composite-key prefixes while the fourth part preserves a dense sortable suffix for simple range checks.<br/>
    /// </summary>
    /// <param name="ordinal">The compact ordinal used to derive the fixed key.</param>
    /// <param name="key0">Receives key part 0.</param>
    /// <param name="key1">Receives key part 1.</param>
    /// <param name="key2">Receives key part 2.</param>
    /// <param name="key3">Receives key part 3.</param>
    private static void CreateFixed32Scalar8Key(int ordinal, out ulong key0, out ulong key1, out ulong key2, out ulong key3)
    {
        key0 = (ulong)(ordinal / 4096);
        key1 = (ulong)((ordinal / 512) % 8);
        key2 = (ulong)((ordinal / 128) % 4);
        key3 = (ulong)ordinal;
    }


    /// <summary>
    /// Validates sorted insert behavior for a primitive `FS32-16` shelf using deterministic out-of-order fixed 32-byte keys and 16-byte identities.<br/>
    /// The generated tuples vary all key lanes plus both identity lanes so the primitive check catches comparison paths that ignore a later byte lane.<br/>
    /// </summary>
    /// <param name="profile">The `FS32-16` shelf profile.</param>
    /// <exception cref="InvalidDataException">Thrown when insert or sorted validation fails.</exception>
    private static void ValidateFixed32Scalar16InsertOrder(Fixed32Scalar16Profile profile)
    {
        byte[] bytes = new byte[profile.ShelfExtentSize];
        Fixed32Scalar16 shelf = new(bytes, profile);
        shelf.Initialize();
        for (int i = 0; i < 512; i++)
        {
            CreateFixed32Scalar8Key(i * 37, out ulong key0, out ulong key1, out ulong key2, out ulong key3);
            CreateFixed16Identity(i * 19, out ulong identityHigh, out ulong identityLow);
            Fixed32Scalar16InsertResult result = shelf.Insert(key0, key1, key2, key3, identityHigh, identityLow, allowDuplicateKeys: true);
            if (result != Fixed32Scalar16InsertResult.Inserted)
            {
                throw new InvalidDataException($"Expected FS32-16 insert at {i}, got {result}.");
            }
        }

        ValidateFixed32Scalar16Sorted(shelf.AsReadOnly());
        CreateFixed32Scalar8Key(74, out ulong lower0, out ulong lower1, out ulong lower2, out ulong lower3);
        int lower = shelf.AsReadOnly().LowerBoundKey(lower0, lower1, lower2, lower3);
        if (lower >= shelf.ItemCount)
        {
            throw new InvalidDataException("Expected FS32-16 lower-bound key to find an inserted range.");
        }
    }


    /// <summary>
    /// Validates duplicate tuple and unique-key conflict behavior for the primitive `FS32-16` shelf.<br/>
    /// The check keeps key equality separate from the two-lane fixed identity tie breaker.<br/>
    /// </summary>
    /// <param name="profile">The `FS32-16` shelf profile.</param>
    /// <exception cref="InvalidDataException">Thrown when duplicate or key-conflict behavior drifts.</exception>
    private static void ValidateFixed32Scalar16DuplicateBehavior(Fixed32Scalar16Profile profile)
    {
        byte[] bytes = new byte[profile.ShelfExtentSize];
        Fixed32Scalar16 shelf = new(bytes, profile);
        shelf.Initialize();
        CreateFixed32Scalar8Key(42, out ulong key0, out ulong key1, out ulong key2, out ulong key3);
        CreateFixed16Identity(100, out ulong identityHigh, out ulong identityLow);
        if (shelf.Insert(key0, key1, key2, key3, identityHigh, identityLow, allowDuplicateKeys: true) != Fixed32Scalar16InsertResult.Inserted)
        {
            throw new InvalidDataException("Expected first FS32-16 duplicate test insert to succeed.");
        }

        if (shelf.Insert(key0, key1, key2, key3, identityHigh, identityLow, allowDuplicateKeys: true) != Fixed32Scalar16InsertResult.AlreadyPresent)
        {
            throw new InvalidDataException("Expected existing FS32-16 tuple to be already present.");
        }

        CreateFixed16Identity(99, out ulong lowerIdentityHigh, out ulong lowerIdentityLow);
        if (shelf.Insert(key0, key1, key2, key3, lowerIdentityHigh, lowerIdentityLow, allowDuplicateKeys: true) != Fixed32Scalar16InsertResult.Inserted)
        {
            throw new InvalidDataException("Expected non-unique FS32-16 duplicate key with distinct identity to insert.");
        }

        CreateFixed16Identity(101, out ulong conflictIdentityHigh, out ulong conflictIdentityLow);
        if (shelf.Insert(key0, key1, key2, key3, conflictIdentityHigh, conflictIdentityLow, allowDuplicateKeys: false) != Fixed32Scalar16InsertResult.KeyConflict)
        {
            throw new InvalidDataException("Expected unique FS32-16 duplicate key to report key conflict.");
        }

        ValidateFixed32Scalar16Sorted(shelf.AsReadOnly());
    }


    /// <summary>
    /// Validates primitive `FS32-16` point and range-copy behavior over deterministic fixed 32-byte keys.<br/>
    /// The output spans keep the 16-byte identity byte lanes separate while preserving the byte-native shelf contract.<br/>
    /// </summary>
    /// <param name="profile">The `FS32-16` shelf profile.</param>
    /// <exception cref="InvalidDataException">Thrown when point lookup or range-copy behavior is incorrect.</exception>
    private static void ValidateFixed32Scalar16RangeCopy(Fixed32Scalar16Profile profile)
    {
        byte[] bytes = new byte[profile.ShelfExtentSize];
        Fixed32Scalar16 shelf = new(bytes, profile);
        shelf.Initialize();
        for (int i = 0; i < 128; i++)
        {
            CreateFixed32Scalar8Key(i, out ulong key0, out ulong key1, out ulong key2, out ulong key3);
            CreateFixed16Identity(i * 10, out ulong identityHigh, out ulong identityLow);
            Fixed32Scalar16InsertResult result = shelf.Insert(key0, key1, key2, key3, identityHigh, identityLow, allowDuplicateKeys: true);
            if (result != Fixed32Scalar16InsertResult.Inserted)
            {
                throw new InvalidDataException($"Expected FS32-16 range setup insert at {i}, got {result}.");
            }
        }

        Fixed32Scalar16ReadOnly readOnly = shelf.AsReadOnly();
        CreateFixed32Scalar8Key(67, out ulong point0, out ulong point1, out ulong point2, out ulong point3);
        if (!readOnly.TryFindFirstIdentity(point0, point1, point2, point3, out ulong foundHigh, out ulong foundLow) || foundHigh != 0 || foundLow != 670)
        {
            throw new InvalidDataException("FS32-16 point lookup did not return the expected identity.");
        }

        CreateFixed32Scalar8Key(68, out ulong lower0, out ulong lower1, out ulong lower2, out ulong lower3);
        CreateFixed32Scalar8Key(75, out ulong upper0, out ulong upper1, out ulong upper2, out ulong upper3);
        ulong[] identityHighs = new ulong[8];
        ulong[] identityLows = new ulong[8];
        int copied = readOnly.CopyIdentitiesInKeyRange(lower0, lower1, lower2, lower3, upper0, upper1, upper2, upper3, identityHighs, identityLows);
        if (copied != 8 || identityHighs[0] != 0 || identityLows[0] != 680 || identityHighs[7] != 0 || identityLows[7] != 750)
        {
            throw new InvalidDataException("FS32-16 range copy did not return the expected identity window.");
        }
    }


    /// <summary>
    /// Validates the primitive `FS32-16` full-shelf boundary for the requested shelf profile.<br/>
    /// This proves the derived max item count, slot region, and 48-byte item region line up for the fixed 32-byte key plus fixed 16-byte identity payload.<br/>
    /// </summary>
    /// <param name="profile">The `FS32-16` shelf profile.</param>
    /// <exception cref="InvalidDataException">Thrown when the shelf fills incorrectly or fails to report full.</exception>
    private static void ValidateFixed32Scalar16FullBoundary(Fixed32Scalar16Profile profile)
    {
        byte[] bytes = new byte[profile.ShelfExtentSize];
        Fixed32Scalar16 shelf = new(bytes, profile);
        shelf.Initialize();
        for (int i = 0; i < profile.MaxItemCount; i++)
        {
            Fixed32Scalar16Layout.EncodeFixed32FromUInt64((ulong)i, out ulong key0, out ulong key1, out ulong key2, out ulong key3);
            Fixed32Scalar16Layout.EncodeFixed16FromUInt64((ulong)i, out ulong identityHigh, out ulong identityLow);
            Fixed32Scalar16InsertResult result = shelf.Insert(key0, key1, key2, key3, identityHigh, identityLow, allowDuplicateKeys: true);
            if (result != Fixed32Scalar16InsertResult.Inserted)
            {
                throw new InvalidDataException($"Expected FS32-16 fill insert at {i}, got {result}.");
            }
        }

        if (shelf.Insert(ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, allowDuplicateKeys: true) != Fixed32Scalar16InsertResult.Full)
        {
            throw new InvalidDataException("Expected full FS32-16 shelf to report full.");
        }

        ValidateFixed32Scalar16Sorted(shelf.AsReadOnly());
    }


    /// <summary>
    /// Validates that a primitive `FS32-16` shelf is sorted by `(key0, key1, key2, key3, identityHigh, identityLow)`.<br/>
    /// The identity lanes are byte-order-preserving projections over the persisted fixed 16-byte identity payload, not public scalar semantics.<br/>
    /// </summary>
    /// <param name="shelf">The read-only `FS32-16` shelf projection.</param>
    /// <exception cref="InvalidDataException">Thrown when validation fails or tuple order is not monotonic.</exception>
    private static void ValidateFixed32Scalar16Sorted(Fixed32Scalar16ReadOnly shelf)
    {
        if (!shelf.IsValid)
        {
            throw new InvalidDataException("FS32-16 shelf failed validation.");
        }

        ulong previous0 = 0;
        ulong previous1 = 0;
        ulong previous2 = 0;
        ulong previous3 = 0;
        ulong previousIdentityHigh = 0;
        ulong previousIdentityLow = 0;
        for (int i = 0; i < shelf.ItemCount; i++)
        {
            ulong key0 = shelf.ReadKeyPart0At(i);
            ulong key1 = shelf.ReadKeyPart1At(i);
            ulong key2 = shelf.ReadKeyPart2At(i);
            ulong key3 = shelf.ReadKeyPart3At(i);
            ulong identityHigh = shelf.ReadIdentityHighAt(i);
            ulong identityLow = shelf.ReadIdentityLowAt(i);
            bool outOfOrder =
                key0 < previous0 ||
                (key0 == previous0 && key1 < previous1) ||
                (key0 == previous0 && key1 == previous1 && key2 < previous2) ||
                (key0 == previous0 && key1 == previous1 && key2 == previous2 && key3 < previous3) ||
                (key0 == previous0 && key1 == previous1 && key2 == previous2 && key3 == previous3 && identityHigh < previousIdentityHigh) ||
                (key0 == previous0 && key1 == previous1 && key2 == previous2 && key3 == previous3 && identityHigh == previousIdentityHigh && identityLow < previousIdentityLow);
            if (i > 0 && outOfOrder)
            {
                throw new InvalidDataException("FS32-16 shelf is not sorted by (key0, key1, key2, key3, identityHigh, identityLow).");
            }

            previous0 = key0;
            previous1 = key1;
            previous2 = key2;
            previous3 = key3;
            previousIdentityHigh = identityHigh;
            previousIdentityLow = identityLow;
        }
    }


    /// <summary>
    /// Validates sorted insert behavior for a primitive `SS8-16` shelf using caller-provided key and identity halves.<br/>
    /// This intentionally exercises high-half and low-half identity ordering so widened identity tie-breaking is not only a padded `SS8-8` scenario.<br/>
    /// </summary>
    /// <param name="profile">The `SS8-16` shelf profile.</param>
    /// <param name="keys">The encoded keys to insert.</param>
    /// <param name="identityHighs">The encoded high identity halves to insert.</param>
    /// <param name="identityLows">The encoded low identity halves to insert.</param>
    /// <exception cref="InvalidDataException">Thrown when insert or sorted validation fails.</exception>
    private static void ValidateScalar8Scalar16InsertOrder(
        Scalar8Scalar16Profile profile,
        ulong[] keys,
        ulong[] identityHighs,
        ulong[] identityLows)
    {
        if (keys.Length != identityHighs.Length || keys.Length != identityLows.Length)
        {
            throw new ArgumentException("Key and identity vectors must have the same length.", nameof(identityLows));
        }

        byte[] bytes = new byte[profile.ShelfExtentSize];
        Scalar8Scalar16 shelf = new(bytes, profile);
        shelf.Initialize();
        for (int i = 0; i < keys.Length; i++)
        {
            Scalar8Scalar16InsertResult result = shelf.Insert(
                Scalar8Scalar16Layout.EncodeUnsignedScalar8(keys[i]),
                identityHighs[i],
                identityLows[i],
                allowDuplicateKeys: true);
            if (result != Scalar8Scalar16InsertResult.Inserted)
            {
                throw new InvalidDataException($"Expected SS8-16 insert, got {result}.");
            }
        }

        ValidateScalar8Scalar16Sorted(shelf.AsReadOnly());
        int lower = shelf.AsReadOnly().LowerBoundKey(Scalar8Scalar16Layout.EncodeUnsignedScalar8(2));
        if (keys.Length >= 3 && lower >= shelf.ItemCount)
        {
            throw new InvalidDataException("Expected SS8-16 lower-bound key to find an inserted range.");
        }
    }


    /// <summary>
    /// Validates duplicate tuple and unique-key conflict behavior for the primitive `SS8-16` shelf.<br/>
    /// The checks mirror `SS8-8` semantics while using a two-half identity to keep the new shape independently covered.<br/>
    /// </summary>
    /// <param name="profile">The `SS8-16` shelf profile.</param>
    /// <exception cref="InvalidDataException">Thrown when duplicate or key-conflict behavior drifts.</exception>
    private static void ValidateScalar8Scalar16DuplicateBehavior(Scalar8Scalar16Profile profile)
    {
        byte[] bytes = new byte[profile.ShelfExtentSize];
        Scalar8Scalar16 shelf = new(bytes, profile);
        shelf.Initialize();
        const ulong key = 42;
        const ulong identityHigh = 7;
        const ulong identityLow = 100;
        if (shelf.Insert(key, identityHigh, identityLow, allowDuplicateKeys: true) != Scalar8Scalar16InsertResult.Inserted)
        {
            throw new InvalidDataException("Expected first SS8-16 duplicate test insert to succeed.");
        }

        if (shelf.Insert(key, identityHigh, identityLow, allowDuplicateKeys: true) != Scalar8Scalar16InsertResult.AlreadyPresent)
        {
            throw new InvalidDataException("Expected existing SS8-16 tuple to be already present.");
        }

        if (shelf.ItemCount != 1)
        {
            throw new InvalidDataException("Already-present SS8-16 tuple mutated item count.");
        }

        if (shelf.Insert(key, identityHigh, 99, allowDuplicateKeys: true) != Scalar8Scalar16InsertResult.Inserted)
        {
            throw new InvalidDataException("Expected non-unique SS8-16 duplicate key with distinct identity to insert.");
        }

        if (shelf.Insert(key, identityHigh, 101, allowDuplicateKeys: false) != Scalar8Scalar16InsertResult.KeyConflict)
        {
            throw new InvalidDataException("Expected unique SS8-16 duplicate key to report key conflict.");
        }

        ValidateScalar8Scalar16Sorted(shelf.AsReadOnly());
    }


    /// <summary>
    /// Validates primitive `SS8-16` point and range-copy behavior over a shelf with duplicate key groups.<br/>
    /// The range returns both identity halves so copy semantics are proven for the widened identity field.<br/>
    /// </summary>
    /// <param name="profile">The `SS8-16` shelf profile.</param>
    /// <exception cref="InvalidDataException">Thrown when point lookup or range-copy behavior is incorrect.</exception>
    private static void ValidateScalar8Scalar16RangeCopy(Scalar8Scalar16Profile profile)
    {
        byte[] bytes = new byte[profile.ShelfExtentSize];
        Scalar8Scalar16 shelf = new(bytes, profile);
        shelf.Initialize();
        for (int i = 0; i < 128; i++)
        {
            ulong key = (ulong)(i / 4);
            ulong identityHigh = (ulong)(i / 64);
            ulong identityLow = (ulong)(i * 10);
            Scalar8Scalar16InsertResult result = shelf.Insert(key, identityHigh, identityLow, allowDuplicateKeys: true);
            if (result != Scalar8Scalar16InsertResult.Inserted)
            {
                throw new InvalidDataException($"Expected SS8-16 range setup insert at {i}, got {result}.");
            }
        }

        Scalar8Scalar16ReadOnly readOnly = shelf.AsReadOnly();
        if (!readOnly.TryFindFirstIdentity(16, out ulong foundHigh, out ulong foundLow) || foundHigh != 1 || foundLow != 640)
        {
            throw new InvalidDataException("SS8-16 point lookup did not return the expected identity.");
        }

        ulong[] identityHighs = new ulong[8];
        ulong[] identityLows = new ulong[8];
        int copied = readOnly.CopyIdentitiesInKeyRange(17, 18, identityHighs, identityLows);
        if (copied != 8 || identityHighs[0] != 1 || identityLows[0] != 680 || identityHighs[7] != 1 || identityLows[7] != 750)
        {
            throw new InvalidDataException("SS8-16 range copy did not return the expected identity window.");
        }
    }


    /// <summary>
    /// Validates the primitive `SS8-16` full-shelf boundary for the requested shelf profile.<br/>
    /// This proves the derived max item count, slot region, and item region line up for the widened fixed-width identity payload.<br/>
    /// </summary>
    /// <param name="profile">The `SS8-16` shelf profile.</param>
    /// <exception cref="InvalidDataException">Thrown when the shelf fills incorrectly or fails to report full.</exception>
    private static void ValidateScalar8Scalar16FullBoundary(Scalar8Scalar16Profile profile)
    {
        byte[] bytes = new byte[profile.ShelfExtentSize];
        Scalar8Scalar16 shelf = new(bytes, profile);
        shelf.Initialize();
        for (int i = 0; i < profile.MaxItemCount; i++)
        {
            Scalar8Scalar16InsertResult result = shelf.Insert(
                Scalar8Scalar16Layout.EncodeUnsignedScalar8((ulong)i),
                0,
                (ulong)i,
                allowDuplicateKeys: true);
            if (result != Scalar8Scalar16InsertResult.Inserted)
            {
                throw new InvalidDataException($"Expected SS8-16 fill insert at {i}, got {result}.");
            }
        }

        if (shelf.Insert(ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, allowDuplicateKeys: true) != Scalar8Scalar16InsertResult.Full)
        {
            throw new InvalidDataException("Expected full SS8-16 shelf to report full.");
        }

        ValidateScalar8Scalar16Sorted(shelf.AsReadOnly());
    }


    /// <summary>
    /// Validates that a primitive `SS8-16` shelf is sorted by `(key, identityHigh, identityLow)`.<br/>
    /// This is the main regression check that widened-identity tuple ordering is independent from `SS8-8` ordering.<br/>
    /// </summary>
    /// <param name="shelf">The read-only `SS8-16` shelf projection.</param>
    /// <exception cref="InvalidDataException">Thrown when validation fails or tuple order is not monotonic.</exception>
    private static void ValidateScalar8Scalar16Sorted(Scalar8Scalar16ReadOnly shelf)
    {
        if (!shelf.IsValid)
        {
            throw new InvalidDataException("SS8-16 shelf failed validation.");
        }

        ulong previousKey = 0;
        ulong previousIdentityHigh = 0;
        ulong previousIdentityLow = 0;
        for (int i = 0; i < shelf.ItemCount; i++)
        {
            ulong key = shelf.ReadKeyAt(i);
            ulong identityHigh = shelf.ReadIdentityHighAt(i);
            ulong identityLow = shelf.ReadIdentityLowAt(i);
            bool outOfOrder =
                key < previousKey ||
                (key == previousKey && identityHigh < previousIdentityHigh) ||
                (key == previousKey && identityHigh == previousIdentityHigh && identityLow < previousIdentityLow);
            if (i > 0 && outOfOrder)
            {
                throw new InvalidDataException("SS8-16 shelf is not sorted by (key, identityHigh, identityLow).");
            }

            previousKey = key;
            previousIdentityHigh = identityHigh;
            previousIdentityLow = identityLow;
        }
    }


    /// <summary>
    /// Validates sorted insert behavior for a primitive `SS16-16` shelf using caller-provided key and identity halves.<br/>
    /// This intentionally exercises both halves of both scalar fields so the new shape is not only a padded `SS8-8` scenario.<br/>
    /// </summary>
    /// <param name="profile">The `SS16-16` shelf profile.</param>
    /// <param name="keyHighs">The encoded key high halves to insert.</param>
    /// <param name="keyLows">The encoded key low halves to insert.</param>
    /// <param name="identityHighs">The encoded identity high halves to insert.</param>
    /// <param name="identityLows">The encoded identity low halves to insert.</param>
    /// <exception cref="InvalidDataException">Thrown when insert or sorted validation fails.</exception>
    private static void ValidateScalar16Scalar16InsertOrder(
        Scalar16Scalar16Profile profile,
        ulong[] keyHighs,
        ulong[] keyLows,
        ulong[] identityHighs,
        ulong[] identityLows)
    {
        if (keyHighs.Length != keyLows.Length || keyHighs.Length != identityHighs.Length || keyHighs.Length != identityLows.Length)
        {
            throw new ArgumentException("Key and identity vectors must have the same length.", nameof(identityLows));
        }

        byte[] bytes = new byte[profile.ShelfExtentSize];
        Scalar16Scalar16 shelf = new(bytes, profile);
        shelf.Initialize();
        for (int i = 0; i < keyHighs.Length; i++)
        {
            Scalar16Scalar16InsertResult result = shelf.Insert(
                keyHighs[i],
                keyLows[i],
                identityHighs[i],
                identityLows[i],
                allowDuplicateKeys: true);
            if (result != Scalar16Scalar16InsertResult.Inserted)
            {
                throw new InvalidDataException($"Expected SS16-16 insert, got {result}.");
            }
        }

        ValidateScalar16Scalar16Sorted(shelf.AsReadOnly());
        int lower = shelf.AsReadOnly().LowerBoundKey(0, 2);
        if (keyHighs.Length >= 3 && lower >= shelf.ItemCount)
        {
            throw new InvalidDataException("Expected SS16-16 lower-bound key to find an inserted range.");
        }
    }


    /// <summary>
    /// Validates random primitive inserts for `SS16-16` while preserving deterministic input order.<br/>
    /// The generated keys and identities deliberately vary both 64-bit halves so tuple comparison cannot accidentally ignore a widened field.<br/>
    /// </summary>
    /// <param name="profile">The `SS16-16` shelf profile.</param>
    /// <exception cref="InvalidDataException">Thrown when random inserts fail or produce unsorted shelf state.</exception>
    private static void ValidateScalar16Scalar16RandomInsertOrder(Scalar16Scalar16Profile profile)
    {
        byte[] bytes = new byte[profile.ShelfExtentSize];
        Scalar16Scalar16 shelf = new(bytes, profile);
        shelf.Initialize();
        Random random = new(1616);
        for (int i = 0; i < 512; i++)
        {
            ulong keyHigh = (ulong)random.Next(0, 4);
            ulong keyLow = (ulong)random.Next(0, 128);
            ulong identityHigh = (ulong)random.Next(0, 3);
            ulong identityLow = (ulong)i;
            Scalar16Scalar16InsertResult result = shelf.Insert(
                keyHigh,
                keyLow,
                identityHigh,
                identityLow,
                allowDuplicateKeys: true);
            if (result != Scalar16Scalar16InsertResult.Inserted)
            {
                throw new InvalidDataException($"Expected random SS16-16 insert, got {result}.");
            }
        }

        ValidateScalar16Scalar16Sorted(shelf.AsReadOnly());
    }


    /// <summary>
    /// Validates duplicate tuple and unique-key conflict behavior for the primitive `SS16-16` shelf.<br/>
    /// The checks mirror the earlier scalar shelf semantics while using two-half keys and identities to keep the new shape independently covered.<br/>
    /// </summary>
    /// <param name="profile">The `SS16-16` shelf profile.</param>
    /// <exception cref="InvalidDataException">Thrown when duplicate or key-conflict behavior drifts.</exception>
    private static void ValidateScalar16Scalar16DuplicateBehavior(Scalar16Scalar16Profile profile)
    {
        byte[] bytes = new byte[profile.ShelfExtentSize];
        Scalar16Scalar16 shelf = new(bytes, profile);
        shelf.Initialize();
        const ulong keyHigh = 7;
        const ulong keyLow = 42;
        const ulong identityHigh = 3;
        const ulong identityLow = 100;
        if (shelf.Insert(keyHigh, keyLow, identityHigh, identityLow, allowDuplicateKeys: true) != Scalar16Scalar16InsertResult.Inserted)
        {
            throw new InvalidDataException("Expected first SS16-16 duplicate test insert to succeed.");
        }

        if (shelf.Insert(keyHigh, keyLow, identityHigh, identityLow, allowDuplicateKeys: true) != Scalar16Scalar16InsertResult.AlreadyPresent)
        {
            throw new InvalidDataException("Expected existing SS16-16 tuple to be already present.");
        }

        if (shelf.ItemCount != 1)
        {
            throw new InvalidDataException("Already-present SS16-16 tuple mutated item count.");
        }

        if (shelf.Insert(keyHigh, keyLow, identityHigh, 99, allowDuplicateKeys: true) != Scalar16Scalar16InsertResult.Inserted)
        {
            throw new InvalidDataException("Expected non-unique SS16-16 duplicate key with distinct identity to insert.");
        }

        if (shelf.Insert(keyHigh, keyLow, identityHigh, 101, allowDuplicateKeys: false) != Scalar16Scalar16InsertResult.KeyConflict)
        {
            throw new InvalidDataException("Expected unique SS16-16 duplicate key to report key conflict.");
        }

        ValidateScalar16Scalar16Sorted(shelf.AsReadOnly());
    }


    /// <summary>
    /// Validates primitive `SS16-16` point and range-copy behavior over a shelf with duplicate key groups.<br/>
    /// The range covers one high-half key partition and returns both identity halves so scan and copy semantics are proven for both widened fields.<br/>
    /// </summary>
    /// <param name="profile">The `SS16-16` shelf profile.</param>
    /// <exception cref="InvalidDataException">Thrown when point lookup or range-copy behavior is incorrect.</exception>
    private static void ValidateScalar16Scalar16RangeCopy(Scalar16Scalar16Profile profile)
    {
        byte[] bytes = new byte[profile.ShelfExtentSize];
        Scalar16Scalar16 shelf = new(bytes, profile);
        shelf.Initialize();
        for (int i = 0; i < 128; i++)
        {
            ulong keyHigh = (ulong)(i / 64);
            ulong keyLow = (ulong)(i % 16);
            ulong identityHigh = (ulong)(i / 64);
            ulong identityLow = (ulong)(i * 10);
            Scalar16Scalar16InsertResult result = shelf.Insert(keyHigh, keyLow, identityHigh, identityLow, allowDuplicateKeys: true);
            if (result != Scalar16Scalar16InsertResult.Inserted)
            {
                throw new InvalidDataException($"Expected SS16-16 range setup insert at {i}, got {result}.");
            }
        }

        Scalar16Scalar16ReadOnly readOnly = shelf.AsReadOnly();
        if (!readOnly.TryFindFirstIdentity(1, 3, out ulong foundHigh, out ulong foundLow) || foundHigh != 1 || foundLow != 670)
        {
            throw new InvalidDataException("SS16-16 point lookup did not return the expected identity.");
        }

        ulong[] identityHighs = new ulong[8];
        ulong[] identityLows = new ulong[8];
        int copied = readOnly.CopyIdentitiesInKeyRange(1, 4, 1, 5, identityHighs, identityLows);
        if (copied != 8 || identityHighs[0] != 1 || identityLows[0] != 680 || identityHighs[7] != 1 || identityLows[7] != 1170)
        {
            throw new InvalidDataException("SS16-16 range copy did not return the expected identity window.");
        }
    }


    /// <summary>
    /// Validates the primitive `SS16-16` full-shelf boundary for the requested shelf profile.<br/>
    /// This proves the derived max item count, slot region, and item region line up for the widened fixed-width payload.<br/>
    /// </summary>
    /// <param name="profile">The `SS16-16` shelf profile.</param>
    /// <exception cref="InvalidDataException">Thrown when the shelf fills incorrectly or fails to report full.</exception>
    private static void ValidateScalar16Scalar16FullBoundary(Scalar16Scalar16Profile profile)
    {
        byte[] bytes = new byte[profile.ShelfExtentSize];
        Scalar16Scalar16 shelf = new(bytes, profile);
        shelf.Initialize();
        for (int i = 0; i < profile.MaxItemCount; i++)
        {
            Scalar16Scalar16Layout.EncodeUnsignedScalar16FromUInt64((ulong)i, out ulong keyHigh, out ulong keyLow);
            Scalar16Scalar16InsertResult result = shelf.Insert(
                keyHigh,
                keyLow,
                0,
                (ulong)i,
                allowDuplicateKeys: true);
            if (result != Scalar16Scalar16InsertResult.Inserted)
            {
                throw new InvalidDataException($"Expected SS16-16 fill insert at {i}, got {result}.");
            }
        }

        if (shelf.Insert(ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, allowDuplicateKeys: true) != Scalar16Scalar16InsertResult.Full)
        {
            throw new InvalidDataException("Expected full SS16-16 shelf to report full.");
        }

        ValidateScalar16Scalar16Sorted(shelf.AsReadOnly());
    }


    /// <summary>
    /// Validates that a primitive `SS16-16` shelf is sorted by `(keyHigh, keyLow, identityHigh, identityLow)`.<br/>
    /// This is the main regression check that widened-key and widened-identity tuple ordering is independent from earlier shelf shapes.<br/>
    /// </summary>
    /// <param name="shelf">The read-only `SS16-16` shelf projection.</param>
    /// <exception cref="InvalidDataException">Thrown when validation fails or tuple order is not monotonic.</exception>
    private static void ValidateScalar16Scalar16Sorted(Scalar16Scalar16ReadOnly shelf)
    {
        if (!shelf.IsValid)
        {
            throw new InvalidDataException("SS16-16 shelf failed validation.");
        }

        ulong previousKeyHigh = 0;
        ulong previousKeyLow = 0;
        ulong previousIdentityHigh = 0;
        ulong previousIdentityLow = 0;
        for (int i = 0; i < shelf.ItemCount; i++)
        {
            ulong keyHigh = shelf.ReadKeyHighAt(i);
            ulong keyLow = shelf.ReadKeyLowAt(i);
            ulong identityHigh = shelf.ReadIdentityHighAt(i);
            ulong identityLow = shelf.ReadIdentityLowAt(i);
            bool outOfOrder =
                keyHigh < previousKeyHigh ||
                (keyHigh == previousKeyHigh && keyLow < previousKeyLow) ||
                (keyHigh == previousKeyHigh && keyLow == previousKeyLow && identityHigh < previousIdentityHigh) ||
                (keyHigh == previousKeyHigh && keyLow == previousKeyLow && identityHigh == previousIdentityHigh && identityLow < previousIdentityLow);
            if (i > 0 && outOfOrder)
            {
                throw new InvalidDataException("SS16-16 shelf is not sorted by (keyHigh, keyLow, identityHigh, identityLow).");
            }

            previousKeyHigh = keyHigh;
            previousKeyLow = keyLow;
            previousIdentityHigh = identityHigh;
            previousIdentityLow = identityLow;
        }
    }


    private static void PopulateScalar8Scalar8Shelf(ref Scalar8Scalar8 shelf, int count)
    {
        for (int i = 0; i < count; i++)
        {
            ulong key = (ulong)((i * 37) % 512);
            ulong identity = (ulong)i;
            Scalar8Scalar8InsertResult result = shelf.Insert(
                Scalar8Scalar8Layout.EncodeUnsignedScalar8(key),
                Scalar8Scalar8Layout.EncodeUnsignedScalar8(identity),
                allowDuplicateKeys: true);
            if (result != Scalar8Scalar8InsertResult.Inserted)
            {
                throw new InvalidDataException($"Expected SS8-8 populate insert at {i}, got {result}.");
            }
        }
    }


    /// <summary>
    /// Validates the write shape for the first combined root-route to `SS16-8` shelf link commit.<br/>
    /// The expected shape is one appended widened-key shelf extent plus one fixed root-router rewrite, with no `SetLength` syscall.<br/>
    /// </summary>
    /// <param name="telemetry">The commit telemetry to validate.</param>
    /// <param name="profile">The `SS16-8` shelf profile used by the linked shelf.</param>
    /// <exception cref="InvalidDataException">Thrown when the commit shape drifts from the expected first integration shape.</exception>
    private static void ValidateScalar16Scalar8RouteLinkCommit(DataKernelCommitTelemetry telemetry, Scalar16Scalar8Profile profile)
    {
        long expectedBytes = profile.ShelfExtentSize + RouterLayout.Size;
        if (telemetry.SetLengthCallCount != 0)
        {
            throw new InvalidDataException("SS16-8 routed shelf link used SetLength.");
        }

        if (telemetry.BytesWritten != expectedBytes)
        {
            throw new InvalidDataException($"SS16-8 routed shelf link wrote {telemetry.BytesWritten} bytes instead of {expectedBytes}.");
        }

        if (telemetry.WriteCallCount is < 1 or > 2)
        {
            throw new InvalidDataException($"SS16-8 routed shelf link expected one or two file write calls after gap coalescing, got {telemetry.WriteCallCount}.");
        }
    }


    /// <summary>
    /// Validates the write shape for the first combined root-route to `FS32-8` shelf link commit.<br/>
    /// The expected shape is one appended fixed-key shelf extent plus one fixed root-router rewrite, with no `SetLength` syscall.<br/>
    /// </summary>
    /// <param name="telemetry">The commit telemetry to validate.</param>
    /// <param name="profile">The `FS32-8` shelf profile used by the linked shelf.</param>
    /// <exception cref="InvalidDataException">Thrown when the commit shape drifts from the expected first integration shape.</exception>
    private static void ValidateFixed32Scalar8RouteLinkCommit(DataKernelCommitTelemetry telemetry, Fixed32Scalar8Profile profile)
    {
        long expectedBytes = profile.ShelfExtentSize + RouterLayout.Size;
        if (telemetry.SetLengthCallCount != 0)
        {
            throw new InvalidDataException("FS32-8 routed shelf link used SetLength.");
        }

        if (telemetry.BytesWritten != expectedBytes)
        {
            throw new InvalidDataException($"FS32-8 routed shelf link wrote {telemetry.BytesWritten} bytes instead of {expectedBytes}.");
        }

        if (telemetry.WriteCallCount is < 1 or > 2)
        {
            throw new InvalidDataException($"FS32-8 routed shelf link expected one or two file write calls after gap coalescing, got {telemetry.WriteCallCount}.");
        }
    }


    /// <summary>
    /// Validates the write shape for the first combined root-route to `SS8-16` shelf link commit.<br/>
    /// The expected shape is one appended widened-identity shelf extent plus one fixed root-router rewrite, with no `SetLength` syscall.<br/>
    /// </summary>
    /// <param name="telemetry">The commit telemetry to validate.</param>
    /// <param name="profile">The `SS8-16` shelf profile used by the linked shelf.</param>
    /// <exception cref="InvalidDataException">Thrown when the commit shape drifts from the expected first integration shape.</exception>
    private static void ValidateScalar8Scalar16RouteLinkCommit(DataKernelCommitTelemetry telemetry, Scalar8Scalar16Profile profile)
    {
        long expectedBytes = profile.ShelfExtentSize + RouterLayout.Size;
        if (telemetry.SetLengthCallCount != 0)
        {
            throw new InvalidDataException("SS8-16 routed shelf link used SetLength.");
        }

        if (telemetry.BytesWritten != expectedBytes)
        {
            throw new InvalidDataException($"SS8-16 routed shelf link wrote {telemetry.BytesWritten} bytes instead of {expectedBytes}.");
        }

        if (telemetry.WriteCallCount is < 1 or > 2)
        {
            throw new InvalidDataException($"SS8-16 routed shelf link expected one or two file write calls after gap coalescing, got {telemetry.WriteCallCount}.");
        }
    }


    /// <summary>
    /// Validates the write shape for the first combined root-route to `SS16-16` shelf link commit.<br/>
    /// The expected shape is one appended widened-key and widened-identity shelf extent plus one fixed root-router rewrite, with no `SetLength` syscall.<br/>
    /// </summary>
    /// <param name="telemetry">The commit telemetry to validate.</param>
    /// <param name="profile">The `SS16-16` shelf profile used by the linked shelf.</param>
    /// <exception cref="InvalidDataException">Thrown when the commit shape drifts from the expected first integration shape.</exception>
    private static void ValidateScalar16Scalar16RouteLinkCommit(DataKernelCommitTelemetry telemetry, Scalar16Scalar16Profile profile)
    {
        long expectedBytes = profile.ShelfExtentSize + RouterLayout.Size;
        if (telemetry.SetLengthCallCount != 0)
        {
            throw new InvalidDataException("SS16-16 routed shelf link used SetLength.");
        }

        if (telemetry.BytesWritten != expectedBytes)
        {
            throw new InvalidDataException($"SS16-16 routed shelf link wrote {telemetry.BytesWritten} bytes instead of {expectedBytes}.");
        }

        if (telemetry.WriteCallCount is < 1 or > 2)
        {
            throw new InvalidDataException($"SS16-16 routed shelf link expected one or two file write calls after gap coalescing, got {telemetry.WriteCallCount}.");
        }
    }


    /// <summary>
    /// Validates the write shape for the first combined root-route to `SS8-8` shelf link commit.<br/>
    /// The expected shape is one appended shelf extent plus one fixed root-router rewrite, with no `SetLength` syscall.<br/>
    /// </summary>
    /// <param name="telemetry">The commit telemetry to validate.</param>
    /// <param name="profile">The `SS8-8` shelf profile used by the linked shelf.</param>
    /// <exception cref="InvalidDataException">Thrown when the commit shape drifts from the expected first integration shape.</exception>
    private static void ValidateScalar8Scalar8RouteLinkCommit(DataKernelCommitTelemetry telemetry, Scalar8Scalar8Profile profile)
    {
        long expectedBytes = profile.ShelfExtentSize + RouterLayout.Size;
        if (telemetry.SetLengthCallCount != 0)
        {
            throw new InvalidDataException("SS8-8 routed shelf link used SetLength.");
        }

        if (telemetry.BytesWritten != expectedBytes)
        {
            throw new InvalidDataException($"SS8-8 routed shelf link wrote {telemetry.BytesWritten} bytes instead of {expectedBytes}.");
        }

        if (telemetry.WriteCallCount is < 1 or > 2)
        {
            throw new InvalidDataException($"SS8-8 routed shelf link expected one or two file write calls after gap coalescing, got {telemetry.WriteCallCount}.");
        }
    }


    /// <summary>
    /// Validates the write shape for a routed `SS8-8` no-split insert.<br/>
    /// The no-split mutation should rewrite only the existing shelf extent and must not dirty the router.<br/>
    /// </summary>
    /// <param name="telemetry">The commit telemetry to validate.</param>
    /// <param name="profile">The `SS8-8` shelf profile used by the rewritten shelf.</param>
    /// <exception cref="InvalidDataException">Thrown when the commit shape drifts from the expected no-split mutation shape.</exception>
    private static void ValidateScalar8Scalar8RouteInsertCommit(DataKernelCommitTelemetry telemetry, Scalar8Scalar8Profile profile)
    {
        if (telemetry.SetLengthCallCount != 0)
        {
            throw new InvalidDataException("SS8-8 routed no-split insert used SetLength.");
        }

        if (telemetry.BytesWritten != profile.ShelfExtentSize)
        {
            throw new InvalidDataException($"SS8-8 routed no-split insert wrote {telemetry.BytesWritten} bytes instead of one shelf extent.");
        }

        if (telemetry.WriteCallCount != 1)
        {
            throw new InvalidDataException($"SS8-8 routed no-split insert expected one file write call, got {telemetry.WriteCallCount}.");
        }
    }


    /// <summary>
    /// Validates the write shape for a routed `SS16-8` no-split insert.<br/>
    /// The no-split mutation should rewrite only the existing widened-key shelf extent and must not dirty the router.<br/>
    /// </summary>
    /// <param name="telemetry">The commit telemetry to validate.</param>
    /// <param name="profile">The `SS16-8` shelf profile used by the rewritten shelf.</param>
    /// <exception cref="InvalidDataException">Thrown when the commit shape drifts from the expected no-split mutation shape.</exception>
    private static void ValidateScalar16Scalar8RouteInsertCommit(DataKernelCommitTelemetry telemetry, Scalar16Scalar8Profile profile)
    {
        if (telemetry.SetLengthCallCount != 0)
        {
            throw new InvalidDataException("SS16-8 routed no-split insert used SetLength.");
        }

        if (telemetry.BytesWritten != profile.ShelfExtentSize)
        {
            throw new InvalidDataException($"SS16-8 routed no-split insert wrote {telemetry.BytesWritten} bytes instead of one shelf extent.");
        }

        if (telemetry.WriteCallCount != 1)
        {
            throw new InvalidDataException($"SS16-8 routed no-split insert expected one file write call, got {telemetry.WriteCallCount}.");
        }
    }


    /// <summary>
    /// Validates the write shape for a routed `FS32-8` no-split insert.<br/>
    /// The no-split mutation should rewrite only the existing fixed-key shelf extent and must not dirty the router.<br/>
    /// </summary>
    /// <param name="telemetry">The commit telemetry to validate.</param>
    /// <param name="profile">The `FS32-8` shelf profile used by the rewritten shelf.</param>
    /// <exception cref="InvalidDataException">Thrown when the commit shape drifts from the expected no-split mutation shape.</exception>
    private static void ValidateFixed32Scalar8RouteInsertCommit(DataKernelCommitTelemetry telemetry, Fixed32Scalar8Profile profile)
    {
        if (telemetry.SetLengthCallCount != 0)
        {
            throw new InvalidDataException("FS32-8 routed no-split insert used SetLength.");
        }

        if (telemetry.BytesWritten != profile.ShelfExtentSize)
        {
            throw new InvalidDataException($"FS32-8 routed no-split insert wrote {telemetry.BytesWritten} bytes instead of one shelf extent.");
        }

        if (telemetry.WriteCallCount != 1)
        {
            throw new InvalidDataException($"FS32-8 routed no-split insert expected one file write call, got {telemetry.WriteCallCount}.");
        }
    }


    /// <summary>
    /// Validates the write shape for a rooted `FS32-8` split where one existing shelf is rewritten, one new shelf is appended, and one root-router extent is rewritten.<br/>
    /// The current physical shape may write-combine the adjacent root-router, left-shelf, and right-shelf extents, so the backing syscall count is allowed to be one or two for three logical extents.<br/>
    /// </summary>
    /// <param name="telemetry">The commit telemetry to validate.</param>
    /// <param name="profile">The `FS32-8` shelf profile used by the rewritten and appended shelves.</param>
    /// <exception cref="InvalidDataException">Thrown when the commit shape drifts from the expected rooted split mutation shape.</exception>
    private static void ValidateFixed32Scalar8RouteSplitCommit(DataKernelCommitTelemetry telemetry, Fixed32Scalar8Profile profile)
    {
        long expectedBytes = (profile.ShelfExtentSize * 2L) + RouterLayout.Size;
        if (telemetry.SetLengthCallCount != 0)
        {
            throw new InvalidDataException("FS32-8 routed split used SetLength.");
        }

        if (telemetry.BytesWritten != expectedBytes)
        {
            throw new InvalidDataException($"FS32-8 routed split wrote {telemetry.BytesWritten} bytes instead of {expectedBytes}.");
        }

        if (telemetry.WriteCallCount is < 1 or > 2)
        {
            throw new InvalidDataException($"FS32-8 routed split expected one or two write-combined file write calls, got {telemetry.WriteCallCount}.");
        }
    }


    /// <summary>
    /// Validates the write shape for a routed `SS8-16` no-split insert.<br/>
    /// The no-split mutation should rewrite only the existing widened-identity shelf extent and must not dirty the router.<br/>
    /// </summary>
    /// <param name="telemetry">The commit telemetry to validate.</param>
    /// <param name="profile">The `SS8-16` shelf profile used by the rewritten shelf.</param>
    /// <exception cref="InvalidDataException">Thrown when the commit shape drifts from the expected no-split mutation shape.</exception>
    private static void ValidateScalar8Scalar16RouteInsertCommit(DataKernelCommitTelemetry telemetry, Scalar8Scalar16Profile profile)
    {
        if (telemetry.SetLengthCallCount != 0)
        {
            throw new InvalidDataException("SS8-16 routed no-split insert used SetLength.");
        }

        if (telemetry.BytesWritten != profile.ShelfExtentSize)
        {
            throw new InvalidDataException($"SS8-16 routed no-split insert wrote {telemetry.BytesWritten} bytes instead of one shelf extent.");
        }

        if (telemetry.WriteCallCount != 1)
        {
            throw new InvalidDataException($"SS8-16 routed no-split insert expected one file write call, got {telemetry.WriteCallCount}.");
        }
    }


    /// <summary>
    /// Validates the write shape for a routed `SS16-16` no-split insert.<br/>
    /// The no-split mutation should rewrite only the existing widened-key and widened-identity shelf extent and must not dirty the router.<br/>
    /// </summary>
    /// <param name="telemetry">The commit telemetry to validate.</param>
    /// <param name="profile">The `SS16-16` shelf profile used by the rewritten shelf.</param>
    /// <exception cref="InvalidDataException">Thrown when the commit shape drifts from the expected no-split mutation shape.</exception>
    private static void ValidateScalar16Scalar16RouteInsertCommit(DataKernelCommitTelemetry telemetry, Scalar16Scalar16Profile profile)
    {
        if (telemetry.SetLengthCallCount != 0)
        {
            throw new InvalidDataException("SS16-16 routed no-split insert used SetLength.");
        }

        if (telemetry.BytesWritten != profile.ShelfExtentSize)
        {
            throw new InvalidDataException($"SS16-16 routed no-split insert wrote {telemetry.BytesWritten} bytes instead of one shelf extent.");
        }

        if (telemetry.WriteCallCount != 1)
        {
            throw new InvalidDataException($"SS16-16 routed no-split insert expected one file write call, got {telemetry.WriteCallCount}.");
        }
    }


    /// <summary>
    /// Validates the write shape for a rooted `SS8-8` split where one existing shelf is rewritten, one new shelf is appended, and one root-router extent is rewritten.<br/>
    /// The current physical shape may write-combine the adjacent root-router, left-shelf, and right-shelf extents, so the backing syscall count is allowed to be one or two for three logical extents.<br/>
    /// </summary>
    /// <param name="telemetry">The commit telemetry to validate.</param>
    /// <param name="profile">The `SS8-8` shelf profile used by the rewritten and appended shelves.</param>
    /// <exception cref="InvalidDataException">Thrown when the commit shape drifts from the expected rooted split mutation shape.</exception>
    private static void ValidateScalar8Scalar8RouteSplitCommit(DataKernelCommitTelemetry telemetry, Scalar8Scalar8Profile profile)
    {
        long expectedBytes = (profile.ShelfExtentSize * 2L) + RouterLayout.Size;
        if (telemetry.SetLengthCallCount != 0)
        {
            throw new InvalidDataException("SS8-8 routed split used SetLength.");
        }

        if (telemetry.BytesWritten != expectedBytes)
        {
            throw new InvalidDataException($"SS8-8 routed split wrote {telemetry.BytesWritten} bytes instead of {expectedBytes}.");
        }

        if (telemetry.WriteCallCount is < 1 or > 2)
        {
            throw new InvalidDataException($"SS8-8 routed split expected one or two write-combined file write calls, got {telemetry.WriteCallCount}.");
        }
    }


    /// <summary>
    /// Validates the write shape for a rooted `SS16-8` split where one existing shelf is rewritten, one new shelf is appended, and one root-router extent is rewritten.<br/>
    /// The current physical shape may write-combine the adjacent root-router, left-shelf, and right-shelf extents, so the backing syscall count is allowed to be one or two for three logical extents.<br/>
    /// </summary>
    /// <param name="telemetry">The commit telemetry to validate.</param>
    /// <param name="profile">The `SS16-8` shelf profile used by the rewritten and appended shelves.</param>
    /// <exception cref="InvalidDataException">Thrown when the commit shape drifts from the expected rooted split mutation shape.</exception>
    private static void ValidateScalar16Scalar8RouteSplitCommit(DataKernelCommitTelemetry telemetry, Scalar16Scalar8Profile profile)
    {
        long expectedBytes = (profile.ShelfExtentSize * 2L) + RouterLayout.Size;
        if (telemetry.SetLengthCallCount != 0)
        {
            throw new InvalidDataException("SS16-8 routed split used SetLength.");
        }

        if (telemetry.BytesWritten != expectedBytes)
        {
            throw new InvalidDataException($"SS16-8 routed split wrote {telemetry.BytesWritten} bytes instead of {expectedBytes}.");
        }

        if (telemetry.WriteCallCount is < 1 or > 2)
        {
            throw new InvalidDataException($"SS16-8 routed split expected one or two write-combined file write calls, got {telemetry.WriteCallCount}.");
        }
    }


    /// <summary>
    /// Validates the write shape for a rooted `SS8-16` split where one existing shelf is rewritten, one new shelf is appended, and one root-router extent is rewritten.<br/>
    /// The current physical shape may write-combine the adjacent root-router, left-shelf, and right-shelf extents, so the backing syscall count is allowed to be one or two for three logical extents.<br/>
    /// </summary>
    /// <param name="telemetry">The commit telemetry to validate.</param>
    /// <param name="profile">The `SS8-16` shelf profile used by the rewritten and appended shelves.</param>
    /// <exception cref="InvalidDataException">Thrown when the commit shape drifts from the expected rooted split mutation shape.</exception>
    private static void ValidateScalar8Scalar16RouteSplitCommit(DataKernelCommitTelemetry telemetry, Scalar8Scalar16Profile profile)
    {
        long expectedBytes = (profile.ShelfExtentSize * 2L) + RouterLayout.Size;
        if (telemetry.SetLengthCallCount != 0)
        {
            throw new InvalidDataException("SS8-16 routed split used SetLength.");
        }

        if (telemetry.BytesWritten != expectedBytes)
        {
            throw new InvalidDataException($"SS8-16 routed split wrote {telemetry.BytesWritten} bytes instead of {expectedBytes}.");
        }

        if (telemetry.WriteCallCount is < 1 or > 2)
        {
            throw new InvalidDataException($"SS8-16 routed split expected one or two write-combined file write calls, got {telemetry.WriteCallCount}.");
        }
    }


    /// <summary>
    /// Validates the write shape for a rooted `SS16-16` split where one existing shelf is rewritten, one new shelf is appended, and one root-router extent is rewritten.<br/>
    /// The current physical shape may write-combine the adjacent root-router, left-shelf, and right-shelf extents, so the backing syscall count is allowed to be one or two for three logical extents.<br/>
    /// </summary>
    /// <param name="telemetry">The commit telemetry to validate.</param>
    /// <param name="profile">The `SS16-16` shelf profile used by the rewritten and appended shelves.</param>
    /// <exception cref="InvalidDataException">Thrown when the commit shape drifts from the expected rooted split mutation shape.</exception>
    private static void ValidateScalar16Scalar16RouteSplitCommit(DataKernelCommitTelemetry telemetry, Scalar16Scalar16Profile profile)
    {
        long expectedBytes = (profile.ShelfExtentSize * 2L) + RouterLayout.Size;
        if (telemetry.SetLengthCallCount != 0)
        {
            throw new InvalidDataException("SS16-16 routed split used SetLength.");
        }

        if (telemetry.BytesWritten != expectedBytes)
        {
            throw new InvalidDataException($"SS16-16 routed split wrote {telemetry.BytesWritten} bytes instead of {expectedBytes}.");
        }

        if (telemetry.WriteCallCount is < 1 or > 2)
        {
            throw new InvalidDataException($"SS16-16 routed split expected one or two write-combined file write calls, got {telemetry.WriteCallCount}.");
        }
    }


    /// <summary>
    /// Validates the write shape for a same-root-prefix `SS8-8` split where the old shelf offset is rewritten as a child router and two replacement shelves are appended.<br/>
    /// The parent root router must not be rewritten; the two appended shelves are contiguous and should be write-combined by `DataKernel`.<br/>
    /// </summary>
    /// <param name="telemetry">The commit telemetry to validate.</param>
    /// <param name="profile">The `SS8-8` shelf profile used by the appended replacement shelves.</param>
    /// <exception cref="InvalidDataException">Thrown when the commit shape drifts from the expected transform split mutation shape.</exception>
    private static void ValidateScalar8Scalar8TransformCommit(DataKernelCommitTelemetry telemetry, Scalar8Scalar8Profile profile)
    {
        long expectedBytes = (profile.ShelfExtentSize * 2L) + RouterLayout.Size;
        if (telemetry.SetLengthCallCount != 0)
        {
            throw new InvalidDataException("SS8-8 transform split used SetLength.");
        }

        if (telemetry.BytesWritten != expectedBytes)
        {
            throw new InvalidDataException($"SS8-8 transform split wrote {telemetry.BytesWritten} bytes instead of {expectedBytes}.");
        }

        if (telemetry.WriteCallCount != 2)
        {
            throw new InvalidDataException($"SS8-8 transform split expected two file write calls, got {telemetry.WriteCallCount}.");
        }
    }


    /// <summary>
    /// Validates the write shape for a same-root-prefix `SS16-8` split where the old shelf offset is rewritten as a child router and two replacement shelves are appended.<br/>
    /// The parent root router must not be rewritten; the two appended widened-key shelves are contiguous and should be write-combined by `DataKernel`.<br/>
    /// </summary>
    /// <param name="telemetry">The commit telemetry to validate.</param>
    /// <param name="profile">The `SS16-8` shelf profile used by the appended replacement shelves.</param>
    /// <exception cref="InvalidDataException">Thrown when the commit shape drifts from the expected transform split mutation shape.</exception>
    private static void ValidateScalar16Scalar8TransformCommit(DataKernelCommitTelemetry telemetry, Scalar16Scalar8Profile profile)
    {
        long expectedBytes = (profile.ShelfExtentSize * 2L) + RouterLayout.Size;
        if (telemetry.SetLengthCallCount != 0)
        {
            throw new InvalidDataException("SS16-8 transform split used SetLength.");
        }

        if (telemetry.BytesWritten != expectedBytes)
        {
            throw new InvalidDataException($"SS16-8 transform split wrote {telemetry.BytesWritten} bytes instead of {expectedBytes}.");
        }

        if (telemetry.WriteCallCount is < 1 or > 2)
        {
            throw new InvalidDataException($"SS16-8 transform split expected one or two file write calls, got {telemetry.WriteCallCount}.");
        }
    }


    /// <summary>
    /// Validates the write shape for a same-root-prefix `FS32-8` split where the old shelf offset is rewritten as a child router and two replacement shelves are appended.<br/>
    /// The parent root router must not be rewritten; the two appended fixed-32-byte-key shelves are contiguous and should be write-combined by `DataKernel`.<br/>
    /// </summary>
    /// <param name="telemetry">The commit telemetry to validate.</param>
    /// <param name="profile">The `FS32-8` shelf profile used by the appended replacement shelves.</param>
    /// <exception cref="InvalidDataException">Thrown when the commit shape drifts from the expected transform split mutation shape.</exception>
    private static void ValidateFixed32Scalar8TransformCommit(DataKernelCommitTelemetry telemetry, Fixed32Scalar8Profile profile)
    {
        long expectedBytes = (profile.ShelfExtentSize * 2L) + RouterLayout.Size;
        if (telemetry.SetLengthCallCount != 0)
        {
            throw new InvalidDataException("FS32-8 transform split used SetLength.");
        }

        if (telemetry.BytesWritten != expectedBytes)
        {
            throw new InvalidDataException($"FS32-8 transform split wrote {telemetry.BytesWritten} bytes instead of {expectedBytes}.");
        }

        if (telemetry.WriteCallCount is < 1 or > 2)
        {
            throw new InvalidDataException($"FS32-8 transform split expected one or two file write calls, got {telemetry.WriteCallCount}.");
        }
    }


    /// <summary>
    /// Validates the write shape for a same-root-prefix `SS8-16` split where the old shelf offset is rewritten as a child router and two replacement shelves are appended.<br/>
    /// The parent root router must not be rewritten; the two appended widened-identity shelves are contiguous and should be write-combined by `DataKernel`.<br/>
    /// </summary>
    /// <param name="telemetry">The commit telemetry to validate.</param>
    /// <param name="profile">The `SS8-16` shelf profile used by the appended replacement shelves.</param>
    /// <exception cref="InvalidDataException">Thrown when the commit shape drifts from the expected transform split mutation shape.</exception>
    private static void ValidateScalar8Scalar16TransformCommit(DataKernelCommitTelemetry telemetry, Scalar8Scalar16Profile profile)
    {
        long expectedBytes = (profile.ShelfExtentSize * 2L) + RouterLayout.Size;
        if (telemetry.SetLengthCallCount != 0)
        {
            throw new InvalidDataException("SS8-16 transform split used SetLength.");
        }

        if (telemetry.BytesWritten != expectedBytes)
        {
            throw new InvalidDataException($"SS8-16 transform split wrote {telemetry.BytesWritten} bytes instead of {expectedBytes}.");
        }

        if (telemetry.WriteCallCount is < 1 or > 2)
        {
            throw new InvalidDataException($"SS8-16 transform split expected one or two file write calls, got {telemetry.WriteCallCount}.");
        }
    }


    /// <summary>
    /// Validates the write shape for a same-root-prefix `SS16-16` split where the old shelf offset is rewritten as a child router and two replacement shelves are appended.<br/>
    /// The parent root router must not be rewritten; the two appended widened-key and widened-identity shelves are contiguous and should be write-combined by `DataKernel`.<br/>
    /// </summary>
    /// <param name="telemetry">The commit telemetry to validate.</param>
    /// <param name="profile">The `SS16-16` shelf profile used by the appended replacement shelves.</param>
    /// <exception cref="InvalidDataException">Thrown when the commit shape drifts from the expected transform split mutation shape.</exception>
    private static void ValidateScalar16Scalar16TransformCommit(DataKernelCommitTelemetry telemetry, Scalar16Scalar16Profile profile)
    {
        long expectedBytes = (profile.ShelfExtentSize * 2L) + RouterLayout.Size;
        if (telemetry.SetLengthCallCount != 0)
        {
            throw new InvalidDataException("SS16-16 transform split used SetLength.");
        }

        if (telemetry.BytesWritten != expectedBytes)
        {
            throw new InvalidDataException($"SS16-16 transform split wrote {telemetry.BytesWritten} bytes instead of {expectedBytes}.");
        }

        if (telemetry.WriteCallCount is < 1 or > 2)
        {
            throw new InvalidDataException($"SS16-16 transform split expected one or two file write calls, got {telemetry.WriteCallCount}.");
        }
    }


    /// <summary>
    /// Validates a routed `SS8-8` shelf read through the root router.<br/>
    /// This checks route target, read telemetry, sorted shelf shape, seek-one behavior, range walk, and scoop output.<br/>
    /// </summary>
    /// <param name="session">The file session used for routed reads.</param>
    /// <param name="rootOffset">The persisted root-router offset.</param>
    /// <param name="expectedShelfOffset">The expected routed shelf offset.</param>
    /// <param name="profile">The `SS8-8` shelf profile.</param>
    /// <param name="itemCount">The expected number of deterministic shelf tuples.</param>
    /// <exception cref="InvalidDataException">Thrown when routed shelf behavior or telemetry does not match expectations.</exception>
    private static void ValidateRoutedScalar8Scalar8Shelf(
        LibraDexFileSession session,
        long rootOffset,
        long expectedShelfOffset,
        Scalar8Scalar8Profile profile,
        int itemCount)
    {
        ValidateRoutedScalar8Scalar8Shelf(session, rootOffset, expectedShelfOffset, profile, itemCount, 0, 0);
    }


    /// <summary>
    /// Validates a routed `SS8-8` shelf read through the root router and optionally checks a specific tuple.<br/>
    /// This overload is used by the routed insert sanity path after it adds a tuple to the existing shelf.<br/>
    /// </summary>
    /// <param name="session">The file session used for routed reads.</param>
    /// <param name="rootOffset">The persisted root-router offset.</param>
    /// <param name="expectedShelfOffset">The expected routed shelf offset.</param>
    /// <param name="profile">The `SS8-8` shelf profile.</param>
    /// <param name="itemCount">The expected number of deterministic shelf tuples.</param>
    /// <param name="expectedKey">The optional encoded key that must exist when nonzero.</param>
    /// <param name="expectedIdentity">The optional encoded identity that must exist when nonzero.</param>
    /// <exception cref="InvalidDataException">Thrown when routed shelf behavior or telemetry does not match expectations.</exception>
    private static void ValidateRoutedScalar8Scalar8Shelf(
        LibraDexFileSession session,
        long rootOffset,
        long expectedShelfOffset,
        Scalar8Scalar8Profile profile,
        int itemCount,
        ulong expectedKey,
        ulong expectedIdentity)
    {
        byte[] shelfBytes = new byte[profile.ShelfExtentSize];
        long actualShelfOffset = session.ReadRoutedScalar8Scalar8Shelf(rootOffset, 0x00, profile, shelfBytes);
        if (actualShelfOffset != expectedShelfOffset)
        {
            throw new InvalidDataException($"Routed SS8-8 shelf offset mismatch. Expected {expectedShelfOffset}, actual {actualShelfOffset}.");
        }

        DataKernelReadTelemetry readTelemetry = session.GetAndResetReadTelemetry();
        PrintRead("ss8-8 routed read", readTelemetry);
        if (readTelemetry.ReadCallCount != 2 || readTelemetry.BytesRead != RouterLayout.Size + profile.ShelfExtentSize)
        {
            throw new InvalidDataException("Routed SS8-8 read shape should be one root-router read plus one shelf read.");
        }

        Scalar8Scalar8ReadOnly shelf = new(shelfBytes, profile);
        ValidateScalar8Scalar8Sorted(shelf);
        if (shelf.ItemCount != itemCount)
        {
            throw new InvalidDataException($"Routed SS8-8 shelf item count mismatch. Expected {itemCount}, actual {shelf.ItemCount}.");
        }

        if ((expectedKey != 0 || expectedIdentity != 0) && !shelf.Contains(expectedKey, expectedIdentity))
        {
            throw new InvalidDataException("Routed SS8-8 shelf does not contain the expected inserted tuple.");
        }

        int seekSlot = shelf.LowerBoundKey(Scalar8Scalar8Layout.EncodeUnsignedScalar8(128));
        if (seekSlot != 128 || shelf.ReadIdentityAt(seekSlot) != Scalar8Scalar8Layout.EncodeUnsignedScalar8(128))
        {
            throw new InvalidDataException("Routed SS8-8 seek-one validation failed.");
        }

        const int rangeStart = 128;
        const int rangeLength = 64;
        long rangeSum = 0;
        int rangeSlot = shelf.LowerBoundKey(Scalar8Scalar8Layout.EncodeUnsignedScalar8(rangeStart));
        ulong[] scoop = new ulong[rangeLength];
        for (int i = 0; i < rangeLength; i++)
        {
            ulong identity = shelf.ReadIdentityAt(rangeSlot + i);
            scoop[i] = identity;
            rangeSum += (long)identity;
        }

        long expectedSum = ((long)rangeStart + rangeStart + rangeLength - 1) * rangeLength / 2;
        if (rangeSum != expectedSum || scoop[0] != (ulong)rangeStart || scoop[rangeLength - 1] != (ulong)(rangeStart + rangeLength - 1))
        {
            throw new InvalidDataException("Routed SS8-8 range/scoop validation failed.");
        }
    }


    /// <summary>
    /// Validates a routed `SS16-8` shelf read through the root router.<br/>
    /// This checks route target, read telemetry, sorted shelf shape, seek-one behavior, and range-copy output over widened-key shelf bytes.<br/>
    /// </summary>
    /// <param name="session">The file session used for routed reads.</param>
    /// <param name="rootOffset">The persisted root-router offset.</param>
    /// <param name="expectedShelfOffset">The expected routed shelf offset.</param>
    /// <param name="profile">The `SS16-8` shelf profile.</param>
    /// <param name="itemCount">The expected number of deterministic tuples in the shelf.</param>
    /// <param name="expectedKeyHigh">The encoded high key half of the required tuple.</param>
    /// <param name="expectedKeyLow">The encoded low key half of the required tuple.</param>
    /// <param name="expectedIdentity">The encoded identity of the required tuple.</param>
    /// <exception cref="InvalidDataException">Thrown when routed shelf behavior or telemetry does not match expectations.</exception>
    private static void ValidateRoutedScalar16Scalar8Shelf(
        LibraDexFileSession session,
        long rootOffset,
        long expectedShelfOffset,
        Scalar16Scalar8Profile profile,
        int itemCount,
        ulong expectedKeyHigh = 0,
        ulong expectedKeyLow = 128,
        ulong expectedIdentity = 128)
    {
        byte[] shelfBytes = new byte[profile.ShelfExtentSize];
        long actualShelfOffset = session.ReadRoutedScalar16Scalar8Shelf(rootOffset, 0x00, profile, shelfBytes);
        if (actualShelfOffset != expectedShelfOffset)
        {
            throw new InvalidDataException($"Routed SS16-8 shelf offset mismatch. Expected {expectedShelfOffset}, actual {actualShelfOffset}.");
        }

        DataKernelReadTelemetry readTelemetry = session.GetAndResetReadTelemetry();
        PrintRead("ss16-8 routed read", readTelemetry);
        if (readTelemetry.ReadCallCount != 2 || readTelemetry.BytesRead != RouterLayout.Size + profile.ShelfExtentSize)
        {
            throw new InvalidDataException("Routed SS16-8 read shape should be one root-router read plus one shelf read.");
        }

        Scalar16Scalar8ReadOnly shelf = new(shelfBytes, profile);
        ValidateScalar16Scalar8Sorted(shelf);
        if (shelf.ItemCount != itemCount)
        {
            throw new InvalidDataException($"Routed SS16-8 shelf item count mismatch. Expected {itemCount}, actual {shelf.ItemCount}.");
        }

        if (!shelf.Contains(expectedKeyHigh, expectedKeyLow, expectedIdentity))
        {
            throw new InvalidDataException("Routed SS16-8 shelf does not contain the expected tuple.");
        }

        int seekSlot = shelf.LowerBoundKey(0, 128);
        if (seekSlot != 128 || shelf.ReadIdentityAt(seekSlot) != 128)
        {
            throw new InvalidDataException("Routed SS16-8 seek-one validation failed.");
        }

        const int rangeStart = 128;
        const int rangeLength = 64;
        ulong[] identities = new ulong[rangeLength];
        int copied = shelf.CopyIdentitiesInKeyRange(0, rangeStart, 0, rangeStart + rangeLength - 1, identities);
        long rangeSum = 0;
        for (int i = 0; i < copied; i++)
        {
            rangeSum += (long)identities[i];
        }

        long expectedSum = ((long)rangeStart + rangeStart + rangeLength - 1) * rangeLength / 2;
        if (copied != rangeLength || rangeSum != expectedSum || identities[0] != (ulong)rangeStart || identities[rangeLength - 1] != (ulong)(rangeStart + rangeLength - 1))
        {
            throw new InvalidDataException("Routed SS16-8 range-copy validation failed.");
        }
    }


    /// <summary>
    /// Validates a routed `FS32-8` shelf read through the root router.<br/>
    /// This checks route target, read telemetry, sorted shelf shape, seek-one behavior, and range-copy output over fixed 32-byte keys.<br/>
    /// </summary>
    /// <param name="session">The file session used for routed reads.</param>
    /// <param name="rootOffset">The persisted root-router offset.</param>
    /// <param name="expectedShelfOffset">The expected routed shelf offset.</param>
    /// <param name="profile">The `FS32-8` shelf profile.</param>
    /// <param name="itemCount">The expected number of deterministic tuples in the shelf.</param>
    /// <param name="expectedKey0">The encoded key part 0 of the required tuple.</param>
    /// <param name="expectedKey1">The encoded key part 1 of the required tuple.</param>
    /// <param name="expectedKey2">The encoded key part 2 of the required tuple.</param>
    /// <param name="expectedKey3">The encoded key part 3 of the required tuple.</param>
    /// <param name="expectedIdentity">The encoded identity of the required tuple.</param>
    /// <exception cref="InvalidDataException">Thrown when routed shelf behavior or telemetry does not match expectations.</exception>
    private static void ValidateRoutedFixed32Scalar8Shelf(
        LibraDexFileSession session,
        long rootOffset,
        long expectedShelfOffset,
        Fixed32Scalar8Profile profile,
        int itemCount,
        ulong expectedKey0 = 0,
        ulong expectedKey1 = 0,
        ulong expectedKey2 = 0,
        ulong expectedKey3 = 128,
        ulong expectedIdentity = 128)
    {
        byte[] shelfBytes = new byte[profile.ShelfExtentSize];
        long actualShelfOffset = session.ReadRoutedFixed32Scalar8Shelf(rootOffset, 0x00, profile, shelfBytes);
        if (actualShelfOffset != expectedShelfOffset)
        {
            throw new InvalidDataException($"Routed FS32-8 shelf offset mismatch. Expected {expectedShelfOffset}, actual {actualShelfOffset}.");
        }

        DataKernelReadTelemetry readTelemetry = session.GetAndResetReadTelemetry();
        PrintRead("fs32-8 routed read", readTelemetry);
        if (readTelemetry.ReadCallCount != 2 || readTelemetry.BytesRead != RouterLayout.Size + profile.ShelfExtentSize)
        {
            throw new InvalidDataException("Routed FS32-8 read shape should be one root-router read plus one shelf read.");
        }

        Fixed32Scalar8ReadOnly shelf = new(shelfBytes, profile);
        ValidateFixed32Scalar8Sorted(shelf);
        if (shelf.ItemCount != itemCount)
        {
            throw new InvalidDataException($"Routed FS32-8 shelf item count mismatch. Expected {itemCount}, actual {shelf.ItemCount}.");
        }

        if (!shelf.Contains(expectedKey0, expectedKey1, expectedKey2, expectedKey3, expectedIdentity))
        {
            throw new InvalidDataException("Routed FS32-8 shelf does not contain the expected tuple.");
        }

        Fixed32Scalar8Layout.EncodeFixed32FromUInt64(128, out ulong seek0, out ulong seek1, out ulong seek2, out ulong seek3);
        int seekSlot = shelf.LowerBoundKey(seek0, seek1, seek2, seek3);
        if (seekSlot != 128 || shelf.ReadIdentityAt(seekSlot) != 128)
        {
            throw new InvalidDataException("Routed FS32-8 seek-one validation failed.");
        }

        const int rangeStart = 128;
        const int rangeLength = 64;
        Fixed32Scalar8Layout.EncodeFixed32FromUInt64(rangeStart, out ulong lower0, out ulong lower1, out ulong lower2, out ulong lower3);
        Fixed32Scalar8Layout.EncodeFixed32FromUInt64(rangeStart + rangeLength - 1, out ulong upper0, out ulong upper1, out ulong upper2, out ulong upper3);
        ulong[] identities = new ulong[rangeLength];
        int copied = shelf.CopyIdentitiesInKeyRange(lower0, lower1, lower2, lower3, upper0, upper1, upper2, upper3, identities);
        long rangeSum = 0;
        for (int i = 0; i < copied; i++)
        {
            rangeSum += (long)identities[i];
        }

        long expectedSum = ((long)rangeStart + rangeStart + rangeLength - 1) * rangeLength / 2;
        if (copied != rangeLength || rangeSum != expectedSum || identities[0] != (ulong)rangeStart || identities[rangeLength - 1] != (ulong)(rangeStart + rangeLength - 1))
        {
            throw new InvalidDataException("Routed FS32-8 range-copy validation failed.");
        }
    }


    /// <summary>
    /// Validates a routed `SS8-16` shelf read through the root router.<br/>
    /// This checks route target, read telemetry, sorted shelf shape, seek-one behavior, and range-copy output over both widened identity halves.<br/>
    /// </summary>
    /// <param name="session">The file session used for routed reads.</param>
    /// <param name="rootOffset">The persisted root-router offset.</param>
    /// <param name="expectedShelfOffset">The expected routed shelf offset.</param>
    /// <param name="profile">The `SS8-16` shelf profile.</param>
    /// <param name="itemCount">The expected number of deterministic tuples in the shelf.</param>
    /// <param name="expectedKey">The encoded key of the required tuple.</param>
    /// <param name="expectedIdentityHigh">The encoded high identity half of the required tuple.</param>
    /// <param name="expectedIdentityLow">The encoded low identity half of the required tuple.</param>
    /// <exception cref="InvalidDataException">Thrown when routed shelf behavior or telemetry does not match expectations.</exception>
    private static void ValidateRoutedScalar8Scalar16Shelf(
        LibraDexFileSession session,
        long rootOffset,
        long expectedShelfOffset,
        Scalar8Scalar16Profile profile,
        int itemCount,
        ulong expectedKey = 128,
        ulong expectedIdentityHigh = 0,
        ulong expectedIdentityLow = 128)
    {
        byte[] shelfBytes = new byte[profile.ShelfExtentSize];
        long actualShelfOffset = session.ReadRoutedScalar8Scalar16Shelf(rootOffset, 0x00, profile, shelfBytes);
        if (actualShelfOffset != expectedShelfOffset)
        {
            throw new InvalidDataException($"Routed SS8-16 shelf offset mismatch. Expected {expectedShelfOffset}, actual {actualShelfOffset}.");
        }

        DataKernelReadTelemetry readTelemetry = session.GetAndResetReadTelemetry();
        PrintRead("ss8-16 routed read", readTelemetry);
        if (readTelemetry.ReadCallCount != 2 || readTelemetry.BytesRead != RouterLayout.Size + profile.ShelfExtentSize)
        {
            throw new InvalidDataException("Routed SS8-16 read shape should be one root-router read plus one shelf read.");
        }

        Scalar8Scalar16ReadOnly shelf = new(shelfBytes, profile);
        ValidateScalar8Scalar16Sorted(shelf);
        if (shelf.ItemCount != itemCount)
        {
            throw new InvalidDataException($"Routed SS8-16 shelf item count mismatch. Expected {itemCount}, actual {shelf.ItemCount}.");
        }

        ulong encodedExpectedKey = Scalar8Scalar16Layout.EncodeUnsignedScalar8(expectedKey);
        if (!shelf.Contains(encodedExpectedKey, expectedIdentityHigh, expectedIdentityLow))
        {
            throw new InvalidDataException("Routed SS8-16 shelf does not contain the expected tuple.");
        }

        int seekSlot = shelf.LowerBoundKey(Scalar8Scalar16Layout.EncodeUnsignedScalar8(128));
        if (seekSlot != 128 || shelf.ReadIdentityHighAt(seekSlot) != 0 || shelf.ReadIdentityLowAt(seekSlot) != 128)
        {
            throw new InvalidDataException("Routed SS8-16 seek-one validation failed.");
        }

        const int rangeStart = 128;
        const int rangeLength = 64;
        ulong[] identityHighs = new ulong[rangeLength];
        ulong[] identityLows = new ulong[rangeLength];
        int copied = shelf.CopyIdentitiesInKeyRange(
            Scalar8Scalar16Layout.EncodeUnsignedScalar8(rangeStart),
            Scalar8Scalar16Layout.EncodeUnsignedScalar8(rangeStart + rangeLength - 1),
            identityHighs,
            identityLows);
        long rangeSum = 0;
        for (int i = 0; i < copied; i++)
        {
            rangeSum += (long)identityLows[i];
            if (identityHighs[i] != 0)
            {
                throw new InvalidDataException("Routed SS8-16 range-copy returned an unexpected high identity half.");
            }
        }

        long expectedSum = ((long)rangeStart + rangeStart + rangeLength - 1) * rangeLength / 2;
        if (copied != rangeLength || rangeSum != expectedSum || identityLows[0] != (ulong)rangeStart || identityLows[rangeLength - 1] != (ulong)(rangeStart + rangeLength - 1))
        {
            throw new InvalidDataException("Routed SS8-16 range-copy validation failed.");
        }
    }


    /// <summary>
    /// Validates a routed `SS16-16` shelf read through the root router.<br/>
    /// This checks route target, read telemetry, sorted shelf shape, seek-one behavior, and range-copy output over widened key and identity shelf bytes.<br/>
    /// </summary>
    /// <param name="session">The file session used for routed reads.</param>
    /// <param name="rootOffset">The persisted root-router offset.</param>
    /// <param name="expectedShelfOffset">The expected routed shelf offset.</param>
    /// <param name="profile">The `SS16-16` shelf profile.</param>
    /// <param name="itemCount">The expected number of deterministic tuples in the shelf.</param>
    /// <param name="expectedKeyHigh">The encoded high key half of the required tuple.</param>
    /// <param name="expectedKeyLow">The encoded low key half of the required tuple.</param>
    /// <param name="expectedIdentityHigh">The encoded high identity half of the required tuple.</param>
    /// <param name="expectedIdentityLow">The encoded low identity half of the required tuple.</param>
    /// <exception cref="InvalidDataException">Thrown when routed shelf behavior or telemetry does not match expectations.</exception>
    private static void ValidateRoutedScalar16Scalar16Shelf(
        LibraDexFileSession session,
        long rootOffset,
        long expectedShelfOffset,
        Scalar16Scalar16Profile profile,
        int itemCount,
        ulong expectedKeyHigh = 0,
        ulong expectedKeyLow = 128,
        ulong expectedIdentityHigh = 0,
        ulong expectedIdentityLow = 128)
    {
        byte[] shelfBytes = new byte[profile.ShelfExtentSize];
        long actualShelfOffset = session.ReadRoutedScalar16Scalar16Shelf(rootOffset, 0x00, profile, shelfBytes);
        if (actualShelfOffset != expectedShelfOffset)
        {
            throw new InvalidDataException($"Routed SS16-16 shelf offset mismatch. Expected {expectedShelfOffset}, actual {actualShelfOffset}.");
        }

        DataKernelReadTelemetry readTelemetry = session.GetAndResetReadTelemetry();
        PrintRead("ss16-16 routed read", readTelemetry);
        if (readTelemetry.ReadCallCount != 2 || readTelemetry.BytesRead != RouterLayout.Size + profile.ShelfExtentSize)
        {
            throw new InvalidDataException("Routed SS16-16 read shape should be one root-router read plus one shelf read.");
        }

        Scalar16Scalar16ReadOnly shelf = new(shelfBytes, profile);
        ValidateScalar16Scalar16Sorted(shelf);
        if (shelf.ItemCount != itemCount)
        {
            throw new InvalidDataException($"Routed SS16-16 shelf item count mismatch. Expected {itemCount}, actual {shelf.ItemCount}.");
        }

        if (!shelf.Contains(expectedKeyHigh, expectedKeyLow, expectedIdentityHigh, expectedIdentityLow))
        {
            throw new InvalidDataException("Routed SS16-16 shelf does not contain the expected tuple.");
        }

        int seekSlot = shelf.LowerBoundKey(0, 128);
        if (seekSlot != 128 || shelf.ReadIdentityHighAt(seekSlot) != 0 || shelf.ReadIdentityLowAt(seekSlot) != 128)
        {
            throw new InvalidDataException("Routed SS16-16 seek-one validation failed.");
        }

        const int rangeStart = 128;
        const int rangeLength = 64;
        ulong[] identityHighs = new ulong[rangeLength];
        ulong[] identityLows = new ulong[rangeLength];
        int copied = shelf.CopyIdentitiesInKeyRange(0, rangeStart, 0, rangeStart + rangeLength - 1, identityHighs, identityLows);
        long rangeSum = 0;
        for (int i = 0; i < copied; i++)
        {
            rangeSum += (long)identityLows[i];
            if (identityHighs[i] != 0)
            {
                throw new InvalidDataException("Routed SS16-16 range-copy returned an unexpected high identity half.");
            }
        }

        long expectedSum = ((long)rangeStart + rangeStart + rangeLength - 1) * rangeLength / 2;
        if (copied != rangeLength || rangeSum != expectedSum || identityLows[0] != (ulong)rangeStart || identityLows[rangeLength - 1] != (ulong)(rangeStart + rangeLength - 1))
        {
            throw new InvalidDataException("Routed SS16-16 range-copy validation failed.");
        }
    }


    /// <summary>
    /// Validates a routed `SS8-8` shelf read through a caller-provided root-router prefix byte.<br/>
    /// This split-focused helper checks route target, read telemetry, sorted shelf shape, item count, and one required tuple without assuming low unsigned keys.<br/>
    /// </summary>
    /// <param name="session">The file session used for routed reads.</param>
    /// <param name="rootOffset">The persisted root-router offset.</param>
    /// <param name="prefixByte">The root-router prefix byte to route through.</param>
    /// <param name="expectedShelfOffset">The expected routed shelf offset.</param>
    /// <param name="profile">The `SS8-8` shelf profile.</param>
    /// <param name="expectedItemCount">The expected number of shelf tuples.</param>
    /// <param name="expectedKey">The encoded key that must exist in the shelf.</param>
    /// <param name="expectedIdentity">The encoded identity that must exist in the shelf.</param>
    /// <exception cref="InvalidDataException">Thrown when routed shelf behavior or telemetry does not match expectations.</exception>
    private static void ValidateRoutedScalar8Scalar8ShelfShape(
        LibraDexFileSession session,
        long rootOffset,
        byte prefixByte,
        long expectedShelfOffset,
        Scalar8Scalar8Profile profile,
        int expectedItemCount,
        ulong expectedKey,
        ulong expectedIdentity)
    {
        byte[] shelfBytes = new byte[profile.ShelfExtentSize];
        long actualShelfOffset = session.ReadRoutedScalar8Scalar8Shelf(rootOffset, prefixByte, profile, shelfBytes);
        if (actualShelfOffset != expectedShelfOffset)
        {
            throw new InvalidDataException($"Routed SS8-8 shelf offset mismatch for prefix 0x{prefixByte:X2}. Expected {expectedShelfOffset}, actual {actualShelfOffset}.");
        }

        DataKernelReadTelemetry readTelemetry = session.GetAndResetReadTelemetry();
        PrintRead($"ss8-8 routed read 0x{prefixByte:X2}", readTelemetry);
        bool coldRead = readTelemetry.ReadCallCount == 2 && readTelemetry.BytesRead == RouterLayout.Size + profile.ShelfExtentSize;
        bool warmRead = readTelemetry.ReadCallCount == 1 && readTelemetry.BytesRead == profile.ShelfExtentSize;
        if (!coldRead && !warmRead)
        {
            throw new InvalidDataException("Routed SS8-8 split read shape should be a cold root-router plus shelf read or a warm shelf-only read.");
        }

        Scalar8Scalar8ReadOnly shelf = new(shelfBytes, profile);
        ValidateScalar8Scalar8Sorted(shelf);
        if (shelf.ItemCount != expectedItemCount)
        {
            throw new InvalidDataException($"Routed SS8-8 shelf item count mismatch for prefix 0x{prefixByte:X2}. Expected {expectedItemCount}, actual {shelf.ItemCount}.");
        }

        if (!shelf.Contains(expectedKey, expectedIdentity))
        {
            throw new InvalidDataException($"Routed SS8-8 shelf for prefix 0x{prefixByte:X2} does not contain the expected tuple.");
        }
    }


    /// <summary>
    /// Validates a routed `SS16-8` shelf read through a caller-provided root-router prefix byte.<br/>
    /// This split-focused helper checks route target, read telemetry, sorted widened-key shelf shape, item count, and one required tuple.<br/>
    /// </summary>
    /// <param name="session">The file session used for routed reads.</param>
    /// <param name="rootOffset">The persisted root-router offset.</param>
    /// <param name="prefixByte">The root-router prefix byte to route through.</param>
    /// <param name="expectedShelfOffset">The expected routed shelf offset.</param>
    /// <param name="profile">The `SS16-8` shelf profile.</param>
    /// <param name="expectedItemCount">The expected number of shelf tuples.</param>
    /// <param name="expectedKeyHigh">The encoded high key half that must exist in the shelf.</param>
    /// <param name="expectedKeyLow">The encoded low key half that must exist in the shelf.</param>
    /// <param name="expectedIdentity">The encoded identity that must exist in the shelf.</param>
    /// <exception cref="InvalidDataException">Thrown when routed shelf behavior or telemetry does not match expectations.</exception>
    private static void ValidateRoutedScalar16Scalar8ShelfShape(
        LibraDexFileSession session,
        long rootOffset,
        byte prefixByte,
        long expectedShelfOffset,
        Scalar16Scalar8Profile profile,
        int expectedItemCount,
        ulong expectedKeyHigh,
        ulong expectedKeyLow,
        ulong expectedIdentity)
    {
        byte[] shelfBytes = new byte[profile.ShelfExtentSize];
        long actualShelfOffset = session.ReadRoutedScalar16Scalar8Shelf(rootOffset, prefixByte, profile, shelfBytes);
        if (actualShelfOffset != expectedShelfOffset)
        {
            throw new InvalidDataException($"Routed SS16-8 shelf offset mismatch for prefix 0x{prefixByte:X2}. Expected {expectedShelfOffset}, actual {actualShelfOffset}.");
        }

        DataKernelReadTelemetry readTelemetry = session.GetAndResetReadTelemetry();
        PrintRead($"ss16-8 routed read 0x{prefixByte:X2}", readTelemetry);
        bool coldRead = readTelemetry.ReadCallCount == 2 && readTelemetry.BytesRead == RouterLayout.Size + profile.ShelfExtentSize;
        bool warmRead = readTelemetry.ReadCallCount == 1 && readTelemetry.BytesRead == profile.ShelfExtentSize;
        if (!coldRead && !warmRead)
        {
            throw new InvalidDataException("Routed SS16-8 split read shape should be a cold root-router plus shelf read or a warm shelf-only read.");
        }

        Scalar16Scalar8ReadOnly shelf = new(shelfBytes, profile);
        ValidateScalar16Scalar8Sorted(shelf);
        if (shelf.ItemCount != expectedItemCount)
        {
            throw new InvalidDataException($"Routed SS16-8 shelf item count mismatch for prefix 0x{prefixByte:X2}. Expected {expectedItemCount}, actual {shelf.ItemCount}.");
        }

        if (!shelf.Contains(expectedKeyHigh, expectedKeyLow, expectedIdentity))
        {
            throw new InvalidDataException($"Routed SS16-8 shelf for prefix 0x{prefixByte:X2} does not contain the expected tuple.");
        }
    }


    /// <summary>
    /// Validates a routed `FS32-8` shelf read through a caller-provided root-router prefix byte.<br/>
    /// This split-focused helper checks route target, read telemetry, sorted fixed-key shelf shape, item count, and one required tuple.<br/>
    /// </summary>
    /// <param name="session">The file session used for routed reads.</param>
    /// <param name="rootOffset">The persisted root-router offset.</param>
    /// <param name="prefixByte">The root-router prefix byte to route through.</param>
    /// <param name="expectedShelfOffset">The expected routed shelf offset.</param>
    /// <param name="profile">The `FS32-8` shelf profile.</param>
    /// <param name="expectedItemCount">The expected number of shelf tuples.</param>
    /// <param name="expectedKey0">The first encoded key lane that must exist in the shelf.</param>
    /// <param name="expectedKey1">The second encoded key lane that must exist in the shelf.</param>
    /// <param name="expectedKey2">The third encoded key lane that must exist in the shelf.</param>
    /// <param name="expectedKey3">The fourth encoded key lane that must exist in the shelf.</param>
    /// <param name="expectedIdentity">The encoded identity that must exist in the shelf.</param>
    /// <exception cref="InvalidDataException">Thrown when routed shelf behavior or telemetry does not match expectations.</exception>
    private static void ValidateRoutedFixed32Scalar8ShelfShape(
        LibraDexFileSession session,
        long rootOffset,
        byte prefixByte,
        long expectedShelfOffset,
        Fixed32Scalar8Profile profile,
        int expectedItemCount,
        ulong expectedKey0,
        ulong expectedKey1,
        ulong expectedKey2,
        ulong expectedKey3,
        ulong expectedIdentity)
    {
        byte[] shelfBytes = new byte[profile.ShelfExtentSize];
        long actualShelfOffset = session.ReadRoutedFixed32Scalar8Shelf(rootOffset, prefixByte, profile, shelfBytes);
        if (actualShelfOffset != expectedShelfOffset)
        {
            throw new InvalidDataException($"Routed FS32-8 shelf offset mismatch for prefix 0x{prefixByte:X2}. Expected {expectedShelfOffset}, actual {actualShelfOffset}.");
        }

        DataKernelReadTelemetry readTelemetry = session.GetAndResetReadTelemetry();
        PrintRead($"fs32-8 routed read 0x{prefixByte:X2}", readTelemetry);
        bool coldRead = readTelemetry.ReadCallCount == 2 && readTelemetry.BytesRead == RouterLayout.Size + profile.ShelfExtentSize;
        bool warmRead = readTelemetry.ReadCallCount == 1 && readTelemetry.BytesRead == profile.ShelfExtentSize;
        if (!coldRead && !warmRead)
        {
            throw new InvalidDataException("Routed FS32-8 split read shape should be a cold root-router plus shelf read or a warm shelf-only read.");
        }

        Fixed32Scalar8ReadOnly shelf = new(shelfBytes, profile);
        ValidateFixed32Scalar8Sorted(shelf);
        if (shelf.ItemCount != expectedItemCount)
        {
            throw new InvalidDataException($"Routed FS32-8 shelf item count mismatch for prefix 0x{prefixByte:X2}. Expected {expectedItemCount}, actual {shelf.ItemCount}.");
        }

        if (!shelf.Contains(expectedKey0, expectedKey1, expectedKey2, expectedKey3, expectedIdentity))
        {
            throw new InvalidDataException($"Routed FS32-8 shelf for prefix 0x{prefixByte:X2} does not contain the expected tuple.");
        }
    }


    /// <summary>
    /// Validates a routed `SS8-16` shelf read through a caller-provided root-router prefix byte.<br/>
    /// This split-focused helper checks route target, read telemetry, sorted widened-identity shelf shape, item count, and one required tuple.<br/>
    /// </summary>
    /// <param name="session">The file session used for routed reads.</param>
    /// <param name="rootOffset">The persisted root-router offset.</param>
    /// <param name="prefixByte">The root-router prefix byte to route through.</param>
    /// <param name="expectedShelfOffset">The expected routed shelf offset.</param>
    /// <param name="profile">The `SS8-16` shelf profile.</param>
    /// <param name="expectedItemCount">The expected number of shelf tuples.</param>
    /// <param name="expectedKey">The encoded key that must exist in the shelf.</param>
    /// <param name="expectedIdentityHigh">The encoded high identity half that must exist in the shelf.</param>
    /// <param name="expectedIdentityLow">The encoded low identity half that must exist in the shelf.</param>
    /// <exception cref="InvalidDataException">Thrown when routed shelf behavior or telemetry does not match expectations.</exception>
    private static void ValidateRoutedScalar8Scalar16ShelfShape(
        LibraDexFileSession session,
        long rootOffset,
        byte prefixByte,
        long expectedShelfOffset,
        Scalar8Scalar16Profile profile,
        int expectedItemCount,
        ulong expectedKey,
        ulong expectedIdentityHigh,
        ulong expectedIdentityLow)
    {
        byte[] shelfBytes = new byte[profile.ShelfExtentSize];
        long actualShelfOffset = session.ReadRoutedScalar8Scalar16Shelf(rootOffset, prefixByte, profile, shelfBytes);
        if (actualShelfOffset != expectedShelfOffset)
        {
            throw new InvalidDataException($"Routed SS8-16 shelf offset mismatch for prefix 0x{prefixByte:X2}. Expected {expectedShelfOffset}, actual {actualShelfOffset}.");
        }

        DataKernelReadTelemetry readTelemetry = session.GetAndResetReadTelemetry();
        PrintRead($"ss8-16 routed read 0x{prefixByte:X2}", readTelemetry);
        bool coldRead = readTelemetry.ReadCallCount == 2 && readTelemetry.BytesRead == RouterLayout.Size + profile.ShelfExtentSize;
        bool warmRead = readTelemetry.ReadCallCount == 1 && readTelemetry.BytesRead == profile.ShelfExtentSize;
        if (!coldRead && !warmRead)
        {
            throw new InvalidDataException("Routed SS8-16 split read shape should be a cold root-router plus shelf read or a warm shelf-only read.");
        }

        Scalar8Scalar16ReadOnly shelf = new(shelfBytes, profile);
        ValidateScalar8Scalar16Sorted(shelf);
        if (shelf.ItemCount != expectedItemCount)
        {
            throw new InvalidDataException($"Routed SS8-16 shelf item count mismatch for prefix 0x{prefixByte:X2}. Expected {expectedItemCount}, actual {shelf.ItemCount}.");
        }

        if (!shelf.Contains(expectedKey, expectedIdentityHigh, expectedIdentityLow))
        {
            throw new InvalidDataException($"Routed SS8-16 shelf for prefix 0x{prefixByte:X2} does not contain the expected tuple.");
        }
    }


    /// <summary>
    /// Validates a routed `SS16-16` shelf read through a caller-provided root-router prefix byte.<br/>
    /// This split-focused helper checks route target, read telemetry, sorted widened-key and widened-identity shelf shape, item count, and one required tuple.<br/>
    /// </summary>
    /// <param name="session">The file session used for routed reads.</param>
    /// <param name="rootOffset">The persisted root-router offset.</param>
    /// <param name="prefixByte">The root-router prefix byte to route through.</param>
    /// <param name="expectedShelfOffset">The expected routed shelf offset.</param>
    /// <param name="profile">The `SS16-16` shelf profile.</param>
    /// <param name="expectedItemCount">The expected number of shelf tuples.</param>
    /// <param name="expectedKeyHigh">The encoded high key half that must exist in the shelf.</param>
    /// <param name="expectedKeyLow">The encoded low key half that must exist in the shelf.</param>
    /// <param name="expectedIdentityHigh">The encoded high identity half that must exist in the shelf.</param>
    /// <param name="expectedIdentityLow">The encoded low identity half that must exist in the shelf.</param>
    /// <exception cref="InvalidDataException">Thrown when routed shelf behavior or telemetry does not match expectations.</exception>
    private static void ValidateRoutedScalar16Scalar16ShelfShape(
        LibraDexFileSession session,
        long rootOffset,
        byte prefixByte,
        long expectedShelfOffset,
        Scalar16Scalar16Profile profile,
        int expectedItemCount,
        ulong expectedKeyHigh,
        ulong expectedKeyLow,
        ulong expectedIdentityHigh,
        ulong expectedIdentityLow)
    {
        byte[] shelfBytes = new byte[profile.ShelfExtentSize];
        long actualShelfOffset = session.ReadRoutedScalar16Scalar16Shelf(rootOffset, prefixByte, profile, shelfBytes);
        if (actualShelfOffset != expectedShelfOffset)
        {
            throw new InvalidDataException($"Routed SS16-16 shelf offset mismatch for prefix 0x{prefixByte:X2}. Expected {expectedShelfOffset}, actual {actualShelfOffset}.");
        }

        DataKernelReadTelemetry readTelemetry = session.GetAndResetReadTelemetry();
        PrintRead($"ss16-16 routed read 0x{prefixByte:X2}", readTelemetry);
        bool coldRead = readTelemetry.ReadCallCount == 2 && readTelemetry.BytesRead == RouterLayout.Size + profile.ShelfExtentSize;
        bool warmRead = readTelemetry.ReadCallCount == 1 && readTelemetry.BytesRead == profile.ShelfExtentSize;
        if (!coldRead && !warmRead)
        {
            throw new InvalidDataException("Routed SS16-16 split read shape should be a cold root-router plus shelf read or a warm shelf-only read.");
        }

        Scalar16Scalar16ReadOnly shelf = new(shelfBytes, profile);
        ValidateScalar16Scalar16Sorted(shelf);
        if (shelf.ItemCount != expectedItemCount)
        {
            throw new InvalidDataException($"Routed SS16-16 shelf item count mismatch for prefix 0x{prefixByte:X2}. Expected {expectedItemCount}, actual {shelf.ItemCount}.");
        }

        if (!shelf.Contains(expectedKeyHigh, expectedKeyLow, expectedIdentityHigh, expectedIdentityLow))
        {
            throw new InvalidDataException($"Routed SS16-16 shelf for prefix 0x{prefixByte:X2} does not contain the expected tuple.");
        }
    }


    /// <summary>
    /// Validates read telemetry for a two-level routed shelf read.<br/>
    /// Legal shapes include cold root+child+shelf, root-warm child+shelf, warm shelf-only, and router-arena first-touch child+shelf reads.<br/>
    /// </summary>
    /// <param name="shapeName">The compact shape label used in failure messages.</param>
    /// <param name="readTelemetry">The captured read telemetry for the routed shelf read.</param>
    /// <param name="shelfExtentSize">The profiled shelf extent size.</param>
    /// <exception cref="InvalidDataException">Thrown when the read telemetry does not match a supported routed read shape.</exception>
    private static void ValidateTwoLevelRoutedReadTelemetry(
        string shapeName,
        DataKernelReadTelemetry readTelemetry,
        int shelfExtentSize)
    {
        int routerArenaReadSize = Math.Min(shelfExtentSize, 64 * 1024);
        bool coldRead = readTelemetry.ReadCallCount == 3 && readTelemetry.BytesRead == (RouterLayout.Size * 2) + shelfExtentSize;
        bool rootWarmRead = readTelemetry.ReadCallCount == 2 && readTelemetry.BytesRead == RouterLayout.Size + shelfExtentSize;
        bool allWarmRead = readTelemetry.ReadCallCount == 1 && readTelemetry.BytesRead == shelfExtentSize;
        bool arenaFirstTouchRead = readTelemetry.ReadCallCount == 2 && readTelemetry.BytesRead == routerArenaReadSize + shelfExtentSize;
        bool rootWarmArenaDiscoveryRead = readTelemetry.ReadCallCount == 3 && readTelemetry.BytesRead == RouterLayout.Size + routerArenaReadSize + shelfExtentSize;
        bool coldArenaDiscoveryRead = readTelemetry.ReadCallCount == 4 && readTelemetry.BytesRead == (RouterLayout.Size * 2) + routerArenaReadSize + shelfExtentSize;
        if (!coldRead && !rootWarmRead && !allWarmRead && !arenaFirstTouchRead && !rootWarmArenaDiscoveryRead && !coldArenaDiscoveryRead)
        {
            throw new InvalidDataException($"Two-level routed {shapeName} read shape should be cold root+child+shelf, root-warm child+shelf, warm shelf-only, router-arena first-touch child+shelf, root-warm router-arena discovery+shelf, or cold router-arena discovery+shelf.");
        }
    }


    /// <summary>
    /// Validates a two-level routed `SS8-8` shelf read through root and transformed child routers.<br/>
    /// This checks parent stability, child route target, read telemetry, sorted shelf shape, item count, and one required tuple.<br/>
    /// </summary>
    /// <param name="session">The file session used for routed reads.</param>
    /// <param name="rootOffset">The persisted root-router offset.</param>
    /// <param name="rootPrefixByte">The root-router prefix byte to route through.</param>
    /// <param name="childPrefixByte">The child-router prefix byte to route through.</param>
    /// <param name="expectedChildRouterOffset">The expected transformed child-router offset.</param>
    /// <param name="expectedShelfOffset">The expected routed shelf offset.</param>
    /// <param name="profile">The `SS8-8` shelf profile.</param>
    /// <param name="expectedItemCount">The expected number of shelf tuples.</param>
    /// <param name="expectedKey">The encoded key that must exist in the shelf.</param>
    /// <param name="expectedIdentity">The encoded identity that must exist in the shelf.</param>
    /// <exception cref="InvalidDataException">Thrown when routed shelf behavior or telemetry does not match expectations.</exception>
    private static void ValidateTwoLevelRoutedScalar8Scalar8ShelfShape(
        LibraDexFileSession session,
        long rootOffset,
        byte rootPrefixByte,
        byte childPrefixByte,
        long expectedChildRouterOffset,
        long expectedShelfOffset,
        Scalar8Scalar8Profile profile,
        int expectedItemCount,
        ulong expectedKey,
        ulong expectedIdentity)
    {
        byte[] shelfBytes = new byte[profile.ShelfExtentSize];
        (long childRouterOffset, long shelfOffset) = session.ReadTwoLevelRoutedScalar8Scalar8Shelf(
            rootOffset,
            rootPrefixByte,
            childPrefixByte,
            profile,
            shelfBytes);
        if (childRouterOffset != expectedChildRouterOffset || shelfOffset != expectedShelfOffset)
        {
            throw new InvalidDataException($"Two-level routed SS8-8 target mismatch for prefixes 0x{rootPrefixByte:X2}/0x{childPrefixByte:X2}.");
        }

        DataKernelReadTelemetry readTelemetry = session.GetAndResetReadTelemetry();
        PrintRead($"ss8-8 two-level routed read 0x{rootPrefixByte:X2}/0x{childPrefixByte:X2}", readTelemetry);
        ValidateTwoLevelRoutedReadTelemetry("SS8-8", readTelemetry, profile.ShelfExtentSize);

        Scalar8Scalar8ReadOnly shelf = new(shelfBytes, profile);
        ValidateScalar8Scalar8Sorted(shelf);
        if (shelf.ItemCount != expectedItemCount)
        {
            throw new InvalidDataException($"Two-level routed SS8-8 shelf item count mismatch. Expected {expectedItemCount}, actual {shelf.ItemCount}.");
        }

        if (!shelf.Contains(expectedKey, expectedIdentity))
        {
            throw new InvalidDataException("Two-level routed SS8-8 shelf does not contain the expected tuple.");
        }
    }


    /// <summary>
    /// Validates a two-level routed `SS16-8` shelf read through root and transformed child routers.<br/>
    /// This checks parent stability, child route target, read telemetry, sorted widened-key shelf shape, item count, and one required tuple.<br/>
    /// </summary>
    /// <param name="session">The file session used for routed reads.</param>
    /// <param name="rootOffset">The persisted root-router offset.</param>
    /// <param name="rootPrefixByte">The root-router prefix byte to route through.</param>
    /// <param name="childPrefixByte">The child-router prefix byte to route through.</param>
    /// <param name="expectedChildRouterOffset">The expected transformed child-router offset.</param>
    /// <param name="expectedShelfOffset">The expected routed shelf offset.</param>
    /// <param name="profile">The `SS16-8` shelf profile.</param>
    /// <param name="expectedItemCount">The expected number of shelf tuples.</param>
    /// <param name="expectedKeyHigh">The encoded high key half that must exist in the shelf.</param>
    /// <param name="expectedKeyLow">The encoded low key half that must exist in the shelf.</param>
    /// <param name="expectedIdentity">The encoded identity that must exist in the shelf.</param>
    /// <exception cref="InvalidDataException">Thrown when routed shelf behavior or telemetry does not match expectations.</exception>
    private static void ValidateTwoLevelRoutedScalar16Scalar8ShelfShape(
        LibraDexFileSession session,
        long rootOffset,
        byte rootPrefixByte,
        byte childPrefixByte,
        long expectedChildRouterOffset,
        long expectedShelfOffset,
        Scalar16Scalar8Profile profile,
        int expectedItemCount,
        ulong expectedKeyHigh,
        ulong expectedKeyLow,
        ulong expectedIdentity)
    {
        byte[] shelfBytes = new byte[profile.ShelfExtentSize];
        (long childRouterOffset, long shelfOffset) = session.ReadTwoLevelRoutedScalar16Scalar8Shelf(
            rootOffset,
            rootPrefixByte,
            childPrefixByte,
            profile,
            shelfBytes);
        if (childRouterOffset != expectedChildRouterOffset || shelfOffset != expectedShelfOffset)
        {
            throw new InvalidDataException($"Two-level routed SS16-8 target mismatch for prefixes 0x{rootPrefixByte:X2}/0x{childPrefixByte:X2}.");
        }

        DataKernelReadTelemetry readTelemetry = session.GetAndResetReadTelemetry();
        PrintRead($"ss16-8 two-level routed read 0x{rootPrefixByte:X2}/0x{childPrefixByte:X2}", readTelemetry);
        ValidateTwoLevelRoutedReadTelemetry("SS16-8", readTelemetry, profile.ShelfExtentSize);

        Scalar16Scalar8ReadOnly shelf = new(shelfBytes, profile);
        ValidateScalar16Scalar8Sorted(shelf);
        if (shelf.ItemCount != expectedItemCount)
        {
            throw new InvalidDataException($"Two-level routed SS16-8 shelf item count mismatch. Expected {expectedItemCount}, actual {shelf.ItemCount}.");
        }

        if (!shelf.Contains(expectedKeyHigh, expectedKeyLow, expectedIdentity))
        {
            throw new InvalidDataException("Two-level routed SS16-8 shelf does not contain the expected tuple.");
        }
    }


    /// <summary>
    /// Validates a two-level routed `FS32-8` shelf read through root and transformed child routers.<br/>
    /// This checks parent stability, child route target, read telemetry, sorted fixed-key shelf shape, item count, and one required tuple.<br/>
    /// </summary>
    /// <param name="session">The file session used for routed reads.</param>
    /// <param name="rootOffset">The persisted root-router offset.</param>
    /// <param name="rootPrefixByte">The root-router prefix byte to route through.</param>
    /// <param name="childPrefixByte">The child-router prefix byte to route through.</param>
    /// <param name="expectedChildRouterOffset">The expected transformed child-router offset.</param>
    /// <param name="expectedShelfOffset">The expected routed shelf offset.</param>
    /// <param name="profile">The `FS32-8` shelf profile.</param>
    /// <param name="expectedItemCount">The expected number of shelf tuples.</param>
    /// <param name="expectedKey0">The first encoded key lane that must exist in the shelf.</param>
    /// <param name="expectedKey1">The second encoded key lane that must exist in the shelf.</param>
    /// <param name="expectedKey2">The third encoded key lane that must exist in the shelf.</param>
    /// <param name="expectedKey3">The fourth encoded key lane that must exist in the shelf.</param>
    /// <param name="expectedIdentity">The encoded identity that must exist in the shelf.</param>
    /// <exception cref="InvalidDataException">Thrown when routed shelf behavior or telemetry does not match expectations.</exception>
    private static void ValidateTwoLevelRoutedFixed32Scalar8ShelfShape(
        LibraDexFileSession session,
        long rootOffset,
        byte rootPrefixByte,
        byte childPrefixByte,
        long expectedChildRouterOffset,
        long expectedShelfOffset,
        Fixed32Scalar8Profile profile,
        int expectedItemCount,
        ulong expectedKey0,
        ulong expectedKey1,
        ulong expectedKey2,
        ulong expectedKey3,
        ulong expectedIdentity)
    {
        byte[] shelfBytes = new byte[profile.ShelfExtentSize];
        (long childRouterOffset, long shelfOffset) = session.ReadTwoLevelRoutedFixed32Scalar8Shelf(
            rootOffset,
            rootPrefixByte,
            childPrefixByte,
            profile,
            shelfBytes);
        if (childRouterOffset != expectedChildRouterOffset || shelfOffset != expectedShelfOffset)
        {
            throw new InvalidDataException($"Two-level routed FS32-8 target mismatch for prefixes 0x{rootPrefixByte:X2}/0x{childPrefixByte:X2}.");
        }

        DataKernelReadTelemetry readTelemetry = session.GetAndResetReadTelemetry();
        PrintRead($"fs32-8 two-level routed read 0x{rootPrefixByte:X2}/0x{childPrefixByte:X2}", readTelemetry);
        ValidateTwoLevelRoutedReadTelemetry("FS32-8", readTelemetry, profile.ShelfExtentSize);

        Fixed32Scalar8ReadOnly shelf = new(shelfBytes, profile);
        ValidateFixed32Scalar8Sorted(shelf);
        if (shelf.ItemCount != expectedItemCount)
        {
            throw new InvalidDataException($"Two-level routed FS32-8 shelf item count mismatch. Expected {expectedItemCount}, actual {shelf.ItemCount}.");
        }

        if (!shelf.Contains(expectedKey0, expectedKey1, expectedKey2, expectedKey3, expectedIdentity))
        {
            throw new InvalidDataException("Two-level routed FS32-8 shelf does not contain the expected tuple.");
        }
    }


    /// <summary>
    /// Validates a two-level routed `SS8-16` shelf read through root and transformed child routers.<br/>
    /// This checks parent stability, child route target, read telemetry, sorted widened-identity shelf shape, item count, and one required tuple.<br/>
    /// </summary>
    /// <param name="session">The file session used for routed reads.</param>
    /// <param name="rootOffset">The persisted root-router offset.</param>
    /// <param name="rootPrefixByte">The root-router prefix byte to route through.</param>
    /// <param name="childPrefixByte">The child-router prefix byte to route through.</param>
    /// <param name="expectedChildRouterOffset">The expected transformed child-router offset.</param>
    /// <param name="expectedShelfOffset">The expected routed shelf offset.</param>
    /// <param name="profile">The `SS8-16` shelf profile.</param>
    /// <param name="expectedItemCount">The expected number of shelf tuples.</param>
    /// <param name="expectedKey">The encoded key that must exist in the shelf.</param>
    /// <param name="expectedIdentityHigh">The encoded high identity half that must exist in the shelf.</param>
    /// <param name="expectedIdentityLow">The encoded low identity half that must exist in the shelf.</param>
    /// <exception cref="InvalidDataException">Thrown when routed shelf behavior or telemetry does not match expectations.</exception>
    private static void ValidateTwoLevelRoutedScalar8Scalar16ShelfShape(
        LibraDexFileSession session,
        long rootOffset,
        byte rootPrefixByte,
        byte childPrefixByte,
        long expectedChildRouterOffset,
        long expectedShelfOffset,
        Scalar8Scalar16Profile profile,
        int expectedItemCount,
        ulong expectedKey,
        ulong expectedIdentityHigh,
        ulong expectedIdentityLow)
    {
        byte[] shelfBytes = new byte[profile.ShelfExtentSize];
        (long childRouterOffset, long shelfOffset) = session.ReadTwoLevelRoutedScalar8Scalar16Shelf(
            rootOffset,
            rootPrefixByte,
            childPrefixByte,
            profile,
            shelfBytes);
        if (childRouterOffset != expectedChildRouterOffset || shelfOffset != expectedShelfOffset)
        {
            throw new InvalidDataException($"Two-level routed SS8-16 target mismatch for prefixes 0x{rootPrefixByte:X2}/0x{childPrefixByte:X2}.");
        }

        DataKernelReadTelemetry readTelemetry = session.GetAndResetReadTelemetry();
        PrintRead($"ss8-16 two-level routed read 0x{rootPrefixByte:X2}/0x{childPrefixByte:X2}", readTelemetry);
        ValidateTwoLevelRoutedReadTelemetry("SS8-16", readTelemetry, profile.ShelfExtentSize);

        Scalar8Scalar16ReadOnly shelf = new(shelfBytes, profile);
        ValidateScalar8Scalar16Sorted(shelf);
        if (shelf.ItemCount != expectedItemCount)
        {
            throw new InvalidDataException($"Two-level routed SS8-16 shelf item count mismatch. Expected {expectedItemCount}, actual {shelf.ItemCount}.");
        }

        if (!shelf.Contains(expectedKey, expectedIdentityHigh, expectedIdentityLow))
        {
            throw new InvalidDataException("Two-level routed SS8-16 shelf does not contain the expected tuple.");
        }
    }


    /// <summary>
    /// Validates a two-level routed `SS16-16` shelf read through root and transformed child routers.<br/>
    /// This checks parent stability, child route target, read telemetry, sorted widened-key and widened-identity shelf shape, item count, and one required tuple.<br/>
    /// </summary>
    /// <param name="session">The file session used for routed reads.</param>
    /// <param name="rootOffset">The persisted root-router offset.</param>
    /// <param name="rootPrefixByte">The root-router prefix byte to route through.</param>
    /// <param name="childPrefixByte">The child-router prefix byte to route through.</param>
    /// <param name="expectedChildRouterOffset">The expected transformed child-router offset.</param>
    /// <param name="expectedShelfOffset">The expected routed shelf offset.</param>
    /// <param name="profile">The `SS16-16` shelf profile.</param>
    /// <param name="expectedItemCount">The expected number of shelf tuples.</param>
    /// <param name="expectedKeyHigh">The encoded high key half that must exist in the shelf.</param>
    /// <param name="expectedKeyLow">The encoded low key half that must exist in the shelf.</param>
    /// <param name="expectedIdentityHigh">The encoded high identity half that must exist in the shelf.</param>
    /// <param name="expectedIdentityLow">The encoded low identity half that must exist in the shelf.</param>
    /// <exception cref="InvalidDataException">Thrown when routed shelf behavior or telemetry does not match expectations.</exception>
    private static void ValidateTwoLevelRoutedScalar16Scalar16ShelfShape(
        LibraDexFileSession session,
        long rootOffset,
        byte rootPrefixByte,
        byte childPrefixByte,
        long expectedChildRouterOffset,
        long expectedShelfOffset,
        Scalar16Scalar16Profile profile,
        int expectedItemCount,
        ulong expectedKeyHigh,
        ulong expectedKeyLow,
        ulong expectedIdentityHigh,
        ulong expectedIdentityLow)
    {
        byte[] shelfBytes = new byte[profile.ShelfExtentSize];
        (long childRouterOffset, long shelfOffset) = session.ReadTwoLevelRoutedScalar16Scalar16Shelf(
            rootOffset,
            rootPrefixByte,
            childPrefixByte,
            profile,
            shelfBytes);
        if (childRouterOffset != expectedChildRouterOffset || shelfOffset != expectedShelfOffset)
        {
            throw new InvalidDataException($"Two-level routed SS16-16 target mismatch for prefixes 0x{rootPrefixByte:X2}/0x{childPrefixByte:X2}.");
        }

        DataKernelReadTelemetry readTelemetry = session.GetAndResetReadTelemetry();
        PrintRead($"ss16-16 two-level routed read 0x{rootPrefixByte:X2}/0x{childPrefixByte:X2}", readTelemetry);
        ValidateTwoLevelRoutedReadTelemetry("SS16-16", readTelemetry, profile.ShelfExtentSize);

        Scalar16Scalar16ReadOnly shelf = new(shelfBytes, profile);
        ValidateScalar16Scalar16Sorted(shelf);
        if (shelf.ItemCount != expectedItemCount)
        {
            throw new InvalidDataException($"Two-level routed SS16-16 shelf item count mismatch. Expected {expectedItemCount}, actual {shelf.ItemCount}.");
        }

        if (!shelf.Contains(expectedKeyHigh, expectedKeyLow, expectedIdentityHigh, expectedIdentityLow))
        {
            throw new InvalidDataException("Two-level routed SS16-16 shelf does not contain the expected tuple.");
        }
    }


    private static void ValidateScalar8Scalar8RandomInsertOrder(Scalar8Scalar8Profile profile)
    {
        byte[] bytes = new byte[profile.ShelfExtentSize];
        Scalar8Scalar8 shelf = new(bytes, profile);
        shelf.Initialize();
        Random random = new(8675309);
        for (int i = 0; i < 512; i++)
        {
            ulong key = (ulong)random.Next(0, 128);
            ulong identity = (ulong)i;
            Scalar8Scalar8InsertResult result = shelf.Insert(
                Scalar8Scalar8Layout.EncodeUnsignedScalar8(key),
                Scalar8Scalar8Layout.EncodeUnsignedScalar8(identity),
                allowDuplicateKeys: true);
            if (result != Scalar8Scalar8InsertResult.Inserted)
            {
                throw new InvalidDataException($"Expected random SS8-8 insert, got {result}.");
            }
        }

        ValidateScalar8Scalar8Sorted(shelf.AsReadOnly());
    }


    private static void ValidateScalar8Scalar8DuplicateBehavior(Scalar8Scalar8Profile profile)
    {
        byte[] bytes = new byte[profile.ShelfExtentSize];
        Scalar8Scalar8 shelf = new(bytes, profile);
        shelf.Initialize();
        ulong key = Scalar8Scalar8Layout.EncodeUnsignedScalar8(42);
        ulong identity = Scalar8Scalar8Layout.EncodeUnsignedScalar8(100);
        if (shelf.Insert(key, identity, allowDuplicateKeys: true) != Scalar8Scalar8InsertResult.Inserted)
        {
            throw new InvalidDataException("Expected first SS8-8 duplicate test insert to succeed.");
        }

        if (shelf.Insert(key, identity, allowDuplicateKeys: true) != Scalar8Scalar8InsertResult.AlreadyPresent)
        {
            throw new InvalidDataException("Expected existing SS8-8 tuple to be already present.");
        }

        if (shelf.ItemCount != 1)
        {
            throw new InvalidDataException("Already-present SS8-8 tuple mutated item count.");
        }

        if (shelf.Insert(key, Scalar8Scalar8Layout.EncodeUnsignedScalar8(99), allowDuplicateKeys: true) != Scalar8Scalar8InsertResult.Inserted)
        {
            throw new InvalidDataException("Expected non-unique SS8-8 duplicate key with distinct identity to insert.");
        }

        if (shelf.Insert(key, Scalar8Scalar8Layout.EncodeUnsignedScalar8(101), allowDuplicateKeys: false) != Scalar8Scalar8InsertResult.KeyConflict)
        {
            throw new InvalidDataException("Expected unique SS8-8 duplicate key to report key conflict.");
        }

        ValidateScalar8Scalar8Sorted(shelf.AsReadOnly());
    }


    private static void ValidateScalar8Scalar8FullBoundary(Scalar8Scalar8Profile profile)
    {
        byte[] bytes = new byte[profile.ShelfExtentSize];
        Scalar8Scalar8 shelf = new(bytes, profile);
        shelf.Initialize();
        for (int i = 0; i < profile.MaxItemCount; i++)
        {
            Scalar8Scalar8InsertResult result = shelf.Insert(
                Scalar8Scalar8Layout.EncodeUnsignedScalar8((ulong)i),
                Scalar8Scalar8Layout.EncodeUnsignedScalar8((ulong)i),
                allowDuplicateKeys: true);
            if (result != Scalar8Scalar8InsertResult.Inserted)
            {
                throw new InvalidDataException($"Expected SS8-8 fill insert at {i}, got {result}.");
            }
        }

        if (shelf.Insert(ulong.MaxValue, ulong.MaxValue, allowDuplicateKeys: true) != Scalar8Scalar8InsertResult.Full)
        {
            throw new InvalidDataException("Expected full SS8-8 shelf to report full.");
        }

        ValidateScalar8Scalar8Sorted(shelf.AsReadOnly());
    }


    private static void ValidateScalar8Scalar8Sorted(Scalar8Scalar8ReadOnly shelf)
    {
        if (!shelf.IsValid)
        {
            throw new InvalidDataException("SS8-8 shelf failed validation.");
        }

        ulong previousKey = 0;
        ulong previousIdentity = 0;
        for (int i = 0; i < shelf.ItemCount; i++)
        {
            ulong key = shelf.ReadKeyAt(i);
            ulong identity = shelf.ReadIdentityAt(i);
            if (i > 0 && (key < previousKey || (key == previousKey && identity < previousIdentity)))
            {
                throw new InvalidDataException("SS8-8 shelf is not sorted by (key, identity).");
            }

            previousKey = key;
            previousIdentity = identity;
        }
    }


    /// <summary>
    /// Measures one `SS8-8` shelf profile with precomputed input vectors.<br/>
    /// The input vectors keep insert order, lookup keys, and range starts stable across shelf sizes when item count is stable.<br/>
    /// Buffer allocation is intentionally outside the timed insert loop so insert mechanics are not polluted by repeated managed allocation cost.<br/>
    /// </summary>
    /// <param name="profile">The shelf profile under test.</param>
    /// <param name="itemCount">The number of logical items to fill in each measured shelf.</param>
    /// <param name="insertRuns">The number of full shelf insert passes per sample.</param>
    /// <param name="sampleRuns">The number of independent measurement samples to aggregate.</param>
    /// <param name="vectors">The precomputed workload vectors for the selected item count.</param>
    /// <returns>Aggregated performance measurements for the profile and workload.</returns>
    private static Scalar8Scalar8PerfResult MeasureScalar8Scalar8Profile(
        Scalar8Scalar8Profile profile,
        int itemCount,
        int insertRuns,
        int sampleRuns,
        Scalar8Scalar8PerfVectors vectors)
    {
        double[] sortedInsert = new double[sampleRuns];
        double[] reverseInsert = new double[sampleRuns];
        double[] randomInsert = new double[sampleRuns];
        double[] lowerBound = new double[sampleRuns];
        double[] range = new double[sampleRuns];
        double[] rangePerIdentity = new double[sampleRuns];
        double[] scoop = new double[sampleRuns];
        double[] scoopPerIdentity = new double[sampleRuns];
        long checksum = 0;

        byte[] insertBytes = new byte[profile.ShelfExtentSize];
        for (int sample = 0; sample < sampleRuns; sample++)
        {
            Scalar8Scalar8InsertPerfResult sorted = MeasureScalar8Scalar8Insert(profile, itemCount, insertRuns, vectors.SortedInsertOrder, insertBytes);
            Scalar8Scalar8InsertPerfResult reverse = MeasureScalar8Scalar8Insert(profile, itemCount, insertRuns, vectors.ReverseInsertOrder, insertBytes);
            Scalar8Scalar8InsertPerfResult random = MeasureScalar8Scalar8Insert(profile, itemCount, insertRuns, vectors.RandomInsertOrder, insertBytes);

            byte[] readBytes = new byte[profile.ShelfExtentSize];
            Scalar8Scalar8 shelf = new(readBytes, profile);
            shelf.Initialize();
            FillScalar8Scalar8Shelf(ref shelf, itemCount);
            Scalar8Scalar8ReadOnly readOnly = shelf.AsReadOnly();
            ValidateScalar8Scalar8Sorted(readOnly);

            Scalar8Scalar8LookupPerfResult lookupResult = MeasureScalar8Scalar8LowerBound(readOnly, vectors.LookupKeys);
            Scalar8Scalar8RangePerfResult rangeResult = MeasureScalar8Scalar8RangeWalk(readOnly, vectors.RangeStarts, vectors.RangeLength);
            Scalar8Scalar8RangePerfResult scoopResult = MeasureScalar8Scalar8IdentityScoop(readOnly, vectors.RangeStarts, vectors.RangeLength);

            sortedInsert[sample] = sorted.NsPerInsert;
            reverseInsert[sample] = reverse.NsPerInsert;
            randomInsert[sample] = random.NsPerInsert;
            lowerBound[sample] = lookupResult.NsPerLookup;
            range[sample] = rangeResult.NsPerRange;
            rangePerIdentity[sample] = rangeResult.NsPerIdentity;
            scoop[sample] = scoopResult.NsPerRange;
            scoopPerIdentity[sample] = scoopResult.NsPerIdentity;
            checksum ^= sorted.Checksum ^ reverse.Checksum ^ random.Checksum ^ lookupResult.Checksum ^ rangeResult.Checksum ^ scoopResult.Checksum;
        }

        return new Scalar8Scalar8PerfResult(
            profile.ShelfExtentSize,
            profile.MaxItemCount,
            itemCount,
            profile.UnusedTailBytes,
            ComputeStats(sortedInsert),
            ComputeStats(reverseInsert),
            ComputeStats(randomInsert),
            ComputeStats(lowerBound),
            ComputeStats(range),
            ComputeStats(rangePerIdentity),
            ComputeStats(scoop),
            ComputeStats(scoopPerIdentity),
            checksum);
    }


    /// <summary>
    /// Measures many `SS8-8` shelves stored end-to-end in one byte arena.<br/>
    /// This keeps the workload controlled while adding cache and slice-locality effects that a single hot shelf cannot show.<br/>
    /// The insert phase creates every shelf once per sample with the same random order vector, and read phases distribute lookups across shelves.<br/>
    /// </summary>
    /// <param name="profile">The shelf profile under test.</param>
    /// <param name="itemCount">The equal-work item count inserted into each shelf.</param>
    /// <param name="shelfCount">The number of shelves stored end-to-end in the arena.</param>
    /// <param name="sampleRuns">The number of independent samples to aggregate.</param>
    /// <param name="vectors">The shared equal-work vectors.</param>
    /// <returns>Aggregated multi-shelf performance measurements.</returns>
    private static Scalar8Scalar8MultiShelfPerfResult MeasureScalar8Scalar8MultiShelfProfile(
        Scalar8Scalar8Profile profile,
        int itemCount,
        int shelfCount,
        int sampleRuns,
        Scalar8Scalar8PerfVectors vectors)
    {
        double[] randomInsert = new double[sampleRuns];
        double[] lowerBound = new double[sampleRuns];
        double[] rangePerIdentity = new double[sampleRuns];
        double[] scoopPerIdentity = new double[sampleRuns];
        long checksum = 0;

        for (int sample = 0; sample < sampleRuns; sample++)
        {
            byte[] arena = new byte[profile.ShelfExtentSize * shelfCount];
            Scalar8Scalar8InsertPerfResult insert = MeasureScalar8Scalar8MultiShelfInsert(profile, itemCount, shelfCount, vectors.RandomInsertOrder, arena);
            Scalar8Scalar8LookupPerfResult lookup = MeasureScalar8Scalar8MultiShelfLowerBound(profile, shelfCount, vectors.LookupKeys, arena);
            Scalar8Scalar8RangePerfResult range = MeasureScalar8Scalar8MultiShelfRangeWalk(profile, shelfCount, vectors.RangeStarts, vectors.RangeLength, arena);
            Scalar8Scalar8RangePerfResult scoop = MeasureScalar8Scalar8MultiShelfIdentityScoop(profile, shelfCount, vectors.RangeStarts, vectors.RangeLength, arena);

            randomInsert[sample] = insert.NsPerInsert;
            lowerBound[sample] = lookup.NsPerLookup;
            rangePerIdentity[sample] = range.NsPerIdentity;
            scoopPerIdentity[sample] = scoop.NsPerIdentity;
            checksum ^= insert.Checksum ^ lookup.Checksum ^ range.Checksum ^ scoop.Checksum;
        }

        return new Scalar8Scalar8MultiShelfPerfResult(
            profile.ShelfExtentSize,
            profile.MaxItemCount,
            itemCount,
            shelfCount,
            (long)profile.ShelfExtentSize * shelfCount,
            ComputeStats(randomInsert),
            ComputeStats(lowerBound),
            ComputeStats(rangePerIdentity),
            ComputeStats(scoopPerIdentity),
            checksum);
    }


    /// <summary>
    /// Warms the size-sweep paths before measured samples are collected.<br/>
    /// This reduces first-use JIT and tiering noise in the reported breakpoint data.<br/>
    /// </summary>
    /// <param name="profiles">The shelf profiles included in the sweep.</param>
    /// <param name="itemCount">The controlled item count used by all profiles.</param>
    /// <param name="rangeLength">The largest range length used in the sweep.</param>
    private static void WarmUpScalar8Scalar8SizeSweep(ReadOnlySpan<Scalar8Scalar8Profile> profiles, int itemCount, int rangeLength)
    {
        Scalar8Scalar8PerfVectors vectors = CreateScalar8Scalar8PerfVectors(itemCount, 10_000, 1_000, rangeLength);
        for (int i = 0; i < profiles.Length; i++)
        {
            Scalar8Scalar8Profile profile = profiles[i];
            byte[] shelfBytes = CreateFilledScalar8Scalar8ShelfBytes(profile, itemCount);
            byte[] arena = CreateFilledScalar8Scalar8Arena(profile, itemCount, Math.Min(32, itemCount));
            Scalar8Scalar8ReadOnly shelf = new(shelfBytes, profile);
            _ = MeasureScalar8Scalar8LowerBound(shelf, vectors.LookupKeys);
            _ = MeasureScalar8Scalar8SeekOne(shelf, vectors.LookupKeys);
            _ = MeasureScalar8Scalar8RangeWalk(shelf, vectors.RangeStarts, rangeLength);
            _ = MeasureScalar8Scalar8KnownSlotRange(shelf, vectors.RangeStarts, rangeLength);
            _ = MeasureScalar8Scalar8MultiShelfLowerBound(profile, Math.Min(32, itemCount), vectors.LookupKeys, arena);
            _ = MeasureScalar8Scalar8MultiShelfRangeWalk(profile, Math.Min(32, itemCount), vectors.RangeStarts, rangeLength, arena);
        }
    }


    /// <summary>
    /// Creates one initialized shelf byte buffer filled with sorted `SS8-8` tuples.<br/>
    /// The buffer can be reused across read-only measurements because no read measurement mutates the shelf.<br/>
    /// </summary>
    /// <param name="profile">The shelf profile to create.</param>
    /// <param name="itemCount">The number of sorted tuples to insert.</param>
    /// <returns>A filled shelf byte buffer.</returns>
    private static byte[] CreateFilledScalar8Scalar8ShelfBytes(Scalar8Scalar8Profile profile, int itemCount)
    {
        byte[] bytes = new byte[profile.ShelfExtentSize];
        Scalar8Scalar8 shelf = new(bytes, profile);
        shelf.Initialize();
        FillScalar8Scalar8Shelf(ref shelf, itemCount);
        return bytes;
    }


    /// <summary>
    /// Creates a contiguous arena of initialized `SS8-8` shelves.<br/>
    /// The arena is used to expose distributed read locality effects across shelf sizes.<br/>
    /// </summary>
    /// <param name="profile">The shelf profile to create.</param>
    /// <param name="itemCount">The number of sorted tuples inserted into each shelf.</param>
    /// <param name="shelfCount">The number of shelf extents in the arena.</param>
    /// <returns>A contiguous byte arena containing initialized shelves.</returns>
    private static byte[] CreateFilledScalar8Scalar8Arena(Scalar8Scalar8Profile profile, int itemCount, int shelfCount)
    {
        byte[] arena = new byte[profile.ShelfExtentSize * shelfCount];
        for (int shelfIndex = 0; shelfIndex < shelfCount; shelfIndex++)
        {
            int offset = shelfIndex * profile.ShelfExtentSize;
            Scalar8Scalar8 shelf = new(arena.AsSpan(offset, profile.ShelfExtentSize), profile);
            shelf.Initialize();
            FillScalar8Scalar8Shelf(ref shelf, itemCount);
        }

        return arena;
    }


    /// <summary>
    /// Measures cached shelf-view reads for one profile and one range length.<br/>
    /// The result separates seek-only, seek-one, seek-range, known-slot range, and multi-shelf distributed costs.<br/>
    /// </summary>
    /// <param name="profile">The shelf profile under test.</param>
    /// <param name="itemCount">The controlled item count used in each shelf.</param>
    /// <param name="shelfCount">The number of shelves in the multi-shelf arena.</param>
    /// <param name="sampleRuns">The number of independent measurement samples.</param>
    /// <param name="vectors">The precomputed workload vectors.</param>
    /// <param name="singleShelfBytes">The initialized single-shelf byte buffer.</param>
    /// <param name="multiShelfArena">The initialized multi-shelf byte arena.</param>
    /// <returns>The aggregated read sweep result.</returns>
    private static Scalar8Scalar8ReadSweepResult MeasureScalar8Scalar8ReadSweepProfile(
        Scalar8Scalar8Profile profile,
        int itemCount,
        int shelfCount,
        int sampleRuns,
        Scalar8Scalar8PerfVectors vectors,
        byte[] singleShelfBytes,
        byte[] multiShelfArena)
    {
        double[] seekOnly = new double[sampleRuns];
        double[] seekOne = new double[sampleRuns];
        double[] seekRangePerIdentity = new double[sampleRuns];
        double[] knownRangePerIdentity = new double[sampleRuns];
        double[] multiSeekOnly = new double[sampleRuns];
        double[] multiSeekRangePerIdentity = new double[sampleRuns];
        long checksum = 0;

        for (int sample = 0; sample < sampleRuns; sample++)
        {
            Scalar8Scalar8ReadOnly shelf = new(singleShelfBytes, profile);
            Scalar8Scalar8LookupPerfResult seekOnlyResult = MeasureScalar8Scalar8LowerBound(shelf, vectors.LookupKeys);
            Scalar8Scalar8LookupPerfResult seekOneResult = MeasureScalar8Scalar8SeekOne(shelf, vectors.LookupKeys);
            Scalar8Scalar8RangePerfResult seekRangeResult = MeasureScalar8Scalar8RangeWalk(shelf, vectors.RangeStarts, vectors.RangeLength);
            Scalar8Scalar8RangePerfResult knownRangeResult = MeasureScalar8Scalar8KnownSlotRange(shelf, vectors.RangeStarts, vectors.RangeLength);
            Scalar8Scalar8LookupPerfResult multiSeekOnlyResult = MeasureScalar8Scalar8MultiShelfLowerBound(profile, shelfCount, vectors.LookupKeys, multiShelfArena);
            Scalar8Scalar8RangePerfResult multiSeekRangeResult = MeasureScalar8Scalar8MultiShelfRangeWalk(profile, shelfCount, vectors.RangeStarts, vectors.RangeLength, multiShelfArena);

            seekOnly[sample] = seekOnlyResult.NsPerLookup;
            seekOne[sample] = seekOneResult.NsPerLookup;
            seekRangePerIdentity[sample] = seekRangeResult.NsPerIdentity;
            knownRangePerIdentity[sample] = knownRangeResult.NsPerIdentity;
            multiSeekOnly[sample] = multiSeekOnlyResult.NsPerLookup;
            multiSeekRangePerIdentity[sample] = multiSeekRangeResult.NsPerIdentity;
            checksum ^= seekOnlyResult.Checksum ^ seekOneResult.Checksum ^ seekRangeResult.Checksum ^ knownRangeResult.Checksum ^ multiSeekOnlyResult.Checksum ^ multiSeekRangeResult.Checksum;
        }

        return new Scalar8Scalar8ReadSweepResult(
            profile.ShelfExtentSize,
            profile.MaxItemCount,
            itemCount,
            vectors.RangeLength,
            ComputeStats(seekOnly),
            ComputeStats(seekOne),
            ComputeStats(seekRangePerIdentity),
            ComputeStats(knownRangePerIdentity),
            ComputeStats(multiSeekOnly),
            ComputeStats(multiSeekRangePerIdentity),
            checksum);
    }


    /// <summary>
    /// Measures DataKernel file reads for one `SS8-8` shelf profile.<br/>
    /// The file is created with many shelf-sized extents, then reopened and read sequentially and by deterministic pseudo-random extent order.<br/>
    /// This does not bypass the operating-system file cache, so results represent the current DataKernel/OS/file-device stack rather than raw device-only timing.<br/>
    /// </summary>
    /// <param name="profile">The shelf profile under test.</param>
    /// <param name="requestedTotalBytes">The target file payload bytes for this profile.</param>
    /// <param name="runs">The number of read samples to collect.</param>
    /// <param name="directory">The directory where the temporary `.lbdx` file is written.</param>
    /// <returns>The aggregated DataKernel file-read sweep result.</returns>
    private static Scalar8Scalar8DataKernelReadSweepResult MeasureScalar8Scalar8DataKernelReadSweepProfile(
        Scalar8Scalar8Profile profile,
        long requestedTotalBytes,
        int runs,
        string directory)
    {
        string path = Path.Combine(directory, $"ss8-8-size-sweep-{profile.ShelfExtentSize}.lbdx");
        File.Delete(path);

        int shelfCount = Math.Max(1, checked((int)(requestedTotalBytes / profile.ShelfExtentSize)));
        long measuredBytes = (long)shelfCount * profile.ShelfExtentSize;
        DataKernelOptions options = new(
            AppendBufferSize: Math.Max(ValidationAppendBufferSize, profile.ShelfExtentSize),
            ReservedPrefixBytes: DefaultReservedPrefixBytes,
            FlushToDiskOnCommit: false,
            MaxCommitGapCoalesceBytes: 512);

        DataKernelCommitTelemetry createTelemetry;
        using (DataKernel createKernel = DataKernel.Open(path, FileMode.Create, options, DataKernelTelemetryOptions.EnabledOptions))
        {
            for (int shelfIndex = 0; shelfIndex < shelfCount; shelfIndex++)
            {
                RawDataReservation reservation = createKernel.Reserve(profile.ShelfExtentSize);
                Scalar8Scalar8 shelf = new(reservation.Span, profile);
                shelf.Initialize();
                FillScalar8Scalar8Shelf(ref shelf, Math.Min((int)profile.MaxItemCount, 256));
            }

            createTelemetry = createKernel.Commit();
        }

        double[] sequentialMiBs = new double[runs];
        double[] sequentialNsPerRead = new double[runs];
        double[] randomMiBs = new double[runs];
        double[] randomNsPerRead = new double[runs];
        long sequentialReadCalls = 0;
        long randomReadCalls = 0;
        long checksum = 0;

        using (DataKernel readKernel = DataKernel.Open(path, FileMode.Open, options, DataKernelTelemetryOptions.EnabledOptions))
        {
            byte[] buffer = new byte[profile.ShelfExtentSize];
            int randomStride = GetScalar8Scalar8CoprimeStride(shelfCount);
            for (int run = 0; run < runs; run++)
            {
                Stopwatch sequentialWatch = Stopwatch.StartNew();
                for (int i = 0; i < shelfCount; i++)
                {
                    long offset = DefaultReservedPrefixBytes + (long)i * profile.ShelfExtentSize;
                    readKernel.Read(offset, buffer);
                    checksum += buffer[0];
                }

                sequentialWatch.Stop();
                DataKernelReadTelemetry sequentialTelemetry = readKernel.GetAndResetReadTelemetry();
                sequentialReadCalls += sequentialTelemetry.ReadCallCount;
                sequentialMiBs[run] = (measuredBytes / 1024d / 1024d) / Math.Max(sequentialWatch.Elapsed.TotalSeconds, 0.000001d);
                sequentialNsPerRead[run] = (sequentialWatch.Elapsed.TotalSeconds * 1_000_000_000d) / shelfCount;

                Stopwatch randomWatch = Stopwatch.StartNew();
                for (int i = 0; i < shelfCount; i++)
                {
                    int shelfIndex = (int)((long)i * randomStride % shelfCount);
                    long offset = DefaultReservedPrefixBytes + (long)shelfIndex * profile.ShelfExtentSize;
                    readKernel.Read(offset, buffer);
                    checksum += buffer[0];
                }

                randomWatch.Stop();
                DataKernelReadTelemetry randomTelemetry = readKernel.GetAndResetReadTelemetry();
                randomReadCalls += randomTelemetry.ReadCallCount;
                randomMiBs[run] = (measuredBytes / 1024d / 1024d) / Math.Max(randomWatch.Elapsed.TotalSeconds, 0.000001d);
                randomNsPerRead[run] = (randomWatch.Elapsed.TotalSeconds * 1_000_000_000d) / shelfCount;
            }
        }

        return new Scalar8Scalar8DataKernelReadSweepResult(
            profile.ShelfExtentSize,
            profile.MaxItemCount,
            shelfCount,
            measuredBytes,
            createTelemetry.WriteCallCount,
            createTelemetry.BytesWritten,
            createTelemetry.SetLengthCallCount,
            sequentialReadCalls / runs,
            randomReadCalls / runs,
            ComputeStats(sequentialMiBs),
            ComputeStats(sequentialNsPerRead),
            ComputeStats(randomMiBs),
            ComputeStats(randomNsPerRead),
            checksum);
    }


    /// <summary>
    /// Measures creation of multiple shelves in one contiguous arena.<br/>
    /// The same order vector is reused for every shelf so timing changes reflect shelf profile size and memory layout, not workload changes.<br/>
    /// </summary>
    /// <param name="profile">The shelf profile under test.</param>
    /// <param name="itemCount">The number of items inserted into each shelf.</param>
    /// <param name="shelfCount">The number of shelves to create.</param>
    /// <param name="order">The exact key insertion order.</param>
    /// <param name="arena">The contiguous arena containing all shelf extents.</param>
    /// <returns>The measured nanoseconds per insert plus a checksum guard.</returns>
    private static Scalar8Scalar8InsertPerfResult MeasureScalar8Scalar8MultiShelfInsert(
        Scalar8Scalar8Profile profile,
        int itemCount,
        int shelfCount,
        int[] order,
        byte[] arena)
    {
        long checksum = 0;
        long insertCount = (long)itemCount * shelfCount;
        Stopwatch watch = Stopwatch.StartNew();
        for (int shelfIndex = 0; shelfIndex < shelfCount; shelfIndex++)
        {
            int offset = shelfIndex * profile.ShelfExtentSize;
            Scalar8Scalar8 shelf = new(arena.AsSpan(offset, profile.ShelfExtentSize), profile);
            shelf.Initialize();
            for (int i = 0; i < itemCount; i++)
            {
                int value = order[i];
                Scalar8Scalar8InsertResult result = shelf.Insert(
                    Scalar8Scalar8Layout.EncodeUnsignedScalar8((ulong)value),
                    Scalar8Scalar8Layout.EncodeUnsignedScalar8((ulong)value),
                    allowDuplicateKeys: true);
                if (result != Scalar8Scalar8InsertResult.Inserted)
                {
                    throw new InvalidDataException($"Expected SS8-8 multi-shelf insert, got {result}.");
                }
            }

            checksum += shelf.ItemCount;
        }

        watch.Stop();
        return new Scalar8Scalar8InsertPerfResult((watch.Elapsed.TotalSeconds * 1_000_000_000d) / insertCount, checksum);
    }


    /// <summary>
    /// Measures lower-bound lookups distributed across many shelves in one arena.<br/>
    /// The shelf index is deterministic and evenly distributed to avoid measuring one permanently hot shelf only.<br/>
    /// </summary>
    /// <param name="profile">The shelf profile under test.</param>
    /// <param name="shelfCount">The number of shelves in the arena.</param>
    /// <param name="lookupKeys">The logical key values to seek.</param>
    /// <param name="arena">The contiguous arena containing initialized shelves.</param>
    /// <returns>The measured nanoseconds per lookup plus a checksum guard.</returns>
    private static Scalar8Scalar8LookupPerfResult MeasureScalar8Scalar8MultiShelfLowerBound(
        Scalar8Scalar8Profile profile,
        int shelfCount,
        int[] lookupKeys,
        byte[] arena)
    {
        long checksum = 0;
        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 0; i < lookupKeys.Length; i++)
        {
            int shelfIndex = i % shelfCount;
            int offset = shelfIndex * profile.ShelfExtentSize;
            Scalar8Scalar8ReadOnly shelf = new(arena.AsSpan(offset, profile.ShelfExtentSize), profile);
            checksum += shelf.LowerBoundKey(Scalar8Scalar8Layout.EncodeUnsignedScalar8((ulong)lookupKeys[i]));
        }

        watch.Stop();
        return new Scalar8Scalar8LookupPerfResult((watch.Elapsed.TotalSeconds * 1_000_000_000d) / lookupKeys.Length, checksum);
    }


    /// <summary>
    /// Measures range walking distributed across many shelves in one arena.<br/>
    /// This keeps range start keys controlled while adding end-to-end shelf traversal locality effects.<br/>
    /// </summary>
    /// <param name="profile">The shelf profile under test.</param>
    /// <param name="shelfCount">The number of shelves in the arena.</param>
    /// <param name="rangeStarts">The logical range-start keys.</param>
    /// <param name="rangeLength">The number of identities to walk per range.</param>
    /// <param name="arena">The contiguous arena containing initialized shelves.</param>
    /// <returns>The measured nanoseconds per range and per identity plus a checksum guard.</returns>
    private static Scalar8Scalar8RangePerfResult MeasureScalar8Scalar8MultiShelfRangeWalk(
        Scalar8Scalar8Profile profile,
        int shelfCount,
        int[] rangeStarts,
        int rangeLength,
        byte[] arena)
    {
        long checksum = 0;
        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 0; i < rangeStarts.Length; i++)
        {
            int shelfIndex = i % shelfCount;
            int offset = shelfIndex * profile.ShelfExtentSize;
            Scalar8Scalar8ReadOnly shelf = new(arena.AsSpan(offset, profile.ShelfExtentSize), profile);
            int slot = shelf.LowerBoundKey(Scalar8Scalar8Layout.EncodeUnsignedScalar8((ulong)rangeStarts[i]));
            int end = slot + rangeLength;
            for (int j = slot; j < end; j++)
            {
                checksum += (long)shelf.ReadIdentityAt(j);
            }
        }

        watch.Stop();
        double totalNs = watch.Elapsed.TotalSeconds * 1_000_000_000d;
        long identityCount = (long)rangeStarts.Length * rangeLength;
        return new Scalar8Scalar8RangePerfResult(totalNs / rangeStarts.Length, totalNs / identityCount, checksum);
    }


    /// <summary>
    /// Measures identity scoop distributed across many shelves in one arena.<br/>
    /// This includes lower-bound, slot-indirected identity reads, and writes into a reusable caller buffer.<br/>
    /// </summary>
    /// <param name="profile">The shelf profile under test.</param>
    /// <param name="shelfCount">The number of shelves in the arena.</param>
    /// <param name="rangeStarts">The logical range-start keys.</param>
    /// <param name="rangeLength">The number of identities copied per range.</param>
    /// <param name="arena">The contiguous arena containing initialized shelves.</param>
    /// <returns>The measured nanoseconds per range and per identity plus a checksum guard.</returns>
    private static Scalar8Scalar8RangePerfResult MeasureScalar8Scalar8MultiShelfIdentityScoop(
        Scalar8Scalar8Profile profile,
        int shelfCount,
        int[] rangeStarts,
        int rangeLength,
        byte[] arena)
    {
        ulong[] buffer = new ulong[rangeLength];
        long checksum = 0;
        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 0; i < rangeStarts.Length; i++)
        {
            int shelfIndex = i % shelfCount;
            int offset = shelfIndex * profile.ShelfExtentSize;
            Scalar8Scalar8ReadOnly shelf = new(arena.AsSpan(offset, profile.ShelfExtentSize), profile);
            int slot = shelf.LowerBoundKey(Scalar8Scalar8Layout.EncodeUnsignedScalar8((ulong)rangeStarts[i]));
            for (int j = 0; j < rangeLength; j++)
            {
                ulong identity = shelf.ReadIdentityAt(slot + j);
                buffer[j] = identity;
                checksum += (long)identity;
            }
        }

        watch.Stop();
        double totalNs = watch.Elapsed.TotalSeconds * 1_000_000_000d;
        long identityCount = (long)rangeStarts.Length * rangeLength;
        return new Scalar8Scalar8RangePerfResult(totalNs / rangeStarts.Length, totalNs / identityCount, checksum);
    }


    private static void WarmUpScalar8Scalar8Perf(ReadOnlySpan<int> shelfSizes, int requestedRangeLength)
    {
        for (int sizeIndex = 0; sizeIndex < shelfSizes.Length; sizeIndex++)
        {
            Scalar8Scalar8Profile profile = Scalar8Scalar8Profile.Create(shelfSizes[sizeIndex]);
            int itemCount = profile.MaxItemCount;
            Scalar8Scalar8PerfVectors vectors = CreateScalar8Scalar8PerfVectors(itemCount, 10_000, 1_000, Math.Min(requestedRangeLength, itemCount));
            byte[] insertBytes = new byte[profile.ShelfExtentSize];
            _ = MeasureScalar8Scalar8Insert(profile, itemCount, 2, vectors.SortedInsertOrder, insertBytes);
            _ = MeasureScalar8Scalar8Insert(profile, itemCount, 2, vectors.ReverseInsertOrder, insertBytes);
            _ = MeasureScalar8Scalar8Insert(profile, itemCount, 2, vectors.RandomInsertOrder, insertBytes);

            byte[] bytes = new byte[profile.ShelfExtentSize];
            Scalar8Scalar8 shelf = new(bytes, profile);
            shelf.Initialize();
            FillScalar8Scalar8Shelf(ref shelf, itemCount);

            Scalar8Scalar8ReadOnly readOnly = shelf.AsReadOnly();
            _ = MeasureScalar8Scalar8LowerBound(readOnly, vectors.LookupKeys);
            _ = MeasureScalar8Scalar8RangeWalk(readOnly, vectors.RangeStarts, vectors.RangeLength);
            _ = MeasureScalar8Scalar8IdentityScoop(readOnly, vectors.RangeStarts, vectors.RangeLength);
        }
    }


    /// <summary>
    /// Measures repeated inserts using one reusable shelf buffer and one precomputed order vector.<br/>
    /// This isolates insert/search/slot-shift behavior from per-run byte-array allocation noise.<br/>
    /// </summary>
    /// <param name="profile">The shelf profile under test.</param>
    /// <param name="itemCount">The number of items to insert per run.</param>
    /// <param name="runs">The number of full insert runs to execute.</param>
    /// <param name="order">The exact key order to insert.</param>
    /// <param name="bytes">A reusable shelf byte buffer whose length matches the profile extent size.</param>
    /// <returns>The measured nanoseconds per insert plus a checksum guard.</returns>
    private static Scalar8Scalar8InsertPerfResult MeasureScalar8Scalar8Insert(Scalar8Scalar8Profile profile, int itemCount, int runs, int[] order, byte[] bytes)
    {
        long checksum = 0;
        long insertCount = (long)itemCount * runs;
        Stopwatch watch = Stopwatch.StartNew();
        for (int run = 0; run < runs; run++)
        {
            Scalar8Scalar8 shelf = new(bytes, profile);
            shelf.Initialize();
            for (int i = 0; i < itemCount; i++)
            {
                int value = order[i];
                Scalar8Scalar8InsertResult result = shelf.Insert(
                    Scalar8Scalar8Layout.EncodeUnsignedScalar8((ulong)value),
                    Scalar8Scalar8Layout.EncodeUnsignedScalar8((ulong)value),
                    allowDuplicateKeys: true);
                if (result != Scalar8Scalar8InsertResult.Inserted)
                {
                    throw new InvalidDataException($"Expected SS8-8 perf insert, got {result}.");
                }
            }

            checksum += shelf.ItemCount;
        }

        watch.Stop();
        double nsPerInsert = (watch.Elapsed.TotalSeconds * 1_000_000_000d) / insertCount;
        return new Scalar8Scalar8InsertPerfResult(nsPerInsert, checksum);
    }


    /// <summary>
    /// Creates the reusable workload vectors for a single `SS8-8` item count.<br/>
    /// Equal-work shelf-size comparisons pass the same instance shape to every profile so only shelf extent size changes.<br/>
    /// </summary>
    /// <param name="itemCount">The number of logical items in the measured shelf.</param>
    /// <param name="lookupIterations">The number of lower-bound lookup keys to create.</param>
    /// <param name="rangeIterations">The number of range-start keys to create.</param>
    /// <param name="rangeLength">The number of identities included in each range.</param>
    /// <returns>The precomputed workload vectors.</returns>
    private static Scalar8Scalar8PerfVectors CreateScalar8Scalar8PerfVectors(int itemCount, int lookupIterations, int rangeIterations, int rangeLength)
    {
        int maxStart = itemCount - rangeLength;
        int[] lookupKeys = new int[lookupIterations];
        int[] rangeStarts = new int[rangeIterations];
        for (int i = 0; i < lookupKeys.Length; i++)
        {
            lookupKeys[i] = i % itemCount;
        }

        for (int i = 0; i < rangeStarts.Length; i++)
        {
            rangeStarts[i] = maxStart == 0 ? 0 : i % (maxStart + 1);
        }

        return new Scalar8Scalar8PerfVectors(
            CreateScalar8Scalar8OrderVector(itemCount, Scalar8Scalar8InsertOrder.Sorted),
            CreateScalar8Scalar8OrderVector(itemCount, Scalar8Scalar8InsertOrder.Reverse),
            CreateScalar8Scalar8OrderVector(itemCount, Scalar8Scalar8InsertOrder.Random),
            lookupKeys,
            rangeStarts,
            rangeLength);
    }


    /// <summary>
    /// Creates one deterministic insert order vector.<br/>
    /// The random order uses a coprime stride permutation so every key appears once without a shuffle allocation inside measurement.<br/>
    /// </summary>
    /// <param name="count">The number of insert positions to generate.</param>
    /// <param name="order">The requested insert-order family.</param>
    /// <returns>A key order vector containing each key from zero to count minus one exactly once.</returns>
    private static int[] CreateScalar8Scalar8OrderVector(int count, Scalar8Scalar8InsertOrder order)
    {
        int[] values = new int[count];
        int stride = order == Scalar8Scalar8InsertOrder.Random ? GetScalar8Scalar8CoprimeStride(count) : 1;
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = order switch
            {
                Scalar8Scalar8InsertOrder.Sorted => i,
                Scalar8Scalar8InsertOrder.Reverse => count - i - 1,
                _ => (int)((long)i * stride % count)
            };
        }

        return values;
    }


    private static int GetScalar8Scalar8CoprimeStride(int count)
    {
        int stride = Math.Max(1, count / 2);
        if ((stride & 1) == 0)
        {
            stride++;
        }

        while (GreatestCommonDivisor(stride, count) != 1)
        {
            stride += 2;
        }

        return stride;
    }


    /// <summary>
    /// Fills a shelf with sorted `SS8-8` tuples for read-path measurement.<br/>
    /// The key and identity are identical so range checks remain easy to verify by checksum.<br/>
    /// </summary>
    /// <param name="shelf">The mutable shelf to fill.</param>
    /// <param name="itemCount">The number of sorted tuples to insert.</param>
    private static void FillScalar8Scalar8Shelf(ref Scalar8Scalar8 shelf, int itemCount)
    {
        for (int i = 0; i < itemCount; i++)
        {
            Scalar8Scalar8InsertResult result = shelf.Insert(
                Scalar8Scalar8Layout.EncodeUnsignedScalar8((ulong)i),
                Scalar8Scalar8Layout.EncodeUnsignedScalar8((ulong)i),
                allowDuplicateKeys: true);
            if (result != Scalar8Scalar8InsertResult.Inserted)
            {
                throw new InvalidDataException($"Expected SS8-8 perf fill insert at {i}, got {result}.");
            }
        }
    }


    /// <summary>
    /// Measures lower-bound lookups from a precomputed key vector.<br/>
    /// The vector is shared across equal-work profiles so lookup distribution is controlled.<br/>
    /// </summary>
    /// <param name="shelf">The read-only shelf view.</param>
    /// <param name="lookupKeys">The logical key values to seek.</param>
    /// <returns>The measured nanoseconds per lookup plus a checksum guard.</returns>
    private static Scalar8Scalar8LookupPerfResult MeasureScalar8Scalar8LowerBound(Scalar8Scalar8ReadOnly shelf, int[] lookupKeys)
    {
        long checksum = 0;
        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 0; i < lookupKeys.Length; i++)
        {
            ulong key = Scalar8Scalar8Layout.EncodeUnsignedScalar8((ulong)lookupKeys[i]);
            checksum += shelf.LowerBoundKey(key);
        }

        watch.Stop();
        double nsPerLookup = (watch.Elapsed.TotalSeconds * 1_000_000_000d) / lookupKeys.Length;
        return new Scalar8Scalar8LookupPerfResult(nsPerLookup, checksum);
    }


    /// <summary>
    /// Measures `LowerBoundKey` plus one identity read from a precomputed key vector.<br/>
    /// This represents seek-and-take-one behavior without range-loop amortization.<br/>
    /// </summary>
    /// <param name="shelf">The read-only shelf view.</param>
    /// <param name="lookupKeys">The logical key values to seek.</param>
    /// <returns>The measured nanoseconds per seek-one operation plus a checksum guard.</returns>
    private static Scalar8Scalar8LookupPerfResult MeasureScalar8Scalar8SeekOne(Scalar8Scalar8ReadOnly shelf, int[] lookupKeys)
    {
        long checksum = 0;
        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 0; i < lookupKeys.Length; i++)
        {
            int slot = shelf.LowerBoundKey(Scalar8Scalar8Layout.EncodeUnsignedScalar8((ulong)lookupKeys[i]));
            checksum += (long)shelf.ReadIdentityAt(slot);
        }

        watch.Stop();
        double nsPerLookup = (watch.Elapsed.TotalSeconds * 1_000_000_000d) / lookupKeys.Length;
        return new Scalar8Scalar8LookupPerfResult(nsPerLookup, checksum);
    }


    /// <summary>
    /// Measures range walking from precomputed range starts.<br/>
    /// This captures lower-bound plus repeated slot-indirected identity reads without caller-buffer writes.<br/>
    /// </summary>
    /// <param name="shelf">The read-only shelf view.</param>
    /// <param name="rangeStarts">The logical range-start keys.</param>
    /// <param name="rangeLength">The number of identities to walk after each lower-bound.</param>
    /// <returns>The measured nanoseconds per range and per identity plus a checksum guard.</returns>
    private static Scalar8Scalar8RangePerfResult MeasureScalar8Scalar8RangeWalk(Scalar8Scalar8ReadOnly shelf, int[] rangeStarts, int rangeLength)
    {
        long identitiesWalked = (long)rangeStarts.Length * rangeLength;
        long checksum = 0;
        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 0; i < rangeStarts.Length; i++)
        {
            int start = rangeStarts[i];
            int slot = shelf.LowerBoundKey(Scalar8Scalar8Layout.EncodeUnsignedScalar8((ulong)start));
            int end = slot + rangeLength;
            for (int j = slot; j < end; j++)
            {
                checksum += (long)shelf.ReadIdentityAt(j);
            }
        }

        watch.Stop();
        double totalNs = watch.Elapsed.TotalSeconds * 1_000_000_000d;
        return new Scalar8Scalar8RangePerfResult(totalNs / rangeStarts.Length, totalNs / identitiesWalked, checksum);
    }


    /// <summary>
    /// Measures range walking from known slot positions, without lower-bound seek cost.<br/>
    /// This isolates slot-indirected range read cost from the seek cost paid before a range starts.<br/>
    /// </summary>
    /// <param name="shelf">The read-only shelf view.</param>
    /// <param name="rangeStarts">The already-valid slot starts.</param>
    /// <param name="rangeLength">The number of identities to walk per range.</param>
    /// <returns>The measured nanoseconds per range and per identity plus a checksum guard.</returns>
    private static Scalar8Scalar8RangePerfResult MeasureScalar8Scalar8KnownSlotRange(Scalar8Scalar8ReadOnly shelf, int[] rangeStarts, int rangeLength)
    {
        long identitiesWalked = (long)rangeStarts.Length * rangeLength;
        long checksum = 0;
        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 0; i < rangeStarts.Length; i++)
        {
            int slot = rangeStarts[i];
            int end = slot + rangeLength;
            for (int j = slot; j < end; j++)
            {
                checksum += (long)shelf.ReadIdentityAt(j);
            }
        }

        watch.Stop();
        double totalNs = watch.Elapsed.TotalSeconds * 1_000_000_000d;
        return new Scalar8Scalar8RangePerfResult(totalNs / rangeStarts.Length, totalNs / identitiesWalked, checksum);
    }


    /// <summary>
    /// Measures range scoop behavior from precomputed range starts.<br/>
    /// Scoop includes the range lower-bound plus writes each identity into a reusable caller buffer.<br/>
    /// </summary>
    /// <param name="shelf">The read-only shelf view.</param>
    /// <param name="rangeStarts">The logical range-start keys.</param>
    /// <param name="rangeLength">The number of identities copied into the caller buffer.</param>
    /// <returns>The measured nanoseconds per range and per identity plus a checksum guard.</returns>
    private static Scalar8Scalar8RangePerfResult MeasureScalar8Scalar8IdentityScoop(Scalar8Scalar8ReadOnly shelf, int[] rangeStarts, int rangeLength)
    {
        ulong[] buffer = new ulong[rangeLength];
        long identitiesScooped = (long)rangeStarts.Length * rangeLength;
        long checksum = 0;
        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 0; i < rangeStarts.Length; i++)
        {
            int start = rangeStarts[i];
            int slot = shelf.LowerBoundKey(Scalar8Scalar8Layout.EncodeUnsignedScalar8((ulong)start));
            for (int j = 0; j < rangeLength; j++)
            {
                ulong identity = shelf.ReadIdentityAt(slot + j);
                buffer[j] = identity;
                checksum += (long)identity;
            }
        }

        watch.Stop();
        double totalNs = watch.Elapsed.TotalSeconds * 1_000_000_000d;
        return new Scalar8Scalar8RangePerfResult(totalNs / rangeStarts.Length, totalNs / identitiesScooped, checksum);
    }


    /// <summary>
    /// Computes mean, median, min, max, and sample standard deviation for repeated measurements.<br/>
    /// The harness keeps median visible beside mean so transient host noise does not drive performance decisions.<br/>
    /// </summary>
    /// <param name="values">The measured values for one metric.</param>
    /// <returns>The aggregated measurement statistics.</returns>
    private static Scalar8Scalar8MetricStats ComputeStats(double[] values)
    {
        double sum = 0;
        double min = double.MaxValue;
        double max = double.MinValue;
        for (int i = 0; i < values.Length; i++)
        {
            double value = values[i];
            sum += value;
            min = Math.Min(min, value);
            max = Math.Max(max, value);
        }

        double mean = sum / values.Length;
        double[] sortedValues = new double[values.Length];
        values.CopyTo(sortedValues, 0);
        Array.Sort(sortedValues);
        double median = sortedValues.Length % 2 == 0
            ? (sortedValues[(sortedValues.Length / 2) - 1] + sortedValues[sortedValues.Length / 2]) / 2.0
            : sortedValues[sortedValues.Length / 2];
        double varianceSum = 0;
        for (int i = 0; i < values.Length; i++)
        {
            double delta = values[i] - mean;
            varianceSum += delta * delta;
        }

        double standardDeviation = values.Length <= 1 ? 0 : Math.Sqrt(varianceSum / (values.Length - 1));
        return new Scalar8Scalar8MetricStats(mean, median, min, max, standardDeviation);
    }


    /// <summary>
    /// Appends one order-specific cadence drift row to the cadence-suite report.<br/>
    /// The row compares the `102` and `10` item commit cadences against the `1024` item cadence so fixed-per-commit overhead remains obvious.<br/>
    /// </summary>
    /// <param name="builder">The markdown report builder receiving the row.</param>
    /// <param name="rows">The measured cadence rows.</param>
    /// <param name="orderName">The order name to compare, normally `sorted` or `random`.</param>
    private static void AppendScalar8Scalar8RoutedCadenceDrift(
        StringBuilder builder,
        IReadOnlyList<Scalar8Scalar8RoutedCadenceResult> rows,
        string orderName)
    {
        Scalar8Scalar8RoutedBulkWriteResult large = FindScalar8Scalar8RoutedCadenceResult(rows, orderName, 1024);
        Scalar8Scalar8RoutedBulkWriteResult medium = FindScalar8Scalar8RoutedCadenceResult(rows, orderName, 102);
        Scalar8Scalar8RoutedBulkWriteResult tiny = FindScalar8Scalar8RoutedCadenceResult(rows, orderName, 10);
        builder.AppendLine(CultureInfo.InvariantCulture, $"| {orderName} | {large.ItemsPerSecond:F2} | {medium.ItemsPerSecond:F2} | {tiny.ItemsPerSecond:F2} | {FormatPercentChange(medium.ItemsPerSecond, large.ItemsPerSecond)} | {FormatPercentChange(tiny.ItemsPerSecond, large.ItemsPerSecond)} | {large.BytesPerItem:F2} | {medium.BytesPerItem:F2} | {tiny.BytesPerItem:F2} |");
    }


    /// <summary>
    /// Finds one cadence-suite result by order name and items-per-batch.<br/>
    /// The lookup keeps report shaping simple while still failing loudly if the fixed cadence ladder changes without updating the drift table.<br/>
    /// </summary>
    /// <param name="rows">The measured cadence rows.</param>
    /// <param name="orderName">The order name to match.</param>
    /// <param name="itemsPerBatch">The cadence item count to match.</param>
    /// <returns>The matching routed bulk-write result.</returns>
    private static Scalar8Scalar8RoutedBulkWriteResult FindScalar8Scalar8RoutedCadenceResult(
        IReadOnlyList<Scalar8Scalar8RoutedCadenceResult> rows,
        string orderName,
        int itemsPerBatch)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            Scalar8Scalar8RoutedCadenceResult row = rows[i];
            if (row.OrderName == orderName && row.Result.ItemsPerBatch == itemsPerBatch)
            {
                return row.Result;
            }
        }

        throw new InvalidDataException($"Missing SS8-8 routed cadence-suite row for order '{orderName}' with {itemsPerBatch} items per batch.");
    }


    /// <summary>
    /// Measures raw `DataKernel` writing of contiguous 16-byte logical item payloads with one commit per batch.<br/>
    /// The order vector controls generated key identity values so this row shares the same logical item sequence as `SS8-8` rows.<br/>
    /// </summary>
    /// <param name="path">The backing file path for the row.</param>
    /// <param name="name">The report row name.</param>
    /// <param name="batches">The measured batch count.</param>
    /// <param name="warmupBatches">The unmeasured warmup batch count.</param>
    /// <param name="itemsPerBatch">The number of 16-byte items per batch.</param>
    /// <param name="order">The deterministic item order vector.</param>
    /// <param name="options">The `DataKernel` options.</param>
    /// <returns>The measured write-parity row.</returns>
    private static Scalar8Scalar8WriteParityResult MeasureDataKernelItemStreamWriteParity(
        string path,
        string name,
        int batches,
        int warmupBatches,
        int itemsPerBatch,
        int[] order,
        DataKernelOptions options)
    {
        File.Delete(path);
        using DataKernel kernel = OpenKernel(DataKernelBackingKind.File, path, FileMode.Create, options, DataKernelTelemetryOptions.EnabledOptions);
        _ = RunDataKernelItemStreamWriteParityLoop(kernel, warmupBatches, itemsPerBatch, order);
        Stopwatch watch = Stopwatch.StartNew();
        WriteParityLoopTelemetry telemetry = RunDataKernelItemStreamWriteParityLoop(kernel, batches, itemsPerBatch, order);
        watch.Stop();
        return CreateWriteParityResult(name, batches, itemsPerBatch, watch.Elapsed, telemetry);
    }


    /// <summary>
    /// Measures one raw `DataKernel` contiguous fixed-width payload stream sample.<br/>
    /// The measured loop writes exactly `itemsPerBatch * payloadBytes` bytes per logical batch and commits once, matching the harness batch cadence without shelf/router work.<br/>
    /// </summary>
    /// <param name="path">The backing file path.</param>
    /// <param name="name">The report row name.</param>
    /// <param name="batches">The measured batch count.</param>
    /// <param name="warmupBatches">The unmeasured warmup batch count.</param>
    /// <param name="itemsPerBatch">The number of payload rows per batch.</param>
    /// <param name="payloadBytes">The fixed logical payload bytes per row.</param>
    /// <param name="order">The deterministic item order vector.</param>
    /// <param name="options">The `DataKernel` options.</param>
    /// <returns>The measured raw payload-stream write row.</returns>
    private static Scalar8Scalar8WriteParityResult MeasureDataKernelPayloadStreamWriteParity(
        string path,
        string name,
        int batches,
        int warmupBatches,
        int itemsPerBatch,
        int payloadBytes,
        int[] order,
        DataKernelOptions options)
    {
        if (payloadBytes <= 0 || payloadBytes % sizeof(ulong) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(payloadBytes), payloadBytes, "Raw payload bytes must be a positive multiple of eight.");
        }

        File.Delete(path);
        using DataKernel kernel = OpenKernel(DataKernelBackingKind.File, path, FileMode.Create, options, DataKernelTelemetryOptions.EnabledOptions);
        _ = RunDataKernelPayloadStreamWriteParityLoop(kernel, warmupBatches, itemsPerBatch, payloadBytes, order);
        Stopwatch watch = Stopwatch.StartNew();
        WriteParityLoopTelemetry telemetry = RunDataKernelPayloadStreamWriteParityLoop(kernel, batches, itemsPerBatch, payloadBytes, order);
        watch.Stop();
        return CreateWriteParityResult(name, batches, itemsPerBatch, watch.Elapsed, telemetry);
    }


    /// <summary>
    /// Measures raw `DataKernel` writing of one generated shelf-sized extent with one commit per batch.<br/>
    /// This row pays full-shelf bytes without real `SS8-8` slot maintenance so the shelf mutation cost can be isolated.<br/>
    /// </summary>
    /// <param name="path">The backing file path for the row.</param>
    /// <param name="name">The report row name.</param>
    /// <param name="batches">The measured batch count.</param>
    /// <param name="warmupBatches">The unmeasured warmup batch count.</param>
    /// <param name="itemsPerBatch">The number of logical items generated into each extent.</param>
    /// <param name="order">The deterministic item order vector.</param>
    /// <param name="profile">The `SS8-8` profile that supplies shelf extent size.</param>
    /// <param name="options">The `DataKernel` options.</param>
    /// <returns>The measured write-parity row.</returns>
    private static Scalar8Scalar8WriteParityResult MeasureDataKernelShelfExtentWriteParity(
        string path,
        string name,
        int batches,
        int warmupBatches,
        int itemsPerBatch,
        int[] order,
        Scalar8Scalar8Profile profile,
        DataKernelOptions options)
    {
        File.Delete(path);
        using DataKernel kernel = OpenKernel(DataKernelBackingKind.File, path, FileMode.Create, options, DataKernelTelemetryOptions.EnabledOptions);
        _ = RunDataKernelShelfExtentWriteParityLoop(kernel, warmupBatches, itemsPerBatch, order, profile);
        Stopwatch watch = Stopwatch.StartNew();
        WriteParityLoopTelemetry telemetry = RunDataKernelShelfExtentWriteParityLoop(kernel, batches, itemsPerBatch, order, profile);
        watch.Stop();
        return CreateWriteParityResult(name, batches, itemsPerBatch, watch.Elapsed, telemetry);
    }


    /// <summary>
    /// Measures real `SS8-8` shelf construction through the mutable shelf view with one committed shelf extent per batch.<br/>
    /// This row includes shelf initialization, binary search, slot movement, tuple writes, and `DataKernel` commit cost.<br/>
    /// </summary>
    /// <param name="path">The backing file path for the row.</param>
    /// <param name="name">The report row name.</param>
    /// <param name="batches">The measured batch count.</param>
    /// <param name="warmupBatches">The unmeasured warmup batch count.</param>
    /// <param name="itemsPerBatch">The number of items inserted into each shelf.</param>
    /// <param name="order">The deterministic item order vector.</param>
    /// <param name="profile">The `SS8-8` profile that supplies shelf extent size.</param>
    /// <param name="options">The `DataKernel` options.</param>
    /// <returns>The measured write-parity row.</returns>
    private static Scalar8Scalar8WriteParityResult MeasureScalar8Scalar8ShelfWriteParity(
        string path,
        string name,
        int batches,
        int warmupBatches,
        int itemsPerBatch,
        int[] order,
        Scalar8Scalar8Profile profile,
        DataKernelOptions options)
    {
        File.Delete(path);
        using DataKernel kernel = OpenKernel(DataKernelBackingKind.File, path, FileMode.Create, options, DataKernelTelemetryOptions.EnabledOptions);
        _ = RunScalar8Scalar8ShelfWriteParityLoop(kernel, warmupBatches, itemsPerBatch, order, profile);
        Stopwatch watch = Stopwatch.StartNew();
        WriteParityLoopTelemetry telemetry = RunScalar8Scalar8ShelfWriteParityLoop(kernel, batches, itemsPerBatch, order, profile);
        watch.Stop();
        return CreateWriteParityResult(name, batches, itemsPerBatch, watch.Elapsed, telemetry);
    }


    /// <summary>
    /// Measures public-wrapper routed `SS8-8` bulk insertion with one `Scalar8Scalar8Batch.Commit` call per batch.<br/>
    /// Warmup uses the same file so the measured loop starts after initial facade/session setup and first-route creation noise has been exercised.<br/>
    /// </summary>
    /// <param name="path">The backing file path for the row.</param>
    /// <param name="name">The report row name.</param>
    /// <param name="batches">The measured batch count.</param>
    /// <param name="warmupBatches">The unmeasured warmup batch count.</param>
    /// <param name="itemsPerBatch">The number of encoded tuples inserted per committed batch.</param>
    /// <param name="prefixCount">The number of root prefixes covered by each generated batch.</param>
    /// <param name="order">The deterministic insertion order vector.</param>
    /// <param name="options">The `DataKernel` options.</param>
    /// <returns>The measured routed bulk-write row.</returns>
    private static Scalar8Scalar8RoutedBulkWriteResult MeasureScalar8Scalar8RoutedBulkWrite(
        string path,
        string name,
        int batches,
        int warmupBatches,
        int itemsPerBatch,
        int prefixCount,
        int[] order,
        DataKernelOptions options,
        Scalar8Scalar8Profile profile)
    {
        File.Delete(path);
        using Scalar8Scalar8Index index = Indexes.SS88.Create(
            path,
            DataKernelBackingKind.File,
            name: "routed-bulk",
            options: options,
            developerMetadata: CreateDesignPerfMetadata(91),
            telemetryOptions: DataKernelTelemetryOptions.EnabledOptions,
            shelfExtentSize: profile.ShelfExtentSize);

        _ = RunScalar8Scalar8RoutedBulkWriteLoop(index, warmupBatches, itemsPerBatch, prefixCount, order, batchOffset: 0);
        Stopwatch watch = Stopwatch.StartNew();
        RoutedBulkWriteLoopTelemetry telemetry = RunScalar8Scalar8RoutedBulkWriteLoop(index, batches, itemsPerBatch, prefixCount, order, warmupBatches);
        watch.Stop();
        return CreateRoutedBulkWriteResult(name, batches, itemsPerBatch, watch.Elapsed, telemetry);
    }


    /// <summary>
    /// Measures one harness-level `SS16-16` routed bulk-write sample.<br/>
    /// The root router is created before timing, warmup rows are inserted into the same fresh file, and the measured rows use the same generated key set as the SQLite BLOB comparison.<br/>
    /// </summary>
    /// <param name="path">The LibraDex file path.</param>
    /// <param name="name">The report row name.</param>
    /// <param name="batches">The measured logical batch count.</param>
    /// <param name="warmupBatches">The unmeasured warmup logical batch count.</param>
    /// <param name="itemsPerBatch">The item count inserted per logical batch.</param>
    /// <param name="prefixCount">The number of root prefixes covered by each generated batch.</param>
    /// <param name="order">The deterministic insertion order vector.</param>
    /// <param name="options">The DataKernel options.</param>
    /// <param name="profile">The `SS16-16` shelf profile.</param>
    /// <param name="includeAttribution">True to collect Stopwatch-based attribution timings in addition to cheap structural counts.</param>
    /// <returns>The measured routed bulk-write result.</returns>
    private static Scalar8Scalar8RoutedBulkWriteResult MeasureScalar16Scalar16RoutedBulkWrite(
        string path,
        string name,
        int batches,
        int warmupBatches,
        int itemsPerBatch,
        int prefixCount,
        int[] order,
        DataKernelOptions options,
        Scalar16Scalar16Profile profile,
        bool includeAttribution)
    {
        File.Delete(path);
        using LibraDexFileSession session = LibraDexFileSession.Initialize(path, options, CreateDesignPerfMetadata(411), DataKernelTelemetryOptions.EnabledOptions);
        (RouterSnapshot root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "s1616bulk", 0));
        _ = RunScalar16Scalar16RoutedBulkWriteLoop(session, root.Offset, warmupBatches, itemsPerBatch, prefixCount, order, batchOffset: 0, profile, includeAttribution);
        Stopwatch watch = Stopwatch.StartNew();
        RoutedBulkWriteLoopTelemetry telemetry = RunScalar16Scalar16RoutedBulkWriteLoop(session, root.Offset, batches, itemsPerBatch, prefixCount, order, warmupBatches, profile, includeAttribution);
        watch.Stop();
        return CreateRoutedBulkWriteResult(name, batches, itemsPerBatch, watch.Elapsed, telemetry);
    }


    /// <summary>
    /// Measures one harness-level `SS16-8` routed bulk-write sample.<br/>
    /// The root router is created before timing, warmup rows are inserted into the same fresh file, and the measured rows use the same widened-key generator as `SS16-16`.<br/>
    /// </summary>
    /// <param name="path">The LibraDex file path.</param>
    /// <param name="name">The report row name.</param>
    /// <param name="batches">The measured logical batch count.</param>
    /// <param name="warmupBatches">The unmeasured warmup logical batch count.</param>
    /// <param name="itemsPerBatch">The item count inserted per logical batch.</param>
    /// <param name="prefixCount">The number of root prefixes covered by each generated batch.</param>
    /// <param name="order">The deterministic insertion order vector.</param>
    /// <param name="options">The DataKernel options.</param>
    /// <param name="profile">The `SS16-8` shelf profile.</param>
    /// <param name="includeAttribution">True to collect Stopwatch-based attribution timings in addition to cheap structural counts.</param>
    /// <returns>The measured routed bulk-write result.</returns>
    private static Scalar8Scalar8RoutedBulkWriteResult MeasureScalar16Scalar8RoutedBulkWrite(
        string path,
        string name,
        int batches,
        int warmupBatches,
        int itemsPerBatch,
        int prefixCount,
        int[] order,
        DataKernelOptions options,
        Scalar16Scalar8Profile profile,
        bool includeAttribution)
    {
        File.Delete(path);
        using LibraDexFileSession session = LibraDexFileSession.Initialize(path, options, CreateDesignPerfMetadata(168), DataKernelTelemetryOptions.EnabledOptions);
        (RouterSnapshot root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "s168bulk", 0));
        _ = RunScalar16Scalar8RoutedBulkWriteLoop(session, root.Offset, warmupBatches, itemsPerBatch, prefixCount, order, batchOffset: 0, profile, includeAttribution);
        Stopwatch watch = Stopwatch.StartNew();
        RoutedBulkWriteLoopTelemetry telemetry = RunScalar16Scalar8RoutedBulkWriteLoop(session, root.Offset, batches, itemsPerBatch, prefixCount, order, warmupBatches, profile, includeAttribution);
        watch.Stop();
        return CreateRoutedBulkWriteResult(name, batches, itemsPerBatch, watch.Elapsed, telemetry);
    }


    /// <summary>
    /// Measures one harness-level `SS8-16` routed bulk-write sample.<br/>
    /// The root router is created before timing, warmup rows are inserted into the same fresh file, and the measured rows use the normal 8-byte routed key generator.<br/>
    /// </summary>
    /// <param name="path">The LibraDex file path.</param>
    /// <param name="name">The report row name.</param>
    /// <param name="batches">The measured logical batch count.</param>
    /// <param name="warmupBatches">The unmeasured warmup logical batch count.</param>
    /// <param name="itemsPerBatch">The item count inserted per logical batch.</param>
    /// <param name="prefixCount">The number of root prefixes covered by each generated batch.</param>
    /// <param name="order">The deterministic insertion order vector.</param>
    /// <param name="options">The DataKernel options.</param>
    /// <param name="profile">The `SS8-16` shelf profile.</param>
    /// <param name="includeAttribution">True to collect Stopwatch-based attribution timings in addition to cheap structural counts.</param>
    /// <returns>The measured routed bulk-write result.</returns>
    private static Scalar8Scalar8RoutedBulkWriteResult MeasureScalar8Scalar16RoutedBulkWrite(
        string path,
        string name,
        int batches,
        int warmupBatches,
        int itemsPerBatch,
        int prefixCount,
        int[] order,
        DataKernelOptions options,
        Scalar8Scalar16Profile profile,
        bool includeAttribution)
    {
        File.Delete(path);
        using LibraDexFileSession session = LibraDexFileSession.Initialize(path, options, CreateDesignPerfMetadata(816), DataKernelTelemetryOptions.EnabledOptions);
        (RouterSnapshot root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "s816bulk", 0));
        _ = RunScalar8Scalar16RoutedBulkWriteLoop(session, root.Offset, warmupBatches, itemsPerBatch, prefixCount, order, batchOffset: 0, profile, includeAttribution);
        Stopwatch watch = Stopwatch.StartNew();
        RoutedBulkWriteLoopTelemetry telemetry = RunScalar8Scalar16RoutedBulkWriteLoop(session, root.Offset, batches, itemsPerBatch, prefixCount, order, warmupBatches, profile, includeAttribution);
        watch.Stop();
        return CreateRoutedBulkWriteResult(name, batches, itemsPerBatch, watch.Elapsed, telemetry);
    }


    /// <summary>
    /// Measures one routed `SS8-8` write-attribution sample.<br/>
    /// The measured loop separates insert-path wall time from batch commit time and records structural mutation kinds before the DataKernel folds staged writes into one durable publication.<br/>
    /// </summary>
    /// <param name="path">The backing file path for the row.</param>
    /// <param name="name">The report row name.</param>
    /// <param name="batches">The measured batch count.</param>
    /// <param name="warmupBatches">The unmeasured warmup batch count.</param>
    /// <param name="itemsPerBatch">The number of encoded tuples inserted per committed batch.</param>
    /// <param name="prefixCount">The number of root prefixes covered by each generated batch.</param>
    /// <param name="order">The deterministic insertion order vector.</param>
    /// <param name="options">The `DataKernel` options.</param>
    /// <returns>The measured write-attribution row.</returns>
    private static Scalar8Scalar8WriteAttributionResult MeasureScalar8Scalar8WriteAttribution(
        string path,
        string name,
        int batches,
        int warmupBatches,
        int itemsPerBatch,
        int prefixCount,
        int[] order,
        DataKernelOptions options)
    {
        File.Delete(path);
        using Scalar8Scalar8Index index = Indexes.SS88.Create(
            path,
            DataKernelBackingKind.File,
            name: "routed-write-attribution",
            options: options,
            developerMetadata: CreateDesignPerfMetadata(101),
            telemetryOptions: DataKernelTelemetryOptions.EnabledOptions);

        _ = RunScalar8Scalar8RoutedBulkWriteLoop(index, warmupBatches, itemsPerBatch, prefixCount, order, batchOffset: 0);
        Stopwatch totalWatch = Stopwatch.StartNew();
        Scalar8Scalar8WriteAttributionTelemetry telemetry = RunScalar8Scalar8WriteAttributionLoop(index, batches, itemsPerBatch, prefixCount, order, warmupBatches);
        totalWatch.Stop();
        return CreateScalar8Scalar8WriteAttributionResult(name, batches, itemsPerBatch, totalWatch.Elapsed, telemetry);
    }


    /// <summary>
    /// Measures typed unsigned public-wrapper routed `SS8-8` bulk insertion with one typed batch commit per batch.<br/>
    /// The generated logical keys match the encoded public-wrapper benchmark so storage shape can be compared directly.<br/>
    /// </summary>
    /// <param name="path">The backing file path for the row.</param>
    /// <param name="name">The report row name.</param>
    /// <param name="batches">The measured batch count.</param>
    /// <param name="warmupBatches">The unmeasured warmup batch count.</param>
    /// <param name="itemsPerBatch">The number of tuples inserted per committed batch.</param>
    /// <param name="prefixCount">The number of root prefixes covered by each generated batch.</param>
    /// <param name="order">The deterministic insertion order vector.</param>
    /// <param name="options">The `DataKernel` options.</param>
    /// <returns>The measured typed routed bulk-write row.</returns>
    private static Scalar8Scalar8RoutedBulkWriteResult MeasureUnsignedScalar8Scalar8RoutedBulkWrite(
        string path,
        string name,
        int batches,
        int warmupBatches,
        int itemsPerBatch,
        int prefixCount,
        int[] order,
        DataKernelOptions options)
    {
        File.Delete(path);
        using UnsignedScalar8Scalar8Index index = Indexes.SS88.Unsigned.Create(
            path,
            DataKernelBackingKind.File,
            name: "typed-routed-bulk",
            options: options,
            developerMetadata: CreateDesignPerfMetadata(98),
            telemetryOptions: DataKernelTelemetryOptions.EnabledOptions);

        _ = RunUnsignedScalar8Scalar8RoutedBulkWriteLoop(index, warmupBatches, itemsPerBatch, prefixCount, order, batchOffset: 0);
        Stopwatch watch = Stopwatch.StartNew();
        RoutedBulkWriteLoopTelemetry telemetry = RunUnsignedScalar8Scalar8RoutedBulkWriteLoop(index, batches, itemsPerBatch, prefixCount, order, warmupBatches);
        watch.Stop();
        return CreateRoutedBulkWriteResult(name, batches, itemsPerBatch, watch.Elapsed, telemetry);
    }


    /// <summary>
    /// Measures one typed unsigned public range read over a batch-built `SS8-8` index.<br/>
    /// The validation keys match the encoded benchmark so any throughput drift is attributable to the typed wrapper path or host noise.<br/>
    /// </summary>
    /// <param name="index">The reopened typed public unsigned index.</param>
    /// <param name="name">The result row name.</param>
    /// <param name="batches">The setup batch count.</param>
    /// <param name="itemsPerBatch">The setup items per batch.</param>
    /// <param name="prefixCount">The setup root-prefix count.</param>
    /// <param name="lowerPrefix">The inclusive lower root prefix.</param>
    /// <param name="upperPrefix">The inclusive upper root prefix.</param>
    /// <param name="iterations">The measured read iterations.</param>
    /// <param name="enableCoalescing">Whether to enable public range-read coalescing.</param>
    /// <param name="identities">The reusable identity output buffer.</param>
    /// <returns>The measured typed public bulk-read row.</returns>
    private static Scalar8Scalar8PublicBulkReadResult MeasureUnsignedScalar8Scalar8PublicBulkReadRange(
        UnsignedScalar8Scalar8Index index,
        string name,
        int batches,
        int itemsPerBatch,
        int prefixCount,
        int lowerPrefix,
        int upperPrefix,
        int iterations,
        bool enableCoalescing,
        Span<ulong> identities)
    {
        ulong lowerKey = (ulong)(byte)lowerPrefix << 56;
        ulong upperKey = ((ulong)(byte)upperPrefix << 56) | 0x00FF_FFFF_FFFF_FFFFUL;
        int expectedCount = CountScalar8Scalar8PublicBulkReadExpectedKeys(batches, itemsPerBatch, prefixCount, lowerPrefix, upperPrefix);
        if (identities.Length < expectedCount)
        {
            throw new ArgumentException("The typed public bulk-read identity buffer is too small for the requested range.", nameof(identities));
        }

        ulong[] expectedIdentities = CreateSortedScalar8Scalar8PublicBulkReadExpectedKeys(batches, itemsPerBatch, prefixCount, lowerPrefix, upperPrefix, expectedCount);
        _ = index.GetAndResetReadTelemetry();
        long checksum = 0;
        long totalIdentities = 0;
        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            UnsignedScalar8Scalar8RangeReadResult result = index.ReadRange(
                lowerKey,
                upperKey,
                identities,
                enableCoalescing);
            if (result.IdentityCount != expectedCount)
            {
                throw new InvalidDataException($"Typed public bulk read {name} returned {result.IdentityCount} identities; expected {expectedCount}.");
            }

            checksum += CheckScalar8Scalar8PublicBulkReadRange(identities, result.IdentityCount, expectedIdentities);
            totalIdentities += result.IdentityCount;
        }

        watch.Stop();
        DataKernelReadTelemetry readTelemetry = index.GetAndResetReadTelemetry();
        double seconds = Math.Max(watch.Elapsed.TotalSeconds, 0.000000001d);
        return new Scalar8Scalar8PublicBulkReadResult(
            name,
            iterations,
            expectedCount,
            totalIdentities,
            watch.Elapsed,
            totalIdentities / seconds,
            seconds * 1_000_000_000d / totalIdentities,
            readTelemetry.ReadCallCount / (double)iterations,
            readTelemetry.BytesRead / (double)iterations,
            checksum,
            1);
    }


    /// <summary>
    /// Measures one public encoded range read over a batch-built `SS8-8` index.<br/>
    /// The method validates every returned identity on every iteration so route-tree mistakes remain visible during perf probes.<br/>
    /// </summary>
    /// <param name="index">The reopened public encoded index.</param>
    /// <param name="name">The result row name.</param>
    /// <param name="batches">The setup batch count.</param>
    /// <param name="itemsPerBatch">The setup items per batch.</param>
    /// <param name="prefixCount">The setup root-prefix count.</param>
    /// <param name="lowerPrefix">The inclusive lower root prefix.</param>
    /// <param name="upperPrefix">The inclusive upper root prefix.</param>
    /// <param name="iterations">The measured read iterations.</param>
    /// <param name="enableCoalescing">Whether to enable public range-read coalescing.</param>
    /// <param name="identities">The reusable identity output buffer.</param>
    /// <returns>The measured public bulk-read row.</returns>
    private static Scalar8Scalar8PublicBulkReadResult MeasureScalar8Scalar8PublicBulkReadRange(
        Scalar8Scalar8Index index,
        string name,
        int batches,
        int itemsPerBatch,
        int prefixCount,
        int lowerPrefix,
        int upperPrefix,
        int iterations,
        bool enableCoalescing,
        Span<ulong> identities)
    {
        ulong lowerKey = Scalar8Scalar8Layout.EncodeUnsignedScalar8((ulong)(byte)lowerPrefix << 56);
        ulong upperKey = Scalar8Scalar8Layout.EncodeUnsignedScalar8(((ulong)(byte)upperPrefix << 56) | 0x00FF_FFFF_FFFF_FFFFUL);
        int expectedCount = CountScalar8Scalar8PublicBulkReadExpectedKeys(batches, itemsPerBatch, prefixCount, lowerPrefix, upperPrefix);
        if (identities.Length < expectedCount)
        {
            throw new ArgumentException("The public bulk-read identity buffer is too small for the requested range.", nameof(identities));
        }

        ulong[] expectedIdentities = CreateSortedScalar8Scalar8PublicBulkReadExpectedKeys(batches, itemsPerBatch, prefixCount, lowerPrefix, upperPrefix, expectedCount);
        _ = index.GetAndResetReadTelemetry();
        long checksum = 0;
        long totalIdentities = 0;
        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            Scalar8Scalar8EncodedRangeReadResult result = index.ReadEncodedRange(
                lowerKey,
                upperKey,
                identities,
                enableCoalescing);
            if (result.IdentityCount != expectedCount)
            {
                throw new InvalidDataException($"Public bulk read {name} returned {result.IdentityCount} identities; expected {expectedCount}.");
            }

            checksum += CheckScalar8Scalar8PublicBulkReadRange(identities, result.IdentityCount, expectedIdentities);
            totalIdentities += result.IdentityCount;
        }

        watch.Stop();
        DataKernelReadTelemetry readTelemetry = index.GetAndResetReadTelemetry();
        double seconds = Math.Max(watch.Elapsed.TotalSeconds, 0.000000001d);
        return new Scalar8Scalar8PublicBulkReadResult(
            name,
            iterations,
            expectedCount,
            totalIdentities,
            watch.Elapsed,
            totalIdentities / seconds,
            seconds * 1_000_000_000d / totalIdentities,
            readTelemetry.ReadCallCount / (double)iterations,
            readTelemetry.BytesRead / (double)iterations,
            checksum,
            1);
    }


    /// <summary>
    /// Diagnoses route-target locality for one public bulk-read prefix range.<br/>
    /// The diagnostic follows router targets in the same route order as range traversal and records only distinct shelf offsets.<br/>
    /// </summary>
    /// <param name="session">The reopened file session.</param>
    /// <param name="handle">The encoded `SS8-8` index handle.</param>
    /// <param name="name">The diagnostic row name.</param>
    /// <param name="lowerPrefix">The inclusive lower root prefix.</param>
    /// <param name="upperPrefix">The inclusive upper root prefix.</param>
    /// <returns>The target-locality diagnostic row.</returns>
    private static Scalar8Scalar8PublicReadTargetResult DiagnoseScalar8Scalar8PublicReadTargets(
        LibraDexFileSession session,
        Scalar8Scalar8IndexHandle handle,
        string name,
        int lowerPrefix,
        int upperPrefix)
    {
        List<long> shelfOffsets = [];
        Scalar8Scalar8PublicReadTargetTelemetry telemetry = default;
        ulong lowerKey = Scalar8Scalar8Layout.EncodeUnsignedScalar8((ulong)(byte)lowerPrefix << 56);
        ulong upperKey = Scalar8Scalar8Layout.EncodeUnsignedScalar8(((ulong)(byte)upperPrefix << 56) | 0x00FF_FFFF_FFFF_FFFFUL);
        TraverseScalar8Scalar8PublicReadTargets(
            session,
            handle.RootRouterOffset,
            lowerKey,
            upperKey,
            lowerEdge: true,
            upperEdge: true,
            remainingRouterHops: 8,
            shelfOffsets,
            ref telemetry);

        int runCount = 0;
        int maxRunShelves = 0;
        int currentRunShelves = 0;
        long runStartOffset = 0;
        for (int i = 0; i < shelfOffsets.Count; i++)
        {
            long offset = shelfOffsets[i];
            if (currentRunShelves == 0)
            {
                runStartOffset = offset;
                currentRunShelves = 1;
                runCount++;
                continue;
            }

            if (offset == runStartOffset + ((long)currentRunShelves * handle.Profile.ShelfExtentSize))
            {
                currentRunShelves++;
                continue;
            }

            maxRunShelves = Math.Max(maxRunShelves, currentRunShelves);
            runStartOffset = offset;
            currentRunShelves = 1;
            runCount++;
        }

        maxRunShelves = Math.Max(maxRunShelves, currentRunShelves);
        double averageRunShelves = runCount == 0 ? 0 : shelfOffsets.Count / (double)runCount;
        string firstOffsets = FormatFirstOffsets(shelfOffsets, maxCount: 8);
        return new Scalar8Scalar8PublicReadTargetResult(
            name,
            shelfOffsets.Count,
            runCount,
            maxRunShelves,
            averageRunShelves,
            telemetry.DuplicateTargetSkipCount,
            telemetry.RouterTargetCount,
            telemetry.MaxRouterDepth,
            firstOffsets);
    }


    /// <summary>
    /// Traverses public bulk-read route targets and records distinct shelf offsets in read order.<br/>
    /// This mirrors the coalesced traversal enough to diagnose locality without reading each shelf payload.<br/>
    /// </summary>
    /// <param name="session">The active session used to read router snapshots and classify targets.</param>
    /// <param name="routerOffset">The current router offset.</param>
    /// <param name="lowerEncodedKey">The inclusive lower encoded key.</param>
    /// <param name="upperEncodedKey">The inclusive upper encoded key.</param>
    /// <param name="lowerEdge">Whether this branch is on the lower edge of the range.</param>
    /// <param name="upperEdge">Whether this branch is on the upper edge of the range.</param>
    /// <param name="remainingRouterHops">The remaining router hop budget.</param>
    /// <param name="shelfOffsets">The route-order distinct shelf offsets.</param>
    /// <param name="telemetry">The target traversal telemetry accumulator.</param>
    private static void TraverseScalar8Scalar8PublicReadTargets(
        LibraDexFileSession session,
        long routerOffset,
        ulong lowerEncodedKey,
        ulong upperEncodedKey,
        bool lowerEdge,
        bool upperEdge,
        int remainingRouterHops,
        List<long> shelfOffsets,
        ref Scalar8Scalar8PublicReadTargetTelemetry telemetry)
    {
        if (remainingRouterHops <= 0)
        {
            throw new InvalidDataException("The public read-target diagnostic exceeded the configured maximum router hop count.");
        }

        RouterSnapshot router = session.ReadRouterSnapshot(routerOffset);
        telemetry.MaxRouterDepth = Math.Max(telemetry.MaxRouterDepth, router.KeyDepth);
        byte startPrefix = lowerEdge ? GetScalar8Scalar8PrefixForHarness(lowerEncodedKey, router.KeyDepth) : (byte)0;
        byte endPrefix = upperEdge ? GetScalar8Scalar8PrefixForHarness(upperEncodedKey, router.KeyDepth) : byte.MaxValue;
        long previousTargetOffset = 0;
        for (int prefix = startPrefix; prefix <= endPrefix; prefix++)
        {
            byte prefixByte = (byte)prefix;
            long targetOffset = session.FindRouterTarget(routerOffset, prefixByte);
            if (targetOffset == 0)
            {
                continue;
            }

            if (targetOffset == previousTargetOffset)
            {
                telemetry.DuplicateTargetSkipCount++;
                continue;
            }

            previousTargetOffset = targetOffset;
            Scalar8Scalar8RouteTargetKind kind = session.ClassifyScalar8Scalar8RouteTarget(targetOffset);
            if (kind == Scalar8Scalar8RouteTargetKind.Shelf)
            {
                if (shelfOffsets.Count == 0 || shelfOffsets[^1] != targetOffset)
                {
                    shelfOffsets.Add(targetOffset);
                }
                else
                {
                    telemetry.DuplicateTargetSkipCount++;
                }

                continue;
            }

            if (kind == Scalar8Scalar8RouteTargetKind.Router)
            {
                telemetry.RouterTargetCount++;
                TraverseScalar8Scalar8PublicReadTargets(
                    session,
                    targetOffset,
                    lowerEncodedKey,
                    upperEncodedKey,
                    lowerEdge && prefixByte == startPrefix,
                    upperEdge && prefixByte == endPrefix,
                    remainingRouterHops - 1,
                    shelfOffsets,
                    ref telemetry);
                continue;
            }

            throw new InvalidDataException("The public read-target diagnostic found an unsupported route target.");
        }
    }


    /// <summary>
    /// Measures public bulk-build file growth against unique live routers and shelves reachable from the encoded index handle.<br/>
    /// Structural counts are diagnostic; the headline values are final file bytes, live bytes, overhead bytes, bytes/item, and the bulk-write throughput already measured during setup.<br/>
    /// </summary>
    /// <param name="path">The file path of the built public index.</param>
    /// <param name="handle">The reopened public index handle.</param>
    /// <param name="batches">The number of setup batches.</param>
    /// <param name="itemsPerBatch">The number of inserted items per setup batch.</param>
    /// <param name="writeTelemetry">The bulk-write telemetry collected during setup.</param>
    /// <param name="session">The reopened file session used for route classification.</param>
    /// <returns>The public growth result.</returns>
    private static Scalar8Scalar8PublicGrowthResult MeasureScalar8Scalar8PublicGrowth(
        string path,
        Scalar8Scalar8IndexHandle handle,
        int batches,
        int itemsPerBatch,
        RoutedBulkWriteLoopTelemetry writeTelemetry,
        LibraDexFileSession session)
    {
        HashSet<long> routerOffsets = [];
        HashSet<long> shelfOffsets = [];
        TraverseScalar8Scalar8LiveStructure(session, handle.RootRouterOffset, remainingRouterHops: 8, routerOffsets, shelfOffsets);

        long itemCount = checked((long)batches * itemsPerBatch);
        long finalLength = GetFileLength(path);
        long metadataBytes = SuperblockLayout.Size + IndexDirectoryLayout.Size;
        long routerBytes = checked((long)routerOffsets.Count * RouterLayout.Size);
        long shelfBytes = checked((long)shelfOffsets.Count * handle.Profile.ShelfExtentSize);
        long liveBytes = checked(metadataBytes + routerBytes + shelfBytes);
        long overheadBytes = finalLength - liveBytes;
        long shelfCapacity = checked((long)shelfOffsets.Count * handle.Profile.MaxItemCount);
        long freeItemSlots = shelfCapacity - itemCount;
        long shelfFreeCapacityBytes = checked((freeItemSlots * (Scalar8Scalar8Layout.ItemSize + Scalar8Scalar8Layout.SlotSize)) + ((long)shelfOffsets.Count * handle.Profile.UnusedTailBytes));
        double occupancyPercent = shelfCapacity == 0 ? 0 : itemCount * 100d / shelfCapacity;
        double fileBytesPerItem = itemCount == 0 ? 0 : finalLength / (double)itemCount;
        double liveBytesPerItem = itemCount == 0 ? 0 : liveBytes / (double)itemCount;
        double overheadBytesPerItem = itemCount == 0 ? 0 : overheadBytes / (double)itemCount;
        double writtenBytesPerItem = writeTelemetry.InsertedCount == 0 ? 0 : writeTelemetry.BytesWritten / (double)writeTelemetry.InsertedCount;
        double writesPerBatch = writeTelemetry.CommitCount == 0 ? 0 : writeTelemetry.WriteCallCount / (double)writeTelemetry.CommitCount;

        return new Scalar8Scalar8PublicGrowthResult(
            finalLength,
            liveBytes,
            metadataBytes,
            routerBytes,
            shelfBytes,
            overheadBytes,
            routerOffsets.Count,
            shelfOffsets.Count,
            itemCount,
            shelfCapacity,
            occupancyPercent,
            freeItemSlots,
            shelfFreeCapacityBytes,
            fileBytesPerItem,
            liveBytesPerItem,
            overheadBytesPerItem,
            writtenBytesPerItem,
            writesPerBatch,
            writeTelemetry.SetLengthCallCount,
            writeTelemetry.Checksum,
            1);
    }


    /// <summary>
    /// Traverses the live `SS8-8` route graph and records unique router and shelf offsets reachable from one router.<br/>
    /// Duplicate route targets are folded so range compression and repeated direct-index entries do not inflate live structure counts.<br/>
    /// </summary>
    /// <param name="session">The reopened file session used to classify route targets.</param>
    /// <param name="routerOffset">The router offset to traverse.</param>
    /// <param name="remainingRouterHops">The remaining router hop limit.</param>
    /// <param name="routerOffsets">The unique live router offsets found so far.</param>
    /// <param name="shelfOffsets">The unique live shelf offsets found so far.</param>
    /// <exception cref="InvalidDataException">Thrown when the route graph exceeds the hop limit or contains an unsupported target.</exception>
    private static void TraverseScalar8Scalar8LiveStructure(
        LibraDexFileSession session,
        long routerOffset,
        int remainingRouterHops,
        HashSet<long> routerOffsets,
        HashSet<long> shelfOffsets)
    {
        if (remainingRouterHops <= 0)
        {
            throw new InvalidDataException("The public growth diagnostic exceeded the configured maximum router hop count.");
        }

        if (!routerOffsets.Add(routerOffset))
        {
            return;
        }

        for (int prefix = 0; prefix <= byte.MaxValue; prefix++)
        {
            long targetOffset = session.FindRouterTarget(routerOffset, (byte)prefix);
            if (targetOffset == 0)
            {
                continue;
            }

            Scalar8Scalar8RouteTargetKind kind = session.ClassifyScalar8Scalar8RouteTarget(targetOffset);
            if (kind == Scalar8Scalar8RouteTargetKind.Shelf)
            {
                shelfOffsets.Add(targetOffset);
                continue;
            }

            if (kind == Scalar8Scalar8RouteTargetKind.Router)
            {
                TraverseScalar8Scalar8LiveStructure(session, targetOffset, remainingRouterHops - 1, routerOffsets, shelfOffsets);
                continue;
            }

            throw new InvalidDataException("The public growth diagnostic found an unsupported route target.");
        }
    }


    /// <summary>
    /// Extracts one encoded `SS8-8` key byte for harness diagnostics.<br/>
    /// This mirrors storage routing byte selection without depending on private session helpers.<br/>
    /// </summary>
    /// <param name="encodedKey">The encoded sortable key.</param>
    /// <param name="keyDepth">The zero-based encoded key byte depth.</param>
    /// <returns>The selected key byte.</returns>
    private static byte GetScalar8Scalar8PrefixForHarness(ulong encodedKey, int keyDepth)
    {
        if ((uint)keyDepth >= Scalar8Scalar8Layout.KeySize)
        {
            throw new ArgumentOutOfRangeException(nameof(keyDepth), keyDepth, "SS8-8 scalar keys expose exactly eight routing bytes.");
        }

        int shift = (Scalar8Scalar8Layout.KeySize - 1 - keyDepth) * 8;
        return (byte)(encodedKey >> shift);
    }


    /// <summary>
    /// Creates sorted expected identities for one public bulk-read validation range.<br/>
    /// Large runs can wrap diagnostic routing bytes inside the generated key, so expected keys are sorted once before timed read iterations rather than assuming batch/local ordinal order.<br/>
    /// </summary>
    /// <param name="batches">The setup batch count.</param>
    /// <param name="itemsPerBatch">The setup items per batch.</param>
    /// <param name="prefixCount">The setup root-prefix count.</param>
    /// <param name="lowerPrefix">The inclusive lower root prefix.</param>
    /// <param name="upperPrefix">The inclusive upper root prefix.</param>
    /// <param name="expectedCount">The expected identity count for the range.</param>
    /// <returns>The sorted expected encoded identities.</returns>
    private static ulong[] CreateSortedScalar8Scalar8PublicBulkReadExpectedKeys(
        int batches,
        int itemsPerBatch,
        int prefixCount,
        int lowerPrefix,
        int upperPrefix,
        int expectedCount)
    {
        int itemsPerPrefix = (itemsPerBatch + prefixCount - 1) / prefixCount;
        ulong[] expectedKeys = new ulong[expectedCount];
        int expectedIndex = 0;
        for (int prefix = lowerPrefix; prefix <= upperPrefix; prefix++)
        {
            int firstOrdinal = prefix * itemsPerPrefix;
            int lastOrdinalExclusive = Math.Min(itemsPerBatch, firstOrdinal + itemsPerPrefix);
            if (firstOrdinal >= lastOrdinalExclusive)
            {
                continue;
            }

            for (int batch = 0; batch < batches; batch++)
            {
                for (int itemOrdinal = firstOrdinal; itemOrdinal < lastOrdinalExclusive; itemOrdinal++)
                {
                    if (expectedIndex >= expectedKeys.Length)
                    {
                        throw new InvalidDataException("Public bulk-read validation generated more expected keys than expected.");
                    }

                    expectedKeys[expectedIndex++] = CreateRoutedBulkWriteKey(batch, itemOrdinal, itemsPerBatch, prefixCount);
                }
            }
        }

        if (expectedIndex != expectedCount)
        {
            throw new InvalidDataException($"Public bulk-read validation generated {expectedIndex} expected keys; expected count was {expectedCount}.");
        }

        Array.Sort(expectedKeys);
        return expectedKeys;
    }


    /// <summary>
    /// Counts generated identities covered by one root-prefix range for public bulk-read validation.<br/>
    /// The final prefix group in a batch may be partial, and later prefixes can be empty when `itemsPerBatch` is smaller than the requested prefix fanout.<br/>
    /// </summary>
    /// <param name="batches">The setup batch count.</param>
    /// <param name="itemsPerBatch">The setup items per batch.</param>
    /// <param name="prefixCount">The setup root-prefix count.</param>
    /// <param name="lowerPrefix">The inclusive lower root prefix.</param>
    /// <param name="upperPrefix">The inclusive upper root prefix.</param>
    /// <returns>The number of generated identities that should be returned by the range.</returns>
    private static int CountScalar8Scalar8PublicBulkReadExpectedKeys(
        int batches,
        int itemsPerBatch,
        int prefixCount,
        int lowerPrefix,
        int upperPrefix)
    {
        int itemsPerPrefix = (itemsPerBatch + prefixCount - 1) / prefixCount;
        int perBatchCount = 0;
        for (int prefix = lowerPrefix; prefix <= upperPrefix; prefix++)
        {
            int firstOrdinal = prefix * itemsPerPrefix;
            int lastOrdinalExclusive = Math.Min(itemsPerBatch, firstOrdinal + itemsPerPrefix);
            if (firstOrdinal < lastOrdinalExclusive)
            {
                perBatchCount += lastOrdinalExclusive - firstOrdinal;
            }
        }

        return checked(batches * perBatchCount);
    }


    /// <summary>
    /// Validates one public bulk-read identity span against precomputed expected encoded identities.<br/>
    /// Expected identities must already be sorted in persisted encoded key order so validation does not pollute timed read iterations with sorting work.<br/>
    /// </summary>
    /// <param name="identities">The returned identity span.</param>
    /// <param name="actualCount">The returned identity count.</param>
    /// <param name="expectedIdentities">The precomputed sorted expected identities.</param>
    /// <returns>A compact checksum over the validated identities.</returns>
    private static long CheckScalar8Scalar8PublicBulkReadRange(
        ReadOnlySpan<ulong> identities,
        int actualCount,
        ReadOnlySpan<ulong> expectedIdentities)
    {
        if (expectedIdentities.Length != actualCount)
        {
            throw new InvalidDataException($"Public bulk-read validation expected {expectedIdentities.Length} identities; actual count was {actualCount}.");
        }

        long checksum = 0;
        for (int index = 0; index < actualCount; index++)
        {
            ulong expected = expectedIdentities[index];
            if (identities[index] != expected)
            {
                throw new InvalidDataException($"Unexpected public bulk-read identity at index {index}: {identities[index]} expected {expected}.");
            }

            checksum += unchecked((long)((identities[index] >> 32) ^ identities[index] ^ (ulong)(index + 1)));
        }

        return checksum;
    }


    /// <summary>
    /// Executes public-wrapper routed `SS8-8` bulk insertion and aggregates batch commit telemetry.<br/>
    /// The generated keys cover a fixed root-prefix interval so sorted and random rows share the same routed key set.<br/>
    /// </summary>
    /// <param name="index">The active public encoded index wrapper.</param>
    /// <param name="batches">The number of committed batches to execute.</param>
    /// <param name="itemsPerBatch">The number of encoded tuples inserted per committed batch.</param>
    /// <param name="prefixCount">The number of root prefixes covered by each generated batch.</param>
    /// <param name="order">The deterministic insertion order vector.</param>
    /// <param name="batchOffset">The logical batch offset used to keep warmup and measured keys disjoint.</param>
    /// <returns>Aggregated routed bulk-write telemetry.</returns>
    private static RoutedBulkWriteLoopTelemetry RunScalar8Scalar8RoutedBulkWriteLoop(
        Scalar8Scalar8Index index,
        int batches,
        int itemsPerBatch,
        int prefixCount,
        int[] order,
        int batchOffset)
    {
        return RunScalar8Scalar8RoutedBulkWriteLoop(
            index,
            batches,
            itemsPerBatch,
            prefixCount,
            order,
            batchOffset,
            Scalar8Scalar8BulkWriteLocalityMode.BatchMajor);
    }


    /// <summary>
    /// Executes public-wrapper routed `SS8-8` bulk insertion with a selected write-locality mode.<br/>
    /// `BatchMajor` preserves the normal benchmark order and commit cadence; `PrefixMajor` writes the same logical key set one root prefix at a time to test allocation locality without changing core allocator semantics.<br/>
    /// </summary>
    /// <param name="index">The active public encoded index wrapper.</param>
    /// <param name="batches">The number of logical generated batches.</param>
    /// <param name="itemsPerBatch">The number of encoded tuples generated per logical batch.</param>
    /// <param name="prefixCount">The number of root prefixes covered by each generated logical batch.</param>
    /// <param name="order">The deterministic insertion order vector used by batch-major mode.</param>
    /// <param name="batchOffset">The logical batch offset used to keep warmup and measured keys disjoint.</param>
    /// <param name="localityMode">The write ordering strategy used to feed the public batch API.</param>
    /// <returns>Aggregated routed bulk-write telemetry.</returns>
    private static RoutedBulkWriteLoopTelemetry RunScalar8Scalar8RoutedBulkWriteLoop(
        Scalar8Scalar8Index index,
        int batches,
        int itemsPerBatch,
        int prefixCount,
        int[] order,
        int batchOffset,
        Scalar8Scalar8BulkWriteLocalityMode localityMode)
    {
        return localityMode switch
        {
            Scalar8Scalar8BulkWriteLocalityMode.BatchMajor => RunScalar8Scalar8RoutedBulkWriteLoopBatchMajor(index, batches, itemsPerBatch, prefixCount, order, batchOffset),
            Scalar8Scalar8BulkWriteLocalityMode.PrefixMajor => RunScalar8Scalar8RoutedBulkWriteLoopPrefixMajor(index, batches, itemsPerBatch, prefixCount, batchOffset),
            _ => throw new ArgumentOutOfRangeException(nameof(localityMode), localityMode, "Unknown SS8-8 bulk write locality mode.")
        };
    }


    /// <summary>
    /// Executes public-wrapper routed `SS8-8` bulk insertion in logical batch order.<br/>
    /// This is the default benchmark shape and keeps one public durability commit per generated batch.<br/>
    /// </summary>
    /// <param name="index">The active public encoded index wrapper.</param>
    /// <param name="batches">The number of committed batches to execute.</param>
    /// <param name="itemsPerBatch">The number of encoded tuples inserted per committed batch.</param>
    /// <param name="prefixCount">The number of root prefixes covered by each generated batch.</param>
    /// <param name="order">The deterministic insertion order vector.</param>
    /// <param name="batchOffset">The logical batch offset used to keep warmup and measured keys disjoint.</param>
    /// <returns>Aggregated routed bulk-write telemetry.</returns>
    private static RoutedBulkWriteLoopTelemetry RunScalar8Scalar8RoutedBulkWriteLoopBatchMajor(
        Scalar8Scalar8Index index,
        int batches,
        int itemsPerBatch,
        int prefixCount,
        int[] order,
        int batchOffset)
    {
        RoutedBulkWriteLoopTelemetry telemetry = default;
        for (int batch = 0; batch < batches; batch++)
        {
            using Scalar8Scalar8Batch bulk = index.BeginBatch();
            int logicalBatch = batchOffset + batch;
            for (int i = 0; i < itemsPerBatch; i++)
            {
                ulong key = CreateRoutedBulkWriteKey(logicalBatch, order[i], itemsPerBatch, prefixCount);
                Scalar8Scalar8EncodedInsertResult result = bulk.InsertEncoded(key, key, allowDuplicateKeys: true);
                if (result.Outcome != Scalar8Scalar8EncodedInsertOutcome.Inserted)
                {
                    throw new InvalidDataException($"Expected routed bulk insert, got {result.Outcome}.");
                }

                telemetry.Checksum += unchecked((long)((key >> 32) ^ key ^ (ulong)(i + 1)));
            }

            Scalar8Scalar8BatchCommitResult commit = bulk.Commit();
            if (commit.InsertedCount != itemsPerBatch || commit.AttemptedInsertCount != itemsPerBatch)
            {
                throw new InvalidDataException($"Routed bulk batch inserted {commit.InsertedCount} of {commit.AttemptedInsertCount}; expected {itemsPerBatch}.");
            }

            AddRoutedBulkWriteCommit(commit, ref telemetry);
        }

        return telemetry;
    }


    /// <summary>
    /// Executes public-wrapper routed `SS8-8` bulk insertion one root prefix at a time.<br/>
    /// The generated key set is the same as batch-major mode, but each prefix is staged in its own public batch so replacement shelves for that read range can allocate near each other.<br/>
    /// </summary>
    /// <param name="index">The active public encoded index wrapper.</param>
    /// <param name="batches">The number of logical generated batches.</param>
    /// <param name="itemsPerBatch">The number of encoded tuples generated per logical batch.</param>
    /// <param name="prefixCount">The number of root prefixes covered by each generated logical batch.</param>
    /// <param name="batchOffset">The logical batch offset used to keep warmup and measured keys disjoint.</param>
    /// <returns>Aggregated routed bulk-write telemetry.</returns>
    private static RoutedBulkWriteLoopTelemetry RunScalar8Scalar8RoutedBulkWriteLoopPrefixMajor(
        Scalar8Scalar8Index index,
        int batches,
        int itemsPerBatch,
        int prefixCount,
        int batchOffset)
    {
        RoutedBulkWriteLoopTelemetry telemetry = default;
        int itemsPerPrefix = (itemsPerBatch + prefixCount - 1) / prefixCount;
        for (int prefix = 0; prefix < prefixCount; prefix++)
        {
            int firstOrdinal = prefix * itemsPerPrefix;
            int lastOrdinalExclusive = Math.Min(itemsPerBatch, firstOrdinal + itemsPerPrefix);
            if (firstOrdinal >= lastOrdinalExclusive)
            {
                continue;
            }

            using Scalar8Scalar8Batch bulk = index.BeginBatch();
            int attempted = 0;
            for (int batch = 0; batch < batches; batch++)
            {
                int logicalBatch = batchOffset + batch;
                for (int itemOrdinal = firstOrdinal; itemOrdinal < lastOrdinalExclusive; itemOrdinal++)
                {
                    ulong key = CreateRoutedBulkWriteKey(logicalBatch, itemOrdinal, itemsPerBatch, prefixCount);
                    Scalar8Scalar8EncodedInsertResult result = bulk.InsertEncoded(key, key, allowDuplicateKeys: true);
                    if (result.Outcome != Scalar8Scalar8EncodedInsertOutcome.Inserted)
                    {
                        throw new InvalidDataException($"Expected routed prefix-major bulk insert, got {result.Outcome}.");
                    }

                    attempted++;
                    telemetry.Checksum += unchecked((long)((key >> 32) ^ key ^ (ulong)(itemOrdinal + 1)));
                }
            }

            Scalar8Scalar8BatchCommitResult commit = bulk.Commit();
            if (commit.InsertedCount != attempted || commit.AttemptedInsertCount != attempted)
            {
                throw new InvalidDataException($"Routed prefix-major bulk batch inserted {commit.InsertedCount} of {commit.AttemptedInsertCount}; expected {attempted}.");
            }

            AddRoutedBulkWriteCommit(commit, ref telemetry);
        }

        return telemetry;
    }


    /// <summary>
    /// Executes typed unsigned public-wrapper routed `SS8-8` bulk insertion in logical batch order.<br/>
    /// The generated key set matches the encoded public benchmark so typed facade overhead can be isolated from storage shape.<br/>
    /// </summary>
    /// <param name="index">The active typed public unsigned index wrapper.</param>
    /// <param name="batches">The number of committed batches to execute.</param>
    /// <param name="itemsPerBatch">The number of tuples inserted per committed batch.</param>
    /// <param name="prefixCount">The number of root prefixes covered by each generated batch.</param>
    /// <param name="order">The deterministic insertion order vector.</param>
    /// <param name="batchOffset">The logical batch offset used to keep warmup and measured keys disjoint.</param>
    /// <returns>Aggregated routed bulk-write telemetry.</returns>
    private static RoutedBulkWriteLoopTelemetry RunUnsignedScalar8Scalar8RoutedBulkWriteLoop(
        UnsignedScalar8Scalar8Index index,
        int batches,
        int itemsPerBatch,
        int prefixCount,
        int[] order,
        int batchOffset)
    {
        RoutedBulkWriteLoopTelemetry telemetry = default;
        for (int batch = 0; batch < batches; batch++)
        {
            using UnsignedScalar8Scalar8Batch bulk = index.BeginBatch();
            int logicalBatch = batchOffset + batch;
            for (int i = 0; i < itemsPerBatch; i++)
            {
                ulong key = CreateRoutedBulkWriteKey(logicalBatch, order[i], itemsPerBatch, prefixCount);
                UnsignedScalar8Scalar8InsertResult result = bulk.Insert(key, key, allowDuplicateKeys: true);
                if (result.Outcome != UnsignedScalar8Scalar8InsertOutcome.Inserted)
                {
                    throw new InvalidDataException($"Expected typed routed bulk insert, got {result.Outcome}.");
                }

                telemetry.Checksum += unchecked((long)((key >> 32) ^ key ^ (ulong)(i + 1)));
            }

            UnsignedScalar8Scalar8BatchCommitResult commit = bulk.Commit();
            if (commit.InsertedCount != itemsPerBatch || commit.AttemptedInsertCount != itemsPerBatch)
            {
                throw new InvalidDataException($"Typed routed bulk batch inserted {commit.InsertedCount} of {commit.AttemptedInsertCount}; expected {itemsPerBatch}.");
            }

            AddRoutedBulkWriteCommit(commit, ref telemetry);
        }

        return telemetry;
    }


    /// <summary>
    /// Executes harness-level `SS16-16` routed bulk insertion with one session durability batch per logical batch.<br/>
    /// The implementation intentionally uses the completed walked insert path directly, so this is a storage-path parity anchor rather than a cached public batch API result.<br/>
    /// </summary>
    /// <param name="session">The active LibraDex session.</param>
    /// <param name="rootRouterOffset">The root router offset for the `SS16-16` index.</param>
    /// <param name="batches">The number of committed logical batches to execute.</param>
    /// <param name="itemsPerBatch">The number of encoded tuples inserted per committed batch.</param>
    /// <param name="prefixCount">The number of root prefixes covered by each generated batch.</param>
    /// <param name="order">The deterministic insertion order vector.</param>
    /// <param name="batchOffset">The logical batch offset used to keep warmup and measured keys disjoint.</param>
    /// <param name="profile">The `SS16-16` shelf profile.</param>
    /// <param name="includeAttribution">True to collect Stopwatch-based attribution timings in addition to cheap structural counts.</param>
    /// <returns>Aggregated routed bulk-write telemetry.</returns>
    private static RoutedBulkWriteLoopTelemetry RunScalar16Scalar16RoutedBulkWriteLoop(
        LibraDexFileSession session,
        long rootRouterOffset,
        int batches,
        int itemsPerBatch,
        int prefixCount,
        int[] order,
        int batchOffset,
        Scalar16Scalar16Profile profile,
        bool includeAttribution)
    {
        RoutedBulkWriteLoopTelemetry telemetry = default;
        for (int batch = 0; batch < batches; batch++)
        {
            Dictionary<long, Scalar16Scalar16BatchShelfCacheEntry> dirtyShelves = [];
            Dictionary<Scalar16Scalar16BatchRouteCacheKey, Scalar16Scalar16RouteTarget> routeTargetCache = [];
            using LibraDexFileSessionDurabilityBatch durabilityBatch = session.BeginDurabilityBatch();
            int logicalBatch = batchOffset + batch;
            for (int i = 0; i < itemsPerBatch; i++)
            {
                ulong keyHigh = CreateRoutedBulkWriteKey(logicalBatch, order[i], itemsPerBatch, prefixCount);
                ulong keyLow = CreateSqliteScalar16Scalar16LowHalf(keyHigh);
                byte rootPrefix = (byte)(keyHigh >> 56);
                Scalar16Scalar16RouteTarget target;
                if (TryGetCachedScalar16Scalar16RouteTarget(routeTargetCache, keyHigh, keyLow, out target))
                {
                    telemetry.RouteTargetCacheHitCount++;
                }
                else
                {
                    telemetry.RouteTargetCacheMissCount++;
                    long rootLookupStart = includeAttribution ? Stopwatch.GetTimestamp() : 0;
                    if (session.FindRouterTarget(rootRouterOffset, rootPrefix) == 0)
                    {
                        telemetry.RootRouteCreateCount++;
                        _ = session.CreateScalar16Scalar16ShelfAndLinkRootRoute(rootRouterOffset, rootPrefix, profile, itemCount: 0);
                    }

                    if (includeAttribution)
                    {
                        telemetry.RootLookupTicks += Stopwatch.GetTimestamp() - rootLookupStart;
                    }

                    long routeWalkStart = includeAttribution ? Stopwatch.GetTimestamp() : 0;
                    Scalar16Scalar16RoutePathTarget pathTarget = session.WalkScalar16Scalar16RoutePathTarget(rootRouterOffset, keyHigh, keyLow, maxRouterHops: 8);
                    if (includeAttribution)
                    {
                        telemetry.RouteWalkTicks += Stopwatch.GetTimestamp() - routeWalkStart;
                    }

                    telemetry.RouteWalkCount++;
                    target = pathTarget.Target;
                    if (target.Kind == Scalar16Scalar16RouteTargetKind.Shelf)
                    {
                        routeTargetCache[Scalar16Scalar16BatchRouteCacheKey.Create(keyHigh, keyLow, target.RouterDepth)] = target;
                    }
                }

                bool insertedByCache = false;
                if (target.Kind == Scalar16Scalar16RouteTargetKind.Shelf)
                {
                    if (!dirtyShelves.TryGetValue(target.Offset, out Scalar16Scalar16BatchShelfCacheEntry? entry))
                    {
                        telemetry.ShelfCacheMissCount++;
                        byte[] shelfBytes = session.ReadScalar16Scalar16ShelfBytesForBatch(target.Offset, profile);
                        entry = new Scalar16Scalar16BatchShelfCacheEntry(shelfBytes);
                        dirtyShelves[target.Offset] = entry;
                    }
                    else
                    {
                        telemetry.ShelfCacheHitCount++;
                    }

                    long shelfInsertStart = includeAttribution ? Stopwatch.GetTimestamp() : 0;
                    Scalar16Scalar16 shelf = new(entry.Bytes, profile);
                    Scalar16Scalar16InsertResult cachedResult = shelf.InsertWithMutationBounds(keyHigh, keyLow, keyHigh, keyLow, allowDuplicateKeys: true, out Scalar16Scalar16MutationBounds mutationBounds);
                    if (includeAttribution)
                    {
                        telemetry.ShelfInsertTicks += Stopwatch.GetTimestamp() - shelfInsertStart;
                    }
                    if (cachedResult == Scalar16Scalar16InsertResult.Inserted)
                    {
                        entry.Dirty = true;
                        entry.Include(mutationBounds);
                        insertedByCache = true;
                        telemetry.CachedInsertCount++;
                    }
                    else if (cachedResult == Scalar16Scalar16InsertResult.Full)
                    {
                        telemetry.CacheFullFallbackCount++;
                        dirtyShelves.Remove(target.Offset);
                        routeTargetCache.Clear();
                        Scalar16Scalar16RoutedInsertResult result = session.InsertWalkedRoutedScalar16Scalar16FromShelfImage(rootRouterOffset, profile, target.Offset, entry.Bytes, keyHigh, keyLow, keyHigh, keyLow, allowDuplicateKeys: true, maxRouterHops: 8);
                        if (result.InsertResult != Scalar16Scalar16InsertResult.Inserted)
                        {
                            throw new InvalidDataException($"Expected SS16-16 routed bulk split insert, got {result.Kind}/{result.InsertResult}.");
                        }

                        AddScalar16Scalar16RoutedBulkWriteFallback(result, ref telemetry);
                        insertedByCache = true;
                    }
                    else
                    {
                        throw new InvalidDataException($"Expected SS16-16 cached routed bulk insert, got {cachedResult}.");
                    }
                }

                if (!insertedByCache)
                {
                    telemetry.WalkedFallbackCount++;
                    Scalar16Scalar16RoutedInsertResult result = session.InsertWalkedRoutedScalar16Scalar16(rootRouterOffset, profile, keyHigh, keyLow, keyHigh, keyLow, allowDuplicateKeys: true, maxRouterHops: 8);
                    if (result.InsertResult != Scalar16Scalar16InsertResult.Inserted)
                    {
                        throw new InvalidDataException($"Expected SS16-16 routed bulk insert, got {result.Kind}/{result.InsertResult}.");
                    }

                    AddScalar16Scalar16RoutedBulkWriteFallback(result, ref telemetry);
                    routeTargetCache.Clear();
                }

                telemetry.InsertedCount++;
                telemetry.Checksum += unchecked((long)(keyHigh ^ keyLow ^ (ulong)(i + 1)));
            }

            foreach (KeyValuePair<long, Scalar16Scalar16BatchShelfCacheEntry> pair in dirtyShelves)
            {
                StageScalar16Scalar16ShelfDeltaRewrite(session, pair.Key, profile, pair.Value, ref telemetry);
            }

            (DataKernelCommitTelemetry commit, long deferredRequests, _) = durabilityBatch.Commit();
            foreach (KeyValuePair<long, Scalar16Scalar16BatchShelfCacheEntry> pair in dirtyShelves)
            {
                session.StoreScalar16Scalar16CleanShelfBytesForBatch(pair.Key, profile, pair.Value.Bytes);
            }

            telemetry.CommitCount++;
            telemetry.DeferredCommitRequests += deferredRequests;
            telemetry.WriteCallCount += commit.WriteCallCount;
            telemetry.BytesWritten += commit.BytesWritten;
            telemetry.SetLengthCallCount += commit.SetLengthCallCount;
        }

        return telemetry;
    }


    /// <summary>
    /// Executes harness-level `SS16-8` routed bulk insertion with one session durability batch per logical batch.<br/>
    /// The implementation uses a batch-local widened-key route-target cache plus a mutable shelf cache so the intermediate shape can be compared against `SS16-16` using the same lessons.<br/>
    /// </summary>
    /// <param name="session">The active LibraDex session.</param>
    /// <param name="rootRouterOffset">The root router offset for the `SS16-8` index.</param>
    /// <param name="batches">The number of committed logical batches to execute.</param>
    /// <param name="itemsPerBatch">The number of encoded tuples inserted per committed batch.</param>
    /// <param name="prefixCount">The number of root prefixes covered by each generated batch.</param>
    /// <param name="order">The deterministic insertion order vector.</param>
    /// <param name="batchOffset">The logical batch offset used to keep warmup and measured keys disjoint.</param>
    /// <param name="profile">The `SS16-8` shelf profile.</param>
    /// <param name="includeAttribution">True to collect Stopwatch-based attribution timings in addition to cheap structural counts.</param>
    /// <returns>Aggregated routed bulk-write telemetry.</returns>
    private static RoutedBulkWriteLoopTelemetry RunScalar16Scalar8RoutedBulkWriteLoop(
        LibraDexFileSession session,
        long rootRouterOffset,
        int batches,
        int itemsPerBatch,
        int prefixCount,
        int[] order,
        int batchOffset,
        Scalar16Scalar8Profile profile,
        bool includeAttribution)
    {
        RoutedBulkWriteLoopTelemetry telemetry = default;
        for (int batch = 0; batch < batches; batch++)
        {
            Dictionary<long, Scalar16Scalar8BatchShelfCacheEntry> dirtyShelves = [];
            Dictionary<Scalar16Scalar16BatchRouteCacheKey, Scalar16Scalar8RouteTarget> routeTargetCache = [];
            using LibraDexFileSessionDurabilityBatch durabilityBatch = session.BeginDurabilityBatch();
            int logicalBatch = batchOffset + batch;
            for (int i = 0; i < itemsPerBatch; i++)
            {
                ulong keyHigh = CreateRoutedBulkWriteKey(logicalBatch, order[i], itemsPerBatch, prefixCount);
                ulong keyLow = CreateSqliteScalar16Scalar16LowHalf(keyHigh);
                ulong identity = keyHigh;
                byte rootPrefix = (byte)(keyHigh >> 56);
                Scalar16Scalar8RouteTarget target;
                if (TryGetCachedScalar16Scalar8RouteTarget(routeTargetCache, keyHigh, keyLow, out target))
                {
                    telemetry.RouteTargetCacheHitCount++;
                }
                else
                {
                    telemetry.RouteTargetCacheMissCount++;
                    long rootLookupStart = includeAttribution ? Stopwatch.GetTimestamp() : 0;
                    if (session.FindRouterTarget(rootRouterOffset, rootPrefix) == 0)
                    {
                        telemetry.RootRouteCreateCount++;
                        _ = session.CreateScalar16Scalar8ShelfAndLinkRootRoute(rootRouterOffset, rootPrefix, profile, itemCount: 0);
                    }

                    if (includeAttribution)
                    {
                        telemetry.RootLookupTicks += Stopwatch.GetTimestamp() - rootLookupStart;
                    }

                    long routeWalkStart = includeAttribution ? Stopwatch.GetTimestamp() : 0;
                    Scalar16Scalar8RoutePathTarget pathTarget = session.WalkScalar16Scalar8RoutePathTarget(rootRouterOffset, keyHigh, keyLow, maxRouterHops: 8);
                    if (includeAttribution)
                    {
                        telemetry.RouteWalkTicks += Stopwatch.GetTimestamp() - routeWalkStart;
                    }

                    telemetry.RouteWalkCount++;
                    target = pathTarget.Target;
                    if (target.Kind == Scalar16Scalar8RouteTargetKind.Shelf)
                    {
                        routeTargetCache[Scalar16Scalar16BatchRouteCacheKey.Create(keyHigh, keyLow, target.RouterDepth)] = target;
                    }
                }

                bool insertedByCache = false;
                if (target.Kind == Scalar16Scalar8RouteTargetKind.Shelf)
                {
                    if (!dirtyShelves.TryGetValue(target.Offset, out Scalar16Scalar8BatchShelfCacheEntry? entry))
                    {
                        telemetry.ShelfCacheMissCount++;
                        byte[] shelfBytes = session.ReadScalar16Scalar8ShelfBytesForBatch(target.Offset, profile);
                        entry = new Scalar16Scalar8BatchShelfCacheEntry(shelfBytes);
                        dirtyShelves[target.Offset] = entry;
                    }
                    else
                    {
                        telemetry.ShelfCacheHitCount++;
                    }

                    long shelfInsertStart = includeAttribution ? Stopwatch.GetTimestamp() : 0;
                    Scalar16Scalar8 shelf = new(entry.Bytes, profile);
                    Scalar16Scalar8InsertResult cachedResult = shelf.InsertWithMutationBounds(keyHigh, keyLow, identity, allowDuplicateKeys: true, out Scalar16Scalar8MutationBounds mutationBounds);
                    if (includeAttribution)
                    {
                        telemetry.ShelfInsertTicks += Stopwatch.GetTimestamp() - shelfInsertStart;
                    }

                    if (cachedResult == Scalar16Scalar8InsertResult.Inserted)
                    {
                        entry.Dirty = true;
                        entry.Include(mutationBounds);
                        insertedByCache = true;
                        telemetry.CachedInsertCount++;
                    }
                    else if (cachedResult == Scalar16Scalar8InsertResult.Full)
                    {
                        telemetry.CacheFullFallbackCount++;
                        dirtyShelves.Remove(target.Offset);
                        routeTargetCache.Clear();
                        Scalar16Scalar8RoutedInsertResult result = session.InsertWalkedRoutedScalar16Scalar8FromShelfImage(rootRouterOffset, profile, target.Offset, entry.Bytes, keyHigh, keyLow, identity, allowDuplicateKeys: true, maxRouterHops: 8);
                        if (result.InsertResult != Scalar16Scalar8InsertResult.Inserted)
                        {
                            throw new InvalidDataException($"Expected SS16-8 routed bulk split insert, got {result.Kind}/{result.InsertResult}.");
                        }

                        AddScalar16Scalar8RoutedBulkWriteFallback(result, ref telemetry);
                        insertedByCache = true;
                    }
                    else
                    {
                        throw new InvalidDataException($"Expected SS16-8 cached routed bulk insert, got {cachedResult}.");
                    }
                }

                if (!insertedByCache)
                {
                    telemetry.WalkedFallbackCount++;
                    Scalar16Scalar8RoutedInsertResult result = session.InsertWalkedRoutedScalar16Scalar8(rootRouterOffset, profile, keyHigh, keyLow, identity, allowDuplicateKeys: true, maxRouterHops: 8);
                    if (result.InsertResult != Scalar16Scalar8InsertResult.Inserted)
                    {
                        throw new InvalidDataException($"Expected SS16-8 routed bulk insert, got {result.Kind}/{result.InsertResult}.");
                    }

                    AddScalar16Scalar8RoutedBulkWriteFallback(result, ref telemetry);
                    routeTargetCache.Clear();
                }

                telemetry.InsertedCount++;
                telemetry.Checksum += unchecked((long)(keyHigh ^ keyLow ^ identity ^ (ulong)(i + 1)));
            }

            foreach (KeyValuePair<long, Scalar16Scalar8BatchShelfCacheEntry> pair in dirtyShelves)
            {
                StageScalar16Scalar8ShelfDeltaRewrite(session, pair.Key, profile, pair.Value, ref telemetry);
            }

            (DataKernelCommitTelemetry commit, long deferredRequests, _) = durabilityBatch.Commit();
            foreach (KeyValuePair<long, Scalar16Scalar8BatchShelfCacheEntry> pair in dirtyShelves)
            {
                session.StoreScalar16Scalar8CleanShelfBytesForBatch(pair.Key, profile, pair.Value.Bytes);
            }

            telemetry.CommitCount++;
            telemetry.DeferredCommitRequests += deferredRequests;
            telemetry.WriteCallCount += commit.WriteCallCount;
            telemetry.BytesWritten += commit.BytesWritten;
            telemetry.SetLengthCallCount += commit.SetLengthCallCount;
        }

        return telemetry;
    }


    /// <summary>
    /// Executes harness-level `SS8-16` routed bulk insertion with one session durability batch per logical batch.<br/>
    /// The implementation uses an 8-byte-key route-target cache plus a mutable shelf cache so this shape can be compared against `SS16-8` and `SS16-16` on the same batch-publication model.<br/>
    /// </summary>
    /// <param name="session">The active LibraDex session.</param>
    /// <param name="rootRouterOffset">The root router offset for the `SS8-16` index.</param>
    /// <param name="batches">The number of committed logical batches to execute.</param>
    /// <param name="itemsPerBatch">The number of encoded tuples inserted per committed batch.</param>
    /// <param name="prefixCount">The number of root prefixes covered by each generated batch.</param>
    /// <param name="order">The deterministic insertion order vector.</param>
    /// <param name="batchOffset">The logical batch offset used to keep warmup and measured keys disjoint.</param>
    /// <param name="profile">The `SS8-16` shelf profile.</param>
    /// <param name="includeAttribution">True to collect Stopwatch-based attribution timings in addition to cheap structural counts.</param>
    /// <returns>Aggregated routed bulk-write telemetry.</returns>
    private static RoutedBulkWriteLoopTelemetry RunScalar8Scalar16RoutedBulkWriteLoop(
        LibraDexFileSession session,
        long rootRouterOffset,
        int batches,
        int itemsPerBatch,
        int prefixCount,
        int[] order,
        int batchOffset,
        Scalar8Scalar16Profile profile,
        bool includeAttribution)
    {
        RoutedBulkWriteLoopTelemetry telemetry = default;
        for (int batch = 0; batch < batches; batch++)
        {
            Dictionary<long, Scalar8Scalar16BatchShelfCacheEntry> dirtyShelves = [];
            Dictionary<Scalar8Scalar16BatchRouteCacheKey, Scalar8Scalar16RouteTarget> routeTargetCache = [];
            using LibraDexFileSessionDurabilityBatch durabilityBatch = session.BeginDurabilityBatch();
            int logicalBatch = batchOffset + batch;
            for (int i = 0; i < itemsPerBatch; i++)
            {
                ulong key = CreateRoutedBulkWriteKey(logicalBatch, order[i], itemsPerBatch, prefixCount);
                ulong identityHigh = key;
                ulong identityLow = CreateSqliteScalar16Scalar16LowHalf(key);
                byte rootPrefix = (byte)(key >> 56);
                Scalar8Scalar16RouteTarget target;
                if (TryGetCachedScalar8Scalar16RouteTarget(routeTargetCache, key, out target))
                {
                    telemetry.RouteTargetCacheHitCount++;
                }
                else
                {
                    telemetry.RouteTargetCacheMissCount++;
                    long rootLookupStart = includeAttribution ? Stopwatch.GetTimestamp() : 0;
                    if (session.FindRouterTarget(rootRouterOffset, rootPrefix) == 0)
                    {
                        telemetry.RootRouteCreateCount++;
                        _ = session.CreateScalar8Scalar16ShelfAndLinkRootRoute(rootRouterOffset, rootPrefix, profile, itemCount: 0);
                    }

                    if (includeAttribution)
                    {
                        telemetry.RootLookupTicks += Stopwatch.GetTimestamp() - rootLookupStart;
                    }

                    long routeWalkStart = includeAttribution ? Stopwatch.GetTimestamp() : 0;
                    Scalar8Scalar16RoutePathTarget pathTarget = session.WalkScalar8Scalar16RoutePathTarget(rootRouterOffset, key, maxRouterHops: 8);
                    if (includeAttribution)
                    {
                        telemetry.RouteWalkTicks += Stopwatch.GetTimestamp() - routeWalkStart;
                    }

                    telemetry.RouteWalkCount++;
                    target = pathTarget.Target;
                    if (target.Kind == Scalar8Scalar16RouteTargetKind.Shelf)
                    {
                        routeTargetCache[Scalar8Scalar16BatchRouteCacheKey.Create(key, target.RouterDepth)] = target;
                    }
                }

                bool insertedByCache = false;
                if (target.Kind == Scalar8Scalar16RouteTargetKind.Shelf)
                {
                    if (!dirtyShelves.TryGetValue(target.Offset, out Scalar8Scalar16BatchShelfCacheEntry? entry))
                    {
                        telemetry.ShelfCacheMissCount++;
                        byte[] shelfBytes = session.ReadScalar8Scalar16ShelfBytesForBatch(target.Offset, profile);
                        entry = new Scalar8Scalar16BatchShelfCacheEntry(shelfBytes);
                        dirtyShelves[target.Offset] = entry;
                    }
                    else
                    {
                        telemetry.ShelfCacheHitCount++;
                    }

                    long shelfInsertStart = includeAttribution ? Stopwatch.GetTimestamp() : 0;
                    Scalar8Scalar16 shelf = new(entry.Bytes, profile);
                    Scalar8Scalar16InsertResult cachedResult = shelf.InsertWithMutationBounds(key, identityHigh, identityLow, allowDuplicateKeys: true, out Scalar8Scalar16MutationBounds mutationBounds);
                    if (includeAttribution)
                    {
                        telemetry.ShelfInsertTicks += Stopwatch.GetTimestamp() - shelfInsertStart;
                    }

                    if (cachedResult == Scalar8Scalar16InsertResult.Inserted)
                    {
                        entry.Dirty = true;
                        entry.Include(mutationBounds);
                        insertedByCache = true;
                        telemetry.CachedInsertCount++;
                    }
                    else if (cachedResult == Scalar8Scalar16InsertResult.Full)
                    {
                        telemetry.CacheFullFallbackCount++;
                        dirtyShelves.Remove(target.Offset);
                        routeTargetCache.Clear();
                        Scalar8Scalar16RoutedInsertResult result = session.InsertWalkedRoutedScalar8Scalar16FromShelfImage(rootRouterOffset, profile, target.Offset, entry.Bytes, key, identityHigh, identityLow, allowDuplicateKeys: true, maxRouterHops: 8);
                        if (result.InsertResult != Scalar8Scalar16InsertResult.Inserted)
                        {
                            throw new InvalidDataException($"Expected SS8-16 routed bulk split insert, got {result.Kind}/{result.InsertResult}.");
                        }

                        AddScalar8Scalar16RoutedBulkWriteFallback(result, ref telemetry);
                        insertedByCache = true;
                    }
                    else
                    {
                        throw new InvalidDataException($"Expected SS8-16 cached routed bulk insert, got {cachedResult}.");
                    }
                }

                if (!insertedByCache)
                {
                    telemetry.WalkedFallbackCount++;
                    Scalar8Scalar16RoutedInsertResult result = session.InsertWalkedRoutedScalar8Scalar16(rootRouterOffset, profile, key, identityHigh, identityLow, allowDuplicateKeys: true, maxRouterHops: 8);
                    if (result.InsertResult != Scalar8Scalar16InsertResult.Inserted)
                    {
                        throw new InvalidDataException($"Expected SS8-16 routed bulk insert, got {result.Kind}/{result.InsertResult}.");
                    }

                    AddScalar8Scalar16RoutedBulkWriteFallback(result, ref telemetry);
                    routeTargetCache.Clear();
                }

                telemetry.InsertedCount++;
                telemetry.Checksum += unchecked((long)(key ^ identityHigh ^ identityLow ^ (ulong)(i + 1)));
            }

            foreach (KeyValuePair<long, Scalar8Scalar16BatchShelfCacheEntry> pair in dirtyShelves)
            {
                StageScalar8Scalar16ShelfDeltaRewrite(session, pair.Key, profile, pair.Value, ref telemetry);
            }

            (DataKernelCommitTelemetry commit, long deferredRequests, _) = durabilityBatch.Commit();
            foreach (KeyValuePair<long, Scalar8Scalar16BatchShelfCacheEntry> pair in dirtyShelves)
            {
                session.StoreScalar8Scalar16CleanShelfBytesForBatch(pair.Key, profile, pair.Value.Bytes);
            }

            telemetry.CommitCount++;
            telemetry.DeferredCommitRequests += deferredRequests;
            telemetry.WriteCallCount += commit.WriteCallCount;
            telemetry.BytesWritten += commit.BytesWritten;
            telemetry.SetLengthCallCount += commit.SetLengthCallCount;
        }

        return telemetry;
    }


    /// <summary>
    /// Executes harness-level `FS32-8` routed bulk insertion with one session durability batch per logical batch.<br/>
    /// The implementation uses a 32-byte route-target cache plus a mutable shelf cache so 32-byte fixed-key performance is compared with the same publish strategy as the scalar fixed-width shapes.<br/>
    /// </summary>
    /// <param name="session">The active LibraDex session.</param>
    /// <param name="rootRouterOffset">The root router offset for the `FS32-8` index.</param>
    /// <param name="batches">The number of committed logical batches to execute.</param>
    /// <param name="itemsPerBatch">The number of encoded tuples inserted per committed batch.</param>
    /// <param name="prefixCount">The number of root prefixes covered by each generated batch.</param>
    /// <param name="order">The deterministic insertion order vector.</param>
    /// <param name="batchOffset">The logical batch offset used to keep warmup and measured keys disjoint.</param>
    /// <param name="profile">The `FS32-8` shelf profile.</param>
    /// <param name="includeAttribution">True to collect Stopwatch-based attribution timings in addition to cheap structural counts.</param>
    /// <returns>Aggregated routed bulk-write telemetry.</returns>
    private static RoutedBulkWriteLoopTelemetry RunFixed32Scalar8RoutedBulkWriteLoop(
        LibraDexFileSession session,
        long rootRouterOffset,
        int batches,
        int itemsPerBatch,
        int prefixCount,
        int[] order,
        int batchOffset,
        Fixed32Scalar8Profile profile,
        bool includeAttribution)
    {
        RoutedBulkWriteLoopTelemetry telemetry = default;
        for (int batch = 0; batch < batches; batch++)
        {
            Dictionary<long, Fixed32Scalar8BatchShelfCacheEntry> dirtyShelves = [];
            Dictionary<Fixed32Scalar8BatchRouteCacheKey, Fixed32Scalar8RouteTarget> routeTargetCache = [];
            using LibraDexFileSessionDurabilityBatch durabilityBatch = session.BeginDurabilityBatch();
            int logicalBatch = batchOffset + batch;
            for (int i = 0; i < itemsPerBatch; i++)
            {
                CreateFixed32Scalar8BulkWriteKey(logicalBatch, order[i], itemsPerBatch, prefixCount, out ulong key0, out ulong key1, out ulong key2, out ulong key3);
                ulong identity = Fixed32Scalar8Layout.EncodeUnsignedScalar8(key0);
                byte rootPrefix = (byte)(key0 >> 56);
                Fixed32Scalar8RouteTarget target;
                if (TryGetCachedFixed32Scalar8RouteTarget(routeTargetCache, key0, key1, key2, key3, out target))
                {
                    telemetry.RouteTargetCacheHitCount++;
                }
                else
                {
                    telemetry.RouteTargetCacheMissCount++;
                    long rootLookupStart = includeAttribution ? Stopwatch.GetTimestamp() : 0;
                    if (session.FindRouterTarget(rootRouterOffset, rootPrefix) == 0)
                    {
                        telemetry.RootRouteCreateCount++;
                        _ = session.CreateFixed32Scalar8ShelfAndLinkRootRoute(rootRouterOffset, rootPrefix, profile, itemCount: 0);
                    }

                    if (includeAttribution)
                    {
                        telemetry.RootLookupTicks += Stopwatch.GetTimestamp() - rootLookupStart;
                    }

                    long routeWalkStart = includeAttribution ? Stopwatch.GetTimestamp() : 0;
                    Fixed32Scalar8RoutePathTarget pathTarget = session.WalkFixed32Scalar8RoutePathTarget(rootRouterOffset, key0, key1, key2, key3, maxRouterHops: 32);
                    if (includeAttribution)
                    {
                        telemetry.RouteWalkTicks += Stopwatch.GetTimestamp() - routeWalkStart;
                    }

                    telemetry.RouteWalkCount++;
                    target = pathTarget.Target;
                    if (target.Kind == Fixed32Scalar8RouteTargetKind.Shelf)
                    {
                        routeTargetCache[Fixed32Scalar8BatchRouteCacheKey.Create(key0, key1, key2, key3, target.RouterDepth)] = target;
                    }
                }

                bool insertedByCache = false;
                if (target.Kind == Fixed32Scalar8RouteTargetKind.Shelf)
                {
                    if (!dirtyShelves.TryGetValue(target.Offset, out Fixed32Scalar8BatchShelfCacheEntry? entry))
                    {
                        telemetry.ShelfCacheMissCount++;
                        byte[] shelfBytes = session.ReadFixed32Scalar8ShelfBytesForBatch(target.Offset, profile);
                        entry = new Fixed32Scalar8BatchShelfCacheEntry(shelfBytes);
                        dirtyShelves[target.Offset] = entry;
                    }
                    else
                    {
                        telemetry.ShelfCacheHitCount++;
                    }

                    long shelfInsertStart = includeAttribution ? Stopwatch.GetTimestamp() : 0;
                    Fixed32Scalar8 shelf = new(entry.Bytes, profile);
                    Fixed32Scalar8InsertResult cachedResult = shelf.InsertWithMutationBounds(key0, key1, key2, key3, identity, allowDuplicateKeys: true, out Fixed32Scalar8MutationBounds mutationBounds);
                    if (includeAttribution)
                    {
                        telemetry.ShelfInsertTicks += Stopwatch.GetTimestamp() - shelfInsertStart;
                    }

                    if (cachedResult == Fixed32Scalar8InsertResult.Inserted)
                    {
                        entry.Dirty = true;
                        entry.Include(mutationBounds);
                        insertedByCache = true;
                        telemetry.CachedInsertCount++;
                    }
                    else if (cachedResult == Fixed32Scalar8InsertResult.Full)
                    {
                        telemetry.CacheFullFallbackCount++;
                        dirtyShelves.Remove(target.Offset);
                        routeTargetCache.Clear();
                        Fixed32Scalar8RoutedInsertResult result = session.InsertWalkedRoutedFixed32Scalar8FromShelfImage(rootRouterOffset, profile, target.Offset, entry.Bytes, key0, key1, key2, key3, identity, allowDuplicateKeys: true, maxRouterHops: 32);
                        if (result.InsertResult != Fixed32Scalar8InsertResult.Inserted)
                        {
                            throw new InvalidDataException($"Expected FS32-8 routed bulk split insert, got {result.Kind}/{result.InsertResult}.");
                        }

                        AddFixed32Scalar8RoutedBulkWriteFallback(result, ref telemetry);
                        insertedByCache = true;
                    }
                    else
                    {
                        throw new InvalidDataException($"Expected FS32-8 cached routed bulk insert, got {cachedResult}.");
                    }
                }

                if (!insertedByCache)
                {
                    telemetry.WalkedFallbackCount++;
                    Fixed32Scalar8RoutedInsertResult result = session.InsertWalkedRoutedFixed32Scalar8(rootRouterOffset, profile, key0, key1, key2, key3, identity, allowDuplicateKeys: true, maxRouterHops: 32);
                    if (result.InsertResult != Fixed32Scalar8InsertResult.Inserted)
                    {
                        throw new InvalidDataException($"Expected FS32-8 routed bulk insert, got {result.Kind}/{result.InsertResult}.");
                    }

                    AddFixed32Scalar8RoutedBulkWriteFallback(result, ref telemetry);
                    routeTargetCache.Clear();
                }

                telemetry.InsertedCount++;
                telemetry.Checksum += unchecked((long)(key0 ^ key1 ^ key2 ^ key3 ^ identity ^ (ulong)(i + 1)));
            }

            foreach (KeyValuePair<long, Fixed32Scalar8BatchShelfCacheEntry> pair in dirtyShelves)
            {
                StageFixed32Scalar8ShelfDeltaRewrite(session, pair.Key, profile, pair.Value, ref telemetry);
            }

            (DataKernelCommitTelemetry commit, long deferredRequests, _) = durabilityBatch.Commit();
            foreach (KeyValuePair<long, Fixed32Scalar8BatchShelfCacheEntry> pair in dirtyShelves)
            {
                session.StoreFixed32Scalar8CleanShelfBytesForBatch(pair.Key, profile, pair.Value.Bytes);
            }

            telemetry.CommitCount++;
            telemetry.DeferredCommitRequests += deferredRequests;
            telemetry.WriteCallCount += commit.WriteCallCount;
            telemetry.BytesWritten += commit.BytesWritten;
            telemetry.SetLengthCallCount += commit.SetLengthCallCount;
        }

        return telemetry;
    }


    /// <summary>
    /// Stages changed regions for one dirty cached `SS16-16` shelf instead of blindly staging the full shelf extent.<br/>
    /// The current shelf image is validated as a whole, then the staged ranges are limited to changed header, slot, and item payload spans when those ranges are smaller than the full shelf.<br/>
    /// </summary>
    /// <param name="session">The active LibraDex session.</param>
    /// <param name="shelfOffset">The file offset of the dirty shelf.</param>
    /// <param name="profile">The `SS16-16` shelf profile that defines the shelf regions.</param>
    /// <param name="entry">The dirty cached shelf entry with tracked mutation bounds.</param>
    private static void StageScalar16Scalar16ShelfDeltaRewrite(
        LibraDexFileSession session,
        long shelfOffset,
        Scalar16Scalar16Profile profile,
        Scalar16Scalar16BatchShelfCacheEntry entry,
        ref RoutedBulkWriteLoopTelemetry telemetry)
    {
        if (!entry.Dirty)
        {
            return;
        }

        byte[] currentBytes = entry.Bytes;
        Scalar16Scalar16ReadOnly readOnly = new(currentBytes, profile);
        if (!readOnly.IsValid)
        {
            throw new InvalidDataException("The batch-local SS16-16 shelf cache attempted to stage invalid shelf bytes.");
        }

        ChangedSpan headerSpan = entry.GetHeaderSpan();
        ChangedSpan slotSpan = entry.GetSlotSpan();
        ChangedSpan itemSpan = entry.GetItemSpan();
        int deltaBytes = headerSpan.Length + slotSpan.Length + itemSpan.Length;
        if (deltaBytes <= 0)
        {
            return;
        }

        if (deltaBytes >= profile.ShelfExtentSize)
        {
            session.StageScalar16Scalar16ShelfRewriteForBatch(shelfOffset, profile, currentBytes);
            telemetry.FullShelfStageCount++;
            return;
        }

        telemetry.DeltaShelfStageCount++;
        AddScalar16Scalar16ChangedSpanAttribution(headerSpan, ref telemetry);
        AddScalar16Scalar16ChangedSpanAttribution(slotSpan, ref telemetry);
        AddScalar16Scalar16ChangedSpanAttribution(itemSpan, ref telemetry);
        StageScalar16Scalar16ShelfChangedSpan(session, shelfOffset, profile, currentBytes, headerSpan);
        StageScalar16Scalar16ShelfChangedSpan(session, shelfOffset, profile, currentBytes, slotSpan);
        StageScalar16Scalar16ShelfChangedSpan(session, shelfOffset, profile, currentBytes, itemSpan);
    }


    private static void AddScalar16Scalar16ChangedSpanAttribution(
        ChangedSpan span,
        ref RoutedBulkWriteLoopTelemetry telemetry)
    {
        if (span.Length == 0)
        {
            return;
        }

        telemetry.DeltaChangedSpanCount++;
        telemetry.DeltaChangedBytes += span.Length;
    }


    private static void StageScalar16Scalar8ShelfDeltaRewrite(
        LibraDexFileSession session,
        long shelfOffset,
        Scalar16Scalar8Profile profile,
        Scalar16Scalar8BatchShelfCacheEntry entry,
        ref RoutedBulkWriteLoopTelemetry telemetry)
    {
        if (!entry.Dirty)
        {
            return;
        }

        byte[] currentBytes = entry.Bytes;
        Scalar16Scalar8ReadOnly readOnly = new(currentBytes, profile);
        if (!readOnly.IsValid)
        {
            throw new InvalidDataException("The batch-local SS16-8 shelf cache attempted to stage invalid shelf bytes.");
        }

        ChangedSpan headerSpan = entry.GetHeaderSpan();
        ChangedSpan slotSpan = entry.GetSlotSpan();
        ChangedSpan itemSpan = entry.GetItemSpan();
        int deltaBytes = headerSpan.Length + slotSpan.Length + itemSpan.Length;
        if (deltaBytes <= 0)
        {
            return;
        }

        if (deltaBytes >= profile.ShelfExtentSize)
        {
            session.StageScalar16Scalar8ShelfRewriteForBatch(shelfOffset, profile, currentBytes);
            telemetry.FullShelfStageCount++;
            return;
        }

        telemetry.DeltaShelfStageCount++;
        AddScalar16Scalar16ChangedSpanAttribution(headerSpan, ref telemetry);
        AddScalar16Scalar16ChangedSpanAttribution(slotSpan, ref telemetry);
        AddScalar16Scalar16ChangedSpanAttribution(itemSpan, ref telemetry);
        StageScalar16Scalar8ShelfChangedSpan(session, shelfOffset, profile, currentBytes, headerSpan);
        StageScalar16Scalar8ShelfChangedSpan(session, shelfOffset, profile, currentBytes, slotSpan);
        StageScalar16Scalar8ShelfChangedSpan(session, shelfOffset, profile, currentBytes, itemSpan);
    }


    private static void StageFixed32Scalar8ShelfDeltaRewrite(
        LibraDexFileSession session,
        long shelfOffset,
        Fixed32Scalar8Profile profile,
        Fixed32Scalar8BatchShelfCacheEntry entry,
        ref RoutedBulkWriteLoopTelemetry telemetry)
    {
        if (!entry.Dirty)
        {
            return;
        }

        byte[] currentBytes = entry.Bytes;
        Fixed32Scalar8ReadOnly readOnly = new(currentBytes, profile);
        if (!readOnly.IsValid)
        {
            throw new InvalidDataException("The batch-local FS32-8 shelf cache attempted to stage invalid shelf bytes.");
        }

        ChangedSpan headerSpan = entry.GetHeaderSpan();
        ChangedSpan slotSpan = entry.GetSlotSpan();
        ChangedSpan itemSpan = entry.GetItemSpan();
        int deltaBytes = headerSpan.Length + slotSpan.Length + itemSpan.Length;
        if (deltaBytes <= 0)
        {
            return;
        }

        if (deltaBytes >= profile.ShelfExtentSize)
        {
            session.StageFixed32Scalar8ShelfRewriteForBatch(shelfOffset, profile, currentBytes);
            telemetry.FullShelfStageCount++;
            return;
        }

        telemetry.DeltaShelfStageCount++;
        AddScalar16Scalar16ChangedSpanAttribution(headerSpan, ref telemetry);
        AddScalar16Scalar16ChangedSpanAttribution(slotSpan, ref telemetry);
        AddScalar16Scalar16ChangedSpanAttribution(itemSpan, ref telemetry);
        StageFixed32Scalar8ShelfChangedSpan(session, shelfOffset, profile, currentBytes, headerSpan);
        StageFixed32Scalar8ShelfChangedSpan(session, shelfOffset, profile, currentBytes, slotSpan);
        StageFixed32Scalar8ShelfChangedSpan(session, shelfOffset, profile, currentBytes, itemSpan);
    }


    private static void StageFixed32Scalar8ShelfChangedSpan(
        LibraDexFileSession session,
        long shelfOffset,
        Fixed32Scalar8Profile profile,
        byte[] currentBytes,
        ChangedSpan span)
    {
        if (span.Length == 0)
        {
            return;
        }

        session.StageFixed32Scalar8ShelfSliceRewriteForBatch(
            shelfOffset,
            profile,
            span.Offset,
            currentBytes.AsSpan(span.Offset, span.Length));
    }


    private static void StageScalar16Scalar8ShelfChangedSpan(
        LibraDexFileSession session,
        long shelfOffset,
        Scalar16Scalar8Profile profile,
        byte[] currentBytes,
        ChangedSpan span)
    {
        if (span.Length == 0)
        {
            return;
        }

        session.StageScalar16Scalar8ShelfSliceRewriteForBatch(
            shelfOffset,
            profile,
            span.Offset,
            currentBytes.AsSpan(span.Offset, span.Length));
    }


    private static bool TryGetCachedScalar16Scalar8RouteTarget(
        Dictionary<Scalar16Scalar16BatchRouteCacheKey, Scalar16Scalar8RouteTarget> cache,
        ulong encodedKeyHigh,
        ulong encodedKeyLow,
        out Scalar16Scalar8RouteTarget target)
    {
        for (byte depth = 15; depth > 0; depth--)
        {
            if (cache.TryGetValue(Scalar16Scalar16BatchRouteCacheKey.CreateForDepth(encodedKeyHigh, encodedKeyLow, depth), out target))
            {
                return true;
            }
        }

        return cache.TryGetValue(Scalar16Scalar16BatchRouteCacheKey.CreateForDepth(encodedKeyHigh, encodedKeyLow, 0), out target);
    }


    private static bool TryGetCachedFixed32Scalar8RouteTarget(
        Dictionary<Fixed32Scalar8BatchRouteCacheKey, Fixed32Scalar8RouteTarget> cache,
        ulong key0,
        ulong key1,
        ulong key2,
        ulong key3,
        out Fixed32Scalar8RouteTarget target)
    {
        for (byte depth = 31; depth > 0; depth--)
        {
            if (cache.TryGetValue(Fixed32Scalar8BatchRouteCacheKey.CreateForDepth(key0, key1, key2, key3, depth), out target))
            {
                return true;
            }
        }

        return cache.TryGetValue(Fixed32Scalar8BatchRouteCacheKey.CreateForDepth(key0, key1, key2, key3, 0), out target);
    }


    private static void StageScalar8Scalar16ShelfDeltaRewrite(
        LibraDexFileSession session,
        long shelfOffset,
        Scalar8Scalar16Profile profile,
        Scalar8Scalar16BatchShelfCacheEntry entry,
        ref RoutedBulkWriteLoopTelemetry telemetry)
    {
        if (!entry.Dirty)
        {
            return;
        }

        byte[] currentBytes = entry.Bytes;
        Scalar8Scalar16ReadOnly readOnly = new(currentBytes, profile);
        if (!readOnly.IsValid)
        {
            throw new InvalidDataException("The batch-local SS8-16 shelf cache attempted to stage invalid shelf bytes.");
        }

        ChangedSpan headerSpan = entry.GetHeaderSpan();
        ChangedSpan slotSpan = entry.GetSlotSpan();
        ChangedSpan itemSpan = entry.GetItemSpan();
        int deltaBytes = headerSpan.Length + slotSpan.Length + itemSpan.Length;
        if (deltaBytes <= 0)
        {
            return;
        }

        if (deltaBytes >= profile.ShelfExtentSize)
        {
            session.StageScalar8Scalar16ShelfRewriteForBatch(shelfOffset, profile, currentBytes);
            telemetry.FullShelfStageCount++;
            return;
        }

        telemetry.DeltaShelfStageCount++;
        AddScalar16Scalar16ChangedSpanAttribution(headerSpan, ref telemetry);
        AddScalar16Scalar16ChangedSpanAttribution(slotSpan, ref telemetry);
        AddScalar16Scalar16ChangedSpanAttribution(itemSpan, ref telemetry);
        StageScalar8Scalar16ShelfChangedSpan(session, shelfOffset, profile, currentBytes, headerSpan);
        StageScalar8Scalar16ShelfChangedSpan(session, shelfOffset, profile, currentBytes, slotSpan);
        StageScalar8Scalar16ShelfChangedSpan(session, shelfOffset, profile, currentBytes, itemSpan);
    }


    private static void StageScalar8Scalar16ShelfChangedSpan(
        LibraDexFileSession session,
        long shelfOffset,
        Scalar8Scalar16Profile profile,
        byte[] currentBytes,
        ChangedSpan span)
    {
        if (span.Length == 0)
        {
            return;
        }

        session.StageScalar8Scalar16ShelfSliceRewriteForBatch(
            shelfOffset,
            profile,
            span.Offset,
            currentBytes.AsSpan(span.Offset, span.Length));
    }


    private static bool TryGetCachedScalar8Scalar16RouteTarget(
        Dictionary<Scalar8Scalar16BatchRouteCacheKey, Scalar8Scalar16RouteTarget> cache,
        ulong encodedKey,
        out Scalar8Scalar16RouteTarget target)
    {
        for (byte depth = 7; depth > 0; depth--)
        {
            if (cache.TryGetValue(Scalar8Scalar16BatchRouteCacheKey.CreateForDepth(encodedKey, depth), out target))
            {
                return true;
            }
        }

        return cache.TryGetValue(Scalar8Scalar16BatchRouteCacheKey.CreateForDepth(encodedKey, 0), out target);
    }


    private static bool TryGetCachedScalar16Scalar16RouteTarget(
        Dictionary<Scalar16Scalar16BatchRouteCacheKey, Scalar16Scalar16RouteTarget> cache,
        ulong encodedKeyHigh,
        ulong encodedKeyLow,
        out Scalar16Scalar16RouteTarget target)
    {
        for (byte depth = 15; depth > 0; depth--)
        {
            if (cache.TryGetValue(Scalar16Scalar16BatchRouteCacheKey.CreateForDepth(encodedKeyHigh, encodedKeyLow, depth), out target))
            {
                return true;
            }
        }

        return cache.TryGetValue(Scalar16Scalar16BatchRouteCacheKey.CreateForDepth(encodedKeyHigh, encodedKeyLow, 0), out target);
    }


    private static void StageScalar16Scalar16ShelfChangedSpan(
        LibraDexFileSession session,
        long shelfOffset,
        Scalar16Scalar16Profile profile,
        byte[] currentBytes,
        ChangedSpan span)
    {
        if (span.Length == 0)
        {
            return;
        }

        session.StageScalar16Scalar16ShelfSliceRewriteForBatch(
            shelfOffset,
            profile,
            span.Offset,
            currentBytes.AsSpan(span.Offset, span.Length));
    }


    private sealed class Scalar16Scalar16BatchShelfCacheEntry
    {
        internal Scalar16Scalar16BatchShelfCacheEntry(byte[] bytes)
        {
            Bytes = bytes;
        }

        internal byte[] Bytes { get; }

        internal bool Dirty { get; set; }

        private int headerStart = int.MaxValue;
        private int headerEnd;
        private int slotStart = int.MaxValue;
        private int slotEnd;
        private int itemStart = int.MaxValue;
        private int itemEnd;

        internal void Include(Scalar16Scalar16MutationBounds bounds)
        {
            IncludeRange(ref headerStart, ref headerEnd, bounds.HeaderOffset, bounds.HeaderLength);
            IncludeRange(ref slotStart, ref slotEnd, bounds.SlotOffset, bounds.SlotLength);
            IncludeRange(ref itemStart, ref itemEnd, bounds.ItemOffset, bounds.ItemLength);
        }

        internal ChangedSpan GetHeaderSpan()
        {
            return GetSpan(headerStart, headerEnd);
        }

        internal ChangedSpan GetSlotSpan()
        {
            return GetSpan(slotStart, slotEnd);
        }

        internal ChangedSpan GetItemSpan()
        {
            return GetSpan(itemStart, itemEnd);
        }

        private static void IncludeRange(ref int start, ref int end, int offset, int length)
        {
            if (length <= 0)
            {
                return;
            }

            start = Math.Min(start, offset);
            end = Math.Max(end, checked(offset + length));
        }

        private static ChangedSpan GetSpan(int start, int end)
        {
            return start == int.MaxValue ? default : new ChangedSpan(start, end - start);
        }
    }


    private sealed class Scalar16Scalar8BatchShelfCacheEntry
    {
        internal Scalar16Scalar8BatchShelfCacheEntry(byte[] bytes)
        {
            Bytes = bytes;
        }

        internal byte[] Bytes { get; }

        internal bool Dirty { get; set; }

        private int headerStart = int.MaxValue;
        private int headerEnd;
        private int slotStart = int.MaxValue;
        private int slotEnd;
        private int itemStart = int.MaxValue;
        private int itemEnd;

        internal void Include(Scalar16Scalar8MutationBounds bounds)
        {
            IncludeRange(ref headerStart, ref headerEnd, bounds.HeaderOffset, bounds.HeaderLength);
            IncludeRange(ref slotStart, ref slotEnd, bounds.SlotOffset, bounds.SlotLength);
            IncludeRange(ref itemStart, ref itemEnd, bounds.ItemOffset, bounds.ItemLength);
        }

        internal ChangedSpan GetHeaderSpan()
        {
            return GetSpan(headerStart, headerEnd);
        }

        internal ChangedSpan GetSlotSpan()
        {
            return GetSpan(slotStart, slotEnd);
        }

        internal ChangedSpan GetItemSpan()
        {
            return GetSpan(itemStart, itemEnd);
        }

        private static void IncludeRange(ref int start, ref int end, int offset, int length)
        {
            if (length <= 0)
            {
                return;
            }

            start = Math.Min(start, offset);
            end = Math.Max(end, checked(offset + length));
        }

        private static ChangedSpan GetSpan(int start, int end)
        {
            return start == int.MaxValue ? default : new ChangedSpan(start, end - start);
        }
    }


    private sealed class Scalar8Scalar16BatchShelfCacheEntry
    {
        internal Scalar8Scalar16BatchShelfCacheEntry(byte[] bytes)
        {
            Bytes = bytes;
        }

        internal byte[] Bytes { get; }

        internal bool Dirty { get; set; }

        private int headerStart = int.MaxValue;
        private int headerEnd;
        private int slotStart = int.MaxValue;
        private int slotEnd;
        private int itemStart = int.MaxValue;
        private int itemEnd;

        internal void Include(Scalar8Scalar16MutationBounds bounds)
        {
            IncludeRange(ref headerStart, ref headerEnd, bounds.HeaderOffset, bounds.HeaderLength);
            IncludeRange(ref slotStart, ref slotEnd, bounds.SlotOffset, bounds.SlotLength);
            IncludeRange(ref itemStart, ref itemEnd, bounds.ItemOffset, bounds.ItemLength);
        }

        internal ChangedSpan GetHeaderSpan()
        {
            return GetSpan(headerStart, headerEnd);
        }

        internal ChangedSpan GetSlotSpan()
        {
            return GetSpan(slotStart, slotEnd);
        }

        internal ChangedSpan GetItemSpan()
        {
            return GetSpan(itemStart, itemEnd);
        }

        private static void IncludeRange(ref int start, ref int end, int offset, int length)
        {
            if (length <= 0)
            {
                return;
            }

            start = Math.Min(start, offset);
            end = Math.Max(end, checked(offset + length));
        }

        private static ChangedSpan GetSpan(int start, int end)
        {
            return start == int.MaxValue ? default : new ChangedSpan(start, end - start);
        }
    }


    private sealed class Fixed32Scalar8BatchShelfCacheEntry
    {
        internal Fixed32Scalar8BatchShelfCacheEntry(byte[] bytes)
        {
            Bytes = bytes;
        }

        internal byte[] Bytes { get; }

        internal bool Dirty { get; set; }

        private int headerStart = int.MaxValue;
        private int headerEnd;
        private int slotStart = int.MaxValue;
        private int slotEnd;
        private int itemStart = int.MaxValue;
        private int itemEnd;

        internal void Include(Fixed32Scalar8MutationBounds bounds)
        {
            IncludeRange(ref headerStart, ref headerEnd, bounds.HeaderOffset, bounds.HeaderLength);
            IncludeRange(ref slotStart, ref slotEnd, bounds.SlotOffset, bounds.SlotLength);
            IncludeRange(ref itemStart, ref itemEnd, bounds.ItemOffset, bounds.ItemLength);
        }

        internal ChangedSpan GetHeaderSpan()
        {
            return GetSpan(headerStart, headerEnd);
        }

        internal ChangedSpan GetSlotSpan()
        {
            return GetSpan(slotStart, slotEnd);
        }

        internal ChangedSpan GetItemSpan()
        {
            return GetSpan(itemStart, itemEnd);
        }

        private static void IncludeRange(ref int start, ref int end, int offset, int length)
        {
            if (length <= 0)
            {
                return;
            }

            start = Math.Min(start, offset);
            end = Math.Max(end, checked(offset + length));
        }

        private static ChangedSpan GetSpan(int start, int end)
        {
            return start == int.MaxValue ? default : new ChangedSpan(start, end - start);
        }
    }


    private readonly record struct Scalar8Scalar16BatchRouteCacheKey(byte Depth, ulong Prefix)
    {
        internal static Scalar8Scalar16BatchRouteCacheKey Create(ulong encodedKey, ushort routerDepth)
        {
            if (routerDepth > 7)
            {
                throw new InvalidDataException($"The SS8-16 route depth {routerDepth} cannot be cached for an 8-byte encoded scalar key.");
            }

            return CreateForDepth(encodedKey, (byte)routerDepth);
        }

        internal static Scalar8Scalar16BatchRouteCacheKey CreateForDepth(ulong encodedKey, byte depth)
        {
            return new Scalar8Scalar16BatchRouteCacheKey(depth, ExtractPrefix(encodedKey, depth));
        }

        private static ulong ExtractPrefix(ulong encodedKey, byte depth)
        {
            int prefixBits = (depth + 1) * 8;
            int shift = 64 - prefixBits;
            return encodedKey >> shift;
        }
    }


    private readonly record struct Scalar16Scalar16BatchRouteCacheKey(byte Depth, ulong PrefixHigh, ulong PrefixLow)
    {
        internal static Scalar16Scalar16BatchRouteCacheKey Create(ulong encodedKeyHigh, ulong encodedKeyLow, ushort routerDepth)
        {
            if (routerDepth > 15)
            {
                throw new InvalidDataException($"The SS16-16 route depth {routerDepth} cannot be cached for a 16-byte encoded scalar key.");
            }

            return CreateForDepth(encodedKeyHigh, encodedKeyLow, (byte)routerDepth);
        }

        internal static Scalar16Scalar16BatchRouteCacheKey CreateForDepth(ulong encodedKeyHigh, ulong encodedKeyLow, byte depth)
        {
            ExtractPrefix(encodedKeyHigh, encodedKeyLow, depth, out ulong prefixHigh, out ulong prefixLow);
            return new Scalar16Scalar16BatchRouteCacheKey(depth, prefixHigh, prefixLow);
        }

        private static void ExtractPrefix(ulong encodedKeyHigh, ulong encodedKeyLow, byte depth, out ulong prefixHigh, out ulong prefixLow)
        {
            int prefixBits = (depth + 1) * 8;
            if (prefixBits <= 64)
            {
                prefixHigh = encodedKeyHigh >> (64 - prefixBits);
                prefixLow = 0;
                return;
            }

            int lowPrefixBits = prefixBits - 64;
            prefixHigh = encodedKeyHigh;
            prefixLow = encodedKeyLow >> (64 - lowPrefixBits);
        }
    }


    private readonly record struct Fixed32Scalar8BatchRouteCacheKey(byte Depth, ulong Prefix0, ulong Prefix1, ulong Prefix2, ulong Prefix3)
    {
        internal static Fixed32Scalar8BatchRouteCacheKey Create(ulong key0, ulong key1, ulong key2, ulong key3, ushort routerDepth)
        {
            if (routerDepth > 31)
            {
                throw new InvalidDataException($"The FS32-8 route depth {routerDepth} cannot be cached for a 32-byte encoded fixed key.");
            }

            return CreateForDepth(key0, key1, key2, key3, (byte)routerDepth);
        }

        internal static Fixed32Scalar8BatchRouteCacheKey CreateForDepth(ulong key0, ulong key1, ulong key2, ulong key3, byte depth)
        {
            ExtractPrefix(key0, key1, key2, key3, depth, out ulong prefix0, out ulong prefix1, out ulong prefix2, out ulong prefix3);
            return new Fixed32Scalar8BatchRouteCacheKey(depth, prefix0, prefix1, prefix2, prefix3);
        }

        private static void ExtractPrefix(ulong key0, ulong key1, ulong key2, ulong key3, byte depth, out ulong prefix0, out ulong prefix1, out ulong prefix2, out ulong prefix3)
        {
            int prefixBits = (depth + 1) * 8;
            prefix0 = 0;
            prefix1 = 0;
            prefix2 = 0;
            prefix3 = 0;
            if (prefixBits <= 64)
            {
                prefix0 = key0 >> (64 - prefixBits);
                return;
            }

            prefix0 = key0;
            if (prefixBits <= 128)
            {
                int prefix1Bits = prefixBits - 64;
                prefix1 = key1 >> (64 - prefix1Bits);
                return;
            }

            prefix1 = key1;
            if (prefixBits <= 192)
            {
                int prefix2Bits = prefixBits - 128;
                prefix2 = key2 >> (64 - prefix2Bits);
                return;
            }

            prefix2 = key2;
            int prefix3Bits = prefixBits - 192;
            prefix3 = key3 >> (64 - prefix3Bits);
        }
    }


    /// <summary>
    /// Executes routed `SS8-8` bulk insertion while collecting first-principles attribution counters.<br/>
    /// Each batch still publishes through one public `Scalar8Scalar8Batch.Commit`, but per-insert structural choices are counted before commit coalescing hides intermediate rewrites.<br/>
    /// </summary>
    /// <param name="index">The active public encoded index wrapper.</param>
    /// <param name="batches">The number of committed batches to execute.</param>
    /// <param name="itemsPerBatch">The number of encoded tuples inserted per committed batch.</param>
    /// <param name="prefixCount">The number of root prefixes covered by each generated batch.</param>
    /// <param name="order">The deterministic insertion order vector.</param>
    /// <param name="batchOffset">The logical batch offset used to keep warmup and measured keys disjoint.</param>
    /// <returns>Aggregated routed write-attribution telemetry.</returns>
    private static Scalar8Scalar8WriteAttributionTelemetry RunScalar8Scalar8WriteAttributionLoop(
        Scalar8Scalar8Index index,
        int batches,
        int itemsPerBatch,
        int prefixCount,
        int[] order,
        int batchOffset)
    {
        Scalar8Scalar8WriteAttributionTelemetry telemetry = default;
        Scalar8Scalar8Profile profile = Scalar8Scalar8Profile.Default32KiB;
        Dictionary<long, int> aggregateShelfRewriteCounts = [];
        for (int batch = 0; batch < batches; batch++)
        {
            Dictionary<long, int> batchShelfRewriteCounts = [];
            using Scalar8Scalar8Batch bulk = index.BeginBatch();
            bulk.EnableInsertAttribution();
            bulk.EnablePublicationAttribution();
            bulk.EnableCommitAttribution();
            int logicalBatch = batchOffset + batch;
            for (int i = 0; i < itemsPerBatch; i++)
            {
                ulong key = CreateRoutedBulkWriteKey(logicalBatch, order[i], itemsPerBatch, prefixCount);
                long insertStart = Stopwatch.GetTimestamp();
                Scalar8Scalar8EncodedInsertResult result = bulk.InsertEncoded(key, key, allowDuplicateKeys: true);
                telemetry.InsertElapsedTicks += Stopwatch.GetTimestamp() - insertStart;
                if (result.Outcome != Scalar8Scalar8EncodedInsertOutcome.Inserted)
                {
                    throw new InvalidDataException($"Expected routed attribution insert, got {result.Outcome}.");
                }

                AddScalar8Scalar8WriteAttributionInsert(result, profile, batchShelfRewriteCounts, aggregateShelfRewriteCounts, ref telemetry);
                telemetry.Checksum += unchecked((long)((key >> 32) ^ key ^ (ulong)(i + 1)));
            }

            AddScalar8Scalar8BatchInsertAttribution(bulk.GetInsertAttributionTelemetry(), ref telemetry);
            long commitStart = Stopwatch.GetTimestamp();
            Scalar8Scalar8BatchCommitResult commit = bulk.Commit();
            telemetry.CommitWallElapsedTicks += Stopwatch.GetTimestamp() - commitStart;
            if (commit.InsertedCount != itemsPerBatch || commit.AttemptedInsertCount != itemsPerBatch)
            {
                throw new InvalidDataException($"Routed attribution batch inserted {commit.InsertedCount} of {commit.AttemptedInsertCount}; expected {itemsPerBatch}.");
            }

            AddScalar8Scalar8WriteAttributionCommit(commit, ref telemetry);
            AddScalar8Scalar8PublicationAttribution(bulk.GetPublicationAttributionTelemetry(), ref telemetry);
            AddScalar8Scalar8BatchCommitAttribution(bulk.GetCommitAttributionTelemetry(), ref telemetry);
            AddScalar8Scalar8BatchRewriteRepeatStats(batchShelfRewriteCounts, ref telemetry);
        }

        telemetry.DistinctShelfRewriteOffsets = aggregateShelfRewriteCounts.Count;
        return telemetry;
    }


    /// <summary>
    /// Adds one batch's commit-path attribution counters to the aggregate routed write-attribution telemetry.<br/>
    /// These counters split batch publication work above the DataKernel commit boundary.<br/>
    /// </summary>
    /// <param name="batch">The per-batch commit attribution snapshot.</param>
    /// <param name="telemetry">The aggregate routed write-attribution telemetry.</param>
    private static void AddScalar8Scalar8BatchCommitAttribution(
        Scalar8Scalar8BatchCommitAttributionTelemetry batch,
        ref Scalar8Scalar8WriteAttributionTelemetry telemetry)
    {
        telemetry.BatchDirtyShelfFlushTicks += batch.DirtyShelfFlushTicks;
        telemetry.BatchPublicationAttributionTicks += batch.PublicationAttributionTicks;
        telemetry.BatchDeltaSelectionTicks += batch.DeltaSelectionTicks;
        telemetry.BatchDeltaStagingTicks += batch.DeltaStagingTicks;
        telemetry.BatchFullShelfStagingTicks += batch.FullShelfStagingTicks;
        telemetry.BatchSplitFallbackFullShelfStagingTicks += batch.SplitFallbackFullShelfStagingTicks;
        telemetry.BatchDataKernelCommitTicks += batch.DataKernelCommitTicks;
        telemetry.DeltaShelfCount += batch.DeltaShelfCount;
        telemetry.DeltaUngroupedSpanCount += batch.DeltaUngroupedSpanCount;
        telemetry.DeltaUngroupedBytes += batch.DeltaUngroupedBytes;
        telemetry.DeltaGrouped512SpanCount += batch.DeltaGrouped512SpanCount;
        telemetry.DeltaGrouped512Bytes += batch.DeltaGrouped512Bytes;
        telemetry.DeltaGrouped1024SpanCount += batch.DeltaGrouped1024SpanCount;
        telemetry.DeltaGrouped1024Bytes += batch.DeltaGrouped1024Bytes;
        telemetry.DeltaGrouped4096SpanCount += batch.DeltaGrouped4096SpanCount;
        telemetry.DeltaGrouped4096Bytes += batch.DeltaGrouped4096Bytes;
        telemetry.DeltaGrouped8192SpanCount += batch.DeltaGrouped8192SpanCount;
        telemetry.DeltaGrouped8192Bytes += batch.DeltaGrouped8192Bytes;
        telemetry.DeltaPositiveGapCount += batch.DeltaPositiveGapCount;
        telemetry.DeltaPositiveGapBytes += batch.DeltaPositiveGapBytes;
        telemetry.DeltaMaxPositiveGapBytes = Math.Max(telemetry.DeltaMaxPositiveGapBytes, batch.DeltaMaxPositiveGapBytes);
    }


    /// <summary>
    /// Adds one batch's dirty-shelf publication attribution counters to the aggregate routed write-attribution telemetry.<br/>
    /// These counters estimate whether cached dirty shelves are compact enough to publish as changed regions instead of full shelf extents.<br/>
    /// </summary>
    /// <param name="batch">The per-batch publication attribution snapshot.</param>
    /// <param name="telemetry">The aggregate routed write-attribution telemetry.</param>
    private static void AddScalar8Scalar8PublicationAttribution(
        Scalar8Scalar8BatchPublicationAttributionTelemetry batch,
        ref Scalar8Scalar8WriteAttributionTelemetry telemetry)
    {
        telemetry.PublicationDirtyShelfCount += batch.DirtyShelfCount;
        telemetry.PublicationFullShelfBytes += batch.FullShelfBytes;
        telemetry.PublicationRawChangedBytes += batch.RawChangedBytes;
        telemetry.PublicationRawChangedRangeCount += batch.RawChangedRangeCount;
        telemetry.PublicationDeltaCandidateBytes += batch.DeltaCandidateBytes;
        telemetry.PublicationDeltaCandidateRangeCount += batch.DeltaCandidateRangeCount;
        telemetry.PublicationHeaderDeltaBytes += batch.HeaderDeltaBytes;
        telemetry.PublicationSlotDeltaBytes += batch.SlotDeltaBytes;
        telemetry.PublicationItemDeltaBytes += batch.ItemDeltaBytes;
        telemetry.PublicationMaxDeltaCandidateBytesPerShelf = Math.Max(telemetry.PublicationMaxDeltaCandidateBytesPerShelf, batch.MaxDeltaCandidateBytesPerShelf);
        telemetry.PublicationFullShelfBetterOrEqualCount += batch.FullShelfBetterOrEqualCount;
    }


    /// <summary>
    /// Adds one batch's internal cached-insert attribution counters to the aggregate routed write-attribution telemetry.<br/>
    /// These counters split the measured insert path into route lookup, route walking/classification, shelf-cache lookup, and shelf-local insert phases.<br/>
    /// </summary>
    /// <param name="batch">The per-batch cached insert attribution snapshot.</param>
    /// <param name="telemetry">The aggregate routed write-attribution telemetry.</param>
    private static void AddScalar8Scalar8BatchInsertAttribution(
        Scalar8Scalar8BatchInsertAttributionTelemetry batch,
        ref Scalar8Scalar8WriteAttributionTelemetry telemetry)
    {
        telemetry.CachedAttemptCount += batch.AttemptCount;
        telemetry.CachedHandledCount += batch.CachedHandledCount;
        telemetry.CacheMissEmptyRouteCount += batch.CacheMissEmptyRouteCount;
        telemetry.CacheMissNonShelfTargetCount += batch.CacheMissNonShelfTargetCount;
        telemetry.CacheMissFullShelfCount += batch.CacheMissFullShelfCount;
        telemetry.RouteCacheHitCount += batch.RouteCacheHitCount;
        telemetry.RouteCacheMissCount += batch.RouteCacheMissCount;
        telemetry.RouteCacheProbeTicks += batch.RouteCacheProbeTicks;
        telemetry.RootLookupTicks += batch.RootLookupTicks;
        telemetry.RouteWalkTicks += batch.RouteWalkTicks;
        telemetry.RouteWalkDirectViewLookupTicks += batch.RouteWalkAttribution.DirectViewLookupTicks;
        telemetry.RouteWalkDirectPrefixTicks += batch.RouteWalkAttribution.DirectPrefixTicks;
        telemetry.RouteWalkDirectSlotLoadTicks += batch.RouteWalkAttribution.DirectSlotLoadTicks;
        telemetry.RouteWalkDirectTargetClassificationTicks += batch.RouteWalkAttribution.DirectTargetClassificationTicks;
        telemetry.RouteWalkDirectRouterTargetCount += batch.RouteWalkAttribution.DirectRouterTargetCount;
        telemetry.RouteWalkDirectShelfTargetCount += batch.RouteWalkAttribution.DirectShelfTargetCount;
        telemetry.RouteWalkNonDirectRouterReadTicks += batch.RouteWalkAttribution.NonDirectRouterReadTicks;
        telemetry.RouteWalkNonDirectPrefixTicks += batch.RouteWalkAttribution.NonDirectPrefixTicks;
        telemetry.RouteWalkNonDirectRouteLookupTicks += batch.RouteWalkAttribution.NonDirectRouteLookupTicks;
        telemetry.RouteWalkNonDirectTargetClassificationTicks += batch.RouteWalkAttribution.NonDirectTargetClassificationTicks;
        telemetry.RouteWalkNonDirectRouterTargetCount += batch.RouteWalkAttribution.NonDirectRouterTargetCount;
        telemetry.RouteWalkNonDirectShelfTargetCount += batch.RouteWalkAttribution.NonDirectShelfTargetCount;
        telemetry.RouteTargetClassificationTicks += batch.RouteTargetClassificationTicks;
        telemetry.ShelfCacheLookupTicks += batch.ShelfCacheLookupTicks;
        telemetry.ShelfCacheProbeTicks += batch.ShelfCacheProbeTicks;
        telemetry.ShelfCacheHitCount += batch.ShelfCacheHitCount;
        telemetry.ShelfCacheMissCount += batch.ShelfCacheMissCount;
        telemetry.ShelfCacheReadTicks += batch.ShelfCacheReadTicks;
        telemetry.ShelfCacheSnapshotCloneTicks += batch.ShelfCacheSnapshotCloneTicks;
        telemetry.ShelfCacheEntryCreateTicks += batch.ShelfCacheEntryCreateTicks;
        telemetry.ShelfCacheEntryAddTicks += batch.ShelfCacheEntryAddTicks;
        telemetry.ShelfInsertTicks += batch.ShelfInsertTicks;
        telemetry.ShelfAppendFastPathTicks += batch.ShelfAppendFastPathTicks;
        telemetry.ShelfAppendFastPathCount += batch.ShelfAppendFastPathCount;
        telemetry.ShelfLowerBoundTicks += batch.ShelfLowerBoundTicks;
        telemetry.ShelfDuplicateCheckTicks += batch.ShelfDuplicateCheckTicks;
        telemetry.ShelfUniqueCheckTicks += batch.ShelfUniqueCheckTicks;
        telemetry.ShelfPayloadWriteTicks += batch.ShelfPayloadWriteTicks;
        telemetry.ShelfSlotMoveTicks += batch.ShelfSlotMoveTicks;
        telemetry.ShelfHeaderWriteTicks += batch.ShelfHeaderWriteTicks;
    }


    /// <summary>
    /// Adds one per-insert structural result to routed write-attribution telemetry.<br/>
    /// Planned byte counts describe the mutation shape before DataKernel superseded-write elimination and contiguous write combining are applied at commit.<br/>
    /// </summary>
    /// <param name="result">The encoded insert result carrying internal structural attribution fields.</param>
    /// <param name="profile">The `SS8-8` profile used for shelf extent size.</param>
    /// <param name="batchShelfRewriteCounts">The per-batch rewritten-shelf count map.</param>
    /// <param name="aggregateShelfRewriteCounts">The run-level rewritten-shelf count map.</param>
    /// <param name="telemetry">The attribution telemetry accumulator.</param>
    private static void AddScalar8Scalar8WriteAttributionInsert(
        Scalar8Scalar8EncodedInsertResult result,
        Scalar8Scalar8Profile profile,
        Dictionary<long, int> batchShelfRewriteCounts,
        Dictionary<long, int> aggregateShelfRewriteCounts,
        ref Scalar8Scalar8WriteAttributionTelemetry telemetry)
    {
        telemetry.InsertedCount++;
        if (result.CreatedInitialShelfRoute)
        {
            telemetry.InitialRouteCreateCount++;
            telemetry.PlannedShelfAppendCount++;
            telemetry.PlannedRouterRewriteCount++;
            telemetry.PlannedStagedBytes += profile.ShelfExtentSize + RouterLayout.Size;
        }

        switch (result.StructuralKind)
        {
            case Scalar8Scalar8RoutedInsertKind.NoSplit:
            case Scalar8Scalar8RoutedInsertKind.TwoLevelNoSplit:
            case Scalar8Scalar8RoutedInsertKind.WalkedNoSplit:
                telemetry.NoSplitCount++;
                telemetry.PlannedShelfRewriteCount++;
                telemetry.PlannedStagedBytes += profile.ShelfExtentSize;
                AddScalar8Scalar8ShelfRewriteOffset(result.LeftShelfOffset, batchShelfRewriteCounts, aggregateShelfRewriteCounts);
                break;
            case Scalar8Scalar8RoutedInsertKind.RootPrefixSplit:
            case Scalar8Scalar8RoutedInsertKind.WalkedParentRouteSplit:
                telemetry.NormalSplitCount++;
                telemetry.PlannedShelfRewriteCount++;
                telemetry.PlannedShelfAppendCount++;
                telemetry.PlannedRouterRewriteCount++;
                telemetry.PlannedStagedBytes += (profile.ShelfExtentSize * 2L) + RouterLayout.Size;
                AddScalar8Scalar8ShelfRewriteOffset(result.LeftShelfOffset, batchShelfRewriteCounts, aggregateShelfRewriteCounts);
                break;
            case Scalar8Scalar8RoutedInsertKind.ShelfTransformSplit:
            case Scalar8Scalar8RoutedInsertKind.WalkedShelfTransformSplit:
                telemetry.TransformSplitCount++;
                telemetry.PlannedShelfAppendCount += 2;
                telemetry.PlannedRouterRewriteCount++;
                telemetry.PlannedStagedBytes += (profile.ShelfExtentSize * 2L) + RouterLayout.Size;
                break;
            case Scalar8Scalar8RoutedInsertKind.NoOp:
                telemetry.NoOpCount++;
                break;
            case Scalar8Scalar8RoutedInsertKind.KeyConflict:
                telemetry.KeyConflictCount++;
                break;
            default:
                throw new InvalidDataException($"Unknown SS8-8 attribution structural kind {result.StructuralKind}.");
        }
    }


    /// <summary>
    /// Adds one rewritten shelf offset to both per-batch and run-level rewrite maps.<br/>
    /// Repeated offsets inside one batch expose staged rewrites that DataKernel may later collapse before durable publication.<br/>
    /// </summary>
    /// <param name="offset">The shelf offset rewritten by an insert.</param>
    /// <param name="batchShelfRewriteCounts">The per-batch rewritten-shelf count map.</param>
    /// <param name="aggregateShelfRewriteCounts">The run-level rewritten-shelf count map.</param>
    private static void AddScalar8Scalar8ShelfRewriteOffset(
        long offset,
        Dictionary<long, int> batchShelfRewriteCounts,
        Dictionary<long, int> aggregateShelfRewriteCounts)
    {
        if (offset == 0)
        {
            return;
        }

        batchShelfRewriteCounts[offset] = batchShelfRewriteCounts.TryGetValue(offset, out int batchCount) ? batchCount + 1 : 1;
        aggregateShelfRewriteCounts[offset] = aggregateShelfRewriteCounts.TryGetValue(offset, out int aggregateCount) ? aggregateCount + 1 : 1;
    }


    /// <summary>
    /// Adds per-batch repeated shelf rewrite statistics after one public batch has committed.<br/>
    /// Revisit counts are causal diagnostics for write amplification because only the last full overwrite of a shelf must survive durable publication.<br/>
    /// </summary>
    /// <param name="batchShelfRewriteCounts">The per-batch rewritten-shelf count map.</param>
    /// <param name="telemetry">The attribution telemetry accumulator.</param>
    private static void AddScalar8Scalar8BatchRewriteRepeatStats(
        Dictionary<long, int> batchShelfRewriteCounts,
        ref Scalar8Scalar8WriteAttributionTelemetry telemetry)
    {
        foreach (int rewriteCount in batchShelfRewriteCounts.Values)
        {
            if (rewriteCount > 1)
            {
                telemetry.SameShelfRewriteRevisitCount += rewriteCount - 1;
                telemetry.MaxShelfRewriteCountInBatch = Math.Max(telemetry.MaxShelfRewriteCountInBatch, rewriteCount);
            }
            else
            {
                telemetry.MaxShelfRewriteCountInBatch = Math.Max(telemetry.MaxShelfRewriteCountInBatch, 1);
            }
        }
    }


    /// <summary>
    /// Adds one public batch commit result to routed write-attribution telemetry.<br/>
    /// Commit counters represent the post-coalescing DataKernel publication shape, while planned counters represent the pre-coalescing structural mutation shape.<br/>
    /// </summary>
    /// <param name="batch">The committed public batch result.</param>
    /// <param name="telemetry">The attribution telemetry accumulator.</param>
    private static void AddScalar8Scalar8WriteAttributionCommit(
        Scalar8Scalar8BatchCommitResult batch,
        ref Scalar8Scalar8WriteAttributionTelemetry telemetry)
    {
        telemetry.CommitCount++;
        telemetry.DeferredCommitRequests += batch.DeferredCommitRequests;
        telemetry.CommitElapsedTicks += batch.Commit.ElapsedTicks;
        telemetry.StagedSegmentCount += batch.Commit.StagedSegmentCount;
        telemetry.StagedExtentCount += batch.Commit.StagedExtentCount;
        telemetry.WriteCallCount += batch.Commit.WriteCallCount;
        telemetry.BackingWriteCallCount += batch.Commit.BackingWriteCallCount;
        telemetry.SetLengthCallCount += batch.Commit.SetLengthCallCount;
        telemetry.FlushCallCount += batch.Commit.FlushCallCount;
        telemetry.BytesWritten += batch.Commit.BytesWritten;
        telemetry.CoalescedAdjacentSegmentCount += batch.Commit.CoalescedAdjacentSegmentCount;
        telemetry.CoalescedGapCount += batch.Commit.CoalescedGapCount;
        telemetry.CoalescedGapBytes += batch.Commit.CoalescedGapBytes;
        telemetry.MaxCoalescedGapBytes = Math.Max(telemetry.MaxCoalescedGapBytes, batch.Commit.MaxCoalescedGapBytes);
        telemetry.RejectedGapCount += batch.Commit.RejectedGapCount;
        telemetry.RejectedGapBytes += batch.Commit.RejectedGapBytes;
        telemetry.MaxRejectedGapBytes = Math.Max(telemetry.MaxRejectedGapBytes, batch.Commit.MaxRejectedGapBytes);
        telemetry.OverlapBreakCount += batch.Commit.OverlapBreakCount;
        telemetry.FileCommitSliceBuildTicks += batch.Commit.FileCommitSliceBuildTicks;
        telemetry.FileCommitCoveredRangeMergeTicks += batch.Commit.FileCommitCoveredRangeMergeTicks;
        telemetry.FileCommitGroupShapeTicks += batch.Commit.FileCommitGroupShapeTicks;
        telemetry.FileCommitBufferBuildTicks += batch.Commit.FileCommitBufferBuildTicks;
        telemetry.FileCommitGapReadTicks += batch.Commit.FileCommitGapReadTicks;
        telemetry.FileCommitBackingWriteTicks += batch.Commit.FileCommitBackingWriteTicks;
    }


    /// <summary>
    /// Creates one deterministic 32-byte fixed key for the `FS32-8` routed bulk-write benchmark.<br/>
    /// The first key lane preserves the scalar routed-prefix cadence so route fanout matches other shapes, while the remaining lanes make SQLite and shelf comparisons exercise a true 32-byte key payload.<br/>
    /// </summary>
    /// <param name="batch">The logical batch ordinal.</param>
    /// <param name="itemOrdinal">The item ordinal before insertion-order permutation.</param>
    /// <param name="itemsPerBatch">The number of generated items in each batch.</param>
    /// <param name="prefixCount">The number of root prefixes covered by the generated key set.</param>
    /// <param name="key0">Receives encoded fixed-key lane 0.</param>
    /// <param name="key1">Receives encoded fixed-key lane 1.</param>
    /// <param name="key2">Receives encoded fixed-key lane 2.</param>
    /// <param name="key3">Receives encoded fixed-key lane 3.</param>
    private static void CreateFixed32Scalar8BulkWriteKey(
        int batch,
        int itemOrdinal,
        int itemsPerBatch,
        int prefixCount,
        out ulong key0,
        out ulong key1,
        out ulong key2,
        out ulong key3)
    {
        key0 = CreateRoutedBulkWriteKey(batch, itemOrdinal, itemsPerBatch, prefixCount);
        key1 = CreateSqliteScalar16Scalar16LowHalf(key0);
        key2 = unchecked((key1 << 29) ^ (key0 >> 11) ^ 0xD1B5_4A32_D192_ED03UL);
        key3 = unchecked((key2 << 7) ^ (key1 >> 19) ^ 0x94D0_49BB_1331_11EBUL);
    }


    /// <summary>
    /// Adds one public batch commit result into the routed bulk-write telemetry accumulator.<br/>
    /// The accumulator keeps both logical batch counts and folded lower-level commit-request counts visible.<br/>
    /// </summary>
    /// <param name="batch">The public batch commit result.</param>
    /// <param name="telemetry">The aggregate telemetry accumulator.</param>
    private static void AddRoutedBulkWriteCommit(Scalar8Scalar8BatchCommitResult batch, ref RoutedBulkWriteLoopTelemetry telemetry)
    {
        telemetry.CommitCount++;
        telemetry.DeferredCommitRequests += batch.DeferredCommitRequests;
        telemetry.WriteCallCount += batch.Commit.WriteCallCount;
        telemetry.BytesWritten += batch.Commit.BytesWritten;
        telemetry.SetLengthCallCount += batch.Commit.SetLengthCallCount;
        telemetry.InsertedCount += batch.InsertedCount;
    }


    /// <summary>
    /// Adds one typed unsigned public batch commit result into the routed bulk-write telemetry accumulator.<br/>
    /// The typed wrapper preserves the same commit shape as the encoded core while keeping benchmark call sites free of encoded result types.<br/>
    /// </summary>
    /// <param name="batch">The typed unsigned batch commit result.</param>
    /// <param name="telemetry">The aggregate telemetry accumulator.</param>
    private static void AddRoutedBulkWriteCommit(UnsignedScalar8Scalar8BatchCommitResult batch, ref RoutedBulkWriteLoopTelemetry telemetry)
    {
        telemetry.CommitCount++;
        telemetry.DeferredCommitRequests += batch.DeferredCommitRequests;
        telemetry.WriteCallCount += batch.Commit.WriteCallCount;
        telemetry.BytesWritten += batch.Commit.BytesWritten;
        telemetry.SetLengthCallCount += batch.Commit.SetLengthCallCount;
        telemetry.InsertedCount += batch.InsertedCount;
    }


    private static void AddScalar16Scalar16RoutedBulkWriteFallback(
        Scalar16Scalar16RoutedInsertResult result,
        ref RoutedBulkWriteLoopTelemetry telemetry)
    {
        switch (result.Kind)
        {
            case Scalar16Scalar16RoutedInsertKind.WalkedNoSplit:
                telemetry.NoSplitFallbackCount++;
                break;
            case Scalar16Scalar16RoutedInsertKind.WalkedShelfTransformSplit:
                telemetry.TransformSplitFallbackCount++;
                break;
            case Scalar16Scalar16RoutedInsertKind.WalkedParentRouteSplit:
                telemetry.ParentRouteSplitFallbackCount++;
                break;
            case Scalar16Scalar16RoutedInsertKind.NoOp:
            case Scalar16Scalar16RoutedInsertKind.KeyConflict:
                break;
            default:
                throw new InvalidDataException($"Unknown SS16-16 routed bulk-write fallback kind {result.Kind}.");
        }
    }


    private static void AddScalar16Scalar8RoutedBulkWriteFallback(
        Scalar16Scalar8RoutedInsertResult result,
        ref RoutedBulkWriteLoopTelemetry telemetry)
    {
        switch (result.Kind)
        {
            case Scalar16Scalar8RoutedInsertKind.WalkedNoSplit:
                telemetry.NoSplitFallbackCount++;
                break;
            case Scalar16Scalar8RoutedInsertKind.WalkedShelfTransformSplit:
                telemetry.TransformSplitFallbackCount++;
                break;
            case Scalar16Scalar8RoutedInsertKind.WalkedParentRouteSplit:
                telemetry.ParentRouteSplitFallbackCount++;
                break;
            case Scalar16Scalar8RoutedInsertKind.NoOp:
            case Scalar16Scalar8RoutedInsertKind.KeyConflict:
                break;
            default:
                throw new InvalidDataException($"Unknown SS16-8 routed bulk-write fallback kind {result.Kind}.");
        }
    }


    private static void AddScalar8Scalar16RoutedBulkWriteFallback(
        Scalar8Scalar16RoutedInsertResult result,
        ref RoutedBulkWriteLoopTelemetry telemetry)
    {
        switch (result.Kind)
        {
            case Scalar8Scalar16RoutedInsertKind.WalkedNoSplit:
                telemetry.NoSplitFallbackCount++;
                break;
            case Scalar8Scalar16RoutedInsertKind.WalkedShelfTransformSplit:
                telemetry.TransformSplitFallbackCount++;
                break;
            case Scalar8Scalar16RoutedInsertKind.WalkedParentRouteSplit:
                telemetry.ParentRouteSplitFallbackCount++;
                break;
            case Scalar8Scalar16RoutedInsertKind.NoOp:
            case Scalar8Scalar16RoutedInsertKind.KeyConflict:
                break;
            default:
                throw new InvalidDataException($"Unknown SS8-16 routed bulk-write fallback kind {result.Kind}.");
        }
    }


    private static void AddFixed32Scalar8RoutedBulkWriteFallback(
        Fixed32Scalar8RoutedInsertResult result,
        ref RoutedBulkWriteLoopTelemetry telemetry)
    {
        switch (result.Kind)
        {
            case Fixed32Scalar8RoutedInsertKind.WalkedNoSplit:
                telemetry.NoSplitFallbackCount++;
                break;
            case Fixed32Scalar8RoutedInsertKind.WalkedShelfTransformSplit:
                telemetry.TransformSplitFallbackCount++;
                break;
            case Fixed32Scalar8RoutedInsertKind.WalkedParentRouteSplit:
                telemetry.ParentRouteSplitFallbackCount++;
                break;
            case Fixed32Scalar8RoutedInsertKind.NoOp:
            case Fixed32Scalar8RoutedInsertKind.KeyConflict:
                break;
            default:
                throw new InvalidDataException($"Unknown FS32-8 routed bulk-write fallback kind {result.Kind}.");
        }
    }


    /// <summary>
    /// Creates the final routed bulk-write result row from elapsed time and aggregate public batch telemetry.<br/>
    /// Logical item throughput is kept as the headline number and byte movement remains diagnostic evidence.<br/>
    /// </summary>
    /// <param name="name">The result row name.</param>
    /// <param name="batches">The measured batch count.</param>
    /// <param name="itemsPerBatch">The number of items committed per batch.</param>
    /// <param name="elapsed">The measured elapsed time.</param>
    /// <param name="telemetry">The aggregate routed bulk-write telemetry.</param>
    /// <returns>The completed routed bulk-write row.</returns>
    private static Scalar8Scalar8RoutedBulkWriteResult CreateRoutedBulkWriteResult(
        string name,
        int batches,
        int itemsPerBatch,
        TimeSpan elapsed,
        RoutedBulkWriteLoopTelemetry telemetry)
    {
        long itemCount = (long)batches * itemsPerBatch;
        double seconds = Math.Max(elapsed.TotalSeconds, 0.000000001d);
        double stopwatchTickToNs = 1_000_000_000d / Stopwatch.Frequency;
        return new Scalar8Scalar8RoutedBulkWriteResult(
            name,
            batches,
            itemsPerBatch,
            itemCount,
            telemetry.CommitCount,
            elapsed,
            itemCount / seconds,
            elapsed.TotalMilliseconds * 1000d / itemCount,
            batches / seconds,
            telemetry.CommitCount == 0 ? 0 : (double)telemetry.DeferredCommitRequests / telemetry.CommitCount,
            telemetry.CommitCount == 0 ? 0 : (double)telemetry.WriteCallCount / telemetry.CommitCount,
            batches == 0 ? 0 : (double)telemetry.BytesWritten / batches,
            itemCount == 0 ? 0 : (double)telemetry.BytesWritten / itemCount,
            CalculateMiBs(telemetry.BytesWritten, seconds),
            telemetry.SetLengthCallCount,
            telemetry.Checksum,
            itemCount == 0 ? 0 : telemetry.RootLookupTicks * stopwatchTickToNs / itemCount,
            telemetry.RouteTargetCacheHitCount,
            telemetry.RouteTargetCacheMissCount,
            telemetry.RouteWalkCount,
            itemCount == 0 ? 0 : telemetry.RouteWalkTicks * stopwatchTickToNs / itemCount,
            itemCount == 0 ? 0 : telemetry.ShelfInsertTicks * stopwatchTickToNs / itemCount,
            telemetry.ShelfCacheHitCount,
            telemetry.ShelfCacheMissCount,
            telemetry.CachedInsertCount,
            telemetry.WalkedFallbackCount,
            telemetry.CacheFullFallbackCount,
            telemetry.NoSplitFallbackCount,
            telemetry.TransformSplitFallbackCount,
            telemetry.ParentRouteSplitFallbackCount,
            telemetry.RootRouteCreateCount,
            telemetry.FullShelfStageCount,
            telemetry.DeltaShelfStageCount,
            telemetry.DeltaChangedSpanCount,
            telemetry.DeltaChangedBytes,
            1);
    }


    /// <summary>
    /// Creates the final routed write-attribution row from elapsed time and aggregate lower-layer counters.<br/>
    /// The result keeps CPU path timing, commit timing, structural mutation counts, planned bytes, and post-coalescing write shape side by side.<br/>
    /// </summary>
    /// <param name="name">The result row name.</param>
    /// <param name="batches">The measured batch count.</param>
    /// <param name="itemsPerBatch">The number of items committed per batch.</param>
    /// <param name="elapsed">The measured total elapsed time.</param>
    /// <param name="telemetry">The aggregate write-attribution telemetry.</param>
    /// <returns>The completed write-attribution row.</returns>
    private static Scalar8Scalar8WriteAttributionResult CreateScalar8Scalar8WriteAttributionResult(
        string name,
        int batches,
        int itemsPerBatch,
        TimeSpan elapsed,
        Scalar8Scalar8WriteAttributionTelemetry telemetry)
    {
        long itemCount = (long)batches * itemsPerBatch;
        double seconds = Math.Max(elapsed.TotalSeconds, 0.000000001d);
        double stopwatchTickToNs = 1_000_000_000d / Stopwatch.Frequency;
        return new Scalar8Scalar8WriteAttributionResult(
            name,
            batches,
            itemsPerBatch,
            itemCount,
            elapsed,
            itemCount / seconds,
            elapsed.TotalMilliseconds * 1000d / itemCount,
            telemetry.InsertElapsedTicks * stopwatchTickToNs / itemCount,
            telemetry.CommitWallElapsedTicks * stopwatchTickToNs / itemCount,
            telemetry.CommitElapsedTicks * stopwatchTickToNs / itemCount,
            telemetry.BatchDirtyShelfFlushTicks * stopwatchTickToNs / itemCount,
            telemetry.BatchPublicationAttributionTicks * stopwatchTickToNs / itemCount,
            telemetry.BatchDeltaSelectionTicks * stopwatchTickToNs / itemCount,
            telemetry.BatchDeltaStagingTicks * stopwatchTickToNs / itemCount,
            telemetry.BatchFullShelfStagingTicks * stopwatchTickToNs / itemCount,
            telemetry.BatchSplitFallbackFullShelfStagingTicks * stopwatchTickToNs / itemCount,
            telemetry.BatchDataKernelCommitTicks * stopwatchTickToNs / itemCount,
            telemetry.RootLookupTicks * stopwatchTickToNs / itemCount,
            telemetry.RouteWalkTicks * stopwatchTickToNs / itemCount,
            telemetry.RouteWalkDirectViewLookupTicks * stopwatchTickToNs / itemCount,
            telemetry.RouteWalkDirectPrefixTicks * stopwatchTickToNs / itemCount,
            telemetry.RouteWalkDirectSlotLoadTicks * stopwatchTickToNs / itemCount,
            telemetry.RouteWalkDirectTargetClassificationTicks * stopwatchTickToNs / itemCount,
            telemetry.RouteWalkDirectRouterTargetCount,
            telemetry.RouteWalkDirectShelfTargetCount,
            telemetry.RouteWalkNonDirectRouterReadTicks * stopwatchTickToNs / itemCount,
            telemetry.RouteWalkNonDirectPrefixTicks * stopwatchTickToNs / itemCount,
            telemetry.RouteWalkNonDirectRouteLookupTicks * stopwatchTickToNs / itemCount,
            telemetry.RouteWalkNonDirectTargetClassificationTicks * stopwatchTickToNs / itemCount,
            telemetry.RouteWalkNonDirectRouterTargetCount,
            telemetry.RouteWalkNonDirectShelfTargetCount,
            telemetry.RouteTargetClassificationTicks * stopwatchTickToNs / itemCount,
            telemetry.RouteCacheProbeTicks * stopwatchTickToNs / itemCount,
            telemetry.ShelfCacheLookupTicks * stopwatchTickToNs / itemCount,
            telemetry.ShelfCacheProbeTicks * stopwatchTickToNs / itemCount,
            telemetry.ShelfCacheHitCount,
            telemetry.ShelfCacheMissCount,
            telemetry.ShelfCacheReadTicks * stopwatchTickToNs / itemCount,
            telemetry.ShelfCacheSnapshotCloneTicks * stopwatchTickToNs / itemCount,
            telemetry.ShelfCacheEntryCreateTicks * stopwatchTickToNs / itemCount,
            telemetry.ShelfCacheEntryAddTicks * stopwatchTickToNs / itemCount,
            telemetry.ShelfInsertTicks * stopwatchTickToNs / itemCount,
            telemetry.ShelfAppendFastPathTicks * stopwatchTickToNs / itemCount,
            telemetry.ShelfAppendFastPathCount,
            telemetry.ShelfLowerBoundTicks * stopwatchTickToNs / itemCount,
            telemetry.ShelfDuplicateCheckTicks * stopwatchTickToNs / itemCount,
            telemetry.ShelfUniqueCheckTicks * stopwatchTickToNs / itemCount,
            telemetry.ShelfPayloadWriteTicks * stopwatchTickToNs / itemCount,
            telemetry.ShelfSlotMoveTicks * stopwatchTickToNs / itemCount,
            telemetry.ShelfHeaderWriteTicks * stopwatchTickToNs / itemCount,
            telemetry.CachedAttemptCount,
            telemetry.CachedHandledCount,
            telemetry.CacheMissEmptyRouteCount,
            telemetry.CacheMissNonShelfTargetCount,
            telemetry.CacheMissFullShelfCount,
            telemetry.RouteCacheHitCount,
            telemetry.RouteCacheMissCount,
            telemetry.InsertedCount,
            telemetry.InitialRouteCreateCount,
            telemetry.NoSplitCount,
            telemetry.NormalSplitCount,
            telemetry.TransformSplitCount,
            telemetry.NoOpCount,
            telemetry.KeyConflictCount,
            telemetry.PlannedShelfRewriteCount,
            telemetry.PlannedShelfAppendCount,
            telemetry.PlannedRouterRewriteCount,
            telemetry.PlannedStagedBytes,
            itemCount == 0 ? 0 : telemetry.PlannedStagedBytes / (double)itemCount,
            telemetry.SameShelfRewriteRevisitCount,
            telemetry.DistinctShelfRewriteOffsets,
            telemetry.MaxShelfRewriteCountInBatch,
            telemetry.CommitCount,
            telemetry.DeferredCommitRequests,
            telemetry.CommitCount == 0 ? 0 : (double)telemetry.StagedSegmentCount / telemetry.CommitCount,
            telemetry.CommitCount == 0 ? 0 : (double)telemetry.WriteCallCount / telemetry.CommitCount,
            telemetry.CommitCount == 0 ? 0 : (double)telemetry.BytesWritten / telemetry.CommitCount,
            itemCount == 0 ? 0 : telemetry.BytesWritten / (double)itemCount,
            telemetry.SetLengthCallCount,
            telemetry.Checksum,
            telemetry.PublicationDirtyShelfCount,
            telemetry.PublicationFullShelfBytes,
            telemetry.PublicationRawChangedBytes,
            telemetry.PublicationRawChangedRangeCount,
            telemetry.PublicationDeltaCandidateBytes,
            telemetry.PublicationDeltaCandidateRangeCount,
            telemetry.PublicationHeaderDeltaBytes,
            telemetry.PublicationSlotDeltaBytes,
            telemetry.PublicationItemDeltaBytes,
            telemetry.PublicationMaxDeltaCandidateBytesPerShelf,
            telemetry.PublicationFullShelfBetterOrEqualCount,
            telemetry.DeltaShelfCount,
            telemetry.DeltaUngroupedSpanCount,
            telemetry.DeltaUngroupedBytes,
            telemetry.DeltaGrouped512SpanCount,
            telemetry.DeltaGrouped512Bytes,
            telemetry.DeltaGrouped1024SpanCount,
            telemetry.DeltaGrouped1024Bytes,
            telemetry.DeltaGrouped4096SpanCount,
            telemetry.DeltaGrouped4096Bytes,
            telemetry.DeltaGrouped8192SpanCount,
            telemetry.DeltaGrouped8192Bytes,
            telemetry.DeltaPositiveGapCount,
            telemetry.DeltaPositiveGapBytes,
            telemetry.DeltaMaxPositiveGapBytes,
            telemetry.CommitCount == 0 ? 0 : telemetry.CoalescedAdjacentSegmentCount / (double)telemetry.CommitCount,
            telemetry.CommitCount == 0 ? 0 : telemetry.CoalescedGapCount / (double)telemetry.CommitCount,
            telemetry.CommitCount == 0 ? 0 : telemetry.CoalescedGapBytes / (double)telemetry.CommitCount,
            telemetry.MaxCoalescedGapBytes,
            telemetry.CommitCount == 0 ? 0 : telemetry.RejectedGapCount / (double)telemetry.CommitCount,
            telemetry.CommitCount == 0 ? 0 : telemetry.RejectedGapBytes / (double)telemetry.CommitCount,
            telemetry.MaxRejectedGapBytes,
            telemetry.CommitCount == 0 ? 0 : telemetry.OverlapBreakCount / (double)telemetry.CommitCount,
            telemetry.FileCommitSliceBuildTicks * stopwatchTickToNs / itemCount,
            telemetry.FileCommitCoveredRangeMergeTicks * stopwatchTickToNs / itemCount,
            telemetry.FileCommitGroupShapeTicks * stopwatchTickToNs / itemCount,
            telemetry.FileCommitBufferBuildTicks * stopwatchTickToNs / itemCount,
            telemetry.FileCommitGapReadTicks * stopwatchTickToNs / itemCount,
            telemetry.FileCommitBackingWriteTicks * stopwatchTickToNs / itemCount,
            1);
    }


    /// <summary>
    /// Executes the SQLite insert loop with one transaction per generated batch.<br/>
    /// Keys are transformed to signed integers with an order-preserving unsigned mapping so SQLite's native integer B-tree ordering matches the `SS8-8` unsigned scalar order.<br/>
    /// </summary>
    /// <param name="connection">The open SQLite connection.</param>
    /// <param name="batches">The number of transactions to commit.</param>
    /// <param name="itemsPerBatch">The item count inserted in each transaction.</param>
    /// <param name="prefixCount">The number of root-prefix buckets covered by generated keys.</param>
    /// <param name="order">The deterministic insertion order vector.</param>
    /// <param name="batchOffset">The logical batch offset used to keep warmup and measured keys disjoint.</param>
    /// <returns>A checksum over inserted keys.</returns>
    private static long RunSqliteScalar8Scalar8WriteLoop(
        SqliteConnection connection,
        int batches,
        int itemsPerBatch,
        int prefixCount,
        int[] order,
        int batchOffset)
    {
        long checksum = 0;
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT INTO items(k, i) VALUES($k, $i);";
        SqliteParameter keyParameter = command.Parameters.Add("$k", SqliteType.Integer);
        SqliteParameter identityParameter = command.Parameters.Add("$i", SqliteType.Blob);
        for (int batch = 0; batch < batches; batch++)
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            command.Transaction = transaction;
            int logicalBatch = batchOffset + batch;
            for (int i = 0; i < itemsPerBatch; i++)
            {
                ulong key = CreateRoutedBulkWriteKey(logicalBatch, order[i], itemsPerBatch, prefixCount);
                long sortable = EncodeSqliteSortableUnsignedScalar8(key);
                keyParameter.Value = sortable;
                identityParameter.Value = sortable;
                if (command.ExecuteNonQuery() != 1)
                {
                    throw new InvalidDataException("SQLite comparison insert did not affect exactly one row.");
                }

                checksum += unchecked((long)((key >> 32) ^ key ^ (ulong)(i + 1)));
            }

            transaction.Commit();
            command.Transaction = null;
        }

        return checksum;
    }


    /// <summary>
    /// Executes the SQLite `SS16-16` BLOB insert loop with one transaction per generated batch.<br/>
    /// Key and identity values are fixed 16-byte big-endian blobs, preserving unsigned order under SQLite binary BLOB comparison.<br/>
    /// </summary>
    /// <param name="connection">The open SQLite connection.</param>
    /// <param name="batches">The number of transactions to commit.</param>
    /// <param name="itemsPerBatch">The number of rows inserted in each transaction.</param>
    /// <param name="prefixCount">The number of root prefixes covered by each generated batch.</param>
    /// <param name="order">The deterministic insertion order vector.</param>
    /// <param name="batchOffset">The logical batch offset used to keep warmup and measured keys disjoint.</param>
    /// <returns>A checksum over inserted key halves.</returns>
    private static long RunSqliteScalar16Scalar16BlobWriteLoop(
        SqliteConnection connection,
        int batches,
        int itemsPerBatch,
        int prefixCount,
        int[] order,
        int batchOffset)
    {
        long checksum = 0;
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT INTO items(k, i) VALUES($k, $i);";
        SqliteParameter keyParameter = command.Parameters.Add("$k", SqliteType.Blob);
        SqliteParameter identityParameter = command.Parameters.Add("$i", SqliteType.Blob);
        for (int batch = 0; batch < batches; batch++)
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            command.Transaction = transaction;
            int logicalBatch = batchOffset + batch;
            for (int i = 0; i < itemsPerBatch; i++)
            {
                ulong keyHigh = CreateRoutedBulkWriteKey(logicalBatch, order[i], itemsPerBatch, prefixCount);
                ulong keyLow = CreateSqliteScalar16Scalar16LowHalf(keyHigh);
                byte[] keyBlob = CreateSqliteScalar16Scalar16Blob(keyHigh, keyLow);
                keyParameter.Value = keyBlob;
                identityParameter.Value = keyBlob;
                if (command.ExecuteNonQuery() != 1)
                {
                    throw new InvalidDataException("SQLite SS16-16 BLOB comparison insert did not affect exactly one row.");
                }

                checksum += unchecked((long)(keyHigh ^ keyLow ^ (ulong)(i + 1)));
            }

            transaction.Commit();
            command.Transaction = null;
        }

        return checksum;
    }


    /// <summary>
    /// Executes the SQLite `SS16-8` insert loop with one transaction per generated batch.<br/>
    /// Keys are fixed 16-byte big-endian BLOBs and identities use the order-preserving signed integer mapping used by the `SS8-8` SQLite comparison.<br/>
    /// </summary>
    /// <param name="connection">The open SQLite connection.</param>
    /// <param name="batches">The number of transactions to commit.</param>
    /// <param name="itemsPerBatch">The number of rows inserted in each transaction.</param>
    /// <param name="prefixCount">The number of root prefixes covered by each generated batch.</param>
    /// <param name="order">The deterministic insertion order vector.</param>
    /// <param name="batchOffset">The logical batch offset used to keep warmup and measured keys disjoint.</param>
    /// <returns>A checksum over inserted key and identity values.</returns>
    private static long RunSqliteScalar16Scalar8BlobWriteLoop(
        SqliteConnection connection,
        int batches,
        int itemsPerBatch,
        int prefixCount,
        int[] order,
        int batchOffset)
    {
        long checksum = 0;
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT INTO items(k, i) VALUES($k, $i);";
        SqliteParameter keyParameter = command.Parameters.Add("$k", SqliteType.Blob);
        SqliteParameter identityParameter = command.Parameters.Add("$i", SqliteType.Integer);
        for (int batch = 0; batch < batches; batch++)
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            command.Transaction = transaction;
            int logicalBatch = batchOffset + batch;
            for (int i = 0; i < itemsPerBatch; i++)
            {
                ulong keyHigh = CreateRoutedBulkWriteKey(logicalBatch, order[i], itemsPerBatch, prefixCount);
                ulong keyLow = CreateSqliteScalar16Scalar16LowHalf(keyHigh);
                byte[] keyBlob = CreateSqliteScalar16Scalar16Blob(keyHigh, keyLow);
                long identity = EncodeSqliteSortableUnsignedScalar8(keyHigh);
                keyParameter.Value = keyBlob;
                identityParameter.Value = identity;
                if (command.ExecuteNonQuery() != 1)
                {
                    throw new InvalidDataException("SQLite SS16-8 BLOB comparison insert did not affect exactly one row.");
                }

                checksum += unchecked((long)(keyHigh ^ keyLow ^ (ulong)identity ^ (ulong)(i + 1)));
            }

            transaction.Commit();
            command.Transaction = null;
        }

        return checksum;
    }


    /// <summary>
    /// Executes the SQLite `FS32-8` insert loop with one transaction per generated batch.<br/>
    /// Keys are fixed 32-byte big-endian BLOBs and identities use the order-preserving signed integer mapping used by the scalar SQLite comparison.<br/>
    /// </summary>
    /// <param name="connection">The open SQLite connection.</param>
    /// <param name="batches">The number of transactions to commit.</param>
    /// <param name="itemsPerBatch">The number of rows inserted in each transaction.</param>
    /// <param name="prefixCount">The number of root prefixes covered by each generated batch.</param>
    /// <param name="order">The deterministic insertion order vector.</param>
    /// <param name="batchOffset">The logical batch offset used to keep warmup and measured keys disjoint.</param>
    /// <returns>A checksum over inserted key and identity values.</returns>
    private static long RunSqliteFixed32Scalar8BlobWriteLoop(
        SqliteConnection connection,
        int batches,
        int itemsPerBatch,
        int prefixCount,
        int[] order,
        int batchOffset)
    {
        long checksum = 0;
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT INTO items(k, i) VALUES($k, $i);";
        SqliteParameter keyParameter = command.Parameters.Add("$k", SqliteType.Blob);
        SqliteParameter identityParameter = command.Parameters.Add("$i", SqliteType.Integer);
        for (int batch = 0; batch < batches; batch++)
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            command.Transaction = transaction;
            int logicalBatch = batchOffset + batch;
            for (int i = 0; i < itemsPerBatch; i++)
            {
                CreateFixed32Scalar8BulkWriteKey(logicalBatch, order[i], itemsPerBatch, prefixCount, out ulong key0, out ulong key1, out ulong key2, out ulong key3);
                byte[] keyBlob = CreateSqliteFixed32Scalar8Blob(key0, key1, key2, key3);
                long identity = EncodeSqliteSortableUnsignedScalar8(key0);
                keyParameter.Value = keyBlob;
                identityParameter.Value = identity;
                if (command.ExecuteNonQuery() != 1)
                {
                    throw new InvalidDataException("SQLite FS32-8 BLOB comparison insert did not affect exactly one row.");
                }

                checksum += unchecked((long)(key0 ^ key1 ^ key2 ^ key3 ^ (ulong)identity ^ (ulong)(i + 1)));
            }

            transaction.Commit();
            command.Transaction = null;
        }

        return checksum;
    }


    /// <summary>
    /// Executes the SQLite `SS8-16` insert loop with one transaction per generated batch.<br/>
    /// Keys use the order-preserving signed integer mapping and identities are fixed 16-byte big-endian BLOBs so both payload widths stay explicit.<br/>
    /// </summary>
    /// <param name="connection">The open SQLite connection.</param>
    /// <param name="batches">The number of transactions to commit.</param>
    /// <param name="itemsPerBatch">The number of rows inserted in each transaction.</param>
    /// <param name="prefixCount">The number of root prefixes covered by each generated batch.</param>
    /// <param name="order">The deterministic insertion order vector.</param>
    /// <param name="batchOffset">The logical batch offset used to keep warmup and measured keys disjoint.</param>
    /// <returns>A checksum over inserted key and identity values.</returns>
    private static long RunSqliteScalar8Scalar16BlobWriteLoop(
        SqliteConnection connection,
        int batches,
        int itemsPerBatch,
        int prefixCount,
        int[] order,
        int batchOffset)
    {
        long checksum = 0;
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT INTO items(k, i) VALUES($k, $i);";
        SqliteParameter keyParameter = command.Parameters.Add("$k", SqliteType.Integer);
        SqliteParameter identityParameter = command.Parameters.Add("$i", SqliteType.Blob);
        for (int batch = 0; batch < batches; batch++)
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            command.Transaction = transaction;
            int logicalBatch = batchOffset + batch;
            for (int i = 0; i < itemsPerBatch; i++)
            {
                ulong key = CreateRoutedBulkWriteKey(logicalBatch, order[i], itemsPerBatch, prefixCount);
                ulong identityLow = CreateSqliteScalar16Scalar16LowHalf(key);
                byte[] identityBlob = CreateSqliteScalar16Scalar16Blob(key, identityLow);
                long sortableKey = EncodeSqliteSortableUnsignedScalar8(key);
                keyParameter.Value = sortableKey;
                identityParameter.Value = identityBlob;
                if (command.ExecuteNonQuery() != 1)
                {
                    throw new InvalidDataException("SQLite SS8-16 BLOB comparison insert did not affect exactly one row.");
                }

                checksum += unchecked((long)(key ^ identityLow ^ (ulong)sortableKey ^ (ulong)(i + 1)));
            }

            transaction.Commit();
            command.Transaction = null;
        }

        return checksum;
    }


    /// <summary>
    /// Prints the routed bulk-write benchmark rows to the console.<br/>
    /// This mirrors the markdown result shape so quick Release runs can be read without opening the report file.<br/>
    /// </summary>
    /// <param name="results">The routed bulk-write rows to print.</param>
    private static void PrintScalar8Scalar8RoutedBulkWriteResults(ReadOnlySpan<Scalar8Scalar8RoutedBulkWriteResult> results)
    {
        Console.WriteLine("| scenario | samples | items/sec | items vs prev | us/item | batches/sec | commits | deferred/batch | writes/batch | bytes/batch | bytes/item | bytes/item vs prev | MiB/s | raw commit % | setLength | checksum |");
        Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        for (int i = 0; i < results.Length; i++)
        {
            Scalar8Scalar8RoutedBulkWriteResult result = results[i];
            double previousItemsPerSecond = GetPreviousPublicBulkWriteItemsPerSecond(result.Name);
            double rawCommitPercent = CalculateBaselinePercent(result.MiBs, FileReserveCommitBaselineMiBs);
            Console.WriteLine($"| {result.Name} | {result.SampleCount} | {result.ItemsPerSecond:F2} | {FormatPercentChange(result.ItemsPerSecond, previousItemsPerSecond)} | {result.MicrosecondsPerItem:F3} | {result.BatchesPerSecond:F2} | {result.CommitCount} | {result.DeferredCommitRequestsPerBatch:F3} | {result.WritesPerBatch:F3} | {result.BytesPerBatch:F2} | {result.BytesPerItem:F2} | {FormatPercentChange(result.BytesPerItem, PreviousPublicBulkWriteBytesPerItem)} | {result.MiBs:F2} | {FormatBaselinePercent(rawCommitPercent)} | {result.SetLengthCallCount} | {result.Checksum} |");
        }
    }


    /// <summary>
    /// Prints routed cadence-suite rows to the console.<br/>
    /// The compact console table mirrors the report headline so long suite runs can be inspected without opening the markdown artifact.<br/>
    /// </summary>
    /// <param name="rows">The routed cadence-suite rows to print.</param>
    private static void PrintScalar8Scalar8RoutedCadenceSuiteResults(IReadOnlyList<Scalar8Scalar8RoutedCadenceResult> rows)
    {
        Console.WriteLine("| cadence | order | batches | warmup | items/batch | samples | items/sec | us/item | writes/batch | bytes/item | MiB/s | setLength | checksum |");
        Console.WriteLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        for (int i = 0; i < rows.Count; i++)
        {
            Scalar8Scalar8RoutedCadenceResult row = rows[i];
            Scalar8Scalar8RoutedBulkWriteResult result = row.Result;
            Console.WriteLine($"| {row.CadenceName} | {row.OrderName} | {result.Batches} | {row.WarmupBatches} | {result.ItemsPerBatch} | {result.SampleCount} | {result.ItemsPerSecond:F2} | {result.MicrosecondsPerItem:F3} | {result.WritesPerBatch:F3} | {result.BytesPerItem:F2} | {result.MiBs:F2} | {result.SetLengthCallCount} | {result.Checksum} |");
        }
    }


    /// <summary>
    /// Prints routed write-attribution rows to the console.<br/>
    /// The compact console output focuses on timing, repeated shelf rewrites, and planned-versus-written byte shape for quick lower-layer diagnosis.<br/>
    /// </summary>
    /// <param name="results">The write-attribution rows to print.</param>
    private static void PrintScalar8Scalar8WriteAttributionResults(ReadOnlySpan<Scalar8Scalar8WriteAttributionResult> results)
    {
        Console.WriteLine("| scenario | samples | items/sec | us/item | insert ns/item | commit ns/item | no split | normal splits | transform splits | same-shelf revisits | planned B/item | written B/item | staged seg/batch | writes/batch |");
        Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        for (int i = 0; i < results.Length; i++)
        {
            Scalar8Scalar8WriteAttributionResult result = results[i];
            Console.WriteLine($"| {result.Name} | {result.SampleCount} | {result.ItemsPerSecond:F2} | {result.MicrosecondsPerItem:F3} | {result.InsertNsPerItem:F2} | {result.CommitWallNsPerItem:F2} | {result.NoSplitCount} | {result.NormalSplitCount} | {result.TransformSplitCount} | {result.SameShelfRewriteRevisitCount} | {result.PlannedBytesPerItem:F2} | {result.WrittenBytesPerItem:F2} | {result.StagedSegmentsPerBatch:F3} | {result.WritesPerBatch:F3} |");
        }

        Console.WriteLine("| scenario | route-cache probe ns/item | root lookup ns/item | route walk ns/item | classify ns/item | cache lookup ns/item | shelf insert ns/item | append fast ns/item | append fast % | lower-bound ns/item | slot move ns/item | cached handled % | route-cache hit % |");
        Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        for (int i = 0; i < results.Length; i++)
        {
            Scalar8Scalar8WriteAttributionResult result = results[i];
            double handledRate = result.CachedAttemptCount == 0 ? 0 : result.CachedHandledCount * 100d / result.CachedAttemptCount;
            double routeCacheHitRate = result.CachedAttemptCount == 0 ? 0 : result.RouteCacheHitCount * 100d / result.CachedAttemptCount;
            double appendFastRate = result.CachedHandledCount == 0 ? 0 : result.ShelfAppendFastPathCount * 100d / result.CachedHandledCount;
            Console.WriteLine($"| {result.Name} | {result.RouteCacheProbeNsPerItem:F2} | {result.RootLookupNsPerItem:F2} | {result.RouteWalkNsPerItem:F2} | {result.RouteTargetClassificationNsPerItem:F2} | {result.ShelfCacheLookupNsPerItem:F2} | {result.ShelfInsertNsPerItem:F2} | {result.ShelfAppendFastPathNsPerItem:F2} | {appendFastRate:F2}% | {result.ShelfLowerBoundNsPerItem:F2} | {result.ShelfSlotMoveNsPerItem:F2} | {handledRate:F2}% | {routeCacheHitRate:F2}% |");
        }

        Console.WriteLine("| scenario | direct view lookup ns/item | direct prefix ns/item | direct slot load ns/item | direct classify ns/item | direct router targets | direct shelf targets | non-direct read ns/item | non-direct lookup ns/item | non-direct classify ns/item |");
        Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        for (int i = 0; i < results.Length; i++)
        {
            Scalar8Scalar8WriteAttributionResult result = results[i];
            Console.WriteLine($"| {result.Name} | {result.RouteWalkDirectViewLookupNsPerItem:F2} | {result.RouteWalkDirectPrefixNsPerItem:F2} | {result.RouteWalkDirectSlotLoadNsPerItem:F2} | {result.RouteWalkDirectTargetClassificationNsPerItem:F2} | {result.RouteWalkDirectRouterTargetCount} | {result.RouteWalkDirectShelfTargetCount} | {result.RouteWalkNonDirectRouterReadNsPerItem:F2} | {result.RouteWalkNonDirectRouteLookupNsPerItem:F2} | {result.RouteWalkNonDirectTargetClassificationNsPerItem:F2} |");
        }

        Console.WriteLine("| scenario | shelf probe ns/item | shelf-cache hits | shelf-cache misses | shelf read ns/item | snapshot clone ns/item | entry create ns/item | entry add ns/item | shelf-cache hit % |");
        Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|");
        for (int i = 0; i < results.Length; i++)
        {
            Scalar8Scalar8WriteAttributionResult result = results[i];
            long shelfCacheAttempts = result.ShelfCacheHitCount + result.ShelfCacheMissCount;
            double shelfCacheHitRate = shelfCacheAttempts == 0 ? 0 : result.ShelfCacheHitCount * 100d / shelfCacheAttempts;
            Console.WriteLine($"| {result.Name} | {result.ShelfCacheProbeNsPerItem:F2} | {result.ShelfCacheHitCount} | {result.ShelfCacheMissCount} | {result.ShelfCacheReadNsPerItem:F2} | {result.ShelfCacheSnapshotCloneNsPerItem:F2} | {result.ShelfCacheEntryCreateNsPerItem:F2} | {result.ShelfCacheEntryAddNsPerItem:F2} | {shelfCacheHitRate:F2}% |");
        }
    }


    /// <summary>
    /// Prints public bulk-read benchmark rows to the console.<br/>
    /// The shape mirrors the markdown report and keeps read telemetry visible during focused runs.<br/>
    /// </summary>
    /// <param name="results">The public bulk-read rows to print.</param>
    private static void PrintScalar8Scalar8PublicBulkReadResults(ReadOnlySpan<Scalar8Scalar8PublicBulkReadResult> results)
    {
        Console.WriteLine("| scenario | samples | iterations | range identities | elapsed ms | identities/sec | ids/sec vs prev | ns/identity | ns/id vs prev | reads/range | bytes/range | effective MiB/s | raw read % | checksum |");
        Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        for (int i = 0; i < results.Length; i++)
        {
            Scalar8Scalar8PublicBulkReadResult result = results[i];
            double effectiveMiBs = CalculateMiBs(result.BytesPerRange * result.Iterations, result.Elapsed.TotalSeconds);
            double rawReadPercent = CalculateBaselinePercent(effectiveMiBs, FileReserveReadBaselineMiBs);
            double previousIdentitiesPerSecond = GetPreviousPublicReadBaseline(result.Name, nanoseconds: false);
            double previousNsPerIdentity = GetPreviousPublicReadBaseline(result.Name, nanoseconds: true);
            Console.WriteLine($"| {result.Name} | {result.SampleCount} | {result.Iterations} | {result.RangeIdentityCount} | {result.Elapsed.TotalMilliseconds:F3} | {result.IdentitiesPerSecond:F2} | {FormatPercentChange(result.IdentitiesPerSecond, previousIdentitiesPerSecond)} | {result.NsPerIdentity:F2} | {FormatPercentChange(result.NsPerIdentity, previousNsPerIdentity)} | {result.ReadsPerRange:F3} | {result.BytesPerRange:F2} | {effectiveMiBs:F2} | {FormatBaselinePercent(rawReadPercent)} | {result.Checksum} |");
        }
    }


    /// <summary>
    /// Prints typed unsigned routed bulk-write benchmark rows to the console.<br/>
    /// The shape mirrors the typed markdown comparison report and keeps encoded-baseline drift visible during focused runs.<br/>
    /// </summary>
    /// <param name="results">The typed routed bulk-write rows to print.</param>
    private static void PrintUnsignedScalar8Scalar8RoutedBulkWriteResults(ReadOnlySpan<Scalar8Scalar8RoutedBulkWriteResult> results)
    {
        Console.WriteLine("| scenario | samples | items/sec | items/sec vs encoded | us/item | writes/batch | bytes/item | bytes/item vs encoded | setLength | checksum |");
        Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        for (int i = 0; i < results.Length; i++)
        {
            Scalar8Scalar8RoutedBulkWriteResult result = results[i];
            double encodedItemsPerSecond = GetEncodedMedianPublicBulkWriteItemsPerSecond(result.Name);
            Console.WriteLine($"| {result.Name} | {result.SampleCount} | {result.ItemsPerSecond:F2} | {FormatPercentChange(result.ItemsPerSecond, encodedItemsPerSecond)} | {result.MicrosecondsPerItem:F3} | {result.WritesPerBatch:F3} | {result.BytesPerItem:F2} | {FormatPercentChange(result.BytesPerItem, EncodedMedianPublicBulkWriteBytesPerItem)} | {result.SetLengthCallCount} | {result.Checksum} |");
        }
    }


    /// <summary>
    /// Prints harness-level `SS16-16` routed bulk-write rows to the console.<br/>
    /// The table includes commit folding and bytes per item so it can be read beside the SQLite BLOB comparison output.<br/>
    /// </summary>
    /// <param name="results">The routed bulk-write rows to print.</param>
    private static void PrintScalar16Scalar16RoutedBulkWriteResults(ReadOnlySpan<Scalar8Scalar8RoutedBulkWriteResult> results)
    {
        Console.WriteLine("| scenario | samples | items/sec | us/item | batches/sec | commits | deferred/batch | writes/batch | bytes/batch | bytes/item | MiB/s | setLength | checksum |");
        Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        for (int i = 0; i < results.Length; i++)
        {
            Scalar8Scalar8RoutedBulkWriteResult result = results[i];
            Console.WriteLine($"| {result.Name} | {result.SampleCount} | {result.ItemsPerSecond:F2} | {result.MicrosecondsPerItem:F3} | {result.BatchesPerSecond:F2} | {result.CommitCount} | {result.DeferredCommitRequestsPerBatch:F3} | {result.WritesPerBatch:F3} | {result.BytesPerBatch:F2} | {result.BytesPerItem:F2} | {result.MiBs:F2} | {result.SetLengthCallCount} | {result.Checksum} |");
        }
    }


    /// <summary>
    /// Prints the compact all-shape write-parity table to the console.<br/>
    /// The table keeps the three comparison lanes together so outliers can be seen immediately during Release runs.<br/>
    /// </summary>
    /// <param name="rows">The measured all-shape parity rows.</param>
    private static void PrintAllShapeWriteParityResults(ReadOnlySpan<AllShapeWriteParityRow> rows)
    {
        Console.WriteLine("| shape | order | payload B | LibraDex items/sec | SQLite items/sec | raw DK items/sec | LibraDex/SQLite | LibraDex/raw | LibraDex B/item | SQLite B/item | raw B/item | LibraDex writes/batch | raw writes/batch |");
        Console.WriteLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        for (int i = 0; i < rows.Length; i++)
        {
            AllShapeWriteParityRow row = rows[i];
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"| {row.Shape} | {row.OrderName} | {row.PayloadBytes} | {row.LibraDex.ItemsPerSecond:F2} | {row.Sqlite.ItemsPerSecond:F2} | {row.RawDataKernel.ItemsPerSecond:F2} | {FormatMultiplier(row.LibraDex.ItemsPerSecond, row.Sqlite.ItemsPerSecond)} | {FormatMultiplier(row.LibraDex.ItemsPerSecond, row.RawDataKernel.ItemsPerSecond)} | {row.LibraDex.BytesPerItem:F2} | {row.Sqlite.StorageBytesPerItem:F2} | {row.RawDataKernel.BytesPerItem:F2} | {row.LibraDex.WritesPerBatch:F3} | {row.RawDataKernel.WritesPerBatch:F3} |"));
        }
    }


    private static void PrintFixed32Scalar8ReadParityResults(
        ReadOnlySpan<Fixed32Scalar8ReadParityResult> libraResults,
        ReadOnlySpan<SqliteScalar8Scalar8ReadResult> sqliteResults)
    {
        Console.WriteLine("| scenario | samples | FS32-16 ids/sec | SQLite ids/sec | FS32-16/SQLite | FS32-16 ns/id | SQLite ns/id | FS32-16 reads/range | FS32-16 bytes/range |");
        Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|");
        for (int i = 0; i < libraResults.Length; i++)
        {
            Fixed32Scalar8ReadParityResult libra = libraResults[i];
            SqliteScalar8Scalar8ReadResult sqlite = sqliteResults[i];
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"| {libra.Name} | {libra.SampleCount} | {libra.IdentitiesPerSecond:F2} | {sqlite.IdentitiesPerSecond:F2} | {FormatMultiplier(libra.IdentitiesPerSecond, sqlite.IdentitiesPerSecond)} | {libra.NsPerIdentity:F2} | {sqlite.NsPerIdentity:F2} | {libra.ReadsPerRange:F3} | {libra.BytesPerRange:F2} |"));
        }
    }


    private static void PrintFixed32Scalar8SizeSweepRows(ReadOnlySpan<Fixed32Scalar8SizeSweepRow> rows)
    {
        Console.WriteLine("| shelf | max items | sorted items/sec | random items/sec | write B/item | p0 ids/sec | p0 ns/id | p0 bytes/range | p0-2 ids/sec | p0-2 ns/id | p0-2 bytes/range |");
        Console.WriteLine("|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        for (int i = 0; i < rows.Length; i++)
        {
            Fixed32Scalar8SizeSweepRow row = rows[i];
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"| {row.Profile.ShelfExtentSize} | {row.Profile.MaxItemCount} | {row.SortedWrite.ItemsPerSecond:F2} | {row.RandomWrite.ItemsPerSecond:F2} | {row.RandomWrite.BytesPerItem:F2} | {row.Prefix0Read.IdentitiesPerSecond:F2} | {row.Prefix0Read.NsPerIdentity:F2} | {row.Prefix0Read.BytesPerRange:F2} | {row.PrefixSpanRead.IdentitiesPerSecond:F2} | {row.PrefixSpanRead.NsPerIdentity:F2} | {row.PrefixSpanRead.BytesPerRange:F2} |"));
        }
    }


    private static void PrintFixed32Scalar16SizeSweepRows(ReadOnlySpan<Fixed32Scalar16SizeSweepRow> rows)
    {
        Console.WriteLine("| shelf | max items | sorted items/sec | random items/sec | write B/item | p0 ids/sec | p0 ns/id | p0 bytes/range | p0-2 ids/sec | p0-2 ns/id | p0-2 bytes/range |");
        Console.WriteLine("|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        for (int i = 0; i < rows.Length; i++)
        {
            Fixed32Scalar16SizeSweepRow row = rows[i];
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"| {row.Profile.ShelfExtentSize} | {row.Profile.MaxItemCount} | {row.SortedWrite.ItemsPerSecond:F2} | {row.RandomWrite.ItemsPerSecond:F2} | {row.RandomWrite.BytesPerItem:F2} | {row.Prefix0Read.IdentitiesPerSecond:F2} | {row.Prefix0Read.NsPerIdentity:F2} | {row.Prefix0Read.BytesPerRange:F2} | {row.PrefixSpanRead.IdentitiesPerSecond:F2} | {row.PrefixSpanRead.NsPerIdentity:F2} | {row.PrefixSpanRead.BytesPerRange:F2} |"));
        }
    }


    /// <summary>
    /// Prints typed unsigned public bulk-read benchmark rows to the console.<br/>
    /// The shape mirrors the typed markdown comparison report and keeps encoded-baseline drift visible during focused runs.<br/>
    /// </summary>
    /// <param name="results">The typed public bulk-read rows to print.</param>
    private static void PrintUnsignedScalar8Scalar8PublicBulkReadResults(ReadOnlySpan<Scalar8Scalar8PublicBulkReadResult> results)
    {
        Console.WriteLine("| scenario | samples | range identities | identities/sec | ids/sec vs encoded | ns/identity | ns/id vs encoded | reads/range | bytes/range | checksum |");
        Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        for (int i = 0; i < results.Length; i++)
        {
            Scalar8Scalar8PublicBulkReadResult result = results[i];
            double encodedIdentitiesPerSecond = GetEncodedMedianPublicReadBaseline(result.Name, nanoseconds: false);
            double encodedNsPerIdentity = GetEncodedMedianPublicReadBaseline(result.Name, nanoseconds: true);
            Console.WriteLine($"| {result.Name} | {result.SampleCount} | {result.RangeIdentityCount} | {result.IdentitiesPerSecond:F2} | {FormatPercentChange(result.IdentitiesPerSecond, encodedIdentitiesPerSecond)} | {result.NsPerIdentity:F2} | {FormatPercentChange(result.NsPerIdentity, encodedNsPerIdentity)} | {result.ReadsPerRange:F3} | {result.BytesPerRange:F2} | {result.Checksum} |");
        }
    }


    private static void CreateFixed32Scalar8PrefixRangeBounds(
        int lowerPrefix,
        int upperPrefix,
        out ulong lower0,
        out ulong lower1,
        out ulong lower2,
        out ulong lower3,
        out ulong upper0,
        out ulong upper1,
        out ulong upper2,
        out ulong upper3)
    {
        lower0 = (ulong)(byte)lowerPrefix << 56;
        lower1 = 0;
        lower2 = 0;
        lower3 = 0;
        upper0 = ((ulong)(byte)upperPrefix << 56) | 0x00FF_FFFF_FFFF_FFFFUL;
        upper1 = ulong.MaxValue;
        upper2 = ulong.MaxValue;
        upper3 = ulong.MaxValue;
    }


    /// <summary>
    /// Prints SQLite write comparison rows to the console.<br/>
    /// The console shape mirrors the markdown report so focused Release runs can be inspected without opening the artifact file.<br/>
    /// </summary>
    /// <param name="results">The SQLite write result rows.</param>
    private static void PrintSqliteScalar8Scalar8WriteResults(ReadOnlySpan<SqliteScalar8Scalar8WriteResult> results)
    {
        Console.WriteLine("| scenario | samples | items/sec | items/sec vs SS8-8 | us/item | batches/sec | storage bytes | storage bytes/item | checksum |");
        Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|");
        for (int i = 0; i < results.Length; i++)
        {
            SqliteScalar8Scalar8WriteResult result = results[i];
            double encodedItemsPerSecond = GetEncodedMedianPublicBulkWriteItemsPerSecond(result.Name);
            Console.WriteLine($"| {result.Name} | {result.SampleCount} | {result.ItemsPerSecond:F2} | {FormatPercentChange(result.ItemsPerSecond, encodedItemsPerSecond)} | {result.MicrosecondsPerItem:F3} | {result.BatchesPerSecond:F2} | {result.StorageBytes} | {result.StorageBytesPerItem:F2} | {result.Checksum} |");
        }
    }


    /// <summary>
    /// Prints SQLite read comparison rows to the console.<br/>
    /// The console shape mirrors the markdown report and keeps `SS8-8` checkpoint distance visible during focused runs.<br/>
    /// </summary>
    /// <param name="results">The SQLite read result rows.</param>
    private static void PrintSqliteScalar8Scalar8ReadResults(ReadOnlySpan<SqliteScalar8Scalar8ReadResult> results)
    {
        Console.WriteLine("| scenario | samples | iterations | range identities | identities/sec | ids/sec vs SS8-8 | ns/identity | ns/id vs SS8-8 | logical MiB/s | checksum |");
        Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        for (int i = 0; i < results.Length; i++)
        {
            SqliteScalar8Scalar8ReadResult result = results[i];
            double encodedIdentitiesPerSecond = GetEncodedMedianPublicReadBaseline(result.Name, nanoseconds: false);
            double encodedNsPerIdentity = GetEncodedMedianPublicReadBaseline(result.Name, nanoseconds: true);
            Console.WriteLine($"| {result.Name} | {result.SampleCount} | {result.Iterations} | {result.RangeIdentityCount} | {result.IdentitiesPerSecond:F2} | {FormatPercentChange(result.IdentitiesPerSecond, encodedIdentitiesPerSecond)} | {result.NsPerIdentity:F2} | {FormatPercentChange(result.NsPerIdentity, encodedNsPerIdentity)} | {result.LogicalMiBs:F2} | {result.Checksum} |");
        }
    }


    /// <summary>
    /// Prints SQLite `SS16-16` BLOB write comparison rows to the console.<br/>
    /// This report has no current LibraDex public `SS16-16` bulk-write baseline, so it exposes raw SQLite throughput and storage size only.<br/>
    /// </summary>
    /// <param name="results">The SQLite BLOB write result rows.</param>
    private static void PrintSqliteScalar16Scalar16BlobWriteResults(ReadOnlySpan<SqliteScalar8Scalar8WriteResult> results)
    {
        Console.WriteLine("| scenario | samples | items/sec | us/item | batches/sec | storage bytes | storage bytes/item | checksum |");
        Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|");
        for (int i = 0; i < results.Length; i++)
        {
            SqliteScalar8Scalar8WriteResult result = results[i];
            Console.WriteLine($"| {result.Name} | {result.SampleCount} | {result.ItemsPerSecond:F2} | {result.MicrosecondsPerItem:F3} | {result.BatchesPerSecond:F2} | {result.StorageBytes} | {result.StorageBytesPerItem:F2} | {result.Checksum} |");
        }
    }


    /// <summary>
    /// Prints SQLite `SS16-16` BLOB read comparison rows to the console.<br/>
    /// Logical MiB/s uses 32 returned payload bytes per identity to match the `SS16-16` item width.<br/>
    /// </summary>
    /// <param name="results">The SQLite BLOB read result rows.</param>
    private static void PrintSqliteScalar16Scalar16BlobReadResults(ReadOnlySpan<SqliteScalar8Scalar8ReadResult> results)
    {
        Console.WriteLine("| scenario | samples | iterations | range identities | identities/sec | ns/identity | logical MiB/s | checksum |");
        Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|");
        for (int i = 0; i < results.Length; i++)
        {
            SqliteScalar8Scalar8ReadResult result = results[i];
            Console.WriteLine($"| {result.Name} | {result.SampleCount} | {result.Iterations} | {result.RangeIdentityCount} | {result.IdentitiesPerSecond:F2} | {result.NsPerIdentity:F2} | {result.LogicalMiBs:F2} | {result.Checksum} |");
        }
    }


    /// <summary>
    /// Creates the SQLite comparison table and index shape.<br/>
    /// `WITHOUT ROWID` keeps the primary-key B-tree as the table itself, avoiding an extra rowid B-tree that would not match LibraDex's identity-index role.<br/>
    /// </summary>
    /// <param name="connection">The open SQLite connection.</param>
    private static void InitializeSqliteScalar8Scalar8Schema(SqliteConnection connection)
    {
        ExecuteSqliteScalar8Scalar8NonQuery(
            connection,
            "CREATE TABLE items(k INTEGER NOT NULL, i INTEGER NOT NULL, PRIMARY KEY(k, i)) WITHOUT ROWID;");
    }


    /// <summary>
    /// Creates the SQLite `SS16-16` BLOB comparison table and index shape.<br/>
    /// `WITHOUT ROWID` keeps the primary-key B-tree as the table itself, while fixed-length checks preserve the 16-byte key and 16-byte identity contract.<br/>
    /// </summary>
    /// <param name="connection">The open SQLite connection.</param>
    private static void InitializeSqliteScalar16Scalar16BlobSchema(SqliteConnection connection)
    {
        ExecuteSqliteScalar8Scalar8NonQuery(
            connection,
            "CREATE TABLE items(k BLOB NOT NULL CHECK(length(k)=16), i BLOB NOT NULL CHECK(length(i)=16), PRIMARY KEY(k, i)) WITHOUT ROWID;");
    }


    /// <summary>
    /// Creates the SQLite `SS16-8` mixed-width comparison table and index shape.<br/>
    /// `WITHOUT ROWID` keeps the primary-key B-tree as the table itself, while the fixed-length key check preserves the 16-byte key contract.<br/>
    /// </summary>
    /// <param name="connection">The open SQLite connection.</param>
    private static void InitializeSqliteScalar16Scalar8BlobSchema(SqliteConnection connection)
    {
        ExecuteSqliteScalar8Scalar8NonQuery(
            connection,
            "CREATE TABLE items(k BLOB NOT NULL CHECK(length(k)=16), i INTEGER NOT NULL, PRIMARY KEY(k, i)) WITHOUT ROWID;");
    }


    /// <summary>
    /// Creates the SQLite `SS8-16` mixed-width comparison table and index shape.<br/>
    /// `WITHOUT ROWID` keeps the primary-key B-tree as the table itself, while the fixed-length identity check preserves the 16-byte identity contract.<br/>
    /// </summary>
    /// <param name="connection">The open SQLite connection.</param>
    private static void InitializeSqliteScalar8Scalar16BlobSchema(SqliteConnection connection)
    {
        ExecuteSqliteScalar8Scalar8NonQuery(
            connection,
            "CREATE TABLE items(k INTEGER NOT NULL, i BLOB NOT NULL CHECK(length(i)=16), PRIMARY KEY(k, i)) WITHOUT ROWID;");
    }


    /// <summary>
    /// Creates the SQLite `FS32-8` mixed-width comparison table and index shape.<br/>
    /// `WITHOUT ROWID` keeps the primary-key B-tree as the table itself, while the fixed-length key check preserves the 32-byte key contract.<br/>
    /// </summary>
    /// <param name="connection">The open SQLite connection.</param>
    private static void InitializeSqliteFixed32Scalar8BlobSchema(SqliteConnection connection)
    {
        ExecuteSqliteScalar8Scalar8NonQuery(
            connection,
            "CREATE TABLE items(k BLOB NOT NULL CHECK(length(k)=32), i INTEGER NOT NULL, PRIMARY KEY(k, i)) WITHOUT ROWID;");
    }


    /// <summary>
    /// Creates a fixed 16-byte big-endian SQLite BLOB for one `SS16-16` scalar value.<br/>
    /// SQLite compares BLOB values bytewise, so big-endian unsigned halves preserve the same ordering as the numeric high/low tuple.<br/>
    /// </summary>
    /// <param name="high">The high 64 bits of the scalar value.</param>
    /// <param name="low">The low 64 bits of the scalar value.</param>
    /// <returns>A new 16-byte BLOB value.</returns>
    private static byte[] CreateSqliteScalar16Scalar16Blob(ulong high, ulong low)
    {
        byte[] blob = new byte[16];
        WriteSqliteScalar16Scalar16BigEndianUInt64(blob, 0, high);
        WriteSqliteScalar16Scalar16BigEndianUInt64(blob, 8, low);
        return blob;
    }


    /// <summary>
    /// Creates a fixed 32-byte big-endian SQLite BLOB for one `FS32-8` fixed key.<br/>
    /// SQLite compares BLOB values bytewise, so big-endian unsigned lanes preserve the same ordering as the fixed-key tuple.<br/>
    /// </summary>
    /// <param name="key0">The first 64-bit fixed-key lane.</param>
    /// <param name="key1">The second 64-bit fixed-key lane.</param>
    /// <param name="key2">The third 64-bit fixed-key lane.</param>
    /// <param name="key3">The fourth 64-bit fixed-key lane.</param>
    /// <returns>A new 32-byte BLOB value.</returns>
    private static byte[] CreateSqliteFixed32Scalar8Blob(ulong key0, ulong key1, ulong key2, ulong key3)
    {
        byte[] blob = new byte[32];
        WriteSqliteScalar16Scalar16BigEndianUInt64(blob, 0, key0);
        WriteSqliteScalar16Scalar16BigEndianUInt64(blob, 8, key1);
        WriteSqliteScalar16Scalar16BigEndianUInt64(blob, 16, key2);
        WriteSqliteScalar16Scalar16BigEndianUInt64(blob, 24, key3);
        return blob;
    }


    /// <summary>
    /// Creates the deterministic low half used by SQLite `SS16-16` BLOB comparison values.<br/>
    /// The high half carries the existing routed prefix workload, while this mixed low half ensures SQLite compares full 16-byte values without changing prefix range selection.<br/>
    /// </summary>
    /// <param name="high">The generated high half.</param>
    /// <returns>The deterministic low half.</returns>
    private static ulong CreateSqliteScalar16Scalar16LowHalf(ulong high)
    {
        return unchecked((high << 17) ^ (high >> 23) ^ 0x9E37_79B9_7F4A_7C15UL);
    }


    /// <summary>
    /// Writes one unsigned 64-bit value into a span as big-endian bytes.<br/>
    /// This avoids platform-endianness dependencies in SQLite BLOB ordering tests.<br/>
    /// </summary>
    /// <param name="target">The target span.</param>
    /// <param name="offset">The starting byte offset.</param>
    /// <param name="value">The value to write.</param>
    private static void WriteSqliteScalar16Scalar16BigEndianUInt64(Span<byte> target, int offset, ulong value)
    {
        for (int i = 0; i < sizeof(ulong); i++)
        {
            target[offset + i] = (byte)(value >> ((sizeof(ulong) - 1 - i) * 8));
        }
    }


    /// <summary>
    /// Reads one unsigned 64-bit value from big-endian bytes.<br/>
    /// Returned SQLite identity BLOBs are decoded this way for deterministic validation and checksumming.<br/>
    /// </summary>
    /// <param name="source">The source bytes.</param>
    /// <param name="offset">The starting byte offset.</param>
    /// <returns>The decoded unsigned value.</returns>
    private static ulong ReadSqliteScalar16Scalar16BigEndianUInt64(ReadOnlySpan<byte> source, int offset)
    {
        ulong value = 0;
        for (int i = 0; i < sizeof(ulong); i++)
        {
            value = (value << 8) | source[offset + i];
        }

        return value;
    }


    /// <summary>
    /// Prints public read-target locality rows to the console.<br/>
    /// </summary>
    /// <param name="results">The read-target diagnostic rows.</param>
    private static void PrintScalar8Scalar8PublicReadTargetResults(ReadOnlySpan<Scalar8Scalar8PublicReadTargetResult> results)
    {
        Console.WriteLine("| range | distinct shelves | contiguous runs | max run shelves | avg run shelves | duplicate target skips | router targets | max depth | first offsets |");
        Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|---|");
        for (int i = 0; i < results.Length; i++)
        {
            Scalar8Scalar8PublicReadTargetResult result = results[i];
            Console.WriteLine($"| {result.Name} | {result.DistinctShelfCount} | {result.ContiguousRunCount} | {result.MaxContiguousRunShelves} | {result.AverageContiguousRunShelves:F2} | {result.DuplicateTargetSkipCount} | {result.RouterTargetCount} | {result.MaxRouterDepth} | {result.FirstOffsets} |");
        }
    }


    /// <summary>
    /// Prints the public bulk-build growth row to the console.<br/>
    /// The shape mirrors the markdown report so focused runs expose disk footprint and live-structure estimates immediately.<br/>
    /// </summary>
    /// <param name="result">The public growth result to print.</param>
    private static void PrintScalar8Scalar8PublicGrowth(Scalar8Scalar8PublicGrowthResult result)
    {
        Console.WriteLine("| samples | final bytes | live bytes | metadata | routers bytes | shelves bytes | overhead bytes | routers | shelves | items | capacity | occ % | free slots | file bytes/item | file vs raw 16B | file vs prev | live bytes/item | live vs raw 18B | live vs prev | overhead bytes/item | overhead vs prev | written bytes/item | written vs prev | writes/batch | writes vs prev | setLength |");
        Console.WriteLine("|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        Console.WriteLine($"| {result.SampleCount} | {result.FinalLength} | {result.LiveStructureBytes} | {result.MetadataBytes} | {result.RouterBytes} | {result.ShelfBytes} | {result.OverheadBytes} | {result.RouterCount} | {result.ShelfCount} | {result.ItemCount} | {result.ShelfCapacity} | {result.ShelfOccupancyPercent:F2} | {result.FreeItemSlots} | {result.FileBytesPerItem:F2} | {FormatPercentDistance(result.FileBytesPerItem, Scalar8Scalar8Layout.ItemSize)} | {FormatPercentChange(result.FileBytesPerItem, PreviousPublicGrowthFileBytesPerItem)} | {result.LiveBytesPerItem:F2} | {FormatPercentDistance(result.LiveBytesPerItem, Scalar8Scalar8Layout.ItemSize + Scalar8Scalar8Layout.SlotSize)} | {FormatPercentChange(result.LiveBytesPerItem, PreviousPublicGrowthLiveBytesPerItem)} | {result.OverheadBytesPerItem:F2} | {FormatPercentChange(result.OverheadBytesPerItem, PreviousPublicGrowthOverheadBytesPerItem)} | {result.WrittenBytesPerItem:F2} | {FormatPercentChange(result.WrittenBytesPerItem, PreviousPublicGrowthWrittenBytesPerItem)} | {result.WritesPerBatch:F3} | {FormatPercentChange(result.WritesPerBatch, PreviousPublicGrowthWritesPerBatch)} | {result.SetLengthCallCount} |");
    }


    /// <summary>
    /// Executes the raw shelf-sized extent write loop and aggregates commit telemetry.<br/>
    /// The loop clears each extent before writing logical item payloads so it resembles generated shelf bytes without slot maintenance.<br/>
    /// </summary>
    /// <param name="kernel">The active `DataKernel`.</param>
    /// <param name="batches">The number of committed batches to execute.</param>
    /// <param name="itemsPerBatch">The number of items in each batch.</param>
    /// <param name="order">The deterministic item order vector.</param>
    /// <param name="profile">The `SS8-8` profile that supplies shelf extent size.</param>
    /// <returns>Aggregated write telemetry and checksum.</returns>
    private static WriteParityLoopTelemetry RunDataKernelShelfExtentWriteParityLoop(DataKernel kernel, int batches, int itemsPerBatch, int[] order, Scalar8Scalar8Profile profile)
    {
        WriteParityLoopTelemetry telemetry = default;
        for (int batch = 0; batch < batches; batch++)
        {
            RawDataReservation reservation = kernel.Reserve(profile.ShelfExtentSize);
            reservation.Span.Clear();
            FillWriteParityItems(reservation.Span, itemsPerBatch, order, batch);
            AddWriteParityCommit(kernel.Commit(), ref telemetry);
            telemetry.Checksum += reservation.Extent.Length;
        }

        return telemetry;
    }


    /// <summary>
    /// Executes the real `SS8-8` shelf write loop and aggregates commit telemetry.<br/>
    /// Each batch reserves one full shelf extent, initializes it, inserts all requested items, and commits immediately.<br/>
    /// </summary>
    /// <param name="kernel">The active `DataKernel`.</param>
    /// <param name="batches">The number of committed batches to execute.</param>
    /// <param name="itemsPerBatch">The number of items in each shelf batch.</param>
    /// <param name="order">The deterministic item order vector.</param>
    /// <param name="profile">The `SS8-8` profile that supplies shelf extent size.</param>
    /// <returns>Aggregated write telemetry and checksum.</returns>
    private static WriteParityLoopTelemetry RunScalar8Scalar8ShelfWriteParityLoop(DataKernel kernel, int batches, int itemsPerBatch, int[] order, Scalar8Scalar8Profile profile)
    {
        WriteParityLoopTelemetry telemetry = default;
        for (int batch = 0; batch < batches; batch++)
        {
            RawDataReservation reservation = kernel.Reserve(profile.ShelfExtentSize);
            Scalar8Scalar8 shelf = new(reservation.Span, profile);
            shelf.Initialize();
            for (int i = 0; i < itemsPerBatch; i++)
            {
                ulong value = (ulong)order[i] + ((ulong)batch << 32);
                Scalar8Scalar8InsertResult result = shelf.Insert(
                    Scalar8Scalar8Layout.EncodeUnsignedScalar8(value),
                    Scalar8Scalar8Layout.EncodeUnsignedScalar8(value),
                    allowDuplicateKeys: true);
                if (result != Scalar8Scalar8InsertResult.Inserted)
                {
                    throw new InvalidDataException($"Expected SS8-8 write parity insert, got {result}.");
                }
            }

            AddWriteParityCommit(kernel.Commit(), ref telemetry);
            telemetry.Checksum += shelf.ItemCount;
        }

        return telemetry;
    }


    /// <summary>
    /// Creates the final write-parity result row from elapsed time and aggregate commit telemetry.<br/>
    /// Items per second is the headline metric; MiB/s remains a supporting byte-movement diagnostic.<br/>
    /// </summary>
    /// <param name="name">The result row name.</param>
    /// <param name="batches">The measured batch count.</param>
    /// <param name="itemsPerBatch">The number of items committed per batch.</param>
    /// <param name="elapsed">The measured elapsed time.</param>
    /// <param name="telemetry">The aggregate commit telemetry.</param>
    /// <returns>The completed write-parity row.</returns>
    private static Scalar8Scalar8WriteParityResult CreateWriteParityResult(
        string name,
        int batches,
        int itemsPerBatch,
        TimeSpan elapsed,
        WriteParityLoopTelemetry telemetry)
    {
        long itemCount = (long)batches * itemsPerBatch;
        double seconds = Math.Max(elapsed.TotalSeconds, 0.000000001d);
        return new Scalar8Scalar8WriteParityResult(
            name,
            batches,
            itemsPerBatch,
            itemCount,
            telemetry.CommitCount,
            elapsed,
            itemCount / seconds,
            elapsed.TotalMilliseconds * 1000d / itemCount,
            batches / seconds,
            telemetry.CommitCount == 0 ? 0 : (double)telemetry.WriteCallCount / telemetry.CommitCount,
            batches == 0 ? 0 : (double)telemetry.BytesWritten / batches,
            itemCount == 0 ? 0 : (double)telemetry.BytesWritten / itemCount,
            CalculateMiBs(telemetry.BytesWritten, seconds),
            telemetry.SetLengthCallCount,
            telemetry.Checksum);
    }


    /// <summary>
    /// Selects the median routed bulk-write sample by item throughput.<br/>
    /// The middle value is used instead of the average so one noisy run does not dominate the report row.<br/>
    /// </summary>
    /// <param name="samples">The collected routed bulk-write samples.</param>
    /// <returns>The median routed bulk-write sample.</returns>
    private static Scalar8Scalar8RoutedBulkWriteResult SelectMedianRoutedBulkWriteSample(ReadOnlySpan<Scalar8Scalar8RoutedBulkWriteResult> samples)
    {
        Scalar8Scalar8RoutedBulkWriteResult[] sorted = samples.ToArray();
        Array.Sort(sorted, static (left, right) => left.ItemsPerSecond.CompareTo(right.ItemsPerSecond));
        return sorted[sorted.Length / 2];
    }


    /// <summary>
    /// Selects the median raw write-parity sample by item throughput.<br/>
    /// Raw `DataKernel` rows can be very fast and noisy, so median selection keeps the all-shape parity report aligned with routed and SQLite repeated samples.<br/>
    /// </summary>
    /// <param name="samples">The collected raw write-parity samples.</param>
    /// <returns>The median raw write-parity sample.</returns>
    private static Scalar8Scalar8WriteParityResult SelectMedianWriteParitySample(ReadOnlySpan<Scalar8Scalar8WriteParityResult> samples)
    {
        Scalar8Scalar8WriteParityResult[] sorted = samples.ToArray();
        Array.Sort(sorted, static (left, right) => left.ItemsPerSecond.CompareTo(right.ItemsPerSecond));
        return sorted[sorted.Length / 2];
    }


    /// <summary>
    /// Selects the median routed write-attribution sample by item throughput.<br/>
    /// The attribution row is sorted the same way as routed bulk-write rows so timing comparisons stay aligned.<br/>
    /// </summary>
    /// <param name="samples">The collected write-attribution samples.</param>
    /// <returns>The median write-attribution sample.</returns>
    private static Scalar8Scalar8WriteAttributionResult SelectMedianScalar8Scalar8WriteAttributionSample(ReadOnlySpan<Scalar8Scalar8WriteAttributionResult> samples)
    {
        Scalar8Scalar8WriteAttributionResult[] sorted = samples.ToArray();
        Array.Sort(sorted, static (left, right) => left.ItemsPerSecond.CompareTo(right.ItemsPerSecond));
        return sorted[sorted.Length / 2];
    }


    /// <summary>
    /// Selects the median public bulk-read sample by nanoseconds per identity.<br/>
    /// Lower latency is better, but the middle value is selected to represent typical current behavior.<br/>
    /// </summary>
    /// <param name="samples">The collected public bulk-read samples.</param>
    /// <returns>The median public bulk-read sample.</returns>
    private static Scalar8Scalar8PublicBulkReadResult SelectMedianPublicBulkReadSample(ReadOnlySpan<Scalar8Scalar8PublicBulkReadResult> samples)
    {
        Scalar8Scalar8PublicBulkReadResult[] sorted = samples.ToArray();
        Array.Sort(sorted, static (left, right) => left.NsPerIdentity.CompareTo(right.NsPerIdentity));
        return sorted[sorted.Length / 2];
    }


    private static Fixed32Scalar8ReadParityResult SelectMedianFixed32Scalar8ReadParitySample(ReadOnlySpan<Fixed32Scalar8ReadParityResult> samples)
    {
        Fixed32Scalar8ReadParityResult[] sorted = samples.ToArray();
        Array.Sort(sorted, static (left, right) => left.NsPerIdentity.CompareTo(right.NsPerIdentity));
        return sorted[sorted.Length / 2];
    }


    private static Fixed32Scalar16ReadParityResult SelectMedianFixed32Scalar16ReadParitySample(ReadOnlySpan<Fixed32Scalar16ReadParityResult> samples)
    {
        Fixed32Scalar16ReadParityResult[] sorted = samples.ToArray();
        Array.Sort(sorted, static (left, right) => left.NsPerIdentity.CompareTo(right.NsPerIdentity));
        return sorted[sorted.Length / 2];
    }


    /// <summary>
    /// Selects the median public growth sample by final file bytes per item.<br/>
    /// Growth rows are normally deterministic; this still keeps repeat reporting consistent with timed benchmark commands.<br/>
    /// </summary>
    /// <param name="samples">The collected public growth samples.</param>
    /// <returns>The median public growth sample.</returns>
    private static Scalar8Scalar8PublicGrowthResult SelectMedianPublicGrowthSample(ReadOnlySpan<Scalar8Scalar8PublicGrowthResult> samples)
    {
        Scalar8Scalar8PublicGrowthResult[] sorted = samples.ToArray();
        Array.Sort(sorted, static (left, right) =>
        {
            int fileCompare = left.FileBytesPerItem.CompareTo(right.FileBytesPerItem);
            return fileCompare != 0 ? fileCompare : left.WrittenBytesPerItem.CompareTo(right.WrittenBytesPerItem);
        });
        return sorted[sorted.Length / 2];
    }


    /// <summary>
    /// Selects the median SQLite write sample by item throughput.<br/>
    /// The middle value is used so a single noisy transaction run does not dominate the competitor comparison row.<br/>
    /// </summary>
    /// <param name="samples">The collected SQLite write samples.</param>
    /// <returns>The median SQLite write sample.</returns>
    private static SqliteScalar8Scalar8WriteResult SelectMedianSqliteScalar8Scalar8WriteSample(ReadOnlySpan<SqliteScalar8Scalar8WriteResult> samples)
    {
        SqliteScalar8Scalar8WriteResult[] sorted = samples.ToArray();
        Array.Sort(sorted, static (left, right) => left.ItemsPerSecond.CompareTo(right.ItemsPerSecond));
        return sorted[sorted.Length / 2];
    }


    /// <summary>
    /// Selects the median SQLite read sample by nanoseconds per identity.<br/>
    /// Lower latency is better, but the middle sample is selected so the report describes typical current behavior rather than a best-case cache moment.<br/>
    /// </summary>
    /// <param name="samples">The collected SQLite read samples.</param>
    /// <returns>The median SQLite read sample.</returns>
    private static SqliteScalar8Scalar8ReadResult SelectMedianSqliteScalar8Scalar8ReadSample(ReadOnlySpan<SqliteScalar8Scalar8ReadResult> samples)
    {
        SqliteScalar8Scalar8ReadResult[] sorted = samples.ToArray();
        Array.Sort(sorted, static (left, right) => left.NsPerIdentity.CompareTo(right.NsPerIdentity));
        return sorted[sorted.Length / 2];
    }


    /// <summary>
    /// Prints the markdown header used for single-shelf `SS8-8` performance rows.<br/>
    /// The method is harness-only formatting and intentionally stays outside measured hot paths.<br/>
    /// </summary>
    private static void PrintScalar8Scalar8PerfHeader()
    {
        Console.WriteLine("| shelf bytes | max items | measured items | fill % | unused | sorted insert ns | reverse insert ns | random insert ns | lower-bound ns | range ns | range ns/id | scoop ns | scoop ns/id | checksum |");
        Console.WriteLine("| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
    }


    /// <summary>
    /// Prints one single-shelf `SS8-8` performance row.<br/>
    /// Values are already aggregated, so this method performs only markdown formatting.<br/>
    /// </summary>
    /// <param name="result">The result row to print.</param>
    private static void PrintScalar8Scalar8Perf(Scalar8Scalar8PerfResult result)
    {
        Console.WriteLine($"| {result.ShelfSize} | {result.MaxItems} | {result.MeasuredItems} | {result.FillPercent:N2}% | {result.UnusedTailBytes} | {FormatStats(result.SortedInsertNs)} | {FormatStats(result.ReverseInsertNs)} | {FormatStats(result.RandomInsertNs)} | {FormatStats(result.LowerBoundNs)} | {FormatStats(result.RangeNs)} | {FormatStats(result.RangeNsPerIdentity)} | {FormatStats(result.ScoopNs)} | {FormatStats(result.ScoopNsPerIdentity)} | {result.Checksum} |");
    }


    /// <summary>
    /// Prints the markdown header used for multi-shelf `SS8-8` performance rows.<br/>
    /// Multi-shelf rows focus on distributed arena behavior rather than every single-shelf metric.<br/>
    /// </summary>
    private static void PrintScalar8Scalar8MultiShelfPerfHeader()
    {
        Console.WriteLine("| shelf bytes | max items | measured items/shelf | shelf count | arena bytes | random insert ns | lower-bound ns | range ns/id | scoop ns/id | checksum |");
        Console.WriteLine("| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
    }


    /// <summary>
    /// Prints one multi-shelf `SS8-8` performance row.<br/>
    /// Values are already aggregated, so this method performs only markdown formatting.<br/>
    /// </summary>
    /// <param name="result">The multi-shelf result row to print.</param>
    private static void PrintScalar8Scalar8MultiShelfPerf(Scalar8Scalar8MultiShelfPerfResult result)
    {
        Console.WriteLine($"| {result.ShelfSize} | {result.MaxItems} | {result.MeasuredItems} | {result.ShelfCount} | {result.ArenaBytes} | {FormatStats(result.RandomInsertNs)} | {FormatStats(result.LowerBoundNs)} | {FormatStats(result.RangeNsPerIdentity)} | {FormatStats(result.ScoopNsPerIdentity)} | {result.Checksum} |");
    }


    /// <summary>
    /// Prints the markdown header for cached shelf-view size-sweep rows.<br/>
    /// The columns separate seek cost from range scan cost so breakpoint slope is easier to see.<br/>
    /// </summary>
    private static void PrintScalar8Scalar8ReadSweepHeader()
    {
        Console.WriteLine("| shelf bytes | max items | measured items | range length | seek ns | seek+one ns | seek+range ns/id | known-range ns/id | multi-seek ns | multi-seek+range ns/id | checksum |");
        Console.WriteLine("| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
    }


    /// <summary>
    /// Prints one cached shelf-view size-sweep row.<br/>
    /// Formatting happens after measurement so it cannot affect benchmark timing.<br/>
    /// </summary>
    /// <param name="result">The row to print.</param>
    private static void PrintScalar8Scalar8ReadSweep(Scalar8Scalar8ReadSweepResult result)
    {
        Console.WriteLine($"| {result.ShelfSize} | {result.MaxItems} | {result.MeasuredItems} | {result.RangeLength} | {FormatStats(result.SeekOnlyNs)} | {FormatStats(result.SeekOneNs)} | {FormatStats(result.SeekRangeNsPerIdentity)} | {FormatStats(result.KnownRangeNsPerIdentity)} | {FormatStats(result.MultiShelfSeekOnlyNs)} | {FormatStats(result.MultiShelfSeekRangeNsPerIdentity)} | {result.Checksum} |");
    }


    /// <summary>
    /// Prints the markdown header for DataKernel file-read size-sweep rows.<br/>
    /// These rows show read throughput, per-extent cost, and syscall counts for shelf-sized extents.<br/>
    /// </summary>
    private static void PrintScalar8Scalar8DataKernelReadSweepHeader()
    {
        Console.WriteLine("| shelf bytes | max items | shelf count | bytes | create writes | setLength | seq reads/run | random reads/run | seq MiB/s | seq ns/read | random MiB/s | random ns/read | checksum |");
        Console.WriteLine("| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
    }


    /// <summary>
    /// Prints one DataKernel file-read size-sweep row.<br/>
    /// Formatting happens after measurement so it cannot affect benchmark timing.<br/>
    /// </summary>
    /// <param name="result">The row to print.</param>
    private static void PrintScalar8Scalar8DataKernelReadSweep(Scalar8Scalar8DataKernelReadSweepResult result)
    {
        Console.WriteLine($"| {result.ShelfSize} | {result.MaxItems} | {result.ShelfCount} | {result.MeasuredBytes} | {result.CreateWriteCalls} | {result.CreateSetLengthCalls} | {result.SequentialReadCallsPerRun} | {result.RandomReadCallsPerRun} | {FormatStats(result.SequentialMiBs)} | {FormatStats(result.SequentialNsPerRead)} | {FormatStats(result.RandomMiBs)} | {FormatStats(result.RandomNsPerRead)} | {result.Checksum} |");
    }


    private static byte[] CreateFixedNIdentity16(int value)
    {
        byte[] identity = new byte[FixedNScalar16Layout.IdentitySize];
        BinaryPrimitives.WriteUInt64BigEndian(identity.AsSpan(8, 8), (ulong)value);
        return identity;
    }


    private readonly record struct Scalar8Scalar8InsertPerfResult(
        double NsPerInsert,
        long Checksum);


    private readonly record struct Scalar8Scalar8LookupPerfResult(
        double NsPerLookup,
        long Checksum);


    private readonly record struct Scalar8Scalar8RangePerfResult(
        double NsPerRange,
        double NsPerIdentity,
        long Checksum);


    private readonly record struct Scalar8Scalar8MetricStats(
        double Mean,
        double Median,
        double Min,
        double Max,
        double StandardDeviation);


    private readonly record struct Scalar8Scalar8PerfVectors(
        int[] SortedInsertOrder,
        int[] ReverseInsertOrder,
        int[] RandomInsertOrder,
        int[] LookupKeys,
        int[] RangeStarts,
        int RangeLength);


    private readonly record struct Scalar8Scalar8MultiShelfPerfResult(
        int ShelfSize,
        int MaxItems,
        int MeasuredItems,
        int ShelfCount,
        long ArenaBytes,
        Scalar8Scalar8MetricStats RandomInsertNs,
        Scalar8Scalar8MetricStats LowerBoundNs,
        Scalar8Scalar8MetricStats RangeNsPerIdentity,
        Scalar8Scalar8MetricStats ScoopNsPerIdentity,
        long Checksum);


    private readonly record struct Scalar8Scalar8ReadSweepResult(
        int ShelfSize,
        int MaxItems,
        int MeasuredItems,
        int RangeLength,
        Scalar8Scalar8MetricStats SeekOnlyNs,
        Scalar8Scalar8MetricStats SeekOneNs,
        Scalar8Scalar8MetricStats SeekRangeNsPerIdentity,
        Scalar8Scalar8MetricStats KnownRangeNsPerIdentity,
        Scalar8Scalar8MetricStats MultiShelfSeekOnlyNs,
        Scalar8Scalar8MetricStats MultiShelfSeekRangeNsPerIdentity,
        long Checksum);


    private readonly record struct Scalar8Scalar8DataKernelReadSweepResult(
        int ShelfSize,
        int MaxItems,
        int ShelfCount,
        long MeasuredBytes,
        long CreateWriteCalls,
        long CreateBytesWritten,
        long CreateSetLengthCalls,
        long SequentialReadCallsPerRun,
        long RandomReadCallsPerRun,
        Scalar8Scalar8MetricStats SequentialMiBs,
        Scalar8Scalar8MetricStats SequentialNsPerRead,
        Scalar8Scalar8MetricStats RandomMiBs,
        Scalar8Scalar8MetricStats RandomNsPerRead,
        long Checksum);


    private readonly record struct Scalar8Scalar8DesignPerfSample(
        long ElapsedTicks,
        long ReadCallCount,
        long BytesRead,
        long WriteCallCount,
        long BytesWritten,
        long SetLengthCallCount);


    private readonly record struct Scalar8Scalar8DesignPerfResult(
        string Name,
        int Runs,
        double AverageMilliseconds,
        double MedianMilliseconds,
        double MinMilliseconds,
        double MaxMilliseconds,
        double StandardDeviationMilliseconds,
        double AverageReadCalls,
        double AverageBytesRead,
        double AverageWriteCalls,
        double AverageBytesWritten,
        double AverageSetLengthCalls);


    private readonly record struct Scalar8Scalar8GrowthSlackResult(
        string Name,
        long SetupLength,
        long FinalLength,
        long GrowthBytes,
        long LiveStructureBytes,
        long ExtentSlackBytes,
        int RouterCount,
        int ShelfCount,
        int ItemCount,
        int ShelfCapacity,
        double ShelfOccupancyPercent,
        int FreeItemSlots,
        long ShelfFreeCapacityBytes,
        long RouterReservedSlackBytes,
        long WriteCalls,
        long BytesWritten,
        long SetLengthCalls);


    private readonly record struct Scalar8Scalar8PerfResult(
        int ShelfSize,
        int MaxItems,
        int MeasuredItems,
        int UnusedTailBytes,
        Scalar8Scalar8MetricStats SortedInsertNs,
        Scalar8Scalar8MetricStats ReverseInsertNs,
        Scalar8Scalar8MetricStats RandomInsertNs,
        Scalar8Scalar8MetricStats LowerBoundNs,
        Scalar8Scalar8MetricStats RangeNs,
        Scalar8Scalar8MetricStats RangeNsPerIdentity,
        Scalar8Scalar8MetricStats ScoopNs,
        Scalar8Scalar8MetricStats ScoopNsPerIdentity,
        long Checksum)
    {
        public double FillPercent => MaxItems == 0 ? 0 : (double)MeasuredItems * 100d / MaxItems;
    }


    private readonly record struct Scalar8Scalar8BatchLookupPerfResult(
        string Name,
        int Iterations,
        int BatchCount,
        TimeSpan Elapsed,
        long ReadCallCount,
        long BackingReadCallCount,
        long BytesRead,
        long Checksum);


    private readonly record struct Scalar8Scalar8RangeScoopPerfResult(
        string Name,
        int Iterations,
        int RangeLength,
        long IdentityCount,
        TimeSpan Elapsed,
        long ReadCallCount,
        long BackingReadCallCount,
        long BytesRead,
        long Checksum);


    private struct Scalar8Scalar8WriteAttributionTelemetry
    {
        public long InsertElapsedTicks;
        public long CommitWallElapsedTicks;
        public long CommitElapsedTicks;
        public long BatchDirtyShelfFlushTicks;
        public long BatchPublicationAttributionTicks;
        public long BatchDeltaSelectionTicks;
        public long BatchDeltaStagingTicks;
        public long BatchFullShelfStagingTicks;
        public long BatchSplitFallbackFullShelfStagingTicks;
        public long BatchDataKernelCommitTicks;
        public long RootLookupTicks;
        public long RouteWalkTicks;
        public long RouteWalkDirectViewLookupTicks;
        public long RouteWalkDirectPrefixTicks;
        public long RouteWalkDirectSlotLoadTicks;
        public long RouteWalkDirectTargetClassificationTicks;
        public long RouteWalkDirectRouterTargetCount;
        public long RouteWalkDirectShelfTargetCount;
        public long RouteWalkNonDirectRouterReadTicks;
        public long RouteWalkNonDirectPrefixTicks;
        public long RouteWalkNonDirectRouteLookupTicks;
        public long RouteWalkNonDirectTargetClassificationTicks;
        public long RouteWalkNonDirectRouterTargetCount;
        public long RouteWalkNonDirectShelfTargetCount;
        public long RouteTargetClassificationTicks;
        public long RouteCacheProbeTicks;
        public long ShelfCacheLookupTicks;
        public long ShelfCacheProbeTicks;
        public long ShelfCacheHitCount;
        public long ShelfCacheMissCount;
        public long ShelfCacheReadTicks;
        public long ShelfCacheSnapshotCloneTicks;
        public long ShelfCacheEntryCreateTicks;
        public long ShelfCacheEntryAddTicks;
        public long ShelfInsertTicks;
        public long ShelfAppendFastPathTicks;
        public long ShelfAppendFastPathCount;
        public long ShelfLowerBoundTicks;
        public long ShelfDuplicateCheckTicks;
        public long ShelfUniqueCheckTicks;
        public long ShelfPayloadWriteTicks;
        public long ShelfSlotMoveTicks;
        public long ShelfHeaderWriteTicks;
        public long CachedAttemptCount;
        public long CachedHandledCount;
        public long CacheMissEmptyRouteCount;
        public long CacheMissNonShelfTargetCount;
        public long CacheMissFullShelfCount;
        public long RouteCacheHitCount;
        public long RouteCacheMissCount;
        public long InsertedCount;
        public long InitialRouteCreateCount;
        public long NoSplitCount;
        public long NormalSplitCount;
        public long TransformSplitCount;
        public long NoOpCount;
        public long KeyConflictCount;
        public long PlannedShelfRewriteCount;
        public long PlannedShelfAppendCount;
        public long PlannedRouterRewriteCount;
        public long PlannedStagedBytes;
        public long SameShelfRewriteRevisitCount;
        public long DistinctShelfRewriteOffsets;
        public long MaxShelfRewriteCountInBatch;
        public long CommitCount;
        public long DeferredCommitRequests;
        public long StagedSegmentCount;
        public long StagedExtentCount;
        public long WriteCallCount;
        public long BackingWriteCallCount;
        public long SetLengthCallCount;
        public long FlushCallCount;
        public long BytesWritten;
        public long Checksum;
        public long PublicationDirtyShelfCount;
        public long PublicationFullShelfBytes;
        public long PublicationRawChangedBytes;
        public long PublicationRawChangedRangeCount;
        public long PublicationDeltaCandidateBytes;
        public long PublicationDeltaCandidateRangeCount;
        public long PublicationHeaderDeltaBytes;
        public long PublicationSlotDeltaBytes;
        public long PublicationItemDeltaBytes;
        public long PublicationMaxDeltaCandidateBytesPerShelf;
        public long PublicationFullShelfBetterOrEqualCount;
        public long DeltaShelfCount;
        public long DeltaUngroupedSpanCount;
        public long DeltaUngroupedBytes;
        public long DeltaGrouped512SpanCount;
        public long DeltaGrouped512Bytes;
        public long DeltaGrouped1024SpanCount;
        public long DeltaGrouped1024Bytes;
        public long DeltaGrouped4096SpanCount;
        public long DeltaGrouped4096Bytes;
        public long DeltaGrouped8192SpanCount;
        public long DeltaGrouped8192Bytes;
        public long DeltaPositiveGapCount;
        public long DeltaPositiveGapBytes;
        public long DeltaMaxPositiveGapBytes;
        public long CoalescedAdjacentSegmentCount;
        public long CoalescedGapCount;
        public long CoalescedGapBytes;
        public long MaxCoalescedGapBytes;
        public long RejectedGapCount;
        public long RejectedGapBytes;
        public long MaxRejectedGapBytes;
        public long OverlapBreakCount;
        public long FileCommitSliceBuildTicks;
        public long FileCommitCoveredRangeMergeTicks;
        public long FileCommitGroupShapeTicks;
        public long FileCommitBufferBuildTicks;
        public long FileCommitGapReadTicks;
        public long FileCommitBackingWriteTicks;
    }


    private struct Scalar8Scalar8PublicReadTargetTelemetry
    {
        public int DuplicateTargetSkipCount;
        public int RouterTargetCount;
        public int MaxRouterDepth;
    }


    private readonly record struct SqliteScalar8Scalar8Options(
        string JournalMode,
        string Synchronous);


    private readonly record struct SqliteScalar8Scalar8WriteResult(
        string Name,
        int Batches,
        int WarmupBatches,
        int ItemsPerBatch,
        long MeasuredItemCount,
        long TotalItemCount,
        TimeSpan Elapsed,
        double ItemsPerSecond,
        double MicrosecondsPerItem,
        double BatchesPerSecond,
        long StorageBytes,
        double StorageBytesPerItem,
        long Checksum,
        int SampleCount);


    private readonly record struct SqliteScalar8Scalar8ReadResult(
        string Name,
        int Iterations,
        int RangeIdentityCount,
        long TotalIdentityCount,
        TimeSpan Elapsed,
        double IdentitiesPerSecond,
        double NsPerIdentity,
        double LogicalMiBs,
        long Checksum,
        int SampleCount);


    private readonly record struct AllShapeWriteParityRow(
        string Shape,
        string OrderName,
        int PayloadBytes,
        Scalar8Scalar8RoutedBulkWriteResult LibraDex,
        SqliteScalar8Scalar8WriteResult Sqlite,
        Scalar8Scalar8WriteParityResult RawDataKernel);


    private readonly record struct Scalar8Scalar8WriteParityResult(
        string Name,
        int Batches,
        int ItemsPerBatch,
        long ItemCount,
        long CommitCount,
        TimeSpan Elapsed,
        double ItemsPerSecond,
        double MicrosecondsPerItem,
        double BatchesPerSecond,
        double WritesPerBatch,
        double BytesPerBatch,
        double BytesPerItem,
        double MiBs,
        long SetLengthCallCount,
        long Checksum);


    private readonly record struct Scalar8Scalar8RoutedBulkWriteResult(
        string Name,
        int Batches,
        int ItemsPerBatch,
        long ItemCount,
        long CommitCount,
        TimeSpan Elapsed,
        double ItemsPerSecond,
        double MicrosecondsPerItem,
        double BatchesPerSecond,
        double DeferredCommitRequestsPerBatch,
        double WritesPerBatch,
        double BytesPerBatch,
        double BytesPerItem,
        double MiBs,
        long SetLengthCallCount,
        long Checksum,
        double RootLookupNsPerItem,
        long RouteTargetCacheHitCount,
        long RouteTargetCacheMissCount,
        long RouteWalkCount,
        double RouteWalkNsPerItem,
        double ShelfInsertNsPerItem,
        long ShelfCacheHitCount,
        long ShelfCacheMissCount,
        long CachedInsertCount,
        long WalkedFallbackCount,
        long CacheFullFallbackCount,
        long NoSplitFallbackCount,
        long TransformSplitFallbackCount,
        long ParentRouteSplitFallbackCount,
        long RootRouteCreateCount,
        long FullShelfStageCount,
        long DeltaShelfStageCount,
        long DeltaChangedSpanCount,
        long DeltaChangedBytes,
        int SampleCount);


    private readonly record struct Scalar8Scalar8RoutedCadenceDefinition(
        string Name,
        int Batches,
        int WarmupBatches,
        int ItemsPerBatch);


    private readonly record struct Scalar8Scalar8RoutedCadenceResult(
        string CadenceName,
        string OrderName,
        int WarmupBatches,
        Scalar8Scalar8RoutedBulkWriteResult Result);


    private readonly record struct Scalar8Scalar8WriteAttributionResult(
        string Name,
        int Batches,
        int ItemsPerBatch,
        long ItemCount,
        TimeSpan Elapsed,
        double ItemsPerSecond,
        double MicrosecondsPerItem,
        double InsertNsPerItem,
        double CommitWallNsPerItem,
        double DataKernelCommitNsPerItem,
        double BatchDirtyShelfFlushNsPerItem,
        double BatchPublicationAttributionNsPerItem,
        double BatchDeltaSelectionNsPerItem,
        double BatchDeltaStagingNsPerItem,
        double BatchFullShelfStagingNsPerItem,
        double BatchSplitFallbackFullShelfStagingNsPerItem,
        double BatchDataKernelCommitNsPerItem,
        double RootLookupNsPerItem,
        double RouteWalkNsPerItem,
        double RouteWalkDirectViewLookupNsPerItem,
        double RouteWalkDirectPrefixNsPerItem,
        double RouteWalkDirectSlotLoadNsPerItem,
        double RouteWalkDirectTargetClassificationNsPerItem,
        long RouteWalkDirectRouterTargetCount,
        long RouteWalkDirectShelfTargetCount,
        double RouteWalkNonDirectRouterReadNsPerItem,
        double RouteWalkNonDirectPrefixNsPerItem,
        double RouteWalkNonDirectRouteLookupNsPerItem,
        double RouteWalkNonDirectTargetClassificationNsPerItem,
        long RouteWalkNonDirectRouterTargetCount,
        long RouteWalkNonDirectShelfTargetCount,
        double RouteTargetClassificationNsPerItem,
        double RouteCacheProbeNsPerItem,
        double ShelfCacheLookupNsPerItem,
        double ShelfCacheProbeNsPerItem,
        long ShelfCacheHitCount,
        long ShelfCacheMissCount,
        double ShelfCacheReadNsPerItem,
        double ShelfCacheSnapshotCloneNsPerItem,
        double ShelfCacheEntryCreateNsPerItem,
        double ShelfCacheEntryAddNsPerItem,
        double ShelfInsertNsPerItem,
        double ShelfAppendFastPathNsPerItem,
        long ShelfAppendFastPathCount,
        double ShelfLowerBoundNsPerItem,
        double ShelfDuplicateCheckNsPerItem,
        double ShelfUniqueCheckNsPerItem,
        double ShelfPayloadWriteNsPerItem,
        double ShelfSlotMoveNsPerItem,
        double ShelfHeaderWriteNsPerItem,
        long CachedAttemptCount,
        long CachedHandledCount,
        long CacheMissEmptyRouteCount,
        long CacheMissNonShelfTargetCount,
        long CacheMissFullShelfCount,
        long RouteCacheHitCount,
        long RouteCacheMissCount,
        long InsertedCount,
        long InitialRouteCreateCount,
        long NoSplitCount,
        long NormalSplitCount,
        long TransformSplitCount,
        long NoOpCount,
        long KeyConflictCount,
        long PlannedShelfRewriteCount,
        long PlannedShelfAppendCount,
        long PlannedRouterRewriteCount,
        long PlannedStagedBytes,
        double PlannedBytesPerItem,
        long SameShelfRewriteRevisitCount,
        long DistinctShelfRewriteOffsets,
        long MaxShelfRewriteCountInBatch,
        long CommitCount,
        long DeferredCommitRequests,
        double StagedSegmentsPerBatch,
        double WritesPerBatch,
        double WrittenBytesPerBatch,
        double WrittenBytesPerItem,
        long SetLengthCallCount,
        long Checksum,
        long PublicationDirtyShelfCount,
        long PublicationFullShelfBytes,
        long PublicationRawChangedBytes,
        long PublicationRawChangedRangeCount,
        long PublicationDeltaCandidateBytes,
        long PublicationDeltaCandidateRangeCount,
        long PublicationHeaderDeltaBytes,
        long PublicationSlotDeltaBytes,
        long PublicationItemDeltaBytes,
        long PublicationMaxDeltaCandidateBytesPerShelf,
        long PublicationFullShelfBetterOrEqualCount,
        long DeltaShelfCount,
        long DeltaUngroupedSpanCount,
        long DeltaUngroupedBytes,
        long DeltaGrouped512SpanCount,
        long DeltaGrouped512Bytes,
        long DeltaGrouped1024SpanCount,
        long DeltaGrouped1024Bytes,
        long DeltaGrouped4096SpanCount,
        long DeltaGrouped4096Bytes,
        long DeltaGrouped8192SpanCount,
        long DeltaGrouped8192Bytes,
        long DeltaPositiveGapCount,
        long DeltaPositiveGapBytes,
        long DeltaMaxPositiveGapBytes,
        double CoalescedAdjacentSegmentsPerBatch,
        double CoalescedGapsPerBatch,
        double CoalescedGapBytesPerBatch,
        long MaxCoalescedGapBytes,
        double RejectedGapsPerBatch,
        double RejectedGapBytesPerBatch,
        long MaxRejectedGapBytes,
        double OverlapBreaksPerBatch,
        double FileCommitSliceBuildNsPerItem,
        double FileCommitCoveredRangeMergeNsPerItem,
        double FileCommitGroupShapeNsPerItem,
        double FileCommitBufferBuildNsPerItem,
        double FileCommitGapReadNsPerItem,
        double FileCommitBackingWriteNsPerItem,
        int SampleCount)
    {
        public double DeferredCommitRequestsPerBatch => CommitCount == 0 ? 0 : DeferredCommitRequests / (double)CommitCount;
    }


    private readonly record struct Scalar8Scalar8PublicBulkReadResult(
        string Name,
        int Iterations,
        int RangeIdentityCount,
        long TotalIdentityCount,
        TimeSpan Elapsed,
        double IdentitiesPerSecond,
        double NsPerIdentity,
        double ReadsPerRange,
        double BytesPerRange,
        long Checksum,
        int SampleCount);


    private readonly record struct Fixed32Scalar8ReadParityResult(
        string Name,
        int Iterations,
        int RangeIdentityCount,
        long TotalIdentityCount,
        TimeSpan Elapsed,
        double IdentitiesPerSecond,
        double NsPerIdentity,
        double LogicalMiBs,
        double ReadsPerRange,
        double BytesPerRange,
        long Checksum,
        int SampleCount);


    private readonly record struct Fixed32Scalar16ReadParityResult(
        string Name,
        int Iterations,
        int RangeIdentityCount,
        long TotalIdentityCount,
        TimeSpan Elapsed,
        double IdentitiesPerSecond,
        double NsPerIdentity,
        double LogicalMiBs,
        double ReadsPerRange,
        double BytesPerRange,
        long Checksum,
        int SampleCount);


    private readonly record struct Fixed32Scalar8SizeSweepRow(
        Fixed32Scalar8Profile Profile,
        Scalar8Scalar8RoutedBulkWriteResult SortedWrite,
        Scalar8Scalar8RoutedBulkWriteResult RandomWrite,
        Fixed32Scalar8ReadParityResult Prefix0Read,
        Fixed32Scalar8ReadParityResult PrefixSpanRead);


    private readonly record struct Fixed32Scalar16SizeSweepRow(
        Fixed32Scalar16Profile Profile,
        Scalar8Scalar8RoutedBulkWriteResult SortedWrite,
        Scalar8Scalar8RoutedBulkWriteResult RandomWrite,
        Fixed32Scalar16ReadParityResult Prefix0Read,
        Fixed32Scalar16ReadParityResult PrefixSpanRead);


    private readonly record struct Scalar8Scalar8PublicReadTargetResult(
        string Name,
        int DistinctShelfCount,
        int ContiguousRunCount,
        int MaxContiguousRunShelves,
        double AverageContiguousRunShelves,
        int DuplicateTargetSkipCount,
        int RouterTargetCount,
        int MaxRouterDepth,
        string FirstOffsets);


    private readonly record struct Scalar8Scalar8PublicGrowthResult(
        long FinalLength,
        long LiveStructureBytes,
        long MetadataBytes,
        long RouterBytes,
        long ShelfBytes,
        long OverheadBytes,
        int RouterCount,
        int ShelfCount,
        long ItemCount,
        long ShelfCapacity,
        double ShelfOccupancyPercent,
        long FreeItemSlots,
        long ShelfFreeCapacityBytes,
        double FileBytesPerItem,
        double LiveBytesPerItem,
        double OverheadBytesPerItem,
        double WrittenBytesPerItem,
        double WritesPerBatch,
        long SetLengthCallCount,
        long Checksum,
        int SampleCount);


    private enum Scalar8Scalar8InsertOrder
    {
        Sorted,
        Reverse,
        Random
    }


    private enum Scalar8Scalar8BulkWriteLocalityMode
    {
        BatchMajor,
        PrefixMajor
    }



    private static RoutedBulkWriteLoopTelemetry RunFixed32Scalar16RoutedBulkWriteLoop(
        LibraDexFileSession session,
        long rootRouterOffset,
        int batches,
        int itemsPerBatch,
        int prefixCount,
        int[] order,
        int batchOffset,
        Fixed32Scalar16Profile profile,
        bool includeAttribution)
    {
        RoutedBulkWriteLoopTelemetry telemetry = default;
        for (int batch = 0; batch < batches; batch++)
        {
            Dictionary<long, Fixed32Scalar16BatchShelfCacheEntry> dirtyShelves = [];
            Dictionary<Fixed32Scalar16BatchRouteCacheKey, Fixed32Scalar16RouteTarget> routeTargetCache = [];
            using LibraDexFileSessionDurabilityBatch durabilityBatch = session.BeginDurabilityBatch();
            int logicalBatch = batchOffset + batch;
            for (int i = 0; i < itemsPerBatch; i++)
            {
                CreateFixed32Scalar16BulkWriteKey(logicalBatch, order[i], itemsPerBatch, prefixCount, out ulong key0, out ulong key1, out ulong key2, out ulong key3);
                ulong identity = Fixed32Scalar16Layout.EncodeUnsignedScalar8(key0);
                byte rootPrefix = (byte)(key0 >> 56);
                Fixed32Scalar16RouteTarget target;
                if (TryGetCachedFixed32Scalar16RouteTarget(routeTargetCache, key0, key1, key2, key3, out target))
                {
                    telemetry.RouteTargetCacheHitCount++;
                }
                else
                {
                    telemetry.RouteTargetCacheMissCount++;
                    long rootLookupStart = includeAttribution ? Stopwatch.GetTimestamp() : 0;
                    if (session.FindRouterTarget(rootRouterOffset, rootPrefix) == 0)
                    {
                        telemetry.RootRouteCreateCount++;
                        _ = session.CreateFixed32Scalar16ShelfAndLinkRootRoute(rootRouterOffset, rootPrefix, profile, itemCount: 0);
                    }

                    if (includeAttribution)
                    {
                        telemetry.RootLookupTicks += Stopwatch.GetTimestamp() - rootLookupStart;
                    }

                    long routeWalkStart = includeAttribution ? Stopwatch.GetTimestamp() : 0;
                    Fixed32Scalar16RoutePathTarget pathTarget = session.WalkFixed32Scalar16RoutePathTarget(rootRouterOffset, key0, key1, key2, key3, maxRouterHops: 32);
                    if (includeAttribution)
                    {
                        telemetry.RouteWalkTicks += Stopwatch.GetTimestamp() - routeWalkStart;
                    }

                    telemetry.RouteWalkCount++;
                    target = pathTarget.Target;
                    if (target.Kind == Fixed32Scalar16RouteTargetKind.Shelf)
                    {
                        routeTargetCache[Fixed32Scalar16BatchRouteCacheKey.Create(key0, key1, key2, key3, target.RouterDepth)] = target;
                    }
                }

                bool insertedByCache = false;
                if (target.Kind == Fixed32Scalar16RouteTargetKind.Shelf)
                {
                    if (!dirtyShelves.TryGetValue(target.Offset, out Fixed32Scalar16BatchShelfCacheEntry? entry))
                    {
                        telemetry.ShelfCacheMissCount++;
                        byte[] shelfBytes = session.ReadFixed32Scalar16ShelfBytesForBatch(target.Offset, profile);
                        entry = new Fixed32Scalar16BatchShelfCacheEntry(shelfBytes);
                        dirtyShelves[target.Offset] = entry;
                    }
                    else
                    {
                        telemetry.ShelfCacheHitCount++;
                    }

                    long shelfInsertStart = includeAttribution ? Stopwatch.GetTimestamp() : 0;
                    Fixed32Scalar16 shelf = new(entry.Bytes, profile);
                    Fixed32Scalar16InsertResult cachedResult = shelf.InsertWithMutationBounds(key0, key1, key2, key3, identity, allowDuplicateKeys: true, out Fixed32Scalar16MutationBounds mutationBounds);
                    if (includeAttribution)
                    {
                        telemetry.ShelfInsertTicks += Stopwatch.GetTimestamp() - shelfInsertStart;
                    }

                    if (cachedResult == Fixed32Scalar16InsertResult.Inserted)
                    {
                        entry.Dirty = true;
                        entry.Include(mutationBounds);
                        insertedByCache = true;
                        telemetry.CachedInsertCount++;
                    }
                    else if (cachedResult == Fixed32Scalar16InsertResult.Full)
                    {
                        telemetry.CacheFullFallbackCount++;
                        dirtyShelves.Remove(target.Offset);
                        routeTargetCache.Clear();
                        Fixed32Scalar16RoutedInsertResult result = session.InsertWalkedRoutedFixed32Scalar16FromShelfImage(rootRouterOffset, profile, target.Offset, entry.Bytes, key0, key1, key2, key3, identity, identity, allowDuplicateKeys: true, maxRouterHops: 32);
                        if (result.InsertResult != Fixed32Scalar16InsertResult.Inserted)
                        {
                            throw new InvalidDataException($"Expected FS32-16 routed bulk split insert, got {result.Kind}/{result.InsertResult}.");
                        }

                        AddFixed32Scalar16RoutedBulkWriteFallback(result, ref telemetry);
                        insertedByCache = true;
                    }
                    else
                    {
                        throw new InvalidDataException($"Expected FS32-16 cached routed bulk insert, got {cachedResult}.");
                    }
                }

                if (!insertedByCache)
                {
                    telemetry.WalkedFallbackCount++;
                    Fixed32Scalar16RoutedInsertResult result = session.InsertWalkedRoutedFixed32Scalar16(rootRouterOffset, profile, key0, key1, key2, key3, identity, allowDuplicateKeys: true, maxRouterHops: 32);
                    if (result.InsertResult != Fixed32Scalar16InsertResult.Inserted)
                    {
                        throw new InvalidDataException($"Expected FS32-16 routed bulk insert, got {result.Kind}/{result.InsertResult}.");
                    }

                    AddFixed32Scalar16RoutedBulkWriteFallback(result, ref telemetry);
                    routeTargetCache.Clear();
                }

                telemetry.InsertedCount++;
                telemetry.Checksum += unchecked((long)(key0 ^ key1 ^ key2 ^ key3 ^ identity ^ (ulong)(i + 1)));
            }

            foreach (KeyValuePair<long, Fixed32Scalar16BatchShelfCacheEntry> pair in dirtyShelves)
            {
                StageFixed32Scalar16ShelfDeltaRewrite(session, pair.Key, profile, pair.Value, ref telemetry);
            }

            (DataKernelCommitTelemetry commit, long deferredRequests, _) = durabilityBatch.Commit();
            foreach (KeyValuePair<long, Fixed32Scalar16BatchShelfCacheEntry> pair in dirtyShelves)
            {
                session.StoreFixed32Scalar16CleanShelfBytesForBatch(pair.Key, profile, pair.Value.Bytes);
            }

            telemetry.CommitCount++;
            telemetry.DeferredCommitRequests += deferredRequests;
            telemetry.WriteCallCount += commit.WriteCallCount;
            telemetry.BytesWritten += commit.BytesWritten;
            telemetry.SetLengthCallCount += commit.SetLengthCallCount;
        }

        return telemetry;
    }



    private static void StageFixed32Scalar16ShelfDeltaRewrite(
        LibraDexFileSession session,
        long shelfOffset,
        Fixed32Scalar16Profile profile,
        Fixed32Scalar16BatchShelfCacheEntry entry,
        ref RoutedBulkWriteLoopTelemetry telemetry)
    {
        if (!entry.Dirty)
        {
            return;
        }

        byte[] currentBytes = entry.Bytes;
        Fixed32Scalar16ReadOnly readOnly = new(currentBytes, profile);
        if (!readOnly.IsValid)
        {
            throw new InvalidDataException("The batch-local FS32-16 shelf cache attempted to stage invalid shelf bytes.");
        }

        ChangedSpan headerSpan = entry.GetHeaderSpan();
        ChangedSpan slotSpan = entry.GetSlotSpan();
        ChangedSpan itemSpan = entry.GetItemSpan();
        int deltaBytes = headerSpan.Length + slotSpan.Length + itemSpan.Length;
        if (deltaBytes <= 0)
        {
            return;
        }

        if (deltaBytes >= profile.ShelfExtentSize)
        {
            session.StageFixed32Scalar16ShelfRewriteForBatch(shelfOffset, profile, currentBytes);
            telemetry.FullShelfStageCount++;
            return;
        }

        telemetry.DeltaShelfStageCount++;
        AddScalar16Scalar16ChangedSpanAttribution(headerSpan, ref telemetry);
        AddScalar16Scalar16ChangedSpanAttribution(slotSpan, ref telemetry);
        AddScalar16Scalar16ChangedSpanAttribution(itemSpan, ref telemetry);
        StageFixed32Scalar16ShelfChangedSpan(session, shelfOffset, profile, currentBytes, headerSpan);
        StageFixed32Scalar16ShelfChangedSpan(session, shelfOffset, profile, currentBytes, slotSpan);
        StageFixed32Scalar16ShelfChangedSpan(session, shelfOffset, profile, currentBytes, itemSpan);
    }



    private static void StageFixed32Scalar16ShelfChangedSpan(
        LibraDexFileSession session,
        long shelfOffset,
        Fixed32Scalar16Profile profile,
        byte[] currentBytes,
        ChangedSpan span)
    {
        if (span.Length == 0)
        {
            return;
        }

        session.StageFixed32Scalar16ShelfSliceRewriteForBatch(
            shelfOffset,
            profile,
            span.Offset,
            currentBytes.AsSpan(span.Offset, span.Length));
    }



    private static bool TryGetCachedFixed32Scalar16RouteTarget(
        Dictionary<Fixed32Scalar16BatchRouteCacheKey, Fixed32Scalar16RouteTarget> cache,
        ulong key0,
        ulong key1,
        ulong key2,
        ulong key3,
        out Fixed32Scalar16RouteTarget target)
    {
        for (byte depth = 31; depth > 0; depth--)
        {
            if (cache.TryGetValue(Fixed32Scalar16BatchRouteCacheKey.CreateForDepth(key0, key1, key2, key3, depth), out target))
            {
                return true;
            }
        }

        return cache.TryGetValue(Fixed32Scalar16BatchRouteCacheKey.CreateForDepth(key0, key1, key2, key3, 0), out target);
    }



    private static void AddFixed32Scalar16RoutedBulkWriteFallback(
        Fixed32Scalar16RoutedInsertResult result,
        ref RoutedBulkWriteLoopTelemetry telemetry)
    {
        switch (result.Kind)
        {
            case Fixed32Scalar16RoutedInsertKind.WalkedNoSplit:
                telemetry.NoSplitFallbackCount++;
                break;
            case Fixed32Scalar16RoutedInsertKind.WalkedShelfTransformSplit:
                telemetry.TransformSplitFallbackCount++;
                break;
            case Fixed32Scalar16RoutedInsertKind.WalkedParentRouteSplit:
                telemetry.ParentRouteSplitFallbackCount++;
                break;
            case Fixed32Scalar16RoutedInsertKind.NoOp:
            case Fixed32Scalar16RoutedInsertKind.KeyConflict:
                break;
            default:
                throw new InvalidDataException($"Unknown FS32-16 routed bulk-write fallback kind {result.Kind}.");
        }
    }



    private sealed class Fixed32Scalar16BatchShelfCacheEntry
    {
        internal Fixed32Scalar16BatchShelfCacheEntry(byte[] bytes)
        {
            Bytes = bytes;
        }

        internal byte[] Bytes { get; }

        internal bool Dirty { get; set; }

        private int headerStart = int.MaxValue;
        private int headerEnd;
        private int slotStart = int.MaxValue;
        private int slotEnd;
        private int itemStart = int.MaxValue;
        private int itemEnd;

        internal void Include(Fixed32Scalar16MutationBounds bounds)
        {
            IncludeRange(ref headerStart, ref headerEnd, bounds.HeaderOffset, bounds.HeaderLength);
            IncludeRange(ref slotStart, ref slotEnd, bounds.SlotOffset, bounds.SlotLength);
            IncludeRange(ref itemStart, ref itemEnd, bounds.ItemOffset, bounds.ItemLength);
        }

        internal ChangedSpan GetHeaderSpan()
        {
            return GetSpan(headerStart, headerEnd);
        }

        internal ChangedSpan GetSlotSpan()
        {
            return GetSpan(slotStart, slotEnd);
        }

        internal ChangedSpan GetItemSpan()
        {
            return GetSpan(itemStart, itemEnd);
        }

        private static void IncludeRange(ref int start, ref int end, int offset, int length)
        {
            if (length <= 0)
            {
                return;
            }

            start = Math.Min(start, offset);
            end = Math.Max(end, checked(offset + length));
        }

        private static ChangedSpan GetSpan(int start, int end)
        {
            return start == int.MaxValue ? default : new ChangedSpan(start, end - start);
        }
    }



    private readonly record struct Fixed32Scalar16BatchRouteCacheKey(byte Depth, ulong Prefix0, ulong Prefix1, ulong Prefix2, ulong Prefix3)
    {
        internal static Fixed32Scalar16BatchRouteCacheKey Create(ulong key0, ulong key1, ulong key2, ulong key3, ushort routerDepth)
        {
            if (routerDepth > 31)
            {
                throw new InvalidDataException($"The FS32-16 route depth {routerDepth} cannot be cached for a 32-byte encoded fixed key.");
            }

            return CreateForDepth(key0, key1, key2, key3, (byte)routerDepth);
        }

        internal static Fixed32Scalar16BatchRouteCacheKey CreateForDepth(ulong key0, ulong key1, ulong key2, ulong key3, byte depth)
        {
            ExtractPrefix(key0, key1, key2, key3, depth, out ulong prefix0, out ulong prefix1, out ulong prefix2, out ulong prefix3);
            return new Fixed32Scalar16BatchRouteCacheKey(depth, prefix0, prefix1, prefix2, prefix3);
        }

        private static void ExtractPrefix(ulong key0, ulong key1, ulong key2, ulong key3, byte depth, out ulong prefix0, out ulong prefix1, out ulong prefix2, out ulong prefix3)
        {
            int prefixBits = (depth + 1) * 8;
            prefix0 = 0;
            prefix1 = 0;
            prefix2 = 0;
            prefix3 = 0;
            if (prefixBits <= 64)
            {
                prefix0 = key0 >> (64 - prefixBits);
                return;
            }

            prefix0 = key0;
            if (prefixBits <= 128)
            {
                int prefix1Bits = prefixBits - 64;
                prefix1 = key1 >> (64 - prefix1Bits);
                return;
            }

            prefix1 = key1;
            if (prefixBits <= 192)
            {
                int prefix2Bits = prefixBits - 128;
                prefix2 = key2 >> (64 - prefix2Bits);
                return;
            }

            prefix2 = key2;
            int prefix3Bits = prefixBits - 192;
            prefix3 = key3 >> (64 - prefix3Bits);
        }
    }



    private static void CreateFixed32Scalar16BulkWriteKey(
        int batch,
        int itemOrdinal,
        int itemsPerBatch,
        int prefixCount,
        out ulong key0,
        out ulong key1,
        out ulong key2,
        out ulong key3)
    {
        key0 = CreateRoutedBulkWriteKey(batch, itemOrdinal, itemsPerBatch, prefixCount);
        key1 = CreateSqliteScalar16Scalar16LowHalf(key0);
        key2 = unchecked((key1 << 29) ^ (key0 >> 11) ^ 0xD1B5_4A32_D192_ED03UL);
        key3 = unchecked((key2 << 7) ^ (key1 >> 19) ^ 0x94D0_49BB_1331_11EBUL);
    }



    private static long RunSqliteFixed32Scalar16BlobWriteLoop(
        SqliteConnection connection,
        int batches,
        int itemsPerBatch,
        int prefixCount,
        int[] order,
        int batchOffset)
    {
        long checksum = 0;
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT INTO items(k, i) VALUES($k, $i);";
        SqliteParameter keyParameter = command.Parameters.Add("$k", SqliteType.Blob);
        SqliteParameter identityParameter = command.Parameters.Add("$i", SqliteType.Integer);
        for (int batch = 0; batch < batches; batch++)
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            command.Transaction = transaction;
            int logicalBatch = batchOffset + batch;
            for (int i = 0; i < itemsPerBatch; i++)
            {
                CreateFixed32Scalar16BulkWriteKey(logicalBatch, order[i], itemsPerBatch, prefixCount, out ulong key0, out ulong key1, out ulong key2, out ulong key3);
                byte[] keyBlob = CreateSqliteFixed32Scalar8Blob(key0, key1, key2, key3);
                ulong identityLow = Fixed32Scalar16Layout.EncodeUnsignedScalar8(key0);
                byte[] identityBlob = CreateSqliteScalar16Scalar16Blob(0, identityLow);
                keyParameter.Value = keyBlob;
                identityParameter.Value = identityBlob;
                if (command.ExecuteNonQuery() != 1)
                {
                    throw new InvalidDataException("SQLite FS32-16 BLOB comparison insert did not affect exactly one row.");
                }

                checksum += unchecked((long)(key0 ^ key1 ^ key2 ^ key3 ^ identityLow ^ (ulong)(i + 1)));
            }

            transaction.Commit();
            command.Transaction = null;
        }

        return checksum;
    }



    private static void InitializeSqliteFixed32Scalar16BlobSchema(SqliteConnection connection)
    {
        ExecuteSqliteScalar8Scalar8NonQuery(
            connection,
            "CREATE TABLE items(k BLOB NOT NULL CHECK(length(k)=32), i BLOB NOT NULL CHECK(length(i)=16), PRIMARY KEY(k, i)) WITHOUT ROWID;");
    }



    private static void PrintFixed32Scalar16ReadParityResults(
        ReadOnlySpan<Fixed32Scalar16ReadParityResult> libraResults,
        ReadOnlySpan<SqliteScalar8Scalar8ReadResult> sqliteResults)
    {
        Console.WriteLine("| scenario | samples | FS32-16 ids/sec | SQLite ids/sec | FS32-16/SQLite | FS32-16 ns/id | SQLite ns/id | FS32-16 reads/range | FS32-16 bytes/range |");
        Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|");
        for (int i = 0; i < libraResults.Length; i++)
        {
            Fixed32Scalar16ReadParityResult libra = libraResults[i];
            SqliteScalar8Scalar8ReadResult sqlite = sqliteResults[i];
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"| {libra.Name} | {libra.SampleCount} | {libra.IdentitiesPerSecond:F2} | {sqlite.IdentitiesPerSecond:F2} | {FormatMultiplier(libra.IdentitiesPerSecond, sqlite.IdentitiesPerSecond)} | {libra.NsPerIdentity:F2} | {sqlite.NsPerIdentity:F2} | {libra.ReadsPerRange:F3} | {libra.BytesPerRange:F2} |"));
        }
    }



    private static void CreateFixed32Scalar16PrefixRangeBounds(
        int lowerPrefix,
        int upperPrefix,
        out ulong lower0,
        out ulong lower1,
        out ulong lower2,
        out ulong lower3,
        out ulong upper0,
        out ulong upper1,
        out ulong upper2,
        out ulong upper3)
    {
        lower0 = (ulong)(byte)lowerPrefix << 56;
        lower1 = 0;
        lower2 = 0;
        lower3 = 0;
        upper0 = ((ulong)(byte)upperPrefix << 56) | 0x00FF_FFFF_FFFF_FFFFUL;
        upper1 = ulong.MaxValue;
        upper2 = ulong.MaxValue;
        upper3 = ulong.MaxValue;
    }


    private static AllShapeReadRangeCase[] CreateAllShapeReadRangeCases(int prefixCount)
    {
        int last = prefixCount - 1;
        List<AllShapeReadRangeCase> ranges =
        [
            new("prefix 0", 0, 0)
        ];

        int three = Math.Min(2, last);
        if (three > 0)
        {
            ranges.Add(new("prefix 0-2", 0, three));
        }

        int eight = Math.Min(7, last);
        if (eight > three)
        {
            ranges.Add(new($"prefix 0-{eight}", 0, eight));
        }

        if (last > eight)
        {
            ranges.Add(new($"prefix 0-{last}", 0, last));
        }

        return [.. ranges];
    }


    private static void MeasureAllShapeReadRangeSweepSs88(
        string directory,
        int batches,
        int itemsPerBatch,
        int prefixCount,
        int iterations,
        int repeatCount,
        int[] sortedOrder,
        DataKernelOptions options,
        SqliteScalar8Scalar8Options sqliteOptions,
        Scalar8Scalar8Profile profile,
        ReadOnlySpan<AllShapeReadRangeCase> ranges,
        ulong[] identities,
        List<AllShapeReadRangeSweepRow> rows)
    {
        string libraPath = Path.Combine(directory, "read-sweep-ss8-8.lbdx");
        File.Delete(libraPath);
        using (Scalar8Scalar8Index created = Indexes.SS88.Create(libraPath, DataKernelBackingKind.File, name: "read-sweep-ss8-8", options: options, developerMetadata: CreateDesignPerfMetadata(8801), telemetryOptions: DataKernelTelemetryOptions.EnabledOptions, shelfExtentSize: profile.ShelfExtentSize))
        {
            _ = RunScalar8Scalar8RoutedBulkWriteLoop(created, batches, itemsPerBatch, prefixCount, sortedOrder, batchOffset: 0);
        }

        string sqlitePath = Path.Combine(directory, "read-sweep-sqlite-ss8-8.db");
        ResetSqliteScalar8Scalar8Files(sqlitePath);
        using (SqliteConnection connection = OpenSqliteScalar8Scalar8Connection(sqlitePath, sqliteOptions))
        {
            InitializeSqliteScalar8Scalar8Schema(connection);
            RunSqliteScalar8Scalar8WriteLoop(connection, batches, itemsPerBatch, prefixCount, sortedOrder, batchOffset: 0);
        }

        using Scalar8Scalar8Index opened = Indexes.SS88.Open(libraPath, options: options, telemetryOptions: DataKernelTelemetryOptions.EnabledOptions);
        using SqliteConnection sqlite = OpenSqliteScalar8Scalar8Connection(sqlitePath, sqliteOptions);
        for (int i = 0; i < ranges.Length; i++)
        {
            AllShapeReadRangeCase range = ranges[i];
            Scalar8Scalar8PublicBulkReadResult libra = MeasureRepeatedScalar8Scalar8PublicBulkReadRange(opened, $"SS8-8 {range.Name}", batches, itemsPerBatch, prefixCount, range.LowerPrefix, range.UpperPrefix, iterations, enableCoalescing: true, identities, repeatCount);
            SqliteScalar8Scalar8ReadResult sql = MeasureRepeatedSqliteScalar8Scalar8ReadRange(sqlite, $"sqlite SS8-8 {range.Name}", batches, itemsPerBatch, prefixCount, range.LowerPrefix, range.UpperPrefix, iterations, identities, repeatCount);
            double libraLogicalMiBs = CalculateMiBs((double)libra.TotalIdentityCount * Scalar8Scalar8Layout.IdentitySize, Math.Max(libra.Elapsed.TotalSeconds, 0.000000001d));
            rows.Add(CreateAllShapeReadRangeSweepRow("SS8-8", 16, 8, range.Name, libra.RangeIdentityCount, libra.IdentitiesPerSecond, libra.NsPerIdentity, libra.ReadsPerRange, libra.BytesPerRange, libraLogicalMiBs, sql.IdentitiesPerSecond, sql.NsPerIdentity, sql.LogicalMiBs));
        }
    }


    private static AllShapeReadRangeSweepSample MeasureScalar16Scalar8ReadRange(LibraDexFileSession session, long rootRouterOffset, Scalar16Scalar8Profile profile, AllShapeReadRangeCase range, int batches, int itemsPerBatch, int prefixCount, int iterations, Span<ulong> identities)
    {
        int expectedCount = CountScalar8Scalar8PublicBulkReadExpectedKeys(batches, itemsPerBatch, prefixCount, range.LowerPrefix, range.UpperPrefix);
        ulong[] expected = CreateSortedScalar8Scalar8PublicBulkReadExpectedKeys(batches, itemsPerBatch, prefixCount, range.LowerPrefix, range.UpperPrefix, expectedCount);
        CreateScalar16RangeBounds(range, out ulong lowerHigh, out ulong lowerLow, out ulong upperHigh, out ulong upperLow);
        byte[] shelfScratch = new byte[profile.ShelfExtentSize];
        using RouteTargetKindCache targetKindCache = RouteTargetKindCache.Rent();
        long checksum = 0;
        long total = 0;
        _ = session.GetAndResetReadTelemetry();
        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            int count = session.ReadScalar16Scalar8IdentityRange(rootRouterOffset, profile, lowerHigh, lowerLow, upperHigh, upperLow, maxRouterHops: 8, identities, shelfScratch, targetKindCache);
            if (count != expectedCount)
            {
                throw new InvalidDataException($"SS16-8 range diagnostic for {range.Name}: expected {expectedCount}, actual {count}, root {DescribeScalar16Scalar8RouteTargets(session, rootRouterOffset, profile, range)}.");
            }

            checksum += CheckScalar8Scalar8PublicBulkReadRange(identities, count, expected);
            total += count;
        }

        watch.Stop();
        DataKernelReadTelemetry telemetry = session.GetAndResetReadTelemetry();
        return CreateAllShapeReadRangeSweepSample(range.Name, iterations, expectedCount, total, watch.Elapsed, Scalar16Scalar8Layout.IdentitySize, telemetry, checksum);
    }


    private static AllShapeReadRangeSweepSample MeasureScalar8Scalar16ReadRange(LibraDexFileSession session, long rootRouterOffset, Scalar8Scalar16Profile profile, AllShapeReadRangeCase range, int batches, int itemsPerBatch, int prefixCount, int iterations, Span<ulong> identityHighs, Span<ulong> identityLows)
    {
        int expectedCount = CountScalar8Scalar8PublicBulkReadExpectedKeys(batches, itemsPerBatch, prefixCount, range.LowerPrefix, range.UpperPrefix);
        ulong[] expected = CreateSortedScalar8Scalar8PublicBulkReadExpectedKeys(batches, itemsPerBatch, prefixCount, range.LowerPrefix, range.UpperPrefix, expectedCount);
        CreateScalar8RangeBounds(range, out ulong lower, out ulong upper);
        byte[] shelfScratch = new byte[profile.ShelfExtentSize];
        using RouteTargetKindCache targetKindCache = RouteTargetKindCache.Rent();
        long checksum = 0;
        long total = 0;
        _ = session.GetAndResetReadTelemetry();
        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            int count = session.ReadScalar8Scalar16IdentityRange(rootRouterOffset, profile, lower, upper, maxRouterHops: 8, identityHighs, identityLows, shelfScratch, targetKindCache);
            checksum += CheckScalar8Scalar8PublicBulkReadRange(identityHighs, count, expected);
            total += count;
        }

        watch.Stop();
        DataKernelReadTelemetry telemetry = session.GetAndResetReadTelemetry();
        return CreateAllShapeReadRangeSweepSample(range.Name, iterations, expectedCount, total, watch.Elapsed, Scalar8Scalar16Layout.IdentitySize, telemetry, checksum);
    }


    private static AllShapeReadRangeSweepSample MeasureScalar16Scalar16ReadRange(LibraDexFileSession session, long rootRouterOffset, Scalar16Scalar16Profile profile, AllShapeReadRangeCase range, int batches, int itemsPerBatch, int prefixCount, int iterations, Span<ulong> identityHighs, Span<ulong> identityLows)
    {
        int expectedCount = CountScalar8Scalar8PublicBulkReadExpectedKeys(batches, itemsPerBatch, prefixCount, range.LowerPrefix, range.UpperPrefix);
        ulong[] expected = CreateSortedScalar8Scalar8PublicBulkReadExpectedKeys(batches, itemsPerBatch, prefixCount, range.LowerPrefix, range.UpperPrefix, expectedCount);
        CreateScalar16RangeBounds(range, out ulong lowerHigh, out ulong lowerLow, out ulong upperHigh, out ulong upperLow);
        byte[] shelfScratch = new byte[profile.ShelfExtentSize];
        using RouteTargetKindCache targetKindCache = RouteTargetKindCache.Rent();
        long checksum = 0;
        long total = 0;
        _ = session.GetAndResetReadTelemetry();
        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            int count = session.ReadScalar16Scalar16IdentityRange(rootRouterOffset, profile, lowerHigh, lowerLow, upperHigh, upperLow, maxRouterHops: 8, identityHighs, identityLows, shelfScratch, targetKindCache);
            checksum += CheckScalar8Scalar8PublicBulkReadRange(identityHighs, count, expected);
            total += count;
        }

        watch.Stop();
        DataKernelReadTelemetry telemetry = session.GetAndResetReadTelemetry();
        return CreateAllShapeReadRangeSweepSample(range.Name, iterations, expectedCount, total, watch.Elapsed, Scalar16Scalar16Layout.IdentitySize, telemetry, checksum);
    }


    private static void CreateScalar8RangeBounds(AllShapeReadRangeCase range, out ulong lower, out ulong upper)
    {
        lower = Scalar8Scalar8Layout.EncodeUnsignedScalar8((ulong)(byte)range.LowerPrefix << 56);
        upper = Scalar8Scalar8Layout.EncodeUnsignedScalar8(((ulong)(byte)range.UpperPrefix << 56) | 0x00FF_FFFF_FFFF_FFFFUL);
    }


    private static void CreateScalar16RangeBounds(AllShapeReadRangeCase range, out ulong lowerHigh, out ulong lowerLow, out ulong upperHigh, out ulong upperLow)
    {
        lowerHigh = Scalar8Scalar8Layout.EncodeUnsignedScalar8((ulong)(byte)range.LowerPrefix << 56);
        lowerLow = 0;
        upperHigh = Scalar8Scalar8Layout.EncodeUnsignedScalar8(((ulong)(byte)range.UpperPrefix << 56) | 0x00FF_FFFF_FFFF_FFFFUL);
        upperLow = ulong.MaxValue;
    }


    private static AllShapeReadRangeSweepSample SelectMedianAllShapeReadRangeSweepSample(ReadOnlySpan<AllShapeReadRangeSweepSample> samples)
    {
        AllShapeReadRangeSweepSample[] sorted = samples.ToArray();
        Array.Sort(sorted, static (left, right) => left.NsPerIdentity.CompareTo(right.NsPerIdentity));
        return sorted[sorted.Length / 2];
    }


    private static void PrintAllShapeReadRangeSweepRows(IReadOnlyList<AllShapeReadRangeSweepRow> rows)
    {
        Console.WriteLine("| shape | range | ids | LibraDex ids/sec | SQLite ids/sec | LD/SQLite | ns/id | reads/range | B/range | amp | raw read % |");
        Console.WriteLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        for (int i = 0; i < rows.Count; i++)
        {
            AllShapeReadRangeSweepRow row = rows[i];
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"| {row.Shape} | {row.RangeName} | {row.RangeIdentityCount} | {row.LibraDexIdentitiesPerSecond:F2} | {row.SqliteIdentitiesPerSecond:F2} | {FormatMultiplier(row.LibraDexIdentitiesPerSecond, row.SqliteIdentitiesPerSecond)} | {row.LibraDexNsPerIdentity:F2} | {row.LibraDexReadsPerRange:F3} | {row.LibraDexBytesPerRange:F2} | {row.ReadAmplification:F2}x | {FormatBaselinePercent(row.RawReadBaselinePercent)} |"));
        }
    }


    private readonly record struct AllShapeReadRangeSweepSample(
        string Name,
        int Iterations,
        int RangeIdentityCount,
        long TotalIdentityCount,
        TimeSpan Elapsed,
        double IdentitiesPerSecond,
        double NsPerIdentity,
        double LogicalMiBs,
        double ReadsPerRange,
        double BytesPerRange,
        long Checksum,
        int SampleCount);


    private readonly record struct AllShapeReadRangeSweepRow(
        string Shape,
        string RangeName,
        int PayloadBytes,
        int IdentityBytes,
        int RangeIdentityCount,
        double LibraDexIdentitiesPerSecond,
        double SqliteIdentitiesPerSecond,
        double LibraDexNsPerIdentity,
        double SqliteNsPerIdentity,
        double LibraDexReadsPerRange,
        double LibraDexBytesPerRange,
        double ReadAmplification,
        double LibraDexLogicalMiBs,
        double SqliteLogicalMiBs,
        double RawReadBaselinePercent);

}
