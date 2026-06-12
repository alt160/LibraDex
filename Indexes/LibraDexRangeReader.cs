namespace LibraDex;

/// <summary>
/// Provides a forward-only generic cursor over fixed-shape LibraDex range results.<br/>
/// The cursor wraps the concrete fixed-shape reader selected by `LibraDexIndex&lt;TKey,TIdentity&gt;`, preserving datareader-style iteration while decoding CLR values only when the caller asks for the current key or identity.<br/>
/// For 32-byte fixed keys the generic key type is `byte[]`; <see cref="CurrentKey"/> materializes a new owned array, while shape-specific encoded readers remain available internally for allocation-sensitive lanes.<br/>
/// </summary>
/// <typeparam name="TKey">The public key type.</typeparam>
/// <typeparam name="TIdentity">The public identity type.</typeparam>
public sealed class LibraDexRangeReader<TKey, TIdentity> : IDisposable
{
    private readonly object innerReader;
    private readonly LibraDexGenericScalarShape shape;
    private readonly IReadOnlyList<LibraDexTuple<TKey, TIdentity>>? bufferedRows;
    private readonly Action? recordDelete;
    private readonly Func<TKey, TIdentity, TKey, LibraDexGenericInsertResult>? rekeyTuple;
    private readonly QueryDirection direction;
    private int bufferedOrdinal = -1;
    private int directedOrdinal = -1;
    private int? remainingLimit;
    private bool disposed;

    internal LibraDexRangeReader(
        object innerReader,
        LibraDexGenericScalarShape shape,
        int? takeLimit = null,
        QueryDirection direction = QueryDirection.Ascending,
        Action? recordDelete = null,
        Func<TKey, TIdentity, TKey, LibraDexGenericInsertResult>? rekeyTuple = null)
    {
        this.innerReader = innerReader;
        this.shape = shape;
        remainingLimit = takeLimit;
        this.direction = direction;
        this.recordDelete = recordDelete;
        this.rekeyTuple = rekeyTuple;
    }

    internal LibraDexRangeReader(
        IReadOnlyList<LibraDexTuple<TKey, TIdentity>> bufferedRows,
        int? takeLimit = null,
        QueryDirection direction = QueryDirection.Ascending)
    {
        this.bufferedRows = bufferedRows;
        innerReader = EmptyDisposable.Instance;
        shape = default;
        remainingLimit = takeLimit;
        this.direction = direction;
    }

    internal void ApplyTakeLimit(int? takeLimit)
    {
        if (takeLimit is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(takeLimit), takeLimit, "Take cannot be negative.");
        }

        remainingLimit = takeLimit;
    }

    /// <summary>
    /// Gets the number of matching rows available to this reader.<br/>
    /// The count is derived from the concrete reader's shelf-local slot ranges and does not materialize the result set.<br/>
    /// </summary>
    public int Count
    {
        get
        {
            ThrowIfDisposed();
            if (bufferedRows is not null)
            {
                return ApplyLimitToCount(bufferedRows.Count);
            }

            return shape switch
            {
                LibraDexGenericScalarShape.SS88 => ApplyLimitToCount(((Scalar8Scalar8RangeReader)innerReader).Count),
                LibraDexGenericScalarShape.SS168 => ApplyLimitToCount(((Scalar16Scalar8RangeReader)innerReader).Count),
                LibraDexGenericScalarShape.SS816 => ApplyLimitToCount(((Scalar8Scalar16RangeReader)innerReader).Count),
                LibraDexGenericScalarShape.SS1616 => ApplyLimitToCount(((Scalar16Scalar16RangeReader)innerReader).Count),
                LibraDexGenericScalarShape.FS328 => ApplyLimitToCount(((Fixed32Scalar8RangeReader)innerReader).Count),
                LibraDexGenericScalarShape.FS3216 => ApplyLimitToCount(((Fixed32Scalar16RangeReader)innerReader).Count),
                _ => throw new NotSupportedException($"Generic LibraDex range readers do not support resolved shape {shape}.")
            };
        }
    }

    /// <summary>
    /// Gets the zero-based row ordinal after a successful <see cref="MoveNext"/> call.<br/>
    /// The value is `-1` before the first row and equals <see cref="Count"/> after the reader passes the final row.<br/>
    /// </summary>
    public int Ordinal
    {
        get
        {
            ThrowIfDisposed();
            if (bufferedRows is not null)
            {
                return bufferedOrdinal;
            }

            if (direction == QueryDirection.Descending)
            {
                return directedOrdinal;
            }

            return shape switch
            {
                LibraDexGenericScalarShape.SS88 => ((Scalar8Scalar8RangeReader)innerReader).Ordinal,
                LibraDexGenericScalarShape.SS168 => ((Scalar16Scalar8RangeReader)innerReader).Ordinal,
                LibraDexGenericScalarShape.SS816 => ((Scalar8Scalar16RangeReader)innerReader).Ordinal,
                LibraDexGenericScalarShape.SS1616 => ((Scalar16Scalar16RangeReader)innerReader).Ordinal,
                LibraDexGenericScalarShape.FS328 => ((Fixed32Scalar8RangeReader)innerReader).Ordinal,
                LibraDexGenericScalarShape.FS3216 => ((Fixed32Scalar16RangeReader)innerReader).Ordinal,
                _ => throw new NotSupportedException($"Generic LibraDex range readers do not support resolved shape {shape}.")
            };
        }
    }

    /// <summary>
    /// Gets the current key decoded as the public CLR key type.<br/>
    /// Call this only after <see cref="MoveNext"/> returns <see langword="true"/>; byte-array keys are materialized as owned arrays.<br/>
    /// </summary>
    public TKey CurrentKey
    {
        get
        {
            ThrowIfDisposed();
            if (bufferedRows is not null)
            {
                return CurrentBufferedRow.Key;
            }

            return shape switch
            {
                LibraDexGenericScalarShape.SS88 => LibraDexGenericScalarCodec<TKey>.Decode8(((Scalar8Scalar8RangeReader)innerReader).CurrentEncodedKey),
                LibraDexGenericScalarShape.SS168 => LibraDexGenericScalarCodec<TKey>.Decode16(((Scalar16Scalar8RangeReader)innerReader).CurrentEncodedKeyHigh, ((Scalar16Scalar8RangeReader)innerReader).CurrentEncodedKeyLow),
                LibraDexGenericScalarShape.SS816 => LibraDexGenericScalarCodec<TKey>.Decode8(((Scalar8Scalar16RangeReader)innerReader).CurrentEncodedKey),
                LibraDexGenericScalarShape.SS1616 => LibraDexGenericScalarCodec<TKey>.Decode16(((Scalar16Scalar16RangeReader)innerReader).CurrentEncodedKeyHigh, ((Scalar16Scalar16RangeReader)innerReader).CurrentEncodedKeyLow),
                LibraDexGenericScalarShape.FS328 => DecodeFixed32Key((Fixed32Scalar8RangeReader)innerReader),
                LibraDexGenericScalarShape.FS3216 => DecodeFixed32Key((Fixed32Scalar16RangeReader)innerReader),
                _ => throw new NotSupportedException($"Generic LibraDex range readers do not support resolved shape {shape}.")
            };
        }
    }

    /// <summary>
    /// Gets the current identity decoded as the public CLR identity type.<br/>
    /// Call this only after <see cref="MoveNext"/> returns <see langword="true"/>; byte-array identities are materialized as owned arrays.<br/>
    /// </summary>
    public TIdentity CurrentIdentity
    {
        get
        {
            ThrowIfDisposed();
            if (bufferedRows is not null)
            {
                return CurrentBufferedRow.Identity;
            }

            return shape switch
            {
                LibraDexGenericScalarShape.SS88 => LibraDexGenericScalarCodec<TIdentity>.Decode8(((Scalar8Scalar8RangeReader)innerReader).CurrentEncodedIdentity),
                LibraDexGenericScalarShape.SS168 => LibraDexGenericScalarCodec<TIdentity>.Decode8(((Scalar16Scalar8RangeReader)innerReader).CurrentEncodedIdentity),
                LibraDexGenericScalarShape.SS816 => DecodeScalar8Scalar16Identity((Scalar8Scalar16RangeReader)innerReader),
                LibraDexGenericScalarShape.SS1616 => DecodeScalar16Scalar16Identity((Scalar16Scalar16RangeReader)innerReader),
                LibraDexGenericScalarShape.FS328 => LibraDexGenericScalarCodec<TIdentity>.Decode8(((Fixed32Scalar8RangeReader)innerReader).CurrentEncodedIdentity),
                LibraDexGenericScalarShape.FS3216 => DecodeFixed32Scalar16Identity((Fixed32Scalar16RangeReader)innerReader),
                _ => throw new NotSupportedException($"Generic LibraDex range readers do not support resolved shape {shape}.")
            };
        }
    }

    /// <summary>
    /// Advances the cursor and decodes only the next identity value.<br/>
    /// This is the lower-overhead identity streaming path for callers that do not need keys; it combines movement and identity projection so the generic wrapper does not perform separate shape dispatch for `MoveNext` and `CurrentIdentity`.<br/>
    /// When the method returns <see langword="false"/>, <paramref name="identity"/> is set to `default` and the reader is positioned after the final row.<br/>
    /// </summary>
    /// <param name="identity">Receives the decoded identity when a row is available.</param>
    /// <returns><see langword="true"/> when an identity was read; otherwise <see langword="false"/>.</returns>
    public bool TryReadNextIdentity(out TIdentity identity)
    {
        ThrowIfDisposed();
        if (bufferedRows is not null)
        {
            if (!MoveNextBuffered())
            {
                identity = default!;
                return false;
            }

            identity = CurrentBufferedRow.Identity;
            return true;
        }

        if (remainingLimit == 0)
        {
            identity = default!;
            return false;
        }

        if (direction == QueryDirection.Descending)
        {
            if (!MoveNext())
            {
                identity = default!;
                return false;
            }

            identity = CurrentIdentity;
            return true;
        }

        switch (shape)
        {
            case LibraDexGenericScalarShape.SS88:
            {
                Scalar8Scalar8RangeReader reader = (Scalar8Scalar8RangeReader)innerReader;
                if (!reader.TryReadNextEncodedIdentity(out ulong encodedIdentity))
                {
                    identity = default!;
                    return false;
                }

                identity = LibraDexGenericScalarCodec<TIdentity>.Decode8(encodedIdentity);
                ConsumeLimit();
                return true;
            }
            case LibraDexGenericScalarShape.SS168:
            {
                Scalar16Scalar8RangeReader reader = (Scalar16Scalar8RangeReader)innerReader;
                if (!reader.TryReadNextEncodedIdentity(out ulong encodedIdentity))
                {
                    identity = default!;
                    return false;
                }

                identity = LibraDexGenericScalarCodec<TIdentity>.Decode8(encodedIdentity);
                ConsumeLimit();
                return true;
            }
            case LibraDexGenericScalarShape.SS816:
            {
                Scalar8Scalar16RangeReader reader = (Scalar8Scalar16RangeReader)innerReader;
                if (!reader.TryReadNextEncodedIdentity(out ulong identityHigh, out ulong identityLow))
                {
                    identity = default!;
                    return false;
                }

                identity = LibraDexGenericScalarCodec<TIdentity>.Decode16(identityHigh, identityLow);
                ConsumeLimit();
                return true;
            }
            case LibraDexGenericScalarShape.SS1616:
            {
                Scalar16Scalar16RangeReader reader = (Scalar16Scalar16RangeReader)innerReader;
                if (!reader.TryReadNextEncodedIdentity(out ulong identityHigh, out ulong identityLow))
                {
                    identity = default!;
                    return false;
                }

                identity = LibraDexGenericScalarCodec<TIdentity>.Decode16(identityHigh, identityLow);
                ConsumeLimit();
                return true;
            }
            case LibraDexGenericScalarShape.FS328:
            {
                Fixed32Scalar8RangeReader reader = (Fixed32Scalar8RangeReader)innerReader;
                if (!reader.TryReadNextEncodedIdentity(out ulong encodedIdentity))
                {
                    identity = default!;
                    return false;
                }

                identity = LibraDexGenericScalarCodec<TIdentity>.Decode8(encodedIdentity);
                ConsumeLimit();
                return true;
            }
            case LibraDexGenericScalarShape.FS3216:
            {
                Fixed32Scalar16RangeReader reader = (Fixed32Scalar16RangeReader)innerReader;
                if (!reader.TryReadNextEncodedIdentity(out ulong identityHigh, out ulong identityLow))
                {
                    identity = default!;
                    return false;
                }

                identity = LibraDexGenericScalarCodec<TIdentity>.Decode16(identityHigh, identityLow);
                ConsumeLimit();
                return true;
            }
            default:
                throw new NotSupportedException($"Generic LibraDex range readers do not support resolved shape {shape}.");
        }
    }

    /// <summary>
    /// Advances the cursor and returns the next encoded 8-byte key with its decoded identity.<br/>
    /// This internal path is used by structured component predicates so date/time filters can inspect packed fields through shift-and-mask without reconstructing CLR key values.<br/>
    /// The method is intentionally limited to 8-byte scalar key shapes because Abraxas-compatible structured date/time keys are stored as one sortable 64-bit scalar.<br/>
    /// </summary>
    /// <param name="encodedKey">Receives the encoded scalar key when a row is available.</param>
    /// <param name="identity">Receives the decoded identity when a row is available.</param>
    /// <returns><see langword="true"/> when a row was read; otherwise <see langword="false"/>.</returns>
    internal bool TryReadNextEncodedScalar8KeyIdentity(out ulong encodedKey, out TIdentity identity)
    {
        ThrowIfDisposed();
        if (bufferedRows is not null)
        {
            if (!MoveNextBuffered())
            {
                encodedKey = default;
                identity = default!;
                return false;
            }

            encodedKey = LibraDexGenericScalarCodec<TKey>.Encode8(CurrentBufferedRow.Key);
            identity = CurrentBufferedRow.Identity;
            return true;
        }

        if (remainingLimit == 0)
        {
            encodedKey = default;
            identity = default!;
            return false;
        }

        if (direction == QueryDirection.Descending)
        {
            if (!MoveNext())
            {
                encodedKey = default;
                identity = default!;
                return false;
            }

            encodedKey = LibraDexGenericScalarCodec<TKey>.Encode8(CurrentKey);
            identity = CurrentIdentity;
            return true;
        }

        switch (shape)
        {
            case LibraDexGenericScalarShape.SS88:
            {
                Scalar8Scalar8RangeReader reader = (Scalar8Scalar8RangeReader)innerReader;
                if (!reader.MoveNext())
                {
                    encodedKey = default;
                    identity = default!;
                    return false;
                }

                encodedKey = reader.CurrentEncodedKey;
                identity = LibraDexGenericScalarCodec<TIdentity>.Decode8(reader.CurrentEncodedIdentity);
                ConsumeLimit();
                return true;
            }
            case LibraDexGenericScalarShape.SS816:
            {
                Scalar8Scalar16RangeReader reader = (Scalar8Scalar16RangeReader)innerReader;
                if (!reader.MoveNext())
                {
                    encodedKey = default;
                    identity = default!;
                    return false;
                }

                encodedKey = reader.CurrentEncodedKey;
                identity = DecodeScalar8Scalar16Identity(reader);
                ConsumeLimit();
                return true;
            }
            default:
                throw new NotSupportedException($"Encoded 8-byte key iteration does not support resolved shape {shape}.");
        }
    }

    /// <summary>
    /// Advances the cursor and returns the next encoded 16-byte key with its decoded identity.<br/>
    /// This internal path is used by GUID pattern predicates so LibraDex can inspect stored GUID bytes without reconstructing Guid or string values per candidate row.<br/>
    /// </summary>
    /// <param name="encodedHigh">Receives the first encoded 8-byte key lane when a row is available.</param>
    /// <param name="encodedLow">Receives the second encoded 8-byte key lane when a row is available.</param>
    /// <param name="identity">Receives the decoded identity when a row is available.</param>
    /// <returns><see langword="true"/> when a row was read; otherwise <see langword="false"/>.</returns>
    internal bool TryReadNextEncodedScalar16KeyIdentity(out ulong encodedHigh, out ulong encodedLow, out TIdentity identity)
    {
        ThrowIfDisposed();
        if (bufferedRows is not null)
        {
            if (!MoveNextBuffered())
            {
                encodedHigh = default;
                encodedLow = default;
                identity = default!;
                return false;
            }

            LibraDexGenericScalarCodec<TKey>.Encode16(CurrentBufferedRow.Key, out encodedHigh, out encodedLow);
            identity = CurrentBufferedRow.Identity;
            return true;
        }

        if (remainingLimit == 0)
        {
            encodedHigh = default;
            encodedLow = default;
            identity = default!;
            return false;
        }

        if (direction == QueryDirection.Descending)
        {
            if (!MoveNext())
            {
                encodedHigh = default;
                encodedLow = default;
                identity = default!;
                return false;
            }

            LibraDexGenericScalarCodec<TKey>.Encode16(CurrentKey, out encodedHigh, out encodedLow);
            identity = CurrentIdentity;
            return true;
        }

        switch (shape)
        {
            case LibraDexGenericScalarShape.SS168:
            {
                Scalar16Scalar8RangeReader reader = (Scalar16Scalar8RangeReader)innerReader;
                if (!reader.MoveNext())
                {
                    encodedHigh = default;
                    encodedLow = default;
                    identity = default!;
                    return false;
                }

                encodedHigh = reader.CurrentEncodedKeyHigh;
                encodedLow = reader.CurrentEncodedKeyLow;
                identity = LibraDexGenericScalarCodec<TIdentity>.Decode8(reader.CurrentEncodedIdentity);
                ConsumeLimit();
                return true;
            }
            case LibraDexGenericScalarShape.SS1616:
            {
                Scalar16Scalar16RangeReader reader = (Scalar16Scalar16RangeReader)innerReader;
                if (!reader.MoveNext())
                {
                    encodedHigh = default;
                    encodedLow = default;
                    identity = default!;
                    return false;
                }

                encodedHigh = reader.CurrentEncodedKeyHigh;
                encodedLow = reader.CurrentEncodedKeyLow;
                identity = DecodeScalar16Scalar16Identity(reader);
                ConsumeLimit();
                return true;
            }
            default:
                throw new NotSupportedException($"Encoded 16-byte key iteration does not support resolved shape {shape}.");
        }
    }

    /// <summary>
    /// Advances the cursor and copies the next encoded fixed-width key bytes with its decoded identity.<br/>
    /// This internal path is used by raw binary predicates so byte-array filters can inspect stored key bytes without allocating a decoded `byte[]` for each candidate row.<br/>
    /// Supported key widths are 8, 16, and 32 bytes, matching the fixed scalar byte-array shapes currently exposed by the generic scalar codec.<br/>
    /// </summary>
    /// <param name="keyBuffer">The destination buffer that receives the encoded key bytes in forward byte order.</param>
    /// <param name="keyByteCount">Receives the number of key bytes copied when a row is available.</param>
    /// <param name="identity">Receives the decoded identity when a row is available.</param>
    /// <returns><see langword="true"/> when a row was read; otherwise <see langword="false"/>.</returns>
    internal bool TryReadNextEncodedKeyBytes(Span<byte> keyBuffer, out int keyByteCount, out TIdentity identity)
    {
        ThrowIfDisposed();
        if (bufferedRows is not null)
        {
            if (!MoveNextBuffered())
            {
                keyByteCount = 0;
                identity = default!;
                return false;
            }

            if (CurrentBufferedRow.Key is not byte[] keyBytes)
            {
                throw new NotSupportedException($"Buffered encoded key-byte iteration requires byte-array keys, not {typeof(TKey).FullName}.");
            }

            if (keyBuffer.Length < keyBytes.Length)
            {
                throw new ArgumentException("The destination key buffer is too small for the encoded key.", nameof(keyBuffer));
            }

            keyBytes.CopyTo(keyBuffer);
            keyByteCount = keyBytes.Length;
            identity = CurrentBufferedRow.Identity;
            return true;
        }

        if (remainingLimit == 0)
        {
            keyByteCount = 0;
            identity = default!;
            return false;
        }

        if (direction == QueryDirection.Descending)
        {
            if (!MoveNext())
            {
                keyByteCount = 0;
                identity = default!;
                return false;
            }

            TKey key = CurrentKey;
            switch (shape)
            {
                case LibraDexGenericScalarShape.SS88 or LibraDexGenericScalarShape.SS816:
                    EnsureKeyBufferCapacity(keyBuffer, 8);
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(keyBuffer[..8], LibraDexGenericScalarCodec<TKey>.Encode8(key));
                    keyByteCount = 8;
                    break;
                case LibraDexGenericScalarShape.SS168 or LibraDexGenericScalarShape.SS1616:
                    EnsureKeyBufferCapacity(keyBuffer, 16);
                    LibraDexGenericScalarCodec<TKey>.Encode16(key, out ulong keyHigh, out ulong keyLow);
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(keyBuffer[..8], keyHigh);
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(keyBuffer.Slice(8, 8), keyLow);
                    keyByteCount = 16;
                    break;
                case LibraDexGenericScalarShape.FS328 or LibraDexGenericScalarShape.FS3216:
                    EnsureKeyBufferCapacity(keyBuffer, 32);
                    LibraDexGenericScalarCodec<TKey>.Encode32(key, out ulong key0, out ulong key1, out ulong key2, out ulong key3);
                    WriteFixed32Key(keyBuffer, key0, key1, key2, key3);
                    keyByteCount = 32;
                    break;
                default:
                    throw new NotSupportedException($"Encoded key-byte iteration does not support resolved shape {shape}.");
            }

            identity = CurrentIdentity;
            return true;
        }

        switch (shape)
        {
            case LibraDexGenericScalarShape.SS88:
            {
                EnsureKeyBufferCapacity(keyBuffer, 8);
                Scalar8Scalar8RangeReader reader = (Scalar8Scalar8RangeReader)innerReader;
                if (!reader.MoveNext())
                {
                    keyByteCount = 0;
                    identity = default!;
                    return false;
                }

                System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(keyBuffer[..8], reader.CurrentEncodedKey);
                keyByteCount = 8;
                identity = LibraDexGenericScalarCodec<TIdentity>.Decode8(reader.CurrentEncodedIdentity);
                ConsumeLimit();
                return true;
            }
            case LibraDexGenericScalarShape.SS816:
            {
                EnsureKeyBufferCapacity(keyBuffer, 8);
                Scalar8Scalar16RangeReader reader = (Scalar8Scalar16RangeReader)innerReader;
                if (!reader.MoveNext())
                {
                    keyByteCount = 0;
                    identity = default!;
                    return false;
                }

                System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(keyBuffer[..8], reader.CurrentEncodedKey);
                keyByteCount = 8;
                identity = DecodeScalar8Scalar16Identity(reader);
                ConsumeLimit();
                return true;
            }
            case LibraDexGenericScalarShape.SS168:
            {
                EnsureKeyBufferCapacity(keyBuffer, 16);
                Scalar16Scalar8RangeReader reader = (Scalar16Scalar8RangeReader)innerReader;
                if (!reader.MoveNext())
                {
                    keyByteCount = 0;
                    identity = default!;
                    return false;
                }

                System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(keyBuffer[..8], reader.CurrentEncodedKeyHigh);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(keyBuffer.Slice(8, 8), reader.CurrentEncodedKeyLow);
                keyByteCount = 16;
                identity = LibraDexGenericScalarCodec<TIdentity>.Decode8(reader.CurrentEncodedIdentity);
                ConsumeLimit();
                return true;
            }
            case LibraDexGenericScalarShape.SS1616:
            {
                EnsureKeyBufferCapacity(keyBuffer, 16);
                Scalar16Scalar16RangeReader reader = (Scalar16Scalar16RangeReader)innerReader;
                if (!reader.MoveNext())
                {
                    keyByteCount = 0;
                    identity = default!;
                    return false;
                }

                System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(keyBuffer[..8], reader.CurrentEncodedKeyHigh);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(keyBuffer.Slice(8, 8), reader.CurrentEncodedKeyLow);
                keyByteCount = 16;
                identity = DecodeScalar16Scalar16Identity(reader);
                ConsumeLimit();
                return true;
            }
            case LibraDexGenericScalarShape.FS328:
            {
                EnsureKeyBufferCapacity(keyBuffer, 32);
                Fixed32Scalar8RangeReader reader = (Fixed32Scalar8RangeReader)innerReader;
                if (!reader.MoveNext())
                {
                    keyByteCount = 0;
                    identity = default!;
                    return false;
                }

                reader.ReadCurrentKey(out ulong key0, out ulong key1, out ulong key2, out ulong key3);
                WriteFixed32Key(keyBuffer, key0, key1, key2, key3);
                keyByteCount = 32;
                identity = LibraDexGenericScalarCodec<TIdentity>.Decode8(reader.CurrentEncodedIdentity);
                ConsumeLimit();
                return true;
            }
            case LibraDexGenericScalarShape.FS3216:
            {
                EnsureKeyBufferCapacity(keyBuffer, 32);
                Fixed32Scalar16RangeReader reader = (Fixed32Scalar16RangeReader)innerReader;
                if (!reader.MoveNext())
                {
                    keyByteCount = 0;
                    identity = default!;
                    return false;
                }

                reader.ReadCurrentKey(out ulong key0, out ulong key1, out ulong key2, out ulong key3);
                WriteFixed32Key(keyBuffer, key0, key1, key2, key3);
                keyByteCount = 32;
                identity = DecodeFixed32Scalar16Identity(reader);
                ConsumeLimit();
                return true;
            }
            default:
                throw new NotSupportedException($"Encoded key-byte iteration does not support resolved shape {shape}.");
        }
    }

    /// <summary>
    /// Advances the cursor and decodes only the next key value.<br/>
    /// This is the lower-overhead key streaming path for callers that do not need identities; it combines movement and key projection so the generic wrapper does not perform separate shape dispatch for `MoveNext` and `CurrentKey`.<br/>
    /// Byte-array keys are still materialized as owned arrays because the generic API cannot expose shape-local encoded lanes through `TKey`.<br/>
    /// </summary>
    /// <param name="key">Receives the decoded key when a row is available.</param>
    /// <returns><see langword="true"/> when a key was read; otherwise <see langword="false"/>.</returns>
    public bool TryReadNextKey(out TKey key)
    {
        ThrowIfDisposed();
        if (bufferedRows is not null)
        {
            if (!MoveNextBuffered())
            {
                key = default!;
                return false;
            }

            key = CurrentBufferedRow.Key;
            return true;
        }

        if (remainingLimit == 0)
        {
            key = default!;
            return false;
        }

        if (direction == QueryDirection.Descending)
        {
            if (!MoveNext())
            {
                key = default!;
                return false;
            }

            key = CurrentKey;
            return true;
        }

        switch (shape)
        {
            case LibraDexGenericScalarShape.SS88:
            {
                Scalar8Scalar8RangeReader reader = (Scalar8Scalar8RangeReader)innerReader;
                if (!reader.MoveNext())
                {
                    key = default!;
                    return false;
                }

                key = LibraDexGenericScalarCodec<TKey>.Decode8(reader.CurrentEncodedKey);
                ConsumeLimit();
                return true;
            }
            case LibraDexGenericScalarShape.SS168:
            {
                Scalar16Scalar8RangeReader reader = (Scalar16Scalar8RangeReader)innerReader;
                if (!reader.MoveNext())
                {
                    key = default!;
                    return false;
                }

                key = LibraDexGenericScalarCodec<TKey>.Decode16(reader.CurrentEncodedKeyHigh, reader.CurrentEncodedKeyLow);
                ConsumeLimit();
                return true;
            }
            case LibraDexGenericScalarShape.SS816:
            {
                Scalar8Scalar16RangeReader reader = (Scalar8Scalar16RangeReader)innerReader;
                if (!reader.MoveNext())
                {
                    key = default!;
                    return false;
                }

                key = LibraDexGenericScalarCodec<TKey>.Decode8(reader.CurrentEncodedKey);
                ConsumeLimit();
                return true;
            }
            case LibraDexGenericScalarShape.SS1616:
            {
                Scalar16Scalar16RangeReader reader = (Scalar16Scalar16RangeReader)innerReader;
                if (!reader.MoveNext())
                {
                    key = default!;
                    return false;
                }

                key = LibraDexGenericScalarCodec<TKey>.Decode16(reader.CurrentEncodedKeyHigh, reader.CurrentEncodedKeyLow);
                ConsumeLimit();
                return true;
            }
            case LibraDexGenericScalarShape.FS328:
            {
                Fixed32Scalar8RangeReader reader = (Fixed32Scalar8RangeReader)innerReader;
                if (!reader.MoveNext())
                {
                    key = default!;
                    return false;
                }

                key = DecodeFixed32Key(reader);
                ConsumeLimit();
                return true;
            }
            case LibraDexGenericScalarShape.FS3216:
            {
                Fixed32Scalar16RangeReader reader = (Fixed32Scalar16RangeReader)innerReader;
                if (!reader.MoveNext())
                {
                    key = default!;
                    return false;
                }

                key = DecodeFixed32Key(reader);
                ConsumeLimit();
                return true;
            }
            default:
                throw new NotSupportedException($"Generic LibraDex range readers do not support resolved shape {shape}.");
        }
    }

    /// <summary>
    /// Advances the cursor and decodes the next key and identity as one tuple.<br/>
    /// This is the lower-overhead full-row streaming path for callers that need both sides of the tuple; it combines movement and projection so wrapper dispatch happens once per row instead of once per member access.<br/>
    /// Byte-array keys or identities are materialized as owned arrays because the generic API returns CLR values rather than shape-local spans or encoded lanes.<br/>
    /// </summary>
    /// <param name="key">Receives the decoded key when a row is available.</param>
    /// <param name="identity">Receives the decoded identity when a row is available.</param>
    /// <returns><see langword="true"/> when a tuple was read; otherwise <see langword="false"/>.</returns>
    public bool TryReadNext(out TKey key, out TIdentity identity)
    {
        ThrowIfDisposed();
        if (bufferedRows is not null)
        {
            if (!MoveNextBuffered())
            {
                key = default!;
                identity = default!;
                return false;
            }

            LibraDexTuple<TKey, TIdentity> tuple = CurrentBufferedRow;
            key = tuple.Key;
            identity = tuple.Identity;
            return true;
        }

        if (remainingLimit == 0)
        {
            key = default!;
            identity = default!;
            return false;
        }

        if (direction == QueryDirection.Descending)
        {
            return TryReadPrevious(out key, out identity);
        }

        switch (shape)
        {
            case LibraDexGenericScalarShape.SS88:
            {
                Scalar8Scalar8RangeReader reader = (Scalar8Scalar8RangeReader)innerReader;
                if (!reader.MoveNext())
                {
                    key = default!;
                    identity = default!;
                    return false;
                }

                key = LibraDexGenericScalarCodec<TKey>.Decode8(reader.CurrentEncodedKey);
                identity = LibraDexGenericScalarCodec<TIdentity>.Decode8(reader.CurrentEncodedIdentity);
                ConsumeLimit();
                return true;
            }
            case LibraDexGenericScalarShape.SS168:
            {
                Scalar16Scalar8RangeReader reader = (Scalar16Scalar8RangeReader)innerReader;
                if (!reader.MoveNext())
                {
                    key = default!;
                    identity = default!;
                    return false;
                }

                key = LibraDexGenericScalarCodec<TKey>.Decode16(reader.CurrentEncodedKeyHigh, reader.CurrentEncodedKeyLow);
                identity = LibraDexGenericScalarCodec<TIdentity>.Decode8(reader.CurrentEncodedIdentity);
                ConsumeLimit();
                return true;
            }
            case LibraDexGenericScalarShape.SS816:
            {
                Scalar8Scalar16RangeReader reader = (Scalar8Scalar16RangeReader)innerReader;
                if (!reader.MoveNext())
                {
                    key = default!;
                    identity = default!;
                    return false;
                }

                key = LibraDexGenericScalarCodec<TKey>.Decode8(reader.CurrentEncodedKey);
                identity = DecodeScalar8Scalar16Identity(reader);
                ConsumeLimit();
                return true;
            }
            case LibraDexGenericScalarShape.SS1616:
            {
                Scalar16Scalar16RangeReader reader = (Scalar16Scalar16RangeReader)innerReader;
                if (!reader.MoveNext())
                {
                    key = default!;
                    identity = default!;
                    return false;
                }

                key = LibraDexGenericScalarCodec<TKey>.Decode16(reader.CurrentEncodedKeyHigh, reader.CurrentEncodedKeyLow);
                identity = DecodeScalar16Scalar16Identity(reader);
                ConsumeLimit();
                return true;
            }
            case LibraDexGenericScalarShape.FS328:
            {
                Fixed32Scalar8RangeReader reader = (Fixed32Scalar8RangeReader)innerReader;
                if (!reader.MoveNext())
                {
                    key = default!;
                    identity = default!;
                    return false;
                }

                key = DecodeFixed32Key(reader);
                identity = LibraDexGenericScalarCodec<TIdentity>.Decode8(reader.CurrentEncodedIdentity);
                ConsumeLimit();
                return true;
            }
            case LibraDexGenericScalarShape.FS3216:
            {
                Fixed32Scalar16RangeReader reader = (Fixed32Scalar16RangeReader)innerReader;
                if (!reader.MoveNext())
                {
                    key = default!;
                    identity = default!;
                    return false;
                }

                key = DecodeFixed32Key(reader);
                identity = DecodeFixed32Scalar16Identity(reader);
                ConsumeLimit();
                return true;
            }
            default:
                throw new NotSupportedException($"Generic LibraDex range readers do not support resolved shape {shape}.");
        }
    }

    /// <summary>
    /// Moves the descending reader and decodes the previous physical key/identity row as the next public tuple.<br/>
    /// The method uses shape-specific fused previous-tuple readers, avoiding a separate `MovePrevious` plus current-key/current-identity projection.<br/>
    /// </summary>
    /// <param name="key">Receives the decoded key when a row is available.</param>
    /// <param name="identity">Receives the decoded identity when a row is available.</param>
    /// <returns><see langword="true"/> when a tuple was read; otherwise <see langword="false"/>.</returns>
    private bool TryReadPrevious(out TKey key, out TIdentity identity)
    {
        if (remainingLimit == 0)
        {
            key = default!;
            identity = default!;
            return false;
        }

        switch (shape)
        {
            case LibraDexGenericScalarShape.SS88:
            {
                Scalar8Scalar8RangeReader reader = (Scalar8Scalar8RangeReader)innerReader;
                if (!reader.TryReadPreviousEncodedTuple(out ulong encodedKey, out ulong encodedIdentity))
                {
                    key = default!;
                    identity = default!;
                    return false;
                }

                key = LibraDexGenericScalarCodec<TKey>.Decode8(encodedKey);
                identity = LibraDexGenericScalarCodec<TIdentity>.Decode8(encodedIdentity);
                directedOrdinal++;
                ConsumeLimit();
                return true;
            }
            case LibraDexGenericScalarShape.SS168:
            {
                Scalar16Scalar8RangeReader reader = (Scalar16Scalar8RangeReader)innerReader;
                if (!reader.TryReadPreviousEncodedTuple(out ulong keyHigh, out ulong keyLow, out ulong encodedIdentity))
                {
                    key = default!;
                    identity = default!;
                    return false;
                }

                key = LibraDexGenericScalarCodec<TKey>.Decode16(keyHigh, keyLow);
                identity = LibraDexGenericScalarCodec<TIdentity>.Decode8(encodedIdentity);
                directedOrdinal++;
                ConsumeLimit();
                return true;
            }
            case LibraDexGenericScalarShape.SS816:
            {
                Scalar8Scalar16RangeReader reader = (Scalar8Scalar16RangeReader)innerReader;
                if (!reader.TryReadPreviousEncodedTuple(out ulong encodedKey, out ulong identityHigh, out ulong identityLow))
                {
                    key = default!;
                    identity = default!;
                    return false;
                }

                key = LibraDexGenericScalarCodec<TKey>.Decode8(encodedKey);
                identity = LibraDexGenericScalarCodec<TIdentity>.Decode16(identityHigh, identityLow);
                directedOrdinal++;
                ConsumeLimit();
                return true;
            }
            case LibraDexGenericScalarShape.SS1616:
            {
                Scalar16Scalar16RangeReader reader = (Scalar16Scalar16RangeReader)innerReader;
                if (!reader.TryReadPreviousEncodedTuple(out ulong keyHigh, out ulong keyLow, out ulong identityHigh, out ulong identityLow))
                {
                    key = default!;
                    identity = default!;
                    return false;
                }

                key = LibraDexGenericScalarCodec<TKey>.Decode16(keyHigh, keyLow);
                identity = LibraDexGenericScalarCodec<TIdentity>.Decode16(identityHigh, identityLow);
                directedOrdinal++;
                ConsumeLimit();
                return true;
            }
            case LibraDexGenericScalarShape.FS328:
            {
                Fixed32Scalar8RangeReader reader = (Fixed32Scalar8RangeReader)innerReader;
                if (!reader.TryReadPreviousEncodedTuple(out ulong key0, out ulong key1, out ulong key2, out ulong key3, out ulong encodedIdentity))
                {
                    key = default!;
                    identity = default!;
                    return false;
                }

                key = LibraDexGenericScalarCodec<TKey>.Decode32(key0, key1, key2, key3);
                identity = LibraDexGenericScalarCodec<TIdentity>.Decode8(encodedIdentity);
                directedOrdinal++;
                ConsumeLimit();
                return true;
            }
            case LibraDexGenericScalarShape.FS3216:
            {
                Fixed32Scalar16RangeReader reader = (Fixed32Scalar16RangeReader)innerReader;
                if (!reader.TryReadPreviousEncodedTuple(out ulong key0, out ulong key1, out ulong key2, out ulong key3, out ulong identityHigh, out ulong identityLow))
                {
                    key = default!;
                    identity = default!;
                    return false;
                }

                key = LibraDexGenericScalarCodec<TKey>.Decode32(key0, key1, key2, key3);
                identity = LibraDexGenericScalarCodec<TIdentity>.Decode16(identityHigh, identityLow);
                directedOrdinal++;
                ConsumeLimit();
                return true;
            }
            default:
                throw new NotSupportedException($"Generic LibraDex range readers do not support resolved shape {shape}.");
        }
    }

    /// <summary>
    /// Advances the cursor to the next matching key/identity row in the reader's configured direction.<br/>
    /// The method delegates to the selected fixed-shape reader and performs no CLR value decoding by itself.<br/>
    /// Descending movement walks retained shelf ranges backward without materializing decoded tuples.<br/>
    /// </summary>
    /// <returns><see langword="true"/> when the reader is positioned on a valid row.</returns>
    public bool MoveNext()
    {
        ThrowIfDisposed();
        if (bufferedRows is not null)
        {
            return MoveNextBuffered();
        }

        if (remainingLimit == 0)
        {
            return false;
        }

        if (direction == QueryDirection.Descending)
        {
            bool movedPrevious = shape switch
            {
                LibraDexGenericScalarShape.SS88 => ((Scalar8Scalar8RangeReader)innerReader).MovePrevious(),
                LibraDexGenericScalarShape.SS168 => ((Scalar16Scalar8RangeReader)innerReader).MovePrevious(),
                LibraDexGenericScalarShape.SS816 => ((Scalar8Scalar16RangeReader)innerReader).MovePrevious(),
                LibraDexGenericScalarShape.SS1616 => ((Scalar16Scalar16RangeReader)innerReader).MovePrevious(),
                LibraDexGenericScalarShape.FS328 => ((Fixed32Scalar8RangeReader)innerReader).MovePrevious(),
                LibraDexGenericScalarShape.FS3216 => ((Fixed32Scalar16RangeReader)innerReader).MovePrevious(),
                _ => throw new NotSupportedException($"Generic LibraDex range readers do not support resolved shape {shape}.")
            };
            if (movedPrevious)
            {
                directedOrdinal++;
                ConsumeLimit();
            }
            else
            {
                directedOrdinal = Count;
            }

            return movedPrevious;
        }

        bool moved = shape switch
        {
            LibraDexGenericScalarShape.SS88 => ((Scalar8Scalar8RangeReader)innerReader).MoveNext(),
            LibraDexGenericScalarShape.SS168 => ((Scalar16Scalar8RangeReader)innerReader).MoveNext(),
            LibraDexGenericScalarShape.SS816 => ((Scalar8Scalar16RangeReader)innerReader).MoveNext(),
            LibraDexGenericScalarShape.SS1616 => ((Scalar16Scalar16RangeReader)innerReader).MoveNext(),
            LibraDexGenericScalarShape.FS328 => ((Fixed32Scalar8RangeReader)innerReader).MoveNext(),
            LibraDexGenericScalarShape.FS3216 => ((Fixed32Scalar16RangeReader)innerReader).MoveNext(),
            _ => throw new NotSupportedException($"Generic LibraDex range readers do not support resolved shape {shape}.")
        };
        if (moved)
        {
            ConsumeLimit();
        }

        return moved;
    }

    /// <summary>
    /// Deletes the current row from the backing fixed-scalar index and positions this cursor before the next surviving row.<br/>
    /// Call this only after a successful move/read operation has positioned the cursor on a row; current key and identity properties become invalid until the next successful move.<br/>
    /// The next <see cref="MoveNext"/> or `TryReadNext*` call continues with the tuple that shifted into the deleted slot, so deletion does not skip a surviving row in the same shelf.<br/>
    /// Buffered readers are immutable snapshots and do not support cursor-local mutation.<br/>
    /// </summary>
    /// <returns><see langword="true"/> when the positioned row was deleted.</returns>
    public bool DeleteCurrent()
    {
        ThrowIfDisposed();
        if (bufferedRows is not null)
        {
            throw new NotSupportedException("Buffered LibraDex range readers do not support cursor-local deletion.");
        }

        if (direction == QueryDirection.Descending)
        {
            throw new NotSupportedException("Descending LibraDex range readers do not support cursor-local deletion yet.");
        }

        bool deleted = shape switch
        {
            LibraDexGenericScalarShape.SS88 => ((Scalar8Scalar8RangeReader)innerReader).DeleteCurrent(),
            LibraDexGenericScalarShape.SS168 => ((Scalar16Scalar8RangeReader)innerReader).DeleteCurrent(),
            LibraDexGenericScalarShape.SS816 => ((Scalar8Scalar16RangeReader)innerReader).DeleteCurrent(),
            LibraDexGenericScalarShape.SS1616 => ((Scalar16Scalar16RangeReader)innerReader).DeleteCurrent(),
            LibraDexGenericScalarShape.FS328 => ((Fixed32Scalar8RangeReader)innerReader).DeleteCurrent(),
            LibraDexGenericScalarShape.FS3216 => ((Fixed32Scalar16RangeReader)innerReader).DeleteCurrent(),
            _ => throw new NotSupportedException($"Generic LibraDex range readers do not support resolved shape {shape}.")
        };
        if (deleted)
        {
            recordDelete?.Invoke();
        }

        return deleted;
    }

    /// <summary>
    /// Skips up to <paramref name="count"/> rows without decoding keys or identities.<br/>
    /// The concrete reader advances across shelf-local slot ranges instead of stepping one row at a time where possible.<br/>
    /// Descending readers reposition backward through retained shelf ranges without materializing decoded tuples.<br/>
    /// </summary>
    /// <param name="count">The maximum number of rows to skip.</param>
    /// <returns>The number of rows actually skipped.</returns>
    public int Skip(int count)
    {
        ThrowIfDisposed();
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "Skip count cannot be negative.");
        }

        if (bufferedRows is not null)
        {
            int bufferedRequestedCount = remainingLimit.HasValue
                ? Math.Min(count, remainingLimit.Value)
                : count;
            int available = Math.Max(0, bufferedRows.Count - (bufferedOrdinal + 1));
            int bufferedSkipped = Math.Min(bufferedRequestedCount, available);
            bufferedOrdinal += bufferedSkipped;
            if (remainingLimit.HasValue)
            {
                remainingLimit = Math.Max(0, remainingLimit.Value - bufferedSkipped);
            }

            return bufferedSkipped;
        }

        int requestedCount = remainingLimit.HasValue
            ? Math.Min(count, remainingLimit.Value)
            : count;
        if (direction == QueryDirection.Descending)
        {
            int skippedPrevious = shape switch
            {
                LibraDexGenericScalarShape.SS88 => ((Scalar8Scalar8RangeReader)innerReader).SkipPrevious(requestedCount),
                LibraDexGenericScalarShape.SS168 => ((Scalar16Scalar8RangeReader)innerReader).SkipPrevious(requestedCount),
                LibraDexGenericScalarShape.SS816 => ((Scalar8Scalar16RangeReader)innerReader).SkipPrevious(requestedCount),
                LibraDexGenericScalarShape.SS1616 => ((Scalar16Scalar16RangeReader)innerReader).SkipPrevious(requestedCount),
                LibraDexGenericScalarShape.FS328 => ((Fixed32Scalar8RangeReader)innerReader).SkipPrevious(requestedCount),
                LibraDexGenericScalarShape.FS3216 => ((Fixed32Scalar16RangeReader)innerReader).SkipPrevious(requestedCount),
                _ => throw new NotSupportedException($"Generic LibraDex range readers do not support resolved shape {shape}.")
            };
            directedOrdinal += skippedPrevious;
            if (remainingLimit.HasValue)
            {
                remainingLimit = Math.Max(0, remainingLimit.Value - skippedPrevious);
            }

            return skippedPrevious;
        }

        int skipped = shape switch
        {
            LibraDexGenericScalarShape.SS88 => ((Scalar8Scalar8RangeReader)innerReader).Skip(requestedCount),
            LibraDexGenericScalarShape.SS168 => ((Scalar16Scalar8RangeReader)innerReader).Skip(requestedCount),
            LibraDexGenericScalarShape.SS816 => ((Scalar8Scalar16RangeReader)innerReader).Skip(requestedCount),
            LibraDexGenericScalarShape.SS1616 => ((Scalar16Scalar16RangeReader)innerReader).Skip(requestedCount),
            LibraDexGenericScalarShape.FS328 => ((Fixed32Scalar8RangeReader)innerReader).Skip(requestedCount),
            LibraDexGenericScalarShape.FS3216 => ((Fixed32Scalar16RangeReader)innerReader).Skip(requestedCount),
            _ => throw new NotSupportedException($"Generic LibraDex range readers do not support resolved shape {shape}.")
        };
        if (remainingLimit.HasValue)
        {
            remainingLimit = Math.Max(0, remainingLimit.Value - skipped);
        }

        return skipped;
    }

    /// <summary>
    /// Re-keys the current tuple while preserving cursor traversal stability.<br/>
    /// A connected implementation must capture or preserve the next original candidate before applying the mutation so the updated tuple is not revisited and the next original tuple is not skipped.<br/>
    /// Batch mode controls durability cadence for this mutation; it does not make cursor mutation a rollback-capable transaction.<br/>
    /// </summary>
    /// <param name="newKey">The replacement key for the current identity.</param>
    /// <returns>The insert/delete result for the physical re-key operation once connected.</returns>
    public LibraDexGenericInsertResult SetKey(TKey newKey)
    {
        ThrowIfDisposed();
        if (bufferedRows is not null)
        {
            throw new NotSupportedException("Buffered LibraDex range readers do not support cursor-local re-key.");
        }

        if (direction == QueryDirection.Descending)
        {
            throw new NotSupportedException("Descending LibraDex range readers do not support cursor-local re-key yet.");
        }

        Func<TKey, TIdentity, TKey, LibraDexGenericInsertResult> localRekey = rekeyTuple ?? throw new NotSupportedException("This LibraDex range reader was not opened with cursor-local re-key support.");
        TKey oldKey = CurrentKey;
        TIdentity identity = CurrentIdentity;
        if (TupleComponentEquals(oldKey, newKey))
        {
            return new LibraDexGenericInsertResult(false, false, default, default);
        }

        LibraDexGenericInsertResult insert = localRekey(oldKey, identity, newKey);
        InvalidateCurrentAfterExternalMutation();
        return insert;
    }

    /// <summary>
    /// Deletes the current key/identity tuple while preserving cursor traversal stability.<br/>
    /// A connected implementation must capture or preserve the next original candidate before applying the delete so traversal neither revisits nor skips entries because of the mutation.<br/>
    /// Batch mode controls durability cadence for this mutation; it does not make cursor deletion a rollback-capable transaction.<br/>
    /// </summary>
    public void Delete()
    {
        ThrowIfDisposed();
        _ = DeleteCurrent();
    }

    private static bool TupleComponentEquals<TValue>(TValue left, TValue right)
    {
        if (left is byte[] leftBytes && right is byte[] rightBytes)
        {
            return leftBytes.AsSpan().SequenceEqual(rightBytes);
        }

        return EqualityComparer<TValue>.Default.Equals(left, right);
    }

    private void InvalidateCurrentAfterExternalMutation()
    {
        switch (shape)
        {
            case LibraDexGenericScalarShape.SS88:
                ((Scalar8Scalar8RangeReader)innerReader).InvalidateCurrentAfterExternalMutation();
                return;
            case LibraDexGenericScalarShape.SS168:
                ((Scalar16Scalar8RangeReader)innerReader).InvalidateCurrentAfterExternalMutation();
                return;
            case LibraDexGenericScalarShape.SS816:
                ((Scalar8Scalar16RangeReader)innerReader).InvalidateCurrentAfterExternalMutation();
                return;
            case LibraDexGenericScalarShape.SS1616:
                ((Scalar16Scalar16RangeReader)innerReader).InvalidateCurrentAfterExternalMutation();
                return;
            case LibraDexGenericScalarShape.FS328:
                ((Fixed32Scalar8RangeReader)innerReader).InvalidateCurrentAfterExternalMutation();
                return;
            case LibraDexGenericScalarShape.FS3216:
                ((Fixed32Scalar16RangeReader)innerReader).InvalidateCurrentAfterExternalMutation();
                return;
            default:
                throw new NotSupportedException($"Generic LibraDex range readers do not support resolved shape {shape}.");
        }
    }

    /// <summary>
    /// Releases the wrapped concrete range reader.<br/>
    /// The cursor must not be used after disposal.<br/>
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (bufferedRows is null)
        {
            ((IDisposable)innerReader).Dispose();
        }
    }

    private LibraDexTuple<TKey, TIdentity> CurrentBufferedRow
    {
        get
        {
            if (bufferedRows is null)
            {
                throw new InvalidOperationException("The reader is not buffered.");
            }

            if (bufferedOrdinal < 0 || bufferedOrdinal >= bufferedRows.Count)
            {
                throw new InvalidOperationException("The buffered reader is not positioned on a row.");
            }

            return bufferedRows[bufferedOrdinal];
        }
    }

    private bool MoveNextBuffered()
    {
        if (bufferedRows is null)
        {
            return false;
        }

        if (remainingLimit == 0)
        {
            return false;
        }

        int next = bufferedOrdinal + 1;
        if (next >= bufferedRows.Count)
        {
            bufferedOrdinal = bufferedRows.Count;
            return false;
        }

        bufferedOrdinal = next;
        ConsumeLimit();
        return true;
    }

    private sealed class EmptyDisposable : IDisposable
    {
        internal static readonly EmptyDisposable Instance = new();

        private EmptyDisposable()
        {
        }

        public void Dispose()
        {
        }
    }

    private static TKey DecodeFixed32Key(Fixed32Scalar8RangeReader reader)
    {
        reader.ReadCurrentKey(out ulong key0, out ulong key1, out ulong key2, out ulong key3);
        return LibraDexGenericScalarCodec<TKey>.Decode32(key0, key1, key2, key3);
    }

    private static TKey DecodeFixed32Key(Fixed32Scalar16RangeReader reader)
    {
        reader.ReadCurrentKey(out ulong key0, out ulong key1, out ulong key2, out ulong key3);
        return LibraDexGenericScalarCodec<TKey>.Decode32(key0, key1, key2, key3);
    }

    private static TIdentity DecodeScalar8Scalar16Identity(Scalar8Scalar16RangeReader reader)
    {
        reader.ReadCurrentIdentity(out ulong identityHigh, out ulong identityLow);
        return LibraDexGenericScalarCodec<TIdentity>.Decode16(identityHigh, identityLow);
    }

    private static TIdentity DecodeScalar16Scalar16Identity(Scalar16Scalar16RangeReader reader)
    {
        reader.ReadCurrentIdentity(out ulong identityHigh, out ulong identityLow);
        return LibraDexGenericScalarCodec<TIdentity>.Decode16(identityHigh, identityLow);
    }

    private static TIdentity DecodeFixed32Scalar16Identity(Fixed32Scalar16RangeReader reader)
    {
        reader.ReadCurrentIdentity(out ulong identityHigh, out ulong identityLow);
        return LibraDexGenericScalarCodec<TIdentity>.Decode16(identityHigh, identityLow);
    }

    private static void EnsureKeyBufferCapacity(Span<byte> keyBuffer, int requiredLength)
    {
        if (keyBuffer.Length < requiredLength)
        {
            throw new ArgumentException("The destination key buffer is too small for the encoded key.", nameof(keyBuffer));
        }
    }

    private static void WriteFixed32Key(Span<byte> keyBuffer, ulong key0, ulong key1, ulong key2, ulong key3)
    {
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(keyBuffer[..8], key0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(keyBuffer.Slice(8, 8), key1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(keyBuffer.Slice(16, 8), key2);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(keyBuffer.Slice(24, 8), key3);
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(LibraDexRangeReader<TKey, TIdentity>));
        }
    }

    private int ApplyLimitToCount(int count)
    {
        return remainingLimit.HasValue
            ? Math.Min(count, remainingLimit.Value)
            : count;
    }

    private void ConsumeLimit()
    {
        if (remainingLimit.HasValue)
        {
            remainingLimit--;
        }
    }
}
