# Shelf route ownership: all-shape review and BLX repair design

**Implementation follow-up:** The repair and its measured acceptance boundaries are recorded in [shelf-route-production-repair-results.md](shelf-route-production-repair-results.md). The original review below remains historical.

Date: 2026-09-17. Status: source review and proposed repair; no engine changes or new executable acceptance runs in this review.

**Scenario-review addendum:** See [shelf-route-scenario-matrix.md](shelf-route-scenario-matrix.md). The earlier "Selected design" below is retained as history of the ownership-refinement branch, not a complete split-selection policy. The scenario review adds same-parent multi-byte splits, whole-parent representation/capacity checks, effective fallback domains, and a conditional repair portfolio.

## Finding

The Meridian SS16-8 failure is an instance of a cross-shape publication invariant: the keys currently present in a shelf do not define all keys its incoming routes are allowed to admit. Replacing a range-shared shelf with a subtree that skips part of that admissible range requires reconciling ownership during publication.

The prior retained Meridian fixture established mispartitioning before the exception. Details remain in `E:/VSProjects/AbraxasDB/artifacts/meridian-ss16-root-cause-20260917.md`. This review does not reinterpret that evidence as a reproduced failure in every other shape.

## Coverage and evidence classification

Fourteen physical shelf families were reviewed at their structural routing paths. Logical string/numeric/custom codecs are consumers, not additional physical routing families. Terminal identity roots are a separate lifecycle case shared across families.

| Family | Source findings | Disposition |
| --- | --- | --- |
| SS8-8 | Both queued root transform and serialized transform already clear irrelevant parent aliases. Serialized transform validates first/last key ownership and parent depth. A nested-parent regression exists. | Preserve as the reference behavior; rerun and extend tests. Not newly diagnosed as missing the same fix. |
| SS16-8 | Narrow direct transform and serialized transform advance depth and retain parent aliases. | Runtime-confirmed originating defect; repair both entry families. |
| SS8-16 | Same omitted parent ownership refinement in direct and serialized transforms. | Source-confirmed missing protection; targeted reproduction required. |
| SS16-16 | Same omitted parent ownership refinement in direct and serialized transforms. | Source-confirmed missing protection; targeted reproduction required. |
| FS32-8 | Same structural issue in direct and serialized transform paths, including callers using existing shelf images. | Source-confirmed missing protection; cover ordinary and sorted-tail fallback entry paths. |
| FS32-16 | Same structural issue in direct and serialized transform paths. | Source-confirmed missing protection; cover ordinary and sorted-tail fallback entry paths. |
| FSN-8 | Counts parent aliases and reconsumes the parent byte when shared; same-depth partition updates parent mappings. Deeper chains use exact stem targets. | Different existing ownership-aware strategy. Preserve pending execution of common invariant tests; not certified defect-free. |
| FSN-16 | Same ownership-aware approach as FSN-8. | Preserve and test; include terminal lifecycle separately. |
| FV (fixed-N key, variable identity) | Same parent-alias count/reconsumption and same-depth publication model. | Preserve and test; include terminal lifecycle separately. |
| VS8 | Parent range inspection, same-depth split publication, exact-stem chains, and owner-set replacement fallback are explicit. | Do not replace with a single-parent scalar helper. Validate shared/multiple-owner and compressed-parent cases. |
| VS16 | Parent range inspection chooses parent-depth reconsumption when shared; same-depth publication is explicit. | Preserve existing strategy and apply invariant tests. |
| VV | Parent range inspection and parent-depth reconsumption parallel VS16. Terminal mismatch can rebuild from depth zero. | Preserve existing strategy and apply invariant tests. |
| SV8 | Deeper split constructs a chain below the selected parent depth, then graph-repoints aliases at that depth. A separate same-depth split path and exact-route terminal handling exist. Repointing aliases does not by itself establish that all aliases own the skipped stem. | Related source-level exposure requiring adversarial reproduction and an owner-set-aware repair, not a single-parent assumption. |
| SV16 | Split selection starts from a supplied depth of zero; caller publishes with child depth equal to split depth. The publisher rewrites the source offset with a router and does not reconcile parent ownership of skipped stem bytes. | Related source-level exposure. Establish admissible ownership before skipping directly to split depth; reproduce separately. |

### Source anchors

- `FileSession/LibraDexFileSession.cs`: narrow fixed transforms around 4409, 4871, 5336, 5816, 6465; SS8 queued cleanup around 6924; SS8 serialized ownership validation around 28793 and alias cleanup around 28918.
- Same file: fixed-N parent alias counting around 11508, 12603, 13014; VS8 parent ownership validation around 24230; VS16 depth selection around 38154; generic route-range creation around 30089.
- `Indexes/LibraDexScalar8VarIdentitySession.cs`: deeper split around 985, overflow-chain split around 3662, router-chain construction around 4918, graph-wide repointing around 5024, exact-route terminal handling around 5159.
- `Indexes/LibraDexScalar16VarIdentitySession.cs`: split dispatch around 799 and publication around 1668.
- `Indexes/LibraDexVarKeyVarIdentitySession.cs`: parent-range/depth selection around 1290-1319.
- `FileSession/LibraDexFileSession.FixedScalar8Terminal.cs`: mismatch-to-router transition around 303, shared with scalar-16-identity callers in `LibraDexFileSession.FixedScalar16Terminal.cs`.
- `LibraDex.Harness/Commands/RawHarness.Scalar8Scalar8Ordering.cs`: existing root and nested-parent alias regression tests.

## Additional lifecycle finding: terminal roots

An exact-key terminal root and an ordinary shelf have different admissible-key semantics. The fixed scalar terminal mismatch helper rejects a key that diverges at or above the parent depth, then otherwise publishes a deeper router chain at the terminal offset. It does not receive the parent offset, so it cannot narrow parent aliases itself. A shelf-to-terminal transition followed by distinct-key insertion must therefore be included in the same ownership review and repair.

The SS8 implementation already contains consumed-route alias handling. That is useful precedent, not proof that all other terminal families are safe. Fixed-N terminal readers validate exact key equality; their lifecycle needs a duplicate-then-neighbor test even though ordinary fixed-N split handling already accounts for aliases.

## BLX alternatives

| Branch | Correctness conditions | Cost / tradeoff | Decision |
| --- | --- | --- | --- |
| A. Refine incoming ownership during structural publication | Prove the new subtree's admitted prefix, update all applicable owning routes, publish parent and child together, invalidate all changed routing views. | Single-parent direct router: at most 256 target inspections on a structural transition, and one parent page rewrite only when aliases must change. No added normal lookup hop, per-row allocation, or file-format field. | Preferred for ordinary fixed-scalar single-parent transitions. |
| B. Reconsume the ambiguous parent byte in a guard router | Guard must cover the actual complete ownership boundary, not only the selected route; unset routes must get independent shelves. | Can avoid parent mutation but adds router storage and traversal; router hop limits and range walkers need validation. | Next-best, already used in fixed-N/variable-key paths. Retain where it is the appropriate ownership model. |
| C. Add persistent prefix metadata and validate it during every traversal | Define format, reopen, mutation, and mismatch-split behavior for all readers/writers. | Wider format/cache changes and hot-path checks; may save router pages for long common stems. | Not justified for this correction; possible later measured optimization. |
| D. Detect/recover mismatch during ordinary reads/inserts | Must repair topology, not just reject one insert; reads before detection still matter. | Adds frequent work and makes the original bad publication survive. | Reject as primary repair. Retain focused diagnostic guards only. |
| E. Route backfill through sorted builders only | Does not address later incremental inserts, edits, terminal transitions, or other publishers. | May improve a particular workload while masking defective generic mutation. | Reject as correctness repair. |
| F. Eliminate shared shelves or allocate all prefix siblings eagerly | Avoids some alias cases but must preserve existing data and all other transitions. | Potentially severe page slack/allocation amplification; broad redesign. | Reject absent evidence that it beats bounded ownership refinement. |

### Selected design

Use one invariant with specialized publishers, not one universal whole-tree scan and not duplicated ad-hoc fixes:

> Every incoming route to a replacement subtree must admit only keys covered by that subtree's represented prefix. A byte may be skipped only when all incoming ownership proves that byte fixed.

For direct, single-parent fixed-scalar transforms, adopt the existing SS8 ownership-refinement model for the other five families:

1. Carry parent offset, expected source target, parent depth, and selected prefix through every transform entry point, including serialized fallbacks, root wrappers, walked wrappers, and shelf-image paths. No optional zero parent that silently disables the invariant.
2. The sorted split input must prove one common consumed stem, including the parent byte. A mixed-prefix source needs a parent-level partition, not a deeper transform.
3. Under the existing publication boundary, re-read/revalidate the current parent, then clear only mappings to this source target that lie outside the proven owned prefix. Preserve unrelated sibling targets.
4. Stage parent refinement and source-to-router replacement in the same structural publication. Invalidate both parent and child decoded routing/cache views before any deferred batch continues using them.
5. Lazily create independent shelves for newly arriving sibling prefixes; do not preallocate 256 shelves.
6. Keep normal shelf insertion and lookup unchanged. Share the parent ownership operation where it genuinely removes repeated correctness risk; retain typed key comparison/encoding and shape-specific shelf construction.

For SV8/SV16, first establish the complete owner set using their existing shared-shelf/graph facilities. Exact single-parent cleanup is insufficient where several parent routes or depths can own the source. Either refine every relevant owner, or preserve/reconsume the required stem with an owner-valid subtree. Never graph-repoint every alias into a subtree that assumes only the selected alias. Do not remove the existing exact-stem/parent-range handling from FSN/VS/VV merely to make the implementation uniform.

## Concurrency, caches, and durability

- Revalidate the current parent while holding the structural publication exclusion; do not publish a stale planning snapshot over a sibling split.
- Two transforms of different children of the same parent must preserve each other's route edits.
- Writer contexts already validate expected parent target and target kind (for example SS16-8 around `ValidateScalar16Scalar8RouteClaimsForWriteContext`). Tests must establish that a stale context retries/rejects rather than writing shelf bytes over a newly published router.
- `CommitAndDeferRouterReadCacheInvalidation` explicitly requires per-router invalidation before continued batch use. Updating disk bytes without invalidating the parent is not a repair.
- Grouping writes in the existing publication mechanism is not a new claim of crash atomicity. Reopen and supported fault-injection/crash-boundary tests must prove the actual durability contract.
- Avoid new per-key locks, delegates, graph scans, or heap ownership records on ordinary lookup/insert paths. Multiple-owner discovery belongs only on shapes/transitions that require it.

## Validation required before calling the correction complete

### Deterministic red/green tests

- Current keys all use parent prefix A; parent also aliases B; force a deeper transform; then insert B with a smaller following byte. Verify immediate topology and complete ordering before filling another shelf.
- Mirror the case for lower sibling prefix, and use non-contiguous aliases plus an unrelated neighboring shelf.
- Current full shelf spans A and B: partition at the parent rather than silently narrowing away B.
- Repeat at nested depths, across several subsequent transforms, and at maximum key depth.
- Exact duplicate runs that become terminal roots, followed by neighbor keys differing before, at, and after the last consumed byte.
- Shared-owner graphs for SV8/SV16 and compressed/range parents for variable-key shapes.
- Apply these semantic cases to all fourteen families with suitable capacities and identity encodings. Distinguish unsupported mutation modes from untested supported modes.

### API and lifecycle parity

- Full ascending and descending result sequences against an independent sorted multiset oracle, not only count or sampled first/last pages.
- Exact lookup, boundary ranges, empty ranges, count, aggregates, duplicate identities, deletion/reinsertion, and key-changing edits.
- Ordinary, queued/direct, serialized fallback, batch, sorted-tail fallback, and applicable bulk-build-then-mutate paths.
- Same-session warm caches, post-commit, reopen, and supported concurrent reader/writer schedules.
- Meridian's original 550/1,000-order cases, then the larger sample; validate every affected indexed sort and range rather than only the previously sampled pages.

### Performance gates

Measure baseline and candidate with identical build/configuration/durability/data, separate correctness from timing, and repeat serial runs. Cover no-split inserts, split-heavy inserts, uniform/skewed/duplicate-heavy distributions, allocations, parent/router reads and writes, file growth, and cold/warm point/range reads. Compare equivalent completed operations; the crashing baseline is not an end-to-end performance baseline. No speedup or negligible-overhead claim is established by this source review.

## Existing indexes

Prevention does not repair previously mispartitioned records. Merely clearing stale aliases in a damaged tree can make existing tuples unreachable. For a known affected Abraxas index, rebuild the derived index from authoritative records after correction and verify it before replacing the old index. Retain the original failed fixture as evidence. Standalone LibraDex salvage without an external source is a separate design problem; do not silently promise it or automatically mutate user databases.

## Implementation boundary

This document is the requested cross-shape review and BLX recommendation. Production changes, new regression execution, performance measurement, and migration/rebuild acceptance remain open. The next implementation step is the common invariant regression corpus, followed by the five ordinary fixed-scalar publishers, owner-aware SV and terminal work, then complete fourteen-family parity and Meridian validation.
