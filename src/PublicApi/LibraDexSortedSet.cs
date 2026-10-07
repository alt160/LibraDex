using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LibraDex;

/// <summary>
/// Provides an ordered mutable set of distinct keys over LibraDex memory or file storage.<br/>
/// The current experimental slice supports <see cref="ulong"/> keys; the generic handle preserves one candidate collection shape while the design remains under evaluation.<br/>
/// Keys are stored without dummy identities, and ascending or descending copies follow the persisted radix order without a result-side sort or deduplication set.<br/>
/// </summary>
/// <remarks>
/// PARKED R&amp;D FEATURE: this type is intentionally assembly-internal, incomplete, unsupported, and excluded from LibraDex's public API contract.<br/>
/// Its implementation and harness coverage are retained so the set workstream can resume without losing validated design and performance evidence.<br/>
/// </remarks>
/// <typeparam name="TKey">The key type; this release supports <see cref="ulong"/>.<br/></typeparam>
internal sealed class LibraDexSortedSet<TKey> : IDisposable
{
    private UInt64SetPrototype? _uint64Storage;
    private readonly string? _filePath;

    private LibraDexSortedSet(UInt64SetPrototype storage, string? filePath)
    {
        _uint64Storage = storage;
        _filePath = filePath;
    }

    /// <summary>
    /// Gets the exact number of distinct keys currently present.<br/>
    /// The value is maintained at mutation publication and does not enumerate the set.<br/>
    /// </summary>
    public ulong Count => Storage.Count;

    /// <summary>
    /// Gets the successful logical mutation generation.<br/>
    /// Duplicate additions and removal misses do not advance this value.<br/>
    /// </summary>
    public ulong Generation => Storage.Generation;

    /// <summary>
    /// Gets whether this set uses process-local memory and therefore cannot be reopened after disposal.<br/>
    /// </summary>
    public bool IsMemoryBacked => Storage.BackingKind == DataKernelBackingKind.Memory;

    /// <summary>
    /// Gets the canonical durable path for a file-backed set, or <see langword="null"/> for a memory-backed set.<br/>
    /// </summary>
    public string? FilePath => _filePath;

    /// <summary>
    /// Creates an empty process-local ordered set.<br/>
    /// Memory and file backings use the same key, router, and shelf formats, but memory storage has no reopen lifecycle.<br/>
    /// </summary>
    /// <returns>An empty memory-backed set.<br/></returns>
    public static LibraDexSortedSet<TKey> CreateMemory()
    {
        EnsureSupportedKeyType();
        return new LibraDexSortedSet<TKey>(UInt64SetPrototype.CreateMemory(), filePath: null);
    }

    /// <summary>
    /// Creates an empty durable ordered set at a new file path.<br/>
    /// Creation fails when the target already exists so an unrelated set cannot be overwritten implicitly.<br/>
    /// </summary>
    /// <param name="path">The new set file path.<br/></param>
    /// <returns>An empty file-backed set.<br/></returns>
    public static LibraDexSortedSet<TKey> Create(string path)
    {
        EnsureSupportedKeyType();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        return new LibraDexSortedSet<TKey>(UInt64SetPrototype.Create(fullPath), fullPath);
    }

    /// <summary>
    /// Opens an existing durable ordered set and validates its root kind before exposing it.<br/>
    /// </summary>
    /// <param name="path">The existing set file path.<br/></param>
    /// <returns>The reopened file-backed set.<br/></returns>
    public static LibraDexSortedSet<TKey> Open(string path)
    {
        EnsureSupportedKeyType();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        return new LibraDexSortedSet<TKey>(UInt64SetPrototype.Open(fullPath), fullPath);
    }

    /// <summary>
    /// Adds one key when it is not already present.<br/>
    /// Concurrent callers are linearized by the set owner, so exactly one caller reports insertion for one previously absent key.<br/>
    /// </summary>
    /// <param name="key">The key to add.<br/></param>
    /// <returns><see langword="true"/> when the key became newly present; otherwise <see langword="false"/>.<br/></returns>
    public bool TryAdd(TKey key) => Storage.TryAdd(Encode(key));

    /// <summary>
    /// Adds a caller-provided span through one explicit high-throughput set mutation.<br/>
    /// The batch accepts unsorted input and ignores values already present or repeated within the span; successful publication advances <see cref="Generation"/> once regardless of the number of newly added keys.<br/>
    /// Use <see cref="TryAdd(TKey)"/> when each key must become independently visible or the caller needs a per-input duplicate decision.<br/>
    /// </summary>
    /// <param name="keys">The input keys; caller order and duplicate values are allowed.<br/></param>
    /// <returns>The number of keys that became newly present in the completed batch.<br/></returns>
    public int AddMany(ReadOnlySpan<TKey> keys)
    {
        EnsureSupportedKeyType();
        if (keys.IsEmpty)
            return 0;

        ref TKey first = ref MemoryMarshal.GetReference(keys);
        ref ulong firstUInt64 = ref Unsafe.As<TKey, ulong>(ref first);
        ReadOnlySpan<ulong> encodedKeys = MemoryMarshal.CreateReadOnlySpan(ref firstUInt64, keys.Length);
        return Storage.AddMany(encodedKeys);
    }

    /// <summary>
    /// Tests whether one key is present through a bounded radix walk and one leaf binary search.<br/>
    /// </summary>
    /// <param name="key">The key to locate.<br/></param>
    /// <returns><see langword="true"/> when the key is present.<br/></returns>
    public bool Contains(TKey key) => Storage.Contains(Encode(key));

    /// <summary>
    /// Removes one key when present.<br/>
    /// A removal miss does not change cardinality or generation.<br/>
    /// </summary>
    /// <param name="key">The key to remove.<br/></param>
    /// <returns><see langword="true"/> when a present key was removed.<br/></returns>
    public bool Remove(TKey key) => Storage.Remove(Encode(key));

    /// <summary>
    /// Clears the complete set through one root publication and retires its non-root topology for reuse.<br/>
    /// The operation does not perform record-by-record deletion.<br/>
    /// </summary>
    public void Clear() => Storage.Clear();

    /// <summary>
    /// Copies every key into a caller-owned span in ascending or descending natural key order.<br/>
    /// The operation uses bounded traversal workspace and performs no result-side sort or duplicate elimination.<br/>
    /// </summary>
    /// <param name="destination">The destination span, which must fit the captured <see cref="Count"/>.<br/></param>
    /// <param name="descending">Whether to copy in descending rather than ascending order.<br/></param>
    /// <returns>The number of keys copied.<br/></returns>
    public int CopyTo(Span<TKey> destination, bool descending = false)
    {
        EnsureSupportedKeyType();
        ref TKey first = ref MemoryMarshal.GetReference(destination);
        ref ulong firstUInt64 = ref Unsafe.As<TKey, ulong>(ref first);
        Span<ulong> encodedDestination = MemoryMarshal.CreateSpan(ref firstUInt64, destination.Length);
        return Storage.CopyTo(encodedDestination, descending);
    }

    /// <summary>
    /// Materializes the current keys into a new array in ascending or descending natural key order.<br/>
    /// Prefer <see cref="CopyTo(Span{TKey}, bool)"/> when the caller already owns a reusable result buffer.<br/>
    /// </summary>
    /// <param name="descending">Whether to return descending rather than ascending order.<br/></param>
    /// <returns>A newly allocated exact-length ordered key array.<br/></returns>
    public TKey[] ToArray(bool descending = false)
    {
        ulong capturedCount = Count;
        if (capturedCount > int.MaxValue)
            throw new InvalidOperationException("The set is too large to materialize into one managed array; use a bounded traversal API.");

        TKey[] result = new TKey[(int)capturedCount];
        CopyTo(result, descending);
        return result;
    }

    /// <summary>
    /// Captures internal topology and arena accounting after benchmark timing has stopped.<br/>
    /// The diagnostic is intentionally assembly-internal so physical implementation evidence does not become part of the public collection contract.<br/>
    /// </summary>
    /// <returns>The current sorted topology and backing-storage snapshot.<br/></returns>
    internal UInt64SetTopologyStorageSnapshot GetTopologyStorageSnapshot() =>
        Storage.GetTopologyStorageSnapshot();

    /// <summary>
    /// Releases the owned memory arena or durable file handle.<br/>
    /// Disposing a file-backed set leaves its current authoritative image reopenable.<br/>
    /// </summary>
    public void Dispose()
    {
        UInt64SetPrototype? storage = Interlocked.Exchange(ref _uint64Storage, null);
        storage?.Dispose();
    }

    private UInt64SetPrototype Storage =>
        _uint64Storage ?? throw new ObjectDisposedException(GetType().FullName);

    /// <summary>
    /// Verifies that the closed generic handle has a promoted physical key codec.<br/>
    /// Unsupported types fail at creation/open rather than silently boxing, stringifying, or changing comparison semantics.<br/>
    /// </summary>
    private static void EnsureSupportedKeyType()
    {
        if (typeof(TKey) != typeof(ulong))
        {
            throw new NotSupportedException(
                $"{typeof(LibraDexSortedSet<TKey>).Name} does not yet have a promoted physical codec for '{typeof(TKey).FullName}'. " +
                $"The first production slice supports '{typeof(ulong).FullName}'.");
        }
    }

    /// <summary>
    /// Reinterprets one validated UInt64 key without boxing or allocating an encoded-key buffer.<br/>
    /// </summary>
    /// <param name="key">The public key value.<br/></param>
    /// <returns>The UInt64 physical key bits.<br/></returns>
    private static ulong Encode(TKey key)
    {
        EnsureSupportedKeyType();
        return Unsafe.As<TKey, ulong>(ref key);
    }
}
