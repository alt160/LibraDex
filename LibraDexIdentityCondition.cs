using System.Collections;

namespace LibraDex;

/// <summary>
/// Represents one condition operand that can be materialized at execution time.<br/>
/// Static operands preserve ordinary handwritten call sites; factory operands preserve Abraxas-style late value resolution and parameter replacement workflows.<br/>
/// </summary>
public sealed class LibraDexConditionOperand
{
    private readonly object? staticValue;
    private readonly Func<object?>? valueFactory;

    private LibraDexConditionOperand(object? staticValue, Func<object?>? valueFactory, string? name)
    {
        this.staticValue = staticValue;
        this.valueFactory = valueFactory;
        Name = name;
    }

    /// <summary>
    /// Gets the optional parameter name used by replacement APIs.<br/>
    /// Named operands let adapters reuse canned condition shapes while swapping values before materialization.<br/>
    /// </summary>
    public string? Name { get; }

    internal bool IsDeferred => valueFactory is not null;

    /// <summary>
    /// Creates a static condition operand.<br/>
    /// </summary>
    /// <param name="value">The value to capture.</param>
    /// <param name="name">Optional replacement name.</param>
    /// <returns>A condition operand.</returns>
    public static LibraDexConditionOperand Value(object? value, string? name = null)
    {
        return new LibraDexConditionOperand(value, valueFactory: null, name);
    }

    /// <summary>
    /// Creates a late-bound condition operand.<br/>
    /// The supplied factory is invoked when the condition is materialized, not when the condition is built.<br/>
    /// </summary>
    /// <param name="valueFactory">The value factory.</param>
    /// <param name="name">Optional replacement name.</param>
    /// <returns>A condition operand.</returns>
    public static LibraDexConditionOperand Deferred(Func<object?> valueFactory, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(valueFactory);
        return new LibraDexConditionOperand(staticValue: null, valueFactory, name);
    }

    /// <summary>
    /// Materializes the operand value.<br/>
    /// </summary>
    /// <returns>The current static or deferred value.</returns>
    public object? GetValue()
    {
        return valueFactory is null ? staticValue : valueFactory();
    }

    internal LibraDexConditionOperand Replace(string name, LibraDexConditionOperand replacement)
    {
        return string.Equals(Name, name, StringComparison.Ordinal)
            ? replacement
            : this;
    }
}

/// <summary>
/// Represents a reusable identity condition over one LibraDex identity group.<br/>
/// Conditions preserve grouping and late-bound operands, then materialize into <see cref="IIdentityCriterion"/> trees when execution or planning is requested.<br/>
/// </summary>
public sealed class LibraDexIdentityCondition
{
    private readonly LibraDexIdentityConditionNode root;
    private IIdentityCriterion? cachedStaticCriterion;

    internal LibraDexIdentityCondition(string group, LibraDexIdentityConditionNode root)
    {
        Group = group;
        this.root = root;
    }

    /// <summary>
    /// Gets the identity group over which this condition is valid.<br/>
    /// </summary>
    public string Group { get; }

    /// <summary>
    /// Gets the distinct named operands used by this condition.<br/>
    /// This lets generated callers inspect reusable condition templates and supply replacements without maintaining a separate parameter list out-of-band.<br/>
    /// </summary>
    public IReadOnlyList<string> ParameterNames => root.GetParameterNames();

    /// <summary>
    /// Gets criteria-scoped mutation descriptor builders for this condition.<br/>
    /// The returned builder materializes the condition when a mutation descriptor is requested, preserving deferred operand validation at the execution boundary.<br/>
    /// </summary>
    public IIdentityCriterionMutationBuilder Mutate => Materialize().Mutate;

    /// <summary>
    /// Gets the terminal identity projection for this condition.<br/>
    /// This keeps condition-first call sites from reaching into `Materialize()` when they only need the normal identity projection surface.<br/>
    /// </summary>
    public IIdentityCriterionProjection IDs => Materialize().IDs;

    /// <summary>
    /// Creates a terminal identity projection for this condition with explicit ordering, duplicate handling, paging, and bookmark options.<br/>
    /// This is the condition-builder equivalent of `IIdentityCriterion.IDsWith(...)` and keeps generated callers on the condition object as the canonical query descriptor.<br/>
    /// </summary>
    /// <param name="ordering">The requested identity ordering contract.</param>
    /// <param name="deduplication">The requested duplicate identity policy.</param>
    /// <param name="skip">The number of matching identities to skip.</param>
    /// <param name="take">The optional maximum number of identities to return.</param>
    /// <param name="bookmark">The optional continuation bookmark.</param>
    /// <returns>An identity projection descriptor.</returns>
    public IIdentityCriterionProjection IDsWith(
        IdentityResultOrdering ordering = IdentityResultOrdering.PlanNatural,
        IdentityDeduplication deduplication = IdentityDeduplication.Distinct,
        int skip = 0,
        int? take = null,
        LibraDexBookmark? bookmark = null)
    {
        return Materialize().IDsWith(ordering, deduplication, skip, take, bookmark);
    }

    /// <summary>
    /// Materializes this condition into the existing programmatic identity-criteria tree.<br/>
    /// Deferred operands are evaluated during this call, which mirrors Abraxas' execution-time parameter materialization model.<br/>
    /// </summary>
    /// <returns>An identity criterion tree.</returns>
    public IIdentityCriterion Materialize()
    {
        if (root.HasDeferredOperands)
        {
            return root.Materialize();
        }

        return cachedStaticCriterion ??= root.Materialize();
    }

    /// <summary>
    /// Builds an execution-plan descriptor for this condition without materializing matching identities.<br/>
    /// This is the condition-builder bridge from public intent to LibraDex's internal primitive execution planner.<br/>
    /// </summary>
    /// <param name="ordering">The requested identity ordering contract.</param>
    /// <param name="deduplication">The requested duplicate identity policy.</param>
    /// <param name="skip">The number of matching identities to skip.</param>
    /// <param name="take">The optional maximum number of identities to return.</param>
    /// <param name="bookmark">The optional continuation bookmark.</param>
    /// <returns>An identity execution plan descriptor.</returns>
    public LibraDexIdentityExecutionPlan Plan(
        IdentityResultOrdering ordering = IdentityResultOrdering.PlanNatural,
        IdentityDeduplication deduplication = IdentityDeduplication.Distinct,
        int skip = 0,
        int? take = null,
        LibraDexBookmark? bookmark = null)
    {
        return Project(ordering, deduplication, skip, take, bookmark).Plan();
    }

    /// <summary>
    /// Executes this condition and materializes matching identities plus plan diagnostics.<br/>
    /// `Get` is the canonical materializing bridge for programmatic callers; direct index lookup verbs can remain compatibility adapters over the same condition vocabulary.<br/>
    /// </summary>
    /// <param name="ordering">The requested identity ordering contract.</param>
    /// <param name="deduplication">The requested duplicate identity policy.</param>
    /// <param name="skip">The number of matching identities to skip.</param>
    /// <param name="take">The optional maximum number of identities to return.</param>
    /// <param name="bookmark">The optional continuation bookmark.</param>
    /// <returns>The materialized identity execution result.</returns>
    public LibraDexIdentityExecutionResult Get(
        IdentityResultOrdering ordering = IdentityResultOrdering.PlanNatural,
        IdentityDeduplication deduplication = IdentityDeduplication.Distinct,
        int skip = 0,
        int? take = null,
        LibraDexBookmark? bookmark = null)
    {
        return Project(ordering, deduplication, skip, take, bookmark).Execute();
    }

    /// <summary>
    /// Determines whether this condition has at least one matching identity.<br/>
    /// The current bridge asks for one identity through the materializing executor; later physical execution can replace this with a true stop-at-first primitive without changing the public condition shape.<br/>
    /// </summary>
    /// <param name="deduplication">The duplicate identity policy to apply before existence is reported.</param>
    /// <returns><see langword="true"/> when the condition returns at least one identity.</returns>
    public bool Exists(IdentityDeduplication deduplication = IdentityDeduplication.Distinct)
    {
        return LibraDexIdentityExecutionPlanner.Exists(Materialize(), deduplication);
    }

    /// <summary>
    /// Counts matching identities for this condition.<br/>
    /// This is the condition-builder aggregate bridge for identity counts; physical execution can later replace the materialized count with shelf/run counters where available.<br/>
    /// </summary>
    /// <param name="deduplication">The duplicate identity policy to apply before counting.</param>
    /// <returns>The number of matching identities after the selected duplicate policy is applied.</returns>
    public long Count(IdentityDeduplication deduplication = IdentityDeduplication.Distinct)
    {
        return LibraDexIdentityExecutionPlanner.Count(Materialize(), deduplication);
    }

    /// <summary>
    /// Executes this condition and yields materialized identity objects in the selected result order.<br/>
    /// The first bridge delegates to the materializing executor; later physical executors can replace this with a streaming primitive cursor without changing the public condition shape.<br/>
    /// </summary>
    /// <param name="ordering">The requested identity ordering contract.</param>
    /// <param name="deduplication">The requested duplicate identity policy.</param>
    /// <param name="skip">The number of matching identities to skip.</param>
    /// <param name="take">The optional maximum number of identities to return.</param>
    /// <param name="bookmark">The optional continuation bookmark.</param>
    /// <returns>An enumerable over matching identity objects.</returns>
    public IEnumerable<object> Iterate(
        IdentityResultOrdering ordering = IdentityResultOrdering.PlanNatural,
        IdentityDeduplication deduplication = IdentityDeduplication.Distinct,
        int skip = 0,
        int? take = null,
        LibraDexBookmark? bookmark = null)
    {
        return IDsWith(ordering, deduplication, skip, take, bookmark).Iterate();
    }

    /// <summary>
    /// Executes this condition and yields typed identities in the selected result order.<br/>
    /// This is the streaming counterpart to <see cref="ToList{TIdentity}"/> and avoids adapter-side object casts when the identity type is known.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The expected identity CLR type.</typeparam>
    /// <param name="ordering">The requested identity ordering contract.</param>
    /// <param name="deduplication">The requested duplicate identity policy.</param>
    /// <param name="skip">The number of matching identities to skip.</param>
    /// <param name="take">The optional maximum number of identities to return.</param>
    /// <param name="bookmark">The optional continuation bookmark.</param>
    /// <returns>An enumerable over matching typed identities.</returns>
    public IEnumerable<TIdentity> Iterate<TIdentity>(
        IdentityResultOrdering ordering = IdentityResultOrdering.PlanNatural,
        IdentityDeduplication deduplication = IdentityDeduplication.Distinct,
        int skip = 0,
        int? take = null,
        LibraDexBookmark? bookmark = null)
    {
        return IDsWith(ordering, deduplication, skip, take, bookmark).Iterate<TIdentity>();
    }

    /// <summary>
    /// Executes this condition and materializes typed identities.<br/>
    /// The helper keeps adapters from hand-writing runtime casts when they know the identity CLR type at their boundary.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The expected identity CLR type.</typeparam>
    /// <param name="ordering">The requested identity ordering contract.</param>
    /// <param name="deduplication">The requested duplicate identity policy.</param>
    /// <param name="skip">The number of matching identities to skip.</param>
    /// <param name="take">The optional maximum number of identities to return.</param>
    /// <param name="bookmark">The optional continuation bookmark.</param>
    /// <returns>A materialized typed identity list.</returns>
    public IReadOnlyList<TIdentity> ToList<TIdentity>(
        IdentityResultOrdering ordering = IdentityResultOrdering.PlanNatural,
        IdentityDeduplication deduplication = IdentityDeduplication.Distinct,
        int skip = 0,
        int? take = null,
        LibraDexBookmark? bookmark = null)
    {
        return Project(ordering, deduplication, skip, take, bookmark).ToList<TIdentity>();
    }

    private IIdentityCriterionProjection Project(
        IdentityResultOrdering ordering,
        IdentityDeduplication deduplication,
        int skip,
        int? take,
        LibraDexBookmark? bookmark)
    {
        return Materialize().IDsWith(ordering, deduplication, skip, take, bookmark);
    }

    /// <summary>
    /// Creates a negated condition descriptor over this condition's existing root.<br/>
    /// This is useful when an adapter has already built a reusable condition and needs complement semantics without rebuilding every leaf as negated.<br/>
    /// </summary>
    /// <returns>A cloned condition whose root is negated.</returns>
    public LibraDexIdentityCondition Not()
    {
        return new LibraDexIdentityCondition(Group, LibraDexIdentityConditionNode.Not(root));
    }

    /// <summary>
    /// Creates a condition with every matching named operand replaced by a static value.<br/>
    /// This supports canned condition shapes where generated callers swap parameter values immediately before materialization.<br/>
    /// </summary>
    /// <param name="name">The operand name to replace.</param>
    /// <param name="value">The replacement value.</param>
    /// <returns>A cloned condition with matching operands replaced.</returns>
    public LibraDexIdentityCondition Replace(string name, object? value)
    {
        return Replace(name, LibraDexConditionOperand.Value(value, name));
    }

    /// <summary>
    /// Creates a condition with every matching named operand replaced by a deferred value factory.<br/>
    /// </summary>
    /// <param name="name">The operand name to replace.</param>
    /// <param name="valueFactory">The replacement value factory.</param>
    /// <returns>A cloned condition with matching operands replaced.</returns>
    public LibraDexIdentityCondition Replace(string name, Func<object?> valueFactory)
    {
        return Replace(name, LibraDexConditionOperand.Deferred(valueFactory, name));
    }

    /// <summary>
    /// Creates a condition with every matching named operand replaced by the supplied operand.<br/>
    /// </summary>
    /// <param name="name">The operand name to replace.</param>
    /// <param name="operand">The replacement operand.</param>
    /// <returns>A cloned condition with matching operands replaced.</returns>
    public LibraDexIdentityCondition Replace(string name, LibraDexConditionOperand operand)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(operand);
        return new LibraDexIdentityCondition(Group, root.Replace(name, operand));
    }
}

/// <summary>
/// Entry point for Abraxas-style identity condition construction.<br/>
/// The builder is grouped first so accidental cross-identity composition fails while the condition is being built rather than at execution time.<br/>
/// </summary>
public static class LibraDexIdentityConditions
{
    /// <summary>
    /// Starts a condition builder for one identity group.<br/>
    /// </summary>
    /// <param name="group">The identity group name.</param>
    /// <returns>A condition builder scoped to the supplied group.</returns>
    public static LibraDexIdentityConditionBuilder ForGroup(string group)
    {
        return new LibraDexIdentityConditionBuilder(group);
    }
}

/// <summary>
/// Builds a grouped identity condition from index-backed leaves and explicit boolean composition.<br/>
/// </summary>
public sealed class LibraDexIdentityConditionBuilder
{
    private LibraDexIdentityConditionNode? current;
    private LibraDexIdentityCriterionNodeKind? pendingOperation;

    /// <summary>
    /// Creates a condition builder for one identity group.<br/>
    /// </summary>
    /// <param name="group">The identity group name.</param>
    public LibraDexIdentityConditionBuilder(string group)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        Group = group;
    }

    /// <summary>
    /// Gets the identity group for this builder.<br/>
    /// </summary>
    public string Group { get; }

    /// <summary>
    /// Starts an index-backed leaf condition.<br/>
    /// </summary>
    /// <param name="index">The index to query.</param>
    /// <returns>A leaf clause builder.</returns>
    public LibraDexIdentityConditionClause Where(IIndex index)
    {
        ValidateIndex(index);
        return new LibraDexIdentityConditionClause(this, index, negate: false);
    }

    /// <summary>
    /// Starts a negated index-backed leaf condition.<br/>
    /// This provides the condition-builder counterpart to `index.Not.Find(key)` while preserving explicit boolean grouping.<br/>
    /// </summary>
    /// <param name="index">The index to query.</param>
    /// <returns>A negated leaf clause builder.</returns>
    public LibraDexIdentityConditionClause Not(IIndex index)
    {
        ValidateIndex(index);
        return new LibraDexIdentityConditionClause(this, index, negate: true);
    }

    /// <summary>
    /// Adds a parenthesized condition group as the next node.<br/>
    /// </summary>
    /// <param name="configure">Callback that builds the grouped condition.</param>
    /// <returns>A continuation for adding more conditions or ending the builder.</returns>
    public LibraDexIdentityConditionContinuation Grouped(Action<LibraDexIdentityConditionBuilder> configure)
    {
        return AddGrouped(configure, negate: false);
    }

    /// <summary>
    /// Adds a negated parenthesized condition group as the next node.<br/>
    /// This preserves explicit precedence for generated condition trees that need `Not(...)` around a whole grouped branch rather than around a single leaf.<br/>
    /// </summary>
    /// <param name="configure">Callback that builds the grouped condition.</param>
    /// <returns>A continuation for adding more conditions or ending the builder.</returns>
    public LibraDexIdentityConditionContinuation NotGrouped(Action<LibraDexIdentityConditionBuilder> configure)
    {
        return AddGrouped(configure, negate: true);
    }

    internal LibraDexIdentityConditionContinuation AddLeaf(
        IIndex index,
        LibraDexCriteriaKind criteriaKind,
        bool negate,
        params LibraDexConditionOperand[] operands)
    {
        ValidateIndex(index);
        LibraDexIdentityConditionNode leaf = LibraDexIdentityConditionNode.Leaf(index, criteriaKind, operands);
        return AddNode(negate ? LibraDexIdentityConditionNode.Not(leaf) : leaf);
    }

    internal LibraDexIdentityConditionContinuation SetNextOperation(LibraDexIdentityCriterionNodeKind operation)
    {
        if (current is null)
        {
            throw new InvalidOperationException("A LibraDex condition cannot start with a composition operator.");
        }

        pendingOperation = operation;
        return new LibraDexIdentityConditionContinuation(this);
    }

    internal LibraDexIdentityCondition End()
    {
        return new LibraDexIdentityCondition(Group, RequireCurrent());
    }

    private LibraDexIdentityConditionContinuation AddNode(LibraDexIdentityConditionNode node)
    {
        if (current is null)
        {
            current = node;
        }
        else
        {
            LibraDexIdentityCriterionNodeKind operation = pendingOperation
                ?? throw new InvalidOperationException("A LibraDex condition requires And, Or, or Except between condition clauses.");
            current = LibraDexIdentityConditionNode.Compose(operation, current, node);
            pendingOperation = null;
        }

        return new LibraDexIdentityConditionContinuation(this);
    }

    private LibraDexIdentityConditionContinuation AddGrouped(Action<LibraDexIdentityConditionBuilder> configure, bool negate)
    {
        ArgumentNullException.ThrowIfNull(configure);
        LibraDexIdentityConditionBuilder builder = new(Group);
        configure(builder);
        LibraDexIdentityConditionNode groupNode = builder.RequireCurrent();
        return AddNode(negate ? LibraDexIdentityConditionNode.Not(groupNode) : groupNode);
    }

    private LibraDexIdentityConditionNode RequireCurrent()
    {
        if (current is null)
        {
            throw new InvalidOperationException("A LibraDex condition must contain at least one clause.");
        }

        if (pendingOperation is not null)
        {
            throw new InvalidOperationException("A LibraDex condition cannot end with a composition operator.");
        }

        return current;
    }

    private void ValidateIndex(IIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (!string.Equals(index.Group, Group, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A LibraDex condition can only use indexes from its identity group.");
        }
    }
}

/// <summary>
/// Builds one index-backed leaf condition.<br/>
/// </summary>
public sealed class LibraDexIdentityConditionClause
{
    private readonly LibraDexIdentityConditionBuilder builder;
    private readonly IIndex index;
    private readonly bool negate;
    private readonly LibraDexIdentityNegatedConditionClause? not;

    internal LibraDexIdentityConditionClause(LibraDexIdentityConditionBuilder builder, IIndex index, bool negate)
    {
        this.builder = builder;
        this.index = index;
        this.negate = negate;
        if (!negate)
        {
            not = new LibraDexIdentityNegatedConditionClause(this);
        }
    }

    /// <summary>
    /// Gets a negated view over this leaf clause.<br/>
    /// For example, `Where(age).Not.Between(10, 20)` materializes to a `Not(Between(...))` criterion node rather than an ambiguous enum parameter on `Between`.<br/>
    /// </summary>
    public LibraDexIdentityNegatedConditionClause Not => not ?? throw new InvalidOperationException("This condition clause is already negated.");

    internal LibraDexIdentityConditionClause CreateNegated()
    {
        return new LibraDexIdentityConditionClause(builder, index, negate: true);
    }

    /// <summary>
    /// Adds an all-identities criterion for this index.<br/>
    /// </summary>
    /// <returns>A condition continuation.</returns>
    public LibraDexIdentityConditionContinuation All()
    {
        return builder.AddLeaf(index, LibraDexCriteriaKind.All, negate);
    }

    /// <summary>
    /// Adds an exact-key criterion.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation Find(object key, string? name = null)
    {
        return Find(LibraDexConditionOperand.Value(key, name));
    }

    /// <summary>
    /// Adds a late-bound exact-key criterion.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation Find(Func<object?> key, string? name = null)
    {
        return Find(LibraDexConditionOperand.Deferred(key, name));
    }

    /// <summary>
    /// Adds an exact-key criterion from a prepared operand.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation Find(LibraDexConditionOperand key)
    {
        return builder.AddLeaf(index, LibraDexCriteriaKind.Find, negate, key);
    }

    /// <summary>
    /// Adds an inclusive range criterion.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation Between(object lowerKey, object upperKey, string? lowerName = null, string? upperName = null)
    {
        return Between(LibraDexConditionOperand.Value(lowerKey, lowerName), LibraDexConditionOperand.Value(upperKey, upperName));
    }

    /// <summary>
    /// Adds an inclusive range criterion from prepared operands.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation Between(LibraDexConditionOperand lowerKey, LibraDexConditionOperand upperKey)
    {
        return builder.AddLeaf(index, LibraDexCriteriaKind.Between, negate, lowerKey, upperKey);
    }

    /// <summary>
    /// Adds an exclusive upper-bound criterion.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation Before(object key, string? name = null)
    {
        return Before(LibraDexConditionOperand.Value(key, name));
    }

    /// <summary>
    /// Adds an exclusive upper-bound criterion from a prepared operand.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation Before(LibraDexConditionOperand key)
    {
        return builder.AddLeaf(index, LibraDexCriteriaKind.Before, negate, key);
    }

    /// <summary>
    /// Adds an inclusive upper-bound criterion.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation AtOrBefore(object key, string? name = null)
    {
        return AtOrBefore(LibraDexConditionOperand.Value(key, name));
    }

    /// <summary>
    /// Adds an inclusive upper-bound criterion from a prepared operand.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation AtOrBefore(LibraDexConditionOperand key)
    {
        return builder.AddLeaf(index, LibraDexCriteriaKind.AtOrBefore, negate, key);
    }

    /// <summary>
    /// Adds an exclusive lower-bound criterion.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation After(object key, string? name = null)
    {
        return After(LibraDexConditionOperand.Value(key, name));
    }

    /// <summary>
    /// Adds an exclusive lower-bound criterion from a prepared operand.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation After(LibraDexConditionOperand key)
    {
        return builder.AddLeaf(index, LibraDexCriteriaKind.After, negate, key);
    }

    /// <summary>
    /// Adds an inclusive lower-bound criterion.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation AtOrAfter(object key, string? name = null)
    {
        return AtOrAfter(LibraDexConditionOperand.Value(key, name));
    }

    /// <summary>
    /// Adds an inclusive lower-bound criterion from a prepared operand.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation AtOrAfter(LibraDexConditionOperand key)
    {
        return builder.AddLeaf(index, LibraDexCriteriaKind.AtOrAfter, negate, key);
    }

    /// <summary>
    /// Adds a prefix criterion.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation Prefix(object prefix, string? name = null)
    {
        return Prefix(LibraDexConditionOperand.Value(prefix, name));
    }

    /// <summary>
    /// Adds a prefix criterion from a prepared operand.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation Prefix(LibraDexConditionOperand prefix)
    {
        return builder.AddLeaf(index, LibraDexCriteriaKind.Prefix, negate, prefix);
    }

    /// <summary>
    /// Adds a suffix criterion.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation Suffix(object suffix, string? name = null)
    {
        return Suffix(LibraDexConditionOperand.Value(suffix, name));
    }

    /// <summary>
    /// Adds a suffix criterion from a prepared operand.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation Suffix(LibraDexConditionOperand suffix)
    {
        return builder.AddLeaf(index, LibraDexCriteriaKind.Suffix, negate, suffix);
    }

    /// <summary>
    /// Adds a contains criterion.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation Contains(object value, string? name = null)
    {
        return Contains(LibraDexConditionOperand.Value(value, name));
    }

    /// <summary>
    /// Adds a contains criterion from a prepared operand.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation Contains(LibraDexConditionOperand value)
    {
        return builder.AddLeaf(index, LibraDexCriteriaKind.Contains, negate, value);
    }

    /// <summary>
    /// Adds a membership criterion.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation In(IEnumerable<object> keys, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return In(LibraDexConditionOperand.Value(keys.ToArray(), name));
    }

    /// <summary>
    /// Adds a membership criterion from a prepared operand.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation In(LibraDexConditionOperand keys)
    {
        return builder.AddLeaf(index, LibraDexCriteriaKind.In, negate, keys);
    }

    /// <summary>
    /// Adds a prepared-set membership criterion.<br/>
    /// The descriptor preserves InSet as a distinct lookup intent so a later planner can reuse encoded membership state instead of treating it as ordinary enumerable membership.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation InSet(IEnumerable<object> keys, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return InSet(LibraDexConditionOperand.Value(keys.ToArray(), name));
    }

    /// <summary>
    /// Adds a prepared-set membership criterion from a strict non-generic prepared set.<br/>
    /// This lets generated callers prepare key membership once through an index handle and pass that prepared intent into a reusable condition without flattening it back to ordinary enumerable membership.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation InSet(LibraDexPreparedObjectSet set, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(set);
        return InSet(LibraDexConditionOperand.Value(set, name));
    }

    /// <summary>
    /// Adds a prepared-set membership criterion from a prepared operand.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation InSet(LibraDexConditionOperand keys)
    {
        return builder.AddLeaf(index, LibraDexCriteriaKind.InSet, negate, keys);
    }

    /// <summary>
    /// Adds a pattern criterion.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation Matches(object pattern, string? name = null)
    {
        return Matches(LibraDexConditionOperand.Value(pattern, name));
    }

    /// <summary>
    /// Adds a pattern criterion from a prepared operand.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation Matches(LibraDexConditionOperand pattern)
    {
        return builder.AddLeaf(index, LibraDexCriteriaKind.Matches, negate, pattern);
    }
}

/// <summary>
/// Provides negated leaf methods for a condition clause.<br/>
/// This avoids boolean or enum negation parameters on every lookup method while keeping negation visible at the call site.<br/>
/// </summary>
public sealed class LibraDexIdentityNegatedConditionClause
{
    private readonly LibraDexIdentityConditionClause clause;

    internal LibraDexIdentityNegatedConditionClause(LibraDexIdentityConditionClause clause)
    {
        this.clause = clause.CreateNegated();
    }

    /// <summary>
    /// Adds a negated all-identities criterion.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation All() => clause.All();

    /// <summary>
    /// Adds a negated exact-key criterion.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation Find(object key, string? name = null) => clause.Find(key, name);

    /// <summary>
    /// Adds a negated late-bound exact-key criterion.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation Find(Func<object?> key, string? name = null) => clause.Find(key, name);

    /// <summary>
    /// Adds a negated exact-key criterion from a prepared operand.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation Find(LibraDexConditionOperand key) => clause.Find(key);

    /// <summary>
    /// Adds a negated inclusive range criterion.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation Between(object lowerKey, object upperKey, string? lowerName = null, string? upperName = null) => clause.Between(lowerKey, upperKey, lowerName, upperName);

    /// <summary>
    /// Adds a negated inclusive range criterion from prepared operands.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation Between(LibraDexConditionOperand lowerKey, LibraDexConditionOperand upperKey) => clause.Between(lowerKey, upperKey);

    /// <summary>
    /// Adds a negated exclusive upper-bound criterion.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation Before(object key, string? name = null) => clause.Before(key, name);

    /// <summary>
    /// Adds a negated exclusive upper-bound criterion from a prepared operand.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation Before(LibraDexConditionOperand key) => clause.Before(key);

    /// <summary>
    /// Adds a negated inclusive upper-bound criterion.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation AtOrBefore(object key, string? name = null) => clause.AtOrBefore(key, name);

    /// <summary>
    /// Adds a negated inclusive upper-bound criterion from a prepared operand.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation AtOrBefore(LibraDexConditionOperand key) => clause.AtOrBefore(key);

    /// <summary>
    /// Adds a negated exclusive lower-bound criterion.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation After(object key, string? name = null) => clause.After(key, name);

    /// <summary>
    /// Adds a negated exclusive lower-bound criterion from a prepared operand.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation After(LibraDexConditionOperand key) => clause.After(key);

    /// <summary>
    /// Adds a negated inclusive lower-bound criterion.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation AtOrAfter(object key, string? name = null) => clause.AtOrAfter(key, name);

    /// <summary>
    /// Adds a negated inclusive lower-bound criterion from a prepared operand.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation AtOrAfter(LibraDexConditionOperand key) => clause.AtOrAfter(key);

    /// <summary>
    /// Adds a negated prefix criterion.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation Prefix(object prefix, string? name = null) => clause.Prefix(prefix, name);

    /// <summary>
    /// Adds a negated prefix criterion from a prepared operand.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation Prefix(LibraDexConditionOperand prefix) => clause.Prefix(prefix);

    /// <summary>
    /// Adds a negated suffix criterion.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation Suffix(object suffix, string? name = null) => clause.Suffix(suffix, name);

    /// <summary>
    /// Adds a negated suffix criterion from a prepared operand.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation Suffix(LibraDexConditionOperand suffix) => clause.Suffix(suffix);

    /// <summary>
    /// Adds a negated contains criterion.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation Contains(object value, string? name = null) => clause.Contains(value, name);

    /// <summary>
    /// Adds a negated contains criterion from a prepared operand.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation Contains(LibraDexConditionOperand value) => clause.Contains(value);

    /// <summary>
    /// Adds a negated membership criterion.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation In(IEnumerable<object> keys, string? name = null) => clause.In(keys, name);

    /// <summary>
    /// Adds a negated membership criterion from a prepared operand.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation In(LibraDexConditionOperand keys) => clause.In(keys);

    /// <summary>
    /// Adds a negated prepared-set membership criterion.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation InSet(IEnumerable<object> keys, string? name = null) => clause.InSet(keys, name);

    /// <summary>
    /// Adds a negated prepared-set membership criterion from a strict non-generic prepared set.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation InSet(LibraDexPreparedObjectSet set, string? name = null) => clause.InSet(set, name);

    /// <summary>
    /// Adds a negated prepared-set membership criterion from a prepared operand.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation InSet(LibraDexConditionOperand keys) => clause.InSet(keys);

    /// <summary>
    /// Adds a negated pattern criterion.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation Matches(object pattern, string? name = null) => clause.Matches(pattern, name);

    /// <summary>
    /// Adds a negated pattern criterion from a prepared operand.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation Matches(LibraDexConditionOperand pattern) => clause.Matches(pattern);
}

/// <summary>
/// Continues or ends a grouped identity condition.<br/>
/// </summary>
public sealed class LibraDexIdentityConditionContinuation
{
    private readonly LibraDexIdentityConditionBuilder builder;

    internal LibraDexIdentityConditionContinuation(LibraDexIdentityConditionBuilder builder)
    {
        this.builder = builder;
    }

    /// <summary>
    /// Adds an intersection operator and starts the next index-backed clause.<br/>
    /// </summary>
    public LibraDexIdentityConditionClause And(IIndex index)
    {
        _ = builder.SetNextOperation(LibraDexIdentityCriterionNodeKind.And);
        return builder.Where(index);
    }

    /// <summary>
    /// Adds an intersection operator and starts the next negated index-backed clause.<br/>
    /// </summary>
    public LibraDexIdentityConditionClause AndNot(IIndex index)
    {
        _ = builder.SetNextOperation(LibraDexIdentityCriterionNodeKind.And);
        return builder.Not(index);
    }

    /// <summary>
    /// Adds a union operator and starts the next index-backed clause.<br/>
    /// </summary>
    public LibraDexIdentityConditionClause Or(IIndex index)
    {
        _ = builder.SetNextOperation(LibraDexIdentityCriterionNodeKind.Or);
        return builder.Where(index);
    }

    /// <summary>
    /// Adds a union operator and starts the next negated index-backed clause.<br/>
    /// </summary>
    public LibraDexIdentityConditionClause OrNot(IIndex index)
    {
        _ = builder.SetNextOperation(LibraDexIdentityCriterionNodeKind.Or);
        return builder.Not(index);
    }

    /// <summary>
    /// Adds an exclusion operator and starts the next index-backed clause.<br/>
    /// </summary>
    public LibraDexIdentityConditionClause Except(IIndex index)
    {
        _ = builder.SetNextOperation(LibraDexIdentityCriterionNodeKind.Except);
        return builder.Where(index);
    }

    /// <summary>
    /// Adds an exclusion operator and starts the next negated index-backed clause.<br/>
    /// </summary>
    public LibraDexIdentityConditionClause ExceptNot(IIndex index)
    {
        _ = builder.SetNextOperation(LibraDexIdentityCriterionNodeKind.Except);
        return builder.Not(index);
    }

    /// <summary>
    /// Adds an intersection operator followed by a grouped condition.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation AndGroup(Action<LibraDexIdentityConditionBuilder> configure)
    {
        _ = builder.SetNextOperation(LibraDexIdentityCriterionNodeKind.And);
        return builder.Grouped(configure);
    }

    /// <summary>
    /// Adds a union operator followed by a grouped condition.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation OrGroup(Action<LibraDexIdentityConditionBuilder> configure)
    {
        _ = builder.SetNextOperation(LibraDexIdentityCriterionNodeKind.Or);
        return builder.Grouped(configure);
    }

    /// <summary>
    /// Adds an exclusion operator followed by a grouped condition.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation ExceptGroup(Action<LibraDexIdentityConditionBuilder> configure)
    {
        _ = builder.SetNextOperation(LibraDexIdentityCriterionNodeKind.Except);
        return builder.Grouped(configure);
    }

    /// <summary>
    /// Adds an intersection operator followed by a negated grouped condition.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation AndNotGroup(Action<LibraDexIdentityConditionBuilder> configure)
    {
        _ = builder.SetNextOperation(LibraDexIdentityCriterionNodeKind.And);
        return builder.NotGrouped(configure);
    }

    /// <summary>
    /// Adds a union operator followed by a negated grouped condition.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation OrNotGroup(Action<LibraDexIdentityConditionBuilder> configure)
    {
        _ = builder.SetNextOperation(LibraDexIdentityCriterionNodeKind.Or);
        return builder.NotGrouped(configure);
    }

    /// <summary>
    /// Adds an exclusion operator followed by a negated grouped condition.<br/>
    /// </summary>
    public LibraDexIdentityConditionContinuation ExceptNotGroup(Action<LibraDexIdentityConditionBuilder> configure)
    {
        _ = builder.SetNextOperation(LibraDexIdentityCriterionNodeKind.Except);
        return builder.NotGrouped(configure);
    }

    /// <summary>
    /// Ends the condition and returns a reusable descriptor.<br/>
    /// </summary>
    public LibraDexIdentityCondition End()
    {
        return builder.End();
    }
}

internal sealed class LibraDexIdentityConditionNode
{
    private readonly IIndex? index;
    private readonly LibraDexCriteriaKind? criteriaKind;
    private readonly IReadOnlyList<LibraDexConditionOperand> operands;
    private readonly LibraDexIdentityConditionNode? left;
    private readonly LibraDexIdentityConditionNode? right;

    private LibraDexIdentityConditionNode(
        LibraDexIdentityCriterionNodeKind nodeKind,
        IIndex? index,
        LibraDexCriteriaKind? criteriaKind,
        IReadOnlyList<LibraDexConditionOperand> operands,
        LibraDexIdentityConditionNode? left,
        LibraDexIdentityConditionNode? right)
    {
        NodeKind = nodeKind;
        this.index = index;
        this.criteriaKind = criteriaKind;
        this.operands = operands;
        this.left = left;
        this.right = right;
        HasDeferredOperands = ComputeHasDeferredOperands(operands, left, right);
    }

    internal LibraDexIdentityCriterionNodeKind NodeKind { get; }

    internal bool HasDeferredOperands { get; }

    internal static LibraDexIdentityConditionNode Leaf(
        IIndex index,
        LibraDexCriteriaKind criteriaKind,
        IReadOnlyList<LibraDexConditionOperand> operands)
    {
        return new LibraDexIdentityConditionNode(
            LibraDexIdentityCriterionNodeKind.Leaf,
            index,
            criteriaKind,
            operands,
            left: null,
            right: null);
    }

    internal static LibraDexIdentityConditionNode Compose(
        LibraDexIdentityCriterionNodeKind operation,
        LibraDexIdentityConditionNode left,
        LibraDexIdentityConditionNode right)
    {
        if (operation != LibraDexIdentityCriterionNodeKind.And &&
            operation != LibraDexIdentityCriterionNodeKind.Or &&
            operation != LibraDexIdentityCriterionNodeKind.Except)
        {
            throw new ArgumentOutOfRangeException(nameof(operation), operation, "Condition composition requires And, Or, or Except.");
        }

        return new LibraDexIdentityConditionNode(
            operation,
            index: null,
            criteriaKind: null,
            Array.Empty<LibraDexConditionOperand>(),
            left,
            right);
    }

    internal static LibraDexIdentityConditionNode Not(LibraDexIdentityConditionNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return new LibraDexIdentityConditionNode(
            LibraDexIdentityCriterionNodeKind.Not,
            index: null,
            criteriaKind: null,
            Array.Empty<LibraDexConditionOperand>(),
            node,
            right: null);
    }

    internal IIdentityCriterion Materialize()
    {
        return NodeKind switch
        {
            LibraDexIdentityCriterionNodeKind.Leaf => MaterializeLeaf(),
            LibraDexIdentityCriterionNodeKind.And => RequireLeft().Materialize().And(RequireRight().Materialize()),
            LibraDexIdentityCriterionNodeKind.Or => RequireLeft().Materialize().Or(RequireRight().Materialize()),
            LibraDexIdentityCriterionNodeKind.Except => RequireLeft().Materialize().Except(RequireRight().Materialize()),
            LibraDexIdentityCriterionNodeKind.Not => RequireLeft().Materialize().Not(),
            _ => throw new InvalidOperationException("Unsupported LibraDex condition node.")
        };
    }

    internal LibraDexIdentityConditionNode Replace(string name, LibraDexConditionOperand operand)
    {
        if (NodeKind == LibraDexIdentityCriterionNodeKind.Leaf)
        {
            LibraDexConditionOperand[] replaced = new LibraDexConditionOperand[operands.Count];
            for (int i = 0; i < operands.Count; i++)
            {
                replaced[i] = operands[i].Replace(name, operand);
            }

            return new LibraDexIdentityConditionNode(NodeKind, index, criteriaKind, replaced, left: null, right: null);
        }

        return new LibraDexIdentityConditionNode(
            NodeKind,
            index: null,
            criteriaKind: null,
            Array.Empty<LibraDexConditionOperand>(),
            left?.Replace(name, operand),
            right?.Replace(name, operand));
    }

    internal IReadOnlyList<string> GetParameterNames()
    {
        List<string> names = new();
        AddParameterNames(names);
        return names;
    }

    private IIdentityCriterion MaterializeLeaf()
    {
        IIndex leafIndex = index ?? throw new InvalidOperationException("Leaf condition is missing its index.");
        LibraDexCriteriaKind kind = criteriaKind ?? throw new InvalidOperationException("Leaf condition is missing its criteria kind.");
        object?[] values = MaterializeOperands();
        return kind switch
        {
            LibraDexCriteriaKind.All => leafIndex.Criteria.All(),
            LibraDexCriteriaKind.Find => leafIndex.Criteria.Find(RequireValue(values, 0)),
            LibraDexCriteriaKind.Between => leafIndex.Criteria.Between(RequireValue(values, 0), RequireValue(values, 1)),
            LibraDexCriteriaKind.Before => leafIndex.Criteria.Before(RequireValue(values, 0)),
            LibraDexCriteriaKind.AtOrBefore => leafIndex.Criteria.AtOrBefore(RequireValue(values, 0)),
            LibraDexCriteriaKind.After => leafIndex.Criteria.After(RequireValue(values, 0)),
            LibraDexCriteriaKind.AtOrAfter => leafIndex.Criteria.AtOrAfter(RequireValue(values, 0)),
            LibraDexCriteriaKind.Prefix => leafIndex.Criteria.Prefix(RequireValue(values, 0)),
            LibraDexCriteriaKind.Suffix => leafIndex.Criteria.Suffix(RequireValue(values, 0)),
            LibraDexCriteriaKind.Contains => leafIndex.Criteria.Contains(RequireValue(values, 0)),
            LibraDexCriteriaKind.In => leafIndex.Criteria.In(ToObjectEnumerable(RequireValue(values, 0))),
            LibraDexCriteriaKind.InSet when RequireValue(values, 0) is LibraDexPreparedObjectSet prepared => leafIndex.Criteria.InSet(prepared),
            LibraDexCriteriaKind.InSet => leafIndex.Criteria.InSet(ToObjectEnumerable(RequireValue(values, 0))),
            LibraDexCriteriaKind.Matches => leafIndex.Criteria.Matches(RequireValue(values, 0)),
            _ => throw new NotSupportedException($"{kind} cannot be materialized by the condition builder yet.")
        };
    }

    private void AddParameterNames(List<string> names)
    {
        for (int i = 0; i < operands.Count; i++)
        {
            string? name = operands[i].Name;
            if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name, StringComparer.Ordinal))
            {
                names.Add(name);
            }
        }

        left?.AddParameterNames(names);
        right?.AddParameterNames(names);
    }

    private object?[] MaterializeOperands()
    {
        object?[] values = new object?[operands.Count];
        for (int i = 0; i < operands.Count; i++)
        {
            values[i] = operands[i].GetValue();
        }

        return values;
    }

    private static bool ComputeHasDeferredOperands(
        IReadOnlyList<LibraDexConditionOperand> operands,
        LibraDexIdentityConditionNode? left,
        LibraDexIdentityConditionNode? right)
    {
        for (int i = 0; i < operands.Count; i++)
        {
            if (operands[i].IsDeferred)
            {
                return true;
            }
        }

        return left?.HasDeferredOperands == true || right?.HasDeferredOperands == true;
    }

    private LibraDexIdentityConditionNode RequireLeft()
    {
        return left ?? throw new InvalidOperationException("Composite condition is missing its left child.");
    }

    private LibraDexIdentityConditionNode RequireRight()
    {
        return right ?? throw new InvalidOperationException("Composite condition is missing its right child.");
    }

    private static object RequireValue(IReadOnlyList<object?> values, int index)
    {
        if (index >= values.Count || values[index] is null)
        {
            throw new InvalidOperationException("The LibraDex condition operand did not materialize a value.");
        }

        return values[index]!;
    }

    private static IEnumerable<object> ToObjectEnumerable(object value)
    {
        if (value is IEnumerable<object> objectValues)
        {
            return objectValues;
        }

        if (value is IEnumerable enumerable)
        {
            return enumerable.Cast<object>();
        }

        throw new InvalidOperationException("The LibraDex In condition operand must materialize an enumerable value.");
    }
}
