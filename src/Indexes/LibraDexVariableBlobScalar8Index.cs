namespace LibraDex;

/// <summary>
/// Provides a public bounded variable-length blob-key facade over routed `VS8` storage.<br/>
/// Keys retain exact byte-array semantics: null, empty, and non-empty payloads are distinct, and ordinary payloads are compared lexicographically by byte value.<br/>
/// The maximum key size is a persisted logical payload cap; LibraDex reserves its physical sentinel byte internally and does not expose that storage detail to callers.<br/>
/// </summary>
/// <typeparam name="TIdentity">The scalar identity type encoded into the physical 8-byte identity lane.<br/></typeparam>
public sealed class LibraDexVariableBlobScalar8Index<TIdentity> : IIndex, IIdentityPrimitiveExecutor, IIdentityPrimitiveExecutor<TIdentity>, IIdentityPrimitiveTupleStreamer, IDisposable
{
    private readonly Catalog catalog;
    private readonly VarKeyScalar8Index inner;
    private readonly IndexKeys keyContract;
    private readonly LibraDexIndexSortOrder sortOrder;
    private bool disposed;

    internal LibraDexVariableBlobScalar8Index(
        Catalog catalog,
        string group,
        string name,
        VarKeyScalar8Index inner,
        IndexKeys keyContract,
        LibraDexIndexSortOrder sortOrder = LibraDexIndexSortOrder.Ascending)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        if (LibraDexGenericScalarCodec<TIdentity>.ResolveWidth(null) != LibraDexScalarWidth.Bytes8)
            throw new NotSupportedException("Variable blob indexes currently require an identity type that encodes into the 8-byte scalar identity lane.");

        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Group = group;
        Name = name;
        this.keyContract = keyContract;
        this.sortOrder = sortOrder;
    }

    /// <summary>
    /// Gets the open catalog that owns this index and its session lifetime.<br/>
    /// </summary>
    public Catalog Catalog => catalog;

    /// <summary>
    /// Gets the logical index name inside its identity group.<br/>
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the identity group that owns this index.<br/>
    /// </summary>
    public string Group { get; }

    /// <summary>
    /// Gets the maximum developer-facing blob payload length accepted by this index.<br/>
    /// The internal variable-key sentinel byte is not included in this value.<br/>
    /// </summary>
    public int MaxKeyBytes => inner.MaxKeyLength;

    /// <summary>
    /// Gets the developer-facing key type.<br/>
    /// </summary>
    public Type KeyType => typeof(byte[]);

    /// <summary>
    /// Gets the developer-facing scalar identity type.<br/>
    /// </summary>
    public Type IdentityType => typeof(TIdentity);

    /// <summary>
    /// Gets the persisted duplicate-key contract.<br/>
    /// </summary>
    public IndexKeys KeyContract => keyContract;

    /// <summary>
    /// Gets the current identity-to-key multiplicity contract.<br/>
    /// Variable blob indexes allow one identity to occur under multiple keys.<br/>
    /// </summary>
    public IdentityKeyMultiplicity IdentityKeyMultiplicity => IdentityKeyMultiplicity.MultipleKeysPerIdentity;

    /// <summary>
    /// Gets the logical blob key family used by condition classification.<br/>
    /// </summary>
    public CatalogIndexKeyFamily KeyFamily => CatalogIndexKeyFamily.Blob;

    /// <summary>
    /// Gets the logical scalar identity family used by condition classification.<br/>
    /// </summary>
    public CatalogIndexIdentityFamily IdentityFamily => CatalogIndexIdentityFamily.Scalar;

    /// <summary>
    /// Gets the optional logical shape descriptor.<br/>
    /// Variable blob sizing is persisted directly in catalog metadata, so no additional shape descriptor is required.<br/>
    /// </summary>
    public LibraDexIndexShapeSpec? LogicalShape => null;

    /// <summary>
    /// Gets the persisted natural key traversal order for this variable-blob index.<br/>
    /// </summary>
    public LibraDexIndexSortOrder SortOrder => sortOrder;

    /// <summary>
    /// Gets strongly typed key-membership operations for this opened index.<br/>
    /// </summary>
    public LibraDexIndexKeys<byte[], TIdentity> Keys => new(this);

    /// <summary>
    /// Gets strongly typed identity-membership operations for this opened index.<br/>
    /// </summary>
    public LibraDexIndexIdentities<byte[], TIdentity> Identities => new(this);

    /// <summary>
    /// Gets strongly typed exact-entry operations for this opened index.<br/>
    /// </summary>
    public LibraDexIndexEntries<byte[], TIdentity> Entries => new(this);

    /// <summary>
    /// Counts all visible key/identity tuples without materializing identities.<br/>
    /// </summary>
    /// <returns>The number of stored tuples.</returns>
    public long Count()
    {
        ThrowIfDisposed();
        using VarKeyScalar8RangeReader reader = OpenAllReader();
        return reader.Count;
    }

    /// <summary>
    /// Inserts one exact nullable blob key and scalar identity.<br/>
    /// Null and empty arrays remain distinct persisted key states; non-empty payloads are rejected when they exceed <see cref="MaxKeyBytes"/>.<br/>
    /// </summary>
    /// <param name="key">The raw blob payload, or null for the null-key state.<br/></param>
    /// <param name="identity">The scalar identity associated with the key.<br/></param>
    /// <returns>The insert outcome and physical commit diagnostics.</returns>
    public LibraDexGenericInsertResult Insert(byte[]? key, TIdentity identity)
    {
        ThrowIfDisposed();
        VarKeyScalar8InsertOutcome result = inner.Insert(
            key,
            LibraDexGenericScalarCodec<TIdentity>.Encode8(identity),
            keyContract != IndexKeys.Unique);
        return new LibraDexGenericInsertResult(
            result.Inserted,
            result.CreatedInitialShelfRoute,
            default,
            LibraDexOperationDiagnostics.FromDataKernel(result.Commit));
    }

    /// <summary>
    /// Inserts one runtime blob key and scalar identity after strict type validation.<br/>
    /// <see cref="NullKey.Null"/> maps to null and <see cref="NullKey.Empty"/> maps to an empty array; other key-state values are predicates rather than writable keys.<br/>
    /// </summary>
    /// <param name="key">A byte array, null, or a concrete writable null-key state.<br/></param>
    /// <param name="identity">An identity assignable to <typeparamref name="TIdentity"/>.<br/></param>
    /// <returns>The insert outcome and physical commit diagnostics.</returns>
    public LibraDexGenericInsertResult Insert(object? key, object identity)
    {
        TIdentity typedIdentity = RequireIdentity(identity, nameof(identity));
        return key switch
        {
            null => Insert(null, typedIdentity),
            DBNull => Insert(null, typedIdentity),
            NullKey.Null => Insert(null, typedIdentity),
            NullKey.Empty => Insert(Array.Empty<byte>(), typedIdentity),
            byte[] bytes => Insert(bytes, typedIdentity),
            NullKey state => throw new ArgumentException($"{state} is a predicate state and cannot be inserted as one blob key.", nameof(key)),
            _ => throw new ArgumentException("Variable blob indexes require byte[] runtime keys.", nameof(key))
        };
    }

    /// <summary>
    /// Prepares byte-array membership values using LibraDex structural byte-array equality.<br/>
    /// Null values remain explicit null-key members and are not converted to empty arrays.<br/>
    /// </summary>
    /// <param name="keys">The runtime blob values to validate and prepare.</param>
    /// <returns>A prepared membership descriptor.</returns>
    public LibraDexPreparedObjectSet PrepareInSet(IEnumerable<object> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        List<object> prepared = new();
        foreach (object? key in keys)
        {
            prepared.Add(key switch
            {
                null => null!,
                DBNull => null!,
                NullKey.Null => null!,
                NullKey.Empty => Array.Empty<byte>(),
                byte[] bytes => bytes,
                _ => throw new ArgumentException("Variable blob membership requires byte[] or explicit null/empty key values.", nameof(keys))
            });
        }

        return new LibraDexPreparedObjectSet(typeof(byte[]), prepared);
    }

    IReadOnlyList<object> IIdentityPrimitiveExecutor.ExecuteIdentityPrimitive(LibraDexIdentityPrimitiveRequest request)
        => BoxIdentityIterator(IterateIdentityPrimitiveTyped(request)).ToArray();

    IEnumerable<object> IIdentityPrimitiveExecutor.IterateIdentityPrimitive(LibraDexIdentityPrimitiveRequest request)
        => BoxIdentityIterator(IterateIdentityPrimitiveTyped(request));

    IEnumerable<TIdentity> IIdentityPrimitiveExecutor<TIdentity>.IterateIdentityPrimitiveTyped(LibraDexIdentityPrimitiveRequest request)
        => IterateIdentityPrimitiveTyped(request);

    long IIdentityPrimitiveExecutor.CountIdentityPrimitive(LibraDexIdentityPrimitiveRequest request)
        => IterateIdentityPrimitiveTyped(request).LongCount();

    IReadOnlyList<object> IIdentityPrimitiveExecutor.ExecuteAllIdentities()
        => BoxIdentityIterator(IterateIdentityPrimitiveTyped(new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.All, Array.Empty<object?>()))).ToArray();

    IEnumerable<object> IIdentityPrimitiveExecutor.IterateIdentityUniverse()
        => BoxIdentityIterator(IterateIdentityPrimitiveTyped(new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.All, Array.Empty<object?>())));

    IEnumerable<TIdentity> IIdentityPrimitiveExecutor<TIdentity>.IterateIdentityUniverseTyped()
        => IterateIdentityPrimitiveTyped(new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.All, Array.Empty<object?>()));

    IEnumerable<LibraDexObjectTuple> IIdentityPrimitiveTupleStreamer.IterateTuplePrimitive(
        LibraDexIdentityPrimitiveRequest request)
        => IterateTuplePrimitive(request);

    /// <summary>
    /// Releases this facade's routed variable-key handle.<br/>
    /// The owning catalog retains responsibility for the shared session lifetime.<br/>
    /// </summary>
    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        inner.Dispose();
    }

    /// <summary>
    /// Adapts typed scalar identities to the legacy runtime-object executor contract.<br/>
    /// Typed condition projections bypass this adapter and consume physical scalar-8 identities without per-row boxing.<br/>
    /// </summary>
    /// <param name="identities">The typed identities to expose to object-shaped callers.<br/></param>
    /// <returns>A lazy boxed sequence preserving physical traversal order.<br/></returns>
    private static IEnumerable<object> BoxIdentityIterator(IEnumerable<TIdentity> identities)
    {
        foreach (TIdentity identity in identities)
        {
            yield return identity!;
        }
    }

    private IEnumerable<TIdentity> IterateIdentityPrimitiveTyped(LibraDexIdentityPrimitiveRequest request)
    {
        ThrowIfDisposed();
        if (request.TakeLimit is < 0)
            throw new ArgumentOutOfRangeException(nameof(request.TakeLimit), request.TakeLimit, "Take cannot be negative.");
        if (request.TakeLimit == 0)
            yield break;

        int yielded = 0;
        if (request.CriteriaKind == LibraDexCriteriaKind.All)
        {
            using VarKeyScalar8RangeReader allReader = OpenAllReader(request.Direction);
            foreach (TIdentity identity in IterateReader(allReader, request.TakeLimit, keyFilter: null, direction: request.Direction))
            {
                yield return identity;
            }

            yield break;
        }

        if (request.CriteriaKind == LibraDexCriteriaKind.BinaryPattern)
        {
            LibraDexBinaryPatternPredicate predicate =
                request.Values.Count != 0 &&
                request.Values[0] is LibraDexBinaryPatternPredicate compiled
                    ? compiled
                    : throw new InvalidOperationException("Variable blob binary-pattern execution requires one compiled binary predicate.");
            using VarKeyScalar8RangeReader patternReader = OpenAllReader(request.Direction);
            while (MoveReader(patternReader, request.Direction))
            {
                if (patternReader.CurrentKeyIsNull || !predicate.Matches(patternReader.CurrentKey))
                    continue;

                yield return LibraDexGenericScalarCodec<TIdentity>.Decode8(patternReader.CurrentEncodedIdentity)!;
                yielded++;
                if (request.TakeLimit is int limit && yielded >= limit)
                    yield break;
            }

            yield break;
        }

        if (request.CriteriaKind == LibraDexCriteriaKind.BinaryTypedSlice)
        {
            LibraDexBinaryTypedSlicePredicate predicate =
                request.Values.Count != 0 &&
                request.Values[0] is LibraDexBinaryTypedSlicePredicate compiled
                    ? compiled
                    : throw new InvalidOperationException("Variable blob typed-slice execution requires one compiled binary typed-slice predicate.");
            using VarKeyScalar8RangeReader typedSliceReader = OpenAllReader(request.Direction);
            while (MoveReader(typedSliceReader, request.Direction))
            {
                if (typedSliceReader.CurrentKeyIsNull || !predicate.Matches(typedSliceReader.CurrentKey))
                    continue;

                yield return LibraDexGenericScalarCodec<TIdentity>.Decode8(typedSliceReader.CurrentEncodedIdentity)!;
                yielded++;
                if (request.TakeLimit is int limit && yielded >= limit)
                    yield break;
            }

            yield break;
        }

        if (request.CriteriaKind is LibraDexCriteriaKind.Before or
            LibraDexCriteriaKind.AtOrBefore or
            LibraDexCriteriaKind.After or
            LibraDexCriteriaKind.AtOrAfter or
            LibraDexCriteriaKind.Between)
        {
            byte[] lower;
            byte[] upper;
            Func<byte[]?, bool>? filter = null;
            if (request.CriteriaKind == LibraDexCriteriaKind.Between)
            {
                lower = RequireBytes(request.Values, 0);
                upper = RequireBytes(request.Values, 1);
            }
            else
            {
                byte[] boundary = RequireBytes(request.Values, 0);
                bool before = request.CriteriaKind is LibraDexCriteriaKind.Before or LibraDexCriteriaKind.AtOrBefore;
                lower = before ? null! : boundary;
                upper = before ? boundary : CreateUpperBound();
                if (request.CriteriaKind is LibraDexCriteriaKind.Before or LibraDexCriteriaKind.After)
                {
                    filter = request.CriteriaKind == LibraDexCriteriaKind.Before
                        ? key => CompareKeys(key, boundary) < 0
                        : key => CompareKeys(key, boundary) > 0;
                }
            }

            using VarKeyScalar8RangeReader rangeReader = inner.OpenRangeReader(lower, upper, request.Direction);
            foreach (TIdentity identity in IterateReader(rangeReader, request.TakeLimit, filter, request.Direction))
                yield return identity;
            yield break;
        }

        foreach (byte[]? key in OrderedPointKeys(request))
        {
            using VarKeyScalar8RangeReader reader = inner.OpenRangeReader(key, key, request.Direction);
            while (MoveReader(reader, request.Direction))
            {
                yield return LibraDexGenericScalarCodec<TIdentity>.Decode8(reader.CurrentEncodedIdentity)!;
                yielded++;
                if (request.TakeLimit is int limit && yielded >= limit)
                    yield break;
            }
        }
    }

    /// <summary>
    /// Streams authoritative variable-blob tuples for full, exact, membership, range, pattern, and typed-slice requests.<br/>
    /// Point requests reuse the requested key without materializing it per tuple; scanning requests allocate only keys that survive their predicate.<br/>
    /// </summary>
    /// <param name="request">The normalized primitive tuple request.<br/></param>
    /// <returns>The live logical key/identity tuples in physical key order.<br/></returns>
    private IEnumerable<LibraDexObjectTuple> IterateTuplePrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        ThrowIfDisposed();
        if (request.TakeLimit is < 0)
            throw new ArgumentOutOfRangeException(nameof(request.TakeLimit), request.TakeLimit, "Take cannot be negative.");
        if (request.TakeLimit == 0)
            yield break;

        int yielded = 0;
        if (request.CriteriaKind == LibraDexCriteriaKind.All)
        {
            using VarKeyScalar8RangeReader allReader = OpenAllReader(request.Direction);
            while (MoveReader(allReader, request.Direction))
            {
                byte[]? key = allReader.CurrentKeyIsNull ? null : allReader.CurrentKey.ToArray();
                TIdentity identity = LibraDexGenericScalarCodec<TIdentity>.Decode8(allReader.CurrentEncodedIdentity);
                yield return new LibraDexObjectTuple(key, identity!);
                yielded++;
                if (request.TakeLimit is int limit && yielded >= limit)
                    yield break;
            }

            yield break;
        }

        if (request.CriteriaKind is LibraDexCriteriaKind.BinaryPattern or LibraDexCriteriaKind.BinaryTypedSlice)
        {
            LibraDexBinaryPatternPredicate? pattern = request.CriteriaKind == LibraDexCriteriaKind.BinaryPattern &&
                request.Values.Count != 0
                    ? request.Values[0] as LibraDexBinaryPatternPredicate
                    : null;
            LibraDexBinaryTypedSlicePredicate? slice = request.CriteriaKind == LibraDexCriteriaKind.BinaryTypedSlice &&
                request.Values.Count != 0
                    ? request.Values[0] as LibraDexBinaryTypedSlicePredicate
                    : null;
            if (pattern is null && slice is null)
                throw new InvalidOperationException("Variable blob tuple streaming requires one compiled binary predicate.");

            using VarKeyScalar8RangeReader predicateReader = OpenAllReader(request.Direction);
            while (MoveReader(predicateReader, request.Direction))
            {
                if (predicateReader.CurrentKeyIsNull ||
                    (pattern is not null && !pattern.Matches(predicateReader.CurrentKey)) ||
                    (slice is not null && !slice.Matches(predicateReader.CurrentKey)))
                {
                    continue;
                }

                byte[] key = predicateReader.CurrentKey.ToArray();
                TIdentity identity = LibraDexGenericScalarCodec<TIdentity>.Decode8(predicateReader.CurrentEncodedIdentity);
                yield return new LibraDexObjectTuple(key, identity!);
                yielded++;
                if (request.TakeLimit is int limit && yielded >= limit)
                    yield break;
            }

            yield break;
        }

        if (request.CriteriaKind is LibraDexCriteriaKind.Before or
            LibraDexCriteriaKind.AtOrBefore or
            LibraDexCriteriaKind.After or
            LibraDexCriteriaKind.AtOrAfter or
            LibraDexCriteriaKind.Between)
        {
            byte[] lower;
            byte[] upper;
            byte[]? exclusiveBoundary = null;
            if (request.CriteriaKind == LibraDexCriteriaKind.Between)
            {
                lower = RequireBytes(request.Values, 0);
                upper = RequireBytes(request.Values, 1);
            }
            else
            {
                byte[] boundary = RequireBytes(request.Values, 0);
                bool before = request.CriteriaKind is LibraDexCriteriaKind.Before or LibraDexCriteriaKind.AtOrBefore;
                lower = before ? null! : boundary;
                upper = before ? boundary : CreateUpperBound();
                if (request.CriteriaKind is LibraDexCriteriaKind.Before or LibraDexCriteriaKind.After)
                    exclusiveBoundary = boundary;
            }

            using VarKeyScalar8RangeReader rangeReader = inner.OpenRangeReader(lower, upper, request.Direction);
            while (MoveReader(rangeReader, request.Direction))
            {
                int comparison = exclusiveBoundary is null
                    ? 0
                    : rangeReader.CurrentKeyIsNull
                        ? -1
                        : rangeReader.CurrentKey.SequenceCompareTo(exclusiveBoundary);
                if ((request.CriteriaKind == LibraDexCriteriaKind.Before && comparison >= 0) ||
                    (request.CriteriaKind == LibraDexCriteriaKind.After && comparison <= 0))
                {
                    continue;
                }

                byte[]? key = rangeReader.CurrentKeyIsNull ? null : rangeReader.CurrentKey.ToArray();
                TIdentity identity = LibraDexGenericScalarCodec<TIdentity>.Decode8(rangeReader.CurrentEncodedIdentity);
                yield return new LibraDexObjectTuple(key, identity!);
                yielded++;
                if (request.TakeLimit is int limit && yielded >= limit)
                    yield break;
            }

            yield break;
        }

        foreach (byte[]? key in OrderedPointKeys(request))
        {
            using VarKeyScalar8RangeReader pointReader = inner.OpenRangeReader(key, key, request.Direction);
            while (MoveReader(pointReader, request.Direction))
            {
                TIdentity identity = LibraDexGenericScalarCodec<TIdentity>.Decode8(pointReader.CurrentEncodedIdentity);
                yield return new LibraDexObjectTuple(key, identity!);
                yielded++;
                if (request.TakeLimit is int limit && yielded >= limit)
                    yield break;
            }
        }
    }

    private IEnumerable<byte[]?> ExpandPointKeys(LibraDexIdentityPrimitiveRequest request)
    {
        switch (request.CriteriaKind)
        {
            case LibraDexCriteriaKind.Find:
                yield return RequireBytes(request.Values, 0);
                yield break;

            case LibraDexCriteriaKind.KeyState:
                foreach (byte[]? key in ExpandKeyState(request.Values))
                    yield return key;
                yield break;

            case LibraDexCriteriaKind.InSet:
                foreach (object? value in RequireMembershipValues(request.Values))
                {
                    yield return value switch
                    {
                        null => null,
                        byte[] bytes => bytes,
                        _ => throw new InvalidOperationException("Variable blob membership contains a non-byte-array value.")
                    };
                }
                yield break;

            default:
                throw new NotSupportedException($"{request.CriteriaKind} is not connected to the exact variable-blob facade yet.");
        }
    }

    private VarKeyScalar8RangeReader OpenAllReader(QueryDirection direction = QueryDirection.Ascending)
    {
        return inner.OpenRangeReader(null, CreateUpperBound(), direction);
    }

    private byte[] CreateUpperBound()
    {
        byte[] upper = new byte[MaxKeyBytes];
        Array.Fill(upper, byte.MaxValue);
        return upper;
    }

    private static IEnumerable<TIdentity> IterateReader(
        VarKeyScalar8RangeReader reader,
        int? takeLimit,
        Func<byte[]?, bool>? keyFilter,
        QueryDirection direction)
    {
        int yielded = 0;
        while (MoveReader(reader, direction))
        {
            byte[]? key = reader.CurrentKeyIsNull ? null : reader.MaterializeCurrentKey();
            if (keyFilter is not null && !keyFilter(key))
                continue;

            yield return LibraDexGenericScalarCodec<TIdentity>.Decode8(reader.CurrentEncodedIdentity)!;
            yielded++;
            if (takeLimit is int limit && yielded >= limit)
                yield break;
        }
    }

    private static int CompareKeys(byte[]? left, byte[] right)
    {
        return left is null ? -1 : left.AsSpan().SequenceCompareTo(right);
    }

    private static byte[] RequireBytes(IReadOnlyList<object?> values, int index)
    {
        if ((uint)index >= (uint)values.Count || values[index] is not byte[] bytes)
            throw new InvalidOperationException("Variable blob exact lookup requires a byte[] operand.");
        return bytes;
    }

    private static IEnumerable<byte[]?> ExpandKeyState(IReadOnlyList<object?> values)
    {
        if (values.Count == 0 || values[0] is not NullKey state)
            throw new InvalidOperationException("Variable blob key-state lookup requires a NullKey operand.");

        if (state is NullKey.Null or NullKey.NullOrEmpty)
            yield return null;
        if (state is NullKey.Empty or NullKey.NullOrEmpty)
            yield return Array.Empty<byte>();
    }

    private static IEnumerable<object?> RequireMembershipValues(IReadOnlyList<object?> values)
    {
        if (values.Count != 1 || values[0] is not IEnumerable<object> membership)
            throw new InvalidOperationException("Variable blob membership requires one object-enumerable operand.");
        return membership;
    }

    private static TIdentity RequireIdentity(object identity, string parameterName)
    {
        if (identity is TIdentity typed)
            return typed;
        throw new ArgumentException($"Variable blob indexes require identities assignable to {typeof(TIdentity).FullName}.", parameterName);
    }

    /// <summary>
    /// Deletes one exact nullable blob-key and scalar-identity tuple.<br/>
    /// Null, empty, and non-empty blob keys are encoded through the same persisted sentinel contract used by insertion, so deletion cannot conflate distinct key states.<br/>
    /// The operation removes only the requested tuple and preserves neighboring identities stored under the same non-unique key.<br/>
    /// </summary>
    /// <param name="key">The exact blob payload to delete, or null for the null-key state.<br/></param>
    /// <param name="identity">The exact scalar identity associated with the key.<br/></param>
    /// <returns><see langword="true"/> when the tuple existed and was removed; otherwise <see langword="false"/>.<br/></returns>
    public bool Delete(byte[]? key, TIdentity identity)
    {
        ThrowIfDisposed();
        byte[] encodedKey = LibraDexVarLenKeyCodec.Encode(key, inner.Handle.MaxKeyLength, nameof(key));
        bool deleted = inner.DeleteEncodedExactTuple(
            encodedKey,
            LibraDexGenericScalarCodec<TIdentity>.Encode8(identity));
        if (deleted)
            catalog.Stats.RecordDelete();
        return deleted;
    }

    /// <summary>
    /// Deletes one runtime blob-key and scalar-identity tuple after strict type validation.<br/>
    /// <see cref="NullKey.Null"/> maps to the null-key route and <see cref="NullKey.Empty"/> maps to an empty blob; predicate-only key states are rejected.<br/>
    /// This method supplies the non-generic <see cref="IIndex"/> mutation contract used by higher-level object stores such as Abraxas.<br/>
    /// </summary>
    /// <param name="key">A byte array, null, or a concrete writable null-key state.<br/></param>
    /// <param name="identity">An identity assignable to <typeparamref name="TIdentity"/>.<br/></param>
    /// <returns><see langword="true"/> when the tuple existed and was removed; otherwise <see langword="false"/>.<br/></returns>
    public bool Delete(object? key, object identity)
    {
        TIdentity typedIdentity = RequireIdentity(identity, nameof(identity));
        return key switch
        {
            null => Delete(null, typedIdentity),
            DBNull => Delete(null, typedIdentity),
            NullKey.Null => Delete(null, typedIdentity),
            NullKey.Empty => Delete(Array.Empty<byte>(), typedIdentity),
            byte[] bytes => Delete(bytes, typedIdentity),
            NullKey state => throw new ArgumentException($"{state} is a predicate state and cannot identify one writable blob tuple.", nameof(key)),
            _ => throw new ArgumentException("Variable blob indexes require byte[] runtime keys.", nameof(key))
        };
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    /// <summary>
    /// Advances a variable-key reader in the request's physical traversal direction.<br/>
    /// Both directions use the same index and yield complete key/identity tuple order without result sorting.<br/>
    /// </summary>
    /// <param name="reader">The positioned or unpositioned physical range reader.<br/></param>
    /// <param name="direction">The requested key and identity traversal direction.<br/></param>
    /// <returns><see langword="true"/> while another tuple is available.<br/></returns>
    private static bool MoveReader(VarKeyScalar8RangeReader reader, QueryDirection direction)
        => direction == QueryDirection.Descending ? reader.MovePrevious() : reader.MoveNext();

    /// <summary>
    /// Orders the small set of point routes in descending physical key order when requested.<br/>
    /// Each matching key then streams its stored identity run backward without buffering the result tuples.<br/>
    /// </summary>
    /// <param name="request">The point, key-state, or membership primitive request.<br/></param>
    /// <returns>Point keys in the request's natural traversal order.<br/></returns>
    private IEnumerable<byte[]?> OrderedPointKeys(LibraDexIdentityPrimitiveRequest request)
    {
        IEnumerable<byte[]?> keys = ExpandPointKeys(request);
        if (request.Direction != QueryDirection.Descending)
            return keys;
        List<byte[]?> ordered = keys.ToList();
        ordered.Sort(static (left, right) =>
            left is null ? right is null ? 0 : 1 :
            right is null ? -1 : right.AsSpan().SequenceCompareTo(left));
        return ordered;
    }
}
