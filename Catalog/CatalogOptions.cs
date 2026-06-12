namespace LibraDex;

/// <summary>
/// Public options bag for catalog creation and opening.<br/>
/// Catalog-level policy is the broadest runtime default and can be overridden by index-level or method-level options.<br/>
/// </summary>
public sealed class CatalogOptions
{
    /// <summary>
    /// Gets or initializes the catalog-level default string comparison policy.<br/>
    /// The policy is used only for managed residual comparison or prepared membership cases where a comparer is genuinely required; encoded-key primitives and maintained projections remain the preferred execution routes.<br/>
    /// </summary>
    public LibraDexStringComparisonPolicy? StringComparisonPolicy { get; init; }
}
