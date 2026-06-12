namespace LibraDex;

/// <summary>
/// Encodes developer-facing variable-length keys into LibraDex's internal ordered key contract.<br/>
/// The first byte is reserved as a sentinel so null, empty, and non-empty keys remain distinct and sort before ordinary payload-bearing keys.<br/>
/// </summary>
internal static class LibraDexVarLenKeyCodec
{
    internal const byte NullMarker = 0x00;
    internal const byte EmptyMarker = 0x01;
    internal const byte ValueMarker = 0x02;

    internal static int GetMaxLogicalLength(int maxPhysicalLength)
    {
        return Math.Max(0, maxPhysicalLength - 1);
    }

    internal static byte[] Encode(ReadOnlySpan<byte> key, int maxPhysicalLength, string paramName)
    {
        if (key.Length > GetMaxLogicalLength(maxPhysicalLength))
        {
            throw new ArgumentOutOfRangeException(paramName, key.Length, $"Variable-length key payload length must be from 0 to {GetMaxLogicalLength(maxPhysicalLength)} bytes.");
        }

        if (key.Length == 0)
        {
            return new[] { EmptyMarker };
        }

        byte[] encoded = new byte[key.Length + 1];
        encoded[0] = ValueMarker;
        key.CopyTo(encoded.AsSpan(1));
        return encoded;
    }

    internal static byte[] Encode(byte[]? key, int maxPhysicalLength, string paramName)
    {
        return key is null
            ? EncodeNull(maxPhysicalLength, paramName)
            : Encode(key.AsSpan(), maxPhysicalLength, paramName);
    }

    internal static byte[] EncodeNull(int maxPhysicalLength, string paramName)
    {
        if (maxPhysicalLength < 1)
        {
            throw new ArgumentOutOfRangeException(paramName, maxPhysicalLength, "Variable-length key storage must reserve at least one sentinel byte.");
        }

        return new[] { NullMarker };
    }

    internal static ReadOnlySpan<byte> DecodePayloadSpan(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length == 0)
        {
            throw new InvalidDataException("Encoded variable-length keys must contain a sentinel byte.");
        }

        return encoded[0] switch
        {
            NullMarker => ReadOnlySpan<byte>.Empty,
            EmptyMarker => ReadOnlySpan<byte>.Empty,
            ValueMarker => encoded[1..],
            _ => throw new InvalidDataException("Encoded variable-length key sentinel is not recognized.")
        };
    }

    internal static bool IsNull(ReadOnlySpan<byte> encoded)
    {
        return encoded.Length > 0 && encoded[0] == NullMarker;
    }

    internal static byte[]? DecodeToArrayOrNull(ReadOnlySpan<byte> encoded)
    {
        if (IsNull(encoded))
        {
            return null;
        }

        return DecodePayloadSpan(encoded).ToArray();
    }
}
