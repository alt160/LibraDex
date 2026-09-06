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
    private const long StructuredDateTickQuantum = 10;

    /// <summary>
    /// Gets whether <typeparamref name="T"/> has a finite CLR value domain that should govern condition-derived boundary movement.<br/>
    /// This is separate from raw encoded-lane movement so codec-defined domains such as `byte[]` and `Guid` can continue to use bytewise stepping while scalar/date domains stay inside valid public values.<br/>
    /// </summary>
    internal static bool HasExplicitScalarDomain
    {
        get
        {
            Type type = typeof(T);
            return type == typeof(bool) ||
                type == typeof(byte) ||
                type == typeof(sbyte) ||
                type == typeof(short) ||
                type == typeof(ushort) ||
                type == typeof(char) ||
                type == typeof(int) ||
                type == typeof(uint) ||
                type == typeof(long) ||
                type == typeof(ulong) ||
                type == typeof(float) ||
                type == typeof(double) ||
                type == typeof(decimal) ||
                type == typeof(Int128) ||
                type == typeof(UInt128) ||
                type == typeof(DateTime) ||
                type == typeof(DateTimeOffset) ||
                type == typeof(DateOnly) ||
                type == typeof(TimeOnly) ||
                type == typeof(TimeSpan);
        }
    }

    /// <summary>
    /// Gets the lowest developer-facing value in the supported scalar key domain.<br/>
    /// This is used by condition-derived all/before/after primitives so narrow CLR domains such as `byte`, `uint`, `char`, and `bool` do not decode invalid values from raw `ulong` sentinels.<br/>
    /// </summary>
    /// <param name="value">Receives the minimum supported scalar value for <typeparamref name="T"/>.</param>
    /// <returns><see langword="true"/> when <typeparamref name="T"/> has an explicit supported scalar domain.</returns>
    internal static bool TryGetMinimumValue(out T value)
    {
        Type type = typeof(T);
        if (type == typeof(bool))
        {
            bool typed = false;
            value = Unsafe.As<bool, T>(ref typed);
            return true;
        }

        if (type == typeof(byte))
        {
            byte typed = byte.MinValue;
            value = Unsafe.As<byte, T>(ref typed);
            return true;
        }

        if (type == typeof(sbyte))
        {
            sbyte typed = sbyte.MinValue;
            value = Unsafe.As<sbyte, T>(ref typed);
            return true;
        }

        if (type == typeof(short))
        {
            short typed = short.MinValue;
            value = Unsafe.As<short, T>(ref typed);
            return true;
        }

        if (type == typeof(ushort))
        {
            ushort typed = ushort.MinValue;
            value = Unsafe.As<ushort, T>(ref typed);
            return true;
        }

        if (type == typeof(char))
        {
            char typed = char.MinValue;
            value = Unsafe.As<char, T>(ref typed);
            return true;
        }

        if (type == typeof(int))
        {
            int typed = int.MinValue;
            value = Unsafe.As<int, T>(ref typed);
            return true;
        }

        if (type == typeof(uint))
        {
            uint typed = uint.MinValue;
            value = Unsafe.As<uint, T>(ref typed);
            return true;
        }

        if (type == typeof(long))
        {
            long typed = long.MinValue;
            value = Unsafe.As<long, T>(ref typed);
            return true;
        }

        if (type == typeof(ulong))
        {
            ulong typed = ulong.MinValue;
            value = Unsafe.As<ulong, T>(ref typed);
            return true;
        }

        if (type == typeof(float))
        {
            float typed = float.NegativeInfinity;
            value = Unsafe.As<float, T>(ref typed);
            return true;
        }

        if (type == typeof(double))
        {
            double typed = double.NegativeInfinity;
            value = Unsafe.As<double, T>(ref typed);
            return true;
        }

        if (type == typeof(decimal))
        {
            decimal typed = decimal.MinValue;
            value = Unsafe.As<decimal, T>(ref typed);
            return true;
        }

        if (type == typeof(Int128))
        {
            Int128 typed = Int128.MinValue;
            value = Unsafe.As<Int128, T>(ref typed);
            return true;
        }

        if (type == typeof(UInt128))
        {
            UInt128 typed = UInt128.MinValue;
            value = Unsafe.As<UInt128, T>(ref typed);
            return true;
        }

        if (type == typeof(DateTime))
        {
            DateTime typed = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
            value = Unsafe.As<DateTime, T>(ref typed);
            return true;
        }

        if (type == typeof(DateTimeOffset))
        {
            DateTimeOffset typed = DateTimeOffset.MinValue.ToUniversalTime();
            value = Unsafe.As<DateTimeOffset, T>(ref typed);
            return true;
        }

        if (type == typeof(DateOnly))
        {
            DateOnly typed = DateOnly.MinValue;
            value = Unsafe.As<DateOnly, T>(ref typed);
            return true;
        }

        if (type == typeof(TimeOnly))
        {
            TimeOnly typed = TimeOnly.MinValue;
            value = Unsafe.As<TimeOnly, T>(ref typed);
            return true;
        }

        if (type == typeof(TimeSpan))
        {
            TimeSpan typed = TimeSpan.MinValue;
            value = Unsafe.As<TimeSpan, T>(ref typed);
            return true;
        }

        value = default!;
        return false;
    }

    /// <summary>
    /// Gets the highest developer-facing value in the supported scalar key domain.<br/>
    /// This keeps full-domain range readers inside valid CLR values instead of constructing raw encoded sentinels that may not round-trip through narrow public types.<br/>
    /// </summary>
    /// <param name="value">Receives the maximum supported scalar value for <typeparamref name="T"/>.</param>
    /// <returns><see langword="true"/> when <typeparamref name="T"/> has an explicit supported scalar domain.</returns>
    internal static bool TryGetMaximumValue(out T value)
    {
        Type type = typeof(T);
        if (type == typeof(bool))
        {
            bool typed = true;
            value = Unsafe.As<bool, T>(ref typed);
            return true;
        }

        if (type == typeof(byte))
        {
            byte typed = byte.MaxValue;
            value = Unsafe.As<byte, T>(ref typed);
            return true;
        }

        if (type == typeof(sbyte))
        {
            sbyte typed = sbyte.MaxValue;
            value = Unsafe.As<sbyte, T>(ref typed);
            return true;
        }

        if (type == typeof(short))
        {
            short typed = short.MaxValue;
            value = Unsafe.As<short, T>(ref typed);
            return true;
        }

        if (type == typeof(ushort))
        {
            ushort typed = ushort.MaxValue;
            value = Unsafe.As<ushort, T>(ref typed);
            return true;
        }

        if (type == typeof(char))
        {
            char typed = char.MaxValue;
            value = Unsafe.As<char, T>(ref typed);
            return true;
        }

        if (type == typeof(int))
        {
            int typed = int.MaxValue;
            value = Unsafe.As<int, T>(ref typed);
            return true;
        }

        if (type == typeof(uint))
        {
            uint typed = uint.MaxValue;
            value = Unsafe.As<uint, T>(ref typed);
            return true;
        }

        if (type == typeof(long))
        {
            long typed = long.MaxValue;
            value = Unsafe.As<long, T>(ref typed);
            return true;
        }

        if (type == typeof(ulong))
        {
            ulong typed = ulong.MaxValue;
            value = Unsafe.As<ulong, T>(ref typed);
            return true;
        }

        if (type == typeof(float))
        {
            float typed = float.PositiveInfinity;
            value = Unsafe.As<float, T>(ref typed);
            return true;
        }

        if (type == typeof(double))
        {
            double typed = double.PositiveInfinity;
            value = Unsafe.As<double, T>(ref typed);
            return true;
        }

        if (type == typeof(decimal))
        {
            decimal typed = decimal.MaxValue;
            value = Unsafe.As<decimal, T>(ref typed);
            return true;
        }

        if (type == typeof(Int128))
        {
            Int128 typed = Int128.MaxValue;
            value = Unsafe.As<Int128, T>(ref typed);
            return true;
        }

        if (type == typeof(UInt128))
        {
            UInt128 typed = UInt128.MaxValue;
            value = Unsafe.As<UInt128, T>(ref typed);
            return true;
        }

        if (type == typeof(DateTime))
        {
            DateTime typed = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);
            value = Unsafe.As<DateTime, T>(ref typed);
            return true;
        }

        if (type == typeof(DateTimeOffset))
        {
            DateTimeOffset typed = DateTimeOffset.MaxValue.ToUniversalTime();
            value = Unsafe.As<DateTimeOffset, T>(ref typed);
            return true;
        }

        if (type == typeof(DateOnly))
        {
            DateOnly typed = DateOnly.MaxValue;
            value = Unsafe.As<DateOnly, T>(ref typed);
            return true;
        }

        if (type == typeof(TimeOnly))
        {
            TimeOnly typed = TimeOnly.MaxValue;
            value = Unsafe.As<TimeOnly, T>(ref typed);
            return true;
        }

        if (type == typeof(TimeSpan))
        {
            TimeSpan typed = TimeSpan.MaxValue;
            value = Unsafe.As<TimeSpan, T>(ref typed);
            return true;
        }

        value = default!;
        return false;
    }

    /// <summary>
    /// Attempts to move one public scalar key to its immediate predecessor when the CLR domain is narrower than the persisted 8-byte lane.<br/>
    /// Returning <see langword="false"/> means the value is already at the public minimum, or that <typeparamref name="T"/> should use the generic encoded fallback because it does not expose an explicit scalar domain.<br/>
    /// </summary>
    /// <param name="current">The current scalar value.</param>
    /// <param name="previous">Receives the previous scalar value when one exists.</param>
    /// <returns><see langword="true"/> when a previous value exists, otherwise <see langword="false"/>.</returns>
    internal static bool TryGetPreviousValue(T current, out T previous)
    {
        return TryGetPreviousValue(current, DateTimeKeyEncoding.CalendarSdt, out previous);
    }

    internal static bool TryGetPreviousValue(T current, DateTimeKeyEncoding dateTimeKeyEncoding, out T previous)
    {
        Type type = typeof(T);
        if (type == typeof(bool))
        {
            bool typed = Unsafe.As<T, bool>(ref current);
            if (!typed)
            {
                previous = default!;
                return false;
            }

            bool result = false;
            previous = Unsafe.As<bool, T>(ref result);
            return true;
        }

        if (type == typeof(byte))
        {
            byte typed = Unsafe.As<T, byte>(ref current);
            if (typed == byte.MinValue)
            {
                previous = default!;
                return false;
            }

            typed--;
            previous = Unsafe.As<byte, T>(ref typed);
            return true;
        }

        if (type == typeof(ushort))
        {
            ushort typed = Unsafe.As<T, ushort>(ref current);
            if (typed == ushort.MinValue)
            {
                previous = default!;
                return false;
            }

            typed--;
            previous = Unsafe.As<ushort, T>(ref typed);
            return true;
        }

        if (type == typeof(char))
        {
            char typed = Unsafe.As<T, char>(ref current);
            if (typed == char.MinValue)
            {
                previous = default!;
                return false;
            }

            typed--;
            previous = Unsafe.As<char, T>(ref typed);
            return true;
        }

        if (type == typeof(uint))
        {
            uint typed = Unsafe.As<T, uint>(ref current);
            if (typed == uint.MinValue)
            {
                previous = default!;
                return false;
            }

            typed--;
            previous = Unsafe.As<uint, T>(ref typed);
            return true;
        }

        previous = default!;
        return TryGetPreviousSignedLikeValue(current, dateTimeKeyEncoding, out previous);
    }

    /// <summary>
    /// Attempts to move one public scalar key to its immediate successor when the CLR domain is narrower than the persisted 8-byte lane.<br/>
    /// This protects exclusive `After` boundaries for public scalar domains such as `bool`, `byte`, and `uint`, where decoding encoded+1 beyond the public max would otherwise overflow or alias.<br/>
    /// </summary>
    /// <param name="current">The current scalar value.</param>
    /// <param name="next">Receives the next scalar value when one exists.</param>
    /// <returns><see langword="true"/> when a next value exists, otherwise <see langword="false"/>.</returns>
    internal static bool TryGetNextValue(T current, out T next)
    {
        return TryGetNextValue(current, DateTimeKeyEncoding.CalendarSdt, out next);
    }

    internal static bool TryGetNextValue(T current, DateTimeKeyEncoding dateTimeKeyEncoding, out T next)
    {
        Type type = typeof(T);
        if (type == typeof(bool))
        {
            bool typed = Unsafe.As<T, bool>(ref current);
            if (typed)
            {
                next = default!;
                return false;
            }

            bool result = true;
            next = Unsafe.As<bool, T>(ref result);
            return true;
        }

        if (type == typeof(byte))
        {
            byte typed = Unsafe.As<T, byte>(ref current);
            if (typed == byte.MaxValue)
            {
                next = default!;
                return false;
            }

            typed++;
            next = Unsafe.As<byte, T>(ref typed);
            return true;
        }

        if (type == typeof(ushort))
        {
            ushort typed = Unsafe.As<T, ushort>(ref current);
            if (typed == ushort.MaxValue)
            {
                next = default!;
                return false;
            }

            typed++;
            next = Unsafe.As<ushort, T>(ref typed);
            return true;
        }

        if (type == typeof(char))
        {
            char typed = Unsafe.As<T, char>(ref current);
            if (typed == char.MaxValue)
            {
                next = default!;
                return false;
            }

            typed++;
            next = Unsafe.As<char, T>(ref typed);
            return true;
        }

        if (type == typeof(uint))
        {
            uint typed = Unsafe.As<T, uint>(ref current);
            if (typed == uint.MaxValue)
            {
                next = default!;
                return false;
            }

            typed++;
            next = Unsafe.As<uint, T>(ref typed);
            return true;
        }

        next = default!;
        return TryGetNextSignedLikeValue(current, dateTimeKeyEncoding, out next);
    }

    /// <summary>
    /// Handles predecessor movement for signed, full-width unsigned, and date-like scalar domains.<br/>
    /// These domains either have natural CLR arithmetic or a safe tick/day step, so condition exclusion can stay in public value space instead of decoding invalid raw sentinels.<br/>
    /// </summary>
    /// <param name="current">The current scalar value.</param>
    /// <param name="previous">Receives the previous scalar value when one exists.</param>
    /// <returns><see langword="true"/> when a previous value exists, otherwise <see langword="false"/>.</returns>
    private static bool TryGetPreviousSignedLikeValue(T current, DateTimeKeyEncoding dateTimeKeyEncoding, out T previous)
    {
        Type type = typeof(T);
        if (type == typeof(float))
        {
            float typed = Unsafe.As<T, float>(ref current);
            if (float.IsNaN(typed) || float.IsNegativeInfinity(typed))
            {
                previous = default!;
                return false;
            }

            float result = MathF.BitDecrement(typed);
            previous = Unsafe.As<float, T>(ref result);
            return true;
        }

        if (type == typeof(double))
        {
            double typed = Unsafe.As<T, double>(ref current);
            if (double.IsNaN(typed) || double.IsNegativeInfinity(typed))
            {
                previous = default!;
                return false;
            }

            double result = Math.BitDecrement(typed);
            previous = Unsafe.As<double, T>(ref result);
            return true;
        }

        if (type == typeof(sbyte))
        {
            sbyte typed = Unsafe.As<T, sbyte>(ref current);
            if (typed == sbyte.MinValue)
            {
                previous = default!;
                return false;
            }

            typed--;
            previous = Unsafe.As<sbyte, T>(ref typed);
            return true;
        }

        if (type == typeof(short))
        {
            short typed = Unsafe.As<T, short>(ref current);
            if (typed == short.MinValue)
            {
                previous = default!;
                return false;
            }

            typed--;
            previous = Unsafe.As<short, T>(ref typed);
            return true;
        }

        if (type == typeof(int))
        {
            int typed = Unsafe.As<T, int>(ref current);
            if (typed == int.MinValue)
            {
                previous = default!;
                return false;
            }

            typed--;
            previous = Unsafe.As<int, T>(ref typed);
            return true;
        }

        if (type == typeof(long))
        {
            long typed = Unsafe.As<T, long>(ref current);
            if (typed == long.MinValue)
            {
                previous = default!;
                return false;
            }

            typed--;
            previous = Unsafe.As<long, T>(ref typed);
            return true;
        }

        if (type == typeof(ulong))
        {
            ulong typed = Unsafe.As<T, ulong>(ref current);
            if (typed == ulong.MinValue)
            {
                previous = default!;
                return false;
            }

            typed--;
            previous = Unsafe.As<ulong, T>(ref typed);
            return true;
        }

        if (type == typeof(decimal))
        {
            decimal typed = Unsafe.As<T, decimal>(ref current);
            if (!LibraDexOrderedDecimalCodec.TryGetPrevious(typed, out decimal result))
            {
                previous = default!;
                return false;
            }

            previous = Unsafe.As<decimal, T>(ref result);
            return true;
        }

        if (type == typeof(Int128))
        {
            Int128 typed = Unsafe.As<T, Int128>(ref current);
            if (typed == Int128.MinValue)
            {
                previous = default!;
                return false;
            }

            typed--;
            previous = Unsafe.As<Int128, T>(ref typed);
            return true;
        }

        if (type == typeof(UInt128))
        {
            UInt128 typed = Unsafe.As<T, UInt128>(ref current);
            if (typed == UInt128.MinValue)
            {
                previous = default!;
                return false;
            }

            typed--;
            previous = Unsafe.As<UInt128, T>(ref typed);
            return true;
        }

        if (type == typeof(DateTime))
        {
            DateTime typed = Unsafe.As<T, DateTime>(ref current);
            if (!TryGetPreviousStructuredDateTicks(typed.Ticks, DateTime.MinValue.Ticks, dateTimeKeyEncoding, out long previousTicks))
            {
                previous = default!;
                return false;
            }

            typed = new DateTime(previousTicks, typed.Kind);
            previous = Unsafe.As<DateTime, T>(ref typed);
            return true;
        }

        if (type == typeof(DateTimeOffset))
        {
            DateTimeOffset typed = Unsafe.As<T, DateTimeOffset>(ref current).ToUniversalTime();
            if (!TryGetPreviousStructuredDateTicks(typed.UtcTicks, DateTimeOffset.MinValue.ToUniversalTime().UtcTicks, dateTimeKeyEncoding, out long previousTicks))
            {
                previous = default!;
                return false;
            }

            typed = new DateTimeOffset(new DateTime(previousTicks, DateTimeKind.Utc), TimeSpan.Zero);
            previous = Unsafe.As<DateTimeOffset, T>(ref typed);
            return true;
        }

        if (type == typeof(DateOnly))
        {
            DateOnly typed = Unsafe.As<T, DateOnly>(ref current);
            if (typed <= DateOnly.MinValue)
            {
                previous = default!;
                return false;
            }

            typed = typed.AddDays(-1);
            previous = Unsafe.As<DateOnly, T>(ref typed);
            return true;
        }

        if (type == typeof(TimeOnly))
        {
            TimeOnly typed = Unsafe.As<T, TimeOnly>(ref current);
            if (!TryGetPreviousStructuredDateTicks(typed.Ticks, TimeOnly.MinValue.Ticks, dateTimeKeyEncoding, out long previousTicks))
            {
                previous = default!;
                return false;
            }

            typed = new TimeOnly(previousTicks);
            previous = Unsafe.As<TimeOnly, T>(ref typed);
            return true;
        }

        if (type == typeof(TimeSpan))
        {
            TimeSpan typed = Unsafe.As<T, TimeSpan>(ref current);
            if (typed <= TimeSpan.MinValue)
            {
                previous = default!;
                return false;
            }

            typed = typed.Add(TimeSpan.FromTicks(-1));
            previous = Unsafe.As<TimeSpan, T>(ref typed);
            return true;
        }

        previous = default!;
        return false;
    }

    /// <summary>
    /// Handles successor movement for signed, full-width unsigned, and date-like scalar domains.<br/>
    /// The helper keeps exclusive upper/lower condition bounds inside valid public CLR values before the request reaches range-reader construction.<br/>
    /// </summary>
    /// <param name="current">The current scalar value.</param>
    /// <param name="next">Receives the next scalar value when one exists.</param>
    /// <returns><see langword="true"/> when a next value exists, otherwise <see langword="false"/>.</returns>
    private static bool TryGetNextSignedLikeValue(T current, DateTimeKeyEncoding dateTimeKeyEncoding, out T next)
    {
        Type type = typeof(T);
        if (type == typeof(float))
        {
            float typed = Unsafe.As<T, float>(ref current);
            if (float.IsNaN(typed) || float.IsPositiveInfinity(typed))
            {
                next = default!;
                return false;
            }

            float result = MathF.BitIncrement(typed);
            next = Unsafe.As<float, T>(ref result);
            return true;
        }

        if (type == typeof(double))
        {
            double typed = Unsafe.As<T, double>(ref current);
            if (double.IsNaN(typed) || double.IsPositiveInfinity(typed))
            {
                next = default!;
                return false;
            }

            double result = Math.BitIncrement(typed);
            next = Unsafe.As<double, T>(ref result);
            return true;
        }

        if (type == typeof(sbyte))
        {
            sbyte typed = Unsafe.As<T, sbyte>(ref current);
            if (typed == sbyte.MaxValue)
            {
                next = default!;
                return false;
            }

            typed++;
            next = Unsafe.As<sbyte, T>(ref typed);
            return true;
        }

        if (type == typeof(short))
        {
            short typed = Unsafe.As<T, short>(ref current);
            if (typed == short.MaxValue)
            {
                next = default!;
                return false;
            }

            typed++;
            next = Unsafe.As<short, T>(ref typed);
            return true;
        }

        if (type == typeof(int))
        {
            int typed = Unsafe.As<T, int>(ref current);
            if (typed == int.MaxValue)
            {
                next = default!;
                return false;
            }

            typed++;
            next = Unsafe.As<int, T>(ref typed);
            return true;
        }

        if (type == typeof(long))
        {
            long typed = Unsafe.As<T, long>(ref current);
            if (typed == long.MaxValue)
            {
                next = default!;
                return false;
            }

            typed++;
            next = Unsafe.As<long, T>(ref typed);
            return true;
        }

        if (type == typeof(ulong))
        {
            ulong typed = Unsafe.As<T, ulong>(ref current);
            if (typed == ulong.MaxValue)
            {
                next = default!;
                return false;
            }

            typed++;
            next = Unsafe.As<ulong, T>(ref typed);
            return true;
        }

        if (type == typeof(decimal))
        {
            decimal typed = Unsafe.As<T, decimal>(ref current);
            if (!LibraDexOrderedDecimalCodec.TryGetNext(typed, out decimal result))
            {
                next = default!;
                return false;
            }

            next = Unsafe.As<decimal, T>(ref result);
            return true;
        }

        if (type == typeof(Int128))
        {
            Int128 typed = Unsafe.As<T, Int128>(ref current);
            if (typed == Int128.MaxValue)
            {
                next = default!;
                return false;
            }

            typed++;
            next = Unsafe.As<Int128, T>(ref typed);
            return true;
        }

        if (type == typeof(UInt128))
        {
            UInt128 typed = Unsafe.As<T, UInt128>(ref current);
            if (typed == UInt128.MaxValue)
            {
                next = default!;
                return false;
            }

            typed++;
            next = Unsafe.As<UInt128, T>(ref typed);
            return true;
        }

        if (type == typeof(DateTime))
        {
            DateTime typed = Unsafe.As<T, DateTime>(ref current);
            if (!TryGetNextStructuredDateTicks(typed.Ticks, DateTime.MaxValue.Ticks, dateTimeKeyEncoding, out long nextTicks))
            {
                next = default!;
                return false;
            }

            typed = new DateTime(nextTicks, typed.Kind);
            next = Unsafe.As<DateTime, T>(ref typed);
            return true;
        }

        if (type == typeof(DateTimeOffset))
        {
            DateTimeOffset typed = Unsafe.As<T, DateTimeOffset>(ref current).ToUniversalTime();
            if (!TryGetNextStructuredDateTicks(typed.UtcTicks, DateTimeOffset.MaxValue.ToUniversalTime().UtcTicks, dateTimeKeyEncoding, out long nextTicks))
            {
                next = default!;
                return false;
            }

            typed = new DateTimeOffset(new DateTime(nextTicks, DateTimeKind.Utc), TimeSpan.Zero);
            next = Unsafe.As<DateTimeOffset, T>(ref typed);
            return true;
        }

        if (type == typeof(DateOnly))
        {
            DateOnly typed = Unsafe.As<T, DateOnly>(ref current);
            if (typed >= DateOnly.MaxValue)
            {
                next = default!;
                return false;
            }

            typed = typed.AddDays(1);
            next = Unsafe.As<DateOnly, T>(ref typed);
            return true;
        }

        if (type == typeof(TimeOnly))
        {
            TimeOnly typed = Unsafe.As<T, TimeOnly>(ref current);
            if (!TryGetNextStructuredDateTicks(typed.Ticks, TimeOnly.MaxValue.Ticks, dateTimeKeyEncoding, out long nextTicks))
            {
                next = default!;
                return false;
            }

            typed = new TimeOnly(nextTicks);
            next = Unsafe.As<TimeOnly, T>(ref typed);
            return true;
        }

        if (type == typeof(TimeSpan))
        {
            TimeSpan typed = Unsafe.As<T, TimeSpan>(ref current);
            if (typed >= TimeSpan.MaxValue)
            {
                next = default!;
                return false;
            }

            typed = typed.Add(TimeSpan.FromTicks(1));
            next = Unsafe.As<TimeSpan, T>(ref typed);
            return true;
        }

        next = default!;
        return false;
    }

    /// <summary>
    /// Calculates the next representable structured date/time tick value after a public CLR boundary.<br/>
    /// The structured codec stores sub-millisecond precision in 10-tick slices, so strict `After` ranges must move to the next encoded quantum rather than one raw CLR tick.<br/>
    /// </summary>
    /// <param name="ticks">The public CLR tick boundary.</param>
    /// <param name="maxTicks">The maximum valid tick value for the CLR type.</param>
    /// <param name="nextTicks">The next encoded-quantum tick value when one exists.</param>
    /// <returns>True when a successor quantum exists within the type domain.</returns>
    private static bool TryGetNextStructuredDateTicks(long ticks, long maxTicks, DateTimeKeyEncoding encoding, out long nextTicks)
    {
        long quantum = GetStructuredDateTickQuantum(encoding);
        long remainder = ticks % quantum;
        long delta = remainder == 0
            ? quantum
            : quantum - remainder;
        if (ticks > maxTicks - delta)
        {
            nextTicks = default;
            return false;
        }

        nextTicks = ticks + delta;
        return true;
    }

    /// <summary>
    /// Calculates the previous representable structured date/time tick value before a public CLR boundary.<br/>
    /// The structured codec stores sub-millisecond precision in 10-tick slices, so strict `Before` ranges must move to the prior encoded quantum when the boundary is already aligned.<br/>
    /// </summary>
    /// <param name="ticks">The public CLR tick boundary.</param>
    /// <param name="minTicks">The minimum valid tick value for the CLR type.</param>
    /// <param name="previousTicks">The previous encoded-quantum tick value when one exists.</param>
    /// <returns>True when a predecessor quantum exists within the type domain.</returns>
    private static bool TryGetPreviousStructuredDateTicks(long ticks, long minTicks, DateTimeKeyEncoding encoding, out long previousTicks)
    {
        long quantum = GetStructuredDateTickQuantum(encoding);
        long remainder = ticks % quantum;
        long delta = remainder == 0
            ? quantum
            : remainder;
        if (ticks < minTicks + delta)
        {
            previousTicks = default;
            return false;
        }

        previousTicks = ticks - delta;
        return true;
    }

    private static long GetStructuredDateTickQuantum(DateTimeKeyEncoding encoding)
    {
        return encoding switch
        {
            DateTimeKeyEncoding.CalendarSdt => StructuredDateTickQuantum,
            DateTimeKeyEncoding.PrecisionSdt => 1,
            _ => throw new ArgumentOutOfRangeException(nameof(encoding), encoding, "Unknown DateTime key encoding.")
        };
    }

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

        if (type == typeof(Guid) ||
            type == typeof(decimal))
        {
            return LibraDexScalarWidth.Bytes16;
        }

        if (type == typeof(Int128) ||
            type == typeof(UInt128))
        {
            return LibraDexScalarWidth.Bytes16;
        }

        if (type == typeof(DateTime) ||
            type == typeof(DateTimeOffset) ||
            type == typeof(DateOnly) ||
            type == typeof(TimeOnly) ||
            type == typeof(TimeSpan))
        {
            return LibraDexScalarWidth.Bytes8;
        }

        if (type == typeof(bool) ||
            type == typeof(byte) ||
            type == typeof(sbyte) ||
            type == typeof(short) ||
            type == typeof(ushort) ||
            type == typeof(char) ||
            type == typeof(int) ||
            type == typeof(uint) ||
            type == typeof(long) ||
            type == typeof(ulong) ||
            type == typeof(float) ||
            type == typeof(double))
        {
            return LibraDexScalarWidth.Bytes8;
        }

        throw new NotSupportedException($"Generic LibraDex indexes do not support scalar type {type.FullName}.");
    }

    internal static ulong Encode8(T value)
    {
        return Encode8(value, DateTimeKeyEncoding.CalendarSdt);
    }

    internal static ulong Encode8(T value, DateTimeKeyEncoding dateTimeKeyEncoding)
    {
        Type type = typeof(T);
        if (type == typeof(bool))
        {
            bool typed = Unsafe.As<T, bool>(ref value);
            return typed ? 1UL : 0UL;
        }

        if (type == typeof(byte))
        {
            return Unsafe.As<T, byte>(ref value);
        }

        if (type == typeof(sbyte))
        {
            sbyte typed = Unsafe.As<T, sbyte>(ref value);
            return unchecked((ulong)((long)typed ^ long.MinValue));
        }

        if (type == typeof(short))
        {
            short typed = Unsafe.As<T, short>(ref value);
            return unchecked((ulong)((long)typed ^ long.MinValue));
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
            return unchecked((ulong)((long)typed ^ long.MinValue));
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

        if (type == typeof(float))
        {
            float typed = Unsafe.As<T, float>(ref value);
            return LibraDexOrderedFloatingCodec.Encode(typed);
        }

        if (type == typeof(double))
        {
            double typed = Unsafe.As<T, double>(ref value);
            return LibraDexOrderedFloatingCodec.Encode(typed);
        }

        if (type == typeof(DateTime))
        {
            DateTime typed = Unsafe.As<T, DateTime>(ref value);
            return LibraDexStructuredDateCodec.Encode(typed, dateTimeKeyEncoding);
        }

        if (type == typeof(DateTimeOffset))
        {
            DateTimeOffset typed = Unsafe.As<T, DateTimeOffset>(ref value);
            return LibraDexStructuredDateCodec.Encode(typed, dateTimeKeyEncoding);
        }

        if (type == typeof(DateOnly))
        {
            DateOnly typed = Unsafe.As<T, DateOnly>(ref value);
            return LibraDexStructuredDateCodec.Encode(typed, dateTimeKeyEncoding);
        }

        if (type == typeof(TimeOnly))
        {
            TimeOnly typed = Unsafe.As<T, TimeOnly>(ref value);
            return LibraDexStructuredDateCodec.Encode(typed, dateTimeKeyEncoding);
        }

        if (type == typeof(TimeSpan))
        {
            TimeSpan typed = Unsafe.As<T, TimeSpan>(ref value);
            return unchecked((ulong)(typed.Ticks ^ long.MinValue));
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

        if (type == typeof(Int128))
        {
            Int128 typed = Unsafe.As<T, Int128>(ref value);
            UInt128 bits = unchecked((UInt128)typed);
            high = ((ulong)(bits >> 64)) ^ 0x8000_0000_0000_0000UL;
            low = (ulong)bits;
            return;
        }

        if (type == typeof(UInt128))
        {
            UInt128 typed = Unsafe.As<T, UInt128>(ref value);
            high = (ulong)(typed >> 64);
            low = (ulong)typed;
            return;
        }

        if (type == typeof(decimal))
        {
            decimal typed = Unsafe.As<T, decimal>(ref value);
            LibraDexOrderedDecimalCodec.Encode(typed, out high, out low);
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
        return Decode8(encodedValue, DateTimeKeyEncoding.CalendarSdt);
    }

    internal static T Decode8(ulong encodedValue, DateTimeKeyEncoding dateTimeKeyEncoding)
    {
        Type type = typeof(T);
        if (type == typeof(bool))
        {
            bool typed = encodedValue != 0;
            return Unsafe.As<bool, T>(ref typed);
        }

        if (type == typeof(byte))
        {
            byte typed = checked((byte)encodedValue);
            return Unsafe.As<byte, T>(ref typed);
        }

        if (type == typeof(sbyte))
        {
            long numeric = unchecked((long)(encodedValue ^ (ulong)long.MinValue));
            sbyte typed = checked((sbyte)numeric);
            return Unsafe.As<sbyte, T>(ref typed);
        }

        if (type == typeof(short))
        {
            long numeric = unchecked((long)(encodedValue ^ (ulong)long.MinValue));
            short typed = checked((short)numeric);
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
            long numeric = unchecked((long)(encodedValue ^ (ulong)long.MinValue));
            int typed = checked((int)numeric);
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

        if (type == typeof(float))
        {
            float typed = LibraDexOrderedFloatingCodec.DecodeSingle(encodedValue);
            return Unsafe.As<float, T>(ref typed);
        }

        if (type == typeof(double))
        {
            double typed = LibraDexOrderedFloatingCodec.DecodeDouble(encodedValue);
            return Unsafe.As<double, T>(ref typed);
        }

        if (type == typeof(DateTime))
        {
            DateTime typed = LibraDexStructuredDateCodec.DecodeDateTime(encodedValue, dateTimeKeyEncoding);
            return Unsafe.As<DateTime, T>(ref typed);
        }

        if (type == typeof(DateTimeOffset))
        {
            DateTimeOffset typed = new(LibraDexStructuredDateCodec.DecodeDateTime(encodedValue, dateTimeKeyEncoding), TimeSpan.Zero);
            return Unsafe.As<DateTimeOffset, T>(ref typed);
        }

        if (type == typeof(DateOnly))
        {
            DateOnly typed = LibraDexStructuredDateCodec.DecodeDateOnly(encodedValue);
            return Unsafe.As<DateOnly, T>(ref typed);
        }

        if (type == typeof(TimeOnly))
        {
            TimeOnly typed = LibraDexStructuredDateCodec.DecodeTimeOnly(encodedValue, dateTimeKeyEncoding);
            return Unsafe.As<TimeOnly, T>(ref typed);
        }

        if (type == typeof(TimeSpan))
        {
            long ticks = unchecked((long)(encodedValue ^ (ulong)long.MinValue));
            TimeSpan typed = TimeSpan.FromTicks(ticks);
            return Unsafe.As<TimeSpan, T>(ref typed);
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

        if (type == typeof(Int128))
        {
            UInt128 bits = ((UInt128)(high ^ 0x8000_0000_0000_0000UL) << 64) | low;
            Int128 typed = unchecked((Int128)bits);
            return Unsafe.As<Int128, T>(ref typed);
        }

        if (type == typeof(UInt128))
        {
            UInt128 typed = ((UInt128)high << 64) | low;
            return Unsafe.As<UInt128, T>(ref typed);
        }

        if (type == typeof(decimal))
        {
            decimal typed = LibraDexOrderedDecimalCodec.Decode(high, low);
            return Unsafe.As<decimal, T>(ref typed);
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
