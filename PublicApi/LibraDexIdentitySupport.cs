namespace LibraDex;

internal readonly record struct LibraDexTuple<TKey, TIdentity>(TKey Key, TIdentity Identity);

internal readonly record struct LibraDexObjectTuple(object? Key, object Identity)
{
    /// <summary>
    /// Compares two runtime values using structural byte-array equality when needed and default equality otherwise.<br/>
    /// Fixed binary keys and identities often materialize as byte arrays, where reference equality would not match LibraDex tuple semantics.<br/>
    /// </summary>
    /// <param name="left">The first runtime value.</param>
    /// <param name="right">The second runtime value.</param>
    /// <returns><see langword="true"/> when the values represent the same logical tuple component.</returns>
    internal static bool ValueEquals(object? left, object? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        if (left is byte[] leftBytes && right is byte[] rightBytes)
        {
            return leftBytes.AsSpan().SequenceEqual(rightBytes);
        }

        if (left is LibraDexCompositeKey leftComposite && right is LibraDexCompositeKey rightComposite)
        {
            if (leftComposite.Count != rightComposite.Count)
            {
                return false;
            }

            for (int i = 0; i < leftComposite.Count; i++)
            {
                object? leftValue = leftComposite.Values[i].Value;
                object? rightValue = rightComposite.Values[i].Value;
                if (leftValue is null || rightValue is null)
                {
                    if (leftValue is not null || rightValue is not null)
                    {
                        return false;
                    }

                    continue;
                }

                if (!ValueEquals(leftValue, rightValue))
                {
                    return false;
                }
            }

            return true;
        }

        return EqualityComparer<object>.Default.Equals(left, right);
    }
}

/// <summary>
/// Provides LibraDex key equality for decoded public key values.<br/>
/// `byte[]` keys need structural equality because array reference equality would split equivalent binary keys into different buckets during condition-level grouping and membership work.<br/>
/// Other key types delegate to <see cref="EqualityComparer{T}.Default"/> so existing value equality semantics remain intact.<br/>
/// </summary>
/// <typeparam name="TKey">The decoded key type.</typeparam>
internal static class LibraDexKeyEquality<TKey>
{
    /// <summary>
    /// Gets the comparer used for decoded key dictionary and set operations.<br/>
    /// This comparer is intentionally internal to condition/materialization helpers and is not a public query contract.<br/>
    /// </summary>
    internal static IEqualityComparer<TKey> Comparer { get; } = typeof(TKey) == typeof(byte[])
        ? (IEqualityComparer<TKey>)(object)ByteArrayKeyEqualityComparer.Instance
        : typeof(TKey) == typeof(LibraDexCompositeKey)
            ? (IEqualityComparer<TKey>)(object)CompositeKeyEqualityComparer.Instance
        : EqualityComparer<TKey>.Default;

    private sealed class ByteArrayKeyEqualityComparer : IEqualityComparer<byte[]>
    {
        internal static readonly ByteArrayKeyEqualityComparer Instance = new();

        public bool Equals(byte[]? x, byte[]? y)
        {
            return ReferenceEquals(x, y) || (x is not null && y is not null && x.AsSpan().SequenceEqual(y));
        }

        public int GetHashCode(byte[] obj)
        {
            ArgumentNullException.ThrowIfNull(obj);
            HashCode hash = new();
            foreach (byte value in obj)
            {
                hash.Add(value);
            }

            return hash.ToHashCode();
        }
    }

    private sealed class CompositeKeyEqualityComparer : IEqualityComparer<LibraDexCompositeKey>
    {
        internal static readonly CompositeKeyEqualityComparer Instance = new();

        public bool Equals(LibraDexCompositeKey? x, LibraDexCompositeKey? y)
        {
            if (ReferenceEquals(x, y))
            {
                return true;
            }

            if (x is null || y is null || x.Count != y.Count)
            {
                return false;
            }

            for (int i = 0; i < x.Count; i++)
            {
                if (!LibraDexObjectTuple.ValueEquals(x.Values[i].Value, y.Values[i].Value))
                {
                    return false;
                }
            }

            return true;
        }

        public int GetHashCode(LibraDexCompositeKey obj)
        {
            ArgumentNullException.ThrowIfNull(obj);
            HashCode hash = new();
            for (int i = 0; i < obj.Count; i++)
            {
                AddValueHash(ref hash, obj.Values[i].Value);
            }

            return hash.ToHashCode();
        }

        /// <summary>
        /// Adds one composite part value to a structural hash code.<br/>
        /// Binary key parts hash by byte content so they match the tuple equality rules used by LibraDex key comparison.<br/>
        /// </summary>
        /// <param name="hash">The hash accumulator.<br/></param>
        /// <param name="value">The composite part value.<br/></param>
        private static void AddValueHash(ref HashCode hash, object? value)
        {
            if (value is null)
            {
                hash.Add(0);
                return;
            }

            if (value is byte[] bytes)
            {
                for (int i = 0; i < bytes.Length; i++)
                {
                    hash.Add(bytes[i]);
                }

                return;
            }

            hash.Add(value);
        }
    }
}

/// <summary>
/// Represents a strict non-generic public handle over an opened LibraDex index.<br/>
/// This surface is for generated and programmatic callers that cannot comfortably carry `TKey` and `TIdentity` through every layer, while still preserving runtime type validation and metadata-driven behavior.<br/>
/// </summary>
