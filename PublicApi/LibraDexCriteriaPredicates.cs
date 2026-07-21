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
    NotMatchesPattern = 20
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
    private readonly Regex? regex;

    private LibraDexStringPatternPredicate(
        LibraDexStringPatternMode mode,
        string value,
        string? upperValue,
        IReadOnlyCollection<string>? setValues,
        ISet<string>? membershipSet,
        LibraDexStringComparisonPolicy policy,
        int? matchGroupNumber,
        Regex? regex)
    {
        this.mode = mode;
        this.value = value;
        this.upperValue = upperValue;
        this.setValues = setValues;
        this.membershipSet = membershipSet;
        this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
        this.matchGroupNumber = matchGroupNumber;
        this.regex = regex;
    }

    internal static LibraDexStringPatternPredicate Create(LibraDexStringPatternMode mode, string value, bool ignoreCase, string? culture)
        => Create(mode, value, LibraDexStringComparisonPolicy.FromLegacy(ignoreCase, culture));

    internal static LibraDexStringPatternPredicate Create(LibraDexStringPatternMode mode, string value, LibraDexStringComparisonPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new LibraDexStringPatternPredicate(mode, value, null, null, null, policy, matchGroupNumber: null, regex: null);
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

    internal static LibraDexStringPatternPredicate Create(LibraDexStringPatternMode mode, string value, string upperValue, LibraDexStringComparisonPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(upperValue);
        return new LibraDexStringPatternPredicate(mode, value, upperValue, null, null, policy, matchGroupNumber: null, regex: null);
    }

    /// <summary>
    /// Creates a boolean regular-expression predicate over one string key.<br/>
    /// String patterns are compiled by the executor with comparison-policy options, allowing the condition builder to keep the low-friction `ignoreCase` flag while still using regex semantics.<br/>
    /// </summary>
    /// <param name="mode">Whether the regex match is positive or negated.</param>
    /// <param name="pattern">The regular expression pattern.</param>
    /// <param name="policy">The comparison policy used to derive regex options for string patterns.</param>
    /// <returns>A compiled string predicate descriptor for the exact-index executor.</returns>
    internal static LibraDexStringPatternPredicate CreateRegex(
        LibraDexStringPatternMode mode,
        string pattern,
        LibraDexStringComparisonPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(policy);
        return new LibraDexStringPatternPredicate(mode, pattern, null, null, null, policy, matchGroupNumber: null, regex: null);
    }

    /// <summary>
    /// Creates a boolean regular-expression predicate over one string key from a caller-provided <see cref="Regex"/> instance.<br/>
    /// The supplied regex is reused directly so caller-selected options, timeout, and compiled/interpreted behavior are preserved without reparsing the pattern.<br/>
    /// </summary>
    /// <param name="mode">Whether the regex match is positive or negated.</param>
    /// <param name="regex">The caller-provided regular expression instance.</param>
    /// <param name="policy">The comparison policy used only by surrounding condition metadata; regex matching uses the supplied instance options.</param>
    /// <returns>A compiled string predicate descriptor for the exact-index executor.</returns>
    internal static LibraDexStringPatternPredicate CreateRegex(
        LibraDexStringPatternMode mode,
        Regex regex,
        LibraDexStringComparisonPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(regex);
        ArgumentNullException.ThrowIfNull(policy);
        return new LibraDexStringPatternPredicate(mode, regex.ToString(), null, null, null, policy, matchGroupNumber: null, regex);
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
        return new LibraDexStringPatternPredicate(mode, pattern, expectedValue, null, null, policy, groupNumber, regex: null);
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

        return new LibraDexStringPatternPredicate(mode, pattern, null, prepared, prepared, policy, groupNumber, regex: null);
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

        return new LibraDexStringPatternPredicate(mode, captured.First(), null, captured, membershipSet, policy, matchGroupNumber: null, regex: null);
    }

    internal IReadOnlyList<(string Lower, string Upper)> CreateCandidateRanges()
    {
        bool ignoreCase = policy.IgnoreCase;
        string prefix = mode switch
        {
            LibraDexStringPatternMode.MatchesPattern => GetLeadingLiteralPrefix(value),
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
        CompareInfo compareInfo = policy.ResolveCulture().CompareInfo;
        CompareOptions options = policy.CompareOptions;
        return mode switch
        {
            LibraDexStringPatternMode.StartsWith => compareInfo.IsPrefix(candidate, value, options),
            LibraDexStringPatternMode.EndsWith => compareInfo.IsSuffix(candidate, value, options),
            LibraDexStringPatternMode.Contains => compareInfo.IndexOf(candidate, value, options) >= 0,
            LibraDexStringPatternMode.MatchesPattern => MatchesWildcard(candidate, value, compareInfo, options),
            LibraDexStringPatternMode.NotMatchesPattern => !MatchesWildcard(candidate, value, compareInfo, options),
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
    /// Attempts to compile this string predicate into an ordinal UTF-8 byte predicate.<br/>
    /// The caller supplies the same operand transform used by the selected physical projection, allowing folded-text scans to compare already-folded key bytes against already-folded criteria bytes without decoding each candidate key.<br/>
    /// Predicates that require regex, wildcard, capture, custom managed comparison, or unnormalized ignore-case exact-index semantics return <see langword="false"/> so the executor can keep the existing managed string fallback.<br/>
    /// </summary>
    /// <param name="operandTransform">Transforms developer-facing operands into the bytes stored by the selected string projection.</param>
    /// <param name="allowCaseNormalizedBytes">True when the selected projection already stores keys normalized for the predicate's case policy.</param>
    /// <param name="matcher">Receives the compiled byte matcher when the predicate can execute over UTF-8 bytes.</param>
    /// <returns><see langword="true"/> when byte-native residual comparison is safe for this predicate.</returns>
    internal bool TryCreateUtf8ByteMatcher(
        Func<string?, string?> operandTransform,
        bool allowCaseNormalizedBytes,
        out LibraDexUtf8StringPatternPredicate? matcher)
    {
        ArgumentNullException.ThrowIfNull(operandTransform);
        matcher = null;
        if (!CanUseUtf8ByteMatcher(allowCaseNormalizedBytes))
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

        matcher = new LibraDexUtf8StringPatternPredicate(mode, expected, upper, set);
        return true;
    }

    /// <summary>
    /// Determines whether this predicate's semantics can be represented by ordinal byte comparison over already-projected UTF-8 key bytes.<br/>
    /// Folded projection callers may permit ignore-case policies because both candidate and criteria bytes are normalized before comparison; exact-index callers must stay case-sensitive to avoid changing .NET comparison semantics.<br/>
    /// </summary>
    /// <param name="allowCaseNormalizedBytes">True when the selected projection normalizes case for both keys and operands.</param>
    /// <returns><see langword="true"/> when the byte matcher can preserve the intended comparison contract.</returns>
    private bool CanUseUtf8ByteMatcher(bool allowCaseNormalizedBytes)
    {
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
    /// <param name="compareInfo">The culture-specific comparison engine.</param>
    /// <param name="options">The comparison options selected by the condition.</param>
    /// <returns>The .NET comparison result.</returns>
    private static int Compare(string candidate, string expected, CompareInfo compareInfo, CompareOptions options)
        => compareInfo.Compare(candidate, expected, options);

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

    private static bool MatchesWildcard(string candidate, string pattern, CompareInfo compareInfo, CompareOptions options)
        => MatchesWildcardCore(candidate, 0, pattern, 0, compareInfo, options);

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
/// Executes a compiled string-pattern residual directly against UTF-8 key payload bytes.<br/>
/// The matcher deliberately owns already-transformed criteria bytes and performs only ordinal byte operations, keeping scan-backed exact/folded string predicates away from per-row string allocation.<br/>
/// </summary>
internal sealed class LibraDexUtf8StringPatternPredicate
{
    private readonly LibraDexStringPatternMode mode;
    private readonly byte[] value;
    private readonly byte[]? upperValue;
    private readonly byte[][]? setValues;

    /// <summary>
    /// Initializes a byte-native string pattern predicate from projection-compatible UTF-8 operands.<br/>
    /// The caller prepares operands once per query using the same transform as the selected physical projection; execution then compares candidate bytes directly.<br/>
    /// </summary>
    /// <param name="mode">The string predicate mode.</param>
    /// <param name="value">The primary operand bytes.</param>
    /// <param name="upperValue">The upper operand bytes for between-style predicates.</param>
    /// <param name="setValues">The membership operand bytes for set-style predicates.</param>
    internal LibraDexUtf8StringPatternPredicate(
        LibraDexStringPatternMode mode,
        byte[] value,
        byte[]? upperValue,
        byte[][]? setValues)
    {
        this.mode = mode;
        this.value = value;
        this.upperValue = upperValue;
        this.setValues = setValues;
    }

    /// <summary>
    /// Tests one UTF-8 key payload against this compiled byte predicate.<br/>
    /// The candidate span must exclude LibraDex's string sentinel byte and represent the same exact or folded projection selected when the matcher was created.<br/>
    /// </summary>
    /// <param name="candidate">The candidate UTF-8 payload bytes.</param>
    /// <returns><see langword="true"/> when the candidate satisfies the predicate.</returns>
    internal bool Matches(ReadOnlySpan<byte> candidate)
    {
        return mode switch
        {
            LibraDexStringPatternMode.StartsWith => candidate.StartsWith(value),
            LibraDexStringPatternMode.EndsWith => candidate.EndsWith(value),
            LibraDexStringPatternMode.Contains => candidate.IndexOf(value) >= 0,
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
    }

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
                    TimeSpan.FromTicks(System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(slice[8..]))), value, upperValue),
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
/// Identifies a set operation over two LibraDex query streams.<br/>
/// Set operations are intended to compose ordered query results without forcing callers to materialize and reshape data themselves.<br/>
/// </summary>
