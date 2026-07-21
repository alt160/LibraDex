using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace LibraDex;

internal enum LibraDexConditionNodeKind
{
    Leaf,
    External,
    Not,
    And,
    Or
}

internal sealed class LibraDexConditionBuilder
{
    private LibraDexConditionNode? current;
    private LibraDexConditionNodeKind? pendingOperation;

    internal LibraDexConditionBuilder(string group)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        Group = group;
    }

    internal string Group { get; }

    internal LibraDexConditionContinueOrEnd AddLeaf(LibraDexConditionLeafDescriptor leaf)
    {
        ArgumentNullException.ThrowIfNull(leaf);
        return AddNode(LibraDexConditionNode.Leaf(leaf));
    }

    internal LibraDexConditionContinueOrEnd AddExternal(Func<LibraDexExternalIdentityContext, bool> filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return AddNode(LibraDexConditionNode.External(filter));
    }

    internal LibraDexConditionContinueOrEnd AddExternalSource(Func<IEnumerable<object>> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return AddNode(LibraDexConditionNode.ExternalSource(source));
    }

    internal LibraDexConditionContinueOrEnd AddGroup(LibraDexConditionEndCondition groupCondition, bool negate = false)
    {
        if (!string.Equals(Group, groupCondition.Group, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A LibraDex condition group can only contain child groups from the same identity group.");
        }

        LibraDexConditionNode node = groupCondition.GetRoot();
        return AddNode(negate ? LibraDexConditionNode.Not(node) : node);
    }

    internal void SetNextOperation(LibraDexConditionNodeKind operation)
    {
        if (operation is LibraDexConditionNodeKind.Leaf or LibraDexConditionNodeKind.Not)
        {
            throw new ArgumentOutOfRangeException(nameof(operation));
        }

        if (current is null)
        {
            throw new InvalidOperationException("A LibraDex condition cannot start with a composition operator.");
        }

        pendingOperation = operation;
    }

    internal LibraDexConditionEndCondition End()
    {
        if (current is null)
        {
            throw new InvalidOperationException("A LibraDex condition must contain at least one clause.");
        }

        if (pendingOperation is not null)
        {
            throw new InvalidOperationException("A LibraDex condition cannot end with a composition operator.");
        }

        return new LibraDexConditionEndCondition(Group, current);
    }

    private LibraDexConditionContinueOrEnd AddNode(LibraDexConditionNode node)
    {
        if (current is null)
        {
            current = node;
        }
        else
        {
            LibraDexConditionNodeKind operation = pendingOperation
                ?? throw new InvalidOperationException("A LibraDex condition requires AND or OR between clauses.");
            current = LibraDexConditionNode.Compose(operation, current, node);
            pendingOperation = null;
        }

        return new LibraDexConditionContinueOrEnd(this);
    }
}

internal sealed class LibraDexConditionNode
{
    private readonly LibraDexConditionLeafDescriptor? leaf;
    private readonly Func<LibraDexExternalIdentityContext, bool>? externalIdentityFilter;
    private readonly Func<IEnumerable<object>>? externalIdentitySource;
    private readonly LibraDexConditionNode? left;
    private readonly LibraDexConditionNode? right;

    private LibraDexConditionNode(
        LibraDexConditionNodeKind kind,
        LibraDexConditionLeafDescriptor? leaf,
        Func<LibraDexExternalIdentityContext, bool>? externalIdentityFilter,
        Func<IEnumerable<object>>? externalIdentitySource,
        LibraDexConditionNode? left,
        LibraDexConditionNode? right)
    {
        Kind = kind;
        this.leaf = leaf;
        this.externalIdentityFilter = externalIdentityFilter;
        this.externalIdentitySource = externalIdentitySource;
        this.left = left;
        this.right = right;
    }

    internal LibraDexConditionNodeKind Kind { get; }

    internal static LibraDexConditionNode Leaf(LibraDexConditionLeafDescriptor leaf)
        => new LibraDexConditionNode(LibraDexConditionNodeKind.Leaf, leaf, externalIdentityFilter: null, externalIdentitySource: null, left: null, right: null);

    internal static LibraDexConditionNode External(Func<LibraDexExternalIdentityContext, bool> filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return new LibraDexConditionNode(LibraDexConditionNodeKind.External, leaf: null, filter, externalIdentitySource: null, left: null, right: null);
    }

    internal static LibraDexConditionNode ExternalSource(Func<IEnumerable<object>> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new LibraDexConditionNode(LibraDexConditionNodeKind.External, leaf: null, externalIdentityFilter: null, source, left: null, right: null);
    }

    internal static LibraDexConditionNode Not(LibraDexConditionNode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        return new LibraDexConditionNode(LibraDexConditionNodeKind.Not, leaf: null, externalIdentityFilter: null, externalIdentitySource: null, left: child, right: null);
    }

    internal static LibraDexConditionNode Compose(
        LibraDexConditionNodeKind kind,
        LibraDexConditionNode left,
        LibraDexConditionNode right)
    {
        if (kind is LibraDexConditionNodeKind.Leaf or LibraDexConditionNodeKind.External or LibraDexConditionNodeKind.Not)
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        return new LibraDexConditionNode(kind, leaf: null, externalIdentityFilter: null, externalIdentitySource: null, left, right);
    }

    internal IReadOnlyList<LibraDexConditionLeafDescriptor> GetLeaves()
    {
        List<LibraDexConditionLeafDescriptor> leaves = new();
        AddLeaves(leaves);
        return leaves;
    }

    internal LibraDexConditionNode Rewrite(Func<LibraDexConditionLeafDescriptor, LibraDexConditionLeafDescriptor> rewriteLeaf)
    {
        return Kind switch
        {
            LibraDexConditionNodeKind.Leaf => Leaf(rewriteLeaf(RequireLeaf())),
            LibraDexConditionNodeKind.External when externalIdentitySource is not null => ExternalSource(RequireExternalIdentitySource()),
            LibraDexConditionNodeKind.External => External(RequireExternalIdentityFilter()),
            LibraDexConditionNodeKind.Not => Not(RequireLeft().Rewrite(rewriteLeaf)),
            LibraDexConditionNodeKind.And => Compose(LibraDexConditionNodeKind.And, RequireLeft().Rewrite(rewriteLeaf), RequireRight().Rewrite(rewriteLeaf)),
            LibraDexConditionNodeKind.Or => Compose(LibraDexConditionNodeKind.Or, RequireLeft().Rewrite(rewriteLeaf), RequireRight().Rewrite(rewriteLeaf)),
            _ => throw new InvalidOperationException($"Unsupported condition node kind {Kind}.")
        };
    }

    internal IIdentityCriterion Materialize(
        string group,
        Func<string, IIndex> resolveIndex,
        Func<LibraDexConditionLeafDescriptor, LibraDexConditionLeafClassification, IIndex?>? resolveProjectionIndex)
    {
        return Kind switch
        {
            LibraDexConditionNodeKind.Leaf => MaterializeLeaf(group, resolveIndex, resolveProjectionIndex),
            LibraDexConditionNodeKind.External when externalIdentitySource is not null => LibraDexIdentityCriterion.ExternalSource(group, RequireExternalIdentitySource()),
            LibraDexConditionNodeKind.External => LibraDexIdentityCriterion.External(group, RequireExternalIdentityFilter()),
            LibraDexConditionNodeKind.Not => RequireLeft().Materialize(group, resolveIndex, resolveProjectionIndex).Not(),
            LibraDexConditionNodeKind.And => RequireLeft().Materialize(group, resolveIndex, resolveProjectionIndex).And(RequireRight().Materialize(group, resolveIndex, resolveProjectionIndex)),
            LibraDexConditionNodeKind.Or => RequireLeft().Materialize(group, resolveIndex, resolveProjectionIndex).Or(RequireRight().Materialize(group, resolveIndex, resolveProjectionIndex)),
            _ => throw new InvalidOperationException($"Unsupported condition node kind {Kind}.")
        };
    }

    private void AddLeaves(List<LibraDexConditionLeafDescriptor> leaves)
    {
        if (Kind == LibraDexConditionNodeKind.Leaf)
        {
            leaves.Add(RequireLeaf());
            return;
        }

        if (Kind == LibraDexConditionNodeKind.External)
        {
            return;
        }

        RequireLeft().AddLeaves(leaves);
        if (Kind != LibraDexConditionNodeKind.Not)
        {
            RequireRight().AddLeaves(leaves);
        }
    }

    private IIdentityCriterion MaterializeLeaf(
        string group,
        Func<string, IIndex> resolveIndex,
        Func<LibraDexConditionLeafDescriptor, LibraDexConditionLeafClassification, IIndex?>? resolveProjectionIndex)
    {
        LibraDexConditionLeafDescriptor descriptor = RequireLeaf();
        IIndex index = resolveIndex(descriptor.IndexName);
        if (index.Group.Length != 0 && !string.Equals(index.Group, group, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Resolved index '{descriptor.IndexName}' belongs to group '{index.Group}', not condition group '{group}'.");
        }

        LibraDexConditionLeafClassification classification = LibraDexConditionEndCondition.ClassifyResolvedLeaf(group, descriptor, index);
        object?[] values = MaterializeOperandValues(descriptor);
        if (classification.ExecutionClass == LibraDexConditionExecutionClass.ProjectionBacked)
        {
            if (resolveProjectionIndex is null)
            {
                if (TryMaterializeProjectionFallback(index, values, descriptor, out IIdentityCriterion? fallbackWithoutBridge))
                {
                    return fallbackWithoutBridge;
                }

                throw new NotSupportedException($"Condition operator {descriptor.Operator} requires a maintained {classification.ProjectionKind} projection bridge.");
            }

            IIndex? projectionIndex = resolveProjectionIndex(descriptor, classification);
            if (projectionIndex is not null)
            {
                try
                {
                    return MaterializeProjectionLeaf(group, descriptor, classification, projectionIndex, values);
                }
                catch (NotSupportedException) when (TryMaterializeProjectionFallback(index, values, descriptor, out IIdentityCriterion? fallbackAfterBridge))
                {
                    return fallbackAfterBridge;
                }
            }

            if (TryMaterializeProjectionFallback(index, values, descriptor, out IIdentityCriterion? fallbackWithoutProjection))
            {
                return fallbackWithoutProjection;
            }

            throw new NotSupportedException($"Condition operator {descriptor.Operator} requires a maintained {classification.ProjectionKind} projection bridge, but the resolver did not return one.");
        }

        if (descriptor.ValueKind == LibraDexConditionValueKind.String &&
            RequiresManagedStringComparison(descriptor, index) &&
            IsStringComparisonOperator(descriptor.Operator))
        {
            return MaterializeStringComparisonLeaf(index, values, descriptor);
        }

        if (descriptor.ValueKind == LibraDexConditionValueKind.String &&
            RequiresManagedStringComparison(descriptor, index) &&
            IsStringMembershipOperator(descriptor.Operator))
        {
            return MaterializeStringMembershipLeaf(index, values, descriptor);
        }

        return MaterializeResolvedPrimitiveLeaf(index, values, descriptor);
    }

    /// <summary>
    /// Tries to express a projection-backed leaf as a visible scan criterion against the exact index when the requested projection is not connected.<br/>
    /// This keeps declared-but-unavailable folded, sort-key, reversed, and binary suffix projections correct across fixed, variable, and composite shape families while preserving the maintained projection as the fast path.<br/>
    /// </summary>
    /// <param name="index">The resolved exact index that owns the condition leaf.</param>
    /// <param name="values">The already materialized operand values.</param>
    /// <param name="descriptor">The condition leaf descriptor requesting the projection.</param>
    /// <param name="criterion">Receives the exact-index scan criterion when the leaf can fall back without losing semantics.</param>
    /// <returns><see langword="true"/> when a correct exact-index fallback was built.</returns>
    private static bool TryMaterializeProjectionFallback(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor,
        [NotNullWhen(true)] out IIdentityCriterion? criterion)
    {
        criterion = null;
        if (descriptor.ValueKind == LibraDexConditionValueKind.String)
        {
            if (IsStringComparisonOperator(descriptor.Operator))
            {
                criterion = MaterializeStringComparisonLeaf(index, values, descriptor);
                return true;
            }

            if (IsStringMembershipOperator(descriptor.Operator))
            {
                criterion = MaterializeStringMembershipLeaf(index, values, descriptor);
                return true;
            }

            if (TryMaterializePatternPrimitiveLeaf(index, values, descriptor, out criterion) &&
                criterion is not null)
            {
                return true;
            }

            criterion = null;
            return false;
        }

        if (descriptor.ValueKind == LibraDexConditionValueKind.Binary &&
            TryMaterializePatternPrimitiveLeaf(index, values, descriptor, out criterion) &&
            criterion is not null)
        {
            return true;
        }

        criterion = null;
        return false;
    }

    /// <summary>
    /// Materializes adopted condition operands exactly once before the resolved leaf is dispatched to a domain materializer.<br/>
    /// This keeps deferred operand evaluation visible and avoids LINQ allocation on the common materialization path.<br/>
    /// </summary>
    /// <param name="descriptor">The leaf descriptor whose operands should be evaluated.</param>
    /// <returns>The materialized operand values.</returns>
    private static object?[] MaterializeOperandValues(LibraDexConditionLeafDescriptor descriptor)
    {
        if (descriptor.Operands.Count == 0)
        {
            return Array.Empty<object?>();
        }

        object?[] values = new object?[descriptor.Operands.Count];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = descriptor.Operands[i].GetValue();
        }

        return values;
    }

    /// <summary>
    /// Dispatches one already-resolved leaf to the narrowest domain materializer that can express it.<br/>
    /// The dispatch is intentionally grouped by execution domain so future operators do not accumulate in one monolithic switch.<br/>
    /// </summary>
    /// <param name="index">The resolved index that owns the leaf.</param>
    /// <param name="values">The already materialized operand values.</param>
    /// <param name="descriptor">The adopted condition leaf descriptor.</param>
    /// <returns>An executable identity criterion leaf or composition.</returns>
    private static IIdentityCriterion MaterializeResolvedPrimitiveLeaf(IIndex index, object?[] values, LibraDexConditionLeafDescriptor descriptor)
    {
        if (TryMaterializeCorePrimitiveLeaf(index, values, descriptor, out IIdentityCriterion? coreCriterion) &&
            coreCriterion is not null)
        {
            return coreCriterion;
        }

        if (TryMaterializePatternPrimitiveLeaf(index, values, descriptor, out IIdentityCriterion? patternCriterion) &&
            patternCriterion is not null)
        {
            return patternCriterion;
        }

        if (TryMaterializeStructuredDatePrimitiveLeaf(index, values, descriptor, out IIdentityCriterion? dateCriterion) &&
            dateCriterion is not null)
        {
            return dateCriterion;
        }

        throw new NotSupportedException($"Condition operator {descriptor.Operator} is not connected to the LibraDex criteria bridge yet.");
    }

    /// <summary>
    /// Tries to materialize scalar, set, bitmask, and other value-domain-neutral primitive leaves.<br/>
    /// These operators do not need a specialized pattern or structured-date bridge once the index has been resolved.<br/>
    /// </summary>
    /// <param name="index">The resolved index that owns the leaf.</param>
    /// <param name="values">The already materialized operand values.</param>
    /// <param name="descriptor">The adopted condition leaf descriptor.</param>
    /// <param name="criterion">Receives the executable criterion when the operator belongs to this domain.</param>
    /// <returns><see langword="true"/> when the operator was handled.</returns>
    private static bool TryMaterializeCorePrimitiveLeaf(IIndex index, object?[] values, LibraDexConditionLeafDescriptor descriptor, out IIdentityCriterion? criterion)
    {
        criterion = descriptor.Operator switch
        {
            LibraDexConditionOperatorKind.All => CreateConditionLeaf(index, LibraDexCriteriaKind.All),
            LibraDexConditionOperatorKind.EqualTo when descriptor.ValueKind == LibraDexConditionValueKind.String && TryGetNullKeyState(values, 0, out NullKey stringEqualState) => CreateConditionLeaf(index, LibraDexCriteriaKind.KeyState, stringEqualState),
            LibraDexConditionOperatorKind.NotEqualTo when descriptor.ValueKind == LibraDexConditionValueKind.String && TryGetNullKeyState(values, 0, out NullKey stringNotEqualState) => CreateConditionLeaf(index, LibraDexCriteriaKind.KeyState, stringNotEqualState).Not(),
            LibraDexConditionOperatorKind.EqualTo when descriptor.ValueKind == LibraDexConditionValueKind.String => CreateConditionLeaf(index, LibraDexCriteriaKind.Find, RequireString(values, 0, descriptor)),
            LibraDexConditionOperatorKind.NotEqualTo when descriptor.ValueKind == LibraDexConditionValueKind.String => CreateOrderedPointExclusionLeaf(index, RequireString(values, 0, descriptor)),
            LibraDexConditionOperatorKind.EqualTo when descriptor.ValueKind == LibraDexConditionValueKind.Binary && TryGetNullKeyState(values, 0, out NullKey binaryEqualState) => CreateConditionLeaf(index, LibraDexCriteriaKind.KeyState, binaryEqualState),
            LibraDexConditionOperatorKind.NotEqualTo when descriptor.ValueKind == LibraDexConditionValueKind.Binary && TryGetNullKeyState(values, 0, out NullKey binaryNotEqualState) => CreateConditionLeaf(index, LibraDexCriteriaKind.KeyState, binaryNotEqualState).Not(),
            LibraDexConditionOperatorKind.GreaterThan when descriptor.ValueKind == LibraDexConditionValueKind.String => CreateConditionLeaf(index, LibraDexCriteriaKind.After, RequireString(values, 0, descriptor)),
            LibraDexConditionOperatorKind.GreaterOrEqual when descriptor.ValueKind == LibraDexConditionValueKind.String => CreateConditionLeaf(index, LibraDexCriteriaKind.AtOrAfter, RequireString(values, 0, descriptor)),
            LibraDexConditionOperatorKind.LessThan when descriptor.ValueKind == LibraDexConditionValueKind.String => CreateConditionLeaf(index, LibraDexCriteriaKind.Before, RequireString(values, 0, descriptor)),
            LibraDexConditionOperatorKind.LessOrEqual when descriptor.ValueKind == LibraDexConditionValueKind.String => CreateConditionLeaf(index, LibraDexCriteriaKind.AtOrBefore, RequireString(values, 0, descriptor)),
            LibraDexConditionOperatorKind.Between when descriptor.ValueKind == LibraDexConditionValueKind.String => CreateConditionLeaf(index, LibraDexCriteriaKind.Between, RequireString(values, 0, descriptor), RequireString(values, 1, descriptor)),
            LibraDexConditionOperatorKind.NotBetween when descriptor.ValueKind == LibraDexConditionValueKind.String => CreateOrderedRangeExclusionLeaf(index, RequireString(values, 0, descriptor), RequireString(values, 1, descriptor)),
            LibraDexConditionOperatorKind.EqualTo => CreateConditionLeaf(index, LibraDexCriteriaKind.Find, RequireValue(values, 0, descriptor)),
            LibraDexConditionOperatorKind.NotEqualTo => CreateOrderedPointExclusionLeaf(index, RequireValue(values, 0, descriptor)),
            LibraDexConditionOperatorKind.GreaterThan => CreateConditionLeaf(index, LibraDexCriteriaKind.After, RequireValue(values, 0, descriptor)),
            LibraDexConditionOperatorKind.GreaterOrEqual => CreateConditionLeaf(index, LibraDexCriteriaKind.AtOrAfter, RequireValue(values, 0, descriptor)),
            LibraDexConditionOperatorKind.LessThan => CreateConditionLeaf(index, LibraDexCriteriaKind.Before, RequireValue(values, 0, descriptor)),
            LibraDexConditionOperatorKind.LessOrEqual => CreateConditionLeaf(index, LibraDexCriteriaKind.AtOrBefore, RequireValue(values, 0, descriptor)),
            LibraDexConditionOperatorKind.Between => CreateConditionLeaf(index, LibraDexCriteriaKind.Between, RequireValue(values, 0, descriptor), RequireValue(values, 1, descriptor)),
            LibraDexConditionOperatorKind.NotBetween => CreateOrderedRangeExclusionLeaf(index, RequireValue(values, 0, descriptor), RequireValue(values, 1, descriptor)),
            LibraDexConditionOperatorKind.BitAndEqualTo => MaterializeBitmaskLeaf(index, values, descriptor, LibraDexBitmaskComparisonMode.EqualTo),
            LibraDexConditionOperatorKind.BitAndNotEqualTo => MaterializeBitmaskLeaf(index, values, descriptor, LibraDexBitmaskComparisonMode.NotEqualTo),
            LibraDexConditionOperatorKind.InSet => CreateMembershipLeaf(index, values, descriptor),
            LibraDexConditionOperatorKind.NotInSet => CreateMembershipLeaf(index, values, descriptor).Not(),
            LibraDexConditionOperatorKind.ScalarNullState => CreateConditionLeaf(index, LibraDexCriteriaKind.ScalarNull, RequireEnum<ScalarNull>(values, 0, descriptor)),
            _ => null
        };

        return criterion is not null;
    }

    /// <summary>
    /// Tries to materialize string, GUID, binary, and composite pattern-style leaves.<br/>
    /// Unsupported pattern operators still fail explicitly when no projection or scan bridge has been connected for the selected value kind.<br/>
    /// </summary>
    /// <param name="index">The resolved index that owns the leaf.</param>
    /// <param name="values">The already materialized operand values.</param>
    /// <param name="descriptor">The adopted condition leaf descriptor.</param>
    /// <param name="criterion">Receives the executable criterion when the operator belongs to this domain.</param>
    /// <returns><see langword="true"/> when the operator was handled.</returns>
    private static bool TryMaterializePatternPrimitiveLeaf(IIndex index, object?[] values, LibraDexConditionLeafDescriptor descriptor, out IIdentityCriterion? criterion)
    {
        criterion = descriptor.Operator switch
        {
            LibraDexConditionOperatorKind.StartsWith when descriptor.ValueKind == LibraDexConditionValueKind.String && !RequiresManagedStringComparison(descriptor, index) => MaterializeExactStringPrefixLeaf(index, values, descriptor),
            LibraDexConditionOperatorKind.StartsWith or
            LibraDexConditionOperatorKind.EndsWith or
            LibraDexConditionOperatorKind.Contains or
            LibraDexConditionOperatorKind.MatchesPattern when descriptor.ValueKind == LibraDexConditionValueKind.Guid => MaterializeGuidPatternLeaf(index, values, descriptor),
            LibraDexConditionOperatorKind.StartsWith or
            LibraDexConditionOperatorKind.EndsWith or
            LibraDexConditionOperatorKind.Contains or
            LibraDexConditionOperatorKind.MatchesPattern or
            LibraDexConditionOperatorKind.BinarySliceEqual when descriptor.ValueKind == LibraDexConditionValueKind.Binary => MaterializeBinaryPatternLeaf(index, values, descriptor),
            LibraDexConditionOperatorKind.BinaryTypedSliceEqualTo or
            LibraDexConditionOperatorKind.BinaryTypedSliceGreaterThan or
            LibraDexConditionOperatorKind.BinaryTypedSliceGreaterOrEqual or
            LibraDexConditionOperatorKind.BinaryTypedSliceLessThan or
            LibraDexConditionOperatorKind.BinaryTypedSliceLessOrEqual or
            LibraDexConditionOperatorKind.BinaryTypedSliceBetween or
            LibraDexConditionOperatorKind.BinaryTypedSliceStartsWith or
            LibraDexConditionOperatorKind.BinaryTypedSliceContains or
            LibraDexConditionOperatorKind.BinaryTypedSliceBitAndEqualTo or
            LibraDexConditionOperatorKind.BinaryTypedSliceBitAndNotEqualTo when descriptor.ValueKind == LibraDexConditionValueKind.Binary => MaterializeBinaryTypedSliceLeaf(index, values, descriptor),
            LibraDexConditionOperatorKind.CompositeMatch when descriptor.ValueKind == LibraDexConditionValueKind.Composite => MaterializeCompositeLeaf(index, values, descriptor),
            LibraDexConditionOperatorKind.StartsWith or
            LibraDexConditionOperatorKind.EndsWith or
            LibraDexConditionOperatorKind.Contains or
            LibraDexConditionOperatorKind.MatchesPattern or
            LibraDexConditionOperatorKind.NotMatchesPattern or
            LibraDexConditionOperatorKind.RegexMatches or
            LibraDexConditionOperatorKind.NotRegexMatches or
            LibraDexConditionOperatorKind.MatchesWith or
            LibraDexConditionOperatorKind.NotMatchesWith or
            LibraDexConditionOperatorKind.MatchesInSet or
            LibraDexConditionOperatorKind.NotMatchesInSet when descriptor.ValueKind == LibraDexConditionValueKind.String => MaterializeStringPatternLeaf(index, values, descriptor),
            LibraDexConditionOperatorKind.StartsWith or
            LibraDexConditionOperatorKind.EndsWith or
            LibraDexConditionOperatorKind.Contains or
            LibraDexConditionOperatorKind.MatchesPattern or
            LibraDexConditionOperatorKind.NotMatchesPattern or
            LibraDexConditionOperatorKind.RegexMatches or
            LibraDexConditionOperatorKind.NotRegexMatches or
            LibraDexConditionOperatorKind.MatchesWith or
            LibraDexConditionOperatorKind.NotMatchesWith or
            LibraDexConditionOperatorKind.MatchesInSet or
            LibraDexConditionOperatorKind.NotMatchesInSet => throw new NotSupportedException($"Condition operator {descriptor.Operator} requires an explicit maintained projection bridge before it can materialize."),
            _ => null
        };

        return criterion is not null;
    }

    /// <summary>
    /// Tries to materialize structured date, calendar component, and time-of-day condition leaves.<br/>
    /// Keeping this switch isolated makes the calendar-specific branch family easier to review independently from scalar and pattern operators.<br/>
    /// </summary>
    /// <param name="index">The resolved index that owns the leaf.</param>
    /// <param name="values">The already materialized operand values.</param>
    /// <param name="descriptor">The adopted condition leaf descriptor.</param>
    /// <param name="criterion">Receives the executable criterion when the operator belongs to this domain.</param>
    /// <returns><see langword="true"/> when the operator was handled.</returns>
    private static bool TryMaterializeStructuredDatePrimitiveLeaf(IIndex index, object?[] values, LibraDexConditionLeafDescriptor descriptor, out IIdentityCriterion? criterion)
    {
        criterion = descriptor.Operator switch
        {
            LibraDexConditionOperatorKind.YearEqualTo => MaterializeStructuredDateYearLeaf(index, values, descriptor),
            LibraDexConditionOperatorKind.YearNotEqualTo => MaterializeStructuredDateYearExclusionLeaf(index, values, descriptor),
            LibraDexConditionOperatorKind.YearIn => MaterializeStructuredDateYearInLeaf(index, values, descriptor),
            LibraDexConditionOperatorKind.YearNotIn => MaterializeStructuredDateYearInLeaf(index, values, descriptor).Not(),
            LibraDexConditionOperatorKind.YearRange => MaterializeStructuredDateYearRangeLeaf(index, values, descriptor),
            LibraDexConditionOperatorKind.YearNotRange => MaterializeStructuredDateYearRangeExclusionLeaf(index, values, descriptor),
            LibraDexConditionOperatorKind.YearOnOrAfter => MaterializeStructuredDateYearOnOrAfterLeaf(index, values, descriptor),
            LibraDexConditionOperatorKind.YearOnOrBefore => MaterializeStructuredDateYearOnOrBeforeLeaf(index, values, descriptor),
            LibraDexConditionOperatorKind.YearMonth => MaterializeStructuredDateYearMonthLeaf(index, values, descriptor),
            LibraDexConditionOperatorKind.YearMonthDay => MaterializeStructuredDateYearMonthDayLeaf(index, values, descriptor),
            LibraDexConditionOperatorKind.YearMonthIn => MaterializeStructuredDateYearMonthInLeaf(index, values, descriptor),
            LibraDexConditionOperatorKind.YearMonthDayIn => MaterializeStructuredDateYearMonthDayInLeaf(index, values, descriptor),
            LibraDexConditionOperatorKind.YearInMonths => MaterializeStructuredDateYearInMonthsLeaf(index, values, descriptor),
            LibraDexConditionOperatorKind.YearQuarter => MaterializeStructuredDateYearQuarterLeaf(index, values, descriptor),
            LibraDexConditionOperatorKind.IsToday => MaterializeStructuredDateRelativeDayLeaf(index, DateTime.UtcNow.Date, descriptor),
            LibraDexConditionOperatorKind.IsYesterday => MaterializeStructuredDateRelativeDayLeaf(index, DateTime.UtcNow.Date.AddDays(-1), descriptor),
            LibraDexConditionOperatorKind.IsInLastDays => MaterializeStructuredDateInLastDaysLeaf(index, values, descriptor),
            LibraDexConditionOperatorKind.IsInLastHours => MaterializeStructuredDateInLastHoursLeaf(index, values, descriptor),
            LibraDexConditionOperatorKind.IsInLastMinutes => MaterializeStructuredDateInLastMinutesLeaf(index, values, descriptor),
            LibraDexConditionOperatorKind.MonthEqualTo => MaterializeStructuredDateComponentLeaf(index, CreateComponentPredicate(MonthTest(RequireInt32(values, 0, descriptor))), descriptor),
            LibraDexConditionOperatorKind.MonthIn => MaterializeStructuredDateComponentLeaf(index, CreateComponentPredicate(MonthTest(RequireInt32Set(values, 0, descriptor))), descriptor),
            LibraDexConditionOperatorKind.MonthNotIn => MaterializeStructuredDateComponentLeaf(index, CreateComponentPredicate(MonthTest(RequireInt32Set(values, 0, descriptor), negate: true)), descriptor),
            LibraDexConditionOperatorKind.MonthRange => MaterializeStructuredDateComponentLeaf(index, CreateComponentPredicate(MonthTest(RequireInt32Range(values, descriptor))), descriptor),
            LibraDexConditionOperatorKind.MonthNotRange => MaterializeStructuredDateComponentLeaf(index, CreateComponentPredicate(MonthTest(RequireInt32Range(values, descriptor), negate: true)), descriptor),
            LibraDexConditionOperatorKind.DayEqualTo => MaterializeStructuredDateComponentLeaf(index, CreateComponentPredicate(DayTest(RequireInt32(values, 0, descriptor))), descriptor),
            LibraDexConditionOperatorKind.DayIn => MaterializeStructuredDateComponentLeaf(index, CreateComponentPredicate(DayTest(RequireInt32Set(values, 0, descriptor))), descriptor),
            LibraDexConditionOperatorKind.DayRange => MaterializeStructuredDateComponentLeaf(index, CreateComponentPredicate(DayTest(RequireInt32Range(values, descriptor))), descriptor),
            LibraDexConditionOperatorKind.DayNotRange => MaterializeStructuredDateComponentLeaf(index, CreateComponentPredicate(DayTest(RequireInt32Range(values, descriptor), negate: true)), descriptor),
            LibraDexConditionOperatorKind.MonthDay => MaterializeStructuredDateMonthDayComponentLeaf(index, values, descriptor),
            LibraDexConditionOperatorKind.QuarterEqualTo => MaterializeStructuredDateComponentLeaf(index, CreateComponentPredicate(MonthTest(MonthsForQuarter(RequireInt32(values, 0, descriptor), descriptor))), descriptor),
            LibraDexConditionOperatorKind.InQuarter => MaterializeStructuredDateComponentLeaf(index, CreateComponentPredicate(MonthTest(MonthsForQuarter(RequireInt32(values, 0, descriptor), descriptor))), descriptor),
            LibraDexConditionOperatorKind.InQuarterRange => MaterializeStructuredDateComponentLeaf(index, CreateComponentPredicate(MonthTest(MonthsForQuarterRange(RequireInt32(values, 0, descriptor), RequireInt32(values, 1, descriptor), descriptor))), descriptor),
            LibraDexConditionOperatorKind.IsQuarterStart => MaterializeStructuredDateComponentLeaf(index, CreateComponentPredicate(MonthTest(new[] { 1, 4, 7, 10 }), DayTest(1)), descriptor),
            LibraDexConditionOperatorKind.IsQuarterEnd => MaterializeStructuredDateComponentLeaf(index, CreateComponentPredicate(requireLastDayOfMonth: true, MonthTest(new[] { 3, 6, 9, 12 })), descriptor),
            LibraDexConditionOperatorKind.IsHalfYearStart => MaterializeStructuredDateComponentLeaf(index, CreateComponentPredicate(MonthTest(new[] { 1, 7 }), DayTest(1)), descriptor),
            LibraDexConditionOperatorKind.IsHalfYearEnd => MaterializeStructuredDateComponentLeaf(index, CreateComponentPredicate(requireLastDayOfMonth: true, MonthTest(new[] { 6, 12 })), descriptor),
            LibraDexConditionOperatorKind.IsFirstOfMonth => MaterializeStructuredDateComponentLeaf(index, CreateComponentPredicate(DayTest(1)), descriptor),
            LibraDexConditionOperatorKind.IsLastOfMonth => MaterializeStructuredDateComponentLeaf(index, new LibraDexStructuredComponentPredicate(Array.Empty<LibraDexStructuredComponentTest>(), requireLastDayOfMonth: true), descriptor),
            LibraDexConditionOperatorKind.IsWeekend => MaterializeStructuredDateComponentLeaf(index, CreateComponentPredicate(DayOfWeekTest(new[] { 0, 6 })), descriptor),
            LibraDexConditionOperatorKind.IsWeekday => MaterializeStructuredDateComponentLeaf(index, CreateComponentPredicate(DayOfWeekTest(new[] { 1, 2, 3, 4, 5 })), descriptor),
            LibraDexConditionOperatorKind.IsMorning => MaterializeStructuredTimeOfDayLeaf(index, 5, 11, descriptor),
            LibraDexConditionOperatorKind.IsAfternoon => MaterializeStructuredTimeOfDayLeaf(index, 12, 16, descriptor),
            LibraDexConditionOperatorKind.IsEvening => MaterializeStructuredTimeOfDayLeaf(index, 17, 21, descriptor),
            LibraDexConditionOperatorKind.IsNight => MaterializeStructuredNightLeaf(index, descriptor),
            _ => null
        };

        return criterion is not null;
    }

    /// <summary>
    /// Creates a zero-operand internal condition leaf without allocating a caller-side `params` array.<br/>
    /// </summary>
    /// <param name="index">The resolved logical index that owns the leaf.</param>
    /// <param name="criteriaKind">The internal primitive request kind.</param>
    /// <returns>An identity criterion leaf for the condition executor.</returns>
    private static IIdentityCriterion CreateConditionLeaf(IIndex index, LibraDexCriteriaKind criteriaKind)
        => LibraDexIdentityCriterion.Leaf(index, criteriaKind, CreateConditionDiagnostics(criteriaKind), Array.Empty<object?>());

    /// <summary>
    /// Creates a one-operand internal condition leaf without routing through the many-value validation path.<br/>
    /// </summary>
    /// <param name="index">The resolved logical index that owns the leaf.</param>
    /// <param name="criteriaKind">The internal primitive request kind.</param>
    /// <param name="value">The primitive operand value.</param>
    /// <returns>An identity criterion leaf for the condition executor.</returns>
    private static IIdentityCriterion CreateConditionLeaf(IIndex index, LibraDexCriteriaKind criteriaKind, object? value)
    {
        return LibraDexIdentityCriterion.Leaf(
            index,
            criteriaKind,
            CreateConditionDiagnostics(criteriaKind),
            ValidateConditionLeafValue(index, criteriaKind, value));
    }

    /// <summary>
    /// Creates a two-operand internal condition leaf for range-style conditions without LINQ allocation.<br/>
    /// </summary>
    /// <param name="index">The resolved logical index that owns the leaf.</param>
    /// <param name="criteriaKind">The internal primitive request kind.</param>
    /// <param name="first">The first primitive operand value.</param>
    /// <param name="second">The second primitive operand value.</param>
    /// <returns>An identity criterion leaf for the condition executor.</returns>
    private static IIdentityCriterion CreateConditionLeaf(IIndex index, LibraDexCriteriaKind criteriaKind, object? first, object? second)
    {
        return LibraDexIdentityCriterion.Leaf(
            index,
            criteriaKind,
            CreateConditionDiagnostics(criteriaKind),
            ValidateConditionLeafValue(index, criteriaKind, first),
            ValidateConditionLeafValue(index, criteriaKind, second));
    }

    private static IIdentityCriterion CreateConditionLeaf(IIndex index, LibraDexCriteriaKind criteriaKind, params object?[] values)
    {
        object?[] validatedValues = ValidateConditionLeafValues(index, criteriaKind, values);
        return LibraDexIdentityCriterion.Leaf(index, criteriaKind, CreateConditionDiagnostics(criteriaKind), validatedValues);
    }

    /// <summary>
    /// Creates a zero-operand projection-backed condition leaf without allocating a caller-side `params` array.<br/>
    /// </summary>
    /// <param name="group">The logical identity group for the condition.</param>
    /// <param name="index">The physical projection index that owns the primitive route.</param>
    /// <param name="criteriaKind">The internal primitive request kind.</param>
    /// <returns>An identity criterion leaf for the condition executor.</returns>
    private static IIdentityCriterion CreateProjectionConditionLeaf(string group, IIndex index, LibraDexCriteriaKind criteriaKind)
        => LibraDexIdentityCriterion.Leaf(group, index, criteriaKind, CreateConditionDiagnostics(criteriaKind), Array.Empty<object?>());

    /// <summary>
    /// Creates a one-operand projection-backed condition leaf without routing through the many-value validation path.<br/>
    /// </summary>
    /// <param name="group">The logical identity group for the condition.</param>
    /// <param name="index">The physical projection index that owns the primitive route.</param>
    /// <param name="criteriaKind">The internal primitive request kind.</param>
    /// <param name="value">The primitive operand value.</param>
    /// <returns>An identity criterion leaf for the condition executor.</returns>
    private static IIdentityCriterion CreateProjectionConditionLeaf(string group, IIndex index, LibraDexCriteriaKind criteriaKind, object? value)
    {
        return LibraDexIdentityCriterion.Leaf(
            group,
            index,
            criteriaKind,
            CreateConditionDiagnostics(criteriaKind),
            ValidateConditionLeafValue(index, criteriaKind, value));
    }

    /// <summary>
    /// Creates a two-operand projection-backed condition leaf for range-style projection conditions without LINQ allocation.<br/>
    /// </summary>
    /// <param name="group">The logical identity group for the condition.</param>
    /// <param name="index">The physical projection index that owns the primitive route.</param>
    /// <param name="criteriaKind">The internal primitive request kind.</param>
    /// <param name="first">The first primitive operand value.</param>
    /// <param name="second">The second primitive operand value.</param>
    /// <returns>An identity criterion leaf for the condition executor.</returns>
    private static IIdentityCriterion CreateProjectionConditionLeaf(string group, IIndex index, LibraDexCriteriaKind criteriaKind, object? first, object? second)
    {
        return LibraDexIdentityCriterion.Leaf(
            group,
            index,
            criteriaKind,
            CreateConditionDiagnostics(criteriaKind),
            ValidateConditionLeafValue(index, criteriaKind, first),
            ValidateConditionLeafValue(index, criteriaKind, second));
    }

    private static IIdentityCriterion CreateProjectionConditionLeaf(string group, IIndex index, LibraDexCriteriaKind criteriaKind, params object?[] values)
    {
        object?[] validatedValues = ValidateConditionLeafValues(index, criteriaKind, values);
        return LibraDexIdentityCriterion.Leaf(group, index, criteriaKind, CreateConditionDiagnostics(criteriaKind), validatedValues);
    }

    /// <summary>
    /// Validates many primitive leaf operands using an explicit loop instead of LINQ projection.<br/>
    /// </summary>
    /// <param name="index">The resolved index whose key contract validates the values.</param>
    /// <param name="criteriaKind">The internal primitive request kind.</param>
    /// <param name="values">The primitive operand values to validate.</param>
    /// <returns>The validated primitive operand values.</returns>
    private static object?[] ValidateConditionLeafValues(IIndex index, LibraDexCriteriaKind criteriaKind, object?[] values)
    {
        if (values.Length == 0)
        {
            return Array.Empty<object?>();
        }

        object?[] validatedValues = new object?[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            validatedValues[i] = ValidateConditionLeafValue(index, criteriaKind, values[i]);
        }

        return validatedValues;
    }

    /// <summary>
    /// Creates an ordered-key exclusion for one exact value without falling back to a complement over the full identity universe.<br/>
    /// The bridge uses the two ordered extents that can contain valid matches: keys before the excluded value and keys after the excluded value.<br/>
    /// This keeps `NotEqualTo` tied to range-reader primitives and exposes the physical need as ordered extent union rather than hidden post-filtering.<br/>
    /// </summary>
    /// <param name="index">The resolved logical index that owns the leaf.</param>
    /// <param name="value">The exact key value to exclude.</param>
    /// <returns>An identity criterion union over the lower and upper key extents.</returns>
    private static IIdentityCriterion CreateOrderedPointExclusionLeaf(IIndex index, object? value)
    {
        return CreateConditionLeaf(index, LibraDexCriteriaKind.Before, value)
            .Or(CreateConditionLeaf(index, LibraDexCriteriaKind.After, value));
    }

    /// <summary>
    /// Creates an ordered-key exclusion for one inclusive range without falling back to a complement over the full identity universe.<br/>
    /// The bridge maps `not between lower and upper` to the union of keys before the lower boundary and keys after the upper boundary.<br/>
    /// This is the efficient extent shape forced by the condition permutation and keeps range exclusion independent from old direct retrieval facades.<br/>
    /// </summary>
    /// <param name="index">The resolved logical index that owns the leaf.</param>
    /// <param name="lower">The inclusive lower key boundary to exclude.</param>
    /// <param name="upper">The inclusive upper key boundary to exclude.</param>
    /// <returns>An identity criterion union over the lower and upper key extents.</returns>
    private static IIdentityCriterion CreateOrderedRangeExclusionLeaf(IIndex index, object? lower, object? upper)
    {
        return CreateConditionLeaf(index, LibraDexCriteriaKind.Before, lower)
            .Or(CreateConditionLeaf(index, LibraDexCriteriaKind.After, upper));
    }

    /// <summary>
    /// Creates one internal condition retrieval leaf for a same-index multi-range request.<br/>
    /// This is the first primitive shape derived from condition permutations rather than inherited from the old direct criteria surface.<br/>
    /// </summary>
    /// <param name="index">The resolved logical index that owns the ranges.</param>
    /// <param name="ranges">The inclusive key ranges to execute in order.</param>
    /// <returns>An identity criterion leaf for condition-driven multi-range execution.</returns>
    private static IIdentityCriterion CreateConditionMultiRangeLeaf(IIndex index, IEnumerable<LibraDexIdentityKeyRange> ranges)
    {
        LibraDexIdentityKeyRange[] captured = ranges.ToArray();
        if (captured.Length == 0)
        {
            throw new InvalidOperationException("Condition multi-range retrieval requires at least one key range.");
        }

        for (int i = 0; i < captured.Length; i++)
        {
            _ = ValidateConditionLeafValue(index, LibraDexCriteriaKind.Between, captured[i].LowerKey);
            _ = ValidateConditionLeafValue(index, LibraDexCriteriaKind.Between, captured[i].UpperKey);
        }

        return LibraDexIdentityCriterion.Leaf(index, LibraDexCriteriaKind.MultiRange, CreateConditionDiagnostics(LibraDexCriteriaKind.MultiRange), captured);
    }

    /// <summary>
    /// Creates one internal condition retrieval leaf for membership without discarding the caller's managed set object.<br/>
    /// The method validates every member against the resolved key type, then passes the original enumerable as a single primitive operand so the executor can consume sets, prepared sets, or arrays without another descriptor-layer copy.<br/>
    /// </summary>
    /// <param name="index">The resolved logical index that owns the membership leaf.</param>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion leaf for membership execution.</returns>
    private static IIdentityCriterion CreateMembershipLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor)
    {
        object source = RequireValue(values, 0, descriptor);
        if (source is LibraDexPreparedObjectSet prepared)
        {
            if (prepared.KeyType != index.KeyType)
            {
                throw new ArgumentException($"Prepared membership key type {prepared.KeyType.FullName} does not match index key type {index.KeyType.FullName}.");
            }

            return LibraDexIdentityCriterion.Leaf(index, LibraDexCriteriaKind.InSet, CreateConditionDiagnostics(LibraDexCriteriaKind.InSet), prepared);
        }

        IEnumerable enumerable = source as IEnumerable
            ?? throw new InvalidOperationException($"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires an enumerable membership operand.");
        if (source is string)
        {
            throw new InvalidOperationException($"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires an enumerable membership operand.");
        }

        foreach (object? item in enumerable)
        {
            if (TryGetNullKeyState(new[] { item }, 0, out _) &&
                descriptor.ValueKind is LibraDexConditionValueKind.String or LibraDexConditionValueKind.Binary)
            {
                continue;
            }

            _ = ValidateConditionLeafValue(index, LibraDexCriteriaKind.Find, RequireNonNullMembershipValue(item, descriptor));
        }

        return LibraDexIdentityCriterion.Leaf(index, LibraDexCriteriaKind.InSet, CreateConditionDiagnostics(LibraDexCriteriaKind.InSet), source);
    }

    /// <summary>
    /// Materializes a numeric bitmask condition as an explicit compact-index scan predicate.<br/>
    /// The predicate normalizes the mask and comparison value once against the resolved key type, then the primitive executor applies `(key &amp; mask)` to decoded scalar keys.<br/>
    /// </summary>
    /// <param name="index">The resolved logical scalar index selected by the condition.</param>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <param name="mode">Whether the masked result must equal or not equal the comparison operand.</param>
    /// <returns>An identity criterion leaf for bitmask execution.</returns>
    private static IIdentityCriterion MaterializeBitmaskLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor,
        LibraDexBitmaskComparisonMode mode)
    {
        if (descriptor.ValueKind != LibraDexConditionValueKind.Numeric)
        {
            throw new NotSupportedException($"Condition operator {descriptor.Operator} can only materialize against numeric scalar indexes.");
        }

        object mask = RequireValue(values, 0, descriptor);
        object compareValue = RequireValue(values, 1, descriptor);
        return CreateConditionLeaf(index, LibraDexCriteriaKind.Bitmask, LibraDexBitmaskPredicate.Create(index.KeyType, mask, compareValue, mode));
    }

    /// <summary>
    /// Creates one projection-backed membership leaf after validating transformed projection keys individually.<br/>
    /// Projection membership receives a generated enumerable such as `byte[][]` or `string[]`; validating the collection itself would confuse the enumerable container with one key, so this helper validates each member while preserving the generated collection as the primitive operand.<br/>
    /// </summary>
    /// <param name="index">The resolved projection index that owns the membership leaf.</param>
    /// <param name="source">The transformed projection-key enumerable.</param>
    /// <returns>An identity criterion leaf for projection membership execution.</returns>
    private static IIdentityCriterion CreateProjectionMembershipLeaf(IIndex index, object source)
    {
        IEnumerable enumerable = source as IEnumerable
            ?? throw new InvalidOperationException("Projection membership requires an enumerable projection-key operand.");
        if (source is string)
        {
            throw new InvalidOperationException("Projection membership requires an enumerable projection-key operand.");
        }

        foreach (object? item in enumerable)
        {
            _ = ValidateConditionLeafValue(index, LibraDexCriteriaKind.Find, item ?? throw new InvalidOperationException("Projection membership does not allow null keys."));
        }

        return LibraDexIdentityCriterion.Leaf(index, LibraDexCriteriaKind.InSet, CreateConditionDiagnostics(LibraDexCriteriaKind.InSet), source);
    }

    /// <summary>
    /// Creates diagnostics for an internal condition retrieval leaf.<br/>
    /// The diagnostics describe the current execution bridge only; they are not public query grammar and should not drive future condition-builder terminology.<br/>
    /// </summary>
    /// <param name="criteriaKind">The internal primitive request kind.</param>
    /// <returns>The diagnostics attached to the generated leaf.</returns>
    private static LibraDexQueryDiagnostics CreateConditionDiagnostics(LibraDexCriteriaKind criteriaKind)
    {
        LibraDexExecutionKind executionKind = criteriaKind switch
        {
            LibraDexCriteriaKind.In or
            LibraDexCriteriaKind.InSet or
            LibraDexCriteriaKind.MultiRange => LibraDexExecutionKind.Projection,
            LibraDexCriteriaKind.StructuredComponent => LibraDexExecutionKind.Scan,
            LibraDexCriteriaKind.GuidPattern => LibraDexExecutionKind.Scan,
            LibraDexCriteriaKind.BinaryPattern => LibraDexExecutionKind.Scan,
            LibraDexCriteriaKind.BinaryTypedSlice => LibraDexExecutionKind.Scan,
            LibraDexCriteriaKind.StringPattern => LibraDexExecutionKind.Scan,
            LibraDexCriteriaKind.Bitmask => LibraDexExecutionKind.Scan,
            LibraDexCriteriaKind.CompositeMatch => LibraDexExecutionKind.FastPath,
            _ => LibraDexExecutionKind.FastPath
        };

        return new LibraDexQueryDiagnostics(executionKind);
    }

    /// <summary>
    /// Validates one internal condition retrieval operand against the resolved index key type.<br/>
    /// Membership operands are expanded before this helper is called, so every non-null value should be a concrete key value except opaque pattern descriptors for future projection-backed pattern branches.<br/>
    /// </summary>
    /// <param name="index">The resolved logical index that owns the leaf.</param>
    /// <param name="criteriaKind">The internal primitive request kind.</param>
    /// <param name="value">The operand value to validate.</param>
    /// <returns>The validated value.</returns>
    private static object? ValidateConditionLeafValue(IIndex index, LibraDexCriteriaKind criteriaKind, object? value)
    {
        if (value is null || criteriaKind == LibraDexCriteriaKind.All)
        {
            return value;
        }

        if (criteriaKind == LibraDexCriteriaKind.StructuredComponent)
        {
            return value is LibraDexStructuredComponentPredicate
                ? value
                : throw new ArgumentException("Structured component conditions require a compiled structured component predicate.");
        }

        if (criteriaKind == LibraDexCriteriaKind.GuidPattern)
        {
            return value is LibraDexGuidPatternPredicate
                ? value
                : throw new ArgumentException("GUID pattern conditions require a compiled GUID pattern predicate.");
        }

        if (criteriaKind == LibraDexCriteriaKind.BinaryPattern)
        {
            return value is LibraDexBinaryPatternPredicate
                ? value
                : throw new ArgumentException("Binary pattern conditions require a compiled binary pattern predicate.");
        }

        if (criteriaKind == LibraDexCriteriaKind.BinaryTypedSlice)
        {
            return value is LibraDexBinaryTypedSlicePredicate
                ? value
                : throw new ArgumentException("Binary typed-slice conditions require a compiled binary typed-slice predicate.");
        }

        if (criteriaKind == LibraDexCriteriaKind.StringPattern)
        {
            return value is LibraDexStringPatternPredicate
                ? value
                : throw new ArgumentException("String pattern conditions require a compiled string pattern predicate.");
        }

        if (criteriaKind == LibraDexCriteriaKind.Bitmask)
        {
            return value is LibraDexBitmaskPredicate
                ? value
                : throw new ArgumentException("Bitmask conditions require a compiled bitmask predicate.");
        }

        if (criteriaKind == LibraDexCriteriaKind.ScalarNull)
        {
            return value is ScalarNull
                ? value
                : throw new ArgumentException("Scalar null conditions require a ScalarNull operand.");
        }

        if (criteriaKind == LibraDexCriteriaKind.KeyState)
        {
            return value is NullKey
                ? value
                : throw new ArgumentException("Key-state conditions require a NullKey operand.");
        }

        if (criteriaKind == LibraDexCriteriaKind.CompositeMatch)
        {
            return value is LibraDexCompositePredicate
                ? value
                : throw new ArgumentException("Composite match conditions require a compiled composite predicate.");
        }

        if (!index.KeyType.IsInstanceOfType(value))
        {
            throw new ArgumentException($"Runtime key type {value.GetType().FullName} does not match index key type {index.KeyType.FullName}.");
        }

        return value;
    }

    /// <summary>
    /// Determines whether the resolved key type preserves the component required by a structured component materializer.<br/>
    /// This duplicate of the classifier-side support check keeps materialization defensive when descriptors are generated without first calling classification.<br/>
    /// </summary>
    /// <param name="keyType">The resolved index key type.</param>
    /// <param name="operatorKind">The condition operator to inspect.</param>
    /// <returns><see langword="true"/> when the key type can execute the operator without projection lookup.</returns>
    private static bool SupportsStructuredComponentOperator(Type keyType, LibraDexConditionOperatorKind operatorKind)
    {
        if (keyType == typeof(TimeOnly))
        {
            return operatorKind is
                LibraDexConditionOperatorKind.IsMorning or
                LibraDexConditionOperatorKind.IsAfternoon or
                LibraDexConditionOperatorKind.IsEvening or
                LibraDexConditionOperatorKind.IsNight;
        }

        bool isDateLike = keyType == typeof(DateTime) ||
            keyType == typeof(DateTimeOffset) ||
            keyType == typeof(DateOnly);
        if (!isDateLike)
        {
            return false;
        }

        bool requiresHour = operatorKind is
            LibraDexConditionOperatorKind.IsMorning or
            LibraDexConditionOperatorKind.IsAfternoon or
            LibraDexConditionOperatorKind.IsEvening or
            LibraDexConditionOperatorKind.IsNight;
        return !requiresHour || keyType != typeof(DateOnly);
    }

    /// <summary>
    /// Materializes a GUID partial/pattern branch as an encoded canonical-nibble predicate.<br/>
    /// The predicate reads the stored 16-byte GUID key directly and applies the Abraxas-compatible comparison mode without converting every candidate to text.<br/>
    /// </summary>
    /// <param name="index">The logical GUID index selected by the condition.</param>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion leaf for GUID pattern execution.</returns>
    private static IIdentityCriterion MaterializeGuidPatternLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor)
    {
        if (index.KeyType != typeof(Guid))
        {
            throw new NotSupportedException($"Condition operator {descriptor.Operator} cannot materialize as a GUID pattern predicate against index key type {index.KeyType.FullName}.");
        }

        object operand = RequireValue(values, 0, descriptor);
        LibraDexGuidPatternMode mode = descriptor.Operator switch
        {
            LibraDexConditionOperatorKind.StartsWith => LibraDexGuidPatternMode.StartsWith,
            LibraDexConditionOperatorKind.EndsWith => LibraDexGuidPatternMode.EndsWith,
            LibraDexConditionOperatorKind.Contains => LibraDexGuidPatternMode.Contains,
            LibraDexConditionOperatorKind.MatchesPattern => LibraDexGuidPatternMode.MatchesPattern,
            _ => throw new NotSupportedException($"Condition operator {descriptor.Operator} is not a GUID pattern operator.")
        };
        LibraDexGuidPatternPredicate predicate = operand switch
        {
            LibraDexGuidPatternPredicate compiled => compiled,
            string pattern => LibraDexGuidPatternPredicate.Create(pattern, mode),
            byte[] bytes => LibraDexGuidPatternPredicate.Create(bytes, mode),
            _ => throw new InvalidOperationException($"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires a GUID pattern operand.")
        };
        return CreateConditionLeaf(index, LibraDexCriteriaKind.GuidPattern, predicate);
    }

    /// <summary>
    /// Materializes a raw binary byte-slice branch as an encoded key-byte predicate.<br/>
    /// The predicate reads fixed-width byte-array key bytes directly and applies the Abraxas-style slice intent without decoding every candidate key to a caller-facing array.<br/>
    /// </summary>
    /// <param name="index">The logical binary index selected by the condition.</param>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion leaf for binary byte-pattern execution.</returns>
    private static IIdentityCriterion MaterializeBinaryPatternLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor)
    {
        if (index.KeyType != typeof(byte[]))
        {
            throw new NotSupportedException($"Condition operator {descriptor.Operator} cannot materialize as a binary byte predicate against index key type {index.KeyType.FullName}.");
        }

        object operand = RequireValue(values, descriptor.Operator == LibraDexConditionOperatorKind.BinarySliceEqual && values.Length > 1 ? 1 : 0, descriptor);
        LibraDexBinaryPatternMode mode = descriptor.Operator switch
        {
            LibraDexConditionOperatorKind.StartsWith => LibraDexBinaryPatternMode.StartsWith,
            LibraDexConditionOperatorKind.EndsWith => LibraDexBinaryPatternMode.EndsWith,
            LibraDexConditionOperatorKind.Contains => LibraDexBinaryPatternMode.Contains,
            LibraDexConditionOperatorKind.BinarySliceEqual => LibraDexBinaryPatternMode.SliceEqual,
            LibraDexConditionOperatorKind.MatchesPattern => LibraDexBinaryPatternMode.MatchesPattern,
            _ => throw new NotSupportedException($"Condition operator {descriptor.Operator} is not a binary pattern operator.")
        };
        if (operand is LibraDexBinaryPatternPredicate compiled)
        {
            return CreateConditionLeaf(index, LibraDexCriteriaKind.BinaryPattern, compiled);
        }

        byte[] pattern = operand as byte[]
            ?? throw new InvalidOperationException($"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires a binary byte pattern operand.");
        int offset = descriptor.Operator == LibraDexConditionOperatorKind.BinarySliceEqual && values.Length > 1
            ? RequireInt32(values, 0, descriptor)
            : 0;
        return CreateConditionLeaf(index, LibraDexCriteriaKind.BinaryPattern, LibraDexBinaryPatternPredicate.Create(mode, pattern, offset));
    }

    /// <summary>
    /// Materializes a string pattern branch as an exact-index candidate scan with residual text comparison.<br/>
    /// Maintained projections still win when available; this fallback honors developer intent by scanning compact indexed keys rather than source records.<br/>
    /// </summary>
    /// <param name="index">The logical string index selected by the condition.</param>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion leaf for string-pattern execution.</returns>
    private static IIdentityCriterion MaterializeStringPatternLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor)
    {
        if (index.KeyType != typeof(string))
        {
            throw new NotSupportedException($"Condition operator {descriptor.Operator} cannot materialize as a string predicate against index key type {index.KeyType.FullName}.");
        }

        LibraDexStringPatternMode mode = descriptor.Operator switch
        {
            LibraDexConditionOperatorKind.StartsWith => LibraDexStringPatternMode.StartsWith,
            LibraDexConditionOperatorKind.EndsWith => LibraDexStringPatternMode.EndsWith,
            LibraDexConditionOperatorKind.Contains => LibraDexStringPatternMode.Contains,
            LibraDexConditionOperatorKind.MatchesPattern => LibraDexStringPatternMode.MatchesPattern,
            LibraDexConditionOperatorKind.NotMatchesPattern => LibraDexStringPatternMode.NotMatchesPattern,
            LibraDexConditionOperatorKind.RegexMatches => LibraDexStringPatternMode.RegexMatches,
            LibraDexConditionOperatorKind.NotRegexMatches => LibraDexStringPatternMode.NotRegexMatches,
            LibraDexConditionOperatorKind.MatchesWith => LibraDexStringPatternMode.MatchesWith,
            LibraDexConditionOperatorKind.NotMatchesWith => LibraDexStringPatternMode.NotMatchesWith,
            LibraDexConditionOperatorKind.MatchesInSet => LibraDexStringPatternMode.MatchesInSet,
            LibraDexConditionOperatorKind.NotMatchesInSet => LibraDexStringPatternMode.NotMatchesInSet,
            _ => throw new NotSupportedException($"Condition operator {descriptor.Operator} is not a string pattern operator.")
        };
        if (descriptor.Operator is LibraDexConditionOperatorKind.RegexMatches or LibraDexConditionOperatorKind.NotRegexMatches)
        {
            object pattern = RequireRegexPattern(values, 0, descriptor);
            LibraDexStringComparisonPolicy policy = ResolveStringComparisonPolicy(descriptor, index);
            return CreateConditionLeaf(
                index,
                LibraDexCriteriaKind.StringPattern,
                pattern is Regex regex
                    ? LibraDexStringPatternPredicate.CreateRegex(mode, regex, policy)
                    : LibraDexStringPatternPredicate.CreateRegex(mode, (string)pattern, policy));
        }

        if (descriptor.Operator is LibraDexConditionOperatorKind.MatchesWith or LibraDexConditionOperatorKind.NotMatchesWith)
        {
            object pattern = RequireRegexPattern(values, 0, descriptor);
            LibraDexStringComparisonPolicy policy = ResolveStringComparisonPolicy(descriptor, index);
            return CreateConditionLeaf(
                index,
                LibraDexCriteriaKind.StringPattern,
                pattern is Regex regex
                    ? LibraDexStringPatternPredicate.CreateRegexCapture(
                        mode,
                        regex,
                        RequireNonNullString(values, 1, descriptor),
                        TryRequireRegexGroupNumber(values, 2, descriptor),
                        policy)
                    : LibraDexStringPatternPredicate.CreateRegexCapture(
                        mode,
                        (string)pattern,
                        RequireNonNullString(values, 1, descriptor),
                        TryRequireRegexGroupNumber(values, 2, descriptor),
                        policy));
        }

        if (descriptor.Operator is LibraDexConditionOperatorKind.MatchesInSet or LibraDexConditionOperatorKind.NotMatchesInSet)
        {
            object pattern = RequireRegexPattern(values, 0, descriptor);
            LibraDexStringComparisonPolicy policy = ResolveStringComparisonPolicy(descriptor, index);
            return CreateConditionLeaf(
                index,
                LibraDexCriteriaKind.StringPattern,
                pattern is Regex regex
                    ? LibraDexStringPatternPredicate.CreateRegexCaptureSet(
                        mode,
                        regex,
                        RequireNonNullStringEnumerable(values, 1, descriptor),
                        TryRequireRegexGroupNumber(values, 2, descriptor),
                        policy)
                    : LibraDexStringPatternPredicate.CreateRegexCaptureSet(
                        mode,
                        (string)pattern,
                        RequireNonNullStringEnumerable(values, 1, descriptor),
                        TryRequireRegexGroupNumber(values, 2, descriptor),
                        policy));
        }

        return CreateConditionLeaf(
            index,
            LibraDexCriteriaKind.StringPattern,
                LibraDexStringPatternPredicate.Create(mode, RequireNonNullString(values, 0, descriptor), ResolveStringComparisonPolicy(descriptor, index)));
    }

    /// <summary>
    /// Materializes a case-sensitive string prefix condition as a direct ordered range over the exact string index.<br/>
    /// This keeps exact `StartsWith` on the cheapest encoded-key path instead of using the residual `StringPattern` fallback that exists for scan-backed text conditions.<br/>
    /// </summary>
    /// <param name="index">The logical string index selected by the condition.</param>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion leaf over the exact string key range.</returns>
    private static IIdentityCriterion MaterializeExactStringPrefixLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor)
    {
        if (index.KeyType != typeof(string))
        {
            throw new NotSupportedException($"Condition operator {descriptor.Operator} cannot materialize as an exact string prefix range against index key type {index.KeyType.FullName}.");
        }

        string value = RequireNonNullString(values, 0, descriptor);
        return CreateConditionLeaf(index, LibraDexCriteriaKind.Prefix, value);
    }

    /// <summary>
    /// Determines whether an adopted string operator needs comparison semantics instead of text-pattern semantics.<br/>
    /// The check is used only after maintained projection routing has had first refusal, so a positive answer means the exact string index must be scanned or bounded and residual-compared.<br/>
    /// </summary>
    /// <param name="operatorKind">The adopted condition operator.</param>
    /// <returns><see langword="true"/> when the operator compares whole string values.</returns>
    private static bool IsStringComparisonOperator(LibraDexConditionOperatorKind operatorKind)
    {
        return operatorKind is
            LibraDexConditionOperatorKind.EqualTo or
            LibraDexConditionOperatorKind.NotEqualTo or
            LibraDexConditionOperatorKind.GreaterThan or
            LibraDexConditionOperatorKind.GreaterOrEqual or
            LibraDexConditionOperatorKind.LessThan or
            LibraDexConditionOperatorKind.LessOrEqual or
            LibraDexConditionOperatorKind.Between or
            LibraDexConditionOperatorKind.NotBetween;
    }

    /// <summary>
    /// Determines whether an adopted string operator evaluates membership over a value set.<br/>
    /// The check is used after projection routing, so it identifies the exact-index residual fallback for no-case membership conditions.<br/>
    /// </summary>
    /// <param name="operatorKind">The adopted condition operator.</param>
    /// <returns><see langword="true"/> when the operator is a membership operator.</returns>
    private static bool IsStringMembershipOperator(LibraDexConditionOperatorKind operatorKind)
        => operatorKind is LibraDexConditionOperatorKind.InSet or LibraDexConditionOperatorKind.NotInSet;

    private static bool RequiresManagedStringComparison(LibraDexConditionLeafDescriptor descriptor, IIndex index)
    {
        if (descriptor.IgnoreCase || !string.IsNullOrEmpty(descriptor.Culture) || descriptor.StringComparisonPolicy is not null)
        {
            return true;
        }

        return index is ILibraDexStringComparisonPolicyProvider provider &&
            provider.StringComparisonPolicy is { Kind: not LibraDexStringComparisonPolicyKind.Invariant };
    }

    /// <summary>
    /// Materializes a case-insensitive string comparison as an exact-index residual predicate when no maintained sort-key projection is available.<br/>
    /// Equality can still create bounded exact-key candidate ranges from simple case variants; ordered comparisons intentionally scan compact indexed keys and apply .NET culture-aware comparison to preserve correctness.<br/>
    /// </summary>
    /// <param name="index">The logical string index selected by the condition.</param>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion leaf for scan-backed string comparison execution.</returns>
    private static IIdentityCriterion MaterializeStringComparisonLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor)
    {
        if (index.KeyType != typeof(string))
        {
            throw new NotSupportedException($"Condition operator {descriptor.Operator} cannot materialize as a string comparison predicate against index key type {index.KeyType.FullName}.");
        }

        LibraDexStringPatternMode mode = descriptor.Operator switch
        {
            LibraDexConditionOperatorKind.EqualTo => LibraDexStringPatternMode.EqualTo,
            LibraDexConditionOperatorKind.NotEqualTo => LibraDexStringPatternMode.NotEqualTo,
            LibraDexConditionOperatorKind.GreaterThan => LibraDexStringPatternMode.GreaterThan,
            LibraDexConditionOperatorKind.GreaterOrEqual => LibraDexStringPatternMode.GreaterOrEqual,
            LibraDexConditionOperatorKind.LessThan => LibraDexStringPatternMode.LessThan,
            LibraDexConditionOperatorKind.LessOrEqual => LibraDexStringPatternMode.LessOrEqual,
            LibraDexConditionOperatorKind.Between => LibraDexStringPatternMode.Between,
            LibraDexConditionOperatorKind.NotBetween => LibraDexStringPatternMode.NotBetween,
            _ => throw new NotSupportedException($"Condition operator {descriptor.Operator} is not a string comparison operator.")
        };
        string lower = RequireNonNullString(values, 0, descriptor);
        return descriptor.Operator is LibraDexConditionOperatorKind.Between or LibraDexConditionOperatorKind.NotBetween
            ? CreateConditionLeaf(
                index,
                LibraDexCriteriaKind.StringPattern,
                LibraDexStringPatternPredicate.Create(mode, lower, RequireNonNullString(values, 1, descriptor), ResolveStringComparisonPolicy(descriptor, index)))
            : CreateConditionLeaf(
                index,
                LibraDexCriteriaKind.StringPattern,
                LibraDexStringPatternPredicate.Create(mode, lower, ResolveStringComparisonPolicy(descriptor, index)));
    }

    /// <summary>
    /// Materializes case-insensitive string membership as an exact-index residual predicate when no maintained sort-key projection is available.<br/>
    /// `InSet` may still narrow to exact-key case-variant candidates; `NotInSet` scans the compact exact index and keeps identities whose decoded keys do not match the membership set.<br/>
    /// </summary>
    /// <param name="index">The logical string index selected by the condition.</param>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion leaf for scan-backed string membership execution.</returns>
    private static IIdentityCriterion MaterializeStringMembershipLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor)
    {
        if (index.KeyType != typeof(string))
        {
            throw new NotSupportedException($"Condition operator {descriptor.Operator} cannot materialize as a string membership predicate against index key type {index.KeyType.FullName}.");
        }

        LibraDexStringPatternMode mode = descriptor.Operator switch
        {
            LibraDexConditionOperatorKind.InSet => LibraDexStringPatternMode.InSet,
            LibraDexConditionOperatorKind.NotInSet => LibraDexStringPatternMode.NotInSet,
            _ => throw new NotSupportedException($"Condition operator {descriptor.Operator} is not a string membership operator.")
        };
        return CreateConditionLeaf(
            index,
            LibraDexCriteriaKind.StringPattern,
            LibraDexStringPatternPredicate.CreateSet(mode, RequireNonNullStringEnumerable(values, 0, descriptor), ResolveStringComparisonPolicy(descriptor, index)));
    }

    /// <summary>
    /// Materializes one routed composite condition as a single composite-match primitive.<br/>
    /// Keeping all tier predicates in one primitive lets the composite executor traverse mini-router tiers in order rather than intersecting independent part scans after materialization.<br/>
    /// </summary>
    /// <param name="index">The logical composite index selected by the condition.</param>
    /// <param name="values">The materialized composite part predicates.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion leaf for routed composite execution.</returns>
    private static IIdentityCriterion MaterializeCompositeLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor)
    {
        if (index.LogicalShape?.KeyFamily != CatalogIndexKeyFamily.Composite)
        {
            throw new NotSupportedException($"Condition operator {descriptor.Operator} requires a composite index shape.");
        }

        LibraDexCompositePartCriterion[] parts = values
            .Select(value => value as LibraDexCompositePartCriterion ?? throw new ArgumentException("Composite conditions require composite part predicates."))
            .ToArray();
        LibraDexCompositePredicate predicate = new(parts);
        predicate.ValidateAgainst(index.LogicalShape);
        return CreateConditionLeaf(index, LibraDexCriteriaKind.CompositeMatch, predicate);
    }

    /// <summary>
    /// Resolves the managed string comparison policy for a materialized string leaf.<br/>
    /// Method-level policy wins, then the opened index's runtime policy, then legacy ignore-case/culture operands, then the LibraDex default.<br/>
    /// </summary>
    /// <param name="descriptor">The condition leaf descriptor.</param>
    /// <param name="index">The resolved index.</param>
    /// <returns>The resolved managed string comparison policy.</returns>
    private static LibraDexStringComparisonPolicy ResolveStringComparisonPolicy(LibraDexConditionLeafDescriptor descriptor, IIndex index)
    {
        if (descriptor.StringComparisonPolicy is not null)
        {
            return descriptor.StringComparisonPolicy;
        }

        if (descriptor.IgnoreCase || !string.IsNullOrEmpty(descriptor.Culture))
        {
            return LibraDexStringComparisonPolicy.FromLegacy(descriptor.IgnoreCase, descriptor.Culture);
        }

        if (index is ILibraDexStringComparisonPolicyProvider provider &&
            provider.StringComparisonPolicy is not null)
        {
            return provider.StringComparisonPolicy;
        }

        return LibraDexStringComparisonPolicy.Default;
    }

    /// <summary>
    /// Materializes a typed binary-slice branch as an encoded key-byte predicate.<br/>
    /// The predicate interprets only the selected byte slice, using Abraxas-compatible little-endian numeric and date/time layouts where applicable.<br/>
    /// </summary>
    /// <param name="index">The logical binary index selected by the condition.</param>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion leaf for typed binary-slice execution.</returns>
    private static IIdentityCriterion MaterializeBinaryTypedSliceLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor)
    {
        if (index.KeyType != typeof(byte[]))
        {
            throw new NotSupportedException($"Condition operator {descriptor.Operator} cannot materialize as a binary typed-slice predicate against index key type {index.KeyType.FullName}.");
        }

        LibraDexBinarySliceValueKind valueKind = RequireEnum<LibraDexBinarySliceValueKind>(values, 0, descriptor);
        int offset = RequireInt32(values, 1, descriptor);
        int length = RequireInt32(values, 2, descriptor);
        object value = RequireValue(values, 3, descriptor);
        object? upperValue = descriptor.Operator is LibraDexConditionOperatorKind.BinaryTypedSliceBetween or
            LibraDexConditionOperatorKind.BinaryTypedSliceBitAndEqualTo or
            LibraDexConditionOperatorKind.BinaryTypedSliceBitAndNotEqualTo
            ? RequireValue(values, 4, descriptor)
            : null;
        Encoding? encoding = valueKind == LibraDexBinarySliceValueKind.CustomEncodingString
            ? RequireValue(values, descriptor.Operator is LibraDexConditionOperatorKind.BinaryTypedSliceBetween or
                LibraDexConditionOperatorKind.BinaryTypedSliceBitAndEqualTo or
                LibraDexConditionOperatorKind.BinaryTypedSliceBitAndNotEqualTo ? 5 : 4, descriptor) as Encoding
                ?? throw new InvalidOperationException($"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires an Encoding operand for custom encoded binary string slices.")
            : null;
        LibraDexBinarySliceComparisonKind comparisonKind = descriptor.Operator switch
        {
            LibraDexConditionOperatorKind.BinaryTypedSliceEqualTo => LibraDexBinarySliceComparisonKind.EqualTo,
            LibraDexConditionOperatorKind.BinaryTypedSliceGreaterThan => LibraDexBinarySliceComparisonKind.GreaterThan,
            LibraDexConditionOperatorKind.BinaryTypedSliceGreaterOrEqual => LibraDexBinarySliceComparisonKind.GreaterOrEqual,
            LibraDexConditionOperatorKind.BinaryTypedSliceLessThan => LibraDexBinarySliceComparisonKind.LessThan,
            LibraDexConditionOperatorKind.BinaryTypedSliceLessOrEqual => LibraDexBinarySliceComparisonKind.LessOrEqual,
            LibraDexConditionOperatorKind.BinaryTypedSliceBetween => LibraDexBinarySliceComparisonKind.Between,
            LibraDexConditionOperatorKind.BinaryTypedSliceStartsWith => LibraDexBinarySliceComparisonKind.StartsWith,
            LibraDexConditionOperatorKind.BinaryTypedSliceContains => LibraDexBinarySliceComparisonKind.Contains,
            LibraDexConditionOperatorKind.BinaryTypedSliceBitAndEqualTo => LibraDexBinarySliceComparisonKind.BitAndEqualTo,
            LibraDexConditionOperatorKind.BinaryTypedSliceBitAndNotEqualTo => LibraDexBinarySliceComparisonKind.BitAndNotEqualTo,
            _ => throw new NotSupportedException($"Condition operator {descriptor.Operator} is not a binary typed-slice operator.")
        };
        return CreateConditionLeaf(
            index,
            LibraDexCriteriaKind.BinaryTypedSlice,
            LibraDexBinaryTypedSlicePredicate.Create(valueKind, comparisonKind, offset, length, value, upperValue, encoding));
    }

    /// <summary>
    /// Creates one condition leaf for an encoded structured date/time component predicate.<br/>
    /// This bridge is used when a condition is not a contiguous ordered extent but the requested component is directly addressable in the packed scalar key by shift-and-mask.<br/>
    /// </summary>
    /// <param name="index">The logical structured date/time index selected by the condition.</param>
    /// <param name="predicate">The compiled component predicate.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion leaf for structured component execution.</returns>
    private static IIdentityCriterion MaterializeStructuredDateComponentLeaf(
        IIndex index,
        LibraDexStructuredComponentPredicate predicate,
        LibraDexConditionLeafDescriptor descriptor)
    {
        if (!SupportsStructuredComponentOperator(index.KeyType, descriptor.Operator))
        {
            throw new NotSupportedException($"Condition operator {descriptor.Operator} cannot materialize as a structured component predicate against index key type {index.KeyType.FullName}.");
        }

        return CreateConditionLeaf(index, LibraDexCriteriaKind.StructuredComponent, predicate.WithEncoding(index.DateTimeKeyEncoding));
    }

    /// <summary>
    /// Materializes a month/day component tuple across all years as two packed-field tests.<br/>
    /// The resulting primitive reads month and day directly from the structured scalar key and does not generate one range per year.<br/>
    /// </summary>
    /// <param name="index">The logical structured date/time index selected by the condition.</param>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion leaf for the month/day component predicate.</returns>
    private static IIdentityCriterion MaterializeStructuredDateMonthDayComponentLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor)
    {
        (int month, int day) = RequireMonthDay(values, 0, descriptor);
        return MaterializeStructuredDateComponentLeaf(index, CreateComponentPredicate(MonthTest(month), DayTest(day)), descriptor);
    }

    /// <summary>
    /// Creates a structured component predicate from tests that must all match.<br/>
    /// The predicate is a compiled physical payload for the condition bridge rather than a public query grammar object.<br/>
    /// </summary>
    /// <param name="tests">The component tests to apply.</param>
    /// <returns>The compiled structured component predicate.</returns>
    private static LibraDexStructuredComponentPredicate CreateComponentPredicate(params LibraDexStructuredComponentTest[] tests)
        => new LibraDexStructuredComponentPredicate(tests);

    /// <summary>
    /// Creates a structured component predicate from tests plus an optional last-day-of-month requirement.<br/>
    /// The last-day requirement is evaluated from packed year, month, and day fields and avoids CLR date reconstruction.<br/>
    /// </summary>
    /// <param name="requireLastDayOfMonth">Whether the predicate also requires the encoded day to be the last valid day of its month.</param>
    /// <param name="tests">The component tests to apply before the last-day check.</param>
    /// <returns>The compiled structured component predicate.</returns>
    private static LibraDexStructuredComponentPredicate CreateComponentPredicate(bool requireLastDayOfMonth, params LibraDexStructuredComponentTest[] tests)
        => new LibraDexStructuredComponentPredicate(tests, requireLastDayOfMonth);

    /// <summary>
    /// Creates a month component test for one allowed month.<br/>
    /// </summary>
    /// <param name="month">The allowed month component.</param>
    /// <returns>The compiled month component test.</returns>
    private static LibraDexStructuredComponentTest MonthTest(int month)
    {
        ValidateMonth(month, descriptor: null);
        return MonthTest(new[] { month });
    }

    /// <summary>
    /// Creates a month component test for a set of allowed months.<br/>
    /// </summary>
    /// <param name="months">The allowed month components.</param>
    /// <param name="negate">Whether to match months outside the supplied set.</param>
    /// <returns>The compiled month component test.</returns>
    private static LibraDexStructuredComponentTest MonthTest(IEnumerable<int> months, bool negate = false)
        => new LibraDexStructuredComponentTest(LibraDexStructuredDateComponent.Month, BuildComponentBits(months, 1, 12, "month"), negate);

    /// <summary>
    /// Creates a month component test from an inclusive component range.<br/>
    /// </summary>
    /// <param name="range">The inclusive component range.</param>
    /// <param name="negate">Whether to match months outside the supplied range.</param>
    /// <returns>The compiled month component test.</returns>
    private static LibraDexStructuredComponentTest MonthTest((int start, int end) range, bool negate = false)
        => MonthTest(EnumerateInclusive(range.start, range.end), negate);

    /// <summary>
    /// Creates a day-of-month component test for one allowed day.<br/>
    /// </summary>
    /// <param name="day">The allowed day component.</param>
    /// <returns>The compiled day component test.</returns>
    private static LibraDexStructuredComponentTest DayTest(int day)
    {
        ValidateDay(day, descriptor: null);
        return DayTest(new[] { day });
    }

    /// <summary>
    /// Creates a day-of-month component test for a set of allowed days.<br/>
    /// </summary>
    /// <param name="days">The allowed day components.</param>
    /// <param name="negate">Whether to match days outside the supplied set.</param>
    /// <returns>The compiled day component test.</returns>
    private static LibraDexStructuredComponentTest DayTest(IEnumerable<int> days, bool negate = false)
        => new LibraDexStructuredComponentTest(LibraDexStructuredDateComponent.Day, BuildComponentBits(days, 1, 31, "day"), negate);

    /// <summary>
    /// Creates a day-of-month component test from an inclusive component range.<br/>
    /// </summary>
    /// <param name="range">The inclusive component range.</param>
    /// <param name="negate">Whether to match days outside the supplied range.</param>
    /// <returns>The compiled day component test.</returns>
    private static LibraDexStructuredComponentTest DayTest((int start, int end) range, bool negate = false)
        => DayTest(EnumerateInclusive(range.start, range.end), negate);

    /// <summary>
    /// Creates an hour component test from a set of allowed hours.<br/>
    /// </summary>
    /// <param name="hours">The allowed hour components.</param>
    /// <param name="negate">Whether to match hours outside the supplied set.</param>
    /// <returns>The compiled hour component test.</returns>
    private static LibraDexStructuredComponentTest HourTest(IEnumerable<int> hours, bool negate = false)
        => new LibraDexStructuredComponentTest(LibraDexStructuredDateComponent.Hour, BuildComponentBits(hours, 0, 23, "hour"), negate);

    /// <summary>
    /// Creates a day-of-week component test from a set of allowed Abraxas/DateTime day values.<br/>
    /// Sunday is 0 and Saturday is 6, matching <see cref="DayOfWeek"/> and the structured scalar layout.<br/>
    /// </summary>
    /// <param name="days">The allowed day-of-week components.</param>
    /// <returns>The compiled day-of-week component test.</returns>
    private static LibraDexStructuredComponentTest DayOfWeekTest(IEnumerable<int> days)
        => new LibraDexStructuredComponentTest(LibraDexStructuredDateComponent.DayOfWeek, BuildComponentBits(days, 0, 6, "day-of-week"));

    /// <summary>
    /// Builds a compact bit set for small-domain structured date/time component values.<br/>
    /// Each component value becomes one bit position so matching can use a single integer bit test after field extraction.<br/>
    /// </summary>
    /// <param name="values">The component values to allow.</param>
    /// <param name="minimum">The inclusive minimum component value.</param>
    /// <param name="maximum">The inclusive maximum component value.</param>
    /// <param name="name">The component name for diagnostics.</param>
    /// <returns>The compiled allowed-value bit set.</returns>
    private static ulong BuildComponentBits(IEnumerable<int> values, int minimum, int maximum, string name)
    {
        ulong bits = 0;
        foreach (int value in values)
        {
            if (value < minimum || value > maximum)
            {
                throw new ArgumentOutOfRangeException(nameof(values), $"{name} component value {value} is outside {minimum} through {maximum}.");
            }

            bits |= 1UL << value;
        }

        if (bits == 0)
        {
            throw new ArgumentException($"At least one {name} component value is required.", nameof(values));
        }

        return bits;
    }

    /// <summary>
    /// Enumerates an inclusive integer component range after validating the lower/upper ordering.<br/>
    /// This helper keeps component range expansion explicit at bridge materialization time rather than hiding it inside query execution.<br/>
    /// </summary>
    /// <param name="start">The inclusive start value.</param>
    /// <param name="end">The inclusive end value.</param>
    /// <returns>The inclusive value sequence.</returns>
    private static IEnumerable<int> EnumerateInclusive(int start, int end)
    {
        if (end < start)
        {
            throw new ArgumentOutOfRangeException(nameof(end), "Component range end must be greater than or equal to start.");
        }

        for (int value = start; value <= end; value++)
        {
            yield return value;
        }
    }

    /// <summary>
    /// Reads two Int32 operands as an inclusive component range.<br/>
    /// Component range operators store their lower and upper values as adjacent operands.<br/>
    /// </summary>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>The inclusive component range.</returns>
    private static (int start, int end) RequireInt32Range(object?[] values, LibraDexConditionLeafDescriptor descriptor)
    {
        int start = RequireInt32(values, 0, descriptor);
        int end = RequireInt32(values, 1, descriptor);
        if (end < start)
        {
            throw new ArgumentOutOfRangeException(nameof(end), $"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires range end greater than or equal to start.");
        }

        return (start, end);
    }

    /// <summary>
    /// Reads the required month/day tuple payload from a component-only condition leaf.<br/>
    /// The tuple is used for same-day-every-year style predicates that should execute by masking month and day fields, not by generating ranges for every possible year.<br/>
    /// </summary>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="ordinal">The operand ordinal to read.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>The required month/day payload.</returns>
    private static (int month, int day) RequireMonthDay(object?[] values, int ordinal, LibraDexConditionLeafDescriptor descriptor)
    {
        if (values.Length > ordinal + 1 &&
            values[ordinal] is int monthOperand &&
            values[ordinal + 1] is int dayOperand)
        {
            ValidateMonth(monthOperand, descriptor);
            ValidateDay(dayOperand, descriptor);
            return (monthOperand, dayOperand);
        }

        object value = RequireValue(values, ordinal, descriptor);
        if (value is ValueTuple<int, int> tuple)
        {
            ValidateMonth(tuple.Item1, descriptor);
            ValidateDay(tuple.Item2, descriptor);
            return (tuple.Item1, tuple.Item2);
        }

        throw new InvalidOperationException($"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires a month/day tuple operand.");
    }

    /// <summary>
    /// Expands one quarter to its three month components.<br/>
    /// The result feeds the structured component primitive rather than an ordered range because quarter-only predicates repeat every year.<br/>
    /// </summary>
    /// <param name="quarter">The quarter component.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>The month components in the quarter.</returns>
    private static IReadOnlyList<int> MonthsForQuarter(int quarter, LibraDexConditionLeafDescriptor descriptor)
    {
        ValidateQuarter(quarter, descriptor);
        int firstMonth = ((quarter - 1) * 3) + 1;
        return new[] { firstMonth, firstMonth + 1, firstMonth + 2 };
    }

    /// <summary>
    /// Expands an inclusive quarter range to the represented month components.<br/>
    /// This preserves the condition's component-only meaning while exposing the physical mask predicate used by LibraDex.<br/>
    /// </summary>
    /// <param name="startQuarter">The inclusive starting quarter.</param>
    /// <param name="endQuarter">The inclusive ending quarter.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>The month components in the quarter range.</returns>
    private static IReadOnlyList<int> MonthsForQuarterRange(int startQuarter, int endQuarter, LibraDexConditionLeafDescriptor descriptor)
    {
        ValidateQuarter(startQuarter, descriptor);
        ValidateQuarter(endQuarter, descriptor);
        if (endQuarter < startQuarter)
        {
            throw new ArgumentOutOfRangeException(nameof(endQuarter), $"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires ending quarter greater than or equal to starting quarter.");
        }

        List<int> months = new();
        for (int quarter = startQuarter; quarter <= endQuarter; quarter++)
        {
            months.AddRange(MonthsForQuarter(quarter, descriptor));
        }

        return months;
    }

    /// <summary>
    /// Materializes a structured date `YearEqualTo` leaf as an inclusive range over the selected logical index.<br/>
    /// This relies on the Abraxas-compatible structured date codec placing the year in the high key bits, which makes all values for one year contiguous in encoded order.<br/>
    /// Unsupported date-like key types are rejected instead of routed through a scan-shaped fallback.<br/>
    /// </summary>
    /// <param name="index">The logical date index selected by the condition.</param>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion over the structured date year range.</returns>
    private static IIdentityCriterion MaterializeStructuredDateYearLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor)
    {
        int year = RequireInt32(values, 0, descriptor);
        return index.KeyType == typeof(DateTime)
            ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, CreateDateTimeYearLower(year), CreateDateTimeYearUpper(year))
            : index.KeyType == typeof(DateTimeOffset)
                ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, new DateTimeOffset(CreateDateTimeYearLower(year), TimeSpan.Zero), new DateTimeOffset(CreateDateTimeYearUpper(year), TimeSpan.Zero))
                : index.KeyType == typeof(DateOnly)
                    ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, new DateOnly(year, 1, 1), new DateOnly(year, 12, 31))
                    : throw new NotSupportedException($"Condition operator {descriptor.Operator} cannot materialize against index key type {index.KeyType.FullName}.");
    }

    /// <summary>
    /// Materializes a structured date `YearRange` leaf as an inclusive range over the selected logical index.<br/>
    /// This is a single ordered range because the Abraxas-compatible structured date codec stores year before all lower-resolution components.<br/>
    /// </summary>
    /// <param name="index">The logical date index selected by the condition.</param>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion over the structured date year range.</returns>
    private static IIdentityCriterion MaterializeStructuredDateYearRangeLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor)
    {
        int startYear = RequireInt32(values, 0, descriptor);
        int endYear = RequireInt32(values, 1, descriptor);
        if (endYear < startYear)
        {
            throw new ArgumentOutOfRangeException(nameof(endYear), $"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires end year greater than or equal to start year.");
        }

        return index.KeyType == typeof(DateTime)
            ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, CreateDateTimeYearLower(startYear), CreateDateTimeYearUpper(endYear))
            : index.KeyType == typeof(DateTimeOffset)
                ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, new DateTimeOffset(CreateDateTimeYearLower(startYear), TimeSpan.Zero), new DateTimeOffset(CreateDateTimeYearUpper(endYear), TimeSpan.Zero))
                : index.KeyType == typeof(DateOnly)
                    ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, new DateOnly(startYear, 1, 1), new DateOnly(endYear, 12, 31))
                    : throw new NotSupportedException($"Condition operator {descriptor.Operator} cannot materialize against index key type {index.KeyType.FullName}.");
    }

    /// <summary>
    /// Materializes a structured date `YearNotEqualTo` leaf as two ordered extents outside the excluded year.<br/>
    /// Because structured date encoding stores year before lower-resolution parts, all values for the excluded year are contiguous and can be skipped with before/after primitives.<br/>
    /// This avoids the older complement-universe path and keeps negated year equality as explicit ordered range work.<br/>
    /// </summary>
    /// <param name="index">The logical date index selected by the condition.</param>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion union over dates before and after the excluded year.</returns>
    private static IIdentityCriterion MaterializeStructuredDateYearExclusionLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor)
    {
        int year = RequireInt32(values, 0, descriptor);
        LibraDexIdentityKeyRange range = CreateStructuredDateYearKeyRange(index, year, year, descriptor);
        return CreateOrderedRangeExclusionLeaf(index, range.LowerKey, range.UpperKey);
    }

    /// <summary>
    /// Materializes a structured date `YearNotRange` leaf as two ordered extents outside the excluded year span.<br/>
    /// The excluded year span is contiguous in the Abraxas-compatible structured date codec, so the efficient bridge is keys before the lower year and keys after the upper year.<br/>
    /// </summary>
    /// <param name="index">The logical date index selected by the condition.</param>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion union over dates before and after the excluded year span.</returns>
    private static IIdentityCriterion MaterializeStructuredDateYearRangeExclusionLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor)
    {
        int startYear = RequireInt32(values, 0, descriptor);
        int endYear = RequireInt32(values, 1, descriptor);
        LibraDexIdentityKeyRange range = CreateStructuredDateYearKeyRange(index, startYear, endYear, descriptor);
        return CreateOrderedRangeExclusionLeaf(index, range.LowerKey, range.UpperKey);
    }

    /// <summary>
    /// Materializes a structured date `YearIn` leaf as a same-index union of inclusive year ranges.<br/>
    /// Each year is contiguous in the Abraxas-compatible structured date codec, so this branch uses current ordered-range primitives without a component scan.<br/>
    /// </summary>
    /// <param name="index">The logical date index selected by the condition.</param>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion representing the union of all requested year ranges.</returns>
    private static IIdentityCriterion MaterializeStructuredDateYearInLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor)
    {
        IReadOnlyList<int> years = RequireInt32Set(values, 0, descriptor);
        return CreateConditionMultiRangeLeaf(index, years.Select(year => CreateStructuredDateYearKeyRange(index, year, year, descriptor)));
    }

    /// <summary>
    /// Materializes a structured date lower year-boundary leaf as one ordered range.<br/>
    /// The range starts at the first representable tick/day of the supplied year and extends to the maximum value supported by the resolved key type.<br/>
    /// </summary>
    /// <param name="index">The logical date index selected by the condition.</param>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion over the year-on-or-after range.</returns>
    private static IIdentityCriterion MaterializeStructuredDateYearOnOrAfterLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor)
    {
        int year = RequireInt32(values, 0, descriptor);
        return index.KeyType == typeof(DateTime)
            ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, CreateDateTimeYearLower(year), DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc))
            : index.KeyType == typeof(DateTimeOffset)
                ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, new DateTimeOffset(CreateDateTimeYearLower(year), TimeSpan.Zero), new DateTimeOffset(DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc), TimeSpan.Zero))
                : index.KeyType == typeof(DateOnly)
                    ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, new DateOnly(year, 1, 1), DateOnly.MaxValue)
                    : throw new NotSupportedException($"Condition operator {descriptor.Operator} cannot materialize against index key type {index.KeyType.FullName}.");
    }

    /// <summary>
    /// Materializes a structured date upper year-boundary leaf as one ordered range.<br/>
    /// The range starts at the minimum value supported by the resolved key type and ends at the last representable tick/day of the supplied year.<br/>
    /// </summary>
    /// <param name="index">The logical date index selected by the condition.</param>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion over the year-on-or-before range.</returns>
    private static IIdentityCriterion MaterializeStructuredDateYearOnOrBeforeLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor)
    {
        int year = RequireInt32(values, 0, descriptor);
        return index.KeyType == typeof(DateTime)
            ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc), CreateDateTimeYearUpper(year))
            : index.KeyType == typeof(DateTimeOffset)
                ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, new DateTimeOffset(DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc), TimeSpan.Zero), new DateTimeOffset(CreateDateTimeYearUpper(year), TimeSpan.Zero))
                : index.KeyType == typeof(DateOnly)
                    ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, DateOnly.MinValue, new DateOnly(year, 12, 31))
                    : throw new NotSupportedException($"Condition operator {descriptor.Operator} cannot materialize against index key type {index.KeyType.FullName}.");
    }

    /// <summary>
    /// Materializes one structured date year span as an inclusive range over the selected logical index.<br/>
    /// The helper is shared by scalar, set-valued, and negated year operators so all paths preserve identical DateTimeOffset UTC normalization.<br/>
    /// </summary>
    /// <param name="index">The logical date index selected by the condition.</param>
    /// <param name="startYear">The inclusive lower year component.</param>
    /// <param name="endYear">The inclusive upper year component.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion over the requested structured date year span.</returns>
    private static IIdentityCriterion MaterializeStructuredDateYearRange(
        IIndex index,
        int startYear,
        int endYear,
        LibraDexConditionLeafDescriptor descriptor)
    {
        if (endYear < startYear)
        {
            throw new ArgumentOutOfRangeException(nameof(endYear), $"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires end year greater than or equal to start year.");
        }

        return index.KeyType == typeof(DateTime)
            ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, CreateDateTimeYearLower(startYear), CreateDateTimeYearUpper(endYear))
            : index.KeyType == typeof(DateTimeOffset)
                ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, new DateTimeOffset(CreateDateTimeYearLower(startYear), TimeSpan.Zero), new DateTimeOffset(CreateDateTimeYearUpper(endYear), TimeSpan.Zero))
                : index.KeyType == typeof(DateOnly)
                    ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, new DateOnly(startYear, 1, 1), new DateOnly(endYear, 12, 31))
                    : throw new NotSupportedException($"Condition operator {descriptor.Operator} cannot materialize against index key type {index.KeyType.FullName}.");
    }

    /// <summary>
    /// Creates one inclusive structured-date year range payload for condition-driven multi-range retrieval.<br/>
    /// The bounds use the same DateTimeOffset UTC-normalized values as the single-range materializer so multi-range and single-range date branches stay byte-order equivalent.<br/>
    /// </summary>
    /// <param name="index">The logical date index selected by the condition.</param>
    /// <param name="startYear">The inclusive lower year component.</param>
    /// <param name="endYear">The inclusive upper year component.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>The runtime key range payload.</returns>
    private static LibraDexIdentityKeyRange CreateStructuredDateYearKeyRange(
        IIndex index,
        int startYear,
        int endYear,
        LibraDexConditionLeafDescriptor descriptor)
    {
        if (endYear < startYear)
        {
            throw new ArgumentOutOfRangeException(nameof(endYear), $"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires end year greater than or equal to start year.");
        }

        return index.KeyType == typeof(DateTime)
            ? new LibraDexIdentityKeyRange(CreateDateTimeYearLower(startYear), CreateDateTimeYearUpper(endYear))
            : index.KeyType == typeof(DateTimeOffset)
                ? new LibraDexIdentityKeyRange(new DateTimeOffset(CreateDateTimeYearLower(startYear), TimeSpan.Zero), new DateTimeOffset(CreateDateTimeYearUpper(endYear), TimeSpan.Zero))
                : index.KeyType == typeof(DateOnly)
                    ? new LibraDexIdentityKeyRange(new DateOnly(startYear, 1, 1), new DateOnly(endYear, 12, 31))
                    : throw new NotSupportedException($"Condition operator {descriptor.Operator} cannot materialize against index key type {index.KeyType.FullName}.");
    }

    /// <summary>
    /// Materializes a structured date `YearMonth` leaf as an inclusive range over the selected logical index.<br/>
    /// This is a single ordered range because the Abraxas-compatible structured date codec stores year and month before all lower-resolution components.<br/>
    /// </summary>
    /// <param name="index">The logical date index selected by the condition.</param>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion over the structured date year/month range.</returns>
    private static IIdentityCriterion MaterializeStructuredDateYearMonthLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor)
    {
        int year = RequireInt32(values, 0, descriptor);
        int month = RequireInt32(values, 1, descriptor);
        return MaterializeStructuredDateYearMonthRange(index, year, month, descriptor);
    }

    /// <summary>
    /// Materializes a structured date `YearMonthIn` leaf as a same-index union of inclusive ranges.<br/>
    /// This keeps the public condition contract aligned with Abraxas tuple membership while using only current LibraDex ordered-range primitives.<br/>
    /// Empty tuple sets are rejected because they would otherwise hide a likely caller or descriptor-generation error.<br/>
    /// </summary>
    /// <param name="index">The logical date index selected by the condition.</param>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion representing the union of all requested year/month ranges.</returns>
    private static IIdentityCriterion MaterializeStructuredDateYearMonthInLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor)
    {
        IReadOnlyList<(int year, int month)> pairs = RequireYearMonthSet(values, 0, descriptor);
        return CreateConditionMultiRangeLeaf(index, pairs.Select(pair => CreateStructuredDateYearMonthKeyRange(index, pair.year, pair.month, descriptor)));
    }

    /// <summary>
    /// Materializes one structured date year/month pair as an inclusive range over the selected logical index.<br/>
    /// The helper is shared by single-pair and tuple-set operators so both paths validate and encode range bounds identically.<br/>
    /// </summary>
    /// <param name="index">The logical date index selected by the condition.</param>
    /// <param name="year">The year component to match.</param>
    /// <param name="month">The month component to match.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion over the structured date year/month range.</returns>
    private static IIdentityCriterion MaterializeStructuredDateYearMonthRange(
        IIndex index,
        int year,
        int month,
        LibraDexConditionLeafDescriptor descriptor)
    {
        LibraDexIdentityKeyRange range = CreateStructuredDateYearMonthKeyRange(index, year, month, descriptor);
        object lower = range.LowerKey;
        object upper = range.UpperKey;
        return index.KeyType == typeof(DateTime)
            ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, lower, upper)
            : index.KeyType == typeof(DateTimeOffset)
                ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, lower, upper)
                : index.KeyType == typeof(DateOnly)
                    ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, lower, upper)
                    : throw new NotSupportedException($"Condition operator {descriptor.Operator} cannot materialize against index key type {index.KeyType.FullName}.");
    }

    /// <summary>
    /// Creates one inclusive structured-date year/month range payload for condition-driven multi-range retrieval.<br/>
    /// The month component is validated once here so single-range and multi-range branches share identical bounds and diagnostics.<br/>
    /// </summary>
    /// <param name="index">The logical date index selected by the condition.</param>
    /// <param name="year">The year component to match.</param>
    /// <param name="month">The month component to match.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>The runtime key range payload.</returns>
    private static LibraDexIdentityKeyRange CreateStructuredDateYearMonthKeyRange(
        IIndex index,
        int year,
        int month,
        LibraDexConditionLeafDescriptor descriptor)
    {
        ValidateMonth(month, descriptor);
        DateTime lower = CreateDateTimeMonthLower(year, month);
        DateTime upper = CreateDateTimeMonthUpper(year, month);
        return index.KeyType == typeof(DateTime)
            ? new LibraDexIdentityKeyRange(lower, upper)
            : index.KeyType == typeof(DateTimeOffset)
                ? new LibraDexIdentityKeyRange(new DateTimeOffset(lower, TimeSpan.Zero), new DateTimeOffset(upper, TimeSpan.Zero))
                : index.KeyType == typeof(DateOnly)
                    ? new LibraDexIdentityKeyRange(DateOnly.FromDateTime(lower), DateOnly.FromDateTime(upper))
                    : throw new NotSupportedException($"Condition operator {descriptor.Operator} cannot materialize against index key type {index.KeyType.FullName}.");
    }

    /// <summary>
    /// Materializes a structured date `YearMonthDay` leaf as an inclusive range over the selected logical index.<br/>
    /// This is a single ordered range because the Abraxas-compatible structured date codec stores year, month, and day before all time components.<br/>
    /// </summary>
    /// <param name="index">The logical date index selected by the condition.</param>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion over the structured date day range.</returns>
    private static IIdentityCriterion MaterializeStructuredDateYearMonthDayLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor)
    {
        int year = RequireInt32(values, 0, descriptor);
        int month = RequireInt32(values, 1, descriptor);
        int day = RequireInt32(values, 2, descriptor);
        return MaterializeStructuredDateYearMonthDayRange(index, year, month, day, descriptor);
    }

    /// <summary>
    /// Materializes a structured date `YearMonthDayIn` leaf as a same-index union of inclusive ranges.<br/>
    /// Each tuple remains a contiguous date range over the selected index, which avoids text conversion, scans, or companion indexes for these exact tuple permutations.<br/>
    /// Empty tuple sets are rejected because a zero-tuple condition has no useful primitive execution shape in the current contract.<br/>
    /// </summary>
    /// <param name="index">The logical date index selected by the condition.</param>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion representing the union of all requested year/month/day ranges.</returns>
    private static IIdentityCriterion MaterializeStructuredDateYearMonthDayInLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor)
    {
        IReadOnlyList<(int year, int month, int day)> tuples = RequireYearMonthDaySet(values, 0, descriptor);
        return CreateConditionMultiRangeLeaf(index, tuples.Select(tuple => CreateStructuredDateYearMonthDayKeyRange(index, tuple.year, tuple.month, tuple.day, descriptor)));
    }

    /// <summary>
    /// Materializes one structured date year/month/day tuple as an inclusive range over the selected logical index.<br/>
    /// The helper is shared by single-tuple and tuple-set operators so both paths keep identical UTC-normalized bounds for DateTimeOffset indexes.<br/>
    /// </summary>
    /// <param name="index">The logical date index selected by the condition.</param>
    /// <param name="year">The year component to match.</param>
    /// <param name="month">The month component to match.</param>
    /// <param name="day">The day component to match.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion over the structured date day range.</returns>
    private static IIdentityCriterion MaterializeStructuredDateYearMonthDayRange(
        IIndex index,
        int year,
        int month,
        int day,
        LibraDexConditionLeafDescriptor descriptor)
    {
        LibraDexIdentityKeyRange range = CreateStructuredDateYearMonthDayKeyRange(index, year, month, day, descriptor);
        object lower = range.LowerKey;
        object upper = range.UpperKey;
        return index.KeyType == typeof(DateTime)
            ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, lower, upper)
            : index.KeyType == typeof(DateTimeOffset)
                ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, lower, upper)
                : index.KeyType == typeof(DateOnly)
                    ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, lower, upper)
                    : throw new NotSupportedException($"Condition operator {descriptor.Operator} cannot materialize against index key type {index.KeyType.FullName}.");
    }

    /// <summary>
    /// Creates one inclusive structured-date year/month/day range payload for condition-driven multi-range retrieval.<br/>
    /// The helper preserves the same day bounds for single tuple and tuple-set branches so they can differ only by primitive shape, not encoded range semantics.<br/>
    /// </summary>
    /// <param name="index">The logical date index selected by the condition.</param>
    /// <param name="year">The year component to match.</param>
    /// <param name="month">The month component to match.</param>
    /// <param name="day">The day component to match.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>The runtime key range payload.</returns>
    private static LibraDexIdentityKeyRange CreateStructuredDateYearMonthDayKeyRange(
        IIndex index,
        int year,
        int month,
        int day,
        LibraDexConditionLeafDescriptor descriptor)
    {
        DateTime lower = CreateDateTimeDayLower(year, month, day);
        DateTime upper = CreateDateTimeDayUpper(year, month, day);
        return index.KeyType == typeof(DateTime)
            ? new LibraDexIdentityKeyRange(lower, upper)
            : index.KeyType == typeof(DateTimeOffset)
                ? new LibraDexIdentityKeyRange(new DateTimeOffset(lower, TimeSpan.Zero), new DateTimeOffset(upper, TimeSpan.Zero))
                : index.KeyType == typeof(DateOnly)
                    ? new LibraDexIdentityKeyRange(DateOnly.FromDateTime(lower), DateOnly.FromDateTime(upper))
                    : throw new NotSupportedException($"Condition operator {descriptor.Operator} cannot materialize against index key type {index.KeyType.FullName}.");
    }

    /// <summary>
    /// Creates one inclusive structured `TimeOnly` hour range payload for condition-driven range retrieval.<br/>
    /// The lower bound starts at the first tick of <paramref name="startHour"/> and the upper bound ends at the last tick of <paramref name="endHour"/> so the range includes every time inside the selected semantic interval.<br/>
    /// </summary>
    /// <param name="index">The logical time index selected by the condition.</param>
    /// <param name="startHour">The inclusive lower hour.</param>
    /// <param name="endHour">The inclusive upper hour.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>The runtime key range payload.</returns>
    private static LibraDexIdentityKeyRange CreateStructuredTimeOnlyHourKeyRange(
        IIndex index,
        int startHour,
        int endHour,
        LibraDexConditionLeafDescriptor descriptor)
    {
        ValidateHour(startHour, descriptor);
        ValidateHour(endHour, descriptor);
        if (endHour < startHour)
        {
            throw new ArgumentOutOfRangeException(nameof(endHour), $"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires end hour greater than or equal to start hour.");
        }

        return index.KeyType == typeof(TimeOnly)
            ? new LibraDexIdentityKeyRange(new TimeOnly(startHour, 0), CreateTimeOnlyHourUpper(endHour))
            : throw new NotSupportedException($"Condition operator {descriptor.Operator} cannot materialize against index key type {index.KeyType.FullName}.");
    }

    /// <summary>
    /// Materializes a structured date `YearInMonths` leaf as a same-index union of month ranges inside one year.<br/>
    /// This is executable through the current ordered-range bridge because each selected year/month pair is contiguous in the encoded date order.<br/>
    /// </summary>
    /// <param name="index">The logical date index selected by the condition.</param>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion representing the union of the selected month ranges inside the requested year.</returns>
    private static IIdentityCriterion MaterializeStructuredDateYearInMonthsLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor)
    {
        (int year, IReadOnlyList<int> months) = RequireYearMonths(values, 0, descriptor);
        return CreateConditionMultiRangeLeaf(index, months.Select(month => CreateStructuredDateYearMonthKeyRange(index, year, month, descriptor)));
    }

    /// <summary>
    /// Materializes a structured date `YearQuarter` leaf as one ordered range inside a single year.<br/>
    /// Quarter-without-year branches are not contiguous across the full key space, but one quarter inside one year is contiguous and can use the current range primitive.<br/>
    /// </summary>
    /// <param name="index">The logical date index selected by the condition.</param>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion over the selected year/quarter range.</returns>
    private static IIdentityCriterion MaterializeStructuredDateYearQuarterLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor)
    {
        int year = RequireInt32(values, 0, descriptor);
        int quarter = RequireInt32(values, 1, descriptor);
        ValidateQuarter(quarter, descriptor);
        int startMonth = ((quarter - 1) * 3) + 1;
        int endMonth = startMonth + 2;
        DateTime lower = CreateDateTimeMonthLower(year, startMonth);
        DateTime upper = CreateDateTimeMonthUpper(year, endMonth);
        return index.KeyType == typeof(DateTime)
            ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, lower, upper)
            : index.KeyType == typeof(DateTimeOffset)
                ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, new DateTimeOffset(lower, TimeSpan.Zero), new DateTimeOffset(upper, TimeSpan.Zero))
                : index.KeyType == typeof(DateOnly)
                    ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, DateOnly.FromDateTime(lower), DateOnly.FromDateTime(upper))
                    : throw new NotSupportedException($"Condition operator {descriptor.Operator} cannot materialize against index key type {index.KeyType.FullName}.");
    }

    /// <summary>
    /// Materializes a UTC-relative day branch as an inclusive day range over the selected logical index.<br/>
    /// DateTime and DateTimeOffset use the full day tick range; DateOnly uses the exact corresponding date value.<br/>
    /// </summary>
    /// <param name="index">The logical date index selected by the condition.</param>
    /// <param name="day">The UTC day to match.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion over the requested UTC day.</returns>
    private static IIdentityCriterion MaterializeStructuredDateRelativeDayLeaf(
        IIndex index,
        DateTime day,
        LibraDexConditionLeafDescriptor descriptor)
    {
        DateTime lower = DateTime.SpecifyKind(day.Date, DateTimeKind.Utc);
        DateTime upper = lower.AddDays(1).AddTicks(-1);
        return index.KeyType == typeof(DateTime)
            ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, lower, upper)
            : index.KeyType == typeof(DateTimeOffset)
                ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, new DateTimeOffset(lower, TimeSpan.Zero), new DateTimeOffset(upper, TimeSpan.Zero))
                : index.KeyType == typeof(DateOnly)
                    ? CreateConditionLeaf(index, LibraDexCriteriaKind.Find, DateOnly.FromDateTime(lower))
                    : throw new NotSupportedException($"Condition operator {descriptor.Operator} cannot materialize against index key type {index.KeyType.FullName}.");
    }

    /// <summary>
    /// Materializes a UTC-relative trailing day-window branch over the selected logical index.<br/>
    /// DateTime and DateTimeOffset match Abraxas' lower-bound behavior from `UtcNow - days`; DateOnly uses an inclusive date range from that UTC date through today.<br/>
    /// </summary>
    /// <param name="index">The logical date index selected by the condition.</param>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion over the requested trailing day window.</returns>
    private static IIdentityCriterion MaterializeStructuredDateInLastDaysLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor)
    {
        int days = RequireNonNegativeInt32(values, 0, descriptor);
        DateTime now = DateTime.UtcNow;
        DateTime from = DateTime.SpecifyKind(now.AddDays(-days), DateTimeKind.Utc);
        return index.KeyType == typeof(DateTime)
            ? CreateConditionLeaf(index, LibraDexCriteriaKind.AtOrAfter, from)
            : index.KeyType == typeof(DateTimeOffset)
                ? CreateConditionLeaf(index, LibraDexCriteriaKind.AtOrAfter, new DateTimeOffset(from, TimeSpan.Zero))
                : index.KeyType == typeof(DateOnly)
                    ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, DateOnly.FromDateTime(from), DateOnly.FromDateTime(now))
                    : throw new NotSupportedException($"Condition operator {descriptor.Operator} cannot materialize against index key type {index.KeyType.FullName}.");
    }

    /// <summary>
    /// Materializes a UTC-relative trailing hour-window branch over the selected logical index.<br/>
    /// This branch is valid only for DateTime and DateTimeOffset indexes because DateOnly does not preserve hour components.<br/>
    /// </summary>
    /// <param name="index">The logical date index selected by the condition.</param>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion over the requested trailing hour window.</returns>
    private static IIdentityCriterion MaterializeStructuredDateInLastHoursLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor)
    {
        int hours = RequireNonNegativeInt32(values, 0, descriptor);
        DateTime from = DateTime.SpecifyKind(DateTime.UtcNow.AddHours(-hours), DateTimeKind.Utc);
        return index.KeyType == typeof(DateTime)
            ? CreateConditionLeaf(index, LibraDexCriteriaKind.AtOrAfter, from)
            : index.KeyType == typeof(DateTimeOffset)
                ? CreateConditionLeaf(index, LibraDexCriteriaKind.AtOrAfter, new DateTimeOffset(from, TimeSpan.Zero))
                : throw new NotSupportedException($"Condition operator {descriptor.Operator} cannot materialize against index key type {index.KeyType.FullName}.");
    }

    /// <summary>
    /// Materializes a UTC-relative trailing minute-window branch over the selected logical index.<br/>
    /// This branch is valid only for DateTime and DateTimeOffset indexes because DateOnly does not preserve minute components.<br/>
    /// </summary>
    /// <param name="index">The logical date index selected by the condition.</param>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion over the requested trailing minute window.</returns>
    private static IIdentityCriterion MaterializeStructuredDateInLastMinutesLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor)
    {
        int minutes = RequireNonNegativeInt32(values, 0, descriptor);
        DateTime from = DateTime.SpecifyKind(DateTime.UtcNow.AddMinutes(-minutes), DateTimeKind.Utc);
        return index.KeyType == typeof(DateTime)
            ? CreateConditionLeaf(index, LibraDexCriteriaKind.AtOrAfter, from)
            : index.KeyType == typeof(DateTimeOffset)
                ? CreateConditionLeaf(index, LibraDexCriteriaKind.AtOrAfter, new DateTimeOffset(from, TimeSpan.Zero))
                : throw new NotSupportedException($"Condition operator {descriptor.Operator} cannot materialize against index key type {index.KeyType.FullName}.");
    }

    /// <summary>
    /// Materializes a structured `TimeOnly` semantic hour branch as one inclusive ordered time range.<br/>
    /// Abraxas defines morning as hours 5 through 11, afternoon as 12 through 16, and evening as 17 through 21; each is contiguous in the structured time key.<br/>
    /// This bridge is intentionally limited to `TimeOnly` indexes because the same hour branch over `DateTime` is component-only across all dates and needs a different primitive or projection.<br/>
    /// </summary>
    /// <param name="index">The logical time index selected by the condition.</param>
    /// <param name="startHour">The inclusive starting hour.</param>
    /// <param name="endHour">The inclusive ending hour.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion over the requested semantic time range.</returns>
    private static IIdentityCriterion MaterializeStructuredTimeOnlyHourRangeLeaf(
        IIndex index,
        int startHour,
        int endHour,
        LibraDexConditionLeafDescriptor descriptor)
    {
        LibraDexIdentityKeyRange range = CreateStructuredTimeOnlyHourKeyRange(index, startHour, endHour, descriptor);
        return CreateConditionLeaf(index, LibraDexCriteriaKind.Between, range.LowerKey, range.UpperKey);
    }

    /// <summary>
    /// Materializes a semantic time-of-day branch against either a `TimeOnly` index or a full date/time index.<br/>
    /// TimeOnly keys can use one ordered range because their encoded key space is one day; DateTime and DateTimeOffset keys use the structured component primitive because the hour interval repeats across dates.<br/>
    /// </summary>
    /// <param name="index">The logical time or date/time index selected by the condition.</param>
    /// <param name="startHour">The inclusive starting hour.</param>
    /// <param name="endHour">The inclusive ending hour.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion for the semantic time-of-day branch.</returns>
    private static IIdentityCriterion MaterializeStructuredTimeOfDayLeaf(
        IIndex index,
        int startHour,
        int endHour,
        LibraDexConditionLeafDescriptor descriptor)
    {
        if (index.KeyType == typeof(TimeOnly))
        {
            return MaterializeStructuredTimeOnlyHourRangeLeaf(index, startHour, endHour, descriptor);
        }

        return MaterializeStructuredDateComponentLeaf(
            index,
            CreateComponentPredicate(HourTest(EnumerateInclusive(startHour, endHour))),
            descriptor);
    }

    /// <summary>
    /// Materializes Abraxas' structured `TimeOnly` night branch as two ordered ranges around midnight.<br/>
    /// Night is defined as hours 22 through 23 or 0 through 4, so the efficient ordered-key bridge is a union of those two extents.<br/>
    /// </summary>
    /// <param name="index">The logical time index selected by the condition.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion union over the two night ranges.</returns>
    private static IIdentityCriterion MaterializeStructuredTimeOnlyNightLeaf(
        IIndex index,
        LibraDexConditionLeafDescriptor descriptor)
    {
        LibraDexIdentityKeyRange late = CreateStructuredTimeOnlyHourKeyRange(index, 22, 23, descriptor);
        LibraDexIdentityKeyRange early = CreateStructuredTimeOnlyHourKeyRange(index, 0, 4, descriptor);
        return CreateConditionMultiRangeLeaf(index, new[] { early, late });
    }

    /// <summary>
    /// Materializes Abraxas' semantic night branch against either a `TimeOnly` index or a full date/time index.<br/>
    /// TimeOnly keys use two ordered ranges around midnight, while DateTime and DateTimeOffset keys use an hour-component mask because night recurs for every encoded date.<br/>
    /// </summary>
    /// <param name="index">The logical time or date/time index selected by the condition.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion for the night branch.</returns>
    private static IIdentityCriterion MaterializeStructuredNightLeaf(
        IIndex index,
        LibraDexConditionLeafDescriptor descriptor)
    {
        if (index.KeyType == typeof(TimeOnly))
        {
            return MaterializeStructuredTimeOnlyNightLeaf(index, descriptor);
        }

        return MaterializeStructuredDateComponentLeaf(
            index,
            CreateComponentPredicate(HourTest(new[] { 0, 1, 2, 3, 4, 22, 23 })),
            descriptor);
    }

    private static IIdentityCriterion MaterializeProjectionLeaf(
        string group,
        LibraDexConditionLeafDescriptor descriptor,
        LibraDexConditionLeafClassification classification,
        IIndex projectionIndex,
        object?[] values)
    {
        if (projectionIndex.Group.Length != 0 &&
            !string.Equals(projectionIndex.Group, group, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Projection index '{projectionIndex.Name}' belongs to group '{projectionIndex.Group}', not condition group '{group}'.");
        }

        return classification.ProjectionKind switch
        {
            LibraDexIndexProjectionKind.Exact => MaterializeExactProjectionLeaf(group, projectionIndex, descriptor, values),
            LibraDexIndexProjectionKind.SortKey => MaterializeSortKeyProjectionLeaf(projectionIndex, descriptor, values),
            LibraDexIndexProjectionKind.FoldedText => MaterializeFoldedTextProjectionLeaf(projectionIndex, descriptor, values),
            _ => throw new NotSupportedException($"Projection bridge for condition operator {descriptor.Operator} with projection {classification.ProjectionKind} is not connected yet.")
        };
    }

    /// <summary>
    /// Materializes an exact projection condition through a caller-supplied physical projection index.<br/>
    /// String suffixes use reversed text bounds, and binary suffixes use reversed byte bounds or a reversed masked predicate fallback.<br/>
    /// </summary>
    /// <param name="group">The logical identity group that owns the condition.</param>
    /// <param name="projectionIndex">The maintained exact projection index selected by the caller's projection resolver.</param>
    /// <param name="descriptor">The original condition leaf descriptor.</param>
    /// <param name="values">The materialized original operands.</param>
    /// <returns>An identity criterion leaf over the exact projection index.</returns>
    private static IIdentityCriterion MaterializeExactProjectionLeaf(
        string group,
        IIndex projectionIndex,
        LibraDexConditionLeafDescriptor descriptor,
        object?[] values)
    {
        if (projectionIndex.KeyType == typeof(byte[]) && descriptor.ValueKind == LibraDexConditionValueKind.Binary)
        {
            return MaterializeExactBinaryProjectionLeaf(group, projectionIndex, descriptor, values);
        }

        if (projectionIndex.KeyType != typeof(string))
        {
            throw new NotSupportedException("Exact-text projection conditions currently require a string projection index so suffix bounds can stay ordered.");
        }

        return descriptor.Operator switch
        {
            LibraDexConditionOperatorKind.EndsWith => CreateConditionLeaf(
                projectionIndex,
                LibraDexCriteriaKind.Between,
                CreateReversedExactTextProjectionValue(RequireNonNullString(values, 0, descriptor)),
                CreateReversedExactTextPrefixUpperBound(RequireNonNullString(values, 0, descriptor))),
            _ => throw new NotSupportedException($"Exact-text projection bridge for condition operator {descriptor.Operator} is not connected yet.")
        };
    }

    /// <summary>
    /// Materializes a binary suffix condition through a maintained reversed exact-byte projection.<br/>
    /// Unmasked suffixes become ordered byte ranges; masked suffix patterns execute as reversed prefix predicates over the projection.<br/>
    /// </summary>
    /// <param name="group">The logical identity group that owns the condition.</param>
    /// <param name="projectionIndex">The maintained reversed exact-byte projection index.</param>
    /// <param name="descriptor">The original binary condition leaf descriptor.</param>
    /// <param name="values">The materialized original operands.</param>
    /// <returns>An identity criterion leaf over the binary projection index.</returns>
    private static IIdentityCriterion MaterializeExactBinaryProjectionLeaf(
        string group,
        IIndex projectionIndex,
        LibraDexConditionLeafDescriptor descriptor,
        object?[] values)
    {
        if (descriptor.Operator != LibraDexConditionOperatorKind.EndsWith)
        {
            throw new NotSupportedException($"Exact binary projection bridge for condition operator {descriptor.Operator} is not connected yet.");
        }

        object operand = RequireValue(values, 0, descriptor);
        if (operand is LibraDexBinaryPatternPredicate compiled)
        {
            if (compiled.TryGetUnmaskedValue(LibraDexBinaryPatternMode.EndsWith, out byte[] unmaskedValue))
            {
                return CreateExactBinarySuffixProjectionRange(group, projectionIndex, unmaskedValue);
            }

            return CreateProjectionConditionLeaf(group, projectionIndex, LibraDexCriteriaKind.BinaryPattern, compiled.ToReversedStartsWith());
        }

        byte[] suffix = operand as byte[]
            ?? throw new InvalidOperationException($"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires a binary suffix operand.");
        return CreateExactBinarySuffixProjectionRange(group, projectionIndex, suffix);
    }

    /// <summary>
    /// Builds the ordered range over reversed fixed-width binary keys for an original-key suffix value.<br/>
    /// For example, suffix `CC DD` over a 16-byte key becomes reversed-prefix range `DD CC 00...` through `DD CC FF...`.<br/>
    /// </summary>
    /// <param name="group">The logical identity group that owns the condition.</param>
    /// <param name="projectionIndex">The maintained reversed exact-byte projection index.</param>
    /// <param name="suffix">The original forward suffix bytes.</param>
    /// <returns>An identity criterion leaf over the reversed projection range.</returns>
    private static IIdentityCriterion CreateExactBinarySuffixProjectionRange(string group, IIndex projectionIndex, byte[] suffix)
    {
        int width = projectionIndex.FixedKeyByteWidth
            ?? throw new NotSupportedException("Binary suffix projections require a fixed-width byte[] projection index.");
        if (suffix.Length > width)
        {
            byte[] reversedSuffix = (byte[])suffix.Clone();
            Array.Reverse(reversedSuffix);
            return CreateProjectionConditionLeaf(
                group,
                projectionIndex,
                LibraDexCriteriaKind.BinaryPattern,
                LibraDexBinaryPatternPredicate.Create(LibraDexBinaryPatternMode.StartsWith, reversedSuffix));
        }

        byte[] lower = new byte[width];
        byte[] upper = new byte[width];
        Array.Fill(upper, (byte)0xFF);
        for (int i = 0; i < suffix.Length; i++)
        {
            byte value = suffix[suffix.Length - 1 - i];
            lower[i] = value;
            upper[i] = value;
        }

        return CreateProjectionConditionLeaf(group, projectionIndex, LibraDexCriteriaKind.Between, lower, upper);
    }

    /// <summary>
    /// Materializes a case-insensitive string comparison through a caller-supplied sort-key projection index.<br/>
    /// The original condition operands stay developer-facing strings, while the projection leaf receives `CompareInfo.GetSortKey(..., IgnoreCase).KeyData` byte keys so execution can reuse the ordinary exact and ordered primitive routes.<br/>
    /// </summary>
    /// <param name="projectionIndex">The maintained sort-key projection index selected by the caller's projection resolver.</param>
    /// <param name="descriptor">The original condition leaf descriptor.</param>
    /// <param name="values">The materialized original string operands.</param>
    /// <returns>An identity criterion leaf or ordered exclusion tree over the sort-key projection index.</returns>
    private static IIdentityCriterion MaterializeSortKeyProjectionLeaf(
        IIndex projectionIndex,
        LibraDexConditionLeafDescriptor descriptor,
        object?[] values)
    {
        if (projectionIndex.KeyType != typeof(byte[]))
        {
            throw new NotSupportedException("Sort-key projection conditions require a byte[] projection index.");
        }

        return descriptor.Operator switch
        {
            LibraDexConditionOperatorKind.EqualTo => CreateConditionLeaf(projectionIndex, LibraDexCriteriaKind.Find, CreateSortKeyProjectionValue(RequireNonNullString(values, 0, descriptor), descriptor)),
            LibraDexConditionOperatorKind.NotEqualTo => CreateOrderedPointExclusionLeaf(projectionIndex, CreateSortKeyProjectionValue(RequireNonNullString(values, 0, descriptor), descriptor)),
            LibraDexConditionOperatorKind.GreaterThan => CreateConditionLeaf(projectionIndex, LibraDexCriteriaKind.After, CreateSortKeyProjectionValue(RequireNonNullString(values, 0, descriptor), descriptor)),
            LibraDexConditionOperatorKind.GreaterOrEqual => CreateConditionLeaf(projectionIndex, LibraDexCriteriaKind.AtOrAfter, CreateSortKeyProjectionValue(RequireNonNullString(values, 0, descriptor), descriptor)),
            LibraDexConditionOperatorKind.LessThan => CreateConditionLeaf(projectionIndex, LibraDexCriteriaKind.Before, CreateSortKeyProjectionValue(RequireNonNullString(values, 0, descriptor), descriptor)),
            LibraDexConditionOperatorKind.LessOrEqual => CreateConditionLeaf(projectionIndex, LibraDexCriteriaKind.AtOrBefore, CreateSortKeyProjectionValue(RequireNonNullString(values, 0, descriptor), descriptor)),
            LibraDexConditionOperatorKind.Between => CreateConditionLeaf(
                projectionIndex,
                LibraDexCriteriaKind.Between,
                CreateSortKeyProjectionValue(RequireNonNullString(values, 0, descriptor), descriptor),
                CreateSortKeyProjectionValue(RequireNonNullString(values, 1, descriptor), descriptor)),
            LibraDexConditionOperatorKind.NotBetween => CreateOrderedRangeExclusionLeaf(
                projectionIndex,
                CreateSortKeyProjectionValue(RequireNonNullString(values, 0, descriptor), descriptor),
                CreateSortKeyProjectionValue(RequireNonNullString(values, 1, descriptor), descriptor)),
            LibraDexConditionOperatorKind.InSet => CreateProjectionMembershipLeaf(
                projectionIndex,
                (object)RequireNonNullStringEnumerable(values, 0, descriptor).Select(value => CreateSortKeyProjectionValue(value, descriptor)).ToArray()),
            LibraDexConditionOperatorKind.NotInSet => CreateProjectionMembershipLeaf(
                projectionIndex,
                (object)RequireNonNullStringEnumerable(values, 0, descriptor).Select(value => CreateSortKeyProjectionValue(value, descriptor)).ToArray()).Not(),
            _ => throw new NotSupportedException($"Sort-key projection bridge for condition operator {descriptor.Operator} is not connected yet.")
        };
    }

    /// <summary>
    /// Materializes a folded-text condition through a caller-supplied folded projection index.<br/>
    /// Folded string projection keys use the same invariant-or-named culture lower-casing convention as the adopted Abraxas string operator; starts-with is represented as an ordered string extent when the projection key is string-backed.<br/>
    /// </summary>
    /// <param name="projectionIndex">The maintained folded-text projection index selected by the caller's projection resolver.</param>
    /// <param name="descriptor">The original condition leaf descriptor.</param>
    /// <param name="values">The materialized original string operands.</param>
    /// <returns>An identity criterion leaf or ordered exclusion tree over the folded-text projection index.</returns>
    private static IIdentityCriterion MaterializeFoldedTextProjectionLeaf(
        IIndex projectionIndex,
        LibraDexConditionLeafDescriptor descriptor,
        object?[] values)
    {
        if (projectionIndex.KeyType != typeof(string))
        {
            throw new NotSupportedException("Folded-text projection conditions currently require a string projection index so prefix bounds can stay ordered.");
        }

        return descriptor.Operator switch
        {
            LibraDexConditionOperatorKind.EqualTo => CreateConditionLeaf(projectionIndex, LibraDexCriteriaKind.Find, CreateFoldedTextProjectionValue(RequireNonNullString(values, 0, descriptor), descriptor)),
            LibraDexConditionOperatorKind.NotEqualTo => CreateOrderedPointExclusionLeaf(projectionIndex, CreateFoldedTextProjectionValue(RequireNonNullString(values, 0, descriptor), descriptor)),
            LibraDexConditionOperatorKind.StartsWith => CreateConditionLeaf(
                projectionIndex,
                LibraDexCriteriaKind.Between,
                CreateFoldedTextProjectionValue(RequireNonNullString(values, 0, descriptor), descriptor),
                CreateFoldedTextPrefixUpperBound(RequireNonNullString(values, 0, descriptor), descriptor)),
            LibraDexConditionOperatorKind.EndsWith => CreateConditionLeaf(
                projectionIndex,
                LibraDexCriteriaKind.Between,
                CreateReversedFoldedTextProjectionValue(RequireNonNullString(values, 0, descriptor), descriptor),
                CreateReversedFoldedTextPrefixUpperBound(RequireNonNullString(values, 0, descriptor), descriptor)),
            LibraDexConditionOperatorKind.InSet => CreateProjectionMembershipLeaf(
                projectionIndex,
                (object)RequireNonNullStringEnumerable(values, 0, descriptor).Select(value => CreateFoldedTextProjectionValue(value, descriptor)).ToArray()),
            LibraDexConditionOperatorKind.NotInSet => CreateProjectionMembershipLeaf(
                projectionIndex,
                (object)RequireNonNullStringEnumerable(values, 0, descriptor).Select(value => CreateFoldedTextProjectionValue(value, descriptor)).ToArray()).Not(),
            _ => throw new NotSupportedException($"Folded-text projection bridge for condition operator {descriptor.Operator} is not connected yet.")
        };
    }

    /// <summary>
    /// Creates one sort-key projection operand using Abraxas-compatible culture selection and ignore-case comparison options.<br/>
    /// </summary>
    /// <param name="value">The original string value.</param>
    /// <param name="descriptor">The source descriptor carrying the optional culture name.</param>
    /// <returns>The stable sort-key bytes for the supplied string.</returns>
    private static byte[] CreateSortKeyProjectionValue(string value, LibraDexConditionLeafDescriptor descriptor)
        => ResolveConditionCulture(descriptor).CompareInfo.GetSortKey(value, CompareOptions.IgnoreCase).KeyData;

    /// <summary>
    /// Creates one folded-text projection operand using Abraxas-compatible invariant-or-named culture lower-casing.<br/>
    /// </summary>
    /// <param name="value">The original string value.</param>
    /// <param name="descriptor">The source descriptor carrying the optional culture name.</param>
    /// <returns>The folded string value.</returns>
    private static string CreateFoldedTextProjectionValue(string value, LibraDexConditionLeafDescriptor descriptor)
        => value.ToLower(ResolveConditionCulture(descriptor));

    /// <summary>
    /// Creates the exclusive-like upper sentinel used to represent a folded-text prefix as an inclusive LibraDex range leaf.<br/>
    /// The current range primitive is inclusive, so the sentinel mirrors Abraxas' high Unicode suffix and remains a projection bridge detail rather than new public retrieval vocabulary.<br/>
    /// </summary>
    /// <param name="value">The original prefix value.</param>
    /// <param name="descriptor">The source descriptor carrying the optional culture name.</param>
    /// <returns>The folded prefix plus the high sentinel character.</returns>
    private static string CreateFoldedTextPrefixUpperBound(string value, LibraDexConditionLeafDescriptor descriptor)
        => CreateFoldedTextProjectionValue(value, descriptor) + '\uffff';

    /// <summary>
    /// Creates one reversed folded-text projection operand for suffix matching.<br/>
    /// A suffix condition such as `EndsWith("son")` becomes a prefix-shaped ordered extent over the maintained reversed folded projection key `nos`.<br/>
    /// </summary>
    /// <param name="value">The original suffix value.</param>
    /// <param name="descriptor">The source descriptor carrying the optional culture name.</param>
    /// <returns>The reversed folded suffix value.</returns>
    private static string CreateReversedFoldedTextProjectionValue(string value, LibraDexConditionLeafDescriptor descriptor)
    {
        string folded = CreateFoldedTextProjectionValue(value, descriptor);
        return string.Create(folded.Length, folded, static (destination, source) =>
        {
            for (int i = 0; i < source.Length; i++)
            {
                destination[i] = source[source.Length - 1 - i];
            }
        });
    }

    /// <summary>
    /// Creates one reversed exact-text projection operand.<br/>
    /// A suffix condition such as `EndsWith("son")` becomes a prefix-shaped ordered extent over the maintained reversed exact projection key `nos` while preserving case-sensitive semantics.<br/>
    /// </summary>
    /// <param name="value">The original suffix value.</param>
    /// <returns>The reversed exact suffix value.</returns>
    private static string CreateReversedExactTextProjectionValue(string value)
    {
        return string.Create(value.Length, value, static (destination, source) =>
        {
            for (int i = 0; i < source.Length; i++)
            {
                destination[i] = source[source.Length - 1 - i];
            }
        });
    }

    /// <summary>
    /// Creates the inclusive upper sentinel for a reversed exact suffix extent.<br/>
    /// The current range primitive is inclusive, so the high sentinel mirrors the forward prefix bridge while targeting reversed exact keys.<br/>
    /// </summary>
    /// <param name="value">The original suffix value.</param>
    /// <returns>The reversed exact suffix plus the high sentinel character.</returns>
    private static string CreateReversedExactTextPrefixUpperBound(string value)
        => CreateReversedExactTextProjectionValue(value) + '\uffff';

    /// <summary>
    /// Creates the inclusive upper sentinel for a reversed folded suffix extent.<br/>
    /// The current range primitive is inclusive, so the high sentinel mirrors the forward folded-prefix bridge while targeting the reversed projection.<br/>
    /// </summary>
    /// <param name="value">The original suffix value.</param>
    /// <param name="descriptor">The source descriptor carrying the optional culture name.</param>
    /// <returns>The reversed folded suffix plus the high sentinel character.</returns>
    private static string CreateReversedFoldedTextPrefixUpperBound(string value, LibraDexConditionLeafDescriptor descriptor)
        => CreateReversedFoldedTextProjectionValue(value, descriptor) + '\uffff';

    /// <summary>
    /// Resolves the culture metadata captured by a string condition descriptor.<br/>
    /// Empty or null culture values follow Abraxas' convention and use <see cref="CultureInfo.InvariantCulture"/>.<br/>
    /// </summary>
    /// <param name="descriptor">The source condition descriptor.</param>
    /// <returns>The resolved culture.</returns>
    private static CultureInfo ResolveConditionCulture(LibraDexConditionLeafDescriptor descriptor)
    {
        return string.IsNullOrEmpty(descriptor.Culture)
            ? CultureInfo.InvariantCulture
            : CultureInfo.GetCultureInfo(descriptor.Culture);
    }

    /// <summary>
    /// Reads one required string operand from a condition leaf.<br/>
    /// Projection bridges intentionally fail before primitive execution if a generated descriptor supplies a non-string operand for a string projection.<br/>
    /// </summary>
    /// <param name="values">The materialized operand values.</param>
    /// <param name="ordinal">The operand index.</param>
    /// <param name="descriptor">The source descriptor used for error context.</param>
    /// <returns>The required string operand.</returns>
    private static string? RequireString(object?[] values, int ordinal, LibraDexConditionLeafDescriptor descriptor)
    {
        if (ordinal >= values.Length)
        {
            throw new InvalidOperationException($"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires string operand {ordinal}.");
        }

        object? value = values[ordinal];
        return value is null || value is string
            ? (string?)value
            : throw new InvalidOperationException($"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires string operand {ordinal}.");
    }

    private static string RequireNonNullString(object?[] values, int ordinal, LibraDexConditionLeafDescriptor descriptor)
    {
        return RequireString(values, ordinal, descriptor)
            ?? throw new InvalidOperationException($"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires non-null string operand {ordinal}.");
    }

    /// <summary>
    /// Reads one required regex pattern operand from a condition leaf.<br/>
    /// Regex-based operators accept either a pattern string or a caller-provided <see cref="Regex"/> instance so fluent code can choose between low-friction inline regex text and reusable compiled regex objects.<br/>
    /// </summary>
    /// <param name="values">The materialized operand values.</param>
    /// <param name="ordinal">The operand ordinal to read.</param>
    /// <param name="descriptor">The source condition leaf descriptor for diagnostics.</param>
    /// <returns>The required pattern as either <see cref="string"/> or <see cref="Regex"/>.</returns>
    private static object RequireRegexPattern(object?[] values, int ordinal, LibraDexConditionLeafDescriptor descriptor)
    {
        object value = RequireValue(values, ordinal, descriptor);
        return value is string or Regex
            ? value
            : throw new InvalidOperationException($"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires string or Regex operand {ordinal}.");
    }

    /// <summary>
    /// Attempts to classify one already-materialized operand as a null or empty key-state selector.<br/>
    /// The condition builder uses this before ordinary key materialization so explicit null/empty predicates route through metadata-backed key-state roots instead of sentinel values in the value router.<br/>
    /// </summary>
    /// <param name="values">The materialized operand values.</param>
    /// <param name="ordinal">The operand index.</param>
    /// <param name="keyState">Receives the key-state selector when the operand is null, empty, or already a <see cref="NullKey"/>.</param>
    /// <returns><see langword="true"/> when the operand is a key-state selector.</returns>
    private static bool TryGetNullKeyState(object?[] values, int ordinal, out NullKey keyState)
    {
        if (ordinal >= values.Length)
        {
            keyState = default;
            return false;
        }

        object? value = values[ordinal];
        switch (value)
        {
            case NullKey state:
                keyState = state;
                return true;
            case null:
            case DBNull:
                keyState = NullKey.Null;
                return true;
            case string text when text.Length == 0:
                keyState = NullKey.Empty;
                return true;
            case byte[] bytes when bytes.Length == 0:
                keyState = NullKey.Empty;
                return true;
            default:
                keyState = default;
                return false;
        }
    }

    /// <summary>
    /// Reads one required string set operand from a condition leaf.<br/>
    /// The adopted condition builder stores membership operands as an enumerable so projection bridges can transform every member into the maintained projection key type in one place.<br/>
    /// </summary>
    /// <param name="values">The materialized operand values.</param>
    /// <param name="ordinal">The operand index.</param>
    /// <param name="descriptor">The source descriptor used for error context.</param>
    /// <returns>The required string values.</returns>
    private static IReadOnlyList<string?> RequireStringSet(object?[] values, int ordinal, LibraDexConditionLeafDescriptor descriptor)
    {
        List<string?> strings = new();
        foreach (string? text in RequireStringEnumerable(values, ordinal, descriptor))
        {
            strings.Add(text);
        }

        return strings;
    }

    private static IEnumerable<string?> RequireStringEnumerable(object?[] values, int ordinal, LibraDexConditionLeafDescriptor descriptor)
    {
        foreach (object? value in RequireEnumerable(values, ordinal, descriptor))
        {
            if (value is null)
            {
                yield return null;
                continue;
            }

            if (value is not string text)
            {
                throw new InvalidOperationException($"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires only string set operands.");
            }

            yield return text;
        }
    }

    private static IEnumerable<string> RequireNonNullStringEnumerable(object?[] values, int ordinal, LibraDexConditionLeafDescriptor descriptor)
    {
        foreach (string? value in RequireStringEnumerable(values, ordinal, descriptor))
        {
            yield return value
                ?? throw new InvalidOperationException($"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires only non-null string set operands.");
        }
    }

    private static object RequireValue(object?[] values, int ordinal, LibraDexConditionLeafDescriptor descriptor)
    {
        if (ordinal >= values.Length || values[ordinal] is null)
        {
            throw new InvalidOperationException($"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires operand {ordinal}.");
        }

        return values[ordinal]!;
    }

    private static object RequireNonNullMembershipValue(object? value, LibraDexConditionLeafDescriptor descriptor)
        => value ?? throw new InvalidOperationException($"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' does not allow null membership values.");

    /// <summary>
    /// Reads one required Int32 operand from a condition leaf.<br/>
    /// Date-part operators use Int32 component values so invalid generated descriptors fail before reaching physical criteria execution.<br/>
    /// </summary>
    /// <param name="values">The materialized operand values.</param>
    /// <param name="ordinal">The operand ordinal to read.</param>
    /// <param name="descriptor">The source condition leaf descriptor for diagnostics.</param>
    /// <returns>The required Int32 operand.</returns>
    private static int RequireInt32(object?[] values, int ordinal, LibraDexConditionLeafDescriptor descriptor)
    {
        object value = RequireValue(values, ordinal, descriptor);
        return value is int typed
            ? typed
            : throw new InvalidOperationException($"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires Int32 operand {ordinal}.");
    }

    /// <summary>
    /// Reads an optional regex capture-group number from a condition leaf.<br/>
    /// Missing group operands mean group zero, which compares `Regex.Match(...).Value` rather than a numbered capture group.<br/>
    /// </summary>
    /// <param name="values">The materialized operand values.</param>
    /// <param name="ordinal">The optional operand ordinal to read.</param>
    /// <param name="descriptor">The source condition leaf descriptor for diagnostics.</param>
    /// <returns>The capture group number, or null when the whole match should be compared.</returns>
    private static int? TryRequireRegexGroupNumber(object?[] values, int ordinal, LibraDexConditionLeafDescriptor descriptor)
    {
        if (ordinal >= values.Length)
        {
            return null;
        }

        int groupNumber = RequireInt32(values, ordinal, descriptor);
        return groupNumber >= 0
            ? groupNumber
            : throw new ArgumentOutOfRangeException(nameof(values), groupNumber, $"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires a non-negative regex group number.");
    }

    private static TEnum RequireEnum<TEnum>(object?[] values, int ordinal, LibraDexConditionLeafDescriptor descriptor)
        where TEnum : struct, Enum
    {
        object value = RequireValue(values, ordinal, descriptor);
        return value is TEnum typed
            ? typed
            : throw new InvalidOperationException($"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires {typeof(TEnum).Name} operand {ordinal}.");
    }

    /// <summary>
    /// Reads one required non-negative Int32 operand from a condition leaf.<br/>
    /// Relative date-window operators use non-negative component counts so invalid generated descriptors fail before physical criteria execution.<br/>
    /// </summary>
    /// <param name="values">The materialized operand values.</param>
    /// <param name="ordinal">The operand ordinal to read.</param>
    /// <param name="descriptor">The source condition leaf descriptor for diagnostics.</param>
    /// <returns>The required non-negative Int32 operand.</returns>
    private static int RequireNonNegativeInt32(object?[] values, int ordinal, LibraDexConditionLeafDescriptor descriptor)
    {
        int value = RequireInt32(values, ordinal, descriptor);
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), $"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires a non-negative Int32 operand {ordinal}.");
        }

        return value;
    }

    /// <summary>
    /// Reads the required structured date component set from a condition leaf.<br/>
    /// Component-set operators store the caller's selected values as one operand so materialization can distinguish set membership from scalar component operands.<br/>
    /// </summary>
    /// <param name="values">The materialized operand values.</param>
    /// <param name="ordinal">The operand ordinal to read.</param>
    /// <param name="descriptor">The source condition leaf descriptor for diagnostics.</param>
    /// <returns>The required component set.</returns>
    private static IReadOnlyList<int> RequireInt32Set(object?[] values, int ordinal, LibraDexConditionLeafDescriptor descriptor)
    {
        object value = RequireValue(values, ordinal, descriptor);
        return value is IReadOnlyList<int> typed
            ? typed
            : throw new InvalidOperationException($"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires an Int32 component set operand.");
    }

    /// <summary>
    /// Reads the required year-plus-month-set payload from a condition leaf.<br/>
    /// The payload is captured as one tuple so the bridge can build same-index month-range unions without conflating the year with membership operands.<br/>
    /// </summary>
    /// <param name="values">The materialized operand values.</param>
    /// <param name="ordinal">The operand ordinal to read.</param>
    /// <param name="descriptor">The source condition leaf descriptor for diagnostics.</param>
    /// <returns>The required year and month set payload.</returns>
    private static (int year, IReadOnlyList<int> months) RequireYearMonths(object?[] values, int ordinal, LibraDexConditionLeafDescriptor descriptor)
    {
        object value = RequireValue(values, ordinal, descriptor);
        if (value is ValueTuple<int, int[]> tuple)
        {
            return (tuple.Item1, tuple.Item2);
        }

        throw new InvalidOperationException($"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires a year and month-set operand.");
    }

    /// <summary>
    /// Reads the required structured date year/month tuple set from a condition leaf.<br/>
    /// Tuple-set operators store the caller's selected pairs as one operand so materialization can build a same-index union without reinterpreting scalar operands.<br/>
    /// </summary>
    /// <param name="values">The materialized operand values.</param>
    /// <param name="ordinal">The operand ordinal to read.</param>
    /// <param name="descriptor">The source condition leaf descriptor for diagnostics.</param>
    /// <returns>The required year/month tuple set.</returns>
    private static IReadOnlyList<(int year, int month)> RequireYearMonthSet(object?[] values, int ordinal, LibraDexConditionLeafDescriptor descriptor)
    {
        object value = RequireValue(values, ordinal, descriptor);
        return value is IReadOnlyList<(int year, int month)> typed
            ? typed
            : throw new InvalidOperationException($"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires a year/month tuple set operand.");
    }

    /// <summary>
    /// Reads the required structured date year/month/day tuple set from a condition leaf.<br/>
    /// Tuple-set operators store the caller's selected tuples as one operand so materialization can build a same-index union without reinterpreting scalar operands.<br/>
    /// </summary>
    /// <param name="values">The materialized operand values.</param>
    /// <param name="ordinal">The operand ordinal to read.</param>
    /// <param name="descriptor">The source condition leaf descriptor for diagnostics.</param>
    /// <returns>The required year/month/day tuple set.</returns>
    private static IReadOnlyList<(int year, int month, int day)> RequireYearMonthDaySet(object?[] values, int ordinal, LibraDexConditionLeafDescriptor descriptor)
    {
        object value = RequireValue(values, ordinal, descriptor);
        return value is IReadOnlyList<(int year, int month, int day)> typed
            ? typed
            : throw new InvalidOperationException($"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires a year/month/day tuple set operand.");
    }

    /// <summary>
    /// Creates the inclusive lower DateTime bound for one structured date year.<br/>
    /// The returned value uses UTC kind so DateTimeOffset materialization can preserve Abraxas-style UTC normalization without changing the DateTime key contract.<br/>
    /// </summary>
    /// <param name="year">The year component.</param>
    /// <returns>The first representable tick in the requested year.</returns>
    private static DateTime CreateDateTimeYearLower(int year)
        => new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Creates the inclusive upper DateTime bound for one structured date year.<br/>
    /// The method uses the tick before the next year, with a `DateTime.MaxValue` guard for year 9999.<br/>
    /// </summary>
    /// <param name="year">The year component.</param>
    /// <returns>The last representable tick in the requested year.</returns>
    private static DateTime CreateDateTimeYearUpper(int year)
    {
        return year == 9999
            ? DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc)
            : new DateTime(year + 1, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(-1);
    }

    /// <summary>
    /// Creates the inclusive lower DateTime bound for one structured date month.<br/>
    /// </summary>
    /// <param name="year">The year component.</param>
    /// <param name="month">The month component.</param>
    /// <returns>The first representable tick in the requested month.</returns>
    private static DateTime CreateDateTimeMonthLower(int year, int month)
        => new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Creates the inclusive upper DateTime bound for one structured date month.<br/>
    /// The method uses the tick before the next month, with a `DateTime.MaxValue` guard for December 9999.<br/>
    /// </summary>
    /// <param name="year">The year component.</param>
    /// <param name="month">The month component.</param>
    /// <returns>The last representable tick in the requested month.</returns>
    private static DateTime CreateDateTimeMonthUpper(int year, int month)
    {
        return year == 9999 && month == 12
            ? DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc)
            : new DateTime(month == 12 ? year + 1 : year, month == 12 ? 1 : month + 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(-1);
    }

    /// <summary>
    /// Creates the inclusive lower DateTime bound for one structured date day.<br/>
    /// </summary>
    /// <param name="year">The year component.</param>
    /// <param name="month">The month component.</param>
    /// <param name="day">The day component.</param>
    /// <returns>The first representable tick in the requested day.</returns>
    private static DateTime CreateDateTimeDayLower(int year, int month, int day)
        => new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Creates the inclusive upper DateTime bound for one structured date day.<br/>
    /// The method uses the tick before the next day, with a `DateTime.MaxValue` guard for December 31, 9999.<br/>
    /// </summary>
    /// <param name="year">The year component.</param>
    /// <param name="month">The month component.</param>
    /// <param name="day">The day component.</param>
    /// <returns>The last representable tick in the requested day.</returns>
    private static DateTime CreateDateTimeDayUpper(int year, int month, int day)
    {
        DateTime lower = CreateDateTimeDayLower(year, month, day);
        return lower.Date == DateTime.MaxValue.Date
            ? DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc)
            : lower.AddDays(1).AddTicks(-1);
    }

    /// <summary>
    /// Creates the inclusive upper `TimeOnly` bound for one structured time hour.<br/>
    /// The method returns the tick before the next hour, with a `TimeOnly.MaxValue` guard for hour 23.<br/>
    /// </summary>
    /// <param name="hour">The hour component.</param>
    /// <returns>The last representable tick in the requested hour.</returns>
    private static TimeOnly CreateTimeOnlyHourUpper(int hour)
    {
        ValidateHour(hour, descriptor: null);
        return hour == 23
            ? TimeOnly.MaxValue
            : new TimeOnly(hour + 1, 0).Add(TimeSpan.FromTicks(-1));
    }

    /// <summary>
    /// Validates one month component before constructing DateTime range bounds.<br/>
    /// This keeps generated condition descriptor errors close to the condition bridge rather than leaking a less specific DateTime constructor exception.<br/>
    /// </summary>
    /// <param name="month">The month component to validate.</param>
    /// <param name="descriptor">The source condition leaf descriptor for diagnostics.</param>
    private static void ValidateMonth(int month, LibraDexConditionLeafDescriptor? descriptor)
    {
        if (month < 1 || month > 12)
        {
            string context = descriptor is null
                ? "Structured date condition"
                : $"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}'";
            throw new ArgumentOutOfRangeException(nameof(month), $"{context} requires month 1 through 12.");
        }
    }

    /// <summary>
    /// Validates one day-of-month component before constructing structured date component predicates.<br/>
    /// The check validates the component domain only; month-specific calendar validity remains the responsibility of tuple/range materializers that know the month and year context.<br/>
    /// </summary>
    /// <param name="day">The day component to validate.</param>
    /// <param name="descriptor">The optional source condition leaf descriptor for diagnostics.</param>
    private static void ValidateDay(int day, LibraDexConditionLeafDescriptor? descriptor)
    {
        if (day < 1 || day > 31)
        {
            string context = descriptor is null
                ? "Structured date condition"
                : $"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}'";
            throw new ArgumentOutOfRangeException(nameof(day), $"{context} requires day 1 through 31.");
        }
    }

    /// <summary>
    /// Validates one quarter component before constructing date range bounds.<br/>
    /// This keeps generated condition descriptor errors close to the condition bridge rather than leaking an imprecise arithmetic or range exception later.<br/>
    /// </summary>
    /// <param name="quarter">The quarter component to validate.</param>
    /// <param name="descriptor">The source condition leaf descriptor for diagnostics.</param>
    private static void ValidateQuarter(int quarter, LibraDexConditionLeafDescriptor descriptor)
    {
        if (quarter < 1 || quarter > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(quarter), $"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires quarter 1 through 4.");
        }
    }

    /// <summary>
    /// Validates one hour component before constructing `TimeOnly` range bounds.<br/>
    /// This keeps generated descriptor errors close to the condition bridge rather than leaking a less specific `TimeOnly` constructor exception.<br/>
    /// </summary>
    /// <param name="hour">The hour component to validate.</param>
    /// <param name="descriptor">The optional source condition leaf descriptor for diagnostics.</param>
    private static void ValidateHour(int hour, LibraDexConditionLeafDescriptor? descriptor)
    {
        if (hour < 0 || hour > 23)
        {
            string context = descriptor is null
                ? "Structured time condition"
                : $"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}'";
            throw new ArgumentOutOfRangeException(nameof(hour), $"{context} requires hour 0 through 23.");
        }
    }

    private static IEnumerable<object> RequireEnumerable(object?[] values, int ordinal, LibraDexConditionLeafDescriptor descriptor)
    {
        object value = RequireValue(values, ordinal, descriptor);
        if (value is IEnumerable<object> objectValues)
        {
            return objectValues;
        }

        if (value is IEnumerable enumerable and not string)
        {
            return enumerable.Cast<object>();
        }

        throw new InvalidOperationException($"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires an enumerable operand.");
    }

    private LibraDexConditionLeafDescriptor RequireLeaf()
        => leaf ?? throw new InvalidOperationException("Condition node is not a leaf.");

    private Func<LibraDexExternalIdentityContext, bool> RequireExternalIdentityFilter()
        => externalIdentityFilter ?? throw new InvalidOperationException("Condition node is not an external identity filter.");

    private Func<IEnumerable<object>> RequireExternalIdentitySource()
        => externalIdentitySource ?? throw new InvalidOperationException("Condition node is not an external identity source.");

    private LibraDexConditionNode RequireLeft()
        => left ?? throw new InvalidOperationException("Condition node is missing its left child.");

    private LibraDexConditionNode RequireRight()
        => right ?? throw new InvalidOperationException("Condition node is missing its right child.");
}
