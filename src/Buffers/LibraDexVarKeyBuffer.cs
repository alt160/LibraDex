using System.Buffers;

namespace LibraDex;

/// <summary>
/// Owns a disconnected bulk read result for variable-length key bytes.<br/>
/// The buffer stores all keys in one pooled payload array plus pooled offset and length arrays, avoiding `byte[][]` materialization while still giving callers array-like indexed access after the index read has returned.<br/>
/// The buffer is independent from the source index and must be disposed by the caller when the result is no longer needed.<br/>
/// </summary>
public sealed class LibraDexVarKeyBuffer : IDisposable
{
    private const int DefaultPayloadCapacity = 4096;

    private byte[] payload;
    private int[] offsets;
    private int[] lengths;
    private int count;
    private int payloadLength;
    private bool disposed;

    internal LibraDexVarKeyBuffer(int keyCapacity, int initialPayloadCapacity = 0)
    {
        if (keyCapacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(keyCapacity), keyCapacity, "Key capacity must be non-negative.");
        }

        int offsetCapacity = Math.Max(keyCapacity, 1);
        int payloadCapacity = Math.Max(initialPayloadCapacity, Math.Min(Math.Max(keyCapacity, 1) * 32, DefaultPayloadCapacity));
        payload = ArrayPool<byte>.Shared.Rent(payloadCapacity);
        offsets = ArrayPool<int>.Shared.Rent(offsetCapacity);
        lengths = ArrayPool<int>.Shared.Rent(offsetCapacity);
    }

    /// <summary>
    /// Gets the number of keys stored in this buffer.<br/>
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
    /// Gets the number of payload bytes occupied by all keys in this buffer.<br/>
    /// This is useful for diagnostics and for callers that want to estimate result payload size without inspecting every key.<br/>
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
    /// Gets the occupied flat payload bytes for all keys in this buffer.<br/>
    /// The payload is useful for diagnostics, hashing, or custom bulk codecs; use <see cref="GetOffset"/> and <see cref="GetLength"/> to map a key index back into this flat span.<br/>
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
    /// Gets the occupied flat payload bytes for all keys in this buffer as memory.<br/>
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
    /// Gets the key bytes at the supplied zero-based index as a span over this buffer's owned payload.<br/>
    /// The returned span is valid until this buffer is disposed; callers that need longer lifetime should copy the bytes explicitly.<br/>
    /// </summary>
    /// <param name="index">The zero-based key index.</param>
    /// <returns>The key bytes at <paramref name="index"/>.</returns>
    public ReadOnlySpan<byte> this[int index] => GetSpan(index);

    /// <summary>
    /// Gets the key bytes at the supplied zero-based index as a span over this buffer's owned payload.<br/>
    /// The returned span is valid until this buffer is disposed; no per-key array is allocated.<br/>
    /// </summary>
    /// <param name="index">The zero-based key index.</param>
    /// <returns>The key bytes at <paramref name="index"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="index"/> is outside the buffered key count.</exception>
    public ReadOnlySpan<byte> GetSpan(int index)
    {
        ThrowIfDisposed();
        ValidateIndex(index);
        return payload.AsSpan(offsets[index], lengths[index]);
    }

    /// <summary>
    /// Gets the key bytes at the supplied zero-based index as memory over this buffer's owned payload.<br/>
    /// Use this when a helper API requires <see cref="ReadOnlyMemory{T}"/> instead of <see cref="ReadOnlySpan{T}"/>; the memory remains valid until this buffer is disposed.<br/>
    /// </summary>
    /// <param name="index">The zero-based key index.</param>
    /// <returns>The key bytes at <paramref name="index"/> as read-only memory.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="index"/> is outside the buffered key count.</exception>
    public ReadOnlyMemory<byte> GetMemory(int index)
    {
        ThrowIfDisposed();
        ValidateIndex(index);
        return payload.AsMemory(offsets[index], lengths[index]);
    }

    /// <summary>
    /// Gets the flat payload offset for the key at the supplied zero-based index.<br/>
    /// This is intended for advanced callers that want to process <see cref="PayloadSpan"/> or <see cref="PayloadMemory"/> directly without per-key method calls.<br/>
    /// </summary>
    /// <param name="index">The zero-based key index.</param>
    /// <returns>The byte offset of the key inside the occupied flat payload.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="index"/> is outside the buffered key count.</exception>
    public int GetOffset(int index)
    {
        ThrowIfDisposed();
        ValidateIndex(index);
        return offsets[index];
    }

    /// <summary>
    /// Gets the byte length for the key at the supplied zero-based index.<br/>
    /// This avoids creating a span when a caller only needs to size a destination buffer or skip over keys by length.<br/>
    /// </summary>
    /// <param name="index">The zero-based key index.</param>
    /// <returns>The byte length of the key at <paramref name="index"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="index"/> is outside the buffered key count.</exception>
    public int GetLength(int index)
    {
        ThrowIfDisposed();
        ValidateIndex(index);
        return lengths[index];
    }

    /// <summary>
    /// Copies one buffered key into caller-owned storage and throws when the destination is too small.<br/>
    /// Prefer <see cref="TryCopyTo"/> when destination size is speculative; use this method when insufficient space is a caller error.<br/>
    /// </summary>
    /// <param name="index">The zero-based key index.</param>
    /// <param name="destination">The caller-owned destination buffer.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="index"/> is outside the buffered key count.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="destination"/> is smaller than the requested key.</exception>
    public void CopyTo(int index, Span<byte> destination)
    {
        ReadOnlySpan<byte> key = GetSpan(index);
        if (destination.Length < key.Length)
        {
            throw new ArgumentException("Destination is smaller than the requested key.", nameof(destination));
        }

        key.CopyTo(destination);
    }

    /// <summary>
    /// Attempts to copy one buffered key into caller-owned storage.<br/>
    /// This is the allocation-free escape hatch for callers that need a stable copy outside the buffer's lifetime.<br/>
    /// </summary>
    /// <param name="index">The zero-based key index.</param>
    /// <param name="destination">The caller-owned destination buffer.</param>
    /// <returns><see langword="true"/> when the key fit in <paramref name="destination"/>; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="index"/> is outside the buffered key count.</exception>
    public bool TryCopyTo(int index, Span<byte> destination)
    {
        ReadOnlySpan<byte> key = GetSpan(index);
        if (destination.Length < key.Length)
        {
            return false;
        }

        key.CopyTo(destination);
        return true;
    }

    /// <summary>
    /// Materializes one buffered key as an owned byte array.<br/>
    /// This method is intentionally explicit so heap allocation is a caller choice rather than the default bulk-read behavior.<br/>
    /// </summary>
    /// <param name="index">The zero-based key index.</param>
    /// <returns>An owned byte array containing the key bytes at <paramref name="index"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="index"/> is outside the buffered key count.</exception>
    public byte[] ToArray(int index)
    {
        return GetSpan(index).ToArray();
    }

    /// <summary>
    /// Returns a struct enumerator over the buffered keys.<br/>
    /// Enumeration exposes spans over this buffer's owned payload and does not allocate; the spans remain valid until this buffer is disposed.<br/>
    /// </summary>
    /// <returns>A forward-only struct enumerator over the buffered key spans.</returns>
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
    /// Appends one variable-length key to this disconnected result buffer.<br/>
    /// The key bytes are copied into the owned flat payload, and only offset/length metadata is stored per key.<br/>
    /// </summary>
    /// <param name="key">The key bytes to append.</param>
    internal void Add(ReadOnlySpan<byte> key)
    {
        ThrowIfDisposed();
        EnsureKeyCapacity(count + 1);
        EnsurePayloadCapacity(payloadLength + key.Length);
        offsets[count] = payloadLength;
        lengths[count] = key.Length;
        key.CopyTo(payload.AsSpan(payloadLength, key.Length));
        payloadLength += key.Length;
        count++;
    }

    private void EnsureKeyCapacity(int requiredCapacity)
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
            throw new ArgumentOutOfRangeException(nameof(index), index, "Key index is outside the buffered result count.");
        }
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(LibraDexVarKeyBuffer));
        }
    }

    /// <summary>
    /// Enumerates key spans from a <see cref="LibraDexVarKeyBuffer"/> without allocating iterator state.<br/>
    /// The enumerator is intentionally tied to the source buffer; callers must not use it after the buffer is disposed.<br/>
    /// </summary>
    public struct Enumerator
    {
        private readonly LibraDexVarKeyBuffer buffer;
        private int index;

        internal Enumerator(LibraDexVarKeyBuffer buffer)
        {
            this.buffer = buffer;
            index = -1;
        }

        /// <summary>
        /// Gets the key bytes at the current enumerator position.<br/>
        /// The returned span is valid until the source buffer is disposed.<br/>
        /// </summary>
        public readonly ReadOnlySpan<byte> Current => buffer.GetSpan(index);

        /// <summary>
        /// Advances the enumerator to the next buffered key.<br/>
        /// </summary>
        /// <returns><see langword="true"/> when another key is available; otherwise <see langword="false"/>.</returns>
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
