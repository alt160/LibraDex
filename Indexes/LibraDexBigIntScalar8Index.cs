using System.Buffers.Binary;
using System.Numerics;

namespace LibraDex;

/// <summary>
/// Provides a first-class BigInteger key facade over fixed or variable BigInt key storage.<br/>
/// Fixed-width storage supports scalar-8 and scalar-16 identities through programmable `FSN` shelves; variable-width storage currently supports scalar-8 identities.<br/>
/// </summary>
/// <typeparam name="TIdentity">The scalar identity type encoded into the underlying fixed identity lane.</typeparam>
public sealed class LibraDexBigIntScalar8Index<TIdentity> : IIndex, IIdentityPrimitiveExecutor, IDisposable
{
    private readonly VarKeyScalar8Index? varInner;
    private readonly FixedNScalar8Index? fixedInner8;
    private readonly FixedNScalar16Index? fixedInner16;
    private readonly LibraDexBigIntKeyStorage storage;
    private readonly IndexKeys keyContract;
    private readonly LibraDexScalarWidth identityWidth;

    internal LibraDexBigIntScalar8Index(
        string group,
        string name,
        VarKeyScalar8Index inner,
        int maxBytes,
        LibraDexBigIntKeyStorage storage,
        IndexKeys keyContract)
    {
        ArgumentNullException.ThrowIfNull(inner);
        LibraDexBigIntCodec.ValidateMaxBytes(maxBytes, nameof(maxBytes));
        if (LibraDexGenericScalarCodec<TIdentity>.ResolveWidth(null) != LibraDexScalarWidth.Bytes8)
        {
            throw new NotSupportedException("BigIntVarLenKeys currently requires an identity type that encodes into the 8-byte scalar identity lane.");
        }

        Group = group;
        Name = name;
        varInner = inner;
        MaxBytes = maxBytes;
        this.storage = storage;
        this.keyContract = keyContract;
        identityWidth = LibraDexScalarWidth.Bytes8;
    }

    internal LibraDexBigIntScalar8Index(
        string group,
        string name,
        FixedNScalar8Index inner,
        int maxBytes,
        LibraDexBigIntKeyStorage storage,
        IndexKeys keyContract)
    {
        ArgumentNullException.ThrowIfNull(inner);
        LibraDexBigIntCodec.ValidateMaxBytes(maxBytes, nameof(maxBytes));
        if (LibraDexGenericScalarCodec<TIdentity>.ResolveWidth(null) != LibraDexScalarWidth.Bytes8)
        {
            throw new NotSupportedException("BigInt key indexes currently require an identity type that encodes into the 8-byte scalar identity lane.");
        }

        if (storage != LibraDexBigIntKeyStorage.FixedWidth)
        {
            throw new ArgumentException("The fixed FSN BigInt facade requires fixed-width storage.", nameof(storage));
        }

        Group = group;
        Name = name;
        fixedInner8 = inner;
        MaxBytes = maxBytes;
        this.storage = storage;
        this.keyContract = keyContract;
        identityWidth = LibraDexScalarWidth.Bytes8;
    }

    internal LibraDexBigIntScalar8Index(
        string group,
        string name,
        FixedNScalar16Index inner,
        int maxBytes,
        LibraDexBigIntKeyStorage storage,
        IndexKeys keyContract)
    {
        ArgumentNullException.ThrowIfNull(inner);
        LibraDexBigIntCodec.ValidateMaxBytes(maxBytes, nameof(maxBytes));
        if (LibraDexGenericScalarCodec<TIdentity>.ResolveWidth(null) != LibraDexScalarWidth.Bytes16)
        {
            throw new NotSupportedException("Fixed BigInt FSN-16 indexes require an identity type that encodes into the 16-byte scalar identity lane.");
        }

        if (storage != LibraDexBigIntKeyStorage.FixedWidth)
        {
            throw new ArgumentException("The fixed FSN BigInt facade requires fixed-width storage.", nameof(storage));
        }

        Group = group;
        Name = name;
        fixedInner16 = inner;
        MaxBytes = maxBytes;
        this.storage = storage;
        this.keyContract = keyContract;
        identityWidth = LibraDexScalarWidth.Bytes16;
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
    /// Gets the maximum canonical magnitude byte count accepted by this BigInt index.<br/>
    /// The value is part of the persisted index shape and is validated for both inserted values and query boundary values.<br/>
    /// </summary>
    public int MaxBytes { get; }

    /// <summary>
    /// Gets whether this facade stores fixed-width normalized BigInt keys.<br/>
    /// The current public BigInt facade is expected to return <see langword="false"/> because fixed BigInt keys wait for FixedN shelf storage.<br/>
    /// </summary>
    public bool IsFixedWidth => storage == LibraDexBigIntKeyStorage.FixedWidth;

    /// <summary>
    /// Gets the CLR key type accepted by this index.<br/>
    /// </summary>
    public Type KeyType => typeof(BigInteger);

    /// <summary>
    /// Gets the CLR identity type returned by this index.<br/>
    /// </summary>
    public Type IdentityType => typeof(TIdentity);

    /// <summary>
    /// Gets the duplicate-key contract selected when the index was created.<br/>
    /// </summary>
    public IndexKeys KeyContract => keyContract;

    /// <summary>
    /// Gets the logical key family recorded for this index.<br/>
    /// </summary>
    public CatalogIndexKeyFamily KeyFamily => CatalogIndexKeyFamily.BigInt;

    /// <summary>
    /// Gets the logical identity family recorded for this index.<br/>
    /// </summary>
    public CatalogIndexIdentityFamily IdentityFamily => CatalogIndexIdentityFamily.Scalar;

    /// <summary>
    /// Gets the logical shape descriptor for this facade when available.<br/>
    /// BigInt metadata is currently stored directly in catalog metadata rather than through a composite shape descriptor.<br/>
    /// </summary>
    public LibraDexIndexShapeSpec? LogicalShape => null;

    /// <summary>
    /// Adds one BigInteger key and identity to this index.<br/>
    /// The key is converted once into LibraDex's sortable BigInt byte format, then routed through the underlying byte-key index.<br/>
    /// </summary>
    /// <param name="key">The BigInteger key value to add.</param>
    /// <param name="identity">The identity associated with the key.</param>
    /// <returns>The insert result plus any commit telemetry surfaced by the underlying routed storage.</returns>
    public LibraDexGenericInsertResult Add(BigInteger key, TIdentity identity)
    {
        return Insert(key, identity);
    }

    /// <summary>
    /// Inserts one BigInteger key and identity into this index.<br/>
    /// This method is equivalent to <see cref="Add(BigInteger, TIdentity)"/> and remains available for callers that prefer insert terminology.<br/>
    /// </summary>
    /// <param name="key">The BigInteger key value to insert.</param>
    /// <param name="identity">The identity associated with the key.</param>
    /// <returns>The insert result plus any commit telemetry surfaced by the underlying routed storage.</returns>
    public LibraDexGenericInsertResult Insert(BigInteger key, TIdentity identity)
    {
        byte[] encodedKey = EncodeKey(key);
        if (storage == LibraDexBigIntKeyStorage.FixedWidth)
        {
            if (identityWidth == LibraDexScalarWidth.Bytes8)
            {
                ulong encodedIdentity = LibraDexGenericScalarCodec<TIdentity>.Encode8(identity);
                FixedNScalar8Index inner = fixedInner8 ?? throw new ObjectDisposedException(nameof(LibraDexBigIntScalar8Index<TIdentity>));
                (FixedNScalarInsertResult result, DataKernelCommitTelemetry commit) = inner.Insert(encodedKey, encodedIdentity, keyContract != IndexKeys.Unique);
                return new LibraDexGenericInsertResult(
                    result == FixedNScalarInsertResult.Inserted,
                    CreatedInitialShelfRoute: false,
                    default,
                    commit);
            }
            else
            {
                Span<byte> encodedIdentity = stackalloc byte[16];
                EncodeIdentity16(identity, encodedIdentity);
                FixedNScalar16Index inner = fixedInner16 ?? throw new ObjectDisposedException(nameof(LibraDexBigIntScalar8Index<TIdentity>));
                (FixedNScalarInsertResult result, DataKernelCommitTelemetry commit) = inner.Insert(encodedKey, encodedIdentity, keyContract != IndexKeys.Unique);
                return new LibraDexGenericInsertResult(
                    result == FixedNScalarInsertResult.Inserted,
                    CreatedInitialShelfRoute: false,
                    default,
                    commit);
            }
        }
        else
        {
            ulong encodedIdentity = LibraDexGenericScalarCodec<TIdentity>.Encode8(identity);
            VarKeyScalar8Index inner = varInner ?? throw new ObjectDisposedException(nameof(LibraDexBigIntScalar8Index<TIdentity>));
            VarKeyScalar8InsertOutcome result = inner.Insert(encodedKey, encodedIdentity, keyContract != IndexKeys.Unique);
            return new LibraDexGenericInsertResult(
                result.Inserted,
                result.CreatedInitialShelfRoute,
                default,
                result.Commit);
        }
    }

    /// <summary>
    /// Inserts one runtime key and runtime identity after validating both values against this BigInt facade's type contract.<br/>
    /// </summary>
    /// <param name="key">The runtime key value; it must be a <see cref="BigInteger"/>.</param>
    /// <param name="identity">The runtime identity value; it must match <typeparamref name="TIdentity"/>.</param>
    /// <returns>The insert result plus any commit telemetry surfaced by the underlying routed storage.</returns>
    public LibraDexGenericInsertResult Insert(object? key, object identity)
    {
        if (key is not BigInteger typedKey)
        {
            throw new ArgumentException("BigInt indexes require BigInteger runtime keys.", nameof(key));
        }

        if (identity is not TIdentity typedIdentity)
        {
            throw new ArgumentException($"BigInt indexes require identities assignable to {typeof(TIdentity).FullName}.", nameof(identity));
        }

        return Insert(typedKey, typedIdentity);
    }

    /// <summary>
    /// Reads identities whose BigInteger keys fall inside the inclusive range.<br/>
    /// The method keeps the comparison in encoded-key order so fixed and variable BigInt storage share the same public retrieval behavior.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower BigInteger key.</param>
    /// <param name="upperKey">The inclusive upper BigInteger key.</param>
    /// <returns>Decoded identities matching the requested range.</returns>
    public IReadOnlyList<TIdentity> GetIdentities(BigInteger lowerKey, BigInteger upperKey)
    {
        byte[] lower = EncodeKey(lowerKey);
        byte[] upper = EncodeKey(upperKey);
        if (storage == LibraDexBigIntKeyStorage.FixedWidth)
        {
            if (identityWidth == LibraDexScalarWidth.Bytes8)
            {
                FixedNScalar8Index inner = fixedInner8 ?? throw new ObjectDisposedException(nameof(LibraDexBigIntScalar8Index<TIdentity>));
                ulong[] encodedIdentities = inner.ReadIdentityRange(lower, upper);
                TIdentity[] identities = new TIdentity[encodedIdentities.Length];
                for (int i = 0; i < encodedIdentities.Length; i++)
                {
                    identities[i] = LibraDexGenericScalarCodec<TIdentity>.Decode8(encodedIdentities[i]);
                }

                return identities;
            }
            else
            {
                FixedNScalar16Index inner = fixedInner16 ?? throw new ObjectDisposedException(nameof(LibraDexBigIntScalar8Index<TIdentity>));
                byte[] encodedIdentities = inner.ReadIdentityRange(lower, upper);
                TIdentity[] identities = new TIdentity[encodedIdentities.Length / 16];
                for (int i = 0; i < identities.Length; i++)
                {
                    ReadOnlySpan<byte> source = encodedIdentities.AsSpan(i * 16, 16);
                    ulong high = BinaryPrimitives.ReadUInt64BigEndian(source[..8]);
                    ulong low = BinaryPrimitives.ReadUInt64BigEndian(source.Slice(8, 8));
                    identities[i] = LibraDexGenericScalarCodec<TIdentity>.Decode16(high, low);
                }

                return identities;
            }
        }
        else
        {
            VarKeyScalar8Index inner = varInner ?? throw new ObjectDisposedException(nameof(LibraDexBigIntScalar8Index<TIdentity>));
            using VarKeyScalar8RangeReader reader = inner.OpenRangeReader(lower, upper);
            List<TIdentity> identities = new(reader.Count);
            while (reader.MoveNext())
            {
                identities.Add(LibraDexGenericScalarCodec<TIdentity>.Decode8(reader.CurrentEncodedIdentity));
            }

            return identities;
        }
    }

    /// <summary>
    /// Reads identities whose BigInteger key exactly matches <paramref name="key"/>.<br/>
    /// </summary>
    /// <param name="key">The exact BigInteger key.</param>
    /// <returns>Decoded identities matching the exact key.</returns>
    public IReadOnlyList<TIdentity> GetIdentities(BigInteger key)
    {
        return GetIdentities(key, key);
    }

    /// <summary>
    /// Prepares a strict non-generic key-membership set for condition-builder `InSet` calls.<br/>
    /// BigInt condition execution consumes this prepared set through the same internal primitive bridge as exact and range predicates, while preserving strict key-type validation.<br/>
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
                throw new ArgumentException("BigInt indexes require BigInteger values in prepared sets.", nameof(keys));
            }

            _ = EncodeKey(big);
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

    /// <summary>
    /// Releases the underlying routed var-key index handle.<br/>
    /// </summary>
    public void Dispose()
    {
        varInner?.Dispose();
        fixedInner8?.Dispose();
        fixedInner16?.Dispose();
    }

    private IReadOnlyList<object> ExecuteIdentityPrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        return IterateIdentityPrimitive(request).ToList();
    }

    private IEnumerable<object> IterateIdentityPrimitive(LibraDexIdentityPrimitiveRequest request)
    {
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

            foreach (TIdentity identity in GetIdentities(lower, upper))
            {
                yield return identity!;
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
                throw new NotSupportedException($"{request.CriteriaKind} BigInt identity execution is not connected to physical readers yet.");
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
            throw new InvalidOperationException("BigInt multi-range execution requires a captured LibraDexIdentityKeyRange array.");
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

    private byte[] EncodeKey(BigInteger key)
    {
        return LibraDexBigIntCodec.Encode(key, MaxBytes, storage);
    }

    private static void EncodeIdentity16(TIdentity identity, Span<byte> destination)
    {
        LibraDexGenericScalarCodec<TIdentity>.Encode16(identity, out ulong high, out ulong low);
        BinaryPrimitives.WriteUInt64BigEndian(destination[..8], high);
        BinaryPrimitives.WriteUInt64BigEndian(destination.Slice(8, 8), low);
    }
}
