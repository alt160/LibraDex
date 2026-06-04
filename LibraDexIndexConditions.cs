namespace LibraDex;

/// <summary>
/// Represents a condition expression rooted at one typed opened index.<br/>
/// The expression adds same-index continuation helpers over the shared expression terminal surface.<br/>
/// </summary>
/// <typeparam name="TKey">The index key type.</typeparam>
/// <typeparam name="TIdentity">The index identity type.</typeparam>
public sealed class LibraDexIndexCondition<TKey, TIdentity>
{
    private readonly LibraDexIndex<TKey, TIdentity> index;

    internal LibraDexIndexCondition(LibraDexIndex<TKey, TIdentity> index, LibraDexConditionEndCondition condition)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(condition);
        this.index = index;
        Condition = condition;
    }

    /// <summary>
    /// Gets the identity group shared by every index referenced by this fluent condition state.<br/>
    /// </summary>
    public string Group => Condition.Group;

    internal LibraDexConditionEndCondition Condition { get; }

    /// <summary>
    /// Ends opened-index fluent construction and returns the completed condition descriptor accepted by terminal APIs.<br/>
    /// Until this boundary is reached the condition remains a continuation-capable grammar state, preserving Abraxas-style variablization and later `.And` / `.Or` continuation.<br/>
    /// </summary>
    public LibraDexConditionEndCondition EndCondition => Condition;

    /// <summary>
    /// Ends opened-index fluent construction and returns the reusable expression accepted by terminal APIs.<br/>
    /// This compact alias mirrors the adopted condition builder's `ec` alias for callers that prefer terse handwritten filters.<br/>
    /// </summary>
    public LibraDexConditionEndCondition ec => EndCondition;

    /// <summary>
    /// Continues this expression with another predicate over the same opened index using identity-set intersection.<br/>
    /// This keeps the current opened-index grammar compact for handwritten conditions; wider expression composition remains available through `.AndAlso(...)` without changing the eventual executor path.<br/>
    /// </summary>
    public LibraDexIndexConditionContinuation<TKey, TIdentity> And => new(this, index, useOr: false, negateNext: false);

    /// <summary>
    /// Continues this expression with another predicate over the same opened index using identity-set union.<br/>
    /// This keeps the current opened-index grammar compact for handwritten conditions; wider expression composition remains available through `.OrElse(...)` without changing the eventual executor path.<br/>
    /// </summary>
    public LibraDexIndexConditionContinuation<TKey, TIdentity> Or => new(this, index, useOr: true, negateNext: false);

    internal LibraDexIndexCondition<TKey, TIdentity> ComposeSameIndex(LibraDexIndexCondition<TKey, TIdentity> other, bool useOr)
    {
        LibraDexConditionContinueOrEnd left = LibraDexCondition.ForGroup(Group).Group(Condition);
        LibraDexConditionEndCondition composed = useOr
            ? left.OR.Group(other.Condition).EndCondition
            : left.AND.Group(other.Condition).EndCondition;
        return new LibraDexIndexCondition<TKey, TIdentity>(index, composed);
    }
}

/// <summary>
/// Starts a typed condition from an opened index handle.<br/>
/// The selected index supplies group, name, and key type information, so callers do not repeat `ForGroup(...).Index(...).AsType` for common handwritten conditions.<br/>
/// </summary>
/// <typeparam name="TKey">The index key type.</typeparam>
/// <typeparam name="TIdentity">The index identity type.</typeparam>
public sealed class LibraDexIndexWhere<TKey, TIdentity>
{
    private readonly LibraDexIndex<TKey, TIdentity> index;
    private readonly bool negate;

    internal LibraDexIndexWhere(LibraDexIndex<TKey, TIdentity> index, bool negate = false)
    {
        ArgumentNullException.ThrowIfNull(index);
        this.index = index;
        this.negate = negate;
    }

    /// <summary>
    /// Negates the next index-local predicate by mapping it to the corresponding opposite operator where the adopted condition bridge supports one.<br/>
    /// For example, `.Not.EqualTo(value)` records `NotEqualTo(value)` instead of adding a separate execution architecture.<br/>
    /// </summary>
    public LibraDexIndexWhere<TKey, TIdentity> Not => new(index, negate: !negate);

    /// <summary>
    /// Resumes same-index condition grammar from an existing completed condition.<br/>
    /// This is intended for staged builders that stored a condition fragment and later need to append another predicate over this opened index.<br/>
    /// </summary>
    /// <param name="expression">The existing expression to continue.</param>
    /// <returns>A same-index continuation rooted at the supplied expression.</returns>
    public LibraDexIndexConditionContinuation<TKey, TIdentity> Continue(LibraDexConditionEndCondition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        if (!string.Equals(condition.Group, index.Group, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The supplied condition belongs to a different LibraDex identity group.");
        }

        return new LibraDexIndexConditionContinuation<TKey, TIdentity>(
            new LibraDexIndexCondition<TKey, TIdentity>(index, condition),
            index,
            useOr: false,
            negateNext: false);
    }

    /// <summary>
    /// Resumes same-index condition grammar from an existing opened-index fluent state.<br/>
    /// This overload lets callers store a continuable partial state and later continue it without closing and reopening the expression by hand.<br/>
    /// </summary>
    /// <param name="condition">The existing fluent condition state to continue.</param>
    /// <returns>A same-index continuation rooted at the supplied fluent state.</returns>
    public LibraDexIndexConditionContinuation<TKey, TIdentity> Continue(LibraDexIndexCondition<TKey, TIdentity> condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        return Continue(condition.Condition);
    }

    /// <summary>
    /// Resumes same-index condition grammar from an existing reusable expression using union for the next predicate.<br/>
    /// This keeps staged same-index `.Or` continuation available even after a fragment has been widened to the common expression type.<br/>
    /// </summary>
    /// <param name="expression">The existing expression to continue.</param>
    /// <returns>A same-index union continuation rooted at the supplied expression.</returns>
    public LibraDexIndexConditionContinuation<TKey, TIdentity> ContinueOr(LibraDexConditionEndCondition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        if (!string.Equals(condition.Group, index.Group, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The supplied condition belongs to a different LibraDex identity group.");
        }

        return new LibraDexIndexConditionContinuation<TKey, TIdentity>(
            new LibraDexIndexCondition<TKey, TIdentity>(index, condition),
            index,
            useOr: true,
            negateNext: false);
    }

    /// <summary>
    /// Resumes same-index condition grammar from an existing opened-index fluent state using union for the next predicate.<br/>
    /// This overload keeps variablized partial states continuable while still allowing explicit `.EndCondition` when passing to terminal APIs.<br/>
    /// </summary>
    /// <param name="condition">The existing fluent condition state to continue.</param>
    /// <returns>A same-index union continuation rooted at the supplied fluent state.</returns>
    public LibraDexIndexConditionContinuation<TKey, TIdentity> ContinueOr(LibraDexIndexCondition<TKey, TIdentity> condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        return ContinueOr(condition.Condition);
    }

    /// <summary>
    /// Captures equality against the opened index key type.<br/>
    /// </summary>
    /// <param name="value">The key value to match.</param>
    /// <returns>A reusable index-rooted condition expression.</returns>
    public LibraDexIndexCondition<TKey, TIdentity> EqualTo(TKey value, string? name = null)
    {
        return Create(LibraDexConditionOperatorKind.EqualTo, LibraDexConditionOperand.Value(value, name));
    }

    /// <summary>
    /// Captures equality against an explicit null or empty key state for opened string or binary indexes.<br/>
    /// `NullKey.Null` records a null operand, `NullKey.Empty` records the key type's empty value, and `NullKey.NullOrEmpty` composes the null and empty predicates without requiring a caller-side set allocation.<br/>
    /// </summary>
    /// <param name="keyState">The key-state sentinel to match.</param>
    /// <param name="name">Optional operand name for later replacement.</param>
    /// <returns>A reusable index-rooted condition expression.</returns>
    public LibraDexIndexCondition<TKey, TIdentity> EqualTo(NullKey keyState, string? name = null)
    {
        return CreateKeyStateCondition(LibraDexConditionOperatorKind.EqualTo, keyState, name);
    }

    /// <summary>
    /// Captures equality against the stored null-key sentinel from <see cref="DBNull.Value"/> for opened string or binary indexes.<br/>
    /// This overload routes to <see cref="NullKey.Null"/> instead of treating `DBNull` as a key value.<br/>
    /// </summary>
    /// <param name="value">The database null sentinel; normally <see cref="DBNull.Value"/>.</param>
    /// <param name="name">Optional operand name for later replacement.</param>
    /// <returns>A reusable index-rooted condition expression.</returns>
    public LibraDexIndexCondition<TKey, TIdentity> EqualTo(DBNull value, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        return EqualTo(NullKey.Null, name);
    }

    /// <summary>
    /// Captures equality against a deferred opened-index key value.<br/>
    /// The value factory is evaluated when the expression is materialized, allowing the same condition object to be reused across changing request values.<br/>
    /// </summary>
    /// <param name="value">Factory that returns the key value to match.</param>
    /// <param name="name">Optional operand name for later replacement.</param>
    /// <returns>A reusable index-rooted condition expression.</returns>
    public LibraDexIndexCondition<TKey, TIdentity> EqualTo(Func<TKey> value, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Create(LibraDexConditionOperatorKind.EqualTo, LibraDexConditionOperand.Deferred(() => value(), name));
    }

    /// <summary>
    /// Captures scalar null-state equality for opened scalar indexes.<br/>
    /// `ScalarNull.Null` selects the compact null route, while `ScalarNull.NonNull` selects ordinary non-null scalar value routes.<br/>
    /// </summary>
    /// <param name="state">The scalar null-state predicate to match.</param>
    /// <returns>A reusable index-rooted condition expression.</returns>
    public LibraDexIndexCondition<TKey, TIdentity> EqualTo(ScalarNull state)
    {
        EnsureScalarNullKeyType();
        return Create(LibraDexConditionOperatorKind.ScalarNullState, LibraDexConditionOperand.Value(state));
    }

    /// <summary>
    /// Captures inequality against the opened index key type.<br/>
    /// </summary>
    /// <param name="value">The key value to exclude.</param>
    /// <returns>A reusable index-rooted condition expression.</returns>
    public LibraDexIndexCondition<TKey, TIdentity> NotEqualTo(TKey value, string? name = null)
    {
        return Create(LibraDexConditionOperatorKind.NotEqualTo, LibraDexConditionOperand.Value(value, name));
    }

    /// <summary>
    /// Captures inequality against an explicit null or empty key state for opened string or binary indexes.<br/>
    /// `NullKey.NullOrEmpty` composes non-null and non-empty predicates so both sentinels are excluded without requiring a caller-side set allocation.<br/>
    /// </summary>
    /// <param name="keyState">The key-state sentinel to exclude.</param>
    /// <param name="name">Optional operand name for later replacement.</param>
    /// <returns>A reusable index-rooted condition expression.</returns>
    public LibraDexIndexCondition<TKey, TIdentity> NotEqualTo(NullKey keyState, string? name = null)
    {
        return CreateKeyStateCondition(LibraDexConditionOperatorKind.NotEqualTo, keyState, name);
    }

    /// <summary>
    /// Captures inequality against the stored null-key sentinel from <see cref="DBNull.Value"/> for opened string or binary indexes.<br/>
    /// This overload routes to <see cref="NullKey.Null"/> instead of treating `DBNull` as a key value.<br/>
    /// </summary>
    /// <param name="value">The database null sentinel; normally <see cref="DBNull.Value"/>.</param>
    /// <param name="name">Optional operand name for later replacement.</param>
    /// <returns>A reusable index-rooted condition expression.</returns>
    public LibraDexIndexCondition<TKey, TIdentity> NotEqualTo(DBNull value, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        return NotEqualTo(NullKey.Null, name);
    }

    /// <summary>
    /// Captures scalar null-state inequality for opened scalar indexes.<br/>
    /// Inequality maps to the opposite scalar null state so execution can use the same route-state primitive.<br/>
    /// </summary>
    /// <param name="state">The scalar null-state predicate to exclude.</param>
    /// <returns>A reusable index-rooted condition expression.</returns>
    public LibraDexIndexCondition<TKey, TIdentity> NotEqualTo(ScalarNull state)
    {
        EnsureScalarNullKeyType();
        ScalarNull opposite = state switch
        {
            ScalarNull.Null => ScalarNull.NonNull,
            ScalarNull.NonNull => ScalarNull.Null,
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown scalar null state.")
        };
        return EqualTo(opposite);
    }

    /// <summary>
    /// Captures a greater-than comparison over the opened index key type.<br/>
    /// </summary>
    /// <param name="value">The exclusive lower boundary.</param>
    /// <returns>A reusable index-rooted condition expression.</returns>
    public LibraDexIndexCondition<TKey, TIdentity> GreaterThan(TKey value, string? name = null)
    {
        return Create(LibraDexConditionOperatorKind.GreaterThan, LibraDexConditionOperand.Value(value, name));
    }

    /// <summary>
    /// Captures a deferred greater-than comparison over the opened index key type.<br/>
    /// </summary>
    /// <param name="value">Factory that returns the exclusive lower boundary.</param>
    /// <param name="name">Optional operand name for later replacement.</param>
    /// <returns>A reusable index-rooted condition expression.</returns>
    public LibraDexIndexCondition<TKey, TIdentity> GreaterThan(Func<TKey> value, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Create(LibraDexConditionOperatorKind.GreaterThan, LibraDexConditionOperand.Deferred(() => value(), name));
    }

    /// <summary>
    /// Captures a greater-than-or-equal comparison over the opened index key type.<br/>
    /// </summary>
    /// <param name="value">The inclusive lower boundary.</param>
    /// <returns>A reusable index-rooted condition expression.</returns>
    public LibraDexIndexCondition<TKey, TIdentity> GreaterOrEqual(TKey value, string? name = null)
    {
        return Create(LibraDexConditionOperatorKind.GreaterOrEqual, LibraDexConditionOperand.Value(value, name));
    }

    /// <summary>
    /// Captures a deferred greater-than-or-equal comparison over the opened index key type.<br/>
    /// </summary>
    /// <param name="value">Factory that returns the inclusive lower boundary.</param>
    /// <param name="name">Optional operand name for later replacement.</param>
    /// <returns>A reusable index-rooted condition expression.</returns>
    public LibraDexIndexCondition<TKey, TIdentity> GreaterOrEqual(Func<TKey> value, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Create(LibraDexConditionOperatorKind.GreaterOrEqual, LibraDexConditionOperand.Deferred(() => value(), name));
    }

    /// <summary>
    /// Captures a less-than comparison over the opened index key type.<br/>
    /// </summary>
    /// <param name="value">The exclusive upper boundary.</param>
    /// <returns>A reusable index-rooted condition expression.</returns>
    public LibraDexIndexCondition<TKey, TIdentity> LessThan(TKey value, string? name = null)
    {
        return Create(LibraDexConditionOperatorKind.LessThan, LibraDexConditionOperand.Value(value, name));
    }

    /// <summary>
    /// Captures a deferred less-than comparison over the opened index key type.<br/>
    /// </summary>
    /// <param name="value">Factory that returns the exclusive upper boundary.</param>
    /// <param name="name">Optional operand name for later replacement.</param>
    /// <returns>A reusable index-rooted condition expression.</returns>
    public LibraDexIndexCondition<TKey, TIdentity> LessThan(Func<TKey> value, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Create(LibraDexConditionOperatorKind.LessThan, LibraDexConditionOperand.Deferred(() => value(), name));
    }

    /// <summary>
    /// Captures a less-than-or-equal comparison over the opened index key type.<br/>
    /// </summary>
    /// <param name="value">The inclusive upper boundary.</param>
    /// <returns>A reusable index-rooted condition expression.</returns>
    public LibraDexIndexCondition<TKey, TIdentity> LessOrEqual(TKey value, string? name = null)
    {
        return Create(LibraDexConditionOperatorKind.LessOrEqual, LibraDexConditionOperand.Value(value, name));
    }

    /// <summary>
    /// Captures a deferred less-than-or-equal comparison over the opened index key type.<br/>
    /// </summary>
    /// <param name="value">Factory that returns the inclusive upper boundary.</param>
    /// <param name="name">Optional operand name for later replacement.</param>
    /// <returns>A reusable index-rooted condition expression.</returns>
    public LibraDexIndexCondition<TKey, TIdentity> LessOrEqual(Func<TKey> value, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Create(LibraDexConditionOperatorKind.LessOrEqual, LibraDexConditionOperand.Deferred(() => value(), name));
    }

    /// <summary>
    /// Captures an inclusive key range over the opened index key type.<br/>
    /// </summary>
    /// <param name="lower">The inclusive lower boundary.</param>
    /// <param name="upper">The inclusive upper boundary.</param>
    /// <returns>A reusable index-rooted condition expression.</returns>
    public LibraDexIndexCondition<TKey, TIdentity> Between(TKey lower, TKey upper, string? lowerName = null, string? upperName = null)
    {
        return Create(
            LibraDexConditionOperatorKind.Between,
            LibraDexConditionOperand.Value(lower, lowerName),
            LibraDexConditionOperand.Value(upper, upperName));
    }

    /// <summary>
    /// Captures an outside-range comparison over the opened index key type.<br/>
    /// The range bounds are inclusive, so identities with keys lower than <paramref name="lower"/> or greater than <paramref name="upper"/> match.<br/>
    /// </summary>
    /// <param name="lower">The inclusive lower boundary to exclude.</param>
    /// <param name="upper">The inclusive upper boundary to exclude.</param>
    /// <param name="lowerName">Optional operand name for the lower boundary.</param>
    /// <param name="upperName">Optional operand name for the upper boundary.</param>
    /// <returns>A reusable index-rooted condition expression.</returns>
    public LibraDexIndexCondition<TKey, TIdentity> NotBetween(TKey lower, TKey upper, string? lowerName = null, string? upperName = null)
    {
        return Create(
            LibraDexConditionOperatorKind.NotBetween,
            LibraDexConditionOperand.Value(lower, lowerName),
            LibraDexConditionOperand.Value(upper, upperName));
    }

    /// <summary>
    /// Captures membership in a supplied key set.<br/>
    /// HashSet inputs are preserved as the operand object so the materializer can use the caller's set where compatible.<br/>
    /// </summary>
    /// <param name="values">The key values to match.</param>
    /// <returns>A reusable index-rooted condition expression.</returns>
    public LibraDexIndexCondition<TKey, TIdentity> InSet(IEnumerable<TKey> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        object captured = values is ISet<TKey> or IReadOnlyCollection<TKey> ? values : values.ToArray();
        return Create(LibraDexConditionOperatorKind.InSet, LibraDexConditionOperand.Value(captured));
    }

    internal LibraDexIndexCondition<TKey, TIdentity> Create(LibraDexConditionOperatorKind operatorKind, params LibraDexConditionOperand[] operands)
    {
        LibraDexConditionOperatorKind effectiveOperator = negate ? Negate(operatorKind) : operatorKind;
        LibraDexConditionBuilder builder = new(index.Group);
        LibraDexConditionEndCondition condition = builder.AddLeaf(new LibraDexConditionLeafDescriptor(
            index.Name,
            ResolveValueKind(),
            effectiveOperator,
            operands,
            IgnoreCase: false,
            Culture: null)).EndCondition;
        return new LibraDexIndexCondition<TKey, TIdentity>(index, condition);
    }

    private LibraDexIndexCondition<TKey, TIdentity> CreateKeyStateCondition(LibraDexConditionOperatorKind operatorKind, NullKey keyState, string? name)
    {
        Type keyType = typeof(TKey);
        if (keyType != typeof(string) && keyType != typeof(byte[]))
        {
            throw new NotSupportedException("Null-key state conditions are supported only for string and binary opened indexes.");
        }

        bool effectiveEquals = negate
            ? operatorKind == LibraDexConditionOperatorKind.NotEqualTo
            : operatorKind == LibraDexConditionOperatorKind.EqualTo;
        object? emptyValue = keyType == typeof(string)
            ? string.Empty
            : Array.Empty<byte>();
        return keyState switch
        {
            NullKey.Null => Create(operatorKind, LibraDexConditionOperand.Value(null, name)),
            NullKey.Empty => Create(operatorKind, LibraDexConditionOperand.Value(emptyValue, name)),
            NullKey.NullOrEmpty when effectiveEquals => Create(LibraDexConditionOperatorKind.EqualTo, LibraDexConditionOperand.Value(null, name))
                .ComposeSameIndex(new LibraDexIndexWhere<TKey, TIdentity>(index).Create(LibraDexConditionOperatorKind.EqualTo, LibraDexConditionOperand.Value(emptyValue, name)), useOr: true),
            NullKey.NullOrEmpty => Create(LibraDexConditionOperatorKind.NotEqualTo, LibraDexConditionOperand.Value(null, name))
                .ComposeSameIndex(new LibraDexIndexWhere<TKey, TIdentity>(index).Create(LibraDexConditionOperatorKind.NotEqualTo, LibraDexConditionOperand.Value(emptyValue, name)), useOr: false),
            _ => throw new ArgumentOutOfRangeException(nameof(keyState), keyState, "Unknown null-key state.")
        };
    }

    private static void EnsureScalarNullKeyType()
    {
        Type keyType = typeof(TKey);
        if (keyType == typeof(string) || keyType == typeof(byte[]))
        {
            throw new NotSupportedException("ScalarNull conditions are for scalar key families. Use NullKey for string and binary key states.");
        }
    }

    private static LibraDexConditionOperatorKind Negate(LibraDexConditionOperatorKind operatorKind)
    {
        return operatorKind switch
        {
            LibraDexConditionOperatorKind.EqualTo => LibraDexConditionOperatorKind.NotEqualTo,
            LibraDexConditionOperatorKind.NotEqualTo => LibraDexConditionOperatorKind.EqualTo,
            LibraDexConditionOperatorKind.GreaterThan => LibraDexConditionOperatorKind.LessOrEqual,
            LibraDexConditionOperatorKind.GreaterOrEqual => LibraDexConditionOperatorKind.LessThan,
            LibraDexConditionOperatorKind.LessThan => LibraDexConditionOperatorKind.GreaterOrEqual,
            LibraDexConditionOperatorKind.LessOrEqual => LibraDexConditionOperatorKind.GreaterThan,
            LibraDexConditionOperatorKind.Between => LibraDexConditionOperatorKind.NotBetween,
            LibraDexConditionOperatorKind.NotBetween => LibraDexConditionOperatorKind.Between,
            LibraDexConditionOperatorKind.InSet => LibraDexConditionOperatorKind.NotInSet,
            LibraDexConditionOperatorKind.NotInSet => LibraDexConditionOperatorKind.InSet,
            _ => throw new NotSupportedException($"Index-local negation is not supported for condition operator {operatorKind}.")
        };
    }

    private static LibraDexConditionValueKind ResolveValueKind()
    {
        Type keyType = typeof(TKey);
        if (keyType == typeof(string))
        {
            return LibraDexConditionValueKind.String;
        }

        if (keyType == typeof(byte[]))
        {
            return LibraDexConditionValueKind.Binary;
        }

        if (keyType == typeof(bool))
        {
            return LibraDexConditionValueKind.Boolean;
        }

        if (keyType == typeof(Guid))
        {
            return LibraDexConditionValueKind.Guid;
        }

        if (keyType == typeof(DateTime) || keyType == typeof(DateTimeOffset))
        {
            return LibraDexConditionValueKind.DateTime;
        }

        if (keyType == typeof(DateOnly))
        {
            return LibraDexConditionValueKind.DateOnly;
        }

        if (keyType == typeof(TimeOnly))
        {
            return LibraDexConditionValueKind.TimeOnly;
        }

        if (keyType == typeof(TimeSpan))
        {
            return LibraDexConditionValueKind.TimeSpan;
        }

        return LibraDexConditionValueKind.Numeric;
    }
}

/// <summary>
/// Continues an existing opened-index condition with another predicate over the same index.<br/>
/// This type is returned by `.And` and `.Or` and supports `.Not` before the next predicate for Abraxas-like local grammar.<br/>
/// </summary>
/// <typeparam name="TKey">The index key type.</typeparam>
/// <typeparam name="TIdentity">The index identity type.</typeparam>
public sealed class LibraDexIndexConditionContinuation<TKey, TIdentity>
{
    private readonly LibraDexIndexCondition<TKey, TIdentity> previous;
    private readonly LibraDexIndex<TKey, TIdentity> index;
    private readonly bool useOr;
    private readonly bool negateNext;

    internal LibraDexIndexConditionContinuation(
        LibraDexIndexCondition<TKey, TIdentity> previous,
        LibraDexIndex<TKey, TIdentity> index,
        bool useOr,
        bool negateNext)
    {
        this.previous = previous;
        this.index = index;
        this.useOr = useOr;
        this.negateNext = negateNext;
    }

    /// <summary>
    /// Negates the next same-index predicate before composing it with the previous expression.<br/>
    /// </summary>
    public LibraDexIndexConditionContinuation<TKey, TIdentity> Not => new(previous, index, useOr, !negateNext);

    /// <summary>
    /// Captures equality for the next same-index predicate.<br/>
    /// </summary>
    /// <param name="value">The key value to match.</param>
    /// <returns>A composed same-index condition expression.</returns>
    public LibraDexIndexCondition<TKey, TIdentity> EqualTo(TKey value)
    {
        return Compose(new LibraDexIndexWhere<TKey, TIdentity>(index, negateNext).EqualTo(value));
    }

    /// <summary>
    /// Captures equality against an explicit null or empty key state for the next same-index predicate.<br/>
    /// This keeps opened-index continuations consistent with root `.Where.EqualTo(NullKey...)` syntax for string and binary indexes.<br/>
    /// </summary>
    /// <param name="keyState">The key-state sentinel to match.</param>
    /// <returns>A composed same-index condition expression.</returns>
    public LibraDexIndexCondition<TKey, TIdentity> EqualTo(NullKey keyState)
    {
        return Compose(new LibraDexIndexWhere<TKey, TIdentity>(index, negateNext).EqualTo(keyState));
    }

    /// <summary>
    /// Captures equality against the stored null-key sentinel from <see cref="DBNull.Value"/> for the next same-index predicate.<br/>
    /// This overload routes to <see cref="NullKey.Null"/> instead of treating `DBNull` as a key value.<br/>
    /// </summary>
    /// <param name="value">The database null sentinel; normally <see cref="DBNull.Value"/>.</param>
    /// <returns>A composed same-index condition expression.</returns>
    public LibraDexIndexCondition<TKey, TIdentity> EqualTo(DBNull value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return EqualTo(NullKey.Null);
    }

    /// <summary>
    /// Captures scalar null-state equality for the next same-index predicate.<br/>
    /// This keeps continuations aligned with opened-index root `.Where.EqualTo(ScalarNull...)` syntax.<br/>
    /// </summary>
    /// <param name="state">The scalar null-state predicate to match.</param>
    /// <returns>A composed same-index condition expression.</returns>
    public LibraDexIndexCondition<TKey, TIdentity> EqualTo(ScalarNull state)
    {
        return Compose(new LibraDexIndexWhere<TKey, TIdentity>(index, negateNext).EqualTo(state));
    }

    /// <summary>
    /// Captures inequality for the next same-index predicate.<br/>
    /// </summary>
    /// <param name="value">The key value to exclude.</param>
    /// <returns>A composed same-index condition expression.</returns>
    public LibraDexIndexCondition<TKey, TIdentity> NotEqualTo(TKey value)
    {
        return Compose(new LibraDexIndexWhere<TKey, TIdentity>(index, negateNext).NotEqualTo(value));
    }

    /// <summary>
    /// Captures inequality against an explicit null or empty key state for the next same-index predicate.<br/>
    /// This keeps opened-index continuations consistent with root `.Where.NotEqualTo(NullKey...)` syntax for string and binary indexes.<br/>
    /// </summary>
    /// <param name="keyState">The key-state sentinel to exclude.</param>
    /// <returns>A composed same-index condition expression.</returns>
    public LibraDexIndexCondition<TKey, TIdentity> NotEqualTo(NullKey keyState)
    {
        return Compose(new LibraDexIndexWhere<TKey, TIdentity>(index, negateNext).NotEqualTo(keyState));
    }

    /// <summary>
    /// Captures inequality against the stored null-key sentinel from <see cref="DBNull.Value"/> for the next same-index predicate.<br/>
    /// This overload routes to <see cref="NullKey.Null"/> instead of treating `DBNull` as a key value.<br/>
    /// </summary>
    /// <param name="value">The database null sentinel; normally <see cref="DBNull.Value"/>.</param>
    /// <returns>A composed same-index condition expression.</returns>
    public LibraDexIndexCondition<TKey, TIdentity> NotEqualTo(DBNull value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return NotEqualTo(NullKey.Null);
    }

    /// <summary>
    /// Captures scalar null-state inequality for the next same-index predicate.<br/>
    /// Inequality maps to the opposite scalar null state before composition.<br/>
    /// </summary>
    /// <param name="state">The scalar null-state predicate to exclude.</param>
    /// <returns>A composed same-index condition expression.</returns>
    public LibraDexIndexCondition<TKey, TIdentity> NotEqualTo(ScalarNull state)
    {
        return Compose(new LibraDexIndexWhere<TKey, TIdentity>(index, negateNext).NotEqualTo(state));
    }

    /// <summary>
    /// Captures a greater-than comparison for the next same-index predicate.<br/>
    /// </summary>
    /// <param name="value">The exclusive lower boundary.</param>
    /// <returns>A composed same-index condition expression.</returns>
    public LibraDexIndexCondition<TKey, TIdentity> GreaterThan(TKey value)
    {
        return Compose(new LibraDexIndexWhere<TKey, TIdentity>(index, negateNext).GreaterThan(value));
    }

    /// <summary>
    /// Captures a greater-than-or-equal comparison for the next same-index predicate.<br/>
    /// </summary>
    /// <param name="value">The inclusive lower boundary.</param>
    /// <returns>A composed same-index condition expression.</returns>
    public LibraDexIndexCondition<TKey, TIdentity> GreaterOrEqual(TKey value)
    {
        return Compose(new LibraDexIndexWhere<TKey, TIdentity>(index, negateNext).GreaterOrEqual(value));
    }

    /// <summary>
    /// Captures a less-than comparison for the next same-index predicate.<br/>
    /// </summary>
    /// <param name="value">The exclusive upper boundary.</param>
    /// <returns>A composed same-index condition expression.</returns>
    public LibraDexIndexCondition<TKey, TIdentity> LessThan(TKey value)
    {
        return Compose(new LibraDexIndexWhere<TKey, TIdentity>(index, negateNext).LessThan(value));
    }

    /// <summary>
    /// Captures a less-than-or-equal comparison for the next same-index predicate.<br/>
    /// </summary>
    /// <param name="value">The inclusive upper boundary.</param>
    /// <returns>A composed same-index condition expression.</returns>
    public LibraDexIndexCondition<TKey, TIdentity> LessOrEqual(TKey value)
    {
        return Compose(new LibraDexIndexWhere<TKey, TIdentity>(index, negateNext).LessOrEqual(value));
    }

    /// <summary>
    /// Captures an inclusive key range for the next same-index predicate.<br/>
    /// </summary>
    /// <param name="lower">The inclusive lower boundary.</param>
    /// <param name="upper">The inclusive upper boundary.</param>
    /// <returns>A composed same-index condition expression.</returns>
    public LibraDexIndexCondition<TKey, TIdentity> Between(TKey lower, TKey upper)
    {
        return Compose(new LibraDexIndexWhere<TKey, TIdentity>(index, negateNext).Between(lower, upper));
    }

    /// <summary>
    /// Captures an outside-range comparison for the next same-index predicate.<br/>
    /// The range bounds are inclusive, so identities with keys lower than <paramref name="lower"/> or greater than <paramref name="upper"/> match.<br/>
    /// </summary>
    /// <param name="lower">The inclusive lower boundary to exclude.</param>
    /// <param name="upper">The inclusive upper boundary to exclude.</param>
    /// <returns>A composed same-index condition expression.</returns>
    public LibraDexIndexCondition<TKey, TIdentity> NotBetween(TKey lower, TKey upper)
    {
        return Compose(new LibraDexIndexWhere<TKey, TIdentity>(index, negateNext).NotBetween(lower, upper));
    }

    /// <summary>
    /// Captures membership for the next same-index predicate.<br/>
    /// </summary>
    /// <param name="values">The key values to match.</param>
    /// <returns>A composed same-index condition expression.</returns>
    public LibraDexIndexCondition<TKey, TIdentity> InSet(IEnumerable<TKey> values)
    {
        return Compose(new LibraDexIndexWhere<TKey, TIdentity>(index, negateNext).InSet(values));
    }

    private LibraDexIndexCondition<TKey, TIdentity> Compose(LibraDexIndexCondition<TKey, TIdentity> next)
    {
        return previous.ComposeSameIndex(next, useOr);
    }

}

/// <summary>
/// Selects the value family for an ordered `MultiKey(...)` participant.<br/>
/// The participant is selected first by ordinal through `Where(ordinal)` or `AndAlso(ordinal)`, then the caller chooses key typing or projection intent through `.AsString`, `.AsGuid`, `.AsInt64`, and related members.<br/>
/// This keeps generated and programmatic multi-key syntax aligned with catalog-group index-first condition syntax.<br/>
/// </summary>
public sealed class LibraDexMultiKeyValueTypeSelector
{
    private readonly LibraDexConditionValueTypeSelector inner;
    private readonly IIndex selectedIndex;
    private readonly IIndex[] orderedIndexes;

    internal LibraDexMultiKeyValueTypeSelector(LibraDexConditionValueTypeSelector inner, IIndex selectedIndex, IIndex[] orderedIndexes)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(selectedIndex);
        ArgumentNullException.ThrowIfNull(orderedIndexes);
        this.inner = inner;
        this.selectedIndex = selectedIndex;
        this.orderedIndexes = orderedIndexes;
    }

    /// <summary>
    /// Negates the next selected multi-key predicate over the current participant.<br/>
    /// This mirrors catalog-group `.Where(...).Not.As...` syntax while preserving the ordered participant list for later `.AndAlso(ordinal)` and `.OrElse(ordinal)` continuations.<br/>
    /// </summary>
    public LibraDexMultiKeyValueTypeSelector Not => new(inner.Not, selectedIndex, orderedIndexes);

    /// <summary>
    /// Selects string operators for the current ordered multi-key participant.<br/>
    /// The selected participant must be a string-keyed index; the check happens immediately so generated ordinal mistakes fail before materialization.<br/>
    /// </summary>
    public LibraDexMultiKeyStringWhere AsString => new(inner.AsString, ValidateKeyType(typeof(string)));

    /// <summary>
    /// Selects binary operators for the current ordered multi-key participant.<br/>
    /// The selected participant must be a byte-array-keyed index.<br/>
    /// </summary>
    public LibraDexMultiKeyBinaryWhere AsBinary => new(inner.AsBinary, ValidateKeyType(typeof(byte[])));

    /// <summary>
    /// Selects Boolean operators for the current ordered multi-key participant.<br/>
    /// The selected participant must be a Boolean-keyed index.<br/>
    /// </summary>
    public LibraDexMultiKeyScalarWhere<bool> AsBoolean => new(inner.AsBoolean, ValidateKeyType(typeof(bool)));

    /// <summary>
    /// Selects Guid operators for the current ordered multi-key participant.<br/>
    /// The selected participant must be a Guid-keyed index.<br/>
    /// </summary>
    public LibraDexMultiKeyGuidWhere AsGuid => new(inner.AsGuid, ValidateKeyType(typeof(Guid)));

    /// <summary>
    /// Selects DateTime operators for the current ordered multi-key participant.<br/>
    /// The selected participant must be a DateTime-keyed index.<br/>
    /// </summary>
    public LibraDexMultiKeyDateWhere<DateTime> AsDate => new(inner.AsDate, ValidateKeyType(typeof(DateTime)));

    /// <summary>
    /// Selects DateTimeOffset operators for the current ordered multi-key participant.<br/>
    /// The selected participant must be a DateTimeOffset-keyed index.<br/>
    /// </summary>
    public LibraDexMultiKeyDateWhere<DateTimeOffset> AsDateTimeOffset => new(inner.AsDateTimeOffset, ValidateKeyType(typeof(DateTimeOffset)));

    /// <summary>
    /// Selects DateOnly operators for the current ordered multi-key participant.<br/>
    /// The selected participant must be a DateOnly-keyed index.<br/>
    /// </summary>
    public LibraDexMultiKeyDateWhere<DateOnly> AsDateOnly => new(inner.AsDateOnly, ValidateKeyType(typeof(DateOnly)));

    /// <summary>
    /// Selects TimeOnly operators for the current ordered multi-key participant.<br/>
    /// The selected participant must be a TimeOnly-keyed index.<br/>
    /// </summary>
    public LibraDexMultiKeyDateWhere<TimeOnly> AsTimeOnly => new(inner.AsTimeOnly, ValidateKeyType(typeof(TimeOnly)));

    /// <summary>
    /// Selects TimeSpan operators for the current ordered multi-key participant.<br/>
    /// The selected participant must be a TimeSpan-keyed index.<br/>
    /// </summary>
    public LibraDexMultiKeyScalarWhere<TimeSpan> AsTimeSpan => new(inner.AsTimeSpan, ValidateKeyType(typeof(TimeSpan)));

    /// <summary>
    /// Selects Int32 operators for the current ordered multi-key participant.<br/>
    /// The selected participant must be an Int32-keyed index.<br/>
    /// </summary>
    public LibraDexMultiKeyScalarWhere<int> AsInt32 => new(inner.AsInt32, ValidateKeyType(typeof(int)));

    /// <summary>
    /// Selects Byte operators for the current ordered multi-key participant.<br/>
    /// The selected participant must be a Byte-keyed index.<br/>
    /// </summary>
    public LibraDexMultiKeyScalarWhere<byte> AsByte => new(inner.AsByte, ValidateKeyType(typeof(byte)));

    /// <summary>
    /// Selects SByte operators for the current ordered multi-key participant.<br/>
    /// The selected participant must be an SByte-keyed index.<br/>
    /// </summary>
    public LibraDexMultiKeyScalarWhere<sbyte> AsSByte => new(inner.AsSByte, ValidateKeyType(typeof(sbyte)));

    /// <summary>
    /// Selects Int16 operators for the current ordered multi-key participant.<br/>
    /// The selected participant must be an Int16-keyed index.<br/>
    /// </summary>
    public LibraDexMultiKeyScalarWhere<short> AsInt16 => new(inner.AsInt16, ValidateKeyType(typeof(short)));

    /// <summary>
    /// Selects UInt16 operators for the current ordered multi-key participant.<br/>
    /// The selected participant must be a UInt16-keyed index.<br/>
    /// </summary>
    public LibraDexMultiKeyScalarWhere<ushort> AsUInt16 => new(inner.AsUInt16, ValidateKeyType(typeof(ushort)));

    /// <summary>
    /// Selects Int64 operators for the current ordered multi-key participant.<br/>
    /// The selected participant must be an Int64-keyed index.<br/>
    /// </summary>
    public LibraDexMultiKeyScalarWhere<long> AsInt64 => new(inner.AsInt64, ValidateKeyType(typeof(long)));

    /// <summary>
    /// Selects UInt32 operators for the current ordered multi-key participant.<br/>
    /// The selected participant must be a UInt32-keyed index.<br/>
    /// </summary>
    public LibraDexMultiKeyScalarWhere<uint> AsUInt32 => new(inner.AsUInt32, ValidateKeyType(typeof(uint)));

    /// <summary>
    /// Selects UInt64 operators for the current ordered multi-key participant.<br/>
    /// The selected participant must be a UInt64-keyed index.<br/>
    /// </summary>
    public LibraDexMultiKeyScalarWhere<ulong> AsUInt64 => new(inner.AsUInt64, ValidateKeyType(typeof(ulong)));

    /// <summary>
    /// Selects Int128 operators for the current ordered multi-key participant.<br/>
    /// The selected participant must be an Int128-keyed index.<br/>
    /// </summary>
    public LibraDexMultiKeyScalarWhere<Int128> AsInt128 => new(inner.AsInt128, ValidateKeyType(typeof(Int128)));

    /// <summary>
    /// Selects UInt128 operators for the current ordered multi-key participant.<br/>
    /// The selected participant must be a UInt128-keyed index.<br/>
    /// </summary>
    public LibraDexMultiKeyScalarWhere<UInt128> AsUInt128 => new(inner.AsUInt128, ValidateKeyType(typeof(UInt128)));

    /// <summary>
    /// Selects BigInteger operators for the current ordered multi-key participant.<br/>
    /// The selected participant must be a BigInteger-keyed index.<br/>
    /// </summary>
    public LibraDexMultiKeyScalarWhere<System.Numerics.BigInteger> AsBigInteger => new(inner.AsBigInteger, ValidateKeyType(typeof(System.Numerics.BigInteger)));

    /// <summary>
    /// Selects Char operators for the current ordered multi-key participant.<br/>
    /// The selected participant must be a Char-keyed index.<br/>
    /// </summary>
    public LibraDexMultiKeyScalarWhere<char> AsChar => new(inner.AsChar, ValidateKeyType(typeof(char)));

    private IIndex[] ValidateKeyType(Type expectedKeyType)
    {
        if (selectedIndex.KeyType != expectedKeyType)
        {
            throw new ArgumentException($"Index '{selectedIndex.Name}' has key type {selectedIndex.KeyType.FullName}, but this selector requires {expectedKeyType.FullName}.");
        }

        return orderedIndexes;
    }
}

/// <summary>
/// Continues a group-level multi-key condition after one predicate has been captured.<br/>
/// Ordered multi-key continuations select the next participant by ordinal through `.AndAlso(ordinal)` or `.OrElse(ordinal)`, then select value family through `.AsString`, `.AsGuid`, `.AsInt64`, and related members.<br/>
/// </summary>
public sealed class LibraDexMultiKeyContinuation
{
    private readonly string group;
    private readonly LibraDexConditionContinueOrEnd continuation;
    private readonly IIndex[]? orderedIndexes;

    internal LibraDexMultiKeyContinuation(string group, LibraDexConditionContinueOrEnd continuation, IIndex[]? orderedIndexes)
    {
        this.group = group;
        this.continuation = continuation;
        this.orderedIndexes = orderedIndexes;
    }

    /// <summary>
    /// Creates the next multi-key continuation from one captured condition operation.<br/>
    /// This keeps scalar, string, Guid, binary, and date wrappers on one continuation factory instead of repeating wrapper-local plumbing.<br/>
    /// </summary>
    /// <param name="continuation">The captured adopted condition continuation.</param>
    /// <param name="orderedIndexes">The optional ordered index list used by ordinal selectors.</param>
    /// <returns>A multi-key continuation over the same normalized condition tree.</returns>
    internal static LibraDexMultiKeyContinuation From(LibraDexConditionContinueOrEnd continuation, IIndex[]? orderedIndexes)
    {
        return new LibraDexMultiKeyContinuation(continuation.EndCondition.Group, continuation, orderedIndexes);
    }

    /// <summary>
    /// Adds an identity-set intersection and selects the next ordered multi-key participant by ordinal.<br/>
    /// Key typing and projection intent are chosen after the ordinal through members such as `.AsString`, `.AsGuid`, and `.AsInt64`.<br/>
    /// </summary>
    /// <param name="ordinal">The zero-based participant ordinal supplied to `MultiKey(...)`.</param>
    /// <returns>A value-family selector for the selected participant.</returns>
    public LibraDexMultiKeyValueTypeSelector AndAlso(int ordinal)
    {
        return SelectOrdinal(continuation.AND, ordinal);
    }

    /// <summary>
    /// Adds an identity-set intersection and selects the next participant by index name.<br/>
    /// This keeps ordered `MultiKey(...)` chains compatible with generated code that sometimes switches from ordinal-selected participants to a known named index in the same identity group.<br/>
    /// </summary>
    /// <param name="indexName">The next index name inside the same identity group.</param>
    /// <returns>A value-family selector for the selected participant.</returns>
    public LibraDexConditionValueTypeSelector AndAlso(string indexName)
    {
        return continuation.AndAlso(indexName);
    }

    /// <summary>
    /// Adds an identity-set intersection and selects the next participant from an opened index handle.<br/>
    /// The handle is validated by the underlying group continuation, preserving the same group-safety checks as catalog conditions.<br/>
    /// </summary>
    /// <param name="index">The opened index instance to select for the next predicate.</param>
    /// <returns>A value-family selector for the selected participant.</returns>
    public LibraDexConditionValueTypeSelector AndAlso(IIndex index)
    {
        return continuation.AndAlso(index);
    }

    /// <summary>
    /// Adds an identity-set union and selects the next ordered multi-key participant by ordinal.<br/>
    /// Key typing and projection intent are chosen after the ordinal through members such as `.AsString`, `.AsGuid`, and `.AsInt64`.<br/>
    /// </summary>
    /// <param name="ordinal">The zero-based participant ordinal supplied to `MultiKey(...)`.</param>
    /// <returns>A value-family selector for the selected participant.</returns>
    public LibraDexMultiKeyValueTypeSelector OrElse(int ordinal)
    {
        return SelectOrdinal(continuation.OR, ordinal);
    }

    /// <summary>
    /// Adds an identity-set union and selects the next participant by index name.<br/>
    /// This mirrors catalog-group `.OrElse(indexName)` for mixed generated/manual condition assembly; callers that need later ordinal continuation should keep using ordinal selectors.<br/>
    /// </summary>
    /// <param name="indexName">The next index name inside the same identity group.</param>
    /// <returns>A value-family selector for the selected participant.</returns>
    public LibraDexConditionValueTypeSelector OrElse(string indexName)
    {
        return continuation.OrElse(indexName);
    }

    /// <summary>
    /// Adds an identity-set union and selects the next participant from an opened index handle.<br/>
    /// The handle is validated by the underlying group continuation, preserving the same group-safety checks as catalog conditions.<br/>
    /// </summary>
    /// <param name="index">The opened index instance to select for the next predicate.</param>
    /// <returns>A value-family selector for the selected participant.</returns>
    public LibraDexConditionValueTypeSelector OrElse(IIndex index)
    {
        return continuation.OrElse(index);
    }

    /// <summary>
    /// Completes the multi-key/group fluent construction and returns the condition descriptor accepted by terminal APIs.<br/>
    /// This mirrors the raw condition builder and opened-index roots so `.EndCondition` is the single final terminator across condition construction.<br/>
    /// </summary>
    public LibraDexConditionEndCondition EndCondition => continuation.EndCondition;

    /// <summary>
    /// Convenience alias for <see cref="EndCondition"/> that matches Abraxas' short `ec` alias.<br/>
    /// </summary>
    public LibraDexConditionEndCondition ec => EndCondition;

    private LibraDexMultiKeyValueTypeSelector SelectOrdinal(LibraDexConditionClause clause, int ordinal)
    {
        if (orderedIndexes is null)
        {
            throw new InvalidOperationException("Ordinal selectors require a condition started from MultiKey(...).");
        }

        if ((uint)ordinal >= (uint)orderedIndexes.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal), "The multi-key ordinal is outside the participant list.");
        }

        IIndex index = orderedIndexes[ordinal];
        return new LibraDexMultiKeyValueTypeSelector(clause.Index(index.Name), index, orderedIndexes);
    }
}

/// <summary>
/// Provides scalar operators for one selected group-level index.<br/>
/// </summary>
/// <typeparam name="TValue">The key value type selected by the caller.</typeparam>
public sealed class LibraDexMultiKeyScalarWhere<TValue>
{
    private readonly LibraDexConditionOperator<TValue> inner;
    private readonly IIndex[]? orderedIndexes;

    internal LibraDexMultiKeyScalarWhere(LibraDexConditionOperator<TValue> inner, IIndex[]? orderedIndexes)
    {
        this.inner = inner;
        this.orderedIndexes = orderedIndexes;
    }

    /// <summary>
    /// Captures equality against the selected index.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation EqualTo(TValue value) => LibraDexMultiKeyContinuation.From(inner.EqualTo(value), orderedIndexes);

    /// <summary>
    /// Captures scalar null-state equality against the selected index.<br/>
    /// This keeps ordered MultiKey scalar participants aligned with catalog and opened-index scalar-null routing through `ScalarNull.Null` and `ScalarNull.NonNull`.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation EqualTo(ScalarNull state) => LibraDexMultiKeyContinuation.From(inner.EqualTo(state), orderedIndexes);

    /// <summary>
    /// Captures inequality against the selected index.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation NotEqualTo(TValue value) => LibraDexMultiKeyContinuation.From(inner.NotEqualTo(value), orderedIndexes);

    /// <summary>
    /// Captures scalar null-state inequality against the selected index.<br/>
    /// Inequality maps to the opposite scalar null route, avoiding a caller-side complement or allocation-heavy sentinel collection.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation NotEqualTo(ScalarNull state) => LibraDexMultiKeyContinuation.From(inner.NotEqualTo(state), orderedIndexes);

    /// <summary>
    /// Captures a greater-than comparison against the selected index.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation GreaterThan(TValue value) => LibraDexMultiKeyContinuation.From(inner.GreaterThan(value), orderedIndexes);

    /// <summary>
    /// Captures a greater-than-or-equal comparison against the selected index.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation GreaterOrEqual(TValue value) => LibraDexMultiKeyContinuation.From(inner.GreaterOrEqual(value), orderedIndexes);

    /// <summary>
    /// Captures a less-than comparison against the selected index.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation LessThan(TValue value) => LibraDexMultiKeyContinuation.From(inner.LessThan(value), orderedIndexes);

    /// <summary>
    /// Captures a less-than-or-equal comparison against the selected index.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation LessOrEqual(TValue value) => LibraDexMultiKeyContinuation.From(inner.LessOrEqual(value), orderedIndexes);

    /// <summary>
    /// Captures an inclusive range against the selected index.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation Between(TValue lower, TValue upper) => LibraDexMultiKeyContinuation.From(inner.Between(lower, upper), orderedIndexes);

    /// <summary>
    /// Captures an outside-range comparison against the selected index.<br/>
    /// The range bounds are inclusive, so identities with keys lower than <paramref name="lower"/> or greater than <paramref name="upper"/> match.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation NotBetween(TValue lower, TValue upper) => LibraDexMultiKeyContinuation.From(inner.NotBetween(lower, upper), orderedIndexes);

    /// <summary>
    /// Captures membership against the selected index from any enumerable value list.<br/>
    /// Non-set enumerables are captured by the adopted materializer and can be normalized at execution time.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation In(IEnumerable<TValue> values) => LibraDexMultiKeyContinuation.From(inner.In(values), orderedIndexes);

    /// <summary>
    /// Captures membership against the selected index from a caller-supplied set-shaped value collection.<br/>
    /// Compatible set instances can be used as supplied by the execution bridge rather than forcing serialization or rebuild at condition-construction time.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation InSet(IEnumerable<TValue> values) => LibraDexMultiKeyContinuation.From(inner.InSet(values), orderedIndexes);
}

/// <summary>
/// Provides text operators for one selected group-level string index.<br/>
/// </summary>
public sealed class LibraDexMultiKeyStringWhere
{
    private readonly LibraDexStringConditionOperator inner;
    private readonly IIndex[]? orderedIndexes;

    internal LibraDexMultiKeyStringWhere(LibraDexStringConditionOperator inner, IIndex[]? orderedIndexes)
    {
        this.inner = inner;
        this.orderedIndexes = orderedIndexes;
    }

    /// <summary>
    /// Captures string equality against the selected index.<br/>
    /// Case and culture options are recorded in the condition descriptor so execution can choose an accelerated folded or sort-key projection when available, or a scoped comparison fallback when it is not.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation EqualTo(string? value, bool ignoreCase = false, string? culture = null) => LibraDexMultiKeyContinuation.From(inner.EqualTo(value, ignoreCase, culture), orderedIndexes);

    /// <summary>
    /// Captures string equality against an explicit null or empty key state.<br/>
    /// This forwards to the selected string operator so group-level and ordered multikey syntax share the same null/empty sentinel semantics.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation EqualTo(NullKey keyState) => LibraDexMultiKeyContinuation.From(inner.EqualTo(keyState), orderedIndexes);

    /// <summary>
    /// Captures string equality against the stored null-key sentinel from <see cref="DBNull.Value"/>.<br/>
    /// This forwards to <see cref="NullKey.Null"/> instead of treating `DBNull` as a string operand.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation EqualTo(DBNull value) => LibraDexMultiKeyContinuation.From(inner.EqualTo(value), orderedIndexes);

    /// <summary>
    /// Captures string inequality against the selected index.<br/>
    /// The selector remains index-bound while the supplied value is recorded as the operand for later materialization.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation NotEqualTo(string? value, bool ignoreCase = false, string? culture = null) => LibraDexMultiKeyContinuation.From(inner.NotEqualTo(value, ignoreCase, culture), orderedIndexes);

    /// <summary>
    /// Captures string inequality against an explicit null or empty key state.<br/>
    /// This forwards to the selected string operator so group-level and ordered multikey syntax share the same null/empty sentinel semantics.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation NotEqualTo(NullKey keyState) => LibraDexMultiKeyContinuation.From(inner.NotEqualTo(keyState), orderedIndexes);

    /// <summary>
    /// Captures string inequality against the stored null-key sentinel from <see cref="DBNull.Value"/>.<br/>
    /// This forwards to <see cref="NullKey.Null"/> instead of treating `DBNull` as a string operand.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation NotEqualTo(DBNull value) => LibraDexMultiKeyContinuation.From(inner.NotEqualTo(value), orderedIndexes);

    /// <summary>
    /// Captures a prefix condition against the selected string index.<br/>
    /// Execution may use normal routing, folded routing, sort-key routing, or scoped fallback according to the index profile and requested comparison policy.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation StartsWith(string value, bool ignoreCase = false, string? culture = null) => LibraDexMultiKeyContinuation.From(inner.StartsWith(value, ignoreCase, culture), orderedIndexes);

    /// <summary>
    /// Captures a suffix condition against the selected string index.<br/>
    /// Reversed subindexes can accelerate this operator, while missing projections still leave the condition valid for scoped scan execution.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation EndsWith(string value, bool ignoreCase = false, string? culture = null) => LibraDexMultiKeyContinuation.From(inner.EndsWith(value, ignoreCase, culture), orderedIndexes);

    /// <summary>
    /// Captures a substring condition against the selected string index.<br/>
    /// This remains a first-class condition shape even when the best execution path is scan-like rather than route-exact.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation Contains(string value, bool ignoreCase = false, string? culture = null) => LibraDexMultiKeyContinuation.From(inner.Contains(value, ignoreCase, culture), orderedIndexes);

    /// <summary>
    /// Captures a pattern condition against the selected string index.<br/>
    /// The condition descriptor preserves pattern intent so execution can apply any available routing prefix before falling back to pattern evaluation.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation MatchesPattern(string pattern, bool ignoreCase = false, string? culture = null) => LibraDexMultiKeyContinuation.From(inner.MatchesPattern(pattern, ignoreCase, culture), orderedIndexes);

    /// <summary>
    /// Captures a pattern condition against the selected string index using the short public spelling.<br/>
    /// This is the preferred alias for <see cref="MatchesPattern(string, bool, string?)"/> and preserves the same optional case and culture metadata.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation Matches(string pattern, bool ignoreCase = false, string? culture = null) => LibraDexMultiKeyContinuation.From(inner.Matches(pattern, ignoreCase, culture), orderedIndexes);

    /// <summary>
    /// Captures a regex whole-match value comparison against the selected string index.<br/>
    /// This forwards to the selected string operator so group-level and ordered multikey syntax share the same regex capture semantics.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation MatchesWith(string pattern, string value, bool ignoreCase = false, string? culture = null) => LibraDexMultiKeyContinuation.From(inner.MatchesWith(pattern, value, ignoreCase, culture), orderedIndexes);

    /// <summary>
    /// Captures a regex numbered-group value comparison against the selected string index.<br/>
    /// Group zero compares the whole regex match; positive values compare `Regex.Match(...).Groups(groupNumber).Value`.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation MatchesWith(string pattern, string value, int groupNumber, bool ignoreCase = false, string? culture = null) => LibraDexMultiKeyContinuation.From(inner.MatchesWith(pattern, value, groupNumber, ignoreCase, culture), orderedIndexes);

    /// <summary>
    /// Captures a negated regex whole-match value comparison against the selected string index.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation NotMatchesWith(string pattern, string value, bool ignoreCase = false, string? culture = null) => LibraDexMultiKeyContinuation.From(inner.NotMatchesWith(pattern, value, ignoreCase, culture), orderedIndexes);

    /// <summary>
    /// Captures a negated regex numbered-group value comparison against the selected string index.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation NotMatchesWith(string pattern, string value, int groupNumber, bool ignoreCase = false, string? culture = null) => LibraDexMultiKeyContinuation.From(inner.NotMatchesWith(pattern, value, groupNumber, ignoreCase, culture), orderedIndexes);

    /// <summary>
    /// Captures regex whole-match membership against the selected string index.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation MatchesIn(string pattern, IEnumerable<string> values, bool ignoreCase = false, string? culture = null) => LibraDexMultiKeyContinuation.From(inner.MatchesIn(pattern, values, ignoreCase, culture), orderedIndexes);

    /// <summary>
    /// Captures regex numbered-group membership against the selected string index.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation MatchesIn(string pattern, IEnumerable<string> values, int groupNumber, bool ignoreCase = false, string? culture = null) => LibraDexMultiKeyContinuation.From(inner.MatchesIn(pattern, values, groupNumber, ignoreCase, culture), orderedIndexes);

    /// <summary>
    /// Captures regex whole-match membership against the selected string index using set terminology.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation MatchesInSet(string pattern, IEnumerable<string> values, bool ignoreCase = false, string? culture = null) => LibraDexMultiKeyContinuation.From(inner.MatchesInSet(pattern, values, ignoreCase, culture), orderedIndexes);

    /// <summary>
    /// Captures regex numbered-group membership against the selected string index using set terminology.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation MatchesInSet(string pattern, IEnumerable<string> values, int groupNumber, bool ignoreCase = false, string? culture = null) => LibraDexMultiKeyContinuation.From(inner.MatchesInSet(pattern, values, groupNumber, ignoreCase, culture), orderedIndexes);

    /// <summary>
    /// Captures negated regex whole-match membership against the selected string index.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation NotMatchesIn(string pattern, IEnumerable<string> values, bool ignoreCase = false, string? culture = null) => LibraDexMultiKeyContinuation.From(inner.NotMatchesIn(pattern, values, ignoreCase, culture), orderedIndexes);

    /// <summary>
    /// Captures negated regex numbered-group membership against the selected string index.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation NotMatchesIn(string pattern, IEnumerable<string> values, int groupNumber, bool ignoreCase = false, string? culture = null) => LibraDexMultiKeyContinuation.From(inner.NotMatchesIn(pattern, values, groupNumber, ignoreCase, culture), orderedIndexes);

    /// <summary>
    /// Captures negated regex whole-match membership against the selected string index using set terminology.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation NotMatchesInSet(string pattern, IEnumerable<string> values, bool ignoreCase = false, string? culture = null) => LibraDexMultiKeyContinuation.From(inner.NotMatchesInSet(pattern, values, ignoreCase, culture), orderedIndexes);

    /// <summary>
    /// Captures negated regex numbered-group membership against the selected string index using set terminology.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation NotMatchesInSet(string pattern, IEnumerable<string> values, int groupNumber, bool ignoreCase = false, string? culture = null) => LibraDexMultiKeyContinuation.From(inner.NotMatchesInSet(pattern, values, groupNumber, ignoreCase, culture), orderedIndexes);

    /// <summary>
    /// Captures string membership from an enumerable value list.<br/>
    /// Enumerable inputs remain low-friction for callers and can be normalized to a set by the execution bridge when that improves repeated matching.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation In(IEnumerable<string> values, bool ignoreCase = false, string? culture = null) => LibraDexMultiKeyContinuation.From(inner.In(values, ignoreCase, culture), orderedIndexes);

    /// <summary>
    /// Captures string membership from a set-shaped value collection.<br/>
    /// Compatible caller-supplied sets can be preserved as-is so comparer policy is not rebuilt unnecessarily.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation InSet(IEnumerable<string> values, bool ignoreCase = false, string? culture = null) => LibraDexMultiKeyContinuation.From(inner.InSet(values, ignoreCase, culture), orderedIndexes);
}

/// <summary>
/// Provides Guid operators for one selected group-level Guid index.<br/>
/// </summary>
public sealed class LibraDexMultiKeyGuidWhere
{
    private readonly LibraDexGuidConditionOperator inner;
    private readonly IIndex[]? orderedIndexes;

    internal LibraDexMultiKeyGuidWhere(LibraDexGuidConditionOperator inner, IIndex[]? orderedIndexes)
    {
        this.inner = inner;
        this.orderedIndexes = orderedIndexes;
    }

    /// <summary>
    /// Captures Guid equality against the selected index.<br/>
    /// Guid values remain binary-domain operands; callers that want string Guid behavior should store a string index explicitly.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation EqualTo(Guid value) => LibraDexMultiKeyContinuation.From(inner.EqualTo(value), orderedIndexes);

    /// <summary>
    /// Captures Guid equality from canonical text input.<br/>
    /// The text is parsed once when the condition is built, then execution uses the binary Guid key domain.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation EqualTo(string value) => LibraDexMultiKeyContinuation.From(inner.EqualTo(value), orderedIndexes);

    /// <summary>
    /// Captures Guid inequality against the selected index.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation NotEqualTo(Guid value) => LibraDexMultiKeyContinuation.From(inner.NotEqualTo(value), orderedIndexes);

    /// <summary>
    /// Captures Guid inequality from canonical text input.<br/>
    /// The text is parsed once when the condition is built, then execution uses the binary Guid key domain.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation NotEqualTo(string value) => LibraDexMultiKeyContinuation.From(inner.NotEqualTo(value), orderedIndexes);

    /// <summary>
    /// Captures a canonical Guid text-prefix condition against the selected Guid index.<br/>
    /// This overload keeps prefix intent explicit without making the selector overload itself look like a condition value overload.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation StartsWith(string guidTextPrefix) => LibraDexMultiKeyContinuation.From(inner.StartsWith(guidTextPrefix), orderedIndexes);

    /// <summary>
    /// Captures a stored-byte Guid prefix condition against the selected Guid index.<br/>
    /// The byte order is the same order produced by `Guid.TryWriteBytes`, matching the binary representation indexed by LibraDex.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation StartsWith(byte[] guidBytePrefix) => LibraDexMultiKeyContinuation.From(inner.StartsWith(guidBytePrefix), orderedIndexes);

    /// <summary>
    /// Captures a canonical Guid text-suffix condition against the selected Guid index.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation EndsWith(string guidTextSuffix) => LibraDexMultiKeyContinuation.From(inner.EndsWith(guidTextSuffix), orderedIndexes);

    /// <summary>
    /// Captures a stored-byte Guid suffix condition against the selected Guid index.<br/>
    /// The byte order is the same order produced by `Guid.TryWriteBytes`, matching the binary representation indexed by LibraDex.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation EndsWith(byte[] guidByteSuffix) => LibraDexMultiKeyContinuation.From(inner.EndsWith(guidByteSuffix), orderedIndexes);

    /// <summary>
    /// Captures a canonical Guid text containment condition against the selected Guid index.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation Contains(string guidText) => LibraDexMultiKeyContinuation.From(inner.Contains(guidText), orderedIndexes);

    /// <summary>
    /// Captures a stored-byte Guid containment condition against the selected Guid index.<br/>
    /// The byte order is the same order produced by `Guid.TryWriteBytes`, matching the binary representation indexed by LibraDex.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation Contains(byte[] guidBytes) => LibraDexMultiKeyContinuation.From(inner.Contains(guidBytes), orderedIndexes);

    /// <summary>
    /// Captures a canonical Guid text pattern condition against the selected Guid index.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation MatchesPattern(string pattern) => LibraDexMultiKeyContinuation.From(inner.MatchesPattern(pattern), orderedIndexes);

    /// <summary>
    /// Captures a canonical Guid text pattern condition using the short public spelling.<br/>
    /// This is the preferred alias for <see cref="MatchesPattern(string)"/>.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation Matches(string pattern) => LibraDexMultiKeyContinuation.From(inner.Matches(pattern), orderedIndexes);

    /// <summary>
    /// Captures a full stored-byte Guid pattern condition against the selected Guid index.<br/>
    /// The byte order is the same order produced by `Guid.TryWriteBytes`, and every nibble is compared.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation MatchesPattern(byte[] pattern) => LibraDexMultiKeyContinuation.From(inner.MatchesPattern(pattern), orderedIndexes);

    /// <summary>
    /// Captures a full stored-byte Guid pattern condition using the short public spelling.<br/>
    /// This is the preferred alias for <see cref="MatchesPattern(byte[])"/>.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation Matches(byte[] pattern) => LibraDexMultiKeyContinuation.From(inner.Matches(pattern), orderedIndexes);
}

/// <summary>
/// Provides binary operators for one selected group-level byte-array index.<br/>
/// </summary>
public sealed class LibraDexMultiKeyBinaryWhere
{
    private readonly LibraDexBinaryConditionOperator inner;
    private readonly IIndex[]? orderedIndexes;

    internal LibraDexMultiKeyBinaryWhere(LibraDexBinaryConditionOperator inner, IIndex[]? orderedIndexes)
    {
        this.inner = inner;
        this.orderedIndexes = orderedIndexes;
    }

    /// <summary>
    /// Captures exact byte-array equality against the selected binary index.<br/>
    /// The supplied byte array is captured by the adopted descriptor and validated against the resolved binary key contract at materialization.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation EqualTo(byte[] value) => LibraDexMultiKeyContinuation.From(inner.EqualTo(value), orderedIndexes);

    /// <summary>
    /// Captures binary equality against an explicit null or empty key state.<br/>
    /// This forwards to the selected binary operator so group-level and ordered multikey syntax share the same null/empty sentinel semantics.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation EqualTo(NullKey keyState) => LibraDexMultiKeyContinuation.From(inner.EqualTo(keyState), orderedIndexes);

    /// <summary>
    /// Captures binary equality against the stored null-key sentinel from <see cref="DBNull.Value"/>.<br/>
    /// This forwards to <see cref="NullKey.Null"/> instead of treating `DBNull` as a byte-array operand.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation EqualTo(DBNull value) => LibraDexMultiKeyContinuation.From(inner.EqualTo(value), orderedIndexes);

    /// <summary>
    /// Captures byte-array inequality against the selected binary index.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation NotEqualTo(byte[] value) => LibraDexMultiKeyContinuation.From(inner.NotEqualTo(value), orderedIndexes);

    /// <summary>
    /// Captures binary inequality against an explicit null or empty key state.<br/>
    /// This forwards to the selected binary operator so group-level and ordered multikey syntax share the same null/empty sentinel semantics.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation NotEqualTo(NullKey keyState) => LibraDexMultiKeyContinuation.From(inner.NotEqualTo(keyState), orderedIndexes);

    /// <summary>
    /// Captures binary inequality against the stored null-key sentinel from <see cref="DBNull.Value"/>.<br/>
    /// This forwards to <see cref="NullKey.Null"/> instead of treating `DBNull` as a byte-array operand.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation NotEqualTo(DBNull value) => LibraDexMultiKeyContinuation.From(inner.NotEqualTo(value), orderedIndexes);

    /// <summary>
    /// Captures a raw byte-prefix condition against the selected binary index.<br/>
    /// Execution reads encoded key bytes directly and does not decode the whole key for each candidate.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation StartsWith(byte[] value) => LibraDexMultiKeyContinuation.From(inner.StartsWith(value), orderedIndexes);

    /// <summary>
    /// Captures a raw byte-prefix condition from a readable hexadecimal pattern.<br/>
    /// Hex digits select nibbles, `x` or `X` select wildcard nibbles, and common separators are ignored before execution compares stored key bytes directly.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation StartsWithHex(string hexPattern) => LibraDexMultiKeyContinuation.From(inner.StartsWithHex(hexPattern), orderedIndexes);

    /// <summary>
    /// Captures a raw byte-suffix condition against the selected binary index.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation EndsWith(byte[] value) => LibraDexMultiKeyContinuation.From(inner.EndsWith(value), orderedIndexes);

    /// <summary>
    /// Captures a raw byte-suffix condition from a readable hexadecimal pattern.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation EndsWithHex(string hexPattern) => LibraDexMultiKeyContinuation.From(inner.EndsWithHex(hexPattern), orderedIndexes);

    /// <summary>
    /// Captures a raw byte-containment condition against the selected binary index.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation Contains(byte[] value) => LibraDexMultiKeyContinuation.From(inner.Contains(value), orderedIndexes);

    /// <summary>
    /// Captures a raw byte-containment condition from a readable hexadecimal pattern.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation ContainsHex(string hexPattern) => LibraDexMultiKeyContinuation.From(inner.ContainsHex(hexPattern), orderedIndexes);

    /// <summary>
    /// Captures a full fixed-key byte pattern from a readable hexadecimal pattern.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation MatchesHexPattern(string hexPattern) => LibraDexMultiKeyContinuation.From(inner.MatchesHexPattern(hexPattern), orderedIndexes);

    /// <summary>
    /// Captures a full fixed-key byte pattern from a readable hexadecimal pattern using the short public spelling.<br/>
    /// This is the preferred alias for <see cref="MatchesHexPattern(string)"/>.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation Matches(string hexPattern) => LibraDexMultiKeyContinuation.From(inner.Matches(hexPattern), orderedIndexes);

    /// <summary>
    /// Captures equality for a fixed raw byte slice inside the selected binary index key.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation SliceEqual(int offset, byte[] value) => LibraDexMultiKeyContinuation.From(inner.SliceEqual(offset, value), orderedIndexes);

    /// <summary>
    /// Captures equality for a fixed raw byte slice from a readable hexadecimal pattern.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation SliceMatchesHex(int offset, string hexPattern) => LibraDexMultiKeyContinuation.From(inner.SliceMatchesHex(offset, hexPattern), orderedIndexes);
}

/// <summary>
/// Provides full-value and structured date operators for one selected group-level date index.<br/>
/// </summary>
/// <typeparam name="TValue">The CLR date/time value type selected by the caller.</typeparam>
public sealed class LibraDexMultiKeyDateWhere<TValue>
{
    private readonly LibraDexDateConditionOperator<TValue> inner;
    private readonly IIndex[]? orderedIndexes;

    internal LibraDexMultiKeyDateWhere(LibraDexDateConditionOperator<TValue> inner, IIndex[]? orderedIndexes)
    {
        this.inner = inner;
        this.orderedIndexes = orderedIndexes;
    }

    /// <summary>
    /// Captures full date/time equality against the selected index.<br/>
    /// Structured date convenience operators remain available separately so callers do not have to convert date parts into text or ad hoc ranges.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation EqualTo(TValue value) => LibraDexMultiKeyContinuation.From(inner.EqualTo(value), orderedIndexes);

    /// <summary>
    /// Captures full date/time inequality against the selected index.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation NotEqualTo(TValue value) => LibraDexMultiKeyContinuation.From(inner.NotEqualTo(value), orderedIndexes);

    /// <summary>
    /// Captures an inclusive full date/time range against the selected index.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation Between(TValue lower, TValue upper) => LibraDexMultiKeyContinuation.From(inner.Between(lower, upper), orderedIndexes);

    /// <summary>
    /// Captures a structured year equality condition against the selected date index.<br/>
    /// The execution bridge can evaluate this from the packed structured date bytes without converting stored keys to text.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation YearEqualTo(int year) => LibraDexMultiKeyContinuation.From(inner.YearEqualTo(year), orderedIndexes);

    /// <summary>
    /// Captures structured year membership against the selected date index.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation YearIn(params int[] years) => LibraDexMultiKeyContinuation.From(inner.YearIn(years), orderedIndexes);

    /// <summary>
    /// Captures an inclusive structured year range against the selected date index.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation YearRange(int startYear, int endYear) => LibraDexMultiKeyContinuation.From(inner.YearRange(startYear, endYear), orderedIndexes);

    /// <summary>
    /// Captures structured month equality against the selected date index.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation MonthEqualTo(int month) => LibraDexMultiKeyContinuation.From(inner.MonthEqualTo(month), orderedIndexes);

    /// <summary>
    /// Captures structured day-of-month equality against the selected date index.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation DayEqualTo(int day) => LibraDexMultiKeyContinuation.From(inner.DayEqualTo(day), orderedIndexes);

    /// <summary>
    /// Captures a structured year/month condition against the selected date index.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation YearMonth(int year, int month) => LibraDexMultiKeyContinuation.From(inner.YearMonth(year, month), orderedIndexes);

    /// <summary>
    /// Captures a structured year/month/day condition against the selected date index.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation YearMonthDay(int year, int month, int day) => LibraDexMultiKeyContinuation.From(inner.YearMonthDay(year, month, day), orderedIndexes);
}

/// <summary>
/// Captures the participant list for ordered group-level multi-key condition builders.<br/>
/// The ordered list is only a developer-experience aid for programmatic construction; it is not a separate execution contract and does not bypass normal condition-tree planning.<br/>
/// </summary>
public sealed class LibraDexOrderedMultiKeyBuilder
{
    private readonly string group;
    private readonly IIndex[] indexes;

    internal LibraDexOrderedMultiKeyBuilder(string group, IIndex[] indexes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentNullException.ThrowIfNull(indexes);
        if (indexes.Length == 0)
        {
            throw new ArgumentException("At least one index must participate in a multi-key condition.", nameof(indexes));
        }

        for (int i = 0; i < indexes.Length; i++)
        {
            ArgumentNullException.ThrowIfNull(indexes[i]);
            if (!string.Equals(indexes[i].Group, group, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("All multi-key indexes must belong to the same LibraDex identity group.");
            }
        }

        this.group = group;
        this.indexes = indexes.ToArray();
    }

    /// <summary>
    /// Starts a condition builder by selecting an ordered multi-key participant by ordinal.<br/>
    /// Key typing and projection intent are chosen after the ordinal through members such as `.AsString`, `.AsGuid`, and `.AsInt64`, matching catalog-group index-first condition syntax.<br/>
    /// The resulting predicates normalize to the same identity-group condition tree as manually composed opened-index expressions.<br/>
    /// </summary>
    /// <param name="ordinal">The zero-based participant ordinal supplied to `MultiKey(...)`.</param>
    /// <returns>A value-family selector for the selected participant.</returns>
    public LibraDexMultiKeyValueTypeSelector Where(int ordinal)
    {
        return SelectOrdinal(LibraDexCondition.ForGroup(group), ordinal);
    }

    /// <summary>
    /// Starts a condition builder by selecting an index name inside the multi-key identity group.<br/>
    /// This is useful when programmatic callers carry an ordered participant list for some branches but already know the logical index name for the first predicate.<br/>
    /// </summary>
    /// <param name="indexName">The index name inside this identity group.</param>
    /// <returns>A value-family selector for the selected index.</returns>
    public LibraDexConditionValueTypeSelector Where(string indexName)
    {
        return LibraDexCondition.ForGroup(group).Index(indexName);
    }

    /// <summary>
    /// Starts a condition builder by selecting an opened index handle inside the multi-key identity group.<br/>
    /// The handle is validated against the group captured by `MultiKey(...)` before the condition descriptor is created.<br/>
    /// </summary>
    /// <param name="index">The opened index instance to select.</param>
    /// <returns>A value-family selector for the selected index.</returns>
    public LibraDexConditionValueTypeSelector Where(IIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (!string.Equals(index.Group, group, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The supplied index handle belongs to a different LibraDex identity group.");
        }

        return Where(index.Name);
    }

    private LibraDexMultiKeyValueTypeSelector SelectOrdinal(LibraDexConditionClause clause, int ordinal)
    {
        if ((uint)ordinal >= (uint)indexes.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal), "The multi-key ordinal is outside the participant list.");
        }

        IIndex index = indexes[ordinal];
        return new LibraDexMultiKeyValueTypeSelector(clause.Index(index.Name), index, indexes);
    }
}
