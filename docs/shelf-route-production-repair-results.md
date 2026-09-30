# Shelf-route production repair results

Verified September 17, 2026. Baseline: `020e077b54b150c94483e4c1ba0b8206fd9b0ea5`.

**Follow-up, September 17 at 23:24 Phoenix:** The five write-byte assertion failures described below are now resolved as harness accounting defects. All five corrected tests pass against both the repaired engine and the unchanged baseline engine. The original observations below remain historical; see [allocator assertion correction](shelf-route-allocator-assertion-correction.md).

## Implemented repair

The repair makes route ownership explicit when a shared shelf becomes a deeper router or terminal shelf. It clears only obsolete aliases to that shelf, preserves unrelated siblings, and publishes parent and child changes through the existing structural publication boundary. Mixed-prefix shelves retain parent-level partitioning rather than being treated as single-prefix shelves.

The fixed-scalar repair covers SS16-8, SS8-16, SS16-16, FS32-8, and FS32-16, including their terminal conversions. SV8 variable-identity publication refines graph owners, including one-byte compressed-range owners; SV16 preserves every required intermediate prefix rather than skipping unproven stem bytes.

Testing exposed a necessary companion repair: clearing stale aliases creates legitimately unset routes at nested depths. Fixed-scalar and SV insertion now retain the immediate parent when walking an unset route and publish a populated, correctly typed cold shelf there. Without this companion change, the original Meridian workload still failed after ownership refinement.

Ordinary existing-route insertion does not perform the structural ownership scan. The fixed-family helper avoids allocating or rewriting a parent already known to have exact ownership. SV8 compressed-range expansion allocates only on the structural repair path. SV16 may require additional router pages to preserve exact routing semantics; these are a correctness cost, not a claimed free optimization.

No public API or persisted-format migration was introduced. No AbraxasDB or Meridian production source was changed by this repair. Previously damaged indexes are not automatically healed; rebuild affected indexes from authoritative records.

## Verification results

| Verification | Result |
| --- | --- |
| Final Release x64 LibraDex/harness build | Passed; zero warnings and errors |
| New ownership regression command | 43 cases passed |
| Same new command against clean baseline engine | Failed on retained unrelated alias, demonstrating a baseline defect |
| Existing distinct regression commands listed below | 21 passed |
| Contrived routing model | 10 checks passed; design evidence, not engine acceptance |
| Meridian fresh 550-order reproduction | Passed, including 2,750 lines |
| Meridian fresh 10,000-order dataset | Passed, including 50,000 lines |
| Independent full-page Meridian sort oracle | 44 complete streams passed: 11 sorts, two directions, two datasets |
| Meridian 1,000-order workbench verification | Passed all 13 top-level checks and foundation checks |
| Working-tree whitespace/error check | `git diff --check` passed |

The 43 ownership cases comprise 40 fixed-family cases (five families, serialized/direct/mixed/terminal modes, root/nested owners), plus SV8 direct-parent, SV8 compressed-range-parent, and SV16 cases. They check complete ordered identity results, both traversal directions where applicable, insertion into newly cold routes, and close/reopen. Each variable-identity case inserts 6,003 tuples, including earlier-stem neighbors after repeated splitting.

The Meridian full-page oracle reads every page through end-of-stream, compares values against independently sorted unsorted-record input, and verifies unique identity counts. This goes beyond the application's first-two-page sort checks. The workbench run covers packed line hydration, indexed reverse customer hydration, link suppression, grouped aggregates, relationship paging, analysis drill-down, edits preserving links, stale-edit rejection, invalid-stock rejection, and close/reopen. These are programmatic application checks, not visual WebView2 acceptance.

### Existing passing commands

- `fixed-shape-few-thousand-storage-sanity`
- `fixedn-few-thousand-storage-sanity`
- `variable-shape-few-thousand-storage-sanity`
- `duplicate-run-sanity`
- `concurrency-contract-sanity`
- `concurrency-admission-sanity`
- `scalar8-concurrent-batch-publication-sanity`
- `vs8-compressed-parent-fallback-split-sanity`
- `vs8-noncontiguous-owner-split-sanity`
- `vs8-converged-prefix-split-sanity`
- `vs8-shared-shelf-growth-sanity`
- `vs16-shared-shelf-growth-sanity`
- `vv-shared-shelf-growth-sanity`
- `fixedn-varidentity-routed-sanity`
- `ss8-8-mixed-prefix-ordering-sanity`
- `sv8-routed-sanity`
- `sv16-routed-sanity`
- `sv16-shared-shelf-growth-sanity`
- `vs8-route-versioned-publish-sanity`
- `vs8-concurrent-pool-abort-sanity`
- `concurrency-workload-matrix`

Together these suites exercise all 14 shelf families, but do not apply every new contrived topology to every family.

### Five pre-existing failures retained

The following older commands fail their write-byte assertions identically on the candidate and the clean baseline. Their assertions were not changed to manufacture a green result.

| Command | Actual bytes | Expected bytes |
| --- | ---: | ---: |
| `ss16-8-walked-transform-sanity` | 81,920 | 73,728 |
| `ss8-16-walked-transform-sanity` | 81,920 | 73,728 |
| `ss16-16-walked-transform-sanity` | 81,920 | 73,728 |
| `fs32-8-walked-transform-sanity` | 94,208 | 90,112 |
| `fs32-16-walked-transform-sanity` | 94,208 | 90,112 |

These commands stop at the assertion, so their later checks cannot be counted as passing. The new regression cases supply separate functional coverage. The full test suite is therefore not unconditionally green.

## Performance evaluation

An isolated no-split SS16-8 routed-bulk workload compared clean baseline and candidate across four alternating process runs each, 32 batches of 512 items, 32 prefixes, five warmups and ten repetitions per process. Tiered compilation was disabled; process launch requested a fixed inherited CPU affinity. This measures a bounded internal storage path, not application-level durable throughput.

| Pattern | Baseline process throughput range | Candidate process throughput range |
| --- | ---: | ---: |
| Sorted | 1.311–1.420 million items/s | 1.198–1.459 million items/s |
| Random | 1.289–1.405 million items/s | 1.310–1.472 million items/s |

Checksums and storage work matched: one write per batch, 1,048,576 bytes per batch, 32 commits, zero SetLength calls. The overlapping variance does not establish a speedup or a consistent regression. Earlier short uncontrolled measurements are retained alongside these results; they are not sufficient for a performance conclusion. Structural repair costs remain workload-dependent.

## Acceptance boundaries

- No new one-million-item Meridian run, long-duration soak, or cold-cache benchmark was completed in this repair pass.
- No fault-injection/crash-recovery proof or exhaustive concurrent graph-state proof is claimed.
- Direct and one-byte compressed SV8 owners are exercised; this does not prove every possible multi-owner/depth combination.
- New multi-byte routing optimizations were not introduced.
- Existing damaged-index recovery remains a separate operation.

## Evidence locations

All run evidence is under `E:\VSProjects\Diagnostics\LibraDexRouteOptions-20260917`:

- `owner-tests-final.log`: final 43-case regression results.
- `acceptance` and `acceptance-last`: existing passing command logs.
- `suite-final` and `baseline-tests`: matching older failures; baseline new-regression failure.
- `build-repair.log`: final LibraDex/harness build.
- `meridian-550-b.json`, `meridian-10000.json`: application reproduction reports.
- `meridian-full-final.log`: 44 full ordered-stream checks.
- `meridian-workbench.json`: functional application checks.
- `meridian-full`: isolated full-stream checker source and build.
- `perf-repeat-*`: controlled performance logs and reports.
- `baseline`: isolated clean-baseline source/build plus the new regression harness.

The original failing Meridian fixture under AbraxasDB artifacts was preserved. This report supersedes proposed acceptance claims in the earlier review without rewriting their historical measurements.
