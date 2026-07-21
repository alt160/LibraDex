namespace LibraDex;

/// <summary>
/// Provides a catalog-visible UInt64-key index over routed `SV8` storage with variable-length raw identity bytes.<br/>
/// This facade exists for workbench and adapter paths that need ordinary condition-builder scalar predicates while the identity is a raw path, blob, or other variable-length payload.<br/>
/// </summary>
public sealed class LibraDexUInt64VarIdentityIndex : IIndex, IIdentityPrimitiveExecutor, IIdentityPrimitiveTupleExecutor, IIdentityPrimitiveTupleStreamer, IDisposable
{
    private readonly Scalar8VarIdentityIndex inner;
    private readonly IndexKeys keyContract;
    private bool disposed;

    internal LibraDexUInt64VarIdentityIndex(
        string group,
        string name,
        Scalar8VarIdentityIndex inner,
        IndexKeys keyContract)
    {
        ArgumentNullException.ThrowIfNull(inner);
        Group = group;
        Name = name;
        this.inner = inner;
        this.keyContract = keyContract;
    }

    /// <summary>
    /// Gets the identity group name recorded for this index.<br/>
    /// Condition materialization uses this value to keep index-set predicates bound to one identity universe.<br/>
    /// </summary>
    public string Group { get; }

    /// <summary>
    /// Gets the logical index name recorded for this index.<br/>
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the maximum raw identity byte count accepted by this index.<br/>
    /// </summary>
    public int MaxIdentityBytes => inner.MaxIdentityLength;

    public Type KeyType => typeof(ulong);

    public Type IdentityType => typeof(byte[]);

    public IndexKeys KeyContract => keyContract;

    public IdentityKeyMultiplicity IdentityKeyMultiplicity => IdentityKeyMultiplicity.MultipleKeysPerIdentity;

    public CatalogIndexKeyFamily KeyFamily => CatalogIndexKeyFamily.Scalar;

    public CatalogIndexIdentityFamily IdentityFamily => CatalogIndexIdentityFamily.Blob;

    public LibraDexIndexShapeSpec? LogicalShape => null;

    /// <summary>
    /// Inserts one UInt64 key and variable-length identity byte payload into the `SV8` index.<br/>
    /// The UInt64 key is already the encoded sortable scalar lane used by the physical route, and the identity is copied into the variable-identity shelf record.<br/>
    /// </summary>
    /// <param name="key">The UInt64 key to index.</param>
    /// <param name="identity">The raw identity bytes associated with the key.</param>
    /// <returns>The generic insert result expected by catalog and workbench callers.</returns>
    public LibraDexGenericInsertResult Insert(ulong key, byte[] identity)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(identity);
        Scalar8VarIdentityInsertOutcome result = inner.Insert(key, identity, keyContract != IndexKeys.Unique);
        return new LibraDexGenericInsertResult(
            result.Inserted,
            result.CreatedInitialShelfRoute,
            default,
            LibraDexOperationDiagnostics.FromDataKernel(result.Commit));
    }

    /// <summary>
    /// Inserts one UInt64 key and variable-length identity byte payload into the caller's active durability scope.<br/>
    /// Catalog group-batching paths use this to avoid opening a nested `SV8` durability batch for each path-identity tuple while preserving the same routed insert semantics as <see cref="Insert(ulong, byte[])"/>.<br/>
    /// Commit telemetry is default because the outer group batch owns publication and timing.<br/>
    /// </summary>
    /// <param name="key">The UInt64 key to index.<br/></param>
    /// <param name="identity">The raw identity bytes associated with the key.<br/></param>
    /// <returns>The generic insert result expected by catalog and workbench callers.<br/></returns>
    internal LibraDexGenericInsertResult InsertInCurrentScope(ulong key, byte[] identity)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(identity);
        Scalar8VarIdentityInsertOutcome result = inner.InsertInCurrentScope(key, identity, keyContract != IndexKeys.Unique);
        return new LibraDexGenericInsertResult(
            result.Inserted,
            result.CreatedInitialShelfRoute,
            default,
            default);
    }

    internal Scalar8VarIdentityInsertOutcome InsertInCurrentScopeDetailed(ulong key, byte[] identity)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(identity);
        return inner.InsertInCurrentScope(key, identity, keyContract != IndexKeys.Unique);
    }

    /// <summary>
    /// Inserts one runtime key and variable-length identity after validating the values against this facade's public contract.<br/>
    /// Runtime callers should pass a UInt64-compatible key and a `byte[]` identity; narrower unsigned integers are widened without changing their ordering semantics.<br/>
    /// </summary>
    /// <param name="key">The runtime key value.</param>
    /// <param name="identity">The runtime identity byte payload.</param>
    /// <returns>The generic insert result expected by metadata-driven callers.</returns>
    public LibraDexGenericInsertResult Insert(object? key, object identity)
    {
        if (identity is not byte[] bytes)
        {
            throw new ArgumentException("UInt64 variable-identity indexes require byte[] runtime identities.", nameof(identity));
        }

        return Insert(RequireUInt64(key), bytes);
    }

    /// <summary>
    /// Prepares a strict non-generic key-membership set for condition-builder `InSet` calls.<br/>
    /// Values are validated as UInt64-compatible keys up front so execution can expand the prepared set without repeating type diagnostics late in the query path.<br/>
    /// </summary>
    /// <param name="keys">The runtime key values to validate.</param>
    /// <returns>A prepared object set containing normalized UInt64 key values.</returns>
    public LibraDexPreparedObjectSet PrepareInSet(IEnumerable<object> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        List<object> prepared = new();
        foreach (object key in keys)
        {
            prepared.Add(RequireUInt64(key));
        }

        object[] values = prepared.ToArray();
        return new LibraDexPreparedObjectSet(typeof(ulong), values);
    }

    /// <summary>
    /// Reads raw identities whose UInt64 keys fall inside the inclusive range.<br/>
    /// Returned arrays are disconnected copies owned by the caller, so the underlying pooled read buffer can be released immediately.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower UInt64 key.</param>
    /// <param name="upperKey">The inclusive upper UInt64 key.</param>
    /// <returns>Raw identity byte arrays matching the requested range.</returns>
    public IReadOnlyList<byte[]> GetIdentities(ulong lowerKey, ulong upperKey)
    {
        ThrowIfDisposed();
        using LibraDexVarIdentityBuffer buffer = inner.ReadIdentityBuffer(lowerKey, upperKey);
        byte[][] identities = new byte[buffer.Count][];
        for (int i = 0; i < identities.Length; i++)
        {
            identities[i] = buffer[i].ToArray();
        }

        return identities;
    }

    /// <summary>
    /// Describes where one exact UInt64 key is physically present in the underlying routed `SV8` tree.<br/>
    /// This internal diagnostic surface is for workbench route-pruning validation only; normal query paths should use condition-builder APIs or <see cref="GetIdentities(ulong, ulong)"/>.<br/>
    /// The report scans descendants and marks whether each matching shelf stayed on the exact routed key byte at every router hop.<br/>
    /// </summary>
    /// <param name="encodedKey">The exact UInt64 key to locate.<br/></param>
    /// <param name="maxRows">The maximum number of matching identity rows to include in the report.<br/></param>
    /// <returns>A multi-line diagnostic report.</returns>
    internal string DescribeExactKeyRoutes(ulong encodedKey, int maxRows)
    {
        ThrowIfDisposed();
        return inner.Session.DescribeScalar8VarIdentityExactKeyRoutes(inner.RootRouterOffset, inner.MaxIdentityLength, encodedKey, maxRows);
    }

    /// <summary>
    /// Deletes all rows for one exact UInt64 key through the underlying `SV8` range-delete path.<br/>
    /// This internal method exists for workbench route-pruning validation so selective delete can be tested against the same var-identity route topology as retrieval.<br/>
    /// </summary>
    /// <param name="encodedKey">The exact UInt64 key to delete.<br/></param>
    /// <returns>The number of rows deleted.</returns>
    internal long DeleteExactKeyForDiagnostics(ulong encodedKey)
    {
        ThrowIfDisposed();
        return inner.DeleteRange(encodedKey, encodedKey);
    }

    /// <summary>
    /// Counts raw tuples in one UInt64 range without materializing identity byte arrays.<br/>
    /// This is a diagnostic/workbench surface for measuring the routed `SV8` cursor's potential cost before caller-owned identity copies, string decoding, or row projection are added.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower UInt64 key.<br/></param>
    /// <param name="upperKey">The inclusive upper UInt64 key.<br/></param>
    /// <param name="identityBytes">Receives the sum of matching raw identity byte lengths.<br/></param>
    /// <returns>The number of matching tuples.</returns>
    internal long CountRawTuplesForDiagnostics(ulong lowerKey, ulong upperKey, out long identityBytes)
    {
        ThrowIfDisposed();
        long count = 0;
        long bytes = 0;
        using Scalar8VarIdentityRangeReader reader = inner.OpenRangeReader(lowerKey, upperKey);
        while (reader.MoveNext())
        {
            count++;
            bytes += reader.CurrentIdentityLength;
        }

        identityBytes = bytes;
        return count;
    }

    /// <summary>
    /// Opens the underlying raw `SV8` range reader for diagnostics that need skip/take without identity materialization.<br/>
    /// The caller owns the returned reader and must dispose it after the cursor pass completes.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower UInt64 key.<br/></param>
    /// <param name="upperKey">The inclusive upper UInt64 key.<br/></param>
    /// <returns>A raw `SV8` cursor over matching key/path-identity tuples.<br/></returns>
    internal Scalar8VarIdentityRangeReader OpenRawRangeReaderForDiagnostics(ulong lowerKey, ulong upperKey)
    {
        ThrowIfDisposed();
        return inner.OpenRangeReader(lowerKey, upperKey);
    }

    IReadOnlyList<object> IIdentityPrimitiveExecutor.ExecuteIdentityPrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        return IterateIdentityPrimitive(request).ToList();
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
    /// Executes count as a shape-native aggregate over the UInt64 variable-identity primitive path.<br/>
    /// The current aggregate uses routed `SV8` range-reader counts, so matching identities are counted from shelf-local slot ranges without copying raw identity bytes.<br/>
    /// </summary>
    /// <param name="request">The aggregate request to execute.<br/></param>
    /// <returns>The aggregate count result and physical plan classification.<br/></returns>
    LibraDexPrimitiveAggregateResult IIdentityPrimitiveAggregateExecutor.ExecuteIdentityPrimitiveAggregate(LibraDexPrimitiveAggregateRequest request)
    {
        if (request.Kind != LibraDexPrimitiveAggregateKind.Count)
        {
            throw new NotSupportedException($"{request.Kind} is not connected to UInt64 variable-identity aggregation yet.");
        }

        if (request.Scope != AggregateScope.Tuples)
        {
            throw new NotSupportedException($"{request.Scope} aggregate scope is not connected to UInt64 variable-identity aggregation yet.");
        }

        return LibraDexPrimitiveAggregateResult.ForCount(
            CountIdentityPrimitive(request.PrimitiveRequest),
            LibraDexPrimitiveAggregatePlanKind.RangeSlots);
    }

    IEnumerable<LibraDexObjectTuple> IIdentityPrimitiveTupleStreamer.IterateTuplePrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        return IterateTuplePrimitive(request);
    }

    /// <summary>
    /// Counts raw identities for one normalized condition-builder primitive without copying identity bytes.<br/>
    /// The count drains each routed `SV8` range reader only far enough to load shelf-local slot ranges, preserving cursor-shaped traversal for Abraxas-style identity queries.<br/>
    /// </summary>
    /// <param name="request">The primitive request to count.</param>
    /// <returns>The number of matching identity rows.</returns>
    private long CountIdentityPrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        long count = 0;
        foreach ((ulong lower, ulong upper) in ExpandCountPrimitiveRanges(request))
        {
            if (lower <= upper)
            {
                count += inner.CountIdentityRange(lower, upper);
            }
        }

        return count;
    }

    /// <summary>
    /// Expands a condition-builder primitive into inclusive UInt64 key ranges for count-only execution.<br/>
    /// Membership criteria are deduplicated as set membership so repeated operand keys do not count the same exact-key range more than once.<br/>
    /// Multi-range criteria are sorted and merged as range-union requests so overlapping extents do not count the same physical key span repeatedly.<br/>
    /// The iterator path intentionally keeps its original expansion so this optimization does not change plan-natural streaming order.<br/>
    /// </summary>
    /// <param name="request">The primitive request to normalize for counting.<br/></param>
    /// <returns>Inclusive UInt64 key ranges for count-only execution.<br/></returns>
    private static IEnumerable<(ulong Lower, ulong Upper)> ExpandCountPrimitiveRanges(LibraDexIdentityPrimitiveRequest request)
    {
        if (request.CriteriaKind is LibraDexCriteriaKind.In or LibraDexCriteriaKind.InSet)
        {
            return ExpandDistinctMembershipCountRanges(request.Values);
        }

        if (request.CriteriaKind == LibraDexCriteriaKind.MultiRange)
        {
            return ExpandMergedMultiRangeCountRanges(request.Values);
        }

        return ExpandPrimitiveRanges(request);
    }

    /// <summary>
    /// Expands UInt64 membership operands into one exact-key count range per distinct key.<br/>
    /// This preserves physical tuple multiplicity under each key while avoiding repeated range counts for duplicate operands.<br/>
    /// </summary>
    /// <param name="values">The primitive request values containing direct keys or prepared sets.<br/></param>
    /// <returns>Exact-key count ranges in first-seen operand order.<br/></returns>
    private static IEnumerable<(ulong Lower, ulong Upper)> ExpandDistinctMembershipCountRanges(IReadOnlyList<object?> values)
    {
        HashSet<ulong> seenKeys = new();
        foreach (ulong key in EnumerateMembershipKeys(values))
        {
            if (seenKeys.Add(key))
            {
                yield return (key, key);
            }
        }
    }

    /// <summary>
    /// Expands UInt64 multi-range operands into sorted non-overlapping count ranges.<br/>
    /// The merge step treats generated ranges as a union, preventing duplicate counts when adjacent or overlapping extents reach the same physical keys.<br/>
    /// </summary>
    /// <param name="values">The primitive request values containing one range array.<br/></param>
    /// <returns>Merged inclusive UInt64 key ranges for count-only execution.<br/></returns>
    private static IEnumerable<(ulong Lower, ulong Upper)> ExpandMergedMultiRangeCountRanges(IReadOnlyList<object?> values)
    {
        LibraDexIdentityKeyRange[] ranges = RequireIdentityKeyRanges(values);
        if (ranges.Length == 0)
        {
            yield break;
        }

        (ulong Lower, ulong Upper)[] typedRanges = new (ulong Lower, ulong Upper)[ranges.Length];
        for (int i = 0; i < ranges.Length; i++)
        {
            ulong lower = RequireUInt64(ranges[i].LowerKey);
            ulong upper = RequireUInt64(ranges[i].UpperKey);
            if (lower > upper)
            {
                throw new ArgumentException("UInt64 variable-identity multi-range count requires each lower key to be less than or equal to its upper key.", nameof(values));
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

        ulong currentLower = typedRanges[0].Lower;
        ulong currentUpper = typedRanges[0].Upper;
        for (int i = 1; i < typedRanges.Length; i++)
        {
            if (typedRanges[i].Lower <= currentUpper || currentUpper != ulong.MaxValue && typedRanges[i].Lower == currentUpper + 1)
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

    IReadOnlyList<object> IIdentityPrimitiveExecutor.ExecuteAllIdentities()
    {
        return IterateIdentityPrimitive(new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.All, Array.Empty<object?>())).ToList();
    }

    IEnumerable<object> IIdentityPrimitiveExecutor.IterateIdentityUniverse()
    {
        return IterateIdentityPrimitive(new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.All, Array.Empty<object?>()));
    }

    IReadOnlyList<LibraDexObjectTuple> IIdentityPrimitiveTupleExecutor.ExecuteTuplePrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        return IterateTuplePrimitive(request).ToList();
    }

    /// <summary>
    /// Releases the underlying index handle when this facade is disposed.<br/>
    /// Catalog-owned instances keep the shared catalog session alive because the wrapped raw index was opened with catalog ownership disabled.<br/>
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        inner.Dispose();
        disposed = true;
    }

    /// <summary>
    /// Streams raw identities for one normalized condition-builder primitive.<br/>
    /// This keeps plan-natural query execution from materializing more than the shape-specific range read already requires.<br/>
    /// </summary>
    /// <param name="request">The primitive request to execute.</param>
    /// <returns>A forward-only sequence of raw identity byte arrays.</returns>
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
        foreach ((ulong lower, ulong upper) in ExpandPrimitiveRanges(request))
        {
            if (lower > upper)
            {
                continue;
            }

            using Scalar8VarIdentityRangeReader reader = inner.OpenRangeReader(lower, upper);
            while (reader.MoveNext())
            {
                yield return reader.MaterializeCurrentIdentity();
                returned++;
                if (request.TakeLimit is not null && returned >= request.TakeLimit.Value)
                {
                    yield break;
                }
            }
        }
    }

    /// <summary>
    /// Streams key/identity tuples for one normalized condition-builder primitive.<br/>
    /// Each identity is copied into an owned array because the underlying SV8 reader exposes span-backed row storage whose lifetime ends on the next cursor movement.<br/>
    /// </summary>
    /// <param name="request">The primitive request to execute.</param>
    /// <returns>A forward-only sequence of runtime key/identity tuples.</returns>
    private IEnumerable<LibraDexObjectTuple> IterateTuplePrimitive(LibraDexIdentityPrimitiveRequest request)
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
        foreach ((ulong lower, ulong upper) in ExpandPrimitiveRanges(request))
        {
            if (lower > upper)
            {
                continue;
            }

            using Scalar8VarIdentityRangeReader reader = inner.OpenRangeReader(lower, upper);
            while (reader.MoveNext())
            {
                yield return new LibraDexObjectTuple(reader.CurrentEncodedKey, reader.MaterializeCurrentIdentity());
                returned++;
                if (request.TakeLimit is not null && returned >= request.TakeLimit.Value)
                {
                    yield break;
                }
            }
        }
    }

    /// <summary>
    /// Expands a condition-builder primitive into inclusive UInt64 key ranges accepted by the raw `SV8` reader.<br/>
    /// Membership requests are intentionally expanded as exact one-key ranges so caller-supplied key order is preserved by the outer iterator.<br/>
    /// </summary>
    /// <param name="request">The primitive request to normalize.</param>
    /// <returns>Inclusive UInt64 key ranges.</returns>
    private static IEnumerable<(ulong Lower, ulong Upper)> ExpandPrimitiveRanges(LibraDexIdentityPrimitiveRequest request)
    {
        switch (request.CriteriaKind)
        {
            case LibraDexCriteriaKind.All:
                yield return (0, ulong.MaxValue);
                yield break;
            case LibraDexCriteriaKind.Find:
            {
                ulong key = RequireUInt64(RequireValue(request.Values, 0));
                yield return (key, key);
                yield break;
            }
            case LibraDexCriteriaKind.Between:
                yield return (RequireUInt64(RequireValue(request.Values, 0)), RequireUInt64(RequireValue(request.Values, 1)));
                yield break;
            case LibraDexCriteriaKind.Before:
            {
                ulong key = RequireUInt64(RequireValue(request.Values, 0));
                if (key > 0)
                {
                    yield return (0, key - 1);
                }

                yield break;
            }
            case LibraDexCriteriaKind.AtOrBefore:
                yield return (0, RequireUInt64(RequireValue(request.Values, 0)));
                yield break;
            case LibraDexCriteriaKind.After:
            {
                ulong key = RequireUInt64(RequireValue(request.Values, 0));
                if (key < ulong.MaxValue)
                {
                    yield return (key + 1, ulong.MaxValue);
                }

                yield break;
            }
            case LibraDexCriteriaKind.AtOrAfter:
                yield return (RequireUInt64(RequireValue(request.Values, 0)), ulong.MaxValue);
                yield break;
            case LibraDexCriteriaKind.In:
            case LibraDexCriteriaKind.InSet:
                foreach (ulong key in EnumerateMembershipKeys(request.Values))
                {
                    yield return (key, key);
                }

                yield break;
            case LibraDexCriteriaKind.MultiRange:
                foreach (LibraDexIdentityKeyRange range in RequireIdentityKeyRanges(request.Values))
                {
                    yield return (RequireUInt64(range.LowerKey), RequireUInt64(range.UpperKey));
                }

                yield break;
            default:
                throw new NotSupportedException($"{request.CriteriaKind} identity execution is not connected for UInt64 variable-identity indexes yet.");
        }
    }

    /// <summary>
    /// Reads the captured multi-range operands from a condition-builder primitive request.<br/>
    /// The condition materializer owns construction of the range array; this helper keeps UInt64 facade execution strict about the expected payload shape.<br/>
    /// </summary>
    /// <param name="values">The primitive request values.</param>
    /// <returns>The captured key ranges.</returns>
    private static LibraDexIdentityKeyRange[] RequireIdentityKeyRanges(IReadOnlyList<object?> values)
    {
        if (values.Count != 1 || values[0] is not LibraDexIdentityKeyRange[] ranges)
        {
            throw new InvalidOperationException("UInt64 variable-identity multi-range execution requires a captured LibraDexIdentityKeyRange array.");
        }

        return ranges;
    }

    /// <summary>
    /// Enumerates UInt64 membership operand keys from direct values or prepared runtime sets.<br/>
    /// This helper is shared by count and iterator expansion so both paths keep identical operand validation while differing only in count-time deduplication.<br/>
    /// </summary>
    /// <param name="values">The primitive request values containing direct keys or prepared sets.<br/></param>
    /// <returns>The decoded UInt64 membership keys in caller operand order.<br/></returns>
    private static IEnumerable<ulong> EnumerateMembershipKeys(IReadOnlyList<object?> values)
    {
        for (int i = 0; i < values.Count; i++)
        {
            object? value = values[i];
            if (value is LibraDexPreparedObjectSet set)
            {
                for (int j = 0; j < set.Values.Count; j++)
                {
                    yield return RequireUInt64(set.Values[j]);
                }

                continue;
            }

            if (value is System.Collections.IEnumerable enumerable && value is not string)
            {
                foreach (object? item in enumerable)
                {
                    yield return RequireUInt64(item);
                }

                continue;
            }

            yield return RequireUInt64(value);
        }
    }

    /// <summary>
    /// Reads a required criterion operand from a condition-builder request.<br/>
    /// </summary>
    /// <param name="values">The criterion operand list.</param>
    /// <param name="ordinal">The zero-based operand ordinal.</param>
    /// <returns>The requested operand.</returns>
    private static object? RequireValue(IReadOnlyList<object?> values, int ordinal)
    {
        return values.Count > ordinal
            ? values[ordinal]
            : throw new InvalidOperationException("The UInt64 variable-identity criterion did not include the required operand.");
    }

    /// <summary>
    /// Converts one runtime scalar operand to the UInt64 key lane used by this facade.<br/>
    /// </summary>
    /// <param name="value">The runtime scalar operand.</param>
    /// <returns>The UInt64 key value.</returns>
    private static ulong RequireUInt64(object? value)
    {
        return value switch
        {
            ulong typed => typed,
            uint typed => typed,
            ushort typed => typed,
            byte typed => typed,
            _ => throw new ArgumentException("UInt64 variable-identity indexes require UInt64-compatible runtime keys.", nameof(value))
        };
    }

    /// <summary>
    /// Throws when this facade has already been disposed.<br/>
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(LibraDexUInt64VarIdentityIndex));
        }
    }
}
