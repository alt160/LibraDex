using System.Diagnostics;

namespace LibraDex;

public sealed partial class CatalogIdentityGroupIndexes
{
    /// <summary>
    /// Explains how a completed typed condition is expected to execute without enumerating its results.<br/>
    /// The explanation resolves current index selectors once for this inspection and exposes developer-facing scan reasons rather than requiring bridge-enum interpretation.<br/>
    /// </summary>
    /// <typeparam name="TResult">The logical result type declared by the condition.<br/></typeparam>
    /// <param name="condition">The completed typed condition to inspect.<br/></param>
    /// <returns>A value-free execution explanation.<br/></returns>
    public LibraDexQueryExplanation Explain<TResult>(LibraDexCondition<TResult> condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        if (!string.Equals(condition.Group, Name, StringComparison.Ordinal))
            throw new InvalidOperationException("The supplied condition belongs to a different LibraDex index set.");
        return Explain(condition.Filter, condition.Plan());
    }

    /// <summary>
    /// Explains how a completed identity condition is expected to execute without enumerating identities.<br/>
    /// </summary>
    /// <param name="condition">The completed identity condition to inspect.<br/></param>
    /// <returns>A value-free execution explanation.<br/></returns>
    public LibraDexQueryExplanation Explain(LibraDexConditionEndCondition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        if (!string.Equals(condition.Group, Name, StringComparison.Ordinal))
            throw new InvalidOperationException("The supplied condition belongs to a different LibraDex index set.");
        return Explain(condition, resultPlan: null);
    }

    /// <summary>
    /// Executes one completed typed condition with allocation, elapsed-time, route, and returned-row diagnostics.<br/>
    /// Measurement is explicit and works when bounded query history is disabled; when history is enabled the same execution is published once.<br/>
    /// </summary>
    /// <typeparam name="TResult">The logical result type declared by the condition.<br/></typeparam>
    /// <param name="condition">The completed typed condition to execute.<br/></param>
    /// <param name="skip">The number of logical results to skip before collection begins.<br/></param>
    /// <param name="take">The optional maximum number of logical results to return.<br/></param>
    /// <param name="bookmark">The optional condition-result bookmark.<br/></param>
    /// <param name="consistency">The bookmark consistency policy.<br/></param>
    /// <returns>The result collection and diagnostics for this exact execution.<br/></returns>
    public LibraDexQueryMeasurement<TResult> Measure<TResult>(
        LibraDexCondition<TResult> condition,
        int skip = 0,
        int? take = null,
        LibraDexBookmark? bookmark = null,
        LibraDexBookmarkConsistency consistency = LibraDexBookmarkConsistency.GenerationBound)
    {
        ValidateResultCondition(condition, skip, take);
        return MeasureCore(
            condition,
            skip,
            take,
            bookmark,
            consistency,
            publishTelemetry: true,
            forceAllocationMeasurement: true);
    }

    private IReadOnlyList<TResult> GetCore<TResult>(
        LibraDexCondition<TResult> condition,
        int skip,
        int? take,
        LibraDexBookmark? bookmark,
        LibraDexBookmarkConsistency consistency,
        bool conditionIsFrozen = false,
        bool hadDeferredSelectors = false,
        bool hadDeferredValues = false)
    {
        if (take == 0)
            return Array.Empty<TResult>();

        LibraDexPreparedBookmarkResult<TResult> prepared = conditionIsFrozen
            ? PrepareBookmarkResult(
                condition,
                bookmark,
                consistency,
                conditionIsFrozen: true,
                hadDeferredSelectors,
                hadDeferredValues)
            : PrepareBookmarkResult(condition, bookmark, consistency);
        List<TResult> results = take.HasValue ? new List<TResult>(take.Value) : new List<TResult>();
        int skipped = 0;
        foreach (LibraDexBookmarkResult<TResult> result in prepared.Results)
        {
            if (skipped < skip)
            {
                skipped++;
                continue;
            }

            results.Add(result.Value);
            if (take.HasValue && results.Count >= take.Value)
                break;
        }

        return results.ToArray();
    }

    private LibraDexQueryMeasurement<TResult> MeasureCore<TResult>(
        LibraDexCondition<TResult> condition,
        int skip,
        int? take,
        LibraDexBookmark? bookmark,
        LibraDexBookmarkConsistency consistency,
        bool publishTelemetry,
        bool forceAllocationMeasurement)
    {
        LibraDexCondition<TResult> frozen = condition.FreezeForBookmark(
            out bool hadDeferredSelectors,
            out bool hadDeferredValues);
        LibraDexQueryExplanation explanation = Explain(frozen);
        bool measureAllocations = forceAllocationMeasurement ||
            (catalog.Diagnostics.Queries.TryGetFields(out QueryDiagnosticFields fields) &&
             (fields & QueryDiagnosticFields.Allocations) != 0);
        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        long startedTimestamp = Stopwatch.GetTimestamp();
        long startedAllocated = measureAllocations
            ? GC.GetAllocatedBytesForCurrentThread()
            : 0;
        try
        {
            IReadOnlyList<TResult> results = GetCore(
                frozen,
                skip,
                take,
                bookmark,
                consistency,
                conditionIsFrozen: true,
                hadDeferredSelectors,
                hadDeferredValues);
            long allocated = measureAllocations
                ? GC.GetAllocatedBytesForCurrentThread() - startedAllocated
                : 0;
            long elapsed = CatalogQueryTelemetry.GetElapsedTicks(startedTimestamp);
            LibraDexQueryDiagnostics diagnostics = CreateMeasuredDiagnostics(
                explanation,
                results.Count,
                elapsed,
                allocated);
            if (publishTelemetry)
            {
                catalog.Diagnostics.Queries.Publish(
                    startedUtc,
                    Name,
                    explanation.ConditionShape,
                    explanation.RequiresScan,
                    results.Count,
                    elapsed,
                    allocated,
                    LibraDexQueryCompletion.Completed);
            }

            return new LibraDexQueryMeasurement<TResult>(results, diagnostics);
        }
        catch
        {
            if (publishTelemetry)
            {
                catalog.Diagnostics.Queries.Publish(
                    startedUtc,
                    Name,
                    explanation.ConditionShape,
                    explanation.RequiresScan,
                    rowsReturned: 0,
                    CatalogQueryTelemetry.GetElapsedTicks(startedTimestamp),
                    measureAllocations
                        ? GC.GetAllocatedBytesForCurrentThread() - startedAllocated
                        : 0,
                    LibraDexQueryCompletion.Failed);
            }

            throw;
        }
    }

    private LibraDexQueryExplanation Explain(
        LibraDexConditionEndCondition? condition,
        LibraDexConditionResultPlan? resultPlan)
    {
        if (condition is null)
        {
            string unfilteredShape = resultPlan?.Shape ?? "Identities";
            return new LibraDexQueryExplanation(
                Name,
                unfilteredShape,
                requiresScan: false,
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<string>(),
                resultPlan);
        }

        IReadOnlyList<LibraDexConditionLeafClassification> classifications =
            condition.Classify(ResolveOptionalIndex);
        List<string> indexes = new();
        List<string> projections = new();
        List<string> scanReasons = new();
        for (int i = 0; i < classifications.Count; i++)
        {
            LibraDexConditionLeafClassification classification = classifications[i];
            if (!indexes.Contains(classification.IndexName, StringComparer.Ordinal))
                indexes.Add(classification.IndexName);
            if (classification.ProjectionKind is LibraDexIndexProjectionKind projection)
            {
                string name = projection.ToString();
                if (!projections.Contains(name, StringComparer.Ordinal))
                    projections.Add(name);
            }
            if (classification.ExecutionClass == LibraDexConditionExecutionClass.VisibleScanLike)
            {
                scanReasons.Add(
                    $"{classification.IndexName}: {classification.Reason}");
            }
        }

        string conditionShape = resultPlan is null
            ? condition.GetStructureShape()
            : $"{resultPlan.Value.Shape}.Filter[{condition.GetStructureShape()}]";
        return new LibraDexQueryExplanation(
            Name,
            conditionShape,
            scanReasons.Count != 0,
            indexes.ToArray(),
            projections.ToArray(),
            scanReasons.ToArray(),
            resultPlan);
    }

    private IIndex? ResolveOptionalIndex(string indexName)
        => TryGetInfo(indexName, out _) ? this[indexName].Open() : null;

    private static LibraDexQueryDiagnostics CreateMeasuredDiagnostics(
        LibraDexQueryExplanation explanation,
        long rowsReturned,
        long elapsedTicks,
        long allocatedBytes)
    {
        LibraDexExecutionKind executionKind = explanation.RequiresScan
            ? LibraDexExecutionKind.Scan
            : explanation.ProjectionsUsed.Count != 0
                ? LibraDexExecutionKind.Projection
                : LibraDexExecutionKind.FastPath;
        string? projectionName = explanation.ProjectionsUsed.Count == 0
            ? null
            : string.Join(",", explanation.ProjectionsUsed);
        return new LibraDexQueryDiagnostics(
            executionKind,
            projectionName,
            RowsScanned: 0,
            RowsReturned: rowsReturned,
            ElapsedTicks: elapsedTicks,
            ThreadAllocatedBytes: allocatedBytes,
            RequiresScan: explanation.RequiresScan);
    }
}
