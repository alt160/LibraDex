namespace LibraDex;

/// <summary>
/// Public options bag for catalog creation and opening.<br/>
/// Catalog-level policy is the broadest runtime default and can be overridden by index-level or method-level options.<br/>
/// </summary>
public sealed class CatalogOptions
{
    /// <summary>
    /// Gets a reusable options instance for catalogs whose indexes must use UInt64 scalar identities.<br/>
    /// This is intended for owners such as Abraxas where the external record identity source is fixed at 64 bits.<br/>
    /// </summary>
    public static CatalogOptions UInt64Identities { get; } = new()
    {
        RequiredIdentityType = typeof(ulong)
    };

    /// <summary>
    /// Gets or initializes the required scalar identity CLR type for every index created, opened, or queried through this catalog.<br/>
    /// A null value preserves LibraDex's normal mixed-identity catalog behavior; setting this to <c>typeof(ulong)</c> makes the catalog fail fast on non-UInt64 or non-scalar identity routes.<br/>
    /// </summary>
    public Type? RequiredIdentityType { get; init; }

    /// <summary>
    /// Gets or initializes the catalog-level default string comparison policy.<br/>
    /// The policy is used only for managed residual comparison or prepared membership cases where a comparer is genuinely required; encoded-key primitives and maintained projections remain the preferred execution routes.<br/>
    /// </summary>
    public LibraDexStringComparisonPolicy? StringComparisonPolicy { get; init; }

    /// <summary>
    /// Gets or initializes the catalog-level diagnostics collection level.<br/>
    /// `Off` is the steady-state default so query and write hot paths do not pay for diagnostic timing or counters unless the caller explicitly opts in.<br/>
    /// `Detailed` may enable fail-fast diagnostics for unsupported same-session write overlap; it does not queue writers or provide a published-reader view.<br/>
    /// </summary>
    public LibraDexDiagnosticsLevel DiagnosticsLevel { get; init; } = LibraDexDiagnosticsLevel.Off;
}
