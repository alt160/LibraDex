# Shelf-route option performance evaluation

Evaluated 2026-09-17, America/Phoenix. This is pre-production evidence, not a production repair or engine acceptance result.

## Outcome

Prefer ownership refinement at structural change for the existing direct-routing path, where the scenario's ownership and publication contracts permit it. Avoid an unconditional extra guard level. Preserve same-parent multi-byte splitting as a conditional candidate: a specialized small-router prototype is competitive, but the current compressed reader is not a general CPU-performance replacement for direct routing. Do not select a universal fanout threshold from this microbenchmark.

The complementary repair branches in `shelf-route-scenario-matrix.md` remain necessary: these measurements do not make a single-parent refinement correct for shared owners, skipped stems, terminal transitions, or incompatible walkers.

## Reproducible artifacts and method

Isolated project: `E:\VSProjects\Diagnostics\LibraDexRouteOptions-20260917\RouteOptions.csproj`; source: `Program.cs`. No production C# files were edited. The project links unchanged RouterReader, RouterWriter, RouterLayout, RouterRouteSnapshot, and RouterMultiByteRouteSnapshot source.

Primary results are `pinned-run-1.json`, `pinned-run-2.json`, and `pinned-run-3.json`. Earlier `run-*`, `binary-run-*`, and `final-run-*` outputs are retained, including noisier unpinned measurements. `jit-final.txt` is generated assembly; timings from `jit-final-run.json` are deliberately excluded because that run uses shorter loops and disassembly instrumentation.

- AMD Ryzen 9 8945HS, 8 cores / 16 logical processors; Windows; .NET 8.0.31 x64, Release optimized.
- Only the benchmark process is pinned, affinity `0x4000`. Tiered compilation disabled (`DOTNET_TieredCompilation=0`): FullOpts JIT, not a Tiered-PGO production comparison.
- Five fanouts (2, 8, 32, 128, 256 route entries), three admitted-key distributions (uniform, 90% first stem, 90% last stem), deterministic preallocated 4,096-key query sequences.
- Each process has seven samples per candidate, 131,072 lookup operations per sample, warmup, rotated candidate order. Three independent processes. Reported numbers are medians of the three process medians, not confidence intervals.
- Cycles use Windows QueryThreadCycleTime: OS-reported thread cycles, not retired instructions or a fixed-frequency conversion of elapsed time. No hardware cache-miss or branch-misprediction counters were collected.
- Allocations use GC.GetAllocatedBytesForCurrentThread. All five admitted-key lookup candidates measured zero managed bytes per operation.
- All 65,536 two-byte keys were checked for each fanout: both isolated prototypes against actual compressed-reader semantics, including off-stem fallback; admitted keys additionally against direct/guard routing and an independent leaf oracle. That is 327,680 per-key validations per process, 983,040 across the three primary runs. These are routing-fixture checks, not all-shape engine regressions.
- Final benchmark Build succeeded with zero warnings and zero errors (`build-pinned.log`). Source snapshots were taken before compilation in the isolated project's `.code-history`.

## Resident lookup results

Each cell is **nanoseconds / thread cycles per lookup**, rounded. Fanout means compressed route-entry count; each first-byte stem has two complete final-byte ranges.

| Representation | 2 | 8 | 32 | 128 | 256 |
|---|---:|---:|---:|---:|---:|
| Existing direct parent + child | 5.41 / 21.53 | 5.70 / 22.66 | 6.01 / 23.02 | 5.82 / 23.20 | 7.10 / 28.36 |
| Direct + extra exact-prefix guard | 8.35 / 32.13 | 8.09 / 32.25 | 7.85 / 31.27 | 8.79 / 34.91 | 9.66 / 38.39 |
| Existing compressed reader | 11.28 / 44.99 | 19.72 / 78.78 | 60.40 / 239.52 | 197.18 / 760.70 | 404.47 / 1536.00 |
| Isolated binary-search prototype | 15.61 / 59.35 | 23.95 / 83.71 | 31.03 / 122.83 | 44.00 / 163.50 | 55.46 / 220.48 |
| Isolated hoisted-linear prototype | 3.89 / 15.56 | 5.93 / 23.62 | 21.22 / 84.55 | 57.58 / 221.18 | 91.67 / 363.44 |

Variation matters: at fanout 256, the three process medians span 7.02–7.47 ns for direct, 9.23–11.57 for guard, 383.42–504.55 for existing compressed, 49.46–58.03 for binary, and 90.02–107.36 for hoisted linear. At fanout 2, direct spans 5.30–5.62 and hoisted linear 3.25–4.09. This supports broad rankings, not fine-grained universal crossover constants.

The prototypes are deliberately narrow: valid two-byte compressed pages with two complete final-byte ranges per stem. They are not production replacements covering arbitrary prefix widths, sparse layouts, malformed input, or all consumer contracts.

### Distribution and fallback

At fanout 256, hot-first / hot-last ns per lookup:

| Representation | Hot first | Hot last |
|---|---:|---:|
| Direct | 6.31 | 6.37 |
| Guard | 9.07 | 9.09 |
| Existing compressed | 50.29 | 734.01 |
| Binary prototype | 49.47 | 46.95 |
| Hoisted-linear prototype | 15.39 | 152.56 |

Existing compressed off-stem fallback measured 23.33, 67.18, 243.47, 882.37, and 1949.79 ns across the five fanouts. These are separate semantic cases: direct routing has an unset route for these keys, so its miss cannot be compared as an equivalent operation. Effective fallback ownership must still be preserved by any repair.

## Calls, traversal length, and generated code

In this FullOpts build, direct lookup crosses two router pages and executes two actual FindTarget(byte, out) calls per query. Guard lookup crosses three and executes three. The compressed loop crosses one router page and calls the full-key reader once, but that reader linearly scans entries and calls GetMultiByteRouteStemOffset within the scan. A miss additionally invokes nearest-route fallback. Thus a smaller traversal depth can perform substantially more work.

The hoisted-linear prototype moves invariant page-layout computation outside its entry scan; its hot lookup is inlined into the batch loop. The binary prototype reduces entry comparisons but retains calls to GetMultiByteRouteStemAt. Cold exception-helper calls in the assembly are not counted as ordinary successful lookup work.

Batch-loop machine-code sizes are 307 bytes (direct), 430 (guard), 194 (compressed caller), 519 (binary), and 491 (hoisted linear). The compressed callee is another 828 bytes. These are descriptive JIT observations, not directly comparable total instruction counts: some bodies inline and others do not. Page hops, nested calls, dynamic loop length, and code size must not be conflated.

## Structural metadata cost

Seven samples of 8,192 operations per candidate/process; prepared input metadata. Median of process medians:

| Probe | ns/op | cycles/op | managed bytes/op |
|---|---:|---:|---:|
| Parent check, one owner / no aliases | 211.38 | 808.93 | 0 |
| Parent refinement, two aliases | 586.91 | 2330.05 | 4120 |
| Parent refinement, 256 aliases | 611.41 | 2385.36 | 4120 |
| Extra guard page construction (256-alias case) | 565.20 | 2215.24 | 4120 |
| Compressed page construction, 256 routes | 3151.50 | 11985.36 | 4120 |

The refinement model scans the parent and clones its 4-KiB image only when aliases exist; it clears routes not owned by the replacement. The 4,120 bytes include the managed array overhead, not extra on-disk index space. These are not complete split timings: planning-input allocations, tuple redistribution, storage allocation, locks, cache invalidation, write ordering, and durability are excluded. The refinement scan belongs at structural change, not every ordinary insertion or lookup.

## What could change the ranking?

Memory and I/O are the strongest competing consideration. In these constructed fixtures, compressed routing uses one 4-KiB page; direct uses a parent plus fanout/2 children; guards add fanout/2 pages. At 256 entries that is 1 versus 129 versus 257 routing pages. This is a fixture footprint, not a prediction for an entire real index. All measured pages are resident managed arrays; no file-session page acquisition, storage reads, lock contention, cold-cache behavior, or actual persistence was measured. Fewer pages could outweigh extra CPU in a different workload.

## Production decision gates

1. Use this evidence to prioritize structural-time ownership refinement where correct, retaining compatible existing fast reader paths. An always-present guard is not the default performance choice.
2. Keep same-parent splitting eligible when the effective domain, whole-page capacity, sibling preservation, and consumer capabilities allow it. Low-fanout hoisted compressed lookup is the next-best performance branch; binary search is a large-fanout improvement over the current scan, not a demonstrated winner over resident direct routing.
3. Before production selection, run real engine regressions from the scenario matrix across applicable shelf shapes and entry paths, including publication/concurrency and reopen. Routing equivalence alone does not validate a split.
4. Compare matched end-to-end prototypes on split latency, steady-state reads/inserts, allocations, routing-page count, resident versus cold working sets, and concurrent readers. Include the Meridian failure fixture and larger representative datasets. Do not conflate the original 1M-item ingestion timing with these nanosecond routing measurements.

## Linked-source SHA-256

Captured after measurement; production Git status contains only the review/scenario documents, no tracked source edits.

| Source | SHA-256 |
|---|---|
| Views/RouterReader.cs | C80F982B15FCDAEA5145288451274F3C4D1DE72246ACFA1686499A4EEEADE291 |
| Views/RouterWriter.cs | 7B72DB0B656945ABC811C781FCB7F5CC9D651C18C0C7DA08162C6DBBCFCD36CB |
| Layouts/RouterLayout.cs | A471EEFC2CEB9842351BAD15C07DEB966BB7F09BD56F1A5697FF05F146F77206 |
| Routing/RouterRouteSnapshot.cs | F2DDD6FA2D7C961DCC1158029E12F5D96D4C61DEBA0B74007722004121AB5647 |
| Routing/RouterMultiByteRouteSnapshot.cs | 4A7F36E45441A162FFAA832AC52BDF3FD139C7796C73295362B4C2EAC6E076E6 |
