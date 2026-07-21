# LibraDex Performance Redo Checklist

Start here for the latest current matrices, drift matrices, blockers, exact commands, and restart context: `docs/performance-redo-start-here.md`.

## Purpose

Redo the LibraDex performance evidence after the concurrency, grouping, and fixed-N work, including SQLite comparison rows that treat SQLite as an index structure rather than as a source database.

## Ground Rules

- 🟩 [x] SQLite comparison schemas should remain index-shaped: key plus identity only, preferably `PRIMARY KEY(k, i) WITHOUT ROWID`.
- 🟩 [x] SQLite comparison rows should report journal mode, synchronous mode, storage bytes including sidecars, and explicit batch/transaction cadence.
- 🟩 [x] LibraDex is measured as an identity index. The source data lives outside LibraDex.
- 🟩 [x] File-backed durability and memory-backed staging are separate result categories.
- 🟩 [x] Primary performance rows are batch-first. Single-item or tiny-operation rows are diagnostics/smoke rows unless they represent a high-fanout read with meaningful returned identity volume.
- 🟩 [x] SQLite transaction cadence must match LibraDex publish/commit cadence for parity rows. If LibraDex publishes every `X` items, SQLite commits every `X` items.
- 🟩 [x] Primary batch tiers are `256` for small/low-RAM batches, `1024` for standard batches, `4096` for sustained-throughput batches, and full-batch for known fixed units.
- 🟩 [x] Read rows should prefer rotating or representative windows over one repeated hot range unless the row is explicitly labeled as hot-window diagnostic evidence.
- 🟨 [~] Existing historical numbers in `LIBRADEX_DESIGN_CHECKLIST.md` are useful for broad context, but they are not a substitute for a current regenerated artifact set.
- 🟨 [~] Existing `docs/performance-redo-start-here.md` C-series rows are now pre-policy snapshots. New current rows should use the B-series batched baseline matrix until the full matrix is regenerated.

## Current Harness Inventory

- 🟩 [x] Existing shape parity reports:
  - `all-shape-write-parity`
  - `all-shape-read-range-sweep`
  - `fs32-8-read-parity`
  - `fs32-16-read-parity`
- 🟩 [x] Existing SQLite fixed-shape reports:
  - `sqlite-ss8-8-comparison`
  - `sqlite-ss16-16-comparison`
- 🟩 [x] Existing routed varlen SQLite comparisons:
  - `vs8-routed-sqlite-comparison`
  - `vs16-routed-sqlite-comparison`
  - `sv8-routed-sqlite-comparison`
  - `sv16-routed-sqlite-comparison`
  - `vv-routed-sqlite-comparison`
- 🟩 [x] Existing concurrency proof:
  - `concurrency-performance-proof`
  - `fixedn-concurrency-performance-proof`
  - `fixedn-batch-coalescing-proof`
- 🟩 [x] Existing grouping/HPC proof:
  - `group-by-execution-proof`
  - `group-by-direct-target-proof`
  - `group-by-composite-proof`
  - `group-by-string-proof`
  - `group-by-string-key-state-promotion-proof`
  - `scalar16-key-state-promotion-proof`
  - `fixedn-key-state-promotion-proof`
  - `group-by-aggregate-proof`
  - `group-by-row-reader-proof`

## Current Result Gap

- 🟩 [x] Main fixed-shape write, read-sweep, and SQLite current reports have been regenerated for the June campaign; use `docs/performance-redo-start-here.md` for historical matrices.
- 🟩 [x] Current broad non-concurrent ShapeBench rows were regenerated on 2026-07-17 with schema-v2 provenance: `artifacts/shape-bench/current-nonconcurrent-250k-20260717-0248/report.html`.
- 🟨 [~] `concurrency-performance-proof` has CSV/markdown artifacts under `artifacts/`, but no SQLite concurrency analogue exists yet.
- 🟨 [~] Fixed-N batch and concurrency proofs print measured rows but do not yet write rotating CSV/markdown reports.
- 🟥 [!] SQLite mutation/concurrency comparison is the largest missing competitor guardrail: no current index-only SQLite rows for multi-thread insert, delete, rekey, or mixed mutation pressure.

## Phase 1: Build And Smoke

- 🟩 [x] Back up changed source/config/project files into `.code-history` before build.
- 🟩 [x] Rebuild `LibraDex.Harness` Release x64.
- 🟩 [x] Run `concurrency-contract-sanity`.
- 🟩 [x] Run `concurrency-workload-matrix`.
- 🟩 [x] Run a small `concurrency-performance-proof` smoke with CSV/markdown output.

### Phase 1 Results

- 🟩 [x] Backed up `120` changed source/config/project files to `.code-history`.
- 🟩 [x] Initial clean rebuild without restore failed before compile because `obj/project.assets.json` had been deleted; reran with `/restore`.
- 🟩 [x] Release x64 rebuild passed. Build log: `artifacts/build-harness-release-x64-performance-redo-restore-20260628.log`.
- 🟩 [x] `concurrency-contract-sanity` passed.
- 🟩 [x] `concurrency-workload-matrix` passed.
- 🟩 [x] `concurrency-performance-proof --single-ops 128 --ops-per-thread 32 --max-threads 4 --include-file true` passed and wrote:
  - `artifacts/performance-redo-20260628/concurrency-performance-smoke.csv`
  - `artifacts/performance-redo-20260628/concurrency-performance-smoke.md`

## Phase 2: Current Core Reports

- 🟩 [x] Run `all-shape-write-parity --batches 100 --warmup-batches 5 --items-per-batch 1024 --prefix-count 16 --repeat-count 3 --journal-mode wal --synchronous normal`.
- 🟩 [x] Run `all-shape-read-range-sweep --batches 100 --items-per-batch 1024 --prefix-count 16 --iterations 100 --repeat-count 3 --journal-mode wal --synchronous normal`.
- 🟩 [x] Run `sqlite-ss8-8-comparison --batches 100 --warmup-batches 5 --items-per-batch 1024 --prefix-count 16 --iterations 100 --repeat-count 3 --journal-mode wal --synchronous normal`.
- 🟩 [x] Run `sqlite-ss16-16-comparison --batches 100 --warmup-batches 5 --items-per-batch 1024 --prefix-count 16 --iterations 100 --repeat-count 3 --journal-mode wal --synchronous normal`.

### Phase 2 Results

- 🟩 [x] `all-shape-write-parity` passed and wrote `artifacts/perf-runs/all-shape-write-parity-current.md`.
- 🟩 [x] 2026-06-29 all-shape default40 write run superseded the old widened-shape red rows and the legacy `64KiB` FS32 profile cliff rows. `SS8-8`, `SS16-8`, `SS8-16`, `SS16-16`, `FS32-8`, and `FS32-16` are all ahead of SQLite on sorted and random writes with the current no-override defaults.
- 🟩 [x] Superseded default40 write summary: `FS32-8` was sorted `485.73K/sec` / random `563.51K/sec` and `1.77x` / `1.66x SQLite`; `FS32-16` was sorted `588.58K/sec` / random `570.73K/sec` and `1.68x` / `1.63x SQLite`.
- 🟩 [x] 2026-06-29 `FS32-8` size-sweep isolated the write weakness to the legacy `Default64KiB` shelf profile rather than fixed-32 keys in general: `40KiB` shelves measured sorted `723K items/sec` / random `608K items/sec` at `724.48 B/item`, while `64KiB` shelves measured sorted `135K` / random `131K` at `1089.28 B/item`.
- 🟩 [x] Added and ran matching `FS32-16` size sweep: `40KiB` shelves measured sorted `530K items/sec` / random `449K items/sec` at `724.48 B/item`, while `64KiB` shelves measured sorted `123K` / random `124K` at `1111.04 B/item`.
- 🟨 [~] `fs32-8-size-sweep` and `fs32-16-size-sweep` are LibraDex-only shelf-profile tuning probes. They intentionally do not compare SQLite because SQLite has no LibraDex shelf extent variable; after a candidate shelf size is selected, `all-shape-write-parity` and read-parity commands provide the SQLite comparison.
- 🟨 [~] Standalone routed-bulk profile sweeps for `SS8-8`, `SS16-8`, `SS8-16`, and `SS16-16` also show a 64KiB cliff, but their absolute SS throughput does not match the all-shape command; all-shape parity is the authoritative SQLite comparison until that command-path variance is fully explained.
- 🟨 [~] Ran common-size all-shape write sweeps at `16KiB`, `24KiB`, `32KiB`, `48KiB`, and `64KiB`, plus a `40KiB` probe for wider shapes. The `64KiB` write cliff reproduces across every fixed scalar shape in the all-shape parity command.
- 🟩 [x] Read-leaning default policy selected: keep `SS8-8=32KiB`, `SS16-8=32KiB`, and `SS8-16=32KiB`; move only `SS16-16` to `24KiB`; keep `FS32-8` and `FS32-16` at `40KiB`.
- 🟨 [~] A mixed-candidate rerun kept every shape above SQLite but showed repeat drift: `SS16-16` improved strongly at `24KiB`, while `SS16-8` sorted at `24KiB` dropped from `1.11M/sec` in the all-24KiB sweep to `574K/sec`. Do not change additional defaults without repeat confirmation.
- 🟩 [x] Added shelf-size profile options to `all-shape-read-range-sweep`, rebuilt, and ran common read sweeps at `16KiB`, `24KiB`, and `32KiB`, plus a mixed read candidate with FS32 at `40KiB`.
- 🟨 [~] Read-side candidates differ from write-side candidates: `SS16-8`, `SS8-16`, and `FS32-8` read best at `32KiB`; `SS16-16` reads best at `24KiB`; `FS32-16` reads best in the mixed `40KiB` run. The attempted all-`40KiB` read sweep stopped because `SS8-8` does not support a `40KiB` shelf extent.
- 🟨 [~] Repeated the mixed write/read candidate. The write repeat recovered `SS16-8` sorted to `1.01M/sec`, suggesting the previous `574K/sec` was outlier noise. The read repeat kept every shape well above SQLite, but full-range read throughput still drifted enough that defaults should be selected as balanced tradeoffs, not single-row peaks.
- 🟩 [x] Third mixed-candidate write run confirmed the SS `24KiB` write candidates: `SS16-8` sorted/random `1.09M`/`961K`, `SS8-16` `1.13M`/`1.02M`, and `SS16-16` `981K`/`850K`.
- 🟩 [x] Implemented `SS16-16 Default24KiB`, updated the runtime/view/all-shape harness defaults, rebuilt, and reran no-override all-shape write/read parity.
- 🟩 [x] Current read-leaning default write summary: `SS8-8` sorted/random `366.83K`/`714.78K`, `SS16-8` `1.07M`/`893.36K`, `SS8-16` `861.07K`/`1.06M`, `SS16-16` `901.90K`/`855.24K`, `FS32-8` `600.15K`/`621.40K`, and `FS32-16` `539.63K`/`481.52K`; every row remains above SQLite.
- 🟥 [!] Pre-fix `all-shape-read-range-sweep` failed on `SS16-8 prefix 0`: expected `6400`, actual `1259`.
- 🟩 [x] Root cause isolated and fixed: the widened/fixed-shape cached bulk writer staged a full batch-local shelf image into the active durability batch, then performed a structural split; the outer batch commit could later overwrite the split/router publication at the same offset with stale shelf bytes.
- 🟩 [x] Current `all-shape-read-range-sweep` passed and wrote `artifacts/perf-runs/all-shape-read-range-sweep-current.md`.
- 🟩 [x] Post-`VS16` final regression `all-shape-read-range-sweep` passed, confirming the earlier `SS16-8 prefix 0` blocker remains fixed.
- 🟩 [x] 2026-06-29 original-size isolation rerun did not reproduce the suspicious `SS16-8` `24.11M ids/sec` full-range row; current `SS16-8` full-range is `43.73M ids/sec` with the same `5.31 MB/range`.
- 🟩 [x] Current read-leaning default read-sweep summary for full `prefix 0-15`: `SS8-8` `60.79M ids/sec` / `14.39x SQLite`, `SS16-8` `37.09M ids/sec` / `10.32x SQLite`, `SS8-16` `22.89M ids/sec` / `10.05x SQLite`, `SS16-16` `22.75M ids/sec` / `11.12x SQLite`, `FS32-8` `32.77M ids/sec` / `10.80x SQLite`, `FS32-16` `28.11M ids/sec` / `11.36x SQLite`.
- 🟩 [x] 2026-07-14 public sorted-batch fast-path tranche accepted: typed active-batch dispatch removes the ordinary `Guid` boxing and manager/guard forwarding frames, insert statistics publish once per durability batch, and all six fixed-scalar normalization paths skip compaction allocation when no tombstones exist. Isolated one-million-row ShapeBench medians improved from `609,566` to `635,704/sec` for `SS16-8`, `647,605` to `816,556/sec` for `SS8-16`, and `496,683` to `552,827/sec` for `SS16-16`. The `SS16-16` GC trace fell from `1.315 GB` to `1.004 GB` sampled allocation, boxed `Guid` allocation fell from `67.8 MB` to zero, and Gen0/Gen1/Gen2 collections fell from `106/49/8` to `87/31/7`; traces are `artifacts/ss16-16-fast-batch-cpu-20260714.nettrace` and `artifacts/ss16-16-fast-batch-gc-20260714.nettrace`.
- 🟩 [x] No-override default40 FS32 read-parity rows passed: `FS32-8` ranged from `12.79M` to `29.59M ids/sec` and `2.49x` to `6.24x SQLite`; `FS32-16` ranged from `8.64M` to `35.15M ids/sec` and `2.97x` to `11.71x SQLite`.
- 🟨 [~] `SS16-8` remains slower than `SS8-8` by identity throughput, but the measured gap now mostly tracks physical bytes per returned identity: `SS16-8` reads `5.31 MB/range` for `102400` identities versus `SS8-8` `3.15 MB/range`, because `SS16-8` stores 24-byte key/identity tuples versus `SS8-8` 16-byte tuples.
- 🟩 [x] `sqlite-ss8-8-comparison` passed and wrote `artifacts/perf-runs/sqlite-ss8-8-comparison-current.md`.
- 🟩 [x] `sqlite-ss16-16-comparison` passed and wrote `artifacts/perf-runs/sqlite-ss16-16-comparison-current.md`.

## Phase 3: Varlen SQLite Comparison

- 🟩 [x] Run `vs8-routed-sqlite-comparison` with a moderate broad-fanout shape.
- 🟩 [x] Run `vs16-routed-sqlite-comparison` with the mirrored broad-fanout shape.
- 🟩 [x] Run `sv8-routed-sqlite-comparison` with natural and tuple-major order.
- 🟩 [x] Run `sv16-routed-sqlite-comparison` with natural and tuple-major order.
- 🟩 [x] Run `vv-routed-sqlite-comparison` with the current accepted write-intent settings.

### Phase 3 Results

- 🟨 [~] `VS8` moderate run: routed write was `1.30x` SQLite, routed read was `0.87x` SQLite, bulk build was `6.03x` SQLite write and `2.07x` SQLite read.
- 🟩 [x] `VS16` moderate run: routed write was `1.25x` SQLite, routed read was `1.89x` SQLite, bulk build was `7.08x` SQLite write and `3.33x` SQLite read.
- 🟥 [!] `SV8` natural duplicate-pressure run: routed write was `0.41x` SQLite and routed read was `0.26x` SQLite.
- 🟩 [x] 2026-06-30 `SV8` pruned-read fix corrected the dup256 tuple-major read over-scan: backing reads fell from `27,396` to `2,356`.
- 🟩 [x] 2026-06-30 `SV8` immediate shelf rewrite slot-flush fix corrected the routed sanity corruption. Accepted rebuild log: `artifacts/build-harness-release-x64-sv8-accepted-slotflush-rerun-20260630.log`; `sv8-routed-sanity` passed for duplicateModulo `256` and `1`.
- 🟩 [x] `SV8` tuple-major dup256 recovered to near parity after the pruned-read and slot-flush fixes: write `1.06x SQLite`, read `0.98x SQLite`, `2,356` backing reads.
- 🟥 [!] `SV8` natural dup256 writes remain dominated by duplicate-chain work: post-slot-flush attribution measured duplicate-chain time at `90.49%` with `9,936` duplicate-run overflows; the latest diagnostic-path rerun measured duplicate-chain time at `89.38%`.
- 🟥 [!] `SV8` natural dup256 remains red after the accepted fixes: latest diagnostic-path rerun measured write `0.23x SQLite` and read `0.41x SQLite`.
- 🟥 [!] `SV8` natural dup256 path counts identify the active blocker as mixed overflow-chain walking: `None=2,080`, `HeaderTailFast=77`, and `OverflowChainLocal=9,843`.
- 🟥 [!] Lowering `Scalar8VarIdentityTerminalDuplicatePressureItemCount` from `32` to `8` was rejected: natural duplicate-chain attribution stayed at `91.16%`, and tuple-major write dropped to `0.72x SQLite`; the constant was reverted to `32`.
- 🟥 [!] Changing natural dup256 `--initial-shelf-kib` to `32` or `16` was rejected for C51: both runs still reported `OverflowChainLocal=9,843`, so shelf sizing does not address the topology.
- 🟥 [!] BLX rejected linked mixed-chain terminal extraction for C51: natural dup256 read improved to `1.15x SQLite`, but write collapsed to `0.07x` and storage exploded to `84.24 MB` / `7,020 B/item`; prototype reverted.
- 🟥 [!] BLX rejected early routed split before local chain insert: natural dup256 write fell to `0.05x`, tuple-major write fell to `0.60x`, and `OverflowChainLocal` remained `9,843`; prototype reverted.
- 🟥 [!] BLX rejected volatile per-key chain hints: natural dup256 remained red at write `0.22x SQLite`, and tuple-major drifted lower; prototype reverted.
- 🟩 [x] BLX key-major upper-bound proof passed: same dup256 data in `--order key-major` measured write `1.37x SQLite` and read `1.14x SQLite`. This points the remaining C51 work toward explicit batch/bulk coalescing or durable per-key route optimization rather than shelf-size, threshold, or local hint tuning.
- 🟩 [x] Added and rebuilt an explicit `SV8` coalesced batch-write path. Build log: `artifacts/build-harness-release-x64-sv8-coalesced-batch-20260701.log`.
- 🟨 [~] Accepted the coalesced path as a targeted C51 route, not a default direct-write replacement. Natural dup256 with `--coalesce-writes true` measured `65,632/sec` write / `1.35M ids/sec` read (`0.95x` / `0.92x` SQLite), then `62,934/sec` write / `1.39M ids/sec` read (`0.67x` / `1.07x` SQLite) on a consistency rerun, then post-revert verification measured `48,415/sec` write / `953K ids/sec` read (`0.57x` / `0.90x` SQLite). Duplicate overflows were `0` in all runs.
- 🟨 [~] Tuple-major with `--coalesce-writes true` measured write `64,439/sec`, read `965K ids/sec`, write `0.60x`, read `1.19x`; keep direct tuple-major C52 as the better write path.
- 🟩 [x] Post-build sanity passed: `sv8-routed-sanity --items 1400 --duplicate-modulo 256 --identity-length 96` and serial same-key `sv8-routed-sanity --items 1400 --duplicate-modulo 1 --identity-length 96`.
- 🟥 [!] BLX rejected one-key pre-link mixed extraction for direct C51. Threshold `8` did not activate (`OverflowChainLocal=9,843`); threshold `2` plus exhausted-depth extraction improved read to `1.14x SQLite`, but write collapsed to `0.02x` with structural attribution `89.12%`. Prototype reverted and rebuilt: `artifacts/build-harness-release-x64-sv8-prelink-extract-reverted-20260701.log`.
- 🟨 [~] Enabled existing `SV8` same-depth fixed-key split path as a routing-invariant repair. Rebuild passed: `artifacts/build-harness-release-x64-sv8-samedepth-20260701.log`. Direct natural C51 now has splits `15`, duplicate overflows `0`, storage `183.98 B/item`, and read bytes `26.30 MB` instead of `118.09 MB`; write remains red at `24,352/sec` (`0.36x SQLite`) in the no-attribution run.
- 🟩 [x] Same-depth validation passed `sv8-routed-sanity --items 1400 --duplicate-modulo 256 --identity-length 96` and same-key `--duplicate-modulo 1`. Tuple-major rerun remained functionally correct with duplicate overflows `3,808`; rerun measured write `50,225/sec`, read `1.21M ids/sec`, write `0.83x`, read `0.99x`.
- 🟩 [x] Added `--read-pattern range|exact-keys` to `sv8-routed-sqlite-comparison` so C51-style range reads are no longer conflated with exact/specific-key identity lookups. Rebuild passed: `artifacts/build-harness-release-x64-sv8-readpatterns-final-20260701.log`.
- 🟩 [x] Added a reusable `Scalar8VarIdentityRangeReader.Reset(...)` path and changed exact-key measurement to reuse one reader across the sorted fixed key unit. Rebuild passed: `artifacts/build-harness-release-x64-sv8-exact-reuse-20260701.log`; `sv8-routed-sanity --items 1400 --duplicate-modulo 256 --identity-length 96` passed.
- 🟨 [~] Low-fanout exact-key probes after reader reuse: direct exact1 read `0.44x SQLite`, direct exact16 read `0.46x`, coalesced exact1 read `0.47x`, and coalesced exact16 read `1.30x`. Reader setup/teardown was real overhead, but direct ordinary-shelf exact probes remain read-volume limited.
- 🟩 [x] Date-like high-fanout exact-key probes after reader reuse are green: coalesced exact1 with `120000` items / `365` keys measured write `5.75x SQLite` and read `1.08x`; exact16 measured direct read `1.07x` and coalesced read `1.24x`. This better matches the user's "identities for these specific dates" workload than the original C51 BETWEEN range.
- 🟩 [x] Added `--write-batch-size`, `--read-pattern rotating-range`, `--read-window-count`, and `--read-window-step` to `sv8-routed-sqlite-comparison`, rebuilt in `artifacts/build-harness-release-x64-batched-policy-20260701.log`, and passed `sv8-routed-sanity --items 1400 --duplicate-modulo 256 --identity-length 96`.
- 🟨 [~] First batch-policy C51a rotating range rows: direct full-batch write/read `0.27x` / `1.50x`; coalesced full-batch `0.59x` / `1.51x`; coalesced microbatch1024 `0.33x` / `1.51x`. Reads are now green under rotating windows, but small dup256 write rows remain red versus SQLite.
- 🟩 [x] Date-like coalesced microbatch4096 rotating range row is green: `120000` items / `365` keys measured write `1.35x SQLite`, read `3.63x SQLite`, LibraDex storage `152.78 B/item`, SQLite storage `1089.07 B/item`.
- 🟥 [!] Added the `256` small-batch tier. C51a coalesced microbatch256 measured write `0.37x SQLite`, read `1.89x`, LibraDex storage `183.98 B/item`, SQLite storage `2644.32 B/item`. Date-like coalesced microbatch256 measured write `0.31x`, read `3.20x`, LibraDex storage `170.26 B/item`, SQLite storage `6039.34 B/item`. Treat `256` writes as an active focus row even though reads/storage are strong.
- 🟥 [!] SV8 write diagnosis points to batch-local key locality/topology rather than variable-identity byte generation. Date-like natural microbatch256 stayed red, but key-major microbatch256 measured write `85,427/sec` (`0.94x SQLite`) and `125.54 B/item`; the same natural microbatch256 with fixed-length identities fell to `3,282/sec` (`0.27x SQLite`) and `236.51 B/item`.
- 🟥 [!] SV8 date-like natural microbatch1024 improves but is still write-red: write `11,310/sec` (`0.74x SQLite`), read `3.22x`, storage `156.06 B/item`.
- 🟩 [x] SV8 date-like sorted/coalesced microbatch2048 read correctness blocker is cleared by the coalesced locality gate. The gate sorts only when a submitted batch has a same-key frequency of at least `32`; this avoids the low-locality sorted same-depth split reachability failure that stranded the first `101` generated identities for exact key `91`.
- 🟨 [~] SV8 date-like coalesced microbatch2048 rotating range is read/storage green but write-mixed after public-batch overhead fixes. Final non-attribution rotating rows after root-prefix caching, single-validation insert, and diagnostic gating measured write/read `1.05x`/`3.05x` SQLite, then `0.82x`/`3.07x` on a consistency rerun. Direct control on the same binary also drifted below SQLite on write at `0.88x`, so do not call write parity stable until repeat/median evidence exists.
- 🟨 [~] Former blocker exact key `91` now completes with checksum parity after the locality gate and public-batch fixes. The exact-key diagnostic row measured write `0.93x` and read `0.82x`; keep it as correctness evidence and low-fanout exact-key watch evidence, not as the main rotating-range baseline.
- 🟩 [x] Added temporary `SV8` write telemetry to `sv8-routed-sqlite-comparison` behind `--write-telemetry true`, including timing/allocation buckets, batch counts, coalesced same-key frequency, and commit-shape counters. Rebuild passed in `artifacts/build-harness-release-x64-sv8-write-telemetry-commitshape-20260701.log`.
- 🟥 [!] Telemetry isolated the date-like microbatch256 write gap to full dirty ordinary-shelf rewrite amplification: direct microbatch256 wrote `3.75 GB` through commit for a `22.53 MB` final LibraDex file, flushed `27,918` dirty `SV8` shelves over `469` publishes, and allocated `6.29 GB` in `libra insert`. Direct microbatch4096 dropped to `352 MB` committed, `2,582` dirty shelves, `30` publishes, and crossed SQLite on write at `1.04x`.
- 🟨 [~] Coalesced write telemetry shows the coalesced path is intentionally inert for this date-like workload after the same-key gate: sorted batches were `0`; largest same-key frequency max was `1`, `3`, `6`, and `12` for microbatch `256`, `1024`, `2048`, and `4096`. Do not expect the coalesced route to fix date-like microbatch writes unless the submitted batch has enough same-key locality.
- 🟨 [~] Initial shelf profile telemetry at `16KiB`, `32KiB`, `64KiB`, and `128KiB` rejects shelf size as the C51a date-like write fix. Microbatch256 stayed at about `3.75 GB` committed and `27,918` dirty `SV8` shelves; microbatch4096 stayed at about `352 MB` committed and `2,582` dirty shelves. Next branches should target dirty-range/delta publication, pooled/reused mutable shelf images, or explicit app-batch coalescing with clear durability semantics.
- 🟩 [x] Implemented dirty-range ordinary-shelf publication for `SV8` mutable shelves and pooled mutable shelf images. Rebuilds passed in `artifacts/build-harness-release-x64-sv8-dirtyrange-20260702.log`, `artifacts/build-harness-release-x64-sv8-dirtyrange-structuralfull-20260702.log`, and `artifacts/build-harness-release-x64-sv8-dirtyrange-pool-20260702.log`; `sv8-routed-sanity --items 1400 --duplicate-modulo 256 --identity-length 96` passed.
- 🟩 [x] Dirty-range publication is intentionally final-publish only for structural batch paths. A partial structural pre-flush failed microbatch1024 because `DataKernel.ReadPending` is not interval-overlay aware; full-shelf structural pre-flush was restored while final publish keeps the dirty-range optimization.
- 🟩 [x] Dirty-range plus pooling accepted for date-like `SV8` batch tiers `1024+`: direct microbatch1024/2048/4096 measured write ratios `1.17x`, `1.36x`, and `1.19x` SQLite with reads all near `3x` SQLite, while commit bytes fell to `189.69 MB`, `160.59 MB`, and `129.04 MB`.
- 🟨 [~] Dirty-range plus pooling moved date-like direct microbatch256 from red to near-parity: first pooled run measured write `0.81x` and a focused repeat measured write `1.02x`; commit bytes fell from `3.75 GB` to `245.86 MB` and insert allocation fell from `6.29 GB` to about `140 MB`. This row needs repeat/median evidence before being called stable green.
- 🟨 [~] Commit-gap coalescing is rejected as the primary microbatch256 fix. Rebuild with `--commit-gap-coalesce-bytes` passed in `artifacts/build-harness-release-x64-sv8-commitgap-option-20260702.log`; gap sweep showed `0`, `512`, `4096`, and `16384` byte thresholds produce near-parity/mixed write ratios, but widening to `16KiB` only reduced write calls from about `55.9K` to `51.6K` and increased committed bytes from `245.86 MB` to `283.04 MB`.
- 🟥 [!] BLX rejected branches for the sorted/coalesced microbatch2048 issue: same-depth-disable fixed exact key `91` but collapsed reads (`0.09x`) and still missed a later rotating window; terminal duplicate pressure `4` could misroute nonmatching keys into exhausted terminal roots; stack-backed frequency counting regressed the rotating write row to `0.77x`.
- 🟨 [~] `SV8` dup256 tuple-major pre-fix reads were dominated by descendant edge-router over-scan: clean identity-mode read issued `27,396` backing reads for the repeated 16-key range; key-only mode stayed red at `0.09x SQLite`, so identity-copy cost was not the primary cause.
- 🟨 [~] `SV8` duplicate pressure is workload-sensitive: tuple-major dup16 read measured `2.93x SQLite`, while high-byte fanout dup256 was worse and bloated the file to `27.28 MB`.
- 🟨 [~] `SV16` natural run: write `1.13x` SQLite and read `0.74x` SQLite.
- 🟨 [~] `SV16` tuple-major fixed-identity run: write `0.81x` SQLite and read `1.15x` SQLite.
- 🟩 [x] `VV` moderate run: routed write was `1.26x` SQLite, routed read was `3.86x` SQLite, bulk build was `3.89x` SQLite write and `4.74x` SQLite read.

## Phase 3A: VS8 Scoop Range Count

- 🟩 [x] Diagnose the `count-range-api` `0.02x` row through the production count and range paths.
- 🟩 [x] Experimentally test the existing target-metadata counter, reject it after transformed/shared targets break parity, and restore the reader-backed count.
- 🟥 [!] Rejected the counted prefix B+ replacement after restoring LibraDex's core boundary-to-boundary shelf-scoop invariant to the analysis; keep [vs8-canonical-counted-tree-design.md](vs8-canonical-counted-tree-design.md) only as an evaluated BLX branch.
- 🟩 [x] Select the core-compatible algorithm: exact lower/upper shelf-slot boundaries plus persisted live counts for distinct shelves between them in logical route order.
- 🟩 [x] Record the route/shelf invariants, BLX comparison, seven LXL proofs, expected performance shape, and proof gates in [vs8-scoop-range-count-design.md](vs8-scoop-range-count-design.md).
- 🟩 [x] Obtain explicit approval for the read-only topology proof slice.
- 🟩 [x] Implement and prove the read-only route-order/shelf-interval inspector gate; it correctly exposed the original `16` overlapping shelf intervals.
- 🟩 [x] Repair expanded logical prefix-range transforms by republishing the complete contiguous parent run at immediate divergence and retaining the still-varying parent depth for deeper divergence.
- 🟩 [x] Clear the topology gate: `65,536 x prefix8` passes with `9` routers, `32` extents, and zero violations; `250,000 x prefix64` passes with `65` routers, `128` extents, and zero violations. Both preserve exact live/reopen tuple parity, and routed/multi-byte-transform sanity tests remain green.
- 🟩 [x] Implement a count-only scoop using the existing VS8 format; full plus `128` sampled ranges and terminal duplicates pass against the reader oracle live/reopen.
- 🟩 [x] Repair the public encoded-key transform rollover with expanded exact-stem chains and lazy independent sibling shelves; the final `250,000`-row topology has zero aliases, multiple parents, cycles, or overlaps.
- 🟩 [x] Repeat isolated ShapeBench `count-range-api`, batch `5000`, thread `1`, in three processes per engine with exact `799,916` result parity: LibraDex median `26,056.554 ops/s`, SQLite median `6,138.714 ops/s`, ratio `4.245x`.
- 🟩 [x] Adopt the proven scoop and topology principles for VS16/VV while preserving their identity/layout semantics. Three-process medians with exact `799,916` parity: VS16 `41,090.975` versus SQLite `14,221.882` (`2.889x`); VV `88,342.875` versus SQLite `14,860.940` (`5.945x`).
- ⬜ [ ] Add a fixed-key topology inspector before changing fixed-key routing; their count paths already scoop, but repeated-transform disjoint interval ownership remains to be independently proven.

### Phase 3A Rejected Branches

- 🟥 [!] Whole-target counts inferred from non-edge routes: route containment does not prove that the target lies between the actual shelf boundaries.
- 🟥 [!] Per-edge or router-subtree counts: add write amplification and hot shared metadata to solve a read-only aggregate problem.
- 🟥 [!] B+ replacement: discards the core router/shelf advantages to solve one count path.
- 🟨 [~] Ordinary-shelf next/previous links: next-best only if logical route-order traversal remains too expensive after the scoop implementation.

## Phase 4: Concurrency And Mutation Proof

- 🟥 [!] Superseded the first uncapped concurrent-batch rerun because workers raced through one shared claim counter; rows such as batch `5000` / 8 threads let one worker process `215,000` of `250,000` items and did not describe per-caller concurrency fairly.
- 🟥 [!] Superseded best-thread conclusions from the dynamic-claim telemetry. The old best worker was also the worker that claimed most of the work, so best/single was not a valid caller-throughput comparison.
- 🟩 [x] Realign ShapeBench concurrency around fixed equal per-thread work, synchronized starts, and deterministic `disjoint`, `mixed`, and `overlap` key-locality modes over the same `250,000` unique tuples for LibraDex and SQLite.
- 🟩 [x] Remove artificial batch-size permutations from `BeginConcurrentWriter`; it has no public batch boundary. Retain one canonical writer cadence and all three batch sizes only for `BeginConcurrentBatch`.
- 🟩 [x] Run all `63` corrected concurrency rows serially: `9` one-thread API baselines plus `54` 8/16-thread locality rows. All rows returned exactly `250,000` items, every concurrent worker received an equal item count, all requested workers were active, stderr stayed empty, and successful child payloads were removed.
- 🟩 [x] Update ShapeBench CSV/HTML with LibraDex-first concurrency reporting: aggregate/single, average-thread/single, best-thread/single, slowest-thread rate, rate fairness, active/requested writers, publications, and items/publication. SQLite remains secondary single-writer context.
- 🟩 [x] `SS8-8 BeginConcurrentBatch` now shows the intended capability boundary. Disjoint writes scale to `1.87x-2.48x` single aggregate throughput; mixed 8-thread writes are `1.09x-1.35x`; mixed 16-thread writes fall to `0.34x-0.60x`; overlap rows are `0.37x-0.84x`. Publication fragmentation tracks the collapse: disjoint rows retain `34.0-162.9` items/publication, while mixed/overlap 16-thread failures fall as low as `1.8-3.1`.
- 🟩 [x] Generic `BeginConcurrentWriter` is throughput-stable rather than scalable in the current implementation. Across six fixed-scalar shapes its median aggregate/single ratio is `0.99x`; shape medians range from `0.91x` to `1.05x`, all callers progress, and overlap is the weakest locality. The current facade admits concurrent callers but serializes individual insert admission broadly enough that locality has limited throughput effect.
- 🟩 [x] Correct the physical-locality controls: disjoint now has zero shared terminal shelves, mixed measures the intended approximately `25%` shared-shelf item ratio, and overlap measures `100%` shared-shelf items. Add matched one-thread replays for every locality/API/worker plan.
- 🟩 [x] Exclude concurrency diagnostics from general SQLite rankings and expose plan-worker, terminal-shelf, and immediate-parent sharing in the dedicated concurrency report.
- 🟩 [x] Add exact `SS8-8` close/reopen tuple validation against the deterministic worker plan. The first gate exposed an existing overlap loss where API counters reported `50,000` but range enumeration, `Count()`, and point lookup proved `49,999` persisted tuples.
- 🟩 [x] Fix the stale-shelf/topology race by holding the per-root `SS8-8` topology read gate for the complete writer-context lifetime. Ten repeated overlap/batch-64 runs and all `24` focused `8/16 x batch 64/250/1000/5000 x locality` rows passed exact `50,000`-tuple reopen parity.
- 🟥 [!] Reject the first empty-conflict-context abort optimization despite lower publication counts: a writer-context operation can mutate bytes before throwing an ownership exception, so the operation-level staged counter cannot prove that abort is safe.
- 🟩 [x] Replace the unsafe counter with context-owned dirty state: ordinary read/claim images and accepted dirty shelves are distinct, terminal staged bytes remain explicit, and only provably empty contexts abort.
- 🟩 [x] Add `EmptyContextAbortCount` to concurrent-batch results and ShapeBench attribution. Direct A/B reduced conflict publications approximately `91-97%`; throughput split `6` wins / `6` losses, while all `24` focused rows and the final `20`-run telemetry smoke retained exact reopen parity.
- 🟥 [!] Reject in-place empty-context reset; exactness held, but focused overlap/batch-64 throughput regressed versus abort/recreate.
- 🟥 [!] Reject removing the generic queued-writer facade lock as the current improvement: aggregate throughput regressed to approximately `24K-27K/sec` against matched one-thread controls around `41K-42K/sec`.
- 🟨 [~] Treat batch size as locality-dependent. Disjoint continues to amortize publication through `5000`; mixed/overlap generally have a balanced knee near `1000`, while larger batches remain scheduling-sensitive at 16 workers. Keep repeat/latency evidence open before selecting an adaptive production default.
- 🟩 [x] Generate the focused matched-control artifact set with `36` unique exact-parity rows: `artifacts/shape-bench/concurrent-topology-gate-20260716/results.csv` and `report.html`.
- 🟩 [x] Expose a reliable changed-before-exception signal through context-owned dirty state and use it to eliminate read/claim-only publications without weakening exact reopen parity.
- 🟨 [~] Concurrent-batch latency branch: ShapeBench now reports batch/final-publish p50/p95/p99/max plus conflicted/unconflicted cohorts. Three isolated mixed/overlap samples proved the long tail is conflict recovery, not final publish. Reject periodic 1 ms bounded backoff because it reduced retry counts without improving median p95 and introduced p50/worst-sample regressions. Next evaluate owner-release notification or deterministic acquisition ordering; keep conflict-local merge as the higher-risk fallback.
- 🟩 [x] Run a repeated current `concurrency-performance-proof` memory profile and keep CSV/markdown artifacts.
- 🟩 [x] Run a file-backed `concurrency-performance-proof` smoke and keep CSV/markdown artifacts.
- 🟩 [x] Run `fixedn-concurrency-performance-proof`.
- 🟩 [x] Run `fixedn-batch-coalescing-proof`.
- ⬜ [ ] Decide whether fixed-N proof commands need rotating CSV/markdown report output before deeper comparison.
- ⬜ [ ] Decide whether to surface `ReadExact(key)` / `ReadExact(keys)` publicly over the reusable exact-key cursor idea, with fixed-unit keys sorted/deduped internally.
- ⬜ [ ] Regenerate the full matrix under the batch-first policy; treat old C-series rows as pre-policy evidence until rerun.
- ⬜ [ ] Add SQLite index-only concurrency comparison only after existing reruns confirm the missing evidence is still important.

### Phase 4 Results

- 🟩 [x] File-backed smoke passed and wrote `artifacts/performance-redo-20260628/concurrency-performance-smoke.csv` and `.md`.
- 🟥 [!] Larger memory profile failed in `VS16` multi-insert with `The VS16 transform split path requires at least two distinct prefixes in the remaining raw key bytes.`
- 🟩 [x] `VS16` blocker isolated and fixed. The failing shelf had `firstKeyDepth=4` while its keys still diverged earlier (`first=02000B00`, `last=020023A8`), proving an over-narrow var-key route-depth path rather than duplicate logical keys. `VS16` now mirrors the existing `VS8` fallback to split from root depth and uses intermediate var-key routes that preserve left/right sibling exits.
- 🟩 [x] Isolation rerun passed: `concurrency-performance-proof --single-ops 2048 --ops-per-thread 2048 --max-threads 4 --include-file false`, artifact `artifacts/performance-redo-20260628/concurrency-performance-memory-2048x4-vs16-fallback.md`.
- 🟩 [x] Original large memory profile passed and wrote `artifacts/performance-redo-20260628/concurrency-performance-memory-1024x8-vs16-final.csv` and `.md`; `VS16` 8-thread multi-insert was `14,239.52 ops/sec` with expected/readback count `8200`.
- 🟩 [x] `fixedn-concurrency-performance-proof --single-ops 512 --ops-per-thread 128 --max-threads 8` passed.
- 🟨 [~] Fixed-N 8-thread current rows: `FSN-8` insert `7,430 ops/sec`, `FSN-16` insert `7,514 ops/sec`, `FSN-8` mixed delete/rekey `3,768 ops/sec`, `FSN-16` mixed delete/rekey `2,517 ops/sec`.
- 🟩 [x] `fixedn-batch-coalescing-proof --small 8 --medium 64 --large 512` passed.
- 🟨 [~] Fixed-N 512-row batch summary: explicit insert remains a large win over one-shot, bulk delete/rekey variants now beat the per-operation explicit batch delete/rekey variants in this run.

## Phase 5: Grouping/HPC Proof

- 🟩 [x] Run grouped counts/results proofs at moderate size.
- 🟩 [x] Run string group key-state promotion proof.
- 🟩 [x] Run scalar-16 and fixed-N key-state promotion proofs.
- ⬜ [ ] Record whether grouped result paths still avoid per-group member materialization for HPC terminals.

### Phase 5 Results

- 🟩 [x] `group-by-execution-proof`, `group-by-direct-target-proof`, `group-by-composite-proof`, `group-by-aggregate-proof`, `group-by-row-reader-proof`, and `group-by-string-proof` passed.
- 🟩 [x] `group-by-string-key-state-promotion-proof --per-state 2048`, `scalar16-key-state-promotion-proof --per-state 1024`, and `fixedn-key-state-promotion-proof --per-state 1024` passed.
- 🟨 [~] Numeric grouped counts and aggregates are faster and lower allocation than legacy materialized grouping in this run; metadata/representative paths are lower allocation but not consistently faster.
- 🟩 [x] Direct-target grouped counts remain strong: `2.12x` speedup and `3.96x` allocation ratio versus legacy materialized path in this run.

## SQLite Concurrency Candidate

If Phase 4 requires a new SQLite concurrency guardrail, keep it index-only:

```sql
CREATE TABLE items(
    k INTEGER NOT NULL,
    i INTEGER NOT NULL,
    PRIMARY KEY(k, i)
) WITHOUT ROWID;
```

Use equivalent fixed-width BLOB schemas for 16-byte key or identity shapes. Candidate rows should include:

- ⬜ [ ] Single-thread batched insert.
- ⬜ [ ] Multi-thread same table insert with one connection per writer.
- ⬜ [ ] Delete exact tuple.
- ⬜ [ ] Rekey as delete plus insert in one transaction.
- ⬜ [ ] Mixed insert/delete/rekey pressure.
- ⬜ [ ] WAL/normal defaults, with optional WAL/full durability comparison if the result is ambiguous.

## Open Questions

- ⬜ [ ] Should the final campaign prioritize wall-clock repeatability or broad shape coverage first?
- ⬜ [ ] Should fixed-N proof output be upgraded to CSV/markdown before the full redo, or is console capture enough for this pass?
- ⬜ [ ] Should SQLite concurrency rows compare against LibraDex memory-backed concurrency, file-backed concurrency, or both?
- 🟩 [x] 2026-07-15 FS32 sorted-batch BLX: promoted FS32 direct-router walking, interval-sweep commit planning, allocation-free monotonic route frontier, active-shelf reuse, and borrowed retained-shelf publication accepted.<br/>
- 🟩 [x] Conservative exact-final-binary five-run 1M/b5000/t1 medians: FS32-8 `416,009.372 ops/sec`; FS32-16 `343,929.042 ops/sec`; best observed `482,562.880`/`386,156.729`; exact result/file-size parity retained.<br/>
- 🟩 [x] Final random insert, reopened lookup, count-all, generic API, and focused FS32 structural sanity checks passed.<br/>
- 🟥 [!] Header/slot write-span coalescing rejected on measured regression; global targeted router invalidation removed from the final candidate due cross-shape blast radius.<br/>
- 🟥 [!] Separate blocker: `validate --tier fast` stops at the SS8-8 router-arena read-cache expected-read-shape assertion (`4` reads, `8,200` bytes observed); reproduction survives restoration of original broad invalidation.<br/>
- 🟩 [x] 2026-07-19 FS32 range-reader BLX accepted a promoted-target-first probe using the existing session router projection, with authoritative classification retained for first touch; no second routing strategy or pointer cache was added.<br/>
- 🟩 [x] All 18 focused FS32-8/16 range identities/keys/pairs cells passed exact `799,916` parity. LibraDex gained `1.426x-2.643x` versus the prior canonical rows (`1.978x` median); trace-attributed classifier time fell `98.26%` and total range traversal samples fell `59.51%`.<br/>
- 🟨 [~] Treat the new `2.976x-6.604x` focused SQLite ratios cautiously: focused SQLite throughput and both engines' process spreads drifted materially. Use the LibraDex before/after and trace for implementation attribution.<br/>
- 🟩 [x] Retest the final four sub-par FS32 prefix rows with three isolated runs per engine; all passed exact `319,984` parity and now measure `2.850x-4.197x` SQLite. The refreshed `173 / 291` canonical report has no completed row below `1.0x`.<br/>
- 🟨 [~] Preserve the focused-prefix provenance caveat: SQLite throughput was unusually low and spreads reached `49.610%` LibraDex / `45.414%` SQLite, so do not treat the ratios as a stable cross-campaign ceiling.<br/>
- 🟩 [x] Realign canonical reads to one isolated process per engine/cell, one discarded cold pass, and three warm same-session passes with fresh per-pass transient execution objects.<br/>
- 🟩 [x] Disable SQLite connection pooling and use one fresh covering-index command per complete pass, rebinding only within that pass; retain connection-local B-tree cache while excluding prepared-command carryover.<br/>
- 🟨 [~] Reset the visible canonical report to `0 / 291` and run `canonical-read-warm-t1-250k-3rep-20260719` serially from the first shape with incremental HTML updates and a 15-minute heartbeat.<br/>
