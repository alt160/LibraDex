namespace LibraDex;

/// <summary>
/// Adds enum operands to explicitly typed numeric condition operators.<br/>
/// The numeric selector remains authoritative because LibraDex indexes store physical scalar bytes without retaining the caller's original CLR interpretation.<br/>
/// Enum values are checked-converted to that declared scalar type before the existing condition primitive is executed.<br/>
/// </summary>
public static class LibraDexEnumNumericConditionExtensions
{
    /// <summary>
    /// Captures equality to an enum value after checked conversion to the selected scalar type.<br/>
    /// </summary>
    /// <typeparam name="TScalar">The scalar interpretation explicitly selected by the caller.<br/></typeparam>
    /// <typeparam name="TEnum">The enum operand type.<br/></typeparam>
    /// <param name="condition">The selected numeric condition operator.<br/></param>
    /// <param name="value">The enum value to match.<br/></param>
    /// <returns>A continuation for adding clauses or ending the condition.<br/></returns>
    public static LibraDexConditionContinueOrEnd EqualTo<TScalar, TEnum>(
        this LibraDexEnumCompatibleNumericConditionOperator<TScalar> condition,
        TEnum value)
        where TEnum : struct, Enum
        => Required(condition).EqualTo(LibraDexEnumScalarConverter<TScalar, TEnum>.Convert(value));

    /// <summary>
    /// Captures equality to a deferred enum value that is obtained and checked-converted when the condition is materialized.<br/>
    /// </summary>
    /// <typeparam name="TScalar">The selected scalar interpretation.<br/></typeparam>
    /// <typeparam name="TEnum">The enum operand type.<br/></typeparam>
    /// <param name="condition">The selected numeric condition operator.<br/></param>
    /// <param name="value">The enum value factory evaluated at use time.<br/></param>
    /// <returns>A continuation for adding clauses or ending the condition.<br/></returns>
    public static LibraDexConditionContinueOrEnd EqualTo<TScalar, TEnum>(
        this LibraDexEnumCompatibleNumericConditionOperator<TScalar> condition,
        Func<TEnum> value)
        where TEnum : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(value);
        return Required(condition).EqualTo(() => LibraDexEnumScalarConverter<TScalar, TEnum>.Convert(value()));
    }

    /// <summary>
    /// Captures inequality to an enum value after checked scalar conversion.<br/>
    /// </summary>
    public static LibraDexConditionContinueOrEnd NotEqualTo<TScalar, TEnum>(
        this LibraDexEnumCompatibleNumericConditionOperator<TScalar> condition,
        TEnum value)
        where TEnum : struct, Enum
        => Required(condition).NotEqualTo(LibraDexEnumScalarConverter<TScalar, TEnum>.Convert(value));

    /// <summary>
    /// Captures inequality to a deferred enum value converted when the condition is materialized.<br/>
    /// </summary>
    public static LibraDexConditionContinueOrEnd NotEqualTo<TScalar, TEnum>(
        this LibraDexEnumCompatibleNumericConditionOperator<TScalar> condition,
        Func<TEnum> value)
        where TEnum : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(value);
        return Required(condition).NotEqualTo(() => LibraDexEnumScalarConverter<TScalar, TEnum>.Convert(value()));
    }

    /// <summary>
    /// Captures an exclusive lower boundary supplied as an enum value.<br/>
    /// </summary>
    public static LibraDexConditionContinueOrEnd GreaterThan<TScalar, TEnum>(this LibraDexEnumCompatibleNumericConditionOperator<TScalar> condition, TEnum value)
        where TEnum : struct, Enum
        => Required(condition).GreaterThan(LibraDexEnumScalarConverter<TScalar, TEnum>.Convert(value));

    /// <summary>
    /// Captures a deferred exclusive lower enum boundary converted at materialization time.<br/>
    /// </summary>
    public static LibraDexConditionContinueOrEnd GreaterThan<TScalar, TEnum>(this LibraDexEnumCompatibleNumericConditionOperator<TScalar> condition, Func<TEnum> value)
        where TEnum : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(value);
        return Required(condition).GreaterThan(() => LibraDexEnumScalarConverter<TScalar, TEnum>.Convert(value()));
    }

    /// <summary>
    /// Captures an inclusive lower boundary supplied as an enum value.<br/>
    /// </summary>
    public static LibraDexConditionContinueOrEnd GreaterOrEqual<TScalar, TEnum>(this LibraDexEnumCompatibleNumericConditionOperator<TScalar> condition, TEnum value)
        where TEnum : struct, Enum
        => Required(condition).GreaterOrEqual(LibraDexEnumScalarConverter<TScalar, TEnum>.Convert(value));

    /// <summary>
    /// Captures a deferred inclusive lower enum boundary converted at materialization time.<br/>
    /// </summary>
    public static LibraDexConditionContinueOrEnd GreaterOrEqual<TScalar, TEnum>(this LibraDexEnumCompatibleNumericConditionOperator<TScalar> condition, Func<TEnum> value)
        where TEnum : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(value);
        return Required(condition).GreaterOrEqual(() => LibraDexEnumScalarConverter<TScalar, TEnum>.Convert(value()));
    }

    /// <summary>
    /// Captures an exclusive upper boundary supplied as an enum value.<br/>
    /// </summary>
    public static LibraDexConditionContinueOrEnd LessThan<TScalar, TEnum>(this LibraDexEnumCompatibleNumericConditionOperator<TScalar> condition, TEnum value)
        where TEnum : struct, Enum
        => Required(condition).LessThan(LibraDexEnumScalarConverter<TScalar, TEnum>.Convert(value));

    /// <summary>
    /// Captures a deferred exclusive upper enum boundary converted at materialization time.<br/>
    /// </summary>
    public static LibraDexConditionContinueOrEnd LessThan<TScalar, TEnum>(this LibraDexEnumCompatibleNumericConditionOperator<TScalar> condition, Func<TEnum> value)
        where TEnum : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(value);
        return Required(condition).LessThan(() => LibraDexEnumScalarConverter<TScalar, TEnum>.Convert(value()));
    }

    /// <summary>
    /// Captures an inclusive upper boundary supplied as an enum value.<br/>
    /// </summary>
    public static LibraDexConditionContinueOrEnd LessOrEqual<TScalar, TEnum>(this LibraDexEnumCompatibleNumericConditionOperator<TScalar> condition, TEnum value)
        where TEnum : struct, Enum
        => Required(condition).LessOrEqual(LibraDexEnumScalarConverter<TScalar, TEnum>.Convert(value));

    /// <summary>
    /// Captures a deferred inclusive upper enum boundary converted at materialization time.<br/>
    /// </summary>
    public static LibraDexConditionContinueOrEnd LessOrEqual<TScalar, TEnum>(this LibraDexEnumCompatibleNumericConditionOperator<TScalar> condition, Func<TEnum> value)
        where TEnum : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(value);
        return Required(condition).LessOrEqual(() => LibraDexEnumScalarConverter<TScalar, TEnum>.Convert(value()));
    }

    /// <summary>
    /// Captures an inclusive enum range after checked conversion of both boundaries.<br/>
    /// </summary>
    public static LibraDexConditionContinueOrEnd Between<TScalar, TEnum>(
        this LibraDexEnumCompatibleNumericConditionOperator<TScalar> condition,
        TEnum lower,
        TEnum upper)
        where TEnum : struct, Enum
        => Required(condition).Between(
            LibraDexEnumScalarConverter<TScalar, TEnum>.Convert(lower),
            LibraDexEnumScalarConverter<TScalar, TEnum>.Convert(upper));

    /// <summary>
    /// Captures a deferred inclusive enum range whose factories are evaluated once per materialization.<br/>
    /// </summary>
    public static LibraDexConditionContinueOrEnd Between<TScalar, TEnum>(
        this LibraDexEnumCompatibleNumericConditionOperator<TScalar> condition,
        Func<TEnum> lower,
        Func<TEnum> upper)
        where TEnum : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(lower);
        ArgumentNullException.ThrowIfNull(upper);
        return Required(condition).Between(
            () => LibraDexEnumScalarConverter<TScalar, TEnum>.Convert(lower()),
            () => LibraDexEnumScalarConverter<TScalar, TEnum>.Convert(upper()));
    }

    /// <summary>
    /// Captures exclusion of an inclusive enum range after checked conversion of both boundaries.<br/>
    /// </summary>
    public static LibraDexConditionContinueOrEnd NotBetween<TScalar, TEnum>(
        this LibraDexEnumCompatibleNumericConditionOperator<TScalar> condition,
        TEnum lower,
        TEnum upper)
        where TEnum : struct, Enum
        => Required(condition).NotBetween(
            LibraDexEnumScalarConverter<TScalar, TEnum>.Convert(lower),
            LibraDexEnumScalarConverter<TScalar, TEnum>.Convert(upper));

    /// <summary>
    /// Captures exclusion of a deferred inclusive enum range evaluated once per materialization.<br/>
    /// </summary>
    public static LibraDexConditionContinueOrEnd NotBetween<TScalar, TEnum>(
        this LibraDexEnumCompatibleNumericConditionOperator<TScalar> condition,
        Func<TEnum> lower,
        Func<TEnum> upper)
        where TEnum : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(lower);
        ArgumentNullException.ThrowIfNull(upper);
        return Required(condition).NotBetween(
            () => LibraDexEnumScalarConverter<TScalar, TEnum>.Convert(lower()),
            () => LibraDexEnumScalarConverter<TScalar, TEnum>.Convert(upper()));
    }

    /// <summary>
    /// Captures enum membership while deferring enumeration and scalar conversion until the condition is materialized.<br/>
    /// Each materialization enumerates the supplied sequence exactly once and owns a stable scalar snapshot.<br/>
    /// </summary>
    public static LibraDexConditionContinueOrEnd InSet<TScalar, TEnum>(
        this LibraDexEnumCompatibleNumericConditionOperator<TScalar> condition,
        IEnumerable<TEnum> values)
        where TEnum : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(values);
        return Required(condition).AddMaterializedMembership(
            LibraDexConditionOperatorKind.InSet,
            () => LibraDexEnumScalarConverter<TScalar, TEnum>.Materialize(values));
    }

    /// <summary>
    /// Captures enum membership from a sequence factory evaluated once when the condition is materialized.<br/>
    /// The returned sequence is enumerated exactly once into an execution-local scalar snapshot.<br/>
    /// </summary>
    public static LibraDexConditionContinueOrEnd InSet<TScalar, TEnum>(
        this LibraDexEnumCompatibleNumericConditionOperator<TScalar> condition,
        Func<IEnumerable<TEnum>> values)
        where TEnum : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(values);
        return Required(condition).AddMaterializedMembership(
            LibraDexConditionOperatorKind.InSet,
            () => LibraDexEnumScalarConverter<TScalar, TEnum>.Materialize(values()));
    }

    /// <summary>
    /// Captures enum membership using the concise alias for <see cref="InSet{TScalar, TEnum}(LibraDexEnumCompatibleNumericConditionOperator{TScalar}, IEnumerable{TEnum})"/>.<br/>
    /// Enumeration and checked scalar conversion remain deferred until each condition materialization.<br/>
    /// </summary>
    /// <typeparam name="TScalar">The scalar interpretation explicitly selected by the caller.<br/></typeparam>
    /// <typeparam name="TEnum">The enum operand type.<br/></typeparam>
    /// <param name="condition">The selected numeric condition operator.<br/></param>
    /// <param name="values">The enum sequence to materialize once per use.<br/></param>
    /// <returns>A continuation for adding clauses or ending the condition.<br/></returns>
    public static LibraDexConditionContinueOrEnd In<TScalar, TEnum>(
        this LibraDexEnumCompatibleNumericConditionOperator<TScalar> condition,
        IEnumerable<TEnum> values)
        where TEnum : struct, Enum
        => InSet(condition, values);

    /// <summary>
    /// Captures enum membership from a deferred sequence using the concise alias for <see cref="InSet{TScalar, TEnum}(LibraDexEnumCompatibleNumericConditionOperator{TScalar}, Func{IEnumerable{TEnum}})"/>.<br/>
    /// The factory and its returned sequence are each consumed once per condition materialization.<br/>
    /// </summary>
    /// <typeparam name="TScalar">The scalar interpretation explicitly selected by the caller.<br/></typeparam>
    /// <typeparam name="TEnum">The enum operand type.<br/></typeparam>
    /// <param name="condition">The selected numeric condition operator.<br/></param>
    /// <param name="values">The enum sequence factory evaluated at use time.<br/></param>
    /// <returns>A continuation for adding clauses or ending the condition.<br/></returns>
    public static LibraDexConditionContinueOrEnd In<TScalar, TEnum>(
        this LibraDexEnumCompatibleNumericConditionOperator<TScalar> condition,
        Func<IEnumerable<TEnum>> values)
        where TEnum : struct, Enum
        => InSet(condition, values);

    /// <summary>
    /// Captures enum membership using the readable alias for <see cref="InSet{TScalar, TEnum}(LibraDexEnumCompatibleNumericConditionOperator{TScalar}, IEnumerable{TEnum})"/>.<br/>
    /// Enumeration and checked scalar conversion remain deferred until each condition materialization.<br/>
    /// </summary>
    /// <typeparam name="TScalar">The scalar interpretation explicitly selected by the caller.<br/></typeparam>
    /// <typeparam name="TEnum">The enum operand type.<br/></typeparam>
    /// <param name="condition">The selected numeric condition operator.<br/></param>
    /// <param name="values">The enum sequence to materialize once per use.<br/></param>
    /// <returns>A continuation for adding clauses or ending the condition.<br/></returns>
    public static LibraDexConditionContinueOrEnd IsIn<TScalar, TEnum>(
        this LibraDexEnumCompatibleNumericConditionOperator<TScalar> condition,
        IEnumerable<TEnum> values)
        where TEnum : struct, Enum
        => InSet(condition, values);

    /// <summary>
    /// Captures enum membership from a deferred sequence using the readable alias for <see cref="InSet{TScalar, TEnum}(LibraDexEnumCompatibleNumericConditionOperator{TScalar}, Func{IEnumerable{TEnum}})"/>.<br/>
    /// The factory and its returned sequence are each consumed once per condition materialization.<br/>
    /// </summary>
    /// <typeparam name="TScalar">The scalar interpretation explicitly selected by the caller.<br/></typeparam>
    /// <typeparam name="TEnum">The enum operand type.<br/></typeparam>
    /// <param name="condition">The selected numeric condition operator.<br/></param>
    /// <param name="values">The enum sequence factory evaluated at use time.<br/></param>
    /// <returns>A continuation for adding clauses or ending the condition.<br/></returns>
    public static LibraDexConditionContinueOrEnd IsIn<TScalar, TEnum>(
        this LibraDexEnumCompatibleNumericConditionOperator<TScalar> condition,
        Func<IEnumerable<TEnum>> values)
        where TEnum : struct, Enum
        => InSet(condition, values);

    /// <summary>
    /// Captures enum membership exclusion with use-time enumeration and checked scalar conversion.<br/>
    /// </summary>
    public static LibraDexConditionContinueOrEnd NotInSet<TScalar, TEnum>(this LibraDexEnumCompatibleNumericConditionOperator<TScalar> condition, IEnumerable<TEnum> values)
        where TEnum : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(values);
        return Required(condition).AddMaterializedMembership(
            LibraDexConditionOperatorKind.NotInSet,
            () => LibraDexEnumScalarConverter<TScalar, TEnum>.Materialize(values));
    }

    /// <summary>
    /// Captures enum membership exclusion from a sequence factory evaluated once per materialization.<br/>
    /// </summary>
    public static LibraDexConditionContinueOrEnd NotInSet<TScalar, TEnum>(this LibraDexEnumCompatibleNumericConditionOperator<TScalar> condition, Func<IEnumerable<TEnum>> values)
        where TEnum : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(values);
        return Required(condition).AddMaterializedMembership(
            LibraDexConditionOperatorKind.NotInSet,
            () => LibraDexEnumScalarConverter<TScalar, TEnum>.Materialize(values()));
    }

    /// <summary>
    /// Captures enum membership exclusion using the concise alias for <see cref="NotInSet{TScalar, TEnum}(LibraDexEnumCompatibleNumericConditionOperator{TScalar}, IEnumerable{TEnum})"/>.<br/>
    /// Enumeration and checked scalar conversion remain deferred until each condition materialization.<br/>
    /// </summary>
    /// <typeparam name="TScalar">The scalar interpretation explicitly selected by the caller.<br/></typeparam>
    /// <typeparam name="TEnum">The enum operand type.<br/></typeparam>
    /// <param name="condition">The selected numeric condition operator.<br/></param>
    /// <param name="values">The enum sequence to materialize once per use.<br/></param>
    /// <returns>A continuation for adding clauses or ending the condition.<br/></returns>
    public static LibraDexConditionContinueOrEnd NotIn<TScalar, TEnum>(
        this LibraDexEnumCompatibleNumericConditionOperator<TScalar> condition,
        IEnumerable<TEnum> values)
        where TEnum : struct, Enum
        => NotInSet(condition, values);

    /// <summary>
    /// Captures enum membership exclusion from a deferred sequence using the concise alias for <see cref="NotInSet{TScalar, TEnum}(LibraDexEnumCompatibleNumericConditionOperator{TScalar}, Func{IEnumerable{TEnum}})"/>.<br/>
    /// The factory and its returned sequence are each consumed once per condition materialization.<br/>
    /// </summary>
    /// <typeparam name="TScalar">The scalar interpretation explicitly selected by the caller.<br/></typeparam>
    /// <typeparam name="TEnum">The enum operand type.<br/></typeparam>
    /// <param name="condition">The selected numeric condition operator.<br/></param>
    /// <param name="values">The enum sequence factory evaluated at use time.<br/></param>
    /// <returns>A continuation for adding clauses or ending the condition.<br/></returns>
    public static LibraDexConditionContinueOrEnd NotIn<TScalar, TEnum>(
        this LibraDexEnumCompatibleNumericConditionOperator<TScalar> condition,
        Func<IEnumerable<TEnum>> values)
        where TEnum : struct, Enum
        => NotInSet(condition, values);

    /// <summary>
    /// Captures enum membership exclusion using the readable alias for <see cref="NotInSet{TScalar, TEnum}(LibraDexEnumCompatibleNumericConditionOperator{TScalar}, IEnumerable{TEnum})"/>.<br/>
    /// Enumeration and checked scalar conversion remain deferred until each condition materialization.<br/>
    /// </summary>
    /// <typeparam name="TScalar">The scalar interpretation explicitly selected by the caller.<br/></typeparam>
    /// <typeparam name="TEnum">The enum operand type.<br/></typeparam>
    /// <param name="condition">The selected numeric condition operator.<br/></param>
    /// <param name="values">The enum sequence to materialize once per use.<br/></param>
    /// <returns>A continuation for adding clauses or ending the condition.<br/></returns>
    public static LibraDexConditionContinueOrEnd IsNotIn<TScalar, TEnum>(
        this LibraDexEnumCompatibleNumericConditionOperator<TScalar> condition,
        IEnumerable<TEnum> values)
        where TEnum : struct, Enum
        => NotInSet(condition, values);

    /// <summary>
    /// Captures enum membership exclusion from a deferred sequence using the readable alias for <see cref="NotInSet{TScalar, TEnum}(LibraDexEnumCompatibleNumericConditionOperator{TScalar}, Func{IEnumerable{TEnum}})"/>.<br/>
    /// The factory and its returned sequence are each consumed once per condition materialization.<br/>
    /// </summary>
    /// <typeparam name="TScalar">The scalar interpretation explicitly selected by the caller.<br/></typeparam>
    /// <typeparam name="TEnum">The enum operand type.<br/></typeparam>
    /// <param name="condition">The selected numeric condition operator.<br/></param>
    /// <param name="values">The enum sequence factory evaluated at use time.<br/></param>
    /// <returns>A continuation for adding clauses or ending the condition.<br/></returns>
    public static LibraDexConditionContinueOrEnd IsNotIn<TScalar, TEnum>(
        this LibraDexEnumCompatibleNumericConditionOperator<TScalar> condition,
        Func<IEnumerable<TEnum>> values)
        where TEnum : struct, Enum
        => NotInSet(condition, values);

    /// <summary>
    /// Captures an enum bitwise-AND equality predicate after checked conversion of both operands.<br/>
    /// </summary>
    public static LibraDexConditionContinueOrEnd BitAnd<TScalar, TEnum>(this LibraDexEnumCompatibleNumericConditionOperator<TScalar> condition, TEnum bitMask, TEnum equalTo)
        where TEnum : struct, Enum
        => Required(condition).BitAnd(
            LibraDexEnumScalarConverter<TScalar, TEnum>.Convert(bitMask),
            LibraDexEnumScalarConverter<TScalar, TEnum>.Convert(equalTo));

    /// <summary>
    /// Captures bitwise-AND equality with a scalar mask and enum comparison value.<br/>
    /// </summary>
    public static LibraDexConditionContinueOrEnd BitAnd<TScalar, TEnum>(this LibraDexEnumCompatibleNumericConditionOperator<TScalar> condition, TScalar bitMask, TEnum equalTo)
        where TEnum : struct, Enum
        => Required(condition).BitAnd(bitMask, LibraDexEnumScalarConverter<TScalar, TEnum>.Convert(equalTo));

    /// <summary>
    /// Captures bitwise-AND equality with an enum mask and scalar comparison value.<br/>
    /// </summary>
    public static LibraDexConditionContinueOrEnd BitAnd<TScalar, TEnum>(this LibraDexEnumCompatibleNumericConditionOperator<TScalar> condition, TEnum bitMask, TScalar equalTo)
        where TEnum : struct, Enum
        => Required(condition).BitAnd(LibraDexEnumScalarConverter<TScalar, TEnum>.Convert(bitMask), equalTo);

    /// <summary>
    /// Captures an enum bitwise-AND nonzero predicate after checked scalar conversion.<br/>
    /// </summary>
    public static LibraDexConditionContinueOrEnd BitAnd<TScalar, TEnum>(this LibraDexEnumCompatibleNumericConditionOperator<TScalar> condition, TEnum bitMask)
        where TEnum : struct, Enum
        => Required(condition).BitAnd(LibraDexEnumScalarConverter<TScalar, TEnum>.Convert(bitMask));

    /// <summary>
    /// Captures an enum bitwise-AND inequality predicate after checked conversion of both operands.<br/>
    /// </summary>
    public static LibraDexConditionContinueOrEnd BitAndNotEqualTo<TScalar, TEnum>(this LibraDexEnumCompatibleNumericConditionOperator<TScalar> condition, TEnum bitMask, TEnum notEqualTo)
        where TEnum : struct, Enum
        => Required(condition).BitAndNotEqualTo(
            LibraDexEnumScalarConverter<TScalar, TEnum>.Convert(bitMask),
            LibraDexEnumScalarConverter<TScalar, TEnum>.Convert(notEqualTo));

    /// <summary>
    /// Captures bitwise-AND inequality with a scalar mask and enum comparison value.<br/>
    /// </summary>
    public static LibraDexConditionContinueOrEnd BitAndNotEqualTo<TScalar, TEnum>(this LibraDexEnumCompatibleNumericConditionOperator<TScalar> condition, TScalar bitMask, TEnum notEqualTo)
        where TEnum : struct, Enum
        => Required(condition).BitAndNotEqualTo(bitMask, LibraDexEnumScalarConverter<TScalar, TEnum>.Convert(notEqualTo));

    /// <summary>
    /// Captures bitwise-AND inequality with an enum mask and scalar comparison value.<br/>
    /// </summary>
    public static LibraDexConditionContinueOrEnd BitAndNotEqualTo<TScalar, TEnum>(this LibraDexEnumCompatibleNumericConditionOperator<TScalar> condition, TEnum bitMask, TScalar notEqualTo)
        where TEnum : struct, Enum
        => Required(condition).BitAndNotEqualTo(LibraDexEnumScalarConverter<TScalar, TEnum>.Convert(bitMask), notEqualTo);

    /// <summary>
    /// Captures a flags-enum predicate requiring all selected bits.<br/>
    /// </summary>
    public static LibraDexConditionContinueOrEnd AllBitsSet<TScalar, TEnum>(this LibraDexEnumCompatibleNumericConditionOperator<TScalar> condition, TEnum bitMask)
        where TEnum : struct, Enum
        => Required(condition).AllBitsSet(LibraDexEnumScalarConverter<TScalar, TEnum>.Convert(bitMask));

    /// <summary>
    /// Captures a flags-enum predicate requiring at least one selected bit.<br/>
    /// </summary>
    public static LibraDexConditionContinueOrEnd AnyBitsSet<TScalar, TEnum>(this LibraDexEnumCompatibleNumericConditionOperator<TScalar> condition, TEnum bitMask)
        where TEnum : struct, Enum
        => Required(condition).AnyBitsSet(LibraDexEnumScalarConverter<TScalar, TEnum>.Convert(bitMask));

    /// <summary>
    /// Captures a flags-enum predicate requiring no selected bits.<br/>
    /// </summary>
    public static LibraDexConditionContinueOrEnd NoBitsSet<TScalar, TEnum>(this LibraDexEnumCompatibleNumericConditionOperator<TScalar> condition, TEnum bitMask)
        where TEnum : struct, Enum
        => Required(condition).NoBitsSet(LibraDexEnumScalarConverter<TScalar, TEnum>.Convert(bitMask));

    private static LibraDexEnumCompatibleNumericConditionOperator<TScalar> Required<TScalar>(LibraDexEnumCompatibleNumericConditionOperator<TScalar> condition)
        => condition ?? throw new ArgumentNullException(nameof(condition));
}

internal static class LibraDexEnumScalarConverter<TScalar, TEnum>
    where TEnum : struct, Enum
{
    private static readonly Type _scalarType = typeof(TScalar);
    private static readonly Type _enumStorageType = Enum.GetUnderlyingType(typeof(TEnum));
    private static readonly bool _enumIsUnsigned = _enumStorageType == typeof(byte) ||
        _enumStorageType == typeof(ushort) ||
        _enumStorageType == typeof(uint) ||
        _enumStorageType == typeof(ulong);

    internal static TScalar Convert(TEnum value)
    {
        object scalar = _enumIsUnsigned
            ? FromUnsigned(System.Convert.ToUInt64(value))
            : FromSigned(System.Convert.ToInt64(value));
        return (TScalar)scalar;
    }

    internal static TScalar[] Materialize(IEnumerable<TEnum> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        int capacity = values.TryGetNonEnumeratedCount(out int count) ? count : 0;
        List<TScalar> converted = capacity == 0 ? new List<TScalar>() : new List<TScalar>(capacity);
        foreach (TEnum value in values)
            converted.Add(Convert(value));

        return converted.ToArray();
    }

    private static object FromSigned(long value)
    {
        checked
        {
            if (_scalarType == typeof(sbyte)) return (sbyte)value;
            if (_scalarType == typeof(byte)) return (byte)value;
            if (_scalarType == typeof(short)) return (short)value;
            if (_scalarType == typeof(ushort)) return (ushort)value;
            if (_scalarType == typeof(int)) return (int)value;
            if (_scalarType == typeof(uint)) return (uint)value;
            if (_scalarType == typeof(long)) return value;
            if (_scalarType == typeof(ulong)) return (ulong)value;
        }

        throw UnsupportedScalarType();
    }

    private static object FromUnsigned(ulong value)
    {
        checked
        {
            if (_scalarType == typeof(sbyte)) return (sbyte)value;
            if (_scalarType == typeof(byte)) return (byte)value;
            if (_scalarType == typeof(short)) return (short)value;
            if (_scalarType == typeof(ushort)) return (ushort)value;
            if (_scalarType == typeof(int)) return (int)value;
            if (_scalarType == typeof(uint)) return (uint)value;
            if (_scalarType == typeof(long)) return (long)value;
            if (_scalarType == typeof(ulong)) return value;
        }

        throw UnsupportedScalarType();
    }

    private static NotSupportedException UnsupportedScalarType()
        => new($"Enum operands require an explicitly selected integral scalar interpretation; '{_scalarType.Name}' is not supported.");
}
