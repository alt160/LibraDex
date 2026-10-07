using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace LibraDex;

public sealed partial class LibraDexRoutedCompositeIndex
{
    /// <summary>
    /// Opens a page-native cursor over this persisted composite index.<br/>
    /// The cursor walks durable node pages directly, keeps a bounded route stack, and decodes requested values from their current byte slices without hydrating the routed object tree.<br/>
    /// Memory-only composite indexes do not have page bytes to borrow and therefore are intentionally rejected instead of silently falling back to the object-based cursor.<br/>
    /// </summary>
    /// <returns>An unpositioned page-native composite cursor owned by the caller.<br/></returns>
    internal NativeEntryCursor OpenNativeEntryCursor()
        => new(this);

    /// <summary>
    /// Traverses a persisted routed composite tree directly from its node pages.<br/>
    /// Page buffers are rented per active route depth and returned on disposal; current spans and memories are borrowed and become invalid after the next <see cref="Read"/> or disposal.<br/>
    /// </summary>
    internal sealed class NativeEntryCursor : IDisposable
    {
        private const int HeaderSize = 22;
        private const int Version2HeaderSize = 26;
        private const ushort Version1 = 1;
        private const ushort Version2 = 2;
        private const int ChildDirectoryEntrySize = 16;

        private readonly LibraDexRoutedCompositeIndex owner;
        private readonly PageFrame[] pages;
        private readonly ValueLocation[] parts;
        private ValueLocation identity;
        private int tier;
        private bool started;
        private bool positioned;
        private bool disposed;

        /// <summary>
        /// Creates one page-native reader over the current durable root snapshot.<br/>
        /// </summary>
        /// <param name="owner">The persisted routed composite index to traverse.<br/></param>
        internal NativeEntryCursor(LibraDexRoutedCompositeIndex owner)
        {
            this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
            if (owner.session is null || owner.root.DurableOffset <= 0)
            {
                throw new InvalidOperationException(
                    $"Composite index '{owner.Name}' has no durable node-page root. Page-native readers require a persisted composite index.");
            }

            int partCount = owner.shape.CompositeParts.Count;
            pages = new PageFrame[partCount + 1];
            parts = new ValueLocation[partCount];
            tier = -1;
        }

        /// <summary>
        /// Advances to the next key/identity tuple in natural composite order.<br/>
        /// No composite key object, object array, or boxed scalar value is created while traversing node-page bytes.<br/>
        /// </summary>
        /// <returns><see langword="true"/> when current part and identity accessors are available; otherwise <see langword="false"/>.<br/></returns>
        public bool Read()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            positioned = false;
            identity = default;

            if (!started)
            {
                started = true;
                LoadPage(0, owner.root.DurableOffset);
                tier = 0;
            }

            int partCount = parts.Length;
            while (tier >= 0)
            {
                ref PageFrame page = ref pages[tier];
                if (tier == partCount)
                {
                    if (page.NextIdentity < page.IdentityCount)
                    {
                        identity = ReadNextIdentity(ref page);
                        positioned = true;
                        return true;
                    }

                    ReleasePage(tier);
                    tier--;
                    if (tier >= 0)
                        parts[tier] = default;
                    continue;
                }

                if (page.NextChild < page.ChildCount)
                {
                    if (!TryReadNextChild(ref page, owner.shape.CompositeParts[tier], out ValueLocation value, out long childOffset))
                    {
                        throw new InvalidDataException("Composite node page child route is invalid.");
                    }

                    parts[tier] = value;
                    LoadPage(tier + 1, childOffset);
                    tier++;
                    continue;
                }

                ReleasePage(tier);
                if (tier == 0)
                {
                    tier = -1;
                    return false;
                }

                tier--;
                parts[tier] = default;
            }

            return false;
        }

        /// <summary>
        /// Gets the current first typed composite value.<br/>
        /// Accessing a string or byte-array value materializes that requested CLR object; callers that only need UTF-8 or binary bytes should use the borrowed span or memory accessors instead.<br/>
        /// </summary>
        internal T ReadPart<T>(int partIndex)
        {
            EnsurePositioned();
            ValidatePartIndex(partIndex);
            ValueLocation value = parts[partIndex];
            if (value.IsNull)
            {
                if (default(T) is null)
                    return default!;
                throw new InvalidOperationException($"Composite part {partIndex + 1} is null; inspect IsNull before reading it as {typeof(T).FullName}.");
            }

            return ReadValue<T>(value, owner.shape.CompositeParts[partIndex].KeyType);
        }

        /// <summary>
        /// Gets the current typed identity directly from its persisted bytes.<br/>
        /// </summary>
        internal T ReadIdentity<T>()
        {
            EnsurePositioned();
            return ReadValue<T>(identity, owner.shape.IdentityType);
        }

        /// <summary>
        /// Tests whether a current key component is the persisted logical null route.<br/>
        /// </summary>
        internal bool IsNull(int partIndex)
        {
            EnsurePositioned();
            ValidatePartIndex(partIndex);
            return parts[partIndex].IsNull;
        }

        /// <summary>
        /// Gets the exact encoded bytes for one current composite component, including its null-state marker and value framing.<br/>
        /// The returned span is borrowed and must not be retained after the next <see cref="Read"/> or disposal.<br/>
        /// </summary>
        internal ReadOnlySpan<byte> GetRawSpan(int partIndex)
        {
            EnsurePositioned();
            ValidatePartIndex(partIndex);
            return parts[partIndex].RawSpan;
        }

        /// <summary>
        /// Gets the exact encoded bytes for one current composite component as borrowed memory, including its null-state marker and value framing.<br/>
        /// The returned memory is backed by a rented page buffer and must not be retained after the next <see cref="Read"/> or disposal.<br/>
        /// </summary>
        internal ReadOnlyMemory<byte> GetRawMem(int partIndex)
        {
            EnsurePositioned();
            ValidatePartIndex(partIndex);
            return parts[partIndex].RawMemory;
        }

        /// <summary>
        /// Gets the logical UTF-8 payload of one current string component without allocating a UTF-16 <see cref="string"/>.<br/>
        /// Null string routes return an empty span; use <see cref="IsNull"/> when empty and null must remain distinct.<br/>
        /// </summary>
        internal ReadOnlySpan<byte> GetUtf8Span(int partIndex)
        {
            EnsurePositioned();
            ValidateStringPart(partIndex);
            return GetUtf8Payload(parts[partIndex]).Span;
        }

        /// <summary>
        /// Gets the logical UTF-8 payload of one current string component as borrowed memory without allocating a UTF-16 <see cref="string"/>.<br/>
        /// Null string routes return empty memory; use <see cref="IsNull"/> when empty and null must remain distinct.<br/>
        /// </summary>
        internal ReadOnlyMemory<byte> GetUtf8Mem(int partIndex)
        {
            EnsurePositioned();
            ValidateStringPart(partIndex);
            return GetUtf8Payload(parts[partIndex]);
        }

        /// <summary>
        /// Gets the exact encoded bytes for the current identity.<br/>
        /// The returned span is borrowed and must not be retained after the next <see cref="Read"/> or disposal.<br/>
        /// </summary>
        internal ReadOnlySpan<byte> GetIdentityRawSpan()
        {
            EnsurePositioned();
            return identity.RawSpan;
        }

        /// <summary>
        /// Gets the exact encoded bytes for the current identity as borrowed memory.<br/>
        /// The returned memory must not be retained after the next <see cref="Read"/> or disposal.<br/>
        /// </summary>
        internal ReadOnlyMemory<byte> GetIdentityRawMem()
        {
            EnsurePositioned();
            return identity.RawMemory;
        }

        /// <summary>
        /// Returns every rented page buffer and invalidates all borrowed values.<br/>
        /// </summary>
        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            positioned = false;
            identity = default;
            Array.Clear(parts);
            for (int i = 0; i < pages.Length; i++)
                ReleasePage(i);
        }

        private void LoadPage(int pageTier, long offset)
        {
            if (offset <= 0)
                throw new InvalidDataException("Composite node page has an invalid child offset.");

            ref PageFrame frame = ref pages[pageTier];
            ReleasePage(pageTier);
            Span<byte> destination = frame.Buffer is null ? Span<byte>.Empty : frame.Buffer;
            if (!owner.session!.TryReadCompositeNodePage(offset, destination, out int length))
                throw new InvalidDataException("Composite node page could not be read from the requested offset.");

            if (frame.Buffer is null || frame.Buffer.Length < length)
            {
                if (frame.Buffer is not null)
                    ArrayPool<byte>.Shared.Return(frame.Buffer);
                frame.Buffer = ArrayPool<byte>.Shared.Rent(length);
                if (!owner.session.TryReadCompositeNodePage(offset, frame.Buffer, out int rereadLength) || rereadLength != length)
                    throw new InvalidDataException("Composite node page changed while it was being opened for page-native traversal.");
            }

            frame.Length = length;
            InitializeFrame(ref frame, pageTier);
        }

        private void InitializeFrame(ref PageFrame frame, int expectedTier)
        {
            ReadOnlySpan<byte> source = frame.Span;
            if (source.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(source[..4]) != 0x4E50434C)
                throw new InvalidDataException("Composite node page does not contain the expected magic value.");

            ushort version = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(4, 2));
            if (version != Version1 && version != Version2)
                throw new InvalidDataException($"Composite node page version {version} is not supported.");
            if (BinaryPrimitives.ReadInt32LittleEndian(source.Slice(6, 4)) != source.Length)
                throw new InvalidDataException("Composite node page length does not match the page buffer.");

            int pageTier = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(10, 4));
            int identityCount = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(14, 4));
            int childCount = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(18, 4));
            if (pageTier != expectedTier || identityCount < 0 || childCount < 0)
                throw new InvalidDataException("Composite node page has an invalid tier or count.");
            if (identityCount > 0 && pageTier != parts.Length)
                throw new InvalidDataException("Composite node page contains identities before its terminal tier.");
            if (childCount > 0 && pageTier >= parts.Length)
                throw new InvalidDataException("Terminal composite node page contains child routes.");

            frame.Version = version;
            frame.IdentityCount = identityCount;
            frame.ChildCount = childCount;
            frame.NextIdentity = 0;
            frame.NextChild = 0;
            frame.IdentityOffset = version == Version1 ? HeaderSize : Version2HeaderSize;
            frame.NextChildOffset = frame.IdentityOffset;
            frame.ChildDirectoryOffset = 0;

            int identityLimit = source.Length;
            if (version == Version2)
            {
                if (source.Length < Version2HeaderSize)
                    throw new InvalidDataException("Version 2 composite node page header is truncated.");
                int directoryOffset = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(22, 4));
                int directoryLength = checked(childCount * ChildDirectoryEntrySize);
                if (directoryOffset < Version2HeaderSize || directoryOffset > source.Length || directoryLength > source.Length - directoryOffset)
                    throw new InvalidDataException("Composite node page child directory is outside the page body.");
                frame.ChildDirectoryOffset = directoryOffset;
                identityLimit = directoryOffset;
            }

            int identityOffset = frame.IdentityOffset;
            for (int i = 0; i < identityCount; i++)
            {
                if (!TryReadValueLocation(frame.Buffer!, identityOffset, identityLimit, owner.shape.IdentityType, out ValueLocation current))
                    throw new InvalidDataException("Composite node page identity is malformed.");
                identityOffset += current.Length;
            }

            if (version == Version1)
                frame.NextChildOffset = identityOffset;
            else if (identityOffset != identityLimit)
                throw new InvalidDataException("Composite node page identity payload overlaps its child directory.");
        }

        private ValueLocation ReadNextIdentity(ref PageFrame page)
        {
            int offset = page.IdentityOffset;
            int limit = page.Version == Version2 ? page.ChildDirectoryOffset : page.Length;
            for (int i = 0; i < page.NextIdentity; i++)
            {
                if (!TryReadValueLocation(page.Buffer!, offset, limit, owner.shape.IdentityType, out ValueLocation prior))
                    throw new InvalidDataException("Composite node page identity is malformed.");
                offset += prior.Length;
            }

            if (!TryReadValueLocation(page.Buffer!, offset, limit, owner.shape.IdentityType, out ValueLocation value))
                throw new InvalidDataException("Composite node page identity is malformed.");
            page.NextIdentity++;
            return value;
        }

        private static bool TryReadNextChild(
            ref PageFrame page,
            LibraDexCompositeKeyPartSpec part,
            out ValueLocation value,
            out long childOffset)
        {
            value = default;
            childOffset = 0;
            if (page.NextChild >= page.ChildCount || page.Buffer is null)
                return false;

            if (page.Version == Version2)
            {
                int entryOffset = checked(page.ChildDirectoryOffset + (page.NextChild * ChildDirectoryEntrySize));
                ReadOnlySpan<byte> entry = page.Span.Slice(entryOffset, ChildDirectoryEntrySize);
                int valueOffset = BinaryPrimitives.ReadInt32LittleEndian(entry[..4]);
                int valueLength = BinaryPrimitives.ReadInt32LittleEndian(entry.Slice(4, 4));
                if (valueOffset < 0 || valueLength <= 0 || valueOffset > page.Length || valueLength > page.Length - valueOffset ||
                    !TryReadPartLocation(page.Buffer, valueOffset, valueOffset + valueLength, part.KeyType, out value) ||
                    value.Length != valueLength)
                {
                    return false;
                }

                childOffset = BinaryPrimitives.ReadInt64LittleEndian(entry.Slice(8, 8));
                page.NextChild++;
                return childOffset > 0;
            }

            if (!TryReadPartLocation(page.Buffer, page.NextChildOffset, page.Length, part.KeyType, out value))
                return false;
            int offsetOffset = checked(page.NextChildOffset + value.Length);
            if (offsetOffset > page.Length - sizeof(long))
                return false;
            childOffset = BinaryPrimitives.ReadInt64LittleEndian(page.Span.Slice(offsetOffset, sizeof(long)));
            page.NextChildOffset = offsetOffset + sizeof(long);
            page.NextChild++;
            return childOffset > 0;
        }

        private static bool TryReadPartLocation(byte[] buffer, int offset, int limit, Type type, out ValueLocation location)
        {
            location = default;
            if (offset < 0 || offset >= limit)
                return false;

            byte marker = buffer[offset];
            if (marker is 1 or 2)
            {
                location = new ValueLocation(buffer, offset, 1, offset + 1, 0, isNull: true);
                return true;
            }

            if (marker != 0 || !TryReadValueLocation(buffer, offset + 1, limit, type, out ValueLocation payload))
                return false;
            location = new ValueLocation(buffer, offset, payload.Length + 1, payload.Offset, payload.Length, isNull: false);
            return true;
        }

        private static bool TryReadValueLocation(byte[] buffer, int offset, int limit, Type type, out ValueLocation location)
        {
            location = default;
            if (offset < 0 || offset > limit)
                return false;

            ReadOnlySpan<byte> source = buffer.AsSpan(offset, limit - offset);
            int length;
            if (type == typeof(string))
            {
                if (!TryRead7BitEncodedInt(source, out int byteLength, out int prefixLength) || byteLength < 0 || byteLength > source.Length - prefixLength)
                    return false;
                length = prefixLength + byteLength;
            }
            else if (type == typeof(byte[]))
            {
                if (source.Length < sizeof(int))
                    return false;
                int byteLength = BinaryPrimitives.ReadInt32LittleEndian(source);
                if (byteLength < 0 || byteLength > source.Length - sizeof(int))
                    return false;
                length = sizeof(int) + byteLength;
            }
            else if (type == typeof(Guid) || type == typeof(Int128) || type == typeof(UInt128) || type == typeof(decimal) || type == typeof(DateTimeOffset))
                length = 16;
            else if (type == typeof(long) || type == typeof(ulong) || type == typeof(double) || type == typeof(TimeSpan) || type == typeof(TimeOnly))
                length = 8;
            else if (type == typeof(int) || type == typeof(uint) || type == typeof(float) || type == typeof(DateOnly))
                length = 4;
            else if (type == typeof(short) || type == typeof(ushort))
                length = 2;
            else if (type == typeof(byte) || type == typeof(sbyte) || type == typeof(bool))
                length = 1;
            else if (type == typeof(DateTime))
                length = 9;
            else if (type == typeof(char))
            {
                if (source.IsEmpty)
                    return false;
                length = GetUtf8SequenceLength(source[0]);
            }
            else
                return false;

            if (length < 0 || length > source.Length)
                return false;
            location = new ValueLocation(buffer, offset, length, offset, length, isNull: false);
            return true;
        }

        private static ReadOnlyMemory<byte> GetUtf8Payload(ValueLocation value)
        {
            if (value.IsNull)
                return ReadOnlyMemory<byte>.Empty;
            ReadOnlySpan<byte> encoded = value.PayloadSpan;
            if (!TryRead7BitEncodedInt(encoded, out int length, out int prefixLength) || length < 0 || length != encoded.Length - prefixLength)
                throw new InvalidDataException("Composite string value has an invalid UTF-8 length prefix.");
            return value.Buffer!.AsMemory(value.PayloadOffset + prefixLength, length);
        }

        private static T ReadValue<T>(ValueLocation value, Type type)
        {
            if (typeof(T) != type)
            {
                throw new InvalidOperationException($"The requested reader type {typeof(T).FullName} does not match persisted composite type {type.FullName}.");
            }

            ReadOnlySpan<byte> source = value.PayloadSpan;
            if (type == typeof(string))
            {
                ReadOnlyMemory<byte> utf8 = GetUtf8Payload(value);
                string text = Encoding.UTF8.GetString(utf8.Span);
                return Reinterpret<string, T>(text);
            }

            if (type == typeof(byte[]))
            {
                if (source.Length < sizeof(int))
                    throw new InvalidDataException("Composite binary value is truncated.");
                int length = BinaryPrimitives.ReadInt32LittleEndian(source);
                if (length < 0 || length != source.Length - sizeof(int))
                    throw new InvalidDataException("Composite binary value length is invalid.");
                byte[] bytes = source.Slice(sizeof(int), length).ToArray();
                return Reinterpret<byte[], T>(bytes);
            }

            if (type == typeof(Guid)) return Reinterpret<Guid, T>(new Guid(source));
            if (type == typeof(long)) return Reinterpret<long, T>(BinaryPrimitives.ReadInt64LittleEndian(source));
            if (type == typeof(ulong)) return Reinterpret<ulong, T>(BinaryPrimitives.ReadUInt64LittleEndian(source));
            if (type == typeof(int)) return Reinterpret<int, T>(BinaryPrimitives.ReadInt32LittleEndian(source));
            if (type == typeof(uint)) return Reinterpret<uint, T>(BinaryPrimitives.ReadUInt32LittleEndian(source));
            if (type == typeof(short)) return Reinterpret<short, T>(BinaryPrimitives.ReadInt16LittleEndian(source));
            if (type == typeof(ushort)) return Reinterpret<ushort, T>(BinaryPrimitives.ReadUInt16LittleEndian(source));
            if (type == typeof(byte)) return Reinterpret<byte, T>(source[0]);
            if (type == typeof(sbyte)) return Reinterpret<sbyte, T>(unchecked((sbyte)source[0]));
            if (type == typeof(bool)) return Reinterpret<bool, T>(source[0] != 0);
            if (type == typeof(float)) return Reinterpret<float, T>(BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(source)));
            if (type == typeof(double)) return Reinterpret<double, T>(BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(source)));
            if (type == typeof(Int128))
            {
                Int128 parsed = ((Int128)BinaryPrimitives.ReadInt64LittleEndian(source.Slice(8, 8)) << 64) |
                    BinaryPrimitives.ReadUInt64LittleEndian(source);
                return Reinterpret<Int128, T>(parsed);
            }
            if (type == typeof(UInt128))
            {
                UInt128 parsed = ((UInt128)BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(8, 8)) << 64) |
                    BinaryPrimitives.ReadUInt64LittleEndian(source);
                return Reinterpret<UInt128, T>(parsed);
            }
            if (type == typeof(decimal))
            {
                int lo = BinaryPrimitives.ReadInt32LittleEndian(source);
                int mid = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(4, 4));
                int hi = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(8, 4));
                int flags = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(12, 4));
                decimal parsed = new(lo, mid, hi, flags < 0, checked((byte)((flags >> 16) & 0x7F)));
                return Reinterpret<decimal, T>(parsed);
            }
            if (type == typeof(char))
            {
                Span<char> chars = stackalloc char[1];
                if (Encoding.UTF8.GetChars(source, chars) != 1)
                    throw new InvalidDataException("Composite character value is not one UTF-8 character.");
                return Reinterpret<char, T>(chars[0]);
            }
            if (type == typeof(DateTime))
                return Reinterpret<DateTime, T>(new DateTime(BinaryPrimitives.ReadInt64LittleEndian(source), (DateTimeKind)source[8]));
            if (type == typeof(DateTimeOffset))
                return Reinterpret<DateTimeOffset, T>(new DateTimeOffset(BinaryPrimitives.ReadInt64LittleEndian(source), TimeSpan.FromTicks(BinaryPrimitives.ReadInt64LittleEndian(source.Slice(8, 8)))));
            if (type == typeof(DateOnly)) return Reinterpret<DateOnly, T>(DateOnly.FromDayNumber(BinaryPrimitives.ReadInt32LittleEndian(source)));
            if (type == typeof(TimeOnly)) return Reinterpret<TimeOnly, T>(TimeOnly.FromTimeSpan(TimeSpan.FromTicks(BinaryPrimitives.ReadInt64LittleEndian(source))));
            if (type == typeof(TimeSpan)) return Reinterpret<TimeSpan, T>(TimeSpan.FromTicks(BinaryPrimitives.ReadInt64LittleEndian(source)));

            throw new NotSupportedException($"Composite page-native reader does not support type {type.FullName}.");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static TTo Reinterpret<TFrom, TTo>(TFrom value)
            => Unsafe.As<TFrom, TTo>(ref value);

        private static bool TryRead7BitEncodedInt(ReadOnlySpan<byte> source, out int value, out int length)
        {
            value = 0;
            length = 0;
            uint result = 0;
            for (int shift = 0; shift < 35; shift += 7)
            {
                if (length >= source.Length)
                    return false;
                byte current = source[length++];
                result |= (uint)(current & 0x7F) << shift;
                if ((current & 0x80) == 0)
                {
                    if (result > int.MaxValue)
                        return false;
                    value = (int)result;
                    return true;
                }
            }

            return false;
        }

        private static int GetUtf8SequenceLength(byte value)
        {
            if (value < 0x80) return 1;
            if ((value & 0xE0) == 0xC0) return 2;
            if ((value & 0xF0) == 0xE0) return 3;
            if ((value & 0xF8) == 0xF0) return 4;
            throw new InvalidDataException("Composite character value has an invalid UTF-8 prefix.");
        }

        private void ValidatePartIndex(int partIndex)
        {
            if ((uint)partIndex >= (uint)parts.Length)
                throw new ArgumentOutOfRangeException(nameof(partIndex));
        }

        private void ValidateStringPart(int partIndex)
        {
            ValidatePartIndex(partIndex);
            if (owner.shape.CompositeParts[partIndex].KeyType != typeof(string))
            {
                throw new InvalidOperationException(
                    $"Composite part {partIndex + 1} stores {owner.shape.CompositeParts[partIndex].KeyType.FullName}, not UTF-8 text.");
            }
        }

        private void EnsurePositioned()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!positioned)
                throw new InvalidOperationException("The page-native composite reader is not positioned on an entry.");
        }

        private void ReleasePage(int pageTier)
        {
            ref PageFrame frame = ref pages[pageTier];
            if (frame.Buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(frame.Buffer);
                frame.Buffer = null;
            }

            frame = default;
        }

        private struct PageFrame
        {
            internal byte[]? Buffer;
            internal int Length;
            internal ushort Version;
            internal int IdentityCount;
            internal int ChildCount;
            internal int NextIdentity;
            internal int NextChild;
            internal int IdentityOffset;
            internal int NextChildOffset;
            internal int ChildDirectoryOffset;

            internal readonly ReadOnlySpan<byte> Span => Buffer is null ? ReadOnlySpan<byte>.Empty : Buffer.AsSpan(0, Length);
        }

        private readonly struct ValueLocation
        {
            internal ValueLocation(byte[] buffer, int offset, int length, int payloadOffset, int payloadLength, bool isNull)
            {
                Buffer = buffer;
                Offset = offset;
                Length = length;
                PayloadOffset = payloadOffset;
                PayloadLength = payloadLength;
                IsNull = isNull;
            }

            internal byte[]? Buffer { get; }
            internal int Offset { get; }
            internal int Length { get; }
            internal int PayloadOffset { get; }
            internal int PayloadLength { get; }
            internal bool IsNull { get; }
            internal ReadOnlySpan<byte> RawSpan => Buffer is null ? ReadOnlySpan<byte>.Empty : Buffer.AsSpan(Offset, Length);
            internal ReadOnlyMemory<byte> RawMemory => Buffer is null ? ReadOnlyMemory<byte>.Empty : Buffer.AsMemory(Offset, Length);
            internal ReadOnlySpan<byte> PayloadSpan => Buffer is null ? ReadOnlySpan<byte>.Empty : Buffer.AsSpan(PayloadOffset, PayloadLength);
        }
    }
}
