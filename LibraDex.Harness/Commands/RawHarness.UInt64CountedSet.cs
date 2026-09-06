using LibraDex;

internal static partial class RawHarness
{
    /// <summary>
    /// Validates exact plus every promoted saturating counter-width boundary through public memory and file handles.<br/>
    /// The proof covers new-key insertion, repeated occurrences, saturation no-ops, count lookup, thresholds, ordered copying, decrement, whole-key removal, durable reopen, fast clear, and reuse-before-extension repopulation.<br/>
    /// </summary>
    /// <param name="args">The complete harness argument vector; this command accepts no trailing arguments.<br/></param>
    /// <returns>Zero when all counted-set modes and backings preserve their contracts.<br/></returns>
    private static int RunUInt64CountedSetSanity(string[] args)
    {
        if (args.Length != 1 || !string.Equals(args[0], "uint64-counted-set-sanity", StringComparison.Ordinal))
            throw new ArgumentException("uint64-counted-set-sanity does not accept arguments.", nameof(args));

        ulong[] saturationCeilings = [0, 2, 3, 15, byte.MaxValue, ushort.MaxValue, uint.MaxValue, ulong.MaxValue - 1];
        ulong[] keys = CreateUInt64SetPrototypeKeys(4_096);
        for (int i = 0; i < saturationCeilings.Length; i++)
        {
            RunUInt64CountedSetModeSanity(keys, saturationCeilings[i], fileBacked: false);
            RunUInt64CountedSetModeSanity(keys, saturationCeilings[i], fileBacked: true);
        }

        Console.WriteLine("uint64-counted-set-sanity passed for exact and 2/4/8/16/32/64-bit saturating layouts in memory and file modes.");
        return 0;
    }

    /// <summary>
    /// Runs one complete public counted-set lifecycle for one policy and backing.<br/>
    /// </summary>
    private static void RunUInt64CountedSetModeSanity(ulong[] keys, ulong saturationCeiling, bool fileBacked)
    {
        LibraDexCountedSetOptions options = saturationCeiling == 0
            ? LibraDexCountedSetOptions.Exact
            : LibraDexCountedSetOptions.Saturating(saturationCeiling);
        string? path = fileBacked
            ? Path.Combine(Path.GetTempPath(), $"libradex-counted-set-{Guid.NewGuid():N}.lbdxset")
            : null;
        LibraDexCountedSet<ulong>? set = null;
        try
        {
            set = fileBacked
                ? LibraDexCountedSet<ulong>.Create(path!, options)
                : LibraDexCountedSet<ulong>.CreateMemory(options);
            int expectedCounterBits = ResolveExpectedCounterBits(saturationCeiling);
            if (set.CounterBits != expectedCounterBits || set.IsSaturating != (saturationCeiling != 0))
                throw new InvalidDataException($"Counted-set policy resolved to {set.CounterBits} bits and saturating={set.IsSaturating}, expected {expectedCounterBits} and {saturationCeiling != 0}.");

            Dictionary<ulong, ulong> expected = new(keys.Length);
            for (int i = 0; i < keys.Length; i++)
            {
                if (set.AddOccurrence(keys[i]) != 1)
                    throw new InvalidDataException($"Counted set rejected first occurrence of 0x{keys[i]:X16}.");
                expected.Add(keys[i], 1);
            }

            int step = Math.Max(1, keys.Length / 257);
            for (int i = 0; i < keys.Length; i += step)
            {
                for (int repeat = 0; repeat < 4; repeat++)
                {
                    ulong prior = expected[keys[i]];
                    ulong expectedNext = saturationCeiling == 0 ? checked(prior + 1) : Math.Min(saturationCeiling, prior + 1);
                    ulong actualNext = set.AddOccurrence(keys[i]);
                    if (actualNext != expectedNext)
                        throw new InvalidDataException($"Counted set returned {actualNext:N0} for 0x{keys[i]:X16}; expected {expectedNext:N0}.");
                    expected[keys[i]] = expectedNext;
                }
            }

            ValidateUInt64CountedSet(set, expected, "initial/repeated");
            ulong firstKey = keys[0];
            ulong priorFirst = expected[firstKey];
            ulong expectedAfterDecrement = priorFirst - 1;
            if (set.RemoveOccurrence(firstKey) != expectedAfterDecrement)
                throw new InvalidDataException("Counted-set decrement returned the wrong post-operation count.");
            if (expectedAfterDecrement == 0)
                expected.Remove(firstKey);
            else
                expected[firstKey] = expectedAfterDecrement;

            ulong secondKey = keys[1];
            ulong expectedRemoved = expected[secondKey];
            if (set.Remove(secondKey) != expectedRemoved || set.Remove(secondKey) != 0)
                throw new InvalidDataException("Counted-set whole-key removal returned the wrong prior count or accepted a repeated removal.");
            expected.Remove(secondKey);
            ValidateUInt64CountedSet(set, expected, "after removals");

            long fileLengthBeforeClear = path is null ? 0 : new FileInfo(path).Length;
            if (fileBacked)
            {
                set.Dispose();
                set = LibraDexCountedSet<ulong>.Open(path!);
                ValidateUInt64CountedSet(set, expected, "after reopen");
            }

            set.Clear();
            if (set.DistinctCount != 0 || set.RetainedOccurrenceCount != 0)
                throw new InvalidDataException("Counted-set clear retained root counts.");
            for (int i = 0; i < keys.Length; i++)
                _ = set.AddOccurrence(keys[i]);
            if (fileBacked && new FileInfo(path!).Length > fileLengthBeforeClear)
                throw new InvalidDataException("Counted-set same-shape repopulation extended the file despite reusable compatible extents.");
        }
        finally
        {
            set?.Dispose();
            if (path is not null && File.Exists(path))
                File.Delete(path);
        }
    }

    /// <summary>
    /// Compares public root aggregates, point lookups, threshold behavior, and both ordered traversal directions with a managed reference.<br/>
    /// </summary>
    private static void ValidateUInt64CountedSet(
        LibraDexCountedSet<ulong> set,
        IReadOnlyDictionary<ulong, ulong> expected,
        string phase)
    {
        ulong expectedTotal = 0;
        foreach (ulong count in expected.Values)
            expectedTotal = checked(expectedTotal + count);
        if (set.DistinctCount != (ulong)expected.Count || set.RetainedOccurrenceCount != expectedTotal)
            throw new InvalidDataException($"Counted set {phase} root totals do not match the managed reference.");

        KeyValuePair<ulong, ulong>[] ascending = expected.OrderBy(static pair => pair.Key).ToArray();
        LibraDexCountedSetEntry<ulong>[] actual = set.ToArray();
        if (actual.Length != ascending.Length)
            throw new InvalidDataException($"Counted set {phase} emitted {actual.Length:N0} entries; expected {ascending.Length:N0}.");
        for (int i = 0; i < actual.Length; i++)
        {
            if (actual[i].Key != ascending[i].Key || actual[i].Count != ascending[i].Value ||
                set.GetCount(actual[i].Key) != actual[i].Count ||
                !set.ContainsAtLeast(actual[i].Key, actual[i].Count))
            {
                throw new InvalidDataException($"Counted set {phase} diverged at ordered ordinal {i:N0}.");
            }
        }

        LibraDexCountedSetEntry<ulong>[] descending = set.ToArray(descending: true);
        for (int i = 0; i < descending.Length; i++)
        {
            LibraDexCountedSetEntry<ulong> expectedEntry = actual[actual.Length - i - 1];
            if (descending[i] != expectedEntry)
                throw new InvalidDataException($"Counted set {phase} descending order diverged at ordinal {i:N0}.");
        }
    }

    /// <summary>
    /// Resolves the expected minimal physical width for one exact or saturating public policy.<br/>
    /// </summary>
    private static int ResolveExpectedCounterBits(ulong saturationCeiling) => saturationCeiling switch
    {
        0 => 64,
        <= 0x03UL => 2,
        <= 0x0FUL => 4,
        <= byte.MaxValue => 8,
        <= ushort.MaxValue => 16,
        <= uint.MaxValue => 32,
        _ => 64
    };
}
