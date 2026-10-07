using System.Numerics;

namespace LibraDex;

public readonly partial struct LibraDexIndexIdentities<TKey, TIdentity>
{
    /// <summary>
    /// Materializes every identity associated with one exact key.<br/>
    /// The lookup follows the index's ordered point route, including continuation shelves and dedicated null or empty routes, without building a reusable condition.<br/>
    /// Use a condition and reader when results should remain streamed instead of eagerly materialized.<br/>
    /// </summary>
    /// <param name="key">The exact key to seek.<br/></param>
    /// <returns>The identities associated with <paramref name="key"/> in natural index order, or an empty list when the key is absent.<br/></returns>
    public IReadOnlyList<TIdentity> GetByKey(TKey key)
        => LibraDexExactKeyLookupExecution.GetIdentities<TKey, TIdentity>(index, key);

    /// <summary>
    /// Counts identities associated with one exact key.<br/>
    /// Shape-native range or route metadata is used when available so the count does not require a returned identity collection.<br/>
    /// </summary>
    /// <param name="key">The exact key to count.<br/></param>
    /// <returns>The number of identities associated with <paramref name="key"/>.<br/></returns>
    public long CountByKey(TKey key)
        => LibraDexExactKeyLookupExecution.CountIdentities(index, key);

    /// <summary>
    /// Materializes identities for each distinct supplied key.<br/>
    /// Results are keyed in natural physical index order; duplicate requested keys are normalized, and absent keys remain present with an empty identity list.<br/>
    /// This eager dictionary form is intended for direct point materialization; use a condition for streaming, bookmarks, filtering, or ordering beyond the index's natural order.<br/>
    /// </summary>
    /// <param name="keys">The exact keys to seek.<br/></param>
    /// <returns>A read-only dictionary from each distinct requested key to its identities.<br/></returns>
    public IReadOnlyDictionary<TKey, IReadOnlyList<TIdentity>> GetByKeys(IReadOnlyList<TKey> keys)
        => LibraDexExactKeyLookupExecution.GetIdentities<TKey, TIdentity>(index, keys);

    /// <summary>
    /// Counts identities for each distinct supplied key.<br/>
    /// Results are keyed in natural physical index order; duplicate requested keys are normalized, and absent keys remain present with a zero count.<br/>
    /// </summary>
    /// <param name="keys">The exact keys to count.<br/></param>
    /// <returns>A read-only dictionary from each distinct requested key to its identity count.<br/></returns>
    public IReadOnlyDictionary<TKey, long> CountByKeys(IReadOnlyList<TKey> keys)
        => LibraDexExactKeyLookupExecution.CountIdentities(index, keys);
}

public readonly partial struct LibraDexIndexEntries<TKey, TIdentity>
{
    /// <summary>
    /// Materializes complete key/identity entries associated with one exact key.<br/>
    /// The key is retained in every returned entry so the result can be combined with other eager entry materializations without losing its association.<br/>
    /// </summary>
    /// <param name="key">The exact key to seek.<br/></param>
    /// <returns>The matching complete entries in natural index order, or an empty list when the key is absent.<br/></returns>
    public IReadOnlyList<LibraDexIndexEntry<TKey, TIdentity>> GetByKey(TKey key)
        => LibraDexExactKeyLookupExecution.GetEntries<TKey, TIdentity>(index, key);

    /// <summary>
    /// Materializes complete entries for each distinct supplied key.<br/>
    /// Results are keyed in natural physical index order; duplicate requested keys are normalized, and absent keys remain present with an empty entry list.<br/>
    /// </summary>
    /// <param name="keys">The exact keys to seek.<br/></param>
    /// <returns>A read-only dictionary from each requested key to its complete entries.<br/></returns>
    public IReadOnlyDictionary<TKey, IReadOnlyList<LibraDexIndexEntry<TKey, TIdentity>>> GetByKeys(IReadOnlyList<TKey> keys)
        => LibraDexExactKeyLookupExecution.GetEntries<TKey, TIdentity>(index, keys);
}

public readonly partial struct LibraDexIndexIdentities
{
    /// <summary>
    /// Materializes canonical binary identities associated with one runtime key.<br/>
    /// Returned byte arrays are caller-owned copies of LibraDex's canonical encoded identity values.<br/>
    /// </summary>
    /// <param name="key">The runtime key accepted by the opened index.<br/></param>
    /// <returns>The matching canonical identity bytes in natural index order.<br/></returns>
    public IReadOnlyList<byte[]> GetByKey(object? key)
        => LibraDexExactKeyLookupExecution.GetIdentities<object?, byte[]>(index, key);

    /// <summary>
    /// Materializes supported CLR identity values associated with one exact key.<br/>
    /// <typeparamref name="TKey"/> and <typeparamref name="TIdentity"/> must be primitive projection types supported by LibraDex; custom values should use the raw binary overload and caller-owned conversion.<br/>
    /// </summary>
    /// <typeparam name="TKey">The supplied runtime key type.<br/></typeparam>
    /// <typeparam name="TIdentity">The requested identity result type.<br/></typeparam>
    /// <param name="key">The exact key to seek.<br/></param>
    /// <returns>The matching typed identities in natural index order.<br/></returns>
    public IReadOnlyList<TIdentity> GetByKey<TKey, TIdentity>(TKey key)
        => LibraDexExactKeyLookupExecution.GetIdentities<TKey, TIdentity>(index, key);

    /// <summary>
    /// Counts identities associated with one runtime key without materializing their values.<br/>
    /// </summary>
    /// <param name="key">The exact runtime key to count.<br/></param>
    /// <returns>The number of associated identities.<br/></returns>
    public long CountByKey(object? key)
        => LibraDexExactKeyLookupExecution.CountIdentities(index, key);

    /// <summary>
    /// Materializes canonical binary identities for each distinct runtime key.<br/>
    /// Dictionary keys are canonical encoded key bytes using structural byte equality; absent keys remain present with empty lists.<br/>
    /// </summary>
    /// <param name="keys">The runtime keys accepted by the opened index.<br/></param>
    /// <returns>A read-only binary-key dictionary in natural physical index order.<br/></returns>
    public IReadOnlyDictionary<byte[]?, IReadOnlyList<byte[]>> GetByKeys(IReadOnlyList<object?> keys)
        => LibraDexExactKeyLookupExecution.GetRawIdentities(index, keys);

    /// <summary>
    /// Materializes typed identities for each distinct supplied key.<br/>
    /// Both generic arguments must belong to LibraDex's supported primitive projection vocabulary; results retain caller key typing while using LibraDex key equality.<br/>
    /// </summary>
    /// <typeparam name="TKey">The supplied and returned dictionary key type.<br/></typeparam>
    /// <typeparam name="TIdentity">The requested identity result type.<br/></typeparam>
    /// <param name="keys">The exact keys to seek.<br/></param>
    /// <returns>A read-only typed dictionary in natural physical index order.<br/></returns>
    public IReadOnlyDictionary<TKey, IReadOnlyList<TIdentity>> GetByKeys<TKey, TIdentity>(IReadOnlyList<TKey> keys)
        => LibraDexExactKeyLookupExecution.GetIdentities<TKey, TIdentity>(index, keys);

    /// <summary>
    /// Counts identities for each distinct runtime key.<br/>
    /// Dictionary keys are canonical encoded key bytes using structural byte equality; absent keys remain present with zero counts.<br/>
    /// </summary>
    /// <param name="keys">The runtime keys accepted by the opened index.<br/></param>
    /// <returns>A read-only binary-key dictionary of identity counts.<br/></returns>
    public IReadOnlyDictionary<byte[]?, long> CountByKeys(IReadOnlyList<object?> keys)
        => LibraDexExactKeyLookupExecution.CountRawIdentities(index, keys);

    /// <summary>
    /// Counts identities for each distinct typed key.<br/>
    /// <typeparamref name="TKey"/> must belong to LibraDex's supported primitive projection vocabulary.<br/>
    /// </summary>
    /// <typeparam name="TKey">The supplied and returned dictionary key type.<br/></typeparam>
    /// <param name="keys">The exact keys to count.<br/></param>
    /// <returns>A read-only typed dictionary of identity counts.<br/></returns>
    public IReadOnlyDictionary<TKey, long> CountByKeys<TKey>(IReadOnlyList<TKey> keys)
        => LibraDexExactKeyLookupExecution.CountIdentities(index, keys);
}

public readonly partial struct LibraDexIndexEntries
{
    /// <summary>
    /// Materializes complete canonical binary entries associated with one runtime key.<br/>
    /// Both sides of every entry are caller-owned canonical byte arrays; dedicated string null and empty routes retain their distinct encoded sentinels, while a nullable scalar null route remains a null key.<br/>
    /// </summary>
    /// <param name="key">The runtime key accepted by the opened index.<br/></param>
    /// <returns>The matching canonical binary entries in natural index order.<br/></returns>
    public IReadOnlyList<LibraDexIndexEntry<byte[]?, byte[]>> GetByKey(object? key)
        => LibraDexExactKeyLookupExecution.GetEntries<object?, byte[]? , byte[]>(index, key);

    /// <summary>
    /// Materializes typed complete entries associated with one exact key.<br/>
    /// Generic arguments must belong to LibraDex's supported primitive projection vocabulary.<br/>
    /// </summary>
    /// <typeparam name="TKey">The requested entry key type.<br/></typeparam>
    /// <typeparam name="TIdentity">The requested entry identity type.<br/></typeparam>
    /// <param name="key">The exact key to seek.<br/></param>
    /// <returns>The matching typed entries in natural index order.<br/></returns>
    public IReadOnlyList<LibraDexIndexEntry<TKey, TIdentity>> GetByKey<TKey, TIdentity>(TKey key)
        => LibraDexExactKeyLookupExecution.GetEntries<TKey, TIdentity>(index, key);

    /// <summary>
    /// Materializes complete canonical binary entries for each distinct runtime key.<br/>
    /// Dictionary keys use structural byte equality and absent keys remain present with empty entry lists.<br/>
    /// </summary>
    /// <param name="keys">The runtime keys accepted by the opened index.<br/></param>
    /// <returns>A read-only binary-key dictionary in natural physical index order.<br/></returns>
    public IReadOnlyDictionary<byte[]?, IReadOnlyList<LibraDexIndexEntry<byte[]?, byte[]>>> GetByKeys(IReadOnlyList<object?> keys)
        => LibraDexExactKeyLookupExecution.GetRawEntries(index, keys);

    /// <summary>
    /// Materializes typed complete entries for each distinct supplied key.<br/>
    /// Generic arguments must belong to LibraDex's supported primitive projection vocabulary; absent keys remain present with empty entry lists.<br/>
    /// </summary>
    /// <typeparam name="TKey">The supplied and returned dictionary key type.<br/></typeparam>
    /// <typeparam name="TIdentity">The requested entry identity type.<br/></typeparam>
    /// <param name="keys">The exact keys to seek.<br/></param>
    /// <returns>A read-only typed dictionary in natural physical index order.<br/></returns>
    public IReadOnlyDictionary<TKey, IReadOnlyList<LibraDexIndexEntry<TKey, TIdentity>>> GetByKeys<TKey, TIdentity>(IReadOnlyList<TKey> keys)
        => LibraDexExactKeyLookupExecution.GetEntries<TKey, TIdentity>(index, keys);
}

internal static class LibraDexExactKeyLookupExecution
{
    internal static IReadOnlyList<TIdentity> GetIdentities<TKey, TIdentity>(IIndex index, TKey key)
    {
        ValidateKey(key);
        ValidateType<TIdentity>("identity");
        IIdentityPrimitiveExecutor executor = index as IIdentityPrimitiveExecutor
            ?? throw new NotSupportedException($"Index '{index.Name}' does not expose exact identity lookup.");
        List<TIdentity> result = new();
        foreach (object identity in executor.IterateIdentityPrimitive(
            LibraDexExistenceExecution.CreateKeyRequest(key, TakeLimit: null)))
        {
            result.Add(LibraDexResultValueConverter.Convert<TIdentity>(identity, index, isIdentity: true));
        }

        return result;
    }

    internal static long CountIdentities<TKey>(IIndex index, TKey key)
    {
        ValidateKey(key);
        IIdentityPrimitiveExecutor executor = index as IIdentityPrimitiveExecutor
            ?? throw new NotSupportedException($"Index '{index.Name}' does not expose exact identity counting.");
        return executor.CountIdentityPrimitive(
            LibraDexExistenceExecution.CreateKeyRequest(key, TakeLimit: null));
    }

    internal static IReadOnlyList<LibraDexIndexEntry<TKey, TIdentity>> GetEntries<TKey, TIdentity>(IIndex index, TKey key)
        => GetEntries<TKey, TKey, TIdentity>(index, key);

    internal static IReadOnlyList<LibraDexIndexEntry<TResultKey, TIdentity>> GetEntries<TKey, TResultKey, TIdentity>(
        IIndex index,
        TKey key)
    {
        ValidateKey(key);
        ValidateType<TResultKey>("returned key");
        ValidateType<TIdentity>("identity");
        IIdentityPrimitiveTupleStreamer streamer = index as IIdentityPrimitiveTupleStreamer
            ?? throw new NotSupportedException($"Index '{index.Name}' does not expose exact entry lookup.");
        List<LibraDexIndexEntry<TResultKey, TIdentity>> result = new();
        foreach (LibraDexObjectTuple tuple in streamer.IterateTuplePrimitive(
            LibraDexExistenceExecution.CreateKeyRequest(key, TakeLimit: null)))
        {
            TResultKey returnedKey = ConvertValue<TResultKey>(tuple.Key, index, isIdentity: false);
            TIdentity identity = ConvertValue<TIdentity>(tuple.Identity, index, isIdentity: true);
            result.Add(new LibraDexIndexEntry<TResultKey, TIdentity>(returnedKey, identity));
        }

        return result;
    }

    internal static IReadOnlyDictionary<TKey, IReadOnlyList<TIdentity>> GetIdentities<TKey, TIdentity>(
        IIndex index,
        IReadOnlyList<TKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ValidateType<TKey>("key");
        ValidateType<TIdentity>("identity");
        List<LibraDexExactLookupKey<TKey>> ordered = NormalizeAndOrder(index, keys);
        LibraDexGroupedDictionary<TKey, IReadOnlyList<TIdentity>> result = new();
        for (int i = 0; i < ordered.Count; i++)
        {
            LibraDexExactLookupKey<TKey> key = ordered[i];
            result.Add(key.Value, GetIdentities<TKey, TIdentity>(index, key.Value));
        }

        return result;
    }

    internal static IReadOnlyDictionary<TKey, long> CountIdentities<TKey>(
        IIndex index,
        IReadOnlyList<TKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ValidateType<TKey>("key");
        List<LibraDexExactLookupKey<TKey>> ordered = NormalizeAndOrder(index, keys);
        LibraDexGroupedDictionary<TKey, long> result = new();
        for (int i = 0; i < ordered.Count; i++)
        {
            LibraDexExactLookupKey<TKey> key = ordered[i];
            result.Add(key.Value, CountIdentities(index, key.Value));
        }

        return result;
    }

    internal static IReadOnlyDictionary<TKey, IReadOnlyList<LibraDexIndexEntry<TKey, TIdentity>>> GetEntries<TKey, TIdentity>(
        IIndex index,
        IReadOnlyList<TKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ValidateType<TKey>("key");
        ValidateType<TIdentity>("identity");
        List<LibraDexExactLookupKey<TKey>> ordered = NormalizeAndOrder(index, keys);
        LibraDexGroupedDictionary<TKey, IReadOnlyList<LibraDexIndexEntry<TKey, TIdentity>>> result = new();
        for (int i = 0; i < ordered.Count; i++)
        {
            LibraDexExactLookupKey<TKey> key = ordered[i];
            result.Add(key.Value, GetEntries<TKey, TIdentity>(index, key.Value));
        }

        return result;
    }

    internal static IReadOnlyDictionary<byte[]?, IReadOnlyList<byte[]>> GetRawIdentities(
        IIndex index,
        IReadOnlyList<object?> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        List<LibraDexExactLookupKey<object?>> ordered = NormalizeAndOrder(index, keys);
        LibraDexGroupedDictionary<byte[]?, IReadOnlyList<byte[]>> result = new();
        for (int i = 0; i < ordered.Count; i++)
        {
            LibraDexExactLookupKey<object?> key = ordered[i];
            result.Add(key.Raw, GetIdentities<object?, byte[]>(index, key.Value));
        }

        return result;
    }

    internal static IReadOnlyDictionary<byte[]?, long> CountRawIdentities(
        IIndex index,
        IReadOnlyList<object?> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        List<LibraDexExactLookupKey<object?>> ordered = NormalizeAndOrder(index, keys);
        LibraDexGroupedDictionary<byte[]?, long> result = new();
        for (int i = 0; i < ordered.Count; i++)
        {
            LibraDexExactLookupKey<object?> key = ordered[i];
            result.Add(key.Raw, CountIdentities(index, key.Value));
        }

        return result;
    }

    internal static IReadOnlyDictionary<byte[]?, IReadOnlyList<LibraDexIndexEntry<byte[]?, byte[]>>> GetRawEntries(
        IIndex index,
        IReadOnlyList<object?> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        List<LibraDexExactLookupKey<object?>> ordered = NormalizeAndOrder(index, keys);
        LibraDexGroupedDictionary<byte[]?, IReadOnlyList<LibraDexIndexEntry<byte[]?, byte[]>>> result = new();
        for (int i = 0; i < ordered.Count; i++)
        {
            LibraDexExactLookupKey<object?> key = ordered[i];
            result.Add(key.Raw, GetEntries<object?, byte[]?, byte[]>(index, key.Value));
        }

        return result;
    }

    private static List<LibraDexExactLookupKey<TKey>> NormalizeAndOrder<TKey>(
        IIndex index,
        IReadOnlyList<TKey> keys)
    {
        LibraDexGroupedDictionary<TKey, LibraDexExactLookupKey<TKey>> distinct = new();
        for (int i = 0; i < keys.Count; i++)
        {
            TKey key = keys[i];
            if (distinct.ContainsKey(key))
                continue;
            distinct.Add(key, new LibraDexExactLookupKey<TKey>(
                key,
                LibraDexResultValueConverter.EncodeRawBytes(key, index, isIdentity: false)));
        }

        List<LibraDexExactLookupKey<TKey>> result = new(distinct.Count);
        foreach (LibraDexExactLookupKey<TKey> key in distinct.Values)
            result.Add(key);
        result.Sort(static (left, right) => CompareRaw(left.Raw, right.Raw));
        return result;
    }

    private static int CompareRaw(byte[]? left, byte[]? right)
    {
        if (left is null)
            return right is null ? 0 : -1;
        if (right is null)
            return 1;
        return left.AsSpan().SequenceCompareTo(right);
    }

    private static TValue ConvertValue<TValue>(object? value, IIndex index, bool isIdentity)
    {
        if (typeof(TValue) == typeof(byte[]))
            return (TValue)(object)LibraDexResultValueConverter.EncodeRawBytes(value, index, isIdentity)!;
        return LibraDexResultValueConverter.Convert<TValue>(value, index, isIdentity);
    }

    private static void ValidateType<TValue>(string role)
        => ValidateType(typeof(TValue), role);

    private static void ValidateKey<TKey>(TKey key)
    {
        Type declared = Nullable.GetUnderlyingType(typeof(TKey)) ?? typeof(TKey);
        object? value = key;
        if (value is LibraDexCompositeKey)
            return;
        if (declared != typeof(object))
        {
            ValidateType(declared, "key");
            return;
        }

        if (value is not null && value != DBNull.Value)
            ValidateType(value.GetType(), "key");
    }

    private static void ValidateType(Type candidate, string role)
    {
        Type type = Nullable.GetUnderlyingType(candidate) ?? candidate;
        if (type.IsEnum ||
            type == typeof(byte[]) ||
            type == typeof(string) ||
            type == typeof(bool) ||
            type == typeof(byte) ||
            type == typeof(sbyte) ||
            type == typeof(short) ||
            type == typeof(ushort) ||
            type == typeof(char) ||
            type == typeof(int) ||
            type == typeof(uint) ||
            type == typeof(long) ||
            type == typeof(ulong) ||
            type == typeof(Int128) ||
            type == typeof(UInt128) ||
            type == typeof(float) ||
            type == typeof(double) ||
            type == typeof(decimal) ||
            type == typeof(BigInteger) ||
            type == typeof(Guid) ||
            type == typeof(DateTime) ||
            type == typeof(DateTimeOffset) ||
            type == typeof(DateOnly) ||
            type == typeof(TimeOnly) ||
            type == typeof(TimeSpan))
        {
            return;
        }

        throw new NotSupportedException(
            $"Direct exact-key {role} type '{type.FullName}' is not in LibraDex's supported primitive projection vocabulary. Request raw byte[] results and apply the caller-owned conversion.");
    }
}

internal readonly record struct LibraDexExactLookupKey<TKey>(TKey Value, byte[]? Raw);
