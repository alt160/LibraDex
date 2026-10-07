using System.Buffers;

namespace LibraDex;

/// <summary>
/// Owns a disconnected bulk read result for variable-length identity bytes.<br/>
/// The buffer stores all identities in one pooled payload array plus pooled offset and length arrays, avoiding `byte[][]` materialization while still giving callers array-like indexed access after the index read has returned.<br/>
/// The buffer is independent from the source index and must be disposed by the caller when the result is no longer needed.<br/>
/// </summary>
public sealed class LibraDexVarIdentityBuffer : IDisposable
{
    private const int DefaultPayloadCapacity = 4096;

    private byte[] payload;
    private int[] offsets;
    private int[] lengths;
    private int count;
    private int payloadLength;
    private bool disposed;

    internal LibraDexVarIdentityBuffer(int identityCapacity, int initialPayloadCapacity = 0)
    {
        if (identityCapacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(identityCapacity), identityCapacity, "Identity capacity must be non-negative.");
        }

        int offsetCapacity = Math.Max(identityCapacity, 1);
        int payloadCapacity = Math.Max(initialPayloadCapacity, Math.Min(Math.Max(identityCapacity, 1) * 32, DefaultPayloadCapacity));
        payload = ArrayPool<byte>.Shared.Rent(payloadCapacity);
        offsets = ArrayPool<int>.Shared.Rent(offsetCapacity);
        lengths = ArrayPool<int>.Shared.Rent(offsetCapacity);
    }

    /// <summary>
    /// Gets the number of identities stored in this buffer.<br/>
    /// This is the logical result count, not the capacity of the internal pooled arrays.<br/>
    /// </summary>
    public int Count
    {
        get
        {
            ThrowIfDisposed();
            return count;
        }
    }

    /// <summary>
    /// Gets the number of payload bytes occupied by all identities in this buffer.<br/>
    /// This is useful for diagnostics and for callers that want to estimate result payload size without inspecting every identity.<br/>
    /// </summary>
    public int PayloadLength
    {
        get
        {
            ThrowIfDisposed();
            return payloadLength;
        }
    }

    /// <summary>
    /// Gets the occupied flat payload bytes for all identities in this buffer.<br/>
    /// The payload is useful for diagnostics, hashing, or custom bulk codecs; use <see cref="GetOffset"/> and <see cref="GetLength"/> to map an identity index back into this flat span.<br/>
    /// The returned span is valid until this buffer is disposed.<br/>
    /// </summary>
    public ReadOnlySpan<byte> PayloadSpan
    {
        get
        {
            ThrowIfDisposed();
            return payload.AsSpan(0, payloadLength);
        }
    }

    /// <summary>
    /// Gets the occupied flat payload bytes for all identities in this buffer as memory.<br/>
    /// Use this when a helper API requires <see cref="ReadOnlyMemory{T}"/> instead of <see cref="ReadOnlySpan{T}"/>; the memory remains valid until this buffer is disposed.<br/>
    /// </summary>
    public ReadOnlyMemory<byte> PayloadMemory
    {
        get
        {
            ThrowIfDisposed();
            return payload.AsMemory(0, payloadLength);
        }
    }

    /// <summary>
    /// Gets the identity bytes at the supplied zero-based index as a span over this buffer's owned payload.<br/>
    /// The returned span is valid until this buffer is disposed; callers that need longer lifetime should copy the bytes explicitly.<br/>
    /// </summary>
    /// <param name="index">The zero-based identity index.</param>
    /// <returns>The identity bytes at <paramref name="index"/>.</returns>
    public ReadOnlySpan<byte> this[int index] => GetSpan(index);

    /// <summary>
    /// Gets the identity bytes at the supplied zero-based index as a span over this buffer's owned payload.<br/>
    /// The returned span is valid until this buffer is disposed; no per-identity array is allocated.<br/>
    /// </summary>
    /// <param name="index">The zero-based identity index.</param>
    /// <returns>The identity bytes at <paramref name="index"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="index"/> is outside the buffered identity count.</exception>
    public ReadOnlySpan<byte> GetSpan(int index)
    {
        ThrowIfDisposed();
        ValidateIndex(index);
        return payload.AsSpan(offsets[index], lengths[index]);
    }

    /// <summary>
    /// Gets the identity bytes at the supplied zero-based index as memory over this buffer's owned payload.<br/>
    /// Use this when a helper API requires <see cref="ReadOnlyMemory{T}"/> instead of <see cref="ReadOnlySpan{T}"/>; the memory remains valid until this buffer is disposed.<br/>
    /// </summary>
    /// <param name="index">The zero-based identity index.</param>
    /// <returns>The identity bytes at <paramref name="index"/> as read-only memory.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="index"/> is outside the buffered identity count.</exception>
    public ReadOnlyMemory<byte> GetMemory(int index)
    {
        ThrowIfDisposed();
        ValidateIndex(index);
        return payload.AsMemory(offsets[index], lengths[index]);
    }

    /// <summary>
    /// Gets the flat payload offset for the identity at the supplied zero-based index.<br/>
    /// This is intended for advanced callers that want to process <see cref="PayloadSpan"/> or <see cref="PayloadMemory"/> directly without per-identity method calls.<br/>
    /// </summary>
    /// <param name="index">The zero-based identity index.</param>
    /// <returns>The byte offset of the identity inside the occupied flat payload.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="index"/> is outside the buffered identity count.</exception>
    public int GetOffset(int index)
    {
        ThrowIfDisposed();
        ValidateIndex(index);
        return offsets[index];
    }

    /// <summary>
    /// Gets the byte length for the identity at the supplied zero-based index.<br/>
    /// This avoids creating a span when a caller only needs to size a destination buffer or skip over identities by length.<br/>
    /// </summary>
    /// <param name="index">The zero-based identity index.</param>
    /// <returns>The byte length of the identity at <paramref name="index"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="index"/> is outside the buffered identity count.</exception>
    public int GetLength(int index)
    {
        ThrowIfDisposed();
        ValidateIndex(index);
        return lengths[index];
    }

    /// <summary>
    /// Copies one buffered identity into caller-owned storage and throws when the destination is too small.<br/>
    /// Prefer <see cref="TryCopyTo"/> when destination size is speculative; use this method when insufficient space is a caller error.<br/>
    /// </summary>
    /// <param name="index">The zero-based identity index.</param>
    /// <param name="destination">The caller-owned destination buffer.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="index"/> is outside the buffered identity count.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="destination"/> is smaller than the requested identity.</exception>
    public void CopyTo(int index, Span<byte> destination)
    {
        ReadOnlySpan<byte> identity = GetSpan(index);
        if (destination.Length < identity.Length)
        {
            throw new ArgumentException("Destination is smaller than the requested identity.", nameof(destination));
        }

        identity.CopyTo(destination);
    }

    /// <summary>
    /// Attempts to copy one buffered identity into caller-owned storage.<br/>
    /// This is the allocation-free escape hatch for callers that need a stable copy outside the buffer's lifetime.<br/>
    /// </summary>
    /// <param name="index">The zero-based identity index.</param>
    /// <param name="destination">The caller-owned destination buffer.</param>
    /// <returns><see langword="true"/> when the identity fit in <paramref name="destination"/>; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="index"/> is outside the buffered identity count.</exception>
    public bool TryCopyTo(int index, Span<byte> destination)
    {
        ReadOnlySpan<byte> identity = GetSpan(index);
        if (destination.Length < identity.Length)
        {
            return false;
        }

        identity.CopyTo(destination);
        return true;
    }

    /// <summary>
    /// Materializes one buffered identity as an owned byte array.<br/>
    /// This method is intentionally explicit so heap allocation is a caller choice rather than the default bulk-read behavior.<br/>
    /// </summary>
    /// <param name="index">The zero-based identity index.</param>
    /// <returns>An owned byte array containing the identity bytes at <paramref name="index"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="index"/> is outside the buffered identity count.</exception>
    public byte[] ToArray(int index)
    {
        return GetSpan(index).ToArray();
    }

    /// <summary>
    /// Returns a struct enumerator over the buffered identities.<br/>
    /// Enumeration exposes spans over this buffer's owned payload and does not allocate; the spans remain valid until this buffer is disposed.<br/>
    /// </summary>
    /// <returns>A forward-only struct enumerator over the buffered identity spans.</returns>
    public Enumerator GetEnumerator()
    {
        ThrowIfDisposed();
        return new Enumerator(this);
    }

    /// <summary>
    /// Releases all pooled storage owned by this buffer.<br/>
    /// Spans and memory values previously obtained from this buffer must not be used after disposal.<br/>
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        ArrayPool<byte>.Shared.Return(payload, clearArray: false);
        ArrayPool<int>.Shared.Return(offsets, clearArray: false);
        ArrayPool<int>.Shared.Return(lengths, clearArray: false);
        payload = Array.Empty<byte>();
        offsets = Array.Empty<int>();
        lengths = Array.Empty<int>();
        count = 0;
        payloadLength = 0;
    }

    /// <summary>
    /// Appends one variable-length identity to this disconnected result buffer.<br/>
    /// The identity bytes are copied into the owned flat payload, and only offset/length metadata is stored per identity.<br/>
    /// </summary>
    /// <param name="identity">The identity bytes to append.</param>
    internal void Add(ReadOnlySpan<byte> identity)
    {
        ThrowIfDisposed();
        EnsureIdentityCapacity(count + 1);
        EnsurePayloadCapacity(payloadLength + identity.Length);
        offsets[count] = payloadLength;
        lengths[count] = identity.Length;
        identity.CopyTo(payload.AsSpan(payloadLength, identity.Length));
        payloadLength += identity.Length;
        count++;
    }

    private void EnsureIdentityCapacity(int requiredCapacity)
    {
        if (requiredCapacity <= offsets.Length)
        {
            return;
        }

        int newCapacity = Math.Max(requiredCapacity, checked(offsets.Length * 2));
        int[] newOffsets = ArrayPool<int>.Shared.Rent(newCapacity);
        int[] newLengths = ArrayPool<int>.Shared.Rent(newCapacity);
        offsets.AsSpan(0, count).CopyTo(newOffsets);
        lengths.AsSpan(0, count).CopyTo(newLengths);
        ArrayPool<int>.Shared.Return(offsets, clearArray: false);
        ArrayPool<int>.Shared.Return(lengths, clearArray: false);
        offsets = newOffsets;
        lengths = newLengths;
    }

    private void EnsurePayloadCapacity(int requiredCapacity)
    {
        if (requiredCapacity <= payload.Length)
        {
            return;
        }

        int newCapacity = Math.Max(requiredCapacity, checked(payload.Length * 2));
        byte[] newPayload = ArrayPool<byte>.Shared.Rent(newCapacity);
        payload.AsSpan(0, payloadLength).CopyTo(newPayload);
        ArrayPool<byte>.Shared.Return(payload, clearArray: false);
        payload = newPayload;
    }

    private void ValidateIndex(int index)
    {
        if ((uint)index >= (uint)count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "Identity index is outside the buffered result count.");
        }
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(LibraDexVarIdentityBuffer));
        }
    }

    /// <summary>
    /// Enumerates identity spans from a <see cref="LibraDexVarIdentityBuffer"/> without allocating iterator state.<br/>
    /// The enumerator is intentionally tied to the source buffer; callers must not use it after the buffer is disposed.<br/>
    /// </summary>
    public struct Enumerator
    {
        private readonly LibraDexVarIdentityBuffer buffer;
        private int index;

        internal Enumerator(LibraDexVarIdentityBuffer buffer)
        {
            this.buffer = buffer;
            index = -1;
        }

        /// <summary>
        /// Gets the identity bytes at the current enumerator position.<br/>
        /// The returned span is valid until the source buffer is disposed.<br/>
        /// </summary>
        public readonly ReadOnlySpan<byte> Current => buffer.GetSpan(index);

        /// <summary>
        /// Advances the enumerator to the next buffered identity.<br/>
        /// </summary>
        /// <returns><see langword="true"/> when another identity is available; otherwise <see langword="false"/>.</returns>
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
