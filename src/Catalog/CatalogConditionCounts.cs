namespace LibraDex;

public sealed partial class CatalogIdentityGroupIndexes
{
    /// <summary>Counts a single non-repeating primitive after the owning adapter proves one maintained key per identity.<br/>
    /// Uses the same projection-aware materializer as identity cursors; compound, negated and overlapping membership plans decline.<br/>
    /// The proof is internal adapter knowledge, never a new developer-facing index contract.<br/></summary>
    /// <param name="condition">Completed condition in this identity group.<br/></param>
    /// <param name="multiplicityProven">Whether the adapter proved the exact source contribution semantics.<br/></param>
    /// <param name="count">Native physical count when admitted.<br/></param>
    /// <returns>False when exact distinct-record counting still needs the existing fallback.<br/></returns>
    internal bool TryCountMaintainedScalarCondition(LibraDexConditionEndCondition condition, bool multiplicityProven, out long count)
    {
        count = 0;
        if (!multiplicityProven || condition.Group != Name) return false;
        IIdentityCriterion criterion = condition.MaterializeWithProjectionBridge(
            name => this[name], ResolveProjectionIndex, TryResolveConditionIndex);
        if (criterion.NodeKind != LibraDexIdentityCriterionNodeKind.Leaf || criterion.CriteriaKind is null ||
            !(PrimitiveVisitsEachTupleAtMostOnce(criterion.CriteriaKind.Value) ||
              criterion.CriteriaKind is LibraDexCriteriaKind.KeyState or LibraDexCriteriaKind.ScalarNull or LibraDexCriteriaKind.Bitmask) ||
            criterion.Index?.IdentityType != typeof(ulong)) return false;
        count = LibraDexIdentityExecutionPlanner.Count(criterion, IdentityDeduplication.Preserve);
        return true;
    }
}
