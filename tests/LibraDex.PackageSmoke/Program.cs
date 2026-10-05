using LibraDex;

var catalogDirectory = Path.Combine(AppContext.BaseDirectory, "package-smoke", Guid.NewGuid().ToString("N"));
var location = CatalogLocation.Named("products", catalogDirectory);

CreateAndWrite();
ReopenAndRead();
Directory.Delete(catalogDirectory, recursive: true);

Console.WriteLine("LibraDex package smoke test passed.");

void CreateAndWrite()
{
    using Catalog catalog = Catalog.CreateOrOpen(location);
    using LibraDexIndex<long, long> sku = catalog.Indexes["products"]["sku"]
        .Int64Keys<long>()
        .CreateOrOpen(keys: IndexKeys.NonUnique);

    sku.Insert(100_042, 501);
    sku.Insert(100_042, 502);
}

void ReopenAndRead()
{
    using Catalog catalog = Catalog.Open(location);
    using LibraDexIndex<long, long> sku = catalog.Indexes["products"]["sku"]
        .Int64Keys<long>()
        .Open();
    using LibraDexRangeReader<long, long> reader = sku.OpenReader();

    if (!reader.MoveNext() || reader.CurrentKey != 100_042 || reader.CurrentIdentity != 501)
        throw new InvalidOperationException("The package consumer could not retrieve the expected first index entry.");

    if (!reader.MoveNext() || reader.CurrentKey != 100_042 || reader.CurrentIdentity != 502)
        throw new InvalidOperationException("The package consumer could not retrieve the expected second index entry.");
}
