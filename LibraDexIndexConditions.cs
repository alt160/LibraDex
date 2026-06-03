namespace LibraDex;

/// <summary>
/// Represents a reusable condition expression over one LibraDex identity group.<br/>
/// The expression remains descriptor-shaped until a terminal method receives an index resolver, preserving Abraxas-style deferred materialization while letting opened index handles provide a lower-friction condition root.<br/>
/// </summary>
/// <typeparam name="TIdentity">The identity type shared by the expression's indexes.</typeparam>
public class LibraDexConditionExpression<TIdentity>
{
    internal LibraDexConditionExpression(LibraDexConditionEndCondition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        Condition = condition;
    }

    /// <summary>
    /// Gets the identity group shared by every index referenced by this expression.<br/>
    /// Cross-index composition requires the same group so identity-set operations cannot accidentally span unrelated identity universes.<br/>
    /// </summary>
    public string Group => Condition.Group;

    internal LibraDexConditionEndCondition Condition { get; }

    /// <summary>
    /// Wraps an already completed condition as a reusable typed expression.<br/>
    /// This is the bridge from the adopted Abraxas-style builder to LibraDex group and index terminal helpers when a condition was not started from an opened index handle.<br/>
    /// </summary>
    /// <param name="condition">The completed condition descriptor.</param>
    /// <returns>A reusable typed condition expression.</returns>
    public static LibraDexConditionExpression<TIdentity> From(LibraDexConditionEndCondition condition)
    {
        return new LibraDexConditionExpression<TIdentity>(condition);
    }

    /// <summary>
    /// Wraps another reusable expression as an explicitly grouped expression.<br/>
    /// The resulting descriptor is semantically equivalent by itself, but it preserves grouping intent when used by generated code or later composition helpers.<br/>
    /// </summary>
    /// <param name="expression">The expression to group.</param>
    /// <returns>A grouped reusable condition expression.</returns>
    public static LibraDexConditionExpression<TIdentity> Grouped(LibraDexConditionExpression<TIdentity> expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return expression.Grouped();
    }

    /// <summary>
    /// Returns this expression as an explicitly grouped reusable expression.<br/>
    /// This mirrors the Abraxas habit of preserving parenthesized fragments while still delaying executable materialization until a terminal call.<br/>
    /// </summary>
    /// <returns>A grouped reusable condition expression.</returns>
    public LibraDexConditionExpression<TIdentity> Grouped()
    {
        LibraDexConditionEndCondition grouped = LibraDexCondition.ForGroup(Group).Group(Condition).EndCondition;
        return new LibraDexConditionExpression<TIdentity>(grouped);
    }

    /// <summary>
    /// Composes this expression with another expression from the same identity group using identity-set intersection.<br/>
    /// This is the hand-written/reusable-fragment counterpart to ordinal `MultiKey(...).Where...And...` construction; both forms normalize to the same executable condition tree.<br/>
    /// Use this form when caller code naturally holds completed condition expressions instead of an ordered index array.<br/>
    /// </summary>
    /// <param name="other">The expression to intersect with this expression.</param>
    /// <returns>A composed condition expression.</returns>
    public LibraDexConditionExpression<TIdentity> AndAlso(LibraDexConditionExpression<TIdentity> other)
    {
        return Compose(other, useOr: false);
    }

    /// <summary>
    /// Composes this expression with a lazily supplied expression from the same identity group using identity-set intersection.<br/>
    /// This lets callers keep reusable fragments in variables and complete a larger expression only when the remaining fragment is known.<br/>
    /// </summary>
    /// <param name="otherFactory">Factory that supplies the expression to intersect with this expression.</param>
    /// <returns>A composed condition expression.</returns>
    public LibraDexConditionExpression<TIdentity> AndAlso(Func<LibraDexConditionExpression<TIdentity>> otherFactory)
    {
        ArgumentNullException.ThrowIfNull(otherFactory);
        return AndAlso(otherFactory());
    }

    /// <summary>
    /// Composes this expression with another expression from the same identity group using identity-set union.<br/>
    /// This is the hand-written/reusable-fragment counterpart to ordinal `MultiKey(...).Where...Or...` construction; both forms normalize to the same executable condition tree.<br/>
    /// Use this form when caller code naturally holds completed condition expressions instead of an ordered index array.<br/>
    /// </summary>
    /// <param name="other">The expression to union with this expression.</param>
    /// <returns>A composed condition expression.</returns>
    public LibraDexConditionExpression<TIdentity> OrElse(LibraDexConditionExpression<TIdentity> other)
    {
        return Compose(other, useOr: true);
    }

    /// <summary>
    /// Composes this expression with a lazily supplied expression from the same identity group using identity-set union.<br/>
    /// This is useful for generated or staged condition assembly where the next fragment should not be selected until composition time.<br/>
    /// </summary>
    /// <param name="otherFactory">Factory that supplies the expression to union with this expression.</param>
    /// <returns>A composed condition expression.</returns>
    public LibraDexConditionExpression<TIdentity> OrElse(Func<LibraDexConditionExpression<TIdentity>> otherFactory)
    {
        ArgumentNullException.ThrowIfNull(otherFactory);
        return OrElse(otherFactory());
    }

    /// <summary>
    /// Replaces every named operand in this expression with a static value.<br/>
    /// The original expression is not modified, so partially built conditions can be assigned once and reused with different runtime bindings.<br/>
    /// </summary>
    /// <param name="name">The operand name to replace.</param>
    /// <param name="value">The static replacement value.</param>
    /// <returns>A new expression with matching operands replaced.</returns>
    public LibraDexConditionExpression<TIdentity> WithValue(string name, object? value)
    {
        return RewriteOperands(name, LibraDexConditionOperand.Value(value, name));
    }

    /// <summary>
    /// Replaces every named operand in this expression with a deferred value factory.<br/>
    /// The factory is evaluated during materialization so a reusable condition can bind to current request state without rebuilding the chain.<br/>
    /// </summary>
    /// <param name="name">The operand name to replace.</param>
    /// <param name="valueFactory">Factory that returns the current operand value.</param>
    /// <returns>A new expression with matching operands replaced.</returns>
    public LibraDexConditionExpression<TIdentity> WithDeferredValue(string name, Func<object?> valueFactory)
    {
        return RewriteOperands(name, LibraDexConditionOperand.Deferred(valueFactory, name));
    }

    /// <summary>
    /// Replaces every named index selector in this expression with a static index name.<br/>
    /// This supports Abraxas-style proppath aliasing in a LibraDex-shaped form where aliases bind to index names inside the expression's identity group.<br/>
    /// </summary>
    /// <param name="name">The selector name to replace.</param>
    /// <param name="indexName">The replacement index name inside this expression's identity group.</param>
    /// <returns>A new expression with matching selectors replaced.</returns>
    public LibraDexConditionExpression<TIdentity> WithIndex(string name, string indexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new LibraDexConditionExpression<TIdentity>(Condition.Rewrite(leaf =>
            string.Equals(leaf.IndexSelector.Name, name, StringComparison.Ordinal)
                ? leaf.WithIndexSelector(LibraDexConditionIndexSelector.Static(indexName, name))
                : leaf));
    }

    /// <summary>
    /// Replaces every named index selector in this expression with a deferred index-name factory.<br/>
    /// The factory is evaluated only when the expression is inspected or materialized, allowing one reusable condition to target different aligned indexes over time.<br/>
    /// </summary>
    /// <param name="name">The selector name to replace.</param>
    /// <param name="indexNameFactory">Factory that returns the replacement index name inside this expression's identity group.</param>
    /// <returns>A new expression with matching selectors replaced.</returns>
    public LibraDexConditionExpression<TIdentity> WithDeferredIndex(string name, Func<string> indexNameFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new LibraDexConditionExpression<TIdentity>(Condition.Rewrite(leaf =>
            string.Equals(leaf.IndexSelector.Name, name, StringComparison.Ordinal)
                ? leaf.WithIndexSelector(LibraDexConditionIndexSelector.Deferred(indexNameFactory, name))
                : leaf));
    }

    /// <summary>
    /// Materializes this expression to the existing identity-criterion tree with a caller supplied index resolver.<br/>
    /// The resolver is evaluated only at materialization time, allowing reusable condition variables to bind to different catalog sessions.<br/>
    /// </summary>
    /// <param name="resolveIndex">Function that resolves index names inside this expression's group.</param>
    /// <returns>An executable identity criterion.</returns>
    public IIdentityCriterion Materialize(Func<string, IIndex> resolveIndex)
    {
        return Condition.Materialize(resolveIndex);
    }

    /// <summary>
    /// Materializes matching identities as a typed list.<br/>
    /// This is the terminal form used by group and index convenience APIs; it delegates to the adopted condition materializer rather than adding a parallel retrieval grammar.<br/>
    /// </summary>
    /// <param name="resolveIndex">Function that resolves index names inside this expression's group.</param>
    /// <param name="ordering">The requested identity ordering contract.</param>
    /// <param name="deduplication">The requested duplicate identity policy.</param>
    /// <param name="skip">The number of matching identities to skip.</param>
    /// <param name="take">The optional maximum number of identities to return.</param>
    /// <param name="bookmark">The optional continuation bookmark.</param>
    /// <returns>A typed list of matching identities.</returns>
    public IReadOnlyList<TIdentity> GetIdentities(
        Func<string, IIndex> resolveIndex,
        IdentityResultOrdering ordering = IdentityResultOrdering.PlanNatural,
        IdentityDeduplication deduplication = IdentityDeduplication.Distinct,
        int skip = 0,
        int? take = null,
        LibraDexBookmark? bookmark = null)
    {
        return Condition.ToList<TIdentity>(resolveIndex, ordering, deduplication, skip, take, bookmark);
    }

    private LibraDexConditionExpression<TIdentity> Compose(LibraDexConditionExpression<TIdentity> other, bool useOr)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (!string.Equals(Group, other.Group, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("LibraDex condition expressions can only compose inside the same identity group.");
        }

        LibraDexConditionContinueOrEnd left = LibraDexCondition.ForGroup(Group).Group(Condition);
        LibraDexConditionEndCondition composed = useOr
            ? left.OR.Group(other.Condition).EndCondition
            : left.AND.Group(other.Condition).EndCondition;
        return new LibraDexConditionExpression<TIdentity>(composed);
    }

    private LibraDexConditionExpression<TIdentity> RewriteOperands(string name, LibraDexConditionOperand replacement)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new LibraDexConditionExpression<TIdentity>(Condition.Rewrite(leaf =>
        {
            LibraDexConditionOperand[] operands = new LibraDexConditionOperand[leaf.Operands.Count];
            bool changed = false;
            for (int i = 0; i < operands.Length; i++)
            {
                LibraDexConditionOperand operand = leaf.Operands[i];
                if (string.Equals(operand.Name, name, StringComparison.Ordinal))
                {
                    operands[i] = replacement;
                    changed = true;
                }
                else
                {
                    operands[i] = operand;
                }
            }

            return changed ? leaf.WithOperands(operands) : leaf;
        }));
    }
}

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
    /// Ends opened-index fluent construction and returns the reusable expression accepted by terminal APIs.<br/>
    /// Until this boundary is reached the condition remains a continuation-capable grammar state, preserving Abraxas-style variablization and later `.And` / `.Or` continuation.<br/>
    /// </summary>
    public LibraDexConditionExpression<TIdentity> EndCondition => LibraDexConditionExpression<TIdentity>.From(Condition);

    /// <summary>
    /// Ends opened-index fluent construction and returns the reusable expression accepted by terminal APIs.<br/>
    /// This compact alias mirrors the adopted condition builder's `ec` alias for callers that prefer terse handwritten filters.<br/>
    /// </summary>
    public LibraDexConditionExpression<TIdentity> ec => EndCondition;

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
    /// Resumes same-index condition grammar from an existing reusable expression.<br/>
    /// This is intended for staged builders that stored a fragment as `LibraDexConditionExpression&lt;TIdentity&gt;` and later need to append another predicate over this opened index.<br/>
    /// </summary>
    /// <param name="expression">The existing expression to continue.</param>
    /// <returns>A same-index continuation rooted at the supplied expression.</returns>
    public LibraDexIndexConditionContinuation<TKey, TIdentity> Continue(LibraDexConditionExpression<TIdentity> expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        if (!string.Equals(expression.Group, index.Group, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The supplied condition expression belongs to a different LibraDex identity group.");
        }

        return new LibraDexIndexConditionContinuation<TKey, TIdentity>(
            new LibraDexIndexCondition<TKey, TIdentity>(index, expression.Condition),
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
        return Continue(condition.EndCondition);
    }

    /// <summary>
    /// Resumes same-index condition grammar from an existing reusable expression using union for the next predicate.<br/>
    /// This keeps staged same-index `.Or` continuation available even after a fragment has been widened to the common expression type.<br/>
    /// </summary>
    /// <param name="expression">The existing expression to continue.</param>
    /// <returns>A same-index union continuation rooted at the supplied expression.</returns>
    public LibraDexIndexConditionContinuation<TKey, TIdentity> ContinueOr(LibraDexConditionExpression<TIdentity> expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        if (!string.Equals(expression.Group, index.Group, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The supplied condition expression belongs to a different LibraDex identity group.");
        }

        return new LibraDexIndexConditionContinuation<TKey, TIdentity>(
            new LibraDexIndexCondition<TKey, TIdentity>(index, expression.Condition),
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
        return ContinueOr(condition.EndCondition);
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
    /// Captures inequality against the opened index key type.<br/>
    /// </summary>
    /// <param name="value">The key value to exclude.</param>
    /// <returns>A reusable index-rooted condition expression.</returns>
    public LibraDexIndexCondition<TKey, TIdentity> NotEqualTo(TKey value, string? name = null)
    {
        return Create(LibraDexConditionOperatorKind.NotEqualTo, LibraDexConditionOperand.Value(value, name));
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
    /// Captures inequality for the next same-index predicate.<br/>
    /// </summary>
    /// <param name="value">The key value to exclude.</param>
    /// <returns>A composed same-index condition expression.</returns>
    public LibraDexIndexCondition<TKey, TIdentity> NotEqualTo(TKey value)
    {
        return Compose(new LibraDexIndexWhere<TKey, TIdentity>(index, negateNext).NotEqualTo(value));
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
/// Represents a completed non-generic condition over one LibraDex identity group.<br/>
/// This is the descriptor returned by group-level multi-key builders where the identity type is supplied by the terminal API instead of the selector chain.<br/>
/// </summary>
public sealed class LibraDexGroupCondition
{
    internal LibraDexGroupCondition(LibraDexConditionEndCondition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        EndCondition = condition;
    }

    /// <summary>
    /// Gets the identity group shared by every index referenced by this condition.<br/>
    /// </summary>
    public string Group => EndCondition.Group;

    internal LibraDexConditionEndCondition EndCondition { get; }

    /// <summary>
    /// Converts this non-generic descriptor into the typed condition expression expected by typed terminal APIs.<br/>
    /// The conversion does not materialize data; it only records the caller's expected identity type for the later terminal read, delete, or mutation operation.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The identity type expected from the participating indexes.</typeparam>
    /// <returns>A typed reusable condition expression.</returns>
    public LibraDexConditionExpression<TIdentity> As<TIdentity>()
    {
        return LibraDexConditionExpression<TIdentity>.From(EndCondition);
    }
}

/// <summary>
/// Starts typed selector-first conditions over one identity group.<br/>
/// Selector methods identify the index by name, opened handle, or ordered multi-key ordinal; operator calls then provide the condition value.<br/>
/// This is a construction convenience for programmatic callers: ordinal, name, and handle selectors all emit ordinary index-name condition leaves that execute through the same planner as `.AndAlso(...)`, `.OrElse(...)`, and grouped handwritten expressions.<br/>
/// </summary>
public sealed class LibraDexMultiKeyWhere
{
    private readonly string group;
    private readonly LibraDexConditionClause clause;
    private readonly IIndex[]? orderedIndexes;

    internal LibraDexMultiKeyWhere(string group)
        : this(group, LibraDexCondition.ForGroup(group), null)
    {
    }

    internal LibraDexMultiKeyWhere(string group, LibraDexConditionClause clause, IIndex[]? orderedIndexes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentNullException.ThrowIfNull(clause);
        this.group = group;
        this.clause = clause;
        this.orderedIndexes = orderedIndexes;
    }

    /// <summary>
    /// Selects a string-keyed index by name for the next predicate.<br/>
    /// The index name is resolved only at materialization time so reusable conditions can be built before indexes are opened.<br/>
    /// </summary>
    /// <param name="indexName">The index name inside the current identity group.</param>
    /// <returns>String operators for the selected index.</returns>
    public LibraDexMultiKeyStringWhere String(string indexName)
    {
        return new LibraDexMultiKeyStringWhere(clause.Index(indexName).AsString, orderedIndexes);
    }

    /// <summary>
    /// Selects a string-keyed opened index handle for the next predicate.<br/>
    /// The handle validates group and key type immediately, then the descriptor stores the handle's index name for normal deferred materialization.<br/>
    /// </summary>
    /// <param name="index">The opened string-keyed index handle.</param>
    /// <returns>String operators for the selected index.</returns>
    public LibraDexMultiKeyStringWhere String(IIndex index)
    {
        return String(ValidateIndex(index, typeof(string)).Name);
    }

    /// <summary>
    /// Selects a string-keyed participant by ordinal from an ordered `MultiKey(...)` builder.<br/>
    /// Ordinals make generated and array-backed condition assembly concise while still compiling to the same index-name descriptors produced by handwritten composition.<br/>
    /// </summary>
    /// <param name="ordinal">The zero-based participant ordinal supplied to `MultiKey(...)`.</param>
    /// <returns>String operators for the selected index.</returns>
    public LibraDexMultiKeyStringWhere String(int ordinal)
    {
        return String(ResolveOrdinal(ordinal, typeof(string)).Name);
    }

    /// <summary>
    /// Selects a byte-array-keyed index by name for the next predicate.<br/>
    /// Binary selectors keep raw bytes as bytes; pattern helpers such as `StartsWith` and `SliceEqual` compile to encoded key-byte predicates rather than string conversions.<br/>
    /// </summary>
    /// <param name="indexName">The index name inside the current identity group.</param>
    /// <returns>Binary operators for the selected index.</returns>
    public LibraDexMultiKeyBinaryWhere Binary(string indexName)
    {
        return new LibraDexMultiKeyBinaryWhere(clause.Index(indexName).AsBinary, orderedIndexes);
    }

    /// <summary>
    /// Selects a byte-array-keyed opened index handle for the next predicate.<br/>
    /// The handle validates group and key type immediately, then the descriptor stores the handle's index name for normal deferred materialization.<br/>
    /// </summary>
    /// <param name="index">The opened byte-array-keyed index handle.</param>
    /// <returns>Binary operators for the selected index.</returns>
    public LibraDexMultiKeyBinaryWhere Binary(IIndex index)
    {
        return Binary(ValidateIndex(index, typeof(byte[])).Name);
    }

    /// <summary>
    /// Selects a byte-array-keyed participant by ordinal from an ordered `MultiKey(...)` builder.<br/>
    /// </summary>
    /// <param name="ordinal">The zero-based participant ordinal supplied to `MultiKey(...)`.</param>
    /// <returns>Binary operators for the selected index.</returns>
    public LibraDexMultiKeyBinaryWhere Binary(int ordinal)
    {
        return Binary(ResolveOrdinal(ordinal, typeof(byte[])).Name);
    }

    /// <summary>
    /// Selects a Guid-keyed index by name for the next predicate.<br/>
    /// The selector identifies the index, not the condition value; values are supplied by the returned operator methods such as `EqualTo`.<br/>
    /// </summary>
    /// <param name="indexName">The index name inside the current identity group.</param>
    /// <returns>Guid operators for the selected index.</returns>
    public LibraDexMultiKeyGuidWhere Guid(string indexName)
    {
        return new LibraDexMultiKeyGuidWhere(clause.Index(indexName).AsGuid, orderedIndexes);
    }

    /// <summary>
    /// Selects a Guid-keyed opened index handle for the next predicate.<br/>
    /// The method intentionally accepts an index handle rather than a Guid value, keeping selector and operand roles distinct in IntelliSense and code reviews.<br/>
    /// </summary>
    /// <param name="index">The opened Guid-keyed index handle.</param>
    /// <returns>Guid operators for the selected index.</returns>
    public LibraDexMultiKeyGuidWhere Guid(IIndex index)
    {
        return Guid(ValidateIndex(index, typeof(Guid)).Name);
    }

    /// <summary>
    /// Selects a Guid-keyed participant by ordinal from an ordered `MultiKey(...)` builder.<br/>
    /// </summary>
    /// <param name="ordinal">The zero-based participant ordinal supplied to `MultiKey(...)`.</param>
    /// <returns>Guid operators for the selected index.</returns>
    public LibraDexMultiKeyGuidWhere Guid(int ordinal)
    {
        return Guid(ResolveOrdinal(ordinal, typeof(Guid)).Name);
    }

    /// <summary>
    /// Selects a DateTime-keyed index by name for the next predicate.<br/>
    /// Date part operators use the same structured date condition descriptor as the adopted Abraxas-style builder.<br/>
    /// </summary>
    /// <param name="indexName">The index name inside the current identity group.</param>
    /// <returns>DateTime operators for the selected index.</returns>
    public LibraDexMultiKeyDateWhere<DateTime> Date(string indexName)
    {
        return new LibraDexMultiKeyDateWhere<DateTime>(clause.Index(indexName).AsDate, orderedIndexes);
    }

    /// <summary>
    /// Selects a DateTime-keyed opened index handle for the next predicate.<br/>
    /// </summary>
    /// <param name="index">The opened DateTime-keyed index handle.</param>
    /// <returns>DateTime operators for the selected index.</returns>
    public LibraDexMultiKeyDateWhere<DateTime> Date(IIndex index)
    {
        return Date(ValidateIndex(index, typeof(DateTime)).Name);
    }

    /// <summary>
    /// Selects a DateTime-keyed participant by ordinal from an ordered `MultiKey(...)` builder.<br/>
    /// </summary>
    /// <param name="ordinal">The zero-based participant ordinal supplied to `MultiKey(...)`.</param>
    /// <returns>DateTime operators for the selected index.</returns>
    public LibraDexMultiKeyDateWhere<DateTime> Date(int ordinal)
    {
        return Date(ResolveOrdinal(ordinal, typeof(DateTime)).Name);
    }

    /// <summary>
    /// Selects an Int32-keyed index by name for the next predicate.<br/>
    /// </summary>
    /// <param name="indexName">The index name inside the current identity group.</param>
    /// <returns>Int32 operators for the selected index.</returns>
    public LibraDexMultiKeyScalarWhere<int> Int32(string indexName)
    {
        return new LibraDexMultiKeyScalarWhere<int>(clause.Index(indexName).AsInt32, orderedIndexes);
    }

    /// <summary>
    /// Selects an Int32-keyed opened index handle for the next predicate.<br/>
    /// </summary>
    /// <param name="index">The opened Int32-keyed index handle.</param>
    /// <returns>Int32 operators for the selected index.</returns>
    public LibraDexMultiKeyScalarWhere<int> Int32(IIndex index)
    {
        return Int32(ValidateIndex(index, typeof(int)).Name);
    }

    /// <summary>
    /// Selects an Int32-keyed participant by ordinal from an ordered `MultiKey(...)` builder.<br/>
    /// </summary>
    /// <param name="ordinal">The zero-based participant ordinal supplied to `MultiKey(...)`.</param>
    /// <returns>Int32 operators for the selected index.</returns>
    public LibraDexMultiKeyScalarWhere<int> Int32(int ordinal)
    {
        return Int32(ResolveOrdinal(ordinal, typeof(int)).Name);
    }

    /// <summary>
    /// Selects an Int64-keyed index by name for the next predicate.<br/>
    /// </summary>
    /// <param name="indexName">The index name inside the current identity group.</param>
    /// <returns>Int64 operators for the selected index.</returns>
    public LibraDexMultiKeyScalarWhere<long> Int64(string indexName)
    {
        return new LibraDexMultiKeyScalarWhere<long>(clause.Index(indexName).AsInt64, orderedIndexes);
    }

    /// <summary>
    /// Selects an Int64-keyed opened index handle for the next predicate.<br/>
    /// </summary>
    /// <param name="index">The opened Int64-keyed index handle.</param>
    /// <returns>Int64 operators for the selected index.</returns>
    public LibraDexMultiKeyScalarWhere<long> Int64(IIndex index)
    {
        return Int64(ValidateIndex(index, typeof(long)).Name);
    }

    /// <summary>
    /// Selects an Int64-keyed participant by ordinal from an ordered `MultiKey(...)` builder.<br/>
    /// </summary>
    /// <param name="ordinal">The zero-based participant ordinal supplied to `MultiKey(...)`.</param>
    /// <returns>Int64 operators for the selected index.</returns>
    public LibraDexMultiKeyScalarWhere<long> Int64(int ordinal)
    {
        return Int64(ResolveOrdinal(ordinal, typeof(long)).Name);
    }

    private IIndex ValidateIndex(IIndex index, Type expectedKeyType)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (!string.Equals(index.Group, group, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The supplied index handle belongs to a different LibraDex identity group.");
        }

        if (index.KeyType != expectedKeyType)
        {
            throw new ArgumentException($"Index '{index.Name}' has key type {index.KeyType.FullName}, but this selector requires {expectedKeyType.FullName}.", nameof(index));
        }

        return index;
    }

    private IIndex ResolveOrdinal(int ordinal, Type expectedKeyType)
    {
        if (orderedIndexes is null)
        {
            throw new InvalidOperationException("Ordinal selectors require a condition started from MultiKey(...).");
        }

        if ((uint)ordinal >= (uint)orderedIndexes.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal), "The multi-key ordinal is outside the participant list.");
        }

        return ValidateIndex(orderedIndexes[ordinal], expectedKeyType);
    }
}

/// <summary>
/// Continues a group-level multi-key condition after one predicate has been captured.<br/>
/// The next selector remains typed so `.And.String(indexName)` and `.Or.Guid(ordinal)` preserve selector-value separation through the whole chain.<br/>
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
    /// Adds an identity-set intersection and starts the next typed selector.<br/>
    /// </summary>
    public LibraDexMultiKeyWhere And => new(group, continuation.AND, orderedIndexes);

    /// <summary>
    /// Adds an identity-set union and starts the next typed selector.<br/>
    /// </summary>
    public LibraDexMultiKeyWhere Or => new(group, continuation.OR, orderedIndexes);

    /// <summary>
    /// Completes the condition as a non-generic group descriptor.<br/>
    /// </summary>
    public LibraDexGroupCondition Condition => new(continuation.EndCondition);

    /// <summary>
    /// Converts the completed condition into a typed reusable expression.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The identity type expected from the participating indexes.</typeparam>
    /// <returns>A typed reusable condition expression.</returns>
    public LibraDexConditionExpression<TIdentity> As<TIdentity>()
    {
        return Condition.As<TIdentity>();
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
    /// Captures inequality against the selected index.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation NotEqualTo(TValue value) => LibraDexMultiKeyContinuation.From(inner.NotEqualTo(value), orderedIndexes);

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
    public LibraDexMultiKeyContinuation EqualTo(string value, bool ignoreCase = false, string? culture = null) => LibraDexMultiKeyContinuation.From(inner.EqualTo(value, ignoreCase, culture), orderedIndexes);

    /// <summary>
    /// Captures string inequality against the selected index.<br/>
    /// The selector remains index-bound while the supplied value is recorded as the operand for later materialization.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation NotEqualTo(string value, bool ignoreCase = false, string? culture = null) => LibraDexMultiKeyContinuation.From(inner.NotEqualTo(value, ignoreCase, culture), orderedIndexes);

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
    /// Captures a full stored-byte Guid pattern condition against the selected Guid index.<br/>
    /// The byte order is the same order produced by `Guid.TryWriteBytes`, and every nibble is compared.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation MatchesPattern(byte[] pattern) => LibraDexMultiKeyContinuation.From(inner.MatchesPattern(pattern), orderedIndexes);
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
    /// Captures byte-array inequality against the selected binary index.<br/>
    /// </summary>
    public LibraDexMultiKeyContinuation NotEqualTo(byte[] value) => LibraDexMultiKeyContinuation.From(inner.NotEqualTo(value), orderedIndexes);

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
    /// Starts a condition builder whose ordinal selectors map to the ordered indexes supplied to `MultiKey(...)`.<br/>
    /// The resulting predicates normalize to the same identity-group condition tree as manually composed opened-index expressions.<br/>
    /// </summary>
    public LibraDexMultiKeyWhere Where => new(group, LibraDexCondition.ForGroup(group), indexes);
}
