namespace LibraDex;

/// <summary>
/// Identifies one of the five canonical hexadecimal segments in a GUID.<br/>
/// Segment order and width match the familiar `8-4-4-4-12` `Guid.ToString("D")` representation, independent of .NET's mixed-endian physical GUID byte layout.<br/>
/// </summary>
public enum GuidSegment
{
    /// <summary>The first eight canonical hexadecimal digits.<br/></summary>
    First = 0,

    /// <summary>The second four canonical hexadecimal digits.<br/></summary>
    Second = 1,

    /// <summary>The third four canonical hexadecimal digits.<br/></summary>
    Third = 2,

    /// <summary>The fourth four canonical hexadecimal digits.<br/></summary>
    Fourth = 3,

    /// <summary>The fifth twelve canonical hexadecimal digits.<br/></summary>
    Fifth = 4
}

/// <summary>
/// Selects how one canonical GUID nibble slice should be interpreted by the following comparison operator.<br/>
/// The selector records projection intent in the condition while execution continues to compare compact stored GUID bytes without materializing a GUID string.<br/>
/// </summary>
public sealed class LibraDexGuidSliceSelector
{
    private readonly LibraDexGuidConditionOperator owner;
    private readonly int startNibble;
    private readonly int nibbleCount;

    internal LibraDexGuidSliceSelector(LibraDexGuidConditionOperator owner, int startNibble, int nibbleCount)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ValidateBounds(startNibble, nibbleCount);
        this.owner = owner;
        this.startNibble = startNibble;
        this.nibbleCount = nibbleCount;
    }

    /// <summary>
    /// Interprets the selected canonical GUID slice as case-insensitive hexadecimal text.<br/>
    /// Comparison parses the supplied text once per materialization and does not call `Guid.ToString` for indexed candidates.<br/>
    /// </summary>
    public LibraDexGuidStringSliceConditionOperator AsString
        => new(owner, startNibble, nibbleCount);

    /// <summary>
    /// Interprets an even-width canonical GUID slice as bytes written in the same left-to-right order as its visible hexadecimal digits.<br/>
    /// This projection is canonical-text byte order, not `Guid.TryWriteBytes` order.<br/>
    /// </summary>
    public LibraDexGuidBinarySliceConditionOperator AsBinary
        => new(owner, startNibble, nibbleCount);

    /// <summary>Interprets the selected canonical GUID slice as an unsigned 8-bit hexadecimal magnitude.<br/></summary>
    public LibraDexGuidScalarSliceConditionOperator<byte> AsByte => Scalar<byte>();

    /// <summary>Interprets the selected canonical GUID slice as a signed 8-bit two's-complement value.<br/></summary>
    public LibraDexGuidScalarSliceConditionOperator<sbyte> AsSByte => Scalar<sbyte>();

    /// <summary>Interprets the selected canonical GUID slice as a signed 16-bit two's-complement value.<br/></summary>
    public LibraDexGuidScalarSliceConditionOperator<short> AsInt16 => Scalar<short>();

    /// <summary>Interprets the selected canonical GUID slice as an unsigned 16-bit hexadecimal magnitude.<br/></summary>
    public LibraDexGuidScalarSliceConditionOperator<ushort> AsUInt16 => Scalar<ushort>();

    /// <summary>Interprets the selected canonical GUID slice as a signed 32-bit two's-complement value.<br/></summary>
    public LibraDexGuidScalarSliceConditionOperator<int> AsInt32 => Scalar<int>();

    /// <summary>Interprets the selected canonical GUID slice as an unsigned 32-bit hexadecimal magnitude.<br/></summary>
    public LibraDexGuidScalarSliceConditionOperator<uint> AsUInt32 => Scalar<uint>();

    /// <summary>Interprets the selected canonical GUID slice as a signed 64-bit two's-complement value.<br/></summary>
    public LibraDexGuidScalarSliceConditionOperator<long> AsInt64 => Scalar<long>();

    /// <summary>Interprets the selected canonical GUID slice as an unsigned 64-bit hexadecimal magnitude.<br/></summary>
    public LibraDexGuidScalarSliceConditionOperator<ulong> AsUInt64 => Scalar<ulong>();

    /// <summary>Interprets the selected canonical GUID slice as a signed 128-bit hexadecimal magnitude.<br/></summary>
    public LibraDexGuidScalarSliceConditionOperator<Int128> AsInt128 => Scalar<Int128>();

    /// <summary>Interprets the selected canonical GUID slice as an unsigned 128-bit hexadecimal magnitude.<br/></summary>
    public LibraDexGuidScalarSliceConditionOperator<UInt128> AsUInt128 => Scalar<UInt128>();

    private LibraDexGuidScalarSliceConditionOperator<TScalar> Scalar<TScalar>()
        where TScalar : struct
        => new(owner, startNibble, nibbleCount);

    internal static LibraDexGuidSliceSelector Create(LibraDexGuidConditionOperator owner, GuidSegment segment)
    {
        return segment switch
        {
            GuidSegment.First => new LibraDexGuidSliceSelector(owner, 0, 8),
            GuidSegment.Second => new LibraDexGuidSliceSelector(owner, 8, 4),
            GuidSegment.Third => new LibraDexGuidSliceSelector(owner, 12, 4),
            GuidSegment.Fourth => new LibraDexGuidSliceSelector(owner, 16, 4),
            GuidSegment.Fifth => new LibraDexGuidSliceSelector(owner, 20, 12),
            _ => throw new ArgumentOutOfRangeException(nameof(segment), segment, "Unknown canonical GUID segment.")
        };
    }

    private static void ValidateBounds(int startNibble, int nibbleCount)
    {
        if (startNibble is < 0 or > 31)
        {
            throw new ArgumentOutOfRangeException(nameof(startNibble), startNibble, "GUID slice start nibble must be 0 through 31.");
        }

        if (nibbleCount <= 0 || startNibble + nibbleCount > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(nibbleCount), nibbleCount, "GUID slice must contain at least one nibble and remain within the 32 canonical GUID nibbles.");
        }
    }
}

/// <summary>
/// Captures exact hexadecimal-text equality for one canonical GUID nibble slice.<br/>
/// Hexadecimal letter case is ignored because both the slice and operand are compared as nibble values.<br/>
/// </summary>
public sealed class LibraDexGuidStringSliceConditionOperator
{
    private readonly LibraDexGuidConditionOperator owner;
    private readonly int startNibble;
    private readonly int nibbleCount;

    internal LibraDexGuidStringSliceConditionOperator(LibraDexGuidConditionOperator owner, int startNibble, int nibbleCount)
    {
        this.owner = owner;
        this.startNibble = startNibble;
        this.nibbleCount = nibbleCount;
    }

    /// <summary>
    /// Captures equality against the selected canonical GUID slice.<br/>
    /// The supplied value must contain exactly the selected number of hexadecimal digits after ordinary GUID punctuation is removed.<br/>
    /// </summary>
    /// <param name="value">The hexadecimal slice value to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd EqualTo(string value)
        => owner.AddGuidPattern(
            LibraDexConditionOperatorKind.MatchesPattern,
            LibraDexGuidPatternPredicate.CreateSlice(startNibble, nibbleCount, value));

    /// <summary>
    /// Captures deferred equality against the selected canonical GUID slice.<br/>
    /// The factory is evaluated once per condition materialization so reusable conditions can use current values without rebuilding their shape.<br/>
    /// </summary>
    /// <param name="value">A factory returning the current hexadecimal slice value.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd EqualTo(Func<string> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return owner.AddGuidPattern(
            LibraDexConditionOperatorKind.MatchesPattern,
            LibraDexConditionOperand.Deferred(() => LibraDexGuidPatternPredicate.CreateSlice(startNibble, nibbleCount, value())));
    }
}

/// <summary>
/// Captures exact byte equality for one even-width canonical GUID nibble slice.<br/>
/// Each supplied byte represents two adjacent visible hexadecimal digits, keeping physical .NET GUID byte order hidden from the developer-facing condition.<br/>
/// </summary>
public sealed class LibraDexGuidBinarySliceConditionOperator
{
    private readonly LibraDexGuidConditionOperator owner;
    private readonly int startNibble;
    private readonly int nibbleCount;

    internal LibraDexGuidBinarySliceConditionOperator(LibraDexGuidConditionOperator owner, int startNibble, int nibbleCount)
    {
        if ((nibbleCount & 1) != 0)
        {
            throw new InvalidOperationException("A GUID slice must contain an even number of nibbles before it can be projected as binary bytes.");
        }

        this.owner = owner;
        this.startNibble = startNibble;
        this.nibbleCount = nibbleCount;
    }

    /// <summary>
    /// Captures equality against canonical-order bytes for the selected GUID slice.<br/>
    /// The byte array length must equal half the selected nibble count.<br/>
    /// </summary>
    /// <param name="value">The canonical-order byte sequence to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd EqualTo(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return owner.AddGuidPattern(
            LibraDexConditionOperatorKind.MatchesPattern,
            LibraDexGuidPatternPredicate.CreateSlice(startNibble, nibbleCount, ExpandBytes(value, nibbleCount)));
    }

    /// <summary>
    /// Captures deferred equality against canonical-order bytes for the selected GUID slice.<br/>
    /// The factory is evaluated once per condition materialization and its returned bytes are expanded to canonical nibbles once.<br/>
    /// </summary>
    /// <param name="value">A factory returning the current canonical-order byte sequence.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd EqualTo(Func<byte[]> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return owner.AddGuidPattern(
            LibraDexConditionOperatorKind.MatchesPattern,
            LibraDexConditionOperand.Deferred(() => LibraDexGuidPatternPredicate.CreateSlice(startNibble, nibbleCount, ExpandBytes(value(), nibbleCount))));
    }

    private static byte[] ExpandBytes(byte[] value, int nibbleCount)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length * 2 != nibbleCount)
        {
            throw new FormatException($"GUID binary slice value must contain exactly {nibbleCount / 2} bytes.");
        }

        byte[] nibbles = new byte[nibbleCount];
        for (int i = 0; i < value.Length; i++)
        {
            nibbles[i * 2] = (byte)(value[i] >> 4);
            nibbles[(i * 2) + 1] = (byte)(value[i] & 0xF);
        }

        return nibbles;
    }
}

/// <summary>
/// Captures exact integral equality for one canonical GUID nibble slice.<br/>
/// The selected digits are interpreted as one big-endian hexadecimal magnitude; signed negative operands use their ordinary two's-complement low bits when they fit the selected width.<br/>
/// </summary>
/// <typeparam name="TScalar">The supported integral CLR type accepted by this operator.</typeparam>
public sealed class LibraDexGuidScalarSliceConditionOperator<TScalar>
    where TScalar : struct
{
    private readonly LibraDexGuidConditionOperator owner;
    private readonly int startNibble;
    private readonly int nibbleCount;

    internal LibraDexGuidScalarSliceConditionOperator(LibraDexGuidConditionOperator owner, int startNibble, int nibbleCount)
    {
        ValidateScalarWidth(nibbleCount, GetScalarWidth());
        this.owner = owner;
        this.startNibble = startNibble;
        this.nibbleCount = nibbleCount;
    }

    /// <summary>
    /// Captures equality against the integral value represented by the selected canonical GUID digits.<br/>
    /// The value is converted to hexadecimal nibbles once while the condition is captured.<br/>
    /// </summary>
    /// <param name="value">The integral slice value to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd EqualTo(TScalar value)
        => owner.AddGuidPattern(
            LibraDexConditionOperatorKind.MatchesPattern,
            LibraDexGuidPatternPredicate.CreateSlice(startNibble, nibbleCount, ToNibbles(value, nibbleCount)));

    /// <summary>
    /// Captures deferred equality against the integral value represented by the selected canonical GUID digits.<br/>
    /// The factory is evaluated and converted once per condition materialization, preserving reusable condition shape without per-key scalar conversion.<br/>
    /// </summary>
    /// <param name="value">A factory returning the current integral slice value.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd EqualTo(Func<TScalar> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return owner.AddGuidPattern(
            LibraDexConditionOperatorKind.MatchesPattern,
            LibraDexConditionOperand.Deferred(() => LibraDexGuidPatternPredicate.CreateSlice(startNibble, nibbleCount, ToNibbles(value(), nibbleCount))));
    }

    private static int GetScalarWidth()
    {
        Type type = typeof(TScalar);
        if (type == typeof(byte) || type == typeof(sbyte)) return 8;
        if (type == typeof(short) || type == typeof(ushort)) return 16;
        if (type == typeof(int) || type == typeof(uint)) return 32;
        if (type == typeof(long) || type == typeof(ulong)) return 64;
        if (type == typeof(Int128) || type == typeof(UInt128)) return 128;
        throw new NotSupportedException($"GUID scalar slices do not support {type.FullName}.");
    }

    private static void ValidateScalarWidth(int nibbleCount, int scalarWidth)
    {
        if (nibbleCount * 4 > scalarWidth)
        {
            throw new InvalidOperationException($"A {nibbleCount}-nibble GUID slice cannot be represented by the selected {scalarWidth}-bit scalar type.");
        }
    }

    private static byte[] ToNibbles(TScalar value, int nibbleCount)
    {
        object boxed = value;
        UInt128 raw;
        bool negative;
        switch (boxed)
        {
            case byte v: raw = v; negative = false; break;
            case sbyte v: raw = unchecked((byte)v); negative = v < 0; break;
            case short v: raw = unchecked((ushort)v); negative = v < 0; break;
            case ushort v: raw = v; negative = false; break;
            case int v: raw = unchecked((uint)v); negative = v < 0; break;
            case uint v: raw = v; negative = false; break;
            case long v: raw = unchecked((ulong)v); negative = v < 0; break;
            case ulong v: raw = v; negative = false; break;
            case Int128 v: raw = unchecked((UInt128)v); negative = v < 0; break;
            case UInt128 v: raw = v; negative = false; break;
            default: throw new NotSupportedException($"GUID scalar slices do not support {typeof(TScalar).FullName}.");
        }

        int bitCount = nibbleCount * 4;
        UInt128 mask = bitCount == 128 ? UInt128.MaxValue : (UInt128.One << bitCount) - UInt128.One;
        if (!negative && (raw & ~mask) != 0)
        {
            throw new OverflowException($"GUID slice value does not fit in {nibbleCount} hexadecimal nibbles.");
        }

        if (negative)
        {
            Int128 signedValue = boxed switch
            {
                sbyte v => v,
                short v => v,
                int v => v,
                long v => v,
                Int128 v => v,
                _ => throw new InvalidOperationException("The negative GUID slice value is not a supported signed integral type.")
            };
            if (bitCount < 128)
            {
                Int128 minimum = -(Int128.One << (bitCount - 1));
                if (signedValue < minimum)
                {
                    throw new OverflowException($"Negative GUID slice value does not fit in {nibbleCount} hexadecimal nibbles.");
                }
            }

            int scalarWidth = GetScalarWidth();
            UInt128 scalarMask = scalarWidth == 128 ? UInt128.MaxValue : (UInt128.One << scalarWidth) - UInt128.One;
            if ((raw & (scalarMask & ~mask)) != (scalarMask & ~mask))
            {
                throw new OverflowException($"Negative GUID slice value does not fit in {nibbleCount} hexadecimal nibbles.");
            }
        }

        raw &= mask;
        byte[] nibbles = new byte[nibbleCount];
        for (int i = nibbleCount - 1; i >= 0; i--)
        {
            nibbles[i] = (byte)(raw & 0xF);
            raw >>= 4;
        }

        return nibbles;
    }
}
