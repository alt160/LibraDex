using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace LibraDex;

/// <summary>
/// Retains one immutable shelf family for every matching index opened through one file session.<br/>
/// Physical shelf offsets remain session-unique, while retained bytes and optional eviction limits are accounted independently by owning index root.<br/>
/// Approximate least-recently-used eviction stays inactive for the zero-limit default and keeps limited-index hits lock-free.<br/>
/// </summary>
/// <typeparam name="TShelf">The strongly typed immutable shelf representation.<br/></typeparam>
internal sealed class SessionReadCache<TShelf>
    where TShelf : class
{
    private const int EntryOverheadEstimate = 128;
    private readonly ConcurrentDictionary<long, Entry> entries = [];
    private readonly Dictionary<long, long> cachedBytesByIndex = [];
    private readonly Dictionary<long, int> entryCountsByIndex = [];
    private readonly Dictionary<long, long> maxCachedBytesByIndex = [];
    private readonly object trimSync = new();
    private readonly string shapeName;
    private readonly Func<TShelf, long> getRetainedBytes;
    private long cachedBytes;
    private int generation;

    /// <summary>
    /// Creates one typed session cache whose diagnostics retain the concrete shelf-shape name.<br/>
    /// The name is used only on configuration or corruption failures and does not enter the read-hit path.<br/>
    /// </summary>
    /// <param name="shapeName">The short persisted shelf-shape name.<br/></param>
    /// <param name="getRetainedBytes">The callback that reports the retained byte size of each cached shelf.<br/></param>
    internal SessionReadCache(string shapeName, Func<TShelf, long> getRetainedBytes)
    {
        this.shapeName = shapeName;
        this.getRetainedBytes = getRetainedBytes;
    }

    internal int Count => entries.Count;

    internal long CachedBytes => Interlocked.Read(ref cachedBytes);

    internal int Generation => Volatile.Read(ref generation);

    /// <summary>
    /// Sets the runtime read-cache limit for one physical var-identity index root.<br/>
    /// Zero removes the limit without clearing useful entries; a positive value immediately trims that index when its current retention is above the new ceiling.<br/>
    /// </summary>
    /// <param name="indexRootOffset">The physical root router offset identifying the index.<br/></param>
    /// <param name="maxCachedBytes">The positive retained-byte limit, or zero for no limit.<br/></param>
    internal void Configure(long indexRootOffset, long maxCachedBytes)
    {
        if (indexRootOffset <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(indexRootOffset), indexRootOffset, $"The {shapeName} cache owner root must be positive.");
        }

        if (maxCachedBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCachedBytes), maxCachedBytes, $"The {shapeName} per-index read-cache limit cannot be negative.");
        }

        lock (trimSync)
        {
            maxCachedBytesByIndex[indexRootOffset] = maxCachedBytes;
            foreach (Entry entry in entries.Values)
            {
                if (entry.IndexRootOffset == indexRootOffset)
                {
                    entry.SetTrackRecency(maxCachedBytes > 0);
                }
            }

            if (maxCachedBytes > 0 &&
                cachedBytesByIndex.TryGetValue(indexRootOffset, out long indexCachedBytes) &&
                indexCachedBytes > maxCachedBytes)
            {
                TrimUnderLock(indexRootOffset, maxCachedBytes, protectedShelfOffset: long.MinValue);
            }
        }
    }

    /// <summary>
    /// Tries to acquire one immutable cached shelf without entering the eviction lock.<br/>
    /// A hit advances an approximate access stamp so later admission can discard colder shelves first.<br/>
    /// </summary>
    /// <param name="indexRootOffset">The owning index root used to reject cross-index cache aliasing.<br/></param>
    /// <param name="shelfOffset">The physical shelf file offset.<br/></param>
    /// <param name="shelf">Receives the immutable decoded shelf on a cache hit.<br/></param>
    /// <returns><see langword="true"/> when the shelf is cached; otherwise <see langword="false"/>.<br/></returns>
    internal bool TryGet(long indexRootOffset, long shelfOffset, out TShelf shelf)
    {
        if (entries.TryGetValue(shelfOffset, out Entry? entry) && entry.IndexRootOffset == indexRootOffset)
        {
            entry.TouchWhenLimited(Stopwatch.GetTimestamp());
            shelf = entry.Shelf;
            return true;
        }

        shelf = null!;
        return false;
    }

    /// <summary>
    /// Admits one immutable shelf when the cache generation still matches the read that produced it.<br/>
    /// Duplicate concurrent materializations converge on the first admitted entry, and overflow evicts the coldest entries down to a reuse headroom target.<br/>
    /// </summary>
    /// <param name="indexRootOffset">The owning index root used for per-index cache accounting.<br/></param>
    /// <param name="shelfOffset">The physical shelf file offset.<br/></param>
    /// <param name="shelf">The immutable decoded shelf to retain.<br/></param>
    /// <param name="expectedGeneration">The cache generation observed before raw shelf I/O began.<br/></param>
    /// <returns>The admitted shelf, or the existing equivalent entry when another reader won admission.<br/></returns>
    internal TShelf Store(
        long indexRootOffset,
        long shelfOffset,
        TShelf shelf,
        int expectedGeneration)
    {
        long retainedBytes = checked(getRetainedBytes(shelf) + EntryOverheadEstimate);
        lock (trimSync)
        {
            if (generation != expectedGeneration)
            {
                return shelf;
            }

            maxCachedBytesByIndex.TryGetValue(indexRootOffset, out long maxCachedBytes);
            if (maxCachedBytes > 0 && retainedBytes > maxCachedBytes)
            {
                return shelf;
            }

            if (entries.TryGetValue(shelfOffset, out Entry? existing))
            {
                if (existing.IndexRootOffset != indexRootOffset)
                {
                    throw new InvalidDataException($"One physical {shapeName} shelf offset was associated with more than one index root in the session read cache.");
                }

                existing.Touch(Stopwatch.GetTimestamp());
                return existing.Shelf;
            }

            Entry entry = new(indexRootOffset, shelf, retainedBytes, Stopwatch.GetTimestamp(), maxCachedBytes > 0);
            entries[shelfOffset] = entry;
            cachedBytes += retainedBytes;
            cachedBytesByIndex.TryGetValue(indexRootOffset, out long indexCachedBytes);
            cachedBytesByIndex[indexRootOffset] = indexCachedBytes + retainedBytes;
            entryCountsByIndex.TryGetValue(indexRootOffset, out int indexEntryCount);
            entryCountsByIndex[indexRootOffset] = indexEntryCount + 1;
            if (maxCachedBytes == 0)
            {
                return shelf;
            }

            long targetBytes = maxCachedBytes;
            long currentIndexCachedBytes = cachedBytesByIndex[indexRootOffset];
            if (currentIndexCachedBytes > maxCachedBytes)
            {
                targetBytes = maxCachedBytes - (maxCachedBytes >> 3);
            }

            if (currentIndexCachedBytes > targetBytes)
            {
                TrimUnderLock(indexRootOffset, targetBytes, shelfOffset);
            }

            return shelf;
        }
    }

    /// <summary>
    /// Removes one physical shelf after its mutable image has been published.<br/>
    /// Concurrent readers may safely finish against the immutable object they already acquired; later readers must reload the published bytes.<br/>
    /// </summary>
    /// <param name="shelfOffset">The physical shelf file offset to invalidate.<br/></param>
    internal void Remove(long shelfOffset)
    {
        lock (trimSync)
        {
            if (entries.TryRemove(shelfOffset, out Entry? removed))
            {
                RemoveAccountingUnderLock(removed);
            }
        }
    }

    /// <summary>
    /// Invalidates every cached shelf at a structural session mutation boundary.<br/>
    /// Incrementing the generation prevents a raw read that began before invalidation from repopulating stale bytes afterward.<br/>
    /// </summary>
    internal void Clear()
    {
        lock (trimSync)
        {
            generation++;
            entries.Clear();
            cachedBytesByIndex.Clear();
            entryCountsByIndex.Clear();
            cachedBytes = 0;
        }
    }

    /// <summary>
    /// Gets the retained shelf count and estimated retained bytes for one physical var-identity index root.<br/>
    /// This focused diagnostic reads accounting under the admission lock and does not affect cache recency.<br/>
    /// </summary>
    /// <param name="indexRootOffset">The owning index root to inspect.<br/></param>
    /// <returns>The retained entry count, estimated retained bytes, and configured limit for that index.<br/></returns>
    internal (int EntryCount, long CachedBytes, long MaxCachedBytes) GetStats(long indexRootOffset)
    {
        lock (trimSync)
        {
            entryCountsByIndex.TryGetValue(indexRootOffset, out int entryCount);
            cachedBytesByIndex.TryGetValue(indexRootOffset, out long indexCachedBytes);
            maxCachedBytesByIndex.TryGetValue(indexRootOffset, out long maxCachedBytes);
            return (entryCount, indexCachedBytes, maxCachedBytes);
        }
    }

    /// <summary>
    /// Evicts approximately least-recently-used entries until retained bytes fit the requested target.<br/>
    /// The newly admitted shelf may be protected for the current trim so one oversized working-set pass does not read and immediately discard the same shelf.<br/>
    /// </summary>
    /// <param name="indexRootOffset">The owning index root whose entries may be evicted.<br/></param>
    /// <param name="targetBytes">The per-index retained-byte target after trimming.<br/></param>
    /// <param name="protectedShelfOffset">The shelf offset to preserve during this trim, or <see cref="long.MinValue"/> when none is protected.<br/></param>
    private void TrimUnderLock(long indexRootOffset, long targetBytes, long protectedShelfOffset)
    {
        while (cachedBytesByIndex.TryGetValue(indexRootOffset, out long indexCachedBytes) && indexCachedBytes > targetBytes)
        {
            long oldestOffset = long.MinValue;
            long oldestAccess = long.MaxValue;
            foreach (KeyValuePair<long, Entry> pair in entries)
            {
                if (pair.Key == protectedShelfOffset || pair.Value.IndexRootOffset != indexRootOffset)
                {
                    continue;
                }

                long access = pair.Value.LastAccess;
                if (access < oldestAccess)
                {
                    oldestAccess = access;
                    oldestOffset = pair.Key;
                }
            }

            if (oldestOffset == long.MinValue || !entries.TryRemove(oldestOffset, out Entry? removed))
            {
                break;
            }

            RemoveAccountingUnderLock(removed);
        }
    }

    /// <summary>
    /// Removes one entry's global and per-index retained-byte accounting after its dictionary entry has been removed.<br/>
    /// The caller holds the admission lock so the count and byte maps remain coherent with the physical entry map.<br/>
    /// </summary>
    /// <param name="removed">The removed immutable cache entry.<br/></param>
    private void RemoveAccountingUnderLock(Entry removed)
    {
        cachedBytes -= removed.RetainedBytes;
        long indexCachedBytes = cachedBytesByIndex[removed.IndexRootOffset] - removed.RetainedBytes;
        int indexEntryCount = entryCountsByIndex[removed.IndexRootOffset] - 1;
        if (indexEntryCount == 0)
        {
            cachedBytesByIndex.Remove(removed.IndexRootOffset);
            entryCountsByIndex.Remove(removed.IndexRootOffset);
            return;
        }

        cachedBytesByIndex[removed.IndexRootOffset] = indexCachedBytes;
        entryCountsByIndex[removed.IndexRootOffset] = indexEntryCount;
    }

    private sealed class Entry
    {
        private long lastAccess;
        private int trackRecency;

        internal Entry(long indexRootOffset, TShelf shelf, long retainedBytes, long lastAccess, bool trackRecency)
        {
            IndexRootOffset = indexRootOffset;
            Shelf = shelf;
            RetainedBytes = retainedBytes;
            this.lastAccess = lastAccess;
            this.trackRecency = trackRecency ? 1 : 0;
        }

        internal long IndexRootOffset { get; }

        internal TShelf Shelf { get; }

        internal long RetainedBytes { get; }

        internal long LastAccess => Volatile.Read(ref lastAccess);

        /// <summary>
        /// Advances approximate recency only when the owning index has a positive eviction ceiling.<br/>
        /// Unlimited indexes never evict, so suppressing the shared timestamp write avoids false sharing between concurrent readers.<br/>
        /// </summary>
        /// <param name="access">The current monotonic timestamp.<br/></param>
        internal void TouchWhenLimited(long access)
        {
            if (Volatile.Read(ref trackRecency) != 0)
            {
                Volatile.Write(ref lastAccess, access);
            }
        }

        /// <summary>
        /// Changes recency tracking when the developer reconfigures this entry's owning index between unlimited and bounded retention.<br/>
        /// The cache admission lock serializes configuration with eviction while the volatile flag keeps read hits lock-free.<br/>
        /// </summary>
        /// <param name="enabled">Whether later hits must refresh approximate LRU recency.<br/></param>
        internal void SetTrackRecency(bool enabled) => Volatile.Write(ref trackRecency, enabled ? 1 : 0);

        internal void Touch(long access) => Volatile.Write(ref lastAccess, access);
    }
}
