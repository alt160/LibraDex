using System.Numerics;

namespace LibraDex;

/// <summary>
/// Provides a typed two-part composite index handle over an opened routed composite index.<br/>
/// The generic part types are validated when the handle is opened, then exposed through ordinal `Part1` / `Part2` selectors for tuple-style compiler help without generated named members.<br/>
/// </summary>
/// <typeparam name="TPart1">The first composite key-part type.</typeparam>
/// <typeparam name="TPart2">The second composite key-part type.</typeparam>
/// <typeparam name="TIdentity">The composite index identity type.</typeparam>
public sealed class LibraDexCompositeIndex<TPart1, TPart2, TIdentity>
{
    private readonly LibraDexRoutedCompositeIndex inner;

    internal LibraDexCompositeIndex(LibraDexRoutedCompositeIndex inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        inner.ValidateTypedCompositeShape(typeof(TIdentity), typeof(TPart1), typeof(TPart2));
        this.inner = inner;
    }

    /// <summary>
    /// Gets the identity group owned by this composite index.<br/>
    /// </summary>
    public string Group => inner.Group;

    /// <summary>
    /// Gets the catalog index name owned by this composite index.<br/>
    /// </summary>
    public string Name => inner.Name;

    /// <summary>
    /// Gets the untyped routed composite index handle for APIs that still need name-based or dynamic part selection.<br/>
    /// </summary>
    public LibraDexRoutedCompositeIndex Untyped => inner;

    /// <summary>
    /// Starts typed tuple-style condition construction for this two-part composite index.<br/>
    /// `Part1` maps to <typeparamref name="TPart1"/> and `Part2` maps to <typeparamref name="TPart2"/>; the persisted metadata is already validated by the handle constructor.<br/>
    /// </summary>
    public LibraDexCompositeIndexWhere<TPart1, TPart2, TIdentity> Where => new(inner.Where, PartNames());

    /// <summary>
    /// Selects a named part through the untyped composite grammar for dynamic or metadata-driven callers.<br/>
    /// </summary>
    /// <param name="name">The composite key-part name.</param>
    /// <returns>A named part selector that still requires an `.As...` type selector.</returns>
    public LibraDexCompositeConditionKeyPartSelector KeyPart(string name)
        => inner.Where.KeyPart(name);

    private string[] PartNames()
        => new[] { inner.LogicalShape!.CompositeParts[0].Name, inner.LogicalShape.CompositeParts[1].Name };
}

/// <summary>
/// Provides a typed three-part composite index handle over an opened routed composite index.<br/>
/// The generic part types are validated when the handle is opened, then exposed through ordinal `Part1`, `Part2`, and `Part3` selectors.<br/>
/// </summary>
/// <typeparam name="TPart1">The first composite key-part type.</typeparam>
/// <typeparam name="TPart2">The second composite key-part type.</typeparam>
/// <typeparam name="TPart3">The third composite key-part type.</typeparam>
/// <typeparam name="TIdentity">The composite index identity type.</typeparam>
public sealed class LibraDexCompositeIndex<TPart1, TPart2, TPart3, TIdentity>
{
    private readonly LibraDexRoutedCompositeIndex inner;

    internal LibraDexCompositeIndex(LibraDexRoutedCompositeIndex inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        inner.ValidateTypedCompositeShape(typeof(TIdentity), typeof(TPart1), typeof(TPart2), typeof(TPart3));
        this.inner = inner;
    }

    /// <summary>
    /// Gets the identity group owned by this composite index.<br/>
    /// </summary>
    public string Group => inner.Group;

    /// <summary>
    /// Gets the catalog index name owned by this composite index.<br/>
    /// </summary>
    public string Name => inner.Name;

    /// <summary>
    /// Gets the untyped routed composite index handle for APIs that still need name-based or dynamic part selection.<br/>
    /// </summary>
    public LibraDexRoutedCompositeIndex Untyped => inner;

    /// <summary>
    /// Starts typed tuple-style condition construction for this three-part composite index.<br/>
    /// The ordinal selectors map directly to the generic part type order supplied to `CompositeIndex&lt;...&gt;`.<br/>
    /// </summary>
    public LibraDexCompositeIndexWhere<TPart1, TPart2, TPart3, TIdentity> Where => new(inner.Where, PartNames());

    /// <summary>
    /// Selects a named part through the untyped composite grammar for dynamic or metadata-driven callers.<br/>
    /// </summary>
    /// <param name="name">The composite key-part name.</param>
    /// <returns>A named part selector that still requires an `.As...` type selector.</returns>
    public LibraDexCompositeConditionKeyPartSelector KeyPart(string name)
        => inner.Where.KeyPart(name);

    private string[] PartNames()
        => new[] { inner.LogicalShape!.CompositeParts[0].Name, inner.LogicalShape.CompositeParts[1].Name, inner.LogicalShape.CompositeParts[2].Name };
}

/// <summary>
/// Starts typed condition construction for a two-part composite index.<br/>
/// </summary>
public sealed class LibraDexCompositeIndexWhere<TPart1, TPart2, TIdentity>
{
    private readonly LibraDexCompositeConditionWhere inner;
    private readonly string[] partNames;

    internal LibraDexCompositeIndexWhere(LibraDexCompositeConditionWhere inner, string[] partNames)
    {
        this.inner = inner;
        this.partNames = partNames;
    }

    /// <summary>
    /// Gets a typed condition selector for the first composite key part.<br/>
    /// </summary>
    public LibraDexCompositeTypedPart<TPart1, LibraDexCompositeIndexContinuation<TPart1, TPart2, TIdentity>> Part1
        => Create<TPart1>(0);

    /// <summary>
    /// Gets a typed condition selector for the second composite key part.<br/>
    /// </summary>
    public LibraDexCompositeTypedPart<TPart2, LibraDexCompositeIndexContinuation<TPart1, TPart2, TIdentity>> Part2
        => Create<TPart2>(1);

    /// <summary>
    /// Selects a named part through the untyped composite grammar for dynamic or metadata-driven callers.<br/>
    /// </summary>
    public LibraDexCompositeConditionKeyPartSelector KeyPart(string name)
        => inner.KeyPart(name);

    private LibraDexCompositeTypedPart<TValue, LibraDexCompositeIndexContinuation<TPart1, TPart2, TIdentity>> Create<TValue>(int ordinal)
        => new(partNames[ordinal], part => new LibraDexCompositeIndexContinuation<TPart1, TPart2, TIdentity>(inner.Capture(part), partNames));
}

/// <summary>
/// Starts typed condition construction for a three-part composite index.<br/>
/// </summary>
public sealed class LibraDexCompositeIndexWhere<TPart1, TPart2, TPart3, TIdentity>
{
    private readonly LibraDexCompositeConditionWhere inner;
    private readonly string[] partNames;

    internal LibraDexCompositeIndexWhere(LibraDexCompositeConditionWhere inner, string[] partNames)
    {
        this.inner = inner;
        this.partNames = partNames;
    }

    /// <summary>
    /// Gets a typed condition selector for the first composite key part.<br/>
    /// </summary>
    public LibraDexCompositeTypedPart<TPart1, LibraDexCompositeIndexContinuation<TPart1, TPart2, TPart3, TIdentity>> Part1
        => Create<TPart1>(0);

    /// <summary>
    /// Gets a typed condition selector for the second composite key part.<br/>
    /// </summary>
    public LibraDexCompositeTypedPart<TPart2, LibraDexCompositeIndexContinuation<TPart1, TPart2, TPart3, TIdentity>> Part2
        => Create<TPart2>(1);

    /// <summary>
    /// Gets a typed condition selector for the third composite key part.<br/>
    /// </summary>
    public LibraDexCompositeTypedPart<TPart3, LibraDexCompositeIndexContinuation<TPart1, TPart2, TPart3, TIdentity>> Part3
        => Create<TPart3>(2);

    /// <summary>
    /// Selects a named part through the untyped composite grammar for dynamic or metadata-driven callers.<br/>
    /// </summary>
    public LibraDexCompositeConditionKeyPartSelector KeyPart(string name)
        => inner.KeyPart(name);

    private LibraDexCompositeTypedPart<TValue, LibraDexCompositeIndexContinuation<TPart1, TPart2, TPart3, TIdentity>> Create<TValue>(int ordinal)
        => new(partNames[ordinal], part => new LibraDexCompositeIndexContinuation<TPart1, TPart2, TPart3, TIdentity>(inner.Capture(part), partNames));
}

/// <summary>
/// Continues typed condition construction for a two-part composite index.<br/>
/// </summary>
public sealed class LibraDexCompositeIndexContinuation<TPart1, TPart2, TIdentity> : LibraDexCompositeResultContinuation
{
    private readonly LibraDexCompositeConditionContinuation inner;
    private readonly string[] partNames;

    internal LibraDexCompositeIndexContinuation(LibraDexCompositeConditionContinuation inner, string[] partNames)
        : base(inner.ResultSource)
    {
        this.inner = inner;
        this.partNames = partNames;
    }

    /// <summary>
    /// Continues this composite condition with an AND predicate over the same typed composite index.<br/>
    /// </summary>
    public LibraDexCompositeIndexWhere<TPart1, TPart2, TIdentity> And => new(inner.And, partNames);

    /// <summary>
    /// Continues this composite condition with an OR predicate over the same typed composite index.<br/>
    /// </summary>
    public LibraDexCompositeIndexWhere<TPart1, TPart2, TIdentity> Or => new(inner.Or, partNames);

    /// <summary>
    /// Completes the condition descriptor.<br/>
    /// </summary>
    public LibraDexConditionEndCondition EndCondition => inner.EndCondition;

    /// <summary>
    /// Convenience alias for <see cref="EndCondition"/>.<br/>
    /// </summary>
    public LibraDexConditionEndCondition ec => EndCondition;
}

/// <summary>
/// Continues typed condition construction for a three-part composite index.<br/>
/// </summary>
public sealed class LibraDexCompositeIndexContinuation<TPart1, TPart2, TPart3, TIdentity> : LibraDexCompositeResultContinuation
{
    private readonly LibraDexCompositeConditionContinuation inner;
    private readonly string[] partNames;

    internal LibraDexCompositeIndexContinuation(LibraDexCompositeConditionContinuation inner, string[] partNames)
        : base(inner.ResultSource)
    {
        this.inner = inner;
        this.partNames = partNames;
    }

    /// <summary>
    /// Continues this composite condition with an AND predicate over the same typed composite index.<br/>
    /// </summary>
    public LibraDexCompositeIndexWhere<TPart1, TPart2, TPart3, TIdentity> And => new(inner.And, partNames);

    /// <summary>
    /// Continues this composite condition with an OR predicate over the same typed composite index.<br/>
    /// </summary>
    public LibraDexCompositeIndexWhere<TPart1, TPart2, TPart3, TIdentity> Or => new(inner.Or, partNames);

    /// <summary>
    /// Completes the condition descriptor.<br/>
    /// </summary>
    public LibraDexConditionEndCondition EndCondition => inner.EndCondition;

    /// <summary>
    /// Convenience alias for <see cref="EndCondition"/>.<br/>
    /// </summary>
    public LibraDexConditionEndCondition ec => EndCondition;
}

/// <summary>
/// Represents one typed ordinal composite key part inside a fluent condition.<br/>
/// Core equality and membership operators use <typeparamref name="TValue"/> directly; family-specific operators are supplied as extension methods for constructed types such as `string`, `Guid`, `byte[]`, and `DateTime`.<br/>
/// </summary>
/// <typeparam name="TValue">The validated composite key-part value type.</typeparam>
/// <typeparam name="TContinuation">The fluent continuation type returned after this part captures a predicate.</typeparam>
public sealed class LibraDexCompositeTypedPart<TValue, TContinuation>
{
    private readonly string name;
    private readonly Func<LibraDexCompositePartCriterion, TContinuation> capture;

    internal LibraDexCompositeTypedPart(string name, Func<LibraDexCompositePartCriterion, TContinuation> capture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        this.name = name;
        this.capture = capture;
    }

    internal string Name => name;

    internal TContinuation Capture(LibraDexCompositePartCriterion part)
        => capture(part);

    /// <summary>
    /// Captures equality against this typed composite key part.<br/>
    /// </summary>
    /// <param name="value">The value to match.</param>
    /// <returns>The typed composite continuation.</returns>
    public TContinuation EqualTo(TValue value)
        => capture(Create(LibraDexConditionOperatorKind.EqualTo, value));

    /// <summary>
    /// Captures inequality against this typed composite key part.<br/>
    /// </summary>
    /// <param name="value">The value to exclude.</param>
    /// <returns>The typed composite continuation.</returns>
    public TContinuation NotEqualTo(TValue value)
        => capture(Create(LibraDexConditionOperatorKind.NotEqualTo, value));

    /// <summary>
    /// Captures membership against this typed composite key part.<br/>
    /// </summary>
    /// <param name="values">The values to match.</param>
    /// <returns>The typed composite continuation.</returns>
    public TContinuation InSet(IEnumerable<TValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return capture(Create(LibraDexConditionOperatorKind.InSet, CaptureMembershipInput(values)));
    }

    /// <summary>
    /// Captures membership using LibraDex's short set spelling.<br/>
    /// </summary>
    public TContinuation In(IEnumerable<TValue> values)
        => InSet(values);

    /// <summary>
    /// Captures Abraxas-style membership.<br/>
    /// </summary>
    public TContinuation IsIn(IEnumerable<TValue> values)
        => InSet(values);

    /// <summary>
    /// Captures membership exclusion against this typed composite key part.<br/>
    /// </summary>
    /// <param name="values">The values to exclude.</param>
    /// <returns>The typed composite continuation.</returns>
    public TContinuation NotInSet(IEnumerable<TValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return capture(Create(LibraDexConditionOperatorKind.NotInSet, CaptureMembershipInput(values)));
    }

    /// <summary>
    /// Captures membership exclusion using LibraDex's short set spelling.<br/>
    /// </summary>
    public TContinuation NotIn(IEnumerable<TValue> values)
        => NotInSet(values);

    /// <summary>
    /// Captures Abraxas-style membership exclusion.<br/>
    /// </summary>
    public TContinuation IsNotIn(IEnumerable<TValue> values)
        => NotInSet(values);

    /// <summary>
    /// Captures a typed greater-than predicate for scalar-like and date-like composite parts.<br/>
    /// </summary>
    public TContinuation GreaterThan(TValue value)
        => capture(Create(LibraDexConditionOperatorKind.GreaterThan, value));

    /// <summary>
    /// Captures a typed greater-than-or-equal predicate for scalar-like and date-like composite parts.<br/>
    /// </summary>
    public TContinuation GreaterOrEqual(TValue value)
        => capture(Create(LibraDexConditionOperatorKind.GreaterOrEqual, value));

    /// <summary>
    /// Captures a typed less-than predicate for scalar-like and date-like composite parts.<br/>
    /// </summary>
    public TContinuation LessThan(TValue value)
        => capture(Create(LibraDexConditionOperatorKind.LessThan, value));

    /// <summary>
    /// Captures a typed less-than-or-equal predicate for scalar-like and date-like composite parts.<br/>
    /// </summary>
    public TContinuation LessOrEqual(TValue value)
        => capture(Create(LibraDexConditionOperatorKind.LessOrEqual, value));

    /// <summary>
    /// Captures a typed inclusive range predicate for scalar-like and date-like composite parts.<br/>
    /// </summary>
    public TContinuation Between(TValue lower, TValue upper)
        => capture(Create(LibraDexConditionOperatorKind.Between, lower, upper));

    /// <summary>
    /// Captures a typed outside-range predicate for scalar-like and date-like composite parts.<br/>
    /// </summary>
    public TContinuation NotBetween(TValue lower, TValue upper)
        => capture(Create(LibraDexConditionOperatorKind.NotBetween, lower, upper));

    /// <summary>
    /// Captures composite-part equality from a reusable execution-time parameter.<br/>
    /// The parameter is snapshotted with the surrounding condition before composite traversal begins.<br/>
    /// </summary>
    /// <param name="value">The parameter supplying the current part value.</param>
    /// <returns>The typed composite continuation.</returns>
    public TContinuation EqualTo(LibraDexParameter<TValue> value)
        => capture(Create(LibraDexConditionOperatorKind.EqualTo, LibraDexConditionOperand.Parameter(value)));

    /// <summary>
    /// Captures composite-part inequality from a reusable execution-time parameter.<br/>
    /// </summary>
    /// <param name="value">The parameter supplying the current excluded part value.</param>
    /// <returns>The typed composite continuation.</returns>
    public TContinuation NotEqualTo(LibraDexParameter<TValue> value)
        => capture(Create(LibraDexConditionOperatorKind.NotEqualTo, LibraDexConditionOperand.Parameter(value)));

    /// <summary>
    /// Captures a composite-part inclusive range from reusable execution-time parameters.<br/>
    /// </summary>
    /// <param name="lower">The parameter supplying the inclusive lower boundary.</param>
    /// <param name="upper">The parameter supplying the inclusive upper boundary.</param>
    /// <returns>The typed composite continuation.</returns>
    public TContinuation Between(LibraDexParameter<TValue> lower, LibraDexParameter<TValue> upper)
        => capture(Create(LibraDexConditionOperatorKind.Between, LibraDexConditionOperand.Parameter(lower), LibraDexConditionOperand.Parameter(upper)));

    /// <summary>
    /// Captures composite-part membership from a reusable execution-time collection parameter.<br/>
    /// </summary>
    /// <typeparam name="TValues">The enumerable collection type retained by the caller.</typeparam>
    /// <param name="values">The parameter supplying current part values.</param>
    /// <returns>The typed composite continuation.</returns>
    public TContinuation InSet<TValues>(LibraDexParameter<TValues> values)
        where TValues : IEnumerable<TValue>
        => capture(Create(LibraDexConditionOperatorKind.InSet, LibraDexConditionOperand.Parameter(values)));

    internal LibraDexCompositePartCriterion Create(LibraDexConditionOperatorKind operatorKind, params object?[] values)
    {
        return new LibraDexCompositePartCriterion(
            name,
            ResolveValueKind(),
            operatorKind,
            Array.AsReadOnly(values),
            ignoreCase: false,
            culture: null);
    }

    internal LibraDexCompositePartCriterion CreateString(
        LibraDexConditionOperatorKind operatorKind,
        bool ignoreCase,
        string? culture,
        params object?[] values)
    {
        return new LibraDexCompositePartCriterion(
            name,
            LibraDexConditionValueKind.String,
            operatorKind,
            Array.AsReadOnly(values),
            ignoreCase,
            culture);
    }

    private static object CaptureMembershipInput(IEnumerable<TValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values is System.Collections.ICollection or IReadOnlyCollection<TValue>
            ? values
            : values.ToArray();
    }

    private static LibraDexConditionValueKind ResolveValueKind()
    {
        Type type = typeof(TValue);
        if (type == typeof(string))
            return LibraDexConditionValueKind.String;
        if (type == typeof(byte[]))
            return LibraDexConditionValueKind.Binary;
        if (type == typeof(Guid))
            return LibraDexConditionValueKind.Guid;
        if (type == typeof(DateTime))
            return LibraDexConditionValueKind.DateTime;
        if (type == typeof(DateOnly))
            return LibraDexConditionValueKind.DateOnly;
        if (type == typeof(TimeOnly))
            return LibraDexConditionValueKind.TimeOnly;
        if (type == typeof(TimeSpan))
            return LibraDexConditionValueKind.TimeSpan;

        return LibraDexConditionValueKind.Numeric;
    }
}

/// <summary>
/// Provides type-specific typed composite part operators that can be offered by IntelliSense only for compatible constructed part types.<br/>
/// </summary>
public static class LibraDexCompositeTypedPartExtensions
{
    /// <summary>
    /// Captures equality against an explicit null or empty state for a typed string composite part.<br/>
    /// `NullKey.Null` matches the stored composite null route, `NullKey.Empty` matches the stored empty string route, and `NullKey.NullOrEmpty` matches either state.<br/>
    /// </summary>
    public static TContinuation EqualTo<TContinuation>(
        this LibraDexCompositeTypedPart<string, TContinuation> part,
        NullKey keyState)
        => part.Capture(part.CreateString(LibraDexConditionOperatorKind.EqualTo, ignoreCase: false, culture: null, keyState));

    /// <summary>
    /// Captures inequality against an explicit null or empty state for a typed string composite part.<br/>
    /// This is the concise typed tuple-style presence predicate for optional string parts.<br/>
    /// </summary>
    public static TContinuation NotEqualTo<TContinuation>(
        this LibraDexCompositeTypedPart<string, TContinuation> part,
        NullKey keyState)
        => part.Capture(part.CreateString(LibraDexConditionOperatorKind.NotEqualTo, ignoreCase: false, culture: null, keyState));

    /// <summary>
    /// Captures equality against an explicit null or empty state for a typed binary composite part.<br/>
    /// Null and empty byte-array routes remain distinct, matching ordinary binary-index <see cref="NullKey"/> semantics.<br/>
    /// </summary>
    public static TContinuation EqualTo<TContinuation>(
        this LibraDexCompositeTypedPart<byte[], TContinuation> part,
        NullKey keyState)
        => part.Capture(part.Create(LibraDexConditionOperatorKind.EqualTo, keyState));

    /// <summary>
    /// Captures inequality against an explicit null or empty state for a typed binary composite part.<br/>
    /// This provides tuple-style binary part presence syntax without caller-owned marker byte arrays.<br/>
    /// </summary>
    public static TContinuation NotEqualTo<TContinuation>(
        this LibraDexCompositeTypedPart<byte[], TContinuation> part,
        NullKey keyState)
        => part.Capture(part.Create(LibraDexConditionOperatorKind.NotEqualTo, keyState));

    /// <summary>
    /// Captures equality against a scalar null-state for a typed non-string/non-binary composite part.<br/>
    /// `ScalarNull.Null` matches the stored composite null route and `ScalarNull.NonNull` matches ordinary scalar, GUID, or date-like values.<br/>
    /// </summary>
    public static TContinuation EqualTo<TValue, TContinuation>(
        this LibraDexCompositeTypedPart<TValue, TContinuation> part,
        ScalarNull state)
        where TValue : struct
        => part.Capture(part.Create(LibraDexConditionOperatorKind.EqualTo, state));

    /// <summary>
    /// Captures inequality against a scalar null-state for a typed non-string/non-binary composite part.<br/>
    /// This is the tuple-style presence counterpart to ordinary scalar-index null-state predicates.<br/>
    /// </summary>
    public static TContinuation NotEqualTo<TValue, TContinuation>(
        this LibraDexCompositeTypedPart<TValue, TContinuation> part,
        ScalarNull state)
        where TValue : struct
        => part.Capture(part.Create(LibraDexConditionOperatorKind.NotEqualTo, state));

    /// <summary>
    /// Captures a string prefix predicate against a typed string composite part.<br/>
    /// </summary>
    public static TContinuation StartsWith<TContinuation>(
        this LibraDexCompositeTypedPart<string, TContinuation> part,
        string value,
        bool ignoreCase = false,
        string? culture = null)
        => part.Capture(part.CreateString(LibraDexConditionOperatorKind.StartsWith, ignoreCase, culture, value));

    /// <summary>
    /// Captures a string suffix predicate against a typed string composite part.<br/>
    /// </summary>
    public static TContinuation EndsWith<TContinuation>(
        this LibraDexCompositeTypedPart<string, TContinuation> part,
        string value,
        bool ignoreCase = false,
        string? culture = null)
        => part.Capture(part.CreateString(LibraDexConditionOperatorKind.EndsWith, ignoreCase, culture, value));

    /// <summary>
    /// Captures a string containment predicate against a typed string composite part.<br/>
    /// </summary>
    public static TContinuation Contains<TContinuation>(
        this LibraDexCompositeTypedPart<string, TContinuation> part,
        string value,
        bool ignoreCase = false,
        string? culture = null)
        => part.Capture(part.CreateString(LibraDexConditionOperatorKind.Contains, ignoreCase, culture, value));

    /// <summary>
    /// Captures a negated string containment predicate against a typed string composite part.<br/>
    /// </summary>
    public static TContinuation NotContains<TContinuation>(
        this LibraDexCompositeTypedPart<string, TContinuation> part,
        string value,
        bool ignoreCase = false,
        string? culture = null)
        => part.Capture(part.CreateString(LibraDexConditionOperatorKind.NotContains, ignoreCase, culture, value));

    /// <summary>
    /// Captures a wildcard predicate against a typed string composite part.<br/>
    /// Simple wildcard shapes are reduced to exact, prefix, suffix, or containment operators before execution.<br/>
    /// </summary>
    public static TContinuation Like<TContinuation>(
        this LibraDexCompositeTypedPart<string, TContinuation> part,
        string pattern,
        bool ignoreCase = false,
        string? culture = null)
        => CreateWildcard(part, pattern, negate: false, ignoreCase, culture);

    /// <summary>
    /// Captures a negated wildcard predicate against a typed string composite part.<br/>
    /// </summary>
    public static TContinuation NotLike<TContinuation>(
        this LibraDexCompositeTypedPart<string, TContinuation> part,
        string pattern,
        bool ignoreCase = false,
        string? culture = null)
        => CreateWildcard(part, pattern, negate: true, ignoreCase, culture);

    /// <summary>
    /// Captures a wildcard string pattern predicate against a typed string composite part.<br/>
    /// </summary>
    public static TContinuation Matches<TContinuation>(
        this LibraDexCompositeTypedPart<string, TContinuation> part,
        string pattern,
        bool ignoreCase = false,
        string? culture = null)
        => part.Capture(part.CreateString(LibraDexConditionOperatorKind.MatchesPattern, ignoreCase, culture, pattern));

    private static TContinuation CreateWildcard<TContinuation>(
        LibraDexCompositeTypedPart<string, TContinuation> part,
        string pattern,
        bool negate,
        bool ignoreCase,
        string? culture)
    {
        LibraDexWildcardPattern wildcard = LibraDexWildcardPattern.Create(
            pattern,
            LibraDexStringComparisonPolicy.FromLegacy(ignoreCase, culture),
            compileComplex: false);
        LibraDexConditionOperatorKind operatorKind = wildcard.Shape switch
        {
            LibraDexWildcardShape.Exact => negate ? LibraDexConditionOperatorKind.NotEqualTo : LibraDexConditionOperatorKind.EqualTo,
            LibraDexWildcardShape.StartsWith => negate ? LibraDexConditionOperatorKind.NotStartsWith : LibraDexConditionOperatorKind.StartsWith,
            LibraDexWildcardShape.EndsWith => negate ? LibraDexConditionOperatorKind.NotEndsWith : LibraDexConditionOperatorKind.EndsWith,
            LibraDexWildcardShape.Contains => negate ? LibraDexConditionOperatorKind.NotContains : LibraDexConditionOperatorKind.Contains,
            _ => negate ? LibraDexConditionOperatorKind.NotMatchesPattern : LibraDexConditionOperatorKind.MatchesPattern
        };
        string operand = wildcard.Shape == LibraDexWildcardShape.Complex ? pattern : wildcard.Literal;
        return part.Capture(part.CreateString(operatorKind, ignoreCase, culture, operand));
    }

    /// <summary>
    /// Captures a GUID byte-prefix predicate against a typed GUID composite part.<br/>
    /// </summary>
    public static TContinuation StartsWith<TContinuation>(
        this LibraDexCompositeTypedPart<Guid, TContinuation> part,
        Guid value,
        int byteCount)
        => part.Capture(part.Create(LibraDexConditionOperatorKind.StartsWith, value, byteCount));

    /// <summary>
    /// Captures a canonical GUID text-prefix predicate against a typed GUID composite part.<br/>
    /// The pattern is compiled once and composite candidates remain binary during execution.<br/>
    /// </summary>
    public static TContinuation StartsWith<TContinuation>(
        this LibraDexCompositeTypedPart<Guid, TContinuation> part,
        string value)
        => part.Capture(part.Create(LibraDexConditionOperatorKind.StartsWith, LibraDexGuidPatternPredicate.Create(value, LibraDexGuidPatternMode.StartsWith)));

    /// <summary>
    /// Captures a canonical GUID text-suffix predicate against a typed GUID composite part.<br/>
    /// </summary>
    public static TContinuation EndsWith<TContinuation>(
        this LibraDexCompositeTypedPart<Guid, TContinuation> part,
        string value)
        => part.Capture(part.Create(LibraDexConditionOperatorKind.EndsWith, LibraDexGuidPatternPredicate.Create(value, LibraDexGuidPatternMode.EndsWith)));

    /// <summary>
    /// Captures anywhere-containment among the 32 canonical hexadecimal digits of a typed GUID composite part.<br/>
    /// </summary>
    public static TContinuation Contains<TContinuation>(
        this LibraDexCompositeTypedPart<Guid, TContinuation> part,
        string value)
        => part.Capture(part.Create(LibraDexConditionOperatorKind.Contains, LibraDexGuidPatternPredicate.Create(value, LibraDexGuidPatternMode.Contains)));

    /// <summary>
    /// Captures a full `x`-wildcard canonical pattern against a typed GUID composite part.<br/>
    /// </summary>
    public static TContinuation Matches<TContinuation>(
        this LibraDexCompositeTypedPart<Guid, TContinuation> part,
        string pattern)
        => part.Capture(part.Create(LibraDexConditionOperatorKind.MatchesPattern, LibraDexGuidPatternPredicate.Create(pattern, LibraDexGuidPatternMode.MatchesPattern)));

    /// <summary>
    /// Captures a byte-prefix predicate against a typed binary composite part.<br/>
    /// </summary>
    public static TContinuation StartsWith<TContinuation>(
        this LibraDexCompositeTypedPart<byte[], TContinuation> part,
        byte[] value)
        => part.Capture(part.Create(LibraDexConditionOperatorKind.StartsWith, value));

    /// <summary>
    /// Captures a byte-suffix predicate against a typed binary composite part.<br/>
    /// </summary>
    public static TContinuation EndsWith<TContinuation>(
        this LibraDexCompositeTypedPart<byte[], TContinuation> part,
        byte[] value)
        => part.Capture(part.Create(LibraDexConditionOperatorKind.EndsWith, value));

    /// <summary>
    /// Captures byte containment against a typed binary composite part.<br/>
    /// </summary>
    public static TContinuation Contains<TContinuation>(
        this LibraDexCompositeTypedPart<byte[], TContinuation> part,
        byte[] value)
        => part.Capture(part.Create(LibraDexConditionOperatorKind.Contains, value));

    /// <summary>
    /// Captures a structured year equality predicate against a typed DateTime composite part.<br/>
    /// </summary>
    public static TContinuation YearEqualTo<TContinuation>(
        this LibraDexCompositeTypedPart<DateTime, TContinuation> part,
        int year)
        => part.Capture(part.Create(LibraDexConditionOperatorKind.YearEqualTo, year));

    /// <summary>
    /// Captures a structured year-month predicate against a typed DateTime composite part.<br/>
    /// </summary>
    public static TContinuation YearMonth<TContinuation>(
        this LibraDexCompositeTypedPart<DateTime, TContinuation> part,
        int year,
        int month)
        => part.Capture(part.Create(LibraDexConditionOperatorKind.YearMonth, year, month));

    /// <summary>
    /// Captures a structured year-month-day predicate against a typed DateTime composite part.<br/>
    /// </summary>
    public static TContinuation YearMonthDay<TContinuation>(
        this LibraDexCompositeTypedPart<DateTime, TContinuation> part,
        int year,
        int month,
        int day)
        => part.Capture(part.Create(LibraDexConditionOperatorKind.YearMonthDay, year, month, day));

    /// <summary>
    /// Matches a typed string composite part stored on its explicit null route.<br/>
    /// This preserves null as distinct from both an empty string and an absent composite tuple.<br/>
    /// </summary>
    public static TContinuation IsNull<TContinuation>(this LibraDexCompositeTypedPart<string, TContinuation> part)
        => part.EqualTo(NullKey.Null);

    /// <summary>
    /// Matches a typed string composite part that is not explicitly null.<br/>
    /// Empty strings remain included because they occupy their own key-state route.<br/>
    /// </summary>
    public static TContinuation IsNotNull<TContinuation>(this LibraDexCompositeTypedPart<string, TContinuation> part)
        => part.NotEqualTo(NullKey.Null);

    /// <summary>
    /// Matches a typed string composite part stored on its explicit empty-string route.<br/>
    /// Whitespace-only strings remain ordinary non-empty values.<br/>
    /// </summary>
    public static TContinuation IsEmpty<TContinuation>(this LibraDexCompositeTypedPart<string, TContinuation> part)
        => part.EqualTo(NullKey.Empty);

    /// <summary>
    /// Matches a typed string composite part that is not the empty string.<br/>
    /// Explicit null values remain included by this single-state complement.<br/>
    /// </summary>
    public static TContinuation IsNotEmpty<TContinuation>(this LibraDexCompositeTypedPart<string, TContinuation> part)
        => part.NotEqualTo(NullKey.Empty);

    /// <summary>
    /// Matches a typed string composite part stored as either null or empty.<br/>
    /// The two maintained part states are combined without caller-side grouping.<br/>
    /// </summary>
    public static TContinuation IsNullOrEmpty<TContinuation>(this LibraDexCompositeTypedPart<string, TContinuation> part)
        => part.EqualTo(NullKey.NullOrEmpty);

    /// <summary>
    /// Matches a typed string composite part stored as neither null nor empty.<br/>
    /// Ordinary values, including whitespace-only strings, remain eligible.<br/>
    /// </summary>
    public static TContinuation IsNotNullOrEmpty<TContinuation>(this LibraDexCompositeTypedPart<string, TContinuation> part)
        => part.NotEqualTo(NullKey.NullOrEmpty);

    /// <summary>
    /// Matches a typed binary composite part stored on its explicit null route.<br/>
    /// The null state remains distinct from a zero-length byte sequence.<br/>
    /// </summary>
    public static TContinuation IsNull<TContinuation>(this LibraDexCompositeTypedPart<byte[], TContinuation> part)
        => part.EqualTo(NullKey.Null);

    /// <summary>
    /// Matches a typed binary composite part that is not explicitly null.<br/>
    /// Empty byte sequences remain included because they occupy their own route.<br/>
    /// </summary>
    public static TContinuation IsNotNull<TContinuation>(this LibraDexCompositeTypedPart<byte[], TContinuation> part)
        => part.NotEqualTo(NullKey.Null);

    /// <summary>
    /// Matches a typed binary composite part stored on its explicit empty-byte route.<br/>
    /// This represents a zero-length byte sequence rather than a null part.<br/>
    /// </summary>
    public static TContinuation IsEmpty<TContinuation>(this LibraDexCompositeTypedPart<byte[], TContinuation> part)
        => part.EqualTo(NullKey.Empty);

    /// <summary>
    /// Matches a typed binary composite part that is not an empty byte sequence.<br/>
    /// Explicit null values remain included by this single-state complement.<br/>
    /// </summary>
    public static TContinuation IsNotEmpty<TContinuation>(this LibraDexCompositeTypedPart<byte[], TContinuation> part)
        => part.NotEqualTo(NullKey.Empty);

    /// <summary>
    /// Matches a typed binary composite part stored as either null or empty.<br/>
    /// The two maintained part states are combined without caller-side grouping.<br/>
    /// </summary>
    public static TContinuation IsNullOrEmpty<TContinuation>(this LibraDexCompositeTypedPart<byte[], TContinuation> part)
        => part.EqualTo(NullKey.NullOrEmpty);

    /// <summary>
    /// Matches a typed binary composite part stored as neither null nor empty.<br/>
    /// Only ordinary non-empty binary part values remain eligible.<br/>
    /// </summary>
    public static TContinuation IsNotNullOrEmpty<TContinuation>(this LibraDexCompositeTypedPart<byte[], TContinuation> part)
        => part.NotEqualTo(NullKey.NullOrEmpty);

    /// <summary>
    /// Matches a typed value-type composite part stored on its explicit scalar-null route.<br/>
    /// Value-type parts have no empty state, so only null and non-null predicates are exposed.<br/>
    /// </summary>
    public static TContinuation IsNull<TValue, TContinuation>(this LibraDexCompositeTypedPart<TValue, TContinuation> part)
        where TValue : struct
        => part.EqualTo(ScalarNull.Null);

    /// <summary>
    /// Matches a typed value-type composite part stored on an ordinary non-null route.<br/>
    /// This is the complete key-state complement for scalar, GUID, and temporal composite parts.<br/>
    /// </summary>
    public static TContinuation IsNotNull<TValue, TContinuation>(this LibraDexCompositeTypedPart<TValue, TContinuation> part)
        where TValue : struct
        => part.EqualTo(ScalarNull.NonNull);
}

/// <summary>
/// Adds bitmask predicates only to typed composite parts whose scalar type implements the CLR bitwise-operator contract.<br/>
/// Enum operands are checked-converted through the same selected-scalar adapter used by ordinary numeric conditions, so the typed composite index definition remains authoritative.<br/>
/// </summary>
public static class LibraDexCompositeTypedBitmaskExtensions
{
    /// <summary>
    /// Captures a bitwise-AND equality predicate against a typed integral composite part.<br/>
    /// </summary>
    /// <typeparam name="TScalar">The numeric scalar type declared for the composite part.<br/></typeparam>
    /// <typeparam name="TContinuation">The fluent continuation returned after the predicate is captured.<br/></typeparam>
    /// <param name="part">The typed composite part receiving the predicate.<br/></param>
    /// <param name="bitMask">The bits retained by the AND operation.<br/></param>
    /// <param name="equalTo">The scalar value the retained bits must equal.<br/></param>
    /// <returns>The next composite-condition continuation.<br/></returns>
    public static TContinuation BitAnd<TScalar, TContinuation>(
        this LibraDexCompositeTypedPart<TScalar, TContinuation> part,
        TScalar bitMask,
        TScalar equalTo)
        where TScalar : IBitwiseOperators<TScalar, TScalar, TScalar>
        => part.Capture(part.Create(LibraDexConditionOperatorKind.BitAndEqualTo, bitMask, equalTo));

    /// <summary>
    /// Captures a bitwise-AND zero predicate against a typed integral composite part.<br/>
    /// </summary>
    /// <typeparam name="TScalar">The numeric scalar type declared for the composite part.<br/></typeparam>
    /// <typeparam name="TContinuation">The fluent continuation returned after the predicate is captured.<br/></typeparam>
    /// <param name="part">The typed composite part receiving the predicate.<br/></param>
    /// <param name="bitMask">The bits that must all be clear.<br/></param>
    /// <returns>The next composite-condition continuation.<br/></returns>
    public static TContinuation BitAnd<TScalar, TContinuation>(
        this LibraDexCompositeTypedPart<TScalar, TContinuation> part,
        TScalar bitMask)
        where TScalar : IBitwiseOperators<TScalar, TScalar, TScalar>
        => part.Capture(part.Create(LibraDexConditionOperatorKind.BitAndEqualTo, bitMask, default(TScalar)!));

    /// <summary>
    /// Captures a bitwise-AND inequality predicate against a typed integral composite part.<br/>
    /// </summary>
    /// <typeparam name="TScalar">The numeric scalar type declared for the composite part.<br/></typeparam>
    /// <typeparam name="TContinuation">The fluent continuation returned after the predicate is captured.<br/></typeparam>
    /// <param name="part">The typed composite part receiving the predicate.<br/></param>
    /// <param name="bitMask">The bits retained by the AND operation.<br/></param>
    /// <param name="notEqualTo">The scalar value the retained bits must not equal.<br/></param>
    /// <returns>The next composite-condition continuation.<br/></returns>
    public static TContinuation BitAndNotEqualTo<TScalar, TContinuation>(
        this LibraDexCompositeTypedPart<TScalar, TContinuation> part,
        TScalar bitMask,
        TScalar notEqualTo)
        where TScalar : IBitwiseOperators<TScalar, TScalar, TScalar>
        => part.Capture(part.Create(LibraDexConditionOperatorKind.BitAndNotEqualTo, bitMask, notEqualTo));

    /// <summary>
    /// Requires every selected bit to be set on a typed integral composite part.<br/>
    /// </summary>
    /// <typeparam name="TScalar">The numeric scalar type declared for the composite part.<br/></typeparam>
    /// <typeparam name="TContinuation">The fluent continuation returned after the predicate is captured.<br/></typeparam>
    /// <param name="part">The typed composite part receiving the predicate.<br/></param>
    /// <param name="bitMask">The bits that must all be set.<br/></param>
    /// <returns>The next composite-condition continuation.<br/></returns>
    public static TContinuation AllBitsSet<TScalar, TContinuation>(
        this LibraDexCompositeTypedPart<TScalar, TContinuation> part,
        TScalar bitMask)
        where TScalar : IBitwiseOperators<TScalar, TScalar, TScalar>
        => part.Capture(part.Create(LibraDexConditionOperatorKind.BitAndEqualTo, bitMask, bitMask));

    /// <summary>
    /// Requires at least one selected bit to be set on a typed integral composite part.<br/>
    /// </summary>
    /// <typeparam name="TScalar">The numeric scalar type declared for the composite part.<br/></typeparam>
    /// <typeparam name="TContinuation">The fluent continuation returned after the predicate is captured.<br/></typeparam>
    /// <param name="part">The typed composite part receiving the predicate.<br/></param>
    /// <param name="bitMask">The bits from which at least one must be set.<br/></param>
    /// <returns>The next composite-condition continuation.<br/></returns>
    public static TContinuation AnyBitsSet<TScalar, TContinuation>(
        this LibraDexCompositeTypedPart<TScalar, TContinuation> part,
        TScalar bitMask)
        where TScalar : IBitwiseOperators<TScalar, TScalar, TScalar>
        => part.Capture(part.Create(LibraDexConditionOperatorKind.BitAndNotEqualTo, bitMask, default(TScalar)!));

    /// <summary>
    /// Requires none of the selected bits to be set on a typed integral composite part.<br/>
    /// </summary>
    /// <typeparam name="TScalar">The numeric scalar type declared for the composite part.<br/></typeparam>
    /// <typeparam name="TContinuation">The fluent continuation returned after the predicate is captured.<br/></typeparam>
    /// <param name="part">The typed composite part receiving the predicate.<br/></param>
    /// <param name="bitMask">The bits that must all be clear.<br/></param>
    /// <returns>The next composite-condition continuation.<br/></returns>
    public static TContinuation NoBitsSet<TScalar, TContinuation>(
        this LibraDexCompositeTypedPart<TScalar, TContinuation> part,
        TScalar bitMask)
        where TScalar : IBitwiseOperators<TScalar, TScalar, TScalar>
        => part.Capture(part.Create(LibraDexConditionOperatorKind.BitAndEqualTo, bitMask, default(TScalar)!));

    /// <summary>
    /// Captures bitwise-AND equality using enum operands checked-converted to the typed composite scalar.<br/>
    /// </summary>
    /// <typeparam name="TScalar">The numeric scalar type declared for the composite part.<br/></typeparam>
    /// <typeparam name="TContinuation">The fluent continuation returned after the predicate is captured.<br/></typeparam>
    /// <typeparam name="TEnum">The CLR enum type supplying both operands.<br/></typeparam>
    /// <param name="part">The typed composite part receiving the predicate.<br/></param>
    /// <param name="bitMask">The enum mask checked-converted to <typeparamref name="TScalar"/>.<br/></param>
    /// <param name="equalTo">The enum comparison value checked-converted to <typeparamref name="TScalar"/>.<br/></param>
    /// <returns>The next composite-condition continuation.<br/></returns>
    public static TContinuation BitAnd<TScalar, TContinuation, TEnum>(
        this LibraDexCompositeTypedPart<TScalar, TContinuation> part,
        TEnum bitMask,
        TEnum equalTo)
        where TScalar : IBitwiseOperators<TScalar, TScalar, TScalar>
        where TEnum : struct, Enum
        => part.BitAnd(
            LibraDexEnumScalarConverter<TScalar, TEnum>.Convert(bitMask),
            LibraDexEnumScalarConverter<TScalar, TEnum>.Convert(equalTo));

    /// <summary>
    /// Captures bitwise-AND equality using a scalar mask and a checked-converted enum comparison value.<br/>
    /// </summary>
    /// <typeparam name="TScalar">The numeric scalar type declared for the composite part.<br/></typeparam>
    /// <typeparam name="TContinuation">The fluent continuation returned after the predicate is captured.<br/></typeparam>
    /// <typeparam name="TEnum">The CLR enum type supplying the comparison value.<br/></typeparam>
    /// <param name="part">The typed composite part receiving the predicate.<br/></param>
    /// <param name="bitMask">The scalar bits retained by the AND operation.<br/></param>
    /// <param name="equalTo">The enum comparison value checked-converted to <typeparamref name="TScalar"/>.<br/></param>
    /// <returns>The next composite-condition continuation.<br/></returns>
    public static TContinuation BitAnd<TScalar, TContinuation, TEnum>(
        this LibraDexCompositeTypedPart<TScalar, TContinuation> part,
        TScalar bitMask,
        TEnum equalTo)
        where TScalar : IBitwiseOperators<TScalar, TScalar, TScalar>
        where TEnum : struct, Enum
        => part.BitAnd(bitMask, LibraDexEnumScalarConverter<TScalar, TEnum>.Convert(equalTo));

    /// <summary>
    /// Captures bitwise-AND equality using a checked-converted enum mask and a scalar comparison value.<br/>
    /// </summary>
    /// <typeparam name="TScalar">The numeric scalar type declared for the composite part.<br/></typeparam>
    /// <typeparam name="TContinuation">The fluent continuation returned after the predicate is captured.<br/></typeparam>
    /// <typeparam name="TEnum">The CLR enum type supplying the mask.<br/></typeparam>
    /// <param name="part">The typed composite part receiving the predicate.<br/></param>
    /// <param name="bitMask">The enum mask checked-converted to <typeparamref name="TScalar"/>.<br/></param>
    /// <param name="equalTo">The scalar value the retained bits must equal.<br/></param>
    /// <returns>The next composite-condition continuation.<br/></returns>
    public static TContinuation BitAnd<TScalar, TContinuation, TEnum>(
        this LibraDexCompositeTypedPart<TScalar, TContinuation> part,
        TEnum bitMask,
        TScalar equalTo)
        where TScalar : IBitwiseOperators<TScalar, TScalar, TScalar>
        where TEnum : struct, Enum
        => part.BitAnd(LibraDexEnumScalarConverter<TScalar, TEnum>.Convert(bitMask), equalTo);

    /// <summary>
    /// Captures a bitwise-AND zero predicate using an enum mask checked-converted to the typed composite scalar.<br/>
    /// </summary>
    /// <typeparam name="TScalar">The numeric scalar type declared for the composite part.<br/></typeparam>
    /// <typeparam name="TContinuation">The fluent continuation returned after the predicate is captured.<br/></typeparam>
    /// <typeparam name="TEnum">The CLR enum type supplying the mask.<br/></typeparam>
    /// <param name="part">The typed composite part receiving the predicate.<br/></param>
    /// <param name="bitMask">The enum mask checked-converted to <typeparamref name="TScalar"/>.<br/></param>
    /// <returns>The next composite-condition continuation.<br/></returns>
    public static TContinuation BitAnd<TScalar, TContinuation, TEnum>(
        this LibraDexCompositeTypedPart<TScalar, TContinuation> part,
        TEnum bitMask)
        where TScalar : IBitwiseOperators<TScalar, TScalar, TScalar>
        where TEnum : struct, Enum
        => part.BitAnd(LibraDexEnumScalarConverter<TScalar, TEnum>.Convert(bitMask));

    /// <summary>
    /// Captures bitwise-AND inequality using enum operands checked-converted to the typed composite scalar.<br/>
    /// </summary>
    /// <typeparam name="TScalar">The numeric scalar type declared for the composite part.<br/></typeparam>
    /// <typeparam name="TContinuation">The fluent continuation returned after the predicate is captured.<br/></typeparam>
    /// <typeparam name="TEnum">The CLR enum type supplying both operands.<br/></typeparam>
    /// <param name="part">The typed composite part receiving the predicate.<br/></param>
    /// <param name="bitMask">The enum mask checked-converted to <typeparamref name="TScalar"/>.<br/></param>
    /// <param name="notEqualTo">The enum comparison value checked-converted to <typeparamref name="TScalar"/>.<br/></param>
    /// <returns>The next composite-condition continuation.<br/></returns>
    public static TContinuation BitAndNotEqualTo<TScalar, TContinuation, TEnum>(
        this LibraDexCompositeTypedPart<TScalar, TContinuation> part,
        TEnum bitMask,
        TEnum notEqualTo)
        where TScalar : IBitwiseOperators<TScalar, TScalar, TScalar>
        where TEnum : struct, Enum
        => part.BitAndNotEqualTo(
            LibraDexEnumScalarConverter<TScalar, TEnum>.Convert(bitMask),
            LibraDexEnumScalarConverter<TScalar, TEnum>.Convert(notEqualTo));

    /// <summary>
    /// Captures bitwise-AND inequality using a scalar mask and a checked-converted enum comparison value.<br/>
    /// </summary>
    /// <typeparam name="TScalar">The numeric scalar type declared for the composite part.<br/></typeparam>
    /// <typeparam name="TContinuation">The fluent continuation returned after the predicate is captured.<br/></typeparam>
    /// <typeparam name="TEnum">The CLR enum type supplying the comparison value.<br/></typeparam>
    /// <param name="part">The typed composite part receiving the predicate.<br/></param>
    /// <param name="bitMask">The scalar bits retained by the AND operation.<br/></param>
    /// <param name="notEqualTo">The enum comparison value checked-converted to <typeparamref name="TScalar"/>.<br/></param>
    /// <returns>The next composite-condition continuation.<br/></returns>
    public static TContinuation BitAndNotEqualTo<TScalar, TContinuation, TEnum>(
        this LibraDexCompositeTypedPart<TScalar, TContinuation> part,
        TScalar bitMask,
        TEnum notEqualTo)
        where TScalar : IBitwiseOperators<TScalar, TScalar, TScalar>
        where TEnum : struct, Enum
        => part.BitAndNotEqualTo(bitMask, LibraDexEnumScalarConverter<TScalar, TEnum>.Convert(notEqualTo));

    /// <summary>
    /// Captures bitwise-AND inequality using a checked-converted enum mask and a scalar comparison value.<br/>
    /// </summary>
    /// <typeparam name="TScalar">The numeric scalar type declared for the composite part.<br/></typeparam>
    /// <typeparam name="TContinuation">The fluent continuation returned after the predicate is captured.<br/></typeparam>
    /// <typeparam name="TEnum">The CLR enum type supplying the mask.<br/></typeparam>
    /// <param name="part">The typed composite part receiving the predicate.<br/></param>
    /// <param name="bitMask">The enum mask checked-converted to <typeparamref name="TScalar"/>.<br/></param>
    /// <param name="notEqualTo">The scalar value the retained bits must not equal.<br/></param>
    /// <returns>The next composite-condition continuation.<br/></returns>
    public static TContinuation BitAndNotEqualTo<TScalar, TContinuation, TEnum>(
        this LibraDexCompositeTypedPart<TScalar, TContinuation> part,
        TEnum bitMask,
        TScalar notEqualTo)
        where TScalar : IBitwiseOperators<TScalar, TScalar, TScalar>
        where TEnum : struct, Enum
        => part.BitAndNotEqualTo(LibraDexEnumScalarConverter<TScalar, TEnum>.Convert(bitMask), notEqualTo);

    /// <summary>
    /// Requires every bit in a checked-converted enum mask on the typed composite scalar.<br/>
    /// </summary>
    /// <typeparam name="TScalar">The numeric scalar type declared for the composite part.<br/></typeparam>
    /// <typeparam name="TContinuation">The fluent continuation returned after the predicate is captured.<br/></typeparam>
    /// <typeparam name="TEnum">The CLR enum type supplying the mask.<br/></typeparam>
    /// <param name="part">The typed composite part receiving the predicate.<br/></param>
    /// <param name="bitMask">The enum bits that must all be set after checked conversion.<br/></param>
    /// <returns>The next composite-condition continuation.<br/></returns>
    public static TContinuation AllBitsSet<TScalar, TContinuation, TEnum>(
        this LibraDexCompositeTypedPart<TScalar, TContinuation> part,
        TEnum bitMask)
        where TScalar : IBitwiseOperators<TScalar, TScalar, TScalar>
        where TEnum : struct, Enum
        => part.AllBitsSet(LibraDexEnumScalarConverter<TScalar, TEnum>.Convert(bitMask));

    /// <summary>
    /// Requires any bit in a checked-converted enum mask on the typed composite scalar.<br/>
    /// </summary>
    /// <typeparam name="TScalar">The numeric scalar type declared for the composite part.<br/></typeparam>
    /// <typeparam name="TContinuation">The fluent continuation returned after the predicate is captured.<br/></typeparam>
    /// <typeparam name="TEnum">The CLR enum type supplying the mask.<br/></typeparam>
    /// <param name="part">The typed composite part receiving the predicate.<br/></param>
    /// <param name="bitMask">The enum bits from which at least one must be set after checked conversion.<br/></param>
    /// <returns>The next composite-condition continuation.<br/></returns>
    public static TContinuation AnyBitsSet<TScalar, TContinuation, TEnum>(
        this LibraDexCompositeTypedPart<TScalar, TContinuation> part,
        TEnum bitMask)
        where TScalar : IBitwiseOperators<TScalar, TScalar, TScalar>
        where TEnum : struct, Enum
        => part.AnyBitsSet(LibraDexEnumScalarConverter<TScalar, TEnum>.Convert(bitMask));

    /// <summary>
    /// Requires no bit in a checked-converted enum mask on the typed composite scalar.<br/>
    /// </summary>
    /// <typeparam name="TScalar">The numeric scalar type declared for the composite part.<br/></typeparam>
    /// <typeparam name="TContinuation">The fluent continuation returned after the predicate is captured.<br/></typeparam>
    /// <typeparam name="TEnum">The CLR enum type supplying the mask.<br/></typeparam>
    /// <param name="part">The typed composite part receiving the predicate.<br/></param>
    /// <param name="bitMask">The enum bits that must all be clear after checked conversion.<br/></param>
    /// <returns>The next composite-condition continuation.<br/></returns>
    public static TContinuation NoBitsSet<TScalar, TContinuation, TEnum>(
        this LibraDexCompositeTypedPart<TScalar, TContinuation> part,
        TEnum bitMask)
        where TScalar : IBitwiseOperators<TScalar, TScalar, TScalar>
        where TEnum : struct, Enum
        => part.NoBitsSet(LibraDexEnumScalarConverter<TScalar, TEnum>.Convert(bitMask));
}
