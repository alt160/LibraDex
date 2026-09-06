using LibraDex;

internal static partial class RawHarness
{
    /// <summary>
    /// Proves the public UInt64 presence and counted handles linearize overlapping mutations and retain coherent reads.<br/>
    /// The probe covers one-winner same-key insertion, disjoint-key parallel population with concurrent membership readers, exact increments without lost updates, and saturation without retained-count or generation inflation.<br/>
    /// </summary>
    /// <param name="args">The command arguments; no additional options are currently accepted.<br/></param>
    /// <returns>Zero when every concurrency invariant passes.<br/></returns>
    private static int RunUInt64SetConcurrencySanity(string[] args)
    {
        _ = args;
        RunUInt64PresenceSetConcurrencySanity();
        RunUInt64RoutedSetConcurrencySanity();
        RunUInt64CountedSetConcurrencySanity();
        Console.WriteLine("uint64-set-concurrency-sanity passed for sorted presence, routed presence, exact-counted, and saturation-two memory handles.");
        return 0;
    }

    /// <summary>
    /// Verifies that exactly one overlapping presence insertion wins and that coherent membership readers can overlap disjoint-key population without observing invalid topology.<br/>
    /// </summary>
    private static void RunUInt64PresenceSetConcurrencySanity()
    {
        using LibraDexSortedSet<ulong> set = LibraDexSortedSet<ulong>.CreateMemory();
        int winners = 0;
        Parallel.For(0, 64, _ =>
        {
            if (set.TryAdd(0xF0F0_F0F0_F0F0_F0F0UL))
                Interlocked.Increment(ref winners);
        });
        if (winners != 1 || set.Count != 1)
            throw new InvalidDataException($"Concurrent presence insertion expected one winner and one key; winners={winners:N0}, count={set.Count:N0}.");

        const int distinctAdds = 25_000;
        int writerFinished = 0;
        Task writer = Task.Run(() =>
        {
            try
            {
                Parallel.For(0, distinctAdds, i =>
                {
                    if (!set.TryAdd(unchecked((ulong)i + 1)))
                        throw new InvalidDataException($"Concurrent disjoint presence insertion unexpectedly found duplicate key {i + 1:N0}.");
                });
            }
            finally
            {
                Volatile.Write(ref writerFinished, 1);
            }
        });
        Task[] readers = new Task[4];
        for (int readerIndex = 0; readerIndex < readers.Length; readerIndex++)
        {
            int readerSeed = readerIndex;
            readers[readerIndex] = Task.Run(() =>
            {
                int probe = readerSeed + 1;
                while (Volatile.Read(ref writerFinished) == 0)
                {
                    _ = set.Contains(unchecked((ulong)probe));
                    probe += readers.Length;
                    if (probe > distinctAdds)
                        probe = readerSeed + 1;
                }
            });
        }

        writer.GetAwaiter().GetResult();
        Task.WaitAll(readers);
        if (set.Count != distinctAdds + 1UL)
            throw new InvalidDataException($"Concurrent disjoint presence population expected {distinctAdds + 1:N0} keys, got {set.Count:N0}.");
        for (int i = 1; i <= distinctAdds; i++)
        {
            if (!set.Contains(unchecked((ulong)i)))
                throw new InvalidDataException($"Concurrent presence population lost key {i:N0}.");
        }
    }

    /// <summary>
    /// Verifies that exact counted increments lose no updates and that saturation-two stores only its bounded semantic state under heavy overlap.<br/>
    /// </summary>
    private static void RunUInt64CountedSetConcurrencySanity()
    {
        const ulong key = 0x0BAD_F00D_CAFE_BEEFUL;
        const int occurrenceInputs = 100_000;
        using (LibraDexCountedSet<ulong> exact = LibraDexCountedSet<ulong>.CreateMemory(LibraDexCountedSetOptions.Exact))
        {
            Parallel.For(0, occurrenceInputs, _ => exact.AddOccurrence(key));
            if (exact.DistinctCount != 1 ||
                exact.GetCount(key) != occurrenceInputs ||
                exact.RetainedOccurrenceCount != occurrenceInputs ||
                exact.Generation != occurrenceInputs)
            {
                throw new InvalidDataException(
                    $"Concurrent exact counted-set state mismatch: distinct={exact.DistinctCount:N0}, keyCount={exact.GetCount(key):N0}, retained={exact.RetainedOccurrenceCount:N0}, generation={exact.Generation:N0}.");
            }
        }

        using LibraDexCountedSet<ulong> saturating = LibraDexCountedSet<ulong>.CreateMemory(LibraDexCountedSetOptions.Saturating(2));
        Parallel.For(0, occurrenceInputs, _ => saturating.AddOccurrence(key));
        if (saturating.DistinctCount != 1 ||
            saturating.GetCount(key) != 2 ||
            saturating.RetainedOccurrenceCount != 2 ||
            saturating.Generation != 2)
        {
            throw new InvalidDataException(
                $"Concurrent saturation-two state mismatch: distinct={saturating.DistinctCount:N0}, keyCount={saturating.GetCount(key):N0}, retained={saturating.RetainedOccurrenceCount:N0}, generation={saturating.Generation:N0}.");
        }
    }

    /// <summary>
    /// Verifies that routed presence leaves preserve one-winner duplicate semantics and coherent reads while many callers populate disjoint keys.<br/>
    /// The probe deliberately shares the public owner across tasks so it exercises the same serialized structural-publication boundary available to an Abraxas deduplication producer.<br/>
    /// </summary>
    private static void RunUInt64RoutedSetConcurrencySanity()
    {
        using LibraDexRoutedSet<ulong> set = LibraDexRoutedSet<ulong>.CreateMemory();
        const ulong sharedKey = 0xABCD_EF01_2345_6789UL;
        int winners = 0;
        Parallel.For(0, 64, _ =>
        {
            if (set.TryAdd(sharedKey))
                Interlocked.Increment(ref winners);
        });
        if (winners != 1 || set.Count != 1)
            throw new InvalidDataException($"Concurrent routed insertion expected one winner and one key; winners={winners:N0}, count={set.Count:N0}.");

        const int distinctAdds = 25_000;
        int writerFinished = 0;
        Task writer = Task.Run(() =>
        {
            try
            {
                Parallel.For(0, distinctAdds, i =>
                {
                    ulong key = unchecked((ulong)i + 1);
                    if (!set.TryAdd(key))
                        throw new InvalidDataException($"Concurrent disjoint routed insertion unexpectedly found duplicate key {key:N0}.");
                });
            }
            finally
            {
                Volatile.Write(ref writerFinished, 1);
            }
        });

        Task[] readers = new Task[4];
        for (int readerIndex = 0; readerIndex < readers.Length; readerIndex++)
        {
            int readerSeed = readerIndex;
            readers[readerIndex] = Task.Run(() =>
            {
                int probe = readerSeed + 1;
                while (Volatile.Read(ref writerFinished) == 0)
                {
                    _ = set.Contains(unchecked((ulong)probe));
                    probe += readers.Length;
                    if (probe > distinctAdds)
                        probe = readerSeed + 1;
                }
            });
        }

        writer.GetAwaiter().GetResult();
        Task.WaitAll(readers);
        if (set.Count != distinctAdds + 1UL)
            throw new InvalidDataException($"Concurrent routed population expected {distinctAdds + 1:N0} keys, got {set.Count:N0}.");
        for (int i = 1; i <= distinctAdds; i++)
        {
            if (!set.Contains(unchecked((ulong)i)))
                throw new InvalidDataException($"Concurrent routed population lost key {i:N0}.");
        }
    }
}
