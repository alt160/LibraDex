using System.IO;

namespace LibraDex;

/// <summary>
/// Encodes CLR Decimal values into one fixed 16-byte key whose unsigned lane order matches <see cref="decimal.Compare(decimal, decimal)"/>.<br/>
/// The codec canonicalizes equivalent scales, aligns every non-zero coefficient to a 29-digit significand, and keeps zero between mirrored negative and positive magnitude ranks.<br/>
/// Encoding, decoding, and adjacent-value movement use only value types and stack storage so ordinary index operations do not allocate.<br/>
/// </summary>
internal static class LibraDexOrderedDecimalCodec
{
    private const int SignificandBits = 97;
    private static readonly UInt128[] PowersOfTen = CreatePowersOfTen();
    private static readonly UInt128 MaxCoefficient = (UInt128.One << 96) - 1;
    private static readonly UInt128 SignificandMask = (UInt128.One << SignificandBits) - 1;
    private static readonly UInt128 ZeroCode = UInt128.One << 127;
    private static readonly UInt128 MinimumNormalizedSignificand = PowersOfTen[28];
    private static readonly UInt128 MaximumNormalizedSignificand = PowersOfTen[29];

    /// <summary>
    /// Encodes one Decimal into two unsigned 64-bit lanes in natural LibraDex scalar-16 comparison order.<br/>
    /// Numerically equal values with different CLR scales produce the same lanes, including every representation of zero.<br/>
    /// </summary>
    /// <param name="value">The Decimal value to encode.<br/></param>
    /// <param name="high">The most-significant ordered lane.<br/></param>
    /// <param name="low">The least-significant ordered lane.<br/></param>
    internal static void Encode(decimal value, out ulong high, out ulong low)
    {
        GetParts(value, out UInt128 coefficient, out int scale, out bool negative);
        if (coefficient == 0)
        {
            high = (ulong)(ZeroCode >> 64);
            low = (ulong)ZeroCode;
            return;
        }

        while (scale > 0 && coefficient % 10 == 0)
        {
            coefficient /= 10;
            scale--;
        }

        int digits = GetDigitCount(coefficient);
        UInt128 significand = coefficient * PowersOfTen[29 - digits];

        int exponent = digits - 1 - scale;
        UInt128 rank = ((UInt128)(exponent + 28) << SignificandBits) | significand;
        UInt128 encoded = negative ? ZeroCode - rank : ZeroCode + rank;
        high = (ulong)(encoded >> 64);
        low = (ulong)encoded;
    }

    /// <summary>
    /// Decodes two ordered scalar-16 lanes into their canonical numeric Decimal value.<br/>
    /// Persisted lane combinations that cannot have been emitted by <see cref="Encode(decimal, out ulong, out ulong)"/> are rejected as corrupt.<br/>
    /// </summary>
    /// <param name="high">The most-significant ordered lane.<br/></param>
    /// <param name="low">The least-significant ordered lane.<br/></param>
    /// <returns>The decoded Decimal value.<br/></returns>
    internal static decimal Decode(ulong high, ulong low)
    {
        UInt128 encoded = ((UInt128)high << 64) | low;
        if (encoded == ZeroCode)
            return decimal.Zero;

        bool negative = encoded < ZeroCode;
        UInt128 rank = negative ? ZeroCode - encoded : encoded - ZeroCode;
        int exponentIndex = (int)(rank >> SignificandBits);
        UInt128 coefficient = rank & SignificandMask;
        if ((uint)exponentIndex > 56 ||
            coefficient < MinimumNormalizedSignificand ||
            coefficient >= MaximumNormalizedSignificand)
        {
            throw new InvalidDataException("The scalar-16 Decimal key contains an invalid exponent or significand.");
        }

        int scale = 56 - exponentIndex;
        if (scale > 28)
        {
            int trim = scale - 28;
            UInt128 divisor = PowersOfTen[trim];
            if (coefficient % divisor != 0)
                throw new InvalidDataException("The scalar-16 Decimal key contains a non-canonical low-exponent significand.");

            coefficient /= divisor;
            scale = 28;
        }

        if (coefficient > MaxCoefficient)
        {
            if (scale == 0 || coefficient % 10 != 0)
                throw new InvalidDataException("The scalar-16 Decimal key cannot be represented by the CLR Decimal domain.");

            coefficient /= 10;
            scale--;
        }

        return CreateDecimal(coefficient, scale, negative);
    }

    /// <summary>
    /// Finds the immediately preceding numeric value in the finite CLR Decimal domain.<br/>
    /// Scale is expanded only while the 96-bit coefficient can still represent a closer value, avoiding the false fixed-quantum assumption that Decimal always advances by `1e-28`.<br/>
    /// </summary>
    /// <param name="value">The current Decimal value.<br/></param>
    /// <param name="previous">The immediately preceding Decimal when one exists.<br/></param>
    /// <returns><see langword="true"/> unless <paramref name="value"/> is <see cref="decimal.MinValue"/>.<br/></returns>
    internal static bool TryGetPrevious(decimal value, out decimal previous)
    {
        if (value == decimal.MinValue)
        {
            previous = default;
            return false;
        }

        if (value == 0)
        {
            previous = new decimal(1, 0, 0, isNegative: true, scale: 28);
            return true;
        }

        bool negative = value < 0;
        decimal magnitude = negative ? decimal.Negate(value) : value;
        previous = negative
            ? decimal.Negate(NextPositiveMagnitude(magnitude))
            : PreviousPositiveMagnitude(magnitude);
        return true;
    }

    /// <summary>
    /// Finds the immediately following numeric value in the finite CLR Decimal domain.<br/>
    /// This is used by strict greater-than ranges so the inclusive scalar reader begins at the closest representable Decimal rather than an arbitrary encoded-lane hole.<br/>
    /// </summary>
    /// <param name="value">The current Decimal value.<br/></param>
    /// <param name="next">The immediately following Decimal when one exists.<br/></param>
    /// <returns><see langword="true"/> unless <paramref name="value"/> is <see cref="decimal.MaxValue"/>.<br/></returns>
    internal static bool TryGetNext(decimal value, out decimal next)
    {
        if (value == decimal.MaxValue)
        {
            next = default;
            return false;
        }

        if (value == 0)
        {
            next = new decimal(1, 0, 0, isNegative: false, scale: 28);
            return true;
        }

        bool negative = value < 0;
        decimal magnitude = negative ? decimal.Negate(value) : value;
        next = negative
            ? decimal.Negate(PreviousPositiveMagnitude(magnitude))
            : NextPositiveMagnitude(magnitude);
        return true;
    }

    private static decimal NextPositiveMagnitude(decimal value)
    {
        GetParts(value, out UInt128 coefficient, out int scale, out _);
        while (scale < 28 && coefficient <= (MaxCoefficient - 1) / 10)
        {
            coefficient *= 10;
            scale++;
        }

        coefficient++;
        return CreateDecimal(coefficient, scale, negative: false);
    }

    private static decimal PreviousPositiveMagnitude(decimal value)
    {
        GetParts(value, out UInt128 coefficient, out int scale, out _);
        while (scale < 28 && coefficient <= MaxCoefficient / 10)
        {
            coefficient *= 10;
            scale++;
        }

        coefficient--;
        return CreateDecimal(coefficient, scale, negative: false);
    }

    private static void GetParts(decimal value, out UInt128 coefficient, out int scale, out bool negative)
    {
        Span<int> bits = stackalloc int[4];
        _ = decimal.GetBits(value, bits);
        coefficient = ((UInt128)(uint)bits[2] << 64) |
            ((UInt128)(uint)bits[1] << 32) |
            (uint)bits[0];
        scale = (bits[3] >> 16) & 0xFF;
        negative = bits[3] < 0;
    }

    private static decimal CreateDecimal(UInt128 coefficient, int scale, bool negative)
    {
        return new decimal(
            unchecked((int)(uint)coefficient),
            unchecked((int)(uint)(coefficient >> 32)),
            unchecked((int)(uint)(coefficient >> 64)),
            negative,
            checked((byte)scale));
    }

    private static int GetDigitCount(UInt128 value)
    {
        int lower = 1;
        int upper = 29;
        while (lower < upper)
        {
            int middle = (lower + upper) >> 1;
            if (value < PowersOfTen[middle])
                upper = middle;
            else
                lower = middle + 1;
        }

        return lower;
    }

    private static UInt128[] CreatePowersOfTen()
    {
        UInt128[] powers = new UInt128[30];
        UInt128 value = 1;
        for (int i = 0; i < powers.Length; i++)
        {
            powers[i] = value;
            value *= 10;
        }

        return powers;
    }
}
