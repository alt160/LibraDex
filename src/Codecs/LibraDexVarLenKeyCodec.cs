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
        byte[] encoded = GC.AllocateUninitializedArray<byte>(GetEncodedLength(key, maxPhysicalLength, paramName));
        Encode(key, maxPhysicalLength, paramName, encoded);
        return encoded;
    }

    /// <summary>
    /// Returns the exact physical byte count required for one non-null variable-length key.<br/>
    /// The result includes the one-byte key-state marker and validates the configured maximum before a caller reserves arena storage.<br/>
    /// </summary>
    /// <param name="key">Developer-facing key payload.<br/></param>
    /// <param name="maxPhysicalLength">Maximum encoded key length accepted by the target shape.<br/></param>
    /// <param name="paramName">Parameter name used by validation failures.<br/></param>
    /// <returns>The exact encoded byte count.<br/></returns>
    internal static int GetEncodedLength(ReadOnlySpan<byte> key, int maxPhysicalLength, string paramName)
    {
        if (key.Length > GetMaxLogicalLength(maxPhysicalLength))
        {
            throw new ArgumentOutOfRangeException(paramName, key.Length, $"Variable-length key payload length must be from 0 to {GetMaxLogicalLength(maxPhysicalLength)} bytes.");
        }
        return checked(key.Length + 1);
    }

    /// <summary>
    /// Encodes one non-null variable-length key directly into caller-owned storage.<br/>
    /// This avoids a temporary per-key array when a bulk builder already owns a compact byte arena.<br/>
    /// </summary>
    /// <param name="key">Developer-facing key payload.<br/></param>
    /// <param name="maxPhysicalLength">Maximum encoded key length accepted by the target shape.<br/></param>
    /// <param name="paramName">Parameter name used by validation failures.<br/></param>
    /// <param name="destination">Destination whose length must equal the validated encoded length.<br/></param>
    internal static void Encode(ReadOnlySpan<byte> key, int maxPhysicalLength, string paramName, Span<byte> destination)
    {
        int required = GetEncodedLength(key, maxPhysicalLength, paramName);
        if (destination.Length != required)
            throw new ArgumentException($"Encoded variable-key destination length must be exactly {required:N0} bytes.", nameof(destination));
        destination[0] = key.Length == 0 ? EmptyMarker : ValueMarker;
        key.CopyTo(destination[1..]);
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
