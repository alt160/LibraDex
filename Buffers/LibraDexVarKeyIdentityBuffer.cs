namespace LibraDex;

/// <summary>
/// Owns a disconnected bulk read result for variable-length key and variable-length identity tuples.<br/>
/// The buffer keeps keys and identities in separate pooled flat payload buffers with matched row ordinals, avoiding `byte[][]` tuple materialization while preserving array-like access after the index read has returned.<br/>
/// The buffer is independent from the source index and must be disposed by the caller when the result is no longer needed.<br/>
/// </summary>
public sealed class LibraDexVarKeyIdentityBuffer : IDisposable
{
    private readonly LibraDexVarKeyBuffer keys;
    private readonly LibraDexVarIdentityBuffer identities;
    private bool disposed;

    internal LibraDexVarKeyIdentityBuffer(int tupleCapacity, int initialKeyPayloadCapacity = 0, int initialIdentityPayloadCapacity = 0)
    {
        keys = new LibraDexVarKeyBuffer(tupleCapacity, initialKeyPayloadCapacity);
        identities = new LibraDexVarIdentityBuffer(tupleCapacity, initialIdentityPayloadCapacity);
    }

    /// <summary>
    /// Gets the number of key/identity tuples stored in this buffer.<br/>
    /// Key and identity buffers always use the same row ordinal count.<br/>
    /// </summary>
    public int Count
    {
        get
        {
            ThrowIfDisposed();
            return keys.Count;
        }
    }

    /// <summary>
    /// Gets the disconnected key buffer owned by this tuple buffer.<br/>
    /// The returned buffer has the same <see cref="Count"/> and row ordinals as <see cref="Identities"/> and remains valid until this tuple buffer is disposed.<br/>
    /// </summary>
    public LibraDexVarKeyBuffer Keys
    {
        get
        {
            ThrowIfDisposed();
            return keys;
        }
    }

    /// <summary>
    /// Gets the disconnected identity buffer owned by this tuple buffer.<br/>
    /// The returned buffer has the same <see cref="Count"/> and row ordinals as <see cref="Keys"/> and remains valid until this tuple buffer is disposed.<br/>
    /// </summary>
    public LibraDexVarIdentityBuffer Identities
    {
        get
        {
            ThrowIfDisposed();
            return identities;
        }
    }

    /// <summary>
    /// Gets the total occupied key payload bytes across all buffered tuples.<br/>
    /// This mirrors <see cref="LibraDexVarKeyBuffer.PayloadLength"/> for callers that want tuple-level diagnostics without dereferencing <see cref="Keys"/>.<br/>
    /// </summary>
    public int KeyPayloadLength
    {
        get
        {
            ThrowIfDisposed();
            return keys.PayloadLength;
        }
    }

    /// <summary>
    /// Gets the total occupied identity payload bytes across all buffered tuples.<br/>
    /// This mirrors <see cref="LibraDexVarIdentityBuffer.PayloadLength"/> for callers that want tuple-level diagnostics without dereferencing <see cref="Identities"/>.<br/>
    /// </summary>
    public int IdentityPayloadLength
    {
        get
        {
            ThrowIfDisposed();
            return identities.PayloadLength;
        }
    }

    /// <summary>
    /// Gets the key bytes at the supplied zero-based tuple index as a span over this buffer's owned key payload.<br/>
    /// The returned span is valid until this tuple buffer is disposed; callers that need longer lifetime should copy the bytes explicitly.<br/>
    /// </summary>
    /// <param name="index">The zero-based tuple index.</param>
    /// <returns>The key bytes at <paramref name="index"/>.</returns>
    public ReadOnlySpan<byte> GetKeySpan(int index)
    {
        ThrowIfDisposed();
        return keys.GetSpan(index);
    }

    /// <summary>
    /// Gets the identity bytes at the supplied zero-based tuple index as a span over this buffer's owned identity payload.<br/>
    /// The returned span is valid until this tuple buffer is disposed; callers that need longer lifetime should copy the bytes explicitly.<br/>
    /// </summary>
    /// <param name="index">The zero-based tuple index.</param>
    /// <returns>The identity bytes at <paramref name="index"/>.</returns>
    public ReadOnlySpan<byte> GetIdentitySpan(int index)
    {
        ThrowIfDisposed();
        return identities.GetSpan(index);
    }

    /// <summary>
    /// Gets key and identity spans for one tuple in a single call.<br/>
    /// The spans are over this buffer's owned payload arrays and remain valid until this tuple buffer is disposed.<br/>
    /// </summary>
    /// <param name="index">The zero-based tuple index.</param>
    /// <param name="key">The key bytes at <paramref name="index"/>.</param>
    /// <param name="identity">The identity bytes at <paramref name="index"/>.</param>
    public void GetTuple(int index, out ReadOnlySpan<byte> key, out ReadOnlySpan<byte> identity)
    {
        ThrowIfDisposed();
        key = keys.GetSpan(index);
        identity = identities.GetSpan(index);
    }

    /// <summary>
    /// Copies one buffered tuple into caller-owned key and identity storage.<br/>
    /// Prefer <see cref="TryCopyTo"/> when destination sizes are speculative; use this method when insufficient space is a caller error.<br/>
    /// </summary>
    /// <param name="index">The zero-based tuple index.</param>
    /// <param name="keyDestination">The caller-owned destination buffer for the key.</param>
    /// <param name="identityDestination">The caller-owned destination buffer for the identity.</param>
    /// <exception cref="ArgumentException">Thrown when either destination is smaller than its matching tuple component.</exception>
    public void CopyTo(int index, Span<byte> keyDestination, Span<byte> identityDestination)
    {
        ThrowIfDisposed();
        keys.CopyTo(index, keyDestination);
        identities.CopyTo(index, identityDestination);
    }

    /// <summary>
    /// Attempts to copy one buffered tuple into caller-owned key and identity storage.<br/>
    /// No partial copy is performed when either destination is too small.<br/>
    /// </summary>
    /// <param name="index">The zero-based tuple index.</param>
    /// <param name="keyDestination">The caller-owned destination buffer for the key.</param>
    /// <param name="identityDestination">The caller-owned destination buffer for the identity.</param>
    /// <returns><see langword="true"/> when both tuple components fit; otherwise <see langword="false"/>.</returns>
    public bool TryCopyTo(int index, Span<byte> keyDestination, Span<byte> identityDestination)
    {
        ThrowIfDisposed();
        ReadOnlySpan<byte> key = keys.GetSpan(index);
        ReadOnlySpan<byte> identity = identities.GetSpan(index);
        if (keyDestination.Length < key.Length || identityDestination.Length < identity.Length)
        {
            return false;
        }

        key.CopyTo(keyDestination);
        identity.CopyTo(identityDestination);
        return true;
    }

    /// <summary>
    /// Returns a struct enumerator over buffered key/identity tuple spans.<br/>
    /// Enumeration does not allocate; the returned spans remain valid until this tuple buffer is disposed.<br/>
    /// </summary>
    /// <returns>A forward-only struct enumerator over key/identity tuple spans.</returns>
    public Enumerator GetEnumerator()
    {
        ThrowIfDisposed();
        return new Enumerator(this);
    }

    /// <summary>
    /// Releases all pooled storage owned by this tuple buffer.<br/>
    /// Spans, memory values, key buffer references, identity buffer references, and enumerators obtained from this object must not be used after disposal.<br/>
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        keys.Dispose();
        identities.Dispose();
    }

    /// <summary>
    /// Appends one variable-length key and variable-length identity tuple to this disconnected result buffer.<br/>
    /// The key and identity bytes are copied into their matching owned flat payloads, sharing the same row ordinal.<br/>
    /// </summary>
    /// <param name="key">The key bytes to append.</param>
    /// <param name="identity">The identity bytes to append.</param>
    internal void Add(ReadOnlySpan<byte> key, ReadOnlySpan<byte> identity)
    {
        ThrowIfDisposed();
        keys.Add(key);
        identities.Add(identity);
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(LibraDexVarKeyIdentityBuffer));
        }
    }

    /// <summary>
    /// Enumerates key/identity tuple spans from a <see cref="LibraDexVarKeyIdentityBuffer"/> without allocating iterator state.<br/>
    /// The enumerator is intentionally tied to the source buffer; callers must not use it after the buffer is disposed.<br/>
    /// </summary>
    public struct Enumerator
    {
        private readonly LibraDexVarKeyIdentityBuffer buffer;
        private int index;

        internal Enumerator(LibraDexVarKeyIdentityBuffer buffer)
        {
            this.buffer = buffer;
            index = -1;
        }

        /// <summary>
        /// Gets the current tuple as key and identity spans.<br/>
        /// The returned tuple contains spans over the source buffer's owned payload arrays and remains valid until the source buffer is disposed.<br/>
        /// </summary>
        public readonly LibraDexVarKeyIdentitySpan Current => new(buffer.GetKeySpan(index), buffer.GetIdentitySpan(index));

        /// <summary>
        /// Advances the enumerator to the next buffered tuple.<br/>
        /// </summary>
        /// <returns><see langword="true"/> when another tuple is available; otherwise <see langword="false"/>.</returns>
        public bool MoveNext()
        {
            int next = index + 1;
            if (next >= buffer.Count)
            {
                return false;
            }

            index = next;
            return true;
        }
    }
}

/// <summary>
/// Carries one variable-length key and variable-length identity tuple as spans.<br/>
/// The spans are views over the owning buffer or reader and must not be used after that owner advances or is disposed.<br/>
/// </summary>
/// <param name="Key">The raw key bytes.</param>
/// <param name="Identity">The raw identity bytes.</param>
public readonly ref struct LibraDexVarKeyIdentitySpan(ReadOnlySpan<byte> Key, ReadOnlySpan<byte> Identity)
{
    /// <summary>
    /// Gets the raw key bytes for this tuple.<br/>
    /// </summary>
    public ReadOnlySpan<byte> Key { get; } = Key;

    /// <summary>
    /// Gets the raw identity bytes for this tuple.<br/>
    /// </summary>
    public ReadOnlySpan<byte> Identity { get; } = Identity;
}
