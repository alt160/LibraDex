using LibraDex;

var catalogDirectory = Path.Combine(AppContext.BaseDirectory, "package-smoke", Guid.NewGuid().ToString("N"));

CreateAndWrite();
ReopenAndRead();
ValidateLifetimeContracts();
Directory.Delete(catalogDirectory, recursive: true);

Console.WriteLine("LibraDex package smoke test passed.");

void CreateAndWrite()
{
    using Catalog catalog = Catalog.CreateOrOpen("products", directory: catalogDirectory);
    if (catalog.Name != "products" || catalog.DirectoryPath != Path.GetFullPath(catalogDirectory) ||
        catalog.Path != Catalog.GetFilePath("products", catalogDirectory))
        throw new InvalidOperationException("Named catalog metadata did not match its resolved path.");
    using LibraDexIndex<long, long> sku = catalog.Indexes["products"]["sku"]
        .Int64Keys<long>()
        .CreateOrOpen(keys: IndexKeys.NonUnique);

    sku.Insert(100_042, 501);
    sku.Insert(100_042, 502);
}

void ReopenAndRead()
{
    using Catalog catalog = Catalog.Open("products", directory: catalogDirectory);
    using LibraDexIndex<long, long> sku = catalog.Indexes["products"]["sku"]
        .Int64Keys<long>()
        .Open();
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
    using (var exact = Catalog.Open(Catalog.GetFilePath("products", catalogDirectory), null))
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
