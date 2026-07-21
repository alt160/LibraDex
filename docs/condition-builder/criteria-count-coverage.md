# Criteria Count Coverage

This matrix tracks whether public `.Count()` / `.Count(where)` reaches a physical count primitive instead of materializing identities or walking tuples.

Legend:

- 🟩 [x] Proven optimized: public count reaches a count primitive that avoids identity materialization.
- 🟨 [~] Partial: a physical primitive exists, but public count still has reader/residual work or only some criteria are optimized.
- 🟥 [!] Gap: no shape-native criteria count path is proven for public `.Count(where)`.
- ⬜ [ ] Not audited yet.

## Public Count Spine

| Area | Status | Current path | Notes |
| --- | --- | --- | --- |
| Public `.Count()` no condition | 🟩 [x] | `LibraDexIndex.Count()` / facade count paths call ordinary count primitives. | Generic scalar `CountOrdinaryIdentityObjects()` uses routed metadata count for `SS88`, `SS168`, `SS816`, `SS1616`, `FS328`, and `FS3216`. String/varlen facades use key-state metadata plus routed ordinary counts. |
| Public `.Count(where)` leaf | 🟨 [~] | `LibraDexIdentityExecutionPlanner.Count()` first tries `IIdentityPrimitiveAggregateExecutor`. | Leaf counts can be specialized. Generic scalar ordered criteria now use direct count primitives for `Find`, `Between`, before/after, membership, and multirange fan-out; residual predicate shapes still scan. |
| Public `.Count(where)` composed `And` / `Or` / `Not` | 🟨 [~] | Preserve-duplicate `Or` recursively sums child counts, so physical leaf aggregate counts remain active under disjunction. `And`, non-empty `Except`, and non-empty `Not` still perform identity-membership work where required by current condition semantics. | Central same-index range-union counting is intentionally not used because preserve `Or` is stream concatenation and `Distinct` is caller-selected afterward. Physical composition is only safe where identity multiplicity across keys is proven irrelevant. |
| Distinct count / sum / average style aggregates | ⬜ [ ] | Not implemented as shape-native aggregates. | The primitive aggregate interface is a good spine for this later. |

## Shape Matrix

| Shape / facade | Count all | Find / Between | Before / After | Prefix / StartsWith | In / MultiRange | Residual predicates | Public `.Count(where)` status |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `LibraDexStringScalar8Index` over `VS8` exact string | 🟩 [x] Key-state metadata + `VS8` ordinary count. | 🟩 [x] Encoded `VS8` range count. | 🟨 [~] Uses encoded range count when inclusive; exclusive edges can use key filters. | 🟩 [x] `Prefix` criteria reaches `CountEncodedIdentityPrefix()` and measured `fallbackToRange=0` for path-like compressed routers. | 🟩 [x] Membership count dedupes transformed operands and counts one exact-key/key-state route per distinct value. | 🟨 [~] Candidate ranges prune, then key filter scans encoded keys. | 🟩 [x] Strongest currently proven criteria-count path. |
| `LibraDexStringScalar8Index` folded / sort-key projections | 🟨 [~] Same `VS8` backing where projection exists. | 🟨 [~] Uses same core count after transform. | 🟨 [~] Same edge-filter caveat. | 🟨 [~] Prefix routes through transformed projection when condition materialization selects it. | 🟩 [x] Folded/sort-key membership count dedupes transformed operands; sort-key dedupe uses structural `byte[]` equality. | 🟨 [~] Projection/residual matching can require key checks. | 🟨 [~] Needs targeted perf proof per projection policy. |
| `VarKeyScalar8Index` raw `VS8` | 🟩 [x] `CountVarKeyScalar8Identities()`. | 🟩 [x] `CountVarKeyScalar8IdentityRange()`. | 🟩 [x] Via full lower/upper encoded range, when called directly. | 🟩 [x] Internal prefix planner exists. | 🟨 [~] Fan-out must be audited at public facade level. | 🟨 [~] Residual key filters scan keys. | 🟩 [x] Physical primitive is strong; public exposure depends on facade. |
| `VarKeyScalar16Index` raw `VS16` | 🟩 [x] `CountVarKeyScalar16Identities()`. | 🟩 [x] `CountVarKeyScalar16IdentityRange()` uses contained-route metadata counts with narrow shelf scans only for boundary targets. | 🟩 [x] Same range primitive, via full lower/upper encoded bounds. | 🟩 [x] Internal prefix planner exists and mirrors the `VS8` compressed-router containment model. | 🟨 [~] Fan-out not audited at public facade level. | 🟨 [~] Residual filters scan keys. | 🟩 [x] Physical criteria-count primitive is aligned with `VS8`; facade fan-out remains a separate audit. |
| `VarKeyVarIdentityIndex` / `VV` | 🟩 [x] `CountVarKeyVarIdentityIdentities()`. | 🟩 [x] `CountVarKeyVarIdentityRange()` uses contained-route metadata counts with narrow shelf and terminal-root scans only for boundary targets. | 🟩 [x] Same range primitive, via full lower/upper encoded bounds. | 🟩 [x] Internal prefix planner exists and mirrors the `VS8` / `VS16` compressed-router containment model. | 🟨 [~] Fan-out not audited at public facade level. | 🟨 [~] Residual filters scan keys. | 🟩 [x] Physical range and prefix counts are shape-native; public aggregate fan-out remains a separate audit. |
| Generic scalar `SS88`, `SS168`, `SS816`, `SS1616` | 🟩 [x] Generic `CountOrdinaryIdentityObjects()` uses routed metadata count. | 🟩 [x] Public ordered criteria dispatch to shape-native count ranges with volatile route-target aggregate caching for fully covered middle prefixes. | 🟩 [x] Before/after translate to the same direct ordered-count ranges. | N/A | 🟩 [x] Membership dedupes operand keys; multirange sorts/merges overlapping extents; final extents use direct ordered-count dispatch. | 🟨 [~] Structured/guid/binary/bitmask paths are classified key scans. | 🟩 [x] Direct ordered counts are proven for scalar 8/16 key and identity combinations, including duplicate membership and duplicate generated multirange fan-out. |
| Generic fixed32 scalar `FS328`, `FS3216` | 🟩 [x] Generic `CountOrdinaryIdentityObjects()` uses routed metadata count. | 🟩 [x] Public ordered criteria dispatch to fixed32 count ranges with volatile route-target aggregate caching for fully covered middle prefixes. | 🟩 [x] Before/after translate to the same direct ordered-count ranges. | N/A | 🟩 [x] Same generic membership/multirange count fan-out as scalar shapes, with structural `byte[]` membership equality and encoded fixed32 range ordering. | 🟨 [~] Pattern/slice predicates scan keys. | 🟩 [x] Direct ordered counts are proven for fixed32 key with scalar-8 and scalar-16 identities; fan-out shares the generic count layer. |
| `FixedNScalar8Index`, `FixedNScalar16Index` | 🟩 [x] `CountOrdinaryIdentities()` exists. | 🟩 [x] `CountIdentityRange()` uses routed contained-route metadata counts with narrow shelf scans only for boundary targets. | 🟩 [x] Can be expressed as full-bound ranges. | N/A | 🟨 [~] Public fan-out not audited. | 🟨 [~] Residual predicates not metadata-only. | 🟨 [~] Physical primitive is optimized; public facade use needs proof. |
| `FixedNVarIdentityIndex` | 🟩 [x] `CountOrdinaryIdentities()` exists. | 🟩 [x] `CountIdentityRange()` exists. | 🟩 [x] Can be expressed as full-bound ranges. | N/A | 🟨 [~] Public fan-out not audited. | 🟨 [~] Residual predicates not metadata-only. | 🟨 [~] Physical primitive is present; public facade use needs proof. |
| `Scalar8VarIdentityIndex`, `Scalar16VarIdentityIndex` | 🟩 [x] `CountOrdinaryIdentities()` exists. | 🟩 [x] `CountIdentityRange()` exists; public `UInt64` facade fan-out uses it. | 🟩 [x] Public `UInt64` before/after criteria expand to direct range counts. | N/A | 🟩 [x] Public `LibraDexUInt64VarIdentityIndex` membership count dedupes typed arrays, prepared sets, and scalar operands; internal multirange count sorts/merges overlapping extents before range counts. | 🟨 [~] Residual predicates not metadata-only. | 🟩 [x] UInt64 var-identity count fan-out is proven for membership, before/after, and multirange primitives. |
| `LibraDexBigIntScalar8Index` fixed-width | 🟩 [x] Delegates to `FixedNScalar8/16.CountOrdinaryIdentities()`. | 🟩 [x] Delegates to fixed `CountIdentityRange()`. | 🟨 [~] Needs public before/after audit. | N/A | 🟩 [x] Membership count dedupes BigInteger operands; multirange count sorts/merges overlapping ranges before fixed-N range counts. | 🟨 [~] Residual predicates not metadata-only. | 🟩 [x] BigInt scalar membership is public-condition proven; multirange is primitive-leaf proven. |
| `LibraDexBigIntScalar8Index` variable-width | 🟩 [x] Delegates to `VS8.CountOrdinaryIdentities()`. | 🟩 [x] Delegates to `VS8.CountEncodedIdentityRange()`. | 🟨 [~] Needs public before/after audit. | N/A | 🟨 [~] Fan-out not perf-proven. | 🟨 [~] Residual predicates not metadata-only. | 🟨 [~] Uses strong `VS8` range primitive but no BigInt-prefix concept. |
| `LibraDexBigIntVarIdentityIndex` | 🟩 [x] Delegates to `FixedNVarIdentity.CountOrdinaryIdentities()`. | 🟩 [x] Delegates to `FixedNVarIdentity.CountIdentityRange()`. | 🟨 [~] Needs public before/after audit. | N/A | 🟩 [x] Membership count dedupes BigInteger operands; multirange count sorts/merges overlapping ranges before fixed-N var-identity range counts. | 🟨 [~] Residual predicates not metadata-only. | 🟩 [x] BigInt var-identity multirange is primitive-leaf proven. |
| `LibraDexRoutedCompositeIndex` | ⬜ [ ] | Count-all and criteria count require separate composite audit. | ⬜ [ ] | ⬜ [ ] | Prefix depends on component shape/projection. | ⬜ [ ] | Composite residual planning likely matters. | ⬜ [ ] Not audited in this pass. |

## Immediate Gaps

1. 🟨 [~] Membership and multirange fan-out is fixed for generic scalar/fixed32, string, public UInt64 var-identity, and BigInt count facades; remaining raw fixed-N/fixed-var public surfaces need proof only where they become condition-builder executors.
2. 🟨 [~] Composed condition counts now preserve leaf aggregate counts under preserve-duplicate `Or` and short-circuit empty membership sides, but `And`, non-empty `Except`, and non-empty `Not` still need shape/index-aware aggregate planning to avoid identity scans safely.
3. ⬜ [ ] Composite indexes need a separate audit because key-prefix/range semantics are component-aware.

## Composed Count Multiplicity Audit

BLX result:

1. 🟥 [!] Central same-index range-union counting is not currently safe for identity-set semantics.
   `IndexKeys.Unique` proves only that one key maps to at most one identity. It does not prove that one identity appears under at most one key in the same index. If the same identity is stored under keys `10` and `11`, then `EqualTo(10).Or.EqualTo(11)` with `IdentityDeduplication.Distinct` should count one identity, while a physical tuple/range count would count two.
2. 🟩 [x] Preserve-duplicate `Or` can safely sum child counts.
   Current preserve semantics concatenate left and right streams, so summing child counts is semantically equivalent and keeps leaf aggregate fast paths active inside disjunctions.
3. 🟨 [~] `And`, distinct `Or`, non-empty `Except`, and non-empty `Not` need identity-membership semantics unless the planner has a stronger contract.
   A same-index `And` is especially subtle: identity-set intersection can match an identity that appears under one key satisfying the left branch and a different key satisfying the right branch. Intersecting key ranges would answer a different tuple-level question.
4. ⬜ [ ] Required future proof contract: an enforced index-level identity-key multiplicity policy.
   The missing durable fact is whether an identity can be associated with more than one key inside the same logical index or projection. A useful future public/internal contract would distinguish the current permissive behavior from a `SingleKeyPerIdentity`-style enforced mode. The planner should only use physical tuple/range cardinality for identity-set compositions when that mode is enforced by insert, delete, rekey, batch, projection, and reopen paths.
5. ⬜ [ ] Required planner guard: physical composition must opt in by proof, not by heuristic.
   Do not infer safety from key uniqueness, scalar identity width, projection kind, or same-index reference alone. The proof must come from enforced metadata plus a shape path whose physical primitive counts the same semantic unit requested by the condition terminal.

## Current Best Next Target

🟩 [x] Completed target: generic scalar/fixed32 membership and multirange count fan-out now dedupes duplicate keys, merges overlapping ranges, and dispatches merged extents through direct ordered-count primitives.

Next best target: design and enforce an identity-key multiplicity contract before adding same-index physical composition counts for distinct `Or`, `And`, `Except`, or `Not`.
