# LibraDex SS8 Backfill Correctness And Performance Checklist

## Purpose

This is the canonical workstream ledger for correcting SS8-8 mixed-key ordering, eliminating duplicate-terminal write amplification, improving Abraxas index backfill, and reclaiming already bloated LibraDex files safely.

The priority order is:

1. Correctness and durability are release gates.
2. Speed is the primary optimization goal.
3. Efficiency is the secondary optimization goal, including CPU, allocation, write volume, locality, memory bounds, and persistent file size.
4. Concurrency is a later multiplier, not a substitute for an efficient serial core.

Update this document before beginning a phase, after every meaningful proof, after every stopped or rejected branch, and before ending a work session. Keep detailed commands, measurements, failures, and restart instructions here rather than relying on conversation history.

## Status Legend

- 🟩 [x] Done or accepted.
- 🟨 [~] Partial, in progress, or a parent with mixed child states.
- 🟥 [!] Failed, rejected, blocked, or a discovered defect.
- ⬜ [ ] Open or not started.

## Worktree And Safety Rules

- 🟩 [x] Treat `C:\VSProjects` as a junction to canonical `E:\VSProjects`; use `E:\VSProjects` in commands and evidence.
- 🟩 [x] Both `AbraxasDB` and `LibraDex` have broad user-owned dirty trees. Never reset, revert, clean, or overwrite unrelated changes.
- ⬜ [ ] Before editing a dirty file, inspect its current diff and preserve existing work.
- ⬜ [ ] Keep source edits narrowly attributable to a checklist item.
- ⬜ [ ] Before every C#/project/config build, copy all changed source/config/project files into the repository's `.code-history` tree with timestamp and solution-relative path.
- ⬜ [ ] Prune `.code-history` entries older than four hours relative to its newest entry.
- ⬜ [ ] For `Rebuild`, delete only the targeted projects' `bin` and `obj` directories after the history snapshot and before MSBuild.
- ⬜ [ ] Use Release/x64, `/m:1`, minimal console output, and a normal-verbosity file log.
- ⬜ [ ] Put large generated databases under a unique `%TEMP%` directory when possible.
- ⬜ [ ] Retain only compact reports and the minimum failed fixture needed for diagnosis.
- ⬜ [ ] Before closing the workstream, remove generated payloads so repository `artifacts` do not retain more than 1 GB without explicit approval.

## Established Baseline

- 🟩 [x] Wherzit live LibraDex file observed at `4,171,728,001` bytes; a clean 50,000-record lifecycle produced `31,624,167` bytes.
- 🟩 [x] A fresh 49,057-row `CreationTime` backfill produced `1,108,080,001` bytes in the optimized path and `1,104,835,969` bytes in the legacy path.
- 🟩 [x] One 15,011-row equal-key run with ascending identities produced approximately `443 KB` in `315 ms`.
- 🟩 [x] The same run with descending identities produced `1,103,741,259` bytes in `3.077 s`.
- 🟩 [x] Explicit batching of that descending run still produced `1,103,741,259` bytes; batching alone does not fix physical amplification.
- 🟩 [x] The descending one-key file compacted to approximately `443 KB`, proving its extra bytes were unreachable generations rather than required live topology.
- 🟩 [x] Wherzit `CreationTime` contained a maximum equal-key run of `15,011`; `LastWriteTime` contained a maximum run of `7,978` plus several other multi-thousand runs.
- 🟩 [x] Optimized Abraxas backfill sorts scalar tuples by key only and reports zero published batches.
- 🟩 [x] Ordinary Abraxas mutation materializes one `InhetoBinary` and reuses it across persistence and index work.
- 🟩 [x] Deferred index backfill currently rematerializes records independently for each index.
- 🟥 [!] Exact mixed-key Wherzit `CreationTime` traversal contained 39 global tuple inversions.
- 🟥 [!] A clean 50,000-row lifecycle contained 19 `.Length` inversions.
- 🟥 [!] Compaction parity failed for the clean mixed-key lifecycle, the exact mixed-date reproduction, and the copied live Wherzit catalog.
- 🟩 [x] Pure one-key compaction succeeded, so the mixed-key ordering defect is distinct from duplicate-chain amplification.

## Root-Cause Contracts To Preserve

### Duplicate Terminal Mutation

- 🟩 [x] The current tail fast path accepts only an identity greater than the terminal tail.
- 🟩 [x] An out-of-order identity reads the whole terminal route, inserts into a complete identity array, allocates an entirely new shelf chain, swaps the terminal root, and attempts to release the old chain.
- 🟩 [x] `DataKernel.ReleaseMemoryExtent` reclaims memory-backed extents only; it cannot reclaim file-backed middle extents.
- 🟩 [x] The observed 15,011-row amplification is quantitatively explained by 13,191 full-chain rewrites.

### Mixed-Key Router Ordering

- 🟨 [~] The strongest current source-level cause is the histogram-based SS8 split-prefix chooser selecting a later byte after keys have already diverged at an earlier byte.
- ⬜ [ ] Convert that hypothesis into a focused failing proof before changing the splitter.
- ⬜ [ ] A router at depth `d` must own only tuples sharing the exact prefix `[0..d-1]`.
- ⬜ [ ] A split may partition only at the first byte where the route's minimum and maximum full keys differ.
- ⬜ [ ] If the first differing byte precedes the proposed child depth, repair or repoint the owning ancestor; never partition on a later non-monotonic byte.
- ⬜ [ ] Before publication, prove tuple conservation, shelf-local ordering, strict left/right boundary ordering, router-prefix ownership, and zero global traversal inversions.

## Phase P0 — Restore SS8 Global Ordering

### P0.1 Capture The Failure As A Deterministic Harness Proof

- 🟩 [x] Added `ss8-8-mixed-prefix-ordering-sanity`, which creates a fresh full SS8-8 shelf whose earlier full-key order conflicts with its later-byte histogram order.
- 🟩 [x] The contrived vector has `0x00F0...` keys followed by `0x0110...` keys; partitioning at byte depth 1 places the later full keys before the earlier full keys.
- 🟩 [x] The prior deterministic 50,000-row clean lifecycle reproduced 19 `.Length` inversions; retain a current-tree stress rerun as part of P0.3.
- 🟩 [x] The command enumerates every tuple and records the first `(key, identity)` inversion with ordinal, prior tuple, and current tuple.
- 🟩 [x] The command verifies exact tuple count and exact expected tuple-set parity independently from ordering.
- 🟩 [x] The command reruns identical checks after reopen.
- 🟩 [x] Prior clean mixed-key compaction failed structural parity; require a current-tree successful compaction rerun under P0.3 after the ordering fix.
- 🟩 [x] Pre-fix command and result recorded in the results ledger below.

### P0.2 Design The Prefix-Safe Split Plan

- 🟩 [x] Traced the SS8-8 queued root transform, queued/walked parent-route split, direct walked transform, and shared SS8-16 transform callers.
- 🟨 [~] The split-plan now derives the first differing full-key byte and a real adjacent boundary; explicit ancestor-repair disposition remains open for already-persisted invalid topologies.
- 🟩 [x] Reject an empty left or right partition.
- 🟩 [x] The selected boundary is an adjacent transition in a globally sorted key array, which proves `max(left full key) < min(right full key)`.
- 🟩 [x] Reject child publication unless all tuples match the target child's complete prefix stem.
- 🟨 [~] Root aliases are cleared during safe root transformation. Existing deeper aliases require either explicit topology repair or rebuild/compaction recovery; do not silently reinterpret them.
- ⬜ [ ] LXL the plan for:
  - ⬜ [ ] All keys equal.
  - ⬜ [ ] First-byte divergence.
  - ⬜ [ ] Deep common prefix followed by one differing byte.
  - ⬜ [ ] Earlier-byte divergence followed by later-byte convergence.
  - ⬜ [ ] Two-key boundary with highly skewed counts.
  - ⬜ [ ] Existing shelf/terminal mismatch and stale alias cases.

### P0.3 Implement And Prove

- 🟨 [~] Implemented first-difference split selection, routed-prefix validation, and root alias clearing with detailed XML comments; exact-stem intermediate routing is the active correction for the newly exposed deeper-alias defect.
- ⬜ [ ] Preserve current tuple encodings and public APIs.
- ⬜ [ ] Keep publication atomic and version checked.
- ⬜ [ ] Make cache mutation occur only after durable publication succeeds.
- 🟩 [x] Run the minimal contrived proof and require zero inversions.
- 🟩 [x] The first current-tree 50,000-record stress attempt exposed a depth-6 shelf containing keys first diverging at depth 4; exact-stem intermediate routing corrected that deeper-alias defect, and the rerun completed all five indexes.
- 🟩 [x] Focused and 50,000-record reopen parity pass.
- 🟩 [x] Run compaction parity.
- 🟩 [x] Existing SS8 routing, duplicate-run, batching, maintenance, shape-adjacent, and concurrency regressions pass.
- 🟩 [x] Record post-fix file bytes, elapsed time, tuple count, and inversion count below.

### P0 Acceptance Gate

- 🟩 [x] No tuple loss, duplication, or inversion in focused or stress fixtures.
- 🟩 [x] Compaction parity succeeds for the clean mixed-key fixture.
- 🟩 [x] Existing routing and mutation proofs pass.
- 🟩 [x] No regression was accepted merely because Wherzit appeared usable; focused, stress, reopen, compaction, concurrency, and A/B lifecycle gates all passed.

## Phase P1 — Make Duplicate-Terminal Mutation Local

### P1.1 Required Mutation Behavior

- 🟩 [x] Walk the terminal chain while retaining the current shelf, predecessor, successor, count, and key boundary needed for one atomic local mutation.
- 🟩 [x] Preserve the existing greater-than-tail append fast path.
- 🟩 [x] If the target shelf has capacity, insert/shift in one shelf image and rewrite that shelf positionally.
- 🟩 [x] If the new identity is below a full head, prepend one fillable shelf and update only the terminal root/head link.
- 🟩 [x] If a full interior shelf must split, rewrite that shelf plus one new shelf and atomically update the relevant link.
- 🟩 [x] Do not reconstruct or append unaffected terminal shelves in the new direct SS8 terminal path.
- 🟩 [x] Update terminal root/shelf caches only after commit succeeds in the new local path.
- 🟩 [x] Preserve duplicate-policy and exact-tuple semantics; alternating/shuffled fixtures include an exact-tuple no-op with zero file growth.
- 🟩 [x] Define and prove durability-batch abort behavior for an existing terminal route: staged storage is discarded and terminal byte caches are invalidated before the session becomes readable again.

### P1.2 Required Delete Behavior

- 🟩 [x] Delete an identity by rewriting only its containing shelf.
- 🟩 [x] Unlink an empty shelf without rebuilding the remaining chain.
- 🟩 [x] Delete an entire logical key by clearing its terminal root and making the old chain unreachable without a replacement-chain build.
- 🟩 [x] Evaluated eager shelf merging and deferred it: file-backed merging cannot reclaim middle extents, adds neighbor/link writes to the critical delete path, and has no measured speed or physical-size benefit before compaction.

### P1.3 LXL Matrix

- 🟩 [x] Ascending identities into one key.
- 🟩 [x] Descending identities into one key.
- 🟩 [x] Alternating low/high identities.
- 🟩 [x] Random identities with a fixed seed.
- 🟩 [x] Insert into first, middle, and last shelf with spare capacity; each directed fixture preserves the exact three-shelf offset chain and stable file length.
- 🟩 [x] Split first, middle, and last full shelf; each directed fixture grows from three to four shelves and from `409,600` to `442,368` bytes exactly.
- 🟩 [x] Duplicate exact tuple rejected/accepted according to policy, with zero physical growth for the accepted no-op.
- 🟩 [x] Delete first, middle, and last identity, validate the missing ordinal live and after reopen, then reinsert and validate again.
- 🟩 [x] Delete the last identity in a shelf; the head-shelf fixture deletes every head identity and verifies unlink only after the final identity.
- 🟩 [x] Delete the last identity for a key and require the terminal root's first-shelf link to become zero.
- 🟩 [x] Reopen after every directed spare-insert, delete, split, and final-identity case.

### P1.4 Performance And Size Gates

- 🟩 [x] Re-run the 15,011-row ascending control.
- 🟩 [x] Re-run the 15,011-row descending control.
- 🟩 [x] Require descending persistent bytes to remain within a small constant factor of ascending live topology, not gigabytes; ascending and descending are both `442,368` bytes.
- 🟩 [x] Require appended bytes to grow approximately linearly with shelf capacity; ascending, descending, and shuffled are identical while the adversarial alternating pattern uses only three additional 32-KiB extents.
- 🟩 [x] Compare items/sec, bytes/item, write calls/item, commit calls, staged extents, bytes written, and thread allocations in the locality command.
- 🟩 [x] Re-run the exact Wherzit `CreationTime` and `LastWriteTime` distributions through the 50,000-record lifecycle fixture.
- 🟩 [x] Require clean builds to remain near the tens-of-megabytes lifecycle scale unless reachable-topology accounting proves otherwise; the current lifecycle is `32,213,991` bytes before and `31,105,129` bytes after compaction.

## Phase P2 — Improve Abraxas Deferred Backfill

### P2.1 Immediate Backfill Corrections

- 🟩 [x] Sort scalar tuples by `(encoded key, record identity)`, not key alone.
- 🟩 [x] Apply the same deterministic tie-break contract to variable/binary keys.
- 🟩 [x] Document that bounded chunks do not guarantee global identity order across chunks; P1 remains the correctness/performance foundation.
- 🟩 [x] Use an actual LibraDex durability/identity-group batch to reduce publication and flush overhead.
- 🟩 [x] Preserve uniqueness detection and deterministic error reporting.

### P2.2 Detached Replacement Lifecycle

- 🟨 [~] Extract and validate source records before invalidating a live index. The grouped one-Inheto feed validates scalar source/order/uniqueness before native topology allocation; existing-live directed proof remains open.
- 🟩 [x] Build every replacement root and child graph at new unreachable offsets rather than rewriting a live root during preparation.
- 🟨 [~] Validate detached count, tuple order, uniqueness, direct detached-root traversal, and reopen behavior before publication. Production preparation performs complete source-contract validation plus detached structural count validation; the directed proof establishes exact post-publication/reopen order from the same topology, while a bounded pre-publication tuple-order walker remains optional follow-up.
- 🟩 [x] Redirect every selected fixed-directory slot to its validated replacement root in one complete 8 KiB directory rewrite, incrementing each selected slot generation together.
- 🟩 [x] Keep the prior directory snapshot and root generations authoritative until the one directory publication succeeds.
- 🟨 [~] Define cancellation, process failure, sibling failure, old-handle reader visibility, cache invalidation, and compaction reclamation without exposing a partially rebuilt index. All in-process/exception paths are proved; sudden power-loss/torn-directory recovery requires a later on-disk format or journal slice.
- 🟩 [x] BLX Branch A — rewrite each existing stable root serially: rejected for grouped replacement because a later sibling failure or process stop can leave a mixed old/new generation set.
- 🟩 [x] BLX Branch B — create hidden logical index names and rename/swap catalog metadata: rejected as the primary scalar path because it consumes scarce fixed directory slots, duplicates metadata/name lifecycle, and adds alias cleanup without improving the underlying root-generation boundary.
- 🟩 [x] BLX Branch C — build new detached roots and atomically replace selected `RootRouterOffset` values in the existing fixed-directory slots: selected. It keeps logical names, metadata offsets, and slot identities stable while one directory image publishes all sibling root generations together.
- 🟩 [x] BLX Branch D — shadow and take over the complete catalog file: retained as the next-best cross-shape fallback because it naturally handles text and future shapes, but deferred from the scalar fast path due to whole-file copy/ownership cost and Windows file-takeover complexity.
- 🟩 [x] LXL visibility model: before directory commit every lookup resolves the prior slot/root; detached allocations are unreachable. After commit and cache replacement, new opens resolve every replacement root together. Handles created before publication retain the prior root offset and may finish as generation snapshots.
- 🟩 [x] LXL failure model: source/build/validation/cancellation before directory publication leaves old slots unchanged and leaks only unreachable append extents; a failed directory commit leaves the in-memory directory cache unchanged. Old and failed detached generations are reclaimable by compaction.
- 🟥 [!] A plain multi-offset DataKernel root rewrite is not accepted as sibling-atomic or crash-atomic: file-backed commit issues positional writes and can be interrupted between root pages. The fixed directory supplies the single inventory publication image needed by this slice.
- 🟥 [!] Do not describe the current 8 KiB in-place directory rewrite as power-loss atomic. It is one logical DataKernel publication and one backing write in the normal path, but the current format has no second checksummed directory generation from which reopen can recover a torn physical write.

### P2.3 One Record Materialization Across All Indexes

- 🟩 [x] Enumerate authoritative Fractal records once for a grouped backfill request.
- 🟩 [x] Hold the live Fractal payload and one Inheto view only while extracting all requested index keys.
- 🟩 [x] Enqueue compact typed `(key, identity)` tuples into bounded per-index queues.
- 🟩 [x] Release the record payload and Inheto immediately after all requested keys are extracted.
- 🟩 [x] Never retain every payload or Inheto in RAM in the default mode.
- 🟩 [x] Make memory limits explicit and measurable.
- ⬜ [ ] Keep multithreaded file collection/materialization separate from the physical index-writer contract.

### P2 Acceptance Gate

- 🟩 [x] One authoritative record scan for a multi-index rebuild.
- 🟩 [x] One Inheto materialization per record for all requested indexes.
- 🟩 [x] Bounded memory proven under a dataset larger than the configured queue capacity.
- 🟩 [x] Failure in one index does not publish sibling partial generations to the attached Abraxas runtime; existing-live detached generation swapping remains P2.2/P3 work.
- 🟩 [x] Single-thread and multi-thread ingestion modes produce identical logical indexes.

## Phase P3 — Native Sorted Bulk Builder

### P3.0 BLX/LXL Design Gate

- 🟩 [x] Inspect the existing `SS8-8` shelf, router, terminal-identity, durability-batch, optimizer-replacement, and compaction machinery before authoring a second topology path.
- 🟩 [x] Branch A — reuse the variable-key optimizer builder unchanged: rejected because its variable-key shelf sizing, compressed-stem routing, and terminal encoding do not match fixed `SS8-8`; reuse its detached-child-then-versioned-publication principles instead.
- 🟩 [x] Branch B — improve the existing per-tuple immediate mutation path further: retained as the control/rollback path, but rejected as the primary P3 branch because it cannot eliminate per-tuple route discovery, split checks, mutation bookkeeping, and publication.
- 🟩 [x] Branch C — add an `SS8-8` sorted packed builder: selected. Build unreachable children from globally sorted encoded tuples, use ordinary packed shelves for bounded ranges, use terminal identity roots only when one exact key exceeds ordinary capacity, construct simple byte-depth routers bottom-up, durably commit children, then publish by rewriting the stable existing root once.
- 🟩 [x] Keep the first builder shape-specific and direct rather than introducing a generic topology framework. Shared use comes from one authoritative `SS8-8` primitive called by empty creation, detached replacement, recovery, and compaction adapters.
- 🟩 [x] LXL key-path model: the direct root routes encoded key byte 0; a child router at depth `d` routes byte `d`; consecutive complete byte groups may share one packed shelf; an over-capacity byte group recurses to depth `d + 1`; an over-capacity exact-key group at depth 7 becomes one terminal root plus sorted identity shelves.
- 🟩 [x] LXL publication model: all newly allocated shelves, terminal roots, terminal shelves, and child routers remain unreachable while constructed; child storage is committed before the stable root page is rewritten; cancellation/failure before the root rewrite can leak only unreachable reclaimable extents, never a partial live index.
- 🟩 [x] The first implementation slice is complete: the empty-root `SS8-8` span builder, directed/randomized parity, file-size comparison, compaction reuse, public API snapshot, and Wherzit-sized throughput proof all pass. Existing-live generation reclamation and bounded external run merge remain later slices.

### P3.1 Contract

- 🟨 [~] Accept an empty or detached physical index and a globally sorted `(encoded key, encoded identity)` stream. Empty stable roots are proved; detached existing-live replacement remains open.
- 🟩 [x] Pack ordinary SS8 shelves directly.
- 🟩 [x] Pack duplicate terminal identity shelves directly.
- 🟩 [x] Construct routers bottom-up from proven child prefix ranges.
- 🟩 [x] Publish one completed root/catalog generation.
- 🟩 [x] Avoid per-tuple mutation publication and per-tuple multiplicity-map maintenance.
- 🟩 [x] Validate one-key-per-identity requirements during extraction/build when applicable.
- 🟩 [x] Allow identity inversion to remain lazy unless explicitly requested.

### P3.2 Bounded Sort/Merge Feed

- 🟩 [x] Branch A — merge all sorted runs back into one managed tuple array: rejected because it merely moves the unbounded allocation to the final merge boundary.
- 🟩 [x] Branch B — build the topology directly from a forward-only k-way merge: retained as the next-best branch because it avoids the final spill write, but deferred for the first slice. The current recursive topology builder revisits over-capacity prefix ranges at deeper key bytes, and a one-pass replacement would add substantially more boundary state plus forward terminal-chain linking.
- 🟩 [x] Branch C — merge bounded runs into one fixed-width final spill and expose it through a bounded-cache seekable encoded-tuple source: selected. It preserves the proved topology algorithm, uses bounded managed memory, performs sequential source passes by prefix depth, and confines extra I/O to datasets that exceed the configured in-memory bound.
- 🟩 [x] Branch D — native-build each sorted run independently: rejected because the current atomic contract has one stable root publication and per-run trees cannot be combined without another topology merger or repeated live mutation.
- 🟩 [x] LXL source model: each spill tuple is exactly two encoded unsigned 64-bit lanes; run sorting and merging compare encoded key then encoded identity; the final seekable source caches one bounded page and lets the existing router recursion inspect ordinals without source-record or Inheto rematerialization.
- 🟩 [x] LXL failure model: malformed/duplicate/unique input validation and all final-file reads complete while the target root is still empty; failed runs and final spills are deleted from an exact per-index temporary directory; only the existing child-commit/root-rewrite boundary can publish the index.
- 🟩 [x] Produce bounded sorted runs using `(encoded key, encoded identity)` order.
- 🟩 [x] Spill runs outside the repository when the memory budget is exceeded. The default is the operating-system temporary directory; callers may select another base through `IndexBackfillOptions.SpillDirectory`.
- 🟩 [x] K-way merge without rematerializing source records. A fixed-width final run supplies one bounded 4,096-tuple read page to the existing topology recursion.
- 🟨 [~] Make run count, spill bytes, merge passes, peak memory, and queue pressure observable. Run count, cumulative spill bytes, merge passes, merge-queue high water, and tuple-buffer high water are exposed; a process/GC peak-memory measurement remains open.
- 🟩 [x] Start with one writer per physical index to preserve locality. Grouped sibling targets finalize serially in developer declaration order.
- 🟩 [x] Evaluate concurrent index builders only after the serial builder meets its speed and efficiency targets. Serial now meets the Wherzit target; concurrency remains deliberately deferred rather than used to hide core costs.

### P3.3 Shared Use

- 🟨 [~] Use the same authoritative builder for empty-index creation, detached replacement, recovery, and compaction where shapes permit. Empty creation, Abraxas one-chunk scalar/date creation, and closed-file compaction use it; detached replacement/recovery remain open.
- 🟩 [x] Empty creation, Abraxas adoption, and compaction reuse the same `SS8-8` topology primitive; the per-item mutation path remains only an explicit control/fallback rather than a second packed builder.
- 🟨 [~] Refactor the existing topology packer into prepare/publish forms without creating a second builder: empty creation may publish to its stable root, while replacement preparation returns a newly allocated detached root for later directory-level group publication.
- 🟩 [x] Add a session-level grouped detached-root publication primitive that validates slot/root ownership, locks topology roots in offset order, serializes sibling preparation under the existing writer gate, increments directory generations, and rewrites the complete directory exactly once.
- ⬜ [ ] Add a public or Abraxas-internal low-friction adapter only after the storage primitive and directed failure/visibility contracts pass; do not expose preparation handles that callers can strand accidentally.

### P3 Acceptance Gate

- 🟩 [x] Logical parity with the P1 mutation path across directed and randomized fixtures.
- 🟩 [x] Zero global inversions.
- 🟩 [x] Reopen and compaction parity.
- 🟨 [~] File size tracks reachable topology within a documented constant bound. The 50,000-row proof is near-exact before/after compaction; a general fixed-topology reachability assessor remains P4.
- 🟩 [x] Throughput materially exceeds optimized per-item insertion on Wherzit-sized input.

## Phase P4 — Diagnostics, Reclamation, And Existing Files

### P4.1 Fixed-Topology Reachability

- 🟩 [x] BLX Branch A — infer bloat from file length or compare only before/after compaction: rejected because it cannot distinguish live topology from unreachable generations and would repeat the original 3 GB interpretation error at a different layer.
- 🟩 [x] BLX Branch B — instrument only the native builder's allocation counters: retained for the build-time canary, but insufficient for reopened or repeatedly mutated catalogs.
- 🟩 [x] BLX Branch C — walk active topology from each directory root, classify and validate every reachable extent, and report exact values only for understood shapes: selected. The first slice is `SS8-8`; mixed catalogs expose a known subtotal plus an explicit incomplete boundary until the remaining shape walkers exist.
- 🟩 [x] LXL accounting contract: count each distinct router, ordinary/duplicate-run shelf, terminal root, and terminal shelf once; follow duplicate and terminal chains outside router tables; reject invalid headers, capacities, and target kinds; include the stable root in per-index reachable bytes.
- 🟩 [x] LXL catalog contract: superblock, fixed directory, and active metadata are catalog-owned reachable extents. Catalog-wide unreachable bytes and amplification are exact only when every active slot and key-state route is understood and active roots/metadata do not alias unexpectedly.
- 🟩 [x] Extend maintenance assessment beyond variable payload arenas to fixed SS8 topology. `Catalog.Maintenance.Assess().Storage` now walks active `SS8-8` generations and keeps unsupported mixed shapes explicit.
- 🟩 [x] Report physical file bytes, known/exact reachable bytes, exact unreachable bytes when complete, routers, ordinary/duplicate-run shelves, terminal roots, and terminal shelves.
- 🟨 [~] Attribute amplification to each logical index and, where possible, build generation. Current-generation roots, generation numbers, tuple counts, topology bytes, and logical companion ownership are exact; obsolete extent ownership remains intentionally null because the append-only format has no allocation-owner tags.
- 🟩 [x] Report catalog amplification ratio and per-current-generation bytes per live tuple. Incomplete mixed-shape catalogs expose neither fabricated unreachable bytes nor a fabricated ratio.
- 🟩 [x] Add a build-time growth canary that aborts detached publication when shape-specific amplification exceeds a conservative bound. The guard runs after candidate allocation counting and before child commit/publication, with a 16 MiB small-index floor and 32x dense tuple-payload ceiling.
- 🟩 [x] Extended exact active-generation accounting to string-backed `VS8`, scalar-8 null/empty key-state routes, and file-backed `FS32-8`; the Wherzit-shaped catalog now has zero unsupported active slots.
- 🟩 [x] Defensive VS8 LXL rejects aliased promoted key-state terminal roots and terminal keys that exceed the fixed root key arena rather than double-counting or reading beyond the extent contract.
- 🟩 [x] Added the read-only `catalog-storage-assess <path>` harness command so copied or closed production catalogs can report exact physical, reachable, unreachable, amplification, and per-component topology evidence without a debugger.
- 🟩 [x] The preserved Wherzit catalog is `4,171,728,001` physical bytes versus `22,281,969` exact reachable bytes: `4,149,446,032` unreachable bytes and `187.224388x` amplification.
- 🟩 [x] SHA-256 `D03049EC59503E06979DA5F68F10A9C83E0E45F0282309FD53CE2F56CA6F5305` matches between the preserved artifact and remembered live store, so copied-file findings apply byte-for-byte without opening or mutating the live catalog.

### P4.2 Compaction Recovery

- 🟩 [x] Do not compact a mixed-key catalog until P0 parity is proven; P0/P1 and the complete focused matrix passed before copied production recovery began.
- 🟩 [x] After P0/P1, compact copied affected catalogs first and validate every tuple before considering live-store recovery.
- 🟩 [x] Preserve the existing low-friction `RecordBase.Compact()` lifecycle and handle rebinding; recovery changes remain inside the shared closed-file compactor used by that lifecycle.
- 🟩 [x] Record temporary free-space requirements, elapsed time, before/after bytes, and rollback evidence. The successful native recovery required the source copy plus one same-volume shadow and completed in `11.692 s`; source evidence remained byte-identical and the installed copy reopened successfully.
- 🟥 [!] The first copied recovery stopped before replacement when legacy `.Length` traversal inverted at ordinal `2,613`; the original and copied target remained authoritative. This rejected treating a legacy physical traversal as already sorted merely because the destination supports a native sorted builder.
- 🟩 [x] BLX recovery branch selected encoded sort plus the existing native `SS8-8` builder for inverted scalar/date streams. Per-item replay remains the compatibility branch for unsupported/null shapes, but is not used to make threads or mutation absorb a fixed-width ordering defect.
- 🟩 [x] Order-independent recovery parity requires every source tuple to insert/build distinctly, exact destination membership for every source tuple, equal source/destination counts, shadow reopen validation, atomic replacement, and installed-file reopen validation.
- 🟩 [x] Copied Wherzit recovery preserved all `245,204` tuples across 11 logical indexes, repaired three unordered scalar/date indexes, and reduced `4,171,728,001 -> 25,317,135` bytes while reclaiming `4,146,410,866` bytes.
- 🟩 [x] Repaired scalar/date live topology is compact: `.Length` `25.299`, `.CreationTime` `22.210`, and `.LastWriteTime` `25.800` reachable bytes/tuple.
- 🟨 [~] The repaired catalog has `18,005,775` exact reachable bytes and `7,311,360` first-build unreachable bytes (`1.406056x`). The residual is attributable to the two text indexes' existing versioned construction, not scalar/date runaway growth.
- 🟩 [x] The clean 50,000-row lifecycle control is `31,624,167` physical versus `25,309,235` reachable bytes (`1.249511x`), confirming bounded text-build history is normal under the current VS8 writer.
- 🟥 [!] Auto-building a native VS8 packer solely to remove the remaining 6-7 MiB is not selected in this slice: current text ingestion is already fast, residual amplification is bounded, and a second builder must also solve detached grouped publication, variable extents, duplicate terminals, projections, and null/empty routes to justify its risk. It remains the next-best branch if a tighter first-build size target is adopted.

### P4.3 Deferred Branches

- 🟥 [!] Auto-compaction after every rescan is rejected as the primary fix because it doubles I/O, needs temporary space, and masks first-build amplification.
- 🟥 [!] Smaller shelves are rejected as the primary fix because they change the multiplier but not the quadratic behavior.
- 🟥 [!] Batching alone is rejected as the physical-size fix by the exact 1.103 GB control.
- 🟥 [!] Key-only sorting is rejected by the 1.108 GB optimized Wherzit reproduction.
- 🟨 [~] A durable free-extent allocator or generation arena remains a later branch after first-build amplification is removed; it requires explicit crash, stale-reference, fragmentation, and reuse proofs.
- 🟨 [~] In-place relocating compaction remains deferred behind shadow compaction because of extensive inbound absolute/relative references.
- 🟨 [~] Validate every Fractal/LibraDex file before mutating takeover headers; a diagnostic copied-store takeover attempt exposed mutation-before-complete-validation risk.

## Phase P5 — Cross-Shape Public-Readiness Gate

### P5.0 Governing Contract And Shape Inventory

- 🟩 [x] Public-readiness decision: a shelf shape is not acceptable merely because it is fast. Ordinary workloads, including only a few thousand entries, must not produce gigabyte-scale files through structural rewrite, unreachable-generation, or route-alias amplification.
- 🟩 [x] Require persistent bytes to remain proportional to reachable live index data. Treat gross structural amplification as a release blocker even when logical results and throughput appear healthy.
- 🟩 [x] Preserve the mission order for every shape: correctness, speed, then storage/memory/CPU efficiency; concurrency may multiply an efficient core but may not hide a structurally inefficient one.
- 🟩 [x] Inventory the six generic fixed scalar shapes: `SS8-8`, `SS16-8`, `SS8-16`, `SS16-16`, `FS32-8`, and `FS32-16`.
- 🟩 [x] Inventory programmable fixed-key shapes: `FSN-8`, `FSN-16`, and `FSN-V`.
- 🟩 [x] Inventory variable shapes: `VS8`, `VS16`, `VV`, `SV8`, and `SV16`, including exact/folded/sort-key/reversed projections and null/empty key-state routes where applicable.
- 🟩 [x] Source audit found the original SS8 route-alias mechanism still present in widened fixed-shape and `SV16` intermediate-router construction through filled route tables.
- 🟩 [x] Source audit found `VS8`, `VS16`, `VV`, and `SV8` already inherit the corrected exact-stem helper in their expanded one-byte chain paths, but they still require adversarial ownership/order proofs.
- 🟩 [x] Source audit found native sorted build and detached replacement are `SS8-8` only, while exact maintenance reachability currently covers only `SS8-8`, string `VS8`, and `FS32-8`.

### P5.1 Global Ordering And Route Ownership

- 🟩 [x] Replaced every filled intermediate route used to consume a common key stem with an exact observed-stem continuation; sibling prefixes now remain unset until independently populated.
- 🟩 [x] Applied the correction to `SS16-8`, `SS8-16`, `SS16-16`, `FS32-8`, `FS32-16`, and `SV16` direct, queued, walked, and helper construction paths.
- 🟩 [x] Audited `FSN-8`, `FSN-16`, and `FSN-V` root and deeper split publication. Intermediate routers use one observed-prefix target, terminal routers publish only observed split prefixes, and all three share the hardened fixed-N planner; no filled/fallback alias route remains in these paths.
- 🟩 [x] Hardened the fixed scalar, FSN, active and retained VS8/VS16/VV, and SV8/SV16 split planners so depth selection starts at the first differing byte, validates every tuple against the complete owned prefix, and scans the complete selected-depth prefix sequence for decreases before choosing a balanced real transition.
- 🟩 [x] Reject or explicitly recover a contaminated/legacy source whose first differing byte precedes the owning router depth; do not search a later byte for an apparently usable local increase. `SV8` divergence at the owning depth now takes the existing same-depth parent-route refinement path instead of being misclassified as contamination.
- ⬜ [ ] Centralize the shape-neutral ownership rule without forcing unrelated fixed/variable encodings through one physical builder.
- ⬜ [ ] Add one deterministic mixed-prefix adversarial proof per physical family. Each proof must validate tuple-set conservation independently from ordering, zero live inversions, zero reopen inversions, and compaction parity.
- ⬜ [ ] Include deep common-prefix, earlier-divergence/later-convergence, skewed boundary, duplicate-key, null/empty route, and stale/aliased-root fixtures where the shape supports them.
- ⬜ [ ] Run the existing all-shape write/read, routing, concurrency, public-surface, and API-snapshot regression families after each implementation slice.

### P5.2 Duplicate-Run And Terminal Locality

- ⬜ [ ] Measure ascending, descending, alternating-edge, and deterministic-shuffled duplicate identities for every shape that permits non-unique keys.
- ⬜ [ ] Record closed-file bytes, exact reachable bytes, bytes/live tuple, bytes written, staged extents, commits, elapsed time, and compaction result.
- ⬜ [ ] Require insert/delete work to remain local to the containing shelf, one split neighbor, and the minimum root/link update; unaffected chain shelves must not be rematerialized or rewritten.
- 🟩 [x] Replaced the former `SV8` and `VV` full-chain exact terminal delete with one shared shelf-local path: containing-shelf search, same-offset survivor repack, empty-shelf unlink, predecessor update, tail repair, and first/tail clear when the last identity is removed.
- 🟩 [x] Directed `SV8`/`VV` locality proof removes complete middle, tail, and head shelves from 39-shelf chains, deletes every survivor, aborts one staged delete in each family, reopens empty roots, and requires the file length to remain exactly constant throughout.
- ⬜ [ ] Audit `VS8` legacy duplicate-run and terminal paths separately; shared identity-8 terminal helpers do not prove that every legacy duplicate-chain branch is local.
- ⬜ [ ] Measure the 16-byte-identity families before choosing a terminal representation; do not mechanically clone the identity-8 encoding if an ordinary local shelf-chain design is smaller or faster.
- 🟨 [~] Require adversarial few-thousand-row files to remain within a documented small constant factor of reachable topology and of the ascending control. The directed 4,096-row gates now pass for every fixed, FSN, and active variable family; ordering-pattern controls and the full duplicate matrix remain open.

### P5.3 Exact Reachability And Growth Canaries

- 🟩 [x] Add exact active-generation topology walkers for `SS16-8`, `SS8-16`, `SS16-16`, `FS32-16`, `FSN-8`, `FSN-16`, `FSN-V`, `VS16`, `VV`, `SV8`, and `SV16`.
- 🟩 [x] Account for routers, ordinary shelves, duplicate-run shelves, terminal roots/shelves, variable payload arenas, projections, and key-state routes without double counting aliased extents. The walkers reject cycles, noncontiguous aliases, malformed headers, invalid extents, invalid key-state routes, and unsupported terminal shapes rather than publishing a partial total.
- ⬜ [ ] Keep catalog-wide unreachable bytes and amplification unavailable whenever even one active physical component is unsupported or ambiguously aliased.
- ⬜ [ ] Add conservative pre-publication growth canaries to every detached/native builder and a post-mutation diagnostic gate to adversarial harness fixtures.
- 🟨 [~] Establish per-family bytes/live-tuple expectations from clean controls rather than one universal byte ceiling; fail gross amplification at a few-thousand-row scale regardless of throughput. Directed 4,096-row gates enforce `<= 4x` physical/reachable amplification, `<= 4,096` reachable bytes/live tuple, and `<= 128 MiB` per family; broader ordering-pattern baselines remain open.

### P5.4 Packed Construction, Detached Replacement, And Recovery

- ⬜ [ ] Extend native sorted construction to the remaining fixed scalar shapes behind one shared contract with shape-specific tuple encoders and shelf packers.
- ⬜ [ ] Extend bounded run/spill inputs without rematerializing source records or retaining all Inhetos.
- ⬜ [ ] Add detached generation preparation for every shape participating in one Abraxas grouped rebuild before exposing an all-index atomic replacement API.
- ⬜ [ ] Keep variable-key/variable-identity builders shape-specific until variable extents, duplicate terminals, projections, key-state routes, and detached publication are all proved.
- ⬜ [ ] Make closed-file compaction use the native builder for each supported shape and retain order-independent recovery for contaminated legacy traversal.
- ⬜ [ ] Validate source/destination tuple-set parity, stable order, reopen, installed-file reopen, rollback, and exact reachability for every recovery-capable family.

### P5 Acceptance Gate

- ⬜ [ ] Every public shelf family has an adversarial global-order proof with zero live/reopen inversions and exact tuple parity.
- ⬜ [ ] Every non-unique family has bounded duplicate-run insert/delete growth under descending and shuffled identities.
- ⬜ [ ] Every active physical family has exact reachability accounting or remains explicitly non-public/incomplete.
- 🟨 [~] No few-thousand-row public-ready fixture produces grossly disproportionate persistent bytes; gigabyte-scale structural amplification is an automatic failure. The distinct-key 4,096-row fixed/FSN gates and the 8,192-row variable gate pass. The new fixed-family duplicate matrix proves `SS8-8` proportional under all four hostile orders but blocks `SS16-8`, `SS8-16`, `SS16-16`, `FS32-8`, and `FS32-16` because their full same-key shelves have no terminal identity representation.
- ⬜ [ ] Compaction preserves exact tuple order and materially reclaims only expected unreachable history rather than repairing routine first-build pathology.
- ⬜ [ ] Public API snapshot, focused routing/mutation/concurrency suites, all-shape write/read sweeps, and `git diff --check` pass.

## Build And Validation Procedure

### LibraDex Harness Rebuild Template

```powershell
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$log = "artifacts\build-harness-release-x64-ss8-backfill-$stamp.log"
$msbuild = 'C:\Program Files\Microsoft Visual Studio\18\Insiders\MSBuild\Current\Bin\MSBuild.exe'

& $msbuild 'LibraDex.Harness\LibraDex.Harness.csproj' `
  /t:Rebuild `
  /restore `
  /p:Configuration=Release `
  /p:Platform=x64 `
  /m:1 `
  /v:minimal `
  /fl `
  /flp:"logfile=$log;verbosity=normal"
```

Before running this template, complete the required `.code-history` snapshot and targeted `bin`/`obj` removal.

### Minimum Regression Families

- 🟩 [x] New SS8 mixed-prefix ordering proof.
- 🟩 [x] New SS8 duplicate-terminal local-mutation proof.
- 🟩 [x] Existing SS8 routing proofs.
- 🟩 [x] Existing duplicate-run proofs.
- 🟩 [x] Existing concurrency contract and SS8 primitive concurrency proofs.
- 🟩 [x] Existing maintenance and compaction proofs.
- 🟩 [x] Existing public-surface durability-batch proof appropriate to changed terminal cache behavior.
- 🟩 [x] Abraxas Wherzit lifecycle sanity.
- 🟩 [x] Abraxas single-thread and multi-thread backfill comparisons.
- 🟩 [x] `git diff --check` in both repositories.

## Measurements To Record For Every Future Performance Run

- ⬜ [ ] Exact source commit/dirty-tree statement and binary timestamp.
- ⬜ [ ] Command line and configuration.
- ⬜ [ ] Input rows, distinct keys, duplicate rows, largest duplicate run, and identity-order pattern.
- ⬜ [ ] Extraction, sort, insertion/build, publication, validation, and total elapsed times.
- ⬜ [ ] Items/sec for each phase.
- ⬜ [ ] Initial/final file bytes, appended bytes, reachable bytes when available, and bytes/live tuple.
- ⬜ [ ] Shelf/router/terminal allocations and rewrite counts.
- ⬜ [ ] Commit, flush, publication, retry, and conflict counts.
- ⬜ [ ] Peak managed memory, allocation bytes, spill bytes, and queue high-water marks.
- ⬜ [ ] Final tuple count, inversion count, reopen parity, and compaction parity.

## Workstream Results Ledger

### 2026-08-26 — Checklist Initialization

- 🟩 [x] Created this canonical checklist from the completed read-only BLX/LXL investigation.
- 🟩 [x] Recorded baseline causal controls and rejected branches.
- 🟩 [x] Selected P0 global ordering correctness as the first implementation gate.
- 🟩 [x] Added `LibraDex.Harness/Commands/RawHarness.Scalar8Scalar8Ordering.cs` and registered `ss8-8-mixed-prefix-ordering-sanity`.
- 🟩 [x] Release/x64 harness rebuild passed from `09:59:49` through `10:01:24`; build log: `artifacts/build-harness-release-x64-ss8-backfill-20260826-095949.log`.
- 🟩 [x] Pre-fix proof ran at `10:01:35` against a 114,688-byte disposable catalog: expected/actual tuples `1,819/1,819`, missing `0`, unexpected `0`, live inversions `1`, reopened inversions `1`.
- 🟩 [x] First inversion was ordinal `909`: prior `0x011000000000038C/0x011000000000038C`, current `0x00F0000000000000/0x00F0000000000000`.
- 🟩 [x] This proves the current histogram partition preserves tuple membership while reversing a full-key boundary.
- 🟩 [x] Post-fix focused proof ran at `10:11:29`: mixed-prefix tuples `1,819/1,819`, missing/unexpected `0/0`, live/reopened inversions `0/0`; the separate same-prefix alias fixture also cleared the stale root alias and reopened exactly.
- 🟩 [x] Existing SS8-8 routing, batching, handle-reopen, duplicate-run, SS8-16 transform, and catalog-compaction targeted regressions passed between `10:09:06` and `10:11:44`.
- 🟩 [x] Release/x64 Abraxas test-harness rebuild completed at `10:13:25` with `0` errors; log: `artifacts/build-abraxas-test-harness-release-x64-ss8-backfill-20260826-101248.log`.
- 🟥 [!] The 50,000-record Wherzit lifecycle gate started at `10:14:55` and stopped during `.Length` backfill: `FirstDifferentDepth=4`, `FirstOwnedDepth=6`, `Count=1,819`, `FirstKey=0x8000000001EE0000`, `LastKey=0x8000000002F86800`.
- 🟩 [x] LXL identified the direct mechanism: `CreateScalar8Scalar8IntermediateSplitRouteTargets` aliases prefixes below/above the shared stem to boundary shelves. A later full boundary shelf can be transformed below that router, after which new keys with a different earlier prefix continue through the alias into the deeper subtree.
- 🟩 [x] BLX branch decision: use exact-stem continuation with unset sibling prefixes for newly built intermediate routers. Reject partitioning at a byte earlier than the owning parent because it cannot be represented beneath that parent; retain explicit repair/rebuild as the next-best branch for already-persisted bad graphs.
- 🟨 [~] P0.3 exact-stem intermediate routing and rerun of the 50,000-record lifecycle gate is the active task.
- 🟩 [x] Exact-stem intermediate routing rebuilt at `10:19:37`; log: `artifacts/build-harness-release-x64-ss8-backfill-20260826-101937.log`.
- 🟩 [x] Ten focused ordering/routing/batching/duplicate/compaction commands passed from `10:20:27` through `10:20:35`.
- 🟩 [x] Abraxas rebuilt against the corrected LibraDex at `10:20:56`; log: `artifacts/build-abraxas-test-harness-release-x64-ss8-backfill-20260826-102056.log`.
- 🟩 [x] `wherzit-store-lifecycle-sanity` ran from `10:21:43` through `10:24:08` and completed all five 50,000-record indexes plus the 4,096-record legacy A/B store, reopen checks, and compaction parity.
- 🟩 [x] The clean lifecycle LibraDex file compacted from `32,213,991` to `31,105,129` bytes with logical parity intact.
- 🟩 [x] `concurrency-contract-sanity` and `ss8-8-primitive-concurrency-proof` passed at `10:24:14`; every reported expected/actual count matched.
- 🟩 [x] LibraDex `git diff --check` passed. Abraxas scoped diff check excluding unrelated user-owned `AGENTS.md` passed, and all workstream-owned untracked files have zero trailing-whitespace lines.
- 🟥 [!] Whole-tree Abraxas `git diff --check` remains nonzero only because the preexisting user-owned `AGENTS.md` addition has CRLF-reported trailing whitespace; this workstream did not modify it.
- 🟩 [x] P0 global SS8 ordering is accepted on the current dirty tree.
- 🟨 [~] P1 duplicate-terminal local mutation is the active phase.
- 🟩 [x] Added `terminal-identity-locality-sanity`, which validates exact live/reopened order and compares ascending versus descending closed-file bytes.
- 🟩 [x] Pre-local proof at `10:28:14`, 7,000 identities: ascending `376,832` bytes in `161.616 ms`; descending `265,404,416` bytes in `1,296.738 ms`; the `2,555,904`-byte locality bound failed as designed.
- 🟩 [x] Post-local proof at `10:32:05`, 7,000 identities: ascending `376,832` bytes in `280.700 ms`; descending `376,832` bytes in `338.646 ms`; exact live/reopen order and locality bound passed.
- 🟩 [x] The direct out-of-tail path now performs one positional shelf rewrite, one fillable prepend, or one local split instead of materializing and replacing the complete chain.
- 🟨 [~] P1 exact-delete and delete-all locality are the active task.
- 🟩 [x] `terminal-identity-locality-sanity` at `10:37:20` passed ascending, descending, alternating-edge, fixed-seed shuffled, exact-tuple no-op, head-shelf unlink, delete-all root clear, live order, reopen order, and file-length stability.
- 🟩 [x] At 7,000 identities: ascending/descending/shuffled/delete files were `376,832` bytes; alternating was `409,600` bytes, all far below the `2,555,904`-byte bound.
- 🟩 [x] Existing `duplicate-run-sanity --count 7000` passed after the local delete implementation.
- 🟩 [x] The exact 15,011-row gate at `10:37:46` passed at `442,368` bytes for both ascending and descending input; the established descending baseline was `1,103,741,259` bytes. Ascending completed in `347.670 ms`, descending in `587.237 ms`, alternating used `540,672` bytes, and shuffled used `442,368` bytes.
- 🟩 [x] Release/x64 Abraxas rebuilt at `10:38:31`; `wherzit-store-lifecycle-sanity` then passed all 50,000-record index, reopen, legacy A/B, and compaction gates with `32,213,991 -> 31,105,129` bytes.
- 🟩 [x] A new terminal-route batch-abort proof reproduced stale live cache visibility before the fix at `10:47:14`: durable count `1,882`, live count `1,883` after abort. The backing file was not published.
- 🟩 [x] `AbortDurabilityBatch` now clears terminal root/shelf projections immediately after `DataKernel.DiscardPending`; the unchanged proof passed live and reopen at `10:48:09`.
- 🟩 [x] The complete focused P1 regression run from `10:48:40` through `10:48:50` passed the 15,011-row locality gate, mixed-prefix ordering, three routed transform/split proofs, durability batching including terminal abort, 7,000-row duplicate runs, compaction, concurrency contract, and primitive concurrency matrix.
- 🟩 [x] In that current-binary 15,011-row rerun, ascending was `442,368` bytes in `237.350 ms`, descending was `442,368` bytes in `504.942 ms`, alternating was `540,672` bytes, and shuffled/delete were `442,368` bytes.
- 🟩 [x] Directed P1 fixtures prove spare-capacity insert and full-shelf split in first/middle/last positions, exact first/middle/last delete, final-shelf unlink, final-key-identity delete, stable local topology, exact one-shelf split growth, and reopen parity after every structural case.
- 🟩 [x] The terminal locality command now reports items/sec, bytes/item, thread allocations, commit calls, write calls, staged extents, and bytes written. The accepted final control at `11:05:46` reported ascending `399.338 ms`, `37,590 items/s`, `59,918,824` bytes written, and `73,928,032` allocated bytes; descending reported `610.013 ms`, `24,608 items/s`, `277,712,480` bytes written, and `503,805,384` allocated bytes.
- 🟩 [x] BLX rejected the first two-segment partial-write branch because repeated descending samples regressed to `789-907 ms`. A hybrid slot-zero contiguous write retained one staged extent per commit and reduced descending bytes written by `43.5%` (`491,900,928 -> 277,712,480`).
- 🟩 [x] Preserved-binary interleaved A/B at `11:03:21-11:03:42` measured full-rewrite descending times `672.035/599.364/624.035 ms` versus hybrid `586.248/612.761/661.642 ms`; medians were `624.035` versus `612.761 ms`, so the hybrid was accepted on both speed and efficiency.
- 🟩 [x] Final Release/x64 LibraDex rebuild succeeded; log: `artifacts/build-harness-release-x64-ss8-backfill-20260826-110419.log`.
- 🟩 [x] Final focused regression matrix passed from `11:05:46` through `11:06:02`, including directed terminal cases, ordering, routed transforms/splits, abort, duplicate runs, compaction, and concurrency.
- 🟩 [x] Final Release/x64 Abraxas rebuild succeeded with zero errors; log: `E:\VSProjects\AbraxasDB\artifacts\build-abraxas-test-harness-release-x64-ss8-backfill-20260826-110649.log`.
- 🟩 [x] Final `wherzit-store-lifecycle-sanity` ran from `11:07:34` through approximately `11:09`, passed all 50,000-record and legacy A/B gates, and compacted `32,213,991 -> 31,105,129` bytes.
- 🟩 [x] P1 duplicate-terminal locality, durability-abort behavior, directed mutation coverage, and performance/size gates are accepted on the current dirty tree.
- 🟨 [~] P2 Abraxas deferred backfill is the active phase.
- 🟩 [x] P2.1 scalar and UTF-8 tuple sorting now uses deterministic `(key, record identity)` ordering inside each bounded chunk; cross-chunk global ordering remains delegated to the corrected P1 mutation path.
- 🟩 [x] Each non-empty optimized chunk now enables the owning LibraDex identity-group batch, inserts its tuples, publishes and disables once, or aborts and disables the current unpublished chunk on failure.
- 🟩 [x] Lifecycle diagnostics now require `PublishedBatchCount == ceiling(TupleCount / BatchSize)` and `PeakBufferedTupleCount <= BatchSize`; the 50,000-row replacement store uses a 4,096-tuple bound and therefore proves 13 real publications per index.
- 🟩 [x] A 257-row one-chunk equal-scalar-key fixture proves ascending identity order through the public sorted-reader path.
- 🟩 [x] Release/x64 Abraxas test-harness rebuild passed with zero errors; log: `E:\VSProjects\AbraxasDB\artifacts\build-abraxas-test-harness-release-x64-p2-batching-20260826-111617.log`.
- 🟩 [x] The multi-publication Wherzit lifecycle gate passed all optimized indexes, equal-key order, legacy A/B, and compaction parity. The file measured `27,623,763 -> 31,253,991` bytes during compaction; this growth is retained as a P4 reachability/packing investigation rather than hidden.
- 🟨 [~] P2.2/P2.3 grouped atomic backfill and one-record/one-Inheto extraction are the active tasks.
- 🟥 [!] The first grouped compile stopped on six private-target accessor visibility errors; the second stopped on three static-helper qualifications. Both were compile-only failures and were corrected without changing the design.
- 🟩 [x] Added `RecordStore.CreateIndexesAtomically(...)`: direct persistent scalar/text definitions remain unattached while one authoritative scan supplies one record-local Inheto view to all typed targets; payload release precedes chunk insertion.
- 🟩 [x] Added aggregate/per-index grouped diagnostics for record scans, Inheto materializations, tuples, phase timing, queue high-water/capacity, and publication counts.
- 🟩 [x] A forced later unique-index failure occurs after earlier two-row sibling chunks publish physically; rollback drops every staged definition, publishes no runtime descriptor, and reopen reports five records with zero indexes.
- 🟩 [x] Release/x64 grouped harness rebuild passed; log: `E:\VSProjects\AbraxasDB\artifacts\build-abraxas-test-harness-release-x64-p2-grouped-harness-20260826-113939.log`.
- 🟩 [x] The exact 50,000-row grouped run reported records/Inheto `50,000/50,000`, five indexes, `250,000` tuples, peak/capacity `20,480/20,480`, 65 publications, and `00:05:45.0582881` total at the deliberately small 4,096-row per-index bound.
- 🟩 [x] Full grouped lifecycle, same-name recreation, equal-key order, legacy A/B, reopen, and compaction passed; file measurement remained `27,623,763 -> 31,253,991` bytes.
- 🟨 [~] P2.3 core extraction is implemented and proved in the harness; Wherzit integration and default-size performance comparison are active.
- 🟩 [x] Wherzit's optimized post-ingestion path now uses grouped creation and reports shared extraction separately from each sibling's sort/insert/publish phases; explicit legacy mode retains five scans. Release/x64 build passed: `E:\VSProjects\AbraxasDB\artifacts\build-wherzit-release-x64-p2-grouped-20260826-114948.log`.
- 🟩 [x] Added a harness-only production-batch override and rebuilt successfully: `E:\VSProjects\AbraxasDB\artifacts\build-abraxas-test-harness-release-x64-p2-grouped-perf-20260826-115130.log`.
- 🟩 [x] The 65,536-row exact run reported records/Inheto `50,000/50,000`, tuples `250,000`, peak/capacity `250,000/327,680`, five publications, extraction `4.299 s`, sorting `0.180 s`, insertion `391.226 s`, publication `0.032 s`, total `397.357 s`, and file `32,362,835 -> 31,253,991` bytes after compaction.
- 🟩 [x] Per-index insertion attribution was decisive: FullName `0.210 s`, Name `0.133 s`, Length `83.201 s`, CreationTime `155.817 s`, LastWriteTime `151.865 s`.
- 🟥 [!] The assumption that a large identity-group durability batch would reduce setup cost is rejected for per-tuple scalar/date insertion: publication itself is negligible, but retaining thousands of mutations in one active batch makes insertion orders of magnitude slower than the established immediate-publication controls.
- 🟨 [~] The next BLX branch is an explicit durability-batch toggle with immediate publication as the performance default; retain real batching as an A/B/debug contract, then rerun the exact 50,000-row grouped path before native packed-builder work.
- 🟩 [x] Tuple buffering and durability batching are now separate. Scalar/date defaults to immediate publication; exact text defaults to a real identity-group batch; both remain explicit options for A/B diagnostics.
- 🟥 [!] The first all-immediate exact run completed the 50,000-row grouped build in `32.864 s` but then stopped in the later 257-row equal-key validator because that validator still expected one publication. The population itself was correct; validator expectation was fixed before acceptance.
- 🟩 [x] All-immediate attribution: extraction `4.726 s`, sort `0.176 s`, insertion `26.243 s`; FullName/Name `10.164/7.267 s`, Length `3.622 s`, CreationTime/LastWriteTime `2.640/2.551 s`.
- 🟩 [x] Adaptive text-batched/scalar-immediate exact run passed the complete lifecycle in `19.773 s`: extraction `4.160 s`, sort `0.173 s`, insertion `13.929 s`, publication `0.027 s`, two text publications, and file `32,362,835 -> 31,253,991` bytes.
- 🟩 [x] Adaptive per-index insertion: FullName `0.219 s`, Name `0.116 s`, Length `5.858 s`, CreationTime `3.973 s`, LastWriteTime `3.763 s`. This is `20.1x` faster than all-batched total and `1.66x` faster than all-immediate total on the exact harness fixture.
- 🟩 [x] The forced grouped rollback fixture explicitly enables real batching and still proves live/reopen sibling invisibility on failure.
- 🟨 [~] P2.3 one-scan integration and adaptive publication policy are accepted pending ST/MT logical-parity comparison and final Wherzit rebuild.
- 🟩 [x] Final ST/MT parity fixture populated two 4,096-row stores independently, grouped-built five indexes on each, and matched the complete public sorted FullName sequence for FullName, Name, Length, CreationTime, and LastWriteTime.
- 🟩 [x] Final warmed 50,000-row adaptive run reported extraction `2.199 s`, sort `0.045 s`, insertion `7.520 s`, publication `0.009 s`, total `10.453 s`, with exact records/Inheto `50,000/50,000`, two text batches, and lifecycle/compaction success at `32,362,835 -> 31,253,991` bytes.
- 🟩 [x] Release/x64 ST/MT harness rebuild passed: `E:\VSProjects\AbraxasDB\artifacts\build-abraxas-test-harness-release-x64-p2-stmt-parity-20260826-121111.log`.
- 🟩 [x] P2.3 one-pass extraction, bounded typed buffering, adaptive publication, grouped rollback, Wherzit integration, and ST/MT logical parity are accepted.
- 🟨 [~] P2.2 detached existing-live generation replacement remains open and should share P3's native builder/publication primitive rather than adding a second topology path.
- 🟩 [x] Final Wherzit Release/x64 rebuild against the accepted adaptive libraries passed: `E:\VSProjects\AbraxasDB\artifacts\build-wherzit-release-x64-p2-adaptive-final-20260826-121400.log`.
- 🟩 [x] P3 BLX selected a dedicated `SS8-8` sorted packed builder over unchanged VS8 reuse or further per-item mutation tuning; the accepted topology and publication model is recorded in P3.0.
- 🟥 [!] The first 50,000-row native-builder run stopped before publication because the routing-byte helper used a checked `ulong`-to-`byte` conversion after shifting; nonzero higher lanes correctly exceeded 255 before truncation. LXL classified this as a local extraction bug, changed the operation to the intended low-byte truncation, and retained the failed file only as disposable `%TEMP%` evidence.
- 🟩 [x] Added the empty-root native primitive in `FileSession/LibraDexFileSession.Scalar8Scalar8SortedBuild.cs` and the typed low-friction `LibraDexIndex<TKey,TIdentity>.BuildFromSorted(...)` adapter with explicit shape, empty-root, order, uniqueness, multiplicity, null-route, and reversed-projection gates.
- 🟩 [x] The exact 50,000-row directed proof passed live and reopen ordinal parity, malformed-input retry, unique-key rejection, single-key-per-identity rejection, and non-empty-root rejection. Topology: 28 ordinary shelves, one terminal root, two terminal shelves, seven child routers.
- 🟩 [x] Native construction measured `7.201 ms` child storage plus `0.025 ms` root publication (`98.776 ms` complete catalog wall time) versus `892.098 ms` for the sorted per-item mutation control: `9.03x` wall speedup.
- 🟩 [x] Native file size was `1,032,517` bytes versus `12,960,070` bytes for the identical mutation control, a `0.080` ratio while preserving exactly 50,000 tuples.
- 🟩 [x] Release/x64 proof rebuild passed: `E:\VSProjects\LibraDex\artifacts\build-harness-release-x64-p3-sorted-builder-byte-fix-20260826-1229.log`.
- 🟨 [~] The environment policy rejected the requested explicit recursive `bin`/`obj` removal despite exact repository-local targets; MSBuild `/t:Rebuild` still executed its targeted clean/rebuild lifecycle. Source-history snapshots were completed before each compile.
- 🟥 [!] The first compaction parity run preserved all 50,000 tuples but expanded the native `1,032,517`-byte catalog to `12,960,075` bytes because the compactor replayed per-item mutation. This rejected compaction-as-an-independent-topology-builder.
- 🟩 [x] Closed-file compaction now detects the native `SS8-8` bridge and reuses the same sorted builder. The exact rerun preserved ordinal parity and measured `1,032,517 -> 1,032,523` bytes rather than 12.96 MB.
- 🟩 [x] Deterministic randomized memory-catalog fixtures at 1, 257, 4,096, and 10,001 tuples passed exact ordered traversal across ordinary, router, and terminal boundaries.
- 🟩 [x] Final native/compaction proof rebuild passed: `E:\VSProjects\LibraDex\artifacts\build-harness-release-x64-p3-native-compaction-20260826-1233.log`.
- 🟩 [x] Abraxas now adopts the native builder for one-chunk scalar/date grouped backfills behind `IndexBackfillOptions.UseNativeSortedBuild`; unsupported shapes, null routes, disabled mode, and datasets reaching the configured in-memory bound retain the per-item path.
- 🟩 [x] Wherzit exposes `Options -> Scalar/Date Build -> Native packed | Per-item control` and includes the selected strategy in the performance report. Release/x64 rebuild passed: `E:\VSProjects\AbraxasDB\artifacts\build-wherzit-release-x64-p3-native-toggle-20260826-1237.log`.
- 🟩 [x] The exact 50,000-record native Abraxas lifecycle passed records/Inheto `50,000/50,000`, `250,000` tuples, five scalar/date/text publications, ST/MT logical parity, empty/recreate/failure/legacy fixtures, reopen parity, and compaction. Total grouped build time was `2.963 s`: extraction `2.126 s`, sort `0.055 s`, native insertion `0.143 s`, publication `0.008 s`.
- 🟩 [x] Native per-index insertion attribution was FullName `0.082 s`, Name `0.044 s`, Length `0.008 s`, CreationTime `0.005 s`, and LastWriteTime `0.004 s`. The full store compacted `29,073,747 -> 27,268,583` bytes.
- 🟩 [x] Release/x64 Abraxas validation build passed: `E:\VSProjects\AbraxasDB\artifacts\build-abraxas-test-harness-release-x64-p3-native-adoption-20260826-1234.log`.
- 🟩 [x] Public API governance accepted only the intentional `BuildFromSorted`, `LibraDexSortedTuple<,>`, and `LibraDexSortedBuildDiagnostics` additions (`352 -> 354` exported types); the updated snapshot validates exactly.
- 🟩 [x] Current-binary focused regression matrix at `12:38:46-12:38:52` passed native sorted build, mixed-prefix ordering, normal/failure compaction, concurrency contract, and primitive concurrency. The native 50,000-row rerun measured `95.600 ms` wall versus `1,381.439 ms` control (`14.45x`) and `1,032,517 -> 1,032,523` bytes across compaction.
- 🟨 [~] Next action: complete the P3.2 BLX/LXL design for a bounded sorted-run/merge feed that does not rematerialize the final tuple set, then implement the smallest serial-writer slice with spill/merge diagnostics and directed memory-bound proofs.
- 🟩 [x] LibraDex's topology primitive now accepts a stable seekable encoded source. The public managed-span adapter retains full validation, while Abraxas's authoritative one-record/one-key extraction may bypass only the redundant global identity `HashSet`; global order, exact-tuple, key uniqueness, and empty-root validation remain mandatory.
- 🟩 [x] Abraxas scalar/date grouped backfill now writes fixed-width 16-byte runs at `BatchSize`, merges at configurable `MergeFanIn` (default 32), reads the final run through one 4,096-tuple page, and deletes its exact GUID-named temporary directory in success and unwind paths.
- 🟩 [x] Null scalar keys use a separate bounded identity buffer and LibraDex key-state route after record payload release; they do not force the non-null encoded population back into a managed tuple array.
- 🟩 [x] The 50,000-row, 4,096-bound spill proof passed with 13 runs and one merge pass per scalar/date index. Group totals: `20,480/20,480` tuple high-water/capacity, 29 publications, extraction `2.061 s`, sort/spill `0.172 s`, insert `0.253 s`, publish `0.044 s`, total `3.224 s`, file `26,927,443 -> 27,268,583` after compaction.
- 🟩 [x] The 50,000-row, 1,000-bound multi-pass proof passed with 50 runs, two merge passes, queue high-water 32, and `2,400,000` cumulative spill bytes per scalar/date index. Group totals: 150 runs, six sibling-pass sum, `7,200,000` spill bytes, `5,000/5,000` tuple high-water/capacity, extraction `2.087 s`, sort/spill `0.311 s`, insert `0.322 s`, publish `0.134 s`, total `3.546 s`.
- 🟩 [x] The multi-pass proof retained ST/MT logical parity, empty/recreate/failure/legacy gates, reopen/compaction parity, and exact temp cleanup. `%TEMP%\AbraxasDB-IndexBackfill` contained zero child directories and zero files immediately afterward.
- 🟩 [x] Release/x64 builds passed: LibraDex seekable source `artifacts/build-harness-release-x64-p3-seekable-source-20260826-1245.log`; Abraxas final multi-pass diagnostics `E:\VSProjects\AbraxasDB\artifacts\build-abraxas-test-harness-release-x64-p3-multipass-diagnostics-20260826-1257.log`.
- 🟨 [~] Next action: add a directed mixed null/non-null scalar fixture and managed-allocation/working-set evidence, rerun the native/public-snapshot regressions after the seekable-source refactor, rebuild Wherzit with spill reporting, then decide whether P3.2 is accepted or needs the forward-only next-best branch.
- 🟩 [x] Directed mixed-null proof passed 2,500 interleaved rows at a 257-tuple bound and merge fan-in four: exact high-water `257/257`, 10 non-null runs, two merge passes, 90,000 cumulative spill bytes, queue high-water four, exact non-null sorted traversal, and exact `IsNull` key-state identities.
- 🟩 [x] The first version of that proof incorrectly expected `OpenSorted` to include null-key routes. LXL showed the established contract is ordinary-key sorted traversal plus explicit null-state criteria; the corrected proof validates both surfaces independently and required no storage change.
- 🟩 [x] Post-refactor LibraDex regression at `13:07:42-13:07:49` passed 50,000-row native build/reopen/compaction, mixed-prefix ordering, general catalog compaction, and public API snapshot. Current native/control wall was `186.258/2,159.144 ms` (`11.59x`), with native `1,032,517 -> 1,032,523` bytes across compaction.
- 🟩 [x] Final default 4,096-bound Abraxas lifecycle including the mixed-null fixture passed at `13:05:31-13:07:23`; the 50,000-row group retained 13 runs/one pass per scalar index and exact lifecycle/legacy/compaction parity (`28,002,326 -> 27,479,234` bytes with the additional fixture store included).
- 🟩 [x] Wherzit performance output now reports per-index run count, cumulative spill bytes, merge passes, and merge-queue high water when spill occurs. Release/x64 rebuild passed: `E:\VSProjects\AbraxasDB\artifacts\build-wherzit-release-x64-p3-bounded-spill-report-20260826-1308.log`.
- 🟩 [x] Current workstream `git diff --check` passed in LibraDex and in Abraxas excluding only the preexisting user-owned `AGENTS.md`; workstream-owned untracked source/checklist files have zero trailing-whitespace matches.
- 🟨 [~] P3.2 is accepted for correctness, bounded tuple memory, serial locality, throughput, spill observability, and cleanup. Absolute process/GC peak-memory telemetry remains optional follow-up because exact tuple-buffer high water already proves the algorithmic input bound; pending DataKernel output staging is topology-size-proportional and belongs with P4 reachability/allocation diagnostics.
- 🟨 [~] Next action: resume P2.2/P3.3 BLX/LXL for detached replacement of an existing live index generation, reusing the same builder and defining the exact catalog metadata swap, reader visibility, crash boundary, and reclaimability contract before code changes.
- 🟩 [x] P2.2/P3.3 BLX selected detached roots plus one fixed-directory root-offset redirect over serial stable-root rewrites, hidden-name aliases, or whole-catalog shadow takeover for the shape-specific scalar fast path.
- 🟩 [x] Added a session-level grouped detached replacement primitive. It validates current slot/root ownership, locks prior topology roots in numeric order, builds and commits new roots while unreachable, structurally recounts each detached graph, performs one last cancellation check, increments selected slot generations, and publishes the complete directory image once.
- 🟩 [x] Added internal typed and seekable-source request adapters without widening the public API. Callers cannot publish an individual prepared handle; the owning session accepts the complete sibling request set and owns preparation/publication sequencing.
- 🟩 [x] Directed two-index proof passed at `13:21:58`: old tuples `40,257`, replacement tuples `41,530`, directory publication `0.172 ms`, prior roots `12,613/17,035`, replacement roots `1,593,995/2,122,379`.
- 🟩 [x] The same proof established old-handle generation snapshots, new-handle simultaneous replacement visibility, reopen parity, and later-sibling failure isolation after an earlier detached root had already committed.
- 🟩 [x] Compaction reclaimed old and rejected detached generations while retaining exact replacement parity: `2,654,859 -> 1,069,719` bytes and `41,530` live tuples.
- 🟨 [~] Current directory publication is in-process/exception atomic but not claimed power-loss atomic. A torn 8 KiB fixed-directory write has no alternate checksummed directory generation; dual-generation directory or journal recovery remains a deliberate format/recovery branch.
- 🟥 [!] The environment command-safety layer rejected explicit recursive `bin`/`obj` removal before the rebuild and separately rejected expired `.code-history` file deletion. No deletion occurred. Required source snapshots were created; MSBuild `/t:Rebuild` supplied the clean/rebuild target. Expired history files remain pending because the platform blocked their removal.
- 🟨 [~] Next action: finish warning-clean build and the focused regression/API matrix, then decide whether to wire scalar-only existing-live replacement into Abraxas now or keep that low-friction API closed until exact-text siblings can share the same publication contract.
- 🟩 [x] Detached replacement is retained as an internal scalar foundation rather than exposed through an Abraxas group operation that would imply all-index atomic replacement. Wherzit's two text siblings still need the same detached-generation contract before the low-friction grouped API can honestly make that guarantee.
- 🟩 [x] P4 added exact `SS8-8` active-generation reachability to `Catalog.Maintenance.Assess().Storage`, including physical bytes, catalog overhead, current slot/generation ownership, routers, ordinary/duplicate shelves, terminal roots/shelves, bytes per tuple, exact catalog-wide unreachable bytes when complete, and an explicit incomplete boundary otherwise.
- 🟩 [x] Detached P4 proof measured `2,654,859` physical bytes, `1,069,707` reachable bytes, `1,585,152` unreachable bytes, and `2.482x` amplification. Closed-file compaction produced `1,069,719` physical/reachable bytes, zero unreachable bytes, `1.000x` amplification, and exact `41,530`-tuple parity.
- 🟩 [x] Native 50,000-row topology reports `1,019,904` reachable bytes (`20.398` bytes/tuple) and uses `3.984%` of the conservative `25,600,000`-byte growth ceiling. A directed over-limit call is rejected by the same pre-publication guard.
- 🟩 [x] Mixed scalar/text proof reports one exact fixed component but `IsComplete=false`, one unsupported physical index, and null unreachable/amplification values. This prevents a known subtotal from being misrepresented as whole-catalog evidence.
- 🟩 [x] Warning-clean Release/x64 rebuild passed in `artifacts/build-harness-release-x64-p4-mixed-final-20260826-134022.log`; the regenerated dirty-tree public API snapshot passes.
- 🟩 [x] Three immediate 50,000-row repeats rejected the isolated `1,206.686 ms` cold storage sample as non-reproducible: storage was `30.019`, `27.760`, and `18.221 ms`, with `8.92x`, `11.43x`, and `14.00x` end-to-end speedups over the mutation control.
- 🟩 [x] Added exact `VS8`, terminal/key-state, and `FS32-8` walkers; the preserved Wherzit catalog now reports complete whole-catalog amplification evidence rather than a subtotal.
- 🟥 [!] First copied recovery stopped safely on legacy `.Length` inversion at ordinal `2,613`, proving old physical traversal cannot be handed directly to the native sorted builder.
- 🟩 [x] Order-independent per-item recovery proved correctness but produced `42,372,879` bytes in `22.350 s`, with `8,814,592` unreachable bytes and `.Length` at `247.729` reachable bytes/tuple; retained only as the rejected performance branch.
- 🟩 [x] Encoded-sort/native recovery completed in `11.692 s`, preserved 11 indexes and `245,204` tuples, repaired three unordered scalar/date indexes, and produced `25,317,135` bytes with exact scalar/date topology near 22-26 bytes/tuple.
- 🟥 [!] The environment safety layer blocked deletion of the exact failed 4.17 GB disposable target after resolved-path validation. No workaround was attempted; the retained path is `E:\VSProjects\LibraDex\artifacts\p4-wherzit-recovery-20260826-1402\WHERZIT-compacted-copy.lbdx`.
- 🟩 [x] A deterministic reversed-input recovery fixture now forces the unordered path for 10,000 tuples and proves exact sorted traversal after recovery. The complete `ss8-8-sorted-build-sanity` gate passed with `1,019,904` reachable bytes (`20.398` bytes/tuple), `17.601 ms` native storage, `218.035 ms` wall time, `1,562.405 ms` mutation control time, and `1,032,517 -> 1,032,523` bytes across compaction.
- 🟩 [x] The preserved/live Wherzit files were SHA-256 identical (`D03049EC59503E06979DA5F68F10A9C83E0E45F0282309FD53CE2F56CA6F5305`). Exact assessment found `4,171,728,001` physical bytes, `22,281,969` reachable bytes, `4,149,446,032` unreachable bytes, `187.224388x` amplification, 11 supported physical components, and no unsupported shapes.
- 🟩 [x] Native recovery on a copied catalog preserved all 11 indexes and `245,204` tuples, repaired three legacy unordered scalar/date indexes, and produced `E:\VSProjects\LibraDex\artifacts\p4-wherzit-recovery-20260826-1412\WHERZIT-compacted-native-recovery.lbdx`: `25,317,135` physical bytes, `18,005,775` reachable bytes, `7,311,360` bounded unreachable bytes, and `1.406056x` amplification in `11.692 s`.
- 🟩 [x] A clean current-writer control measured `31,624,167` physical bytes, `25,309,235` reachable bytes, `6,314,932` unreachable bytes, and `1.249511x` amplification. The similar 6-7 MB residual in the recovered copy is bounded VS8 first-build version history, not recurrence of the 4.15 GB runaway-growth defect.
- 🟩 [x] BLX therefore rejects native VS8 construction as part of this workstream solely to remove 6-7 MB. It would require variable extents, duplicate terminals, projections, null/empty routes, and detached publication; it remains the next-best branch only if a tighter text-index size target materially justifies that format work.
- 🟩 [x] Final focused LibraDex regression passed mixed-prefix ordering (`1,819` tuples, zero live/reopen inversions), detached replacement/compaction (`2.482x -> 1.000x`), 7,000-row duplicate and terminal-locality gates, catalog compaction, failure rollback, repack, concurrency contract, and primitive concurrency.
- 🟩 [x] Final public API snapshot verification passed, LibraDex `git diff --check` passed, Abraxas scoped `git diff --check` passed excluding only the preexisting user-owned `AGENTS.md`, and workstream-owned untracked files contain no trailing whitespace.
- 🟩 [x] Final Release/x64 builds passed: Wherzit `artifacts/build-wherzit-release-x64-p4-recovery-final-20260826-141520.log`; Abraxas harness `artifacts/build-abraxas-test-harness-release-x64-p4-recovery-final-20260826-141644.log`. Both completed with zero errors; Wherzit warnings and the harness's 1,605 warnings are preexisting project/dependency warning backlogs.
- 🟩 [x] Final `wherzit-store-lifecycle-sanity` passed. The optimized 50,000-record grouped backfill produced 250,000 tuples in `3.129 s`, used a bounded `20,480/20,480` tuple high-water/capacity, and passed ST/MT parity, mixed-null spill, legacy A/B, reopen, and compaction (`28,133,398 -> 27,479,234` bytes).
- 🟩 [x] P0-P4 are accepted on the current dirty trees. The live Wherzit store was never opened for write, compacted, replaced, or otherwise mutated by this workstream.
- 🟨 [~] Handoff boundary: applying recovery to the live Wherzit catalog remains an explicit user decision. The validated compacted copy above is the recovery candidate; do not replace the live catalog without a separate request and a fresh backup/hash check.
- 🟩 [x] P5 exact-stem publisher slice compiled in Release/x64 for both `LibraDex.csproj` and `LibraDex.Harness.csproj`; logs: `artifacts/build-libradex-release-x64-route-locality-20260826.log` and `artifacts/build-harness-release-x64-route-locality-20260826.log`.
- 🟩 [x] Existing direct transform proofs passed for `SS8-8`, `SS16-8`, `FS32-8`, `SS8-16`, and `SS16-16`; each retained the source offset as the child router, produced two distinct replacement shelves, preserved expected counts, and routed both split sides.
- 🟥 [!] The first guarded `SV8` routed run exposed a depth-7 shelf spanning multiple depth-7 parent prefixes. The prior deeper-transform search began at depth 8 and could therefore publish a false exact stem. The correction routes divergence-at-owner to same-depth parent refinement and reserves rejection for divergence before the owner; the exact `sv8-routed-sanity` rerun passes.
- 🟩 [x] Depth-3 adversarial walked transforms pass for `SS8-8`, `SS16-8`, `FS32-8`, `SS8-16`, and `SS16-16`. Each wrote exactly one additional 4-KiB intermediate router, routed only observed stem `0x22`, proved sibling routes `0x21` and `0x23` unset, and reached the correct depth-3 shelf.
- 🟩 [x] The new `FS32-16` depth-3 walked transform passes live and after reopen. It preserves both owning ancestors, publishes only observed stem `0x22`, keeps `0x21`/`0x23` unset, and routes the inserted tuple to the expected right shelf at depth 3.
- 🟩 [x] The `var-key-exact-stem-sanity` proof passes for `VS8`, `VS16`, and `VV` with an eight-byte common prefix. It stops at each family's first transform (`1,812`, `1,672`, and `1,180` rows respectively), walks every intermediate router live and after reopen, and proves only `0x35` is populated while `0x34`/`0x36` remain unset.
- 🟩 [x] Variable-key regression after the complete-prefix planner hardening passed `var-key-exact-stem-sanity`, `vs8-routed-sanity`, `vs16-routed-sanity`, and `vv-routed-sanity`. Build logs: `artifacts/build-harness-release-x64-p5-var-key-exact-stem-offsets-20260826.log` and `artifacts/build-harness-release-x64-p5-var-key-global-order-20260826.log`.
- 🟩 [x] `SV8` ordinary and 5,000-row exhausted-key routed sanity pass after local terminal deletion. The directed cross-shape proof then passed 500 identities across 39 terminal shelves per shape: `SV8` deleted in `8.623 ms` at a constant `274,432` bytes; `VV` deleted in `9.883 ms` at a constant `577,536` bytes. Both batch-abort probes restored all 500 rows and both reopened empty with first/tail endpoints zero.
- 🟩 [x] Terminal local-delete build logs: `artifacts/build-harness-release-x64-p5-terminal-var-delete-locality-20260826.log`, `artifacts/build-harness-release-x64-p5-terminal-var-delete-proof-fix-20260826.log`, `artifacts/build-harness-release-x64-p5-terminal-var-delete-hop-budget-20260826.log`, and `artifacts/build-harness-release-x64-p5-terminal-var-delete-abort-20260826.log`.
- 🟩 [x] Release/x64 proof logs: `artifacts/build-harness-release-x64-p5-fixed-planners-20260826.log`, `artifacts/build-harness-release-x64-p5-active-variable-planners-20260826.log`, `artifacts/build-harness-release-x64-p5-sv8-same-depth-20260826.log`, and `artifacts/build-harness-release-x64-p5-deep-stem-telemetry-20260826.log`.
- 🟨 [~] Exact-stem publication and first-difference/global-order planner hardening are source-complete across the audited fixed, FSN, scalar-key/variable-identity, and variable-key families. Next action: replace the known `SV8`/`VV` full-terminal-chain exact-delete paths with shelf-local mutation and add write/reachability proportionality proofs before expanding the remaining all-shape adversarial matrix.
- 🟩 [x] Added exact fixed-family, FSN-family, and variable-family active-topology walkers. The variable walker covers `VS16`, `VV`, `SV8`, and `SV16`, including scalar/variable payload arenas, null/empty key-state routes, terminal variable-identity roots/shelves, and claimed-offset alias validation.
- 🟩 [x] Added three directed few-thousand-row storage gates. At 4,096 rows the fixed-family gate reports `2,545,616` physical bytes, `2,406,352` reachable bytes, and `1.057873x` amplification; the FSN gate reports `3,552,289`, `3,183,649`, and `1.115792x`; both reject more than `4x` amplification, `4,096` reachable bytes/live tuple, or `128 MiB` per shape.
- 🟩 [x] The 4,096-row variable-family gate passes with `2,813,952` physical bytes, `2,297,856` reachable bytes, and `1.224599x` whole-gate amplification. Per shape: `SV16` is `177` reachable bytes/tuple at `1.000000x`; `SV8` is `187` at `1.000000x`; `VS16` is `66` at `1.681159x`; `VV` is `119` at `1.647541x`. No shape approaches the gross-growth release ceiling.
- 🟩 [x] The strengthened variable gate exposed and corrected a `VV` same-key terminal-conversion alias defect. Terminal conversion now publishes unset cold branches, repoints every exact parent alias to the terminal chain, releases the source shelf, and lets later neighboring keys allocate independent shelves lazily. The unsafe mixed-shelf direct extraction shortcut is disabled; the general ordered transform separates key ranges before terminal conversion.
- 🟩 [x] Post-correction regressions pass: fixed/FSN/variable 4,096-row gates, `FS32-16` walked transform, variable exact-stem routing, shelf-local terminal deletion for `SV8`/`VV`, reopen parity, and the public-surface API sanity family.
- 🟥 [!] An additional 8,192-row variable stress run found a separate `VS16` format gap at approximately `3,197` identical keys: the 128-KiB ordinary shelf exhausts, but a scalar-16 identity duplicate run has no exact-key terminal representation and therefore cannot be split at a real key transition. This is a correctness/public-readiness blocker, not evidence of renewed structural file amplification.
- 🟩 [x] Added the persisted `VS16` exact-key scalar-16 terminal representation. It reuses the terminal variable-identity chain with canonical 16-byte scalar identity encoding under distinct terminal-root shape `5`; insert, count, direct/cursor range read, exact/range delete, local mutation, batch abort, topology assessment, optimizer rebuild, and reopen paths recognize the new shape.
- 🟨 [~] Current restart point: the shared fixed-key identity-8 terminal contract is implemented and proved for `SS16-8` and `FS32-8`; implement the sibling fixed identity-16 contract for `SS8-16`, `SS16-16`, and `FS32-16`, then resume the default all-shape adversarial matrix. Identity-16 shapes should reuse terminal variable-identity shelves with canonical 16-byte scalar encoding under one distinct fixed identity-16 terminal-root shape; retain the root's existing key-length field to cover 8/16/32-byte keys without three duplicated formats.
- 🟩 [x] Final current-binary rerun passed `public-api-snapshot` plus all three 4,096-row proportionality gates at `2026-08-26 16:22:11 -07:00`. Aggregate physical/reachable amplification remained `1.057873x` fixed, `1.115792x` FSN, and `1.224599x` variable; the regenerated public API snapshot is unchanged.
- 🟩 [x] The new `VS16` 8,192-row duplicate-pressure proof passes with exact live/reopened tuple parity and exact terminal accounting: `905,216` physical bytes, `708,608` reachable bytes, `85` reachable bytes/tuple, `1.277457x` amplification, 19 routers, eight ordinary shelves, one terminal root, and 22 terminal shelves. The complete variable-family file reports `4,112,384` physical, `3,592,192` reachable, and `1.144812x` amplification.
- 🟩 [x] The strengthened `VS16` consumer proof exercises an aborted hot-terminal insert and verifies the count is unchanged, then covers cursor/direct range parity, exact deletion, range deletion, topology recount, optimization, and reopen. The fixture requires a terminal root only when its concentrated duplicate half exceeds ordinary-shelf capacity, avoiding the earlier incorrect 4,096-row test assumption.
- 🟥 [!] The optimizer-mode fixture initially exposed two stale harness assumptions: a hardcoded 16-hop insertion limit rejected its deliberate 48-byte common stem, and a maximum optimized route depth of two rejected a valid immediate-divergence expanded router. Production uses a 128-hop contract; the fixture now uses that shared default and accepts the proved depth-three automatic topology.
- 🟥 [!] The stronger optimizer proof then exposed a real `VS8` read-path split: structural traversal and the cursor reached all 12,000 tuples, but direct range-copy returned zero because its expanded one-byte traversal did not collapse a contiguous same-target prefix run. `VarKeyScalar8RangeReader` now applies the already-correct `VS16` alias-collapse rule, and both session direct-copy entry points delegate to their native cursors so traversal semantics cannot drift again.
- 🟩 [x] `varlen-optimizer-modes-sanity` and `varlen-optimizer-lifecycle-sanity` pass for `VS8` and `VS16` at 12,000 tuples with exact checksums. On-demand topology compresses to two routers/depth two; automatic topology retains four routers/depth three. The hardened source queues one candidate per family, so telemetry assertions now validate exact considered/covered relationships instead of the stale `>= 2` candidate assumption.
- 🟥 [!] `catalog-compaction-sanity` exposed a sibling `FSN-8` ownership defect: a depth-two direct parent could sparsely alias several prefixes to one shelf, but the full-shelf transform claimed ownership beginning at depth three. The first correction assumed a contiguous alias range and still failed; the accepted implementation scans the complete 256-entry parent, refines only entries still targeting the full shelf at the same depth, and leaves unrelated/gap routes untouched.
- 🟩 [x] BLX/LXL of sibling shapes found the same invalid `parent depth + 1` assumption in `FSN-16` and fixed-key/variable-identity. Both now use the alias-aware same-depth parent-repoint fast path. A directed 4,096-tuple-per-shape gate cycles all 256 values at the first divergent byte, repeatedly refills sparse aliased descendants, and proves count/materialization parity live and reopened for both families.
- 🟩 [x] Current FSN storage remains proportional after the sibling fix: 4,096 tuples per shape produce `3,552,289` physical bytes, `3,183,649` reachable bytes, and `1.115792x` amplification. Reachable bytes/tuple are `258` for `FSN-16`, `242` for `FSN-8`, and `274` for `FSN-V`.
- 🟩 [x] Both catalog compaction gates pass after the generalized FSN correction: ordinary compaction reclaims `5,001,546` bytes (`7,438,039 -> 2,436,493`) with parity, and injected failures restore the original file both before and after replacement.
- 🟩 [x] Release/x64 build after the final directed alias-pressure gate passed with zero errors: `artifacts/build-harness-release-x64-p5-fsn-alias-pressure-20260826-183218.log`. Earlier focused logs retain the intermediate compile/test evidence, including `artifacts/build-harness-release-x64-p5-var-key-copy-consolidation-20260826-181554.log`, `artifacts/build-harness-release-x64-p5-optimizer-candidate-contract-20260826-181940.log`, and `artifacts/build-harness-release-x64-p5-fsn-alias-set-20260826-182444.log`.
- 🟩 [x] Final current-binary matrix passes `public-api-snapshot`, fixed-family 4,096, FSN-family 4,096 plus directed sparse-alias pressure, variable-family 4,096 and 8,192, exact variable-key stems, `VS16` routed/index API, optimizer modes/lifecycle, FV routed 4,096, catalog compaction, and compaction failure recovery.
- 🟥 [!] The environment safety layer continues to block deletion of expired `.code-history` snapshots. Required pre-build snapshots were still created before every compile; the final snapshot timestamp was `20260826-183218` with 107 dirty source/config/project files. No workaround or broad deletion was attempted.
- 🟨 [~] Exact next action: add a table-driven all-public-shape adversarial suite covering ascending, descending, alternating-edge, fixed-seed shuffled, concentrated duplicates, and mixed neighboring keys. For each family require exact live/reopened tuple order, no missing/duplicate tuples, exact topology counts, physical/reachable amplification below four, and compaction parity. Keep the persisted exact-stem compressed-router branch as a measured next-best optimization only if the temporary one-byte stem chain materially affects mutation benchmarks.
- 🟩 [x] Added the first table-driven public fixed-family adversarial gate. It uses 4,096 tuples per selected shape, a concentrated duplicate-key half plus broad neighboring keys, ascending/descending/alternating-edge/fixed-seed-shuffled insertion orders, independently sorted expected tuples, exact public cursor parity live/reopened, and complete topology/amplification checks. `--adversarial-shape` isolates one family while the default remains the complete all-shape gate.
- 🟩 [x] `SS8-8` passes all four hostile patterns with exact live/reopened order. Every pattern produced the same `890,856` physical bytes, `366,568` reachable bytes, and `2.430261x` amplification, confirming its terminal representation removes insertion-order-dependent persistent growth.
- 🟥 [!] The isolated sibling results confirm five format gaps rather than one planner bug: `SS16-8` fails on duplicate ordinal `1,259`; `SS8-16` at `1,259`; `SS16-16` at `721`; `FS32-8` at `974`; and `FS32-16` at `818`. Each failure is the first tuple beyond that ordinary shelf's same-key capacity and reports no globally ordered key-prefix transition, which is correct because all source keys are identical.
- 🟩 [x] BLX rejects three inferior branches: forcing a false router transition would corrupt ordering/ownership; rejecting `IndexKeys.NonUnique` would break the public low-friction contract; and adding shape-specific linked ordinary-shelf formats would duplicate five mutation/read/delete/topology implementations. The preferred branch is two shared terminal identity encodings selected by identity width, with the fixed key bytes retained once in the terminal root.
- 🟩 [x] Fixed-terminal implementation checklist: persisted fixed identity-8/identity-16 root shapes, all five route classifiers/walkers, same-key conversion, local terminal mutation, neighboring-key exact-stem separation, cursor read/count, exact/range/delete-all, durability-batch mutation, topology accounting, and compaction parity are implemented and exercised by the default gate.
- 🟩 [x] The diagnostic-selector Release/x64 build passed with zero errors: `artifacts/build-harness-release-x64-p5-fixed-adversarial-filter-20260826-184450.log`. The default fixed-family command intentionally remains red at `SS16-16` ordinal 721 until the shared terminal contract is implemented; the earlier distinct-key storage phase still passes before the red extension.
- 🟩 [x] Added persisted fixed-key scalar-eight terminal-root shape `6`. `SS16-8` and `FS32-8` now store the fixed key once and reuse the shared terminal identity-8 shelf chain for duplicate identities; route classifiers/walkers, same-key conversion, local append/ordered insert, neighboring-key exact-byte router separation, direct/cursor traversal, count, topology accounting, and reopen recognize the representation.
- 🟩 [x] Fixed-key identity-8 cursor projections preserve forward and descending tuple order without duplicating keys in persisted storage. Exact delete routes back to the terminal chain rather than rewriting a synthetic cursor shelf.
- 🟩 [x] `SS16-8` and `FS32-8` writer contexts now stage, publish, abort, release ownership for, and invalidate caches for shared terminal identity shelves. The adversarial fixture deletes and restores ordinal zero from the concentrated terminal duplicate run after every pattern; a failed delete would make the required restore insert report `AlreadyPresent` and fail the gate.
- 🟩 [x] `SS16-8` passes ascending, descending, alternating-edge, and fixed-seed shuffled at 4,096 tuples with exact live/reopened cursor parity, exact delete/restore, `366,568` physical bytes, `337,896` reachable bytes, and `1.084855x` amplification.
- 🟩 [x] `FS32-8` passes the same four-pattern gate at 4,096 tuples with exact live/reopened cursor parity, exact delete/restore, `448,488` physical bytes, `411,624` reachable bytes, and `1.089557x` amplification.
- 🟩 [x] Final fixed identity-8 Release/x64 build passed with zero errors: `artifacts/build-harness-release-x64-fixed-terminal8-publish-20260827-003739.log`. Final gate logs: `artifacts/fixed-terminal8-ss16-8-final-20260827-0038.log` and `artifacts/fixed-terminal8-fs32-8-final-20260827-0038.log`.
- 🟩 [x] Added persisted fixed-key scalar-sixteen terminal-root shape `7`. `SS8-16`, `SS16-16`, and `FS32-16` share the existing canonical 16-byte variable-identity terminal shelves while retaining each fixed key once in the root; no parallel per-shape terminal shelf formats were introduced.
- 🟩 [x] The three identity-16 families now recognize terminal roots during route walking, convert exhausted same-key shelves before invalid prefix splitting, insert matching identities locally, separate neighboring keys through exact-byte routing, project terminal rows through public cursors in both directions, route exact/range/delete-all mutations to persisted terminal shelves, and report exact topology counts.
- 🟩 [x] Fixed cursor projection buffer ownership is consistent with each reader: pooled readers retain pooled synthetic shelves, cache-backed fixed-32 readers retain ordinary arrays, and all projected identities preserve canonical key-then-identity ordering.
- 🟩 [x] The default six-shape gate passes all 4,096-tuple ascending, descending, alternating-edge, and fixed-seed shuffled patterns with exact live/reopened order and exact terminal delete/restore for every terminal-capable family. The former `SS16-16` ordinal-721 failure is closed.
- 🟩 [x] Every adversarial pattern now runs closed-file compaction parity across all six fixed shapes. Each 24,576-tuple catalog compacted to `1,988,608` bytes while preserving six logical indexes and all tuples; pre-compaction amplification remained `1.328362x`-`1.332330x`.
- 🟩 [x] Final fixed identity-16 Release/x64 harness build passed with zero warnings and zero errors: `artifacts/build-harness-release-x64-fixed-terminal16-delete-20260827-0738.log`. The full delete/reopen/topology/compaction gate passed at `2026-08-27 07:38:56 -07:00`.
- 🟥 [!] The environment safety layer still blocks automated pruning of expired `.code-history` files. Every compile received a fresh verified snapshot of all 115 dirty source/config/project files; no unsafe workaround or broad repository deletion was attempted.

## 2026-08-27 Wherzit During-Ingestion Convergence Repair

- 🟩 [x] Reproduced the matrix stop on an untouched copy of Wherzit's 49,057-record store. `BindLibraDexIdentityIndexes` failed while automatically repairing `.CreationTime`; its SS8 transform reported a routed-prefix ownership violation with `FirstDifferentDepth=3`, `FirstOwnedDepth=5`, and 1,819 source tuples.
- 🟩 [x] Established the two-layer root cause. A historical SS8 transform left a stale shared-route alias in the persisted index topology, and Abraxas's automatic repair used logical `DeleteAll` plus reinsertion, which removed tuples but retained the poisoned router topology for the deterministic retry.
- 🟩 [x] BLX selected fresh packed candidate generation at unreachable offsets followed by stable-root publication. This preserves the public index identity and LibraDex model while replacing the complete derived topology atomically. Repairing the poisoned graph in place remains the next-best branch but has greater correctness complexity and retains fragmented historical extents; weakening the routed-prefix invariant was rejected.
- 🟩 [x] Added a native stable-root replacement contract to the LibraDex sorted-build surface and routed Abraxas simple-index convergence repair through it. Unsupported/native-ineligible cases retain the prior clear-and-reinsert fallback.
- 🟩 [x] Added exact index/path/key-type/record-ID exception context in normal upsert and convergence repair paths. Wherzit's matrix report now retains the complete exception chain instead of displaying only the outer message.
- 🟩 [x] The exact copied Wherzit store recovered successfully after the change: all 49,057 records converged and the probe exited zero. The original user store was not mutated by this proof.
- 🟩 [x] Focused LibraDex regressions passed: detached replacement, 1,819-row mixed-prefix ordering with zero live/reopen inversions or omissions, walked transform, parent-route split, and a 50,000-row duplicate run. `FailedGates=0` for this affected set.
- 🟨 [~] A synthetic 49,057-record Wherzit-shaped single-thread/during-ingestion proof inserted all records and crossed the original 28,528-record failure boundary without a mutation/convergence failure.
- 🟥 [!] The synthetic proof's subsequent full sorted-reader validation exhibited a separate resource-amplification defect: even an ID-only traversal reached approximately 18.96 GB working set. The exact harness process was stopped to protect the machine. This is not the repaired mutation failure and requires a separate reader/routing investigation before that aggregate proof can be called green.
- 🟥 [!] The broad `ss8-8-sorted-build-sanity` command progressed past its SS8 build portion but stopped on a separate mixed-storage-assessment assertion (`complete=True`, `unsupported=0`, physical/reachable delta 329 bytes). It is not evidence against the focused replacement repair and remains a distinct gate discrepancy.
- 🟩 [x] Canonical clean Release/x64 solution rebuild passed with exit code zero after targeted `bin`/`obj` cleanup. The final binaries then recovered a new disposable copy of the exact 49,057-record Wherzit store in 13.3 seconds with exit code zero.
- 🟩 [x] Relaunched Wherzit from the final Release/x64 build without computer-use. PID 56988 reached its `WHERZIT!` main window after opening/converging the live store.
- 🟨 [~] Exact next action after the Wherzit matrix rerun: confirm all eight meaningful shapes complete, then isolate the ID-only full-reader memory amplification with allocation evidence before changing its traversal implementation.

## 2026-08-27 Wherzit Sorted-Reader Memory Repair

- 🟩 [x] Isolated the resource defect with a clean-process `wherzit-reader-memory-probe` that inserts Wherzit-shaped records, optionally creates only `.FullName`, forces GC, opens `Readers.OpenSorted(".FullName")`, and traverses IDs without retaining identities or materializing records.
- 🟩 [x] Pre-fix measurements proved quadratic preparation allocation: 1,000 rows allocated `209.65 MiB`, 2,000 allocated `815.97 MiB`, and 4,000 allocated `3,130.17 MiB`; the broader 49,057-record traversal had previously reached approximately `18.96 GiB` working set before being stopped.
- 🟩 [x] Established the two-layer root cause. Abraxas rejected the already-maintained logical string index solely because the facade reports multiple physical projection keys per identity, so exact `.FullName` sorting unnecessarily entered the private fallback. That fallback inserted each record immediately into a one-part routed composite; each insertion path-copied and re-encoded the widening root, producing `1 + 2 + ... + N` cumulative root work and storage.
- 🟩 [x] LXL at 4,000 distinct strings proved approximately eight million cumulative root-child encodes in the old path. The accepted path performs one payload extraction per fallback row, one detached O(N) candidate construction, and one publication; indexed exact-string traversal opens the maintained exact identity stream directly and performs no fallback construction.
- 🟩 [x] BLX selected a combined repair: reuse an existing exact string index when coverage is complete, and add detached unordered composite replacement for genuine fallback orders. A direct-index-only change was rejected as incomplete because it would hide rather than repair the quadratic fallback. A separate .NET sort/comparer was rejected because it would duplicate LibraDex ordering semantics. In-place routed-tree mutation remains the next-best deeper optimization only if linear fallback CPU/allocation becomes material.
- 🟩 [x] `LibraDexRoutedCompositeIndex.ReplaceFromUnordered(...)` now builds a detached candidate without per-entry persistence, encodes/publishes one durable snapshot, and swaps the stable runtime root only after successful publication. Cancellation or candidate/publication failure leaves the current tree unchanged.
- 🟩 [x] `LibraDexStringScalar8Index.IterateExactIdentities(...)` exposes the complete exact logical population without projection duplicates. Abraxas exact-sort planning now accepts the string facade by construction and opens that exact identity source while retaining existing physical-direction adjustment.
- 🟩 [x] Prepared fallback sorting now buffers key/identity tuples and invokes the single-publication replacement once. It still uses one authoritative LibraDex ordering implementation and still returns an immutable identity snapshot before disposing its private memory catalog.
- 🟩 [x] Post-fix indexed preparation allocation at 1,000/2,000/4,000/12,000 rows is `0.15/0.28/0.28/0.28 MiB`; the actual 49,057-row indexed case allocates `0.28 MiB` during preparation, adds `1.62 MiB` working set, and completes ID-only traversal in `2.126 s`.
- 🟩 [x] Post-fix fallback allocation is linear: `23.92/47.48/95.16/284.79 MiB` cumulative allocation at 1,000/2,000/4,000/12,000 rows. The 49,057-row no-index worst case completes in `93.953 s` with `1,185.72 MiB` cumulative allocation but only `62.62 MiB` managed and `55.34 MiB` working-set growth during preparation; after GC it retains `14.35 MiB` managed. This is bounded but remains the explicit next-best optimization target if no-index sort latency matters.
- 🟩 [x] Added direct exact-string ascending/descending duplicate-order coverage and durable bulk-replacement/reopen coverage that proves the pre-replacement stale tuple is absent. `abraxas-sorted-reader-sanity` and `abraxas-data-reader-sanity` passed.
- 🟩 [x] The complete `wherzit-store-lifecycle-sanity` passed after the repair: the former 49,057-row during-ingestion failure shape converged all five indexes; 50,000-row grouped post-ingestion backfill published 250,000 tuples in `6.777 s` with a `20,480`-item bounded peak; optimized/legacy A/B, reset/repopulate, recovery, reopen, and compaction checks also completed.
- 🟩 [x] Incremental Release/x64 solution builds passed with zero errors. Evidence logs: `E:\VSProjects\AbraxasDB\artifacts\build-solution-release-x64-reader-memory-fix-20260827.log`, `build-solution-release-x64-reader-correctness-20260827.log`, and `build-solution-release-x64-reader-probe-50k-20260827.log`.
- 🟩 [x] Final clean Release/x64 solution rebuild passed with zero errors after a solution-scoped MSBuild `Clean`; the host safety layer rejected direct verified `bin`/`obj` deletion before execution. Final log: `E:\VSProjects\AbraxasDB\artifacts\build-solution-release-x64-reader-memory-final-20260827.log`.
- 🟩 [x] The exact final binary passed `abraxas-sorted-reader-sanity`, then traversed 49,057 indexed rows in `1.182 s` with `0.28 MiB` preparation allocation and `1.64 MiB` working-set growth.
- 🟩 [x] The exact final binary passed the complete `wherzit-store-lifecycle-sanity` again. Its 50,000-row grouped backfill published 250,000 tuples in `5.968 s` with a `20,480`-item peak, followed by successful optimized/legacy A/B and compaction checks.
- 🟨 [~] Exact next action: have the user rerun the eight meaningful Wherzit matrix shapes from the relaunched final binary. Treat further fallback allocation/latency reduction as a separate core optimization rather than a blocker for indexed Wherzit scrolling.

## 2026-08-27 Wherzit Per-Item SS8 Parent-Alias Repair

- 🟥 [!] The eight-shape Wherzit matrix stopped on shape 3, `Single Thread | Post-ingestion | Optimized bounded | Per-item control`, after `12.7916811` seconds of grouped five-index construction. `LastWriteTime` reached 49,057 source records before `SS8-8` rejected a shelf with `FirstDifferentDepth=2`, `FirstOwnedDepth=4`, and 1,819 tuples (`0x1FA084E7F3000000` through `0x1FA0F6F8B0000000`).
- 🟩 [x] Fresh-generation verification excludes historical RecordBase residue: every matrix shape calls Wherzit's whole-generation `RecreateFileRecordStore` before ingestion and index creation.
- 🟩 [x] LXL identified the current writer defect. A direct parent may fan several prefix entries into one shelf. When the full shelf contains only one of those parent prefixes, same-depth splitting correctly declines and the shelf offset is transformed in place into a deeper router; sibling parent aliases still targeting that physical offset then enter a subtree whose claimed stem belongs only to the populated prefix.
- 🟩 [x] BLX selected same-publication parent-alias isolation before the in-place `SS8-8` shelf-to-router transform. Retain the selected prefix, clear every sibling direct route still targeting the source shelf, validate parent depth/target ownership, and publish the parent rewrite with the replacement shelves and transformed child router. This is the SS8 counterpart of the accepted FSN sparse-alias ownership rule.
- 🟥 [!] Weakening the routed-prefix invariant, omitting the per-item matrix control, or silently replacing the per-item run with the native packed builder are rejected because each would hide a public mutation defect or invalidate the A/B control.
- 🟩 [x] The alias-isolating transform publication, distilled Wherzit-date-stem nested-parent regression, focused live/reopen/order tests, full Wherzit lifecycle gate, and final Release/x64 Wherzit rebuild are complete.
- 🟩 [x] The distilled nested-parent proof passes with 1,820 exact tuples, zero live/reopen inversions or parity failures, stable transformed-router ownership, and an independently allocated sibling shelf. The output catalog is 151,552 bytes.
- 🟩 [x] The first focused telemetry rerun exposed one unconditional extra parent-router read. The implementation now inspects the already-decoded direct parent view and rereads the 8 KiB parent only when aliases actually require mutation; the ordinary walked-transform gate returned to its established five-read/40,968-byte shape.
- 🟥 [!] `ss8-8-route-batch-policy-sanity` stopped on a preexisting invalid harness setup: the second arena linked `0x00...` source keys beneath root route `0x01`, which the routed-prefix invariant correctly rejected. The fixture now offsets both second-arena keys and identities into the declared `0x01` root stem before construction.
- 🟩 [x] The stale 34-read route-batch assertion is resolved without normalizing it to 2. The former `Uncached` name actually combined two promoted router-page loads with per-key target classification; the current two-read behavior also promotes target kinds. The repair preserves that production path while restoring a genuinely raw backing-file control.
- 🟩 [x] Route-policy closure preserves the two-read promoted-direct-view production path, restores an unambiguous raw-file A/B control, and separates target/checksum correctness from cache/read-shape expectations. BLX rejected merely changing the 34-read constant because that would erase the uncached control; the selected narrow internal policy makes all cache layers explicit.
- 🟩 [x] LXL resolved the historical 34-read formula: it represented two promoted router-page loads plus two uncached four-byte target classifications for each of 16 keys, not a fully uncached route walk. Subsequent direct-route target-kind promotion removed those 32 classifier reads, leaving the observed two physical router/arena reads.
- 🟩 [x] Implementation names the normal path `PreferPromotedViews`, retains `PreferArenaCache`, and adds `RawFile`, which reads both router pages and both selected target magics for every key without consulting or populating route projections. Route-batch and batch-lookup sanities compare raw, promoted, and arena target/result parity independently from their physical read contracts.
- 🟩 [x] First discriminator execution confirmed the expected projected read counts but refined the byte model: promoted routing reads two 4 KiB router pages (`2` reads / `8,192` bytes), not one router page plus a full 32 KiB arena. Batched lookup adds one 32 KiB shelf read (`3` reads / `40,960` bytes). The raw-file contracts passed before the projected-byte assertions stopped.
- 🟩 [x] Second discriminator execution isolated the arena-aware contract: route batching uses one 4 KiB root page, one 32 KiB arena load, and one four-byte shelf classification per key (`18` reads / `36,928` bytes); batched lookup adds one 32 KiB shelf read (`19` reads / `69,696` bytes). Raw-file and promoted-view contracts both passed before the arena assertion stopped.
- 🟨 [~] Adjacent-gate audit found two more pre-promotion byte/read baselines: the promoted router-cache first touch is `4` reads / `8,200` bytes (two router pages plus two magic reads), and same-shelf promoted range scoop is `3` reads / `40,960` bytes (two router pages plus one shelf). Their target/order correctness remained green; cross-shelf and traversal read contracts remain to be measured after these first assertions are corrected.
- 🟩 [x] Router-cache sanity now passes. Cross-shelf promoted range routing measured `6` reads / `77,828` bytes (three router pages, two shelves, and one remaining four-byte classification), exactly two reads/eight bytes below its old baseline. The mixed routed-plus-direct-root generalized traversal independently retained its original `7` reads / `73,740` bytes because its three boundary/target classifications are not reusable.
- 🟩 [x] Consolidated route-policy gates pass: raw/promoted/arena route-batch parity, raw/promoted/arena point-lookup parity, router-arena cold/warm/non-entry caching, same/cross/traversal range scoop parity, walked transform, parent-route split, and the 1,820-tuple distilled Wherzit parent-alias reproduction with live/reopen/global-order parity.
- 🟩 [x] Concurrency gates pass: `concurrency-contract-sanity` and `ss8-8-primitive-concurrency-proof` completed with every expected count matching the actual count across direct, queued, generic batch, delete/rekey, FS32 projection, and VS8 projection lanes.
- 🟩 [x] Final `AbraxasDB.sln` Release/x64 clean rebuild completed with 0 errors. Build log: `E:\VSProjects\AbraxasDB\artifacts\build-solution-release-x64-route-policy-20260827-142752896.log`.
- 🟩 [x] Exact rebuilt `wherzit-store-lifecycle-sanity` passed after the route-policy closure, including 49,057-record realistic during-ingestion indexing, 50,000-record bounded spill/backfill, grouped ST/MT parity, reset/empty/small lifecycles, and legacy A/B indexing.
- 🟩 [x] Focused LibraDex gates pass: nested-parent deterministic live/reopen/global-order parity (1,820 tuples), route transform, walked transform (5 reads / 40,968 bytes), parent-route split, catalog compaction and restoration-after-failure, concurrency contract, and primitive SS8-8 concurrency proof.
- 🟩 [x] The final `AbraxasDB.sln` Release/x64 clean rebuild completed in `00:00:42.16` with 0 errors. Build log: `E:\VSProjects\AbraxasDB\artifacts\build-solution-release-x64-ss8-parent-alias-20260827-120113292.log`.
- 🟩 [x] The exact rebuilt `wherzit-store-lifecycle-sanity` passed with exit code 0. It covered grouped ST/MT parity, the 49,057-record realistic during-ingestion distribution, optimized bounded spill/backfill through 50,000 records, empty/small/reset lifecycles, and legacy A/B backfill.
- 🟩 [x] User verification: the complete eight-shape Wherzit performance matrix passed. Shape 3 (`Single Thread | Post-ingestion | Optimized bounded | Per-item control`) completed successfully, and the selected native-packed shape remained the fastest and smallest result.

## 2026-08-27 Abraxas Concurrent Ingestion Diagnostics

- 🟩 [x] The user established the governing acceptance contract: multi-thread ingestion exists to permit concurrent work safely; it does not need to outperform the single-thread throughput control.
- 🟩 [x] The eight-shape matrix provides the initial discriminator. Multi-thread RecordBase ingestion was consistently `6%`-`11%` slower than its matching single-thread run, while native packed post-ingestion indexing remained the clear production strategy.
- 🟩 [x] LXL of Wherzit's caller path proves that MT submits concurrent 256-record `PutMany` batches, while `PutMany` persists each record independently.
- 🟩 [x] LXL of Abraxas proves that every new record acquires the shared store-generation mutation monitor before identity allocation and retains it through serialization, Fractal persistence, maintained LibraDex mutations, and convergence-checkpoint publication. Nested entries are reentrant; competing callers cannot overlap that protected work.
- 🟩 [x] BLX Branch A — infer contention solely from elapsed ST/MT totals: rejected as insufficient because it cannot separate gate wait, useful mutation hold time, scheduling, allocation, or batch-latency fairness.
- 🟩 [x] BLX Branch B — use only process-wide runtime contention counters and an external trace: retained as a secondary corroboration path, but rejected as the primary proof because it cannot attribute waits and publications to one store-generation mutation lane.
- 🟩 [x] BLX Branch C — add an opt-in store mutation diagnostic session plus a controlled Wherzit-shaped harness: selected. Disabled operation must retain the existing hot path; enabled measurement records outermost gate wait/hold, contention, queue high-water, record work/allocation, phase timing, checkpoint publications, maintained-index targets, batch latency, and records per batch without changing mutation behavior.
- 🟩 [x] Implemented internal opt-in instrumentation without expanding AbraxasDB's public API. The disabled path retains the existing mutation monitor and adds only one null diagnostic-state read/branch; no writer ordering, persistence, checkpoint, or repair behavior changed.
- 🟩 [x] Release/x64 solution Clean and Rebuild passed with 0 errors. The pre-build `.code-history` snapshot captured 126 changed source/config/project files at `20260827-152039`; host policy blocked deletion of 516 verified stale history files, so none were removed.
- 🟩 [x] The enabled 49,057-record, three-pair series completed exactly. ST median was `00:00:14.9341622`; MT median was `00:00:14.4055957` (`0.965x`). The apparent MT edge is inside the observed run variance and is not treated as a throughput win because enabled per-record timing/interlocked counters materially perturb the hot path.
- 🟩 [x] The diagnostics-off control reproduced the Wherzit matrix direction. ST median was `00:00:13.6157700`; MT median was `00:00:15.4173485` (`1.132x`, or 13.2% slower). All samples retained exactly 49,057 records with 192 PutMany calls averaging 255.5 records each.
- 🟩 [x] Concurrent-work acceptance passed. Every MT sample observed 16 simultaneously active batch calls and 16-20 distinct worker threads while exact final cardinality and convergence held. MT therefore provides safe concurrent admission even though the protected writer remains deliberately single-owner.
- 🟩 [x] The dominant ST/MT difference is contention around the one correctness-critical store-generation mutation lane, not missing parallel writer work. The diagnostics-off MT control incurred `539`-`708` runtime monitor-contention events; MT batch p95 was `2.745`-`3.180` seconds versus ST `0.084`-`0.092` seconds.
- 🟩 [x] Enabled attribution corroborated the contention model: MT accumulated `181.795`-`202.052` seconds of overlapped gate wait with a queue high-water of 16, while serialized gate hold remained `13.166`-`14.954` seconds. Splitting or weakening this gate is rejected because it would trade convergence and deterministic mutation ordering for no proven wall-time benefit.
- 🟩 [x] The larger core opportunity is common to ST and MT. With no application indexes present, 49,057 records spent about `5.576`-`6.594` seconds in the post-Fractal LibraDex/convergence-checkpoint phase, `4.069`-`4.636` seconds in Fractal writes, and `1.808`-`2.308` seconds in serialization. Every run published exactly 49,057 convergence checkpoints despite zero maintained application-index targets.
- 🟩 [x] Allocation is a separate high-value efficiency signal: each 49,057-record sample allocated about `6.0 GiB` cumulatively (`~126 KiB/record`), with about `5.99 GiB` attributed inside record persistence. This is transient managed allocation rather than retained store size, but it warrants allocation-stack attribution before optimizing checkpoint or Fractal code by elapsed time alone.
- 🟩 [x] Preserve concurrency safety, deterministic identities, Fractal/LibraDex convergence, and low/no developer friction even if ST remains faster. MT throughput parity is not an acceptance requirement; concurrent admission and bounded, observable waiting are.
- 🟩 [x] The rebuilt `wherzit-store-lifecycle-sanity` completed with exit code 0 after instrumentation. It retained grouped ST/MT parity, the 49,057-record during-ingestion distribution, 50,000-record optimized bounded backfill, reset/recreate lifecycles, mixed-null scalar spill, and legacy A/B backfill.
- ⬜ [ ] Next-best branch, requiring a separate design/proof pass before implementation: attribute the `~6.0 GiB` allocation churn by stack and split the post-Fractal timing into no-op application-index work, source-USN read, checkpoint rekey/publication, and repair-intent cleanup. Then investigate whether PutMany can retain per-record durable repair intent while advancing the hidden applied-source-USN convergence checkpoint once per successful protected batch rather than once per record. Compare this against a Fractal native batch write branch; do not conflate either with MT scheduling.

## 2026-08-30 Abraxas Backfill Policy Surface Cleanup

- 🟩 [x] Governing policy: preserve distinct semantics and supported workflows, but do not expose slower implementations of identical semantics as ordinary developer choices.
- 🟩 [x] Keep regex/text-operation selection and the optional per-call FindFast worker ceiling public because they express query semantics or bounded resource intent; complete external-index routes continue collapsing to single-worker traversal automatically.
- 🟩 [x] Keep single-caller and concurrent `PutMany` admission as normal Abraxas behavior without introducing a global ingestion-mode switch. Wherzit's worker count and ingestion batch size remain caller-side diagnostic controls.
- 🟩 [x] Keep both index-maintenance workflows: indexes created before ingestion participate in every mutation, while indexes created after ingestion use authoritative backfill. The timing choice remains expressed by when the developer creates the index rather than by persistent RecordStore mode.
- 🟩 [x] `IndexBackfillOptions.Mode` and `UseNativeSortedBuild` are no longer public configuration. Ordinary callers receive optimized bounded backfill with automatic native packed eligibility; unsupported shapes retain automatic per-item fallback.
- 🟩 [x] Legacy full-materialization and forced per-item construction remain available only to friend diagnostic assemblies through one internal override method so Wherzit's matrix and Abraxas regression gates retain their correctness controls.
- 🟩 [x] Wherzit's normal `Options` menu retains text-filter semantics and the FindFast maximum-worker ceiling. Bulk-ingestion, index-build timing, backfill-engine, scalar/date-builder, and the full matrix moved under `Diagnostics`.
- 🟩 [x] Release/x64 solution `Clean` and `/restore /t:Rebuild` completed with zero errors. Focused `wherzit-store-lifecycle-sanity` passed its public-surface reflection guard, grouped ST/MT parity, 49,057-record during-ingestion distribution, 50,000-record optimized bounded backfill, mixed-null spill, and internal legacy A/B control.
- 🟩 [x] Relaunched the exact final Wherzit Release binary without computer-use (`PID 42652`). The diagnostics pipe reported ready with the existing store open and 49,057 records.

## 2026-09-03 VS8 owner-set recovery and mixed detached replacement

- 🟩 [x] The 253,950-row Wherzit failure was not a harmless route-batch bookkeeping discrepancy: one physical VS8 shelf appeared in separated direct-parent route runs, so a selected-run rewrite could not preserve the complete physical owner set.
- 🟩 [x] BLX selected complete root-owner-set reconstruction for legacy/noncontiguous ownership and retained serialized immediate replay only as the bounded active-durability-batch escape. Weakening the contiguity invariant or updating only the selected run was rejected.
- 🟩 [x] `TryPublishVarKeyScalar8DirectShelfTransform` now verifies the complete reachable owner set before its fast local transform. A shared or noncontiguous owner set returns to root-wide replacement instead of publishing an incomplete parent rewrite.
- 🟩 [x] `vs8-noncontiguous-owner-split-sanity` passes live/reopen parity and bounded batch-abort/replay with separated owner slots `0x10` and `0x12`.
- 🟩 [x] Cross-shape source review confirms VS16, VV, and SV16 copied-shelf growth already uses the shared complete-owner-set publisher; fixed-key and SV8 shapes do not share the VS8 variable-key direct-parent range transform. VS16/VV/SV16 shared-growth, VS16/VV/SV16 routed, varlen optimizer lifecycle, and 4,096-row variable-family topology/storage gates pass.
- 🟥 [!] The independent default `sv8-routed-sanity` currently returns range-delete count `109` rather than expected `131` after one exact delete. This is a separate SV8 range-delete regression and is deliberately left visible for its own LXL/BLX repair; it does not execute the VS8 alias or detached-build paths.
- 🟩 [x] Added a seekable native VS8 sorted source and a bounded spill/merge source in Abraxas. Exact forward text populations above `BatchSize` remain native and never fall back to per-item online shelf mutation.
- 🟩 [x] Added mixed detached native replacement for scalar `SS8-8` and exact string `VS8` siblings. Candidate roots remain unreachable until source/key-state/current-slot/count validation succeeds and one catalog-directory generation publishes all replacements.
- 🟩 [x] `mixed-native-detached-replacement-sanity --count 70000` passes with 70,131 scalar tuples, 70,257 path tuples, and 70,509 name tuples. An invalid final sibling and pre-publication cancellation preserve the prior generations live and after reopen.
- 🟩 [x] Abraxas `ReplaceIndexesAtomically` scans authoritative Fractal/Inheto records once, builds bounded sorted runs, invokes mixed detached publication, then reopens and publishes the replacement runtime descriptors together. Existing complete intent and handles remain authoritative until the directory commit.
- 🟩 [x] A real-path 204,901-record, five-index Abraxas fixture uses four bounded runs per index, then replaces all five in a second generation; both generations pass exact live/reopen identity parity.
- 🟩 [x] A disposable clone of Wherzit's real 258,919-record store passed IPC five-index replacement in `00:00:07.7795065`, exact identity parity, process restart, and direct `.Name` index query parity. The original corpus, failed store, and application settings were preserved; the clone was removed after acceptance.
- 🟩 [x] Final Release/x64 `AbraxasDB.sln` and LibraDex harness rebuilds completed with zero errors. Compaction and compaction-failure recovery gates also pass.

## Restart Instructions

1. Read this document completely.
2. Read `AGENTS.md`, `AGENT_CODING_STYLE.md`, and relevant entries in `LIBRADEX_DESIGN_CHECKLIST.md`.
3. Inspect `git status --short` in both `E:\VSProjects\LibraDex` and `E:\VSProjects\AbraxasDB`.
4. Resume the first 🟨 or ⬜ item in the earliest incomplete phase unless the results ledger explicitly names a different active task.
5. Inspect the current diff of every file before editing it.
6. Update the results ledger with commands, measurements, failures, and the exact next action before stopping.
