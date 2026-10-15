using LibraDex;

var catalogDirectory = Path.Combine(AppContext.BaseDirectory, "package-smoke", Guid.NewGuid().ToString("N"));

CreateAndWrite();
ReopenAndRead();
ValidateLifetimeContracts();
ValidateIndexLifecycle();
ValidateCountBoundaries();
ValidateCompositeMutationReaders();
Directory.Delete(catalogDirectory, recursive: true);

Console.WriteLine("LibraDex package smoke test passed.");

void CreateAndWrite()
{
    using Catalog catalog = Catalog.CreateOrOpen("products", directory: catalogDirectory);
    if (catalog.Name != "products" || catalog.DirectoryPath != Path.GetFullPath(catalogDirectory) ||
        catalog.Path != Catalog.GetFilePath("products", catalogDirectory))
        throw new InvalidOperationException("Named catalog metadata did not match its resolved path.");
    Expect<KeyNotFoundException>(() => { _ = catalog.Indexes["products"]; });
    using LibraDexIndex<long, long> sku = catalog.Indexes.CreateOrOpen<long, long>(
        "sku", indexSet: "products", keys: IndexKeys.NonUnique);

    sku.Insert(100_042, 501);
    sku.Insert(100_042, 502);
}

void ReopenAndRead()
{
    using Catalog catalog = Catalog.Open("products", directory: catalogDirectory);
    using LibraDexIndex<long, long> sku = catalog.Indexes["products"].Open<long, long>("sku");
    using LibraDexRangeReader<long, long> reader = sku.OpenReader();

    if (!reader.MoveNext() || reader.CurrentKey != 100_042 || reader.CurrentIdentity != 501)
        throw new InvalidOperationException("The package consumer could not retrieve the expected first index entry.");

    if (!reader.MoveNext() || reader.CurrentKey != 100_042 || reader.CurrentIdentity != 502)
        throw new InvalidOperationException("The package consumer could not retrieve the expected second index entry.");

    if (reader.MoveNext())
        throw new InvalidOperationException("The reader continued past the expected entries.");
}

/// <summary>
/// Exercises named lifecycle guards, compatibility adapters, compaction, and exact-file reopening.<br/>
/// All generated files stay in this isolated smoke run and are deleted after successful validation.<br/>
/// </summary>
void ValidateLifetimeContracts()
{
    Expect<IOException>(() => { using var duplicate = Catalog.Create("products", directory: catalogDirectory); });
    Expect<FileNotFoundException>(() => { using var missing = Catalog.Open("missing", directory: catalogDirectory); });
    foreach (string invalid in new[] { "", " ", "../products", "..\\products", ".", "..", ".lbdx" })
        Expect<ArgumentException>(() => Catalog.GetFilePath(invalid, catalogDirectory));
    if (File.Exists(Catalog.GetFilePath("missing", catalogDirectory)))
        throw new InvalidOperationException("Open created a missing catalog.");
    if (Catalog.GetFilePath("products.lbdx", catalogDirectory) != Catalog.GetFilePath("products", catalogDirectory))
        throw new InvalidOperationException("Catalog extension normalization changed the resolved file.");
    using (var exact = Catalog.Open(Catalog.GetFilePath("products", catalogDirectory), options: null))
    {
        if (exact.Location is not { IsNamed: false }) throw new InvalidOperationException("Exact-path compatibility metadata changed.");
    }
    using (var compatible = Catalog.CreateOrOpen(CatalogLocation.Named("products", catalogDirectory)))
    {
        if (compatible.Location is not { IsNamed: true }) throw new InvalidOperationException("Location compatibility metadata changed.");
    }
    using (var named = Catalog.CreateOrOpen("products", directory: catalogDirectory))
    {
        if (named.Location is not { IsNamed: true }) throw new InvalidOperationException("Named compatibility metadata changed.");
    }
    using (var created = Catalog.Create("created", directory: catalogDirectory)) { }
    using (var reopened = Catalog.CreateOrOpen("created", directory: catalogDirectory)) { }
    using (var repeated = Catalog.Create("suffix.lbdx.lbdx", directory: catalogDirectory))
    {
        if (repeated.Location?.FilePath != repeated.Path)
            throw new InvalidOperationException("Compatibility metadata normalized an already-resolved filename twice.");
    }
    using (var memory = Catalog.CreateMemory())
    {
        if (memory.Name is not null || memory.DirectoryPath is not null || memory.Location is not null)
            throw new InvalidOperationException("Memory catalog acquired file metadata.");
    }
    Catalog.Compact("products", directory: catalogDirectory);
    ReopenAndRead();
}

/// <summary>
/// Requires a specified lifetime/argument failure rather than permitting a silent operation.<br/>
/// </summary>
/// <typeparam name="T">The expected exception type.<br/></typeparam>
/// <param name="operation">The isolated operation under test.<br/></param>
void Expect<T>(Action operation) where T : Exception
{
    try { operation(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name} was not thrown.");
}

/// <summary>
/// Verifies explicit typed lifecycle methods and existing-only indexers across independent index sets.<br/>
/// Missing lookups must leave catalog metadata unchanged; existing handles must expose persisted names and types.<br/>
/// The memory catalog isolates this fixture without creating additional files.<br/>
/// </summary>
void ValidateIndexLifecycle()
{
    using var catalog = Catalog.CreateMemory();
    Expect<KeyNotFoundException>(() => { _ = catalog.Indexes["products"]; });
    Expect<KeyNotFoundException>(() => { _ = catalog["products"]; });
    if (catalog.Indexes.List().Length != 0)
        throw new InvalidOperationException("A missing-set lookup changed catalog metadata.");

    using var sku = catalog.Indexes.Create<long, long>("sku", indexSet: "products");
    sku.Insert(42, 501);
    using var customer = catalog.Indexes.CreateOrOpen<long, long>("number", indexSet: "customers");
    customer.Insert(42, 902);
    using var price = catalog.Indexes["products"].CreateOrOpen<long, long>("price");
    price.Insert(12, 501);

    IIndex existing = catalog.Indexes["products"]["sku"];
    if (existing.Name != "sku" || existing.Group != "products" || existing.KeyType != typeof(long))
        throw new InvalidOperationException("Indexer did not retrieve the expected existing index.");
    Expect<KeyNotFoundException>(() => { _ = catalog.Indexes["products"]["missing"]; });
    Expect<InvalidOperationException>(() => { using var duplicate = catalog.Indexes.Create<long, long>("sku", indexSet: "products"); });
    Expect<InvalidDataException>(() => { using var missing = catalog.Indexes.Open<long, long>("missing", indexSet: "products"); });
    Expect<InvalidDataException>(() => { using var wrong = catalog.Indexes["products"].Open<int, long>("sku"); });

    using var reopened = catalog.Indexes.Open<long, long>("sku", indexSet: "products");
    using var same = catalog.Indexes["products"].CreateOrOpen<long, long>("sku");
    using var reader = reopened.OpenReader();
    if (!reader.MoveNext() || reader.CurrentKey != 42 || reader.CurrentIdentity != 501 || reader.MoveNext())
        throw new InvalidOperationException("Existing index lifecycle lost or duplicated entries.");
    if (catalog.Indexes.IndexSetNames().Length != 2 || catalog.Indexes.List().Length != 3)
        throw new InvalidOperationException("Missing-name operations created storage or index sets leaked into one another.");
}

/// <summary>
/// Exercises exact counts and actual identity retrieval over a larger shared-prefix population through public packaged APIs.<br/>
/// Missing prefixes and post-delete misses must stay zero without weakening existing-key counts.<br/>
/// </summary>
void ValidateCountBoundaries()
{
    using var catalog = Catalog.CreateMemory();
    var group = catalog.Indexes.IndexSet("counts");
    using var index = group.Define("text").String.Create(StringKeys.Exact);
    for (int i = 0; i < 9239; i++) index.Insert("shared/" + new string('p', i % 31) + "/" + i.ToString("D8"), (ulong)i);
    foreach (string missing in new[] { "never-existed", "reopened-insert", "zzzz", "shared/missing" })
    {
        var condition = group.Where(index).EqualTo(missing).EndCondition;
        if (condition.Count(_ => index) != 0 || group.GetIdentities<ulong>(condition).Count != 0)
            throw new InvalidDataException("Missing shared-prefix text key did not count as zero: " + missing);
    }
    var existing = group.Where(index).EqualTo("shared/" + new string('p', 7) + "/00000007").EndCondition;
    if (existing.Count(_ => index) != 1 || !group.GetIdentities<ulong>(existing).SequenceEqual(new[] { 7UL }))
        throw new InvalidDataException("Existing text key count/retrieval changed.");
    index.Delete("shared/" + new string('p', 7) + "/00000007", 7UL);
    if (existing.Count(_ => index) != 0 || group.GetIdentities<ulong>(existing).Count != 0)
        throw new InvalidDataException("Deleted text key did not count as zero.");
}

/// <summary>
/// Verifies public packaged native readers remain available after exact delete, overwrite-style insertion, rekey, replacement and clear.<br/>
/// Reads the same surviving tuples again after reopening, and distinguishes a valid empty root from a missing node-page root.<br/>
/// </summary>
void ValidateCompositeMutationReaders()
{
    string path = Path.Combine(catalogDirectory, "composite.lbdx");
    using (Catalog catalog = Catalog.Create(path, CatalogOptions.UInt64Identities))
    {
        var group = catalog.Indexes.IndexSet("composite");
        var index = (LibraDexRoutedCompositeIndex)group.Define("pair").Composite<ulong>(C.Int32("group"), C.Int32("value")).Create();
        index.Insert(Key.Of(1, 5), 10UL);
        index.Insert(Key.Of(1, 5), 11UL);
        index.Insert(Key.Of(2, 20), 20UL);
        if (!index.Delete(Key.Of(1, 5), 10UL)) throw new InvalidDataException("Composite exact delete failed.");
        index.Insert(Key.Of(1, 105), 10UL);
        if (!index.Rekey(20UL, Key.Of(2, 20), Key.Of(2, 120))) throw new InvalidDataException("Composite rekey failed.");
        CheckCompositePackageRows(group);
    }
    using (Catalog catalog = Catalog.Open(path, CatalogOptions.UInt64Identities))
    {
        var group = catalog.Indexes["composite"];
        CheckCompositePackageRows(group);
        var index = (LibraDexRoutedCompositeIndex)group["pair"];
        if (index.ReplaceFromUnordered(new[] { (Key.Of(1, 5), (object)11UL), (Key.Of(1, 105), (object)10UL), (Key.Of(2, 120), (object)20UL) }) != 3)
            throw new InvalidDataException("Composite replacement count changed.");
        CheckCompositePackageRows(group);
        if (index.DeleteAll() != 3) throw new InvalidDataException("Composite clear count changed.");
        using var empty = group.OpenCompositeReader<int, int, ulong>("pair");
        if (empty.Read()) throw new InvalidDataException("Cleared composite reader returned a row.");
    }
    using (Catalog catalog = Catalog.Open(path, CatalogOptions.UInt64Identities))
    using (var empty = catalog.Indexes["composite"].OpenCompositeReader<int, int, ulong>("pair"))
    {
        if (empty.Read()) throw new InvalidDataException("Reopened cleared composite reader returned a row.");
    }
}

/// <summary>
/// Checks complete identity and component values through the public typed page-native reader.<br/>
/// </summary>
/// <param name="group">The catalog index set containing the mutated composite index.<br/></param>
void CheckCompositePackageRows(CatalogIdentityGroupIndexes group)
{
    var expected = new Dictionary<ulong, (int, int)> { [10] = (1, 105), [11] = (1, 5), [20] = (2, 120) };
    using var reader = group.OpenCompositeReader<int, int, ulong>("pair");
    while (reader.Read())
    {
        if (!expected.Remove(reader.Identity, out var value) || reader.Part1 != value.Item1 || reader.Part2 != value.Item2)
            throw new InvalidDataException("Composite native package values changed.");
    }
    if (expected.Count != 0) throw new InvalidDataException("Composite native package reader omitted rows.");
}
