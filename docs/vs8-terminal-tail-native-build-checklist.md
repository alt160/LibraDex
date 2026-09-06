# LibraDex VS8 Terminal Tail And Native Build Checklist

## Purpose

This is the live workstream checklist for correcting variable-key/scalar-8 (`VS8`) exhausted-key terminal behavior and replacing Wherzit's temporary Attributes-order population loop with a shape-native sorted builder.

The governing priority is:

1. Correctness and durability are release gates.
2. Speed is the primary optimization goal.
3. Efficiency is secondary, including allocation, I/O, write amplification, persistent size, and locality.
4. Concurrency may multiply an efficient core later; it must not conceal an inefficient serial algorithm.

Update this file before beginning a phase, after every meaningful proof, after every rejected or stopped branch, and before ending a work session.

## Status Legend

- 🟩 [x] Done or accepted.
- 🟨 [~] Partial, in progress, or a parent with mixed child states.
- 🟥 [!] Failed, rejected, stopped, or a discovered defect.
- ⬜ [ ] Open or not started.

## Safety And Build Rules

- 🟩 [x] Use `E:\VSProjects` as the canonical workspace; `C:\VSProjects` is the same workspace through a junction.
- 🟩 [x] Preserve the broad user-owned dirty trees in LibraDex and AbraxasDB.
- ⬜ [ ] Inspect the current diff of every existing file before editing it.
- ⬜ [ ] Keep workstream edits narrowly attributable and do not reset, restore, clean, or overwrite unrelated changes.
- ⬜ [ ] Immediately before every C#/project/config compile, snapshot every changed source/config/project file into `.code-history` with timestamp and solution-relative path.
- ⬜ [ ] Prune `.code-history` entries older than four hours relative to its newest entry.
- ⬜ [ ] For Rebuild, remove only the targeted projects' `bin` and `obj` directories after the history snapshot.
- ⬜ [ ] Build Release/x64 with Visual Studio 18 Insiders MSBuild, `/m:1`, minimal console output, and a normal-verbosity file log.
- ⬜ [ ] Put large generated catalogs in a unique disposable directory and retain only compact proof output.

## Accepted Design Contract

- 🟩 [x] Do not add a separate full-shelf marker. `ItemCount == capacity` already answers fullness from the identity-8 shelf header.
- 🟩 [x] Do not move the next-shelf link into the slot array. `TerminalIdentity8ShelfLayout.NextShelfOffsetOffset` already provides a direct eight-byte header link.
- 🟩 [x] Activate the existing terminal root tail field rather than changing the persisted format.
- 🟩 [x] Preserve the exhausted-key representation: the exact key is stored once in the terminal root; identity shelves contain sorted identities without repeating the exhausted key.
- 🟩 [x] Use the existing VS8 optimizer's detached, version-checked publication model rather than introducing a second router architecture.
- 🟩 [x] Add a native VS8 sorted builder for the newly measured dogfood speed problem. The earlier decision to defer it solely for a small file-size saving no longer governs because Wherzit's 49,057-row Attributes order takes approximately 63 seconds through per-item insertion.
- 🟩 [x] Defer skip links, identity routers, or a new terminal format until much larger random-mutation evidence proves the simple tail-plus-local-shelf design insufficient.

## BLX Branch Record

### Terminal Tail

- 🟩 [x] Branch A — add a full marker and slot-array next pointer: rejected as redundant format complexity.
- 🟩 [x] Branch B — scan from the first shelf for every append: rejected as avoidable O(shelf-count) work when the root already owns a tail field.
- 🟩 [x] Branch C — maintain and validate the existing root tail offset: selected. The hot append reaches the last shelf directly; legacy zero-tail roots require one bounded repair walk during mutation or maintenance.
- 🟨 [~] Branch D — add a transient batch append cursor: reserve as a measured follow-up only if the corrected root/shelf caches do not make native or repeated append sufficiently direct.

### Native VS8 Build

- 🟩 [x] Branch A — continue ordinary `Insert` for every tuple: rejected as the production builder for bulk order construction because it repeats route discovery, mutation, encoding, and publication work.
- 🟩 [x] Branch B — sort in .NET and bypass LibraDex ordering: rejected because it duplicates LibraDex key semantics and turns the dogfood path into a separate ordering engine.
- 🟩 [x] Branch C — build terminal shelves and VS8 routers directly from sorted encoded tuples, then version-check one publication: selected.
- 🟨 [~] Branch D — concurrent subtree/index builders: retained as a later multiplier, not selected before the serial core meets correctness, speed, and efficiency gates.

## Phase T0 — Current-Source Audit And Baseline

- 🟩 [x] Read repository instructions and the complete canonical SS8/backfill checklist.
- 🟩 [x] Inspect the relevant LibraDex and Abraxas dirty-tree inventory without modifying or cleaning it.
- 🟩 [x] Confirm terminal identity-8 shelf header fields: item count and next-shelf offset.
- 🟩 [x] Confirm terminal identity root fields: first-shelf offset, shelf extent size, and tail-shelf offset.
- 🟩 [x] Confirm current VS8 append starts at the first shelf and does not publish root tail updates.
- 🟩 [x] Confirm current VS8 rewrite builds a forward shelf chain but leaves the terminal root tail zero.
- 🟩 [x] Confirm local insert/split/delete paths do not consistently maintain the root tail.
- 🟩 [x] Confirm current topology assessment does not validate the terminal root tail.
- 🟩 [x] Confirm VS16 and fixed scalar-16 terminal paths already demonstrate the intended first/tail lifecycle.
- 🟩 [x] Confirm the VS8 optimizer lacks an all-equal-key terminal branch before divergence search.
- 🟩 [x] Confirm Wherzit fallback population calls the temporary LibraDex target once per key/identity tuple.
- 🟩 [x] Record measured baseline: 49,057 Attributes rows, approximately `63.1369766 s` ordering/list, `775 records/s`.

## Phase T1 — Identity-8 Terminal Tail Lifecycle

### T1.1 Mutation Contract

- 🟩 [x] Make append read and validate a nonzero root tail directly instead of walking from the first shelf.
- 🟩 [x] On the first terminal shelf, publish `first == tail == new shelf` in the same logical mutation.
- 🟩 [x] When a full tail gets a successor, publish `oldTail.next == newTail` and `root.tail == newTail` together.
- 🟩 [x] When a local split includes the previous tail, update root tail to the new right shelf.
- 🟩 [x] When deleting/unlinking the non-head tail shelf, update root tail to the predecessor.
- 🟩 [x] When the last shelf is removed, clear both first and tail.
- 🟩 [x] Keep ordinary non-tail insertion/deletion local and avoid rewriting the terminal root when first/tail did not change.
- 🟩 [x] Keep cache publication aligned with durable publication and invalidate staged terminal projections on batch abort.

### T1.2 Legacy And Corruption Contract

- 🟩 [x] Accept legacy `first != 0 && tail == 0` as a recoverable old encoding.
- 🟩 [x] Discover the legacy tail by walking header next links only; do not scan identity entries.
- 🟨 [~] Repair a legacy zero tail during the next mutation, optimizer rewrite, compaction, or explicit maintenance operation; append/local mutation and full rewrite are implemented, while explicit maintenance-only repair remains unnecessary unless later evidence justifies mutating outside ordinary work.
- 🟩 [x] Reject `first == 0 && tail != 0`.
- ⬜ [ ] Reject a nonzero tail that is unreachable from first.
- ⬜ [ ] Reject a nonzero tail whose next link is nonzero.
- 🟩 [x] Detect cycles and malformed shelf headers during a fallback/assessment walk.

### T1.3 LXL And Harness Matrix

- 🟩 [x] Empty root to one shelf: first and tail both become S1.
- 🟩 [x] Append within S1: root remains unchanged.
- 🟩 [x] Fill S1 then append: S1.next becomes S2 and tail becomes S2.
- 🟩 [x] Split first, middle, and last shelves; only a last-shelf split changes tail.
- 🟩 [x] Delete the last identity from a middle shelf; tail remains unchanged.
- 🟩 [x] Delete the last identity from the tail shelf; tail becomes predecessor.
- 🟩 [x] Delete the final route identity; first and tail both become zero.
- 🟩 [x] Ascending, descending, alternating-edge, and deterministic-shuffled identity orders.
- 🟩 [x] Exact-tuple no-op and duplicate policy.
- 🟩 [x] Live, reopen, abort, compaction, and topology-assessment parity.
- 🟩 [x] Legacy zero-tail reopen followed by one mutation and durable repaired-tail reopen.
- ⬜ [ ] Deliberately corrupt tail unreachable/next-nonzero cases fail with specific diagnostics.

### T1 Acceptance Gate

- 🟩 [x] Exact tuple order and count pass live and after reopen.
- 🟩 [x] Root tail always names the reachable final shelf in newly written topology.
- 🟩 [x] Hot ascending append does not walk prior terminal shelves.
- 🟩 [x] Local mutations remain bounded to the containing shelf, one split/link neighbor, and first/tail metadata only when required.
- 🟩 [x] Existing VS8, VS16, fixed-terminal, batching, abort, optimizer, compaction, concurrency, and public-surface regressions pass for the focused T1 surface.

## Phase T2 — Native VS8 Sorted Builder

### T2.1 Builder Contract

- 🟩 [x] Accept encoded `(variable key, scalar-8 identity)` tuples through a low-friction typed/internal adapter.
- 🟩 [x] Accept unordered input and sort once by encoded key then identity inside the owning builder; root-prefix planner feeds consume the validated sorted spans without re-sorting.
- 🟩 [x] Validate global tuple order after sorting, exact-tuple duplicates, unique-key policy, one-key-per-identity policy, maximum key length, null/empty key-state routing, and cancellation before publication.
- 🟩 [x] Keep source-record and Inheto materialization outside the physical builder.
- 🟩 [x] Reuse existing VS8 key encoders and comparer semantics.
- 🟩 [x] Build unreachable candidate topology, commit children, revalidate ownership under the serialized empty-root scope, then publish through one stable-root rewrite.
- 🟩 [x] On cancellation or failure before publication, leave the prior root authoritative; unreachable appended extents remain maintenance-reclaimable.

### T2.2 Exhausted-Key Terminal Construction

- 🟩 [x] Detect an all-equal-key range before asking for a key-divergence depth.
- 🟩 [x] Store the exhausted key once in the terminal root.
- 🟩 [x] Pack sorted identities directly into terminal identity-8 shelves.
- 🟩 [x] Build the terminal shelf chain backward so every shelf's next link is final when first written.
- 🟩 [x] Persist exact first and tail shelf offsets in the terminal root.
- 🟩 [x] Use the existing exact-key router-chain builder to attach the terminal root beneath the variable-key prefix.
- 🟩 [x] Do not retain all source records or Inhetos while terminal shelves are built.

### T2.3 Mixed-Key Router Construction

- 🟩 [x] Extend the existing VS8 optimizer replacement planner rather than adding a parallel router model.
- 🟩 [x] Preserve complete-prefix ownership and the accepted exact-stem routing invariant.
- 🟩 [x] Reuse existing detached child construction, tuple conservation checks, candidate validation, and stable empty-root publication.
- 🟩 [x] Preserve null/empty routes and reject maintained-projection facades until a grouped native replacement can honor all projection ownership.
- 🟩 [x] Preserve deterministic `(key, identity)` traversal in ascending and descending directions.

### T2.4 Bounded Input And Memory

- 🟩 [x] Select the smallest first implementation that proves the Wherzit workload without turning all source payloads into a retained object graph.
- ⬜ [ ] Measure managed allocation, working-set change, tuple-buffer high water, spill bytes if any, and candidate topology bytes.
- 🟩 [x] Document the first slice honestly: it retains one compact `(byte[] key reference, ulong identity)` tuple array and one encoded byte array per distinct string, but never retains source records or Inhetos and does not claim bounded streaming.
- ⬜ [ ] Retain the existing bounded run/spill architecture as the next-best branch if Wherzit-sized strings exceed the accepted in-memory bound.

### T2 LXL And Harness Matrix

- ⬜ [ ] Empty, one tuple, one ordinary shelf, router boundary, and multi-router key sets.
- ⬜ [ ] One exhausted key at exactly one less than, equal to, and one more than terminal-shelf capacity.
- 🟩 [x] One exhausted key spanning many terminal shelves.
- ⬜ [ ] Exhausted key before, between, and after ordinary key ranges.
- ⬜ [ ] Deep shared prefixes, earlier divergence/later convergence, and skewed distributions.
- 🟨 [~] Ascending, descending, and deterministic-shuffled source tuples pass; alternating-edge native-source order remains open.
- ⬜ [ ] Duplicate policy and uniqueness rejection.
- 🟨 [~] Live/reopen/descending exact parity passes; native-build compaction parity remains open.
- ⬜ [ ] Candidate failure, cancellation, and version-conflict publication isolation.
- ⬜ [ ] Exact reachability and growth-canary proof.

### T2 Acceptance Gate

- 🟩 [x] Zero tuple loss, duplication, or ordering inversion in the 50,000-row native proof.
- ⬜ [ ] Reopen and compaction preserve exact tuple order.
- 🟩 [x] Newly built terminal roots have correct first/tail links as independently walked by topology assessment live and after reopen.
- 🟩 [x] Physical bytes remain proportional to exact reachable topology in the first 50,000-row proof (`1,176,560` file bytes; `1,163,264` reachable bytes).
- 🟩 [x] Native construction materially outperforms the prior per-item VS8 path on Wherzit-sized input (`0.4782140` including live order validation versus the prior approximately `63.14 s` Wherzit build baseline).
- ⬜ [ ] Existing optimizer, mutation, range/scoop, projection, maintenance, compaction, concurrency, and API regressions pass.

## Phase T3 — Abraxas And Wherzit Adoption

- 🟩 [x] Add the narrowest Abraxas-internal adapter that can populate the temporary string/scalar-8 order from extracted values and identities without 49,057 ordinary inserts.
- 🟩 [x] Keep Attributes deliberately unmaintained so it remains the dogfood example of a real unindexed order request.
- 🟩 [x] Preserve the `Defensive candidate/fallback` route label unless a more precise native-fallback label improves diagnostics without implying a maintained index.
- 🟨 [~] Preserve cancellation, form responsiveness, selected-row continuity, three-state sort behavior, and sort-status copy output: the real Wherzit ascending/descending/natural cycle and copied reports pass; selected-row continuity was not explicitly reported and remains open.
- 🟩 [x] Preserve current string interning behavior for displayed repetitive Attributes strings without coupling storage keys to CLR intern lifetime; native encoding reuse is scoped to one build dictionary.
- 🟨 [~] Existing Wherzit ordering/list and final-grid timing remains intact. The first real report shows approximately four seconds remain in ordering/list, so finer extraction/native-build/traversal attribution is now warranted before selecting another optimization.
- 🟩 [x] Rebuild Wherzit, terminate the old process if present, and relaunch the rebuilt interactive application without computer-use.

### T3 Performance Gates

- 🟩 [x] Repeat the 49,057-row Attributes ascending, descending, and natural-order cycle.
- 🟨 [~] Require exact record count and selected-row continuity: all three reports contain exactly `49,057` records; selected-row continuity was not explicitly reported.
- 🟩 [x] Hard direction: beat the prior approximately `63.14 s` per-item run materially. Measured ordering/list is `4.3805727 s` ascending and `3.9170778 s` descending.
- 🟥 [!] Comparative target: beat the earlier approximately `2.84 s` defensive in-memory/composite fallback without weakening LibraDex ordering semantics. The current native VS8 fallback is much faster than the pathological run but has not yet beaten that earlier timing.
- 🟥 [!] Stretch target: complete the fallback ordering/build portion in under one second on the current 49,057-row corpus. The full ordering/list measurement remains approximately four seconds; component attribution is not yet exposed.
- ⬜ [ ] Run 1x/2x/4x synthetic scaling and require approximately linear growth rather than quadratic growth.

## Results Ledger

### 2026-08-29 — Complete Exact-Index Reader And Legacy Population Repair

- 🟩 [x] Confirmed the Wherzit `Path`/`Attributes` count mismatch was real: the authoritative `FileInfoStore` contained `49,057` identities while the persisted `.FullName` exact-text index exposed `48,532`, a deficit of `525` identities.
- 🟩 [x] Ruled out duplicate source keys and traversal-only hiding. All `525` missing identities had unique FullName values, exact key lookup also failed, and the omissions clustered in `26` ranges under one long Python-cache prefix. This is consistent with the already-corrected historical route-batch publication/topology defect.
- 🟩 [x] Added an ordinary-index population revision to the Abraxas catalog. Ready exact-string indexes written before revision `1` are marked unready and rebuilt once from authoritative Fractal records during attachment; current indexes pay no steady-state parity scan or Fractal enumeration cost.
- 🟩 [x] Added a grouped exact-string publication gate: the just-built LibraDex identity count must equal the admitted tuple count before Abraxas can publish the index Ready.
- 🟩 [x] Routed complete-store, single-index `OpenSorted` requests through the exact index identity cursor instead of generic tuple/key reconstruction.
- 🟩 [x] Root-caused the remaining approximately `0.8 s` `OpenSorted` cost to `ReaderCore` calling `Fractal.RecordExists` once per already-converged index identity. Exact index row plans now trust identity existence just as the direct identity API already does, while record payload retrieval remains lazy and authoritative.
- 🟩 [x] The `49,057`-row performance proof now measures direct ascending `0.012–0.035 s`, exact-index `OpenSorted` ascending `0.009–0.023 s`, direct descending `0.013–0.014 s`, and exact-index `OpenSorted` descending `0.004–0.006 s`, with exact sequence parity.
- 🟩 [x] `wherzit-common-prefix-index-sanity 49057` passes live, after reopen, and after add/delete mutation using grouped exact-text batches of `256`.
- 🟩 [x] Adjacent regressions pass: `virtual-property-index-sanity`, `abraxas-sorted-reader-sanity`, `abraxas-bookmark-sanity`, `abraxas-data-reader-sanity`, and `fractal-record-reader-sanity`.
- 🟩 [x] Release/x64 clean Rebuild passed with zero errors and `1,605` reported warnings in `00:00:24.86`; log: `E:\VSProjects\AbraxasDB\artifacts\build-solution-release-x64-exact-index-trusted-reader-20260829.log`.
- 🟩 [x] Opening the real Wherzit store through the rebuilt stack completed its one-time text-index recovery in `00:00:14.8645766`. Independent post-repair audit: authoritative count `49,057`, persistent FullName identities `49,057`, distinct identities `49,057`, deficit `0`.

### 2026-08-29 — Native Descending Exact-Identity Traversal

- 🟨 [~] Root cause confirmed: `LibraDexStringScalar8Index.IterateExactIdentities(Descending)` routed through `MaterializeExactTuples(All)`, decoded and retained every exact string/identity tuple, reversed the whole-result list, and only then yielded identities; ascending already used the shape-native identity stream.
- 🟩 [x] BLX selected native reverse `VS8` traversal over a smaller identity-only reversal buffer because the latter would reduce allocations while preserving whole-result latency and would not satisfy the indexed-reader contract.
- 🟩 [x] Implemented reverse router visitation and reverse shelf-slot movement in `VarKeyScalar8RangeReader`; forward-only terminal and duplicate chains retain their already-read shelf buffers once and reverse only the parallel shelf-reference range.
- 🟩 [x] Replaced the exact-string descending materialization bridge with the directional encoded range reader, followed by reverse empty/null key-state identity emission.
- 🟨 [~] Live and reopen tuple parity passes for the 50,000-row mixed ordinary/exhausted/null/empty native fixture plus the existing routed, multi-byte transform, exact-stem, duplicate-run, and scoop fixtures; an explicit synthetic persisted VS8 duplicate-run continuation remains unavailable because current repeated-key growth canonicalizes to a terminal root.
- 🟩 [x] Three fresh 50,000-row proof catalogs measured steady-state ascending at `0.0089950–0.0122147 s` and descending at `0.0034469–0.0080964 s`; both streams consumed exact parity without whole-result tuple/string materialization.
- 🟩 [x] `duplicate-run-sanity`, `vs8-index-api-sanity`, `vs8-routed-sanity`, `vs8-mb-router-transform-sanity`, `var-key-exact-stem-sanity`, `vs8-scoop-topology-proof`, `varlen-optimizer-lifecycle-sanity`, and `public-surface-api-sanity` pass.
- 🟥 [!] The first targeted clean Rebuild stopped before compilation because the removed `obj` tree also removed NuGet assets and project-level MSBuild did not restore automatically. The repeated required snapshot preceded `/restore /t:Rebuild`, which passed with zero errors.
- 🟩 [x] The dependent AbraxasDB solution `/restore /t:Rebuild` passed Release/x64 with zero errors and 1,605 reported warnings in `00:00:22.87`; log: `E:\VSProjects\AbraxasDB\artifacts\build-solution-release-x64-vs8-native-descending-20260829.log`.
- 🟩 [x] `virtual-property-index-sanity`, `virtual-property-index-performance 49057`, `wherzit-store-lifecycle-sanity`, and `fractal-record-reader-sanity` pass against the rebuilt dependency graph. The cold virtual-property build measured `0.9347644 s` total, including `0.8209871 s` extraction/conversion and `0.0775837 s` native physical VS8 construction.
- 🟨 [~] Interactive Wherzit Attributes descending timing acceptance remains; the rebuilt application is ready for the user-visible sort cycle.

### 2026-08-28 — Checklist Initialization

- 🟩 [x] User selected terminal-tail lifecycle first, followed by native VS8 construction and Wherzit adoption.
- 🟩 [x] Created this separate checklist so commands, design decisions, proofs, failures, and exact restart state do not depend on conversation history.
- 🟩 [x] Confirmed no new persisted shelf shape is required for the selected tail repair.
- 🟩 [x] Recorded the current 49,057-row Attributes baseline and the accepted BLX branches.
- 🟩 [x] Implemented direct root-tail append, first/tail creation, tail-aware local split/append/delete, backward-packed full rewrites, and VS8 topology tail validation without changing the persisted format.
- 🟩 [x] Added fresh-topology first/tail assertions and a directed legacy zero-tail file fixture whose next ascending mutation must repair the existing tail durably.
- 🟥 [!] First rebuild stopped on one compile-only local-name collision in the rewritten append method; it was corrected without changing the design.
- 🟩 [x] Required `.code-history` snapshot copied 118 changed source/config/project files before the successful rebuild. The host safety layer blocked pruning 351 expired history files and blocked direct recursive `bin`/`obj` removal; neither operation was bypassed. An explicit MSBuild Clean preceded Rebuild.
- 🟩 [x] Release/x64 harness rebuild passed with zero errors in `00:00:35.6405507`; log: `artifacts/build-harness-release-x64-vs8-tail-20260828-082419414.log`.
- 🟩 [x] `terminal-identity-locality-sanity --count 7000` passed in `00:00:04.5104015`. Ascending/descending were both `376,832` bytes; alternating was `409,600`; shuffled was `376,832`.
- 🟩 [x] The same command passed first/middle/last spare insertion, first/middle/last full-shelf split, delete locality, exact final delete, durable first/tail checks, and legacy zero-tail mutation repair (`tail=344064`).
- 🟩 [x] Focused adjacent gates passed: `duplicate-run-sanity`, `var-key-exact-stem-sanity`, `varlen-optimizer-lifecycle-sanity`, `catalog-compaction-sanity`, `concurrency-contract-sanity`, and `ss8-8-primitive-concurrency-proof`.
- 🟩 [x] Phase T1 is accepted on the current dirty tree.
- 🟩 [x] Added the VS8 all-equal-key optimizer branch, 4 KB backward-packed terminal identity construction, a one-sort empty-root native builder, and an exact-only string facade adapter with per-distinct-string encoding reuse.
- 🟩 [x] Required snapshot captured 120 changed source/config/project files; Release/x64 harness Clean + Rebuild passed with zero errors. Log: `artifacts/build-harness-release-x64-vs8-native-proof-20260828-084138843.log`.
- 🟩 [x] `vs8-sorted-build-sanity --count 50000` passed in `00:00:00.4782140` at `104,556/s`; file bytes `1,176,560`, reachable bytes `1,163,264`, routers `12`, ordinary shelves `7`, terminal roots `1`, terminal shelves `69`, key-state roots `2`.
- 🟩 [x] Removed the redundant per-prefix comparison sort: the native builder now sorts once globally and feeds validated root-prefix spans directly into the existing optimizer planner.
- 🟩 [x] Final native proof passed in `00:00:00.4397727` at `113,695/s` with identical physical topology and live/reopen ascending/descending parity.
- 🟩 [x] AbraxasDB solution explicit Clean + Release/x64 Rebuild passed in `00:01:15.4589493`; log: `E:\VSProjects\AbraxasDB\artifacts\build-solution-release-x64-vs8-native-20260828-084510442.log`.
- 🟩 [x] `abraxas-sorted-reader-sanity` passed in `00:00:02.1150834`, including explicit/implicit preparation, cancellation retry, exact-index reuse, stable iteration, mixed directions, and metadata reopen.
- 🟩 [x] `wherzit-store-lifecycle-sanity` passed in `00:04:55.9965734`, including 49,057-record during-ingestion stress, 50,000-record bounded grouped backfill, ST/MT parity, spill paths, reset cases, and legacy A/B backfill.
- 🟩 [x] Adjacent gates passed: terminal identity locality, duplicate runs, exact stems, optimizer lifecycle, catalog compaction, and the full primitive-concurrency proof. `concurrency-contract-sanity` had one timing-sensitive telemetry stop after five earlier gates, then passed immediately when rerun alone; exact-count concurrency proof passed in the original sweep.
- 🟩 [x] Rebuilt Wherzit launched directly from `E:\VSProjects\AbraxasDB\Wherzit\bin\Release\net8.0-windows\Wherzit.exe` as process `47848`; no computer-use was used.
- 🟩 [x] First real 49,057-row Attributes cycle completed: ascending ordering/list `4.3805727 s`, descending `3.9170778 s`, and Fractal natural order `0.7035600 s`; all reports contain exactly `49,057` records.
- 🟩 [x] Relative to the `63.1369766 s` regression, ascending improved by approximately `14.4x` and descending by approximately `16.1x`; the quadratic/per-item construction pathology is no longer present at this scale.
- 🟨 [~] Active task: if further Attributes-sort optimization is desired, add phase attribution around value extraction, native VS8 construction/publication, and ordered traversal before changing another core algorithm. Confirm selected-row continuity interactively.

## Restart Instructions

1. Read this document completely.
2. Read `AGENTS.md`, `AGENT_CODING_STYLE.md`, the relevant current entries in `LIBRADEX_DESIGN_CHECKLIST.md`, and `docs/ss8-backfill-correctness-performance-checklist.md`.
3. Inspect `git status --short` in both `E:\VSProjects\LibraDex` and `E:\VSProjects\AbraxasDB`.
4. Resume the first 🟨 or ⬜ item in the earliest incomplete phase unless the results ledger names a more specific active task.
5. Inspect the current diff of every existing file before editing it.
6. Update this ledger after every proof, failure, rejected branch, build, benchmark, or change of active task.
