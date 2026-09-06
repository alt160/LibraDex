using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LibraDex;

/// <summary>
/// Selects exact or caller-bounded occurrence semantics for a <see cref="LibraDexCountedSet{TKey}"/>.<br/>
/// </summary>
/// <remarks>
/// PARKED R&amp;D FEATURE: this type is intentionally assembly-internal and is not part of LibraDex's supported public API.<br/>
/// </remarks>
internal enum LibraDexCountedSetMode
{
    /// <summary>
    /// Retains every occurrence in a checked UInt64 counter.<br/>
    /// </summary>
    Exact = 0,

    /// <summary>
    /// Retains each key's count only through a caller-selected ceiling.<br/>
    /// Further increments are successful no-ops, which is useful for bounded questions such as one, two, or two-or-more.<br/>
    /// </summary>
    Saturating = 1
}

/// <summary>
/// Defines counted-set creation policy without charging presence-only <see cref="LibraDexSortedSet{TKey}"/> instances for counters.<br/>
/// </summary>
/// <remarks>
/// PARKED R&amp;D FEATURE: this type is intentionally assembly-internal and is not part of LibraDex's supported public API.<br/>
/// </remarks>
internal readonly record struct LibraDexCountedSetOptions
{
    private LibraDexCountedSetOptions(LibraDexCountedSetMode mode, ulong saturationCeiling)
    {
        Mode = mode;
        SaturationCeiling = saturationCeiling;
    }

    /// <summary>
    /// Gets whether the set retains exact or capped per-key counts.<br/>
    /// </summary>
    public LibraDexCountedSetMode Mode { get; }

    /// <summary>
    /// Gets the inclusive per-key cap for saturating mode, or zero for exact mode.<br/>
    /// </summary>
    public ulong SaturationCeiling { get; }

    /// <summary>
    /// Gets the exact-counting policy.<br/>
    /// </summary>
    public static LibraDexCountedSetOptions Exact => default;

    /// <summary>
    /// Creates a compact saturating policy with an inclusive per-key ceiling.<br/>
    /// A ceiling of two is the natural duplicate-detection profile: zero, one, or two-or-more.<br/>
    /// </summary>
    /// <param name="ceiling">The inclusive cap, which must be at least two.<br/></param>
    /// <returns>The validated saturating policy.<br/></returns>
    public static LibraDexCountedSetOptions Saturating(ulong ceiling = 2)
    {
        if (ceiling < 2)
            throw new ArgumentOutOfRangeException(nameof(ceiling), ceiling, "A saturating counted set requires a ceiling of at least two; use LibraDexSortedSet<TKey> or LibraDexRoutedSet<TKey> for presence-only behavior.");
        return new LibraDexCountedSetOptions(LibraDexCountedSetMode.Saturating, ceiling);
    }

    /// <summary>
    /// Resolves the internal creation ceiling after validating enum and exact-mode invariants.<br/>
    /// </summary>
    /// <returns>Zero for exact mode or the positive saturation ceiling.<br/></returns>
    internal ulong ResolveCreationCeiling()
    {
        return Mode switch
        {
            LibraDexCountedSetMode.Exact when SaturationCeiling == 0 => 0,
            LibraDexCountedSetMode.Saturating when SaturationCeiling >= 2 => SaturationCeiling,
            LibraDexCountedSetMode.Exact => throw new ArgumentException("Exact counted-set options cannot specify a saturation ceiling."),
            LibraDexCountedSetMode.Saturating => throw new ArgumentException("Saturating counted-set options require a ceiling of at least two."),
            _ => throw new ArgumentOutOfRangeException(nameof(Mode), Mode, "The counted-set mode is not recognized.")
        };
    }
}

/// <summary>
/// Represents one naturally ordered counted-set key and its positive exact or capped stored count.<br/>
/// </summary>
/// <remarks>
/// PARKED R&amp;D FEATURE: this type is intentionally assembly-internal and is not part of LibraDex's supported public API.<br/>
/// </remarks>
/// <typeparam name="TKey">The key type.<br/></typeparam>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct LibraDexCountedSetEntry<TKey>(TKey Key, ulong Count);

/// <summary>
/// Provides an ordered mutable occurrence-counting set over LibraDex memory or file storage.<br/>
/// Exact and saturating modes use distinct persisted leaf contracts; both share the same radix routing, reuse-before-extension allocator, and coherent publication model.<br/>
/// The current experimental slice supports <see cref="ulong"/> keys.<br/>
/// </summary>
/// <remarks>
/// PARKED R&amp;D FEATURE: this type is intentionally assembly-internal, incomplete, unsupported, and excluded from LibraDex's public API contract.<br/>
/// Its implementation and harness coverage are retained so the set workstream can resume without losing validated design and performance evidence.<br/>
/// </remarks>
/// <typeparam name="TKey">The key type; this release supports <see cref="ulong"/>.<br/></typeparam>
internal sealed class LibraDexCountedSet<TKey> : IDisposable
{
    private UInt64CountedSetPrototype? _uint64Storage;
    private readonly string? _filePath;

    private LibraDexCountedSet(UInt64CountedSetPrototype storage, string? filePath)
    {
        _uint64Storage = storage;
        _filePath = filePath;
    }

    /// <summary>
    /// Gets the exact number of distinct keys having a positive stored count.<br/>
    /// </summary>
    public ulong DistinctCount => Storage.DistinctCount;

    /// <summary>
    /// Gets the exact sum of retained counters.<br/>
    /// Exact mode includes every accepted occurrence; saturating mode excludes increments above each key's ceiling.<br/>
    /// </summary>
    public ulong RetainedOccurrenceCount => Storage.RetainedOccurrenceCount;

    /// <summary>
    /// Gets the successful logical mutation generation.<br/>
    /// </summary>
    public ulong Generation => Storage.Generation;

    /// <summary>
    /// Gets whether per-key counters stop at a caller-selected ceiling.<br/>
    /// </summary>
    public bool IsSaturating => Storage.IsSaturating;

    /// <summary>
    /// Gets the inclusive saturation ceiling, or <see cref="ulong.MaxValue"/> for exact mode.<br/>
    /// </summary>
    public ulong MaximumStoredCount => Storage.MaximumStoredCount;

    /// <summary>
    /// Gets the selected persisted counter width in bits.<br/>
    /// </summary>
    public int CounterBits => Storage.CounterBits;

    /// <summary>
    /// Gets whether this set uses process-local memory rather than a reopenable file.<br/>
    /// </summary>
    public bool IsMemoryBacked => Storage.BackingKind == DataKernelBackingKind.Memory;

    /// <summary>
    /// Gets the canonical durable path, or <see langword="null"/> for memory backing.<br/>
    /// </summary>
    public string? FilePath => _filePath;

    /// <summary>
    /// Creates an empty memory-backed counted set.<br/>
    /// </summary>
    /// <param name="options">Exact or saturating creation policy; the default is exact.<br/></param>
    /// <returns>The initialized memory-backed counted set.<br/></returns>
    public static LibraDexCountedSet<TKey> CreateMemory(LibraDexCountedSetOptions options = default)
    {
        EnsureSupportedKeyType();
        return new LibraDexCountedSet<TKey>(UInt64CountedSetPrototype.CreateMemory(options.ResolveCreationCeiling()), filePath: null);
    }

    /// <summary>
    /// Creates an empty durable counted set at a new path.<br/>
    /// </summary>
    /// <param name="path">The new set file path.<br/></param>
    /// <param name="options">Exact or saturating creation policy; the default is exact.<br/></param>
    /// <returns>The initialized file-backed counted set.<br/></returns>
    public static LibraDexCountedSet<TKey> Create(string path, LibraDexCountedSetOptions options = default)
    {
        EnsureSupportedKeyType();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        return new LibraDexCountedSet<TKey>(UInt64CountedSetPrototype.Create(fullPath, options.ResolveCreationCeiling()), fullPath);
    }

    /// <summary>
    /// Opens an existing durable counted set and restores its persisted exact or saturation policy.<br/>
    /// </summary>
    /// <param name="path">The existing counted-set path.<br/></param>
    /// <returns>The reopened file-backed counted set.<br/></returns>
    public static LibraDexCountedSet<TKey> Open(string path)
    {
        EnsureSupportedKeyType();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        return new LibraDexCountedSet<TKey>(UInt64CountedSetPrototype.Open(fullPath), fullPath);
    }

    /// <summary>
    /// Adds one occurrence and returns the new exact or capped stored count.<br/>
    /// </summary>
    /// <param name="key">The key whose occurrence count should increase.<br/></param>
    /// <returns>The post-operation stored count.<br/></returns>
    public ulong AddOccurrence(TKey key) => Storage.AddOccurrence(Encode(key));

    /// <summary>
    /// Reads the exact or capped stored count for one key.<br/>
    /// </summary>
    /// <param name="key">The key to locate.<br/></param>
    /// <returns>The stored count, or zero when absent.<br/></returns>
    public ulong GetCount(TKey key) => Storage.GetCount(Encode(key));

    /// <summary>
    /// Tests whether one key has a stored count at least as large as a positive threshold.<br/>
    /// In saturating mode, thresholds above <see cref="MaximumStoredCount"/> can never match.<br/>
    /// </summary>
    /// <param name="key">The key to test.<br/></param>
    /// <param name="threshold">The positive inclusive threshold.<br/></param>
    /// <returns><see langword="true"/> when the stored count reaches the threshold.<br/></returns>
    public bool ContainsAtLeast(TKey key, ulong threshold)
    {
        if (threshold == 0)
            throw new ArgumentOutOfRangeException(nameof(threshold), threshold, "ContainsAtLeast requires a positive threshold.");
        return threshold <= MaximumStoredCount && GetCount(key) >= threshold;
    }

    /// <summary>
    /// Removes one retained occurrence and returns the new stored count.<br/>
    /// </summary>
    /// <param name="key">The key whose retained count should decrease.<br/></param>
    /// <returns>The post-operation count, or zero when absent or removed.<br/></returns>
    public ulong RemoveOccurrence(TKey key) => Storage.RemoveOccurrence(Encode(key));

    /// <summary>
    /// Removes one key and its complete exact or capped stored count.<br/>
    /// </summary>
    /// <param name="key">The key to remove.<br/></param>
    /// <returns>The prior stored count, or zero when absent.<br/></returns>
    public ulong Remove(TKey key) => Storage.Remove(Encode(key));

    /// <summary>
    /// Clears the complete counted set through one root publication and reusable-topology retirement.<br/>
    /// </summary>
    public void Clear() => Storage.Clear();

    /// <summary>
    /// Copies every key/count entry into a caller-owned span in ascending or descending natural key order.<br/>
    /// The UInt64 first slice reinterprets layout-compatible entries without per-entry boxing or conversion allocations.<br/>
    /// </summary>
    /// <param name="destination">The destination span, which must fit the captured <see cref="DistinctCount"/>.<br/></param>
    /// <param name="descending">Whether to copy in descending rather than ascending order.<br/></param>
    /// <returns>The number of entries copied.<br/></returns>
    public int CopyTo(Span<LibraDexCountedSetEntry<TKey>> destination, bool descending = false)
    {
        EnsureSupportedKeyType();
        ref LibraDexCountedSetEntry<TKey> first = ref MemoryMarshal.GetReference(destination);
        ref UInt64CountedSetPrototypeEntry firstPhysical = ref Unsafe.As<LibraDexCountedSetEntry<TKey>, UInt64CountedSetPrototypeEntry>(ref first);
        Span<UInt64CountedSetPrototypeEntry> physical = MemoryMarshal.CreateSpan(ref firstPhysical, destination.Length);
        return Storage.CopyTo(physical, descending);
    }

    /// <summary>
    /// Materializes all current key/count entries into a new naturally ordered array.<br/>
    /// Prefer <see cref="CopyTo(Span{LibraDexCountedSetEntry{TKey}}, bool)"/> when the caller owns a reusable buffer.<br/>
    /// </summary>
    /// <param name="descending">Whether to return descending rather than ascending order.<br/></param>
    /// <returns>A newly allocated exact-length ordered entry array.<br/></returns>
    public LibraDexCountedSetEntry<TKey>[] ToArray(bool descending = false)
    {
        ulong capturedCount = DistinctCount;
        if (capturedCount > int.MaxValue)
            throw new InvalidOperationException("The counted set is too large to materialize into one managed array; use bounded traversal.");
        LibraDexCountedSetEntry<TKey>[] result = new LibraDexCountedSetEntry<TKey>[(int)capturedCount];
        CopyTo(result, descending);
        return result;
    }

    /// <summary>
    /// Releases the owned memory arena or durable file handle.<br/>
    /// </summary>
    public void Dispose()
    {
        UInt64CountedSetPrototype? storage = Interlocked.Exchange(ref _uint64Storage, null);
        storage?.Dispose();
    }

    private UInt64CountedSetPrototype Storage =>
        _uint64Storage ?? throw new ObjectDisposedException(GetType().FullName);

    /// <summary>
    /// Verifies that the closed generic handle has a promoted physical key codec.<br/>
    /// </summary>
    private static void EnsureSupportedKeyType()
    {
        if (typeof(TKey) != typeof(ulong))
        {
            throw new NotSupportedException(
                $"{typeof(LibraDexCountedSet<TKey>).Name} does not yet have a promoted physical codec for '{typeof(TKey).FullName}'. " +
                $"The first production slice supports '{typeof(ulong).FullName}'.");
        }
    }

    /// <summary>
    /// Reinterprets one validated UInt64 key without boxing or allocating an encoded buffer.<br/>
    /// </summary>
    private static ulong Encode(TKey key)
    {
        EnsureSupportedKeyType();
        return Unsafe.As<TKey, ulong>(ref key);
    }
}
