using System.Globalization;

namespace LibraDex;

/// <summary>
/// Identifies a stable string comparison policy kind.<br/>
/// Stable kinds may be persisted by name, while <see cref="Custom"/> is a runtime-only policy with optional descriptive metadata.<br/>
/// </summary>
public enum LibraDexStringComparisonPolicyKind
{
    /// <summary>
    /// Uses invariant-culture, case-sensitive managed comparison semantics.<br/>
    /// </summary>
    Invariant = 0,

    /// <summary>
    /// Uses invariant-culture, case-insensitive managed comparison semantics.<br/>
    /// </summary>
    InvariantIgnoreCase = 1,

    /// <summary>
    /// Uses ordinal, case-sensitive managed equality semantics.<br/>
    /// Ordered text operations still use invariant culture unless a named-culture policy is supplied.<br/>
    /// </summary>
    Ordinal = 2,

    /// <summary>
    /// Uses ordinal, case-insensitive managed equality semantics.<br/>
    /// Ordered text operations still use invariant culture with ignore-case options unless a named-culture policy is supplied.<br/>
    /// </summary>
    OrdinalIgnoreCase = 3,

    /// <summary>
    /// Uses a named .NET culture and explicit comparison options.<br/>
    /// </summary>
    Culture = 4,

    /// <summary>
    /// Uses a caller-supplied runtime comparer.<br/>
    /// Custom comparer instances are not serialized; persisted metadata should record only that a custom policy was used and, when useful, the comparer type name.<br/>
    /// </summary>
    Custom = 5
}

/// <summary>
/// Describes managed string comparison behavior for the cases where LibraDex genuinely needs runtime comparison semantics.<br/>
/// Encoded-byte primitives, maintained projections, sortable codecs, and structured formats should remain the primary routes; this policy is for residual comparison, prepared membership, and caller-requested runtime behavior.<br/>
/// </summary>
public sealed class LibraDexStringComparisonPolicy
{
    /// <summary>
    /// Creates a managed string comparison policy.<br/>
    /// </summary>
    /// <param name="kind">The stable policy kind.</param>
    /// <param name="cultureName">The optional .NET culture name used for ordered comparison.</param>
    /// <param name="compareOptions">The .NET comparison options used for ordered comparison.</param>
    /// <param name="equalityComparer">Optional equality comparer for membership and equality residuals.</param>
    /// <param name="customComparerTypeName">Optional descriptive type name for a custom runtime comparer.</param>
    public LibraDexStringComparisonPolicy(
        LibraDexStringComparisonPolicyKind kind = LibraDexStringComparisonPolicyKind.Invariant,
        string? cultureName = null,
        CompareOptions compareOptions = CompareOptions.None,
        IEqualityComparer<string>? equalityComparer = null,
        string? customComparerTypeName = null)
    {
        Kind = kind;
        CultureName = cultureName ?? string.Empty;
        CompareOptions = compareOptions;
        EqualityComparer = equalityComparer ?? CreateDefaultEqualityComparer(kind);
        CustomComparerTypeName = customComparerTypeName ?? (kind == LibraDexStringComparisonPolicyKind.Custom ? equalityComparer?.GetType().AssemblyQualifiedName : null);
    }

    /// <summary>
    /// Gets the default managed string comparison policy.<br/>
    /// The default is invariant and case-sensitive so exact encoded-key routes remain the natural path unless a caller opts into managed comparison behavior.<br/>
    /// </summary>
    public static LibraDexStringComparisonPolicy Default { get; } = new();

    /// <summary>
    /// Gets an invariant-culture, case-insensitive policy.<br/>
    /// </summary>
    public static LibraDexStringComparisonPolicy InvariantIgnoreCase { get; } = new(LibraDexStringComparisonPolicyKind.InvariantIgnoreCase, compareOptions: CompareOptions.IgnoreCase);

    /// <summary>
    /// Gets an ordinal, case-sensitive policy for equality and membership.<br/>
    /// </summary>
    public static LibraDexStringComparisonPolicy Ordinal { get; } = new(LibraDexStringComparisonPolicyKind.Ordinal, equalityComparer: StringComparer.Ordinal);

    /// <summary>
    /// Gets an ordinal, case-insensitive policy for equality and membership.<br/>
    /// </summary>
    public static LibraDexStringComparisonPolicy OrdinalIgnoreCase { get; } = new(LibraDexStringComparisonPolicyKind.OrdinalIgnoreCase, compareOptions: CompareOptions.IgnoreCase, equalityComparer: StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the stable policy kind.<br/>
    /// </summary>
    public LibraDexStringComparisonPolicyKind Kind { get; }

    /// <summary>
    /// Gets the optional .NET culture name used for ordered comparison.<br/>
    /// Empty means invariant culture.<br/>
    /// </summary>
    public string CultureName { get; }

    /// <summary>
    /// Gets the .NET comparison options used for residual ordered and pattern comparison.<br/>
    /// </summary>
    public CompareOptions CompareOptions { get; }

    /// <summary>
    /// Gets the equality comparer used for residual equality and membership checks.<br/>
    /// Caller-provided hash sets can be reused as-is only when their comparer is compatible with this comparer.<br/>
    /// </summary>
    public IEqualityComparer<string> EqualityComparer { get; }

    /// <summary>
    /// Gets the optional descriptive type name for a runtime custom comparer.<br/>
    /// The value is metadata only; LibraDex does not instantiate custom comparers from persisted type names.<br/>
    /// </summary>
    public string? CustomComparerTypeName { get; }

    /// <summary>
    /// Gets whether this policy includes case-insensitive comparison options.<br/>
    /// </summary>
    public bool IgnoreCase => (CompareOptions & CompareOptions.IgnoreCase) != 0 ||
        ReferenceEquals(EqualityComparer, StringComparer.OrdinalIgnoreCase) ||
        ReferenceEquals(EqualityComparer, StringComparer.InvariantCultureIgnoreCase) ||
        ReferenceEquals(EqualityComparer, StringComparer.CurrentCultureIgnoreCase);

    /// <summary>
    /// Creates a named-culture policy.<br/>
    /// </summary>
    /// <param name="cultureName">The .NET culture name.</param>
    /// <param name="compareOptions">The comparison options for residual ordered and pattern comparison.</param>
    /// <returns>A managed string comparison policy.</returns>
    public static LibraDexStringComparisonPolicy ForCulture(string cultureName, CompareOptions compareOptions = CompareOptions.None)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cultureName);
        return new LibraDexStringComparisonPolicy(LibraDexStringComparisonPolicyKind.Culture, cultureName, compareOptions, CreateCultureEqualityComparer(cultureName, compareOptions));
    }

    /// <summary>
    /// Creates a runtime-only custom comparer policy.<br/>
    /// The comparer instance is held in memory and is not serialized across catalog reopen boundaries.<br/>
    /// </summary>
    /// <param name="equalityComparer">The runtime equality comparer.</param>
    /// <param name="customComparerTypeName">Optional descriptive type name or logical comparer name.</param>
    /// <returns>A runtime custom comparer policy.</returns>
    public static LibraDexStringComparisonPolicy Custom(IEqualityComparer<string> equalityComparer, string? customComparerTypeName = null)
    {
        ArgumentNullException.ThrowIfNull(equalityComparer);
        return new LibraDexStringComparisonPolicy(LibraDexStringComparisonPolicyKind.Custom, equalityComparer: equalityComparer, customComparerTypeName: customComparerTypeName);
    }

    /// <summary>
    /// Creates a legacy-compatible policy from the existing ignore-case and culture operands on string condition methods.<br/>
    /// </summary>
    /// <param name="ignoreCase">Whether the condition requested case-insensitive behavior.</param>
    /// <param name="cultureName">The optional culture name.</param>
    /// <returns>A managed string comparison policy.</returns>
    public static LibraDexStringComparisonPolicy FromLegacy(bool ignoreCase, string? cultureName)
    {
        CompareOptions options = ignoreCase ? CompareOptions.IgnoreCase : CompareOptions.None;
        return string.IsNullOrEmpty(cultureName)
            ? new LibraDexStringComparisonPolicy(ignoreCase ? LibraDexStringComparisonPolicyKind.InvariantIgnoreCase : LibraDexStringComparisonPolicyKind.Invariant, compareOptions: options)
            : ForCulture(cultureName, options);
    }

    /// <summary>
    /// Recreates a standard persisted string comparison policy from catalog metadata.<br/>
    /// Custom policies are runtime-only and return <see langword="null"/> so callers can inspect metadata and attach an explicit comparer if needed.<br/>
    /// </summary>
    /// <param name="kind">The persisted policy kind.</param>
    /// <param name="cultureName">The persisted culture name.</param>
    /// <param name="compareOptions">The persisted comparison options.</param>
    /// <param name="customComparerTypeName">The optional custom comparer type-name sentinel.</param>
    /// <returns>A standard runtime policy, or null for custom runtime-only policies.</returns>
    public static LibraDexStringComparisonPolicy? FromPersisted(
        LibraDexStringComparisonPolicyKind kind,
        string? cultureName,
        CompareOptions compareOptions,
        string? customComparerTypeName = null)
    {
        return kind switch
        {
            LibraDexStringComparisonPolicyKind.Invariant => new LibraDexStringComparisonPolicy(LibraDexStringComparisonPolicyKind.Invariant, compareOptions: compareOptions),
            LibraDexStringComparisonPolicyKind.InvariantIgnoreCase => new LibraDexStringComparisonPolicy(LibraDexStringComparisonPolicyKind.InvariantIgnoreCase, compareOptions: compareOptions | CompareOptions.IgnoreCase),
            LibraDexStringComparisonPolicyKind.Ordinal => Ordinal,
            LibraDexStringComparisonPolicyKind.OrdinalIgnoreCase => OrdinalIgnoreCase,
            LibraDexStringComparisonPolicyKind.Culture => ForCulture(string.IsNullOrEmpty(cultureName) ? CultureInfo.InvariantCulture.Name : cultureName, compareOptions),
            LibraDexStringComparisonPolicyKind.Custom => null,
            _ => null
        };
    }

    /// <summary>
    /// Resolves the culture used for residual ordered and pattern comparison.<br/>
    /// </summary>
    /// <returns>The selected culture.</returns>
    public CultureInfo ResolveCulture()
    {
        return string.IsNullOrEmpty(CultureName)
            ? CultureInfo.InvariantCulture
            : CultureInfo.GetCultureInfo(CultureName);
    }

    /// <summary>
    /// Determines whether a caller-provided string set can be used without rebuilding it under a different comparer.<br/>
    /// </summary>
    /// <param name="comparer">The set comparer to inspect.</param>
    /// <returns><see langword="true"/> when the comparer is compatible with this policy.</returns>
    internal bool IsCompatible(IEqualityComparer<string> comparer)
    {
        return ReferenceEquals(comparer, EqualityComparer) ||
            comparer.GetType() == EqualityComparer.GetType();
    }

    private static IEqualityComparer<string> CreateDefaultEqualityComparer(LibraDexStringComparisonPolicyKind kind)
    {
        return kind switch
        {
            LibraDexStringComparisonPolicyKind.InvariantIgnoreCase => StringComparer.InvariantCultureIgnoreCase,
            LibraDexStringComparisonPolicyKind.Ordinal => StringComparer.Ordinal,
            LibraDexStringComparisonPolicyKind.OrdinalIgnoreCase => StringComparer.OrdinalIgnoreCase,
            _ => StringComparer.InvariantCulture
        };
    }

    private static IEqualityComparer<string> CreateCultureEqualityComparer(string cultureName, CompareOptions compareOptions)
    {
        return (compareOptions & CompareOptions.IgnoreCase) != 0
            ? StringComparer.Create(CultureInfo.GetCultureInfo(cultureName), ignoreCase: true)
            : StringComparer.Create(CultureInfo.GetCultureInfo(cultureName), ignoreCase: false);
    }
}

internal interface ILibraDexStringComparisonPolicyProvider
{
    LibraDexStringComparisonPolicy? StringComparisonPolicy { get; }
}
