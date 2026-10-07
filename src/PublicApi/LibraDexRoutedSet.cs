using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LibraDex;

/// <summary>
/// Provides a route-ordered mutable set of distinct keys over LibraDex memory or file storage.<br/>
/// Radix routers retain ordered key regions, while keys within each leaf remain in physical end-fill order so ordinary insertion and removal do not shift a sorted suffix.<br/>
/// Choose <see cref="LibraDexSortedSet{TKey}"/> when callers require total ascending or descending traversal; this type exposes router-major physical traversal only.<br/>
/// </summary>
/// <remarks>
/// PARKED R&amp;D FEATURE: this type is intentionally assembly-internal, incomplete, unsupported, and excluded from LibraDex's public API contract.<br/>
/// Its implementation and harness coverage are retained so the set workstream can resume without losing validated design and performance evidence.<br/>
/// </remarks>
/// <typeparam name="TKey">The key type; this release supports <see cref="ulong"/>.<br/></typeparam>
internal sealed class LibraDexRoutedSet<TKey> : IDisposable
{
    private UInt64SetPrototype? _uint64Storage;
    private readonly string? _filePath;

    private LibraDexRoutedSet(UInt64SetPrototype storage, string? filePath)
    {
        _uint64Storage = storage;
        _filePath = filePath;
    }

    /// <summary>
    /// Gets the exact number of distinct keys currently present.<br/>
    /// The value is maintained at mutation publication and does not enumerate the routed topology.<br/>
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
    /// Creates an empty process-local routed set.<br/>
    /// Memory and file backings use the same compact leaf, router, and root formats, but memory storage has no reopen lifecycle.<br/>
    /// </summary>
    /// <returns>An empty memory-backed routed set.<br/></returns>
    public static LibraDexRoutedSet<TKey> CreateMemory()
    {
        EnsureSupportedKeyType();
        return new LibraDexRoutedSet<TKey>(UInt64SetPrototype.CreateRoutedMemory(), filePath: null);
    }

    /// <summary>
    /// Creates an empty durable routed set at a new file path.<br/>
    /// Creation fails when the target already exists so an unrelated set cannot be overwritten implicitly.<br/>
    /// </summary>
    /// <param name="path">The new routed-set file path.<br/></param>
    /// <returns>An empty file-backed routed set.<br/></returns>
    public static LibraDexRoutedSet<TKey> Create(string path)
    {
        EnsureSupportedKeyType();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        return new LibraDexRoutedSet<TKey>(UInt64SetPrototype.CreateRouted(fullPath), fullPath);
    }

    /// <summary>
    /// Opens an existing durable routed set and validates its distinct routed root kind before exposing it.<br/>
    /// A sorted or counted set file is rejected rather than silently changing traversal or mutation semantics.<br/>
    /// </summary>
    /// <param name="path">The existing routed-set file path.<br/></param>
    /// <returns>The reopened file-backed routed set.<br/></returns>
    public static LibraDexRoutedSet<TKey> Open(string path)
    {
        EnsureSupportedKeyType();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        return new LibraDexRoutedSet<TKey>(UInt64SetPrototype.OpenRouted(fullPath), fullPath);
    }

    /// <summary>
    /// Adds one key when it is not already present.<br/>
    /// The leaf-local mutation appends into free physical space and performs no sorted suffix movement; concurrent callers are linearized by the set owner.<br/>
    /// </summary>
    /// <param name="key">The key to add.<br/></param>
    /// <returns><see langword="true"/> when the key became newly present; otherwise <see langword="false"/>.<br/></returns>
    public bool TryAdd(TKey key) => Storage.TryAdd(Encode(key));

    /// <summary>
    /// Tests whether one key is present through a bounded radix walk and one exact scan of the selected 4 KiB routed leaf.<br/>
    /// The UInt64 leaf lookup uses packed four-bit rejection fingerprints with an AVX2 candidate-mask route when available, then proves every candidate by exact full-key equality.<br/>
    /// </summary>
    /// <param name="key">The key to locate.<br/></param>
    /// <returns><see langword="true"/> when the key is present.<br/></returns>
    public bool Contains(TKey key) => Storage.Contains(Encode(key));

    /// <summary>
    /// Removes one key when present by moving only the final live leaf key into the released physical position.<br/>
    /// A removal miss does not change cardinality or generation.<br/>
    /// </summary>
    /// <param name="key">The key to remove.<br/></param>
    /// <returns><see langword="true"/> when a present key was removed.<br/></returns>
    public bool Remove(TKey key) => Storage.Remove(Encode(key));

    /// <summary>
    /// Clears the complete set through one root publication and retires its non-root topology for reuse.<br/>
    /// The operation does not perform key-by-key deletion.<br/>
    /// </summary>
    public void Clear() => Storage.Clear();

    /// <summary>
    /// Copies every key into a caller-owned span in router-major physical order.<br/>
    /// Router regions are visited ascending, but keys within a leaf retain end-fill order; the result is intentionally not a total key ordering.<br/>
    /// </summary>
    /// <param name="destination">The destination span, which must fit the captured <see cref="Count"/>.<br/></param>
    /// <returns>The number of keys copied.<br/></returns>
    public int CopyTo(Span<TKey> destination)
    {
        EnsureSupportedKeyType();
        ref TKey first = ref MemoryMarshal.GetReference(destination);
        ref ulong firstUInt64 = ref Unsafe.As<TKey, ulong>(ref first);
        Span<ulong> encodedDestination = MemoryMarshal.CreateSpan(ref firstUInt64, destination.Length);
        return Storage.CopyTo(encodedDestination);
    }

    /// <summary>
    /// Materializes the current keys into a new array in router-major physical order.<br/>
    /// Prefer <see cref="CopyTo(Span{TKey})"/> when the caller already owns a reusable result buffer; choose <see cref="LibraDexSortedSet{TKey}"/> for total ordering.<br/>
    /// </summary>
    /// <returns>A newly allocated exact-length router-major key array.<br/></returns>
    public TKey[] ToArray()
    {
        ulong capturedCount = Count;
        if (capturedCount > int.MaxValue)
            throw new InvalidOperationException("The set is too large to materialize into one managed array; use a bounded traversal API.");

        TKey[] result = new TKey[(int)capturedCount];
        CopyTo(result);
        return result;
    }

    /// <summary>
    /// Captures internal topology and arena accounting after benchmark timing has stopped.<br/>
    /// The diagnostic is intentionally assembly-internal so physical implementation evidence does not become part of the public collection contract.<br/>
    /// </summary>
    /// <returns>The current routed topology and backing-storage snapshot.<br/></returns>
    internal UInt64SetTopologyStorageSnapshot GetTopologyStorageSnapshot() =>
        Storage.GetTopologyStorageSnapshot();

    /// <summary>
    /// Releases the owned memory arena or durable file handle.<br/>
    /// Disposing a file-backed routed set leaves its current authoritative image reopenable.<br/>
    /// </summary>
    public void Dispose()
    {
        UInt64SetPrototype? storage = Interlocked.Exchange(ref _uint64Storage, null);
        storage?.Dispose();
    }

    private UInt64SetPrototype Storage =>
        _uint64Storage ?? throw new ObjectDisposedException(GetType().FullName);

    /// <summary>
    /// Verifies that the closed generic handle has a promoted routed physical key codec.<br/>
    /// Unsupported types fail at creation/open rather than silently boxing, stringifying, or changing equality semantics.<br/>
    /// </summary>
    private static void EnsureSupportedKeyType()
    {
        if (typeof(TKey) != typeof(ulong))
        {
            throw new NotSupportedException(
                $"{typeof(LibraDexRoutedSet<TKey>).Name} does not yet have a promoted physical codec for '{typeof(TKey).FullName}'. " +
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
