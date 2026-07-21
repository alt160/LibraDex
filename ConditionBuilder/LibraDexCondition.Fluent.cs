using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace LibraDex;

public sealed class LibraDexConditionClause
{
    private readonly LibraDexConditionBuilder builder;
    private readonly bool negateNext;

    internal LibraDexConditionClause(LibraDexConditionBuilder builder, bool negateNext = false)
    {
        this.builder = builder;
        this.negateNext = negateNext;
    }

    /// <summary>
    /// Negates the next index or group clause selected from this condition clause.<br/>
    /// This is the canonical condition-builder negation form, so `.AND.Not.Where("status").AsString.EqualTo("Archived")` and `.AND.Not.Group(fragment)` both record a negated next clause instead of requiring each operator family to expose separate negative method names.<br/>
    /// </summary>
    public LibraDexConditionClause Not => new(builder, !negateNext);

    /// <summary>
    /// Selects the LibraDex index that owns the next condition leaf.<br/>
    /// This is the canonical predicate-target selector for descriptor-level conditions, matching the catalog-backed `catalog.IndexSet("group").Where("index")...` surface.<br/>
    /// The name is not resolved immediately; reusable conditions can be built first and resolved against opened indexes later.<br/>
    /// </summary>
    /// <param name="indexName">The index name inside the current identity group.</param>
    /// <returns>A value-type selector for the chosen index.</returns>
    public LibraDexConditionValueTypeSelector Where(string indexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        return new LibraDexConditionValueTypeSelector(builder, LibraDexConditionIndexSelector.Static(indexName), negateNext);
    }

    /// <summary>
    /// Selects the LibraDex index that owns the next condition leaf using the legacy selector name.<br/>
    /// Prefer <see cref="Where(string)"/> for predicate construction so condition code reads as group-then-where instead of group-then-index.<br/>
    /// </summary>
    /// <param name="indexName">The index name inside the current identity group.</param>
    /// <returns>A value-type selector for the chosen index.</returns>
    public LibraDexConditionValueTypeSelector Index(string indexName)
        => Where(indexName);

    /// <summary>
    /// Selects the LibraDex index for the next condition leaf using a deferred index-name factory.<br/>
    /// The factory is evaluated only when the condition is inspected or materialized, matching Abraxas' deferred proppath behavior while keeping LibraDex resolution index-name based.<br/>
    /// </summary>
    /// <param name="indexNameFactory">Factory that returns the index name inside the current identity group.</param>
    /// <param name="name">Optional selector name for replacement on reusable condition expressions.</param>
    /// <returns>A value-type selector for the chosen index.</returns>
    public LibraDexConditionValueTypeSelector Where(Func<string> indexNameFactory, string? name = null)
        => new LibraDexConditionValueTypeSelector(builder, LibraDexConditionIndexSelector.Deferred(indexNameFactory, name), negateNext);

    /// <summary>
    /// Selects the LibraDex index for the next condition leaf using a deferred index-name factory and the legacy selector name.<br/>
    /// Prefer <see cref="Where(Func{string}, string?)"/> for new descriptor-level predicate code.<br/>
    /// </summary>
    /// <param name="indexNameFactory">Factory that returns the index name inside the current identity group.</param>
    /// <param name="name">Optional selector name for replacement on reusable condition expressions.</param>
    /// <returns>A value-type selector for the chosen index.</returns>
    public LibraDexConditionValueTypeSelector Index(Func<string> indexNameFactory, string? name = null)
        => Where(indexNameFactory, name);

    /// <summary>
    /// Selects the LibraDex index for the next condition leaf using a prepared selector.<br/>
    /// This overload is the low-level bridge used by typed public wrappers that need named or deferred selector replacement.<br/>
    /// </summary>
    /// <param name="indexSelector">The static or deferred index selector.</param>
    /// <returns>A value-type selector for the chosen index.</returns>
    public LibraDexConditionValueTypeSelector Where(LibraDexConditionIndexSelector indexSelector)
        => new LibraDexConditionValueTypeSelector(builder, indexSelector, negateNext);

    /// <summary>
    /// Selects the LibraDex index for the next condition leaf using a prepared selector and the legacy selector name.<br/>
    /// Prefer <see cref="Where(LibraDexConditionIndexSelector)"/> for new descriptor-level predicate code.<br/>
    /// </summary>
    /// <param name="indexSelector">The static or deferred index selector.</param>
    /// <returns>A value-type selector for the chosen index.</returns>
    public LibraDexConditionValueTypeSelector Index(LibraDexConditionIndexSelector indexSelector)
        => Where(indexSelector);

    /// <summary>
    /// Adds an already completed grouped condition as the next node.<br/>
    /// The grouped condition must belong to the same identity group as this builder.<br/>
    /// </summary>
    /// <param name="groupCondition">The completed grouped condition.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd Group(LibraDexConditionEndCondition groupCondition)
    {
        ArgumentNullException.ThrowIfNull(groupCondition);
        return builder.AddGroup(groupCondition, negateNext);
    }

    /// <summary>
    /// Adds a caller-supplied identity filter as the next condition clause.<br/>
    /// This is the anchored identity-filter form: execution passes candidate identities from an indexed sibling branch into caller code and keeps only accepted identities.<br/>
    /// Use this form in an intersection shape such as `.And.External(id => ...)`; use `.External(ids)` or `.External(() => ids)` when caller code already owns the full identity stream.<br/>
    /// LibraDex does not load caller objects for this predicate; the identity remains the only value supplied by LibraDex.<br/>
    /// </summary>
    /// <param name="filter">Predicate that receives each candidate identity and returns whether it should remain in the result stream.<br/></param>
    /// <returns>A continuation for adding more clauses or ending the condition.<br/></returns>
    public LibraDexConditionContinueOrEnd External(Func<object, bool> filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return External(context => filter(context.Identity));
    }

    /// <summary>
    /// Adds a caller-supplied identity filter with stream-position values as the next condition clause.<br/>
    /// This is the anchored identity-filter form with lightweight stream metadata: execution passes candidate identities from an indexed sibling branch into caller code and keeps only accepted identities.<br/>
    /// The ordinal and `isFirst` values let callers initialize or reuse their own side-data caches at the start of filtering without requiring LibraDex to load source objects.<br/>
    /// Use this overload when the predicate benefits from request-local cache setup, batching state, or telemetry tied to the candidate stream.<br/>
    /// </summary>
    /// <param name="filter">Predicate that receives candidate identity, zero-based candidate ordinal, and first-candidate flag, then returns whether the identity should remain in the result stream.<br/></param>
    /// <returns>A continuation for adding more clauses or ending the condition.<br/></returns>
    public LibraDexConditionContinueOrEnd External(Func<object, long, bool, bool> filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return External(context => filter(context.Identity, context.Ordinal, context.IsFirst));
    }

    /// <summary>
    /// Adds a caller-supplied identity filter with a single context value as the next condition clause.<br/>
    /// This is the anchored identity-filter form with a named context value instead of a multi-argument lambda.<br/>
    /// The context exposes the candidate identity, zero-based ordinal, and `IsFirst` flag while keeping source-object loading caller-owned.<br/>
    /// Use this form for named delegates, generated code, or predicates that prefer a single strongly named parameter.<br/>
    /// </summary>
    /// <param name="filter">Predicate that receives candidate identity context and returns whether the identity should remain in the result stream.<br/></param>
    /// <returns>A continuation for adding more clauses or ending the condition.<br/></returns>
    public LibraDexConditionContinueOrEnd External(Func<LibraDexExternalIdentityContext, bool> filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        Func<LibraDexExternalIdentityContext, bool> effectiveFilter = negateNext
            ? context => !filter(context)
            : filter;
        return builder.AddExternal(effectiveFilter);
    }

    /// <summary>
    /// Adds caller-supplied identities as an external source clause.<br/>
    /// This node defines its own identity stream and can stand alone or compose through `Or`, `And`, and other identity-set operations.<br/>
    /// Use this form when caller code already has identities, not keys, and LibraDex should compose those identities with indexed criteria.<br/>
    /// This source form can execute without an indexed sibling; anchored predicate forms such as `.External(id => ...)` cannot.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The identity value type supplied by caller code.<br/></typeparam>
    /// <param name="identities">The identities to expose as an external source stream.<br/></param>
    /// <returns>A continuation for adding more clauses or ending the condition.<br/></returns>
    public LibraDexConditionContinueOrEnd External<TIdentity>(IEnumerable<TIdentity> identities)
    {
        ArgumentNullException.ThrowIfNull(identities);
        return External(() => identities);
    }

    /// <summary>
    /// Adds a caller-supplied identity source factory as an external source clause.<br/>
    /// The factory is invoked when the condition executes, allowing callers to use request-time caches, precomputed lists, or another storage engine as the identity source.<br/>
    /// This source form can execute by itself and can also compose with indexed conditions through `And` and `Or` because it supplies its own identity universe.<br/>
    /// The factory should return identities only; use `External<TKey, TIdentity>(...)` with `LibraDexExternalEntry<TKey, TIdentity>` when caller code needs LibraDex to apply key predicates to runtime key/identity pairs.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The identity value type supplied by caller code.<br/></typeparam>
    /// <param name="identityFactory">Factory that returns the identities to expose as an external source stream.<br/></param>
    /// <returns>A continuation for adding more clauses or ending the condition.<br/></returns>
    public LibraDexConditionContinueOrEnd External<TIdentity>(Func<IEnumerable<TIdentity>> identityFactory)
    {
        ArgumentNullException.ThrowIfNull(identityFactory);
        if (negateNext)
        {
            throw new NotSupportedException("External identity sources cannot be negated without an indexed universe; compose with an indexed condition and use Except-style semantics when that surface is available.");
        }

        return builder.AddExternalSource(() => BoxExternalIdentities(identityFactory()));
    }

    /// <summary>
    /// Adds caller-supplied external key/identity entries and selects key operators for the external branch.<br/>
    /// The selected key operators filter the external entries before their identities are composed with the rest of the condition tree.<br/>
    /// This is the runtime-index entry form: caller code supplies both keys and identities, and LibraDex applies operators such as `.EqualTo(...)`, `.Between(...)`, and `.InSet(...)` to the caller-owned keys.<br/>
    /// Use this form when caller-owned data behaves like a temporary index for the current condition.<br/>
    /// </summary>
    /// <typeparam name="TKey">The external branch key type.<br/></typeparam>
    /// <typeparam name="TIdentity">The identity type associated with the external entries.<br/></typeparam>
    /// <param name="entries">The external key/identity entries to expose as a runtime index-like branch.<br/></param>
    /// <returns>A typed external condition operator for the supplied key type.<br/></returns>
    public LibraDexExternalConditionOperator<TKey> External<TKey, TIdentity>(IEnumerable<LibraDexExternalEntry<TKey, TIdentity>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return External<TKey, TIdentity>(() => entries);
    }

    /// <summary>
    /// Adds caller-supplied external key/identity pairs and selects key operators for the external branch.<br/>
    /// This overload accepts the standard .NET key/value pair shape and treats the pair value as the typed LibraDex identity for the surrounding identity group.<br/>
    /// The selected key operators filter the external pairs before their identities are composed with the rest of the condition tree.<br/>
    /// </summary>
    /// <typeparam name="TKey">The external branch key type.<br/></typeparam>
    /// <typeparam name="TIdentity">The identity type associated with the external pairs.<br/></typeparam>
    /// <param name="entries">The external key/identity pairs to expose as a runtime index-like branch.<br/></param>
    /// <returns>A typed external condition operator for the supplied key type.<br/></returns>
    public LibraDexExternalConditionOperator<TKey> External<TKey, TIdentity>(IEnumerable<KeyValuePair<TKey, TIdentity>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return External<TKey, TIdentity>(() => entries);
    }

    /// <summary>
    /// Adds a caller-supplied external key/identity entry source and selects key operators for the external branch.<br/>
    /// This is the runtime-index form: caller code supplies entries, LibraDex applies the selected external key predicate, and matching identities participate in normal condition composition.<br/>
    /// The factory is invoked when the condition executes, which allows the runtime key/identity source to be request-local or backed by another storage engine.<br/>
    /// Use `LibraDexExternalEntry<TKey, TIdentity>` entries here so the condition can materialize identities after filtering the caller-owned keys.<br/>
    /// </summary>
    /// <typeparam name="TKey">The external branch key type.<br/></typeparam>
    /// <typeparam name="TIdentity">The identity type associated with the external entries.<br/></typeparam>
    /// <param name="entryFactory">Factory that returns external key/identity entries.<br/></param>
    /// <returns>A typed external condition operator for the supplied key type.<br/></returns>
    public LibraDexExternalConditionOperator<TKey> External<TKey, TIdentity>(Func<IEnumerable<LibraDexExternalEntry<TKey, TIdentity>>> entryFactory)
    {
        ArgumentNullException.ThrowIfNull(entryFactory);
        return new LibraDexExternalConditionOperator<TKey>(builder, negateNext, () => BoxExternalEntries(entryFactory()), candidateKeyFactory: null);
    }

    /// <summary>
    /// Adds a caller-supplied external key/identity pair source and selects key operators for the external branch.<br/>
    /// This overload accepts the standard .NET key/value pair shape and treats the pair value as the typed LibraDex identity for the surrounding identity group.<br/>
    /// The factory is invoked when the condition executes, allowing caller-owned runtime indexes to stay request-local.<br/>
    /// </summary>
    /// <typeparam name="TKey">The external branch key type.<br/></typeparam>
    /// <typeparam name="TIdentity">The identity type associated with the external pairs.<br/></typeparam>
    /// <param name="entryFactory">Factory that returns external key/identity pairs.<br/></param>
    /// <returns>A typed external condition operator for the supplied key type.<br/></returns>
    public LibraDexExternalConditionOperator<TKey> External<TKey, TIdentity>(Func<IEnumerable<KeyValuePair<TKey, TIdentity>>> entryFactory)
    {
        ArgumentNullException.ThrowIfNull(entryFactory);
        return new LibraDexExternalConditionOperator<TKey>(builder, negateNext, () => BoxExternalPairs(entryFactory()), candidateKeyFactory: null);
    }

    /// <summary>
    /// Adds a correlated caller-supplied external key source and selects key operators for the external branch.<br/>
    /// The factory receives each candidate identity from the indexed sibling branch and returns external keys for that identity; the selected key predicate decides whether the identity remains matched.<br/>
    /// This is the anchored correlated-key form: use it with an indexed sibling branch such as `.And.External<TKey>(id => keys).Between(...)` so LibraDex has candidate identities to ask about.<br/>
    /// Use the runtime-index entry form instead when caller code already has key/identity pairs and does not need a candidate identity anchor.<br/>
    /// </summary>
    /// <typeparam name="TKey">The external branch key type.<br/></typeparam>
    /// <param name="candidateKeyFactory">Factory that returns external keys for one candidate identity.<br/></param>
    /// <returns>A typed external condition operator for the supplied key type.<br/></returns>
    public LibraDexExternalConditionOperator<TKey> External<TKey>(Func<object, IEnumerable<TKey>> candidateKeyFactory)
    {
        ArgumentNullException.ThrowIfNull(candidateKeyFactory);
        return new LibraDexExternalConditionOperator<TKey>(builder, negateNext, entryFactory: null, candidateKeyFactory);
    }

    /// <summary>
    /// Boxes one caller-supplied identity stream into the non-generic execution stream used by condition composition.<br/>
    /// The source is enumerated lazily so caller-owned streams do not materialize before LibraDex begins executing the condition.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The caller identity type.<br/></typeparam>
    /// <param name="identities">The caller identity stream.<br/></param>
    /// <returns>A boxed identity stream.</returns>
    private static IEnumerable<object> BoxExternalIdentities<TIdentity>(IEnumerable<TIdentity> identities)
    {
        ArgumentNullException.ThrowIfNull(identities);
        foreach (TIdentity identity in identities)
        {
            object? boxed = identity;
            yield return boxed ?? throw new InvalidOperationException("External identity sources cannot yield null identities.");
        }
    }

    /// <summary>
    /// Boxes one caller-supplied typed key/identity entry stream into the internal runtime-entry stream used by condition composition.<br/>
    /// Public callers keep typed identity values, while the descriptor boundary stores identities as objects for mixed-condition execution.<br/>
    /// </summary>
    /// <typeparam name="TKey">The external branch key type.<br/></typeparam>
    /// <typeparam name="TIdentity">The caller identity type.<br/></typeparam>
    /// <param name="entries">The caller external key/identity entries.<br/></param>
    /// <returns>An internal external entry stream.</returns>
    private static IEnumerable<LibraDexExternalEntryInternal<TKey>> BoxExternalEntries<TKey, TIdentity>(IEnumerable<LibraDexExternalEntry<TKey, TIdentity>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        foreach (LibraDexExternalEntry<TKey, TIdentity> entry in entries)
        {
            object? identity = entry.Identity;
            yield return new LibraDexExternalEntryInternal<TKey>(
                entry.Key,
                identity ?? throw new InvalidOperationException("External entries cannot yield null identities."));
        }
    }

    /// <summary>
    /// Boxes one caller-supplied typed key/value pair stream into the internal runtime-entry stream used by condition composition.<br/>
    /// Pair keys are external branch keys, and pair values are the typed LibraDex identities to compose after key filtering.<br/>
    /// </summary>
    /// <typeparam name="TKey">The external branch key type.<br/></typeparam>
    /// <typeparam name="TIdentity">The caller identity type.<br/></typeparam>
    /// <param name="entries">The caller external key/identity pairs.<br/></param>
    /// <returns>An internal external entry stream.</returns>
    private static IEnumerable<LibraDexExternalEntryInternal<TKey>> BoxExternalPairs<TKey, TIdentity>(IEnumerable<KeyValuePair<TKey, TIdentity>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        foreach (KeyValuePair<TKey, TIdentity> entry in entries)
        {
            object? identity = entry.Value;
            yield return new LibraDexExternalEntryInternal<TKey>(
                entry.Key,
                identity ?? throw new InvalidOperationException("External entries cannot yield null identities."));
        }
    }
}

/// <summary>
/// Represents one internal external key/identity entry after public typed identities have crossed the descriptor boundary.<br/>
/// Public APIs keep identity type safety; this internal shape lets the condition executor compose mixed identity streams through its existing object channel.<br/>
/// </summary>
/// <typeparam name="TKey">The external branch key type.<br/></typeparam>
internal readonly record struct LibraDexExternalEntryInternal<TKey>(TKey Key, object Identity);

/// <summary>
/// Captures typed key predicates for one external runtime condition branch.<br/>
/// External source branches filter caller-supplied key/identity entries into identities; correlated branches filter the current indexed candidate identity by asking caller code for that identity's external keys.<br/>
/// </summary>
/// <typeparam name="TKey">The external branch key type.<br/></typeparam>
public sealed class LibraDexExternalConditionOperator<TKey>
{
    private readonly LibraDexConditionBuilder builder;
    private readonly bool negate;
    private readonly Func<IEnumerable<LibraDexExternalEntryInternal<TKey>>>? entryFactory;
    private readonly Func<object, IEnumerable<TKey>>? candidateKeyFactory;

    internal LibraDexExternalConditionOperator(
        LibraDexConditionBuilder builder,
        bool negate,
        Func<IEnumerable<LibraDexExternalEntryInternal<TKey>>>? entryFactory,
        Func<object, IEnumerable<TKey>>? candidateKeyFactory)
    {
        this.builder = builder;
        this.negate = negate;
        this.entryFactory = entryFactory;
        this.candidateKeyFactory = candidateKeyFactory;
    }

    /// <summary>
    /// Negates the next external key predicate.<br/>
    /// The negation is applied inside the external branch before identities are composed with the surrounding condition.<br/>
    /// </summary>
    public LibraDexExternalConditionOperator<TKey> Not => new(builder, !negate, entryFactory, candidateKeyFactory);

    /// <summary>
    /// Captures external keys equal to <paramref name="value"/>.<br/>
    /// </summary>
    /// <param name="value">The key value to match.<br/></param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd EqualTo(TKey value)
        => Add(key => LibraDexKeyEquality<TKey>.Comparer.Equals(key, value));

    /// <summary>
    /// Captures external keys not equal to <paramref name="value"/>.<br/>
    /// </summary>
    /// <param name="value">The key value to exclude.<br/></param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotEqualTo(TKey value)
        => Not.EqualTo(value);

    /// <summary>
    /// Captures external keys greater than <paramref name="value"/>.<br/>
    /// </summary>
    /// <param name="value">The exclusive lower key boundary.<br/></param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd GreaterThan(TKey value)
        => Add(key => Compare(key, value) > 0);

    /// <summary>
    /// Captures external keys greater than or equal to <paramref name="value"/>.<br/>
    /// </summary>
    /// <param name="value">The inclusive lower key boundary.<br/></param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd GreaterOrEqual(TKey value)
        => Add(key => Compare(key, value) >= 0);

    /// <summary>
    /// Captures external keys less than <paramref name="value"/>.<br/>
    /// </summary>
    /// <param name="value">The exclusive upper key boundary.<br/></param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd LessThan(TKey value)
        => Add(key => Compare(key, value) < 0);

    /// <summary>
    /// Captures external keys less than or equal to <paramref name="value"/>.<br/>
    /// </summary>
    /// <param name="value">The inclusive upper key boundary.<br/></param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd LessOrEqual(TKey value)
        => Add(key => Compare(key, value) <= 0);

    /// <summary>
    /// Captures external keys within the inclusive <paramref name="lower"/> to <paramref name="upper"/> range.<br/>
    /// </summary>
    /// <param name="lower">The inclusive lower key boundary.<br/></param>
    /// <param name="upper">The inclusive upper key boundary.<br/></param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd Between(TKey lower, TKey upper)
        => Add(key => Compare(key, lower) >= 0 && Compare(key, upper) <= 0);

    /// <summary>
    /// Captures external keys outside the inclusive <paramref name="lower"/> to <paramref name="upper"/> range.<br/>
    /// </summary>
    /// <param name="lower">The inclusive lower key boundary.<br/></param>
    /// <param name="upper">The inclusive upper key boundary.<br/></param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotBetween(TKey lower, TKey upper)
        => Not.Between(lower, upper);

    /// <summary>
    /// Captures external keys contained in <paramref name="values"/>.<br/>
    /// </summary>
    /// <param name="values">The external key membership set.<br/></param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd InSet(IEnumerable<TKey> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        HashSet<TKey> set = new(values, LibraDexKeyEquality<TKey>.Comparer);
        return Add(set.Contains);
    }

    /// <summary>
    /// Captures external keys not contained in <paramref name="values"/>.<br/>
    /// </summary>
    /// <param name="values">The external key membership set.<br/></param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotInSet(IEnumerable<TKey> values)
        => Not.InSet(values);

    /// <summary>
    /// Captures one compiled external key predicate as either an identity source or an identity filter.<br/>
    /// Runtime-entry branches emit identities for entries whose keys satisfy the predicate; correlated branches test candidate identities from an indexed sibling stream.<br/>
    /// </summary>
    /// <param name="predicate">The external key predicate to apply before identity composition.<br/></param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    private LibraDexConditionContinueOrEnd Add(Func<TKey, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        Func<TKey, bool> effectivePredicate = negate
            ? key => !predicate(key)
            : predicate;
        if (entryFactory is not null)
        {
            return builder.AddExternalSource(() => FilterEntries(entryFactory(), effectivePredicate));
        }

        Func<object, IEnumerable<TKey>> keyFactory = candidateKeyFactory
            ?? throw new InvalidOperationException("External key predicate is missing its source.");
        return builder.AddExternal(context => MatchesAny(keyFactory(context.Identity), effectivePredicate));
    }

    /// <summary>
    /// Filters caller-supplied external entries by key and yields only their identities.<br/>
    /// This keeps external branch keys local to the branch while the condition tree continues to compose identity streams.<br/>
    /// </summary>
    /// <param name="entries">The external key/identity entries supplied by caller code.<br/></param>
    /// <param name="predicate">The external key predicate selected by the fluent operator.<br/></param>
    /// <returns>Identities whose external entries satisfy the predicate.<br/></returns>
    private static IEnumerable<object> FilterEntries(IEnumerable<LibraDexExternalEntryInternal<TKey>> entries, Func<TKey, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(entries);
        foreach (LibraDexExternalEntryInternal<TKey> entry in entries)
        {
            if (entry.Identity is null)
            {
                throw new InvalidOperationException("External entries cannot yield null identities.");
            }

            if (predicate(entry.Key))
            {
                yield return entry.Identity;
            }
        }
    }

    /// <summary>
    /// Tests whether any caller-supplied external key for one candidate identity satisfies the selected predicate.<br/>
    /// The method short-circuits on the first match so correlated branches can avoid enumerating unnecessary side-data keys.<br/>
    /// </summary>
    /// <param name="keys">The external keys associated with one candidate identity.<br/></param>
    /// <param name="predicate">The selected external key predicate.<br/></param>
    /// <returns><see langword="true"/> when any key satisfies the predicate.<br/></returns>
    private static bool MatchesAny(IEnumerable<TKey> keys, Func<TKey, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(keys);
        foreach (TKey key in keys)
        {
            if (predicate(key))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Compares two external keys using the default comparer for the selected key type.<br/>
    /// External ordered predicates require caller-supplied key types to support this comparer, matching ordinary in-memory .NET range semantics.<br/>
    /// </summary>
    /// <param name="left">The left key value.<br/></param>
    /// <param name="right">The right key value.<br/></param>
    /// <returns>The comparer result for the two key values.<br/></returns>
    private static int Compare(TKey left, TKey right)
        => Comparer<TKey>.Default.Compare(left, right);
}

/// <summary>
/// Selects the value family for an adopted condition leaf.<br/>
/// The selected value family records adapter intent and chooses typed operator overloads, while final validation still happens against the resolved LibraDex index key type.<br/>
/// </summary>
public sealed class LibraDexConditionValueTypeSelector
{
    private readonly LibraDexConditionBuilder builder;
    private readonly LibraDexConditionIndexSelector indexSelector;
    private readonly bool negate;

    internal LibraDexConditionValueTypeSelector(LibraDexConditionBuilder builder, LibraDexConditionIndexSelector indexSelector, bool negate = false)
    {
        ArgumentNullException.ThrowIfNull(indexSelector);
        this.builder = builder;
        this.indexSelector = indexSelector;
        this.negate = negate;
    }

    /// <summary>
    /// Negates the next selected value-family predicate over this index.<br/>
    /// This is a descriptor-level convenience for cross-index and same-index catalog conditions, so `.Where("status").Not.AsString.EqualTo("archived")` records the same leaf as `.Where("status").AsString.NotEqualTo("archived")` where an inverse operator exists.<br/>
    /// </summary>
    public LibraDexConditionValueTypeSelector Not => new(builder, indexSelector, !negate);

    /// <summary>
    /// Selects operators from a known CLR key type supplied by a typed index handle.<br/>
    /// This supports `.Where(indexInstance).EqualTo(value)` style call sites for generic scalar indexes while richer family-specific selectors such as `.AsString` remain available when string, binary, GUID, or date-only APIs are needed.<br/>
    /// </summary>
    /// <typeparam name="TValue">The CLR key type carried by the selected index handle.</typeparam>
    /// <returns>A typed condition operator for the selected key type.</returns>
    public LibraDexConditionOperator<TValue> As<TValue>()
        => new LibraDexConditionOperator<TValue>(builder, indexSelector, ResolveValueKind(typeof(TValue)), negate);

    /// <summary>
    /// Selects string operators for the current index.<br/>
    /// </summary>
    public LibraDexStringConditionOperator AsString => new(builder, indexSelector, negate);

    /// <summary>
    /// Selects binary operators for the current index.<br/>
    /// </summary>
    public LibraDexBinaryConditionOperator AsBinary => new(builder, indexSelector, negate);

    /// <summary>
    /// Selects Boolean operators for the current index.<br/>
    /// </summary>
    public LibraDexConditionOperator<bool> AsBoolean => new(builder, indexSelector, LibraDexConditionValueKind.Boolean, negate);

    /// <summary>
    /// Selects GUID operators for the current index.<br/>
    /// </summary>
    public LibraDexGuidConditionOperator AsGuid => new(builder, indexSelector, negate);

    /// <summary>
    /// Selects DateTime operators for the current index.<br/>
    /// </summary>
    public LibraDexDateConditionOperator<DateTime> AsDate => new(builder, indexSelector, LibraDexConditionValueKind.DateTime, negate);

    /// <summary>
    /// Selects DateTimeOffset operators for the current index.<br/>
    /// Full-value comparisons use the exact encoded date/time key, while structured date branches normalize through the same UTC packed format used by DateTimeOffset indexes.<br/>
    /// </summary>
    public LibraDexDateConditionOperator<DateTimeOffset> AsDateTimeOffset => new(builder, indexSelector, LibraDexConditionValueKind.DateTime, negate);

    /// <summary>
    /// Selects DateOnly operators for the current index.<br/>
    /// </summary>
    public LibraDexDateConditionOperator<DateOnly> AsDateOnly => new(builder, indexSelector, LibraDexConditionValueKind.DateOnly, negate);

    /// <summary>
    /// Selects TimeOnly operators for the current index.<br/>
    /// </summary>
    public LibraDexDateConditionOperator<TimeOnly> AsTimeOnly => new(builder, indexSelector, LibraDexConditionValueKind.TimeOnly, negate);

    /// <summary>
    /// Selects TimeSpan operators for the current index.<br/>
    /// </summary>
    public LibraDexConditionOperator<TimeSpan> AsTimeSpan => new(builder, indexSelector, LibraDexConditionValueKind.TimeSpan, negate);

    /// <summary>
    /// Selects numeric operators for an Int32 key or projection.<br/>
    /// </summary>
    public LibraDexNumericConditionOperator<int> AsInt32 => new(builder, indexSelector, negate);

    /// <summary>
    /// Selects numeric operators for a Byte key or projection.<br/>
    /// Byte conditions collapse to the same ordered scalar primitive route as wider numeric keys while preserving the developer-facing operand type in the descriptor.<br/>
    /// </summary>
    public LibraDexNumericConditionOperator<byte> AsByte => new(builder, indexSelector, negate);

    /// <summary>
    /// Selects numeric operators for an SByte key or projection.<br/>
    /// Signed byte conditions use the existing sortable signed-scalar encoding at execution time, so range and membership operators remain ordered without query-time conversion.<br/>
    /// </summary>
    public LibraDexNumericConditionOperator<sbyte> AsSByte => new(builder, indexSelector, negate);

    /// <summary>
    /// Selects numeric operators for an Int16 key or projection.<br/>
    /// The selector keeps copied/generated condition code strongly typed while materialization still resolves against the opened LibraDex index key contract.<br/>
    /// </summary>
    public LibraDexNumericConditionOperator<short> AsInt16 => new(builder, indexSelector, negate);

    /// <summary>
    /// Selects numeric operators for a UInt16 key or projection.<br/>
    /// UInt16 values route through the same exact, boundary, range, and membership bridge used by other scalar numeric keys.<br/>
    /// </summary>
    public LibraDexNumericConditionOperator<ushort> AsUInt16 => new(builder, indexSelector, negate);

    /// <summary>
    /// Selects numeric operators for an Int64 key or projection.<br/>
    /// </summary>
    public LibraDexNumericConditionOperator<long> AsInt64 => new(builder, indexSelector, negate);

    /// <summary>
    /// Selects numeric operators for a UInt32 key or projection.<br/>
    /// This fills the common unsigned-width selector gap without adding a new primitive: materialization remains an ordered scalar condition leaf.<br/>
    /// </summary>
    public LibraDexNumericConditionOperator<uint> AsUInt32 => new(builder, indexSelector, negate);

    /// <summary>
    /// Selects numeric operators for a UInt64 key or projection.<br/>
    /// </summary>
    public LibraDexNumericConditionOperator<ulong> AsUInt64 => new(builder, indexSelector, negate);

    /// <summary>
    /// Selects numeric operators for an Int128 key or projection.<br/>
    /// Int128 conditions use the same ordered primitive bridge as other scalar keys; the resolved index owns the signed fixed-16 sortable encoding.<br/>
    /// </summary>
    public LibraDexNumericConditionOperator<Int128> AsInt128 => new(builder, indexSelector, negate);

    /// <summary>
    /// Selects numeric operators for a UInt128 key or projection.<br/>
    /// UInt128 conditions use the same ordered primitive bridge as other scalar keys; the resolved index owns the unsigned fixed-16 big-endian encoding.<br/>
    /// </summary>
    public LibraDexNumericConditionOperator<UInt128> AsUInt128 => new(builder, indexSelector, negate);

    /// <summary>
    /// Selects numeric operators for a BigInteger key or projection.<br/>
    /// BigInteger conditions use the same ordered primitive condition shape as fixed-width scalar keys, while the resolved index owns the sortable BigInt byte encoding and max-width validation.<br/>
    /// </summary>
    public LibraDexNumericConditionOperator<BigInteger> AsBigInteger => new(builder, indexSelector, negate);

    /// <summary>
    /// Selects scalar operators for a Char key or projection.<br/>
    /// Char keys are treated as ordered scalar code-unit values, matching the current generic scalar codec rather than text collation semantics.<br/>
    /// </summary>
    public LibraDexConditionOperator<char> AsChar => new(builder, indexSelector, LibraDexConditionValueKind.Numeric, negate);

    /// <summary>
    /// Captures routed composite-key predicates for the current index.<br/>
    /// The selected index must be a composite index; each key-part predicate is captured as part of one composite-match leaf so execution can traverse the declared tier routes instead of flattening values into one synthetic key.<br/>
    /// </summary>
    /// <param name="keyParts">The composite key-part predicates to apply as one composite-match leaf.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd Where(params LibraDexCompositePartCriterion[] keyParts)
        => CompositeWhereRoot.Where(keyParts);

    internal LibraDexCompositeConditionWhere CompositeWhereRoot => new(builder, indexSelector);

    private static LibraDexConditionValueKind ResolveValueKind(Type keyType)
    {
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
/// Builds composite-key predicates for one logical routed composite index.<br/>
/// This is exposed directly as `.Where` from a composite index selector so callers do not have to restate that the selected index is composite before describing key-part intent.<br/>
/// </summary>

public sealed class LibraDexNumericConditionOperator<TValue> : LibraDexConditionOperator<TValue>
{
    internal LibraDexNumericConditionOperator(LibraDexConditionBuilder builder, LibraDexConditionIndexSelector indexSelector, bool negate = false)
        : base(builder, indexSelector, LibraDexConditionValueKind.Numeric, negate)
    {
    }

    /// <summary>
    /// Negates the next numeric predicate over the selected index.<br/>
    /// This preserves numeric-specific helpers such as bitmask predicates while letting typed-handle syntax use `.Not` without falling back to `.As...` selectors.<br/>
    /// </summary>
    public new LibraDexNumericConditionOperator<TValue> Not => new(Builder, IndexSelector, !IsNegated);

    /// <summary>
    /// Captures a bitwise-AND equality predicate over the selected numeric index.<br/>
    /// The condition means `(storedValue &amp; bitMask) == equalTo` and materializes as an explicit compact-index scan unless a future optimizer can narrow the key space safely.<br/>
    /// </summary>
    /// <param name="bitMask">The mask applied to each stored key value.</param>
    /// <param name="equalTo">The expected masked value.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd BitAnd(TValue bitMask, TValue equalTo)
    {
        return Add(
            LibraDexConditionOperatorKind.BitAndEqualTo,
            LibraDexConditionOperand.Value(bitMask),
            LibraDexConditionOperand.Value(equalTo));
    }

    /// <summary>
    /// Captures a bitwise-AND equality predicate over the selected numeric index with deferred operands.<br/>
    /// Both factories are invoked only when the completed condition is materialized, preserving reusable condition fragments without re-recording the builder chain.<br/>
    /// </summary>
    /// <param name="bitMask">The deferred mask factory.</param>
    /// <param name="equalTo">The deferred expected masked-value factory.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd BitAnd(Func<TValue> bitMask, Func<TValue> equalTo)
    {
        ArgumentNullException.ThrowIfNull(bitMask);
        ArgumentNullException.ThrowIfNull(equalTo);
        return Add(
            LibraDexConditionOperatorKind.BitAndEqualTo,
            LibraDexConditionOperand.Deferred(() => bitMask()),
            LibraDexConditionOperand.Deferred(() => equalTo()));
    }

    /// <summary>
    /// Captures a bitwise-AND equality predicate over the selected numeric index with a deferred mask.<br/>
    /// The mask factory is invoked when the condition materializes; the comparison operand is captured statically.<br/>
    /// </summary>
    /// <param name="bitMask">The deferred mask factory.</param>
    /// <param name="equalTo">The expected masked value.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd BitAnd(Func<TValue> bitMask, TValue equalTo)
    {
        ArgumentNullException.ThrowIfNull(bitMask);
        return Add(
            LibraDexConditionOperatorKind.BitAndEqualTo,
            LibraDexConditionOperand.Deferred(() => bitMask()),
            LibraDexConditionOperand.Value(equalTo));
    }

    /// <summary>
    /// Captures a bitwise-AND equality predicate over the selected numeric index with a deferred comparison value.<br/>
    /// The comparison factory is invoked when the condition materializes; the mask is captured statically.<br/>
    /// </summary>
    /// <param name="bitMask">The mask applied to each stored key value.</param>
    /// <param name="equalTo">The deferred expected masked-value factory.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd BitAnd(TValue bitMask, Func<TValue> equalTo)
    {
        ArgumentNullException.ThrowIfNull(equalTo);
        return Add(
            LibraDexConditionOperatorKind.BitAndEqualTo,
            LibraDexConditionOperand.Value(bitMask),
            LibraDexConditionOperand.Deferred(() => equalTo()));
    }

    /// <summary>
    /// Captures a bitwise-AND zero predicate over the selected numeric index.<br/>
    /// The condition means `(storedValue &amp; bitMask) == default(TValue)` and mirrors Abraxas' default comparison operand while keeping the scan-backed execution explicit.<br/>
    /// </summary>
    /// <param name="bitMask">The mask applied to each stored key value.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd BitAnd(TValue bitMask)
    {
        return Add(
            LibraDexConditionOperatorKind.BitAndEqualTo,
            LibraDexConditionOperand.Value(bitMask),
            LibraDexConditionOperand.Value(default(TValue)!));
    }

    /// <summary>
    /// Captures a bitwise-AND inequality predicate over the selected numeric index.<br/>
    /// The condition means `(storedValue &amp; bitMask) != notEqualTo`; this is the primitive form used for "any selected bit is set" without inventing a separate index structure.<br/>
    /// </summary>
    /// <param name="bitMask">The mask applied to each stored key value.</param>
    /// <param name="notEqualTo">The masked value that must not match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd BitAndNotEqualTo(TValue bitMask, TValue notEqualTo)
    {
        return Add(
            LibraDexConditionOperatorKind.BitAndNotEqualTo,
            LibraDexConditionOperand.Value(bitMask),
            LibraDexConditionOperand.Value(notEqualTo));
    }

    /// <summary>
    /// Captures a bitwise-AND inequality predicate over the selected numeric index with deferred operands.<br/>
    /// Both factories are invoked only when the completed condition is materialized.<br/>
    /// </summary>
    /// <param name="bitMask">The deferred mask factory.</param>
    /// <param name="notEqualTo">The deferred masked value that must not match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd BitAndNotEqualTo(Func<TValue> bitMask, Func<TValue> notEqualTo)
    {
        ArgumentNullException.ThrowIfNull(bitMask);
        ArgumentNullException.ThrowIfNull(notEqualTo);
        return Add(
            LibraDexConditionOperatorKind.BitAndNotEqualTo,
            LibraDexConditionOperand.Deferred(() => bitMask()),
            LibraDexConditionOperand.Deferred(() => notEqualTo()));
    }

    /// <summary>
    /// Captures a bitwise-AND inequality predicate over the selected numeric index with a deferred mask.<br/>
    /// The mask factory is invoked when the condition materializes; the comparison operand is captured statically.<br/>
    /// </summary>
    /// <param name="bitMask">The deferred mask factory.</param>
    /// <param name="notEqualTo">The masked value that must not match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd BitAndNotEqualTo(Func<TValue> bitMask, TValue notEqualTo)
    {
        ArgumentNullException.ThrowIfNull(bitMask);
        return Add(
            LibraDexConditionOperatorKind.BitAndNotEqualTo,
            LibraDexConditionOperand.Deferred(() => bitMask()),
            LibraDexConditionOperand.Value(notEqualTo));
    }

    /// <summary>
    /// Captures a bitwise-AND inequality predicate over the selected numeric index with a deferred comparison value.<br/>
    /// The comparison factory is invoked when the condition materializes; the mask is captured statically.<br/>
    /// </summary>
    /// <param name="bitMask">The mask applied to each stored key value.</param>
    /// <param name="notEqualTo">The deferred masked value that must not match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd BitAndNotEqualTo(TValue bitMask, Func<TValue> notEqualTo)
    {
        ArgumentNullException.ThrowIfNull(notEqualTo);
        return Add(
            LibraDexConditionOperatorKind.BitAndNotEqualTo,
            LibraDexConditionOperand.Value(bitMask),
            LibraDexConditionOperand.Deferred(() => notEqualTo()));
    }

    /// <summary>
    /// Captures a predicate requiring every bit in <paramref name="bitMask"/> to be set.<br/>
    /// This is a readability wrapper for <see cref="BitAnd(TValue, TValue)"/> where the comparison value is the same mask.<br/>
    /// </summary>
    /// <param name="bitMask">The mask whose bits must all be present in each stored key value.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd AllBitsSet(TValue bitMask)
        => BitAnd(bitMask, bitMask);

    /// <summary>
    /// Captures a deferred predicate requiring every bit in the runtime mask to be set.<br/>
    /// The mask factory is evaluated when the completed condition materializes and is used for both the AND mask and comparison operand.<br/>
    /// </summary>
    /// <param name="bitMask">The deferred mask factory.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd AllBitsSet(Func<TValue> bitMask)
    {
        ArgumentNullException.ThrowIfNull(bitMask);
        return Add(
            LibraDexConditionOperatorKind.BitAndEqualTo,
            LibraDexConditionOperand.Deferred(() => bitMask()),
            LibraDexConditionOperand.Deferred(() => bitMask()));
    }

    /// <summary>
    /// Captures a predicate requiring at least one bit in <paramref name="bitMask"/> to be set.<br/>
    /// This is a readability wrapper for `(storedValue &amp; bitMask) != default(TValue)` and normally executes as a visible compact-index scan.<br/>
    /// </summary>
    /// <param name="bitMask">The mask whose bits are tested for any overlap.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd AnyBitsSet(TValue bitMask)
    {
        return Add(
            LibraDexConditionOperatorKind.BitAndNotEqualTo,
            LibraDexConditionOperand.Value(bitMask),
            LibraDexConditionOperand.Value(default(TValue)!));
    }

    /// <summary>
    /// Captures a deferred predicate requiring at least one bit in the runtime mask to be set.<br/>
    /// The mask factory is evaluated when the completed condition materializes.<br/>
    /// </summary>
    /// <param name="bitMask">The deferred mask factory.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd AnyBitsSet(Func<TValue> bitMask)
    {
        ArgumentNullException.ThrowIfNull(bitMask);
        return Add(
            LibraDexConditionOperatorKind.BitAndNotEqualTo,
            LibraDexConditionOperand.Deferred(() => bitMask()),
            LibraDexConditionOperand.Value(default(TValue)!));
    }

    /// <summary>
    /// Captures a predicate requiring no bits in <paramref name="bitMask"/> to be set.<br/>
    /// This is a readability wrapper for `(storedValue &amp; bitMask) == default(TValue)` and preserves the caller's bit-pattern intent for signed numeric keys.<br/>
    /// </summary>
    /// <param name="bitMask">The mask whose bits must not overlap each stored key value.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NoBitsSet(TValue bitMask)
    {
        return Add(
            LibraDexConditionOperatorKind.BitAndEqualTo,
            LibraDexConditionOperand.Value(bitMask),
            LibraDexConditionOperand.Value(default(TValue)!));
    }

    /// <summary>
    /// Captures a deferred predicate requiring no bits in the runtime mask to be set.<br/>
    /// The mask factory is evaluated when the completed condition materializes.<br/>
    /// </summary>
    /// <param name="bitMask">The deferred mask factory.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NoBitsSet(Func<TValue> bitMask)
    {
        ArgumentNullException.ThrowIfNull(bitMask);
        return Add(
            LibraDexConditionOperatorKind.BitAndEqualTo,
            LibraDexConditionOperand.Deferred(() => bitMask()),
            LibraDexConditionOperand.Value(default(TValue)!));
    }
}

/// <summary>
/// Captures typed comparison operators for one adopted condition leaf.<br/>
/// The operator methods intentionally mirror Abraxas' low-friction grammar, but they only record descriptor intent until a LibraDex index resolver is supplied.<br/>
/// </summary>
/// <typeparam name="TValue">The typed operand value accepted by this operator chain.</typeparam>
public class LibraDexConditionOperator<TValue>
{
    private readonly LibraDexConditionBuilder builder;
    private readonly LibraDexConditionIndexSelector indexSelector;
    private readonly LibraDexConditionValueKind valueKind;
    private readonly bool negate;

    internal LibraDexConditionOperator(LibraDexConditionBuilder builder, LibraDexConditionIndexSelector indexSelector, LibraDexConditionValueKind valueKind, bool negate = false)
    {
        this.builder = builder;
        this.indexSelector = indexSelector;
        this.valueKind = valueKind;
        this.negate = negate;
    }

    /// <summary>
    /// Negates the next predicate over the selected typed index.<br/>
    /// The negation maps to an existing inverse descriptor, such as `EqualTo` to `NotEqualTo`, rather than adding a separate execution tree node.<br/>
    /// </summary>
    public LibraDexConditionOperator<TValue> Not => new(builder, indexSelector, valueKind, !negate);

    private protected LibraDexConditionBuilder Builder => builder;

    private protected LibraDexConditionIndexSelector IndexSelector => indexSelector;

    private protected bool IsNegated => negate;

    internal LibraDexConditionContinueOrEnd All()
        => Add(LibraDexConditionOperatorKind.All);

    /// <summary>
    /// Captures equality against a static value.<br/>
    /// </summary>
    /// <param name="value">The value to compare with the selected index key.</param>
    /// <param name="name">Optional replacement name for reusable condition templates.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd EqualTo(TValue value, string? name = null)
        => Add(LibraDexConditionOperatorKind.EqualTo, LibraDexConditionOperand.Value(value, name));

    /// <summary>
    /// Captures equality against a deferred value factory.<br/>
    /// The factory is invoked only when the completed condition is materialized, matching Abraxas' parameter materialization behavior.<br/>
    /// </summary>
    /// <param name="value">The deferred value factory.</param>
    /// <param name="name">Optional replacement name for reusable condition templates.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd EqualTo(Func<TValue> value, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Add(LibraDexConditionOperatorKind.EqualTo, LibraDexConditionOperand.Deferred(() => value(), name));
    }

    /// <summary>
    /// Captures scalar null-state equality for key families that have no empty-key state.<br/>
    /// `ScalarNull.Null` selects the compact root null route; `ScalarNull.NonNull` selects ordinary non-null scalar value routes.<br/>
    /// </summary>
    /// <param name="state">The scalar null-state predicate to capture.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd EqualTo(ScalarNull state)
    {
        if (negate)
        {
            return Add(LibraDexConditionOperatorKind.ScalarNullState, LibraDexConditionOperand.Value(Opposite(state)));
        }

        return Add(LibraDexConditionOperatorKind.ScalarNullState, LibraDexConditionOperand.Value(state));
    }

    /// <summary>
    /// Captures inequality against a static value.<br/>
    /// </summary>
    /// <param name="value">The value to exclude.</param>
    /// <param name="name">Optional replacement name for reusable condition templates.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotEqualTo(TValue value, string? name = null)
        => Add(LibraDexConditionOperatorKind.NotEqualTo, LibraDexConditionOperand.Value(value, name));

    /// <summary>
    /// Captures inequality against a deferred value factory.<br/>
    /// The factory is invoked only when the completed condition is materialized, preserving reusable condition templates.<br/>
    /// </summary>
    /// <param name="value">The deferred value factory.</param>
    /// <param name="name">Optional replacement name for reusable condition templates.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotEqualTo(Func<TValue> value, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Add(LibraDexConditionOperatorKind.NotEqualTo, LibraDexConditionOperand.Deferred(() => value(), name));
    }

    /// <summary>
    /// Captures scalar null-state inequality for key families that have no empty-key state.<br/>
    /// Inequality maps to the opposite scalar null state so the descriptor remains a single executable key-state predicate.<br/>
    /// </summary>
    /// <param name="state">The scalar null-state predicate to exclude.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotEqualTo(ScalarNull state)
    {
        ScalarNull opposite = Opposite(state);
        return EqualTo(opposite);
    }

    /// <summary>
    /// Captures a greater-than comparison.<br/>
    /// </summary>
    /// <param name="value">The exclusive lower boundary value.</param>
    /// <param name="name">Optional replacement name for reusable condition templates.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd GreaterThan(TValue value, string? name = null)
        => Add(LibraDexConditionOperatorKind.GreaterThan, LibraDexConditionOperand.Value(value, name));

    /// <summary>
    /// Captures a greater-than comparison against a deferred value factory.<br/>
    /// The factory is invoked only when the completed condition is materialized, preserving reusable condition templates.<br/>
    /// </summary>
    /// <param name="value">The deferred exclusive lower boundary factory.</param>
    /// <param name="name">Optional replacement name for reusable condition templates.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd GreaterThan(Func<TValue> value, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Add(LibraDexConditionOperatorKind.GreaterThan, LibraDexConditionOperand.Deferred(() => value(), name));
    }

    /// <summary>
    /// Captures a greater-than-or-equal comparison.<br/>
    /// </summary>
    /// <param name="value">The inclusive lower boundary value.</param>
    /// <param name="name">Optional replacement name for reusable condition templates.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd GreaterOrEqual(TValue value, string? name = null)
        => Add(LibraDexConditionOperatorKind.GreaterOrEqual, LibraDexConditionOperand.Value(value, name));

    /// <summary>
    /// Captures a greater-than-or-equal comparison against a deferred value factory.<br/>
    /// The factory is invoked only when the completed condition is materialized, preserving reusable condition templates.<br/>
    /// </summary>
    /// <param name="value">The deferred inclusive lower boundary factory.</param>
    /// <param name="name">Optional replacement name for reusable condition templates.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd GreaterOrEqual(Func<TValue> value, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Add(LibraDexConditionOperatorKind.GreaterOrEqual, LibraDexConditionOperand.Deferred(() => value(), name));
    }

    /// <summary>
    /// Captures a less-than comparison.<br/>
    /// </summary>
    /// <param name="value">The exclusive upper boundary value.</param>
    /// <param name="name">Optional replacement name for reusable condition templates.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd LessThan(TValue value, string? name = null)
        => Add(LibraDexConditionOperatorKind.LessThan, LibraDexConditionOperand.Value(value, name));

    /// <summary>
    /// Captures a less-than comparison against a deferred value factory.<br/>
    /// The factory is invoked only when the completed condition is materialized, preserving reusable condition templates.<br/>
    /// </summary>
    /// <param name="value">The deferred exclusive upper boundary factory.</param>
    /// <param name="name">Optional replacement name for reusable condition templates.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd LessThan(Func<TValue> value, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Add(LibraDexConditionOperatorKind.LessThan, LibraDexConditionOperand.Deferred(() => value(), name));
    }

    /// <summary>
    /// Captures a less-than-or-equal comparison.<br/>
    /// </summary>
    /// <param name="value">The inclusive upper boundary value.</param>
    /// <param name="name">Optional replacement name for reusable condition templates.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd LessOrEqual(TValue value, string? name = null)
        => Add(LibraDexConditionOperatorKind.LessOrEqual, LibraDexConditionOperand.Value(value, name));

    /// <summary>
    /// Captures a less-than-or-equal comparison against a deferred value factory.<br/>
    /// The factory is invoked only when the completed condition is materialized, preserving reusable condition templates.<br/>
    /// </summary>
    /// <param name="value">The deferred inclusive upper boundary factory.</param>
    /// <param name="name">Optional replacement name for reusable condition templates.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd LessOrEqual(Func<TValue> value, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Add(LibraDexConditionOperatorKind.LessOrEqual, LibraDexConditionOperand.Deferred(() => value(), name));
    }

    /// <summary>
    /// Captures an inclusive two-boundary comparison.<br/>
    /// </summary>
    /// <param name="startValue">The inclusive lower boundary value.</param>
    /// <param name="endValue">The inclusive upper boundary value.</param>
    /// <param name="startName">Optional replacement name for the lower boundary.</param>
    /// <param name="endName">Optional replacement name for the upper boundary.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd Between(TValue startValue, TValue endValue, string? startName = null, string? endName = null)
    {
        return Add(
            LibraDexConditionOperatorKind.Between,
            LibraDexConditionOperand.Value(startValue, startName),
            LibraDexConditionOperand.Value(endValue, endName));
    }

    /// <summary>
    /// Captures an inclusive two-boundary comparison with deferred boundary factories.<br/>
    /// The factories are invoked only when the completed condition is materialized, preserving reusable condition templates.<br/>
    /// </summary>
    /// <param name="startValue">The deferred inclusive lower boundary factory.</param>
    /// <param name="endValue">The deferred inclusive upper boundary factory.</param>
    /// <param name="startName">Optional replacement name for the lower boundary.</param>
    /// <param name="endName">Optional replacement name for the upper boundary.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd Between(Func<TValue> startValue, Func<TValue> endValue, string? startName = null, string? endName = null)
    {
        ArgumentNullException.ThrowIfNull(startValue);
        ArgumentNullException.ThrowIfNull(endValue);
        return Add(
            LibraDexConditionOperatorKind.Between,
            LibraDexConditionOperand.Deferred(() => startValue(), startName),
            LibraDexConditionOperand.Deferred(() => endValue(), endName));
    }

    /// <summary>
    /// Captures an inclusive two-boundary comparison with a deferred lower boundary and static upper boundary.<br/>
    /// The deferred factory is invoked only when the completed condition is materialized, preserving reusable condition templates.<br/>
    /// </summary>
    /// <param name="startValue">The deferred inclusive lower boundary factory.</param>
    /// <param name="endValue">The inclusive upper boundary value.</param>
    /// <param name="startName">Optional replacement name for the lower boundary.</param>
    /// <param name="endName">Optional replacement name for the upper boundary.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd Between(Func<TValue> startValue, TValue endValue, string? startName = null, string? endName = null)
    {
        ArgumentNullException.ThrowIfNull(startValue);
        return Add(
            LibraDexConditionOperatorKind.Between,
            LibraDexConditionOperand.Deferred(() => startValue(), startName),
            LibraDexConditionOperand.Value(endValue, endName));
    }

    /// <summary>
    /// Captures an inclusive two-boundary comparison with a static lower boundary and deferred upper boundary.<br/>
    /// The deferred factory is invoked only when the completed condition is materialized, preserving reusable condition templates.<br/>
    /// </summary>
    /// <param name="startValue">The inclusive lower boundary value.</param>
    /// <param name="endValue">The deferred inclusive upper boundary factory.</param>
    /// <param name="startName">Optional replacement name for the lower boundary.</param>
    /// <param name="endName">Optional replacement name for the upper boundary.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd Between(TValue startValue, Func<TValue> endValue, string? startName = null, string? endName = null)
    {
        ArgumentNullException.ThrowIfNull(endValue);
        return Add(
            LibraDexConditionOperatorKind.Between,
            LibraDexConditionOperand.Value(startValue, startName),
            LibraDexConditionOperand.Deferred(() => endValue(), endName));
    }

    /// <summary>
    /// Captures an excluded two-boundary comparison.<br/>
    /// </summary>
    /// <param name="startValue">The inclusive lower boundary value of the excluded window.</param>
    /// <param name="endValue">The inclusive upper boundary value of the excluded window.</param>
    /// <param name="startName">Optional replacement name for the lower boundary.</param>
    /// <param name="endName">Optional replacement name for the upper boundary.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotBetween(TValue startValue, TValue endValue, string? startName = null, string? endName = null)
    {
        return Add(
            LibraDexConditionOperatorKind.NotBetween,
            LibraDexConditionOperand.Value(startValue, startName),
            LibraDexConditionOperand.Value(endValue, endName));
    }

    /// <summary>
    /// Captures an excluded two-boundary comparison with deferred boundary factories.<br/>
    /// The factories are invoked only when the completed condition is materialized, preserving reusable condition templates.<br/>
    /// </summary>
    /// <param name="startValue">The deferred inclusive lower boundary factory of the excluded window.</param>
    /// <param name="endValue">The deferred inclusive upper boundary factory of the excluded window.</param>
    /// <param name="startName">Optional replacement name for the lower boundary.</param>
    /// <param name="endName">Optional replacement name for the upper boundary.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotBetween(Func<TValue> startValue, Func<TValue> endValue, string? startName = null, string? endName = null)
    {
        ArgumentNullException.ThrowIfNull(startValue);
        ArgumentNullException.ThrowIfNull(endValue);
        return Add(
            LibraDexConditionOperatorKind.NotBetween,
            LibraDexConditionOperand.Deferred(() => startValue(), startName),
            LibraDexConditionOperand.Deferred(() => endValue(), endName));
    }

    /// <summary>
    /// Captures an excluded two-boundary comparison with a deferred lower boundary and static upper boundary.<br/>
    /// The deferred factory is invoked only when the completed condition is materialized, preserving reusable condition templates.<br/>
    /// </summary>
    /// <param name="startValue">The deferred inclusive lower boundary factory of the excluded window.</param>
    /// <param name="endValue">The inclusive upper boundary value of the excluded window.</param>
    /// <param name="startName">Optional replacement name for the lower boundary.</param>
    /// <param name="endName">Optional replacement name for the upper boundary.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotBetween(Func<TValue> startValue, TValue endValue, string? startName = null, string? endName = null)
    {
        ArgumentNullException.ThrowIfNull(startValue);
        return Add(
            LibraDexConditionOperatorKind.NotBetween,
            LibraDexConditionOperand.Deferred(() => startValue(), startName),
            LibraDexConditionOperand.Value(endValue, endName));
    }

    /// <summary>
    /// Captures an excluded two-boundary comparison with a static lower boundary and deferred upper boundary.<br/>
    /// The deferred factory is invoked only when the completed condition is materialized, preserving reusable condition templates.<br/>
    /// </summary>
    /// <param name="startValue">The inclusive lower boundary value of the excluded window.</param>
    /// <param name="endValue">The deferred inclusive upper boundary factory of the excluded window.</param>
    /// <param name="startName">Optional replacement name for the lower boundary.</param>
    /// <param name="endName">Optional replacement name for the upper boundary.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotBetween(TValue startValue, Func<TValue> endValue, string? startName = null, string? endName = null)
    {
        ArgumentNullException.ThrowIfNull(endValue);
        return Add(
            LibraDexConditionOperatorKind.NotBetween,
            LibraDexConditionOperand.Value(startValue, startName),
            LibraDexConditionOperand.Deferred(() => endValue(), endName));
    }

    /// <summary>
    /// Captures membership in a supplied value set.<br/>
    /// The set is captured as a descriptor operand so the execution bridge can later choose repeated lookup, prepared encoding, or visible scan behavior.<br/>
    /// </summary>
    /// <param name="values">The values to match.</param>
    /// <param name="name">Optional replacement name for reusable condition templates.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd InSet(IEnumerable<TValue> values, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        return Add(LibraDexConditionOperatorKind.InSet, LibraDexConditionOperand.Value(CaptureMembershipInput(values), name));
    }

    /// <summary>
    /// Captures membership in a deferred value set factory.<br/>
    /// The factory is invoked only when the completed condition is materialized so request-scoped sets can be reused without rebuilding the condition.<br/>
    /// </summary>
    /// <param name="values">The deferred value set factory.</param>
    /// <param name="name">Optional replacement name for reusable condition templates.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd InSet(Func<IEnumerable<TValue>> values, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        return Add(LibraDexConditionOperatorKind.InSet, LibraDexConditionOperand.Deferred(() => CaptureMembershipInput(values()), name));
    }

    /// <summary>
    /// Captures membership in a supplied value set.<br/>
    /// This is a LibraDex alias for <see cref="InSet(IEnumerable{TValue})"/>; unlike Abraxas, LibraDex does not need a separate serialized-set route because membership stays in managed .NET execution.<br/>
    /// </summary>
    /// <param name="values">The values to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd In(IEnumerable<TValue> values)
        => InSet(values);

    /// <summary>
    /// Captures membership in a deferred value set factory.<br/>
    /// This is a LibraDex alias for <see cref="InSet(Func{IEnumerable{TValue}})"/> and shares the same managed membership descriptor.<br/>
    /// </summary>
    /// <param name="values">The deferred values to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd In(Func<IEnumerable<TValue>> values)
        => InSet(values);

    /// <summary>
    /// Captures Abraxas-style membership in a supplied value set.<br/>
    /// This is an alias for <see cref="InSet(IEnumerable{TValue})"/> so copied condition-builder call sites do not need terminology rewrites before bridge planning.<br/>
    /// </summary>
    /// <param name="values">The values to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd IsIn(IEnumerable<TValue> values)
        => InSet(values);

    /// <summary>
    /// Captures Abraxas-style membership in a deferred value set factory.<br/>
    /// This is an alias for <see cref="InSet(Func{IEnumerable{TValue}})"/> so copied condition-builder call sites can preserve source grammar.<br/>
    /// </summary>
    /// <param name="values">The deferred values to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd IsIn(Func<IEnumerable<TValue>> values)
        => InSet(values);

    /// <summary>
    /// Captures membership exclusion in a supplied value set.<br/>
    /// </summary>
    /// <param name="values">The values to exclude.</param>
    /// <param name="name">Optional replacement name for reusable condition templates.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotInSet(IEnumerable<TValue> values, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        return Add(LibraDexConditionOperatorKind.NotInSet, LibraDexConditionOperand.Value(CaptureMembershipInput(values), name));
    }

    /// <summary>
    /// Captures membership exclusion from a deferred value set factory.<br/>
    /// The factory is invoked only when the completed condition is materialized so request-scoped sets can be reused without rebuilding the condition.<br/>
    /// </summary>
    /// <param name="values">The deferred values to exclude.</param>
    /// <param name="name">Optional replacement name for reusable condition templates.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotInSet(Func<IEnumerable<TValue>> values, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        return Add(LibraDexConditionOperatorKind.NotInSet, LibraDexConditionOperand.Deferred(() => CaptureMembershipInput(values()), name));
    }

    /// <summary>
    /// Captures membership exclusion from a supplied value set.<br/>
    /// This is a LibraDex alias for <see cref="NotInSet(IEnumerable{TValue})"/> and shares the same managed membership descriptor.<br/>
    /// </summary>
    /// <param name="values">The values to exclude.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotIn(IEnumerable<TValue> values)
        => NotInSet(values);

    /// <summary>
    /// Captures membership exclusion from a deferred value set factory.<br/>
    /// This is a LibraDex alias for <see cref="NotInSet(Func{IEnumerable{TValue}})"/> and shares the same managed membership descriptor.<br/>
    /// </summary>
    /// <param name="values">The deferred values to exclude.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotIn(Func<IEnumerable<TValue>> values)
        => NotInSet(values);

    /// <summary>
    /// Captures Abraxas-style membership exclusion from a supplied value set.<br/>
    /// This is an alias for <see cref="NotInSet(IEnumerable{TValue})"/> so copied condition-builder call sites can preserve source grammar while LibraDex plans through one descriptor kind.<br/>
    /// </summary>
    /// <param name="values">The values to exclude.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd IsNotIn(IEnumerable<TValue> values)
        => NotInSet(values);

    /// <summary>
    /// Captures Abraxas-style membership exclusion from a deferred value set factory.<br/>
    /// This is an alias for <see cref="NotInSet(Func{IEnumerable{TValue}})"/> so copied condition-builder call sites can preserve source grammar.<br/>
    /// </summary>
    /// <param name="values">The deferred values to exclude.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd IsNotIn(Func<IEnumerable<TValue>> values)
        => NotInSet(values);

    protected LibraDexConditionContinueOrEnd Add(
        LibraDexConditionOperatorKind operatorKind,
        params LibraDexConditionOperand[] operands)
    {
        return builder.AddLeaf(new LibraDexConditionLeafDescriptor(
            indexSelector,
            valueKind,
            EffectiveOperator(operatorKind),
            operands,
            IgnoreCase: false,
            Culture: null));
    }

    protected LibraDexConditionOperatorKind EffectiveOperator(LibraDexConditionOperatorKind operatorKind)
        => negate ? NegateOperator(operatorKind) : operatorKind;

    internal static LibraDexConditionOperatorKind NegateOperator(LibraDexConditionOperatorKind operatorKind)
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
            LibraDexConditionOperatorKind.MatchesPattern => LibraDexConditionOperatorKind.NotMatchesPattern,
            LibraDexConditionOperatorKind.NotMatchesPattern => LibraDexConditionOperatorKind.MatchesPattern,
            LibraDexConditionOperatorKind.MatchesWith => LibraDexConditionOperatorKind.NotMatchesWith,
            LibraDexConditionOperatorKind.NotMatchesWith => LibraDexConditionOperatorKind.MatchesWith,
            LibraDexConditionOperatorKind.MatchesInSet => LibraDexConditionOperatorKind.NotMatchesInSet,
            LibraDexConditionOperatorKind.NotMatchesInSet => LibraDexConditionOperatorKind.MatchesInSet,
            LibraDexConditionOperatorKind.RegexMatches => LibraDexConditionOperatorKind.NotRegexMatches,
            LibraDexConditionOperatorKind.NotRegexMatches => LibraDexConditionOperatorKind.RegexMatches,
            LibraDexConditionOperatorKind.YearEqualTo => LibraDexConditionOperatorKind.YearNotEqualTo,
            LibraDexConditionOperatorKind.YearNotEqualTo => LibraDexConditionOperatorKind.YearEqualTo,
            LibraDexConditionOperatorKind.YearIn => LibraDexConditionOperatorKind.YearNotIn,
            LibraDexConditionOperatorKind.YearNotIn => LibraDexConditionOperatorKind.YearIn,
            LibraDexConditionOperatorKind.YearRange => LibraDexConditionOperatorKind.YearNotRange,
            LibraDexConditionOperatorKind.YearNotRange => LibraDexConditionOperatorKind.YearRange,
            LibraDexConditionOperatorKind.MonthIn => LibraDexConditionOperatorKind.MonthNotIn,
            LibraDexConditionOperatorKind.MonthNotIn => LibraDexConditionOperatorKind.MonthIn,
            LibraDexConditionOperatorKind.MonthRange => LibraDexConditionOperatorKind.MonthNotRange,
            LibraDexConditionOperatorKind.MonthNotRange => LibraDexConditionOperatorKind.MonthRange,
            LibraDexConditionOperatorKind.DayRange => LibraDexConditionOperatorKind.DayNotRange,
            LibraDexConditionOperatorKind.DayNotRange => LibraDexConditionOperatorKind.DayRange,
            LibraDexConditionOperatorKind.BitAndEqualTo => LibraDexConditionOperatorKind.BitAndNotEqualTo,
            LibraDexConditionOperatorKind.BitAndNotEqualTo => LibraDexConditionOperatorKind.BitAndEqualTo,
            _ => throw new NotSupportedException($"Condition-builder negation is not supported for operator {operatorKind}.")
        };
    }

    private static ScalarNull Opposite(ScalarNull state)
    {
        return state switch
        {
            ScalarNull.Null => ScalarNull.NonNull,
            ScalarNull.NonNull => ScalarNull.Null,
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown scalar null state.")
        };
    }

    private static object CaptureMembershipInput(IEnumerable<TValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values is ISet<TValue> or IReadOnlyCollection<TValue>
            ? values
            : values.ToArray();
    }
}

/// <summary>
/// Captures string-specific adopted condition operators for one selected LibraDex index.<br/>
/// Text options are preserved as descriptor metadata so the bridge can require exact, folded-text, or sort-key projections instead of hiding query-time scans.<br/>
/// </summary>
public sealed class LibraDexStringConditionOperator : LibraDexConditionOperator<string>
{
    private readonly LibraDexConditionBuilder builder;
    private readonly LibraDexConditionIndexSelector indexSelector;

    internal LibraDexStringConditionOperator(LibraDexConditionBuilder builder, LibraDexConditionIndexSelector indexSelector, bool negate = false)
        : base(builder, indexSelector, LibraDexConditionValueKind.String, negate)
    {
        this.builder = builder;
        this.indexSelector = indexSelector;
    }

    /// <summary>
    /// Negates the next string predicate over the selected index.<br/>
    /// This preserves string-specific operators while mapping supported predicates to their inverse descriptor, such as `EqualTo` to `NotEqualTo` and regex capture membership to negated capture membership.<br/>
    /// </summary>
    public new LibraDexStringConditionOperator Not => new(builder, indexSelector, !IsNegated);

    /// <summary>
    /// Captures a string equality condition with optional case-insensitive projection intent.<br/>
    /// When <paramref name="ignoreCase"/> is true, materialization requires a maintained sort-key projection instead of normalizing or scanning the exact string index at query time.<br/>
    /// </summary>
    /// <param name="value">The string value to match.</param>
    /// <param name="ignoreCase">Whether case-insensitive text behavior was requested.</param>
    /// <param name="culture">The culture name for case or sort behavior, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd EqualTo(string? value, bool ignoreCase = false, string? culture = null)
    {
        if (value is null)
        {
            return EqualTo(NullKey.Null);
        }

        if (value.Length == 0)
        {
            return EqualTo(NullKey.Empty);
        }

        return AddText(LibraDexConditionOperatorKind.EqualTo, value, ignoreCase, culture);
    }

    /// <summary>
    /// Captures string equality against an explicit null or empty key state.<br/>
    /// `NullKey.Null` maps to the stored string null route, `NullKey.Empty` maps to the stored empty-string route, and `NullKey.NullOrEmpty` maps to both key-state routes.<br/>
    /// </summary>
    /// <param name="keyState">The key-state sentinel to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd EqualTo(NullKey keyState)
    {
        return keyState switch
        {
            NullKey.Null or NullKey.Empty or NullKey.NullOrEmpty => AddText(LibraDexConditionOperatorKind.EqualTo, keyState, ignoreCase: false, culture: null),
            _ => throw new ArgumentOutOfRangeException(nameof(keyState), keyState, "Unknown null-key state.")
        };
    }

    /// <summary>
    /// Captures string equality against the stored null-key sentinel from <see cref="DBNull.Value"/>.<br/>
    /// This overload keeps database-shaped call sites allocation-free and routes to <see cref="NullKey.Null"/> rather than treating `DBNull` as a string value.<br/>
    /// </summary>
    /// <param name="value">The database null sentinel; normally <see cref="DBNull.Value"/>.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd EqualTo(DBNull value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return EqualTo(NullKey.Null);
    }

    /// <summary>
    /// Captures a string inequality condition with optional case-insensitive projection intent.<br/>
    /// When <paramref name="ignoreCase"/> is true, materialization requires a maintained sort-key projection and maps the exclusion to ordered extents over that projection.<br/>
    /// </summary>
    /// <param name="value">The string value to exclude.</param>
    /// <param name="ignoreCase">Whether case-insensitive text behavior was requested.</param>
    /// <param name="culture">The culture name for case or sort behavior, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotEqualTo(string? value, bool ignoreCase = false, string? culture = null)
    {
        if (value is null)
        {
            return NotEqualTo(NullKey.Null);
        }

        if (value.Length == 0)
        {
            return NotEqualTo(NullKey.Empty);
        }

        return AddText(LibraDexConditionOperatorKind.NotEqualTo, value, ignoreCase, culture);
    }

    /// <summary>
    /// Captures string inequality against an explicit null or empty key state.<br/>
    /// `NullKey.NullOrEmpty` maps to the identity-universe complement of both key-state routes, excluding explicit null and explicit empty without constructing a caller-side collection.<br/>
    /// </summary>
    /// <param name="keyState">The key-state sentinel to exclude.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotEqualTo(NullKey keyState)
    {
        return keyState switch
        {
            NullKey.Null or NullKey.Empty or NullKey.NullOrEmpty => AddText(LibraDexConditionOperatorKind.NotEqualTo, keyState, ignoreCase: false, culture: null),
            _ => throw new ArgumentOutOfRangeException(nameof(keyState), keyState, "Unknown null-key state.")
        };
    }

    /// <summary>
    /// Captures string inequality against the stored null-key sentinel from <see cref="DBNull.Value"/>.<br/>
    /// This overload keeps database-shaped call sites explicit and routes to <see cref="NullKey.Null"/> instead of comparing against the `DBNull` object itself.<br/>
    /// </summary>
    /// <param name="value">The database null sentinel; normally <see cref="DBNull.Value"/>.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotEqualTo(DBNull value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return NotEqualTo(NullKey.Null);
    }

    /// <summary>
    /// Captures a string greater-than condition with optional case-insensitive projection intent.<br/>
    /// Case-insensitive ordered comparison uses a maintained sort-key projection so range readers compare stored projection bytes rather than performing query-time collation.<br/>
    /// </summary>
    /// <param name="value">The exclusive lower bound.</param>
    /// <param name="ignoreCase">Whether case-insensitive text behavior was requested.</param>
    /// <param name="culture">The culture name for case or sort behavior, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd GreaterThan(string? value, bool ignoreCase = false, string? culture = null)
        => AddText(LibraDexConditionOperatorKind.GreaterThan, value, ignoreCase, culture);

    /// <summary>
    /// Captures a string greater-than-or-equal condition with optional case-insensitive projection intent.<br/>
    /// Case-insensitive ordered comparison uses a maintained sort-key projection so the primitive route remains an ordered key boundary.<br/>
    /// </summary>
    /// <param name="value">The inclusive lower bound.</param>
    /// <param name="ignoreCase">Whether case-insensitive text behavior was requested.</param>
    /// <param name="culture">The culture name for case or sort behavior, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd GreaterOrEqual(string? value, bool ignoreCase = false, string? culture = null)
        => AddText(LibraDexConditionOperatorKind.GreaterOrEqual, value, ignoreCase, culture);

    /// <summary>
    /// Captures a string less-than condition with optional case-insensitive projection intent.<br/>
    /// Case-insensitive ordered comparison uses a maintained sort-key projection so the primitive route remains an ordered key boundary.<br/>
    /// </summary>
    /// <param name="value">The exclusive upper bound.</param>
    /// <param name="ignoreCase">Whether case-insensitive text behavior was requested.</param>
    /// <param name="culture">The culture name for case or sort behavior, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd LessThan(string? value, bool ignoreCase = false, string? culture = null)
        => AddText(LibraDexConditionOperatorKind.LessThan, value, ignoreCase, culture);

    /// <summary>
    /// Captures a string less-than-or-equal condition with optional case-insensitive projection intent.<br/>
    /// Case-insensitive ordered comparison uses a maintained sort-key projection so the primitive route remains an ordered key boundary.<br/>
    /// </summary>
    /// <param name="value">The inclusive upper bound.</param>
    /// <param name="ignoreCase">Whether case-insensitive text behavior was requested.</param>
    /// <param name="culture">The culture name for case or sort behavior, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd LessOrEqual(string? value, bool ignoreCase = false, string? culture = null)
        => AddText(LibraDexConditionOperatorKind.LessOrEqual, value, ignoreCase, culture);

    /// <summary>
    /// Captures an inclusive string range condition with optional case-insensitive projection intent.<br/>
    /// Case-insensitive ranges use maintained sort-key projection operands so retrieval collapses to the existing ordered range primitive.<br/>
    /// </summary>
    /// <param name="startValue">The inclusive lower string bound.</param>
    /// <param name="endValue">The inclusive upper string bound.</param>
    /// <param name="ignoreCase">Whether case-insensitive text behavior was requested.</param>
    /// <param name="culture">The culture name for case or sort behavior, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd Between(string? startValue, string? endValue, bool ignoreCase = false, string? culture = null)
        => AddText(LibraDexConditionOperatorKind.Between, new[] { LibraDexConditionOperand.Value(startValue), LibraDexConditionOperand.Value(endValue) }, ignoreCase, culture);

    /// <summary>
    /// Captures a string outside-range condition with optional case-insensitive projection intent.<br/>
    /// Case-insensitive outside-ranges use maintained sort-key projection operands and materialize as lower/upper ordered extents.<br/>
    /// </summary>
    /// <param name="startValue">The inclusive lower string bound to exclude.</param>
    /// <param name="endValue">The inclusive upper string bound to exclude.</param>
    /// <param name="ignoreCase">Whether case-insensitive text behavior was requested.</param>
    /// <param name="culture">The culture name for case or sort behavior, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotBetween(string? startValue, string? endValue, bool ignoreCase = false, string? culture = null)
        => AddText(LibraDexConditionOperatorKind.NotBetween, new[] { LibraDexConditionOperand.Value(startValue), LibraDexConditionOperand.Value(endValue) }, ignoreCase, culture);

    /// <summary>
    /// Captures string membership with optional case-insensitive projection intent.<br/>
    /// Case-insensitive membership uses maintained projection operands so the bridge can reuse membership execution rather than scanning the exact string index.<br/>
    /// </summary>
    /// <param name="values">The string values to match.</param>
    /// <param name="ignoreCase">Whether case-insensitive text behavior was requested.</param>
    /// <param name="culture">The culture name for case or sort behavior, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd InSet(IEnumerable<string> values, bool ignoreCase = false, string? culture = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        return AddText(LibraDexConditionOperatorKind.InSet, new[] { LibraDexConditionOperand.Value(CaptureStringMembershipInput(values)) }, ignoreCase, culture);
    }

    /// <summary>
    /// Captures string membership with a deferred value set factory and optional case-insensitive projection intent.<br/>
    /// The factory is invoked only when the completed condition is materialized so request-scoped sets can be reused without rebuilding the condition.<br/>
    /// </summary>
    /// <param name="values">The deferred string values to match.</param>
    /// <param name="ignoreCase">Whether case-insensitive text behavior was requested.</param>
    /// <param name="culture">The culture name for case or sort behavior, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd InSet(Func<IEnumerable<string>> values, bool ignoreCase = false, string? culture = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        return AddText(LibraDexConditionOperatorKind.InSet, new[] { LibraDexConditionOperand.Deferred(() => CaptureStringMembershipInput(values())) }, ignoreCase, culture);
    }

    /// <summary>
    /// Captures string membership with optional case-insensitive projection intent.<br/>
    /// This is a LibraDex alias for <see cref="InSet(IEnumerable{string}, bool, string?)"/>; managed execution removes Abraxas' need to distinguish serialized set membership from enumerable membership.<br/>
    /// </summary>
    /// <param name="values">The string values to match.</param>
    /// <param name="ignoreCase">Whether case-insensitive text behavior was requested.</param>
    /// <param name="culture">The culture name for case or sort behavior, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd In(IEnumerable<string> values, bool ignoreCase = false, string? culture = null)
        => InSet(values, ignoreCase, culture);

    /// <summary>
    /// Captures string membership with a deferred value set factory and optional case-insensitive projection intent.<br/>
    /// This is a LibraDex alias for <see cref="InSet(Func{IEnumerable{string}}, bool, string?)"/> and shares the same managed membership descriptor.<br/>
    /// </summary>
    /// <param name="values">The deferred string values to match.</param>
    /// <param name="ignoreCase">Whether case-insensitive text behavior was requested.</param>
    /// <param name="culture">The culture name for case or sort behavior, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd In(Func<IEnumerable<string>> values, bool ignoreCase = false, string? culture = null)
        => InSet(values, ignoreCase, culture);

    /// <summary>
    /// Captures string membership with an explicit method-level managed comparison policy.<br/>
    /// The policy is used only when membership needs managed residual comparison; maintained projections and encoded exact routes still have first refusal.<br/>
    /// </summary>
    /// <param name="values">The string values to match.</param>
    /// <param name="stringComparisonPolicy">The method-level string comparison policy.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd InSet(IEnumerable<string> values, LibraDexStringComparisonPolicy stringComparisonPolicy)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(stringComparisonPolicy);
        return AddText(LibraDexConditionOperatorKind.InSet, new[] { LibraDexConditionOperand.Value(CaptureStringMembershipInput(values)) }, stringComparisonPolicy.IgnoreCase, stringComparisonPolicy.CultureName, stringComparisonPolicy);
    }

    /// <summary>
    /// Captures string membership with a deferred value set factory and an explicit method-level managed comparison policy.<br/>
    /// The factory is invoked only when the completed condition is materialized so request-scoped sets can be reused without rebuilding the condition.<br/>
    /// </summary>
    /// <param name="values">The deferred string values to match.</param>
    /// <param name="stringComparisonPolicy">The method-level string comparison policy.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd InSet(Func<IEnumerable<string>> values, LibraDexStringComparisonPolicy stringComparisonPolicy)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(stringComparisonPolicy);
        return AddText(LibraDexConditionOperatorKind.InSet, new[] { LibraDexConditionOperand.Deferred(() => CaptureStringMembershipInput(values())) }, stringComparisonPolicy.IgnoreCase, stringComparisonPolicy.CultureName, stringComparisonPolicy);
    }

    /// <summary>
    /// Captures string membership with an explicit method-level managed comparison policy.<br/>
    /// This is a LibraDex alias for <see cref="InSet(IEnumerable{string}, LibraDexStringComparisonPolicy)"/> and uses the same prepared membership path.<br/>
    /// </summary>
    /// <param name="values">The string values to match.</param>
    /// <param name="stringComparisonPolicy">The method-level string comparison policy.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd In(IEnumerable<string> values, LibraDexStringComparisonPolicy stringComparisonPolicy)
        => InSet(values, stringComparisonPolicy);

    /// <summary>
    /// Captures string membership with a deferred value set factory and an explicit method-level managed comparison policy.<br/>
    /// This is a LibraDex alias for <see cref="InSet(Func{IEnumerable{string}}, LibraDexStringComparisonPolicy)"/> and uses the same prepared membership path.<br/>
    /// </summary>
    /// <param name="values">The deferred string values to match.</param>
    /// <param name="stringComparisonPolicy">The method-level string comparison policy.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd In(Func<IEnumerable<string>> values, LibraDexStringComparisonPolicy stringComparisonPolicy)
        => InSet(values, stringComparisonPolicy);

    /// <summary>
    /// Captures string membership with optional case-insensitive projection intent.<br/>
    /// This alias preserves the Abraxas-style `IsIn` spelling while keeping the descriptor shape identical to `InSet`.<br/>
    /// </summary>
    /// <param name="values">The string values to match.</param>
    /// <param name="ignoreCase">Whether case-insensitive text behavior was requested.</param>
    /// <param name="culture">The culture name for case or sort behavior, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd IsIn(IEnumerable<string> values, bool ignoreCase = false, string? culture = null)
        => InSet(values, ignoreCase, culture);

    /// <summary>
    /// Captures Abraxas-style string membership with a deferred value set factory.<br/>
    /// This alias preserves the Abraxas-style `IsIn` spelling while keeping the descriptor shape identical to `InSet`.<br/>
    /// </summary>
    /// <param name="values">The deferred string values to match.</param>
    /// <param name="ignoreCase">Whether case-insensitive text behavior was requested.</param>
    /// <param name="culture">The culture name for case or sort behavior, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd IsIn(Func<IEnumerable<string>> values, bool ignoreCase = false, string? culture = null)
        => InSet(values, ignoreCase, culture);

    /// <summary>
    /// Captures string non-membership with optional case-insensitive projection intent.<br/>
    /// Case-insensitive non-membership uses maintained projection operands and then negates the resulting membership leaf.<br/>
    /// </summary>
    /// <param name="values">The string values to exclude.</param>
    /// <param name="ignoreCase">Whether case-insensitive text behavior was requested.</param>
    /// <param name="culture">The culture name for case or sort behavior, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotInSet(IEnumerable<string> values, bool ignoreCase = false, string? culture = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        return AddText(LibraDexConditionOperatorKind.NotInSet, new[] { LibraDexConditionOperand.Value(CaptureStringMembershipInput(values)) }, ignoreCase, culture);
    }

    /// <summary>
    /// Captures string non-membership with a deferred value set factory and optional case-insensitive projection intent.<br/>
    /// The factory is invoked only when the completed condition is materialized so request-scoped sets can be reused without rebuilding the condition.<br/>
    /// </summary>
    /// <param name="values">The deferred string values to exclude.</param>
    /// <param name="ignoreCase">Whether case-insensitive text behavior was requested.</param>
    /// <param name="culture">The culture name for case or sort behavior, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotInSet(Func<IEnumerable<string>> values, bool ignoreCase = false, string? culture = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        return AddText(LibraDexConditionOperatorKind.NotInSet, new[] { LibraDexConditionOperand.Deferred(() => CaptureStringMembershipInput(values())) }, ignoreCase, culture);
    }

    /// <summary>
    /// Captures string non-membership with optional case-insensitive projection intent.<br/>
    /// This is a LibraDex alias for <see cref="NotInSet(IEnumerable{string}, bool, string?)"/> and shares the same managed membership descriptor.<br/>
    /// </summary>
    /// <param name="values">The string values to exclude.</param>
    /// <param name="ignoreCase">Whether case-insensitive text behavior was requested.</param>
    /// <param name="culture">The culture name for case or sort behavior, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotIn(IEnumerable<string> values, bool ignoreCase = false, string? culture = null)
        => NotInSet(values, ignoreCase, culture);

    /// <summary>
    /// Captures string non-membership with a deferred value set factory and optional case-insensitive projection intent.<br/>
    /// This is a LibraDex alias for <see cref="NotInSet(Func{IEnumerable{string}}, bool, string?)"/> and shares the same managed membership descriptor.<br/>
    /// </summary>
    /// <param name="values">The deferred string values to exclude.</param>
    /// <param name="ignoreCase">Whether case-insensitive text behavior was requested.</param>
    /// <param name="culture">The culture name for case or sort behavior, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotIn(Func<IEnumerable<string>> values, bool ignoreCase = false, string? culture = null)
        => NotInSet(values, ignoreCase, culture);

    /// <summary>
    /// Captures string non-membership with an explicit method-level managed comparison policy.<br/>
    /// The policy is used only when non-membership needs managed residual comparison; maintained projections and encoded exact routes still have first refusal.<br/>
    /// </summary>
    /// <param name="values">The string values to exclude.</param>
    /// <param name="stringComparisonPolicy">The method-level string comparison policy.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotInSet(IEnumerable<string> values, LibraDexStringComparisonPolicy stringComparisonPolicy)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(stringComparisonPolicy);
        return AddText(LibraDexConditionOperatorKind.NotInSet, new[] { LibraDexConditionOperand.Value(CaptureStringMembershipInput(values)) }, stringComparisonPolicy.IgnoreCase, stringComparisonPolicy.CultureName, stringComparisonPolicy);
    }

    /// <summary>
    /// Captures string non-membership with a deferred value set factory and an explicit method-level managed comparison policy.<br/>
    /// The factory is invoked only when the completed condition is materialized so request-scoped sets can be reused without rebuilding the condition.<br/>
    /// </summary>
    /// <param name="values">The deferred string values to exclude.</param>
    /// <param name="stringComparisonPolicy">The method-level string comparison policy.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotInSet(Func<IEnumerable<string>> values, LibraDexStringComparisonPolicy stringComparisonPolicy)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(stringComparisonPolicy);
        return AddText(LibraDexConditionOperatorKind.NotInSet, new[] { LibraDexConditionOperand.Deferred(() => CaptureStringMembershipInput(values())) }, stringComparisonPolicy.IgnoreCase, stringComparisonPolicy.CultureName, stringComparisonPolicy);
    }

    /// <summary>
    /// Captures string non-membership with an explicit method-level managed comparison policy.<br/>
    /// This is a LibraDex alias for <see cref="NotInSet(IEnumerable{string}, LibraDexStringComparisonPolicy)"/> and uses the same prepared membership path.<br/>
    /// </summary>
    /// <param name="values">The string values to exclude.</param>
    /// <param name="stringComparisonPolicy">The method-level string comparison policy.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotIn(IEnumerable<string> values, LibraDexStringComparisonPolicy stringComparisonPolicy)
        => NotInSet(values, stringComparisonPolicy);

    /// <summary>
    /// Captures string non-membership with a deferred value set factory and an explicit method-level managed comparison policy.<br/>
    /// This is a LibraDex alias for <see cref="NotInSet(Func{IEnumerable{string}}, LibraDexStringComparisonPolicy)"/> and uses the same prepared membership path.<br/>
    /// </summary>
    /// <param name="values">The deferred string values to exclude.</param>
    /// <param name="stringComparisonPolicy">The method-level string comparison policy.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotIn(Func<IEnumerable<string>> values, LibraDexStringComparisonPolicy stringComparisonPolicy)
        => NotInSet(values, stringComparisonPolicy);

    /// <summary>
    /// Captures string non-membership with optional case-insensitive projection intent.<br/>
    /// This alias preserves the Abraxas-style `IsNotIn` spelling while keeping the descriptor shape identical to `NotInSet`.<br/>
    /// </summary>
    /// <param name="values">The string values to exclude.</param>
    /// <param name="ignoreCase">Whether case-insensitive text behavior was requested.</param>
    /// <param name="culture">The culture name for case or sort behavior, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd IsNotIn(IEnumerable<string> values, bool ignoreCase = false, string? culture = null)
        => NotInSet(values, ignoreCase, culture);

    /// <summary>
    /// Captures Abraxas-style string non-membership with a deferred value set factory.<br/>
    /// This alias preserves the Abraxas-style `IsNotIn` spelling while keeping the descriptor shape identical to `NotInSet`.<br/>
    /// </summary>
    /// <param name="values">The deferred string values to exclude.</param>
    /// <param name="ignoreCase">Whether case-insensitive text behavior was requested.</param>
    /// <param name="culture">The culture name for case or sort behavior, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd IsNotIn(Func<IEnumerable<string>> values, bool ignoreCase = false, string? culture = null)
        => NotInSet(values, ignoreCase, culture);

    /// <summary>
    /// Captures a starts-with text condition.<br/>
    /// The descriptor preserves case and culture options so the resolver can require a matching maintained projection or report visible scan behavior.<br/>
    /// </summary>
    /// <param name="value">The text prefix value.</param>
    /// <param name="ignoreCase">Whether case-insensitive text behavior was requested.</param>
    /// <param name="culture">The culture name for case or sort behavior, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd StartsWith(string value, bool ignoreCase = false, string? culture = null)
        => AddText(LibraDexConditionOperatorKind.StartsWith, value, ignoreCase, culture);

    /// <summary>
    /// Captures an ends-with text condition.<br/>
    /// </summary>
    /// <param name="value">The text suffix value.</param>
    /// <param name="ignoreCase">Whether case-insensitive text behavior was requested.</param>
    /// <param name="culture">The culture name for case or sort behavior, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd EndsWith(string value, bool ignoreCase = false, string? culture = null)
        => AddText(LibraDexConditionOperatorKind.EndsWith, value, ignoreCase, culture);

    /// <summary>
    /// Captures a contains text condition.<br/>
    /// </summary>
    /// <param name="value">The contained text value.</param>
    /// <param name="ignoreCase">Whether case-insensitive text behavior was requested.</param>
    /// <param name="culture">The culture name for case or sort behavior, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd Contains(string value, bool ignoreCase = false, string? culture = null)
        => AddText(LibraDexConditionOperatorKind.Contains, value, ignoreCase, culture);

    /// <summary>
    /// Captures a pattern text condition.<br/>
    /// Pattern execution is descriptor-only in this slice; the resolver decides later whether a maintained projection, predicate, or unsupported case applies.<br/>
    /// </summary>
    /// <param name="pattern">The pattern descriptor.</param>
    /// <param name="ignoreCase">Whether case-insensitive text behavior was requested.</param>
    /// <param name="culture">The culture name for case or sort behavior, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd MatchesPattern(string pattern, bool ignoreCase = false, string? culture = null)
        => AddText(LibraDexConditionOperatorKind.MatchesPattern, pattern, ignoreCase, culture);

    /// <summary>
    /// Captures a wildcard-like text condition using the low-friction public spelling.<br/>
    /// The pattern uses `*` for any text and `?` for one character; execution may narrow by the leading literal before applying the residual wildcard comparison.<br/>
    /// </summary>
    /// <param name="pattern">The wildcard pattern descriptor.</param>
    /// <param name="ignoreCase">Whether wildcard comparison should ignore case.</param>
    /// <param name="culture">The culture name for wildcard comparison, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd Like(string pattern, bool ignoreCase = false, string? culture = null)
        => MatchesPattern(pattern, ignoreCase, culture);

    /// <summary>
    /// Captures a negated wildcard-like text condition using the low-friction public spelling.<br/>
    /// This maps to the same descriptor family as <see cref="Like(string, bool, string?)"/> through the canonical condition-builder negation path.<br/>
    /// </summary>
    /// <param name="pattern">The wildcard pattern descriptor.</param>
    /// <param name="ignoreCase">Whether wildcard comparison should ignore case.</param>
    /// <param name="culture">The culture name for wildcard comparison, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotLike(string pattern, bool ignoreCase = false, string? culture = null)
        => Not.Like(pattern, ignoreCase, culture);

    /// <summary>
    /// Captures a boolean regular-expression condition using the short public spelling.<br/>
    /// The expression matches when the regex finds a match anywhere in the candidate string; anchored regexes such as `^api` may narrow the candidate scan by their leading literal before the residual regex is evaluated.<br/>
    /// </summary>
    /// <param name="pattern">The regular expression pattern.</param>
    /// <param name="ignoreCase">Whether regex matching should ignore case.</param>
    /// <param name="culture">The culture name used to derive comparison metadata, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd Matches(string pattern, bool ignoreCase = false, string? culture = null)
        => AddText(LibraDexConditionOperatorKind.RegexMatches, pattern, ignoreCase, culture);

    /// <summary>
    /// Captures a boolean regular-expression condition from a caller-provided <see cref="Regex"/> instance.<br/>
    /// The regex instance is preserved as the operand so caller-selected options, timeout, and compiled/interpreted behavior are reused during residual predicate evaluation.<br/>
    /// </summary>
    /// <param name="regex">The regular expression instance to evaluate.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd Matches(Regex regex)
    {
        ArgumentNullException.ThrowIfNull(regex);
        return AddText(LibraDexConditionOperatorKind.RegexMatches, regex, ignoreCase: false, culture: null);
    }

    /// <summary>
    /// Captures a negated boolean regular-expression condition.<br/>
    /// Identities match when the regex does not find a match in the candidate string; use <see cref="Not"/> with <see cref="Matches(string, bool, string?)"/> when the canonical negation style is preferred.<br/>
    /// </summary>
    /// <param name="pattern">The regular expression pattern.</param>
    /// <param name="ignoreCase">Whether regex matching should ignore case.</param>
    /// <param name="culture">The culture name used to derive comparison metadata, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotMatches(string pattern, bool ignoreCase = false, string? culture = null)
        => AddText(LibraDexConditionOperatorKind.NotRegexMatches, pattern, ignoreCase, culture);

    /// <summary>
    /// Captures a negated boolean regular-expression condition from a caller-provided <see cref="Regex"/> instance.<br/>
    /// The regex instance is preserved as the operand and identities match when it does not find a match in the candidate string.<br/>
    /// </summary>
    /// <param name="regex">The regular expression instance to evaluate.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotMatches(Regex regex)
    {
        ArgumentNullException.ThrowIfNull(regex);
        return AddText(LibraDexConditionOperatorKind.NotRegexMatches, regex, ignoreCase: false, culture: null);
    }

    /// <summary>
    /// Captures a regex match-value comparison against the selected string index.<br/>
    /// The regular expression is evaluated with <see cref="System.Text.RegularExpressions.Regex.Match(string, string)"/> semantics, and <see cref="System.Text.RegularExpressions.Match.Value"/> is compared to <paramref name="value"/>.<br/>
    /// </summary>
    /// <param name="pattern">The regular expression pattern.</param>
    /// <param name="value">The expected whole-match value.</param>
    /// <param name="ignoreCase">Whether regex matching and captured-value comparison should ignore case.</param>
    /// <param name="culture">The culture name for captured-value comparison, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd MatchesWith(string pattern, string value, bool ignoreCase = false, string? culture = null)
        => AddText(LibraDexConditionOperatorKind.MatchesWith, CreateRegexCaptureOperands(pattern, value, groupNumber: null), ignoreCase, culture);

    /// <summary>
    /// Captures a regex match-value comparison from a caller-provided <see cref="Regex"/> instance.<br/>
    /// The regular expression object is reused during execution, and <see cref="System.Text.RegularExpressions.Match.Value"/> is compared to <paramref name="value"/> using the supplied comparison metadata.<br/>
    /// </summary>
    /// <param name="regex">The regular expression instance.</param>
    /// <param name="value">The expected whole-match value.</param>
    /// <param name="ignoreCase">Whether captured-value comparison should ignore case.</param>
    /// <param name="culture">The culture name for captured-value comparison, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd MatchesWith(Regex regex, string value, bool ignoreCase = false, string? culture = null)
        => AddText(LibraDexConditionOperatorKind.MatchesWith, CreateRegexCaptureOperands(regex, value, groupNumber: null), ignoreCase, culture);

    /// <summary>
    /// Captures a regex capture-group comparison against the selected string index.<br/>
    /// The regular expression is evaluated with <see cref="System.Text.RegularExpressions.Regex.Match(string, string)"/> semantics, and `Match.Groups[groupNumber].Value` is compared to <paramref name="value"/>.<br/>
    /// </summary>
    /// <param name="pattern">The regular expression pattern.</param>
    /// <param name="value">The expected capture-group value.</param>
    /// <param name="groupNumber">The regex group number to compare; zero compares the whole match.</param>
    /// <param name="ignoreCase">Whether regex matching and captured-value comparison should ignore case.</param>
    /// <param name="culture">The culture name for captured-value comparison, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd MatchesWith(string pattern, string value, int groupNumber, bool ignoreCase = false, string? culture = null)
        => AddText(LibraDexConditionOperatorKind.MatchesWith, CreateRegexCaptureOperands(pattern, value, groupNumber), ignoreCase, culture);

    /// <summary>
    /// Captures a regex capture-group comparison from a caller-provided <see cref="Regex"/> instance.<br/>
    /// The regular expression object is reused during execution, and `Match.Groups[groupNumber].Value` is compared to <paramref name="value"/> using the supplied comparison metadata.<br/>
    /// </summary>
    /// <param name="regex">The regular expression instance.</param>
    /// <param name="value">The expected capture-group value.</param>
    /// <param name="groupNumber">The regex group number to compare; zero compares the whole match.</param>
    /// <param name="ignoreCase">Whether captured-value comparison should ignore case.</param>
    /// <param name="culture">The culture name for captured-value comparison, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd MatchesWith(Regex regex, string value, int groupNumber, bool ignoreCase = false, string? culture = null)
        => AddText(LibraDexConditionOperatorKind.MatchesWith, CreateRegexCaptureOperands(regex, value, groupNumber), ignoreCase, culture);

    /// <summary>
    /// Captures a negated regex match-value comparison against the selected string index.<br/>
    /// The regular expression is evaluated with <see cref="System.Text.RegularExpressions.Regex.Match(string, string)"/> semantics, and identities match when <see cref="System.Text.RegularExpressions.Match.Value"/> differs from <paramref name="value"/> or the regex does not match.<br/>
    /// </summary>
    /// <param name="pattern">The regular expression pattern.</param>
    /// <param name="value">The whole-match value to exclude.</param>
    /// <param name="ignoreCase">Whether regex matching and captured-value comparison should ignore case.</param>
    /// <param name="culture">The culture name for captured-value comparison, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotMatchesWith(string pattern, string value, bool ignoreCase = false, string? culture = null)
        => AddText(LibraDexConditionOperatorKind.NotMatchesWith, CreateRegexCaptureOperands(pattern, value, groupNumber: null), ignoreCase, culture);

    /// <summary>
    /// Captures a negated regex match-value comparison from a caller-provided <see cref="Regex"/> instance.<br/>
    /// The regular expression object is reused during execution, and identities match when the whole match value differs from <paramref name="value"/> or the regex does not match.<br/>
    /// </summary>
    /// <param name="regex">The regular expression instance.</param>
    /// <param name="value">The whole-match value to exclude.</param>
    /// <param name="ignoreCase">Whether captured-value comparison should ignore case.</param>
    /// <param name="culture">The culture name for captured-value comparison, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotMatchesWith(Regex regex, string value, bool ignoreCase = false, string? culture = null)
        => AddText(LibraDexConditionOperatorKind.NotMatchesWith, CreateRegexCaptureOperands(regex, value, groupNumber: null), ignoreCase, culture);

    /// <summary>
    /// Captures a negated regex capture-group comparison against the selected string index.<br/>
    /// The regular expression is evaluated with <see cref="System.Text.RegularExpressions.Regex.Match(string, string)"/> semantics, and identities match when `Match.Groups[groupNumber].Value` differs from <paramref name="value"/> or the regex/group does not match.<br/>
    /// </summary>
    /// <param name="pattern">The regular expression pattern.</param>
    /// <param name="value">The capture-group value to exclude.</param>
    /// <param name="groupNumber">The regex group number to compare; zero compares the whole match.</param>
    /// <param name="ignoreCase">Whether regex matching and captured-value comparison should ignore case.</param>
    /// <param name="culture">The culture name for captured-value comparison, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotMatchesWith(string pattern, string value, int groupNumber, bool ignoreCase = false, string? culture = null)
        => AddText(LibraDexConditionOperatorKind.NotMatchesWith, CreateRegexCaptureOperands(pattern, value, groupNumber), ignoreCase, culture);

    /// <summary>
    /// Captures a negated regex capture-group comparison from a caller-provided <see cref="Regex"/> instance.<br/>
    /// The regular expression object is reused during execution, and identities match when the selected group value differs from <paramref name="value"/> or the regex/group does not match.<br/>
    /// </summary>
    /// <param name="regex">The regular expression instance.</param>
    /// <param name="value">The capture-group value to exclude.</param>
    /// <param name="groupNumber">The regex group number to compare; zero compares the whole match.</param>
    /// <param name="ignoreCase">Whether captured-value comparison should ignore case.</param>
    /// <param name="culture">The culture name for captured-value comparison, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotMatchesWith(Regex regex, string value, int groupNumber, bool ignoreCase = false, string? culture = null)
        => AddText(LibraDexConditionOperatorKind.NotMatchesWith, CreateRegexCaptureOperands(regex, value, groupNumber), ignoreCase, culture);

    /// <summary>
    /// Captures regex match-value membership against the selected string index.<br/>
    /// The regular expression is evaluated once per candidate key, and <see cref="System.Text.RegularExpressions.Match.Value"/> is compared to the supplied values.<br/>
    /// </summary>
    /// <param name="pattern">The regular expression pattern.</param>
    /// <param name="values">The expected whole-match values.</param>
    /// <param name="ignoreCase">Whether regex matching and captured-value comparison should ignore case.</param>
    /// <param name="culture">The culture name for captured-value comparison, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd MatchesIn(string pattern, IEnumerable<string> values, bool ignoreCase = false, string? culture = null)
        => MatchesInSet(pattern, values, ignoreCase, culture);

    /// <summary>
    /// Captures regex match-value membership from a caller-provided <see cref="Regex"/> instance.<br/>
    /// The regular expression object is reused during execution, and <see cref="System.Text.RegularExpressions.Match.Value"/> is compared to the supplied value set.<br/>
    /// </summary>
    /// <param name="regex">The regular expression instance.</param>
    /// <param name="values">The expected whole-match values.</param>
    /// <param name="ignoreCase">Whether captured-value membership comparison should ignore case.</param>
    /// <param name="culture">The culture name for captured-value comparison, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd MatchesIn(Regex regex, IEnumerable<string> values, bool ignoreCase = false, string? culture = null)
        => MatchesInSet(regex, values, ignoreCase, culture);

    /// <summary>
    /// Captures regex capture-group membership against the selected string index.<br/>
    /// The regular expression is evaluated once per candidate key, and `Match.Groups[groupNumber].Value` is compared to the supplied values.<br/>
    /// </summary>
    /// <param name="pattern">The regular expression pattern.</param>
    /// <param name="values">The expected capture-group values.</param>
    /// <param name="groupNumber">The regex group number to compare; zero compares the whole match.</param>
    /// <param name="ignoreCase">Whether regex matching and captured-value comparison should ignore case.</param>
    /// <param name="culture">The culture name for captured-value comparison, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd MatchesIn(string pattern, IEnumerable<string> values, int groupNumber, bool ignoreCase = false, string? culture = null)
        => MatchesInSet(pattern, values, groupNumber, ignoreCase, culture);

    /// <summary>
    /// Captures regex capture-group membership from a caller-provided <see cref="Regex"/> instance.<br/>
    /// The regular expression object is reused during execution, and `Match.Groups[groupNumber].Value` is compared to the supplied value set.<br/>
    /// </summary>
    /// <param name="regex">The regular expression instance.</param>
    /// <param name="values">The expected capture-group values.</param>
    /// <param name="groupNumber">The regex group number to compare; zero compares the whole match.</param>
    /// <param name="ignoreCase">Whether captured-value membership comparison should ignore case.</param>
    /// <param name="culture">The culture name for captured-value comparison, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd MatchesIn(Regex regex, IEnumerable<string> values, int groupNumber, bool ignoreCase = false, string? culture = null)
        => MatchesInSet(regex, values, groupNumber, ignoreCase, culture);

    /// <summary>
    /// Captures regex match-value membership against the selected string index using set terminology.<br/>
    /// Compatible set-shaped inputs can be reused by the executor while preserving the captured-value comparison policy.<br/>
    /// </summary>
    /// <param name="pattern">The regular expression pattern.</param>
    /// <param name="values">The expected whole-match values.</param>
    /// <param name="ignoreCase">Whether regex matching and captured-value comparison should ignore case.</param>
    /// <param name="culture">The culture name for captured-value comparison, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd MatchesInSet(string pattern, IEnumerable<string> values, bool ignoreCase = false, string? culture = null)
        => AddText(LibraDexConditionOperatorKind.MatchesInSet, CreateRegexCaptureSetOperands(pattern, values, groupNumber: null), ignoreCase, culture);

    /// <summary>
    /// Captures regex match-value membership from a caller-provided <see cref="Regex"/> instance using set terminology.<br/>
    /// Compatible set-shaped inputs can be reused by the executor while preserving the captured-value comparison policy.<br/>
    /// </summary>
    /// <param name="regex">The regular expression instance.</param>
    /// <param name="values">The expected whole-match values.</param>
    /// <param name="ignoreCase">Whether captured-value membership comparison should ignore case.</param>
    /// <param name="culture">The culture name for captured-value comparison, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd MatchesInSet(Regex regex, IEnumerable<string> values, bool ignoreCase = false, string? culture = null)
        => AddText(LibraDexConditionOperatorKind.MatchesInSet, CreateRegexCaptureSetOperands(regex, values, groupNumber: null), ignoreCase, culture);

    /// <summary>
    /// Captures regex capture-group membership against the selected string index using set terminology.<br/>
    /// Compatible set-shaped inputs can be reused by the executor while preserving the captured-value comparison policy.<br/>
    /// </summary>
    /// <param name="pattern">The regular expression pattern.</param>
    /// <param name="values">The expected capture-group values.</param>
    /// <param name="groupNumber">The regex group number to compare; zero compares the whole match.</param>
    /// <param name="ignoreCase">Whether regex matching and captured-value comparison should ignore case.</param>
    /// <param name="culture">The culture name for captured-value comparison, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd MatchesInSet(string pattern, IEnumerable<string> values, int groupNumber, bool ignoreCase = false, string? culture = null)
        => AddText(LibraDexConditionOperatorKind.MatchesInSet, CreateRegexCaptureSetOperands(pattern, values, groupNumber), ignoreCase, culture);

    /// <summary>
    /// Captures regex capture-group membership from a caller-provided <see cref="Regex"/> instance using set terminology.<br/>
    /// Compatible set-shaped inputs can be reused by the executor while preserving the captured-value comparison policy.<br/>
    /// </summary>
    /// <param name="regex">The regular expression instance.</param>
    /// <param name="values">The expected capture-group values.</param>
    /// <param name="groupNumber">The regex group number to compare; zero compares the whole match.</param>
    /// <param name="ignoreCase">Whether captured-value membership comparison should ignore case.</param>
    /// <param name="culture">The culture name for captured-value comparison, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd MatchesInSet(Regex regex, IEnumerable<string> values, int groupNumber, bool ignoreCase = false, string? culture = null)
        => AddText(LibraDexConditionOperatorKind.MatchesInSet, CreateRegexCaptureSetOperands(regex, values, groupNumber), ignoreCase, culture);

    /// <summary>
    /// Captures negated regex match-value membership against the selected string index.<br/>
    /// Identities match when the regex does not match or the selected match value is not in the supplied values.<br/>
    /// </summary>
    /// <param name="pattern">The regular expression pattern.</param>
    /// <param name="values">The whole-match values to exclude.</param>
    /// <param name="ignoreCase">Whether regex matching and captured-value comparison should ignore case.</param>
    /// <param name="culture">The culture name for captured-value comparison, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotMatchesIn(string pattern, IEnumerable<string> values, bool ignoreCase = false, string? culture = null)
        => NotMatchesInSet(pattern, values, ignoreCase, culture);

    /// <summary>
    /// Captures negated regex match-value membership from a caller-provided <see cref="Regex"/> instance.<br/>
    /// Identities match when the regex does not match or the whole match value is not in the supplied values.<br/>
    /// </summary>
    /// <param name="regex">The regular expression instance.</param>
    /// <param name="values">The whole-match values to exclude.</param>
    /// <param name="ignoreCase">Whether captured-value membership comparison should ignore case.</param>
    /// <param name="culture">The culture name for captured-value comparison, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotMatchesIn(Regex regex, IEnumerable<string> values, bool ignoreCase = false, string? culture = null)
        => NotMatchesInSet(regex, values, ignoreCase, culture);

    /// <summary>
    /// Captures negated regex capture-group membership against the selected string index.<br/>
    /// Identities match when the regex/group does not match or the selected group value is not in the supplied values.<br/>
    /// </summary>
    /// <param name="pattern">The regular expression pattern.</param>
    /// <param name="values">The capture-group values to exclude.</param>
    /// <param name="groupNumber">The regex group number to compare; zero compares the whole match.</param>
    /// <param name="ignoreCase">Whether regex matching and captured-value comparison should ignore case.</param>
    /// <param name="culture">The culture name for captured-value comparison, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotMatchesIn(string pattern, IEnumerable<string> values, int groupNumber, bool ignoreCase = false, string? culture = null)
        => NotMatchesInSet(pattern, values, groupNumber, ignoreCase, culture);

    /// <summary>
    /// Captures negated regex capture-group membership from a caller-provided <see cref="Regex"/> instance.<br/>
    /// Identities match when the regex/group does not match or the selected group value is not in the supplied values.<br/>
    /// </summary>
    /// <param name="regex">The regular expression instance.</param>
    /// <param name="values">The capture-group values to exclude.</param>
    /// <param name="groupNumber">The regex group number to compare; zero compares the whole match.</param>
    /// <param name="ignoreCase">Whether captured-value membership comparison should ignore case.</param>
    /// <param name="culture">The culture name for captured-value comparison, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotMatchesIn(Regex regex, IEnumerable<string> values, int groupNumber, bool ignoreCase = false, string? culture = null)
        => NotMatchesInSet(regex, values, groupNumber, ignoreCase, culture);

    /// <summary>
    /// Captures negated regex match-value membership against the selected string index using set terminology.<br/>
    /// Identities match when the regex does not match or the selected match value is not in the supplied values.<br/>
    /// </summary>
    /// <param name="pattern">The regular expression pattern.</param>
    /// <param name="values">The whole-match values to exclude.</param>
    /// <param name="ignoreCase">Whether regex matching and captured-value comparison should ignore case.</param>
    /// <param name="culture">The culture name for captured-value comparison, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotMatchesInSet(string pattern, IEnumerable<string> values, bool ignoreCase = false, string? culture = null)
        => AddText(LibraDexConditionOperatorKind.NotMatchesInSet, CreateRegexCaptureSetOperands(pattern, values, groupNumber: null), ignoreCase, culture);

    /// <summary>
    /// Captures negated regex match-value membership from a caller-provided <see cref="Regex"/> instance using set terminology.<br/>
    /// Identities match when the regex does not match or the whole match value is not in the supplied values.<br/>
    /// </summary>
    /// <param name="regex">The regular expression instance.</param>
    /// <param name="values">The whole-match values to exclude.</param>
    /// <param name="ignoreCase">Whether captured-value membership comparison should ignore case.</param>
    /// <param name="culture">The culture name for captured-value comparison, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotMatchesInSet(Regex regex, IEnumerable<string> values, bool ignoreCase = false, string? culture = null)
        => AddText(LibraDexConditionOperatorKind.NotMatchesInSet, CreateRegexCaptureSetOperands(regex, values, groupNumber: null), ignoreCase, culture);

    /// <summary>
    /// Captures negated regex capture-group membership against the selected string index using set terminology.<br/>
    /// Identities match when the regex/group does not match or the selected group value is not in the supplied values.<br/>
    /// </summary>
    /// <param name="pattern">The regular expression pattern.</param>
    /// <param name="values">The capture-group values to exclude.</param>
    /// <param name="groupNumber">The regex group number to compare; zero compares the whole match.</param>
    /// <param name="ignoreCase">Whether regex matching and captured-value comparison should ignore case.</param>
    /// <param name="culture">The culture name for captured-value comparison, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotMatchesInSet(string pattern, IEnumerable<string> values, int groupNumber, bool ignoreCase = false, string? culture = null)
        => AddText(LibraDexConditionOperatorKind.NotMatchesInSet, CreateRegexCaptureSetOperands(pattern, values, groupNumber), ignoreCase, culture);

    /// <summary>
    /// Captures negated regex capture-group membership from a caller-provided <see cref="Regex"/> instance using set terminology.<br/>
    /// Identities match when the regex/group does not match or the selected group value is not in the supplied values.<br/>
    /// </summary>
    /// <param name="regex">The regular expression instance.</param>
    /// <param name="values">The capture-group values to exclude.</param>
    /// <param name="groupNumber">The regex group number to compare; zero compares the whole match.</param>
    /// <param name="ignoreCase">Whether captured-value membership comparison should ignore case.</param>
    /// <param name="culture">The culture name for captured-value comparison, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotMatchesInSet(Regex regex, IEnumerable<string> values, int groupNumber, bool ignoreCase = false, string? culture = null)
        => AddText(LibraDexConditionOperatorKind.NotMatchesInSet, CreateRegexCaptureSetOperands(regex, values, groupNumber), ignoreCase, culture);

    private LibraDexConditionContinueOrEnd AddText(
        LibraDexConditionOperatorKind operatorKind,
        string? value,
        bool ignoreCase,
        string? culture,
        LibraDexStringComparisonPolicy? stringComparisonPolicy = null)
    {
        return AddText(operatorKind, new[] { LibraDexConditionOperand.Value(value) }, ignoreCase, culture, stringComparisonPolicy);
    }

    private LibraDexConditionContinueOrEnd AddText(
        LibraDexConditionOperatorKind operatorKind,
        object? value,
        bool ignoreCase,
        string? culture,
        LibraDexStringComparisonPolicy? stringComparisonPolicy = null)
    {
        return AddText(operatorKind, new[] { LibraDexConditionOperand.Value(value) }, ignoreCase, culture, stringComparisonPolicy);
    }

    private LibraDexConditionContinueOrEnd AddText(
        LibraDexConditionOperatorKind operatorKind,
        IReadOnlyList<LibraDexConditionOperand> operands,
        bool ignoreCase,
        string? culture,
        LibraDexStringComparisonPolicy? stringComparisonPolicy = null)
    {
        return builder.AddLeaf(new LibraDexConditionLeafDescriptor(
            indexSelector,
            LibraDexConditionValueKind.String,
            EffectiveOperator(operatorKind),
            operands,
            ignoreCase,
            culture,
            stringComparisonPolicy));
    }

    private static object CaptureStringMembershipInput(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values is HashSet<string> or ISet<string> or IReadOnlyCollection<string>
            ? values
            : values.ToArray();
    }

    private static LibraDexConditionOperand[] CreateRegexCaptureOperands(string pattern, string value, int? groupNumber)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(value);
        return groupNumber.HasValue
            ? new[] { LibraDexConditionOperand.Value(pattern), LibraDexConditionOperand.Value(value), LibraDexConditionOperand.Value(groupNumber.Value) }
            : new[] { LibraDexConditionOperand.Value(pattern), LibraDexConditionOperand.Value(value) };
    }

    private static LibraDexConditionOperand[] CreateRegexCaptureOperands(Regex regex, string value, int? groupNumber)
    {
        ArgumentNullException.ThrowIfNull(regex);
        ArgumentNullException.ThrowIfNull(value);
        return groupNumber.HasValue
            ? new[] { LibraDexConditionOperand.Value(regex), LibraDexConditionOperand.Value(value), LibraDexConditionOperand.Value(groupNumber.Value) }
            : new[] { LibraDexConditionOperand.Value(regex), LibraDexConditionOperand.Value(value) };
    }

    private static LibraDexConditionOperand[] CreateRegexCaptureSetOperands(string pattern, IEnumerable<string> values, int? groupNumber)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(values);
        return groupNumber.HasValue
            ? new[] { LibraDexConditionOperand.Value(pattern), LibraDexConditionOperand.Value(CaptureStringMembershipInput(values)), LibraDexConditionOperand.Value(groupNumber.Value) }
            : new[] { LibraDexConditionOperand.Value(pattern), LibraDexConditionOperand.Value(CaptureStringMembershipInput(values)) };
    }

    private static LibraDexConditionOperand[] CreateRegexCaptureSetOperands(Regex regex, IEnumerable<string> values, int? groupNumber)
    {
        ArgumentNullException.ThrowIfNull(regex);
        ArgumentNullException.ThrowIfNull(values);
        return groupNumber.HasValue
            ? new[] { LibraDexConditionOperand.Value(regex), LibraDexConditionOperand.Value(CaptureStringMembershipInput(values)), LibraDexConditionOperand.Value(groupNumber.Value) }
            : new[] { LibraDexConditionOperand.Value(regex), LibraDexConditionOperand.Value(CaptureStringMembershipInput(values)) };
    }
}

/// <summary>
/// Captures structured date/time adopted condition operators for one selected LibraDex index.<br/>
/// Date-part methods capture Abraxas-compatible descriptors; materialization chooses ordered ranges, same-index multi-ranges, or packed-field structured component predicates based on the selected operator.<br/>
/// </summary>
/// <typeparam name="TValue">The date-like CLR type accepted by the exact comparison operators.</typeparam>
public sealed class LibraDexDateConditionOperator<TValue> : LibraDexConditionOperator<TValue>
{
    private readonly LibraDexConditionBuilder builder;
    private readonly LibraDexConditionIndexSelector indexSelector;
    private readonly LibraDexConditionValueKind valueKind;

    internal LibraDexDateConditionOperator(LibraDexConditionBuilder builder, LibraDexConditionIndexSelector indexSelector, LibraDexConditionValueKind valueKind, bool negate = false)
        : base(builder, indexSelector, valueKind, negate)
    {
        this.builder = builder;
        this.indexSelector = indexSelector;
        this.valueKind = valueKind;
    }

    /// <summary>
    /// Negates the next date/time predicate over the selected index.<br/>
    /// This preserves structured date helpers while mapping supported exact, range, and component predicates to their existing inverse descriptors.<br/>
    /// </summary>
    public new LibraDexDateConditionOperator<TValue> Not => new(builder, indexSelector, valueKind, !IsNegated);

    /// <summary>
    /// Captures a structured year-part condition.<br/>
    /// </summary>
    /// <param name="year">The year component to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd YearEqualTo(int year)
        => AddDatePart(LibraDexConditionOperatorKind.YearEqualTo, year);

    /// <summary>
    /// Captures the Abraxas-style structured year equality branch.<br/>
    /// This is an alias for <see cref="YearEqualTo(int)"/> so copied date-condition code can preserve the source grammar while LibraDex keeps one execution bridge.<br/>
    /// </summary>
    /// <param name="year">The year component to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd YearEqual(int year)
        => YearEqualTo(year);

    /// <summary>
    /// Captures a structured year inequality condition.<br/>
    /// The current bridge can execute this as the complement of one ordered year range over the logical date index.<br/>
    /// </summary>
    /// <param name="year">The year component to exclude.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd YearNotEqual(int year)
        => AddDatePart(LibraDexConditionOperatorKind.YearNotEqualTo, year);

    /// <summary>
    /// Captures a structured year membership condition.<br/>
    /// Each year is one contiguous ordered range; the bridge can execute the set as a same-index union of those ranges.<br/>
    /// </summary>
    /// <param name="years">The year components to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd YearIn(params int[] years)
        => AddDateComponentSet(LibraDexConditionOperatorKind.YearIn, years);

    /// <summary>
    /// Captures a structured year non-membership condition.<br/>
    /// The current bridge can execute this as the complement of a same-index union of ordered year ranges.<br/>
    /// </summary>
    /// <param name="years">The year components to exclude.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd YearNotIn(params int[] years)
        => AddDateComponentSet(LibraDexConditionOperatorKind.YearNotIn, years);

    /// <summary>
    /// Captures a structured contiguous year-range condition.<br/>
    /// The structured date codec stores year in the high ordered key bits, so this condition can execute as one contiguous range over the logical date index.<br/>
    /// </summary>
    /// <param name="startYear">The inclusive lower year component.</param>
    /// <param name="endYear">The inclusive upper year component.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd YearRange(int startYear, int endYear)
        => AddDateParts(LibraDexConditionOperatorKind.YearRange, startYear, endYear);

    /// <summary>
    /// Captures a structured year non-range condition.<br/>
    /// The current bridge can execute this as the complement of one contiguous ordered year range.<br/>
    /// </summary>
    /// <param name="startYear">The inclusive lower year component.</param>
    /// <param name="endYear">The inclusive upper year component.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd YearNotRange(int startYear, int endYear)
        => AddDateParts(LibraDexConditionOperatorKind.YearNotRange, startYear, endYear);

    /// <summary>
    /// Captures a structured lower year boundary condition.<br/>
    /// The bridge expands this to a single ordered range from the supplied year through the maximum supported date value.<br/>
    /// </summary>
    /// <param name="year">The inclusive lower year component.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd OnOrAfterYear(int year)
        => AddDatePart(LibraDexConditionOperatorKind.YearOnOrAfter, year);

    /// <summary>
    /// Captures a structured upper year boundary condition.<br/>
    /// The bridge expands this to a single ordered range from the minimum supported date value through the supplied year.<br/>
    /// </summary>
    /// <param name="year">The inclusive upper year component.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd OnOrBeforeYear(int year)
        => AddDatePart(LibraDexConditionOperatorKind.YearOnOrBefore, year);

    /// <summary>
    /// Captures a structured month-part condition.<br/>
    /// </summary>
    /// <param name="month">The month component to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd MonthEqualTo(int month)
        => AddDatePart(LibraDexConditionOperatorKind.MonthEqualTo, month);

    /// <summary>
    /// Captures the Abraxas-style month equality branch.<br/>
    /// This branch is component-only across all years and executes through the structured component primitive by reading the packed month bits.<br/>
    /// </summary>
    /// <param name="month">The month component to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd MonthEqual(int month)
        => MonthEqualTo(month);

    /// <summary>
    /// Captures a month component membership branch.<br/>
    /// This branch is component-only across all years and executes through the structured component primitive by reading the packed month bits.<br/>
    /// </summary>
    /// <param name="months">The month components to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd MonthIn(params int[] months)
        => AddDateComponentSet(LibraDexConditionOperatorKind.MonthIn, months);

    /// <summary>
    /// Captures a month component non-membership branch.<br/>
    /// This branch is component-only across all years and executes through the structured component primitive by negating a packed month membership test.<br/>
    /// </summary>
    /// <param name="months">The month components to exclude.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd MonthNotIn(params int[] months)
        => AddDateComponentSet(LibraDexConditionOperatorKind.MonthNotIn, months);

    /// <summary>
    /// Captures a month component range branch.<br/>
    /// This branch is not contiguous in the full ordered date key across all years, so it executes through the structured component primitive rather than an ordered range.<br/>
    /// </summary>
    /// <param name="startMonth">The inclusive lower month component.</param>
    /// <param name="endMonth">The inclusive upper month component.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd MonthRange(int startMonth, int endMonth)
        => AddDateParts(LibraDexConditionOperatorKind.MonthRange, startMonth, endMonth);

    /// <summary>
    /// Captures a month component non-range branch.<br/>
    /// This branch is component-only across all years and executes through the structured component primitive by negating a packed month range test.<br/>
    /// </summary>
    /// <param name="startMonth">The inclusive lower month component to exclude.</param>
    /// <param name="endMonth">The inclusive upper month component to exclude.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd MonthNotInRange(int startMonth, int endMonth)
        => AddDateParts(LibraDexConditionOperatorKind.MonthNotRange, startMonth, endMonth);

    /// <summary>
    /// Captures a structured day-part condition.<br/>
    /// </summary>
    /// <param name="day">The day component to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd DayEqualTo(int day)
        => AddDatePart(LibraDexConditionOperatorKind.DayEqualTo, day);

    /// <summary>
    /// Captures the Abraxas-style day equality branch.<br/>
    /// This branch is component-only across all months and years and executes through the structured component primitive by reading the packed day bits.<br/>
    /// </summary>
    /// <param name="day">The day component to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd DayEqual(int day)
        => DayEqualTo(day);

    /// <summary>
    /// Captures a day component membership branch.<br/>
    /// This branch is component-only across all months and years and executes through the structured component primitive by reading the packed day bits.<br/>
    /// </summary>
    /// <param name="days">The day components to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd DayIn(params int[] days)
        => AddDateComponentSet(LibraDexConditionOperatorKind.DayIn, days);

    /// <summary>
    /// Captures a day component range branch.<br/>
    /// This branch is component-only and executes through the structured component primitive rather than an ordered range.<br/>
    /// </summary>
    /// <param name="startDay">The inclusive lower day component.</param>
    /// <param name="endDay">The inclusive upper day component.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd DayRange(int startDay, int endDay)
        => AddDateParts(LibraDexConditionOperatorKind.DayRange, startDay, endDay);

    /// <summary>
    /// Captures a day component non-range branch.<br/>
    /// This branch is component-only and executes through the structured component primitive by negating a packed day range test.<br/>
    /// </summary>
    /// <param name="startDay">The inclusive lower day component to exclude.</param>
    /// <param name="endDay">The inclusive upper day component to exclude.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotDayRange(int startDay, int endDay)
        => AddDateParts(LibraDexConditionOperatorKind.DayNotRange, startDay, endDay);

    /// <summary>
    /// Captures a structured quarter-part condition.<br/>
    /// </summary>
    /// <param name="quarter">The quarter component to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd QuarterEqualTo(int quarter)
        => AddDatePart(LibraDexConditionOperatorKind.QuarterEqualTo, quarter);

    /// <summary>
    /// Captures the Abraxas-style quarter branch across all years.<br/>
    /// This branch is month-derived and component-only, so it executes through the structured component primitive over packed month bits.<br/>
    /// </summary>
    /// <param name="quarter">The quarter component to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd InQuarter(int quarter)
        => AddDatePart(LibraDexConditionOperatorKind.InQuarter, quarter);

    /// <summary>
    /// Captures the Abraxas-style quarter range branch across all years.<br/>
    /// This branch is month-derived and component-only, so it executes through the structured component primitive over packed month bits.<br/>
    /// </summary>
    /// <param name="startQuarter">The inclusive lower quarter component.</param>
    /// <param name="endQuarter">The inclusive upper quarter component.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd InQuarterRange(int startQuarter, int endQuarter)
        => AddDateParts(LibraDexConditionOperatorKind.InQuarterRange, startQuarter, endQuarter);

    /// <summary>
    /// Captures a structured year/month condition.<br/>
    /// The structured date codec stores year and month in the high ordered key bits, so this condition can execute as one contiguous range over the logical date index.<br/>
    /// </summary>
    /// <param name="year">The year component to match.</param>
    /// <param name="month">The month component to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd YearMonth(int year, int month)
        => AddDateParts(LibraDexConditionOperatorKind.YearMonth, year, month);

    /// <summary>
    /// Captures a structured year/month membership condition.<br/>
    /// Each supplied year/month pair is one contiguous range over the logical date index; the bridge can execute the full set as a same-index union of those ranges.<br/>
    /// </summary>
    /// <param name="values">The year/month pairs to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd YearMonthIn(IEnumerable<(int year, int month)> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return AddDateTupleSet(LibraDexConditionOperatorKind.YearMonthIn, values.ToArray());
    }

    /// <summary>
    /// Captures a structured year/month/day condition.<br/>
    /// The structured date codec stores year, month, and day in the high ordered key bits, so this condition can execute as one contiguous range over the logical date index.<br/>
    /// </summary>
    /// <param name="year">The year component to match.</param>
    /// <param name="month">The month component to match.</param>
    /// <param name="day">The day component to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd YearMonthDay(int year, int month, int day)
        => AddDateParts(LibraDexConditionOperatorKind.YearMonthDay, year, month, day);

    /// <summary>
    /// Captures a structured year/month/day membership condition.<br/>
    /// Each supplied year/month/day tuple is one contiguous range over the logical date index; the bridge can execute the full set as a same-index union of those ranges.<br/>
    /// </summary>
    /// <param name="values">The year/month/day tuples to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd YearMonthDayIn(IEnumerable<(int year, int month, int day)> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return AddDateTupleSet(LibraDexConditionOperatorKind.YearMonthDayIn, values.ToArray());
    }

    /// <summary>
    /// Captures one year with several month components.<br/>
    /// Each month inside the year is a contiguous ordered range, so the bridge can execute this as a same-index union without scanning.<br/>
    /// </summary>
    /// <param name="year">The year component to match.</param>
    /// <param name="months">The month components to match inside the supplied year.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd YearInMonths(int year, params int[] months)
        => AddDateTupleSet(LibraDexConditionOperatorKind.YearInMonths, (year, months));

    /// <summary>
    /// Captures one month/day tuple across all years.<br/>
    /// This branch is not contiguous in the ordered date key across all years, so it executes through the structured component primitive over packed month and day bits.<br/>
    /// </summary>
    /// <param name="month">The month component to match.</param>
    /// <param name="day">The day component to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd MonthDay(int month, int day)
        => AddDateParts(LibraDexConditionOperatorKind.MonthDay, month, day);

    /// <summary>
    /// Captures one year and quarter.<br/>
    /// The selected quarter inside one year is a contiguous ordered range and can execute through the current date range bridge.<br/>
    /// </summary>
    /// <param name="year">The year component to match.</param>
    /// <param name="quarter">The quarter component to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd YearQuarter(int year, int quarter)
        => AddDateParts(LibraDexConditionOperatorKind.YearQuarter, year, quarter);

    /// <summary>
    /// Captures the quarter-start calendar branch.<br/>
    /// This branch is component-derived across all years and executes through packed month/day component tests.<br/>
    /// </summary>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd IsQuarterStart()
        => AddDateParts(LibraDexConditionOperatorKind.IsQuarterStart);

    /// <summary>
    /// Captures the quarter-end calendar branch.<br/>
    /// This branch is component-derived across all years and executes through packed month bits plus a packed year/month/day last-day check.<br/>
    /// </summary>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd IsQuarterEnd()
        => AddDateParts(LibraDexConditionOperatorKind.IsQuarterEnd);

    /// <summary>
    /// Captures the half-year-start calendar branch.<br/>
    /// This branch is component-derived across all years and executes through packed month/day component tests.<br/>
    /// </summary>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd IsHalfYearStart()
        => AddDateParts(LibraDexConditionOperatorKind.IsHalfYearStart);

    /// <summary>
    /// Captures the half-year-end calendar branch.<br/>
    /// This branch is component-derived across all years and executes through packed month bits plus a packed year/month/day last-day check.<br/>
    /// </summary>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd IsHalfYearEnd()
        => AddDateParts(LibraDexConditionOperatorKind.IsHalfYearEnd);

    /// <summary>
    /// Captures the first-of-month calendar branch.<br/>
    /// This branch is component-only across all months and years and executes through the packed day component.<br/>
    /// </summary>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd IsFirstOfMonth()
        => AddDateParts(LibraDexConditionOperatorKind.IsFirstOfMonth);

    /// <summary>
    /// Captures the last-of-month calendar branch.<br/>
    /// This branch depends on month length and leap-year semantics, so it executes through packed year/month/day fields and integer calendar arithmetic.<br/>
    /// </summary>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd IsLastOfMonth()
        => AddDateParts(LibraDexConditionOperatorKind.IsLastOfMonth);

    /// <summary>
    /// Captures the current UTC day branch.<br/>
    /// The bridge evaluates the current UTC date at materialization time and expands it to the appropriate DateTime, DateTimeOffset, or DateOnly range.<br/>
    /// </summary>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd IsToday()
        => AddDateParts(LibraDexConditionOperatorKind.IsToday);

    /// <summary>
    /// Captures the previous UTC day branch.<br/>
    /// The bridge evaluates the previous UTC date at materialization time and expands it to the appropriate DateTime, DateTimeOffset, or DateOnly range.<br/>
    /// </summary>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd IsYesterday()
        => AddDateParts(LibraDexConditionOperatorKind.IsYesterday);

    /// <summary>
    /// Captures a trailing UTC day-window branch.<br/>
    /// The bridge evaluates the current UTC date at materialization time and expands the supplied day count to a contiguous ordered range.<br/>
    /// </summary>
    /// <param name="days">The number of trailing UTC days to include.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd IsInLastDays(int days)
        => AddDatePart(LibraDexConditionOperatorKind.IsInLastDays, days);

    /// <summary>
    /// Captures a trailing UTC hour-window branch.<br/>
    /// This branch applies only to DateTime and DateTimeOffset indexes because DateOnly has no hour component.<br/>
    /// </summary>
    /// <param name="hours">The number of trailing UTC hours to include.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd IsInLastHours(int hours)
        => AddDatePart(LibraDexConditionOperatorKind.IsInLastHours, hours);

    /// <summary>
    /// Captures a trailing UTC minute-window branch.<br/>
    /// This branch applies only to DateTime and DateTimeOffset indexes because DateOnly has no minute component.<br/>
    /// </summary>
    /// <param name="minutes">The number of trailing UTC minutes to include.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd IsInLastMinutes(int minutes)
        => AddDatePart(LibraDexConditionOperatorKind.IsInLastMinutes, minutes);

    /// <summary>
    /// Captures the weekend calendar branch.<br/>
    /// This branch is day-of-week derived and executes through the packed day-of-week bits.<br/>
    /// </summary>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd IsWeekend()
        => AddDateParts(LibraDexConditionOperatorKind.IsWeekend);

    /// <summary>
    /// Captures the weekday calendar branch.<br/>
    /// This branch is day-of-week derived and executes through the packed day-of-week bits.<br/>
    /// </summary>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd IsWeekday()
        => AddDateParts(LibraDexConditionOperatorKind.IsWeekday);

    /// <summary>
    /// Captures the morning time-of-day branch.<br/>
    /// DateTime and DateTimeOffset indexes execute this through the packed hour component; TimeOnly indexes use an ordered time range.<br/>
    /// </summary>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd IsMorning()
        => AddDateParts(LibraDexConditionOperatorKind.IsMorning);

    /// <summary>
    /// Captures the afternoon time-of-day branch.<br/>
    /// DateTime and DateTimeOffset indexes execute this through the packed hour component; TimeOnly indexes use an ordered time range.<br/>
    /// </summary>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd IsAfternoon()
        => AddDateParts(LibraDexConditionOperatorKind.IsAfternoon);

    /// <summary>
    /// Captures the evening time-of-day branch.<br/>
    /// DateTime and DateTimeOffset indexes execute this through the packed hour component; TimeOnly indexes use an ordered time range.<br/>
    /// </summary>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd IsEvening()
        => AddDateParts(LibraDexConditionOperatorKind.IsEvening);

    /// <summary>
    /// Captures the night time-of-day branch.<br/>
    /// DateTime and DateTimeOffset indexes execute this through packed hour membership; TimeOnly indexes use two ordered time ranges around midnight.<br/>
    /// </summary>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd IsNight()
        => AddDateParts(LibraDexConditionOperatorKind.IsNight);

    private LibraDexConditionContinueOrEnd AddDatePart(LibraDexConditionOperatorKind operatorKind, int value)
        => AddDateParts(operatorKind, value);

    private LibraDexConditionContinueOrEnd AddDateParts(LibraDexConditionOperatorKind operatorKind, params int[] values)
    {
        return builder.AddLeaf(new LibraDexConditionLeafDescriptor(
            indexSelector,
            valueKind,
            EffectiveOperator(operatorKind),
            values.Select(static value => LibraDexConditionOperand.Value(value)).ToArray(),
            IgnoreCase: false,
            Culture: null));
    }

    /// <summary>
    /// Captures a date component membership set as one operand so later planning can distinguish one set-valued condition from several scalar component operands.<br/>
    /// This matters for branches such as `YearIn`, where the current bridge can union contiguous year ranges, and branches such as `MonthIn`, where the structured component primitive compiles the set into a packed-field bit mask.<br/>
    /// </summary>
    /// <param name="operatorKind">The date component set operator to capture.</param>
    /// <param name="values">The component values to capture.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    private LibraDexConditionContinueOrEnd AddDateComponentSet(LibraDexConditionOperatorKind operatorKind, int[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return AddDateTupleSet(operatorKind, values);
    }

    /// <summary>
    /// Captures a date tuple set as one operand so the bridge can preserve the caller's selected pairs or tuples as a single condition payload.<br/>
    /// The tuple payload is intentionally separate from scalar component operands used by single-range date operators.<br/>
    /// </summary>
    /// <param name="operatorKind">The date tuple-set operator to capture.</param>
    /// <param name="value">The tuple-set payload.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    private LibraDexConditionContinueOrEnd AddDateTupleSet(LibraDexConditionOperatorKind operatorKind, object value)
    {
        return builder.AddLeaf(new LibraDexConditionLeafDescriptor(
            indexSelector,
            valueKind,
            EffectiveOperator(operatorKind),
            new[] { LibraDexConditionOperand.Value(value) },
            IgnoreCase: false,
            Culture: null));
    }
}

/// <summary>
/// Captures binary-specific adopted condition operators for one selected LibraDex index.<br/>
/// </summary>
public sealed class LibraDexBinaryConditionOperator : LibraDexConditionOperator<byte[]>
{
    private readonly LibraDexConditionBuilder builder;
    private readonly LibraDexConditionIndexSelector indexSelector;

    internal LibraDexBinaryConditionOperator(LibraDexConditionBuilder builder, LibraDexConditionIndexSelector indexSelector, bool negate = false)
        : base(builder, indexSelector, LibraDexConditionValueKind.Binary, negate)
    {
        this.builder = builder;
        this.indexSelector = indexSelector;
    }

    /// <summary>
    /// Negates the next binary predicate over the selected index.<br/>
    /// Exact equality, inequality, membership, and supported range-like descriptors map to their inverse operators; unsupported pattern negation fails when the predicate is captured.<br/>
    /// </summary>
    public new LibraDexBinaryConditionOperator Not => new(builder, indexSelector, !IsNegated);

    /// <summary>
    /// Captures binary equality against an explicit null or empty key state.<br/>
    /// `NullKey.Null` maps to the stored binary null route, `NullKey.Empty` maps to the stored empty-byte route, and `NullKey.NullOrEmpty` maps to both key-state routes.<br/>
    /// </summary>
    /// <param name="keyState">The key-state sentinel to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd EqualTo(NullKey keyState)
    {
        return keyState switch
        {
            NullKey.Null or NullKey.Empty or NullKey.NullOrEmpty => Add(LibraDexConditionOperatorKind.EqualTo, LibraDexConditionOperand.Value(keyState)),
            _ => throw new ArgumentOutOfRangeException(nameof(keyState), keyState, "Unknown null-key state.")
        };
    }

    /// <summary>
    /// Captures binary equality against the stored null-key sentinel from <see cref="DBNull.Value"/>.<br/>
    /// This overload keeps database-shaped call sites explicit and routes to <see cref="NullKey.Null"/> instead of comparing against the `DBNull` object itself.<br/>
    /// </summary>
    /// <param name="value">The database null sentinel; normally <see cref="DBNull.Value"/>.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd EqualTo(DBNull value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return EqualTo(NullKey.Null);
    }

    /// <summary>
    /// Captures binary inequality against an explicit null or empty key state.<br/>
    /// `NullKey.NullOrEmpty` maps to the identity-universe complement of both key-state routes, excluding explicit null and explicit empty without allocating a caller-side set.<br/>
    /// </summary>
    /// <param name="keyState">The key-state sentinel to exclude.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotEqualTo(NullKey keyState)
    {
        return keyState switch
        {
            NullKey.Null or NullKey.Empty or NullKey.NullOrEmpty => Add(LibraDexConditionOperatorKind.NotEqualTo, LibraDexConditionOperand.Value(keyState)),
            _ => throw new ArgumentOutOfRangeException(nameof(keyState), keyState, "Unknown null-key state.")
        };
    }

    /// <summary>
    /// Captures binary inequality against the stored null-key sentinel from <see cref="DBNull.Value"/>.<br/>
    /// This overload keeps database-shaped call sites explicit and routes to <see cref="NullKey.Null"/> instead of comparing against the `DBNull` object itself.<br/>
    /// </summary>
    /// <param name="value">The database null sentinel; normally <see cref="DBNull.Value"/>.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotEqualTo(DBNull value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return NotEqualTo(NullKey.Null);
    }

    /// <summary>
    /// Captures a raw binary starts-with condition.<br/>
    /// The descriptor materializes as an encoded key-byte predicate over fixed-width byte-array keys.<br/>
    /// </summary>
    /// <param name="value">The byte prefix to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd StartsWith(byte[] value)
        => AddBinary(LibraDexConditionOperatorKind.StartsWith, value);

    /// <summary>
    /// Captures a raw binary starts-with condition from a readable hexadecimal pattern.<br/>
    /// Hex digits select nibbles, `x` or `X` select wildcard nibbles, and common separators are ignored before execution compares stored key bytes directly.<br/>
    /// </summary>
    /// <param name="hexPattern">The byte-aligned hexadecimal prefix pattern.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd StartsWithHex(string hexPattern)
        => AddBinaryPattern(LibraDexConditionOperatorKind.StartsWith, LibraDexBinaryPatternPredicate.CreateHex(LibraDexBinaryPatternMode.StartsWith, hexPattern));

    /// <summary>
    /// Captures a raw binary ends-with condition.<br/>
    /// The descriptor materializes as an encoded key-byte predicate over fixed-width byte-array keys.<br/>
    /// </summary>
    /// <param name="value">The byte suffix to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd EndsWith(byte[] value)
        => AddBinary(LibraDexConditionOperatorKind.EndsWith, value);

    /// <summary>
    /// Captures a raw binary ends-with condition from a readable hexadecimal pattern.<br/>
    /// Hex digits select nibbles, `x` or `X` select wildcard nibbles, and common separators are ignored before execution compares stored key bytes directly.<br/>
    /// </summary>
    /// <param name="hexPattern">The byte-aligned hexadecimal suffix pattern.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd EndsWithHex(string hexPattern)
        => AddBinaryPattern(LibraDexConditionOperatorKind.EndsWith, LibraDexBinaryPatternPredicate.CreateHex(LibraDexBinaryPatternMode.EndsWith, hexPattern));

    /// <summary>
    /// Captures a raw binary contains condition.<br/>
    /// The descriptor materializes as an encoded key-byte predicate over fixed-width byte-array keys.<br/>
    /// </summary>
    /// <param name="value">The contiguous byte sequence to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd Contains(byte[] value)
        => AddBinary(LibraDexConditionOperatorKind.Contains, value);

    /// <summary>
    /// Captures a raw binary contains condition from a readable hexadecimal pattern.<br/>
    /// Hex digits select nibbles, `x` or `X` select wildcard nibbles, and common separators are ignored before execution compares stored key bytes directly.<br/>
    /// </summary>
    /// <param name="hexPattern">The byte-aligned hexadecimal contained pattern.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd ContainsHex(string hexPattern)
        => AddBinaryPattern(LibraDexConditionOperatorKind.Contains, LibraDexBinaryPatternPredicate.CreateHex(LibraDexBinaryPatternMode.Contains, hexPattern));

    /// <summary>
    /// Captures a raw binary full-key pattern from a readable hexadecimal pattern.<br/>
    /// The cleaned pattern must be byte-aligned and match the fixed binary key length; `x` or `X` are wildcard nibbles.<br/>
    /// </summary>
    /// <param name="hexPattern">The byte-aligned full-key hexadecimal pattern.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd MatchesHexPattern(string hexPattern)
        => AddBinaryPattern(LibraDexConditionOperatorKind.MatchesPattern, LibraDexBinaryPatternPredicate.CreateHex(LibraDexBinaryPatternMode.MatchesPattern, hexPattern));

    /// <summary>
    /// Captures a raw binary full-key pattern from a readable hexadecimal pattern using the short public spelling.<br/>
    /// This is the preferred alias for <see cref="MatchesHexPattern(string)"/> and preserves the same byte-aligned wildcard semantics.<br/>
    /// </summary>
    /// <param name="hexPattern">The byte-aligned full-key hexadecimal pattern.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd Matches(string hexPattern)
        => MatchesHexPattern(hexPattern);

    /// <summary>
    /// Captures a raw binary fixed-slice equality condition.<br/>
    /// The zero-based offset and expected bytes mirror Abraxas binary slice intent while keeping LibraDex execution over encoded key bytes.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset at which the expected sequence must start.</param>
    /// <param name="value">The byte sequence that must equal the selected slice.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd SliceEqual(int offset, byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Binary slice offset cannot be negative.");
        }

        return builder.AddLeaf(new LibraDexConditionLeafDescriptor(
            indexSelector,
            LibraDexConditionValueKind.Binary,
            LibraDexConditionOperatorKind.BinarySliceEqual,
            new[]
            {
                LibraDexConditionOperand.Value(offset),
                LibraDexConditionOperand.Value((byte[])value.Clone())
            },
            IgnoreCase: false,
            Culture: null));
    }

    /// <summary>
    /// Captures a raw binary fixed-slice pattern from a readable hexadecimal pattern.<br/>
    /// Hex digits select nibbles, `x` or `X` select wildcard nibbles, and common separators are ignored before execution compares the selected stored key slice directly.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset at which the pattern must start.</param>
    /// <param name="hexPattern">The byte-aligned hexadecimal slice pattern.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd SliceMatchesHex(int offset, string hexPattern)
    {
        return AddBinaryPattern(
            LibraDexConditionOperatorKind.BinarySliceEqual,
            LibraDexBinaryPatternPredicate.CreateHex(LibraDexBinaryPatternMode.SliceEqual, hexPattern, offset));
    }

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as a little-endian Int32.<br/>
    /// The returned operator records typed comparisons that execute over the selected key bytes without decoding the whole binary key.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the Int32 slice.</param>
    /// <returns>A typed binary slice operator.</returns>
    public LibraDexBinaryTypedSliceConditionOperator<int> SlicedAsInt32(int offset)
        => new LibraDexBinaryTypedSliceConditionOperator<int>(builder, indexSelector, LibraDexBinarySliceValueKind.Int32, offset, sizeof(int));

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as an Int8.<br/>
    /// The returned operator records typed comparisons that execute over the selected key byte without decoding the whole binary key.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the Int8 slice.</param>
    /// <returns>A typed binary slice operator.</returns>
    public LibraDexBinaryTypedSliceConditionOperator<sbyte> SlicedAsSByte(int offset)
        => new LibraDexBinaryTypedSliceConditionOperator<sbyte>(builder, indexSelector, LibraDexBinarySliceValueKind.Int8, offset, sizeof(byte));

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as a UInt8.<br/>
    /// The returned operator records typed comparisons that execute over the selected key byte without decoding the whole binary key.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the UInt8 slice.</param>
    /// <returns>A typed binary slice operator.</returns>
    public LibraDexBinaryTypedSliceConditionOperator<byte> SlicedAsByte(int offset)
        => new LibraDexBinaryTypedSliceConditionOperator<byte>(builder, indexSelector, LibraDexBinarySliceValueKind.UInt8, offset, sizeof(byte));

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as a little-endian Int16.<br/>
    /// The returned operator records typed comparisons that execute over the selected key bytes without decoding the whole binary key.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the Int16 slice.</param>
    /// <returns>A typed binary slice operator.</returns>
    public LibraDexBinaryTypedSliceConditionOperator<short> SlicedAsInt16(int offset)
        => new LibraDexBinaryTypedSliceConditionOperator<short>(builder, indexSelector, LibraDexBinarySliceValueKind.Int16, offset, sizeof(short));

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as a little-endian UInt16.<br/>
    /// The returned operator records typed comparisons that execute over the selected key bytes without decoding the whole binary key.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the UInt16 slice.</param>
    /// <returns>A typed binary slice operator.</returns>
    public LibraDexBinaryTypedSliceConditionOperator<ushort> SlicedAsUInt16(int offset)
        => new LibraDexBinaryTypedSliceConditionOperator<ushort>(builder, indexSelector, LibraDexBinarySliceValueKind.UInt16, offset, sizeof(ushort));

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as a little-endian UInt32.<br/>
    /// The returned operator records typed comparisons that execute over the selected key bytes without decoding the whole binary key.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the UInt32 slice.</param>
    /// <returns>A typed binary slice operator.</returns>
    public LibraDexBinaryTypedSliceConditionOperator<uint> SlicedAsUInt32(int offset)
        => new LibraDexBinaryTypedSliceConditionOperator<uint>(builder, indexSelector, LibraDexBinarySliceValueKind.UInt32, offset, sizeof(uint));

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as a little-endian Int64.<br/>
    /// The returned operator records typed comparisons that execute over the selected key bytes without decoding the whole binary key.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the Int64 slice.</param>
    /// <returns>A typed binary slice operator.</returns>
    public LibraDexBinaryTypedSliceConditionOperator<long> SlicedAsInt64(int offset)
        => new LibraDexBinaryTypedSliceConditionOperator<long>(builder, indexSelector, LibraDexBinarySliceValueKind.Int64, offset, sizeof(long));

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as a little-endian UInt64.<br/>
    /// The returned operator records typed comparisons that execute over the selected key bytes without decoding the whole binary key.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the UInt64 slice.</param>
    /// <returns>A typed binary slice operator.</returns>
    public LibraDexBinaryTypedSliceConditionOperator<ulong> SlicedAsUInt64(int offset)
        => new LibraDexBinaryTypedSliceConditionOperator<ulong>(builder, indexSelector, LibraDexBinarySliceValueKind.UInt64, offset, sizeof(ulong));

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as a little-endian Single.<br/>
    /// The returned operator records typed comparisons that execute over the selected key bytes without decoding the whole binary key.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the Single slice.</param>
    /// <returns>A typed binary slice operator.</returns>
    public LibraDexBinaryTypedSliceConditionOperator<float> SlicedAsSingle(int offset)
        => new LibraDexBinaryTypedSliceConditionOperator<float>(builder, indexSelector, LibraDexBinarySliceValueKind.Single, offset, sizeof(float));

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as a little-endian Double.<br/>
    /// The returned operator records typed comparisons that execute over the selected key bytes without decoding the whole binary key.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the Double slice.</param>
    /// <returns>A typed binary slice operator.</returns>
    public LibraDexBinaryTypedSliceConditionOperator<double> SlicedAsDouble(int offset)
        => new LibraDexBinaryTypedSliceConditionOperator<double>(builder, indexSelector, LibraDexBinarySliceValueKind.Double, offset, sizeof(double));

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as a Decimal.<br/>
    /// The returned operator records typed comparisons that execute over the selected key bytes without decoding the whole binary key.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the Decimal slice.</param>
    /// <returns>A typed binary slice operator.</returns>
    public LibraDexBinaryTypedSliceConditionOperator<decimal> SlicedAsDecimal(int offset)
        => new LibraDexBinaryTypedSliceConditionOperator<decimal>(builder, indexSelector, LibraDexBinarySliceValueKind.Decimal, offset, 16);

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as a little-endian Int128.<br/>
    /// The returned operator records typed comparisons that execute over the selected key bytes without decoding the whole binary key.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the Int128 slice.</param>
    /// <returns>A typed binary slice operator.</returns>
    public LibraDexBinaryTypedSliceConditionOperator<Int128> SlicedAsInt128(int offset)
        => new LibraDexBinaryTypedSliceConditionOperator<Int128>(builder, indexSelector, LibraDexBinarySliceValueKind.Int128, offset, 16);

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as a little-endian UInt128.<br/>
    /// The returned operator records typed comparisons that execute over the selected key bytes without decoding the whole binary key.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the UInt128 slice.</param>
    /// <returns>A typed binary slice operator.</returns>
    public LibraDexBinaryTypedSliceConditionOperator<UInt128> SlicedAsUInt128(int offset)
        => new LibraDexBinaryTypedSliceConditionOperator<UInt128>(builder, indexSelector, LibraDexBinarySliceValueKind.UInt128, offset, 16);

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as a little-endian BigInteger.<br/>
    /// The returned operator records typed comparisons that execute over the selected key bytes without decoding the whole binary key.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the BigInteger slice.</param>
    /// <param name="byteLength">The byte length of the BigInteger slice.</param>
    /// <returns>A typed binary slice operator.</returns>
    public LibraDexBinaryTypedSliceConditionOperator<BigInteger> SlicedAsBigInteger(int offset, int byteLength)
        => new LibraDexBinaryTypedSliceConditionOperator<BigInteger>(builder, indexSelector, LibraDexBinarySliceValueKind.BigInteger, offset, byteLength);

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as a Guid.<br/>
    /// The returned operator records typed comparisons that execute over the selected 16 key bytes without decoding the whole binary key.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the Guid slice.</param>
    /// <returns>A typed binary slice operator.</returns>
    public LibraDexBinaryTypedSliceConditionOperator<Guid> SlicedAsGuid(int offset)
        => new LibraDexBinaryTypedSliceConditionOperator<Guid>(builder, indexSelector, LibraDexBinarySliceValueKind.Guid, offset, 16);

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as little-endian DateTime ticks.<br/>
    /// The returned operator records typed comparisons that execute over the selected key bytes without decoding the whole binary key.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the DateTime tick slice.</param>
    /// <returns>A typed binary slice operator.</returns>
    public LibraDexBinaryTypedSliceConditionOperator<DateTime> SlicedAsDateTime(int offset)
        => new LibraDexBinaryTypedSliceConditionOperator<DateTime>(builder, indexSelector, LibraDexBinarySliceValueKind.DateTimeTicks, offset, sizeof(long));

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as a DateOnly day number.<br/>
    /// The returned operator records typed comparisons that execute over the selected key bytes without decoding the whole binary key.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the DateOnly slice.</param>
    /// <returns>A typed binary slice operator.</returns>
    public LibraDexBinaryTypedSliceConditionOperator<DateOnly> SlicedAsDateOnly(int offset)
        => new LibraDexBinaryTypedSliceConditionOperator<DateOnly>(builder, indexSelector, LibraDexBinarySliceValueKind.DateOnly, offset, sizeof(int));

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as TimeOnly ticks.<br/>
    /// The returned operator records typed comparisons that execute over the selected key bytes without decoding the whole binary key.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the TimeOnly slice.</param>
    /// <returns>A typed binary slice operator.</returns>
    public LibraDexBinaryTypedSliceConditionOperator<TimeOnly> SlicedAsTimeOnly(int offset)
        => new LibraDexBinaryTypedSliceConditionOperator<TimeOnly>(builder, indexSelector, LibraDexBinarySliceValueKind.TimeOnly, offset, sizeof(long));

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as TimeSpan ticks.<br/>
    /// The returned operator records typed comparisons that execute over the selected key bytes without decoding the whole binary key.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the TimeSpan slice.</param>
    /// <returns>A typed binary slice operator.</returns>
    public LibraDexBinaryTypedSliceConditionOperator<TimeSpan> SlicedAsTimeSpan(int offset)
        => new LibraDexBinaryTypedSliceConditionOperator<TimeSpan>(builder, indexSelector, LibraDexBinarySliceValueKind.TimeSpanTicks, offset, sizeof(long));

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as DateTimeOffset ticks plus offset ticks.<br/>
    /// The returned operator accepts DateTimeOffset operands so `.SlicedAsDateTime(...)` and `.SlicedAsDateTimeOffset(...)` stay type-consistent and caller intent remains visible at the fluent call site.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the 16-byte DateTimeOffset pair.</param>
    /// <returns>A typed binary slice operator over DateTimeOffset values.</returns>
    public LibraDexBinaryTypedSliceConditionOperator<DateTimeOffset> SlicedAsDateTimeOffset(int offset)
        => new LibraDexBinaryTypedSliceConditionOperator<DateTimeOffset>(builder, indexSelector, LibraDexBinarySliceValueKind.DateTimeOffsetPair, offset, 16);

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as UTF-8 text.<br/>
    /// The returned operator records text comparisons that execute over the selected key bytes without decoding the whole binary key.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the UTF-8 slice.</param>
    /// <param name="byteLength">The byte length of the UTF-8 slice.</param>
    /// <returns>A typed binary string slice operator.</returns>
    public LibraDexBinaryStringSliceConditionOperator SlicedAsUtf8String(int offset, int byteLength)
        => new LibraDexBinaryStringSliceConditionOperator(builder, indexSelector, LibraDexBinarySliceValueKind.Utf8String, offset, byteLength);

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as Latin1 text.<br/>
    /// The returned operator records ordinal text comparisons over the selected key bytes.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the Latin1 slice.</param>
    /// <param name="byteLength">The byte length of the Latin1 slice.</param>
    /// <returns>A typed binary string slice operator.</returns>
    public LibraDexBinaryStringSliceConditionOperator SlicedAsLatin1String(int offset, int byteLength)
        => new LibraDexBinaryStringSliceConditionOperator(builder, indexSelector, LibraDexBinarySliceValueKind.Latin1String, offset, byteLength);

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as UTF-16 text.<br/>
    /// The returned operator records ordinal text comparisons over the selected key bytes.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the UTF-16 slice.</param>
    /// <param name="byteLength">The byte length of the UTF-16 slice.</param>
    /// <returns>A typed binary string slice operator.</returns>
    public LibraDexBinaryStringSliceConditionOperator SlicedAsUtf16String(int offset, int byteLength)
        => new LibraDexBinaryStringSliceConditionOperator(builder, indexSelector, LibraDexBinarySliceValueKind.Utf16String, offset, byteLength);

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as UTF-32 text.<br/>
    /// The returned operator records ordinal text comparisons over the selected key bytes.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the UTF-32 slice.</param>
    /// <param name="byteLength">The byte length of the UTF-32 slice.</param>
    /// <returns>A typed binary string slice operator.</returns>
    public LibraDexBinaryStringSliceConditionOperator SlicedAsUtf32String(int offset, int byteLength)
        => new LibraDexBinaryStringSliceConditionOperator(builder, indexSelector, LibraDexBinarySliceValueKind.Utf32String, offset, byteLength);

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as ASCII text.<br/>
    /// The returned operator records ordinal text comparisons over the selected key bytes.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the ASCII slice.</param>
    /// <param name="byteLength">The byte length of the ASCII slice.</param>
    /// <returns>A typed binary string slice operator.</returns>
    public LibraDexBinaryStringSliceConditionOperator SlicedAsAsciiString(int offset, int byteLength)
        => new LibraDexBinaryStringSliceConditionOperator(builder, indexSelector, LibraDexBinarySliceValueKind.AsciiString, offset, byteLength);

    /// <summary>
    /// Captures a binary slice interpreted with a caller-supplied text encoding.<br/>
    /// LibraDex stores the supplied <see cref="Encoding"/> instance in the condition descriptor and uses it directly during .NET-native execution; no encoding inference or name reconstruction is performed.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the encoded text slice.</param>
    /// <param name="byteLength">The byte length of the encoded text slice.</param>
    /// <param name="encoding">The caller-supplied text encoding used to decode the slice.</param>
    /// <returns>A typed binary string slice operator.</returns>
    public LibraDexBinaryStringSliceConditionOperator SlicedAsEncodedString(int offset, int byteLength, Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        return new LibraDexBinaryStringSliceConditionOperator(builder, indexSelector, LibraDexBinarySliceValueKind.CustomEncodingString, offset, byteLength, encoding);
    }

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as one UTF-16 char.<br/>
    /// The returned operator records ordinal text comparisons over that one-character slice.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the UTF-16 char slice.</param>
    /// <returns>A typed binary string slice operator.</returns>
    public LibraDexBinaryStringSliceConditionOperator SlicedAsCharUtf16(int offset)
        => new LibraDexBinaryStringSliceConditionOperator(builder, indexSelector, LibraDexBinarySliceValueKind.CharUtf16, offset, sizeof(char));

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as one UTF-32 rune.<br/>
    /// The returned operator records ordinal text comparisons over that rune's string representation.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the UTF-32 rune slice.</param>
    /// <returns>A typed binary string slice operator.</returns>
    public LibraDexBinaryStringSliceConditionOperator SlicedAsRuneUtf32(int offset)
        => new LibraDexBinaryStringSliceConditionOperator(builder, indexSelector, LibraDexBinarySliceValueKind.RuneUtf32, offset, sizeof(int));

    private LibraDexConditionContinueOrEnd AddBinary(LibraDexConditionOperatorKind operatorKind, byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return builder.AddLeaf(new LibraDexConditionLeafDescriptor(
            indexSelector,
            LibraDexConditionValueKind.Binary,
            EffectiveOperator(operatorKind),
            new[] { LibraDexConditionOperand.Value((byte[])value.Clone()) },
            IgnoreCase: false,
            Culture: null));
    }

    private LibraDexConditionContinueOrEnd AddBinaryPattern(LibraDexConditionOperatorKind operatorKind, LibraDexBinaryPatternPredicate predicate)
    {
        return builder.AddLeaf(new LibraDexConditionLeafDescriptor(
            indexSelector,
            LibraDexConditionValueKind.Binary,
            EffectiveOperator(operatorKind),
            new[] { LibraDexConditionOperand.Value(predicate) },
            IgnoreCase: false,
            Culture: null));
    }
}

/// <summary>
/// Captures typed comparison operators for one binary slice selected from a fixed-width byte-array index key.<br/>
/// The slice metadata is kept with each descriptor so materialization can build an encoded-byte predicate instead of routing through a separate decoded projection.<br/>
/// </summary>
/// <typeparam name="TValue">The typed slice value accepted by this operator chain.</typeparam>
public sealed class LibraDexBinaryTypedSliceConditionOperator<TValue>
{
    private readonly LibraDexConditionBuilder builder;
    private readonly LibraDexConditionIndexSelector indexSelector;
    private readonly LibraDexBinarySliceValueKind valueKind;
    private readonly int offset;
    private readonly int length;

    internal LibraDexBinaryTypedSliceConditionOperator(
        LibraDexConditionBuilder builder,
        LibraDexConditionIndexSelector indexSelector,
        LibraDexBinarySliceValueKind valueKind,
        int offset,
        int length)
    {
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Binary slice offset cannot be negative.");
        }

        if (length <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "Binary slice length must be positive.");
        }

        this.builder = builder;
        this.indexSelector = indexSelector;
        this.valueKind = valueKind;
        this.offset = offset;
        this.length = length;
    }

    /// <summary>
    /// Captures equality against a typed binary slice value.<br/>
    /// </summary>
    /// <param name="value">The typed value to compare with the selected binary slice.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd EqualTo(TValue value)
        => Add(LibraDexConditionOperatorKind.BinaryTypedSliceEqualTo, value);

    /// <summary>
    /// Captures a greater-than comparison against a typed binary slice value.<br/>
    /// </summary>
    /// <param name="value">The exclusive lower boundary value.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd GreaterThan(TValue value)
        => Add(LibraDexConditionOperatorKind.BinaryTypedSliceGreaterThan, value);

    /// <summary>
    /// Captures a greater-than-or-equal comparison against a typed binary slice value.<br/>
    /// </summary>
    /// <param name="value">The inclusive lower boundary value.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd GreaterOrEqual(TValue value)
        => Add(LibraDexConditionOperatorKind.BinaryTypedSliceGreaterOrEqual, value);

    /// <summary>
    /// Captures a less-than comparison against a typed binary slice value.<br/>
    /// </summary>
    /// <param name="value">The exclusive upper boundary value.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd LessThan(TValue value)
        => Add(LibraDexConditionOperatorKind.BinaryTypedSliceLessThan, value);

    /// <summary>
    /// Captures a less-than-or-equal comparison against a typed binary slice value.<br/>
    /// </summary>
    /// <param name="value">The inclusive upper boundary value.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd LessOrEqual(TValue value)
        => Add(LibraDexConditionOperatorKind.BinaryTypedSliceLessOrEqual, value);

    /// <summary>
    /// Captures an inclusive two-boundary comparison against a typed binary slice value.<br/>
    /// </summary>
    /// <param name="startValue">The inclusive lower boundary value.</param>
    /// <param name="endValue">The inclusive upper boundary value.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd Between(TValue startValue, TValue endValue)
        => Add(LibraDexConditionOperatorKind.BinaryTypedSliceBetween, startValue, endValue);

    /// <summary>
    /// Captures a bitwise-AND equality predicate against an integral typed binary slice.<br/>
    /// The condition means `(sliceValue &amp; bitMask) == equalTo` and executes as an encoded-key byte-slice residual predicate.<br/>
    /// </summary>
    /// <param name="bitMask">The mask applied to each interpreted slice value.</param>
    /// <param name="equalTo">The expected masked value.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd BitAnd(TValue bitMask, TValue equalTo)
        => Add(LibraDexConditionOperatorKind.BinaryTypedSliceBitAndEqualTo, bitMask, equalTo);

    /// <summary>
    /// Captures a bitwise-AND zero predicate against an integral typed binary slice.<br/>
    /// The condition means `(sliceValue &amp; bitMask) == default(TValue)` and mirrors scalar bitmask grammar.<br/>
    /// </summary>
    /// <param name="bitMask">The mask applied to each interpreted slice value.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd BitAnd(TValue bitMask)
        => BitAnd(bitMask, default!);

    /// <summary>
    /// Captures a bitwise-AND inequality predicate against an integral typed binary slice.<br/>
    /// The condition means `(sliceValue &amp; bitMask) != notEqualTo` and is the primitive form behind any-bit-set checks.<br/>
    /// </summary>
    /// <param name="bitMask">The mask applied to each interpreted slice value.</param>
    /// <param name="notEqualTo">The masked value that must not match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd BitAndNotEqualTo(TValue bitMask, TValue notEqualTo)
        => Add(LibraDexConditionOperatorKind.BinaryTypedSliceBitAndNotEqualTo, bitMask, notEqualTo);

    /// <summary>
    /// Captures a predicate requiring every bit in <paramref name="bitMask"/> to be set on an integral typed binary slice.<br/>
    /// This is a readability wrapper for <see cref="BitAnd(TValue, TValue)"/> where the comparison value is the same mask.<br/>
    /// </summary>
    /// <param name="bitMask">The mask whose bits must all be present in each interpreted slice value.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd AllBitsSet(TValue bitMask)
        => BitAnd(bitMask, bitMask);

    /// <summary>
    /// Captures a predicate requiring at least one bit in <paramref name="bitMask"/> to be set on an integral typed binary slice.<br/>
    /// This is a readability wrapper for `(sliceValue &amp; bitMask) != default(TValue)`.<br/>
    /// </summary>
    /// <param name="bitMask">The mask whose bits are tested for overlap.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd AnyBitsSet(TValue bitMask)
        => BitAndNotEqualTo(bitMask, default!);

    /// <summary>
    /// Captures a predicate requiring no bits in <paramref name="bitMask"/> to be set on an integral typed binary slice.<br/>
    /// This is a readability wrapper for `(sliceValue &amp; bitMask) == default(TValue)`.<br/>
    /// </summary>
    /// <param name="bitMask">The mask whose bits must not overlap each interpreted slice value.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NoBitsSet(TValue bitMask)
        => BitAnd(bitMask, default!);

    private LibraDexConditionContinueOrEnd Add(
        LibraDexConditionOperatorKind operatorKind,
        TValue value,
        TValue? upperValue = default)
    {
        List<LibraDexConditionOperand> operands = new()
        {
            LibraDexConditionOperand.Value(valueKind),
            LibraDexConditionOperand.Value(offset),
            LibraDexConditionOperand.Value(length),
            LibraDexConditionOperand.Value(value!)
        };
        if (operatorKind is LibraDexConditionOperatorKind.BinaryTypedSliceBetween or
            LibraDexConditionOperatorKind.BinaryTypedSliceBitAndEqualTo or
            LibraDexConditionOperatorKind.BinaryTypedSliceBitAndNotEqualTo)
        {
            operands.Add(LibraDexConditionOperand.Value(upperValue!));
        }

        return builder.AddLeaf(new LibraDexConditionLeafDescriptor(
            indexSelector,
            LibraDexConditionValueKind.Binary,
            operatorKind,
            operands.ToArray(),
            IgnoreCase: false,
            Culture: null));
    }
}

/// <summary>
/// Captures text comparison operators for one UTF-8 binary slice selected from a fixed-width byte-array index key.<br/>
/// The current bridge uses ordinal string semantics to avoid introducing folded-text or culture projection behavior into raw binary slices.<br/>
/// </summary>
public sealed class LibraDexBinaryStringSliceConditionOperator
{
    private readonly LibraDexConditionBuilder builder;
    private readonly LibraDexConditionIndexSelector indexSelector;
    private readonly LibraDexBinarySliceValueKind valueKind;
    private readonly int offset;
    private readonly int length;
    private readonly Encoding? encoding;

    internal LibraDexBinaryStringSliceConditionOperator(
        LibraDexConditionBuilder builder,
        LibraDexConditionIndexSelector indexSelector,
        LibraDexBinarySliceValueKind valueKind,
        int offset,
        int length,
        Encoding? encoding = null)
    {
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Binary string slice offset cannot be negative.");
        }

        if (length <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "Binary string slice length must be positive.");
        }

        this.builder = builder;
        this.indexSelector = indexSelector;
        this.valueKind = valueKind;
        this.offset = offset;
        this.length = length;
        this.encoding = encoding;
    }

    /// <summary>
    /// Captures ordinal equality against a UTF-8 binary slice.<br/>
    /// </summary>
    /// <param name="value">The string value to compare with the selected slice.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd EqualTo(string value)
        => Add(LibraDexConditionOperatorKind.BinaryTypedSliceEqualTo, value);

    /// <summary>
    /// Captures ordinal starts-with comparison against a UTF-8 binary slice.<br/>
    /// </summary>
    /// <param name="value">The string prefix to compare with the selected slice.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd StartsWith(string value)
        => Add(LibraDexConditionOperatorKind.BinaryTypedSliceStartsWith, value);

    /// <summary>
    /// Captures ordinal contains comparison against a UTF-8 binary slice.<br/>
    /// </summary>
    /// <param name="value">The string fragment to compare with the selected slice.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd Contains(string value)
        => Add(LibraDexConditionOperatorKind.BinaryTypedSliceContains, value);

    private LibraDexConditionContinueOrEnd Add(LibraDexConditionOperatorKind operatorKind, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return builder.AddLeaf(new LibraDexConditionLeafDescriptor(
            indexSelector,
            LibraDexConditionValueKind.Binary,
            operatorKind,
            CreateOperands(value),
            IgnoreCase: false,
            Culture: null));
    }

    private LibraDexConditionOperand[] CreateOperands(string value)
    {
        if (valueKind == LibraDexBinarySliceValueKind.CustomEncodingString)
        {
            return new[]
            {
                LibraDexConditionOperand.Value(valueKind),
                LibraDexConditionOperand.Value(offset),
                LibraDexConditionOperand.Value(length),
                LibraDexConditionOperand.Value(value),
                LibraDexConditionOperand.Value(encoding!)
            };
        }

        return new[]
        {
            LibraDexConditionOperand.Value(valueKind),
            LibraDexConditionOperand.Value(offset),
            LibraDexConditionOperand.Value(length),
            LibraDexConditionOperand.Value(value)
        };
    }
}

/// <summary>
/// Captures GUID-specific adopted condition operators for one selected LibraDex index.<br/>
/// Text-oriented GUID methods compile to encoded canonical-nibble predicates; exact GUID methods use the ordinary exact-key bridge.<br/>
/// </summary>
public sealed class LibraDexGuidConditionOperator : LibraDexConditionOperator<Guid>
{
    private readonly LibraDexConditionBuilder builder;
    private readonly LibraDexConditionIndexSelector indexSelector;

    internal LibraDexGuidConditionOperator(LibraDexConditionBuilder builder, LibraDexConditionIndexSelector indexSelector, bool negate = false)
        : base(builder, indexSelector, LibraDexConditionValueKind.Guid, negate)
    {
        this.builder = builder;
        this.indexSelector = indexSelector;
    }

    /// <summary>
    /// Negates the next GUID predicate over the selected index.<br/>
    /// Exact equality and inequality map to inverse GUID descriptors; unsupported GUID pattern negation fails when the predicate is captured.<br/>
    /// </summary>
    public new LibraDexGuidConditionOperator Not => new(builder, indexSelector, !IsNegated);

    /// <summary>
    /// Captures a GUID text or segment starts-with condition.<br/>
    /// The descriptor materializes as an encoded canonical-nibble predicate over stored GUID bytes.<br/>
    /// </summary>
    /// <param name="value">The GUID text or segment prefix.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd StartsWith(string value)
        => AddGuidPattern(LibraDexConditionOperatorKind.StartsWith, LibraDexGuidPatternPredicate.Create(value, LibraDexGuidPatternMode.StartsWith));

    /// <summary>
    /// Captures a GUID byte-domain starts-with condition.<br/>
    /// The byte order is the stored GUID byte order produced by `Guid.TryWriteBytes`, and the descriptor stores a compiled nibble predicate rather than text.<br/>
    /// </summary>
    /// <param name="value">The stored GUID byte prefix.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd StartsWith(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return AddGuidPattern(LibraDexConditionOperatorKind.StartsWith, LibraDexGuidPatternPredicate.Create(value, LibraDexGuidPatternMode.StartsWith));
    }

    /// <summary>
    /// Captures exact GUID equality from text input.<br/>
    /// The text is parsed once at descriptor creation so execution can use the ordinary exact-key primitive.<br/>
    /// </summary>
    /// <param name="value">The GUID text to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd EqualTo(string value)
        => EqualTo(Guid.Parse(value));

    /// <summary>
    /// Captures exact GUID inequality from text input.<br/>
    /// The text is parsed once at descriptor creation so execution can use the ordered exact-key exclusion bridge.<br/>
    /// </summary>
    /// <param name="value">The GUID text to exclude.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotEqualTo(string value)
        => NotEqualTo(Guid.Parse(value));

    /// <summary>
    /// Captures a GUID text ends-with condition.<br/>
    /// </summary>
    /// <param name="value">The GUID text suffix.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd EndsWith(string value)
        => AddGuidPattern(LibraDexConditionOperatorKind.EndsWith, LibraDexGuidPatternPredicate.Create(value, LibraDexGuidPatternMode.EndsWith));

    /// <summary>
    /// Captures a GUID byte-domain ends-with condition.<br/>
    /// The byte order is the stored GUID byte order produced by `Guid.TryWriteBytes`, and the descriptor stores a compiled nibble predicate rather than text.<br/>
    /// </summary>
    /// <param name="value">The stored GUID byte suffix.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd EndsWith(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return AddGuidPattern(LibraDexConditionOperatorKind.EndsWith, LibraDexGuidPatternPredicate.Create(value, LibraDexGuidPatternMode.EndsWith));
    }

    /// <summary>
    /// Captures a GUID text contains condition.<br/>
    /// </summary>
    /// <param name="value">The contained GUID text fragment.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd Contains(string value)
        => AddGuidPattern(LibraDexConditionOperatorKind.Contains, LibraDexGuidPatternPredicate.Create(value, LibraDexGuidPatternMode.Contains));

    /// <summary>
    /// Captures a GUID byte-domain contains condition.<br/>
    /// The byte order is the stored GUID byte order produced by `Guid.TryWriteBytes`, and the descriptor stores a compiled nibble predicate rather than text.<br/>
    /// </summary>
    /// <param name="value">The stored GUID byte segment.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd Contains(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return AddGuidPattern(LibraDexConditionOperatorKind.Contains, LibraDexGuidPatternPredicate.Create(value, LibraDexGuidPatternMode.Contains));
    }

    /// <summary>
    /// Captures a GUID text pattern condition.<br/>
    /// </summary>
    /// <param name="pattern">The GUID text pattern descriptor.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd MatchesPattern(string pattern)
        => AddGuidPattern(LibraDexConditionOperatorKind.MatchesPattern, LibraDexGuidPatternPredicate.Create(pattern, LibraDexGuidPatternMode.MatchesPattern));

    /// <summary>
    /// Captures a GUID text pattern condition using the short public spelling.<br/>
    /// This is the preferred alias for <see cref="MatchesPattern(string)"/> and keeps wildcard GUID syntax concise at call sites.<br/>
    /// </summary>
    /// <param name="pattern">The GUID text pattern descriptor.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd Matches(string pattern)
        => MatchesPattern(pattern);

    /// <summary>
    /// Captures a full 16-byte GUID byte-domain pattern condition.<br/>
    /// The byte order is the stored GUID byte order produced by `Guid.TryWriteBytes`, and every nibble is compared.<br/>
    /// </summary>
    /// <param name="pattern">The full stored GUID byte pattern.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd MatchesPattern(byte[] pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        return AddGuidPattern(LibraDexConditionOperatorKind.MatchesPattern, LibraDexGuidPatternPredicate.Create(pattern, LibraDexGuidPatternMode.MatchesPattern));
    }

    /// <summary>
    /// Captures a full 16-byte GUID byte-domain pattern condition using the short public spelling.<br/>
    /// This is the preferred alias for <see cref="MatchesPattern(byte[])"/> and preserves the stored GUID byte-order contract.<br/>
    /// </summary>
    /// <param name="pattern">The full stored GUID byte pattern.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd Matches(byte[] pattern)
        => MatchesPattern(pattern);

    private LibraDexConditionContinueOrEnd AddGuidPattern(LibraDexConditionOperatorKind operatorKind, LibraDexGuidPatternPredicate predicate)
    {
        return builder.AddLeaf(new LibraDexConditionLeafDescriptor(
            indexSelector,
            LibraDexConditionValueKind.Guid,
            EffectiveOperator(operatorKind),
            new[] { LibraDexConditionOperand.Value(predicate) },
            IgnoreCase: false,
            Culture: null));
    }
}

/// <summary>
/// Continues or ends an adopted condition after one leaf or group has been captured.<br/>
/// The uppercase `AND` and `OR` members intentionally preserve Abraxas grammar shape for low-friction copy-then-modify adoption.<br/>
/// </summary>
public sealed class LibraDexConditionContinueOrEnd
{
    private readonly LibraDexConditionBuilder builder;

    internal LibraDexConditionContinueOrEnd(LibraDexConditionBuilder builder)
    {
        this.builder = builder;
    }

    /// <summary>
    /// Adds an intersection operator and starts the next clause.<br/>
    /// </summary>
    public LibraDexConditionClause AND
    {
        get
        {
            builder.SetNextOperation(LibraDexConditionNodeKind.And);
            return new LibraDexConditionClause(builder);
        }
    }

    /// <summary>
    /// Adds an intersection operator and starts the next clause using the canonical Pascal-case spelling.<br/>
    /// This aliases <see cref="AND"/> so condition groups can read naturally as `.And.Group(...)` and `.And.Not.Group(...)` while preserving the older Abraxas-style uppercase member.<br/>
    /// </summary>
    public LibraDexConditionClause And => AND;

    /// <summary>
    /// Adds an identity-set intersection and selects the next index by name.<br/>
    /// Key typing and projection intent are chosen after this selector through members such as `.AsString`, `.AsGuid`, and `.AsInt64`, preserving the index-first condition grammar across multi-index chains.<br/>
    /// The index name is resolved only when the completed condition is materialized.<br/>
    /// </summary>
    /// <param name="indexName">The next index name inside the current identity group.</param>
    /// <returns>A value-family selector for the next condition leaf.</returns>
    public LibraDexConditionValueTypeSelector AndAlso(string indexName)
        => AND.Where(indexName);

    /// <summary>
    /// Adds an identity-set intersection and selects the next index from an opened index instance.<br/>
    /// The handle supplies the next index name and verifies the identity group immediately; key-type compatibility remains tied to the `.As...` family selected after the index.<br/>
    /// This overload keeps instance-based multi-index conditions concise without repeating index names.<br/>
    /// </summary>
    /// <param name="index">The opened index instance to select for the next condition leaf.</param>
    /// <returns>A value-family selector for the next condition leaf.</returns>
    public LibraDexConditionValueTypeSelector AndAlso(IIndex index)
        => AND.Where(ValidateIndex(index).Name);

    /// <summary>
    /// Adds an identity-set intersection from a generic typed index instance and selects the index key type automatically.<br/>
    /// This supports cross-index chains such as `.Where(age).GreaterOrEqual(18).AndAlso(status).EqualTo(1)` without repeating `.AsInt32` when the index handle already carries the key type.<br/>
    /// </summary>
    /// <typeparam name="TKey">The key type carried by the opened index handle.</typeparam>
    /// <typeparam name="TIdentity">The identity type carried by the opened index handle.</typeparam>
    /// <param name="index">The opened generic index instance to select for the next condition leaf.</param>
    /// <returns>A typed operator for the selected index key type.</returns>
    public LibraDexConditionOperator<TKey> AndAlso<TKey, TIdentity>(LibraDexIndex<TKey, TIdentity> index)
        => AndAlso((IIndex)index).As<TKey>();

    /// <summary>
    /// Adds an identity-set intersection from a string index facade and selects string operators automatically.<br/>
    /// This keeps `.AndAlso(name).StartsWith("A")` available when the continuation receives an opened string index handle.<br/>
    /// </summary>
    /// <param name="index">The opened string index instance to select for the next condition leaf.</param>
    /// <returns>String operators for the selected index.</returns>
    public LibraDexStringConditionOperator AndAlso(LibraDexStringScalar8Index index)
        => AndAlso((IIndex)index).AsString;

    /// <summary>
    /// Adds a union operator and starts the next clause.<br/>
    /// </summary>
    public LibraDexConditionClause OR
    {
        get
        {
            builder.SetNextOperation(LibraDexConditionNodeKind.Or);
            return new LibraDexConditionClause(builder);
        }
    }

    /// <summary>
    /// Adds a union operator and starts the next clause using the canonical Pascal-case spelling.<br/>
    /// This aliases <see cref="OR"/> so condition groups can read naturally as `.Or.Group(...)` and `.Or.Not.Group(...)` while preserving the older Abraxas-style uppercase member.<br/>
    /// </summary>
    public LibraDexConditionClause Or => OR;

    /// <summary>
    /// Adds an identity-set union and selects the next index by name.<br/>
    /// Key typing and projection intent are chosen after this selector through members such as `.AsString`, `.AsGuid`, and `.AsInt64`, matching the index-first grammar used by `AndAlso`.<br/>
    /// The index name is resolved only when the completed condition is materialized.<br/>
    /// </summary>
    /// <param name="indexName">The next index name inside the current identity group.</param>
    /// <returns>A value-family selector for the next condition leaf.</returns>
    public LibraDexConditionValueTypeSelector OrElse(string indexName)
        => OR.Where(indexName);

    /// <summary>
    /// Adds an identity-set union and selects the next index from an opened index instance.<br/>
    /// The handle supplies the next index name and verifies the identity group immediately; key-type compatibility remains tied to the `.As...` family selected after the index.<br/>
    /// This overload mirrors `AndAlso(IIndex)` for union-shaped multi-index conditions.<br/>
    /// </summary>
    /// <param name="index">The opened index instance to select for the next condition leaf.</param>
    /// <returns>A value-family selector for the next condition leaf.</returns>
    public LibraDexConditionValueTypeSelector OrElse(IIndex index)
        => OR.Where(ValidateIndex(index).Name);

    /// <summary>
    /// Adds an identity-set union from a generic typed index instance and selects the index key type automatically.<br/>
    /// This supports cross-index chains such as `.Where(priority).GreaterOrEqual(3).OrElse(status).EqualTo(1)` without repeating `.AsInt32` when the index handle already carries the key type.<br/>
    /// </summary>
    /// <typeparam name="TKey">The key type carried by the opened index handle.</typeparam>
    /// <typeparam name="TIdentity">The identity type carried by the opened index handle.</typeparam>
    /// <param name="index">The opened generic index instance to select for the next condition leaf.</param>
    /// <returns>A typed operator for the selected index key type.</returns>
    public LibraDexConditionOperator<TKey> OrElse<TKey, TIdentity>(LibraDexIndex<TKey, TIdentity> index)
        => OrElse((IIndex)index).As<TKey>();

    /// <summary>
    /// Adds an identity-set union from a string index facade and selects string operators automatically.<br/>
    /// This keeps `.OrElse(name).StartsWith("A")` available when the continuation receives an opened string index handle.<br/>
    /// </summary>
    /// <param name="index">The opened string index instance to select for the next condition leaf.</param>
    /// <returns>String operators for the selected index.</returns>
    public LibraDexStringConditionOperator OrElse(LibraDexStringScalar8Index index)
        => OrElse((IIndex)index).AsString;

    /// <summary>
    /// Completes the adopted condition descriptor.<br/>
    /// The returned condition can be inspected as leaves or materialized by resolving index names to opened LibraDex indexes.<br/>
    /// </summary>
    public LibraDexConditionEndCondition EndCondition => builder.End();

    /// <summary>
    /// Convenience alias for <see cref="EndCondition"/> that matches Abraxas' short `ec` alias.<br/>
    /// </summary>
    public LibraDexConditionEndCondition ec => EndCondition;

    private IIndex ValidateIndex(IIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (!string.Equals(index.Group, builder.Group, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The supplied index handle belongs to a different LibraDex identity group.");
        }

        return index;
    }
}
