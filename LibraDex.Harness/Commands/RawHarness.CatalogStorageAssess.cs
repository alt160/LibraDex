using System.Globalization;
using System.Diagnostics;
using LibraDex;

internal static partial class RawHarness
{
    /// <summary>
    /// Opens an existing UInt64-identity catalog and prints its disconnected, non-mutating storage assessment.<br/>
    /// The command reports exact whole-file amplification only when every active physical topology is supported by the current walkers; otherwise it labels the reachable value as a known subtotal.<br/>
    /// </summary>
    /// <param name="args">Command arguments in the form `catalog-storage-assess &lt;catalog-path&gt;`.<br/></param>
    /// <returns>Zero when assessment completes, or one when the path is missing or assessment fails.<br/></returns>
    private static int RunCatalogStorageAssess(string[] args)
    {
        if (args.Length != 2 || string.IsNullOrWhiteSpace(args[1]))
        {
            Console.Error.WriteLine("Usage: catalog-storage-assess <catalog-path>");
            return 1;
        }

        string path = Path.GetFullPath(args[1]);
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"Catalog not found: {path}");
            return 1;
        }

        try
        {
            using Catalog catalog = Catalog.Open(path, CatalogOptions.UInt64Identities);
            LibraDexMaintenanceAssessment assessment = catalog.Maintenance.Assess();
            PrintCatalogStorageAssessment(path, assessment.Storage);
            PrintCatalogIndexShapes(catalog.Indexes.List());
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Catalog storage assessment failed: {exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Prints one stable line-oriented representation of catalog and per-component storage evidence.<br/>
    /// Nullable whole-catalog values are rendered as `unavailable` so incomplete shape coverage cannot be mistaken for zero unreachable storage.<br/>
    /// </summary>
    /// <param name="path">Absolute assessed catalog path.<br/></param>
    /// <param name="storage">Disconnected storage assessment to render.<br/></param>
    private static void PrintCatalogStorageAssessment(string path, LibraDexCatalogStorageAssessment storage)
    {
        Console.WriteLine($"path={path}");
        Console.WriteLine($"physicalBytes={FormatNullableInt64(storage.PhysicalBytes)}");
        Console.WriteLine($"knownReachableBytes={storage.KnownReachableBytes.ToString(CultureInfo.InvariantCulture)}");
        Console.WriteLine($"catalogOverheadReachableBytes={storage.CatalogOverheadReachableBytes.ToString(CultureInfo.InvariantCulture)}");
        Console.WriteLine($"allocatorOverheadReachableBytes={storage.AllocatorOverheadReachableBytes.ToString(CultureInfo.InvariantCulture)}");
        Console.WriteLine($"reusableBytes={storage.ReusableBytes.ToString(CultureInfo.InvariantCulture)}");
        Console.WriteLine($"unreachableBytes={FormatNullableInt64(storage.UnreachableBytes)}");
        Console.WriteLine($"amplification={FormatNullableDouble(storage.AmplificationRatio)}");
        Console.WriteLine($"isComplete={storage.IsComplete}");
        Console.WriteLine($"unsupportedIndexCount={storage.UnsupportedIndexCount.ToString(CultureInfo.InvariantCulture)}");
        Console.WriteLine($"fixedComponentCount={storage.FixedTopologyComponents.Count.ToString(CultureInfo.InvariantCulture)}");
        Console.WriteLine($"variableTextComponentCount={storage.VariableTextTopologyComponents.Count.ToString(CultureInfo.InvariantCulture)}");

        foreach (LibraDexFixedTopologyStorageComponent component in storage.FixedTopologyComponents)
        {
            Console.WriteLine(
                $"fixed group={component.Group} index={component.IndexName} slot={component.PhysicalSlotIndex} generation={component.Generation} " +
                $"tuples={component.TupleCount} reachableBytes={component.ReachableBytes} bytesPerTuple={component.BytesPerLiveTuple.ToString("F3", CultureInfo.InvariantCulture)} " +
                $"routers={component.RouterCount} shelves={component.OrdinaryShelfCount} duplicateShelves={component.DuplicateRunShelfCount} " +
                $"terminalRoots={component.TerminalRootCount} terminalShelves={component.TerminalShelfCount} shelfExtent={component.ShelfExtentSize}");
        }

        foreach (LibraDexVariableTextTopologyStorageComponent component in storage.VariableTextTopologyComponents)
        {
            Console.WriteLine(
                $"text group={component.Group} index={component.IndexName} slot={component.PhysicalSlotIndex} generation={component.Generation} " +
                $"tuples={component.TupleCount} reachableBytes={component.ReachableBytes} bytesPerTuple={component.BytesPerLiveTuple.ToString("F3", CultureInfo.InvariantCulture)} " +
                $"reclaimablePayloadBytes={component.ReclaimablePayloadBytes} routers={component.RouterCount} shelves={component.OrdinaryShelfCount} " +
                $"duplicateShelves={component.DuplicateRunShelfCount} terminalRoots={component.TerminalRootCount} " +
                $"terminalShelves={component.TerminalShelfCount} keyStateRoots={component.KeyStateRootCount}");
        }
    }

    /// <summary>
    /// Formats an optional integer using invariant digits or the explicit unavailable marker.<br/>
    /// </summary>
    /// <param name="value">Optional integer value.<br/></param>
    /// <returns>Invariant digits or `unavailable`.<br/></returns>
    private static string FormatNullableInt64(long? value)
        => value?.ToString(CultureInfo.InvariantCulture) ?? "unavailable";

    /// <summary>
    /// Formats an optional ratio with six invariant fractional digits or the explicit unavailable marker.<br/>
    /// </summary>
    /// <param name="value">Optional ratio value.<br/></param>
    /// <returns>Invariant ratio text or `unavailable`.<br/></returns>
    private static string FormatNullableDouble(double? value)
        => value?.ToString("F6", CultureInfo.InvariantCulture) ?? "unavailable";

    /// <summary>
    /// Prints the persisted shape selectors for every active directory slot so unsupported assessment components can be identified without a debugger or file mutation.<br/>
    /// Projection collections are intentionally omitted because the owning slot and physical companion metadata already identify the storage family needed by a topology walker.<br/>
    /// </summary>
    /// <param name="indexes">Disconnected active catalog-index snapshots.<br/></param>
    private static void PrintCatalogIndexShapes(ReadOnlySpan<CatalogIndexInfo> indexes)
    {
        for (int i = 0; i < indexes.Length; i++)
        {
            ref readonly CatalogIndexInfo index = ref indexes[i];
            Console.WriteLine(
                $"index group={index.Group} name={index.Name} slot={index.SlotIndex} generation={index.Generation} items={index.ItemCount} " +
                $"keyFamily={index.KeyFamily} identityFamily={index.IdentityFamily} keyProfile={index.KeyProfileId} " +
                $"identityProfile={index.IdentityProfileId} routerProfile={index.RouterProfileId} allocationClass={index.AllocationClassId} " +
                $"maxKeyLength={index.VarKeyMaxKeyLength} maxIdentityLength={index.VarIdentityMaxLength} root={index.RootRouterOffset}");
        }
    }

    /// <summary>
    /// Copies one closed UInt64-identity catalog to a new path, compacts only that copy, and reports the compactor's structural-parity result and elapsed time.<br/>
    /// The command refuses an existing destination and distinct-path violations so diagnostic recovery cannot overwrite either the source evidence or another caller-owned file.<br/>
    /// </summary>
    /// <param name="args">Command arguments in the form `catalog-compact-copy &lt;source-path&gt; &lt;new-target-path&gt;`.<br/></param>
    /// <returns>Zero after validated replacement of the copied target, or one after a rejected request or failed copy/compaction.<br/></returns>
    private static int RunCatalogCompactCopy(string[] args)
    {
        if (args.Length != 3 || string.IsNullOrWhiteSpace(args[1]) || string.IsNullOrWhiteSpace(args[2]))
        {
            Console.Error.WriteLine("Usage: catalog-compact-copy <source-path> <new-target-path>");
            return 1;
        }

        string sourcePath = Path.GetFullPath(args[1]);
        string targetPath = Path.GetFullPath(args[2]);
        if (!File.Exists(sourcePath))
        {
            Console.Error.WriteLine($"Catalog not found: {sourcePath}");
            return 1;
        }
        if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Source and target catalog paths must differ.");
            return 1;
        }
        if (File.Exists(targetPath))
        {
            Console.Error.WriteLine($"Target already exists: {targetPath}");
            return 1;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            Stopwatch copyWatch = Stopwatch.StartNew();
            File.Copy(sourcePath, targetPath, overwrite: false);
            copyWatch.Stop();

            Stopwatch compactionWatch = Stopwatch.StartNew();
            LibraDexCompactionResult result = Catalog.Compact(
                targetPath,
                new LibraDexCompactionOptions { CatalogOptions = CatalogOptions.UInt64Identities },
                CancellationToken.None);
            compactionWatch.Stop();

            Console.WriteLine($"sourcePath={sourcePath}");
            Console.WriteLine($"targetPath={targetPath}");
            Console.WriteLine($"copyElapsed={copyWatch.Elapsed:c}");
            Console.WriteLine($"compactionElapsed={compactionWatch.Elapsed:c}");
            Console.WriteLine($"sourceBytes={result.SourceBytes.ToString(CultureInfo.InvariantCulture)}");
            Console.WriteLine($"compactedBytes={result.CompactedBytes.ToString(CultureInfo.InvariantCulture)}");
            Console.WriteLine($"reclaimedBytes={result.ReclaimedBytes.ToString(CultureInfo.InvariantCulture)}");
            Console.WriteLine($"logicalIndexCount={result.LogicalIndexCount.ToString(CultureInfo.InvariantCulture)}");
            Console.WriteLine($"tupleCount={result.TupleCount.ToString(CultureInfo.InvariantCulture)}");
            Console.WriteLine($"recoveredUnorderedIndexCount={result.RecoveredUnorderedIndexCount.ToString(CultureInfo.InvariantCulture)}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Copied-catalog compaction failed: {exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }
}
