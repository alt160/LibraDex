using LibraDex;
using System.Collections;
using System.Reflection;

internal static partial class RawHarness
{
    /// <summary>
    /// Exercises composite mutations against complete native-page and logical-entry oracles at minimum, ordinary, wide, and maximum depth.<br/>
    /// File-backed cases reopen after each mutation; memory catalogs exercise the identical publisher without a file boundary.<br/>
    /// The baseline switch accepts only the known missing-node-root exception immediately after an otherwise successful exact deletion.<br/>
    /// </summary>
    /// <param name="args">Options including the disposable T-drive root and optional baseline characterization switch.<br/></param>
    /// <returns>Zero only when every requested mutation and reader invariant passes.<br/></returns>
    private static int RunCompositeMutationNativeSanity(string[] args)
    {
        string root = GetOption(args, "--root", @"T:\AbraxasCompositeMutationRepair-20261015");
        Directory.CreateDirectory(root);
        bool expectBug = args.Contains("--expect-snapshot-bug");
        int cases = 0;
        foreach (bool memory in new[] { false, true })
        foreach (int depth in new[] { 1, 2, 8, 16 })
        {
            string path = Path.Combine(root, $"mutation-{depth}-{memory}-{Guid.NewGuid():N}.lbdx");
            Catalog catalog = memory ? Catalog.CreateMemory(CatalogOptions.UInt64Identities) : Catalog.Create(path, CatalogOptions.UInt64Identities);
            try
            {
                var parts = new LibraDexCompositeKeyPartSpec[depth];
                for (int part = 0; part < depth; part++)
                    parts[part] = C.Int32($"p{part}");
                var index = (LibraDexRoutedCompositeIndex)catalog.Indexes.IndexSet("mutation").Define("keys").Composite<ulong>(parts).Create();
                var expected = new Dictionary<ulong, int>();
                for (int value = 0; value < 40; value++)
                {
                    ulong id = (ulong)value + 1;
                    ValidateGenericInsert(index.Insert(CompositeMutationKey(value, depth), id), "mutation seed");
                    expected.Add(id, value);
                }
                ValidateGenericInsert(index.Insert(CompositeMutationKey(5, depth), 1_000UL), "duplicate key with different identity");
                expected.Add(1_000UL, 5);
                CheckCompositeMutationRows(index, expected, depth);
                ReopenCompositeMutationCatalog(ref catalog, ref index, path, memory);
                CheckCompositeMutationRows(index, expected, depth);
                Dictionary<object, long> originalPages = CaptureCompositeMutationPages(index);
                if (!index.Delete(CompositeMutationKey(5, depth), 6UL))
                    throw new InvalidDataException("Exact tuple deletion did not remove its identity.");
                expected.Remove(6UL);
                if (expectBug)
                {
                    try
                    {
                        using var cursor = index.OpenNativeEntryCursor();
                        throw new InvalidDataException("Original code did not reproduce the snapshot downgrade.");
                    }
                    catch (InvalidOperationException error) when (error.Message.Contains("has no durable node-page root", StringComparison.Ordinal))
                    {
                        if (((IIdentityPrimitiveExecutor)index).ExecuteAllIdentities().Count != expected.Count)
                            throw new InvalidDataException("Baseline logical population changed unexpectedly.");
                        Console.WriteLine($"EXPECTED SNAPSHOT BUG depth={depth} memory={memory} logicalRows={expected.Count}");
                    }
                    cases++;
                    continue;
                }
                CheckCompositeMutationRows(index, expected, depth);
                CheckCompositeMutationPageReuse(index, originalPages, depth + 1);
                ReopenCompositeMutationCatalog(ref catalog, ref index, path, memory);
                CheckCompositeMutationRows(index, expected, depth);
                if (index.Delete(CompositeMutationKey(5, depth), 6UL) || index.Delete(CompositeMutationKey(500, depth), 999UL))
                    throw new InvalidDataException("Missing tuple deletion unexpectedly succeeded.");
                if (!index.Rekey(1_000UL, CompositeMutationKey(5, depth), CompositeMutationKey(105, depth)))
                    throw new InvalidDataException("Rekey failed.");
                expected[1_000UL] = 105;
                CheckCompositeMutationRows(index, expected, depth);
                ReopenCompositeMutationCatalog(ref catalog, ref index, path, memory);
                CheckCompositeMutationRows(index, expected, depth);

                ValidateGenericInsert(index.Insert(CompositeMutationKey(10, depth), 1_001UL), "exact-key duplicate seed");
                expected.Add(1_001UL, 10);
                LibraDexIdentityMutationResult exact = ((IIdentityPrimitiveMutator)index).DeleteIdentityPrimitive(
                    new(LibraDexCriteriaKind.Find, new object?[] { CompositeMutationKey(10, depth) }));
                if (exact.ChangedCount != 2)
                    throw new InvalidDataException("Exact-key deletion count mismatch.");
                expected.Remove(11UL);
                expected.Remove(1_001UL);
                CheckCompositeMutationRows(index, expected, depth);
                Dictionary<object, long> predicatePages = CaptureCompositeMutationPages(index);
                var predicate = new LibraDexCompositePredicate(new[] { LibraDexCompositePart.Scalar<int>("p0").Between(20, 25) });
                LibraDexIdentityMutationResult broad = ((IIdentityPrimitiveMutator)index).DeleteIdentityPrimitive(
                    new(LibraDexCriteriaKind.CompositeMatch, new object?[] { predicate }));
                if (broad.ChangedCount != 6)
                    throw new InvalidDataException("Predicate deletion count mismatch.");
                foreach (ulong id in expected.Where(row => row.Value >= 20 && row.Value <= 25).Select(row => row.Key).ToArray())
                    expected.Remove(id);
                CheckCompositeMutationRows(index, expected, depth);
                CheckCompositeMutationPageReuse(index, predicatePages, 1 + 6 * depth);
                ReopenCompositeMutationCatalog(ref catalog, ref index, path, memory);
                CheckCompositeMutationRows(index, expected, depth);

                using (var cancelled = new CancellationTokenSource())
                {
                    cancelled.Cancel();
                    try
                    {
                        index.ReplaceFromUnordered(new[] { (CompositeMutationKey(999, depth), (object)999UL) }, cancelled.Token);
                        throw new InvalidDataException("Cancelled replacement unexpectedly succeeded.");
                    }
                    catch (OperationCanceledException) { }
                }
                try
                {
                    index.ReplaceFromUnordered(new[] { (CompositeMutationKey(999, depth), (object)999UL), (Key.Of("invalid"), (object)998UL) });
                    throw new InvalidDataException("Invalid replacement unexpectedly succeeded.");
                }
                catch (ArgumentException) { }
                using (var midStream = new CancellationTokenSource())
                {
                    try
                    {
                        index.ReplaceFromUnordered(CompositeMutationInterruptedEntries(depth, midStream, fail: false), midStream.Token);
                        throw new InvalidDataException("Mid-stream cancellation unexpectedly succeeded.");
                    }
                    catch (OperationCanceledException) { }
                    try
                    {
                        index.ReplaceFromUnordered(CompositeMutationInterruptedEntries(depth, midStream, fail: true));
                        throw new InvalidDataException("Failed source enumeration unexpectedly succeeded.");
                    }
                    catch (IOException error) when (error.Message == "injected composite enumeration failure") { }
                }
                CheckCompositeMutationRows(index, expected, depth);
                ReopenCompositeMutationCatalog(ref catalog, ref index, path, memory);
                CheckCompositeMutationRows(index, expected, depth);

                var replacement = new List<(LibraDexCompositeKey, object)>();
                expected.Clear();
                for (int value = 59; value >= 50; value--)
                {
                    replacement.Add((CompositeMutationKey(value, depth), (ulong)value + 1));
                    expected.Add((ulong)value + 1, value);
                }
                replacement.Add(replacement[0]);
                if (index.ReplaceFromUnordered(replacement) != expected.Count)
                    throw new InvalidDataException("Replacement duplicate/count mismatch.");
                CheckCompositeMutationRows(index, expected, depth);
                ReopenCompositeMutationCatalog(ref catalog, ref index, path, memory);
                CheckCompositeMutationRows(index, expected, depth);
                if (index.DeleteAll() != expected.Count || index.DeleteAll() != 0)
                    throw new InvalidDataException("DeleteAll count mismatch.");
                expected.Clear();
                CheckCompositeMutationRows(index, expected, depth);
                ReopenCompositeMutationCatalog(ref catalog, ref index, path, memory);
                CheckCompositeMutationRows(index, expected, depth);
                ValidateGenericInsert(index.Insert(CompositeMutationKey(70, depth), 71UL), "insert after clear");
                expected.Add(71UL, 70);
                CheckCompositeMutationRows(index, expected, depth);
                if (index.InsertBatch(new[] { CompositeMutationKey(80, depth), CompositeMutationKey(81, depth) }, new[] { 81UL, 82UL }) != 2)
                    throw new InvalidDataException("Bulk insertion after clear failed.");
                expected.Add(81UL, 80);
                expected.Add(82UL, 81);
                CheckCompositeMutationRows(index, expected, depth);
                if (index.ReplaceFromUnordered(Array.Empty<(LibraDexCompositeKey, object)>()) != 0)
                    throw new InvalidDataException("Empty replacement count mismatch.");
                expected.Clear();
                CheckCompositeMutationRows(index, expected, depth);
                ReopenCompositeMutationCatalog(ref catalog, ref index, path, memory);
                CheckCompositeMutationRows(index, expected, depth);
                cases++;
                Console.WriteLine($"COMPOSITE MUTATION PASS depth={depth} memory={memory}");
            }
            finally
            {
                catalog.Dispose();
                if (File.Exists(path))
                    File.Delete(path);
            }
        }
        Console.WriteLine($"composite-mutation-native-sanity cases={cases} expectedBug={expectBug} ok");
        return 0;
    }

    /// <summary>
    /// Produces a deterministic complete key with one shared leading branch and depth-specific remaining components.<br/>
    /// Setup allocations are outside all measured traversal paths.<br/>
    /// </summary>
    /// <param name="value">The seed value for the key.<br/></param>
    /// <param name="depth">The declared component count.<br/></param>
    /// <returns>A complete typed composite key.<br/></returns>
    private static LibraDexCompositeKey CompositeMutationKey(int value, int depth)
    {
        var values = new object?[depth];
        for (int part = 0; part < depth; part++)
            values[part] = part == 0 ? value : value + part * 100;
        return Key.Of(values);
    }

    /// <summary>
    /// Compares every native decoded component, identity, raw view, ordering step, and logical entry count with the independent mutation oracle.<br/>
    /// </summary>
    /// <param name="index">The mutated index to read without any fallback.<br/></param>
    /// <param name="expected">Expected identity-to-key seed mappings.<br/></param>
    /// <param name="depth">The declared component count.<br/></param>
    private static void CheckCompositeMutationRows(LibraDexRoutedCompositeIndex index, Dictionary<ulong, int> expected, int depth)
    {
        var seen = new HashSet<ulong>();
        int previous = int.MinValue;
        using var reader = index.OpenNativeEntryCursor();
        while (reader.Read())
        {
            ulong id = reader.ReadIdentity<ulong>();
            if (!expected.TryGetValue(id, out int value) || !seen.Add(id) || value < previous)
                throw new InvalidDataException("Native mutation identity/order mismatch.");
            for (int part = 0; part < depth; part++)
            {
                if (reader.IsNull(part) || reader.ReadPart<int>(part) != (part == 0 ? value : value + part * 100) ||
                    !reader.GetRawSpan(part).SequenceEqual(reader.GetRawMem(part).Span))
                    throw new InvalidDataException("Native mutation component/raw-view mismatch.");
            }
            previous = value;
        }
        if (seen.Count != expected.Count || ((IIdentityPrimitiveExecutor)index).ExecuteAllIdentities().Count != expected.Count)
            throw new InvalidDataException("Native/logical mutation population mismatch.");
    }

    /// <summary>
    /// Reopens a file-backed catalog and its canonical composite handle; memory cases deliberately retain the current page owner.<br/>
    /// </summary>
    /// <param name="catalog">The catalog owner replaced only for file cases.<br/></param>
    /// <param name="index">The canonical index handle refreshed after reopening.<br/></param>
    /// <param name="path">The disposable catalog file path.<br/></param>
    /// <param name="memory">Whether there is no file reopen boundary.<br/></param>
    private static void ReopenCompositeMutationCatalog(ref Catalog catalog, ref LibraDexRoutedCompositeIndex index, string path, bool memory)
    {
        if (memory)
            return;
        catalog.Dispose();
        catalog = Catalog.Open(path, CatalogOptions.UInt64Identities);
        CatalogIndexInfo info = catalog.Indexes.IndexSet("mutation").List().Single(row => row.Name == "keys");
        index = (LibraDexRoutedCompositeIndex)catalog.Indexes.Open(info.CreateShape());
    }

    /// <summary>
    /// Captures private node offsets only in this excluded harness, after logical traversal has loaded the complete tree.<br/>
    /// The production API and node layout are unchanged; the oracle detects unintended sibling-page rewrites.<br/>
    /// </summary>
    /// <param name="index">The index whose current page identities are recorded.<br/></param>
    /// <returns>A reference-identity map of routed nodes to durable offsets.<br/></returns>
    private static Dictionary<object, long> CaptureCompositeMutationPages(LibraDexRoutedCompositeIndex index)
    {
        object root = typeof(LibraDexRoutedCompositeIndex).GetField("root", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(index)!;
        var result = new Dictionary<object, long>();
        var pending = new Stack<object>();
        pending.Push(root);
        while (pending.TryPop(out object? node))
        {
            Type type = node.GetType();
            result.Add(node, (long)type.GetProperty("DurableOffset", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(node)!);
            foreach (object child in (IEnumerable)type.GetProperty("Children", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(node)!)
                pending.Push(child.GetType().GetProperty("Node")!.GetValue(child)!);
        }
        return result;
    }

    /// <summary>
    /// Proves exact changed-node counts while every untouched node retains its prior page offset.<br/>
    /// </summary>
    /// <param name="index">The index after a deletion that retains its routed nodes.<br/></param>
    /// <param name="before">The complete node-offset oracle captured before deletion.<br/></param>
    /// <param name="expectedChanges">The exact route-node population that should be republished.<br/></param>
    private static void CheckCompositeMutationPageReuse(LibraDexRoutedCompositeIndex index, Dictionary<object, long> before, int expectedChanges)
    {
        Dictionary<object, long> after = CaptureCompositeMutationPages(index);
        int changed = 0;
        foreach ((object node, long offset) in before)
        {
            if (!after.TryGetValue(node, out long current) || current <= 0)
                throw new InvalidDataException("Mutation lost a durable routed node.");
            if (current != offset)
                changed++;
        }
        if (before.Count != after.Count || changed != expectedChanges)
            throw new InvalidDataException($"Mutation republished {changed} nodes, expected {expectedChanges}; sibling-page reuse failed.");
    }

    /// <summary>
    /// Interrupts detached candidate construction after one valid tuple to prove old-root ownership is retained.<br/>
    /// </summary>
    /// <param name="depth">The complete candidate key depth.<br/></param>
    /// <param name="cancellation">Cancellation source activated after the first candidate tuple.<br/></param>
    /// <param name="fail">Whether source enumeration throws instead of cancelling.<br/></param>
    /// <returns>A deliberately interrupted replacement stream.<br/></returns>
    private static IEnumerable<(LibraDexCompositeKey, object)> CompositeMutationInterruptedEntries(int depth, CancellationTokenSource cancellation, bool fail)
    {
        yield return (CompositeMutationKey(999, depth), 999UL);
        if (fail)
            throw new IOException("injected composite enumeration failure");
        cancellation.Cancel();
    }
}
