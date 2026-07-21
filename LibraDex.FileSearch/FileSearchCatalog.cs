using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Buffers.Binary;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LibraDex.FileSearch;

internal sealed class FileSearchCatalog : IDisposable
{
    private const string GroupName = "files";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true
    };

    private readonly bool fileBacked;
    private readonly string? catalogPath;
    private Catalog catalog;
    private FileSearchState state;
    private bool disposed;

    private LibraDexStringScalar8Index fileNameIndex = null!;
    private LibraDexStringScalar8Index extensionIndex = null!;
    private LibraDexIndex<ulong, ulong> sizeIndex = null!;
    private LibraDexIndex<ulong, ulong> createdUtcIndex = null!;
    private LibraDexIndex<ulong, ulong> modifiedUtcIndex = null!;
    private LibraDexIndex<ulong, ulong> accessedUtcIndex = null!;
    private LibraDexIndex<uint, ulong> attributesIndex = null!;
    private LibraDexUInt64VarIdentityIndex sizePathIdentityIndex = null!;
    private LibraDexUInt64VarIdentityIndex createdUtcPathIdentityIndex = null!;
    private LibraDexUInt64VarIdentityIndex modifiedUtcPathIdentityIndex = null!;
    private LibraDexUInt64VarIdentityIndex accessedUtcPathIdentityIndex = null!;
    private LibraDexUInt64VarIdentityIndex attributesPathIdentityIndex = null!;
    private VarKeyVarIdentityIndex fileNamePathIdentityIndex = null!;
    private VarKeyVarIdentityIndex extensionPathIdentityIndex = null!;
    private VarKeyVarIdentityIndex fileNameFoldedPathIdentityIndex = null!;
    private VarKeyVarIdentityIndex extensionFoldedPathIdentityIndex = null!;
    private VarKeyVarIdentityIndex fileNameSortKeyPathIdentityIndex = null!;
    private VarKeyVarIdentityIndex extensionSortKeyPathIdentityIndex = null!;
    private VarKeyVarIdentityIndex fileNameReversedPathIdentityIndex = null!;
    private VarKeyVarIdentityIndex extensionReversedPathIdentityIndex = null!;
    private VarKeyVarIdentityIndex fileNameFoldedReversedPathIdentityIndex = null!;
    private VarKeyVarIdentityIndex extensionFoldedReversedPathIdentityIndex = null!;

    private FileSearchCatalog(Catalog catalog, bool fileBacked, string? catalogPath, FileSearchState state)
    {
        this.catalog = catalog;
        this.fileBacked = fileBacked;
        this.catalogPath = catalogPath;
        this.state = state;
        OpenIndexes();
    }

    public string DisplayName => fileBacked && catalogPath is not null
        ? catalogPath
        : "Memory catalog";

    public IReadOnlyList<string> Roots => state.Roots;

    public IReadOnlyList<FileRecord> Files => state.Files;

    public int FileCount => state.Files.Count;

    public FileSearchIdentityMode IdentityMode => state.IdentityMode;

    public bool IsFileBacked => fileBacked;

    public string? CatalogPath => catalogPath;

    public static FileSearchCatalog CreateMemory()
    {
        return new FileSearchCatalog(Catalog.CreateMemory(), fileBacked: false, catalogPath: null, new FileSearchState());
    }

    public static FileSearchCatalog CreateFile(string path, bool overwrite)
    {
        if (overwrite && File.Exists(path))
        {
            File.Delete(path);
        }

        string sidecarPath = GetSidecarPath(path);
        if (overwrite && File.Exists(sidecarPath))
        {
            File.Delete(sidecarPath);
        }

        Catalog catalog = Catalog.CreateOrOpen(path);
        FileSearchState state = File.Exists(sidecarPath)
            ? LoadState(sidecarPath)
            : new FileSearchState();
        PrepareTransientRecordBytes(state.Files);
        return new FileSearchCatalog(catalog, fileBacked: true, path, state);
    }

    public void AddRoot(string path)
    {
        string fullPath = System.IO.Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException(fullPath);
        }

        for (int i = 0; i < state.Roots.Count; i++)
        {
            if (string.Equals(state.Roots[i], fullPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        state.Roots.Add(fullPath);
        SaveState();
    }

    public void RemoveRoot(string path)
    {
        state.Roots.RemoveAll(root => string.Equals(root, path, StringComparison.OrdinalIgnoreCase));
        SaveState();
    }

    public void ResetRoots()
    {
        state.Roots.Clear();
        SaveState();
    }

    public ReindexResult Reindex(FileSearchReindexOptions options, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        state.IdentityMode = options.IdentityMode;
        state.IndexStringFolded = options.IndexStringFolded;
        state.IndexStringSortKey = options.IndexStringSortKey;
        state.IndexStringReversed = options.IndexStringReversed;
        RecreateCatalogStorage();
        OpenIndexes();
        state.Files.Clear();
        state.DisabledSecondaryFields.Clear();
        state.NextId = 1;
        FileSearchReindexTelemetry telemetry = new();
        LibraDexStringScalar8Index.ResetThreadInsertDiagnostics();

        try
        {
        long scanned = 0;
        long indexed = 0;
        long failedFiles = 0;
        long failedFieldAttempts = 0;
        Stopwatch collectWatch = Stopwatch.StartNew();
        List<FileRecord> collected = new();
        for (int i = 0; i < state.Roots.Count; i++)
        {
            string root = state.Roots[i];
            if (!Directory.Exists(root))
            {
                progress?.Report($"Skipped missing root: {root}");
                continue;
            }

            foreach (string path in EnumerateFiles(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                scanned++;
                if (TryCreateRecord(path, state.NextId, out FileRecord? record) && record is not null)
                {
                    state.NextId++;
                    collected.Add(record);
                    if (options.MaxFiles > 0 && collected.Count >= options.MaxFiles)
                    {
                        break;
                    }
                }

                if ((scanned & 1023) == 0)
                {
                    progress?.Report($"Collected {scanned:n0} files from filesystem.");
                }
            }

            if (options.MaxFiles > 0 && collected.Count >= options.MaxFiles)
            {
                break;
            }
        }

        collectWatch.Stop();
        if (options.IdentityMode == FileSearchIdentityMode.PathString)
        {
            PreparePathIdentityStringProjectionBytes(collected, options);
        }
        else if (options.IndexExtension)
        {
            PrepareSyntheticExtensionKeys(collected);
        }

        Stopwatch sortWatch = Stopwatch.StartNew();
        ApplyIngestOrder(collected, options.WriteOrder);
        sortWatch.Stop();
        progress?.Report($"Collected {scanned:n0} files in {FormatDuration(collectWatch.Elapsed)}; insert order {options.WriteOrder} prepared in {FormatDuration(sortWatch.Elapsed)}; layout {options.InsertLayout}.");

        Stopwatch insertWatch = Stopwatch.StartNew();
        FileSearchIndexFailureSummary failures = new(state.DisabledSecondaryFields);
        CatalogIdentityGroupBatchManager? batch = null;
        bool completed = false;
        int effectiveBatchCommitFileCount = options.BatchCommitFileCount;
        if (options.UseGroupBatching)
        {
            batch = catalog.IndexSet(GroupName).Batch;
            batch.Enable(options.ToWriteIntent());
        }

        try
        {
            if (options.InsertLayout == FileSearchInsertLayout.IndexMajor)
            {
                if (options.IdentityMode == FileSearchIdentityMode.PathString)
                {
                    InsertPathIdentityIndexMajor(collected, failures, batch, effectiveBatchCommitFileCount, options, progress, telemetry, insertWatch, cancellationToken, ref indexed, ref failedFiles, ref failedFieldAttempts);
                }
                else
                {
                    InsertIndexMajor(collected, failures, batch, effectiveBatchCommitFileCount, options.IndexExtension, progress, telemetry, insertWatch, cancellationToken, ref indexed, ref failedFiles, ref failedFieldAttempts);
                }
            }
            else
            {
                if (options.IdentityMode == FileSearchIdentityMode.PathString)
                {
                    InsertPathIdentityRecordMajor(collected, failures, batch, effectiveBatchCommitFileCount, options, progress, telemetry, insertWatch, cancellationToken, ref indexed, ref failedFiles, ref failedFieldAttempts);
                }
                else
                {
                    InsertRecordMajor(collected, failures, batch, effectiveBatchCommitFileCount, options.IndexExtension, progress, telemetry, insertWatch, cancellationToken, ref indexed, ref failedFiles, ref failedFieldAttempts);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            completed = true;
        }
        finally
        {
            if (batch is not null && batch.IsEnabled)
            {
                if (completed)
                {
                    Stopwatch finalCommitWatch = Stopwatch.StartNew();
                    LibraDexGenericBatchCommitResult finalCommit = batch.CommitAndDisable();
                    finalCommitWatch.Stop();
                    telemetry.RecordFinalCommit(finalCommit.AttemptedInsertCount, finalCommit.InsertedCount, finalCommit.DeferredCommitRequests, finalCommitWatch.Elapsed);
                    FileSearchLog.Info($"Final group batch commit: Attempted={finalCommit.AttemptedInsertCount:n0}; Inserted={finalCommit.InsertedCount:n0}; DeferredCommits={finalCommit.DeferredCommitRequests:n0}; DirtyShelves={finalCommit.StorageDiagnostics.TotalDirtyShelves:n0}; Bytes={finalCommit.CommitDiagnostics.BytesWritten:n0}; Segments={finalCommit.CommitDiagnostics.StagedSegmentCount:n0}; Commit={FormatDuration(finalCommitWatch.Elapsed)}.");
                }
                else
                {
                    long abandoned = batch.AbortAndDisable();
                    FileSearchLog.Info($"Reindex batch aborted. AbandonedDeferredCommits={abandoned:n0}.");
                }
            }
        }
        insertWatch.Stop();

        cancellationToken.ThrowIfCancellationRequested();

        failures.FlushSummary();
        state.DisabledSecondaryFields = new Dictionary<string, string>(failures.DisabledFields, StringComparer.Ordinal);
        SaveState();
        progress?.Report($"Indexed {indexed:n0} files from {state.Roots.Count:n0} roots. Failed files {failedFiles:n0}, failed field attempts {failedFieldAttempts:n0}, skipped disabled fields {failures.SkippedFieldCount:n0}. Layout {options.InsertLayout}. Collect {FormatDuration(collectWatch.Elapsed)}, sort {FormatDuration(sortWatch.Elapsed)}, insert {FormatDuration(insertWatch.Elapsed)}.");
        FileSearchLog.Info($"String VS8 insert telemetry: {LibraDexStringScalar8Index.CreateThreadInsertDiagnosticsSummary()}.");
        return new ReindexResult(scanned, indexed, failedFiles, failedFieldAttempts, failures.SkippedFieldCount, failures.DisabledFieldNames, new ReindexPhaseDurations(collectWatch.Elapsed, sortWatch.Elapsed, insertWatch.Elapsed), telemetry);
        }
        catch (OperationCanceledException)
        {
            RecreateCatalogStorage();
            OpenIndexes();
            state.Files.Clear();
            state.DisabledSecondaryFields.Clear();
            state.NextId = 1;
            SaveState();
            CompactWorkbenchHeapAfterReset();
            progress?.Report("Reindex canceled; catalog storage was reset.");
            FileSearchLog.Info($"Reindex canceled field telemetry: {telemetry.CreateFieldSummary()}.");
            FileSearchLog.Info($"String VS8 insert telemetry: {LibraDexStringScalar8Index.CreateThreadInsertDiagnosticsSummary()}.");
            FileSearchLog.Info("Reindex canceled; catalog storage was reset.");
            throw;
        }
    }

    public int ClearDisabledSecondaryFields()
    {
        int count = state.DisabledSecondaryFields.Count;
        state.DisabledSecondaryFields.Clear();
        SaveState();
        return count;
    }

    public QueryResult Execute(IReadOnlyList<QueryRow> rows, int skip, int length, FileSearchReadOptions options)
    {
        EnsureCatalogIdentityMode(options.IdentityMode);
        int? take = NormalizeTake(length);
        if (rows.Count == 0)
        {
            IEnumerable<FileRecord> files = state.Files;
            if (skip > 0)
            {
                files = files.Skip(skip);
            }

            if (take is int sidecarTake)
            {
                files = files.Take(sidecarTake);
            }

            return new QueryResult(Hydrate(files, options), "// no condition rows; showing sidecar records");
        }

        if (options.IdentityMode == FileSearchIdentityMode.PathString)
        {
            string pathPreview;
            IReadOnlyList<byte[]> pathIdentities;
            if (HasPathIdentityVarKeyRows(rows))
            {
                pathIdentities = ExecutePathIdentityQuery(rows, skip, take, out pathPreview);
            }
            else
            {
                Stopwatch buildWatch = Stopwatch.StartNew();
                LibraDexConditionEndCondition pathCondition = BuildCondition(rows, out pathPreview);
                buildWatch.Stop();
                Stopwatch retrieveWatch = Stopwatch.StartNew();
                pathIdentities = catalog
                    .IndexSet(GroupName)
                    .GetIdentities<byte[]>(
                        pathCondition,
                        IdentityResultOrdering.PlanNatural,
                        IdentityDeduplication.Distinct,
                        skip: skip,
                        take: take);
                retrieveWatch.Stop();
                LogConditionBuilderQueryUse("Record", rows, FileSearchIdentityMode.PathString, skip, take, pathIdentities.Count, buildWatch.Elapsed, retrieveWatch.Elapsed, TimeSpan.Zero, "SV8");
            }

            Stopwatch hydrateWatch = Stopwatch.StartNew();
            Dictionary<string, FileRecord> byPath = state.Files.ToDictionary(static file => file.Path, StringComparer.OrdinalIgnoreCase);
            List<FileRecord> pathResults = new(pathIdentities.Count);
            for (int i = 0; i < pathIdentities.Count; i++)
            {
                string path = Encoding.UTF8.GetString(pathIdentities[i]);
                if (byPath.TryGetValue(path, out FileRecord? record))
                {
                    pathResults.Add(record);
                }
            }

            IReadOnlyList<FileRecord> hydrated = Hydrate(pathResults, options);
            hydrateWatch.Stop();
            LogQueryPresentationUse("Record", rows, options.IdentityMode, pathIdentities.Count, hydrated.Count, hydrateWatch.Elapsed);
            return new QueryResult(hydrated, pathPreview);
        }

        Stopwatch syntheticBuildWatch = Stopwatch.StartNew();
        LibraDexConditionEndCondition condition = BuildCondition(rows, out string preview);
        syntheticBuildWatch.Stop();
        Stopwatch syntheticRetrieveWatch = Stopwatch.StartNew();
        IReadOnlyList<ulong> ids = catalog
            .IndexSet(GroupName)
            .GetIdentities<ulong>(
                condition,
                IdentityResultOrdering.PlanNatural,
                IdentityDeduplication.Distinct,
                skip: skip,
                take: take);
        syntheticRetrieveWatch.Stop();
        LogConditionBuilderQueryUse("Record", rows, FileSearchIdentityMode.SyntheticUInt64, skip, take, ids.Count, syntheticBuildWatch.Elapsed, syntheticRetrieveWatch.Elapsed, TimeSpan.Zero, "native");
        Stopwatch syntheticHydrateWatch = Stopwatch.StartNew();
        Dictionary<ulong, FileRecord> byId = state.Files.ToDictionary(static file => file.Id);
        List<FileRecord> results = new(ids.Count);
        for (int i = 0; i < ids.Count; i++)
        {
            if (byId.TryGetValue(ids[i], out FileRecord? record))
            {
                results.Add(record);
            }
        }

        IReadOnlyList<FileRecord> syntheticHydrated = Hydrate(results, options);
        syntheticHydrateWatch.Stop();
        LogQueryPresentationUse("Record", rows, options.IdentityMode, ids.Count, syntheticHydrated.Count, syntheticHydrateWatch.Elapsed);
        return new QueryResult(syntheticHydrated, preview);
    }

    /// <summary>
    /// Executes a query as raw identity discovery without hydrating file records.<br/>
    /// This lets the workbench measure condition-builder retrieval separately from sidecar or file-system presentation costs.<br/>
    /// </summary>
    public IdentityQueryResult ExecuteIdentities(IReadOnlyList<QueryRow> rows, int skip, int length, FileSearchReadOptions options)
    {
        EnsureCatalogIdentityMode(options.IdentityMode);
        int? take = NormalizeTake(length);
        if (rows.Count == 0)
        {
            IEnumerable<FileRecord> files = state.Files;
            if (skip > 0)
            {
                files = files.Skip(skip);
            }

            if (take is int sidecarTake)
            {
                files = files.Take(sidecarTake);
            }

            List<FileSearchIdentityRow> sidecarRows = files
                .Select(file => new FileSearchIdentityRow { Identity = options.IdentityMode == FileSearchIdentityMode.PathString ? file.Path : file.Id.ToString(CultureInfo.InvariantCulture) })
                .ToList();
            return new IdentityQueryResult(sidecarRows, "// no condition rows; showing sidecar identity values");
        }

        if (options.IdentityMode == FileSearchIdentityMode.PathString)
        {
            string pathPreview;
            IReadOnlyList<byte[]> identities;
            if (HasPathIdentityVarKeyRows(rows))
            {
                identities = ExecutePathIdentityQuery(rows, skip, take, out pathPreview);
            }
            else
            {
                Stopwatch buildWatch = Stopwatch.StartNew();
                LibraDexConditionEndCondition pathCondition = BuildCondition(rows, out pathPreview);
                buildWatch.Stop();
                Stopwatch retrieveWatch = Stopwatch.StartNew();
                identities = catalog
                    .IndexSet(GroupName)
                    .GetIdentities<byte[]>(pathCondition, IdentityResultOrdering.PlanNatural, IdentityDeduplication.Distinct, skip: skip, take: take);
                retrieveWatch.Stop();
                LogConditionBuilderQueryUse("Identity", rows, FileSearchIdentityMode.PathString, skip, take, identities.Count, buildWatch.Elapsed, retrieveWatch.Elapsed, TimeSpan.Zero, "SV8");
            }

            Stopwatch formatWatch = Stopwatch.StartNew();
            IReadOnlyList<FileSearchIdentityRow> formatted = FormatIdentityRows(identities, options.IdentityDisplayMode);
            formatWatch.Stop();
            LogQueryPresentationUse("Identity", rows, options.IdentityMode, identities.Count, formatted.Count, formatWatch.Elapsed);
            return new IdentityQueryResult(formatted, pathPreview);
        }

        Stopwatch syntheticBuildWatch = Stopwatch.StartNew();
        LibraDexConditionEndCondition condition = BuildCondition(rows, out string preview);
        syntheticBuildWatch.Stop();
        Stopwatch syntheticRetrieveWatch = Stopwatch.StartNew();
        IReadOnlyList<ulong> ids = catalog
            .IndexSet(GroupName)
            .GetIdentities<ulong>(condition, IdentityResultOrdering.PlanNatural, IdentityDeduplication.Distinct, skip: skip, take: take);
        syntheticRetrieveWatch.Stop();
        LogConditionBuilderQueryUse("Identity", rows, FileSearchIdentityMode.SyntheticUInt64, skip, take, ids.Count, syntheticBuildWatch.Elapsed, syntheticRetrieveWatch.Elapsed, TimeSpan.Zero, "native");
        Stopwatch syntheticFormatWatch = Stopwatch.StartNew();
        IReadOnlyList<FileSearchIdentityRow> syntheticFormatted = FormatIdentityRows(ids, options.IdentityDisplayMode);
        syntheticFormatWatch.Stop();
        LogQueryPresentationUse("Identity", rows, options.IdentityMode, ids.Count, syntheticFormatted.Count, syntheticFormatWatch.Elapsed);
        return new IdentityQueryResult(syntheticFormatted, preview);
    }

    /// <summary>
    /// Executes a single-condition query as key/identity tuples from that condition's target index.<br/>
    /// Multi-condition queries intentionally use identity-only output because intersections do not have one natural key column.<br/>
    /// </summary>
    public TupleQueryResult ExecuteSelectedIndexTuples(IReadOnlyList<QueryRow> rows, int skip, int length, FileSearchReadOptions options)
    {
        EnsureCatalogIdentityMode(options.IdentityMode);
        if (rows.Count != 1)
        {
            IdentityQueryResult identities = ExecuteIdentities(rows, skip, length, options);
            return new TupleQueryResult(
                identities.Rows.Select(static row => new FileSearchTupleRow { Key = string.Empty, Identity = row.Identity }).ToList(),
                identities.ConditionPreview);
        }

        if (options.IdentityMode == FileSearchIdentityMode.PathString &&
            IsPathIdentityVarKeyField(rows[0].Field))
        {
            return ExecutePathIdentityVarKeyTupleQuery(rows[0], skip, NormalizeTake(length), options);
        }

        Stopwatch buildWatch = Stopwatch.StartNew();
        LibraDexConditionEndCondition condition = BuildCondition(rows, out string preview);
        buildWatch.Stop();
        IIndex index = GetRuntimeIndex(rows[0].Field, options.IdentityMode);
        Stopwatch retrieveWatch = Stopwatch.StartNew();
        IReadOnlyList<LibraDexRuntimeTuple> tuples = catalog
            .IndexSet(GroupName)
            .GetTuples(index, condition, skip, NormalizeTake(length));
        retrieveWatch.Stop();
        Stopwatch formatWatch = Stopwatch.StartNew();
        IReadOnlyList<FileSearchTupleRow> formatted = FormatTupleRows(tuples, rows[0].Field, options.KeyDisplayMode, options.IdentityDisplayMode);
        formatWatch.Stop();
        LogConditionBuilderQueryUse("Tuple", rows, options.IdentityMode, skip, NormalizeTake(length), tuples.Count, buildWatch.Elapsed, retrieveWatch.Elapsed, formatWatch.Elapsed, index.Name);
        return new TupleQueryResult(formatted, preview);
    }

    public IReadOnlyList<FileRecord> Enumerate(SearchField field, int skip, int length, FileSearchReadOptions options)
    {
        EnsureCatalogIdentityMode(options.IdentityMode);
        int? take = NormalizeTake(length);
        IEnumerable<FileRecord> query = field switch
        {
            SearchField.FileName => state.Files.OrderBy(static file => file.FileName, StringComparer.OrdinalIgnoreCase),
            SearchField.Extension => state.Files.OrderBy(static file => file.Extension, StringComparer.OrdinalIgnoreCase).ThenBy(static file => file.FileName, StringComparer.OrdinalIgnoreCase),
            SearchField.Size => state.Files.OrderBy(static file => file.Size).ThenBy(static file => file.FileName, StringComparer.OrdinalIgnoreCase),
            SearchField.CreatedUtc => state.Files.OrderBy(static file => file.CreatedUtc).ThenBy(static file => file.FileName, StringComparer.OrdinalIgnoreCase),
            SearchField.ModifiedUtc => state.Files.OrderBy(static file => file.ModifiedUtc).ThenBy(static file => file.FileName, StringComparer.OrdinalIgnoreCase),
            SearchField.AccessedUtc => state.Files.OrderBy(static file => file.AccessedUtc).ThenBy(static file => file.FileName, StringComparer.OrdinalIgnoreCase),
            SearchField.Attributes => state.Files.OrderBy(static file => file.Attributes).ThenBy(static file => file.FileName, StringComparer.OrdinalIgnoreCase),
            _ => state.Files
        };

        if (skip > 0)
        {
            query = query.Skip(skip);
        }

        if (take is int limit)
        {
            query = query.Take(limit);
        }

        return Hydrate(query, options);
    }

    /// <summary>
    /// Enumerates key/identity tuples directly from the selected LibraDex index.<br/>
    /// This path does not hydrate file records and is intended for physical index performance tests in the workbench.<br/>
    /// </summary>
    public IReadOnlyList<FileSearchTupleRow> EnumerateTuples(SearchField field, int skip, int length, FileSearchReadOptions options)
    {
        EnsureCatalogIdentityMode(options.IdentityMode);
        if (options.IdentityMode == FileSearchIdentityMode.PathString &&
            (field == SearchField.FileName || field == SearchField.Extension))
        {
            return EnumeratePathIdentityVarKeyTuples(field, skip, length, options);
        }

        LibraDexConditionEndCondition condition = BuildAllCondition(field, options.IdentityMode);
        IReadOnlyList<LibraDexRuntimeTuple> tuples = catalog
            .IndexSet(GroupName)
            .GetTuples(GetRuntimeIndex(field, options.IdentityMode), condition, skip, NormalizeTake(length));
        return FormatTupleRows(tuples, field, options.KeyDisplayMode, options.IdentityDisplayMode);
    }

    /// <summary>
    /// Enumerates selected-index tuples for stress telemetry while separating index tuple read from display materialization.<br/>
    /// This is intentionally internal to the workbench stress path because the public workbench API returns already formatted rows.<br/>
    /// </summary>
    private StressEnumerationProbe EnumerateTuplesForStress(SearchField field, int skip, int length, FileSearchReadOptions options)
    {
        EnsureCatalogIdentityMode(options.IdentityMode);
        if (options.IdentityMode == FileSearchIdentityMode.PathString &&
            (field == SearchField.FileName || field == SearchField.Extension))
        {
            return EnumeratePathIdentityVarKeyTuplesForStress(field, skip, length, options);
        }

        Stopwatch buildReadWatch = Stopwatch.StartNew();
        LibraDexConditionEndCondition condition = BuildAllCondition(field, options.IdentityMode);
        IReadOnlyList<LibraDexRuntimeTuple> tuples = catalog
            .IndexSet(GroupName)
            .GetTuples(GetRuntimeIndex(field, options.IdentityMode), condition, skip, NormalizeTake(length));
        buildReadWatch.Stop();

        Stopwatch formatWatch = Stopwatch.StartNew();
        IReadOnlyList<FileSearchTupleRow> rows = FormatTupleRows(tuples, field, options.KeyDisplayMode, options.IdentityDisplayMode);
        formatWatch.Stop();
        return new StressEnumerationProbe(rows, buildReadWatch.Elapsed, formatWatch.Elapsed);
    }

    public CatalogMetrics GetMetrics(SearchField selectedField)
    {
        return new CatalogMetrics(
            DisplayName,
            fileBacked,
            state.Roots.Count,
            state.Files.Count,
            selectedField,
            CountDistinct(selectedField));
    }

    /// <summary>
    /// Runs a loaded-catalog stress certification pass against the currently indexed FileSearch data.<br/>
    /// The pass exercises tuple enumeration, identity discovery, selected-index tuple projection, record hydration, single-row query operators, and a small AND-query matrix without rebuilding the catalog.<br/>
    /// Correctness mismatches are failures; slow operations are warnings so the workbench can establish practical baselines before hard performance gates are introduced.<br/>
    /// </summary>
    /// <param name="baseOptions">The UI-selected read options used as the starting point for identity mode and display behavior.<br/></param>
    /// <param name="progress">Receives short status updates for the workbench status bar.<br/></param>
    /// <param name="cancellationToken">Allows the UI to stop the stress pass before the next case begins.<br/></param>
    /// <returns>A compact stress result containing check counts, warnings, failures, elapsed time, and summary text.</returns>
    public FileSearchStressResult RunStressCertification(FileSearchReadOptions baseOptions, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        EnsureCatalogIdentityMode(baseOptions.IdentityMode);
        Stopwatch total = Stopwatch.StartNew();
        FileSearchStressResult result = new();
        if (state.Files.Count == 0)
        {
            result.Summary = "No indexed files to stress.";
            FileSearchLog.Info($"Stress certification skipped. Reason=NoIndexedFiles; Catalog='{DisplayName}'.");
            return result;
        }

        FileSearchLog.Info($"Stress certification started. Catalog='{DisplayName}'; Identity={baseOptions.IdentityMode}; Files={state.Files.Count:n0}; Roots={state.Roots.Count:n0}; StringFolded={state.IndexStringFolded}; StringSortKey={state.IndexStringSortKey}; StringReversed={state.IndexStringReversed}; Memory={GetCatalogMemoryDiagnosticsText()}.");
        RouteVisitedContextSet.ResetDiagnostics();
        FileSearchReadOptions tupleOptions = CloneReadOptions(baseOptions, FileSearchDisplayFieldMode.SelectedFields, FileSearchRowHydrationMode.SidecarCache, FileSearchValueDisplayMode.Text, FileSearchValueDisplayMode.Text);
        FileSearchReadOptions identityOptions = CloneReadOptions(baseOptions, FileSearchDisplayFieldMode.IdentityOnly, FileSearchRowHydrationMode.SidecarCache, FileSearchValueDisplayMode.Text, FileSearchValueDisplayMode.Text);
        FileSearchReadOptions recordOptions = CloneReadOptions(baseOptions, FileSearchDisplayFieldMode.AllKnownFields, FileSearchRowHydrationMode.SidecarCache, FileSearchValueDisplayMode.Text, FileSearchValueDisplayMode.Text);

        RunStressEnumerations(tupleOptions, progress, cancellationToken, result);
        IReadOnlyList<FileSearchStressQueryCase> queryCases = CreateStressQueryCases();
        RunStressStrategyChecks(queryCases, baseOptions.IdentityMode, result);
        RunStressQueries(queryCases, identityOptions, tupleOptions, recordOptions, progress, cancellationToken, result);

        total.Stop();
        result.Elapsed = total.Elapsed;
        string stateText = result.Failures == 0 ? "PASS" : "FAIL";
        result.Summary = $"{stateText}: enum={result.EnumerationChecks:n0}; query={result.QueryChecks:n0}; strategy={result.StrategyChecks:n0}; warnings={result.Warnings:n0}; failures={result.Failures:n0}; elapsed={FormatDuration(result.Elapsed)}";
        FileSearchLog.Info($"Stress phase totals: {FormatStressPhaseTotals(result.PhaseStats)}.");
        FileSearchLog.Info($"Stress route diagnostics: {RouteVisitedContextSet.CreateDiagnosticsSummary()}.");
        FileSearchLog.Info($"Stress certification completed. {result.Summary}; Catalog='{DisplayName}'; Memory={GetCatalogMemoryDiagnosticsText()}.");
        return result;
    }

    /// <summary>
    /// Exercises direct tuple enumeration for every FileSearch field over full, first-page, middle-page, near-end, and beyond-end ranges.<br/>
    /// This validates range-reader skip/take behavior separately from query condition formation and UI grid materialization.<br/>
    /// </summary>
    private void RunStressEnumerations(FileSearchReadOptions options, IProgress<string>? progress, CancellationToken cancellationToken, FileSearchStressResult result)
    {
        int count = state.Files.Count;
        FileSearchStressEnumerationCase[] cases =
        [
            new(SearchField.FileName, 0, 0, "full"),
            new(SearchField.FileName, 0, Math.Min(100, count), "first-page"),
            new(SearchField.FileName, Math.Min(100, count), Math.Min(100, count), "offset-page"),
            new(SearchField.FileName, Math.Max(0, count - 25), 50, "near-end"),
            new(SearchField.FileName, count + 10, 50, "beyond-end")
        ];

        SearchField[] fields = Enum.GetValues<SearchField>();
        for (int f = 0; f < fields.Length; f++)
        {
            for (int c = 0; c < cases.Length; c++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileSearchStressEnumerationCase shape = cases[c];
                FileSearchStressEnumerationCase test = new(fields[f], shape.Skip, shape.Length, shape.Label);
                progress?.Report($"Stress enumerate {test.Field} {test.Label}...");
                Stopwatch watch = Stopwatch.StartNew();
                try
                {
                    StressRawProbe rawProbe = CountRawTuplesForStress(test.Field, test.Skip, test.Length, options);
                    result.PhaseStats.AddRawDiscovery(rawProbe.Elapsed);
                    StressEnumerationProbe probe = EnumerateTuplesForStress(test.Field, test.Skip, test.Length, options);
                    IReadOnlyList<FileSearchTupleRow> rows = probe.Rows;
                    watch.Stop();
                    result.PhaseStats.AddIndexTupleRead(probe.IndexTupleReadElapsed);
                    result.PhaseStats.AddKeyIdentityMaterialization(probe.FormatElapsed);
                    TimeSpan gridElapsed = SimulateGridTraversal(rows);
                    result.PhaseStats.AddGridSimulation(gridElapsed);
                    int expected = ExpectedPagedCount(count, test.Skip, test.Length);
                    bool pass = rows.Count == expected;
                    if (!pass)
                    {
                        result.Failures++;
                    }

                    if (watch.Elapsed > TimeSpan.FromSeconds(2))
                    {
                        result.Warnings++;
                        FileSearchLog.Info($"WARNING: Stress enumeration slow. Field={test.Field}; Case={test.Label}; Elapsed={FormatDuration(watch.Elapsed)}; Rows={rows.Count:n0}; Expected={expected:n0}.");
                    }

                    result.EnumerationChecks++;
                    FileSearchLog.Info($"Stress enumeration {(pass ? "PASS" : "FAIL")}. Field={test.Field}; Case={test.Label}; Skip={test.Skip:n0}; Length={DescribeStressLength(test.Length)}; Rows={rows.Count:n0}; Expected={expected:n0}; RawPath={rawProbe.Path}; RawRows={rawProbe.Rows:n0}; RawKeyBytes={rawProbe.KeyBytes:n0}; RawIdentityBytes={rawProbe.IdentityBytes:n0}; RawDiscovery={FormatDuration(rawProbe.Elapsed)}; IndexTupleRead={FormatDuration(probe.IndexTupleReadElapsed)}; KeyIdentityMaterialize={FormatDuration(probe.FormatElapsed)}; GridSim={FormatDuration(gridElapsed)}; Elapsed={FormatDuration(watch.Elapsed + gridElapsed)}; Identity={options.IdentityMode}; KeyDisplay={options.KeyDisplayMode}; IdentityDisplay={options.IdentityDisplayMode}.");
                }
                catch (Exception ex)
                {
                    watch.Stop();
                    result.EnumerationChecks++;
                    result.Failures++;
                    FileSearchLog.Error($"Stress enumeration FAIL. Field={test.Field}; Case={test.Label}; Skip={test.Skip:n0}; Length={DescribeStressLength(test.Length)}; Elapsed={FormatDuration(watch.Elapsed)}.", ex);
                }
            }
        }
    }

    /// <summary>
    /// Executes generated query cases through identity, tuple, and record read paths and checks each result count against the sidecar predicate model.<br/>
    /// The normal query methods perform the actual LibraDex condition-builder or direct-reader work, so existing query index-use telemetry is emitted for every case.<br/>
    /// </summary>
    private void RunStressQueries(IReadOnlyList<FileSearchStressQueryCase> cases, FileSearchReadOptions identityOptions, FileSearchReadOptions tupleOptions, FileSearchReadOptions recordOptions, IProgress<string>? progress, CancellationToken cancellationToken, FileSearchStressResult result)
    {
        for (int i = 0; i < cases.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileSearchStressQueryCase test = cases[i];
            progress?.Report($"Stress query {i + 1:n0}/{cases.Count:n0}: {test.Label}");
            int expected;
            Stopwatch expectedWatch = Stopwatch.StartNew();
            try
            {
                expected = CountExpectedMatches(test.Rows);
                expectedWatch.Stop();
                if (expectedWatch.Elapsed > TimeSpan.FromMilliseconds(250))
                {
                    FileSearchLog.Info($"WARNING: Stress expected-count slow. Label={test.Label}; Elapsed={FormatDuration(expectedWatch.Elapsed)}; Rows={expected:n0}; Conditions={FormatStressRows(test.Rows)}.");
                }

                FileSearchLog.Info($"Stress expected-count completed. Label={test.Label}; Rows={expected:n0}; Elapsed={FormatDuration(expectedWatch.Elapsed)}; Conditions={FormatStressRows(test.Rows)}.");
            }
            catch (Exception ex)
            {
                expectedWatch.Stop();
                result.QueryChecks++;
                result.Failures++;
                FileSearchLog.Error($"Stress query FAIL. Mode=expected-count; Label={test.Label}; Elapsed={FormatDuration(expectedWatch.Elapsed)}; Conditions={FormatStressRows(test.Rows)}.", ex);
                continue;
            }

            RunStressRawQuery(test, expected, expectedWatch.Elapsed, identityOptions, result);
            RunStressIdentityQuery(test, expected, expectedWatch.Elapsed, identityOptions, result);
            if (test.Rows.Length == 1)
            {
                RunStressTupleQuery(test, expected, expectedWatch.Elapsed, tupleOptions, result);
            }

            RunStressRecordQuery(test, expected, expectedWatch.Elapsed, recordOptions, result);
        }
    }

    /// <summary>
    /// Runs one single-condition stress query through the raw PathString cursor path without formatting rows or materializing identities.<br/>
    /// Multi-condition cases are skipped here because a fair byte-native intersection needs a dedicated raw identity set structure rather than temporary `byte[]` lists.<br/>
    /// </summary>
    /// <param name="test">The stress query case.<br/></param>
    /// <param name="expected">The expected matching row count from the sidecar model.<br/></param>
    /// <param name="expectedElapsed">The elapsed expected-count duration.<br/></param>
    /// <param name="options">The read options carrying the active identity mode.<br/></param>
    /// <param name="result">The stress result receiving phase counters, warnings, and failures.<br/></param>
    private void RunStressRawQuery(FileSearchStressQueryCase test, int expected, TimeSpan expectedElapsed, FileSearchReadOptions options, FileSearchStressResult result)
    {
        if (options.IdentityMode != FileSearchIdentityMode.PathString || test.Rows.Length != 1)
        {
            return;
        }

        Stopwatch watch = Stopwatch.StartNew();
        try
        {
            StressRawProbe probe = CountRawQueryForStress(test.Rows[0]);
            watch.Stop();
            result.PhaseStats.AddRawDiscovery(probe.Elapsed);
            LogStressQueryResult("raw", test, checked((int)Math.Min(probe.Rows, int.MaxValue)), expected, expectedElapsed, watch.Elapsed, options, result);
            FileSearchLog.Info($"Stress query phase. Mode=raw; Label={test.Label}; Path={probe.Path}; Rows={probe.Rows:n0}; RawKeyBytes={probe.KeyBytes:n0}; RawIdentityBytes={probe.IdentityBytes:n0}; RawDiscovery={FormatDuration(probe.Elapsed)}.");
        }
        catch (Exception ex)
        {
            watch.Stop();
            result.QueryChecks++;
            result.Failures++;
            FileSearchLog.Error($"Stress query FAIL. Mode=raw; Label={test.Label}; Expected={expected:n0}; Elapsed={FormatDuration(watch.Elapsed)}.", ex);
        }
    }

    /// <summary>
    /// Validates that stress query cases select the expected maintained projection path before row-count correctness is measured.<br/>
    /// A certification run should fail when a query silently falls back to an exact scan despite the requested folded or sort-key projection being maintained.<br/>
    /// </summary>
    /// <param name="cases">The generated stress cases that will be executed.</param>
    /// <param name="result">The mutable stress result receiving strategy counts and failures.</param>
    private void RunStressStrategyChecks(IReadOnlyList<FileSearchStressQueryCase> cases, FileSearchIdentityMode identityMode, FileSearchStressResult result)
    {
        for (int i = 0; i < cases.Count; i++)
        {
            FileSearchStressQueryCase test = cases[i];
            for (int r = 0; r < test.Rows.Length; r++)
            {
                QueryRow row = test.Rows[r];
                if (!TryGetExpectedStringProjection(row, out string? expectedProjection))
                {
                    continue;
                }

                result.StrategyChecks++;
                string actualProjection;
                string indexName;
                string filterText;
                if (identityMode == FileSearchIdentityMode.PathString)
                {
                    VarKeyVarIdentityQueryPlan plan = CreatePathIdentityVarKeyQueryPlan(row);
                    actualProjection = plan.Projection;
                    indexName = plan.Index.Name;
                    filterText = $"{plan.FilterKeys}; FilterMode={plan.FilterMode}";
                }
                else
                {
                    actualProjection = DescribeSyntheticStringProjection(row);
                    indexName = GetLogicalIndexName(row.Field, identityMode);
                    filterText = "condition-builder";
                }

                bool pass = string.Equals(actualProjection, expectedProjection, StringComparison.Ordinal);
                if (!pass)
                {
                    result.Failures++;
                }

                FileSearchLog.Info($"Stress strategy {(pass ? "PASS" : "FAIL")}. Label={test.Label}; Identity={identityMode}; Field={row.Field}; Operator={row.Operator}; IgnoreCase={row.IgnoreCase}; Projection={actualProjection}; Expected={expectedProjection}; Index={indexName}; Filter={filterText}.");
            }
        }
    }

    /// <summary>
    /// Returns the projection path that a PathString string query is expected to use when the related subindex is maintained.<br/>
    /// Queries without a maintained projection expectation are ignored so fallback behavior remains valid when the developer intentionally did not index that projection.<br/>
    /// </summary>
    /// <param name="row">The stress query row.</param>
    /// <param name="expectedProjection">Receives the expected projection token used by query-index telemetry.</param>
    /// <returns><see langword="true"/> when this row has a projection expectation.</returns>
    private bool TryGetExpectedStringProjection(QueryRow row, out string? expectedProjection)
    {
        expectedProjection = null;
        if (!row.IgnoreCase || !IsPathIdentityVarKeyField(row.Field))
        {
            return false;
        }

        if ((row.Operator == SearchOperator.EqualTo || row.Operator == SearchOperator.StartsWith) &&
            state.IndexStringFolded)
        {
            expectedProjection = "folded";
            return true;
        }

        if (row.Operator == SearchOperator.Contains &&
            state.IndexStringFolded)
        {
            expectedProjection = "folded-scan";
            return true;
        }

        if ((row.Operator == SearchOperator.GreaterOrEqual ||
                row.Operator == SearchOperator.LessOrEqual ||
                row.Operator == SearchOperator.Between) &&
            state.IndexStringSortKey)
        {
            expectedProjection = "sortkey";
            return true;
        }

        return false;
    }

    /// <summary>
    /// Describes the maintained string projection expected for a SyntheticUInt64 string condition row.<br/>
    /// This mirrors the condition-builder string projection selection used by the logical `LibraDexStringScalar8Index` facade, giving stress summaries a counted strategy check for scalar-identity string indexes.<br/>
    /// </summary>
    /// <param name="row">The stress query row.</param>
    /// <returns>The projection token used by stress telemetry.</returns>
    private string DescribeSyntheticStringProjection(QueryRow row)
    {
        if (!row.IgnoreCase || !IsPathIdentityVarKeyField(row.Field))
        {
            return "exact";
        }

        if ((row.Operator == SearchOperator.EqualTo || row.Operator == SearchOperator.StartsWith) && state.IndexStringFolded)
        {
            return "folded";
        }

        if (row.Operator == SearchOperator.Contains && state.IndexStringFolded)
        {
            return "folded-scan";
        }

        if ((row.Operator == SearchOperator.GreaterOrEqual || row.Operator == SearchOperator.LessOrEqual || row.Operator == SearchOperator.Between) && state.IndexStringSortKey)
        {
            return "sortkey";
        }

        return "exact-scan";
    }

    /// <summary>
    /// Runs one stress query through raw identity discovery and records correctness, timing, and warning metadata.<br/>
    /// Identity discovery avoids row hydration so it is the closest workbench measure of condition execution cost.<br/>
    /// </summary>
    private void RunStressIdentityQuery(FileSearchStressQueryCase test, int expected, TimeSpan expectedElapsed, FileSearchReadOptions options, FileSearchStressResult result)
    {
        Stopwatch watch = Stopwatch.StartNew();
        try
        {
            IdentityQueryResult rows = ExecuteIdentities(test.Rows, skip: 0, length: 0, options);
            watch.Stop();
            result.PhaseStats.AddIdentityMaterialization(watch.Elapsed);
            TimeSpan gridElapsed = SimulateGridTraversal(rows.Rows);
            result.PhaseStats.AddGridSimulation(gridElapsed);
            LogStressQueryResult("identity", test, rows.Rows.Count, expected, expectedElapsed, watch.Elapsed, options, result);
            FileSearchLog.Info($"Stress query phase. Mode=identity; Label={test.Label}; IdentityMaterialize={FormatDuration(watch.Elapsed)}; GridSim={FormatDuration(gridElapsed)}; Rows={rows.Rows.Count:n0}.");
        }
        catch (Exception ex)
        {
            watch.Stop();
            result.QueryChecks++;
            result.Failures++;
            FileSearchLog.Error($"Stress query FAIL. Mode=identity; Label={test.Label}; Expected={expected:n0}; Elapsed={FormatDuration(watch.Elapsed)}.", ex);
        }
    }

    /// <summary>
    /// Runs one single-condition stress query through selected-index tuple projection.<br/>
    /// This verifies that the selected physical index can project key/identity rows consistently with identity discovery.<br/>
    /// </summary>
    private void RunStressTupleQuery(FileSearchStressQueryCase test, int expected, TimeSpan expectedElapsed, FileSearchReadOptions options, FileSearchStressResult result)
    {
        Stopwatch watch = Stopwatch.StartNew();
        try
        {
            TupleQueryResult rows = ExecuteSelectedIndexTuples(test.Rows, skip: 0, length: 0, options);
            watch.Stop();
            result.PhaseStats.AddKeyIdentityMaterialization(watch.Elapsed);
            TimeSpan gridElapsed = SimulateGridTraversal(rows.Rows);
            result.PhaseStats.AddGridSimulation(gridElapsed);
            LogStressQueryResult("tuple", test, rows.Rows.Count, expected, expectedElapsed, watch.Elapsed, options, result);
            FileSearchLog.Info($"Stress query phase. Mode=tuple; Label={test.Label}; KeyIdentityMaterialize={FormatDuration(watch.Elapsed)}; GridSim={FormatDuration(gridElapsed)}; Rows={rows.Rows.Count:n0}.");
        }
        catch (Exception ex)
        {
            watch.Stop();
            result.QueryChecks++;
            result.Failures++;
            FileSearchLog.Error($"Stress query FAIL. Mode=tuple; Label={test.Label}; Expected={expected:n0}; Elapsed={FormatDuration(watch.Elapsed)}.", ex);
        }
    }

    /// <summary>
    /// Runs one stress query through record hydration using sidecar cache mode.<br/>
    /// The result checks that identity discovery can be joined back to the currently loaded record set without exercising file-system refresh cost.<br/>
    /// </summary>
    private void RunStressRecordQuery(FileSearchStressQueryCase test, int expected, TimeSpan expectedElapsed, FileSearchReadOptions options, FileSearchStressResult result)
    {
        Stopwatch watch = Stopwatch.StartNew();
        try
        {
            QueryResult rows = Execute(test.Rows, skip: 0, length: 0, options);
            watch.Stop();
            result.PhaseStats.AddRecordMaterialization(watch.Elapsed);
            TimeSpan gridElapsed = SimulateGridTraversal(rows.Files);
            result.PhaseStats.AddGridSimulation(gridElapsed);
            LogStressQueryResult("record", test, rows.Files.Count, expected, expectedElapsed, watch.Elapsed, options, result);
            FileSearchLog.Info($"Stress query phase. Mode=record; Label={test.Label}; RecordMaterialize={FormatDuration(watch.Elapsed)}; GridSim={FormatDuration(gridElapsed)}; Rows={rows.Files.Count:n0}.");
        }
        catch (Exception ex)
        {
            watch.Stop();
            result.QueryChecks++;
            result.Failures++;
            FileSearchLog.Error($"Stress query FAIL. Mode=record; Label={test.Label}; Expected={expected:n0}; Elapsed={FormatDuration(watch.Elapsed)}.", ex);
        }
    }

    /// <summary>
    /// Logs one stress query result and applies warning-only performance gates.<br/>
    /// The warning gate is deliberately soft so the workbench can collect baseline timings before enforcing hard public-readiness thresholds.<br/>
    /// </summary>
    private void LogStressQueryResult(string mode, FileSearchStressQueryCase test, int actual, int expected, TimeSpan expectedElapsed, TimeSpan elapsed, FileSearchReadOptions options, FileSearchStressResult result)
    {
        bool pass = actual == expected;
        if (!pass)
        {
            result.Failures++;
        }

        if (elapsed > TimeSpan.FromSeconds(2))
        {
            result.Warnings++;
            FileSearchLog.Info($"WARNING: Stress query slow. Mode={mode}; Label={test.Label}; ExpectedCount={FormatDuration(expectedElapsed)}; Execute={FormatDuration(elapsed)}; Rows={actual:n0}; Expected={expected:n0}; Path={DescribeStressQueryPath(test.Rows, options)}.");
        }

        result.QueryChecks++;
        FileSearchLog.Info($"Stress query {(pass ? "PASS" : "FAIL")}. Mode={mode}; Label={test.Label}; Rows={actual:n0}; Expected={expected:n0}; ExpectedCount={FormatDuration(expectedElapsed)}; Execute={FormatDuration(elapsed)}; Path={DescribeStressQueryPath(test.Rows, options)}; Conditions={FormatStressRows(test.Rows)}.");
    }

    /// <summary>
    /// Formats aggregate stress phase timings so retrieval, materialization, hydration, and grid-like traversal costs stay visually distinct.<br/>
    /// Counts represent measured cases, not result rows, because individual result counts are already logged per case.<br/>
    /// </summary>
    private static string FormatStressPhaseTotals(FileSearchStressPhaseStats stats)
    {
        return
            $"RawDiscovery={FormatStressPhase(stats.RawDiscoveryCount, stats.RawDiscoveryElapsed)}; " +
            $"IndexTupleRead={FormatStressPhase(stats.IndexTupleReadCount, stats.IndexTupleReadElapsed)}; " +
            $"IdentityMaterialize={FormatStressPhase(stats.IdentityMaterializationCount, stats.IdentityMaterializationElapsed)}; " +
            $"KeyIdentityMaterialize={FormatStressPhase(stats.KeyIdentityMaterializationCount, stats.KeyIdentityMaterializationElapsed)}; " +
            $"RecordMaterialize={FormatStressPhase(stats.RecordMaterializationCount, stats.RecordMaterializationElapsed)}; " +
            $"GridSim={FormatStressPhase(stats.GridSimulationCount, stats.GridSimulationElapsed)}";
    }

    /// <summary>
    /// Formats one aggregate stress phase with total and average duration.<br/>
    /// The average helps compare phase buckets with different case counts without forcing external log parsing.<br/>
    /// </summary>
    private static string FormatStressPhase(int count, TimeSpan elapsed)
    {
        TimeSpan average = count == 0
            ? TimeSpan.Zero
            : TimeSpan.FromTicks(elapsed.Ticks / count);
        return $"{FormatDuration(elapsed)} over {count:n0} cases, avg {FormatDuration(average)}";
    }

    /// <summary>
    /// Simulates the cheap portion of grid population by touching identity-row display values after query materialization.<br/>
    /// This deliberately avoids WinForms controls so the runner and UI stress paths can share the same low-allocation signal.<br/>
    /// </summary>
    private static TimeSpan SimulateGridTraversal(IReadOnlyList<FileSearchIdentityRow> rows)
    {
        Stopwatch watch = Stopwatch.StartNew();
        long checksum = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            checksum += rows[i].Identity.Length;
        }

        watch.Stop();
        GC.KeepAlive(checksum);
        return watch.Elapsed;
    }

    /// <summary>
    /// Simulates the cheap portion of tuple-grid population by touching key and identity display values after query materialization.<br/>
    /// The method measures presentation traversal separately from LibraDex tuple discovery and text formatting.<br/>
    /// </summary>
    private static TimeSpan SimulateGridTraversal(IReadOnlyList<FileSearchTupleRow> rows)
    {
        Stopwatch watch = Stopwatch.StartNew();
        long checksum = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            checksum += rows[i].Key.Length + rows[i].Identity.Length;
        }

        watch.Stop();
        GC.KeepAlive(checksum);
        return watch.Elapsed;
    }

    /// <summary>
    /// Simulates the cheap portion of file-grid population by touching the record fields visible in the workbench grid.<br/>
    /// Full WinForms binding is measured by the interactive UI; this runner-safe signal isolates row traversal from control painting and handle work.<br/>
    /// </summary>
    private static TimeSpan SimulateGridTraversal(IReadOnlyList<FileRecord> rows)
    {
        Stopwatch watch = Stopwatch.StartNew();
        long checksum = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            FileRecord row = rows[i];
            checksum += row.Path.Length + row.FileName.Length + row.Extension.Length;
            checksum += unchecked((long)row.Id + (long)row.Size + row.CreatedUtc.Ticks + row.ModifiedUtc.Ticks + row.AccessedUtc.Ticks + row.Attributes);
        }

        watch.Stop();
        GC.KeepAlive(checksum);
        return watch.Elapsed;
    }

    /// <summary>
    /// Creates stress query cases from real indexed records so every condition value is known to be representable by the current catalog.<br/>
    /// String fields get exact, prefix, contains, ordered-range, and case-sensitivity permutations; scalar fields get exact and range permutations.<br/>
    /// </summary>
    private IReadOnlyList<FileSearchStressQueryCase> CreateStressQueryCases()
    {
        List<FileSearchStressQueryCase> cases = new(64);
        FileRecord first = state.Files[0];
        FileRecord middle = state.Files[state.Files.Count / 2];
        FileRecord last = state.Files[^1];
        AddStringCases(cases, SearchField.FileName, middle.FileName);
        AddStringCases(cases, SearchField.Extension, middle.Extension);
        AddScalarCases(cases, SearchField.Size, first.Size, middle.Size, last.Size);
        AddDateCases(cases, SearchField.CreatedUtc, first.CreatedUtc, middle.CreatedUtc, last.CreatedUtc);
        AddDateCases(cases, SearchField.ModifiedUtc, first.ModifiedUtc, middle.ModifiedUtc, last.ModifiedUtc);
        AddDateCases(cases, SearchField.AccessedUtc, first.AccessedUtc, middle.AccessedUtc, last.AccessedUtc);
        AddScalarCases(cases, SearchField.Attributes, first.Attributes, middle.Attributes, last.Attributes);
        cases.Add(new FileSearchStressQueryCase(
            "mixed:fileName-prefix+size-ge",
            [
                CreateStringQuery(SearchField.FileName, SearchOperator.StartsWith, CreatePrefix(middle.FileName), string.Empty, ignoreCase: true),
                CreateScalarQuery(SearchField.Size, SearchOperator.GreaterOrEqual, middle.Size, 0)
            ]));
        cases.Add(new FileSearchStressQueryCase(
            "mixed:extension-eq+modified-ge",
            [
                CreateStringQuery(SearchField.Extension, SearchOperator.EqualTo, middle.Extension, string.Empty, ignoreCase: true),
                CreateDateQuery(SearchField.ModifiedUtc, SearchOperator.GreaterOrEqual, middle.ModifiedUtc, default)
            ]));
        cases.Add(new FileSearchStressQueryCase(
            "mixed:size-range+attributes-eq",
            [
                CreateScalarQuery(SearchField.Size, SearchOperator.Between, Math.Min(first.Size, last.Size), Math.Max(first.Size, last.Size)),
                CreateScalarQuery(SearchField.Attributes, SearchOperator.EqualTo, middle.Attributes, 0)
            ]));
        return cases;
    }

    /// <summary>
    /// Adds string query permutations for one real field value.<br/>
    /// Empty values are still useful because they stress empty-prefix and exact-empty behavior for extension-like fields.<br/>
    /// </summary>
    private static void AddStringCases(List<FileSearchStressQueryCase> cases, SearchField field, string value)
    {
        string prefix = CreatePrefix(value);
        string contains = CreateContainsNeedle(value);
        string upper = value.ToUpperInvariant();
        string lower = value.ToLowerInvariant();
        cases.Add(new FileSearchStressQueryCase($"{field}:eq:ignore", [CreateStringQuery(field, SearchOperator.EqualTo, upper, string.Empty, ignoreCase: true)]));
        cases.Add(new FileSearchStressQueryCase($"{field}:eq:case", [CreateStringQuery(field, SearchOperator.EqualTo, value, string.Empty, ignoreCase: false)]));
        cases.Add(new FileSearchStressQueryCase($"{field}:starts:ignore", [CreateStringQuery(field, SearchOperator.StartsWith, prefix.ToUpperInvariant(), string.Empty, ignoreCase: true)]));
        cases.Add(new FileSearchStressQueryCase($"{field}:starts:case", [CreateStringQuery(field, SearchOperator.StartsWith, prefix, string.Empty, ignoreCase: false)]));
        cases.Add(new FileSearchStressQueryCase($"{field}:contains:ignore", [CreateStringQuery(field, SearchOperator.Contains, contains.ToUpperInvariant(), string.Empty, ignoreCase: true)]));
        cases.Add(new FileSearchStressQueryCase($"{field}:ge:ignore", [CreateStringQuery(field, SearchOperator.GreaterOrEqual, lower, string.Empty, ignoreCase: true)]));
        cases.Add(new FileSearchStressQueryCase($"{field}:le:ignore", [CreateStringQuery(field, SearchOperator.LessOrEqual, upper, string.Empty, ignoreCase: true)]));
        cases.Add(new FileSearchStressQueryCase($"{field}:between:ignore", [CreateStringQuery(field, SearchOperator.Between, lower, upper, ignoreCase: true)]));
    }

    /// <summary>
    /// Adds unsigned scalar query permutations for one field using real sampled values.<br/>
    /// The values are sorted only for the Between case so the exact sampled keys remain visible in the log labels.<br/>
    /// </summary>
    private static void AddScalarCases(List<FileSearchStressQueryCase> cases, SearchField field, ulong first, ulong middle, ulong last)
    {
        ulong low = Math.Min(first, Math.Min(middle, last));
        ulong high = Math.Max(first, Math.Max(middle, last));
        cases.Add(new FileSearchStressQueryCase($"{field}:eq", [CreateScalarQuery(field, SearchOperator.EqualTo, middle, 0)]));
        cases.Add(new FileSearchStressQueryCase($"{field}:ge", [CreateScalarQuery(field, SearchOperator.GreaterOrEqual, middle, 0)]));
        cases.Add(new FileSearchStressQueryCase($"{field}:le", [CreateScalarQuery(field, SearchOperator.LessOrEqual, middle, 0)]));
        cases.Add(new FileSearchStressQueryCase($"{field}:between", [CreateScalarQuery(field, SearchOperator.Between, low, high)]));
    }

    /// <summary>
    /// Adds unsigned scalar query permutations for UInt32-backed fields while using the same query-row text format as UInt64 fields.<br/>
    /// PathString identity mode promotes attributes through UInt64 keys, so decimal text keeps both shapes covered by the same workbench row type.<br/>
    /// </summary>
    private static void AddScalarCases(List<FileSearchStressQueryCase> cases, SearchField field, uint first, uint middle, uint last)
        => AddScalarCases(cases, field, (ulong)first, middle, last);

    /// <summary>
    /// Adds UTC date query permutations using real sampled timestamps.<br/>
    /// Date strings use round-trip ISO text because the normal FileSearch query parser accepts that format from the UI grid.<br/>
    /// </summary>
    private static void AddDateCases(List<FileSearchStressQueryCase> cases, SearchField field, DateTime first, DateTime middle, DateTime last)
    {
        DateTime low = Min(first, Min(middle, last));
        DateTime high = Max(first, Max(middle, last));
        cases.Add(new FileSearchStressQueryCase($"{field}:eq", [CreateDateQuery(field, SearchOperator.EqualTo, middle, default)]));
        cases.Add(new FileSearchStressQueryCase($"{field}:ge", [CreateDateQuery(field, SearchOperator.GreaterOrEqual, middle, default)]));
        cases.Add(new FileSearchStressQueryCase($"{field}:le", [CreateDateQuery(field, SearchOperator.LessOrEqual, middle, default)]));
        cases.Add(new FileSearchStressQueryCase($"{field}:between", [CreateDateQuery(field, SearchOperator.Between, low, high)]));
    }

    /// <summary>
    /// Counts expected matches from the sidecar records using the same operator semantics as the FileSearch workbench query rows.<br/>
    /// This gives stress certification an independent correctness model while remaining tied to the loaded catalog snapshot rather than the live file system.<br/>
    /// </summary>
    private int CountExpectedMatches(IReadOnlyList<QueryRow> rows)
    {
        int count = 0;
        for (int i = 0; i < state.Files.Count; i++)
        {
            bool match = true;
            for (int r = 0; r < rows.Count; r++)
            {
                if (!MatchesExpected(state.Files[i], rows[r]))
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Evaluates one workbench query row against a sidecar record.<br/>
    /// The method mirrors FileSearch query semantics by using ordinal string equality/prefix/contains and invariant-culture range comparisons for strings.<br/>
    /// </summary>
    private static bool MatchesExpected(FileRecord record, QueryRow row)
    {
        return row.Field switch
        {
            SearchField.FileName => MatchesString(record.FileName, row),
            SearchField.Extension => MatchesString(record.Extension, row),
            SearchField.Size => MatchesUInt64(record.Size, row),
            SearchField.CreatedUtc => MatchesUInt64(ToUtcTicks(record.CreatedUtc), ToTicksQuery(row)),
            SearchField.ModifiedUtc => MatchesUInt64(ToUtcTicks(record.ModifiedUtc), ToTicksQuery(row)),
            SearchField.AccessedUtc => MatchesUInt64(ToUtcTicks(record.AccessedUtc), ToTicksQuery(row)),
            SearchField.Attributes => MatchesUInt64(record.Attributes, row),
            _ => false
        };
    }

    /// <summary>
    /// Evaluates one string predicate using the same comparison split as the direct `VV` path.<br/>
    /// Prefix and contains operations use ordinal comparisons; ordered operations use invariant-culture string ordering.<br/>
    /// </summary>
    private static bool MatchesString(string value, QueryRow row)
    {
        string value1 = row.Value1 ?? string.Empty;
        string value2 = row.Value2 ?? string.Empty;
        StringComparison comparison = row.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return row.Operator switch
        {
            SearchOperator.EqualTo => string.Equals(value, value1, comparison),
            SearchOperator.StartsWith => value.StartsWith(value1, comparison),
            SearchOperator.Contains => value.Contains(value1, comparison),
            SearchOperator.GreaterOrEqual => string.Compare(value, value1, row.IgnoreCase, CultureInfo.InvariantCulture) >= 0,
            SearchOperator.LessOrEqual => string.Compare(value, value1, row.IgnoreCase, CultureInfo.InvariantCulture) <= 0,
            SearchOperator.Between => string.Compare(value, value1, row.IgnoreCase, CultureInfo.InvariantCulture) >= 0 &&
                string.Compare(value, value2, row.IgnoreCase, CultureInfo.InvariantCulture) <= 0,
            _ => false
        };
    }

    /// <summary>
    /// Evaluates one unsigned scalar predicate against a pre-parsed query row.<br/>
    /// The same helper covers UInt64 keys and UInt32 attributes because both are compared as non-negative integer values in the workbench model.<br/>
    /// </summary>
    private static bool MatchesUInt64(ulong value, QueryRow row)
    {
        ulong value1 = ulong.Parse(row.Value1, CultureInfo.InvariantCulture);
        ulong value2 = row.Operator == SearchOperator.Between ? ulong.Parse(row.Value2, CultureInfo.InvariantCulture) : 0;
        return MatchesUInt64(value, row.Operator, value1, value2);
    }

    /// <summary>
    /// Evaluates one unsigned scalar predicate against already converted values.<br/>
    /// This avoids reparsing date ticks after the date row has been normalized into a scalar row.<br/>
    /// </summary>
    private static bool MatchesUInt64(ulong value, QueryRow row, ulong value1, ulong value2)
        => MatchesUInt64(value, row.Operator, value1, value2);

    /// <summary>
    /// Evaluates one unsigned scalar predicate by operator.<br/>
    /// Unsupported text-only operators return false so generated stress cases cannot accidentally pass through an invalid scalar condition.<br/>
    /// </summary>
    private static bool MatchesUInt64(ulong value, SearchOperator queryOperator, ulong value1, ulong value2)
    {
        return queryOperator switch
        {
            SearchOperator.EqualTo => value == value1,
            SearchOperator.GreaterOrEqual => value >= value1,
            SearchOperator.LessOrEqual => value <= value1,
            SearchOperator.Between => value >= value1 && value <= value2,
            _ => false
        };
    }

    /// <summary>
    /// Converts a date query row to an equivalent UTC-tick scalar row for expected-count evaluation.<br/>
    /// This mirrors the physical DateTime index representation used by FileSearch for the current scalar indexes.<br/>
    /// </summary>
    private static QueryRow ToTicksQuery(QueryRow row)
    {
        return new QueryRow
        {
            Field = row.Field,
            Operator = row.Operator,
            Value1 = ToUtcTicks(ParseUtcDateTime(row.Value1, row.Field.ToString())).ToString(CultureInfo.InvariantCulture),
            Value2 = row.Operator == SearchOperator.Between ? ToUtcTicks(ParseUtcDateTime(row.Value2, row.Field.ToString())).ToString(CultureInfo.InvariantCulture) : string.Empty,
            IgnoreCase = row.IgnoreCase
        };
    }

    /// <summary>
    /// Converts a scalar FileSearch query row to the inclusive UInt64 range used by raw `SV8` path-identity readers.<br/>
    /// The conversion mirrors condition-builder scalar semantics but returns bounds only, keeping stress raw-query probes free of runtime tuple or identity materialization.<br/>
    /// </summary>
    /// <param name="row">The scalar query row to convert.<br/></param>
    /// <param name="lowerKey">Receives the inclusive lower UInt64 key.<br/></param>
    /// <param name="upperKey">Receives the inclusive upper UInt64 key.<br/></param>
    /// <returns><see langword="true"/> when the operator maps to one contiguous scalar range.<br/></returns>
    private static bool TryCreateScalarQueryRange(QueryRow row, out ulong lowerKey, out ulong upperKey)
    {
        lowerKey = 0;
        upperKey = ulong.MaxValue;
        if (!TryParseScalarQueryValue(row, row.Value1, out ulong value1))
        {
            return false;
        }

        ulong value2 = 0;
        if (row.Operator == SearchOperator.Between && !TryParseScalarQueryValue(row, row.Value2, out value2))
        {
            return false;
        }

        switch (row.Operator)
        {
            case SearchOperator.EqualTo:
                lowerKey = value1;
                upperKey = value1;
                return true;
            case SearchOperator.GreaterOrEqual:
                lowerKey = value1;
                upperKey = ulong.MaxValue;
                return true;
            case SearchOperator.LessOrEqual:
                lowerKey = 0;
                upperKey = value1;
                return true;
            case SearchOperator.Between:
                lowerKey = Math.Min(value1, value2);
                upperKey = Math.Max(value1, value2);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Parses one scalar query-row value into the UInt64 key representation stored by path-identity `SV8` indexes.<br/>
    /// Date fields are converted to UTC ticks so raw cursor probes use the same byte-sortable key lane as normal FileSearch indexing.<br/>
    /// </summary>
    /// <param name="row">The source query row.<br/></param>
    /// <param name="value">The text value to parse.<br/></param>
    /// <param name="key">Receives the UInt64 key value.<br/></param>
    /// <returns><see langword="true"/> when the value could be parsed for the row's field.<br/></returns>
    private static bool TryParseScalarQueryValue(QueryRow row, string value, out ulong key)
    {
        key = 0;
        try
        {
            if (row.Field == SearchField.CreatedUtc || row.Field == SearchField.ModifiedUtc || row.Field == SearchField.AccessedUtc)
            {
                key = ToUtcTicks(ParseUtcDateTime(value, row.Field.ToString()));
                return true;
            }

            if (row.Field == SearchField.Size || row.Field == SearchField.Attributes)
            {
                return ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out key);
            }
        }
        catch (FormatException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }

        return false;
    }

    /// <summary>
    /// Clones read options while forcing the display and hydration knobs needed by a stress sub-pass.<br/>
    /// Keeping this explicit makes it obvious which cost surface each stress mode measures.<br/>
    /// </summary>
    private static FileSearchReadOptions CloneReadOptions(FileSearchReadOptions source, FileSearchDisplayFieldMode display, FileSearchRowHydrationMode hydration, FileSearchValueDisplayMode keyDisplay, FileSearchValueDisplayMode identityDisplay)
    {
        return new FileSearchReadOptions
        {
            IdentityMode = source.IdentityMode,
            RowHydrationMode = hydration,
            DisplayFieldMode = display,
            KeyDisplayMode = keyDisplay,
            IdentityDisplayMode = identityDisplay
        };
    }

    /// <summary>
    /// Creates a string query row with invariant text defaults.<br/>
    /// Stress generation uses this to keep row creation compact while still going through the same FileSearch row shape as the UI grid.<br/>
    /// </summary>
    private static QueryRow CreateStringQuery(SearchField field, SearchOperator queryOperator, string value1, string value2, bool ignoreCase)
        => new() { Field = field, Operator = queryOperator, Value1 = value1, Value2 = value2, IgnoreCase = ignoreCase };

    /// <summary>
    /// Creates an unsigned scalar query row using invariant decimal text.<br/>
    /// The workbench condition builder parses the same text values that users type into the query grid.<br/>
    /// </summary>
    private static QueryRow CreateScalarQuery(SearchField field, SearchOperator queryOperator, ulong value1, ulong value2)
        => new() { Field = field, Operator = queryOperator, Value1 = value1.ToString(CultureInfo.InvariantCulture), Value2 = value2.ToString(CultureInfo.InvariantCulture) };

    /// <summary>
    /// Creates a UTC date query row using round-trip ISO strings.<br/>
    /// This keeps stress cases readable in the log while preserving exact tick values through parsing.<br/>
    /// </summary>
    private static QueryRow CreateDateQuery(SearchField field, SearchOperator queryOperator, DateTime value1, DateTime value2)
        => new() { Field = field, Operator = queryOperator, Value1 = FormatStressDateValue(value1), Value2 = queryOperator == SearchOperator.Between ? FormatStressDateValue(value2) : string.Empty };

    /// <summary>
    /// Computes the expected count after FileSearch skip/length semantics.<br/>
    /// A length of zero means no limit, matching the workbench enumeration controls.<br/>
    /// </summary>
    private static int ExpectedPagedCount(int total, int skip, int length)
    {
        int available = Math.Max(0, total - Math.Max(0, skip));
        return length <= 0 ? available : Math.Min(available, length);
    }

    /// <summary>
    /// Formats a stress enumeration length value using the same zero-means-all convention as the UI.<br/>
    /// The catalog owns a local helper so stress logging does not depend on form-only formatting code.<br/>
    /// </summary>
    private static string DescribeStressLength(int length)
        => length <= 0 ? "all" : length.ToString("n0", CultureInfo.InvariantCulture);

    /// <summary>
    /// Formats a stress date value as raw round-trip text for the normal workbench query parser.<br/>
    /// This intentionally differs from <see cref="FormatDate"/>, which creates condition-preview source text rather than grid/input text.<br/>
    /// </summary>
    private static string FormatStressDateValue(DateTime value)
        => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    /// Creates a non-empty prefix when possible while preserving empty-string coverage.<br/>
    /// This lets extension-like values such as empty or `(none)` still participate in prefix tests.<br/>
    /// </summary>
    private static string CreatePrefix(string value)
        => value.Length <= 2 ? value : value[..2];

    /// <summary>
    /// Creates a contains needle from the middle of a real value.<br/>
    /// Empty source values remain empty so the stress pass explicitly covers broad contains behavior.<br/>
    /// </summary>
    private static string CreateContainsNeedle(string value)
    {
        if (value.Length <= 2)
        {
            return value;
        }

        int start = Math.Max(0, (value.Length / 2) - 1);
        return value.Substring(start, Math.Min(2, value.Length - start));
    }

    /// <summary>
    /// Formats query rows into a compact single-line log fragment.<br/>
    /// The stress log uses this instead of the condition preview so multi-row AND cases remain readable in tail views.<br/>
    /// </summary>
    private static string FormatStressRows(IReadOnlyList<QueryRow> rows)
    {
        StringBuilder builder = new();
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(" AND ");
            }

            QueryRow row = rows[i];
            builder.Append(row.Field).Append('.').Append(row.Operator).Append('(').Append(Escape(row.Value1));
            if (row.Operator == SearchOperator.Between)
            {
                builder.Append(", ").Append(Escape(row.Value2));
            }

            builder.Append("; ignoreCase=").Append(row.IgnoreCase).Append(')');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Returns the earlier of two DateTime values by UTC tick ordering.<br/>
    /// The helper keeps date stress case construction branch-light and culture-free.<br/>
    /// </summary>
    private static DateTime Min(DateTime left, DateTime right)
        => ToUtcTicks(left) <= ToUtcTicks(right) ? left : right;

    /// <summary>
    /// Returns the later of two DateTime values by UTC tick ordering.<br/>
    /// The helper keeps date stress case construction branch-light and culture-free.<br/>
    /// </summary>
    private static DateTime Max(DateTime left, DateTime right)
        => ToUtcTicks(left) >= ToUtcTicks(right) ? left : right;

    /// <summary>
    /// Formats the active catalog's memory-backed DataKernel arena diagnostics for logs and runner output.<br/>
    /// File-backed catalogs report zero arena values because their committed bytes do not live in the managed volatile arena.<br/>
    /// </summary>
    /// <returns>A compact invariant diagnostic string describing arena pages, live bytes, free bytes, and high-water offset.</returns>
    public string GetCatalogMemoryDiagnosticsText()
    {
        System.Reflection.MethodInfo? method = typeof(Catalog).GetMethod("GetMemoryDiagnostics", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (method is null)
        {
            return "unavailable";
        }

        object? diagnostics = method.Invoke(catalog, null);
        if (diagnostics is null)
        {
            return "unavailable";
        }

        Type diagnosticsType = diagnostics.GetType();
        int pageCount = GetDiagnosticInt(diagnosticsType, diagnostics, "PageCount");
        long arenaBytes = GetDiagnosticLong(diagnosticsType, diagnostics, "ArenaBytes");
        int liveRangeCount = GetDiagnosticInt(diagnosticsType, diagnostics, "LiveRangeCount");
        long liveBytes = GetDiagnosticLong(diagnosticsType, diagnostics, "LiveBytes");
        int freeExtentCount = GetDiagnosticInt(diagnosticsType, diagnostics, "FreeExtentCount");
        long freeBytes = GetDiagnosticLong(diagnosticsType, diagnostics, "FreeBytes");
        long endOffset = GetDiagnosticLong(diagnosticsType, diagnostics, "EndOffset");
        return $"ArenaPages={pageCount:n0}; ArenaBytes={FormatBytes(arenaBytes)}; LiveRanges={liveRangeCount:n0}; LiveBytes={FormatBytes(liveBytes)}; FreeExtents={freeExtentCount:n0}; FreeBytes={FormatBytes(freeBytes)}; EndOffset={FormatBytes(endOffset)}";
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        catalog.Dispose();
        disposed = true;
    }

    private void OpenIndexes()
    {
        CatalogIdentityGroupIndexes group = catalog.CreateIndexSet(GroupName);
        if (state.IdentityMode == FileSearchIdentityMode.PathString)
        {
            const int maxStringKeyBytes = 1024;
            const int maxPathIdentityBytes = 1024;
            fileNamePathIdentityIndex = group["fileName"].VarKeyVarIdentityKeys(maxStringKeyBytes, maxPathIdentityBytes).CreateOrOpen();
            extensionPathIdentityIndex = group["extension"].VarKeyVarIdentityKeys(maxStringKeyBytes, maxPathIdentityBytes).CreateOrOpen();
            fileNameFoldedPathIdentityIndex = group["fileName#folded"].VarKeyVarIdentityKeys(maxStringKeyBytes, maxPathIdentityBytes).CreateOrOpen();
            extensionFoldedPathIdentityIndex = group["extension#folded"].VarKeyVarIdentityKeys(maxStringKeyBytes, maxPathIdentityBytes).CreateOrOpen();
            fileNameSortKeyPathIdentityIndex = group["fileName#sortkey"].VarKeyVarIdentityKeys(maxStringKeyBytes, maxPathIdentityBytes).CreateOrOpen();
            extensionSortKeyPathIdentityIndex = group["extension#sortkey"].VarKeyVarIdentityKeys(maxStringKeyBytes, maxPathIdentityBytes).CreateOrOpen();
            fileNameReversedPathIdentityIndex = group["fileName#exact-rev"].VarKeyVarIdentityKeys(maxStringKeyBytes, maxPathIdentityBytes).CreateOrOpen();
            extensionReversedPathIdentityIndex = group["extension#exact-rev"].VarKeyVarIdentityKeys(maxStringKeyBytes, maxPathIdentityBytes).CreateOrOpen();
            fileNameFoldedReversedPathIdentityIndex = group["fileName#folded-rev"].VarKeyVarIdentityKeys(maxStringKeyBytes, maxPathIdentityBytes).CreateOrOpen();
            extensionFoldedReversedPathIdentityIndex = group["extension#folded-rev"].VarKeyVarIdentityKeys(maxStringKeyBytes, maxPathIdentityBytes).CreateOrOpen();
            sizePathIdentityIndex = group["size"].UInt64VarIdentityKeys(maxPathIdentityBytes).CreateOrOpen();
            createdUtcPathIdentityIndex = group["createdUtc"].UInt64VarIdentityKeys(maxPathIdentityBytes).CreateOrOpen();
            modifiedUtcPathIdentityIndex = group["modifiedUtc"].UInt64VarIdentityKeys(maxPathIdentityBytes).CreateOrOpen();
            accessedUtcPathIdentityIndex = group["accessedUtc"].UInt64VarIdentityKeys(maxPathIdentityBytes).CreateOrOpen();
            attributesPathIdentityIndex = group["attributes"].UInt64VarIdentityKeys(maxPathIdentityBytes).CreateOrOpen();
            return;
        }

        StringKeys stringKeys = CreateStringKeys();
        LibraDexProjectionDirectionSet directions = state.IndexStringReversed
            ? LibraDexProjectionDirectionSet.ForwardAndReversed
            : LibraDexProjectionDirectionSet.Forward;
        fileNameIndex = group["fileName"].StringKeys().CreateOrOpen(stringKeys, directions);
        extensionIndex = group["extension"].StringKeys().CreateOrOpen(stringKeys, directions);
        sizeIndex = group["size"].CreateOrOpen<ulong, ulong>();
        createdUtcIndex = group["createdUtc"].CreateOrOpen<ulong, ulong>();
        modifiedUtcIndex = group["modifiedUtc"].CreateOrOpen<ulong, ulong>();
        accessedUtcIndex = group["accessedUtc"].CreateOrOpen<ulong, ulong>();
        attributesIndex = group["attributes"].CreateOrOpen<uint, ulong>();
    }

    /// <summary>
    /// Creates the SyntheticUInt64 string-key projection profile selected by the workbench reindex options.<br/>
    /// Exact text is always retained; folded and sort-key projections are optional so insert cost and query behavior can be measured independently.<br/>
    /// </summary>
    /// <returns>The string projection flags used when opening or creating SyntheticUInt64 string indexes.</returns>
    private StringKeys CreateStringKeys()
    {
        if (state.IndexStringFolded)
        {
            return state.IndexStringSortKey
                ? StringKeys.ExactFoldedAndSortKey
                : StringKeys.ExactAndFolded;
        }

        if (state.IndexStringSortKey)
        {
            return StringKeys.ExactAndSortKey;
        }

        return StringKeys.Exact;
    }

    private void RecreateCatalogStorage()
    {
        catalog.Dispose();
        if (fileBacked && catalogPath is not null)
        {
            if (File.Exists(catalogPath))
            {
                File.Delete(catalogPath);
            }

            catalog = Catalog.Create(catalogPath);
            return;
        }

        catalog = Catalog.CreateMemory();
    }

    private void InsertRecordMajor(
        List<FileRecord> collected,
        FileSearchIndexFailureSummary failures,
        CatalogIdentityGroupBatchManager? batch,
        int batchCommitFileCount,
        bool indexExtension,
        IProgress<string>? progress,
        FileSearchReindexTelemetry telemetry,
        Stopwatch insertWatch,
        CancellationToken cancellationToken,
        ref long indexed,
        ref long failedFiles,
        ref long failedFieldAttempts)
    {
        long batchEntries = 0;
        long lastBatchEntries = 0;
        TimeSpan lastBatchElapsed = TimeSpan.Zero;
        for (int i = 0; i < collected.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileRecord record = collected[i];
            FileIndexResult result = InsertRecord(record, failures, telemetry, indexExtension);
            if (result.CoreIndexed)
            {
                state.Files.Add(record);
                indexed++;
                batchEntries++;
                CommitBatchIfNeeded(batch, batchCommitFileCount, batchEntries, ref lastBatchEntries, ref lastBatchElapsed, progress, telemetry, insertWatch, "record-major files", cancellationToken);
            }
            else
            {
                failedFiles++;
            }

            failedFieldAttempts += result.FailedFieldCount;
            if (((i + 1) & 1023) == 0)
            {
                progress?.Report($"Record-major inserted {i + 1:n0}/{collected.Count:n0}, indexed {indexed:n0}, failed files {failedFiles:n0}, failed field attempts {failedFieldAttempts:n0}, skipped disabled fields {failures.SkippedFieldCount:n0}");
            }
        }
    }

    private void InsertIndexMajor(
        List<FileRecord> collected,
        FileSearchIndexFailureSummary failures,
        CatalogIdentityGroupBatchManager? batch,
        int batchCommitFileCount,
        bool indexExtension,
        IProgress<string>? progress,
        FileSearchReindexTelemetry telemetry,
        Stopwatch insertWatch,
        CancellationToken cancellationToken,
        ref long indexed,
        ref long failedFiles,
        ref long failedFieldAttempts)
    {
        bool[] coreIndexed = new bool[collected.Count];
        long batchEntries = 0;
        long lastBatchEntries = 0;
        TimeSpan lastBatchElapsed = TimeSpan.Zero;
        InsertCoreIndexPass("fileName", collected, coreIndexed, failures, batch, batchCommitFileCount, progress, telemetry, insertWatch, cancellationToken, ref batchEntries, ref lastBatchEntries, ref lastBatchElapsed, ref failedFiles, ref failedFieldAttempts, allowAllRecords: true);

        for (int i = 0; i < collected.Count; i++)
        {
            if (coreIndexed[i])
            {
                state.Files.Add(collected[i]);
                indexed++;
            }
        }

        if (indexExtension)
        {
            InsertSecondaryIndexPass("extension", collected, coreIndexed, failures, batch, batchCommitFileCount, progress, telemetry, insertWatch, cancellationToken, ref batchEntries, ref lastBatchEntries, ref lastBatchElapsed, ref failedFieldAttempts);
        }

        InsertSecondaryIndexPass("size", collected, coreIndexed, failures, batch, batchCommitFileCount, progress, telemetry, insertWatch, cancellationToken, ref batchEntries, ref lastBatchEntries, ref lastBatchElapsed, ref failedFieldAttempts);
        InsertSecondaryIndexPass("createdUtc", collected, coreIndexed, failures, batch, batchCommitFileCount, progress, telemetry, insertWatch, cancellationToken, ref batchEntries, ref lastBatchEntries, ref lastBatchElapsed, ref failedFieldAttempts);
        InsertSecondaryIndexPass("modifiedUtc", collected, coreIndexed, failures, batch, batchCommitFileCount, progress, telemetry, insertWatch, cancellationToken, ref batchEntries, ref lastBatchEntries, ref lastBatchElapsed, ref failedFieldAttempts);
        InsertSecondaryIndexPass("accessedUtc", collected, coreIndexed, failures, batch, batchCommitFileCount, progress, telemetry, insertWatch, cancellationToken, ref batchEntries, ref lastBatchEntries, ref lastBatchElapsed, ref failedFieldAttempts);
        InsertSecondaryIndexPass("attributes", collected, coreIndexed, failures, batch, batchCommitFileCount, progress, telemetry, insertWatch, cancellationToken, ref batchEntries, ref lastBatchEntries, ref lastBatchElapsed, ref failedFieldAttempts);
        progress?.Report($"Index-major inserted {indexed:n0}/{collected.Count:n0}, failed files {failedFiles:n0}, failed field attempts {failedFieldAttempts:n0}, skipped disabled fields {failures.SkippedFieldCount:n0}");
    }

    /// <summary>
    /// Inserts collected records into path-identity indexes using each UTF-8 file path as the raw variable identity.<br/>
    /// Filename and extension use raw-byte `VV`; scalar properties use UInt64-key `SV8`.<br/>
    /// </summary>
    private void InsertPathIdentityRecordMajor(
        List<FileRecord> collected,
        FileSearchIndexFailureSummary failures,
        CatalogIdentityGroupBatchManager? batch,
        int batchCommitFileCount,
        FileSearchReindexOptions options,
        IProgress<string>? progress,
        FileSearchReindexTelemetry telemetry,
        Stopwatch insertWatch,
        CancellationToken cancellationToken,
        ref long indexed,
        ref long failedFiles,
        ref long failedFieldAttempts)
    {
        long batchEntries = 0;
        long lastBatchEntries = 0;
        TimeSpan lastBatchElapsed = TimeSpan.Zero;
        for (int i = 0; i < collected.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileRecord record = collected[i];
            FileIndexResult result = InsertPathIdentityRecord(record, failures, telemetry, batch is not null, options);
            if (result.CoreIndexed)
            {
                state.Files.Add(record);
                indexed++;
                batchEntries++;
                CommitBatchIfNeeded(batch, batchCommitFileCount, batchEntries, ref lastBatchEntries, ref lastBatchElapsed, progress, telemetry, insertWatch, "path-identity record-major files", cancellationToken);
            }
            else
            {
                failedFiles++;
            }

            failedFieldAttempts += result.FailedFieldCount;
            if (((i + 1) & 1023) == 0)
            {
                progress?.Report($"Path-identity record-major inserted {i + 1:n0}/{collected.Count:n0}, indexed {indexed:n0}, failed files {failedFiles:n0}, failed field attempts {failedFieldAttempts:n0}, skipped disabled fields {failures.SkippedFieldCount:n0}");
            }
        }
    }

    /// <summary>
    /// Inserts collected records into path-identity indexes one field pass at a time using each UTF-8 file path as the raw variable identity.<br/>
    /// Filename and extension use raw-byte `VV`; scalar properties use UInt64-key `SV8`.<br/>
    /// </summary>
    private void InsertPathIdentityIndexMajor(
        List<FileRecord> collected,
        FileSearchIndexFailureSummary failures,
        CatalogIdentityGroupBatchManager? batch,
        int batchCommitFileCount,
        FileSearchReindexOptions options,
        IProgress<string>? progress,
        FileSearchReindexTelemetry telemetry,
        Stopwatch insertWatch,
        CancellationToken cancellationToken,
        ref long indexed,
        ref long failedFiles,
        ref long failedFieldAttempts)
    {
        bool[] coreIndexed = new bool[collected.Count];
        Array.Fill(coreIndexed, true);
        long batchEntries = 0;
        long lastBatchEntries = 0;
        TimeSpan lastBatchElapsed = TimeSpan.Zero;
        InsertPathIdentityIndexPass("fileName", collected, coreIndexed, failures, batch, batchCommitFileCount, progress, telemetry, insertWatch, cancellationToken, ref batchEntries, ref lastBatchEntries, ref lastBatchElapsed, ref failedFieldAttempts);
        InsertPathIdentityIndexPass("extension", collected, coreIndexed, failures, batch, batchCommitFileCount, progress, telemetry, insertWatch, cancellationToken, ref batchEntries, ref lastBatchEntries, ref lastBatchElapsed, ref failedFieldAttempts);
        if (options.IndexStringFolded)
        {
            InsertPathIdentityIndexPass("fileName#folded", collected, coreIndexed, failures, batch, batchCommitFileCount, progress, telemetry, insertWatch, cancellationToken, ref batchEntries, ref lastBatchEntries, ref lastBatchElapsed, ref failedFieldAttempts);
            InsertPathIdentityIndexPass("extension#folded", collected, coreIndexed, failures, batch, batchCommitFileCount, progress, telemetry, insertWatch, cancellationToken, ref batchEntries, ref lastBatchEntries, ref lastBatchElapsed, ref failedFieldAttempts);
        }

        if (options.IndexStringSortKey)
        {
            InsertPathIdentityIndexPass("fileName#sortkey", collected, coreIndexed, failures, batch, batchCommitFileCount, progress, telemetry, insertWatch, cancellationToken, ref batchEntries, ref lastBatchEntries, ref lastBatchElapsed, ref failedFieldAttempts);
            InsertPathIdentityIndexPass("extension#sortkey", collected, coreIndexed, failures, batch, batchCommitFileCount, progress, telemetry, insertWatch, cancellationToken, ref batchEntries, ref lastBatchEntries, ref lastBatchElapsed, ref failedFieldAttempts);
        }

        if (options.IndexStringReversed)
        {
            InsertPathIdentityIndexPass("fileName#exact-rev", collected, coreIndexed, failures, batch, batchCommitFileCount, progress, telemetry, insertWatch, cancellationToken, ref batchEntries, ref lastBatchEntries, ref lastBatchElapsed, ref failedFieldAttempts);
            InsertPathIdentityIndexPass("extension#exact-rev", collected, coreIndexed, failures, batch, batchCommitFileCount, progress, telemetry, insertWatch, cancellationToken, ref batchEntries, ref lastBatchEntries, ref lastBatchElapsed, ref failedFieldAttempts);
            if (options.IndexStringFolded)
            {
                InsertPathIdentityIndexPass("fileName#folded-rev", collected, coreIndexed, failures, batch, batchCommitFileCount, progress, telemetry, insertWatch, cancellationToken, ref batchEntries, ref lastBatchEntries, ref lastBatchElapsed, ref failedFieldAttempts);
                InsertPathIdentityIndexPass("extension#folded-rev", collected, coreIndexed, failures, batch, batchCommitFileCount, progress, telemetry, insertWatch, cancellationToken, ref batchEntries, ref lastBatchEntries, ref lastBatchElapsed, ref failedFieldAttempts);
            }
        }

        InsertPathIdentityIndexPass("size", collected, coreIndexed, failures, batch, batchCommitFileCount, progress, telemetry, insertWatch, cancellationToken, ref batchEntries, ref lastBatchEntries, ref lastBatchElapsed, ref failedFieldAttempts);
        InsertPathIdentityIndexPass("createdUtc", collected, coreIndexed, failures, batch, batchCommitFileCount, progress, telemetry, insertWatch, cancellationToken, ref batchEntries, ref lastBatchEntries, ref lastBatchElapsed, ref failedFieldAttempts);
        InsertPathIdentityIndexPass("modifiedUtc", collected, coreIndexed, failures, batch, batchCommitFileCount, progress, telemetry, insertWatch, cancellationToken, ref batchEntries, ref lastBatchEntries, ref lastBatchElapsed, ref failedFieldAttempts);
        InsertPathIdentityIndexPass("accessedUtc", collected, coreIndexed, failures, batch, batchCommitFileCount, progress, telemetry, insertWatch, cancellationToken, ref batchEntries, ref lastBatchEntries, ref lastBatchElapsed, ref failedFieldAttempts);
        InsertPathIdentityIndexPass("attributes", collected, coreIndexed, failures, batch, batchCommitFileCount, progress, telemetry, insertWatch, cancellationToken, ref batchEntries, ref lastBatchEntries, ref lastBatchElapsed, ref failedFieldAttempts);
        for (int i = 0; i < collected.Count; i++)
        {
            state.Files.Add(collected[i]);
            indexed++;
        }

        progress?.Report($"Path-identity index-major inserted {indexed:n0}/{collected.Count:n0}, failed files {failedFiles:n0}, failed field attempts {failedFieldAttempts:n0}, skipped disabled fields {failures.SkippedFieldCount:n0}");
    }

    private void InsertCoreIndexPass(
        string fieldName,
        List<FileRecord> records,
        bool[] targetIndexed,
        FileSearchIndexFailureSummary failures,
        CatalogIdentityGroupBatchManager? batch,
        int batchCommitFileCount,
        IProgress<string>? progress,
        FileSearchReindexTelemetry telemetry,
        Stopwatch insertWatch,
        CancellationToken cancellationToken,
        ref long batchEntries,
        ref long lastBatchEntries,
        ref TimeSpan lastBatchElapsed,
        ref long failedFiles,
        ref long failedFieldAttempts,
        bool allowAllRecords = false,
        bool[]? sourceIndexed = null)
    {
        for (int i = 0; i < records.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!allowAllRecords && (sourceIndexed is null || !sourceIndexed[i]))
            {
                continue;
            }

            int failedFields = 0;
            bool inserted = TryInsertIndexMajorField(fieldName, records[i], failures, telemetry, ref failedFields, allowDisable: false);
            targetIndexed[i] = inserted;
            if (!inserted)
            {
                failedFiles++;
            }

            failedFieldAttempts += failedFields;
            batchEntries++;
            CommitBatchIfNeeded(batch, batchCommitFileCount, batchEntries, ref lastBatchEntries, ref lastBatchElapsed, progress, telemetry, insertWatch, $"index-major {fieldName} entries", cancellationToken);
            if (((i + 1) & 4095) == 0)
            {
                progress?.Report($"Index-major {fieldName}: {i + 1:n0}/{records.Count:n0}, failed files {failedFiles:n0}, failed field attempts {failedFieldAttempts:n0}");
            }
        }

        CommitIndexPassIfNeeded(batch, batchCommitFileCount, batchEntries, ref lastBatchEntries, ref lastBatchElapsed, progress, telemetry, insertWatch, $"index-major {fieldName} pass", cancellationToken);
        FileSearchLog.Info($"Index-major {fieldName} memory diagnostics: {GetCatalogMemoryDiagnosticsText()}.");
    }

    private void InsertSecondaryIndexPass(
        string fieldName,
        List<FileRecord> records,
        bool[] coreIndexed,
        FileSearchIndexFailureSummary failures,
        CatalogIdentityGroupBatchManager? batch,
        int batchCommitFileCount,
        IProgress<string>? progress,
        FileSearchReindexTelemetry telemetry,
        Stopwatch insertWatch,
        CancellationToken cancellationToken,
        ref long batchEntries,
        ref long lastBatchEntries,
        ref TimeSpan lastBatchElapsed,
        ref long failedFieldAttempts)
    {
        int[] order = CreateSyntheticIndexMajorFieldOrder(fieldName, records, coreIndexed, out int orderCount);
        for (int i = 0; i < orderCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int recordIndex = order[i];
            int failedFields = 0;
            _ = TryInsertIndexMajorField(fieldName, records[recordIndex], failures, telemetry, ref failedFields);
            failedFieldAttempts += failedFields;
            batchEntries++;
            CommitBatchIfNeeded(batch, batchCommitFileCount, batchEntries, ref lastBatchEntries, ref lastBatchElapsed, progress, telemetry, insertWatch, $"index-major {fieldName} entries", cancellationToken);
            if (((i + 1) & 4095) == 0)
            {
                progress?.Report($"Index-major {fieldName}: {i + 1:n0}/{orderCount:n0}, failed field attempts {failedFieldAttempts:n0}, skipped disabled fields {failures.SkippedFieldCount:n0}");
            }
        }

        CommitIndexPassIfNeeded(batch, batchCommitFileCount, batchEntries, ref lastBatchEntries, ref lastBatchElapsed, progress, telemetry, insertWatch, $"index-major {fieldName} pass", cancellationToken);
        FileSearchLog.Info($"Index-major {fieldName} memory diagnostics: {GetCatalogMemoryDiagnosticsText()}.");
    }

    /// <summary>
    /// Builds an index-local sorted order for one synthetic-identity IndexMajor secondary pass.<br/>
    /// Sorting by field key and then synthetic identity keeps duplicate-key runs append-friendly without disturbing the collected record list or the `coreIndexed` map.<br/>
    /// </summary>
    /// <param name="fieldName">The synthetic secondary field being inserted.<br/></param>
    /// <param name="records">The collected records whose original positions are used by <paramref name="coreIndexed"/>.<br/></param>
    /// <param name="coreIndexed">The core-index success map produced before secondary insertion.<br/></param>
    /// <param name="count">Receives the number of usable record indexes written into the returned order array.<br/></param>
    /// <returns>An array whose first <paramref name="count"/> entries are sorted record indexes for this field pass.<br/></returns>
    private static int[] CreateSyntheticIndexMajorFieldOrder(
        string fieldName,
        List<FileRecord> records,
        bool[] coreIndexed,
        out int count)
    {
        int[] order = new int[records.Count];
        count = 0;
        for (int i = 0; i < records.Count; i++)
        {
            if (coreIndexed[i])
            {
                order[count++] = i;
            }
        }

        Array.Sort(order, 0, count, new SyntheticIndexMajorFieldOrderComparer(fieldName, records));
        return order;
    }

    private sealed class SyntheticIndexMajorFieldOrderComparer : IComparer<int>
    {
        private readonly string fieldName;
        private readonly List<FileRecord> records;

        internal SyntheticIndexMajorFieldOrderComparer(string fieldName, List<FileRecord> records)
        {
            this.fieldName = fieldName;
            this.records = records;
        }

        public int Compare(int leftIndex, int rightIndex)
        {
            FileRecord left = records[leftIndex];
            FileRecord right = records[rightIndex];
            int keyComparison = fieldName switch
            {
                "extension" => string.CompareOrdinal(left.Extension, right.Extension),
                "size" => left.Size.CompareTo(right.Size),
                "createdUtc" => ToUtcTicks(left.CreatedUtc).CompareTo(ToUtcTicks(right.CreatedUtc)),
                "modifiedUtc" => ToUtcTicks(left.ModifiedUtc).CompareTo(ToUtcTicks(right.ModifiedUtc)),
                "accessedUtc" => ToUtcTicks(left.AccessedUtc).CompareTo(ToUtcTicks(right.AccessedUtc)),
                "attributes" => left.Attributes.CompareTo(right.Attributes),
                _ => throw new NotSupportedException($"Unsupported synthetic IndexMajor order field '{fieldName}'.")
            };
            if (keyComparison != 0)
            {
                return keyComparison;
            }

            return left.Id.CompareTo(right.Id);
        }
    }

    /// <summary>
    /// Inserts one synthetic-identity IndexMajor field without delegate or lambda dispatch.<br/>
    /// This keeps the workbench hot path closer to the direct LibraDex insert cost being measured.<br/>
    /// </summary>
    private bool TryInsertIndexMajorField(
        string fieldName,
        FileRecord record,
        FileSearchIndexFailureSummary failures,
        FileSearchReindexTelemetry telemetry,
        ref int failedFields,
        bool allowDisable = true)
    {
        if (failures.IsDisabled(fieldName))
        {
            failures.RecordSkipped(fieldName);
            telemetry.RecordFieldSkipped(fieldName);
            return false;
        }

        long startTicks = Stopwatch.GetTimestamp();
        long startAllocatedBytes = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            switch (fieldName)
            {
                case "fileName":
                    RequireInserted(fileNameIndex.Insert(record.FileName, record.Id), fieldName);
                    break;
                case "extension":
                    RequireInserted(record.ExtensionPreparedKey is null
                        ? extensionIndex.Insert(record.Extension, record.Id)
                        : extensionIndex.InsertPrepared(record.ExtensionPreparedKey, record.Id), fieldName);
                    break;
                case "size":
                    RequireInserted(sizeIndex.Insert(record.Size, record.Id), fieldName);
                    break;
                case "createdUtc":
                    RequireInserted(createdUtcIndex.Insert(ToUtcTicks(record.CreatedUtc), record.Id), fieldName);
                    break;
                case "modifiedUtc":
                    RequireInserted(modifiedUtcIndex.Insert(ToUtcTicks(record.ModifiedUtc), record.Id), fieldName);
                    break;
                case "accessedUtc":
                    RequireInserted(accessedUtcIndex.Insert(ToUtcTicks(record.AccessedUtc), record.Id), fieldName);
                    break;
                case "attributes":
                    RequireInserted(attributesIndex.Insert(record.Attributes, record.Id), fieldName);
                    break;
                default:
                    throw new NotSupportedException($"Unsupported IndexMajor field '{fieldName}'.");
            }

            telemetry.RecordField(fieldName, Stopwatch.GetTimestamp() - startTicks, GC.GetAllocatedBytesForCurrentThread() - startAllocatedBytes, inserted: true, failed: false);
            return true;
        }
        catch (Exception ex)
        {
            telemetry.RecordField(fieldName, Stopwatch.GetTimestamp() - startTicks, GC.GetAllocatedBytesForCurrentThread() - startAllocatedBytes, inserted: false, failed: true);
            failedFields++;
            _ = failures.Record(fieldName, record.Path, ex, allowDisable);
            return false;
        }
    }

    /// <summary>
    /// Inserts one path-identity IndexMajor field pass without delegate or lambda dispatch.<br/>
    /// The path identity bytes are reused from collection so this path measures scalar-key/SV8 identity insert behavior directly.<br/>
    /// </summary>
    private void InsertPathIdentityIndexPass(
        string fieldName,
        List<FileRecord> records,
        bool[] coreIndexed,
        FileSearchIndexFailureSummary failures,
        CatalogIdentityGroupBatchManager? batch,
        int batchCommitFileCount,
        IProgress<string>? progress,
        FileSearchReindexTelemetry telemetry,
        Stopwatch insertWatch,
        CancellationToken cancellationToken,
        ref long batchEntries,
        ref long lastBatchEntries,
        ref TimeSpan lastBatchElapsed,
        ref long failedFieldAttempts)
    {
        SortPathIdentityIndexPass(records, fieldName);
        for (int i = 0; i < records.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!coreIndexed[i])
            {
                continue;
            }

            int failedFields = 0;
            _ = TryInsertPathIdentityIndexMajorField(fieldName, records[i], failures, telemetry, ref failedFields, batch is not null);
            failedFieldAttempts += failedFields;
            batchEntries++;
            CommitBatchIfNeeded(batch, batchCommitFileCount, batchEntries, ref lastBatchEntries, ref lastBatchElapsed, progress, telemetry, insertWatch, $"index-major {fieldName} entries", cancellationToken);
            if (((i + 1) & 4095) == 0)
            {
                progress?.Report($"Index-major {fieldName}: {i + 1:n0}/{records.Count:n0}, failed field attempts {failedFieldAttempts:n0}, skipped disabled fields {failures.SkippedFieldCount:n0}");
            }
        }

        CommitIndexPassIfNeeded(batch, batchCommitFileCount, batchEntries, ref lastBatchEntries, ref lastBatchElapsed, progress, telemetry, insertWatch, $"index-major {fieldName} pass", cancellationToken);
        FileSearchLog.Info($"Index-major {fieldName} memory diagnostics: {GetCatalogMemoryDiagnosticsText()}.");
    }

    /// <summary>
    /// Inserts one path-identity scalar field without delegate or lambda dispatch.<br/>
    /// This is the IndexMajor companion to `InsertPathIdentityRecord`, but with a direct switch per field pass.<br/>
    /// </summary>
    private bool TryInsertPathIdentityIndexMajorField(
        string fieldName,
        FileRecord record,
        FileSearchIndexFailureSummary failures,
        FileSearchReindexTelemetry telemetry,
        ref int failedFields,
        bool useCurrentScope,
        bool allowDisable = true)
    {
        if (failures.IsDisabled(fieldName))
        {
            failures.RecordSkipped(fieldName);
            telemetry.RecordFieldSkipped(fieldName);
            return false;
        }

        long startTicks = Stopwatch.GetTimestamp();
        long startAllocatedBytes = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            switch (fieldName)
            {
                case "fileName":
                    RequireInserted(InsertPathIdentity(fileNamePathIdentityIndex, record.FileNameKeyBytes ?? Encoding.UTF8.GetBytes(record.FileName), record, useCurrentScope), fieldName);
                    break;
                case "extension":
                    RequireInserted(InsertPathIdentity(extensionPathIdentityIndex, record.ExtensionKeyBytes ?? Encoding.UTF8.GetBytes(record.Extension), record, useCurrentScope), fieldName);
                    break;
                case "fileName#folded":
                    RequireInserted(InsertPathIdentity(fileNameFoldedPathIdentityIndex, RequireProjectionBytes(record.FileNameFoldedKeyBytes, fieldName), record, useCurrentScope), fieldName);
                    break;
                case "extension#folded":
                    RequireInserted(InsertPathIdentity(extensionFoldedPathIdentityIndex, RequireProjectionBytes(record.ExtensionFoldedKeyBytes, fieldName), record, useCurrentScope), fieldName);
                    break;
                case "fileName#sortkey":
                    RequireInserted(InsertPathIdentity(fileNameSortKeyPathIdentityIndex, RequireProjectionBytes(record.FileNameSortKeyBytes, fieldName), record, useCurrentScope), fieldName);
                    break;
                case "extension#sortkey":
                    RequireInserted(InsertPathIdentity(extensionSortKeyPathIdentityIndex, RequireProjectionBytes(record.ExtensionSortKeyBytes, fieldName), record, useCurrentScope), fieldName);
                    break;
                case "fileName#exact-rev":
                    RequireInserted(InsertPathIdentity(fileNameReversedPathIdentityIndex, RequireProjectionBytes(record.FileNameReversedKeyBytes, fieldName), record, useCurrentScope), fieldName);
                    break;
                case "extension#exact-rev":
                    RequireInserted(InsertPathIdentity(extensionReversedPathIdentityIndex, RequireProjectionBytes(record.ExtensionReversedKeyBytes, fieldName), record, useCurrentScope), fieldName);
                    break;
                case "fileName#folded-rev":
                    RequireInserted(InsertPathIdentity(fileNameFoldedReversedPathIdentityIndex, RequireProjectionBytes(record.FileNameFoldedReversedKeyBytes, fieldName), record, useCurrentScope), fieldName);
                    break;
                case "extension#folded-rev":
                    RequireInserted(InsertPathIdentity(extensionFoldedReversedPathIdentityIndex, RequireProjectionBytes(record.ExtensionFoldedReversedKeyBytes, fieldName), record, useCurrentScope), fieldName);
                    break;
                case "size":
                    RequireInserted(InsertPathIdentityDetailed(sizePathIdentityIndex, record.Size, record, useCurrentScope), fieldName, telemetry);
                    break;
                case "createdUtc":
                    RequireInserted(InsertPathIdentityDetailed(createdUtcPathIdentityIndex, ToUtcTicks(record.CreatedUtc), record, useCurrentScope), fieldName, telemetry);
                    break;
                case "modifiedUtc":
                    RequireInserted(InsertPathIdentityDetailed(modifiedUtcPathIdentityIndex, ToUtcTicks(record.ModifiedUtc), record, useCurrentScope), fieldName, telemetry);
                    break;
                case "accessedUtc":
                    RequireInserted(InsertPathIdentityDetailed(accessedUtcPathIdentityIndex, ToUtcTicks(record.AccessedUtc), record, useCurrentScope), fieldName, telemetry);
                    break;
                case "attributes":
                    RequireInserted(InsertPathIdentityDetailed(attributesPathIdentityIndex, record.Attributes, record, useCurrentScope), fieldName, telemetry);
                    break;
                default:
                    throw new NotSupportedException($"Unsupported path-identity IndexMajor field '{fieldName}'.");
            }

            telemetry.RecordField(fieldName, Stopwatch.GetTimestamp() - startTicks, GC.GetAllocatedBytesForCurrentThread() - startAllocatedBytes, inserted: true, failed: false);
            return true;
        }
        catch (Exception ex)
        {
            telemetry.RecordField(fieldName, Stopwatch.GetTimestamp() - startTicks, GC.GetAllocatedBytesForCurrentThread() - startAllocatedBytes, inserted: false, failed: true);
            failedFields++;
            _ = failures.Record(fieldName, record.Path, ex, allowDisable);
            return false;
        }
    }

    /// <summary>
    /// Commits an index-major field pass when the workbench commit interval is zero.<br/>
    /// Positive intervals keep entry-count commit behavior, while zero means one commit per completed index pass.<br/>
    /// </summary>
    private static void CommitIndexPassIfNeeded(
        CatalogIdentityGroupBatchManager? batch,
        int batchCommitFileCount,
        long batchEntries,
        ref long lastBatchEntries,
        ref TimeSpan lastBatchElapsed,
        IProgress<string>? progress,
        FileSearchReindexTelemetry telemetry,
        Stopwatch insertWatch,
        string label,
        CancellationToken cancellationToken)
    {
        if (batchCommitFileCount == 0)
        {
            CommitBatch(batch, batchEntries, ref lastBatchEntries, ref lastBatchElapsed, progress, telemetry, insertWatch, label, cancellationToken);
        }
    }

    private static void CommitBatchIfNeeded(
        CatalogIdentityGroupBatchManager? batch,
        int batchCommitFileCount,
        long batchEntries,
        ref long lastBatchEntries,
        ref TimeSpan lastBatchElapsed,
        IProgress<string>? progress,
        FileSearchReindexTelemetry telemetry,
        Stopwatch insertWatch,
        string label,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (batch is not null &&
            batchCommitFileCount > 0 &&
            batchEntries % batchCommitFileCount == 0)
        {
            CommitBatch(batch, batchEntries, ref lastBatchEntries, ref lastBatchElapsed, progress, telemetry, insertWatch, label, cancellationToken);
        }
    }

    /// <summary>
    /// Commits the active group batch and records one consistent telemetry/log entry.<br/>
    /// The method is shared by interval commits and index-pass commits so their timings are directly comparable in logs.<br/>
    /// </summary>
    private static void CommitBatch(
        CatalogIdentityGroupBatchManager? batch,
        long batchEntries,
        ref long lastBatchEntries,
        ref TimeSpan lastBatchElapsed,
        IProgress<string>? progress,
        FileSearchReindexTelemetry telemetry,
        Stopwatch insertWatch,
        string label,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (batch is null || batchEntries == lastBatchEntries)
        {
            return;
        }

        TimeSpan beforeCommitElapsed = insertWatch.Elapsed;
        long beforeCommitTicks = Stopwatch.GetTimestamp();
        LibraDexGenericBatchCommitResult result = batch.Commit();
        long commitTicks = Stopwatch.GetTimestamp() - beforeCommitTicks;
        cancellationToken.ThrowIfCancellationRequested();
        TimeSpan afterCommitElapsed = insertWatch.Elapsed;
        long deltaEntries = batchEntries - lastBatchEntries;
        lastBatchEntries = batchEntries;
        TimeSpan insertDeltaElapsed = beforeCommitElapsed - lastBatchElapsed;
        TimeSpan batchTotalElapsed = afterCommitElapsed - lastBatchElapsed;
        lastBatchElapsed = afterCommitElapsed;
        TimeSpan commitElapsed = TimeSpan.FromTicks((long)(commitTicks * (TimeSpan.TicksPerSecond / (double)Stopwatch.Frequency)));
        telemetry.RecordBatch(label, batchEntries, deltaEntries, result.AttemptedInsertCount, result.InsertedCount, result.DeferredCommitRequests, insertDeltaElapsed, commitElapsed, batchTotalElapsed, afterCommitElapsed);
        bool indexMajor = label.StartsWith("index-major ", StringComparison.Ordinal);
        string unit = indexMajor ? "IndexEntries" : "Records";
        string message = $"Committed group batch: Label={label}; Unit={unit}; Delta{unit}={deltaEntries:n0}; Cumulative{unit}={batchEntries:n0}; InsertDelta={FormatDuration(insertDeltaElapsed)}; Commit={FormatDuration(commitElapsed)}; BatchTotal={FormatDuration(batchTotalElapsed)}; CumulativeElapsed={FormatDuration(afterCommitElapsed)}; Attempted={result.AttemptedInsertCount:n0}; Inserted={result.InsertedCount:n0}; DeferredCommits={result.DeferredCommitRequests:n0}; DirtyShelves={result.StorageDiagnostics.TotalDirtyShelves:n0}; VS8Dirty={result.StorageDiagnostics.VarKeyScalar8DirtyShelves:n0}; SS88Dirty={result.StorageDiagnostics.Scalar8Scalar8DirtyShelves:n0}; VSMutableBytes={result.StorageDiagnostics.TotalVarKeyScalarMutableShelfBytes:n0}; Bytes={result.CommitDiagnostics.BytesWritten:n0}; Segments={result.CommitDiagnostics.StagedSegmentCount:n0}; Writes={result.CommitDiagnostics.WriteCallCount:n0}; BatchRate={FormatRate(deltaEntries, batchTotalElapsed)} {unit}/s.";
        progress?.Report(message);
        FileSearchLog.Info(message);
    }

    private FileIndexResult InsertRecord(FileRecord record, FileSearchIndexFailureSummary failures, FileSearchReindexTelemetry telemetry, bool indexExtension)
    {
        int failedFields = 0;
        bool coreIndexed = TryInsertField("fileName", record, () => fileNameIndex.Insert(record.FileName, record.Id), failures, telemetry, ref failedFields, allowDisable: false);

        if (!coreIndexed)
        {
            return new FileIndexResult(false, failedFields);
        }

        if (indexExtension)
        {
        _ = TryInsertField("extension", record, () =>
        {
            return record.ExtensionPreparedKey is null
                ? extensionIndex.Insert(record.Extension, record.Id)
                : extensionIndex.InsertPrepared(record.ExtensionPreparedKey, record.Id);
        }, failures, telemetry, ref failedFields);
        }

        _ = TryInsertField("size", record, () => sizeIndex.Insert(record.Size, record.Id), failures, telemetry, ref failedFields);
        _ = TryInsertField("createdUtc", record, () => createdUtcIndex.Insert(ToUtcTicks(record.CreatedUtc), record.Id), failures, telemetry, ref failedFields);
        _ = TryInsertField("modifiedUtc", record, () => modifiedUtcIndex.Insert(ToUtcTicks(record.ModifiedUtc), record.Id), failures, telemetry, ref failedFields);
        _ = TryInsertField("accessedUtc", record, () => accessedUtcIndex.Insert(ToUtcTicks(record.AccessedUtc), record.Id), failures, telemetry, ref failedFields);
        _ = TryInsertField("attributes", record, () => attributesIndex.Insert(record.Attributes, record.Id), failures, telemetry, ref failedFields);
        return new FileIndexResult(true, failedFields);
    }

    /// <summary>
    /// Inserts one record into every connected filepath-identity index.<br/>
    /// Filename and extension use raw-byte `VV`; scalar file properties use UInt64-key `SV8`.<br/>
    /// </summary>
    private FileIndexResult InsertPathIdentityRecord(FileRecord record, FileSearchIndexFailureSummary failures, FileSearchReindexTelemetry telemetry, bool useCurrentScope, FileSearchReindexOptions options)
    {
        int failedFields = 0;
        bool coreIndexed =
            TryInsertField("fileName", record, () => RequireInserted(InsertPathIdentity(fileNamePathIdentityIndex, record.FileNameKeyBytes ?? Encoding.UTF8.GetBytes(record.FileName), record, useCurrentScope), "fileName"), failures, telemetry, ref failedFields, allowDisable: false) &
            TryInsertField("extension", record, () => RequireInserted(InsertPathIdentity(extensionPathIdentityIndex, record.ExtensionKeyBytes ?? Encoding.UTF8.GetBytes(record.Extension), record, useCurrentScope), "extension"), failures, telemetry, ref failedFields, allowDisable: false) &
            TryInsertField("size", record, () => RequireInserted(InsertPathIdentity(sizePathIdentityIndex, record.Size, record, useCurrentScope), "size"), failures, telemetry, ref failedFields, allowDisable: false) &
            TryInsertField("createdUtc", record, () => RequireInserted(InsertPathIdentity(createdUtcPathIdentityIndex, ToUtcTicks(record.CreatedUtc), record, useCurrentScope), "createdUtc"), failures, telemetry, ref failedFields, allowDisable: false) &
            TryInsertField("modifiedUtc", record, () => RequireInserted(InsertPathIdentity(modifiedUtcPathIdentityIndex, ToUtcTicks(record.ModifiedUtc), record, useCurrentScope), "modifiedUtc"), failures, telemetry, ref failedFields, allowDisable: false) &
            TryInsertField("accessedUtc", record, () => RequireInserted(InsertPathIdentity(accessedUtcPathIdentityIndex, ToUtcTicks(record.AccessedUtc), record, useCurrentScope), "accessedUtc"), failures, telemetry, ref failedFields, allowDisable: false) &
            TryInsertField("attributes", record, () => RequireInserted(InsertPathIdentity(attributesPathIdentityIndex, record.Attributes, record, useCurrentScope), "attributes"), failures, telemetry, ref failedFields, allowDisable: false);
        if (!coreIndexed)
        {
            return new FileIndexResult(false, failedFields);
        }

        if (options.IndexStringFolded)
        {
            _ = TryInsertField("fileName#folded", record, () => RequireInserted(InsertPathIdentity(fileNameFoldedPathIdentityIndex, RequireProjectionBytes(record.FileNameFoldedKeyBytes, "fileName#folded"), record, useCurrentScope), "fileName#folded"), failures, telemetry, ref failedFields);
            _ = TryInsertField("extension#folded", record, () => RequireInserted(InsertPathIdentity(extensionFoldedPathIdentityIndex, RequireProjectionBytes(record.ExtensionFoldedKeyBytes, "extension#folded"), record, useCurrentScope), "extension#folded"), failures, telemetry, ref failedFields);
        }

        if (options.IndexStringSortKey)
        {
            _ = TryInsertField("fileName#sortkey", record, () => RequireInserted(InsertPathIdentity(fileNameSortKeyPathIdentityIndex, RequireProjectionBytes(record.FileNameSortKeyBytes, "fileName#sortkey"), record, useCurrentScope), "fileName#sortkey"), failures, telemetry, ref failedFields);
            _ = TryInsertField("extension#sortkey", record, () => RequireInserted(InsertPathIdentity(extensionSortKeyPathIdentityIndex, RequireProjectionBytes(record.ExtensionSortKeyBytes, "extension#sortkey"), record, useCurrentScope), "extension#sortkey"), failures, telemetry, ref failedFields);
        }

        if (options.IndexStringReversed)
        {
            _ = TryInsertField("fileName#exact-rev", record, () => RequireInserted(InsertPathIdentity(fileNameReversedPathIdentityIndex, RequireProjectionBytes(record.FileNameReversedKeyBytes, "fileName#exact-rev"), record, useCurrentScope), "fileName#exact-rev"), failures, telemetry, ref failedFields);
            _ = TryInsertField("extension#exact-rev", record, () => RequireInserted(InsertPathIdentity(extensionReversedPathIdentityIndex, RequireProjectionBytes(record.ExtensionReversedKeyBytes, "extension#exact-rev"), record, useCurrentScope), "extension#exact-rev"), failures, telemetry, ref failedFields);
            if (options.IndexStringFolded)
            {
                _ = TryInsertField("fileName#folded-rev", record, () => RequireInserted(InsertPathIdentity(fileNameFoldedReversedPathIdentityIndex, RequireProjectionBytes(record.FileNameFoldedReversedKeyBytes, "fileName#folded-rev"), record, useCurrentScope), "fileName#folded-rev"), failures, telemetry, ref failedFields);
                _ = TryInsertField("extension#folded-rev", record, () => RequireInserted(InsertPathIdentity(extensionFoldedReversedPathIdentityIndex, RequireProjectionBytes(record.ExtensionFoldedReversedKeyBytes, "extension#folded-rev"), record, useCurrentScope), "extension#folded-rev"), failures, telemetry, ref failedFields);
            }
        }

        return new FileIndexResult(coreIndexed, failedFields);
    }

    /// <summary>
    /// Inserts one scalar key and the record's UTF-8 path bytes into a UInt64/SV8 variable-identity index.<br/>
    /// The path identity bytes are computed during collection and reused across all scalar field inserts for the record.<br/>
    /// When `useCurrentScope` is true, the insert joins the caller's active group batch instead of trying to open a nested batch.<br/>
    /// </summary>
    private static LibraDexGenericInsertResult InsertPathIdentity(LibraDexUInt64VarIdentityIndex index, ulong key, FileRecord record, bool useCurrentScope)
    {
        byte[] identityBytes = record.PathIdentityBytes ?? Encoding.UTF8.GetBytes(record.Path);
        if (useCurrentScope)
        {
            Scalar8VarIdentityInsertOutcome outcome = index.InsertInCurrentScopeDetailed(key, identityBytes);
            if (!outcome.Inserted)
            {
                throw new InvalidOperationException($"The PathString SV8 insert for '{index.Name}' did not report Inserted=true. Kind={outcome.Kind}; InsertResult={outcome.InsertResult}; AlreadyPresent={outcome.AlreadyPresent}; KeyConflict={outcome.KeyConflict}; Grew={outcome.GrewShelf}; Split={outcome.SplitShelf}; DuplicateRun={outcome.DuplicateRunOverflow}; TargetItems={outcome.TargetShelfItemCount:n0}; TargetBytes={outcome.TargetShelfExtentSize:n0}; RouterDepth={outcome.TargetRouterDepth:n0}; IdentityBytes={identityBytes.Length:n0}; MaxIdentityBytes={index.MaxIdentityBytes:n0}.");
            }

            return new LibraDexGenericInsertResult(
                outcome.Inserted,
                outcome.CreatedInitialShelfRoute,
                default,
                default);
        }

        return index.Insert(key, identityBytes);
    }

    /// <summary>
    /// Inserts one scalar key and path identity while retaining the detailed `SV8` routed outcome for workbench telemetry.<br/>
    /// The index-major hot path uses this to classify insert route behavior without changing the public generic insert result contract.<br/>
    /// Non-current-scope callers still execute the ordinary index insert and receive a minimal synthetic detailed outcome because the public path owns commit telemetry rather than per-route diagnostics.<br/>
    /// </summary>
    private static Scalar8VarIdentityInsertOutcome InsertPathIdentityDetailed(LibraDexUInt64VarIdentityIndex index, ulong key, FileRecord record, bool useCurrentScope)
    {
        byte[] identityBytes = record.PathIdentityBytes ?? Encoding.UTF8.GetBytes(record.Path);
        if (useCurrentScope)
        {
            return index.InsertInCurrentScopeDetailed(key, identityBytes);
        }

        LibraDexGenericInsertResult result = index.Insert(key, identityBytes);
        return new Scalar8VarIdentityInsertOutcome(
            result.Inserted,
            AlreadyPresent: false,
            KeyConflict: false,
            Scalar8VarIdentityRoutedInsertKind.NoOp,
            result.Inserted ? Scalar8VarIdentityInsertResult.Inserted : Scalar8VarIdentityInsertResult.Invalid,
            result.CreatedInitialShelfRoute,
            GrewShelf: false,
            SplitShelf: false,
            DuplicateRunOverflow: false,
            TargetShelfItemCount: 0,
            TargetShelfExtentSize: 0,
            TargetRouterDepth: 0,
            Scalar8VarIdentityInsertDiagnosticPath.None,
            DiagnosticAllocatedBytes: 0,
            default,
            DeferredCommitRequests: 0);
    }

    /// <summary>
    /// Inserts one raw variable key and the record's UTF-8 path bytes into a `VV` variable-key/variable-identity index.<br/>
    /// The workbench uses this for filename and extension under PathString identity mode so enumeration can read key/path tuples directly from LibraDex.<br/>
    /// </summary>
    private static LibraDexGenericInsertResult InsertPathIdentity(VarKeyVarIdentityIndex index, ReadOnlySpan<byte> key, FileRecord record, bool useCurrentScope)
    {
        byte[] identityBytes = record.PathIdentityBytes ?? Encoding.UTF8.GetBytes(record.Path);
        VarKeyVarIdentityIndexInsertResult result = useCurrentScope
            ? index.InsertInCurrentScope(key, identityBytes)
            : index.Insert(key, identityBytes);
        return new LibraDexGenericInsertResult(
            result.Inserted,
            result.CreatedInitialShelfRoute,
            default,
            default);
    }

    private static LibraDexGenericInsertResult RequireInserted(LibraDexGenericInsertResult result, string fieldName)
    {
        if (!result.Inserted)
        {
            throw new InvalidOperationException($"The PathString SV8 insert for '{fieldName}' did not report Inserted=true.");
        }

        return result;
    }

    /// <summary>
    /// Validates one detailed `SV8` insert outcome and records its routed insert kind for reindex diagnostics.<br/>
    /// This keeps path-frequency telemetry beside the field timing counters without adding per-insert log lines or allocations.<br/>
    /// </summary>
    private static void RequireInserted(Scalar8VarIdentityInsertOutcome outcome, string fieldName, FileSearchReindexTelemetry telemetry)
    {
        telemetry.RecordScalar8VarIdentityOutcome(fieldName, outcome);
        if (!outcome.Inserted)
        {
            throw new InvalidOperationException($"The PathString SV8 insert for '{fieldName}' did not report Inserted=true. Kind={outcome.Kind}; InsertResult={outcome.InsertResult}; AlreadyPresent={outcome.AlreadyPresent}; KeyConflict={outcome.KeyConflict}; Grew={outcome.GrewShelf}; Split={outcome.SplitShelf}; DuplicateRun={outcome.DuplicateRunOverflow}; TargetItems={outcome.TargetShelfItemCount:n0}; TargetBytes={outcome.TargetShelfExtentSize:n0}; RouterDepth={outcome.TargetRouterDepth:n0}.");
        }
    }

    private static byte[] RequireProjectionBytes(byte[]? bytes, string fieldName)
        => bytes ?? throw new InvalidOperationException($"Projection bytes were not prepared for '{fieldName}'.");

    private static bool TryInsertField(
        string fieldName,
        FileRecord record,
        Func<LibraDexGenericInsertResult> insert,
        FileSearchIndexFailureSummary failures,
        FileSearchReindexTelemetry telemetry,
        ref int failedFields,
        bool allowDisable = true)
    {
        if (failures.IsDisabled(fieldName))
        {
            failures.RecordSkipped(fieldName);
            telemetry.RecordFieldSkipped(fieldName);
            return false;
        }

        long startTicks = Stopwatch.GetTimestamp();
        long startAllocatedBytes = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            _ = insert();
            telemetry.RecordField(
                fieldName,
                Stopwatch.GetTimestamp() - startTicks,
                GC.GetAllocatedBytesForCurrentThread() - startAllocatedBytes,
                inserted: true,
                failed: false);
            return true;
        }
        catch (Exception ex)
        {
            telemetry.RecordField(
                fieldName,
                Stopwatch.GetTimestamp() - startTicks,
                GC.GetAllocatedBytesForCurrentThread() - startAllocatedBytes,
                inserted: false,
                failed: true);
            failedFields++;
            _ = failures.Record(fieldName, record.Path, ex, allowDisable);
            return false;
        }
    }

    private int CountDistinct(SearchField field)
    {
        return field switch
        {
            SearchField.FileName => state.Files.Select(static file => file.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            SearchField.Extension => state.Files.Select(static file => file.Extension).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            SearchField.Size => state.Files.Select(static file => file.Size).Distinct().Count(),
            SearchField.CreatedUtc => state.Files.Select(static file => file.CreatedUtc).Distinct().Count(),
            SearchField.ModifiedUtc => state.Files.Select(static file => file.ModifiedUtc).Distinct().Count(),
            SearchField.AccessedUtc => state.Files.Select(static file => file.AccessedUtc).Distinct().Count(),
            SearchField.Attributes => state.Files.Select(static file => file.Attributes).Distinct().Count(),
            _ => state.Files.Count
        };
    }

    private static int? NormalizeTake(int length)
    {
        return length <= 0 ? null : length;
    }

    private static void ApplyIngestOrder(List<FileRecord> records, LibraDexWriteOrder writeOrder)
    {
        switch (writeOrder)
        {
            case LibraDexWriteOrder.Sorted:
                records.Sort(static (left, right) => string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase));
                break;
            case LibraDexWriteOrder.Random:
                records.Sort(static (left, right) =>
                {
                    int high = left.PathHashHigh.CompareTo(right.PathHashHigh);
                    return high != 0 ? high : left.PathHashLow.CompareTo(right.PathHashLow);
                });
                break;
        }
    }

    private static void SortPathIdentityIndexPass(List<FileRecord> records, string fieldName)
    {
        records.Sort(fieldName switch
        {
            "size" => ComparePathIdentitySize,
            "fileName" => ComparePathIdentityFileName,
            "extension" => ComparePathIdentityExtension,
            "fileName#folded" => ComparePathIdentityFileNameFolded,
            "extension#folded" => ComparePathIdentityExtensionFolded,
            "fileName#sortkey" => ComparePathIdentityFileNameSortKey,
            "extension#sortkey" => ComparePathIdentityExtensionSortKey,
            "fileName#exact-rev" => ComparePathIdentityFileNameReversed,
            "extension#exact-rev" => ComparePathIdentityExtensionReversed,
            "fileName#folded-rev" => ComparePathIdentityFileNameFoldedReversed,
            "extension#folded-rev" => ComparePathIdentityExtensionFoldedReversed,
            "createdUtc" => ComparePathIdentityCreatedUtc,
            "modifiedUtc" => ComparePathIdentityModifiedUtc,
            "accessedUtc" => ComparePathIdentityAccessedUtc,
            "attributes" => ComparePathIdentityAttributes,
            _ => ComparePathIdentityBytes
        });
    }

    private static int ComparePathIdentitySize(FileRecord left, FileRecord right)
    {
        int key = left.Size.CompareTo(right.Size);
        return key != 0 ? key : ComparePathIdentityBytes(left, right);
    }

    private static int ComparePathIdentityFileName(FileRecord left, FileRecord right)
    {
        ReadOnlySpan<byte> leftKey = left.FileNameKeyBytes ?? Encoding.UTF8.GetBytes(left.FileName);
        ReadOnlySpan<byte> rightKey = right.FileNameKeyBytes ?? Encoding.UTF8.GetBytes(right.FileName);
        int key = leftKey.SequenceCompareTo(rightKey);
        return key != 0 ? key : ComparePathIdentityBytes(left, right);
    }

    private static int ComparePathIdentityExtension(FileRecord left, FileRecord right)
    {
        ReadOnlySpan<byte> leftKey = left.ExtensionKeyBytes ?? Encoding.UTF8.GetBytes(left.Extension);
        ReadOnlySpan<byte> rightKey = right.ExtensionKeyBytes ?? Encoding.UTF8.GetBytes(right.Extension);
        int key = leftKey.SequenceCompareTo(rightKey);
        return key != 0 ? key : ComparePathIdentityBytes(left, right);
    }

    private static int ComparePathIdentityFileNameFolded(FileRecord left, FileRecord right)
        => ComparePathIdentityProjectionBytes(RequireProjectionBytes(left.FileNameFoldedKeyBytes, "fileName#folded"), RequireProjectionBytes(right.FileNameFoldedKeyBytes, "fileName#folded"), left, right);

    private static int ComparePathIdentityExtensionFolded(FileRecord left, FileRecord right)
        => ComparePathIdentityProjectionBytes(RequireProjectionBytes(left.ExtensionFoldedKeyBytes, "extension#folded"), RequireProjectionBytes(right.ExtensionFoldedKeyBytes, "extension#folded"), left, right);

    private static int ComparePathIdentityFileNameSortKey(FileRecord left, FileRecord right)
        => ComparePathIdentityProjectionBytes(RequireProjectionBytes(left.FileNameSortKeyBytes, "fileName#sortkey"), RequireProjectionBytes(right.FileNameSortKeyBytes, "fileName#sortkey"), left, right);

    private static int ComparePathIdentityExtensionSortKey(FileRecord left, FileRecord right)
        => ComparePathIdentityProjectionBytes(RequireProjectionBytes(left.ExtensionSortKeyBytes, "extension#sortkey"), RequireProjectionBytes(right.ExtensionSortKeyBytes, "extension#sortkey"), left, right);

    private static int ComparePathIdentityFileNameReversed(FileRecord left, FileRecord right)
        => ComparePathIdentityProjectionBytes(RequireProjectionBytes(left.FileNameReversedKeyBytes, "fileName#exact-rev"), RequireProjectionBytes(right.FileNameReversedKeyBytes, "fileName#exact-rev"), left, right);

    private static int ComparePathIdentityExtensionReversed(FileRecord left, FileRecord right)
        => ComparePathIdentityProjectionBytes(RequireProjectionBytes(left.ExtensionReversedKeyBytes, "extension#exact-rev"), RequireProjectionBytes(right.ExtensionReversedKeyBytes, "extension#exact-rev"), left, right);

    private static int ComparePathIdentityFileNameFoldedReversed(FileRecord left, FileRecord right)
        => ComparePathIdentityProjectionBytes(RequireProjectionBytes(left.FileNameFoldedReversedKeyBytes, "fileName#folded-rev"), RequireProjectionBytes(right.FileNameFoldedReversedKeyBytes, "fileName#folded-rev"), left, right);

    private static int ComparePathIdentityExtensionFoldedReversed(FileRecord left, FileRecord right)
        => ComparePathIdentityProjectionBytes(RequireProjectionBytes(left.ExtensionFoldedReversedKeyBytes, "extension#folded-rev"), RequireProjectionBytes(right.ExtensionFoldedReversedKeyBytes, "extension#folded-rev"), left, right);

    private static int ComparePathIdentityProjectionBytes(ReadOnlySpan<byte> leftKey, ReadOnlySpan<byte> rightKey, FileRecord left, FileRecord right)
    {
        int key = leftKey.SequenceCompareTo(rightKey);
        return key != 0 ? key : ComparePathIdentityBytes(left, right);
    }

    private static int ComparePathIdentityCreatedUtc(FileRecord left, FileRecord right)
    {
        int key = ToUtcTicks(left.CreatedUtc).CompareTo(ToUtcTicks(right.CreatedUtc));
        return key != 0 ? key : ComparePathIdentityBytes(left, right);
    }

    private static int ComparePathIdentityModifiedUtc(FileRecord left, FileRecord right)
    {
        int key = ToUtcTicks(left.ModifiedUtc).CompareTo(ToUtcTicks(right.ModifiedUtc));
        return key != 0 ? key : ComparePathIdentityBytes(left, right);
    }

    private static int ComparePathIdentityAccessedUtc(FileRecord left, FileRecord right)
    {
        int key = ToUtcTicks(left.AccessedUtc).CompareTo(ToUtcTicks(right.AccessedUtc));
        return key != 0 ? key : ComparePathIdentityBytes(left, right);
    }

    private static int ComparePathIdentityAttributes(FileRecord left, FileRecord right)
    {
        int key = left.Attributes.CompareTo(right.Attributes);
        return key != 0 ? key : ComparePathIdentityBytes(left, right);
    }

    private static int ComparePathIdentityBytes(FileRecord left, FileRecord right)
    {
        ReadOnlySpan<byte> leftIdentity = left.PathIdentityBytes ?? Encoding.UTF8.GetBytes(left.Path);
        ReadOnlySpan<byte> rightIdentity = right.PathIdentityBytes ?? Encoding.UTF8.GetBytes(right.Path);
        return leftIdentity.SequenceCompareTo(rightIdentity);
    }

    private static void SetPathHash(FileRecord record)
    {
        Span<byte> hash = stackalloc byte[16];
        Span<char> stackChars = stackalloc char[512];
        char[]? rentedChars = null;
        Span<char> normalizedChars = record.Path.Length <= stackChars.Length
            ? stackChars[..record.Path.Length]
            : (rentedChars = ArrayPool<char>.Shared.Rent(record.Path.Length)).AsSpan(0, record.Path.Length);

        Span<byte> stackPathBytes = stackalloc byte[1024];
        byte[]? rentedBytes = null;
        try
        {
            _ = record.Path.AsSpan().ToUpperInvariant(normalizedChars);
            int byteCount = Encoding.UTF8.GetByteCount(normalizedChars);
            Span<byte> pathBytes = byteCount <= stackPathBytes.Length
                ? stackPathBytes[..byteCount]
                : (rentedBytes = ArrayPool<byte>.Shared.Rent(byteCount)).AsSpan(0, byteCount);
            _ = Encoding.UTF8.GetBytes(normalizedChars, pathBytes);
            _ = MD5.HashData(pathBytes, hash);
        }
        finally
        {
            if (rentedBytes is not null)
            {
                ArrayPool<byte>.Shared.Return(rentedBytes);
            }

            if (rentedChars is not null)
            {
                ArrayPool<char>.Shared.Return(rentedChars);
            }
        }

        record.PathHashHigh = BinaryPrimitives.ReadUInt64BigEndian(hash[..8]);
        record.PathHashLow = BinaryPrimitives.ReadUInt64BigEndian(hash.Slice(8, 8));
    }

    private static string FormatDuration(TimeSpan elapsed)
    {
        return elapsed.TotalSeconds >= 1
            ? $"{elapsed.TotalSeconds:n2}s"
            : $"{elapsed.TotalMilliseconds:n0}ms";
    }

    private static string ReverseString(string value)
    {
        return string.Create(value.Length, value, static (destination, source) =>
        {
            for (int i = 0; i < source.Length; i++)
            {
                destination[i] = source[source.Length - 1 - i];
            }
        });
    }

    private static string FoldString(string value)
        => value.ToLower(CultureInfo.InvariantCulture);

    /// <summary>
    /// Creates an invariant ignore-case sort-key projection payload that remains safe for raw `VV` route range traversal.<br/>
    /// `SortKey.KeyData` can contain embedded zero bytes; hex encoding doubles the bytes but preserves lexicographic ordering while keeping this workbench projection in the non-zero ASCII byte domain.<br/>
    /// </summary>
    /// <param name="value">The developer-facing string value.</param>
    /// <returns>Uppercase ASCII hex bytes for the invariant ignore-case sort key.</returns>
    private static byte[] CreateInvariantSortKey(string value)
    {
        byte[] sortKey = CultureInfo.InvariantCulture.CompareInfo.GetSortKey(value, CompareOptions.IgnoreCase).KeyData;
        byte[] encoded = GC.AllocateUninitializedArray<byte>(checked(sortKey.Length * 2));
        const string Hex = "0123456789ABCDEF";
        for (int i = 0, o = 0; i < sortKey.Length; i++)
        {
            byte valueByte = sortKey[i];
            encoded[o++] = (byte)Hex[valueByte >> 4];
            encoded[o++] = (byte)Hex[valueByte & 0x0F];
        }

        return encoded;
    }

    private static string FormatRate(long entries, TimeSpan elapsed)
    {
        return elapsed.TotalSeconds > 0
            ? (entries / elapsed.TotalSeconds).ToString("N0", CultureInfo.InvariantCulture)
            : "n/a";
    }

    /// <summary>
    /// Reads one integer property from the internal DataKernel diagnostic snapshot through reflection.<br/>
    /// The workbench uses reflection here to avoid promoting memory-arena diagnostics into the public catalog API.<br/>
    /// </summary>
    /// <param name="diagnosticsType">The runtime type of the internal diagnostic snapshot.</param>
    /// <param name="diagnostics">The boxed internal diagnostic snapshot.</param>
    /// <param name="propertyName">The diagnostic property to read.</param>
    /// <returns>The integer diagnostic value, or zero when the property is unavailable.</returns>
    private static int GetDiagnosticInt(Type diagnosticsType, object diagnostics, string propertyName)
    {
        return (int)(diagnosticsType.GetProperty(propertyName)?.GetValue(diagnostics) ?? 0);
    }

    /// <summary>
    /// Reads one long integer property from the internal DataKernel diagnostic snapshot through reflection.<br/>
    /// The helper keeps the FileSearch workbench diagnostic path separate from the public LibraDex catalog surface.<br/>
    /// </summary>
    /// <param name="diagnosticsType">The runtime type of the internal diagnostic snapshot.</param>
    /// <param name="diagnostics">The boxed internal diagnostic snapshot.</param>
    /// <param name="propertyName">The diagnostic property to read.</param>
    /// <returns>The long diagnostic value, or zero when the property is unavailable.</returns>
    private static long GetDiagnosticLong(Type diagnosticsType, object diagnostics, string propertyName)
    {
        return (long)(diagnosticsType.GetProperty(propertyName)?.GetValue(diagnostics) ?? 0L);
    }

    private static string FormatBytes(long bytes)
    {
        const double KiB = 1024;
        const double MiB = KiB * 1024;
        const double GiB = MiB * 1024;
        double value = Math.Abs((double)bytes);
        string sign = bytes < 0 ? "-" : string.Empty;
        return value >= GiB
            ? $"{sign}{value / GiB:n1} GB"
            : value >= MiB
                ? $"{sign}{value / MiB:n1} MB"
                : value >= KiB
                    ? $"{sign}{value / KiB:n1} KB"
                    : $"{bytes:n0} B";
    }

    /// <summary>
    /// Forces a compacting full collection after the dogfood workbench discards a canceled in-memory catalog build.<br/>
    /// The FileSearch app intentionally uses large, adversarial insert runs that can leave the CLR with gigabytes of committed but no-longer-live GC segments after cancellation.<br/>
    /// This method belongs to the workbench reset path rather than the core LibraDex write path because normal library callers should not pay forced-GC latency during ordinary operations.<br/>
    /// </summary>
    private static void CompactWorkbenchHeapAfterReset()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    /// <summary>
    /// Verifies that the requested read identity mode matches the active catalog shape.<br/>
    /// The FileSearch workbench recreates physical indexes when identity mode changes, so mixed-mode reads are rejected instead of returning misleading rows.<br/>
    /// </summary>
    private void EnsureCatalogIdentityMode(FileSearchIdentityMode identityMode)
    {
        if (identityMode != state.IdentityMode)
        {
            throw new InvalidOperationException($"The active catalog was indexed with {state.IdentityMode}; requested read mode was {identityMode}.");
        }
    }

    private static IReadOnlyList<FileRecord> Hydrate(IEnumerable<FileRecord> records, FileSearchReadOptions options)
    {
        if (options.RowHydrationMode == FileSearchRowHydrationMode.LibraDexInverseIndexes)
        {
            throw new NotSupportedException("LibraDexInverseIndexes hydration is visible as a planned test mode, but the current catalog stores file properties as key-to-identity indexes and has no identity-to-property projection yet.");
        }

        List<FileRecord> hydrated = new();
        foreach (FileRecord record in records)
        {
            FileRecord output = options.RowHydrationMode == FileSearchRowHydrationMode.FileSystem
                ? RefreshFromFileSystem(record)
                : CloneRecord(record);
            hydrated.Add(ApplyDisplayMode(output, options.DisplayFieldMode));
        }

        return hydrated;
    }

    private static FileRecord RefreshFromFileSystem(FileRecord source)
    {
        if (!File.Exists(source.Path))
        {
            return new FileRecord
            {
                Id = source.Id,
                Path = source.Path,
                FileName = source.FileName,
                Extension = source.Extension
            };
        }

        FileInfo info = new(source.Path);
        return new FileRecord
        {
            Id = source.Id,
            Path = info.FullName,
            FileName = info.Name,
            Extension = NormalizeExtension(info.Extension),
            Size = (ulong)Math.Max(0, info.Length),
            CreatedUtc = DateTime.SpecifyKind(info.CreationTimeUtc, DateTimeKind.Utc),
            ModifiedUtc = DateTime.SpecifyKind(info.LastWriteTimeUtc, DateTimeKind.Utc),
            AccessedUtc = DateTime.SpecifyKind(info.LastAccessTimeUtc, DateTimeKind.Utc),
            Attributes = (uint)info.Attributes
        };
    }

    private static FileRecord ApplyDisplayMode(FileRecord source, FileSearchDisplayFieldMode mode)
    {
        if (mode == FileSearchDisplayFieldMode.AllKnownFields)
        {
            return source;
        }

        FileRecord record = new()
        {
            Id = source.Id,
            Path = source.Path
        };
        if (mode == FileSearchDisplayFieldMode.SelectedFields)
        {
            record.FileName = source.FileName;
            record.Extension = source.Extension;
            record.Size = source.Size;
            record.ModifiedUtc = source.ModifiedUtc;
        }

        return record;
    }

    private static FileRecord CloneRecord(FileRecord source)
    {
        return new FileRecord
        {
            Id = source.Id,
            Path = source.Path,
            FileName = source.FileName,
            Extension = source.Extension,
            Size = source.Size,
            CreatedUtc = source.CreatedUtc,
            ModifiedUtc = source.ModifiedUtc,
            AccessedUtc = source.AccessedUtc,
            Attributes = source.Attributes
        };
    }

    private LibraDexConditionEndCondition BuildCondition(IReadOnlyList<QueryRow> rows, out string preview)
    {
        LibraDexConditionClause clause = LibraDexCondition.Group(GroupName);
        LibraDexConditionContinueOrEnd? continuation = null;
        List<string> fragments = new(rows.Count);

        for (int i = 0; i < rows.Count; i++)
        {
            QueryRow row = rows[i];
            clause = i == 0
                ? LibraDexCondition.Group(GroupName)
                : continuation!.AND;
            continuation = ApplyClause(clause, row, state.IdentityMode, out string fragment);
            fragments.Add(i == 0 ? fragment : $".AND.{fragment}");
        }

        preview = $"LibraDexCondition.Group(\"{GroupName}\").{string.Concat(fragments)}.EndCondition";
        return continuation!.EndCondition;
    }

    /// <summary>
    /// Logs the workbench-observable condition-builder query path and phase timings.<br/>
    /// The method intentionally stays at the workbench boundary: condition build, LibraDex retrieval, and output formatting are separated without adding instrumentation inside core readers.<br/>
    /// </summary>
    /// <param name="outputMode">The user-visible output shape being produced: identity, tuple, or record.<br/></param>
    /// <param name="rows">The AND-connected condition rows used to build the condition.<br/></param>
    /// <param name="identityMode">The active identity mode for this catalog.<br/></param>
    /// <param name="skip">The requested skip count.<br/></param>
    /// <param name="take">The requested take count, or <see langword="null"/> for no limit.<br/></param>
    /// <param name="outputRows">The number of rows returned by the LibraDex retrieval phase.<br/></param>
    /// <param name="buildElapsed">The condition-builder construction duration.<br/></param>
    /// <param name="retrieveElapsed">The LibraDex retrieval duration.<br/></param>
    /// <param name="formatElapsed">The projection or display-formatting duration included in this query path.<br/></param>
    /// <param name="selectedIndex">The selected physical index for tuple reads, or a compact route family label for identity reads.<br/></param>
    private void LogConditionBuilderQueryUse(
        string outputMode,
        IReadOnlyList<QueryRow> rows,
        FileSearchIdentityMode identityMode,
        int skip,
        int? take,
        int outputRows,
        TimeSpan buildElapsed,
        TimeSpan retrieveElapsed,
        TimeSpan formatElapsed,
        string selectedIndex)
    {
        FileSearchLog.Info(
            $"Query index use: Output={outputMode}; Path=ConditionBuilder; Identity={identityMode}; Fields={FormatQueryFields(rows)}; Operators={FormatQueryOperators(rows)}; " +
            $"RequestedIndexes={FormatQueryIndexList(rows, identityMode)}; SelectedIndex={selectedIndex}; Strategy={DescribeConditionBuilderStrategy(rows, identityMode)}; " +
            $"Skip={skip:n0}; Take={FormatTake(take)}; OutputRows={outputRows:n0}; Build={FormatDuration(buildElapsed)}; Retrieve={FormatDuration(retrieveElapsed)}; Format={FormatDuration(formatElapsed)}; Total={FormatDuration(buildElapsed + retrieveElapsed + formatElapsed)}.");
    }

    /// <summary>
    /// Logs workbench presentation cost after raw identity discovery has completed.<br/>
    /// This makes slow stress records distinguish LibraDex retrieval from sidecar dictionary joins, path decoding, hydration, and display-row formatting.<br/>
    /// </summary>
    /// <param name="outputMode">The user-visible output shape being produced.<br/></param>
    /// <param name="rows">The query rows that produced the identities.<br/></param>
    /// <param name="identityMode">The active identity mode for this catalog.<br/></param>
    /// <param name="inputRows">The number of identity rows entering presentation work.<br/></param>
    /// <param name="outputRows">The number of rows emitted after presentation work.<br/></param>
    /// <param name="elapsed">The presentation phase duration.<br/></param>
    private static void LogQueryPresentationUse(string outputMode, IReadOnlyList<QueryRow> rows, FileSearchIdentityMode identityMode, int inputRows, int outputRows, TimeSpan elapsed)
    {
        FileSearchLog.Info($"Query presentation use: Output={outputMode}; Identity={identityMode}; Fields={FormatQueryFields(rows)}; InputRows={inputRows:n0}; OutputRows={outputRows:n0}; Elapsed={FormatDuration(elapsed)}.");
    }

    /// <summary>
    /// Describes the likely query execution path used by a stress case.<br/>
    /// The string is compact and log-oriented so slow warnings can be scanned without opening the full condition preview.<br/>
    /// </summary>
    /// <param name="rows">The stress query rows.</param>
    /// <param name="options">The read options used for the stress mode.</param>
    /// <returns>A compact description of direct-reader versus condition-builder path, index names, and projection strategy.</returns>
    private string DescribeStressQueryPath(IReadOnlyList<QueryRow> rows, FileSearchReadOptions options)
    {
        if (options.IdentityMode == FileSearchIdentityMode.PathString && HasPathIdentityVarKeyRows(rows))
        {
            return $"DirectVV+Bridge; Indexes={FormatQueryIndexList(rows, options.IdentityMode)}; Strategy={DescribeConditionBuilderStrategy(rows, options.IdentityMode)}";
        }

        return $"ConditionBuilder; Indexes={FormatQueryIndexList(rows, options.IdentityMode)}; Strategy={DescribeConditionBuilderStrategy(rows, options.IdentityMode)}";
    }

    /// <summary>
    /// Counts one single-row query through the raw PathString cursor path without copying identities or formatting display values.<br/>
    /// String-key rows use the direct `VV` plan; scalar rows use the underlying `SV8` range reader with UInt64-encoded bounds.<br/>
    /// </summary>
    /// <param name="row">The single query row to count.<br/></param>
    /// <returns>The raw row count, byte totals, elapsed time, and physical path label.<br/></returns>
    private StressRawProbe CountRawQueryForStress(QueryRow row)
    {
        return IsPathIdentityVarKeyField(row.Field)
            ? CountRawVarKeyQueryForStress(row)
            : CountRawScalarPathIdentityQueryForStress(row);
    }

    /// <summary>
    /// Counts one raw `VV` string-key query by scanning only key and identity spans exposed by the range reader.<br/>
    /// Filtered projections still evaluate against byte spans, so this avoids UTF-8 string materialization in the measurement path.<br/>
    /// </summary>
    /// <param name="row">The string-key query row.<br/></param>
    /// <returns>The raw row count, byte totals, elapsed time, and physical path label.<br/></returns>
    private StressRawProbe CountRawVarKeyQueryForStress(QueryRow row)
    {
        VarKeyVarIdentityQueryPlan plan = CreatePathIdentityVarKeyQueryPlan(row);
        Stopwatch watch = Stopwatch.StartNew();
        using VarKeyVarIdentityRangeReader reader = plan.Index.OpenRangeReader(plan.LowerKey, plan.UpperKey);
        long rows = 0;
        long keyBytes = 0;
        long identityBytes = 0;
        while (reader.MoveNext())
        {
            if (plan.FilterKeys && !PathIdentityVarKeyMatches(reader.CurrentKey, row, plan))
            {
                continue;
            }

            rows++;
            keyBytes += reader.CurrentKeyLength;
            identityBytes += reader.CurrentIdentityLength;
        }

        watch.Stop();
        return new StressRawProbe(rows, keyBytes, identityBytes, watch.Elapsed, $"VV/{plan.Projection}");
    }

    /// <summary>
    /// Counts one raw `SV8` scalar-key query by expanding the query row to an inclusive UInt64 range.<br/>
    /// The probe reads key and identity lengths from the cursor only, separating routed index potential from byte-array or string materialization.<br/>
    /// </summary>
    /// <param name="row">The scalar-key query row.<br/></param>
    /// <returns>The raw row count, byte totals, elapsed time, and physical path label.<br/></returns>
    private StressRawProbe CountRawScalarPathIdentityQueryForStress(QueryRow row)
    {
        if (!TryCreateScalarQueryRange(row, out ulong lowerKey, out ulong upperKey))
        {
            return new StressRawProbe(0, 0, 0, TimeSpan.Zero, "SV8/unsupported");
        }

        Stopwatch watch = Stopwatch.StartNew();
        LibraDexUInt64VarIdentityIndex index = GetPathIdentityScalarIndex(row.Field);
        long rows = index.CountRawTuplesForDiagnostics(lowerKey, upperKey, out long identityBytes);
        watch.Stop();
        return new StressRawProbe(rows, rows * sizeof(ulong), identityBytes, watch.Elapsed, "SV8");
    }

    /// <summary>
    /// Formats the query fields as a stable comma-separated list.<br/>
    /// This avoids allocating LINQ iterator state in the stress logging path and keeps repeated log lines easy to correlate.<br/>
    /// </summary>
    private static string FormatQueryFields(IReadOnlyList<QueryRow> rows)
    {
        StringBuilder builder = new(rows.Count * 12);
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(rows[i].Field);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Formats the query operators as a stable comma-separated list.<br/>
    /// Keeping this separate from field names makes log scanning simpler when the same field is tested across all operators.<br/>
    /// </summary>
    private static string FormatQueryOperators(IReadOnlyList<QueryRow> rows)
    {
        StringBuilder builder = new(rows.Count * 14);
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(rows[i].Operator);
            if (rows[i].IgnoreCase)
            {
                builder.Append(":ignore");
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Formats the logical index names requested by one query.<br/>
    /// The names match the workbench catalog setup names, which lets stress logs be compared directly with reindex insert telemetry.<br/>
    /// </summary>
    private static string FormatQueryIndexList(IReadOnlyList<QueryRow> rows, FileSearchIdentityMode identityMode)
    {
        StringBuilder builder = new(rows.Count * 18);
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(GetLogicalIndexName(rows[i].Field, identityMode));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Returns the logical index name for one FileSearch field and identity mode.<br/>
    /// Synthetic UInt64 catalogs use the primary scalar/string indexes; PathString catalogs use `VV` for string-key fields and `SV8` for scalar fields.<br/>
    /// </summary>
    private static string GetLogicalIndexName(SearchField field, FileSearchIdentityMode identityMode)
    {
        string name = field switch
        {
            SearchField.FileName => "fileName",
            SearchField.Extension => "extension",
            SearchField.Size => "size",
            SearchField.CreatedUtc => "createdUtc",
            SearchField.ModifiedUtc => "modifiedUtc",
            SearchField.AccessedUtc => "accessedUtc",
            SearchField.Attributes => "attributes",
            _ => field.ToString()
        };
        if (identityMode == FileSearchIdentityMode.PathString)
        {
            return field == SearchField.FileName || field == SearchField.Extension ? $"{name}:VV" : $"{name}:SV8";
        }

        return $"{name}:native";
    }

    /// <summary>
    /// Describes the expected projection behavior for string query rows and the base strategy for scalar query rows.<br/>
    /// This is a workbench-side expectation, not a core planner trace, but it makes slow stress warnings actionable when folded or sort-key subindexes are enabled.<br/>
    /// </summary>
    private string DescribeConditionBuilderStrategy(IReadOnlyList<QueryRow> rows, FileSearchIdentityMode identityMode)
    {
        StringBuilder builder = new(rows.Count * 24);
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            QueryRow row = rows[i];
            builder.Append(row.Field).Append('=');
            if (row.Field != SearchField.FileName && row.Field != SearchField.Extension)
            {
                builder.Append(identityMode == FileSearchIdentityMode.PathString ? "scalar-varid" : "scalar-u64id");
                continue;
            }

            if (!row.IgnoreCase)
            {
                builder.Append("exact");
                continue;
            }

            if ((row.Operator == SearchOperator.EqualTo || row.Operator == SearchOperator.StartsWith) && state.IndexStringFolded)
            {
                builder.Append("folded");
            }
            else if (row.Operator == SearchOperator.Contains && state.IndexStringFolded)
            {
                builder.Append("folded-scan");
            }
            else if ((row.Operator == SearchOperator.GreaterOrEqual || row.Operator == SearchOperator.LessOrEqual || row.Operator == SearchOperator.Between) && state.IndexStringSortKey)
            {
                builder.Append("sortkey");
            }
            else
            {
                builder.Append("scan");
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Formats an optional take value for query-use logs.<br/>
    /// The stress tool uses `0` in the UI as unlimited, which reaches this method as <see langword="null"/>.<br/>
    /// </summary>
    private static string FormatTake(int? take)
        => take.HasValue ? take.Value.ToString("n0", CultureInfo.InvariantCulture) : "all";

    /// <summary>
    /// Builds an all-rows condition for one selected index field.<br/>
    /// This condition keeps tuple enumeration sourced from LibraDex rather than from the FileSearch sidecar cache.<br/>
    /// </summary>
    private static LibraDexConditionEndCondition BuildAllCondition(SearchField field, FileSearchIdentityMode identityMode)
    {
        return field switch
        {
            SearchField.FileName when identityMode == FileSearchIdentityMode.SyntheticUInt64 => LibraDexCondition.Group(GroupName).Where("fileName").AsString.All().EndCondition,
            SearchField.Extension when identityMode == FileSearchIdentityMode.SyntheticUInt64 => LibraDexCondition.Group(GroupName).Where("extension").AsString.All().EndCondition,
            SearchField.FileName or SearchField.Extension => throw new NotSupportedException("PathString filename and extension tuple enumeration use the direct raw VV reader path until a condition-builder VV tuple facade is promoted."),
            SearchField.Size => LibraDexCondition.Group(GroupName).Where("size").AsUInt64.All().EndCondition,
            SearchField.CreatedUtc => LibraDexCondition.Group(GroupName).Where("createdUtc").AsUInt64.All().EndCondition,
            SearchField.ModifiedUtc => LibraDexCondition.Group(GroupName).Where("modifiedUtc").AsUInt64.All().EndCondition,
            SearchField.AccessedUtc => LibraDexCondition.Group(GroupName).Where("accessedUtc").AsUInt64.All().EndCondition,
            SearchField.Attributes when identityMode == FileSearchIdentityMode.PathString => LibraDexCondition.Group(GroupName).Where("attributes").AsUInt64.All().EndCondition,
            SearchField.Attributes => LibraDexCondition.Group(GroupName).Where("attributes").AsUInt32.All().EndCondition,
            _ => throw new NotSupportedException($"Unsupported index enumeration field {field}.")
        };
    }

    /// <summary>
    /// Resolves the active runtime index handle for one FileSearch field and identity mode.<br/>
    /// The returned handle is used only for tuple projection, so it preserves the selected physical index as the single source of key/identity rows.<br/>
    /// </summary>
    private IIndex GetRuntimeIndex(SearchField field, FileSearchIdentityMode identityMode)
    {
        if (identityMode == FileSearchIdentityMode.PathString)
        {
            return field switch
            {
                SearchField.Size => sizePathIdentityIndex,
                SearchField.CreatedUtc => createdUtcPathIdentityIndex,
                SearchField.ModifiedUtc => modifiedUtcPathIdentityIndex,
                SearchField.AccessedUtc => accessedUtcPathIdentityIndex,
                SearchField.Attributes => attributesPathIdentityIndex,
                SearchField.FileName or SearchField.Extension => throw new NotSupportedException("PathString filename and extension tuple enumeration use the direct raw VV reader path until a condition-builder VV tuple facade is promoted."),
                _ => throw new NotSupportedException($"Unsupported path-identity index field {field}.")
            };
        }

        return field switch
        {
            SearchField.FileName => fileNameIndex,
            SearchField.Extension => extensionIndex,
            SearchField.Size => sizeIndex,
            SearchField.CreatedUtc => createdUtcIndex,
            SearchField.ModifiedUtc => modifiedUtcIndex,
            SearchField.AccessedUtc => accessedUtcIndex,
            SearchField.Attributes => attributesIndex,
            _ => throw new NotSupportedException($"Unsupported synthetic identity index field {field}.")
        };
    }

    /// <summary>
    /// Resolves one PathString scalar-key index for raw stress and route diagnostics.<br/>
    /// Filename and extension are deliberately excluded because they use the raw `VV` index family instead of `SV8`.<br/>
    /// </summary>
    /// <param name="field">The scalar FileSearch field.<br/></param>
    /// <returns>The active UInt64-key/path-identity index for the requested field.<br/></returns>
    private LibraDexUInt64VarIdentityIndex GetPathIdentityScalarIndex(SearchField field)
    {
        return field switch
        {
            SearchField.Size => sizePathIdentityIndex,
            SearchField.CreatedUtc => createdUtcPathIdentityIndex,
            SearchField.ModifiedUtc => modifiedUtcPathIdentityIndex,
            SearchField.AccessedUtc => accessedUtcPathIdentityIndex,
            SearchField.Attributes => attributesPathIdentityIndex,
            _ => throw new NotSupportedException($"Path identity scalar diagnostics are only connected for UInt64-backed scalar fields. Field={field}.")
        };
    }

    /// <summary>
    /// Describes where one exact scalar key is physically present in the selected path-identity `SV8` index.<br/>
    /// This is a runner/workbench diagnostic for route-pruning correctness; it intentionally bypasses sidecar hydration and condition-builder execution.<br/>
    /// </summary>
    /// <param name="field">The scalar file property index to inspect.<br/></param>
    /// <param name="encodedKey">The exact encoded UInt64 key to locate.<br/></param>
    /// <param name="maxRows">The maximum number of matching identity rows to print.<br/></param>
    /// <returns>A multi-line route diagnostic report.</returns>
    internal string DescribePathIdentityExactKeyRoutes(SearchField field, ulong encodedKey, int maxRows)
    {
        EnsureCatalogIdentityMode(FileSearchIdentityMode.PathString);
        LibraDexUInt64VarIdentityIndex index = field switch
        {
            SearchField.Size => sizePathIdentityIndex,
            SearchField.CreatedUtc => createdUtcPathIdentityIndex,
            SearchField.ModifiedUtc => modifiedUtcPathIdentityIndex,
            SearchField.AccessedUtc => accessedUtcPathIdentityIndex,
            SearchField.Attributes => attributesPathIdentityIndex,
            _ => throw new NotSupportedException($"Path identity route diagnostics are only connected for UInt64-backed scalar fields. Field={field}.")
        };
        return index.DescribeExactKeyRoutes(encodedKey, maxRows);
    }

    /// <summary>
    /// Deletes one exact scalar key from the selected path-identity `SV8` index for route-pruning diagnostics.<br/>
    /// The FileSearch runner uses this only on disposable catalogs after read validation, so the workbench sidecar records are intentionally not updated.<br/>
    /// </summary>
    /// <param name="field">The scalar path-identity index field to mutate.<br/></param>
    /// <param name="encodedKey">The exact encoded UInt64 key to delete.<br/></param>
    /// <returns>The number of index rows deleted.</returns>
    internal long DeletePathIdentityExactKeyForDiagnostics(SearchField field, ulong encodedKey)
    {
        EnsureCatalogIdentityMode(FileSearchIdentityMode.PathString);
        LibraDexUInt64VarIdentityIndex index = field switch
        {
            SearchField.Size => sizePathIdentityIndex,
            SearchField.CreatedUtc => createdUtcPathIdentityIndex,
            SearchField.ModifiedUtc => modifiedUtcPathIdentityIndex,
            SearchField.AccessedUtc => accessedUtcPathIdentityIndex,
            SearchField.Attributes => attributesPathIdentityIndex,
            _ => throw new NotSupportedException($"Path identity delete diagnostics are only connected for UInt64-backed scalar fields. Field={field}.")
        };
        return index.DeleteExactKeyForDiagnostics(encodedKey);
    }

    /// <summary>
    /// Enumerates key/path tuples from the raw `VV` path-identity filename or extension index.<br/>
    /// This keeps PathString workbench enumeration sourced from LibraDex storage even though the promoted condition-builder facade for string-key/blob-identity tuples is not connected yet.<br/>
    /// </summary>
    private IReadOnlyList<FileSearchTupleRow> EnumeratePathIdentityVarKeyTuples(SearchField field, int skip, int length, FileSearchReadOptions options)
    {
        VarKeyVarIdentityIndex index = field switch
        {
            SearchField.FileName => fileNamePathIdentityIndex,
            SearchField.Extension => extensionPathIdentityIndex,
            _ => throw new NotSupportedException($"PathString raw VV tuple enumeration is only connected for filename and extension. Field={field}.")
        };

        int take = NormalizeTake(length) ?? int.MaxValue;
        if (take == 0)
        {
            return Array.Empty<FileSearchTupleRow>();
        }

        using VarKeyVarIdentityRangeReader reader = index.OpenRangeReader(Array.Empty<byte>(), CreateMaxVarKeyBound(index.MaxKeyLength));
        if (skip > 0)
        {
            _ = reader.Skip(skip);
        }

        List<FileSearchTupleRow> rows = new(take == int.MaxValue ? Math.Min(reader.Count, 4096) : take);
        while (rows.Count < take && reader.MoveNext())
        {
            rows.Add(new FileSearchTupleRow
            {
                Key = FormatBytesValue(reader.CurrentKey, options.KeyDisplayMode),
                Identity = FormatBytesValue(reader.CurrentIdentity, options.IdentityDisplayMode)
            });
        }

        return rows;
    }

    /// <summary>
    /// Enumerates raw `VV` path-identity tuples for stress telemetry with reader traversal and formatting timed separately.<br/>
    /// The reader pass copies current key and identity bytes because the range reader exposes spans whose backing storage changes as shelves advance.<br/>
    /// </summary>
    private StressEnumerationProbe EnumeratePathIdentityVarKeyTuplesForStress(SearchField field, int skip, int length, FileSearchReadOptions options)
    {
        VarKeyVarIdentityIndex index = field switch
        {
            SearchField.FileName => fileNamePathIdentityIndex,
            SearchField.Extension => extensionPathIdentityIndex,
            _ => throw new NotSupportedException($"PathString raw VV tuple enumeration is only connected for filename and extension. Field={field}.")
        };

        int take = NormalizeTake(length) ?? int.MaxValue;
        if (take == 0)
        {
            return new StressEnumerationProbe(Array.Empty<FileSearchTupleRow>(), TimeSpan.Zero, TimeSpan.Zero);
        }

        Stopwatch readWatch = Stopwatch.StartNew();
        using VarKeyVarIdentityRangeReader reader = index.OpenRangeReader(Array.Empty<byte>(), CreateMaxVarKeyBound(index.MaxKeyLength));
        if (skip > 0)
        {
            _ = reader.Skip(skip);
        }

        List<(byte[] Key, byte[] Identity)> rawRows = new(take == int.MaxValue ? Math.Min(reader.Count, 4096) : take);
        while (rawRows.Count < take && reader.MoveNext())
        {
            rawRows.Add((reader.CurrentKey.ToArray(), reader.CurrentIdentity.ToArray()));
        }

        readWatch.Stop();
        Stopwatch formatWatch = Stopwatch.StartNew();
        FileSearchTupleRow[] rows = new FileSearchTupleRow[rawRows.Count];
        for (int i = 0; i < rawRows.Count; i++)
        {
            rows[i] = new FileSearchTupleRow
            {
                Key = FormatBytesValue(rawRows[i].Key, options.KeyDisplayMode),
                Identity = FormatBytesValue(rawRows[i].Identity, options.IdentityDisplayMode)
            };
        }

        formatWatch.Stop();
        return new StressEnumerationProbe(rows, readWatch.Elapsed, formatWatch.Elapsed);
    }

    /// <summary>
    /// Counts raw selected-index tuples for stress telemetry without materializing key strings, identity strings, or output rows.<br/>
    /// The method measures the physical cursor potential of the active PathString indexes; formatted tuple enumeration is measured separately by <see cref="EnumerateTuplesForStress"/>.<br/>
    /// </summary>
    /// <param name="field">The selected FileSearch index field.<br/></param>
    /// <param name="skip">The number of rows to skip before counting.<br/></param>
    /// <param name="length">The maximum rows to count, or zero for all rows.<br/></param>
    /// <param name="options">The active stress options, used only for identity-mode validation.<br/></param>
    /// <returns>Raw row count, byte counts, and elapsed cursor duration.</returns>
    private StressRawProbe CountRawTuplesForStress(SearchField field, int skip, int length, FileSearchReadOptions options)
    {
        EnsureCatalogIdentityMode(options.IdentityMode);
        if (options.IdentityMode != FileSearchIdentityMode.PathString)
        {
            return new StressRawProbe(0, 0, 0, TimeSpan.Zero, "unsupported");
        }

        int take = NormalizeTake(length) ?? int.MaxValue;
        if (take == 0)
        {
            return new StressRawProbe(0, 0, 0, TimeSpan.Zero, "empty");
        }

        return IsPathIdentityVarKeyField(field)
            ? CountRawVarKeyTuplesForStress(field, skip, take)
            : CountRawScalarPathIdentityTuplesForStress(field, skip, take);
    }

    /// <summary>
    /// Counts raw `VV` tuples for a filename or extension index without copying key or identity bytes.<br/>
    /// The loop touches span lengths only, which makes it a low-allocation baseline for route traversal and shelf scanning cost.<br/>
    /// </summary>
    /// <param name="field">The variable-key FileSearch field.<br/></param>
    /// <param name="skip">The number of matching rows to skip.<br/></param>
    /// <param name="take">The maximum rows to count.<br/></param>
    /// <returns>Raw row count, key bytes, identity bytes, elapsed time, and source path label.</returns>
    private StressRawProbe CountRawVarKeyTuplesForStress(SearchField field, int skip, int take)
    {
        VarKeyVarIdentityIndex index = field switch
        {
            SearchField.FileName => fileNamePathIdentityIndex,
            SearchField.Extension => extensionPathIdentityIndex,
            _ => throw new NotSupportedException($"PathString raw VV tuple counting is only connected for filename and extension. Field={field}.")
        };

        Stopwatch watch = Stopwatch.StartNew();
        using VarKeyVarIdentityRangeReader reader = index.OpenRangeReader(Array.Empty<byte>(), CreateMaxVarKeyBound(index.MaxKeyLength));
        if (skip > 0)
        {
            _ = reader.Skip(skip);
        }

        long rows = 0;
        long keyBytes = 0;
        long identityBytes = 0;
        while (rows < take && reader.MoveNext())
        {
            rows++;
            keyBytes += reader.CurrentKeyLength;
            identityBytes += reader.CurrentIdentityLength;
        }

        watch.Stop();
        return new StressRawProbe(rows, keyBytes, identityBytes, watch.Elapsed, "VV");
    }

    /// <summary>
    /// Counts raw `SV8` path-identity tuples for one scalar-key index without copying identity bytes.<br/>
    /// This is the scalar counterpart to the raw `VV` probe and gives date/size/attribute stress cases a byte-native baseline.<br/>
    /// </summary>
    /// <param name="field">The scalar FileSearch field.<br/></param>
    /// <param name="skip">The number of matching rows to skip.</param>
    /// <param name="take">The maximum rows to count.<br/></param>
    /// <returns>Raw row count, key bytes, identity bytes, elapsed time, and source path label.</returns>
    private StressRawProbe CountRawScalarPathIdentityTuplesForStress(SearchField field, int skip, int take)
    {
        LibraDexUInt64VarIdentityIndex index = GetPathIdentityScalarIndex(field);
        Stopwatch watch = Stopwatch.StartNew();
        long rows;
        long identityBytes;
        if (skip == 0 && take == int.MaxValue)
        {
            rows = index.CountRawTuplesForDiagnostics(0, ulong.MaxValue, out identityBytes);
        }
        else
        {
            using Scalar8VarIdentityRangeReader reader = index.OpenRawRangeReaderForDiagnostics(0, ulong.MaxValue);
            if (skip > 0)
            {
                _ = reader.Skip(skip);
            }

            rows = 0;
            identityBytes = 0;
            while (rows < take && reader.MoveNext())
            {
                rows++;
                identityBytes += reader.CurrentIdentityLength;
            }
        }

        watch.Stop();
        return new StressRawProbe(rows, rows * sizeof(ulong), identityBytes, watch.Elapsed, "SV8");
    }

    /// <summary>
    /// Executes PathString identity queries when at least one row targets a raw `VV` string-key index.<br/>
    /// The condition-builder facade does not currently expose string-key/varlen-identity `VV`, so this workbench path reads each target index directly and intersects path identity bytes in process.<br/>
    /// Scalar rows still use the promoted condition-builder path so mixed queries continue to exercise the normal `SV8` retrieval surface for those fields.<br/>
    /// </summary>
    /// <param name="rows">The AND-connected query rows supplied by the workbench.<br/></param>
    /// <param name="skip">The number of distinct path identities to skip after intersection.<br/></param>
    /// <param name="take">The optional number of distinct path identities to return after intersection.<br/></param>
    /// <param name="preview">Receives the query preview shown in the workbench log and preview panel.<br/></param>
    /// <returns>The distinct UTF-8 path identity byte arrays matching all supplied rows.</returns>
    private IReadOnlyList<byte[]> ExecutePathIdentityQuery(IReadOnlyList<QueryRow> rows, int skip, int? take, out string preview)
    {
        List<string> fragments = new(rows.Count);
        List<byte[]>? identities = null;
        for (int i = 0; i < rows.Count; i++)
        {
            IReadOnlyList<byte[]> rowIdentities = IsPathIdentityVarKeyField(rows[i].Field)
                ? ExecutePathIdentityVarKeyIdentityQuery(rows[i], out string fragment)
                : ExecutePathIdentityScalarIdentityQuery(rows[i], out fragment);
            fragments.Add(i == 0 ? fragment : $"AND {fragment}");
            identities = identities is null
                ? DistinctByteIdentities(rowIdentities)
                : IntersectByteIdentities(identities, rowIdentities);
            if (identities.Count == 0)
            {
                break;
            }
        }

        preview = $"WorkbenchPathStringQuery({string.Join(" ", fragments)})";
        return SliceByteIdentities(identities ?? new List<byte[]>(), skip, take);
    }

    /// <summary>
    /// Executes a single raw `VV` string-key row as key/path tuples for SelectedFields query display.<br/>
    /// This keeps the tuple grid single-sourced from the selected LibraDex index instead of hydrating file records or projecting from the sidecar cache.<br/>
    /// </summary>
    /// <param name="row">The single FileName or Extension query row.<br/></param>
    /// <param name="skip">The number of matching tuples to skip.<br/></param>
    /// <param name="take">The optional maximum number of tuples to return.<br/></param>
    /// <param name="options">The display options used to format raw key and identity bytes.<br/></param>
    /// <returns>The formatted key/path tuples and the workbench query preview.</returns>
    private TupleQueryResult ExecutePathIdentityVarKeyTupleQuery(QueryRow row, int skip, int? take, FileSearchReadOptions options)
    {
        Stopwatch watch = Stopwatch.StartNew();
        VarKeyVarIdentityQueryPlan plan = CreatePathIdentityVarKeyQueryPlan(row);
        string preview = CreatePathIdentityVarKeyPreview(row);
        int limit = take ?? int.MaxValue;
        if (limit == 0)
        {
            return new TupleQueryResult(Array.Empty<FileSearchTupleRow>(), preview);
        }

        using VarKeyVarIdentityRangeReader reader = plan.Index.OpenRangeReader(plan.LowerKey, plan.UpperKey);
        int readerCount = reader.Count;
        int requestedSkip = skip;
        int readerSkipped = 0;
        if (!plan.FilterKeys && skip > 0)
        {
            readerSkipped = reader.Skip(skip);
            skip = 0;
        }

        List<FileSearchTupleRow> rows = new(limit == int.MaxValue ? Math.Min(readerCount, 4096) : limit);
        int scanned = 0;
        int skipped = 0;
        int filterRejected = 0;
        while (rows.Count < limit && reader.MoveNext())
        {
            scanned++;
            if (plan.FilterKeys && !PathIdentityVarKeyMatches(reader.CurrentKey, row, plan))
            {
                filterRejected++;
                continue;
            }

            if (skipped < skip)
            {
                skipped++;
                continue;
            }

            rows.Add(new FileSearchTupleRow
            {
                Key = FormatBytesValue(reader.CurrentKey, options.KeyDisplayMode),
                Identity = FormatBytesValue(reader.CurrentIdentity, options.IdentityDisplayMode)
            });
        }

        watch.Stop();
        LogPathIdentityVarKeyQueryUse("Tuple", row, plan, readerCount, requestedSkip, readerSkipped, scanned, filterRejected, skipped, rows.Count, take, watch.Elapsed);
        return new TupleQueryResult(rows, preview);
    }

    /// <summary>
    /// Executes one FileName or Extension query row over its raw `VV` path-identity index and materializes only matching identity bytes.<br/>
    /// Case-sensitive exact, prefix, and range operators use bounded reads; ignore-case and contains operators scan/filter for correctness because no folded `VV` index is stored yet.<br/>
    /// </summary>
    /// <param name="row">The FileName or Extension query row to execute.<br/></param>
    /// <param name="preview">Receives the raw `VV` query fragment used in the workbench preview.<br/></param>
    /// <returns>The UTF-8 path identity bytes matched by the row.</returns>
    private IReadOnlyList<byte[]> ExecutePathIdentityVarKeyIdentityQuery(QueryRow row, out string preview)
    {
        Stopwatch watch = Stopwatch.StartNew();
        VarKeyVarIdentityQueryPlan plan = CreatePathIdentityVarKeyQueryPlan(row);
        preview = CreatePathIdentityVarKeyPreview(row);
        using VarKeyVarIdentityRangeReader reader = plan.Index.OpenRangeReader(plan.LowerKey, plan.UpperKey);
        int readerCount = reader.Count;
        List<byte[]> identities = new(Math.Min(readerCount, 4096));
        int scanned = 0;
        int filterRejected = 0;
        while (reader.MoveNext())
        {
            scanned++;
            if (plan.FilterKeys && !PathIdentityVarKeyMatches(reader.CurrentKey, row, plan))
            {
                filterRejected++;
                continue;
            }

            identities.Add(reader.CurrentIdentity.ToArray());
        }

        watch.Stop();
        LogPathIdentityVarKeyQueryUse("Identity", row, plan, readerCount, requestedSkip: 0, readerSkipped: 0, scanned, filterRejected, postFilterSkipped: 0, identities.Count, take: null, watch.Elapsed);
        return identities;
    }

    /// <summary>
    /// Executes one scalar PathString identity query row through the existing condition-builder path.<br/>
    /// This helper is used only by the mixed direct-`VV` query path so scalar fields keep using their normal `SV8` index semantics.<br/>
    /// </summary>
    /// <param name="row">The scalar query row to execute.<br/></param>
    /// <param name="preview">Receives the condition-builder fragment for the scalar row.<br/></param>
    /// <returns>The UTF-8 path identity bytes matched by the scalar row.</returns>
    private IReadOnlyList<byte[]> ExecutePathIdentityScalarIdentityQuery(QueryRow row, out string preview)
    {
        Stopwatch watch = Stopwatch.StartNew();
        LibraDexConditionClause clause = LibraDexCondition.Group(GroupName);
        LibraDexConditionContinueOrEnd continuation = ApplyClause(clause, row, FileSearchIdentityMode.PathString, out string fragment);
        preview = $"LibraDexCondition.Group(\"{GroupName}\").{fragment}.EndCondition";
        IReadOnlyList<byte[]> identities = catalog
            .IndexSet(GroupName)
            .GetIdentities<byte[]>(
                continuation.EndCondition,
                IdentityResultOrdering.PlanNatural,
                IdentityDeduplication.Distinct);
        watch.Stop();
        FileSearchLog.Info($"Query index use: Output=Identity; Path=ConditionBuilder; Field={row.Field}; Operator={row.Operator}; Identity=PathString; OutputRows={identities.Count:n0}; Elapsed={FormatDuration(watch.Elapsed)}; Fragment={fragment}.");
        return identities;
    }

    /// <summary>
    /// Returns whether any query row targets the PathString raw `VV` FileName or Extension indexes.<br/>
    /// The answer decides whether the workbench can use the promoted condition-builder path or must bridge through direct `VV` readers.<br/>
    /// </summary>
    private static bool HasPathIdentityVarKeyRows(IReadOnlyList<QueryRow> rows)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            if (IsPathIdentityVarKeyField(rows[i].Field))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns whether the field is backed by a raw variable-key/variable-identity index in PathString identity mode.<br/>
    /// </summary>
    private static bool IsPathIdentityVarKeyField(SearchField field)
        => field == SearchField.FileName || field == SearchField.Extension;

    /// <summary>
    /// Resolves the raw `VV` path-identity index for a FileName or Extension field.<br/>
    /// </summary>
    private VarKeyVarIdentityIndex GetPathIdentityVarKeyIndex(SearchField field)
    {
        return field switch
        {
            SearchField.FileName => fileNamePathIdentityIndex,
            SearchField.Extension => extensionPathIdentityIndex,
            _ => throw new NotSupportedException($"PathString raw VV query is only connected for filename and extension. Field={field}.")
        };
    }

    private VarKeyVarIdentityQueryPlan CreatePathIdentityVarKeyQueryPlan(QueryRow row)
    {
        VarKeyVarIdentityIndex exactIndex = GetPathIdentityVarKeyIndex(row.Field);
        VarKeyVarIdentityIndex index = exactIndex;
        string projection = "exact";
        PathIdentityVarKeyFilterMode filterMode = PathIdentityVarKeyFilterMode.ExactText;
        bool filterKeys = row.Operator == SearchOperator.Contains;
        byte[] value1 = Encoding.UTF8.GetBytes(row.Value1 ?? string.Empty);
        byte[] value2 = Encoding.UTF8.GetBytes(row.Value2 ?? string.Empty);

        if (row.IgnoreCase)
        {
            if ((row.Operator == SearchOperator.EqualTo || row.Operator == SearchOperator.StartsWith) && state.IndexStringFolded)
            {
                index = GetPathIdentityFoldedIndex(row.Field);
                projection = "folded";
                filterMode = PathIdentityVarKeyFilterMode.FoldedText;
                value1 = Encoding.UTF8.GetBytes(FoldString(row.Value1 ?? string.Empty));
                value2 = Encoding.UTF8.GetBytes(FoldString(row.Value2 ?? string.Empty));
                filterKeys = false;
            }
            else if (row.Operator == SearchOperator.GreaterOrEqual || row.Operator == SearchOperator.LessOrEqual || row.Operator == SearchOperator.Between)
            {
                if (state.IndexStringSortKey)
                {
                    index = GetPathIdentitySortKeyIndex(row.Field);
                    projection = "sortkey";
                    filterMode = PathIdentityVarKeyFilterMode.SortKey;
                    value1 = CreateInvariantSortKey(row.Value1 ?? string.Empty);
                    value2 = CreateInvariantSortKey(row.Value2 ?? string.Empty);
                    filterKeys = false;
                }
                else
                {
                    projection = "exact-scan";
                    filterMode = PathIdentityVarKeyFilterMode.ExactText;
                    filterKeys = true;
                }
            }
            else if (row.Operator == SearchOperator.Contains && state.IndexStringFolded)
            {
                index = GetPathIdentityFoldedIndex(row.Field);
                projection = "folded-scan";
                filterMode = PathIdentityVarKeyFilterMode.FoldedText;
                value1 = Encoding.UTF8.GetBytes(FoldString(row.Value1 ?? string.Empty));
                filterKeys = true;
            }
            else
            {
                projection = "exact-scan";
                filterMode = PathIdentityVarKeyFilterMode.ExactText;
                filterKeys = true;
            }
        }

        CreatePathIdentityVarKeyBounds(index, row.Operator, value1, value2, filterKeys, out byte[] lowerKey, out byte[] upperKey);
        return new VarKeyVarIdentityQueryPlan(index, projection, lowerKey, upperKey, filterKeys, filterMode, value1, value2);
    }

    private VarKeyVarIdentityIndex GetPathIdentityFoldedIndex(SearchField field)
    {
        return field switch
        {
            SearchField.FileName => fileNameFoldedPathIdentityIndex,
            SearchField.Extension => extensionFoldedPathIdentityIndex,
            _ => throw new NotSupportedException($"PathString folded raw VV query is only connected for filename and extension. Field={field}.")
        };
    }

    private VarKeyVarIdentityIndex GetPathIdentitySortKeyIndex(SearchField field)
    {
        return field switch
        {
            SearchField.FileName => fileNameSortKeyPathIdentityIndex,
            SearchField.Extension => extensionSortKeyPathIdentityIndex,
            _ => throw new NotSupportedException($"PathString sort-key raw VV query is only connected for filename and extension. Field={field}.")
        };
    }

    /// <summary>
    /// Creates inclusive raw-key range bounds for one PathString `VV` string-key query row.<br/>
    /// The caller must still filter when this method reports <paramref name="filterKeys"/> because ignore-case and contains semantics need per-key inspection.<br/>
    /// </summary>
    private static void CreatePathIdentityVarKeyBounds(VarKeyVarIdentityIndex index, SearchOperator queryOperator, byte[] value1, byte[] value2, bool filterKeys, out byte[] lowerKey, out byte[] upperKey)
    {
        if (filterKeys)
        {
            lowerKey = Array.Empty<byte>();
            upperKey = CreateMaxVarKeyBound(index.MaxKeyLength);
            return;
        }

        switch (queryOperator)
        {
            case SearchOperator.EqualTo:
                lowerKey = value1;
                upperKey = value1;
                break;
            case SearchOperator.StartsWith:
                lowerKey = value1;
                upperKey = CreatePrefixUpperBound(value1, index.MaxKeyLength);
                break;
            case SearchOperator.GreaterOrEqual:
                lowerKey = value1;
                upperKey = CreateMaxVarKeyBound(index.MaxKeyLength);
                break;
            case SearchOperator.LessOrEqual:
                lowerKey = Array.Empty<byte>();
                upperKey = value1;
                break;
            case SearchOperator.Between:
                lowerKey = value1;
                upperKey = value2;
                break;
            default:
                throw new NotSupportedException($"Unsupported PathString raw VV string operator {queryOperator}.");
        }
    }

    /// <summary>
    /// Creates an inclusive upper bound that covers all raw keys sharing a prefix.<br/>
    /// The prefix bytes are copied first and the remaining logical-key capacity is filled with `0xFF` for lexicographic range coverage.<br/>
    /// </summary>
    private static byte[] CreatePrefixUpperBound(ReadOnlySpan<byte> prefix, int maxLogicalKeyLength)
    {
        if (prefix.Length == 0)
        {
            return CreateMaxVarKeyBound(maxLogicalKeyLength);
        }

        byte[] upper = new byte[maxLogicalKeyLength];
        prefix.CopyTo(upper);
        upper.AsSpan(prefix.Length).Fill(byte.MaxValue);
        return upper;
    }

    /// <summary>
    /// Applies a PathString raw `VV` per-key filter using prepared query bytes when the operator is byte-semantic.<br/>
    /// Equality, prefix, and contains filters compare UTF-8 key bytes directly so large scans avoid per-candidate string allocation; ordered fallback keeps the existing invariant-culture string comparison semantics.<br/>
    /// </summary>
    /// <param name="keyBytes">The current raw `VV` key bytes from the range reader.</param>
    /// <param name="row">The query row being evaluated.</param>
    /// <param name="plan">The prepared raw `VV` query plan containing projection-compatible filter bytes.</param>
    /// <returns><see langword="true"/> when the current key satisfies the row filter.</returns>
    private static bool PathIdentityVarKeyMatches(ReadOnlySpan<byte> keyBytes, QueryRow row, VarKeyVarIdentityQueryPlan plan)
    {
        return row.Operator switch
        {
            SearchOperator.EqualTo => keyBytes.SequenceEqual(plan.FilterValue1),
            SearchOperator.StartsWith => keyBytes.StartsWith(plan.FilterValue1),
            SearchOperator.Contains => keyBytes.IndexOf(plan.FilterValue1) >= 0,
            SearchOperator.GreaterOrEqual => PathIdentityVarKeyOrderedCompare(keyBytes, plan.FilterValue1, row.IgnoreCase) >= 0,
            SearchOperator.LessOrEqual => PathIdentityVarKeyOrderedCompare(keyBytes, plan.FilterValue1, row.IgnoreCase) <= 0,
            SearchOperator.Between => PathIdentityVarKeyOrderedCompare(keyBytes, plan.FilterValue1, row.IgnoreCase) >= 0 &&
                PathIdentityVarKeyOrderedCompare(keyBytes, plan.FilterValue2, row.IgnoreCase) <= 0,
            _ => throw new NotSupportedException($"Unsupported PathString raw VV string operator {row.Operator}.")
        };
    }

    /// <summary>
    /// Compares one raw `VV` UTF-8 key to a prepared UTF-8 operand through the existing invariant-culture text fallback.<br/>
    /// This fallback is intentionally isolated to ordered operators that are not safely represented by folded-byte or sort-key range routing in the current workbench path.<br/>
    /// </summary>
    /// <param name="keyBytes">The current raw key bytes.</param>
    /// <param name="valueBytes">The prepared comparison operand bytes.</param>
    /// <param name="ignoreCase">Whether the invariant-culture comparison should ignore case.</param>
    /// <returns>The invariant-culture comparison result.</returns>
    private static int PathIdentityVarKeyOrderedCompare(ReadOnlySpan<byte> keyBytes, ReadOnlySpan<byte> valueBytes, bool ignoreCase)
    {
        string key = Encoding.UTF8.GetString(keyBytes);
        string value = Encoding.UTF8.GetString(valueBytes);
        return string.Compare(key, value, ignoreCase, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Creates a concise raw `VV` query preview fragment for FileName and Extension rows.<br/>
    /// The preview deliberately names the direct reader path so logs do not imply that the condition-builder facade handled this shape.<br/>
    /// </summary>
    private static string CreatePathIdentityVarKeyPreview(QueryRow row)
    {
        string indexName = row.Field == SearchField.FileName ? "fileName" : "extension";
        string suffix = row.IgnoreCase ? ", ignoreCase: true" : string.Empty;
        string value1 = Escape(row.Value1 ?? string.Empty);
        string value2 = Escape(row.Value2 ?? string.Empty);
        return row.Operator switch
        {
            SearchOperator.EqualTo => $"VV(\"{indexName}\").EqualTo(\"{value1}\"{suffix})",
            SearchOperator.StartsWith => $"VV(\"{indexName}\").StartsWith(\"{value1}\"{suffix})",
            SearchOperator.Contains => $"VV(\"{indexName}\").Contains(\"{value1}\"{suffix})",
            SearchOperator.GreaterOrEqual => $"VV(\"{indexName}\").GreaterOrEqual(\"{value1}\"{suffix})",
            SearchOperator.LessOrEqual => $"VV(\"{indexName}\").LessOrEqual(\"{value1}\"{suffix})",
            SearchOperator.Between => $"VV(\"{indexName}\").Between(\"{value1}\", \"{value2}\"{suffix})",
            _ => throw new NotSupportedException($"Unsupported PathString raw VV string operator {row.Operator}.")
        };
    }

    private static void LogPathIdentityVarKeyQueryUse(
        string outputMode,
        QueryRow row,
        VarKeyVarIdentityQueryPlan plan,
        int candidateRows,
        int requestedSkip,
        int readerSkipped,
        int scannedRows,
        int filterRejectedRows,
        int postFilterSkipped,
        int outputRows,
        int? take,
        TimeSpan elapsed)
    {
        FileSearchLog.Info(
            $"Query index use: Output={outputMode}; Path=DirectVV; Field={row.Field}; Operator={row.Operator}; IgnoreCase={row.IgnoreCase}; " +
            $"Index={plan.Index.Name}; Slot={plan.Index.SlotIndex}; Projection={plan.Projection}; Filter={plan.FilterKeys}; FilterMode={plan.FilterMode}; " +
            $"CandidateRows={candidateRows:n0}; RequestedSkip={requestedSkip:n0}; ReaderSkippedRows={readerSkipped:n0}; ScannedRows={scannedRows:n0}; FilterRejectedRows={filterRejectedRows:n0}; PostFilterSkippedRows={postFilterSkipped:n0}; OutputRows={outputRows:n0}; Take={(take.HasValue ? take.Value.ToString("n0", CultureInfo.InvariantCulture) : "all")}; " +
            $"LowerBytes={FormatByteSummary(plan.LowerKey)}; UpperBytes={FormatByteSummary(plan.UpperKey)}; Elapsed={FormatDuration(elapsed)}.");
    }

    /// <summary>
    /// Copies identity rows into insertion order while removing duplicate byte payloads.<br/>
    /// The workbench query API presents distinct identities, matching the normal condition-builder query behavior.<br/>
    /// </summary>
    private static List<byte[]> DistinctByteIdentities(IReadOnlyList<byte[]> source)
    {
        List<byte[]> rows = new(source.Count);
        HashSet<byte[]> seen = new(ByteArrayComparer.Instance);
        for (int i = 0; i < source.Count; i++)
        {
            if (seen.Add(source[i]))
            {
                rows.Add(source[i]);
            }
        }

        return rows;
    }

    /// <summary>
    /// Intersects two identity byte sequences while preserving the left-side order.<br/>
    /// This gives AND-connected mixed PathString queries deterministic workbench output without hydrating records during discovery.<br/>
    /// </summary>
    private static List<byte[]> IntersectByteIdentities(IReadOnlyList<byte[]> left, IReadOnlyList<byte[]> right)
    {
        HashSet<byte[]> rightSet = new(right, ByteArrayComparer.Instance);
        List<byte[]> rows = new(Math.Min(left.Count, right.Count));
        HashSet<byte[]> emitted = new(ByteArrayComparer.Instance);
        for (int i = 0; i < left.Count; i++)
        {
            if (rightSet.Contains(left[i]) && emitted.Add(left[i]))
            {
                rows.Add(left[i]);
            }
        }

        return rows;
    }

    /// <summary>
    /// Applies final query skip/take semantics after identity intersection has completed.<br/>
    /// The returned list preserves the byte-array instances already materialized by the contributing index readers.<br/>
    /// </summary>
    private static IReadOnlyList<byte[]> SliceByteIdentities(IReadOnlyList<byte[]> identities, int skip, int? take)
    {
        int start = Math.Min(Math.Max(skip, 0), identities.Count);
        int count = take is int limit ? Math.Min(Math.Max(limit, 0), identities.Count - start) : identities.Count - start;
        if (start == 0 && count == identities.Count)
        {
            return identities;
        }

        List<byte[]> rows = new(count);
        for (int i = 0; i < count; i++)
        {
            rows.Add(identities[start + i]);
        }

        return rows;
    }

    /// <summary>
    /// Creates an inclusive high logical key bound for a raw `VV` full-index scan.<br/>
    /// The payload is filled with `0xFF` so every non-null logical key up to the configured max length is inside the range.<br/>
    /// </summary>
    private static byte[] CreateMaxVarKeyBound(int maxLogicalKeyLength)
    {
        byte[] upper = new byte[maxLogicalKeyLength];
        Array.Fill(upper, byte.MaxValue);
        return upper;
    }

    private static LibraDexConditionContinueOrEnd ApplyClause(
        LibraDexConditionClause clause,
        QueryRow row,
        FileSearchIdentityMode identityMode,
        out string preview)
    {
        if (identityMode == FileSearchIdentityMode.PathString &&
            (row.Field == SearchField.FileName || row.Field == SearchField.Extension))
        {
            throw new NotSupportedException("PathString identity mode currently indexes scalar file properties through SV8. Filename and extension queries need the planned string-key/VV path-identity facade.");
        }

        return row.Field switch
        {
            SearchField.FileName => ApplyStringClause(clause, "fileName", row, out preview),
            SearchField.Extension => ApplyStringClause(clause, "extension", row, out preview),
            SearchField.Size => ApplyUInt64Clause(clause, "size", row, out preview),
            SearchField.CreatedUtc => ApplyDateTimeTicksClause(clause, "createdUtc", row, out preview),
            SearchField.ModifiedUtc => ApplyDateTimeTicksClause(clause, "modifiedUtc", row, out preview),
            SearchField.AccessedUtc => ApplyDateTimeTicksClause(clause, "accessedUtc", row, out preview),
            SearchField.Attributes => identityMode == FileSearchIdentityMode.PathString
                ? ApplyUInt64Clause(clause, "attributes", row, out preview)
                : ApplyUInt32Clause(clause, "attributes", row, out preview),
            _ => throw new NotSupportedException($"Unsupported query field {row.Field}.")
        };
    }

    /// <summary>
    /// Formats runtime LibraDex tuples for the workbench grid.<br/>
    /// Formatting is deliberately outside the retrieval path so raw tuple reads can be compared with text/byte presentation overhead.<br/>
    /// </summary>
    private static IReadOnlyList<FileSearchTupleRow> FormatTupleRows(IReadOnlyList<LibraDexRuntimeTuple> tuples, SearchField field, FileSearchValueDisplayMode keyDisplayMode, FileSearchValueDisplayMode identityDisplayMode)
    {
        FileSearchTupleRow[] rows = new FileSearchTupleRow[tuples.Count];
        for (int i = 0; i < tuples.Count; i++)
        {
            rows[i] = new FileSearchTupleRow
            {
                Key = FormatKeyValue(tuples[i].Key, field, keyDisplayMode),
                Identity = FormatRuntimeValue(tuples[i].Identity, identityDisplayMode)
            };
        }

        return rows;
    }

    /// <summary>
    /// Formats identity-only query results for the workbench grid.<br/>
    /// Byte-array identities honor the selected render mode so path identity presentation cost remains a visible test knob.<br/>
    /// </summary>
    private static IReadOnlyList<FileSearchIdentityRow> FormatIdentityRows<TIdentity>(IReadOnlyList<TIdentity> identities, FileSearchValueDisplayMode identityDisplayMode)
    {
        FileSearchIdentityRow[] rows = new FileSearchIdentityRow[identities.Count];
        for (int i = 0; i < identities.Count; i++)
        {
            rows[i] = new FileSearchIdentityRow { Identity = FormatRuntimeValue(identities[i], identityDisplayMode) };
        }

        return rows;
    }

    /// <summary>
    /// Formats one runtime key or identity for grid display.<br/>
    /// Byte arrays can be kept byte-oriented, decoded as UTF-8 text, or shown with both views to make presentation overhead explicit.<br/>
    /// </summary>
    private static string FormatRuntimeValue(object? value, FileSearchValueDisplayMode displayMode)
    {
        return value switch
        {
            null => "<null>",
            byte[] bytes => FormatBytesValue(bytes, displayMode),
            string text => FormatStringValue(text, displayMode),
            DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
            ulong value64 => FormatUnsigned(value64, displayMode),
            uint value32 => FormatUnsigned(value32, displayMode),
            long signed64 => FormatSigned(signed64, displayMode),
            int signed32 => FormatSigned(signed32, displayMode),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
            _ => value.ToString() ?? string.Empty
        };
    }

    /// <summary>
    /// Formats a runtime index key according to its logical field type.<br/>
    /// Date fields can be shown as UTC timestamps or raw ticks, while other fields use the general value formatter.<br/>
    /// </summary>
    private static string FormatKeyValue(object? value, SearchField field, FileSearchValueDisplayMode displayMode)
    {
        if (field == SearchField.Extension && value is LibraDexCompositeKey compositeKey && compositeKey.Count > 0)
        {
            return FormatRuntimeValue(compositeKey[0], displayMode);
        }

        if ((field == SearchField.CreatedUtc || field == SearchField.ModifiedUtc || field == SearchField.AccessedUtc) &&
            value is ulong ticks)
        {
            if (displayMode == FileSearchValueDisplayMode.UtcDateTime)
            {
                return FormatUtcTicks(ticks);
            }

            if (displayMode == FileSearchValueDisplayMode.Both)
            {
                return $"{ticks.ToString(CultureInfo.InvariantCulture)} | {FormatUtcTicks(ticks)}";
            }
        }

        return FormatRuntimeValue(value, displayMode);
    }

    /// <summary>
    /// Formats raw byte values according to the workbench identity render mode.<br/>
    /// The hex display is intentionally capped so a path identity column does not allocate massive strings for every row by default.<br/>
    /// </summary>
    private static string FormatBytesValue(byte[] bytes, FileSearchValueDisplayMode displayMode)
        => FormatBytesValue(bytes.AsSpan(), displayMode);

    /// <summary>
    /// Formats raw byte spans according to the workbench identity render mode.<br/>
    /// This overload keeps direct range-reader tuple projection from copying bytes only to display them.<br/>
    /// </summary>
    private static string FormatBytesValue(ReadOnlySpan<byte> bytes, FileSearchValueDisplayMode displayMode)
    {
        return displayMode switch
        {
            FileSearchValueDisplayMode.Text => Encoding.UTF8.GetString(bytes),
            FileSearchValueDisplayMode.RawBytesText => Encoding.Latin1.GetString(bytes),
            FileSearchValueDisplayMode.Both => $"{Encoding.UTF8.GetString(bytes)} | {FormatByteSummary(bytes)}",
            _ => FormatByteSummary(bytes)
        };
    }

    /// <summary>
    /// Formats a string value through the selected workbench display mode.<br/>
    /// Byte modes intentionally encode on demand so raw tuple retrieval cost stays separate from presentation cost.<br/>
    /// </summary>
    private static string FormatStringValue(string value, FileSearchValueDisplayMode displayMode)
    {
        if (displayMode == FileSearchValueDisplayMode.Bytes)
        {
            return FormatByteSummary(Encoding.UTF8.GetBytes(value));
        }

        if (displayMode == FileSearchValueDisplayMode.RawBytesText)
        {
            return value;
        }

        if (displayMode == FileSearchValueDisplayMode.Both)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            return $"{value} | {FormatByteSummary(bytes)}";
        }

        return value;
    }

    /// <summary>
    /// Formats an unsigned integer value in decimal, hex, or both for key/identity test output.<br/>
    /// Keeping this local makes numeric display cost explicit and avoids accidental culture-specific formatting.<br/>
    /// </summary>
    private static string FormatUnsigned(ulong value, FileSearchValueDisplayMode displayMode)
    {
        return displayMode switch
        {
            FileSearchValueDisplayMode.Hex => $"0x{value:X16}",
            FileSearchValueDisplayMode.Both => $"{value.ToString(CultureInfo.InvariantCulture)} | 0x{value:X16}",
            _ => value.ToString(CultureInfo.InvariantCulture)
        };
    }

    /// <summary>
    /// Formats a signed integer value in decimal, hex, or both for key/identity test output.<br/>
    /// Signed values are uncommon in current FileSearch indexes, but this keeps runtime tuple presentation robust.<br/>
    /// </summary>
    private static string FormatSigned(long value, FileSearchValueDisplayMode displayMode)
    {
        return displayMode switch
        {
            FileSearchValueDisplayMode.Hex => $"0x{value:X16}",
            FileSearchValueDisplayMode.Both => $"{value.ToString(CultureInfo.InvariantCulture)} | 0x{value:X16}",
            _ => value.ToString(CultureInfo.InvariantCulture)
        };
    }

    /// <summary>
    /// Formats stored UTC ticks as an ISO 8601 timestamp for date-key display.<br/>
    /// Invalid tick values fall back to their raw integer form so diagnostic grids never throw while rendering rows.<br/>
    /// </summary>
    private static string FormatUtcTicks(ulong ticks)
    {
        if (ticks > (ulong)DateTime.MaxValue.Ticks)
        {
            return ticks.ToString(CultureInfo.InvariantCulture);
        }

        return new DateTime((long)ticks, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Formats a compact byte-length and hex-prefix summary.<br/>
    /// This keeps raw-byte output useful for performance testing without materializing full-path hex strings unless a future setting asks for that explicitly.<br/>
    /// </summary>
    private static string FormatByteSummary(byte[] bytes)
        => FormatByteSummary(bytes.AsSpan());

    /// <summary>
    /// Formats a compact byte-length and hex-prefix summary from a byte span.<br/>
    /// This keeps range-reader output byte-native until the final display string is required.<br/>
    /// </summary>
    private static string FormatByteSummary(ReadOnlySpan<byte> bytes)
    {
        int count = Math.Min(bytes.Length, 32);
        StringBuilder builder = new();
        builder.Append(bytes.Length.ToString(CultureInfo.InvariantCulture)).Append(" bytes");
        if (count > 0)
        {
            builder.Append(" 0x");
            for (int i = 0; i < count; i++)
            {
                builder.Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
            }

            if (count < bytes.Length)
            {
                builder.Append("...");
            }
        }

        return builder.ToString();
    }

    private static LibraDexConditionContinueOrEnd ApplyStringClause(
        LibraDexConditionClause clause,
        string indexName,
        QueryRow row,
        out string preview)
    {
        string value1 = row.Value1 ?? string.Empty;
        string value2 = row.Value2 ?? string.Empty;
        string suffix = row.IgnoreCase ? ", ignoreCase: true" : string.Empty;
        return row.Operator switch
        {
            SearchOperator.EqualTo => WithPreview(clause.Where(indexName).AsString.EqualTo(value1, row.IgnoreCase), $"Where(\"{indexName}\").AsString.EqualTo(\"{Escape(value1)}\"{suffix})", out preview),
            SearchOperator.StartsWith => WithPreview(clause.Where(indexName).AsString.StartsWith(value1, row.IgnoreCase), $"Where(\"{indexName}\").AsString.StartsWith(\"{Escape(value1)}\"{suffix})", out preview),
            SearchOperator.Contains => WithPreview(clause.Where(indexName).AsString.Contains(value1, row.IgnoreCase), $"Where(\"{indexName}\").AsString.Contains(\"{Escape(value1)}\"{suffix})", out preview),
            SearchOperator.Between => WithPreview(clause.Where(indexName).AsString.Between(value1, value2, row.IgnoreCase), $"Where(\"{indexName}\").AsString.Between(\"{Escape(value1)}\", \"{Escape(value2)}\"{suffix})", out preview),
            SearchOperator.GreaterOrEqual => WithPreview(clause.Where(indexName).AsString.GreaterOrEqual(value1, row.IgnoreCase), $"Where(\"{indexName}\").AsString.GreaterOrEqual(\"{Escape(value1)}\"{suffix})", out preview),
            SearchOperator.LessOrEqual => WithPreview(clause.Where(indexName).AsString.LessOrEqual(value1, row.IgnoreCase), $"Where(\"{indexName}\").AsString.LessOrEqual(\"{Escape(value1)}\"{suffix})", out preview),
            _ => throw new NotSupportedException($"Unsupported string operator {row.Operator}.")
        };
    }

    private static LibraDexConditionContinueOrEnd ApplyUInt64Clause(
        LibraDexConditionClause clause,
        string indexName,
        QueryRow row,
        out string preview)
    {
        ulong value1 = ParseUInt64(row.Value1, indexName);
        ulong value2 = row.Operator == SearchOperator.Between ? ParseUInt64(row.Value2, indexName) : 0;
        return row.Operator switch
        {
            SearchOperator.EqualTo => WithPreview(clause.Where(indexName).AsUInt64.EqualTo(value1), $"Where(\"{indexName}\").AsUInt64.EqualTo({value1})", out preview),
            SearchOperator.GreaterOrEqual => WithPreview(clause.Where(indexName).AsUInt64.GreaterOrEqual(value1), $"Where(\"{indexName}\").AsUInt64.GreaterOrEqual({value1})", out preview),
            SearchOperator.LessOrEqual => WithPreview(clause.Where(indexName).AsUInt64.LessOrEqual(value1), $"Where(\"{indexName}\").AsUInt64.LessOrEqual({value1})", out preview),
            SearchOperator.Between => WithPreview(clause.Where(indexName).AsUInt64.Between(value1, value2), $"Where(\"{indexName}\").AsUInt64.Between({value1}, {value2})", out preview),
            _ => throw new NotSupportedException($"Unsupported UInt64 operator {row.Operator}.")
        };
    }

    private static LibraDexConditionContinueOrEnd ApplyUInt32Clause(
        LibraDexConditionClause clause,
        string indexName,
        QueryRow row,
        out string preview)
    {
        uint value1 = ParseUInt32(row.Value1, indexName);
        uint value2 = row.Operator == SearchOperator.Between ? ParseUInt32(row.Value2, indexName) : 0;
        return row.Operator switch
        {
            SearchOperator.EqualTo => WithPreview(clause.Where(indexName).AsUInt32.EqualTo(value1), $"Where(\"{indexName}\").AsUInt32.EqualTo({value1})", out preview),
            SearchOperator.GreaterOrEqual => WithPreview(clause.Where(indexName).AsUInt32.GreaterOrEqual(value1), $"Where(\"{indexName}\").AsUInt32.GreaterOrEqual({value1})", out preview),
            SearchOperator.LessOrEqual => WithPreview(clause.Where(indexName).AsUInt32.LessOrEqual(value1), $"Where(\"{indexName}\").AsUInt32.LessOrEqual({value1})", out preview),
            SearchOperator.Between => WithPreview(clause.Where(indexName).AsUInt32.Between(value1, value2), $"Where(\"{indexName}\").AsUInt32.Between({value1}, {value2})", out preview),
            _ => throw new NotSupportedException($"Unsupported UInt32 operator {row.Operator}.")
        };
    }

    private static LibraDexConditionContinueOrEnd ApplyDateTimeClause(
        LibraDexConditionClause clause,
        string indexName,
        QueryRow row,
        out string preview)
    {
        DateTime value1 = ParseUtcDateTime(row.Value1, indexName);
        DateTime value2 = row.Operator == SearchOperator.Between ? ParseUtcDateTime(row.Value2, indexName) : default;
        return row.Operator switch
        {
            SearchOperator.EqualTo => WithPreview(clause.Where(indexName).AsDate.EqualTo(value1), $"Where(\"{indexName}\").AsDate.EqualTo({FormatDate(value1)})", out preview),
            SearchOperator.GreaterOrEqual => WithPreview(clause.Where(indexName).AsDate.GreaterOrEqual(value1), $"Where(\"{indexName}\").AsDate.GreaterOrEqual({FormatDate(value1)})", out preview),
            SearchOperator.LessOrEqual => WithPreview(clause.Where(indexName).AsDate.LessOrEqual(value1), $"Where(\"{indexName}\").AsDate.LessOrEqual({FormatDate(value1)})", out preview),
            SearchOperator.Between => WithPreview(clause.Where(indexName).AsDate.Between(value1, value2), $"Where(\"{indexName}\").AsDate.Between({FormatDate(value1)}, {FormatDate(value2)})", out preview),
            _ => throw new NotSupportedException($"Unsupported DateTime operator {row.Operator}.")
        };
    }

    private static LibraDexConditionContinueOrEnd ApplyDateTimeTicksClause(
        LibraDexConditionClause clause,
        string indexName,
        QueryRow row,
        out string preview)
    {
        DateTime date1 = ParseUtcDateTime(row.Value1, indexName);
        DateTime date2 = row.Operator == SearchOperator.Between ? ParseUtcDateTime(row.Value2, indexName) : default;
        ulong value1 = ToUtcTicks(date1);
        ulong value2 = row.Operator == SearchOperator.Between ? ToUtcTicks(date2) : 0;
        return row.Operator switch
        {
            SearchOperator.EqualTo => WithPreview(clause.Where(indexName).AsUInt64.EqualTo(value1), $"Where(\"{indexName}\").AsUInt64.EqualTo({value1}) /* {FormatDate(date1)} */", out preview),
            SearchOperator.GreaterOrEqual => WithPreview(clause.Where(indexName).AsUInt64.GreaterOrEqual(value1), $"Where(\"{indexName}\").AsUInt64.GreaterOrEqual({value1}) /* {FormatDate(date1)} */", out preview),
            SearchOperator.LessOrEqual => WithPreview(clause.Where(indexName).AsUInt64.LessOrEqual(value1), $"Where(\"{indexName}\").AsUInt64.LessOrEqual({value1}) /* {FormatDate(date1)} */", out preview),
            SearchOperator.Between => WithPreview(clause.Where(indexName).AsUInt64.Between(value1, value2), $"Where(\"{indexName}\").AsUInt64.Between({value1}, {value2}) /* {FormatDate(date1)}..{FormatDate(date2)} */", out preview),
            _ => throw new NotSupportedException($"Unsupported DateTime operator {row.Operator}.")
        };
    }

    private static LibraDexConditionContinueOrEnd ApplyCompositeStringClause(
        LibraDexConditionClause clause,
        string indexName,
        QueryRow row,
        out string preview)
    {
        string value1 = row.Value1 ?? string.Empty;
        string value2 = row.Value2 ?? string.Empty;
        string suffix = row.IgnoreCase ? ", ignoreCase: true" : string.Empty;
        LibraDexCompositeStringPartCondition part = LibraDexCompositePart.String(indexName);
        return row.Operator switch
        {
            SearchOperator.EqualTo => WithPreview(clause.Where(indexName).Where(part.EqualTo(value1, row.IgnoreCase)), $"Where(\"{indexName}\").Where(CompositePart.String(\"{indexName}\").EqualTo(\"{Escape(value1)}\"{suffix}))", out preview),
            SearchOperator.StartsWith => WithPreview(clause.Where(indexName).Where(part.StartsWith(value1, row.IgnoreCase)), $"Where(\"{indexName}\").Where(CompositePart.String(\"{indexName}\").StartsWith(\"{Escape(value1)}\"{suffix}))", out preview),
            SearchOperator.Contains => WithPreview(clause.Where(indexName).Where(part.Contains(value1, row.IgnoreCase)), $"Where(\"{indexName}\").Where(CompositePart.String(\"{indexName}\").Contains(\"{Escape(value1)}\"{suffix}))", out preview),
            SearchOperator.Between => WithPreview(clause.Where(indexName).Where(part.Between(value1, value2, row.IgnoreCase)), $"Where(\"{indexName}\").Where(CompositePart.String(\"{indexName}\").Between(\"{Escape(value1)}\", \"{Escape(value2)}\"{suffix}))", out preview),
            SearchOperator.GreaterOrEqual => WithPreview(clause.Where(indexName).Where(part.GreaterOrEqual(value1, row.IgnoreCase)), $"Where(\"{indexName}\").Where(CompositePart.String(\"{indexName}\").GreaterOrEqual(\"{Escape(value1)}\"{suffix}))", out preview),
            SearchOperator.LessOrEqual => WithPreview(clause.Where(indexName).Where(part.LessOrEqual(value1, row.IgnoreCase)), $"Where(\"{indexName}\").Where(CompositePart.String(\"{indexName}\").LessOrEqual(\"{Escape(value1)}\"{suffix}))", out preview),
            _ => throw new NotSupportedException($"Unsupported string operator {row.Operator}.")
        };
    }

    private static LibraDexConditionContinueOrEnd ApplyCompositeUInt64Clause(
        LibraDexConditionClause clause,
        string indexName,
        QueryRow row,
        out string preview)
    {
        ulong value1 = ParseUInt64(row.Value1, indexName);
        ulong value2 = row.Operator == SearchOperator.Between ? ParseUInt64(row.Value2, indexName) : 0;
        LibraDexCompositeScalarPartCondition<ulong> part = LibraDexCompositePart.Scalar<ulong>(indexName);
        return row.Operator switch
        {
            SearchOperator.EqualTo => WithPreview(clause.Where(indexName).Where(part.EqualTo(value1)), $"Where(\"{indexName}\").Where(CompositePart.Scalar<ulong>(\"{indexName}\").EqualTo({value1}))", out preview),
            SearchOperator.GreaterOrEqual => WithPreview(clause.Where(indexName).Where(part.GreaterOrEqual(value1)), $"Where(\"{indexName}\").Where(CompositePart.Scalar<ulong>(\"{indexName}\").GreaterOrEqual({value1}))", out preview),
            SearchOperator.LessOrEqual => WithPreview(clause.Where(indexName).Where(part.LessOrEqual(value1)), $"Where(\"{indexName}\").Where(CompositePart.Scalar<ulong>(\"{indexName}\").LessOrEqual({value1}))", out preview),
            SearchOperator.Between => WithPreview(clause.Where(indexName).Where(part.Between(value1, value2)), $"Where(\"{indexName}\").Where(CompositePart.Scalar<ulong>(\"{indexName}\").Between({value1}, {value2}))", out preview),
            _ => throw new NotSupportedException($"Unsupported UInt64 operator {row.Operator}.")
        };
    }

    private static LibraDexConditionContinueOrEnd ApplyCompositeUInt32Clause(
        LibraDexConditionClause clause,
        string indexName,
        QueryRow row,
        out string preview)
    {
        uint value1 = ParseUInt32(row.Value1, indexName);
        uint value2 = row.Operator == SearchOperator.Between ? ParseUInt32(row.Value2, indexName) : 0;
        LibraDexCompositeScalarPartCondition<uint> part = LibraDexCompositePart.Scalar<uint>(indexName);
        return row.Operator switch
        {
            SearchOperator.EqualTo => WithPreview(clause.Where(indexName).Where(part.EqualTo(value1)), $"Where(\"{indexName}\").Where(CompositePart.Scalar<uint>(\"{indexName}\").EqualTo({value1}))", out preview),
            SearchOperator.GreaterOrEqual => WithPreview(clause.Where(indexName).Where(part.GreaterOrEqual(value1)), $"Where(\"{indexName}\").Where(CompositePart.Scalar<uint>(\"{indexName}\").GreaterOrEqual({value1}))", out preview),
            SearchOperator.LessOrEqual => WithPreview(clause.Where(indexName).Where(part.LessOrEqual(value1)), $"Where(\"{indexName}\").Where(CompositePart.Scalar<uint>(\"{indexName}\").LessOrEqual({value1}))", out preview),
            SearchOperator.Between => WithPreview(clause.Where(indexName).Where(part.Between(value1, value2)), $"Where(\"{indexName}\").Where(CompositePart.Scalar<uint>(\"{indexName}\").Between({value1}, {value2}))", out preview),
            _ => throw new NotSupportedException($"Unsupported UInt32 operator {row.Operator}.")
        };
    }

    private static LibraDexConditionContinueOrEnd ApplyCompositeDateTimeClause(
        LibraDexConditionClause clause,
        string indexName,
        QueryRow row,
        out string preview)
    {
        DateTime value1 = ParseUtcDateTime(row.Value1, indexName);
        DateTime value2 = row.Operator == SearchOperator.Between ? ParseUtcDateTime(row.Value2, indexName) : default;
        LibraDexCompositeDatePartCondition part = LibraDexCompositePart.Date(indexName);
        return row.Operator switch
        {
            SearchOperator.EqualTo => WithPreview(clause.Where(indexName).Where(part.EqualTo(value1)), $"Where(\"{indexName}\").Where(CompositePart.Date(\"{indexName}\").EqualTo({FormatDate(value1)}))", out preview),
            SearchOperator.GreaterOrEqual => WithPreview(clause.Where(indexName).Where(part.GreaterOrEqual(value1)), $"Where(\"{indexName}\").Where(CompositePart.Date(\"{indexName}\").GreaterOrEqual({FormatDate(value1)}))", out preview),
            SearchOperator.LessOrEqual => WithPreview(clause.Where(indexName).Where(part.LessOrEqual(value1)), $"Where(\"{indexName}\").Where(CompositePart.Date(\"{indexName}\").LessOrEqual({FormatDate(value1)}))", out preview),
            SearchOperator.Between => WithPreview(clause.Where(indexName).Where(part.Between(value1, value2)), $"Where(\"{indexName}\").Where(CompositePart.Date(\"{indexName}\").Between({FormatDate(value1)}, {FormatDate(value2)}))", out preview),
            _ => throw new NotSupportedException($"Unsupported DateTime operator {row.Operator}.")
        };
    }

    private static LibraDexConditionContinueOrEnd WithPreview(LibraDexConditionContinueOrEnd continuation, string text, out string preview)
    {
        preview = text;
        return continuation;
    }

    private static IEnumerable<string> EnumerateFiles(string root)
    {
        Stack<string> pending = new();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            string[] files;
            try
            {
                files = Directory.GetFiles(directory);
            }
            catch
            {
                continue;
            }

            for (int i = 0; i < files.Length; i++)
            {
                yield return files[i];
            }

            string[] children;
            try
            {
                children = Directory.GetDirectories(directory);
            }
            catch
            {
                continue;
            }

            for (int i = 0; i < children.Length; i++)
            {
                pending.Push(children[i]);
            }
        }
    }

    private static bool TryCreateRecord(string path, ulong id, out FileRecord? record)
    {
        try
        {
            FileInfo info = new(path);
            record = new FileRecord
            {
                Id = id,
                Path = info.FullName,
                FileName = info.Name,
                Extension = NormalizeExtension(info.Extension),
                Size = checked((ulong)Math.Max(0, info.Length)),
                CreatedUtc = DateTime.SpecifyKind(info.CreationTimeUtc, DateTimeKind.Utc),
                ModifiedUtc = DateTime.SpecifyKind(info.LastWriteTimeUtc, DateTimeKind.Utc),
                AccessedUtc = DateTime.SpecifyKind(info.LastAccessTimeUtc, DateTimeKind.Utc),
                Attributes = (uint)info.Attributes
            };
            SetPathHash(record);
            PrepareTransientRecordBytes(record);
            return true;
        }
        catch
        {
            record = null;
            return false;
        }
    }

    private static FileSearchState LoadState(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<FileSearchState>(stream, JsonOptions) ?? new FileSearchState();
    }

    /// <summary>
    /// Rebuilds non-persisted byte caches for sidecar-loaded records.<br/>
    /// These buffers keep PathString insert, sort, and raw tuple work byte-native after JSON serialization drops transient fields.<br/>
    /// </summary>
    private static void PrepareTransientRecordBytes(List<FileRecord> records)
    {
        for (int i = 0; i < records.Count; i++)
        {
            PrepareTransientRecordBytes(records[i]);
        }
    }

    /// <summary>
    /// Prepares optional string projection key bytes for PathString `VV` test indexes.<br/>
    /// The workbench calls this only when the matching reindex knobs are enabled so baseline raw-key runs do not pay projection allocation or culture work.<br/>
    /// </summary>
    /// <param name="records">The collected file records to prepare.<br/></param>
    /// <param name="options">The reindex options controlling which projection key buffers are needed.<br/></param>
    private static void PreparePathIdentityStringProjectionBytes(List<FileRecord> records, FileSearchReindexOptions options)
    {
        if (!options.IndexStringFolded && !options.IndexStringSortKey && !options.IndexStringReversed)
        {
            return;
        }

        for (int i = 0; i < records.Count; i++)
        {
            FileRecord record = records[i];
            if (options.IndexStringFolded)
            {
                string foldedFileName = FoldString(record.FileName);
                string foldedExtension = FoldString(record.Extension);
                record.FileNameFoldedKeyBytes = Encoding.UTF8.GetBytes(foldedFileName);
                record.ExtensionFoldedKeyBytes = Encoding.UTF8.GetBytes(foldedExtension);
                if (options.IndexStringReversed)
                {
                    record.FileNameFoldedReversedKeyBytes = Encoding.UTF8.GetBytes(ReverseString(foldedFileName));
                    record.ExtensionFoldedReversedKeyBytes = Encoding.UTF8.GetBytes(ReverseString(foldedExtension));
                }
            }

            if (options.IndexStringSortKey)
            {
                record.FileNameSortKeyBytes = CreateInvariantSortKey(record.FileName);
                record.ExtensionSortKeyBytes = CreateInvariantSortKey(record.Extension);
            }

            if (options.IndexStringReversed)
            {
                record.FileNameReversedKeyBytes = Encoding.UTF8.GetBytes(ReverseString(record.FileName));
                record.ExtensionReversedKeyBytes = Encoding.UTF8.GetBytes(ReverseString(record.Extension));
            }
        }
    }

    /// <summary>
    /// Prepares reusable synthetic extension keys for the `VS8` string facade.<br/>
    /// Extension is intentionally cached by distinct value because real file trees tend to repeat a small number of extensions many times, and each logical insert can maintain exact, folded, sort-key, and reversed projections.<br/>
    /// Filename remains uncached here because its high cardinality would mostly retain one prepared object per file and obscure the index memory being measured.<br/>
    /// </summary>
    /// <param name="records">The collected records being reindexed.</param>
    private void PrepareSyntheticExtensionKeys(List<FileRecord> records)
    {
        Dictionary<string, LibraDexStringScalar8Index.LibraDexStringScalar8PreparedKey> keys = new(StringComparer.Ordinal);
        for (int i = 0; i < records.Count; i++)
        {
            FileRecord record = records[i];
            if (!keys.TryGetValue(record.Extension, out LibraDexStringScalar8Index.LibraDexStringScalar8PreparedKey? prepared))
            {
                prepared = extensionIndex.PrepareKey(record.Extension);
                keys.Add(record.Extension, prepared);
            }

            record.ExtensionPreparedKey = prepared;
        }

        FileSearchLog.Info($"Prepared synthetic extension keys. Records={records.Count:n0}; DistinctExtensions={keys.Count:n0}; StringFolded={state.IndexStringFolded}; StringSortKey={state.IndexStringSortKey}; StringReversed={state.IndexStringReversed}.");
    }

    /// <summary>
    /// Rebuilds non-persisted byte caches for one file record.<br/>
    /// The cached values are not part of the durable sidecar contract; they exist only to avoid repeated UTF-8 encoding during workbench runs.<br/>
    /// </summary>
    private static void PrepareTransientRecordBytes(FileRecord record)
    {
        record.PathIdentityBytes = Encoding.UTF8.GetBytes(record.Path);
        record.FileNameKeyBytes = Encoding.UTF8.GetBytes(record.FileName);
        record.ExtensionKeyBytes = Encoding.UTF8.GetBytes(record.Extension);
    }

    private void SaveState()
    {
        if (!fileBacked || catalogPath is null)
        {
            return;
        }

        string sidecarPath = GetSidecarPath(catalogPath);
        using FileStream stream = File.Create(sidecarPath);
        JsonSerializer.Serialize(stream, state, JsonOptions);
    }

    private static string GetSidecarPath(string path)
        => $"{path}.filesearch.json";

    /// <summary>
    /// Creates a short workbench-facing storage-size summary for the active catalog files.<br/>
    /// Memory-backed catalogs report their in-memory arena diagnostics; file-backed catalogs report the visible `.lbdx` and FileSearch sidecar lengths so long-running reindex passes do not appear stalled at zero bytes.<br/>
    /// </summary>
    /// <returns>A compact storage summary suitable for status bars and diagnostic logs.</returns>
    public string GetCatalogStorageStatusText()
    {
        if (!fileBacked || catalogPath is null)
        {
            return GetCatalogMemoryDiagnosticsText();
        }

        long catalogBytes = GetExistingFileLength(catalogPath);
        long sidecarBytes = GetExistingFileLength(GetSidecarPath(catalogPath));
        return $"CatalogFile={FormatBytes(catalogBytes)}; Sidecar={FormatBytes(sidecarBytes)}; Path='{catalogPath}'";
    }

    /// <summary>
    /// Reads a file length for workbench diagnostics without treating missing files as failures.<br/>
    /// The FileSearch test tool calls this while reindexing, so transient delete/create windows are represented as zero bytes instead of surfacing as UI exceptions.<br/>
    /// </summary>
    /// <param name="path">The file path to inspect.<br/></param>
    /// <returns>The current file length, or zero when the file does not currently exist.</returns>
    private static long GetExistingFileLength(string path)
    {
        return File.Exists(path)
            ? new FileInfo(path).Length
            : 0;
    }

    private static ulong ParseUInt64(string value, string field)
    {
        if (!ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong result))
        {
            throw new FormatException($"{field} requires an unsigned integer value.");
        }

        return result;
    }

    private static uint ParseUInt32(string value, string field)
    {
        if (!uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint result))
        {
            throw new FormatException($"{field} requires a 32-bit unsigned integer value.");
        }

        return result;
    }

    private static DateTime ParseUtcDateTime(string value, string field)
    {
        if (!DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out DateTime result))
        {
            throw new FormatException($"{field} requires a date/time value.");
        }

        return result.Kind == DateTimeKind.Utc
            ? result
            : result.ToUniversalTime();
    }

    private static ulong ToUtcTicks(DateTime value)
    {
        DateTime utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        return checked((ulong)utc.Ticks);
    }

    private static string FormatDate(DateTime value)
        => $"DateTime.Parse(\"{value:O}\", null, DateTimeStyles.RoundtripKind)";

    private static string Escape(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string NormalizeExtension(string extension)
        => extension.Length == 0 ? "(none)" : extension;
}

internal readonly record struct FileIndexResult(bool CoreIndexed, int FailedFieldCount);

internal sealed class FileSearchReindexTelemetry
{
    private readonly Dictionary<string, FileSearchFieldInsertTelemetry> fieldTelemetry = new(StringComparer.Ordinal);
    private readonly List<FileSearchBatchCommitTelemetry> batchCommits = new();

    public IReadOnlyDictionary<string, FileSearchFieldInsertTelemetry> FieldTelemetry => fieldTelemetry;

    public IReadOnlyList<FileSearchBatchCommitTelemetry> BatchCommits => batchCommits;

    public long FinalCommitAttempted { get; private set; }

    public long FinalCommitInserted { get; private set; }

    public long FinalCommitDeferredCommits { get; private set; }

    public TimeSpan FinalCommitDuration { get; private set; }

    public void RecordField(string fieldName, long elapsedTicks, long allocatedBytes, bool inserted, bool failed)
    {
        ref FileSearchFieldInsertTelemetry? telemetry = ref CollectionsMarshal.GetValueRefOrAddDefault(fieldTelemetry, fieldName, out bool exists);
        if (!exists || telemetry is null)
        {
            telemetry = new FileSearchFieldInsertTelemetry(fieldName);
        }

        telemetry.Record(elapsedTicks, allocatedBytes, inserted, failed);
    }

    public void RecordFieldSkipped(string fieldName)
    {
        ref FileSearchFieldInsertTelemetry? telemetry = ref CollectionsMarshal.GetValueRefOrAddDefault(fieldTelemetry, fieldName, out bool exists);
        if (!exists || telemetry is null)
        {
            telemetry = new FileSearchFieldInsertTelemetry(fieldName);
        }

        telemetry.RecordSkipped();
    }

    /// <summary>
    /// Records the routed insert kind for one scalar-key/path-identity `SV8` field insert.<br/>
    /// The workbench uses this to attribute hot paths without emitting per-insert log lines or changing LibraDex public result objects.<br/>
    /// </summary>
    public void RecordScalar8VarIdentityOutcome(string fieldName, Scalar8VarIdentityInsertOutcome outcome)
    {
        ref FileSearchFieldInsertTelemetry? telemetry = ref CollectionsMarshal.GetValueRefOrAddDefault(fieldTelemetry, fieldName, out bool exists);
        if (!exists || telemetry is null)
        {
            telemetry = new FileSearchFieldInsertTelemetry(fieldName);
        }

        telemetry.RecordScalar8VarIdentityOutcome(outcome);
    }

    public void RecordBatch(
        string label,
        long cumulativeEntries,
        long deltaEntries,
        long attempted,
        long inserted,
        long deferredCommits,
        TimeSpan insertDeltaElapsed,
        TimeSpan commitElapsed,
        TimeSpan batchTotalElapsed,
        TimeSpan cumulativeElapsed)
    {
        batchCommits.Add(new FileSearchBatchCommitTelemetry(label, cumulativeEntries, deltaEntries, attempted, inserted, deferredCommits, insertDeltaElapsed, commitElapsed, batchTotalElapsed, cumulativeElapsed));
    }

    public void RecordFinalCommit(long attempted, long inserted, long deferredCommits, TimeSpan duration)
    {
        FinalCommitAttempted = attempted;
        FinalCommitInserted = inserted;
        FinalCommitDeferredCommits = deferredCommits;
        FinalCommitDuration = duration;
    }

    public string CreateFieldSummary()
    {
        if (fieldTelemetry.Count == 0)
        {
            return "none";
        }

        StringBuilder builder = new();
        foreach (FileSearchFieldInsertTelemetry field in fieldTelemetry.Values.OrderByDescending(static item => item.ElapsedTicks))
        {
            if (builder.Length != 0)
            {
                builder.Append(" | ");
            }

            builder
                .Append(field.FieldName)
                .Append(": attempts=")
                .Append(field.Attempted.ToString("N0", CultureInfo.InvariantCulture))
                .Append("; inserted=")
                .Append(field.Inserted.ToString("N0", CultureInfo.InvariantCulture))
                .Append("; failed=")
                .Append(field.Failed.ToString("N0", CultureInfo.InvariantCulture))
                .Append("; skipped=")
                .Append(field.Skipped.ToString("N0", CultureInfo.InvariantCulture))
                .Append("; elapsed=")
                .Append(FormatTelemetryDuration(field.Elapsed))
                .Append("; allocated=")
                .Append(FormatTelemetryBytes(field.AllocatedBytes));
            field.AppendScalar8VarIdentitySummary(builder);
        }

        return builder.ToString();
    }

    private static string FormatTelemetryDuration(TimeSpan elapsed)
    {
        return elapsed.TotalSeconds >= 1
            ? $"{elapsed.TotalSeconds:n2}s"
            : $"{elapsed.TotalMilliseconds:n0}ms";
    }

    private static string FormatTelemetryBytes(long bytes)
    {
        const double KiB = 1024;
        const double MiB = KiB * 1024;
        const double GiB = MiB * 1024;
        return bytes >= GiB
            ? $"{bytes / GiB:n1} GB"
            : bytes >= MiB
                ? $"{bytes / MiB:n1} MB"
                : bytes >= KiB
                    ? $"{bytes / KiB:n1} KB"
                    : $"{bytes:n0} B";
    }
}

internal sealed class ByteArrayComparer : IEqualityComparer<byte[]>
{
    public static readonly ByteArrayComparer Instance = new();

    private ByteArrayComparer()
    {
    }

    public bool Equals(byte[]? left, byte[]? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        return left.AsSpan().SequenceEqual(right);
    }

    public int GetHashCode(byte[] value)
    {
        HashCode hash = new();
        int count = Math.Min(value.Length, 32);
        hash.Add(value.Length);
        for (int i = 0; i < count; i++)
        {
            hash.Add(value[i]);
        }

        if (value.Length > count)
        {
            hash.Add(value[value.Length - 1]);
        }

        return hash.ToHashCode();
    }
}

internal enum PathIdentityVarKeyFilterMode
{
    ExactText,
    FoldedText,
    SortKey
}

internal readonly record struct VarKeyVarIdentityQueryPlan(
    VarKeyVarIdentityIndex Index,
    string Projection,
    byte[] LowerKey,
    byte[] UpperKey,
    bool FilterKeys,
    PathIdentityVarKeyFilterMode FilterMode,
    byte[] FilterValue1,
    byte[] FilterValue2);

internal sealed class FileSearchFieldInsertTelemetry
{
    public FileSearchFieldInsertTelemetry(string fieldName)
    {
        FieldName = fieldName;
    }

    public string FieldName { get; }

    public long Attempted { get; private set; }

    public long Inserted { get; private set; }

    public long Failed { get; private set; }

    public long Skipped { get; private set; }

    public long ElapsedTicks { get; private set; }

    public long AllocatedBytes { get; private set; }

    public long Scalar8NoOp { get; private set; }

    public long Scalar8WalkedNoSplit { get; private set; }

    public long Scalar8WalkedGrow { get; private set; }

    public long Scalar8WalkedShelfSplit { get; private set; }

    public long Scalar8Full { get; private set; }

    public long Scalar8KeyConflict { get; private set; }

    public long Scalar8Invalid { get; private set; }

    public long Scalar8DuplicateRunOverflow { get; private set; }

    public long Scalar8CreatedInitialRoute { get; private set; }

    public long Scalar8TargetShelfItems { get; private set; }

    public long Scalar8TargetShelfBytes { get; private set; }

    public long Scalar8TargetRouterDepth { get; private set; }

    public long Scalar8HeaderTailFast { get; private set; }

    public long Scalar8DuplicateRunChainInsert { get; private set; }

    public long Scalar8DuplicateRunTailAppend { get; private set; }

    public long Scalar8OverflowTailAppend { get; private set; }

    public long Scalar8TerminalTailAppend { get; private set; }

    public long Scalar8TerminalChainInsert { get; private set; }

    public long Scalar8TerminalFullRewrite { get; private set; }

    public long Scalar8OverflowChainLocal { get; private set; }

    public long Scalar8OverflowChainRewrite { get; private set; }

    public long Scalar8TerminalTailInPlace { get; private set; }

    public long Scalar8TerminalTailNewShelf { get; private set; }

    public long Scalar8DiagnosticAllocatedBytes { get; private set; }

    public long Scalar8NoSplitAllocatedBytes { get; private set; }

    public long Scalar8GrowAllocatedBytes { get; private set; }

    public long Scalar8SplitAllocatedBytes { get; private set; }

    public long Scalar8DuplicateOverflowAllocatedBytes { get; private set; }

    public TimeSpan Elapsed => TimeSpan.FromTicks((long)(ElapsedTicks * (TimeSpan.TicksPerSecond / (double)Stopwatch.Frequency)));

    public void Record(long elapsedTicks, long allocatedBytes, bool inserted, bool failed)
    {
        Attempted++;
        ElapsedTicks += elapsedTicks;
        AllocatedBytes += allocatedBytes;
        if (inserted)
        {
            Inserted++;
        }

        if (failed)
        {
            Failed++;
        }
    }

    public void RecordSkipped()
    {
        Skipped++;
    }

    /// <summary>
    /// Records one detailed `SV8` insert outcome using scalar counters only.<br/>
    /// This is intentionally allocation-free on the hot path and defers all text formatting until the final summary log line.<br/>
    /// </summary>
    public void RecordScalar8VarIdentityOutcome(Scalar8VarIdentityInsertOutcome outcome)
    {
        switch (outcome.Kind)
        {
            case Scalar8VarIdentityRoutedInsertKind.NoOp:
                Scalar8NoOp++;
                break;
            case Scalar8VarIdentityRoutedInsertKind.WalkedNoSplit:
                Scalar8WalkedNoSplit++;
                Scalar8NoSplitAllocatedBytes += outcome.DiagnosticAllocatedBytes;
                break;
            case Scalar8VarIdentityRoutedInsertKind.WalkedGrow:
                Scalar8WalkedGrow++;
                Scalar8GrowAllocatedBytes += outcome.DiagnosticAllocatedBytes;
                break;
            case Scalar8VarIdentityRoutedInsertKind.WalkedShelfSplit:
                Scalar8WalkedShelfSplit++;
                Scalar8SplitAllocatedBytes += outcome.DiagnosticAllocatedBytes;
                break;
            case Scalar8VarIdentityRoutedInsertKind.Full:
                Scalar8Full++;
                break;
            case Scalar8VarIdentityRoutedInsertKind.KeyConflict:
                Scalar8KeyConflict++;
                break;
            case Scalar8VarIdentityRoutedInsertKind.Invalid:
                Scalar8Invalid++;
                break;
            case Scalar8VarIdentityRoutedInsertKind.WalkedDuplicateRunOverflow:
                Scalar8DuplicateRunOverflow++;
                Scalar8DuplicateOverflowAllocatedBytes += outcome.DiagnosticAllocatedBytes;
                break;
        }

        if (outcome.CreatedInitialShelfRoute)
        {
            Scalar8CreatedInitialRoute++;
        }

        Scalar8TargetShelfItems += outcome.TargetShelfItemCount;
        Scalar8TargetShelfBytes += outcome.TargetShelfExtentSize;
        Scalar8TargetRouterDepth += outcome.TargetRouterDepth;
        Scalar8DiagnosticAllocatedBytes += outcome.DiagnosticAllocatedBytes;
        switch (outcome.DiagnosticPath)
        {
            case Scalar8VarIdentityInsertDiagnosticPath.HeaderTailFast:
                Scalar8HeaderTailFast++;
                break;
            case Scalar8VarIdentityInsertDiagnosticPath.DuplicateRunChainInsert:
                Scalar8DuplicateRunChainInsert++;
                break;
            case Scalar8VarIdentityInsertDiagnosticPath.DuplicateRunTailAppend:
                Scalar8DuplicateRunTailAppend++;
                break;
            case Scalar8VarIdentityInsertDiagnosticPath.OverflowTailAppend:
                Scalar8OverflowTailAppend++;
                break;
            case Scalar8VarIdentityInsertDiagnosticPath.TerminalTailAppend:
                Scalar8TerminalTailAppend++;
                break;
            case Scalar8VarIdentityInsertDiagnosticPath.TerminalChainInsert:
                Scalar8TerminalChainInsert++;
                break;
            case Scalar8VarIdentityInsertDiagnosticPath.TerminalFullRewrite:
                Scalar8TerminalFullRewrite++;
                break;
            case Scalar8VarIdentityInsertDiagnosticPath.OverflowChainLocal:
                Scalar8OverflowChainLocal++;
                break;
            case Scalar8VarIdentityInsertDiagnosticPath.OverflowChainRewrite:
                Scalar8OverflowChainRewrite++;
                break;
            case Scalar8VarIdentityInsertDiagnosticPath.TerminalTailInPlace:
                Scalar8TerminalTailInPlace++;
                break;
            case Scalar8VarIdentityInsertDiagnosticPath.TerminalTailNewShelf:
                Scalar8TerminalTailNewShelf++;
                break;
        }
    }

    /// <summary>
    /// Appends final `SV8` route-kind telemetry for this field when the field used scalar-key/path-identity inserts.<br/>
    /// Formatting is centralized here so the insert hot path only updates counters.<br/>
    /// </summary>
    public void AppendScalar8VarIdentitySummary(StringBuilder builder)
    {
        long total =
            Scalar8NoOp +
            Scalar8WalkedNoSplit +
            Scalar8WalkedGrow +
            Scalar8WalkedShelfSplit +
            Scalar8Full +
            Scalar8KeyConflict +
            Scalar8Invalid +
            Scalar8DuplicateRunOverflow;
        if (total == 0)
        {
            return;
        }

        builder
            .Append("; sv8={")
            .Append("noSplit=").Append(Scalar8WalkedNoSplit.ToString("N0", CultureInfo.InvariantCulture))
            .Append(", grow=").Append(Scalar8WalkedGrow.ToString("N0", CultureInfo.InvariantCulture))
            .Append(", split=").Append(Scalar8WalkedShelfSplit.ToString("N0", CultureInfo.InvariantCulture))
            .Append(", dupOverflow=").Append(Scalar8DuplicateRunOverflow.ToString("N0", CultureInfo.InvariantCulture))
            .Append(", createdRoute=").Append(Scalar8CreatedInitialRoute.ToString("N0", CultureInfo.InvariantCulture))
            .Append(", noOp=").Append(Scalar8NoOp.ToString("N0", CultureInfo.InvariantCulture))
            .Append(", full=").Append(Scalar8Full.ToString("N0", CultureInfo.InvariantCulture))
            .Append(", keyConflict=").Append(Scalar8KeyConflict.ToString("N0", CultureInfo.InvariantCulture))
            .Append(", invalid=").Append(Scalar8Invalid.ToString("N0", CultureInfo.InvariantCulture));
        if (total != 0)
        {
            builder
                .Append(", avgItems=").Append((Scalar8TargetShelfItems / (double)total).ToString("N1", CultureInfo.InvariantCulture))
                .Append(", avgBytes=").Append((Scalar8TargetShelfBytes / (double)total).ToString("N0", CultureInfo.InvariantCulture))
                .Append(", avgDepth=").Append((Scalar8TargetRouterDepth / (double)total).ToString("N1", CultureInfo.InvariantCulture))
                .Append(", diagAllocated=").Append(FormatTelemetryBytes(Scalar8DiagnosticAllocatedBytes))
                .Append(", allocKinds=noSplit:").Append(FormatTelemetryBytes(Scalar8NoSplitAllocatedBytes))
                .Append("/grow:").Append(FormatTelemetryBytes(Scalar8GrowAllocatedBytes))
                .Append("/split:").Append(FormatTelemetryBytes(Scalar8SplitAllocatedBytes))
                .Append("/dup:").Append(FormatTelemetryBytes(Scalar8DuplicateOverflowAllocatedBytes));
        }

        long pathTotal =
            Scalar8HeaderTailFast +
            Scalar8DuplicateRunChainInsert +
            Scalar8DuplicateRunTailAppend +
            Scalar8OverflowTailAppend +
            Scalar8TerminalTailAppend +
            Scalar8TerminalChainInsert +
            Scalar8TerminalFullRewrite +
            Scalar8OverflowChainLocal +
            Scalar8OverflowChainRewrite +
            Scalar8TerminalTailInPlace +
            Scalar8TerminalTailNewShelf;
        if (pathTotal != 0)
        {
            builder
                .Append(", paths=")
                .Append("headerTail=").Append(Scalar8HeaderTailFast.ToString("N0", CultureInfo.InvariantCulture))
                .Append("/dupChain=").Append(Scalar8DuplicateRunChainInsert.ToString("N0", CultureInfo.InvariantCulture))
                .Append("/dupTail=").Append(Scalar8DuplicateRunTailAppend.ToString("N0", CultureInfo.InvariantCulture))
                .Append("/ovTail=").Append(Scalar8OverflowTailAppend.ToString("N0", CultureInfo.InvariantCulture))
                .Append("/termTail=").Append(Scalar8TerminalTailAppend.ToString("N0", CultureInfo.InvariantCulture))
                .Append("/termChain=").Append(Scalar8TerminalChainInsert.ToString("N0", CultureInfo.InvariantCulture))
                .Append("/termRewrite=").Append(Scalar8TerminalFullRewrite.ToString("N0", CultureInfo.InvariantCulture))
                .Append("/termInPlace=").Append(Scalar8TerminalTailInPlace.ToString("N0", CultureInfo.InvariantCulture))
                .Append("/termNewShelf=").Append(Scalar8TerminalTailNewShelf.ToString("N0", CultureInfo.InvariantCulture))
                .Append("/ovLocal=").Append(Scalar8OverflowChainLocal.ToString("N0", CultureInfo.InvariantCulture))
                .Append("/ovRewrite=").Append(Scalar8OverflowChainRewrite.ToString("N0", CultureInfo.InvariantCulture));
        }

        builder.Append('}');
    }

    private static string FormatTelemetryBytes(long bytes)
    {
        const double KiB = 1024;
        const double MiB = KiB * 1024;
        const double GiB = MiB * 1024;
        return bytes >= GiB
            ? $"{bytes / GiB:n1} GB"
            : bytes >= MiB
                ? $"{bytes / MiB:n1} MB"
                : bytes >= KiB
                    ? $"{bytes / KiB:n1} KB"
                    : $"{bytes:n0} B";
    }
}

internal readonly record struct FileSearchBatchCommitTelemetry(
    string Label,
    long CumulativeEntries,
    long DeltaEntries,
    long Attempted,
    long Inserted,
    long DeferredCommits,
    TimeSpan InsertDeltaElapsed,
    TimeSpan CommitElapsed,
    TimeSpan BatchTotalElapsed,
    TimeSpan CumulativeElapsed);

internal readonly record struct ReindexResult(long Scanned, long Indexed, long FailedFiles, long FailedFieldAttempts, long SkippedFields, string DisabledFields, ReindexPhaseDurations Phases, FileSearchReindexTelemetry Telemetry);

internal sealed record QueryResult(IReadOnlyList<FileRecord> Files, string ConditionPreview);

internal sealed record TupleQueryResult(IReadOnlyList<FileSearchTupleRow> Rows, string ConditionPreview);

internal sealed record IdentityQueryResult(IReadOnlyList<FileSearchIdentityRow> Rows, string ConditionPreview);

internal readonly record struct StressEnumerationProbe(IReadOnlyList<FileSearchTupleRow> Rows, TimeSpan IndexTupleReadElapsed, TimeSpan FormatElapsed);

internal readonly record struct StressRawProbe(long Rows, long KeyBytes, long IdentityBytes, TimeSpan Elapsed, string Path);

internal readonly record struct CatalogMetrics(string DisplayName, bool FileBacked, int RootCount, int FileCount, SearchField SelectedField, int SelectedFieldDistinctKeys);
