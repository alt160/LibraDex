using System.Diagnostics;
using System.Globalization;
using LibraDex;
using LibraDex.FileSearch;

return FileSearchPerfRunner.Run(args);

internal static class FileSearchPerfRunner
{
    /// <summary>
    /// Runs the FileSearch reindex path from a console process for repeatable allocation and timing work.<br/>
    /// This intentionally calls <see cref="FileSearchCatalog.Reindex"/> rather than duplicating the indexing loop so perf fixes are measured against the same workbench path the UI uses.<br/>
    /// </summary>
    public static int Run(string[] args)
    {
        string root = GetOption(args, "--root", Directory.Exists(@"C:\msys64") ? @"C:\msys64" : Directory.GetCurrentDirectory());
        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine($"Root not found: {root}");
            return 1;
        }

        bool fileBacked = GetBoolOption(args, "--file", false);
        string catalogPath = GetOption(args, "--catalog", Path.Combine("artifacts", "filesearch-runner.lbdx"));
        bool progressEnabled = GetBoolOption(args, "--progress", false);
        bool checkRoutePruning = GetBoolOption(args, "--check-route-pruning", false);
        bool checkRouteDelete = GetBoolOption(args, "--check-route-delete", false);
        bool stressExisting = GetBoolOption(args, "--stress-existing", false);
        bool describeExisting = GetBoolOption(args, "--describe-existing", false);
        FileSearchIdentityMode identityMode = ParseEnum(GetOption(args, "--identity", FileSearchIdentityMode.SyntheticUInt64.ToString()), FileSearchIdentityMode.SyntheticUInt64);
        using CancellationTokenSource cancellation = CreateCancellation(args);
        Progress<string>? progress = progressEnabled ? new Progress<string>(Console.WriteLine) : null;
        if (describeExisting)
        {
            using FileSearchCatalog describeCatalog = FileSearchCatalog.CreateFile(catalogPath, overwrite: false);
            SearchField field = ParseEnum(GetOption(args, "--field", SearchField.Size.ToString()), SearchField.Size);
            ulong key = ulong.Parse(GetOption(args, "--key", "0"), NumberStyles.Integer, CultureInfo.InvariantCulture);
            int maxRows = GetIntOption(args, "--max-rows", 32);
            Console.WriteLine(describeCatalog.DescribePathIdentityExactKeyRoutes(field, key, maxRows));
            return 0;
        }

        if (stressExisting)
        {
            using FileSearchCatalog stressCatalog = FileSearchCatalog.CreateFile(catalogPath, overwrite: false);
            FileSearchReadOptions stressOptions = new()
            {
                IdentityMode = identityMode,
                DisplayFieldMode = FileSearchDisplayFieldMode.SelectedFields,
                RowHydrationMode = FileSearchRowHydrationMode.SidecarCache,
                KeyDisplayMode = FileSearchValueDisplayMode.Text,
                IdentityDisplayMode = FileSearchValueDisplayMode.Text
            };
            FileSearchStressResult stress = stressCatalog.RunStressCertification(stressOptions, progress, cancellation.Token);
            Console.WriteLine($"Stress {stress.Summary}");
            return stress.Failures == 0 ? 0 : 1;
        }

        using FileSearchCatalog catalog = fileBacked
            ? FileSearchCatalog.CreateFile(catalogPath, overwrite: true)
            : FileSearchCatalog.CreateMemory();
        catalog.AddRoot(root);

        FileSearchReindexOptions options = new()
        {
            IdentityMode = identityMode,
            MaxFiles = GetIntOption(args, "--max-files", 0),
            UseGroupBatching = GetBoolOption(args, "--batch", true),
            BatchCommitFileCount = GetIntOption(args, "--commit-every", 2000),
            IndexExtension = GetBoolOption(args, "--extension", true),
            IndexStringFolded = GetBoolOption(args, "--string-folded", false),
            IndexStringSortKey = GetBoolOption(args, "--string-sortkey", false),
            IndexStringReversed = GetBoolOption(args, "--string-reversed", false),
            InsertLayout = ParseEnum(GetOption(args, "--layout", FileSearchInsertLayout.RecordMajor.ToString()), FileSearchInsertLayout.RecordMajor),
            WriteOrder = ParseEnum(GetOption(args, "--write-order", GetOption(args, "--order", LibraDexWriteOrder.Default.ToString())), LibraDexWriteOrder.Default),
            WriteVolume = ParseEnum(GetOption(args, "--write-volume", LibraDexWriteVolume.Thousands.ToString()), LibraDexWriteVolume.Thousands),
            WriteLocality = ParseEnum(GetOption(args, "--write-locality", LibraDexWriteLocality.Clustered.ToString()), LibraDexWriteLocality.Clustered),
            WritePriority = ParseEnum(GetOption(args, "--write-priority", LibraDexWritePriority.WriteSpeed.ToString()), LibraDexWritePriority.WriteSpeed)
        };

        Console.WriteLine($"FileSearch runner root={root} fileBacked={fileBacked} identity={options.IdentityMode} maxFiles={options.MaxFiles:n0} batch={options.UseGroupBatching} commitEvery={options.BatchCommitFileCount:n0} layout={options.InsertLayout} order={options.WriteOrder} stringFolded={options.IndexStringFolded} stringSortKey={options.IndexStringSortKey} stringReversed={options.IndexStringReversed}");
        Console.WriteLine($"Log={FileSearchLog.Path}");

        Process process = Process.GetCurrentProcess();
        long startAllocated = GC.GetTotalAllocatedBytes(precise: true);
        long startPrivate = process.PrivateMemorySize64;
        long startWorkingSet = process.WorkingSet64;
        Stopwatch watch = Stopwatch.StartNew();
        try
        {
            ReindexResult result = catalog.Reindex(options, progress, cancellation.Token);
            watch.Stop();
            process.Refresh();
            long allocated = GC.GetTotalAllocatedBytes(precise: true) - startAllocated;
            Console.WriteLine($"Reindex ok scanned={result.Scanned:n0} indexed={result.Indexed:n0} failedFiles={result.FailedFiles:n0} failedFields={result.FailedFieldAttempts:n0} skipped={result.SkippedFields:n0}");
            Console.WriteLine($"Durations collect={FormatDuration(result.Phases.Collect)} sort={FormatDuration(result.Phases.Sort)} insert={FormatDuration(result.Phases.Insert)} total={FormatDuration(watch.Elapsed)}");
            Console.WriteLine($"Fields {result.Telemetry.CreateFieldSummary()}");
            Console.WriteLine($"CatalogMemory {catalog.GetCatalogMemoryDiagnosticsText()}");
            Console.WriteLine($"Resource allocatedDelta={FormatBytes(allocated)} privateDelta={FormatBytes(process.PrivateMemorySize64 - startPrivate)} workingSetDelta={FormatBytes(process.WorkingSet64 - startWorkingSet)} private={FormatBytes(process.PrivateMemorySize64)} workingSet={FormatBytes(process.WorkingSet64)}");
            FileSearchReadOptions readOptions = new()
            {
                IdentityMode = options.IdentityMode,
                DisplayFieldMode = FileSearchDisplayFieldMode.SelectedFields,
                RowHydrationMode = FileSearchRowHydrationMode.SidecarCache,
                KeyDisplayMode = FileSearchValueDisplayMode.Text,
                IdentityDisplayMode = FileSearchValueDisplayMode.Value
            };
            if (options.IdentityMode == FileSearchIdentityMode.SyntheticUInt64)
            {
                IReadOnlyList<FileSearchTupleRow> fileNameTuples = catalog.EnumerateTuples(SearchField.FileName, skip: 0, length: 0, readOptions);
                IReadOnlyList<FileSearchTupleRow> extensionTuples = options.IndexExtension
                    ? catalog.EnumerateTuples(SearchField.Extension, skip: 0, length: 0, readOptions)
                    : Array.Empty<FileSearchTupleRow>();
                int sizeCount = catalog.EnumerateTuples(SearchField.Size, skip: 0, length: 0, readOptions).Count;
                int createdCount = catalog.EnumerateTuples(SearchField.CreatedUtc, skip: 0, length: 0, readOptions).Count;
                int modifiedCount = catalog.EnumerateTuples(SearchField.ModifiedUtc, skip: 0, length: 0, readOptions).Count;
                int accessedCount = catalog.EnumerateTuples(SearchField.AccessedUtc, skip: 0, length: 0, readOptions).Count;
                int attributesCount = catalog.EnumerateTuples(SearchField.Attributes, skip: 0, length: 0, readOptions).Count;
                Console.WriteLine($"Tuples fileName={fileNameTuples.Count:n0} extension={(options.IndexExtension ? extensionTuples.Count.ToString("n0", CultureInfo.InvariantCulture) : "disabled")} size={sizeCount:n0} createdUtc={createdCount:n0} modifiedUtc={modifiedCount:n0} accessedUtc={accessedCount:n0} attributes={attributesCount:n0} indexed={result.Indexed:n0}");
                if (fileNameTuples.Count != result.Indexed ||
                    (options.IndexExtension && extensionTuples.Count != result.Indexed) ||
                    sizeCount != result.Indexed ||
                    createdCount != result.Indexed ||
                    modifiedCount != result.Indexed ||
                    accessedCount != result.Indexed ||
                    attributesCount != result.Indexed)
                {
                    DumpSyntheticTupleMismatch(catalog, readOptions);
                    Console.Error.WriteLine("Synthetic scalar tuple enumeration mismatch after reindex.");
                    return 1;
                }
            }
            else
            {
                readOptions.KeyDisplayMode = FileSearchValueDisplayMode.Value;
                readOptions.IdentityDisplayMode = FileSearchValueDisplayMode.Text;
                int fileNameCount = catalog.EnumerateTuples(SearchField.FileName, skip: 0, length: 0, readOptions).Count;
                int extensionCount = catalog.EnumerateTuples(SearchField.Extension, skip: 0, length: 0, readOptions).Count;
                int sizeCount = catalog.EnumerateTuples(SearchField.Size, skip: 0, length: 0, readOptions).Count;
                int createdCount = catalog.EnumerateTuples(SearchField.CreatedUtc, skip: 0, length: 0, readOptions).Count;
                int modifiedCount = catalog.EnumerateTuples(SearchField.ModifiedUtc, skip: 0, length: 0, readOptions).Count;
                int accessedCount = catalog.EnumerateTuples(SearchField.AccessedUtc, skip: 0, length: 0, readOptions).Count;
                int attributesCount = catalog.EnumerateTuples(SearchField.Attributes, skip: 0, length: 0, readOptions).Count;
                Console.WriteLine($"Tuples fileName={fileNameCount:n0} extension={extensionCount:n0} size={sizeCount:n0} createdUtc={createdCount:n0} modifiedUtc={modifiedCount:n0} accessedUtc={accessedCount:n0} attributes={attributesCount:n0} indexed={result.Indexed:n0}");
                if (fileNameCount != result.Indexed ||
                    extensionCount != result.Indexed ||
                    sizeCount != result.Indexed ||
                    createdCount != result.Indexed ||
                    modifiedCount != result.Indexed ||
                    accessedCount != result.Indexed ||
                    attributesCount != result.Indexed)
                {
                    DumpPathStringTupleMismatch(catalog, readOptions);
                    Console.Error.WriteLine("PathString scalar tuple enumeration mismatch after reindex.");
                    return 1;
                }

                if (!ValidatePathStringQueries(catalog, readOptions))
                {
                    Console.Error.WriteLine("PathString scalar tuple query mismatch after reindex.");
                    return 1;
                }

                if (checkRoutePruning && !ValidatePathStringRoutePruning(catalog, readOptions, checkRouteDelete))
                {
                    Console.Error.WriteLine("PathString scalar route-pruning mismatch after reindex.");
                    return 1;
                }
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            watch.Stop();
            process.Refresh();
            long allocated = GC.GetTotalAllocatedBytes(precise: true) - startAllocated;
            Console.WriteLine($"Reindex canceled after {FormatDuration(watch.Elapsed)} allocatedDelta={FormatBytes(allocated)} private={FormatBytes(process.PrivateMemorySize64)} workingSet={FormatBytes(process.WorkingSet64)}");
            return 2;
        }
        catch (Exception ex)
        {
            watch.Stop();
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    /// <summary>
    /// Creates an optional cancellation token for bounded perf passes.<br/>
    /// The runner uses this instead of UI cancel so every iteration has the same timing and workload envelope.<br/>
    /// </summary>
    private static CancellationTokenSource CreateCancellation(string[] args)
    {
        int seconds = GetIntOption(args, "--cancel-after-seconds", 0);
        CancellationTokenSource source = new();
        if (seconds > 0)
        {
            source.CancelAfter(TimeSpan.FromSeconds(seconds));
        }

        return source;
    }

    /// <summary>
    /// Reads a string option from simple `--name value` or `--name=value` command-line forms.<br/>
    /// This keeps the runner dependency-free and close to the existing harness parsing style.<br/>
    /// </summary>
    private static string GetOption(string[] args, string name, string defaultValue)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return args[i + 1];
            }

            string prefix = name + "=";
            if (args[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return args[i][prefix.Length..];
            }
        }

        return defaultValue;
    }

    /// <summary>
    /// Reads an integer option with invariant-culture parsing and a supplied default.<br/>
    /// Invalid values are treated as argument errors because silent fallback would make perf runs ambiguous.<br/>
    /// </summary>
    private static int GetIntOption(string[] args, string name, int defaultValue)
    {
        string value = GetOption(args, name, defaultValue.ToString(CultureInfo.InvariantCulture));
        return int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Reads a boolean option using the same permissive forms users commonly type in the workbench loop.<br/>
    /// Accepted true values are `true`, `1`, `yes`, and `y`; accepted false values are `false`, `0`, `no`, and `n`.<br/>
    /// </summary>
    private static bool GetBoolOption(string[] args, string name, bool defaultValue)
    {
        string value = GetOption(args, name, defaultValue.ToString(CultureInfo.InvariantCulture));
        if (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("y", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (value.Equals("0", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("no", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("n", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        throw new ArgumentException($"Invalid boolean value for {name}: {value}");
    }

    /// <summary>
    /// Parses an enum option while accepting the hyphenated names that are easier to type in shell commands.<br/>
    /// The default value only provides type inference and does not mask invalid input.<br/>
    /// </summary>
    private static T ParseEnum<T>(string value, T defaultValue)
        where T : struct, Enum
    {
        _ = defaultValue;
        string normalized = value.Replace("-", string.Empty, StringComparison.Ordinal);
        return Enum.Parse<T>(normalized, ignoreCase: true);
    }

    /// <summary>
    /// Formats a duration compactly for side-by-side runner output.<br/>
    /// The command-line summary uses the same broad units as the FileSearch workbench log.<br/>
    /// </summary>
    private static string FormatDuration(TimeSpan elapsed)
        => elapsed.TotalSeconds >= 1
            ? $"{elapsed.TotalSeconds:n2}s"
            : $"{elapsed.TotalMilliseconds:n0}ms";

    /// <summary>
    /// Converts a filesystem timestamp to the same encoded UTC tick value used by the FileSearch scalar date indexes.<br/>
    /// Keeping the runner conversion local avoids touching FileSearch internals while preserving exact diagnostic key comparison.<br/>
    /// </summary>
    /// <param name="value">The timestamp captured from the filesystem record.</param>
    /// <returns>The UTC tick count encoded as an unsigned scalar key.</returns>
    private static ulong ToUtcTicks(DateTime value)
    {
        DateTime utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        return checked((ulong)utc.Ticks);
    }

    /// <summary>
    /// Prints a compact identity-level diff for PathString tuple enumeration failures.<br/>
    /// The diagnostic keeps the runner self-contained while letting SV8 routing bugs show the first missing path and its source record properties.<br/>
    /// </summary>
    /// <param name="catalog">The populated FileSearch catalog.</param>
    /// <param name="readOptions">The active read options used by tuple enumeration.</param>
    private static void DumpPathStringTupleMismatch(FileSearchCatalog catalog, FileSearchReadOptions readOptions)
    {
        DumpPathStringTupleMismatch(catalog, readOptions, SearchField.CreatedUtc);
        DumpPathStringTupleMismatch(catalog, readOptions, SearchField.ModifiedUtc);
        DumpPathStringTupleMismatch(catalog, readOptions, SearchField.AccessedUtc);
        DumpPathStringTupleMismatch(catalog, readOptions, SearchField.Size);
        DumpPathStringTupleMismatch(catalog, readOptions, SearchField.Attributes);
    }

    /// <summary>
    /// Prints compact synthetic-identity tuple mismatch diagnostics for scalar indexes.<br/>
    /// The diagnostic compares enumerated tuple buckets with the sidecar file records collected by the same runner process, so it can distinguish missing keys from duplicated routed rows without allocating a full row-by-row diff.<br/>
    /// </summary>
    /// <param name="catalog">The populated FileSearch catalog.</param>
    /// <param name="readOptions">The active read options used by tuple enumeration.</param>
    private static void DumpSyntheticTupleMismatch(FileSearchCatalog catalog, FileSearchReadOptions readOptions)
    {
        DumpSyntheticTupleMismatch(catalog, readOptions, SearchField.CreatedUtc);
        DumpSyntheticTupleMismatch(catalog, readOptions, SearchField.ModifiedUtc);
        DumpSyntheticTupleMismatch(catalog, readOptions, SearchField.AccessedUtc);
        DumpSyntheticTupleMismatch(catalog, readOptions, SearchField.Size);
        DumpSyntheticTupleMismatch(catalog, readOptions, SearchField.Attributes);
    }

    /// <summary>
    /// Prints one synthetic-identity tuple mismatch summary for a scalar field.<br/>
    /// Counts are grouped by displayed key and identity text to keep the diagnostic independent of internal index handles while still exposing duplicated persisted rows.<br/>
    /// </summary>
    /// <param name="catalog">The populated FileSearch catalog.</param>
    /// <param name="readOptions">The active read options used by tuple enumeration.</param>
    /// <param name="field">The scalar field to inspect.</param>
    private static void DumpSyntheticTupleMismatch(FileSearchCatalog catalog, FileSearchReadOptions readOptions, SearchField field)
    {
        IReadOnlyList<FileSearchTupleRow> rows = catalog.EnumerateTuples(field, skip: 0, length: 0, readOptions);
        if (rows.Count == catalog.FileCount)
        {
            return;
        }

        Console.WriteLine($"Mismatch {field}: rows={rows.Count:n0}; expected={catalog.FileCount:n0}; delta={rows.Count - catalog.FileCount:n0}");
        Dictionary<string, int> actualByKey = rows
            .GroupBy(static row => row.Key, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);
        IEnumerable<IGrouping<string, FileRecord>> expectedGroups = field switch
        {
            SearchField.CreatedUtc => catalog.Files.GroupBy(static file => ToUtcTicks(file.CreatedUtc).ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal),
            SearchField.ModifiedUtc => catalog.Files.GroupBy(static file => ToUtcTicks(file.ModifiedUtc).ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal),
            SearchField.AccessedUtc => catalog.Files.GroupBy(static file => ToUtcTicks(file.AccessedUtc).ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal),
            SearchField.Size => catalog.Files.GroupBy(static file => file.Size.ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal),
            SearchField.Attributes => catalog.Files.GroupBy(static file => file.Attributes.ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal),
            _ => []
        };

        foreach (IGrouping<string, FileRecord> group in expectedGroups.OrderByDescending(group =>
                     Math.Abs(group.Count() - (actualByKey.TryGetValue(group.Key, out int actualCount) ? actualCount : 0))).Take(5))
        {
            actualByKey.TryGetValue(group.Key, out int actualCount);
            int expectedCount = group.Count();
            if (actualCount != expectedCount)
            {
                Console.WriteLine($"  key bucket {field}: key={group.Key}; expected={expectedCount:n0}; actual={actualCount:n0}; delta={actualCount - expectedCount:n0}");
            }
        }

        foreach (IGrouping<string, FileSearchTupleRow> duplicate in rows.GroupBy(static row => row.Key + "\u001F" + row.Identity, StringComparer.Ordinal).Where(static group => group.Count() > 1).Take(5))
        {
            FileSearchTupleRow row = duplicate.First();
            Console.WriteLine($"  duplicate tuple {field}: key={row.Key}; identity={row.Identity}; count={duplicate.Count():n0}");
        }

        Dictionary<string, HashSet<string>> actualIdentitiesByKey = rows
            .GroupBy(static row => row.Key, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Select(static row => row.Identity).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (IGrouping<string, FileRecord> group in expectedGroups)
        {
            if (!actualIdentitiesByKey.TryGetValue(group.Key, out HashSet<string>? actualIdentities))
            {
                actualIdentities = [];
            }

            foreach (FileRecord missing in group.Where(file => !actualIdentities.Contains(file.Id.ToString(CultureInfo.InvariantCulture))).Take(3))
            {
                Console.WriteLine($"  missing tuple {field}: key={group.Key}; identity={missing.Id}; size={missing.Size}; attrs={missing.Attributes}; path={missing.Path}");
            }
        }
    }

    private static void DumpPathStringTupleMismatch(FileSearchCatalog catalog, FileSearchReadOptions readOptions, SearchField field)
    {
        IReadOnlyList<FileSearchTupleRow> rows = catalog.EnumerateTuples(field, skip: 0, length: 0, readOptions);
        HashSet<string> actual = new(rows.Select(static row => row.Identity), StringComparer.OrdinalIgnoreCase);
        List<FileRecord> missing = catalog.Files.Where(file => !actual.Contains(file.Path)).Take(5).ToList();
        if (missing.Count == 0 && rows.Count == catalog.FileCount)
        {
            return;
        }

        Console.WriteLine($"Mismatch {field}: rows={rows.Count:n0}; expected={catalog.FileCount:n0}; missingSample={missing.Count:n0}");
        Dictionary<string, int> actualByKey = rows
            .GroupBy(static row => row.Key, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);
        IEnumerable<IGrouping<string, FileRecord>> expectedGroups = field switch
        {
            SearchField.CreatedUtc => catalog.Files.GroupBy(static file => ToUtcTicks(file.CreatedUtc).ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal),
            SearchField.ModifiedUtc => catalog.Files.GroupBy(static file => ToUtcTicks(file.ModifiedUtc).ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal),
            SearchField.AccessedUtc => catalog.Files.GroupBy(static file => ToUtcTicks(file.AccessedUtc).ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal),
            SearchField.Size => catalog.Files.GroupBy(static file => file.Size.ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal),
            SearchField.Attributes => catalog.Files.GroupBy(static file => file.Attributes.ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal),
            _ => []
        };
        foreach (IGrouping<string, FileRecord> group in expectedGroups.Select(group => group).OrderByDescending(group => group.Count() - (actualByKey.TryGetValue(group.Key, out int actualCount) ? actualCount : 0)).Take(3))
        {
            actualByKey.TryGetValue(group.Key, out int actualCount);
            int expectedCount = group.Count();
            if (actualCount != expectedCount)
            {
                Console.WriteLine($"  key bucket {field}: key={group.Key}; expected={expectedCount:n0}; actual={actualCount:n0}; missing={expectedCount - actualCount:n0}");
            }
        }

        foreach (FileRecord file in missing)
        {
            Console.WriteLine($"  missing {field}: size={file.Size}; created={file.CreatedUtc:O}; modified={file.ModifiedUtc:O}; accessed={file.AccessedUtc:O}; attrs={file.Attributes}; path={file.Path}");
        }
    }

    /// <summary>
    /// Validates representative PathString query execution against the sidecar records collected from the same filesystem pass.<br/>
    /// The checks cover scalar `SV8` rows and raw string-key `VV` rows, and each row is executed through tuple, identity-only, and hydrated-record workbench paths.<br/>
    /// </summary>
    /// <param name="catalog">The populated FileSearch catalog.</param>
    /// <param name="readOptions">The active read options used by tuple queries.</param>
    /// <returns><see langword="true"/> when representative scalar queries return the sidecar-expected row counts.</returns>
    private static bool ValidatePathStringQueries(FileSearchCatalog catalog, FileSearchReadOptions readOptions)
    {
        if (catalog.Files.Count == 0)
        {
            return true;
        }

        FileRecord sample = catalog.Files[catalog.Files.Count / 2];
        bool sizeOk = ValidatePathStringQuery(
            catalog,
            readOptions,
            new QueryRow { Field = SearchField.Size, Operator = SearchOperator.EqualTo, Value1 = sample.Size.ToString(CultureInfo.InvariantCulture) },
            catalog.Files.Count(file => file.Size == sample.Size),
            "size");
        bool createdOk = ValidatePathStringQuery(
            catalog,
            readOptions,
            new QueryRow { Field = SearchField.CreatedUtc, Operator = SearchOperator.EqualTo, Value1 = sample.CreatedUtc.ToString("O", CultureInfo.InvariantCulture) },
            catalog.Files.Count(file => file.CreatedUtc == sample.CreatedUtc),
            "createdUtc");
        bool fileNameOk = ValidatePathStringStringQueries(catalog, readOptions, SearchField.FileName, sample.FileName, "fileName");
        bool extensionOk = ValidatePathStringStringQueries(catalog, readOptions, SearchField.Extension, sample.Extension, "extension");
        bool mixedOk = ValidatePathStringMixedQuery(catalog, readOptions, sample);
        return sizeOk && createdOk && fileNameOk && extensionOk && mixedOk;
    }

    /// <summary>
    /// Validates every workbench string operator for one PathString raw `VV` string-key field.<br/>
    /// The expected counts come from the sidecar file records so the runner can catch direct-reader query gaps without depending on UI state.<br/>
    /// </summary>
    /// <param name="catalog">The populated FileSearch catalog.</param>
    /// <param name="readOptions">The active read options used by query execution.</param>
    /// <param name="field">The string-key field to validate.</param>
    /// <param name="sampleValue">A real key value from the indexed file set.</param>
    /// <param name="label">The log label for the field.</param>
    /// <returns><see langword="true"/> when all representative string operators match expected sidecar counts.</returns>
    private static bool ValidatePathStringStringQueries(FileSearchCatalog catalog, FileSearchReadOptions readOptions, SearchField field, string sampleValue, string label)
    {
        string value = sampleValue ?? string.Empty;
        string prefix = value.Length <= 3 ? value : value[..3];
        string contains = value.Length <= 2 ? value : value.Substring(1, Math.Min(3, value.Length - 1));
        QueryRow[] rows =
        [
            new() { Field = field, Operator = SearchOperator.EqualTo, Value1 = value, IgnoreCase = true },
            new() { Field = field, Operator = SearchOperator.EqualTo, Value1 = value, IgnoreCase = false },
            new() { Field = field, Operator = SearchOperator.StartsWith, Value1 = prefix, IgnoreCase = true },
            new() { Field = field, Operator = SearchOperator.StartsWith, Value1 = prefix, IgnoreCase = false },
            new() { Field = field, Operator = SearchOperator.Contains, Value1 = contains, IgnoreCase = true },
            new() { Field = field, Operator = SearchOperator.GreaterOrEqual, Value1 = value, IgnoreCase = true },
            new() { Field = field, Operator = SearchOperator.LessOrEqual, Value1 = value, IgnoreCase = true },
            new() { Field = field, Operator = SearchOperator.Between, Value1 = value, Value2 = value, IgnoreCase = true }
        ];

        bool ok = true;
        for (int i = 0; i < rows.Length; i++)
        {
            QueryRow row = rows[i];
            int expected = catalog.Files.Count(file => MatchesStringQuery(GetStringField(file, field), row));
            ok &= ValidatePathStringQuery(catalog, readOptions, row, expected, $"{label}.{row.Operator}.ignoreCase={row.IgnoreCase}");
        }

        return ok;
    }

    /// <summary>
    /// Validates a mixed PathString AND query that combines direct raw `VV` string-key rows with a scalar `SV8` row.<br/>
    /// This covers the workbench intersection bridge used when the condition builder cannot represent every selected physical index shape directly.<br/>
    /// </summary>
    /// <param name="catalog">The populated FileSearch catalog.</param>
    /// <param name="readOptions">The active read options used by query execution.</param>
    /// <param name="sample">A real sidecar record used to choose matching query values.</param>
    /// <returns><see langword="true"/> when tuple fallback, identity-only, and hydrated-record query paths all match sidecar expectations.</returns>
    private static bool ValidatePathStringMixedQuery(FileSearchCatalog catalog, FileSearchReadOptions readOptions, FileRecord sample)
    {
        string fileNamePrefix = sample.FileName.Length <= 3 ? sample.FileName : sample.FileName[..3];
        QueryRow[] rows =
        [
            new() { Field = SearchField.FileName, Operator = SearchOperator.StartsWith, Value1 = fileNamePrefix, IgnoreCase = true },
            new() { Field = SearchField.Extension, Operator = SearchOperator.EqualTo, Value1 = sample.Extension, IgnoreCase = true },
            new() { Field = SearchField.Size, Operator = SearchOperator.GreaterOrEqual, Value1 = sample.Size.ToString(CultureInfo.InvariantCulture) }
        ];
        int expected = catalog.Files.Count(file =>
            MatchesStringQuery(file.FileName, rows[0]) &&
            MatchesStringQuery(file.Extension, rows[1]) &&
            file.Size >= sample.Size);
        FileSearchReadOptions tupleOptions = CreateReadOptions(readOptions, FileSearchDisplayFieldMode.SelectedFields);
        FileSearchReadOptions identityOptions = CreateReadOptions(readOptions, FileSearchDisplayFieldMode.IdentityOnly);
        FileSearchReadOptions recordOptions = CreateReadOptions(readOptions, FileSearchDisplayFieldMode.AllKnownFields);
        TupleQueryResult tupleResult = catalog.ExecuteSelectedIndexTuples(rows, skip: 0, length: 0, tupleOptions);
        IdentityQueryResult identityResult = catalog.ExecuteIdentities(rows, skip: 0, length: 0, identityOptions);
        QueryResult recordResult = catalog.Execute(rows, skip: 0, length: 0, recordOptions);
        Console.WriteLine($"Query mixed.fileName.extension.size: tuples={tupleResult.Rows.Count:n0}; identities={identityResult.Rows.Count:n0}; records={recordResult.Files.Count:n0}; expected={expected:n0}; condition={identityResult.ConditionPreview}");
        return tupleResult.Rows.Count == expected &&
            identityResult.Rows.Count == expected &&
            recordResult.Files.Count == expected;
    }

    /// <summary>
    /// Compares `SV8` exhaustive and pruned route traversal over FileSearch's path-identity scalar indexes.<br/>
    /// This is intentionally runner-local diagnostic plumbing so route publication bugs can be reproduced against real filesystem keys without widening the product API.<br/>
    /// </summary>
    /// <param name="catalog">The populated FileSearch catalog.</param>
    /// <param name="readOptions">The active read options used by tuple enumeration.</param>
    /// <param name="checkRouteDelete">Whether to run the destructive exact-key delete diagnostic after read-only pruning checks pass.</param>
    /// <returns><see langword="true"/> when pruned and exhaustive traversal return the same tuple counts.</returns>
    private static bool ValidatePathStringRoutePruning(FileSearchCatalog catalog, FileSearchReadOptions readOptions, bool checkRouteDelete)
    {
        if (!TrySetScalar8VarIdentityExhaustiveTraversal(true, out bool previousExhaustive))
        {
            Console.WriteLine("Route pruning diagnostic skipped: Scalar8VarIdentityRangeReader traversal switch is unavailable.");
            return true;
        }

        bool hasDescendantScanSwitch = TrySetScalar8VarIdentityDescendantScan(true, out bool previousDescendantScan);
        try
        {
            int exhaustiveSize = catalog.EnumerateTuples(SearchField.Size, skip: 0, length: 0, readOptions).Count;
            int exhaustiveCreated = catalog.EnumerateTuples(SearchField.CreatedUtc, skip: 0, length: 0, readOptions).Count;
            int exhaustiveModified = catalog.EnumerateTuples(SearchField.ModifiedUtc, skip: 0, length: 0, readOptions).Count;
            int exhaustiveAccessed = catalog.EnumerateTuples(SearchField.AccessedUtc, skip: 0, length: 0, readOptions).Count;
            int exhaustiveAttributes = catalog.EnumerateTuples(SearchField.Attributes, skip: 0, length: 0, readOptions).Count;
            FileRecord sample = catalog.Files[catalog.Files.Count / 2];
            QueryRow sizeRow = new() { Field = SearchField.Size, Operator = SearchOperator.EqualTo, Value1 = sample.Size.ToString(CultureInfo.InvariantCulture) };
            QueryRow createdRow = new() { Field = SearchField.CreatedUtc, Operator = SearchOperator.EqualTo, Value1 = sample.CreatedUtc.ToString("O", CultureInfo.InvariantCulture) };
            int exhaustiveSizeQuery = catalog.ExecuteSelectedIndexTuples([sizeRow], skip: 0, length: 0, readOptions).Rows.Count;
            int exhaustiveCreatedQuery = catalog.ExecuteSelectedIndexTuples([createdRow], skip: 0, length: 0, readOptions).Rows.Count;
            _ = TrySetScalar8VarIdentityExhaustiveTraversal(false, out _);
            if (hasDescendantScanSwitch)
            {
                _ = TrySetScalar8VarIdentityDescendantScan(true, out _);
            }

            int prunedSize = catalog.EnumerateTuples(SearchField.Size, skip: 0, length: 0, readOptions).Count;
            int prunedCreated = catalog.EnumerateTuples(SearchField.CreatedUtc, skip: 0, length: 0, readOptions).Count;
            int prunedModified = catalog.EnumerateTuples(SearchField.ModifiedUtc, skip: 0, length: 0, readOptions).Count;
            int prunedAccessed = catalog.EnumerateTuples(SearchField.AccessedUtc, skip: 0, length: 0, readOptions).Count;
            int prunedAttributes = catalog.EnumerateTuples(SearchField.Attributes, skip: 0, length: 0, readOptions).Count;
            int prunedSizeQuery = catalog.ExecuteSelectedIndexTuples([sizeRow], skip: 0, length: 0, readOptions).Rows.Count;
            int prunedCreatedQuery = catalog.ExecuteSelectedIndexTuples([createdRow], skip: 0, length: 0, readOptions).Rows.Count;
            bool descendantScanOk = prunedSize == exhaustiveSize &&
                prunedCreated == exhaustiveCreated &&
                prunedModified == exhaustiveModified &&
                prunedAccessed == exhaustiveAccessed &&
                prunedAttributes == exhaustiveAttributes &&
                prunedSizeQuery == exhaustiveSizeQuery &&
                prunedCreatedQuery == exhaustiveCreatedQuery;
            Console.WriteLine($"RoutePruning descendant-scan tuples size={prunedSize:n0}/{exhaustiveSize:n0} createdUtc={prunedCreated:n0}/{exhaustiveCreated:n0} modifiedUtc={prunedModified:n0}/{exhaustiveModified:n0} accessedUtc={prunedAccessed:n0}/{exhaustiveAccessed:n0} attributes={prunedAttributes:n0}/{exhaustiveAttributes:n0}");
            Console.WriteLine($"RoutePruning descendant-scan queries size={prunedSizeQuery:n0}/{exhaustiveSizeQuery:n0} createdUtc={prunedCreatedQuery:n0}/{exhaustiveCreatedQuery:n0}");
            if (!hasDescendantScanSwitch)
            {
                return descendantScanOk;
            }

            _ = TrySetScalar8VarIdentityDescendantScan(false, out _);
            int strictSize = catalog.EnumerateTuples(SearchField.Size, skip: 0, length: 0, readOptions).Count;
            int strictCreated = catalog.EnumerateTuples(SearchField.CreatedUtc, skip: 0, length: 0, readOptions).Count;
            int strictModified = catalog.EnumerateTuples(SearchField.ModifiedUtc, skip: 0, length: 0, readOptions).Count;
            int strictAccessed = catalog.EnumerateTuples(SearchField.AccessedUtc, skip: 0, length: 0, readOptions).Count;
            int strictAttributes = catalog.EnumerateTuples(SearchField.Attributes, skip: 0, length: 0, readOptions).Count;
            int strictSizeQuery = catalog.ExecuteSelectedIndexTuples([sizeRow], skip: 0, length: 0, readOptions).Rows.Count;
            int strictCreatedQuery = catalog.ExecuteSelectedIndexTuples([createdRow], skip: 0, length: 0, readOptions).Rows.Count;
            bool strictOk = strictSize == exhaustiveSize &&
                strictCreated == exhaustiveCreated &&
                strictModified == exhaustiveModified &&
                strictAccessed == exhaustiveAccessed &&
                strictAttributes == exhaustiveAttributes &&
                strictSizeQuery == exhaustiveSizeQuery &&
                strictCreatedQuery == exhaustiveCreatedQuery;
            Console.WriteLine($"RoutePruning strict tuples size={strictSize:n0}/{exhaustiveSize:n0} createdUtc={strictCreated:n0}/{exhaustiveCreated:n0} modifiedUtc={strictModified:n0}/{exhaustiveModified:n0} accessedUtc={strictAccessed:n0}/{exhaustiveAccessed:n0} attributes={strictAttributes:n0}/{exhaustiveAttributes:n0}");
            Console.WriteLine($"RoutePruning strict queries size={strictSizeQuery:n0}/{exhaustiveSizeQuery:n0} createdUtc={strictCreatedQuery:n0}/{exhaustiveCreatedQuery:n0}");
            if (!strictOk)
            {
                if (strictSizeQuery != exhaustiveSizeQuery)
                {
                    DumpStrictQueryMismatch(catalog, readOptions, sizeRow, "size");
                }

                if (strictCreatedQuery != exhaustiveCreatedQuery)
                {
                    DumpStrictQueryMismatch(catalog, readOptions, createdRow, "createdUtc");
                }

                Console.WriteLine("RoutePruning strict diagnostic mismatch; descendant-scan pruning remains the active validated reader mode.");
            }

            if (descendantScanOk && checkRouteDelete)
            {
                descendantScanOk &= ValidatePathStringRoutePruningDelete(catalog, readOptions, sizeRow, exhaustiveSizeQuery, "size");
            }

            return descendantScanOk;
        }
        finally
        {
            _ = TrySetScalar8VarIdentityExhaustiveTraversal(previousExhaustive, out _);
            if (hasDescendantScanSwitch)
            {
                _ = TrySetScalar8VarIdentityDescendantScan(previousDescendantScan, out _);
            }
        }
    }

    /// <summary>
    /// Validates that `SV8` exact-key range delete reaches the same descendant-pruned rows as the active range reader.<br/>
    /// This mutates only the disposable runner catalog after all read checks have completed, so FileSearch sidecar records are not updated.<br/>
    /// </summary>
    /// <param name="catalog">The populated FileSearch catalog.</param>
    /// <param name="readOptions">The active read options.</param>
    /// <param name="row">The exact-key query row to delete.</param>
    /// <param name="expectedDeleted">The expected number of deleted rows from the earlier exhaustive/descendant read check.</param>
    /// <param name="label">The short field label for runner output.</param>
    /// <returns><see langword="true"/> when delete count and post-delete query count match expectations.</returns>
    private static bool ValidatePathStringRoutePruningDelete(
        FileSearchCatalog catalog,
        FileSearchReadOptions readOptions,
        QueryRow row,
        int expectedDeleted,
        string label)
    {
        if (readOptions.IdentityMode != FileSearchIdentityMode.PathString ||
            !TryGetRouteDiagnosticKey(row, out ulong diagnosticKey))
        {
            return true;
        }

        long deleted = catalog.DeletePathIdentityExactKeyForDiagnostics(row.Field, diagnosticKey);
        TupleQueryResult afterDelete = catalog.ExecuteSelectedIndexTuples([row], skip: 0, length: 0, readOptions);
        Console.WriteLine($"RoutePruning delete {label}: deleted={deleted:n0}/{expectedDeleted:n0}; after={afterDelete.Rows.Count:n0}");
        return deleted == expectedDeleted && afterDelete.Rows.Count == 0;
    }

    /// <summary>
    /// Prints identity values that are visible through exhaustive `SV8` traversal but missed by strict route traversal for one diagnostic row.<br/>
    /// This runner-only helper keeps route-publication investigation concrete without widening the FileSearch workbench API surface.<br/>
    /// </summary>
    /// <param name="catalog">The populated FileSearch catalog.</param>
    /// <param name="readOptions">The active read options.</param>
    /// <param name="row">The single query row being compared.</param>
    private static void DumpStrictQueryMismatch(FileSearchCatalog catalog, FileSearchReadOptions readOptions, QueryRow row, string label)
    {
        if (!TrySetScalar8VarIdentityExhaustiveTraversal(true, out bool previousExhaustive))
        {
            return;
        }

        bool hasDescendantScanSwitch = TrySetScalar8VarIdentityDescendantScan(true, out bool previousDescendantScan);
        try
        {
            TupleQueryResult exhaustive = catalog.ExecuteSelectedIndexTuples([row], skip: 0, length: 0, readOptions);
            _ = TrySetScalar8VarIdentityExhaustiveTraversal(false, out _);
            if (hasDescendantScanSwitch)
            {
                _ = TrySetScalar8VarIdentityDescendantScan(false, out _);
            }

            TupleQueryResult strict = catalog.ExecuteSelectedIndexTuples([row], skip: 0, length: 0, readOptions);
            for (int i = 0; i < strict.Rows.Count && i < 16; i++)
            {
                Console.WriteLine($"RoutePruning strict {label} hit key={strict.Rows[i].Key} identity={strict.Rows[i].Identity}");
            }

            HashSet<string> strictIdentities = new(strict.Rows.Select(static tuple => tuple.Identity), StringComparer.Ordinal);
            int missingCount = 0;
            foreach (FileSearchTupleRow tuple in exhaustive.Rows)
            {
                if (strictIdentities.Contains(tuple.Identity))
                {
                    continue;
                }

                missingCount++;
                if (missingCount <= 16)
                {
                    Console.WriteLine($"RoutePruning strict {label} missing key={tuple.Key} identity={tuple.Identity}");
                }
            }

            Console.WriteLine($"RoutePruning strict {label} missing total={missingCount:n0}");
            if (readOptions.IdentityMode == FileSearchIdentityMode.PathString &&
                TryGetRouteDiagnosticKey(row, out ulong diagnosticKey))
            {
                Console.Write(catalog.DescribePathIdentityExactKeyRoutes(row.Field, diagnosticKey, maxRows: 24));
            }
        }
        finally
        {
            _ = TrySetScalar8VarIdentityExhaustiveTraversal(previousExhaustive, out _);
            if (hasDescendantScanSwitch)
            {
                _ = TrySetScalar8VarIdentityDescendantScan(previousDescendantScan, out _);
            }
        }
    }

    /// <summary>
    /// Converts a FileSearch diagnostic query row to the exact UInt64 key stored by the path-identity `SV8` indexes.<br/>
    /// The route-pruning diagnostics operate below the condition-builder layer, so date fields must be converted to UTC ticks just as query construction does.<br/>
    /// </summary>
    /// <param name="row">The query row used for the strict/exhaustive comparison.</param>
    /// <param name="key">Receives the exact encoded key when conversion succeeds.</param>
    /// <returns><see langword="true"/> when the row maps to a UInt64-backed path-identity index.</returns>
    private static bool TryGetRouteDiagnosticKey(QueryRow row, out ulong key)
    {
        key = 0;
        switch (row.Field)
        {
            case SearchField.Size:
            case SearchField.Attributes:
                return ulong.TryParse(row.Value1, NumberStyles.None, CultureInfo.InvariantCulture, out key);
            case SearchField.CreatedUtc:
            case SearchField.ModifiedUtc:
            case SearchField.AccessedUtc:
                key = ToUtcTicks(DateTime.Parse(row.Value1, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Sets the internal `SV8` range-reader traversal mode through reflection for runner-only diagnostics.<br/>
    /// The reflection boundary keeps the core switch internal while allowing this dogfood process to compare route-pruned and exhaustive reads.<br/>
    /// </summary>
    /// <param name="value">The requested exhaustive traversal value.</param>
    /// <param name="previous">Receives the previous traversal value when the switch is available.</param>
    /// <returns><see langword="true"/> when the switch was found and updated.</returns>
    private static bool TrySetScalar8VarIdentityExhaustiveTraversal(bool value, out bool previous)
    {
        previous = true;
        Type? readerType = Type.GetType("LibraDex.Scalar8VarIdentityRangeReader, LibraDex", throwOnError: false);
        System.Reflection.PropertyInfo? property = readerType?.GetProperty("UseExhaustiveRouteTraversal", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        if (property is null || property.GetValue(null) is not bool current)
        {
            return false;
        }

        previous = current;
        property.SetValue(null, value);
        return true;
    }

    /// <summary>
    /// Sets the internal `SV8` descendant-scan pruning switch through reflection for runner-only diagnostics.<br/>
    /// When disabled, route-pruned reads follow the exact key prefix at every router depth so writer route-publication invariants can be tested separately from the safe read fallback.<br/>
    /// </summary>
    /// <param name="value">The requested descendant-scan value.</param>
    /// <param name="previous">Receives the previous descendant-scan value when the switch is available.</param>
    /// <returns><see langword="true"/> when the switch was found and updated.</returns>
    private static bool TrySetScalar8VarIdentityDescendantScan(bool value, out bool previous)
    {
        previous = true;
        Type? readerType = Type.GetType("LibraDex.Scalar8VarIdentityRangeReader, LibraDex", throwOnError: false);
        System.Reflection.PropertyInfo? property = readerType?.GetProperty("ScanDescendantRoutesForPrunedReads", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        if (property is null || property.GetValue(null) is not bool current)
        {
            return false;
        }

        previous = current;
        property.SetValue(null, value);
        return true;
    }

    private static bool ValidatePathStringQuery(FileSearchCatalog catalog, FileSearchReadOptions readOptions, QueryRow row, int expected, string label)
    {
        FileSearchReadOptions tupleOptions = CreateReadOptions(readOptions, FileSearchDisplayFieldMode.SelectedFields);
        FileSearchReadOptions identityOptions = CreateReadOptions(readOptions, FileSearchDisplayFieldMode.IdentityOnly);
        FileSearchReadOptions recordOptions = CreateReadOptions(readOptions, FileSearchDisplayFieldMode.AllKnownFields);
        TupleQueryResult tupleResult = catalog.ExecuteSelectedIndexTuples([row], skip: 0, length: 0, tupleOptions);
        IdentityQueryResult identityResult = catalog.ExecuteIdentities([row], skip: 0, length: 0, identityOptions);
        QueryResult recordResult = catalog.Execute([row], skip: 0, length: 0, recordOptions);
        Console.WriteLine($"Query {label}: tuples={tupleResult.Rows.Count:n0}; identities={identityResult.Rows.Count:n0}; records={recordResult.Files.Count:n0}; expected={expected:n0}; condition={tupleResult.ConditionPreview}");
        if (tupleResult.Rows.Count == expected &&
            identityResult.Rows.Count == expected &&
            recordResult.Files.Count == expected)
        {
            return true;
        }

        IReadOnlyList<FileSearchTupleRow> allRows = catalog.EnumerateTuples(row.Field, skip: 0, length: 0, tupleOptions);
        string expectedKey = row.Field switch
        {
            SearchField.Size => row.Value1,
            SearchField.CreatedUtc => ToUtcTicks(DateTime.Parse(row.Value1, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)).ToString(CultureInfo.InvariantCulture),
            SearchField.ModifiedUtc => ToUtcTicks(DateTime.Parse(row.Value1, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)).ToString(CultureInfo.InvariantCulture),
            SearchField.AccessedUtc => ToUtcTicks(DateTime.Parse(row.Value1, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)).ToString(CultureInfo.InvariantCulture),
            SearchField.Attributes => row.Value1,
            _ => row.Value1
        };
        int allTupleKeyCount = allRows.Count(tuple => string.Equals(tuple.Key, expectedKey, StringComparison.Ordinal));
        Console.WriteLine($"  all-tuples key count {label}: key={expectedKey}; rows={allTupleKeyCount:n0}");
        foreach (FileSearchTupleRow tuple in tupleResult.Rows.Take(3))
        {
            Console.WriteLine($"  query row {label}: key={tuple.Key}; identity={tuple.Identity}");
        }

        return false;
    }

    /// <summary>
    /// Creates a read-options copy with a different display field mode for query-path validation.<br/>
    /// This keeps runner checks aligned with the workbench toggles without mutating the caller's reusable options instance.<br/>
    /// </summary>
    private static FileSearchReadOptions CreateReadOptions(FileSearchReadOptions source, FileSearchDisplayFieldMode displayFieldMode)
    {
        return new FileSearchReadOptions
        {
            IdentityMode = source.IdentityMode,
            RowHydrationMode = source.RowHydrationMode,
            DisplayFieldMode = displayFieldMode,
            IdentityDisplayMode = source.IdentityDisplayMode,
            KeyDisplayMode = source.KeyDisplayMode
        };
    }

    /// <summary>
    /// Reads a string-key FileSearch field from one sidecar record.<br/>
    /// </summary>
    private static string GetStringField(FileRecord file, SearchField field)
    {
        return field switch
        {
            SearchField.FileName => file.FileName,
            SearchField.Extension => file.Extension,
            _ => throw new NotSupportedException($"Field {field} is not a FileSearch string-key field.")
        };
    }

    /// <summary>
    /// Applies the same string semantics used by the PathString raw `VV` workbench query bridge.<br/>
    /// Expected runner counts use this helper so case-sensitive and ignore-case checks remain explicit and reproducible.<br/>
    /// </summary>
    private static bool MatchesStringQuery(string value, QueryRow row)
    {
        string value1 = row.Value1 ?? string.Empty;
        string value2 = row.Value2 ?? string.Empty;
        StringComparison comparison = row.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return row.Operator switch
        {
            SearchOperator.EqualTo => string.Equals(value, value1, comparison),
            SearchOperator.StartsWith => value.StartsWith(value1, comparison),
            SearchOperator.Contains => value.Contains(value1, comparison),
            SearchOperator.GreaterOrEqual => CompareStringRangeValue(value, value1, row.IgnoreCase) >= 0,
            SearchOperator.LessOrEqual => CompareStringRangeValue(value, value1, row.IgnoreCase) <= 0,
            SearchOperator.Between => CompareStringRangeValue(value, value1, row.IgnoreCase) >= 0 &&
                CompareStringRangeValue(value, value2, row.IgnoreCase) <= 0,
            _ => throw new NotSupportedException($"Unsupported string operator {row.Operator}.")
        };
    }

    /// <summary>
    /// Compares string range values using the workbench's user-facing text range semantics.<br/>
    /// Sort-key projections are currently indexed for storage/perf testing, but FileSearch range queries use exact scan/filter until the VV sort-key range contract is validated separately.<br/>
    /// </summary>
    private static int CompareStringRangeValue(string left, string right, bool ignoreCase)
        => string.Compare(left, right, ignoreCase, CultureInfo.InvariantCulture);

    /// <summary>
    /// Formats byte counts in binary units without hiding the sign of delta values.<br/>
    /// Negative deltas are useful after cancellation or compaction because they show released process memory.<br/>
    /// </summary>
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
}
