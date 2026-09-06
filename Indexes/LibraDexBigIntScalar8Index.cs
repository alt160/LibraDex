using System.Buffers.Binary;
using System.Numerics;

namespace LibraDex;

/// <summary>
/// Provides a first-class BigInteger key facade over fixed or variable BigInt key storage.<br/>
/// Fixed-width storage supports scalar-8 and scalar-16 identities through programmable `FSN` shelves; variable-width storage currently supports scalar-8 identities.<br/>
/// </summary>
/// <typeparam name="TIdentity">The scalar identity type encoded into the underlying fixed identity lane.</typeparam>
public sealed class LibraDexBigIntScalar8Index<TIdentity> : IIndex, IIdentityPrimitiveExecutor, IIdentityPrimitiveExecutor<TIdentity>, IIdentityPrimitiveTupleStreamer, IDisposable
{
    private readonly Catalog catalog;
    private readonly VarKeyScalar8Index? varInner;
    private readonly FixedNScalar8Index? fixedInner8;
    private readonly FixedNScalar16Index? fixedInner16;
    private readonly LibraDexBigIntKeyStorage storage;
    private readonly IndexKeys keyContract;
    private readonly LibraDexScalarWidth identityWidth;

    internal LibraDexBigIntScalar8Index(
        Catalog catalog,
        string group,
        string name,
        VarKeyScalar8Index inner,
        int maxBytes,
        LibraDexBigIntKeyStorage storage,
        IndexKeys keyContract)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
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
        Catalog catalog,
        string group,
        string name,
        FixedNScalar8Index inner,
        int maxBytes,
        LibraDexBigIntKeyStorage storage,
        IndexKeys keyContract)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
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
        Catalog catalog,
        string group,
        string name,
        FixedNScalar16Index inner,
        int maxBytes,
        LibraDexBigIntKeyStorage storage,
        IndexKeys keyContract)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
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
    /// Gets the open catalog that owns this index handle.<br/>
    /// </summary>
    public Catalog Catalog => catalog;

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

    public IdentityKeyMultiplicity IdentityKeyMultiplicity => IdentityKeyMultiplicity.MultipleKeysPerIdentity;

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
    /// Adds one identity to this fixed-width BigInteger index's scalar null route.<br/>
    /// Use <see cref="ScalarNull.Null"/> as the only accepted state; <see cref="ScalarNull.NonNull"/> is a predicate state and should be written with a concrete <see cref="BigInteger"/> key through <see cref="Insert(BigInteger, TIdentity)"/>.<br/>
    /// Fixed-N stores the null state in the slot-owned identity route instead of the fixed-key value shelves, so optional-key writes do not contend with ordinary routed fixed-key inserts.<br/>
    /// </summary>
    /// <param name="keyState">The scalar key state to write; only <see cref="ScalarNull.Null"/> is accepted.<br/></param>
    /// <param name="identity">The identity associated with the scalar null key state.<br/></param>
    /// <returns>The insert result reported through the generic public result contract.<br/></returns>
    public LibraDexGenericInsertResult Add(ScalarNull keyState, TIdentity identity)
    {
        return Insert(keyState, identity);
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
                    LibraDexOperationDiagnostics.FromDataKernel(commit));
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
                    LibraDexOperationDiagnostics.FromDataKernel(commit));
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
                LibraDexOperationDiagnostics.FromDataKernel(result.Commit));
        }
    }

    /// <summary>
    /// Inserts one identity into this fixed-width BigInteger index's scalar null route.<br/>
    /// The null route is identity-keyed, so duplicate identity writes are no-ops and unique-key indexes reject a second different identity for the null state.<br/>
    /// Variable-width BigInteger storage does not yet expose a slot-owned key-state route through this facade, so this method currently targets fixed-N storage only.<br/>
    /// </summary>
    /// <param name="keyState">The scalar key state to write; only <see cref="ScalarNull.Null"/> is accepted.<br/></param>
    /// <param name="identity">The identity associated with the scalar null key state.<br/></param>
    /// <returns>The insert result reported through the generic public result contract.<br/></returns>
    public LibraDexGenericInsertResult Insert(ScalarNull keyState, TIdentity identity)
    {
        EnsureFixedScalarNullRouteSupported();
        if (keyState != ScalarNull.Null)
        {
            throw new ArgumentOutOfRangeException(nameof(keyState), keyState, "Only ScalarNull.Null is a concrete scalar key state for insertion.");
        }

        bool inserted = InsertScalarNullIdentity(identity);
        return new LibraDexGenericInsertResult(inserted, false, default, default);
    }

    /// <summary>
    /// Inserts one runtime key and runtime identity after validating both values against this BigInt facade's type contract.<br/>
    /// </summary>
    /// <param name="key">The runtime key value; it must be a <see cref="BigInteger"/>.</param>
    /// <param name="identity">The runtime identity value; it must match <typeparamref name="TIdentity"/>.</param>
    /// <returns>The insert result plus any commit telemetry surfaced by the underlying routed storage.</returns>
    public LibraDexGenericInsertResult Insert(object? key, object identity)
    {
        if (key is null || key == DBNull.Value)
        {
            return Insert(ScalarNull.Null, RequireObjectIdentity(identity, nameof(identity)));
        }

        if (key is ScalarNull keyState)
        {
            return Insert(keyState, RequireObjectIdentity(identity, nameof(identity)));
        }

        if (key is not BigInteger typedKey)
        {
            throw new ArgumentException("BigInt indexes require BigInteger runtime keys.", nameof(key));
        }

        return Insert(typedKey, RequireObjectIdentity(identity, nameof(identity)));
    }

    /// <summary>
    /// Starts a fixed-width BigInteger durability batch for ordinary-key inserts.<br/>
    /// The first batch slice is intentionally shelf-local: warmed routes that fit in their current leaf shelves are coalesced into batch-local shelf images and published once when the batch commits.<br/>
    /// </summary>
    /// <param name="writeIntent">Optional coarse write-pattern hints for the active batch.<br/></param>
    /// <returns>A BigInteger batch that owns the publication boundary.<br/></returns>
    public LibraDexBigIntScalar8Batch<TIdentity> BeginBatch(LibraDexWriteIntent writeIntent = default)
    {
        EnsureFixedWidthOrdinaryBatchSupported();
        LibraDexFileSessionDurabilityBatch batch = identityWidth == LibraDexScalarWidth.Bytes8
            ? (fixedInner8 ?? throw new ObjectDisposedException(nameof(LibraDexBigIntScalar8Index<TIdentity>))).BeginDurabilityBatch(writeIntent)
            : (fixedInner16 ?? throw new ObjectDisposedException(nameof(LibraDexBigIntScalar8Index<TIdentity>))).BeginDurabilityBatch(writeIntent);
        return new LibraDexBigIntScalar8Batch<TIdentity>(this, batch);
    }

    /// <summary>
    /// Stages one ordinary fixed-width BigInteger insert into an active BigInt batch.<br/>
    /// The method returns <see langword="false"/> only when route topology work is required, leaving the caller-owned batch unchanged except for prior staged operations.<br/>
    /// </summary>
    /// <param name="durabilityBatch">The active durability batch that owns publication.<br/></param>
    /// <param name="key">The BigInteger key value to encode and stage.<br/></param>
    /// <param name="identity">The identity associated with the key.<br/></param>
    /// <returns>The insert result and whether the current batch slice handled the operation.<br/></returns>
    internal (bool Handled, LibraDexGenericInsertResult Result) InsertForBatch(
        LibraDexFileSessionDurabilityBatch durabilityBatch,
        BigInteger key,
        TIdentity identity)
    {
        (bool handled, FixedNScalarInsertResult result) = InsertFixedWidthForBatch(durabilityBatch, key, identity);
        return (handled, new LibraDexGenericInsertResult(result == FixedNScalarInsertResult.Inserted, false, default, default));
    }

    /// <summary>
    /// Stages one ordinary fixed-width BigInteger insert while preserving the shape-native insert result for composed mutations.<br/>
    /// Batch rekey uses the structural result to distinguish an already-present replacement tuple from a unique-key conflict.<br/>
    /// </summary>
    /// <param name="durabilityBatch">The active durability batch that owns publication.<br/></param>
    /// <param name="key">The BigInteger key value to encode and stage.<br/></param>
    /// <param name="identity">The identity associated with the key.<br/></param>
    /// <returns>The fixed-N insert result and whether the current batch slice handled the operation.<br/></returns>
    internal (bool Handled, FixedNScalarInsertResult Result) InsertFixedWidthForBatch(
        LibraDexFileSessionDurabilityBatch durabilityBatch,
        BigInteger key,
        TIdentity identity)
    {
        EnsureFixedWidthOrdinaryBatchSupported();
        byte[] encodedKey = EncodeKey(key);
        if (identityWidth == LibraDexScalarWidth.Bytes8)
        {
            FixedNScalar8Index inner = fixedInner8 ?? throw new ObjectDisposedException(nameof(LibraDexBigIntScalar8Index<TIdentity>));
            (bool handled, FixedNScalarInsertResult result) = inner.InsertForDurabilityBatch(
                durabilityBatch,
                encodedKey,
                LibraDexGenericScalarCodec<TIdentity>.Encode8(identity),
                keyContract != IndexKeys.Unique);
            return (handled, result);
        }

        Span<byte> encodedIdentity = stackalloc byte[16];
        EncodeIdentity16(identity, encodedIdentity);
        FixedNScalar16Index inner16 = fixedInner16 ?? throw new ObjectDisposedException(nameof(LibraDexBigIntScalar8Index<TIdentity>));
        (bool handled16, FixedNScalarInsertResult result16) = inner16.InsertForDurabilityBatch(
            durabilityBatch,
            encodedKey,
            encodedIdentity,
            keyContract != IndexKeys.Unique);
        return (handled16, result16);
    }

    /// <summary>
    /// Stages one ordinary fixed-width BigInteger exact delete into an active BigInt batch.<br/>
    /// The current slice handles warmed shelf-local ordinary-key deletes and leaves topology-changing cases to one-shot writes.<br/>
    /// </summary>
    /// <param name="durabilityBatch">The active durability batch that owns publication.<br/></param>
    /// <param name="key">The BigInteger key value to delete.<br/></param>
    /// <param name="identity">The identity associated with the key.<br/></param>
    /// <returns>The delete result and whether the current batch slice handled the operation.<br/></returns>
    internal (bool Handled, LibraDexGenericDeleteResult Result) DeleteForBatch(
        LibraDexFileSessionDurabilityBatch durabilityBatch,
        BigInteger key,
        TIdentity identity)
    {
        EnsureFixedWidthOrdinaryBatchSupported();
        byte[] encodedKey = EncodeKey(key);
        if (identityWidth == LibraDexScalarWidth.Bytes8)
        {
            FixedNScalar8Index inner = fixedInner8 ?? throw new ObjectDisposedException(nameof(LibraDexBigIntScalar8Index<TIdentity>));
            (bool handled, bool deleted) = inner.DeleteForDurabilityBatch(
                durabilityBatch,
                encodedKey,
                LibraDexGenericScalarCodec<TIdentity>.Encode8(identity));
            return (handled, new LibraDexGenericDeleteResult(deleted, default));
        }

        Span<byte> encodedIdentity = stackalloc byte[16];
        EncodeIdentity16(identity, encodedIdentity);
        FixedNScalar16Index inner16 = fixedInner16 ?? throw new ObjectDisposedException(nameof(LibraDexBigIntScalar8Index<TIdentity>));
        (bool handled16, bool deleted16) = inner16.DeleteForDurabilityBatch(
            durabilityBatch,
            encodedKey,
            encodedIdentity);
        return (handled16, new LibraDexGenericDeleteResult(deleted16, default));
    }

    /// <summary>
    /// Stages one ordinary fixed-width BigInteger rekey into an active BigInt batch.<br/>
    /// The replacement tuple is staged before the old exact tuple is removed, matching LibraDex identity-index mutation semantics without implying SQL rollback behavior.<br/>
    /// </summary>
    /// <param name="durabilityBatch">The active durability batch that owns publication.<br/></param>
    /// <param name="identity">The identity to move.<br/></param>
    /// <param name="oldKey">The current ordinary BigInteger key.<br/></param>
    /// <param name="newKey">The replacement ordinary BigInteger key.<br/></param>
    /// <returns>The rekey result and whether the current batch slice handled the operation.<br/></returns>
    internal (bool Handled, LibraDexGenericRekeyResult Result) RekeyForBatch(
        LibraDexFileSessionDurabilityBatch durabilityBatch,
        TIdentity identity,
        BigInteger oldKey,
        BigInteger newKey)
    {
        if (oldKey == newKey)
        {
            return (true, new LibraDexGenericRekeyResult(false, default, default));
        }

        (bool insertHandled, FixedNScalarInsertResult insertResult) = InsertFixedWidthForBatch(durabilityBatch, newKey, identity);
        if (!insertHandled)
        {
            return (false, default);
        }

        if (insertResult == FixedNScalarInsertResult.KeyConflict || insertResult == FixedNScalarInsertResult.Full)
        {
            throw new InvalidOperationException("Fixed-N BigInt batch rekey could not stage the replacement tuple; the old tuple was left unchanged.");
        }

        LibraDexGenericInsertResult replacement = new(insertResult == FixedNScalarInsertResult.Inserted, false, default, default);
        (bool deleteHandled, LibraDexGenericDeleteResult removal) = DeleteForBatch(durabilityBatch, oldKey, identity);
        if (!deleteHandled)
        {
            return (false, default);
        }

        return (true, new LibraDexGenericRekeyResult(removal.Deleted, replacement, removal));
    }

    /// <summary>
    /// Deletes one exact BigInteger key and identity tuple from this fixed-N index.<br/>
    /// Routed fixed-N indexes rewrite only the selected leaf shelf and do not merge or remove empty routes in this first mutation slice.<br/>
    /// </summary>
    /// <param name="key">The BigInteger key value to delete.<br/></param>
    /// <param name="identity">The identity associated with the key.<br/></param>
    /// <returns><see langword="true"/> when the tuple was present and removed.<br/></returns>
    public bool Delete(BigInteger key, TIdentity identity)
    {
        byte[] encodedKey = EncodeKey(key);
        if (storage != LibraDexBigIntKeyStorage.FixedWidth)
        {
            throw new NotSupportedException("BigInt ordinary tuple deletion is currently connected for fixed-width BigInt storage only.");
        }

        if (identityWidth == LibraDexScalarWidth.Bytes8)
        {
            FixedNScalar8Index inner = fixedInner8 ?? throw new ObjectDisposedException(nameof(LibraDexBigIntScalar8Index<TIdentity>));
            (bool deleted, _) = inner.Delete(encodedKey, LibraDexGenericScalarCodec<TIdentity>.Encode8(identity));
            return deleted;
        }

        Span<byte> encodedIdentity = stackalloc byte[16];
        EncodeIdentity16(identity, encodedIdentity);
        FixedNScalar16Index inner16 = fixedInner16 ?? throw new ObjectDisposedException(nameof(LibraDexBigIntScalar8Index<TIdentity>));
        (bool deleted16, _) = inner16.Delete(encodedKey, encodedIdentity);
        return deleted16;
    }

    /// <summary>
    /// Deletes one identity from this fixed-width BigInteger index's scalar null route.<br/>
    /// This is the typed exact-delete counterpart to <see cref="Insert(ScalarNull, TIdentity)"/> and avoids scanning ordinary fixed-key shelves.<br/>
    /// </summary>
    /// <param name="keyState">The scalar key state to delete; only <see cref="ScalarNull.Null"/> is accepted.<br/></param>
    /// <param name="identity">The identity to remove from the scalar null key state.<br/></param>
    /// <returns><see langword="true"/> when the identity was present and removed.<br/></returns>
    public bool Delete(ScalarNull keyState, TIdentity identity)
    {
        EnsureFixedScalarNullRouteSupported();
        if (keyState != ScalarNull.Null)
        {
            throw new ArgumentOutOfRangeException(nameof(keyState), keyState, "Only ScalarNull.Null is a concrete scalar key state for deletion.");
        }

        return DeleteScalarNullIdentity(identity);
    }

    /// <summary>
    /// Deletes one runtime key/identity tuple after validating both values against this BigInt facade's type contract.<br/>
    /// Fixed-N currently supports scalar null exact deletion through this facade; ordinary fixed-key tuple deletion waits for a routed fixed-N delete/compact path.<br/>
    /// </summary>
    /// <param name="key">The runtime key value or scalar null state to delete.<br/></param>
    /// <param name="identity">The runtime identity value to remove.<br/></param>
    /// <returns><see langword="true"/> when one tuple was removed.<br/></returns>
    public bool Delete(object? key, object identity)
    {
        if (key is null || key == DBNull.Value)
        {
            return Delete(ScalarNull.Null, RequireObjectIdentity(identity, nameof(identity)));
        }

        if (key is ScalarNull keyState)
        {
            return Delete(keyState, RequireObjectIdentity(identity, nameof(identity)));
        }

        if (key is not BigInteger typedKey)
        {
            throw new ArgumentException("BigInt indexes require BigInteger runtime keys.", nameof(key));
        }

        return Delete(typedKey, RequireObjectIdentity(identity, nameof(identity)));
    }

    /// <summary>
    /// Re-keys one fixed-N BigInteger identity when the old and new ordinary keys are known.<br/>
    /// The replacement tuple is inserted before the old exact tuple is removed, matching LibraDex identity-index mutation semantics without promising database rollback.<br/>
    /// </summary>
    /// <param name="identity">The identity to move.<br/></param>
    /// <param name="oldKey">The current BigInteger key.<br/></param>
    /// <param name="newKey">The replacement BigInteger key.<br/></param>
    /// <returns><see langword="true"/> when the old tuple existed and was removed after the replacement tuple was available.<br/></returns>
    public bool Rekey(TIdentity identity, BigInteger oldKey, BigInteger newKey)
    {
        if (oldKey == newKey)
        {
            return false;
        }

        if (!ContainsExactTuple(oldKey, identity))
        {
            return false;
        }

        if (!ContainsExactTuple(newKey, identity))
        {
            LibraDexGenericInsertResult insert = Insert(newKey, identity);
            if (!insert.Inserted && !ContainsExactTuple(newKey, identity))
            {
                throw new InvalidOperationException("Fixed-N BigInt rekey could not create the replacement tuple; the original tuple was left unchanged.");
            }
        }

        return Delete(oldKey, identity);
    }

    /// <summary>
    /// Re-keys one fixed-N BigInteger identity from the scalar null route to an ordinary key.<br/>
    /// The replacement ordinary tuple is inserted before the null-state identity is removed so a failed replacement leaves the null route unchanged.<br/>
    /// </summary>
    /// <param name="identity">The identity to move.<br/></param>
    /// <param name="oldKeyState">The old scalar key state; only <see cref="ScalarNull.Null"/> is accepted.<br/></param>
    /// <param name="newKey">The replacement BigInteger key.<br/></param>
    /// <returns><see langword="true"/> when the null-state identity existed and was removed after the replacement tuple was available.<br/></returns>
    public bool Rekey(TIdentity identity, ScalarNull oldKeyState, BigInteger newKey)
    {
        if (oldKeyState != ScalarNull.Null)
        {
            throw new ArgumentOutOfRangeException(nameof(oldKeyState), oldKeyState, "Only ScalarNull.Null is a concrete scalar key state for rekey.");
        }

        if (!ContainsScalarNullIdentity(identity))
        {
            return false;
        }

        if (!ContainsExactTuple(newKey, identity))
        {
            LibraDexGenericInsertResult insert = Insert(newKey, identity);
            if (!insert.Inserted && !ContainsExactTuple(newKey, identity))
            {
                throw new InvalidOperationException("Fixed-N BigInt rekey could not create the replacement tuple; the scalar-null route was left unchanged.");
            }
        }

        return Delete(ScalarNull.Null, identity);
    }

    /// <summary>
    /// Re-keys one fixed-N BigInteger identity from an ordinary key to the scalar null route.<br/>
    /// The null-state route insert is performed before the ordinary tuple is removed so a failed replacement leaves the old tuple unchanged.<br/>
    /// </summary>
    /// <param name="identity">The identity to move.<br/></param>
    /// <param name="oldKey">The current BigInteger key.<br/></param>
    /// <param name="newKeyState">The replacement scalar key state; only <see cref="ScalarNull.Null"/> is accepted.<br/></param>
    /// <returns><see langword="true"/> when the old tuple existed and was removed after the replacement route was available.<br/></returns>
    public bool Rekey(TIdentity identity, BigInteger oldKey, ScalarNull newKeyState)
    {
        if (newKeyState != ScalarNull.Null)
        {
            throw new ArgumentOutOfRangeException(nameof(newKeyState), newKeyState, "Only ScalarNull.Null is a concrete scalar key state for rekey.");
        }

        if (!ContainsExactTuple(oldKey, identity))
        {
            return false;
        }

        if (!ContainsScalarNullIdentity(identity))
        {
            LibraDexGenericInsertResult insert = Insert(ScalarNull.Null, identity);
            if (!insert.Inserted && !ContainsScalarNullIdentity(identity))
            {
                throw new InvalidOperationException("Fixed-N BigInt rekey could not create the scalar-null replacement route; the original tuple was left unchanged.");
            }
        }

        return Delete(oldKey, identity);
    }

    /// <summary>
    /// Re-keys one runtime identity after validating the supplied runtime keys against this BigInt facade's type contract.<br/>
    /// Fixed-N supports ordinary-to-ordinary, null-to-ordinary, and ordinary-to-null rekeys through this facade.<br/>
    /// </summary>
    /// <param name="identity">The runtime identity to move.<br/></param>
    /// <param name="oldKey">The current runtime key or scalar null state.<br/></param>
    /// <param name="newKey">The replacement runtime key or scalar null state.<br/></param>
    /// <returns><see langword="true"/> when the old tuple or route existed and was removed after replacement was available.<br/></returns>
    public bool Rekey(object identity, object? oldKey, object? newKey)
    {
        TIdentity typedIdentity = RequireObjectIdentity(identity, nameof(identity));
        bool oldIsNull = oldKey is null || oldKey == DBNull.Value || oldKey is ScalarNull.Null;
        bool newIsNull = newKey is null || newKey == DBNull.Value || newKey is ScalarNull.Null;
        if (oldIsNull && newIsNull)
        {
            return false;
        }

        if (oldIsNull)
        {
            return Rekey(typedIdentity, ScalarNull.Null, RequireObjectBigInteger(newKey, nameof(newKey)));
        }

        if (newIsNull)
        {
            return Rekey(typedIdentity, RequireObjectBigInteger(oldKey, nameof(oldKey)), ScalarNull.Null);
        }

        return Rekey(typedIdentity, RequireObjectBigInteger(oldKey, nameof(oldKey)), RequireObjectBigInteger(newKey, nameof(newKey)));
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
    /// Counts all identities visible through this BigInteger index facade.<br/>
    /// Fixed-width storage counts ordinary tuples from fixed-N shelf metadata and includes the scalar-null identity route when present; variable-width storage keeps its existing physical reader path.<br/>
    /// </summary>
    /// <returns>The physical identity tuple count for this index facade.<br/></returns>
    public long Count()
    {
        return CountIdentityPrimitive(new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.All, Array.Empty<object?>()));
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
        return BoxIdentityIterator(IterateIdentityPrimitiveTyped(request));
    }

    IEnumerable<TIdentity> IIdentityPrimitiveExecutor<TIdentity>.IterateIdentityPrimitiveTyped(LibraDexIdentityPrimitiveRequest request)
    {
        return IterateIdentityPrimitiveTyped(request);
    }

    long IIdentityPrimitiveExecutor.CountIdentityPrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        return CountIdentityPrimitive(request);
    }

    /// <summary>
    /// Executes count as a BigInteger scalar aggregate over the condition-materialized primitive.<br/>
    /// Fixed-width ordinary ranges use fixed-N shelf/range counts, scalar-null routes use route-root metadata, and variable-width ordinary ranges use routed `VS8` reader counts.<br/>
    /// </summary>
    /// <param name="request">The aggregate request to execute.<br/></param>
    /// <returns>The aggregate count result and physical plan classification.<br/></returns>
    LibraDexPrimitiveAggregateResult IIdentityPrimitiveAggregateExecutor.ExecuteIdentityPrimitiveAggregate(LibraDexPrimitiveAggregateRequest request)
    {
        if (request.Kind != LibraDexPrimitiveAggregateKind.Count)
        {
            throw new NotSupportedException($"{request.Kind} is not connected to BigInteger scalar aggregation yet.");
        }

        if (request.Scope != AggregateScope.Tuples)
        {
            throw new NotSupportedException($"{request.Scope} aggregate scope is not connected to BigInteger scalar aggregation yet.");
        }

        return LibraDexPrimitiveAggregateResult.ForCount(
            CountIdentityPrimitive(request.PrimitiveRequest),
            ClassifyBigIntegerAggregatePlan(request.PrimitiveRequest));
    }

    IReadOnlyList<object> IIdentityPrimitiveExecutor.ExecuteAllIdentities()
    {
        return ExecuteIdentityPrimitive(new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.All, Array.Empty<object?>()));
    }

    IEnumerable<object> IIdentityPrimitiveExecutor.IterateIdentityUniverse()
    {
        return BoxIdentityIterator(IterateIdentityPrimitiveTyped(new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.All, Array.Empty<object?>())));
    }

    IEnumerable<TIdentity> IIdentityPrimitiveExecutor<TIdentity>.IterateIdentityUniverseTyped()
    {
        return IterateIdentityPrimitiveTyped(new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.All, Array.Empty<object?>()));
    }

    IEnumerable<LibraDexObjectTuple> IIdentityPrimitiveTupleStreamer.IterateTuplePrimitive(
        LibraDexIdentityPrimitiveRequest request)
    {
        return IterateTuplePrimitive(request);
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

    /// <summary>
    /// Adapts typed scalar identities to the legacy runtime-object executor contract.<br/>
    /// Typed condition projections bypass this method and retain <typeparamref name="TIdentity"/> from physical scalar decoding through result composition.<br/>
    /// </summary>
    /// <param name="identities">The typed identities to expose to object-shaped callers.<br/></param>
    /// <returns>A lazy boxed identity sequence preserving source order.<br/></returns>
    private static IEnumerable<object> BoxIdentityIterator(IEnumerable<TIdentity> identities)
    {
        foreach (TIdentity identity in identities)
        {
            yield return identity!;
        }
    }

    private IReadOnlyList<object> ExecuteIdentityPrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        return BoxIdentityIterator(IterateIdentityPrimitiveTyped(request)).ToList();
    }

    /// <summary>
    /// Streams authoritative BigInteger key/identity tuples for full-index maintenance work.<br/>
    /// Fixed-width storage emits its slot-owned scalar-null route explicitly before ordinary fixed-N tuples, while variable-width storage decodes keys directly from the routed `VS8` cursor.<br/>
    /// </summary>
    /// <param name="request">The normalized primitive request; maintenance tuple streaming currently accepts only <see cref="LibraDexCriteriaKind.All"/>.<br/></param>
    /// <returns>Live logical tuples in stable physical order.<br/></returns>
    private IEnumerable<LibraDexObjectTuple> IterateTuplePrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        if (request.CriteriaKind != LibraDexCriteriaKind.All)
            throw new NotSupportedException($"{request.CriteriaKind} tuple streaming is not connected to the BigInteger scalar facade.");
        if (request.TakeLimit is < 0)
            throw new ArgumentOutOfRangeException(nameof(request.TakeLimit), request.TakeLimit, "Take cannot be negative.");
        if (request.TakeLimit == 0)
            yield break;

        int yielded = 0;
        if (storage == LibraDexBigIntKeyStorage.FixedWidth)
        {
            foreach (TIdentity identity in IterateScalarNullRouteIdentityObjects())
            {
                yield return new LibraDexObjectTuple(null, identity!);
                yielded++;
                if (request.TakeLimit is int limit && yielded >= limit)
                    yield break;
            }

            if (identityWidth == LibraDexScalarWidth.Bytes8)
            {
                FixedNScalar8Index inner = fixedInner8 ?? throw new ObjectDisposedException(nameof(LibraDexBigIntScalar8Index<TIdentity>));
                foreach (FixedNScalar8Tuple tuple in inner.IterateTuples())
                {
                    BigInteger key = LibraDexBigIntCodec.Decode(tuple.Key, MaxBytes, LibraDexBigIntKeyStorage.FixedWidth);
                    TIdentity identity = LibraDexGenericScalarCodec<TIdentity>.Decode8(tuple.EncodedIdentity);
                    yield return new LibraDexObjectTuple(key, identity!);
                    yielded++;
                    if (request.TakeLimit is int limit && yielded >= limit)
                        yield break;
                }

                yield break;
            }

            FixedNScalar16Index inner16 = fixedInner16 ?? throw new ObjectDisposedException(nameof(LibraDexBigIntScalar8Index<TIdentity>));
            foreach (FixedNScalar16Tuple tuple in inner16.IterateTuples())
            {
                BigInteger key = LibraDexBigIntCodec.Decode(tuple.Key, MaxBytes, LibraDexBigIntKeyStorage.FixedWidth);
                ulong high = BinaryPrimitives.ReadUInt64BigEndian(tuple.EncodedIdentity.AsSpan(0, 8));
                ulong low = BinaryPrimitives.ReadUInt64BigEndian(tuple.EncodedIdentity.AsSpan(8, 8));
                TIdentity identity = LibraDexGenericScalarCodec<TIdentity>.Decode16(high, low);
                yield return new LibraDexObjectTuple(key, identity!);
                yielded++;
                if (request.TakeLimit is int limit && yielded >= limit)
                    yield break;
            }

            yield break;
        }

        (BigInteger minimum, BigInteger maximum) = GetFullKeyBounds();
        byte[] lower = EncodeKey(minimum);
        byte[] upper = EncodeKey(maximum);
        VarKeyScalar8Index varIndex = varInner ?? throw new ObjectDisposedException(nameof(LibraDexBigIntScalar8Index<TIdentity>));
        using VarKeyScalar8RangeReader reader = varIndex.OpenRangeReader(lower, upper);
        while (reader.MoveNext())
        {
            BigInteger key = LibraDexBigIntCodec.Decode(reader.CurrentKey, MaxBytes, LibraDexBigIntKeyStorage.VariableWidth);
            TIdentity identity = LibraDexGenericScalarCodec<TIdentity>.Decode8(reader.CurrentEncodedIdentity);
            yield return new LibraDexObjectTuple(key, identity!);
            yielded++;
            if (request.TakeLimit is int limit && yielded >= limit)
                yield break;
        }
    }

    private IEnumerable<TIdentity> IterateIdentityPrimitiveTyped(LibraDexIdentityPrimitiveRequest request)
    {
        if (request.TakeLimit is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request.TakeLimit), request.TakeLimit, "Take cannot be negative.");
        }

        if (request.TakeLimit == 0)
        {
            yield break;
        }

        if (request.CriteriaKind == LibraDexCriteriaKind.ScalarNull)
        {
            foreach (TIdentity identity in IterateScalarNullIdentityObjects(request.Values, request.TakeLimit))
            {
                yield return identity;
            }

            yield break;
        }

        if (request.CriteriaKind == LibraDexCriteriaKind.All)
        {
            foreach (TIdentity identity in IterateAllIdentityObjects(request.TakeLimit))
            {
                yield return identity;
            }

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
        if (request.CriteriaKind == LibraDexCriteriaKind.ScalarNull)
        {
            return CountScalarNullIdentityObjects(request.Values);
        }

        if (request.CriteriaKind == LibraDexCriteriaKind.All)
        {
            return CountAllIdentityObjects();
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
    /// Expands a BigInteger primitive into count-only ordered key ranges.<br/>
    /// Membership operands are deduplicated as set membership, and multirange operands are sorted and merged so overlapping ranges do not double count the same physical key extent.<br/>
    /// Iterator expansion is intentionally separate so plan-natural streaming behavior remains unchanged.<br/>
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
    /// This avoids repeated metadata/range counts when a caller supplies duplicate membership values while preserving duplicate physical identities under the selected key.<br/>
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
                throw new ArgumentException("BigInt multi-range identity count requires each lower key to be less than or equal to its upper key.", nameof(values));
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

    /// <summary>
    /// Classifies the physical plan used by a BigInteger scalar aggregate primitive.<br/>
    /// Scalar-null criteria read key-state route metadata, while ordinary BigInteger criteria count routed fixed-N or variable-key slot ranges.<br/>
    /// </summary>
    /// <param name="request">The primitive request being aggregated.<br/></param>
    /// <returns>The conservative physical plan classification.</returns>
    private static LibraDexPrimitiveAggregatePlanKind ClassifyBigIntegerAggregatePlan(LibraDexIdentityPrimitiveRequest request)
    {
        return request.CriteriaKind == LibraDexCriteriaKind.ScalarNull
            ? LibraDexPrimitiveAggregatePlanKind.Metadata
            : LibraDexPrimitiveAggregatePlanKind.RangeSlots;
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

    private IEnumerable<TIdentity> IterateAllIdentityObjects(int? takeLimit = null)
    {
        int returned = 0;
        if (storage == LibraDexBigIntKeyStorage.FixedWidth)
        {
            foreach (TIdentity identity in IterateScalarNullRouteIdentityObjects(takeLimit))
            {
                yield return identity;
                returned++;
                if (takeLimit is not null && returned >= takeLimit.Value)
                {
                    yield break;
                }
            }
        }

        (BigInteger lower, BigInteger upper) = GetFullKeyBounds();
        foreach (TIdentity identity in GetIdentities(lower, upper))
        {
            yield return identity!;
            returned++;
            if (takeLimit is not null && returned >= takeLimit.Value)
            {
                yield break;
            }
        }
    }

    private long CountAllIdentityObjects()
    {
        long count = CountAllOrdinaryIdentityObjects();
        if (storage == LibraDexBigIntKeyStorage.FixedWidth)
        {
            count += CountScalarNullRouteIdentityObjects();
        }

        return count;
    }

    private IEnumerable<TIdentity> IterateScalarNullIdentityObjects(IReadOnlyList<object?> values, int? takeLimit = null)
    {
        ScalarNull state = RequireScalarNullState(values);
        if (state == ScalarNull.NonNull)
        {
            (BigInteger lower, BigInteger upper) = GetFullKeyBounds();
            int returned = 0;
            foreach (TIdentity identity in GetIdentities(lower, upper))
            {
                yield return identity!;
                returned++;
                if (takeLimit is not null && returned >= takeLimit.Value)
                {
                    yield break;
                }
            }

            yield break;
        }

        foreach (TIdentity identity in IterateScalarNullRouteIdentityObjects(takeLimit))
        {
            yield return identity;
        }
    }

    private long CountScalarNullIdentityObjects(IReadOnlyList<object?> values)
    {
        ScalarNull state = RequireScalarNullState(values);
        if (state == ScalarNull.NonNull)
        {
            return CountAllOrdinaryIdentityObjects();
        }

        return CountScalarNullRouteIdentityObjects();
    }

    private long CountAllOrdinaryIdentityObjects()
    {
        if (storage == LibraDexBigIntKeyStorage.FixedWidth)
        {
            if (identityWidth == LibraDexScalarWidth.Bytes8)
            {
                FixedNScalar8Index inner = fixedInner8 ?? throw new ObjectDisposedException(nameof(LibraDexBigIntScalar8Index<TIdentity>));
                return inner.CountOrdinaryIdentities();
            }

            FixedNScalar16Index inner16 = fixedInner16 ?? throw new ObjectDisposedException(nameof(LibraDexBigIntScalar8Index<TIdentity>));
            return inner16.CountOrdinaryIdentities();
        }

        (BigInteger lower, BigInteger upper) = GetFullKeyBounds();
        return CountVariableWidthIdentityRange(lower, upper);
    }

    private long CountOrdinaryIdentityRange(BigInteger lowerKey, BigInteger upperKey)
    {
        if (lowerKey > upperKey)
        {
            return 0;
        }

        if (storage == LibraDexBigIntKeyStorage.FixedWidth)
        {
            byte[] lower = EncodeKey(lowerKey);
            byte[] upper = EncodeKey(upperKey);
            if (identityWidth == LibraDexScalarWidth.Bytes8)
            {
                FixedNScalar8Index inner = fixedInner8 ?? throw new ObjectDisposedException(nameof(LibraDexBigIntScalar8Index<TIdentity>));
                return inner.CountIdentityRange(lower, upper);
            }

            FixedNScalar16Index inner16 = fixedInner16 ?? throw new ObjectDisposedException(nameof(LibraDexBigIntScalar8Index<TIdentity>));
            return inner16.CountIdentityRange(lower, upper);
        }

        return CountVariableWidthIdentityRange(lowerKey, upperKey);
    }

    /// <summary>
    /// Counts variable-width BigInteger scalar identities in the inclusive key range without materializing decoded identities.<br/>
    /// The underlying `VS8` reader derives its count from shelf-local slot ranges and terminal shelf item counts, so this facade keeps criteria counts on the physical count path.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower BigInteger key.<br/></param>
    /// <param name="upperKey">The inclusive upper BigInteger key.<br/></param>
    /// <returns>The number of physical identity tuples in the requested variable-width key range.<br/></returns>
    private long CountVariableWidthIdentityRange(BigInteger lowerKey, BigInteger upperKey)
    {
        byte[] lower = EncodeKey(lowerKey);
        byte[] upper = EncodeKey(upperKey);
        VarKeyScalar8Index inner = varInner ?? throw new ObjectDisposedException(nameof(LibraDexBigIntScalar8Index<TIdentity>));
        return inner.CountIdentityRange(lower, upper);
    }

    private bool InsertScalarNullIdentity(TIdentity identity)
    {
        if (identityWidth == LibraDexScalarWidth.Bytes8)
        {
            ulong encodedIdentity = LibraDexGenericScalarCodec<TIdentity>.Encode8(identity);
            FixedNScalar8Index inner = fixedInner8 ?? throw new ObjectDisposedException(nameof(LibraDexBigIntScalar8Index<TIdentity>));
            if (keyContract == IndexKeys.Unique)
            {
                ulong[] existing = inner.ReadScalarNullIdentities();
                if (existing.Length != 0 && !inner.ContainsScalarNullIdentity(encodedIdentity))
                {
                    return false;
                }
            }

            return inner.InsertScalarNullIdentity(encodedIdentity);
        }

        LibraDexGenericScalarCodec<TIdentity>.Encode16(identity, out ulong high, out ulong low);
        FixedNScalar16Index inner16 = fixedInner16 ?? throw new ObjectDisposedException(nameof(LibraDexBigIntScalar8Index<TIdentity>));
        if (keyContract == IndexKeys.Unique)
        {
            (ulong[] highs, _) = inner16.ReadScalarNullIdentities();
            if (highs.Length != 0 && !inner16.ContainsScalarNullIdentity(high, low))
            {
                return false;
            }
        }

        return inner16.InsertScalarNullIdentity(high, low);
    }

    private bool DeleteScalarNullIdentity(TIdentity identity)
    {
        if (identityWidth == LibraDexScalarWidth.Bytes8)
        {
            FixedNScalar8Index inner = fixedInner8 ?? throw new ObjectDisposedException(nameof(LibraDexBigIntScalar8Index<TIdentity>));
            return inner.DeleteScalarNullIdentity(LibraDexGenericScalarCodec<TIdentity>.Encode8(identity));
        }

        LibraDexGenericScalarCodec<TIdentity>.Encode16(identity, out ulong high, out ulong low);
        FixedNScalar16Index inner16 = fixedInner16 ?? throw new ObjectDisposedException(nameof(LibraDexBigIntScalar8Index<TIdentity>));
        return inner16.DeleteScalarNullIdentity(high, low);
    }

    private bool ContainsScalarNullIdentity(TIdentity identity)
    {
        EnsureFixedScalarNullRouteSupported();
        if (identityWidth == LibraDexScalarWidth.Bytes8)
        {
            FixedNScalar8Index inner = fixedInner8 ?? throw new ObjectDisposedException(nameof(LibraDexBigIntScalar8Index<TIdentity>));
            return inner.ContainsScalarNullIdentity(LibraDexGenericScalarCodec<TIdentity>.Encode8(identity));
        }

        LibraDexGenericScalarCodec<TIdentity>.Encode16(identity, out ulong high, out ulong low);
        FixedNScalar16Index inner16 = fixedInner16 ?? throw new ObjectDisposedException(nameof(LibraDexBigIntScalar8Index<TIdentity>));
        return inner16.ContainsScalarNullIdentity(high, low);
    }

    private bool ContainsExactTuple(BigInteger key, TIdentity identity)
    {
        IReadOnlyList<TIdentity> identities = GetIdentities(key);
        EqualityComparer<TIdentity> comparer = EqualityComparer<TIdentity>.Default;
        for (int i = 0; i < identities.Count; i++)
        {
            if (comparer.Equals(identities[i], identity))
            {
                return true;
            }
        }

        return false;
    }

    private IEnumerable<TIdentity> IterateScalarNullRouteIdentityObjects(int? takeLimit = null)
    {
        EnsureFixedScalarNullRouteSupported();
        if (takeLimit == 0)
        {
            yield break;
        }

        if (identityWidth == LibraDexScalarWidth.Bytes8)
        {
            FixedNScalar8Index inner = fixedInner8 ?? throw new ObjectDisposedException(nameof(LibraDexBigIntScalar8Index<TIdentity>));
            ulong[] encodedIdentities = inner.ReadScalarNullIdentities();
            for (int i = 0; i < encodedIdentities.Length; i++)
            {
                yield return LibraDexGenericScalarCodec<TIdentity>.Decode8(encodedIdentities[i])!;
                if (takeLimit is not null && i + 1 >= takeLimit.Value)
                {
                    yield break;
                }
            }

            yield break;
        }

        FixedNScalar16Index inner16 = fixedInner16 ?? throw new ObjectDisposedException(nameof(LibraDexBigIntScalar8Index<TIdentity>));
        (ulong[] highs, ulong[] lows) = inner16.ReadScalarNullIdentities();
        for (int i = 0; i < highs.Length; i++)
        {
            yield return LibraDexGenericScalarCodec<TIdentity>.Decode16(highs[i], lows[i])!;
            if (takeLimit is not null && i + 1 >= takeLimit.Value)
            {
                yield break;
            }
        }
    }

    private long CountScalarNullRouteIdentityObjects()
    {
        EnsureFixedScalarNullRouteSupported();
        if (identityWidth == LibraDexScalarWidth.Bytes8)
        {
            FixedNScalar8Index inner = fixedInner8 ?? throw new ObjectDisposedException(nameof(LibraDexBigIntScalar8Index<TIdentity>));
            return inner.CountScalarNullIdentities();
        }

        FixedNScalar16Index inner16 = fixedInner16 ?? throw new ObjectDisposedException(nameof(LibraDexBigIntScalar8Index<TIdentity>));
        return inner16.CountScalarNullIdentities();
    }

    private static ScalarNull RequireScalarNullState(IReadOnlyList<object?> values)
    {
        if (values.Count == 0 || values[0] is not ScalarNull state)
        {
            throw new InvalidOperationException("Scalar null criteria require a ScalarNull operand.");
        }

        return state;
    }

    private void EnsureFixedScalarNullRouteSupported()
    {
        if (storage != LibraDexBigIntKeyStorage.FixedWidth)
        {
            throw new NotSupportedException("BigInt ScalarNull routes are currently connected for fixed-width BigInt storage only.");
        }
    }

    /// <summary>
    /// Verifies that the first BigInteger batch slice can stage ordinary fixed-width keys for this facade.<br/>
    /// Variable-width BigInteger keys and scalar-null routes use different storage paths and remain outside this warmed-shelf coalescing branch.<br/>
    /// </summary>
    private void EnsureFixedWidthOrdinaryBatchSupported()
    {
        if (storage != LibraDexBigIntKeyStorage.FixedWidth)
        {
            throw new NotSupportedException("BigInt batching is currently connected for fixed-width BigInt storage only.");
        }
    }

    private static TIdentity RequireObjectIdentity(object identity, string parameterName)
    {
        if (identity is TIdentity typedIdentity)
        {
            return typedIdentity;
        }

        throw new ArgumentException($"BigInt indexes require identities assignable to {typeof(TIdentity).FullName}.", parameterName);
    }

    private static BigInteger RequireObjectBigInteger(object? key, string parameterName)
    {
        if (key is BigInteger typedKey)
        {
            return typedKey;
        }

        string actualType = key?.GetType().FullName ?? "<null>";
        throw new ArgumentException($"BigInt indexes require BigInteger runtime keys; received {actualType}.", parameterName);
    }

    private static void EncodeIdentity16(TIdentity identity, Span<byte> destination)
    {
        LibraDexGenericScalarCodec<TIdentity>.Encode16(identity, out ulong high, out ulong low);
        BinaryPrimitives.WriteUInt64BigEndian(destination[..8], high);
        BinaryPrimitives.WriteUInt64BigEndian(destination.Slice(8, 8), low);
    }
}

/// <summary>
/// Describes one ordinary fixed-width BigInteger rekey operation for <see cref="LibraDexBigIntScalar8Batch{TIdentity}.RekeyMany(IEnumerable{LibraDexBigIntScalar8Rekey{TIdentity}})"/>.<br/>
/// The identity is staged at <see cref="NewKey"/> before the old exact tuple at <see cref="OldKey"/> is removed, matching LibraDex identity-index mutation semantics.<br/>
/// </summary>
/// <typeparam name="TIdentity">The scalar identity type encoded into the fixed-N identity lane.<br/></typeparam>
/// <param name="Identity">The identity to move.<br/></param>
/// <param name="OldKey">The current ordinary BigInteger key.<br/></param>
/// <param name="NewKey">The replacement ordinary BigInteger key.<br/></param>
public readonly record struct LibraDexBigIntScalar8Rekey<TIdentity>(
    TIdentity Identity,
    BigInteger OldKey,
    BigInteger NewKey);

/// <summary>
/// Batches fixed-width BigInteger ordinary-key inserts behind one caller-owned publication boundary.<br/>
/// This first fixed-N batch slice is intentionally narrow: warmed shelf-local inserts are coalesced into batch-local shelf images, while cold-route and split/topology cases are reported as unsupported so callers can choose a smaller batch or fall back to one-shot writes.<br/>
/// </summary>
/// <typeparam name="TIdentity">The scalar identity type encoded into the fixed-N identity lane.<br/></typeparam>
public sealed class LibraDexBigIntScalar8Batch<TIdentity> : IDisposable
{
    private readonly LibraDexBigIntScalar8Index<TIdentity> index;
    private readonly LibraDexFileSessionDurabilityBatch durabilityBatch;
    private long attemptedInsertCount;
    private long insertedCount;
    private long attemptedDeleteCount;
    private long deletedCount;
    private long attemptedRekeyCount;
    private long changedRekeyCount;
    private bool completed;

    internal LibraDexBigIntScalar8Batch(
        LibraDexBigIntScalar8Index<TIdentity> index,
        LibraDexFileSessionDurabilityBatch durabilityBatch)
    {
        this.index = index;
        this.durabilityBatch = durabilityBatch;
    }

    /// <summary>
    /// Gets the number of delete calls attempted through this batch.<br/>
    /// The commit result remains insert-shaped for compatibility, so delete totals are exposed directly on the active batch object.<br/>
    /// </summary>
    public long AttemptedDeleteCount => attemptedDeleteCount;

    /// <summary>
    /// Gets the number of exact tuples deleted through this batch.<br/>
    /// The value is accumulated before publication and describes staged batch-local changes.<br/>
    /// </summary>
    public long DeletedCount => deletedCount;

    /// <summary>
    /// Gets the number of rekey calls attempted through this batch.<br/>
    /// Rekey is composed as replacement insert plus old tuple delete inside the same batch-local shelf images.<br/>
    /// </summary>
    public long AttemptedRekeyCount => attemptedRekeyCount;

    /// <summary>
    /// Gets the number of rekeys that removed the old tuple after the replacement was staged.<br/>
    /// A no-op same-key rekey or missing old tuple does not increment this value.<br/>
    /// </summary>
    public long ChangedRekeyCount => changedRekeyCount;

    /// <summary>
    /// Stages one ordinary BigInteger key and identity into the active batch.<br/>
    /// Multiple inserts that target the same warmed fixed-N shelf reuse one staged shelf image and publish once when the batch commits.<br/>
    /// </summary>
    /// <param name="key">The ordinary BigInteger key value to insert.<br/></param>
    /// <param name="identity">The identity associated with the key.<br/></param>
    /// <returns>The generic insert result with default commit diagnostics until publication.<br/></returns>
    public LibraDexGenericInsertResult Insert(BigInteger key, TIdentity identity)
    {
        ThrowIfCompleted();
        attemptedInsertCount++;
        (bool handled, LibraDexGenericInsertResult result) = index.InsertForBatch(durabilityBatch, key, identity);
        if (!handled)
        {
            throw new NotSupportedException(
                "The current fixed-N BigInt batch slice supports warmed shelf-local inserts only. " +
                "Cold-route creation, full-shelf split, and route topology mutation should use ordinary one-shot writes or a smaller pre-warmed batch.");
        }

        if (result.Inserted)
        {
            insertedCount++;
        }

        return result;
    }

    /// <summary>
    /// Stages one ordinary BigInteger key and identity into the active batch.<br/>
    /// This is an alias for <see cref="Insert(BigInteger, TIdentity)"/> for callers who prefer add terminology.<br/>
    /// </summary>
    /// <param name="key">The ordinary BigInteger key value to insert.<br/></param>
    /// <param name="identity">The identity associated with the key.<br/></param>
    /// <returns>The generic insert result with default commit diagnostics until publication.<br/></returns>
    public LibraDexGenericInsertResult Add(BigInteger key, TIdentity identity)
        => Insert(key, identity);

    /// <summary>
    /// Stages one ordinary BigInteger exact delete into the active batch.<br/>
    /// Multiple deletes and inserts that target the same warmed fixed-N shelf reuse the same staged shelf image and publish once when the batch commits.<br/>
    /// </summary>
    /// <param name="key">The ordinary BigInteger key value to delete.<br/></param>
    /// <param name="identity">The identity associated with the key.<br/></param>
    /// <returns>The generic delete result with default diagnostics until publication.<br/></returns>
    public LibraDexGenericDeleteResult Delete(BigInteger key, TIdentity identity)
    {
        ThrowIfCompleted();
        attemptedDeleteCount++;
        (bool handled, LibraDexGenericDeleteResult result) = index.DeleteForBatch(durabilityBatch, key, identity);
        if (!handled)
        {
            throw new NotSupportedException(
                "The current fixed-N BigInt batch slice supports warmed shelf-local deletes only. " +
                "Route topology mutation should use ordinary one-shot writes or a smaller pre-warmed batch.");
        }

        if (result.Deleted)
        {
            deletedCount++;
        }

        return result;
    }

    /// <summary>
    /// Stages one ordinary BigInteger exact delete into the active batch.<br/>
    /// This is an alias for <see cref="Delete(BigInteger, TIdentity)"/> for callers who prefer remove terminology.<br/>
    /// </summary>
    /// <param name="key">The ordinary BigInteger key value to delete.<br/></param>
    /// <param name="identity">The identity associated with the key.<br/></param>
    /// <returns>The generic delete result with default diagnostics until publication.<br/></returns>
    public LibraDexGenericDeleteResult Remove(BigInteger key, TIdentity identity)
        => Delete(key, identity);

    /// <summary>
    /// Stages many ordinary BigInteger exact deletes into the active batch.<br/>
    /// Operations are materialized and processed in descending key order so repeated same-shelf deletes reduce sorted-slot tail movement while preserving exact per-tuple delete results.<br/>
    /// </summary>
    /// <param name="entries">The key/identity tuples to delete.<br/></param>
    /// <returns>The number of tuples found and deleted.<br/></returns>
    public long DeleteMany(IEnumerable<(BigInteger Key, TIdentity Identity)> entries)
    {
        ThrowIfCompleted();
        ArgumentNullException.ThrowIfNull(entries);
        List<(BigInteger Key, TIdentity Identity)> orderedEntries = new();
        foreach ((BigInteger key, TIdentity identity) in entries)
        {
            orderedEntries.Add((key, identity));
        }

        return DeleteManyOrdered(orderedEntries);
    }

    /// <summary>
    /// Stages many ordinary BigInteger exact deletes into the active batch.<br/>
    /// This overload accepts <see cref="KeyValuePair{TKey,TValue}"/> sources for callers that already hold dictionary-style key/identity pairs.<br/>
    /// </summary>
    /// <param name="entries">The key/identity tuples to delete.<br/></param>
    /// <returns>The number of tuples found and deleted.<br/></returns>
    public long DeleteMany(IEnumerable<KeyValuePair<BigInteger, TIdentity>> entries)
    {
        ThrowIfCompleted();
        ArgumentNullException.ThrowIfNull(entries);
        List<(BigInteger Key, TIdentity Identity)> orderedEntries = new();
        foreach (KeyValuePair<BigInteger, TIdentity> entry in entries)
        {
            orderedEntries.Add((entry.Key, entry.Value));
        }

        return DeleteManyOrdered(orderedEntries);
    }

    /// <summary>
    /// Stages one ordinary BigInteger rekey into the active batch.<br/>
    /// The replacement tuple is staged before the old exact tuple is removed, so replacement-path failure leaves the old tuple untouched in the batch-local image.<br/>
    /// </summary>
    /// <param name="identity">The identity to move.<br/></param>
    /// <param name="oldKey">The current ordinary BigInteger key.<br/></param>
    /// <param name="newKey">The replacement ordinary BigInteger key.<br/></param>
    /// <returns>The generic rekey result with default diagnostics until publication.<br/></returns>
    public LibraDexGenericRekeyResult Rekey(TIdentity identity, BigInteger oldKey, BigInteger newKey)
    {
        ThrowIfCompleted();
        attemptedRekeyCount++;
        (bool handled, LibraDexGenericRekeyResult result) = index.RekeyForBatch(durabilityBatch, identity, oldKey, newKey);
        if (!handled)
        {
            throw new NotSupportedException(
                "The current fixed-N BigInt batch slice supports warmed shelf-local rekeys only. " +
                "Cold-route creation, full-shelf split, and route topology mutation should use ordinary one-shot writes or a smaller pre-warmed batch.");
        }

        if (result.Changed)
        {
            changedRekeyCount++;
        }

        return result;
    }

    /// <summary>
    /// Stages many ordinary BigInteger rekeys into the active batch.<br/>
    /// Operations are materialized and processed in descending old-key order so each replacement remains available before its old tuple is removed while repeated same-shelf deletes reduce sorted-slot tail movement.<br/>
    /// </summary>
    /// <param name="entries">The rekey operations to stage.<br/></param>
    /// <returns>The number of rekeys that removed the old tuple after staging the replacement tuple.<br/></returns>
    public long RekeyMany(IEnumerable<LibraDexBigIntScalar8Rekey<TIdentity>> entries)
    {
        ThrowIfCompleted();
        ArgumentNullException.ThrowIfNull(entries);
        List<LibraDexBigIntScalar8Rekey<TIdentity>> orderedEntries = new();
        foreach (LibraDexBigIntScalar8Rekey<TIdentity> entry in entries)
        {
            orderedEntries.Add(entry);
        }

        orderedEntries.Sort(static (left, right) => right.OldKey.CompareTo(left.OldKey));
        long changed = 0;
        for (int i = 0; i < orderedEntries.Count; i++)
        {
            LibraDexBigIntScalar8Rekey<TIdentity> entry = orderedEntries[i];
            LibraDexGenericRekeyResult result = Rekey(entry.Identity, entry.OldKey, entry.NewKey);
            if (result.Changed)
            {
                changed++;
            }
        }

        return changed;
    }

    private long DeleteManyOrdered(List<(BigInteger Key, TIdentity Identity)> orderedEntries)
    {
        orderedEntries.Sort(static (left, right) => right.Key.CompareTo(left.Key));
        long deleted = 0;
        for (int i = 0; i < orderedEntries.Count; i++)
        {
            (BigInteger key, TIdentity identity) = orderedEntries[i];
            LibraDexGenericDeleteResult result = Delete(key, identity);
            if (result.Deleted)
            {
                deleted++;
            }
        }

        return deleted;
    }

    /// <summary>
    /// Publishes all fixed-N shelf images staged by this batch.<br/>
    /// This controls durability cadence and reader visibility; it is not a SQL transaction boundary and does not roll back already published work.<br/>
    /// </summary>
    /// <returns>The aggregate batch outcome and DataKernel publication telemetry.<br/></returns>
    public LibraDexGenericBatchCommitResult Commit()
    {
        ThrowIfCompleted();
        (DataKernelCommitTelemetry commit, long deferredRequests, LibraDexBatchStorageDiagnostics storageDiagnostics) = durabilityBatch.Commit();
        completed = true;
        return new LibraDexGenericBatchCommitResult(
            attemptedInsertCount,
            insertedCount,
            InitialShelfRouteCreateCount: 0,
            deferredRequests,
            LibraDexOperationDiagnostics.FromDataKernel(commit),
            storageDiagnostics);
    }

    /// <summary>
    /// Publishes all fixed-N shelf images staged by this batch.<br/>
    /// This spelling matches LibraDex publication terminology while preserving the same behavior as <see cref="Commit"/>.<br/>
    /// </summary>
    /// <returns>The aggregate batch outcome and DataKernel publication telemetry.<br/></returns>
    public LibraDexGenericBatchCommitResult Publish()
        => Commit();

    /// <summary>
    /// Aborts unpublished fixed-N shelf images staged by this batch.<br/>
    /// Abort discards staged shelf bytes owned by this batch and does not affect already published catalog state.<br/>
    /// </summary>
    /// <returns>The aggregate abort result.<br/></returns>
    public LibraDexGenericBatchAbortResult Abort()
    {
        ThrowIfCompleted();
        long deferredRequests = durabilityBatch.Abort();
        completed = true;
        return new LibraDexGenericBatchAbortResult(attemptedInsertCount, deferredRequests);
    }

    /// <summary>
    /// Aborts the batch when disposed without explicit publication.<br/>
    /// This keeps `using` scopes safe for early exits while leaving successful publication explicit.<br/>
    /// </summary>
    public void Dispose()
    {
        if (!completed)
        {
            _ = Abort();
        }
    }

    private void ThrowIfCompleted()
    {
        if (completed)
        {
            throw new InvalidOperationException("The fixed-N BigInt batch has already completed.");
        }
    }
}
