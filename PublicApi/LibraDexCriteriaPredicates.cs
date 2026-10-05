using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LibraDex;

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
    NotMatchesInSet = 17,
    RegexMatches = 18,
    NotRegexMatches = 19,
    NotMatchesPattern = 20,
    NotStartsWith = 21,
    NotEndsWith = 22,
    NotContains = 23
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
    private readonly LibraDexTextNormalization textNormalization;
    private readonly int? matchGroupNumber;
    private readonly Regex? regex;
    private readonly LibraDexWildcardPattern? wildcard;

    private LibraDexStringPatternPredicate(
        LibraDexStringPatternMode mode,
        string value,
        string? upperValue,
        IReadOnlyCollection<string>? setValues,
        ISet<string>? membershipSet,
        LibraDexStringComparisonPolicy policy,
        int? matchGroupNumber,
        Regex? regex,
        LibraDexTextNormalization textNormalization = LibraDexTextNormalization.None,
        LibraDexWildcardPattern? wildcard = null)
    {
        this.mode = mode;
        this.value = value;
        this.upperValue = upperValue;
        this.setValues = setValues;
        this.membershipSet = membershipSet;
        this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
        this.textNormalization = textNormalization;
        this.matchGroupNumber = matchGroupNumber;
        this.regex = regex;
        this.wildcard = wildcard;
    }

    internal static LibraDexStringPatternPredicate Create(LibraDexStringPatternMode mode, string value, bool ignoreCase, string? culture)
        => Create(mode, value, LibraDexStringComparisonPolicy.FromLegacy(ignoreCase, culture));

    internal static LibraDexStringPatternPredicate Create(
        LibraDexStringPatternMode mode,
        string value,
        LibraDexStringComparisonPolicy policy,
        LibraDexTextNormalization textNormalization = LibraDexTextNormalization.None)
    {
        ArgumentNullException.ThrowIfNull(value);
        string prepared = PrepareForComparison(value, policy, textNormalization);
        LibraDexWildcardPattern? wildcard = mode is LibraDexStringPatternMode.MatchesPattern or LibraDexStringPatternMode.NotMatchesPattern
            ? LibraDexWildcardPattern.Create(prepared, policy)
            : null;
        return new LibraDexStringPatternPredicate(mode, prepared, null, null, null, policy, matchGroupNumber: null, regex: null, textNormalization, wildcard);
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
        => Create(mode, value, upperValue, LibraDexStringComparisonPolicy.FromLegacy(ignoreCase, culture));

    internal static LibraDexStringPatternPredicate Create(
        LibraDexStringPatternMode mode,
        string value,
        string upperValue,
        LibraDexStringComparisonPolicy policy,
        LibraDexTextNormalization textNormalization = LibraDexTextNormalization.None)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(upperValue);
        return new LibraDexStringPatternPredicate(
            mode,
            PrepareForComparison(value, policy, textNormalization),
            PrepareForComparison(upperValue, policy, textNormalization),
            null,
            null,
            policy,
            matchGroupNumber: null,
            regex: null,
            textNormalization);
    }

    /// <summary>
    /// Creates a boolean regular-expression predicate over one string key.<br/>
    /// String patterns are compiled by the executor with comparison-policy options, allowing the condition builder to keep the low-friction `ignoreCase` flag while still using regex semantics.<br/>
    /// </summary>
    /// <param name="mode">Whether the regex match is positive or negated.</param>
    /// <param name="pattern">The regular expression pattern.</param>
    /// <param name="policy">The comparison policy used to derive regex options for string patterns.</param>
    /// <param name="textNormalization">The text-normalization policy used for string predicate evaluation.<br/></param>
    /// <returns>A compiled string predicate descriptor for the exact-index executor.</returns>
    internal static LibraDexStringPatternPredicate CreateRegex(
        LibraDexStringPatternMode mode,
        string pattern,
        LibraDexStringComparisonPolicy policy,
        LibraDexTextNormalization textNormalization = LibraDexTextNormalization.None)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(policy);
        Regex regex = new(
            pattern,
            CreateRegexOptions(policy.CompareOptions) | RegexOptions.Compiled);
        return new LibraDexStringPatternPredicate(mode, pattern, null, null, null, policy, matchGroupNumber: null, regex, textNormalization);
    }

    /// <summary>
    /// Creates a boolean regular-expression predicate over one string key from a caller-provided <see cref="Regex"/> instance.<br/>
    /// The supplied regex is reused directly so caller-selected options, timeout, and compiled/interpreted behavior are preserved without reparsing the pattern.<br/>
    /// </summary>
    /// <param name="mode">Whether the regex match is positive or negated.</param>
    /// <param name="regex">The caller-provided regular expression instance.</param>
    /// <param name="policy">The comparison policy used only by surrounding condition metadata; regex matching uses the supplied instance options.</param>
    /// <param name="textNormalization">The text-normalization policy used for string predicate evaluation.<br/></param>
    /// <returns>A compiled string predicate descriptor for the exact-index executor.</returns>
    internal static LibraDexStringPatternPredicate CreateRegex(
        LibraDexStringPatternMode mode,
        Regex regex,
        LibraDexStringComparisonPolicy policy,
        LibraDexTextNormalization textNormalization = LibraDexTextNormalization.None)
    {
        ArgumentNullException.ThrowIfNull(regex);
        ArgumentNullException.ThrowIfNull(policy);
        return new LibraDexStringPatternPredicate(mode, regex.ToString(), null, null, null, policy, matchGroupNumber: null, regex, textNormalization);
    }

    /// <summary>
    /// Creates a regex capture predicate that compares the selected regex match text or numbered capture group to one expected value.<br/>
    /// Group zero is the whole match and matches the behavior of <c>Match.Value</c>; positive group numbers compare <c>Group.Value</c> for that group.<br/>
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
        ArgumentNullException.ThrowIfNull(policy);
        ValidateRegexGroupNumber(groupNumber);
        Regex regex = new(
            pattern,
            CreateRegexOptions(policy.CompareOptions) | RegexOptions.Compiled);
        return new LibraDexStringPatternPredicate(mode, pattern, expectedValue, null, null, policy, groupNumber, regex);
    }

    /// <summary>
    /// Creates a regex capture predicate from a caller-provided <see cref="Regex"/> instance.<br/>
    /// The regex object is reused for candidate matching, while the selected match or capture text is compared with the supplied string comparison policy.<br/>
    /// </summary>
    /// <param name="mode">Whether the comparison is positive or negated.</param>
    /// <param name="regex">The caller-provided regular expression instance.</param>
    /// <param name="expectedValue">The expected match or group value.</param>
    /// <param name="groupNumber">The optional group number; null and zero both mean the whole match.</param>
    /// <param name="policy">The string comparison policy for comparing captured text to the expected value.</param>
    /// <returns>A compiled string predicate descriptor for the exact-index executor.</returns>
    internal static LibraDexStringPatternPredicate CreateRegexCapture(
        LibraDexStringPatternMode mode,
        Regex regex,
        string expectedValue,
        int? groupNumber,
        LibraDexStringComparisonPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(regex);
        ArgumentNullException.ThrowIfNull(expectedValue);
        ValidateRegexGroupNumber(groupNumber);
        return new LibraDexStringPatternPredicate(mode, regex.ToString(), expectedValue, null, null, policy, groupNumber, regex);
    }

    /// <summary>
    /// Creates a regex capture predicate that compares the selected regex match text or numbered capture group to a value set.<br/>
    /// Group zero is the whole match and matches the behavior of <c>Match.Value</c>; positive group numbers compare <c>Group.Value</c> for that group.<br/>
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

        Regex regex = new(
            pattern,
            CreateRegexOptions(policy.CompareOptions) | RegexOptions.Compiled);
        return new LibraDexStringPatternPredicate(mode, pattern, null, prepared, prepared, policy, groupNumber, regex);
    }

    /// <summary>
    /// Creates a regex capture membership predicate from a caller-provided <see cref="Regex"/> instance.<br/>
    /// The regex object is reused for candidate matching, while captured text membership uses the prepared policy-compatible value set.<br/>
    /// </summary>
    /// <param name="mode">Whether the comparison is positive or negated.</param>
    /// <param name="regex">The caller-provided regular expression instance.</param>
    /// <param name="expectedValues">The expected match or group values.</param>
    /// <param name="groupNumber">The optional group number; null and zero both mean the whole match.</param>
    /// <param name="policy">The string comparison policy for comparing captured text to the expected values.</param>
    /// <returns>A compiled string predicate descriptor for the exact-index executor.</returns>
    internal static LibraDexStringPatternPredicate CreateRegexCaptureSet(
        LibraDexStringPatternMode mode,
        Regex regex,
        IEnumerable<string> expectedValues,
        int? groupNumber,
        LibraDexStringComparisonPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(regex);
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

        return new LibraDexStringPatternPredicate(mode, regex.ToString(), null, prepared, prepared, policy, groupNumber, regex);
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
        => CreateSet(mode, values, LibraDexStringComparisonPolicy.FromLegacy(ignoreCase, culture));

    internal static LibraDexStringPatternPredicate CreateSet(
        LibraDexStringPatternMode mode,
        IEnumerable<string> values,
        LibraDexStringComparisonPolicy policy,
        LibraDexTextNormalization textNormalization = LibraDexTextNormalization.None)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(policy);
        ISet<string>? membershipSet = null;
        IReadOnlyCollection<string> captured;
        if (textNormalization == LibraDexTextNormalization.None &&
            values is HashSet<string> hashSet &&
            policy.IsCompatible(hashSet.Comparer))
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
                prepared.Add(PrepareForComparison(value, policy, textNormalization));
            }

            membershipSet = prepared;
            captured = prepared;
        }

        if (captured.Count == 0)
        {
            throw new ArgumentException("String membership predicates require at least one value.", nameof(values));
        }

        return new LibraDexStringPatternPredicate(mode, captured.First(), null, captured, membershipSet, policy, matchGroupNumber: null, regex: null, textNormalization);
    }

    internal IReadOnlyList<(string Lower, string Upper)> CreateCandidateRanges()
    {
        if (textNormalization != LibraDexTextNormalization.None)
        {
            return Array.Empty<(string Lower, string Upper)>();
        }

        // Culture folding is not confined to ASCII upper/lower pairs.  Unicode
        // characters such as the Kelvin sign can fold onto an ordinal prefix whose
        // original exact bytes occupy a completely different routed range.  A
        // folded-ordinal predicate therefore scans the complete exact key space
        // unless a maintained folded projection is selected before this fallback.
        if (policy.Kind == LibraDexStringComparisonPolicyKind.FoldedOrdinal)
        {
            return Array.Empty<(string Lower, string Upper)>();
        }

        // An ignore-case Regex can match text whose ordinal UTF-8 prefix differs from the
        // pattern's literal prefix (for example, Turkish "i" and "İ").  Exact indexes are
        // ordered by the stored bytes, so narrowing that regex to literal-prefix shelves
        // would discard valid candidates before the preserved Regex evaluates them.
        // Scan the complete exact-key space for semantic safety; case-sensitive regexes
        // can continue using their anchored literal prefix as an ordinal candidate range.
        if (mode == LibraDexStringPatternMode.RegexMatches &&
            regex is not null &&
            (regex.Options & RegexOptions.IgnoreCase) != 0)
        {
            return Array.Empty<(string Lower, string Upper)>();
        }

        bool ignoreCase = policy.IgnoreCase;
        string prefix = mode switch
        {
            LibraDexStringPatternMode.MatchesPattern => wildcard?.LeadingLiteral ?? string.Empty,
            LibraDexStringPatternMode.NotMatchesPattern => string.Empty,
            LibraDexStringPatternMode.RegexMatches => GetAnchoredRegexLiteralPrefix(value),
            LibraDexStringPatternMode.NotRegexMatches => string.Empty,
            _ => value
        };
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
            mode != LibraDexStringPatternMode.MatchesPattern &&
            mode != LibraDexStringPatternMode.RegexMatches)
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
        if (TryMatchInvariantAsciiFoldedPattern(candidate, out bool asciiMatch))
        {
            return asciiMatch;
        }

        candidate = PrepareForComparison(candidate, policy, textNormalization);
        CompareInfo compareInfo = policy.ResolveCulture().CompareInfo;
        CompareOptions options = policy.CompareOptions;
        bool foldedOrdinal = policy.Kind == LibraDexStringComparisonPolicyKind.FoldedOrdinal;
        return mode switch
        {
            LibraDexStringPatternMode.StartsWith => foldedOrdinal ? candidate.StartsWith(value, StringComparison.Ordinal) : compareInfo.IsPrefix(candidate, value, options),
            LibraDexStringPatternMode.EndsWith => foldedOrdinal ? candidate.EndsWith(value, StringComparison.Ordinal) : compareInfo.IsSuffix(candidate, value, options),
            LibraDexStringPatternMode.Contains => foldedOrdinal ? candidate.Contains(value, StringComparison.Ordinal) : compareInfo.IndexOf(candidate, value, options) >= 0,
            LibraDexStringPatternMode.NotStartsWith => foldedOrdinal ? !candidate.StartsWith(value, StringComparison.Ordinal) : !compareInfo.IsPrefix(candidate, value, options),
            LibraDexStringPatternMode.NotEndsWith => foldedOrdinal ? !candidate.EndsWith(value, StringComparison.Ordinal) : !compareInfo.IsSuffix(candidate, value, options),
            LibraDexStringPatternMode.NotContains => foldedOrdinal ? !candidate.Contains(value, StringComparison.Ordinal) : compareInfo.IndexOf(candidate, value, options) < 0,
            LibraDexStringPatternMode.MatchesPattern => wildcard is not null && (foldedOrdinal ? wildcard.MatchesOrdinal(candidate) : wildcard.Matches(candidate, compareInfo, options)),
            LibraDexStringPatternMode.NotMatchesPattern => wildcard is null || !(foldedOrdinal ? wildcard.MatchesOrdinal(candidate) : wildcard.Matches(candidate, compareInfo, options)),
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
            LibraDexStringPatternMode.RegexMatches => MatchesRegex(candidate, options),
            LibraDexStringPatternMode.NotRegexMatches => !MatchesRegex(candidate, options),
            _ => false
        };
    }

    /// <summary>
    /// Attempts to evaluate one UTF-8 index-key payload through the retained compiled regular expression without allocating a managed string per key.<br/>
    /// The method applies only to direct positive or negated regex predicates with no canonical text normalization; all culture, normalization, capture, wildcard, and non-regex shapes fail closed to the established managed-string path.<br/>
    /// Small decoded keys use stack storage, larger keys rent one temporary character buffer, and the supplied UTF-8 bytes are never retained after the call.<br/>
    /// </summary>
    /// <param name="utf8Candidate">UTF-8 bytes for one logical string candidate, excluding LibraDex's key-state marker.<br/></param>
    /// <param name="matches">Receives the complete regex result when this method returns <see langword="true"/>.<br/></param>
    /// <returns><see langword="true"/> when span-native regex evaluation preserved the complete predicate semantics; otherwise <see langword="false"/> so the caller can use managed-string evaluation.<br/></returns>
    internal bool TryMatchUtf8Regex(ReadOnlySpan<byte> utf8Candidate, out bool matches)
    {
        matches = false;
        if (regex is null ||
            textNormalization != LibraDexTextNormalization.None ||
            mode is not (LibraDexStringPatternMode.RegexMatches or LibraDexStringPatternMode.NotRegexMatches))
        {
            return false;
        }

        const int StackCharacterLimit = 512;
        int characterCount = Encoding.UTF8.GetCharCount(utf8Candidate);
        char[]? rented = null;
        Span<char> characters = characterCount <= StackCharacterLimit
            ? stackalloc char[characterCount]
            : (rented = ArrayPool<char>.Shared.Rent(characterCount));
        try
        {
            int written = Encoding.UTF8.GetChars(utf8Candidate, characters);
            bool regexMatch = regex.IsMatch(characters[..written]);
            matches = mode == LibraDexStringPatternMode.RegexMatches
                ? regexMatch
                : !regexMatch;
            return true;
        }
        finally
        {
            if (rented is not null)
                ArrayPool<char>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Creates the execution-local predicate instance used by one dedicated parallel index worker.<br/>
    /// Non-regex predicates are immutable and safely reuse this instance; regex predicates clone only the <see cref="Regex"/> runner owner while sharing immutable operands, comparison policy, membership data, and wildcard metadata.<br/>
    /// This prevents concurrent workers from exhausting one compiled regex's small internal runner cache and allocating replacement runners throughout a large key scan, while preserving the original pattern, options, and timeout exactly.<br/>
    /// </summary>
    /// <returns>This immutable predicate when it has no regex, or a semantically equivalent predicate with a worker-owned regex instance.<br/></returns>
    internal LibraDexStringPatternPredicate CreateParallelWorkerCopy()
    {
        if (regex is null)
            return this;

        Regex workerRegex = new(regex.ToString(), regex.Options, regex.MatchTimeout);
        return new LibraDexStringPatternPredicate(
            mode,
            value,
            upperValue,
            setValues,
            membershipSet,
            policy,
            matchGroupNumber,
            workerRegex,
            textNormalization,
            wildcard);
    }

    /// <summary>
    /// Attempts one allocation-free folded-ordinal pattern comparison for invariant ASCII operands and candidates.<br/>
    /// Invariant lower-case-plus-ordinal semantics are identical to ordinal-ignore-case for ASCII, so simple positive and negated pattern operators can avoid allocating a folded candidate string.<br/>
    /// Non-ASCII text, explicit cultures, canonical normalization, and complex pattern modes return <see langword="false"/> so the established managed preparation path remains authoritative.<br/>
    /// </summary>
    /// <param name="candidate">The decoded exact-index key before comparison preparation.<br/></param>
    /// <param name="matches">Receives the simple-pattern result when the fast path applies.<br/></param>
    /// <returns><see langword="true"/> when <paramref name="matches"/> contains the final result; otherwise <see langword="false"/>.<br/></returns>
    private bool TryMatchInvariantAsciiFoldedPattern(string candidate, out bool matches)
    {
        matches = false;
        if (policy.Kind != LibraDexStringComparisonPolicyKind.FoldedOrdinal ||
            !string.IsNullOrEmpty(policy.CultureName) ||
            textNormalization != LibraDexTextNormalization.None ||
            !IsAscii(value) ||
            !IsAscii(candidate))
        {
            return false;
        }

        switch (mode)
        {
            case LibraDexStringPatternMode.StartsWith:
                matches = candidate.StartsWith(value, StringComparison.OrdinalIgnoreCase);
                return true;
            case LibraDexStringPatternMode.EndsWith:
                matches = candidate.EndsWith(value, StringComparison.OrdinalIgnoreCase);
                return true;
            case LibraDexStringPatternMode.Contains:
                matches = candidate.Contains(value, StringComparison.OrdinalIgnoreCase);
                return true;
            case LibraDexStringPatternMode.NotStartsWith:
                matches = !candidate.StartsWith(value, StringComparison.OrdinalIgnoreCase);
                return true;
            case LibraDexStringPatternMode.NotEndsWith:
                matches = !candidate.EndsWith(value, StringComparison.OrdinalIgnoreCase);
                return true;
            case LibraDexStringPatternMode.NotContains:
                matches = !candidate.Contains(value, StringComparison.OrdinalIgnoreCase);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Determines whether a prepared operand or exact-index candidate contains only ASCII UTF-16 code units.<br/>
    /// The check is deliberately local and allocation-free because it runs only while considering the invariant folded-pattern fast path.<br/>
    /// </summary>
    /// <param name="source">The string to inspect.<br/></param>
    /// <returns><see langword="true"/> when the entire string is ASCII; otherwise <see langword="false"/>.<br/></returns>
    private static bool IsAscii(string source)
    {
        for (int index = 0; index < source.Length; index++)
        {
            if (source[index] > 0x7f)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Applies the selector-level normalization contract while returning already-normalized strings without allocation.<br/>
    /// </summary>
    /// <param name="source">The developer-facing or exact-index string.</param>
    /// <param name="normalization">The canonical normalization contract.</param>
    /// <returns>The normalized string, or the original instance when no transformation is required.</returns>
    private static string Normalize(string source, LibraDexTextNormalization normalization)
        => normalization == LibraDexTextNormalization.FormC && !source.IsNormalized(NormalizationForm.FormC)
            ? source.Normalize(NormalizationForm.FormC)
            : source;

    /// <summary>
    /// Applies selector-level canonical normalization followed by the selected residual comparison preparation.<br/>
    /// Folded-ordinal policies lower-case after Form-C normalization, matching the maintained folded projection's transform order while ordinary policies retain the previous allocation-free fast path.<br/>
    /// </summary>
    /// <param name="source">The developer-facing operand or decoded exact-index key.<br/></param>
    /// <param name="policy">The residual string comparison policy.<br/></param>
    /// <param name="normalization">Optional selector-level canonical normalization.<br/></param>
    /// <returns>The prepared comparison value.<br/></returns>
    private static string PrepareForComparison(
        string source,
        LibraDexStringComparisonPolicy policy,
        LibraDexTextNormalization normalization)
        => policy.PrepareForComparison(Normalize(source, normalization));

    /// <summary>
    /// Attempts to compile this string predicate into an ordinal UTF-8 byte predicate.<br/>
    /// The caller supplies the same operand transform used by the selected physical projection, allowing folded-text scans to compare already-folded key bytes against already-folded criteria bytes without decoding each candidate key.<br/>
    /// Predicates that require regex, wildcard, capture, custom managed comparison, or unnormalized ignore-case exact-index semantics return <see langword="false"/> so the executor can keep the existing managed string fallback.<br/>
    /// </summary>
    /// <param name="operandTransform">Transforms developer-facing operands into the bytes stored by the selected string projection.<br/></param>
    /// <param name="allowCaseNormalizedBytes">True when the selected projection already stores keys normalized for the predicate's case policy.<br/></param>
    /// <param name="matcher">Receives the compiled byte matcher when the predicate can execute over UTF-8 bytes.<br/></param>
    /// <returns><see langword="true"/> when byte-native residual comparison is safe for this predicate.<br/></returns>
    internal bool TryCreateUtf8ByteMatcher(
        Func<string?, string?> operandTransform,
        bool allowCaseNormalizedBytes,
        out LibraDexUtf8StringPatternPredicate? matcher)
    {
        ArgumentNullException.ThrowIfNull(operandTransform);
        matcher = null;
        if (TryCreateLiteralRegexUtf8ByteMatcher(operandTransform, allowCaseNormalizedBytes, out matcher))
            return true;

        bool requiresInvariantAsciiManagedFallback = CanUseInvariantAsciiExactKeyMatcher(allowCaseNormalizedBytes);
        if (!CanUseUtf8ByteMatcher(allowCaseNormalizedBytes) && !requiresInvariantAsciiManagedFallback)
        {
            return false;
        }

        byte[] expected = EncodeTransformedOperand(operandTransform, value);
        byte[]? upper = upperValue is null ? null : EncodeTransformedOperand(operandTransform, upperValue);
        byte[][]? set = null;
        if (setValues is not null)
        {
            set = new byte[setValues.Count][];
            int index = 0;
            foreach (string item in setValues)
            {
                set[index++] = EncodeTransformedOperand(operandTransform, item);
            }
        }

        matcher = new LibraDexUtf8StringPatternPredicate(
            mode,
            expected,
            upper,
            set,
            requiresInvariantAsciiManagedFallback);
        return true;
    }

    /// <summary>
    /// Attempts to reduce a semantically plain regular expression to an exact-index UTF-8 contains predicate.<br/>
    /// A pattern containing no regex metacharacters has the same existence semantics as substring search; case-sensitive literals can compare all UTF-8 keys directly, while culture-invariant ignore-case ASCII literals compare ASCII keys directly and defer Unicode keys to the retained regex.<br/>
    /// Folded projections, canonical normalization, culture-sensitive ignore-case behavior, ignore-pattern-whitespace, and every metacharacter-bearing pattern fail closed to normal regex evaluation.<br/>
    /// This optimization changes only candidate evaluation mechanics: empty/null key-state handling and the original regex remain authoritative wherever the byte matcher returns a managed-fallback request.<br/>
    /// </summary>
    /// <param name="operandTransform">The exact projection transform applied to the literal once.<br/></param>
    /// <param name="allowCaseNormalizedBytes">Whether the selected projection stores transformed case-normalized bytes.<br/></param>
    /// <param name="matcher">Receives the semantically equivalent UTF-8 contains matcher when the reduction is safe.<br/></param>
    /// <returns><see langword="true"/> only when the regex is a plain literal whose semantics are preserved by the returned matcher.<br/></returns>
    private bool TryCreateLiteralRegexUtf8ByteMatcher(
        Func<string?, string?> operandTransform,
        bool allowCaseNormalizedBytes,
        out LibraDexUtf8StringPatternPredicate? matcher)
    {
        matcher = null;
        if (allowCaseNormalizedBytes ||
            regex is null ||
            textNormalization != LibraDexTextNormalization.None ||
            mode is not (LibraDexStringPatternMode.RegexMatches or LibraDexStringPatternMode.NotRegexMatches) ||
            (regex.Options & RegexOptions.IgnorePatternWhitespace) != 0 ||
            value.IndexOfAny(['.', '$', '^', '{', '[', '(', '|', ')', '*', '+', '?', '\\']) >= 0)
        {
            return false;
        }

        bool ignoreCase = (regex.Options & RegexOptions.IgnoreCase) != 0;
        if (ignoreCase &&
            ((regex.Options & RegexOptions.CultureInvariant) == 0 || !IsAscii(value)))
        {
            return false;
        }

        matcher = new LibraDexUtf8StringPatternPredicate(
            mode == LibraDexStringPatternMode.RegexMatches
                ? LibraDexStringPatternMode.Contains
                : LibraDexStringPatternMode.NotContains,
            EncodeTransformedOperand(operandTransform, value),
            upperValue: null,
            setValues: null,
            requiresInvariantAsciiManagedFallback: ignoreCase);
        return true;
    }

    /// <summary>
    /// Determines whether an exact-key folded-ordinal predicate can compare ASCII key bytes while explicitly deferring Unicode keys to managed semantics.<br/>
    /// Only invariant, non-normalizing simple pattern operators qualify; explicit cultures and non-ASCII operands retain decoded-string evaluation for every key.<br/>
    /// </summary>
    /// <param name="allowCaseNormalizedBytes">Whether the selected physical projection already stores case-normalized bytes.<br/></param>
    /// <returns><see langword="true"/> when ASCII candidates can be decided byte-natively and other candidates require managed evaluation.<br/></returns>
    private bool CanUseInvariantAsciiExactKeyMatcher(bool allowCaseNormalizedBytes)
    {
        if (allowCaseNormalizedBytes ||
            textNormalization != LibraDexTextNormalization.None ||
            policy.Kind != LibraDexStringComparisonPolicyKind.FoldedOrdinal ||
            !string.IsNullOrEmpty(policy.CultureName) ||
            !IsAscii(value))
        {
            return false;
        }

        return mode is LibraDexStringPatternMode.StartsWith or
            LibraDexStringPatternMode.EndsWith or
            LibraDexStringPatternMode.Contains or
            LibraDexStringPatternMode.NotStartsWith or
            LibraDexStringPatternMode.NotEndsWith or
            LibraDexStringPatternMode.NotContains;
    }

    /// <summary>
    /// Determines whether this predicate's semantics can be represented by ordinal byte comparison over already-projected UTF-8 key bytes.<br/>
    /// Folded projection callers may permit ignore-case policies because both candidate and criteria bytes are normalized before comparison; exact-index callers must stay case-sensitive to avoid changing .NET comparison semantics.<br/>
    /// </summary>
    /// <param name="allowCaseNormalizedBytes">True when the selected projection normalizes case for both keys and operands.</param>
    /// <returns><see langword="true"/> when the byte matcher can preserve the intended comparison contract.</returns>
    private bool CanUseUtf8ByteMatcher(bool allowCaseNormalizedBytes)
    {
        if (textNormalization != LibraDexTextNormalization.None && !allowCaseNormalizedBytes)
        {
            return false;
        }

        if (mode is LibraDexStringPatternMode.MatchesPattern or
            LibraDexStringPatternMode.NotMatchesPattern or
            LibraDexStringPatternMode.RegexMatches or
            LibraDexStringPatternMode.NotRegexMatches or
            LibraDexStringPatternMode.MatchesWith or
            LibraDexStringPatternMode.NotMatchesWith or
            LibraDexStringPatternMode.MatchesInSet or
            LibraDexStringPatternMode.NotMatchesInSet)
        {
            return false;
        }

        if (policy.Kind == LibraDexStringComparisonPolicyKind.Custom)
        {
            return false;
        }

        if (!allowCaseNormalizedBytes && policy.Kind != LibraDexStringComparisonPolicyKind.Ordinal)
        {
            return false;
        }

        return mode is LibraDexStringPatternMode.StartsWith or
            LibraDexStringPatternMode.EndsWith or
            LibraDexStringPatternMode.Contains or
            LibraDexStringPatternMode.EqualTo or
            LibraDexStringPatternMode.NotEqualTo or
            LibraDexStringPatternMode.InSet or
            LibraDexStringPatternMode.NotInSet;
    }

    /// <summary>
    /// Converts one transformed string operand to UTF-8 bytes for byte-native residual matching.<br/>
    /// Null operands cannot participate in ordinary string-pattern residuals because null and empty strings are stored through key-state routes outside the var-key scan path.<br/>
    /// </summary>
    /// <param name="operandTransform">The projection-compatible operand transform.</param>
    /// <param name="operand">The developer-facing operand.</param>
    /// <returns>The transformed UTF-8 operand bytes.</returns>
    private static byte[] EncodeTransformedOperand(Func<string?, string?> operandTransform, string operand)
    {
        string? transformed = operandTransform(operand);
        if (transformed is null)
        {
            throw new InvalidOperationException("String byte predicates cannot use null transformed operands.");
        }

        return Encoding.UTF8.GetBytes(transformed);
    }

    private bool MatchesRegex(string candidate, CompareOptions options)
        => regex is null
            ? Regex.IsMatch(candidate, value, CreateRegexOptions(options))
            : regex.IsMatch(candidate);

    private bool MatchesRegexCapture(string candidate, CompareInfo compareInfo, CompareOptions options, string expectedValue)
    {
        Match match = MatchRegex(candidate, options);
        if (!match.Success)
        {
            return false;
        }

        string captured = SelectRegexCapture(match);
        return Compare(captured, expectedValue, compareInfo, options) == 0;
    }

    private bool MatchesRegexCapture(string candidate, ISet<string> expectedValues)
    {
        Match match = MatchRegex(candidate, policy.CompareOptions);
        return match.Success && expectedValues.Contains(SelectRegexCapture(match));
    }

    private Match MatchRegex(string candidate, CompareOptions options)
        => regex is null
            ? Regex.Match(candidate, value, CreateRegexOptions(options))
            : regex.Match(candidate);

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
        => upperValue ?? throw new InvalidOperationException("The string predicate mode requires an upper comparison value.");

    /// <summary>
    /// Returns the captured membership operands for set-style predicates.<br/>
    /// The guard keeps predicate construction errors visible instead of treating a missing set as an empty set.<br/>
    /// </summary>
    /// <returns>The required membership operands.</returns>
    private IReadOnlyCollection<string> RequireSetValues()
        => setValues ?? throw new InvalidOperationException("The string predicate mode requires membership values.");

    private ISet<string> RequireMembershipSet()
        => membershipSet ?? throw new InvalidOperationException("The string predicate mode requires prepared membership values.");

    /// <summary>
    /// Compares one decoded exact-index string key to one condition operand using the selected .NET comparison options.<br/>
    /// Keeping the comparison in one helper makes the scan-backed string fallback explicit and avoids accidental ordinal byte-key reuse for no-case semantics.<br/>
    /// </summary>
    /// <param name="candidate">The decoded exact-index key.</param>
    /// <param name="expected">The condition comparison operand.</param>
    /// <param name="options">The .NET comparison options applied by the culture comparison implementation.<br/></param>
    /// <param name="compareInfo">The culture comparison implementation used unless the policy requests folded ordinal comparison.<br/></param>
    /// <returns>The .NET comparison result.</returns>
    private int Compare(string candidate, string expected, CompareInfo compareInfo, CompareOptions options)
        => policy.Kind == LibraDexStringComparisonPolicyKind.FoldedOrdinal
            ? string.CompareOrdinal(candidate, expected)
            : compareInfo.Compare(candidate, expected, options);

    /// <summary>
    /// Determines whether one decoded exact-index string key is present in a condition membership set.<br/>
    /// The comparison deliberately uses the same culture-aware .NET path as no-case equality so membership cannot regress to ordinal byte-key semantics.<br/>
    /// </summary>
    /// <param name="candidate">The decoded exact-index key.</param>
    /// <param name="values">The condition membership operands.</param>
    /// <returns><see langword="true"/> when the candidate matches any membership operand.</returns>
    private static bool MatchesSet(string candidate, ISet<string> values)
        => values.Contains(candidate);

    /// <summary>
    /// Builds exact candidate ranges from already-expanded string candidates.<br/>
    /// The caller owns candidate expansion; this helper only sorts, de-duplicates, and maps each value to an exact lower/upper pair.<br/>
    /// </summary>
    /// <param name="candidates">The candidate string values to normalize.</param>
    /// <returns>Sorted exact candidate ranges.</returns>
    private static IReadOnlyList<(string Lower, string Upper)> CreateExactCandidateRanges(List<string> candidates)
        => CreateCandidateRanges(candidates, exact: true);

    /// <summary>
    /// Builds prefix candidate ranges from already-expanded string candidates.<br/>
    /// Each returned range uses the candidate as the lower bound and the maximal suffix sentinel as the upper bound.<br/>
    /// </summary>
    /// <param name="candidates">The candidate string prefixes to normalize.</param>
    /// <returns>Sorted prefix candidate ranges.</returns>
    private static IReadOnlyList<(string Lower, string Upper)> CreatePrefixCandidateRanges(List<string> candidates)
        => CreateCandidateRanges(candidates, exact: false);

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

    private static string GetAnchoredRegexLiteralPrefix(string pattern)
    {
        if (pattern.Length == 0)
        {
            return string.Empty;
        }

        int index;
        if (pattern[0] == '^')
        {
            index = 1;
        }
        else if (pattern.StartsWith(@"\A", StringComparison.Ordinal))
        {
            index = 2;
        }
        else
        {
            return string.Empty;
        }

        StringBuilder prefix = new();
        while (index < pattern.Length)
        {
            char current = pattern[index];
            if (current == '\\')
            {
                if (index + 1 >= pattern.Length)
                {
                    break;
                }

                char escaped = pattern[index + 1];
                if (!IsEscapedRegexLiteral(escaped))
                {
                    break;
                }

                prefix.Append(escaped);
                index += 2;
                continue;
            }

            if (IsRegexMeta(current))
            {
                break;
            }

            prefix.Append(current);
            index++;
        }

        return prefix.ToString();
    }

    private static bool IsEscapedRegexLiteral(char value)
        => value is '.' or '$' or '^' or '{' or '[' or '(' or '|' or ')' or '*' or '+' or '?' or '\\';

    private static bool IsRegexMeta(char value)
        => value is '.' or '$' or '^' or '{' or '[' or '(' or '|' or ')' or '*' or '+' or '?' or '\\';

}

internal enum LibraDexWildcardShape
{
    Exact,
    StartsWith,
    EndsWith,
    Contains,
    Complex
}

internal readonly record struct LibraDexWildcardToken(char Value, bool IsWildcard)
{
    internal bool IsStar => IsWildcard && Value == '*';
    internal bool IsQuestion => IsWildcard && Value == '?';
}

internal sealed class LibraDexWildcardPattern
{
    private readonly LibraDexWildcardToken[] tokens;
    private readonly Regex? regex;

    private LibraDexWildcardPattern(
        LibraDexWildcardToken[] tokens,
        LibraDexWildcardShape shape,
        string literal,
        string leadingLiteral,
        Regex? regex)
    {
        this.tokens = tokens;
        Shape = shape;
        Literal = literal;
        LeadingLiteral = leadingLiteral;
        this.regex = regex;
    }

    internal LibraDexWildcardShape Shape { get; }
    internal string Literal { get; }
    internal string LeadingLiteral { get; }

    internal static LibraDexWildcardPattern Create(
        string pattern,
        LibraDexStringComparisonPolicy policy,
        bool compileComplex = true)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(policy);

        List<LibraDexWildcardToken> parsed = new(pattern.Length);
        for (int i = 0; i < pattern.Length; i++)
        {
            char value = pattern[i];
            if (value == '\\' && i + 1 < pattern.Length)
            {
                char next = pattern[i + 1];
                if (next is '*' or '?')
                {
                    parsed.Add(new LibraDexWildcardToken(next, IsWildcard: false));
                    i++;
                    continue;
                }

                if (next == '\\' && i + 2 < pattern.Length && pattern[i + 2] is '*' or '?')
                {
                    parsed.Add(new LibraDexWildcardToken('\\', IsWildcard: false));
                    i++;
                    continue;
                }
            }

            if (value == '*' && parsed.Count != 0 && parsed[^1].IsStar)
                continue;

            parsed.Add(new LibraDexWildcardToken(value, value is '*' or '?'));
        }

        LibraDexWildcardToken[] tokens = parsed.ToArray();
        int firstWildcard = Array.FindIndex(tokens, static token => token.IsWildcard);
        string leadingLiteral = firstWildcard < 0
            ? TokensToString(tokens, 0, tokens.Length)
            : TokensToString(tokens, 0, firstWildcard);
        LibraDexWildcardShape shape;
        string literal;
        if (firstWildcard < 0)
        {
            shape = LibraDexWildcardShape.Exact;
            literal = leadingLiteral;
        }
        else if (tokens[^1].IsStar && firstWildcard == tokens.Length - 1)
        {
            shape = LibraDexWildcardShape.StartsWith;
            literal = leadingLiteral;
        }
        else if (tokens[0].IsStar && !HasWildcard(tokens, 1, tokens.Length - 1))
        {
            shape = LibraDexWildcardShape.EndsWith;
            literal = TokensToString(tokens, 1, tokens.Length - 1);
        }
        else if (tokens.Length >= 2 && tokens[0].IsStar && tokens[^1].IsStar &&
            !HasWildcard(tokens, 1, tokens.Length - 2))
        {
            shape = LibraDexWildcardShape.Contains;
            literal = TokensToString(tokens, 1, tokens.Length - 2);
        }
        else
        {
            shape = LibraDexWildcardShape.Complex;
            literal = string.Empty;
        }

        Regex? regex = null;
        if (compileComplex &&
            shape == LibraDexWildcardShape.Complex &&
            string.IsNullOrEmpty(policy.CultureName) &&
            policy.Kind != LibraDexStringComparisonPolicyKind.Custom &&
            (policy.CompareOptions & ~CompareOptions.IgnoreCase) == 0)
        {
            var expression = new StringBuilder(pattern.Length + 8);
            expression.Append("\\A");
            foreach (LibraDexWildcardToken token in tokens)
            {
                if (token.IsStar)
                    expression.Append("[\\s\\S]*");
                else if (token.IsQuestion)
                    expression.Append("[\\s\\S]");
                else
                    expression.Append(Regex.Escape(token.Value.ToString()));
            }
            expression.Append("\\z");

            RegexOptions options = RegexOptions.Compiled | RegexOptions.CultureInvariant;
            if (policy.IgnoreCase)
                options |= RegexOptions.IgnoreCase;
            regex = new Regex(expression.ToString(), options);
        }

        return new LibraDexWildcardPattern(tokens, shape, literal, leadingLiteral, regex);
    }

    internal bool Matches(string candidate, CompareInfo compareInfo, CompareOptions options)
    {
        return Shape switch
        {
            LibraDexWildcardShape.Exact => compareInfo.Compare(candidate, Literal, options) == 0,
            LibraDexWildcardShape.StartsWith => compareInfo.IsPrefix(candidate, Literal, options),
            LibraDexWildcardShape.EndsWith => compareInfo.IsSuffix(candidate, Literal, options),
            LibraDexWildcardShape.Contains => compareInfo.IndexOf(candidate, Literal, options) >= 0,
            _ when regex is not null => regex.IsMatch(candidate),
            _ => MatchesCultureAware(candidate, compareInfo, options)
        };
    }

    /// <summary>
    /// Evaluates this already-prepared wildcard against an already-prepared candidate using ordinal character semantics.<br/>
    /// Folded-ordinal exact-index fallbacks use this path after both pattern literals and candidate text have been lower-cased with the same culture, reproducing the maintained folded projection contract without broadening comparison through culture collation.<br/>
    /// </summary>
    /// <param name="candidate">The non-null candidate prepared by the owning comparison policy.<br/></param>
    /// <returns><see langword="true"/> when the candidate satisfies the wildcard pattern.<br/></returns>
    internal bool MatchesOrdinal(string candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return Shape switch
        {
            LibraDexWildcardShape.Exact => string.Equals(candidate, Literal, StringComparison.Ordinal),
            LibraDexWildcardShape.StartsWith => candidate.StartsWith(Literal, StringComparison.Ordinal),
            LibraDexWildcardShape.EndsWith => candidate.EndsWith(Literal, StringComparison.Ordinal),
            LibraDexWildcardShape.Contains => candidate.Contains(Literal, StringComparison.Ordinal),
            _ when regex is not null => regex.IsMatch(candidate),
            _ => MatchesOrdinalCore(candidate)
        };
    }

    private bool MatchesCultureAware(string candidate, CompareInfo compareInfo, CompareOptions options)
    {
        int candidateIndex = 0;
        int patternIndex = 0;
        int starIndex = -1;
        int starCandidateIndex = -1;
        Span<char> literal = stackalloc char[1];
        while (candidateIndex < candidate.Length)
        {
            if (patternIndex < tokens.Length && tokens[patternIndex].IsQuestion)
            {
                candidateIndex++;
                patternIndex++;
                continue;
            }

            if (patternIndex < tokens.Length && !tokens[patternIndex].IsWildcard)
            {
                literal[0] = tokens[patternIndex].Value;
                if (compareInfo.Compare(candidate.AsSpan(candidateIndex, 1), literal, options) == 0)
                {
                    candidateIndex++;
                    patternIndex++;
                    continue;
                }
            }

            if (patternIndex < tokens.Length && tokens[patternIndex].IsStar)
            {
                starIndex = patternIndex++;
                starCandidateIndex = candidateIndex;
                continue;
            }

            if (starIndex >= 0)
            {
                patternIndex = starIndex + 1;
                candidateIndex = ++starCandidateIndex;
                continue;
            }

            return false;
        }

        while (patternIndex < tokens.Length && tokens[patternIndex].IsStar)
            patternIndex++;
        return patternIndex == tokens.Length;
    }

    /// <summary>
    /// Applies the complex wildcard token program with ordinal character equality.<br/>
    /// The algorithm mirrors the culture-aware star backtracking path but avoids collation after folded-ordinal preparation has already established the comparison representation.<br/>
    /// </summary>
    /// <param name="candidate">The prepared candidate string.<br/></param>
    /// <returns><see langword="true"/> when every literal, question mark, and star token is satisfied.<br/></returns>
    private bool MatchesOrdinalCore(string candidate)
    {
        int candidateIndex = 0;
        int patternIndex = 0;
        int starIndex = -1;
        int starCandidateIndex = -1;
        while (candidateIndex < candidate.Length)
        {
            if (patternIndex < tokens.Length && tokens[patternIndex].IsQuestion)
            {
                candidateIndex++;
                patternIndex++;
                continue;
            }

            if (patternIndex < tokens.Length &&
                !tokens[patternIndex].IsWildcard &&
                candidate[candidateIndex] == tokens[patternIndex].Value)
            {
                candidateIndex++;
                patternIndex++;
                continue;
            }

            if (patternIndex < tokens.Length && tokens[patternIndex].IsStar)
            {
                starIndex = patternIndex++;
                starCandidateIndex = candidateIndex;
                continue;
            }

            if (starIndex >= 0)
            {
                patternIndex = starIndex + 1;
                candidateIndex = ++starCandidateIndex;
                continue;
            }

            return false;
        }

        while (patternIndex < tokens.Length && tokens[patternIndex].IsStar)
            patternIndex++;
        return patternIndex == tokens.Length;
    }

    private static string TokensToString(LibraDexWildcardToken[] tokens, int start, int length)
    {
        return string.Create(length, (tokens, start), static (destination, state) =>
        {
            for (int i = 0; i < destination.Length; i++)
                destination[i] = state.tokens[state.start + i].Value;
        });
    }

    private static bool HasWildcard(LibraDexWildcardToken[] tokens, int start, int length)
    {
        int end = start + length;
        for (int i = start; i < end; i++)
        {
            if (tokens[i].IsWildcard)
                return true;
        }
        return false;
    }
}

/// <summary>
/// Executes a compiled string-pattern residual directly against UTF-8 key payload bytes.<br/>
/// The matcher deliberately owns already-transformed criteria bytes and performs only ordinal byte operations, keeping scan-backed exact/folded string predicates away from per-row string allocation.<br/>
/// </summary>
internal sealed class LibraDexUtf8StringPatternPredicate
{
    private readonly LibraDexStringPatternMode mode;
    private readonly byte[] value;
    private readonly byte[]? upperValue;
    private readonly byte[][]? setValues;
    private readonly bool requiresInvariantAsciiManagedFallback;

    /// <summary>
    /// Initializes a byte-native string pattern predicate from projection-compatible UTF-8 operands.<br/>
    /// The caller prepares operands once per query using the same transform as the selected physical projection; execution then compares candidate bytes directly.<br/>
    /// </summary>
    /// <param name="mode">The string predicate mode.<br/></param>
    /// <param name="value">The primary operand bytes.<br/></param>
    /// <param name="upperValue">The upper operand bytes for between-style predicates.<br/></param>
    /// <param name="setValues">The membership operand bytes for set-style predicates.<br/></param>
    /// <param name="requiresInvariantAsciiManagedFallback">Whether non-ASCII candidates require decoded managed evaluation.<br/></param>
    internal LibraDexUtf8StringPatternPredicate(
        LibraDexStringPatternMode mode,
        byte[] value,
        byte[]? upperValue,
        byte[][]? setValues,
        bool requiresInvariantAsciiManagedFallback = false)
    {
        this.mode = mode;
        this.value = value;
        this.upperValue = upperValue;
        this.setValues = setValues;
        this.requiresInvariantAsciiManagedFallback = requiresInvariantAsciiManagedFallback;
    }

    /// <summary>
    /// Tests one UTF-8 key payload against this compiled byte predicate.<br/>
    /// The candidate span must exclude LibraDex's string sentinel byte and represent the same exact or folded projection selected when the matcher was created.<br/>
    /// </summary>
    /// <param name="candidate">The candidate UTF-8 payload bytes.<br/></param>
    /// <returns><see langword="true"/> when the candidate satisfies the predicate.<br/></returns>
    internal bool Matches(ReadOnlySpan<byte> candidate)
    {
        LibraDexUtf8MatchResult result = Evaluate(candidate);
        if (result == LibraDexUtf8MatchResult.RequiresManaged)
        {
            throw new InvalidOperationException("The UTF-8 string predicate requires its managed Unicode fallback for this candidate.");
        }

        return result == LibraDexUtf8MatchResult.Match;
    }

    /// <summary>
    /// Evaluates one UTF-8 key payload and distinguishes a final byte result from a required managed Unicode fallback.<br/>
    /// Projection-normalized and case-sensitive predicates always return a final result; invariant folded exact-key predicates defer only non-ASCII candidates.<br/>
    /// </summary>
    /// <param name="candidate">The UTF-8 key payload without LibraDex's leading string sentinel.<br/></param>
    /// <returns>The final byte result or an explicit managed-fallback request.<br/></returns>
    internal LibraDexUtf8MatchResult Evaluate(ReadOnlySpan<byte> candidate)
    {
        if (requiresInvariantAsciiManagedFallback)
        {
            if (!IsAscii(candidate))
            {
                return LibraDexUtf8MatchResult.RequiresManaged;
            }

            bool asciiMatch = mode switch
            {
                LibraDexStringPatternMode.StartsWith => StartsWithAsciiIgnoreCase(candidate, value),
                LibraDexStringPatternMode.EndsWith => EndsWithAsciiIgnoreCase(candidate, value),
                LibraDexStringPatternMode.Contains => IndexOfAsciiIgnoreCase(candidate, value) >= 0,
                LibraDexStringPatternMode.NotStartsWith => !StartsWithAsciiIgnoreCase(candidate, value),
                LibraDexStringPatternMode.NotEndsWith => !EndsWithAsciiIgnoreCase(candidate, value),
                LibraDexStringPatternMode.NotContains => IndexOfAsciiIgnoreCase(candidate, value) < 0,
                _ => false
            };
            return asciiMatch ? LibraDexUtf8MatchResult.Match : LibraDexUtf8MatchResult.NoMatch;
        }

        bool matches = mode switch
        {
            LibraDexStringPatternMode.StartsWith => candidate.StartsWith(value),
            LibraDexStringPatternMode.EndsWith => candidate.EndsWith(value),
            LibraDexStringPatternMode.Contains => candidate.IndexOf(value) >= 0,
            LibraDexStringPatternMode.NotStartsWith => !candidate.StartsWith(value),
            LibraDexStringPatternMode.NotEndsWith => !candidate.EndsWith(value),
            LibraDexStringPatternMode.NotContains => candidate.IndexOf(value) < 0,
            LibraDexStringPatternMode.EqualTo => candidate.SequenceEqual(value),
            LibraDexStringPatternMode.NotEqualTo => !candidate.SequenceEqual(value),
            LibraDexStringPatternMode.GreaterThan => candidate.SequenceCompareTo(value) > 0,
            LibraDexStringPatternMode.GreaterOrEqual => candidate.SequenceCompareTo(value) >= 0,
            LibraDexStringPatternMode.LessThan => candidate.SequenceCompareTo(value) < 0,
            LibraDexStringPatternMode.LessOrEqual => candidate.SequenceCompareTo(value) <= 0,
            LibraDexStringPatternMode.Between => candidate.SequenceCompareTo(value) >= 0 &&
                candidate.SequenceCompareTo(RequireUpperValue()) <= 0,
            LibraDexStringPatternMode.NotBetween => candidate.SequenceCompareTo(value) < 0 ||
                candidate.SequenceCompareTo(RequireUpperValue()) > 0,
            LibraDexStringPatternMode.InSet => MatchesSet(candidate),
            LibraDexStringPatternMode.NotInSet => !MatchesSet(candidate),
            _ => false
        };
        return matches ? LibraDexUtf8MatchResult.Match : LibraDexUtf8MatchResult.NoMatch;
    }

    /// <summary>
    /// Tests whether one UTF-8 payload contains only single-byte ASCII values.<br/>
    /// A high-bit byte identifies a multi-byte or otherwise non-ASCII sequence and therefore requests the managed Unicode path.<br/>
    /// </summary>
    /// <param name="candidate">The UTF-8 payload to inspect.<br/></param>
    /// <returns><see langword="true"/> when every byte is ASCII; otherwise <see langword="false"/>.<br/></returns>
    private static bool IsAscii(ReadOnlySpan<byte> candidate)
    {
        for (int index = 0; index < candidate.Length; index++)
        {
            if ((candidate[index] & 0x80) != 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Tests an ASCII prefix under invariant folded semantics without decoded or lower-cased string allocation.<br/>
    /// </summary>
    /// <param name="candidate">The ASCII candidate payload.<br/></param>
    /// <param name="prefix">The ASCII prefix payload.<br/></param>
    /// <returns><see langword="true"/> when the folded prefix matches.<br/></returns>
    private static bool StartsWithAsciiIgnoreCase(ReadOnlySpan<byte> candidate, ReadOnlySpan<byte> prefix)
        => candidate.Length >= prefix.Length && EqualsAsciiIgnoreCase(candidate[..prefix.Length], prefix);

    /// <summary>
    /// Tests an ASCII suffix under invariant folded semantics without decoded or lower-cased string allocation.<br/>
    /// </summary>
    /// <param name="candidate">The ASCII candidate payload.<br/></param>
    /// <param name="suffix">The ASCII suffix payload.<br/></param>
    /// <returns><see langword="true"/> when the folded suffix matches.<br/></returns>
    private static bool EndsWithAsciiIgnoreCase(ReadOnlySpan<byte> candidate, ReadOnlySpan<byte> suffix)
        => candidate.Length >= suffix.Length && EqualsAsciiIgnoreCase(candidate[^suffix.Length..], suffix);

    /// <summary>
    /// Finds an ASCII value inside another ASCII payload using invariant folded comparison.<br/>
    /// Empty values match at offset zero, matching ordinary string containment semantics.<br/>
    /// </summary>
    /// <param name="candidate">The ASCII candidate payload.<br/></param>
    /// <param name="value">The ASCII value to locate.<br/></param>
    /// <returns>The first matching offset, or -1 when no folded match exists.<br/></returns>
    private static int IndexOfAsciiIgnoreCase(ReadOnlySpan<byte> candidate, ReadOnlySpan<byte> value)
    {
        if (value.Length == 0)
        {
            return 0;
        }

        int last = candidate.Length - value.Length;
        byte foldedFirst = FoldAscii(value[0]);
        byte alternateFirst = foldedFirst is >= (byte)'a' and <= (byte)'z'
            ? (byte)(foldedFirst - ('a' - 'A'))
            : foldedFirst;
        int searchOffset = 0;
        while (searchOffset <= last)
        {
            ReadOnlySpan<byte> remainingStarts = candidate.Slice(searchOffset, last - searchOffset + 1);
            int relative = foldedFirst == alternateFirst
                ? remainingStarts.IndexOf(foldedFirst)
                : remainingStarts.IndexOfAny(foldedFirst, alternateFirst);
            if (relative < 0)
                return -1;

            int offset = searchOffset + relative;
            if (EqualsAsciiIgnoreCase(candidate.Slice(offset, value.Length), value))
            {
                return offset;
            }

            searchOffset = offset + 1;
        }

        return -1;
    }

    /// <summary>
    /// Compares equal-length ASCII payloads after folding only uppercase ASCII letters.<br/>
    /// </summary>
    /// <param name="left">The first ASCII payload.<br/></param>
    /// <param name="right">The second ASCII payload.<br/></param>
    /// <returns><see langword="true"/> when both payloads are equal under invariant ASCII folding.<br/></returns>
    private static bool EqualsAsciiIgnoreCase(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (int index = 0; index < left.Length; index++)
        {
            if (FoldAscii(left[index]) != FoldAscii(right[index]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Folds one uppercase ASCII letter to lowercase while returning every other byte unchanged.<br/>
    /// </summary>
    /// <param name="value">The ASCII byte to fold.<br/></param>
    /// <returns>The invariant folded ASCII byte.<br/></returns>
    private static byte FoldAscii(byte value)
        => value is >= (byte)'A' and <= (byte)'Z'
            ? (byte)(value + ('a' - 'A'))
            : value;

    /// <summary>
    /// Returns the required upper byte operand for between-style comparisons.<br/>
    /// The guard keeps invalid matcher construction visible instead of silently treating the missing bound as an empty payload.<br/>
    /// </summary>
    /// <returns>The upper operand bytes.</returns>
    private byte[] RequireUpperValue()
        => upperValue ?? throw new InvalidOperationException("The UTF-8 byte string predicate requires an upper comparison value.");

    /// <summary>
    /// Tests one candidate payload against the prepared byte membership set.<br/>
    /// The simple linear scan avoids building a span comparer allocation surface; condition builder membership sets are expected to route exact lookups when large enough to matter.<br/>
    /// </summary>
    /// <param name="candidate">The candidate UTF-8 payload bytes.</param>
    /// <returns><see langword="true"/> when the candidate matches a set operand.</returns>
    private bool MatchesSet(ReadOnlySpan<byte> candidate)
    {
        byte[][] localSet = setValues ?? throw new InvalidOperationException("The UTF-8 byte string predicate requires membership values.");
        for (int i = 0; i < localSet.Length; i++)
        {
            if (candidate.SequenceEqual(localSet[i]))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Reports whether a UTF-8 residual predicate produced a final result or must defer to decoded managed semantics.<br/>
/// </summary>
internal enum LibraDexUtf8MatchResult
{
    /// <summary>The candidate definitively does not match.<br/></summary>
    NoMatch = 0,

    /// <summary>The candidate definitively matches.<br/></summary>
    Match = 1,

    /// <summary>The candidate requires managed Unicode or culture-aware evaluation.<br/></summary>
    RequiresManaged = 2
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
    CustomEncodingString = 27,
    StructuredDateTimeCalendar = 28,
    StructuredDateTimePrecision = 29,
    StructuredDateOnlyCalendar = 30,
    StructuredDateOnlyPrecision = 31,
    StructuredTimeOnlyCalendar = 32,
    StructuredTimeOnlyPrecision = 33,
    StructuredDateTimeOffsetCalendarUtc = 34,
    StructuredDateTimeOffsetPrecisionUtc = 35,
    OrderedTimeSpanTicks = 36
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
    private static readonly System.Text.Encoding StrictUtf8 = new System.Text.UTF8Encoding(false, true);
    private static readonly System.Text.Encoding StrictUtf16 = new System.Text.UnicodeEncoding(false, false, true);
    private static readonly System.Text.Encoding StrictUtf32 = new System.Text.UTF32Encoding(false, false, true);

    private readonly LibraDexBinarySliceValueKind valueKind;
    private readonly LibraDexBinarySliceComparisonKind comparisonKind;
    private readonly int offset;
    private readonly int length;
    private readonly object value;
    private readonly object? upperValue;
    private readonly System.Text.Encoding? encoding;
    private readonly LibraDexTextEncoding? stableEncoding;
    private readonly Coercion.Numeric numericCoercion;
    private readonly Coercion.Text textCoercion;
    private readonly byte[]? encodedTextValue;
    private readonly int encodedTextUnitSize;

    private LibraDexBinaryTypedSlicePredicate(
        LibraDexBinarySliceValueKind valueKind,
        LibraDexBinarySliceComparisonKind comparisonKind,
        int offset,
        int length,
        object value,
        object? upperValue,
        System.Text.Encoding? encoding,
        Coercion.Numeric numericCoercion,
        Coercion.Text textCoercion,
        LibraDexTextEncoding? stableEncoding)
    {
        this.valueKind = valueKind;
        this.comparisonKind = comparisonKind;
        this.offset = offset;
        this.length = length;
        this.value = NormalizeOperand(valueKind, value);
        this.upperValue = upperValue is null ? null : NormalizeOperand(valueKind, upperValue);
        this.encoding = encoding;
        this.stableEncoding = stableEncoding;
        this.numericCoercion = numericCoercion;
        this.textCoercion = textCoercion;
        encodedTextValue = TryEncodeOrdinalText(valueKind, value, out encodedTextUnitSize);
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
    /// <param name="numericCoercion">The physical numeric representation used by numeric slice kinds.</param>
    /// <param name="textCoercion">The malformed-input policy used by deterministic encoded-text slice kinds.</param>
    /// <param name="stableEncoding">The optional stable code-page contract for custom encoded string slices.<br/></param>
    /// <returns>The compiled typed binary slice predicate.</returns>
    internal static LibraDexBinaryTypedSlicePredicate Create(
        LibraDexBinarySliceValueKind valueKind,
        LibraDexBinarySliceComparisonKind comparisonKind,
        int offset,
        int length,
        object value,
        object? upperValue = null,
        System.Text.Encoding? encoding = null,
        Coercion.Numeric numericCoercion = Coercion.Numeric.DotNet,
        Coercion.Text textCoercion = Coercion.Text.Strict,
        LibraDexTextEncoding? stableEncoding = null)
    {
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Binary slice offset cannot be negative.");
        }

        bool isString = valueKind is LibraDexBinarySliceValueKind.Utf8String or
            LibraDexBinarySliceValueKind.Utf16String or
            LibraDexBinarySliceValueKind.Utf32String or
            LibraDexBinarySliceValueKind.AsciiString or
            LibraDexBinarySliceValueKind.Latin1String or
            LibraDexBinarySliceValueKind.CustomEncodingString;
        if (length != LibraDexBinaryStringSliceConditionOperator.RemainingLength && length <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "Binary slice length must be positive.");
        }

        if (length == LibraDexBinaryStringSliceConditionOperator.RemainingLength && !isString)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "Only binary string slices can extend through the end of a key.");
        }

        if (valueKind == LibraDexBinarySliceValueKind.CustomEncodingString &&
            encoding is null &&
            stableEncoding is null)
        {
            throw new ArgumentNullException(
                nameof(encoding),
                "Custom encoded binary string slices require a caller-supplied Encoding instance or stable LibraDexTextEncoding contract.");
        }

        if (!Enum.IsDefined(textCoercion))
        {
            throw new ArgumentOutOfRangeException(nameof(textCoercion), textCoercion, "Unknown binary text-slice coercion policy.");
        }

        return new LibraDexBinaryTypedSlicePredicate(
            valueKind,
            comparisonKind,
            offset,
            length,
            value,
            upperValue,
            encoding,
            numericCoercion,
            textCoercion,
            stableEncoding);
    }

    /// <summary>
    /// Evaluates this predicate against one encoded fixed-width byte-array key.<br/>
    /// The typed value is read directly from the requested slice; invalid offsets or invalid typed payloads simply do not match.<br/>
    /// </summary>
    /// <param name="encodedKey">The encoded key bytes for the current row.</param>
    /// <returns><see langword="true"/> when the typed slice satisfies the predicate.</returns>
    internal bool Matches(ReadOnlySpan<byte> encodedKey)
    {
        if (offset > encodedKey.Length)
        {
            return false;
        }

        int sliceLength = length == LibraDexBinaryStringSliceConditionOperator.RemainingLength
            ? encodedKey.Length - offset
            : length;
        if (sliceLength > encodedKey.Length - offset)
            return false;

        try
        {
            ReadOnlySpan<byte> slice = encodedKey.Slice(offset, sliceLength);
            if (TryGetNumericType(valueKind, out Type? numericType))
            {
                object numericValue = LibraDexDuplicateExecution.ProjectNumeric(slice, numericType!, numericCoercion);
                return CompareNumeric(numericValue, value, upperValue);
            }

            return valueKind switch
            {
                LibraDexBinarySliceValueKind.Guid => CompareValue(new Guid(slice[..16]), value, upperValue),
                LibraDexBinarySliceValueKind.DateTimeTicks => CompareValue(System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(slice), value, upperValue),
                LibraDexBinarySliceValueKind.Utf8String => CompareEncodedOrDecodedString(slice, System.Text.Encoding.UTF8),
                LibraDexBinarySliceValueKind.DateOnly => CompareValue(System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(slice), value, upperValue),
                LibraDexBinarySliceValueKind.TimeOnly => CompareValue(System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(slice), value, upperValue),
                LibraDexBinarySliceValueKind.TimeSpanTicks => CompareValue(System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(slice), value, upperValue),
                LibraDexBinarySliceValueKind.DateTimeOffsetPair => TryReadDateTimeOffsetUtcTicks(slice, out long utcTicks) && CompareValue(utcTicks, value, upperValue),
                LibraDexBinarySliceValueKind.Utf16String => CompareEncodedOrDecodedString(slice, System.Text.Encoding.Unicode),
                LibraDexBinarySliceValueKind.Utf32String => CompareEncodedOrDecodedString(slice, System.Text.Encoding.UTF32),
                LibraDexBinarySliceValueKind.AsciiString => CompareEncodedOrDecodedString(slice, System.Text.Encoding.ASCII),
                LibraDexBinarySliceValueKind.Latin1String => CompareEncodedOrDecodedString(slice, System.Text.Encoding.Latin1),
                LibraDexBinarySliceValueKind.CharUtf16 => CompareString(System.Runtime.InteropServices.MemoryMarshal.Read<char>(slice).ToString(), value),
                LibraDexBinarySliceValueKind.RuneUtf32 => CompareString(char.ConvertFromUtf32(System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(slice)), value),
                LibraDexBinarySliceValueKind.CustomEncodingString => CompareString(
                    stableEncoding is not null
                        ? stableEncoding.Decode(slice)
                        : (encoding ?? throw new InvalidOperationException(
                            "Custom encoded binary string slices require an Encoding instance or stable LibraDexTextEncoding contract.")).GetString(slice),
                    value),
                LibraDexBinarySliceValueKind.StructuredDateTimeCalendar or
                LibraDexBinarySliceValueKind.StructuredDateTimePrecision or
                LibraDexBinarySliceValueKind.StructuredDateOnlyCalendar or
                LibraDexBinarySliceValueKind.StructuredDateOnlyPrecision or
                LibraDexBinarySliceValueKind.StructuredTimeOnlyCalendar or
                LibraDexBinarySliceValueKind.StructuredTimeOnlyPrecision or
                LibraDexBinarySliceValueKind.StructuredDateTimeOffsetCalendarUtc or
                LibraDexBinarySliceValueKind.StructuredDateTimeOffsetPrecisionUtc or
                LibraDexBinarySliceValueKind.OrderedTimeSpanTicks => CompareValue(System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(slice), value, upperValue),
                _ => throw new InvalidOperationException($"Unknown binary slice value kind {valueKind}.")
            };
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool TryGetNumericType(
        LibraDexBinarySliceValueKind kind,
        out Type? type)
    {
        type = kind switch
        {
            LibraDexBinarySliceValueKind.Int8 => typeof(sbyte),
            LibraDexBinarySliceValueKind.UInt8 => typeof(byte),
            LibraDexBinarySliceValueKind.Int16 => typeof(short),
            LibraDexBinarySliceValueKind.UInt16 => typeof(ushort),
            LibraDexBinarySliceValueKind.Int32 => typeof(int),
            LibraDexBinarySliceValueKind.UInt32 => typeof(uint),
            LibraDexBinarySliceValueKind.Int64 => typeof(long),
            LibraDexBinarySliceValueKind.UInt64 => typeof(ulong),
            LibraDexBinarySliceValueKind.Int128 => typeof(Int128),
            LibraDexBinarySliceValueKind.UInt128 => typeof(UInt128),
            LibraDexBinarySliceValueKind.Single => typeof(float),
            LibraDexBinarySliceValueKind.Double => typeof(double),
            LibraDexBinarySliceValueKind.Decimal => typeof(decimal),
            LibraDexBinarySliceValueKind.BigInteger => typeof(System.Numerics.BigInteger),
            _ => null
        };
        return type is not null;
    }

    private bool CompareNumeric(object candidate, object expected, object? upper)
    {
        return candidate switch
        {
            byte value => CompareValue(value, expected, upper),
            sbyte value => CompareValue(value, expected, upper),
            short value => CompareValue(value, expected, upper),
            ushort value => CompareValue(value, expected, upper),
            int value => CompareValue(value, expected, upper),
            uint value => CompareValue(value, expected, upper),
            long value => CompareValue(value, expected, upper),
            ulong value => CompareValue(value, expected, upper),
            Int128 value => CompareValue(value, expected, upper),
            UInt128 value => CompareValue(value, expected, upper),
            float value => CompareValue(value, expected, upper),
            double value => CompareValue(value, expected, upper),
            decimal value => CompareValue(value, expected, upper),
            System.Numerics.BigInteger value => CompareValue(value, expected, upper),
            _ => throw new NotSupportedException($"Binary numeric slice comparison does not support '{candidate.GetType().FullName}'.")
        };
    }

    private static object NormalizeOperand(LibraDexBinarySliceValueKind valueKind, object operand)
    {
        return valueKind switch
        {
            LibraDexBinarySliceValueKind.DateTimeTicks => operand is DateTime dateTime
                ? dateTime.Ticks
                : throw OperandTypeException(valueKind, operand, typeof(DateTime)),
            LibraDexBinarySliceValueKind.StructuredDateTimeCalendar => operand is DateTime calendarDateTime
                ? LibraDexStructuredDateCodec.Encode(calendarDateTime, DateTimeKeyEncoding.CalendarSdt)
                : throw OperandTypeException(valueKind, operand, typeof(DateTime)),
            LibraDexBinarySliceValueKind.StructuredDateTimePrecision => operand is DateTime precisionDateTime
                ? LibraDexStructuredDateCodec.Encode(precisionDateTime, DateTimeKeyEncoding.PrecisionSdt)
                : throw OperandTypeException(valueKind, operand, typeof(DateTime)),
            LibraDexBinarySliceValueKind.DateOnly => operand is DateOnly dateOnly
                ? dateOnly.DayNumber
                : throw OperandTypeException(valueKind, operand, typeof(DateOnly)),
            LibraDexBinarySliceValueKind.StructuredDateOnlyCalendar => operand is DateOnly calendarDateOnly
                ? LibraDexStructuredDateCodec.Encode(calendarDateOnly, DateTimeKeyEncoding.CalendarSdt)
                : throw OperandTypeException(valueKind, operand, typeof(DateOnly)),
            LibraDexBinarySliceValueKind.StructuredDateOnlyPrecision => operand is DateOnly precisionDateOnly
                ? LibraDexStructuredDateCodec.Encode(precisionDateOnly, DateTimeKeyEncoding.PrecisionSdt)
                : throw OperandTypeException(valueKind, operand, typeof(DateOnly)),
            LibraDexBinarySliceValueKind.TimeOnly => operand is TimeOnly timeOnly
                ? timeOnly.Ticks
                : throw OperandTypeException(valueKind, operand, typeof(TimeOnly)),
            LibraDexBinarySliceValueKind.StructuredTimeOnlyCalendar => operand is TimeOnly calendarTimeOnly
                ? LibraDexStructuredDateCodec.Encode(calendarTimeOnly, DateTimeKeyEncoding.CalendarSdt)
                : throw OperandTypeException(valueKind, operand, typeof(TimeOnly)),
            LibraDexBinarySliceValueKind.StructuredTimeOnlyPrecision => operand is TimeOnly precisionTimeOnly
                ? LibraDexStructuredDateCodec.Encode(precisionTimeOnly, DateTimeKeyEncoding.PrecisionSdt)
                : throw OperandTypeException(valueKind, operand, typeof(TimeOnly)),
            LibraDexBinarySliceValueKind.TimeSpanTicks => operand is TimeSpan timeSpan
                ? timeSpan.Ticks
                : throw OperandTypeException(valueKind, operand, typeof(TimeSpan)),
            LibraDexBinarySliceValueKind.OrderedTimeSpanTicks => operand is TimeSpan orderedTimeSpan
                ? unchecked((ulong)(orderedTimeSpan.Ticks ^ long.MinValue))
                : throw OperandTypeException(valueKind, operand, typeof(TimeSpan)),
            LibraDexBinarySliceValueKind.DateTimeOffsetPair => operand is DateTimeOffset dateTimeOffset
                ? dateTimeOffset.UtcTicks
                : throw OperandTypeException(valueKind, operand, typeof(DateTimeOffset)),
            LibraDexBinarySliceValueKind.StructuredDateTimeOffsetCalendarUtc => operand is DateTimeOffset calendarDateTimeOffset
                ? LibraDexStructuredDateCodec.Encode(calendarDateTimeOffset, DateTimeKeyEncoding.CalendarSdt)
                : throw OperandTypeException(valueKind, operand, typeof(DateTimeOffset)),
            LibraDexBinarySliceValueKind.StructuredDateTimeOffsetPrecisionUtc => operand is DateTimeOffset precisionDateTimeOffset
                ? LibraDexStructuredDateCodec.Encode(precisionDateTimeOffset, DateTimeKeyEncoding.PrecisionSdt)
                : throw OperandTypeException(valueKind, operand, typeof(DateTimeOffset)),
            _ => operand
        };
    }

    private static ArgumentException OperandTypeException(LibraDexBinarySliceValueKind valueKind, object operand, Type expectedType)
    {
        return new ArgumentException(
            $"Binary slice kind {valueKind} requires a {expectedType.FullName} comparison operand, not {operand.GetType().FullName}.",
            nameof(operand));
    }

    private static bool TryReadDateTimeOffsetUtcTicks(ReadOnlySpan<byte> slice, out long utcTicks)
    {
        long localTicks = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(slice);
        long offsetTicks = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(slice[sizeof(long)..]);
        if (localTicks < DateTime.MinValue.Ticks || localTicks > DateTime.MaxValue.Ticks ||
            offsetTicks < -14 * TimeSpan.TicksPerHour || offsetTicks > 14 * TimeSpan.TicksPerHour ||
            offsetTicks % TimeSpan.TicksPerMinute != 0)
        {
            utcTicks = 0;
            return false;
        }

        utcTicks = checked(localTicks - offsetTicks);
        return utcTicks >= DateTime.MinValue.Ticks && utcTicks <= DateTime.MaxValue.Ticks;
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

    private bool CompareEncodedOrDecodedString(ReadOnlySpan<byte> candidate, System.Text.Encoding decoder)
    {
        if (encodedTextValue is null)
            return CompareString(decoder.GetString(candidate), value);

        if (!IsValidEncodedText(candidate))
        {
            return textCoercion == Coercion.Text.DotNetReplacement &&
                CompareString(decoder.GetString(candidate), value);
        }

        ReadOnlySpan<byte> expected = encodedTextValue;
        return comparisonKind switch
        {
            LibraDexBinarySliceComparisonKind.EqualTo => candidate.SequenceEqual(expected),
            LibraDexBinarySliceComparisonKind.StartsWith => candidate.StartsWith(expected),
            LibraDexBinarySliceComparisonKind.Contains => IndexOfAligned(candidate, expected, encodedTextUnitSize) >= 0,
            _ => throw new InvalidOperationException($"Binary string slice comparison {comparisonKind} is not supported.")
        };
    }

    private bool IsValidEncodedText(ReadOnlySpan<byte> candidate)
    {
        if (valueKind == LibraDexBinarySliceValueKind.Latin1String)
            return true;

        if (valueKind == LibraDexBinarySliceValueKind.AsciiString)
        {
            for (int i = 0; i < candidate.Length; i++)
            {
                if (candidate[i] > 0x7F)
                    return false;
            }

            return true;
        }

        System.Text.Encoding strict = valueKind switch
        {
            LibraDexBinarySliceValueKind.Utf8String => StrictUtf8,
            LibraDexBinarySliceValueKind.Utf16String => StrictUtf16,
            LibraDexBinarySliceValueKind.Utf32String => StrictUtf32,
            _ => throw new InvalidOperationException($"Binary string slice kind {valueKind} has no byte-native validator.")
        };
        try
        {
            _ = strict.GetCharCount(candidate);
            return true;
        }
        catch (System.Text.DecoderFallbackException)
        {
            return false;
        }
    }

    private static byte[]? TryEncodeOrdinalText(LibraDexBinarySliceValueKind valueKind, object value, out int unitSize)
    {
        unitSize = 1;
        if (value is not string text)
            return null;

        try
        {
            switch (valueKind)
            {
                case LibraDexBinarySliceValueKind.Utf8String:
                    return StrictUtf8.GetBytes(text);
                case LibraDexBinarySliceValueKind.Utf16String:
                    unitSize = 2;
                    return StrictUtf16.GetBytes(text);
                case LibraDexBinarySliceValueKind.Utf32String:
                    unitSize = 4;
                    return StrictUtf32.GetBytes(text);
                case LibraDexBinarySliceValueKind.AsciiString:
                    for (int i = 0; i < text.Length; i++)
                    {
                        if (text[i] > 0x7F)
                            return null;
                    }

                    return System.Text.Encoding.ASCII.GetBytes(text);
                case LibraDexBinarySliceValueKind.Latin1String:
                    for (int i = 0; i < text.Length; i++)
                    {
                        if (text[i] > 0xFF)
                            return null;
                    }

                    return System.Text.Encoding.Latin1.GetBytes(text);
                default:
                    return null;
            }
        }
        catch (System.Text.EncoderFallbackException)
        {
            return null;
        }
    }

    private static int IndexOfAligned(ReadOnlySpan<byte> candidate, ReadOnlySpan<byte> expected, int unitSize)
    {
        if (expected.Length == 0)
            return 0;

        int last = candidate.Length - expected.Length;
        for (int i = 0; i <= last; i += unitSize)
        {
            if (candidate.Slice(i, expected.Length).SequenceEqual(expected))
                return i;
        }

        return -1;
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
    private const byte WildcardNibble = byte.MaxValue;

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
    private readonly byte[]? containedNibbles;
    private readonly bool containsInCanonicalOrder;
    private readonly int containsStartStep;

    private LibraDexGuidPatternPredicate(
        ulong comparedStorageNibbleBits,
        byte[] targetStorageNibbles,
        byte[]? containedNibbles = null,
        bool containsInCanonicalOrder = false,
        int containsStartStep = 1)
    {
        this.comparedStorageNibbleBits = comparedStorageNibbleBits;
        this.targetStorageNibbles = targetStorageNibbles;
        this.containedNibbles = containedNibbles;
        this.containsInCanonicalOrder = containsInCanonicalOrder;
        this.containsStartStep = containsStartStep;
    }

    /// <summary>
    /// Creates a GUID pattern predicate from canonical GUID text input.<br/>
    /// Ordinary GUID punctuation is ignored, `x` is the only wildcard token, and all other characters are rejected instead of being silently discarded.<br/>
    /// </summary>
    /// <param name="value">The caller-supplied GUID text, partial text, or wildcard pattern.</param>
    /// <param name="mode">The comparison mode.</param>
    /// <returns>The compiled GUID pattern predicate.</returns>
    internal static LibraDexGuidPatternPredicate Create(string value, LibraDexGuidPatternMode mode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string core = Clean(value);
        if (mode == LibraDexGuidPatternMode.Contains)
        {
            return new LibraDexGuidPatternPredicate(
                0,
                Array.Empty<byte>(),
                ParseNibbles(core),
                containsInCanonicalOrder: true);
        }

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

        if (mode == LibraDexGuidPatternMode.Contains)
        {
            byte[] contained = new byte[comparedNibbleCount];
            for (int valueNibble = 0; valueNibble < comparedNibbleCount; valueNibble++)
            {
                byte source = value[valueNibble >> 1];
                contained[valueNibble] = (valueNibble & 1) == 0
                    ? (byte)(source >> 4)
                    : (byte)(source & 0xF);
            }

            return new LibraDexGuidPatternPredicate(0, Array.Empty<byte>(), contained, containsStartStep: 2);
        }

        int startNibble = mode switch
        {
            LibraDexGuidPatternMode.StartsWith or LibraDexGuidPatternMode.MatchesPattern => 0,
            LibraDexGuidPatternMode.EndsWith => 32 - comparedNibbleCount,
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
    /// Creates an equality predicate over one canonical GUID nibble slice.<br/>
    /// Slice ordinals follow the familiar 32 hexadecimal digits shown by `Guid.ToString("N")`, never the mixed-endian byte order returned by `Guid.TryWriteBytes`.<br/>
    /// </summary>
    /// <param name="startNibble">The zero-based canonical nibble at which the slice begins.</param>
    /// <param name="nibbleCount">The number of canonical nibbles selected by the slice.</param>
    /// <param name="canonicalNibbles">The exact nibble values expected in canonical order.</param>
    /// <returns>The compiled GUID slice predicate.</returns>
    internal static LibraDexGuidPatternPredicate CreateSlice(int startNibble, int nibbleCount, ReadOnlySpan<byte> canonicalNibbles)
    {
        ValidateSlice(startNibble, nibbleCount);
        if (canonicalNibbles.Length != nibbleCount)
        {
            throw new FormatException($"GUID slice value must contain exactly {nibbleCount} hexadecimal nibbles.");
        }

        ulong compared = 0;
        byte[] target = new byte[32];
        for (int i = 0; i < nibbleCount; i++)
        {
            byte nibble = canonicalNibbles[i];
            if (nibble > 0xF)
            {
                throw new FormatException("GUID slice values must contain hexadecimal nibbles only.");
            }

            int storageNibble = CanonicalNibbleToStorageNibble[startNibble + i];
            compared |= 1UL << storageNibble;
            target[storageNibble] = nibble;
        }

        return new LibraDexGuidPatternPredicate(compared, target);
    }

    /// <summary>
    /// Creates an equality predicate over one canonical GUID nibble slice from hexadecimal text.<br/>
    /// Hexadecimal letter case is ignored because the text is parsed to nibble values before the condition executes.<br/>
    /// </summary>
    /// <param name="startNibble">The zero-based canonical nibble at which the slice begins.</param>
    /// <param name="nibbleCount">The number of canonical nibbles selected by the slice.</param>
    /// <param name="value">The exact hexadecimal slice value.</param>
    /// <returns>The compiled GUID slice predicate.</returns>
    internal static LibraDexGuidPatternPredicate CreateSlice(int startNibble, int nibbleCount, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string core = Clean(value, allowWildcard: false);
        return CreateSlice(startNibble, nibbleCount, ParseNibbles(core));
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
        return MatchesStorage(storage);
    }

    /// <summary>
    /// Evaluates this predicate against one routed composite GUID value without formatting it as text.<br/>
    /// The GUID is written once to a stack buffer and then evaluated by the same canonical-nibble engine used by ordinary GUID indexes.<br/>
    /// </summary>
    /// <param name="value">The candidate GUID value.</param>
    /// <returns><see langword="true"/> when the GUID satisfies the compiled predicate.</returns>
    internal bool Matches(Guid value)
    {
        Span<byte> storage = stackalloc byte[16];
        value.TryWriteBytes(storage);
        return MatchesStorage(storage);
    }

    /// <summary>
    /// Evaluates fixed-position and anywhere-containment GUID predicates over stored GUID bytes.<br/>
    /// Canonical containment maps each candidate nibble to physical storage on demand; byte-domain containment deliberately scans physical stored-byte order.<br/>
    /// </summary>
    /// <param name="storage">The 16 bytes in `Guid.TryWriteBytes` order.</param>
    /// <returns><see langword="true"/> when the stored GUID satisfies the predicate.</returns>
    private bool MatchesStorage(ReadOnlySpan<byte> storage)
    {
        if (containedNibbles is not null)
        {
            int lastStart = 32 - containedNibbles.Length;
            for (int start = 0; start <= lastStart; start += containsStartStep)
            {
                bool matches = true;
                for (int i = 0; i < containedNibbles.Length; i++)
                {
                    byte expected = containedNibbles[i];
                    if (expected == WildcardNibble)
                    {
                        continue;
                    }

                    int sourceNibble = containsInCanonicalOrder
                        ? CanonicalNibbleToStorageNibble[start + i]
                        : start + i;
                    if (ReadStorageNibble(storage, sourceNibble) != expected)
                    {
                        matches = false;
                        break;
                    }
                }

                if (matches)
                {
                    return true;
                }
            }

            return false;
        }

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
    /// <param name="allowWildcard">Whether X or x characters are accepted as wildcard markers alongside hexadecimal digits.<br/></param>
    /// <returns>The cleaned lowercase pattern core.</returns>
    private static string Clean(string value, bool allowWildcard = true)
    {
        Span<char> cleaned = stackalloc char[32];
        int count = 0;
        foreach (char c in value)
        {
            if (Uri.IsHexDigit(c) || (allowWildcard && (c == 'x' || c == 'X')))
            {
                if (count >= 32)
                {
                    throw new FormatException("GUID pattern cannot exceed 32 nibbles.");
                }

                cleaned[count++] = char.ToLowerInvariant(c);
                continue;
            }

            if (c is '-' or '{' or '}' or '(' or ')' || char.IsWhiteSpace(c))
            {
                continue;
            }

            throw new FormatException($"GUID pattern character '{c}' is invalid. Use hexadecimal digits and 'x' wildcard nibbles only.");
        }

        if (count == 0)
        {
            throw new FormatException("GUID pattern must contain at least one hex digit or wildcard.");
        }

        return new string(cleaned[..count]);
    }

    /// <summary>
    /// Parses a cleaned canonical hexadecimal core to nibble values.<br/>
    /// The internal wildcard marker is retained only for pattern containment; slice callers reject wildcard input before this method is reached.<br/>
    /// </summary>
    /// <param name="core">The cleaned lowercase hexadecimal or wildcard core.</param>
    /// <returns>The canonical nibble sequence.</returns>
    private static byte[] ParseNibbles(string core)
    {
        byte[] nibbles = new byte[core.Length];
        for (int i = 0; i < core.Length; i++)
        {
            nibbles[i] = core[i] == 'x' ? WildcardNibble : HexToNibble(core[i]);
        }

        return nibbles;
    }

    /// <summary>
    /// Validates canonical GUID slice bounds.<br/>
    /// </summary>
    /// <param name="startNibble">The zero-based canonical starting nibble.</param>
    /// <param name="nibbleCount">The positive nibble count.</param>
    private static void ValidateSlice(int startNibble, int nibbleCount)
    {
        if (startNibble is < 0 or > 31)
        {
            throw new ArgumentOutOfRangeException(nameof(startNibble), startNibble, "GUID slice start nibble must be 0 through 31.");
        }

        if (nibbleCount <= 0 || startNibble + nibbleCount > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(nibbleCount), nibbleCount, "GUID slice must contain at least one nibble and remain within the 32 canonical GUID nibbles.");
        }
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
        => new LibraDexStructuredComponentPredicate(tests, requireLastDayOfMonth, dateTimeKeyEncoding);

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
/// Evaluates one planner-visible Decimal, Single, or Double transform against a typed comparison.<br/>
/// The first execution path is an explicit compact-key scan so correctness remains independent of inverse-range boundary derivation.<br/>
/// </summary>
internal sealed class LibraDexNumericTransformPredicate
{
    private readonly Type keyType;
    private readonly LibraDexNumericTransformDescriptor transform;
    private readonly LibraDexConditionOperatorKind comparison;
    private readonly object?[] operands;

    /// <summary>
    /// Initializes an executable transformed numeric comparison after materialization has validated its operands.<br/>
    /// </summary>
    /// <param name="keyType">The exact Decimal, Single, or Double index key type.<br/></param>
    /// <param name="transform">The native transform applied before comparison.<br/></param>
    /// <param name="comparison">The scalar, range, or membership comparison to evaluate.<br/></param>
    /// <param name="operands">The already materialized typed operands.<br/></param>
    internal LibraDexNumericTransformPredicate(
        Type keyType,
        LibraDexNumericTransformDescriptor transform,
        LibraDexConditionOperatorKind comparison,
        object?[] operands)
    {
        ArgumentNullException.ThrowIfNull(keyType);
        ArgumentNullException.ThrowIfNull(operands);
        if (keyType != typeof(decimal) && keyType != typeof(float) && keyType != typeof(double))
            throw new NotSupportedException($"Numeric transform predicates require Decimal, Single, or Double keys; received {keyType.FullName}.");

        this.keyType = keyType;
        this.transform = transform;
        this.comparison = comparison;
        this.operands = operands;
    }

    /// <summary>
    /// Applies the native transform and evaluates the configured typed comparison for one decoded index key.<br/>
    /// </summary>
    /// <param name="value">The decoded Decimal, Single, or Double key.<br/></param>
    /// <returns><see langword="true"/> when the transformed key satisfies the comparison.<br/></returns>
    internal bool Matches(object value)
    {
        object transformed = value switch
        {
            decimal typed when keyType == typeof(decimal) => transform.Kind switch
            {
                LibraDexNumericTransformKind.Round => decimal.Round(typed, transform.Digits, transform.MidpointRounding),
                LibraDexNumericTransformKind.Floor => decimal.Floor(typed),
                LibraDexNumericTransformKind.Ceiling => decimal.Ceiling(typed),
                LibraDexNumericTransformKind.Truncate => decimal.Truncate(typed),
                _ => throw new ArgumentOutOfRangeException(nameof(transform), transform.Kind, "Unknown Decimal transform.")
            },
            float typed when keyType == typeof(float) => transform.Kind switch
            {
                LibraDexNumericTransformKind.Round => MathF.Round(typed, transform.Digits, transform.MidpointRounding),
                LibraDexNumericTransformKind.Floor => MathF.Floor(typed),
                LibraDexNumericTransformKind.Ceiling => MathF.Ceiling(typed),
                LibraDexNumericTransformKind.Truncate => MathF.Truncate(typed),
                _ => throw new ArgumentOutOfRangeException(nameof(transform), transform.Kind, "Unknown Single transform.")
            },
            double typed when keyType == typeof(double) => transform.Kind switch
            {
                LibraDexNumericTransformKind.Round => Math.Round(typed, transform.Digits, transform.MidpointRounding),
                LibraDexNumericTransformKind.Floor => Math.Floor(typed),
                LibraDexNumericTransformKind.Ceiling => Math.Ceiling(typed),
                LibraDexNumericTransformKind.Truncate => Math.Truncate(typed),
                _ => throw new ArgumentOutOfRangeException(nameof(transform), transform.Kind, "Unknown Double transform.")
            },
            _ => throw new InvalidOperationException($"Numeric transform predicate expected {keyType.FullName}, received {value.GetType().FullName}.")
        };

        return comparison switch
        {
            LibraDexConditionOperatorKind.EqualTo => Equal(transformed, RequireOperand(0)),
            LibraDexConditionOperatorKind.NotEqualTo => !Equal(transformed, RequireOperand(0)),
            LibraDexConditionOperatorKind.GreaterThan => Compare(transformed, RequireOperand(0), static value => value > 0),
            LibraDexConditionOperatorKind.GreaterOrEqual => Compare(transformed, RequireOperand(0), static value => value >= 0),
            LibraDexConditionOperatorKind.LessThan => Compare(transformed, RequireOperand(0), static value => value < 0),
            LibraDexConditionOperatorKind.LessOrEqual => Compare(transformed, RequireOperand(0), static value => value <= 0),
            LibraDexConditionOperatorKind.Between => Compare(transformed, RequireOperand(0), static value => value >= 0) &&
                                                     Compare(transformed, RequireOperand(1), static value => value <= 0),
            LibraDexConditionOperatorKind.NotBetween => Compare(transformed, RequireOperand(0), static value => value < 0) ||
                                                        Compare(transformed, RequireOperand(1), static value => value > 0),
            LibraDexConditionOperatorKind.InSet => InSet(transformed, RequireMembershipOperand()),
            LibraDexConditionOperatorKind.NotInSet => !InSet(transformed, RequireMembershipOperand()),
            _ => throw new NotSupportedException($"Numeric transform comparison {comparison} is not supported.")
        };
    }

    /// <summary>
    /// Returns one scalar operand after verifying its presence and exact key type.<br/>
    /// </summary>
    /// <param name="index">The zero-based operand position.<br/></param>
    /// <returns>The validated scalar operand.<br/></returns>
    private object RequireOperand(int index)
    {
        if ((uint)index >= (uint)operands.Length || operands[index] is null)
            throw new InvalidOperationException($"Numeric transform comparison {comparison} requires non-null operand {index}.");

        object value = operands[index]!;
        if (value.GetType() != keyType)
            throw new InvalidOperationException($"Numeric transform comparison expected {keyType.FullName} operand {index}, received {value.GetType().FullName}.");

        return value;
    }

    /// <summary>
    /// Returns the non-null enumerable membership operand without confusing its array type with one scalar key.<br/>
    /// </summary>
    /// <returns>The captured membership enumerable.<br/></returns>
    private object RequireMembershipOperand()
    {
        if (operands.Length == 0 || operands[0] is null)
            throw new InvalidOperationException($"Numeric transform comparison {comparison} requires a non-null membership operand.");

        return operands[0]!;
    }

    /// <summary>
    /// Evaluates exact CLR equality after requiring the right operand to match the index key type.<br/>
    /// </summary>
    /// <param name="left">The transformed key.<br/></param>
    /// <param name="right">The comparison operand.<br/></param>
    /// <returns><see langword="true"/> when the values are equal.<br/></returns>
    private bool Equal(object left, object right)
    {
        if (right.GetType() != keyType)
            throw new InvalidOperationException($"Numeric transform comparison expected {keyType.FullName}, received {right.GetType().FullName}.");

        return left.Equals(right);
    }

    /// <summary>
    /// Performs an ordered typed comparison while keeping floating NaN outside ordered relations.<br/>
    /// </summary>
    /// <param name="left">The transformed key.<br/></param>
    /// <param name="right">The comparison operand.<br/></param>
    /// <param name="accept">The relation applied to the typed comparison result.<br/></param>
    /// <returns><see langword="true"/> when the requested ordered relation is satisfied.<br/></returns>
    private bool Compare(object left, object right, Func<int, bool> accept)
    {
        if (right.GetType() != keyType)
            throw new InvalidOperationException($"Numeric transform comparison expected {keyType.FullName}, received {right.GetType().FullName}.");
        if ((left is float leftSingle && float.IsNaN(leftSingle)) ||
            (right is float rightSingle && float.IsNaN(rightSingle)) ||
            (left is double leftDouble && double.IsNaN(leftDouble)) ||
            (right is double rightDouble && double.IsNaN(rightDouble)))
        {
            return false;
        }

        int value = left switch
        {
            decimal typed => typed.CompareTo((decimal)right),
            float typed => typed.CompareTo((float)right),
            double typed => typed.CompareTo((double)right),
            _ => throw new InvalidOperationException($"Numeric transform comparison cannot compare {left.GetType().FullName}.")
        };
        return accept(value);
    }

    /// <summary>
    /// Tests the transformed key against a validated typed membership enumerable without materializing another set.<br/>
    /// </summary>
    /// <param name="transformed">The transformed key value.<br/></param>
    /// <param name="values">The captured membership enumerable.<br/></param>
    /// <returns><see langword="true"/> when any member equals the transformed key.<br/></returns>
    private bool InSet(object transformed, object values)
    {
        if (values is not System.Collections.IEnumerable enumerable)
            throw new InvalidOperationException("Numeric transform membership requires an enumerable operand.");

        foreach (object? value in enumerable)
        {
            if (value is not null && Equal(transformed, value))
                return true;
        }

        return false;
    }
}
