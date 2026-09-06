using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using LibraDex;

internal static partial class RawHarness
{
    private const int KeyStatePromotionProofScalar8InlineCapacity = (4096 - 48) / sizeof(ulong);
    private const int KeyStatePromotionProofScalar16InlineCapacity = (4096 - 48) / (sizeof(ulong) * 2);

    /// <summary>
    /// Compares the current dictionary-backed condition grouping terminal with a streaming ordered-index grouping proof.<br/>
    /// The proof intentionally stays on one simple grouping key so the first optimization target remains predictable and Abraxas-shaped.<br/>
    /// </summary>
    /// <param name="args">The harness command-line arguments.<br/></param>
    /// <returns>Zero when both paths produce identical group counts.<br/></returns>
    private static int RunGroupByExecutionProof(string[] args)
    {
        int itemCount = GetIntOption(args, "--items", 100_000);
        int tenantCount = GetIntOption(args, "--tenants", 1_000);
        int activeModulo = GetIntOption(args, "--active-modulo", 3);
        if (itemCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(args), itemCount, "Group proof item count must be positive.");
        if (tenantCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(args), tenantCount, "Group proof tenant count must be positive.");
        if (activeModulo <= 0)
            throw new ArgumentOutOfRangeException(nameof(args), activeModulo, "Group proof active modulo must be positive.");

        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        CatalogIdentityGroupIndexes group = catalog.Indexes["group-proof"];
        using LibraDexIndex<int, long> statusIndex = group["status"].Int32Keys<long>().Create();
        using LibraDexIndex<int, long> tenantIndex = group["tenant"].Int32Keys<long>().Create();
        SeedGroupByExecutionProofRows(statusIndex, tenantIndex, itemCount, tenantCount, activeModulo);

        LibraDexConditionEndCondition activeCondition = LibraDexCondition
            .ForGroup("group-proof")
            .Index("status")
            .AsInt32
            .EqualTo(1)
            .EndCondition;
        Func<string, IIndex> resolver = name => name == "status" ? statusIndex : throw new KeyNotFoundException(name);

        GroupByExecutionProofMeasurement legacy = MeasureGroupByExecutionProofLegacy(activeCondition, resolver, tenantIndex);
        GroupByExecutionProofMeasurement current = MeasureGroupByExecutionProofCurrent(activeCondition, resolver, tenantIndex);
        GroupByExecutionProofMeasurement streaming = MeasureGroupByExecutionProofStreaming(activeCondition, resolver, tenantIndex);
        ValidateGroupByExecutionProof(legacy.Counts, current.Counts);
        ValidateGroupByExecutionProof(current.Counts, streaming.Counts);
        GroupByMetadataProofMeasurement legacyMetadata = MeasureGroupByMetadataProofLegacy(activeCondition, resolver, tenantIndex);
        GroupByMetadataProofMeasurement currentMetadata = MeasureGroupByMetadataProofCurrent(activeCondition, resolver, tenantIndex);
        ValidateGroupByMetadataProof(legacyMetadata.Metadata, currentMetadata.Metadata);
        GroupByRepresentativeProofMeasurement legacyRepresentatives = MeasureGroupByRepresentativeProofLegacy(activeCondition, resolver, tenantIndex);
        GroupByRepresentativeProofMeasurement currentRepresentatives = MeasureGroupByRepresentativeProofCurrent(activeCondition, resolver, tenantIndex);
        ValidateGroupByRepresentativeProof(legacyRepresentatives.Representatives, currentRepresentatives.Representatives);
        GroupByReaderProofMeasurement legacyReader = MeasureGroupByReaderProofLegacy(activeCondition, resolver, tenantIndex);
        GroupByReaderProofMeasurement currentReader = MeasureGroupByReaderProofCurrent(activeCondition, resolver, tenantIndex);
        ValidateGroupByReaderProof(legacyReader.Groups, currentReader.Groups);

        Console.WriteLine("count-path groups total-count elapsed-ms alloc-bytes bytes-per-row");
        WriteGroupByExecutionProofRow("legacy-materialized", legacy, itemCount);
        WriteGroupByExecutionProofRow("production-counts", current, itemCount);
        WriteGroupByExecutionProofRow("streaming-counts", streaming, itemCount);
        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "production-speedup {0:F2} production-allocation-ratio {1:F2}",
            legacy.Elapsed.TotalMilliseconds / Math.Max(current.Elapsed.TotalMilliseconds, 0.000001),
            (double)legacy.AllocatedBytes / Math.Max(current.AllocatedBytes, 1)));
        Console.WriteLine("metadata-path groups total-count elapsed-ms alloc-bytes bytes-per-row");
        WriteGroupByMetadataProofRow("legacy-materialized", legacyMetadata, itemCount);
        WriteGroupByMetadataProofRow("production-metadata", currentMetadata, itemCount);
        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "metadata-production-speedup {0:F2} metadata-production-allocation-ratio {1:F2}",
            legacyMetadata.Elapsed.TotalMilliseconds / Math.Max(currentMetadata.Elapsed.TotalMilliseconds, 0.000001),
            (double)legacyMetadata.AllocatedBytes / Math.Max(currentMetadata.AllocatedBytes, 1)));
        Console.WriteLine("representative-path groups elapsed-ms alloc-bytes bytes-per-row");
        WriteGroupByRepresentativeProofRow("legacy-materialized", legacyRepresentatives, itemCount);
        WriteGroupByRepresentativeProofRow("production-representatives", currentRepresentatives, itemCount);
        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "representative-production-speedup {0:F2} representative-production-allocation-ratio {1:F2}",
            legacyRepresentatives.Elapsed.TotalMilliseconds / Math.Max(currentRepresentatives.Elapsed.TotalMilliseconds, 0.000001),
            (double)legacyRepresentatives.AllocatedBytes / Math.Max(currentRepresentatives.AllocatedBytes, 1)));
        Console.WriteLine("reader-path groups total-count elapsed-ms alloc-bytes bytes-per-row");
        WriteGroupByReaderProofRow("legacy-materialized", legacyReader, itemCount);
        WriteGroupByReaderProofRow("current-materialized-reader", currentReader, itemCount);
        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "reader-production-speedup {0:F2} reader-production-allocation-ratio {1:F2}",
            legacyReader.Elapsed.TotalMilliseconds / Math.Max(currentReader.Elapsed.TotalMilliseconds, 0.000001),
            (double)legacyReader.AllocatedBytes / Math.Max(currentReader.AllocatedBytes, 1)));
        Console.WriteLine("group-by-execution-proof ok");
        return 0;
    }

    /// <summary>
    /// Proves the direct target-index grouping path where the condition leaf and grouping index are the same physical index.<br/>
    /// This is the guarded case that can use the shared condition cursor executor without prebuilding the grouped terminal's candidate identity `HashSet`.<br/>
    /// </summary>
    /// <param name="args">The harness command-line arguments.<br/></param>
    /// <returns>Zero when the direct target-index grouped counts match the legacy materialized baseline.<br/></returns>
    private static int RunGroupByDirectTargetProof(string[] args)
    {
        int itemCount = GetIntOption(args, "--items", 100_000);
        int tenantCount = GetIntOption(args, "--tenants", 1_000);
        int activeModulo = GetIntOption(args, "--active-modulo", 3);
        int upperTenant = GetIntOption(args, "--upper-tenant", Math.Max(0, tenantCount / 2 - 1));
        if (itemCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(args), itemCount, "Direct target group proof item count must be positive.");
        if (tenantCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(args), tenantCount, "Direct target group proof tenant count must be positive.");
        if (activeModulo <= 0)
            throw new ArgumentOutOfRangeException(nameof(args), activeModulo, "Direct target group proof active modulo must be positive.");
        if (upperTenant < 0 || upperTenant >= tenantCount)
            throw new ArgumentOutOfRangeException(nameof(args), upperTenant, "Direct target group proof upper tenant must be inside the tenant range.");

        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        CatalogIdentityGroupIndexes group = catalog.Indexes["direct-target-group-proof"];
        using LibraDexIndex<int, long> statusIndex = group["status"].Int32Keys<long>().Create();
        using LibraDexIndex<int, long> tenantIndex = group["tenant"].Int32Keys<long>().Create();
        SeedGroupByExecutionProofRows(statusIndex, tenantIndex, itemCount, tenantCount, activeModulo);

        LibraDexConditionEndCondition tenantCondition = LibraDexCondition
            .ForGroup("direct-target-group-proof")
            .Index("tenant")
            .AsInt32
            .Between(0, upperTenant)
            .EndCondition;
        Func<string, IIndex> resolver = name => name == "tenant" ? tenantIndex : throw new KeyNotFoundException(name);

        GroupByExecutionProofMeasurement legacy = MeasureGroupByExecutionProofLegacy(tenantCondition, resolver, tenantIndex);
        GroupByExecutionProofMeasurement current = MeasureGroupByExecutionProofCurrent(tenantCondition, resolver, tenantIndex);
        ValidateGroupByExecutionProof(legacy.Counts, current.Counts);

        Console.WriteLine("direct-target-count-path groups total-count elapsed-ms alloc-bytes bytes-per-row");
        WriteGroupByExecutionProofRow("legacy-materialized", legacy, itemCount);
        WriteGroupByExecutionProofRow("production-direct-target-counts", current, itemCount);
        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "direct-target-speedup {0:F2} direct-target-allocation-ratio {1:F2}",
            legacy.Elapsed.TotalMilliseconds / Math.Max(current.Elapsed.TotalMilliseconds, 0.000001),
            (double)legacy.AllocatedBytes / Math.Max(current.AllocatedBytes, 1)));
        Console.WriteLine("group-by-direct-target-proof ok");
        return 0;
    }

    /// <summary>
    /// Proves condition grouping over a routed composite index without caller-owned key concatenation.<br/>
    /// This is the first multi-key grouping proof: the condition is scalar, while grouping uses the composite full key `(tenant, user)` through `Groups(...).By&lt;TIdentity&gt;(compositeIndex)`.<br/>
    /// </summary>
    /// <param name="args">The harness command-line arguments.<br/></param>
    /// <returns>Zero when composite grouped counts and metadata match the deterministic seed model.<br/></returns>
    private static int RunGroupByCompositeProof(string[] args)
    {
        int itemCount = GetIntOption(args, "--items", 100_000);
        int tenantCount = GetIntOption(args, "--tenants", 1_000);
        int userCount = GetIntOption(args, "--users", 128);
        int activeModulo = GetIntOption(args, "--active-modulo", 3);
        if (itemCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(args), itemCount, "Composite group proof item count must be positive.");
        if (tenantCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(args), tenantCount, "Composite group proof tenant count must be positive.");
        if (userCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(args), userCount, "Composite group proof user count must be positive.");
        if (activeModulo <= 0)
            throw new ArgumentOutOfRangeException(nameof(args), activeModulo, "Composite group proof active modulo must be positive.");

        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        CatalogIdentityGroupIndexes group = catalog.Indexes["composite-group-proof"];
        using LibraDexIndex<int, long> statusIndex = group["status"].Int32Keys<long>().Create();
        IIndex compositeHandle = group["tenantUser"].Composite<long>(C.Text("tenant"), C.Text("user")).Create();
        LibraDexRoutedCompositeIndex compositeIndex = (LibraDexRoutedCompositeIndex)compositeHandle;
        Dictionary<string, GroupByCompositeProofExpectedRow> expected = SeedGroupByCompositeProofRows(
            statusIndex,
            compositeIndex,
            itemCount,
            tenantCount,
            userCount,
            activeModulo);

        LibraDexConditionEndCondition activeCondition = LibraDexCondition
            .ForGroup("composite-group-proof")
            .Index("status")
            .AsInt32
            .EqualTo(1)
            .EndCondition;
        Func<string, IIndex> resolver = name => name == "status" ? statusIndex : throw new KeyNotFoundException(name);

        GroupByCompositeCountProofMeasurement counts = MeasureGroupByCompositeCounts(activeCondition, resolver, compositeIndex);
        GroupByCompositeMetadataProofMeasurement metadata = MeasureGroupByCompositeMetadata(activeCondition, resolver, compositeIndex);
        ValidateGroupByCompositeCounts(expected, counts.Counts);
        ValidateGroupByCompositeMetadata(expected, metadata.Metadata);
        ValidateCompositeExtendedScalarCodec();

        _ = compositeIndex.VisitEntries(static (parts, identity) =>
        {
            if (parts.Length != 2 || parts[0] is not string || parts[1] is not string || identity is not long)
                throw new InvalidDataException("Borrowed composite visitor warm-up returned an incompatible tuple.");
        });
        long visitorRows = 0;
        long visitorIdentitySum = 0;
        long visitorAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long visitorReported = compositeIndex.VisitEntries((parts, identity) =>
        {
            if (parts.Length != 2 || parts[0] is not string || parts[1] is not string || identity is not long typedIdentity)
                throw new InvalidDataException("Borrowed composite visitor returned an incompatible tuple.");
            visitorRows++;
            visitorIdentitySum = unchecked(visitorIdentitySum + typedIdentity);
        });
        long visitorAllocated = GC.GetAllocatedBytesForCurrentThread() - visitorAllocatedBefore;
        long expectedIdentitySum = checked((long)itemCount * (itemCount + 1L) / 2L);
        if (visitorReported != itemCount || visitorRows != itemCount || visitorIdentitySum != expectedIdentitySum)
            throw new InvalidDataException("Borrowed composite visitor did not report every seeded tuple exactly once.");
        if (visitorAllocated > 8_192)
            throw new InvalidDataException($"Borrowed composite visitor allocated {visitorAllocated:N0} bytes for {itemCount:N0} rows; expected a bounded traversal under 8,192 bytes.");

        using (LibraDexRoutedCompositeIndex.EntryCursor warmCursor = compositeIndex.OpenEntryCursor())
        {
            if (!warmCursor.Read() || warmCursor.KeyParts.Length != 2 || warmCursor.Identity is not long)
                throw new InvalidDataException("Borrowed composite cursor warm-up returned an incompatible tuple.");
        }
        long cursorRows = 0;
        long cursorIdentitySum = 0;
        long cursorAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        using (LibraDexRoutedCompositeIndex.EntryCursor cursor = compositeIndex.OpenEntryCursor())
        {
            while (cursor.Read())
            {
                ReadOnlySpan<object?> parts = cursor.KeyParts;
                if (parts.Length != 2 || parts[0] is not string || parts[1] is not string || cursor.Identity is not long typedIdentity)
                    throw new InvalidDataException("Borrowed composite cursor returned an incompatible tuple.");
                cursorRows++;
                cursorIdentitySum = unchecked(cursorIdentitySum + typedIdentity);
            }
        }
        long cursorAllocated = GC.GetAllocatedBytesForCurrentThread() - cursorAllocatedBefore;
        if (cursorRows != itemCount || cursorIdentitySum != expectedIdentitySum)
            throw new InvalidDataException("Borrowed composite cursor did not report every seeded tuple exactly once.");
        if (cursorAllocated > 8_192)
            throw new InvalidDataException($"Borrowed composite cursor allocated {cursorAllocated:N0} bytes for {itemCount:N0} rows; expected a bounded traversal under 8,192 bytes.");

        Console.WriteLine("composite-count-path groups total-count elapsed-ms alloc-bytes bytes-per-row");
        WriteGroupByCompositeCountProofRow("production-composite-counts", counts, itemCount);
        Console.WriteLine("composite-metadata-path groups total-count elapsed-ms alloc-bytes bytes-per-row");
        WriteGroupByCompositeMetadataProofRow("production-composite-metadata", metadata, itemCount);
        Console.WriteLine($"composite-borrowed-visitor rows={visitorRows:N0} alloc-bytes={visitorAllocated:N0}");
        Console.WriteLine($"composite-borrowed-cursor rows={cursorRows:N0} alloc-bytes={cursorAllocated:N0}");
        Console.WriteLine("group-by-composite-proof ok");
        return 0;
    }

    /// <summary>
    /// Verifies exact durable value round trips for the extended scalar types accepted by routed composite shapes.<br/>
    /// The proof uses the same primitive codec invoked by composite snapshots and node pages, catching advertised-type drift before a catalog reopen loses a route value.<br/>
    /// </summary>
    private static void ValidateCompositeExtendedScalarCodec()
    {
        object[] values =
        [
            Int128.MinValue + 123,
            UInt128.MaxValue - 123,
            -123.5f,
            Math.PI,
            123456789.0123456789m
        ];
        foreach (object value in values)
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
                LibraDexCompositeSnapshotCodec.WriteValue(writer, value.GetType(), value);
            stream.Position = 0;
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            object? reopened = LibraDexCompositeSnapshotCodec.ReadValue(reader, value.GetType());
            if (!value.Equals(reopened) || stream.Position != stream.Length)
                throw new InvalidDataException($"Composite scalar codec did not exactly round-trip '{value.GetType().FullName}'.");
        }
    }

    /// <summary>
    /// Proves grouped aggregate terminals against a legacy member-list aggregate baseline.<br/>
    /// The aggregate values are identity-derived because LibraDex is an identity index and does not own the source object payloads being indexed.<br/>
    /// </summary>
    /// <param name="args">The harness command-line arguments.<br/></param>
    /// <returns>Zero when sum, min, max, and custom aggregate terminals match the legacy materialized baseline.<br/></returns>
    private static int RunGroupByAggregateProof(string[] args)
    {
        int itemCount = GetIntOption(args, "--items", 100_000);
        int tenantCount = GetIntOption(args, "--tenants", 1_000);
        int activeModulo = GetIntOption(args, "--active-modulo", 3);
        if (itemCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(args), itemCount, "Aggregate group proof item count must be positive.");
        if (tenantCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(args), tenantCount, "Aggregate group proof tenant count must be positive.");
        if (activeModulo <= 0)
            throw new ArgumentOutOfRangeException(nameof(args), activeModulo, "Aggregate group proof active modulo must be positive.");

        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        CatalogIdentityGroupIndexes group = catalog.Indexes["aggregate-group-proof"];
        using LibraDexIndex<int, long> statusIndex = group["status"].Int32Keys<long>().Create();
        using LibraDexIndex<int, long> tenantIndex = group["tenant"].Int32Keys<long>().Create();
        SeedGroupByExecutionProofRows(statusIndex, tenantIndex, itemCount, tenantCount, activeModulo);

        LibraDexConditionEndCondition activeCondition = LibraDexCondition
            .ForGroup("aggregate-group-proof")
            .Index("status")
            .AsInt32
            .EqualTo(1)
            .EndCondition;
        Func<string, IIndex> resolver = name => name == "status" ? statusIndex : throw new KeyNotFoundException(name);

        GroupByAggregateProofMeasurement legacy = MeasureGroupByAggregateProofLegacy(activeCondition, resolver, tenantIndex);
        GroupByAggregateProofMeasurement current = MeasureGroupByAggregateProofCurrent(activeCondition, resolver, tenantIndex);
        ValidateGroupByAggregateProof(legacy.Rows, current.Rows);
        ValidateGroupByAggregateConvenienceTerminals(activeCondition, resolver, tenantIndex, legacy.Rows);

        Console.WriteLine("aggregate-path groups total-sum elapsed-ms alloc-bytes bytes-per-row");
        WriteGroupByAggregateProofRow("legacy-materialized", legacy, itemCount);
        WriteGroupByAggregateProofRow("production-one-pass-aggregate", current, itemCount);
        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "aggregate-production-speedup {0:F2} aggregate-production-allocation-ratio {1:F2}",
            legacy.Elapsed.TotalMilliseconds / Math.Max(current.Elapsed.TotalMilliseconds, 0.000001),
            (double)legacy.AllocatedBytes / Math.Max(current.AllocatedBytes, 1)));
        Console.WriteLine("group-by-aggregate-proof ok");
        return 0;
    }

    /// <summary>
    /// Proves row-streaming grouped reader behavior against the existing materialized grouped reader.<br/>
    /// The row reader reports group boundaries without allocating one `IReadOnlyList` per group, so it is the guarded replacement path for large full grouped-result scans.<br/>
    /// </summary>
    /// <param name="args">The harness command-line arguments.<br/></param>
    /// <returns>Zero when row streaming produces the same group counts and edge identities as the materialized reader.</returns>
    private static int RunGroupByRowReaderProof(string[] args)
    {
        int itemCount = GetIntOption(args, "--items", 100_000);
        int tenantCount = GetIntOption(args, "--tenants", 1_000);
        int activeModulo = GetIntOption(args, "--active-modulo", 3);
        if (itemCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(args), itemCount, "Grouped row reader proof item count must be positive.");
        if (tenantCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(args), tenantCount, "Grouped row reader proof tenant count must be positive.");
        if (activeModulo <= 0)
            throw new ArgumentOutOfRangeException(nameof(args), activeModulo, "Grouped row reader proof active modulo must be positive.");

        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        CatalogIdentityGroupIndexes group = catalog.Indexes["row-reader-group-proof"];
        using LibraDexIndex<int, long> statusIndex = group["status"].Int32Keys<long>().Create();
        using LibraDexIndex<int, long> tenantIndex = group["tenant"].Int32Keys<long>().Create();
        SeedGroupByExecutionProofRows(statusIndex, tenantIndex, itemCount, tenantCount, activeModulo);

        LibraDexConditionEndCondition activeCondition = LibraDexCondition
            .ForGroup("row-reader-group-proof")
            .Index("status")
            .AsInt32
            .EqualTo(1)
            .EndCondition;
        Func<string, IIndex> resolver = name => name == "status" ? statusIndex : throw new KeyNotFoundException(name);

        GroupByReaderProofMeasurement materialized = MeasureGroupByReaderProofCurrent(activeCondition, resolver, tenantIndex);
        GroupByReaderProofMeasurement streaming = MeasureGroupByRowReaderProofCurrent(activeCondition, resolver, tenantIndex);
        ValidateGroupByReaderProof(materialized.Groups, streaming.Groups);

        Console.WriteLine("row-reader-path groups total-count elapsed-ms alloc-bytes bytes-per-row");
        WriteGroupByReaderProofRow("current-materialized-reader", materialized, itemCount);
        WriteGroupByReaderProofRow("streaming-row-reader", streaming, itemCount);
        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "row-reader-speedup {0:F2} row-reader-allocation-ratio {1:F2}",
            materialized.Elapsed.TotalMilliseconds / Math.Max(streaming.Elapsed.TotalMilliseconds, 0.000001),
            (double)materialized.AllocatedBytes / Math.Max(streaming.AllocatedBytes, 1)));
        Console.WriteLine("group-by-row-reader-proof ok");
        return 0;
    }

    /// <summary>
    /// Proves direct grouping by the logical string index facade using exact non-null string keys.<br/>
    /// The proof covers unrelated-condition grouping through the fallback candidate set and same-index string conditions through the direct target tuple stream.<br/>
    /// </summary>
    /// <param name="args">The harness command-line arguments.<br/></param>
    /// <returns>Zero when exact string grouped counts match the deterministic seed model.<br/></returns>
    private static int RunGroupByStringProof(string[] args)
    {
        int itemCount = GetIntOption(args, "--items", 100_000);
        int nameCount = GetIntOption(args, "--names", 1_000);
        int activeModulo = GetIntOption(args, "--active-modulo", 3);
        string prefix = GetOption(args, "--prefix", "name-000");
        if (itemCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(args), itemCount, "String group proof item count must be positive.");
        if (nameCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(args), nameCount, "String group proof name count must be positive.");
        if (activeModulo <= 0)
            throw new ArgumentOutOfRangeException(nameof(args), activeModulo, "String group proof active modulo must be positive.");

        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        CatalogIdentityGroupIndexes group = catalog.Indexes["string-group-proof"];
        using LibraDexIndex<int, ulong> statusIndex = group["status"].Int32Keys<ulong>().Create();
        using LibraDexStringScalar8Index nameIndex = group["name"].String.Create(stringKeys: StringKeys.ExactFoldedAndSortKey);
        GroupByStringProofSeed seed = SeedGroupByStringProofRows(statusIndex, nameIndex, itemCount, nameCount, activeModulo, prefix);

        LibraDexConditionEndCondition activeCondition = LibraDexCondition
            .ForGroup("string-group-proof")
            .Index("status")
            .AsInt32
            .EqualTo(1)
            .EndCondition;
        Func<string, IIndex> statusResolver = name => name == "status" ? statusIndex : throw new KeyNotFoundException(name);

        LibraDexConditionEndCondition prefixCondition = LibraDexCondition
            .ForGroup("string-group-proof")
            .Index("name")
            .AsString
            .StartsWith(prefix)
            .EndCondition;
        Func<string, IIndex> nameResolver = name => name == "name" ? nameIndex : throw new KeyNotFoundException(name);

        GroupByStringCountProofMeasurement activeCounts = MeasureGroupByStringCounts(activeCondition, statusResolver, nameIndex);
        GroupByStringCountProofMeasurement foldedCounts = MeasureGroupByFoldedStringCounts(activeCondition, statusResolver, nameIndex);
        GroupByStringCountProofMeasurement sortKeyCounts = MeasureGroupBySortKeyStringCounts(activeCondition, statusResolver, nameIndex);
        GroupByStringCountProofMeasurement prefixCounts = MeasureGroupByStringCounts(prefixCondition, nameResolver, nameIndex);
        GroupByStringMetadataProofMeasurement exactMetadata = MeasureGroupByStringMetadata(activeCondition, statusResolver, nameIndex);
        GroupByStringMetadataProofMeasurement foldedMetadata = MeasureGroupByFoldedStringMetadata(activeCondition, statusResolver, nameIndex);
        GroupByStringMetadataProofMeasurement sortKeyMetadata = MeasureGroupBySortKeyStringMetadata(activeCondition, statusResolver, nameIndex);
        GroupByStringAggregateProofMeasurement exactAggregates = MeasureGroupByStringAggregates(activeCondition, statusResolver, nameIndex);
        GroupByStringAggregateProofMeasurement foldedAggregates = MeasureGroupByFoldedStringAggregates(activeCondition, statusResolver, nameIndex);
        GroupByStringAggregateProofMeasurement sortKeyAggregates = MeasureGroupBySortKeyStringAggregates(activeCondition, statusResolver, nameIndex);
        GroupByStringMetadataProofMeasurement exactRows = MeasureGroupByStringRowReader(activeCondition, statusResolver, nameIndex);
        ValidateGroupByStringCounts(seed.ActiveCounts, activeCounts.Counts);
        ValidateGroupByStringCounts(seed.FoldedCounts, foldedCounts.Counts);
        ValidateGroupByStringCounts(seed.SortKeyCounts, sortKeyCounts.Counts);
        ValidateGroupByStringCounts(seed.PrefixCounts, prefixCounts.Counts);
        ValidateGroupByStringMetadata(seed.ActiveRows, exactMetadata.Metadata);
        ValidateGroupByStringMetadata(seed.FoldedRows, foldedMetadata.Metadata);
        ValidateGroupByStringMetadata(seed.SortKeyRows, sortKeyMetadata.Metadata);
        ValidateGroupByStringAggregates(seed.ActiveRows, exactAggregates.Rows);
        ValidateGroupByStringAggregates(seed.FoldedRows, foldedAggregates.Rows);
        ValidateGroupByStringAggregates(seed.SortKeyRows, sortKeyAggregates.Rows);
        ValidateGroupByStringMetadata(seed.ActiveRows, exactRows.Metadata);

        Console.WriteLine("string-count-path groups total-count elapsed-ms alloc-bytes bytes-per-row");
        WriteGroupByStringCountProofRow("production-string-exact-counts", activeCounts, itemCount);
        WriteGroupByStringCountProofRow("production-string-folded-counts", foldedCounts, itemCount);
        WriteGroupByStringCountProofRow("production-string-sortkey-counts", sortKeyCounts, itemCount);
        WriteGroupByStringCountProofRow("production-string-exact-direct-target-counts", prefixCounts, itemCount);
        Console.WriteLine("string-metadata-path groups total-count elapsed-ms alloc-bytes bytes-per-row");
        WriteGroupByStringMetadataProofRow("production-string-exact-metadata", exactMetadata, itemCount);
        WriteGroupByStringMetadataProofRow("production-string-folded-metadata", foldedMetadata, itemCount);
        WriteGroupByStringMetadataProofRow("production-string-sortkey-metadata", sortKeyMetadata, itemCount);
        Console.WriteLine("string-aggregate-path groups total-sum elapsed-ms alloc-bytes bytes-per-row");
        WriteGroupByStringAggregateProofRow("production-string-exact-aggregate", exactAggregates, itemCount);
        WriteGroupByStringAggregateProofRow("production-string-folded-aggregate", foldedAggregates, itemCount);
        WriteGroupByStringAggregateProofRow("production-string-sortkey-aggregate", sortKeyAggregates, itemCount);
        Console.WriteLine("string-row-reader-path groups total-count elapsed-ms alloc-bytes bytes-per-row");
        WriteGroupByStringMetadataProofRow("production-string-exact-row-reader", exactRows, itemCount);
        Console.WriteLine("group-by-string-proof ok");
        return 0;
    }

    /// <summary>
    /// Forces null and empty string key-state routes past inline capacity and validates the promoted terminal-identity route through ordinary string conditions and tuple mutations.<br/>
    /// This proof protects the exact string grouping path from regressing to the old inline-only route limit when Abraxas fields contain many null or empty values.<br/>
    /// </summary>
    /// <param name="args">The harness command-line arguments.<br/></param>
    /// <returns>Zero when promoted null and empty key-state routes remain readable, deletable, and rekeyable.</returns>
    private static int RunGroupByStringKeyStatePromotionProof(string[] args)
    {
        int perStateCount = GetIntOption(args, "--per-state", 2_048);
        if (perStateCount <= KeyStatePromotionProofScalar8InlineCapacity)
            throw new ArgumentOutOfRangeException(nameof(args), perStateCount, "String key-state promotion proof count must exceed scalar-8 inline route capacity.");

        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        CatalogIdentityGroupIndexes group = catalog.Indexes["string-key-state-promotion-proof"];
        using LibraDexStringScalar8Index nameIndex = group["name"].String.Create(stringKeys: StringKeys.ExactFoldedAndSortKey);
        Func<string, IIndex> resolver = name => name == "name" ? nameIndex : throw new KeyNotFoundException(name);

        for (int i = 0; i < perStateCount; i++)
        {
            ulong nullIdentity = (ulong)i + 1UL;
            ulong emptyIdentity = (ulong)perStateCount + (ulong)i + 1UL;
            if (!nameIndex.Add(null, nullIdentity).Inserted)
                throw new InvalidOperationException("Null key-state promotion proof failed to insert a null identity.");
            if (!nameIndex.Add(string.Empty, emptyIdentity).Inserted)
                throw new InvalidOperationException("Empty key-state promotion proof failed to insert an empty identity.");
        }

        ValidateStringKeyStatePromotionCount(resolver, nameIndex, NullKey.Null, perStateCount, "null-before-mutation");
        ValidateStringKeyStatePromotionCount(resolver, nameIndex, NullKey.Empty, perStateCount, "empty-before-mutation");
        ValidateStringKeyStatePromotionCount(resolver, nameIndex, NullKey.NullOrEmpty, checked(perStateCount * 2), "null-or-empty-before-mutation");

        ulong deletedNullIdentity = (ulong)(perStateCount / 2);
        ulong deletedEmptyIdentity = (ulong)perStateCount + (ulong)(perStateCount / 2);
        ulong movedNullToEmptyIdentity = (ulong)perStateCount;
        ulong movedEmptyToTextIdentity = (ulong)(perStateCount * 2);
        if (!nameIndex.Delete(null, deletedNullIdentity))
            throw new InvalidOperationException("Null key-state promotion proof could not delete a promoted null identity.");
        if (!nameIndex.Delete(string.Empty, deletedEmptyIdentity))
            throw new InvalidOperationException("Empty key-state promotion proof could not delete a promoted empty identity.");
        if (!nameIndex.Rekey(movedNullToEmptyIdentity, null, string.Empty))
            throw new InvalidOperationException("String key-state promotion proof could not rekey a promoted null identity to empty.");
        if (!nameIndex.Rekey(movedEmptyToTextIdentity, string.Empty, "promoted-normal"))
            throw new InvalidOperationException("String key-state promotion proof could not rekey a promoted empty identity to text.");

        ValidateStringKeyStatePromotionCount(resolver, nameIndex, NullKey.Null, perStateCount - 2, "null-after-mutation");
        ValidateStringKeyStatePromotionCount(resolver, nameIndex, NullKey.Empty, perStateCount - 1, "empty-after-mutation");
        ValidateStringKeyStatePromotionCount(resolver, nameIndex, NullKey.NullOrEmpty, checked((perStateCount * 2) - 3), "null-or-empty-after-mutation");
        ValidateStringKeyStateTextConditionCount(resolver, nameIndex, "promoted-normal", 1, "text-after-mutation");

        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "string-key-state-promotion per-state {0} inline-capacity {1}",
            perStateCount,
            KeyStatePromotionProofScalar8InlineCapacity));
        Console.WriteLine("group-by-string-key-state-promotion-proof ok");
        return 0;
    }

    /// <summary>
    /// Forces scalar-16 null and empty binary key-state routes past inline capacity and validates promoted route reads, deletes, and rekeys through the public fixed-32/GUID index surface.<br/>
    /// This is the scalar-16 counterpart to the string key-state promotion proof and covers the `FS32-16` route users found by lxl tracing.<br/>
    /// </summary>
    /// <param name="args">The harness command-line arguments.<br/></param>
    /// <returns>Zero when promoted scalar-16 key-state routes remain readable, deletable, and rekeyable.</returns>
    private static int RunScalar16KeyStatePromotionProof(string[] args)
    {
        int perStateCount = GetIntOption(args, "--per-state", 1_024);
        if (perStateCount <= KeyStatePromotionProofScalar16InlineCapacity)
            throw new ArgumentOutOfRangeException(nameof(args), perStateCount, "Scalar-16 key-state promotion proof count must exceed scalar-16 inline route capacity.");

        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        CatalogIdentityGroupIndexes group = catalog.Indexes["scalar16-key-state-promotion-proof"];
        using LibraDexIndex<byte[], Guid> index = group["hash"].Blob.Scalar<Guid>(LibraDexScalarWidth.Bytes32).Create();
        Func<string, IIndex> resolver = name => name == "hash" ? index : throw new KeyNotFoundException(name);

        for (int i = 0; i < perStateCount; i++)
        {
            Guid nullIdentity = CreateScalar16KeyStateProofGuid(i + 1);
            Guid emptyIdentity = CreateScalar16KeyStateProofGuid(perStateCount + i + 1);
            if (!index.Add(NullKey.Null, nullIdentity).Inserted)
                throw new InvalidOperationException("Scalar-16 key-state promotion proof failed to insert a null-route identity.");
            if (!index.Add(NullKey.Empty, emptyIdentity).Inserted)
                throw new InvalidOperationException("Scalar-16 key-state promotion proof failed to insert an empty-route identity.");
        }

        ValidateScalar16KeyStatePromotionCount(resolver, NullKey.Null, perStateCount, "null-before-mutation");
        ValidateScalar16KeyStatePromotionCount(resolver, NullKey.Empty, perStateCount, "empty-before-mutation");
        ValidateScalar16KeyStatePromotionCount(resolver, NullKey.NullOrEmpty, checked(perStateCount * 2), "null-or-empty-before-mutation");

        Guid deletedNullIdentity = CreateScalar16KeyStateProofGuid(perStateCount / 2);
        Guid deletedEmptyIdentity = CreateScalar16KeyStateProofGuid(perStateCount + (perStateCount / 2));
        Guid movedNullToEmptyIdentity = CreateScalar16KeyStateProofGuid(perStateCount);
        Guid movedEmptyToBinaryIdentity = CreateScalar16KeyStateProofGuid(perStateCount * 2);
        if (!index.Delete(NullKey.Null, deletedNullIdentity))
            throw new InvalidOperationException("Scalar-16 key-state promotion proof could not delete a promoted null identity.");
        if (!index.Delete(NullKey.Empty, deletedEmptyIdentity))
            throw new InvalidOperationException("Scalar-16 key-state promotion proof could not delete a promoted empty identity.");
        if (!index.Rekey(movedNullToEmptyIdentity, NullKey.Null, NullKey.Empty))
            throw new InvalidOperationException("Scalar-16 key-state promotion proof could not rekey a promoted null identity to empty.");
        if (!index.Rekey(movedEmptyToBinaryIdentity, NullKey.Empty, CreateScalar16KeyStateProofKey(1)))
            throw new InvalidOperationException("Scalar-16 key-state promotion proof could not rekey a promoted empty identity to a fixed binary key.");

        ValidateScalar16KeyStatePromotionCount(resolver, NullKey.Null, perStateCount - 2, "null-after-mutation");
        ValidateScalar16KeyStatePromotionCount(resolver, NullKey.Empty, perStateCount - 1, "empty-after-mutation");
        ValidateScalar16KeyStatePromotionCount(resolver, NullKey.NullOrEmpty, checked((perStateCount * 2) - 3), "null-or-empty-after-mutation");
        ValidateScalar16BinaryConditionCount(resolver, CreateScalar16KeyStateProofKey(1), 1, "binary-after-mutation");

        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "scalar16-key-state-promotion per-state {0} inline-capacity {1}",
            perStateCount,
            KeyStatePromotionProofScalar16InlineCapacity));
        Console.WriteLine("scalar16-key-state-promotion-proof ok");
        return 0;
    }

    /// <summary>
    /// Forces fixed-N BigInteger scalar-null routes past inline capacity for both scalar-8 and scalar-16 identity lanes.<br/>
    /// The proof validates condition materialization, `All` inclusion, exact scalar-null deletion, and normal fixed-key inserts in the same index so fixed-N optional-key support stays aligned with the generic scalar shapes.<br/>
    /// </summary>
    /// <param name="args">The harness command-line arguments.<br/></param>
    /// <returns>Zero when promoted fixed-N scalar-null routes remain readable and mutable.<br/></returns>
    private static int RunFixedNKeyStatePromotionProof(string[] args)
    {
        int scalar8Count = GetIntOption(args, "--scalar8-count", 2_048);
        int scalar16Count = GetIntOption(args, "--scalar16-count", 1_024);
        if (scalar8Count <= KeyStatePromotionProofScalar8InlineCapacity)
            throw new ArgumentOutOfRangeException(nameof(args), scalar8Count, "Fixed-N scalar-8 key-state proof count must exceed scalar-8 inline route capacity.");
        if (scalar16Count <= KeyStatePromotionProofScalar16InlineCapacity)
            throw new ArgumentOutOfRangeException(nameof(args), scalar16Count, "Fixed-N scalar-16 key-state proof count must exceed scalar-16 inline route capacity.");

        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        CatalogIdentityGroupIndexes group = catalog.Indexes["fixedn-key-state-promotion-proof"];
        using LibraDexBigIntScalar8Index<long> scalar8Index = group["score8"].BigIntKeys<long>(maxBytes: 32).Create();
        using LibraDexBigIntScalar8Index<Guid> scalar16Index = group["score16"].BigIntKeys<Guid>(maxBytes: 32).Create();
        Func<string, IIndex> resolver = name => name switch
        {
            "score8" => scalar8Index,
            "score16" => scalar16Index,
            _ => throw new KeyNotFoundException(name)
        };

        for (int i = 0; i < scalar8Count; i++)
        {
            if (!scalar8Index.Add(ScalarNull.Null, i + 1L).Inserted)
                throw new InvalidOperationException("Fixed-N scalar-8 key-state promotion proof failed to insert a scalar-null identity.");
        }

        for (int i = 0; i < scalar16Count; i++)
        {
            if (!scalar16Index.Add(ScalarNull.Null, CreateScalar16KeyStateProofGuid(i + 1)).Inserted)
                throw new InvalidOperationException("Fixed-N scalar-16 key-state promotion proof failed to insert a scalar-null identity.");
        }

        ValidateFixedNScalar8KeyStatePromotionCount(resolver, scalar8Count, "scalar8-before-delete");
        ValidateFixedNScalar16KeyStatePromotionCount(resolver, scalar16Count, "scalar16-before-delete");
        ValidateFixedNAllCount(resolver, "score8", scalar8Count, "scalar8-all-before-normal");
        ValidateFixedNAllCount(resolver, "score16", scalar16Count, "scalar16-all-before-normal");

        if (!scalar8Index.Delete(ScalarNull.Null, scalar8Count / 2L))
            throw new InvalidOperationException("Fixed-N scalar-8 key-state promotion proof could not delete a promoted scalar-null identity.");
        if (!scalar16Index.Delete(ScalarNull.Null, CreateScalar16KeyStateProofGuid(scalar16Count / 2)))
            throw new InvalidOperationException("Fixed-N scalar-16 key-state promotion proof could not delete a promoted scalar-null identity.");

        ValidateGenericInsert(scalar8Index.Add(new BigInteger(42), 9_000_001L), "fixed-N scalar-8 normal insert after scalar-null promotion");
        Guid normalGuid = CreateScalar16KeyStateProofGuid(9_000_002);
        ValidateGenericInsert(scalar16Index.Add(new BigInteger(84), normalGuid), "fixed-N scalar-16 normal insert after scalar-null promotion");
        ValidateGenericInsert(scalar8Index.Add(new BigInteger(43), 9_000_003L), "fixed-N scalar-8 normal delete seed");
        Guid deleteGuid = CreateScalar16KeyStateProofGuid(9_000_004);
        ValidateGenericInsert(scalar16Index.Add(new BigInteger(85), deleteGuid), "fixed-N scalar-16 normal delete seed");
        if (!scalar8Index.Delete(new BigInteger(43), 9_000_003L))
            throw new InvalidOperationException("Fixed-N scalar-8 proof could not delete an ordinary fixed-key tuple.");
        if (!scalar16Index.Delete(new BigInteger(85), deleteGuid))
            throw new InvalidOperationException("Fixed-N scalar-16 proof could not delete an ordinary fixed-key tuple.");
        if (!scalar8Index.Rekey(1L, ScalarNull.Null, new BigInteger(44)))
            throw new InvalidOperationException("Fixed-N scalar-8 proof could not rekey a scalar-null identity to an ordinary key.");
        if (!scalar8Index.Rekey(9_000_001L, new BigInteger(42), new BigInteger(45)))
            throw new InvalidOperationException("Fixed-N scalar-8 proof could not rekey an ordinary key to another ordinary key.");
        if (!scalar8Index.Rekey(9_000_001L, new BigInteger(45), ScalarNull.Null))
            throw new InvalidOperationException("Fixed-N scalar-8 proof could not rekey an ordinary key back to scalar-null.");
        Guid movedGuid = CreateScalar16KeyStateProofGuid(1);
        if (!scalar16Index.Rekey(movedGuid, ScalarNull.Null, new BigInteger(86)))
            throw new InvalidOperationException("Fixed-N scalar-16 proof could not rekey a scalar-null identity to an ordinary key.");
        if (!scalar16Index.Rekey(normalGuid, new BigInteger(84), new BigInteger(87)))
            throw new InvalidOperationException("Fixed-N scalar-16 proof could not rekey an ordinary key to another ordinary key.");
        if (!scalar16Index.Rekey(normalGuid, new BigInteger(87), ScalarNull.Null))
            throw new InvalidOperationException("Fixed-N scalar-16 proof could not rekey an ordinary key back to scalar-null.");

        ValidateFixedNScalar8KeyStatePromotionCount(resolver, scalar8Count - 1, "scalar8-after-delete");
        ValidateFixedNScalar16KeyStatePromotionCount(resolver, scalar16Count - 1, "scalar16-after-delete");
        ValidateFixedNAllCount(resolver, "score8", scalar8Count, "scalar8-all-after-normal");
        ValidateFixedNAllCount(resolver, "score16", scalar16Count, "scalar16-all-after-normal");
        ValidateFixedNScalar8NormalCondition(resolver, new BigInteger(42), 0, "scalar8-original-normal-after-rekey");
        ValidateFixedNScalar8NormalCondition(resolver, new BigInteger(44), 1, "scalar8-null-to-normal-after-rekey");
        ValidateFixedNScalar8NormalCondition(resolver, new BigInteger(45), 0, "scalar8-normal-to-null-after-rekey");
        ValidateFixedNScalar16NormalCondition(resolver, new BigInteger(84), normalGuid, 0, "scalar16-original-normal-after-rekey");
        ValidateFixedNScalar16NormalCondition(resolver, new BigInteger(86), movedGuid, 1, "scalar16-null-to-normal-after-rekey");
        ValidateFixedNScalar16NormalCondition(resolver, new BigInteger(87), normalGuid, 0, "scalar16-normal-to-null-after-rekey");

        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "fixedn-key-state-promotion scalar8-count {0} scalar8-inline-capacity {1} scalar16-count {2} scalar16-inline-capacity {3}",
            scalar8Count,
            KeyStatePromotionProofScalar8InlineCapacity,
            scalar16Count,
            KeyStatePromotionProofScalar16InlineCapacity));
        Console.WriteLine("fixedn-key-state-promotion-proof ok");
        return 0;
    }

    /// <summary>
    /// Seeds a status filter index and a grouping-key index with deterministic Abraxas-shaped identity rows.<br/>
    /// The status index receives only active identities, while the tenant index receives every identity so grouping must filter by candidate identity.<br/>
    /// </summary>
    /// <param name="statusIndex">The condition/filter index.<br/></param>
    /// <param name="tenantIndex">The grouping index.<br/></param>
    /// <param name="itemCount">The total number of identities to seed.<br/></param>
    /// <param name="tenantCount">The number of tenant groups to distribute rows across.<br/></param>
    /// <param name="activeModulo">Every Nth identity is omitted from active status when N is greater than one.<br/></param>
    private static void SeedGroupByExecutionProofRows(
        LibraDexIndex<int, long> statusIndex,
        LibraDexIndex<int, long> tenantIndex,
        int itemCount,
        int tenantCount,
        int activeModulo)
    {
        for (int i = 0; i < itemCount; i++)
        {
            long identity = i + 1L;
            int tenant = i % tenantCount;
            _ = tenantIndex.Insert(tenant, identity);
            if (i % activeModulo != 0)
            {
                _ = statusIndex.Insert(1, identity);
            }
        }
    }

    /// <summary>
    /// Seeds a scalar status filter and composite `(tenant, user)` grouping index while building the expected grouped result model.<br/>
    /// The expected model is string-keyed only inside the harness so the production path can keep returning `LibraDexCompositeKey` without imposing delimiter rules on callers.<br/>
    /// </summary>
    /// <param name="statusIndex">The condition/filter index.<br/></param>
    /// <param name="compositeIndex">The composite grouping index.<br/></param>
    /// <param name="itemCount">The total number of identities to seed.<br/></param>
    /// <param name="tenantCount">The number of tenant key-part values.<br/></param>
    /// <param name="userCount">The number of user key-part values.<br/></param>
    /// <param name="activeModulo">Every Nth identity is omitted from active status when N is greater than one.<br/></param>
    /// <returns>The deterministic grouped counts and edge identities expected from the active rows.<br/></returns>
    private static Dictionary<string, GroupByCompositeProofExpectedRow> SeedGroupByCompositeProofRows(
        LibraDexIndex<int, long> statusIndex,
        LibraDexRoutedCompositeIndex compositeIndex,
        int itemCount,
        int tenantCount,
        int userCount,
        int activeModulo)
    {
        Dictionary<string, GroupByCompositeProofExpectedRow> expected = new(StringComparer.Ordinal);
        for (int i = 0; i < itemCount; i++)
        {
            long identity = i + 1L;
            string tenant = string.Create(CultureInfo.InvariantCulture, $"tenant-{i % tenantCount:D5}");
            string user = string.Create(CultureInfo.InvariantCulture, $"user-{i % userCount:D5}");
            _ = compositeIndex.Insert(Key.Of(tenant, user), identity);
            if (i % activeModulo == 0)
            {
                continue;
            }

            _ = statusIndex.Insert(1, identity);
            string key = CompositeProofKey(tenant, user);
            if (expected.TryGetValue(key, out GroupByCompositeProofExpectedRow row))
            {
                expected[key] = row with
                {
                    Count = row.Count + 1,
                    LastIdentity = identity
                };
            }
            else
            {
                expected.Add(key, new GroupByCompositeProofExpectedRow(1, identity, identity));
            }
        }

        return expected;
    }

    /// <summary>
    /// Seeds a scalar status filter and logical string grouping index while building exact string expected count models.<br/>
    /// Name values are ordinal and intentionally include case-distinct buckets so the proof detects accidental folded grouping.<br/>
    /// </summary>
    /// <param name="statusIndex">The condition/filter index.<br/></param>
    /// <param name="nameIndex">The exact string grouping index.<br/></param>
    /// <param name="itemCount">The total number of identities to seed.<br/></param>
    /// <param name="nameCount">The number of exact string group keys to distribute rows across.<br/></param>
    /// <param name="activeModulo">Every Nth identity is omitted from active status when N is greater than one.<br/></param>
    /// <param name="prefix">The exact prefix used by the direct-target string condition proof.<br/></param>
    /// <returns>The deterministic active exact, folded, sort-key, and prefix grouped count models.<br/></returns>
    private static GroupByStringProofSeed SeedGroupByStringProofRows(
        LibraDexIndex<int, ulong> statusIndex,
        LibraDexStringScalar8Index nameIndex,
        int itemCount,
        int nameCount,
        int activeModulo,
        string prefix)
    {
        Dictionary<string, long> activeCounts = new(StringComparer.Ordinal);
        Dictionary<string, long> foldedCounts = new(StringComparer.Ordinal);
        Dictionary<string, long> sortKeyCounts = new(StringComparer.Ordinal);
        Dictionary<string, long> prefixCounts = new(StringComparer.Ordinal);
        Dictionary<string, GroupByStringProofExpectedRow> activeRows = new(StringComparer.Ordinal);
        Dictionary<string, GroupByStringProofExpectedRow> foldedRows = new(StringComparer.Ordinal);
        Dictionary<string, GroupByStringProofExpectedRow> sortKeyRows = new(StringComparer.Ordinal);
        for (int i = 0; i < itemCount; i++)
        {
            ulong identity = (ulong)i + 1UL;
            string? key = CreateStringGroupProofKey(i, nameCount);
            _ = nameIndex.Add(key, identity);
            if (key is not null && key.StartsWith(prefix, StringComparison.Ordinal))
            {
                IncrementStringProofCount(prefixCounts, CreateStringProofKeyLabel(key));
            }

            if (i % activeModulo == 0)
            {
                continue;
            }

            _ = statusIndex.Insert(1, identity);
            IncrementStringProofCount(activeCounts, CreateStringProofKeyLabel(key));
            IncrementStringProofCount(foldedCounts, CreateStringProofKeyLabel(key?.ToLower(CultureInfo.InvariantCulture)));
            IncrementStringProofCount(sortKeyCounts, CreateStringProofSortKeyLabel(key));
            AddStringProofExpectedRow(activeRows, CreateStringProofKeyLabel(key), identity);
            AddStringProofExpectedRow(foldedRows, CreateStringProofKeyLabel(key?.ToLower(CultureInfo.InvariantCulture)), identity);
            AddStringProofExpectedRow(sortKeyRows, CreateStringProofSortKeyLabel(key), identity);
        }

        return new GroupByStringProofSeed(activeCounts, foldedCounts, sortKeyCounts, prefixCounts, activeRows, foldedRows, sortKeyRows);
    }

    /// <summary>
    /// Measures a legacy materialized grouping baseline for comparison against the production count terminal.<br/>
    /// This path materializes the condition identities, scans the grouping index, builds per-group member lists, and then projects counts.<br/>
    /// </summary>
    /// <param name="condition">The completed condition used to select candidate identities.<br/></param>
    /// <param name="resolver">The condition index resolver.<br/></param>
    /// <param name="groupingIndex">The grouping key index.<br/></param>
    /// <returns>The measured counts and resource deltas.<br/></returns>
    private static GroupByExecutionProofMeasurement MeasureGroupByExecutionProofLegacy(
        LibraDexConditionEndCondition condition,
        Func<string, IIndex> resolver,
        LibraDexIndex<int, long> groupingIndex)
    {
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch watch = Stopwatch.StartNew();
        HashSet<long> identities = new(condition.ToList<long>(resolver), LibraDexKeyEquality<long>.Comparer);
        Dictionary<int, List<long>> groups = new();
        foreach (LibraDexTuple<int, long> tuple in groupingIndex.IterateAllTuples())
        {
            if (!identities.Contains(tuple.Identity))
                continue;

            if (!groups.TryGetValue(tuple.Key, out List<long>? group))
            {
                group = new List<long>();
                groups.Add(tuple.Key, group);
            }

            group.Add(tuple.Identity);
        }

        Dictionary<int, long> counts = new(groups.Count);
        foreach (KeyValuePair<int, List<long>> group in groups)
            counts.Add(group.Key, group.Value.Count);

        watch.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        return new GroupByExecutionProofMeasurement(counts, watch.Elapsed, allocated);
    }

    /// <summary>
    /// Measures a legacy materialized metadata baseline for comparison against the production metadata terminal.<br/>
    /// This path builds full per-group member lists before projecting count and edge identity metadata.<br/>
    /// </summary>
    /// <param name="condition">The completed condition used to select candidate identities.<br/></param>
    /// <param name="resolver">The condition index resolver.<br/></param>
    /// <param name="groupingIndex">The grouping key index.<br/></param>
    /// <returns>The measured metadata and resource deltas.<br/></returns>
    private static GroupByMetadataProofMeasurement MeasureGroupByMetadataProofLegacy(
        LibraDexConditionEndCondition condition,
        Func<string, IIndex> resolver,
        LibraDexIndex<int, long> groupingIndex)
    {
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch watch = Stopwatch.StartNew();
        HashSet<long> identities = new(condition.ToList<long>(resolver), LibraDexKeyEquality<long>.Comparer);
        Dictionary<int, List<long>> groups = new();
        foreach (LibraDexTuple<int, long> tuple in groupingIndex.IterateAllTuples())
        {
            if (!identities.Contains(tuple.Identity))
                continue;

            if (!groups.TryGetValue(tuple.Key, out List<long>? group))
            {
                group = new List<long>();
                groups.Add(tuple.Key, group);
            }

            group.Add(tuple.Identity);
        }

        Dictionary<int, GroupByMetadataProofRow> metadata = new(groups.Count);
        foreach (KeyValuePair<int, List<long>> group in groups)
            metadata.Add(group.Key, new GroupByMetadataProofRow(group.Value.Count, group.Value[0], group.Value[group.Value.Count - 1]));

        watch.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        return new GroupByMetadataProofMeasurement(metadata, watch.Elapsed, allocated);
    }

    /// <summary>
    /// Measures the current public grouping metadata terminal.<br/>
    /// This is the production path that should choose terminal-specific grouping metadata internally.<br/>
    /// </summary>
    /// <param name="condition">The completed condition used to select candidate identities.<br/></param>
    /// <param name="resolver">The condition index resolver.<br/></param>
    /// <param name="groupingIndex">The grouping key index.<br/></param>
    /// <returns>The measured metadata and resource deltas.<br/></returns>
    private static GroupByMetadataProofMeasurement MeasureGroupByMetadataProofCurrent(
        LibraDexConditionEndCondition condition,
        Func<string, IIndex> resolver,
        LibraDexIndex<int, long> groupingIndex)
    {
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch watch = Stopwatch.StartNew();
        IReadOnlyList<LibraDexGroupMetadata<int, long>> rows = condition.Groups(resolver).By(groupingIndex).Metadata();
        Dictionary<int, GroupByMetadataProofRow> metadata = new(rows.Count);
        foreach (LibraDexGroupMetadata<int, long> row in rows)
            metadata.Add(row.Key, new GroupByMetadataProofRow(row.Count, row.FirstIdentity, row.LastIdentity));

        watch.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        return new GroupByMetadataProofMeasurement(metadata, watch.Elapsed, allocated);
    }

    /// <summary>
    /// Measures a legacy materialized representative baseline for comparison against the production representative terminal.<br/>
    /// This path builds full per-group member lists before projecting first and last representatives.<br/>
    /// </summary>
    /// <param name="condition">The completed condition used to select candidate identities.<br/></param>
    /// <param name="resolver">The condition index resolver.<br/></param>
    /// <param name="groupingIndex">The grouping key index.<br/></param>
    /// <returns>The measured representatives and resource deltas.<br/></returns>
    private static GroupByRepresentativeProofMeasurement MeasureGroupByRepresentativeProofLegacy(
        LibraDexConditionEndCondition condition,
        Func<string, IIndex> resolver,
        LibraDexIndex<int, long> groupingIndex)
    {
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch watch = Stopwatch.StartNew();
        Dictionary<int, long> first = MaterializeGroupByRepresentativeProofLegacy(condition, resolver, groupingIndex, firstRepresentative: true);
        Dictionary<int, long> last = MaterializeGroupByRepresentativeProofLegacy(condition, resolver, groupingIndex, firstRepresentative: false);
        Dictionary<int, GroupByRepresentativeProofRow> representatives = new(first.Count);
        foreach (KeyValuePair<int, long> row in first)
        {
            if (!last.TryGetValue(row.Key, out long lastIdentity))
                throw new InvalidDataException($"Legacy group representative proof key {row.Key} had no last representative.");

            representatives.Add(row.Key, new GroupByRepresentativeProofRow(row.Value, lastIdentity));
        }

        watch.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        return new GroupByRepresentativeProofMeasurement(representatives, watch.Elapsed, allocated);
    }

    /// <summary>
    /// Materializes one legacy representative terminal so the proof baseline matches separate public first/last calls.<br/>
    /// </summary>
    /// <param name="condition">The completed condition used to select candidate identities.<br/></param>
    /// <param name="resolver">The condition index resolver.<br/></param>
    /// <param name="groupingIndex">The grouping key index.<br/></param>
    /// <param name="firstRepresentative">True for first representatives; false for last representatives.<br/></param>
    /// <returns>The materialized representative dictionary.<br/></returns>
    private static Dictionary<int, long> MaterializeGroupByRepresentativeProofLegacy(
        LibraDexConditionEndCondition condition,
        Func<string, IIndex> resolver,
        LibraDexIndex<int, long> groupingIndex,
        bool firstRepresentative)
    {
        HashSet<long> identities = new(condition.ToList<long>(resolver), LibraDexKeyEquality<long>.Comparer);
        Dictionary<int, List<long>> groups = new();
        foreach (LibraDexTuple<int, long> tuple in groupingIndex.IterateAllTuples())
        {
            if (!identities.Contains(tuple.Identity))
                continue;

            if (!groups.TryGetValue(tuple.Key, out List<long>? group))
            {
                group = new List<long>();
                groups.Add(tuple.Key, group);
            }

            group.Add(tuple.Identity);
        }

        Dictionary<int, long> representatives = new(groups.Count);
        foreach (KeyValuePair<int, List<long>> group in groups)
            representatives.Add(group.Key, firstRepresentative ? group.Value[0] : group.Value[group.Value.Count - 1]);

        return representatives;
    }

    /// <summary>
    /// Measures the current public grouping representative terminals.<br/>
    /// This covers first and last representatives because both should be satisfied by streamed metadata edges.<br/>
    /// </summary>
    /// <param name="condition">The completed condition used to select candidate identities.<br/></param>
    /// <param name="resolver">The condition index resolver.<br/></param>
    /// <param name="groupingIndex">The grouping key index.<br/></param>
    /// <returns>The measured representatives and resource deltas.<br/></returns>
    private static GroupByRepresentativeProofMeasurement MeasureGroupByRepresentativeProofCurrent(
        LibraDexConditionEndCondition condition,
        Func<string, IIndex> resolver,
        LibraDexIndex<int, long> groupingIndex)
    {
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch watch = Stopwatch.StartNew();
        LibraDexConditionGroupQuery<int, long> groups = condition.Groups(resolver).By(groupingIndex);
        IReadOnlyDictionary<int, long> first = groups.FirstIdentities();
        IReadOnlyDictionary<int, long> last = groups.LastIdentities();
        Dictionary<int, GroupByRepresentativeProofRow> representatives = new(first.Count);
        foreach (KeyValuePair<int, long> row in first)
        {
            if (!last.TryGetValue(row.Key, out long lastIdentity))
                throw new InvalidDataException($"Group representative proof key {row.Key} had no last representative.");

            representatives.Add(row.Key, new GroupByRepresentativeProofRow(row.Value, lastIdentity));
        }

        watch.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        return new GroupByRepresentativeProofMeasurement(representatives, watch.Elapsed, allocated);
    }

    /// <summary>
    /// Measures the legacy dictionary-backed grouped reader baseline.<br/>
    /// This path builds every group member list before reader traversal begins.<br/>
    /// </summary>
    /// <param name="condition">The completed condition used to select candidate identities.<br/></param>
    /// <param name="resolver">The condition index resolver.<br/></param>
    /// <param name="groupingIndex">The grouping key index.<br/></param>
    /// <returns>The measured group member summaries and resource deltas.<br/></returns>
    private static GroupByReaderProofMeasurement MeasureGroupByReaderProofLegacy(
        LibraDexConditionEndCondition condition,
        Func<string, IIndex> resolver,
        LibraDexIndex<int, long> groupingIndex)
    {
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch watch = Stopwatch.StartNew();
        HashSet<long> identities = new(condition.ToList<long>(resolver), LibraDexKeyEquality<long>.Comparer);
        Dictionary<int, List<long>> groups = new();
        foreach (LibraDexTuple<int, long> tuple in groupingIndex.IterateAllTuples())
        {
            if (!identities.Contains(tuple.Identity))
                continue;

            if (!groups.TryGetValue(tuple.Key, out List<long>? group))
            {
                group = new List<long>();
                groups.Add(tuple.Key, group);
            }

            group.Add(tuple.Identity);
        }

        Dictionary<int, GroupByReaderProofRow> rows = new(groups.Count);
        foreach (KeyValuePair<int, List<long>> group in groups)
            rows.Add(group.Key, new GroupByReaderProofRow(group.Value.Count, group.Value[0], group.Value[group.Value.Count - 1]));

        watch.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        return new GroupByReaderProofMeasurement(rows, watch.Elapsed, allocated);
    }

    /// <summary>
    /// Measures the current public grouped reader terminal.<br/>
    /// This path currently remains dictionary-backed because streaming the existing member-list reader shape did not prove a useful throughput or allocation win.<br/>
    /// </summary>
    /// <param name="condition">The completed condition used to select candidate identities.<br/></param>
    /// <param name="resolver">The condition index resolver.<br/></param>
    /// <param name="groupingIndex">The grouping key index.<br/></param>
    /// <returns>The measured group member summaries and resource deltas.<br/></returns>
    private static GroupByReaderProofMeasurement MeasureGroupByReaderProofCurrent(
        LibraDexConditionEndCondition condition,
        Func<string, IIndex> resolver,
        LibraDexIndex<int, long> groupingIndex)
    {
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch watch = Stopwatch.StartNew();
        Dictionary<int, GroupByReaderProofRow> rows = new();
        using LibraDexGroupReader<int, long> reader = condition.Groups(resolver).By(groupingIndex).OpenReader();
        while (reader.MoveNextGroup())
        {
            LibraDexGroup<int, long> group = reader.Current;
            rows.Add(group.Key, new GroupByReaderProofRow(group.Count, group.Items[0], group.Items[group.Items.Count - 1]));
        }

        watch.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        return new GroupByReaderProofMeasurement(rows, watch.Elapsed, allocated);
    }

    /// <summary>
    /// Measures the row-streaming grouped reader terminal.<br/>
    /// This path streams group member rows and records only proof summaries so the benchmark captures the no-member-list public reader shape.<br/>
    /// </summary>
    /// <param name="condition">The completed condition used to select candidate identities.<br/></param>
    /// <param name="resolver">The condition index resolver.<br/></param>
    /// <param name="groupingIndex">The grouping key index.<br/></param>
    /// <returns>The measured group member summaries and resource deltas.<br/></returns>
    private static GroupByReaderProofMeasurement MeasureGroupByRowReaderProofCurrent(
        LibraDexConditionEndCondition condition,
        Func<string, IIndex> resolver,
        LibraDexIndex<int, long> groupingIndex)
    {
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch watch = Stopwatch.StartNew();
        Dictionary<int, GroupByReaderProofRow> rows = new();
        int? currentKey = null;
        long currentCount = 0;
        long firstIdentity = 0;
        long lastIdentity = 0;
        long expectedGroupOrdinal = -1;
        long expectedItemOrdinal = 0;

        using LibraDexGroupRowReader<int, long> reader = condition.Groups(resolver).By(groupingIndex).OpenRowReader();
        while (reader.MoveNext())
        {
            if (reader.IsFirstInGroup)
            {
                if (currentKey is not null)
                {
                    rows.Add(currentKey.Value, new GroupByReaderProofRow(currentCount, firstIdentity, lastIdentity));
                }

                expectedGroupOrdinal++;
                expectedItemOrdinal = 0;
                currentKey = reader.CurrentKey;
                currentCount = 0;
                firstIdentity = reader.CurrentItem;
            }

            if (currentKey is null)
            {
                throw new InvalidDataException("Grouped row reader produced a row before reporting a group boundary.");
            }

            if (reader.GroupOrdinal != expectedGroupOrdinal || reader.ItemOrdinalInGroup != expectedItemOrdinal)
            {
                throw new InvalidDataException("Grouped row reader ordinal metadata did not match streamed row position.");
            }

            if (!EqualityComparer<int>.Default.Equals(reader.CurrentKey, currentKey.Value))
            {
                throw new InvalidDataException("Grouped row reader changed keys without reporting a group boundary.");
            }

            lastIdentity = reader.CurrentItem;
            currentCount++;
            expectedItemOrdinal++;
        }

        if (currentKey is not null)
        {
            rows.Add(currentKey.Value, new GroupByReaderProofRow(currentCount, firstIdentity, lastIdentity));
        }

        watch.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        return new GroupByReaderProofMeasurement(rows, watch.Elapsed, allocated);
    }

    /// <summary>
    /// Measures a legacy materialized aggregate baseline for comparison against production grouped aggregate terminals.<br/>
    /// This path builds full per-group member lists before projecting sum, min, max, and a custom aggregate value.<br/>
    /// </summary>
    /// <param name="condition">The completed condition used to select candidate identities.<br/></param>
    /// <param name="resolver">The condition index resolver.<br/></param>
    /// <param name="groupingIndex">The grouping key index.<br/></param>
    /// <returns>The measured aggregate rows and resource deltas.<br/></returns>
    private static GroupByAggregateProofMeasurement MeasureGroupByAggregateProofLegacy(
        LibraDexConditionEndCondition condition,
        Func<string, IIndex> resolver,
        LibraDexIndex<int, long> groupingIndex)
    {
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch watch = Stopwatch.StartNew();
        HashSet<long> identities = new(condition.ToList<long>(resolver), LibraDexKeyEquality<long>.Comparer);
        Dictionary<int, List<long>> groups = new();
        foreach (LibraDexTuple<int, long> tuple in groupingIndex.IterateAllTuples())
        {
            if (!identities.Contains(tuple.Identity))
                continue;

            if (!groups.TryGetValue(tuple.Key, out List<long>? group))
            {
                group = new List<long>();
                groups.Add(tuple.Key, group);
            }

            group.Add(tuple.Identity);
        }

        Dictionary<int, GroupByAggregateProofRow> rows = new(groups.Count);
        foreach (KeyValuePair<int, List<long>> group in groups)
        {
            long sum = 0;
            long min = long.MaxValue;
            long max = long.MinValue;
            long custom = 0;
            for (int i = 0; i < group.Value.Count; i++)
            {
                long value = AggregateProofValue(group.Value[i]);
                sum += value;
                min = Math.Min(min, value);
                max = Math.Max(max, value);
                custom = AggregateProofCustom(custom, group.Value[i]);
            }

            rows.Add(group.Key, new GroupByAggregateProofRow(sum, min, max, custom));
        }

        watch.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        return new GroupByAggregateProofMeasurement(rows, watch.Elapsed, allocated);
    }

    /// <summary>
    /// Measures the production grouped aggregate terminals.<br/>
    /// Sum, min, max, and custom aggregate are intentionally called separately so the proof covers each public terminal shape.<br/>
    /// </summary>
    /// <param name="condition">The completed condition used to select candidate identities.<br/></param>
    /// <param name="resolver">The condition index resolver.<br/></param>
    /// <param name="groupingIndex">The grouping key index.<br/></param>
    /// <returns>The measured aggregate rows and resource deltas.<br/></returns>
    private static GroupByAggregateProofMeasurement MeasureGroupByAggregateProofCurrent(
        LibraDexConditionEndCondition condition,
        Func<string, IIndex> resolver,
        LibraDexIndex<int, long> groupingIndex)
    {
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch watch = Stopwatch.StartNew();
        LibraDexConditionGroupQuery<int, long> groups = condition.Groups(resolver).By(groupingIndex);
        IReadOnlyDictionary<int, GroupByAggregateProofRow> aggregateRows = groups.Aggregate(
            new GroupByAggregateProofRow(0, long.MaxValue, long.MinValue, 0),
            static (row, identity) =>
            {
                long value = AggregateProofValue(identity);
                return new GroupByAggregateProofRow(
                    row.Sum + value,
                    Math.Min(row.Min, value),
                    Math.Max(row.Max, value),
                    AggregateProofCustom(row.Custom, identity));
            });
        Dictionary<int, GroupByAggregateProofRow> rows = new(aggregateRows.Count);
        foreach (KeyValuePair<int, GroupByAggregateProofRow> row in aggregateRows)
            rows.Add(row.Key, row.Value);

        watch.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        return new GroupByAggregateProofMeasurement(rows, watch.Elapsed, allocated);
    }

    /// <summary>
    /// Measures the production composite grouping count terminal.<br/>
    /// This path uses the public condition grouping overload for routed composite indexes, then converts keys to harness-only labels for validation output.<br/>
    /// </summary>
    /// <param name="condition">The completed condition used to select candidate identities.<br/></param>
    /// <param name="resolver">The condition index resolver.<br/></param>
    /// <param name="groupingIndex">The composite grouping key index.<br/></param>
    /// <returns>The measured composite counts and resource deltas.<br/></returns>
    private static GroupByCompositeCountProofMeasurement MeasureGroupByCompositeCounts(
        LibraDexConditionEndCondition condition,
        Func<string, IIndex> resolver,
        LibraDexRoutedCompositeIndex groupingIndex)
    {
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch watch = Stopwatch.StartNew();
        IReadOnlyDictionary<LibraDexCompositeKey, long> rows = condition.Groups(resolver).By<long>(groupingIndex).Counts();
        Dictionary<string, long> counts = new(rows.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<LibraDexCompositeKey, long> row in rows)
        {
            LibraDexCompositeKey equivalentKey = Key.Of(row.Key.Values[0].Value, row.Key.Values[1].Value);
            if (!rows.ContainsKey(equivalentKey))
            {
                throw new InvalidDataException("Composite grouped count dictionary did not use structural composite-key equality.");
            }

            counts.Add(CompositeProofKey(row.Key), row.Value);
        }

        watch.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        return new GroupByCompositeCountProofMeasurement(counts, watch.Elapsed, allocated);
    }

    /// <summary>
    /// Measures the production composite grouping metadata terminal.<br/>
    /// This proves grouped result metadata can use the same composite tuple source as grouped counts.<br/>
    /// </summary>
    /// <param name="condition">The completed condition used to select candidate identities.<br/></param>
    /// <param name="resolver">The condition index resolver.<br/></param>
    /// <param name="groupingIndex">The composite grouping key index.<br/></param>
    /// <returns>The measured composite metadata and resource deltas.<br/></returns>
    private static GroupByCompositeMetadataProofMeasurement MeasureGroupByCompositeMetadata(
        LibraDexConditionEndCondition condition,
        Func<string, IIndex> resolver,
        LibraDexRoutedCompositeIndex groupingIndex)
    {
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch watch = Stopwatch.StartNew();
        IReadOnlyList<LibraDexGroupMetadata<LibraDexCompositeKey, long>> rows = condition.Groups(resolver).By<long>(groupingIndex).Metadata();
        Dictionary<string, GroupByCompositeProofExpectedRow> metadata = new(rows.Count, StringComparer.Ordinal);
        foreach (LibraDexGroupMetadata<LibraDexCompositeKey, long> row in rows)
            metadata.Add(CompositeProofKey(row.Key), new GroupByCompositeProofExpectedRow(row.Count, row.FirstIdentity, row.LastIdentity));

        watch.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        return new GroupByCompositeMetadataProofMeasurement(metadata, watch.Elapsed, allocated);
    }

    /// <summary>
    /// Measures the current public grouping count terminal over a logical string grouping index.<br/>
    /// This is the production path for exact string grouped counts.<br/>
    /// </summary>
    /// <param name="condition">The completed condition used to select candidate identities.<br/></param>
    /// <param name="resolver">The condition index resolver.<br/></param>
    /// <param name="groupingIndex">The exact string grouping index.<br/></param>
    /// <returns>The measured counts and resource deltas.<br/></returns>
    private static GroupByStringCountProofMeasurement MeasureGroupByStringCounts(
        LibraDexConditionEndCondition condition,
        Func<string, IIndex> resolver,
        LibraDexStringScalar8Index groupingIndex)
    {
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch watch = Stopwatch.StartNew();
        IReadOnlyDictionary<string?, long> counts = condition.Groups(resolver).By(groupingIndex).Counts();
        watch.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        return new GroupByStringCountProofMeasurement(ToMutableStringDictionary(counts), watch.Elapsed, allocated);
    }

    /// <summary>
    /// Measures the current public folded-text grouping count terminal over a logical string grouping index.<br/>
    /// This is the production path for normalized folded string grouped counts.<br/>
    /// </summary>
    /// <param name="condition">The completed condition used to select candidate identities.<br/></param>
    /// <param name="resolver">The condition index resolver.<br/></param>
    /// <param name="groupingIndex">The string grouping index with a maintained folded-text projection.<br/></param>
    /// <returns>The measured counts and resource deltas.<br/></returns>
    private static GroupByStringCountProofMeasurement MeasureGroupByFoldedStringCounts(
        LibraDexConditionEndCondition condition,
        Func<string, IIndex> resolver,
        LibraDexStringScalar8Index groupingIndex)
    {
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch watch = Stopwatch.StartNew();
        IReadOnlyDictionary<string?, long> counts = condition.Groups(resolver).ByFoldedText(groupingIndex).Counts();
        watch.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        return new GroupByStringCountProofMeasurement(ToMutableStringDictionary(counts), watch.Elapsed, allocated);
    }

    /// <summary>
    /// Measures the current public sort-key grouping count terminal over a logical string grouping index.<br/>
    /// This is the production path for binary culture sort-key grouped counts.<br/>
    /// </summary>
    /// <param name="condition">The completed condition used to select candidate identities.<br/></param>
    /// <param name="resolver">The condition index resolver.<br/></param>
    /// <param name="groupingIndex">The string grouping index with a maintained sort-key projection.<br/></param>
    /// <returns>The measured counts and resource deltas.<br/></returns>
    private static GroupByStringCountProofMeasurement MeasureGroupBySortKeyStringCounts(
        LibraDexConditionEndCondition condition,
        Func<string, IIndex> resolver,
        LibraDexStringScalar8Index groupingIndex)
    {
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch watch = Stopwatch.StartNew();
        IReadOnlyDictionary<byte[], long> counts = condition.Groups(resolver).BySortKey(groupingIndex).Counts();
        watch.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        return new GroupByStringCountProofMeasurement(ToMutableByteKeyDictionary(counts), watch.Elapsed, allocated);
    }

    /// <summary>
    /// Measures exact string grouped metadata over the public grouping terminal.<br/>
    /// </summary>
    /// <param name="condition">The completed condition used to select candidate identities.<br/></param>
    /// <param name="resolver">The condition index resolver.<br/></param>
    /// <param name="groupingIndex">The exact string grouping index.<br/></param>
    /// <returns>The measured metadata and resource deltas.<br/></returns>
    private static GroupByStringMetadataProofMeasurement MeasureGroupByStringMetadata(
        LibraDexConditionEndCondition condition,
        Func<string, IIndex> resolver,
        LibraDexStringScalar8Index groupingIndex)
    {
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch watch = Stopwatch.StartNew();
        IReadOnlyList<LibraDexGroupMetadata<string?, ulong>> rows = condition.Groups(resolver).By(groupingIndex).Metadata();
        watch.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        return new GroupByStringMetadataProofMeasurement(ToMutableStringMetadata(rows), watch.Elapsed, allocated);
    }

    /// <summary>
    /// Measures folded-text string grouped metadata over the public grouping terminal.<br/>
    /// </summary>
    /// <param name="condition">The completed condition used to select candidate identities.<br/></param>
    /// <param name="resolver">The condition index resolver.<br/></param>
    /// <param name="groupingIndex">The string grouping index with a maintained folded-text projection.<br/></param>
    /// <returns>The measured metadata and resource deltas.<br/></returns>
    private static GroupByStringMetadataProofMeasurement MeasureGroupByFoldedStringMetadata(
        LibraDexConditionEndCondition condition,
        Func<string, IIndex> resolver,
        LibraDexStringScalar8Index groupingIndex)
    {
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch watch = Stopwatch.StartNew();
        IReadOnlyList<LibraDexGroupMetadata<string?, ulong>> rows = condition.Groups(resolver).ByFoldedText(groupingIndex).Metadata();
        watch.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        return new GroupByStringMetadataProofMeasurement(ToMutableStringMetadata(rows), watch.Elapsed, allocated);
    }

    /// <summary>
    /// Measures sort-key string grouped metadata over the public grouping terminal.<br/>
    /// </summary>
    /// <param name="condition">The completed condition used to select candidate identities.<br/></param>
    /// <param name="resolver">The condition index resolver.<br/></param>
    /// <param name="groupingIndex">The string grouping index with a maintained sort-key projection.<br/></param>
    /// <returns>The measured metadata and resource deltas.<br/></returns>
    private static GroupByStringMetadataProofMeasurement MeasureGroupBySortKeyStringMetadata(
        LibraDexConditionEndCondition condition,
        Func<string, IIndex> resolver,
        LibraDexStringScalar8Index groupingIndex)
    {
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch watch = Stopwatch.StartNew();
        IReadOnlyList<LibraDexGroupMetadata<byte[], ulong>> rows = condition.Groups(resolver).BySortKey(groupingIndex).Metadata();
        watch.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        return new GroupByStringMetadataProofMeasurement(ToMutableByteKeyMetadata(rows), watch.Elapsed, allocated);
    }

    /// <summary>
    /// Measures exact string row-reader grouping over the public no-member-list reader terminal.<br/>
    /// </summary>
    /// <param name="condition">The completed condition used to select candidate identities.<br/></param>
    /// <param name="resolver">The condition index resolver.<br/></param>
    /// <param name="groupingIndex">The exact string grouping index.<br/></param>
    /// <returns>The measured row-reader metadata and resource deltas.<br/></returns>
    private static GroupByStringMetadataProofMeasurement MeasureGroupByStringRowReader(
        LibraDexConditionEndCondition condition,
        Func<string, IIndex> resolver,
        LibraDexStringScalar8Index groupingIndex)
    {
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch watch = Stopwatch.StartNew();
        Dictionary<string, GroupByStringProofExpectedRow> rows = new(StringComparer.Ordinal);
        string? currentKey = null;
        string? currentLabel = null;
        long currentCount = 0;
        ulong firstIdentity = 0;
        ulong lastIdentity = 0;
        long expectedGroupOrdinal = -1;
        long expectedItemOrdinal = 0;

        using LibraDexGroupRowReader<string?, ulong> reader = condition.Groups(resolver).By(groupingIndex).OpenRowReader();
        while (reader.MoveNext())
        {
            if (reader.IsFirstInGroup)
            {
                if (currentLabel is not null)
                {
                    rows.Add(currentLabel, CreateStringProofExpectedRow(currentCount, firstIdentity, lastIdentity));
                }

                expectedGroupOrdinal++;
                expectedItemOrdinal = 0;
                currentKey = reader.CurrentKey;
                currentLabel = CreateStringProofKeyLabel(currentKey);
                currentCount = 0;
                firstIdentity = reader.CurrentItem;
            }

            if (currentLabel is null)
            {
                throw new InvalidDataException("String grouped row reader produced a row before reporting a group boundary.");
            }

            if (reader.GroupOrdinal != expectedGroupOrdinal || reader.ItemOrdinalInGroup != expectedItemOrdinal)
            {
                throw new InvalidDataException("String grouped row reader ordinal metadata did not match streamed row position.");
            }

            if (!EqualityComparer<string?>.Default.Equals(reader.CurrentKey, currentKey))
            {
                throw new InvalidDataException("String grouped row reader changed keys without reporting a group boundary.");
            }

            lastIdentity = reader.CurrentItem;
            currentCount++;
            expectedItemOrdinal++;
        }

        if (currentLabel is not null)
        {
            rows.Add(currentLabel, CreateStringProofExpectedRow(currentCount, firstIdentity, lastIdentity));
        }

        watch.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        return new GroupByStringMetadataProofMeasurement(rows, watch.Elapsed, allocated);
    }

    /// <summary>
    /// Measures exact string grouped aggregate results over the public aggregate terminal.<br/>
    /// </summary>
    /// <param name="condition">The completed condition used to select candidate identities.<br/></param>
    /// <param name="resolver">The condition index resolver.<br/></param>
    /// <param name="groupingIndex">The exact string grouping index.<br/></param>
    /// <returns>The measured aggregate rows and resource deltas.<br/></returns>
    private static GroupByStringAggregateProofMeasurement MeasureGroupByStringAggregates(
        LibraDexConditionEndCondition condition,
        Func<string, IIndex> resolver,
        LibraDexStringScalar8Index groupingIndex)
    {
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch watch = Stopwatch.StartNew();
        IReadOnlyDictionary<string?, GroupByAggregateProofRow> rows = condition.Groups(resolver).By(groupingIndex).Aggregate(
            new GroupByAggregateProofRow(0, long.MaxValue, long.MinValue, 0),
            static (row, identity) => AggregateStringProof(row, identity));
        watch.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        return new GroupByStringAggregateProofMeasurement(ToMutableStringAggregate(rows), watch.Elapsed, allocated);
    }

    /// <summary>
    /// Measures folded-text string grouped aggregate results over the public aggregate terminal.<br/>
    /// </summary>
    /// <param name="condition">The completed condition used to select candidate identities.<br/></param>
    /// <param name="resolver">The condition index resolver.<br/></param>
    /// <param name="groupingIndex">The string grouping index with a maintained folded-text projection.<br/></param>
    /// <returns>The measured aggregate rows and resource deltas.<br/></returns>
    private static GroupByStringAggregateProofMeasurement MeasureGroupByFoldedStringAggregates(
        LibraDexConditionEndCondition condition,
        Func<string, IIndex> resolver,
        LibraDexStringScalar8Index groupingIndex)
    {
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch watch = Stopwatch.StartNew();
        IReadOnlyDictionary<string?, GroupByAggregateProofRow> rows = condition.Groups(resolver).ByFoldedText(groupingIndex).Aggregate(
            new GroupByAggregateProofRow(0, long.MaxValue, long.MinValue, 0),
            static (row, identity) => AggregateStringProof(row, identity));
        watch.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        return new GroupByStringAggregateProofMeasurement(ToMutableStringAggregate(rows), watch.Elapsed, allocated);
    }

    /// <summary>
    /// Measures sort-key string grouped aggregate results over the public aggregate terminal.<br/>
    /// </summary>
    /// <param name="condition">The completed condition used to select candidate identities.<br/></param>
    /// <param name="resolver">The condition index resolver.<br/></param>
    /// <param name="groupingIndex">The string grouping index with a maintained sort-key projection.<br/></param>
    /// <returns>The measured aggregate rows and resource deltas.<br/></returns>
    private static GroupByStringAggregateProofMeasurement MeasureGroupBySortKeyStringAggregates(
        LibraDexConditionEndCondition condition,
        Func<string, IIndex> resolver,
        LibraDexStringScalar8Index groupingIndex)
    {
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch watch = Stopwatch.StartNew();
        IReadOnlyDictionary<byte[], GroupByAggregateProofRow> rows = condition.Groups(resolver).BySortKey(groupingIndex).Aggregate(
            new GroupByAggregateProofRow(0, long.MaxValue, long.MinValue, 0),
            static (row, identity) => AggregateStringProof(row, identity));
        watch.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        return new GroupByStringAggregateProofMeasurement(ToMutableByteKeyAggregate(rows), watch.Elapsed, allocated);
    }

    /// <summary>
    /// Measures the current public grouping count terminal.<br/>
    /// This is the production path that should choose the terminal-specific grouping strategy internally.<br/>
    /// </summary>
    /// <param name="condition">The completed condition used to select candidate identities.<br/></param>
    /// <param name="resolver">The condition index resolver.<br/></param>
    /// <param name="groupingIndex">The grouping key index.<br/></param>
    /// <returns>The measured counts and resource deltas.<br/></returns>
    private static GroupByExecutionProofMeasurement MeasureGroupByExecutionProofCurrent(
        LibraDexConditionEndCondition condition,
        Func<string, IIndex> resolver,
        LibraDexIndex<int, long> groupingIndex)
    {
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch watch = Stopwatch.StartNew();
        IReadOnlyDictionary<int, long> counts = condition.Groups(resolver).By(groupingIndex).Counts();
        watch.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        return new GroupByExecutionProofMeasurement(ToMutableDictionary(counts), watch.Elapsed, allocated);
    }

    /// <summary>
    /// Measures a streaming ordered-index grouping proof for count-only results.<br/>
    /// The candidate identity set is still materialized, but group member lists are not; adjacent equal grouping keys are counted while scanning ordered tuples.<br/>
    /// </summary>
    /// <param name="condition">The completed condition used to select candidate identities.<br/></param>
    /// <param name="resolver">The condition index resolver.<br/></param>
    /// <param name="groupingIndex">The grouping key index.<br/></param>
    /// <returns>The measured counts and resource deltas.<br/></returns>
    private static GroupByExecutionProofMeasurement MeasureGroupByExecutionProofStreaming(
        LibraDexConditionEndCondition condition,
        Func<string, IIndex> resolver,
        LibraDexIndex<int, long> groupingIndex)
    {
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch watch = Stopwatch.StartNew();
        HashSet<long> identities = new(condition.ToList<long>(resolver), LibraDexKeyEquality<long>.Comparer);
        Dictionary<int, long> counts = new();
        bool hasCurrent = false;
        int currentKey = 0;
        long currentCount = 0;

        foreach (LibraDexTuple<int, long> tuple in groupingIndex.IterateAllTuples())
        {
            if (!identities.Contains(tuple.Identity))
                continue;

            if (!hasCurrent)
            {
                hasCurrent = true;
                currentKey = tuple.Key;
                currentCount = 1;
                continue;
            }

            if (tuple.Key == currentKey)
            {
                currentCount++;
                continue;
            }

            counts.Add(currentKey, currentCount);
            currentKey = tuple.Key;
            currentCount = 1;
        }

        if (hasCurrent)
            counts.Add(currentKey, currentCount);

        watch.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        return new GroupByExecutionProofMeasurement(counts, watch.Elapsed, allocated);
    }

    /// <summary>
    /// Converts a read-only count dictionary to a mutable dictionary so validation can use one comparer shape.<br/>
    /// </summary>
    /// <param name="counts">The count dictionary to copy.<br/></param>
    /// <returns>A mutable dictionary with the same key/count pairs.<br/></returns>
    private static Dictionary<int, long> ToMutableDictionary(IReadOnlyDictionary<int, long> counts)
    {
        Dictionary<int, long> result = new(counts.Count);
        foreach (KeyValuePair<int, long> count in counts)
            result.Add(count.Key, count.Value);

        return result;
    }

    /// <summary>
    /// Converts a read-only nullable string count dictionary to a mutable ordinal label dictionary for proof validation.<br/>
    /// The label conversion keeps the harness from depending on ordinary `Dictionary&lt;string, TValue&gt;` null-key behavior while still validating LibraDex's null group.<br/>
    /// </summary>
    /// <param name="counts">The count dictionary to copy.<br/></param>
    /// <returns>A mutable ordinal dictionary with labeled key/count pairs.<br/></returns>
    private static Dictionary<string, long> ToMutableStringDictionary(IReadOnlyDictionary<string?, long> counts)
    {
        Dictionary<string, long> result = new(counts.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string?, long> count in counts)
            result.Add(CreateStringProofKeyLabel(count.Key), count.Value);

        return result;
    }

    /// <summary>
    /// Converts a read-only byte-array count dictionary to a mutable hex-label dictionary for proof validation.<br/>
    /// Byte-array grouped keys use LibraDex byte equality at runtime; the harness uses hex strings only to make expected rows easy to compare and print.<br/>
    /// </summary>
    /// <param name="counts">The count dictionary to copy.<br/></param>
    /// <returns>A mutable ordinal dictionary with hex key/count pairs.<br/></returns>
    private static Dictionary<string, long> ToMutableByteKeyDictionary(IReadOnlyDictionary<byte[], long> counts)
    {
        Dictionary<string, long> result = new(counts.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<byte[], long> count in counts)
            result.Add(Convert.ToHexString(count.Key), count.Value);

        return result;
    }

    /// <summary>
    /// Converts nullable string-key metadata rows to harness validation labels.<br/>
    /// </summary>
    /// <param name="rows">The metadata rows returned by the public grouped terminal.<br/></param>
    /// <returns>A mutable metadata dictionary keyed by harness labels.<br/></returns>
    private static Dictionary<string, GroupByStringProofExpectedRow> ToMutableStringMetadata(IReadOnlyList<LibraDexGroupMetadata<string?, ulong>> rows)
    {
        Dictionary<string, GroupByStringProofExpectedRow> result = new(rows.Count, StringComparer.Ordinal);
        foreach (LibraDexGroupMetadata<string?, ulong> row in rows)
            result.Add(CreateStringProofKeyLabel(row.Key), CreateStringProofExpectedRow(row.Count, row.FirstIdentity, row.LastIdentity));

        return result;
    }

    /// <summary>
    /// Converts byte-array metadata rows to harness validation labels.<br/>
    /// </summary>
    /// <param name="rows">The metadata rows returned by the public grouped terminal.<br/></param>
    /// <returns>A mutable metadata dictionary keyed by hex labels.<br/></returns>
    private static Dictionary<string, GroupByStringProofExpectedRow> ToMutableByteKeyMetadata(IReadOnlyList<LibraDexGroupMetadata<byte[], ulong>> rows)
    {
        Dictionary<string, GroupByStringProofExpectedRow> result = new(rows.Count, StringComparer.Ordinal);
        foreach (LibraDexGroupMetadata<byte[], ulong> row in rows)
            result.Add(Convert.ToHexString(row.Key), CreateStringProofExpectedRow(row.Count, row.FirstIdentity, row.LastIdentity));

        return result;
    }

    /// <summary>
    /// Converts nullable string-key aggregate rows to harness validation labels.<br/>
    /// </summary>
    /// <param name="rows">The aggregate rows returned by the public grouped terminal.<br/></param>
    /// <returns>A mutable aggregate dictionary keyed by harness labels.<br/></returns>
    private static Dictionary<string, GroupByAggregateProofRow> ToMutableStringAggregate(IReadOnlyDictionary<string?, GroupByAggregateProofRow> rows)
    {
        Dictionary<string, GroupByAggregateProofRow> result = new(rows.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string?, GroupByAggregateProofRow> row in rows)
            result.Add(CreateStringProofKeyLabel(row.Key), row.Value);

        return result;
    }

    /// <summary>
    /// Converts byte-array aggregate rows to harness validation labels.<br/>
    /// </summary>
    /// <param name="rows">The aggregate rows returned by the public grouped terminal.<br/></param>
    /// <returns>A mutable aggregate dictionary keyed by hex labels.<br/></returns>
    private static Dictionary<string, GroupByAggregateProofRow> ToMutableByteKeyAggregate(IReadOnlyDictionary<byte[], GroupByAggregateProofRow> rows)
    {
        Dictionary<string, GroupByAggregateProofRow> result = new(rows.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<byte[], GroupByAggregateProofRow> row in rows)
            result.Add(Convert.ToHexString(row.Key), row.Value);

        return result;
    }

    /// <summary>
    /// Validates that both grouping proof paths produce the same counts for every group key.<br/>
    /// </summary>
    /// <param name="expected">The expected count dictionary.<br/></param>
    /// <param name="actual">The actual count dictionary.<br/></param>
    private static void ValidateGroupByExecutionProof(IReadOnlyDictionary<int, long> expected, IReadOnlyDictionary<int, long> actual)
    {
        if (expected.Count != actual.Count)
            throw new InvalidDataException($"Group proof expected {expected.Count} groups but saw {actual.Count}.");

        foreach (KeyValuePair<int, long> count in expected)
        {
            if (!actual.TryGetValue(count.Key, out long actualCount) || actualCount != count.Value)
                throw new InvalidDataException($"Group proof key {count.Key} expected {count.Value} rows but saw {actualCount}.");
        }
    }

    /// <summary>
    /// Validates that both grouping metadata proof paths produce the same count and edge identities for every group key.<br/>
    /// </summary>
    /// <param name="expected">The expected metadata dictionary.<br/></param>
    /// <param name="actual">The actual metadata dictionary.<br/></param>
    private static void ValidateGroupByMetadataProof(
        IReadOnlyDictionary<int, GroupByMetadataProofRow> expected,
        IReadOnlyDictionary<int, GroupByMetadataProofRow> actual)
    {
        if (expected.Count != actual.Count)
            throw new InvalidDataException($"Group metadata proof expected {expected.Count} groups but saw {actual.Count}.");

        foreach (KeyValuePair<int, GroupByMetadataProofRow> row in expected)
        {
            if (!actual.TryGetValue(row.Key, out GroupByMetadataProofRow actualRow) ||
                actualRow.Count != row.Value.Count ||
                actualRow.FirstIdentity != row.Value.FirstIdentity ||
                actualRow.LastIdentity != row.Value.LastIdentity)
            {
                throw new InvalidDataException($"Group metadata proof key {row.Key} did not match legacy materialization.");
            }
        }
    }

    /// <summary>
    /// Validates that both grouping representative proof paths produce the same first and last identities for every group key.<br/>
    /// </summary>
    /// <param name="expected">The expected representative dictionary.<br/></param>
    /// <param name="actual">The actual representative dictionary.<br/></param>
    private static void ValidateGroupByRepresentativeProof(
        IReadOnlyDictionary<int, GroupByRepresentativeProofRow> expected,
        IReadOnlyDictionary<int, GroupByRepresentativeProofRow> actual)
    {
        if (expected.Count != actual.Count)
            throw new InvalidDataException($"Group representative proof expected {expected.Count} groups but saw {actual.Count}.");

        foreach (KeyValuePair<int, GroupByRepresentativeProofRow> row in expected)
        {
            if (!actual.TryGetValue(row.Key, out GroupByRepresentativeProofRow actualRow) ||
                actualRow.FirstIdentity != row.Value.FirstIdentity ||
                actualRow.LastIdentity != row.Value.LastIdentity)
            {
                throw new InvalidDataException($"Group representative proof key {row.Key} did not match legacy materialization.");
            }
        }
    }

    /// <summary>
    /// Validates that both grouped reader proof paths produce the same count and edge identities for every group key.<br/>
    /// </summary>
    /// <param name="expected">The expected grouped reader summary dictionary.<br/></param>
    /// <param name="actual">The actual grouped reader summary dictionary.<br/></param>
    private static void ValidateGroupByReaderProof(
        IReadOnlyDictionary<int, GroupByReaderProofRow> expected,
        IReadOnlyDictionary<int, GroupByReaderProofRow> actual)
    {
        if (expected.Count != actual.Count)
            throw new InvalidDataException($"Group reader proof expected {expected.Count} groups but saw {actual.Count}.");

        foreach (KeyValuePair<int, GroupByReaderProofRow> row in expected)
        {
            if (!actual.TryGetValue(row.Key, out GroupByReaderProofRow actualRow) ||
                actualRow.Count != row.Value.Count ||
                actualRow.FirstIdentity != row.Value.FirstIdentity ||
                actualRow.LastIdentity != row.Value.LastIdentity)
            {
                throw new InvalidDataException($"Group reader proof key {row.Key} did not match legacy materialization.");
            }
        }
    }

    /// <summary>
    /// Validates composite grouped counts against the deterministic seed model.<br/>
    /// </summary>
    /// <param name="expected">The expected active-row grouped counts.<br/></param>
    /// <param name="actual">The actual grouped counts returned by production composite grouping.<br/></param>
    private static void ValidateGroupByCompositeCounts(
        IReadOnlyDictionary<string, GroupByCompositeProofExpectedRow> expected,
        IReadOnlyDictionary<string, long> actual)
    {
        if (expected.Count != actual.Count)
            throw new InvalidDataException($"Composite group proof expected {expected.Count} groups but saw {actual.Count}.");

        foreach (KeyValuePair<string, GroupByCompositeProofExpectedRow> row in expected)
        {
            if (!actual.TryGetValue(row.Key, out long actualCount) || actualCount != row.Value.Count)
            {
                throw new InvalidDataException($"Composite group proof key {row.Key} count did not match expected seed data.");
            }
        }
    }

    /// <summary>
    /// Validates composite grouped metadata against the deterministic seed model.<br/>
    /// </summary>
    /// <param name="expected">The expected active-row grouped metadata.<br/></param>
    /// <param name="actual">The actual grouped metadata returned by production composite grouping.<br/></param>
    private static void ValidateGroupByCompositeMetadata(
        IReadOnlyDictionary<string, GroupByCompositeProofExpectedRow> expected,
        IReadOnlyDictionary<string, GroupByCompositeProofExpectedRow> actual)
    {
        if (expected.Count != actual.Count)
            throw new InvalidDataException($"Composite metadata proof expected {expected.Count} groups but saw {actual.Count}.");

        foreach (KeyValuePair<string, GroupByCompositeProofExpectedRow> row in expected)
        {
            if (!actual.TryGetValue(row.Key, out GroupByCompositeProofExpectedRow actualRow) ||
                actualRow.Count != row.Value.Count ||
                actualRow.FirstIdentity != row.Value.FirstIdentity ||
                actualRow.LastIdentity != row.Value.LastIdentity)
            {
                throw new InvalidDataException($"Composite metadata proof key {row.Key} did not match expected seed data.");
            }
        }
    }

    /// <summary>
    /// Validates exact string grouped counts against a deterministic seed model.<br/>
    /// </summary>
    /// <param name="expected">The expected exact string counts.<br/></param>
    /// <param name="actual">The actual grouped counts returned by production string grouping.<br/></param>
    private static void ValidateGroupByStringCounts(
        IReadOnlyDictionary<string, long> expected,
        IReadOnlyDictionary<string, long> actual)
    {
        if (expected.Count != actual.Count)
            throw new InvalidDataException($"String group proof expected {expected.Count} groups but saw {actual.Count}.");

        foreach (KeyValuePair<string, long> row in expected)
        {
            if (!actual.TryGetValue(row.Key, out long actualCount) || actualCount != row.Value)
            {
                throw new InvalidDataException($"String group proof key {row.Key} expected {row.Value} rows but saw {actualCount}.");
            }
        }
    }

    /// <summary>
    /// Validates string grouped metadata against the deterministic seed model.<br/>
    /// </summary>
    /// <param name="expected">The expected metadata rows.<br/></param>
    /// <param name="actual">The actual metadata rows returned by production string grouping.<br/></param>
    private static void ValidateGroupByStringMetadata(
        IReadOnlyDictionary<string, GroupByStringProofExpectedRow> expected,
        IReadOnlyDictionary<string, GroupByStringProofExpectedRow> actual)
    {
        if (expected.Count != actual.Count)
            throw new InvalidDataException($"String metadata proof expected {expected.Count} groups but saw {actual.Count}.");

        foreach (KeyValuePair<string, GroupByStringProofExpectedRow> row in expected)
        {
            if (!actual.TryGetValue(row.Key, out GroupByStringProofExpectedRow actualRow) ||
                actualRow.Count != row.Value.Count ||
                actualRow.FirstIdentity != row.Value.FirstIdentity ||
                actualRow.LastIdentity != row.Value.LastIdentity)
            {
                throw new InvalidDataException($"String metadata proof key {row.Key} did not match expected seed data.");
            }
        }
    }

    /// <summary>
    /// Validates string grouped aggregate rows against the deterministic seed model.<br/>
    /// </summary>
    /// <param name="expected">The expected metadata-plus-aggregate rows.</param>
    /// <param name="actual">The actual aggregate rows returned by production string grouping.</param>
    private static void ValidateGroupByStringAggregates(
        IReadOnlyDictionary<string, GroupByStringProofExpectedRow> expected,
        IReadOnlyDictionary<string, GroupByAggregateProofRow> actual)
    {
        if (expected.Count != actual.Count)
            throw new InvalidDataException($"String aggregate proof expected {expected.Count} groups but saw {actual.Count}.");

        foreach (KeyValuePair<string, GroupByStringProofExpectedRow> row in expected)
        {
            if (!actual.TryGetValue(row.Key, out GroupByAggregateProofRow actualRow) ||
                actualRow.Sum != row.Value.Sum ||
                actualRow.Min != row.Value.Min ||
                actualRow.Max != row.Value.Max ||
                actualRow.Custom != row.Value.Custom)
            {
                throw new InvalidDataException($"String aggregate proof key {row.Key} did not match expected seed data.");
            }
        }
    }

    /// <summary>
    /// Validates one promoted string key-state route through both condition materialization and grouped count execution.<br/>
    /// The grouped path is included because the original limitation was exposed while grouping by logical string keys that include null and empty states.<br/>
    /// </summary>
    /// <param name="resolver">The condition index resolver.<br/></param>
    /// <param name="nameIndex">The logical string grouping index.<br/></param>
    /// <param name="keyState">The null/empty predicate state to validate.<br/></param>
    /// <param name="expectedCount">The expected identity count.<br/></param>
    /// <param name="label">A diagnostic label for failure messages.<br/></param>
    private static void ValidateStringKeyStatePromotionCount(
        Func<string, IIndex> resolver,
        LibraDexStringScalar8Index nameIndex,
        NullKey keyState,
        int expectedCount,
        string label)
    {
        LibraDexConditionEndCondition condition = LibraDexCondition
            .ForGroup("string-key-state-promotion-proof")
            .Index("name")
            .AsString
            .EqualTo(keyState)
            .EndCondition;
        IReadOnlyList<ulong> identities = condition.ToList<ulong>(resolver);
        if (identities.Count != expectedCount)
        {
            throw new InvalidDataException($"String key-state promotion proof {label} expected {expectedCount} materialized rows but saw {identities.Count}.");
        }

        long groupedCount = 0;
        foreach (KeyValuePair<string?, long> row in condition.Groups(resolver).By(nameIndex).Counts())
        {
            groupedCount += row.Value;
        }

        if (groupedCount != expectedCount)
        {
            throw new InvalidDataException($"String key-state promotion proof {label} expected {expectedCount} grouped rows but saw {groupedCount}.");
        }
    }

    /// <summary>
    /// Validates one ordinary text key after moving an identity out of a promoted empty-string route.<br/>
    /// This proves promoted key-state mutation did not leave the facade unable to maintain ordinary exact string tuples in the same index.<br/>
    /// </summary>
    /// <param name="resolver">The condition index resolver.<br/></param>
    /// <param name="nameIndex">The logical string grouping index.<br/></param>
    /// <param name="key">The exact text key to validate.<br/></param>
    /// <param name="expectedCount">The expected identity count.<br/></param>
    /// <param name="label">A diagnostic label for failure messages.<br/></param>
    private static void ValidateStringKeyStateTextConditionCount(
        Func<string, IIndex> resolver,
        LibraDexStringScalar8Index nameIndex,
        string key,
        int expectedCount,
        string label)
    {
        LibraDexConditionEndCondition condition = LibraDexCondition
            .ForGroup("string-key-state-promotion-proof")
            .Index("name")
            .AsString
            .EqualTo(key)
            .EndCondition;
        IReadOnlyList<ulong> identities = condition.ToList<ulong>(resolver);
        if (identities.Count != expectedCount)
        {
            throw new InvalidDataException($"String key-state promotion proof {label} expected {expectedCount} materialized rows but saw {identities.Count}.");
        }

        long groupedCount = 0;
        foreach (KeyValuePair<string?, long> row in condition.Groups(resolver).By(nameIndex).Counts())
        {
            groupedCount += row.Value;
        }

        if (groupedCount != expectedCount)
        {
            throw new InvalidDataException($"String key-state promotion proof {label} expected {expectedCount} grouped rows but saw {groupedCount}.");
        }
    }

    /// <summary>
    /// Validates one promoted scalar-16 binary key-state route through condition materialization.<br/>
    /// The proof uses the public binary condition builder so the route is exercised the same way Abraxas-facing queries will reach it.<br/>
    /// </summary>
    /// <param name="resolver">The condition index resolver.<br/></param>
    /// <param name="keyState">The null/empty predicate state to validate.<br/></param>
    /// <param name="expectedCount">The expected identity count.<br/></param>
    /// <param name="label">A diagnostic label for failure messages.<br/></param>
    private static void ValidateScalar16KeyStatePromotionCount(
        Func<string, IIndex> resolver,
        NullKey keyState,
        int expectedCount,
        string label)
    {
        LibraDexConditionEndCondition condition = LibraDexCondition
            .ForGroup("scalar16-key-state-promotion-proof")
            .Index("hash")
            .AsBinary
            .EqualTo(keyState)
            .EndCondition;
        IReadOnlyList<Guid> identities = condition.ToList<Guid>(resolver);
        if (identities.Count != expectedCount)
        {
            throw new InvalidDataException($"Scalar-16 key-state promotion proof {label} expected {expectedCount} materialized rows but saw {identities.Count}.");
        }
    }

    /// <summary>
    /// Validates an ordinary fixed binary key after moving an identity out of a promoted empty route.<br/>
    /// This catches promotion bugs that leave the fixed-32/GUID facade unable to resume normal binary-key storage after key-state mutation.<br/>
    /// </summary>
    /// <param name="resolver">The condition index resolver.<br/></param>
    /// <param name="key">The fixed-32 key to validate.<br/></param>
    /// <param name="expectedCount">The expected identity count.<br/></param>
    /// <param name="label">A diagnostic label for failure messages.<br/></param>
    private static void ValidateScalar16BinaryConditionCount(
        Func<string, IIndex> resolver,
        byte[] key,
        int expectedCount,
        string label)
    {
        LibraDexConditionEndCondition condition = LibraDexCondition
            .ForGroup("scalar16-key-state-promotion-proof")
            .Index("hash")
            .AsBinary
            .EqualTo(key)
            .EndCondition;
        IReadOnlyList<Guid> identities = condition.ToList<Guid>(resolver);
        if (identities.Count != expectedCount)
        {
            throw new InvalidDataException($"Scalar-16 key-state promotion proof {label} expected {expectedCount} materialized rows but saw {identities.Count}.");
        }
    }

    /// <summary>
    /// Validates the fixed-N scalar-8 BigInteger scalar-null route through the public condition builder.<br/>
    /// This covers the same identity primitive path Abraxas grouping and filtering will use for optional BigInteger fields.<br/>
    /// </summary>
    /// <param name="resolver">The condition index resolver.<br/></param>
    /// <param name="expectedCount">The expected identity count.<br/></param>
    /// <param name="label">A diagnostic label for failure messages.<br/></param>
    private static void ValidateFixedNScalar8KeyStatePromotionCount(
        Func<string, IIndex> resolver,
        int expectedCount,
        string label)
    {
        LibraDexConditionEndCondition condition = LibraDexCondition
            .ForGroup("fixedn-key-state-promotion-proof")
            .Index("score8")
            .AsBigInt
            .EqualTo(ScalarNull.Null)
            .EndCondition;
        IReadOnlyList<long> identities = condition.ToList<long>(resolver, deduplication: IdentityDeduplication.Preserve);
        if (identities.Count != expectedCount)
        {
            throw new InvalidDataException($"Fixed-N scalar-8 key-state promotion proof {label} expected {expectedCount} materialized rows but saw {identities.Count}.");
        }

        long counted = condition.Count(resolver, IdentityDeduplication.Preserve);
        if (counted != expectedCount)
        {
            throw new InvalidDataException($"Fixed-N scalar-8 key-state promotion proof {label} expected {expectedCount} counted rows but saw {counted}.");
        }
    }

    /// <summary>
    /// Validates the fixed-N scalar-16 BigInteger scalar-null route through the public condition builder.<br/>
    /// This protects GUID identity BigInteger indexes from regressing to inline-only scalar-null route storage.<br/>
    /// </summary>
    /// <param name="resolver">The condition index resolver.<br/></param>
    /// <param name="expectedCount">The expected identity count.<br/></param>
    /// <param name="label">A diagnostic label for failure messages.<br/></param>
    private static void ValidateFixedNScalar16KeyStatePromotionCount(
        Func<string, IIndex> resolver,
        int expectedCount,
        string label)
    {
        LibraDexConditionEndCondition condition = LibraDexCondition
            .ForGroup("fixedn-key-state-promotion-proof")
            .Index("score16")
            .AsBigInt
            .EqualTo(ScalarNull.Null)
            .EndCondition;
        IReadOnlyList<Guid> identities = condition.ToList<Guid>(resolver, deduplication: IdentityDeduplication.Preserve);
        if (identities.Count != expectedCount)
        {
            throw new InvalidDataException($"Fixed-N scalar-16 key-state promotion proof {label} expected {expectedCount} materialized rows but saw {identities.Count}.");
        }

        long counted = condition.Count(resolver, IdentityDeduplication.Preserve);
        if (counted != expectedCount)
        {
            throw new InvalidDataException($"Fixed-N scalar-16 key-state promotion proof {label} expected {expectedCount} counted rows but saw {counted}.");
        }
    }

    /// <summary>
    /// Validates that `All` includes fixed-N scalar-null identities and ordinary fixed-key identities together.<br/>
    /// The expected count is supplied after any exact scalar-null deletions and normal-key inserts have been applied.<br/>
    /// </summary>
    /// <param name="resolver">The condition index resolver.<br/></param>
    /// <param name="indexName">The BigInteger index name to validate.<br/></param>
    /// <param name="expectedCount">The expected total identity count.<br/></param>
    /// <param name="label">A diagnostic label for failure messages.<br/></param>
    private static void ValidateFixedNAllCount(
        Func<string, IIndex> resolver,
        string indexName,
        int expectedCount,
        string label)
    {
        LibraDexConditionEndCondition condition = LibraDexCondition
            .ForGroup("fixedn-key-state-promotion-proof")
            .Index(indexName)
            .AsBigInt
            .All()
            .EndCondition;
        long counted = condition.Count(resolver, IdentityDeduplication.Preserve);
        if (counted != expectedCount)
        {
            throw new InvalidDataException($"Fixed-N key-state promotion proof {label} expected {expectedCount} all rows but saw {counted}.");
        }
    }

    /// <summary>
    /// Validates an ordinary fixed-N scalar-8 BigInteger key after scalar-null promotion and deletion.<br/>
    /// This catches route-state work that accidentally disrupts the normal fixed-key routed shelf path.<br/>
    /// </summary>
    /// <param name="resolver">The condition index resolver.<br/></param>
    /// <param name="expectedCount">The expected identity count.<br/></param>
    /// <param name="label">A diagnostic label for failure messages.<br/></param>
    private static void ValidateFixedNScalar8NormalCondition(
        Func<string, IIndex> resolver,
        BigInteger key,
        int expectedCount,
        string label)
    {
        LibraDexConditionEndCondition condition = LibraDexCondition
            .ForGroup("fixedn-key-state-promotion-proof")
            .Index("score8")
            .AsBigInt
            .EqualTo(key)
            .EndCondition;
        IReadOnlyList<long> identities = condition.ToList<long>(resolver, deduplication: IdentityDeduplication.Preserve);
        if (identities.Count != expectedCount)
        {
            throw new InvalidDataException($"Fixed-N scalar-8 normal-key proof {label} expected {expectedCount} rows but saw {identities.Count}.");
        }
    }

    /// <summary>
    /// Validates an ordinary fixed-N scalar-16 BigInteger key after scalar-null promotion and deletion.<br/>
    /// The exact GUID identity check proves the scalar-16 decode path remains intact after promoted route work.<br/>
    /// </summary>
    /// <param name="resolver">The condition index resolver.<br/></param>
    /// <param name="expectedIdentity">The GUID identity expected on the normal key.<br/></param>
    /// <param name="expectedCount">The expected identity count.<br/></param>
    /// <param name="label">A diagnostic label for failure messages.<br/></param>
    private static void ValidateFixedNScalar16NormalCondition(
        Func<string, IIndex> resolver,
        BigInteger key,
        Guid expectedIdentity,
        int expectedCount,
        string label)
    {
        LibraDexConditionEndCondition condition = LibraDexCondition
            .ForGroup("fixedn-key-state-promotion-proof")
            .Index("score16")
            .AsBigInt
            .EqualTo(key)
            .EndCondition;
        IReadOnlyList<Guid> identities = condition.ToList<Guid>(resolver, deduplication: IdentityDeduplication.Preserve);
        if (identities.Count != expectedCount || (expectedCount > 0 && identities[0] != expectedIdentity))
        {
            throw new InvalidDataException($"Fixed-N scalar-16 normal-key proof {label} expected {expectedCount} rows with identity {expectedIdentity} but saw {identities.Count}.");
        }
    }

    private static Guid CreateScalar16KeyStateProofGuid(int value)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteInt32BigEndian(bytes.Slice(12, sizeof(int)), value);
        return new Guid(bytes);
    }

    private static byte[] CreateScalar16KeyStateProofKey(int value)
    {
        byte[] key = new byte[32];
        BinaryPrimitives.WriteInt32BigEndian(key.AsSpan(28, sizeof(int)), value);
        return key;
    }

    /// <summary>
    /// Validates grouped aggregate terminals against the legacy materialized baseline.<br/>
    /// </summary>
    /// <param name="expected">The expected aggregate rows.</param>
    /// <param name="actual">The actual aggregate rows.</param>
    private static void ValidateGroupByAggregateProof(
        IReadOnlyDictionary<int, GroupByAggregateProofRow> expected,
        IReadOnlyDictionary<int, GroupByAggregateProofRow> actual)
    {
        if (expected.Count != actual.Count)
            throw new InvalidDataException($"Group aggregate proof expected {expected.Count} groups but saw {actual.Count}.");

        foreach (KeyValuePair<int, GroupByAggregateProofRow> row in expected)
        {
            if (!actual.TryGetValue(row.Key, out GroupByAggregateProofRow actualRow) ||
                actualRow.Sum != row.Value.Sum ||
                actualRow.Min != row.Value.Min ||
                actualRow.Max != row.Value.Max ||
                actualRow.Custom != row.Value.Custom)
            {
                throw new InvalidDataException($"Group aggregate proof key {row.Key} did not match legacy materialization.");
            }
        }
    }

    /// <summary>
    /// Validates the convenience aggregate terminals against the same legacy baseline without including their separate scans in the one-pass performance row.<br/>
    /// Multiple independent convenience terminal calls are correct but intentionally not presented as the HPC path for multi-aggregate projection.<br/>
    /// </summary>
    /// <param name="condition">The completed condition used to select candidate identities.<br/></param>
    /// <param name="resolver">The condition index resolver.<br/></param>
    /// <param name="groupingIndex">The grouping key index.<br/></param>
    /// <param name="expected">The expected aggregate rows.<br/></param>
    private static void ValidateGroupByAggregateConvenienceTerminals(
        LibraDexConditionEndCondition condition,
        Func<string, IIndex> resolver,
        LibraDexIndex<int, long> groupingIndex,
        IReadOnlyDictionary<int, GroupByAggregateProofRow> expected)
    {
        LibraDexConditionGroupQuery<int, long> groups = condition.Groups(resolver).By(groupingIndex);
        IReadOnlyDictionary<int, long> sums = groups.Sum(AggregateProofValue);
        IReadOnlyDictionary<int, long> mins = groups.Min(AggregateProofValue);
        IReadOnlyDictionary<int, long> maxes = groups.Max(AggregateProofValue);
        IReadOnlyDictionary<int, long> customValues = groups.Aggregate(0L, AggregateProofCustom);
        foreach (KeyValuePair<int, GroupByAggregateProofRow> row in expected)
        {
            if (!sums.TryGetValue(row.Key, out long sum) ||
                !mins.TryGetValue(row.Key, out long min) ||
                !maxes.TryGetValue(row.Key, out long max) ||
                !customValues.TryGetValue(row.Key, out long custom) ||
                sum != row.Value.Sum ||
                min != row.Value.Min ||
                max != row.Value.Max ||
                custom != row.Value.Custom)
            {
                throw new InvalidDataException($"Aggregate convenience terminal proof key {row.Key} did not match legacy materialization.");
            }
        }
    }

    /// <summary>
    /// Writes one measured grouping proof row to the console.<br/>
    /// </summary>
    /// <param name="name">The proof path name.<br/></param>
    /// <param name="measurement">The measured path result.<br/></param>
    /// <param name="itemCount">The total seeded row count used as the coarse byte-per-row denominator.<br/></param>
    private static void WriteGroupByExecutionProofRow(string name, GroupByExecutionProofMeasurement measurement, int itemCount)
    {
        long total = 0;
        foreach (long count in measurement.Counts.Values)
            total += count;

        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "{0} {1} {2} {3:F3} {4} {5:F2}",
            name,
            measurement.Counts.Count,
            total,
            measurement.Elapsed.TotalMilliseconds,
            measurement.AllocatedBytes,
            (double)measurement.AllocatedBytes / itemCount));
    }

    /// <summary>
    /// Writes one measured grouping metadata proof row to the console.<br/>
    /// </summary>
    /// <param name="name">The proof path name.<br/></param>
    /// <param name="measurement">The measured path result.<br/></param>
    /// <param name="itemCount">The total seeded row count used as the coarse byte-per-row denominator.<br/></param>
    private static void WriteGroupByMetadataProofRow(string name, GroupByMetadataProofMeasurement measurement, int itemCount)
    {
        long total = 0;
        foreach (GroupByMetadataProofRow row in measurement.Metadata.Values)
            total += row.Count;

        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "{0} {1} {2} {3:F3} {4} {5:F2}",
            name,
            measurement.Metadata.Count,
            total,
            measurement.Elapsed.TotalMilliseconds,
            measurement.AllocatedBytes,
            (double)measurement.AllocatedBytes / itemCount));
    }

    /// <summary>
    /// Writes one measured grouping representative proof row to the console.<br/>
    /// </summary>
    /// <param name="name">The proof path name.<br/></param>
    /// <param name="measurement">The measured path result.<br/></param>
    /// <param name="itemCount">The total seeded row count used as the coarse byte-per-row denominator.<br/></param>
    private static void WriteGroupByRepresentativeProofRow(string name, GroupByRepresentativeProofMeasurement measurement, int itemCount)
    {
        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "{0} {1} {2:F3} {3} {4:F2}",
            name,
            measurement.Representatives.Count,
            measurement.Elapsed.TotalMilliseconds,
            measurement.AllocatedBytes,
            (double)measurement.AllocatedBytes / itemCount));
    }

    /// <summary>
    /// Writes one measured grouped reader proof row to the console.<br/>
    /// </summary>
    /// <param name="name">The proof path name.<br/></param>
    /// <param name="measurement">The measured path result.<br/></param>
    /// <param name="itemCount">The total seeded row count used as the coarse byte-per-row denominator.<br/></param>
    private static void WriteGroupByReaderProofRow(string name, GroupByReaderProofMeasurement measurement, int itemCount)
    {
        long total = 0;
        foreach (GroupByReaderProofRow row in measurement.Groups.Values)
            total += row.Count;

        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "{0} {1} {2} {3:F3} {4} {5:F2}",
            name,
            measurement.Groups.Count,
            total,
            measurement.Elapsed.TotalMilliseconds,
            measurement.AllocatedBytes,
            (double)measurement.AllocatedBytes / itemCount));
    }

    /// <summary>
    /// Writes one measured composite grouping count proof row to the console.<br/>
    /// </summary>
    /// <param name="name">The proof path name.<br/></param>
    /// <param name="measurement">The measured path result.<br/></param>
    /// <param name="itemCount">The total seeded row count used as the coarse byte-per-row denominator.<br/></param>
    private static void WriteGroupByCompositeCountProofRow(string name, GroupByCompositeCountProofMeasurement measurement, int itemCount)
    {
        long total = 0;
        foreach (long count in measurement.Counts.Values)
            total += count;

        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "{0} {1} {2} {3:F3} {4} {5:F2}",
            name,
            measurement.Counts.Count,
            total,
            measurement.Elapsed.TotalMilliseconds,
            measurement.AllocatedBytes,
            (double)measurement.AllocatedBytes / itemCount));
    }

    /// <summary>
    /// Writes one measured composite grouping metadata proof row to the console.<br/>
    /// </summary>
    /// <param name="name">The proof path name.<br/></param>
    /// <param name="measurement">The measured path result.<br/></param>
    /// <param name="itemCount">The total seeded row count used as the coarse byte-per-row denominator.<br/></param>
    private static void WriteGroupByCompositeMetadataProofRow(string name, GroupByCompositeMetadataProofMeasurement measurement, int itemCount)
    {
        long total = 0;
        foreach (GroupByCompositeProofExpectedRow row in measurement.Metadata.Values)
            total += row.Count;

        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "{0} {1} {2} {3:F3} {4} {5:F2}",
            name,
            measurement.Metadata.Count,
            total,
            measurement.Elapsed.TotalMilliseconds,
            measurement.AllocatedBytes,
            (double)measurement.AllocatedBytes / itemCount));
    }

    /// <summary>
    /// Writes one measured string grouping count proof row to the console.<br/>
    /// </summary>
    /// <param name="name">The proof path name.<br/></param>
    /// <param name="measurement">The measured path result.<br/></param>
    /// <param name="itemCount">The total seeded row count used as the coarse byte-per-row denominator.<br/></param>
    private static void WriteGroupByStringCountProofRow(string name, GroupByStringCountProofMeasurement measurement, int itemCount)
    {
        long total = 0;
        foreach (long count in measurement.Counts.Values)
            total += count;

        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "{0} {1} {2} {3:F3} {4} {5:F2}",
            name,
            measurement.Counts.Count,
            total,
            measurement.Elapsed.TotalMilliseconds,
            measurement.AllocatedBytes,
            (double)measurement.AllocatedBytes / itemCount));
    }

    /// <summary>
    /// Writes one measured string grouping metadata proof row to the console.<br/>
    /// </summary>
    /// <param name="name">The proof path name.<br/></param>
    /// <param name="measurement">The measured path result.<br/></param>
    /// <param name="itemCount">The total seeded row count used as the coarse byte-per-row denominator.<br/></param>
    private static void WriteGroupByStringMetadataProofRow(string name, GroupByStringMetadataProofMeasurement measurement, int itemCount)
    {
        long total = 0;
        foreach (GroupByStringProofExpectedRow row in measurement.Metadata.Values)
            total += row.Count;

        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "{0} {1} {2} {3:F3} {4} {5:F2}",
            name,
            measurement.Metadata.Count,
            total,
            measurement.Elapsed.TotalMilliseconds,
            measurement.AllocatedBytes,
            (double)measurement.AllocatedBytes / itemCount));
    }

    /// <summary>
    /// Writes one measured string grouping aggregate proof row to the console.<br/>
    /// </summary>
    /// <param name="name">The proof path name.<br/></param>
    /// <param name="measurement">The measured path result.<br/></param>
    /// <param name="itemCount">The total seeded row count used as the coarse byte-per-row denominator.<br/></param>
    private static void WriteGroupByStringAggregateProofRow(string name, GroupByStringAggregateProofMeasurement measurement, int itemCount)
    {
        long total = 0;
        foreach (GroupByAggregateProofRow row in measurement.Rows.Values)
            total += row.Sum;

        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "{0} {1} {2} {3:F3} {4} {5:F2}",
            name,
            measurement.Rows.Count,
            total,
            measurement.Elapsed.TotalMilliseconds,
            measurement.AllocatedBytes,
            (double)measurement.AllocatedBytes / itemCount));
    }

    /// <summary>
    /// Writes one measured grouped aggregate proof row to the console.<br/>
    /// </summary>
    /// <param name="name">The proof path name.<br/></param>
    /// <param name="measurement">The measured path result.<br/></param>
    /// <param name="itemCount">The total seeded row count used as the coarse byte-per-row denominator.<br/></param>
    private static void WriteGroupByAggregateProofRow(string name, GroupByAggregateProofMeasurement measurement, int itemCount)
    {
        long total = 0;
        foreach (GroupByAggregateProofRow row in measurement.Rows.Values)
            total += row.Sum;

        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "{0} {1} {2} {3:F3} {4} {5:F2}",
            name,
            measurement.Rows.Count,
            total,
            measurement.Elapsed.TotalMilliseconds,
            measurement.AllocatedBytes,
            (double)measurement.AllocatedBytes / itemCount));
    }

    /// <summary>
    /// Derives a deterministic aggregate value from an identity for grouped aggregate proofing.<br/>
    /// </summary>
    /// <param name="identity">The LibraDex identity.</param>
    /// <returns>The deterministic proof value.</returns>
    private static long AggregateProofValue(long identity)
        => (identity * 17L) % 10_003L;

    /// <summary>
    /// Derives a deterministic aggregate value from an unsigned identity for string grouped aggregate proofing.<br/>
    /// </summary>
    /// <param name="identity">The LibraDex identity.</param>
    /// <returns>The deterministic proof value.</returns>
    private static long AggregateProofValue(ulong identity)
        => AggregateProofValue((long)identity);

    /// <summary>
    /// Applies a deterministic custom aggregate step for proofing the public `Aggregate` terminal.<br/>
    /// </summary>
    /// <param name="current">The current aggregate value.</param>
    /// <param name="identity">The next LibraDex identity.</param>
    /// <returns>The updated aggregate value.</returns>
    private static long AggregateProofCustom(long current, long identity)
        => current ^ ((identity << 7) ^ (identity >> 3));

    /// <summary>
    /// Applies a deterministic custom aggregate step for unsigned identity grouped aggregate proofing.<br/>
    /// </summary>
    /// <param name="current">The current aggregate value.</param>
    /// <param name="identity">The next LibraDex identity.</param>
    /// <returns>The updated aggregate value.</returns>
    private static long AggregateProofCustom(long current, ulong identity)
        => AggregateProofCustom(current, (long)identity);

    /// <summary>
    /// Converts one composite key to a harness-only validation label.<br/>
    /// Production grouping returns `LibraDexCompositeKey`; this delimiter exists only to keep proof dictionaries easy to compare and print.<br/>
    /// </summary>
    /// <param name="key">The composite key returned by production grouping.<br/></param>
    /// <returns>A stable validation label for the two-part proof key.<br/></returns>
    private static string CompositeProofKey(LibraDexCompositeKey key)
    {
        if (key.Count != 2)
            throw new InvalidDataException($"Composite proof expected two key parts but saw {key.Count}.");

        return CompositeProofKey(
            key.Values[0].Value as string ?? throw new InvalidDataException("Composite proof tenant key was not a string."),
            key.Values[1].Value as string ?? throw new InvalidDataException("Composite proof user key was not a string."));
    }

    /// <summary>
    /// Creates a harness-only validation label for a two-part composite proof key.<br/>
    /// </summary>
    /// <param name="tenant">The tenant key part.<br/></param>
    /// <param name="user">The user key part.<br/></param>
    /// <returns>A stable validation label.</returns>
    private static string CompositeProofKey(string tenant, string user)
        => string.Concat(tenant, "|", user);

    /// <summary>
    /// Creates an exact string grouping key for the string grouping proof.<br/>
    /// The uppercase variant intentionally remains a separate ordinal group from the lowercase variant.<br/>
    /// </summary>
    /// <param name="row">The seeded row ordinal.<br/></param>
    /// <param name="nameCount">The number of base name buckets.<br/></param>
    /// <returns>The exact proof grouping key.<br/></returns>
    private static string? CreateStringGroupProofKey(int row, int nameCount)
    {
        if (row % 4093 == 0)
        {
            return null;
        }

        if (row % 4091 == 0)
        {
            return string.Empty;
        }

        string key = string.Create(CultureInfo.InvariantCulture, $"name-{row % nameCount:D5}");
        return row % 17 == 0 ? key.ToUpperInvariant() : key;
    }

    /// <summary>
    /// Creates a stable harness label for a nullable string grouped key.<br/>
    /// </summary>
    /// <param name="key">The nullable string grouped key.<br/></param>
    /// <returns>A non-null validation label.</returns>
    private static string CreateStringProofKeyLabel(string? key)
        => key is null ? "<null>" : string.Concat("<text>", key);

    /// <summary>
    /// Creates the expected sort-key validation label for one exact string proof key.<br/>
    /// </summary>
    /// <param name="key">The exact string key, or null for the null-key bucket.<br/></param>
    /// <returns>A hex validation label for the maintained sort-key projection.</returns>
    private static string CreateStringProofSortKeyLabel(string? key)
        => key is null
            ? "00"
            : Convert.ToHexString(CultureInfo.InvariantCulture.CompareInfo.GetSortKey(key, CompareOptions.IgnoreCase).KeyData);

    /// <summary>
    /// Increments one exact string grouped count in a harness validation dictionary.<br/>
    /// </summary>
    /// <param name="counts">The count dictionary to update.<br/></param>
    /// <param name="key">The exact string group key.<br/></param>
    private static void IncrementStringProofCount(Dictionary<string, long> counts, string key)
    {
        if (counts.TryGetValue(key, out long count))
        {
            counts[key] = count + 1;
            return;
        }

        counts.Add(key, 1);
    }

    /// <summary>
    /// Adds one identity to a string grouped proof expected row.<br/>
    /// The row tracks both metadata edges and aggregate values so one seed pass can validate several grouped terminals.<br/>
    /// </summary>
    /// <param name="rows">The expected row dictionary to update.<br/></param>
    /// <param name="key">The harness validation key label.<br/></param>
    /// <param name="identity">The identity assigned to the group.<br/></param>
    private static void AddStringProofExpectedRow(Dictionary<string, GroupByStringProofExpectedRow> rows, string key, ulong identity)
    {
        long value = AggregateProofValue(identity);
        if (rows.TryGetValue(key, out GroupByStringProofExpectedRow row))
        {
            rows[key] = row with
            {
                Count = row.Count + 1,
                LastIdentity = identity,
                Sum = row.Sum + value,
                Min = Math.Min(row.Min, value),
                Max = Math.Max(row.Max, value),
                Custom = AggregateProofCustom(row.Custom, identity)
            };
            return;
        }

        rows.Add(key, new GroupByStringProofExpectedRow(1, identity, identity, value, value, value, AggregateProofCustom(0, identity)));
    }

    /// <summary>
    /// Creates a metadata-only proof row from count and edge identities.<br/>
    /// Aggregate fields are neutral because metadata and row-reader validation only compare count and edge identity fields.<br/>
    /// </summary>
    /// <param name="count">The grouped member count.<br/></param>
    /// <param name="firstIdentity">The first identity in group order.<br/></param>
    /// <param name="lastIdentity">The last identity in group order.<br/></param>
    /// <returns>A proof row containing metadata fields and neutral aggregate placeholders.</returns>
    private static GroupByStringProofExpectedRow CreateStringProofExpectedRow(long count, ulong firstIdentity, ulong lastIdentity)
        => new(count, firstIdentity, lastIdentity, 0, 0, 0, 0);

    /// <summary>
    /// Applies the string grouping aggregate proof projection to one identity.<br/>
    /// </summary>
    /// <param name="row">The current aggregate row.</param>
    /// <param name="identity">The next grouped identity.</param>
    /// <returns>The updated aggregate row.</returns>
    private static GroupByAggregateProofRow AggregateStringProof(GroupByAggregateProofRow row, ulong identity)
    {
        long value = AggregateProofValue(identity);
        return new GroupByAggregateProofRow(
            row.Sum + value,
            Math.Min(row.Min, value),
            Math.Max(row.Max, value),
            AggregateProofCustom(row.Custom, identity));
    }

    private readonly record struct GroupByExecutionProofMeasurement(
        Dictionary<int, long> Counts,
        TimeSpan Elapsed,
        long AllocatedBytes);

    private readonly record struct GroupByMetadataProofMeasurement(
        Dictionary<int, GroupByMetadataProofRow> Metadata,
        TimeSpan Elapsed,
        long AllocatedBytes);

    private readonly record struct GroupByMetadataProofRow(
        long Count,
        long FirstIdentity,
        long LastIdentity);

    /// <summary>
    /// Proves staged <c>GroupBy(...).First|Last.Return(...).EndCondition</c> and <c>GroupBy(...).Aggregate(...).Return(...).EndCondition</c> grammar through exact, Folded, and SortKey grouping.<br/>
    /// The proof covers filtered and unfiltered roots, distinct representatives, deterministic extrema ties, natural and explicit result shapes, bookmarks, mutation selection, raw identities, and adaptive reader Skip/Pull movement.<br/>
    /// </summary>
    /// <param name="args">Unused harness arguments.<br/></param>
    /// <returns>Zero when grouped selection and every producer return the same deterministic logical sequence.<br/></returns>
    private static int RunConditionResultLatestReaderSanity(string[] args)
    {
        _ = args;
        using Catalog catalog = Catalog.CreateMemory();
        CatalogIdentityGroupIndexes indexes = catalog.Indexes["events"];
        using LibraDexIndex<int, ulong> statusIndex = indexes["status"].Int32Keys<ulong>().Create();
        using LibraDexStringScalar8Index deviceIndex = indexes["deviceId"].StringKeys().Create(
            StringKeys.ExactFoldedAndSortKey);
        using LibraDexIndex<DateTime, ulong> timestampIndex = indexes["timeStamp"].Create<DateTime, ulong>();
        using LibraDexStringScalar8Index categoryIndex = indexes["category"].StringKeys().Create();
        using LibraDexIndex<int, ulong> deleteReturnedIndex = indexes["deleteReturned"].Int32Keys<ulong>().Create();
        using LibraDexIndex<int, ulong> deleteKeysIndex = indexes["deleteKeys"].Int32Keys<ulong>().Create();
        using LibraDexIndex<int, ulong> setIndex = indexes["setTarget"].Int32Keys<ulong>().Create();
        using LibraDexIndex<int, ulong> transformIndex = indexes["transformTarget"].Int32Keys<ulong>().Create();
        using LibraDexIndex<int, ulong> topWindowIndex = indexes["topWindowTarget"].Int32Keys<ulong>().Create();
        using LibraDexIndex<int, ulong> ordinaryWindowIndex = indexes["ordinaryWindowTarget"].Int32Keys<ulong>().Create();
        using LibraDexIndex<int, ulong> representativeTargetIndex = indexes["representativeTarget"].Int32Keys<ulong>().Create();
        using LibraDexIndex<int, ulong> ambiguousOrderIndex = indexes["ambiguousOrder"].Int32Keys<ulong>().Create();

        DateTime t100 = new(2026, 1, 1, 0, 1, 0, DateTimeKind.Utc);
        DateTime t120 = new(2026, 1, 1, 0, 1, 20, DateTimeKind.Utc);
        DateTime t200 = new(2026, 1, 1, 0, 2, 0, DateTimeKind.Utc);
        DateTime t300 = new(2026, 1, 1, 0, 3, 0, DateTimeKind.Utc);
        DateTime t900 = new(2026, 1, 1, 0, 9, 0, DateTimeKind.Utc);
        Add(1, "Device-A", t100, active: true);
        Add(2, "device-a", t200, active: true);
        Add(3, "Device-B", t900, active: false);
        Add(4, "Device-B", t120, active: true);
        Add(5, "Device-C", t300, active: true);
        Add(6, "Device-C", t300, active: true);
        _ = ambiguousOrderIndex.Insert(10, 1);
        _ = ambiguousOrderIndex.Insert(20, 1);

        LibraDexCondition<(long Identity, string Key1)> firstActivePerFoldedDevice = indexes
            .Where("status").AsInt32.EqualTo(1)
            .GroupBy("deviceId").AsString(SubIndexType.Folded)
            .First
            .Return<long, string>("deviceId")
            .EndCondition;
        LibraDexCondition<(long Identity, string Key1)> lastActivePerFoldedDevice = indexes
            .Where("status").AsInt32.EqualTo(1)
            .GroupBy("deviceId").AsString(SubIndexType.Folded)
            .Last
            .Return<long, string>("deviceId")
            .EndCondition;
        IReadOnlyList<(long Identity, string Key1)> firstActiveRows = indexes.Get(firstActivePerFoldedDevice);
        IReadOnlyList<(long Identity, string Key1)> lastActiveRows = indexes.Get(lastActivePerFoldedDevice);
        LibraDexConditionResultPlan representativePlan = firstActivePerFoldedDevice.Plan();
        if (!representativePlan.IsBlocking ||
            !string.Equals(representativePlan.Shape, "GroupBy.First.Identity+1Key.NaturalOrder", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Grouped representative result plan did not expose its blocking selection and return shape.");
        }
        if (!firstActiveRows.SequenceEqual(new[]
            {
                (1L, "device-a"),
                (4L, "device-b"),
                (5L, "device-c")
            }) ||
            !lastActiveRows.SequenceEqual(new[]
            {
                (2L, "device-a"),
                (4L, "device-b"),
                (6L, "device-c")
            }))
        {
            throw new InvalidOperationException("Condition-native First/Last did not select distinct representatives in folded grouping-index order.");
        }

        LibraDexCondition<LibraDexRepresentativeRow> naturalFirst = indexes
            .GroupBy("deviceId")
            .First
            .EndCondition;
        IReadOnlyList<LibraDexRepresentativeRow> naturalFirstRows = indexes.Get(naturalFirst);
        if (naturalFirstRows.Count != 4 ||
            !naturalFirstRows.Any(row => Equals(row.GroupKey, "Device-B") && Equals(row.Identity, 3UL)))
        {
            throw new InvalidOperationException("Unfiltered GroupBy.First did not expose the natural group-key/identity result.");
        }

        LibraDexCondition<long> firstActiveByNewestRepresentative = indexes
            .Where("status").AsInt32.EqualTo(1)
            .GroupBy("deviceId").AsString(SubIndexType.Folded)
            .First
            .Return<long>()
            .OrderByDescending("timeStamp")
            .Top(2)
            .EndCondition;
        if (!indexes.Get(firstActiveByNewestRepresentative).SequenceEqual(new long[] { 5, 4 }))
            throw new InvalidOperationException("Representative selection did not compose with named ordering and Top after Return.");

        if (!indexes.Iterate(lastActivePerFoldedDevice).SequenceEqual(lastActiveRows))
            throw new InvalidOperationException("Representative Get and Iterate producers returned different logical sequences.");
        using (LibraDexResultReader<(long Identity, string Key1)> representativeReader =
            indexes.OpenReader(firstActivePerFoldedDevice))
        {
            if (!representativeReader.Pull(2).SequenceEqual(firstActiveRows.Take(2)) ||
                !representativeReader.Next() ||
                representativeReader.Current != firstActiveRows[2] ||
                representativeReader.Next())
            {
                throw new InvalidOperationException("Representative OpenReader did not preserve Pull, Next, or cursor advancement.");
            }
        }

        LibraDexBookmark representativeBookmark;
        using (LibraDexResultReader<(long Identity, string Key1)> representativeReader =
            indexes.OpenReader(firstActivePerFoldedDevice))
        {
            if (!representativeReader.Next() || representativeReader.Current != firstActiveRows[0])
                throw new InvalidOperationException("Representative bookmark setup could not consume its first logical row.");
            representativeBookmark = representativeReader.Bookmark;
        }
        using (LibraDexResultReader<(long Identity, string Key1)> representativeReader =
            indexes.OpenReader(firstActivePerFoldedDevice, representativeBookmark))
        {
            if (!representativeReader.Pull(2).SequenceEqual(firstActiveRows.Skip(1)))
                throw new InvalidOperationException("Representative bookmark continuation did not resume at the next grouped winner.");
        }

        LibraDexIdentityMutationResult representativeMutation =
            indexes["representativeTarget"].SetKey(firstActivePerFoldedDevice, 6000);
        if (representativeMutation.ChangedCount != 3 ||
            !GetEqual("representativeTarget", 6000).SequenceEqual(new ulong[] { 1, 4, 5 }))
        {
            throw new InvalidOperationException("Mutation execution did not consume the representative condition's selected identities.");
        }

        LibraDexCondition<(long Identity, string Key1, DateTime Key2)> condition = indexes
            .Where("status")
            .AsInt32
            .EqualTo(1)
            .GroupBy("deviceId")
            .AsString(SubIndexType.Folded)
            .Aggregate("timeStamp", AggType.Max)
            .Return<long, string, DateTime>("deviceId", "timeStamp")
            .EndCondition;

        LibraDexConditionResultPlan plan = condition.Plan();
        if (!plan.IsBlocking || !string.Equals(plan.Shape, "GroupBy.Aggregate.Max.Identity+2Keys.NaturalOrder", StringComparison.Ordinal))
            throw new InvalidOperationException("Grouped aggregate result plan did not expose its blocking execution shape.");

        IReadOnlyList<(long Identity, string Key1, DateTime Key2)> all = indexes.Get(condition);
        ValidateRows(all, 0, 2, "device-a", t200);
        ValidateRows(all, 1, 4, "device-b", t120);
        ValidateRows(all, 2, 5, "device-c", t300);

        IReadOnlyList<(long Identity, string Key1, DateTime Key2)> page = indexes.Get(condition, skip: 1, take: 1);
        ValidateRows(page, 0, 4, "device-b", t120);

        (long Identity, string Key1, DateTime Key2)[] iterated = indexes.Iterate(condition).ToArray();
        if (!all.SequenceEqual(iterated))
            throw new InvalidOperationException("Grouped aggregate iterator differed from getter logical order.");

        using (LibraDexResultReader<(long Identity, string Key1, DateTime Key2)> reader = indexes.OpenReader(condition))
        {
            if (reader.Skip(1) != 1 || !reader.Next())
                throw new InvalidOperationException("Grouped aggregate reader could not skip the first result and inspect the second.");

            ValidateRow(reader.Current, 4, "device-b", t120);
            if (reader.Skip(0) != 0 || reader.Pull(0).Count != 0 || reader.Pull(Span<(long Identity, string Key1, DateTime Key2)>.Empty) != 0)
                throw new InvalidOperationException("Grouped aggregate reader zero-length movement was not a no-op.");

            ValidateRow(reader.Current, 4, "device-b", t120);
            IReadOnlyList<(long Identity, string Key1, DateTime Key2)> pulled = reader.Pull(1);
            ValidateRows(pulled, 0, 5, "device-c", t300);
            if (reader.Next())
                throw new InvalidOperationException("Grouped aggregate reader did not advance beyond its pulled range.");
        }

        using (LibraDexResultReader<(long Identity, string Key1, DateTime Key2)> reader = indexes.OpenReader(condition))
        {
            (long Identity, string Key1, DateTime Key2)[] destination = new (long, string, DateTime)[2];
            if (reader.Pull(destination.AsSpan()) != 2)
                throw new InvalidOperationException("Grouped aggregate span pull did not fill the requested result window.");

            ValidateRows(destination, 0, 2, "device-a", t200);
            ValidateRows(destination, 1, 4, "device-b", t120);
            if (!reader.Next())
                throw new InvalidOperationException("Grouped aggregate reader could not continue after a span pull.");

            ValidateRow(reader.Current, 5, "device-c", t300);
        }

        LibraDexCondition<LibraDexAggregateRow> natural = indexes
            .GroupBy("deviceId")
            .Aggregate("timeStamp", AggType.Max)
            .EndCondition;
        IReadOnlyList<LibraDexAggregateRow> naturalRows = indexes.Get(natural);
        if (naturalRows.Count != 4 || !naturalRows.Any(row => Equals(row.GroupKey, "Device-B") && Equals(row.AggregateValue, t900)))
            throw new InvalidOperationException("Unfiltered natural aggregate rows did not retain group and Max criteria.");

        LibraDexCondition<byte[]> rawIdentities = indexes
            .Where("status").AsInt32.EqualTo(1)
            .GroupBy("deviceId").AsString(SubIndexType.Folded)
            .Aggregate("timeStamp", AggType.Max)
            .Return()
            .EndCondition;
        IReadOnlyList<byte[]> raw = indexes.Get(rawIdentities);
        if (raw.Count != 3 || raw[0].Length != 8 || BinaryPrimitives.ReadUInt64BigEndian(raw[0]) != 2)
            throw new InvalidOperationException("Raw identity return did not preserve the encoded winning identity.");

        LibraDexCondition<(string Key1, DateTime Key2)> keysOnly = indexes
            .Where("status").AsInt32.EqualTo(1)
            .GroupBy("deviceId").AsString(SubIndexType.Folded)
            .Aggregate("timeStamp", AggType.Max)
            .ReturnKeys<string, DateTime>("deviceId", "timeStamp")
            .EndCondition;
        IReadOnlyList<(string Key1, DateTime Key2)> keyRows = indexes.Get(keysOnly);
        if (keyRows.Count != 3 || keyRows[0] != ("device-a", t200))
            throw new InvalidOperationException("Keys-only aggregate return did not map the selected criteria positionally.");

        LibraDexCondition<(long Identity, string Key1, DateTime Key2)> newestTwoDevices = indexes
            .Where("status").AsInt32.EqualTo(1)
            .GroupBy("deviceId").AsString(SubIndexType.Folded)
            .Aggregate("timeStamp", AggType.Max)
            .Return<long, string, DateTime>("deviceId", "timeStamp")
            .OrderByDescending()
            .Top(2)
            .EndCondition;
        IReadOnlyList<(long Identity, string Key1, DateTime Key2)> newestTwoDeviceRows = indexes.Get(newestTwoDevices);
        ValidateRows(newestTwoDeviceRows, 0, 5, "device-c", t300);
        ValidateRows(newestTwoDeviceRows, 1, 2, "device-a", t200);

        LibraDexIdentityMutationResult topWindowMutation = indexes["topWindowTarget"].SetKey(newestTwoDevices, 9000);
        if (topWindowMutation.ChangedCount != 2 ||
            !GetEqual("topWindowTarget", 9000).SequenceEqual(new ulong[] { 2, 5 }))
        {
            throw new InvalidOperationException("Mutation execution ignored the aggregate result's descending Top(2) selection window.");
        }

        LibraDexIdentityMutationResult deleteReturned = indexes["deleteReturned"].Delete(condition);
        LibraDexIdentityMutationResult deleteKeys = indexes["deleteKeys"].Delete(keysOnly);
        ulong[] deleteReturnedRemaining = GetAll("deleteReturned");
        ulong[] deleteKeysRemaining = GetAll("deleteKeys");
        if (deleteReturned.ChangedCount != 3 ||
            deleteKeys.ChangedCount != 3 ||
            !deleteReturnedRemaining.SequenceEqual(new ulong[] { 1, 3, 6 }) ||
            !deleteKeysRemaining.SequenceEqual(deleteReturnedRemaining))
        {
            throw new InvalidOperationException("Aggregate mutation selection changed when Return was replaced by ReturnKeys.");
        }

        LibraDexIdentityMutationResult setResult = indexes["setTarget"].SetKey(condition, 700);
        ulong[] setIdentities = GetEqual("setTarget", 700);
        if (setResult.ChangedCount != 3 || !setIdentities.SequenceEqual(new ulong[] { 2, 4, 5 }))
            throw new InvalidOperationException("Target-owned SetKey did not re-key exactly the grouped aggregate winners.");

        LibraDexIdentityMutationResult transformResult = indexes["transformTarget"].SetKeyUsing(
            keysOnly,
            oldKey => (int)oldKey + 1000);
        if (transformResult.ChangedCount != 3 ||
            !GetEqual("transformTarget", 1102).SequenceEqual(new ulong[] { 2 }) ||
            !GetEqual("transformTarget", 1104).SequenceEqual(new ulong[] { 4 }) ||
            !GetEqual("transformTarget", 1105).SequenceEqual(new ulong[] { 5 }))
        {
            throw new InvalidOperationException("Target-owned SetKeyUsing did not transform each selected tuple's old key.");
        }

        LibraDexIdentityMutationResult deleteAll = indexes["deleteReturned"].DeleteAll();
        if (deleteAll.ChangedCount != 3 || GetAll("deleteReturned").Length != 0)
            throw new InvalidOperationException("Target-owned DeleteAll did not remove every remaining tuple while retaining the index.");

        LibraDexCondition<long> earliestIdentities = indexes
            .Where("status").AsInt32.EqualTo(1)
            .GroupBy("deviceId").AsString(SubIndexType.Folded)
            .Aggregate("timeStamp", AggType.Min)
            .Return<long>()
            .EndCondition;
        IReadOnlyList<long> earliest = indexes.Get(earliestIdentities);
        if (!earliest.SequenceEqual(new long[] { 1, 4, 5 }))
            throw new InvalidOperationException("Folded Min grouping did not retain the earliest deterministic identity per device.");

        LibraDexCondition<long> sortKeyIdentities = indexes
            .Where("status").AsInt32.EqualTo(1)
            .GroupBy("deviceId").AsString(SubIndexType.SortKey)
            .Aggregate("timeStamp", AggType.Max)
            .Return<long>()
            .EndCondition;
        if (indexes.Get(sortKeyIdentities).Count != 3)
            throw new InvalidOperationException("SortKey grouping did not use the maintained binary grouping projection.");

        LibraDexCondition<LibraDexAggregateRow> topCategories = indexes
            .GroupBy("category")
            .Aggregate(AggType.Count)
            .OrderByDescending()
            .Top(2)
            .EndCondition;
        IReadOnlyList<LibraDexAggregateRow> categoryRows = indexes.Get(topCategories);
        if (categoryRows.Count != 2 ||
            !Equals(categoryRows[0].GroupKey, "A") || !Equals(categoryRows[0].AggregateValue, 3L) ||
            !Equals(categoryRows[1].GroupKey, "B") || !Equals(categoryRows[1].AggregateValue, 2L))
        {
            throw new InvalidOperationException("Grouped count descending Top(2) did not return the two largest category groups.");
        }

        LibraDexCondition<LibraDexAggregateRow> bottomCategory = indexes
            .GroupBy("category")
            .Aggregate(AggType.Count)
            .OrderByDescending()
            .Bottom(1)
            .EndCondition;
        IReadOnlyList<LibraDexAggregateRow> bottomCategoryRows = indexes.Get(bottomCategory);
        if (bottomCategoryRows.Count != 1 ||
            !Equals(bottomCategoryRows[0].GroupKey, "C") ||
            !Equals(bottomCategoryRows[0].AggregateValue, 1L))
        {
            throw new InvalidOperationException("Grouped count descending Bottom(1) did not retain the smallest final group.");
        }

        LibraDexCondition<DateTime> naturalTimestampTop = indexes
            .ReturnKeys<DateTime>("timeStamp")
            .Top(3)
            .EndCondition;
        DateTime[] naturalTimestampRows = indexes.Get(naturalTimestampTop).ToArray();
        if (!naturalTimestampRows.SequenceEqual(new[] { t100, t120, t200 }))
            throw new InvalidOperationException("Unfiltered ReturnKeys Top(3) did not preserve natural timestamp-index order.");

        LibraDexCondition<DateTime> newestTimestamps = indexes
            .ReturnKeys<DateTime>("timeStamp")
            .OrderByDescending()
            .Top(3)
            .EndCondition;
        DateTime[] newestTimestampRows = indexes.Get(newestTimestamps).ToArray();
        if (!newestTimestampRows.SequenceEqual(new[] { t900, t300, t300 }))
            throw new InvalidOperationException("Descending timestamp Top(3) did not use reverse index traversal semantics.");

        LibraDexCondition<DateTime> naturalTimestampBottom = indexes
            .ReturnKeys<DateTime>("timeStamp")
            .Bottom(2)
            .EndCondition;
        DateTime[] naturalTimestampBottomRows = indexes.Get(naturalTimestampBottom).ToArray();
        if (!naturalTimestampBottomRows.SequenceEqual(new[] { t300, t900 }))
            throw new InvalidOperationException("Natural timestamp Bottom(2) did not preserve ascending order inside the tail window.");

        LibraDexCondition<DateTime> filteredNewestTimestamps = indexes
            .Where("status").AsInt32.EqualTo(1)
            .ReturnKeys<DateTime>("timeStamp")
            .OrderByDescending()
            .Top(2)
            .EndCondition;
        DateTime[] filteredNewestRows = indexes.Get(filteredNewestTimestamps).ToArray();
        if (!filteredNewestRows.SequenceEqual(new[] { t300, t300 }))
            throw new InvalidOperationException("Filtered descending timestamp Top(2) did not apply selection before the aligned result traversal.");

        LibraDexCondition<long> newestActiveIdentities = indexes
            .Where("status").AsInt32.EqualTo(1)
            .Return<long>()
            .OrderByDescending("timeStamp")
            .Top(3)
            .EndCondition;
        long[] newestActiveRows = indexes.Get(newestActiveIdentities).ToArray();
        if (!newestActiveRows.SequenceEqual(new long[] { 6, 5, 2 }))
            throw new InvalidOperationException("Ordinary identity Return with named descending order did not follow reverse timestamp-index order.");

        LibraDexCondition<(long Identity, string Key1)> oldestActiveWithDevice = indexes
            .Where("status").AsInt32.EqualTo(1)
            .Return<long, string>("deviceId")
            .OrderBy("timeStamp")
            .Top(3)
            .EndCondition;
        (long Identity, string Key1)[] oldestActiveRows = indexes.Get(oldestActiveWithDevice).ToArray();
        if (!oldestActiveRows.SequenceEqual(new[]
            {
                (1L, "Device-A"),
                (4L, "Device-B"),
                (2L, "device-a")
            }))
        {
            throw new InvalidOperationException("Named ordering changed the ordinary identity/key return shape or its timestamp order.");
        }

        LibraDexCondition<long> lastTwoActive = indexes
            .Where("status").AsInt32.EqualTo(1)
            .Return<long>()
            .OrderBy("timeStamp")
            .Bottom(2)
            .EndCondition;
        if (!indexes.Get(lastTwoActive).SequenceEqual(new long[] { 5, 6 }))
            throw new InvalidOperationException("Ordinary Bottom(2) did not preserve ascending order inside the timestamp tail window.");

        long[] iteratedNewestActive = indexes.Iterate(newestActiveIdentities).ToArray();
        using (LibraDexResultReader<long> reader = indexes.OpenReader(newestActiveIdentities))
        {
            IReadOnlyList<long> pulled = reader.Pull(2);
            if (!iteratedNewestActive.SequenceEqual(newestActiveRows) ||
                !pulled.SequenceEqual(new long[] { 6, 5 }) ||
                !reader.Next() ||
                reader.Current != 2 ||
                reader.Next())
            {
                throw new InvalidOperationException("Ordinary Return Get, Iterate, and OpenReader producers did not preserve one logical sequence.");
            }
        }

        LibraDexCondition<byte[]> rawActiveIdentity = indexes
            .Where("status").AsInt32.EqualTo(1)
            .Return()
            .OrderBy("timeStamp")
            .Top(1)
            .EndCondition;
        byte[] rawActive = indexes.Get(rawActiveIdentity)[0];
        if (rawActive.Length != 8 || BinaryPrimitives.ReadUInt64BigEndian(rawActive) != 1)
            throw new InvalidOperationException("Ordinary raw identity Return did not use the order index's identity encoding.");

        LibraDexIdentityMutationResult ordinaryMutation = indexes["ordinaryWindowTarget"].SetKey(newestActiveIdentities, 8000);
        if (ordinaryMutation.ChangedCount != 3 ||
            !GetEqual("ordinaryWindowTarget", 8000).SequenceEqual(new ulong[] { 2, 5, 6 }))
        {
            throw new InvalidOperationException("Mutation execution did not honor an ordinary Return condition's named order and Top window.");
        }

        try
        {
            LibraDexCondition<long> ambiguousOrder = indexes
                .Where("status").AsInt32.EqualTo(1)
                .Return<long>()
                .OrderBy("ambiguousOrder")
                .EndCondition;
            _ = indexes.Get(ambiguousOrder);
            throw new InvalidOperationException("Scalar named ordering accepted one identity mapped to multiple order keys.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("maps to multiple keys", StringComparison.Ordinal))
        {
        }

        LibraDexConditionResultPlan ascendingPlan = indexes
            .ReturnKeys<DateTime>("timeStamp")
            .OrderByAscending()
            .EndCondition
            .Plan();
        if (ascendingPlan.IsBlocking || !ascendingPlan.Materialization.Contains("without result sorting", StringComparison.Ordinal))
            throw new InvalidOperationException("Explicit natural-order selection incorrectly advertised a blocking sort.");

        try
        {
            _ = indexes.ReturnKeys<DateTime>("timeStamp").Top(0);
            throw new InvalidOperationException("Top(0) did not reject its zero-sized result window.");
        }
        catch (ArgumentOutOfRangeException)
        {
        }

        LibraDexBookmark pageBookmark;
        using (LibraDexResultReader<(long Identity, string Key1)> reader = indexes.OpenReader(oldestActiveWithDevice))
        {
            IReadOnlyList<(long Identity, string Key1)> firstPage = reader.Pull(2);
            if (!firstPage.SequenceEqual(new[] { (1L, "Device-A"), (4L, "Device-B") }))
                throw new InvalidOperationException("Bookmark reader first page did not preserve the condition result order.");

            pageBookmark = reader.Bookmark;
            if (pageBookmark.ResultsConsumed != 2 ||
                !string.Equals(pageBookmark.Group, "events", StringComparison.Ordinal) ||
                pageBookmark.Indexes.Count != 3 ||
                !pageBookmark.Indexes.Any(index =>
                    string.Equals(index.Name, "status", StringComparison.Ordinal) &&
                    (index.Roles & LibraDexBookmarkIndexRole.Filter) != 0) ||
                !pageBookmark.Indexes.Any(index =>
                    string.Equals(index.Name, "deviceId", StringComparison.Ordinal) &&
                    (index.Roles & LibraDexBookmarkIndexRole.Return) != 0) ||
                !pageBookmark.Indexes.Any(index =>
                    string.Equals(index.Name, "timeStamp", StringComparison.Ordinal) &&
                    (index.Roles & LibraDexBookmarkIndexRole.Order) != 0))
            {
                throw new InvalidOperationException("Multi-index bookmark inspection did not retain condition-result index provenance.");
            }
        }

        using (LibraDexResultReader<(long Identity, string Key1)> reader = indexes.OpenReader(oldestActiveWithDevice, pageBookmark))
        {
            if (!reader.Next() || reader.Current != (2L, "device-a") || reader.Next())
                throw new InvalidOperationException("Generation-bound reader resume did not continue at the bookmark's next unread result.");
        }

        IReadOnlyList<(long Identity, string Key1)> detachedPage = indexes.Get(
            oldestActiveWithDevice,
            take: 1,
            bookmark: pageBookmark);
        if (detachedPage.Count != 1 || detachedPage[0] != (2L, "device-a"))
            throw new InvalidOperationException("Detached Get did not use the same condition bookmark as OpenReader.");

        LibraDexCondition<long> allActiveByTimestamp = indexes
            .Where("status").AsInt32.EqualTo(1)
            .Return<long>()
            .OrderBy("timeStamp")
            .EndCondition;
        LibraDexBookmark duplicateKeyBookmark;
        using (LibraDexResultReader<long> reader = indexes.OpenReader(allActiveByTimestamp))
        {
            if (!reader.Pull(4).SequenceEqual(new long[] { 1, 4, 2, 5 }))
                throw new InvalidOperationException("Bookmark duplicate-key setup did not stop on the first identity in the shared timestamp run.");

            duplicateKeyBookmark = reader.Bookmark;
        }

        using (LibraDexResultReader<long> reader = indexes.OpenReader(allActiveByTimestamp, duplicateKeyBookmark))
        {
            if (!reader.Next() || reader.Current != 6 || reader.Next())
                throw new InvalidOperationException("Bookmark seek did not find the exact identity inside a duplicate order-key run.");
        }

        int selectorCalls = 0;
        int valueCalls = 0;
        LibraDexCondition<long> deferredBookmarkCondition = LibraDexCondition.Group("events")
            .Where(
                () =>
                {
                    selectorCalls++;
                    return "status";
                },
                "selectedIndex")
            .AsInt32.EqualTo(
                () =>
                {
                    valueCalls++;
                    return 1;
                })
            .Return<long>()
            .OrderBy("timeStamp")
            .EndCondition;

        LibraDexBookmark deferredBookmark;
        using (LibraDexResultReader<long> reader = indexes.OpenReader(deferredBookmarkCondition))
        {
            if (selectorCalls != 1 || valueCalls != 1)
                throw new InvalidOperationException("Bookmark preparation did not materialize a deferred selector and value exactly once.");
            if (reader.Pull(2).Count != 2)
                throw new InvalidOperationException("Deferred bookmark reader did not produce its first page.");

            deferredBookmark = reader.Bookmark;
            _ = deferredBookmark.Condition.Shape;
            _ = deferredBookmark.Indexes[0].Name;
            if (selectorCalls != 1 || valueCalls != 1 ||
                !deferredBookmark.Condition.HasDeferredSelectors ||
                !deferredBookmark.Condition.HasDeferredValues ||
                !deferredBookmark.Indexes.Any(index =>
                    string.Equals(index.Name, "status", StringComparison.Ordinal) &&
                    string.Equals(index.SelectorName, "selectedIndex", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("Bookmark inspection invoked deferred code or lost resolved selector provenance.");
            }
        }

        using (LibraDexResultReader<long> reader = indexes.OpenReader(deferredBookmarkCondition, deferredBookmark))
        {
            if (selectorCalls != 2 || valueCalls != 2 || !reader.Next())
                throw new InvalidOperationException("Deferred condition resume did not materialize once and continue after its bookmark.");
        }

        LibraDexParameter<string> parameterizedIndex = LibraDexParameter.Create("status", "currentIndex");
        LibraDexParameter<int> parameterizedKey = LibraDexParameter.Create(1, "currentStatus");
        LibraDexConditionEndCondition parameterizedCondition = indexes
            .Where(parameterizedIndex).AsInt32.GreaterOrEqual(parameterizedKey)
            .AND.Where(parameterizedIndex).AsInt32.LessOrEqual(parameterizedKey)
            .EndCondition;
        int parameterResolverCalls = 0;
        IReadOnlyList<ulong> firstParameterizedIds = parameterizedCondition
            .Materialize(indexName =>
            {
                parameterResolverCalls++;
                parameterizedIndex.Value = "missing-after-snapshot";
                parameterizedKey.Value = 0;
                return indexes.Index(indexName);
            })
            .IDs
            .ToList<ulong>();
        if (parameterResolverCalls != 2 || !firstParameterizedIds.OrderBy(static value => value).SequenceEqual(new ulong[] { 1, 2, 4, 5, 6 }))
            throw new InvalidOperationException("Execution-time parameters were not fully snapshotted before index resolution began.");

        parameterizedIndex.Value = "status";
        parameterizedKey.Value = 0;
        IReadOnlyList<ulong> secondParameterizedIds = parameterizedCondition.Materialize(indexes.Index).IDs.ToList<ulong>();
        if (!secondParameterizedIds.SequenceEqual(new ulong[] { 3 }))
            throw new InvalidOperationException("A reusable parameterized condition did not observe values changed between executions.");

        using (Catalog otherCatalog = Catalog.CreateMemory())
        using (LibraDexIndex<int, ulong> wrongGroupIndex = otherCatalog.Indexes["other-events"]["status"].Int32Keys<ulong>().Create())
        {
            LibraDexParameter<IIndex> wrongGroupParameter = LibraDexParameter.Create<IIndex>(wrongGroupIndex, "selectedIndex");
            LibraDexConditionEndCondition wrongGroupCondition = indexes.Where(wrongGroupParameter).AsInt32.EqualTo(1).EndCondition;
            try
            {
                _ = wrongGroupCondition.Materialize(indexes.Index);
                throw new InvalidOperationException("An opened-index parameter crossed identity groups during materialization.");
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("not condition group", StringComparison.Ordinal))
            {
            }
        }

        LibraDexCondition<long> enumMembershipBookmarkCondition = indexes
            .Where("status").AsInt32.InSet(new[] { ProofStatus.One })
            .Return<long>()
            .OrderBy("timeStamp")
            .EndCondition;
        LibraDexBookmark enumMembershipBookmark;
        using (LibraDexResultReader<long> reader = indexes.OpenReader(enumMembershipBookmarkCondition))
        {
            if (!reader.Pull(2).SequenceEqual(new long[] { 1, 4 }))
                throw new InvalidOperationException("Enum membership bookmark setup did not preserve the materialized set result order.");

            enumMembershipBookmark = reader.Bookmark;
        }

        using (LibraDexResultReader<long> reader = indexes.OpenReader(enumMembershipBookmarkCondition, enumMembershipBookmark))
        {
            if (!reader.Next() || reader.Current != 2)
                throw new InvalidOperationException("Enum membership bookmark did not structurally match its freshly materialized scalar set.");
        }

        _ = statusIndex.Insert(1, 7);
        try
        {
            using LibraDexResultReader<(long Identity, string Key1)> stale = indexes.OpenReader(oldestActiveWithDevice, pageBookmark);
            throw new InvalidOperationException("Generation-bound bookmark accepted a changed catalog.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("catalog changed", StringComparison.Ordinal))
        {
        }

        using (LibraDexResultReader<(long Identity, string Key1)> live = indexes.OpenReader(
            oldestActiveWithDevice,
            pageBookmark,
            LibraDexBookmarkConsistency.LiveContinuation))
        {
            if (!live.Next() || live.Current != (2L, "device-a") || live.Next())
                throw new InvalidOperationException("Live bookmark continuation did not resume after the retained logical anchor.");
        }

        Console.WriteLine("condition-result-latest-reader-sanity ok");
        return 0;

        void Add(ulong identity, string device, DateTime timestamp, bool active)
        {
            _ = statusIndex.Insert(active ? 1 : 0, identity);
            _ = deviceIndex.Insert(device, identity);
            _ = timestampIndex.Insert(timestamp, identity);
            _ = categoryIndex.Insert(identity <= 3 ? "A" : identity <= 5 ? "B" : "C", identity);
            _ = deleteReturnedIndex.Insert(100 + (int)identity, identity);
            _ = deleteKeysIndex.Insert(100 + (int)identity, identity);
            _ = setIndex.Insert(100 + (int)identity, identity);
            _ = transformIndex.Insert(100 + (int)identity, identity);
            _ = topWindowIndex.Insert(100 + (int)identity, identity);
            _ = ordinaryWindowIndex.Insert(100 + (int)identity, identity);
            _ = representativeTargetIndex.Insert(100 + (int)identity, identity);
        }

        ulong[] GetAll(string indexName)
            => indexes.GetIdentities<ulong>(
                indexes.Where(indexName).AsInt32.All().EndCondition,
                IdentityResultOrdering.IdentityAscending,
                IdentityDeduplication.Preserve).ToArray();

        ulong[] GetEqual(string indexName, int key)
            => indexes.GetIdentities<ulong>(
                indexes.Where(indexName).AsInt32.EqualTo(key).EndCondition,
                IdentityResultOrdering.IdentityAscending,
                IdentityDeduplication.Preserve).ToArray();

        static void ValidateRows(
            IReadOnlyList<(long Identity, string Key1, DateTime Key2)> rows,
            int ordinal,
            long identity,
            string device,
            DateTime timestamp)
        {
            if (rows.Count <= ordinal)
                throw new InvalidOperationException($"Grouped aggregate result is missing ordinal {ordinal}.");

            ValidateRow(rows[ordinal], identity, device, timestamp);
        }

        static void ValidateRow(
            (long Identity, string Key1, DateTime Key2) row,
            long identity,
            string device,
            DateTime timestamp)
        {
            if (row.Identity != identity || !string.Equals(row.Key1, device, StringComparison.Ordinal) || row.Key2 != timestamp)
            {
                throw new InvalidOperationException(
                    $"Grouped aggregate mismatch: expected ({identity}, {device}, {timestamp:O}), actual ({row.Identity}, {row.Key1}, {row.Key2:O}).");
            }
        }
    }

    private readonly record struct GroupByRepresentativeProofMeasurement(
        Dictionary<int, GroupByRepresentativeProofRow> Representatives,
        TimeSpan Elapsed,
        long AllocatedBytes);

    private readonly record struct GroupByRepresentativeProofRow(
        long FirstIdentity,
        long LastIdentity);

    private readonly record struct GroupByReaderProofMeasurement(
        Dictionary<int, GroupByReaderProofRow> Groups,
        TimeSpan Elapsed,
        long AllocatedBytes);

    private readonly record struct GroupByReaderProofRow(
        long Count,
        long FirstIdentity,
        long LastIdentity);

    private readonly record struct GroupByAggregateProofMeasurement(
        Dictionary<int, GroupByAggregateProofRow> Rows,
        TimeSpan Elapsed,
        long AllocatedBytes);

    private readonly record struct GroupByAggregateProofRow(
        long Sum,
        long Min,
        long Max,
        long Custom);

    private readonly record struct GroupByCompositeCountProofMeasurement(
        Dictionary<string, long> Counts,
        TimeSpan Elapsed,
        long AllocatedBytes);

    private readonly record struct GroupByCompositeMetadataProofMeasurement(
        Dictionary<string, GroupByCompositeProofExpectedRow> Metadata,
        TimeSpan Elapsed,
        long AllocatedBytes);

    private readonly record struct GroupByStringCountProofMeasurement(
        Dictionary<string, long> Counts,
        TimeSpan Elapsed,
        long AllocatedBytes);

    private readonly record struct GroupByStringMetadataProofMeasurement(
        Dictionary<string, GroupByStringProofExpectedRow> Metadata,
        TimeSpan Elapsed,
        long AllocatedBytes);

    private readonly record struct GroupByStringAggregateProofMeasurement(
        Dictionary<string, GroupByAggregateProofRow> Rows,
        TimeSpan Elapsed,
        long AllocatedBytes);

    private readonly record struct GroupByStringProofSeed(
        Dictionary<string, long> ActiveCounts,
        Dictionary<string, long> FoldedCounts,
        Dictionary<string, long> SortKeyCounts,
        Dictionary<string, long> PrefixCounts,
        Dictionary<string, GroupByStringProofExpectedRow> ActiveRows,
        Dictionary<string, GroupByStringProofExpectedRow> FoldedRows,
        Dictionary<string, GroupByStringProofExpectedRow> SortKeyRows);

    private readonly record struct GroupByStringProofExpectedRow(
        long Count,
        ulong FirstIdentity,
        ulong LastIdentity,
        long Sum,
        long Min,
        long Max,
        long Custom);

    private readonly record struct GroupByCompositeProofExpectedRow(
        long Count,
        long FirstIdentity,
        long LastIdentity);
}
