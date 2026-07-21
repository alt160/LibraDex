# Abraxas Concurrency Checklist

## Purpose

Track the open concurrency design questions that surfaced while integrating LibraDex into Abraxas.

This checklist is intentionally separate from the earlier Abraxas identity-query and CreateIndex checklists. Those lanes prove integration shape and backend registration. This lane decides what concurrency guarantees LibraDex must offer when Abraxas uses it as an ordered backend.

## Marker Legend

- 🟩 [x] Done / accepted.
- 🟨 [~] Partial / incomplete / mixed child states.
- 🟥 [!] Failed / stopped / ignored / problem item.
- ⬜ [ ] Open / not started.

## Restart Here

- 🟩 [x] Current answer: LibraDex can be modified to support a better concurrency story, but current dried ink is single-owner per active catalog/session.
- 🟩 [x] Core design direction: LibraDex is a rebuildable identity index over external source data, so concurrency should be framed around visible index state, publication, stale/incomplete/rebuild markers, and `EventualIndexed`, not database transactions.
- 🟩 [x] First Abraxas contract: Abraxas serializes LibraDex-backed writes; LibraDex accepts isolated concurrent catalogs when backing resources are distinct.
- 🟩 [x] First reader contract: normal Abraxas reads should use guarded materialized/disconnected identity results; current cursor APIs are live-session cursors, not snapshots.
- 🟩 [x] First guardrail contract: opt-in diagnostic active-write-window ownership checks around `BeginDurabilityBatch` and writer-context-bearing `CommitDurabilityBatch` / `AbortDurabilityBatch`.
- 🟩 [x] First diagnostic owner model: use an atomic session write-window claim and a batch-carried operation token; managed thread id is diagnostic context, not commit/abort authority.
- 🟩 [x] First production packet: public XML/API contract wording, diagnostic write-window ownership implementation, deterministic `concurrency-contract-sanity`, and maintenance-as-write wording.
- 🟩 [x] First failure-state rule: if source data changed and LibraDex indexing fails, prefer stale/incomplete/needs-rebuild state and replay/rebuild/fallback over rollback language.
- 🟨 [~] Still open: exact index-state API/persistence, RAM-backed publication layer, Abraxas status API naming, strict cursor diagnostic mode, broader production tests, broader shape coverage, public batch queue semantics, and published-reader API shapes.
- 🟥 [!] Deliberately deferred: hidden broad locks, implicit queues under existing batch APIs, published-reader claims, cross-process shared-reader model, independent route-topology concurrent writer mutation, and database-style rollback/isolation language.

## Current Position

- 🟩 [x] First Abraxas-facing concurrency contract selected: Abraxas serializes LibraDex-backed writes; LibraDex is treated as single-owner per active catalog/session for now.
- 🟩 [x] Cross-process synchronization is out of the immediate slice because Abraxas has an efficiency gate: when the gate is closed, no other processes have been reported as accessing the same data, so cross-process sync is not required at the moment.
- 🟩 [x] LibraDex is an identity index, not the source-of-truth database. Source data lives outside LibraDex, and LibraDex catalogs/indexes can be rebuilt from that source data.
- 🟩 [x] Abraxas can expose an `eventual indexed` consistency state: writes to source data can be accepted before every LibraDex index projection is fully caught up.
- 🟩 [x] Concurrency work should optimize for fast discrete lookup, rebuildability, explicit stale/incomplete index state, and low/no developer friction rather than SQL-style isolation.
- 🟩 [x] Committed rollback is out of scope. LibraDex may abandon unpublished staged work, but it should not promise database-style undo of already published logical mutations.
- 🟩 [x] Source-write-success/index-write-failure cases should produce stale/incomplete/needs-rebuild state and catch-up/rebuild behavior, not source rollback.
- 🟩 [x] Reader visibility target: readers should see committed/published state only; reader visibility should not be a cause of locks.
- 🟩 [x] `Commit` must be split conceptually between file-backed and RAM-backed catalogs. File-backed commit can approximate owner-side durability plus publication; RAM-backed commit is not a reader-visibility boundary today because direct arena writes may already be visible.
- 🟨 [~] LibraDex has concurrency-friendly physical ideas, but current session/kernel dried ink is not a safe concurrent-writer contract.
- 🟩 [x] Existing code paths have been mapped enough to choose the first packet: session/batch/commit ownership, cursor lifetime, route-cache publication, pending-write overlays, RAM-backed direct arena mutation, file-open sharing, maintenance descriptors, and optimizer route publication.
- 🟩 [x] Keep the first answer low/no developer friction for Abraxas while preserving LibraDex hot-path performance.
- 🟩 [x] Current Abraxas return gate: LibraDex now has a bounded queued writer path for fixed-scalar identity indexes and the Abraxas write adapter, with deterministic coverage for same-shelf retry, different-shelf independent staging, narrowed topology publishers, and explicit broad serialized fallback only for paths that still need it.
- 🟩 [x] Current Abraxas return gate: ordinary direct `SS8-8` `Insert(...)` now uses the index-owned queued writer by default when no batch/projection state requires the older durability path, so the common no-ceremony insert path can overlap safely.
- 🟩 [x] Current Abraxas return gate: same-catalog/different-index `SS8-8` queued inserts are now proven for overlapping writer-context paths; they share one session but keep shelf staging writer-local and serialize only publication.
- 🟩 [x] Current Abraxas return gate: several queued topology fallback cases no longer use the old session-wide writer-operation gate when they target a different index root: cold root-route initialization, root-prefix split, root shelf-transform, walked parent-route split, full same-key duplicate overflow, exact-key terminal identity overflow, and existing duplicate-run chain append/rewrite.
- 🟩 [x] Current Abraxas return gate: exact `SS8-8` tuple deletes are now available through queued writer/adapters. Ordinary shelf-local deletes, terminal identity shelf-local deletes, and linked duplicate-run shelf deletes use writer-context staging when they only rewrite owned shelf bytes; terminal/duplicate cleanup or route relink remains deferred rather than required for the common exact-delete path.
- 🟩 [x] Current Abraxas return gate: key-changing patches can use queued rekey, modeled as replacement insert followed by old tuple delete with both legs reported separately. This is identity-index mutation, not database rollback/transaction semantics.
- 🟩 [x] Current Abraxas return gate: direct exact `IIndex.Delete(...)` and value-route criteria deletes on `SS8-8` now converge on queued exact tuple deletion when no batch/projection state requires the older durability path.
- 🟩 [x] Current Abraxas return gate: direct exact-key `IIndex.Rekey(...)` and value-route criteria `SetKey` on `SS8-8` now have deterministic concurrent proof through the same queued insert plus queued exact-delete bridge.
- 🟩 [x] Current Abraxas return gate: `SS8-8` scalar-null route mutation is serialized at the key-state route, `ScalarNull.NonNull` and `All` value-route deletes reuse queued exact tuple deletion, and cursor-local `DeleteCurrent` / `SetKey` now route physical mutation through the owning index bridge.
- 🟩 [x] Current cross-shape transfer: generic cursor-local delete now converges through the owning index exact-delete path for fixed-scalar shapes, with deterministic convergence probes for `SS16-8`, `SS8-16`, and `SS16-16`.
- 🟩 [x] Current cross-shape transfer: ordinary direct `SS16-8` insert and exact delete now have a first writer-context slice for warmed shelf-local value-route mutations; same-shelf insert overlap is caller-safe through internal retry, and cold root-route initialization is narrowed to a per-root serialized topology path.
- 🟩 [x] Current cross-shape transfer: direct `SS16-8` rekey, criteria delete, and criteria `SetKey` now converge on the widened-key writer-context insert/exact-delete legs for warmed shelf-local value-route mutations.
- 🟩 [x] Current cross-shape transfer: `SS16-8` now admits the generic queued-writer facade for insert/delete/rekey; the facade delegates to the same no-ceremony direct writer-context paths instead of creating a second hidden single-writer funnel.
- 🟩 [x] Current cross-shape transfer: ordinary direct `SS8-16` insert/exact-delete/rekey and value-route criteria delete/`SetKey` now have warmed shelf-local writer-context behavior with same-shelf retry proof.
- 🟩 [x] Current cross-shape transfer: ordinary direct `SS16-16` insert/exact-delete/rekey and value-route criteria delete/`SetKey` now have a first warmed shelf-local writer-context slice.
- 🟩 [x] Current var-key transfer: raw `VS16` one-shot inserts now match the first `VS8` ordinary-shelf writer-context slice for warmed ordinary shelves. Different root-prefix shelves can stage independently, same-shelf ownership is retryable, and growth/split/topology routes still use the existing durability-batch path.
- 🟨 [~] Current fixed-N transfer: fixed-width BigInteger `FSN-8` and `FSN-16` indexes now route `ScalarNull.Null` inserts/deletes/reads/counts through slot-owned key-state identity routes, ordinary fixed-key exact delete/rekey has a routed no-merge leaf-shelf compact/rewrite path, and warmed fixed-N leaf insert/delete paths now attempt writer-context staging before serialized topology fallback. This brings fixed-N much closer to scalar-shape mutation parity for optional keys and exact tuple changes; remaining fixed-N gaps are empty-route cleanup/merge, variable-identity parity, and making the new staging path a performance win rather than only a concurrency/stability win.
- 🟨 [~] Current fixed-N proof: `fixedn-concurrency-performance-proof --single-ops 512 --ops-per-thread 128 --max-threads 8` validates `FSN-8` and `FSN-16` BigInteger inserts plus mixed delete/rekey pressure through 8 concurrent callers. BLX accepted branches: writer contexts now lazily allocate only the shape dictionaries they touch, and `FSN-8`/`FSN-16` exact delete now compacts the dense item arena in place instead of allocating a full temporary compacted item array. Latest proof runs are green. Best 8-thread rows from the accepted branch: `FSN-8` insert reports `1024` ops in `108.188ms`, `9,465 ops/sec`, `125,784,944` allocated bytes; `FSN-16` insert reports `1024` ops in `92.966ms`, `11,014 ops/sec`, `109,518,816` allocated bytes; `FSN-8` mixed delete/rekey reports `512` ops in `109.153ms`, `4,690 ops/sec`, `163,591,720` allocated bytes; `FSN-16` mixed delete/rekey reports `512` ops in `86.692ms`, `5,905 ops/sec`, `301,570,424` allocated bytes. Rejected or deferred BLX branches: unsafe immediate-serialized adaptive fallback over-counted under unpublished fixed-N writer contexts; pooled fixed-N delete compaction hurt high-thread elapsed time; one-publication rekey coalescing worsened high-thread mixed rows; compact operation-journal staging, both insert/delete and insert-only, moved too much work into the publication lock and did not produce a stable elapsed/allocation win. Current conclusion: the stable staged-shelf path with lazy context state and in-place delete compaction is the best proven fixed-N path; the next-best branch would be true multi-operation coalescing only if it has an explicit batching boundary large enough to amortize publication-lock work.
- 🟨 [~] Current fixed-N batch coalescing proof: `fixedn-batch-coalescing-proof --small 8 --medium 64 --large 512` validates the explicit fixed-width BigInt batch surface for warmed shelf-local inserts, exact deletes, and ordinary rekeys. The accepted branch adds `LibraDexBigIntScalar8Index<TIdentity>.BeginBatch(...)` and `LibraDexBigIntScalar8Batch<TIdentity>` for ordinary fixed-width keys, stages `FSN-8`/`FSN-16` shelf images in the caller-owned durability batch, records fixed-N index-directory count deltas in the same write context, and flushes each touched fixed-N shelf once at publish. The branch is deliberately bounded: scalar-null, variable-width BigInt, cold-route creation, full-shelf split, and topology mutation remain outside this batch slice. Proof result: 8-item batches save allocation but lose elapsed time due setup overhead; 64/512 item inserts are clear wins; deletes are allocation-light but remain CPU-bound by dense shelf compaction; rekeys pay insert plus delete. Latest 512-row proof: `FSN-8` one-shot insert `5.036ms` / `38,060,072` bytes versus explicit batch insert `0.912ms` / `2,287,224` bytes, explicit batch delete `5.918ms` / `2,225,784` bytes, and explicit batch rekey `13.153ms` / `4,384,232` bytes; `FSN-16` one-shot insert `6.957ms` / `33,861,672` bytes versus explicit batch insert `1.268ms` / `177,784` bytes, explicit batch delete `7.043ms` / `116,344` bytes, and explicit batch rekey `13.574ms` / `165,352` bytes.
- 🟨 [~] Current fixed-N delete CPU BLX: accepted the format-preserving gap-fill/slot-tail delete branch. Instead of shifting the whole payload tail and rewriting every later slot offset, fixed-N delete now copies the last dense payload item into the removed payload gap, shifts only the sorted slot tail, and patches the single slot that referenced the moved payload. Deferred tombstones remain rejected/deferred because they need a broader active-count/payload-count format or sidecar model; public batch-local delete sorting remains deferred because it changes immediate result timing unless exposed as a new bulk terminal. Proof remains green. Latest 512-row explicit-batch result after the branch: `FSN-8` delete `6.390ms` / `2,225,784` bytes and rekey `10.663ms` / `4,384,232` bytes; `FSN-16` delete `3.003ms` / `116,344` bytes and rekey `12.586ms` / `165,352` bytes. Current conclusion: this is a safe bounded improvement, strongest for `FSN-16` delete, but the next material win probably requires a real bulk delete/rekey terminal or a deferred-compaction shelf state.
- 🟨 [~] Current fixed-N bulk terminal: `LibraDexBigIntScalar8Batch<TIdentity>` now exposes `DeleteMany(...)` and `RekeyMany(...)` for explicit caller-sized fixed-width BigInt batches. `DeleteMany` materializes key/identity tuples and processes them in descending key order. `RekeyMany` materializes rekey operations, sorts by old key descending, then uses the existing replacement-before-removal semantics per operation. This avoids the rejected all-replacements-first branch that temporarily doubled shelf pressure and made large rekeys slower. Latest 512-row proof: `FSN-8` delete improves from per-call batch `3.427ms` / `2,225,784` bytes to bulk `1.578ms` / `2,250,520` bytes, and rekey improves from per-call batch `11.011ms` / `4,384,232` bytes to bulk `7.574ms` / `4,425,288` bytes; `FSN-16` delete improves from `5.273ms` / `116,344` bytes to bulk `1.951ms` / `149,240` bytes, and rekey improves from `10.053ms` / `165,352` bytes to bulk `9.444ms` / `214,568` bytes. Current conclusion: bulk terminals trade small allocation growth for materially lower elapsed time at large batch sizes; small and medium batches should keep per-call methods.
- 🟩 [x] Current Abraxas return gate: normal Abraxas reads should continue through materialized/disconnected identity results; lazy cursors and published-reader semantics remain outside the concurrent integration contract.
- 🟨 [~] Current Abraxas return gate: Abraxas may rely on ordinary direct fixed-scalar insert/exact-delete/rekey/value-route criteria mutation for no-ceremony overlap when the mutation is warmed and shelf-local. Cold-route creation and full-shelf split/transform topology now report `NarrowTopologyPublisher` rather than broad fallback in the proven fixed-scalar matrix, but they still publish route topology under the per-root topology gate plus storage publication lock. No-batch maintained exact-reversed projection inserts now keep primary writer attribution and publish the companion projection tuple immediately; explicit public batches, broad projection-coupled overwrites, lazy-cursor sharing, and maintenance still require outer serialization.
- 🟩 [x] Current group-by finding: condition grouping is correct but not yet HPC/concurrency-shaped. The public terminal materializes candidate identities, scans the grouping index, builds per-group member lists, and only then projects counts/metadata/representatives.
- 🟩 [x] Current group-by proof: `group-by-execution-proof` compares a legacy materialized count baseline, the production `Counts()` terminal, and an independent ordered tuple streaming count path. For `50,000` rows / `1,000` tenants / active modulo `3`, legacy materialized grouping measured `68.192ms` and `6,864,696` allocated bytes; production counts measured `24.941ms` and `4,452,352` allocated bytes. For `200,000` rows / `5,000` tenants / active modulo `4`, legacy materialized grouping measured `126.775ms` and `28,356,024` allocated bytes; production counts measured `74.358ms` and `19,507,136` allocated bytes.
- 🟩 [x] Current group-by implementation: single-key `Counts()` now uses a small internal grouping strategy selector and chooses ordered tuple streaming when the requested terminal/order can be satisfied without per-group member lists. Duplicate/singleton/count-range filters and count-descending/top-N count results stay on the streaming count path.
- 🟩 [x] Current grouped-result implementation: single-key `Metadata()` now uses the same strategy selector and streams key/count/first/last identity rows without building per-group member lists when the requested group and item ordering can be satisfied from ordered tuple traversal.
- 🟩 [x] Current grouped-result proof: for `50,000` rows / `1,000` tenants / active modulo `3`, legacy materialized metadata measured `23.467ms` and `5,541,328` allocated bytes; production metadata measured `23.152ms` and `4,368,024` allocated bytes. For `200,000` rows / `5,000` tenants / active modulo `4`, legacy materialized metadata measured `81.774ms` and `23,762,424` allocated bytes; production metadata measured `72.149ms` and `19,526,848` allocated bytes. This is allocation-positive and roughly throughput-neutral for full metadata.
- 🟩 [x] Current representative-result implementation: `Representatives()`, `FirstIdentities()`, and `LastIdentities()` now use terminal-specific compact count-plus-representative state and avoid per-group member lists for key-ordered and count-ordered workflows.
- 🟩 [x] Current representative-result proof: for `50,000` rows / `1,000` tenants / active modulo `3`, fair legacy first-plus-last materialization measured `42.107ms` and `11,096,096` allocated bytes; production representatives measured `38.056ms` and `8,871,368` allocated bytes. For `200,000` rows / `5,000` tenants / active modulo `4`, fair legacy first-plus-last materialization measured `153.751ms` and `47,553,440` allocated bytes; production representatives measured `111.686ms` and `39,266,136` allocated bytes. This is now both allocation-positive and throughput-positive for paired first-plus-last calls.
- 🟩 [x] Current full grouped-result decision: `ToList()` and `ToDictionary()` intentionally remain materialized because their contracts return owned complete member lists. `OpenReader()` also remains dictionary-backed for now; a key-ascending one-group-at-a-time reader candidate was measured and removed because it did not reduce allocation and had mixed throughput (`50,000` row candidate: `22.546ms` / `5,597,296` allocated bytes vs `23.092ms` / `5,540,320`; `200,000` row candidate: `63.663ms` / `23,863,624` vs `54.785ms` / `23,770,104`).
- 🟩 [x] Current multi-key grouping implementation: `Groups(...).By<TIdentity>(LibraDexRoutedCompositeIndex)` now groups by the full `LibraDexCompositeKey` without caller-owned string concatenation. Routed composite indexes also implement the internal tuple streamer, so composite grouping can scan tuple streams instead of forcing `ExecuteTuplePrimitive(...).ToArray()` first.
- 🟩 [x] Current multi-key grouping proof: `group-by-composite-proof` uses a scalar active-status condition and groups by composite `(tenant, user)` full keys. After the composite key allocation pass, for `50,000` rows / `1,000` tenants / `128` users / active modulo `3`, production composite counts measured `125.618ms` and `15,667,448` allocated bytes across `16,000` groups; production composite metadata measured `55.385ms` and `11,905,240` allocated bytes. For `200,000` rows / `5,000` tenants / `256` users / active modulo `4`, production composite counts measured `430.068ms` and `115,688,840` allocated bytes across `120,000` groups; production composite metadata measured `259.812ms` and `86,771,984` allocated bytes. Count-path allocation includes the harness structural-key lookup assertion.
- 🟩 [x] Current aggregate-result implementation: grouped `Aggregate`, `Sum`, `Min`, and `Max` terminals now exist on `LibraDexConditionGroupQuery`. The HPC path for multiple aggregate values is one custom aggregate state in a single `Aggregate(...)` call; separate convenience calls are correct but each terminal scans independently.
- 🟩 [x] Current aggregate-result proof: `group-by-aggregate-proof` compares legacy member-list materialization with one-pass custom aggregate state that computes sum/min/max/custom identity-derived values. For `50,000` rows / `1,000` tenants / active modulo `3`, legacy materialized aggregate measured `60.988ms` and `6,891,168` allocated bytes; production one-pass aggregate measured `26.828ms` and `4,593,424` allocated bytes. For `200,000` rows / `5,000` tenants / active modulo `4`, legacy materialized aggregate measured `107.436ms` and `28,461,328` allocated bytes; production one-pass aggregate measured `89.264ms` and `20,081,080` allocated bytes.
- 🟩 [x] Current full grouped-result implementation: `OpenRowReader()` now provides a row-streaming grouped result path for key-ascending group order, ascending item order, and unfiltered group counts. It exposes `CurrentKey`, `CurrentItem`, `IsFirstInGroup`, `GroupOrdinal`, and `ItemOrdinalInGroup` so callers can process full grouped results without per-group member-list allocation. `OpenReader()`, `ToList()`, and `ToDictionary()` intentionally remain materialized owned-list contracts.
- 🟩 [x] Current full grouped-result proof: `group-by-row-reader-proof` compares the existing materialized grouped reader to `OpenRowReader()`. For `50,000` rows / `1,000` tenants / active modulo `3`, current materialized reader measured `62.725ms` and `7,239,752` allocated bytes; streaming row reader measured `24.129ms` and `4,406,136` allocated bytes. For `200,000` rows / `5,000` tenants / active modulo `4`, current materialized reader measured `111.136ms` and `29,166,384` allocated bytes; streaming row reader measured `87.398ms` and `19,424,968` allocated bytes.
- 🟩 [x] Current composite-key allocation pressure pass: routed composite tuple reconstruction now uses an internal owned-array `LibraDexCompositeKey` factory so full-key streaming avoids a second defensive key-value array copy per reconstructed composite key. This does not remove composite key object allocation, but it cuts one avoidable array allocation from the hot grouped composite scan path.
- 🟩 [x] Current direct target-index grouping proof: `group-by-direct-target-proof` covers the condition/grouping shape where the condition leaf and grouped index are the same physical index. For `50,000` rows / `1,000` tenants / upper tenant `499`, legacy materialized counts measured `67.432ms` and `5,142,944` allocated bytes; production direct-target counts measured `17.794ms` and `1,297,984` allocated bytes. This proves the grouped terminal can avoid its local candidate `HashSet` when the shared condition cursor executor has a real target-index tuple plan.
- 🟩 [x] Current candidate-set allocation pass: unrelated-index grouping now builds fallback candidate identity sets directly from the condition projection iterator instead of first materializing `ToList<TIdentity>()`. For `50,000` rows / `1,000` tenants / active modulo `3`, unrelated-index production counts measured `32.531ms` and `3,807,592` allocated bytes, metadata measured `27.089ms` and `3,717,464` bytes, representatives measured `51.143ms` and `7,583,992` bytes, row-reader measured `32.151ms` and `3,763,608` bytes, and one-pass aggregate measured `36.502ms` and `3,950,864` bytes. Composite scalar-filter grouping also dropped to `15,025,832` bytes for counts and `11,262,696` bytes for metadata in the `50,000` row proof.
- 🟨 [~] Current group-by direction: unrelated-index condition/grouping shapes still need a local candidate identity set before scanning the grouping index because the shared cursor executor otherwise lacks a true merge/intersection plan for target tuples. The cursor executor fallback now uses hash membership for default-hashable identities, but that is a guardrail rather than a replacement for a real planner. Remaining composite-key object allocation and published-reader/concurrent-cursor semantics should remain guarded until they have their own proof paths. Count-filtered, count-ordered, and descending-member full grouped readers still use metadata/materialized paths because a truly streaming reader cannot know group counts or reverse members before draining each group.

## Initial Scope Boundary

- 🟩 [x] First Abraxas-backed LibraDex usage is serialized by Abraxas ownership convention, not true multi-writer LibraDex access.
- 🟩 [x] Cross-process synchronization is deferred while the Abraxas efficiency gate proves no other process access is reported.
- 🟩 [x] Index loss, stale index contents, or incomplete index publication are recoverable if Abraxas can mark the index state and rebuild from source data.
- 🟨 [~] Define whether file-backed catalogs and memory-backed catalogs have the same concurrency contract: same caller intuition is desired, but RAM-backed catalogs need a publication model above raw memory mutation before that can be true.
- 🟨 [~] Current single-owner contract effectively applies at the active catalog/session boundary; narrower catalog, identity-group, index, file-session, and DataKernel ownership rules remain open.
- 🟨 [~] Define whether the first accepted slice is an explicit limitation document only or includes implementation changes: documentation is now started; runtime guardrails and tests remain pending implementation choices.

## Identity-Index Concurrency Frame

- 🟩 [x] LibraDex corruption or data loss is serious but not catastrophic when source data remains available and rebuild is supported.
- 🟩 [x] The concurrency story should use index-state language: current, stale, rebuilding, incomplete, failed, or eventual indexed.
- 🟩 [x] Reader semantics should mean "visible index state" rather than database transaction visibility.
- 🟩 [x] Writer semantics should mean "index publication/update state" rather than source-data commit.
- 🟩 [x] Failed or interrupted index writes can prefer marking an index/catalog for rebuild over attempting rollback.
- 🟩 [x] Abraxas can choose low-friction behavior where writes to source data succeed and LibraDex indexing catches up.
- 🟥 [!] Avoid importing SQL/database terms such as transaction isolation, serializable reads, rollback guarantees, or source-of-truth durability into the LibraDex contract.
- 🟨 [~] Concurrency implementation should first make unsafe states detectable and rebuildable before pursuing fine-grained concurrent mutation.

## Index-State Vocabulary

- 🟩 [x] `Current`: the visible LibraDex index is believed to match the external source data for the indexed scope known to Abraxas.
- 🟩 [x] `EventualIndexed`: Abraxas has accepted source-data changes and the LibraDex projection is allowed to lag; lookups can be valid for the currently visible index but incomplete relative to the newest source state.
- 🟩 [x] `Stale`: the visible LibraDex index is known or suspected to be behind source data, but remains structurally readable.
- 🟩 [x] `Incomplete`: LibraDex indexing or publication did not cover the full expected source scope; reads can return partial answers and should be labeled as such or avoided by higher layers.
- 🟩 [x] `Rebuilding`: the index/catalog is being rebuilt from external source data; callers should expect lookups to be unavailable, stale, or routed to the last visible state depending on Abraxas policy.
- 🟩 [x] `Failed`: LibraDex detected a structural or publication problem that makes the affected index/catalog unreliable until repaired or rebuilt.
- 🟩 [x] `NeedsRebuild`: remediation marker, not necessarily a separate visible read state. It tells Abraxas/LibraDex that source data should be replayed into a fresh or repaired index.
- 🟩 [x] First implementation should prefer one compact state plus optional repair flags over a database-like transaction log or rollback model.
- 🟩 [x] Ownership split accepted: Abraxas owns source-relative freshness/catch-up state, while LibraDex owns structural index reliability diagnostics.
- 🟨 [~] Exact type/API shape remains open: one enum, enum-plus-flags, per-index marker, per-catalog aggregate marker, or separate Abraxas state plus LibraDex diagnostics.
- 🟨 [~] File-backed LibraDex structural-state persistence remains open. It may live in a future status sidecar or dedicated format extension, not hot-path records.
- 🟥 [!] `Current` does not mean source-of-truth durability. It only means the visible identity index is believed caught up to the external source data known to the owner.
- 🟥 [!] `EventualIndexed` does not mean readers should see unpublished LibraDex writer state. It means the visible index may lag accepted source writes.
- 🟥 [!] Do not repurpose superblock developer metadata for LibraDex-owned state; current comments say LibraDex stores those values durably but does not interpret them.

## Use-Case Matrix For LXL

Use this matrix to decide concurrency behavior from concrete cases instead of starting from an abstract lock model.

Each lxl pass should answer:

- 🟩 [x] Does this case require concurrency for Abraxas or LibraDex developer intuition?
- 🟩 [x] What should the caller expect to see?
- 🟩 [x] What exact current code path is exercised?
- 🟩 [x] Is the current behavior safe, unsafe, accidentally safe, or undefined?
- 🟨 [~] Should LibraDex allow, internally serialize, explicitly reject, document as unsupported, or defer the case?

### Case 1: Single Writer, No Reader

- 🟩 [x] Expected requirement: required baseline.
- 🟩 [x] Expected behavior: one caller can add, update, delete, and commit through one active catalog/session.
- 🟩 [x] LXL target: establish the no-concurrency baseline for batch state, pending writes, commit, and publication.
- 🟩 [x] Decision target: confirm this remains the lowest-overhead path and is not burdened by concurrency work.
- 🟩 [x] Current answer: supported under the single-owner session contract.

### Case 2: Single Writer, Concurrent Readers

- 🟩 [x] Expected requirement: likely required for developer intuition.
- 🟩 [x] Expected behavior: readers see committed/published state only, not unpublished writer state.
- 🟩 [x] LXL target: walk reader paths during an active batch, during commit, and after commit for file-backed and RAM-backed catalogs.
- 🟥 [!] Current answer: unsafe as-is inside one active session because readers can touch pending writer state and mutable session caches.
- 🟨 [~] Decision target: likely allow later through disconnected/materialized read results or an explicit index-publication layer; document unsupported in the first contract.

### Case 3: Multiple Writers To Different Keys In The Same Index

- 🟩 [x] Expected requirement: desirable long-term, not required for the first Abraxas contract.
- 🟥 [!] Current risk: shared session, batch state, pending overlays, append cursor, caches, and commit state.
- 🟩 [x] LXL target: walk two `Insert` or overwrite paths that target different logical keys but share the same active session/kernel.
- 🟥 [!] Current answer: unsafe as-is.
- 🟨 [~] Decision target: reject for now; future work can choose internal queuing or per-index/per-shelf writer isolation.

### Case 4: Multiple Writers To Different Indexes In The Same Catalog

- 🟩 [x] Expected requirement: plausible Abraxas intuition if indexes feel independent.
- 🟥 [!] Current risk: different indexes may still share catalog/session/kernel state and durability batch state.
- 🟩 [x] LXL target: walk writes that enter separate index objects but converge on the same file session or DataKernel.
- 🟥 [!] Current answer: unsafe as-is for true concurrency; sequential group batching is supported but is not concurrent writing.
- 🟨 [~] Decision target: reject for now; future work can queue inside LibraDex at the catalog/session boundary if Abraxas should not manage it.

### Case 5: Multiple Writers To Different Catalogs

- 🟩 [x] Expected requirement: likely should be allowed if catalogs do not share active mutable state.
- 🟨 [~] Current risk: static shared profile instances appear immutable, and diagnostic state found so far is thread-static; same file path is a separate cross-process/same-file case.
- 🟩 [x] LXL target: walk two opened catalogs with separate sessions and backing files or memory arenas.
- 🟩 [x] Current answer: likely safe when catalogs have distinct active sessions and distinct backing resources.
- 🟨 [~] Decision target: allow if isolated; document required isolation and add guardrails only for accidental same-file/session sharing.

### Case 6: Writer Plus Optimization, Compaction, Split, Or Route Maintenance

- 🟩 [x] Expected requirement: not required for first Abraxas write path, but important for background maintenance.
- 🟥 [!] Current risk: internal optimizer replacement paths can start their own durability batch and rewrite route targets; public maintenance APIs are currently mostly scaffolded and not connected to physical mutation.
- 🟩 [x] LXL target: walk one ordinary writer/read cursor against one maintenance-style mutation.
- 🟥 [!] Current answer: public scaffold is low immediate risk; connected physical maintenance must be serialized with active writers/read cursors.
- 🟨 [~] Decision target: explicit maintenance lock or session queue before physical maintenance is exposed as callable concurrent work.

### Case 7: Multiple Processes Against The Same File-Backed Catalog

- 🟨 [~] Expected requirement: deferred while Abraxas efficiency gate says no other process access is reported.
- 🟨 [~] Current risk: file-open sharing blocks a second LibraDex read/write open, but external raw/read-only access could still observe file state without LibraDex cache/version protocol.
- 🟩 [x] LXL target: walk open flags and catalog/session state for two process instances targeting the same files.
- 🟨 [~] Current answer: second LibraDex writer/open is naturally rejected by `FileShare.Read`; a formal shared-reader model is not implemented.
- 🟨 [~] Decision target: keep deferred by Abraxas efficiency gate; later choose exclusive writer open, shared-reader model, advisory lock, versioned index view, or explicit unsupported failure.

### Case 8: RAM-Backed Catalog Concurrent Readers And Writers

- 🟩 [x] Expected requirement: likely same caller intuition as file-backed reads/writes, but different durability semantics.
- 🟥 [!] Current risk: RAM-backed `DataKernel` can write to volatile memory before `DataKernel.Commit()`, so durability cannot define visibility.
- 🟩 [x] LXL target: walk RAM-backed reserve/write/read/commit behavior with a concurrent reader.
- 🟥 [!] Current answer: unsafe as-is for concurrent readers/writers in one active session.
- 🟨 [~] Decision target: define index publication above raw memory mutation, serialize reader/writer access, or document as single-owner only.

## Questions To Answer

### Caller Contract

- 🟩 [x] Can multiple Abraxas callers query the same LibraDex-backed index concurrently? Yes for the normal adapter path only when Abraxas routes them through its owner/serialization boundary and receives disconnected materialized results; lazy same-session cursors are not safe for independent concurrent callers.
- 🟥 [!] Can one Abraxas caller write while another caller queries the same LibraDex-backed index? Not safely in the same active session as-is.
- 🟥 [!] Multiple Abraxas callers writing to different indexes in the same active catalog/session are unsupported in the first contract.
- 🟥 [!] Multiple Abraxas callers writing to the same index concurrently are unsupported in the first contract.
- 🟩 [x] Abraxas owns write serialization for the first contract.
- 🟨 [~] Should LibraDex expose an explicit concurrency mode option so Abraxas can choose low-overhead single-owner mode? Candidate answer: yes, if implementation adds guardrails or queued mode later.

### Read Semantics

- 🟩 [x] Readers should see committed/published state only.
- 🟩 [x] Pending writer state should not leak into ordinary reader visibility.
- 🟥 [!] Are readers allowed during uncommitted batch writes? Not in the same active session as-is.
- 🟩 [x] If readers are allowed during writes, the desired answer is old committed/published state, not in-session pending writes or mixed live mutation state.
- 🟨 [~] Do cursors require stable visible-index-state for their lifetime? Candidate answer: yes for concurrent read support; current lazy cursors do not provide it.
- 🟥 [!] What happens if a cursor is open while commit, delete, overwrite, route split, shelf split, compaction, or varlen replacement occurs? Current behavior is undefined and should be unsupported until disconnected/materialized cursor behavior or visible-index-state pinning exists.
- 🟨 [~] Do identity-returning Abraxas adapter calls return disconnected caller-owned identities before releasing any LibraDex lock? `ToList`-style execution does; cursor/iterator APIs do not.

### Write Semantics

- 🟨 [~] Are LibraDex writes atomic at tuple, index, identity-group, catalog, batch, or file-session level? Current usable boundary is single-owner session index publication, not database isolation.
- 🟩 [x] Committed rollback is out of scope; abandoning unpublished staged work is still allowed and is not database rollback.
- 🟩 [x] Commit cannot be treated as one universal visibility boundary across backing kinds.
- 🟩 [x] File-backed commit can approximate publication plus durability/write boundary for the current owner.
- 🟩 [x] RAM-backed commit has no durable backing-store meaning and is not a reader-visibility boundary today because direct arena mutation can precede commit.
- 🟩 [x] Batch mode is treated as single-owner by current contract.
- 🟥 [!] Pending-write reuse and reclaimed payload queues are not safe to share across concurrent writers under the current implementation.
- 🟨 [~] Does overwrite need remove-old/insert-new atomicity across all ordered backends registered for an Abraxas index? Still open for Abraxas semantics; concurrency lxl says it must be serialized with other writes under the first contract.

### File And Process Semantics

- 🟨 [~] Does LibraDex allow two process instances to open the same file-backed catalog? A second LibraDex read/write open should fail under current `FileShare.Read`; a formal reader-only model does not exist.
- 🟨 [~] Cross-process model is deferred by the Abraxas efficiency gate rather than solved by LibraDex in this slice.
- ⬜ [ ] If cross-process access becomes required, decide whether the intended model is advisory file locks, exclusive writer lock, shared readers, or append/versioned index views.
- 🟨 [~] If cross-process writes remain unsupported, decide where exclusivity is enforced and how Abraxas surfaces the failure. Current file sharing already blocks a second LibraDex read/write open, but the public error shape is not designed.
- ⬜ [ ] Does superblock/header state need a concurrency/version marker?
- 🟥 [!] Are route-cache and arena-cache invalidation rules sufficient for external or cross-session mutation? No for external/cross-session mutation as a supported model; current caches are session-local and assume single-owner mutation.

### Hot-Path Cost

- 🟨 [~] Identify which locks, fences, volatile reads, interlocked counters, or copy-on-write structures would sit on read/query hot paths.
- 🟩 [x] Identify which synchronization costs can be paid only at open, create, commit, or adapter boundary.
- 🟩 [x] Decide whether single-owner mode is the default to avoid hidden overhead.
- 🟩 [x] Diagnostics for contention, ownership conflicts, and wait time should be opt-in only. First packet has no waits; future queued modes can expose wait diagnostics separately.

## Candidate First Contracts

- 🟩 [x] Contract A: explicit single-owner LibraDex session, no internal thread safety, Abraxas must serialize access.
- 🟩 [x] Contract B-lite: serialized writer with Abraxas materialized readers, where adapter `Get`/`GetIdentities` returns disconnected lists and detailed diagnostics reject adapter materialization while a same-session write window is already active.
- 🟨 [~] Contract B-full: single writer with true concurrent published readers remains future work because it needs a visible-index-state publication layer.
- 🟨 [~] Contract C: per-catalog reader/writer lock, simple but potentially broad contention and hot-path cost. Kept as a fallback, but it conflicts with low hidden hot-path overhead if used by default.
- 🟨 [~] Contract D: published index view plus isolated mutable writer session, higher complexity but cleaner reader semantics. Best semantic fit for RAM-backed parity, but higher implementation cost.
- 🟨 [~] Contract E: cross-process exclusive open for writes, no shared process mutation in first Abraxas slice. Deferred while Abraxas efficiency gate says no other process access is reported.

## Guardrail Shape From LXL

- 🟩 [x] Guardrails should detect unsupported same-session concurrency; they should not turn every LibraDex read into a locked database-style read.
- 🟩 [x] The lowest-cost first guardrail point is the session/batch boundary: `BeginDurabilityBatch`, `CommitDurabilityBatch`, and `AbortDurabilityBatch` already define the active mutable write window.
- 🟩 [x] A session-level active-operation or owner-thread diagnostic can catch accidental overlapping writers before they mutate shared route caches, dirty shelf caches, pending segments, or append cursors.
- 🟩 [x] The active-operation diagnostic should be opt-in or debug/diagnostic first so the current single-owner hot path remains low overhead.
- 🟩 [x] Public lazy cursor surfaces need explicit contract language: they are live-session cursors, not disconnected snapshots.
- 🟩 [x] Abraxas-facing read helpers should prefer materialized/disconnected results before returning to user code when concurrent source writes or index catch-up can happen outside the call.
- 🟨 [~] A later published-reader mode could pin a visible index state, but the current code does not have that abstraction and should not imply it.
- 🟨 [~] RAM-backed catalogs need an index-publication layer above raw arena mutation before they can honestly share the same reader/writer intuition as file-backed catalogs.
- 🟥 [!] Avoid adding a broad reader/writer lock as the default first fix. It is easy to explain but risks hidden contention on LibraDex's lookup hot path and still would not by itself solve lazy cursor snapshot semantics.
- 🟥 [!] Avoid using `DataKernel.Commit()` alone as the universal visibility boundary. File-backed commit and RAM-backed publication have different meanings in current code.

## Deterministic Harness Shape

- 🟩 [x] Harness probes should use coordinated start gates rather than timing sleeps so failures are deterministic.
- 🟩 [x] Harness probes should validate the contract outcome, not merely absence of exceptions.
- 🟩 [x] Same-session writer/writer probe should force two callers to attempt overlapping `Insert` or `BeginBatch` work against one active catalog/session.
- 🟩 [x] Same-session writer/writer expected current result: unsafe/undefined before guardrails; once guardrails exist, exactly one writer owns the write window and the other receives a clear unsupported-concurrency failure.
- 🟩 [x] Same-session reader/writer probe should cover both materialized reads and lazy cursor reads while a write batch is active.
- 🟩 [x] Same-session reader/writer expected current result: unsupported for concurrent use; after docs/guardrails, materialized reads must be documented against the visible state they read, and lazy cursors must remain single-owner/live-session only unless a published-reader mode exists.
- 🟩 [x] Separate-catalog writer/writer probe should prove two physically isolated catalogs can write concurrently without shared static state failures.
- 🟩 [x] Same-file open probe should prove current file-backed behavior: a second LibraDex read/write open against the same path fails because of existing file-share policy.
- 🟩 [x] RAM-backed reader/writer probe should prove current memory-backed session behavior is single-owner only and should not be treated as naturally snapshot-capable.
- 🟨 [~] Future published-reader probe should exist only after a visible-index-state abstraction exists.
- 🟨 [~] Future queued-writer probe should exist only if LibraDex adds an internal write queue mode.
- 🟥 [!] Harness probes should not try to "prove safety" by running random stress loops. Stress can supplement deterministic probes, but it cannot define the contract.

## Public Contract Wording Draft

- 🟩 [x] Contract summary: LibraDex catalogs and indexes are single-owner objects unless a future concurrency mode explicitly says otherwise.
- 🟩 [x] Isolated catalogs: separate catalog/session instances may be used concurrently when they do not share the same backing file, memory arena, or caller-owned mutable state.
- 🟩 [x] Same-session writes: overlapping writes through the same catalog/session are unsupported; callers should serialize them or use an Abraxas layer that serializes them.
- 🟩 [x] Batch scope: `BeginBatch`, index batch mode, and identity-group batch mode control commit cadence for one owner; they are not independent thread-safety or transaction-isolation domains.
- 🟩 [x] Reader visibility: ordinary concurrent-reader support is not promised for a live writer session; readers should use materialized/disconnected results when another owner may write.
- 🟩 [x] Cursor scope: cursor APIs are live-session cursors. They should not be described as snapshots and should not be shared across concurrent writers.
- 🟩 [x] Commit scope: file-backed commit is a durability/publication boundary for the current owner; RAM-backed commit is publication/cadence only and does not imply durable storage.
- 🟩 [x] Rollback scope: aborting unpublished staged work is allowed, but LibraDex does not promise rollback of already published index mutations.
- 🟩 [x] Maintenance scope: physical maintenance, once connected, must be serialized with active writers and live cursors unless a future maintenance mode explicitly owns a published state.
- 🟩 [x] Abraxas adapter scope: adapter reads intended for normal Abraxas callers should prefer materialized identity lists/results over live cursors when Abraxas may accept writes concurrently.
- 🟨 [~] Future wording target: if `QueuedWriter` mode exists, callers can submit writes concurrently but LibraDex serializes execution internally at the session boundary.
- 🟨 [~] Future wording target: if `PublishedReaders` mode exists, readers may observe a pinned visible index state while a writer prepares a later state.
- 🟥 [!] Avoid saying "thread-safe" without naming the exact mode and ownership boundary.
- 🟥 [!] Avoid saying "transaction", "isolation", "snapshot", or "rollback" for current LibraDex behavior.

## Diagnostic Ownership Check Draft

- 🟩 [x] Diagnostic ownership should be opt-in through existing diagnostics/options shape, not always-on synchronization.
- 🟩 [x] First tracked concept: active session write window.
- 🟩 [x] First owner verifier should be a monotonically assigned write-window operation token returned inside the durability-batch handle; managed thread id is diagnostic context, not the authority.
- 🟩 [x] First guard point: entering `BeginDurabilityBatch` claims the session write window when no batch is active.
- 🟩 [x] Commit/abort guard points: `CommitDurabilityBatch` and `AbortDurabilityBatch` must be called through the active write-window batch token.
- 🟩 [x] Exit behavior: successful commit or abort releases the active write-window owner even when publication fails after the owner has been identified.
- 🟩 [x] Failure shape: throw a clear unsupported-concurrency exception before shared mutable state is modified.
- 🟩 [x] Implementation shape: in diagnostics mode, the first claim must be atomic, not just a read/set of `durabilityBatchActive`.
- 🟩 [x] Minimum state shape: a tiny diagnostic claim field plus active operation token and owner thread id is enough for first-packet failure messages.
- 🟩 [x] Failure message should point to caller options: serialize access, use separate isolated catalogs, materialize read results, or use a future queued-writer/published-reader mode.
- 🟩 [x] The check should not claim to protect every read. It detects overlapping write-window misuse first.
- 🟨 [~] Cursor diagnostics remain second-stage: live cursor creation while a write batch is active can be flagged, but cursor safety against later writes still needs caller ownership or published-reader pinning.
- 🟩 [x] First enable switch: use existing `CatalogOptions.DiagnosticsLevel` with `LibraDexDiagnosticsLevel.Detailed` as the provisional opt-in for write-window ownership checks.
- 🟩 [x] `LibraDexDiagnosticsLevel.Counters` should remain storage/operation counter collection only; do not make it change semantic failure behavior.
- 🟩 [x] First exception type: use `InvalidOperationException` with precise unsupported-concurrency wording. A custom LibraDex exception can be added later only if Abraxas handling needs a typed catch path.
- 🟥 [!] Do not add a blocking wait, retry loop, or implicit queue under the diagnostic ownership check. Queuing is a separate future mode.
- 🟥 [!] Do not add ownership checks to every hot read path unless a later mode explicitly chooses that cost.

## Abraxas Adapter Concurrency Proof

- 🟩 [x] Existing adapter shape has materialized identity reads: `AbraxasIdentityQueryAdapter.Get` and `GetIdentities` return `IReadOnlyList<TIdentity>`.
- 🟩 [x] Existing adapter shape also has live cursor reads: `OpenCursor` and `GetCursor` return `LibraDexIdentityCursor<TIdentity>`.
- 🟩 [x] Abraxas normal query integration should prefer materialized identity reads when Abraxas may accept source writes concurrently or allow `EventualIndexed` lag.
- 🟩 [x] Adapter materialized reads now have a detailed diagnostic guard: if a same-session write window is active, `Get` fails before condition materialization can read pending writer state.
- 🟩 [x] Adapter materialization boundary is useful because caller code receives a disconnected identity list before returning to source-object hydration.
- 🟩 [x] Cursor adapter methods should be documented as live-session cursors and should remain outside the ordinary concurrent Abraxas read/write intuition for now.
- 🟩 [x] Abraxas write serialization proof should sit above LibraDex for the first contract: only one LibraDex-backed writer enters a catalog/session write window at a time.
- 🟩 [x] `EventualIndexed` proof should show that source writes can be accepted while a LibraDex projection is marked lagging/stale/incomplete, then later rebuilt or caught up.
- 🟩 [x] Read result proof should distinguish "current visible index answered this query" from "all latest source writes are indexed."
- 🟨 [~] Adapter status reporting shape remains open: result metadata, Abraxas-owned state, LibraDex diagnostics, or a small index-state API.
- 🟨 [~] Cursor policy remains open for Abraxas: hide cursor methods from normal Abraxas integration, expose only under single-owner scope, or later make them published-reader backed.
- 🟥 [!] Do not make `EventualIndexed` a hidden behavior. If a query can miss source writes accepted after the visible index state, the state should be visible to Abraxas/caller policy.
- 🟥 [!] Do not use adapter cursors as proof of concurrent reader safety; current cursors are live-session objects.

## Future Concurrency Modes

- 🟩 [x] `SingleOwner`: default mode. One owner uses one active catalog/session at a time; no hidden synchronization is placed on the read/write hot path.
- 🟩 [x] `SingleOwner` allows concurrent work across physically isolated catalogs/sessions.
- 🟩 [x] `SingleOwner` does not allow overlapping same-session writers or live cursor/read use while another owner mutates the same session.
- 🟩 [x] `QueuedWriter`: bounded current mode where callers may submit supported insert operations concurrently, and LibraDex serializes only the conflicting/fallback parts at the session/catalog write-window boundary.
- 🟩 [x] `QueuedWriter` should preserve the same publication semantics as serialized writes; it is not true simultaneous per-shelf mutation.
- 🟩 [x] `QueuedWriter` should surface queueing/wait diagnostics if it becomes visible to callers or affects low/no developer friction.
- 🟩 [x] `QueuedWriter` insert boundary accepted for the first bounded runtime mode: callers submit individual `SS8-8` insert operations and LibraDex owns same-shelf serialization, different-shelf writer-context staging, and serialized fallback for unsupported topology cases.
- 🟨 [~] `QueuedWriter` batch/broader-shape boundary remains open: blocking `BeginBatch`, async submitted batch work, cancellation, fairness, public queue depth diagnostics, non-`SS8-8` shapes, deletes, overwrites, and maintenance are not covered by the current queued insert facade.
- 🟩 [x] `PublishedReaders`: future mode where readers observe a pinned visible index state while a writer prepares or publishes a later state.
- 🟩 [x] `PublishedReaders` requires a visible-index-state abstraction above raw `DataKernel`, especially for RAM-backed catalogs.
- 🟩 [x] `PublishedReaders` can make cursor/read semantics intuitive only if cursor lifetime pins or materializes the visible state it reads.
- 🟨 [~] `PublishedReaders` implementation boundary remains open: materialized identity results, reader-pinned immutable snapshots, copy-on-publish arena/versioning, or generationed state pinning.
- 🟨 [~] Mode-selection API shape remains open: `CatalogOptions`, separate concurrency options, adapter-level policy, or Abraxas-owned policy.
- 🟨 [~] Mode combinability remains open: `QueuedWriter` and `PublishedReaders` could be independent flags or composed as a higher-level mode.
- 🟩 [x] True per-index/per-shelf concurrent writer prerequisites are identified, but implementation remains a later research track after mode contracts, guardrails, index-state markers, and harnesses exist.
- 🟥 [!] `QueuedWriter` must not be silently enabled by diagnostics.
- 🟥 [!] General `QueuedWriter` support must not be claimed for public batches, non-`SS8-8` shapes, deletes, overwrites, maintenance, cancellation, fairness, or queue diagnostics until those semantics are designed and implemented.
- 🟥 [!] `PublishedReaders` must not be claimed until pending writer state and RAM-backed direct arena mutation are hidden behind a real publication boundary.
- 🟥 [!] `PublishedReaders` must not treat current `DataKernel.Commit()` as a universal visibility boundary.
- 🟥 [!] None of these modes should introduce database-style rollback, source-of-truth durability, or transaction isolation language.

## True Concurrent Writer Research Lane

- 🟩 [x] The core storage idea is still compatible with future better concurrency: many ordinary inserts are shelf-local rewrites/appends, and some split paths can keep parent routes stable.
- 🟩 [x] The current blocker is above that physical idea: session ownership, mutable route caches, dirty shelf caches, pending segments, append allocation, deferred commits, and publication are shared.
- 🟩 [x] True same-session concurrent writers should be deferred until `SingleOwner` guardrails, deterministic harnesses, index-state markers, and published-reader semantics are in place.
- 🟩 [x] Required prerequisite: isolate writer-private staged state from session-visible reader state.
- 🟩 [x] Required prerequisite: define an append/reserve allocator that can safely hand out non-overlapping extents to concurrent writers.
- 🟩 [x] Required prerequisite: define conflict detection for same shelf, same route, same root, same identity-group, and same index-directory slot changes.
- 🟩 [x] Required prerequisite: define publication ordering for multiple writer-local changes without exposing partial route/shelf states.
- 🟩 [x] Required prerequisite: define cache invalidation/versioning so route readers do not use stale decoded router or shelf views after another writer publishes.
- 🟩 [x] Required prerequisite: define RAM-backed publication above direct arena mutation, otherwise memory mode cannot hide writer-private bytes.
- 🟨 [~] Per-index writer concurrency may be easier than per-shelf writer concurrency because it can preserve index-level publication boundaries while still sharing the catalog/session/kernel.
- 🟨 [~] Per-shelf writer concurrency may be useful later, but it needs conflict/version ownership at the shelf and parent-route level.
- 🟨 [~] Background maintenance concurrency should remain separate from ordinary writer concurrency because route replacement/optimizer paths can rewrite larger visible topology.
- 🟥 [!] Do not start with true per-shelf writer mutation as the first implementation step. It has the highest correctness risk and is not required for Abraxas' first contract.
- 🟥 [!] Do not assume "different keys" means "different storage conflict." Different keys can still share a shelf, route page, append allocator, commit batch, or cache.
- 🟥 [!] Do not make route-cache invalidation global and frequent as a cheap substitute for a publication model; that would damage read hot-path predictability.

## Current LXL Findings

### 2026-06-20 Shared Write Path

- 🟩 [x] LXL question: does the original concurrent-write sketch match the current dried ink?
- 🟩 [x] Finding: the physical model still supports concurrency-friendly thinking because many mutations are shelf-local rewrites/appends and some split paths keep parent routes stable.
- 🟥 [!] Finding: current public/session/kernel implementation is not safe for concurrent writes as-is.
- 🟥 [!] Two concurrent `index.Insert(k, id)` calls can both enter the convenience batch path, race through the plain `durabilityBatchActive` check/set, and then mutate the same session/DataKernel pending overlay, cache dictionaries, append cursor, and commit state.
- 🟥 [!] This is a session/kernel ownership issue, not only a same-shelf collision issue.
- 🟩 [x] Accepted current answer: LibraDex is single-owner per active catalog/session for Abraxas, and Abraxas owns write serialization.

### 2026-06-20 Commit, Visibility, And Rollback Scope

- 🟩 [x] LXL question: does taking committed rollback out of scope create secondary issues?
- 🟩 [x] Finding: committed rollback can stay out of scope if ordinary readers never see unpublished writer state.
- 🟥 [!] Secondary issue: if readers can see pending writes and those writes are later abandoned, LibraDex would have rollback-like visibility behavior without a rollback contract.
- 🟩 [x] Accepted rule: no database-style rollback of published logical mutations; unpublished staged work may still be abandoned.
- 🟩 [x] LXL question: does `commit` mean the same thing for file-backed and RAM-backed catalogs?
- 🟥 [!] Finding: current RAM-backed `DataKernel` paths can write directly into the volatile memory arena before `DataKernel.Commit()`, so current `DataKernel.Commit()` alone cannot serve as the universal reader-visibility boundary.
- 🟨 [~] Design implication: concurrency work likely needs an index-publication abstraction above raw `DataKernel` so visible-index-state semantics are consistent across file-backed and RAM-backed catalogs.

### 2026-06-20 RAM-Backed Publication LXL

- 🟩 [x] LXL question: does RAM-backed commit currently mean "publish now" in the same way file-backed commit can approximate durable publication?
- 🟩 [x] Contrived case: RAM-backed catalog appends or reserves bytes for a writer while a same-session reader tries to read the affected route/shelf before `Commit`.
- 🟩 [x] Finding: `DataKernel.Append` writes memory-backed bytes directly into `VolatileMemoryArena`, advances `nextAppendOffset`, and returns the extent without waiting for `Commit`.
- 🟩 [x] Finding: `DataKernel.Reserve` can return a writable span over `VolatileMemoryArena`; the caller can populate arena bytes before any commit boundary.
- 🟩 [x] Finding: `TryWriteMemoryDirect` and `TryGetMemoryWritableSpanDirect` explicitly bypass staged pending writes for memory-backed hot mutations.
- 🟩 [x] Finding: memory-backed `Commit` only writes fallback `pendingSegments` into the arena and clears telemetry/staging counters; many direct arena changes are already visible before it runs.
- 🟥 [!] Result: RAM-backed commit is not a reader-visibility boundary today. It is publication/cadence only for paths that happen to stage pending segments, and mostly telemetry/counter cleanup for direct arena paths.
- 🟩 [x] First-contract decision: RAM-backed catalogs stay under the same single-owner active-session rule; do not claim concurrent reader visibility for RAM-backed catalogs in the first packet.
- 🟨 [~] Future requirement: `PublishedReaders` for RAM-backed catalogs needs an explicit visible-index-state layer, copy-on-publish arena/versioning, or reader-pinned materialization above raw direct arena mutation.
- 🟥 [!] Drift guard: do not describe RAM-backed `Commit` as equivalent to file-backed durability or as a snapshot boundary.

### 2026-06-20 Index-State Ownership LXL

- 🟩 [x] LXL question: should the accepted state vocabulary live in LibraDex, Abraxas, or both?
- 🟩 [x] Contrived case: Abraxas accepts a source write, queues LibraDex indexing, and a lookup arrives before the projection catches up.
- 🟩 [x] Finding: LibraDex cannot know whether the external source has newer accepted writes unless Abraxas tells it. Therefore `EventualIndexed`, source-relative `Current`, and source-relative `Stale` are Abraxas-owned status concepts first.
- 🟩 [x] Contrived case: LibraDex detects interrupted publication, unsupported concurrent writer misuse, corrupt structure, or an index mutation failure after source data already changed.
- 🟩 [x] Finding: LibraDex can know that its own structure is unreliable or incomplete. Therefore `Failed`, `Incomplete`, and `NeedsRebuild` are LibraDex-owned structural diagnostics first, with Abraxas free to mirror or aggregate them.
- 🟩 [x] Current persistence evidence: `CatalogIndexMetadata` is versioned structural metadata for group/name/type/key/projection/route fields, not a state/status record.
- 🟩 [x] Current persistence evidence: `IndexDirectorySlotSnapshot.State` currently has only empty/active semantics; `Flags` and `Generation` exist but are reserved/general lifecycle fields, not a documented status protocol.
- 🟩 [x] Current persistence evidence: `SuperblockDeveloperMetadata` is developer-owned and explicitly not interpreted by LibraDex, so it should not become LibraDex structural-state storage.
- 🟩 [x] First-packet decision: do not add persisted status metadata in the first packet. Use public contract wording plus diagnostic write-window ownership first.
- 🟨 [~] Future API decision: add a lightweight status surface later that can report Abraxas freshness and LibraDex structural reliability without forcing those concepts into one enum.
- 🟨 [~] Future persistence decision: if LibraDex persists structural state, prefer a dedicated status sidecar/record or a planned format-version extension over reusing existing developer metadata or hot-path index records.
- 🟥 [!] Drift guard: do not let `Current` imply LibraDex has validated external source catch-up by itself.

### 2026-06-20 Use-Case Matrix LXL

- 🟩 [x] Case 1, single writer/no reader: works under the current single-owner session contract. `LibraDexIndex.Insert` creates a batch when no group/index batch is active, writes through `LibraDexBatch.Insert`, and commits through the session durability batch. This path should remain the zero-friction, lowest-overhead baseline.
- 🟥 [!] Case 2, single writer/concurrent readers in the same active session: unsafe as-is. `DataKernel.Read` checks `ReadPending` before backing storage, so file-backed readers in the same session can see pending writer bytes before durability publication. RAM-backed readers can also see direct writes to `VolatileMemoryArena` before `DataKernel.Commit`.
- 🟥 [!] Case 2, cursor lifetime: unsafe as-is for stable visible-index-state intuition. Public cursors wrap lazy enumerators; `Next` advances the underlying iterator after the cursor is returned, so cursor reads can continue touching live index/session state while a writer mutates it.
- 🟥 [!] Case 3, multiple writers to different keys in the same index: unsafe as-is. Both writers share `durabilityBatchActive`, mutable batch shelf caches, route caches, `DataKernel.pendingSegments`, `nextAppendOffset`, and commit cleanup.
- 🟥 [!] Case 4, multiple writers to different indexes in the same catalog: unsafe as-is for true concurrency. Different index objects still converge on the same `Catalog`, `LibraDexFileSession`, session durability batch, DataKernel, route caches, and catalog batch-manager dictionaries.
- 🟩 [x] Case 5, multiple writers to different catalogs: likely safe when catalogs are physically isolated. Separate `Catalog` instances own separate `LibraDexFileSession` and `DataKernel` instances. Static profile fields observed on generic indexes are immutable profile values, and the diagnostic state found in `LibraDexFileSession` is thread-static.
- 🟨 [~] Case 5, same file path is not covered by "different catalogs." It belongs to Case 7 because the backing file becomes shared state even if caller objects are distinct.
- 🟨 [~] Case 6, writer plus maintenance: public maintenance APIs currently return scaffold descriptors and do not mutate physical storage, so they are low immediate risk. Internal varlen optimizer replacement paths do begin durability batches and publish route replacements, so connected physical maintenance must be serialized with active writers/read cursors.
- 🟨 [~] Case 7, multiple processes against the same file-backed catalog: current file open uses `FileAccess.ReadWrite` with `FileShare.Read`, which naturally blocks a second LibraDex read/write open. It does not define a formal LibraDex shared-reader protocol, version handshake, or visible-index-state rule for external readers.
- 🟥 [!] Case 8, RAM-backed concurrent readers/writers: unsafe as-is. Memory-backed `Reserve`, `ReserveAt`, and `Append` can expose writable spans over the live arena before commit, and the arena dictionaries/ranges are not synchronized.
- 🟩 [x] Consolidated answer: allow separate isolated catalogs; reject or serialize all same-session writer concurrency; reject same-session reader/writer concurrency until LibraDex has disconnected read materialization, visible-index-state pinning, or an index-publication abstraction above raw `DataKernel`.

### 2026-06-20 Identity-Index Reframe

- 🟩 [x] LXL/design question: should LibraDex concurrency be framed like a database?
- 🟩 [x] Finding: no. LibraDex is a rebuildable identity index over external source data, so concurrency should be designed around index visibility, rebuild, and `eventual indexed` state.
- 🟩 [x] Finding: index corruption or loss is recoverable if the source data remains available and Abraxas/LibraDex can mark the affected index/catalog as stale, incomplete, failed, or needing rebuild.
- 🟩 [x] Design implication: the first implementation work should favor explicit unsupported-use guardrails, deterministic failure detection, and rebuild-state hooks before fine-grained concurrent mutation.
- 🟥 [!] Drift guard: avoid adding database-shaped guarantees such as transaction isolation, source-of-truth durability, or rollback semantics to LibraDex.

### 2026-06-20 Guardrail Placement LXL

- 🟩 [x] LXL question: where can LibraDex detect unsupported concurrency without putting locks on every lookup?
- 🟩 [x] Finding: `LibraDexIndex.Insert` converges on `BeginBatch`, which converges on `LibraDexFileSession.BeginDurabilityBatch`. This is the best first choke point for write/write overlap.
- 🟩 [x] Finding: `CommitDurabilityBatch` and `AbortDurabilityBatch` are also required guardrail points because they publish or discard the same session-owned mutable state.
- 🟩 [x] Finding: group and index batch managers are write-window owners, not independent concurrency domains. Their `Enable`, `Commit`, `CommitAndDisable`, and `AbortAndDisable` paths also converge on the session durability batch.
- 🟨 [~] Finding: read guardrails are harder because many read APIs are lazy. `ToList` and `Execute` materialize before returning, while cursor and iterator APIs can keep using live session state after the public call returns.
- 🟨 [~] Design implication: first docs should distinguish materialized reads from live cursors. Future guardrails can reject live cursor creation while a write batch is active, but that still does not make a cursor safe against a later write unless the caller owns the session or LibraDex pins a visible state.
- 🟥 [!] Finding: a broad read lock around cursor creation would be misleading because the real work happens during later enumeration.
- 🟩 [x] Recommended first implementation slice, when code changes are requested: add docs/comments first, then opt-in diagnostic ownership checks around the durability batch lifecycle, then deterministic harness cases that prove same-session writer overlap fails predictably.

### 2026-06-20 Harness Shape LXL

- 🟩 [x] LXL question: what should the first concurrency harness prove?
- 🟩 [x] Finding: it should prove accepted/unsupported boundaries, not broad thread safety.
- 🟩 [x] Finding: the existing `LibraDex.Harness` command registry has many validation commands but no dedicated concurrency command found in the current scan.
- 🟩 [x] Design implication: a future validation command should be narrow and named around the contract, for example `concurrency-contract-sanity`, rather than a stress/perf command.
- 🟩 [x] Design implication: probes should coordinate two tasks at a barrier, then make one side hold a batch or cursor while the other side attempts the overlapping operation.
- 🟨 [~] Current no-code result: document deterministic pass/fail targets now; implement the harness only after the public contract and/or guardrail behavior is chosen.

### 2026-06-20 Harness Placement LXL

- 🟩 [x] LXL question: where should the deterministic concurrency contract harness live when production changes are requested?
- 🟩 [x] Current path: `LibraDex.Harness/Program.cs` registers validation commands in a static `HarnessCommand[]` table with `CommandLane.Validation` handlers.
- 🟩 [x] Finding: there is no existing concurrency-specific harness helper or gate pattern; current harness commands are command-table entries backed by `RawHarness` partial methods.
- 🟩 [x] First command name: `concurrency-contract-sanity`.
- 🟩 [x] First placement: register `concurrency-contract-sanity` as a `CommandLane.Validation` command and implement the handler in the `RawHarness` partials near public-surface/contract sanity code.
- 🟩 [x] First deterministic mechanism: use explicit start/release gates such as `TaskCompletionSource`, `ManualResetEventSlim`, or equivalent coordinated barriers, not sleeps.
- 🟩 [x] First required probe after production guardrail exists: same-session writer/writer overlap with `CatalogOptions.DiagnosticsLevel = Detailed` must fail with the accepted `InvalidOperationException` wording before the second writer mutates shared session state.
- 🟩 [x] First negative-control probe: same-session writer/writer overlap with `DiagnosticsLevel = Off` is not a safety proof; it should be skipped or documented as unsupported/undefined rather than expected to pass.
- 🟨 [~] Follow-up probes: same-session read/write, live cursor/read during write, separate isolated catalogs, RAM-backed reader/writer, and same-file open should be added after their corresponding production behavior is selected.
- 🟥 [!] Drift guard: do not add a stress command as the first validation artifact; the first command must assert specific accepted outcomes.

### 2026-06-20 First Production Packet Result

- 🟩 [x] Implemented opt-in detailed write-window ownership diagnostics in `FileSession/LibraDexFileSession.cs`.
- 🟩 [x] `BeginDurabilityBatch` now atomically claims a diagnostics-only active write-window token before mutating batch/session state when `LibraDexDiagnosticsLevel.Detailed` is enabled.
- 🟩 [x] `LibraDexFileSessionDurabilityBatch` now carries the operation token into commit/abort so managed thread id is diagnostic context, not ownership authority.
- 🟩 [x] `CommitDurabilityBatch(LibraDexWriteContext)` and `AbortDurabilityBatch(LibraDexWriteContext)` now reject wrong-context completion attempts before publishing or discarding shared session state.
- 🟩 [x] `Abort` completion ordering was fixed so a failed abort does not mark the durability-batch handle complete before session cleanup succeeds.
- 🟩 [x] Public XML/API wording now says catalog/session/index/batch/manager/cursor/maintenance surfaces are single-owner/live-session where applicable and do not imply thread safety, isolation, rollback, or published-reader snapshots.
- 🟩 [x] Added `LibraDex.Harness/Commands/RawHarness.Concurrency.cs` and registered `concurrency-contract-sanity` in `LibraDex.Harness/Program.cs`.
- 🟩 [x] Harness probe 1: public same-session overlapping `BeginBatch` with `CatalogOptions.DiagnosticsLevel = Detailed` fails with clear unsupported-concurrency wording before the second writer mutates shared session state.
- 🟩 [x] Harness probe 2: public batch abort from a different task succeeds because ownership follows the batch token rather than the managed thread id.
- 🟩 [x] Harness probe 3: internal wrong-context durability-batch abort is rejected without adding a test-only public hook.
- 🟩 [x] Build verification command: `MSBuild.exe LibraDex.Harness\LibraDex.Harness.csproj /t:Rebuild /p:Configuration=Release /p:Platform=x64 /m:1 /v:minimal /fl /flp:"logfile=artifacts\build-harness-release-x64-concurrency-contract-20260620.log;verbosity=normal"`.
- 🟩 [x] Runtime verification command: `dotnet LibraDex.Harness\bin\x64\Release\net8.0\LibraDex.Harness.dll concurrency-contract-sanity`.
- 🟩 [x] Adjacent public API verification command: `dotnet LibraDex.Harness\bin\x64\Release\net8.0\LibraDex.Harness.dll public-surface-api-sanity`.
- 🟨 [~] Remaining implementation gap: this packet detects and documents unsupported same-session writer overlap; it does not add queued writers, published readers, strict cursor diagnostics, RAM-backed publication, or true per-index/per-shelf concurrent mutation.
- 🟥 [!] Drift guard: do not describe this packet as LibraDex becoming generally thread-safe. It is the first guardrail for the accepted `SingleOwner` contract.

### 2026-06-20 Public Contract Wording LXL

- 🟩 [x] LXL question: what public wording can describe current behavior without over-promising?
- 🟩 [x] Finding: the safest current wording is "single-owner catalog/session" plus "isolated catalogs may run concurrently."
- 🟩 [x] Finding: batch APIs should be described as commit-cadence controls, not concurrency domains.
- 🟩 [x] Finding: cursor APIs need an explicit live-session warning because their enumeration continues after the creation call returns.
- 🟩 [x] Finding: maintenance APIs are scaffolded now, but the public contract should reserve that connected physical maintenance serializes with writers/live cursors.
- 🟥 [!] Drift guard: do not use "snapshot" for current cursor/read behavior unless a future published-reader state pin exists.

### 2026-06-20 Diagnostic Ownership LXL

- 🟩 [x] LXL question: can LibraDex add better concurrency behavior without broad locks?
- 🟩 [x] Finding: yes for detection. An opt-in active write-window check can make unsupported overlapping writers fail predictably before touching shared session state.
- 🟩 [x] Finding: existing options already include `DiagnosticsLevel`, so a first implementation can be diagnostic/explicit rather than changing the default hot path.
- 🟩 [x] Finding: `CatalogOptions.DiagnosticsLevel` defaults to `Off`; `Counters` is documented as low-overhead fixed numeric counters, while `Detailed` is reserved for explicit troubleshooting.
- 🟩 [x] Decision: first ownership checks should hang off `DiagnosticsLevel.Detailed`, not `Counters`, so callers who only want operation counters do not unknowingly change semantic failure behavior.
- 🟩 [x] Finding: the first owner boundary should be the session durability-batch lifecycle, because public inserts, index batch mode, and group batch mode converge there.
- 🟩 [x] Decision: use `InvalidOperationException` first because existing batch lifecycle misuse already uses it and no custom LibraDex exception hierarchy exists.
- 🟩 [x] Failure text should name same-session concurrent writes as unsupported and point to serialize access, use isolated catalogs, materialize reads, or future queued/published-reader modes.
- 🟨 [~] Finding: this improves developer intuition by making misuse loud and actionable, but it is not yet concurrent writing.
- 🟨 [~] Design implication: if callers later want concurrent write submissions without Abraxas serialization, that should be a separate `QueuedWriter` mode, not hidden inside diagnostics.
- 🟥 [!] Drift guard: an ownership check is not a reader visibility model and should not be described as making same-session readers safe during writes.

### 2026-06-20 Abraxas Materialized Read Packet Result

- 🟩 [x] Implemented the minimum Abraxas concurrent read/write contract without adding full published readers.
- 🟩 [x] `AbraxasIdentityQueryAdapter.Get` now applies an opt-in detailed diagnostic boundary check before materializing identities.
- 🟩 [x] The guard rejects adapter materialization while a same-session detailed write window is active, before condition materialization can observe pending writer state.
- 🟩 [x] The guard is scoped to the Abraxas materialized adapter path; it does not add locks to ordinary lookup hot paths and does not change `DiagnosticsLevel.Off` or `Counters`.
- 🟩 [x] Returned `IReadOnlyList<TIdentity>` results are treated as disconnected after return; later source/index writes do not mutate the already returned list.
- 🟩 [x] `concurrency-contract-sanity` now proves both sides of the boundary: a returned adapter list remains disconnected after a later write, and adapter `Get` fails fast during an active detailed same-session write window.
- 🟩 [x] This addresses the immediate Abraxas integration risk: normal Abraxas reads can use materialized identity results under the owner boundary instead of live cursors.
- 🟨 [~] Still not implemented: true same-session published-reader concurrency, live cursor safety during writes, RAM-backed visible-state publication, or arbitrary catalog reads during active writes.
- 🟥 [!] Drift guard: do not describe this as `PublishedReaders`; it is a guarded materialized adapter boundary for Abraxas integration.

### 2026-06-20 Writer Context Refactor LXL

- 🟩 [x] LXL question: if the physical shelf/route design can support better write isolation, what outer-layer seam should move first?
- 🟩 [x] Finding: `Scalar8Scalar8Batch` already owns writer-local shelf images during cached insert work, but final staging copied those images into the session-global `scalar8Scalar8MutableBatchShelfBytes` map.
- 🟥 [!] Finding: while dirty shelf publication lives directly on `LibraDexFileSession`, independent writers converge too early even when they target different shelves.
- 🟩 [x] First refactor slice: add `LibraDexWriteContext` as the pending mutation owner for one writer and move active `SS8-8` dirty shelf publication behind that context.
- 🟩 [x] First publication seam: `LibraDexFileSessionDurabilityBatch` now carries the writer context, and session commit/abort validates that the supplied context owns the active publication boundary.
- 🟩 [x] First publication synchronization seam: `CommitDurabilityBatch` and `AbortDurabilityBatch` now enter a dedicated session publication lock only while validating the writer context, flushing staged dirty state, touching `DataKernel`, and resetting publication state.
- 🟩 [x] LXL finding: cached `SS8-8` dirty shelves now stay writer-context-local until commit; they enter `DataKernel` staging only during the serialized publication step.
- 🟩 [x] Second refactor slice: internal `SS8-8` writer contexts can now claim shelf ownership, stage shelf-local dirty bytes, publish through `PublishScalar8Scalar8WriteContext`, or abandon through `AbortScalar8Scalar8WriteContext`.
- 🟩 [x] Deterministic conflict rule: two writer contexts may stage different `SS8-8` shelves, but a second context staging the same shelf receives a clear same-shelf ownership failure before publication.
- 🟩 [x] Harness proof: `concurrency-contract-sanity` now stages and publishes two different root-prefix shelves through separate writer contexts, then proves a same-shelf conflict path.
- 🟩 [x] This preserves current single-active writer behavior while creating the seam needed for later independent writer contexts.
- 🟩 [x] Verification passed for `ss8-8-index-api-sanity`, `concurrency-contract-sanity`, and `public-surface-api-sanity` after the context and publication-seam refactors.
- 🟨 [~] Still open: public writer admission, full routed insert through independent contexts, split/route publication conflicts, append/reserve allocation, deferred commit counters, router invalidation, and non-`SS8-8` dirty maps remain session-global.
- 🟩 [x] Third refactor slice: internal routed `SS8-8` writer-context inserts now cover the existing non-empty ordinary shelf, no-split case through `InsertWalkedRoutedScalar8Scalar8NoSplitForWriteContext`.
- 🟩 [x] Third-slice lxl result: two writer contexts can route to different shelves, stage inserts independently, publish in either order, and readers then see the published identities.
- 🟩 [x] Third-slice conflict result: two writer contexts routing to the same shelf produce the same deterministic shelf-owner conflict as lower-level staging.
- 🟥 [!] Third-slice deliberate boundary: writer-context routed insert rejects unset routes, terminal-identity route changes, empty-shelf relinks, duplicate-run mutation, and full-shelf split paths because those still require route/topology publication outside shelf-local ownership.
- 🟩 [x] Fourth refactor slice: `Scalar8Scalar8Index` now has internal writer-context wrapper methods for begin, no-split encoded insert, publish, and abort, so callers inside LibraDex do not have to manually route through the session for the proven shelf-local case.
- 🟩 [x] Fourth-slice lxl result: the wrapper path refuses missing root-prefix routes, stages only pre-existing shelf-local inserts, returns operation-facing encoded insert results, and publishes through the same serialized session publication seam.
- 🟩 [x] Fifth refactor slice: `Scalar8Scalar8Writer` now provides a low-friction internal writer facade over one writer context, with direct encoded insert, publish, abort, and dispose-abort lifecycle.
- 🟩 [x] Fifth-slice lxl result: two writer facade instances can stage different shelves, publish in either order, and reject additional inserts after completion.
- 🟥 [!] Fifth-slice deliberate boundary: the writer facade is not a queued writer mode and does not widen supported write shapes; it only removes raw context plumbing for the proven shelf-local case.
- 🟩 [x] Sixth verification slice: `concurrency-contract-sanity` now includes coordinated overlapping task execution with two `Scalar8Scalar8Writer` instances targeting different shelves, proving the facade works under actual concurrent caller timing instead of only sequential lxl staging.
- 🟩 [x] Seventh refactor slice: `Scalar8Scalar8QueuedWriter` now provides an internal serialized facade for overlapping callers that should not manage writer contexts or same-shelf conflicts themselves.
- 🟩 [x] Seventh-slice lxl result: overlapping queued callers targeting the same shelf are serialized successfully, while missing root-prefix routes fall back to the existing serialized insert path inside the same queue.
- 🟥 [!] Seventh-slice caveat: queued writer mode improves caller ergonomics and safety, but it is not the same as true simultaneous same-shelf mutation; the actual concurrent staging win remains the different-shelf writer-context path.
- 🟩 [x] Eighth refactor slice: queued writer results now carry `QueuedInsertPath` attribution so harnesses and future integration code can distinguish writer-context publication from serialized fallback.
- 🟩 [x] Eighth-slice lxl result: raw writer facade inserts report no queued path, queued shelf-local inserts report `WriterContext`, and queued fallback inserts report `SerializedFallback`.
- 🟩 [x] Ninth refactor slice: `UnsignedScalar8Scalar8QueuedWriter` now exposes the queued writer model through typed unsigned scalar keys and identities, preserving `QueuedInsertPath` attribution on typed insert results.
- 🟩 [x] Ninth-slice lxl result: overlapping typed queued inserts publish correctly through writer-context attribution, and typed missing-route fallback reports serialized fallback while remaining readable.
- 🟩 [x] Tenth refactor slice: `LibraDexQueuedWriter<TKey, TIdentity>` now exposes the queued writer model through generic catalog-style `SS8-8` indexes, preserving `QueuedInsertPath` attribution on generic insert results.
- 🟩 [x] Tenth-slice lxl result: overlapping generic queued inserts on `LibraDexIndex<long, long>` publish correctly through writer-context attribution, and generic missing-route fallback reports serialized fallback while remaining readable.
- 🟥 [!] Tenth-slice boundary: generic queued writer support is explicitly limited to `SS8-8` and rejects other generic physical shapes until their writer-context story exists.
- 🟩 [x] Eleventh refactor slice: `LibraDexConcurrencyMode` and `LibraDexConcurrencyOptions` now name the runtime queued-writer mode separately from persisted index shape options.
- 🟩 [x] Eleventh-slice lxl result: queued-writer factories accept explicit `QueuedWriter` options, default to queued-writer mode when called by name, and reject `SingleOwner` options as an unsupported request for that factory.
- 🟩 [x] Twelfth refactor slice: `AbraxasIdentityWriteAdapter<TIdentity>` now binds an identity group to named generic queued writers through `For<TKey>(indexName, options)`.
- 🟩 [x] Twelfth-slice lxl result: the adapter preserves group binding, opens a named `LibraDexIndex<long, long>`, supports overlapping queued writes, and reports writer-context vs serialized-fallback attribution.
- 🟩 [x] Twelfth-slice API boundary: `LibraDexQueuedWriter<TKey, TIdentity>` is now public with an internal constructor so Abraxas integration can use the writer returned by the adapter without constructing one directly.
- 🟩 [x] Thirteenth refactor slice: `AbraxasIdentityWriteAdapter<TIdentity>` now provides one-shot `Insert<TKey>(...)` calls over adapter-owned cached queued writers keyed by index name and key type.
- 🟩 [x] Thirteenth-slice lxl result: concurrent one-shot adapter inserts for the same named index share the cached queue, publish through the writer-context path when eligible, and read back through the original index.
- 🟥 [!] Thirteenth-slice caveat: generic method inference can select `int` for numeric literals; call sites should use typed keys or explicit `TKey` when the catalog index shape matters.
- 🟩 [x] Fourteenth refactor slice: public batch surfaces now expose `Publish()` / `PublishAndDisable()` aliases for generic index batches, index batch managers, identity-group batch managers, and encoded `SS8-8` batch paths.
- 🟩 [x] Fourteenth-slice lxl result: `Publish` delegates to the existing successful commit path, preserving telemetry and readback behavior while giving Abraxas-facing code a non-database term for visibility/durability cadence.
- 🟩 [x] Fourteenth-slice terminology result: `Session` remains a backing/cache/lifetime scope; `Publish` names staged write visibility, and file-backed durability remains an implementation concern below the public batch spelling.
- 🟥 [!] Fourteenth-slice caveat: result type names and internal `DataKernel.Commit()` remain unchanged in this slice for compatibility; a deeper rename would be a broader API and storage vocabulary pass.
- 🟩 [x] Fifteenth refactor slice: `Scalar8Scalar8QueuedWriter` no longer serializes the full writer-context-eligible insert operation; different-shelf no-split inserts can stage through independent writer contexts before serialized publication.
- 🟩 [x] Fifteenth-slice same-shelf result: same-shelf writer-context ownership now raises a typed internal retry signal so queued callers wait and retry instead of falling through to a stale serialized fallback.
- 🟩 [x] Fifteenth-slice cache result: session router read projections now use a session-local cache lock around direct views, multi-byte views, router page bytes, router arena bytes, promoted router views, and cache invalidation so concurrent staging does not corrupt route-cache dictionaries.
- 🟩 [x] Fifteenth-slice harness result: `concurrency-contract-sanity` now covers overlapping queued writer same-shelf inserts, overlapping queued writer different-shelf inserts, typed unsigned queued writer overlap, generic queued writer overlap, and Abraxas adapter one-shot overlap.
- 🟥 [!] Fifteenth-slice caveat: publication is still serialized, unsupported topology work still falls back through the serialized insert path, and broader physical shapes do not yet have writer-context equivalents.
- 🟩 [x] Sixteenth refactor slice: `SS8-8` writer-context route staging now enters a shared topology gate, while queued-writer serialized fallback enters the exclusive side before route initialization, split, duplicate-run, or other non-shelf-local topology work.
- 🟩 [x] Sixteenth-slice lxl result: a queued fallback insert for an unset route can overlap a queued writer-context insert to an existing shelf without corrupting route assumptions; fallback reports `SerializedFallback`, and the existing-shelf insert still reports `WriterContext`.
- 🟩 [x] Sixteenth-slice design result: the gate is below the Abraxas-facing queued writer API, so callers still submit ordinary inserts while LibraDex decides between independent shelf-local staging and exclusive topology fallback internally.
- 🟥 [!] Sixteenth-slice caveat: this gate serializes unsupported topology fallback against writer-context route staging; it does not make topology mutation itself independently concurrent yet.
- 🟩 [x] Seventeenth lxl slice: `concurrency-contract-sanity` now fills an `SS8-8` shelf to capacity, then overlaps the queued split-triggering insert with a queued writer-context insert into another existing shelf.
- 🟩 [x] Seventeenth-slice result: the split-triggering caller reports `SerializedFallback` and remains readable after the split, while the other caller still reports `WriterContext` and remains readable from its original shelf-local route.
- 🟩 [x] Seventeenth-slice implication: the topology fallback gate now has coverage for both unset-route initialization and full-shelf split fallback overlapping normal writer-context staging.
- 🟩 [x] Eighteenth lxl slice: `concurrency-contract-sanity` now fills an `SS8-8` same-key shelf to capacity, then overlaps the queued duplicate-run fallback insert with a queued writer-context insert into another existing shelf.
- 🟩 [x] Eighteenth-slice result: the duplicate-run caller reports `SerializedFallback` and remains readable after terminal identity conversion, while the other caller still reports `WriterContext` and remains readable from its original shelf-local route.
- 🟩 [x] Eighteenth-slice read-path finding: terminal identity roots were classified by the uncached route classifier but not by the arena-cache classifier, and exact-key `SS8-8` range reads rejected terminal-root boundaries.
- 🟩 [x] Eighteenth-slice fix: the arena-cache classifier now recognizes `TerminalIdentityRoot`, and exact same-key `SS8-8` range reads can copy identities from a terminal identity route.
- 🟩 [x] Eighteenth-slice caveat resolved in nineteenth slice: terminal-root cross-range traversal is now covered for conservative and coalesced materialized range reads.
- 🟩 [x] Nineteenth lxl slice: terminal roots are point leaves, not shelf extents. Parent-router enumeration and recursive range traversal must copy their identity chains when the root key falls inside the requested range, and coalesced traversal must flush pending shelf runs before copying terminal identities to preserve route-order output.
- 🟩 [x] Nineteenth-slice implementation: added shared `SS8-8` terminal-root range copying in `LibraDexFileSession`, routed exact terminal reads through it, and taught parent-router, coalesced parent-router, recursive traversal, and coalesced recursive traversal paths to accept `TerminalIdentityRoot` targets.
- 🟩 [x] Nineteenth-slice harness: duplicate-run fallback overlap now verifies a mixed terminal-root/shelf materialized range in both conservative and coalesced read modes.
- 🟩 [x] Nineteenth-slice validation: `MSBuild.exe LibraDex.Harness\LibraDex.Harness.csproj /t:Rebuild /p:Configuration=Release /p:Platform=x64 /m:1 /v:minimal /fl /flp:"logfile=artifacts\build-harness-release-x64-terminal-cross-range-20260620.log;verbosity=normal"` passed.
- 🟩 [x] Nineteenth-slice validation: `concurrency-contract-sanity`, `public-surface-api-sanity`, and `ss8-8-index-api-sanity --path artifacts\ss8-8-index-api-sanity-terminal-cross-range.lbdx` passed.
- 🟩 [x] Twentieth lxl slice: same-catalog/different-index queued `SS8-8` inserts do not need public batch loosening. Each queued insert owns a short operation, stages shelf-local bytes in its own writer context, and publishes through the existing serialized session seam.
- 🟩 [x] Twentieth-slice result: `concurrency-contract-sanity` now creates two generic `SS8-8` indexes in one memory catalog, overlaps two queued writers against different indexes, verifies both report `WriterContext`, and reads both index ranges back.
- 🟨 [~] Twentieth-slice session implication: no session rewrite is needed for same-catalog/different-index queued insert staging. Session rework remains useful for public batch queueing, parallel topology fallback, append allocation isolation, broader physical shapes, and published-reader visibility.
- 🟩 [x] Twentieth-slice validation: `MSBuild.exe LibraDex.Harness\LibraDex.Harness.csproj /t:Rebuild /p:Configuration=Release /p:Platform=x64 /m:1 /v:minimal /fl /flp:"logfile=artifacts\build-harness-release-x64-different-index-queued-20260620.log;verbosity=normal"` passed.
- 🟩 [x] Twentieth-slice validation: `concurrency-contract-sanity`, `public-surface-api-sanity`, and focused `git diff --check` passed.
- 🟩 [x] Twenty-first lxl slice: the session-wide `SS8-8` topology gate was broader than needed. It protected same-index route assumptions correctly, but it also made unrelated index roots contend before the actual shared `DataKernel` publication boundary.
- 🟩 [x] Twenty-first implementation: replaced the single session-wide `SS8-8` topology gate with per-root-router topology gates, and added a serialized topology-fallback wrapper that still uses the session publication seam around `DataKernel` mutation.
- 🟩 [x] Twenty-first result: same-catalog/different-index writer-context staging is no longer forced through one topology lock, while fallback route initialization/split/duplicate-run work remains serialized for publication safety.
- 🟩 [x] Twenty-first harness: `concurrency-contract-sanity` now overlaps a same-catalog/different-index queued fallback insert with a writer-context insert and verifies the expected `SerializedFallback` / `WriterContext` attribution plus readback.
- 🟨 [~] Twenty-first caveat: fallback publication itself is still session-serialized because `DataKernel` append cursor, pending segments, direct RAM writes, and commit telemetry are not independently concurrent-safe yet.
- 🟩 [x] Twenty-first validation: `MSBuild.exe LibraDex.Harness\LibraDex.Harness.csproj /t:Rebuild /p:Configuration=Release /p:Platform=x64 /m:1 /v:minimal /fl /flp:"logfile=artifacts\build-harness-release-x64-index-topology-gates-20260620.log;verbosity=normal"` passed.
- 🟩 [x] Twenty-first validation: `concurrency-contract-sanity`, `public-surface-api-sanity`, `ss8-8-index-api-sanity --path artifacts\ss8-8-index-api-sanity-index-topology-gates.lbdx`, and focused `git diff --check` passed.
- 🟩 [x] Twenty-second lxl slice: per-root topology gates exposed a `DataKernel` hazard. Different-index writer-context staging can read shelf bytes while another index fallback mutates or commits shared `DataKernel` state unless fallback publication has an operation-level exclusion.
- 🟩 [x] Twenty-second implementation: added a narrow `SS8-8` writer-operation gate. Writer-context staging enters it shared, so multiple staging reads can proceed concurrently; serialized topology fallback enters it exclusive only while running the fallback under the existing publication seam.
- 🟩 [x] Twenty-second result: no general read hot-path lock was added. The new gate sits only on queued writer-context staging and queued fallback publication, matching the bounded concurrency model.
- 🟨 [~] Twenty-second caveat: this still does not make `DataKernel` itself concurrently mutable. It preserves one safe writer-operation publication lane while allowing independent staging before publication.
- 🟩 [x] Twenty-second validation: `MSBuild.exe LibraDex.Harness\LibraDex.Harness.csproj /t:Rebuild /p:Configuration=Release /p:Platform=x64 /m:1 /v:minimal /fl /flp:"logfile=artifacts\build-harness-release-x64-writer-operation-gate-20260620.log;verbosity=normal"` passed.
- 🟩 [x] Twenty-second validation: `concurrency-contract-sanity`, `public-surface-api-sanity`, `ss8-8-index-api-sanity --path artifacts\ss8-8-index-api-sanity-writer-operation-gate.lbdx`, and focused `git diff --check` passed.
- 🟩 [x] Twenty-third lxl slice: a single-shelf `SS8-8` duplicate-run route with spare capacity is shelf-local. It can accept a sorted identity insert without route topology changes, append allocation, linked-tail mutation, or terminal-root rewrite.
- 🟩 [x] Twenty-third implementation: `Scalar8Scalar8` now supports `InsertIntoSingleShelfDuplicateRun`, and writer-context inserts use it for valid single-shelf duplicate-run targets before falling back for linked/full duplicate-run shapes.
- 🟩 [x] Twenty-third read/publication fix: duplicate-run shelves are now no-op for ordinary deleted-slot normalization, and `Scalar8Scalar8ReadOnly.CopyIdentitiesInKeyRange` copies duplicate-run identity arrays directly.
- 🟩 [x] Twenty-third harness: `concurrency-contract-sanity` now crafts a valid single-shelf duplicate-run fixture, inserts through the queued writer, verifies `WriterContext` attribution, and reads the materialized range back.
- 🟨 [~] Twenty-third caveat: normal new same-key overflow currently converts to terminal identity roots, so this improves legacy/repaired/internal duplicate-run shelves rather than the dominant current overflow path.
- 🟩 [x] Twenty-third validation: `MSBuild.exe LibraDex.Harness\LibraDex.Harness.csproj /t:Rebuild /p:Configuration=Release /p:Platform=x64 /m:1 /v:minimal /fl /flp:"logfile=artifacts\build-harness-release-x64-duplicate-run-context-20260620.log;verbosity=normal"` passed.
- 🟩 [x] Twenty-third validation: `concurrency-contract-sanity`, `public-surface-api-sanity`, `ss8-8-index-api-sanity --path artifacts\ss8-8-index-api-sanity-duplicate-run-context.lbdx`, and focused `git diff --check` passed.
- 🟨 [~] Next lxl target: decide whether to move the next proof into the AbraxasDB integration repo, or broaden LibraDex writer-context coverage beyond the currently proven `SS8-8` shelf-local insert shape.
- 🟥 [!] Drift guard: this refactor accommodates future concurrent writers; it does not yet enable concurrent same-session public batches.

### 2026-06-20 Write-Window Ownership State LXL

- 🟩 [x] LXL question: would the current `durabilityBatchActive` check be enough for the first deterministic overlapping-writer guardrail?
- 🟥 [!] Finding: no. `BeginDurabilityBatch` currently checks `durabilityBatchActive`, then sets it, then clears mutable batch/session state. Two threads can both observe `false` before either writes `true`.
- 🟥 [!] Contrived failing path: writer A and writer B enter `BeginDurabilityBatch` on the same `LibraDexFileSession`; both read `durabilityBatchActive == false`; both pass the existing guard; both begin clearing mutable batch caches and sharing the same kernel pending state.
- 🟩 [x] Implementation implication: the first diagnostic claim must use a single atomic transition, for example a compare-exchange from unclaimed to claimed, before any mutable session state is cleared.
- 🟩 [x] Minimal diagnostic fields should be separate from `durabilityBatchActive`: claimed/unclaimed state, owner managed thread id, owner operation id, and a next-operation counter for readable failure messages.
- 🟩 [x] Options plumbing implication: `LibraDexFileSession` should retain the public diagnostics level or a precomputed detailed-ownership boolean; the semantic ownership check should not be hidden in `DataKernel`.
- 🟩 [x] Claim point: diagnostics-enabled `BeginDurabilityBatch` claims the write window before setting `durabilityBatchActive`, clearing pending flags, clearing mutable caches, or releasing mutable shelves.
- 🟩 [x] Verify point: diagnostics-enabled `CommitDurabilityBatch` and `AbortDurabilityBatch` verify the active owner before flushing shelves, discarding pending state, clearing router caches, or resetting batch fields.
- 🟩 [x] Release point: after an owned commit/abort reaches the same cleanup point that resets the batch lifecycle, release the diagnostic claim so the next owner can enter.
- 🟩 [x] Failure behavior: if the atomic claim fails, throw `InvalidOperationException` with same-session concurrent writes wording; do not wait, retry, or queue.
- 🟩 [x] Performance boundary: this atomic claim is only for `DiagnosticsLevel.Detailed`; `Off` and `Counters` keep the existing low-overhead path.
- 🟨 [~] Open implementation detail: if commit fails before normal cleanup, the first packet can preserve current batch-failure behavior and only release the diagnostic claim when the session actually leaves the active batch lifecycle.
- 🟥 [!] Drift guard: the atomic diagnostic claim is a misuse detector and harness anchor. It is not a general lock, not a queue, not a reader publication model, and not proof of true concurrent writes.

### 2026-06-20 Commit/Abort Ownership Release LXL

- 🟩 [x] LXL question: should the diagnostic write-window owner be released in a `finally`, or only when the session exits the current batch lifecycle?
- 🟩 [x] Finding: `CommitDurabilityBatch` currently performs dirty-shelf flushes, then `kernel.Commit()`, then router-cache/session cleanup, and only then sets `durabilityBatchActive = false`.
- 🟩 [x] Finding: if a dirty-shelf flush or `kernel.Commit()` throws today, the session does not run the normal cleanup block and the batch lifecycle remains active by existing behavior.
- 🟩 [x] Finding: `AbortDurabilityBatch` has a simpler path: it captures deferred count, calls `kernel.DiscardPending()`, clears router caches, resets batch fields, clears dirty shelf state, and returns.
- 🟩 [x] Finding: `LibraDexFileSessionDurabilityBatch.Commit()` marks the batch handle complete only after session commit succeeds; `Abort()` marks the handle complete before calling the session abort.
- 🟥 [!] Finding: with a new abort-owner check, the current handle-level abort order would mark the handle complete before the session rejects the abort, so later `Dispose()` would not retry aborting the still-active session batch.
- 🟨 [~] Design implication: first-packet diagnostics should not change commit-failure recovery semantics. Release the diagnostic owner at the same point the session leaves the active durability batch.
- 🟩 [x] Handle fix implication: align `LibraDexFileSessionDurabilityBatch.Abort()` with `Commit()` by setting `completed = true` only after `session.AbortDurabilityBatch()` succeeds.
- 🟩 [x] Commit owner check point: after confirming `durabilityBatchActive`, verify the owner before `CreateBatchStorageDiagnosticsSnapshot()` and all dirty-shelf flushes.
- 🟩 [x] Abort owner check point: after confirming `durabilityBatchActive`, verify the owner before `kernel.DiscardPending()` and all cleanup.
- 🟩 [x] Release point: release the diagnostic claim immediately after `durabilityBatchActive = false` in the normal commit and abort cleanup path.
- 🟨 [~] Future hardening option: a later failure-state design can mark the session `Failed` or `NeedsRebuild` after partial commit/cleanup failures, but that belongs to the index-state/status lane.
- 🟥 [!] Drift guard: do not use the diagnostic owner release as a rollback, recovery, or corruption-state mechanism.

### 2026-06-20 Thread Owner vs Batch Token LXL

- 🟩 [x] LXL question: should the first diagnostic owner verifier be managed thread id, or a write-window token carried by the durability-batch handle?
- 🟥 [!] Finding: thread id alone is too strict as authority. A caller can begin a synchronous batch on one thread and later commit/abort the same batch handle from another thread, especially when an outer framework schedules work.
- 🟩 [x] Finding: `LibraDexFileSessionDurabilityBatch` is the natural ownership carrier because all public batch shapes eventually hold one and route commit/abort through it.
- 🟩 [x] Contrived acceptable path: caller begins a batch, stores the returned batch handle, performs serialized work, then commits from a continuation thread. Token authority would accept the same batch handle; thread authority would reject it even though there is no overlapping writer.
- 🟩 [x] Contrived misuse path: writer A owns batch token `17`; writer B attempts a second `BeginDurabilityBatch` on the same session. Atomic claim rejects writer B before mutation, independent of thread id.
- 🟨 [~] Finding: thread id remains useful in exception text because it helps explain where the active write window was claimed, but it should not be the verifier for commit/abort.
- 🟩 [x] Design implication: `BeginDurabilityBatch` should create the operation token after the atomic claim succeeds and pass that token into `LibraDexFileSessionDurabilityBatch`.
- 🟩 [x] Design implication: `CommitDurabilityBatch` and `AbortDurabilityBatch` should accept the operation token from the handle and compare it to the active token before mutating session state.
- 🟨 [~] Follow-up hardening: if token verification allows cross-thread commit/abort, the batch handle's `completed` flag is still not a concurrent-use guard. A later strict mode could make handle completion atomic, but the first packet only needs deterministic same-session writer overlap detection.
- 🟥 [!] Drift guard: do not describe the token as making batch handles thread-safe. It only prevents the wrong batch/window from committing or aborting session state.

### 2026-06-20 Harness Probe Shape LXL

- 🟩 [x] LXL question: how should `concurrency-contract-sanity` prove the first guardrail without becoming a timing-based stress test?
- 🟩 [x] Finding: `LibraDex.Harness` already has `InternalsVisibleTo("LibraDex.Harness")`, so the harness can use internal session/batch surfaces for narrow contract probes when public APIs do not expose the needed boundary.
- 🟩 [x] Finding: the command registry is the static `HarnessCommand[]` table in `LibraDex.Harness/Program.cs`; `concurrency-contract-sanity` belongs in `CommandLane.Validation`.
- 🟩 [x] First public-path probe: create a memory catalog with `CatalogOptions.DiagnosticsLevel = Detailed`, open a normal index batch, hold it with a gate, and attempt a second same-session `BeginBatch` from another task.
- 🟩 [x] Expected result: the second begin fails with `InvalidOperationException` whose message names unsupported same-session concurrent writes; the first batch can still abort/commit normally after the second attempt fails.
- 🟩 [x] Coordination shape: use `ManualResetEventSlim`, `TaskCompletionSource`, or equivalent explicit gates. The probe must not rely on sleeps, scheduler luck, or long-running inserts.
- 🟩 [x] Mutation boundary: the second task should fail at begin/claim time before any second-writer insert occurs, so the assertion is about the write-window claim, not about shelf mutation cleanup.
- 🟩 [x] Token probe: after the first batch is opened, commit or abort the same batch handle from a different task to prove token authority is not thread authority.
- 🟩 [x] Abort-order probe: force an abort-owner failure through an internal mismatched-token path, then confirm the handle is not marked complete before a valid cleanup path can run.
- 🟨 [~] Public-vs-internal split: public APIs should cover the common overlapping-writer behavior; internal hooks may cover mismatched-token and abort-order edge cases because those are implementation guardrails.
- 🟥 [!] Drift guard: do not add a negative-control expectation that `DiagnosticsLevel.Off` is safe. `Off` means unsupported/undefined same-session concurrency remains unchecked, not allowed.
- 🟥 [!] Drift guard: do not turn this into a performance or randomized stress command. Stress can be separate after the contract probe exists.

### 2026-06-20 Failure Message Contract LXL

- 🟩 [x] LXL question: what should the first diagnostic ownership exception tell the developer?
- 🟩 [x] Finding: existing lifecycle failures such as "A LibraDex session durability batch is already active" are correct for ordinary misuse but too thin for the new concurrency guardrail.
- 🟩 [x] Required concept 1: same-session concurrent writes are unsupported in the current `SingleOwner` contract.
- 🟩 [x] Required concept 2: the failure occurred before the competing writer was allowed to mutate shared session state.
- 🟩 [x] Required concept 3: caller choices are serialize access, use isolated catalog/session instances over isolated backing resources, materialize read results, or use a future queued-writer/published-reader mode when available.
- 🟩 [x] Required concept 4: when available, include the active write-window operation id and the claiming thread id as diagnostic context, not as public synchronization API.
- 🟩 [x] Required concept 5: if commit/abort token verification fails, distinguish "wrong batch token for active write window" from "another writer is already active."
- 🟨 [~] Candidate begin-failure shape: "LibraDex detected overlapping same-session writes. Catalog/session writes are single-owner in the current concurrency contract; serialize access or use isolated catalogs. The competing write was rejected before shared session state was mutated. Active write window: operation {id}, claimed by thread {threadId}."
- 🟨 [~] Candidate commit/abort-token failure shape: "The LibraDex durability batch token does not own the active session write window. Commit or abort must be issued by the batch handle that claimed the write window; serialize same-session writes or use isolated catalogs."
- 🟥 [!] Drift guard: do not say "thread-safe", "transaction", "rollback", "snapshot", or "lock wait" in this exception text.
- 🟥 [!] Drift guard: do not imply `DiagnosticsLevel.Detailed` enables safe concurrent writes. It only makes unsupported overlap fail loudly.

### 2026-06-20 QueuedWriter Boundary LXL

- 🟩 [x] LXL question: can future `QueuedWriter` be implemented by quietly adding a lock or wait inside `BeginDurabilityBatch`?
- 🟥 [!] Finding: not cleanly for the current public batch shape. `BeginBatch` returns a mutable batch handle, caller code performs arbitrary writes, and commit/abort happens later, so LibraDex cannot serialize a whole write operation unless the queued boundary owns the full begin/work/commit unit.
- 🟩 [x] Contrived blocking-begin path: writer A opens a batch and pauses; writer B calls `BeginBatch` in queued mode and blocks until writer A commits/aborts. This is simple, but caller-visible wait behavior must be explicit and diagnosable.
- 🟨 [~] Contrived submission path: caller submits a write delegate or operation object to LibraDex; LibraDex runs it under the session write window and owns commit/abort policy. This is cleaner queue ownership, but it is a new API shape and must not be smuggled into existing diagnostics.
- 🟨 [~] Finding: Abraxas may be the better first queue owner because it already owns source-write acceptance, index catch-up policy, and `EventualIndexed` state.
- 🟩 [x] Design implication: first packet should reserve `QueuedWriter` semantics but not implement them; the current diagnostic guardrail stays fail-fast.
- 🟩 [x] Future `QueuedWriter` minimum decisions: blocking versus async submission, cancellation behavior, timeout behavior, fairness/order, commit/abort ownership, exception propagation, and queue depth/wait diagnostics.
- 🟥 [!] Drift guard: do not let `DiagnosticsLevel.Detailed` become a hidden queue or wait mode.
- 🟥 [!] Drift guard: do not describe a blocking `BeginBatch` as low/no friction unless the wait/cancel behavior is explicit and observable.

### 2026-06-20 PublishedReaders Boundary LXL

- 🟩 [x] LXL question: can LibraDex claim concurrent published readers by treating current `DataKernel.Commit()` as the visibility boundary?
- 🟥 [!] Finding: no. Current readers use the live session, live caches, and `DataKernel.Read`, which consults pending overlays and memory arena state rather than a pinned published view.
- 🟥 [!] Finding: RAM-backed writers can mutate `VolatileMemoryArena` directly through append/reserve/direct-write paths before `Commit`, so commit is not a universal visibility boundary.
- 🟩 [x] Contrived file-backed path: writer stages pending bytes; same-session reader calls into a route/shelf read; `DataKernel.Read` can consult pending segments before file bytes, so the reader may observe writer-local state.
- 🟩 [x] Contrived RAM-backed path: writer gets a writable arena span and changes shelf bytes; same-session reader later reads that shelf before commit; the reader can observe changed arena bytes because there is no reader-pinned arena version.
- 🟥 [!] Cursor path: lazy cursors continue touching live session/index state after the cursor object is returned, so cursor creation alone cannot pin a published state.
- 🟩 [x] Minimum future boundary: a visible-index-state object must identify the readable directory/root/router/shelf state separately from writer-private staging.
- 🟨 [~] Possible implementation families: materialized identity results only, reader-pinned immutable route/shelf snapshots, copy-on-publish arena/versioning, or a generationed state table that readers pin for cursor lifetime.
- 🟨 [~] Abraxas first-contract implication: normal Abraxas reads should keep using materialized identity results and should not expose live cursors as concurrent-reader proof.
- 🟥 [!] Drift guard: do not call current file-backed commit or RAM-backed commit a snapshot boundary.
- 🟥 [!] Drift guard: do not claim `PublishedReaders` until pending overlays, direct RAM mutation, cache invalidation, and cursor lifetime pinning have a real publication model.

### 2026-06-20 Cross-Process Boundary LXL

- 🟩 [x] LXL question: does current file-open behavior solve cross-process concurrency for the first Abraxas slice?
- 🟨 [~] Finding: it helps, but it is not a full contract. `DataKernel.Open` opens file-backed catalogs with `FileAccess.ReadWrite` and `FileShare.Read`, so a second LibraDex read/write open should fail while the first handle is open.
- 🟨 [~] Finding: `FileShare.Read` still permits other read-only OS handles, so external tools or a future LibraDex read-only open could read file bytes without a LibraDex version/published-state protocol.
- 🟩 [x] Contrived same-file LibraDex writer path: process A opens a catalog read/write; process B attempts `Catalog.Open` on the same path; B also requests read/write and is blocked by the first handle's share mode before reaching LibraDex session mutation.
- 🟨 [~] Contrived raw reader path: process A has an active writer; process B opens the file read-only outside LibraDex. The OS may allow the handle, but LibraDex has not defined what raw bytes, directory state, cache state, or partial write visibility mean to that reader.
- 🟨 [~] Abraxas first-contract implication: the efficiency gate can keep cross-process synchronization out of the first packet, as long as Abraxas treats "no other process reported" as the precondition for using the low-overhead path.
- 🟩 [x] First-packet decision: document same-process/session concurrency; do not add cross-process locks, advisory lock files, shared-reader opens, or file-version handshakes in this packet.
- 🟨 [~] Future cross-process decisions: exclusive writer lock shape, read-only LibraDex open, version/generation handshake, advisory lock file, stale-reader detection, and error wording when the efficiency gate is open or violated.
- 🟥 [!] Drift guard: do not present `FileShare.Read` as a shared-reader model. It is only the current OS handle policy for read/write LibraDex opens.
- 🟥 [!] Drift guard: do not claim cross-process same-file writes are queued, safe, or recoverable.

### 2026-06-20 Maintenance And Optimizer Boundary LXL

- 🟩 [x] LXL question: do public maintenance and internal optimizer paths need separate concurrency wording?
- 🟩 [x] Finding: public maintenance APIs are still mostly intent/result descriptors; current public-surface sanity validates descriptor shape such as `Validate`, `Optimize`, `Repack`, and `Cache`, not physical background mutation.
- 🟥 [!] Finding: internal varlen optimizer and route replacement paths can rewrite route targets, append replacement shelves/routers, invalidate route caches, release memory extents, and publish through durability batches.
- 🟩 [x] Finding: the existing version-checked route publish primitive is useful future machinery because stale expected targets can be rejected before overwriting a newer route.
- 🟥 [!] Finding: route-version checks are local conflict checks, not a session ownership model. They do not protect shared batch caches, pending segments, append allocation, router caches, or live cursors by themselves.
- 🟩 [x] Contrived safe first-packet path: maintenance descriptor calls remain documentation-scoped and do not need extra runtime ownership until they connect to physical storage mutation.
- 🟥 [!] Contrived unsafe connected path: a background optimizer publishes a route replacement while a foreground writer holds a durability batch or a live cursor is walking the old route; without a shared write-window/published-reader model, cache and cursor state can become incoherent.
- 🟩 [x] First-packet decision: any connected physical maintenance or optimizer publish must use the same diagnostic write-window ownership boundary as ordinary writers.
- 🟨 [~] Future decision: maintenance may later run through `QueuedWriter`, an explicit maintenance scheduler, or a published-state replacement protocol, but that is separate from the first diagnostic guardrail.
- 🟥 [!] Drift guard: do not treat "background" or "optimization" as read-only. Route replacement, repack, compaction, and cache-affecting physical maintenance are writes for concurrency purposes.
- 🟥 [!] Drift guard: do not use local route-version publish success as proof that concurrent writers or live cursors are safe.

### 2026-06-20 Source-Write Succeeds, Index-Write Fails LXL

- 🟩 [x] LXL question: if Abraxas accepts a source write and LibraDex indexing then fails, should LibraDex/Abraxas try to roll back, block readers, or mark/rebuild?
- 🟩 [x] Finding: because LibraDex is an identity index over external source data, the source write and the index projection are separate durability/consistency domains.
- 🟩 [x] Contrived case: Abraxas stores source entity `Customer:42`, then the LibraDex-backed identity index update fails before the new identity is indexed.
- 🟩 [x] Correct state shape: Abraxas can report the source write as accepted while marking the index `EventualIndexed`, `Stale`, `Incomplete`, or `NeedsRebuild` depending on how much it knows.
- 🟩 [x] LibraDex-owned state shape: if LibraDex detects structural unreliability or interrupted publication, it should report `Failed`, `Incomplete`, or `NeedsRebuild` rather than trying to undo external source data.
- 🟩 [x] Reader result shape: normal identity lookups may answer from the currently visible index state, but caller policy must be able to see that the index may lag accepted source data.
- 🟩 [x] Remediation shape: replay source data into the existing index if structurally safe, rebuild the affected index/catalog, or route Abraxas reads through a fallback/source path until catch-up completes.
- 🟨 [~] Persistence/open issue: first packet does not persist LibraDex structural state, so durable `NeedsRebuild`/`Failed` markers need the later status-surface slice.
- 🟩 [x] First-packet implication: failure wording and XML/API docs should prefer "mark stale/incomplete/needs rebuild" over rollback language.
- 🟥 [!] Drift guard: do not imply LibraDex can roll back accepted source writes.
- 🟥 [!] Drift guard: do not hide `EventualIndexed` or stale/incomplete status when query correctness depends on whether source writes have caught up to the index.

### 2026-06-20 Abraxas Adapter LXL

- 🟩 [x] LXL question: what should Abraxas use as the first concurrency-safe-ish read boundary?
- 🟩 [x] Finding: `AbraxasIdentityQueryAdapter.Get` and `GetIdentities` materialize an identity list before returning, so they are the correct first Abraxas-facing read boundary.
- 🟩 [x] Finding: `OpenCursor` and `GetCursor` return live LibraDex cursors and should not be treated as disconnected snapshots.
- 🟩 [x] Finding: `EventualIndexed` belongs to index-state/reporting, not cursor behavior and not pending writer visibility.
- 🟨 [~] Design implication: Abraxas integration tests should prove serialized writes plus materialized reads first; cursor tests should be single-owner or explicitly marked unsupported for concurrent writer scenarios.
- 🟥 [!] Drift guard: do not let the adapter's cursor convenience methods become the default Abraxas concurrent-read story.

### 2026-06-20 Abraxas Status Surface LXL

- 🟩 [x] LXL question: should ordinary Abraxas reads return identities plus index-state metadata, or should status be a separate surface?
- 🟩 [x] Contrived case: caller asks Abraxas for identities from a LibraDex-backed index while the index is `EventualIndexed`.
- 🟩 [x] Finding: `AbraxasIdentityQueryAdapter<TIdentity>.Get` and `GetIdentities` currently return `IReadOnlyList<TIdentity>`, keeping normal lookup syntax low-friction and identity-only.
- 🟩 [x] Finding: wrapping every normal read in a result-with-status object would leak status ceremony into the hot developer path and change the adapter's narrow purpose.
- 🟩 [x] Finding: Abraxas can own source-relative status such as `EventualIndexed` outside the identity result because it already owns the source write and indexing queue/catch-up policy.
- 🟩 [x] Finding: LibraDex structural diagnostics can be pulled or mirrored by Abraxas through a separate status/diagnostics surface once that API exists.
- 🟩 [x] First-contract decision: keep normal Abraxas identity reads materialized and identity-only; expose freshness/rebuild/diagnostic state through an explicit status path rather than result wrapping.
- 🟨 [~] Future API decision: Abraxas status naming remains open. Candidate shape is an Abraxas-owned per-index/backend status object that can include `EventualIndexed`, last known LibraDex structural diagnostic, and rebuild/catch-up intent.
- 🟥 [!] Drift guard: do not hide `EventualIndexed`; the state should be visible to caller policy, but visibility does not require polluting every read result shape.

### 2026-06-20 Cursor Diagnostic Policy LXL

- 🟩 [x] LXL question: should the first guardrail also reject live cursor reads during an active same-session write?
- 🟩 [x] Contrived case: caller opens an identity cursor, pauses before the first `Next`, another same-session caller begins a write window, then the first caller resumes cursor iteration.
- 🟩 [x] Finding: `CatalogIdentityGroupIndexes.GetCursor` materializes the condition descriptor, creates `criterion.IDsWith(...).Iterate<TIdentity>()`, and stores that enumerator inside `LibraDexIdentityCursor<TIdentity>`.
- 🟩 [x] Finding: checking only cursor creation would miss already-open cursors because the underlying enumerator advances later when `Next` calls `MoveNext`.
- 🟥 [!] Finding: adding a `Next` check would put concurrency diagnostics on the read traversal hot path and still would not create a true published-reader snapshot.
- 🟩 [x] Contrived case: caller opens an index cursor, advances to a tuple, then calls `DeleteCurrent`, `DeleteRemaining`, `DeleteAll`, or `SetCurrentKey` while another write window is active.
- 🟩 [x] Finding: `LibraDexIndexCursor<TKey, TIdentity>` stores mutators and can replay delete/re-key operations through the target index while preserving the forward-only cursor stream.
- 🟥 [!] Finding: cursor-local mutation is write behavior even though it is reached from a cursor object; it should be covered by writer ownership diagnostics before any future strict cursor mode claims read/write safety.
- 🟩 [x] First-packet decision: document live cursors as single-owner/live-session and unsupported with concurrent same-session writes; do not add per-`Next` checks, broad read locks, or hidden read synchronization by default.
- 🟨 [~] Future strict diagnostics decision: optional mode may fail `OpenCursor` during an active write window for early misuse detection, but that must be documented as incomplete unless `Next` or a published-reader snapshot model is also added.

### 2026-06-20 Future Mode LXL

- 🟩 [x] LXL question: should the first concurrency improvement be a hidden lock or a named mode?
- 🟩 [x] Finding: named modes are safer for LibraDex's performance and developer-friction goals because each mode states its cost and ownership boundary.
- 🟩 [x] Finding: `SingleOwner` is the current contract and should remain the default.
- 🟩 [x] Finding: `QueuedWriter` began as a future caller-convenience mode and now exists for bounded `SS8-8` insert submissions; it is still not a full physical concurrent-writer model.
- 🟩 [x] Finding: `PublishedReaders` is the future reader-intuition mode, but it requires visible-index-state publication before it is honest.
- 🟨 [~] Design implication: mode APIs can wait until production work reaches guardrails or adapter status; the checklist is only reserving names and semantics now.
- 🟥 [!] Drift guard: do not let mode names imply database isolation or rollback.

### 2026-06-20 True Concurrent Writer LXL

- 🟩 [x] LXL question: can the core design still support better concurrent writes later?
- 🟩 [x] Finding: yes in principle, because many physical mutations can be made local to shelves/routes and the index is rebuildable identity metadata rather than source data.
- 🟥 [!] Finding: no in current dried ink, because writer paths converge on shared session/kernel state before the physical shelf-local idea matters.
- 🟩 [x] Finding: `QueuedWriter` is the right intermediate step if caller ergonomics require concurrent write submissions soon.
- 🟨 [~] Design implication: true per-index/per-shelf writer work should become a separate research/implementation lane after publication, cache versioning, writer-local staging, and deterministic conflict harnesses exist.
- 🟥 [!] Drift guard: do not let the phrase "different keys" bypass conflict analysis; same shelf, route, append cursor, and publication state can still collide.

## Evidence To Gather

- 🟩 [x] Map current mutable state in `Catalog/*`.
- 🟩 [x] Map current mutable state in `FileSession/*`.
- 🟩 [x] Map current mutable state in `DataKernel/*`.
- 🟨 [~] Map current mutable state in `Indexes/*` session and range-reader types.
- 🟨 [~] Identify any static/shared state that affects catalog or index access.
- 🟩 [x] Find existing file-open sharing flags and stream ownership behavior.
- 🟩 [x] Run lxl scenarios through the use-case matrix before choosing implementation strategy.
- 🟩 [x] Case 1 lxl: single writer, no reader.
- 🟩 [x] Case 2 lxl: single writer, concurrent readers.
- 🟩 [x] Case 3 lxl: multiple writers to different keys in the same index.
- 🟩 [x] Case 4 lxl: multiple writers to different indexes in the same catalog.
- 🟩 [x] Case 5 lxl: multiple writers to different catalogs.
- 🟩 [x] Case 6 lxl: writer plus optimization, compaction, split, or route maintenance.
- 🟩 [x] Case 7 lxl: multiple processes against the same file-backed catalog.
- 🟩 [x] Case 8 lxl: RAM-backed catalog concurrent readers and writers.

## Source Evidence Anchors

- 🟩 [x] `FileSession/LibraDexFileSession.cs`: session owns mutable route caches, shelf caches, deleted payload queues, batch flags, deferred commit counters, and the underlying `DataKernel`.
- 🟩 [x] `FileSession/LibraDexFileSession.cs`: `BeginDurabilityBatch` uses a plain `durabilityBatchActive` flag and clears mutable batch caches without synchronization.
- 🟩 [x] `FileSession/LibraDexFileSession.cs`: current durability-batch lifecycle exceptions are short state failures; diagnostic concurrency failures need more actionable unsupported-concurrency wording.
- 🟩 [x] `FileSession/LibraDexFileSession.cs`: the constructor currently stores `DataKernel`, `Superblock`, and `IndexDirectory`, but does not retain catalog diagnostics policy for session-level semantic checks.
- 🟩 [x] `FileSession/LibraDexFileSession.cs`: `CommitAndInvalidateRouterReadCache`, `CommitAndDeferRouterReadCacheInvalidation`, and `CommitWithoutInvalidatingRouterReadCache` defer publication when a durability batch is active.
- 🟩 [x] `FileSession/LibraDexFileSessionDurabilityBatch.cs`: commit/abort delegate to session-level durability batch methods; no concurrency ownership token or thread check exists.
- 🟩 [x] `FileSession/LibraDexFileSessionDurabilityBatch.cs`: the handle currently stores only the session and `completed` flag, so it can carry a first-packet operation token without changing public batch APIs.
- 🟩 [x] `FileSession/LibraDexFileSessionDurabilityBatch.cs`: `Commit()` marks the handle complete after session commit succeeds, while `Abort()` currently marks complete before session abort runs.
- 🟩 [x] `Catalog/Catalog.cs`: `CatalogOptions.DiagnosticsLevel` is translated into `DataKernelTelemetryOptions` at create/open time and the original options are retained on `Catalog`, not on `LibraDexFileSession`.
- 🟩 [x] `DataKernel/DataKernelTelemetryOptions.cs`: storage telemetry knows the requested diagnostics level, but it is intentionally storage telemetry; write-window ownership is a LibraDex session contract, not a raw storage-layer behavior.
- 🟩 [x] `DataKernel/DataKernel.cs`: file-backed open uses `FileAccess.ReadWrite` with `FileShare.Read`.
- 🟩 [x] `DataKernel/DataKernel.cs`: `Read` consults pending segments before backing storage.
- 🟩 [x] `DataKernel/DataKernel.cs`: memory-backed `Append`, `Reserve`, and `ReserveAt` can write or expose writable spans directly over `VolatileMemoryArena` before commit.
- 🟩 [x] `DataKernel/DataKernel.cs`: `TryWriteMemoryDirect` and `TryGetMemoryWritableSpanDirect` make memory-backed in-place mutation visible without pending commit records.
- 🟩 [x] `DataKernel/DataKernel.cs`: memory-backed `Commit` only publishes fallback pending segments and clears telemetry/staging counters; direct arena writes have already reached the visible arena.
- 🟩 [x] `DataKernel/DataKernel.cs`: pending segments, commit planner lists, memory arena dictionaries/ranges, telemetry counters, and append cursor are unsynchronized mutable state.
- 🟩 [x] `Catalog/CatalogIndexMetadata.cs`: persisted index metadata is structural and shape/version oriented, not source-freshness or health-state storage.
- 🟩 [x] `Layouts/IndexDirectoryLayout.cs` and `PublicApi/IndexDirectorySlotSnapshot.cs`: directory slot state currently distinguishes empty versus active; flags/generation are not an established status protocol.
- 🟩 [x] `PublicApi/SuperblockDeveloperMetadata.cs`: developer metadata is durably stored but explicitly not interpreted by LibraDex.
- 🟩 [x] `LibraDex.Harness/Program.cs`: validation commands are registered in the static harness command table; future `concurrency-contract-sanity` belongs there as a validation command.
- 🟩 [x] `Properties/AssemblyInfo.cs`: `InternalsVisibleTo("LibraDex.Harness")` allows narrow internal contract probes when public APIs cannot expose mismatched-token or handle-order cases.
- 🟩 [x] `Indexes/LibraDexIndex.cs`: convenience `Insert` opens a batch, inserts, commits, and returns; group/index batch managers can redirect to active shared mutable batch state.
- 🟩 [x] `PublicApi/LibraDexBatch.cs`: batch insert and commit own counters and shape-specific shared-batch caches without synchronization.
- 🟩 [x] `Catalog/Catalog.cs` and `Catalog/CatalogIdentityGroupBatchManager.cs`: catalog/group batch dictionaries and active typed-batch state are mutable and session-scoped.
- 🟩 [x] `ConditionBuilder/LibraDexConditionCursors.cs` and `PublicApi/LibraDexIdentityCriteriaExecution.cs`: cursor/iterator APIs are lazy and can keep touching live index/session state after the API returns.
- 🟩 [x] `ConditionBuilder/LibraDexConditionCursors.cs`: index cursors expose cursor-local delete and re-key operations, so cursor mutation must be treated as write behavior for diagnostics.
- 🟩 [x] `Catalog/CatalogIndexFactories.cs`, `Indexes/LibraDexIndex.cs`, and `Indexes/LibraDexStringScalar8Index.cs`: cursor factories pass live enumerable streams into cursor wrappers rather than published snapshots.
- 🟩 [x] `PublicApi/PublicApiSurface.cs`: public maintenance APIs are currently intent/scaffold surfaces and do not mutate physical storage.
- 🟩 [x] `Optimizers/LibraDexVarLenOptimizerReplacementPlanner.cs`: internal varlen optimizer replacement paths begin durability batches and publish route replacements.
- 🟩 [x] `FileSession/LibraDexFileSession.cs`: version-checked route publish can reject stale expected targets, but still mutates route state and commits/defer-commits through the session.
- 🟩 [x] `LibraDex.Harness/Commands/RawHarness.PublicSurface.cs`: `vs8-route-versioned-publish-sanity` proves stale route publish rejection and current-target publish success, not broad concurrent writer safety.

## Pending Decisions

- 🟩 [x] Accepted first Abraxas concurrency contract: Abraxas serializes LibraDex-backed writes; LibraDex is single-owner per active catalog/session.
- 🟩 [x] Accepted rollback scope: no database-style committed rollback; unpublished staged work may be abandoned.
- 🟩 [x] Accepted reader visibility target: ordinary readers see committed/published state only.
- 🟩 [x] Accepted product framing: LibraDex is a rebuildable identity index over external source data, not the source-of-truth database.
- 🟩 [x] Accepted Abraxas consistency framing: `eventual indexed` is allowed; source writes can be accepted before all LibraDex projections are current.
- 🟩 [x] Accepted unsupported concurrency cases for public wording: same-session writer/writer and reader/writer are unsupported as-is; isolated catalogs are allowed when backing resources are distinct.
- 🟩 [x] Accepted unsupported concurrency failure behavior for the first guardrail: opt-in diagnostic active-write-window ownership check, fail fast before shared session mutation, and point callers to serialization/materialization/isolation options.
- 🟩 [x] Accepted first guardrail enable switch: `CatalogOptions.DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed`; `Counters` remains storage/operation counter collection only.
- 🟩 [x] Accepted first guardrail exception shape: `InvalidOperationException` with precise unsupported-concurrency wording; custom exception deferred unless Abraxas needs typed handling.
- 🟩 [x] Accepted named future mode semantics: `SingleOwner` is the default, `QueuedWriter` serializes submitted writes at the session/catalog boundary, and `PublishedReaders` requires visible-index-state publication.
- 🟨 [~] Accepted lock/visible-index-state ownership implementation remains open: queue ownership and published-reader pinning are not implemented.
- 🟩 [x] Accepted first cursor lifetime rule: current lazy cursors are single-owner/live-session only; do not promise stable reader visibility for a live cursor while the same session is writing.
- 🟩 [x] Accepted first cursor diagnostic policy: document cursor/read concurrency as unsupported in the first packet; do not add per-`Next` read-path checks or broad read locks by default.
- 🟨 [~] Accepted future cursor diagnostic mode remains open: a strict diagnostics mode may reject cursor creation during an active write window and may reject cursor-local mutation when another write owner exists, but this is not the first packet default.
- 🟩 [x] Accepted file-backed versus RAM-backed first contract: file-backed commit can approximate owner-side publication; RAM-backed commit is not a reader-visibility boundary today.
- 🟨 [~] Accepted RAM-backed future publication model remains open: concurrent readers need a visible-index-state layer, copy-on-publish arena/versioning, or materialized reader boundary above raw arena mutation.
- 🟨 [~] Accepted file-open sharing rule for immediate Abraxas slice: cross-process synchronization deferred by Abraxas efficiency gate; durable LibraDex file-open policy remains open.
- 🟩 [x] Accepted validation harness shape: deterministic same-session write/write, same-session read/write, separate-catalog, same-file-open, and RAM-backed read/write probes with coordinated start gates.
- 🟩 [x] Accepted provisional index-state vocabulary for concurrency/rebuild: `Current`, `EventualIndexed`, `Stale`, `Incomplete`, `Rebuilding`, `Failed`, and `NeedsRebuild`.
- 🟩 [x] Accepted index-state ownership split: Abraxas owns source-relative freshness and catch-up state; LibraDex owns structural reliability diagnostics.
- 🟨 [~] Accepted index-state API/persistence remains open: decide the public status shape and whether LibraDex structural diagnostics persist in a sidecar/record/format extension.
- 🟩 [x] Accepted Abraxas adapter read boundary for the first contract: materialized `Get`/`GetIdentities` results are preferred; cursor methods remain live-session/single-owner unless a future published-reader mode exists.
- 🟩 [x] Accepted Abraxas materialized-read guard: detailed diagnostics reject adapter `Get`/`GetIdentities` during an active same-session write window so normal integration does not silently read pending writer state.
- 🟩 [x] Accepted Abraxas status surface direction: normal identity reads stay identity-only; freshness/rebuild/diagnostic state should be explicit metadata/status queried separately.

## Pending Implementation Changes

- 🟩 [x] Document public concurrency contract in the appropriate API docs/comments: `Catalog`, `LibraDexIndex`, batch managers, cursors, maintenance surfaces, and Abraxas adapter surfaces.
- 🟩 [x] Add low-overhead guardrails for unsupported concurrent use, using a diagnostics-only atomic session write-window claim plus active writer context centered on `BeginDurabilityBatch`, writer-context-bearing `CommitDurabilityBatch`, and writer-context-bearing `AbortDurabilityBatch`.
- ⬜ [ ] Add a status surface that separates Abraxas-owned freshness/catch-up state from LibraDex-owned structural reliability diagnostics.
- 🟩 [x] Add `concurrency-contract-sanity` as a deterministic validation command once production guardrails are requested; keep stress loops separate from contract validation.
- 🟨 [~] Add Abraxas integration tests for the accepted contract: Abraxas serializes writes, materializes/disconnects read results where required, and can surface `eventual indexed`. Proof shape is drafted; test implementation remains open.
- 🟩 [x] Add optional diagnostics only if contention, queueing, or lock ownership becomes visible to callers. First diagnostic target is active write-window ownership under `DiagnosticsLevel.Detailed`; broader wait/queue diagnostics remain future work.
- 🟩 [x] Add Abraxas materialized-read diagnostics for the accepted adapter boundary: detailed diagnostics reject materialized adapter reads during an active same-session write window.
- 🟨 [~] Consider a future `SingleOwner`, `QueuedWriter`, or `PublishedReaders` concurrency option, but do not put hidden synchronization on the current hot path by default. Mode semantics are drafted; API shape and implementation remain open.
- 🟨 [~] Prefer deterministic invalidation/rebuild over rollback if a LibraDex index update fails after source data has already changed. Design rule is accepted; status API/persistence implementation remains open.
- 🟩 [x] Define first production change packet: public contract XML/API wording, opt-in write-window ownership diagnostics, and deterministic same-session writer-overlap harness.

## Recommended Implementation Order

- 🟩 [x] Principle: improve concurrency by making identity-index state explicit before adding broad synchronization.
- 🟩 [x] Slice 1: public contract docs and XML comments for single-owner session use, isolated catalogs, unsupported same-session concurrency, lazy cursor lifetime, and rebuildable identity-index semantics.
- 🟩 [x] Slice 2: diagnostic guardrails for same-session concurrent writers, starting at the session durability-batch lifecycle, with failure messages that point to serialization, materialization, or rebuild instead of implying database isolation.
- 🟨 [~] Slice 3: index/catalog status surface for Abraxas-owned freshness/catch-up state and LibraDex-owned structural diagnostics. Vocabulary and ownership split are accepted; public API and persistence remain open.
- 🟨 [~] Slice 3a: define stale/incomplete/needs-rebuild remediation after source-write-success/index-write-failure cases; design rule is accepted, implementation belongs with the status surface.
- 🟨 [~] Slice 4: deterministic harness cases. First `concurrency-contract-sanity` command is implemented for same-session writer overlap, cross-task batch ownership, Abraxas materialized-read boundary, and wrong-context rejection; same-session read/write, separate catalogs, RAM-backed read/write, and same-file open probes remain follow-ups.
- 🟨 [~] Slice 5: Abraxas adapter proof that source writes can be accepted while LibraDex reports or catches up to `eventual indexed`. Adapter/read-boundary proof shape is drafted; integration tests and status-surface implementation remain open.
- 🟩 [x] Slice 5a: prove the immediate Abraxas adapter read boundary: materialized adapter results are disconnected after return, and detailed diagnostics reject adapter materialization during an active same-session write window.
- 🟩 [x] Slice 6a: bounded `SS8-8` queued insert writer is implemented for encoded, unsigned, generic, and Abraxas adapter paths; same-shelf, different-shelf, unset-route, split, duplicate-run, and terminal-root range fallout are covered by deterministic harness probes.
- 🟨 [~] Slice 6b: general writer queue remains open for public batches, non-`SS8-8` shapes, deletes, overwrites, maintenance, cancellation/fairness policy, and queue diagnostics.
- 🟨 [~] Slice 7: optional published-reader/read-materialization work for concurrent readers without exposing pending writer state. `PublishedReaders` semantics are drafted; visible-index-state implementation remains open.
- 🟨 [~] Slice 8: only after the above, revisit true per-index/per-shelf concurrent writer mutation. Research prerequisites are documented; implementation remains deferred.

## First Production Change Packet

- 🟩 [x] Packet goal: improve developer intuition immediately without changing LibraDex's default synchronization or pretending concurrent writers are supported.
- 🟩 [x] Change 1: add public XML/API wording to `Catalog`, `LibraDexIndex`, `IndexBatchManager`, `CatalogIdentityGroupBatchManager`, `LibraDexBatch`, cursor types, maintenance surfaces, and `AbraxasIdentityQueryAdapter`.
- 🟩 [x] Change 2: add opt-in diagnostic active-write-window ownership state to `LibraDexFileSession`, using an atomic claim field plus active operation token and owner thread id for messages.
- 🟩 [x] Change 2a: pass or retain diagnostics policy at the session layer so `LibraDexFileSession` can decide whether detailed write-window ownership checks are enabled.
- 🟩 [x] Change 2b: pass the claimed operation token into `LibraDexFileSessionDurabilityBatch` so commit/abort verification follows the batch handle instead of the current managed thread.
- 🟩 [x] Change 3: check write-window ownership at `BeginDurabilityBatch`, writer-context-bearing `CommitDurabilityBatch`, and writer-context-bearing `AbortDurabilityBatch`.
- 🟩 [x] Change 3a: verify commit/abort ownership before dirty-shelf flush, `kernel.Commit()`, `kernel.DiscardPending()`, router-cache cleanup, or batch-field reset.
- 🟩 [x] Change 3b: release diagnostic ownership at the same point the session leaves the active durability-batch lifecycle; do not add a new commit-failure recovery policy in this packet.
- 🟩 [x] Change 3c: adjust `LibraDexFileSessionDurabilityBatch.Abort()` so the handle is marked complete only after session abort succeeds, matching commit semantics and preserving dispose-based cleanup after an abort-owner failure.
- 🟩 [x] Change 4: make failure wording explicit: same-session concurrent writes are unsupported; serialize access, use isolated catalogs, materialize reads, or use a future queue/published-reader mode.
- 🟩 [x] Change 4a: enable the first guardrail only when `CatalogOptions.DiagnosticsLevel` is `Detailed`; keep `Off` and `Counters` free of semantic ownership checks.
- 🟩 [x] Change 4b: throw `InvalidOperationException` for first-packet ownership failures; defer custom exception type until Abraxas needs typed catch behavior.
- 🟩 [x] Change 4c: make begin-claim failure text include unsupported same-session writes, rejected-before-shared-mutation wording, caller options, and active operation diagnostics when available.
- 🟩 [x] Change 4d: make commit/abort-token failure text distinguish wrong batch token from already-active competing writer.
- 🟩 [x] Change 4e: public wording should say failed or incomplete index updates after source writes should be marked stale/incomplete/needs-rebuild rather than rolled back.
- 🟩 [x] Change 5: add a deterministic harness command that proves overlapping same-session writer attempts fail predictably when diagnostics are enabled.
- 🟩 [x] Change 5a: name the first deterministic command `concurrency-contract-sanity` and register it in the harness validation command table when implementation is requested.
- 🟩 [x] Change 5b: include a public-path overlapping same-session `BeginBatch` probe using explicit gates and `DiagnosticsLevel.Detailed`.
- 🟩 [x] Change 5c: include a token-authority probe showing the same batch handle can commit/abort from a different task without relying on thread id as authority.
- 🟩 [x] Change 5d: include an internal mismatched-token or abort-order probe only if needed to prove handle completion is not marked before failed session abort.
- 🟩 [x] Change 6 decision: same-session reader/writer and live-cursor runtime diagnostics stay out of the first packet default; first packet documents live-session cursor limits and keeps normal reads free of per-row synchronization.
- 🟩 [x] Change 6a: add the second-packet Abraxas materialized-read diagnostic guard so the normal integration read path fails fast during an active same-session write window under `DiagnosticsLevel.Detailed`.
- 🟨 [~] Change 6 follow-up: strict cursor diagnostics can be added later as an opt-in mode, with separate decisions for `OpenCursor`, `Next`, `DeleteCurrent`, `DeleteRemaining`, `DeleteAll`, and `SetCurrentKey`.
- 🟥 [!] Change 7: index-state/status API stays out of the first production packet. Ownership split is accepted, but public API and persistence need a separate design slice.
- 🟩 [x] Change 8: document that connected physical maintenance/optimizer publication is write behavior and must use the same write-window ownership boundary as ordinary writers.
- 🟥 [!] Out of packet: general public batch writer queue implementation, published-reader implementation, RAM-backed publication layer, cross-process shared-reader model, and true independent route-topology concurrent mutation.
- 🟥 [!] Out of packet: broad read locks, implicit waits, retries, or hidden synchronization on normal lookup paths.
- 🟨 [~] Out-of-packet decision still needed: if LibraDex later owns general batch queueing, choose between blocking batch-entry semantics and an explicit submitted-write API.
- 🟨 [~] Out-of-packet decision still needed: if LibraDex later owns published-reader semantics, choose between materialized results, immutable snapshots, copy-on-publish arena/versioning, or generationed reader pinning.
- 🟨 [~] Out-of-packet decision still needed: if cross-process same-file access becomes required, choose exclusive writer locks, read-only LibraDex opens, version/generation handshakes, advisory lock files, or explicit unsupported errors.

## First Packet Implementation Map

- 🟩 [x] `Catalog/CatalogOptions.cs`: keep `DiagnosticsLevel` as the public opt-in switch; no new concurrency mode option in the first packet.
- 🟩 [x] `Catalog/Catalog.cs`: pass diagnostics policy through `Create`, `Open`, and `CreateMemory` into `LibraDexFileSession` instead of only translating it to `DataKernelTelemetryOptions`.
- 🟩 [x] `FileSession/LibraDexFileSession.cs`: add session-level detailed-diagnostics ownership state: atomic claimed flag, active operation token, next operation token, and owner thread id for messages.
- 🟩 [x] `FileSession/LibraDexFileSession.cs`: claim the diagnostic write window at the start of `BeginDurabilityBatch` before `durabilityBatchActive` and mutable cache state are changed.
- 🟩 [x] `FileSession/LibraDexFileSession.cs`: return `LibraDexFileSessionDurabilityBatch` with the claimed operation token.
- 🟩 [x] `FileSession/LibraDexFileSession.cs`: make `CommitDurabilityBatch` and `AbortDurabilityBatch` writer-context-bearing internal methods and verify the context before flush, `kernel.Commit()`, `kernel.DiscardPending()`, cache cleanup, or batch-field reset.
- 🟩 [x] `FileSession/LibraDexFileSession.cs`: release the diagnostic claim only when the normal commit/abort cleanup exits the active durability-batch lifecycle.
- 🟩 [x] `FileSession/LibraDexFileSessionDurabilityBatch.cs`: store the writer context and pass it to commit/abort; move `Abort()` completion assignment after successful session abort.
- 🟩 [x] `FileSession/LibraDexWriteContext.cs`: introduce the first writer-local mutation owner and move active `SS8-8` dirty shelf images behind it.
- 🟩 [x] `PublicApi/IndexBatchManager.cs`, `Catalog/CatalogIdentityGroupBatchManager.cs`, `PublicApi/LibraDexBatch.cs`, and shape-specific batch wrappers: no public API token exposure; existing batch handles continue routing through `LibraDexFileSessionDurabilityBatch`.
- 🟩 [x] Public XML/API wording targets: `Catalog`, `LibraDexIndex<TKey,TIdentity>`, `IndexBatchManager<TKey,TIdentity>`, `CatalogIdentityGroupBatchManager`, `LibraDexBatch<TKey,TIdentity>`, cursor types, maintenance surfaces, and `AbraxasIdentityQueryAdapter<TIdentity>`.
- 🟩 [x] Cursor wording target: current cursors are live-session/single-owner and are not snapshots or published-reader views.
- 🟩 [x] Maintenance wording target: public maintenance descriptors are currently scaffolded, but connected physical maintenance/optimizer publication is write behavior and must use the same write-window ownership boundary.
- 🟩 [x] `LibraDex.Harness/Program.cs`: register `concurrency-contract-sanity` in the validation command table.
- 🟩 [x] `LibraDex.Harness/Commands/*`: implement the command near public-surface/contract sanity code or in a new focused partial; use deterministic active-batch ownership rather than timing sleeps.
- 🟩 [x] Harness assertions: public overlapping begin fails before second-writer mutation, same batch handle can complete from another task, wrong-context/abort-order edge stays recoverable through the internal writer-context method, and exception checks use stable substrings.
- 🟥 [!] Do not touch `DataKernel` for ownership semantics in the first packet. Storage telemetry and raw byte movement should not own LibraDex session concurrency policy.
- 🟥 [!] Do not add queueing, lock waits, broad read locks, published-reader pinning, cross-process file protocol, or status persistence in the first packet.

## Validation Plan

- 🟩 [x] Build LibraDex Release/x64 with the repo-standard MSBuild command: `MSBuild.exe LibraDex.Harness\LibraDex.Harness.csproj /t:Rebuild /p:Configuration=Release /p:Platform=x64 /m:1 /v:minimal /fl /flp:"logfile=artifacts\build-harness-release-x64-concurrency-contract-20260620.log;verbosity=normal"`.
- 🟩 [x] Build LibraDex Release/x64 after terminal-identity writer-context support: `MSBuild.exe LibraDex.Harness\LibraDex.Harness.csproj /t:Rebuild /p:Configuration=Release /p:Platform=x64 /m:1 /v:minimal /fl /flp:"logfile=artifacts\build-harness-release-x64-terminal-identity-context-20260621.log;verbosity=normal"`.
- 🟩 [x] Build LibraDex Release/x64 after sorted terminal-identity writer-context support: `MSBuild.exe LibraDex.Harness\LibraDex.Harness.csproj /t:Rebuild /p:Configuration=Release /p:Platform=x64 /m:1 /v:minimal /fl /flp:"logfile=artifacts\build-harness-release-x64-terminal-identity-sorted-context-20260621.log;verbosity=normal"`.
- 🟩 [x] Build LibraDex Release/x64 after storage-publication gate narrowing: `MSBuild.exe LibraDex.Harness\LibraDex.Harness.csproj /t:Rebuild /p:Configuration=Release /p:Platform=x64 /m:1 /v:minimal /fl /flp:"logfile=artifacts\build-harness-release-x64-storage-publication-gate-20260621.log;verbosity=normal"`.
- 🟩 [x] Build LibraDex Release/x64 after storage-publication behavioral probe: `MSBuild.exe LibraDex.Harness\LibraDex.Harness.csproj /t:Rebuild /p:Configuration=Release /p:Platform=x64 /m:1 /v:minimal /fl /flp:"logfile=artifacts\build-harness-release-x64-storage-publication-probe-20260621.log;verbosity=normal"`.
- 🟩 [x] Build LibraDex Release/x64 after memory snapshot publication: `MSBuild.exe LibraDex.Harness\LibraDex.Harness.csproj /t:Rebuild /p:Configuration=Release /p:Platform=x64 /m:1 /v:minimal /fl /flp:"logfile=artifacts\build-harness-release-x64-memory-snapshot-publication-20260621.log;verbosity=normal"`.
- 🟩 [x] Build LibraDex Release/x64 after adding the Abraxas-style concurrency workload matrix: `MSBuild.exe LibraDex.Harness\LibraDex.Harness.csproj /t:Rebuild /p:Configuration=Release /p:Platform=x64 /m:1 /v:minimal /fl /flp:"logfile=artifacts\build-harness-release-x64-concurrency-workload-matrix-20260621.log;verbosity=normal"`.
- 🟩 [x] Build LibraDex Release/x64 after narrowing queued cold-route fallback: `MSBuild.exe LibraDex.Harness\LibraDex.Harness.csproj /t:Rebuild /p:Configuration=Release /p:Platform=x64 /m:1 /v:minimal /fl /flp:"logfile=artifacts\build-harness-release-x64-cold-route-narrow-fallback-20260621.log;verbosity=normal"`.
- 🟩 [x] Build LibraDex Release/x64 after narrowing queued root-prefix split fallback: `MSBuild.exe LibraDex.Harness\LibraDex.Harness.csproj /t:Rebuild /p:Configuration=Release /p:Platform=x64 /m:1 /v:minimal /fl /flp:"logfile=artifacts\build-harness-release-x64-root-prefix-split-narrow-fallback-20260621.log;verbosity=normal"`.
- 🟩 [x] Build LibraDex Release/x64 after narrowing queued root shelf-transform fallback: `MSBuild.exe LibraDex.Harness\LibraDex.Harness.csproj /t:Rebuild /p:Configuration=Release /p:Platform=x64 /m:1 /v:minimal /fl /flp:"logfile=artifacts\build-harness-release-x64-root-shelf-transform-narrow-fallback-20260621.log;verbosity=normal"`.
- 🟩 [x] Build LibraDex Release/x64 after narrowing queued walked parent-route split fallback: `MSBuild.exe LibraDex.Harness\LibraDex.Harness.csproj /t:Rebuild /p:Configuration=Release /p:Platform=x64 /m:1 /v:minimal /fl /flp:"logfile=artifacts\build-harness-release-x64-parent-route-split-narrow-fallback-20260621.log;verbosity=normal"`.
- 🟩 [x] Build LibraDex Release/x64 after narrowing queued duplicate-key overflow fallback: `MSBuild.exe LibraDex.Harness\LibraDex.Harness.csproj /t:Rebuild /p:Configuration=Release /p:Platform=x64 /m:1 /v:minimal /fl /flp:"logfile=artifacts\build-harness-release-x64-duplicate-key-overflow-narrow-fallback-20260621.log;verbosity=normal"`.
- 🟩 [x] Build LibraDex Release/x64 after narrowing queued terminal identity overflow fallback: `MSBuild.exe LibraDex.Harness\LibraDex.Harness.csproj /t:Rebuild /p:Configuration=Release /p:Platform=x64 /m:1 /v:minimal /fl /flp:"logfile=artifacts\build-harness-release-x64-terminal-identity-overflow-narrow-fallback-20260621.log;verbosity=normal"`.
- 🟩 [x] Build LibraDex Release/x64 after narrowing queued duplicate-run chain fallback: `MSBuild.exe LibraDex.Harness\LibraDex.Harness.csproj /t:Rebuild /p:Configuration=Release /p:Platform=x64 /m:1 /v:minimal /fl /flp:"logfile=artifacts\build-harness-release-x64-duplicate-run-chain-narrow-fallback-20260621.log;verbosity=normal"`.
- 🟩 [x] Build LibraDex Release/x64 after adding queued exact-delete/rekey support: `MSBuild.exe LibraDex.Harness\LibraDex.Harness.csproj /t:Rebuild /p:Configuration=Release /p:Platform=x64 /m:1 /v:minimal /fl /flp:"logfile=artifacts\build-harness-release-x64-queued-delete-rekey-20260621.log;verbosity=normal"`.
- 🟩 [x] Run existing public-surface sanity harness check: `dotnet LibraDex.Harness\bin\x64\Release\net8.0\LibraDex.Harness.dll public-surface-api-sanity`.
- 🟩 [x] Add and run `concurrency-contract-sanity`; required probes are public overlapping begin, cross-task same-batch abort, Abraxas materialized-read boundary, and mismatched-context/abort-order edge coverage through the internal writer-context method. Assert meaningful exception substrings, not the full prose.
- 🟩 [x] Add and run `concurrency-workload-matrix`; current rows prove writer-context paths for warmed different indexes, warmed different shelves, same-shelf queued retry/wait, and existing terminal identity shelves, while cold route creation, full ordinary shelf split, and duplicate-run overflow remain serialized fallback.
- 🟩 [x] Run SS8-8 file/memory API sanity after terminal-identity writer-context support: `dotnet LibraDex.Harness\bin\x64\Release\net8.0\LibraDex.Harness.dll ss8-8-index-api-sanity --path artifacts\ss8-8-index-api-sanity-terminal-identity-context.lbdx`.
- 🟩 [x] Run SS8-8 file/memory API sanity after sorted terminal-identity writer-context support: `dotnet LibraDex.Harness\bin\x64\Release\net8.0\LibraDex.Harness.dll ss8-8-index-api-sanity --path artifacts\ss8-8-index-api-sanity-terminal-identity-sorted-context.lbdx`.
- 🟩 [x] Run SS8-8 file/memory API sanity after storage-publication gate narrowing: `dotnet LibraDex.Harness\bin\x64\Release\net8.0\LibraDex.Harness.dll ss8-8-index-api-sanity --path artifacts\ss8-8-index-api-sanity-storage-publication-gate.lbdx`.
- 🟩 [x] Run SS8-8 file/memory API sanity after storage-publication behavioral probe: `dotnet LibraDex.Harness\bin\x64\Release\net8.0\LibraDex.Harness.dll ss8-8-index-api-sanity --path artifacts\ss8-8-index-api-sanity-storage-publication-probe.lbdx`.
- 🟩 [x] Run SS8-8 file/memory API sanity after memory snapshot publication: `dotnet LibraDex.Harness\bin\x64\Release\net8.0\LibraDex.Harness.dll ss8-8-index-api-sanity --path artifacts\ss8-8-index-api-sanity-memory-snapshot-publication.lbdx`.
- 🟩 [x] Run SS8-8 file/memory API sanity after parent-route split fallback narrowing: `dotnet LibraDex.Harness\bin\x64\Release\net8.0\LibraDex.Harness.dll ss8-8-index-api-sanity --path artifacts\ss8-8-index-api-sanity-parent-route-split-narrow-fallback.lbdx`.
- 🟩 [x] Run SS8-8 file/memory API sanity after duplicate-key overflow fallback narrowing: `dotnet LibraDex.Harness\bin\x64\Release\net8.0\LibraDex.Harness.dll ss8-8-index-api-sanity --path artifacts\ss8-8-index-api-sanity-duplicate-key-overflow-narrow-fallback.lbdx`.
- 🟩 [x] Run SS8-8 file/memory API sanity after terminal identity overflow fallback narrowing: `dotnet LibraDex.Harness\bin\x64\Release\net8.0\LibraDex.Harness.dll ss8-8-index-api-sanity --path artifacts\ss8-8-index-api-sanity-terminal-identity-overflow-narrow-fallback.lbdx`.
- 🟩 [x] Run SS8-8 file/memory API sanity after duplicate-run chain fallback narrowing: `dotnet LibraDex.Harness\bin\x64\Release\net8.0\LibraDex.Harness.dll ss8-8-index-api-sanity --path artifacts\ss8-8-index-api-sanity-duplicate-run-chain-narrow-fallback.lbdx`.
- 🟩 [x] Run SS8-8 file/memory API sanity after queued exact-delete/rekey support: `dotnet LibraDex.Harness\bin\x64\Release\net8.0\LibraDex.Harness.dll ss8-8-index-api-sanity --path artifacts\ss8-8-index-api-sanity-queued-delete-rekey.lbdx`.
- 🟩 [x] Run `git diff --check` before checkpointing this lane.

## Conversation Log

### 2026-06-20

- 🟩 [x] Started this checklist because Abraxas integration exposed that LibraDex concurrency behavior is unanswered.
- 🟩 [x] Chose Contract A for the first Abraxas slice: Abraxas serializes writes; LibraDex is single-owner per active catalog/session.
- 🟩 [x] Captured Abraxas efficiency-gate note: when closed, no other process access has been reported, so cross-process synchronization is not required at the moment.
- 🟩 [x] Captured lxl finding that current dried ink does not safely support concurrent writes as-is, despite concurrency-friendly physical storage ideas.
- 🟩 [x] Captured rollback/commit distinction: committed rollback is out of scope, readers should see committed/published state only, and file-backed versus RAM-backed commit cannot be treated identically.
- 🟩 [x] Added a concrete use-case matrix so concurrency decisions are driven by lxl evidence from required and non-required cases.
- 🟩 [x] Completed first-pass lxl for all eight matrix cases and recorded case-by-case current behavior, likely allowances, unsupported cases, and deferred decisions.
- 🟩 [x] Reframed the concurrency goal around LibraDex as a rebuildable identity index over external source data, with Abraxas able to expose `eventual indexed`.
- 🟩 [x] Added a guardrail-placement lxl pass: first detection should sit at the session durability-batch lifecycle and lazy cursor surfaces should be documented as live-session cursors, not snapshots.
- 🟩 [x] Added RAM-backed publication lxl: direct arena writes mean RAM-backed commit is not a reader-visibility boundary today.
- 🟩 [x] Added provisional index-state vocabulary and separated visible state from remediation action.
- 🟩 [x] Added deterministic harness shape: coordinated probes should validate contract boundaries and avoid timing-based stress loops as proof.
- 🟩 [x] Added harness placement lxl: first future command should be `concurrency-contract-sanity` in the harness validation command table, with explicit gates rather than sleeps.
- 🟩 [x] Added public contract wording draft for single-owner session behavior, isolated catalogs, batch/cursor scope, commit scope, maintenance scope, and Abraxas materialized reads.
- 🟩 [x] Added diagnostic ownership check draft: opt-in active write-window ownership detection around the durability-batch lifecycle, with fail-fast unsupported-concurrency messaging.
- 🟩 [x] Chose first diagnostic switch and exception shape: `DiagnosticsLevel.Detailed` enables write-window ownership checks, and `InvalidOperationException` is the first failure type.
- 🟩 [x] Added Abraxas adapter concurrency proof shape: normal integration should use materialized identity reads, keep cursors single-owner/live-session, and expose `EventualIndexed` as index state rather than pending writer visibility.
- 🟩 [x] Added Abraxas status surface lxl: status should be explicit metadata/status, not wrapped around every ordinary materialized identity read.
- 🟩 [x] Added future concurrency mode semantics for `SingleOwner`, `QueuedWriter`, and `PublishedReaders`.
- 🟩 [x] Added true concurrent writer research lane and prerequisites, keeping per-index/per-shelf mutation separate from the first implementation sequence.
- 🟩 [x] Added first production change packet with explicit in-scope and out-of-scope items.
- 🟩 [x] Added top-level restart summary with the current answer, first contract, first production packet, open questions, and deferred work.
- 🟩 [x] Added cursor diagnostic policy lxl: live cursors stay documentation-only for the first packet, per-`Next` checks are deferred, and cursor-local mutation is classified as write behavior.
- 🟩 [x] Added index-state ownership lxl: Abraxas owns source-relative freshness/catch-up state, LibraDex owns structural reliability diagnostics, and persisted status metadata stays out of the first packet.
- 🟩 [x] Added write-window ownership state lxl: the first deterministic guardrail needs an atomic diagnostics claim in `LibraDexFileSession`, not only the current plain `durabilityBatchActive` flag.
- 🟩 [x] Added commit/abort release lxl: diagnostic ownership should release when the session exits the active durability-batch lifecycle, and `Abort()` handle completion should move after successful session abort.
- 🟩 [x] Refined diagnostic owner model from thread-authority to batch-token authority; managed thread id remains diagnostic context only.
- 🟩 [x] Added harness probe-shape lxl: `concurrency-contract-sanity` should use explicit gates, cover public overlapping begin, prove cross-task same-token ownership, and reserve internal hooks for mismatched-token/abort-order edges.
- 🟩 [x] Implemented the first production packet: public contract wording, opt-in diagnostic write-window ownership, `concurrency-contract-sanity`, and verification by Release/x64 harness build plus public-surface sanity.
- 🟩 [x] Implemented the Abraxas materialized-read packet: adapter `Get` is guarded under detailed diagnostics, returned identity lists are proven disconnected, and active same-session writes are rejected before adapter condition materialization.
- 🟩 [x] Implemented the first writer-context accommodation slice: active `SS8-8` dirty shelf publication moved behind `LibraDexWriteContext`, and durability commit/abort now verify writer-context ownership.
- 🟩 [x] Added failure-message contract lxl: begin-claim failures and wrong-token commit/abort failures need distinct unsupported-concurrency wording and harnesses should assert stable substrings.
- 🟩 [x] Added `QueuedWriter` boundary lxl: bounded insert queueing uses an explicit submitted-operation facade; any future public batch queueing still needs blocking/submission semantics, and diagnostics must remain fail-fast.
- 🟩 [x] Added `PublishedReaders` boundary lxl: current `DataKernel.Commit()` is not a universal visibility boundary, especially for RAM-backed direct arena mutation and live cursors.
- 🟩 [x] Added cross-process boundary lxl: current `FileShare.Read` blocks second read/write LibraDex opens but is not a formal shared-reader or cross-process write protocol.
- 🟩 [x] Added maintenance/optimizer boundary lxl: public maintenance descriptors are low immediate risk, but connected physical maintenance and optimizer route publication are write behavior under the same write-window contract.
- 🟩 [x] Added source-write/index-write failure lxl: accepted source writes should lead to stale/incomplete/needs-rebuild index state, not LibraDex rollback semantics.
- 🟩 [x] Added first-packet implementation map with exact production touchpoints for diagnostics policy plumbing, session ownership state, batch-token routing, public XML wording, and harness registration.
- 🟨 [~] Next implementation step should lxl and then route no-split `SS8-8` inserts through independent writer contexts before loosening the public single-active writer gate.

### 2026-06-21

- 🟩 [x] Added twenty-fourth lxl slice: terminal identity roots are not automatically topology-changing. Existing tail-shelf append with spare capacity and a strictly greater identity is a bounded local mutation; empty roots, full tails, new linked shelves, and sorted middle inserts still require the serialized route path.
- 🟩 [x] Implemented terminal identity shelf staging in `LibraDexWriteContext`, with per-terminal-shelf writer ownership, publish-time validation, cache invalidation, and abort/publish release.
- 🟩 [x] Routed eligible `SS8-8` terminal identity tail appends through writer-context staging and kept unsupported terminal-root mutations on serialized fallback.
- 🟩 [x] Updated queued writer retry behavior so same terminal identity shelf contention waits and retries like same ordinary shelf contention.
- 🟩 [x] Added the natural harness proof: fill a same-key route until it becomes a terminal identity root, then append a higher identity through `QueuedWriter` and require `WriterContext` attribution plus readable identities.
- 🟩 [x] Fixed a publish/staging overlap found during validation: writer-context publication now enters the shared writer-operation write gate so `DataKernel` write/commit does not overlap another context's route walk or backing-byte read.
- 🟩 [x] Verified this slice with Release/x64 harness build, `concurrency-contract-sanity`, `public-surface-api-sanity`, `ss8-8-index-api-sanity --path artifacts\ss8-8-index-api-sanity-terminal-identity-context.lbdx`, and `git diff --check`.
- 🟩 [x] Added twenty-fifth lxl slice: the short publish/staging exclusion should stay for this packet. Memory-backed `DataKernel` uses mutable arena `List`/`Dictionary` state without internal synchronization, and route walking has many direct `kernel.Read` call sites across router/classification/cache/shelf paths, so partial removal would reopen read/write races.
- 🟩 [x] Added twenty-sixth lxl slice: terminal identity routes can also use writer-context staging for sorted in-shelf inserts, not only monotonic tail appends, when the target terminal shelf has spare capacity and no duplicate identity is present.
- 🟩 [x] Implemented bounded sorted terminal identity shelf insertion with binary search and in-shelf identity shifting. Unsupported terminal cases remain serialized fallback when they need root publication, a new linked shelf, or cross-shelf rebalancing.
- 🟩 [x] Extended the terminal-root harness proof to insert a lower new identity into the existing terminal shelf through `QueuedWriter`, require `WriterContext` attribution, and verify sorted readback.
- 🟩 [x] Added twenty-seventh lxl slice: remaining `SS8-8` queued-writer serialized fallback cases are still real topology/shared-route cases. Missing routes need router target publication, empty shared shelves need private-shelf allocation plus parent-route relink, full ordinary shelves need split/router publication, linked/full duplicate runs need chain publication or terminal conversion, and terminal identity full/cross-shelf cases need a new shelf or rebalance.
- 🟥 [!] Do not force the remaining fallback cases into writer-context staging without a broader topology-publication model. The current shelf-local/terminal-shelf-local writer context is intentionally bounded to mutations that publish exactly owned shelf bytes.
- 🟩 [x] Wording correction: avoid describing the current integration path as if every caller enters an invisible single-writer queue. The more accurate contract is concurrent write admission with independent writer-context staging where possible, retryable same-shelf contention, serialized topology fallback, and short serialized publication.
- 🟨 [~] Funnel answer: not only same-shelf contention is funneled. Same-shelf contention is necessary; topology fallback is necessary; current publication serialization is also necessary until `DataKernel` committed-byte reads/writes have a broader synchronization or immutable snapshot discipline.
- 🟩 [x] Added twenty-eighth implementation slice: narrowed writer-context publication from the broad `SS8-8` writer-operation gate to a `DataKernel` storage-publication gate. Writer-context staging no longer waits on the session-level writer-operation write lock during ordinary publish; raw reads wait only while storage-owned pending/direct-write state is being published.
- 🟩 [x] `DataKernel` now coordinates raw reads, commit, copied fixed-offset writes, telemetry reset, disposal, and volatile memory arena metadata mutation with a recursive storage reader/writer gate.
- 🟩 [x] Added `DataKernel.StageWriteAt(...)` so writer-context publication can copy fixed-offset shelf bytes into storage-owned memory without exposing a caller-filled pending span to concurrent readers.
- 🟩 [x] Added twenty-ninth behavioral proof: `concurrency-contract-sanity` now holds the storage-publication gate, proves `SS8-8` writer-context topology admission can still enter/exit, then proves full writer-context insert work waits on raw storage reads until storage publication is released.
- 🟩 [x] Added thirtieth implementation slice: memory-backed storage publication now captures an immutable committed-byte snapshot at publication entry. Reads use that snapshot while publication is active, and memory publication writes use copy-on-write pages so the snapshot cannot be torn.
- 🟩 [x] Updated the storage-publication behavioral proof: writer-context staging now completes while memory-backed storage publication is held, and only writer-context publish waits for the coherent storage-publication scope.
- 🟩 [x] Fixed same-shelf retry ordering exposed by the snapshot work: writer-context shelf ownership now releases only after the storage-publication snapshot is cleared, preventing a retrying same-shelf writer from staging against the old snapshot.
- 🟨 [~] Remaining funnel: file-backed storage publication is still serialized, and memory-backed publication still serializes the final copy-on-write/swap scope. Removing that final storage publication scope requires a broader generationed file/pending-write model.
- 🟩 [x] Added the Abraxas-style `concurrency-workload-matrix` harness command. Current classification: warmed shelf-local rows use writer-context staging; cold-route and full-shelf topology rows use narrowed topology publishers; terminal-root delete and other still-unsupported repair paths remain broad serialized fallback.
- 🟩 [x] Narrowed queued cold-route fallback: missing root-prefix initialization now uses a dedicated unset-route publisher under the target index's topology gate plus the storage publication boundary, without entering the broad `SS8-8` writer-operation write gate.
- 🟩 [x] Added deterministic cold-route lxl proof: while one index's writer-context staging gate is held open, a queued first write into a different index can initialize its missing root route and complete. The path remains classified as serialized fallback because it still publishes route topology.
- 🟩 [x] Narrowed queued root-prefix split fallback: the simple full-shelf split where multiple root prefixes still point at the same shelf now publishes through a dedicated copied-byte root-prefix split publisher under the target index's topology gate plus the storage publication boundary, without entering the broad `SS8-8` writer-operation write gate.
- 🟩 [x] Added deterministic root-prefix split lxl proof: while one index's writer-context staging gate is held open, a queued full-shelf root-prefix split in a different index can complete and read back. Same-prefix transforms, walked parent-route splits, and duplicate-run overflow remain on the broader serialized topology path.
- 🟩 [x] Narrowed queued root shelf-transform fallback: the root-level same-prefix full-shelf transform now prebuilds replacement shelf bytes/router bytes and publishes them under the target index's topology gate plus the storage publication boundary, without entering the broad `SS8-8` writer-operation write gate.
- 🟩 [x] Added deterministic root shelf-transform lxl proof: while one index's writer-context staging gate is held open, a queued same-root full-shelf transform in a different index can complete and read back. Walked/deeper transforms and parent-route split choices remain on the broader serialized topology path.
- 🟩 [x] Removed the stale session-wide writer-operation read gate from `SS8-8` writer-context staging. Same-index topology is still guarded by the per-root topology gate, and storage publication is still serialized at the `DataKernel` publication boundary.
- 🟩 [x] Narrowed queued walked parent-route split fallback: when a child router has multiple prefix slots pointing at one full shelf, the queued writer can rewalk under the target index topology gate, split the shelf bytes, rewrite only that parent router, and publish under the storage publication boundary without entering the broad writer-operation fallback.
- 🟩 [x] Added deterministic parent-route split lxl proof: while one index's writer-context staging gate is held open, a queued walked parent-route split in a different index can complete and read back. The proof uses a child-router fixture with child prefixes `0x10` and `0x20` pointing at the same full shelf so the actual structural kind is `WalkedParentRouteSplit`, not a walked shelf-transform.
- 🟩 [x] Validation after parent-route narrowing passed: Release/x64 harness build, `concurrency-contract-sanity`, `concurrency-workload-matrix`, `public-surface-api-sanity`, `ss8-8-index-api-sanity --path artifacts\ss8-8-index-api-sanity-parent-route-split-narrow-fallback.lbdx`, and `git diff --check`.
- 🟩 [x] Added duplicate-key overflow lxl: the matrix row starts as a full ordinary same-key shelf. Existing terminal-root inserts with spare capacity already use writer-context staging, while the overflow case needs route deepening and terminal-root conversion before future terminal-local writes can be isolated.
- 🟩 [x] Narrowed queued duplicate-key overflow fallback for the full ordinary same-key shelf case: the helper verifies that every item in the full routed shelf has the incoming key, then runs the existing duplicate route deepening/conversion under the target index topology gate plus publication lock, without entering the broad writer-operation fallback.
- 🟩 [x] Added deterministic duplicate-key overflow lxl proof: while one index's writer-context staging gate is held open, a queued duplicate-key overflow in a different index can complete and read back. The matrix now reports `NarrowTopologyPublisher` for this topology publisher so it is not conflated with broad serialized fallback.
- 🟩 [x] Validation after duplicate-key overflow narrowing passed: Release/x64 harness build, `concurrency-contract-sanity`, `concurrency-workload-matrix`, `public-surface-api-sanity`, `ss8-8-index-api-sanity --path artifacts\ss8-8-index-api-sanity-duplicate-key-overflow-narrow-fallback.lbdx`, and `git diff --check`.
- 🟩 [x] Added terminal identity overflow lxl: exact-key terminal identity roots with spare shelf capacity already use writer-context staging; full terminal shelves still publish topology because a new terminal identity shelf must be linked or the identity chain must be rewritten.
- 🟩 [x] Narrowed queued terminal identity overflow fallback: exact-key terminal roots now append/rewrite their terminal identity shelf chain under the target index topology gate plus publication lock, without entering the broad writer-operation fallback. Mismatched terminal roots still return to the broader repair path.
- 🟩 [x] Added deterministic terminal identity overflow lxl proof: while one index's writer-context staging gate is held open, a queued exact-key terminal identity overflow in a different index can complete and read back.
- 🟩 [x] Validation after terminal identity overflow narrowing passed: Release/x64 harness build, `concurrency-contract-sanity`, `concurrency-workload-matrix`, `public-surface-api-sanity`, `ss8-8-index-api-sanity --path artifacts\ss8-8-index-api-sanity-terminal-identity-overflow-narrow-fallback.lbdx`, and `git diff --check`.
- 🟩 [x] Added duplicate-run chain lxl: current normal same-key overflow converts to terminal identity roots, but older/repaired catalogs can still contain duplicate-run shelves. Spare single-shelf duplicate runs already use writer-context staging; full or linked duplicate-run chains need linked-shelf append or chain rewrite.
- 🟩 [x] Narrowed queued duplicate-run chain fallback: matching duplicate-run shelves now append/rewrite under the target index topology gate plus publication lock, without entering the broad writer-operation fallback.
- 🟩 [x] Fixed linked duplicate-run readback exposed by the proof: the optimized same-shelf `SS8-8` range path now follows duplicate-run next-shelf links, matching the router traversal path's behavior.
- 🟩 [x] Added deterministic duplicate-run chain proof: while one index's writer-context staging gate is held open, a queued full duplicate-run append in a different index can complete and read back.
- 🟩 [x] Validation after duplicate-run chain narrowing passed: Release/x64 harness build, `concurrency-contract-sanity`, `concurrency-workload-matrix`, `public-surface-api-sanity`, `ss8-8-index-api-sanity --path artifacts\ss8-8-index-api-sanity-duplicate-run-chain-narrow-fallback.lbdx`, and `git diff --check`.
- 🟩 [x] Added exact-delete lxl: current generic exact delete uses a range plan and batch-style shelf/root rewrite, so it was not safe as an overlapping low-friction mutation path. The first queued delete slice should be exact tuple only, not criteria/range delete.
- 🟩 [x] Implemented queued exact `SS8-8` tuple delete: ordinary shelf-local and single-shelf duplicate-run deletes stage through writer contexts, same-shelf contention retries inside the queued writer, and terminal-root/linked-chain deletes fall back to serialized exact-delete mutation.
- 🟩 [x] Added queued rekey for key-changing patches: replacement insert is submitted before old tuple delete, and the result exposes both legs. This preserves LibraDex identity-index semantics and avoids database transaction/rollback wording.
- 🟩 [x] Added Abraxas adapter exact-delete and rekey convenience methods backed by the cached queued writer for the named index.
- 🟩 [x] Added deterministic delete/rekey proofs: generic queued exact delete, terminal delete fallback, generic queued rekey, Abraxas adapter queued delete, and Abraxas adapter queued rekey all pass in `concurrency-contract-sanity`.
- 🟩 [x] Validation after queued exact-delete/rekey support passed: Release/x64 harness build, `concurrency-contract-sanity`, `concurrency-workload-matrix`, `public-surface-api-sanity`, `public-api-snapshot`, `ss8-8-index-api-sanity --path artifacts\ss8-8-index-api-sanity-queued-delete-rekey.lbdx`, and `git diff --check`.
- 🟩 [x] Added lxl and fix for writer-context stale shelf images: `SS8-8` writer contexts now claim/register a shelf before reading it, preventing a same-shelf writer from staging against pre-publication bytes after another writer commits.
- 🟩 [x] Invalidated the `SS8-8` clean shelf cache after writer-context publication so later batch-style reads cannot consume a pre-publication cached shelf image.
- 🟩 [x] Added no-ceremony mutation probes for direct exact `IIndex.Delete(...)` and value-route criteria delete; both now pass through queued exact tuple deletion under the no-batch/no-projection guard.
- 🟩 [x] Validation after direct/criteria exact-delete narrowing passed: Release/x64 harness build, `concurrency-contract-sanity`, `concurrency-workload-matrix`, `public-surface-api-sanity`, `public-api-snapshot`, `ss8-8-index-api-sanity --path artifacts\ss8-8-index-api-sanity-direct-queued-mutation.lbdx`, and `git diff --check`.
- 🟩 [x] Reintroduced no-ceremony direct `SS8-8` insert after the stale-shelf race fix and mirrored one-item batch stats/commit accounting for the immediate queued publication path.
- 🟩 [x] Added a direct concurrent insert probe proving ordinary generic `SS8-8` `Insert(...)` calls can overlap without caller-created queued writers when the no-batch/no-projection guard is satisfied.
- 🟩 [x] Validation after direct queued insert default passed: Release/x64 harness build, `concurrency-contract-sanity`, `concurrency-workload-matrix`, `public-surface-api-sanity`, `public-api-snapshot`, `ss8-8-index-api-sanity --path artifacts\ss8-8-index-api-sanity-direct-queued-insert.lbdx`, and `git diff --check`.
- 🟩 [x] Added direct concurrent rekey and criteria `SetKey` probes. LXL confirmed no extra core bridge was needed because both paths already call direct `Insert(...)` for replacement tuples and exact `Delete(...)` for old tuples.
- 🟩 [x] Validation after direct rekey/criteria `SetKey` proof passed: Release/x64 harness build, `concurrency-contract-sanity`, `concurrency-workload-matrix`, `public-surface-api-sanity`, `public-api-snapshot`, `ss8-8-index-api-sanity --path artifacts\ss8-8-index-api-sanity-direct-setkey-rekey.lbdx`, and `git diff --check`.
- 🟩 [x] Added scalar-null/all delete lxl and implementation: key-state routes are serialized per slot/route, `ScalarNull.NonNull` captures ordinary value-route tuples and deletes them through queued exact tuple deletion, and `All` deletes the scalar-null route first before deleting value-route tuples through the same queued bridge.
- 🟩 [x] Added cursor-local mutation lxl and implementation for generic `SS8-8`: `LibraDexRangeReader.DeleteCurrent()` now delegates to the owning index's exact tuple delete callback when supplied, matching `SetKey`'s externalized insert/delete path instead of rewriting the retained shelf image directly.
- 🟩 [x] Added deterministic cursor mutation proofs: concurrent `SS8-8` cursor `DeleteCurrent()` and cursor `SetKey(...)` pass through the no-batch/no-projection owning-index bridge and leave expected readback.
- 🟩 [x] Maintained projection boundary accepted for now: exact reversed projection indexes still stay off the direct queued default because inserts currently share the primary durability batch with the projection and deletes maintain a companion projection tuple. Loosening this safely needs deliberate multi-index publication or explicit eventual-projection semantics.
- 🟩 [x] Public batch boundary accepted for now: batch/group-batch APIs remain durability-cadence surfaces for one owner, not hidden concurrent writer queues. Direct no-ceremony queued mutation is intentionally disabled while batch state is active.
- 🟩 [x] Validation after scalar-null/all and cursor mutation support passed: Release/x64 harness build, `concurrency-contract-sanity`, `concurrency-workload-matrix`, `public-surface-api-sanity`, `public-api-snapshot`, `ss8-8-index-api-sanity --path artifacts\ss8-8-index-api-sanity-ss88-cursor-mutation.lbdx`, and `git diff --check`.
- 🟩 [x] Started the cross-shape transfer after `SS8-8`: cursor-local delete now always delegates through the owning index exact-delete callback, so `SS16-8`, `SS8-16`, and `SS16-16` get the same mutation convergence point for stats, projection cleanup, and future queued bridges.
- 🟩 [x] Added deterministic cross-shape convergence probes for generic `SS16-8`, `SS8-16`, and `SS16-16` cursor delete and cursor `SetKey`. These prove path convergence, not concurrent writer admission.
- 🟩 [x] Added deterministic queued-writer boundary probes for generic `SS16-8`, `SS8-16`, and `SS16-16`: the initial probe recorded the original `SS8-8`-only admission boundary before later shape-specific facade support closed it for the fixed-scalar shapes.
- 🟩 [x] Added the first true `SS16-8` writer-context slice: session-local `SS16-8` staged shelf bytes, per-shelf ownership claims, per-root topology staging gate, publication/abort helpers, and generic direct insert/exact-delete routing for warmed shelf-local value-route mutations.
- 🟩 [x] Added deterministic `SS16-8` writer-context probes: overlapping direct inserts on warmed independent shelves report writer-context path attribution, overlapping direct exact deletes leave expected survivors, and cold-route direct insert remains serialized fallback with route creation.
- 🟩 [x] Extended `SS16-8` warmed shelf-local mutation coverage: direct rekey, criteria delete, and criteria `SetKey` now have deterministic overlapping probes through the widened-key writer-context insert/exact-delete legs.
- 🟩 [x] Added `SS16-8` same-shelf overlap proof: ordinary direct inserts targeting the same warmed shelf remain caller-safe and report writer-context path attribution after any internal same-shelf retry.
- 🟩 [x] Narrowed the first `SS16-8` topology fallback: unset root-prefix route initialization now creates one widened-key shelf and rewrites the direct root-router prefix under the per-root topology gate plus storage publication lock, reporting serialized-fallback attribution without the old one-item batch path.
- 🟩 [x] Added explicit `SS16-8` queued-writer facade support and harness proof for insert, delete, and rekey. The facade is a low-friction adapter over the direct `SS16-8` writer-context path, not a separate single-writer queue.
- 🟩 [x] Removed the old `SS16-8` one-item batch fallback from direct insertion: routes that need full-shelf split/transform topology now run through a per-root serialized topology helper and report serialized-fallback attribution.
- 🟩 [x] Tightened `SS16-8` queued-writer delete attribution: facade delete/rekey now receives the path reported by the shape-specific exact-delete bridge instead of inferring writer-context from a Boolean result, with post-split delete proof.
- 🟩 [x] Added the first true `SS8-16` writer-context slice: session-local `SS8-16` staged shelf bytes, per-shelf ownership claims, per-root topology staging gate, publication/abort helpers, and generic direct insert/exact-delete routing for warmed shelf-local wide-identity mutations.
- 🟩 [x] Added deterministic `SS8-16` warmed-path proofs: overlapping direct inserts on independent shelves, same-shelf insert overlap with retry, overlapping exact deletes, direct rekey, criteria delete, and criteria `SetKey`.
- 🟩 [x] Added explicit `SS8-16` queued-writer facade support and harness proof for insert, delete, and rekey. The facade is a low-friction adapter over the direct `SS8-16` writer-context path, not a separate single-writer queue.
- 🟩 [x] Removed the old `SS8-16` one-item batch fallback from direct insertion: cold root-route creation and routes that need full-shelf split/transform topology now run through per-root serialized topology helpers and report serialized-fallback attribution.
- 🟩 [x] Added the first true `SS16-16` writer-context slice: session-local `SS16-16` staged shelf bytes, per-shelf ownership claims, per-root topology staging gate, publication/abort helpers, and generic direct insert/exact-delete routing for warmed shelf-local wide-key/wide-identity mutations.
- 🟩 [x] Added deterministic `SS16-16` warmed-path proofs: overlapping direct inserts on independent shelves, same-shelf insert overlap with retry, overlapping exact deletes, direct rekey, criteria delete, and criteria `SetKey`.
- 🟩 [x] Added explicit `SS16-16` queued-writer facade support and harness proof for insert, delete, and rekey. The facade is a low-friction adapter over the direct `SS16-16` writer-context path, not a separate single-writer queue.
- 🟩 [x] Removed the old `SS16-16` one-item batch fallback from direct insertion: cold root-route creation and routes that need full-shelf split/transform topology now run through per-root serialized topology helpers and report serialized-fallback attribution.
- 🟩 [x] Expanded `concurrency-workload-matrix` beyond the original `SS8-8` rows: `SS16-8`, `SS8-16`, and `SS16-16` now each report warmed independent-shelf writes, warmed same-shelf contention, cold root-route fallback, and full-shelf topology fallback. The widened rows confirm the common fixed-scalar pattern: warmed shelf-local work uses writer-context staging, while route creation and split/transform work stay serialized topology fallback.
- 🟩 [x] Expanded `concurrency-workload-matrix` mutation visibility: `SS8-8`, `SS16-8`, `SS8-16`, and `SS16-16` now each report warmed exact-delete and warmed rekey rows. Rekey is counted as two physical identity-index legs, replacement insert plus old tuple delete, and all warmed fixed-scalar legs report writer-context attribution.
- 🟩 [x] Added a maintained exact-reversed projection boundary row to `concurrency-workload-matrix`: fixed-width binary projection-owner inserts currently succeed through the shared durability-batch maintenance path and report no writer-context or serialized-topology attribution. LXL: the primary tuple and hidden reversed projection tuple are intentionally coupled through one publication boundary today; loosening this needs either a two-index writer-context publication rule or an explicit eventual-projection contract.
- 🟩 [x] Added true `FS32-8` and `FS32-16` writer-context slices: session-local fixed-32 staged shelf bytes, per-shelf ownership claims, per-root topology staging gates, publication/abort helpers, warmed direct/queued insert routing, warmed exact-delete routing, and queued-writer facade support for insert/delete/rekey.
- 🟩 [x] Expanded `concurrency-workload-matrix` for `FS32-8` and `FS32-16`: warmed independent-shelf inserts, warmed exact deletes, and warmed rekeys all report writer-context attribution; cold missing-root-prefix writes now report narrowed topology attribution because they publish route topology through the specialized publisher.
- 🟩 [x] Removed the old `FS32-8` and `FS32-16` one-item batch fallback from direct insertion: cold root-route creation and full-shelf split/transform topology now run through per-root narrowed topology helpers and report `NarrowTopologyPublisher` attribution. The matrix now includes explicit `fs32-8-full-shelf` and `fs32-16-full-shelf` rows.
- 🟩 [x] Narrowed cold-route topology critical sections for `SS8-8`, `SS16-8`, `SS8-16`, `SS16-16`, `FS32-8`, and `FS32-16`: new shelf bytes are now prepared before entering the per-root topology gate and storage publication boundary. The remaining locked work is route recheck, reservation/copy, root-router rewrite, and commit.
- 🟩 [x] Narrowed `SS8-8` topology publisher critical sections for root-prefix split, root shelf-transform, walked parent-route split, duplicate-key overflow, exact-key terminal identity overflow, and duplicate-run chain fallback: route reads, shelf reads, validation, no-op detection, and split-byte planning now run under the target root topology gate but outside the global storage publication lock. The remaining global publication work is reserve/stage/link/commit or legacy helper publication.
- 🟩 [x] Narrowed walked parent-route split publishers for all currently proven fixed-scalar shapes: `SS16-8`, `SS8-16`, `SS16-16`, `FS32-8`, and `FS32-16` now try a shape-specific parent-route split before the broad topology fallback. Route walk, source shelf read, parent-router validation, no-op detection, and split-byte planning run under the target root topology gate but outside the global storage publication lock; the global publication lock covers only route recheck, right-shelf allocation, shelf/router staging, cache invalidation, and commit.
- 🟩 [x] Narrowed same-prefix shelf-transform publishers for all currently proven fixed-scalar shapes: `SS16-8`, `SS8-16`, `SS16-16`, `FS32-8`, and `FS32-16` now try a shape-specific shelf transform before the broad topology fallback. Route walk, source shelf read, no-op detection, and replacement shelf planning run under the target root topology gate but outside the global storage publication lock; the global publication lock covers route recheck, replacement shelf allocation, appended router allocation, child-router rewrite, cache invalidation, arena registration, and commit.
- 🟩 [x] Split queued-writer attribution: `WriterContext` now means shelf-local staged mutation, `NarrowTopologyPublisher` means a specialized route/topology publisher with narrowed critical sections, and `SerializedFallback` now means the broad fallback path that still could not be handled by writer context or a narrowed publisher.
- 🟩 [x] Validation after narrow-topology attribution passed: Release/x64 harness build, `concurrency-workload-matrix`, `concurrency-contract-sanity`, `public-surface-api-sanity`, `public-api-snapshot`, and `ss8-8-index-api-sanity --path artifacts\ss8-8-index-api-sanity-narrow-topology-attribution.lbdx`.
- 🟩 [x] Added terminal identity exact-delete writer-context support: queued exact deletes that remove one identity from an existing terminal identity shelf now stage that shelf in the writer context and avoid broad serialized fallback when no root cleanup or chain relink is required.
- 🟩 [x] Added linked duplicate-run exact-delete writer-context support: the queued delete stages only the duplicate-run shelf containing the target identity, including the one-item tail case that becomes empty, because empty duplicate-run shelves remain valid and route cleanup is deliberately deferred to maintenance.
- 🟩 [x] Validation after terminal/duplicate-run delete narrowing passed: Release/x64 harness build, `concurrency-contract-sanity`, `concurrency-workload-matrix`, `public-surface-api-sanity`, `public-api-snapshot`, and `ss8-8-index-api-sanity --path artifacts\ss8-8-index-api-sanity-duplicate-run-empty-tail-delete.lbdx`.
- 🟩 [x] Loosened no-batch maintained exact-reversed projection insertion: projection-owning fixed-scalar indexes can now use the same writer-context/narrow-topology primary path, then publish the reversed companion tuple through the projection index's immediate path. Explicit batches still keep the shared durability publication boundary.
- 🟩 [x] Updated `concurrency-workload-matrix` projection row: `exact-reversed-projection` now reports primary `NarrowTopologyPublisher` attribution instead of no queued attribution.
- 🟩 [x] Validation after immediate projection writer-context loosening passed: Release/x64 harness build, `concurrency-contract-sanity`, `concurrency-workload-matrix`, `public-surface-api-sanity`, `public-api-snapshot`, and `ss8-8-index-api-sanity --path artifacts\ss8-8-index-api-sanity-immediate-projection-writer-context.lbdx`.
- 🟩 [x] Fixed queued facade delete convergence for non-`SS8-8` fixed-scalar shapes: queued exact deletes now run projection cleanup and delete stats through the owning generic index instead of returning only the physical delete result.
- 🟩 [x] Added maintained projection delete/rekey matrix coverage: `exact-reversed-delete-warm` reports one writer-context delete leg, and `exact-reversed-rekey-warm` reports writer-context replacement plus removal legs.
- 🟩 [x] Validation after projection delete/rekey writer-context coverage passed: Release/x64 harness build, `concurrency-contract-sanity`, `concurrency-workload-matrix`, `public-surface-api-sanity`, `public-api-snapshot`, and `ss8-8-index-api-sanity --path artifacts\ss8-8-index-api-sanity-projection-delete-rekey-writer-context.lbdx`.
- 🟩 [x] Added active public batch boundary guard: ordinary no-batch immediate mutation and explicit queued-writer mutation now reject while any session durability batch is active, unless the write has already routed through the owning index batch or identity-group batch manager. This closes the lxl issue where unrelated callers could otherwise observe only their own index batch flag and accidentally stage into another owner's session dirty-shelf overlay.
- 🟩 [x] Added public batch boundary probes to `concurrency-contract-sanity`: unrelated immediate mutation fails while an index batch is active, a queued writer created before the batch also fails during the active batch and resumes after abort, and participating identity-group batch inserts still publish through the group manager.
- 🟩 [x] Validation after active public batch boundary guard passed: Release/x64 harness build, `concurrency-contract-sanity`, and `concurrency-workload-matrix`.
- 🟩 [x] Added string key-state batch-boundary guard: `LibraDexStringScalar8Index` now rejects no-batch null/empty key-state insert/delete while a session durability batch is active, preserving the identity-group `InsertInCurrentScope` path for participating group batches. LXL: non-empty `VS8` string writes already enter `VarKeyScalar8Index.BeginBatch()` and reject through the session; the direct metadata-backed key-state route was the bypass.
- 🟩 [x] Added string key-state boundary probe to `concurrency-contract-sanity`: null string insert fails during an unrelated active index batch and succeeds after that batch aborts.
- 🟩 [x] Validation after string key-state guard passed: Release/x64 harness build, `concurrency-contract-sanity`, `concurrency-workload-matrix`, `public-surface-api-sanity`, and `git diff --check`.
- 🟩 [x] Moved string criteria delete and scan-based rekey active-batch checks before tuple materialization, so those mutation paths fail before live-session reads when another owner has an active durability batch.
- 🟩 [x] Validation after early string criteria/rekey guard passed: Release/x64 harness build, `concurrency-contract-sanity`, `public-surface-api-sanity`, and `git diff --check`.
- 🟩 [x] Added fixed-scalar key-state mutation guard: direct `Delete(TKey, identity)`, `Delete(ScalarNull/NullKey, identity)`, public rekey variants, and criteria-scoped mutation now reject while an unrelated session durability batch is active before reaching metadata-backed key-state route helpers.
- 🟩 [x] Added fixed-scalar key-state boundary probe to `concurrency-contract-sanity`: scalar-null delete fails during an unrelated active index batch and succeeds after that batch aborts.
- 🟩 [x] Validation after fixed-scalar key-state guard passed: Release/x64 harness build, `concurrency-contract-sanity`, `concurrency-workload-matrix`, `public-surface-api-sanity`, `public-api-snapshot`, `ss8-8-index-api-sanity --path artifacts\ss8-8-index-api-sanity-key-state-active-batch-boundary.lbdx`, and `git diff --check`.
- 🟩 [x] Added routed composite batch-boundary guard: composite inserts, deletes, rekeys, and criteria deletes now reject while an unrelated session durability batch is active before mutating the in-memory composite route tree or publishing a durable snapshot/root update.
- 🟩 [x] Added routed composite boundary probe to `concurrency-contract-sanity`: composite insert fails during an unrelated active index batch and succeeds after that batch aborts.
- 🟩 [x] Validation after routed composite guard passed: Release/x64 harness build, `concurrency-contract-sanity`, `concurrency-workload-matrix`, `public-surface-api-sanity`, `public-api-snapshot`, `ss8-8-index-api-sanity --path artifacts\ss8-8-index-api-sanity-composite-active-batch-boundary.lbdx`, and `git diff --check`.
- 🟩 [x] Added the first `VS8` writer-context slice: warmed ordinary shelf-local inserts can now stage in writer-local mutable shelves and publish through the serialized DataKernel publication seam. Same-shelf ownership raises `LibraDexWriteContextVarKeyScalar8ShelfOwnershipException`, allowing low-friction retry loops without exposing caller ceremony.
- 🟩 [x] Routed non-batch `VarKeyScalar8Index.InsertEncoded(...)` through the `VS8` writer-context path when the root-prefix route is already warm. This covers the common non-empty string exact/projection insert path because `LibraDexStringScalar8Index` already funnels those writes through `InsertEncoded`; explicit `VarKeyScalar8Batch` remains on the durability-batch path so batch commit cadence is unchanged.
- 🟩 [x] Added `RunInternalVarKeyScalar8WriterContextProbe` to `concurrency-contract-sanity`: two independent warmed `VS8` shelves stage before either publishes, read back after publication, and same-shelf overlap fails with the retryable ownership exception.
- 🟩 [x] Validation after `VS8` writer-context slice passed: Release/x64 harness build, `concurrency-contract-sanity`, `concurrency-workload-matrix`, `public-surface-api-sanity`, `public-api-snapshot`, and `ss8-8-index-api-sanity --path artifacts\ss8-8-index-api-sanity-vs8-writer-context.lbdx`.
- 🟩 [x] Added `VS8` exact-delete writer-context support for warmed ordinary shelves: `DeleteVarKeyScalar8ExactTupleForWriteContext` marks tombstones in writer-local mutable shelves, publication reuses the existing normalization/repack path, and `VarKeyScalar8Index.DeleteEncodedExactTuple(...)` routes no-batch encoded deletes through that path with same-shelf retry.
- 🟩 [x] Loosened exact-only string deletes: string indexes with no maintained projections now delete non-empty exact rows through `DeleteEncodedExactTuple(...)`; projection-owning string indexes still use the shared batch so exact and projection rows remain coupled.
- 🟩 [x] Loosened exact-only string criteria delete: after tuple materialization, ordinary `VS8` rows are deleted through one writer context and one publication; null/empty key-state rows are deleted through their route helper after the ordinary publication. Same-shelf ownership retries internally, while terminal/duplicate/topology shapes fall back to the old batch path.
- 🟩 [x] Validation after `VS8` exact delete and exact-only string criteria delete passed: Release/x64 harness build, `concurrency-contract-sanity`, `concurrency-workload-matrix`, `public-surface-api-sanity`, and `public-api-snapshot`.
- 🟩 [x] Added raw `VS8` range-delete writer-context support for warmed ordinary shelves: `DeleteVarKeyScalar8KeyRangeForWriteContext` recursively walks router targets, stages ordinary shelf tombstones in one writer context, rejects terminal/duplicate-run shapes for fallback, and `VarKeyScalar8Index.DeleteRange(...)` uses that path outside explicit durability batches.
- 🟩 [x] Extended `RunInternalVarKeyScalar8WriterContextProbe` with range-delete coverage: seeded rows are deleted through a writer-context range mutation, published once, and readback proves only the requested interval was removed.
- 🟩 [x] Validation after raw `VS8` range-delete writer-context support passed: Release/x64 harness build, `concurrency-contract-sanity`, `concurrency-workload-matrix`, `public-surface-api-sanity`, `public-api-snapshot`, and `ss8-8-index-api-sanity --path artifacts\ss8-8-index-api-sanity-vs8-range-delete-writer-context.lbdx`.
- 🟩 [x] Added direct exact-only string criteria-to-range delete for no-limit `Find` and non-key-state `Between`: the string facade now counts the encoded exact range, then calls `VarKeyScalar8Index.DeleteEncodedRange(...)` so warmed ordinary `VS8` shelves use the range-delete writer-context path without materializing every tuple for deletion. Null/empty key-state `Find` deletes route identities from a captured array so the live key-state reader is not mutated during enumeration.
- 🟩 [x] Validation after direct exact-only string criteria-to-range delete passed: Release/x64 harness build, `concurrency-contract-sanity`, `concurrency-workload-matrix`, `public-surface-api-sanity`, `public-api-snapshot`, `ss8-8-index-api-sanity --path artifacts\ss8-8-index-api-sanity-string-direct-range-delete.lbdx`, and `git diff --check`.
- 🟩 [x] Added direct exact-only inclusive ordered string criteria delete for no-limit non-key-state `AtOrBefore` and `AtOrAfter`: these now map to encoded full-lower/boundary and boundary/full-upper ranges, then use the same `VS8` range-delete writer-context path. Exclusive `Before`/`After` stay materialized until a byte successor/predecessor rule is proven against variable-length prefix ordering.
- 🟩 [x] Validation after direct exact-only inclusive ordered string criteria delete passed: Release/x64 harness build, `concurrency-contract-sanity`, `concurrency-workload-matrix`, `public-surface-api-sanity`, `public-api-snapshot`, `ss8-8-index-api-sanity --path artifacts\ss8-8-index-api-sanity-string-inclusive-range-delete.lbdx`, and `git diff --check`.
- 🟩 [x] Added direct exact-only string `All` and `KeyState` criteria delete: key-state routes delete captured null/empty identities directly, and ordinary non-empty `All` rows delete through one encoded full-range `VS8` writer-context range mutation when no projections or take limit are involved.
- 🟩 [x] Validation after direct exact-only string `All`/`KeyState` delete passed: Release/x64 harness build, `concurrency-contract-sanity`, `concurrency-workload-matrix`, `public-surface-api-sanity`, `public-api-snapshot`, `ss8-8-index-api-sanity --path artifacts\ss8-8-index-api-sanity-string-all-keystate-direct-delete.lbdx`, and `git diff --check`.
- 🟩 [x] Added direct exact-only exclusive ordered string criteria delete for no-limit non-key-state `Before` and `After`: byte-boundary helpers derive inclusive range bounds that preserve variable-length prefix ordering without materializing rows, then the ordinary `VS8` range-delete writer-context path publishes the deletion.
- 🟩 [x] Validation after direct exact-only exclusive ordered string criteria delete passed: Release/x64 harness build, `concurrency-contract-sanity`, `concurrency-workload-matrix`, `public-surface-api-sanity`, `public-api-snapshot`, `ss8-8-index-api-sanity --path artifacts\ss8-8-index-api-sanity-string-exclusive-range-delete.lbdx`, and `git diff --check`.
- 🟩 [x] Added direct exact-only string `InSet` criteria delete: membership operands are counted before mutation so duplicate operands preserve old matched-count diagnostics, then each unique null, empty, or ordinary encoded key route is deleted once through key-state or `VS8` range-delete writer-context paths.
- 🟩 [x] Validation after direct exact-only string `InSet` delete passed: Release/x64 harness build, `concurrency-contract-sanity`, `concurrency-workload-matrix`, `public-surface-api-sanity`, `public-api-snapshot`, `ss8-8-index-api-sanity --path artifacts\ss8-8-index-api-sanity-string-inset-direct-delete.lbdx`, and `git diff --check`.
- 🟩 [x] Loosened projection-coupled string deletes/rekeys outside explicit batches: direct exact deletes and criteria deletes still capture exact key/identity tuples when projections are maintained, but exact and projection tuple removals now use immediate encoded exact-delete paths so each routed `VS8` index can use writer-context staging where topology allows it. Direct rekey already inserts the replacement first and then calls that exact-delete path for old tuple cleanup. Explicit public batches retain their shared batch cadence.
- 🟩 [x] Validation after projection-coupled string delete loosening passed: Release/x64 harness build, `concurrency-contract-sanity`, `concurrency-workload-matrix`, `public-surface-api-sanity`, `public-api-snapshot`, `ss8-8-index-api-sanity --path artifacts\ss8-8-index-api-sanity-string-projection-immediate-delete.lbdx`, and `git diff --check`.
- 🟩 [x] Fixed `VS8` writer-context terminal publication: `PublishVarKeyScalar8WriteContext` now flushes staged terminal identity shelf bytes, clears terminal read caches, and releases terminal shelf ownership; abort releases the same terminal ownership state. This closes a lxl gap where `VS8` terminal-local writer-context inserts could stage bytes without the var-key publish path flushing them.
- 🟩 [x] Validation after `VS8` terminal publication fix passed: Release/x64 harness build, `concurrency-contract-sanity`, `concurrency-workload-matrix`, `public-surface-api-sanity`, `public-api-snapshot`, `ss8-8-index-api-sanity --path artifacts\ss8-8-index-api-sanity-vs8-terminal-publish-fix.lbdx`, and `git diff --check`.
- 🟩 [x] Added `VS8` terminal identity exact-delete writer-context support: exact deletes against terminal identity roots now stage only the terminal shelf containing the target identity, leave empty shelves/root valid for later maintenance, and retry on terminal-shelf ownership contention rather than falling back to broad serialized mutation.
- 🟩 [x] Validation after `VS8` terminal identity exact-delete writer-context support passed: Release/x64 harness build, `concurrency-contract-sanity`, `concurrency-workload-matrix`, `public-surface-api-sanity`, `public-api-snapshot`, `ss8-8-index-api-sanity --path artifacts\ss8-8-index-api-sanity-vs8-terminal-exact-delete-writer-context.lbdx`, and `git diff --check`.
- 🟩 [x] Added `VS8` duplicate-run exact-delete writer-context support: duplicate-run exact deletes now stage only the duplicate-run shelf containing the target identity, leave empty linked duplicate-run shelves valid for later maintenance, and reuse the ordinary `VS8` shelf-ownership retry path.
- 🟩 [x] Validation after `VS8` duplicate-run exact-delete writer-context support passed: Release/x64 harness build, `concurrency-contract-sanity`, `concurrency-workload-matrix`, `public-surface-api-sanity`, `public-api-snapshot`, `ss8-8-index-api-sanity --path artifacts\ss8-8-index-api-sanity-vs8-duplicate-run-exact-delete-writer-context.lbdx`, and `git diff --check`.
- 🟩 [x] Added `VS8` terminal identity and duplicate-run range-delete writer-context support: range deletes now clear matching terminal identity or duplicate-run chains by staging shelf item counts to zero while deliberately leaving roots, links, and empty shelves for maintenance cleanup.
- 🟩 [x] Validation after `VS8` terminal/duplicate-run range-delete writer-context support passed: Release/x64 harness build, `concurrency-contract-sanity`, `concurrency-workload-matrix`, `public-surface-api-sanity`, `public-api-snapshot`, `ss8-8-index-api-sanity --path artifacts\ss8-8-index-api-sanity-vs8-terminal-duplicate-range-delete-writer-context.lbdx`, and `git diff --check`.
- 🟩 [x] Added direct `VS8` cold root-prefix publisher: no-batch inserts into missing root-prefix routes now prepare the one-row shelf first, then publish exactly one shelf plus one direct root-router route under a per-root `VS8` topology gate and the storage publication lock instead of opening the old short `VarKeyScalar8Batch`.
- 🟩 [x] Validation after direct `VS8` cold root-prefix publisher passed: Release/x64 harness build, `concurrency-contract-sanity`, `concurrency-workload-matrix`, `public-surface-api-sanity`, `public-api-snapshot`, `ss8-8-index-api-sanity --path artifacts\ss8-8-index-api-sanity-vs8-cold-route-direct-publisher.lbdx`, and `git diff --check`.
- 🟩 [x] Narrowed direct `VS8` shelf-growth publication: grown shelf bytes are planned before entering the guarded section, then the publisher rechecks the parent route, appends the grown shelf, rewrites the parent route, and commits under the per-root `VS8` topology gate plus storage publication lock. If the route changed during planning, the insert retries through the current routed shape.
- 🟩 [x] Validation after direct `VS8` shelf-growth narrowing passed: Release/x64 harness build, `concurrency-contract-sanity`, `concurrency-workload-matrix`, `public-surface-api-sanity`, `public-api-snapshot`, `ss8-8-index-api-sanity --path artifacts\ss8-8-index-api-sanity-vs8-shelf-growth-narrow-publisher.lbdx`, and `git diff --check`.
- 🟩 [x] Narrowed direct `VS8` transform-split publication: replacement split shelves are planned before entering the guarded section, then the publisher rechecks the parent route, appends replacement shelves, rewrites the old shelf offset as the child router, and commits under the per-root `VS8` topology gate plus storage publication lock. If the parent route changed during planning, the insert retries against the current topology.
- 🟩 [x] Validation after direct `VS8` transform-split narrowing passed: Release/x64 harness build, `concurrency-contract-sanity`, `concurrency-workload-matrix`, `public-surface-api-sanity`, `public-api-snapshot`, `ss8-8-index-api-sanity --path artifacts\ss8-8-index-api-sanity-vs8-transform-narrow-publisher.lbdx`, and `git diff --check`.
- 🟩 [x] Removed the public raw `VS8` one-item batch funnel for convenience inserts: `VarKeyScalar8Index.Insert(...)` now encodes the developer key and routes through the direct no-batch `InsertEncoded(...)` path, so ordinary public raw-byte inserts get writer-context or narrowed-topology attribution without caller ceremony. Explicit `BeginBatch(...)` remains the caller-owned durability cadence path.
- 🟩 [x] Validation after public raw `VS8` convenience insert loosening passed: Release/x64 harness build, `concurrency-contract-sanity`, `concurrency-workload-matrix`, `public-surface-api-sanity`, `public-api-snapshot`, `ss8-8-index-api-sanity --path artifacts\ss8-8-index-api-sanity-vs8-public-insert-direct.lbdx`, and `git diff --check`.
- 🟩 [x] Confirmed limited/residual exact-only string criteria are not a broad batch funnel: when direct encoded-range deletion is not available, the path materializes exact key/identity tuples and deletes ordinary, terminal, and duplicate-run `VS8` rows through writer-context exact-delete staging. Added the missing terminal-identity shelf ownership retry so terminal contention is handled like ordinary same-shelf contention.
- 🟩 [x] Validation after materialized exact-only string delete terminal retry passed: Release/x64 harness build, `concurrency-contract-sanity`, `concurrency-workload-matrix`, `public-surface-api-sanity`, `public-api-snapshot`, `ss8-8-index-api-sanity --path artifacts\ss8-8-index-api-sanity-string-terminal-retry.lbdx`, and `git diff --check`.
- 🟩 [x] Added `VS8` terminal identity and duplicate-run insert writer-context support for local no-relink cases: inserts into existing terminal identity shelves or duplicate-run shelves with spare capacity now stage through writer-local terminal/raw shelf ownership. Append, chain rebuild, and relink cases still fall back to the topology path.
- 🟩 [x] Added targeted terminal-root proof to `concurrency-contract-sanity` and fixed the direct span range reader for `VS8` terminal identity roots. The cursor reader already understood terminal roots; the direct `ReadVarKeyScalar8IdentityRange(...)` path now treats them as point leaves and copies linked terminal identity shelves.
- 🟩 [x] Validation after `VS8` terminal/duplicate local insert writer-context support and terminal-root span-reader fix passed: Release/x64 harness build, `concurrency-contract-sanity`, `concurrency-workload-matrix`, `public-surface-api-sanity`, `public-api-snapshot`, `ss8-8-index-api-sanity --path artifacts\ss8-8-index-api-sanity-vs8-terminal-reader-probe.lbdx`, and `git diff --check`.
- 🟩 [x] Narrowed terminal identity and `VS8` duplicate-run append publication: append helpers now re-read and recheck the root/tail shelf under `writePublicationSync` plus the DataKernel exclusive publication boundary before linking a first shelf, inserting a tail identity, or appending a new linked shelf. If the evidence changed, the helper returns false so the caller re-enters the existing fallback/rebuild path instead of publishing over stale tail state.
- 🟩 [x] Validation after guarded terminal/duplicate append publication passed: Release/x64 harness build, `concurrency-contract-sanity`, `concurrency-workload-matrix`, `public-surface-api-sanity`, `public-api-snapshot`, `ss8-8-index-api-sanity --path artifacts\ss8-8-index-api-sanity-terminal-duplicate-append-guard.lbdx`, and `git diff --check`.
- 🟩 [x] Guarded full terminal identity and `VS8` duplicate-run chain rebuild publication: rebuild callers now compare captured ordered identity evidence against current linked-chain state under `writePublicationSync` plus the DataKernel exclusive publication boundary before publishing replacement chains. If another writer changed the chain, the caller retries the higher-level insert/delete decision instead of overwriting stale evidence. This keeps rebuilds topology-owned, but removes the blind stale-list replacement hazard.
- 🟩 [x] Validation after guarded terminal/duplicate chain rebuild publication passed: Release/x64 harness build, `concurrency-contract-sanity`, `concurrency-workload-matrix`, `public-surface-api-sanity`, `public-api-snapshot`, `ss8-8-index-api-sanity --path artifacts\ss8-8-index-api-sanity-chain-rebuild-guard.lbdx`, and `git diff --check`.
- 🟩 [x] Revalidated route-optimizer maintenance after the concurrency changes: queued maintenance, optimizer modes, and optimizer lifecycle sanity all pass. The optimizer path already reports stale candidates and uses candidate refresh/version-style checks, so this remains explicit maintenance rather than ordinary write-path concurrency.
- 🟩 [x] Validation after optimizer boundary recheck passed: `varlen-queued-maintenance-sanity`, `varlen-optimizer-modes-sanity`, and `varlen-optimizer-lifecycle-sanity`.
- 🟩 [x] Added the first `VS16` writer-context parity slice: warmed ordinary `VS16` shelf-local inserts now stage in writer-local mutable shelves and publish through the serialized DataKernel publication seam, matching the first `VS8` ordinary-shelf behavior for 16-byte identities.
- 🟩 [x] Routed raw `VS16` one-shot `Insert(...)` / `InsertEncoded(...)` through the writer-context no-split path when no explicit durability batch is active. Unsupported cold-route, growth, split, and transform cases fall back to the existing `VarKeyScalar16Batch` path so caller-owned batch cadence and topology semantics remain unchanged.
- 🟩 [x] Added `RunInternalVarKeyScalar16WriterContextProbe` to `concurrency-contract-sanity`: two independent warmed `VS16` root-prefix shelves stage before either publishes, read back both identity halves after publication, and same-shelf overlap fails with `LibraDexWriteContextVarKeyScalar16ShelfOwnershipException`.
- 🟩 [x] Validation after the first `VS16` writer-context parity slice passed: Release/x64 harness build, `concurrency-contract-sanity`, `concurrency-workload-matrix`, `varlen-queued-maintenance-sanity`, `varlen-optimizer-modes-sanity`, and `public-surface-api-sanity`.
- 🟨 [~] Remaining `VS8`/string boundary: explicit caller-owned `VarKeyScalar8Batch` and fallback from writer-context paths when an active durability batch or unsupported route cleanup is encountered still use the existing durability-batch/topology paths. Those paths are stable but not writer-context concurrent because they intentionally preserve caller-owned durability cadence or explicit maintenance boundaries.
- 🟨 [~] Remaining fixed-scalar writer-admission boundary: cold-route creation, parent-route split, same-prefix shelf-transform, and the proven `SS8-8` duplicate/terminal topology publishers now have narrowed attribution and narrowed critical sections, but they are not shelf-local writer-context mutations. Public batches, broad projection-coupled overwrite semantics, lazy cursor sharing, maintenance, terminal/duplicate cleanup that needs route relink, and unmodeled repair paths remain outside the no-ceremony concurrent writer contract.
- 🟩 [x] Added `VS16` exact-delete and range-delete writer-context parity for warmed ordinary shelves: no-batch raw `VS16` deletes now stage through writer-local ordinary shelves, retry on same-shelf ownership, and fall back to the existing durability-batch path only for unsupported topology or active caller-owned batches.
- 🟩 [x] Added the first `SV8` writer-context slice: warmed ordinary scalar-key/var-identity shelves now support no-batch inserts, exact deletes, and range deletes through writer-local shelf staging; same-shelf ownership is retryable and terminal/linked/topology routes stay on the existing path.
- 🟩 [x] Added the first `SV16` writer-context slice: warmed ordinary 16-byte scalar-key/var-identity shelves now match the `SV8` ordinary-shelf behavior for inserts, exact deletes, and range deletes.
- 🟩 [x] Added the first `VV` writer-context slice: warmed ordinary var-key/var-identity shelves now support no-batch inserts, exact deletes, and range deletes through writer-local shelf staging, with null/empty/value sentinel root-prefix shelves covered by `concurrency-contract-sanity`.
- 🟩 [x] Validation after `VS16` delete/range, `SV8`, `SV16`, and `VV` ordinary-shelf parity passed: Release/x64 harness rebuild, `concurrency-contract-sanity`, `concurrency-workload-matrix`, `public-surface-api-sanity`, and `git diff --check`.
- 🟨 [~] Remaining varlen boundary: explicit caller-owned batches, cold-route creation, shelf growth/split, terminal var-identity roots, linked duplicate/overflow chain relinks, optimizer/maintenance, and other topology publishers are stable fallback or guarded publication paths, but they are not ordinary shelf-local writer-context concurrency.
- 🟩 [x] Added `concurrency-performance-proof` as a bounded performance/proof harness: it reports shape, mode, backing, thread count, operation count, elapsed time, ops/sec, allocation delta, file bytes, expected count, actual count, and checksum for `SS8-8`, `VS8`, `VS16`, `SV8`, `SV16`, and `VV`.
- 🟩 [x] First proof run exposed and fixed a real `VS16` cold-route admission gap: no-batch `VS16` insert now enters writer-context only when the root-prefix route is warmed; missing routes fall back to the existing batch/topology path instead of throwing `The routed VS16 target is unset`.
- 🟩 [x] Validation after the proof harness and `VS16` fallback fix passed: Release/x64 harness rebuild, `concurrency-performance-proof --single-ops 128 --ops-per-thread 32 --max-threads 4 --include-file false`, `concurrency-performance-proof --single-ops 64 --ops-per-thread 16 --max-threads 2 --include-file true`, `concurrency-contract-sanity`, `concurrency-workload-matrix`, and `git diff --check`.
- 🟩 [x] Added durable result export and baseline comparison to `concurrency-performance-proof`: the command writes CSV and markdown artifacts by default, accepts `--output`, `--markdown`, and `--baseline`, and gates matched baseline rows with `--max-slowdown-percent` (default 75), `--max-allocation-growth-percent` (default 25), `--max-file-growth-percent` (default 10), and `--min-throughput-compare-ms` (default 100) so tiny noisy proof rows still compare allocations/file size without false throughput failures.
- 🟩 [x] Validation after proof export/baseline support passed: Release/x64 harness rebuild, default-threshold baseline pair `concurrency-performance-proof --single-ops 32 --ops-per-thread 8 --max-threads 2 --include-file false --output artifacts\concurrency-performance-proof-default-baseline.csv --markdown artifacts\concurrency-performance-proof-default-baseline.md` then `concurrency-performance-proof --single-ops 32 --ops-per-thread 8 --max-threads 2 --include-file false --baseline artifacts\concurrency-performance-proof-default-baseline.csv --output artifacts\concurrency-performance-proof-default-compare.csv --markdown artifacts\concurrency-performance-proof-default-compare.md`, file-backed smoke `concurrency-performance-proof --single-ops 32 --ops-per-thread 8 --max-threads 2 --include-file true --output artifacts\concurrency-performance-proof-file-smoke.csv --markdown artifacts\concurrency-performance-proof-file-smoke.md`, `concurrency-contract-sanity`, `concurrency-workload-matrix`, and `git diff --check`.
- 🟨 [~] Current proof interpretation: the command is now a correctness proof plus a basic performance/allocation/file-size drift gate. It is still not a statistically stable benchmark; production thresholds should be calibrated from repeated local runs before being used as a hard CI gate.
- 🟩 [x] Larger proof run found and closed additional high-volume concurrency gaps: `SV8` fallback collision on active durability batch, `SS8-8` public one-shot route/topology race, `VV` stale writer-context publication during fallback topology, and `VS8`/`VS16` fallback collision/stale publication under heavy same-root-prefix pressure.
- 🟩 [x] `SS8-8` public one-shot inserts now keep a no-contention direct fast path guarded by a per-index admission gate, while overlapped callers enter the existing writer-context/narrow-topology admission path. This avoids the measured all-writer-context allocation regression for single-thread inserts.
- 🟩 [x] Current heavy proof result after fixes: `concurrency-performance-proof --single-ops 4096 --ops-per-thread 1024 --max-threads 8 --include-file false --output artifacts\concurrency-proof-profile-20260624\memory-1024x8-after-varkey-topology-gate.csv --markdown artifacts\concurrency-proof-profile-20260624\memory-1024x8-after-varkey-topology-gate.md` passed for `SS8-8`, `VS8`, `VS16`, `SV8`, `SV16`, and `VV` with all expected counts matching actual counts at the 8-thread row (`8200/8200` each).
- 🟩 [x] Current heavy 8-thread row throughput from the same proof: `SS8-8` `13.89K ops/sec`, `VS8` `9.86K`, `VS16` `9.38K`, `SV8` `12.62K`, `SV16` `13.60K`, and `VV` `9.00K`. Single-thread row throughput in that run was `SS8-8` `25.92K`, `VS8` `5.36K`, `VS16` `6.45K`, `SV8` `6.43K`, `SV16` `7.90K`, and `VV` `7.58K`.
- 🟩 [x] Validation after high-volume proof fixes passed: Release/x64 harness rebuild, heavy memory proof above, file-backed smoke `concurrency-performance-proof --single-ops 128 --ops-per-thread 32 --max-threads 4 --include-file true --output artifacts\concurrency-proof-profile-20260624\file-smoke-after-fixes.csv --markdown artifacts\concurrency-proof-profile-20260624\file-smoke-after-fixes.md`, `concurrency-contract-sanity`, `concurrency-workload-matrix`, and `git diff --check`.
- 🟨 [~] Pre-concurrency performance comparison: no apples-to-apples pre-concurrency `concurrency-performance-proof` artifact exists because this proof command was added during the concurrency lane. Older design-checklist numbers remain useful for broad SQLite/raw-baseline context, but they should not be treated as direct regression evidence for this new multi-thread proof shape.
- 🟩 [x] Closed the high-volume string null/empty key-state route limitation: scalar-8 key-state routes now promote from the inline descriptor page to a terminal identity root when they exceed inline capacity, while reads, membership tests, deletes, rekeys, and grouped string counts continue through the ordinary logical string index surface.
- 🟩 [x] Validation after key-state route promotion passed: Release/Any CPU harness rebuild, `group-by-string-key-state-promotion-proof --per-state 2048`, larger `group-by-string-key-state-promotion-proof --per-state 10000`, `group-by-string-proof --items 50000 --names 500 --active-modulo 3 --prefix name-000`, and `public-api-snapshot --path docs\public-api-snapshot.txt`.
- 🟩 [x] Closed the matching scalar-16 key-state route limitation: scalar-16 null/empty routes now promote from inline descriptor storage to terminal variable-identity roots, covering the `FS32-16` binary-key/GUID-identity route users found by lxl tracing.
- 🟩 [x] Validation after scalar-16 key-state promotion passed: Release/Any CPU harness rebuild, `scalar16-key-state-promotion-proof --per-state 1024`, larger `scalar16-key-state-promotion-proof --per-state 3000`, scalar-8 regression `group-by-string-key-state-promotion-proof --per-state 2048`, `group-by-string-proof --items 50000 --names 500 --active-modulo 3 --prefix name-000`, `public-api-snapshot --path docs\public-api-snapshot.txt`, and `git diff --check`.
