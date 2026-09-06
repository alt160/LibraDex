using System.Diagnostics;
using LibraDex;

internal static partial class RawHarness
{
    /// <summary>
    /// Verifies typed final-result duplicate removal, first-appearance order, preserve semantics, and post-deduplication paging.<br/>
    /// The source intentionally repeats sparse and adjacent identities so the proof exercises logical overlap rather than a physical single-key route that can suppress deduplication structurally.<br/>
    /// </summary>
    /// <param name="args">Command arguments; this validation command accepts no options.<br/></param>
    /// <returns>Zero after all typed identity projection invariants pass.<br/></returns>
    private static int RunIdentityTypedDedupeSanity(string[] args)
    {
        _ = args;
        ulong[] source = [1UL, 2UL, 1UL, 3UL, 2UL, 4UL, 4UL];
        IIdentityCriterion criterion = CreateIdentityDedupeCriterion(
            new IdentityDedupeScenario("sanity", source.Length, 4),
            source);

        ulong[] distinct = criterion
            .IDsWith(deduplication: IdentityDeduplication.Distinct)
            .Iterate<ulong>()
            .ToArray();
        ValidateIdentityDedupeSequence("typed distinct", distinct, [1UL, 2UL, 3UL, 4UL]);

        ulong[] preserved = criterion
            .IDsWith(deduplication: IdentityDeduplication.Preserve)
            .Iterate<ulong>()
            .ToArray();
        ValidateIdentityDedupeSequence("typed preserve", preserved, source);

        IReadOnlyList<ulong> page = criterion
            .IDsWith(
                deduplication: IdentityDeduplication.Distinct,
                skip: 1,
                take: 2)
            .ToList<ulong>();
        ValidateIdentityDedupeSequence("typed distinct page", page, [2UL, 3UL]);

        object[] untyped = criterion
            .IDsWith(deduplication: IdentityDeduplication.Distinct)
            .Iterate()
            .ToArray();
        ValidateIdentityDedupeSequence("untyped compatibility", untyped.Select(static value => (ulong)value).ToArray(), [1UL, 2UL, 3UL, 4UL]);

        IIdentityCriterion left = CreateIdentityDedupeCriterion(
            new IdentityDedupeScenario("left", 4, 3),
            [3UL, 1UL, 2UL, 1UL]);
        IIdentityCriterion right = CreateIdentityDedupeCriterion(
            new IdentityDedupeScenario("right", 3, 3),
            [2UL, 4UL, 3UL]);
        ValidateIdentityDedupeSequence(
            "typed union",
            left.Or(right).IDsWith(deduplication: IdentityDeduplication.Distinct).Iterate<ulong>().ToArray(),
            [3UL, 1UL, 2UL, 4UL]);
        ValidateIdentityDedupeSequence(
            "typed intersection",
            left.And(right).IDsWith(deduplication: IdentityDeduplication.Distinct).Iterate<ulong>().ToArray(),
            [3UL, 2UL]);
        ValidateIdentityDedupeSequence(
            "typed difference",
            left.Except(right).IDsWith(deduplication: IdentityDeduplication.Distinct).Iterate<ulong>().ToArray(),
            [1UL]);
        ValidateIdentityDedupeSequence(
            "typed ascending identity order",
            left.IDsWith(ordering: IdentityResultOrdering.IdentityAscending, deduplication: IdentityDeduplication.Distinct).Iterate<ulong>().ToArray(),
            [1UL, 2UL, 3UL]);
        ValidateIdentityDedupeSequence(
            "typed descending identity order",
            left.IDsWith(ordering: IdentityResultOrdering.IdentityDescending, deduplication: IdentityDeduplication.Distinct).Iterate<ulong>().ToArray(),
            [3UL, 2UL, 1UL]);
        ValidateIdentityDedupeSequence(
            "typed complement",
            left.Not().IDsWith(deduplication: IdentityDeduplication.Distinct).Iterate<ulong>().ToArray(),
            Array.Empty<ulong>());

        Console.WriteLine("PASS identity typed dedupe sanity");
        Console.WriteLine("Typed leaf, union, intersection, difference, complement, identity ordering, distinct paging, preserve, and untyped compatibility passed.");
        return 0;
    }

    /// <summary>
    /// Compares the established object-shaped compatibility route with the fully typed physical-to-result route now used by Abraxas identity cursors.<br/>
    /// Both paths consume the same deterministic primitive source logic and preserve first-appearance order; only the compatibility route boxes each physical value.<br/>
    /// Managed-live measurements are captured while each populated iterator and its set remain alive; they are process-level approximations and are reported with elapsed time and total managed allocation.<br/>
    /// </summary>
    /// <param name="args">Command arguments; element one may specify total inputs and defaults to one million.<br/></param>
    /// <returns>Zero after sparse-duplicate and half-duplicate scenarios pass correctness and retained-memory gates.<br/></returns>
    private static int RunIdentityTypedDedupeProof(string[] args)
    {
        int inputCount = args.Length > 1
            ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture)
            : 1_000_000;
        if (inputCount < 4)
            throw new ArgumentOutOfRangeException(nameof(args), inputCount, "The typed identity dedupe proof requires at least four inputs.");

        IdentityDedupeScenario[] scenarios =
        [
            new("unique-plus-two", inputCount, inputCount - 2),
            new("half-repeated", inputCount & ~1, (inputCount & ~1) / 2)
        ];

        _ = MeasureTypedIdentityDedupe(new IdentityDedupeScenario("warmup", 4_096, 2_048));
        Console.WriteLine("Abraxas/LibraDex final-result identity dedupe proof");
        Console.WriteLine("Retained bytes are GC.GetTotalMemory deltas while the populated enumerator remains alive; treat them as process-level approximations.");
        foreach (IdentityDedupeScenario scenario in scenarios)
        {
            IdentityDedupeMeasurement objectRoute = MeasureObjectIdentityDedupe(scenario);
            IdentityDedupeMeasurement typedRoute = MeasureTypedIdentityDedupe(scenario);
            if (typedRoute.ManagedLiveBytes >= objectRoute.ManagedLiveBytes)
            {
                throw new InvalidOperationException(
                    $"Typed dedupe retained {typedRoute.ManagedLiveBytes:N0} bytes for '{scenario.Name}', which did not improve on the object route's {objectRoute.ManagedLiveBytes:N0} bytes.");
            }

            if (typedRoute.AllocatedBytes >= objectRoute.AllocatedBytes)
            {
                throw new InvalidOperationException(
                    $"Typed source/dedupe allocated {typedRoute.AllocatedBytes:N0} bytes for '{scenario.Name}', which did not improve on the object route's {objectRoute.AllocatedBytes:N0} bytes.");
            }

            Console.WriteLine();
            Console.WriteLine($"Scenario={scenario.Name} Inputs={scenario.InputCount:N0} Distinct={scenario.DistinctCount:N0} Duplicates={scenario.InputCount - scenario.DistinctCount:N0}");
            PrintIdentityDedupeMeasurement(objectRoute);
            PrintIdentityDedupeMeasurement(typedRoute);
            Console.WriteLine($"Managed-live reduction={objectRoute.ManagedLiveBytes - typedRoute.ManagedLiveBytes:N0} bytes ({FormatIdentityDedupeRatio(objectRoute.ManagedLiveBytes, typedRoute.ManagedLiveBytes):F2}x smaller)");
        }

        return 0;
    }

    /// <summary>
    /// Measures the non-generic compatibility projection that retains distinct identities as boxed objects.<br/>
    /// The enumerator is deliberately kept alive across the managed-live sample so its populated <see cref="HashSet{T}"/> remains rooted.<br/>
    /// </summary>
    /// <param name="scenario">The deterministic input and distinct cardinalities to execute.<br/></param>
    /// <returns>Correctness-gated timing and memory observations for the object route.<br/></returns>
    private static IdentityDedupeMeasurement MeasureObjectIdentityDedupe(IdentityDedupeScenario scenario)
    {
        PrepareIdentityDedupeMeasurement(out long baselineManagedBytes, out long allocatedBefore);
        IIdentityCriterionProjection projection = CreateIdentityDedupeProjection(scenario);
        using IEnumerator<object> enumerator = projection.Iterate().GetEnumerator();
        Stopwatch elapsed = Stopwatch.StartNew();
        ValidateObjectIdentityDedupeEnumerator(enumerator, scenario.DistinctCount);
        elapsed.Stop();
        long managedLiveBytes = Math.Max(0, GC.GetTotalMemory(forceFullCollection: true) - baselineManagedBytes);
        GC.KeepAlive(enumerator);
        elapsed.Start();
        if (enumerator.MoveNext())
            throw new InvalidOperationException($"Object dedupe emitted an identity after the expected {scenario.DistinctCount:N0} distinct values.");
        elapsed.Stop();
        return new IdentityDedupeMeasurement(
            "HashSet<object> compatibility",
            elapsed.Elapsed,
            GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore,
            managedLiveBytes,
            scenario.DistinctCount);
    }

    /// <summary>
    /// Measures the generic projection used by Abraxas, which retains distinct identities directly as <see cref="UInt64"/> values.<br/>
    /// The physical test executor emits <see cref="UInt64"/> directly through the typed primitive contract, proving that boxes are not merely made collectible but are never created on this route.<br/>
    /// </summary>
    /// <param name="scenario">The deterministic input and distinct cardinalities to execute.<br/></param>
    /// <returns>Correctness-gated timing and memory observations for the typed route.<br/></returns>
    private static IdentityDedupeMeasurement MeasureTypedIdentityDedupe(IdentityDedupeScenario scenario)
    {
        PrepareIdentityDedupeMeasurement(out long baselineManagedBytes, out long allocatedBefore);
        IIdentityCriterionProjection projection = CreateIdentityDedupeProjection(scenario);
        using IEnumerator<ulong> enumerator = projection.Iterate<ulong>().GetEnumerator();
        Stopwatch elapsed = Stopwatch.StartNew();
        ValidateTypedIdentityDedupeEnumerator(enumerator, scenario.DistinctCount);
        elapsed.Stop();
        long managedLiveBytes = Math.Max(0, GC.GetTotalMemory(forceFullCollection: true) - baselineManagedBytes);
        GC.KeepAlive(enumerator);
        elapsed.Start();
        if (enumerator.MoveNext())
            throw new InvalidOperationException($"Typed dedupe emitted an identity after the expected {scenario.DistinctCount:N0} distinct values.");
        elapsed.Stop();
        return new IdentityDedupeMeasurement(
            "HashSet<ulong> Abraxas route",
            elapsed.Elapsed,
            GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore,
            managedLiveBytes,
            scenario.DistinctCount);
    }

    /// <summary>
    /// Creates a distinct, plan-natural projection over one deterministic primitive executor that exposes both compatibility-object and typed identity routes.<br/>
    /// The index declares multiple keys per identity so the physical multiplicity planner correctly retains final-result deduplication for the repeated source values.<br/>
    /// </summary>
    /// <param name="scenario">The input and distinct cardinalities used by the source factory.<br/></param>
    /// <returns>A reusable identity projection that opens a fresh source iterator for each execution.<br/></returns>
    private static IIdentityCriterionProjection CreateIdentityDedupeProjection(IdentityDedupeScenario scenario)
    {
        IIdentityCriterion criterion = CreateIdentityDedupeCriterion(scenario);
        return criterion.IDsWith(deduplication: IdentityDeduplication.Distinct);
    }

    /// <summary>
    /// Creates one primitive leaf backed by the dual object/typed identity executor used by the allocation proof.<br/>
    /// An optional fixed sequence supports focused semantic checks, while performance scenarios generate values lazily from their cardinality contract.<br/>
    /// </summary>
    /// <param name="scenario">The deterministic input and distinct cardinalities represented by the executor.<br/></param>
    /// <param name="fixedValues">Optional exact values for focused ordering and paging validation.<br/></param>
    /// <returns>An executable all-identities criterion over the test primitive.<br/></returns>
    private static IIdentityCriterion CreateIdentityDedupeCriterion(
        IdentityDedupeScenario scenario,
        IReadOnlyList<ulong>? fixedValues = null)
    {
        var index = new IdentityTypedDedupeIndex(scenario, fixedValues);
        return LibraDexIdentityCriterion.Leaf(
            index,
            LibraDexCriteriaKind.All,
            new LibraDexQueryDiagnostics(LibraDexExecutionKind.FastPath));
    }

    /// <summary>
    /// Produces ascending first appearances followed by deterministic repeated identities without retaining a source array.<br/>
    /// For a distinct cardinality two below the input count only identities one and two repeat; at half cardinality every identity repeats exactly once.<br/>
    /// </summary>
    /// <param name="inputCount">The total number of physical source occurrences.<br/></param>
    /// <param name="distinctCount">The number of distinct identities in the stream.<br/></param>
    /// <returns>A lazy typed UInt64 identity stream.<br/></returns>
    private static IEnumerable<ulong> EnumerateIdentityDedupeValues(int inputCount, int distinctCount)
    {
        for (int i = 0; i < inputCount; i++)
        {
            yield return checked((ulong)((i % distinctCount) + 1));
        }
    }

    /// <summary>
    /// Boxes a typed UInt64 identity sequence for the explicit object-compatibility executor.<br/>
    /// </summary>
    /// <param name="identities">The identities to expose through the internal object criterion stream.<br/></param>
    /// <returns>A lazy boxed identity stream.<br/></returns>
    private static IEnumerable<object> BoxIdentityDedupeValues(IEnumerable<ulong> identities)
    {
        foreach (ulong identity in identities)
        {
            yield return identity;
        }
    }

    /// <summary>
    /// Forces a stable collection boundary before one process-local memory sample.<br/>
    /// Allocation uses the process-wide counter because the iterator implementation is required to remain valid even if later execution introduces asynchronous or worker-thread source stages.<br/>
    /// </summary>
    /// <param name="baselineManagedBytes">Receives managed bytes live after the forced collection.<br/></param>
    /// <param name="allocatedBefore">Receives the total managed allocation counter at the same boundary.<br/></param>
    private static void PrepareIdentityDedupeMeasurement(out long baselineManagedBytes, out long allocatedBefore)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        baselineManagedBytes = GC.GetTotalMemory(forceFullCollection: true);
        allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
    }

    /// <summary>
    /// Validates ascending first-appearance order from the object compatibility iterator.<br/>
    /// The iterator remains positioned on its final expected output so callers can sample the live retained set before exhausting and disposing it.<br/>
    /// </summary>
    /// <param name="enumerator">The object identity enumerator to consume.<br/></param>
    /// <param name="distinctCount">The expected number of projected identities.<br/></param>
    private static void ValidateObjectIdentityDedupeEnumerator(IEnumerator<object> enumerator, int distinctCount)
    {
        for (int i = 0; i < distinctCount; i++)
        {
            if (!enumerator.MoveNext() || enumerator.Current is not ulong identity || identity != checked((ulong)i + 1UL))
                throw new InvalidOperationException($"Object dedupe first-appearance order failed at ordinal {i:N0}.");
        }
    }

    /// <summary>
    /// Validates ascending first-appearance order from the typed Abraxas iterator.<br/>
    /// The iterator remains positioned on its final expected output so callers can sample the live retained set before exhausting and disposing it.<br/>
    /// </summary>
    /// <param name="enumerator">The typed identity enumerator to consume.<br/></param>
    /// <param name="distinctCount">The expected number of projected identities.<br/></param>
    private static void ValidateTypedIdentityDedupeEnumerator(IEnumerator<ulong> enumerator, int distinctCount)
    {
        for (int i = 0; i < distinctCount; i++)
        {
            if (!enumerator.MoveNext() || enumerator.Current != checked((ulong)i + 1UL))
                throw new InvalidOperationException($"Typed dedupe first-appearance order failed at ordinal {i:N0}.");
        }
    }

    /// <summary>
    /// Verifies exact UInt64 sequence equality for focused duplicate and paging checks.<br/>
    /// </summary>
    /// <param name="label">The semantic route named in any failure.<br/></param>
    /// <param name="actual">The identities produced by the route under test.<br/></param>
    /// <param name="expected">The required identities in exact order.<br/></param>
    private static void ValidateIdentityDedupeSequence(string label, IReadOnlyList<ulong> actual, IReadOnlyList<ulong> expected)
    {
        if (actual.Count != expected.Count)
            throw new InvalidOperationException($"{label} returned {actual.Count:N0} identities; expected {expected.Count:N0}.");

        for (int i = 0; i < expected.Count; i++)
        {
            if (actual[i] != expected[i])
                throw new InvalidOperationException($"{label} returned {actual[i]} at ordinal {i}; expected {expected[i]}.");
        }
    }

    /// <summary>
    /// Writes one identity dedupe measurement with throughput and memory attribution.<br/>
    /// </summary>
    /// <param name="measurement">The correctness-gated route measurement to print.<br/></param>
    private static void PrintIdentityDedupeMeasurement(IdentityDedupeMeasurement measurement)
    {
        double throughput = measurement.Elapsed.TotalSeconds <= 0
            ? 0
            : measurement.DistinctCount / measurement.Elapsed.TotalSeconds;
        Console.WriteLine(
            $"{measurement.Route}: elapsed={measurement.Elapsed} distinct-throughput={throughput:N0}/s allocated={measurement.AllocatedBytes:N0} managed-live={measurement.ManagedLiveBytes:N0}");
    }

    /// <summary>
    /// Computes the object-to-typed managed-live ratio without dividing by zero.<br/>
    /// </summary>
    /// <param name="objectBytes">The object route's managed-live bytes.<br/></param>
    /// <param name="typedBytes">The typed route's managed-live bytes.<br/></param>
    /// <returns>The factor by which the object route is larger than the typed route.<br/></returns>
    private static double FormatIdentityDedupeRatio(long objectBytes, long typedBytes)
        => typedBytes <= 0 ? double.PositiveInfinity : (double)objectBytes / typedBytes;

    private readonly record struct IdentityDedupeScenario(string Name, int InputCount, int DistinctCount);

    private readonly record struct IdentityDedupeMeasurement(
        string Route,
        TimeSpan Elapsed,
        long AllocatedBytes,
        long ManagedLiveBytes,
        int DistinctCount);

    /// <summary>
    /// Supplies identical deterministic identities through legacy object and new typed primitive contracts.<br/>
    /// The type intentionally declares multiple keys per identity so the planner must retain a result-level distinct set and the harness can measure both source and set representation together.<br/>
    /// </summary>
    private sealed class IdentityTypedDedupeIndex : IIndex, IIdentityPrimitiveExecutor, IIdentityPrimitiveExecutor<ulong>
    {
        private readonly IdentityDedupeScenario scenario;
        private readonly IReadOnlyList<ulong>? fixedValues;

        /// <summary>
        /// Initializes the deterministic primitive executor used by the semantic and allocation proofs.<br/>
        /// </summary>
        /// <param name="scenario">The generated input and distinct cardinalities.<br/></param>
        /// <param name="fixedValues">Optional exact values that replace generated scenario values.<br/></param>
        internal IdentityTypedDedupeIndex(IdentityDedupeScenario scenario, IReadOnlyList<ulong>? fixedValues)
        {
            this.scenario = scenario;
            this.fixedValues = fixedValues;
        }

        public Catalog Catalog => null!;

        public string Name => "identity-typed-dedupe-proof";

        public string Group => "identity-typed-dedupe-proof";

        public Type KeyType => typeof(ulong);

        public Type IdentityType => typeof(ulong);

        public IndexKeys KeyContract => IndexKeys.NonUnique;

        public IdentityKeyMultiplicity IdentityKeyMultiplicity => IdentityKeyMultiplicity.MultipleKeysPerIdentity;

        public CatalogIndexKeyFamily KeyFamily => CatalogIndexKeyFamily.Scalar;

        public CatalogIndexIdentityFamily IdentityFamily => CatalogIndexIdentityFamily.Scalar;

        public LibraDexIndexShapeSpec? LogicalShape => null;

        /// <summary>
        /// Rejects mutation because this harness-only executor is a deterministic read source.<br/>
        /// </summary>
        /// <param name="key">The unused runtime key.<br/></param>
        /// <param name="identity">The unused runtime identity.<br/></param>
        /// <returns>This method never returns.<br/></returns>
        public LibraDexGenericInsertResult Insert(object? key, object identity)
            => throw new NotSupportedException("The typed dedupe proof index is read-only.");

        /// <summary>
        /// Rejects membership preparation because the proof executes only the normalized `All` primitive.<br/>
        /// </summary>
        /// <param name="keys">The unused runtime keys.<br/></param>
        /// <returns>This method never returns.<br/></returns>
        public LibraDexPreparedObjectSet PrepareInSet(IEnumerable<object> keys)
            => throw new NotSupportedException("The typed dedupe proof index does not prepare membership sets.");

        /// <summary>
        /// Materializes the legacy boxed primitive result for compatibility-route comparison.<br/>
        /// </summary>
        /// <param name="request">The normalized primitive request.<br/></param>
        /// <returns>The boxed identities in deterministic source order.<br/></returns>
        IReadOnlyList<object> IIdentityPrimitiveExecutor.ExecuteIdentityPrimitive(LibraDexIdentityPrimitiveRequest request)
            => BoxIdentityDedupeValues(IterateTyped(request)).ToArray();

        /// <summary>
        /// Streams the legacy boxed primitive result for compatibility-route comparison.<br/>
        /// </summary>
        /// <param name="request">The normalized primitive request.<br/></param>
        /// <returns>A lazy boxed identity stream.<br/></returns>
        IEnumerable<object> IIdentityPrimitiveExecutor.IterateIdentityPrimitive(LibraDexIdentityPrimitiveRequest request)
            => BoxIdentityDedupeValues(IterateTyped(request));

        /// <summary>
        /// Streams the same primitive identities without boxing for the Abraxas typed route.<br/>
        /// </summary>
        /// <param name="request">The normalized primitive request.<br/></param>
        /// <returns>A lazy typed identity stream.<br/></returns>
        IEnumerable<ulong> IIdentityPrimitiveExecutor<ulong>.IterateIdentityPrimitiveTyped(LibraDexIdentityPrimitiveRequest request)
            => IterateTyped(request);

        /// <summary>
        /// Counts deterministic physical occurrences without enumerating or boxing them.<br/>
        /// </summary>
        /// <param name="request">The normalized primitive request.<br/></param>
        /// <returns>The number of source occurrences.<br/></returns>
        long IIdentityPrimitiveExecutor.CountIdentityPrimitive(LibraDexIdentityPrimitiveRequest request)
        {
            ValidateAllRequest(request);
            return fixedValues?.Count ?? scenario.InputCount;
        }

        /// <summary>
        /// Materializes all legacy boxed identities for compatibility-only universe callers.<br/>
        /// </summary>
        /// <returns>The boxed deterministic identities.<br/></returns>
        IReadOnlyList<object> IIdentityPrimitiveExecutor.ExecuteAllIdentities()
            => BoxIdentityDedupeValues(IterateTyped(CreateAllRequest())).ToArray();

        /// <summary>
        /// Streams all legacy boxed identities for compatibility-only universe callers.<br/>
        /// </summary>
        /// <returns>A lazy boxed identity universe.<br/></returns>
        IEnumerable<object> IIdentityPrimitiveExecutor.IterateIdentityUniverse()
            => BoxIdentityDedupeValues(IterateTyped(CreateAllRequest()));

        /// <summary>
        /// Streams all identities as typed values for typed complement planning.<br/>
        /// </summary>
        /// <returns>A lazy typed identity universe.<br/></returns>
        IEnumerable<ulong> IIdentityPrimitiveExecutor<ulong>.IterateIdentityUniverseTyped()
            => IterateTyped(CreateAllRequest());

        /// <summary>
        /// Streams the fixed or generated deterministic source after validating the supported primitive contract.<br/>
        /// </summary>
        /// <param name="request">The normalized all-identities request.<br/></param>
        /// <returns>A lazy unboxed UInt64 sequence.<br/></returns>
        private IEnumerable<ulong> IterateTyped(LibraDexIdentityPrimitiveRequest request)
        {
            ValidateAllRequest(request);
            IEnumerable<ulong> source = fixedValues ?? EnumerateIdentityDedupeValues(scenario.InputCount, scenario.DistinctCount);
            return request.TakeLimit is int take ? source.Take(take) : source;
        }

        /// <summary>
        /// Validates that the harness executor is used only for its supported all-identities primitive.<br/>
        /// </summary>
        /// <param name="request">The request to validate.<br/></param>
        private static void ValidateAllRequest(LibraDexIdentityPrimitiveRequest request)
        {
            if (request.CriteriaKind != LibraDexCriteriaKind.All)
                throw new NotSupportedException($"The typed dedupe proof executor does not support {request.CriteriaKind}.");
            if (request.TakeLimit is < 0)
                throw new ArgumentOutOfRangeException(nameof(request.TakeLimit), request.TakeLimit, "Take cannot be negative.");
        }

        /// <summary>
        /// Creates the normalized unbounded request used by universe compatibility methods.<br/>
        /// </summary>
        /// <returns>An unbounded all-identities primitive request.<br/></returns>
        private static LibraDexIdentityPrimitiveRequest CreateAllRequest()
            => new(LibraDexCriteriaKind.All, Array.Empty<object?>());
    }
}
