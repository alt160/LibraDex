using LibraDex;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

internal static partial class RawHarness
{
    private static int RunFileSearchPathIndexRepro(string[] args)
    {
        string root = GetOption(args, "--root", Directory.Exists(@"C:\msys64") ? @"C:\msys64" : Directory.GetCurrentDirectory());
        int limit = GetIntOption(args, "--limit", 100_000);
        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine($"filesearch-path-index-repro root not found: {root}");
            return 1;
        }

        using Catalog catalog = Catalog.CreateMemory();
        CatalogIdentityGroupIndexes group = catalog.CreateIndexSet("files");
        LibraDexStringScalar8Index pathIndex = group["path"].StringKeys().CreateOrOpen(StringKeys.ExactAndFolded);

        long scanned = 0;
        ulong id = 1;
        foreach (string path in EnumerateFileSearchReproFiles(root))
        {
            if (scanned >= limit)
            {
                break;
            }

            scanned++;
            if (!TryInsertFileSearchRepro("path", path, id, () => pathIndex.Insert(path, id), scanned))
            {
                return 1;
            }

            id++;
        }

        Console.WriteLine($"filesearch-path-index-repro ok root={root} scanned={scanned:n0} indexed={id - 1:n0}");
        return 0;
    }

    private static int RunFileSearchMetadataIndexRepro(string[] args)
    {
        string root = GetOption(args, "--root", Directory.Exists(@"C:\msys64") ? @"C:\msys64" : Directory.GetCurrentDirectory());
        int limit = GetIntOption(args, "--limit", 100_000);
        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine($"filesearch-metadata-index-repro root not found: {root}");
            return 1;
        }

        using Catalog catalog = Catalog.CreateMemory();
        CatalogIdentityGroupIndexes group = catalog.CreateIndexSet("files");
        LibraDexIndex<ulong, ulong> sizeIndex = group["size"].CreateOrOpen<ulong, ulong>();
        LibraDexIndex<ulong, ulong> createdUtcIndex = group["createdUtc"].CreateOrOpen<ulong, ulong>();
        LibraDexIndex<ulong, ulong> modifiedUtcIndex = group["modifiedUtc"].CreateOrOpen<ulong, ulong>();
        LibraDexIndex<ulong, ulong> accessedUtcIndex = group["accessedUtc"].CreateOrOpen<ulong, ulong>();
        LibraDexIndex<uint, ulong> attributesIndex = group["attributes"].CreateOrOpen<uint, ulong>();

        long scanned = 0;
        ulong id = 1;
        foreach (string path in EnumerateFileSearchReproFiles(root))
        {
            if (scanned >= limit)
            {
                break;
            }

            scanned++;
            FileInfo info;
            try
            {
                info = new FileInfo(path);
            }
            catch
            {
                continue;
            }

            ulong size = checked((ulong)Math.Max(0, info.Length));
            DateTime createdUtc = DateTime.SpecifyKind(info.CreationTimeUtc, DateTimeKind.Utc);
            DateTime modifiedUtc = DateTime.SpecifyKind(info.LastWriteTimeUtc, DateTimeKind.Utc);
            DateTime accessedUtc = DateTime.SpecifyKind(info.LastAccessTimeUtc, DateTimeKind.Utc);
            uint attributes = (uint)info.Attributes;

            if (!TryInsertFileSearchRepro("size", path, id, () => sizeIndex.Insert(size, id), scanned)) return 1;
            if (!TryInsertFileSearchRepro("createdUtc", path, id, () => createdUtcIndex.Insert(checked((ulong)createdUtc.Ticks), id), scanned)) return 1;
            if (!TryInsertFileSearchRepro("modifiedUtc", path, id, () => modifiedUtcIndex.Insert(checked((ulong)modifiedUtc.Ticks), id), scanned)) return 1;
            if (!TryInsertFileSearchRepro("accessedUtc", path, id, () => accessedUtcIndex.Insert(checked((ulong)accessedUtc.Ticks), id), scanned)) return 1;
            if (!TryInsertFileSearchRepro("attributes", path, id, () => attributesIndex.Insert(attributes, id), scanned)) return 1;
            id++;
        }

        Console.WriteLine($"filesearch-metadata-index-repro ok root={root} scanned={scanned:n0} indexed={id - 1:n0}");
        return 0;
    }

    private static int RunFileSearchGroupBatchRepro(string[] args)
    {
        string root = GetOption(args, "--root", Directory.Exists(@"C:\msys64") ? @"C:\msys64" : Directory.GetCurrentDirectory());
        int limit = GetIntOption(args, "--limit", 100_000);
        int commitEvery = GetIntOption(args, "--commit-every", 200);
        string writeOrder = GetOption(args, "--write-order", "default");
        bool includeExtension = GetBoolOption(args, "--include-extension", true);
        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine($"filesearch-group-batch-repro root not found: {root}");
            return 1;
        }

        using Catalog catalog = Catalog.CreateMemory();
        CatalogIdentityGroupIndexes group = catalog.CreateIndexSet("files");
        LibraDexStringScalar8Index pathIndex = group["path"].StringKeys().CreateOrOpen(StringKeys.ExactAndFolded);
        LibraDexStringScalar8Index fileNameIndex = group["fileName"].StringKeys().CreateOrOpen(StringKeys.ExactAndFolded);
        IIndex extensionIndex = group["extension"].Composite<ulong>(C.Text("extension"), C.Scalar<ulong>("id")).CreateOrOpen();
        LibraDexIndex<ulong, ulong> sizeIndex = group["size"].CreateOrOpen<ulong, ulong>();
        LibraDexIndex<ulong, ulong> createdUtcIndex = group["createdUtc"].CreateOrOpen<ulong, ulong>();
        LibraDexIndex<ulong, ulong> modifiedUtcIndex = group["modifiedUtc"].CreateOrOpen<ulong, ulong>();
        LibraDexIndex<ulong, ulong> accessedUtcIndex = group["accessedUtc"].CreateOrOpen<ulong, ulong>();
        LibraDexIndex<uint, ulong> attributesIndex = group["attributes"].CreateOrOpen<uint, ulong>();
        CatalogIdentityGroupBatchManager batch = group.Batch;
        batch.Enable(new LibraDexWriteIntent(
            LibraDexWriteOrder.Default,
            LibraDexWriteVolume.Thousands,
            LibraDexWriteLocality.Clustered,
            LibraDexWritePriority.WriteSpeed));

        List<string> paths = new();
        foreach (string path in EnumerateFileSearchReproFiles(root))
        {
            paths.Add(path);
            if (paths.Count >= limit)
            {
                break;
            }
        }

        if (writeOrder.Equals("sorted", StringComparison.OrdinalIgnoreCase))
        {
            paths.Sort(static (left, right) => string.Compare(left, right, StringComparison.OrdinalIgnoreCase));
        }

        long scanned = 0;
        ulong id = 1;
        try
        {
            foreach (string path in paths)
            {
                scanned++;
                FileInfo info;
                try
                {
                    info = new FileInfo(path);
                }
                catch
                {
                    continue;
                }

                string fileName = Path.GetFileName(path);
                string extension = Path.GetExtension(path);
                ulong size = checked((ulong)Math.Max(0, info.Length));
                DateTime createdUtc = DateTime.SpecifyKind(info.CreationTimeUtc, DateTimeKind.Utc);
                DateTime modifiedUtc = DateTime.SpecifyKind(info.LastWriteTimeUtc, DateTimeKind.Utc);
                DateTime accessedUtc = DateTime.SpecifyKind(info.LastAccessTimeUtc, DateTimeKind.Utc);
                uint attributes = (uint)info.Attributes;

                if (!TryInsertFileSearchRepro("path", path, id, () => pathIndex.Insert(path, id), scanned)) return 1;
                if (!TryInsertFileSearchRepro("fileName", path, id, () => fileNameIndex.Insert(fileName, id), scanned)) return 1;
                if (includeExtension && !TryInsertFileSearchRepro("extension", path, id, () => extensionIndex.Insert(Key.Of(extension, id), id), scanned)) return 1;
                if (!TryInsertFileSearchRepro("size", path, id, () => sizeIndex.Insert(size, id), scanned)) return 1;
                if (!TryInsertFileSearchRepro("createdUtc", path, id, () => createdUtcIndex.Insert(checked((ulong)createdUtc.Ticks), id), scanned)) return 1;
                if (!TryInsertFileSearchRepro("modifiedUtc", path, id, () => modifiedUtcIndex.Insert(checked((ulong)modifiedUtc.Ticks), id), scanned)) return 1;
                if (!TryInsertFileSearchRepro("accessedUtc", path, id, () => accessedUtcIndex.Insert(checked((ulong)accessedUtc.Ticks), id), scanned)) return 1;
                if (!TryInsertFileSearchRepro("attributes", path, id, () => attributesIndex.Insert(attributes, id), scanned)) return 1;

                if (commitEvery > 0 && scanned % commitEvery == 0)
                {
                    _ = batch.Commit();
                }

                id++;
            }

            _ = batch.CommitAndDisable();
        }
        catch
        {
            if (batch.IsEnabled)
            {
                _ = batch.AbortAndDisable();
            }

            throw;
        }

        Console.WriteLine($"filesearch-group-batch-repro ok root={root} scanned={scanned:n0} indexed={id - 1:n0} commitEvery={commitEvery:n0} writeOrder={writeOrder} includeExtension={includeExtension}");
        return 0;
    }

    private static int RunFileSearchUiShapedRepro(string[] args)
    {
        string root = GetOption(args, "--root", Directory.Exists(@"C:\msys64") ? @"C:\msys64" : Directory.GetCurrentDirectory());
        int limit = GetIntOption(args, "--limit", 100_000);
        int commitEvery = GetIntOption(args, "--commit-every", 200);
        bool includeExtension = GetBoolOption(args, "--include-extension", true);
        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine($"filesearch-ui-shaped-repro root not found: {root}");
            return 1;
        }

        List<FileSearchHarnessRecord> records = new();
        ulong nextId = 1;
        long scanned = 0;
        foreach (string path in EnumerateFileSearchReproFiles(root))
        {
            if (scanned >= limit)
            {
                break;
            }

            scanned++;
            if (TryCreateFileSearchHarnessRecord(path, nextId, out FileSearchHarnessRecord record))
            {
                records.Add(record);
                nextId++;
            }
        }

        using Catalog catalog = Catalog.CreateMemory();
        CatalogIdentityGroupIndexes group = catalog.CreateIndexSet("files");
        LibraDexStringScalar8Index pathIndex = group["path"].StringKeys().CreateOrOpen(StringKeys.ExactAndFolded);
        LibraDexStringScalar8Index fileNameIndex = group["fileName"].StringKeys().CreateOrOpen(StringKeys.ExactAndFolded);
        IIndex extensionIndex = group["extension"].Composite<ulong>(C.Text("extension"), C.Scalar<ulong>("id")).CreateOrOpen();
        LibraDexIndex<ulong, ulong> sizeIndex = group["size"].CreateOrOpen<ulong, ulong>();
        LibraDexIndex<ulong, ulong> createdUtcIndex = group["createdUtc"].CreateOrOpen<ulong, ulong>();
        LibraDexIndex<ulong, ulong> modifiedUtcIndex = group["modifiedUtc"].CreateOrOpen<ulong, ulong>();
        LibraDexIndex<ulong, ulong> accessedUtcIndex = group["accessedUtc"].CreateOrOpen<ulong, ulong>();
        LibraDexIndex<uint, ulong> attributesIndex = group["attributes"].CreateOrOpen<uint, ulong>();
        CatalogIdentityGroupBatchManager batch = group.Batch;
        batch.Enable(new LibraDexWriteIntent(
            LibraDexWriteOrder.Default,
            LibraDexWriteVolume.Thousands,
            LibraDexWriteLocality.Clustered,
            LibraDexWritePriority.WriteSpeed));

        long indexed = 0;
        try
        {
            for (int i = 0; i < records.Count; i++)
            {
                FileSearchHarnessRecord record = records[i];
                if (!TryInsertFileSearchRepro("path", record.Path, record.Id, () => pathIndex.Insert(record.Path, record.Id), indexed)) return 1;
                if (!TryInsertFileSearchRepro("fileName", record.Path, record.Id, () => fileNameIndex.Insert(record.FileName, record.Id), indexed)) return 1;
                if (includeExtension && !TryInsertFileSearchRepro("extension", record.Path, record.Id, () => extensionIndex.Insert(Key.Of(record.Extension, record.Id), record.Id), indexed)) return 1;
                if (!TryInsertFileSearchRepro("size", record.Path, record.Id, () => sizeIndex.Insert(record.Size, record.Id), indexed)) return 1;
                if (!TryInsertFileSearchRepro("createdUtc", record.Path, record.Id, () => createdUtcIndex.Insert(checked((ulong)record.CreatedUtc.Ticks), record.Id), indexed)) return 1;
                if (!TryInsertFileSearchRepro("modifiedUtc", record.Path, record.Id, () => modifiedUtcIndex.Insert(checked((ulong)record.ModifiedUtc.Ticks), record.Id), indexed)) return 1;
                if (!TryInsertFileSearchRepro("accessedUtc", record.Path, record.Id, () => accessedUtcIndex.Insert(checked((ulong)record.AccessedUtc.Ticks), record.Id), indexed)) return 1;
                if (!TryInsertFileSearchRepro("attributes", record.Path, record.Id, () => attributesIndex.Insert(record.Attributes, record.Id), indexed)) return 1;

                indexed++;
                if (commitEvery > 0 && indexed % commitEvery == 0)
                {
                    _ = batch.Commit();
                }
            }

            _ = batch.CommitAndDisable();
        }
        catch
        {
            if (batch.IsEnabled)
            {
                _ = batch.AbortAndDisable();
            }

            throw;
        }

        Console.WriteLine($"filesearch-ui-shaped-repro ok root={root} scanned={scanned:n0} indexed={indexed:n0} records={records.Count:n0} commitEvery={commitEvery:n0} includeExtension={includeExtension}");
        return 0;
    }

    private static int RunFileSearchPathIdentityScalar8VarIdentityRepro(string[] args)
    {
        string root = GetOption(args, "--root", Directory.Exists(@"C:\msys64") ? @"C:\msys64" : Directory.GetCurrentDirectory());
        int limit = GetIntOption(args, "--limit", 10_000);
        int commitEvery = GetIntOption(args, "--commit-every", 1);
        string order = GetOption(args, "--order", "default");
        string fieldFilter = GetOption(args, "--field", "all");
        bool includeSize = fieldFilter.Equals("all", StringComparison.OrdinalIgnoreCase) || fieldFilter.Equals("size", StringComparison.OrdinalIgnoreCase);
        bool includeCreatedUtc = fieldFilter.Equals("all", StringComparison.OrdinalIgnoreCase) || fieldFilter.Equals("createdUtc", StringComparison.OrdinalIgnoreCase);
        bool includeModifiedUtc = fieldFilter.Equals("all", StringComparison.OrdinalIgnoreCase) || fieldFilter.Equals("modifiedUtc", StringComparison.OrdinalIgnoreCase);
        bool includeAccessedUtc = fieldFilter.Equals("all", StringComparison.OrdinalIgnoreCase) || fieldFilter.Equals("accessedUtc", StringComparison.OrdinalIgnoreCase);
        bool includeAttributes = fieldFilter.Equals("all", StringComparison.OrdinalIgnoreCase) || fieldFilter.Equals("attributes", StringComparison.OrdinalIgnoreCase);
        if (!includeSize && !includeCreatedUtc && !includeModifiedUtc && !includeAccessedUtc && !includeAttributes)
        {
            throw new ArgumentException("The --field option must be all, size, createdUtc, modifiedUtc, accessedUtc, or attributes.", nameof(args));
        }
        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine($"filesearch-pathidentity-sv8-repro root not found: {root}");
            return 1;
        }

        List<FileSearchHarnessRecord> records = new();
        ulong nextId = 1;
        long scanned = 0;
        int maxPathIdentityBytes = 1;
        foreach (string path in EnumerateFileSearchReproFiles(root))
        {
            if (scanned >= limit)
            {
                break;
            }

            scanned++;
            if (TryCreateFileSearchHarnessRecord(path, nextId, out FileSearchHarnessRecord record))
            {
                records.Add(record);
                int byteCount = Encoding.UTF8.GetByteCount(record.Path);
                if (byteCount > maxPathIdentityBytes)
                {
                    maxPathIdentityBytes = byteCount;
                }

                nextId++;
            }
        }

        if (order.Equals("path", StringComparison.OrdinalIgnoreCase))
        {
            records.Sort(static (left, right) => string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase));
        }
        else if (!order.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The --order option must be default or path.", nameof(args));
        }

        using Catalog catalog = Catalog.CreateMemory();
        CatalogIdentityGroupIndexes group = catalog.CreateIndexSet("files");
        LibraDexUInt64VarIdentityIndex sizeIndex = group["size"].UInt64VarIdentityKeys(maxPathIdentityBytes).CreateOrOpen();
        LibraDexUInt64VarIdentityIndex createdUtcIndex = group["createdUtc"].UInt64VarIdentityKeys(maxPathIdentityBytes).CreateOrOpen();
        LibraDexUInt64VarIdentityIndex modifiedUtcIndex = group["modifiedUtc"].UInt64VarIdentityKeys(maxPathIdentityBytes).CreateOrOpen();
        LibraDexUInt64VarIdentityIndex accessedUtcIndex = group["accessedUtc"].UInt64VarIdentityKeys(maxPathIdentityBytes).CreateOrOpen();
        LibraDexUInt64VarIdentityIndex attributesIndex = group["attributes"].UInt64VarIdentityKeys(maxPathIdentityBytes).CreateOrOpen();
        CatalogIdentityGroupBatchManager batch = group.Batch;
        batch.Enable(new LibraDexWriteIntent(
            LibraDexWriteOrder.Default,
            LibraDexWriteVolume.Thousands,
            LibraDexWriteLocality.Clustered,
            LibraDexWritePriority.WriteSpeed));

        Dictionary<ulong, List<string>> expectedSize = new();
        Dictionary<ulong, List<string>> expectedCreatedUtc = new();
        Dictionary<ulong, List<string>> expectedModifiedUtc = new();
        Dictionary<ulong, List<string>> expectedAccessedUtc = new();
        Dictionary<ulong, List<string>> expectedAttributes = new();
        long[] kindCounts = new long[8];
        long[] extentCounts = new long[129];
        long commitBytesWritten = 0;
        long commitStagedExtents = 0;
        long commitDeferredRequests = 0;
        long maxCommitBytesWritten = 0;
        long commitScalar8VarIdentityDirtyShelves = 0;
        long maxCommitScalar8VarIdentityDirtyShelves = 0;
        long indexed = 0;
        System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        try
        {
            for (int i = 0; i < records.Count; i++)
            {
                FileSearchHarnessRecord record = records[i];
                byte[] identity = Encoding.UTF8.GetBytes(record.Path);
                ulong createdTicks = checked((ulong)record.CreatedUtc.Ticks);
                ulong modifiedTicks = checked((ulong)record.ModifiedUtc.Ticks);
                ulong accessedTicks = checked((ulong)record.AccessedUtc.Ticks);
                if (includeSize)
                {
                    Scalar8VarIdentityInsertOutcome sizeOutcome = sizeIndex.InsertInCurrentScopeDetailed(record.Size, identity);
                    ValidatePathIdentityInsert("size", sizeOutcome);
                    RecordFileSearchPathIdentityOutcome(sizeOutcome, kindCounts, extentCounts);
                    AddExpectedPathIdentity(expectedSize, record.Size, record.Path);
                }

                if (includeCreatedUtc)
                {
                    Scalar8VarIdentityInsertOutcome createdOutcome = createdUtcIndex.InsertInCurrentScopeDetailed(createdTicks, identity);
                    ValidatePathIdentityInsert("createdUtc", createdOutcome);
                    RecordFileSearchPathIdentityOutcome(createdOutcome, kindCounts, extentCounts);
                    AddExpectedPathIdentity(expectedCreatedUtc, createdTicks, record.Path);
                }

                if (includeModifiedUtc)
                {
                    Scalar8VarIdentityInsertOutcome modifiedOutcome = modifiedUtcIndex.InsertInCurrentScopeDetailed(modifiedTicks, identity);
                    ValidatePathIdentityInsert("modifiedUtc", modifiedOutcome);
                    RecordFileSearchPathIdentityOutcome(modifiedOutcome, kindCounts, extentCounts);
                    AddExpectedPathIdentity(expectedModifiedUtc, modifiedTicks, record.Path);
                }

                if (includeAccessedUtc)
                {
                    Scalar8VarIdentityInsertOutcome accessedOutcome = accessedUtcIndex.InsertInCurrentScopeDetailed(accessedTicks, identity);
                    ValidatePathIdentityInsert("accessedUtc", accessedOutcome);
                    RecordFileSearchPathIdentityOutcome(accessedOutcome, kindCounts, extentCounts);
                    AddExpectedPathIdentity(expectedAccessedUtc, accessedTicks, record.Path);
                }

                if (includeAttributes)
                {
                    Scalar8VarIdentityInsertOutcome attributesOutcome = attributesIndex.InsertInCurrentScopeDetailed(record.Attributes, identity);
                    ValidatePathIdentityInsert("attributes", attributesOutcome);
                    RecordFileSearchPathIdentityOutcome(attributesOutcome, kindCounts, extentCounts);
                    AddExpectedPathIdentity(expectedAttributes, record.Attributes, record.Path);
                }
                indexed++;
                if (commitEvery > 0 && indexed % commitEvery == 0)
                {
                    RecordFileSearchPathIdentityCommit(batch.Commit(), ref commitBytesWritten, ref commitStagedExtents, ref commitDeferredRequests, ref maxCommitBytesWritten, ref commitScalar8VarIdentityDirtyShelves, ref maxCommitScalar8VarIdentityDirtyShelves);
                }
            }

            RecordFileSearchPathIdentityCommit(batch.CommitAndDisable(), ref commitBytesWritten, ref commitStagedExtents, ref commitDeferredRequests, ref maxCommitBytesWritten, ref commitScalar8VarIdentityDirtyShelves, ref maxCommitScalar8VarIdentityDirtyShelves);
        }
        catch
        {
            if (batch.IsEnabled)
            {
                _ = batch.AbortAndDisable();
            }

            throw;
        }

        long allocatedAfter = GC.GetTotalAllocatedBytes(precise: true);
        if (includeSize) ValidatePathIdentityIndex("size", sizeIndex, expectedSize);
        if (includeCreatedUtc) ValidatePathIdentityIndex("createdUtc", createdUtcIndex, expectedCreatedUtc);
        if (includeModifiedUtc) ValidatePathIdentityIndex("modifiedUtc", modifiedUtcIndex, expectedModifiedUtc);
        if (includeAccessedUtc) ValidatePathIdentityIndex("accessedUtc", accessedUtcIndex, expectedAccessedUtc);
        if (includeAttributes) ValidatePathIdentityIndex("attributes", attributesIndex, expectedAttributes);
        string memoryText = catalog.GetMemoryDiagnostics().ToString();
        Console.WriteLine($"filesearch-pathidentity-sv8-repro ok root={root} field={fieldFilter} order={order} scanned={scanned:n0} indexed={indexed:n0} records={records.Count:n0} commitEvery={commitEvery:n0} elapsed={elapsed.Elapsed.TotalSeconds:n2}s allocatedDelta={FormatFileSearchHarnessBytes(allocatedAfter - allocatedBefore)} commitBytes={FormatFileSearchHarnessBytes(commitBytesWritten)} maxCommitBytes={FormatFileSearchHarnessBytes(maxCommitBytesWritten)} stagedExtents={commitStagedExtents:n0} deferred={commitDeferredRequests:n0} sv8DirtySum={commitScalar8VarIdentityDirtyShelves:n0} sv8DirtyMax={maxCommitScalar8VarIdentityDirtyShelves:n0} kinds={FormatFileSearchPathIdentityKindCounts(kindCounts)} extents={FormatFileSearchPathIdentityExtentCounts(extentCounts)} memory={memoryText}");
        return 0;
    }

    /// <summary>
    /// Validates one `SV8` path-identity insert result in the FileSearch-shaped harness.<br/>
    /// The harness treats non-insert outcomes as structural failures because every file path identity is expected to be unique for a given scalar-key bucket.<br/>
    /// </summary>
    /// <param name="fieldName">The field currently being inserted.</param>
    /// <param name="result">The detailed `SV8` insert outcome.</param>
    private static void ValidatePathIdentityInsert(string fieldName, Scalar8VarIdentityInsertOutcome result)
    {
        if (!result.Inserted)
        {
            throw new InvalidOperationException($"PathIdentity SV8 insert did not insert field={fieldName}; Kind={result.Kind}; InsertResult={result.InsertResult}; TargetItems={result.TargetShelfItemCount:n0}; TargetBytes={result.TargetShelfExtentSize:n0}.");
        }
    }

    /// <summary>
    /// Records one focused FileSearch path-identity `SV8` insert outcome into compact distribution counters.<br/>
    /// The harness keeps this allocation-free on the hot loop so diagnostic runs do not distort the insert path being measured.<br/>
    /// </summary>
    /// <param name="outcome">The routed insert outcome returned by the `SV8` facade.<br/></param>
    /// <param name="kindCounts">Counters keyed by <see cref="Scalar8VarIdentityRoutedInsertKind"/> integer value.<br/></param>
    /// <param name="extentCounts">Counters keyed by KiB shelf extent size.<br/></param>
    private static void RecordFileSearchPathIdentityOutcome(
        Scalar8VarIdentityInsertOutcome outcome,
        long[] kindCounts,
        long[] extentCounts)
    {
        int kindIndex = (int)outcome.Kind;
        if ((uint)kindIndex < (uint)kindCounts.Length)
        {
            kindCounts[kindIndex]++;
        }

        int extentKiB = outcome.TargetShelfExtentSize / 1024;
        if ((uint)extentKiB < (uint)extentCounts.Length)
        {
            extentCounts[extentKiB]++;
        }
    }

    /// <summary>
    /// Aggregates one focused FileSearch path-identity batch commit diagnostic sample.<br/>
    /// The counters connect logical insert behavior to DataKernel publication volume without adding per-insert logging allocations.<br/>
    /// </summary>
    /// <param name="commit">The generic batch commit result returned by the catalog group batch manager.<br/></param>
    /// <param name="bytesWritten">Aggregate committed byte counter to update.<br/></param>
    /// <param name="stagedExtents">Aggregate staged extent counter to update.<br/></param>
    /// <param name="deferredRequests">Aggregate deferred lower-level commit request counter to update.<br/></param>
    /// <param name="maxBytesWritten">Largest single commit byte counter to update.<br/></param>
    private static void RecordFileSearchPathIdentityCommit(
        LibraDexGenericBatchCommitResult commit,
        ref long bytesWritten,
        ref long stagedExtents,
        ref long deferredRequests,
        ref long maxBytesWritten,
        ref long scalar8VarIdentityDirtyShelves,
        ref long maxScalar8VarIdentityDirtyShelves)
    {
        bytesWritten += commit.CommitDiagnostics.BytesWritten;
        stagedExtents += commit.CommitDiagnostics.StagedExtentCount;
        deferredRequests += commit.DeferredCommitRequests;
        scalar8VarIdentityDirtyShelves += commit.StorageDiagnostics.Scalar8VarIdentityDirtyShelves;
        if (commit.CommitDiagnostics.BytesWritten > maxBytesWritten)
        {
            maxBytesWritten = commit.CommitDiagnostics.BytesWritten;
        }

        if (commit.StorageDiagnostics.Scalar8VarIdentityDirtyShelves > maxScalar8VarIdentityDirtyShelves)
        {
            maxScalar8VarIdentityDirtyShelves = commit.StorageDiagnostics.Scalar8VarIdentityDirtyShelves;
        }
    }

    /// <summary>
    /// Formats focused FileSearch path-identity `SV8` insert-kind counters for quick regression comparison.<br/>
    /// Zero-count insert kinds are omitted so the line stays readable in long harness sweeps.<br/>
    /// </summary>
    /// <param name="counts">Counters keyed by <see cref="Scalar8VarIdentityRoutedInsertKind"/> integer value.<br/></param>
    /// <returns>A compact comma-separated counter string.</returns>
    private static string FormatFileSearchPathIdentityKindCounts(long[] counts)
    {
        string text = string.Empty;
        for (int i = 0; i < counts.Length; i++)
        {
            if (counts[i] == 0)
            {
                continue;
            }

            string name = Enum.GetName(typeof(Scalar8VarIdentityRoutedInsertKind), i) ?? i.ToString(CultureInfo.InvariantCulture);
            text = text.Length == 0
                ? $"{name}:{counts[i]:n0}"
                : $"{text},{name}:{counts[i]:n0}";
        }

        return text.Length == 0 ? "none" : text;
    }

    /// <summary>
    /// Formats focused FileSearch path-identity `SV8` shelf-extent counters for quick memory-shape inspection.<br/>
    /// The counter is keyed by KiB so the output reports extent classes such as `4K`, `8K`, and `128K` directly.<br/>
    /// </summary>
    /// <param name="counts">Counters keyed by KiB shelf extent size.<br/></param>
    /// <returns>A compact comma-separated counter string.</returns>
    private static string FormatFileSearchPathIdentityExtentCounts(long[] counts)
    {
        string text = string.Empty;
        for (int i = 0; i < counts.Length; i++)
        {
            if (counts[i] == 0)
            {
                continue;
            }

            text = text.Length == 0
                ? $"{i}K:{counts[i]:n0}"
                : $"{text},{i}K:{counts[i]:n0}";
        }

        return text.Length == 0 ? "none" : text;
    }

    /// <summary>
    /// Adds one expected path identity to the scalar-key bucket map used by the FileSearch-shaped `SV8` harness.<br/>
    /// Bucket lists are sorted during validation so insert order and read order can differ without hiding duplicate or missing identities.<br/>
    /// </summary>
    /// <param name="expected">The expected bucket map.</param>
    /// <param name="key">The scalar key bucket.</param>
    /// <param name="path">The path identity text.</param>
    private static void AddExpectedPathIdentity(Dictionary<ulong, List<string>> expected, ulong key, string path)
    {
        if (!expected.TryGetValue(key, out List<string>? paths))
        {
            paths = new List<string>();
            expected[key] = paths;
        }

        paths.Add(path);
    }

    /// <summary>
    /// Validates all key buckets in one `UInt64/SV8` path-identity index against the expected FileSearch path set.<br/>
    /// This catches stale route aliases, duplicate tuple visibility, and missing identities after duplicate-key route conversion.<br/>
    /// </summary>
    /// <param name="fieldName">The index field name for diagnostics.</param>
    /// <param name="index">The `UInt64/SV8` index under test.</param>
    /// <param name="expected">Expected scalar-key buckets and path identities.</param>
    private static void ValidatePathIdentityIndex(string fieldName, LibraDexUInt64VarIdentityIndex index, Dictionary<ulong, List<string>> expected)
    {
        long expectedTotal = 0;
        long actualTotal = 0;
        foreach (KeyValuePair<ulong, List<string>> pair in expected)
        {
            expectedTotal += pair.Value.Count;
            List<string> actual = index.GetIdentities(pair.Key, pair.Key)
                .Select(static identity => Encoding.UTF8.GetString(identity))
                .ToList();
            actualTotal += actual.Count;
            pair.Value.Sort(StringComparer.Ordinal);
            actual.Sort(StringComparer.Ordinal);
            if (pair.Value.Count != actual.Count || !pair.Value.SequenceEqual(actual))
            {
                string missing = pair.Value.Except(actual, StringComparer.Ordinal).Take(3).DefaultIfEmpty("<none>").Aggregate(static (left, right) => left + " | " + right);
                string extra = actual.Except(pair.Value, StringComparer.Ordinal).Take(3).DefaultIfEmpty("<none>").Aggregate(static (left, right) => left + " | " + right);
                string duplicateActual = actual.GroupBy(static path => path, StringComparer.Ordinal)
                    .Where(static group => group.Count() > 1)
                    .Select(static group => $"{group.Count():n0}x {group.Key}")
                    .Take(3)
                    .DefaultIfEmpty("<none>")
                    .Aggregate(static (left, right) => left + " | " + right);
                string firstMismatch = "<none>";
                int compareCount = Math.Min(pair.Value.Count, actual.Count);
                for (int i = 0; i < compareCount; i++)
                {
                    if (!string.Equals(pair.Value[i], actual[i], StringComparison.Ordinal))
                    {
                        firstMismatch = $"#{i:n0} expected={pair.Value[i]} actual={actual[i]}";
                        break;
                    }
                }

                string routes = index.DescribeExactKeyRoutes(pair.Key, maxRows: 16);
                throw new InvalidDataException($"PathIdentity SV8 mismatch field={fieldName}; key={pair.Key}; expected={pair.Value.Count:n0}; actual={actual.Count:n0}; missing={missing}; extra={extra}; duplicateActual={duplicateActual}; firstMismatch={firstMismatch}.{Environment.NewLine}{routes}");
            }
        }

        if (expectedTotal != actualTotal)
        {
            throw new InvalidDataException($"PathIdentity SV8 total mismatch field={fieldName}; expected={expectedTotal:n0}; actual={actualTotal:n0}.");
        }
    }

    private static string FormatFileSearchHarnessBytes(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB"];
        double value = bytes;
        int suffix = 0;
        while (value >= 1024 && suffix < suffixes.Length - 1)
        {
            value /= 1024;
            suffix++;
        }

        return suffix == 0
            ? $"{bytes:n0} {suffixes[suffix]}"
            : $"{value:n1} {suffixes[suffix]}";
    }

    private static bool TryInsertFileSearchRepro(
        string fieldName,
        string path,
        ulong id,
        Func<LibraDexGenericInsertResult> insert,
        long scanned)
    {
        try
        {
            _ = insert();
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"filesearch-metadata-index-repro reproduced field={fieldName} scanned={scanned:n0} id={id:n0}");
            Console.Error.WriteLine($"path={path}");
            Console.Error.WriteLine(ex);
            return false;
        }
    }

    private static IEnumerable<string> EnumerateFileSearchReproFiles(string root)
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

    private static bool TryCreateFileSearchHarnessRecord(string path, ulong id, out FileSearchHarnessRecord record)
    {
        try
        {
            FileInfo info = new(path);
            record = new FileSearchHarnessRecord(
                id,
                info.FullName,
                info.Name,
                NormalizeFileSearchHarnessExtension(info.Extension),
                checked((ulong)Math.Max(0, info.Length)),
                DateTime.SpecifyKind(info.CreationTimeUtc, DateTimeKind.Utc),
                DateTime.SpecifyKind(info.LastWriteTimeUtc, DateTimeKind.Utc),
                DateTime.SpecifyKind(info.LastAccessTimeUtc, DateTimeKind.Utc),
                (uint)info.Attributes,
                0,
                0);
            SetFileSearchHarnessPathHash(ref record);
            return true;
        }
        catch
        {
            record = default;
            return false;
        }
    }

    private static string NormalizeFileSearchHarnessExtension(string extension)
    {
        if (string.IsNullOrEmpty(extension))
        {
            return string.Empty;
        }

        return extension[0] == '.'
            ? extension[1..].ToLowerInvariant()
            : extension.ToLowerInvariant();
    }

    private static void SetFileSearchHarnessPathHash(ref FileSearchHarnessRecord record)
    {
        Span<byte> hash = stackalloc byte[16];
        string normalized = record.Path.ToUpperInvariant();
        byte[] bytes = Encoding.UTF8.GetBytes(normalized);
        _ = MD5.HashData(bytes, hash);
        record = record with
        {
            PathHashHigh = BinaryPrimitives.ReadUInt64BigEndian(hash[..8]),
            PathHashLow = BinaryPrimitives.ReadUInt64BigEndian(hash.Slice(8, 8))
        };
    }

    private readonly record struct FileSearchHarnessRecord(
        ulong Id,
        string Path,
        string FileName,
        string Extension,
        ulong Size,
        DateTime CreatedUtc,
        DateTime ModifiedUtc,
        DateTime AccessedUtc,
        uint Attributes,
        ulong PathHashHigh,
        ulong PathHashLow);
}
