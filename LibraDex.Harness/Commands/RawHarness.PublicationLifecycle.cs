using System.Buffers.Binary;
using System.Text.Json;
using LibraDex;
using LibraDex.Layouts;

internal static partial class RawHarness
{
    /// <summary>
    /// Contrives closed-file copies and renames while a version-two embedded publication is active, then validates that each destination independently recovers the new image.<br/>
    /// The same probe retains prototype-sidecar destination guards because an interrupted development fixture may still carry those compatibility artifacts.<br/>
    /// Inspect mode supports current-reader validation and deterministic rejection checks against a preserved version-one reader.<br/>
    /// </summary>
    private static int RunPublicationLifecycleProbe(string[] args)
    {
        if (args.Contains("--inspect"))
        {
            string path = GetOption(args, "--inspect", "");
            int actual = InspectPublicationLifecycle(path);
            Console.WriteLine($"INSPECT records={actual} journalPresent={File.Exists(path + ".publication-ready")}");
            return actual == int.Parse(GetOption(args, "--expected", "226")) ? 0 : 1;
        }
        string folder = Path.GetFullPath(Path.Combine("artifacts", "publication-lifecycle-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(folder); var results = new List<object>();
        string source = Path.Combine(folder, "interrupted.lbdx");
        var options = CreateDesignPerfOptions() with { FlushToDiskOnCommit = true };
        bool interrupted = false;
        using (var session = LibraDexFileSession.Initialize(
            source,
            options,
            CreateDesignPerfMetadata(9626),
            DataKernelTelemetryOptions.EnabledOptions,
            SuperblockLayout.RecoverableFormatVersion))
        {
            var (top, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "lifecycle", 0));
            var tree = new SparsePromotionFixture(session, top.Offset, true);
            for (ulong i = 0; i < 225; i++) tree.Add(0, i, i + 1);
            tree.InterruptAfter(PendingSegmentPhase.Payload);
            try { tree.AddStaged(0, 225, 226, false, true); }
            catch (InvalidOperationException ex) when (ex.Message == "Injected component promotion phase interruption.") { interrupted = true; }
        }
        RequireComponent(
            interrupted && ReadFixtureRecoveryState(source).State == PublicationRecoveryState.Active,
            "Protected interruption fixture did not retain an active embedded recovery state.");
        foreach (string mode in new[] { "copy-main-only", "rename-main-only" })
        {
            string target = Path.Combine(folder, mode + ".lbdx"); File.Copy(source, target);
            if (mode == "rename-main-only")
            {
                string renamed = Path.Combine(folder, "renamed.lbdx"); File.Move(target, renamed); target = renamed;
            }
            int actual = InspectPublicationLifecycle(target);
            RequireComponent(actual == 226, "An in-file recovery image did not survive the closed-file lifecycle operation.");
            results.Add(new { mode, actualRecords = actual, completeRecovery = true });
            Console.WriteLine($"PASS {mode}: records={actual}"); File.Delete(target);
        }
        int guardChecks = CheckPublicationMaintenanceGuards(folder);
        results.Add(new { maintenanceGuardsPassed = guardChecks });
        int formatChecks = CheckPublicationFormatContracts(folder);
        results.Add(new { formatCompatibilityChecksPassed = formatChecks });
        File.WriteAllText(Path.Combine(folder, "report.json"), JsonSerializer.Serialize(new { probePassed = true, productionLifecycleGatePassed = true, results }));
        if (!args.Contains("--keep"))
        {
            RequireComponent(InspectPublicationLifecycle(source) == 226, "Source recovery failed."); File.Delete(source);
        }
        Console.WriteLine("RESULT publication-lifecycle " + folder);
        return 0;
    }

    /// <summary>Opens one known diagnostic component tree and counts its exact persisted owner rows.<br/>
    /// Uses only pre-recovery APIs so a preserved older library can independently demonstrate its admission behavior.<br/></summary>
    private static int InspectPublicationLifecycle(string path)
    {
        using var session = LibraDexFileSession.Open(path, CreateDesignPerfOptions(), DataKernelTelemetryOptions.EnabledOptions);
        var tree = new SparsePromotionFixture(session, session.IndexDirectory.ActiveSlots[0].RootRouterOffset, true);
        var rows = tree.Scan();
        RequireComponent(rows.All(row => row.A == 0 && row.C < 226 && row.Id == row.C + 1) && rows.Distinct().Count() == rows.Count, "Lifecycle row oracle failed.");
        return rows.Count;
    }

    /// <summary>Checks backup/rollback path collisions before and during maintenance installation.<br/>
    /// Both prepare and ready sidecars are preserved byte-for-byte; existing destination bytes may not be overwritten.<br/></summary>
    private static int CheckPublicationMaintenanceGuards(string folder)
    {
        string source = Path.Combine(folder, "backup-source.lbdx"); int checks = 0;
        foreach (string suffix in new[] { DataKernel.PublicationPrepareSuffix, DataKernel.PublicationReadySuffix })
        foreach (bool late in new[] { false, true })
        foreach (bool existing in new[] { false, true })
        {
            string destination = Path.Combine(folder, "backup-destination.lbdx"), sidecar = destination + suffix;
            byte[] sentinel = { 1, 3, 5, 7 }; if (existing) File.WriteAllBytes(destination, sentinel);
            if (!late) File.WriteAllBytes(sidecar, sentinel);
            using (var catalog = File.Exists(source) ? Catalog.Open(source) : Catalog.Create(source))
            {
                LibraDexFileSession.LiveBackupCopyEnteredForValidation = late ? () => File.WriteAllBytes(sidecar, sentinel) : null;
                bool rejected = false;
                try { catalog.Backup(destination, new LibraDexBackupOptions { Overwrite = true }); }
                catch (IOException ex) when (ex.Message.StartsWith("Publication recovery files exist at destination", StringComparison.Ordinal)) { rejected = true; }
                finally { LibraDexFileSession.LiveBackupCopyEnteredForValidation = null; }
                RequireComponent(rejected && File.ReadAllBytes(sidecar).AsSpan().SequenceEqual(sentinel), "Backup collision was not preserved/rejected.");
                RequireComponent(existing ? File.ReadAllBytes(destination).AsSpan().SequenceEqual(sentinel) : !File.Exists(destination), "Backup changed a protected destination.");
                RequireComponent(!Directory.EnumerateFiles(folder, ".*.backup-*.tmp").Any(), "Backup staging file leaked."); checks++;
            }
            File.Delete(sidecar); if (existing) File.Delete(destination);
        }
        foreach (string suffix in new[] { DataKernel.PublicationPrepareSuffix, DataKernel.PublicationReadySuffix })
        {
            string rollback = Path.Combine(folder, "rollback.lbdx"), sidecar = rollback + suffix;
            byte[] before = File.ReadAllBytes(source), sentinel = { 2, 4, 6, 8 }; File.WriteAllBytes(sidecar, sentinel);
            bool rejected = false;
            try { Catalog.Compact(source, new LibraDexCompactionOptions { BackupPath = rollback }); }
            catch (IOException ex) when (ex.Message.StartsWith("Publication recovery files exist at destination", StringComparison.Ordinal)) { rejected = true; }
            RequireComponent(rejected && !File.Exists(rollback) && File.ReadAllBytes(source).AsSpan().SequenceEqual(before) && File.ReadAllBytes(sidecar).AsSpan().SequenceEqual(sentinel), "Compaction collision was not preserved/rejected.");
            File.Delete(sidecar); checks++;
        }
        File.Delete(source); Console.WriteLine($"PASS maintenance recovery-sidecar guards checks={checks}"); return checks;
    }

    /// <summary>
    /// Verifies the explicit file-format contract through the public catalog surface.<br/>
    /// Default creation remains version one, opening with the v2 option never upgrades it, direct opt-in creation writes version two, and explicit compaction can migrate version one to version two.<br/>
    /// </summary>
    /// <param name="folder">The isolated lifecycle artifact folder.<br/></param>
    /// <returns>The number of completed format-contract checks.<br/></returns>
    private static int CheckPublicationFormatContracts(string folder)
    {
        string versionOnePath = Path.Combine(folder, "format-v1.lbdx");
        string versionTwoPath = Path.Combine(folder, "format-v2.lbdx");
        CatalogOptions recoverableOptions = new() { UseRecoverableFileFormat = true };
        int checks = 0;
        try
        {
            using (Catalog created = Catalog.Create(versionOnePath))
            {
            }
            RequireComponent(ReadCatalogFormatVersion(versionOnePath) == SuperblockLayout.LegacyFormatVersion, "Default catalog creation no longer writes version one.");
            checks++;

            using (Catalog reopened = Catalog.Open(versionOnePath, recoverableOptions))
            {
            }
            RequireComponent(ReadCatalogFormatVersion(versionOnePath) == SuperblockLayout.LegacyFormatVersion, "Opening a version-one catalog silently upgraded its format.");
            checks++;

            using (Catalog created = Catalog.Create(versionTwoPath, recoverableOptions))
            {
            }
            RequireComponent(ReadCatalogFormatVersion(versionTwoPath) == SuperblockLayout.RecoverableFormatVersion, "Opt-in catalog creation did not write version two.");
            checks++;

            using (Catalog reopened = Catalog.Open(versionTwoPath))
            {
            }
            RequireComponent(ReadCatalogFormatVersion(versionTwoPath) == SuperblockLayout.RecoverableFormatVersion, "The current reader did not preserve version two on reopen.");
            checks++;

            byte[] conflictSentinel = { 9, 7, 5, 3 };
            string conflictingSidecar = versionTwoPath + DataKernel.PublicationReadySuffix;
            File.WriteAllBytes(conflictingSidecar, conflictSentinel);
            bool conflictRejected = false;
            try
            {
                using Catalog ignored = Catalog.Open(versionTwoPath);
            }
            catch (InvalidDataException ex) when (ex.Message.Contains("both embedded recovery state and a prototype sidecar", StringComparison.Ordinal))
            {
                conflictRejected = true;
            }
            RequireComponent(
                conflictRejected && File.ReadAllBytes(conflictingSidecar).AsSpan().SequenceEqual(conflictSentinel),
                "A mixed embedded/sidecar recovery authority was not rejected and preserved.");
            File.Delete(conflictingSidecar);
            checks++;

            Catalog.Compact(versionOnePath, new LibraDexCompactionOptions { CatalogOptions = recoverableOptions });
            RequireComponent(ReadCatalogFormatVersion(versionOnePath) == SuperblockLayout.RecoverableFormatVersion, "Explicit compaction did not migrate version one to version two.");
            checks++;
            Console.WriteLine($"PASS publication format compatibility checks={checks}");
            return checks;
        }
        finally
        {
            if (File.Exists(versionOnePath))
                File.Delete(versionOnePath);
            if (File.Exists(versionTwoPath))
                File.Delete(versionTwoPath);
        }
    }

    /// <summary>
    /// Reads only the fixed superblock format field for a public creation or migration assertion.<br/>
    /// </summary>
    /// <param name="path">The catalog path to inspect.<br/></param>
    /// <returns>The persisted superblock format version.<br/></returns>
    private static ushort ReadCatalogFormatVersion(string path)
    {
        Span<byte> prefix = stackalloc byte[SuperblockLayout.FormatVersionOffset + sizeof(ushort)];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.ReadExactly(prefix);
        return BinaryPrimitives.ReadUInt16LittleEndian(prefix.Slice(SuperblockLayout.FormatVersionOffset));
    }
}
