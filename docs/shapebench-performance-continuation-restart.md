# LibraDex ShapeBench Performance Testing And Reporting Restart

## Paste This Into The New Task

> Continue the LibraDex ShapeBench performance-testing and reporting work from `C:\VSProjects\LibraDex`. Read `AGENTS.md`, `AGENT_CODING_STYLE.md`, and `docs/shapebench-performance-continuation-restart.md` first and treat the restart document as the authoritative current-task state. Then read the narrower linked documents only where directed. Preserve all unrelated working-tree changes. Continue from the restart document's "Immediate Continuation" section without asking me to restate the project history. Correctness and benchmark parity come before timing. Run outer benchmark scenarios serially, use isolated child processes, stop on parity/guard/provenance failures, update the HTML only from validated current rows, and clean disposable payloads after successful runs.

## Purpose

This document is the operational handoff for continuing the long-running
LibraDex-versus-SQLite ShapeBench campaign in a new Codex task.

It is intentionally written for the next coding agent rather than as a
high-level user summary. It records:

- the benchmark contract agreed with the user;
- the current source, binary, CSV, HTML, and artifact state;
- which results are current, historical, focused, or unsafe to merge;
- completed benchmark and LibraDex corrections;
- known open performance/reporting issues;
- exact continuation priorities;
- build, run, resume, merge, monitoring, reporting, and cleanup rules;
- stop conditions and acceptance gates.

Do not ask the user to recreate the history unless repository evidence conflicts
with this document.

## Read Order

Read these files in order before changing code:

1. `C:\VSProjects\LibraDex\AGENTS.md`
2. `C:\VSProjects\LibraDex\AGENT_CODING_STYLE.md`
3. this document;
4. `docs\concurrent-write-investigation-restart.md` for the current
   concurrency branch;
5. `docs\performance-redo-start-here.md` for historical performance evidence;
6. `docs\performance-redo-checklist.md` for accepted/rejected work;
7. `LIBRADEX_DESIGN_CHECKLIST.md` only for the shape or API being changed.

The performance start-here/checklist files are large and historical. Prefer
this document for current execution state, then use those files as evidence.

## User's Benchmark Contract

### Comparison Goal

The campaign compares LibraDex's index creation and index usage with equivalent
SQLite B-tree index work. It is not intended to compare LibraDex with SQLite as
a full relational database.

SQLite should not receive unrelated database-engine advantages that LibraDex
does not have. In particular:

- do not enable WAL merely as a performance trick;
- do not add payload/source columns;
- do not benchmark table-row retrieval when an index-only answer is possible;
- do not use database features unrelated to the equivalent index operation;
- do not intentionally force SQLite lock-failure/retry overhead.

The current ShapeBench SQLite schema is:

```sql
CREATE TABLE ix(k <matching type> NOT NULL, id <matching type> NOT NULL);
CREATE INDEX ix_k_id ON ix(k, id);
```

This follows the user's requested two-column table plus covering `(k,id)`
index. Keep key and identity storage types equivalent to the LibraDex shape:

- 8-byte scalar: SQLite `INTEGER`;
- 16-byte, 32-byte, and variable bytes: SQLite `BLOB`.

For every SQLite read workload, verify with `EXPLAIN QUERY PLAN` in a focused
sanity test that the covering index is actually used and the table is not
visited for returned key/identity data. Do not put `EXPLAIN` inside measured
timing.

### Shapes

The current ShapeBench shape permutation is:

| Shape | Key family | Identity family |
|---|---|---|
| `ss8-8` | scalar 8 | scalar 8 |
| `ss16-8` | scalar 16 | scalar 8 |
| `ss8-16` | scalar 8 | scalar 16 |
| `ss16-16` | scalar 16 | scalar 16 |
| `fs32-8` | fixed 32 | scalar 8 |
| `fs32-16` | fixed 32 | scalar 16 |
| `sv8` | scalar 8 | variable |
| `sv16` | scalar 16 | variable |
| `vs8` | variable | scalar 8 |
| `vs16` | variable | scalar 16 |
| `vv` | variable | variable |

Do not silently remove a family because it is slow or difficult.

### Dataset And Batches

- Canonical broad campaign dataset: `250,000` unique tuples.
- Write batch sizes: `250`, `1,000`, and `5,000`.
- Concurrency caller counts: `8` and `16`, with one-thread controls.
- The same deterministic corpus must be used:
  - between LibraDex and SQLite;
  - between cold and measured passes;
  - between repeated samples;
  - between one-thread and concurrent controls except for the intended caller
    schedule.

The earlier threaded 10,000-item cap was a benchmark defect and was removed.
Never reintroduce a cap that changes the dataset represented by a row.

Focused diagnostic campaigns may use fewer items, but they must be clearly
identified as diagnostics and must never overwrite canonical 250,000-item rows.

### Process Isolation And Temperature

For every measured scenario:

1. launch a fresh child process for LibraDex;
2. run an unrecorded cold/warm-up-equivalent pass in its own disposable files;
3. close and delete that pass;
4. launch/run the recorded equivalent pass in fresh files;
5. repeat independently for SQLite;
6. delete successful database/index payloads after capturing results.

The current `run-all` parent implements engine/pass child isolation:

- LibraDex warmup child;
- LibraDex measured child;
- SQLite warmup child;
- SQLite measured child.

Do not run different outer scenarios concurrently. SSD/VHDX contention would
make the campaign less honest.

### Timing Boundaries

Exclude from measured timing:

- corpus generation;
- key/identity encoding caches;
- worker/thread construction;
- command and parameter construction where reusable;
- allocation-heavy expected-result preparation;
- start-gate preparation;
- correctness digest preparation;
- report aggregation;
- process startup and schema creation unless the workload explicitly measures
  index construction.

Include only the actual operation under comparison and its required publication
or transaction boundary.

Do not hide required index work outside timing. For example, if index
publication/commit is necessary for a completed batch, it belongs in the
measured write.

### Repetition

Do not execute thousands of identical operations merely to inflate timer
duration or reward one engine's process-local cache.

For very fast aggregate operations:

1. begin with three isolated samples;
2. compute spread;
3. when spread is within the declared threshold, report median and worst;
4. when spread is excessive, add two or three isolated samples;
5. report sample count and spread.

`count-all-api` currently uses three initial sample waves and up to three extra
waves when spread exceeds 15%. Review whether those waves remain isolated
enough for the operation being claimed.

### Workload Meaning

The benchmark must use the best public LibraDex API for the real operation, not
one structurally convenient generic reader.

Current workload families:

- sorted batch inserts;
- deterministic random batch inserts;
- deterministic direct inserts;
- public concurrent writer inserts;
- public concurrent batch inserts;
- one-key identity lookup;
- list-of-keys identity lookup;
- range identity retrieval;
- prefix identity retrieval;
- range key retrieval;
- prefix key retrieval;
- range pair retrieval;
- prefix pair retrieval;
- count all through the public count API;
- criteria range count through the public count API;
- criteria prefix count through the public count API.

Real-world questions represented include:

- identities for one exact key;
- identities for a list of exact keys;
- identities between key bounds;
- keys between bounds;
- keys/identities under a prefix;
- total entries;
- entries matching range/prefix criteria;
- sorted versus random-ish write publication;
- independent, mixed, and overlapping concurrent writer domains.

Materializing retrieval workloads and aggregate count workloads are different.
Do not compare a LibraDex count API with a SQLite row-materialization query, or
vice versa.

### Count Semantics

Use the public/count-specialized LibraDex paths:

- `Count()`;
- `Count(condition)`;
- shape-native encoded range/prefix count methods where that is the current
  public optimized contract.

Do not count by iterating every returned item when a count API exists.

LibraDex count-all is not required to be O(1). The intended design is allowed
to walk shelf metadata and sum live slot counts, avoiding item decoding and
materialization without maintaining one globally updated total.

Criteria count should find logical boundaries/extents and add shelf/slot
metadata for contained regions rather than materializing every item.

For adaptive count rows, distinguish:

- logical result cardinality;
- operations/sample waves;
- accumulated `result_items`.

Never treat accumulated sample-wave results as one logical cardinality.

### Concurrency Semantics

SQLite is single-writer. Serialize its write transactions with a runtime
primitive rather than measuring database lock contention. SQLite is secondary
context for concurrent write capability.

The primary concurrent-write comparisons are:

- LibraDex aggregate concurrent throughput versus a matched one-thread
  LibraDex replay;
- average, median, fastest, and slowest caller service;
- caller latency and blocking;
- active/requested callers;
- physical shelf/router sharing;
- publication and write amplification.

Concurrency is not required to outperform single thread on a per-thread basis.
The capability goal is useful independent progress with coordination limited to
the affected shelves/router topology.

Read and follow `docs\concurrent-write-investigation-restart.md` before changing
concurrent-write code or tests.

## Current Repository State

Snapshot taken 2026-07-16 afternoon, America/Phoenix.

- Repository: `C:\VSProjects\LibraDex`
- `C:\VSProjects` is a junction to the same data on the `E:` VHDX.
- The working tree is heavily modified.
- `LibraDex.ShapeBench\` is currently untracked.
- Many other source files contain user work.
- Never reset, checkout, clean, or revert unrelated files.
- No ShapeBench process was running at the snapshot.
- Release x64 ShapeBench binary exists:
  - `LibraDex.ShapeBench\bin\x64\Release\net8.0\LibraDex.ShapeBench.exe`
  - `LibraDex.ShapeBench\bin\x64\Release\net8.0\LibraDex.ShapeBench.dll`
- Current source enumerates `1,089` scenarios at 250,000 items.
- Current scenario defaults:
  - batches `250,1000,5000`;
  - threads `1,8,16`;
  - child timeout 900 seconds;
  - write no-progress timeout 180 seconds.

Always inspect `git status --short` and relevant diffs before editing.

## Current Artifact State

### Broad Canonical Campaign

Root:

`artifacts\shape-bench\index-redo12-20260711-2350`

Files:

- `results.csv`
- `report.html`

Snapshot:

- 1,035 rows;
- 1,035 unique `shape|workload|batch|threads` keys;
- CSV has 37 columns;
- report exists;
- 243 count rows;
- 630 read rows;
- 162 write rows;
- 63 older concurrency rows.

This is the broad human-facing report currently open in the browser, but it is
not fully current for the latest concurrency implementation and report schema.

### Current Source Matrix

The current ShapeBench binary enumerates 1,089 scenarios, not 1,035.

The 54 additional current scenarios are matched one-thread concurrency controls:

- 18 `BeginConcurrentBatch` locality/worker-plan controls;
- 36 generic `BeginConcurrentWriter` locality/worker-plan controls.

Therefore:

- `1,035` is the old broad campaign size;
- `1,089` is the current complete matrix size;
- do not call the broad report a complete current-source report.

### Focused Current Concurrency Evidence

Root:

`artifacts\shape-bench\concurrent-topology-gate-20260716`

Files:

- `results.csv`
- `report.html`

Snapshot:

- 36 rows;
- 36 unique keys;
- `ss8-8` only;
- concurrent batch plus matched one-thread controls;
- batches `250,1000,5000`;
- callers `8,16`;
- diagnostic dataset is 10,000 items;
- every row reports exact reopen parity;
- CSV has the current 51-column concurrency/topology schema.

This focused report is newer and more correct for concurrent-batch behavior,
but it is not a canonical 250,000-item replacement.

### Current 250,000-Item Concurrency Refresh

Root:

`artifacts\shape-bench\current-concurrency-250k-20260716-1515`

Files:

- `results.csv`
- `report.html`
- `stdout.log`
- `stdout-resume.log`
- `stderr.log`

Snapshot completed 2026-07-16:

- 117 rows and 117 unique logical scenario keys;
- current 75-column schema;
- all rows use 250,000 items, scenario schema `2`, one campaign ID, and the
  rebuilt DLL SHA-256
  `7239C9B254938AB71903052F1ADCC081624B488AB0BE79751D4E015E327CAA92`;
- every row has result cardinality 250,000 and every multi-caller row reports
  all requested callers with equal nonzero item ownership;
- zero stderr, zero retained work directories, and zero database/index
  payloads;
- the standalone HTML was regenerated from the completed CSV;
- `concurrency-contract-sanity` and `concurrency-workload-matrix` both passed
  after a clean current-source Harness rebuild.

The refresh is current evidence, but it did not clear canonical merge gates:

- only 48 rows have ShapeBench's exact reopen tuple-map proof because the
  current exact validator is restricted to `SS8-8` locality workloads;
- 69 rows have cardinality parity but no exact reopen tuple-map proof;
- four nominally disjoint generic-writer rows (`SS16-8` and `SS16-16`, 8 and
  16 callers) report shared terminal shelves;
- 13 nominally disjoint rows report at least one shared immediate parent
  router;
- the disjoint-plan generator allocates every shape using the scalar-8 root
  prefix group size, so the widened-key locality failures are a benchmark-plan
  defect until proven otherwise.

Matched-control medians from the completed report:

| API | Disjoint | Mixed | Overlap |
|---|---:|---:|---:|
| concurrent batch | 0.77x | 0.66x | 0.75x |
| generic concurrent writer | 1.04x | 0.98x | 0.97x |

Concurrent-batch p95 latency medians were approximately `270 ms` disjoint,
`738 ms` mixed, and `1,136 ms` overlap. Worst p95 was approximately `2,076 ms`.
Treat these as current performance evidence, not as a canonical accepted cohort,
until exact reopen coverage and physical-disjoint plan validation are complete.

### Critical Merge Prohibition

Do not merge `concurrent-topology-gate-20260716` rows into the broad canonical
CSV.

Reasons:

1. focused rows use 10,000 items;
2. canonical rows use 250,000 items;
3. the current scenario key omits dataset item count;
4. the focused CSV has 51 columns;
5. the canonical CSV has 37 columns.

Eighteen focused keys would replace existing canonical rows despite describing
a different dataset, and eighteen matched controls would be added. That would
produce a numerically tidy but false report.

### Artifact Hygiene

Snapshot retained ShapeBench artifacts:

- approximately 228 files;
- approximately 12.2 MiB total;
- no large work trees;
- no active benchmark process.

This is healthy. Keep it that way.

## Completed Corrections And Proven Findings

### Harness And Reporting

- 🟩 [x] Provenance-aware 75-column CSV rows now record dataset items/kind,
  scenario schema `2`, measurement UTC, campaign, loaded-assembly SHA-256, machine,
  and runtime identity.
- 🟩 [x] Resume identity now includes dataset items/kind, scenario schema, and
  binary SHA-256; resume into legacy 37/51-column CSV is rejected before append.
- 🟩 [x] HTML remains compatible with legacy CSV, labels absent provenance as
  unavailable, exposes dataset/campaign filters, and warns when provenance is
  missing or mixed.
- 🟩 [x] Current 75-column resume/build-identity proof retained at
  `artifacts\shape-bench\provenance75-dll-hash-proof-20260716`: the same
  scenario at 1,000 and 1,200 items produced two rows, and the recorded
  `binary_sha256` exactly equals the loaded `LibraDex.ShapeBench.dll` SHA-256.
- Separate process isolation for each engine/pass.
- Serial outer scenario execution.
- Child timeout/no-progress guards.
- Result-cardinality parity gate before CSV append.
- Successful child work-directory cleanup.
- Human-readable HTML with filters, scoreboards, drilldowns, and raw rows.
- Header-row placement corrected.
- Fixed deterministic tuple generation and engine parity.
- Allocation-heavy setup moved outside timing.
- SQLite threaded writes serialized with a runtime lock.
- Concurrent workers receive equal fixed ownership.
- Concurrent starts are synchronized.
- Thread min/average/median/max throughput is reported.
- Count-all uses adaptive small sampling rather than 2,000 repeated calls.
- The old 10,000-item threaded insert cap was removed.
- Concurrency diagnostics are excluded from general SQLite rankings in the
  current report generator.
- Current concurrency CSV/report can include physical shelf/router sharing.

### Count And Routing

- Count-all tests call public/optimized count APIs.
- Range/prefix count tests use condition/shape-native count APIs rather than
  generic range-reader enumeration where optimized APIs exist.
- Ordinal-to-key generation ordering defects were repaired.
- `VS8`, `VS16`, and `VV` range-count scoop paths were implemented/proven.
- Three-process median count-range evidence:
  - `VS8`: approximately `4.245x` SQLite;
  - `VS16`: approximately `2.889x` SQLite;
  - `VV`: approximately `5.945x` SQLite.
- Variable-key routing and shared-parent publication defects found by ShapeBench
  were repaired with exact parity gates.

### Read Paths

- Stale `lookup-list-identities` rows were isolated and rerun after exact-read
  path changes.
- Variable-key lookup/range defects were debugged from first missing ordinal,
  not explained away as timing noise.
- Range and prefix workloads are now distinct from counts and exact lists.

### FS32 Sorted Batch

The focused 1,000,000-item, batch-5,000, one-thread final measurements recorded:

- `FS32-8` conservative five-run median approximately `416,009 ops/sec`;
- `FS32-16` conservative five-run median approximately `343,929 ops/sec`;
- exact 1,000,000 result parity;
- reopen/lookups/count sanity passed.

See `docs\performance-redo-start-here.md` for the accepted/rejected FS32 design
branches.

### Current Concurrent-Batch Correctness

The latest concurrency investigation proved:

- route-aligned physical locality controls;
- matched one-thread replays;
- exact close/reopen tuple comparison;
- a real stale-shelf race where accepted count was 50,000 but persisted state
  was 49,999;
- accepted fix: hold the per-root `SS8-8` topology read gate for the complete
  writer-context lifetime;
- independent shelf contexts still stage concurrently;
- unsafe empty-context abort based only on completed-operation count was
  rejected;
- accepted context-owned dirty-shelf signal;
- read/claim-only empty contexts now abort without publication;
- conflict publications fell approximately 91-97% in focused A/B checks;
- throughput split evenly between wins and losses, so the accepted claim is
  lower amplification, not universal speedup;
- exact reopen parity survived the focused matrix and repeat smokes.

The next concurrency limiter is conflict retry/service latency, not the already
fixed empty-context publication.

## Known Open Problems

### 🟨 [~] P0: Dataset Provenance And Resume Identity

The harness/resume/report implementation is complete and verified. The broad
canonical artifact still predates row-level provenance and remains pending
structured schema migration; do not resume it directly.

Current CSV rows do not include canonical dataset item count. Current scenario
keys are:

`shape|workload|batch|threads`

The key does not include:

- dataset items;
- benchmark schema/version;
- binary identity;
- run/campaign identity.

Consequences:

- `--resume` can incorrectly skip a scenario measured with another dataset;
- targeted diagnostic rows can overwrite canonical rows;
- old and new CSV schemas can be merged accidentally;
- the HTML cannot reliably explain row provenance.

Fix this before any further canonical resume/merge.

Minimum durable fields:

- `dataset_items`;
- `scenario_schema_version`;
- `measurement_utc`;
- `campaign_id`;
- `binary_sha256` or equivalent build identity;
- optionally source revision plus dirty-state marker;
- machine/runtime identity where practical.

The resume key must include at least:

`shape|workload|batch|threads|dataset_items|scenario_schema_version`

Do not make source commit mandatory because the working tree is intentionally
dirty, but record enough binary/source identity to distinguish builds.

The report must display provenance and warn when filtered rows mix datasets or
binary identities.

### P0: Canonical CSV Schema Migration

Canonical CSV has 37 columns. Current source writes 75 columns; the older
focused concurrency artifact has 51.

Before current rows can be merged:

1. define one current schema;
2. migrate old rows with explicit blanks/defaults for unavailable telemetry;
3. add dataset/provenance columns;
4. validate every row has the expected property set;
5. retain a timestamped backup;
6. regenerate HTML with the current binary;
7. visibly mark migrated historical telemetry as unavailable, not zero.

Never interpret a missing old telemetry value as a measured zero.

### 🟨 [~] P1: Concurrent Batch Latency And Conflict Behavior

Follow `docs\concurrent-write-investigation-restart.md`.

Completed:

- 🟩 [x] record batch p50/p95/p99/max outside post-run aggregation;
- 🟩 [x] separate end-to-end batch, final publish, conflicted-batch, and
  unconflicted-batch latency;
- 🟩 [x] run three isolated `SS8-8` mixed/overlap samples with matched controls;
- 🟥 [!] reject periodic 1 ms bounded backoff: retry counts fell, but median p95
  did not improve and p50/worst-sample latency regressed.

Next-best branch:

- add owner-release notification or deterministic acquisition ordering;
- record narrower blocked/ownership-wait time only if the next branch needs
  core attribution beyond the proven conflicted-batch service tail;
- retain conflict-local merge as a higher-risk alternative.

Use a focused `SS8-8` matrix first. Do not run all 1,089 scenarios to diagnose
one concurrency limiter.

### 🟨 [~] P1: Full 250,000-Item Current Concurrency Refresh

The 117-row current cohort has been run and its standalone HTML generated.
Canonical acceptance remains blocked by incomplete exact-reopen coverage and
nominally disjoint widened-key rows that share physical shelves.

#### 2026-07-16 Accepted Action-Queue Implementation And Rerun

The accepted implementation is shape-specific rather than one-size-fits-all:

- immediate concurrent-writer work now exposes
  `LibraDexQueuedWriter.BeginAction(CancellationToken)`;
- one action normally represents one caller chunk/worker loop and holds one
  session admission lease, so queue admission is not paid per tuple;
- immediate writer actions default to one active publisher because focused
  evidence showed eight active per-tuple publishers reduced useful throughput;
- concurrent batches retain the runtime-derived staging budget of
  `(Environment.ProcessorCount + 1) / 2`, which resolves to 8 on the current
  16-effective-processor host;
- `LibraDexConcurrencyOptions.MaxActiveWriters` remains the expert override;
- writer-wide cancellation can stop all associated actions, while an action
  token can stop one action independently;
- cancellation removes a pending admission request without I/O; unpublished
  batch state aborts when cancellation is observed before publication; once
  publication starts, publication wins.

Implementation anchors:

- `FileSession/LibraDexWriteAdmission.cs`;
- `FileSession/LibraDexFileSession.cs`;
- `PublicApi/IndexOptions.cs`;
- `Indexes/LibraDexIndex.cs`;
- `LibraDex.Harness/Commands/RawHarness.Concurrency.cs`;
- `LibraDex.ShapeBench/Program.cs`.

BLX branches rejected from focused evidence:

- a monitor on every writer insert: multi-caller writer throughput fell to
  approximately `0.46x`-`0.52x` of the prior binary;
- a lock-free active counter on every insert: the cache-line atomics still left
  focused `SS8-8` writer throughput near `0.62x` of the prior binary;
- eight active immediate writer actions: true parallel publishers measured only
  about 29,000 operations/second, below the prior serialized publisher;
- accepted branch: queue immediate work at 1,000-item action/chunk boundaries,
  while allowing concurrent batches to stage with the CPU-derived budget.

Final accepted 250,000-item artifact:

- CSV:
  `artifacts/shape-bench/current-concurrency-action-250k-20260716-2042/results.csv`;
- HTML:
  `artifacts/shape-bench/current-concurrency-action-250k-20260716-2042/report.html`;
- 117 rows, 75 columns, schema 2, one runner binary hash
  `70B1AD4C0E5DC7954ECA7295F89C768264D2BA260F6256DE4FCE5BA6535430A3`;
- zero stderr files and no retained `work` directory;
- exact reopen notes remain present on 48 rows; the existing 69-row exact-map
  coverage gap is not silently upgraded by this rerun.

Matched concurrent aggregate / one-thread control medians:

- writer disjoint: `1.04x` at 8 callers, `1.01x` at 16;
- writer mixed: `0.95x` at 8 callers, `0.95x` at 16;
- writer overlap: `0.96x` at 8 callers, `0.98x` at 16;
- writer median fairness: `0.96`-`0.99`;
- batch disjoint: `1.04x` at 8 callers, `1.11x` at 16;
- batch mixed: `0.98x` at 8 callers, `0.92x` at 16;
- batch overlap: `1.04x` at 8 callers, `0.90x` at 16;
- worst batch p95 in this cohort: 1,663.40 ms;
- worst final-publish p95: 4.25 ms.

Against the immediately prior pre-queue cohort, multi-caller median throughput
improved in every API/locality group:

- batches: `1.25x` disjoint, `1.30x` mixed, `1.20x` overlap;
- writers: `1.28x` disjoint, `1.23x` mixed, `1.24x` overlap.

Correctness gates before the final cohort:

- 🟩 [x] Release x64 Rebuild;
- 🟩 [x] `concurrency-contract-sanity`, including queued cross-thread action
  cancellation and unpublished batch cancellation;
- 🟩 [x] `concurrency-workload-matrix`, all 41 rows.

The standalone concurrency HTML is accepted as current performance evidence for
this implementation. Broad canonical merge remains blocked by the exact-reopen
and widened physical-disjoint issues below.

Remaining acceptance work:

1. extend exact reopen tuple-map validation to every current concurrency shape
   and baseline/control row;
2. correct the per-shape disjoint plan so widened keys do not share terminal
   shelves;
3. decide and document whether one shared immediate parent router is allowed or
   whether the physical-disjoint contract requires zero shared parents;
4. rerun the affected 250,000-item rows serially;
5. require exact reopen tuple parity and validated physical locality;
6. regenerate the standalone current concurrency report;
7. only then merge into a migrated broad canonical report.

Expected current concurrency scenario count is 117:

- 9 original one-thread API baselines;
- 54 multi-caller locality rows;
- 54 matched one-thread locality/worker-plan controls.

Confirm this from `list`; do not rely only on this document if source changes.

### P1: Canonical Broad Refresh

The current complete source matrix is 1,089 scenarios and will take a long time
when run honestly.

Do not restart it blindly after every code change.

Use targeted cohorts:

- rows directly affected by changed code;
- rows currently below SQLite;
- sibling shapes sharing the changed algorithm;
- parity-sensitive boundary cases;
- one stable control cohort to detect machine drift.

After focused acceptance, either:

- refresh only affected canonical rows with matching 250,000-item provenance;
  or
- begin a fresh full 1,089-row campaign when the code is stable enough that
  another broad invalidation is unlikely.

### P1: Report Trust And Human Usability

The report must answer human questions before exposing raw telemetry.

Keep/add:

- executive findings in ordinary language;
- separate read, count, write, and concurrency summaries;
- shape and workload scoreboards;
- collapsible detail;
- filters;
- biggest wins and gaps;
- exact test definition/tooltips;
- clear direction of ratios;
- dataset, sample, binary, and measurement provenance;
- stale/historical/current labels;
- parity and guard status;
- missing telemetry shown as unavailable.

Concurrency rows must remain outside general SQLite rankings.

### P2: Missing Broader Capability Tests

After the current insert campaign is stable:

- sorted/random insert distributions beyond one deterministic corpus;
- delete;
- rekey;
- mixed insert/delete/rekey;
- reader activity during writes;
- count/range/prefix during writes;
- reopen/durability failure injection;
- exact full tuple verification, not cardinality only.

Do not add these to the broad campaign until each focused workload is correct,
bounded, and reportable.

### Separate Existing Validation Blocker

`validate --tier fast` has an existing `SS8-8` router-arena read-cache
expected-read-shape assertion:

- observed four small reads;
- approximately 8,200 bytes;
- expected arena load.

This reproduces independently of the accepted FS32 borrowed-publication change.
Keep it as a separate lane and do not attribute it to ShapeBench concurrency
without new evidence.

## Immediate Continuation

When the user says "continue" in the new task, proceed in this order.

### 🟩 [x] Step 1: Audit Without Editing

- read the required documents;
- inspect `git status --short`;
- inspect diffs for `LibraDex.ShapeBench\Program.cs`,
  `Indexes\LibraDexIndex.cs`, and relevant session/context files;
- confirm no active ShapeBench process;
- confirm current artifact sizes;
- run `ShapeBench list --items 250000` and record current expected count;
- verify canonical and focused CSV row/column counts.

### 🟩 [x] Step 2: Fix Provenance Before More Canonical Work

Update ShapeBench so:

- CSV contains dataset and campaign/build provenance;
- scenario/resume identity includes dataset/schema;
- reports display provenance;
- report generation tolerates old rows but labels absent values;
- mixed-provenance reports show a warning;
- targeted diagnostic rows cannot masquerade as canonical rows.

Add detailed XML comments with `<br/>` for new methods.

Before editing synchronization or performance behavior, keep this change
harness/report-only.

### 🟩 [x] Step 3: Rebuild And Verify Provenance

Follow the repository `.code-history` and Rebuild rules.

Use:

```powershell
$log = 'artifacts\build-shapebench-release-x64-provenance.log'
$msbuild = 'C:\Program Files\Microsoft Visual Studio\18\Insiders\MSBuild\Current\Bin\MSBuild.exe'

& $msbuild 'LibraDex.ShapeBench\LibraDex.ShapeBench.csproj' `
  /t:Rebuild `
  /restore `
  /p:Configuration=Release `
  /p:Platform=x64 `
  /m:1 `
  /v:minimal `
  /fl `
  /flp:"logfile=$log;verbosity=normal"
```

For Rebuild:

- back up changed source/config/project files first;
- delete only targeted ShapeBench `bin`/`obj`;
- do not clean unrelated project outputs.

Run a tiny diagnostic twice with the same scenario but different `--items` and
prove resume does not conflate them.

### 🟨 [~] Step 4: Continue The Concurrency Diagnostic

After provenance is green:

- add batch-latency and blocked/conflict timing telemetry;
- run focused `SS8-8` disjoint/mixed/overlap diagnostics;
- use matched controls;
- test bounded conflict backoff/acquisition ordering as the first branch;
- use at least three isolated samples for ambiguous rows;
- stop on exact reopen parity failure.

Do not optimize the queued-writer facade in the same change unless evidence
specifically points there. The previous lock-removal branch regressed.

### 🟨 [~] Step 5: Produce A Current 250,000-Item Concurrency Report

Once focused behavior is accepted:

- enumerate current concurrency scenarios;
- run them serially at 250,000 items;
- keep a dedicated run root;
- monitor every 30 minutes;
- require no stderr/parity/guard failure;
- verify exact scenario count and unique keys;
- generate standalone HTML;
- clean work payloads;
- summarize capability and limits to the user.

The 117-row run and standalone HTML are complete. Exact reopen coverage and
physical-disjoint validation remain incomplete, so Step 5 is not accepted and
Step 6 must not start.

### Step 6: Migrate/Refresh The Broad Canonical Report

Only after current 250,000-item concurrency evidence exists:

1. back up canonical `results.csv` and `report.html`;
2. migrate canonical rows to the current schema;
3. replace rows by the full provenance-aware key;
4. add current-source rows absent from the old campaign;
5. preserve canonical display IDs where a logical scenario is replaced;
6. assign new IDs after the current maximum for truly new rows;
7. validate unique keys and expected row count;
8. regenerate HTML with current Release binary;
9. verify the HTML contains the replaced current facts;
10. retain backups until report review is complete.

Do not perform a raw text merge. Use structured CSV objects.

## Exact ShapeBench Commands

Executable:

```powershell
$bench = 'C:\VSProjects\LibraDex\LibraDex.ShapeBench\bin\x64\Release\net8.0\LibraDex.ShapeBench.exe'
```

List all current scenarios:

```powershell
& $bench list --items 250000
```

List/count a focused scenario:

```powershell
& $bench list `
  --shape ss8-8 `
  --workload insert-concurrent-batch-overlap `
  --batch 1000 `
  --threads 8 `
  --items 50000
```

Run one engine:

```powershell
& $bench run-one `
  --engine libradex `
  --shape ss8-8 `
  --workload insert-concurrent-batch-overlap `
  --batch 1000 `
  --threads 8 `
  --items 50000 `
  --root 'artifacts\shape-bench\diagnostic-name'
```

Run one paired filtered scenario through parent isolation:

```powershell
& $bench run-all `
  --shape ss8-8 `
  --workload insert-concurrent-batch-overlap `
  --batch 1000 `
  --threads 8 `
  --items 50000 `
  --root 'artifacts\shape-bench\diagnostic-name' `
  --out 'artifacts\shape-bench\diagnostic-name\results.csv' `
  --report 'artifacts\shape-bench\diagnostic-name\report.html' `
  --progress-every 1
```

Resume only after the provenance fix:

```powershell
& $bench run-all `
  --items 250000 `
  --root 'artifacts\shape-bench\campaign-name' `
  --out 'artifacts\shape-bench\campaign-name\results.csv' `
  --report 'artifacts\shape-bench\campaign-name\report.html' `
  --resume `
  --progress-every 1
```

Generate HTML from validated CSV:

```powershell
& $bench report `
  --in 'artifacts\shape-bench\campaign-name\results.csv' `
  --report 'artifacts\shape-bench\campaign-name\report.html'
```

## Monitoring Protocol

For a long serial run, create a conservative 30-minute automation when that
capability is available.

Every check should inspect:

- parent PID/process;
- currently running child command line or work path;
- `results.csv` row count;
- expected campaign row count;
- approximate completion percent;
- latest completed scenario;
- stdout tail;
- stderr tail;
- report existence;
- work-directory size;
- total ShapeBench artifact size;
- parity/guard errors;
- suspicious throughput drift.

Report concise current facts:

- running/completed/failed;
- rows and percent;
- latest/current scenario;
- elapsed age;
- simple top/bottom ratios when meaningful;
- workload medians only after enough rows exist;
- count-all sample operations/spread when count rows first appear;
- any suspicious outlier.

If failed:

- report the key error;
- stop the automation;
- do not restart automatically;
- retain only the smallest failing work payload.

If completed:

- verify expected unique provenance-aware keys;
- verify stderr/parity status;
- generate report;
- clean work payloads;
- report CSV/HTML links;
- stop the automation.

Do not use a foreground sleep loop that blocks the user's UI when an automation
is available.

## Failure Diagnosis Protocol

When a row fails:

1. stop the broad campaign;
2. preserve the smallest failing child directory;
3. identify the first incorrect key/ordinal/tuple;
4. compare LibraDex and SQLite predicates/results;
5. perform line-by-line simulated execution with contrived boundary inputs;
6. decide whether the defect is:
   - benchmark generation;
   - encoding/order conversion;
   - route selection;
   - shelf publication;
   - count boundary logic;
   - concurrent ownership/publication;
   - report interpretation;
7. audit sibling shapes sharing the same algorithm;
8. fix the broadest correct family-level cause;
9. rerun the exact failing scenario;
10. rerun sibling controls;
11. remove the retained failed payload after corrected success.

Never continue timing a parity-failing path.

## Result Validation

Before accepting a run:

- expected row count matches;
- provenance-aware scenario keys are unique;
- no missing current-source scenarios in a claimed full campaign;
- `operations` has the intended meaning;
- `result_items` has the intended logical/accumulated meaning;
- LibraDex/SQLite result parity holds;
- exact tuple parity holds for concurrency diagnostics;
- requested workers all perform their fixed work;
- no hidden item cap;
- no stderr;
- no guard timeout/no-progress trip;
- report regenerated from the same CSV;
- report visibly contains current replaced rows;
- compact artifacts retained;
- work/database payloads removed.

## Reporting Interpretation

### Ratios

For ordinary index comparisons:

`operation ratio = LibraDex operations/sec / SQLite operations/sec`

For materializing workloads, also report item throughput.

For concurrency:

- primary: LibraDex concurrent aggregate / matched LibraDex one-thread;
- secondary: caller rates, latency, fairness, publication attribution;
- SQLite: capability context only.

### RAM And Disk

Report:

- LibraDex RAM at end;
- SQLite RAM at end;
- LibraDex disk at end;
- SQLite disk at end.

Disk size should include the files required for the index result being compared,
not discarded warmup files or unrelated artifacts.

### Human Conclusions

Do not describe a `0.02x` or `20x` row without checking:

- stale versus current binary;
- operation versus item units;
- result cardinality;
- query plan;
- cache/repeat structure;
- dataset size;
- batch/thread plan;
- route/order generation;
- report freshness.

State conclusions as:

- proven current;
- historical/superseded;
- focused diagnostic;
- suspicious/unexplained;
- expected tradeoff.

## Artifact Cleanup

Follow `AGENTS.md` and `Prune-TestArtifacts.ps1`.

- Successful child runs delete `.lbdx`, `.sqlite`, work trees, and bulky logs.
- Failed runs retain only minimal evidence.
- A successful corrected retry removes superseded failure payloads.
- Never delete another active process's files.
- Keep compact CSV/HTML/markdown/manifests/log tails.
- Prefer `%TEMP%` or `T:\` for large intermediate payloads.
- Measure actual retained bytes before ending.
- Do not leave more than 1 GB without explicit user approval.

Example inspection:

```powershell
$files = Get-ChildItem -LiteralPath 'artifacts\shape-bench' -Recurse -File
$bytes = ($files | Measure-Object Length -Sum).Sum
[pscustomobject]@{
    Files = $files.Count
    MiB = [math]::Round($bytes / 1MB, 2)
    GiB = [math]::Round($bytes / 1GB, 3)
}
```

Use the cleanup script narrowly:

```powershell
.\Prune-TestArtifacts.ps1 `
  -Root 'artifacts\shape-bench' `
  -OlderThanHours 0 `
  -RemoveWorkDirectories `
  -WhatIf

.\Prune-TestArtifacts.ps1 `
  -Root 'artifacts\shape-bench' `
  -OlderThanHours 0 `
  -RemoveWorkDirectories
```

Review its scope and active-process guard before execution. Never use broad
filesystem deletion against a computed unverified path.

## Code Editing And Build Rules

- Use `apply_patch` for manual edits.
- Preserve ASCII unless the file already requires Unicode.
- Add new methods at the end of the related file/class/block where practical.
- New methods require detailed XML comments with `<br/>` line terminators.
- Prefer high-performance, low-call-depth, low-friction code.
- Do not add allocation-heavy telemetry inside measured loops.
- Do not swallow worker exceptions.
- Do not revert unrelated changes.

Before compiling:

1. determine changed source/config/project files;
2. copy them to `.code-history` with timestamped names and
   solution-relative paths;
3. prune history older than four hours relative to the newest history file;
4. for Rebuild, delete only target `bin`/`obj`;
5. invoke MSBuild with `/restore`, Release, x64, `/m:1`.

## Stop Conditions

Stop and notify the user when:

- any parity mismatch occurs;
- exact tuple comparison fails;
- a count result is logically implausible;
- a scenario uses the wrong dataset/provenance;
- a resume would mix datasets or schemas;
- SQLite does not use the expected covering index;
- an ordinal/key conversion violates ordering;
- a requested worker performs zero work;
- worker allocation/setup enters measured timing;
- child timeout or no-progress guard trips;
- stderr contains an exception;
- report row keys duplicate or disappear;
- artifact growth exceeds policy;
- a result changes drastically without attributable code/test differences.

Do not allow a broad run to continue merely to collect more invalid rows.

## Current Default Opinion

The broad campaign has produced useful evidence, but the next task should not
immediately launch another days-long full run.

The highest-value continuation is:

1. 🟩 [x] fix dataset/build/campaign provenance and resume identity;
2. 🟩 [x] finish concurrent-batch latency/conflict attribution and implement
   action-scoped admission/cancellation;
3. 🟨 [~] run a current 250,000-item concurrency cohort and close its exact
   reopen/locality gates (the cohort is current; the two gates remain open);
4. ⬜ [ ] migrate the old canonical mixed-age CSV if it is still needed;
5. 🟩 [x] refresh the broad non-concurrent rows from the current binary;
6. 🟩 [x] regenerate the human report from provenance-valid current
   non-concurrent facts.

The current focused concurrency correctness work is credible and important.
The old broad canonical HTML remains useful for historical comparison only.
Use the 2026-07-17 non-concurrent campaign below for current broad
non-concurrent ShapeBench evidence.

## 2026-07-17 Concurrent-Write Admission Hardening

🟩 [x] The low-friction write surface now uses one bounded, FIFO, session-scoped
admission scheduler. The default concurrent-batch limit is derived from the
logical processor count (`(ProcessorCount + 1) / 2`, eight on this host), while
`MaxConcurrentWriters`, `MaxQueuedWriters`, `QueueTimeout`, and
`MaxActionItems` remain configurable. Direct queued-writer operations,
action-scoped operations, synchronous `InsertAll`, and asynchronous action
entry all use the same admission path; no public queued-writer bypass remains.

🟩 [x] Cancellation, timeout, rejection, action rotation, mixed-limit FIFO
fairness, and direct-writer admission are covered by
`concurrency-admission-sanity`. Same-shelf conflicts no longer use an unbounded
`SpinWait`: waiters park on shelf-targeted completion signals. The rejected
alternatives were one global `Monitor.PulseAll` (unrelated thundering-herd
wakeups) and adaptive spinning (bounded but deliberate CPU burn).

🟩 [x] Memory-backed logical range readers retain a copy-on-write arena
snapshot. Snapshot activation is scoped to concrete cursor calls, so a cursor
keeps a coherent view across concurrent publications without making unrelated
same-thread queries observe stale bytes. The broad public-surface mutation
test proved delete, rekey, delete-remaining, and delete-all behavior while a
cursor remains open. File-backed readers retain the existing per-raw-read
storage gate; a file-wide cursor snapshot is not claimed by this change.

🟩 [x] Accepted write campaign:
`artifacts/shape-bench/current-concurrency-targeted-250k-20260717-0121/`.
It contains `117` unique schema-v2 rows at `250,000` items, one campaign ID,
one ShapeBench binary hash, no stderr, no duplicate resume keys, no result-count
mismatch, and no retained `work` directory. All admission cancel, timeout, and
reject totals are zero. Exact reopen tuple-map proof remains present on the
same `48` SS8-8 rows; the existing `69`-row cross-shape exact-map coverage gap
is unchanged and must not be described as closed. That gap is a benchmark-oracle
coverage limit, not a product defect by itself: LibraDex's concurrent-write
contract is storage integrity and valid final state, while caller-created
logical conflicts such as overlapping writes to the same key remain
application-owned unless a future explicit coordination API says otherwise.

🟩 [x] The HTML now includes `Write Admission And Shelf Wait`, reporting queued
and granted entries, maximum pending depth, cancellation/timeout/rejection,
average/maximum admission wait, and shelf wait count/time for each row.

### Accepted Interpretation

- LibraDex concurrent writes are for real-world threaded application usage:
  bounded admission, cancellation, fairness, and coherent storage publication.
  They are not a promise that overlapping-key callers get database-style
  transaction semantics or deterministic logical conflict arbitration;
- queued-writer matched-control medians are `0.911-0.961x` across locality and
  8/16-thread plans; this is a good real-world result because independent
  callers retain roughly 91-96% of the equivalent one-thread action rate while
  gaining bounded waiting, cancellation, and fairness;
- disjoint concurrent batches are `1.028x` at eight workers and `1.124x` at
  sixteen workers versus their plan-matched controls;
- mixed batches are `0.426x`/`0.434x`, and overlap batches are
  `0.546x`/`0.512x`, at eight/sixteen workers respectively. This is expected
  contention cost, not a reason to promise bulk-insert scaling. For intentional
  key overlap, the relevant product checks are valid whole entries, coherent
  indexes/storage, clean reopen, and a final state corresponding to accepted
  writes; exact preservation of every planned overlapping tuple is not the
  public contract;
- sixteen-worker batch plans queue above the host-derived limit with maximum
  pending depth `8`; eight-worker batch plans do not enter the session queue;
- against `current-concurrency-action-250k-20260716-2042`, queued-writer
  threaded throughput is `1.009x` median and controls `1.034x`. Threaded batch
  throughput is `0.655x` median while controls are `1.030x`; the accepted
  tradeoff is removal of spin and unrelated wakeups, not a universal batch
  throughput win.

### Verification

- Release/x64 Harness and ShapeBench rebuilds completed with zero warnings and
  zero errors;
- `concurrency-admission-sanity`: 30 repetitions on the first notification
  branch, 30 after targeted signaling, and 20 after cursor snapshot scoping;
- `concurrency-contract-sanity`: 5, 5, and 3 corresponding repetitions;
- `public-surface-api-sanity` passes after the scoped-snapshot correction;
- `public-api-snapshot` was reviewed, updated, and passes;
- focused targeted-signal A/B:
  `artifacts/shape-bench/targeted-signal-batch-250k-20260717-0113/`, `18/18`
  rows, `0.972x` median versus global `PulseAll`, zero parity/queue-health
  failures. Targeted signaling was accepted for deterministic wake efficiency,
  not as a throughput optimization.

## 2026-07-17 Non-Concurrent ShapeBench Refresh

🟩 [x] Accepted non-concurrent campaign:
`artifacts/shape-bench/current-nonconcurrent-250k-20260717-0248/`.
It contains `972` unique schema-v2 rows at `250,000` items, one campaign ID,
one ShapeBench binary hash, `14` workloads, `11` shapes, and zero concurrent
rows. The campaign completed with no result-cardinality mismatch, no retained
`work` directory, and no error/guard strings in the workload logs.

🟩 [x] The current non-concurrent HTML report is:
`artifacts/shape-bench/current-nonconcurrent-250k-20260717-0248/report.html`.
The CSV is:
`artifacts/shape-bench/current-nonconcurrent-250k-20260717-0248/results.csv`.

### Accepted Interpretation

- overall median LibraDex/SQLite operation ratio is `1.242x`;
- write workload median is `3.674x`;
- count workload median is `1.686x`;
- read workload median is `0.985x`;
- workload medians: `insert-random-batch 5.990x`,
  `insert-sorted-batch 3.220x`, `count-all-api 2.820x`,
  `insert-random-direct 1.910x`, `prefix-keys 1.600x`,
  `prefix-pairs 1.590x`, `prefix-identities 1.320x`,
  `count-prefix-api 1.110x`, `range-pairs 1.060x`,
  `range-keys 0.940x`, `range-identities 0.920x`,
  `lookup-one-identities 0.890x`, `lookup-list-identities 0.760x`,
  and `count-range-api 0.710x`;
- shape medians: `VV 7.250x`, `VS8 3.540x`, `VS16 3.450x`,
  `SS8-8 1.800x`, `SS16-8 1.130x`, `SS16-16 0.940x`,
  `SS8-16 0.880x`, `FS32-8 0.770x`, `SV8 0.690x`,
  `FS32-16 0.550x`, and `SV16 0.170x`.

### Verification

- backed up `150` changed source/config/project files into `.code-history`;
- deleted `5,393` old `.code-history` files older than the required cutoff;
- cleaned targeted `bin`/`obj` folders for `LibraDex` and `LibraDex.ShapeBench`;
- rebuilt `LibraDex.ShapeBench` Release/x64 with MSBuild 18 and `/restore`;
- generated `972/972` expected non-concurrent scenarios from `ShapeBench list`;
- ran workload-by-workload with `--resume`, serial child isolation, and
  `--campaign-id current-nonconcurrent-250k-20260717-0248`;
- final campaign payload is `2.93 MiB`; total `artifacts/` size is `577.30 MiB`.

## 2026-07-18 SV16 Session Read Cache: Unlimited Default And Exact Per-Index Limit

🟩 [x] The accepted design leaves `DataKernel` unchanged as the raw
byte-storage and router layer. Immutable decoded `SV16` shelf retention belongs
to `LibraDexFileSession`, so reads and route mutations use one session strategy
instead of a second index-local routing strategy. Entries remain keyed by
physical shelf offset, with retained-byte accounting and optional limits keyed
by the owning physical index-root offset.

🟩 [x] The per-index runtime default is `0`, meaning no retained-byte
limit and no automatic pressure trim. A developer may supply a positive
`readCacheMaxBytes` value independently when creating or opening each `SV16`
index. That value is an exact ceiling policy: eviction begins only after the
index exceeds it. The option is runtime/session policy and is not persisted in
the file.

🟩 [x] Each cache entry owns one immutable shelf byte array plus decoded
record-offset and key-prefix sidecars. Cache hits avoid the admission/eviction
lock. Mutable shelf paths receive a separate writable image; publication
removes the affected physical shelf, and structural invalidation advances the
cache generation so an earlier raw read cannot repopulate stale bytes.

🟩 [x] The earlier `64 MiB` implementation coupled its nominal ceiling to
a hidden runtime-memory-pressure rule. Every periodic admission could reduce
the effective target to one quarter of the configured value. On this host,
that made `64 MiB` behave like a `16 MiB` pressure target against a `22.197
MiB` working set, evicting shelves and recreating repeated file reads. A same-
binary attribution row measured `8,301.815 ops/s` and retained only `282`
shelves / `19.321 MiB`. This disproves the earlier conclusion that the 7-9K
rows were merely unavoidable cold first-touch cost.

🟩 [x] The hidden pressure reinterpretation was removed. A corrected
same-binary batch-`1,000`, 8-thread A/B measured `34,094.783 ops/s` at default
`0` and `37,279.853 ops/s` with an explicit `64 MiB` ceiling. Both returned
exactly `160,000` identities and retained the identical `324` shelves /
`22.197 MiB`. The 9.3% ordering is ordinary single-run variance; the proof is
that `64 MiB`, when treated as an actual ceiling, does not affect this smaller
working set.

🟩 [x] Current process-isolated default-`0` ShapeBench proof is under
`artifacts/shape-bench/sv16-unlimited-default-exact-limit-final-250k-20260718/`.
All six rows share one final binary hash and one campaign ID,
returned exactly `160,000` identities, reported `per-index limit=unlimited`,
retained `324` shelves / `22.197 MiB`, and left no `work` directory:

| batch | threads | LibraDex ops/s | SQLite ops/s | ratio | versus hidden-pressure campaign |
|---:|---:|---:|---:|---:|---:|
| 250 | 8 | 39,089.496 | 391.994 | 99.72x | 4.21x |
| 250 | 16 | 31,688.931 | 359.748 | 88.09x | 4.47x |
| 1,000 | 8 | 40,302.429 | 369.031 | 109.21x | 4.22x |
| 1,000 | 16 | 35,754.356 | 355.531 | 100.57x | 5.02x |
| 5,000 | 8 | 38,091.436 | 412.481 | 92.35x | 4.06x |
| 5,000 | 16 | 35,770.624 | 413.514 | 86.50x | 4.26x |

🟩 [x] A final-binary diagnostic untimed same-session pass measured
`45,105.389 ops/s` at 8 threads and `55,392.824 ops/s` at 16 threads for batch
`1,000`.
Both rows returned `160,000` identities and retained the same complete `324`
shelves / `22.197 MiB`. These remain hot-cache diagnostics, not replacements
for the process-isolated six-row campaign.

🟩 [x] Focused correctness proof passes for both policies.
`sv16-read-cache-sanity` retained three `65,664`-byte entries under default
unlimited mode, while a `196,991`-byte explicit limit evicted to two entries /
`131,328` bytes and rejected stale-generation admission. Public `SV16`
create/open/create-or-open coverage passed with default `0` and explicit
`196,991`; routed insert/range/exact-delete/range-delete proof passed with
`1,400` items, `32` duplicate groups, `132` range rows, and `131` range
deletes after one exact delete.

🟨 [~] Scope boundary: default `0` deliberately does not provide automatic
low-memory eviction. A positive developer-selected per-index limit is the
current shelf-cache bound. The existing router arena remains a separate `64
MiB` mechanism, and older router-page, decoded-router-view, and other
shape-specific session caches are not unified under one total process policy.
Do not describe total LibraDex process memory as capped by this slice alone.

## 2026-07-18 SV8 Session Shelf Cache And Benchmark-Parity Contract

🟩 [x] The accepted `SV16` session-owned immutable decoded-shelf cache was
generalized by shelf type and applied to `SV8`. `SV8` still walks the existing
router topology for every lookup. Only after routing resolves a physical shelf
offset does the session reuse that shelf's immutable bytes and decoded slot
sidecars. There is no key-to-shelf pointer table and no competing route model.

🟩 [x] `SV8` now exposes the same per-index runtime `readCacheMaxBytes` policy:
`0` is the no-limit default and a positive value is an exact ceiling for that
physical index. Shelf publication removes the affected cache entry; structural
publication clears the session projections and advances the generation so a
pre-invalidation read cannot admit stale bytes afterward. Active durability
batches bypass immutable caching so mutable staged bytes remain authoritative.

🟩 [x] Unlimited indexes no longer write an approximate-LRU timestamp on every
cache hit. LRU recency exists only when a developer configures a positive
ceiling, because unlimited retention cannot evict and gains nothing from a
shared timestamp write. Reconfiguring an open index updates recency tracking
for its existing entries under the cache admission lock.

🟩 [x] ShapeBench now enforces two separate claims. `run-all` is the
LibraDex-versus-SQLite parity campaign and only permits T1. Explicit threaded
requests fail with direction to `run-scaling`. `run-scaling` measures only
LibraDex reads at T1/T8/T16, repeats each cell in an isolated child process,
checks stable result cardinality, and reports aggregate/T1 plus parallel
efficiency; it does not emit or imply a SQLite ratio.

🟩 [x] Final process-isolated `SV8 lookup-list-identities`, batch `1,000`,
250,000-item proof is under
`artifacts/shape-bench/sv8-session-cache-final-scaling-250k-20260718/`.
Every cell has three repetitions, exactly `160,000` results, one final binary
hash (`E952C158F1BA...`), and no retained `work` directory:

| threads | before median ops/s | final median ops/s | final / before | final / T1 | spread |
|---:|---:|---:|---:|---:|---:|
| 1 | 440.283 | 7,728.613 | 17.554x | 1.000x | 7.156% |
| 8 | 605.029 | 4,502.235 | 7.441x | 0.583x | 17.374% |
| 16 | 693.379 | 4,333.381 | 6.250x | 0.561x | 2.741% |

🟨 [~] Decision evidence: the cache is a large absolute improvement at every
thread count, but this workload is now fastest at T1. Once repeat file reads
and repeated slot decoding disappear, its finite lookup work no longer
amortizes thread scheduling, work partitioning, and shared cache-map access.
Do not add a second key-pointer accelerator merely to manufacture scaling.
Keep T1 as the recommended mode for this workload unless a larger caller
workload proves that parallel dispatch amortizes again.

🟩 [x] Correctness and contract proof passed: `sv8-read-cache-sanity` and the
existing `sv16-read-cache-sanity` both proved bounded eviction, unlimited
default retention, and stale-generation rejection. `sv8-index-api-sanity`
passed with 1,400 rows and then warmed, exactly deleted, range-deleted, and
observed zero stale rows. A T1-only LibraDex/SQLite smoke returned identical
32,000 result items at `9,614.035` versus `452.571 ops/s`; a T8 `run-all`
request was rejected before measurement.

## 2026-07-19 FS32 Immutable Shelf Cache And Focused Read Rerun

🟩 [x] The accepted session-owned immutable shelf-cache design was generalized
without adding a second routing model and applied to `FS32-8` and `FS32-16`.
Each range reader still follows the existing FS32 router topology. Once routing
resolves a physical shelf offset, the session can reuse that immutable shelf
image. Mutation publication removes the affected shelf; structural publication
clears the projections; positive per-index limits retain the existing LRU
policy, while the default `0` remains unlimited.

🟩 [x] Public API correctness proof passed for both shapes. The generic-index
sanity lane proved default-unlimited admission, post-cache insert invalidation,
and visibility of the newly inserted tuple on the next range read. Release
builds of ShapeBench and the Harness passed.

🟩 [x] Focused process-isolated T1 reruns completed all `66 / 66` FS32 cells
(`33` per shape), each with three repetitions and exact LibraDex/SQLite result
cardinality parity. No generated work database remains. Compact evidence is
under `artifacts/shape-bench/fs32-read-cache-rerun-20260719/`.

| shape | batch | old LibraDex range-keys ops/s | current LibraDex ops/s | LibraDex gain | current SQLite ops/s | current ratio |
|---|---:|---:|---:|---:|---:|---:|
| FS32-8 | 250 | 15,575.525 | 27,107.314 | 74.0% | 27,283.548 | 0.994x |
| FS32-8 | 1,000 | 15,688.334 | 27,311.817 | 74.1% | 27,011.118 | 1.011x |
| FS32-8 | 5,000 | 16,154.215 | 24,500.561 | 51.7% | 24,201.158 | 1.012x |
| FS32-16 | 250 | 11,494.008 | 16,684.273 | 45.2% | 25,443.671 | 0.656x |
| FS32-16 | 1,000 | 11,373.467 | 14,888.427 | 30.9% | 24,466.766 | 0.609x |
| FS32-16 | 5,000 | 11,959.607 | 15,219.573 | 27.3% | 24,826.823 | 0.613x |

🟨 [~] The canonical CSV/HTML was refreshed by replacing its `66` FS32 rows
with this focused evidence while preserving all other rows and ordinals. It is
still an incomplete `173 / 291` report and now deliberately contains mixed
measurement times and binary hashes. The original CSV and HTML were retained
beside it as `*.pre-fs32-read-cache-20260719.*`. Use each row's provenance
columns; do not describe the merged report as one uninterrupted run.

🟨 [~] Interpretation: all six LibraDex `range-keys` medians improved, so the
design-path benefit is validated. `FS32-8` now reaches current SQLite parity;
`FS32-16` remains `0.609x` to `0.656x` and is the next diagnostic target. The
focused SQLite medians also moved materially versus the older campaign, so use
the current in-cell ratio for present parity and the LibraDex-only old/current
comparison for implementation attribution.

## 2026-07-19 FS32 Promoted-Router Range BLX

🟩 [x] CPU sampling isolated the remaining `FS32-16` range cost below
`Fixed32Scalar16RangeReader.LoadNextShelfRange`: the reader copied and reparsed
the same 4 KiB child-router page even after the session had already promoted
that authoritative direct router into its decoded view. The accepted path lets
both FS32 readers expand that existing session projection. It adds no route
table, key pointer, persisted metadata, or cache policy, and compressed routers
retain the prior persisted-page fallback.

🟩 [x] Post-change trace evidence reduced `Buffer._Memmove` exclusive samples
from `14.52%` to `0.85%` for the FS32-16 trace and removed
`ReadRouterPageUsingArenaCache` from the top 50 sampled methods. The shelf cache,
durable offsets, normal router invalidation, and mutation authority remain
unchanged.

🟥 [!] Rejected shelf-planning branch: adding a binary inclusive upper-bound
search did not improve the three-cell FS32-16 smoke median and destabilized an
FS32-8 sample. Approximately 32-row ShapeBench ranges make the existing short
linear boundary scan competitive, so the extra method was removed.

🟥 [!] Rejected attribution to public 32-byte key materialization. Before the
change, FS32-16 `range-identities`, `range-keys`, `range-pairs`, and
`count-range` clustered despite materially different output projection work;
the trace instead attributed the gap to range-frontier router expansion.

🟩 [x] Final process-isolated proof completed `66 / 66` cells, three repetitions
per engine/cell, exact result parity, one binary hash
`22D66944A249...A493`, and no retained work directory. FS32-16 improved in
`31 / 33` cells with a `23.1%` median cell improvement versus the cache-only
campaign. Five-repetition LibraDex-only controls measured FS32-16 `range-keys`
at `19,793.69`, `20,275.63`, and `19,083.05 ops/s` for batches `250`, `1,000`,
and `5,000`.

| shape | batch | cache-only range-keys ops/s | promoted-router ops/s | gain | current SQLite ops/s | current ratio |
|---|---:|---:|---:|---:|---:|---:|
| FS32-8 | 250 | 27,107.314 | 28,707.206 | 5.9% | 24,433.282 | 1.175x |
| FS32-8 | 1,000 | 27,311.817 | 26,179.384 | -4.1% | 25,965.276 | 1.008x |
| FS32-8 | 5,000 | 24,500.561 | 27,913.998 | 13.9% | 26,564.469 | 1.051x |
| FS32-16 | 250 | 16,684.273 | 17,193.835 | 3.1% | 25,077.678 | 0.686x |
| FS32-16 | 1,000 | 14,888.427 | 20,357.449 | 36.7% | 26,871.371 | 0.758x |
| FS32-16 | 5,000 | 15,219.573 | 17,544.824 | 15.3% | 23,406.643 | 0.750x |

🟨 [~] The fair three-repetition FS32-16 batch-250 and batch-5000 rows retained
`12.48%` and `25.00%` LibraDex spread. Use the five-repetition controls above
for direction/stability and the same-cell three-repetition ratios for current
SQLite parity. FS32-16 remains below SQLite, but the remaining gap is no longer
dominated by repeated router-page copying.

🟩 [x] The canonical `173 / 291` CSV/HTML now contains the final 66 FS32 rows.
The prior cache-only merged report is retained beside it as
`*.pre-fs32-promoted-router-20260719.*`; mixed-provenance warnings still apply
to the incomplete canonical report.

## 2026-07-19 FS32 Count-Only Routed Traversal

🟩 [x] ShapeBench already called the correct public counting contracts:
`Count()` for count-all and `Count(condition)` for range/prefix counts. The
remaining weak rows were exclusively `FS32-8` / `FS32-16` range and prefix
counts. Their public path still constructed a full range-reader plan even
though the dormant count-only route spine could return only a scalar.

🟩 [x] The accepted path walks the authoritative FS32 router topology, reuses
the session's existing promoted direct-router projection, groups shared target
runs, preserves router lower/upper edge context, and reuses immutable cached
shelf images. Every reached shelf still applies exact lower-bound plus inclusive
upper-bound key comparisons; no persisted total, key-to-shelf pointer map,
parallel count strategy, or write-amplifying metadata was added.

🟥 [!] Two candidate shortcuts were rejected before canonical merge. Treating
an unknown target offset as a router caused shelf validation failure, so the
final cache-only promoted-router probe performs no I/O or classification.
Treating a non-edge route target as a wholly contained shelf overcounted shared
physical shelf spans; the final path bounds every shelf exactly. A pre-promotion
full run was also slower than baseline and was discarded.

🟩 [x] Final process-isolated T1 proof completed all `12 / 12` targeted cells,
three repetitions per engine/cell, one binary hash
`13ED9E7EC66F...A2118`, exact result parity (`799,916` range and `319,984`
prefix), and no retained work database. Compact evidence is under
`artifacts/shape-bench/fs32-direct-count-v4-20260719/`.

| shape/workload | prior ratio range | final ratio range | LibraDex gain range |
|---|---:|---:|---:|
| FS32-8 count-range | 0.821x-0.858x | 1.763x-2.000x | 2.019x-2.220x |
| FS32-8 count-prefix | 0.927x-1.133x | 1.645x-1.648x | 1.364x-1.599x |
| FS32-16 count-range | 0.534x-0.670x | 1.338x-1.549x | 2.034x-2.785x |
| FS32-16 count-prefix | 0.852x-0.884x | 1.378x-1.517x | 1.316x-1.725x |

🟩 [x] The canonical `173 / 291` CSV/HTML contains the 12 final rows with
their original canonical IDs. The immediately prior report is retained as
`*.pre-fs32-direct-count-v4-20260719.*`. The report remains intentionally
mixed-provenance and incomplete; use row-level campaign/hash columns.

## 2026-07-19 FS32 Range Promoted-Target Fast Path

🟩 [x] BLX isolated the remaining FS32 range-reader cost to repeated target
classification after the first router visit. The accepted path asks the
session's existing promoted-direct-router projection first, then falls back to
the authoritative classifier only for an unknown target. This preserves the
normal router topology, durable offsets, session shelf cache, mutation
invalidation, and exact edge comparisons; it adds no key-to-shelf pointer map,
second routing plan, persisted metadata, or new unbounded cache.

🟥 [!] Parent-target kind propagation and a session-wide target-kind cache were
rejected. Both duplicate knowledge already represented by the promoted router
view and shelf cache, add pending-target or invalidation state, and can only
avoid the first classification that the accepted path intentionally retains.
A separate precomputed range scoop was deferred because the narrow fix removed
the measured bottleneck without creating another traversal strategy.

🟩 [x] Trace proof on `FS32-16 range-keys batch=250` reduced classifier time
inside range traversal from `1,261.091 ms` to `21.951 ms` (`98.26%`) and total
range traversal samples from `1,282.936 ms` to `519.464 ms` (`59.51%`). The
remaining approximately `4%` classifier/file-read share is legitimate
first-touch work.

🟩 [x] Final isolated T1 validation completed all `18 / 18` cells for
`FS32-8` / `FS32-16` x `range-identities` / `range-keys` / `range-pairs` x
batches `250` / `1,000` / `5,000`, with three processes per engine/cell,
one binary hash `4FC5C863FF08...CCDB1`, exact `799,916` result parity, and no
retained work database. LibraDex improved `1.426x-2.643x` versus the replaced
canonical rows (`1.978x` median). Evidence is under
`artifacts/shape-bench/fs32-range-promoted-first-final-20260719/`.

| shape | batch | prior range-keys ops/s | final LibraDex ops/s | LibraDex gain | focused SQLite ops/s | focused ratio |
|---|---:|---:|---:|---:|---:|---:|
| FS32-8 | 250 | 28,707.206 | 52,653.239 | 1.834x | 10,518.224 | 5.006x |
| FS32-8 | 1,000 | 26,179.384 | 50,351.646 | 1.923x | 11,807.320 | 4.264x |
| FS32-8 | 5,000 | 27,913.998 | 53,112.454 | 1.903x | 12,415.995 | 4.278x |
| FS32-16 | 250 | 17,193.835 | 45,439.327 | 2.643x | 9,832.710 | 4.621x |
| FS32-16 | 1,000 | 20,357.452 | 46,597.211 | 2.289x | 12,435.719 | 3.747x |
| FS32-16 | 5,000 | 17,544.824 | 41,860.922 | 2.386x | 11,547.065 | 3.625x |

🟨 [~] Focused process variance was material: maximum LibraDex spread was
`56.964%` and maximum SQLite spread was `51.400%`. Focused SQLite medians
(`9.2K-12.9K ops/s`) were also below the older canonical samples. Attribute
the implementation gain to same-workload LibraDex before/after throughput and
the trace, not to the larger focused SQLite ratios.

🟩 [x] Five-process LibraDex-only `FS32-16 range-keys` controls confirmed the
gain at T1 (`37,577.756-44,193.035 ops/s`, `2.03x-2.57x` over the prior rows).
T8 scaled `2.63x-2.90x` over matching T1 controls and T16 scaled
`3.02x-3.26x`; these threaded results are intentionally compared only with
LibraDex T1, not SQLite. Generic API sanity, cached-after-mutation behavior,
and close/reopen validation passed.

🟩 [x] The canonical `173 / 291` CSV/HTML now contains the 18 focused rows at
their original IDs. Backups are retained as
`*.pre-fs32-range-promoted-first-20260719.*`. The report is refreshed and its
interactive sort, resize, and hide-column controls remain active. It is still
an incomplete mixed-provenance report; use CSV campaign/hash fields when
auditing provenance.

## 2026-07-19 Remaining Sub-Par Prefix Retest

🟩 [x] After the promoted-target range-reader fix, only four completed
canonical T1 rows remained below SQLite: `FS32-16 prefix-identities b5000`,
`FS32-8 prefix-keys b5000`, and `FS32-16 prefix-keys b1000/b5000`. All four
were rerun serially with three isolated processes per engine on binary hash
`4FC5C863FF08...CCDB1`; every row returned exact `319,984` parity and no work
database was retained.

| ID | scenario | LibraDex ops/s | SQLite ops/s | ratio | LibraDex spread | SQLite spread |
|---:|---|---:|---:|---:|---:|---:|
| 112 | FS32-8 prefix-keys b5000 | 39,892.116 | 10,947.225 | 3.644x | 49.610% | 32.618% |
| 134 | FS32-16 prefix-keys b1000 | 31,901.977 | 11,192.265 | 2.850x | 19.963% | 25.427% |
| 143 | FS32-16 prefix-identities b5000 | 35,098.199 | 8,361.793 | 4.197x | 23.357% | 45.414% |
| 145 | FS32-16 prefix-keys b5000 | 43,379.187 | 11,491.146 | 3.775x | 9.096% | 21.283% |

🟨 [~] All four LibraDex medians improved versus their replaced rows, which is
directionally consistent with the range-reader fix. SQLite again measured far
below the older canonical samples and process spread was high, so these ratios
are focused same-run observations rather than a stable new SQLite baseline.

🟩 [x] The four validated rows replaced canonical IDs `112`, `134`, `143`, and
`145`; backups are retained as
`*.pre-remaining-poor-prefix-retest-20260719.*`. The refreshed interactive
canonical report remains `173 / 291`, has `173` unique keys, and currently has
zero completed rows below `1.0x`.

## 2026-07-19 Canonical Warm-Session Methodology Reset

🟥 [!] The earlier `run-reads` contract was rejected as the canonical read
comparison. It launched a fresh child for every repetition and measured the
first workload pass, so its aggregate mixed cold session/connection state with
warm routing and B-tree behavior. Those rows retain diagnostic value but are
not the intended steady-state comparison.

🟩 [x] The corrected contract launches one isolated process per engine/cell,
builds the corpus through a separate closed builder, opens one fresh
measurement catalog/session or SQLite connection, discards one complete cold
pass, and records three complete warm passes in that same open session. Every
pass creates and disposes fresh transient LibraDex query/reader state or one
SQLite command; SQLite rebinds that pass-owned command within the pass and
finalizes it at the pass boundary.

🟩 [x] Microsoft.Data.Sqlite pooling is explicitly disabled, preventing a
nominally fresh measurement connection from inheriting a pooled native handle.
SQLite continues to force the covering `ix_k_id(k,id)` index, so retrieval does
not require a table lookup. Connection-local page/schema cache survives warm
passes; prepared command state does not.

🟩 [x] The new CSV/report uses warm arithmetic mean as the primary engine rate
and ratio, while retaining warm median, minimum, maximum, spread, and the
discarded cold rate. The HTML states the lifecycle contract and retains sort,
resize, hide-column, incremental flush, and 60-second refresh behavior.

🟩 [x] A four-path diagnostic proof passed exact parity for `SS8-8
range-identities`, `FS32-8 prefix-keys`, `SS8-8 count-all-api`, and `SS8-8
lookup-list-identities`. Evidence is under
`artifacts/shape-bench/warm-session-methodology-smoke-20260719/`; no work
database remains.

🟨 [~] The visible canonical root was reset from `173 / 291` to `0 / 291`; the
prior CSV/HTML are preserved as `*.pre-warm-session-reset-20260719.*`. Campaign
`canonical-read-warm-t1-250k-3rep-20260719` is running serially from `SS8-8`
with binary hash `BE6F244D97C5...B7DA`. The existing 15-minute heartbeat now
monitors this contract and pauses on completion or failure.
