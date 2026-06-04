using System.Globalization;
using System.Text.RegularExpressions;

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
    ScalarNull = 21
}

internal enum LibraDexStringPatternMode
{
    StartsWith = 0,
    EndsWith = 1,
    Contains = 2,
    MatchesPattern = 3,
    EqualTo = 4,
    NotEqualTo = 5,
    GreaterThan = 6,
    GreaterOrEqual = 7,
    LessThan = 8,
    LessOrEqual = 9,
    Between = 10,
    NotBetween = 11,
    InSet = 12,
    NotInSet = 13,
    MatchesWith = 14,
    NotMatchesWith = 15,
    MatchesInSet = 16,
    NotMatchesInSet = 17
}

internal enum LibraDexBitmaskComparisonMode
{
    EqualTo = 0,
    NotEqualTo = 1
}

internal sealed class LibraDexBitmaskPredicate
{
    private readonly Type keyType;
    private readonly ulong mask;
    private readonly ulong compareValue;
    private readonly LibraDexBitmaskComparisonMode mode;

    private LibraDexBitmaskPredicate(Type keyType, ulong mask, ulong compareValue, LibraDexBitmaskComparisonMode mode)
    {
        this.keyType = keyType;
        this.mask = mask;
        this.compareValue = compareValue;
        this.mode = mode;
    }

    /// <summary>
    /// Creates a bitmask predicate for one scalar CLR key type.<br/>
    /// Mask and comparison operands are normalized once to the key type's unsigned bit pattern so execution can scan compact index keys without converting each operand repeatedly.<br/>
    /// </summary>
    /// <param name="keyType">The scalar index key type.</param>
    /// <param name="mask">The bitmask operand supplied by the condition.</param>
    /// <param name="compareValue">The value compared with the masked key result.</param>
    /// <param name="mode">Whether the masked result must equal or not equal <paramref name="compareValue"/>.</param>
    /// <returns>A compiled bitmask predicate.</returns>
    internal static LibraDexBitmaskPredicate Create(Type keyType, object mask, object compareValue, LibraDexBitmaskComparisonMode mode)
    {
        ArgumentNullException.ThrowIfNull(keyType);
        ArgumentNullException.ThrowIfNull(mask);
        ArgumentNullException.ThrowIfNull(compareValue);
        ulong normalizedMask = NormalizeBitPattern(mask, keyType, nameof(mask));
        ulong normalizedCompareValue = NormalizeBitPattern(compareValue, keyType, nameof(compareValue));
        return new LibraDexBitmaskPredicate(keyType, normalizedMask, normalizedCompareValue, mode);
    }

    /// <summary>
    /// Tests one decoded scalar index key against the captured bitmask predicate.<br/>
    /// The candidate is normalized to the same unsigned bit pattern as the mask and comparison operands, preserving signed flag semantics without treating the value as an ordered number.<br/>
    /// </summary>
    /// <param name="candidate">The decoded scalar key value from the index.</param>
    /// <returns><see langword="true"/> when the masked candidate satisfies the predicate.</returns>
    internal bool Matches(object candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ulong candidateBits = NormalizeBitPattern(candidate, keyType, nameof(candidate));
        ulong masked = candidateBits & mask;
        return mode switch
        {
            LibraDexBitmaskComparisonMode.EqualTo => masked == compareValue,
            LibraDexBitmaskComparisonMode.NotEqualTo => masked != compareValue,
            _ => throw new NotSupportedException($"Bitmask comparison mode {mode} is not supported.")
        };
    }

    private static ulong NormalizeBitPattern(object value, Type expectedType, string parameterName)
    {
        Type valueType = value.GetType();
        if (valueType != expectedType)
        {
            throw new ArgumentException($"Bitmask operand type {valueType.FullName} does not match index key type {expectedType.FullName}.", parameterName);
        }

        if (expectedType == typeof(byte))
        {
            return (byte)value;
        }

        if (expectedType == typeof(sbyte))
        {
            return unchecked((byte)(sbyte)value);
        }

        if (expectedType == typeof(short))
        {
            return unchecked((ushort)(short)value);
        }

        if (expectedType == typeof(ushort))
        {
            return (ushort)value;
        }

        if (expectedType == typeof(int))
        {
            return unchecked((uint)(int)value);
        }

        if (expectedType == typeof(uint))
        {
            return (uint)value;
        }

        if (expectedType == typeof(long))
        {
            return unchecked((ulong)(long)value);
        }

        if (expectedType == typeof(ulong))
        {
            return (ulong)value;
        }

        throw new NotSupportedException($"Bitmask conditions require an integral scalar key type, not {expectedType.FullName}.");
    }
}

internal sealed class LibraDexStringPatternPredicate
{
    private const int MaxCandidatePrefixes = 128;
    private readonly LibraDexStringPatternMode mode;
    private readonly string value;
    private readonly string? upperValue;
    private readonly IReadOnlyCollection<string>? setValues;
    private readonly ISet<string>? membershipSet;
    private readonly LibraDexStringComparisonPolicy policy;
    private readonly int? matchGroupNumber;

    private LibraDexStringPatternPredicate(
        LibraDexStringPatternMode mode,
        string value,
        string? upperValue,
        IReadOnlyCollection<string>? setValues,
        ISet<string>? membershipSet,
        LibraDexStringComparisonPolicy policy,
        int? matchGroupNumber)
    {
        this.mode = mode;
        this.value = value;
        this.upperValue = upperValue;
        this.setValues = setValues;
        this.membershipSet = membershipSet;
        this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
        this.matchGroupNumber = matchGroupNumber;
    }

    internal static LibraDexStringPatternPredicate Create(LibraDexStringPatternMode mode, string value, bool ignoreCase, string? culture)
    {
        return Create(mode, value, LibraDexStringComparisonPolicy.FromLegacy(ignoreCase, culture));
    }

    internal static LibraDexStringPatternPredicate Create(LibraDexStringPatternMode mode, string value, LibraDexStringComparisonPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new LibraDexStringPatternPredicate(mode, value, null, null, null, policy, matchGroupNumber: null);
    }

    /// <summary>
    /// Creates a string predicate that needs both lower and upper comparison operands.<br/>
    /// This is used by scan-backed condition materialization for inclusive and exclusive between-style string comparisons when a maintained sort-key projection is not available.<br/>
    /// </summary>
    /// <param name="mode">The string predicate mode to evaluate.</param>
    /// <param name="value">The lower comparison operand.</param>
    /// <param name="upperValue">The upper comparison operand.</param>
    /// <param name="ignoreCase">Whether residual comparison should ignore case.</param>
    /// <param name="culture">The optional .NET culture name used for residual comparison.</param>
    /// <returns>A compiled string predicate descriptor for the exact-index executor.</returns>
    internal static LibraDexStringPatternPredicate Create(LibraDexStringPatternMode mode, string value, string upperValue, bool ignoreCase, string? culture)
    {
        return Create(mode, value, upperValue, LibraDexStringComparisonPolicy.FromLegacy(ignoreCase, culture));
    }

    internal static LibraDexStringPatternPredicate Create(LibraDexStringPatternMode mode, string value, string upperValue, LibraDexStringComparisonPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(upperValue);
        return new LibraDexStringPatternPredicate(mode, value, upperValue, null, null, policy, matchGroupNumber: null);
    }

    /// <summary>
    /// Creates a regex capture predicate that compares the selected regex match text or numbered capture group to one expected value.<br/>
    /// Group zero is the whole match and matches the behavior of <see cref="Match.Value"/>; positive group numbers compare <see cref="Group.Value"/> for that group.<br/>
    /// </summary>
    /// <param name="mode">Whether the comparison is positive or negated.</param>
    /// <param name="pattern">The regular expression pattern.</param>
    /// <param name="expectedValue">The expected match or group value.</param>
    /// <param name="groupNumber">The optional group number; null and zero both mean the whole match.</param>
    /// <param name="policy">The string comparison policy for comparing captured text to the expected value.</param>
    /// <returns>A compiled string predicate descriptor for the exact-index executor.</returns>
    internal static LibraDexStringPatternPredicate CreateRegexCapture(
        LibraDexStringPatternMode mode,
        string pattern,
        string expectedValue,
        int? groupNumber,
        LibraDexStringComparisonPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(expectedValue);
        ValidateRegexGroupNumber(groupNumber);
        return new LibraDexStringPatternPredicate(mode, pattern, expectedValue, null, null, policy, groupNumber);
    }

    /// <summary>
    /// Creates a regex capture predicate that compares the selected regex match text or numbered capture group to a value set.<br/>
    /// Group zero is the whole match and matches the behavior of <see cref="Match.Value"/>; positive group numbers compare <see cref="Group.Value"/> for that group.<br/>
    /// </summary>
    /// <param name="mode">Whether the comparison is positive or negated.</param>
    /// <param name="pattern">The regular expression pattern.</param>
    /// <param name="expectedValues">The expected match or group values.</param>
    /// <param name="groupNumber">The optional group number; null and zero both mean the whole match.</param>
    /// <param name="policy">The string comparison policy for comparing captured text to the expected values.</param>
    /// <returns>A compiled string predicate descriptor for the exact-index executor.</returns>
    internal static LibraDexStringPatternPredicate CreateRegexCaptureSet(
        LibraDexStringPatternMode mode,
        string pattern,
        IEnumerable<string> expectedValues,
        int? groupNumber,
        LibraDexStringComparisonPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(expectedValues);
        ArgumentNullException.ThrowIfNull(policy);
        ValidateRegexGroupNumber(groupNumber);
        HashSet<string> prepared = new(policy.EqualityComparer);
        foreach (string expectedValue in expectedValues)
        {
            ArgumentNullException.ThrowIfNull(expectedValue);
            prepared.Add(expectedValue);
        }

        if (prepared.Count == 0)
        {
            throw new ArgumentException("Regex capture membership predicates require at least one expected value.", nameof(expectedValues));
        }

        return new LibraDexStringPatternPredicate(mode, pattern, null, prepared, prepared, policy, groupNumber);
    }

    /// <summary>
    /// Creates a string predicate that evaluates membership over a caller-supplied string set.<br/>
    /// This is the scan-backed exact-index fallback for no-case string membership when a maintained projection is not available.<br/>
    /// </summary>
    /// <param name="mode">The membership predicate mode to evaluate.</param>
    /// <param name="values">The membership operands supplied by the condition.</param>
    /// <param name="ignoreCase">Whether residual membership comparison should ignore case.</param>
    /// <param name="culture">The optional .NET culture name used for residual comparison.</param>
    /// <returns>A compiled string predicate descriptor for the exact-index executor.</returns>
    internal static LibraDexStringPatternPredicate CreateSet(LibraDexStringPatternMode mode, IReadOnlyList<string> values, bool ignoreCase, string? culture)
    {
        return CreateSet(mode, values, LibraDexStringComparisonPolicy.FromLegacy(ignoreCase, culture));
    }

    internal static LibraDexStringPatternPredicate CreateSet(LibraDexStringPatternMode mode, IEnumerable<string> values, LibraDexStringComparisonPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(policy);
        ISet<string>? membershipSet = null;
        IReadOnlyCollection<string> captured;
        if (values is HashSet<string> hashSet && policy.IsCompatible(hashSet.Comparer))
        {
            membershipSet = hashSet;
            captured = hashSet;
        }
        else
        {
            HashSet<string> prepared = new(policy.EqualityComparer);
            foreach (string value in values)
            {
                ArgumentNullException.ThrowIfNull(value);
                prepared.Add(value);
            }

            membershipSet = prepared;
            captured = prepared;
        }

        if (captured.Count == 0)
        {
            throw new ArgumentException("String membership predicates require at least one value.", nameof(values));
        }

        return new LibraDexStringPatternPredicate(mode, captured.First(), null, captured, membershipSet, policy, matchGroupNumber: null);
    }

    internal IReadOnlyList<(string Lower, string Upper)> CreateCandidateRanges()
    {
        bool ignoreCase = policy.IgnoreCase;
        string prefix = mode == LibraDexStringPatternMode.MatchesPattern
            ? GetLeadingLiteralPrefix(value)
            : value;
        if (mode == LibraDexStringPatternMode.EqualTo)
        {
            List<string> equalityCandidates = ignoreCase
                ? CreateCaseCandidatePrefixes(value, policy.ResolveCulture(), MaxCandidatePrefixes)
                : new List<string>(1) { value };
            return CreateExactCandidateRanges(equalityCandidates);
        }

        if (mode == LibraDexStringPatternMode.InSet)
        {
            IReadOnlyCollection<string> values = RequireSetValues();
            List<string> candidates = new(values.Count);
            foreach (string item in values)
            {
                List<string> itemCandidates = ignoreCase
                    ? CreateCaseCandidatePrefixes(item, policy.ResolveCulture(), MaxCandidatePrefixes)
                    : new List<string>(1) { item };
                if (itemCandidates.Count == 0 || checked(candidates.Count + itemCandidates.Count) > MaxCandidatePrefixes)
                {
                    return Array.Empty<(string Lower, string Upper)>();
                }

                candidates.AddRange(itemCandidates);
            }

            return CreateExactCandidateRanges(candidates);
        }

        if (mode != LibraDexStringPatternMode.StartsWith &&
            mode != LibraDexStringPatternMode.MatchesPattern)
        {
            return Array.Empty<(string Lower, string Upper)>();
        }

        if (prefix.Length == 0)
        {
            return Array.Empty<(string Lower, string Upper)>();
        }

        List<string> prefixes = ignoreCase
            ? CreateCaseCandidatePrefixes(prefix, policy.ResolveCulture(), MaxCandidatePrefixes)
            : new List<string>(1) { prefix };
        return CreatePrefixCandidateRanges(prefixes);
    }

    internal bool Matches(string candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        CompareInfo compareInfo = policy.ResolveCulture().CompareInfo;
        CompareOptions options = policy.CompareOptions;
        return mode switch
        {
            LibraDexStringPatternMode.StartsWith => compareInfo.IsPrefix(candidate, value, options),
            LibraDexStringPatternMode.EndsWith => compareInfo.IsSuffix(candidate, value, options),
            LibraDexStringPatternMode.Contains => compareInfo.IndexOf(candidate, value, options) >= 0,
            LibraDexStringPatternMode.MatchesPattern => MatchesWildcard(candidate, value, compareInfo, options),
            LibraDexStringPatternMode.EqualTo => policy.EqualityComparer.Equals(candidate, value),
            LibraDexStringPatternMode.NotEqualTo => !policy.EqualityComparer.Equals(candidate, value),
            LibraDexStringPatternMode.GreaterThan => Compare(candidate, value, compareInfo, options) > 0,
            LibraDexStringPatternMode.GreaterOrEqual => Compare(candidate, value, compareInfo, options) >= 0,
            LibraDexStringPatternMode.LessThan => Compare(candidate, value, compareInfo, options) < 0,
            LibraDexStringPatternMode.LessOrEqual => Compare(candidate, value, compareInfo, options) <= 0,
            LibraDexStringPatternMode.Between => Compare(candidate, value, compareInfo, options) >= 0 &&
                Compare(candidate, RequireUpperValue(), compareInfo, options) <= 0,
            LibraDexStringPatternMode.NotBetween => Compare(candidate, value, compareInfo, options) < 0 ||
                Compare(candidate, RequireUpperValue(), compareInfo, options) > 0,
            LibraDexStringPatternMode.InSet => MatchesSet(candidate, RequireMembershipSet()),
            LibraDexStringPatternMode.NotInSet => !MatchesSet(candidate, RequireMembershipSet()),
            LibraDexStringPatternMode.MatchesWith => MatchesRegexCapture(candidate, compareInfo, options, RequireUpperValue()),
            LibraDexStringPatternMode.NotMatchesWith => !MatchesRegexCapture(candidate, compareInfo, options, RequireUpperValue()),
            LibraDexStringPatternMode.MatchesInSet => MatchesRegexCapture(candidate, RequireMembershipSet()),
            LibraDexStringPatternMode.NotMatchesInSet => !MatchesRegexCapture(candidate, RequireMembershipSet()),
            _ => false
        };
    }

    private bool MatchesRegexCapture(string candidate, CompareInfo compareInfo, CompareOptions options, string expectedValue)
    {
        Match match = Regex.Match(candidate, value, CreateRegexOptions(options));
        if (!match.Success)
        {
            return false;
        }

        string captured = SelectRegexCapture(match);
        return Compare(captured, expectedValue, compareInfo, options) == 0;
    }

    private bool MatchesRegexCapture(string candidate, ISet<string> expectedValues)
    {
        Match match = Regex.Match(candidate, value, CreateRegexOptions(policy.CompareOptions));
        return match.Success && expectedValues.Contains(SelectRegexCapture(match));
    }

    private string SelectRegexCapture(Match match)
    {
        int groupNumber = matchGroupNumber.GetValueOrDefault(0);
        if (groupNumber == 0)
        {
            return match.Value;
        }

        return groupNumber < match.Groups.Count && match.Groups[groupNumber].Success
            ? match.Groups[groupNumber].Value
            : string.Empty;
    }

    private static RegexOptions CreateRegexOptions(CompareOptions options)
    {
        RegexOptions regexOptions = RegexOptions.CultureInvariant;
        if ((options & CompareOptions.IgnoreCase) != 0)
        {
            regexOptions |= RegexOptions.IgnoreCase;
        }

        return regexOptions;
    }

    private static void ValidateRegexGroupNumber(int? groupNumber)
    {
        if (groupNumber.HasValue && groupNumber.Value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(groupNumber), groupNumber, "Regex capture group number cannot be negative.");
        }
    }

    /// <summary>
    /// Returns the upper comparison operand for between-style predicates.<br/>
    /// The guard keeps invalid predicate construction visible instead of silently treating a missing upper bound as an empty string.<br/>
    /// </summary>
    /// <returns>The required upper comparison operand.</returns>
    private string RequireUpperValue()
    {
        return upperValue ?? throw new InvalidOperationException("The string predicate mode requires an upper comparison value.");
    }

    /// <summary>
    /// Returns the captured membership operands for set-style predicates.<br/>
    /// The guard keeps predicate construction errors visible instead of treating a missing set as an empty set.<br/>
    /// </summary>
    /// <returns>The required membership operands.</returns>
    private IReadOnlyCollection<string> RequireSetValues()
    {
        return setValues ?? throw new InvalidOperationException("The string predicate mode requires membership values.");
    }

    private ISet<string> RequireMembershipSet()
    {
        return membershipSet ?? throw new InvalidOperationException("The string predicate mode requires prepared membership values.");
    }

    /// <summary>
    /// Compares one decoded exact-index string key to one condition operand using the selected .NET comparison options.<br/>
    /// Keeping the comparison in one helper makes the scan-backed string fallback explicit and avoids accidental ordinal byte-key reuse for no-case semantics.<br/>
    /// </summary>
    /// <param name="candidate">The decoded exact-index key.</param>
    /// <param name="expected">The condition comparison operand.</param>
    /// <param name="compareInfo">The culture-specific comparison engine.</param>
    /// <param name="options">The comparison options selected by the condition.</param>
    /// <returns>The .NET comparison result.</returns>
    private static int Compare(string candidate, string expected, CompareInfo compareInfo, CompareOptions options)
    {
        return compareInfo.Compare(candidate, expected, options);
    }

    /// <summary>
    /// Determines whether one decoded exact-index string key is present in a condition membership set.<br/>
    /// The comparison deliberately uses the same culture-aware .NET path as no-case equality so membership cannot regress to ordinal byte-key semantics.<br/>
    /// </summary>
    /// <param name="candidate">The decoded exact-index key.</param>
    /// <param name="values">The condition membership operands.</param>
    /// <param name="compareInfo">The culture-specific comparison engine.</param>
    /// <param name="options">The comparison options selected by the condition.</param>
    /// <returns><see langword="true"/> when the candidate matches any membership operand.</returns>
    private static bool MatchesSet(string candidate, ISet<string> values)
    {
        return values.Contains(candidate);
    }

    /// <summary>
    /// Builds exact candidate ranges from already-expanded string candidates.<br/>
    /// The caller owns candidate expansion; this helper only sorts, de-duplicates, and maps each value to an exact lower/upper pair.<br/>
    /// </summary>
    /// <param name="candidates">The candidate string values to normalize.</param>
    /// <returns>Sorted exact candidate ranges.</returns>
    private static IReadOnlyList<(string Lower, string Upper)> CreateExactCandidateRanges(List<string> candidates)
    {
        return CreateCandidateRanges(candidates, exact: true);
    }

    /// <summary>
    /// Builds prefix candidate ranges from already-expanded string candidates.<br/>
    /// Each returned range uses the candidate as the lower bound and the maximal suffix sentinel as the upper bound.<br/>
    /// </summary>
    /// <param name="candidates">The candidate string prefixes to normalize.</param>
    /// <returns>Sorted prefix candidate ranges.</returns>
    private static IReadOnlyList<(string Lower, string Upper)> CreatePrefixCandidateRanges(List<string> candidates)
    {
        return CreateCandidateRanges(candidates, exact: false);
    }

    /// <summary>
    /// Sorts and de-duplicates candidate strings before mapping them to exact or prefix ranges.<br/>
    /// This replaces the prior LINQ chain on the retrieval path with one explicit pass over the sorted list.<br/>
    /// </summary>
    /// <param name="candidates">The candidate strings to normalize in place.</param>
    /// <param name="exact">When <see langword="true"/>, lower and upper are identical; otherwise the upper bound receives the prefix sentinel.</param>
    /// <returns>Sorted candidate ranges.</returns>
    private static IReadOnlyList<(string Lower, string Upper)> CreateCandidateRanges(List<string> candidates, bool exact)
    {
        if (candidates.Count == 0)
        {
            return Array.Empty<(string Lower, string Upper)>();
        }

        candidates.Sort(StringComparer.Ordinal);
        List<(string Lower, string Upper)> ranges = new(candidates.Count);
        string? previous = null;
        for (int i = 0; i < candidates.Count; i++)
        {
            string candidate = candidates[i];
            if (string.Equals(candidate, previous, StringComparison.Ordinal))
            {
                continue;
            }

            ranges.Add(exact ? (candidate, candidate) : (candidate, candidate + '\uffff'));
            previous = candidate;
        }

        return ranges;
    }

    /// <summary>
    /// Expands a case-insensitive prefix into candidate ordinal prefixes for coarse routed lookup.<br/>
    /// The expansion is intentionally capped so no-case fallback can scope a scan without creating an unbounded candidate fan-out.<br/>
    /// </summary>
    /// <param name="prefix">The caller-supplied prefix.</param>
    /// <param name="culture">The comparison culture used for culture-aware case variants.</param>
    /// <param name="maxCandidates">The maximum number of candidate prefixes allowed.</param>
    /// <returns>The expanded candidate prefixes, or an empty list when the cap is exceeded.</returns>
    private static List<string> CreateCaseCandidatePrefixes(string prefix, CultureInfo culture, int maxCandidates)
    {
        List<string> candidates = new() { string.Empty };
        Span<char> variants = stackalloc char[5];
        for (int i = 0; i < prefix.Length; i++)
        {
            char original = prefix[i];
            int variantCount = 0;
            AddCaseVariant(variants, ref variantCount, original);
            AddCaseVariant(variants, ref variantCount, char.ToLower(original, culture));
            AddCaseVariant(variants, ref variantCount, char.ToUpper(original, culture));
            AddCaseVariant(variants, ref variantCount, char.ToLowerInvariant(original));
            AddCaseVariant(variants, ref variantCount, char.ToUpperInvariant(original));
            if (variantCount == 0 || checked(candidates.Count * variantCount) > maxCandidates)
            {
                return new List<string>();
            }

            List<string> next = new(candidates.Count * variantCount);
            foreach (string candidate in candidates)
            {
                for (int variantIndex = 0; variantIndex < variantCount; variantIndex++)
                {
                    next.Add(candidate + variants[variantIndex]);
                }
            }

            candidates = next;
        }

        return candidates;
    }

    /// <summary>
    /// Adds one character case variant to a small stack buffer when it has not already been captured.<br/>
    /// </summary>
    /// <param name="variants">The reusable variant buffer.</param>
    /// <param name="count">The number of occupied buffer entries.</param>
    /// <param name="candidate">The candidate variant to add.</param>
    private static void AddCaseVariant(Span<char> variants, ref int count, char candidate)
    {
        for (int i = 0; i < count; i++)
        {
            if (variants[i] == candidate)
            {
                return;
            }
        }

        variants[count++] = candidate;
    }

    private static string GetLeadingLiteralPrefix(string pattern)
    {
        int wildcardIndex = pattern.IndexOfAny(new[] { '*', '?' });
        return wildcardIndex < 0 ? pattern : pattern[..wildcardIndex];
    }

    private static bool MatchesWildcard(string candidate, string pattern, CompareInfo compareInfo, CompareOptions options)
    {
        return MatchesWildcardCore(candidate, 0, pattern, 0, compareInfo, options);
    }

    private static bool MatchesWildcardCore(string candidate, int candidateIndex, string pattern, int patternIndex, CompareInfo compareInfo, CompareOptions options)
    {
        while (patternIndex < pattern.Length)
        {
            char token = pattern[patternIndex];
            if (token == '*')
            {
                while (patternIndex + 1 < pattern.Length && pattern[patternIndex + 1] == '*')
                {
                    patternIndex++;
                }

                if (patternIndex + 1 == pattern.Length)
                {
                    return true;
                }

                for (int i = candidateIndex; i <= candidate.Length; i++)
                {
                    if (MatchesWildcardCore(candidate, i, pattern, patternIndex + 1, compareInfo, options))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (candidateIndex >= candidate.Length)
            {
                return false;
            }

            if (token != '?' && compareInfo.Compare(candidate.AsSpan(candidateIndex, 1).ToString(), token.ToString(), options) != 0)
            {
                return false;
            }

            candidateIndex++;
            patternIndex++;
        }

        return candidateIndex == candidate.Length;
    }
}

/// <summary>
/// Identifies one Abraxas-compatible raw binary comparison mode.<br/>
/// Modes are evaluated over the stored key bytes in forward byte order.<br/>
/// </summary>
internal enum LibraDexBinaryPatternMode
{
    StartsWith = 0,
    EndsWith = 1,
    Contains = 2,
    SliceEqual = 3,
    MatchesPattern = 4
}

/// <summary>
/// Compiles one raw binary condition into a byte-slice predicate over encoded fixed-width byte-array keys.<br/>
/// The predicate evaluates key bytes directly and does not allocate decoded byte arrays per candidate row.<br/>
/// </summary>
internal sealed class LibraDexBinaryPatternPredicate
{
    private readonly LibraDexBinaryPatternMode mode;
    private readonly byte[] value;
    private readonly byte[]? mask;
    private readonly int offset;

    private LibraDexBinaryPatternPredicate(LibraDexBinaryPatternMode mode, byte[] value, byte[]? mask, int offset)
    {
        this.mode = mode;
        this.value = value;
        this.mask = mask;
        this.offset = offset;
    }

    /// <summary>
    /// Creates a binary prefix, suffix, contains, or fixed-slice predicate from caller-supplied bytes.<br/>
    /// The supplied byte array is cloned so later caller mutation cannot change the compiled condition.<br/>
    /// </summary>
    /// <param name="mode">The binary comparison mode.</param>
    /// <param name="value">The byte sequence to compare.</param>
    /// <param name="offset">The zero-based byte offset used by fixed-slice equality.</param>
    /// <returns>The compiled binary pattern predicate.</returns>
    internal static LibraDexBinaryPatternPredicate Create(LibraDexBinaryPatternMode mode, byte[] value, int offset = 0)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0)
        {
            throw new ArgumentException("Binary pattern value cannot be empty.", nameof(value));
        }

        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Binary slice offset cannot be negative.");
        }

        return new LibraDexBinaryPatternPredicate(mode, (byte[])value.Clone(), mask: null, offset);
    }

    /// <summary>
    /// Attempts to extract an unmasked byte pattern for a specific binary mode.<br/>
    /// Projection routing uses this to convert exact suffix values into ordered reversed-prefix bounds without losing masked-pattern fallback behavior.<br/>
    /// </summary>
    /// <param name="expectedMode">The binary mode required by the caller.</param>
    /// <param name="unmaskedValue">Receives a cloned byte value when extraction succeeds.</param>
    /// <returns><see langword="true"/> when the predicate is unmasked, zero-offset, and in the requested mode.</returns>
    internal bool TryGetUnmaskedValue(LibraDexBinaryPatternMode expectedMode, out byte[] unmaskedValue)
    {
        if (mode == expectedMode && mask is null && offset == 0)
        {
            unmaskedValue = (byte[])value.Clone();
            return true;
        }

        unmaskedValue = Array.Empty<byte>();
        return false;
    }

    /// <summary>
    /// Creates a predicate over reversed key bytes that is equivalent to an original-key suffix predicate.<br/>
    /// Masked hex suffixes cannot become a tight ordered range yet, but they can still execute correctly against the maintained reversed projection.<br/>
    /// </summary>
    /// <returns>A starts-with predicate over reversed encoded key bytes.</returns>
    internal LibraDexBinaryPatternPredicate ToReversedStartsWith()
    {
        byte[] reversedValue = (byte[])value.Clone();
        Array.Reverse(reversedValue);
        byte[]? reversedMask = null;
        if (mask is not null)
        {
            reversedMask = (byte[])mask.Clone();
            Array.Reverse(reversedMask);
        }

        return new LibraDexBinaryPatternPredicate(LibraDexBinaryPatternMode.StartsWith, reversedValue, reversedMask, offset: 0);
    }

    /// <summary>
    /// Creates a binary predicate from a readable hexadecimal pattern.<br/>
    /// Hex digits select nibbles, `x` or `X` select wildcard nibbles, and common separators are ignored for readability.<br/>
    /// The cleaned pattern must contain an even number of nibbles so first-pass binary pattern matching stays byte-aligned.<br/>
    /// </summary>
    /// <param name="mode">The binary comparison mode.</param>
    /// <param name="hexPattern">The caller-supplied hexadecimal pattern.</param>
    /// <param name="offset">The zero-based byte offset used by fixed-slice pattern matching.</param>
    /// <returns>The compiled binary pattern predicate.</returns>
    internal static LibraDexBinaryPatternPredicate CreateHex(LibraDexBinaryPatternMode mode, string hexPattern, int offset = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hexPattern);
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Binary slice offset cannot be negative.");
        }

        Span<byte> target = stackalloc byte[hexPattern.Length];
        Span<byte> targetMask = stackalloc byte[hexPattern.Length];
        int nibbleCount = 0;
        foreach (char c in hexPattern)
        {
            if (c == 'x' || c == 'X')
            {
                WriteNibble(target, targetMask, nibbleCount++, 0, 0);
                continue;
            }

            if (Uri.IsHexDigit(c))
            {
                WriteNibble(target, targetMask, nibbleCount++, HexToNibble(c), 0xF);
                continue;
            }

            if (char.IsWhiteSpace(c) || c is '-' or '_' or ':' or '.')
            {
                continue;
            }

            throw new FormatException($"Binary hex pattern contains unsupported character '{c}'.");
        }

        if (nibbleCount == 0)
        {
            throw new FormatException("Binary hex pattern must contain at least one hex or wildcard nibble.");
        }

        if ((nibbleCount & 1) != 0)
        {
            throw new FormatException("Binary hex pattern must contain an even number of nibbles.");
        }

        int byteCount = nibbleCount / 2;
        byte[] capturedValue = target[..byteCount].ToArray();
        byte[] capturedMask = targetMask[..byteCount].ToArray();
        return new LibraDexBinaryPatternPredicate(mode, capturedValue, capturedMask, offset);
    }

    /// <summary>
    /// Evaluates this predicate against one encoded fixed-width byte-array key.<br/>
    /// The key span is the stored key bytes in forward byte order; no CLR byte-array key is materialized.<br/>
    /// </summary>
    /// <param name="encodedKey">The encoded key bytes for the current row.</param>
    /// <returns><see langword="true"/> when the key satisfies the predicate.</returns>
    internal bool Matches(ReadOnlySpan<byte> encodedKey)
    {
        ReadOnlySpan<byte> target = value;
        if (mask is not null)
        {
            return mode switch
            {
                LibraDexBinaryPatternMode.StartsWith => target.Length <= encodedKey.Length && MaskedEquals(encodedKey[..target.Length], target, mask),
                LibraDexBinaryPatternMode.EndsWith => target.Length <= encodedKey.Length && MaskedEquals(encodedKey[^target.Length..], target, mask),
                LibraDexBinaryPatternMode.Contains => MaskedIndexOf(encodedKey, target, mask) >= 0,
                LibraDexBinaryPatternMode.SliceEqual => offset <= encodedKey.Length &&
                    target.Length <= encodedKey.Length - offset &&
                    MaskedEquals(encodedKey.Slice(offset, target.Length), target, mask),
                LibraDexBinaryPatternMode.MatchesPattern => target.Length == encodedKey.Length && MaskedEquals(encodedKey, target, mask),
                _ => throw new InvalidOperationException($"Unknown binary pattern mode {mode}.")
            };
        }

        return mode switch
        {
            LibraDexBinaryPatternMode.StartsWith => encodedKey.StartsWith(target),
            LibraDexBinaryPatternMode.EndsWith => encodedKey.EndsWith(target),
            LibraDexBinaryPatternMode.Contains => encodedKey.IndexOf(target) >= 0,
            LibraDexBinaryPatternMode.SliceEqual => offset <= encodedKey.Length &&
                target.Length <= encodedKey.Length - offset &&
                encodedKey.Slice(offset, target.Length).SequenceEqual(target),
            LibraDexBinaryPatternMode.MatchesPattern => encodedKey.SequenceEqual(target),
            _ => throw new InvalidOperationException($"Unknown binary pattern mode {mode}.")
        };
    }

    private static void WriteNibble(Span<byte> target, Span<byte> targetMask, int nibbleIndex, byte value, byte valueMask)
    {
        int byteIndex = nibbleIndex >> 1;
        if ((nibbleIndex & 1) == 0)
        {
            target[byteIndex] = (byte)(value << 4);
            targetMask[byteIndex] = (byte)(valueMask << 4);
        }
        else
        {
            target[byteIndex] |= value;
            targetMask[byteIndex] |= valueMask;
        }
    }

    private static byte HexToNibble(char c)
    {
        if (c >= '0' && c <= '9')
        {
            return (byte)(c - '0');
        }

        if (c >= 'a' && c <= 'f')
        {
            return (byte)(c - 'a' + 10);
        }

        if (c >= 'A' && c <= 'F')
        {
            return (byte)(c - 'A' + 10);
        }

        throw new FormatException($"Invalid hex character '{c}'.");
    }

    private static bool MaskedEquals(ReadOnlySpan<byte> candidate, ReadOnlySpan<byte> target, ReadOnlySpan<byte> targetMask)
    {
        if (candidate.Length != target.Length)
        {
            return false;
        }

        for (int i = 0; i < target.Length; i++)
        {
            if ((candidate[i] & targetMask[i]) != (target[i] & targetMask[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static int MaskedIndexOf(ReadOnlySpan<byte> candidate, ReadOnlySpan<byte> target, ReadOnlySpan<byte> targetMask)
    {
        if (target.Length > candidate.Length)
        {
            return -1;
        }

        for (int i = 0; i <= candidate.Length - target.Length; i++)
        {
            if (MaskedEquals(candidate.Slice(i, target.Length), target, targetMask))
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>
/// Identifies the typed interpretation applied to one raw binary slice.<br/>
/// Numeric and date/time slices intentionally use little-endian interpretation to match Abraxas' `BinarySlice` deterministic function on the current target runtime.<br/>
/// </summary>
internal enum LibraDexBinarySliceValueKind
{
    Int32 = 0,
    Int64 = 1,
    Guid = 2,
    DateTimeTicks = 3,
    Utf8String = 4,
    Int8 = 5,
    UInt8 = 6,
    Int16 = 7,
    UInt16 = 8,
    UInt32 = 9,
    UInt64 = 10,
    Single = 11,
    Double = 12,
    Decimal = 13,
    Int128 = 14,
    UInt128 = 15,
    BigInteger = 16,
    DateOnly = 17,
    TimeOnly = 18,
    TimeSpanTicks = 19,
    DateTimeOffsetPair = 20,
    Utf16String = 21,
    Utf32String = 22,
    AsciiString = 23,
    Latin1String = 24,
    CharUtf16 = 25,
    RuneUtf32 = 26,
    CustomEncodingString = 27
}

/// <summary>
/// Identifies one comparison applied after a raw binary slice is interpreted as a typed value.<br/>
/// These modes are internal bridge payloads, not public retrieval grammar.<br/>
/// </summary>
internal enum LibraDexBinarySliceComparisonKind
{
    EqualTo = 0,
    GreaterThan = 1,
    GreaterOrEqual = 2,
    LessThan = 3,
    LessOrEqual = 4,
    Between = 5,
    StartsWith = 6,
    Contains = 7,
    BitAndEqualTo = 8,
    BitAndNotEqualTo = 9
}

/// <summary>
/// Compiles one typed binary slice condition into a predicate over encoded fixed-width byte-array keys.<br/>
/// The predicate copies no decoded key arrays; it interprets only the requested slice from the current key bytes.<br/>
/// </summary>
internal sealed class LibraDexBinaryTypedSlicePredicate
{
    private readonly LibraDexBinarySliceValueKind valueKind;
    private readonly LibraDexBinarySliceComparisonKind comparisonKind;
    private readonly int offset;
    private readonly int length;
    private readonly object value;
    private readonly object? upperValue;
    private readonly System.Text.Encoding? encoding;

    private LibraDexBinaryTypedSlicePredicate(
        LibraDexBinarySliceValueKind valueKind,
        LibraDexBinarySliceComparisonKind comparisonKind,
        int offset,
        int length,
        object value,
        object? upperValue,
        System.Text.Encoding? encoding)
    {
        this.valueKind = valueKind;
        this.comparisonKind = comparisonKind;
        this.offset = offset;
        this.length = length;
        this.value = value;
        this.upperValue = upperValue;
        this.encoding = encoding;
    }

    /// <summary>
    /// Creates a typed binary slice predicate from already captured condition operands.<br/>
    /// The offset and length are validated once so each candidate row only pays the slice-bound check and typed comparison cost.<br/>
    /// </summary>
    /// <param name="valueKind">The typed interpretation of the selected bytes.</param>
    /// <param name="comparisonKind">The comparison to apply after interpretation.</param>
    /// <param name="offset">The zero-based byte offset of the slice.</param>
    /// <param name="length">The byte length of the slice.</param>
    /// <param name="value">The first comparison value.</param>
    /// <param name="upperValue">The optional upper comparison value for between predicates.</param>
    /// <param name="encoding">The optional caller-supplied text encoding for custom encoded string slices.</param>
    /// <returns>The compiled typed binary slice predicate.</returns>
    internal static LibraDexBinaryTypedSlicePredicate Create(
        LibraDexBinarySliceValueKind valueKind,
        LibraDexBinarySliceComparisonKind comparisonKind,
        int offset,
        int length,
        object value,
        object? upperValue = null,
        System.Text.Encoding? encoding = null)
    {
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Binary slice offset cannot be negative.");
        }

        if (length <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "Binary slice length must be positive.");
        }

        if (valueKind == LibraDexBinarySliceValueKind.CustomEncodingString && encoding is null)
        {
            throw new ArgumentNullException(nameof(encoding), "Custom encoded binary string slices require a caller-supplied Encoding instance.");
        }

        return new LibraDexBinaryTypedSlicePredicate(valueKind, comparisonKind, offset, length, value, upperValue, encoding);
    }

    /// <summary>
    /// Evaluates this predicate against one encoded fixed-width byte-array key.<br/>
    /// The typed value is read directly from the requested slice; invalid offsets or invalid typed payloads simply do not match.<br/>
    /// </summary>
    /// <param name="encodedKey">The encoded key bytes for the current row.</param>
    /// <returns><see langword="true"/> when the typed slice satisfies the predicate.</returns>
    internal bool Matches(ReadOnlySpan<byte> encodedKey)
    {
        if (offset > encodedKey.Length || length > encodedKey.Length - offset)
        {
            return false;
        }

        try
        {
            ReadOnlySpan<byte> slice = encodedKey.Slice(offset, length);
            return valueKind switch
            {
                LibraDexBinarySliceValueKind.Int32 => CompareValue(System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(slice), value, upperValue),
                LibraDexBinarySliceValueKind.Int64 => CompareValue(System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(slice), value, upperValue),
                LibraDexBinarySliceValueKind.Guid => CompareValue(new Guid(slice[..16]), value, upperValue),
                LibraDexBinarySliceValueKind.DateTimeTicks => CompareValue(new DateTime(System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(slice), DateTimeKind.Utc), value, upperValue),
                LibraDexBinarySliceValueKind.Utf8String => CompareString(System.Text.Encoding.UTF8.GetString(slice), value),
                LibraDexBinarySliceValueKind.Int8 => CompareValue(unchecked((sbyte)slice[0]), value, upperValue),
                LibraDexBinarySliceValueKind.UInt8 => CompareValue(slice[0], value, upperValue),
                LibraDexBinarySliceValueKind.Int16 => CompareValue(System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(slice), value, upperValue),
                LibraDexBinarySliceValueKind.UInt16 => CompareValue(System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(slice), value, upperValue),
                LibraDexBinarySliceValueKind.UInt32 => CompareValue(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(slice), value, upperValue),
                LibraDexBinarySliceValueKind.UInt64 => CompareValue(System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(slice), value, upperValue),
                LibraDexBinarySliceValueKind.Single => CompareValue(BitConverter.Int32BitsToSingle(System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(slice)), value, upperValue),
                LibraDexBinarySliceValueKind.Double => CompareValue(BitConverter.Int64BitsToDouble(System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(slice)), value, upperValue),
                LibraDexBinarySliceValueKind.Decimal => CompareValue(System.Runtime.InteropServices.MemoryMarshal.Read<decimal>(slice), value, upperValue),
                LibraDexBinarySliceValueKind.Int128 => CompareValue(System.Runtime.InteropServices.MemoryMarshal.Read<Int128>(slice), value, upperValue),
                LibraDexBinarySliceValueKind.UInt128 => CompareValue(System.Runtime.InteropServices.MemoryMarshal.Read<UInt128>(slice), value, upperValue),
                LibraDexBinarySliceValueKind.BigInteger => CompareValue(new System.Numerics.BigInteger(slice), value, upperValue),
                LibraDexBinarySliceValueKind.DateOnly => CompareValue(DateOnly.FromDayNumber(System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(slice)), value, upperValue),
                LibraDexBinarySliceValueKind.TimeOnly => CompareValue(TimeOnly.FromTimeSpan(TimeSpan.FromTicks(System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(slice))), value, upperValue),
                LibraDexBinarySliceValueKind.TimeSpanTicks => CompareValue(TimeSpan.FromTicks(System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(slice)), value, upperValue),
                LibraDexBinarySliceValueKind.DateTimeOffsetPair => CompareValue(new DateTimeOffset(
                    System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(slice),
                    TimeSpan.FromTicks(System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(slice[8..]))).UtcDateTime, value, upperValue),
                LibraDexBinarySliceValueKind.Utf16String => CompareString(System.Text.Encoding.Unicode.GetString(slice), value),
                LibraDexBinarySliceValueKind.Utf32String => CompareString(System.Text.Encoding.UTF32.GetString(slice), value),
                LibraDexBinarySliceValueKind.AsciiString => CompareString(System.Text.Encoding.ASCII.GetString(slice), value),
                LibraDexBinarySliceValueKind.Latin1String => CompareString(System.Text.Encoding.Latin1.GetString(slice), value),
                LibraDexBinarySliceValueKind.CharUtf16 => CompareString(System.Runtime.InteropServices.MemoryMarshal.Read<char>(slice).ToString(), value),
                LibraDexBinarySliceValueKind.RuneUtf32 => CompareString(char.ConvertFromUtf32(System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(slice)), value),
                LibraDexBinarySliceValueKind.CustomEncodingString => CompareString((encoding ?? throw new InvalidOperationException("Custom encoded binary string slices require an Encoding instance.")).GetString(slice), value),
                _ => throw new InvalidOperationException($"Unknown binary slice value kind {valueKind}.")
            };
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private bool CompareString(string candidate, object expected)
    {
        string text = expected as string
            ?? throw new InvalidOperationException("Binary UTF-8 slice predicates require a string comparison value.");
        return comparisonKind switch
        {
            LibraDexBinarySliceComparisonKind.EqualTo => string.Equals(candidate, text, StringComparison.Ordinal),
            LibraDexBinarySliceComparisonKind.StartsWith => candidate.StartsWith(text, StringComparison.Ordinal),
            LibraDexBinarySliceComparisonKind.Contains => candidate.Contains(text, StringComparison.Ordinal),
            _ => throw new InvalidOperationException($"Binary UTF-8 slice comparison {comparisonKind} is not supported.")
        };
    }

    private bool CompareValue<TValue>(TValue candidate, object expected, object? upper)
        where TValue : IComparable<TValue>
    {
        if (comparisonKind is LibraDexBinarySliceComparisonKind.BitAndEqualTo or LibraDexBinarySliceComparisonKind.BitAndNotEqualTo)
        {
            return CompareBitmaskValue(candidate, expected, upper);
        }

        TValue lower = expected is TValue typedExpected
            ? typedExpected
            : throw new InvalidOperationException($"Binary typed slice predicates require a {typeof(TValue).FullName} comparison value.");
        return comparisonKind switch
        {
            LibraDexBinarySliceComparisonKind.EqualTo => candidate.CompareTo(lower) == 0,
            LibraDexBinarySliceComparisonKind.GreaterThan => candidate.CompareTo(lower) > 0,
            LibraDexBinarySliceComparisonKind.GreaterOrEqual => candidate.CompareTo(lower) >= 0,
            LibraDexBinarySliceComparisonKind.LessThan => candidate.CompareTo(lower) < 0,
            LibraDexBinarySliceComparisonKind.LessOrEqual => candidate.CompareTo(lower) <= 0,
            LibraDexBinarySliceComparisonKind.Between => upper is TValue typedUpper &&
                candidate.CompareTo(lower) >= 0 &&
                candidate.CompareTo(typedUpper) <= 0,
            _ => throw new InvalidOperationException($"Binary typed slice comparison {comparisonKind} is not supported for {typeof(TValue).FullName}.")
        };
    }

    private bool CompareBitmaskValue<TValue>(TValue candidate, object mask, object? compareValue)
    {
        if (compareValue is null)
        {
            throw new InvalidOperationException("Binary typed slice bitmask predicates require a comparison value.");
        }

        UInt128 candidateBits = NormalizeBitPattern(candidate!, typeof(TValue), nameof(candidate));
        UInt128 maskBits = NormalizeBitPattern(mask, typeof(TValue), nameof(mask));
        UInt128 compareBits = NormalizeBitPattern(compareValue, typeof(TValue), nameof(compareValue));
        UInt128 masked = candidateBits & maskBits;
        return comparisonKind switch
        {
            LibraDexBinarySliceComparisonKind.BitAndEqualTo => masked == compareBits,
            LibraDexBinarySliceComparisonKind.BitAndNotEqualTo => masked != compareBits,
            _ => throw new InvalidOperationException($"Binary typed slice comparison {comparisonKind} is not a bitmask comparison.")
        };
    }

    private static UInt128 NormalizeBitPattern(object value, Type expectedType, string parameterName)
    {
        Type valueType = value.GetType();
        if (valueType != expectedType)
        {
            throw new ArgumentException($"Binary typed slice bitmask operand type {valueType.FullName} does not match slice type {expectedType.FullName}.", parameterName);
        }

        if (expectedType == typeof(byte))
        {
            return (byte)value;
        }

        if (expectedType == typeof(sbyte))
        {
            return unchecked((byte)(sbyte)value);
        }

        if (expectedType == typeof(short))
        {
            return unchecked((ushort)(short)value);
        }

        if (expectedType == typeof(ushort))
        {
            return (ushort)value;
        }

        if (expectedType == typeof(int))
        {
            return unchecked((uint)(int)value);
        }

        if (expectedType == typeof(uint))
        {
            return (uint)value;
        }

        if (expectedType == typeof(long))
        {
            return unchecked((ulong)(long)value);
        }

        if (expectedType == typeof(ulong))
        {
            return (ulong)value;
        }

        if (expectedType == typeof(Int128))
        {
            return unchecked((UInt128)(Int128)value);
        }

        if (expectedType == typeof(UInt128))
        {
            return (UInt128)value;
        }

        throw new NotSupportedException($"Binary typed slice bitmask conditions require a fixed-width integral slice type, not {expectedType.FullName}.");
    }
}

/// <summary>
/// Identifies one Abraxas-compatible GUID pattern comparison mode.<br/>
/// Modes are evaluated over the canonical 32-nibble GUID text order while reading the stored 16-byte GUID representation directly.<br/>
/// </summary>
internal enum LibraDexGuidPatternMode
{
    StartsWith = 0,
    EndsWith = 1,
    Contains = 2,
    MatchesPattern = 3
}

/// <summary>
/// Compiles one GUID text-pattern condition into a canonical nibble mask.<br/>
/// The predicate evaluates encoded GUID bytes directly and does not materialize a string for each candidate key.<br/>
/// </summary>
internal sealed class LibraDexGuidPatternPredicate
{
    private static readonly int[] CanonicalNibbleToStorageNibble =
    {
        6, 7, 4, 5, 2, 3, 0, 1,
        10, 11, 8, 9,
        14, 15, 12, 13,
        16, 17, 18, 19,
        20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31
    };

    private readonly ulong comparedStorageNibbleBits;
    private readonly byte[] targetStorageNibbles;

    private LibraDexGuidPatternPredicate(ulong comparedStorageNibbleBits, byte[] targetStorageNibbles)
    {
        this.comparedStorageNibbleBits = comparedStorageNibbleBits;
        this.targetStorageNibbles = targetStorageNibbles;
    }

    /// <summary>
    /// Creates a GUID pattern predicate from Abraxas-compatible text input.<br/>
    /// Dashes, braces, and other non-hex/non-wildcard characters are ignored, and `x` means wildcard for explicit patterns.<br/>
    /// </summary>
    /// <param name="value">The caller-supplied GUID text, partial text, or wildcard pattern.</param>
    /// <param name="mode">The comparison mode.</param>
    /// <returns>The compiled GUID pattern predicate.</returns>
    internal static LibraDexGuidPatternPredicate Create(string value, LibraDexGuidPatternMode mode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string core = Clean(value);
        string pattern = Normalize(core, mode);
        ulong compared = 0;
        byte[] target = new byte[32];
        for (int canonicalNibble = 0; canonicalNibble < pattern.Length; canonicalNibble++)
        {
            char c = pattern[canonicalNibble];
            if (c == 'x')
            {
                continue;
            }

            int storageNibble = CanonicalNibbleToStorageNibble[canonicalNibble];
            compared |= 1UL << storageNibble;
            target[storageNibble] = HexToNibble(c);
        }

        return new LibraDexGuidPatternPredicate(compared, target);
    }

    /// <summary>
    /// Creates a GUID pattern predicate from stored GUID bytes.<br/>
    /// The byte order is the same order produced by `Guid.TryWriteBytes`, matching the bytes LibraDex stores in the 16-byte GUID key lanes.<br/>
    /// </summary>
    /// <param name="value">The caller-supplied stored GUID byte prefix, suffix, contained segment, or full pattern.</param>
    /// <param name="mode">The comparison mode.</param>
    /// <returns>The compiled GUID pattern predicate.</returns>
    internal static LibraDexGuidPatternPredicate Create(ReadOnlySpan<byte> value, LibraDexGuidPatternMode mode)
    {
        if (value.IsEmpty)
        {
            throw new FormatException("GUID byte pattern must contain at least one byte.");
        }

        if (value.Length > 16)
        {
            throw new FormatException("GUID byte pattern cannot exceed 16 bytes.");
        }

        int comparedNibbleCount = mode == LibraDexGuidPatternMode.MatchesPattern
            ? 32
            : value.Length * 2;
        if (mode == LibraDexGuidPatternMode.MatchesPattern && value.Length != 16)
        {
            throw new FormatException("GUID byte pattern must contain exactly 16 bytes for MatchesPattern.");
        }

        int startNibble = mode switch
        {
            LibraDexGuidPatternMode.StartsWith or LibraDexGuidPatternMode.MatchesPattern => 0,
            LibraDexGuidPatternMode.EndsWith => 32 - comparedNibbleCount,
            LibraDexGuidPatternMode.Contains => (32 - comparedNibbleCount) / 2,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown GUID pattern mode.")
        };

        ulong compared = 0;
        byte[] target = new byte[32];
        for (int valueNibble = 0; valueNibble < comparedNibbleCount; valueNibble++)
        {
            byte source = value[valueNibble >> 1];
            byte nibbleValue = (valueNibble & 1) == 0
                ? (byte)(source >> 4)
                : (byte)(source & 0xF);
            int storageNibble = startNibble + valueNibble;
            compared |= 1UL << storageNibble;
            target[storageNibble] = nibbleValue;
        }

        return new LibraDexGuidPatternPredicate(compared, target);
    }

    /// <summary>
    /// Evaluates this predicate against one encoded GUID key represented as two big-endian scalar lanes.<br/>
    /// The encoded lanes are converted back to the stored byte order, then read through the canonical GUID nibble map.<br/>
    /// </summary>
    /// <param name="encodedHigh">The first encoded 8-byte lane.</param>
    /// <param name="encodedLow">The second encoded 8-byte lane.</param>
    /// <returns><see langword="true"/> when the encoded GUID satisfies the predicate.</returns>
    internal bool Matches(ulong encodedHigh, ulong encodedLow)
    {
        Span<byte> storage = stackalloc byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(storage.Slice(0, 8), encodedHigh);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(storage.Slice(8, 8), encodedLow);
        for (int storageNibble = 0; storageNibble < 32; storageNibble++)
        {
            if ((comparedStorageNibbleBits & (1UL << storageNibble)) == 0)
            {
                continue;
            }

            if (ReadStorageNibble(storage, storageNibble) != targetStorageNibbles[storageNibble])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Removes GUID formatting characters while preserving hex digits and wildcard markers.<br/>
    /// </summary>
    /// <param name="value">The raw user input.</param>
    /// <returns>The cleaned lowercase pattern core.</returns>
    private static string Clean(string value)
    {
        Span<char> cleaned = stackalloc char[32];
        int count = 0;
        foreach (char c in value)
        {
            if (Uri.IsHexDigit(c) || c == 'x' || c == 'X')
            {
                if (count >= 32)
                {
                    break;
                }

                cleaned[count++] = char.ToLowerInvariant(c);
            }
        }

        if (count == 0)
        {
            throw new FormatException("GUID pattern must contain at least one hex digit or wildcard.");
        }

        return new string(cleaned[..count]);
    }

    /// <summary>
    /// Expands a cleaned GUID pattern core to the 32-nibble mask shape used by Abraxas GUID comparison modes.<br/>
    /// </summary>
    /// <param name="core">The cleaned lowercase pattern core.</param>
    /// <param name="mode">The comparison mode.</param>
    /// <returns>A 32-character pattern where `x` marks wildcard nibbles.</returns>
    private static string Normalize(string core, LibraDexGuidPatternMode mode)
    {
        if (core.Length > 32)
        {
            throw new FormatException("GUID pattern cannot exceed 32 nibbles.");
        }

        int missing = 32 - core.Length;
        return mode switch
        {
            LibraDexGuidPatternMode.StartsWith => core + new string('x', missing),
            LibraDexGuidPatternMode.EndsWith => new string('x', missing) + core,
            LibraDexGuidPatternMode.Contains => new string('x', missing / 2) + core + new string('x', missing - (missing / 2)),
            LibraDexGuidPatternMode.MatchesPattern => core.Length == 32 ? core : throw new FormatException("GUID pattern must be 32 nibbles for MatchesPattern."),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown GUID pattern mode.")
        };
    }

    /// <summary>
    /// Reads one nibble from stored GUID byte order.<br/>
    /// </summary>
    /// <param name="storage">The 16 stored GUID bytes.</param>
    /// <param name="nibble">The zero-based storage nibble ordinal.</param>
    /// <returns>The nibble value.</returns>
    private static byte ReadStorageNibble(ReadOnlySpan<byte> storage, int nibble)
    {
        byte value = storage[nibble >> 1];
        return (nibble & 1) == 0
            ? (byte)(value >> 4)
            : (byte)(value & 0xF);
    }

    /// <summary>
    /// Converts one hexadecimal character to a nibble value.<br/>
    /// </summary>
    /// <param name="c">The lowercase hexadecimal character.</param>
    /// <returns>The nibble value.</returns>
    private static byte HexToNibble(char c)
    {
        return c <= '9'
            ? (byte)(c - '0')
            : (byte)(10 + c - 'a');
    }
}

/// <summary>
/// Identifies one directly addressable component inside the Abraxas-compatible structured date/time scalar.<br/>
/// The component names describe packed scalar fields, not CLR property access at query time.<br/>
/// </summary>
internal enum LibraDexStructuredDateComponent
{
    Month = 0,
    Day = 1,
    Hour = 2,
    DayOfWeek = 3
}

/// <summary>
/// Describes one small-domain structured date/time component membership test.<br/>
/// Values are represented as a bit set so component equality, membership, range, and negated membership can execute with one shift, one mask, and one bit check per row.<br/>
/// </summary>
/// <param name="Component">The structured scalar component to extract.</param>
/// <param name="AllowedValueBits">The allowed component values represented as bit positions.</param>
/// <param name="Negate">Whether the test accepts values not present in <paramref name="AllowedValueBits"/>.</param>
internal readonly record struct LibraDexStructuredComponentTest(
    LibraDexStructuredDateComponent Component,
    ulong AllowedValueBits,
    bool Negate = false)
{
    /// <summary>
    /// Evaluates this component test against one encoded structured date/time scalar.<br/>
    /// Calendar-SDT reads day-of-week from stored bits; Precision-SDT derives day-of-week from the packed date fields when that component is requested.<br/>
    /// </summary>
    /// <param name="encodedKey">The encoded structured scalar key.</param>
    /// <param name="dateTimeKeyEncoding">The DateTime-like key encoding contract used by the owning index.</param>
    /// <returns><see langword="true"/> when the component value satisfies this test.</returns>
    internal bool Matches(ulong encodedKey, DateTimeKeyEncoding dateTimeKeyEncoding)
    {
        int componentValue = ExtractComponent(encodedKey, Component, dateTimeKeyEncoding);
        if (componentValue < 0)
        {
            return false;
        }

        ulong valueBit = 1UL << componentValue;
        bool contains = (AllowedValueBits & valueBit) != 0;
        return Negate ? !contains : contains;
    }

    /// <summary>
    /// Extracts one small-domain component from the structured scalar layout.<br/>
    /// The bit positions mirror `LibraDexStructuredDateCodec` and Abraxas' structured date/time helpers.<br/>
    /// </summary>
    /// <param name="encodedKey">The encoded structured scalar key.</param>
    /// <param name="component">The component to extract.</param>
    /// <param name="dateTimeKeyEncoding">The DateTime-like key encoding contract used by the owning index.</param>
    /// <returns>The component value as a small integer.</returns>
    private static int ExtractComponent(
        ulong encodedKey,
        LibraDexStructuredDateComponent component,
        DateTimeKeyEncoding dateTimeKeyEncoding)
    {
        return component switch
        {
            LibraDexStructuredDateComponent.Month => (int)((encodedKey >> 46) & 0xF),
            LibraDexStructuredDateComponent.Day => (int)((encodedKey >> 41) & 0x1F),
            LibraDexStructuredDateComponent.Hour => (int)((encodedKey >> 36) & 0x1F),
            LibraDexStructuredDateComponent.DayOfWeek => ExtractDayOfWeek(encodedKey, dateTimeKeyEncoding),
            _ => throw new ArgumentOutOfRangeException(nameof(component), component, "Unknown structured date component.")
        };
    }

    /// <summary>
    /// Extracts or derives the day-of-week component according to the owning index's DateTime-like key encoding.<br/>
    /// Calendar-SDT stores the component directly; Precision-SDT spends those bits on sub-millisecond precision and derives the component from year, month, and day.<br/>
    /// </summary>
    /// <param name="encodedKey">The encoded structured scalar key.</param>
    /// <param name="dateTimeKeyEncoding">The DateTime-like key encoding contract used by the owning index.</param>
    /// <returns>The day-of-week value, or -1 when the packed date fields are not a valid calendar date.</returns>
    private static int ExtractDayOfWeek(ulong encodedKey, DateTimeKeyEncoding dateTimeKeyEncoding)
    {
        if (dateTimeKeyEncoding == DateTimeKeyEncoding.CalendarSdt)
        {
            return (int)((encodedKey >> 1) & 0x7);
        }

        int year = (int)((encodedKey >> 50) & 0x3FFF);
        int month = (int)((encodedKey >> 46) & 0xF);
        int day = (int)((encodedKey >> 41) & 0x1F);
        if (year <= 0 || month <= 0 || month > 12 || day <= 0 || day > DateTime.DaysInMonth(year, month))
        {
            return -1;
        }

        return (int)new DateOnly(year, month, day).DayOfWeek;
    }
}

/// <summary>
/// Compiles one condition-derived structured date/time component predicate for execution over encoded scalar keys.<br/>
/// The predicate is intentionally internal: public callers express intent through the adopted condition builder while this payload records the physical shift-and-mask work LibraDex must perform.<br/>
/// </summary>
internal sealed class LibraDexStructuredComponentPredicate
{
    private readonly IReadOnlyList<LibraDexStructuredComponentTest> tests;
    private readonly bool requireLastDayOfMonth;
    private readonly DateTimeKeyEncoding dateTimeKeyEncoding;

    /// <summary>
    /// Initializes a structured component predicate from already validated component tests.<br/>
    /// Optional last-day-of-month matching uses packed year/month/day fields and does not reconstruct a CLR date value.<br/>
    /// </summary>
    /// <param name="tests">The component tests that must all match.</param>
    /// <param name="requireLastDayOfMonth">Whether matching also requires the day component to be the last calendar day of the encoded month.</param>
    /// <param name="dateTimeKeyEncoding">The DateTime-like key encoding contract used by the owning index.</param>
    internal LibraDexStructuredComponentPredicate(
        IReadOnlyList<LibraDexStructuredComponentTest> tests,
        bool requireLastDayOfMonth = false,
        DateTimeKeyEncoding dateTimeKeyEncoding = DateTimeKeyEncoding.CalendarSdt)
    {
        this.tests = tests;
        this.requireLastDayOfMonth = requireLastDayOfMonth;
        this.dateTimeKeyEncoding = dateTimeKeyEncoding;
    }

    /// <summary>
    /// Creates an equivalent predicate bound to the DateTime-like key encoding selected by the executing index.<br/>
    /// This keeps the public condition builder agnostic while allowing execution to derive Precision-SDT day-of-week predicates when needed.<br/>
    /// </summary>
    /// <param name="dateTimeKeyEncoding">The DateTime-like key encoding contract used by the executing index.</param>
    /// <returns>A predicate with the same component tests and the requested DateTime-like key encoding.</returns>
    internal LibraDexStructuredComponentPredicate WithEncoding(DateTimeKeyEncoding dateTimeKeyEncoding)
    {
        return new LibraDexStructuredComponentPredicate(tests, requireLastDayOfMonth, dateTimeKeyEncoding);
    }

    /// <summary>
    /// Evaluates this predicate against one encoded structured date/time scalar.<br/>
    /// The method performs only bit extraction, integer comparison, and small calendar arithmetic for last-day checks.<br/>
    /// </summary>
    /// <param name="encodedKey">The encoded structured scalar key.</param>
    /// <returns><see langword="true"/> when the encoded key satisfies the predicate.</returns>
    internal bool Matches(ulong encodedKey)
    {
        for (int i = 0; i < tests.Count; i++)
        {
            if (!tests[i].Matches(encodedKey, dateTimeKeyEncoding))
            {
                return false;
            }
        }

        return !requireLastDayOfMonth || IsLastDayOfMonth(encodedKey);
    }

    /// <summary>
    /// Determines whether the encoded day is the last valid day of its encoded month.<br/>
    /// This reads year, month, and day from the structured scalar and uses integer calendar rules without materializing a DateTime.<br/>
    /// </summary>
    /// <param name="encodedKey">The encoded structured scalar key.</param>
    /// <returns><see langword="true"/> when the encoded day is the month end.</returns>
    private static bool IsLastDayOfMonth(ulong encodedKey)
    {
        int year = (int)((encodedKey >> 50) & 0x3FFF);
        int month = (int)((encodedKey >> 46) & 0xF);
        int day = (int)((encodedKey >> 41) & 0x1F);
        if (year <= 0 || month <= 0 || month > 12 || day <= 0)
        {
            return false;
        }

        return day == DateTime.DaysInMonth(year, month);
    }
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
/// Represents one public key/identity tuple returned by a LibraDex index.<br/>
/// The left side is the indexed key and the right side is the identity associated with that key.<br/>
/// </summary>
/// <typeparam name="TKey">The public key type.</typeparam>
/// <typeparam name="TIdentity">The public identity type.</typeparam>
internal readonly record struct LibraDexTuple<TKey, TIdentity>(TKey Key, TIdentity Identity);

internal readonly record struct LibraDexObjectTuple(object? Key, object Identity)
{
    /// <summary>
    /// Compares two runtime values using structural byte-array equality when needed and default equality otherwise.<br/>
    /// Fixed binary keys and identities often materialize as byte arrays, where reference equality would not match LibraDex tuple semantics.<br/>
    /// </summary>
    /// <param name="left">The first runtime value.</param>
    /// <param name="right">The second runtime value.</param>
    /// <returns><see langword="true"/> when the values represent the same logical tuple component.</returns>
    internal static bool ValueEquals(object? left, object? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        if (left is byte[] leftBytes && right is byte[] rightBytes)
        {
            return leftBytes.AsSpan().SequenceEqual(rightBytes);
        }

        if (left is LibraDexCompositeKey leftComposite && right is LibraDexCompositeKey rightComposite)
        {
            if (leftComposite.Count != rightComposite.Count)
            {
                return false;
            }

            for (int i = 0; i < leftComposite.Count; i++)
            {
                object? leftValue = leftComposite.Values[i].Value;
                object? rightValue = rightComposite.Values[i].Value;
                if (leftValue is null || rightValue is null)
                {
                    if (leftValue is not null || rightValue is not null)
                    {
                        return false;
                    }

                    continue;
                }

                if (!ValueEquals(leftValue, rightValue))
                {
                    return false;
                }
            }

            return true;
        }

        return EqualityComparer<object>.Default.Equals(left, right);
    }
}

/// <summary>
/// Provides LibraDex key equality for decoded public key values.<br/>
/// `byte[]` keys need structural equality because array reference equality would split equivalent binary keys into different buckets during condition-level grouping and membership work.<br/>
/// Other key types delegate to <see cref="EqualityComparer{T}.Default"/> so existing value equality semantics remain intact.<br/>
/// </summary>
/// <typeparam name="TKey">The decoded key type.</typeparam>
internal static class LibraDexKeyEquality<TKey>
{
    /// <summary>
    /// Gets the comparer used for decoded key dictionary and set operations.<br/>
    /// This comparer is intentionally internal to condition/materialization helpers and is not a public query contract.<br/>
    /// </summary>
    internal static IEqualityComparer<TKey> Comparer { get; } = typeof(TKey) == typeof(byte[])
        ? (IEqualityComparer<TKey>)(object)ByteArrayKeyEqualityComparer.Instance
        : EqualityComparer<TKey>.Default;

    private sealed class ByteArrayKeyEqualityComparer : IEqualityComparer<byte[]>
    {
        internal static readonly ByteArrayKeyEqualityComparer Instance = new();

        public bool Equals(byte[]? x, byte[]? y)
        {
            return ReferenceEquals(x, y) || (x is not null && y is not null && x.AsSpan().SequenceEqual(y));
        }

        public int GetHashCode(byte[] obj)
        {
            ArgumentNullException.ThrowIfNull(obj);
            HashCode hash = new();
            foreach (byte value in obj)
            {
                hash.Add(value);
            }

            return hash.ToHashCode();
        }
    }
}

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

/// <summary>
/// Describes one inclusive key extent requested by condition-driven multi-range retrieval.<br/>
/// The bounds remain runtime objects at this layer because non-generic condition planning resolves index handles before generic physical readers are invoked.<br/>
/// </summary>
/// <param name="LowerKey">The inclusive lower key.</param>
/// <param name="UpperKey">The inclusive upper key.</param>
internal readonly record struct LibraDexIdentityKeyRange(object LowerKey, object UpperKey);

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

internal interface IIdentityPrimitiveMutator
{
    /// <summary>
    /// Deletes tuples matched by one normalized primitive request from this index.<br/>
    /// The request is already produced by the adopted condition materializer, so implementers should preserve the same key, range, scan, projection, and composite routing semantics used by retrieval for that primitive.<br/>
    /// The returned changed count is tuple-oriented because index mutation removes key/identity entries, not source objects outside LibraDex.<br/>
    /// </summary>
    /// <param name="request">The normalized primitive request to delete.</param>
    /// <returns>A mutation result describing matched and deleted tuple counts.</returns>
    LibraDexIdentityMutationResult DeleteIdentityPrimitive(LibraDexIdentityPrimitiveRequest request);
}

internal interface IIdentityPrimitiveTupleExecutor
{
    /// <summary>
    /// Materializes key/identity tuples matched by one normalized primitive request from this index.<br/>
    /// Criteria-scoped mutation uses this lower-level tuple capture when identity-only projection is not enough to safely replace or remove exact physical tuples.<br/>
    /// Implementers should preserve the same primitive routing semantics as identity retrieval while returning the original key side needed for exact mutation.<br/>
    /// </summary>
    /// <param name="request">The normalized primitive request to execute.</param>
    /// <returns>The matching key/identity tuples as non-generic object values.</returns>
    IReadOnlyList<LibraDexObjectTuple> ExecuteTuplePrimitive(LibraDexIdentityPrimitiveRequest request);
}

internal interface IIdentityExactTupleMutator
{
    /// <summary>
    /// Tests whether one exact key/identity tuple is currently visible in this index.<br/>
    /// This is used by re-key operations to distinguish an already-satisfied target tuple from an insert conflict that did not create the requested tuple.<br/>
    /// </summary>
    /// <param name="key">The key value to test.</param>
    /// <param name="identity">The identity value to test.</param>
    /// <returns><see langword="true"/> when the exact tuple exists.</returns>
    bool ContainsExactTuple(object key, object identity);

    /// <summary>
    /// Deletes one exact key/identity tuple from this index.<br/>
    /// The operation must not delete neighboring identities that share the same key, because criteria-scoped re-key depends on tuple-level replacement semantics.<br/>
    /// </summary>
    /// <param name="key">The key side of the tuple to delete.</param>
    /// <param name="identity">The identity side of the tuple to delete.</param>
    /// <returns><see langword="true"/> when a tuple was removed.</returns>
    bool DeleteExactTuple(object key, object identity);
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

    /// <summary>
    /// Creates an executable leaf criterion for a projection index while preserving the caller's logical identity group.<br/>
    /// Hidden projection indexes may intentionally have no public group metadata, but their identity values still belong to the owning logical group.<br/>
    /// </summary>
    /// <param name="group">The logical identity group for the owning condition.</param>
    /// <param name="index">The physical projection index that executes the primitive.</param>
    /// <param name="criteriaKind">The primitive criteria kind to execute.</param>
    /// <param name="diagnostics">The diagnostics descriptor for the primitive route.</param>
    /// <param name="values">The validated primitive operands.</param>
    /// <returns>An executable identity criterion leaf.</returns>
    internal static IIdentityCriterion Leaf(string group, IIndex index, LibraDexCriteriaKind criteriaKind, LibraDexQueryDiagnostics diagnostics, params object?[] values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentNullException.ThrowIfNull(index);
        if (index.Group.Length != 0 && !string.Equals(index.Group, group, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The projection index does not belong to the requested identity group.");
        }

        return new LibraDexIdentityCriterion(
            group,
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
        if (options.Ordering == IdentityResultOrdering.PlanNatural)
        {
            LibraDexIdentityNodeExecution streamed = ExecutePlanNaturalWithStats(criterion, options);
            return new LibraDexIdentityExecutionResult(
                streamed.Identities,
                plan,
                new LibraDexQueryDiagnostics(
                    plan.Materialization == LibraDexIdentityPlanMaterialization.IdentitySet
                        ? LibraDexExecutionKind.Projection
                        : LibraDexExecutionKind.FastPath,
                    RowsScanned: streamed.RowsScanned,
                    RowsReturned: streamed.Identities.Count));
        }

        LibraDexIdentityNodeExecution execution = ExecuteNodeWithStats(criterion);
        List<object> identities = execution.Identities;
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
                RowsScanned: execution.RowsScanned,
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

    internal static LibraDexIdentityMutationResult ExecuteMutation(IIdentityCriterionMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        return mutation.Kind switch
        {
            LibraDexCriteriaMutationKind.Delete => ExecuteDeleteMutation(mutation.Criterion),
            LibraDexCriteriaMutationKind.SetKey => ExecuteSetKeyMutation(mutation),
            _ => throw new NotSupportedException($"Criteria mutation kind {mutation.Kind} is not supported.")
        };
    }

    internal static LibraDexIdentityMutationResult ExecuteTargetDelete(IIdentityCriterion criterion, IIndex targetIndex)
    {
        ArgumentNullException.ThrowIfNull(criterion);
        ArgumentNullException.ThrowIfNull(targetIndex);
        if (targetIndex is not IIdentityPrimitiveTupleExecutor tupleExecutor ||
            targetIndex is not IIdentityExactTupleMutator exactMutator)
        {
            throw new NotSupportedException("Targeted condition delete requires a target index that can capture and mutate exact physical tuples.");
        }

        List<object> matchedIdentities = ExecuteNode(criterion);
        if (matchedIdentities.Count == 0)
        {
            return new LibraDexIdentityMutationResult(
                LibraDexCriteriaMutationKind.Delete,
                MatchedCount: 0,
                ChangedCount: 0,
                new LibraDexQueryDiagnostics(LibraDexExecutionKind.FastPath, RowsScanned: 0, RowsReturned: 0));
        }

        IReadOnlyList<LibraDexObjectTuple> targetTuples = tupleExecutor.ExecuteTuplePrimitive(
            new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.All, Array.Empty<object?>()));
        long matchedTuples = 0;
        long changed = 0;
        for (int i = 0; i < targetTuples.Count; i++)
        {
            LibraDexObjectTuple tuple = targetTuples[i];
            if (!ContainsIdentity(matchedIdentities, tuple.Identity))
            {
                continue;
            }

            matchedTuples++;
            if (exactMutator.DeleteExactTuple(tuple.Key!, tuple.Identity))
            {
                changed++;
            }
        }

        return new LibraDexIdentityMutationResult(
            LibraDexCriteriaMutationKind.Delete,
            matchedTuples,
            changed,
            new LibraDexQueryDiagnostics(LibraDexExecutionKind.Scan, RowsScanned: targetTuples.Count, RowsReturned: changed));
    }

    internal static LibraDexIdentityMutationResult ExecuteTargetSetKey(
        IIdentityCriterion criterion,
        IIndex targetIndex,
        object? newKey,
        Func<object, object?>? newKeyFactory)
    {
        ArgumentNullException.ThrowIfNull(criterion);
        ArgumentNullException.ThrowIfNull(targetIndex);
        if (targetIndex is not IIdentityPrimitiveTupleExecutor tupleExecutor ||
            targetIndex is not IIdentityExactTupleMutator exactMutator)
        {
            throw new NotSupportedException("Targeted condition SetKey requires a target index that can capture and mutate exact physical tuples.");
        }

        List<object> matchedIdentities = ExecuteNode(criterion);
        if (matchedIdentities.Count == 0)
        {
            return new LibraDexIdentityMutationResult(
                LibraDexCriteriaMutationKind.SetKey,
                MatchedCount: 0,
                ChangedCount: 0,
                new LibraDexQueryDiagnostics(LibraDexExecutionKind.FastPath, RowsScanned: 0, RowsReturned: 0));
        }

        IReadOnlyList<LibraDexObjectTuple> targetTuples = tupleExecutor.ExecuteTuplePrimitive(
            new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.All, Array.Empty<object?>()));
        List<LibraDexObjectTuple> oldTuples = new();
        List<LibraDexObjectTuple> replacementTuples = new();
        for (int i = 0; i < targetTuples.Count; i++)
        {
            LibraDexObjectTuple tuple = targetTuples[i];
            if (!ContainsIdentity(matchedIdentities, tuple.Identity))
            {
                continue;
            }

            object replacementKey = newKeyFactory is null
                ? newKey ?? throw new InvalidOperationException("Targeted SetKey mutation is missing a replacement key.")
                : newKeyFactory(tuple.Identity) ?? throw new InvalidOperationException("Targeted SetKey replacement-key factory returned null.");
            oldTuples.Add(tuple);
            replacementTuples.Add(new LibraDexObjectTuple(replacementKey, tuple.Identity));
        }

        for (int i = 0; i < replacementTuples.Count; i++)
        {
            LibraDexObjectTuple oldTuple = oldTuples[i];
            LibraDexObjectTuple replacement = replacementTuples[i];
            if (LibraDexObjectTuple.ValueEquals(oldTuple.Key, replacement.Key))
            {
                continue;
            }

            if (!exactMutator.ContainsExactTuple(replacement.Key!, replacement.Identity))
            {
                LibraDexGenericInsertResult insert = targetIndex.Insert(replacement.Key!, replacement.Identity);
                if (!insert.Inserted && !exactMutator.ContainsExactTuple(replacement.Key!, replacement.Identity))
                {
                    throw new InvalidOperationException("Targeted SetKey could not create a replacement tuple; original tuples were left unchanged.");
                }
            }
        }

        long changed = 0;
        for (int i = 0; i < oldTuples.Count; i++)
        {
            LibraDexObjectTuple oldTuple = oldTuples[i];
            LibraDexObjectTuple replacement = replacementTuples[i];
            if (LibraDexObjectTuple.ValueEquals(oldTuple.Key, replacement.Key))
            {
                continue;
            }

            if (exactMutator.DeleteExactTuple(oldTuple.Key!, oldTuple.Identity))
            {
                changed++;
            }
        }

        return new LibraDexIdentityMutationResult(
            LibraDexCriteriaMutationKind.SetKey,
            oldTuples.Count,
            changed,
            new LibraDexQueryDiagnostics(LibraDexExecutionKind.Scan, RowsScanned: targetTuples.Count, RowsReturned: changed));
    }

    private static LibraDexIdentityMutationResult ExecuteDeleteMutation(IIdentityCriterion criterion)
    {
        if (criterion.NodeKind != LibraDexIdentityCriterionNodeKind.Leaf ||
            criterion.CriteriaKind is null ||
            criterion.Index is not IIdentityPrimitiveMutator primitiveMutator)
        {
            throw new NotSupportedException("Criteria-scoped delete is currently connected only for a single primitive leaf whose index implements physical tuple deletion.");
        }

        return primitiveMutator.DeleteIdentityPrimitive(
            new LibraDexIdentityPrimitiveRequest(criterion.CriteriaKind.Value, criterion.Values));
    }

    private static LibraDexIdentityMutationResult ExecuteSetKeyMutation(IIdentityCriterionMutation mutation)
    {
        IIdentityCriterion criterion = mutation.Criterion;
        if (criterion.NodeKind != LibraDexIdentityCriterionNodeKind.Leaf ||
            criterion.CriteriaKind is null ||
            criterion.Index is not IIdentityPrimitiveTupleExecutor tupleExecutor ||
            criterion.Index is not IIdentityExactTupleMutator exactMutator)
        {
            throw new NotSupportedException("Criteria-scoped SetKey is currently connected only for a single primitive leaf whose index can capture and mutate exact physical tuples.");
        }

        IReadOnlyList<LibraDexObjectTuple> tuples = tupleExecutor.ExecuteTuplePrimitive(
            new LibraDexIdentityPrimitiveRequest(criterion.CriteriaKind.Value, criterion.Values));
        long changed = 0;
        List<LibraDexObjectTuple> replacements = new(tuples.Count);
        for (int i = 0; i < tuples.Count; i++)
        {
            LibraDexObjectTuple tuple = tuples[i];
            object newKey = mutation.NewKeyFactory is null
                ? mutation.NewKey ?? throw new InvalidOperationException("SetKey mutation is missing a replacement key.")
                : mutation.NewKeyFactory(tuple.Identity) ?? throw new InvalidOperationException("SetKey replacement-key factory returned null.");

            replacements.Add(new LibraDexObjectTuple(newKey, tuple.Identity));
            if (LibraDexObjectTuple.ValueEquals(tuple.Key, newKey))
            {
                continue;
            }

            if (!exactMutator.ContainsExactTuple(newKey, tuple.Identity))
            {
                LibraDexGenericInsertResult insert = criterion.Index.Insert(newKey, tuple.Identity);
                if (!insert.Inserted && !exactMutator.ContainsExactTuple(newKey, tuple.Identity))
                {
                    throw new InvalidOperationException("SetKey could not create the replacement tuple; the original tuple was left unchanged.");
                }
            }
        }

        for (int i = 0; i < tuples.Count; i++)
        {
            LibraDexObjectTuple oldTuple = tuples[i];
            LibraDexObjectTuple replacement = replacements[i];
            if (LibraDexObjectTuple.ValueEquals(oldTuple.Key, replacement.Key))
            {
                continue;
            }

            if (exactMutator.DeleteExactTuple(oldTuple.Key!, oldTuple.Identity))
            {
                changed++;
            }
        }

        return new LibraDexIdentityMutationResult(
            LibraDexCriteriaMutationKind.SetKey,
            tuples.Count,
            changed,
            new LibraDexQueryDiagnostics(LibraDexExecutionKind.FastPath, RowsScanned: tuples.Count, RowsReturned: changed));
    }

    private static List<object> ExecuteNode(IIdentityCriterion criterion)
    {
        return ExecuteNodeWithStats(criterion).Identities;
    }

    private static bool ContainsIdentity(IReadOnlyList<object> identities, object candidate)
    {
        for (int i = 0; i < identities.Count; i++)
        {
            if (LibraDexObjectTuple.ValueEquals(identities[i], candidate))
            {
                return true;
            }
        }

        return false;
    }

    private static LibraDexIdentityNodeExecution ExecuteNodeWithStats(IIdentityCriterion criterion)
    {
        return criterion.NodeKind switch
        {
            LibraDexIdentityCriterionNodeKind.Leaf => ExecuteLeafWithStats(criterion),
            LibraDexIdentityCriterionNodeKind.And => ExecuteIntersectionWithStats(RequireLeft(criterion), RequireRight(criterion)),
            LibraDexIdentityCriterionNodeKind.Or => ExecuteUnionWithStats(RequireLeft(criterion), RequireRight(criterion)),
            LibraDexIdentityCriterionNodeKind.Except => ExecuteDifferenceWithStats(RequireLeft(criterion), RequireRight(criterion)),
            LibraDexIdentityCriterionNodeKind.Not => ExecuteComplementWithStats(criterion, RequireLeft(criterion)),
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

    private static LibraDexIdentityNodeExecution ExecuteLeafWithStats(IIdentityCriterion criterion)
    {
        List<object> identities = ExecuteLeaf(criterion);
        return new LibraDexIdentityNodeExecution(identities, identities.Count);
    }

    /// <summary>
    /// Executes a plan-natural identity projection through streaming primitives and materializes only the requested page.<br/>
    /// Ordered projections still use the full materialization path because sorting requires the complete result set.<br/>
    /// </summary>
    /// <param name="criterion">The materialized identity criterion tree.</param>
    /// <param name="options">The projection options controlling de-duplication and paging.</param>
    /// <returns>The materialized page and observed scan count.</returns>
    private static LibraDexIdentityNodeExecution ExecutePlanNaturalWithStats(IIdentityCriterion criterion, LibraDexIdentityQueryOptions options)
    {
        if (TryExecuteLeafPreserveWithTake(criterion, options, out IReadOnlyList<object>? leafIdentities))
        {
            IReadOnlyList<object> takenIdentities = leafIdentities ?? Array.Empty<object>();
            return new LibraDexIdentityNodeExecution(takenIdentities.ToList(), takenIdentities.Count);
        }

        IEnumerable<object> identities = IterateNode(criterion);
        if (options.Deduplication == IdentityDeduplication.Distinct)
        {
            identities = DistinctIterator(identities);
        }

        return MaterializeStreamingPage(identities, options);
    }

    private static LibraDexIdentityNodeExecution ExecuteIntersectionWithStats(IIdentityCriterion leftCriterion, IIdentityCriterion rightCriterion)
    {
        LibraDexIdentityNodeExecution left = ExecuteNodeWithStats(leftCriterion);
        LibraDexIdentityNodeExecution right = ExecuteNodeWithStats(rightCriterion);
        return new LibraDexIdentityNodeExecution(
            Intersect(left.Identities, right.Identities),
            left.RowsScanned + right.RowsScanned);
    }

    private static LibraDexIdentityNodeExecution ExecuteUnionWithStats(IIdentityCriterion leftCriterion, IIdentityCriterion rightCriterion)
    {
        LibraDexIdentityNodeExecution left = ExecuteNodeWithStats(leftCriterion);
        LibraDexIdentityNodeExecution right = ExecuteNodeWithStats(rightCriterion);
        return new LibraDexIdentityNodeExecution(
            Union(left.Identities, right.Identities),
            left.RowsScanned + right.RowsScanned);
    }

    private static LibraDexIdentityNodeExecution ExecuteDifferenceWithStats(IIdentityCriterion leftCriterion, IIdentityCriterion rightCriterion)
    {
        LibraDexIdentityNodeExecution left = ExecuteNodeWithStats(leftCriterion);
        LibraDexIdentityNodeExecution right = ExecuteNodeWithStats(rightCriterion);
        return new LibraDexIdentityNodeExecution(
            Except(left.Identities, right.Identities),
            left.RowsScanned + right.RowsScanned);
    }

    private static LibraDexIdentityNodeExecution ExecuteComplementWithStats(IIdentityCriterion criterion, IIdentityCriterion childCriterion)
    {
        LibraDexIdentityNodeExecution child = ExecuteNodeWithStats(childCriterion);
        LibraDexIdentityUniverse universe = MaterializeUniverse(criterion);
        return new LibraDexIdentityNodeExecution(
            Except(universe.Identities, child.Identities),
            child.RowsScanned + universe.Identities.Count);
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
    /// Executes a single preserve-duplicates leaf with the primitive take limit when paging makes that safe.<br/>
    /// Distinct projections cannot use this shortcut because raw primitive rows may collapse to fewer identities after de-duplication.<br/>
    /// </summary>
    /// <param name="criterion">The candidate leaf criterion.</param>
    /// <param name="options">The projection options containing skip, take, and bookmark state.</param>
    /// <param name="identities">Receives the requested paged identities when the shortcut applies.</param>
    /// <returns><see langword="true"/> when the shortcut handled the request.</returns>
    private static bool TryExecuteLeafPreserveWithTake(
        IIdentityCriterion criterion,
        LibraDexIdentityQueryOptions options,
        out IReadOnlyList<object>? identities)
    {
        identities = null;
        if (options.Deduplication != IdentityDeduplication.Preserve ||
            options.TakeCount is not { } takeCount)
        {
            return false;
        }

        long start = options.SkipCount;
        if (options.Bookmark is { } bookmark)
        {
            start = Math.Max(start, bookmark.Position);
        }

        long requested = start + takeCount;
        if (requested > int.MaxValue)
        {
            return false;
        }

        if (!TryExecuteLeafWithTake(criterion, checked((int)requested), out IReadOnlyList<object>? rawIdentities))
        {
            return false;
        }

        identities = ApplyPaging(rawIdentities!.ToList(), options);
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
        return Except(MaterializeUniverse(criterion).Identities, excluded);
    }

    private static LibraDexIdentityUniverse MaterializeUniverse(IIdentityCriterion criterion)
    {
        IIdentityCriterion universeRoot = criterion.NodeKind == LibraDexIdentityCriterionNodeKind.Not && criterion.Left is not null
            ? criterion.Left
            : criterion;
        if (TryResolveSingleExecutableUniverse(universeRoot, out IIdentityPrimitiveExecutor? singleIndexExecutor))
        {
            return new LibraDexIdentityUniverse(
                singleIndexExecutor.IterateIdentityPrimitive(
                    new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.All, Array.Empty<object?>())).ToList());
        }

        IIdentityCriterion leaf = FindFirstLeaf(criterion);
        if (leaf.Index is not IIdentityPrimitiveExecutor executor)
        {
            throw new NotSupportedException("Negated identity criteria require at least one executable index to provide the identity universe.");
        }

        return new LibraDexIdentityUniverse(executor.IterateIdentityUniverse().ToList());
    }

    /// <summary>
    /// Resolves whether a negated criterion subtree is scoped to one executable physical index.<br/>
    /// Single-index exclusions such as `NotInSet`, `YearNotIn`, and same-index disjunction complements must enumerate that index's own `All` primitive rather than the broader identity-group universe.<br/>
    /// Multi-index negations intentionally return <see langword="false"/> so the existing identity-group universe path remains available for criteria whose meaning crosses indexes.<br/>
    /// </summary>
    /// <param name="criterion">The criterion subtree that defines the complement scope.</param>
    /// <param name="executor">The single executable index when all leaves share one executor; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when every executable leaf in the subtree uses the same index executor.</returns>
    private static bool TryResolveSingleExecutableUniverse(IIdentityCriterion criterion, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IIdentityPrimitiveExecutor? executor)
    {
        executor = null;
        return TryResolveSingleExecutableUniverseCore(criterion, ref executor) && executor is not null;
    }

    /// <summary>
    /// Walks a criterion tree and verifies that all executable leaves share one index executor.<br/>
    /// The method uses reference identity because condition materialization binds leaves to concrete opened index handles; projection-specific handles are treated as distinct execution scopes unless a later planner deliberately groups them.<br/>
    /// </summary>
    /// <param name="criterion">The current criterion node.</param>
    /// <param name="executor">The first executable leaf encountered, reused as the equality anchor.</param>
    /// <returns><see langword="true"/> when no conflicting executable leaf is found.</returns>
    private static bool TryResolveSingleExecutableUniverseCore(IIdentityCriterion criterion, ref IIdentityPrimitiveExecutor? executor)
    {
        if (criterion.NodeKind == LibraDexIdentityCriterionNodeKind.Leaf)
        {
            if (criterion.Index is not IIdentityPrimitiveExecutor primitiveExecutor)
            {
                return false;
            }

            if (executor is null)
            {
                executor = primitiveExecutor;
                return true;
            }

            return ReferenceEquals(executor, primitiveExecutor);
        }

        if (criterion.Left is not null && !TryResolveSingleExecutableUniverseCore(criterion.Left, ref executor))
        {
            return false;
        }

        return criterion.Right is null || TryResolveSingleExecutableUniverseCore(criterion.Right, ref executor);
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
        IEnumerable<object> universe;
        if (TryResolveSingleExecutableUniverse(criterion, out IIdentityPrimitiveExecutor? singleIndexExecutor))
        {
            universe = singleIndexExecutor.IterateIdentityPrimitive(
                new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.All, Array.Empty<object?>()));
        }
        else
        {
            IIdentityCriterion leaf = FindFirstLeaf(criterion);
            if (leaf.Index is not IIdentityPrimitiveExecutor executor)
            {
                throw new NotSupportedException("Negated identity criteria require at least one executable index to provide the identity universe.");
            }

            universe = executor.IterateIdentityUniverse();
        }

        HashSet<object> excludedSet = new(excluded);
        foreach (object identity in universe)
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

    /// <summary>
    /// Materializes a streamed identity sequence only until the requested page has been satisfied.<br/>
    /// This keeps `take` and bookmark projections from forcing full result materialization when natural plan order is acceptable.<br/>
    /// </summary>
    /// <param name="identities">The streamed identity sequence.</param>
    /// <param name="options">The projection options controlling skip, take, and bookmark state.</param>
    /// <returns>The materialized page and number of streamed identities consumed.</returns>
    private static LibraDexIdentityNodeExecution MaterializeStreamingPage(IEnumerable<object> identities, LibraDexIdentityQueryOptions options)
    {
        long skip = options.SkipCount;
        if (options.Bookmark is { } bookmark)
        {
            skip = Math.Max(skip, bookmark.Position);
        }

        if (options.TakeCount == 0)
        {
            return new LibraDexIdentityNodeExecution(new List<object>(), RowsScanned: 0);
        }

        int capacity = options.TakeCount is { } takeCount ? takeCount : 0;
        List<object> result = capacity > 0 ? new List<object>(capacity) : new List<object>();
        long scanned = 0;
        foreach (object identity in identities)
        {
            scanned++;
            if (skip > 0)
            {
                skip--;
                continue;
            }

            result.Add(identity);
            if (options.TakeCount is { } limit && result.Count >= limit)
            {
                break;
            }
        }

        return new LibraDexIdentityNodeExecution(result, scanned);
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

    private readonly record struct LibraDexIdentityNodeExecution(List<object> Identities, long RowsScanned);

    private readonly record struct LibraDexIdentityUniverse(List<object> Identities);
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

    public LibraDexIdentityMutationResult Execute()
    {
        return LibraDexIdentityExecutionPlanner.ExecuteMutation(this);
    }
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
    IIdentityCriterionMutation SetKey(object newKey);

    /// <summary>
    /// Captures set-key intent with a replacement-key factory.<br/>
    /// </summary>
    /// <param name="newKeyFactory">Factory that receives a matched identity and returns its replacement key.</param>
    /// <returns>A mutation descriptor.</returns>
    IIdentityCriterionMutation SetKey(Func<object, object?> newKeyFactory);
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
            current.RouteChanges - snapshot.RouteChanges,
            current.ReclaimedPayloadCellsRecorded - snapshot.ReclaimedPayloadCellsRecorded,
            current.ReclaimedPayloadCellsConsumed - snapshot.ReclaimedPayloadCellsConsumed);
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
