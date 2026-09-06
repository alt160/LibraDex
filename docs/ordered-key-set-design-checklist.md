# LibraDex Ordered Key Set Design Checklist

## Status

- 🟨 [~] PARKED / INTERNAL / INCOMPLETE: the experimental set family is retained for later work but deliberately excluded from LibraDex's supported public API.
- 🟨 [~] Product direction remains promising, but first-class product status is deferred until the incomplete work and acceptance gates below are resolved.
- 🟨 [~] Memory-backed and file-backed prototypes retain the same logical behavior; catalog integration and a supported durable contract remain incomplete.
- 🟩 [x] Abraxas is expected to be the first high-value memory-backed consumer, especially for large identity deduplication.
- 🟩 [x] Natural key ordering is a first-class result of the radix/shelf model, not an incidental implementation detail.
- 🟩 [x] Repeated-occurrence questions such as "which keys occurred two or more times?" are in scope for the design investigation.
- 🟩 [x] The physical design must borrow only the LibraDex storage/routing machinery that earns its keep for a key-only collection.
- 🟨 [~] Candidate naming and the presence-versus-counting semantic split are retained internally for continued evaluation.
- 🟨 [~] The experimental UInt64 sorted, routed, and counted slices are implemented internally; broader catalog discovery, cursor/range APIs, dense leaves, bulk build, and Abraxas adoption remain open.

This document is the controlling checklist for the parked ordered-set workstream. The UInt64 presence and counted prototypes, typed handles, tests, and measurements are retained as internal R&amp;D assets. They carry no public compatibility or support commitment. Unchecked items gate any future public promotion, catalog integration, or Abraxas adoption.

## 1. Mission

Create a compact, ordered, mutable key collection that:

1. provides membership behavior competitive with managed hash-based sets at large cardinalities;
2. retains LibraDex natural key order for forward, reverse, boundary, and range traversal;
3. works over the existing memory and file DataKernel backings;
4. avoids key-plus-dummy-identity storage and index-only metadata;
5. supports bounded-allocation bulk ingestion and streaming reads;
6. reuses shelves, router arenas, and retired extents before extending durable storage;
7. preserves direct authoritative roots and does not introduce WAL/replay semantics;
8. provides a clean path for occurrence-counting workloads without charging ordinary presence-only sets for unused counts;
9. can replace high-retained-memory `HashSet<T>` deduplication in selected LibraDex and Abraxas query paths only after measured parity and semantic proof.

## 2. Non-goals

- 🟩 [x] Do not represent a set as a normal LibraDex index with a constant/dummy identity.
- 🟩 [x] Do not make the new collection implement `IIndex`; it has no identity axis.
- 🟩 [x] Do not inherit index-set, projection, inverse-identity, source-completeness, identity-multiplicity, grouping, or companion-index metadata.
- 🟩 [x] Do not replace `HashSet<T>` everywhere merely because a LibraDex set exists.
- 🟩 [x] Do not optimize only for monotonic Abraxas `RowID` values; any future public LibraDex feature must remain useful for arbitrary supported keys.
- 🟩 [x] Do not turn durable set mutation into append-only historical logging.
- 🟩 [x] Do not require result materialization for ordered traversal or set algebra.
- 🟩 [x] Do not make an ordinary mathematical set silently retain exact occurrence counts.

## 3. Existing Contracts To Preserve

- 🟩 [x] Full distinguishable-key shelves refine routes and split locally.
- 🟩 [x] Deleted capacity is reused before avoidable topology growth.
- 🟩 [x] A converted shelf extent can act as a local router arena.
- 🟩 [x] File-backed extents use self-describing allocation segments and bounded direct allocation bitmaps.
- 🟩 [x] Replacement topology becomes reachable before superseded extents become reusable.
- 🟩 [x] Readers observe an authoritative direct-root topology, with no mutation-log replay required.
- 🟩 [x] Set durability uses one authoritative root descriptor and one authoritative `RootRouterOffset`; it does not use an A/B root pair, active-slot selector, or catalog-style alternating superblock.
- 🟩 [x] `Commit` remains a coherent publication/optional flush boundary for memory and file backings; it must not be interpreted as permission to add A/B or WAL/replay machinery to the set format.
- 🟩 [x] Memory-backed prototypes retain the same candidate lifecycle shape as file-backed prototypes, except reopen.
- 🟩 [x] Cross-shape review is mandatory whenever shared shelf or router behavior changes.

## 4. Terminology And Namespace Boundaries

- 🟩 [x] `Catalog.IndexSet(name)` continues to mean a named identity universe containing multiple indexes.
- 🟩 [x] `LibraDexPreparedObjectSet` continues to mean a managed/prepared query operand and is not a stored collection.
- 🟨 [~] Internal candidate total-order presence handle: `LibraDexSortedSet<TKey>`; no public compatibility commitment exists.
- 🟨 [~] Internal candidate route-ordered presence handle: `LibraDexRoutedSet<TKey>`.
- 🟨 [~] Internal candidate repeated-occurrence handle: `LibraDexCountedSet<TKey>`.
- 🟨 [~] Catalog discovery surface leading shape: `catalog.Sets.UInt64.Create(name)` and corresponding `Open` / `CreateOrOpen` verbs; validate the final IntelliSense path during public promotion.
- ⬜ [ ] Ensure generated/non-generic callers have a strict runtime-key facade without confusing it with `LibraDexPreparedObjectSet`.

## 5. BLX Physical Branches

| Branch | Membership | Ordered traversal | Space | Reuse of LibraDex core | Principal risk | Status |
|---|---:|---:|---:|---:|---|---|
| A. Key-only adaptive radix shelves | Strong | Native | Strong; key only | Router walk, shelves, allocator, publication | New leaf formats and density transitions | 🟩 [x] Leading branch |
| B. Extendible-hash shelves | Potentially strongest random exact lookup | Requires secondary ordering structure or sort | Strong | Mainly allocator/publication | Two structures or lost order; no longer naturally LibraDex | 🟨 [~] Benchmark fallback only |
| C. Existing SS key plus constant identity | Existing behavior | Native | Poor: repeated identity and index metadata | Maximum code reuse | Pays for semantics the set does not have | 🟥 [!] Rejected |
| D. Existing exhausted-key identity chain | Strong for one key or monotonic appends | Limited across arbitrary keys | Compact in narrow case | Terminal chain reuse | Arbitrary insertion and global order are the wrong shape | 🟥 [!] Rejected |
| E. Abraxas-only segmented RowID bitmap | Exceptional for dense same-store RowIDs | Native numeric order | Exceptional when dense | Allocator plus minimal directory | Not a general LibraDex key collection | 🟨 [~] Candidate specialization under A |
| F. Managed `HashSet<T>` retained as-is | Excellent common exact lookup | None | High managed overhead at large counts | None | GC/retained-memory pressure; no durable form | 🟨 [~] Baseline and small-set winner |

### BLX conclusion

Branch A is the first proof target. It preserves LibraDex's prefix-routed identity while making key-only storage honest. Branch E is not a competing product; it may become a dense scalar leaf selected by Branch A. Branch B remains the next-best branch only if isolated random-membership measurements show that radix routing cannot meet an acceptable latency envelope.

## 6. Minimal Persisted Metadata

### 6.1 Catalog definition

Keep only fields required to find, validate, and decode the collection:

- 🟩 [x] collection kind and format version;
- ⬜ [ ] name and catalog slot/directory ownership;
- 🟨 [~] key family/codec and key comparison contract are fixed to naturally ordered UInt64 in the first format; catalog-level codec discovery remains open;
- 🟩 [x] backing-neutral root offset;
- 🟩 [x] live distinct-key count;
- 🟩 [x] mutation/publication generation;
- 🟩 [x] selected 4 KiB sparse shelf profile;
- 🟩 [x] persisted exact or caller-selected saturation policy and counter width;
- 🟨 [~] format magic/version and structural validation are present; checksums/fences remain intentionally unelected pending failure-injection evidence.

### 6.2 Explicitly omitted index metadata

- 🟩 [x] identity type/family and identity codec;
- 🟩 [x] key uniqueness setting: set keys are distinct by definition;
- 🟩 [x] identity-to-key multiplicity;
- 🟩 [x] inverse identity lookup;
- 🟩 [x] source completeness/watermark state;
- 🟩 [x] expression/function selector metadata;
- 🟩 [x] projection and string companion-index ownership;
- 🟩 [x] key/identity grouping and aggregate metadata;
- 🟩 [x] duplicate-run descriptors whose purpose is storing several identities for one key.

## 7. Candidate Physical Model

### 7.1 Router

- 🟩 [x] Reuse the proven exact-stem prefix-routing rules and canonical route ownership.
- 🟩 [x] Retain only the existing direct-router routing/publication fields; set roots and leaves contain no identity/index metadata.
- 🟩 [x] Reuse the existing direct one-byte router page byte-for-byte without adding index-only set metadata.
- ⬜ [ ] If a smaller set router materially increases fanout or locality, implement it as a distinct format sharing traversal primitives rather than copy/pasting the entire index router.
- 🟩 [x] Preserve same-extent shelf-to-router conversion and file reopen discovery.

### 7.2 Sparse fixed-scalar shelf

- 🟩 [x] Start with unsigned 64-bit keys because it directly represents Abraxas RowIDs and isolates the physical proof.
- 🟩 [x] Store keys once, in encoded sorted order, with no identity bytes.
- 🟩 [x] BLX two local layouts:
  - physically sorted packed keys with binary search and local byte shifting;
  - append payload plus compact sorted slot ordinals, allowing cheaper movement/deletion reuse at the cost of per-key slot bytes.
- 🟨 [~] Randomized incremental insertion, duplicate-heavy insertion, delete, clear/repopulate, and ordered iteration are measured; dedicated monotonic and bulk-merge rows remain open.
- 🟩 [x] Keep live count and fixed capacity in the shelf header so no operation scans a full shelf merely to discover capacity.
- 🟩 [x] Split on a real key-byte boundary and publish disjoint child intervals.

### 7.2.1 Routed presence shelf

- 🟩 [x] `LibraDexRoutedSet<TKey>` preserves ordered radix-router regions while storing each leaf's live keys in physical end-fill order.
- 🟩 [x] The first routed slice is presence-only `ulong` over both memory and file DataKernel backings; counted routed leaves remain a later measured decision.
- 🟩 [x] Duplicate detection uses exact leaf membership rather than ordering metadata; removal closes a hole by moving the final live key into it.
- 🟩 [x] Full routed leaves may temporarily sort one bounded 509-key split workspace to discover safe byte boundaries; this does not impose ongoing leaf order or suffix-shift mutation cost.
- 🟩 [x] `CopyTo` and `ToArray` expose router-major physical order only. They make no total-order promise, and callers needing total key order choose `LibraDexSortedSet<TKey>`.
- 🟩 [x] Routed and sorted sets share root, direct-router, allocator, coherent publication, reopen, clear, and extent-reuse mechanics; only leaf layout and leaf-local operations differ.
- 🟩 [x] RoutedSet retains the single authoritative root and does not add A/B durability, WAL, or replay behavior.
- 🟩 [x] Benchmark immediate insertion, exact membership, removal, traversal, retained memory, and file reuse against `HashSet<ulong>`, `System.Collections.Generic.SortedSet<ulong>`, and `LibraDexSortedSet<ulong>`.

### 7.3 Dense scalar shelf

- ⬜ [ ] Treat a dense bitmap as a leaf representation, not a separate product.
- ⬜ [ ] A dense leaf owns one explicit aligned suffix window beneath a routed prefix.
- ⬜ [ ] Membership is a bit test; ascending/reverse traversal uses set-bit scans and reconstructs full keys from the route prefix plus bit ordinal.
- ⬜ [ ] Store exact population count in the leaf header.
- ⬜ [ ] Define measured sparse-to-dense and dense-to-sparse thresholds with hysteresis to prevent conversion thrash.
- ⬜ [ ] Never allocate a bitmap for an unbounded 64-bit interval; the router must first narrow ownership to a bounded suffix window.
- ⬜ [ ] Evaluate 8-, 12-, and 16-bit suffix windows against lookup cost, router depth, and bytes/key.

### 7.4 Variable and fixed-byte keys

- ⬜ [ ] Do not generalize the first physical implementation prematurely.
- ⬜ [ ] Define how prefix-of-another-key and exhausted-key markers are represented without an identity payload.
- ⬜ [ ] Reuse VS/Fixed-N encoding and comparison primitives only after scalar proof passes.
- ⬜ [ ] Evaluate prefix compression inside sparse variable-key leaves after correctness; it must not create deep call chains or per-key allocations.

## 8. Presence And Occurrence Semantics

### 8.1 Presence-only behavior

- 🟩 [x] `TryAdd(key)` returns `true` only when the key became newly present.
- 🟩 [x] `Contains(key)` performs one route walk and one leaf lookup.
- 🟩 [x] `Remove(key)` returns whether a present key was removed.
- 🟩 [x] `Count` is the exact number of distinct keys and is available without traversal.
- 🟩 [x] `Clear` retires the owned topology through the allocator rather than deleting one key at a time.
- 🟩 [x] The presence-only leaf stores no occurrence counter.

`TryAdd` alone can signal that the current input repeats a previously seen key. It cannot later enumerate every key that appeared at least twice unless the caller retains those keys elsewhere. That distinction must remain explicit.

### 8.2 Counted/repeated-occurrence behavior

- 🟩 [x] Counting is a separate internal `LibraDexCountedSet<TKey>` candidate handle sharing the sorted routing core with `LibraDexSortedSet<TKey>`.
- 🟩 [x] Ordinary set storage does not pay counter bytes or counter-update work.
- 🟨 [~] Increment, decrement, whole-key remove, exact/capped lookup, `ContainsAtLeast`, and ordered key/count copying are implemented; a streaming qualifying-threshold cursor remains open.
- 🟩 [x] Support two persisted counted-set policies: caller-selected saturation for bounded questions and exact counting when exact cardinality is required.
- 🟩 [x] The common duplicate-detection profile may use compact `0 / 1 / 2+` saturation without claiming exact counts above the configured ceiling.
- 🟩 [x] Exact mode uses unsigned counters with explicit, non-wrapping overflow behavior.
- ⬜ [ ] Dense counted leaves may use parallel bit planes for small saturated thresholds; exact counts require a separate packed/sparse counter representation.
- 🟩 [x] Ordered counted output is by key with `(key, stored count)` entries; saturating mode explicitly reports capped rather than exact semantics.

### 8.3 Candidate semantic branches

| Branch | Presence cost | Answers `2+` later | Exact counts | API clarity | Status |
|---|---:|---:|---:|---:|---|
| Separate set and counted-set handles sharing routing | Minimal | Yes, in counted handle | Exact or caller-selected saturation | Strong | 🟩 [x] Selected |
| One set type with creation-time `Presence`, `Saturating`, or `Exact` mode | Minimal when physical formats differ | Yes | Optional | One discovery surface, more stateful semantics | 🟨 [~] Viable alternative |
| Presence set plus caller-managed second duplicate set | Minimal | Yes | No | Simple core, extra caller coordination | 🟨 [~] Useful composition, insufficient as the sole prospective product answer |
| Exact counter on every set entry | Highest | Yes | Yes | Semantically misleading for ordinary sets | 🟥 [!] Reject as default |

## 9. Ordered-Set Surface

Ordered behavior is a product capability, not merely an internal implementation convenience.

- 🟨 [~] Ascending and descending bounded-workspace traversal is implemented internally and exposed through caller-buffer `CopyTo`; a public resumable cursor remains open.
- ⬜ [ ] Inclusive/exclusive range traversal without materialization.
- ⬜ [ ] `Min`, `Max`, predecessor/floor, successor/ceiling, and exact seek.
- ⬜ [ ] `First`/`Last` and bounded `Take` from either direction.
- ⬜ [ ] Prefix traversal for variable-byte/string-capable set shapes.
- 🟩 [x] Stable order across memory/file backing and reopen.
- 🟩 [x] Internal traversal and caller-buffer copying allocate bounded workspace rather than cardinality-sized deduplication/sort state.

## 10. Set Algebra

The ordered physical form permits merge-style algebra with bounded cursor state:

- ⬜ [ ] union;
- ⬜ [ ] intersection;
- ⬜ [ ] difference;
- ⬜ [ ] symmetric difference;
- ⬜ [ ] subset/superset and overlap predicates with early exit;
- ⬜ [ ] output to a caller callback/cursor, caller buffer, or another LibraDex set;
- ⬜ [ ] in-place variants only where publication and alias rules remain unambiguous;
- ⬜ [ ] compatible key-codec/comparison validation before a two-set merge begins.

These are capabilities a `HashSet<T>` does not provide in key order and that a disk-capable ordered set can perform without loading both inputs into managed memory.

## 11. Mutation And Bulk Build

- 🟩 [x] Single-key UInt64 operations use direct scalar bits and avoid object erasure or encoded-key allocation.
- 🟨 [~] Preserve fully published immediate `TryAdd` semantics while exposing `AddMany(ReadOnlySpan<TKey>)` through a pooled sorted-union build; enumerable/bounded-streaming ingestion remains open, and the current non-empty route temporarily materializes the existing-plus-input union.
- ⬜ [ ] A sorted/distinct input hint enables shelf-sized streaming construction.
- ⬜ [ ] Unsorted bulk input chooses between incremental routing and bounded sort/merge batches based on measured cardinality and backing.
- 🟩 [x] Duplicate input behavior is explicit: ignored for presence sets; increments according to exact or saturating counted policy.
- 🟨 [~] The first `AddMany` implementation coalesces a complete replacement topology plus root count/generation into one publication; an incremental dirty-page batch route remains open for small additions to a large existing set.
- 🟩 [x] Optimization order selected: improve and measure the set engine first; translate a shared primitive to ordinary indexes only after a separate cross-shape evidence gate.
- 🟩 [x] `Clear` replaces root routes with an empty image and retires the old topology as one collection operation.
- 🟨 [~] File-backed reopen validates root kind/version, UInt64 format, counts, generation, counted policy, and routed page kinds; exhaustive offline topology validation remains a maintenance follow-on.

## 12. Concurrency And Ownership

- 🟩 [x] Match the existing serialized-publication reader/writer contract without implying lock-free mutation.
- 🟩 [x] Memory readers retain coherent snapshots; set mutation uses the blocking-read publication mode where in-place page ownership requires it.
- 🟩 [x] File readers and retirement honor the existing coherent-reader boundary.
- ⬜ [ ] Disjoint leaf preparation may be parallel, but shared route/root publication remains explicit and measured.
- 🟩 [x] Concurrent `TryAdd` has one linearized winner for a previously absent key.
- 🟩 [x] Counted increments do not lose updates; exact arithmetic is checked and saturation never inflates beyond the ceiling.
- ⬜ [ ] Abraxas may use one set per query/execution owner; lifecycle and disposal must be deterministic.

## 13. LXL Scenarios

### 13.1 One million unique keys plus two duplicates

1. Create an empty memory-backed UInt64 presence set.
2. Feed one million keys in the selected order.
3. On each `TryAdd`, emit the key only when the return value is false.
4. Verify exactly the two repeated inputs are detected at their second occurrences.
5. Verify distinct count remains one million.
6. Traverse ascending and descending; compare hashes and boundaries with a sorted reference.
7. Measure time-to-first-duplicate, total throughput, allocated bytes, peak working set, and retained bytes after collection.

### 13.2 One million inputs with fifty-percent repetition

1. Feed 500,000 distinct keys twice under clustered, interleaved, and randomized distributions.
2. Presence set must end with 500,000 keys and report 500,000 failed `TryAdd` calls.
3. Counted candidate must report every key as `2+`; exact mode, if selected, reports count two.
4. Verify no result-cardinality-sized second managed set is required by the counted candidate.
5. Measure whether duplicate-heavy locality improves or degrades each leaf representation.

### 13.3 Dense Abraxas RowID range

1. Use one store ID with a long monotonic lower-56-bit interval.
2. Verify the routed dense leaf/bitmap specialization is selected only after its measured threshold.
3. Remove sparse holes, reinsert them, and verify bit population/count parity.
4. Traverse across router and dense-leaf boundaries in both directions.
5. Compare bytes/key and throughput with `HashSet<ulong>` and the sparse leaf.

### 13.4 Sparse full-domain UInt64 keys

1. Generate one million deterministic random keys spanning the full unsigned domain.
2. Prove no unbounded bitmap allocation occurs.
3. Exercise repeated router splits, reopen, removals, and extent reuse.
4. Compare exact lookup and iteration with `HashSet<ulong>` and `SortedSet<ulong>`.

### 13.5 Durable churn

1. Create a file-backed set and insert enough keys to create multiple allocation segments.
2. Remove alternating ranges and then insert different keys that fit the retired classes.
3. Close/reopen between phases.
4. Prove authoritative parity, natural order, and reuse before EOF extension.
5. Repeat clear/repopulate cycles and report live, reusable, and file bytes separately.

### 13.6 Publication failure

Inject failures after allocation claim, replacement payload, route/root publication, and retirement. Reopen after each. The old or new complete set must be authoritative; a failure may conservatively leak capacity but must not expose partial keys, duplicate keys, an incorrect distinct count, or live-byte reuse.

## 14. Benchmark Matrix

### 14.1 Baselines

- 🟩 [x] `HashSet<ulong>` for exact mutable membership.
- 🟩 [x] `SortedSet<ulong>` for managed ordered membership.
- 🟩 [x] `Dictionary<ulong, ulong>` for exact occurrence counting.
- ⬜ [ ] Current LibraDex SS8-8 with a constant identity, retained only as a proof of avoided overhead.
- ⬜ [ ] SQLite in-memory `INTEGER PRIMARY KEY` / `WITHOUT ROWID` equivalent where semantic parity is possible.
- 🟨 [~] LibraDex sparse presence and exact/saturating counted candidates are measured; dense leaf remains open.

### 14.2 Cardinalities and distributions

- ⬜ [ ] 0, 1, 16, 1,024, 49,057, 258,919, 1,000,000, and 10,000,000 inputs where machine capacity permits.
- ⬜ [ ] ascending, descending, uniform random, clustered-prefix, and adversarial boundary order.
- ⬜ [ ] all unique; exactly two repeats; 1%, 10%, and 50% repeated inputs; one very hot key.
- ⬜ [ ] warm exact hits, cold exact hits, misses, mixed hit/miss, forward scan, reverse scan, narrow ranges, and full scan.

### 14.3 Required metrics

- 🟨 [~] operations/second and elapsed insertion/export time are reported for immediate, explicit batch, buffered/default hash, buffered/pre-sized hash, streaming hash, sorted set, and counted controls; isolated repeated medians remain open;
- ⬜ [ ] time to first result and time to completion;
- 🟨 [~] total managed allocations and bytes/input are reported; phase-local sorted-union/build attribution remains open;
- 🟨 [~] approximate retained managed bytes and working-set deltas are reported; isolated-process peak validation remains open;
- ⬜ [ ] live bytes/key, committed bytes/key, reusable bytes, and durable EOF size;
- ⬜ [ ] route hops, shelf reads, binary-search comparisons, shifts/rebuild bytes, splits, dense conversions, and allocator claims/reuses;
- ⬜ [ ] Gen0/Gen1/Gen2 collections for long managed comparisons;
- ⬜ [ ] file cold/warm and reopen behavior, with OS-cache state reported rather than implied.

### 14.4 Proposed acceptance approach

- 🟩 [x] Correctness and structural invariants gate the current comparison timing; a mismatched cardinality, duplicate count, order, counted value, or checksum invalidates the command.
- ⬜ [ ] Run each engine in an isolated process, serial outer execution, at least three repeats, and compare medians.
- ⬜ [ ] Do not require the LibraDex set to beat `HashSet<T>` at tiny cardinalities.
- 🟨 [~] The first one-million-input span-batch evidence shows the LibraDex set ahead of buffered/pre-sized `HashSet<ulong>` for insertion plus ordered output and lower approximate retained memory; repeated isolated medians and smaller-cardinality crossover rows remain open.
- ⬜ [ ] Require dense scalar workloads to demonstrate a material space advantage over `HashSet<ulong>` before enabling automatic dense conversion.
- ⬜ [ ] Set numeric pass/fail thresholds only after the first neutral harness run exposes realistic baselines.

## 15. First Implementation Slice

### 15.1 Design proof

- 🟨 [~] Resolve candidate presence/counting semantics and internal names; public contract and naming remain deliberately uncommitted.
- 🟨 [~] Specify and implement byte layouts for root, router reuse, sparse presence, and exact/saturating counted UInt64 shelves; dense UInt64 remains a follow-on.
- 🟩 [x] Write explicit invariants for ordering, uniqueness, live counts, route ownership, conversion, and publication.
- 🟨 [~] LXL and execute the one-million unique/two-duplicate and fifty-percent-repeat scenarios; dense, sparse-domain churn, durable failure, and longer-duration distributions remain open.

### 15.2 R&amp;D implementation

- 🟩 [x] Add internal UInt64 memory/file presence and counted engines behind assembly-internal typed handles and harness coverage.
- 🟩 [x] Implement sparse shelf exact lookup, ordered traversal, add, remove, split, same-extent transform, and clear.
- ⬜ [ ] Add dense leaf conversion only after sparse proof and instrumentation.
- 🟩 [x] Add file reopen and same-shape allocator-reuse proof before public API promotion.
- 🟩 [x] Run neutral managed baselines and retain the shared direct-router bytes; specialized smaller routers remain a measured future option.

### 15.3 Public promotion

- 🟨 [~] Public promotion is deliberately parked; the experimental set types are excluded from the public API snapshot until this section and the acceptance work are complete.
- ⬜ [ ] Add catalog factory/discovery metadata and typed/non-generic handles.
- 🟨 [~] Added the span-based `AddMany` API with detailed XML documentation; enumerable/bounded streaming, sorted-input hints, and ordered cursor APIs remain open.
- ⬜ [ ] Add maintenance topology, backup, compaction, drop, and reopen coverage.
- 🟩 [x] Remove the parked set family from the public API snapshot; future promotion must deliberately reintroduce and govern its API/IntelliSense shape.
- 🟩 [x] Review and repair shared publication/reservation consequences across the existing affected shelf families.

### 15.4 Abraxas adoption

- 🟩 [x] Correct one measured high-cardinality identity-deduplication route first: generic plan-natural projections now retain final distinct state as `HashSet<TIdentity>`, so Abraxas UInt64 cursors use `HashSet<ulong>` rather than `HashSet<object>`.
- 🟩 [x] Keep the existing structural no-dedupe fast path when query/index proof guarantees one visit per identity.
- 🟩 [x] Select typed `HashSet<T>` for current online final-result dedupe; the parked LibraDex set does not yet justify replacing the BCL route.
- 🟥 [!] Do not adopt the experimental UInt64 set specialization while its public/lifecycle contract remains parked and incomplete.
- 🟩 [x] Preserve first-appearance candidate/output order independently from membership semantics; typed dedupe remains before skip, take, and bookmark paging.
- 🟨 [~] Synthetic one-million sparse-duplicate and half-duplicate shapes pass; live Wherzit validation at 49K, 259K, and later one-million store counts remains available but is not required to prove the container correction.

## 16. Cross-Shape Expansion Review

After UInt64 proof, review rather than blindly clone:

- ⬜ [ ] signed/unsigned 8-, 16-, 32-, 64-, and 128-bit scalar keys;
- ⬜ [ ] GUID, DateTime-like, decimal, floating-point, and canonical numeric encodings;
- ⬜ [ ] Fixed-N byte keys;
- ⬜ [ ] variable byte/string keys, including null/empty/prefix exhaustion where supported;
- ⬜ [ ] exact and folded/sort-key string semantics as separate collection definitions, never hidden query-time conversion;
- ⬜ [ ] dense leaf eligibility by key family;
- ⬜ [ ] occurrence-counter packing by key family;
- ⬜ [ ] maintenance, backup, compaction, allocator, and failure-injection walkers for every promoted shape.

## 17. Accepted Decisions

1. 🟩 [x] Use separate presence and counted-set handles sharing the same routing core.
2. 🟩 [x] Counted sets support both exact and caller-selected saturation policies through distinct leaf formats.
3. 🟩 [x] Implement UInt64 first while retaining the documented general cross-shape contract.
4. 🟨 [~] Retain `LibraDexSortedSet<TKey>`, `LibraDexRoutedSet<TKey>`, and `LibraDexCountedSet<TKey>` as internal candidate names for total-order presence, route-order presence, and occurrence-counting semantics; public naming is not committed.

## 18. Decision Record

| Date | Decision | Evidence / consequence |
|---|---|---|
| 2026-09-02 | The feature is general LibraDex capability, not an Abraxas-only temporary structure. | Memory and file backings share semantics; Abraxas remains the primary initial consumer. |
| 2026-09-02 | Ordered traversal is first-class. | Radix/shelf key order enables forward/reverse/range access and merge-style set algebra without a second ordering structure. |
| 2026-09-02 | Use only necessary LibraDex machinery. | Key-only leaves and minimal metadata avoid dummy identities and unrelated index contracts. |
| 2026-09-02 | Repeated-occurrence questions belong in the design. | Presence-only `TryAdd` detects a repeat at ingestion time, while persistent later `2+` queries require counted state or a second set. |
| 2026-09-02 | Adaptive radix shelves are the leading physical branch. | It preserves LibraDex's model and permits sparse and dense leaf representations; hash routing remains a benchmark fallback. |
| 2026-09-02 | `LibraDexSortedSet<TKey>`, `LibraDexRoutedSet<TKey>`, and `LibraDexCountedSet<TKey>` make total-order, route-order, and counting costs explicit. | Presence sets remain semantically honest; RoutedSet avoids sorted leaf shifts while SortedSet preserves total traversal order. |
| 2026-09-02 | Counted sets support both exact and caller-selected saturation policies. | Saturation optimizes bounded questions such as `2+`; exact mode is available when the true occurrence count is required. Distinct leaf formats prevent one policy from burdening the other. |
| 2026-09-02 | UInt64 is the first physical proof shape. | It directly serves Abraxas RowID deduplication and bounds the first implementation while preserving later cross-shape review. |
| 2026-09-02 | Set durability remains single-root rather than A/B. | A set is a one-dimensional key collection. Coherent payload-before-route publication and optional flushing remain necessary, but catalog-style alternating roots, active-slot selection, and replay are excluded. |
| 2026-09-02 | Optimize the set before ordinary indexes. | Immediate `TryAdd` remains independently published; a separate explicit batch route may coalesce work. Any index translation requires its own evidence and cross-shape review. |
| 2026-09-02 | RoutedSet traversal is router-major physical order only. | Radix regions remain ordered, leaf entries remain end-filled, and callers requiring total ascending/descending order choose SortedSet rather than paying an implicit result sort. |
| 2026-09-02 | Park the entire experimental set family as internal and incomplete. | Preserve implementations, tests, and performance evidence for later continuation, but remove all six set-facing types from the supported public API and make no compatibility commitment. |
| 2026-09-02 | Keep Abraxas final-result dedupe on a typed BCL set while LibraDex sets are parked. | `Iterate<TIdentity>()` now performs genuine final distinct processing with `HashSet<TIdentity>` after logical composition and before paging; proven single-key streams still allocate no set. The one-million UInt64 proof reduced approximate live managed retention by 24.0 MB with two duplicate inputs and 12.0 MB with fifty-percent repeats. |

## 19. Set-First Insertion Evidence

The current Release/x64 one-process comparison is a correctness-gated engineering sample, not yet a repeated isolated-process median. The buffered controls retain their caller-owned input across the measurement baseline, matching the explicit span contract rather than charging input creation to either container.

| Scenario | Engine | Insert | Inputs/s | Ordered export | Approx retained managed bytes |
|---|---|---:|---:|---:|---:|
| 1,000,000 inputs / 999,998 distinct | LibraDex immediate `TryAdd` | 1,521.537 ms | 657,230 | 8.481 ms | 11,837,664 |
| 1,000,000 inputs / 999,998 distinct | LibraDex `AddMany` | 64.479 ms | 15,508,877 | 2.964 ms | 18,135,552 |
| 1,000,000 inputs / 999,998 distinct | Buffered/pre-sized `HashSet<ulong>` | 161.059 ms | 6,208,901 | 51.295 ms | 23,270,608 |
| 1,000,000 inputs / 500,000 distinct | LibraDex immediate `TryAdd` | 923.167 ms | 1,083,228 | 2.080 ms | 6,330,512 |
| 1,000,000 inputs / 500,000 distinct | LibraDex `AddMany` | 53.384 ms | 18,732,134 | 1.236 ms | 5,544,008 |
| 1,000,000 inputs / 500,000 distinct | Buffered/pre-sized `HashSet<ulong>` | 56.451 ms | 17,714,447 | 23.662 ms | 23,270,608 |

Interpretation:

- 🟩 [x] A key-only ordered topology is not intrinsically responsible for the earlier insertion deficit; sorted one-publication construction is competitive or materially faster in these large span workloads.
- 🟩 [x] The immediate route retains its semantic value for online duplicate decisions and per-key visibility, but repeated routing and publication dominate its cost.
- 🟩 [x] No A/B root pair was introduced: `AddMany` builds unreachable replacement payload, publishes one new `RootRouterOffset`, and retires the superseded topology for allocator reuse.
- 🟨 [~] The returned pooled union buffer can remain in the process-wide array pool and influences first-use retained-memory estimates; repeated isolated runs must distinguish reusable pool capacity from live set topology.
- ⬜ [ ] Measure sort, compaction, topology-build, publication, and retirement phases separately before selecting any shared primitive for ordinary index work.

## 20. RoutedSet Baseline Evidence

The pre-fingerprint Release/x64 baseline used a distinct 16-byte routed leaf header with 510 native UInt64 equality payloads. Ordinary insertion published the appended 8-byte key plus 2-byte live count; ordinary removal published the moved/cleared key positions plus live count. Structural splits retained full-page copy-on-write publication and used one bounded 511-key sorted scratch span only to identify safe radix boundaries.

| Scenario | Engine | Insert | Inputs/s | Export | Approx retained managed bytes |
|---|---|---:|---:|---:|---:|
| 1,000,000 inputs / 999,998 distinct | `LibraDexRoutedSet<ulong>` | 3,519.661 ms | 284,118 | 9.592 ms router-major | 11,988,640 |
| 1,000,000 inputs / 999,998 distinct | `LibraDexSortedSet<ulong>` immediate | 5,238.850 ms | 190,882 | 27.850 ms ordered | 11,837,664 |
| 1,000,000 inputs / 999,998 distinct | `System.Collections.Generic.SortedSet<ulong>` | 2,954.532 ms | 338,463 | 142.121 ms ordered | 47,999,816 |
| 1,000,000 inputs / 999,998 distinct | pre-sized `HashSet<ulong>` | 192.067 ms | 5,206,511 | 78.358 ms copy plus sort | 23,270,608 |
| 1,000,000 inputs / 500,000 distinct | `LibraDexRoutedSet<ulong>` | 897.769 ms | 1,113,873 | 1.980 ms router-major | 6,620,608 |
| 1,000,000 inputs / 500,000 distinct | `LibraDexSortedSet<ulong>` immediate | 1,100.953 ms | 908,304 | 1.387 ms ordered | 6,330,512 |
| 1,000,000 inputs / 500,000 distinct | `System.Collections.Generic.SortedSet<ulong>` | 1,014.723 ms | 985,491 | 44.344 ms ordered | 24,003,496 |
| 1,000,000 inputs / 500,000 distinct | pre-sized `HashSet<ulong>` | 65.020 ms | 15,379,812 | 23.219 ms copy plus sort | 23,270,608 |

Interpretation:

- 🟩 [x] Routed end-fill removes enough sorted-leaf mutation work to beat the current LibraDex SortedSet immediate route at one million inputs under both tested distributions.
- 🟩 [x] RoutedSet approaches and, on the duplicate-heavy sample, exceeds managed SortedSet insertion while retaining about one quarter of its managed memory and exporting physical membership much faster.
- 🟩 [x] HashSet remains the immediate-insertion throughput leader; RoutedSet's first proven advantages are compact retained state, durable backing symmetry, router locality, and bounded very-fast physical traversal.
- 🟩 [x] The next-best optimization branch was a compact per-leaf equality fingerprint/filter; Sections 21 and later evidence record the measured branch selection.

## 21. RoutedSet Fingerprint Evidence

The retained routed leaf format uses a 16-byte header, 240 packed fingerprint bytes, and 480 native UInt64 equality payloads. Each key receives a four-bit SplitMix-derived rejection tag; a tag match only permits an exact full-key comparison and can never establish membership. AVX2-capable runtimes examine 32 fingerprint bytes at once and convert low/high-nibble equality into candidate bit masks. Other runtimes use the same exact persisted format with a scalar fallback. Ordinary insertion publishes one shared fingerprint byte, one 8-byte key, and the 2-byte live count; removal publishes only the affected packed fingerprint bytes, moved/cleared key positions, and live count.

The topology diagnostic runs after benchmark timing and proves root-count parity while reporting reachable routers, leaves, occupancy, arena pages, live bytes, and reusable extents. It showed that the initial one-byte fingerprint branch's 453-key capacity crossed additional radix split thresholds: 3,763 leaves at 58.7% occupancy and a 16,515,072-byte arena for 999,998 distinct keys. This was real reachable topology rather than allocator leakage; only one 4 KiB free extent remained.

| Fingerprint branch | Scenario | Insert | Inputs/s | Leaves / occupancy | Arena bytes | Approx retained managed bytes |
|---|---|---:|---:|---:|---:|---:|
| 8-bit / vector byte equality | 1,000,000 inputs / 999,998 distinct | 1,208.845 ms | 827,236 | 3,763 / 58.7% | 16,515,072 | 17,140,664 |
| 2-bit packed / scalar masks | 1,000,000 inputs / 999,998 distinct | 1,461.886 ms | 684,048 | 2,909 / 69.6% | 13,107,200 | 13,651,600 |
| 4-bit packed / AVX2 masks, repeat 1 | 1,000,000 inputs / 999,998 distinct | 1,070.861 ms | 933,828 | 3,242 / 64.3% | 14,417,920 | 14,961,288 |
| 4-bit packed / AVX2 masks, repeat 2 | 1,000,000 inputs / 999,998 distinct | 1,202.377 ms | 831,686 | 3,242 / 64.3% | 14,417,920 | 14,961,288 |
| 8-bit / vector byte equality | 1,000,000 inputs / 500,000 distinct | 894.705 ms | 1,117,687 | 1,938 / 57.0% | 9,175,040 | 9,564,944 |
| 2-bit packed / scalar masks | 1,000,000 inputs / 500,000 distinct | 1,195.708 ms | 836,324 | 1,443 / 70.1% | 7,077,888 | 7,410,032 |
| 4-bit packed / AVX2 masks, repeat 1 | 1,000,000 inputs / 500,000 distinct | 930.841 ms | 1,074,297 | 1,656 / 62.9% | 7,864,320 | 8,254,104 |
| 4-bit packed / AVX2 masks, repeat 2 | 1,000,000 inputs / 500,000 distinct | 752.348 ms | 1,329,173 | 1,656 / 62.9% | 7,864,320 | 8,254,104 |

Interpretation:

- 🟩 [x] Packed four-bit fingerprints with AVX2 candidate masks are retained internally as the measured speed/space winner. The repeated unique-heavy runs are 2.93-3.29 times faster than the 3,519.661 ms unfingerprinted baseline while retaining 12.6% more arena bytes than the sorted set, not the one-byte branch's 40.0% increase.
- 🟩 [x] The half-repeated runs straddle the 897.769 ms unfingerprinted baseline (752.348-930.841 ms), so the selected layout does not claim a deterministic duplicate-heavy speedup; it preserves comparable performance while materially reducing memory versus managed alternatives.
- 🟩 [x] The 2-bit branch minimizes fingerprint topology cost but loses materially on insertion. The 8-bit branch minimizes exact candidate comparisons but triggers excessive leaf splitting. Neither becomes a public mode or configuration choice.
- 🟨 [~] These are repeated one-process engineering samples rather than isolated-process medians. The physical choice is sufficiently separated from the unfingerprinted branch for retention, while longer distribution/cold-file measurements remain open.

## 22. Parking Gate

- 🟩 [x] Make every experimental set-facing type assembly-internal and exclude it from the public API snapshot.
- 🟩 [x] Retain implementation, file/memory lifecycle harnesses, correctness gates, and benchmark evidence for later continuation.
- 🟥 [!] The set family is incomplete and unsupported; it must not be represented as production-ready or public while parked.
- ⬜ [ ] Measure isolated-process medians and cardinality crossovers, including the small-count root-fanout and committed-space floor.
- ⬜ [ ] Finish cursor, range, set-algebra, catalog discovery, maintenance, backup, compaction, and failure-injection contracts.
- ⬜ [ ] Complete cross-shape review before expanding beyond UInt64.
- 🟩 [x] Re-evaluate Abraxas deduplication adoption from workload evidence: retain typed `HashSet<ulong>` for now and do not adopt the set merely because the prototype exists.
- ⬜ [ ] Require an explicit public-promotion decision, API review, compatibility commitment, and refreshed acceptance evidence before exposing any set type.

## 23. Typed Abraxas Dedupe Correction

The selected correction changes both the physical-to-logical transport and retained representation without changing LibraDex's logical condition model. Untyped projections keep `HashSet<object>` compatibility. Generic projections now preserve `TIdentity` through built-in primitive readers, logical composition, optional distinct state, identity ordering, and paging. Abraxas already opens UInt64 generic identity cursors, so it adopts the correction without a second mode, public option, or use of the parked set family.

The focused Release/x64 proof consumes the same lazily boxed one-million-occurrence source through both routes and samples `GC.GetTotalMemory` while the populated iterator remains alive. Values are process-level approximations, not isolated-process medians.

| Scenario | Route | Elapsed | Approx allocated | Approx live managed bytes |
|---|---:|---:|---:|---:|
| 1,000,000 inputs / 999,998 distinct | `HashSet<object>` compatibility | 144.700 ms | 77,894,024 | 51,906,152 |
| 1,000,000 inputs / 999,998 distinct | `HashSet<ulong>` Abraxas | 135.383 ms | 77,889,376 | 27,906,264 |
| 1,000,000 inputs / 500,000 distinct | `HashSet<object>` compatibility | 107.355 ms | 49,985,704 | 25,457,456 |
| 1,000,000 inputs / 500,000 distinct | `HashSet<ulong>` Abraxas | 58.538 ms | 49,985,744 | 13,457,544 |

- 🟩 [x] Sparse duplicates preserve 999,998 first appearances and reduce approximate live retained memory by 23,999,888 bytes, or 1.86x.
- 🟩 [x] Fifty-percent repeats preserve 500,000 first appearances and reduce approximate live retained memory by 11,999,912 bytes, or 1.89x.
- 🟩 [x] Typed `ToList<TIdentity>()` now materializes directly from the typed iterator instead of retaining an object result list and copying it to a second typed array.
- 🟩 [x] Focused semantics prove distinct, preserve, first-appearance order, dedupe-before-paging, and unchanged untyped compatibility.
- 🟩 [x] Remove transient primitive-source boxing from built-in scalar identity executors by adding an internal typed executor contract and typed logical composition. Exact/projected string, folded/no-case, sort-key, generic scalar/date/numeric, BigInteger scalar, and variable-blob scalar identities remain typed through projection. Runtime object APIs and explicit external-object callbacks retain compatibility adapters.
- 🟩 [x] Require the million-row A/B to improve total allocation as well as live retention: repeated runs show that the typed route removes approximately 24.0 MB of total managed allocation in both the two-duplicate and half-repeated scenarios while preserving all result semantics.
