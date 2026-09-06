using LibraDex;

internal static partial class RawHarness
{
    /// <summary>
    /// Proves native Decimal, Single, and Double condition transforms together with caller-defined result projection.<br/>
    /// Directed negative values distinguish floor, ceiling, and truncate semantics, while midpoint cases prove both default banker's rounding and explicit away-from-zero rounding.<br/>
    /// </summary>
    /// <param name="args">Command arguments; this validation currently has no options.<br/></param>
    /// <returns>Zero when transformed selection, count, planner classification, and deferred result projection all retain parity.<br/></returns>
    private static int RunNumericTransformResultSanity(string[] args)
    {
        _ = args;
        using Catalog catalog = Catalog.CreateMemory();
        _ = catalog.Indexes["decimal-numbers"].Identities.Int64;
        _ = catalog.Indexes["single-numbers"].Identities.Int64;
        _ = catalog.Indexes["double-numbers"].Identities.Int64;

        using LibraDexIndex<decimal, long> decimals =
            catalog.Indexes["decimal-numbers"]["value"].DecimalKeys<long>().Create(IndexKeys.NonUnique);
        using LibraDexIndex<float, long> singles =
            catalog.Indexes["single-numbers"]["value"].SingleKeys<long>().Create(IndexKeys.NonUnique);
        using LibraDexIndex<double, long> doubles =
            catalog.Indexes["double-numbers"]["value"].DoubleKeys<long>().Create(IndexKeys.NonUnique);

        decimal[] decimalValues = [-2.6m, -2.5m, -2.4m, -1.1m, 1.1m, 2.4m, 2.5m, 2.6m];
        float[] singleValues = [-1.9f, -1.1f, 1.1f, 1.9f];
        double[] doubleValues = [-2.5d, -1.01d, 1.01d, 2.5d];
        InsertValues(decimals, decimalValues);
        InsertValues(singles, singleValues);
        InsertValues(doubles, doubleValues);

        Func<string, IIndex> resolveDecimal =
            name => name == "value" ? decimals : throw new KeyNotFoundException(name);
        Func<string, IIndex> resolveSingle =
            name => name == "value" ? singles : throw new KeyNotFoundException(name);
        Func<string, IIndex> resolveDouble =
            name => name == "value" ? doubles : throw new KeyNotFoundException(name);

        AssertIdentities(
            LibraDexCondition.ForGroup("decimal-numbers").Index("value").AsDecimal.Floor().EqualTo(-2m).EndCondition
                .ToList<long>(resolveDecimal),
            [4L],
            "Decimal Floor negative");
        AssertIdentities(
            LibraDexCondition.ForGroup("decimal-numbers").Index("value").AsDecimal.Ceiling().EqualTo(-2m).EndCondition
                .ToList<long>(resolveDecimal),
            [1L, 2L, 3L],
            "Decimal Ceiling negative");
        AssertIdentities(
            LibraDexCondition.ForGroup("decimal-numbers").Index("value").AsDecimal.Truncate().EqualTo(-2m).EndCondition
                .ToList<long>(resolveDecimal),
            [1L, 2L, 3L],
            "Decimal Truncate negative");
        AssertIdentities(
            LibraDexCondition.ForGroup("decimal-numbers").Index("value").AsDecimal.Round().EqualTo(2m).EndCondition
                .ToList<long>(resolveDecimal),
            [6L, 7L],
            "Decimal Round ToEven");
        AssertIdentities(
            LibraDexCondition.ForGroup("decimal-numbers").Index("value").AsDecimal.Round(0, MidpointRounding.AwayFromZero).EqualTo(3m).EndCondition
                .ToList<long>(resolveDecimal),
            [7L, 8L],
            "Decimal Round AwayFromZero");
        AssertIdentities(
            LibraDexCondition.ForGroup("single-numbers").Index("value").AsSingle.Truncate().EqualTo(-1f).EndCondition
                .ToList<long>(resolveSingle),
            [1L, 2L],
            "Single Truncate negative");
        AssertIdentities(
            LibraDexCondition.ForGroup("double-numbers").Index("value").AsDouble.Ceiling().Between(-1d, 2d).EndCondition
                .ToList<long>(resolveDouble),
            [2L, 3L],
            "Double Ceiling inclusive range");

        LibraDexConditionEndCondition transformedCountCondition =
            LibraDexCondition.ForGroup("decimal-numbers").Index("value").AsDecimal.Round().InSet([2m, 3m]).EndCondition;
        if (transformedCountCondition.Count(resolveDecimal) != 3)
        {
            throw new InvalidDataException("Numeric transform count did not preserve tuple multiplicity.");
        }

        IReadOnlyList<LibraDexConditionLeafClassification> classifications =
            transformedCountCondition.Classify(new Dictionary<string, IIndex>(StringComparer.Ordinal) { ["value"] = decimals });
        if (classifications.Count != 1 ||
            classifications[0].ExecutionClass != LibraDexConditionExecutionClass.VisibleScanLike)
        {
            throw new InvalidDataException("Numeric transform classification did not expose the current key-scan execution boundary.");
        }

        int transformCalls = 0;
        LibraDexCondition<string> transformedResult = LibraDexCondition
            .ForGroup("decimal-numbers")
            .Index("value")
            .AsDecimal
            .GreaterThan(2m)
            .Return<long>()
            .Transform(identity =>
            {
                transformCalls++;
                return $"id:{identity}";
            })
            .EndCondition;
        if (transformCalls != 0)
        {
            throw new InvalidDataException("Result Transform invoked caller code while the condition was being built.");
        }

        IReadOnlyList<string> transformedValues = catalog.IndexSet("decimal-numbers").Get(transformedResult);
        if (!transformedValues.SequenceEqual(["id:6", "id:7", "id:8"]) || transformCalls != transformedValues.Count)
        {
            throw new InvalidDataException("Result Transform did not project each produced logical result exactly once.");
        }

        Console.WriteLine("numeric-transform-result-sanity ok");
        return 0;

        static void InsertValues<TKey>(LibraDexIndex<TKey, long> index, IReadOnlyList<TKey> values)
            where TKey : notnull
        {
            for (int i = 0; i < values.Count; i++)
            {
                ValidateGenericInsert(index.Insert(values[i], i + 1L), $"Numeric transform insert {typeof(TKey).Name} {i}");
            }
        }

        static void AssertIdentities(IReadOnlyList<long> actual, IReadOnlyList<long> expected, string context)
        {
            if (!actual.SequenceEqual(expected))
            {
                throw new InvalidDataException(
                    $"{context} returned [{string.Join(", ", actual)}]; expected [{string.Join(", ", expected)}].");
            }
        }
    }
}
