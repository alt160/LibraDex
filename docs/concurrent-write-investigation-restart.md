# LibraDex Concurrent-Write Investigation Restart

## New-Task Prompt

Use the following prompt in a new Codex task:

> Open `C:\VSProjects\LibraDex\docs\concurrent-write-investigation-restart.md` and follow it as the authoritative restart instructions for the LibraDex concurrent-write investigation. Read `AGENTS.md`, `AGENT_CODING_STYLE.md`, `docs/performance-redo-start-here.md`, and `docs/performance-redo-checklist.md` before changing code. Start by auditing the current working tree and proving the benchmark controls described in the restart document. Do not begin LibraDex optimization until the physical-locality and matched-single-baseline concerns are resolved. Preserve unrelated user changes. Keep correctness/parity ahead of timing, run benchmark processes serially, and clean disposable test payloads after successful runs.

## Objective

Determine whether LibraDex's concurrent-write APIs provide useful independent
writer progress, identify the exact coordination costs that limit contended
writes, and produce benchmark evidence that separates:

1. benchmark-plan effects;
2. unavoidable shared-resource effects;
3. API-level serialization;
4. shelf/router conflicts;
5. topology publication and durability costs;
6. correctness or visibility failures.

The intended capability is not simply "more threads must be faster." The useful
product property is:

- independent writers should continue progressing without a database-wide
  single-writer stop;
- physically independent shelf/router domains should obtain useful aggregate
  scaling;
- overlapping writers may be slower, but coordination should be attributable
  and should remain as narrow as the affected shelves or immediate routing
  topology allows;
- one writer should not silently consume most of the work while other callers
  merely remain alive;
- correctness, durability, and defined reader visibility must hold throughout.

SQLite is secondary context for concurrent writes because SQLite is
single-writer by design. The primary concurrency comparison is LibraDex
multi-caller behavior against a workload-matched LibraDex one-thread control.

## 2026-07-16 Investigation Outcome

- 🟩 [x] ShapeBench now constructs route-aligned `SS8-8` concurrency plans and reports physical terminal-shelf and immediate-parent sharing. At `50,000` items, disjoint plans have zero shared terminal shelves; mixed plans measure approximately `25%` shared-shelf items; overlap plans measure `100%`.
- 🟩 [x] Every concurrency locality/API plan has a matched one-thread replay with the same worker-local tuple order and chunk boundaries. Concurrency diagnostics are excluded from the general SQLite rankings and remain in the dedicated concurrency section.
- 🟩 [x] `SS8-8` concurrency rows now close/reopen and exactly compare every key/identity tuple against the deterministic worker plan. Cardinality and the reported two-word digest are secondary evidence, not the correctness oracle.
- 🟥 [!] Exact reopen validation exposed an existing stale-shelf race in `BeginConcurrentBatch`: an overlapping topology fallback could publish an accepted tuple while another writer context still held an older full-shelf image, and that context could later overwrite the tuple. API counters reported `50,000`, while range enumeration, `Count()`, and point lookup proved `49,999`.
- 🟩 [x] The accepted correctness fix holds the existing per-root `SS8-8` topology read gate for the complete writer-context lifetime. Independent shelf contexts still stage concurrently; broad serialized topology fallback waits for active contexts to publish or abort. Ten repeated overlap/batch-64 reproductions and all `24` focused `8/16 worker x 64/250/1000/5000 batch x disjoint/mixed/overlap` rows passed exact reopen comparison at `50,000` tuples.
- 🟥 [!] Rejected BLX branch: bypassing the generic queued-writer facade lock for multi-key identities regressed aggregate writer throughput to approximately `24K-27K/sec` versus matched one-thread controls around `41K-42K/sec`.
- 🟥 [!] Rejected BLX branch: aborting writer contexts that reported zero completed staged mutations reduced conflict publications by roughly `93-96%` and improved several throughput rows, but lost tuples because a physical mutation can precede an ownership exception and the operation-level staged counter.
- 🟩 [x] Accepted replacement: `SS8-8` contexts now own an authoritative dirty-shelf signal. Ordinary shelf read/claim images are tracked separately from accepted dirty shelves, terminal staged bytes remain explicit, and conflict/final completion aborts only when both mutation sources are empty. This preserves accepted bytes even when an admission exception occurs after mutation.
- 🟩 [x] Direct three-sample A/B checks across `8/16 workers x batch 64/1000/5000 x mixed/overlap` reduced conflict publications by approximately `91-97%`; throughput won `6` rows and lost `6`, so the accepted benefit is materially lower publication/write amplification rather than a claimed universal throughput gain. The full `24`-point focused matrix remained exact, and a final telemetry-enabled `20`-run overlap/batch-64 smoke passed exact reopen parity while reporting hundreds of `emptyContextAborts` per run.
- 🟥 [!] Rejected follow-up: retaining and resetting an empty context in place stayed exact but reduced the overlap/batch-64 median to approximately `37.6K/sec`; abort/recreate remains the better branch.
- 🟨 [~] Batch size materially affects the result but is not a global tuning constant. Disjoint plans benefit from larger batches through publication amortization. Mixed/overlap plans generally improve from `64` toward `1,000`, while `5,000` becomes worker-count and scheduling sensitive because long contested contexts trade fewer normal publications for longer ownership windows.
- 🟨 [~] Remaining limiter: conflict-driven retry and service latency. Empty read/claim-only contexts no longer publish, but real dirty conflicts still require publication. Periodic scheduler backoff is rejected; owner-release notification or deterministic acquisition ordering is the next-best branch, with conflict-local merge retained as a higher-risk alternative.
- 🟩 [x] ShapeBench now records allocation-bounded end-to-end batch and explicit final-publish p50/p95/p99/max, plus separate conflicted/unconflicted cohorts. Three isolated `10,000`-item batch-1000 samples put mixed p95 at `253-268 ms` and overlap p95 at `292-307 ms`, while final-publish p95 stayed below `3.1 ms`; the tail is conflict recovery rather than the final publish call.
- 🟥 [!] Rejected periodic 1 ms bounded backoff after sustained collisions. It reduced median overlap conflict count from `531` to `320` but left median p95 flat, raised mixed median p50, and produced a `350 ms` / `28.2K items/sec` overlap regression. Adaptive `SpinWait` was restored; owner-release notification or deterministic acquisition ordering is now the next-best branch.
- 🟩 [x] Focused artifacts: `artifacts/shape-bench/concurrent-topology-gate-20260716/results.csv` and `report.html` contain `36` unique matched-control rows, all with exact reopen parity.

## Repository And State

- Repository: `C:\VSProjects\LibraDex`
- `C:\VSProjects` is a junction to the same data stored on the `E:` VHDX.
  Do not treat `C:` and `E:` paths as separate copies.
- The working tree is heavily modified and contains user work unrelated to this
  investigation. Never revert or overwrite unrelated changes.
- ShapeBench source is currently untracked under
  `C:\VSProjects\LibraDex\LibraDex.ShapeBench`.
- Canonical benchmark artifacts:
  - `artifacts\shape-bench\index-redo12-20260711-2350\results.csv`
  - `artifacts\shape-bench\index-redo12-20260711-2350\report.html`
- Current concurrency refresh artifacts:
  - `artifacts\shape-bench\concurrency-realigned-20260716-1205`
- Broader performance history:
  - `docs\performance-redo-start-here.md`
  - `docs\performance-redo-checklist.md`

Before any edits, inspect `git status --short` and the relevant diffs. Work with
the existing changes.

## Current Proven Benchmark State

The corrected concurrency campaign contains:

- 9 one-thread API baselines;
- 54 concurrent locality diagnostics;
- 63 concurrency rows total;
- 1,035 unique canonical report rows after merge;
- 250,000 unique key/identity tuples per scenario;
- fixed equal item ownership per worker;
- synchronized starts after worker and input construction;
- serial outer scenario execution to avoid SSD contention;
- separate process isolation per scenario;
- all requested workers active;
- exact final cardinality of 250,000 in every concurrency row;
- no stderr/parity failure;
- successful removal of child work payloads.

The HTML now reports:

- aggregate LibraDex throughput versus the matching one-thread LibraDex API;
- average-thread and best-thread throughput versus single;
- slowest-thread rate and rate fairness;
- active/requested writers;
- per-thread item minimum/maximum;
- publication count and items per publication;
- SQLite only as secondary single-writer context.

## Current Performance Evidence

### `SS8-8 BeginConcurrentBatch`

One-thread baselines:

| Batch | One-thread items/sec |
|---:|---:|
| 250 | 48,082 |
| 1,000 | 75,629 |
| 5,000 | 96,799 |

Aggregate concurrent throughput divided by the matching one-thread baseline:

| Locality | Batch | 8 threads | 16 threads |
|---|---:|---:|---:|
| disjoint | 250 | 1.95x | 2.48x |
| disjoint | 1,000 | 1.91x | 2.08x |
| disjoint | 5,000 | 1.87x | 1.93x |
| mixed | 250 | 1.35x | 0.60x |
| mixed | 1,000 | 1.17x | 0.47x |
| mixed | 5,000 | 1.09x | 0.34x |
| overlap | 250 | 0.84x | 0.41x |
| overlap | 1,000 | 0.84x | 0.37x |
| overlap | 5,000 | 0.81x | 0.71x |

Publication fragmentation closely tracks the collapse:

- disjoint: approximately `34.0-162.9` items/publication;
- badly contended mixed/overlap rows: often `1.8-3.1`
  items/publication.

Do not use fairness alone as a health metric. Some mixed 16-thread rows have
approximately 90% fairness because all workers are uniformly slow.

### Generic `BeginConcurrentWriter`

Across six fixed-scalar shapes:

- median aggregate/single is approximately `0.99x`;
- shape medians range approximately `0.91x-1.05x`;
- all requested callers continue progressing;
- the best individual caller is far below the dedicated one-thread rate.

The generic queued writer currently contains a broad facade lock:

- type: `LibraDexQueuedWriter<TKey,TIdentity>`;
- file: `Indexes\LibraDexIndex.cs`;
- field: `singleKeySync`;
- `Insert` currently enters `lock (singleKeySync)` before staged identity/key
  guard work and insertion.

Treat the near-single aggregate result as explained but not yet accepted. Audit
whether this broad lock is required for all shapes and operations, or whether
the identity/key multiplicity guard can use narrower or staged concurrency.

## Benchmark Concerns To Resolve First

Do these before optimizing LibraDex.

### 1. Prove Physical Locality

The current `disjoint` generator assigns contiguous ordinal ranges. That is only
logically disjoint.

For scalar-8 keys, `Key8` groups ordinals by
`Scalar8RootPrefixGroupSize = 2,048`. Equal worker partitions such as 31,250
items are not aligned to that boundary, so adjacent workers can share a routing
group at partition boundaries.

Variable/fixed-byte key generators can have similar prefix-boundary overlap.

Required correction:

- construct worker-specific key-prefix families that are guaranteed to route to
  separate intended domains; or
- instrument route/shelf ownership and prove the actual overlap count.

The benchmark should report, per scenario:

- distinct terminal shelves touched per worker;
- distinct immediate parent routers touched per worker;
- terminal shelf IDs shared by multiple workers;
- parent router IDs shared by multiple workers;
- maximum workers sharing one shelf/router;
- percentage of items routed to a shared versus private domain.

Use these definitions:

- `disjoint`: zero shared terminal shelves and, where possible, zero shared
  immediate parent publication domains;
- `mixed`: a declared private/shared ratio, currently intended as 75%/25%,
  validated from actual routes;
- `overlap`: every worker intentionally writes different tuples through the
  same bounded shelf/router domain set.

Do not label a result disjoint solely from ordinal assignment.

### 2. Add Locality-Matched One-Thread Controls

The current concurrent locality rows are compared with one global random
single-thread baseline. That can mix insertion-order/topology effects with
concurrency costs.

For each API, locality, and batch size, create a deterministic one-thread
control that replays the same planned tuple order and publication boundaries as
closely as a serial execution permits.

Recommended controls:

- `single-global-random`: preserve the existing broad random baseline;
- `single-disjoint-plan`: replay each disjoint worker partition serially in a
  deterministic worker/chunk schedule;
- `single-mixed-plan`: replay the exact mixed plan serially;
- `single-overlap-plan`: replay the exact overlap plan serially.

The primary ratio for a concurrent diagnostic must use its locality-matched
single control. Retain the global random control only as secondary context.

The same tuple set, tuple encoding, per-caller order, batch boundaries, and
durability behavior must be used. Only caller concurrency should differ.

### 3. Separate Concurrency Rows From SQLite Rankings

The general report currently includes concurrency diagnostics in:

- overall LibraDex-versus-SQLite median;
- shape and workload scoreboards;
- biggest wins;
- biggest gaps;
- generated "What This Run Says" findings.

That contaminates the broad SQLite index comparison with a capability test for
which SQLite is intentionally serialized.

Required report behavior:

- exclude concurrency diagnostic rows from general SQLite scoreboards and
  findings by default;
- retain them in the dedicated `Concurrent Write Capability` section;
- allow an explicit filter or detail view for SQLite context;
- make the primary concurrency ratios LibraDex aggregate/single and
  caller-latency/fairness metrics.

## LibraDex Investigation Branches

Follow BLX: investigate materially different branches, preserve evidence, and
do not commit to the first plausible explanation.

### Branch A: Queued-Writer Admission

Audit `LibraDexQueuedWriter<TKey,TIdentity>` line by line.

Determine:

- what invariant `singleKeySync` protects;
- whether the staged identity/key guard is the only global mutable state;
- whether insertion itself is unnecessarily inside the same lock;
- whether distinct identity/key multiplicity modes need different admission;
- whether the lock can be narrowed to guard reservation/commit bookkeeping;
- whether a concurrent dictionary, striped guard, reservation token, or
  writer-local staged guard better matches the design;
- whether rekey/delete use the same broad serialization;
- whether encoded fixed-shape fast paths bypass or duplicate the guard.

Acceptance is not merely higher throughput. Prove:

- duplicate-key/identity rules;
- exact tuple parity;
- rekey atomicity;
- delete correctness;
- failure rollback;
- durability publication;
- no deadlock or abandoned reservation.

### Branch B: Publication Fragmentation

Add low-overhead attribution counters before changing publication behavior.

Classify each publication as at least:

- normal caller batch completion;
- shelf-capacity split;
- router/topology split;
- conflict-driven flush;
- retry after admission loss;
- fallback-writer publication;
- durability-required publication;
- final completion flush.

Also count:

- attempted versus accepted staged items;
- shelf admission conflicts;
- parent-router admission conflicts;
- topology fallback entries;
- retries per item and per batch;
- useful items per publication;
- bytes written per useful item;
- DataKernel write count and total bytes;
- time blocked in each coordination category.

Correlate throughput with publication cause. Do not infer cause only from total
publication count.

Potential branches to evaluate after attribution:

- larger conflict-local staged contexts;
- coalescing compatible contexts before publication;
- deterministic shelf/router acquisition ordering;
- bounded backoff when many callers target one domain;
- separating terminal-shelf publication from parent-router topology changes;
- allowing independent shelves to publish without sharing a broad fallback;
- reducing repeated route rediscovery after a conflict.

Reject any branch that improves timing while weakening route ownership,
durability, or exact parity.

### Branch C: Per-Caller Service Quality

Aggregate throughput is insufficient. Capture operation or batch latency:

- p50;
- p95;
- p99;
- maximum;
- active execution time;
- blocked time;
- number and duration of stalls.

For `BeginConcurrentBatch`, report batch completion latency. For
`BeginConcurrentWriter`, report sampled insert latency without allocating or
recording every operation inside the timed loop.

The important distinction is:

- healthy sharing: aggregate progress rises and callers complete with bounded
  latency;
- uniform slowdown: fairness is high but every caller is slow;
- starvation: aggregate may look acceptable while one or more callers stall;
- serialized facade: aggregate stays near single and callers complete in a
  queue.

Do not require the best concurrent caller to equal dedicated one-thread
throughput as a universal acceptance rule. Instead, determine whether the
observed reduction is explained by shared CPU/storage bandwidth or by avoidable
coordination.

### Branch D: Scaling Threshold

Use a focused diagnostic, not the full shape matrix:

- threads: `1, 2, 4, 8, 12, 16`;
- one representative fixed-scalar shape first (`SS8-8`);
- selected batch sizes only after matched controls exist;
- disjoint, mixed, and overlap;
- vary the number of independent route/shelf domains separately from thread
  count.

Identify whether collapse correlates with:

- logical processor count;
- independent domain count;
- shared parent-router count;
- publication count;
- DataKernel write bandwidth;
- one specific synchronization primitive.

Only broaden across shapes after the limiting mechanism is proven.

## Missing Correctness And Capability Tests

After the benchmark-control corrections and insert attribution are stable, add
focused concurrency coverage for:

### Exact Final State

- full tuple digest or exact ordered tuple comparison after close/reopen;
- not only final cardinality;
- verify no missing/duplicate substitution can cancel numerically;
- include deterministic expected digest in the result.

### Mutations

- disjoint concurrent deletes;
- overlapping concurrent deletes;
- disjoint rekeys;
- overlapping rekeys;
- mixed insert/delete/rekey;
- duplicate keys with distinct identities;
- duplicate identities where policy allows;
- identity/key uniqueness rejection;
- topology splits during mutation.

### Readers During Writes

- point lookup during writes;
- list lookup during writes;
- range identity/key retrieval during writes;
- prefix retrieval during writes;
- count-all and criteria count during writes;
- defined visibility semantics;
- no torn route/shelf visibility;
- reader p50/p95/p99 latency while writers progress.

State the expected consistency model before judging results. Do not infer a
snapshot guarantee that the public API does not promise.

### Durability And Recovery

- close/reopen after successful concurrent work;
- injected failure before/after shelf publication;
- injected failure around router publication;
- recovery from partially completed caller contexts;
- exact expected tuple state after recovery.

## Benchmark Fairness Rules

- Pre-generate the same deterministic tuple corpus for LibraDex and SQLite.
- Perform alloc-heavy setup outside timing.
- Use a separate process for each measured scenario.
- Perform a cold unrecorded pass, close and delete, then measure the second
  equivalent run where that remains part of the campaign contract.
- Do not run benchmark scenarios concurrently with each other.
- SQLite remains rollback-journal/default-style index context unless the user
  explicitly changes the contract; do not enable WAL or unrelated database
  optimizations.
- SQLite tables/indexes must remain covering and type-equivalent to the LibraDex
  shape.
- Serialize SQLite writes with a runtime primitive instead of measuring SQLite
  lock-failure churn.
- Do not silently cap item counts or operations in a way that changes the
  workload represented by the row.
- Use small adaptive repeat counts for very fast operations. Begin with three
  isolated samples; add two or three only when spread exceeds the declared
  noise threshold.
- Report the sample count and spread.
- Do not repeatedly execute thousands of identical cached operations merely to
  make a timer large.
- Keep parity and performance fields separate.

## Test Artifact Rules

Follow `AGENTS.md` and `Prune-TestArtifacts.ps1`.

- Give every run a dedicated root.
- Prefer `%TEMP%`, `T:\`, or another disposable location for bulky databases
  and index payloads.
- Keep compact CSV, HTML, markdown, manifests, and necessary logs.
- Delete successful child databases/indexes/work directories after result
  capture.
- A failed run may retain only the smallest diagnostic payload.
- Delete retained failure payloads after the corrected retry succeeds.
- Never clean another active test process's work directory.
- Before ending test-heavy work, measure actual retained artifact bytes.
- Do not leave more than 1 GB of generated payload without explicit approval.

## Build And Verification Rules

Before compiling:

1. identify changed source/config/project files;
2. copy them under `.code-history` with timestamped names and
   solution-relative paths;
3. prune history files older than four hours relative to the newest retained
   history file;
4. for a Rebuild, remove only the targeted project `bin` and `obj`;
5. use the repository MSBuild pattern and include `/restore` when assets may
   have been removed.

Use Release x64 and `/m:1`. Do not parallelize benchmark execution.

Minimum verification after a harness-only change:

- Release x64 ShapeBench rebuild;
- `git diff --check` for touched files;
- focused small-corpus parity run;
- focused concurrency diagnostic;
- report generation;
- report row uniqueness and expected-key validation;
- artifact-size and remaining-work-tree validation.

Minimum verification after a LibraDex concurrency implementation change:

- all harness-only gates above;
- relevant public API sanity commands;
- duplicate/multiplicity correctness;
- exact final tuple parity after reopen;
- focused disjoint/mixed/overlap diagnostics;
- mutation tests for any changed operation;
- failure/durability validation for any changed publication path.

## Required Work Order

Use this sequence unless evidence forces a change:

1. Read the repository instructions and current concurrency code.
2. Record current `git status` and do not disturb unrelated work.
3. Reproduce one small current concurrency diagnostic from the existing binary.
4. Correct/prove physical locality.
5. Add locality-matched one-thread controls.
6. Remove concurrency diagnostics from general SQLite rankings.
7. Rebuild and rerun the focused `SS8-8` diagnostic matrix.
8. Validate exact tuple parity and artifact cleanup.
9. Decide whether current results still show:
   - queued-writer facade serialization;
   - publication fragmentation;
   - per-caller latency or starvation;
   - a thread/domain scaling threshold.
10. Add attribution telemetry for the first still-proven limiter.
11. Perform line-by-line simulated execution with contrived inputs before
    changing synchronization/publication behavior.
12. Evaluate multiple implementation branches.
13. Implement only the best-supported branch.
14. Rerun focused diagnostics before broadening across shapes.
15. Update the canonical CSV/HTML only after correctness and scenario-key
    validation.
16. Update `docs/performance-redo-start-here.md` and
    `docs/performance-redo-checklist.md` with accepted findings.
17. Clean disposable artifacts and report retained size.

## Stop Conditions

Stop the run and report immediately when:

- parity differs;
- final exact tuple digest differs;
- any requested worker performs zero work unexpectedly;
- worker exceptions are swallowed or only printed;
- a child exceeds its guard timeout;
- result rows duplicate or omit scenario keys;
- the measured tuple plan differs between engines or controls;
- disk payload growth exceeds the artifact policy;
- publication telemetry is internally inconsistent;
- a claimed disjoint scenario shows unexpected shared-domain routing;
- performance changes materially but attribution counters do not explain why.

Do not automatically continue a broad benchmark after one of these conditions.

## Acceptance Criteria

The investigation is complete only when:

- physical locality is measured, not assumed;
- every concurrent row has a locality-matched one-thread control;
- concurrency rows no longer distort general SQLite scoreboards;
- exact final tuple parity survives close/reopen;
- all workers receive equal planned work and their actual completion is
  reported;
- per-caller latency and blocked time are visible;
- publication causes and write amplification are attributable;
- the queued-writer broad lock is either justified by proven invariants or
  safely narrowed;
- disjoint scaling and contended degradation have a demonstrated mechanism;
- mutation and reader/writer concurrency semantics are validated;
- reports contain current rows only;
- successful runs leave only compact artifacts.

## Expected Communication

Keep updates concise and evidence-based. Distinguish:

- proven benchmark defect;
- proven LibraDex defect;
- expected contention cost;
- unproven suspicion;
- accepted tradeoff.

When reporting a bad row, include:

- shape/API/locality/batch/threads;
- matched one-thread rate;
- aggregate and aggregate/single;
- average/best/slowest caller rates;
- p50/p95/p99 latency when available;
- active writers and actual per-worker item counts;
- shelf/router overlap;
- publication count and cause breakdown;
- items/publication;
- bytes and writes per useful item;
- exact parity result.

Do not describe SQLite as an equivalent concurrent-writer competitor. Use it
only to show the capability boundary and index-maintenance context.

## 2026-07-17 Queue, Cancellation, And Reader/Writer Closure

- 🟩 [x] Added bounded FIFO session admission with CPU-derived defaults and
  configurable active, queued, timeout, and action-quantum limits.
- 🟩 [x] Added synchronous/async action entry, managed-producer `InsertAll`,
  cross-thread cooperative cancellation, timeout/rejection diagnostics, and
  automatic admission for direct queued-writer operations.
- 🟩 [x] Action rotation prevents one long producer from monopolizing admission;
  strict homogeneous waves prevent a later high-limit request from bypassing a
  queued low-limit head.
- 🟩 [x] Same-shelf retry parks on a shelf-targeted completion signal. Global
  `PulseAll` and renewed adaptive spin were evaluated and rejected.
- 🟩 [x] Memory reader/writer coherence uses copy-on-write snapshots activated
  only during cursor reads. The public mutation surface and repeated reader
  progress sanity pass. File-wide logical cursor snapshots remain a separate
  design question; current file reads retain per-raw-read gating.
- 🟩 [x] Canonical results and updated HTML:
  `artifacts/shape-bench/current-concurrency-targeted-250k-20260717-0121/`.
  The result is `117/117` unique rows with zero count/stderr/admission-health
  failure. Preserve the existing warning that only `48` rows have exact reopen
  tuple-map proof, but interpret that as benchmark-oracle coverage rather than
  a LibraDex product requirement for same-key overlap.
- 🟩 [x] Updated concurrency contract: LibraDex owns storage integrity, bounded
  admission, cancellation, fairness, and valid final reopened state. It does not
  arbitrate caller-created logical conflicts such as two unrelated threads
  writing overlapping keys unless a future explicit coordination surface adds
  that behavior. Disjoint and controlled unique-tuple scenarios should keep
  exact tuple-map validation; intentional overlap scenarios should validate
  whole-entry publication, coherent indexes/storage, clean reopen, and healthy
  admission/queue telemetry.
