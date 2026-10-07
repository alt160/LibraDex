namespace LibraDex;

/// <summary>
/// Encodes CLR binary floating-point values into unsigned lanes whose byte order matches numeric order.<br/>
/// Every NaN payload maps to one reserved sentinel, and positive and negative zero map to one canonical zero key.<br/>
/// </summary>
internal static class LibraDexOrderedFloatingCodec
{
    private const uint SingleSignMask = 0x80000000U;
    private const uint SingleMagnitudeMask = 0x7FFFFFFFU;
    private const ulong DoubleSignMask = 0x8000000000000000UL;
    private const ulong DoubleMagnitudeMask = 0x7FFFFFFFFFFFFFFFUL;

    /// <summary>
    /// Encodes one <see cref="float"/> into the low 32 bits of an ordered 8-byte scalar lane.<br/>
    /// NaN is reserved at zero; negative infinity through positive infinity occupy the ordinary numeric interval above it.<br/>
    /// </summary>
    internal static ulong Encode(float value)
    {
        if (float.IsNaN(value))
            return 0;

        uint bits = BitConverter.SingleToUInt32Bits(value);
        if ((bits & SingleMagnitudeMask) == 0)
            return SingleSignMask;

        return (bits & SingleSignMask) != 0 ? ~bits : bits ^ SingleSignMask;
    }

    /// <summary>
    /// Encodes one <see cref="double"/> into an ordered 8-byte scalar lane.<br/>
    /// NaN is reserved at zero; negative infinity through positive infinity occupy the ordinary numeric interval above it.<br/>
    /// </summary>
    internal static ulong Encode(double value)
    {
        if (double.IsNaN(value))
            return 0;

        ulong bits = BitConverter.DoubleToUInt64Bits(value);
        if ((bits & DoubleMagnitudeMask) == 0)
            return DoubleSignMask;

        return (bits & DoubleSignMask) != 0 ? ~bits : bits ^ DoubleSignMask;
    }

    /// <summary>
    /// Decodes one ordered scalar lane into a canonical <see cref="float"/>.<br/>
    /// The reserved zero sentinel returns the CLR canonical NaN, and the zero midpoint returns positive zero.<br/>
    /// </summary>
    internal static float DecodeSingle(ulong encoded)
    {
        if (encoded == 0)
            return float.NaN;
        if (encoded > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(encoded), "Ordered Single encodings must fit in the low 32 bits of the scalar lane.");

        uint ordered = (uint)encoded;
        if (ordered == SingleSignMask)
            return 0f;

        uint bits = (ordered & SingleSignMask) != 0 ? ordered ^ SingleSignMask : ~ordered;
        return BitConverter.UInt32BitsToSingle(bits);
    }

    /// <summary>
    /// Decodes one ordered scalar lane into a canonical <see cref="double"/>.<br/>
    /// The reserved zero sentinel returns the CLR canonical NaN, and the zero midpoint returns positive zero.<br/>
    /// </summary>
    internal static double DecodeDouble(ulong encoded)
    {
        if (encoded == 0)
            return double.NaN;
        if (encoded == DoubleSignMask)
            return 0d;

        ulong bits = (encoded & DoubleSignMask) != 0 ? encoded ^ DoubleSignMask : ~encoded;
        return BitConverter.UInt64BitsToDouble(bits);
    }
}
