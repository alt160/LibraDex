using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace LibraDex;

/// <summary>
/// Groups explicit binary-to-value coercion choices by value family.<br/>
/// Separate family enums keep numeric, text, GUID, and temporal interpretation choices from becoming one unrelated IntelliSense list.<br/>
/// </summary>
public static class Coercion
{
    /// <summary>
    /// Selects how numeric values are read from developer-owned binary keys or key slices.<br/>
    /// Typed numeric indexes already imply LibraDex canonical encoding and do not require this choice.<br/>
    /// </summary>
    public enum Numeric
    {
        /// <summary>
        /// Reads the ordinary fixed-width little-endian representation used by the existing binary-slice numeric grammar.<br/>
        /// This does not assume that the bytes were produced by a LibraDex scalar codec.<br/>
        /// </summary>
        DotNet = 0,

        /// <summary>
        /// Reads LibraDex's fixed eight-byte canonical ordered numeric representation.<br/>
        /// Select this only when LibraDex encoded the value or the developer deliberately used <see cref="LibraDexCanonical.Numeric"/> to produce matching bytes.<br/>
        /// </summary>
        LibraDex = 1
    }

    /// <summary>
    /// Selects how malformed encoded text is handled when a developer-owned binary key or key slice is interpreted as text.<br/>
    /// The choice affects only malformed input; valid encoded text retains the byte-native ordinal comparison path.<br/>
    /// </summary>
    public enum Text
    {
        /// <summary>
        /// Rejects malformed encoded bytes so the containing key does not satisfy the text predicate.<br/>
        /// This preserves LibraDex's original binary string-slice behavior.<br/>
        /// </summary>
        Strict = 0,

        /// <summary>
        /// Uses the selected .NET encoding's replacement fallback for malformed bytes.<br/>
        /// This matches <see cref="System.Text.Encoding.UTF8"/>, <see cref="System.Text.Encoding.Unicode"/>, <see cref="System.Text.Encoding.UTF32"/>, and <see cref="System.Text.Encoding.ASCII"/> default decoding behavior.<br/>
        /// </summary>
        DotNetReplacement = 1
    }

    /// <summary>
    /// Selects the physical representation of a <see cref="System.DateTime"/> stored inside developer-owned binary data.<br/>
    /// Typed DateTime indexes already carry this interpretation and do not require an explicit coercion.<br/>
    /// </summary>
    public enum DateTime
    {
        /// <summary>Reads an eight-byte big-endian LibraDex calendar SDT value.<br/></summary>
        LibraDexCalendarSdt = 0,

        /// <summary>Reads an eight-byte big-endian LibraDex precision SDT value.<br/></summary>
        LibraDexPrecisionSdt = 1,

        /// <summary>Reads an eight-byte little-endian signed .NET tick value.<br/></summary>
        DotNetTicks = 2
    }

    /// <summary>
    /// Selects the physical representation of a <see cref="System.DateOnly"/> stored inside developer-owned binary data.<br/>
    /// </summary>
    public enum DateOnly
    {
        /// <summary>Reads an eight-byte big-endian LibraDex calendar SDT value containing date fields.<br/></summary>
        LibraDexCalendarSdt = 0,

        /// <summary>Reads an eight-byte big-endian LibraDex precision SDT value containing date fields.<br/></summary>
        LibraDexPrecisionSdt = 1,

        /// <summary>Reads a four-byte little-endian .NET <see cref="System.DateOnly.DayNumber"/> value.<br/></summary>
        DotNetDayNumber = 2
    }

    /// <summary>
    /// Selects the physical representation of a <see cref="System.TimeOnly"/> stored inside developer-owned binary data.<br/>
    /// </summary>
    public enum TimeOnly
    {
        /// <summary>Reads an eight-byte big-endian LibraDex calendar SDT value containing time fields.<br/></summary>
        LibraDexCalendarSdt = 0,

        /// <summary>Reads an eight-byte big-endian LibraDex precision SDT value containing time fields.<br/></summary>
        LibraDexPrecisionSdt = 1,

        /// <summary>Reads an eight-byte little-endian signed .NET tick value.<br/></summary>
        DotNetTicks = 2
    }

    /// <summary>
    /// Selects the physical representation of a <see cref="System.DateTimeOffset"/> stored inside developer-owned binary data.<br/>
    /// </summary>
    public enum DateTimeOffset
    {
        /// <summary>Reads an eight-byte big-endian LibraDex calendar SDT value representing the UTC instant.<br/></summary>
        LibraDexCalendarSdtUtc = 0,

        /// <summary>Reads an eight-byte big-endian LibraDex precision SDT value representing the UTC instant.<br/></summary>
        LibraDexPrecisionSdtUtc = 1,

        /// <summary>Reads local .NET ticks followed by offset ticks as two little-endian signed 64-bit values.<br/></summary>
        DotNetTicksAndOffset = 2
    }

    /// <summary>
    /// Selects the physical representation of a <see cref="System.TimeSpan"/> stored inside developer-owned binary data.<br/>
    /// </summary>
    public enum TimeSpan
    {
        /// <summary>Reads an eight-byte big-endian LibraDex ordered signed-tick scalar.<br/></summary>
        LibraDexOrderedTicks = 0,

        /// <summary>Reads an eight-byte little-endian signed .NET tick value.<br/></summary>
        DotNetTicks = 1
    }
}

/// <summary>
/// Exposes LibraDex's canonical ordered binary encodings for developer-owned compound or custom binary values.<br/>
/// These helpers are optional: typed LibraDex indexes apply their declared codec automatically, while raw binary indexes retain exactly the bytes supplied by the developer.<br/>
/// </summary>
public static class LibraDexCanonical
{
    /// <summary>
    /// Provides canonical ordered encoders and decoders for the numeric CLR types supported by LibraDex indexes.<br/>
    /// Fixed scalar values use the same eight- or sixteen-byte ordered lanes as typed indexes; BigInteger uses LibraDex's variable-width sortable representation.<br/>
    /// </summary>
    public static class Numeric
    {
        /// <summary>
        /// Encodes one supported numeric value into a newly allocated LibraDex canonical buffer.<br/>
        /// Use <see cref="Write{T}(T, Span{byte})"/> with caller-owned storage in allocation-sensitive paths.<br/>
        /// </summary>
        /// <typeparam name="T">A supported integral, floating-point, Decimal, or BigInteger value type.<br/></typeparam>
        /// <param name="value">The numeric value to encode.<br/></param>
        /// <returns>A caller-owned eight-byte canonical buffer.<br/></returns>
        public static byte[] Encode<T>(T value)
            where T : struct
        {
            Validate<T>();
            if (typeof(T) == typeof(BigInteger))
            {
                BigInteger big = (BigInteger)(object)value;
                int maxBytes = Math.Max(1, BigInteger.Abs(big).GetByteCount(isUnsigned: true));
                return LibraDexBigIntCodec.Encode(big, maxBytes, LibraDexBigIntKeyStorage.VariableWidth);
            }

            int width = IsScalar16<T>() ? sizeof(ulong) * 2 : sizeof(ulong);
            byte[] bytes = GC.AllocateUninitializedArray<byte>(width);
            Write(value, bytes);
            return bytes;
        }

        /// <summary>
        /// Writes one supported fixed-width numeric value into caller-owned storage.<br/>
        /// The method performs no heap allocation; BigInteger uses <see cref="Encode{T}(T)"/> because its canonical width depends on the value.<br/>
        /// </summary>
        /// <typeparam name="T">A supported fixed-width integral, floating-point, or Decimal value type.<br/></typeparam>
        /// <param name="value">The numeric value to encode.<br/></param>
        /// <param name="destination">The destination containing at least eight writable bytes.<br/></param>
        /// <returns>The encoded width written to <paramref name="destination"/>.<br/></returns>
        public static int Write<T>(T value, Span<byte> destination)
            where T : struct
        {
            Validate<T>();
            if (typeof(T) == typeof(BigInteger))
                throw new NotSupportedException("Caller-buffer BigInteger encoding requires an explicit maximum-width contract; use LibraDexCanonical.Numeric.Encode for variable-width values.");
            if (IsScalar16<T>())
            {
                int width = sizeof(ulong) * 2;
                if (destination.Length < width)
                    throw new ArgumentException($"A LibraDex canonical {typeof(T).Name} destination must contain at least {width} bytes.", nameof(destination));
                LibraDexGenericScalarCodec<T>.Encode16(value, out ulong high, out ulong low);
                BinaryPrimitives.WriteUInt64BigEndian(destination, high);
                BinaryPrimitives.WriteUInt64BigEndian(destination[sizeof(ulong)..], low);
                return width;
            }

            if (destination.Length < sizeof(ulong))
                throw new ArgumentException("A LibraDex canonical numeric destination must contain at least eight bytes.", nameof(destination));
            BinaryPrimitives.WriteUInt64BigEndian(destination, LibraDexGenericScalarCodec<T>.Encode8(value));
            return sizeof(ulong);
        }

        /// <summary>
        /// Decodes one supported numeric value from its LibraDex canonical bytes.<br/>
        /// This does not interpret ordinary CLR little-endian bytes; use the raw numeric coercion for developer-owned .NET layouts.<br/>
        /// </summary>
        /// <typeparam name="T">A supported integral, floating-point, Decimal, or BigInteger value type.<br/></typeparam>
        /// <param name="source">The complete canonical numeric value.<br/></param>
        /// <returns>The decoded CLR numeric value.<br/></returns>
        public static T Read<T>(ReadOnlySpan<byte> source)
            where T : struct
        {
            Validate<T>();
            if (typeof(T) == typeof(BigInteger))
            {
                if (source.Length == 0)
                    throw new ArgumentException("A LibraDex canonical BigInteger value cannot be empty.", nameof(source));
                if (source.Length == 1 && source[0] == 0x01)
                    return (T)(object)BigInteger.Zero;
                if (source.Length < 3)
                    throw new ArgumentException("The LibraDex canonical BigInteger value is incomplete.", nameof(source));

                bool negative = source[0] == 0x00;
                ushort storedLength = BinaryPrimitives.ReadUInt16BigEndian(source.Slice(1, 2));
                int magnitudeLength = negative ? unchecked((ushort)~storedLength) : storedLength;
                LibraDexBigIntKeyStorage storage = source.Length == magnitudeLength + 3
                    ? LibraDexBigIntKeyStorage.VariableWidth
                    : LibraDexBigIntKeyStorage.FixedWidth;
                int maxBytes = storage == LibraDexBigIntKeyStorage.VariableWidth
                    ? magnitudeLength
                    : checked(source.Length - 3);
                return (T)(object)LibraDexBigIntCodec.Decode(source, maxBytes, storage);
            }
            if (IsScalar16<T>())
            {
                if (source.Length != sizeof(ulong) * 2)
                    throw new ArgumentException($"A LibraDex canonical {typeof(T).Name} value must contain exactly {sizeof(ulong) * 2} bytes.", nameof(source));
                return LibraDexGenericScalarCodec<T>.Decode16(
                    BinaryPrimitives.ReadUInt64BigEndian(source),
                    BinaryPrimitives.ReadUInt64BigEndian(source[sizeof(ulong)..]));
            }
            if (source.Length != sizeof(ulong))
                throw new ArgumentException("A LibraDex canonical numeric value must contain exactly eight bytes.", nameof(source));
            return LibraDexGenericScalarCodec<T>.Decode8(BinaryPrimitives.ReadUInt64BigEndian(source));
        }

        private static void Validate<T>()
            where T : struct
        {
            Type type = typeof(T);
            if (type != typeof(byte) &&
                type != typeof(sbyte) &&
                type != typeof(short) &&
                type != typeof(ushort) &&
                type != typeof(int) &&
                type != typeof(uint) &&
                type != typeof(long) &&
                type != typeof(ulong) &&
                type != typeof(Int128) &&
                type != typeof(UInt128) &&
                type != typeof(float) &&
                type != typeof(double) &&
                type != typeof(decimal) &&
                type != typeof(BigInteger) &&
                type != typeof(char))
            {
                throw new NotSupportedException(
                    $"LibraDex canonical numeric conversion does not support '{type.FullName}'.");
            }
        }

        private static bool IsScalar16<T>()
            where T : struct
        {
            Type type = typeof(T);
            return type == typeof(Int128) ||
                type == typeof(UInt128) ||
                type == typeof(decimal);
        }
    }
}

/// <summary>
/// Identifies a reproducible text encoding used to convert developer-owned binary values.<br/>
/// The stable identity consists of the registered numeric code page plus an explicit malformed-input policy; it does not depend on localized display names, CLR implementation type names, or process-local <see cref="Encoding"/> instances.<br/>
/// Use this contract when an encoded binary projection or condition must be persisted and recreated with the same semantics.<br/>
/// </summary>
public sealed class LibraDexTextEncoding
{
    private readonly Encoding encoding;

    /// <summary>
    /// Gets the registered numeric code page that defines the text encoding.<br/>
    /// </summary>
    public int CodePage { get; }

    /// <summary>
    /// Gets the explicit malformed-input policy applied by this encoding contract.<br/>
    /// </summary>
    public global::LibraDex.Coercion.Text Coercion { get; }

    /// <summary>
    /// Creates a stable LibraDex text-encoding contract for one registered numeric code page.<br/>
    /// Unsupported code pages fail immediately instead of falling back to a different encoding and changing persisted semantics.<br/>
    /// </summary>
    /// <param name="codePage">The registered numeric code page.<br/></param>
    /// <param name="coercion">The malformed-input policy used for both encoding and decoding.<br/></param>
    /// <returns>An immutable encoding contract ready for binary conversion or durable condition capture.<br/></returns>
    public static LibraDexTextEncoding ForCodePage(
        int codePage,
        global::LibraDex.Coercion.Text coercion = global::LibraDex.Coercion.Text.Strict)
        => new(codePage, coercion);

    /// <summary>
    /// Attempts to describe an existing .NET encoding using LibraDex's stable code-page and coercion contract.<br/>
    /// Standard exception fallbacks map to <see cref="Coercion.Text.Strict"/>, while the registered encoding's ordinary replacement fallbacks map to <see cref="Coercion.Text.DotNetReplacement"/>.<br/>
    /// Custom encoding subclasses and custom fallback strings remain runtime-only because reconstructing them from a code page would silently lose caller-defined behavior.<br/>
    /// </summary>
    /// <param name="encoding">The .NET encoding instance to inspect.<br/></param>
    /// <param name="textEncoding">The equivalent stable LibraDex contract when the complete behavior is reproducible.<br/></param>
    /// <returns><c>true</c> when the encoding can be recreated exactly from stable fields; otherwise <c>false</c>.<br/></returns>
    public static bool TryFrom(Encoding? encoding, out LibraDexTextEncoding textEncoding)
    {
        textEncoding = null!;
        if (encoding is null)
            return false;

        try
        {
            Encoding registered = Encoding.GetEncoding(encoding.CodePage);
            if (encoding.GetType() != registered.GetType())
                return false;

            global::LibraDex.Coercion.Text coercion;
            if (encoding.EncoderFallback is EncoderExceptionFallback &&
                encoding.DecoderFallback is DecoderExceptionFallback)
            {
                coercion = global::LibraDex.Coercion.Text.Strict;
            }
            else if (!FallbacksMatch(encoding, registered))
            {
                return false;
            }
            else
            {
                coercion = global::LibraDex.Coercion.Text.DotNetReplacement;
            }

            textEncoding = new LibraDexTextEncoding(encoding.CodePage, coercion);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Describes an existing .NET encoding using LibraDex's stable code-page and coercion contract.<br/>
    /// Use <see cref="TryFrom(Encoding?, out LibraDexTextEncoding)"/> when caller-defined encoding behavior should remain an accepted runtime-only alternative.<br/>
    /// </summary>
    /// <param name="encoding">The .NET encoding instance whose behavior must be reproducible.<br/></param>
    /// <returns>The equivalent stable LibraDex encoding contract.<br/></returns>
    /// <exception cref="ArgumentException">Thrown when the supplied encoding uses a custom implementation or fallback behavior that cannot be recreated from stable fields.<br/></exception>
    public static LibraDexTextEncoding From(Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        return TryFrom(encoding, out LibraDexTextEncoding textEncoding)
            ? textEncoding
            : throw new ArgumentException(
                "The supplied Encoding uses implementation or fallback behavior that cannot be represented by LibraDex's stable code-page contract.",
                nameof(encoding));
    }

    /// <summary>
    /// Decodes one complete binary value using this stable encoding contract.<br/>
    /// Strict contracts throw <see cref="DecoderFallbackException"/> for malformed bytes; replacement contracts reproduce the registered .NET encoding's ordinary fallback behavior.<br/>
    /// </summary>
    /// <param name="source">The encoded bytes to decode.<br/></param>
    /// <returns>The decoded string.<br/></returns>
    public string Decode(ReadOnlySpan<byte> source)
        => encoding.GetString(source);

    /// <summary>
    /// Encodes one string into a newly allocated binary value using this stable encoding contract.<br/>
    /// Use <see cref="Write(ReadOnlySpan{char}, Span{byte})"/> with caller-owned storage in allocation-sensitive paths.<br/>
    /// </summary>
    /// <param name="value">The text to encode.<br/></param>
    /// <returns>A caller-owned byte array containing the encoded value.<br/></returns>
    public byte[] Encode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return encoding.GetBytes(value);
    }

    /// <summary>
    /// Returns the exact byte count required to encode one character span with this stable contract.<br/>
    /// </summary>
    /// <param name="value">The characters that will be encoded.<br/></param>
    /// <returns>The required destination length in bytes.<br/></returns>
    public int GetByteCount(ReadOnlySpan<char> value)
        => encoding.GetByteCount(value);

    /// <summary>
    /// Encodes one character span into caller-owned storage without allocating a result array.<br/>
    /// </summary>
    /// <param name="value">The characters to encode.<br/></param>
    /// <param name="destination">The destination that receives the encoded bytes.<br/></param>
    /// <returns>The number of bytes written to <paramref name="destination"/>.<br/></returns>
    public int Write(ReadOnlySpan<char> value, Span<byte> destination)
        => encoding.GetBytes(value, destination);

    private LibraDexTextEncoding(int codePage, global::LibraDex.Coercion.Text coercion)
    {
        if (!Enum.IsDefined(coercion))
            throw new ArgumentOutOfRangeException(nameof(coercion), coercion, "Unknown text coercion policy.");

        Encoding registered = Encoding.GetEncoding(codePage);
        encoding = coercion == global::LibraDex.Coercion.Text.Strict
            ? Encoding.GetEncoding(
                codePage,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback)
            : Encoding.GetEncoding(
                codePage,
                registered.EncoderFallback,
                registered.DecoderFallback);
        CodePage = codePage;
        Coercion = coercion;
    }

    private static bool FallbacksMatch(Encoding candidate, Encoding registered)
        => candidate.EncoderFallback is EncoderReplacementFallback candidateEncoder &&
           registered.EncoderFallback is EncoderReplacementFallback registeredEncoder &&
           string.Equals(candidateEncoder.DefaultString, registeredEncoder.DefaultString, StringComparison.Ordinal) &&
           candidate.DecoderFallback is DecoderReplacementFallback candidateDecoder &&
           registered.DecoderFallback is DecoderReplacementFallback registeredDecoder &&
           string.Equals(candidateDecoder.DefaultString, registeredDecoder.DefaultString, StringComparison.Ordinal);
}
