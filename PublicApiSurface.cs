namespace LibraDex;

/// <summary>
/// Selects the traversal direction requested by a public query.<br/>
/// Direction changes how matching index entries are returned; it should not imply a runtime sort when the index can be traversed in the requested direction.<br/>
/// </summary>
public enum QueryDirection
{
    /// <summary>
    /// Returns matching entries from the lowest ordered key position to the highest ordered key position.<br/>
    /// This is the default because LibraDex physical indexes are ordered ascending unless an index profile deliberately says otherwise.<br/>
    /// </summary>
    Ascending = 0,

    /// <summary>
    /// Returns matching entries from the highest ordered key position to the lowest ordered key position.<br/>
    /// Implementations should prefer reverse traversal over materializing and sorting a result set.<br/>
    /// </summary>
    Descending = 1
}

/// <summary>
/// Selects how a centered positional retrieval anchors an even or duplicate-center window.<br/>
/// Bias controls which real indexed entry is selected as the center anchor; query direction still controls final return order.<br/>
/// </summary>
public enum MiddleBias
{
    /// <summary>
    /// Anchors an ambiguous middle window toward the earlier ordered entry.<br/>
    /// This is the default because it is deterministic and aligns with normal ascending traversal.<br/>
    /// </summary>
    LeftBiased = 0,

    /// <summary>
    /// Anchors an ambiguous middle window toward the later ordered entry.<br/>
    /// This is useful when callers want the right side of an even center to be favored while still receiving results in normal query order.<br/>
    /// </summary>
    RightBiased = 1
}

/// <summary>
/// Selects aggregate cardinality semantics for duplicate-key indexes.<br/>
/// Aggregates operate over keys, but duplicate-key indexes can count or summarize matching tuples or de-duplicated keys depending on caller intent.<br/>
/// </summary>
public enum AggregateScope
{
    /// <summary>
    /// Aggregates matching key/identity tuples.<br/>
    /// For duplicate-key indexes, each identity matched by a key contributes to tuple cardinality.<br/>
    /// </summary>
    Tuples = 0,

    /// <summary>
    /// Aggregates unique keys only.<br/>
    /// For duplicate-key indexes, multiple identities under the same key contribute one distinct key to the aggregate.<br/>
    /// </summary>
    DistinctKeys = 1
}

/// <summary>
/// Selects retrieval cardinality semantics for duplicate-key indexes.<br/>
/// Retrieval needs a separate enum from aggregate scope because returning one identity for a distinct key requires an explicit representative rule.<br/>
/// </summary>
public enum RetrievalScope
{
    /// <summary>
    /// Returns matching key/identity tuples without de-duplicating keys.<br/>
    /// This is the default because it preserves the physical identity-index semantics.<br/>
    /// </summary>
    Tuples = 0,

    /// <summary>
    /// Returns one representative row per distinct key, using the first identity in the duplicate-key run.<br/>
    /// This avoids ambiguous identity selection when callers want de-duplicated keys and still request tuple or identity shapes.<br/>
    /// </summary>
    DistinctKeysFirstIdentity = 1,

    /// <summary>
    /// Returns one representative row per distinct key, using the last identity in the duplicate-key run.<br/>
    /// This is useful when the caller wants a stable right-side representative from each duplicate-key group.<br/>
    /// </summary>
    DistinctKeysLastIdentity = 2
}

/// <summary>
/// Selects the ordering contract for identity-only results from multi-criteria composition.<br/>
/// A composed criteria tree may not have one coherent key order because each leaf can come from a different index; this enum makes the caller's identity-ordering expectation explicit.<br/>
/// </summary>
public enum IdentityResultOrdering
{
    /// <summary>
    /// Preserves the natural executor order for the selected plan.<br/>
    /// Physical execution should document the route through diagnostics when this is used.<br/>
    /// </summary>
    PlanNatural = 0,

    /// <summary>
    /// Returns identities in ascending identity order.<br/>
    /// This may require merge or sort work unless the selected plan already produces that order.<br/>
    /// </summary>
    IdentityAscending = 1,

    /// <summary>
    /// Returns identities in descending identity order.<br/>
    /// This may require reverse traversal, merge, or sort work depending on the selected plan.<br/>
    /// </summary>
    IdentityDescending = 2
}

/// <summary>
/// Selects duplicate identity handling for composed identity criteria.<br/>
/// Duplicate identities can occur with `Or`, duplicate-key indexes, or caller-provided overlapping criteria; the caller should choose whether duplicates are meaningful.<br/>
/// </summary>
public enum IdentityDeduplication
{
    /// <summary>
    /// Preserves duplicates produced by the plan.<br/>
    /// This is useful for diagnostics and for callers that want tuple-like multiplicity from the underlying criteria.<br/>
    /// </summary>
    Preserve = 0,

    /// <summary>
    /// Returns each identity only once.<br/>
    /// This is the default for object lookup semantics because an identity represents one target object even when multiple criteria match it.<br/>
    /// </summary>
    Distinct = 1
}

/// <summary>
/// Identifies the broad physical strategy selected for a programmatic identity criterion projection.<br/>
/// This is a planning descriptor, not a full explain-plan contract; it lets adapters decide whether a criteria shape is stream-friendly before execution is connected.<br/>
/// </summary>
public enum LibraDexIdentityPlanKind
{
    /// <summary>
    /// The plan contains one leaf criterion and can execute through that index's normal lookup path.<br/>
    /// </summary>
    SingleIndex = 0,

    /// <summary>
    /// The plan is an identity-set intersection across criteria in one identity group.<br/>
    /// </summary>
    Intersection = 1,

    /// <summary>
    /// The plan is an identity-set union across criteria in one identity group.<br/>
    /// </summary>
    Union = 2,

    /// <summary>
    /// The plan subtracts right-side identities from left-side identities.<br/>
    /// </summary>
    Difference = 3,

    /// <summary>
    /// The plan computes a complement against the identity group universe.<br/>
    /// </summary>
    Complement = 4,

    /// <summary>
    /// The plan contains mixed boolean operators and should be executed by its child plan tree.<br/>
    /// </summary>
    Composite = 5
}

/// <summary>
/// Identifies whether an identity criteria plan can be streamed directly or needs intermediate identity state.<br/>
/// </summary>
public enum LibraDexIdentityPlanMaterialization
{
    /// <summary>
    /// The plan can be executed as a single index stream.<br/>
    /// </summary>
    None = 0,

    /// <summary>
    /// The plan should be executable by merging compatible ordered streams.<br/>
    /// </summary>
    StreamingMerge = 1,

    /// <summary>
    /// The plan needs a temporary identity set for de-duplication, ordering, complement, or incompatible child streams.<br/>
    /// </summary>
    IdentitySet = 2
}

/// <summary>
/// Describes the broad execution route used by a query or reader.<br/>
/// The enum is intentionally coarse; detailed counters belong on diagnostics snapshots rather than the main result shape.<br/>
/// </summary>
public enum LibraDexExecutionKind
{
    /// <summary>
    /// The execution route has not been recorded or is not known to the public scaffold yet.<br/>
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// The query is expected to use a direct ordered-index route or exact key-run route.<br/>
    /// </summary>
    FastPath = 1,

    /// <summary>
    /// The query is expected to use a maintained projection or sub-index rather than the primary physical key order.<br/>
    /// </summary>
    Projection = 2,

    /// <summary>
    /// The query is expected to scan index entries because no aligned projection is available.<br/>
    /// </summary>
    Scan = 3,

    /// <summary>
    /// The query is expected to walk an ordered route in reverse without sorting materialized results.<br/>
    /// </summary>
    ReverseTraversal = 4,

    /// <summary>
    /// The query would require materialized sorting unless a future physical profile makes the requested order native.<br/>
    /// </summary>
    SortBacked = 5
}

/// <summary>
/// Identifies the public retrieval criterion represented by a query descriptor.<br/>
/// This lets diagnostics and scaffolding describe caller intent even before every criterion has a connected physical reader.<br/>
/// </summary>
public enum LibraDexCriteriaKind
{
    /// <summary>
    /// The descriptor represents all entries in the index.<br/>
    /// </summary>
    All = 0,

    /// <summary>
    /// The descriptor represents exact-key lookup.<br/>
    /// </summary>
    Find = 1,

    /// <summary>
    /// The descriptor represents inclusive range lookup.<br/>
    /// </summary>
    Between = 2,

    /// <summary>
    /// The descriptor represents a key less-than lookup.<br/>
    /// </summary>
    Before = 3,

    /// <summary>
    /// The descriptor represents a key less-than-or-equal lookup.<br/>
    /// </summary>
    AtOrBefore = 4,

    /// <summary>
    /// The descriptor represents a key greater-than lookup.<br/>
    /// </summary>
    After = 5,

    /// <summary>
    /// The descriptor represents a key greater-than-or-equal lookup.<br/>
    /// </summary>
    AtOrAfter = 6,

    /// <summary>
    /// The descriptor represents prefix lookup.<br/>
    /// Prefix can be fast-path when the selected projection is byte-direction aligned with the index order.<br/>
    /// </summary>
    Prefix = 7,

    /// <summary>
    /// The descriptor represents suffix lookup.<br/>
    /// Suffix can be fast-path when a reversed projection is maintained and aligned with the index order.<br/>
    /// </summary>
    Suffix = 8,

    /// <summary>
    /// The descriptor represents contains lookup.<br/>
    /// Contains is normally scan-backed unless a contains-capable projection is maintained.<br/>
    /// </summary>
    Contains = 9,

    /// <summary>
    /// The descriptor represents pattern lookup.<br/>
    /// Pattern matching is normally scan-backed unless a pattern-capable projection is maintained.<br/>
    /// </summary>
    Matches = 10,

    /// <summary>
    /// The descriptor represents membership lookup over ordinary enumerable values.<br/>
    /// </summary>
    In = 11,

    /// <summary>
    /// The descriptor represents membership lookup over a prepared set.<br/>
    /// </summary>
    InSet = 12
}

/// <summary>
/// Identifies a set operation over two LibraDex query streams.<br/>
/// Set operations are intended to compose ordered query results without forcing callers to materialize and reshape data themselves.<br/>
/// </summary>
public enum LibraDexSetOperationKind
{
    /// <summary>
    /// Produces entries present in either query stream.<br/>
    /// Implementations should prefer ordered merge over chained materialization when both streams expose compatible order.<br/>
    /// </summary>
    Union = 0,

    /// <summary>
    /// Produces entries present in both query streams.<br/>
    /// Implementations should prefer streaming intersection when both streams are ordered by a compatible comparison axis.<br/>
    /// </summary>
    Intersect = 1,

    /// <summary>
    /// Produces entries present in the left query stream and not present in the right query stream.<br/>
    /// Implementations should prefer streaming exclusion over row-by-row post-filtering when possible.<br/>
    /// </summary>
    Except = 2
}

/// <summary>
/// Identifies a programmatic identity-criteria node.<br/>
/// Criteria nodes are descriptors that higher-level systems can build from parsed conditions without depending on fluent C# call-chain order.<br/>
/// </summary>
public enum LibraDexIdentityCriterionNodeKind
{
    /// <summary>
    /// The node is one index-backed lookup criterion such as `Find`, `Between`, `Prefix`, or `In`.<br/>
    /// </summary>
    Leaf = 0,

    /// <summary>
    /// The node negates one child criterion.<br/>
    /// Physical execution should prefer ordered complement planning where possible rather than row-by-row filtering.<br/>
    /// </summary>
    Not = 1,

    /// <summary>
    /// The node intersects two child criteria over the same identity group.<br/>
    /// </summary>
    And = 2,

    /// <summary>
    /// The node unions two child criteria over the same identity group.<br/>
    /// </summary>
    Or = 3,

    /// <summary>
    /// The node returns identities present in the left child and not present in the right child.<br/>
    /// </summary>
    Except = 4
}

/// <summary>
/// Identifies an index-native join shape over LibraDex query streams.<br/>
/// These are stream/composition joins, not SQL table joins with large hidden intermediate rowsets.<br/>
/// </summary>
public enum LibraDexJoinKind
{
    /// <summary>
    /// Joins two ordered streams by merging compatible keys or identities.<br/>
    /// </summary>
    Merge = 0,

    /// <summary>
    /// Returns left-side entries that have a matching right-side entry.<br/>
    /// This is the stream-native equivalent of a semi-join.<br/>
    /// </summary>
    Semi = 1,

    /// <summary>
    /// Returns left-side entries that do not have a matching right-side entry.<br/>
    /// This is the stream-native equivalent of an anti-join.<br/>
    /// </summary>
    Anti = 2,

    /// <summary>
    /// Uses each left-side entry to perform a right-side lookup.<br/>
    /// This can be efficient when the right side has direct lookup support and the left stream is small or already filtered.<br/>
    /// </summary>
    Lookup = 3
}

/// <summary>
/// Represents one public key/identity tuple returned by a LibraDex index.<br/>
/// The left side is the indexed key and the right side is the identity associated with that key.<br/>
/// </summary>
/// <typeparam name="TKey">The public key type.</typeparam>
/// <typeparam name="TIdentity">The public identity type.</typeparam>
public readonly record struct LibraDexTuple<TKey, TIdentity>(TKey Key, TIdentity Identity);

/// <summary>
/// Represents a strict non-generic public handle over an opened LibraDex index.<br/>
/// This surface is for generated and programmatic callers that cannot comfortably carry `TKey` and `TIdentity` through every layer, while still preserving runtime type validation and metadata-driven behavior.<br/>
/// </summary>
public interface IIndex
{
    /// <summary>
    /// Gets the index name recorded in catalog metadata or the fixed directory slot.<br/>
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the identity group name recorded in rich catalog metadata, or an empty string for older ungrouped scaffold entries.<br/>
    /// </summary>
    string Group { get; }

    /// <summary>
    /// Gets the CLR key type required by runtime key values supplied through this non-generic handle.<br/>
    /// </summary>
    Type KeyType { get; }

    /// <summary>
    /// Gets the CLR identity type returned by this non-generic handle's index.<br/>
    /// </summary>
    Type IdentityType { get; }

    /// <summary>
    /// Gets the index-wide duplicate-key contract persisted for this index.<br/>
    /// </summary>
    IndexKeys KeyContract { get; }

    /// <summary>
    /// Gets the logical key family recorded for this index.<br/>
    /// </summary>
    CatalogIndexKeyFamily KeyFamily { get; }

    /// <summary>
    /// Gets the logical identity family recorded for this index.<br/>
    /// </summary>
    CatalogIndexIdentityFamily IdentityFamily { get; }

    /// <summary>
    /// Gets the logical shape descriptor used to create this runtime index, when the index was opened through a shape-aware path.<br/>
    /// New catalog metadata can rehydrate projection and composite descriptors after reopen; older metadata returns null when no logical shape was recorded.<br/>
    /// </summary>
    LibraDexIndexShapeSpec? LogicalShape { get; }

    /// <summary>
    /// Gets a programmatic criteria builder for this index.<br/>
    /// The builder creates immutable identity-criteria descriptors that can be composed with criteria from other indexes in the same identity group.<br/>
    /// </summary>
    IIndexCriteriaBuilder Criteria { get; }

    /// <summary>
    /// Inserts one runtime key and runtime identity after validating both values against the persisted CLR type contract for this index.<br/>
    /// This is the strict programmatic mutation path for generated callers that opened an index through metadata rather than generic type arguments.<br/>
    /// </summary>
    /// <param name="key">The runtime key value to insert.</param>
    /// <param name="identity">The runtime identity value to associate with the key.</param>
    /// <returns>The insert result plus any route-create and insert commit telemetry.</returns>
    LibraDexGenericInsertResult Insert(object key, object identity);

    /// <summary>
    /// Captures an exact-key query after validating the runtime key value against <see cref="KeyType"/>.<br/>
    /// </summary>
    /// <param name="key">The key value to find.</param>
    /// <returns>A non-generic query descriptor over the opened index.</returns>
    IIndexQuery Find(object key);

    /// <summary>
    /// Captures an inclusive key-range query after validating both runtime key values against <see cref="KeyType"/>.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower key.</param>
    /// <param name="upperKey">The inclusive upper key.</param>
    /// <returns>A non-generic query descriptor over the opened index.</returns>
    IIndexQuery Between(object lowerKey, object upperKey);

    /// <summary>
    /// Captures an all-tuples query over the opened index.<br/>
    /// This is the programmatic counterpart to typed `All` and is useful when generated callers need a full ordered pass without synthesizing artificial key bounds.<br/>
    /// </summary>
    /// <returns>A non-generic query descriptor over the opened index.</returns>
    IIndexQuery All();

    /// <summary>
    /// Captures an exclusive upper-bound query after validating the runtime key value against <see cref="KeyType"/>.<br/>
    /// </summary>
    /// <param name="key">The exclusive upper key.</param>
    /// <returns>A non-generic query descriptor over the opened index.</returns>
    IIndexQuery Before(object key);

    /// <summary>
    /// Captures an inclusive upper-bound query after validating the runtime key value against <see cref="KeyType"/>.<br/>
    /// </summary>
    /// <param name="key">The inclusive upper key.</param>
    /// <returns>A non-generic query descriptor over the opened index.</returns>
    IIndexQuery AtOrBefore(object key);

    /// <summary>
    /// Captures an exclusive lower-bound query after validating the runtime key value against <see cref="KeyType"/>.<br/>
    /// </summary>
    /// <param name="key">The exclusive lower key.</param>
    /// <returns>A non-generic query descriptor over the opened index.</returns>
    IIndexQuery After(object key);

    /// <summary>
    /// Captures an inclusive lower-bound query after validating the runtime key value against <see cref="KeyType"/>.<br/>
    /// </summary>
    /// <param name="key">The inclusive lower key.</param>
    /// <returns>A non-generic query descriptor over the opened index.</returns>
    IIndexQuery AtOrAfter(object key);

    /// <summary>
    /// Captures a prefix query after validating the runtime key value against <see cref="KeyType"/>.<br/>
    /// Physical execution may be fast-path, projection-backed, or scan-backed depending on the index profile.<br/>
    /// </summary>
    /// <param name="prefix">The prefix value to match.</param>
    /// <returns>A non-generic query descriptor over the opened index.</returns>
    IIndexQuery Prefix(object prefix);

    /// <summary>
    /// Captures a suffix query after validating the runtime key value against <see cref="KeyType"/>.<br/>
    /// Suffix can be projection-backed when a reversed key projection exists, or scan-backed otherwise.<br/>
    /// </summary>
    /// <param name="suffix">The suffix value to match.</param>
    /// <returns>A non-generic query descriptor over the opened index.</returns>
    IIndexQuery Suffix(object suffix);

    /// <summary>
    /// Captures a contains query after validating the runtime key value against <see cref="KeyType"/>.<br/>
    /// Contains is expected to be scan-backed unless a later maintained projection explicitly supports it.<br/>
    /// </summary>
    /// <param name="value">The contained value to match.</param>
    /// <returns>A non-generic query descriptor over the opened index.</returns>
    IIndexQuery Contains(object value);

    /// <summary>
    /// Captures a membership query after validating each supplied runtime key value against <see cref="KeyType"/>.<br/>
    /// This is the non-generic convenience path; repeated membership work should eventually use a prepared-set handle once that programmatic shape is connected.<br/>
    /// </summary>
    /// <param name="keys">The runtime key values to match.</param>
    /// <returns>A non-generic query descriptor over the opened index.</returns>
    IIndexQuery In(IEnumerable<object> keys);

    /// <summary>
    /// Captures a prepared-set membership query after validating each supplied runtime key value against <see cref="KeyType"/>.<br/>
    /// This preserves InSet intent for generated callers even before a reusable non-generic prepared-set handle is added.<br/>
    /// </summary>
    /// <param name="keys">The runtime key values to prepare and match.</param>
    /// <returns>A non-generic query descriptor over the opened index.</returns>
    IIndexQuery InSet(IEnumerable<object> keys);

    /// <summary>
    /// Captures a prepared-set membership query after validating that the prepared set belongs to this index's key type.<br/>
    /// </summary>
    /// <param name="set">The prepared non-generic key set.</param>
    /// <returns>A non-generic query descriptor over the opened index.</returns>
    IIndexQuery InSet(LibraDexPreparedObjectSet set);

    /// <summary>
    /// Prepares a strict non-generic key-membership set for repeated `InSet` and `ExistsInSet` calls.<br/>
    /// </summary>
    /// <param name="keys">The runtime key values to validate and prepare.</param>
    /// <returns>A prepared non-generic key-membership descriptor.</returns>
    LibraDexPreparedObjectSet PrepareInSet(IEnumerable<object> keys);

    /// <summary>
    /// Captures a pattern query.<br/>
    /// Pattern objects are intentionally opaque at this layer because string, blob, GUID, and date projections may eventually use different pattern representations.<br/>
    /// </summary>
    /// <param name="pattern">The pattern descriptor to match.</param>
    /// <returns>A non-generic query descriptor over the opened index.</returns>
    IIndexQuery Matches(object pattern);

    /// <summary>
    /// Determines whether an exact key has at least one matching identity after validating the runtime key value against <see cref="KeyType"/>.<br/>
    /// This boolean shortcut communicates no materialization and lets the implementation stop at the first match.<br/>
    /// </summary>
    /// <param name="key">The runtime key value to test.</param>
    /// <returns><see langword="true"/> when at least one tuple exists for the key.</returns>
    bool Exists(object key);

    /// <summary>
    /// Determines whether any key in an ordinary runtime sequence has at least one matching identity.<br/>
    /// </summary>
    /// <param name="keys">The runtime key values to test.</param>
    /// <returns><see langword="true"/> when any supplied key exists in the index.</returns>
    bool ExistsIn(IEnumerable<object> keys);

    /// <summary>
    /// Determines whether any key in a prepared-set runtime sequence has at least one matching identity.<br/>
    /// This preserves the `ExistsInSet` intent for generated callers even before a reusable non-generic prepared-set handle is added.<br/>
    /// </summary>
    /// <param name="keys">The runtime key values to prepare and test.</param>
    /// <returns><see langword="true"/> when any supplied key exists in the index.</returns>
    bool ExistsInSet(IEnumerable<object> keys);
}

/// <summary>
/// Represents a strict non-generic query descriptor over an opened LibraDex index.<br/>
/// Terminal properties choose result shape after criteria have already been captured, matching the preferred criteria-first public grammar.<br/>
/// </summary>
public interface IIndexQuery
{
    /// <summary>
    /// Gets the non-generic index handle that produced this query.<br/>
    /// </summary>
    IIndex Index { get; }

    /// <summary>
    /// Gets public diagnostics describing the expected execution class.<br/>
    /// </summary>
    LibraDexQueryDiagnostics Diagnostics { get; }

    /// <summary>
    /// Gets tuple-shaped results for this query.<br/>
    /// </summary>
    IIndexResultProjection Tuples { get; }

    /// <summary>
    /// Gets key-shaped results for this query.<br/>
    /// </summary>
    IIndexResultProjection Keys { get; }

    /// <summary>
    /// Gets identity-shaped results for this query.<br/>
    /// </summary>
    IIndexResultProjection IDs { get; }
}

/// <summary>
/// Represents a non-generic terminal result-shape descriptor.<br/>
/// This first scaffold records projection intent; allocation-free object readers can be added once the programmatic API's execution contract is settled.<br/>
/// </summary>
public interface IIndexResultProjection
{
    /// <summary>
    /// Gets the requested result projection.<br/>
    /// </summary>
    LibraDexProjectionKind Projection { get; }
}

/// <summary>
/// Builds programmatic identity criteria over one non-generic index handle.<br/>
/// This surface exists for adapters and query builders that translate an external condition model into LibraDex without relying on handwritten fluent chains or generic type arguments.<br/>
/// </summary>
public interface IIndexCriteriaBuilder
{
    /// <summary>
    /// Gets the index that owns this criteria builder.<br/>
    /// </summary>
    IIndex Index { get; }

    /// <summary>
    /// Creates an all-identities criterion over this index.<br/>
    /// </summary>
    /// <returns>A leaf identity criterion.</returns>
    IIdentityCriterion All();

    /// <summary>
    /// Creates an exact-key identity criterion after validating the runtime key value against the owning index's key type.<br/>
    /// </summary>
    /// <param name="key">The key value to find.</param>
    /// <returns>A leaf identity criterion.</returns>
    IIdentityCriterion Find(object key);

    /// <summary>
    /// Creates an inclusive range identity criterion after validating both runtime key values against the owning index's key type.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower key.</param>
    /// <param name="upperKey">The inclusive upper key.</param>
    /// <returns>A leaf identity criterion.</returns>
    IIdentityCriterion Between(object lowerKey, object upperKey);

    /// <summary>
    /// Creates an exclusive upper-bound identity criterion.<br/>
    /// </summary>
    /// <param name="key">The exclusive upper key.</param>
    /// <returns>A leaf identity criterion.</returns>
    IIdentityCriterion Before(object key);

    /// <summary>
    /// Creates an inclusive upper-bound identity criterion.<br/>
    /// </summary>
    /// <param name="key">The inclusive upper key.</param>
    /// <returns>A leaf identity criterion.</returns>
    IIdentityCriterion AtOrBefore(object key);

    /// <summary>
    /// Creates an exclusive lower-bound identity criterion.<br/>
    /// </summary>
    /// <param name="key">The exclusive lower key.</param>
    /// <returns>A leaf identity criterion.</returns>
    IIdentityCriterion After(object key);

    /// <summary>
    /// Creates an inclusive lower-bound identity criterion.<br/>
    /// </summary>
    /// <param name="key">The inclusive lower key.</param>
    /// <returns>A leaf identity criterion.</returns>
    IIdentityCriterion AtOrAfter(object key);

    /// <summary>
    /// Creates a prefix identity criterion.<br/>
    /// </summary>
    /// <param name="prefix">The prefix key value.</param>
    /// <returns>A leaf identity criterion.</returns>
    IIdentityCriterion Prefix(object prefix);

    /// <summary>
    /// Creates a suffix identity criterion.<br/>
    /// </summary>
    /// <param name="suffix">The suffix key value.</param>
    /// <returns>A leaf identity criterion.</returns>
    IIdentityCriterion Suffix(object suffix);

    /// <summary>
    /// Creates a contains identity criterion.<br/>
    /// </summary>
    /// <param name="value">The contained key value.</param>
    /// <returns>A leaf identity criterion.</returns>
    IIdentityCriterion Contains(object value);

    /// <summary>
    /// Creates a membership identity criterion after validating every runtime key value against the owning index's key type.<br/>
    /// </summary>
    /// <param name="keys">The key values to match.</param>
    /// <returns>A leaf identity criterion.</returns>
    IIdentityCriterion In(IEnumerable<object> keys);

    /// <summary>
    /// Creates a prepared-set membership identity criterion after validating every runtime key value against the owning index's key type.<br/>
    /// This keeps `InSet` distinct from ordinary `In` so planners can later reuse prepared encoding or hash membership without losing caller intent.<br/>
    /// </summary>
    /// <param name="keys">The key values to prepare and match.</param>
    /// <returns>A leaf identity criterion.</returns>
    IIdentityCriterion InSet(IEnumerable<object> keys);

    /// <summary>
    /// Creates a prepared-set membership identity criterion from a non-generic prepared set.<br/>
    /// </summary>
    /// <param name="set">The prepared non-generic key set.</param>
    /// <returns>A leaf identity criterion.</returns>
    IIdentityCriterion InSet(LibraDexPreparedObjectSet set);

    /// <summary>
    /// Creates a pattern identity criterion.<br/>
    /// Pattern objects are stored opaquely so higher-level translators can round-trip their own pattern representation until a physical pattern engine is chosen.<br/>
    /// </summary>
    /// <param name="pattern">The pattern descriptor to match.</param>
    /// <returns>A leaf identity criterion.</returns>
    IIdentityCriterion Matches(object pattern);
}

/// <summary>
/// Represents a programmatic identity criterion over one identity group.<br/>
/// Leaves are index-backed lookups; composite nodes combine identity sets with `And`, `Or`, `Except`, and `Not` without introducing SQL-style table-join vocabulary.<br/>
/// </summary>
public interface IIdentityCriterion
{
    /// <summary>
    /// Gets the identity group over which this criterion is valid.<br/>
    /// Composite criteria require matching non-empty groups so accidental cross-source composition fails early.<br/>
    /// </summary>
    string Group { get; }

    /// <summary>
    /// Gets the node kind represented by this criterion.<br/>
    /// </summary>
    LibraDexIdentityCriterionNodeKind NodeKind { get; }

    /// <summary>
    /// Gets the index for a leaf criterion, or null for composite criteria.<br/>
    /// </summary>
    IIndex? Index { get; }

    /// <summary>
    /// Gets the lookup kind for a leaf criterion, or null for composite criteria.<br/>
    /// </summary>
    LibraDexCriteriaKind? CriteriaKind { get; }

    /// <summary>
    /// Gets captured operand values for a leaf criterion.<br/>
    /// Examples are one value for `Find`, two values for `Between`, many values for `In`, and no values for `All`.<br/>
    /// Composite criteria return an empty list because their operands are child criteria.<br/>
    /// </summary>
    IReadOnlyList<object?> Values { get; }

    /// <summary>
    /// Gets the left child for composite criteria, or null for leaf criteria.<br/>
    /// </summary>
    IIdentityCriterion? Left { get; }

    /// <summary>
    /// Gets the right child for binary composite criteria, or null for leaf and unary criteria.<br/>
    /// </summary>
    IIdentityCriterion? Right { get; }

    /// <summary>
    /// Gets public diagnostics describing the expected execution class for this node.<br/>
    /// Composite nodes currently report projection-style identity composition until physical stream execution is connected.<br/>
    /// </summary>
    LibraDexQueryDiagnostics Diagnostics { get; }

    /// <summary>
    /// Captures intersection with another criterion over the same identity group.<br/>
    /// </summary>
    /// <param name="other">The right-side criterion.</param>
    /// <returns>A composite identity criterion.</returns>
    IIdentityCriterion And(IIdentityCriterion other);

    /// <summary>
    /// Captures union with another criterion over the same identity group.<br/>
    /// </summary>
    /// <param name="other">The right-side criterion.</param>
    /// <returns>A composite identity criterion.</returns>
    IIdentityCriterion Or(IIdentityCriterion other);

    /// <summary>
    /// Captures exclusion of another criterion over the same identity group.<br/>
    /// </summary>
    /// <param name="other">The right-side criterion.</param>
    /// <returns>A composite identity criterion.</returns>
    IIdentityCriterion Except(IIdentityCriterion other);

    /// <summary>
    /// Captures negation of this criterion.<br/>
    /// </summary>
    /// <returns>A negated identity criterion.</returns>
    IIdentityCriterion Not();

    /// <summary>
    /// Gets the terminal identity projection descriptor for this criterion.<br/>
    /// This keeps programmatic criteria explicitly identity-shaped and avoids implying that indexed keys from multiple indexes have one common key type.<br/>
    /// </summary>
    IIdentityCriterionProjection IDs { get; }

    /// <summary>
    /// Creates an identity projection descriptor with explicit ordering, duplicate handling, paging, and bookmark options.<br/>
    /// </summary>
    /// <param name="ordering">The requested identity ordering contract.</param>
    /// <param name="deduplication">The requested duplicate identity policy.</param>
    /// <param name="skip">The number of matching identities to skip.</param>
    /// <param name="take">The optional maximum number of identities to return.</param>
    /// <param name="bookmark">The optional continuation bookmark.</param>
    /// <returns>An identity projection descriptor.</returns>
    IIdentityCriterionProjection IDsWith(
        IdentityResultOrdering ordering = IdentityResultOrdering.PlanNatural,
        IdentityDeduplication deduplication = IdentityDeduplication.Distinct,
        int skip = 0,
        int? take = null,
        LibraDexBookmark? bookmark = null);

    /// <summary>
    /// Gets criteria-scoped mutation descriptor builders.<br/>
    /// Mutation remains explicit and tied to retrieval context rather than existing as an unrelated broad update surface.<br/>
    /// </summary>
    IIdentityCriterionMutationBuilder Mutate { get; }
}

/// <summary>
/// Represents a terminal projection over a programmatic identity criterion.<br/>
/// The first scaffold records projection intent and leaves physical multi-index execution for the dedicated composition layer.<br/>
/// </summary>
public interface IIdentityCriterionProjection
{
    /// <summary>
    /// Gets the criterion that produced this projection.<br/>
    /// </summary>
    IIdentityCriterion Criterion { get; }

    /// <summary>
    /// Gets the requested result projection.<br/>
    /// </summary>
    LibraDexProjectionKind Projection { get; }

    /// <summary>
    /// Gets identity projection execution-shaping options.<br/>
    /// </summary>
    LibraDexIdentityQueryOptions Options { get; }

    /// <summary>
    /// Builds a physical-planning descriptor for the criteria projection.<br/>
    /// The first planner classifies streamability and materialization requirements without executing or materializing identities.<br/>
    /// </summary>
    /// <returns>An identity execution plan descriptor.</returns>
    LibraDexIdentityExecutionPlan Plan();

    /// <summary>
    /// Executes the supported identity criteria projection and materializes identities as runtime objects.<br/>
    /// This first connected executor supports exact, range, boundary, all, and membership leaves over generic indexes; unsupported criteria throw rather than silently scanning.<br/>
    /// </summary>
    /// <returns>A materialized identity list.</returns>
    IReadOnlyList<object> ToList();

    /// <summary>
    /// Iterates supported identity criteria without requiring the final result to be materialized as a list first when the requested ordering permits streaming.<br/>
    /// Explicit identity ordering may still materialize internally until ordered identity-stream execution is connected.<br/>
    /// </summary>
    /// <returns>An enumerable over matching identity objects.</returns>
    IEnumerable<object> Iterate();

    /// <summary>
    /// Iterates supported identity criteria and validates each returned identity against the requested CLR type.<br/>
    /// This is the streaming counterpart to <see cref="ToList{TIdentity}"/> for adapters that know the identity type at their boundary.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The expected identity CLR type.</typeparam>
    /// <returns>An enumerable over matching typed identities.</returns>
    IEnumerable<TIdentity> Iterate<TIdentity>();

    /// <summary>
    /// Executes the supported identity criteria projection and materializes identities as a typed list.<br/>
    /// Each runtime identity is validated against <typeparamref name="TIdentity"/> before it is returned.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The expected identity CLR type.</typeparam>
    /// <returns>A materialized typed identity list.</returns>
    IReadOnlyList<TIdentity> ToList<TIdentity>();

    /// <summary>
    /// Executes the supported identity criteria projection and returns identities with the plan and diagnostics used for the execution.<br/>
    /// </summary>
    /// <returns>An identity execution result descriptor.</returns>
    LibraDexIdentityExecutionResult Execute();
}

/// <summary>
/// Describes one normalized internal read primitive requested by a materialized identity condition.<br/>
/// Public builders keep developer-facing names such as `Prefix`, `Between`, and `InSet`; this request is the private bridge from that intent to the physical index executor.<br/>
/// </summary>
/// <param name="CriteriaKind">The normalized primitive lookup kind.</param>
/// <param name="Values">The already materialized operand values for the primitive.</param>
/// <param name="TakeLimit">An optional maximum number of identities required by the caller.</param>
internal readonly record struct LibraDexIdentityPrimitiveRequest(
    LibraDexCriteriaKind CriteriaKind,
    IReadOnlyList<object?> Values,
    int? TakeLimit = null);

internal interface IIdentityPrimitiveExecutor
{
    /// <summary>
    /// Streams identities for one normalized primitive request in the index's natural physical order.<br/>
    /// The enumerable is intentionally internal so condition execution can use a cursor-style path without adding more public direct lookup verbs.<br/>
    /// Implementations should honor <see cref="LibraDexIdentityPrimitiveRequest.TakeLimit"/> while reading, not by materializing and trimming afterwards.<br/>
    /// </summary>
    /// <param name="request">The normalized primitive request to execute.</param>
    /// <returns>A forward-only identity sequence.</returns>
    IEnumerable<object> IterateIdentityPrimitive(LibraDexIdentityPrimitiveRequest request);

    IReadOnlyList<object> ExecuteIdentityPrimitive(LibraDexIdentityPrimitiveRequest request);

    long CountIdentityPrimitive(LibraDexIdentityPrimitiveRequest request);

    IReadOnlyList<object> ExecuteAllIdentities();

    /// <summary>
    /// Streams the best available identity universe for the executor's identity group.<br/>
    /// Grouped catalog indexes should return the de-duplicated union of identities visible through indexes in the same group, while ungrouped handles may fall back to their own `All` primitive.<br/>
    /// This gives negated criteria a group-aware universe without exposing a public identity-source abstraction yet.<br/>
    /// </summary>
    /// <returns>A forward-only identity sequence representing the current identity universe.</returns>
    IEnumerable<object> IterateIdentityUniverse();
}

internal sealed class LibraDexObjectIndexQuery<TKey, TIdentity> : IIndexQuery
{
    private readonly LibraDexProjectionDescriptor tuples;
    private readonly LibraDexProjectionDescriptor keys;
    private readonly LibraDexProjectionDescriptor ids;

    internal LibraDexObjectIndexQuery(IIndex index, LibraDexQueryDiagnostics diagnostics)
    {
        Index = index;
        Diagnostics = diagnostics;
        tuples = new LibraDexProjectionDescriptor(LibraDexProjectionKind.Tuples);
        keys = new LibraDexProjectionDescriptor(LibraDexProjectionKind.Keys);
        ids = new LibraDexProjectionDescriptor(LibraDexProjectionKind.Identities);
    }

    public IIndex Index { get; }

    public LibraDexQueryDiagnostics Diagnostics { get; }

    public IIndexResultProjection Tuples => tuples;

    public IIndexResultProjection Keys => keys;

    public IIndexResultProjection IDs => ids;
}

internal sealed class LibraDexProjectionDescriptor : IIndexResultProjection
{
    internal LibraDexProjectionDescriptor(LibraDexProjectionKind projection)
    {
        Projection = projection;
    }

    public LibraDexProjectionKind Projection { get; }
}

internal sealed class LibraDexObjectCriteriaBuilder<TKey, TIdentity> : IIndexCriteriaBuilder
{
    private readonly IIndex index;

    internal LibraDexObjectCriteriaBuilder(IIndex index)
    {
        this.index = index;
    }

    public IIndex Index => index;

    public IIdentityCriterion All()
    {
        return LibraDexIdentityCriterion.Leaf(index, LibraDexCriteriaKind.All, CreateDiagnostics(LibraDexCriteriaKind.All));
    }

    public IIdentityCriterion Find(object key)
    {
        return LibraDexIdentityCriterion.Leaf(index, LibraDexCriteriaKind.Find, CreateDiagnostics(LibraDexCriteriaKind.Find), RequireKey(key, nameof(key)));
    }

    public IIdentityCriterion Between(object lowerKey, object upperKey)
    {
        return LibraDexIdentityCriterion.Leaf(
            index,
            LibraDexCriteriaKind.Between,
            CreateDiagnostics(LibraDexCriteriaKind.Between),
            RequireKey(lowerKey, nameof(lowerKey)),
            RequireKey(upperKey, nameof(upperKey)));
    }

    public IIdentityCriterion Before(object key)
    {
        return LibraDexIdentityCriterion.Leaf(index, LibraDexCriteriaKind.Before, CreateDiagnostics(LibraDexCriteriaKind.Before), RequireKey(key, nameof(key)));
    }

    public IIdentityCriterion AtOrBefore(object key)
    {
        return LibraDexIdentityCriterion.Leaf(index, LibraDexCriteriaKind.AtOrBefore, CreateDiagnostics(LibraDexCriteriaKind.AtOrBefore), RequireKey(key, nameof(key)));
    }

    public IIdentityCriterion After(object key)
    {
        return LibraDexIdentityCriterion.Leaf(index, LibraDexCriteriaKind.After, CreateDiagnostics(LibraDexCriteriaKind.After), RequireKey(key, nameof(key)));
    }

    public IIdentityCriterion AtOrAfter(object key)
    {
        return LibraDexIdentityCriterion.Leaf(index, LibraDexCriteriaKind.AtOrAfter, CreateDiagnostics(LibraDexCriteriaKind.AtOrAfter), RequireKey(key, nameof(key)));
    }

    public IIdentityCriterion Prefix(object prefix)
    {
        return LibraDexIdentityCriterion.Leaf(index, LibraDexCriteriaKind.Prefix, CreateDiagnostics(LibraDexCriteriaKind.Prefix), RequireKey(prefix, nameof(prefix)));
    }

    public IIdentityCriterion Suffix(object suffix)
    {
        return LibraDexIdentityCriterion.Leaf(index, LibraDexCriteriaKind.Suffix, CreateDiagnostics(LibraDexCriteriaKind.Suffix), RequireKey(suffix, nameof(suffix)));
    }

    public IIdentityCriterion Contains(object value)
    {
        return LibraDexIdentityCriterion.Leaf(index, LibraDexCriteriaKind.Contains, CreateDiagnostics(LibraDexCriteriaKind.Contains), RequireKey(value, nameof(value)));
    }

    public IIdentityCriterion In(IEnumerable<object> keys)
    {
        object[] values = RequireKeys(keys, nameof(keys));
        return LibraDexIdentityCriterion.Leaf(index, LibraDexCriteriaKind.In, CreateDiagnostics(LibraDexCriteriaKind.In), values);
    }

    public IIdentityCriterion InSet(IEnumerable<object> keys)
    {
        object[] values = RequireKeys(keys, nameof(keys));
        return LibraDexIdentityCriterion.Leaf(index, LibraDexCriteriaKind.InSet, CreateDiagnostics(LibraDexCriteriaKind.InSet), values);
    }

    public IIdentityCriterion InSet(LibraDexPreparedObjectSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (set.KeyType != index.KeyType)
        {
            throw new ArgumentException($"Prepared set key type {set.KeyType.FullName} does not match index key type {index.KeyType.FullName}.", nameof(set));
        }

        return LibraDexIdentityCriterion.Leaf(index, LibraDexCriteriaKind.InSet, CreateDiagnostics(LibraDexCriteriaKind.InSet), set);
    }

    public IIdentityCriterion Matches(object pattern)
    {
        return LibraDexIdentityCriterion.Leaf(index, LibraDexCriteriaKind.Matches, CreateDiagnostics(LibraDexCriteriaKind.Matches), pattern);
    }

    private static LibraDexQueryDiagnostics CreateDiagnostics(LibraDexCriteriaKind criteriaKind)
    {
        LibraDexExecutionKind executionKind = criteriaKind switch
        {
            LibraDexCriteriaKind.Prefix => LibraDexExecutionKind.Scan,
            LibraDexCriteriaKind.Suffix => LibraDexExecutionKind.Scan,
            LibraDexCriteriaKind.Contains => LibraDexExecutionKind.Scan,
            LibraDexCriteriaKind.Matches => LibraDexExecutionKind.Scan,
            LibraDexCriteriaKind.In => LibraDexExecutionKind.Projection,
            LibraDexCriteriaKind.InSet => LibraDexExecutionKind.Projection,
            _ => LibraDexExecutionKind.FastPath
        };

        return new LibraDexQueryDiagnostics(executionKind);
    }

    private object RequireKey(object? value, string parameterName)
    {
        if (value is null)
        {
            throw new ArgumentNullException(parameterName);
        }

        if (!index.KeyType.IsInstanceOfType(value))
        {
            throw new ArgumentException($"Runtime key type {value.GetType().FullName} does not match index key type {index.KeyType.FullName}.", parameterName);
        }

        return value;
    }

    private object[] RequireKeys(IEnumerable<object> values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values.Select(value => RequireKey(value, parameterName)).ToArray();
    }
}

internal sealed class LibraDexIdentityCriterion : IIdentityCriterion
{
    private readonly LibraDexIdentityCriterionProjection ids;
    private readonly LibraDexIdentityCriterionMutationBuilder mutations;

    private LibraDexIdentityCriterion(
        string group,
        LibraDexIdentityCriterionNodeKind nodeKind,
        IIndex? index,
        LibraDexCriteriaKind? criteriaKind,
        IReadOnlyList<object?> values,
        IIdentityCriterion? left,
        IIdentityCriterion? right,
        LibraDexQueryDiagnostics diagnostics)
    {
        Group = group;
        NodeKind = nodeKind;
        Index = index;
        CriteriaKind = criteriaKind;
        Values = values;
        Left = left;
        Right = right;
        Diagnostics = diagnostics;
        ids = new LibraDexIdentityCriterionProjection(this, LibraDexIdentityQueryOptions.Default);
        mutations = new LibraDexIdentityCriterionMutationBuilder(this);
    }

    public string Group { get; }

    public LibraDexIdentityCriterionNodeKind NodeKind { get; }

    public IIndex? Index { get; }

    public LibraDexCriteriaKind? CriteriaKind { get; }

    public IReadOnlyList<object?> Values { get; }

    public IIdentityCriterion? Left { get; }

    public IIdentityCriterion? Right { get; }

    public LibraDexQueryDiagnostics Diagnostics { get; }

    public IIdentityCriterionProjection IDs => ids;

    public IIdentityCriterionMutationBuilder Mutate => mutations;

    public IIdentityCriterionProjection IDsWith(
        IdentityResultOrdering ordering = IdentityResultOrdering.PlanNatural,
        IdentityDeduplication deduplication = IdentityDeduplication.Distinct,
        int skip = 0,
        int? take = null,
        LibraDexBookmark? bookmark = null)
    {
        LibraDexIdentityQueryOptions options = new(ordering, deduplication, skip, take, bookmark);
        options.Validate();
        return new LibraDexIdentityCriterionProjection(this, options);
    }

    internal static IIdentityCriterion Leaf(IIndex index, LibraDexCriteriaKind criteriaKind, LibraDexQueryDiagnostics diagnostics, params object?[] values)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (string.IsNullOrWhiteSpace(index.Group))
        {
            throw new InvalidOperationException("Programmatic identity criteria require an index with persisted identity-group metadata.");
        }

        return new LibraDexIdentityCriterion(
            index.Group,
            LibraDexIdentityCriterionNodeKind.Leaf,
            index,
            criteriaKind,
            Array.AsReadOnly(values),
            left: null,
            right: null,
            diagnostics);
    }

    public IIdentityCriterion And(IIdentityCriterion other)
    {
        return Compose(LibraDexIdentityCriterionNodeKind.And, this, other);
    }

    public IIdentityCriterion Or(IIdentityCriterion other)
    {
        return Compose(LibraDexIdentityCriterionNodeKind.Or, this, other);
    }

    public IIdentityCriterion Except(IIdentityCriterion other)
    {
        return Compose(LibraDexIdentityCriterionNodeKind.Except, this, other);
    }

    public IIdentityCriterion Not()
    {
        return new LibraDexIdentityCriterion(
            Group,
            LibraDexIdentityCriterionNodeKind.Not,
            index: null,
            criteriaKind: null,
            Array.Empty<object?>(),
            left: this,
            right: null,
            new LibraDexQueryDiagnostics(LibraDexExecutionKind.Projection));
    }

    private static IIdentityCriterion Compose(
        LibraDexIdentityCriterionNodeKind nodeKind,
        IIdentityCriterion left,
        IIdentityCriterion right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (string.IsNullOrWhiteSpace(left.Group) ||
            string.IsNullOrWhiteSpace(right.Group) ||
            !string.Equals(left.Group, right.Group, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Programmatic identity criteria can only be composed inside the same identity group.");
        }

        return new LibraDexIdentityCriterion(
            left.Group,
            nodeKind,
            index: null,
            criteriaKind: null,
            Array.Empty<object?>(),
            left,
            right,
            new LibraDexQueryDiagnostics(LibraDexExecutionKind.Projection));
    }
}

internal sealed class LibraDexIdentityCriterionProjection : IIdentityCriterionProjection
{
    internal LibraDexIdentityCriterionProjection(IIdentityCriterion criterion, LibraDexIdentityQueryOptions options)
    {
        Criterion = criterion;
        options.Validate();
        Options = options;
    }

    public IIdentityCriterion Criterion { get; }

    public LibraDexProjectionKind Projection => LibraDexProjectionKind.Identities;

    public LibraDexIdentityQueryOptions Options { get; }

    public LibraDexIdentityExecutionPlan Plan()
    {
        return LibraDexIdentityExecutionPlanner.Plan(Criterion, Options);
    }

    public IReadOnlyList<object> ToList()
    {
        return Execute().Identities;
    }

    public IEnumerable<object> Iterate()
    {
        return LibraDexIdentityExecutionPlanner.Iterate(Criterion, Options);
    }

    public IEnumerable<TIdentity> Iterate<TIdentity>()
    {
        int ordinal = 0;
        foreach (object identityObject in Iterate())
        {
            if (identityObject is not TIdentity identity)
            {
                throw new InvalidCastException($"Identity at ordinal {ordinal} is {identityObject.GetType().FullName}, not {typeof(TIdentity).FullName}.");
            }

            ordinal++;
            yield return identity;
        }
    }

    public IReadOnlyList<TIdentity> ToList<TIdentity>()
    {
        IReadOnlyList<object> identities = ToList();
        TIdentity[] typed = new TIdentity[identities.Count];
        for (int i = 0; i < identities.Count; i++)
        {
            typed[i] = identities[i] is TIdentity identity
                ? identity
                : throw new InvalidCastException($"Identity at ordinal {i} is {identities[i].GetType().FullName}, not {typeof(TIdentity).FullName}.");
        }

        return typed;
    }

    public LibraDexIdentityExecutionResult Execute()
    {
        return LibraDexIdentityExecutionPlanner.Execute(Criterion, Options);
    }

}

internal static class LibraDexIdentityExecutionPlanner
{
    internal static LibraDexIdentityExecutionPlan Plan(IIdentityCriterion criterion, LibraDexIdentityQueryOptions options)
    {
        ArgumentNullException.ThrowIfNull(criterion);
        options.Validate();
        PlanAccumulator accumulator = new();
        LibraDexIdentityExecutionPlan root = PlanNode(criterion, options, accumulator);
        bool requiresOrdering = options.Ordering != IdentityResultOrdering.PlanNatural;
        bool requiresPaging = options.SkipCount > 0 || options.TakeCount is not null || options.Bookmark is not null;
        bool requiresDistinct = options.Deduplication == IdentityDeduplication.Distinct || root.Kind == LibraDexIdentityPlanKind.Union;
        LibraDexIdentityPlanMaterialization materialization = MergeMaterialization(
            root.Materialization,
            requiresOrdering || requiresPaging || requiresDistinct
                ? LibraDexIdentityPlanMaterialization.IdentitySet
                : LibraDexIdentityPlanMaterialization.None);

        return new LibraDexIdentityExecutionPlan(
            criterion,
            options,
            root.Kind,
            materialization,
            requiresDistinct,
            requiresOrdering,
            requiresPaging,
            root.ContainsNegation,
            root.LeafCount,
            root.Depth,
            accumulator.Indexes,
            root.Children);
    }

    internal static LibraDexIdentityExecutionResult Execute(IIdentityCriterion criterion, LibraDexIdentityQueryOptions options)
    {
        ArgumentNullException.ThrowIfNull(criterion);
        options.Validate();
        LibraDexIdentityExecutionPlan plan = Plan(criterion, options);
        List<object> identities = ExecuteNode(criterion);
        identities = ApplyDeduplication(identities, options.Deduplication);
        ApplyOrdering(identities, options.Ordering);
        IReadOnlyList<object> paged = ApplyPaging(identities, options);
        return new LibraDexIdentityExecutionResult(
            paged,
            plan,
            new LibraDexQueryDiagnostics(
                plan.Materialization == LibraDexIdentityPlanMaterialization.IdentitySet
                    ? LibraDexExecutionKind.Projection
                    : LibraDexExecutionKind.FastPath,
                RowsScanned: identities.Count,
                RowsReturned: paged.Count));
    }

    internal static IEnumerable<object> Iterate(IIdentityCriterion criterion, LibraDexIdentityQueryOptions options)
    {
        ArgumentNullException.ThrowIfNull(criterion);
        options.Validate();
        if (options.Ordering != IdentityResultOrdering.PlanNatural)
        {
            foreach (object identity in Execute(criterion, options).Identities)
            {
                yield return identity;
            }

            yield break;
        }

        IEnumerable<object> identities = IterateNode(criterion);
        if (options.Deduplication == IdentityDeduplication.Distinct)
        {
            identities = DistinctIterator(identities);
        }

        foreach (object identity in ApplyStreamingPaging(identities, options))
        {
            yield return identity;
        }
    }

    internal static bool Exists(IIdentityCriterion criterion, IdentityDeduplication deduplication)
    {
        ArgumentNullException.ThrowIfNull(criterion);
        _ = deduplication;
        return ExistsNode(criterion);
    }

    internal static long Count(IIdentityCriterion criterion, IdentityDeduplication deduplication)
    {
        ArgumentNullException.ThrowIfNull(criterion);
        if (deduplication == IdentityDeduplication.Preserve && TryCountLeaf(criterion, out long leafCount))
        {
            return leafCount;
        }

        LibraDexIdentityQueryOptions options = new(
            IdentityResultOrdering.PlanNatural,
            deduplication,
            SkipCount: 0,
            TakeCount: null,
            Bookmark: null);
        long count = 0;
        foreach (object _ in Iterate(criterion, options))
        {
            count++;
        }

        return count;
    }

    private static List<object> ExecuteNode(IIdentityCriterion criterion)
    {
        return criterion.NodeKind switch
        {
            LibraDexIdentityCriterionNodeKind.Leaf => ExecuteLeaf(criterion),
            LibraDexIdentityCriterionNodeKind.And => Intersect(ExecuteNode(RequireLeft(criterion)), ExecuteNode(RequireRight(criterion))),
            LibraDexIdentityCriterionNodeKind.Or => Union(ExecuteNode(RequireLeft(criterion)), ExecuteNode(RequireRight(criterion))),
            LibraDexIdentityCriterionNodeKind.Except => Except(ExecuteNode(RequireLeft(criterion)), ExecuteNode(RequireRight(criterion))),
            LibraDexIdentityCriterionNodeKind.Not => Complement(criterion, ExecuteNode(RequireLeft(criterion))),
            _ => throw new NotSupportedException($"Identity criterion node {criterion.NodeKind} is not supported by identity execution.")
        };
    }

    private static IEnumerable<object> IterateNode(IIdentityCriterion criterion)
    {
        return criterion.NodeKind switch
        {
            LibraDexIdentityCriterionNodeKind.Leaf => IterateLeaf(criterion),
            LibraDexIdentityCriterionNodeKind.And => IntersectIterator(IterateNode(RequireLeft(criterion)), ExecuteNode(RequireRight(criterion))),
            LibraDexIdentityCriterionNodeKind.Or => UnionIterator(IterateNode(RequireLeft(criterion)), IterateNode(RequireRight(criterion))),
            LibraDexIdentityCriterionNodeKind.Except => ExceptIterator(IterateNode(RequireLeft(criterion)), ExecuteNode(RequireRight(criterion))),
            LibraDexIdentityCriterionNodeKind.Not => ComplementIterator(criterion, ExecuteNode(RequireLeft(criterion))),
            _ => throw new NotSupportedException($"Identity criterion node {criterion.NodeKind} is not supported by identity iteration.")
        };
    }

    /// <summary>
    /// Tests whether a criteria tree can produce at least one identity without routing through the general projection pipeline.<br/>
    /// Existence is independent of duplicate handling, so this recursive path can short-circuit `Or`, leaf, intersection, difference, and complement shapes directly.<br/>
    /// The method still preserves explicit primitive failures for unsupported leaves instead of treating unsupported execution as an empty result.<br/>
    /// </summary>
    /// <param name="criterion">The criteria tree to test.</param>
    /// <returns><see langword="true"/> when at least one identity can be produced.</returns>
    private static bool ExistsNode(IIdentityCriterion criterion)
    {
        return criterion.NodeKind switch
        {
            LibraDexIdentityCriterionNodeKind.Leaf => TryLeafExists(criterion, out bool leafExists)
                ? leafExists
                : IterateNode(criterion).Take(1).Any(),
            LibraDexIdentityCriterionNodeKind.And => ExistsIntersection(RequireLeft(criterion), RequireRight(criterion)),
            LibraDexIdentityCriterionNodeKind.Or => ExistsNode(RequireLeft(criterion)) || ExistsNode(RequireRight(criterion)),
            LibraDexIdentityCriterionNodeKind.Except => ExistsDifference(RequireLeft(criterion), RequireRight(criterion)),
            LibraDexIdentityCriterionNodeKind.Not => ComplementIterator(criterion, ExecuteNode(RequireLeft(criterion))).Take(1).Any(),
            _ => throw new NotSupportedException($"Identity criterion node {criterion.NodeKind} is not supported by identity existence checks.")
        };
    }

    private static bool ExistsIntersection(IIdentityCriterion left, IIdentityCriterion right)
    {
        List<object> rightIdentities = ExecuteNode(right);
        if (rightIdentities.Count == 0)
        {
            return false;
        }

        HashSet<object> rightSet = new(rightIdentities);
        foreach (object identity in IterateNode(left))
        {
            if (rightSet.Contains(identity))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ExistsDifference(IIdentityCriterion left, IIdentityCriterion right)
    {
        HashSet<object> rightSet = new(ExecuteNode(right));
        foreach (object identity in IterateNode(left))
        {
            if (!rightSet.Contains(identity))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Opens the internal primitive iterator for a leaf criterion without materializing the leaf result list first.<br/>
    /// This is the condition-builder bridge into LibraDex's cursor-like read mechanics: public criteria keep developer-facing names, while this layer executes one normalized primitive request.<br/>
    /// The caller remains responsible for higher-level composition, de-duplication, ordering fallback, and paging.<br/>
    /// </summary>
    /// <param name="criterion">The leaf criterion to stream.</param>
    /// <returns>A forward-only identity sequence for the leaf.</returns>
    private static IEnumerable<object> IterateLeaf(IIdentityCriterion criterion)
    {
        if (criterion.CriteriaKind is null)
        {
            throw new NotSupportedException("The identity criterion leaf is not backed by an executable LibraDex index.");
        }

        if (criterion.Index is not IIdentityPrimitiveExecutor primitiveExecutor)
        {
            throw new NotSupportedException("The identity criterion leaf is not backed by an executable LibraDex primitive executor.");
        }

        return primitiveExecutor.IterateIdentityPrimitive(
            new LibraDexIdentityPrimitiveRequest(criterion.CriteriaKind.Value, criterion.Values));
    }

    private static List<object> ExecuteLeaf(IIdentityCriterion criterion)
    {
        if (criterion.CriteriaKind is null)
        {
            throw new NotSupportedException("The identity criterion leaf is not backed by an executable LibraDex index.");
        }

        if (criterion.Index is not IIdentityPrimitiveExecutor primitiveExecutor)
        {
            throw new NotSupportedException("The identity criterion leaf is not backed by an executable LibraDex primitive executor.");
        }

        return primitiveExecutor.ExecuteIdentityPrimitive(
            new LibraDexIdentityPrimitiveRequest(criterion.CriteriaKind.Value, criterion.Values)).ToList();
    }

    private static bool TryExecuteLeafWithTake(IIdentityCriterion criterion, int takeLimit, out IReadOnlyList<object>? identities)
    {
        identities = null;
        if (criterion.NodeKind != LibraDexIdentityCriterionNodeKind.Leaf ||
            criterion.CriteriaKind is null ||
            criterion.Index is not IIdentityPrimitiveExecutor primitiveExecutor)
        {
            return false;
        }

        identities = primitiveExecutor.ExecuteIdentityPrimitive(
            new LibraDexIdentityPrimitiveRequest(criterion.CriteriaKind.Value, criterion.Values, takeLimit));
        return true;
    }

    /// <summary>
    /// Tests a single executable leaf through the primitive iterator with a one-row take limit.<br/>
    /// This preserves the no-materialization intent of `Exists` for exact, range, and membership leaves while keeping unsupported primitive errors explicit.<br/>
    /// The method returns <see langword="false"/> only when the criterion is not a primitive leaf; it does not swallow execution failures from a valid leaf.<br/>
    /// </summary>
    /// <param name="criterion">The criterion to test.</param>
    /// <param name="exists">Receives whether the primitive leaf produced at least one identity.</param>
    /// <returns><see langword="true"/> when the criterion was an executable primitive leaf.</returns>
    private static bool TryLeafExists(IIdentityCriterion criterion, out bool exists)
    {
        exists = false;
        if (criterion.NodeKind != LibraDexIdentityCriterionNodeKind.Leaf ||
            criterion.CriteriaKind is null ||
            criterion.Index is not IIdentityPrimitiveExecutor primitiveExecutor)
        {
            return false;
        }

        using IEnumerator<object> enumerator = primitiveExecutor.IterateIdentityPrimitive(
            new LibraDexIdentityPrimitiveRequest(criterion.CriteriaKind.Value, criterion.Values, TakeLimit: 1)).GetEnumerator();
        exists = enumerator.MoveNext();
        return true;
    }

    private static bool TryCountLeaf(IIdentityCriterion criterion, out long count)
    {
        count = 0;
        if (criterion.NodeKind != LibraDexIdentityCriterionNodeKind.Leaf ||
            criterion.CriteriaKind is null ||
            criterion.Index is not IIdentityPrimitiveExecutor primitiveExecutor)
        {
            return false;
        }

        count = primitiveExecutor.CountIdentityPrimitive(
            new LibraDexIdentityPrimitiveRequest(criterion.CriteriaKind.Value, criterion.Values));
        return true;
    }

    private static List<object> Complement(IIdentityCriterion criterion, IReadOnlyList<object> excluded)
    {
        IIdentityCriterion leaf = FindFirstLeaf(criterion);
        if (leaf.Index is not IIdentityPrimitiveExecutor executor)
        {
            throw new NotSupportedException("Negated identity criteria require at least one executable index to provide the identity universe.");
        }

        return Except(executor.IterateIdentityUniverse().ToList(), excluded);
    }

    /// <summary>
    /// Streams a complement by reading the best identity universe available from the first executable leaf's primitive executor and excluding the child result set.<br/>
    /// Grouped catalog indexes provide a de-duplicated union of identities across the identity group; ungrouped handles fall back to the first leaf's own `All` primitive.<br/>
    /// This avoids materializing the entire universe for plan-natural negated iteration while keeping the universe source behind the internal primitive executor contract.<br/>
    /// </summary>
    /// <param name="criterion">The negated criterion whose first leaf supplies the current universe approximation.</param>
    /// <param name="excluded">The materialized child identities to exclude.</param>
    /// <returns>A forward-only sequence of identities outside the child criterion.</returns>
    private static IEnumerable<object> ComplementIterator(IIdentityCriterion criterion, IReadOnlyList<object> excluded)
    {
        IIdentityCriterion leaf = FindFirstLeaf(criterion);
        if (leaf.Index is not IIdentityPrimitiveExecutor executor)
        {
            throw new NotSupportedException("Negated identity criteria require at least one executable index to provide the identity universe.");
        }

        HashSet<object> excludedSet = new(excluded);
        foreach (object identity in executor.IterateIdentityUniverse())
        {
            if (!excludedSet.Contains(identity))
            {
                yield return identity;
            }
        }
    }

    private static IIdentityCriterion FindFirstLeaf(IIdentityCriterion criterion)
    {
        if (criterion.NodeKind == LibraDexIdentityCriterionNodeKind.Leaf)
        {
            return criterion;
        }

        if (criterion.Left is not null)
        {
            return FindFirstLeaf(criterion.Left);
        }

        if (criterion.Right is not null)
        {
            return FindFirstLeaf(criterion.Right);
        }

        throw new InvalidOperationException("Identity criterion tree does not contain an index-backed leaf.");
    }

    private static List<object> Union(IReadOnlyList<object> left, IReadOnlyList<object> right)
    {
        List<object> result = new(left.Count + right.Count);
        result.AddRange(left);
        result.AddRange(right);
        return result;
    }

    private static List<object> Intersect(IReadOnlyList<object> left, IReadOnlyList<object> right)
    {
        HashSet<object> rightSet = new(right);
        List<object> result = new();
        for (int i = 0; i < left.Count; i++)
        {
            if (rightSet.Contains(left[i]))
            {
                result.Add(left[i]);
            }
        }

        return result;
    }

    private static List<object> Except(IReadOnlyList<object> left, IReadOnlyList<object> right)
    {
        HashSet<object> rightSet = new(right);
        List<object> result = new();
        for (int i = 0; i < left.Count; i++)
        {
            if (!rightSet.Contains(left[i]))
            {
                result.Add(left[i]);
            }
        }

        return result;
    }

    private static IEnumerable<object> UnionIterator(IEnumerable<object> left, IEnumerable<object> right)
    {
        foreach (object identity in left)
        {
            yield return identity;
        }

        foreach (object identity in right)
        {
            yield return identity;
        }
    }

    private static IEnumerable<object> IntersectIterator(IEnumerable<object> left, IReadOnlyList<object> right)
    {
        HashSet<object> rightSet = new(right);
        foreach (object identity in left)
        {
            if (rightSet.Contains(identity))
            {
                yield return identity;
            }
        }
    }

    private static IEnumerable<object> ExceptIterator(IEnumerable<object> left, IReadOnlyList<object> right)
    {
        HashSet<object> rightSet = new(right);
        foreach (object identity in left)
        {
            if (!rightSet.Contains(identity))
            {
                yield return identity;
            }
        }
    }

    private static IEnumerable<object> DistinctIterator(IEnumerable<object> identities)
    {
        HashSet<object> seen = new();
        foreach (object identity in identities)
        {
            if (seen.Add(identity))
            {
                yield return identity;
            }
        }
    }

    private static IEnumerable<object> ApplyStreamingPaging(IEnumerable<object> identities, LibraDexIdentityQueryOptions options)
    {
        long skip = options.SkipCount;
        if (options.Bookmark is { } bookmark)
        {
            skip = Math.Max(skip, bookmark.Position);
        }

        long returned = 0;
        foreach (object identity in identities)
        {
            if (skip > 0)
            {
                skip--;
                continue;
            }

            if (options.TakeCount is not null && returned >= options.TakeCount.Value)
            {
                yield break;
            }

            returned++;
            yield return identity;
        }
    }

    private static List<object> ApplyDeduplication(List<object> identities, IdentityDeduplication deduplication)
    {
        if (deduplication == IdentityDeduplication.Preserve)
        {
            return identities;
        }

        HashSet<object> seen = new();
        List<object> result = new(identities.Count);
        for (int i = 0; i < identities.Count; i++)
        {
            if (seen.Add(identities[i]))
            {
                result.Add(identities[i]);
            }
        }

        return result;
    }

    private static void ApplyOrdering(List<object> identities, IdentityResultOrdering ordering)
    {
        if (ordering == IdentityResultOrdering.PlanNatural)
        {
            return;
        }

        identities.Sort(CompareIdentityObjects);
        if (ordering == IdentityResultOrdering.IdentityDescending)
        {
            identities.Reverse();
        }
    }

    private static int CompareIdentityObjects(object? left, object? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        if (left is IComparable comparable)
        {
            return comparable.CompareTo(right);
        }

        throw new NotSupportedException($"Identity type {left.GetType().FullName} does not support ordering.");
    }

    private static IReadOnlyList<object> ApplyPaging(List<object> identities, LibraDexIdentityQueryOptions options)
    {
        int start = options.SkipCount;
        if (options.Bookmark is { } bookmark)
        {
            start = Math.Max(start, checked((int)Math.Min(bookmark.Position, int.MaxValue)));
        }

        if (start >= identities.Count)
        {
            return Array.Empty<object>();
        }

        int count = options.TakeCount ?? identities.Count - start;
        count = Math.Min(count, identities.Count - start);
        return identities.GetRange(start, count);
    }

    private static LibraDexIdentityExecutionPlan PlanNode(
        IIdentityCriterion criterion,
        LibraDexIdentityQueryOptions options,
        PlanAccumulator accumulator)
    {
        switch (criterion.NodeKind)
        {
            case LibraDexIdentityCriterionNodeKind.Leaf:
                if (criterion.Index is not null)
                {
                    accumulator.Add(criterion.Index);
                }

                return new LibraDexIdentityExecutionPlan(
                    criterion,
                    options,
                    LibraDexIdentityPlanKind.SingleIndex,
                    LibraDexIdentityPlanMaterialization.None,
                    requiresDistinct: false,
                    requiresOrdering: false,
                    requiresPaging: false,
                    containsNegation: false,
                    leafCount: 1,
                    depth: 1,
                    criterion.Index is null ? Array.Empty<IIndex>() : new[] { criterion.Index },
                    Array.Empty<LibraDexIdentityExecutionPlan>());

            case LibraDexIdentityCriterionNodeKind.Not:
            {
                LibraDexIdentityExecutionPlan child = PlanNode(RequireLeft(criterion), options, accumulator);
                return new LibraDexIdentityExecutionPlan(
                    criterion,
                    options,
                    LibraDexIdentityPlanKind.Complement,
                    LibraDexIdentityPlanMaterialization.IdentitySet,
                    requiresDistinct: true,
                    requiresOrdering: false,
                    requiresPaging: false,
                    containsNegation: true,
                    child.LeafCount,
                    child.Depth + 1,
                    child.Indexes,
                    new[] { child });
            }

            case LibraDexIdentityCriterionNodeKind.And:
            case LibraDexIdentityCriterionNodeKind.Or:
            case LibraDexIdentityCriterionNodeKind.Except:
            {
                LibraDexIdentityExecutionPlan left = PlanNode(RequireLeft(criterion), options, accumulator);
                LibraDexIdentityExecutionPlan right = PlanNode(RequireRight(criterion), options, accumulator);
                LibraDexIdentityPlanKind kind = criterion.NodeKind switch
                {
                    LibraDexIdentityCriterionNodeKind.And => LibraDexIdentityPlanKind.Intersection,
                    LibraDexIdentityCriterionNodeKind.Or => LibraDexIdentityPlanKind.Union,
                    LibraDexIdentityCriterionNodeKind.Except => LibraDexIdentityPlanKind.Difference,
                    _ => LibraDexIdentityPlanKind.Composite
                };
                LibraDexIdentityPlanMaterialization materialization = SelectCompositeMaterialization(kind, left, right);
                return new LibraDexIdentityExecutionPlan(
                    criterion,
                    options,
                    kind,
                    materialization,
                    requiresDistinct: kind == LibraDexIdentityPlanKind.Union || left.RequiresDistinct || right.RequiresDistinct,
                    requiresOrdering: false,
                    requiresPaging: false,
                    containsNegation: left.ContainsNegation || right.ContainsNegation,
                    left.LeafCount + right.LeafCount,
                    Math.Max(left.Depth, right.Depth) + 1,
                    MergeIndexes(left.Indexes, right.Indexes),
                    new[] { left, right });
            }

            default:
                throw new NotSupportedException($"Identity criterion node {criterion.NodeKind} is not supported by the planner.");
        }
    }

    private static LibraDexIdentityPlanMaterialization SelectCompositeMaterialization(
        LibraDexIdentityPlanKind kind,
        LibraDexIdentityExecutionPlan left,
        LibraDexIdentityExecutionPlan right)
    {
        if (left.ContainsNegation || right.ContainsNegation)
        {
            return LibraDexIdentityPlanMaterialization.IdentitySet;
        }

        if (kind == LibraDexIdentityPlanKind.Union)
        {
            return LibraDexIdentityPlanMaterialization.IdentitySet;
        }

        return MergeMaterialization(
            LibraDexIdentityPlanMaterialization.StreamingMerge,
            MergeMaterialization(left.Materialization, right.Materialization));
    }

    private static LibraDexIdentityPlanMaterialization MergeMaterialization(
        LibraDexIdentityPlanMaterialization left,
        LibraDexIdentityPlanMaterialization right)
    {
        return (LibraDexIdentityPlanMaterialization)Math.Max((int)left, (int)right);
    }

    private static IIdentityCriterion RequireLeft(IIdentityCriterion criterion)
    {
        return criterion.Left ?? throw new InvalidOperationException("Identity criterion is missing its left child.");
    }

    private static IIdentityCriterion RequireRight(IIdentityCriterion criterion)
    {
        return criterion.Right ?? throw new InvalidOperationException("Identity criterion is missing its right child.");
    }

    private static IReadOnlyList<IIndex> MergeIndexes(IReadOnlyList<IIndex> left, IReadOnlyList<IIndex> right)
    {
        List<IIndex> indexes = new(left.Count + right.Count);
        AddDistinct(indexes, left);
        AddDistinct(indexes, right);
        return indexes;
    }

    private static void AddDistinct(List<IIndex> target, IReadOnlyList<IIndex> source)
    {
        for (int i = 0; i < source.Count; i++)
        {
            if (!target.Contains(source[i]))
            {
                target.Add(source[i]);
            }
        }
    }

    private sealed class PlanAccumulator
    {
        private readonly List<IIndex> indexes = new();

        internal IReadOnlyList<IIndex> Indexes => indexes;

        internal void Add(IIndex index)
        {
            if (!indexes.Contains(index))
            {
                indexes.Add(index);
            }
        }
    }
}

internal sealed class LibraDexIdentityCriterionMutationBuilder : IIdentityCriterionMutationBuilder
{
    private readonly IIdentityCriterion criterion;

    internal LibraDexIdentityCriterionMutationBuilder(IIdentityCriterion criterion)
    {
        this.criterion = criterion;
    }

    public IIdentityCriterionMutation Delete()
    {
        return new LibraDexIdentityCriterionMutation(criterion, LibraDexCriteriaMutationKind.Delete, newKey: null, newKeyFactory: null);
    }

    public IIdentityCriterionMutation SetKey(object newKey)
    {
        ArgumentNullException.ThrowIfNull(newKey);
        return new LibraDexIdentityCriterionMutation(criterion, LibraDexCriteriaMutationKind.SetKey, newKey, newKeyFactory: null);
    }

    public IIdentityCriterionMutation SetKey(Func<object, object?> newKeyFactory)
    {
        ArgumentNullException.ThrowIfNull(newKeyFactory);
        return new LibraDexIdentityCriterionMutation(criterion, LibraDexCriteriaMutationKind.SetKey, newKey: null, newKeyFactory);
    }
}

internal sealed class LibraDexIdentityCriterionMutation : IIdentityCriterionMutation
{
    internal LibraDexIdentityCriterionMutation(
        IIdentityCriterion criterion,
        LibraDexCriteriaMutationKind kind,
        object? newKey,
        Func<object, object?>? newKeyFactory)
    {
        Criterion = criterion ?? throw new ArgumentNullException(nameof(criterion));
        Kind = kind;
        NewKey = newKey;
        NewKeyFactory = newKeyFactory;
    }

    public IIdentityCriterion Criterion { get; }

    public LibraDexCriteriaMutationKind Kind { get; }

    public object? NewKey { get; }

    public Func<object, object?>? NewKeyFactory { get; }
}

/// <summary>
/// Carries opaque continuation information for a public query.<br/>
/// The first scaffold stores only public shape metadata; future implementations can add route, shelf, generation, and tie-breaker state without changing query method names.<br/>
/// </summary>
public readonly record struct LibraDexBookmark(long Generation, long Position);

/// <summary>
/// Represents the materialized result of a programmatic identity criteria projection.<br/>
/// This keeps execution metadata beside the returned identities so generated callers can inspect plan shape without rerunning planning separately.<br/>
/// </summary>
public sealed class LibraDexIdentityExecutionResult
{
    internal LibraDexIdentityExecutionResult(
        IReadOnlyList<object> identities,
        LibraDexIdentityExecutionPlan plan,
        LibraDexQueryDiagnostics diagnostics)
    {
        Identities = identities ?? throw new ArgumentNullException(nameof(identities));
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        Diagnostics = diagnostics;
    }

    /// <summary>
    /// Gets the materialized identity values.<br/>
    /// </summary>
    public IReadOnlyList<object> Identities { get; }

    /// <summary>
    /// Gets the identity execution plan used for this result.<br/>
    /// </summary>
    public LibraDexIdentityExecutionPlan Plan { get; }

    /// <summary>
    /// Gets execution diagnostics for this materialized result.<br/>
    /// </summary>
    public LibraDexQueryDiagnostics Diagnostics { get; }
}

/// <summary>
/// Describes public execution metadata for a query or reader.<br/>
/// Diagnostics are not intended to become a required explain-plan workflow, but they let callers verify whether LibraDex used the expected route when performance matters.<br/>
/// </summary>
/// <param name="ExecutionKind">The broad execution route selected for the query.</param>
/// <param name="ProjectionName">The maintained projection or sub-index used by the query, when known.</param>
/// <param name="RowsScanned">The number of rows scanned, when the implementation records it.</param>
/// <param name="RowsReturned">The number of rows returned, when the implementation records it.</param>
public readonly record struct LibraDexQueryDiagnostics(
    LibraDexExecutionKind ExecutionKind,
    string? ProjectionName = null,
    long RowsScanned = 0,
    long RowsReturned = 0);

/// <summary>
/// Describes a planned identity criteria projection.<br/>
/// The descriptor is intentionally compact: it classifies the boolean tree, result options, streamability, and the amount of index participation before physical execution is connected.<br/>
/// </summary>
public sealed class LibraDexIdentityExecutionPlan
{
    internal LibraDexIdentityExecutionPlan(
        IIdentityCriterion criterion,
        LibraDexIdentityQueryOptions options,
        LibraDexIdentityPlanKind kind,
        LibraDexIdentityPlanMaterialization materialization,
        bool requiresDistinct,
        bool requiresOrdering,
        bool requiresPaging,
        bool containsNegation,
        int leafCount,
        int depth,
        IReadOnlyList<IIndex> indexes,
        IReadOnlyList<LibraDexIdentityExecutionPlan> children)
    {
        Criterion = criterion;
        Options = options;
        Kind = kind;
        Materialization = materialization;
        RequiresDistinct = requiresDistinct;
        RequiresOrdering = requiresOrdering;
        RequiresPaging = requiresPaging;
        ContainsNegation = containsNegation;
        LeafCount = leafCount;
        Depth = depth;
        Indexes = indexes;
        Children = children;
    }

    /// <summary>
    /// Gets the criteria tree being planned.<br/>
    /// </summary>
    public IIdentityCriterion Criterion { get; }

    /// <summary>
    /// Gets the identity projection options used by the plan.<br/>
    /// </summary>
    public LibraDexIdentityQueryOptions Options { get; }

    /// <summary>
    /// Gets the broad plan shape.<br/>
    /// </summary>
    public LibraDexIdentityPlanKind Kind { get; }

    /// <summary>
    /// Gets the expected intermediate materialization requirement.<br/>
    /// </summary>
    public LibraDexIdentityPlanMaterialization Materialization { get; }

    /// <summary>
    /// Gets whether duplicate-identity elimination is required by result options or plan shape.<br/>
    /// </summary>
    public bool RequiresDistinct { get; }

    /// <summary>
    /// Gets whether explicit identity ordering is required by result options.<br/>
    /// </summary>
    public bool RequiresOrdering { get; }

    /// <summary>
    /// Gets whether skip, take, or bookmark continuation must be applied.<br/>
    /// </summary>
    public bool RequiresPaging { get; }

    /// <summary>
    /// Gets whether the criteria tree contains a negation node.<br/>
    /// </summary>
    public bool ContainsNegation { get; }

    /// <summary>
    /// Gets the number of index-backed leaf criteria in the tree.<br/>
    /// </summary>
    public int LeafCount { get; }

    /// <summary>
    /// Gets the maximum criteria-tree depth.<br/>
    /// </summary>
    public int Depth { get; }

    /// <summary>
    /// Gets participating indexes in first-seen order.<br/>
    /// </summary>
    public IReadOnlyList<IIndex> Indexes { get; }

    /// <summary>
    /// Gets child plans for composite criteria.<br/>
    /// </summary>
    public IReadOnlyList<LibraDexIdentityExecutionPlan> Children { get; }
}

/// <summary>
/// Captures execution-shaping options for identity-only criteria projections.<br/>
/// Multi-index criteria normally return identities, not keys or tuples, so ordering and duplicate policy must be stated independently from any one index's key order.<br/>
/// </summary>
/// <param name="Ordering">The requested identity ordering contract.</param>
/// <param name="Deduplication">The requested duplicate identity policy.</param>
/// <param name="SkipCount">The number of matching identities to skip after criteria and duplicate policy are applied.</param>
/// <param name="TakeCount">The optional maximum number of identities to return.</param>
/// <param name="Bookmark">The optional continuation bookmark supplied by the caller.</param>
public readonly record struct LibraDexIdentityQueryOptions(
    IdentityResultOrdering Ordering = IdentityResultOrdering.PlanNatural,
    IdentityDeduplication Deduplication = IdentityDeduplication.Distinct,
    int SkipCount = 0,
    int? TakeCount = null,
    LibraDexBookmark? Bookmark = null)
{
    /// <summary>
    /// Gets the default identity criteria projection options.<br/>
    /// Defaults favor object lookup semantics: natural plan order, distinct identities, no skip, no take limit, and no bookmark.<br/>
    /// </summary>
    public static LibraDexIdentityQueryOptions Default { get; } = new();

    /// <summary>
    /// Validates the supplied skip and take counts.<br/>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when skip or take is negative.</exception>
    public void Validate()
    {
        if (SkipCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SkipCount), SkipCount, "Skip cannot be negative.");
        }

        if (TakeCount is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(TakeCount), TakeCount, "Take cannot be negative.");
        }

        if (Bookmark is { Position: < 0 })
        {
            throw new ArgumentOutOfRangeException(nameof(Bookmark), Bookmark, "Bookmark position cannot be negative.");
        }
    }
}

/// <summary>
/// Represents a strict non-generic prepared key-membership set for programmatic callers.<br/>
/// The descriptor validates runtime values against one key type and preserves `InSet` intent without forcing generated adapters to carry generic type arguments.<br/>
/// </summary>
public sealed class LibraDexPreparedObjectSet
{
    /// <summary>
    /// Creates a non-generic prepared key-membership descriptor.<br/>
    /// </summary>
    /// <param name="keyType">The runtime key type accepted by the owning index.</param>
    /// <param name="values">The prepared runtime key values.</param>
    public LibraDexPreparedObjectSet(Type keyType, IReadOnlyList<object> values)
    {
        KeyType = keyType ?? throw new ArgumentNullException(nameof(keyType));
        Values = values ?? throw new ArgumentNullException(nameof(values));
    }

    /// <summary>
    /// Gets the runtime key type accepted by this prepared set.<br/>
    /// </summary>
    public Type KeyType { get; }

    /// <summary>
    /// Gets the prepared runtime key values.<br/>
    /// The first scaffold keeps CLR values; later implementations can attach encoded membership state behind this descriptor.<br/>
    /// </summary>
    public IReadOnlyList<object> Values { get; }
}

/// <summary>
/// Identifies a criteria-scoped mutation intent.<br/>
/// These descriptors are not transactions; they describe explicit mutation work that a future mutable cursor or executor can apply while positioned on matching identities.<br/>
/// </summary>
public enum LibraDexCriteriaMutationKind
{
    /// <summary>
    /// Delete matching key/identity tuples from the index.<br/>
    /// </summary>
    Delete = 0,

    /// <summary>
    /// Replace matching tuples with a caller-supplied key value or key factory.<br/>
    /// </summary>
    SetKey = 1
}

/// <summary>
/// Describes a mutation requested through a criteria context.<br/>
/// The descriptor exists so generated callers can bind "find then mutate" as one inspectable plan without executing immediately.<br/>
/// </summary>
public interface IIdentityCriterionMutation
{
    /// <summary>
    /// Gets the criterion whose matching identities would be mutated.<br/>
    /// </summary>
    IIdentityCriterion Criterion { get; }

    /// <summary>
    /// Gets the mutation kind requested by the caller.<br/>
    /// </summary>
    LibraDexCriteriaMutationKind Kind { get; }

    /// <summary>
    /// Gets the static replacement key when one was supplied.<br/>
    /// </summary>
    object? NewKey { get; }

    /// <summary>
    /// Gets the replacement-key factory when one was supplied.<br/>
    /// The factory receives the current identity object and should return the replacement key for that identity.<br/>
    /// </summary>
    Func<object, object?>? NewKeyFactory { get; }
}

/// <summary>
/// Builds criteria-scoped mutation descriptors.<br/>
/// This keeps update/delete syntax tied to retrieval context while avoiding immediate physical mutation until mutable cursor semantics are connected.<br/>
/// </summary>
public interface IIdentityCriterionMutationBuilder
{
    /// <summary>
    /// Captures delete intent for identities matched by the criterion.<br/>
    /// </summary>
    /// <returns>A mutation descriptor.</returns>
    IIdentityCriterionMutation Delete();

    /// <summary>
    /// Captures set-key intent with one static replacement key.<br/>
    /// </summary>
    /// <param name="newKey">The replacement key value.</param>
    /// <returns>A mutation descriptor.</returns>
    IIdentityCriterionMutation SetKey(object newKey);

    /// <summary>
    /// Captures set-key intent with a replacement-key factory.<br/>
    /// </summary>
    /// <param name="newKeyFactory">Factory that receives a matched identity and returns its replacement key.</param>
    /// <returns>A mutation descriptor.</returns>
    IIdentityCriterionMutation SetKey(Func<object, object?> newKeyFactory);
}

/// <summary>
/// Represents a prepared key-membership set for repeated `InSet` queries.<br/>
/// Prepared sets let LibraDex pre-encode and organize lookup values once instead of rebuilding membership state for every call.<br/>
/// </summary>
/// <typeparam name="TKey">The public key type.</typeparam>
public sealed class LibraDexPreparedSet<TKey>
{
    private readonly IReadOnlyCollection<TKey> keys;

    /// <summary>
    /// Creates a prepared key set from an existing collection.<br/>
    /// The first scaffold preserves values as CLR keys; later implementations can add encoded lanes and hash/sort layouts without changing call sites.<br/>
    /// </summary>
    /// <param name="keys">The keys to include in the prepared membership set.</param>
    public LibraDexPreparedSet(IReadOnlyCollection<TKey> keys)
    {
        this.keys = keys ?? throw new ArgumentNullException(nameof(keys));
    }

    /// <summary>
    /// Gets the number of keys in the prepared set.<br/>
    /// </summary>
    public int Count => keys.Count;

    /// <summary>
    /// Gets the CLR key values captured by the prepared set.<br/>
    /// This keeps the first connected implementation honest and executable while leaving room for encoded or sorted prepared-set internals later.<br/>
    /// </summary>
    public IEnumerable<TKey> Values => keys;
}

/// <summary>
/// Represents a prepared grouping specification for repeated grouped queries.<br/>
/// Prepared grouping specs let LibraDex pre-resolve projection, bucket, or part-selection intent without turning public queries into SQL text parsing.<br/>
/// </summary>
/// <typeparam name="TKey">The public key type.</typeparam>
public sealed class LibraDexPreparedGrouping<TKey>
{
    internal LibraDexPreparedGrouping(string description)
    {
        Description = description;
    }

    /// <summary>
    /// Gets a human-readable description of the prepared grouping projection.<br/>
    /// This is diagnostics metadata for the scaffold; connected implementations can add encoded projection state later.<br/>
    /// </summary>
    public string Description { get; }
}

/// <summary>
/// Represents a reusable stats marker, similar to a trip-counter marker in a car.<br/>
/// Markers are compared against later stats snapshots to produce deltas without requiring the caller to maintain every counter by hand.<br/>
/// </summary>
public readonly record struct LibraDexStatsMarker(long Sequence);

/// <summary>
/// Represents a delta between a stats marker and a later observation point.<br/>
/// This scaffold uses public counter names now; implementation-specific counters can be added when the stats backplane is connected.<br/>
/// </summary>
public readonly record struct LibraDexStatsDelta(
    long Inserts,
    long Deletes,
    long Rekeys,
    long Commits,
    long BytesWritten,
    long ShelfSplits,
    long RouteChanges);

/// <summary>
/// Represents a snapshot of index physical layout health.<br/>
/// Expensive layout metrics should be gathered through explicit snapshot calls rather than hidden work on cheap property reads.<br/>
/// </summary>
public readonly record struct LibraDexLayoutStats(
    int RouterCount,
    int ShelfCount,
    double AverageShelfDensity,
    double MinimumShelfDensity,
    double MaximumShelfDensity,
    long ReusableBytes);

/// <summary>
/// Identifies a maintenance operation requested through the public maintenance facade.<br/>
/// Maintenance is explicit so normal query and mutation calls do not hide optimization or validation work.<br/>
/// </summary>
public enum LibraDexMaintenanceOperation
{
    /// <summary>
    /// Validates catalog or index structure without changing persisted state.<br/>
    /// </summary>
    Validate = 0,

    /// <summary>
    /// Optimizes route or shelf layout according to the connected physical shape's policy.<br/>
    /// </summary>
    Optimize = 1,

    /// <summary>
    /// Re-packs shelves or reusable regions when the connected shape supports it.<br/>
    /// </summary>
    Repack = 2,

    /// <summary>
    /// Updates cache policy or warms runtime state without changing persisted index contents.<br/>
    /// </summary>
    Cache = 3
}

/// <summary>
/// Selects how aggressively a maintenance operation may run.<br/>
/// The enum avoids boolean knobs and gives later implementations room to separate hot-path-safe checks from explicit cold work.<br/>
/// </summary>
public enum LibraDexMaintenanceMode
{
    /// <summary>
    /// Performs only cheap checks or work that should be acceptable between ordinary application operations.<br/>
    /// </summary>
    Light = 0,

    /// <summary>
    /// Performs bounded work that may touch multiple routes or shelves but should still remain application-controlled.<br/>
    /// </summary>
    Bounded = 1,

    /// <summary>
    /// Performs the full requested operation and may be expensive.<br/>
    /// </summary>
    Full = 2
}

/// <summary>
/// Represents public options for a maintenance operation.<br/>
/// Options are intentionally small in the scaffold; shape-specific maintenance should add explicit members only when concrete behavior exists.<br/>
/// </summary>
public sealed class LibraDexMaintenanceOptions
{
    /// <summary>
    /// Gets or initializes the requested maintenance mode.<br/>
    /// Bounded is the default because it keeps explicit maintenance useful without implying unbounded cold optimization by accident.<br/>
    /// </summary>
    public LibraDexMaintenanceMode Mode { get; init; } = LibraDexMaintenanceMode.Bounded;

    /// <summary>
    /// Gets or initializes the maximum number of candidates, routes, shelves, or other work units the operation should consider when applicable.<br/>
    /// Null means the connected operation can use its own mode-specific default.<br/>
    /// </summary>
    public int? MaxWorkItems { get; init; }
}

/// <summary>
/// Represents the result of a maintenance operation.<br/>
/// The first scaffold reports operation intent; connected implementations can populate work and change counters without changing public call sites.<br/>
/// </summary>
public readonly record struct LibraDexMaintenanceResult(
    LibraDexMaintenanceOperation Operation,
    LibraDexMaintenanceMode Mode,
    bool Completed,
    int ConsideredCount,
    int ChangedCount,
    string Message);

/// <summary>
/// Identifies a catalog-level utility operation.<br/>
/// Tools are not hot-path APIs; they exist for inspection, import/export, repair, and developer support workflows.<br/>
/// </summary>
public enum LibraDexToolOperation
{
    /// <summary>
    /// Inspects a catalog format or physical layout summary.<br/>
    /// </summary>
    InspectFormat = 0,

    /// <summary>
    /// Exports a metadata or diagnostic summary.<br/>
    /// </summary>
    ExportSummary = 1,

    /// <summary>
    /// Attempts a repair-oriented operation.<br/>
    /// </summary>
    Repair = 2
}

/// <summary>
/// Represents the result of a catalog utility operation.<br/>
/// Tool results are descriptor-style in the scaffold and become richer once import/export/repair behavior is connected.<br/>
/// </summary>
public readonly record struct LibraDexToolResult(
    LibraDexToolOperation Operation,
    bool Completed,
    string Message);

/// <summary>
/// Identifies compatibility status for a catalog or index profile.<br/>
/// </summary>
public enum LibraDexCompatibilityStatus
{
    /// <summary>
    /// Compatibility has not been checked by a connected implementation.<br/>
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// The catalog or profile is compatible with the current runtime.<br/>
    /// </summary>
    Compatible = 1,

    /// <summary>
    /// The catalog or profile requires migration before normal use.<br/>
    /// </summary>
    RequiresMigration = 2,

    /// <summary>
    /// The catalog or profile is not supported by the current runtime.<br/>
    /// </summary>
    Unsupported = 3
}

/// <summary>
/// Represents compatibility information for a catalog or index profile.<br/>
/// </summary>
public readonly record struct LibraDexCompatibilityReport(
    LibraDexCompatibilityStatus Status,
    int FormatVersion,
    string Message);

/// <summary>
/// Represents public options for migration operations.<br/>
/// Migration should always be explicit because it may rewrite metadata or physical layout.<br/>
/// </summary>
public sealed class LibraDexMigrationOptions
{
    /// <summary>
    /// Gets or initializes whether migration may rewrite the catalog file in place.<br/>
    /// False means the connected implementation must refuse operations that cannot be completed without mutation.<br/>
    /// </summary>
    public bool AllowInPlaceRewrite { get; init; }
}

/// <summary>
/// Represents the common public query shape for tuple-returning retrieval.<br/>
/// The object is intentionally lightweight: it captures caller intent and opens the concrete reader only when requested.<br/>
/// </summary>
/// <typeparam name="TKey">The public key type.</typeparam>
/// <typeparam name="TIdentity">The public identity type.</typeparam>
public sealed class LibraDexQuery<TKey, TIdentity>
{
    private readonly LibraDexIndex<TKey, TIdentity> index;
    private readonly TKey lowerKey;
    private readonly TKey upperKey;
    private LibraDexProjectedQuery<TKey, TIdentity, TKey>? keys;
    private LibraDexProjectedQuery<TKey, TIdentity, TIdentity>? ids;

    internal LibraDexQuery(
        LibraDexIndex<TKey, TIdentity> index,
        TKey lowerKey,
        TKey upperKey,
        QueryDirection direction,
        int skip,
        int? take,
        RetrievalScope scope,
        LibraDexBookmark? bookmark,
        LibraDexExecutionKind executionKind)
    {
        this.index = index;
        this.lowerKey = lowerKey;
        this.upperKey = upperKey;
        Direction = direction;
        SkipCount = skip;
        TakeCount = take;
        Scope = scope;
        Bookmark = bookmark;
        Diagnostics = new LibraDexQueryDiagnostics(executionKind);
    }

    /// <summary>
    /// Gets the requested traversal direction.<br/>
    /// The current scaffold records the intent; concrete reverse traversal will be connected shape by shape.<br/>
    /// </summary>
    public QueryDirection Direction { get; }

    /// <summary>
    /// Gets the number of matching rows the caller asked to skip before returning entries.<br/>
    /// Large offset-style skips may be less efficient than bookmark continuation until route/shelf counts are connected.<br/>
    /// </summary>
    public int SkipCount { get; }

    /// <summary>
    /// Gets the optional maximum number of rows the caller asked to return.<br/>
    /// A null value means the query is unbounded after criteria and skip are applied.<br/>
    /// </summary>
    public int? TakeCount { get; }

    /// <summary>
    /// Gets duplicate-key retrieval semantics requested by the caller.<br/>
    /// Tuple scope preserves all matching key/identity rows; distinct scopes select one representative identity per key.<br/>
    /// </summary>
    public RetrievalScope Scope { get; }

    /// <summary>
    /// Gets the optional bookmark supplied by the caller.<br/>
    /// Bookmark-aware start positioning is reserved for the concrete reader work; the public query shape records it now.<br/>
    /// </summary>
    public LibraDexBookmark? Bookmark { get; }

    /// <summary>
    /// Gets public execution metadata for the query.<br/>
    /// This first scaffold records expected execution class; later readers can update row and shelf counters as work happens.<br/>
    /// </summary>
    public LibraDexQueryDiagnostics Diagnostics { get; }

    /// <summary>
    /// Gets this tuple-returning query descriptor.<br/>
    /// The property exists so criteria-first call sites can choose a terminal shape consistently: `index.Between(a, b).Tuples`, `.Keys`, or `.IDs`.<br/>
    /// </summary>
    public LibraDexQuery<TKey, TIdentity> Tuples => this;

    /// <summary>
    /// Gets a key-only projection over this captured key criterion.<br/>
    /// Criteria remain applied to keys; the terminal projection only changes the returned result shape, so `index.Prefix(value).Keys` means keys whose indexed key matches the prefix criterion.<br/>
    /// </summary>
    public LibraDexProjectedQuery<TKey, TIdentity, TKey> Keys => keys ??= new LibraDexProjectedQuery<TKey, TIdentity, TKey>(this, LibraDexProjectionKind.Keys);

    /// <summary>
    /// Gets an identity-only projection over this captured key criterion.<br/>
    /// Criteria remain applied to keys; the terminal projection only changes the returned result shape, so `index.Prefix(value).IDs` means identities whose indexed key matches the prefix criterion.<br/>
    /// </summary>
    public LibraDexProjectedQuery<TKey, TIdentity, TIdentity> IDs => ids ??= new LibraDexProjectedQuery<TKey, TIdentity, TIdentity>(this, LibraDexProjectionKind.Identities);

    /// <summary>
    /// Opens a cursor for the captured query.<br/>
    /// The current implementation supports ascending tuple scope over inclusive ranges; other captured options are surfaced now and connected incrementally by physical shape.<br/>
    /// </summary>
    /// <returns>A disposable cursor positioned before the first matching tuple.</returns>
    public LibraDexRangeReader<TKey, TIdentity> OpenCursor()
    {
        if (Direction != QueryDirection.Ascending)
        {
            throw new NotSupportedException("Descending query traversal is part of the public API scaffold but is not connected to this physical reader yet.");
        }

        if (Scope != RetrievalScope.Tuples)
        {
            throw new NotSupportedException("Distinct-key retrieval scopes are part of the public API scaffold but are not connected to this physical reader yet.");
        }

        LibraDexRangeReader<TKey, TIdentity> reader = index.OpenRangeReader(lowerKey, upperKey);
        if (SkipCount > 0)
        {
            _ = reader.Skip(SkipCount);
        }

        reader.ApplyTakeLimit(TakeCount);
        return reader;
    }

    /// <summary>
    /// Streams tuple results for this inclusive range query through the connected range reader.<br/>
    /// This gives direct handwritten range queries a non-materializing path while keeping cursor access available for callers that want explicit reader control.<br/>
    /// Projection-specific helpers adapt this same tuple stream until dedicated key-only or identity-only readers are connected.<br/>
    /// </summary>
    /// <returns>A forward-only tuple sequence.</returns>
    public IEnumerable<LibraDexTuple<TKey, TIdentity>> Iterate()
    {
        if (Direction == QueryDirection.Descending)
        {
            if (Scope != RetrievalScope.Tuples)
            {
                throw new NotSupportedException("Distinct-key retrieval scopes are part of the public API scaffold but are not connected to this physical reader yet.");
            }

            List<LibraDexTuple<TKey, TIdentity>> rows = new();
            using (LibraDexRangeReader<TKey, TIdentity> descendingReader = index.OpenRangeReader(lowerKey, upperKey))
            {
                while (descendingReader.TryReadNext(out TKey key, out TIdentity identity))
                {
                    rows.Add(new LibraDexTuple<TKey, TIdentity>(key, identity));
                }
            }

            rows.Reverse();
            IEnumerable<LibraDexTuple<TKey, TIdentity>> shaped = rows;
            if (SkipCount > 0)
            {
                shaped = shaped.Skip(SkipCount);
            }

            if (TakeCount is not null)
            {
                shaped = shaped.Take(TakeCount.Value);
            }

            foreach (LibraDexTuple<TKey, TIdentity> tuple in shaped)
            {
                yield return tuple;
            }

            yield break;
        }

        using LibraDexRangeReader<TKey, TIdentity> reader = OpenCursor();
        while (reader.TryReadNext(out TKey key, out TIdentity identity))
        {
            yield return new LibraDexTuple<TKey, TIdentity>(key, identity);
        }
    }

    /// <summary>
    /// Materializes tuple results for this inclusive range query.<br/>
    /// This is the low-friction direct retrieval helper behind call sites such as `index.Between(a, b).ToList()`.<br/>
    /// Large composed or reusable queries should use condition descriptors so planning remains explicit.<br/>
    /// </summary>
    /// <returns>The materialized tuple results.</returns>
    public IReadOnlyList<LibraDexTuple<TKey, TIdentity>> ToList()
    {
        return Iterate().ToList();
    }

    /// <summary>
    /// Captures a union over this query and another query descriptor.<br/>
    /// Physical execution should prefer ordered stream merge when both descriptors are compatible.<br/>
    /// </summary>
    /// <param name="other">The right-side query descriptor.</param>
    /// <returns>A set-operation descriptor.</returns>
    public LibraDexSetQuery<TKey, TIdentity> Union(LibraDexQuery<TKey, TIdentity> other)
    {
        return new LibraDexSetQuery<TKey, TIdentity>(LibraDexSetOperationKind.Union, this, other);
    }

    /// <summary>
    /// Captures an intersection over this query and another query descriptor.<br/>
    /// Physical execution should prefer streaming intersection over materializing both sides.<br/>
    /// </summary>
    /// <param name="other">The right-side query descriptor.</param>
    /// <returns>A set-operation descriptor.</returns>
    public LibraDexSetQuery<TKey, TIdentity> Intersect(LibraDexQuery<TKey, TIdentity> other)
    {
        return new LibraDexSetQuery<TKey, TIdentity>(LibraDexSetOperationKind.Intersect, this, other);
    }

    /// <summary>
    /// Captures an exclusion over this query and another query descriptor.<br/>
    /// Physical execution should prefer streaming exclusion over row-by-row post-filtering when order is compatible.<br/>
    /// </summary>
    /// <param name="other">The right-side query descriptor.</param>
    /// <returns>A set-operation descriptor.</returns>
    public LibraDexSetQuery<TKey, TIdentity> Except(LibraDexQuery<TKey, TIdentity> other)
    {
        return new LibraDexSetQuery<TKey, TIdentity>(LibraDexSetOperationKind.Except, this, other);
    }

    /// <summary>
    /// Captures a stream-native join between this query and another query descriptor.<br/>
    /// The descriptor records join intent only; physical merge, semi, anti, and lookup join execution is connected later.<br/>
    /// </summary>
    /// <param name="other">The right-side query descriptor.</param>
    /// <param name="kind">The stream join shape requested by the caller.</param>
    /// <returns>A join descriptor.</returns>
    public LibraDexJoinQuery<TKey, TIdentity> Join(LibraDexQuery<TKey, TIdentity> other, LibraDexJoinKind kind = LibraDexJoinKind.Merge)
    {
        return new LibraDexJoinQuery<TKey, TIdentity>(kind, this, other);
    }
}

/// <summary>
/// Represents a named retrieval criterion that is part of the public API but is not yet connected to a physical reader.<br/>
/// The descriptor keeps call sites and diagnostics visible while preventing accidental slow materialization behind an intuitive method name.<br/>
/// </summary>
/// <typeparam name="TKey">The public key type.</typeparam>
/// <typeparam name="TIdentity">The public identity type.</typeparam>
public sealed class LibraDexCriteriaQuery<TKey, TIdentity>
{
    private readonly LibraDexIndex<TKey, TIdentity> index;
    private readonly TKey key;
    private readonly bool hasKey;
    private readonly object? operand;
    private readonly bool hasOperand;
    private LibraDexProjectedCriteriaQuery<TKey, TIdentity, TKey>? keys;
    private LibraDexProjectedCriteriaQuery<TKey, TIdentity, TIdentity>? ids;

    internal LibraDexCriteriaQuery(
        LibraDexIndex<TKey, TIdentity> index,
        LibraDexCriteriaKind criteriaKind,
        QueryDirection direction,
        int skip,
        int? take,
        RetrievalScope scope,
        LibraDexBookmark? bookmark,
        LibraDexExecutionKind executionKind,
        bool isNegated = false,
        TKey key = default!,
        bool hasKey = false,
        object? operand = null,
        bool hasOperand = false)
    {
        this.index = index;
        this.key = key;
        this.hasKey = hasKey;
        this.operand = operand;
        this.hasOperand = hasOperand;
        CriteriaKind = criteriaKind;
        Direction = direction;
        SkipCount = skip;
        TakeCount = take;
        Scope = scope;
        Bookmark = bookmark;
        Diagnostics = new LibraDexQueryDiagnostics(executionKind);
        IsNegated = isNegated;
    }

    /// <summary>
    /// Gets the public criterion represented by this descriptor.<br/>
    /// </summary>
    public LibraDexCriteriaKind CriteriaKind { get; }

    /// <summary>
    /// Gets whether this descriptor represents a negated criterion.<br/>
    /// Negated criteria should be implemented through ordered complement planning where possible rather than row-by-row filtering.<br/>
    /// </summary>
    public bool IsNegated { get; }

    /// <summary>
    /// Gets the requested traversal direction.<br/>
    /// </summary>
    public QueryDirection Direction { get; }

    /// <summary>
    /// Gets the requested skip count.<br/>
    /// </summary>
    public int SkipCount { get; }

    /// <summary>
    /// Gets the requested take count, or null when unbounded.<br/>
    /// </summary>
    public int? TakeCount { get; }

    /// <summary>
    /// Gets the duplicate-key retrieval scope.<br/>
    /// </summary>
    public RetrievalScope Scope { get; }

    /// <summary>
    /// Gets optional bookmark state for a future connected reader.<br/>
    /// </summary>
    public LibraDexBookmark? Bookmark { get; }

    /// <summary>
    /// Gets public diagnostics describing the expected execution class.<br/>
    /// </summary>
    public LibraDexQueryDiagnostics Diagnostics { get; }

    /// <summary>
    /// Gets this tuple-returning criteria descriptor.<br/>
    /// The property keeps criteria-first call sites uniform across executable range queries and descriptor-only criteria such as prefix, suffix, contains, and prepared-set membership.<br/>
    /// </summary>
    public LibraDexCriteriaQuery<TKey, TIdentity> Tuples => this;

    /// <summary>
    /// Gets a key-only projection over this captured key criterion.<br/>
    /// The criterion still applies to index keys; this terminal property changes only the result shape requested by the caller.<br/>
    /// </summary>
    public LibraDexProjectedCriteriaQuery<TKey, TIdentity, TKey> Keys => keys ??= new LibraDexProjectedCriteriaQuery<TKey, TIdentity, TKey>(this, LibraDexProjectionKind.Keys);

    /// <summary>
    /// Gets an identity-only projection over this captured key criterion.<br/>
    /// The criterion still applies to index keys; this avoids misleading forms such as `index.IDs.Prefix(value)` where the ID values appear to be prefix-filtered.<br/>
    /// </summary>
    public LibraDexProjectedCriteriaQuery<TKey, TIdentity, TIdentity> IDs => ids ??= new LibraDexProjectedCriteriaQuery<TKey, TIdentity, TIdentity>(this, LibraDexProjectionKind.Identities);

    /// <summary>
    /// Opens a cursor for this criterion when a physical reader has been connected.<br/>
    /// Bounds-backed criteria such as `All`, `Before`, `AtOrBefore`, `After`, and `AtOrAfter` execute through the same ordered range reader as `Between`.<br/>
    /// Projection-backed and scan-backed criteria remain descriptor-only until their physical policy is connected.<br/>
    /// </summary>
    public LibraDexRangeReader<TKey, TIdentity> OpenCursor()
    {
        return index.OpenCriteriaReader(CriteriaKind, key, hasKey, Direction, SkipCount, TakeCount, Scope, IsNegated);
    }

    /// <summary>
    /// Streams tuple results for this named criterion through the connected direct-criteria execution path.<br/>
    /// Range-backed criteria use ordered cursors, while scan-backed and membership criteria route through the same internal primitive semantics used by the condition builder.<br/>
    /// This keeps handwritten direct criteria usable without making direct lookup methods the architectural center of programmatic querying.<br/>
    /// </summary>
    /// <returns>A forward-only tuple sequence.</returns>
    public IEnumerable<LibraDexTuple<TKey, TIdentity>> Iterate()
    {
        return index.IterateCriteriaTuples(CriteriaKind, hasOperand ? operand : key, hasOperand || hasKey, Direction, SkipCount, TakeCount, Scope, IsNegated);
    }

    /// <summary>
    /// Materializes tuple results for this named criterion.<br/>
    /// This is the direct criteria counterpart to condition-builder identity materialization and is intended for simple handwritten retrievals and sanity tooling.<br/>
    /// Large generated or reusable queries should prefer condition descriptors when composing multiple indexes.<br/>
    /// </summary>
    /// <returns>The materialized tuple results.</returns>
    public IReadOnlyList<LibraDexTuple<TKey, TIdentity>> ToList()
    {
        return Iterate().ToList();
    }

    /// <summary>
    /// Captures a union over this criteria descriptor and another criteria descriptor.<br/>
    /// This keeps programmatic criteria composition explicit without SQL text parsing or hidden planner work.<br/>
    /// </summary>
    public LibraDexCriteriaSetQuery<TKey, TIdentity> Union(LibraDexCriteriaQuery<TKey, TIdentity> other)
    {
        return new LibraDexCriteriaSetQuery<TKey, TIdentity>(LibraDexSetOperationKind.Union, this, other);
    }

    /// <summary>
    /// Captures an intersection over this criteria descriptor and another criteria descriptor.<br/>
    /// </summary>
    public LibraDexCriteriaSetQuery<TKey, TIdentity> Intersect(LibraDexCriteriaQuery<TKey, TIdentity> other)
    {
        return new LibraDexCriteriaSetQuery<TKey, TIdentity>(LibraDexSetOperationKind.Intersect, this, other);
    }

    /// <summary>
    /// Captures an exclusion over this criteria descriptor and another criteria descriptor.<br/>
    /// </summary>
    public LibraDexCriteriaSetQuery<TKey, TIdentity> Except(LibraDexCriteriaQuery<TKey, TIdentity> other)
    {
        return new LibraDexCriteriaSetQuery<TKey, TIdentity>(LibraDexSetOperationKind.Except, this, other);
    }
}

/// <summary>
/// Represents a set operation over two executable range-backed query descriptors.<br/>
/// The first scaffold records intent and prevents accidental materialization until stream composition is connected.<br/>
/// </summary>
public sealed class LibraDexSetQuery<TKey, TIdentity>
{
    internal LibraDexSetQuery(LibraDexSetOperationKind operation, LibraDexQuery<TKey, TIdentity> left, LibraDexQuery<TKey, TIdentity> right)
    {
        Operation = operation;
        Left = left ?? throw new ArgumentNullException(nameof(left));
        Right = right ?? throw new ArgumentNullException(nameof(right));
    }

    /// <summary>
    /// Gets the requested set operation.<br/>
    /// </summary>
    public LibraDexSetOperationKind Operation { get; }

    /// <summary>
    /// Gets the left-side query descriptor.<br/>
    /// </summary>
    public LibraDexQuery<TKey, TIdentity> Left { get; }

    /// <summary>
    /// Gets the right-side query descriptor.<br/>
    /// </summary>
    public LibraDexQuery<TKey, TIdentity> Right { get; }

    /// <summary>
    /// Opens a composed set-operation cursor once physical stream composition has been connected.<br/>
    /// </summary>
    public LibraDexRangeReader<TKey, TIdentity> OpenCursor()
    {
        throw new NotSupportedException($"{Operation} is part of the public API scaffold but is not connected to streaming set execution yet.");
    }

    /// <summary>
    /// Streams the set operation over both executable range query streams.<br/>
    /// Union streams left then right while de-duplicating tuples; intersection and exclusion materialize the right side only, then stream the left side.<br/>
    /// This gives direct retrieval callers a lower-heap path before ordered merge cursors are connected.<br/>
    /// </summary>
    /// <returns>A forward-only tuple sequence.</returns>
    public IEnumerable<LibraDexTuple<TKey, TIdentity>> Iterate()
    {
        return LibraDexSetMaterializer<TKey, TIdentity>.Iterate(Operation, Left.Iterate, Right.Iterate);
    }

    /// <summary>
    /// Materializes the set operation over both executable query streams.<br/>
    /// This is intentionally explicit materialization; streaming set cursors remain a separate implementation step so callers can distinguish heap-backed composition from future ordered merge execution.<br/>
    /// </summary>
    /// <returns>The materialized set-operation result in left-stream order where that order is meaningful.</returns>
    public IReadOnlyList<LibraDexTuple<TKey, TIdentity>> ToList()
    {
        return Iterate().ToList();
    }
}

/// <summary>
/// Represents a set operation over two named criteria descriptors.<br/>
/// Criteria set descriptors are useful for generated/programmatic query construction before every criterion has a physical reader.<br/>
/// </summary>
public sealed class LibraDexCriteriaSetQuery<TKey, TIdentity>
{
    internal LibraDexCriteriaSetQuery(LibraDexSetOperationKind operation, LibraDexCriteriaQuery<TKey, TIdentity> left, LibraDexCriteriaQuery<TKey, TIdentity> right)
    {
        Operation = operation;
        Left = left ?? throw new ArgumentNullException(nameof(left));
        Right = right ?? throw new ArgumentNullException(nameof(right));
    }

    /// <summary>
    /// Gets the requested set operation.<br/>
    /// </summary>
    public LibraDexSetOperationKind Operation { get; }

    /// <summary>
    /// Gets the left-side criteria descriptor.<br/>
    /// </summary>
    public LibraDexCriteriaQuery<TKey, TIdentity> Left { get; }

    /// <summary>
    /// Gets the right-side criteria descriptor.<br/>
    /// </summary>
    public LibraDexCriteriaQuery<TKey, TIdentity> Right { get; }

    /// <summary>
    /// Streams the set operation over both executable criteria streams.<br/>
    /// Criteria that are still unsupported by direct tuple iteration throw through their underlying retrieval path rather than silently changing semantics.<br/>
    /// Union streams both sides with de-duplication; intersection and exclusion keep only the right-side tuple set in memory.<br/>
    /// </summary>
    /// <returns>A forward-only tuple sequence.</returns>
    public IEnumerable<LibraDexTuple<TKey, TIdentity>> Iterate()
    {
        return LibraDexSetMaterializer<TKey, TIdentity>.Iterate(Operation, Left.Iterate, Right.Iterate);
    }

    /// <summary>
    /// Materializes the set operation over both executable criteria streams.<br/>
    /// Criteria that are still descriptor-only will throw through their underlying `OpenCursor` path rather than silently scanning.<br/>
    /// </summary>
    /// <returns>The materialized set-operation result in left-stream order where that order is meaningful.</returns>
    public IReadOnlyList<LibraDexTuple<TKey, TIdentity>> ToList()
    {
        return Iterate().ToList();
    }
}

internal static class LibraDexSetMaterializer<TKey, TIdentity>
{
    internal static IEnumerable<LibraDexTuple<TKey, TIdentity>> Iterate(
        LibraDexSetOperationKind operation,
        Func<IEnumerable<LibraDexTuple<TKey, TIdentity>>> openLeft,
        Func<IEnumerable<LibraDexTuple<TKey, TIdentity>>> openRight)
    {
        return operation switch
        {
            LibraDexSetOperationKind.Union => IterateUnion(openLeft(), openRight()),
            LibraDexSetOperationKind.Intersect => IterateIntersection(openLeft(), openRight().ToList()),
            LibraDexSetOperationKind.Except => IterateExcept(openLeft(), openRight().ToList()),
            _ => throw new NotSupportedException($"Set operation {operation} is not supported.")
        };
    }

    internal static IReadOnlyList<LibraDexTuple<TKey, TIdentity>> Materialize(
        LibraDexSetOperationKind operation,
        Func<LibraDexRangeReader<TKey, TIdentity>> openLeft,
        Func<LibraDexRangeReader<TKey, TIdentity>> openRight)
    {
        List<LibraDexTuple<TKey, TIdentity>> left = ReadAll(openLeft);
        List<LibraDexTuple<TKey, TIdentity>> right = ReadAll(openRight);
        HashSet<LibraDexTuple<TKey, TIdentity>> rightSet = new(right);
        return operation switch
        {
            LibraDexSetOperationKind.Union => MaterializeUnion(left, right),
            LibraDexSetOperationKind.Intersect => left.Where(rightSet.Contains).ToArray(),
            LibraDexSetOperationKind.Except => left.Where(tuple => !rightSet.Contains(tuple)).ToArray(),
            _ => throw new NotSupportedException($"Set operation {operation} is not supported.")
        };
    }

    private static List<LibraDexTuple<TKey, TIdentity>> ReadAll(Func<LibraDexRangeReader<TKey, TIdentity>> open)
    {
        using LibraDexRangeReader<TKey, TIdentity> reader = open();
        List<LibraDexTuple<TKey, TIdentity>> rows = new(reader.Count);
        while (reader.TryReadNext(out TKey key, out TIdentity identity))
        {
            rows.Add(new LibraDexTuple<TKey, TIdentity>(key, identity));
        }

        return rows;
    }

    private static IReadOnlyList<LibraDexTuple<TKey, TIdentity>> MaterializeUnion(
        List<LibraDexTuple<TKey, TIdentity>> left,
        List<LibraDexTuple<TKey, TIdentity>> right)
    {
        List<LibraDexTuple<TKey, TIdentity>> result = new(left.Count + right.Count);
        HashSet<LibraDexTuple<TKey, TIdentity>> seen = new();
        foreach (LibraDexTuple<TKey, TIdentity> tuple in left)
        {
            if (seen.Add(tuple))
            {
                result.Add(tuple);
            }
        }

        foreach (LibraDexTuple<TKey, TIdentity> tuple in right)
        {
            if (seen.Add(tuple))
            {
                result.Add(tuple);
            }
        }

        return result;
    }

    private static IEnumerable<LibraDexTuple<TKey, TIdentity>> IterateUnion(
        IEnumerable<LibraDexTuple<TKey, TIdentity>> left,
        IEnumerable<LibraDexTuple<TKey, TIdentity>> right)
    {
        HashSet<LibraDexTuple<TKey, TIdentity>> seen = new();
        foreach (LibraDexTuple<TKey, TIdentity> tuple in left)
        {
            if (seen.Add(tuple))
            {
                yield return tuple;
            }
        }

        foreach (LibraDexTuple<TKey, TIdentity> tuple in right)
        {
            if (seen.Add(tuple))
            {
                yield return tuple;
            }
        }
    }

    private static IEnumerable<LibraDexTuple<TKey, TIdentity>> IterateIntersection(
        IEnumerable<LibraDexTuple<TKey, TIdentity>> left,
        IReadOnlyList<LibraDexTuple<TKey, TIdentity>> right)
    {
        HashSet<LibraDexTuple<TKey, TIdentity>> rightSet = new(right);
        foreach (LibraDexTuple<TKey, TIdentity> tuple in left)
        {
            if (rightSet.Contains(tuple))
            {
                yield return tuple;
            }
        }
    }

    private static IEnumerable<LibraDexTuple<TKey, TIdentity>> IterateExcept(
        IEnumerable<LibraDexTuple<TKey, TIdentity>> left,
        IReadOnlyList<LibraDexTuple<TKey, TIdentity>> right)
    {
        HashSet<LibraDexTuple<TKey, TIdentity>> rightSet = new(right);
        foreach (LibraDexTuple<TKey, TIdentity> tuple in left)
        {
            if (!rightSet.Contains(tuple))
            {
                yield return tuple;
            }
        }
    }
}

/// <summary>
/// Represents a stream-native join over two executable range-backed query descriptors.<br/>
/// Join descriptors are explicit about join shape so LibraDex can later choose merge, semi, anti, or lookup execution without SQL planner syntax.<br/>
/// </summary>
public sealed class LibraDexJoinQuery<TKey, TIdentity>
{
    internal LibraDexJoinQuery(LibraDexJoinKind kind, LibraDexQuery<TKey, TIdentity> left, LibraDexQuery<TKey, TIdentity> right)
    {
        Kind = kind;
        Left = left ?? throw new ArgumentNullException(nameof(left));
        Right = right ?? throw new ArgumentNullException(nameof(right));
    }

    /// <summary>
    /// Gets the requested stream join shape.<br/>
    /// </summary>
    public LibraDexJoinKind Kind { get; }

    /// <summary>
    /// Gets the left-side query descriptor.<br/>
    /// </summary>
    public LibraDexQuery<TKey, TIdentity> Left { get; }

    /// <summary>
    /// Gets the right-side query descriptor.<br/>
    /// </summary>
    public LibraDexQuery<TKey, TIdentity> Right { get; }

    /// <summary>
    /// Opens a joined cursor once the requested join shape has physical execution support.<br/>
    /// </summary>
    public LibraDexRangeReader<TKey, TIdentity> OpenCursor()
    {
        throw new NotSupportedException($"{Kind} join is part of the public API scaffold but is not connected to streaming join execution yet.");
    }
}

/// <summary>
/// Provides key-only retrieval over an index while preserving the same criteria vocabulary as tuple retrieval.<br/>
/// The facade is a public DX scaffold; concrete allocation-free key readers will be connected shape by shape.<br/>
/// </summary>
public sealed class LibraDexKeyProjection<TKey, TIdentity>
{
    private readonly LibraDexIndex<TKey, TIdentity> index;

    internal LibraDexKeyProjection(LibraDexIndex<TKey, TIdentity> index)
    {
        this.index = index;
        First = new LibraDexPositionalFacade<TKey, TIdentity, TKey>(index, LibraDexProjectionKind.Keys, LibraDexPositionKind.First);
        Last = new LibraDexPositionalFacade<TKey, TIdentity, TKey>(index, LibraDexProjectionKind.Keys, LibraDexPositionKind.Last);
        Middle = new LibraDexPositionalFacade<TKey, TIdentity, TKey>(index, LibraDexProjectionKind.Keys, LibraDexPositionKind.Middle);
        Rank = new LibraDexRankFacade<TKey, TIdentity, TKey>(index, LibraDexProjectionKind.Keys, LibraDexPositionKind.Rank);
        PercentRank = new LibraDexPercentRankFacade<TKey, TIdentity, TKey>(index, LibraDexProjectionKind.Keys);
        Grouped = new LibraDexProjectedGroupedFacade<TKey, TIdentity, TKey>(index, LibraDexProjectionKind.Keys);
    }

    /// <summary>
    /// Gets key-only first-position retrieval methods.<br/>
    /// </summary>
    public LibraDexPositionalFacade<TKey, TIdentity, TKey> First { get; }

    /// <summary>
    /// Gets key-only last-position retrieval methods.<br/>
    /// </summary>
    public LibraDexPositionalFacade<TKey, TIdentity, TKey> Last { get; }

    /// <summary>
    /// Gets key-only middle-position retrieval methods.<br/>
    /// </summary>
    public LibraDexPositionalFacade<TKey, TIdentity, TKey> Middle { get; }

    /// <summary>
    /// Gets key-only absolute-rank retrieval methods.<br/>
    /// Rank retrieval returns real indexed entries at ordered positions and is separate from percentile aggregate values.<br/>
    /// </summary>
    public LibraDexRankFacade<TKey, TIdentity, TKey> Rank { get; }

    /// <summary>
    /// Gets key-only percentile-rank retrieval methods.<br/>
    /// Percent-rank retrieval returns real indexed entries near an ordered percentile and is separate from aggregate percentile interpolation.<br/>
    /// </summary>
    public LibraDexPercentRankFacade<TKey, TIdentity, TKey> PercentRank { get; }

    /// <summary>
    /// Gets grouped key-only retrieval operations.<br/>
    /// Grouped keys are especially useful when the grouping key is a projection, such as folded text, and returned keys preserve exact stored forms.<br/>
    /// </summary>
    public LibraDexProjectedGroupedFacade<TKey, TIdentity, TKey> Grouped { get; }

    /// <summary>
    /// Gets grouped key-only retrieval operations.<br/>
    /// `Groups` is the preferred short public spelling; `Grouped` remains available while the scaffold transitions to the shorter naming posture.<br/>
    /// </summary>
    public LibraDexProjectedGroupedFacade<TKey, TIdentity, TKey> Groups => Grouped;

    /// <summary>
    /// Captures a key-only exact lookup query.<br/>
    /// Projection-rich indexes may use one representation for lookup while returning another representation as the key value.<br/>
    /// </summary>
    public LibraDexProjectedQuery<TKey, TIdentity, TKey> Find(TKey key, QueryDirection direction = QueryDirection.Ascending)
    {
        return Between(key, key, direction);
    }

    /// <summary>
    /// Captures a key-only inclusive range query.<br/>
    /// The result shape is keys only, even though the underlying index stores key/identity tuples.<br/>
    /// </summary>
    public LibraDexProjectedQuery<TKey, TIdentity, TKey> Between(TKey lowerKey, TKey upperKey, QueryDirection direction = QueryDirection.Ascending)
    {
        return new LibraDexProjectedQuery<TKey, TIdentity, TKey>(
            index.Between(lowerKey, upperKey, direction),
            LibraDexProjectionKind.Keys);
    }

    /// <summary>
    /// Captures key-only retrieval over all tuples in the index.<br/>
    /// The projected cursor uses the bounds-backed criteria reader and avoids identity decoding when callers read keys only.<br/>
    /// </summary>
    public LibraDexProjectedCriteriaQuery<TKey, TIdentity, TKey> All(QueryDirection direction = QueryDirection.Ascending, int skip = 0, int? take = null)
    {
        return new LibraDexProjectedCriteriaQuery<TKey, TIdentity, TKey>(index.All(direction, skip, take), LibraDexProjectionKind.Keys);
    }

    /// <summary>
    /// Captures key-only retrieval for keys before the supplied boundary key.<br/>
    /// The boundary is exclusive and is translated through the same encoded predecessor logic as tuple criteria execution.<br/>
    /// </summary>
    public LibraDexProjectedCriteriaQuery<TKey, TIdentity, TKey> Before(TKey key, QueryDirection direction = QueryDirection.Ascending)
    {
        return new LibraDexProjectedCriteriaQuery<TKey, TIdentity, TKey>(index.Before(key, direction), LibraDexProjectionKind.Keys);
    }

    /// <summary>
    /// Captures key-only retrieval for keys at or before the supplied boundary key.<br/>
    /// </summary>
    public LibraDexProjectedCriteriaQuery<TKey, TIdentity, TKey> AtOrBefore(TKey key, QueryDirection direction = QueryDirection.Ascending)
    {
        return new LibraDexProjectedCriteriaQuery<TKey, TIdentity, TKey>(index.AtOrBefore(key, direction), LibraDexProjectionKind.Keys);
    }

    /// <summary>
    /// Captures key-only retrieval for keys after the supplied boundary key.<br/>
    /// The boundary is exclusive and is translated through the same encoded successor logic as tuple criteria execution.<br/>
    /// </summary>
    public LibraDexProjectedCriteriaQuery<TKey, TIdentity, TKey> After(TKey key, QueryDirection direction = QueryDirection.Ascending)
    {
        return new LibraDexProjectedCriteriaQuery<TKey, TIdentity, TKey>(index.After(key, direction), LibraDexProjectionKind.Keys);
    }

    /// <summary>
    /// Captures key-only retrieval for keys at or after the supplied boundary key.<br/>
    /// </summary>
    public LibraDexProjectedCriteriaQuery<TKey, TIdentity, TKey> AtOrAfter(TKey key, QueryDirection direction = QueryDirection.Ascending)
    {
        return new LibraDexProjectedCriteriaQuery<TKey, TIdentity, TKey>(index.AtOrAfter(key, direction), LibraDexProjectionKind.Keys);
    }

    /// <summary>
    /// Captures a key-only prefix query.<br/>
    /// This preserves the projection axis for criteria whose physical execution may be projection-backed or scan-backed.<br/>
    /// </summary>
    public LibraDexProjectedCriteriaQuery<TKey, TIdentity, TKey> Prefix(TKey prefix, QueryDirection direction = QueryDirection.Ascending)
    {
        return new LibraDexProjectedCriteriaQuery<TKey, TIdentity, TKey>(index.Prefix(prefix, direction), LibraDexProjectionKind.Keys);
    }

    /// <summary>
    /// Captures a key-only suffix query.<br/>
    /// This is expected to become fast when a reversed projection is maintained for the index.<br/>
    /// </summary>
    public LibraDexProjectedCriteriaQuery<TKey, TIdentity, TKey> Suffix(TKey suffix, QueryDirection direction = QueryDirection.Ascending)
    {
        return new LibraDexProjectedCriteriaQuery<TKey, TIdentity, TKey>(index.Suffix(suffix, direction), LibraDexProjectionKind.Keys);
    }

    /// <summary>
    /// Captures a key-only contains query.<br/>
    /// Contains remains an explicit public intent even when the first physical route is scan-backed.<br/>
    /// </summary>
    public LibraDexProjectedCriteriaQuery<TKey, TIdentity, TKey> Contains(TKey value, QueryDirection direction = QueryDirection.Ascending)
    {
        return new LibraDexProjectedCriteriaQuery<TKey, TIdentity, TKey>(index.Contains(value, direction), LibraDexProjectionKind.Keys);
    }

    /// <summary>
    /// Captures a key-only pattern query.<br/>
    /// Pattern interpretation is owned by the connected key class or higher-level adapter.<br/>
    /// </summary>
    public LibraDexProjectedCriteriaQuery<TKey, TIdentity, TKey> Matches(object pattern, QueryDirection direction = QueryDirection.Ascending)
    {
        return new LibraDexProjectedCriteriaQuery<TKey, TIdentity, TKey>(index.Matches(pattern, direction), LibraDexProjectionKind.Keys);
    }

    /// <summary>
    /// Captures a key-only membership query over ordinary values.<br/>
    /// </summary>
    public LibraDexProjectedCriteriaQuery<TKey, TIdentity, TKey> In(IEnumerable<TKey> keys, QueryDirection direction = QueryDirection.Ascending)
    {
        return new LibraDexProjectedCriteriaQuery<TKey, TIdentity, TKey>(index.In(keys, direction), LibraDexProjectionKind.Keys);
    }

    /// <summary>
    /// Captures a key-only membership query over a prepared set.<br/>
    /// </summary>
    public LibraDexProjectedCriteriaQuery<TKey, TIdentity, TKey> InSet(LibraDexPreparedSet<TKey> set, QueryDirection direction = QueryDirection.Ascending)
    {
        return new LibraDexProjectedCriteriaQuery<TKey, TIdentity, TKey>(index.InSet(set, direction), LibraDexProjectionKind.Keys);
    }
}

/// <summary>
/// Provides prepared helper construction for one index.<br/>
/// Prepared helpers are limited to reusable runtime objects that remove real repeated work, such as encoded membership sets or grouping specs.<br/>
/// </summary>
public sealed class LibraDexPrepareFacade<TKey, TIdentity>
{
    internal LibraDexPrepareFacade(LibraDexIndex<TKey, TIdentity> index)
    {
        Index = index;
    }

    /// <summary>
    /// Gets the index that owns this prepare surface.<br/>
    /// </summary>
    public LibraDexIndex<TKey, TIdentity> Index { get; }

    /// <summary>
    /// Prepares a key-membership set for repeated `InSet` and `ExistsInSet` calls.<br/>
    /// The first scaffold preserves CLR keys; connected implementations can pre-encode and choose hash/sort layouts by physical key class.<br/>
    /// </summary>
    /// <param name="keys">The keys to include in the prepared set.</param>
    /// <returns>A prepared membership set.</returns>
    public LibraDexPreparedSet<TKey> InSet(IEnumerable<TKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return new LibraDexPreparedSet<TKey>(keys as IReadOnlyCollection<TKey> ?? keys.ToArray());
    }

    /// <summary>
    /// Prepares full-key grouping for repeated grouped queries.<br/>
    /// This is useful for generic code that wants to pass grouping specs around rather than hard-code grouped call chains.<br/>
    /// </summary>
    /// <returns>A prepared grouping spec.</returns>
    public LibraDexPreparedGrouping<TKey> GroupByKey()
    {
        return new LibraDexPreparedGrouping<TKey>("Key");
    }

    /// <summary>
    /// Prepares prefix grouping for repeated grouped queries.<br/>
    /// Prefix length is caller-controlled because prefix grouping can be domain-specific and should not be guessed from data.<br/>
    /// </summary>
    /// <param name="prefixLength">The prefix length used by the grouping projection.</param>
    /// <returns>A prepared grouping spec.</returns>
    public LibraDexPreparedGrouping<TKey> GroupByPrefix(int prefixLength)
    {
        if (prefixLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(prefixLength), prefixLength, "Prefix length must be positive.");
        }

        return new LibraDexPreparedGrouping<TKey>($"Prefix({prefixLength})");
    }
}

/// <summary>
/// Provides identity-only retrieval over an index while preserving the same criteria vocabulary as tuple retrieval.<br/>
/// This is the low-allocation default for object lookup flows that do not need to materialize keys from the index.<br/>
/// </summary>
public sealed class LibraDexIdentityProjection<TKey, TIdentity>
{
    private readonly LibraDexIndex<TKey, TIdentity> index;

    internal LibraDexIdentityProjection(LibraDexIndex<TKey, TIdentity> index)
    {
        this.index = index;
        First = new LibraDexPositionalFacade<TKey, TIdentity, TIdentity>(index, LibraDexProjectionKind.Identities, LibraDexPositionKind.First);
        Last = new LibraDexPositionalFacade<TKey, TIdentity, TIdentity>(index, LibraDexProjectionKind.Identities, LibraDexPositionKind.Last);
        Middle = new LibraDexPositionalFacade<TKey, TIdentity, TIdentity>(index, LibraDexProjectionKind.Identities, LibraDexPositionKind.Middle);
        Rank = new LibraDexRankFacade<TKey, TIdentity, TIdentity>(index, LibraDexProjectionKind.Identities, LibraDexPositionKind.Rank);
        PercentRank = new LibraDexPercentRankFacade<TKey, TIdentity, TIdentity>(index, LibraDexProjectionKind.Identities);
        Grouped = new LibraDexProjectedGroupedFacade<TKey, TIdentity, TIdentity>(index, LibraDexProjectionKind.Identities);
    }

    /// <summary>
    /// Gets identity-only first-position retrieval methods.<br/>
    /// </summary>
    public LibraDexPositionalFacade<TKey, TIdentity, TIdentity> First { get; }

    /// <summary>
    /// Gets identity-only last-position retrieval methods.<br/>
    /// </summary>
    public LibraDexPositionalFacade<TKey, TIdentity, TIdentity> Last { get; }

    /// <summary>
    /// Gets identity-only middle-position retrieval methods.<br/>
    /// </summary>
    public LibraDexPositionalFacade<TKey, TIdentity, TIdentity> Middle { get; }

    /// <summary>
    /// Gets identity-only absolute-rank retrieval methods.<br/>
    /// </summary>
    public LibraDexRankFacade<TKey, TIdentity, TIdentity> Rank { get; }

    /// <summary>
    /// Gets identity-only percentile-rank retrieval methods.<br/>
    /// </summary>
    public LibraDexPercentRankFacade<TKey, TIdentity, TIdentity> PercentRank { get; }

    /// <summary>
    /// Gets grouped identity-only retrieval operations.<br/>
    /// This shape lets object lookup code receive group keys with identity lists or streams without reconstructing groups from flattened rows.<br/>
    /// </summary>
    public LibraDexProjectedGroupedFacade<TKey, TIdentity, TIdentity> Grouped { get; }

    /// <summary>
    /// Gets grouped identity-only retrieval operations.<br/>
    /// `Groups` is the preferred short public spelling; `Grouped` remains available while the scaffold transitions to the shorter naming posture.<br/>
    /// </summary>
    public LibraDexProjectedGroupedFacade<TKey, TIdentity, TIdentity> Groups => Grouped;

    /// <summary>
    /// Captures an identity-only exact lookup query.<br/>
    /// This is the common object-lookup path when the caller only needs identities from the index.<br/>
    /// </summary>
    public LibraDexProjectedQuery<TKey, TIdentity, TIdentity> Find(TKey key, QueryDirection direction = QueryDirection.Ascending)
    {
        return Between(key, key, direction);
    }

    /// <summary>
    /// Captures an identity-only inclusive range query.<br/>
    /// The result shape is identities only, even though the underlying index stores key/identity tuples.<br/>
    /// </summary>
    public LibraDexProjectedQuery<TKey, TIdentity, TIdentity> Between(TKey lowerKey, TKey upperKey, QueryDirection direction = QueryDirection.Ascending)
    {
        return new LibraDexProjectedQuery<TKey, TIdentity, TIdentity>(
            index.Between(lowerKey, upperKey, direction),
            LibraDexProjectionKind.Identities);
    }

    /// <summary>
    /// Captures identity-only retrieval over all tuples in the index.<br/>
    /// The projected cursor uses the bounds-backed criteria reader and can stream identities without materializing keys.<br/>
    /// </summary>
    public LibraDexProjectedCriteriaQuery<TKey, TIdentity, TIdentity> All(QueryDirection direction = QueryDirection.Ascending, int skip = 0, int? take = null)
    {
        return new LibraDexProjectedCriteriaQuery<TKey, TIdentity, TIdentity>(index.All(direction, skip, take), LibraDexProjectionKind.Identities);
    }

    /// <summary>
    /// Captures identity-only retrieval for keys before the supplied boundary key.<br/>
    /// The boundary is exclusive and follows the tuple criteria reader's encoded predecessor behavior.<br/>
    /// </summary>
    public LibraDexProjectedCriteriaQuery<TKey, TIdentity, TIdentity> Before(TKey key, QueryDirection direction = QueryDirection.Ascending)
    {
        return new LibraDexProjectedCriteriaQuery<TKey, TIdentity, TIdentity>(index.Before(key, direction), LibraDexProjectionKind.Identities);
    }

    /// <summary>
    /// Captures identity-only retrieval for keys at or before the supplied boundary key.<br/>
    /// </summary>
    public LibraDexProjectedCriteriaQuery<TKey, TIdentity, TIdentity> AtOrBefore(TKey key, QueryDirection direction = QueryDirection.Ascending)
    {
        return new LibraDexProjectedCriteriaQuery<TKey, TIdentity, TIdentity>(index.AtOrBefore(key, direction), LibraDexProjectionKind.Identities);
    }

    /// <summary>
    /// Captures identity-only retrieval for keys after the supplied boundary key.<br/>
    /// The boundary is exclusive and follows the tuple criteria reader's encoded successor behavior.<br/>
    /// </summary>
    public LibraDexProjectedCriteriaQuery<TKey, TIdentity, TIdentity> After(TKey key, QueryDirection direction = QueryDirection.Ascending)
    {
        return new LibraDexProjectedCriteriaQuery<TKey, TIdentity, TIdentity>(index.After(key, direction), LibraDexProjectionKind.Identities);
    }

    /// <summary>
    /// Captures identity-only retrieval for keys at or after the supplied boundary key.<br/>
    /// </summary>
    public LibraDexProjectedCriteriaQuery<TKey, TIdentity, TIdentity> AtOrAfter(TKey key, QueryDirection direction = QueryDirection.Ascending)
    {
        return new LibraDexProjectedCriteriaQuery<TKey, TIdentity, TIdentity>(index.AtOrAfter(key, direction), LibraDexProjectionKind.Identities);
    }

    /// <summary>
    /// Captures an identity-only prefix query.<br/>
    /// </summary>
    public LibraDexProjectedCriteriaQuery<TKey, TIdentity, TIdentity> Prefix(TKey prefix, QueryDirection direction = QueryDirection.Ascending)
    {
        return new LibraDexProjectedCriteriaQuery<TKey, TIdentity, TIdentity>(index.Prefix(prefix, direction), LibraDexProjectionKind.Identities);
    }

    /// <summary>
    /// Captures an identity-only suffix query.<br/>
    /// </summary>
    public LibraDexProjectedCriteriaQuery<TKey, TIdentity, TIdentity> Suffix(TKey suffix, QueryDirection direction = QueryDirection.Ascending)
    {
        return new LibraDexProjectedCriteriaQuery<TKey, TIdentity, TIdentity>(index.Suffix(suffix, direction), LibraDexProjectionKind.Identities);
    }

    /// <summary>
    /// Captures an identity-only contains query.<br/>
    /// </summary>
    public LibraDexProjectedCriteriaQuery<TKey, TIdentity, TIdentity> Contains(TKey value, QueryDirection direction = QueryDirection.Ascending)
    {
        return new LibraDexProjectedCriteriaQuery<TKey, TIdentity, TIdentity>(index.Contains(value, direction), LibraDexProjectionKind.Identities);
    }

    /// <summary>
    /// Captures an identity-only pattern query.<br/>
    /// </summary>
    public LibraDexProjectedCriteriaQuery<TKey, TIdentity, TIdentity> Matches(object pattern, QueryDirection direction = QueryDirection.Ascending)
    {
        return new LibraDexProjectedCriteriaQuery<TKey, TIdentity, TIdentity>(index.Matches(pattern, direction), LibraDexProjectionKind.Identities);
    }

    /// <summary>
    /// Captures an identity-only membership query over ordinary values.<br/>
    /// </summary>
    public LibraDexProjectedCriteriaQuery<TKey, TIdentity, TIdentity> In(IEnumerable<TKey> keys, QueryDirection direction = QueryDirection.Ascending)
    {
        return new LibraDexProjectedCriteriaQuery<TKey, TIdentity, TIdentity>(index.In(keys, direction), LibraDexProjectionKind.Identities);
    }

    /// <summary>
    /// Captures an identity-only membership query over a prepared set.<br/>
    /// </summary>
    public LibraDexProjectedCriteriaQuery<TKey, TIdentity, TIdentity> InSet(LibraDexPreparedSet<TKey> set, QueryDirection direction = QueryDirection.Ascending)
    {
        return new LibraDexProjectedCriteriaQuery<TKey, TIdentity, TIdentity>(index.InSet(set, direction), LibraDexProjectionKind.Identities);
    }
}

/// <summary>
/// Represents a projected query over an underlying tuple query.<br/>
/// This first scaffold preserves the public shape and can open the tuple cursor; projection-specific readers will be connected as hot paths later.<br/>
/// </summary>
public sealed class LibraDexProjectedQuery<TKey, TIdentity, TResult>
{
    private readonly LibraDexQuery<TKey, TIdentity> query;
    private readonly LibraDexProjectionKind projection;

    internal LibraDexProjectedQuery(LibraDexQuery<TKey, TIdentity> query, LibraDexProjectionKind projection)
    {
        this.query = query;
        this.projection = projection;
    }

    /// <summary>
    /// Gets public execution metadata inherited from the underlying tuple query.<br/>
    /// Projection-specific counters can be added when key-only and identity-only readers become physical hot paths.<br/>
    /// </summary>
    public LibraDexQueryDiagnostics Diagnostics => query.Diagnostics;

    /// <summary>
    /// Opens the underlying tuple cursor for projected iteration.<br/>
    /// This keeps the public scaffold usable while dedicated projected cursors are added incrementally.<br/>
    /// </summary>
    public LibraDexRangeReader<TKey, TIdentity> OpenTupleCursor()
    {
        _ = projection;
        return query.OpenCursor();
    }

    /// <summary>
    /// Streams projected values for the underlying inclusive range query.<br/>
    /// The first connected implementation projects from tuple iteration, preserving correctness while dedicated projected readers are added later.<br/>
    /// Criteria still apply to keys; this projection only controls whether keys or identities are returned.<br/>
    /// </summary>
    /// <returns>A forward-only sequence of projected values.</returns>
    public IEnumerable<TResult> Iterate()
    {
        foreach (LibraDexTuple<TKey, TIdentity> tuple in query.Iterate())
        {
            yield return projection switch
            {
                LibraDexProjectionKind.Keys => tuple.Key is TResult key
                    ? key
                    : throw new InvalidCastException($"Projected key type {typeof(TKey).FullName} is not assignable to {typeof(TResult).FullName}."),
                LibraDexProjectionKind.Identities => tuple.Identity is TResult identity
                    ? identity
                    : throw new InvalidCastException($"Projected identity type {typeof(TIdentity).FullName} is not assignable to {typeof(TResult).FullName}."),
                _ => throw new NotSupportedException($"Projected query shape {projection} is not supported by this projection.")
            };
        }
    }

    /// <summary>
    /// Materializes projected values for the underlying inclusive range query.<br/>
    /// This enables low-friction direct call sites such as `index.Between(a, b).Keys.ToList()` and `index.Find(key).IDs.ToList()`.<br/>
    /// The implementation currently adapts tuple iteration so projection-specific hot paths can be added without changing public syntax.<br/>
    /// </summary>
    /// <returns>The materialized projected values.</returns>
    public IReadOnlyList<TResult> ToList()
    {
        return Iterate().ToList();
    }
}

/// <summary>
/// Represents a projected named-criteria query over tuple retrieval intent.<br/>
/// This mirrors `Keys` and `Identities` projection over criteria that are not yet backed by the executable range-query descriptor.<br/>
/// </summary>
public sealed class LibraDexProjectedCriteriaQuery<TKey, TIdentity, TResult>
{
    private readonly LibraDexCriteriaQuery<TKey, TIdentity> query;
    private readonly LibraDexProjectionKind projection;

    internal LibraDexProjectedCriteriaQuery(LibraDexCriteriaQuery<TKey, TIdentity> query, LibraDexProjectionKind projection)
    {
        this.query = query;
        this.projection = projection;
    }

    /// <summary>
    /// Gets the criterion represented by the underlying query descriptor.<br/>
    /// </summary>
    public LibraDexCriteriaKind CriteriaKind => query.CriteriaKind;

    /// <summary>
    /// Gets the requested result projection.<br/>
    /// </summary>
    public LibraDexProjectionKind Projection => projection;

    /// <summary>
    /// Gets whether this projected criterion is negated.<br/>
    /// </summary>
    public bool IsNegated => query.IsNegated;

    /// <summary>
    /// Gets public execution metadata inherited from the underlying criteria query.<br/>
    /// </summary>
    public LibraDexQueryDiagnostics Diagnostics => query.Diagnostics;

    /// <summary>
    /// Opens a projected cursor when the underlying criterion has connected physical execution.<br/>
    /// The first scaffold throws through the underlying criteria descriptor to avoid hidden scan/materialization behavior.<br/>
    /// </summary>
    public LibraDexRangeReader<TKey, TIdentity> OpenTupleCursor()
    {
        return query.OpenCursor();
    }

    /// <summary>
    /// Streams projected values for this named criterion by reading tuple results and projecting the requested side.<br/>
    /// Projection-specific physical cursors can replace this adapter later, but the public shape is executable now for keys and identities.<br/>
    /// The predicate still applies to keys; projection only changes the returned values.<br/>
    /// </summary>
    /// <returns>A forward-only sequence of projected values.</returns>
    public IEnumerable<TResult> Iterate()
    {
        foreach (LibraDexTuple<TKey, TIdentity> tuple in query.Iterate())
        {
            yield return projection switch
            {
                LibraDexProjectionKind.Keys => tuple.Key is TResult key
                    ? key
                    : throw new InvalidCastException($"Projected key type {typeof(TKey).FullName} is not assignable to {typeof(TResult).FullName}."),
                LibraDexProjectionKind.Identities => tuple.Identity is TResult identity
                    ? identity
                    : throw new InvalidCastException($"Projected identity type {typeof(TIdentity).FullName} is not assignable to {typeof(TResult).FullName}."),
                _ => throw new NotSupportedException($"Projected criteria shape {projection} is not supported by this projection.")
            };
        }
    }

    /// <summary>
    /// Materializes projected values for this named criterion.<br/>
    /// This is the low-friction direct retrieval helper behind call sites such as `index.Prefix(value).IDs.ToList()` and `index.Between(a, b).Keys.ToList()`.<br/>
    /// The implementation currently adapts tuple iteration so correctness arrives before projection-specific hot paths.<br/>
    /// </summary>
    /// <returns>The materialized projected values.</returns>
    public IReadOnlyList<TResult> ToList()
    {
        return Iterate().ToList();
    }
}

/// <summary>
/// Provides negated criteria over an index.<br/>
/// The public name avoids boolean negation parameters at call sites and lets diagnostics later describe complement-planning routes explicitly.<br/>
/// </summary>
public sealed class LibraDexNotFacade<TKey, TIdentity>
{
    private readonly LibraDexIndex<TKey, TIdentity> index;

    internal LibraDexNotFacade(LibraDexIndex<TKey, TIdentity> index)
    {
        this.index = index;
    }

    /// <summary>
    /// Captures a not-equal key query.<br/>
    /// Duplicate-key indexes can plan this as complement key extents rather than row-by-row inequality when the physical shape supports it.<br/>
    /// </summary>
    public LibraDexCriteriaQuery<TKey, TIdentity> Find(TKey key)
    {
        _ = key;
        return new LibraDexCriteriaQuery<TKey, TIdentity>(
            index,
            LibraDexCriteriaKind.Find,
            QueryDirection.Ascending,
            skip: 0,
            take: null,
            RetrievalScope.Tuples,
            bookmark: null,
            LibraDexExecutionKind.Projection,
            isNegated: true);
    }

    /// <summary>
    /// Captures a negated inclusive range query.<br/>
    /// The intended implementation is complement planning over ordered key extents, not caller-side filtering after materialization.<br/>
    /// </summary>
    public LibraDexCriteriaQuery<TKey, TIdentity> Between(TKey lowerKey, TKey upperKey)
    {
        _ = lowerKey;
        _ = upperKey;
        return new LibraDexCriteriaQuery<TKey, TIdentity>(
            index,
            LibraDexCriteriaKind.Between,
            QueryDirection.Ascending,
            skip: 0,
            take: null,
            RetrievalScope.Tuples,
            bookmark: null,
            LibraDexExecutionKind.Projection,
            isNegated: true);
    }

    /// <summary>
    /// Determines whether an exact key has no matching identities.<br/>
    /// This uses the connected positive existence shortcut now; complement planning can later make richer negated criteria efficient too.<br/>
    /// </summary>
    public bool Exists(TKey key)
    {
        return !index.Exists(key);
    }

    /// <summary>
    /// Determines whether none of the supplied keys has a matching identity.<br/>
    /// </summary>
    public bool ExistsIn(IEnumerable<TKey> keys)
    {
        return !index.ExistsIn(keys);
    }

    /// <summary>
    /// Determines whether none of the prepared set keys has a matching identity.<br/>
    /// This uses the connected positive prepared-set existence shortcut and inverts its result.<br/>
    /// </summary>
    public bool ExistsInSet(LibraDexPreparedSet<TKey> set)
    {
        return !index.ExistsInSet(set);
    }
}

/// <summary>
/// Provides aggregate operations over index keys.<br/>
/// Aggregates are separated from retrieval because they compute values and should avoid identity materialization whenever possible.<br/>
/// </summary>
public sealed class LibraDexAggregateFacade<TKey, TIdentity>
{
    internal LibraDexAggregateFacade(LibraDexIndex<TKey, TIdentity> index)
    {
        Count = new LibraDexCountAggregate<TKey, TIdentity>(index);
        Sum = new LibraDexNamedAggregate<TKey, TIdentity>("Sum", index);
        Average = new LibraDexNamedAggregate<TKey, TIdentity>("Average", index);
        Min = new LibraDexNamedAggregate<TKey, TIdentity>("Min", index);
        Max = new LibraDexNamedAggregate<TKey, TIdentity>("Max", index);
        Median = new LibraDexNamedAggregate<TKey, TIdentity>("Median", index);
        Percentile = new LibraDexPercentileAggregate<TKey, TIdentity>();
    }

    /// <summary>
    /// Gets count aggregate operations over matching tuples or distinct keys.<br/>
    /// </summary>
    public LibraDexCountAggregate<TKey, TIdentity> Count { get; }

    /// <summary>
    /// Gets sum aggregate operations over matching keys.<br/>
    /// </summary>
    public LibraDexNamedAggregate<TKey, TIdentity> Sum { get; }

    /// <summary>
    /// Gets average aggregate operations over matching keys.<br/>
    /// </summary>
    public LibraDexNamedAggregate<TKey, TIdentity> Average { get; }

    /// <summary>
    /// Gets minimum-key aggregate operations.<br/>
    /// </summary>
    public LibraDexNamedAggregate<TKey, TIdentity> Min { get; }

    /// <summary>
    /// Gets maximum-key aggregate operations.<br/>
    /// </summary>
    public LibraDexNamedAggregate<TKey, TIdentity> Max { get; }

    /// <summary>
    /// Gets median-key aggregate operations.<br/>
    /// Median is a value aggregate and may not correspond to a stored tuple when continuous semantics are chosen later.<br/>
    /// </summary>
    public LibraDexNamedAggregate<TKey, TIdentity> Median { get; }

    /// <summary>
    /// Gets percentile-key aggregate operations.<br/>
    /// Percentile is a value aggregate; positional percentile retrieval belongs to retrieval/rank APIs instead.<br/>
    /// </summary>
    public LibraDexPercentileAggregate<TKey, TIdentity> Percentile { get; }
}

/// <summary>
/// Provides count aggregate operations.<br/>
/// The connected first slice can count inclusive range readers; additional criteria will be routed as query readers are widened.<br/>
/// </summary>
public sealed class LibraDexCountAggregate<TKey, TIdentity>
{
    private readonly LibraDexIndex<TKey, TIdentity> index;

    internal LibraDexCountAggregate(LibraDexIndex<TKey, TIdentity> index)
    {
        this.index = index;
    }

    /// <summary>
    /// Counts all tuples in an inclusive range using the current range-reader count.<br/>
    /// Distinct-key counting is named now but waits for duplicate-run de-duplication support before it can execute.<br/>
    /// </summary>
    public long Between(TKey lowerKey, TKey upperKey, AggregateScope scope = AggregateScope.Tuples)
    {
        if (scope != AggregateScope.Tuples)
        {
            throw new NotSupportedException("Distinct-key count is part of the public API scaffold but is not connected to duplicate-run de-duplication yet.");
        }

        using LibraDexRangeReader<TKey, TIdentity> reader = index.OpenRangeReader(lowerKey, upperKey);
        return reader.Count;
    }

    /// <summary>
    /// Counts tuples matching one exact key.<br/>
    /// This routes through the inclusive range reader for the current scalar fixed-shape implementation.<br/>
    /// </summary>
    public long Find(TKey key, AggregateScope scope = AggregateScope.Tuples)
    {
        return Between(key, key, scope);
    }

    /// <summary>
    /// Counts all tuples in the index using the bounds-backed all-criteria cursor.<br/>
    /// This is the aggregate counterpart to `index.All()` and avoids caller-side materialization just to learn cardinality.<br/>
    /// </summary>
    public long All(AggregateScope scope = AggregateScope.Tuples)
    {
        return CountCriteria(index.All(), scope);
    }

    /// <summary>
    /// Counts tuples with keys before the supplied boundary key.<br/>
    /// The boundary is exclusive and uses the same encoded predecessor logic as executable tuple criteria.<br/>
    /// </summary>
    public long Before(TKey key, AggregateScope scope = AggregateScope.Tuples)
    {
        return CountCriteria(index.Before(key), scope);
    }

    /// <summary>
    /// Counts tuples with keys at or before the supplied boundary key.<br/>
    /// </summary>
    public long AtOrBefore(TKey key, AggregateScope scope = AggregateScope.Tuples)
    {
        return CountCriteria(index.AtOrBefore(key), scope);
    }

    /// <summary>
    /// Counts tuples with keys after the supplied boundary key.<br/>
    /// The boundary is exclusive and uses the same encoded successor logic as executable tuple criteria.<br/>
    /// </summary>
    public long After(TKey key, AggregateScope scope = AggregateScope.Tuples)
    {
        return CountCriteria(index.After(key), scope);
    }

    /// <summary>
    /// Counts tuples with keys at or after the supplied boundary key.<br/>
    /// </summary>
    public long AtOrAfter(TKey key, AggregateScope scope = AggregateScope.Tuples)
    {
        return CountCriteria(index.AtOrAfter(key), scope);
    }

    /// <summary>
    /// Captures a count over a public named criterion that is not yet physically connected.<br/>
    /// This keeps aggregate vocabulary aligned with retrieval vocabulary without silently scanning or materializing rows.<br/>
    /// </summary>
    public LibraDexAggregateQuery<TKey, TIdentity, long> Prefix(TKey prefix, AggregateScope scope = AggregateScope.Tuples)
    {
        _ = prefix;
        return new LibraDexAggregateQuery<TKey, TIdentity, long>("Count", LibraDexCriteriaKind.Prefix, scope, LibraDexExecutionKind.FastPath);
    }

    /// <summary>
    /// Captures a count over suffix lookup.<br/>
    /// Suffix count can become fast when a reversed projection is maintained; otherwise diagnostics should report scan-backed execution.<br/>
    /// </summary>
    public LibraDexAggregateQuery<TKey, TIdentity, long> Suffix(TKey suffix, AggregateScope scope = AggregateScope.Tuples)
    {
        _ = suffix;
        return new LibraDexAggregateQuery<TKey, TIdentity, long>("Count", LibraDexCriteriaKind.Suffix, scope, LibraDexExecutionKind.Scan);
    }

    /// <summary>
    /// Captures a count over contains lookup.<br/>
    /// Contains count is normally scan-backed unless a contains-capable projection is maintained.<br/>
    /// </summary>
    public LibraDexAggregateQuery<TKey, TIdentity, long> Contains(TKey value, AggregateScope scope = AggregateScope.Tuples)
    {
        _ = value;
        return new LibraDexAggregateQuery<TKey, TIdentity, long>("Count", LibraDexCriteriaKind.Contains, scope, LibraDexExecutionKind.Scan);
    }

    /// <summary>
    /// Captures a count over pattern lookup.<br/>
    /// Pattern count is normally scan-backed unless a pattern-capable projection is maintained.<br/>
    /// </summary>
    public LibraDexAggregateQuery<TKey, TIdentity, long> Matches(object pattern, AggregateScope scope = AggregateScope.Tuples)
    {
        _ = pattern ?? throw new ArgumentNullException(nameof(pattern));
        return new LibraDexAggregateQuery<TKey, TIdentity, long>("Count", LibraDexCriteriaKind.Matches, scope, LibraDexExecutionKind.Scan);
    }

    /// <summary>
    /// Captures a count over ordinary membership lookup.<br/>
    /// Repeated membership counts should prefer prepared sets once physical prepared execution is connected.<br/>
    /// </summary>
    public LibraDexAggregateQuery<TKey, TIdentity, long> In(IEnumerable<TKey> keys, AggregateScope scope = AggregateScope.Tuples)
    {
        ArgumentNullException.ThrowIfNull(keys);
        TKey[] capturedKeys = keys.ToArray();
        return new LibraDexAggregateQuery<TKey, TIdentity, long>(
            "Count",
            LibraDexCriteriaKind.In,
            scope,
            LibraDexExecutionKind.Projection,
            () => CountValues(capturedKeys, scope));
    }

    /// <summary>
    /// Captures a count over prepared-set membership lookup.<br/>
    /// </summary>
    public LibraDexAggregateQuery<TKey, TIdentity, long> InSet(LibraDexPreparedSet<TKey> set, AggregateScope scope = AggregateScope.Tuples)
    {
        ArgumentNullException.ThrowIfNull(set);
        return new LibraDexAggregateQuery<TKey, TIdentity, long>(
            "Count",
            LibraDexCriteriaKind.InSet,
            scope,
            LibraDexExecutionKind.Projection,
            () => CountValues(set.Values, scope));
    }

    private long CountValues(IEnumerable<TKey> keys, AggregateScope scope)
    {
        if (scope != AggregateScope.Tuples)
        {
            throw new NotSupportedException("Distinct-key count is part of the public API scaffold but is not connected to duplicate-run de-duplication yet.");
        }

        long count = 0;
        HashSet<TKey> seen = new();
        foreach (TKey key in keys)
        {
            if (seen.Add(key))
            {
                count += Find(key, scope);
            }
        }

        return count;
    }

    private static long CountCriteria(LibraDexCriteriaQuery<TKey, TIdentity> query, AggregateScope scope)
    {
        if (scope != AggregateScope.Tuples)
        {
            throw new NotSupportedException("Distinct-key count is part of the public API scaffold but is not connected to duplicate-run de-duplication yet.");
        }

        using LibraDexRangeReader<TKey, TIdentity> reader = query.OpenCursor();
        return reader.Count;
    }
}

/// <summary>
/// Provides a named aggregate placeholder for operations whose public shape is agreed but whose physical implementation is pending.<br/>
/// This keeps call-site compile shape visible without silently performing inefficient materialization.<br/>
/// </summary>
public sealed class LibraDexNamedAggregate<TKey, TIdentity>
{
    private readonly string name;
    private readonly LibraDexIndex<TKey, TIdentity> index;

    internal LibraDexNamedAggregate(string name, LibraDexIndex<TKey, TIdentity> index)
    {
        this.name = name;
        this.index = index;
    }

    /// <summary>
    /// Captures an aggregate over an inclusive range.<br/>
    /// The descriptor is compile-visible now; execution is connected only when the aggregate can run without pretending to be a retrieval/materialization helper.<br/>
    /// </summary>
    public LibraDexAggregateQuery<TKey, TIdentity, TKey> Between(TKey lowerKey, TKey upperKey, AggregateScope scope = AggregateScope.Tuples)
    {
        return new LibraDexAggregateQuery<TKey, TIdentity, TKey>(
            name,
            LibraDexCriteriaKind.Between,
            scope,
            LibraDexExecutionKind.FastPath,
            CreateRangeExecutor(lowerKey, upperKey, scope));
    }

    /// <summary>
    /// Captures an aggregate over exact-key lookup.<br/>
    /// </summary>
    public LibraDexAggregateQuery<TKey, TIdentity, TKey> Find(TKey key, AggregateScope scope = AggregateScope.Tuples)
    {
        return new LibraDexAggregateQuery<TKey, TIdentity, TKey>(
            name,
            LibraDexCriteriaKind.Find,
            scope,
            LibraDexExecutionKind.FastPath,
            CreateFindExecutor(key, scope));
    }

    /// <summary>
    /// Captures an aggregate over prefix lookup.<br/>
    /// </summary>
    public LibraDexAggregateQuery<TKey, TIdentity, TKey> Prefix(TKey prefix, AggregateScope scope = AggregateScope.Tuples)
    {
        _ = prefix;
        _ = index;
        return new LibraDexAggregateQuery<TKey, TIdentity, TKey>(name, LibraDexCriteriaKind.Prefix, scope, LibraDexExecutionKind.FastPath);
    }

    /// <summary>
    /// Captures an aggregate over suffix lookup.<br/>
    /// </summary>
    public LibraDexAggregateQuery<TKey, TIdentity, TKey> Suffix(TKey suffix, AggregateScope scope = AggregateScope.Tuples)
    {
        _ = suffix;
        _ = index;
        return new LibraDexAggregateQuery<TKey, TIdentity, TKey>(name, LibraDexCriteriaKind.Suffix, scope, LibraDexExecutionKind.Scan);
    }

    /// <summary>
    /// Captures an aggregate over prepared-set membership lookup.<br/>
    /// </summary>
    public LibraDexAggregateQuery<TKey, TIdentity, TKey> InSet(LibraDexPreparedSet<TKey> set, AggregateScope scope = AggregateScope.Tuples)
    {
        _ = set ?? throw new ArgumentNullException(nameof(set));
        _ = index;
        return new LibraDexAggregateQuery<TKey, TIdentity, TKey>(name, LibraDexCriteriaKind.InSet, scope, LibraDexExecutionKind.Projection);
    }

    private Func<TKey>? CreateFindExecutor(TKey key, AggregateScope scope)
    {
        if (!IsExecutableEdgeAggregate())
        {
            return null;
        }

        return () =>
        {
            if (scope != AggregateScope.Tuples)
            {
                throw new NotSupportedException("Distinct-key min/max aggregates are part of the public API scaffold but are not connected to duplicate-run de-duplication yet.");
            }

            if (index.Aggregates.Count.Find(key) == 0)
            {
                throw new InvalidOperationException($"{name} aggregate over Find has no matching tuples.");
            }

            return key;
        };
    }

    private Func<TKey>? CreateRangeExecutor(TKey lowerKey, TKey upperKey, AggregateScope scope)
    {
        if (!IsExecutableEdgeAggregate())
        {
            return null;
        }

        return () =>
        {
            if (scope != AggregateScope.Tuples)
            {
                throw new NotSupportedException("Distinct-key min/max aggregates are part of the public API scaffold but are not connected to duplicate-run de-duplication yet.");
            }

            using LibraDexRangeReader<TKey, TIdentity> reader = index.OpenRangeReader(lowerKey, upperKey);
            if (string.Equals(name, "Min", StringComparison.Ordinal))
            {
                if (reader.TryReadNextKey(out TKey minKey))
                {
                    return minKey;
                }

                throw new InvalidOperationException("Min aggregate over Between has no matching tuples.");
            }

            bool found = false;
            TKey maxKey = default!;
            while (reader.TryReadNextKey(out TKey key))
            {
                maxKey = key;
                found = true;
            }

            if (!found)
            {
                throw new InvalidOperationException("Max aggregate over Between has no matching tuples.");
            }

            return maxKey;
        };
    }

    private bool IsExecutableEdgeAggregate()
    {
        return string.Equals(name, "Min", StringComparison.Ordinal) ||
            string.Equals(name, "Max", StringComparison.Ordinal);
    }
}

/// <summary>
/// Provides percentile aggregate placeholders over index keys.<br/>
/// Percentile aggregates return values, not tuples, and should use ordered counts/summaries when the storage engine supports them.<br/>
/// </summary>
public sealed class LibraDexPercentileAggregate<TKey, TIdentity>
{
    /// <summary>
    /// Captures a percentile aggregate over an inclusive range.<br/>
    /// The percentile parameter is expressed from 0 through 100 to match common developer expectation and SQLite percentile naming.<br/>
    /// </summary>
    public LibraDexAggregateQuery<TKey, TIdentity, TKey> Between(TKey lowerKey, TKey upperKey, double percentile, AggregateScope scope = AggregateScope.Tuples)
    {
        _ = lowerKey;
        _ = upperKey;
        _ = percentile;
        return new LibraDexAggregateQuery<TKey, TIdentity, TKey>("Percentile", LibraDexCriteriaKind.Between, scope, LibraDexExecutionKind.FastPath);
    }
}

/// <summary>
/// Represents aggregate intent over a public criterion.<br/>
/// The descriptor keeps aggregate call sites compile-visible while preventing unimplemented aggregates from returning fake values or materializing rows behind the scenes.<br/>
/// </summary>
public sealed class LibraDexAggregateQuery<TKey, TIdentity, TResult>
{
    private readonly Func<TResult>? execute;

    internal LibraDexAggregateQuery(
        string aggregateName,
        LibraDexCriteriaKind criteriaKind,
        AggregateScope scope,
        LibraDexExecutionKind executionKind,
        Func<TResult>? execute = null)
    {
        AggregateName = aggregateName;
        CriteriaKind = criteriaKind;
        Scope = scope;
        Diagnostics = new LibraDexQueryDiagnostics(executionKind);
        this.execute = execute;
    }

    /// <summary>
    /// Gets the aggregate operation name represented by this descriptor.<br/>
    /// </summary>
    public string AggregateName { get; }

    /// <summary>
    /// Gets the criterion the aggregate applies to.<br/>
    /// </summary>
    public LibraDexCriteriaKind CriteriaKind { get; }

    /// <summary>
    /// Gets duplicate-key aggregate semantics requested by the caller.<br/>
    /// </summary>
    public AggregateScope Scope { get; }

    /// <summary>
    /// Gets public execution metadata for the aggregate descriptor.<br/>
    /// </summary>
    public LibraDexQueryDiagnostics Diagnostics { get; }

    /// <summary>
    /// Executes the aggregate once a physical implementation has been connected.<br/>
    /// The first scaffold throws so aggregate descriptors do not silently materialize retrieval results.<br/>
    /// </summary>
    public TResult Execute()
    {
        if (execute is not null)
        {
            return execute();
        }

        throw new NotSupportedException($"{AggregateName} aggregate over {CriteriaKind} is part of the public API scaffold but is not connected to physical aggregate execution yet.");
    }
}

/// <summary>
/// Provides grouped retrieval over an index.<br/>
/// Grouping is streaming-first result shaping, not merely an aggregate; materializers should remain explicit.<br/>
/// </summary>
public sealed class LibraDexGroupedFacade<TKey, TIdentity>
{
    internal LibraDexGroupedFacade(LibraDexIndex<TKey, TIdentity> index)
    {
        ByKey = new LibraDexGroupedByKey<TKey, TIdentity>(index);
    }

    /// <summary>
    /// Gets grouping operations that use the full physical key as the group key.<br/>
    /// On unique-key indexes this can degenerate internally to singleton groups while preserving grouped result shape.<br/>
    /// </summary>
    public LibraDexGroupedByKey<TKey, TIdentity> ByKey { get; }
}

/// <summary>
/// Provides grouping by full key.<br/>
/// The public shape is available now; streaming group readers will be connected once group extent readers are implemented.<br/>
/// </summary>
public sealed class LibraDexGroupedByKey<TKey, TIdentity>
{
    private readonly LibraDexIndex<TKey, TIdentity> index;

    internal LibraDexGroupedByKey(LibraDexIndex<TKey, TIdentity> index)
    {
        this.index = index;
    }

    /// <summary>
    /// Captures grouped retrieval for an inclusive key range.<br/>
    /// The result should stream one group at a time and allow group-level skip when extent-backed group readers are connected.<br/>
    /// </summary>
    public LibraDexGroupedQuery<TKey, TIdentity, TKey, LibraDexTuple<TKey, TIdentity>> Between(TKey lowerKey, TKey upperKey)
    {
        return new LibraDexGroupedQuery<TKey, TIdentity, TKey, LibraDexTuple<TKey, TIdentity>>(
            index.Between(lowerKey, upperKey),
            groupKeyDescription: "Key",
            LibraDexProjectionKind.Tuples);
    }
}

/// <summary>
/// Provides grouped retrieval for a projected result shape such as keys-only or identities-only.<br/>
/// The grouping key remains explicit while the group members follow the selected projection.<br/>
/// </summary>
public sealed class LibraDexProjectedGroupedFacade<TKey, TIdentity, TResult>
{
    internal LibraDexProjectedGroupedFacade(LibraDexIndex<TKey, TIdentity> index, LibraDexProjectionKind projection)
    {
        ByKey = new LibraDexProjectedGroupedByKey<TKey, TIdentity, TResult>(index, projection);
    }

    /// <summary>
    /// Gets projected grouping operations that use the full physical key as the group key.<br/>
    /// </summary>
    public LibraDexProjectedGroupedByKey<TKey, TIdentity, TResult> ByKey { get; }
}

/// <summary>
/// Provides projected grouping by full key.<br/>
/// This supports call sites such as `index.Keys.Grouped.ByKey.Between(...)` and `index.Identities.Grouped.ByKey.Between(...)`.<br/>
/// </summary>
public sealed class LibraDexProjectedGroupedByKey<TKey, TIdentity, TResult>
{
    private readonly LibraDexIndex<TKey, TIdentity> index;
    private readonly LibraDexProjectionKind projection;

    internal LibraDexProjectedGroupedByKey(LibraDexIndex<TKey, TIdentity> index, LibraDexProjectionKind projection)
    {
        this.index = index;
        this.projection = projection;
    }

    /// <summary>
    /// Captures projected grouped retrieval for an inclusive key range.<br/>
    /// The descriptor preserves group key meaning and result projection without materializing grouped data eagerly.<br/>
    /// </summary>
    public LibraDexGroupedQuery<TKey, TIdentity, TKey, TResult> Between(TKey lowerKey, TKey upperKey)
    {
        return new LibraDexGroupedQuery<TKey, TIdentity, TKey, TResult>(
            index.Between(lowerKey, upperKey),
            groupKeyDescription: "Key",
            projection);
    }
}

/// <summary>
/// Represents a grouped query over an underlying tuple query.<br/>
/// The first scaffold records grouping intent and diagnostics without materializing groups.<br/>
/// </summary>
public sealed class LibraDexGroupedQuery<TKey, TIdentity, TGroupKey, TResult>
{
    private readonly LibraDexProjectionKind projection;

    internal LibraDexGroupedQuery(LibraDexQuery<TKey, TIdentity> query, string groupKeyDescription, LibraDexProjectionKind projection)
    {
        Query = query;
        GroupKeyDescription = groupKeyDescription;
        this.projection = projection;
        GroupDirection = QueryDirection.Ascending;
        ItemDirection = QueryDirection.Ascending;
    }

    /// <summary>
    /// Gets the underlying tuple query that defines the candidate extent for grouping.<br/>
    /// Group readers can use this query to open the physical cursor when group extent support is connected.<br/>
    /// </summary>
    public LibraDexQuery<TKey, TIdentity> Query { get; }

    /// <summary>
    /// Gets a short description of the group key projection.<br/>
    /// This is diagnostics metadata, not a replacement for the strongly typed group key on future group reader rows.<br/>
    /// </summary>
    public string GroupKeyDescription { get; }

    /// <summary>
    /// Gets the requested group ordering direction.<br/>
    /// Group order and item order are separate axes so callers can scan groups one way and members another way when supported.<br/>
    /// </summary>
    public QueryDirection GroupDirection { get; private set; }

    /// <summary>
    /// Gets the requested ordering direction inside each group.<br/>
    /// This is separate from group ordering because group-key order and member order are not always the same projection.<br/>
    /// </summary>
    public QueryDirection ItemDirection { get; private set; }

    /// <summary>
    /// Gets the minimum group count filter, when supplied.<br/>
    /// This is the LibraDex group-filter equivalent of a simple SQL having-count predicate.<br/>
    /// </summary>
    public long? MinimumCount { get; private set; }

    /// <summary>
    /// Gets a description of the group-key filter, when supplied.<br/>
    /// The first scaffold records filter intent; typed key-filter predicates can be added when group readers are connected.<br/>
    /// </summary>
    public string? GroupKeyFilterDescription { get; private set; }

    /// <summary>
    /// Sets group and item ordering intent for the grouped result.<br/>
    /// Implementations should use ordered group traversal instead of sorting materialized groups when the group projection supports it.<br/>
    /// </summary>
    public LibraDexGroupedQuery<TKey, TIdentity, TGroupKey, TResult> OrderBy(
        QueryDirection groupDirection = QueryDirection.Ascending,
        QueryDirection itemDirection = QueryDirection.Ascending)
    {
        GroupDirection = groupDirection;
        ItemDirection = itemDirection;
        return this;
    }

    /// <summary>
    /// Adds a count-based group filter to the grouped query.<br/>
    /// This mirrors SQL having-count capability while keeping the public API explicit and discoverable.<br/>
    /// </summary>
    public LibraDexGroupedQuery<TKey, TIdentity, TGroupKey, TResult> WhereCount(long minimumCount)
    {
        _ = minimumCount;
        MinimumCount = minimumCount;
        return this;
    }

    /// <summary>
    /// Adds a group-key filter description to the grouped query.<br/>
    /// This keeps the filter axis visible without committing the first scaffold to a specific predicate delegate or expression type.<br/>
    /// </summary>
    public LibraDexGroupedQuery<TKey, TIdentity, TGroupKey, TResult> WhereKey(string description)
    {
        GroupKeyFilterDescription = description ?? throw new ArgumentNullException(nameof(description));
        return this;
    }

    /// <summary>
    /// Opens a streaming grouped reader when group extent traversal has been connected.<br/>
    /// The first scaffold throws to avoid building a heap dictionary behind a streaming-looking API.<br/>
    /// </summary>
    public LibraDexGroupReader<TGroupKey, TResult> OpenReader()
    {
        throw new NotSupportedException("Grouped streaming readers are part of the public API scaffold but are not connected to physical group extents yet.");
    }

    /// <summary>
    /// Materializes groups into a dictionary when grouped execution has been connected.<br/>
    /// Materialization is explicit because streaming should remain the default shape for large grouped results.<br/>
    /// </summary>
    public IReadOnlyDictionary<TGroupKey, IReadOnlyList<TResult>> ToDictionary()
    {
        if (!string.Equals(GroupKeyDescription, "Key", StringComparison.Ordinal))
        {
            throw new NotSupportedException("Only full-key grouped dictionary materialization is connected in this slice.");
        }

        if (GroupKeyFilterDescription is not null)
        {
            throw new NotSupportedException("Group-key filters are recorded in the public scaffold but are not connected to grouped dictionary materialization yet.");
        }

        if (GroupDirection != QueryDirection.Ascending)
        {
            throw new NotSupportedException("Descending group dictionary materialization requires ordered reverse group traversal and is not connected yet.");
        }

#pragma warning disable CS8714
        Dictionary<TGroupKey, List<TResult>> materialized = new();
#pragma warning restore CS8714
        using LibraDexRangeReader<TKey, TIdentity> reader = Query.OpenCursor();
        while (reader.TryReadNext(out TKey key, out TIdentity identity))
        {
            TGroupKey groupKey = ConvertGroupKey(key);
            if (!materialized.TryGetValue(groupKey, out List<TResult>? group))
            {
                group = new List<TResult>();
                materialized.Add(groupKey, group);
            }

            group.Add(ConvertResult(key, identity));
        }

#pragma warning disable CS8714
        Dictionary<TGroupKey, IReadOnlyList<TResult>> result = new(materialized.Count);
#pragma warning restore CS8714
        foreach (KeyValuePair<TGroupKey, List<TResult>> group in materialized)
        {
            if (MinimumCount is not null && group.Value.Count < MinimumCount.Value)
            {
                continue;
            }

            if (ItemDirection == QueryDirection.Descending)
            {
                group.Value.Reverse();
            }

            result.Add(group.Key, group.Value);
        }

        return result;
    }

    private static TGroupKey ConvertGroupKey(TKey key)
    {
        if (key is TGroupKey typed)
        {
            return typed;
        }

        throw new NotSupportedException($"Grouped dictionary materialization cannot convert key type {typeof(TKey).FullName} to group key type {typeof(TGroupKey).FullName}.");
    }

    private TResult ConvertResult(TKey key, TIdentity identity)
    {
        object value = projection switch
        {
            LibraDexProjectionKind.Tuples => new LibraDexTuple<TKey, TIdentity>(key, identity),
            LibraDexProjectionKind.Keys => key!,
            LibraDexProjectionKind.Identities => identity!,
            _ => throw new NotSupportedException($"Grouped dictionary materialization does not support projection {projection}.")
        };

        return (TResult)value;
    }
}

/// <summary>
/// Represents a streaming grouped reader.<br/>
/// The type exists so public call sites can distinguish group-level movement from row-level cursor traversal before the physical implementation is connected.<br/>
/// </summary>
public sealed class LibraDexGroupReader<TGroupKey, TResult> : IDisposable
{
    /// <summary>
    /// Gets metadata and member cursor for the current group.<br/>
    /// </summary>
    public LibraDexGroup<TGroupKey, TResult> Current => throw new InvalidOperationException("No group reader implementation is connected yet.");

    /// <summary>
    /// Advances to the next group.<br/>
    /// Connected implementations should move by group extent, not by draining every member row when group metadata makes skipping possible.<br/>
    /// </summary>
    public bool MoveNextGroup()
    {
        throw new NotSupportedException("Grouped streaming readers are part of the public API scaffold but are not connected to physical group extents yet.");
    }

    /// <summary>
    /// Skips the current group without row-by-row drain when the group is extent-backed.<br/>
    /// This is one of the key intended differences from flattened SQL result streaming.<br/>
    /// </summary>
    public void SkipGroup()
    {
        throw new NotSupportedException("Group skip is part of the public API scaffold but is not connected to physical group extents yet.");
    }

    /// <summary>
    /// Releases the grouped reader.<br/>
    /// </summary>
    public void Dispose()
    {
    }
}

/// <summary>
/// Represents one grouped result and its metadata.<br/>
/// Group metadata should be inspectable without forcing full group enumeration once physical group readers are connected.<br/>
/// </summary>
public sealed class LibraDexGroup<TGroupKey, TResult>
{
    internal LibraDexGroup(TGroupKey key, long count)
    {
        Key = key;
        Count = count;
    }

    /// <summary>
    /// Gets the group key.<br/>
    /// The group key may differ from the physical index key when grouping by projection, date part, GUID segment, or numeric bucket.<br/>
    /// </summary>
    public TGroupKey Key { get; }

    /// <summary>
    /// Gets the number of members in the group when known.<br/>
    /// Extent-backed groups should expose this without requiring member enumeration.<br/>
    /// </summary>
    public long Count { get; }
}

/// <summary>
/// Provides first, last, and middle positional retrieval shapes.<br/>
/// Positional retrieval returns real indexed entries and is intentionally distinct from aggregate median or percentile values.<br/>
/// </summary>
public sealed class LibraDexPositionalFacade<TKey, TIdentity, TResult>
{
    private readonly LibraDexIndex<TKey, TIdentity> index;
    private readonly LibraDexProjectionKind projection;
    private readonly LibraDexPositionKind position;

    internal LibraDexPositionalFacade(
        LibraDexIndex<TKey, TIdentity> index,
        LibraDexProjectionKind projection,
        LibraDexPositionKind position)
    {
        this.index = index;
        this.projection = projection;
        this.position = position;
    }

    /// <summary>
    /// Captures positional retrieval over an inclusive key range.<br/>
    /// The method returns a descriptor now; route-count based positioning will be connected after the public call shape is validated.<br/>
    /// </summary>
    public LibraDexPositionalQuery<TKey, TIdentity, TResult> Between(
        TKey lowerKey,
        TKey upperKey,
        int count = 1,
        MiddleBias bias = MiddleBias.LeftBiased,
        QueryDirection direction = QueryDirection.Ascending)
    {
        return new LibraDexPositionalQuery<TKey, TIdentity, TResult>(
            index.Between(lowerKey, upperKey, direction),
            projection,
            position,
            count,
            bias,
            rank: null,
            percentile: null);
    }
}

/// <summary>
/// Represents positional retrieval intent over an underlying query.<br/>
/// This object keeps `First`, `Last`, and `Middle` call sites compile-visible before route-count positioning is implemented.<br/>
/// </summary>
public sealed class LibraDexPositionalQuery<TKey, TIdentity, TResult>
{
    internal LibraDexPositionalQuery(
        LibraDexQuery<TKey, TIdentity> query,
        LibraDexProjectionKind projection,
        LibraDexPositionKind position,
        int count,
        MiddleBias bias,
        long? rank,
        double? percentile)
    {
        Query = query;
        Projection = projection;
        Position = position;
        Count = count;
        Bias = bias;
        Rank = rank;
        Percentile = percentile;
    }

    /// <summary>
    /// Gets the query extent that positional retrieval operates over.<br/>
    /// </summary>
    public LibraDexQuery<TKey, TIdentity> Query { get; }

    /// <summary>
    /// Gets the requested return projection for the positional result.<br/>
    /// </summary>
    public LibraDexProjectionKind Projection { get; }

    /// <summary>
    /// Gets the requested positional operation.<br/>
    /// </summary>
    public LibraDexPositionKind Position { get; }

    /// <summary>
    /// Gets the requested number of centered or edge-position entries.<br/>
    /// </summary>
    public int Count { get; }

    /// <summary>
    /// Gets the middle-window bias requested by the caller.<br/>
    /// The value is meaningful for middle retrieval and harmlessly recorded for first/last descriptors.<br/>
    /// </summary>
    public MiddleBias Bias { get; }

    /// <summary>
    /// Gets the zero-based absolute rank requested by the caller, when this descriptor represents rank retrieval.<br/>
    /// </summary>
    public long? Rank { get; }

    /// <summary>
    /// Gets the percentile requested by the caller, when this descriptor represents percentile-rank retrieval.<br/>
    /// </summary>
    public double? Percentile { get; }

    /// <summary>
    /// Materializes the requested positional entries from the executable query extent.<br/>
    /// This first connected implementation uses the existing ordered cursor and explicit materialization; future route-count support can seek directly to edge or middle windows without changing call sites.<br/>
    /// </summary>
    /// <returns>The selected positional entries in query order.</returns>
    public IReadOnlyList<TResult> ToList()
    {
        if (Count < 0)
        {
            throw new InvalidOperationException("Positional count cannot be negative.");
        }

        if (Count == 0)
        {
            return Array.Empty<TResult>();
        }

        using LibraDexRangeReader<TKey, TIdentity> reader = Query.OpenCursor();
        int available = reader.Count;
        int requested = Math.Min(Count, available);
        if (requested == 0)
        {
            return Array.Empty<TResult>();
        }

        int skip = Position switch
        {
            LibraDexPositionKind.First => 0,
            LibraDexPositionKind.Last => available - requested,
            LibraDexPositionKind.Middle => GetMiddleSkip(available, requested),
            LibraDexPositionKind.Rank => GetRankSkip(available),
            LibraDexPositionKind.PercentRank => GetPercentRankSkip(available, requested),
            _ => throw new NotSupportedException($"{Position} positional materialization is not connected in this slice.")
        };

        if (skip >= available)
        {
            return Array.Empty<TResult>();
        }

        requested = Math.Min(requested, available - skip);

        if (skip > 0)
        {
            _ = reader.Skip(skip);
        }

        List<TResult> results = new(requested);
        for (int i = 0; i < requested && reader.TryReadNext(out TKey key, out TIdentity identity); i++)
        {
            results.Add(ConvertResult(key, identity));
        }

        return results;
    }

    private int GetMiddleSkip(int available, int requested)
    {
        int remainder = available - requested;
        return Bias == MiddleBias.RightBiased
            ? (remainder + 1) / 2
            : remainder / 2;
    }

    private int GetRankSkip(int available)
    {
        long requestedRank = Rank ?? throw new InvalidOperationException("Rank positional materialization requires a rank value.");
        if (requestedRank >= available)
        {
            return available;
        }

        return checked((int)requestedRank);
    }

    private int GetPercentRankSkip(int available, int requested)
    {
        double requestedPercentile = Percentile ?? throw new InvalidOperationException("Percent-rank materialization requires a percentile value.");
        double scaled = (available - 1) * (requestedPercentile / 100d);
        int anchor = Bias == MiddleBias.RightBiased
            ? (int)Math.Ceiling(scaled)
            : (int)Math.Floor(scaled);
        int leftWidth = (requested - 1) / 2;
        return Math.Clamp(anchor - leftWidth, 0, Math.Max(0, available - requested));
    }

    private TResult ConvertResult(TKey key, TIdentity identity)
    {
        object value = Projection switch
        {
            LibraDexProjectionKind.Tuples => new LibraDexTuple<TKey, TIdentity>(key, identity),
            LibraDexProjectionKind.Keys => key!,
            LibraDexProjectionKind.Identities => identity!,
            _ => throw new NotSupportedException($"Positional materialization does not support projection {Projection}.")
        };

        return (TResult)value;
    }
}

/// <summary>
/// Identifies which public projection a descriptor returns.<br/>
/// The enum is public because diagnostics and future materializers may need to report projection shape explicitly.<br/>
/// </summary>
public enum LibraDexProjectionKind
{
    /// <summary>
    /// Returns key/identity tuples.<br/>
    /// </summary>
    Tuples = 0,

    /// <summary>
    /// Returns keys only.<br/>
    /// </summary>
    Keys = 1,

    /// <summary>
    /// Returns identities only.<br/>
    /// </summary>
    Identities = 2
}

/// <summary>
/// Identifies which ordered-position operation a descriptor represents.<br/>
/// </summary>
public enum LibraDexPositionKind
{
    /// <summary>
    /// Selects entries from the start of the ordered candidate extent.<br/>
    /// </summary>
    First = 0,

    /// <summary>
    /// Selects entries from the end of the ordered candidate extent.<br/>
    /// </summary>
    Last = 1,

    /// <summary>
    /// Selects a centered window of real indexed entries from the ordered candidate extent.<br/>
    /// </summary>
    Middle = 2,

    /// <summary>
    /// Selects entries starting at an absolute zero-based rank in the ordered candidate extent.<br/>
    /// </summary>
    Rank = 3,

    /// <summary>
    /// Selects entries near a percentile rank in the ordered candidate extent.<br/>
    /// </summary>
    PercentRank = 4
}

/// <summary>
/// Provides absolute-rank positional retrieval shapes.<br/>
/// Rank retrieval returns real indexed entries and should use route/shelf counts when connected instead of forcing caller-side count/skip/materialization.<br/>
/// </summary>
public sealed class LibraDexRankFacade<TKey, TIdentity, TResult>
{
    private readonly LibraDexIndex<TKey, TIdentity> index;
    private readonly LibraDexProjectionKind projection;
    private readonly LibraDexPositionKind position;

    internal LibraDexRankFacade(LibraDexIndex<TKey, TIdentity> index, LibraDexProjectionKind projection, LibraDexPositionKind position)
    {
        this.index = index;
        this.projection = projection;
        this.position = position;
    }

    /// <summary>
    /// Captures absolute-rank retrieval over an inclusive key range.<br/>
    /// Rank is zero-based within the ordered candidate extent; count selects a window beginning at that rank.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower key.</param>
    /// <param name="upperKey">The inclusive upper key.</param>
    /// <param name="rank">The zero-based rank inside the ordered candidate extent.</param>
    /// <param name="count">The number of real indexed entries to select.</param>
    /// <param name="direction">The requested return direction for the selected entries.</param>
    /// <returns>A positional descriptor.</returns>
    public LibraDexPositionalQuery<TKey, TIdentity, TResult> Between(
        TKey lowerKey,
        TKey upperKey,
        long rank,
        int count = 1,
        QueryDirection direction = QueryDirection.Ascending)
    {
        if (rank < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rank), rank, "Rank cannot be negative.");
        }

        return new LibraDexPositionalQuery<TKey, TIdentity, TResult>(
            index.Between(lowerKey, upperKey, direction),
            projection,
            position,
            count,
            MiddleBias.LeftBiased,
            rank,
            percentile: null);
    }
}

/// <summary>
/// Provides percentile-rank positional retrieval shapes.<br/>
/// Percent-rank retrieval returns real indexed entries near an ordered percentile and is distinct from aggregate percentile interpolation.<br/>
/// </summary>
public sealed class LibraDexPercentRankFacade<TKey, TIdentity, TResult>
{
    private readonly LibraDexIndex<TKey, TIdentity> index;
    private readonly LibraDexProjectionKind projection;

    internal LibraDexPercentRankFacade(LibraDexIndex<TKey, TIdentity> index, LibraDexProjectionKind projection)
    {
        this.index = index;
        this.projection = projection;
    }

    /// <summary>
    /// Captures percentile-rank retrieval over an inclusive key range.<br/>
    /// Percentile is expressed from 0 through 100; the selected entry or window must be made deterministic when route-count positioning is connected.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower key.</param>
    /// <param name="upperKey">The inclusive upper key.</param>
    /// <param name="percentile">The requested percentile from 0 through 100.</param>
    /// <param name="count">The number of real indexed entries to select around the percentile rank.</param>
    /// <param name="bias">The bias used when the percentile maps between two positions.</param>
    /// <param name="direction">The requested return direction for the selected entries.</param>
    /// <returns>A positional descriptor.</returns>
    public LibraDexPositionalQuery<TKey, TIdentity, TResult> Between(
        TKey lowerKey,
        TKey upperKey,
        double percentile,
        int count = 1,
        MiddleBias bias = MiddleBias.LeftBiased,
        QueryDirection direction = QueryDirection.Ascending)
    {
        if (percentile < 0 || percentile > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(percentile), percentile, "Percentile must be from 0 through 100.");
        }

        return new LibraDexPositionalQuery<TKey, TIdentity, TResult>(
            index.Between(lowerKey, upperKey, direction),
            projection,
            LibraDexPositionKind.PercentRank,
            count,
            bias,
            rank: null,
            percentile);
    }
}

/// <summary>
/// Provides passive stats over a catalog.<br/>
/// Catalog stats answer file/group-level questions; index stats provide physical detail for one index.<br/>
/// </summary>
public sealed class CatalogStats
{
    private long sequence;
    private readonly Dictionary<long, LibraDexStatsDelta> markerSnapshots = [];
    private long inserts;
    private long deletes;
    private long rekeys;
    private long commits;
    private long bytesWritten;
    private long shelfSplits;
    private long routeChanges;

    internal CatalogStats(Catalog catalog)
    {
        Catalog = catalog;
    }

    /// <summary>
    /// Gets the catalog that owns this stats surface.<br/>
    /// </summary>
    public Catalog Catalog { get; }

    /// <summary>
    /// Creates a marker that can be compared with a later stats snapshot.<br/>
    /// The first scaffold records marker identity only; real counters will be connected as mutation paths publish stats.<br/>
    /// </summary>
    public LibraDexStatsMarker Mark()
    {
        LibraDexStatsMarker marker = new(++sequence);
        markerSnapshots[marker.Sequence] = Current;
        return marker;
    }

    /// <summary>
    /// Returns counter deltas since a prior marker.<br/>
    /// The first scaffold returns zero deltas until catalog-level mutation counters are connected.<br/>
    /// </summary>
    public LibraDexStatsDelta Since(LibraDexStatsMarker marker)
    {
        LibraDexStatsDelta snapshot = markerSnapshots.TryGetValue(marker.Sequence, out LibraDexStatsDelta value)
            ? value
            : default;
        LibraDexStatsDelta current = Current;
        return new LibraDexStatsDelta(
            current.Inserts - snapshot.Inserts,
            current.Deletes - snapshot.Deletes,
            current.Rekeys - snapshot.Rekeys,
            current.Commits - snapshot.Commits,
            current.BytesWritten - snapshot.BytesWritten,
            current.ShelfSplits - snapshot.ShelfSplits,
            current.RouteChanges - snapshot.RouteChanges);
    }

    /// <summary>
    /// Gets the current catalog-level counter snapshot.<br/>
    /// These counters are cheap and cumulative for the current catalog instance.<br/>
    /// </summary>
    public LibraDexStatsDelta Current => new(inserts, deletes, rekeys, commits, bytesWritten, shelfSplits, routeChanges);

    internal void RecordInsert(LibraDexGenericInsertResult result)
    {
        if (result.Inserted)
        {
            inserts++;
        }

        if (result.CreatedInitialShelfRoute)
        {
            routeChanges++;
        }

        bytesWritten += result.RouteCreateCommit.BytesWritten + result.InsertCommit.BytesWritten;
    }

    internal void RecordCommit(LibraDexGenericBatchCommitResult result)
    {
        commits++;
        bytesWritten += result.Commit.BytesWritten;
    }

    internal void RecordDelete()
    {
        deletes++;
    }

    internal void RecordRekey()
    {
        rekeys++;
    }

    internal void RecordShelfSplit()
    {
        shelfSplits++;
    }
}

/// <summary>
/// Provides explicit catalog-level maintenance operations.<br/>
/// Maintenance operations are separate from normal query/mutation calls so optimization, validation, and cache work remain developer-controlled.<br/>
/// </summary>
public sealed class CatalogMaintenance
{
    internal CatalogMaintenance(Catalog catalog)
    {
        Catalog = catalog;
    }

    /// <summary>
    /// Gets the catalog that owns this maintenance surface.<br/>
    /// </summary>
    public Catalog Catalog { get; }

    /// <summary>
    /// Captures catalog validation intent.<br/>
    /// Connected validation should inspect catalog-level structure and compatibility without changing persisted state.<br/>
    /// </summary>
    /// <param name="options">Optional maintenance options.</param>
    /// <returns>A maintenance result descriptor.</returns>
    public LibraDexMaintenanceResult Validate(LibraDexMaintenanceOptions? options = null)
    {
        LibraDexMaintenanceOptions effective = options ?? new LibraDexMaintenanceOptions { Mode = LibraDexMaintenanceMode.Light };
        return new LibraDexMaintenanceResult(
            LibraDexMaintenanceOperation.Validate,
            effective.Mode,
            Completed: false,
            ConsideredCount: 0,
            ChangedCount: 0,
            Message: "Catalog validation is part of the public API scaffold but is not connected to structural validation yet.");
    }

    /// <summary>
    /// Captures catalog-wide optimization intent.<br/>
    /// Connected implementations should coordinate index-level optimizers without hiding unbounded cold work behind ordinary operations.<br/>
    /// </summary>
    /// <param name="options">Optional maintenance options.</param>
    /// <returns>A maintenance result descriptor.</returns>
    public LibraDexMaintenanceResult Optimize(LibraDexMaintenanceOptions? options = null)
    {
        LibraDexMaintenanceOptions effective = options ?? new LibraDexMaintenanceOptions();
        return new LibraDexMaintenanceResult(
            LibraDexMaintenanceOperation.Optimize,
            effective.Mode,
            Completed: false,
            ConsideredCount: 0,
            ChangedCount: 0,
            Message: "Catalog optimization is part of the public API scaffold but is not connected to physical maintenance yet.");
    }
}

/// <summary>
/// Provides catalog-level tools and utilities.<br/>
/// These operations are intentionally outside the normal hot path and should be used for developer support, inspection, export, and repair workflows.<br/>
/// </summary>
public sealed class CatalogTools
{
    internal CatalogTools(Catalog catalog)
    {
        Catalog = catalog;
    }

    /// <summary>
    /// Gets the catalog that owns this tools surface.<br/>
    /// </summary>
    public Catalog Catalog { get; }

    /// <summary>
    /// Captures format inspection intent.<br/>
    /// Connected implementations should return a structured summary rather than forcing callers to parse raw bytes or internal snapshots.<br/>
    /// </summary>
    /// <returns>A tool result descriptor.</returns>
    public LibraDexToolResult InspectFormat()
    {
        return new LibraDexToolResult(
            LibraDexToolOperation.InspectFormat,
            Completed: false,
            Message: "Format inspection is part of the public API scaffold but is not connected to a structured tool output yet.");
    }

    /// <summary>
    /// Captures catalog summary export intent.<br/>
    /// Export should remain a utility operation so normal applications do not pay for diagnostic formatting.<br/>
    /// </summary>
    /// <returns>A tool result descriptor.</returns>
    public LibraDexToolResult ExportSummary()
    {
        return new LibraDexToolResult(
            LibraDexToolOperation.ExportSummary,
            Completed: false,
            Message: "Summary export is part of the public API scaffold but is not connected to tool output yet.");
    }

    /// <summary>
    /// Captures repair intent.<br/>
    /// Repair is a tool/utility operation rather than normal open behavior because it may need explicit caller approval and diagnostics.<br/>
    /// </summary>
    /// <returns>A tool result descriptor.</returns>
    public LibraDexToolResult Repair()
    {
        return new LibraDexToolResult(
            LibraDexToolOperation.Repair,
            Completed: false,
            Message: "Repair is part of the public API scaffold but is not connected to repair tooling yet.");
    }
}

/// <summary>
/// Provides catalog compatibility and migration operations.<br/>
/// Compatibility is separated from ordinary open/query paths so upgrade decisions are explicit and reviewable.<br/>
/// </summary>
public sealed class CatalogCompatibility
{
    internal CatalogCompatibility(Catalog catalog)
    {
        Catalog = catalog;
    }

    /// <summary>
    /// Gets the catalog that owns this compatibility surface.<br/>
    /// </summary>
    public Catalog Catalog { get; }

    /// <summary>
    /// Checks catalog compatibility with the current runtime.<br/>
    /// The scaffold returns unknown until the format-version and profile-evolution checks are wired to persisted metadata.<br/>
    /// </summary>
    /// <returns>A compatibility report.</returns>
    public LibraDexCompatibilityReport Check()
    {
        return new LibraDexCompatibilityReport(
            LibraDexCompatibilityStatus.Unknown,
            FormatVersion: 0,
            Message: "Catalog compatibility checks are part of the public API scaffold but are not connected to persisted format metadata yet.");
    }

    /// <summary>
    /// Captures explicit migration intent.<br/>
    /// Connected implementations should refuse in-place rewrites unless the supplied options allow them.<br/>
    /// </summary>
    /// <param name="options">Migration options.</param>
    /// <returns>A compatibility report describing migration status.</returns>
    public LibraDexCompatibilityReport Migrate(LibraDexMigrationOptions? options = null)
    {
        _ = options ?? new LibraDexMigrationOptions();
        return new LibraDexCompatibilityReport(
            LibraDexCompatibilityStatus.RequiresMigration,
            FormatVersion: 0,
            Message: "Catalog migration is part of the public API scaffold but is not connected to migration execution yet.");
    }
}

/// <summary>
/// Provides passive stats over one index.<br/>
/// Index stats expose modification counters, marker deltas, and explicit layout snapshots without making simple property reads expensive.<br/>
/// </summary>
public sealed class LibraDexIndexStats<TKey, TIdentity>
{
    private long sequence;
    private readonly Dictionary<long, LibraDexStatsDelta> markerSnapshots = [];
    private long inserts;
    private long deletes;
    private long rekeys;
    private long commits;
    private long bytesWritten;
    private long shelfSplits;
    private long routeChanges;
    private DateTimeOffset? lastModifiedUtc;

    internal LibraDexIndexStats(LibraDexIndex<TKey, TIdentity> index)
    {
        Index = index;
    }

    /// <summary>
    /// Gets the index that owns this stats surface.<br/>
    /// </summary>
    public LibraDexIndex<TKey, TIdentity> Index { get; }

    /// <summary>
    /// Gets the UTC timestamp of the last known modification.<br/>
    /// The first scaffold exposes null until mutation paths publish public stats events.<br/>
    /// </summary>
    public DateTimeOffset? LastModifiedUtc => lastModifiedUtc;

    /// <summary>
    /// Creates a marker that can be compared with later index stats.<br/>
    /// This is intended for trip-counter style checks around a developer-controlled unit of work.<br/>
    /// </summary>
    public LibraDexStatsMarker Mark()
    {
        LibraDexStatsMarker marker = new(++sequence);
        markerSnapshots[marker.Sequence] = Current;
        return marker;
    }

    /// <summary>
    /// Returns counter deltas since a prior marker.<br/>
    /// The first scaffold returns zero deltas until insert/delete/rekey counters are connected to index mutation paths.<br/>
    /// </summary>
    public LibraDexStatsDelta Since(LibraDexStatsMarker marker)
    {
        LibraDexStatsDelta snapshot = markerSnapshots.TryGetValue(marker.Sequence, out LibraDexStatsDelta value)
            ? value
            : default;
        LibraDexStatsDelta current = Current;
        return new LibraDexStatsDelta(
            current.Inserts - snapshot.Inserts,
            current.Deletes - snapshot.Deletes,
            current.Rekeys - snapshot.Rekeys,
            current.Commits - snapshot.Commits,
            current.BytesWritten - snapshot.BytesWritten,
            current.ShelfSplits - snapshot.ShelfSplits,
            current.RouteChanges - snapshot.RouteChanges);
    }

    /// <summary>
    /// Gets an explicit physical layout snapshot for the index.<br/>
    /// The method is explicit because density and layout calculations may require metadata walks once connected.<br/>
    /// </summary>
    public LibraDexLayoutStats GetLayoutSnapshot()
    {
        return default;
    }

    /// <summary>
    /// Gets the current index-level counter snapshot.<br/>
    /// These counters are cheap and cumulative for the current index instance.<br/>
    /// </summary>
    public LibraDexStatsDelta Current => new(inserts, deletes, rekeys, commits, bytesWritten, shelfSplits, routeChanges);

    internal void RecordInsert(LibraDexGenericInsertResult result)
    {
        if (result.Inserted)
        {
            inserts++;
            lastModifiedUtc = DateTimeOffset.UtcNow;
        }

        if (result.CreatedInitialShelfRoute)
        {
            routeChanges++;
        }

        bytesWritten += result.RouteCreateCommit.BytesWritten + result.InsertCommit.BytesWritten;
    }

    internal void RecordCommit(LibraDexGenericBatchCommitResult result)
    {
        commits++;
        bytesWritten += result.Commit.BytesWritten;
        if (result.InsertedCount > 0)
        {
            lastModifiedUtc = DateTimeOffset.UtcNow;
        }
    }

    internal void RecordDelete()
    {
        deletes++;
        lastModifiedUtc = DateTimeOffset.UtcNow;
    }

    internal void RecordRekey()
    {
        rekeys++;
        lastModifiedUtc = DateTimeOffset.UtcNow;
    }

    internal void RecordShelfSplit()
    {
        shelfSplits++;
    }
}

/// <summary>
/// Provides explicit index-level maintenance operations.<br/>
/// Index maintenance owns route optimization, shelf repacking, validation, and cache-oriented work for one logical index.<br/>
/// </summary>
public sealed class LibraDexIndexMaintenance<TKey, TIdentity>
{
    internal LibraDexIndexMaintenance(LibraDexIndex<TKey, TIdentity> index)
    {
        Index = index;
    }

    /// <summary>
    /// Gets the index that owns this maintenance surface.<br/>
    /// </summary>
    public LibraDexIndex<TKey, TIdentity> Index { get; }

    /// <summary>
    /// Captures index validation intent.<br/>
    /// Connected validation should inspect routes, shelves, ordering invariants, and catalog metadata for this index without changing persisted state.<br/>
    /// </summary>
    /// <param name="options">Optional maintenance options.</param>
    /// <returns>A maintenance result descriptor.</returns>
    public LibraDexMaintenanceResult Validate(LibraDexMaintenanceOptions? options = null)
    {
        LibraDexMaintenanceOptions effective = options ?? new LibraDexMaintenanceOptions { Mode = LibraDexMaintenanceMode.Light };
        return CreateResult(LibraDexMaintenanceOperation.Validate, effective, "Index validation is part of the public API scaffold but is not connected to structural validation yet.");
    }

    /// <summary>
    /// Captures index optimization intent.<br/>
    /// Connected implementations should run shape-owned optimizers such as route/shelf restructuring according to explicit developer-selected boundaries.<br/>
    /// </summary>
    /// <param name="options">Optional maintenance options.</param>
    /// <returns>A maintenance result descriptor.</returns>
    public LibraDexMaintenanceResult Optimize(LibraDexMaintenanceOptions? options = null)
    {
        LibraDexMaintenanceOptions effective = options ?? new LibraDexMaintenanceOptions();
        return CreateResult(LibraDexMaintenanceOperation.Optimize, effective, "Index optimization is part of the public API scaffold but is not connected to physical maintenance yet.");
    }

    /// <summary>
    /// Captures shelf or reusable-region repack intent.<br/>
    /// Repack should remain explicit because it may trade immediate IO and CPU for future density or traversal improvements.<br/>
    /// </summary>
    /// <param name="options">Optional maintenance options.</param>
    /// <returns>A maintenance result descriptor.</returns>
    public LibraDexMaintenanceResult Repack(LibraDexMaintenanceOptions? options = null)
    {
        LibraDexMaintenanceOptions effective = options ?? new LibraDexMaintenanceOptions();
        return CreateResult(LibraDexMaintenanceOperation.Repack, effective, "Index repack is part of the public API scaffold but is not connected to physical maintenance yet.");
    }

    /// <summary>
    /// Captures cache-oriented maintenance intent.<br/>
    /// Cache maintenance can later warm or tune route/shelf runtime state without implying persistent index-content changes.<br/>
    /// </summary>
    /// <param name="options">Optional maintenance options.</param>
    /// <returns>A maintenance result descriptor.</returns>
    public LibraDexMaintenanceResult Cache(LibraDexMaintenanceOptions? options = null)
    {
        LibraDexMaintenanceOptions effective = options ?? new LibraDexMaintenanceOptions { Mode = LibraDexMaintenanceMode.Light };
        return CreateResult(LibraDexMaintenanceOperation.Cache, effective, "Index cache maintenance is part of the public API scaffold but is not connected to runtime cache work yet.");
    }

    private static LibraDexMaintenanceResult CreateResult(
        LibraDexMaintenanceOperation operation,
        LibraDexMaintenanceOptions options,
        string message)
    {
        return new LibraDexMaintenanceResult(
            operation,
            options.Mode,
            Completed: false,
            ConsideredCount: 0,
            ChangedCount: 0,
            message);
    }
}
