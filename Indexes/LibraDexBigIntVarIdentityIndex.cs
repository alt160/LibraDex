using System.Numerics;

namespace LibraDex;

/// <summary>
/// Provides a first-class BigInteger key facade over fixed-width keys with raw variable-length identity bytes.<br/>
/// The explicit type avoids overloading `byte[]` scalar identity semantics, where fixed-width byte arrays already mean fixed blob lanes.<br/>
/// </summary>
public sealed class LibraDexBigIntVarIdentityIndex : IIndex, IIdentityPrimitiveExecutor, IIdentityPrimitiveTupleStreamer, IDisposable
{
    private readonly Catalog catalog;
    private readonly FixedNVarIdentityIndex inner;
    private readonly IndexKeys keyContract;
    private bool disposed;

    internal LibraDexBigIntVarIdentityIndex(
        Catalog catalog,
        string group,
        string name,
        FixedNVarIdentityIndex inner,
        int maxBytes,
        int maxIdentityBytes,
        IndexKeys keyContract)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
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
    /// Gets the open catalog that owns this index handle.<br/>
    /// </summary>
    public Catalog Catalog => catalog;

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

    public IdentityKeyMultiplicity IdentityKeyMultiplicity => IdentityKeyMultiplicity.MultipleKeysPerIdentity;

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
            LibraDexOperationDiagnostics.FromDataKernel(commit));
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
    /// Counts all raw identities visible through this BigInteger variable-identity index facade.<br/>
    /// The count path reads fixed-key / variable-identity shelf metadata and slot keys without copying raw identity byte arrays.<br/>
    /// </summary>
    /// <returns>The physical identity tuple count for this index facade.<br/></returns>
    public long Count()
    {
        return CountIdentityPrimitive(new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.All, Array.Empty<object?>()));
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

    /// <summary>
    /// Executes count as a fixed-key variable-identity aggregate over the condition-materialized BigInteger primitive.<br/>
    /// The aggregate uses fixed-N variable-identity shelf counts and routed range counts, avoiding raw identity byte-array materialization.<br/>
    /// </summary>
    /// <param name="request">The aggregate request to execute.<br/></param>
    /// <returns>The aggregate count result and physical plan classification.<br/></returns>
    LibraDexPrimitiveAggregateResult IIdentityPrimitiveAggregateExecutor.ExecuteIdentityPrimitiveAggregate(LibraDexPrimitiveAggregateRequest request)
    {
        if (request.Kind != LibraDexPrimitiveAggregateKind.Count)
        {
            throw new NotSupportedException($"{request.Kind} is not connected to BigInteger variable-identity aggregation yet.");
        }

        if (request.Scope != AggregateScope.Tuples)
        {
            throw new NotSupportedException($"{request.Scope} aggregate scope is not connected to BigInteger variable-identity aggregation yet.");
        }

        return LibraDexPrimitiveAggregateResult.ForCount(
            CountIdentityPrimitive(request.PrimitiveRequest),
            LibraDexPrimitiveAggregatePlanKind.RangeSlots);
    }

    IReadOnlyList<object> IIdentityPrimitiveExecutor.ExecuteAllIdentities()
    {
        return ExecuteIdentityPrimitive(new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.All, Array.Empty<object?>()));
    }

    IEnumerable<object> IIdentityPrimitiveExecutor.IterateIdentityUniverse()
    {
        return IterateIdentityPrimitive(new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.All, Array.Empty<object?>()));
    }

    IEnumerable<LibraDexObjectTuple> IIdentityPrimitiveTupleStreamer.IterateTuplePrimitive(
        LibraDexIdentityPrimitiveRequest request)
    {
        return IterateTuplePrimitive(request);
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

    /// <summary>
    /// Streams authoritative BigInteger/raw-identity tuples for full-index maintenance work.<br/>
    /// Fixed-N traversal owns each key and identity array, allowing the facade to decode BigInteger keys without retaining shelf-backed spans.<br/>
    /// </summary>
    /// <param name="request">The normalized primitive request; maintenance tuple streaming currently accepts only <see cref="LibraDexCriteriaKind.All"/>.<br/></param>
    /// <returns>Live logical tuples in physical key/identity order.<br/></returns>
    private IEnumerable<LibraDexObjectTuple> IterateTuplePrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        ThrowIfDisposed();
        if (request.CriteriaKind != LibraDexCriteriaKind.All)
            throw new NotSupportedException($"{request.CriteriaKind} tuple streaming is not connected to the BigInteger variable-identity facade.");
        if (request.TakeLimit is < 0)
            throw new ArgumentOutOfRangeException(nameof(request.TakeLimit), request.TakeLimit, "Take cannot be negative.");
        if (request.TakeLimit == 0)
            yield break;

        int yielded = 0;
        foreach (FixedNVarIdentityTuple tuple in inner.IterateTuples())
        {
            BigInteger key = LibraDexBigIntCodec.Decode(tuple.Key, MaxBytes, LibraDexBigIntKeyStorage.FixedWidth);
            yield return new LibraDexObjectTuple(key, tuple.Identity);
            yielded++;
            if (request.TakeLimit is int limit && yielded >= limit)
                yield break;
        }
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
        if (request.CriteriaKind == LibraDexCriteriaKind.All)
        {
            return inner.CountOrdinaryIdentities();
        }

        long count = 0;
        foreach ((BigInteger lower, BigInteger upper) in ExpandCountPrimitiveRanges(request))
        {
            if (lower > upper)
            {
                continue;
            }

            count += CountOrdinaryIdentityRange(lower, upper);
        }

        return count;
    }

    /// <summary>
    /// Expands a BigInteger variable-identity primitive into count-only ordered key ranges.<br/>
    /// Membership operands are deduplicated as set membership, and multirange operands are sorted and merged so overlapping ranges do not double count the same physical key extent.<br/>
    /// Iterator expansion remains separate to preserve plan-natural streaming behavior.<br/>
    /// </summary>
    /// <param name="request">The primitive request to normalize for counting.<br/></param>
    /// <returns>Inclusive BigInteger key ranges for count-only execution.<br/></returns>
    private IEnumerable<(BigInteger Lower, BigInteger Upper)> ExpandCountPrimitiveRanges(LibraDexIdentityPrimitiveRequest request)
    {
        return request.CriteriaKind switch
        {
            LibraDexCriteriaKind.In or LibraDexCriteriaKind.InSet => ExpandDistinctMembershipCountRanges(request.Values),
            LibraDexCriteriaKind.MultiRange => ExpandMergedMultiRangeCountRanges(request.Values),
            _ => ExpandPrimitiveRanges(request)
        };
    }

    /// <summary>
    /// Expands membership operands into one exact-key count range per distinct BigInteger key.<br/>
    /// This avoids repeated range counts when duplicate membership values are supplied while still counting every physical identity stored under each selected key.<br/>
    /// </summary>
    /// <param name="values">The primitive request values containing direct keys or a prepared set.<br/></param>
    /// <returns>Exact-key ranges in first-seen operand order.<br/></returns>
    private IEnumerable<(BigInteger Lower, BigInteger Upper)> ExpandDistinctMembershipCountRanges(IReadOnlyList<object?> values)
    {
        HashSet<BigInteger> seenKeys = new();
        foreach (BigInteger key in EnumerateMembershipKeys(values))
        {
            if (seenKeys.Add(key))
            {
                yield return (key, key);
            }
        }
    }

    /// <summary>
    /// Expands multirange operands into sorted non-overlapping BigInteger count ranges.<br/>
    /// The merge step treats the multirange operand as a range union, preventing duplicate counts when condition materialization produces duplicate or overlapping extents.<br/>
    /// </summary>
    /// <param name="values">The primitive request values containing one range array.<br/></param>
    /// <returns>Merged inclusive count ranges.<br/></returns>
    private static IEnumerable<(BigInteger Lower, BigInteger Upper)> ExpandMergedMultiRangeCountRanges(IReadOnlyList<object?> values)
    {
        LibraDexIdentityKeyRange[] ranges = RequireIdentityKeyRanges(values);
        if (ranges.Length == 0)
        {
            yield break;
        }

        (BigInteger Lower, BigInteger Upper)[] typedRanges = new (BigInteger Lower, BigInteger Upper)[ranges.Length];
        for (int i = 0; i < ranges.Length; i++)
        {
            BigInteger lower = RequireBigInteger(ranges[i].LowerKey, nameof(values));
            BigInteger upper = RequireBigInteger(ranges[i].UpperKey, nameof(values));
            if (lower > upper)
            {
                throw new ArgumentException("BigInt variable-identity multi-range count requires each lower key to be less than or equal to its upper key.", nameof(values));
            }

            typedRanges[i] = (lower, upper);
        }

        Array.Sort(typedRanges, static (left, right) =>
        {
            int lowerComparison = left.Lower.CompareTo(right.Lower);
            return lowerComparison != 0
                ? lowerComparison
                : left.Upper.CompareTo(right.Upper);
        });

        BigInteger currentLower = typedRanges[0].Lower;
        BigInteger currentUpper = typedRanges[0].Upper;
        for (int i = 1; i < typedRanges.Length; i++)
        {
            if (typedRanges[i].Lower <= currentUpper)
            {
                if (typedRanges[i].Upper > currentUpper)
                {
                    currentUpper = typedRanges[i].Upper;
                }

                continue;
            }

            yield return (currentLower, currentUpper);
            currentLower = typedRanges[i].Lower;
            currentUpper = typedRanges[i].Upper;
        }

        yield return (currentLower, currentUpper);
    }

    private long CountOrdinaryIdentityRange(BigInteger lowerKey, BigInteger upperKey)
    {
        ThrowIfDisposed();
        byte[] lower = LibraDexBigIntCodec.Encode(lowerKey, MaxBytes, LibraDexBigIntKeyStorage.FixedWidth);
        byte[] upper = LibraDexBigIntCodec.Encode(upperKey, MaxBytes, LibraDexBigIntKeyStorage.FixedWidth);
        return inner.CountIdentityRange(lower, upper);
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
