using System.Numerics;

namespace LibraDex;

/// <summary>
/// Provides a first-class BigInteger key facade over fixed-width keys with raw variable-length identity bytes.<br/>
/// The explicit type avoids overloading `byte[]` scalar identity semantics, where fixed-width byte arrays already mean fixed blob lanes.<br/>
/// </summary>
public sealed class LibraDexBigIntVarIdentityIndex : IIndex, IIdentityPrimitiveExecutor, IDisposable
{
    private readonly FixedNVarIdentityIndex inner;
    private readonly IndexKeys keyContract;
    private bool disposed;

    internal LibraDexBigIntVarIdentityIndex(
        string group,
        string name,
        FixedNVarIdentityIndex inner,
        int maxBytes,
        int maxIdentityBytes,
        IndexKeys keyContract)
    {
        ArgumentNullException.ThrowIfNull(inner);
        LibraDexBigIntCodec.ValidateMaxBytes(maxBytes, nameof(maxBytes));
        FixedNVarIdentityProfile.Create(FixedNVarIdentityProfile.DefaultShelfExtentSize, LibraDexBigIntCodec.GetFixedEncodedLength(maxBytes), maxIdentityBytes);
        Group = group;
        Name = name;
        this.inner = inner;
        MaxBytes = maxBytes;
        MaxIdentityBytes = maxIdentityBytes;
        this.keyContract = keyContract;
    }

    /// <summary>
    /// Gets the identity group name recorded for this BigInt index.<br/>
    /// </summary>
    public string Group { get; }

    /// <summary>
    /// Gets the logical index name recorded for this BigInt index.<br/>
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the maximum canonical BigInteger magnitude byte count accepted by this index.<br/>
    /// </summary>
    public int MaxBytes { get; }

    /// <summary>
    /// Gets the maximum raw identity byte count accepted by this index.<br/>
    /// </summary>
    public int MaxIdentityBytes { get; }

    public Type KeyType => typeof(BigInteger);

    public Type IdentityType => typeof(byte[]);

    public IndexKeys KeyContract => keyContract;

    public CatalogIndexKeyFamily KeyFamily => CatalogIndexKeyFamily.BigInt;

    public CatalogIndexIdentityFamily IdentityFamily => CatalogIndexIdentityFamily.Blob;

    public LibraDexIndexShapeSpec? LogicalShape => null;

    /// <summary>
    /// Adds one BigInteger key and raw identity byte array to this index.<br/>
    /// The key is encoded once into LibraDex's fixed sortable BigInt byte format, while the identity is copied into the variable-identity shelf record.<br/>
    /// </summary>
    /// <param name="key">The BigInteger key value to add.</param>
    /// <param name="identity">The raw identity bytes associated with the key.</param>
    /// <returns>The insert result plus any commit telemetry surfaced by the routed storage.</returns>
    public LibraDexGenericInsertResult Add(BigInteger key, byte[] identity)
    {
        return Insert(key, identity);
    }

    /// <summary>
    /// Inserts one BigInteger key and raw identity byte array into this index.<br/>
    /// </summary>
    /// <param name="key">The BigInteger key value to insert.</param>
    /// <param name="identity">The raw identity bytes associated with the key.</param>
    /// <returns>The insert result plus any commit telemetry surfaced by the routed storage.</returns>
    public LibraDexGenericInsertResult Insert(BigInteger key, byte[] identity)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(identity);
        byte[] encodedKey = LibraDexBigIntCodec.Encode(key, MaxBytes, LibraDexBigIntKeyStorage.FixedWidth);
        (FixedNVarIdentityInsertResult result, DataKernelCommitTelemetry commit) = inner.Insert(encodedKey, identity, keyContract != IndexKeys.Unique);
        return new LibraDexGenericInsertResult(
            result == FixedNVarIdentityInsertResult.Inserted,
            CreatedInitialShelfRoute: false,
            default,
            commit);
    }

    public LibraDexGenericInsertResult Insert(object? key, object identity)
    {
        if (key is not BigInteger typedKey)
        {
            throw new ArgumentException("BigInt variable-identity indexes require BigInteger runtime keys.", nameof(key));
        }

        if (identity is not byte[] typedIdentity)
        {
            throw new ArgumentException("BigInt variable-identity indexes require byte[] runtime identities.", nameof(identity));
        }

        return Insert(typedKey, typedIdentity);
    }

    /// <summary>
    /// Reads raw identities whose BigInteger keys fall inside the inclusive range.<br/>
    /// The returned byte arrays are disconnected copies owned by the caller.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower BigInteger key.</param>
    /// <param name="upperKey">The inclusive upper BigInteger key.</param>
    /// <returns>Raw identity byte arrays matching the requested range.</returns>
    public IReadOnlyList<byte[]> GetIdentities(BigInteger lowerKey, BigInteger upperKey)
    {
        ThrowIfDisposed();
        byte[] lower = LibraDexBigIntCodec.Encode(lowerKey, MaxBytes, LibraDexBigIntKeyStorage.FixedWidth);
        byte[] upper = LibraDexBigIntCodec.Encode(upperKey, MaxBytes, LibraDexBigIntKeyStorage.FixedWidth);
        return inner.ReadIdentityRange(lower, upper);
    }

    /// <summary>
    /// Reads raw identities whose BigInteger key exactly matches <paramref name="key"/>.<br/>
    /// </summary>
    /// <param name="key">The exact BigInteger key.</param>
    /// <returns>Raw identity byte arrays matching the exact key.</returns>
    public IReadOnlyList<byte[]> GetIdentities(BigInteger key)
    {
        return GetIdentities(key, key);
    }

    /// <summary>
    /// Prepares a strict non-generic key-membership set for condition-builder `InSet` calls.<br/>
    /// BigInt variable-identity condition execution consumes this prepared set through the same internal primitive bridge as exact and range predicates, while preserving strict key-type validation.<br/>
    /// </summary>
    /// <param name="keys">The runtime key values to validate.</param>
    /// <returns>A prepared object set containing the supplied BigInteger values.</returns>
    public LibraDexPreparedObjectSet PrepareInSet(IEnumerable<object> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        object[] values = keys.Select(key =>
        {
            if (key is not BigInteger big)
            {
                throw new ArgumentException("BigInt variable-identity indexes require BigInteger values in prepared sets.", nameof(keys));
            }

            _ = LibraDexBigIntCodec.Encode(big, MaxBytes, LibraDexBigIntKeyStorage.FixedWidth);
            return (object)big;
        }).ToArray();
        return new LibraDexPreparedObjectSet(typeof(BigInteger), values);
    }

    IReadOnlyList<object> IIdentityPrimitiveExecutor.ExecuteIdentityPrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        return ExecuteIdentityPrimitive(request);
    }

    IEnumerable<object> IIdentityPrimitiveExecutor.IterateIdentityPrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        return IterateIdentityPrimitive(request);
    }

    long IIdentityPrimitiveExecutor.CountIdentityPrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        return CountIdentityPrimitive(request);
    }

    IReadOnlyList<object> IIdentityPrimitiveExecutor.ExecuteAllIdentities()
    {
        return ExecuteIdentityPrimitive(new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.All, Array.Empty<object?>()));
    }

    IEnumerable<object> IIdentityPrimitiveExecutor.IterateIdentityUniverse()
    {
        return IterateIdentityPrimitive(new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.All, Array.Empty<object?>()));
    }

    public void Dispose()
    {
        disposed = true;
        inner.Dispose();
    }

    private IReadOnlyList<object> ExecuteIdentityPrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        return IterateIdentityPrimitive(request).ToList();
    }

    private IEnumerable<object> IterateIdentityPrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        ThrowIfDisposed();
        if (request.TakeLimit is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request.TakeLimit), request.TakeLimit, "Take cannot be negative.");
        }

        if (request.TakeLimit == 0)
        {
            yield break;
        }

        int returned = 0;
        foreach ((BigInteger lower, BigInteger upper) in ExpandPrimitiveRanges(request))
        {
            if (lower > upper)
            {
                continue;
            }

            foreach (byte[] identity in GetIdentities(lower, upper))
            {
                yield return identity;
                returned++;
                if (request.TakeLimit is not null && returned >= request.TakeLimit.Value)
                {
                    yield break;
                }
            }
        }
    }

    private long CountIdentityPrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        long count = 0;
        foreach ((BigInteger lower, BigInteger upper) in ExpandPrimitiveRanges(request))
        {
            if (lower > upper)
            {
                continue;
            }

            count += GetIdentities(lower, upper).Count;
        }

        return count;
    }

    private IEnumerable<(BigInteger Lower, BigInteger Upper)> ExpandPrimitiveRanges(LibraDexIdentityPrimitiveRequest request)
    {
        (BigInteger minimum, BigInteger maximum) = GetFullKeyBounds();
        switch (request.CriteriaKind)
        {
            case LibraDexCriteriaKind.All:
                yield return (minimum, maximum);
                yield break;
            case LibraDexCriteriaKind.Find:
            {
                BigInteger key = RequireCriterionBigInteger(request.Values, 0);
                yield return (key, key);
                yield break;
            }
            case LibraDexCriteriaKind.Between:
                yield return (RequireCriterionBigInteger(request.Values, 0), RequireCriterionBigInteger(request.Values, 1));
                yield break;
            case LibraDexCriteriaKind.Before:
            {
                BigInteger key = RequireCriterionBigInteger(request.Values, 0);
                yield return key <= minimum ? (BigInteger.One, BigInteger.Zero) : (minimum, key - BigInteger.One);
                yield break;
            }
            case LibraDexCriteriaKind.AtOrBefore:
                yield return (minimum, RequireCriterionBigInteger(request.Values, 0));
                yield break;
            case LibraDexCriteriaKind.After:
            {
                BigInteger key = RequireCriterionBigInteger(request.Values, 0);
                yield return key >= maximum ? (BigInteger.One, BigInteger.Zero) : (key + BigInteger.One, maximum);
                yield break;
            }
            case LibraDexCriteriaKind.AtOrAfter:
                yield return (RequireCriterionBigInteger(request.Values, 0), maximum);
                yield break;
            case LibraDexCriteriaKind.In:
            case LibraDexCriteriaKind.InSet:
                foreach (BigInteger key in EnumerateMembershipKeys(request.Values))
                {
                    yield return (key, key);
                }

                yield break;
            case LibraDexCriteriaKind.MultiRange:
                foreach (LibraDexIdentityKeyRange range in RequireIdentityKeyRanges(request.Values))
                {
                    yield return (RequireBigInteger(range.LowerKey, nameof(request.Values)), RequireBigInteger(range.UpperKey, nameof(request.Values)));
                }

                yield break;
            default:
                throw new NotSupportedException($"{request.CriteriaKind} BigInt variable-identity execution is not connected to physical readers yet.");
        }
    }

    private IEnumerable<BigInteger> EnumerateMembershipKeys(IReadOnlyList<object?> values)
    {
        if (values.Count > 1)
        {
            foreach (object? value in values)
            {
                yield return RequireBigInteger(value, nameof(values));
            }

            yield break;
        }

        object source = RequireCriterionValue(values, 0);
        IEnumerable<object> keys = source switch
        {
            LibraDexPreparedObjectSet prepared when prepared.KeyType == typeof(BigInteger) => prepared.Source,
            LibraDexPreparedObjectSet prepared => throw new ArgumentException($"Prepared membership key type {prepared.KeyType.FullName} does not match index key type {typeof(BigInteger).FullName}."),
            IEnumerable<object> objectValues => objectValues,
            System.Collections.IEnumerable enumerable when source is not string => enumerable.Cast<object>(),
            _ => throw new InvalidOperationException("Membership identity execution requires an enumerable key value or prepared set.")
        };

        foreach (object key in keys)
        {
            yield return RequireBigInteger(key, nameof(values));
        }
    }

    private static LibraDexIdentityKeyRange[] RequireIdentityKeyRanges(IReadOnlyList<object?> values)
    {
        if (values.Count != 1 || values[0] is not LibraDexIdentityKeyRange[] ranges)
        {
            throw new InvalidOperationException("BigInt variable-identity multi-range execution requires a captured LibraDexIdentityKeyRange array.");
        }

        return ranges;
    }

    private static object RequireCriterionValue(IReadOnlyList<object?> values, int index)
    {
        if (index >= values.Count || values[index] is null)
        {
            throw new InvalidOperationException("The identity criterion is missing a required BigInteger runtime value.");
        }

        return values[index]!;
    }

    private static BigInteger RequireCriterionBigInteger(IReadOnlyList<object?> values, int index)
    {
        return RequireBigInteger(RequireCriterionValue(values, index), nameof(values));
    }

    private static BigInteger RequireBigInteger(object? value, string parameterName)
    {
        if (value is BigInteger key)
        {
            return key;
        }

        string actualType = value?.GetType().FullName ?? "<null>";
        throw new ArgumentException($"Expected a BigInteger key value; received {actualType}.", parameterName);
    }

    private (BigInteger Minimum, BigInteger Maximum) GetFullKeyBounds()
    {
        BigInteger maximum = (BigInteger.One << checked(MaxBytes * 8)) - BigInteger.One;
        return (-maximum, maximum);
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(LibraDexBigIntVarIdentityIndex));
        }
    }
}
