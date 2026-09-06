using System.Runtime.CompilerServices;

namespace LibraDex;

/// <summary>
/// Encodes supported date and time CLR values into the Abraxas structured 64-bit layout.<br/>
/// The layout preserves chronological ordering for complete date/time values while keeping year, month, day, time, sub-millisecond, and day-of-week components directly addressable by bit position.<br/>
/// LibraDex stores this value as an 8-byte scalar key so date conditions can use ordered primitives without query-time text conversion or parsing.<br/>
/// </summary>
internal static class LibraDexStructuredDateCodec
{
    private const int YearShift = 50;
    private const int MonthShift = 46;
    private const int DayShift = 41;
    private const int HourShift = 36;
    private const int MinuteShift = 30;
    private const int SecondShift = 24;
    private const int MillisecondShift = 14;
    private const int NanoSliceShift = 4;
    private const int DayOfWeekShift = 1;
    private const int TickWithinMillisecondShift = 0;

    /// <summary>
    /// Encodes a <see cref="DateTime"/> into the structured date/time scalar used by Abraxas-compatible date indexes.<br/>
    /// The year is stored in bits 50-63, followed by month, day, hour, minute, second, millisecond, a 10-bit sub-millisecond slice, and day-of-week bits.<br/>
    /// The input kind is not changed; callers that need UTC normalization should normalize before indexing, matching the Abraxas `DateTimeOffset.UtcDateTime` behavior.<br/>
    /// </summary>
    /// <param name="value">The date/time value to encode.</param>
    /// <returns>The structured sortable scalar.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Encode(DateTime value)
    {
        return Encode(value, DateTimeKeyEncoding.CalendarSdt);
    }

    /// <summary>
    /// Encodes a <see cref="DateTime"/> using the requested DateTime-like index encoding.<br/>
    /// Calendar SDT matches the Abraxas layout with day-of-week bits and 10-tick sub-millisecond slices; Precision SDT preserves all .NET ticks and leaves day-of-week derivable from the date fields.<br/>
    /// </summary>
    /// <param name="value">The date/time value to encode.</param>
    /// <param name="encoding">The index-level DateTime-like key encoding contract.</param>
    /// <returns>The structured sortable scalar.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Encode(DateTime value, DateTimeKeyEncoding encoding)
    {
        long ticks = value.Ticks;
        int millisecond = (int)((ticks / TimeSpan.TicksPerMillisecond) % 1000);
        int tickWithinMillisecond = (int)(ticks % TimeSpan.TicksPerMillisecond);
        int nanoSlice = tickWithinMillisecond / 10;
        int dayOfWeek = (int)value.DayOfWeek & 0b111;

        ulong encoded =
            ((ulong)value.Year << YearShift) |
            ((ulong)value.Month << MonthShift) |
            ((ulong)value.Day << DayShift) |
            ((ulong)value.Hour << HourShift) |
            ((ulong)value.Minute << MinuteShift) |
            ((ulong)value.Second << SecondShift) |
            ((ulong)millisecond << MillisecondShift);
        return encoding switch
        {
            DateTimeKeyEncoding.CalendarSdt => encoded |
                ((ulong)nanoSlice << NanoSliceShift) |
                ((ulong)dayOfWeek << DayOfWeekShift),
            DateTimeKeyEncoding.PrecisionSdt => encoded |
                ((ulong)tickWithinMillisecond << TickWithinMillisecondShift),
            _ => throw new ArgumentOutOfRangeException(nameof(encoding), encoding, "Unknown DateTime key encoding.")
        };
    }

    /// <summary>
    /// Encodes a <see cref="DateOnly"/> into the structured date scalar used by Abraxas-compatible date indexes.<br/>
    /// Time fields are left as zero while year, month, day, and day-of-week remain directly addressable.<br/>
    /// </summary>
    /// <param name="value">The date value to encode.</param>
    /// <returns>The structured sortable scalar.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Encode(DateOnly value)
    {
        return Encode(value, DateTimeKeyEncoding.CalendarSdt);
    }

    /// <summary>
    /// Encodes a <see cref="DateOnly"/> using the requested DateTime-like index encoding.<br/>
    /// Calendar SDT stores day-of-week bits directly; Precision SDT leaves those low bits available to keep layout parity with precise DateTime keys.<br/>
    /// </summary>
    /// <param name="value">The date value to encode.</param>
    /// <param name="encoding">The index-level DateTime-like key encoding contract.</param>
    /// <returns>The structured sortable scalar.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Encode(DateOnly value, DateTimeKeyEncoding encoding)
    {
        int dayOfWeek = (int)value.DayOfWeek & 0b111;
        ulong encoded =
            ((ulong)value.Year << YearShift) |
            ((ulong)value.Month << MonthShift) |
            ((ulong)value.Day << DayShift);
        return encoding switch
        {
            DateTimeKeyEncoding.CalendarSdt => encoded | ((ulong)dayOfWeek << DayOfWeekShift),
            DateTimeKeyEncoding.PrecisionSdt => encoded,
            _ => throw new ArgumentOutOfRangeException(nameof(encoding), encoding, "Unknown DateTime key encoding.")
        };
    }

    /// <summary>
    /// Encodes a <see cref="TimeOnly"/> into the structured time scalar used by Abraxas-compatible time indexes.<br/>
    /// Date fields are left as zero while hour, minute, second, millisecond, and sub-millisecond slice remain directly addressable.<br/>
    /// </summary>
    /// <param name="value">The time value to encode.</param>
    /// <returns>The structured sortable scalar.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Encode(TimeOnly value)
    {
        return Encode(value, DateTimeKeyEncoding.CalendarSdt);
    }

    /// <summary>
    /// Encodes a <see cref="TimeOnly"/> using the requested DateTime-like index encoding.<br/>
    /// Calendar SDT matches the Abraxas 10-tick slice layout; Precision SDT preserves the full tick within the encoded millisecond.<br/>
    /// </summary>
    /// <param name="value">The time value to encode.</param>
    /// <param name="encoding">The index-level DateTime-like key encoding contract.</param>
    /// <returns>The structured sortable scalar.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Encode(TimeOnly value, DateTimeKeyEncoding encoding)
    {
        long ticks = value.Ticks;
        int millisecond = (int)((ticks / TimeSpan.TicksPerMillisecond) % 1000);
        int tickWithinMillisecond = (int)(ticks % TimeSpan.TicksPerMillisecond);
        int nanoSlice = tickWithinMillisecond / 10;

        ulong encoded =
            ((ulong)value.Hour << HourShift) |
            ((ulong)value.Minute << MinuteShift) |
            ((ulong)value.Second << SecondShift) |
            ((ulong)millisecond << MillisecondShift);
        return encoding switch
        {
            DateTimeKeyEncoding.CalendarSdt => encoded | ((ulong)nanoSlice << NanoSliceShift),
            DateTimeKeyEncoding.PrecisionSdt => encoded | ((ulong)tickWithinMillisecond << TickWithinMillisecondShift),
            _ => throw new ArgumentOutOfRangeException(nameof(encoding), encoding, "Unknown DateTime key encoding.")
        };
    }

    /// <summary>
    /// Encodes a <see cref="DateTimeOffset"/> after normalizing it to UTC, matching Abraxas `GV` behavior for offset-aware values.<br/>
    /// </summary>
    /// <param name="value">The offset-aware date/time value to encode.</param>
    /// <returns>The structured sortable scalar for the UTC instant.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Encode(DateTimeOffset value)
    {
        return Encode(value, DateTimeKeyEncoding.CalendarSdt);
    }

    /// <summary>
    /// Encodes a <see cref="DateTimeOffset"/> after normalizing it to UTC with the requested DateTime-like index encoding.<br/>
    /// </summary>
    /// <param name="value">The offset-aware date/time value to encode.</param>
    /// <param name="encoding">The index-level DateTime-like key encoding contract.</param>
    /// <returns>The structured sortable scalar for the UTC instant.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Encode(DateTimeOffset value, DateTimeKeyEncoding encoding)
    {
        return Encode(value.UtcDateTime, encoding);
    }

    /// <summary>
    /// Decodes a structured scalar into a UTC <see cref="DateTime"/> using the Abraxas component layout.<br/>
    /// If the scalar has no date component, the returned value uses `0001-01-01` as the sentinel date, matching the Abraxas time-only unpacking convention.<br/>
    /// </summary>
    /// <param name="value">The structured scalar to decode.</param>
    /// <returns>The decoded date/time value.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static DateTime DecodeDateTime(ulong value)
    {
        return DecodeDateTime(value, DateTimeKeyEncoding.CalendarSdt);
    }

    /// <summary>
    /// Decodes a structured scalar into a UTC <see cref="DateTime"/> using the requested DateTime-like index encoding.<br/>
    /// </summary>
    /// <param name="value">The structured scalar to decode.</param>
    /// <param name="encoding">The index-level DateTime-like key encoding contract.</param>
    /// <returns>The decoded date/time value.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static DateTime DecodeDateTime(ulong value, DateTimeKeyEncoding encoding)
    {
        UnpackComponents(value, encoding, out int year, out int month, out int day, out int hour, out int minute, out int second, out int millisecond, out int ticksExtra);
        DateTime dateTime = year == 0 || month == 0 || day == 0
            ? new DateTime(1, 1, 1, hour, minute, second, millisecond, DateTimeKind.Utc)
            : new DateTime(year, month, day, hour, minute, second, millisecond, DateTimeKind.Utc);
        return dateTime.AddTicks(ticksExtra);
    }

    /// <summary>
    /// Decodes a structured scalar into a <see cref="DateOnly"/> using the Abraxas component layout.<br/>
    /// Time-only structured values return `0001-01-01` as a sentinel date, matching the Abraxas helper behavior.<br/>
    /// </summary>
    /// <param name="value">The structured scalar to decode.</param>
    /// <returns>The decoded date value.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static DateOnly DecodeDateOnly(ulong value)
    {
        UnpackComponents(value, DateTimeKeyEncoding.CalendarSdt, out int year, out int month, out int day, out _, out _, out _, out _, out _);
        return year == 0 || month == 0 || day == 0
            ? new DateOnly(1, 1, 1)
            : new DateOnly(year, month, day);
    }

    /// <summary>
    /// Decodes a structured scalar into a <see cref="TimeOnly"/> using the Abraxas component layout.<br/>
    /// Date components are ignored by this decoder.<br/>
    /// </summary>
    /// <param name="value">The structured scalar to decode.</param>
    /// <returns>The decoded time value.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static TimeOnly DecodeTimeOnly(ulong value)
    {
        return DecodeTimeOnly(value, DateTimeKeyEncoding.CalendarSdt);
    }

    /// <summary>
    /// Decodes a structured scalar into a <see cref="TimeOnly"/> using the requested DateTime-like index encoding.<br/>
    /// </summary>
    /// <param name="value">The structured scalar to decode.</param>
    /// <param name="encoding">The index-level DateTime-like key encoding contract.</param>
    /// <returns>The decoded time value.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static TimeOnly DecodeTimeOnly(ulong value, DateTimeKeyEncoding encoding)
    {
        UnpackComponents(value, encoding, out _, out _, out _, out int hour, out int minute, out int second, out int millisecond, out int ticksExtra);
        return new TimeOnly(hour, minute, second, millisecond).Add(TimeSpan.FromTicks(ticksExtra));
    }

    /// <summary>
    /// Unpacks the component fields from one structured date scalar.<br/>
    /// This mirrors the Abraxas bit layout and intentionally ignores the encoded day-of-week field because it can be recomputed by reconstructed date values when needed.<br/>
    /// </summary>
    /// <param name="value">The structured scalar to unpack.</param>
    /// <param name="year">The decoded year component.</param>
    /// <param name="month">The decoded month component.</param>
    /// <param name="day">The decoded day component.</param>
    /// <param name="hour">The decoded hour component.</param>
    /// <param name="minute">The decoded minute component.</param>
    /// <param name="second">The decoded second component.</param>
    /// <param name="millisecond">The decoded millisecond component.</param>
    /// <param name="ticksExtra">The decoded extra ticks below millisecond precision.</param>
    private static void UnpackComponents(
        ulong value,
        DateTimeKeyEncoding encoding,
        out int year,
        out int month,
        out int day,
        out int hour,
        out int minute,
        out int second,
        out int millisecond,
        out int ticksExtra)
    {
        year = (int)((value >> YearShift) & 0x3FFF);
        month = (int)((value >> MonthShift) & 0xF);
        day = (int)((value >> DayShift) & 0x1F);
        hour = (int)((value >> HourShift) & 0x1F);
        minute = (int)((value >> MinuteShift) & 0x3F);
        second = (int)((value >> SecondShift) & 0x3F);
        millisecond = (int)((value >> MillisecondShift) & 0x3FF);
        ticksExtra = encoding switch
        {
            DateTimeKeyEncoding.CalendarSdt => (int)((value >> NanoSliceShift) & 0x3FF) * 10,
            DateTimeKeyEncoding.PrecisionSdt => (int)((value >> TickWithinMillisecondShift) & 0x3FFF),
            _ => throw new ArgumentOutOfRangeException(nameof(encoding), encoding, "Unknown DateTime key encoding.")
        };
    }
}

/// <summary>
/// Encodes and decodes the public LibraDex temporal scalar representations used by typed indexes and explicitly coerced binary slices.<br/>
/// Date-like scalars use the selected <see cref="DateTimeKeyEncoding"/> and are stored as unsigned 64-bit values; callers placing them inside binary payloads write those scalars in big-endian byte order as required by the binary-slice coercion contract.<br/>
/// Ordered <see cref="TimeSpan"/> scalars flip the signed tick sign bit so unsigned big-endian comparison preserves the complete signed duration order.<br/>
/// </summary>
public static class LibraDexTemporalScalarCodec
{
    /// <summary>
    /// Encodes one <see cref="DateTime"/> into the selected LibraDex SDT scalar without changing its <see cref="DateTime.Kind"/> or clock fields.<br/>
    /// </summary>
    /// <param name="value">The date/time value whose calendar and clock fields are encoded.<br/></param>
    /// <param name="encoding">The Calendar SDT or Precision SDT representation to produce.<br/></param>
    /// <returns>The unsigned structured scalar; write it in big-endian order when embedding it in binary data.<br/></returns>
    public static ulong Encode(DateTime value, DateTimeKeyEncoding encoding)
        => LibraDexStructuredDateCodec.Encode(value, encoding);

    /// <summary>
    /// Decodes one LibraDex SDT scalar into a UTC <see cref="DateTime"/> while validating all reconstructed CLR calendar and clock fields.<br/>
    /// </summary>
    /// <param name="value">The unsigned structured scalar previously read from big-endian binary data or an index key.<br/></param>
    /// <param name="encoding">The Calendar SDT or Precision SDT representation carried by <paramref name="value"/>.<br/></param>
    /// <returns>The reconstructed UTC date/time value.<br/></returns>
    public static DateTime DecodeDateTime(ulong value, DateTimeKeyEncoding encoding)
    {
        DateTime decoded = LibraDexStructuredDateCodec.DecodeDateTime(value, encoding);
        return LibraDexStructuredDateCodec.Encode(decoded, encoding) == value
            ? decoded
            : throw new ArgumentException("The scalar is not a canonical LibraDex DateTime SDT value.", nameof(value));
    }

    /// <summary>
    /// Encodes one <see cref="DateOnly"/> into the selected LibraDex SDT scalar with all time fields left empty.<br/>
    /// </summary>
    /// <param name="value">The date value whose calendar fields are encoded.<br/></param>
    /// <param name="encoding">The Calendar SDT or Precision SDT representation to produce.<br/></param>
    /// <returns>The unsigned structured scalar; write it in big-endian order when embedding it in binary data.<br/></returns>
    public static ulong Encode(DateOnly value, DateTimeKeyEncoding encoding)
        => LibraDexStructuredDateCodec.Encode(value, encoding);

    /// <summary>
    /// Decodes one date-bearing LibraDex SDT scalar into a <see cref="DateOnly"/> and validates the selected representation and reconstructed calendar fields.<br/>
    /// Clock fields, when present in the supplied scalar, do not affect the returned date.<br/>
    /// </summary>
    /// <param name="value">The unsigned structured scalar previously read from big-endian binary data or an index key.<br/></param>
    /// <param name="encoding">The Calendar SDT or Precision SDT representation carried by <paramref name="value"/>.<br/></param>
    /// <returns>The reconstructed date value.<br/></returns>
    public static DateOnly DecodeDateOnly(ulong value, DateTimeKeyEncoding encoding)
    {
        DateOnly decoded = DateOnly.FromDateTime(LibraDexStructuredDateCodec.DecodeDateTime(value, encoding));
        return LibraDexStructuredDateCodec.Encode(decoded, encoding) == value
            ? decoded
            : throw new ArgumentException("The scalar is not a canonical LibraDex DateOnly SDT value.", nameof(value));
    }

    /// <summary>
    /// Encodes one <see cref="TimeOnly"/> into the selected LibraDex SDT scalar with all date fields left empty.<br/>
    /// </summary>
    /// <param name="value">The time value whose clock fields are encoded.<br/></param>
    /// <param name="encoding">The Calendar SDT or Precision SDT representation to produce.<br/></param>
    /// <returns>The unsigned structured scalar; write it in big-endian order when embedding it in binary data.<br/></returns>
    public static ulong Encode(TimeOnly value, DateTimeKeyEncoding encoding)
        => LibraDexStructuredDateCodec.Encode(value, encoding);

    /// <summary>
    /// Decodes one time-bearing LibraDex SDT scalar into a <see cref="TimeOnly"/> and validates the selected representation and reconstructed clock fields.<br/>
    /// Date fields, when present in the supplied scalar, do not affect the returned time.<br/>
    /// </summary>
    /// <param name="value">The unsigned structured scalar previously read from big-endian binary data or an index key.<br/></param>
    /// <param name="encoding">The Calendar SDT or Precision SDT representation carried by <paramref name="value"/>.<br/></param>
    /// <returns>The reconstructed time value.<br/></returns>
    public static TimeOnly DecodeTimeOnly(ulong value, DateTimeKeyEncoding encoding)
    {
        TimeOnly decoded = LibraDexStructuredDateCodec.DecodeTimeOnly(value, encoding);
        return LibraDexStructuredDateCodec.Encode(decoded, encoding) == value
            ? decoded
            : throw new ArgumentException("The scalar is not a canonical LibraDex TimeOnly SDT value.", nameof(value));
    }

    /// <summary>
    /// Encodes one <see cref="DateTimeOffset"/> as its UTC instant in the selected LibraDex SDT representation.<br/>
    /// The original offset is intentionally not retained because the SDT binary coercion represents an instant rather than a local-time-plus-offset pair.<br/>
    /// </summary>
    /// <param name="value">The offset-aware value normalized to UTC before encoding.<br/></param>
    /// <param name="encoding">The Calendar SDT or Precision SDT representation to produce.<br/></param>
    /// <returns>The unsigned UTC structured scalar; write it in big-endian order when embedding it in binary data.<br/></returns>
    public static ulong Encode(DateTimeOffset value, DateTimeKeyEncoding encoding)
        => LibraDexStructuredDateCodec.Encode(value, encoding);

    /// <summary>
    /// Decodes one UTC LibraDex SDT scalar into a zero-offset <see cref="DateTimeOffset"/> while validating all reconstructed fields.<br/>
    /// </summary>
    /// <param name="value">The unsigned UTC structured scalar previously read from big-endian binary data or an index key.<br/></param>
    /// <param name="encoding">The Calendar SDT or Precision SDT representation carried by <paramref name="value"/>.<br/></param>
    /// <returns>The reconstructed instant with <see cref="TimeSpan.Zero"/> as its offset.<br/></returns>
    public static DateTimeOffset DecodeDateTimeOffset(ulong value, DateTimeKeyEncoding encoding)
    {
        DateTimeOffset decoded = new(LibraDexStructuredDateCodec.DecodeDateTime(value, encoding));
        return LibraDexStructuredDateCodec.Encode(decoded, encoding) == value
            ? decoded
            : throw new ArgumentException("The scalar is not a canonical LibraDex DateTimeOffset SDT value.", nameof(value));
    }

    /// <summary>
    /// Encodes one <see cref="TimeSpan"/> as LibraDex's unsigned ordered tick scalar by flipping the signed tick sign bit.<br/>
    /// </summary>
    /// <param name="value">The complete signed duration to encode.<br/></param>
    /// <returns>The unsigned ordered scalar; write it in big-endian order when embedding it in binary data.<br/></returns>
    public static ulong Encode(TimeSpan value)
        => unchecked((ulong)(value.Ticks ^ long.MinValue));

    /// <summary>
    /// Decodes one LibraDex ordered duration scalar by restoring the signed tick sign bit.<br/>
    /// Every unsigned input maps to exactly one valid <see cref="TimeSpan"/> value.<br/>
    /// </summary>
    /// <param name="value">The unsigned ordered scalar previously read from big-endian binary data or an index key.<br/></param>
    /// <returns>The reconstructed signed duration.<br/></returns>
    public static TimeSpan DecodeTimeSpan(ulong value)
        => TimeSpan.FromTicks(unchecked((long)value) ^ long.MinValue);
}
