# VS8 Scoop Range Count Design

## Status

- 🟩 [x] Preserve the LibraDex router/shelf architecture.
- 🟩 [x] Diagnose the existing reader-backed cost and failed route-containment counter.
- 🟩 [x] Select ordered shelf scooping with exact boundary-slot counts and interior shelf live counts.
- 🟩 [x] Compare core-compatible alternatives and record LXL proofs.
- 🟩 [x] Add a read-only topology/invariant proof and clear it after repairing expanded logical prefix-range transforms.
- 🟩 [x] Approve production implementation.
- 🟩 [x] Implement the production count-only scoop and prove parity before timing.
- 🟩 [x] Replace unsafe live compressed transforms with canonical expanded exact-stem chains and lazily publish previously unset sibling routes.
- 🟩 [x] Repeat the original isolated ShapeBench row in three LibraDex and three SQLite processes.

The production `VS8` count path now uses the shelf scoop. The focused raw fixture proves full-range plus `128` sampled range counts live and after reopen, including terminal duplicates. The public encoded-key ShapeBench fixture proves exact `ResultItems=799,916` parity and a disjoint persisted topology.

## Core Model

LibraDex range retrieval is modeled after finding the first and last books in a physical library. Once the lower and upper extents are located, the ordered entries between them can be consumed without comparing every entry again. A count is the aggregate form of that same scoop:

1. Find the lower shelf and its first matching slot.
2. Find the upper shelf and its exclusive ending slot.
3. Count the boundary shelf slot spans exactly.
4. Add the persisted live count of every distinct shelf logically between the boundaries.
5. Do not decode keys, identities, or slot arrays for an interior shelf.

The algorithm must follow logical route order. Physical offset adjacency is an optional read-coalescing opportunity, not the definition of shelf order.

## Current Evidence

The ShapeBench `count-range-api` workload performs up to `25,000` inclusive range counts. Each range covers up to `32` generated key ordinals. VS8 calls `VarKeyScalar8Index.CountIdentityRange(lower, upper)` and SQLite executes `COUNT(*)` over the same inclusive key bounds. `ResultItems` is the sum of returned counts and must match before timing is accepted.

Current VS8 ordinary shelves already provide the required local facts:

- the persisted header stores `ItemCount`;
- published shelf slots contain only live tuples because deletes are normalized for publication;
- slots are ordered by encoded key and identity;
- `LowerBoundKey` and `UpperBoundKey` identify exact boundary slots;
- duplicate-run shelves store their item count and next-shelf offset in the header;
- terminal identity shelves store item count and continuation metadata.

The ordinary range reader is intentionally general. It reads and decodes each selected VS8 shelf into `recordOffsets` and `keyPrefixes`, computes a matching slot range, retains the shelf, and accumulates row count. That is appropriate for later cursor materialization, but excessive for a count-only operation.

The failed metadata counter did use narrow header counts, but selected them using route containment. Its critical shortcut was effectively: when a route is neither the lower nor upper edge, count its entire target. Shared targets invalidate that inference because one target can be published through several route intervals. The experiment therefore produced both overcounts and undercounts after transforms.

The failure does not invalidate shelf counts or the router/shelf architecture. It proves only that route containment is not equivalent to being logically between the located boundary shelves.

## Structural Invariants Required By Scooping

1. **Shelf interval:** every ordinary shelf contains one contiguous interval of the global encoded tuple order.
2. **Split preservation:** transforming a shelf partitions that ordered interval into non-overlapping left and right shelf intervals.
3. **Alias contiguity:** repeated route references to one target represent one contiguous logical run; aliases do not place the same shelf on both sides of an unrelated shelf interval.
4. **Route order:** ascending router traversal emits shelf intervals in ascending encoded-key order.
5. **Distinct emission:** one physical shelf or terminal root contributes at most once to a scoop, regardless of repeated route references.
6. **Boundary ownership:** lower and upper route walks locate the shelf intervals that would contain the bounds, even when the exact bound key is absent.
7. **Published live count:** an ordinary shelf header `ItemCount` equals its published live slot count.
8. **Duplicate extent:** every duplicate-run or terminal identity chain belongs to exactly one encoded key and is counted wholly when that key is in range.
9. **Snapshot consistency:** boundary reads, intermediate header counts, and route traversal observe one storage mutation version or retry.

The first inspector run disproved the earlier induction for repeated transforms. A split does build sorted left/right shelves, but the old shelf can be referenced by a range of prefix slots. Transforming that shared shelf in place advances the child router to the next key byte as though the parent route consumed one exact byte value. The shared shelf still contains several values at the parent byte, so the new router partitions on a later byte and its two shelf intervals overlap in full encoded-key order.

Point lookup remains valid because each hop reuses the complete incoming key. Ordered shelf scooping is not valid under this topology because no route traversal order can make overlapping shelf intervals disjoint.

## Inspector Proof Result

The read-only `vs8-scoop-topology-proof` command now records logical extent kind, physical shelf count, item count, first/last key and identity, route aliases, parent ownership, cycles, interval overlap, mutation version, and a framed SHA-256 tuple-sequence comparison with the ordinary range reader.

- 🟩 [x] `16,384` ordinary rows across `16` root shelves passed live and reopen: `16` extents, `0` aliases, `0` multiple parents, `0` cycles, `0` overlaps, and `16,384/16,384` tuple parity.
- 🟩 [x] `4,096` identical-key rows passed live and reopen as one terminal-identity logical extent with exact count and hash parity.
- 🟩 [x] Existing `vs8-routed-sanity` and `vs8-mb-router-transform-sanity` remain green.
- 🟥 [!] The initial `65,536` rows with `8` root prefixes proof exposed `16` overlapping adjacent extent intervals despite `65,536/65,536` count and reader-hash parity. One concrete inversion ended at `00 7D...` and the next extent began at `00 00...`.
- 🟥 [!] Reader-hash parity is not an independent ordering proof here: the ordinary range reader follows the same route graph and therefore reproduces the same inverted extent sequence.
- 🟩 [x] The accepted repair keeps the expanded direct router as the physical representation while treating each contiguous same-target run as one logical prefix range. A split at the range's still-varying parent byte atomically republishes the whole run directly to the left/right shelves; a deeper split begins its replacement router at that same parent depth. Singleton targets retain the normal next-depth transform.
- 🟩 [x] The repaired `65,536 x prefix8` proof passes live/reopen with `9` routers, `32` ordinary extents, `0` aliases, `0` multiple parents, `0` cycles, `0` overlaps, and exact `65,536/65,536` tuple parity.
- 🟩 [x] The widened `250,000 x prefix64` proof passes live/reopen with `65` routers, `128` ordinary extents, no topology violations, exact tuple parity, `192` growths, and `64` transforms.
- 🟩 [x] Existing `vs8-routed-sanity` and `vs8-mb-router-transform-sanity` remain green after the repair.
- 🟥 [!] The public encoded-key fixture then exposed a second transform flaw at the `16,000`-row threshold: a persisted compressed router stem was exact only for the stem present when it was created, while later stem rollover was accepted by nearest-route fallback and folded into the old child topology.
- 🟩 [x] Live `VS8` transforms now persist expanded one-byte exact-stem chains. Each intermediate router publishes only its exact stem byte; sibling prefix bytes remain unset until their first insert atomically publishes an independent shelf. This preserves one disjoint shelf interval per logical prefix path without moving away from the router/shelf model.
- 🟩 [x] The final public `250,000`-row fixture has `35` routers, `152` extents, `0` aliases, `0` multiple parents, `0` cycles, `0` overlaps, exact route/reader count `250,000`, and ordered sequence parity.
- 🟩 [x] The final historical transform sanity command validates the exact-stem expanded chain after reopen; its legacy command name is retained for harness compatibility.

The topology gate is now clear for the count-only scoop.

The next-best branch remains dynamically growable compressed persisted stems plus a promoted direct view. It could shorten shared-prefix walks, but it must support later exact stem insertion/range growth rather than nearest-route fallback. The current fixed `4 KiB` router page also means compression does not reduce page storage today.

## Selected Algorithm

### Phase 1: Locate Boundaries

Walk the lower and upper keys through the cached router path. Share their common router prefix so the common path is read once. Capture for each boundary:

- target kind and offset;
- parent/router context needed to anchor ordered traversal;
- lower slot or upper exclusive slot for an ordinary shelf;
- terminal key qualification for a terminal identity root.

If both bounds resolve to the same ordinary shelf, read/decode that shelf once and return `UpperBoundKey(upper) - LowerBoundKey(lower)`. If they resolve to the same duplicate-key extent, return the whole chain count only when its key is in range.

### Phase 2: Count Boundary Shelves

For distinct ordinary boundary shelves:

- lower contribution is `LiveItemCount - LowerBoundKey(lower)`;
- upper contribution is `UpperBoundKey(upper)`;
- if the lower slot is zero or upper slot equals live count, use the header count for the covered full side;
- if either boundary contributes zero, retain the boundary anchor but add nothing.

Published ordinary shelves have no tombstone holes. A batch-local mutable shelf must use `CountLiveItemsInKeyRange` or normalize its deleted-slot sidecar before applying ordinal arithmetic.

### Phase 3: Scoop Intermediate Shelves

Traverse routes in ascending logical order from the lower boundary anchor to the upper boundary anchor. For each distinct target strictly between them:

- ordinary shelf: read and validate only the fixed header, then add `ItemCount`;
- duplicate-run head: add each distinct linked shelf header count;
- terminal identity root: verify its single key is in the ordered interval, then add its identity shelf header counts;
- router: descend in ascending route order;
- repeated alias: skip without changing boundary state.

Use pooled/stack traversal state and the existing router arena cache. Coalesce physically adjacent shelf extents when profitable, exactly as the existing fixed-shape scoop does, while preserving route order across router branches.

### Phase 4: Validate Snapshot

Capture the DataKernel mutation version before boundary discovery. If publication changes that version before the count completes, retry the count. This prevents mixing an old boundary shelf with new interior counts after a split or growth publication.

## BLX Comparison

| Branch | Read cost | Write cost | Fit with LibraDex | Decision |
|---|---:|---:|---|---|
| Ordered boundary-to-boundary shelf scoop | Boundary decode plus one narrow header per interior shelf | None beyond existing shelf count maintenance | Direct expression of the core library/extent model | 🟩 [x] Select |
| Add next/previous links to ordinary shelves | Direct continuation after boundary seek | Split/growth must patch neighboring shelves; more publication contention | Useful fallback, but duplicates ordering already represented by routers | 🟨 [~] Next-best only if route-order traversal is too costly |
| Persist per-route-edge counts | Fast contained-route sums | Every insert/delete may update several aliased edges | Makes alias fanout a write-amplification liability | 🟥 [!] Reject |
| Persist router subtree counts | Logarithmic aggregate | Updates every ancestor and adds hot shared writes | Replaces scooping with a tree aggregate model | 🟥 [!] Reject for this problem |
| Add shelf min/max summaries | Helps classify a shelf | Variable-key boundary storage and maintenance | Does not provide the ordered sequence by itself | 🟥 [!] Reject as primary solution |
| Keep reader-backed count | Correct | None | Retains full shelf decode and reader state | 🟨 [~] Correctness oracle only |
| Replace VS8 with a B+ tree | Can count with subtree metadata | Reworks proven read/write topology | Discards core advantages to solve one aggregate path | 🟥 [!] Reject |

## LXL Proofs

### 1. Same Shelf

Shelf `A` contains ordered keys `10, 20, 30, 40`, live count `4`. Count `[20, 30]`:

- `LowerBoundKey(20) = 1`;
- `UpperBoundKey(30) = 3`;
- both bounds resolve to `A`;
- result is `3 - 1 = 2`;
- no route enumeration or second shelf read occurs.

### 2. Partial Boundaries With One Interior Shelf

`A = [10,20,30]`, `B = [40,50,60]`, `C = [70,80]`. Count `[25,75]`:

- lower boundary `A`: lower slot `2`, contribution `3 - 2 = 1` (`30`);
- interior `B`: add header live count `3` without slot decode;
- upper boundary `C`: upper slot `1`, contribution `1` (`70`);
- total `1 + 3 + 1 = 5`.

### 3. Shelf-Aligned Boundaries

Using the same shelves, count `[10,80]`:

- lower boundary begins at slot `0`, so use all of `A`'s live count `3`;
- add interior `B` live count `3`;
- upper boundary ends at `C.LiveItemCount`, so use all of `C`'s live count `2`;
- total `8`.

### 4. Aliased Transform

A router maps prefixes below stem `40` to shelf `L`, exact stem `40` to child router `R`, and prefixes above `40` to shelf `H`. Repeated prefix routes to `L` or `H` are aliases, not repeated shelf positions.

For a range starting inside `L` and ending inside `H`:

- count the lower tail of `L` once;
- descend `R` and add each complete shelf interval in its route order;
- count the upper head of `H` once;
- visited-target state suppresses repeated aliases without treating an alias interval as proof of shelf containment.

### 5. Absent Boundary Keys

`A = [10,20,30]`, `B = [40,50]`. Count `[31,39]`:

- lower routing anchors at the interval after `A`; `A` contributes zero;
- upper routing anchors before `B`; `B` contributes zero;
- there is no intermediate shelf interval;
- result is zero without requiring either bound to exist.

### 6. Duplicate Terminal Key

Terminal key `42` owns three identity shelves with header counts `900`, `900`, and `250`. Count `[42,42]`:

- terminal key comparison succeeds;
- add the chain header counts `900 + 900 + 250 = 2,050`;
- identities are not read;
- count `[43,50]` skips the terminal extent completely.

### 7. Concurrent Split Retry

The count locates lower shelf `A` at mutation version `100`. A writer replaces `A` with a router and two shelves, publishing version `101`, before the count reaches its upper boundary.

- final mutation-version validation fails;
- partial total is discarded;
- retry locates both boundaries and traverses the version `101` topology;
- the result never mixes old shelf `A` with its replacements.

## Expected Performance Shape

For the ShapeBench 32-key ranges, most operations should touch one boundary shelf or two boundary shelves with few or no intermediate shelves. The intended hot path therefore becomes:

- cached router reads;
- one or two full shelf reads/slot-bound searches;
- zero or a small number of 32-byte interior header reads;
- no identity copying;
- no decoded sidecars for interior shelves;
- no per-operation retained range-reader shelf arrays.

Wide ranges remain proportional to the number of shelves scooped, which is the intended LibraDex model. Physically contiguous intermediate extents may be read in coalesced runs; noncontiguous extents remain header-only reads.

## Required Proof Gates

### Structural

- 🟩 [x] Add a read-only inspector that enumerates VS8 targets in route order and records each distinct shelf's first/last tuple.
- 🟨 [~] Prove shelf intervals are monotonic and non-overlapping: growth, expanded exact-stem router-chain transform, terminal conversion, and reopen are green; delete, rekey, optimizer publication, and writer-context publication remain broader follow-up coverage.
- 🟩 [x] Prove the accepted exact-stem topology has no repeated target aliases in the focused raw and public ShapeBench fixtures.
- 🟩 [x] Prove route-order enumeration matches full range-reader tuple order in the focused `65,536`, `250,000`, terminal, and public encoded-key fixtures.

### Correctness

- 🟩 [x] Compare the scoop against the reader oracle for full plus `128` sampled ranges live/reopen, and against SQLite through the ShapeBench `ResultItems` guard.
- 🟨 [~] Cover same-shelf, adjacent/many-shelf, empty/absent boundaries, exact boundaries, duplicate terminal roots, long keys, and reopen; retain exhaustive prefix-of-another and maximum-length-key permutations as follow-up coverage.
- 🟩 [x] Probe the public encoded fixture across `1,000`, `2,000`, `4,000`, `8,000`, `16,000`, `32,000`, `64,000`, and `250,000` rows while isolating the transform rollover.
- 🟨 [~] Run sampled ranges after the accepted growth/transform/terminal structural classes; broader mutation-class randomization remains open.
- ⬜ [ ] Force concurrent publication between boundary discovery and completion and prove version retry.

### Performance

- 🟩 [x] Use isolated serial ShapeBench scenarios with the hard `ResultItems` parity guard.
- 🟩 [x] Repeat `vs8 count-range-api` at batch `5000`, thread `1`, with three measured processes per engine.
- ⬜ [ ] Record router pages, full boundary shelf reads, interior header reads, decoded slots, allocated bytes, and retries per operation.
- ⬜ [ ] Require zero interior slot decoding and no result-cardinality-proportional allocation.
- ⬜ [ ] Compare one-pass edge-state traversal against shared-prefix dual-boundary discovery; retain the faster path only after both pass the structural inspector.
- ⬜ [ ] Confirm no write-throughput or committed-byte regression, since the selected design requires no new persisted write metadata.

## Implementation Boundary

The implemented traversal merges physical router entries by target before descent, carries lower/upper edge state, counts boundary shelves by exact key slots, counts contained shelves from persisted live metadata, follows duplicate/terminal chains once, and retries when `DataKernel.MutationVersion` changes. It uses pooled visited-target/router state and does not materialize identities. The reader-backed path remains the correctness oracle in the focused harness.

## Final ShapeBench Result

The original row was LibraDex `347 ops/s`, SQLite `17,699 ops/s`, ratio `0.02x`. The accepted build produced three isolated LibraDex processes at `26,056.554`, `33,968.264`, and `24,977.049 ops/s`; median `26,056.554`. Three fresh SQLite processes produced `5,547.233`, `6,138.714`, and `10,017.636 ops/s`; median `6,138.714`. Every process returned `ResultItems=799,916`.

- 🟩 [x] Median-to-median result: LibraDex is `4.245x` SQLite on the current isolated comparison.
- 🟩 [x] Against the original faster SQLite baseline of `17,699 ops/s`, the current LibraDex median is still approximately `1.47x`.
- 🟩 [x] The performance failure is fixed without adding persisted subtree counts or write-side count maintenance.

## VS16 And VV Adoption

The same boundary-to-boundary scoop is now production-connected for `VS16` and `VV`. Both shapes use pooled route-target merging, same-extent dual-boundary discovery, exact boundary searches, contained shelf metadata, and mutation-version retry. `VV` additionally counts contained terminal variable-identity roots from their persisted chain metadata and reuses the session's decoded key-slot sidecar for boundary binary searches.

The adoption exposed both VS8 topology lessons independently:

- fixed compressed transform stems were unsafe when later key groups rolled beyond the originally persisted stem;
- immediate split shelves shared across a parent prefix run could not later advance past that still-varying parent byte.

Live `VS16` and `VV` transforms now use expanded exact-stem chains with lazy independent sibling shelves. When a shared parent run splits at its owning byte, publication rewrites the left shelf in place, appends the right shelf, revalidates the complete parent run, and republishes every slot in that run under the serialized/exclusive publication seam.

Three isolated accepted LibraDex processes returned `ResultItems=799,916` for every row:

| Shape | LibraDex ops/s | LibraDex median | SQLite median | Ratio |
|---|---|---:|---:|---:|
| VS16 | `41,650.277`, `40,904.212`, `41,090.975` | `41,090.975` | `14,221.882` | `2.889x` |
| VV | `88,342.875`, `83,200.241`, `96,436.665` | `88,342.875` | `14,860.940` | `5.945x` |

Focused `vs16-routed-sanity`, `vv-routed-sanity`, `varlen-count-all-sanity`, and compressed compatibility `varlen-mb-range-count-sanity` are green after the final repair.

## Fixed-Length-Key Applicability

The principal lessons apply, but the implementation work is different:

- 🟩 [x] Fixed-N range counters already implement the scoop shape: exact boundary shelf scans, contained shelf header counts, visited-target protection, and reader-like fallback when a boundary target is aliased.
- 🟩 [x] Fixed key depth eliminates variable-length sentinel/stem rollover, so the compressed variable-key repair should not be copied mechanically.
- 🟨 [~] The disjoint ordered-interval invariant still applies. Several fixed-key transform builders intentionally route below-stem prefixes to a left shelf and above-stem prefixes to a right shelf. They require a route-order topology proof before treating every interior alias as safely scoopable across repeated transforms.
- 🟥 [!] Do not change fixed-key production routing merely because the variable-key families needed exact-stem repair. First reproduce an overlap or prove the existing fixed-depth transform publication preserves parent-byte ownership.

The next fixed-key action is therefore a shape-generic read-only topology inspector/proof covering the SS, FS32, and Fixed-N families under repeated transforms, reopen, delete, and rekey. If it exposes the same shared parent-run advance, reuse the parent-run republish principle; otherwise retain their current denser routing.
