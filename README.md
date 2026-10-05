# LibraDex

**LibraDex is a .NET-native identity-index catalog for data you already own.** It creates durable or memory-only indexes that link typed keys to typed identities, without requiring LibraDex to store, serialize, or hydrate the records behind those identities.

Think of it as a way to separate useful indexing capabilities from a table-and-row database model. Your application, files, blobs, service, cache, or existing collection remains the source of truth. LibraDex maintains the typed key-to-identity relationships that make that data fast to find, filter, count, and enumerate.

It can be used as a key/value store, but that is not its central purpose. LibraDex is built for lookups, filtering, analysis, and ordered traversal over identities.

## Why LibraDex?

Use LibraDex when your application repeatedly needs to answer questions such as:

- Which identities have this numeric, string, GUID, date/time, binary, or composite value?
- Which identities satisfy conditions across several indexes over the same identity universe?
- Can those indexes remain separate from the objects, files, blobs, or records to which the identities point?
- Can a query stream identities progressively instead of first creating an application-sized result collection?
- Can the index remain application-local and reopen from a file without deploying a database service?

LibraDex is deliberately an identity-indexing engine, not a general object store or relational database. It does not prescribe an object schema, own your payloads, or force your data into rows. That separation lets an application control record loading, caching, and lifetime while LibraDex focuses on the durable relationships used to locate records.

## Quick start

Install the package:

```shell
dotnet add package LibraDex
```

Create a durable catalog and a typed index. Here, `products` is the shared identity group, `sku` is one index in that group, the key is a `long`, and the record identity is also a `long`.

```csharp
using LibraDex;

using Catalog catalog = Catalog.CreateOrOpen("products", directory: @"C:\MyApp\Data");
// One catalog can contain multiple index sets, such as "products" and "customers".
// Each set groups indexes over its own shared identity universe.
using LibraDexIndex<long, long> sku = catalog.Indexes.CreateOrOpen<long, long>(
    "sku", indexSet: "products", keys: IndexKeys.NonUnique);

sku.Insert(100_042, 501);
sku.Insert(100_042, 502);
sku.Insert(100_043, 503);
```

Open an existing catalog and index explicitly when creation is not part of the operation:

```csharp
using Catalog catalog = Catalog.Open("products", directory: @"C:\MyApp\Data");
// Indexers access existing sets/indexes; they never create missing ones.
using LibraDexIndex<long, long> sku = catalog.Indexes["products"].Open<long, long>("sku");

using LibraDexRangeReader<long, long> reader = sku.OpenReader();
while (reader.MoveNext())
{
    long key = reader.CurrentKey;
    long productId = reader.CurrentIdentity;

    // Load or act on productId in the application's own record store.
}
```

For a process-local, non-durable catalog, use `Catalog.CreateMemory()`. It uses the same catalog and index API shape but has no reopen lifecycle.

Named calls take the catalog basename and directory; LibraDex resolves the `.lbdx` filename. Replace the example's `C:\MyApp\Data` with your application's storage directory (for example, `/var/lib/myapp/data` on Linux). `catalog.Name`, `catalog.DirectoryPath`, and `catalog.Path` expose the resulting metadata. Use `Catalog.GetFilePath("products", @"C:\MyApp\Data")` when you need the resolved path without opening the catalog. The original single-string file-path calls and `CatalogLocation` overloads remain supported for compatibility. To supply options, use the named `options:` argument; use `directory:` to select named-catalog behavior unambiguously.

The `directory` argument accepts absolute or relative paths. Relative paths resolve against the process's current working directory at the time of the call, not necessarily the executable's directory. Null or blank also uses the current working directory. Prefer an absolute path when storage must remain predictable regardless of how the application is launched.

UNC paths (such as `\\server\share\MyApp\Data`) and other network-backed directories are accepted where the underlying filesystem supports LibraDex's required file operations. Network latency, bandwidth, and remote filesystem behavior can make these substantially slower than local SSD storage, especially for frequent small reads and writes. Path acceptance is not a guarantee of compatibility or performance on every network filesystem.

## Index lifecycle syntax

Use `catalog.Indexes.Create<TKey, TIdentity>(name, indexSet: set)` to create a new index, `Open<TKey, TIdentity>` to open an existing one, and `CreateOrOpen<TKey, TIdentity>` for idempotent setup. Creating the first index establishes its index set; a single catalog can contain multiple sets.

Within an existing set, use `catalog.Indexes["products"].CreateOrOpen<long, long>("sku")`. The two indexers in `catalog.Indexes["products"]["sku"]` retrieve an existing set and an existing index handle; a missing name throws `KeyNotFoundException` without creating storage. Use typed `Open<TKey, TIdentity>(name)` when compile-time types and persisted-type validation are wanted.

For explicit widths or specialized key profiles, use `catalog.Indexes.IndexSet("products").Define("sku")` followed by the appropriate configuration and lifecycle method. `IndexSet` and `Define` explicitly configure intent; unlike indexers, they can name a set/index that does not exist yet.

**1.2.0 launch correction:** builder-style indexers from 1.0/1.1 have been replaced with existing-only lookup indexers. This is an intentional source-breaking early-release correction despite the minor version number. Replace construction chains with the lifecycle methods above. Catalog files do not need conversion.

## Good fits

### Secondary indexes for a large in-memory collection

A large `Dictionary<TId, TValue>` is excellent at finding one known identity, but applications often later need sorted enumeration, date ranges, name lookup, tag filtering, or compound lookup over properties of `TValue`. The usual result is a collection of hand-maintained dictionaries, sorted collections, and synchronization rules.

Use `Catalog.CreateMemory()` to keep those secondary indexes in LibraDex while the dictionary remains the source of truth. Each index maps a property value to the dictionary identity; conditions can then combine several indexed properties without repeatedly scanning the collection.

### Sidecar indexing for data outside your process

Index file metadata, document attributes, blobs, remote-service objects, or records owned by another system. The identity can be a path, numeric ID, GUID, or bounded/variable binary identifier. LibraDex stores the search relationship, not the object or payload itself.

This can also provide an application-owned local index over remote database or service data. Index remote fields that are not indexed there, cannot be indexed there, are expensive to query remotely, or need to be combined with metadata from another source. The remote system remains authoritative; LibraDex is the derived local query surface. Your application chooses the synchronization policy for inserts, updates, deletes, missed events, and reconciliation.

### Durable application-local lookup and filtering

Maintain several indexes over a shared identity universe: status, timestamp, normalized name, source, external ID, classification, or a composite business key. Reopen the catalog with the same durable index contract when the application starts again.

### Index-only range and count workloads

Repeatedly find, count, page, or stream identities in ordered ranges without object hydration or a large temporary result list. This is useful for time windows, numeric ranges, ordered identifiers, binary keys, and compound keys.

## Performance characteristics

LibraDex is designed for direct typed key-to-identity work. Its indexes do not need to hydrate application objects, and its readers can stream identities rather than first materializing a complete result collection. Its structures are matched to the declared key and identity contract instead of forcing every relationship through one generic record representation.

That design can matter substantially for index-only work. The checked-in ShapeBench comparisons use equivalent key/identity data and a SQLite covering B-tree, with no payload or table-row lookup. Three-process medians for selected one-thread `count-range-api` workloads measured LibraDex at:

- **4.245x** SQLite for VS8
- **2.889x** SQLite for VS16
- **5.945x** SQLite for VV

Those are specific, index-only workloads—not a claim that LibraDex universally outperforms SQLite. SQLite has won some write workloads. Performance depends on the storage shape, key distribution, mutation pattern, query pattern, data size, and hardware. Benchmark the shape and workload that matter to your application before making capacity or latency commitments.

## Capacity and scale

LibraDex is designed for indexes that grow beyond the comfortable range of ordinary in-memory collections. In ordinary use, indexes from hundreds of megabytes into the many-gigabyte range are a natural fit for its file-backed design. Growth adds routing and shelf work rather than changing lookup into a linear scan, so performance generally remains predictable as an index grows.

Variable key and identity families currently accept values from **1 through 1,024 raw bytes**. Fixed-N key families accept developer-chosen widths from **1 through 515 bytes**. These bounds are part of the persisted index contract.

File-backed storage uses 64-bit offsets, but that is an addressing representation rather than a practical capacity guarantee. Practical catalog size depends on the filesystem, available storage, selected index shapes, key and identity widths, key distribution, storage hardware, write locality, and maintenance activity. Measure with production-like data before setting a capacity or latency commitment.

One checked `SS8-8` scale checkpoint at **1,024,000 identities** held approximately **32.27 file bytes per identity** and measured a coalesced range read at **17.03 ns per identity** (**58.72 million identities per second**). That result illustrates stable density and efficient large-scale traversal for that shape and workload; it is not a universal result for every shape or mutation pattern.

## Structures matched to your data contract

LibraDex indexes are structurally matched to the key and identity contract you declare. For ordinary scalar types, the typed factory determines the matching scalar family automatically. Where the logical data needs a size contract—such as bounded binary or arbitrary-length values—the creation API makes that contract explicit.

LibraDex does not inspect a workload and guess a layout later. The declared contract determines the persisted index structure at creation, and that contract is preserved when the catalog is reopened.

| Key data type | Scalar 8-byte identity | Scalar 16-byte identity | Variable identity |
| --- | --- | --- | --- |
| Scalar 8-byte | SS8-8 | SS8-16 | SV8 |
| Scalar 16-byte | SS16-8 | SS16-16 | SV16 |
| Fixed-N key† | FSN-8 | FSN-16 | FSN-V |
| Variable key | VS8 | VS16 | VV |

† **Fixed-N** means a developer-selected maximum key width, recorded when the index is created and preserved on reopen. Individual logical keys may be shorter. The current programmable fixed-key range is **1–515 bytes**. Fixed-N is useful when lengths vary but remain tightly bounded: it uses fixed-width storage for the selected bound instead of the variable-key path. When `N` is 8 or 16, the fixed-key family retains compact specialized storage for those widths without treating the keys as scalar CLR values.

| Storage family | Best fit | Practical effect |
| --- | --- | --- |
| Fixed scalar | Native values with known 8- or 16-byte representations | Dense fixed rows, predictable comparisons, and efficient ordered traversal |
| Fixed 8/16 or Fixed-N key | Binary or normalized keys with a known upper bound, including keys whose actual lengths vary within that bound | Compact fixed-width layouts avoid variable-key handling; the tradeoff is the configured cap and possible unused capacity for shorter keys |
| Variable key or identity | Strings, blobs, paths, or opaque IDs with meaningful length variation | Stores actual lengths and supports flexible values, with per-record length metadata |
| Composite key | Several typed components queried together | Preserves component types and ordering for compound lookup rather than reducing application semantics to an untyped blob |

### Why it is not simply a radix tree or ART

LibraDex uses byte-prefix routing, so it shares a useful property with radix-style structures: an encoded key can be directed by its prefix. Routing is only part of the design. It directs work into dense, typed shelves where final lookup, range traversal, and counting operate over packed ordered entries.

This routing-plus-dense-shelf design is intended to improve locality for storage, RAM caches, and CPU cache lines. It avoids treating every key as a pointer-heavy generic tree leaf while preserving typed, durable index behavior. ART-style adaptive node layouts and generic radix-tree containers solve related routing problems, but they are not the same storage and last-mile traversal model.

## Retrieve the side of the index you need

LibraDex keeps both sides of an index relationship accessible. Depending on the operation, a caller can retrieve identities, keys, or key-and-identity entries—and can stream those results rather than first building a full collection.

This is intentional: an index is not treated as an opaque implementation detail. It remains a directly usable, typed relationship between a key and an identity. Use the narrowest result form that satisfies the operation to reduce unnecessary work and allocation.

## Allocation-conscious design

LibraDex is designed to keep GC and allocation pressure out of paths where they do not add value. Its implementation uses byte-oriented codecs, spans, pooled buffers, borrowed views, stack-local state, and forward-only readers where those choices make direct index operations less allocation-heavy.

Use streaming readers and typed APIs when low allocation matters. Allocation is not promised to be zero in every API: caller-owned `byte[]` or string materialization, explicit result collections, and other requested object-shaped results necessarily allocate. The goal is to avoid unnecessary allocation and object churn while retaining direct, understandable .NET APIs.

## Thread safety and concurrent writing

LibraDex supports thread-safe reads against coherent published state and explicit APIs for periods when multiple threads may submit writes. Ordinary mutations remain optimized for the simple one-owner case. When multiple threads may write, use the concurrent writer or supported concurrent-batch APIs.

Writers operating on independent physical areas of an index can make progress concurrently. Writes to the same key—or keys close enough to land in the same physical area—may contend, queue, or retry while LibraDex preserves coherent publication. Duplicate-key and overlapping-data semantics remain part of the logical index contract chosen by the application.

Concurrent writing is supported by specific shapes and operations. Select the concurrent API deliberately, and validate its behavior with the key distribution, read/write mix, and storage environment used in production.

## Durability and maintenance

File-backed catalogs are explicit-lifetime resources: create or open a catalog, use its index handles, then close or dispose the catalog. The public API includes assessment, backup, compaction, and repack workflows so storage lifecycle remains an explicit operational concern.

`CatalogOptions.UseRecoverableFileFormat` opts a newly created file into the recoverable format. It affects creation only; opening an existing file does not silently upgrade it.

## When LibraDex is not the right fit

Choose another tool when the primary need is:

- A relational database with SQL, joins, broad transaction semantics, and server-managed administration.
- An object/document database that owns object persistence and querying.
- A simple collection with no need for secondary indexes, rich filtering, ordered traversal, or persistence.
- One combined data-and-index format in which the stored data and indexing engine must be intrinsically coupled.

## Status and target

LibraDex targets `net8.0`. The repository includes focused validation for fixed, routed, and variable storage shapes; persistence/reopen behavior; conditions and readers; catalog maintenance; recovery-oriented publication; and concurrency admission. That validation establishes tested behavior; it is not a universal benchmark or a substitute for application-specific testing.

## Next reading

The public API is designed to be discoverable from `Catalog`, `catalog.Indexes`, and the typed index factory selected for a key shape. The package README will evolve with worked condition and composite-index examples, API references, and operational guidance.
