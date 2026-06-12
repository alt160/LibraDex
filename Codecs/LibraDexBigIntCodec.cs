using System.Buffers.Binary;
using System.Numerics;

namespace LibraDex;

internal enum LibraDexBigIntKeyStorage
{
    FixedWidth = 0,
    VariableWidth = 1
}

internal static class LibraDexBigIntCodec
{
    internal const int MaxMagnitudeBytes = 512;

    internal static int GetFixedEncodedLength(int maxBytes)
    {
        ValidateMaxBytes(maxBytes, nameof(maxBytes));
        return checked(maxBytes + 3);
    }

    internal static void ValidateMaxBytes(int maxBytes, string parameterName)
    {
        if (maxBytes < 1 || maxBytes > MaxMagnitudeBytes)
        {
            throw new ArgumentOutOfRangeException(parameterName, maxBytes, $"BigInt key maxBytes must be from 1 to {MaxMagnitudeBytes} bytes.");
        }
    }

    internal static byte[] Encode(BigInteger value, int maxBytes, LibraDexBigIntKeyStorage storage)
    {
        ValidateMaxBytes(maxBytes, nameof(maxBytes));
        if (value.IsZero)
        {
            if (storage == LibraDexBigIntKeyStorage.VariableWidth)
            {
                return new byte[] { 0x01 };
            }

            byte[] zero = new byte[GetFixedEncodedLength(maxBytes)];
            zero[0] = 0x01;
            return zero;
        }

        bool negative = value.Sign < 0;
        BigInteger magnitude = BigInteger.Abs(value);
        byte[] magnitudeBytes = magnitude.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (magnitudeBytes.Length > maxBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, $"The BigInt magnitude needs {magnitudeBytes.Length} bytes, which exceeds this index's maxBytes value of {maxBytes}.");
        }

        int encodedLength = storage == LibraDexBigIntKeyStorage.FixedWidth
            ? GetFixedEncodedLength(maxBytes)
            : checked(magnitudeBytes.Length + 3);
        byte[] encoded = new byte[encodedLength];
        encoded[0] = negative ? (byte)0x00 : (byte)0x02;
        ushort length = checked((ushort)magnitudeBytes.Length);
        ushort storedLength = negative ? unchecked((ushort)~length) : length;
        BinaryPrimitives.WriteUInt16BigEndian(encoded.AsSpan(1, 2), storedLength);

        int magnitudeOffset = storage == LibraDexBigIntKeyStorage.FixedWidth
            ? encoded.Length - magnitudeBytes.Length
            : 3;
        magnitudeBytes.CopyTo(encoded.AsSpan(magnitudeOffset));
        if (negative)
        {
            for (int i = 3; i < encoded.Length; i++)
            {
                encoded[i] = (byte)~encoded[i];
            }
        }

        return encoded;
    }

    internal static BigInteger Decode(ReadOnlySpan<byte> encoded, int maxBytes, LibraDexBigIntKeyStorage storage)
    {
        ValidateMaxBytes(maxBytes, nameof(maxBytes));
        if (encoded.Length == 0)
        {
            throw new ArgumentException("BigInt encoded keys cannot be empty.", nameof(encoded));
        }

        if (encoded[0] == 0x01)
        {
            return BigInteger.Zero;
        }

        if (encoded.Length < 3 || (encoded[0] != 0x00 && encoded[0] != 0x02))
        {
            throw new ArgumentException("The encoded BigInt key is not in LibraDex sortable BigInt format.", nameof(encoded));
        }

        bool negative = encoded[0] == 0x00;
        ushort storedLength = BinaryPrimitives.ReadUInt16BigEndian(encoded.Slice(1, 2));
        int magnitudeLength = negative ? unchecked((ushort)~storedLength) : storedLength;
        if (magnitudeLength < 1 || magnitudeLength > maxBytes)
        {
            throw new ArgumentException("The encoded BigInt key length marker is outside this index's maxBytes contract.", nameof(encoded));
        }

        if (storage == LibraDexBigIntKeyStorage.FixedWidth && encoded.Length != GetFixedEncodedLength(maxBytes))
        {
            throw new ArgumentException("The encoded fixed-width BigInt key length does not match this index's maxBytes contract.", nameof(encoded));
        }

        if (storage == LibraDexBigIntKeyStorage.VariableWidth && encoded.Length != magnitudeLength + 3)
        {
            throw new ArgumentException("The encoded variable-width BigInt key length does not match its length marker.", nameof(encoded));
        }

        int magnitudeOffset = storage == LibraDexBigIntKeyStorage.FixedWidth
            ? encoded.Length - magnitudeLength
            : 3;
        byte[] magnitudeBytes = encoded.Slice(magnitudeOffset, magnitudeLength).ToArray();
        if (negative)
        {
            for (int i = 0; i < magnitudeBytes.Length; i++)
            {
                magnitudeBytes[i] = (byte)~magnitudeBytes[i];
            }
        }

        BigInteger value = new(magnitudeBytes, isUnsigned: true, isBigEndian: true);
        return negative ? -value : value;
    }
}
