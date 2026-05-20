using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace LibraDex;

internal enum LibraDexGenericScalarShape
{
    SS88 = 0,
    SS168 = 1,
    SS816 = 2,
    SS1616 = 3,
    FS328 = 4,
    FS3216 = 5
}

internal static class LibraDexGenericScalarCodec<T>
{
    internal static LibraDexScalarWidth ResolveWidth(LibraDexScalarWidth? explicitWidth)
    {
        Type type = typeof(T);
        if (type == typeof(byte[]))
        {
            return explicitWidth ?? throw new ArgumentException("Generic LibraDex byte[] keys and identities require an explicit scalar width.");
        }

        if (explicitWidth is not null)
        {
            throw new ArgumentException("Generic LibraDex scalar width overrides are only supported for byte[] keys and identities in this slice.");
        }

        if (type == typeof(Guid))
        {
            return LibraDexScalarWidth.Bytes16;
        }

        if (type == typeof(byte) ||
            type == typeof(sbyte) ||
            type == typeof(short) ||
            type == typeof(ushort) ||
            type == typeof(char) ||
            type == typeof(int) ||
            type == typeof(uint) ||
            type == typeof(long) ||
            type == typeof(ulong))
        {
            return LibraDexScalarWidth.Bytes8;
        }

        throw new NotSupportedException($"Generic LibraDex indexes do not support scalar type {type.FullName}.");
    }

    internal static ulong Encode8(T value)
    {
        Type type = typeof(T);
        if (type == typeof(byte))
        {
            return Unsafe.As<T, byte>(ref value);
        }

        if (type == typeof(sbyte))
        {
            sbyte typed = Unsafe.As<T, sbyte>(ref value);
            return unchecked((byte)(typed ^ sbyte.MinValue));
        }

        if (type == typeof(short))
        {
            short typed = Unsafe.As<T, short>(ref value);
            return unchecked((ushort)(typed ^ short.MinValue));
        }

        if (type == typeof(ushort))
        {
            return Unsafe.As<T, ushort>(ref value);
        }

        if (type == typeof(char))
        {
            return Unsafe.As<T, char>(ref value);
        }

        if (type == typeof(int))
        {
            int typed = Unsafe.As<T, int>(ref value);
            return unchecked((uint)(typed ^ int.MinValue));
        }

        if (type == typeof(uint))
        {
            return Unsafe.As<T, uint>(ref value);
        }

        if (type == typeof(long))
        {
            long typed = Unsafe.As<T, long>(ref value);
            return unchecked((ulong)(typed ^ long.MinValue));
        }

        if (type == typeof(ulong))
        {
            return Unsafe.As<T, ulong>(ref value);
        }

        if (type == typeof(byte[]))
        {
            byte[] typed = Unsafe.As<T, byte[]>(ref value) ?? throw new ArgumentNullException(nameof(value), "Generic LibraDex scalar values cannot be null.");
            return EncodeByteArray8(typed);
        }

        throw new NotSupportedException($"Generic LibraDex indexes cannot encode scalar type {type.FullName} as 8 bytes.");
    }

    internal static void Encode16(T value, out ulong high, out ulong low)
    {
        Type type = typeof(T);
        if (type == typeof(Guid))
        {
            Guid typed = Unsafe.As<T, Guid>(ref value);
            Span<byte> bytes = stackalloc byte[16];
            typed.TryWriteBytes(bytes);
            high = BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(0, 8));
            low = BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(8, 8));
            return;
        }

        if (type == typeof(byte[]))
        {
            byte[] typed = Unsafe.As<T, byte[]>(ref value) ?? throw new ArgumentNullException(nameof(value), "Generic LibraDex scalar values cannot be null.");
            if (typed.Length != 16)
            {
                throw new ArgumentException("Generic LibraDex byte[] values routed to 16-byte scalars must be exactly 16 bytes.", nameof(value));
            }

            high = BinaryPrimitives.ReadUInt64BigEndian(typed.AsSpan(0, 8));
            low = BinaryPrimitives.ReadUInt64BigEndian(typed.AsSpan(8, 8));
            return;
        }

        throw new NotSupportedException($"Generic LibraDex indexes cannot encode scalar type {type.FullName} as 16 bytes.");
    }

    internal static void Encode32(T value, out ulong part0, out ulong part1, out ulong part2, out ulong part3)
    {
        if (typeof(T) != typeof(byte[]))
        {
            throw new NotSupportedException($"Generic LibraDex indexes cannot encode scalar type {typeof(T).FullName} as 32 bytes.");
        }

        byte[] typed = Unsafe.As<T, byte[]>(ref value) ?? throw new ArgumentNullException(nameof(value), "Generic LibraDex scalar values cannot be null.");
        if (typed.Length != 32)
        {
            throw new ArgumentException("Generic LibraDex byte[] values routed to 32-byte fixed keys must be exactly 32 bytes.", nameof(value));
        }

        ReadOnlySpan<byte> bytes = typed;
        part0 = BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(0, 8));
        part1 = BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(8, 8));
        part2 = BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(16, 8));
        part3 = BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(24, 8));
    }

    internal static T Decode8(ulong encodedValue)
    {
        Type type = typeof(T);
        if (type == typeof(byte))
        {
            byte typed = checked((byte)encodedValue);
            return Unsafe.As<byte, T>(ref typed);
        }

        if (type == typeof(sbyte))
        {
            sbyte typed = unchecked((sbyte)((byte)encodedValue ^ sbyte.MinValue));
            return Unsafe.As<sbyte, T>(ref typed);
        }

        if (type == typeof(short))
        {
            short typed = unchecked((short)((ushort)encodedValue ^ short.MinValue));
            return Unsafe.As<short, T>(ref typed);
        }

        if (type == typeof(ushort))
        {
            ushort typed = checked((ushort)encodedValue);
            return Unsafe.As<ushort, T>(ref typed);
        }

        if (type == typeof(char))
        {
            char typed = checked((char)encodedValue);
            return Unsafe.As<char, T>(ref typed);
        }

        if (type == typeof(int))
        {
            int typed = unchecked((int)((uint)encodedValue ^ int.MinValue));
            return Unsafe.As<int, T>(ref typed);
        }

        if (type == typeof(uint))
        {
            uint typed = checked((uint)encodedValue);
            return Unsafe.As<uint, T>(ref typed);
        }

        if (type == typeof(long))
        {
            long typed = unchecked((long)(encodedValue ^ (ulong)long.MinValue));
            return Unsafe.As<long, T>(ref typed);
        }

        if (type == typeof(ulong))
        {
            ulong typed = encodedValue;
            return Unsafe.As<ulong, T>(ref typed);
        }

        if (type == typeof(byte[]))
        {
            byte[] bytes = new byte[sizeof(ulong)];
            BinaryPrimitives.WriteUInt64BigEndian(bytes, encodedValue);
            return Unsafe.As<byte[], T>(ref bytes);
        }

        throw new NotSupportedException($"Generic LibraDex indexes cannot decode scalar type {type.FullName} from 8 bytes.");
    }

    internal static T Decode16(ulong high, ulong low)
    {
        Type type = typeof(T);
        if (type == typeof(Guid))
        {
            Span<byte> bytes = stackalloc byte[16];
            BinaryPrimitives.WriteUInt64BigEndian(bytes.Slice(0, 8), high);
            BinaryPrimitives.WriteUInt64BigEndian(bytes.Slice(8, 8), low);
            Guid typed = new(bytes);
            return Unsafe.As<Guid, T>(ref typed);
        }

        if (type == typeof(byte[]))
        {
            byte[] bytes = new byte[16];
            BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(0, 8), high);
            BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(8, 8), low);
            return Unsafe.As<byte[], T>(ref bytes);
        }

        throw new NotSupportedException($"Generic LibraDex indexes cannot decode scalar type {type.FullName} from 16 bytes.");
    }

    /// <summary>
    /// Decodes four persisted fixed-key lanes into the generic CLR value for a 32-byte fixed scalar projection.<br/>
    /// The current 32-byte generic surface is intentionally restricted to `byte[]`, because there is no broadly accepted CLR scalar wider than 16 bytes that maps naturally to this shelf family.<br/>
    /// The returned byte array is owned by the caller; hot cursor paths that need to avoid this allocation should use the encoded lane reader directly.<br/>
    /// </summary>
    /// <param name="part0">The first encoded key lane in persisted sortable order.</param>
    /// <param name="part1">The second encoded key lane in persisted sortable order.</param>
    /// <param name="part2">The third encoded key lane in persisted sortable order.</param>
    /// <param name="part3">The fourth encoded key lane in persisted sortable order.</param>
    /// <returns>The decoded CLR value represented by the four fixed lanes.</returns>
    internal static T Decode32(ulong part0, ulong part1, ulong part2, ulong part3)
    {
        if (typeof(T) != typeof(byte[]))
        {
            throw new NotSupportedException($"Generic LibraDex indexes cannot decode scalar type {typeof(T).FullName} from 32 bytes.");
        }

        byte[] bytes = new byte[32];
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(0, 8), part0);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(8, 8), part1);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(16, 8), part2);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(24, 8), part3);
        return Unsafe.As<byte[], T>(ref bytes);
    }

    private static ulong EncodeByteArray8(byte[] value)
    {
        if (value.Length != 8)
        {
            throw new ArgumentException("Generic LibraDex byte[] values routed to 8-byte scalars must be exactly 8 bytes.", nameof(value));
        }

        return BinaryPrimitives.ReadUInt64BigEndian(value);
    }
}
