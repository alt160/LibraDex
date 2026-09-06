using System.Buffers;
using System.Collections;
using System.Runtime.CompilerServices;

namespace LibraDex;

internal interface ILibraDexIdentityInverseLookup
{
    bool TryIdentityExistsFromInverse(object identity, out bool exists);
}

internal enum LibraDexExistenceCheckKind
{
    Keys,
    Identities
}

/// <summary>
/// Represents one caller-supplied key/identity association used by the allocation-conscious entry-check APIs.<br/>
/// The value is a readonly struct so arrays, spans, and pooled buffers can carry candidate entries without allocating one object per association.<br/>
/// </summary>
/// <typeparam name="TKey">The index key type.<br/></typeparam>
/// <typeparam name="TIdentity">The index identity type.<br/></typeparam>
/// <param name="Key">The candidate indexed key.<br/></param>
/// <param name="Identity">The candidate identity associated with the key.<br/></param>
public readonly record struct LibraDexIndexEntry<TKey, TIdentity>(TKey Key, TIdentity Identity);

/// <summary>
/// Provides key-membership operations for one opened LibraDex index.<br/>
/// The facade is a readonly struct containing only the opened index reference; accessing `index.Keys` does not allocate a wrapper object.<br/>
/// </summary>
/// <typeparam name="TKey">The public key type accepted by the index.<br/></typeparam>
/// <typeparam name="TIdentity">The public identity type stored by the index.<br/></typeparam>
public readonly partial struct LibraDexIndexKeys<TKey, TIdentity>
{
    private readonly IIndex index;

    internal LibraDexIndexKeys(IIndex index)
    {
        this.index = index ?? throw new ArgumentNullException(nameof(index));
    }

    /// <summary>
    /// Gets duplicate-key collection and reader operations for this index.<br/>
    /// </summary>
    public LibraDexIndexKeyDuplicates<TKey, TIdentity> Duplicates => new(index);

    /// <summary>
    /// Gets singleton-key collection and reader operations for this index.<br/>
    /// A singleton key is associated with exactly one distinct identity.<br/>
    /// </summary>
    public LibraDexIndexKeySingletons<TKey, TIdentity> Singletons => new(index);

    /// <summary>
    /// Determines whether at least one entry is stored under the supplied key.<br/>
    /// The operation uses the index's ordered point-lookup route and stops after the first identity; null and empty keys use their dedicated routes when the index family supports them.<br/>
    /// </summary>
    /// <param name="key">The key to test.<br/></param>
    /// <returns><see langword="true"/> when at least one entry exists under the key.<br/></returns>
    public bool Exists(TKey key) => LibraDexExistenceExecution.KeyExists(index, key);

    /// <summary>
    /// Determines whether at least one supplied key exists in this index.<br/>
    /// Candidate evaluation stops on the first hit and does not allocate a result collection.<br/>
    /// </summary>
    /// <param name="keys">The candidate keys to test.<br/></param>
    /// <returns><see langword="true"/> when any candidate key exists; an empty input returns <see langword="false"/>.<br/></returns>
    public bool ExistsAny(IReadOnlyList<TKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        for (int i = 0; i < keys.Count; i++)
        {
            if (Exists(keys[i]))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Determines whether every supplied key exists in this index.<br/>
    /// Candidate evaluation stops on the first miss; an empty input returns <see langword="true"/> to match normal .NET `All` semantics.<br/>
    /// </summary>
    /// <param name="keys">The candidate keys to test.<br/></param>
    /// <returns><see langword="true"/> when every candidate key exists.<br/></returns>
    public bool ExistsAll(IReadOnlyList<TKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        for (int i = 0; i < keys.Count; i++)
        {
            if (!Exists(keys[i]))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Writes one positional existence result for every supplied key into caller-owned storage.<br/>
    /// LibraDex does not copy or retain the keys, and the caller controls whether the Boolean destination is stack, pooled, or ordinary managed memory.<br/>
    /// </summary>
    /// <param name="keys">The candidate keys in result order.<br/></param>
    /// <param name="results">The destination whose first `keys.Count` cells receive the existence states.<br/></param>
    /// <returns>The number of result cells written.<br/></returns>
    public int Check(IReadOnlyList<TKey> keys, Span<bool> results)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (results.Length < keys.Count)
            throw new ArgumentException("The result span must contain at least one cell for every candidate key.", nameof(results));

        for (int i = 0; i < keys.Count; i++)
            results[i] = Exists(keys[i]);

        return keys.Count;
    }

    /// <summary>
    /// Writes one positional existence result for every supplied key span cell into caller-owned storage.<br/>
    /// This overload keeps array slices and pooled candidate buffers allocation-free while preserving input order exactly.<br/>
    /// </summary>
    /// <param name="keys">The candidate key span.<br/></param>
    /// <param name="results">The destination whose first `keys.Length` cells receive the existence states.<br/></param>
    /// <returns>The number of result cells written.<br/></returns>
    public int CheckSpan(ReadOnlySpan<TKey> keys, Span<bool> results)
    {
        if (results.Length < keys.Length)
            throw new ArgumentException("The result span must contain at least one cell for every candidate key.", nameof(results));

        for (int i = 0; i < keys.Length; i++)
            results[i] = Exists(keys[i]);

        return keys.Length;
    }

    /// <summary>
    /// Opens a bounded-buffer reader that reports candidate keys and existence states in source order.<br/>
    /// The reader rents one candidate buffer and one Boolean buffer, checks each bounded batch, and returns both buffers when disposed instead of materializing a key/result tuple collection.<br/>
    /// </summary>
    /// <param name="keys">The candidate key stream to inspect.<br/></param>
    /// <param name="batchSize">The maximum candidates retained by the reader at one time; the default is 256.<br/></param>
    /// <returns>A forward-only existence-check reader.<br/></returns>
    public LibraDexExistenceCheckReader<TKey> OpenCheckReader(IEnumerable<TKey> keys, int batchSize = 256)
        => new(index, keys, LibraDexExistenceCheckKind.Keys, batchSize);
}

/// <summary>
/// Provides identity-membership operations for one opened LibraDex index.<br/>
/// A fresh configured inverse is preferred; otherwise batch operations walk the identity-only forward stream once with bounded pooled lookup scratch.<br/>
/// </summary>
/// <typeparam name="TKey">The public key type accepted by the index.<br/></typeparam>
/// <typeparam name="TIdentity">The public identity type stored by the index.<br/></typeparam>
public readonly partial struct LibraDexIndexIdentities<TKey, TIdentity>
{
    private readonly IIndex index;

    internal LibraDexIndexIdentities(IIndex index)
    {
        this.index = index ?? throw new ArgumentNullException(nameof(index));
    }

    /// <summary>
    /// Gets duplicate-identity collection and reader operations for this index.<br/>
    /// </summary>
    public LibraDexIndexIdentityDuplicates<TKey, TIdentity> Duplicates => new(index);

    /// <summary>
    /// Gets singleton-identity collection and reader operations for this index.<br/>
    /// A singleton identity is associated with exactly one distinct key.<br/>
    /// </summary>
    public LibraDexIndexIdentitySingletons<TKey, TIdentity> Singletons => new(index);

    /// <summary>
    /// Determines whether the supplied identity has at least one visible entry in this index.<br/>
    /// A configured inverse may answer directly; the fallback streams encoded-index identities without materializing a key collection and stops on the first match.<br/>
    /// </summary>
    /// <param name="identity">The identity to test.<br/></param>
    /// <returns><see langword="true"/> when the identity occurs under an ordinary, null, or empty key route.<br/></returns>
    public bool Exists(TIdentity identity) => LibraDexExistenceExecution.IdentityExists(index, identity);

    /// <summary>
    /// Determines whether at least one supplied identity exists in this index.<br/>
    /// Without an inverse, LibraDex builds bounded pooled candidate-ordinal scratch and walks the forward identity stream once instead of scanning once per candidate.<br/>
    /// </summary>
    /// <param name="identities">The candidate identities to test.<br/></param>
    /// <returns><see langword="true"/> when any supplied identity exists; an empty input returns <see langword="false"/>.<br/></returns>
    public bool ExistsAny(IReadOnlyList<TIdentity> identities)
    {
        ArgumentNullException.ThrowIfNull(identities);
        if (identities.Count == 0)
            return false;

        bool[]? rented = null;
        Span<bool> results = identities.Count <= 256
            ? stackalloc bool[identities.Count]
            : (rented = ArrayPool<bool>.Shared.Rent(identities.Count)).AsSpan(0, identities.Count);
        try
        {
            Check(identities, results);
            for (int i = 0; i < identities.Count; i++)
            {
                if (results[i])
                    return true;
            }

            return false;
        }
        finally
        {
            if (rented is not null)
                ArrayPool<bool>.Shared.Return(rented, clearArray: true);
        }
    }

    /// <summary>
    /// Determines whether every supplied identity exists in this index.<br/>
    /// An empty input returns <see langword="true"/>; configured inverse lookups or one bounded forward walk resolve all non-empty candidates.<br/>
    /// </summary>
    /// <param name="identities">The candidate identities to test.<br/></param>
    /// <returns><see langword="true"/> when every supplied identity exists.<br/></returns>
    public bool ExistsAll(IReadOnlyList<TIdentity> identities)
    {
        ArgumentNullException.ThrowIfNull(identities);
        if (identities.Count == 0)
            return true;

        bool[]? rented = null;
        Span<bool> results = identities.Count <= 256
            ? stackalloc bool[identities.Count]
            : (rented = ArrayPool<bool>.Shared.Rent(identities.Count)).AsSpan(0, identities.Count);
        try
        {
            Check(identities, results);
            for (int i = 0; i < identities.Count; i++)
            {
                if (!results[i])
                    return false;
            }

            return true;
        }
        finally
        {
            if (rented is not null)
                ArrayPool<bool>.Shared.Return(rented, clearArray: true);
        }
    }

    /// <summary>
    /// Writes one positional existence result for every supplied identity into caller-owned storage.<br/>
    /// The input identities are never copied into a result collection; fallback lookup scratch stores pooled candidate ordinals and is released before this method returns.<br/>
    /// </summary>
    /// <param name="identities">The candidate identities in result order.<br/></param>
    /// <param name="results">The destination whose first `identities.Count` cells receive the existence states.<br/></param>
    /// <returns>The number of result cells written.<br/></returns>
    public int Check(IReadOnlyList<TIdentity> identities, Span<bool> results)
        => LibraDexExistenceExecution.CheckIdentities(index, identities, results);

    /// <summary>
    /// Writes one positional existence result for every supplied identity span cell into caller-owned storage.<br/>
    /// This overload supports arrays, stack spans, and pooled slices without retaining another reference collection.<br/>
    /// </summary>
    /// <param name="identities">The candidate identity span.<br/></param>
    /// <param name="results">The destination whose first `identities.Length` cells receive the existence states.<br/></param>
    /// <returns>The number of result cells written.<br/></returns>
    public int CheckSpan(ReadOnlySpan<TIdentity> identities, Span<bool> results)
        => LibraDexExistenceExecution.CheckIdentities(index, identities, results);

    /// <summary>
    /// Opens a bounded-buffer reader that reports candidate identities and existence states in source order.<br/>
    /// Each batch uses the configured inverse or one forward identity walk; the reader never retains the complete candidate source or produces a tuple collection.<br/>
    /// </summary>
    /// <param name="identities">The candidate identity stream to inspect.<br/></param>
    /// <param name="batchSize">The maximum candidates retained by the reader at one time; the default is 256.<br/></param>
    /// <returns>A forward-only existence-check reader.<br/></returns>
    public LibraDexExistenceCheckReader<TIdentity> OpenCheckReader(IEnumerable<TIdentity> identities, int batchSize = 256)
        => new(index, identities, LibraDexExistenceCheckKind.Identities, batchSize);
}

/// <summary>
/// Provides exact key/identity association checks for one opened LibraDex index.<br/>
/// Entry checks seek the supplied key and test only that key's identity run; they do not require an identity inversion.<br/>
/// </summary>
/// <typeparam name="TKey">The public key type accepted by the index.<br/></typeparam>
/// <typeparam name="TIdentity">The public identity type stored by the index.<br/></typeparam>
public readonly partial struct LibraDexIndexEntries<TKey, TIdentity>
{
    private readonly IIndex index;

    internal LibraDexIndexEntries(IIndex index)
    {
        this.index = index ?? throw new ArgumentNullException(nameof(index));
    }

    /// <summary>
    /// Gets complete entry tuples selected by duplicated key.<br/>
    /// </summary>
    public LibraDexEntriesByDuplicateKey<TKey, TIdentity> DuplicateKeys => new(index);

    /// <summary>
    /// Gets complete entry tuples selected by duplicated identity.<br/>
    /// </summary>
    public LibraDexEntriesByDuplicateIdentity<TKey, TIdentity> DuplicateIdentities => new(index);

    /// <summary>
    /// Gets complete entry tuples selected by keys associated with exactly one distinct identity.<br/>
    /// </summary>
    public LibraDexEntriesBySingletonKey<TKey, TIdentity> SingletonKeys => new(index);

    /// <summary>
    /// Gets complete entry tuples selected by identities associated with exactly one distinct key.<br/>
    /// </summary>
    public LibraDexEntriesBySingletonIdentity<TKey, TIdentity> SingletonIdentities => new(index);

    /// <summary>
    /// Determines whether the exact supplied key/identity association exists.<br/>
    /// Null and empty keys use their dedicated routes where supported, while ordinary keys seek only their matching ordered-key range.<br/>
    /// </summary>
    /// <param name="key">The candidate key.<br/></param>
    /// <param name="identity">The candidate identity associated with the key.<br/></param>
    /// <returns><see langword="true"/> when the exact entry exists.<br/></returns>
    public bool Exists(TKey key, TIdentity identity) => LibraDexExistenceExecution.EntryExists(index, key, identity);

    /// <summary>
    /// Determines whether the supplied key is associated with any identity other than the permitted identity.<br/>
    /// Fixed scalar shapes encode the typed key and identity directly and stop on the first different owner without boxing or materializing a result collection.<br/>
    /// </summary>
    /// <param name="key">The key whose ownership route is inspected.<br/></param>
    /// <param name="identity">The identity permitted to retain the key.<br/></param>
    /// <returns><see langword="true"/> when at least one different identity owns the key; otherwise <see langword="false"/>.<br/></returns>
    public bool ExistsOtherIdentity(TKey key, TIdentity identity)
        => LibraDexExistenceExecution.OtherIdentityExists(index, key, identity);

    /// <summary>
    /// Determines whether at least one supplied exact entry exists.<br/>
    /// Evaluation stops on the first hit and does not allocate a result collection.<br/>
    /// </summary>
    /// <param name="entries">The candidate entries to test.<br/></param>
    /// <returns><see langword="true"/> when any candidate entry exists; an empty input returns <see langword="false"/>.<br/></returns>
    public bool ExistsAny(IReadOnlyList<LibraDexIndexEntry<TKey, TIdentity>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        for (int i = 0; i < entries.Count; i++)
        {
            LibraDexIndexEntry<TKey, TIdentity> entry = entries[i];
            if (Exists(entry.Key, entry.Identity))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Determines whether every supplied exact entry exists.<br/>
    /// Evaluation stops on the first miss; an empty input returns <see langword="true"/>.<br/>
    /// </summary>
    /// <param name="entries">The candidate entries to test.<br/></param>
    /// <returns><see langword="true"/> when every candidate entry exists.<br/></returns>
    public bool ExistsAll(IReadOnlyList<LibraDexIndexEntry<TKey, TIdentity>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        for (int i = 0; i < entries.Count; i++)
        {
            LibraDexIndexEntry<TKey, TIdentity> entry = entries[i];
            if (!Exists(entry.Key, entry.Identity))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Writes one positional existence result for every supplied exact entry into caller-owned storage.<br/>
    /// The caller's entry collection is read in place and is not copied or retained by LibraDex.<br/>
    /// </summary>
    /// <param name="entries">The candidate entries in result order.<br/></param>
    /// <param name="results">The destination whose first `entries.Count` cells receive the existence states.<br/></param>
    /// <returns>The number of result cells written.<br/></returns>
    public int Check(IReadOnlyList<LibraDexIndexEntry<TKey, TIdentity>> entries, Span<bool> results)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (results.Length < entries.Count)
            throw new ArgumentException("The result span must contain at least one cell for every candidate entry.", nameof(results));

        for (int i = 0; i < entries.Count; i++)
        {
            LibraDexIndexEntry<TKey, TIdentity> entry = entries[i];
            results[i] = Exists(entry.Key, entry.Identity);
        }

        return entries.Count;
    }

    /// <summary>
    /// Writes one positional existence result for every supplied exact-entry span cell into caller-owned storage.<br/>
    /// This overload keeps stack and pooled candidate buffers allocation-free.<br/>
    /// </summary>
    /// <param name="entries">The candidate entry span.<br/></param>
    /// <param name="results">The destination whose first `entries.Length` cells receive the existence states.<br/></param>
    /// <returns>The number of result cells written.<br/></returns>
    public int CheckSpan(ReadOnlySpan<LibraDexIndexEntry<TKey, TIdentity>> entries, Span<bool> results)
    {
        if (results.Length < entries.Length)
            throw new ArgumentException("The result span must contain at least one cell for every candidate entry.", nameof(results));

        for (int i = 0; i < entries.Length; i++)
            results[i] = Exists(entries[i].Key, entries[i].Identity);

        return entries.Length;
    }

    /// <summary>
    /// Opens a bounded-buffer reader that reports exact candidate entries and existence states in source order.<br/>
    /// The reader rents bounded buffers and returns them on disposal instead of materializing a second complete entry/result collection.<br/>
    /// </summary>
    /// <param name="entries">The candidate entry stream to inspect.<br/></param>
    /// <param name="batchSize">The maximum candidates retained by the reader at one time; the default is 256.<br/></param>
    /// <returns>A forward-only exact-entry existence-check reader.<br/></returns>
    public LibraDexEntryExistenceCheckReader<TKey, TIdentity> OpenCheckReader(
        IEnumerable<LibraDexIndexEntry<TKey, TIdentity>> entries,
        int batchSize = 256)
        => new(index, entries, batchSize);
}

/// <summary>
/// Streams candidate values and their existence states using bounded pooled buffers.<br/>
/// The reader preserves source order and exposes only the current candidate/state pair, avoiding a durable tuple or Boolean result collection.<br/>
/// </summary>
/// <typeparam name="TValue">The candidate key or identity type.<br/></typeparam>
public sealed class LibraDexExistenceCheckReader<TValue> : IDisposable
{
    private readonly IIndex index;
    private readonly IEnumerator<TValue> source;
    private readonly LibraDexExistenceCheckKind kind;
    private readonly int batchSize;
    private TValue[]? values;
    private bool[]? states;
    private int count;
    private int position = -1;
    private bool completed;
    private bool disposed;

    internal LibraDexExistenceCheckReader(
        IIndex index,
        IEnumerable<TValue> source,
        LibraDexExistenceCheckKind kind,
        int batchSize)
    {
        this.index = index ?? throw new ArgumentNullException(nameof(index));
        ArgumentNullException.ThrowIfNull(source);
        if (batchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(batchSize), batchSize, "The check-reader batch size must be positive.");

        this.source = source.GetEnumerator();
        this.kind = kind;
        this.batchSize = batchSize;
    }

    /// <summary>
    /// Gets the current candidate value after a successful <see cref="Read"/> call.<br/>
    /// </summary>
    public TValue Value => position >= 0 && position < count && values is not null
        ? values[position]
        : throw new InvalidOperationException("Call Read before accessing the current check value.");

    /// <summary>
    /// Gets whether the current candidate exists after a successful <see cref="Read"/> call.<br/>
    /// </summary>
    public bool Exists => position >= 0 && position < count && states is not null
        ? states[position]
        : throw new InvalidOperationException("Call Read before accessing the current existence state.");

    /// <summary>
    /// Advances to the next candidate/state pair, refilling and checking one bounded batch when necessary.<br/>
    /// </summary>
    /// <returns><see langword="true"/> when a current candidate/state pair is available.<br/></returns>
    public bool Read()
    {
        ThrowIfDisposed();
        if (position + 1 < count)
        {
            position++;
            return true;
        }

        if (completed)
            return false;

        values ??= ArrayPool<TValue>.Shared.Rent(batchSize);
        states ??= ArrayPool<bool>.Shared.Rent(batchSize);
        count = 0;
        while (count < batchSize && source.MoveNext())
            values[count++] = source.Current;

        if (count == 0)
        {
            completed = true;
            position = -1;
            return false;
        }

        Span<bool> resultSpan = states.AsSpan(0, count);
        if (kind == LibraDexExistenceCheckKind.Keys)
            LibraDexExistenceExecution.CheckKeys<TValue>(index, values.AsSpan(0, count), resultSpan);
        else
            LibraDexExistenceExecution.CheckIdentities<TValue>(index, values.AsSpan(0, count), resultSpan);

        position = 0;
        return true;
    }

    /// <summary>
    /// Disposes the source enumerator and returns rented candidate/result buffers.<br/>
    /// Reference-containing candidate buffers are cleared before returning so the pool does not extend caller object lifetimes.<br/>
    /// </summary>
    public void Dispose()
    {
        if (disposed)
            return;

        source.Dispose();
        if (values is not null)
            ArrayPool<TValue>.Shared.Return(values, RuntimeHelpers.IsReferenceOrContainsReferences<TValue>());
        if (states is not null)
            ArrayPool<bool>.Shared.Return(states, clearArray: true);

        values = null;
        states = null;
        disposed = true;
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(LibraDexExistenceCheckReader<TValue>));
    }
}

/// <summary>
/// Streams exact candidate entries and their existence states using bounded pooled buffers.<br/>
/// </summary>
/// <typeparam name="TKey">The candidate key type.<br/></typeparam>
/// <typeparam name="TIdentity">The candidate identity type.<br/></typeparam>
public sealed class LibraDexEntryExistenceCheckReader<TKey, TIdentity> : IDisposable
{
    private readonly IIndex index;
    private readonly IEnumerator<LibraDexIndexEntry<TKey, TIdentity>> source;
    private readonly int batchSize;
    private LibraDexIndexEntry<TKey, TIdentity>[]? entries;
    private bool[]? states;
    private int count;
    private int position = -1;
    private bool completed;
    private bool disposed;

    internal LibraDexEntryExistenceCheckReader(
        IIndex index,
        IEnumerable<LibraDexIndexEntry<TKey, TIdentity>> source,
        int batchSize)
    {
        this.index = index ?? throw new ArgumentNullException(nameof(index));
        ArgumentNullException.ThrowIfNull(source);
        if (batchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(batchSize), batchSize, "The check-reader batch size must be positive.");

        this.source = source.GetEnumerator();
        this.batchSize = batchSize;
    }

    /// <summary>
    /// Gets the current candidate entry after a successful <see cref="Read"/> call.<br/>
    /// </summary>
    public LibraDexIndexEntry<TKey, TIdentity> Entry => position >= 0 && position < count && entries is not null
        ? entries[position]
        : throw new InvalidOperationException("Call Read before accessing the current check entry.");

    /// <summary>
    /// Gets whether the current exact candidate entry exists after a successful <see cref="Read"/> call.<br/>
    /// </summary>
    public bool Exists => position >= 0 && position < count && states is not null
        ? states[position]
        : throw new InvalidOperationException("Call Read before accessing the current existence state.");

    /// <summary>
    /// Advances to the next candidate/state pair, refilling and checking one bounded batch when necessary.<br/>
    /// </summary>
    /// <returns><see langword="true"/> when a current candidate/state pair is available.<br/></returns>
    public bool Read()
    {
        ThrowIfDisposed();
        if (position + 1 < count)
        {
            position++;
            return true;
        }

        if (completed)
            return false;

        entries ??= ArrayPool<LibraDexIndexEntry<TKey, TIdentity>>.Shared.Rent(batchSize);
        states ??= ArrayPool<bool>.Shared.Rent(batchSize);
        count = 0;
        while (count < batchSize && source.MoveNext())
            entries[count++] = source.Current;

        if (count == 0)
        {
            completed = true;
            position = -1;
            return false;
        }

        LibraDexExistenceExecution.CheckEntries<TKey, TIdentity>(index, entries.AsSpan(0, count), states.AsSpan(0, count));
        position = 0;
        return true;
    }

    /// <summary>
    /// Disposes the source enumerator and returns rented candidate/result buffers.<br/>
    /// Reference-containing entry buffers are cleared before returning so pooled storage does not extend caller object lifetimes.<br/>
    /// </summary>
    public void Dispose()
    {
        if (disposed)
            return;

        source.Dispose();
        if (entries is not null)
            ArrayPool<LibraDexIndexEntry<TKey, TIdentity>>.Shared.Return(entries, RuntimeHelpers.IsReferenceOrContainsReferences<LibraDexIndexEntry<TKey, TIdentity>>());
        if (states is not null)
            ArrayPool<bool>.Shared.Return(states, clearArray: true);

        entries = null;
        states = null;
        disposed = true;
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(LibraDexEntryExistenceCheckReader<TKey, TIdentity>));
    }
}

internal static class LibraDexExistenceExecution
{
    internal static bool KeyExists<TKey>(IIndex index, TKey key)
    {
        IIdentityPrimitiveExecutor executor = index as IIdentityPrimitiveExecutor
            ?? throw new NotSupportedException($"Index '{index.Name}' does not expose primitive key lookup.");
        LibraDexIdentityPrimitiveRequest request = CreateKeyRequest(key, TakeLimit: 1);
        using IEnumerator<object> enumerator = executor.IterateIdentityPrimitive(request).GetEnumerator();
        return enumerator.MoveNext();
    }

    internal static bool IdentityExists<TIdentity>(IIndex index, TIdentity identity)
    {
        object? candidate = identity;
        if (candidate is null)
            throw new ArgumentNullException(nameof(identity));
        if (index is ILibraDexIdentityInverseLookup inverse && inverse.TryIdentityExistsFromInverse(candidate, out bool inverseExists))
            return inverseExists;

        IIdentityPrimitiveExecutor executor = index as IIdentityPrimitiveExecutor
            ?? throw new NotSupportedException($"Index '{index.Name}' does not expose identity iteration.");
        LibraDexExistenceValueComparer<TIdentity> comparer = LibraDexExistenceValueComparer<TIdentity>.Instance;
        foreach (object current in executor.IterateIdentityPrimitive(new LibraDexIdentityPrimitiveRequest(
            LibraDexCriteriaKind.All,
            Array.Empty<object?>())))
        {
            if (current is not TIdentity typed)
                throw new InvalidDataException($"Index '{index.Name}' yielded identity type {current.GetType().FullName}, not {typeof(TIdentity).FullName}.");
            if (comparer.Equals(typed, identity))
                return true;
        }

        return false;
    }

    internal static bool EntryExists<TKey, TIdentity>(IIndex index, TKey key, TIdentity identity)
    {
        IIdentityExactTupleMutator exact = index as IIdentityExactTupleMutator
            ?? throw new NotSupportedException($"Index '{index.Name}' does not expose exact entry lookup.");
        object? boxedIdentity = identity;
        if (boxedIdentity is null)
            throw new ArgumentNullException(nameof(identity));
        return exact.ContainsExactTuple(key, boxedIdentity);
    }

    /// <summary>
    /// Executes a stop-first typed ownership check without materializing the identities stored beneath the key.<br/>
    /// The concrete generic index supplies allocation-free scalar routes; other implementations retain a correct typed exact-lookup fallback.<br/>
    /// </summary>
    /// <typeparam name="TKey">Runtime key type.<br/></typeparam>
    /// <typeparam name="TIdentity">Runtime identity type.<br/></typeparam>
    /// <param name="index">Opened index whose key route is inspected.<br/></param>
    /// <param name="key">Key whose owners are compared.<br/></param>
    /// <param name="identity">Identity allowed to own the key.<br/></param>
    /// <returns><see langword="true"/> when a different identity owns the key; otherwise <see langword="false"/>.<br/></returns>
    internal static bool OtherIdentityExists<TKey, TIdentity>(
        IIndex index,
        TKey key,
        TIdentity identity)
    {
        if (index is LibraDexIndex<TKey, TIdentity> typed)
            return typed.ContainsOtherIdentity(key, identity);

        IReadOnlyList<TIdentity> identities =
            LibraDexExactKeyLookupExecution.GetIdentities<TKey, TIdentity>(index, key);
        EqualityComparer<TIdentity> comparer = EqualityComparer<TIdentity>.Default;
        for (int i = 0; i < identities.Count; i++)
        {
            if (!comparer.Equals(identities[i], identity))
                return true;
        }

        return false;
    }

    internal static int CheckKeys<TKey>(IIndex index, ReadOnlySpan<TKey> keys, Span<bool> results)
    {
        if (results.Length < keys.Length)
            throw new ArgumentException("The result span must contain at least one cell for every candidate key.", nameof(results));
        for (int i = 0; i < keys.Length; i++)
            results[i] = KeyExists(index, keys[i]);
        return keys.Length;
    }

    internal static int CheckEntries<TKey, TIdentity>(
        IIndex index,
        ReadOnlySpan<LibraDexIndexEntry<TKey, TIdentity>> entries,
        Span<bool> results)
    {
        if (results.Length < entries.Length)
            throw new ArgumentException("The result span must contain at least one cell for every candidate entry.", nameof(results));
        for (int i = 0; i < entries.Length; i++)
            results[i] = EntryExists(index, entries[i].Key, entries[i].Identity);
        return entries.Length;
    }

    internal static int CheckIdentities<TIdentity>(IIndex index, IReadOnlyList<TIdentity> identities, Span<bool> results)
    {
        ArgumentNullException.ThrowIfNull(identities);
        if (results.Length < identities.Count)
            throw new ArgumentException("The result span must contain at least one cell for every candidate identity.", nameof(results));
        results.Slice(0, identities.Count).Clear();
        if (identities.Count == 0)
            return 0;

        object? first = identities[0];
        if (first is null)
            throw new ArgumentException("Identity candidates cannot contain null.", nameof(identities));
        if (index is ILibraDexIdentityInverseLookup inverse && inverse.TryIdentityExistsFromInverse(first, out bool firstExists))
        {
            results[0] = firstExists;
            for (int i = 1; i < identities.Count; i++)
            {
                object? candidate = identities[i];
                if (candidate is null)
                    throw new ArgumentException("Identity candidates cannot contain null.", nameof(identities));
                if (!inverse.TryIdentityExistsFromInverse(candidate, out results[i]))
                    throw new InvalidOperationException("The configured inverse stopped accepting identity checks during one batch operation.");
            }

            return identities.Count;
        }

        int tableSize = GetTableSize(identities.Count);
        int[] slots = ArrayPool<int>.Shared.Rent(tableSize);
        Array.Fill(slots, -1, 0, tableSize);
        LibraDexExistenceValueComparer<TIdentity> comparer = LibraDexExistenceValueComparer<TIdentity>.Instance;
        int mask = tableSize - 1;
        int distinct = 0;
        for (int i = 0; i < identities.Count; i++)
        {
            TIdentity candidate = identities[i];
            if (candidate is null)
                throw new ArgumentException("Identity candidates cannot contain null.", nameof(identities));
            int slot = comparer.GetHashCode(candidate) & mask;
            while (slots[slot] >= 0 && !comparer.Equals(identities[slots[slot]], candidate))
                slot = (slot + 1) & mask;
            if (slots[slot] < 0)
            {
                slots[slot] = i;
                distinct++;
            }
        }

        try
        {
            IIdentityPrimitiveExecutor executor = index as IIdentityPrimitiveExecutor
                ?? throw new NotSupportedException($"Index '{index.Name}' does not expose identity iteration.");
            int found = 0;
            foreach (object current in executor.IterateIdentityPrimitive(new LibraDexIdentityPrimitiveRequest(
                LibraDexCriteriaKind.All,
                Array.Empty<object?>())))
            {
                if (current is not TIdentity typed)
                    throw new InvalidDataException($"Index '{index.Name}' yielded identity type {current.GetType().FullName}, not {typeof(TIdentity).FullName}.");
                int slot = comparer.GetHashCode(typed) & mask;
                while (slots[slot] >= 0)
                {
                    int candidateIndex = slots[slot];
                    if (comparer.Equals(identities[candidateIndex], typed))
                    {
                        if (!results[candidateIndex])
                        {
                            results[candidateIndex] = true;
                            found++;
                        }
                        break;
                    }

                    slot = (slot + 1) & mask;
                }

                if (found == distinct)
                    break;
            }

            for (int i = 0; i < identities.Count; i++)
            {
                int slot = comparer.GetHashCode(identities[i]) & mask;
                while (!comparer.Equals(identities[slots[slot]], identities[i]))
                    slot = (slot + 1) & mask;
                results[i] = results[slots[slot]];
            }

            return identities.Count;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(slots, clearArray: false);
        }
    }

    internal static int CheckIdentities<TIdentity>(IIndex index, ReadOnlySpan<TIdentity> identities, Span<bool> results)
    {
        if (results.Length < identities.Length)
            throw new ArgumentException("The result span must contain at least one cell for every candidate identity.", nameof(results));
        results.Slice(0, identities.Length).Clear();
        if (identities.Length == 0)
            return 0;

        object? first = identities[0];
        if (first is null)
            throw new ArgumentException("Identity candidates cannot contain null.", nameof(identities));
        if (index is ILibraDexIdentityInverseLookup inverse && inverse.TryIdentityExistsFromInverse(first, out bool firstExists))
        {
            results[0] = firstExists;
            for (int i = 1; i < identities.Length; i++)
            {
                object? candidate = identities[i];
                if (candidate is null)
                    throw new ArgumentException("Identity candidates cannot contain null.", nameof(identities));
                if (!inverse.TryIdentityExistsFromInverse(candidate, out results[i]))
                    throw new InvalidOperationException("The configured inverse stopped accepting identity checks during one batch operation.");
            }

            return identities.Length;
        }

        int tableSize = GetTableSize(identities.Length);
        int[] slots = ArrayPool<int>.Shared.Rent(tableSize);
        Array.Fill(slots, -1, 0, tableSize);
        LibraDexExistenceValueComparer<TIdentity> comparer = LibraDexExistenceValueComparer<TIdentity>.Instance;
        int mask = tableSize - 1;
        int distinct = 0;
        for (int i = 0; i < identities.Length; i++)
        {
            TIdentity candidate = identities[i];
            if (candidate is null)
                throw new ArgumentException("Identity candidates cannot contain null.", nameof(identities));
            int slot = comparer.GetHashCode(candidate) & mask;
            while (slots[slot] >= 0 && !comparer.Equals(identities[slots[slot]], candidate))
                slot = (slot + 1) & mask;
            if (slots[slot] < 0)
            {
                slots[slot] = i;
                distinct++;
            }
        }

        try
        {
            IIdentityPrimitiveExecutor executor = index as IIdentityPrimitiveExecutor
                ?? throw new NotSupportedException($"Index '{index.Name}' does not expose identity iteration.");
            int found = 0;
            foreach (object current in executor.IterateIdentityPrimitive(new LibraDexIdentityPrimitiveRequest(
                LibraDexCriteriaKind.All,
                Array.Empty<object?>())))
            {
                if (current is not TIdentity typed)
                    throw new InvalidDataException($"Index '{index.Name}' yielded identity type {current.GetType().FullName}, not {typeof(TIdentity).FullName}.");
                int slot = comparer.GetHashCode(typed) & mask;
                while (slots[slot] >= 0)
                {
                    int candidateIndex = slots[slot];
                    if (comparer.Equals(identities[candidateIndex], typed))
                    {
                        if (!results[candidateIndex])
                        {
                            results[candidateIndex] = true;
                            found++;
                        }
                        break;
                    }

                    slot = (slot + 1) & mask;
                }

                if (found == distinct)
                    break;
            }

            for (int i = 0; i < identities.Length; i++)
            {
                int slot = comparer.GetHashCode(identities[i]) & mask;
                while (!comparer.Equals(identities[slots[slot]], identities[i]))
                    slot = (slot + 1) & mask;
                results[i] = results[slots[slot]];
            }

            return identities.Length;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(slots, clearArray: false);
        }
    }

    internal static LibraDexIdentityPrimitiveRequest CreateKeyRequest<TKey>(TKey key, int? TakeLimit)
    {
        object? candidate = key;
        if (candidate is null || candidate == DBNull.Value)
            return new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.KeyState, new object?[] { NullKey.Null }, TakeLimit);
        if (candidate is string text && text.Length == 0 || candidate is byte[] bytes && bytes.Length == 0)
            return new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.KeyState, new object?[] { NullKey.Empty }, TakeLimit);
        return new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.Find, new object?[] { candidate }, TakeLimit);
    }

    private static int GetTableSize(int count)
    {
        int required = Math.Max(4, checked(count * 2));
        int size = 4;
        while (size < required)
            size = checked(size << 1);
        return size;
    }
}

internal sealed class LibraDexExistenceValueComparer<TValue> : IEqualityComparer<TValue>
{
    internal static readonly LibraDexExistenceValueComparer<TValue> Instance = new();

    public bool Equals(TValue? left, TValue? right)
    {
        if (left is byte[] leftBytes && right is byte[] rightBytes)
            return leftBytes.AsSpan().SequenceEqual(rightBytes);
        return EqualityComparer<TValue>.Default.Equals(left!, right!);
    }

    public int GetHashCode(TValue value)
    {
        if (value is byte[] bytes)
        {
            HashCode hash = new();
            for (int i = 0; i < bytes.Length; i++)
                hash.Add(bytes[i]);
            return hash.ToHashCode() & int.MaxValue;
        }

        return EqualityComparer<TValue>.Default.GetHashCode(value!) & int.MaxValue;
    }
}
