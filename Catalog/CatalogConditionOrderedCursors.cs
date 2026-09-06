namespace LibraDex;

public sealed partial class CatalogIdentityGroupIndexes
{
    /// <summary>
    /// Attempts to open a plan-natural identity cursor for one primitive condition after the owning layer has independently proved that each source identity can occur under at most one key.<br/>
    /// LibraDex still materializes and verifies the condition root itself, so a caller cannot accidentally apply the proof to a composite, negated, external, or other duplicate-producing condition shape.<br/>
    /// This bridge is intended for higher-level maintained-index contracts whose logical multiplicity is narrower than the reusable physical facade's general metadata.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The identity type expected by the caller.<br/></typeparam>
    /// <param name="condition">The completed condition to materialize against this identity group.<br/></param>
    /// <param name="identityMultiplicityAlreadyProven">Whether the owning layer has proved one key per identity for the exact logical index represented by the condition.<br/></param>
    /// <param name="cursor">The duplicate-free plan-natural cursor when the condition is one primitive leaf and the proof is present.<br/></param>
    /// <returns><see langword="true"/> only when the caller supplied the multiplicity proof and the materialized condition is one primitive leaf in this identity group.<br/></returns>
    internal bool TryOpenMultiplicityProvenConditionIdentityCursor<TIdentity>(
        LibraDexConditionEndCondition condition,
        bool identityMultiplicityAlreadyProven,
        out LibraDexIdentityCursor<TIdentity>? cursor)
    {
        ArgumentNullException.ThrowIfNull(condition);
        cursor = null;
        if (!identityMultiplicityAlreadyProven ||
            !string.Equals(condition.Group, Name, StringComparison.Ordinal))
        {
            return false;
        }

        IIdentityCriterion criterion = condition.MaterializeWithProjectionBridge(
            name => this[name].Open(),
            ResolveProjectionIndex,
            TryResolveConditionIndex);
        if (criterion.NodeKind != LibraDexIdentityCriterionNodeKind.Leaf ||
            criterion.CriteriaKind is null ||
            !PrimitiveVisitsEachTupleAtMostOnce(criterion.CriteriaKind.Value) ||
            criterion.Index?.IdentityType != typeof(TIdentity))
        {
            return false;
        }

        cursor = new LibraDexIdentityCursor<TIdentity>(
            criterion.IDsWith(
                    IdentityResultOrdering.PlanNatural,
                    IdentityDeduplication.Preserve)
                .Iterate<TIdentity>());
        return true;
    }

    /// <summary>
    /// Attempts to create an exact number of independent physical partitions for one primitive condition after the owning layer has proved one key per source identity.<br/>
    /// Condition materialization and multiplicity checks are identical to the direct single-cursor bridge; success additionally requires the selected physical index shape to implement the exact partition contract for this primitive and current topology.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The identity type expected by the caller.<br/></typeparam>
    /// <param name="condition">The completed condition to materialize against this identity group.<br/></param>
    /// <param name="identityMultiplicityAlreadyProven">Whether the owning layer proved one maintained key per identity for the exact logical index.<br/></param>
    /// <param name="workerCount">The exact number of independent physical workers required.<br/></param>
    /// <param name="partitions">The exact partition set when supported; otherwise <see langword="null"/>.<br/></param>
    /// <returns><see langword="true"/> only when the condition is a safe primitive leaf and its physical index created exactly <paramref name="workerCount"/> partitions.<br/></returns>
    internal bool TryCreateMultiplicityProvenConditionIdentityPartitions<TIdentity>(
        LibraDexConditionEndCondition condition,
        bool identityMultiplicityAlreadyProven,
        int workerCount,
        out LibraDexIdentityPrimitivePartitionSet<TIdentity>? partitions,
        out string? unsupportedReason)
    {
        ArgumentNullException.ThrowIfNull(condition);
        partitions = null;
        unsupportedReason = null;
        if (!identityMultiplicityAlreadyProven)
        {
            unsupportedReason = "Abraxas did not prove one maintained string key per identity for the selected logical index.";
            return false;
        }
        if (workerCount < 2)
        {
            unsupportedReason = "A physical partition request requires at least two workers.";
            return false;
        }
        if (!string.Equals(condition.Group, Name, StringComparison.Ordinal))
        {
            unsupportedReason = "The materialized condition belongs to a different identity group.";
            return false;
        }

        IIdentityCriterion criterion = condition.MaterializeWithProjectionBridge(
            name => this[name].Open(),
            ResolveProjectionIndex,
            TryResolveConditionIndex);
        if (criterion.NodeKind != LibraDexIdentityCriterionNodeKind.Leaf)
        {
            unsupportedReason = $"The materialized condition root is '{criterion.NodeKind}', not one primitive leaf.";
            return false;
        }
        if (criterion.CriteriaKind is null)
        {
            unsupportedReason = "The materialized primitive did not expose a criteria kind.";
            return false;
        }
        if (!PrimitiveVisitsEachTupleAtMostOnce(criterion.CriteriaKind.Value))
        {
            unsupportedReason = $"Primitive '{criterion.CriteriaKind.Value}' can revisit tuples and therefore requires a different exact-worker deduplication contract.";
            return false;
        }
        if (criterion.Index?.IdentityType != typeof(TIdentity))
        {
            unsupportedReason = $"The selected index identity type is '{criterion.Index?.IdentityType}', not '{typeof(TIdentity)}'.";
            return false;
        }
        if (criterion.Index is not IIdentityPrimitivePartitioner<TIdentity> partitioner)
        {
            unsupportedReason = $"Physical index '{criterion.Index.GetType().Name}' does not implement exact primitive partitioning.";
            return false;
        }

        return partitioner.TryCreateIdentityPrimitivePartitions(
            new LibraDexIdentityPrimitiveRequest(criterion.CriteriaKind.Value, criterion.Values),
            workerCount,
            out partitions,
            out unsupportedReason);
    }

    /// <summary>
    /// Identifies direct primitive shapes that visit one physical tuple at most once.<br/>
    /// Membership and multi-range primitives are excluded because duplicate or overlapping operands can repeat an identity even when the maintained index stores only one key for that identity.<br/>
    /// </summary>
    /// <param name="criteriaKind">The materialized primitive kind.<br/></param>
    /// <returns><see langword="true"/> when the primitive is safe under an owning layer's one-key-per-identity proof.<br/></returns>
    private static bool PrimitiveVisitsEachTupleAtMostOnce(LibraDexCriteriaKind criteriaKind)
        => criteriaKind is
            LibraDexCriteriaKind.All or
            LibraDexCriteriaKind.Find or
            LibraDexCriteriaKind.Between or
            LibraDexCriteriaKind.Before or
            LibraDexCriteriaKind.AtOrBefore or
            LibraDexCriteriaKind.After or
            LibraDexCriteriaKind.AtOrAfter or
            LibraDexCriteriaKind.Prefix or
            LibraDexCriteriaKind.Suffix or
            LibraDexCriteriaKind.Contains or
            LibraDexCriteriaKind.Matches or
            LibraDexCriteriaKind.StringPattern or
            LibraDexCriteriaKind.StructuredComponent or
            LibraDexCriteriaKind.GuidPattern or
            LibraDexCriteriaKind.BinaryPattern or
            LibraDexCriteriaKind.BinaryTypedSlice;

    /// <summary>
    /// Attempts to open one identity-only cursor when a completed condition is a single primitive over the exact target index whose key order the caller requested.<br/>
    /// The proof is based on the materialized physical index handle, not merely a matching logical name, so folded, reversed, composite, or other maintained projections cannot be mistaken for the target index's order.<br/>
    /// Successful execution streams the target primitive in the requested direction without materializing a candidate set, intersecting a second complete-index traversal, or applying identity deduplication.<br/>
    /// Callers that require one identity per result must separately prove the target index's identity-key multiplicity contract before using this route.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The identity type expected from the target index.<br/></typeparam>
    /// <param name="targetIndex">The exact physical index whose key order must supply the condition results.<br/></param>
    /// <param name="condition">The completed condition to materialize against this identity group.<br/></param>
    /// <param name="direction">The requested target-index key traversal direction.<br/></param>
    /// <param name="cursor">The direct identity cursor when the physical condition leaf and target index are identical.<br/></param>
    /// <returns><see langword="true"/> only when the condition is one direct primitive over <paramref name="targetIndex"/>; otherwise <see langword="false"/>.<br/></returns>
    internal bool TryOpenDirectConditionIdentityCursor<TIdentity>(
        IIndex targetIndex,
        LibraDexConditionEndCondition condition,
        QueryDirection direction,
        out LibraDexIdentityCursor<TIdentity>? cursor)
    {
        ArgumentNullException.ThrowIfNull(targetIndex);
        ArgumentNullException.ThrowIfNull(condition);
        cursor = null;
        if (!string.Equals(condition.Group, Name, StringComparison.Ordinal) ||
            !string.Equals(targetIndex.Group, Name, StringComparison.Ordinal))
        {
            return false;
        }

        IIdentityCriterion criterion = condition.MaterializeWithProjectionBridge(
            name => string.Equals(name, targetIndex.Name, StringComparison.Ordinal)
                ? targetIndex
                : this[name].Open(),
            ResolveProjectionIndex,
            TryResolveConditionIndex);
        if (criterion.NodeKind != LibraDexIdentityCriterionNodeKind.Leaf ||
            criterion.CriteriaKind is null ||
            !ReferenceEquals(criterion.Index, targetIndex))
        {
            return false;
        }

        cursor = new LibraDexIdentityCursor<TIdentity>(
            IterateDirectConditionIdentities<TIdentity>(criterion, targetIndex, direction));
        return true;
    }

    /// <summary>
    /// Projects identities from one already-proved same-index primitive tuple stream without allocating public tuple wrappers.<br/>
    /// Runtime type validation remains in the iterator so opening the cursor performs no traversal and an incompatible identity contract reports the exact failing ordinal.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The identity type expected by the caller.<br/></typeparam>
    /// <param name="criterion">The materialized direct primitive criterion.<br/></param>
    /// <param name="targetIndex">The exact physical index referenced by the criterion.<br/></param>
    /// <param name="direction">The requested key traversal direction.<br/></param>
    /// <returns>The typed identities in the primitive's native target-index order.<br/></returns>
    private static IEnumerable<TIdentity> IterateDirectConditionIdentities<TIdentity>(
        IIdentityCriterion criterion,
        IIndex targetIndex,
        QueryDirection direction)
    {
        long ordinal = 0;
        foreach (LibraDexObjectTuple tuple in LibraDexConditionCursorExecutor.IterateTargetIndexTuples(
            criterion,
            targetIndex,
            skip: 0,
            take: null,
            direction))
        {
            if (tuple.Identity is not TIdentity identity)
            {
                throw new InvalidCastException(
                    $"Identity at ordinal {ordinal} is {tuple.Identity.GetType().FullName}, not {typeof(TIdentity).FullName}.");
            }

            ordinal++;
            yield return identity;
        }
    }
}
