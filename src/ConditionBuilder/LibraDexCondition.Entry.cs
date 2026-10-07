using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace LibraDex;

/// <summary>
/// Provides the Abraxas-adopted entry point for LibraDex condition construction.<br/>
/// The builder is identity-group scoped first, then index-name scoped, so copied Abraxas condition grammar maps to LibraDex indexes without preserving object-property-path terminology.<br/>
/// </summary>
public static class LibraDexCondition
{
    /// <summary>
    /// Starts a condition builder for one LibraDex identity group.<br/>
    /// This is the descriptor-level counterpart to the catalog-backed `catalog.IndexSet("group").Where(...)` surface; catalog-backed code should prefer the catalog group because it can validate index metadata earlier.<br/>
    /// </summary>
    /// <param name="group">The identity group name shared by every index referenced by the condition.</param>
    /// <returns>A condition clause builder scoped to the supplied identity group.</returns>
    public static LibraDexConditionClause Group(string group)
    {
        return new LibraDexConditionClause(new LibraDexConditionBuilder(group));
    }

    /// <summary>
    /// Starts a legacy condition builder for one LibraDex identity group.<br/>
    /// Index names referenced inside the builder are resolved later against the caller's opened indexes, which lets generated adapters build reusable condition templates before handles are available.<br/>
    /// </summary>
    /// <param name="group">The identity group name shared by every index referenced by the condition.</param>
    /// <returns>A condition clause builder scoped to the supplied identity group.</returns>
    public static LibraDexConditionClause ForGroup(string group)
    {
        return Group(group);
    }
}
