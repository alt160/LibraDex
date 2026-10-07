using System.Text;

namespace LibraDex;

public sealed partial class CatalogIdentityGroupIndexes
{
    /// <summary>
    /// Starts an unfiltered condition that returns keys from one named index.<br/>
    /// Selecting the result index implicitly selects every entry visible through that index, so no public <c>All()</c> predicate is required.<br/>
    /// </summary>
    /// <typeparam name="TKey">The requested key result type.<br/></typeparam>
    /// <param name="indexName">The index supplying returned keys and natural result order.<br/></param>
    /// <returns>A result-shaping stage that can order, limit, or terminate the condition.<br/></returns>
    public LibraDexConditionResultEnd<TKey> ReturnKeys<TKey>(string indexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        var result = new LibraDexProjectedResultDescriptor<TKey>(
            Name,
            filter: null,
            new[] { indexName },
            indexName,
            static row => row.Keys[0].As<TKey>(),
            "1Key");
        return new LibraDexConditionResultEnd<TKey>(new LibraDexCondition<TKey>(Name, filter: null, result));
    }

    /// <summary>
    /// Starts an unfiltered condition that returns two scalar keys for each identity visible through the first named index.<br/>
    /// The first index supplies natural order unless a later named <c>OrderBy</c> selects another index.<br/>
    /// </summary>
    /// <typeparam name="TKey1">The requested first key type.<br/></typeparam>
    /// <typeparam name="TKey2">The requested second key type.<br/></typeparam>
    /// <param name="index1">The index supplying the first returned key and natural order.<br/></param>
    /// <param name="index2">The index supplying the second returned key.<br/></param>
    /// <returns>A result-shaping stage that can order, limit, or terminate the condition.<br/></returns>
    public LibraDexConditionResultEnd<(TKey1 Key1, TKey2 Key2)> ReturnKeys<TKey1, TKey2>(string index1, string index2)
    {
        string[] names = Names(index1, index2);
        var result = new LibraDexProjectedResultDescriptor<(TKey1 Key1, TKey2 Key2)>(
            Name,
            filter: null,
            names,
            index1,
            static row => (row.Keys[0].As<TKey1>(), row.Keys[1].As<TKey2>()),
            "2Keys");
        return new LibraDexConditionResultEnd<(TKey1 Key1, TKey2 Key2)>(new LibraDexCondition<(TKey1 Key1, TKey2 Key2)>(Name, filter: null, result));
    }

    /// <summary>
    /// Starts an unfiltered condition that returns three scalar keys for each identity visible through the first named index.<br/>
    /// Generic parameter and index-name order map positionally to the returned tuple.<br/>
    /// </summary>
    /// <typeparam name="TKey1">The requested first key type.<br/></typeparam>
    /// <typeparam name="TKey2">The requested second key type.<br/></typeparam>
    /// <typeparam name="TKey3">The requested third key type.<br/></typeparam>
    /// <param name="index1">The index supplying the first returned key and natural order.<br/></param>
    /// <param name="index2">The index supplying the second returned key.<br/></param>
    /// <param name="index3">The index supplying the third returned key.<br/></param>
    /// <returns>A result-shaping stage that can order, limit, or terminate the condition.<br/></returns>
    public LibraDexConditionResultEnd<(TKey1 Key1, TKey2 Key2, TKey3 Key3)> ReturnKeys<TKey1, TKey2, TKey3>(string index1, string index2, string index3)
    {
        string[] names = Names(index1, index2, index3);
        var result = new LibraDexProjectedResultDescriptor<(TKey1 Key1, TKey2 Key2, TKey3 Key3)>(
            Name,
            filter: null,
            names,
            index1,
            static row => (row.Keys[0].As<TKey1>(), row.Keys[1].As<TKey2>(), row.Keys[2].As<TKey3>()),
            "3Keys");
        return new LibraDexConditionResultEnd<(TKey1 Key1, TKey2 Key2, TKey3 Key3)>(new LibraDexCondition<(TKey1 Key1, TKey2 Key2, TKey3 Key3)>(Name, filter: null, result));
    }

    /// <summary>
    /// Starts an unfiltered condition that returns four scalar keys for each identity visible through the first named index.<br/>
    /// Generic parameter and index-name order map positionally to the returned tuple.<br/>
    /// </summary>
    /// <typeparam name="TKey1">The requested first key type.<br/></typeparam>
    /// <typeparam name="TKey2">The requested second key type.<br/></typeparam>
    /// <typeparam name="TKey3">The requested third key type.<br/></typeparam>
    /// <typeparam name="TKey4">The requested fourth key type.<br/></typeparam>
    /// <param name="index1">The index supplying the first returned key and natural order.<br/></param>
    /// <param name="index2">The index supplying the second returned key.<br/></param>
    /// <param name="index3">The index supplying the third returned key.<br/></param>
    /// <param name="index4">The index supplying the fourth returned key.<br/></param>
    /// <returns>A result-shaping stage that can order, limit, or terminate the condition.<br/></returns>
    public LibraDexConditionResultEnd<(TKey1 Key1, TKey2 Key2, TKey3 Key3, TKey4 Key4)> ReturnKeys<TKey1, TKey2, TKey3, TKey4>(string index1, string index2, string index3, string index4)
    {
        string[] names = Names(index1, index2, index3, index4);
        var result = new LibraDexProjectedResultDescriptor<(TKey1 Key1, TKey2 Key2, TKey3 Key3, TKey4 Key4)>(
            Name,
            filter: null,
            names,
            index1,
            static row => (row.Keys[0].As<TKey1>(), row.Keys[1].As<TKey2>(), row.Keys[2].As<TKey3>(), row.Keys[3].As<TKey4>()),
            "4Keys");
        return new LibraDexConditionResultEnd<(TKey1 Key1, TKey2 Key2, TKey3 Key3, TKey4 Key4)>(new LibraDexCondition<(TKey1 Key1, TKey2 Key2, TKey3 Key3, TKey4 Key4)>(Name, filter: null, result));
    }

    /// <summary>
    /// Starts an unfiltered grouped result condition over one named index in this identity group.<br/>
    /// Exact stored key bytes define groups by default; use <c>AsString(SubIndexType)</c> only when a maintained Folded or SortKey projection is intended.<br/>
    /// </summary>
    /// <param name="indexName">The index whose exact stored key bytes define groups.<br/></param>
    /// <returns>A grouped continuation that can select <c>First</c>, <c>Last</c>, or an aggregate before termination.<br/></returns>
    public LibraDexConditionGroupBy GroupBy(string indexName)
        => new(Name, filter: null, indexName);

    /// <summary>
    /// Materializes a caller-owned result collection from a completed typed condition.<br/>
    /// Skip and take define the requested collection window up front; use <c>OpenReader&lt;TResult&gt;(LibraDexCondition&lt;TResult&gt;)</c> for adaptive movement during consumption.<br/>
    /// </summary>
    /// <typeparam name="TResult">The logical result type declared by the condition.<br/></typeparam>
    /// <param name="condition">The completed typed condition to execute.<br/></param>
    /// <param name="skip">The number of logical results to skip before collection begins.<br/></param>
    /// <param name="take">The optional maximum number of logical results to return.<br/></param>
    /// <param name="bookmark">The optional condition-result bookmark from which collection should continue.<br/></param>
    /// <param name="consistency">The bookmark consistency policy; generation-bound continuation is the safe default.<br/></param>
    /// <returns>A caller-owned result array for the requested window.<br/></returns>
    public IReadOnlyList<TResult> Get<TResult>(
        LibraDexCondition<TResult> condition,
        int skip = 0,
        int? take = null,
        LibraDexBookmark? bookmark = null,
        LibraDexBookmarkConsistency consistency = LibraDexBookmarkConsistency.GenerationBound)
    {
        ValidateResultCondition(condition, skip, take);
        return catalog.Diagnostics.Queries.IsEnabled
            ? MeasureCore(
                condition,
                skip,
                take,
                bookmark,
                consistency,
                publishTelemetry: true,
                forceAllocationMeasurement: false).Results
            : GetCore(condition, skip, take, bookmark, consistency);
    }

    /// <summary>
    /// Streams the logical sequence described by a completed typed condition.<br/>
    /// The sequence may internally buffer when <c>LibraDexCondition&lt;TResult&gt;.Plan</c> reports a blocking result shape, but it does not create the caller-owned collection used by <c>Get&lt;TResult&gt;(LibraDexCondition&lt;TResult&gt;, int, int?)</c>.<br/>
    /// </summary>
    /// <typeparam name="TResult">The logical result type declared by the condition.<br/></typeparam>
    /// <param name="condition">The completed typed condition to execute.<br/></param>
    /// <param name="bookmark">The optional condition-result bookmark whose next unread result should begin the sequence.<br/></param>
    /// <param name="consistency">The bookmark consistency policy; generation-bound continuation is the safe default.<br/></param>
    /// <returns>The condition's logical result sequence.<br/></returns>
    public IEnumerable<TResult> Iterate<TResult>(
        LibraDexCondition<TResult> condition,
        LibraDexBookmark? bookmark = null,
        LibraDexBookmarkConsistency consistency = LibraDexBookmarkConsistency.GenerationBound)
    {
        ValidateResultCondition(condition, skip: 0, take: null);
        if (!catalog.Diagnostics.Queries.TryGetFields(out QueryDiagnosticFields fields))
        {
            LibraDexPreparedBookmarkResult<TResult> prepared = PrepareBookmarkResult(condition, bookmark, consistency);
            return Values(prepared.Results);
        }

        LibraDexCondition<TResult> frozen = condition.FreezeForBookmark(
            out bool hadDeferredSelectors,
            out bool hadDeferredValues);
        LibraDexQueryExplanation explanation = Explain(frozen);
        bool measureAllocations = (fields & QueryDiagnosticFields.Allocations) != 0;
        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        long startedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        long startedAllocated = measureAllocations
            ? GC.GetAllocatedBytesForCurrentThread()
            : 0;
        LibraDexPreparedBookmarkResult<TResult> tracked = PrepareBookmarkResult(
            frozen,
            bookmark,
            consistency,
            conditionIsFrozen: true,
            hadDeferredSelectors,
            hadDeferredValues);
        var telemetry = new LibraDexQueryTelemetrySession(
            catalog.Diagnostics.Queries,
            startedUtc,
            Name,
            explanation.ConditionShape,
            explanation.RequiresScan,
            CatalogQueryTelemetry.GetElapsedTicks(startedTimestamp),
            measureAllocations
                ? GC.GetAllocatedBytesForCurrentThread() - startedAllocated
                : 0,
            measureAllocations);
        return TrackedValues(tracked.Results, telemetry);
    }

    /// <summary>
    /// Opens a live forward-only reader over the logical sequence described by a completed typed condition.<br/>
    /// The reader owns adaptive <c>Skip</c> and <c>Pull</c> movement while the condition remains an immutable selection-and-result descriptor.<br/>
    /// </summary>
    /// <typeparam name="TResult">The logical result type declared by the condition.<br/></typeparam>
    /// <param name="condition">The completed typed condition to execute.<br/></param>
    /// <param name="bookmark">The optional condition-result bookmark whose next unread result should become the reader's first result.<br/></param>
    /// <param name="consistency">The bookmark consistency policy; generation-bound continuation is the safe default.<br/></param>
    /// <returns>A live reader over the condition's logical results.<br/></returns>
    public LibraDexResultReader<TResult> OpenReader<TResult>(
        LibraDexCondition<TResult> condition,
        LibraDexBookmark? bookmark = null,
        LibraDexBookmarkConsistency consistency = LibraDexBookmarkConsistency.GenerationBound)
    {
        ValidateResultCondition(condition, skip: 0, take: null);
        LibraDexQueryTelemetrySession? telemetry = null;
        LibraDexPreparedBookmarkResult<TResult> prepared;
        if (!catalog.Diagnostics.Queries.TryGetFields(out QueryDiagnosticFields fields))
        {
            prepared = PrepareBookmarkResult(condition, bookmark, consistency);
        }
        else
        {
            LibraDexCondition<TResult> frozen = condition.FreezeForBookmark(
                out bool hadDeferredSelectors,
                out bool hadDeferredValues);
            LibraDexQueryExplanation explanation = Explain(frozen);
            bool measureAllocations = (fields & QueryDiagnosticFields.Allocations) != 0;
            DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
            long startedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            long startedAllocated = measureAllocations
                ? GC.GetAllocatedBytesForCurrentThread()
                : 0;
            prepared = PrepareBookmarkResult(
                frozen,
                bookmark,
                consistency,
                conditionIsFrozen: true,
                hadDeferredSelectors,
                hadDeferredValues);
            telemetry = new LibraDexQueryTelemetrySession(
                catalog.Diagnostics.Queries,
                startedUtc,
                Name,
                explanation.ConditionShape,
                explanation.RequiresScan,
                CatalogQueryTelemetry.GetElapsedTicks(startedTimestamp),
                measureAllocations
                    ? GC.GetAllocatedBytesForCurrentThread() - startedAllocated
                    : 0,
                measureAllocations);
        }
        long resultsConsumed = bookmark?.ResultsConsumed ?? 0;
        return new LibraDexResultReader<TResult>(
            prepared.Results,
            prepared.Template,
            consistency,
            resultsConsumed,
            bookmark?.Anchor,
            telemetry);
    }

    private LibraDexPreparedBookmarkResult<TResult> PrepareBookmarkResult<TResult>(
        LibraDexCondition<TResult> condition,
        LibraDexBookmark? bookmark,
        LibraDexBookmarkConsistency consistency)
    {
        if (consistency is not (LibraDexBookmarkConsistency.GenerationBound or LibraDexBookmarkConsistency.LiveContinuation))
            throw new ArgumentOutOfRangeException(nameof(consistency), consistency, "Unknown bookmark consistency policy.");

        LibraDexCondition<TResult> frozen = condition.FreezeForBookmark(
            out bool hadDeferredSelectors,
            out bool hadDeferredValues);
        return PrepareBookmarkResult(
            frozen,
            bookmark,
            consistency,
            conditionIsFrozen: true,
            hadDeferredSelectors,
            hadDeferredValues);
    }

    private LibraDexPreparedBookmarkResult<TResult> PrepareBookmarkResult<TResult>(
        LibraDexCondition<TResult> frozen,
        LibraDexBookmark? bookmark,
        LibraDexBookmarkConsistency consistency,
        bool conditionIsFrozen,
        bool hadDeferredSelectors,
        bool hadDeferredValues)
    {
        if (!conditionIsFrozen)
            throw new InvalidOperationException("Prepared bookmark execution requires a frozen condition.");

        LibraDexBookmarkTemplate template = CreateBookmarkTemplate(
            frozen,
            hadDeferredSelectors,
            hadDeferredValues);

        if (bookmark is not null)
        {
            if (bookmark.FormatVersion != LibraDexBookmark.CurrentFormatVersion)
                throw new InvalidOperationException("The bookmark format version is not supported by this LibraDex build.");
            if (!string.Equals(bookmark.Group, Name, StringComparison.Ordinal))
                throw new InvalidOperationException($"The bookmark belongs to identity group '{bookmark.Group}', not '{Name}'.");
            if (!bookmark.ConditionKey.Matches(template.ConditionKey))
                throw new InvalidOperationException("The bookmark does not apply to the materialized condition, return shape, or ordering.");
            if (consistency == LibraDexBookmarkConsistency.GenerationBound &&
                bookmark.CatalogMutationVersion != template.CatalogMutationVersion)
            {
                throw new InvalidOperationException("The catalog changed after this generation-bound bookmark was captured.");
            }
        }

        LibraDexBookmarkAnchor? seekAnchor = bookmark?.Anchor;
        IEnumerable<LibraDexBookmarkResult<TResult>> results = frozen.Result.IterateBookmark(this, seekAnchor);
        if (bookmark is not null && bookmark.ResultsConsumed != 0)
        {
            results = bookmark.Anchor.HasValue
                ? ContinueAfterAnchor(results, bookmark.Anchor)
                : SkipBookmarkResults(results, bookmark.ResultsConsumed);
        }

        return new LibraDexPreparedBookmarkResult<TResult>(results, template);
    }

    private LibraDexBookmarkTemplate CreateBookmarkTemplate<TResult>(
        LibraDexCondition<TResult> condition,
        bool hadDeferredSelectors,
        bool hadDeferredValues)
    {
        List<LibraDexBookmarkIndexReference> references = new();
        LibraDexConditionEndCondition? filter = condition.Filter;
        if (filter is not null)
        {
            IReadOnlyList<LibraDexConditionLeafDescriptor> leaves = filter.Leaves;
            for (int i = 0; i < leaves.Count; i++)
            {
                LibraDexConditionLeafDescriptor leaf = leaves[i];
                references.Add(new LibraDexBookmarkIndexReference(
                    leaf.IndexName,
                    leaf.IndexSelector.Name,
                    LibraDexBookmarkIndexRole.Filter));
            }
        }

        IReadOnlyList<LibraDexBookmarkIndexReference> resultIndexes = condition.Result.BookmarkIndexes;
        for (int i = 0; i < resultIndexes.Count; i++)
            references.Add(resultIndexes[i]);

        bool hasResultOrder = false;
        for (int i = 0; i < references.Count; i++)
        {
            if ((references[i].Roles & (LibraDexBookmarkIndexRole.Order | LibraDexBookmarkIndexRole.NaturalOrder)) != 0)
            {
                hasResultOrder = true;
                break;
            }
        }

        if (!hasResultOrder && filter is not null && filter.Leaves.Count != 0)
        {
            LibraDexConditionLeafDescriptor first = filter.Leaves[0];
            references.Add(new LibraDexBookmarkIndexReference(
                first.IndexName,
                first.IndexSelector.Name,
                LibraDexBookmarkIndexRole.NaturalOrder));
        }

        List<LibraDexBookmarkIndexInfo> indexInfos = new(references.Count);
        for (int i = 0; i < references.Count; i++)
        {
            LibraDexBookmarkIndexReference reference = references[i];
            int existingIndex = -1;
            for (int j = 0; j < indexInfos.Count; j++)
            {
                if (string.Equals(indexInfos[j].Name, reference.Name, StringComparison.Ordinal))
                {
                    existingIndex = j;
                    break;
                }
            }

            if (existingIndex >= 0)
            {
                LibraDexBookmarkIndexInfo existing = indexInfos[existingIndex];
                indexInfos[existingIndex] = existing with
                {
                    SelectorName = existing.SelectorName ?? reference.SelectorName,
                    Roles = existing.Roles | reference.Roles
                };
                continue;
            }

            if (!TryGetInfo(reference.Name, out CatalogIndexInfo info))
                throw new KeyNotFoundException($"Index '{reference.Name}' was not found in identity group '{Name}' while preparing bookmark provenance.");

            indexInfos.Add(new LibraDexBookmarkIndexInfo(
                reference.Name,
                reference.SelectorName,
                reference.Roles,
                info.Generation));
        }

        LibraDexConditionResultPlan plan = condition.Plan();
        string conditionShape = CreateConditionShape(condition, plan.Shape);
        LibraDexBookmarkConditionInfo conditionInfo = new(
            Name,
            conditionShape,
            hadDeferredSelectors,
            hadDeferredValues);
        LibraDexBookmarkConditionKey conditionKey = CreateConditionKey(condition, plan.Shape);
        return new LibraDexBookmarkTemplate(
            conditionInfo,
            indexInfos.ToArray(),
            catalog.Session.MutationVersion,
            conditionKey);
    }

    private static string CreateConditionShape<TResult>(LibraDexCondition<TResult> condition, string resultShape)
    {
        StringBuilder shape = new(resultShape);
        if (condition.Filter is null)
            return shape.ToString();

        return shape.Append(".Filter[")
            .Append(condition.Filter.GetStructureShape())
            .Append(']')
            .ToString();
    }

    private static LibraDexBookmarkConditionKey CreateConditionKey<TResult>(
        LibraDexCondition<TResult> condition,
        string resultShape)
    {
        if (condition.Filter is null)
            return new LibraDexBookmarkConditionKey(condition.Group, resultShape, Array.Empty<LibraDexBookmarkLeafKey>());

        IReadOnlyList<LibraDexConditionLeafDescriptor> leaves = condition.Filter.Leaves;
        LibraDexBookmarkLeafKey[] keys = new LibraDexBookmarkLeafKey[leaves.Count];
        for (int i = 0; i < keys.Length; i++)
        {
            LibraDexConditionLeafDescriptor leaf = leaves[i];
            object?[] values = new object?[leaf.Operands.Count];
            for (int j = 0; j < values.Length; j++)
                values[j] = leaf.Operands[j].GetValue();

            keys[i] = new LibraDexBookmarkLeafKey(
                leaf.IndexName,
                leaf.ValueKind,
                leaf.Operator,
                leaf.IgnoreCase,
                leaf.Culture,
                values);
        }

        string structuralResultShape = $"{resultShape}.Filter[{condition.Filter.GetStructureShape()}]";
        return new LibraDexBookmarkConditionKey(condition.Group, structuralResultShape, keys);
    }

    private static IEnumerable<LibraDexBookmarkResult<TResult>> SkipBookmarkResults<TResult>(
        IEnumerable<LibraDexBookmarkResult<TResult>> results,
        long count)
    {
        long skipped = 0;
        foreach (LibraDexBookmarkResult<TResult> result in results)
        {
            if (skipped < count)
            {
                skipped++;
                continue;
            }

            yield return result;
        }
    }

    private static IEnumerable<LibraDexBookmarkResult<TResult>> ContinueAfterAnchor<TResult>(
        IEnumerable<LibraDexBookmarkResult<TResult>> results,
        LibraDexBookmarkAnchor? anchor)
    {
        if (anchor is null)
            throw new InvalidOperationException("A live-continuation bookmark has no logical result anchor.");

        bool found = false;
        foreach (LibraDexBookmarkResult<TResult> result in results)
        {
            if (!found)
            {
                if (BookmarkAnchorEquals(result.Anchor, anchor.Value))
                    found = true;
                continue;
            }

            yield return result;
        }

        if (!found)
            throw new InvalidOperationException("The live-continuation bookmark anchor is no longer present in the current result stream.");
    }

    private static bool BookmarkAnchorEquals(LibraDexBookmarkAnchor left, LibraDexBookmarkAnchor right)
        => LibraDexObjectTuple.ValueEquals(left.OrderValue, right.OrderValue) &&
           LibraDexObjectTuple.ValueEquals(left.IdentityValue, right.IdentityValue);

    private static IEnumerable<TResult> Values<TResult>(IEnumerable<LibraDexBookmarkResult<TResult>> results)
    {
        foreach (LibraDexBookmarkResult<TResult> result in results)
            yield return result.Value;
    }

    private static IEnumerable<TResult> TrackedValues<TResult>(
        IEnumerable<LibraDexBookmarkResult<TResult>> results,
        LibraDexQueryTelemetrySession telemetry)
    {
        using IEnumerator<LibraDexBookmarkResult<TResult>> enumerator = results.GetEnumerator();
        bool completed = false;
        try
        {
            while (true)
            {
                telemetry.StartStep(out long startedTimestamp, out long startedAllocated);
                bool moved;
                LibraDexBookmarkResult<TResult> result = default;
                try
                {
                    moved = enumerator.MoveNext();
                    if (moved)
                        result = enumerator.Current;
                }
                catch
                {
                    telemetry.EndStep(startedTimestamp, startedAllocated);
                    telemetry.Complete(LibraDexQueryCompletion.Failed);
                    throw;
                }
                telemetry.EndStep(startedTimestamp, startedAllocated);

                if (!moved)
                {
                    completed = true;
                    telemetry.Complete(LibraDexQueryCompletion.Completed);
                    yield break;
                }

                telemetry.Returned();
                yield return result.Value;
            }
        }
        finally
        {
            if (!completed)
                telemetry.Complete(LibraDexQueryCompletion.StoppedEarly);
        }
    }

    private void ValidateResultCondition<TResult>(LibraDexCondition<TResult> condition, int skip, int? take)
    {
        ArgumentNullException.ThrowIfNull(condition);
        if (!string.Equals(condition.Group, Name, StringComparison.Ordinal))
            throw new InvalidOperationException("The supplied result condition belongs to a different LibraDex index set.");

        if (skip < 0)
            throw new ArgumentOutOfRangeException(nameof(skip), "Skip must be zero or greater.");

        if (take < 0)
            throw new ArgumentOutOfRangeException(nameof(take), "Take must be zero or greater.");
    }

    private static string[] Names(params string[] names)
    {
        for (int i = 0; i < names.Length; i++)
            ArgumentException.ThrowIfNullOrWhiteSpace(names[i], $"index{i + 1}");

        return names;
    }

    private readonly record struct LibraDexPreparedBookmarkResult<TResult>(
        IEnumerable<LibraDexBookmarkResult<TResult>> Results,
        LibraDexBookmarkTemplate Template);
}
