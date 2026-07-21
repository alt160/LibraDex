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
    Composite = 5,

    /// <summary>
    /// The plan applies a caller-supplied identity predicate to identities produced by an indexed sibling branch.<br/>
    /// </summary>
    ExternalFilter = 6,

    /// <summary>
    /// The plan reads identities from a caller-supplied external source.<br/>
    /// </summary>
    ExternalSource = 7
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
    InSet = 12,

    /// <summary>
    /// The descriptor represents a condition-derived set of ordered key ranges over one index.<br/>
    /// This kind is intentionally produced by condition planning, not by a public direct retrieval method.<br/>
    /// </summary>
    MultiRange = 13,

    /// <summary>
    /// The descriptor represents a condition-derived component predicate over an Abraxas-compatible structured scalar key.<br/>
    /// This kind is intentionally produced by condition planning when a date/time component is directly addressable by shift-and-mask but is not contiguous in the ordered key space.<br/>
    /// </summary>
    StructuredComponent = 14,

    /// <summary>
    /// The descriptor represents a condition-derived GUID nibble predicate over the encoded 16-byte GUID key.<br/>
    /// This kind is intentionally produced by condition planning for Abraxas-compatible GUID starts-with, ends-with, contains, and pattern branches without converting stored keys to text.<br/>
    /// </summary>
    GuidPattern = 15,

    /// <summary>
    /// The descriptor represents a condition-derived binary byte predicate over encoded fixed-width byte-array keys.<br/>
    /// This kind is intentionally produced by condition planning for raw binary byte-slice branches without decoding each candidate key into a caller-facing array.<br/>
    /// </summary>
    BinaryPattern = 16,

    /// <summary>
    /// The descriptor represents a condition-derived typed binary slice predicate over encoded fixed-width byte-array keys.<br/>
    /// This kind is intentionally produced by condition planning for Abraxas-compatible `BinarySlice` branches that reinterpret a byte slice as a typed value.<br/>
    /// </summary>
    BinaryTypedSlice = 17,

    /// <summary>
    /// The descriptor represents a condition-derived string predicate over exact stored string keys.<br/>
    /// This is the explicit index-scan or bounded-candidate bridge used when no maintained text projection is available.<br/>
    /// </summary>
    StringPattern = 18,

    /// <summary>
    /// The descriptor represents a condition-derived routed composite-key predicate.<br/>
    /// This is intentionally separate from flat byte-key retrieval so each composite tier can preserve its own codec, projection, and mini-router semantics.<br/>
    /// </summary>
    CompositeMatch = 19,

    /// <summary>
    /// The descriptor represents a condition-derived bitmask predicate over a scalar key.<br/>
    /// This kind is intentionally scan-backed in the first implementation because arbitrary bitmask predicates are not generally contiguous in ordered key space.<br/>
    /// </summary>
    Bitmask = 20,

    /// <summary>
    /// The descriptor represents scalar null presence over the metadata-backed null route.<br/>
    /// `ScalarNull.Null` reads the compact identity-only route, while `ScalarNull.NonNull` reads ordinary value routes and excludes scalar nulls.<br/>
    /// </summary>
    ScalarNull = 21,

    /// <summary>
    /// The descriptor represents null or empty key states over metadata-backed key-state routes.<br/>
    /// `NullKey.Null` and `NullKey.Empty` read compact identity-only routes, while `NullKey.NullOrEmpty` reads both routes in key-state order.<br/>
    /// </summary>
    KeyState = 22
}

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
    Except = 4,

    /// <summary>
    /// The node filters identities with a caller-supplied predicate during composition with an indexed sibling.<br/>
    /// </summary>
    External = 5
}

/// <summary>
/// Carries one identity-side external filter invocation from LibraDex to caller code.<br/>
/// The context is intentionally identity-only: source-object loading stays caller-owned, while LibraDex supplies stream position metadata that can help callers cache or batch their own side data.<br/>
/// `Ordinal` and `IsFirst` are scoped to the candidate stream being filtered; they are not global result positions after later set composition or retrieval shaping.<br/>
/// </summary>
/// <param name="Identity">The identity value currently being considered by the filter.<br/></param>
/// <param name="Ordinal">The zero-based ordinal within the indexed candidate stream being filtered.<br/></param>
/// <param name="IsFirst"><see langword="true"/> when <paramref name="Identity"/> is the first candidate identity presented to the external filter.<br/></param>
public readonly record struct LibraDexExternalIdentityContext(object Identity, long Ordinal, bool IsFirst);

/// <summary>
/// Represents one caller-supplied external key/identity entry for an inline external condition branch.<br/>
/// The key is local to the external branch's predicates, while the identity is composed with LibraDex identity streams and later retrieval still decides whether callers receive identities, keys, or entries.<br/>
/// This value is for runtime-index style `.External<TKey>(...)` branches; it does not create or mutate a stored LibraDex index.<br/>
/// </summary>
/// <typeparam name="TKey">The external branch key type.<br/></typeparam>
/// <typeparam name="TIdentity">The identity type associated with the catalog group being filtered.<br/></typeparam>
/// <param name="Key">The external key value used by operators such as `Between`, `EqualTo`, and `InSet`.<br/></param>
/// <param name="Identity">The LibraDex identity value associated with the external key.<br/></param>
public readonly record struct LibraDexExternalEntry<TKey, TIdentity>(TKey Key, TIdentity Identity);

/// <summary>
/// Represents one public key/identity tuple returned by a LibraDex index.<br/>
/// The left side is the indexed key and the right side is the identity associated with that key.<br/>
/// </summary>
/// <typeparam name="TKey">The public key type.</typeparam>
/// <typeparam name="TIdentity">The public identity type.</typeparam>
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
    /// Gets the index-wide identity-to-key multiplicity contract persisted for this index.<br/>
    /// This is the planner-facing proof source for whether one identity may appear under multiple keys in the same logical index.<br/>
    /// </summary>
    IdentityKeyMultiplicity IdentityKeyMultiplicity { get; }

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
    /// Gets the DateTime-like key encoding contract used by this index.<br/>
    /// Non-date index families return <see cref="DateTimeKeyEncoding.CalendarSdt"/> as the neutral default.<br/>
    /// </summary>
    DateTimeKeyEncoding DateTimeKeyEncoding => global::LibraDex.DateTimeKeyEncoding.CalendarSdt;

    /// <summary>
    /// Gets the fixed encoded key byte width when this index stores fixed-width raw key bytes.<br/>
    /// Non-binary and variable-width index families return <see langword="null"/> so projection planners can distinguish exact byte-range-capable indexes from scan-backed fallbacks.<br/>
    /// </summary>
    int? FixedKeyByteWidth => null;

    /// <summary>
    /// Counts the physical key/identity tuples currently visible through this index.<br/>
    /// Implementations should answer from maintained index metadata or shelf/range count metadata instead of materializing identities whenever the physical shape supports it.<br/>
    /// </summary>
    /// <returns>The number of stored index tuples.</returns>
    long Count()
    {
        throw new NotSupportedException($"Index '{Name}' does not expose a physical tuple count.");
    }

    /// <summary>
    /// Inserts one runtime key and runtime identity after validating both values against the persisted CLR type contract for this index.<br/>
    /// This is the strict programmatic mutation path for generated callers that opened an index through metadata rather than generic type arguments.<br/>
    /// </summary>
    /// <param name="key">The runtime key value to insert.</param>
    /// <param name="identity">The runtime identity value to associate with the key.</param>
    /// <returns>The insert result plus any route-create and insert commit telemetry.</returns>
    LibraDexGenericInsertResult Insert(object? key, object identity);

    /// <summary>
    /// Deletes one runtime key/identity tuple after validating both values against this index's persisted CLR type contract.<br/>
    /// This is the strict programmatic counterpart to typed direct tuple deletion and preserves neighboring identities that share the same key.<br/>
    /// </summary>
    /// <param name="key">The runtime key side of the tuple to delete.</param>
    /// <param name="identity">The runtime identity side of the tuple to delete.</param>
    /// <returns><see langword="true"/> when one tuple was removed.</returns>
    bool Delete(object? key, object identity)
    {
        throw new NotSupportedException($"Index '{Name}' does not expose non-generic exact tuple deletion.");
    }

    /// <summary>
    /// Re-keys one runtime identity when the caller also knows the old runtime key.<br/>
    /// The operation verifies or creates the replacement tuple before removing the old exact tuple so failed replacement does not lose the original entry.<br/>
    /// </summary>
    /// <param name="identity">The runtime identity to re-key.</param>
    /// <param name="oldKey">The current runtime key associated with the identity.</param>
    /// <param name="newKey">The replacement runtime key to associate with the identity.</param>
    /// <returns><see langword="true"/> when the old tuple existed and was removed after the replacement tuple was available.</returns>
    bool Rekey(object identity, object? oldKey, object? newKey)
    {
        throw new NotSupportedException($"Index '{Name}' does not expose non-generic exact tuple rekey.");
    }

    /// <summary>
    /// Re-keys one runtime identity when the caller does not know the old key.<br/>
    /// Connected implementations may scan visible tuples until a maintained reverse identity lookup exists, preserving the same public call shape for generated callers.<br/>
    /// </summary>
    /// <param name="identity">The runtime identity to re-key.</param>
    /// <param name="newKey">The replacement runtime key to associate with the identity.</param>
    /// <returns>The number of old tuples removed after replacement tuples were available.</returns>
    long Rekey(object identity, object? newKey)
    {
        throw new NotSupportedException($"Index '{Name}' does not expose non-generic identity rekey without an old key.");
    }

    /// <summary>
    /// Prepares a strict non-generic key-membership set for condition-builder `InSet` calls.<br/>
    /// </summary>
    /// <param name="keys">The runtime key values to validate and prepare.</param>
    /// <returns>A prepared non-generic key-membership descriptor.</returns>
    LibraDexPreparedObjectSet PrepareInSet(IEnumerable<object> keys);
}

/// <summary>
/// Represents one runtime key/identity tuple projected from a selected LibraDex index.<br/>
/// The values are runtime objects because this shape is meant for tooling, adapters, and workbench views that choose an index by name rather than carrying generic type parameters.<br/>
/// </summary>
/// <param name="Key">The indexed key value from the selected index.</param>
/// <param name="Identity">The identity value stored for the key.</param>
public readonly record struct LibraDexRuntimeTuple(object? Key, object Identity);

/// <summary>
/// Represents a programmatic identity criterion over one identity group.<br/>
/// Leaves are index-backed lookups; composite nodes combine identity sets with `And`, `Or`, `Except`, and `Not` inside one identity group without introducing mixed-source row-shape vocabulary.<br/>
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
    /// Gets the caller-supplied identity predicate for an external-filter criterion, or null for ordinary index-backed and composite criteria.<br/>
    /// External criteria are filters over an indexed sibling stream; they do not define an identity universe by themselves.<br/>
    /// </summary>
    Func<LibraDexExternalIdentityContext, bool>? ExternalIdentityFilter { get; }

    /// <summary>
    /// Gets the caller-supplied identity source for an external-source criterion, or null for ordinary index-backed and composite criteria.<br/>
    /// External identity sources define their own identity stream and can therefore stand alone or participate in set composition such as `Or` and `And`.<br/>
    /// </summary>
    Func<IEnumerable<object>>? ExternalIdentitySource { get; }

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
/// Identifies which public projection a descriptor returns.<br/>
/// The enum is public because condition diagnostics and materializers report terminal shape explicitly.<br/>
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
    private readonly IReadOnlyList<object>? values;

    /// <summary>
    /// Creates a non-generic prepared key-membership descriptor.<br/>
    /// </summary>
    /// <param name="keyType">The runtime key type accepted by the owning index.</param>
    /// <param name="values">The prepared runtime key values.</param>
    public LibraDexPreparedObjectSet(Type keyType, IReadOnlyList<object> values)
        : this(keyType, values, values, null)
    {
    }

    /// <summary>
    /// Creates a non-generic prepared key-membership descriptor that can preserve the caller's original managed set.<br/>
    /// </summary>
    /// <param name="keyType">The runtime key type accepted by the owning index.</param>
    /// <param name="values">The prepared runtime key values, when already materialized.</param>
    /// <param name="source">The managed source collection used for membership preparation.</param>
    /// <param name="comparer">The optional equality comparer carried by the source set.</param>
    public LibraDexPreparedObjectSet(Type keyType, IReadOnlyList<object>? values, IEnumerable<object> source, IEqualityComparer<object>? comparer = null)
    {
        KeyType = keyType ?? throw new ArgumentNullException(nameof(keyType));
        this.values = values;
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Comparer = comparer;
    }

    /// <summary>
    /// Gets the runtime key type accepted by this prepared set.<br/>
    /// </summary>
    public Type KeyType { get; }

    /// <summary>
    /// Gets the prepared runtime key values.<br/>
    /// The first scaffold keeps CLR values; later implementations can attach encoded membership state behind this descriptor.<br/>
    /// </summary>
    public IReadOnlyList<object> Values => values ?? Source.ToArray();

    /// <summary>
    /// Gets the managed source values supplied or prepared for this set.<br/>
    /// When the caller supplied a compatible set, this can be the original set rather than a copied array.<br/>
    /// </summary>
    public IEnumerable<object> Source { get; }

    /// <summary>
    /// Gets the optional equality comparer associated with the source set.<br/>
    /// This is runtime metadata only and is not serialized across catalog boundaries.<br/>
    /// </summary>
    public IEqualityComparer<object>? Comparer { get; }
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
    /// Gets whether <see cref="NewKey"/> was supplied as a literal replacement key.<br/>
    /// This distinguishes `SetKey(null)` from factory-based mutations where no static key exists.<br/>
    /// </summary>
    bool HasNewKey { get; }

    /// <summary>
    /// Gets the static replacement key when one was supplied.<br/>
    /// </summary>
    object? NewKey { get; }

    /// <summary>
    /// Gets the replacement-key factory when one was supplied.<br/>
    /// The factory receives the current identity object and should return the replacement key for that identity.<br/>
    /// </summary>
    Func<object, object?>? NewKeyFactory { get; }

    /// <summary>
    /// Executes this criteria-scoped mutation through the connected primitive mutation bridge.<br/>
    /// The first connected path supports single-leaf delete primitives whose backing index implements a physical mutator; composed multi-index mutations remain explicit failures until target-scope policy is selected.<br/>
    /// </summary>
    /// <returns>A mutation result describing matched and changed tuple counts.</returns>
    LibraDexIdentityMutationResult Execute();
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
    IIdentityCriterionMutation SetKey(object? newKey);

    /// <summary>
    /// Captures set-key intent with a replacement-key factory.<br/>
    /// </summary>
    /// <param name="newKeyFactory">Factory that receives a matched identity and returns its replacement key.</param>
    /// <returns>A mutation descriptor.</returns>
    IIdentityCriterionMutation SetKeyUsing(Func<object, object?> newKeyFactory);
}

/// <summary>
/// Describes the result of executing one criteria-scoped mutation.<br/>
/// Counts are tuple-oriented because LibraDex indexes store key/identity tuples; deleting one identity from two matching keys is two changed tuples even when the identity value is the same.<br/>
/// </summary>
/// <param name="Kind">The mutation kind that was executed.</param>
/// <param name="MatchedCount">The number of tuples matched by the condition primitive.</param>
/// <param name="ChangedCount">The number of tuples changed by the mutation.</param>
/// <param name="Diagnostics">Execution diagnostics recorded by the primitive mutation bridge.</param>
public readonly record struct LibraDexIdentityMutationResult(
    LibraDexCriteriaMutationKind Kind,
    long MatchedCount,
    long ChangedCount,
    LibraDexQueryDiagnostics Diagnostics);

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
    long RouteChanges,
    long ReclaimedPayloadCellsRecorded = 0,
    long ReclaimedPayloadCellsConsumed = 0);

/// <summary>
/// Represents the current session-local reclaimed fixed-shelf payload-cell ledger.<br/>
/// Fixed scalar deletes remove slot visibility immediately; this snapshot reports the payload cells that have been recorded for reuse or later compaction without implying that the ledger is durable across catalog reopen yet.<br/>
/// </summary>
/// <param name="QueuedShelfCount">The number of shelves that currently have one or more queued reclaimed payload cells.</param>
/// <param name="QueuedCellCount">The number of reclaimed payload cells still queued in the current open session.</param>
/// <param name="RecordedCellCount">The cumulative number of reclaimed payload cells recorded during the current open session.</param>
/// <param name="ConsumedCellCount">The cumulative number of queued reclaimed payload cells consumed by later inserts during the current open session.</param>
public readonly record struct LibraDexReclaimedPayloadStats(
    int QueuedShelfCount,
    long QueuedCellCount,
    long RecordedCellCount,
    long ConsumedCellCount);

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
/// Represents a streaming grouped reader.<br/>
/// The type exists so public call sites can distinguish group-level movement from row-level cursor traversal before the physical implementation is connected.<br/>
/// </summary>
public sealed class LibraDexGroupReader<TGroupKey, TResult> : IDisposable
{
    private readonly IEnumerator<KeyValuePair<TGroupKey, IReadOnlyList<TResult>>> enumerator;
    private LibraDexGroup<TGroupKey, TResult>? current;

    internal LibraDexGroupReader(IReadOnlyDictionary<TGroupKey, IReadOnlyList<TResult>> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        enumerator = groups.GetEnumerator();
    }

    /// <summary>
    /// Gets metadata and member cursor for the current group.<br/>
    /// </summary>
    public LibraDexGroup<TGroupKey, TResult> Current => current ?? throw new InvalidOperationException("The grouped reader is not positioned on a group.");

    /// <summary>
    /// Advances to the next group.<br/>
    /// The current implementation advances over materialized groups; future extent-backed readers should move by group extent without draining every member row.<br/>
    /// </summary>
    public bool MoveNextGroup()
    {
        if (!enumerator.MoveNext())
        {
            current = null;
            return false;
        }

        KeyValuePair<TGroupKey, IReadOnlyList<TResult>> group = enumerator.Current;
        current = new LibraDexGroup<TGroupKey, TResult>(group.Key, group.Value);
        return true;
    }

    /// <summary>
    /// Skips the current group without row-by-row drain when the group is extent-backed.<br/>
    /// Dictionary-backed readers can only discard the current materialized group; physical extent readers should use this method as the cheap group-skip hook.<br/>
    /// </summary>
    public void SkipGroup()
    {
        current = null;
    }

    /// <summary>
    /// Releases the grouped reader.<br/>
    /// </summary>
    public void Dispose()
    {
        enumerator.Dispose();
    }
}

/// <summary>
/// Represents a row-streaming grouped reader that exposes group boundaries without materializing member lists.<br/>
/// This reader is intended for large grouped-result scans where callers want every member row but do not need an owned `IReadOnlyList` for each group.<br/>
/// </summary>
/// <typeparam name="TGroupKey">The group key type.</typeparam>
/// <typeparam name="TResult">The grouped member result type.</typeparam>
public sealed class LibraDexGroupRowReader<TGroupKey, TResult> : IDisposable
{
    private readonly IEnumerator<LibraDexGroupRow<TGroupKey, TResult>> enumerator;
    private LibraDexGroupRow<TGroupKey, TResult> current;
    private bool hasCurrent;

    internal LibraDexGroupRowReader(IEnumerable<LibraDexGroupRow<TGroupKey, TResult>> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        enumerator = rows.GetEnumerator();
    }

    /// <summary>
    /// Gets the current group key.<br/>
    /// The reader must be positioned on a row before this property is read.<br/>
    /// </summary>
    public TGroupKey CurrentKey => Current.Key;

    /// <summary>
    /// Gets the current grouped identity or result row.<br/>
    /// The reader must be positioned on a row before this property is read.<br/>
    /// </summary>
    public TResult CurrentItem => Current.Item;

    /// <summary>
    /// Gets whether the current row is the first streamed row in its group.<br/>
    /// Callers can use this as the group-boundary signal without requiring a per-group member list.<br/>
    /// </summary>
    public bool IsFirstInGroup => Current.IsFirstInGroup;

    /// <summary>
    /// Gets the zero-based ordinal of the current group in the streamed result.<br/>
    /// This ordinal follows the reader's group-order contract and is not a durable catalog position.<br/>
    /// </summary>
    public long GroupOrdinal => Current.GroupOrdinal;

    /// <summary>
    /// Gets the zero-based ordinal of the current row inside its group.<br/>
    /// This value increments as rows are streamed and does not require knowing the group's final count.<br/>
    /// </summary>
    public long ItemOrdinalInGroup => Current.ItemOrdinalInGroup;

    /// <summary>
    /// Gets the current streamed grouped row.<br/>
    /// </summary>
    public LibraDexGroupRow<TGroupKey, TResult> Current
    {
        get
        {
            if (!hasCurrent)
            {
                throw new InvalidOperationException("The grouped row reader is not positioned on a row.");
            }

            return current;
        }
    }

    /// <summary>
    /// Advances to the next grouped row.<br/>
    /// Group boundaries are reported through <see cref="IsFirstInGroup"/> and <see cref="GroupOrdinal"/>.<br/>
    /// </summary>
    /// <returns><see langword="true"/> when a row is available; otherwise, <see langword="false"/>.</returns>
    public bool MoveNext()
    {
        if (!enumerator.MoveNext())
        {
            hasCurrent = false;
            current = default;
            return false;
        }

        current = enumerator.Current;
        hasCurrent = true;
        return true;
    }

    /// <summary>
    /// Releases the grouped row reader.<br/>
    /// </summary>
    public void Dispose()
    {
        enumerator.Dispose();
    }
}

/// <summary>
/// Represents one row from a row-streaming grouped reader.<br/>
/// The row carries enough group-boundary metadata for callers to process large grouped results without forcing per-group member-list allocation.<br/>
/// </summary>
/// <typeparam name="TGroupKey">The group key type.</typeparam>
/// <typeparam name="TResult">The grouped member result type.</typeparam>
/// <param name="Key">The current group key.</param>
/// <param name="Item">The current grouped item.</param>
/// <param name="IsFirstInGroup">Whether this is the first streamed item for the group.</param>
/// <param name="GroupOrdinal">The zero-based streamed group ordinal.</param>
/// <param name="ItemOrdinalInGroup">The zero-based item ordinal inside the group.</param>
public readonly record struct LibraDexGroupRow<TGroupKey, TResult>(
    TGroupKey Key,
    TResult Item,
    bool IsFirstInGroup,
    long GroupOrdinal,
    long ItemOrdinalInGroup);

/// <summary>
/// Represents one grouped result and its metadata.<br/>
/// Group metadata should be inspectable without forcing full group enumeration once physical group readers are connected.<br/>
/// </summary>
public sealed class LibraDexGroup<TGroupKey, TResult>
{
    internal LibraDexGroup(TGroupKey key, IReadOnlyList<TResult> items)
    {
        Key = key;
        Items = items;
        Count = items.Count;
    }

    /// <summary>
    /// Gets the group key.<br/>
    /// The group key may differ from the physical index key when grouping by projection, date part, GUID segment, or numeric bucket.<br/>
    /// </summary>
    public TGroupKey Key { get; }

    /// <summary>
    /// Gets the materialized members in this group for the current fallback grouped reader.<br/>
    /// Future extent-backed readers may replace this with a cursor-style member reader when groups are too large to materialize by default.<br/>
    /// </summary>
    public IReadOnlyList<TResult> Items { get; }

    /// <summary>
    /// Gets the number of members in the group when known.<br/>
    /// Extent-backed groups should expose this without requiring member enumeration.<br/>
    /// </summary>
    public long Count { get; }
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
            current.RouteChanges - snapshot.RouteChanges,
            current.ReclaimedPayloadCellsRecorded - snapshot.ReclaimedPayloadCellsRecorded,
            current.ReclaimedPayloadCellsConsumed - snapshot.ReclaimedPayloadCellsConsumed);
    }

    /// <summary>
    /// Gets the current catalog-level counter snapshot.<br/>
    /// These counters are cheap and cumulative for the current catalog instance.<br/>
    /// </summary>
    public LibraDexStatsDelta Current
    {
        get
        {
            LibraDexReclaimedPayloadStats reclaimed = ReclaimedPayload;
            return new LibraDexStatsDelta(
                inserts,
                deletes,
                rekeys,
                commits,
                bytesWritten,
                shelfSplits,
                routeChanges,
                reclaimed.RecordedCellCount,
                reclaimed.ConsumedCellCount);
        }
    }

    /// <summary>
    /// Gets the current session-local reclaimed fixed-shelf payload-cell snapshot.<br/>
    /// The values are catalog/session scoped because reclaimed cells are currently tracked by durable shelf offset rather than by persisted index ownership metadata.<br/>
    /// </summary>
    public LibraDexReclaimedPayloadStats ReclaimedPayload => Catalog.Session.GetReclaimedPayloadStats();

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

        bytesWritten += result.RouteCreateDiagnostics.BytesWritten + result.InsertDiagnostics.BytesWritten;
    }

    /// <summary>
    /// Records insert counters accumulated by one successfully published typed batch.<br/>
    /// Aggregation keeps ordinary batch inserts free of per-row stats calls while preserving catalog totals for inserted tuples, initial route creation, and operation-local writes.<br/>
    /// </summary>
    /// <param name="insertedCount">The number of tuples inserted by the published batch.<br/></param>
    /// <param name="routeChangeCount">The number of initial shelf routes created by the published batch.<br/></param>
    /// <param name="operationBytesWritten">The bytes attributed to insert-local and route-create operations before the final durability commit.<br/></param>
    internal void RecordBatchInserts(long insertedCount, long routeChangeCount, long operationBytesWritten)
    {
        inserts += insertedCount;
        routeChanges += routeChangeCount;
        bytesWritten += operationBytesWritten;
    }

    internal void RecordCommit(LibraDexGenericBatchCommitResult result)
    {
        commits++;
        bytesWritten += result.CommitDiagnostics.BytesWritten;
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
/// Connected physical maintenance must be treated as write behavior and serialized with the owning catalog session write window.<br/>
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
            current.RouteChanges - snapshot.RouteChanges,
            current.ReclaimedPayloadCellsRecorded - snapshot.ReclaimedPayloadCellsRecorded,
            current.ReclaimedPayloadCellsConsumed - snapshot.ReclaimedPayloadCellsConsumed);
    }

    /// <summary>
    /// Gets an explicit physical layout snapshot for the index.<br/>
    /// The method is explicit because density and layout calculations may require metadata walks once connected.<br/>
    /// </summary>
    public LibraDexLayoutStats GetLayoutSnapshot()
        => default;

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

        bytesWritten += result.RouteCreateDiagnostics.BytesWritten + result.InsertDiagnostics.BytesWritten;
    }

    /// <summary>
    /// Records insert counters accumulated by one successfully published typed batch.<br/>
    /// The modification timestamp is sampled once at publication instead of once per tuple, while insert, route-change, and operation-byte totals remain exact.<br/>
    /// </summary>
    /// <param name="insertedCount">The number of tuples inserted by the published batch.<br/></param>
    /// <param name="routeChangeCount">The number of initial shelf routes created by the published batch.<br/></param>
    /// <param name="operationBytesWritten">The bytes attributed to insert-local and route-create operations before the final durability commit.<br/></param>
    internal void RecordBatchInserts(long insertedCount, long routeChangeCount, long operationBytesWritten)
    {
        inserts += insertedCount;
        routeChanges += routeChangeCount;
        bytesWritten += operationBytesWritten;
        if (insertedCount > 0)
        {
            lastModifiedUtc = DateTimeOffset.UtcNow;
        }
    }

    internal void RecordCommit(LibraDexGenericBatchCommitResult result)
    {
        commits++;
        bytesWritten += result.CommitDiagnostics.BytesWritten;
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
/// Connected physical maintenance and optimizer publication are write behavior, even when scheduled as background work.<br/>
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
