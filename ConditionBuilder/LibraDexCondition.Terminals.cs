using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace LibraDex;

public sealed class LibraDexConditionEndCondition
{
    private readonly LibraDexConditionNode root;

    internal LibraDexConditionEndCondition(string group, LibraDexConditionNode root)
    {
        Group = group;
        this.root = root;
    }

    /// <summary>
    /// Gets the identity group shared by all indexes referenced by this condition.<br/>
    /// The materializer verifies that resolved indexes remain in this group so reusable descriptors cannot accidentally cross identity universes.<br/>
    /// </summary>
    public string Group { get; }

    /// <summary>
    /// Gets the leaf descriptors in the order they were captured by the builder.<br/>
    /// This is intended for permutation tests, bridge diagnostics, and adapter review before the condition is resolved to physical index handles.<br/>
    /// </summary>
    public IReadOnlyList<LibraDexConditionLeafDescriptor> Leaves => root.GetLeaves();

    /// <summary>
    /// Materializes this adopted condition into the current LibraDex identity-criterion tree.<br/>
    /// The resolver receives each index name captured by the builder and must return the corresponding opened index in this condition's identity group.<br/>
    /// </summary>
    /// <param name="resolveIndex">Function that resolves a condition index name to an opened LibraDex index.</param>
    /// <returns>An identity criterion tree executable by the existing LibraDex criteria projection layer.</returns>
    public IIdentityCriterion Materialize(Func<string, IIndex> resolveIndex)
    {
        ArgumentNullException.ThrowIfNull(resolveIndex);
        return root.Materialize(Group, resolveIndex, resolveProjectionIndex: null);
    }

    /// <summary>
    /// Returns this completed condition wrapped as an explicit condition group.<br/>
    /// This is useful when a completed reusable condition is intentionally composed into a larger condition while preserving parenthesized precedence.<br/>
    /// </summary>
    /// <returns>A completed condition whose root is an explicit group around this condition.</returns>
    public LibraDexConditionEndCondition Grouped() => LibraDexCondition.ForGroup(Group).Group(this).EndCondition;

    /// <summary>
    /// Wraps a completed condition as an explicit condition group.<br/>
    /// </summary>
    /// <param name="condition">The completed condition to group.</param>
    /// <returns>A completed condition whose root is an explicit group around <paramref name="condition"/>.</returns>
    public static LibraDexConditionEndCondition Grouped(LibraDexConditionEndCondition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        return condition.Grouped();
    }

    /// <summary>
    /// Composes this completed condition with another completed condition from the same identity group using identity-set intersection.<br/>
    /// Prefer fluent continuation before `.EndCondition` for handwritten conditions; this method exists for reusable completed fragments.<br/>
    /// </summary>
    /// <param name="other">The completed condition to intersect with this condition.</param>
    /// <returns>A composed completed condition.</returns>
    public LibraDexConditionEndCondition AndAlso(LibraDexConditionEndCondition other) => Compose(other, useOr: false);

    /// <summary>
    /// Composes this completed condition with a lazily supplied completed condition from the same identity group using identity-set intersection.<br/>
    /// </summary>
    /// <param name="otherFactory">Factory that supplies the completed condition to intersect with this condition.</param>
    /// <returns>A composed completed condition.</returns>
    public LibraDexConditionEndCondition AndAlso(Func<LibraDexConditionEndCondition> otherFactory)
    {
        ArgumentNullException.ThrowIfNull(otherFactory);
        return AndAlso(otherFactory());
    }

    /// <summary>
    /// Composes this completed condition with another completed condition from the same identity group using identity-set union.<br/>
    /// Prefer fluent continuation before `.EndCondition` for handwritten conditions; this method exists for reusable completed fragments.<br/>
    /// </summary>
    /// <param name="other">The completed condition to union with this condition.</param>
    /// <returns>A composed completed condition.</returns>
    public LibraDexConditionEndCondition OrElse(LibraDexConditionEndCondition other) => Compose(other, useOr: true);

    /// <summary>
    /// Composes this completed condition with a lazily supplied completed condition from the same identity group using identity-set union.<br/>
    /// </summary>
    /// <param name="otherFactory">Factory that supplies the completed condition to union with this condition.</param>
    /// <returns>A composed completed condition.</returns>
    public LibraDexConditionEndCondition OrElse(Func<LibraDexConditionEndCondition> otherFactory)
    {
        ArgumentNullException.ThrowIfNull(otherFactory);
        return OrElse(otherFactory());
    }

    /// <summary>
    /// Replaces every named operand in this completed condition with a static value.<br/>
    /// The original condition is not modified, so reusable condition descriptors can be assigned once and rebound for later operations.<br/>
    /// </summary>
    /// <param name="name">The operand name to replace.</param>
    /// <param name="value">The static replacement value.</param>
    /// <returns>A new completed condition with matching operands replaced.</returns>
    public LibraDexConditionEndCondition WithValue(string name, object? value) => RewriteOperands(name, LibraDexConditionOperand.Value(value, name));

    /// <summary>
    /// Replaces every named operand in this completed condition with a deferred value factory.<br/>
    /// The factory is evaluated during materialization so a reusable condition can bind to current request state without rebuilding the chain.<br/>
    /// </summary>
    /// <param name="name">The operand name to replace.</param>
    /// <param name="valueFactory">Factory that returns the current operand value.</param>
    /// <returns>A new completed condition with matching operands replaced.</returns>
    public LibraDexConditionEndCondition WithDeferredValue(string name, Func<object?> valueFactory)
        => RewriteOperands(name, LibraDexConditionOperand.Deferred(valueFactory, name));

    /// <summary>
    /// Replaces every named index selector in this completed condition with a static index name.<br/>
    /// This supports Abraxas-style proppath aliasing in a LibraDex-shaped form where aliases bind to index names inside the condition's identity group.<br/>
    /// </summary>
    /// <param name="name">The selector name to replace.</param>
    /// <param name="indexName">The replacement index name inside this condition's identity group.</param>
    /// <returns>A new completed condition with matching selectors replaced.</returns>
    public LibraDexConditionEndCondition WithIndex(string name, string indexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Rewrite(leaf =>
            string.Equals(leaf.IndexSelector.Name, name, StringComparison.Ordinal)
                ? leaf.WithIndexSelector(LibraDexConditionIndexSelector.Static(indexName, name))
                : leaf);
    }

    /// <summary>
    /// Replaces every named index selector in this completed condition with a deferred index-name factory.<br/>
    /// The factory is evaluated only when the condition is inspected or materialized, allowing one reusable condition to target different aligned indexes over time.<br/>
    /// </summary>
    /// <param name="name">The selector name to replace.</param>
    /// <param name="indexNameFactory">Factory that returns the replacement index name inside this condition's identity group.</param>
    /// <returns>A new completed condition with matching selectors replaced.</returns>
    public LibraDexConditionEndCondition WithDeferredIndex(string name, Func<string> indexNameFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Rewrite(leaf =>
            string.Equals(leaf.IndexSelector.Name, name, StringComparison.Ordinal)
                ? leaf.WithIndexSelector(LibraDexConditionIndexSelector.Deferred(indexNameFactory, name))
                : leaf);
    }

    /// <summary>
    /// Materializes this adopted condition with an explicit maintained-projection resolver.<br/>
    /// The normal index resolver supplies the logical indexes referenced by the condition, while the projection resolver may supply a physical projection index for leaves classified as projection-backed.<br/>
    /// This keeps projection execution explicit: LibraDex can use a maintained projection when the caller provides one, but it does not silently scan or invent projection storage.<br/>
    /// </summary>
    /// <param name="resolveIndex">Function that resolves a condition index name to the logical opened LibraDex index.</param>
    /// <param name="resolveProjectionIndex">Function that resolves a projection-backed leaf to a maintained projection index, or null when no physical projection is available.</param>
    /// <returns>An identity criterion tree executable by the existing LibraDex criteria projection layer.</returns>
    public IIdentityCriterion MaterializeWithProjectionBridge(
        Func<string, IIndex> resolveIndex,
        Func<LibraDexConditionLeafDescriptor, LibraDexConditionLeafClassification, IIndex?> resolveProjectionIndex)
    {
        ArgumentNullException.ThrowIfNull(resolveIndex);
        ArgumentNullException.ThrowIfNull(resolveProjectionIndex);
        return root.Materialize(Group, resolveIndex, resolveProjectionIndex);
    }

    /// <summary>
    /// Materializes this adopted condition using a dictionary of opened indexes keyed by index name.<br/>
    /// This overload is useful for generated callers and tests that already have the identity-group index handles in a simple lookup table.<br/>
    /// </summary>
    /// <param name="indexes">The opened indexes keyed by LibraDex index name.</param>
    /// <returns>An identity criterion tree executable by the existing LibraDex criteria projection layer.</returns>
    public IIdentityCriterion Materialize(IReadOnlyDictionary<string, IIndex> indexes) => Materialize(CreateRequiredIndexResolver(indexes));

    /// <summary>
    /// Builds an identity projection for this condition after resolving its index names.<br/>
    /// This is a low-friction bridge for the first adopted-builder slice; later slices can add richer terminal helpers without changing the descriptor grammar.<br/>
    /// </summary>
    /// <param name="resolveIndex">Function that resolves a condition index name to an opened LibraDex index.</param>
    /// <returns>An identity projection descriptor.</returns>
    public IIdentityCriterionProjection IDs(Func<string, IIndex> resolveIndex) => Materialize(resolveIndex).IDs;

    /// <summary>
    /// Builds an identity projection for this condition with explicit result-shaping options.<br/>
    /// This keeps adopted-builder callers on the completed condition descriptor when they need ordering, duplicate handling, paging, or bookmark continuation.<br/>
    /// The supplied resolver is evaluated during materialization so reusable condition descriptors can be bound to different opened catalog handles without rebuilding the builder chain.<br/>
    /// </summary>
    /// <param name="resolveIndex">Function that resolves a condition index name to an opened LibraDex index.</param>
    /// <param name="ordering">The requested identity ordering contract.</param>
    /// <param name="deduplication">The requested duplicate identity policy.</param>
    /// <param name="skip">The number of matching identities to skip.</param>
    /// <param name="take">The optional maximum number of identities to return.</param>
    /// <param name="bookmark">The optional continuation bookmark.</param>
    /// <returns>An identity projection descriptor over the materialized adopted condition.</returns>
    public IIdentityCriterionProjection IDsWith(
        Func<string, IIndex> resolveIndex,
        IdentityResultOrdering ordering = IdentityResultOrdering.PlanNatural,
        IdentityDeduplication deduplication = IdentityDeduplication.Distinct,
        int skip = 0,
        int? take = null,
        LibraDexBookmark? bookmark = null) => Materialize(resolveIndex).IDsWith(ordering, deduplication, skip, take, bookmark);

    /// <summary>
    /// Builds an execution-plan descriptor for this adopted condition without materializing matching identities.<br/>
    /// Planning through the adopted condition descriptor makes condition permutations the public contract while keeping the internal identity-criterion tree as an execution detail.<br/>
    /// </summary>
    /// <param name="resolveIndex">Function that resolves a condition index name to an opened LibraDex index.</param>
    /// <param name="ordering">The requested identity ordering contract.</param>
    /// <param name="deduplication">The requested duplicate identity policy.</param>
    /// <param name="skip">The number of matching identities to skip.</param>
    /// <param name="take">The optional maximum number of identities to return.</param>
    /// <param name="bookmark">The optional continuation bookmark.</param>
    /// <returns>An identity execution plan descriptor.</returns>
    public LibraDexIdentityExecutionPlan Plan(
        Func<string, IIndex> resolveIndex,
        IdentityResultOrdering ordering = IdentityResultOrdering.PlanNatural,
        IdentityDeduplication deduplication = IdentityDeduplication.Distinct,
        int skip = 0,
        int? take = null,
        LibraDexBookmark? bookmark = null) => IDsWith(resolveIndex, ordering, deduplication, skip, take, bookmark).Plan();

    /// <summary>
    /// Executes this adopted condition and materializes matching identity objects plus plan diagnostics.<br/>
    /// `Get` is the adopted-builder materialization bridge for callers that need diagnostics and results together without handling the internal criterion tree.<br/>
    /// </summary>
    /// <param name="resolveIndex">Function that resolves a condition index name to an opened LibraDex index.</param>
    /// <param name="ordering">The requested identity ordering contract.</param>
    /// <param name="deduplication">The requested duplicate identity policy.</param>
    /// <param name="skip">The number of matching identities to skip.</param>
    /// <param name="take">The optional maximum number of identities to return.</param>
    /// <param name="bookmark">The optional continuation bookmark.</param>
    /// <returns>The materialized identity execution result.</returns>
    public LibraDexIdentityExecutionResult Get(
        Func<string, IIndex> resolveIndex,
        IdentityResultOrdering ordering = IdentityResultOrdering.PlanNatural,
        IdentityDeduplication deduplication = IdentityDeduplication.Distinct,
        int skip = 0,
        int? take = null,
        LibraDexBookmark? bookmark = null) => IDsWith(resolveIndex, ordering, deduplication, skip, take, bookmark).Execute();

    /// <summary>
    /// Streams matching identity objects for this adopted condition in the selected result shape.<br/>
    /// The current implementation delegates to the internal identity projection iterator; later physical cursors can replace that bridge without changing adopted-builder call sites.<br/>
    /// </summary>
    /// <param name="resolveIndex">Function that resolves a condition index name to an opened LibraDex index.</param>
    /// <param name="ordering">The requested identity ordering contract.</param>
    /// <param name="deduplication">The requested duplicate identity policy.</param>
    /// <param name="skip">The number of matching identities to skip.</param>
    /// <param name="take">The optional maximum number of identities to return.</param>
    /// <param name="bookmark">The optional continuation bookmark.</param>
    /// <returns>A forward-only sequence of matching identity objects.</returns>
    public IEnumerable<object> Iterate(
        Func<string, IIndex> resolveIndex,
        IdentityResultOrdering ordering = IdentityResultOrdering.PlanNatural,
        IdentityDeduplication deduplication = IdentityDeduplication.Distinct,
        int skip = 0,
        int? take = null,
        LibraDexBookmark? bookmark = null) => IDsWith(resolveIndex, ordering, deduplication, skip, take, bookmark).Iterate();

    /// <summary>
    /// Materializes matching identities for this adopted condition as a typed list.<br/>
    /// Runtime identities are validated against <typeparamref name="TIdentity"/> by the underlying projection helper so adapters can fail fast when they bind a condition to the wrong identity type.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The expected identity type.</typeparam>
    /// <param name="resolveIndex">Function that resolves a condition index name to an opened LibraDex index.</param>
    /// <param name="ordering">The requested identity ordering contract.</param>
    /// <param name="deduplication">The requested duplicate identity policy.</param>
    /// <param name="skip">The number of matching identities to skip.</param>
    /// <param name="take">The optional maximum number of identities to return.</param>
    /// <param name="bookmark">The optional continuation bookmark.</param>
    /// <returns>A typed list of matching identities.</returns>
    public IReadOnlyList<TIdentity> ToList<TIdentity>(
        Func<string, IIndex> resolveIndex,
        IdentityResultOrdering ordering = IdentityResultOrdering.PlanNatural,
        IdentityDeduplication deduplication = IdentityDeduplication.Distinct,
        int skip = 0,
        int? take = null,
        LibraDexBookmark? bookmark = null) => IDsWith(resolveIndex, ordering, deduplication, skip, take, bookmark).ToList<TIdentity>();

    /// <summary>
    /// Determines whether this adopted condition has at least one matching identity.<br/>
    /// This terminal shortcut keeps existence checks condition-scoped and lets the primitive executor stop at the first qualifying identity where the current bridge supports it.<br/>
    /// </summary>
    /// <param name="resolveIndex">Function that resolves a condition index name to an opened LibraDex index.</param>
    /// <param name="deduplication">The duplicate identity policy to apply before existence is reported.</param>
    /// <returns><see langword="true"/> when the condition returns at least one identity.</returns>
    public bool Exists(
        Func<string, IIndex> resolveIndex,
        IdentityDeduplication deduplication = IdentityDeduplication.Distinct) => LibraDexIdentityExecutionPlanner.Exists(Materialize(resolveIndex), deduplication);

    /// <summary>
    /// Counts matching identities for this adopted condition.<br/>
    /// Count remains condition-terminal so future shelf/run counters can be introduced beneath the same adopted-builder call shape instead of reviving root index aggregate facades.<br/>
    /// </summary>
    /// <param name="resolveIndex">Function that resolves a condition index name to an opened LibraDex index.</param>
    /// <param name="deduplication">The duplicate identity policy to apply before counting.</param>
    /// <returns>The number of matching identities after the selected duplicate policy is applied.</returns>
    public long Count(
        Func<string, IIndex> resolveIndex,
        IdentityDeduplication deduplication = IdentityDeduplication.Distinct) => LibraDexIdentityExecutionPlanner.Count(Materialize(resolveIndex), deduplication);

    /// <summary>
    /// Deletes tuples matched by this adopted condition through the criteria-scoped mutation bridge.<br/>
    /// This direct terminal is intentionally scoped to a single materialized primitive/composite leaf whose index can delete the exact matched tuples.<br/>
    /// Use <see cref="DeleteFrom(string, Func{string, IIndex})"/> when a composed, inverse, external, or cross-index condition should select identities and mutate one explicit physical target index.<br/>
    /// </summary>
    /// <param name="resolveIndex">Function that resolves a condition index name to an opened LibraDex index.</param>
    /// <returns>A mutation result describing matched and deleted tuple counts.</returns>
    public LibraDexIdentityMutationResult Delete(Func<string, IIndex> resolveIndex) => Materialize(resolveIndex).Mutate.Delete().Execute();

    /// <summary>
    /// Deletes tuples matched by this adopted condition using a dictionary of opened indexes keyed by index name.<br/>
    /// This overload matches the dictionary materialization helper so generated callers can keep retrieval and mutation binding code in the same shape.<br/>
    /// Like the resolver overload, this direct terminal is scoped to a single materialized primitive/composite leaf; composed selectors should use <see cref="DeleteFrom(string, IReadOnlyDictionary{string, IIndex})"/>.<br/>
    /// </summary>
    /// <param name="indexes">The opened indexes keyed by LibraDex index name.</param>
    /// <returns>A mutation result describing matched and deleted tuple counts.</returns>
    public LibraDexIdentityMutationResult Delete(IReadOnlyDictionary<string, IIndex> indexes) => Materialize(indexes).Mutate.Delete().Execute();

    /// <summary>
    /// Deletes tuples from one explicit target index whose identities are matched by this adopted condition.<br/>
    /// The condition may be composed across indexes in the same identity group; only the named target index is physically mutated, which keeps composed mutation low-ceremony without guessing a target.<br/>
    /// The target index is scanned for matching identities in this first bridge, while exact tuple deletion still preserves non-unique keys and sibling identities.<br/>
    /// </summary>
    /// <param name="targetIndexName">The index name whose tuples should be physically deleted.</param>
    /// <param name="resolveIndex">Function that resolves condition and target index names to opened LibraDex indexes.</param>
    /// <returns>A mutation result describing target tuples matched and deleted.</returns>
    public LibraDexIdentityMutationResult DeleteFrom(string targetIndexName, Func<string, IIndex> resolveIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetIndexName);
        ArgumentNullException.ThrowIfNull(resolveIndex);
        IIndex targetIndex = resolveIndex(targetIndexName);
        return LibraDexIdentityExecutionPlanner.ExecuteTargetDelete(Materialize(resolveIndex), targetIndex);
    }

    /// <summary>
    /// Deletes tuples from one explicit target index using a dictionary of opened indexes keyed by index name.<br/>
    /// This is the map-bound counterpart to <see cref="DeleteFrom(string, Func{string, IIndex})"/> for generated callers that already hold opened group indexes in a lookup table.<br/>
    /// </summary>
    /// <param name="targetIndexName">The index name whose tuples should be physically deleted.</param>
    /// <param name="indexes">The opened indexes keyed by LibraDex index name.</param>
    /// <returns>A mutation result describing target tuples matched and deleted.</returns>
    public LibraDexIdentityMutationResult DeleteFrom(string targetIndexName, IReadOnlyDictionary<string, IIndex> indexes)
    {
        IIndex targetIndex = ResolveTargetIndex(targetIndexName, indexes);
        return LibraDexIdentityExecutionPlanner.ExecuteTargetDelete(Materialize(indexes), targetIndex);
    }

    /// <summary>
    /// Replaces the keys of tuples matched by this adopted condition through the criteria-scoped mutation bridge.<br/>
    /// This is the condition-terminal convenience form of `Materialize(resolveIndex).Mutate.SetKey(newKey).Execute()` and keeps common update code on the adopted builder surface.<br/>
    /// This direct terminal is intentionally scoped to a single materialized primitive/composite leaf whose index can capture matching key/identity tuples and delete exact old tuples.<br/>
    /// Use <see cref="SetKeyOn(string, Func{string, IIndex}, object?)"/> when a composed, inverse, external, or cross-index condition should select identities and re-key one explicit physical target index.<br/>
    /// </summary>
    /// <param name="resolveIndex">Function that resolves a condition index name to an opened LibraDex index.</param>
    /// <param name="newKey">The replacement key to assign to every matched tuple.</param>
    /// <returns>A mutation result describing matched and re-keyed tuple counts.</returns>
    public LibraDexIdentityMutationResult SetKey(Func<string, IIndex> resolveIndex, object? newKey) => Materialize(resolveIndex).Mutate.SetKey(newKey).Execute();

    /// <summary>
    /// Replaces the keys of tuples matched by this adopted condition using a dictionary of opened indexes keyed by index name.<br/>
    /// This overload is the map-bound counterpart to <see cref="SetKey(Func{string, IIndex}, object)"/> for generated callers that already hold opened group indexes in a lookup table.<br/>
    /// Like the resolver overload, this direct terminal is scoped to a single materialized primitive/composite leaf; composed selectors should use <see cref="SetKeyOn(string, IReadOnlyDictionary{string, IIndex}, object?)"/>.<br/>
    /// </summary>
    /// <param name="indexes">The opened indexes keyed by LibraDex index name.</param>
    /// <param name="newKey">The replacement key to assign to every matched tuple.</param>
    /// <returns>A mutation result describing matched and re-keyed tuple counts.</returns>
    public LibraDexIdentityMutationResult SetKey(IReadOnlyDictionary<string, IIndex> indexes, object? newKey) => Materialize(indexes).Mutate.SetKey(newKey).Execute();

    /// <summary>
    /// Replaces keys on one explicit target index for tuples whose identities are matched by this adopted condition.<br/>
    /// The condition may be composed across indexes in the same identity group; only the named target index is physically re-keyed, which keeps intent explicit without introducing transaction ceremony.<br/>
    /// Replacement tuples are verified or inserted before old target tuples are removed.<br/>
    /// </summary>
    /// <param name="targetIndexName">The index name whose tuples should be physically re-keyed.</param>
    /// <param name="resolveIndex">Function that resolves condition and target index names to opened LibraDex indexes.</param>
    /// <param name="newKey">The replacement key to assign on the target index.</param>
    /// <returns>A mutation result describing target tuples matched and re-keyed.</returns>
    public LibraDexIdentityMutationResult SetKeyOn(string targetIndexName, Func<string, IIndex> resolveIndex, object? newKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetIndexName);
        ArgumentNullException.ThrowIfNull(resolveIndex);
        IIndex targetIndex = resolveIndex(targetIndexName);
        return LibraDexIdentityExecutionPlanner.ExecuteTargetSetKey(Materialize(resolveIndex), targetIndex, hasNewKey: true, newKey, newKeyFactory: null);
    }

    /// <summary>
    /// Replaces keys on one explicit target index using a dictionary of opened indexes keyed by index name.<br/>
    /// This is the map-bound counterpart to <see cref="SetKeyOn(string, Func{string, IIndex}, object)"/> for generated callers that already hold opened group indexes in a lookup table.<br/>
    /// </summary>
    /// <param name="targetIndexName">The index name whose tuples should be physically re-keyed.</param>
    /// <param name="indexes">The opened indexes keyed by LibraDex index name.</param>
    /// <param name="newKey">The replacement key to assign on the target index.</param>
    /// <returns>A mutation result describing target tuples matched and re-keyed.</returns>
    public LibraDexIdentityMutationResult SetKeyOn(string targetIndexName, IReadOnlyDictionary<string, IIndex> indexes, object? newKey)
    {
        IIndex targetIndex = ResolveTargetIndex(targetIndexName, indexes);
        return LibraDexIdentityExecutionPlanner.ExecuteTargetSetKey(Materialize(indexes), targetIndex, hasNewKey: true, newKey, newKeyFactory: null);
    }

    /// <summary>
    /// Replaces the keys of tuples matched by this adopted condition using a factory evaluated per matched identity.<br/>
    /// The factory receives the identity object for each original tuple and returns the replacement key, allowing callers to express deterministic key migration without opening a separate cursor facade.<br/>
    /// This direct terminal is intentionally scoped to a single materialized primitive/composite leaf whose index can capture matching key/identity tuples and delete exact old tuples.<br/>
    /// Use <see cref="SetKeyOnUsing(string, Func{string, IIndex}, Func{object, object?})"/> when a composed, inverse, external, or cross-index condition should select identities and derive replacement keys for one explicit physical target index.<br/>
    /// </summary>
    /// <param name="resolveIndex">Function that resolves a condition index name to an opened LibraDex index.</param>
    /// <param name="newKeyFactory">Factory that receives a matched identity and returns its replacement key.</param>
    /// <returns>A mutation result describing matched and re-keyed tuple counts.</returns>
    public LibraDexIdentityMutationResult SetKeyUsing(Func<string, IIndex> resolveIndex, Func<object, object?> newKeyFactory) => Materialize(resolveIndex).Mutate.SetKeyUsing(newKeyFactory).Execute();

    /// <summary>
    /// Replaces the keys of tuples matched by this adopted condition using a map-bound index resolver and per-identity key factory.<br/>
    /// The factory receives the identity object for each original tuple and returns the replacement key, while the dictionary supplies the opened indexes referenced by condition leaves.<br/>
    /// Like the resolver overload, this direct terminal is scoped to a single materialized primitive/composite leaf; composed selectors should use <see cref="SetKeyOnUsing(string, IReadOnlyDictionary{string, IIndex}, Func{object, object?})"/>.<br/>
    /// </summary>
    /// <param name="indexes">The opened indexes keyed by LibraDex index name.</param>
    /// <param name="newKeyFactory">Factory that receives a matched identity and returns its replacement key.</param>
    /// <returns>A mutation result describing matched and re-keyed tuple counts.</returns>
    public LibraDexIdentityMutationResult SetKeyUsing(IReadOnlyDictionary<string, IIndex> indexes, Func<object, object?> newKeyFactory) => Materialize(indexes).Mutate.SetKeyUsing(newKeyFactory).Execute();

    /// <summary>
    /// Replaces keys on one explicit target index using a replacement-key factory evaluated per matched target tuple identity.<br/>
    /// The condition supplies the identity set, the target index supplies the physical tuple stream, and the factory maps each target identity to its new key.<br/>
    /// </summary>
    /// <param name="targetIndexName">The index name whose tuples should be physically re-keyed.</param>
    /// <param name="resolveIndex">Function that resolves condition and target index names to opened LibraDex indexes.</param>
    /// <param name="newKeyFactory">Factory that receives a matched identity and returns its target-index replacement key.</param>
    /// <returns>A mutation result describing target tuples matched and re-keyed.</returns>
    public LibraDexIdentityMutationResult SetKeyOnUsing(string targetIndexName, Func<string, IIndex> resolveIndex, Func<object, object?> newKeyFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetIndexName);
        ArgumentNullException.ThrowIfNull(resolveIndex);
        ArgumentNullException.ThrowIfNull(newKeyFactory);
        IIndex targetIndex = resolveIndex(targetIndexName);
        return LibraDexIdentityExecutionPlanner.ExecuteTargetSetKey(Materialize(resolveIndex), targetIndex, hasNewKey: false, newKey: null, newKeyFactory);
    }

    /// <summary>
    /// Replaces keys on one explicit target index using a map-bound index resolver and per-identity replacement-key factory.<br/>
    /// This overload keeps generated composed mutation code on dictionary lookups while preserving explicit physical target selection.<br/>
    /// </summary>
    /// <param name="targetIndexName">The index name whose tuples should be physically re-keyed.</param>
    /// <param name="indexes">The opened indexes keyed by LibraDex index name.</param>
    /// <param name="newKeyFactory">Factory that receives a matched identity and returns its target-index replacement key.</param>
    /// <returns>A mutation result describing target tuples matched and re-keyed.</returns>
    public LibraDexIdentityMutationResult SetKeyOnUsing(string targetIndexName, IReadOnlyDictionary<string, IIndex> indexes, Func<object, object?> newKeyFactory)
    {
        ArgumentNullException.ThrowIfNull(newKeyFactory);
        IIndex targetIndex = ResolveTargetIndex(targetIndexName, indexes);
        return LibraDexIdentityExecutionPlanner.ExecuteTargetSetKey(Materialize(indexes), targetIndex, hasNewKey: false, newKey: null, newKeyFactory);
    }

    /// <summary>
    /// Creates condition-scoped grouping helpers for this adopted condition.<br/>
    /// The resolver binds index names used by the condition descriptor, while the later `By(...)` call selects the grouping index whose keys define group boundaries.<br/>
    /// Grouping remains condition-terminal so root index handles do not regain a parallel grouped retrieval facade.<br/>
    /// </summary>
    /// <param name="resolveIndex">Function that resolves a condition index name to an opened LibraDex index.</param>
    /// <returns>A grouping helper bound to this adopted condition and resolver.</returns>
    public LibraDexConditionGroups Groups(Func<string, IIndex> resolveIndex)
    {
        ArgumentNullException.ThrowIfNull(resolveIndex);
        return new LibraDexConditionGroups(this, resolveIndex);
    }

    /// <summary>
    /// Classifies every leaf in this condition against supplied opened indexes without executing the condition.<br/>
    /// Use this before bridge widening so each permutation states whether it is index-backed, projection-backed, composite-backed, visible scan-like, unsupported, or unresolved.<br/>
    /// </summary>
    /// <param name="indexes">The opened indexes keyed by LibraDex index name.</param>
    /// <returns>One classification row per condition leaf in builder order.</returns>
    public IReadOnlyList<LibraDexConditionLeafClassification> Classify(IReadOnlyDictionary<string, IIndex> indexes) => Classify(CreateOptionalIndexResolver(indexes));

    /// <summary>
    /// Classifies every leaf in this condition through a resolver without executing the condition.<br/>
    /// A null resolver result is recorded as a resolution failure instead of throwing so permutation tests can prove missing-index behavior explicitly.<br/>
    /// </summary>
    /// <param name="resolveIndex">Function that resolves a condition index name to an opened LibraDex index, or null when the name is unavailable.</param>
    /// <returns>One classification row per condition leaf in builder order.</returns>
    public IReadOnlyList<LibraDexConditionLeafClassification> Classify(Func<string, IIndex?> resolveIndex)
    {
        ArgumentNullException.ThrowIfNull(resolveIndex);
        IReadOnlyList<LibraDexConditionLeafDescriptor> leaves = Leaves;
        LibraDexConditionLeafClassification[] classifications = new LibraDexConditionLeafClassification[leaves.Count];
        for (int i = 0; i < leaves.Count; i++)
        {
            LibraDexConditionLeafDescriptor leaf = leaves[i];
            IIndex? index = resolveIndex(leaf.IndexName);
            classifications[i] = ClassifyResolvedLeaf(Group, leaf, index);
        }

        return classifications;
    }

    /// <summary>
    /// Builds the adopted-condition bridge plan for supplied opened indexes.<br/>
    /// The plan shows which leaves can use the current primitive bridge and which leaves need projection, composite, scan-policy, or caller-resolution work first.<br/>
    /// </summary>
    /// <param name="indexes">The opened indexes keyed by LibraDex index name.</param>
    /// <returns>A bridge readiness plan for this condition.</returns>
    public LibraDexConditionBridgePlan PlanBridge(IReadOnlyDictionary<string, IIndex> indexes) => PlanBridge(CreateOptionalIndexResolver(indexes));

    /// <summary>
    /// Builds the adopted-condition bridge plan through a resolver.<br/>
    /// A null resolver result is planned as a caller-resolution action rather than thrown, which keeps permutation checks inspectable.<br/>
    /// </summary>
    /// <param name="resolveIndex">Function that resolves a condition index name to an opened LibraDex index, or null when the name is unavailable.</param>
    /// <returns>A bridge readiness plan for this condition.</returns>
    public LibraDexConditionBridgePlan PlanBridge(Func<string, IIndex?> resolveIndex)
    {
        IReadOnlyList<LibraDexConditionLeafClassification> classifications = Classify(resolveIndex);
        LibraDexConditionBridgePlanRow[] rows = new LibraDexConditionBridgePlanRow[classifications.Count];
        for (int i = 0; i < classifications.Count; i++)
        {
            rows[i] = CreateBridgePlanRow(classifications[i]);
        }

        return new LibraDexConditionBridgePlan(Group, rows);
    }

    internal LibraDexConditionNode GetRoot()
        => root;

    /// <summary>
    /// Creates a resolver that requires every referenced condition index to exist in the supplied dictionary.<br/>
    /// This keeps dictionary-bound materialization overloads on the same error wording as target mutation helpers while avoiding repeated lookup lambdas.<br/>
    /// </summary>
    /// <param name="indexes">The opened indexes keyed by LibraDex index name.</param>
    /// <returns>A resolver that returns an opened index or throws when the index name is missing.</returns>
    private Func<string, IIndex> CreateRequiredIndexResolver(IReadOnlyDictionary<string, IIndex> indexes)
    {
        ArgumentNullException.ThrowIfNull(indexes);
        return indexName =>
        {
            if (!indexes.TryGetValue(indexName, out IIndex? index))
            {
                throw new KeyNotFoundException($"Index '{indexName}' was not supplied for condition group '{Group}'.");
            }

            return index;
        };
    }

    /// <summary>
    /// Creates a resolver that returns null when a supplied dictionary does not contain a referenced condition index.<br/>
    /// Classification and bridge planning use null to keep missing-index diagnostics inspectable instead of throwing during review-oriented planning.<br/>
    /// </summary>
    /// <param name="indexes">The opened indexes keyed by LibraDex index name.</param>
    /// <returns>A resolver that returns an opened index or null when the index name is missing.</returns>
    private static Func<string, IIndex?> CreateOptionalIndexResolver(IReadOnlyDictionary<string, IIndex> indexes)
    {
        ArgumentNullException.ThrowIfNull(indexes);
        return indexName => indexes.TryGetValue(indexName, out IIndex? index) ? index : null;
    }

    /// <summary>
    /// Resolves one explicit target index from a dictionary-bound mutation call.<br/>
    /// Targeted delete and re-key overloads share this helper so missing-target diagnostics do not drift between static-key and factory-key mutations.<br/>
    /// </summary>
    /// <param name="targetIndexName">The index name whose tuples should be physically mutated.</param>
    /// <param name="indexes">The opened indexes keyed by LibraDex index name.</param>
    /// <returns>The opened target index.</returns>
    private IIndex ResolveTargetIndex(string targetIndexName, IReadOnlyDictionary<string, IIndex> indexes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetIndexName);
        ArgumentNullException.ThrowIfNull(indexes);
        if (!indexes.TryGetValue(targetIndexName, out IIndex? targetIndex))
        {
            throw new KeyNotFoundException($"Target index '{targetIndexName}' was not supplied for condition group '{Group}'.");
        }

        return targetIndex;
    }

    /// <summary>
    /// Creates a new completed condition by rewriting every leaf descriptor in the tree.<br/>
    /// This keeps reusable partial conditions immutable while supporting Abraxas-style late replacement of named values and deferred index selectors.<br/>
    /// </summary>
    /// <param name="rewriteLeaf">Function that returns the replacement descriptor for each leaf.</param>
    /// <returns>A completed condition with the rewritten descriptor tree.</returns>
    internal LibraDexConditionEndCondition Rewrite(Func<LibraDexConditionLeafDescriptor, LibraDexConditionLeafDescriptor> rewriteLeaf)
    {
        ArgumentNullException.ThrowIfNull(rewriteLeaf);
        return new LibraDexConditionEndCondition(Group, root.Rewrite(rewriteLeaf));
    }

    private LibraDexConditionEndCondition Compose(LibraDexConditionEndCondition other, bool useOr)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (!string.Equals(Group, other.Group, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("LibraDex conditions can only compose inside the same identity group.");
        }

        LibraDexConditionContinueOrEnd left = LibraDexCondition.ForGroup(Group).Group(this);
        return useOr
            ? left.OR.Group(other).EndCondition
            : left.AND.Group(other).EndCondition;
    }

    private LibraDexConditionEndCondition RewriteOperands(string name, LibraDexConditionOperand replacement)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Rewrite(leaf =>
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
        });
    }

    private static LibraDexConditionBridgePlanRow CreateBridgePlanRow(LibraDexConditionLeafClassification classification)
    {
        return classification.ExecutionClass switch
        {
            LibraDexConditionExecutionClass.IndexBacked => new LibraDexConditionBridgePlanRow(
                classification,
                LibraDexConditionBridgeAction.ExecuteCurrentPrimitive,
                CanExecuteWithCurrentBridge: true,
                "The leaf can materialize through the current primitive criteria bridge."),
            LibraDexConditionExecutionClass.ProjectionBacked => new LibraDexConditionBridgePlanRow(
                classification,
                LibraDexConditionBridgeAction.ConnectProjectionPrimitive,
                CanExecuteWithCurrentBridge: false,
                "The leaf requires a maintained projection primitive before it should execute."),
            LibraDexConditionExecutionClass.CompositeBacked => new LibraDexConditionBridgePlanRow(
                classification,
                LibraDexConditionBridgeAction.ConnectCompositePrimitive,
                CanExecuteWithCurrentBridge: false,
                "The leaf requires a composite-key primitive before it should execute."),
            LibraDexConditionExecutionClass.VisibleScanLike => new LibraDexConditionBridgePlanRow(
                classification,
                LibraDexConditionBridgeAction.RequireVisibleScanPolicy,
                CanExecuteWithCurrentBridge: false,
                "The leaf is valid intent but must opt into or report scan-like behavior before execution."),
            LibraDexConditionExecutionClass.Unsupported => new LibraDexConditionBridgePlanRow(
                classification,
                LibraDexConditionBridgeAction.RejectUnsupported,
                CanExecuteWithCurrentBridge: false,
                "The leaf has no supported codec, projection, or primitive rule yet."),
            LibraDexConditionExecutionClass.ResolutionFailure => new LibraDexConditionBridgePlanRow(
                classification,
                LibraDexConditionBridgeAction.ResolveIndex,
                CanExecuteWithCurrentBridge: false,
                "The caller must supply an opened index for this index name."),
            LibraDexConditionExecutionClass.IdentityGroupMismatch => new LibraDexConditionBridgePlanRow(
                classification,
                LibraDexConditionBridgeAction.FixIdentityGroup,
                CanExecuteWithCurrentBridge: false,
                "The resolved index must belong to the condition identity group."),
            _ => new LibraDexConditionBridgePlanRow(
                classification,
                LibraDexConditionBridgeAction.RejectUnsupported,
                CanExecuteWithCurrentBridge: false,
                "The classification is not recognized.")
        };
    }

    internal static LibraDexConditionLeafClassification ClassifyResolvedLeaf(string group, LibraDexConditionLeafDescriptor leaf, IIndex? index)
    {
        if (index is null)
        {
            return new LibraDexConditionLeafClassification(
                leaf.IndexName,
                leaf.ValueKind,
                leaf.Operator,
                LibraDexConditionExecutionClass.ResolutionFailure,
                "No opened index was supplied for this condition index name.",
                ProjectionKind: null);
        }

        if (!string.Equals(index.Group, group, StringComparison.Ordinal))
        {
            return new LibraDexConditionLeafClassification(
                leaf.IndexName,
                leaf.ValueKind,
                leaf.Operator,
                LibraDexConditionExecutionClass.IdentityGroupMismatch,
                "The resolved index belongs to a different identity group.",
                ProjectionKind: null);
        }

        if (index.LogicalShape?.KeyFamily == CatalogIndexKeyFamily.Composite &&
            leaf.Operator == LibraDexConditionOperatorKind.CompositeMatch)
        {
            return new LibraDexConditionLeafClassification(
                leaf.IndexName,
                leaf.ValueKind,
                leaf.Operator,
                LibraDexConditionExecutionClass.IndexBacked,
                "The resolved index is a routed composite-key shape and the leaf carries named tier predicates.",
                LibraDexIndexProjectionKind.Exact);
        }

        if (index.LogicalShape?.KeyFamily == CatalogIndexKeyFamily.Composite)
        {
            return new LibraDexConditionLeafClassification(
                leaf.IndexName,
                leaf.ValueKind,
                leaf.Operator,
                LibraDexConditionExecutionClass.CompositeBacked,
                "The resolved index is a composite-key shape.",
                LibraDexIndexProjectionKind.Exact);
        }

        return leaf.ValueKind switch
        {
            LibraDexConditionValueKind.String => ClassifyStringLeaf(leaf, index),
            LibraDexConditionValueKind.Guid => ClassifyGuidLeaf(leaf, index),
            LibraDexConditionValueKind.Binary => ClassifyBinaryLeaf(leaf, index),
            LibraDexConditionValueKind.DateTime or LibraDexConditionValueKind.DateOnly or LibraDexConditionValueKind.TimeOnly => ClassifyDateLeaf(leaf, index),
            _ => ClassifyPrimitiveLeaf(leaf)
        };
    }

    private static LibraDexConditionLeafClassification ClassifyPrimitiveLeaf(LibraDexConditionLeafDescriptor leaf)
    {
        return leaf.Operator switch
        {
            LibraDexConditionOperatorKind.EqualTo or
            LibraDexConditionOperatorKind.NotEqualTo or
            LibraDexConditionOperatorKind.GreaterThan or
            LibraDexConditionOperatorKind.GreaterOrEqual or
            LibraDexConditionOperatorKind.LessThan or
            LibraDexConditionOperatorKind.LessOrEqual or
            LibraDexConditionOperatorKind.Between or
            LibraDexConditionOperatorKind.NotBetween or
            LibraDexConditionOperatorKind.InSet or
            LibraDexConditionOperatorKind.NotInSet or
            LibraDexConditionOperatorKind.ScalarNullState => new LibraDexConditionLeafClassification(
                leaf.IndexName,
                leaf.ValueKind,
                leaf.Operator,
                LibraDexConditionExecutionClass.IndexBacked,
                "The operator maps to an ordered-key or exact-key primitive over the selected index.",
                LibraDexIndexProjectionKind.Exact),
            LibraDexConditionOperatorKind.BitAndEqualTo or
            LibraDexConditionOperatorKind.BitAndNotEqualTo => new LibraDexConditionLeafClassification(
                leaf.IndexName,
                leaf.ValueKind,
                leaf.Operator,
                LibraDexConditionExecutionClass.VisibleScanLike,
                "The bitmask operator is valid scalar intent but is not generally contiguous in ordered key space, so the current bridge scans compact index keys and applies a masked residual test.",
                LibraDexIndexProjectionKind.Exact),
            _ => new LibraDexConditionLeafClassification(
                leaf.IndexName,
                leaf.ValueKind,
                leaf.Operator,
                LibraDexConditionExecutionClass.Unsupported,
                "The selected value kind has no primitive or projection rule for this operator.",
                ProjectionKind: null)
        };
    }

    private static LibraDexConditionLeafClassification ClassifyProjectionAwareScalarLeaf(
        LibraDexConditionLeafDescriptor leaf,
        IIndex index,
        LibraDexIndexProjectionKind projectionKind)
    {
        if (leaf.Operator is LibraDexConditionOperatorKind.EqualTo or
            LibraDexConditionOperatorKind.NotEqualTo or
            LibraDexConditionOperatorKind.GreaterThan or
            LibraDexConditionOperatorKind.GreaterOrEqual or
            LibraDexConditionOperatorKind.LessThan or
            LibraDexConditionOperatorKind.LessOrEqual or
            LibraDexConditionOperatorKind.Between or
            LibraDexConditionOperatorKind.NotBetween or
            LibraDexConditionOperatorKind.InSet or
            LibraDexConditionOperatorKind.NotInSet)
        {
            return new LibraDexConditionLeafClassification(
                leaf.IndexName,
                leaf.ValueKind,
                leaf.Operator,
                LibraDexConditionExecutionClass.IndexBacked,
                "The operator maps to the exact ordered-key primitive for this value kind.",
                LibraDexIndexProjectionKind.Exact);
        }

        if (index.LogicalShape?.HasProjection(projectionKind) == true)
        {
            return new LibraDexConditionLeafClassification(
                leaf.IndexName,
                leaf.ValueKind,
                leaf.Operator,
                LibraDexConditionExecutionClass.ProjectionBacked,
                "The operator requires a maintained value-specific projection and the resolved shape declares one.",
                projectionKind);
        }

        return new LibraDexConditionLeafClassification(
            leaf.IndexName,
            leaf.ValueKind,
            leaf.Operator,
            LibraDexConditionExecutionClass.Unsupported,
            "The operator requires a maintained value-specific projection that the resolved shape does not declare.",
            projectionKind);
    }

    /// <summary>
    /// Classifies a date-like condition leaf against the resolved LibraDex index shape.<br/>
    /// Exact and chronological boundary operators remain ordinary index-backed operations, year-qualified operators use ordered ranges, and component-only operators use structured scalar shift-and-mask predicates when the key type preserves the requested component.<br/>
    /// </summary>
    /// <param name="leaf">The captured condition leaf.</param>
    /// <param name="index">The resolved logical index for the leaf.</param>
    /// <returns>The execution classification for the date-like condition leaf.</returns>
    private static LibraDexConditionLeafClassification ClassifyDateLeaf(LibraDexConditionLeafDescriptor leaf, IIndex index)
    {
        if (leaf.Operator is LibraDexConditionOperatorKind.EqualTo or
            LibraDexConditionOperatorKind.NotEqualTo or
            LibraDexConditionOperatorKind.GreaterThan or
            LibraDexConditionOperatorKind.GreaterOrEqual or
            LibraDexConditionOperatorKind.LessThan or
            LibraDexConditionOperatorKind.LessOrEqual or
            LibraDexConditionOperatorKind.Between or
            LibraDexConditionOperatorKind.NotBetween or
            LibraDexConditionOperatorKind.InSet or
            LibraDexConditionOperatorKind.NotInSet)
        {
            return new LibraDexConditionLeafClassification(
                leaf.IndexName,
                leaf.ValueKind,
                leaf.Operator,
                LibraDexConditionExecutionClass.IndexBacked,
                "The operator maps to the exact structured date ordered-key primitive.",
                LibraDexIndexProjectionKind.Exact);
        }

        if ((leaf.Operator is LibraDexConditionOperatorKind.YearEqualTo or
            LibraDexConditionOperatorKind.YearNotEqualTo or
            LibraDexConditionOperatorKind.YearIn or
            LibraDexConditionOperatorKind.YearNotIn or
            LibraDexConditionOperatorKind.YearRange or
            LibraDexConditionOperatorKind.YearNotRange or
            LibraDexConditionOperatorKind.YearOnOrAfter or
            LibraDexConditionOperatorKind.YearOnOrBefore or
            LibraDexConditionOperatorKind.YearMonth or
            LibraDexConditionOperatorKind.YearMonthDay or
            LibraDexConditionOperatorKind.YearMonthIn or
            LibraDexConditionOperatorKind.YearMonthDayIn or
            LibraDexConditionOperatorKind.YearInMonths or
            LibraDexConditionOperatorKind.YearQuarter or
            LibraDexConditionOperatorKind.IsToday or
            LibraDexConditionOperatorKind.IsYesterday or
            LibraDexConditionOperatorKind.IsInLastDays) &&
            index.LogicalShape?.HasProjection(LibraDexIndexProjectionKind.StructuredDate) == true &&
            index.KeyType != typeof(TimeOnly))
        {
            return new LibraDexConditionLeafClassification(
                leaf.IndexName,
                leaf.ValueKind,
                leaf.Operator,
                LibraDexConditionExecutionClass.IndexBacked,
                "The structured date codec stores date components in high key bits, so this operator maps to one ordered range or a same-index union of ordered ranges.",
                LibraDexIndexProjectionKind.StructuredDate);
        }

        if ((leaf.Operator is LibraDexConditionOperatorKind.IsInLastHours or
            LibraDexConditionOperatorKind.IsInLastMinutes) &&
            index.LogicalShape?.HasProjection(LibraDexIndexProjectionKind.StructuredDate) == true &&
            (index.KeyType == typeof(DateTime) || index.KeyType == typeof(DateTimeOffset)))
        {
            return new LibraDexConditionLeafClassification(
                leaf.IndexName,
                leaf.ValueKind,
                leaf.Operator,
                LibraDexConditionExecutionClass.IndexBacked,
                "The structured date-time codec stores chronological values in ordered form, so this relative window maps to one ordered range at materialization time.",
                LibraDexIndexProjectionKind.StructuredDate);
        }

        if ((leaf.Operator is LibraDexConditionOperatorKind.IsMorning or
            LibraDexConditionOperatorKind.IsAfternoon or
            LibraDexConditionOperatorKind.IsEvening or
            LibraDexConditionOperatorKind.IsNight) &&
            index.LogicalShape?.HasProjection(LibraDexIndexProjectionKind.StructuredDate) == true &&
            index.KeyType == typeof(TimeOnly))
        {
            return new LibraDexConditionLeafClassification(
                leaf.IndexName,
                leaf.ValueKind,
                leaf.Operator,
                LibraDexConditionExecutionClass.IndexBacked,
                "The structured time codec stores TimeOnly hour in ordered form, so this semantic time-of-day branch maps to one ordered range or a two-range wraparound union.",
                LibraDexIndexProjectionKind.StructuredDate);
        }

        if (IsStructuredComponentOperator(leaf.Operator) &&
            index.LogicalShape?.HasProjection(LibraDexIndexProjectionKind.StructuredDate) == true &&
            SupportsStructuredComponentOperator(index.KeyType, leaf.Operator))
        {
            return new LibraDexConditionLeafClassification(
                leaf.IndexName,
                leaf.ValueKind,
                leaf.Operator,
                LibraDexConditionExecutionClass.IndexBacked,
                "The structured date/time codec stores this component in fixed key bits, so the operator maps to a condition-derived shift-and-mask primitive over encoded scalar keys.",
                LibraDexIndexProjectionKind.StructuredDate);
        }

        return new LibraDexConditionLeafClassification(
            leaf.IndexName,
            leaf.ValueKind,
            leaf.Operator,
            LibraDexConditionExecutionClass.Unsupported,
            "The structured date operator needs a multi-range, bit-slice, mask, or companion-projection primitive before it should execute.",
            LibraDexIndexProjectionKind.StructuredDate);
    }

    /// <summary>
    /// Determines whether a date/time operator can be represented as a structured scalar component predicate.<br/>
    /// These operators are not necessarily contiguous in ordered key space, but their requested fields are directly addressable through the Abraxas-compatible packed key layout.<br/>
    /// </summary>
    /// <param name="operatorKind">The condition operator to inspect.</param>
    /// <returns><see langword="true"/> when the operator is a component predicate candidate.</returns>
    private static bool IsStructuredComponentOperator(LibraDexConditionOperatorKind operatorKind)
    {
        return operatorKind is
            LibraDexConditionOperatorKind.MonthEqualTo or
            LibraDexConditionOperatorKind.MonthIn or
            LibraDexConditionOperatorKind.MonthNotIn or
            LibraDexConditionOperatorKind.MonthRange or
            LibraDexConditionOperatorKind.MonthNotRange or
            LibraDexConditionOperatorKind.DayEqualTo or
            LibraDexConditionOperatorKind.DayIn or
            LibraDexConditionOperatorKind.DayRange or
            LibraDexConditionOperatorKind.DayNotRange or
            LibraDexConditionOperatorKind.MonthDay or
            LibraDexConditionOperatorKind.QuarterEqualTo or
            LibraDexConditionOperatorKind.InQuarter or
            LibraDexConditionOperatorKind.InQuarterRange or
            LibraDexConditionOperatorKind.IsQuarterStart or
            LibraDexConditionOperatorKind.IsQuarterEnd or
            LibraDexConditionOperatorKind.IsHalfYearStart or
            LibraDexConditionOperatorKind.IsHalfYearEnd or
            LibraDexConditionOperatorKind.IsFirstOfMonth or
            LibraDexConditionOperatorKind.IsLastOfMonth or
            LibraDexConditionOperatorKind.IsWeekend or
            LibraDexConditionOperatorKind.IsWeekday or
            LibraDexConditionOperatorKind.IsMorning or
            LibraDexConditionOperatorKind.IsAfternoon or
            LibraDexConditionOperatorKind.IsEvening or
            LibraDexConditionOperatorKind.IsNight;
    }

    /// <summary>
    /// Determines whether a structured scalar key type preserves the component required by an operator.<br/>
    /// DateOnly supports calendar components, DateTime and DateTimeOffset support calendar plus hour components, and TimeOnly uses the separate ordered time range bridge for semantic time-of-day operators.<br/>
    /// </summary>
    /// <param name="keyType">The resolved index key type.</param>
    /// <param name="operatorKind">The condition operator to inspect.</param>
    /// <returns><see langword="true"/> when the key type can execute the operator without projection lookup.</returns>
    private static bool SupportsStructuredComponentOperator(Type keyType, LibraDexConditionOperatorKind operatorKind)
    {
        if (keyType == typeof(TimeOnly))
        {
            return operatorKind is
                LibraDexConditionOperatorKind.IsMorning or
                LibraDexConditionOperatorKind.IsAfternoon or
                LibraDexConditionOperatorKind.IsEvening or
                LibraDexConditionOperatorKind.IsNight;
        }

        bool isDateLike = keyType == typeof(DateTime) ||
            keyType == typeof(DateTimeOffset) ||
            keyType == typeof(DateOnly);
        if (!isDateLike)
        {
            return false;
        }

        bool requiresHour = operatorKind is
            LibraDexConditionOperatorKind.IsMorning or
            LibraDexConditionOperatorKind.IsAfternoon or
            LibraDexConditionOperatorKind.IsEvening or
            LibraDexConditionOperatorKind.IsNight;
        return !requiresHour || keyType != typeof(DateOnly);
    }

    private static LibraDexConditionLeafClassification ClassifyGuidLeaf(LibraDexConditionLeafDescriptor leaf, IIndex index)
    {
        if (leaf.Operator is LibraDexConditionOperatorKind.EqualTo or
            LibraDexConditionOperatorKind.NotEqualTo or
            LibraDexConditionOperatorKind.InSet or
            LibraDexConditionOperatorKind.NotInSet)
        {
            return new LibraDexConditionLeafClassification(
                leaf.IndexName,
                leaf.ValueKind,
                leaf.Operator,
                LibraDexConditionExecutionClass.IndexBacked,
                "Exact GUID comparison can use the selected index's ordinary exact-key primitive.",
                LibraDexIndexProjectionKind.Exact);
        }

        if (leaf.Operator is LibraDexConditionOperatorKind.StartsWith or
            LibraDexConditionOperatorKind.EndsWith or
            LibraDexConditionOperatorKind.Contains or
            LibraDexConditionOperatorKind.MatchesPattern)
        {
            return new LibraDexConditionLeafClassification(
                leaf.IndexName,
                leaf.ValueKind,
                leaf.Operator,
                LibraDexConditionExecutionClass.IndexBacked,
                "The GUID operator maps to a condition-derived canonical nibble predicate over encoded GUID bytes, avoiding per-row text conversion.",
                LibraDexIndexProjectionKind.GuidSegments);
        }

        return new LibraDexConditionLeafClassification(
            leaf.IndexName,
            leaf.ValueKind,
            leaf.Operator,
            LibraDexConditionExecutionClass.Unsupported,
            "The GUID operator is not connected to an exact-key, membership, or encoded GUID pattern primitive.",
            LibraDexIndexProjectionKind.GuidSegments);
    }

    private static LibraDexConditionLeafClassification ClassifyBinaryLeaf(LibraDexConditionLeafDescriptor leaf, IIndex index)
    {
        if (leaf.Operator is LibraDexConditionOperatorKind.EqualTo or
            LibraDexConditionOperatorKind.NotEqualTo or
            LibraDexConditionOperatorKind.GreaterThan or
            LibraDexConditionOperatorKind.GreaterOrEqual or
            LibraDexConditionOperatorKind.LessThan or
            LibraDexConditionOperatorKind.LessOrEqual or
            LibraDexConditionOperatorKind.Between or
            LibraDexConditionOperatorKind.NotBetween or
            LibraDexConditionOperatorKind.InSet or
            LibraDexConditionOperatorKind.NotInSet)
        {
            return new LibraDexConditionLeafClassification(
                leaf.IndexName,
                leaf.ValueKind,
                leaf.Operator,
                LibraDexConditionExecutionClass.IndexBacked,
                "Exact binary comparison can use the selected index's ordinary fixed-byte ordered-key primitive.",
                LibraDexIndexProjectionKind.Exact);
        }

        if (index.KeyType == typeof(byte[]) &&
            leaf.Operator == LibraDexConditionOperatorKind.EndsWith &&
            index.LogicalShape?.HasProjection(LibraDexIndexProjectionKind.Exact, LibraDexIndexByteDirection.Reversed) == true)
        {
            return new LibraDexConditionLeafClassification(
                leaf.IndexName,
                leaf.ValueKind,
                leaf.Operator,
                LibraDexConditionExecutionClass.ProjectionBacked,
                "Binary suffix lookup can use the selected index's maintained reversed exact-byte projection.",
                LibraDexIndexProjectionKind.Exact);
        }

        if (index.KeyType == typeof(byte[]) &&
            leaf.Operator is LibraDexConditionOperatorKind.StartsWith or
                LibraDexConditionOperatorKind.EndsWith or
                LibraDexConditionOperatorKind.Contains or
                LibraDexConditionOperatorKind.MatchesPattern or
                LibraDexConditionOperatorKind.BinarySliceEqual or
                LibraDexConditionOperatorKind.BinaryTypedSliceEqualTo or
                LibraDexConditionOperatorKind.BinaryTypedSliceGreaterThan or
                LibraDexConditionOperatorKind.BinaryTypedSliceGreaterOrEqual or
                LibraDexConditionOperatorKind.BinaryTypedSliceLessThan or
                LibraDexConditionOperatorKind.BinaryTypedSliceLessOrEqual or
                LibraDexConditionOperatorKind.BinaryTypedSliceBetween or
                LibraDexConditionOperatorKind.BinaryTypedSliceStartsWith or
                LibraDexConditionOperatorKind.BinaryTypedSliceContains or
                LibraDexConditionOperatorKind.BinaryTypedSliceBitAndEqualTo or
                LibraDexConditionOperatorKind.BinaryTypedSliceBitAndNotEqualTo)
        {
            return new LibraDexConditionLeafClassification(
                leaf.IndexName,
                leaf.ValueKind,
                leaf.Operator,
                LibraDexConditionExecutionClass.IndexBacked,
                "The binary operator maps to a condition-derived byte predicate over encoded fixed-width key bytes, avoiding decoded byte-array allocation per candidate row.",
                LibraDexIndexProjectionKind.Exact);
        }

        return new LibraDexConditionLeafClassification(
            leaf.IndexName,
            leaf.ValueKind,
            leaf.Operator,
            LibraDexConditionExecutionClass.Unsupported,
            "The binary operator is not connected to an exact-key, membership, or raw byte-slice primitive.",
            LibraDexIndexProjectionKind.Exact);
    }

    private static LibraDexConditionLeafClassification ClassifyStringLeaf(LibraDexConditionLeafDescriptor leaf, IIndex index)
    {
        return leaf.Operator switch
        {
            LibraDexConditionOperatorKind.EqualTo or
            LibraDexConditionOperatorKind.NotEqualTo or
            LibraDexConditionOperatorKind.GreaterThan or
            LibraDexConditionOperatorKind.GreaterOrEqual or
            LibraDexConditionOperatorKind.LessThan or
            LibraDexConditionOperatorKind.LessOrEqual or
            LibraDexConditionOperatorKind.Between or
            LibraDexConditionOperatorKind.NotBetween when !leaf.IgnoreCase => new LibraDexConditionLeafClassification(
                leaf.IndexName,
                leaf.ValueKind,
                leaf.Operator,
                LibraDexConditionExecutionClass.IndexBacked,
                "Exact string comparison can use the selected index's ordinary ordered-key primitive.",
                LibraDexIndexProjectionKind.Exact),
            LibraDexConditionOperatorKind.EqualTo or
            LibraDexConditionOperatorKind.NotEqualTo or
            LibraDexConditionOperatorKind.GreaterThan or
            LibraDexConditionOperatorKind.GreaterOrEqual or
            LibraDexConditionOperatorKind.LessThan or
            LibraDexConditionOperatorKind.LessOrEqual or
            LibraDexConditionOperatorKind.Between or
            LibraDexConditionOperatorKind.NotBetween => ClassifyTextProjection(leaf, index, LibraDexIndexProjectionKind.SortKey, "Case-insensitive string comparison requires a maintained sort-key projection."),
            LibraDexConditionOperatorKind.StartsWith when !leaf.IgnoreCase => new LibraDexConditionLeafClassification(
                leaf.IndexName,
                leaf.ValueKind,
                leaf.Operator,
                LibraDexConditionExecutionClass.IndexBacked,
                "Exact starts-with can be represented as an ordered-key boundary over the selected string index.",
                LibraDexIndexProjectionKind.Exact),
            LibraDexConditionOperatorKind.StartsWith => ClassifyTextProjection(leaf, index, LibraDexIndexProjectionKind.FoldedText, LibraDexIndexByteDirection.Forward, "Case-insensitive starts-with requires a maintained folded-text projection."),
            LibraDexConditionOperatorKind.EndsWith when leaf.IgnoreCase => ClassifyTextProjection(leaf, index, LibraDexIndexProjectionKind.FoldedText, LibraDexIndexByteDirection.Reversed, "Case-insensitive ends-with requires a maintained reversed folded-text projection."),
            LibraDexConditionOperatorKind.EndsWith => ClassifyTextProjection(leaf, index, LibraDexIndexProjectionKind.Exact, LibraDexIndexByteDirection.Reversed, "Case-sensitive ends-with prefers a maintained reversed exact-text projection."),
            LibraDexConditionOperatorKind.Contains or
            LibraDexConditionOperatorKind.MatchesPattern or
            LibraDexConditionOperatorKind.NotMatchesPattern or
            LibraDexConditionOperatorKind.RegexMatches or
            LibraDexConditionOperatorKind.NotRegexMatches or
            LibraDexConditionOperatorKind.MatchesWith or
            LibraDexConditionOperatorKind.NotMatchesWith or
            LibraDexConditionOperatorKind.MatchesInSet or
            LibraDexConditionOperatorKind.NotMatchesInSet => new LibraDexConditionLeafClassification(
                leaf.IndexName,
                leaf.ValueKind,
                leaf.Operator,
                LibraDexConditionExecutionClass.VisibleScanLike,
                "No contains, wildcard-pattern, or regex-capture maintained projection is declared in the current shape model.",
                ProjectionKind: null),
            LibraDexConditionOperatorKind.InSet or
            LibraDexConditionOperatorKind.NotInSet when !leaf.IgnoreCase => new LibraDexConditionLeafClassification(
                leaf.IndexName,
                leaf.ValueKind,
                leaf.Operator,
                LibraDexConditionExecutionClass.IndexBacked,
                "String membership can use repeated exact-key lookups or a prepared exact-key set.",
                LibraDexIndexProjectionKind.Exact),
            LibraDexConditionOperatorKind.InSet or
            LibraDexConditionOperatorKind.NotInSet => ClassifyTextProjection(leaf, index, LibraDexIndexProjectionKind.SortKey, "Case-insensitive string membership prefers a maintained sort-key projection."),
            _ => new LibraDexConditionLeafClassification(
                leaf.IndexName,
                leaf.ValueKind,
                leaf.Operator,
                LibraDexConditionExecutionClass.Unsupported,
                "The string operator has no current primitive or projection rule.",
                ProjectionKind: null)
        };
    }

    private static LibraDexConditionLeafClassification ClassifyTextProjection(
        LibraDexConditionLeafDescriptor leaf,
        IIndex index,
        LibraDexIndexProjectionKind projectionKind,
        string reason)
    {
        return ClassifyTextProjection(leaf, index, projectionKind, LibraDexIndexByteDirection.Forward, reason);
    }

    private static LibraDexConditionLeafClassification ClassifyTextProjection(
        LibraDexConditionLeafDescriptor leaf,
        IIndex index,
        LibraDexIndexProjectionKind projectionKind,
        LibraDexIndexByteDirection direction,
        string reason)
    {
        if (index.LogicalShape?.HasProjection(projectionKind, direction) == true)
        {
            return new LibraDexConditionLeafClassification(
                leaf.IndexName,
                leaf.ValueKind,
                leaf.Operator,
                LibraDexConditionExecutionClass.ProjectionBacked,
                reason,
                projectionKind);
        }

        return new LibraDexConditionLeafClassification(
            leaf.IndexName,
            leaf.ValueKind,
            leaf.Operator,
            LibraDexConditionExecutionClass.VisibleScanLike,
            $"{reason} The resolved shape does not declare that projection.",
            projectionKind);
    }
}

/// <summary>
/// Provides grouping helpers for a completed adopted condition.<br/>
/// The condition supplies candidate identities, and the grouping index supplies the key axis, keeping grouping attached to selection rather than root index retrieval.<br/>
/// </summary>
