using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace LibraDex;

public sealed class LibraDexCompositeConditionWhere
{
    private readonly LibraDexConditionBuilder builder;
    private readonly LibraDexConditionIndexSelector indexSelector;

    internal LibraDexCompositeConditionWhere(LibraDexConditionBuilder builder, LibraDexConditionIndexSelector indexSelector)
    {
        this.builder = builder;
        this.indexSelector = indexSelector;
    }

    /// <summary>
    /// Captures one routed composite predicate over one or more named key parts.<br/>
    /// Supplying multiple parts lets the composite executor route from the first constrained tier into the next tier without requiring callers to flatten criteria into a synthetic key.<br/>
    /// </summary>
    /// <param name="parts">The ordered or named part predicates to apply.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd Where(params LibraDexCompositePartCriterion[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Length == 0)
        {
            throw new ArgumentException("Composite conditions require at least one part predicate.", nameof(parts));
        }

        return builder.AddLeaf(new LibraDexConditionLeafDescriptor(
            indexSelector,
            LibraDexConditionValueKind.Composite,
            LibraDexConditionOperatorKind.CompositeMatch,
            parts.Select(static part => LibraDexConditionOperand.Value(part)).ToArray(),
            IgnoreCase: false,
            Culture: null));
    }

    /// <summary>
    /// Selects one declared key part inside the current composite index.<br/>
    /// The returned selector separates part selection from type interpretation so generated callers can choose the part name first and later decide whether to treat the stored component as text, bytes, GUID, date/time, or scalar data.<br/>
    /// </summary>
    /// <param name="name">The composite key-part name declared when the index was created.</param>
    /// <returns>A typed selector for the named composite key part.</returns>
    public LibraDexCompositeConditionKeyPartSelector KeyPart(string name)
    {
        return new LibraDexCompositeConditionKeyPartSelector(
            new LibraDexCompositeKeyPartCondition(name),
            Capture);
    }

    /// <summary>
    /// Starts an explicit whole-composite key predicate using index-order key parts and no delimiter.<br/>
    /// This is an ANR-style convenience surface: LibraDex compares against the developer-selected full-key representation instead of inventing implicit cross-part text behavior.<br/>
    /// </summary>
    /// <returns>A full-key predicate builder with no delimiter.</returns>
    public LibraDexCompositeConditionFullKeySelector FullKey()
        => new LibraDexCompositeConditionFullKeySelector(LibraDexCompositePart.FullKey(), Capture);

    /// <summary>
    /// Starts an explicit whole-composite key predicate using index-order key parts and a caller-supplied delimiter.<br/>
    /// The delimiter participates in the full-key representation so callers can make boundary-aware contains or pattern requests without changing the physical composite index shape.<br/>
    /// </summary>
    /// <param name="delimiter">The delimiter inserted between encoded composite key parts.</param>
    /// <returns>A full-key predicate builder using the supplied delimiter.</returns>
    public LibraDexCompositeConditionFullKeySelector FullKey(string delimiter)
        => new LibraDexCompositeConditionFullKeySelector(LibraDexCompositePart.FullKey(delimiter), Capture);

    internal LibraDexCompositeConditionContinuation Capture(LibraDexCompositePartCriterion part)
        => new LibraDexCompositeConditionContinuation(Where(part), indexSelector);
}

/// <summary>
/// Continues or ends a composite-index condition after one routed composite predicate has been captured.<br/>
/// The continuation keeps the same composite index selected so handwritten code can naturally chain `.And.KeyPart(...)` or `.Or.FullKey(...)` without repeating the index name.<br/>
/// </summary>
public sealed class LibraDexCompositeConditionContinuation
{
    private readonly LibraDexConditionContinueOrEnd continuation;
    private readonly LibraDexConditionIndexSelector indexSelector;

    internal LibraDexCompositeConditionContinuation(
        LibraDexConditionContinueOrEnd continuation,
        LibraDexConditionIndexSelector indexSelector)
    {
        this.continuation = continuation;
        this.indexSelector = indexSelector;
    }

    /// <summary>
    /// Adds an intersection operator and continues the same composite-index condition grammar.<br/>
    /// </summary>
    public LibraDexCompositeConditionWhere And => continuation.AND.Index(indexSelector).CompositeWhereRoot;

    /// <summary>
    /// Adds a union operator and continues the same composite-index condition grammar.<br/>
    /// </summary>
    public LibraDexCompositeConditionWhere Or => continuation.OR.Index(indexSelector).CompositeWhereRoot;

    /// <summary>
    /// Completes the condition descriptor.<br/>
    /// </summary>
    public LibraDexConditionEndCondition EndCondition => continuation.EndCondition;

    /// <summary>
    /// Convenience alias for <see cref="EndCondition"/> that matches Abraxas' short `ec` alias.<br/>
    /// </summary>
    public LibraDexConditionEndCondition ec => EndCondition;
}

/// <summary>
/// Selects type-specific predicate operators for one declared composite key part in an opened-index condition chain.<br/>
/// Operators returned from this selector immediately capture their criterion into the owning composite index condition, unlike the lower-level `LibraDexCompositePart` descriptor helpers.<br/>
/// </summary>
public sealed class LibraDexCompositeConditionKeyPartSelector
{
    private readonly LibraDexCompositeKeyPartCondition inner;
    private readonly Func<LibraDexCompositePartCriterion, LibraDexCompositeConditionContinuation> capture;

    internal LibraDexCompositeConditionKeyPartSelector(
        LibraDexCompositeKeyPartCondition inner,
        Func<LibraDexCompositePartCriterion, LibraDexCompositeConditionContinuation> capture)
    {
        this.inner = inner;
        this.capture = capture;
    }

    /// <summary>
    /// Interprets the selected composite key part as string data and captures string operators into the owning composite condition.<br/>
    /// </summary>
    public LibraDexCompositeConditionStringOperator AsString => new(inner.AsString, capture);

    /// <summary>
    /// Interprets the selected composite key part as a GUID stored in the binary GUID domain.<br/>
    /// </summary>
    public LibraDexCompositeConditionGuidOperator AsGuid => new(inner.AsGuid, capture);

    /// <summary>
    /// Interprets the selected composite key part as raw binary data and captures byte-domain operators into the owning composite condition.<br/>
    /// </summary>
    public LibraDexCompositeConditionBinaryOperator AsBinary => new(inner.AsBinary, capture);

    /// <summary>
    /// Interprets the selected composite key part as structured date/time data.<br/>
    /// </summary>
    public LibraDexCompositeConditionDateOperator AsDate => new(inner.AsDate, capture);

    /// <summary>
    /// Interprets the selected composite key part as an ordered scalar value.<br/>
    /// </summary>
    /// <typeparam name="TValue">The scalar component value type.</typeparam>
    /// <returns>Scalar operators that capture into the owning composite condition.</returns>
    public LibraDexCompositeConditionScalarOperator<TValue> AsScalar<TValue>()
        => new LibraDexCompositeConditionScalarOperator<TValue>(inner.AsScalar<TValue>(), capture);
}

/// <summary>
/// Selects a comparison domain for the full composite key in an opened-index condition chain.<br/>
/// Operators returned from this selector immediately capture their criterion into the owning composite index condition.<br/>
/// </summary>
public sealed class LibraDexCompositeConditionFullKeySelector
{
    private readonly LibraDexCompositeFullKeyCondition inner;
    private readonly Func<LibraDexCompositePartCriterion, LibraDexCompositeConditionContinuation> capture;

    internal LibraDexCompositeConditionFullKeySelector(
        LibraDexCompositeFullKeyCondition inner,
        Func<LibraDexCompositePartCriterion, LibraDexCompositeConditionContinuation> capture)
    {
        this.inner = inner;
        this.capture = capture;
    }

    /// <summary>
    /// Narrows the full composite key to selected key-part names while preserving composite index order.<br/>
    /// </summary>
    /// <param name="partNames">The composite key-part names to include in the full-key value.</param>
    /// <returns>A narrowed full-key selector.</returns>
    public LibraDexCompositeConditionFullKeySelector Parts(params string[] partNames)
        => new LibraDexCompositeConditionFullKeySelector(inner.Parts(partNames), capture);

    /// <summary>
    /// Excludes selected key-part names from the full composite key while preserving composite index order for the remaining parts.<br/>
    /// </summary>
    /// <param name="partNames">The composite key-part names to exclude from the full-key value.</param>
    /// <returns>A narrowed full-key selector.</returns>
    public LibraDexCompositeConditionFullKeySelector Excluding(params string[] partNames)
        => new LibraDexCompositeConditionFullKeySelector(inner.Excluding(partNames), capture);

    /// <summary>
    /// Interprets the selected full composite key representation as string data.<br/>
    /// </summary>
    public LibraDexCompositeConditionFullKeyStringOperator AsString => new(inner.AsString, capture);
}

/// <summary>
/// Captures string key-part predicates into an opened composite index condition.<br/>
/// </summary>
public sealed class LibraDexCompositeConditionStringOperator
{
    private readonly LibraDexCompositeStringPartCondition inner;
    private readonly Func<LibraDexCompositePartCriterion, LibraDexCompositeConditionContinuation> capture;

    internal LibraDexCompositeConditionStringOperator(
        LibraDexCompositeStringPartCondition inner,
        Func<LibraDexCompositePartCriterion, LibraDexCompositeConditionContinuation> capture)
    {
        this.inner = inner;
        this.capture = capture;
    }

    /// <summary>
    /// Captures equality against the selected string key part.<br/>
    /// The produced predicate is immediately attached to the owning composite index condition.<br/>
    /// </summary>
    /// <param name="value">The string value to match.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation EqualTo(string value, bool ignoreCase = false, string? culture = null) => capture(inner.EqualTo(value, ignoreCase, culture));

    /// <summary>
    /// Captures equality against an explicit null or empty string key-part state.<br/>
    /// `NullKey.Null` matches the stored composite null route, `NullKey.Empty` matches the stored empty-string route, and `NullKey.NullOrEmpty` matches either state without caller-side grouped OR expansion.<br/>
    /// </summary>
    /// <param name="keyState">The string key-part state to match.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation EqualTo(NullKey keyState) => capture(inner.EqualTo(keyState));

    /// <summary>
    /// Captures inequality against the selected string key part.<br/>
    /// This mirrors the normal string `.Where` operator while still emitting one composite part criterion into the owning `CompositeMatch` leaf.<br/>
    /// </summary>
    /// <param name="value">The string value to exclude.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation NotEqualTo(string value, bool ignoreCase = false, string? culture = null) => capture(inner.NotEqualTo(value, ignoreCase, culture));

    /// <summary>
    /// Captures inequality against an explicit null or empty string key-part state.<br/>
    /// This is the composite-part counterpart to ordinary string-index `NotEqualTo(NullKey...)` and is the concise presence predicate for stored string parts.<br/>
    /// </summary>
    /// <param name="keyState">The string key-part state to exclude.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation NotEqualTo(NullKey keyState) => capture(inner.NotEqualTo(keyState));

    /// <summary>
    /// Captures a prefix predicate against the selected string key part.<br/>
    /// The produced predicate is immediately attached to the owning composite index condition.<br/>
    /// </summary>
    /// <param name="value">The prefix value.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation StartsWith(string value, bool ignoreCase = false, string? culture = null) => capture(inner.StartsWith(value, ignoreCase, culture));

    /// <summary>
    /// Captures a suffix predicate against the selected string key part.<br/>
    /// The produced predicate is immediately attached to the owning composite index condition.<br/>
    /// </summary>
    /// <param name="value">The suffix value.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation EndsWith(string value, bool ignoreCase = false, string? culture = null) => capture(inner.EndsWith(value, ignoreCase, culture));

    /// <summary>
    /// Captures a containment predicate against the selected string key part.<br/>
    /// The produced predicate is immediately attached to the owning composite index condition.<br/>
    /// </summary>
    /// <param name="value">The contained value.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation Contains(string value, bool ignoreCase = false, string? culture = null) => capture(inner.Contains(value, ignoreCase, culture));

    /// <summary>
    /// Captures a wildcard pattern predicate against the selected string key part.<br/>
    /// The produced predicate is immediately attached to the owning composite index condition.<br/>
    /// </summary>
    /// <param name="pattern">The wildcard pattern to apply.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation MatchesPattern(string pattern, bool ignoreCase = false, string? culture = null) => capture(inner.MatchesPattern(pattern, ignoreCase, culture));

    /// <summary>
    /// Captures a pattern text condition over the selected composite string component using the short public spelling.<br/>
    /// This is the preferred alias for <see cref="MatchesPattern(string, bool, string?)"/> and preserves the same case and culture metadata.<br/>
    /// </summary>
    public LibraDexCompositeConditionContinuation Matches(string pattern, bool ignoreCase = false, string? culture = null) => MatchesPattern(pattern, ignoreCase, culture);

    /// <summary>
    /// Captures a greater-than predicate against the selected string key part.<br/>
    /// The produced predicate is immediately attached to the owning composite index condition.<br/>
    /// </summary>
    /// <param name="value">The exclusive lower value.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation GreaterThan(string value, bool ignoreCase = false, string? culture = null) => capture(inner.GreaterThan(value, ignoreCase, culture));

    /// <summary>
    /// Captures a greater-than-or-equal predicate against the selected string key part.<br/>
    /// The produced predicate is immediately attached to the owning composite index condition.<br/>
    /// </summary>
    /// <param name="value">The inclusive lower value.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation GreaterOrEqual(string value, bool ignoreCase = false, string? culture = null) => capture(inner.GreaterOrEqual(value, ignoreCase, culture));

    /// <summary>
    /// Captures a less-than predicate against the selected string key part.<br/>
    /// The produced predicate is immediately attached to the owning composite index condition.<br/>
    /// </summary>
    /// <param name="value">The exclusive upper value.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation LessThan(string value, bool ignoreCase = false, string? culture = null) => capture(inner.LessThan(value, ignoreCase, culture));

    /// <summary>
    /// Captures a less-than-or-equal predicate against the selected string key part.<br/>
    /// The produced predicate is immediately attached to the owning composite index condition.<br/>
    /// </summary>
    /// <param name="value">The inclusive upper value.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation LessOrEqual(string value, bool ignoreCase = false, string? culture = null) => capture(inner.LessOrEqual(value, ignoreCase, culture));

    /// <summary>
    /// Captures an inclusive range predicate against the selected string key part.<br/>
    /// The produced predicate is immediately attached to the owning composite index condition.<br/>
    /// </summary>
    /// <param name="lower">The inclusive lower value.</param>
    /// <param name="upper">The inclusive upper value.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation Between(string lower, string upper, bool ignoreCase = false, string? culture = null) => capture(inner.Between(lower, upper, ignoreCase, culture));

    /// <summary>
    /// Captures an outside-range predicate against the selected string key part.<br/>
    /// This preserves normal `.Where.AsString.NotBetween(...)` vocabulary while the composite executor evaluates the exclusion inside the routed part predicate.<br/>
    /// </summary>
    /// <param name="lower">The inclusive lower value of the excluded window.</param>
    /// <param name="upper">The inclusive upper value of the excluded window.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation NotBetween(string lower, string upper, bool ignoreCase = false, string? culture = null) => capture(inner.NotBetween(lower, upper, ignoreCase, culture));

    /// <summary>
    /// Captures membership against the selected string key part.<br/>
    /// The enumerable is captured as one criterion operand so the routed composite executor can evaluate set membership without expanding the fluent expression into caller-side `OR` clauses.<br/>
    /// </summary>
    /// <param name="values">The string values to match.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation InSet(IEnumerable<string> values, bool ignoreCase = false, string? culture = null) => capture(inner.InSet(values, ignoreCase, culture));

    /// <summary>
    /// Captures membership against the selected string key part using LibraDex's short set spelling.<br/>
    /// This is an alias for <see cref="InSet(IEnumerable{string}, bool, string?)"/> and preserves the same composite part criterion shape.<br/>
    /// </summary>
    /// <param name="values">The string values to match.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation In(IEnumerable<string> values, bool ignoreCase = false, string? culture = null) => InSet(values, ignoreCase, culture);

    /// <summary>
    /// Captures Abraxas-style membership against the selected string key part.<br/>
    /// This is an alias for <see cref="InSet(IEnumerable{string}, bool, string?)"/> so copied condition expressions keep their source grammar.<br/>
    /// </summary>
    /// <param name="values">The string values to match.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation IsIn(IEnumerable<string> values, bool ignoreCase = false, string? culture = null) => InSet(values, ignoreCase, culture);

    /// <summary>
    /// Captures membership exclusion against the selected string key part.<br/>
    /// The set is stored as one composite part criterion operand, matching normal `.Where.AsString.NotInSet(...)` intent without flattening the composite route.<br/>
    /// </summary>
    /// <param name="values">The string values to exclude.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation NotInSet(IEnumerable<string> values, bool ignoreCase = false, string? culture = null) => capture(inner.NotInSet(values, ignoreCase, culture));

    /// <summary>
    /// Captures membership exclusion against the selected string key part using LibraDex's short set spelling.<br/>
    /// This is an alias for <see cref="NotInSet(IEnumerable{string}, bool, string?)"/> and emits the same composite part criterion.<br/>
    /// </summary>
    /// <param name="values">The string values to exclude.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation NotIn(IEnumerable<string> values, bool ignoreCase = false, string? culture = null) => NotInSet(values, ignoreCase, culture);

    /// <summary>
    /// Captures Abraxas-style membership exclusion against the selected string key part.<br/>
    /// This is an alias for <see cref="NotInSet(IEnumerable{string}, bool, string?)"/> so copied condition expressions keep their source grammar.<br/>
    /// </summary>
    /// <param name="values">The string values to exclude.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation IsNotIn(IEnumerable<string> values, bool ignoreCase = false, string? culture = null) => NotInSet(values, ignoreCase, culture);
}

/// <summary>
/// Captures full-key string predicates into an opened composite index condition.<br/>
/// </summary>
public sealed class LibraDexCompositeConditionFullKeyStringOperator
{
    private readonly LibraDexCompositeFullKeyStringCondition inner;
    private readonly Func<LibraDexCompositePartCriterion, LibraDexCompositeConditionContinuation> capture;

    internal LibraDexCompositeConditionFullKeyStringOperator(
        LibraDexCompositeFullKeyStringCondition inner,
        Func<LibraDexCompositePartCriterion, LibraDexCompositeConditionContinuation> capture)
    {
        this.inner = inner;
        this.capture = capture;
    }

    /// <summary>
    /// Captures equality against the full composite key string representation.<br/>
    /// </summary>
    /// <param name="value">The string value to match.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation EqualTo(string value, bool ignoreCase = false, string? culture = null) => capture(inner.EqualTo(value, ignoreCase, culture));

    /// <summary>
    /// Captures equality against an encoded typed operand inside the full composite key.<br/>
    /// </summary>
    /// <param name="value">The typed operand to encode once into the full-key byte domain.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation EqualTo(object value) => capture(inner.EqualTo(value));

    /// <summary>
    /// Captures a string prefix predicate against the full composite key representation.<br/>
    /// </summary>
    /// <param name="value">The prefix value.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation StartsWith(string value, bool ignoreCase = false, string? culture = null) => capture(inner.StartsWith(value, ignoreCase, culture));

    /// <summary>
    /// Captures an encoded typed prefix predicate inside the full composite key.<br/>
    /// </summary>
    /// <param name="value">The typed operand to encode as a prefix.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation StartsWith(object value) => capture(inner.StartsWith(value));

    /// <summary>
    /// Captures a string suffix predicate against the full composite key representation.<br/>
    /// </summary>
    /// <param name="value">The suffix value.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation EndsWith(string value, bool ignoreCase = false, string? culture = null) => capture(inner.EndsWith(value, ignoreCase, culture));

    /// <summary>
    /// Captures an encoded typed suffix predicate inside the full composite key.<br/>
    /// </summary>
    /// <param name="value">The typed operand to encode as a suffix.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation EndsWith(object value) => capture(inner.EndsWith(value));

    /// <summary>
    /// Captures a string containment predicate against the full composite key representation.<br/>
    /// </summary>
    /// <param name="value">The contained value.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation Contains(string value, bool ignoreCase = false, string? culture = null) => capture(inner.Contains(value, ignoreCase, culture));

    /// <summary>
    /// Captures an encoded typed containment predicate inside the full composite key.<br/>
    /// </summary>
    /// <param name="value">The typed operand to encode and search for.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation Contains(object value) => capture(inner.Contains(value));

    /// <summary>
    /// Captures a wildcard pattern predicate against the full composite key representation.<br/>
    /// </summary>
    /// <param name="pattern">The wildcard pattern to apply.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation MatchesPattern(string pattern, bool ignoreCase = false, string? culture = null) => capture(inner.MatchesPattern(pattern, ignoreCase, culture));

    /// <summary>
    /// Captures a pattern text condition over the selected composite full-key string view using the short public spelling.<br/>
    /// This is the preferred alias for <see cref="MatchesPattern(string, bool, string?)"/> and preserves the same case and culture metadata.<br/>
    /// </summary>
    public LibraDexCompositeConditionContinuation Matches(string pattern, bool ignoreCase = false, string? culture = null) => MatchesPattern(pattern, ignoreCase, culture);
}

/// <summary>
/// Captures GUID key-part predicates into an opened composite index condition.<br/>
/// </summary>
public sealed class LibraDexCompositeConditionGuidOperator
{
    private readonly LibraDexCompositeGuidPartCondition inner;
    private readonly Func<LibraDexCompositePartCriterion, LibraDexCompositeConditionContinuation> capture;

    internal LibraDexCompositeConditionGuidOperator(
        LibraDexCompositeGuidPartCondition inner,
        Func<LibraDexCompositePartCriterion, LibraDexCompositeConditionContinuation> capture)
    {
        this.inner = inner;
        this.capture = capture;
    }

    /// <summary>
    /// Captures equality against the selected GUID key part.<br/>
    /// </summary>
    /// <param name="value">The GUID value to match.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation EqualTo(Guid value) => capture(inner.EqualTo(value));

    /// <summary>
    /// Captures equality against a scalar null-state for the selected GUID key part.<br/>
    /// `ScalarNull.Null` matches the stored composite null route and `ScalarNull.NonNull` matches ordinary GUID values without requiring a caller-owned sentinel GUID.<br/>
    /// </summary>
    /// <param name="state">The scalar null-state predicate to match.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation EqualTo(ScalarNull state) => capture(inner.EqualTo(state));

    /// <summary>
    /// Captures inequality against the selected GUID key part.<br/>
    /// </summary>
    /// <param name="value">The GUID value to exclude.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation NotEqualTo(Guid value) => capture(inner.NotEqualTo(value));

    /// <summary>
    /// Captures inequality against a scalar null-state for the selected GUID key part.<br/>
    /// This mirrors ordinary scalar-index null-state vocabulary while keeping the predicate inside the routed composite part match.<br/>
    /// </summary>
    /// <param name="state">The scalar null-state predicate to exclude.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation NotEqualTo(ScalarNull state) => capture(inner.NotEqualTo(state));

    /// <summary>
    /// Captures a binary-prefix predicate against the selected GUID key part.<br/>
    /// </summary>
    /// <param name="value">The GUID value that supplies prefix bytes.</param>
    /// <param name="byteCount">The number of leading GUID bytes to compare.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation StartsWith(Guid value, int byteCount) => capture(inner.StartsWith(value, byteCount));

    /// <summary>
    /// Captures GUID membership against the selected key part.<br/>
    /// The enumerable is stored as one composite part criterion operand so callers do not need to expand tenant sets into grouped `OR` syntax.<br/>
    /// </summary>
    /// <param name="values">The GUID values to match.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation InSet(IEnumerable<Guid> values) => capture(inner.InSet(values));

    /// <summary>
    /// Captures GUID membership against the selected key part using LibraDex's short set spelling.<br/>
    /// This is an alias for <see cref="InSet(IEnumerable{Guid})"/> and emits the same composite part criterion.<br/>
    /// </summary>
    /// <param name="values">The GUID values to match.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation In(IEnumerable<Guid> values) => InSet(values);

    /// <summary>
    /// Captures Abraxas-style GUID membership against the selected key part.<br/>
    /// This is an alias for <see cref="InSet(IEnumerable{Guid})"/> so copied condition expressions keep their source grammar.<br/>
    /// </summary>
    /// <param name="values">The GUID values to match.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation IsIn(IEnumerable<Guid> values) => InSet(values);

    /// <summary>
    /// Captures GUID membership exclusion against the selected key part.<br/>
    /// The enumerable is stored as one composite part criterion operand and evaluated by the routed composite executor.<br/>
    /// </summary>
    /// <param name="values">The GUID values to exclude.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation NotInSet(IEnumerable<Guid> values) => capture(inner.NotInSet(values));

    /// <summary>
    /// Captures GUID membership exclusion against the selected key part using LibraDex's short set spelling.<br/>
    /// This is an alias for <see cref="NotInSet(IEnumerable{Guid})"/> and emits the same composite part criterion.<br/>
    /// </summary>
    /// <param name="values">The GUID values to exclude.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation NotIn(IEnumerable<Guid> values) => NotInSet(values);

    /// <summary>
    /// Captures Abraxas-style GUID membership exclusion against the selected key part.<br/>
    /// This is an alias for <see cref="NotInSet(IEnumerable{Guid})"/> so copied condition expressions keep their source grammar.<br/>
    /// </summary>
    /// <param name="values">The GUID values to exclude.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation IsNotIn(IEnumerable<Guid> values) => NotInSet(values);
}

/// <summary>
/// Captures binary key-part predicates into an opened composite index condition.<br/>
/// </summary>
public sealed class LibraDexCompositeConditionBinaryOperator
{
    private readonly LibraDexCompositeBinaryPartCondition inner;
    private readonly Func<LibraDexCompositePartCriterion, LibraDexCompositeConditionContinuation> capture;

    internal LibraDexCompositeConditionBinaryOperator(
        LibraDexCompositeBinaryPartCondition inner,
        Func<LibraDexCompositePartCriterion, LibraDexCompositeConditionContinuation> capture)
    {
        this.inner = inner;
        this.capture = capture;
    }

    /// <summary>
    /// Captures equality against the selected binary key part.<br/>
    /// The byte-array operand is retained in binary form so composite matching does not render or parse text.<br/>
    /// </summary>
    /// <param name="value">The byte sequence to match.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation EqualTo(byte[] value) => capture(inner.EqualTo(value));

    /// <summary>
    /// Captures equality against an explicit null or empty binary key-part state.<br/>
    /// `NullKey.Null` matches the stored composite null route and `NullKey.Empty` matches the stored empty byte-array route.<br/>
    /// </summary>
    /// <param name="keyState">The binary key-part state to match.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation EqualTo(NullKey keyState) => capture(inner.EqualTo(keyState));

    /// <summary>
    /// Captures inequality against the selected binary key part.<br/>
    /// </summary>
    /// <param name="value">The byte sequence to exclude.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation NotEqualTo(byte[] value) => capture(inner.NotEqualTo(value));

    /// <summary>
    /// Captures inequality against an explicit null or empty binary key-part state.<br/>
    /// This is the composite-part counterpart to ordinary binary-index `NotEqualTo(NullKey...)` and provides the concise binary presence predicate.<br/>
    /// </summary>
    /// <param name="keyState">The binary key-part state to exclude.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation NotEqualTo(NullKey keyState) => capture(inner.NotEqualTo(keyState));

    /// <summary>
    /// Captures a byte-prefix predicate against the selected binary key part.<br/>
    /// </summary>
    /// <param name="value">The byte prefix to match.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation StartsWith(byte[] value) => capture(inner.StartsWith(value));

    /// <summary>
    /// Captures a byte-suffix predicate against the selected binary key part.<br/>
    /// </summary>
    /// <param name="value">The byte suffix to match.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation EndsWith(byte[] value) => capture(inner.EndsWith(value));

    /// <summary>
    /// Captures a byte containment predicate against the selected binary key part.<br/>
    /// </summary>
    /// <param name="value">The byte sequence that must appear inside the part value.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation Contains(byte[] value) => capture(inner.Contains(value));
}

/// <summary>
/// Captures structured date/time key-part predicates into an opened composite index condition.<br/>
/// </summary>
public sealed class LibraDexCompositeConditionDateOperator
{
    private readonly LibraDexCompositeDatePartCondition inner;
    private readonly Func<LibraDexCompositePartCriterion, LibraDexCompositeConditionContinuation> capture;

    internal LibraDexCompositeConditionDateOperator(
        LibraDexCompositeDatePartCondition inner,
        Func<LibraDexCompositePartCriterion, LibraDexCompositeConditionContinuation> capture)
    {
        this.inner = inner;
        this.capture = capture;
    }

    /// <summary>
    /// Captures a year equality predicate against the selected structured date key part.<br/>
    /// </summary>
    /// <param name="year">The year component to match.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation YearEqualTo(int year) => capture(inner.YearEqualTo(year));

    /// <summary>
    /// Captures a year equality predicate using Abraxas-style naming.<br/>
    /// </summary>
    /// <param name="year">The year component to match.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation YearEqual(int year) => capture(inner.YearEqual(year));

    /// <summary>
    /// Captures an inclusive year range predicate against the selected structured date key part.<br/>
    /// </summary>
    /// <param name="startYear">The inclusive starting year.</param>
    /// <param name="endYear">The inclusive ending year.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation YearRange(int startYear, int endYear) => capture(inner.YearRange(startYear, endYear));

    /// <summary>
    /// Captures a year-month predicate against the selected structured date key part.<br/>
    /// </summary>
    /// <param name="year">The year component.</param>
    /// <param name="month">The month component.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation YearMonth(int year, int month) => capture(inner.YearMonth(year, month));

    /// <summary>
    /// Captures a year-month-day predicate against the selected structured date key part.<br/>
    /// </summary>
    /// <param name="year">The year component.</param>
    /// <param name="month">The month component.</param>
    /// <param name="day">The day component.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation YearMonthDay(int year, int month, int day) => capture(inner.YearMonthDay(year, month, day));

    /// <summary>
    /// Captures a month equality predicate across any year for the selected structured date key part.<br/>
    /// </summary>
    /// <param name="month">The month component.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation MonthEqualTo(int month) => capture(inner.MonthEqualTo(month));

    /// <summary>
    /// Captures a day equality predicate across any month and year for the selected structured date key part.<br/>
    /// </summary>
    /// <param name="day">The day component.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation DayEqualTo(int day) => capture(inner.DayEqualTo(day));

    /// <summary>
    /// Captures equality against a full DateTime value for the selected structured date key part.<br/>
    /// </summary>
    /// <param name="value">The DateTime value to match.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation EqualTo(DateTime value) => capture(inner.EqualTo(value));

    /// <summary>
    /// Captures equality against a scalar null-state for the selected date key part.<br/>
    /// `ScalarNull.Null` matches the stored composite null route and `ScalarNull.NonNull` matches ordinary DateTime values without requiring a sentinel date.<br/>
    /// </summary>
    /// <param name="state">The scalar null-state predicate to match.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation EqualTo(ScalarNull state) => capture(inner.EqualTo(state));

    /// <summary>
    /// Captures inequality against a full DateTime value for the selected structured date key part.<br/>
    /// </summary>
    /// <param name="value">The DateTime value to exclude.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation NotEqualTo(DateTime value) => capture(inner.NotEqualTo(value));

    /// <summary>
    /// Captures inequality against a scalar null-state for the selected date key part.<br/>
    /// This keeps optional date parts aligned with ordinary scalar/date index presence predicates.<br/>
    /// </summary>
    /// <param name="state">The scalar null-state predicate to exclude.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation NotEqualTo(ScalarNull state) => capture(inner.NotEqualTo(state));

    /// <summary>
    /// Captures a full DateTime greater-than predicate for the selected structured date key part.<br/>
    /// </summary>
    /// <param name="value">The exclusive lower DateTime value.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation GreaterThan(DateTime value) => capture(inner.GreaterThan(value));

    /// <summary>
    /// Captures a full DateTime greater-than-or-equal predicate for the selected structured date key part.<br/>
    /// </summary>
    /// <param name="value">The inclusive lower DateTime value.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation GreaterOrEqual(DateTime value) => capture(inner.GreaterOrEqual(value));

    /// <summary>
    /// Captures a full DateTime less-than predicate for the selected structured date key part.<br/>
    /// </summary>
    /// <param name="value">The exclusive upper DateTime value.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation LessThan(DateTime value) => capture(inner.LessThan(value));

    /// <summary>
    /// Captures a full DateTime less-than-or-equal predicate for the selected structured date key part.<br/>
    /// </summary>
    /// <param name="value">The inclusive upper DateTime value.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation LessOrEqual(DateTime value) => capture(inner.LessOrEqual(value));

    /// <summary>
    /// Captures an inclusive full DateTime range for the selected structured date key part.<br/>
    /// </summary>
    /// <param name="lower">The inclusive lower DateTime value.</param>
    /// <param name="upper">The inclusive upper DateTime value.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation Between(DateTime lower, DateTime upper) => capture(inner.Between(lower, upper));

    /// <summary>
    /// Captures a full DateTime outside-range predicate for the selected structured date key part.<br/>
    /// </summary>
    /// <param name="lower">The inclusive lower value of the excluded window.</param>
    /// <param name="upper">The inclusive upper value of the excluded window.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation NotBetween(DateTime lower, DateTime upper) => capture(inner.NotBetween(lower, upper));
}

/// <summary>
/// Captures scalar key-part predicates into an opened composite index condition.<br/>
/// </summary>
/// <typeparam name="TValue">The scalar component value type.</typeparam>
public sealed class LibraDexCompositeConditionScalarOperator<TValue>
{
    private readonly LibraDexCompositeScalarPartCondition<TValue> inner;
    private readonly Func<LibraDexCompositePartCriterion, LibraDexCompositeConditionContinuation> capture;

    internal LibraDexCompositeConditionScalarOperator(
        LibraDexCompositeScalarPartCondition<TValue> inner,
        Func<LibraDexCompositePartCriterion, LibraDexCompositeConditionContinuation> capture)
    {
        this.inner = inner;
        this.capture = capture;
    }

    /// <summary>
    /// Captures equality against the selected scalar key part.<br/>
    /// </summary>
    /// <param name="value">The scalar value to match.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation EqualTo(TValue value) => capture(inner.EqualTo(value));

    /// <summary>
    /// Captures equality against a scalar null-state for the selected scalar key part.<br/>
    /// `ScalarNull.Null` matches the stored composite null route and `ScalarNull.NonNull` matches ordinary scalar values.<br/>
    /// </summary>
    /// <param name="state">The scalar null-state predicate to match.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation EqualTo(ScalarNull state) => capture(inner.EqualTo(state));

    /// <summary>
    /// Captures inequality against the selected scalar key part.<br/>
    /// This mirrors the normal typed `.Where` operator while preserving routed composite part intent in the emitted criterion.<br/>
    /// </summary>
    /// <param name="value">The scalar value to exclude.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation NotEqualTo(TValue value) => capture(inner.NotEqualTo(value));

    /// <summary>
    /// Captures inequality against a scalar null-state for the selected scalar key part.<br/>
    /// This is the scalar composite-part counterpart to ordinary `.Where.NotEqualTo(ScalarNull...)` condition syntax.<br/>
    /// </summary>
    /// <param name="state">The scalar null-state predicate to exclude.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation NotEqualTo(ScalarNull state) => capture(inner.NotEqualTo(state));

    /// <summary>
    /// Captures a greater-than predicate against the selected scalar key part.<br/>
    /// </summary>
    /// <param name="value">The exclusive lower value.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation GreaterThan(TValue value) => capture(inner.GreaterThan(value));

    /// <summary>
    /// Captures a greater-than-or-equal predicate against the selected scalar key part.<br/>
    /// </summary>
    /// <param name="value">The inclusive lower value.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation GreaterOrEqual(TValue value) => capture(inner.GreaterOrEqual(value));

    /// <summary>
    /// Captures a less-than predicate against the selected scalar key part.<br/>
    /// </summary>
    /// <param name="value">The exclusive upper value.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation LessThan(TValue value) => capture(inner.LessThan(value));

    /// <summary>
    /// Captures a less-than-or-equal predicate against the selected scalar key part.<br/>
    /// </summary>
    /// <param name="value">The inclusive upper value.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation LessOrEqual(TValue value) => capture(inner.LessOrEqual(value));

    /// <summary>
    /// Captures an inclusive range predicate against the selected scalar key part.<br/>
    /// </summary>
    /// <param name="lower">The inclusive lower value.</param>
    /// <param name="upper">The inclusive upper value.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation Between(TValue lower, TValue upper) => capture(inner.Between(lower, upper));

    /// <summary>
    /// Captures an outside-range predicate against the selected scalar key part.<br/>
    /// This mirrors normal typed `.Where.NotBetween(...)` vocabulary and leaves execution inside the composite part predicate sink.<br/>
    /// </summary>
    /// <param name="lower">The inclusive lower value of the excluded window.</param>
    /// <param name="upper">The inclusive upper value of the excluded window.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation NotBetween(TValue lower, TValue upper) => capture(inner.NotBetween(lower, upper));

    /// <summary>
    /// Captures membership against the selected scalar key part.<br/>
    /// The enumerable is retained as one criterion operand so callers do not need to hand-expand composite membership into multiple clauses.<br/>
    /// </summary>
    /// <param name="values">The scalar values to match.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation InSet(IEnumerable<TValue> values) => capture(inner.InSet(values));

    /// <summary>
    /// Captures membership against the selected scalar key part using LibraDex's short set spelling.<br/>
    /// This is an alias for <see cref="InSet(IEnumerable{TValue})"/> and emits the same composite part criterion.<br/>
    /// </summary>
    /// <param name="values">The scalar values to match.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation In(IEnumerable<TValue> values) => InSet(values);

    /// <summary>
    /// Captures Abraxas-style membership against the selected scalar key part.<br/>
    /// This is an alias for <see cref="InSet(IEnumerable{TValue})"/> so copied condition expressions keep their source grammar.<br/>
    /// </summary>
    /// <param name="values">The scalar values to match.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation IsIn(IEnumerable<TValue> values) => InSet(values);

    /// <summary>
    /// Captures membership exclusion against the selected scalar key part.<br/>
    /// The enumerable is retained as one criterion operand and evaluated by the composite executor against the routed part value.<br/>
    /// </summary>
    /// <param name="values">The scalar values to exclude.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation NotInSet(IEnumerable<TValue> values) => capture(inner.NotInSet(values));

    /// <summary>
    /// Captures membership exclusion against the selected scalar key part using LibraDex's short set spelling.<br/>
    /// This is an alias for <see cref="NotInSet(IEnumerable{TValue})"/> and emits the same composite part criterion.<br/>
    /// </summary>
    /// <param name="values">The scalar values to exclude.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation NotIn(IEnumerable<TValue> values) => NotInSet(values);

    /// <summary>
    /// Captures Abraxas-style membership exclusion against the selected scalar key part.<br/>
    /// This is an alias for <see cref="NotInSet(IEnumerable{TValue})"/> so copied condition expressions keep their source grammar.<br/>
    /// </summary>
    /// <param name="values">The scalar values to exclude.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation IsNotIn(IEnumerable<TValue> values) => NotInSet(values);

    /// <summary>
    /// Captures a bitwise-AND equality predicate against the selected scalar key part.<br/>
    /// </summary>
    /// <param name="bitMask">The bit mask to apply.</param>
    /// <param name="equalTo">The expected masked value.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation BitAnd(TValue bitMask, TValue equalTo) => capture(inner.BitAnd(bitMask, equalTo));

    /// <summary>
    /// Captures a bitwise-AND zero predicate against the selected scalar key part.<br/>
    /// </summary>
    /// <param name="bitMask">The bit mask to apply.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation BitAnd(TValue bitMask) => capture(inner.BitAnd(bitMask));

    /// <summary>
    /// Captures a bitwise-AND inequality predicate against the selected scalar key part.<br/>
    /// </summary>
    /// <param name="bitMask">The bit mask to apply.</param>
    /// <param name="notEqualTo">The masked value that must not match.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation BitAndNotEqualTo(TValue bitMask, TValue notEqualTo) => capture(inner.BitAndNotEqualTo(bitMask, notEqualTo));

    /// <summary>
    /// Captures a predicate requiring every mask bit to be set on the selected scalar key part.<br/>
    /// </summary>
    /// <param name="bitMask">The bit mask whose bits must all be present.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation AllBitsSet(TValue bitMask) => capture(inner.AllBitsSet(bitMask));

    /// <summary>
    /// Captures a predicate requiring at least one mask bit to be set on the selected scalar key part.<br/>
    /// </summary>
    /// <param name="bitMask">The bit mask whose overlap is tested.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation AnyBitsSet(TValue bitMask) => capture(inner.AnyBitsSet(bitMask));

    /// <summary>
    /// Captures a predicate requiring no mask bits to be set on the selected scalar key part.<br/>
    /// </summary>
    /// <param name="bitMask">The bit mask whose bits must not overlap.</param>
    /// <returns>A continuation for the same composite index condition.</returns>
    public LibraDexCompositeConditionContinuation NoBitsSet(TValue bitMask) => capture(inner.NoBitsSet(bitMask));
}

/// <summary>
/// Selects type-specific predicates for one declared composite key part.<br/>
/// The selector keeps key-part choice separate from value interpretation, preserving Abraxas-style selector -> type -> operator flow while avoiding the ambiguous public `.Part(...)` vocabulary.<br/>
/// </summary>
public sealed class LibraDexCompositeKeyPartCondition
{
    private readonly string name;

    internal LibraDexCompositeKeyPartCondition(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        this.name = name;
    }

    /// <summary>
    /// Interprets the selected composite key part as string data.<br/>
    /// This should be used when the composite part descriptor stores a string-compatible key family; execution validates the part shape before materialization.<br/>
    /// </summary>
    public LibraDexCompositeStringPartCondition AsString => LibraDexCompositePart.String(name);

    /// <summary>
    /// Interprets the selected composite key part as a GUID stored in the binary GUID domain.<br/>
    /// GUID criteria stay binary; callers that want text-oriented GUID matching should declare and populate a string key part instead.<br/>
    /// </summary>
    public LibraDexCompositeGuidPartCondition AsGuid => LibraDexCompositePart.Guid(name);

    /// <summary>
    /// Interprets the selected composite key part as raw binary data.<br/>
    /// Binary predicates operate over byte sequences directly and are validated against byte-array composite part descriptors at materialization time.<br/>
    /// </summary>
    public LibraDexCompositeBinaryPartCondition AsBinary => LibraDexCompositePart.Binary(name);

    /// <summary>
    /// Interprets the selected composite key part as structured date/time data.<br/>
    /// Date criteria use the composite part's stored DateTime encoding contract rather than query-time text parsing.<br/>
    /// </summary>
    public LibraDexCompositeDatePartCondition AsDate => LibraDexCompositePart.Date(name);

    /// <summary>
    /// Interprets the selected composite key part as an ordered scalar value.<br/>
    /// The generic value type must match the composite part descriptor at materialization time.<br/>
    /// </summary>
    /// <typeparam name="TValue">The scalar component value type.</typeparam>
    /// <returns>Scalar predicates for the selected key part.</returns>
    public LibraDexCompositeScalarPartCondition<TValue> AsScalar<TValue>()
        => LibraDexCompositePart.Scalar<TValue>(name);
}

/// <summary>
/// Builds named composite-part predicates for routed composite-key conditions.<br/>
/// These helpers keep developer intent part-oriented: callers describe `lastName == Smith` and `firstName starts with J` instead of flattening those values into one string or byte key.<br/>
/// </summary>
public static class LibraDexCompositePart
{
    /// <summary>
    /// Starts a predicate over the full composite key using index-order parts and no delimiter.<br/>
    /// This is an explicit full-key request: LibraDex renders the complete routed path at the terminal node and applies the requested predicate as a residual operation.<br/>
    /// </summary>
    /// <returns>A full-key predicate builder with no delimiter.</returns>
    public static LibraDexCompositeFullKeyCondition FullKey()
        => new LibraDexCompositeFullKeyCondition(delimiter: string.Empty);

    /// <summary>
    /// Starts a predicate over the full composite key using index-order parts and a caller-supplied delimiter.<br/>
    /// The delimiter participates in the rendered text so developers can shape their contains or pattern criteria with explicit key-part boundaries.<br/>
    /// </summary>
    /// <param name="delimiter">The delimiter inserted between rendered composite key parts.</param>
    /// <returns>A full-key predicate builder using the supplied delimiter.</returns>
    public static LibraDexCompositeFullKeyCondition FullKey(string delimiter)
    {
        ArgumentNullException.ThrowIfNull(delimiter);
        return new LibraDexCompositeFullKeyCondition(delimiter);
    }

    /// <summary>
    /// Starts a string predicate for one composite tier.<br/>
    /// </summary>
    /// <param name="name">The composite part name.</param>
    /// <returns>A string predicate builder for the named part.</returns>
    public static LibraDexCompositeStringPartCondition String(string name)
        => new LibraDexCompositeStringPartCondition(name);

    /// <summary>
    /// Starts a GUID predicate for one composite tier.<br/>
    /// GUID criteria stay in the stored GUID byte domain; callers that want text-oriented GUID behavior should declare and populate a string composite part instead.<br/>
    /// </summary>
    /// <param name="name">The composite part name.</param>
    /// <returns>A GUID predicate builder for the named part.</returns>
    public static LibraDexCompositeGuidPartCondition Guid(string name)
        => new LibraDexCompositeGuidPartCondition(name);

    /// <summary>
    /// Starts a raw binary predicate for one composite tier.<br/>
    /// Binary criteria compare byte sequences directly and do not render the component through text formatting.<br/>
    /// </summary>
    /// <param name="name">The composite part name.</param>
    /// <returns>A binary predicate builder for the named part.</returns>
    public static LibraDexCompositeBinaryPartCondition Binary(string name)
        => new LibraDexCompositeBinaryPartCondition(name);

    /// <summary>
    /// Starts a structured date/time predicate for one composite tier.<br/>
    /// Date criteria use the same Abraxas-compatible structured date binary layout as top-level date indexes, not text formatting or query-time parsing.<br/>
    /// </summary>
    /// <param name="name">The composite part name.</param>
    /// <returns>A structured date predicate builder for the named part.</returns>
    public static LibraDexCompositeDatePartCondition Date(string name)
        => new LibraDexCompositeDatePartCondition(name);

    /// <summary>
    /// Starts a typed scalar predicate for one composite tier.<br/>
    /// </summary>
    /// <typeparam name="TValue">The scalar value type.</typeparam>
    /// <param name="name">The composite part name.</param>
    /// <returns>A scalar predicate builder for the named part.</returns>
    public static LibraDexCompositeScalarPartCondition<TValue> Scalar<TValue>(string name)
        => new LibraDexCompositeScalarPartCondition<TValue>(name);
}

/// <summary>
/// Describes one named predicate inside a routed composite-key condition.<br/>
/// The descriptor stores the part name, operator, value kind, and operands separately from physical execution so the composite index can bind the predicate to a specific tier descriptor at materialization time.<br/>
/// </summary>
public sealed class LibraDexCompositePartCriterion
{
    internal const string FullKeyPartName = "__libradex_full_key";

    internal LibraDexCompositePartCriterion(
        string partName,
        LibraDexConditionValueKind valueKind,
        LibraDexConditionOperatorKind operatorKind,
        IReadOnlyList<object?> values,
        bool ignoreCase,
        string? culture,
        string? fullKeyDelimiter = null,
        IReadOnlyList<string>? fullKeyPartNames = null,
        IReadOnlyList<string>? fullKeyExcludedPartNames = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partName);
        PartName = partName;
        ValueKind = valueKind;
        Operator = operatorKind;
        Values = values;
        IgnoreCase = ignoreCase;
        Culture = culture;
        FullKeyDelimiter = fullKeyDelimiter;
        FullKeyPartNames = fullKeyPartNames;
        FullKeyExcludedPartNames = fullKeyExcludedPartNames;
    }

    /// <summary>
    /// Gets the composite key part name targeted by this predicate.<br/>
    /// </summary>
    public string PartName { get; }

    /// <summary>
    /// Gets the captured value kind for this part predicate.<br/>
    /// </summary>
    public LibraDexConditionValueKind ValueKind { get; }

    /// <summary>
    /// Gets the captured operator for this part predicate.<br/>
    /// </summary>
    public LibraDexConditionOperatorKind Operator { get; }

    /// <summary>
    /// Gets the materialized predicate operands.<br/>
    /// </summary>
    public IReadOnlyList<object?> Values { get; }

    /// <summary>
    /// Gets whether text comparison for this part should ignore case when no maintained folded tier is selected.<br/>
    /// </summary>
    public bool IgnoreCase { get; }

    /// <summary>
    /// Gets the optional culture name requested for text comparison.<br/>
    /// </summary>
    public string? Culture { get; }

    /// <summary>
    /// Gets the delimiter used by an explicit full-key composite predicate.<br/>
    /// A null value means the criterion targets a normal named part.<br/>
    /// </summary>
    public string? FullKeyDelimiter { get; }

    /// <summary>
    /// Gets the optional included key-part names used by an explicit full-key composite predicate.<br/>
    /// A null or empty list means the full key renders all composite parts in index order.<br/>
    /// </summary>
    public IReadOnlyList<string>? FullKeyPartNames { get; }

    /// <summary>
    /// Gets the optional excluded key-part names used by an explicit full-key composite predicate.<br/>
    /// Exclusions are applied after inclusion selection and still preserve composite index order.<br/>
    /// </summary>
    public IReadOnlyList<string>? FullKeyExcludedPartNames { get; }
}

/// <summary>
/// Selects a comparison domain for the full composite key.<br/>
/// The full key uses the composite index's part order, all parts, and the delimiter selected by the developer; this is residual matching, not a hidden flattened physical key.<br/>
/// </summary>
public sealed class LibraDexCompositeFullKeyCondition
{
    private readonly string delimiter;
    private readonly IReadOnlyList<string>? partNames;
    private readonly IReadOnlyList<string>? excludedPartNames;

    internal LibraDexCompositeFullKeyCondition(string delimiter)
        : this(delimiter, partNames: null, excludedPartNames: null)
    {
    }

    private LibraDexCompositeFullKeyCondition(
        string delimiter,
        IReadOnlyList<string>? partNames,
        IReadOnlyList<string>? excludedPartNames)
    {
        this.delimiter = delimiter;
        this.partNames = partNames;
        this.excludedPartNames = excludedPartNames;
    }

    /// <summary>
    /// Narrows the full composite key to selected key-part names while preserving composite index order.<br/>
    /// The names describe inclusion only; rendering still follows the index descriptor order so generated callers do not have to sort their input names.<br/>
    /// </summary>
    /// <param name="partNames">The composite key-part names to include in the full-key value.</param>
    /// <returns>A full-key predicate builder that renders only the selected key parts.</returns>
    public LibraDexCompositeFullKeyCondition Parts(params string[] partNames)
    {
        ArgumentNullException.ThrowIfNull(partNames);
        if (partNames.Length == 0)
        {
            throw new ArgumentException("Full composite key part selection requires at least one key-part name.", nameof(partNames));
        }

        string[] copy = new string[partNames.Length];
        for (int i = 0; i < partNames.Length; i++)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(partNames[i]);
            copy[i] = partNames[i];
        }

        return new LibraDexCompositeFullKeyCondition(delimiter, Array.AsReadOnly(copy), excludedPartNames);
    }

    /// <summary>
    /// Excludes selected key-part names from the full composite key while preserving composite index order for the remaining parts.<br/>
    /// This is useful when a composite index has a routing prefix such as tenant or partition that should not participate in user-facing full-key text search.<br/>
    /// </summary>
    /// <param name="partNames">The composite key-part names to exclude from the full-key value.</param>
    /// <returns>A full-key predicate builder that omits the selected key parts.</returns>
    public LibraDexCompositeFullKeyCondition Excluding(params string[] partNames)
    {
        ArgumentNullException.ThrowIfNull(partNames);
        if (partNames.Length == 0)
        {
            throw new ArgumentException("Full composite key exclusion requires at least one key-part name.", nameof(partNames));
        }

        string[] copy = new string[partNames.Length];
        for (int i = 0; i < partNames.Length; i++)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(partNames[i]);
            copy[i] = partNames[i];
        }

        return new LibraDexCompositeFullKeyCondition(delimiter, this.partNames, Array.AsReadOnly(copy));
    }

    /// <summary>
    /// Interprets the selected full composite key representation as string data.<br/>
    /// No delimiter is inserted when the full key was created with <see cref="LibraDexCompositePart.FullKey()"/>; delimiter overloads make boundary text explicit and developer-owned.<br/>
    /// </summary>
    public LibraDexCompositeFullKeyStringCondition AsString => new(delimiter, partNames, excludedPartNames);
}

/// <summary>
/// Captures string predicates over the full composite key.<br/>
/// The full-key representation uses the composite index's part order and selected delimiter, then compares the supplied criteria as residual string intent over that representation.<br/>
/// </summary>
public sealed class LibraDexCompositeFullKeyStringCondition
{
    private readonly string delimiter;
    private readonly IReadOnlyList<string>? partNames;
    private readonly IReadOnlyList<string>? excludedPartNames;

    internal LibraDexCompositeFullKeyStringCondition(
        string delimiter,
        IReadOnlyList<string>? partNames,
        IReadOnlyList<string>? excludedPartNames)
    {
        this.delimiter = delimiter;
        this.partNames = partNames;
        this.excludedPartNames = excludedPartNames;
    }

    /// <summary>
    /// Captures equality against the full composite key string representation.<br/>
    /// </summary>
    /// <param name="value">The rendered key value to match.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A full-key composite predicate.</returns>
    public LibraDexCompositePartCriterion EqualTo(string value, bool ignoreCase = false, string? culture = null)
        => Create(LibraDexConditionOperatorKind.EqualTo, ignoreCase, culture, value);

    /// <summary>
    /// Captures equality against an encoded typed value inside the full composite key.<br/>
    /// The operand is encoded once into the same byte-domain used by composite components, making this suitable for GUID, scalar, date/time, and raw byte values without per-row text conversion.<br/>
    /// </summary>
    /// <param name="value">The typed operand to encode and compare.</param>
    /// <returns>A full-key composite predicate.</returns>
    public LibraDexCompositePartCriterion EqualTo(object value)
        => CreateTyped(LibraDexConditionOperatorKind.EqualTo, value);

    /// <summary>
    /// Captures a prefix predicate against the full composite key string representation.<br/>
    /// </summary>
    /// <param name="value">The prefix value.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A full-key composite predicate.</returns>
    public LibraDexCompositePartCriterion StartsWith(string value, bool ignoreCase = false, string? culture = null)
        => Create(LibraDexConditionOperatorKind.StartsWith, ignoreCase, culture, value);

    /// <summary>
    /// Captures a prefix predicate against an encoded typed value inside the full composite key.<br/>
    /// The operand is encoded once into the same byte-domain used by composite components, so callers can express typed binary-prefix intent without formatting the value as text.<br/>
    /// </summary>
    /// <param name="value">The typed operand to encode and compare as a prefix.</param>
    /// <returns>A full-key composite predicate.</returns>
    public LibraDexCompositePartCriterion StartsWith(object value)
        => CreateTyped(LibraDexConditionOperatorKind.StartsWith, value);

    /// <summary>
    /// Captures a suffix predicate against the full composite key string representation.<br/>
    /// </summary>
    /// <param name="value">The suffix value.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A full-key composite predicate.</returns>
    public LibraDexCompositePartCriterion EndsWith(string value, bool ignoreCase = false, string? culture = null)
        => Create(LibraDexConditionOperatorKind.EndsWith, ignoreCase, culture, value);

    /// <summary>
    /// Captures a suffix predicate against an encoded typed value inside the full composite key.<br/>
    /// The operand is encoded once into the same byte-domain used by composite components, preserving typed search intent without text rendering.<br/>
    /// </summary>
    /// <param name="value">The typed operand to encode and compare as a suffix.</param>
    /// <returns>A full-key composite predicate.</returns>
    public LibraDexCompositePartCriterion EndsWith(object value)
        => CreateTyped(LibraDexConditionOperatorKind.EndsWith, value);

    /// <summary>
    /// Captures a containment predicate against the full composite key string representation.<br/>
    /// </summary>
    /// <param name="value">The contained value.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A full-key composite predicate.</returns>
    public LibraDexCompositePartCriterion Contains(string value, bool ignoreCase = false, string? culture = null)
        => Create(LibraDexConditionOperatorKind.Contains, ignoreCase, culture, value);

    /// <summary>
    /// Captures a containment predicate against an encoded typed value inside the full composite key.<br/>
    /// The operand is encoded once into the same byte-domain used by composite components, which is the preferred path for typed GUID, scalar, date/time, or raw byte containment checks.<br/>
    /// </summary>
    /// <param name="value">The typed operand to encode and search for.</param>
    /// <returns>A full-key composite predicate.</returns>
    public LibraDexCompositePartCriterion Contains(object value)
        => CreateTyped(LibraDexConditionOperatorKind.Contains, value);

    /// <summary>
    /// Captures a wildcard pattern predicate against the full composite key string representation.<br/>
    /// </summary>
    /// <param name="pattern">The wildcard pattern where `*` spans zero or more characters and `?` matches one character.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A full-key composite predicate.</returns>
    public LibraDexCompositePartCriterion MatchesPattern(string pattern, bool ignoreCase = false, string? culture = null)
        => Create(LibraDexConditionOperatorKind.MatchesPattern, ignoreCase, culture, pattern);

    /// <summary>
    /// Captures a pattern text predicate for this composite string part using the short public spelling.<br/>
    /// This is the preferred alias for <see cref="MatchesPattern(string, bool, string?)"/> and preserves the same case and culture metadata.<br/>
    /// </summary>
    public LibraDexCompositePartCriterion Matches(string pattern, bool ignoreCase = false, string? culture = null)
        => MatchesPattern(pattern, ignoreCase, culture);

    private LibraDexCompositePartCriterion Create(
        LibraDexConditionOperatorKind operatorKind,
        bool ignoreCase,
        string? culture,
        params object?[] values)
    {
        return new LibraDexCompositePartCriterion(
            LibraDexCompositePartCriterion.FullKeyPartName,
            LibraDexConditionValueKind.String,
            operatorKind,
            Array.AsReadOnly(values),
            ignoreCase,
            culture,
            fullKeyDelimiter: delimiter,
            fullKeyPartNames: partNames,
            fullKeyExcludedPartNames: excludedPartNames);
    }

    /// <summary>
    /// Creates a full-key predicate over a non-text typed operand.<br/>
    /// The runtime executor validates and encodes the operand into the composite byte domain once before comparing it against encoded full-key paths.<br/>
    /// </summary>
    /// <param name="operatorKind">The full-key operator to apply.</param>
    /// <param name="value">The typed operand to encode.</param>
    /// <returns>A full-key composite predicate.</returns>
    private LibraDexCompositePartCriterion CreateTyped(LibraDexConditionOperatorKind operatorKind, object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new LibraDexCompositePartCriterion(
            LibraDexCompositePartCriterion.FullKeyPartName,
            ClassifyFullKeyValueKind(value),
            operatorKind,
            Array.AsReadOnly(new object?[] { value }),
            ignoreCase: false,
            culture: null,
            fullKeyDelimiter: delimiter,
            fullKeyPartNames: partNames,
            fullKeyExcludedPartNames: excludedPartNames);
    }

    private static LibraDexConditionValueKind ClassifyFullKeyValueKind(object value)
    {
        Type type = value.GetType();
        if (type == typeof(byte[]))
        {
            return LibraDexConditionValueKind.Binary;
        }

        if (type == typeof(Guid))
        {
            return LibraDexConditionValueKind.Guid;
        }

        if (type == typeof(DateTime))
        {
            return LibraDexConditionValueKind.DateTime;
        }

        if (type == typeof(DateOnly))
        {
            return LibraDexConditionValueKind.DateOnly;
        }

        if (type == typeof(TimeOnly))
        {
            return LibraDexConditionValueKind.TimeOnly;
        }

        return type == typeof(string)
            ? LibraDexConditionValueKind.String
            : LibraDexConditionValueKind.Numeric;
    }
}

/// <summary>
/// Captures string predicates for one named routed composite tier.<br/>
/// </summary>
public sealed class LibraDexCompositeStringPartCondition
{
    private readonly string name;

    internal LibraDexCompositeStringPartCondition(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        this.name = name;
    }

    /// <summary>
    /// Captures equality against a string component value.<br/>
    /// </summary>
    /// <param name="value">The string value to match.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion EqualTo(string value, bool ignoreCase = false, string? culture = null)
        => Create(LibraDexConditionOperatorKind.EqualTo, ignoreCase, culture, value);

    /// <summary>
    /// Captures equality against an explicit null or empty string component state.<br/>
    /// The criterion stores the <see cref="NullKey"/> state directly so routed composite execution can distinguish a stored null route from a real empty string route.<br/>
    /// </summary>
    /// <param name="keyState">The string key-state predicate to match.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion EqualTo(NullKey keyState)
        => Create(LibraDexConditionOperatorKind.EqualTo, ignoreCase: false, culture: null, keyState);

    /// <summary>
    /// Captures inequality against a string component value.<br/>
    /// This mirrors the normal string condition operator and records a part-scoped criterion rather than a separate index leaf.<br/>
    /// </summary>
    /// <param name="value">The string value to exclude.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion NotEqualTo(string value, bool ignoreCase = false, string? culture = null)
        => Create(LibraDexConditionOperatorKind.NotEqualTo, ignoreCase, culture, value);

    /// <summary>
    /// Captures inequality against an explicit null or empty string component state.<br/>
    /// This is the part-scoped presence/absence counterpart to ordinary string-index null-state predicates.<br/>
    /// </summary>
    /// <param name="keyState">The string key-state predicate to exclude.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion NotEqualTo(NullKey keyState)
        => Create(LibraDexConditionOperatorKind.NotEqualTo, ignoreCase: false, culture: null, keyState);

    /// <summary>
    /// Captures a string prefix predicate for one component tier.<br/>
    /// </summary>
    /// <param name="value">The prefix value.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion StartsWith(string value, bool ignoreCase = false, string? culture = null)
        => Create(LibraDexConditionOperatorKind.StartsWith, ignoreCase, culture, value);

    /// <summary>
    /// Captures a string suffix predicate for one component tier.<br/>
    /// This is a scan-backed tier predicate unless the composite part is later connected to a maintained reversed projection.<br/>
    /// </summary>
    /// <param name="value">The suffix value.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion EndsWith(string value, bool ignoreCase = false, string? culture = null)
        => Create(LibraDexConditionOperatorKind.EndsWith, ignoreCase, culture, value);

    /// <summary>
    /// Captures a string containment predicate for one component tier.<br/>
    /// The predicate is intentionally part-scoped; whole-composite containment requires an explicit full-key policy so LibraDex does not invent hidden flattening semantics.<br/>
    /// </summary>
    /// <param name="value">The contained value.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion Contains(string value, bool ignoreCase = false, string? culture = null)
        => Create(LibraDexConditionOperatorKind.Contains, ignoreCase, culture, value);

    /// <summary>
    /// Captures a wildcard pattern predicate for one string component tier.<br/>
    /// The pattern uses the same wildcard rules as other LibraDex string patterns: `*` spans zero or more characters and `?` matches one character.<br/>
    /// </summary>
    /// <param name="pattern">The wildcard pattern to apply to the component value.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion MatchesPattern(string pattern, bool ignoreCase = false, string? culture = null)
        => Create(LibraDexConditionOperatorKind.MatchesPattern, ignoreCase, culture, pattern);

    /// <summary>
    /// Captures a pattern text predicate for this composite full-key string view using the short public spelling.<br/>
    /// This is the preferred alias for <see cref="MatchesPattern(string, bool, string?)"/> and preserves the same case and culture metadata.<br/>
    /// </summary>
    public LibraDexCompositePartCriterion Matches(string pattern, bool ignoreCase = false, string? culture = null)
        => MatchesPattern(pattern, ignoreCase, culture);

    /// <summary>
    /// Captures a string component predicate greater than the supplied value.<br/>
    /// </summary>
    /// <param name="value">The exclusive lower value.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion GreaterThan(string value, bool ignoreCase = false, string? culture = null)
        => Create(LibraDexConditionOperatorKind.GreaterThan, ignoreCase, culture, value);

    /// <summary>
    /// Captures a string component predicate greater than or equal to the supplied value.<br/>
    /// </summary>
    /// <param name="value">The inclusive lower value.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion GreaterOrEqual(string value, bool ignoreCase = false, string? culture = null)
        => Create(LibraDexConditionOperatorKind.GreaterOrEqual, ignoreCase, culture, value);

    /// <summary>
    /// Captures a string component predicate less than the supplied value.<br/>
    /// </summary>
    /// <param name="value">The exclusive upper value.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion LessThan(string value, bool ignoreCase = false, string? culture = null)
        => Create(LibraDexConditionOperatorKind.LessThan, ignoreCase, culture, value);

    /// <summary>
    /// Captures a string component predicate less than or equal to the supplied value.<br/>
    /// </summary>
    /// <param name="value">The inclusive upper value.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion LessOrEqual(string value, bool ignoreCase = false, string? culture = null)
        => Create(LibraDexConditionOperatorKind.LessOrEqual, ignoreCase, culture, value);

    /// <summary>
    /// Captures an inclusive string range predicate for one component tier.<br/>
    /// </summary>
    /// <param name="lower">The inclusive lower value.</param>
    /// <param name="upper">The inclusive upper value.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion Between(string lower, string upper, bool ignoreCase = false, string? culture = null)
        => Create(LibraDexConditionOperatorKind.Between, ignoreCase, culture, lower, upper);

    /// <summary>
    /// Captures a string component predicate outside the supplied inclusive range.<br/>
    /// The criterion stays part-scoped so composite traversal can still prune or residual-test the selected component tier.<br/>
    /// </summary>
    /// <param name="lower">The inclusive lower value of the excluded window.</param>
    /// <param name="upper">The inclusive upper value of the excluded window.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion NotBetween(string lower, string upper, bool ignoreCase = false, string? culture = null)
        => Create(LibraDexConditionOperatorKind.NotBetween, ignoreCase, culture, lower, upper);

    /// <summary>
    /// Captures string component membership in a supplied value set.<br/>
    /// The enumerable is captured as one operand so the composite condition remains one `CompositeMatch` leaf rather than caller-expanded `OR` syntax.<br/>
    /// </summary>
    /// <param name="values">The string values to match.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion InSet(IEnumerable<string> values, bool ignoreCase = false, string? culture = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        return Create(LibraDexConditionOperatorKind.InSet, ignoreCase, culture, CaptureStringMembershipInput(values));
    }

    /// <summary>
    /// Captures string component membership using LibraDex's short set spelling.<br/>
    /// This is an alias for <see cref="InSet(IEnumerable{string}, bool, string?)"/> and preserves the same part-scoped descriptor.<br/>
    /// </summary>
    /// <param name="values">The string values to match.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion In(IEnumerable<string> values, bool ignoreCase = false, string? culture = null)
        => InSet(values, ignoreCase, culture);

    /// <summary>
    /// Captures Abraxas-style string component membership.<br/>
    /// This is an alias for <see cref="InSet(IEnumerable{string}, bool, string?)"/> for copied condition-builder expressions.<br/>
    /// </summary>
    /// <param name="values">The string values to match.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion IsIn(IEnumerable<string> values, bool ignoreCase = false, string? culture = null)
        => InSet(values, ignoreCase, culture);

    /// <summary>
    /// Captures string component membership exclusion from a supplied value set.<br/>
    /// The enumerable is captured as one operand and evaluated against the selected composite component value.<br/>
    /// </summary>
    /// <param name="values">The string values to exclude.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion NotInSet(IEnumerable<string> values, bool ignoreCase = false, string? culture = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        return Create(LibraDexConditionOperatorKind.NotInSet, ignoreCase, culture, CaptureStringMembershipInput(values));
    }

    /// <summary>
    /// Captures string component membership exclusion using LibraDex's short set spelling.<br/>
    /// This is an alias for <see cref="NotInSet(IEnumerable{string}, bool, string?)"/> and preserves the same part-scoped descriptor.<br/>
    /// </summary>
    /// <param name="values">The string values to exclude.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion NotIn(IEnumerable<string> values, bool ignoreCase = false, string? culture = null)
        => NotInSet(values, ignoreCase, culture);

    /// <summary>
    /// Captures Abraxas-style string component membership exclusion.<br/>
    /// This is an alias for <see cref="NotInSet(IEnumerable{string}, bool, string?)"/> for copied condition-builder expressions.<br/>
    /// </summary>
    /// <param name="values">The string values to exclude.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion IsNotIn(IEnumerable<string> values, bool ignoreCase = false, string? culture = null)
        => NotInSet(values, ignoreCase, culture);

    private LibraDexCompositePartCriterion Create(
        LibraDexConditionOperatorKind operatorKind,
        bool ignoreCase,
        string? culture,
        params object?[] values)
    {
        return new LibraDexCompositePartCriterion(name, LibraDexConditionValueKind.String, operatorKind, Array.AsReadOnly(values), ignoreCase, culture);
    }

    private static object CaptureStringMembershipInput(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values is HashSet<string> or ISet<string> or IReadOnlyCollection<string>
            ? values
            : values.ToArray();
    }
}

/// <summary>
/// Captures GUID predicates for one named routed composite tier.<br/>
/// GUID values remain binary GUID values; text matching belongs on a caller-declared string part.<br/>
/// </summary>
public sealed class LibraDexCompositeGuidPartCondition
{
    private readonly string name;

    internal LibraDexCompositeGuidPartCondition(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        this.name = name;
    }

    /// <summary>
    /// Captures equality against a GUID component value.<br/>
    /// </summary>
    /// <param name="value">The GUID value to match.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion EqualTo(Guid value)
        => Create(LibraDexConditionOperatorKind.EqualTo, value);

    /// <summary>
    /// Captures equality against a scalar null-state for a GUID component.<br/>
    /// The GUID domain has no empty key state, so optional GUID parts use <see cref="ScalarNull"/> rather than a sentinel GUID value.<br/>
    /// </summary>
    /// <param name="state">The scalar null-state predicate to match.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion EqualTo(ScalarNull state)
        => Create(LibraDexConditionOperatorKind.EqualTo, state);

    /// <summary>
    /// Captures inequality against a GUID component value.<br/>
    /// </summary>
    /// <param name="value">The GUID value to exclude.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion NotEqualTo(Guid value)
        => Create(LibraDexConditionOperatorKind.NotEqualTo, value);

    /// <summary>
    /// Captures inequality against a scalar null-state for a GUID component.<br/>
    /// This provides a concise non-null GUID part predicate without conflating null with <see cref="Guid.Empty"/>.<br/>
    /// </summary>
    /// <param name="state">The scalar null-state predicate to exclude.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion NotEqualTo(ScalarNull state)
        => Create(LibraDexConditionOperatorKind.NotEqualTo, state);

    /// <summary>
    /// Captures a prefix predicate over the stored GUID byte-domain representation.<br/>
    /// The GUID operand is encoded once and the first <paramref name="byteCount"/> bytes are compared against each routed component value.<br/>
    /// </summary>
    /// <param name="value">The GUID value that supplies the prefix bytes.</param>
    /// <param name="byteCount">The number of leading GUID bytes to compare, from 1 through 16.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion StartsWith(Guid value, int byteCount)
    {
        if (byteCount is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(byteCount), byteCount, "GUID prefix byte count must be 1 through 16.");
        }

        return Create(LibraDexConditionOperatorKind.StartsWith, value, byteCount);
    }

    /// <summary>
    /// Captures GUID component membership in a supplied value set.<br/>
    /// The enumerable is captured as one operand so the composite condition remains one `CompositeMatch` leaf rather than caller-expanded `OR` syntax.<br/>
    /// </summary>
    /// <param name="values">The GUID values to match.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion InSet(IEnumerable<Guid> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return Create(LibraDexConditionOperatorKind.InSet, CaptureGuidMembershipInput(values));
    }

    /// <summary>
    /// Captures GUID component membership using LibraDex's short set spelling.<br/>
    /// This is an alias for <see cref="InSet(IEnumerable{Guid})"/> and preserves the same part-scoped descriptor.<br/>
    /// </summary>
    /// <param name="values">The GUID values to match.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion In(IEnumerable<Guid> values)
        => InSet(values);

    /// <summary>
    /// Captures Abraxas-style GUID component membership.<br/>
    /// This is an alias for <see cref="InSet(IEnumerable{Guid})"/> for copied condition-builder expressions.<br/>
    /// </summary>
    /// <param name="values">The GUID values to match.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion IsIn(IEnumerable<Guid> values)
        => InSet(values);

    /// <summary>
    /// Captures GUID component membership exclusion from a supplied value set.<br/>
    /// The enumerable is captured as one operand and evaluated against the selected composite component value.<br/>
    /// </summary>
    /// <param name="values">The GUID values to exclude.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion NotInSet(IEnumerable<Guid> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return Create(LibraDexConditionOperatorKind.NotInSet, CaptureGuidMembershipInput(values));
    }

    /// <summary>
    /// Captures GUID component membership exclusion using LibraDex's short set spelling.<br/>
    /// This is an alias for <see cref="NotInSet(IEnumerable{Guid})"/> and preserves the same part-scoped descriptor.<br/>
    /// </summary>
    /// <param name="values">The GUID values to exclude.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion NotIn(IEnumerable<Guid> values)
        => NotInSet(values);

    /// <summary>
    /// Captures Abraxas-style GUID component membership exclusion.<br/>
    /// This is an alias for <see cref="NotInSet(IEnumerable{Guid})"/> for copied condition-builder expressions.<br/>
    /// </summary>
    /// <param name="values">The GUID values to exclude.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion IsNotIn(IEnumerable<Guid> values)
        => NotInSet(values);

    private LibraDexCompositePartCriterion Create(LibraDexConditionOperatorKind operatorKind, params object?[] values)
        => new LibraDexCompositePartCriterion(name, LibraDexConditionValueKind.Guid, operatorKind, Array.AsReadOnly(values), ignoreCase: false, culture: null);

    private static object CaptureGuidMembershipInput(IEnumerable<Guid> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values is ISet<Guid> or IReadOnlyCollection<Guid>
            ? values
            : values.ToArray();
    }
}

/// <summary>
/// Captures binary predicates for one named routed composite tier.<br/>
/// Binary values remain byte arrays; text or hex conversion belongs at the caller boundary, not inside routed composite execution.<br/>
/// </summary>
public sealed class LibraDexCompositeBinaryPartCondition
{
    private readonly string name;

    internal LibraDexCompositeBinaryPartCondition(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        this.name = name;
    }

    /// <summary>
    /// Captures equality against a binary component value.<br/>
    /// </summary>
    /// <param name="value">The byte sequence to match.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion EqualTo(byte[] value)
        => Create(LibraDexConditionOperatorKind.EqualTo, value);

    /// <summary>
    /// Captures equality against an explicit null or empty binary component state.<br/>
    /// The criterion stores the <see cref="NullKey"/> state directly so null and empty byte-array routes remain distinct.<br/>
    /// </summary>
    /// <param name="keyState">The binary key-state predicate to match.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion EqualTo(NullKey keyState)
        => Create(LibraDexConditionOperatorKind.EqualTo, keyState);

    /// <summary>
    /// Captures inequality against a binary component value.<br/>
    /// </summary>
    /// <param name="value">The byte sequence to exclude.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion NotEqualTo(byte[] value)
        => Create(LibraDexConditionOperatorKind.NotEqualTo, value);

    /// <summary>
    /// Captures inequality against an explicit null or empty binary component state.<br/>
    /// This mirrors ordinary binary-index null-state syntax while keeping evaluation part-scoped inside the composite match.<br/>
    /// </summary>
    /// <param name="keyState">The binary key-state predicate to exclude.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion NotEqualTo(NullKey keyState)
        => Create(LibraDexConditionOperatorKind.NotEqualTo, keyState);

    /// <summary>
    /// Captures a byte-prefix predicate against a binary component value.<br/>
    /// </summary>
    /// <param name="value">The byte prefix to match.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion StartsWith(byte[] value)
        => Create(LibraDexConditionOperatorKind.StartsWith, value);

    /// <summary>
    /// Captures a byte-suffix predicate against a binary component value.<br/>
    /// </summary>
    /// <param name="value">The byte suffix to match.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion EndsWith(byte[] value)
        => Create(LibraDexConditionOperatorKind.EndsWith, value);

    /// <summary>
    /// Captures a byte containment predicate against a binary component value.<br/>
    /// </summary>
    /// <param name="value">The byte sequence that must appear inside the component value.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion Contains(byte[] value)
        => Create(LibraDexConditionOperatorKind.Contains, value);

    private LibraDexCompositePartCriterion Create(LibraDexConditionOperatorKind operatorKind, byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new LibraDexCompositePartCriterion(name, LibraDexConditionValueKind.Binary, operatorKind, Array.AsReadOnly(new object?[] { value }), ignoreCase: false, culture: null);
    }

    private LibraDexCompositePartCriterion Create(LibraDexConditionOperatorKind operatorKind, NullKey keyState)
        => new LibraDexCompositePartCriterion(name, LibraDexConditionValueKind.Binary, operatorKind, Array.AsReadOnly(new object?[] { keyState }), ignoreCase: false, culture: null);
}

/// <summary>
/// Captures structured date/time predicates for one named routed composite tier.<br/>
/// The predicates preserve date intent while the composite executor applies the same packed structured date layout used by top-level date indexes.<br/>
/// </summary>
public sealed class LibraDexCompositeDatePartCondition
{
    private readonly string name;

    internal LibraDexCompositeDatePartCondition(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        this.name = name;
    }

    /// <summary>
    /// Captures a structured date/time year equality predicate.<br/>
    /// </summary>
    /// <param name="year">The year component to match.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion YearEqualTo(int year)
        => Create(LibraDexConditionOperatorKind.YearEqualTo, year);

    /// <summary>
    /// Captures a structured date/time year equality predicate using Abraxas-style naming.<br/>
    /// </summary>
    /// <param name="year">The year component to match.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion YearEqual(int year)
        => YearEqualTo(year);

    /// <summary>
    /// Captures a structured date/time inclusive year range predicate.<br/>
    /// </summary>
    /// <param name="startYear">The inclusive starting year.</param>
    /// <param name="endYear">The inclusive ending year.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion YearRange(int startYear, int endYear)
        => Create(LibraDexConditionOperatorKind.YearRange, startYear, endYear);

    /// <summary>
    /// Captures a structured date/time year and month predicate.<br/>
    /// </summary>
    /// <param name="year">The year component.</param>
    /// <param name="month">The month component, from 1 through 12.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion YearMonth(int year, int month)
        => Create(LibraDexConditionOperatorKind.YearMonth, year, month);

    /// <summary>
    /// Captures a structured date/time year, month, and day predicate.<br/>
    /// </summary>
    /// <param name="year">The year component.</param>
    /// <param name="month">The month component, from 1 through 12.</param>
    /// <param name="day">The day component, from 1 through 31 subject to the month and year.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion YearMonthDay(int year, int month, int day)
        => Create(LibraDexConditionOperatorKind.YearMonthDay, year, month, day);

    /// <summary>
    /// Captures a structured date/time month equality predicate across any year.<br/>
    /// </summary>
    /// <param name="month">The month component, from 1 through 12.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion MonthEqualTo(int month)
        => Create(LibraDexConditionOperatorKind.MonthEqualTo, month);

    /// <summary>
    /// Captures a structured date/time day equality predicate across any month and year.<br/>
    /// </summary>
    /// <param name="day">The day component, from 1 through 31.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion DayEqualTo(int day)
        => Create(LibraDexConditionOperatorKind.DayEqualTo, day);

    /// <summary>
    /// Captures equality against a full DateTime component value.<br/>
    /// The executor compares the packed structured-date representation, preserving the composite part's DateTime encoding contract.<br/>
    /// </summary>
    /// <param name="value">The DateTime value to match.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion EqualTo(DateTime value)
        => Create(LibraDexConditionOperatorKind.EqualTo, value);

    /// <summary>
    /// Captures equality against a scalar null-state for a DateTime component.<br/>
    /// The date domain uses scalar null-state routes so callers do not need DateTime sentinel values for optional date parts.<br/>
    /// </summary>
    /// <param name="state">The scalar null-state predicate to match.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion EqualTo(ScalarNull state)
        => Create(LibraDexConditionOperatorKind.EqualTo, state);

    /// <summary>
    /// Captures inequality against a full DateTime component value.<br/>
    /// </summary>
    /// <param name="value">The DateTime value to exclude.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion NotEqualTo(DateTime value)
        => Create(LibraDexConditionOperatorKind.NotEqualTo, value);

    /// <summary>
    /// Captures inequality against a scalar null-state for a DateTime component.<br/>
    /// This is the optional-date part counterpart to ordinary scalar null-state condition syntax.<br/>
    /// </summary>
    /// <param name="state">The scalar null-state predicate to exclude.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion NotEqualTo(ScalarNull state)
        => Create(LibraDexConditionOperatorKind.NotEqualTo, state);

    /// <summary>
    /// Captures a full DateTime component predicate greater than the supplied value.<br/>
    /// </summary>
    /// <param name="value">The exclusive lower DateTime value.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion GreaterThan(DateTime value)
        => Create(LibraDexConditionOperatorKind.GreaterThan, value);

    /// <summary>
    /// Captures a full DateTime component predicate greater than or equal to the supplied value.<br/>
    /// </summary>
    /// <param name="value">The inclusive lower DateTime value.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion GreaterOrEqual(DateTime value)
        => Create(LibraDexConditionOperatorKind.GreaterOrEqual, value);

    /// <summary>
    /// Captures a full DateTime component predicate less than the supplied value.<br/>
    /// </summary>
    /// <param name="value">The exclusive upper DateTime value.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion LessThan(DateTime value)
        => Create(LibraDexConditionOperatorKind.LessThan, value);

    /// <summary>
    /// Captures a full DateTime component predicate less than or equal to the supplied value.<br/>
    /// </summary>
    /// <param name="value">The inclusive upper DateTime value.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion LessOrEqual(DateTime value)
        => Create(LibraDexConditionOperatorKind.LessOrEqual, value);

    /// <summary>
    /// Captures an inclusive full DateTime range predicate for one component tier.<br/>
    /// </summary>
    /// <param name="lower">The inclusive lower DateTime value.</param>
    /// <param name="upper">The inclusive upper DateTime value.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion Between(DateTime lower, DateTime upper)
        => Create(LibraDexConditionOperatorKind.Between, lower, upper);

    /// <summary>
    /// Captures a full DateTime component predicate outside the supplied inclusive range.<br/>
    /// </summary>
    /// <param name="lower">The inclusive lower value of the excluded window.</param>
    /// <param name="upper">The inclusive upper value of the excluded window.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion NotBetween(DateTime lower, DateTime upper)
        => Create(LibraDexConditionOperatorKind.NotBetween, lower, upper);

    private LibraDexCompositePartCriterion Create(LibraDexConditionOperatorKind operatorKind, params object?[] values)
        => new LibraDexCompositePartCriterion(name, LibraDexConditionValueKind.DateTime, operatorKind, Array.AsReadOnly(values), ignoreCase: false, culture: null);
}

/// <summary>
/// Captures scalar predicates for one named routed composite tier.<br/>
/// </summary>
/// <typeparam name="TValue">The scalar value type.</typeparam>
public sealed class LibraDexCompositeScalarPartCondition<TValue>
{
    private readonly string name;

    internal LibraDexCompositeScalarPartCondition(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        this.name = name;
    }

    /// <summary>
    /// Captures equality against a scalar component value.<br/>
    /// </summary>
    /// <param name="value">The scalar value to match.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion EqualTo(TValue value)
        => Create(LibraDexConditionOperatorKind.EqualTo, value);

    /// <summary>
    /// Captures equality against a scalar null-state for this scalar component.<br/>
    /// `ScalarNull.Null` matches the stored composite null route and `ScalarNull.NonNull` matches ordinary scalar values.<br/>
    /// </summary>
    /// <param name="state">The scalar null-state predicate to match.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion EqualTo(ScalarNull state)
        => Create(LibraDexConditionOperatorKind.EqualTo, state);

    /// <summary>
    /// Captures inequality against a scalar component value.<br/>
    /// This mirrors the normal typed condition operator and records a composite part criterion for the selected route tier.<br/>
    /// </summary>
    /// <param name="value">The scalar value to exclude.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion NotEqualTo(TValue value)
        => Create(LibraDexConditionOperatorKind.NotEqualTo, value);

    /// <summary>
    /// Captures inequality against a scalar null-state for this scalar component.<br/>
    /// This supports concise part presence predicates while preserving a distinct stored null route.<br/>
    /// </summary>
    /// <param name="state">The scalar null-state predicate to exclude.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion NotEqualTo(ScalarNull state)
        => Create(LibraDexConditionOperatorKind.NotEqualTo, state);

    /// <summary>
    /// Captures a scalar component predicate greater than the supplied value.<br/>
    /// </summary>
    /// <param name="value">The exclusive lower value.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion GreaterThan(TValue value)
        => Create(LibraDexConditionOperatorKind.GreaterThan, value);

    /// <summary>
    /// Captures a scalar component predicate greater than or equal to the supplied value.<br/>
    /// </summary>
    /// <param name="value">The inclusive lower value.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion GreaterOrEqual(TValue value)
        => Create(LibraDexConditionOperatorKind.GreaterOrEqual, value);

    /// <summary>
    /// Captures a scalar component predicate less than the supplied value.<br/>
    /// </summary>
    /// <param name="value">The exclusive upper value.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion LessThan(TValue value)
        => Create(LibraDexConditionOperatorKind.LessThan, value);

    /// <summary>
    /// Captures a scalar component predicate less than or equal to the supplied value.<br/>
    /// </summary>
    /// <param name="value">The inclusive upper value.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion LessOrEqual(TValue value)
        => Create(LibraDexConditionOperatorKind.LessOrEqual, value);

    /// <summary>
    /// Captures an inclusive scalar range predicate for one component tier.<br/>
    /// </summary>
    /// <param name="lower">The inclusive lower value.</param>
    /// <param name="upper">The inclusive upper value.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion Between(TValue lower, TValue upper)
        => Create(LibraDexConditionOperatorKind.Between, lower, upper);

    /// <summary>
    /// Captures a scalar component predicate outside the supplied inclusive range.<br/>
    /// The criterion remains part-scoped so the composite executor can evaluate it at the matching routed tier.<br/>
    /// </summary>
    /// <param name="lower">The inclusive lower value of the excluded window.</param>
    /// <param name="upper">The inclusive upper value of the excluded window.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion NotBetween(TValue lower, TValue upper)
        => Create(LibraDexConditionOperatorKind.NotBetween, lower, upper);

    /// <summary>
    /// Captures scalar component membership in a supplied value set.<br/>
    /// The set remains one criterion operand and avoids expanding composite membership into multiple fluent clauses.<br/>
    /// </summary>
    /// <param name="values">The scalar values to match.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion InSet(IEnumerable<TValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return Create(LibraDexConditionOperatorKind.InSet, CaptureMembershipInput(values));
    }

    /// <summary>
    /// Captures scalar component membership using LibraDex's short set spelling.<br/>
    /// This is an alias for <see cref="InSet(IEnumerable{TValue})"/> and preserves the same part-scoped descriptor.<br/>
    /// </summary>
    /// <param name="values">The scalar values to match.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion In(IEnumerable<TValue> values)
        => InSet(values);

    /// <summary>
    /// Captures Abraxas-style scalar component membership.<br/>
    /// This is an alias for <see cref="InSet(IEnumerable{TValue})"/> for copied condition-builder expressions.<br/>
    /// </summary>
    /// <param name="values">The scalar values to match.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion IsIn(IEnumerable<TValue> values)
        => InSet(values);

    /// <summary>
    /// Captures scalar component membership exclusion from a supplied value set.<br/>
    /// The set remains one criterion operand and is evaluated against the selected composite component value.<br/>
    /// </summary>
    /// <param name="values">The scalar values to exclude.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion NotInSet(IEnumerable<TValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return Create(LibraDexConditionOperatorKind.NotInSet, CaptureMembershipInput(values));
    }

    /// <summary>
    /// Captures scalar component membership exclusion using LibraDex's short set spelling.<br/>
    /// This is an alias for <see cref="NotInSet(IEnumerable{TValue})"/> and preserves the same part-scoped descriptor.<br/>
    /// </summary>
    /// <param name="values">The scalar values to exclude.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion NotIn(IEnumerable<TValue> values)
        => NotInSet(values);

    /// <summary>
    /// Captures Abraxas-style scalar component membership exclusion.<br/>
    /// This is an alias for <see cref="NotInSet(IEnumerable{TValue})"/> for copied condition-builder expressions.<br/>
    /// </summary>
    /// <param name="values">The scalar values to exclude.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion IsNotIn(IEnumerable<TValue> values)
        => NotInSet(values);

    /// <summary>
    /// Captures a bitwise-AND equality predicate for one scalar component tier.<br/>
    /// The condition means `(componentValue &amp; bitMask) == equalTo` and is evaluated as a residual predicate inside the routed composite scan for the selected tier path.<br/>
    /// </summary>
    /// <param name="bitMask">The mask applied to each component value.</param>
    /// <param name="equalTo">The expected masked value.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion BitAnd(TValue bitMask, TValue equalTo)
        => Create(LibraDexConditionOperatorKind.BitAndEqualTo, bitMask, equalTo);

    /// <summary>
    /// Captures a bitwise-AND zero predicate for one scalar component tier.<br/>
    /// The condition means `(componentValue &amp; bitMask) == default(TValue)` and preserves Abraxas-style default comparison behavior.<br/>
    /// </summary>
    /// <param name="bitMask">The mask applied to each component value.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion BitAnd(TValue bitMask)
        => BitAnd(bitMask, default!);

    /// <summary>
    /// Captures a bitwise-AND inequality predicate for one scalar component tier.<br/>
    /// The condition means `(componentValue &amp; bitMask) != notEqualTo` and is the primitive form behind any-bit-set checks.<br/>
    /// </summary>
    /// <param name="bitMask">The mask applied to each component value.</param>
    /// <param name="notEqualTo">The masked value that must not match.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion BitAndNotEqualTo(TValue bitMask, TValue notEqualTo)
        => Create(LibraDexConditionOperatorKind.BitAndNotEqualTo, bitMask, notEqualTo);

    /// <summary>
    /// Captures a predicate requiring every bit in <paramref name="bitMask"/> to be set for one scalar component tier.<br/>
    /// This is a readability wrapper for <see cref="BitAnd(TValue, TValue)"/> where the comparison value is the same mask.<br/>
    /// </summary>
    /// <param name="bitMask">The mask whose bits must all be present in each component value.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion AllBitsSet(TValue bitMask)
        => BitAnd(bitMask, bitMask);

    /// <summary>
    /// Captures a predicate requiring at least one bit in <paramref name="bitMask"/> to be set for one scalar component tier.<br/>
    /// This is a readability wrapper for `(componentValue &amp; bitMask) != default(TValue)`.<br/>
    /// </summary>
    /// <param name="bitMask">The mask whose bits are tested for overlap.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion AnyBitsSet(TValue bitMask)
        => BitAndNotEqualTo(bitMask, default!);

    /// <summary>
    /// Captures a predicate requiring no bits in <paramref name="bitMask"/> to be set for one scalar component tier.<br/>
    /// This is a readability wrapper for `(componentValue &amp; bitMask) == default(TValue)`.<br/>
    /// </summary>
    /// <param name="bitMask">The mask whose bits must not overlap each component value.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion NoBitsSet(TValue bitMask)
        => BitAnd(bitMask, default!);

    private LibraDexCompositePartCriterion Create(LibraDexConditionOperatorKind operatorKind, params object?[] values)
        => new LibraDexCompositePartCriterion(name, LibraDexConditionValueKind.Numeric, operatorKind, Array.AsReadOnly(values), ignoreCase: false, culture: null);

    private static object CaptureMembershipInput(IEnumerable<TValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values is ISet<TValue> or IReadOnlyCollection<TValue>
            ? values
            : values.ToArray();
    }
}

/// <summary>
/// Captures numeric comparison and bitmask operators for one adopted condition leaf.<br/>
/// Bitmask operators intentionally stay on numeric selectors rather than string, date, GUID, or binary selectors so they do not leak into unrelated condition grammar.<br/>
/// </summary>
/// <typeparam name="TValue">The numeric operand value accepted by this operator chain.</typeparam>
