# Scenario-driven shelf split review and BLX

2026-09-17. Design review only. Production code and datasets are unchanged.

This extends and qualifies `shelf-route-ownership-repair-review.md`: parent-alias cleanup is a repair primitive, not a complete split-selection policy. The prior fourteen-family review remains a source inventory, not runtime acceptance.

## Evidence and notation

- **C**: current-source trace; code path or format constraint inspected.
- **M**: executed standalone routing model in `scenarios/shelf-route-scenario-model.mjs`.
- **L**: contrived lxl design walk-through; expected behavior, not engine execution.
- **R**: previous retained Meridian reproduction, not rerun here.
- All new production regression cases below remain **open**. Model checks passing means the stated property or counterexample was reproduced in the model, not that LibraDex passed it.

Keys below are unsigned hexadecimal byte sequences, with omitted fixed-key suffix bytes chosen as distinct identities/keys as needed. Toy capacity C=4 makes transitions visible; executable engine fixtures must use actual profile capacities and byte-fit checks. Tuple order is full key then identity. Variable-length key equality includes length, even when routing pads absent bytes with zero.

## Correctness contract: effective ownership

For target T, distinguish:

- P(T): tuples currently stored in T.
- D(T): all keys actually delivered to T by every incoming path, intersected with ancestor restrictions. Include exact matches, range sharing, nearest-route fallback, and multiple owners.

A common prefix of P(T) does not prove a common prefix of D(T). A deeper router may omit a byte only when the effective incoming domain proves that byte, or when publication changes the domain accordingly. For compressed routes, the stored stem alone is not this proof.

A legal plan must preserve every existing tuple exactly once in the logical result; maintain global key/identity order; keep every admitted future key routable or intentionally unassigned to a supported lazy-creation path; preserve unrelated siblings; respect all reader/writer capabilities; and publish coherent topology/cache state. Existing records outside the proposed new domain must be repartitioned, never made unreachable by clearing a route.

## Concrete scenario matrix

| ID | Contrived starting state and follow-up | Current evidence / trap | BLX disposition and required oracle |
| --- | --- | --- | --- |
| S01 | Parent byte-0 routes 20 and 21 share full S: 20:10,20:20,21:10,21:20; insert 21:18. | C/L: existing parent-depth partition can distinguish the groups; a deeper transform is unnecessary. | Split into 2/3 tuples through the parent. Verify full order and future 20:FF and 21:00. |
| S02 | Parent maps 20..21 to S; all C+1 tuples are 20:10,20:20,20:90,20:A0,20:B0. Then insert 21:00. | C/M01/R analogue: retaining 21 while converting S to a byte-1 router puts 21:00 in the left partition. | Narrow effective parent ownership plus child split, or reconsume byte 0. Compare eligible parent widening separately. |
| S03 | Mirror S02 with parent aliases 1F..20; after transform insert 1F:FF. | L: a fix checking only higher siblings is insufficient. | Lower and upper aliases must both be reconciled. Verify min/max and all tuples. |
| S04 | Parent has 20->S,21->U,22->S. S currently owns only 20 keys. | C/L: clearing an assumed contiguous interval can damage U; some VS paths explicitly reject noncontiguous aliases and use owner-set handling. | Modify only references to S, or reject the narrow publisher and use owner-set repair. U stays byte-for-byte/logically unchanged. |
| S05 | Same full shelf reached from two parents; only one parent's aliases are refined. | M09: the other parent still reaches the transformed target. | Prove single ownership or reconcile the entire owner set. A local helper must have an explicit precondition. |
| S06 | Already multi-byte-aware parent, width 2, route 3C:[00..FF]->S and unrelated 50:[00..FF]->U; S splits at 3C:7C. | C/L/M04: same-width replacement by 3C:[00..7B] and 3C:[7C..FF] is representable without another level. | Strong candidate when route capacity and all consumers permit it. Preserve U and test unseen 3B,3D,4F,51 stems plus subsequent splits. |
| S07 | S06 but implementation stores only observed extrema: 3C:[20..3F]->L and 3C:[80..9F]->R. Insert 3C:10. | C/M03: exact search misses, stem-only fallback chooses R, although the key sorts before L. | Reject min/max-only route construction. Define complete effective intervals, not just bounding existing tuples. This is a counterexample to a proposed encoding, not a newly reproduced production publisher. |
| S08 | One-byte parent has all 256 prefix mappings. Widen to width 2; one mapping becomes two ranges, others need one complete final-byte range each. | C/M06: 257 physical entries need 2,891 bytes with header, within 4,096 bytes. | Physically viable candidate, NOT automatically supported: check configured capacity, every consumer, fallback domains, and scan cost. |
| S09 | Width-2 parent has two distinct stems, each covering final bytes 00..FF; widen to width 3 while preserving those full domains explicitly. | C/M06: each old ranged byte becomes an exact stem byte: 512 entries, 6,208 bytes, exceeding one page. | Reject this direct encoding. Consider local child split or broader parent restructuring. A more compact fallback-based encoding needs its own proof, not an assumption of equivalence. |
| S10 | Parent has physical room but RouteCount == MaxRouteCount; add one split entry. | C/L: configured capacity and physical capacity differ; changing MaxRouteCount moves the stem table. | Rebuild the complete router image with validated capacity, or choose a child plan. Do not append over stem storage. |
| S11 | Fixed-scalar byte-only walker encounters S06's multi-byte parent; lookup 3C:90. | C/M05: byte-only lookup compares 3C against final-byte ranges and chooses L; full-key lookup chooses R. | Multi-byte plan is ineligible for that consumer today. Adoption requires all point/range/count/mutation/cache paths, not merely a writer change. |
| S12 | Dense hot direct parent versus sparse compressed parent, each eligible to encode a split. | C/L: direct lookup is indexed; current compressed lookup scans routes, and a miss can scan again. | Compare CPU probes and cache behavior as well as depth/pages. Fewer levels alone cannot select the winner. |
| S13 | Long common stem 20:AA:BB:CC, distinguish at next byte; later insert 20:AA:BC:00. | C/L: converting the common stem into an unguarded skip admits the wrong intermediate prefix. | Exact chain, supported compressed stem, or safe parent refinement. Test divergence at every omitted byte, not only immediate parent. |
| S14 | A child chain approaches the configured maximum router hop count; add another guard. | C/L: walkers enforce a hop limit; extra guard levels are not free. | Count router hops separately from consumed bytes. Reject an unrepresentable plan or adopt a tested compatible compressed/restructured path. |
| S15 | C+1 tuples have identical complete keys but distinct identities. Then insert neighboring key. | C/L: key-byte partition cannot separate the duplicate run; identity storage is the growing dimension. | Use supported terminal/duplicate/overflow handling; validate subsequent key separation before/at/after consumed depth. |
| S16 | Variable keys 3C and 3C:00, plus empty key and zero-leading variants. | C/M08: missing routing bytes are padded zero; routing equivalence does not prove key equality. | Use complete length-aware key comparison before terminal promotion; no endless deeper-byte search. Include logical null separately from empty bytes. |
| S17 | Large variable identities fill a shelf even though few keys exist; two equal-count partitions differ greatly in bytes. | L: count balance is not space balance for variable payloads. | Evaluate actual encoded bytes and legal capacities; bounded growth, byte-balanced split, or overflow as supported. Do not borrow a scalar median blindly. |
| S18 | Append-heavy shelf; balanced split repeatedly rewrites the older half. Then reverse insertion direction. | C/L: sorted-tail paths exist, but monotonic workload is not permanent ownership. | Compare legal tail split versus balanced split; maintain fallback correctness for out-of-order keys and report utilization/write amplification. |
| S19 | Delete most keys after a broad-range split; remaining records share one prefix; refill and transform. | L: deletes narrow P(T), not necessarily D(T). | Apply the same incoming-domain proof as S02; do not infer routing ownership from current occupancy. |
| S20 | Terminal created under a range-shared parent; next key differs in an already-consumed byte. | C/L: fixed terminal mismatch helper can reject divergence at/before parent depth; deeper chain creation alone does not repair aliases. | Terminal promotion and demotion need ownership-aware handling and exact key equality. Test all identity widths. |
| S21 | Two different child splits prepare against the same parent; each changes a different slot. | M10: publishing the second stale complete parent image loses the first edit. | Revalidate/merge from current parent under publication exclusion or retry by version. Then test the actual engine schedule. |
| S22 | Writer has staged S as a shelf when another operation turns S into a router. | C/L: writer-context route/target-kind checks exist. | Verify rejection/retry without overwriting the router; cover every supported mutation entry family. |
| S23 | Parent/child changes occur within a deferred durability batch; warm reader/cache accesses them before final commit. | C/L: targeted invalidation is required by the existing deferred-commit contract. | Invalidate every changed page/view; test same-session, publication, reopen, and supported fault boundaries independently. |
| S24 | Old index already has mixed-prefix keys inside a too-deep subtree, as in Meridian. | R/L: clearing aliases can strand already misrouted data. | Rebuild from authoritative records, or design explicit verified salvage separately. Preventive repair is not retrospective repair. |

This is a distinguishing scenario set, not exhaustive state-space proof. Cross it with the fourteen-family applicability table in the earlier review, and with direct/queued/serialized/batch/sorted-tail entry paths where supported. Add pairwise combinations: shared owners + terminal promotion; compressed parent + late intermediate prefix; deletion + same-parent split; concurrency + deferred invalidation. Any new counterexample expands the corpus before finalizing the corresponding branch.

## Detailed lxl traces driving the decision

### A. Why alias cleanup is necessary but not the first split decision

1. In S01, sorted tuples first differ at byte 0. The existing parent examines byte 0. Its aliases can be partitioned between shelves without adding a level.
2. `TryChooseScalar16Scalar8TransformSplitRightPrefix` checks earlier bytes and a monotone transition at the requested depth; a valid byte-0 boundary is available here.
3. S02 has no byte-0 transition in its current tuples. A byte-1 split is viable, but the parent also admits byte-0 value 21.
4. The unsafe transform partitions on byte 1 and rewrites S in place. Parent 21 still points there. Later 21:00 chooses L, despite sorting after 20:B0 in R.
5. Therefore selection must first try a legal parent-depth split. Ownership refinement becomes necessary when choosing the deeper branch; it is not a universal replacement for split planning.

### B. User's two-multi-byte-routes branch

1. S06's parent already consumes two bytes and its readers understand that representation. Replace one complete final-byte range with two adjacent ranges, preserving all other slots and reserving the new configured capacity if needed.
2. 3C:10 selects L; 3C:90 selects R. The standalone model exhaustively checks all 65,536 two-byte keys for the simpler two-route layout and finds no target-order inversion.
3. `RouterReader.FindTarget(fullKey, depth, ...)` does not stop on an exact-stem miss. It calls `FindNearestMultiByteRoute`. Thus 3B:FF selects L and 3D:00 selects R when there are only those two entries.
4. Those off-stem keys are part of the effective ownership domain unless ancestors exclude them. A later transform of L or R cannot assume the stored stem is its complete ownership proof.
5. With another sibling stem, recompute the fallback intervals across all entries. Do not narrow ownership based solely on the two edited route records.
6. S07 shows why using actual min/max keys as sparse route ranges is not equivalent: missing 3C:10 falls through both same-stem entries, then nearest-stem fallback returns the last one, R. This is a precise rejection of that proposed encoding.
7. This branch remains valuable, but needs full domain preservation and consumer compatibility. It is not universally superior or universally unavailable.

### C. Parent widening can win or lose

1. Router prefix width is global. Each compressed route stores width-1 exact stem bytes and a final-byte range; it cannot wildcard an earlier stem byte.
2. Physical required bytes are `64 + MaxRouteCount * (10 + width - 1)`. Configured capacity, not just current route count, determines reserved stem storage.
3. S08's explicitly represented one-to-two-byte conversion fits: 257 entries occupy 2,891 bytes. This does not prove every consumer accepts 257 routes or that linear lookup cost is acceptable.
4. S09's two-to-three-byte conversion requires 512 entries if both full old final-byte domains are explicitly preserved: 6,208 bytes. It fails the page-capacity gate.
5. Alternatives are to split locally below the parent, restructure a larger parent region, or prove a different effective-range encoding. Do not truncate sibling coverage to make the page fit.
6. Even when widening fits, SS16's current route walker extracts one byte and invokes the byte overload. S11 demonstrates misselection after an unsupported conversion. Storage support does not imply complete operation support.

### D. Shared owners and publication

1. A shelf reachable through P and Q has the union of both domains. Removing one bad alias in P does not alter Q.
2. A planner must either have a proved single-parent invariant, discover the complete owner set on the structural path, or select a representation valid for all owners. This does not justify scanning the whole graph on every ordinary insert.
3. Publication revalidates current ownership. Two child plans prepared from the same parent snapshot cannot each overwrite that snapshot independently.
4. Existing writer-context claims and targeted invalidation are mechanisms to reuse, but source inspection is not concurrency or crash proof. S21-S23 remain executable acceptance requirements.

## BLX outcome: a conditional repair portfolio

### Eligibility gates, before cost comparison

Reject candidates that lose tuples, change unrelated ownership, rely on unrepresented incoming prefixes, exceed byte capacity, cannot handle equality/length, violate supported route formats or hop limits, omit an owner, or cannot use the existing publication contract safely. A faster illegal representation is not a candidate.

### Candidate selection

1. **Existing parent, same representation:** prefer a valid same-depth or same-width multi-byte partition when it needs bounded local work and retains supported reader semantics. This includes the user's example.
2. **Parent conversion/widening:** retain as a candidate when complete sibling-domain encoding fits and all consumers support it. Compare affected fanout, lookup probes, page writes, and expected subsequent expansion. Do not assume it wins merely because it removes a level.
3. **Local child split:** for direct scalar paths with unsupported/expensive widening, use exact ownership refinement plus a child representation already supported by that shape. This is the targeted Meridian correction branch, not the entire architecture.
4. **Parent-byte reconsumption or owner-set replacement:** retain where shared/range ownership makes it appropriate; account for extra pages/hops and existing FSN/VS/VV semantics. Use complete owners for SV families.
5. **Growth/redistribution/tail split:** evaluate only where the shape supports it and actual byte utilization warrants it. Keep out-of-order fallback correct; do not grow indefinitely to avoid deciding.
6. **Duplicate/terminal handling:** choose based on complete key equality, with valid ownership on both entry and exit from terminal representation.

No arbitrary universal cost weights or public tuning knobs are proposed. Start with concrete typed structural branches and a compact ownership/representation description; a general planner framework is not a prerequisite. Eliminate clearly dominated candidates (same correctness, more hot lookup work and no compensating publication/storage benefit). Where read/write or contention tradeoffs remain, measure matched workloads before fixing internal thresholds. Ordinary non-splitting operations should not enumerate these candidates.

### Repair workstreams resulting from the scenarios

- **R1: fixed-scalar ownership publication.** Extend the SS8-style single-owner refinement to missing narrow and fallback paths, after preserving parent-level split eligibility. Include terminal transitions.
- **R2: shared-owner and skipped-stem publication.** Address SV8/SV16 with complete effective ownership rather than a single-parent helper. Preserve existing ownership-aware FSN/VS/VV strategies unless tests identify a defect.
- **R3: multi-byte route eligibility and evolution.** Explicitly account for nearest fallback domains, gaps, configured capacity, sibling expansion, and all consumer capabilities. Same-parent splitting is a first-class candidate; broader fixed-scalar multi-byte adoption is not a one-method repair and must not be silently bundled in as safe.
- **R4: acceptance corpus and old-index handling.** Convert distinguishing cases into engine regressions before declaring a fix complete; retain/rebuild damaged indexes by an explicit verified workflow.

These are complementary, not four mandatory architectural rewrites. R1 alone can address the known Meridian path, but cannot be labeled systemic closure until applicable R2/R3 cases and all-family tests are satisfied.

## What ran and what did not

On 2026-09-17 at 16:15 America/Phoenix, Node ran `docs/scenarios/shelf-route-scenario-model.mjs`: 10 checks passed (including expected counterexamples). M04 and M07 each enumerate the full 65,536-key two-byte domain. The model covers routing semantics, capacity arithmetic, and abstract ownership/publication schedules. It does not load LibraDex, allocate shelves, test its locks, persist/reopen an index, or measure throughput. These limits are printed by the script.

Source anchors: `Views/RouterReader.cs` (byte lookup ~105, full-key lookup ~140, fallback ~359, range selection ~250); `Views/RouterWriter.cs` (multi-byte format validation ~242); `Layouts/RouterLayout.cs` (~258); `FileSession/LibraDexFileSession.cs` (SS16 walker ~18966, split-boundary choice ~29897, VS8 selector ~35537, VS16 selector ~36742). Both inspected variable-key selectors currently choose expanded one-byte or one-byte chains, not their available compressed alternative.

No C# build, engine regression, performance benchmark, or production correction was performed in this scenario-design pass. Next: implement red engine tests for S01/S02/S06/S07/S11/S15/S20 and shared-owner cases, then expand the matrix across families and entry paths while applying the selected repair primitives.

### Subsequent performance evaluation

The later isolated C# routing/metadata benchmark is documented in [shelf-route-performance-evaluation.md](shelf-route-performance-evaluation.md). It measures thread cycles, latency, allocations, and generated calls against unchanged router source plus narrow isolated prototypes. It supports structural-time ownership refinement for the existing direct path and keeps tiny compressed routers as a conditional alternative; it does not constitute production split or all-family engine acceptance.
