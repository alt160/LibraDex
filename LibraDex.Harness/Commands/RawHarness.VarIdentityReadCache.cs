using LibraDex;
using LibraDex.Views;

internal static partial class RawHarness
{
    /// <summary>
    /// Proves that the session-owned `SV16` immutable-shelf cache enforces its byte budget, preserves recently used shelves, and rejects stale post-invalidation admissions.<br/>
    /// The fixture uses a deliberately tiny budget so eviction is deterministic without producing a large database or retaining test payloads.<br/>
    /// </summary>
    /// <param name="args">The unused harness command-line arguments.<br/></param>
    /// <returns>Zero when bounded admission, approximate LRU eviction, and generation invalidation all pass.<br/></returns>
    private static int RunScalar16VarIdentityReadCacheSanity(string[] args)
    {
        _ = args;
        Scalar16VarIdentityProfile profile = Scalar16VarIdentityProfile.DefaultInitial;
        byte[] firstBytes = Scalar16VarIdentity.CreateEmpty(profile);
        byte[] secondBytes = (byte[])firstBytes.Clone();
        byte[] thirdBytes = (byte[])firstBytes.Clone();
        Scalar16VarIdentityReadOnly first = new(firstBytes, profile);
        Scalar16VarIdentityReadOnly second = new(secondBytes, profile);
        Scalar16VarIdentityReadOnly third = new(thirdBytes, profile);
        if (!first.IsValid || !second.IsValid || !third.IsValid)
        {
            throw new InvalidDataException("The SV16 read-cache fixture shelves are invalid.");
        }

        long entryBytes = checked((long)firstBytes.Length + first.RetainedSidecarBytes + 128);
        long maxBytes = checked(entryBytes * 3 - 1);
        const long limitedIndexRoot = 10;
        SessionReadCache<Scalar16VarIdentityReadOnly> cache = new("SV16", static shelf => shelf.Bytes.Length + shelf.RetainedSidecarBytes);
        cache.Configure(limitedIndexRoot, maxBytes);
        int generation = cache.Generation;
        _ = cache.Store(limitedIndexRoot, 100, first, generation);
        _ = cache.Store(limitedIndexRoot, 200, second, generation);
        if (!cache.TryGet(limitedIndexRoot, 100, out _))
        {
            throw new InvalidDataException("The SV16 read-cache fixture could not retouch its first shelf.");
        }

        _ = cache.Store(limitedIndexRoot, 300, third, generation);
        bool hasFirst = cache.TryGet(limitedIndexRoot, 100, out _);
        bool hasSecond = cache.TryGet(limitedIndexRoot, 200, out _);
        bool hasThird = cache.TryGet(limitedIndexRoot, 300, out _);
        (int limitedCount, long limitedBytes, long configuredMaxBytes) = cache.GetStats(limitedIndexRoot);
        if (!hasFirst || hasSecond || !hasThird || limitedCount != 2 || limitedBytes > configuredMaxBytes)
        {
            throw new InvalidDataException(
                $"SV16 read-cache eviction failed. first={hasFirst} second={hasSecond} third={hasThird} count={limitedCount} bytes={limitedBytes}/{configuredMaxBytes}.");
        }

        int staleGeneration = cache.Generation;
        cache.Clear();
        _ = cache.Store(limitedIndexRoot, 400, first, staleGeneration);
        if (cache.Count != 0 || cache.CachedBytes != 0)
        {
            throw new InvalidDataException("The SV16 read cache admitted a shelf produced before its invalidation generation.");
        }

        const long unlimitedIndexRoot = 20;
        int unlimitedGeneration = cache.Generation;
        _ = cache.Store(unlimitedIndexRoot, 500, first, unlimitedGeneration);
        _ = cache.Store(unlimitedIndexRoot, 600, second, unlimitedGeneration);
        _ = cache.Store(unlimitedIndexRoot, 700, third, unlimitedGeneration);
        (int unlimitedCount, long unlimitedBytes, long unlimitedMaxBytes) = cache.GetStats(unlimitedIndexRoot);
        if (unlimitedCount != 3 || unlimitedBytes != entryBytes * 3 || unlimitedMaxBytes != 0)
        {
            throw new InvalidDataException(
                $"SV16 unlimited default failed. count={unlimitedCount}/3 bytes={unlimitedBytes}/{entryBytes * 3} max={unlimitedMaxBytes}/0.");
        }

        Console.WriteLine(
            $"sv16-read-cache-sanity ok limitedMaxBytes={maxBytes} entryBytes={entryBytes} retainedAfterEviction={entryBytes * 2} countAfterEviction=2 unlimitedDefaultBytes={unlimitedBytes} unlimitedDefaultCount=3 staleAdmissionRejected=true");
        return 0;
    }

    /// <summary>
    /// Proves that the generalized session-owned immutable-shelf cache applies the same bounded, unlimited, and generation-safe behavior to `SV8` decoded shelves.<br/>
    /// The focused fixture complements routed mutation tests by directly checking cache accounting and stale-admission rejection without creating a database.<br/>
    /// </summary>
    /// <param name="args">The unused harness command-line arguments.<br/></param>
    /// <returns>Zero when `SV8` bounded admission, eviction, unlimited retention, and generation invalidation pass.<br/></returns>
    private static int RunScalar8VarIdentityReadCacheSanity(string[] args)
    {
        _ = args;
        Scalar8VarIdentityProfile profile = Scalar8VarIdentityProfile.DefaultInitial;
        byte[] firstBytes = Scalar8VarIdentity.CreateEmpty(profile);
        byte[] secondBytes = (byte[])firstBytes.Clone();
        byte[] thirdBytes = (byte[])firstBytes.Clone();
        Scalar8VarIdentityReadOnly first = new(firstBytes, profile);
        Scalar8VarIdentityReadOnly second = new(secondBytes, profile);
        Scalar8VarIdentityReadOnly third = new(thirdBytes, profile);
        if (!first.IsValid || !second.IsValid || !third.IsValid)
        {
            throw new InvalidDataException("The SV8 read-cache fixture shelves are invalid.");
        }

        long entryBytes = checked((long)firstBytes.Length + first.RetainedSidecarBytes + 128);
        long maxBytes = checked(entryBytes * 3 - 1);
        const long limitedIndexRoot = 10;
        SessionReadCache<Scalar8VarIdentityReadOnly> cache = new("SV8", static shelf => shelf.Bytes.Length + shelf.RetainedSidecarBytes);
        cache.Configure(limitedIndexRoot, maxBytes);
        int generation = cache.Generation;
        _ = cache.Store(limitedIndexRoot, 100, first, generation);
        _ = cache.Store(limitedIndexRoot, 200, second, generation);
        if (!cache.TryGet(limitedIndexRoot, 100, out _))
        {
            throw new InvalidDataException("The SV8 read-cache fixture could not retouch its first shelf.");
        }

        _ = cache.Store(limitedIndexRoot, 300, third, generation);
        bool hasFirst = cache.TryGet(limitedIndexRoot, 100, out _);
        bool hasSecond = cache.TryGet(limitedIndexRoot, 200, out _);
        bool hasThird = cache.TryGet(limitedIndexRoot, 300, out _);
        (int limitedCount, long limitedBytes, long configuredMaxBytes) = cache.GetStats(limitedIndexRoot);
        if (!hasFirst || hasSecond || !hasThird || limitedCount != 2 || limitedBytes > configuredMaxBytes)
        {
            throw new InvalidDataException(
                $"SV8 read-cache eviction failed. first={hasFirst} second={hasSecond} third={hasThird} count={limitedCount} bytes={limitedBytes}/{configuredMaxBytes}.");
        }

        int staleGeneration = cache.Generation;
        cache.Clear();
        _ = cache.Store(limitedIndexRoot, 400, first, staleGeneration);
        if (cache.Count != 0 || cache.CachedBytes != 0)
        {
            throw new InvalidDataException("The SV8 read cache admitted a shelf produced before its invalidation generation.");
        }

        const long unlimitedIndexRoot = 20;
        int unlimitedGeneration = cache.Generation;
        _ = cache.Store(unlimitedIndexRoot, 500, first, unlimitedGeneration);
        _ = cache.Store(unlimitedIndexRoot, 600, second, unlimitedGeneration);
        _ = cache.Store(unlimitedIndexRoot, 700, third, unlimitedGeneration);
        (int unlimitedCount, long unlimitedBytes, long unlimitedMaxBytes) = cache.GetStats(unlimitedIndexRoot);
        if (unlimitedCount != 3 || unlimitedBytes != entryBytes * 3 || unlimitedMaxBytes != 0)
        {
            throw new InvalidDataException(
                $"SV8 unlimited default failed. count={unlimitedCount}/3 bytes={unlimitedBytes}/{entryBytes * 3} max={unlimitedMaxBytes}/0.");
        }

        Console.WriteLine(
            $"sv8-read-cache-sanity ok limitedMaxBytes={maxBytes} entryBytes={entryBytes} retainedAfterEviction={entryBytes * 2} countAfterEviction=2 unlimitedDefaultBytes={unlimitedBytes} unlimitedDefaultCount=3 staleAdmissionRejected=true");
        return 0;
    }
}
