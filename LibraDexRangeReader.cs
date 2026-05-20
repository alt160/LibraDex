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
    private int? remainingLimit;
    private bool disposed;

    internal LibraDexRangeReader(object innerReader, LibraDexGenericScalarShape shape, int? takeLimit = null)
    {
        this.innerReader = innerReader;
        this.shape = shape;
        remainingLimit = takeLimit;
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
        if (remainingLimit == 0)
        {
            identity = default!;
            return false;
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
    /// Advances the cursor and decodes only the next key value.<br/>
    /// This is the lower-overhead key streaming path for callers that do not need identities; it combines movement and key projection so the generic wrapper does not perform separate shape dispatch for `MoveNext` and `CurrentKey`.<br/>
    /// Byte-array keys are still materialized as owned arrays because the generic API cannot expose shape-local encoded lanes through `TKey`.<br/>
    /// </summary>
    /// <param name="key">Receives the decoded key when a row is available.</param>
    /// <returns><see langword="true"/> when a key was read; otherwise <see langword="false"/>.</returns>
    public bool TryReadNextKey(out TKey key)
    {
        ThrowIfDisposed();
        if (remainingLimit == 0)
        {
            key = default!;
            return false;
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
    /// Advances the cursor to the next matching key/identity row.<br/>
    /// The method delegates to the selected fixed-shape reader and performs no CLR value decoding by itself.<br/>
    /// </summary>
    /// <returns><see langword="true"/> when the reader is positioned on a valid row.</returns>
    public bool MoveNext()
    {
        ThrowIfDisposed();
        if (remainingLimit == 0)
        {
            return false;
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
    /// Skips up to <paramref name="count"/> rows without decoding keys or identities.<br/>
    /// The concrete reader advances across shelf-local slot ranges instead of stepping one row at a time where possible.<br/>
    /// </summary>
    /// <param name="count">The maximum number of rows to skip.</param>
    /// <returns>The number of rows actually skipped.</returns>
    public int Skip(int count)
    {
        ThrowIfDisposed();
        int requestedCount = remainingLimit.HasValue
            ? Math.Min(count, remainingLimit.Value)
            : count;
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
        _ = newKey;
        throw new NotSupportedException("Cursor-local SetKey is part of the public API scaffold but is not connected to traversal-stable physical mutation yet.");
    }

    /// <summary>
    /// Deletes the current key/identity tuple while preserving cursor traversal stability.<br/>
    /// A connected implementation must capture or preserve the next original candidate before applying the delete so traversal neither revisits nor skips entries because of the mutation.<br/>
    /// Batch mode controls durability cadence for this mutation; it does not make cursor deletion a rollback-capable transaction.<br/>
    /// </summary>
    public void Delete()
    {
        ThrowIfDisposed();
        throw new NotSupportedException("Cursor-local Delete is part of the public API scaffold but is not connected to traversal-stable physical mutation yet.");
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
        ((IDisposable)innerReader).Dispose();
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
