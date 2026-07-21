# VS8 Canonical Counted Tree Design

> 🟥 [!] **Rejected 2026-07-13:** this BLX branch overgeneralized a failed route-containment count shortcut into a reason to replace VS8's core topology. LibraDex's active direction is the native ordered shelf-scoop design in [vs8-scoop-range-count-design.md](vs8-scoop-range-count-design.md). Retain this document only as the evaluated B+ alternative; it does not describe an approved or active replacement.

## Status

- 🟥 [!] Architecture rejected after restoring the core LibraDex scoop invariant to the analysis.
- 🟩 [x] Current topology and failed metadata-count experiment diagnosed.
- 🟩 [x] Canonical ownership, count, traversal, mutation, and durability invariants defined.
- 🟩 [x] Concrete insert, split, exact, range, count, duplicate, and delete LXLs defined.
- 🟥 [!] Production implementation stopped; do not implement this branch.
- ⬜ [ ] Persisted byte offsets and measured page profile finalized.
- ⬜ [ ] Implementation, parity gates, and isolated performance proof completed.

This document is a design checkpoint. It does not authorize production-code changes.

## Rejected Proposal

Replace the VS8 routed shelf graph with a counted, prefix-compressed B+ tree specialized for variable-length byte keys and fixed eight-byte identities.

The replacement is not a compatibility layer over the present router graph. VS8 gets a new persisted format with canonical parent/child ownership. R&D files using the old VS8 format may be rebuilt; an open attempt against an old format must fail explicitly rather than silently translate or fall back. Public API and harness semantics remain stable.

The same physical model is the leading later replacement for VS16 and VV, but VS8 is the reference implementation and proof lane. Sharing must not add deep generic call chains or obscure allocation and ownership behavior.

## Why The Current Topology Fails

The current VS8 root begins as a one-byte router. Shelf transforms can publish many prefix intervals to the same left or right shelf, while one exact stem continues to another router. That aliasing is useful for cheap route publication but destroys canonical logical ownership:

- one physical shelf can be reached through multiple logical intervals;
- one target count describes physical contents, not the portion owned by a selected route;
- route containment does not prove target-content containment;
- a range traversal must retain visited-target state and conservatively inspect descendants;
- ordinary `Count` therefore opens the range reader and loads the applicable shelf extents.

The focused ShapeBench row made the cost visible: VS8 `count-range-api`, batch `5000`, thread `1`, measured `347 ops/s` versus SQLite `17,699 ops/s` (`0.02x`). Wiring the existing target-metadata counter proved that the speed is recoverable but the topology is not count-safe: parity held through the small pre-transform cases, then failed after shared targets appeared, including both overcount and undercount cases. The wiring was reverted and the reader-backed count restored parity.

## BLX Branch Comparison

| Branch | Correctness | Exact lookup | Range/count | Mutation | Decision |
|---|---:|---:|---:|---:|---|
| Keep aliases and compensate during each query | Difficult to prove; requires reconstructing logical ownership | Unchanged | Still graph traversal and deduplication | Low rewrite | 🟥 [!] Reject; preserves the root cause |
| Keep aliases and add target count/min/max metadata | A shared target can represent disjoint intervals, so metadata cannot apportion its rows | Unchanged | Fast only for accidentally safe shapes | Moderate | 🟥 [!] Reject; metadata becomes a correctness band-aid |
| Reverse-map aliases into canonical interval records | Can be made correct with a second ownership structure | Unchanged | Adds another index that must stay transactionally synchronized | High | 🟥 [!] Reject; two truths for one tree |
| Strict path-compressed radix tree with counted children | Correct and canonical | Strong | Strong | Byte-depth split/path compression remains complex | 🟨 [~] Next-best branch |
| Counted prefix B+ tree with linked leaves | Correct and canonical | Strong, shallow fanout search | Best fit: one lower seek, sequential leaves, exact subtree counts | Familiar local split and ancestor delta | 🟩 [x] Select |

The strict radix branch is the fallback if measurement proves separator storage or internal-node comparison cost materially worse than expected. It does not justify retaining aliases.

## Persisted Shape

### Internal Node

Each internal node owns one ordered, disjoint key interval and contains:

- format magic, format version, node kind, header size, page extent size, and node version;
- item/child count and live subtree identity count;
- optional common prefix bytes shared by its separators;
- a high fence and right-sibling offset for structural validation/recovery;
- a compact sorted slot directory;
- variable-length separator payloads;
- for each child: child offset and exact subtree identity count.

Separator `s[n]` is the lowest boundary routed to child `n + 1`. Child `0` owns keys below `s[0]`; child `n` owns keys greater than or equal to `s[n - 1]` and below `s[n]`; the last child owns keys greater than or equal to the last separator and below the node high fence. A separator is the shortest encoded byte prefix that still satisfies `max(left) < separator <= min(right)`. A full first key of the right child is valid during the first correctness implementation; shortening is a layout optimization only after separator invariants are proven.

### Leaf Node

Each leaf contains:

- the same core format/version/extent/version fields;
- tuple-slot count and live identity count;
- low/high fences and the next-leaf offset;
- a compact sorted slot directory;
- variable-length key records and fixed eight-byte identities;
- optional duplicate-run descriptors for keys whose identity run cannot remain local.

Records sort by encoded key and then encoded identity. All identities for one key are contiguous. The ordinary case remains direct tuples. A key that cannot fit in one leaf becomes one leaf entry pointing to an ordered identity overflow chain with an exact identity count; the tree never creates several indistinguishable separator entries for one duplicate key.

### Page Profile

The architecture does not hard-code a guessed page size. Start the proof sweep with `16KiB`, `24KiB`, `32KiB`, and `48KiB` leaves and `4KiB`, `8KiB`, and `16KiB` internal nodes. Reject `64KiB` as a default candidate unless VS8-specific evidence reverses the repository's existing broad `64KiB` write cliff. Select one default from repeated parity-guarded write, exact, range, count, allocation, and bytes-per-item results.

## Required Invariants

1. **Disjoint ownership:** every non-root node has exactly one logical parent, and sibling key intervals do not overlap.
2. **Total coverage:** a parent's child intervals cover the parent's interval without gaps.
3. **Separator truth:** each separator routes every possible key to exactly one child and is consistent with the adjacent child fences.
4. **Exact counts:** each child count equals the number of live identities in that entire child subtree; each node total is the sum of its child counts or leaf entries.
5. **Ordered leaves:** leaf keys are globally ordered through `nextLeaf`; the last leaf has no successor.
6. **Duplicate ownership:** all identities for one encoded key have one leaf owner, even when identity payload spills into an overflow chain.
7. **Path validation:** a mutation publishes only if every captured `(nodeOffset, childOrdinal, nodeVersion)` still names the traversed path.
8. **Atomic visibility:** readers observe either the pre-publication tree or the post-publication tree, never a parent/count/link mixture.
9. **Root truth:** the directory points to one root; a root split publishes the new root and its total count atomically.
10. **No fallback truth:** exact counts never fall back to stale, approximate, sampled, or alias-derived metadata.

## Read Algorithms

### Exact

Walk internal nodes with an upper-bound separator search, retaining no graph visited set. Binary-search the selected leaf by key and identity. A duplicate-run entry searches or streams its identity chain only for that exact key.

### Range Materialization

Seek the lower bound once. Enumerate qualifying tuples to the end of that leaf, then follow `nextLeaf` until the high bound is passed. Allocation must be proportional to caller-requested disconnected output; cursor iteration owns only bounded traversal state.

### Range Count

At each internal node:

- add a child count without reading that child when the child interval is wholly inside the requested inclusive range;
- skip it when wholly outside;
- descend only into lower and upper boundary children.

At leaves, binary-search the two slot boundaries and sum tuple/duplicate-run identity counts. Work is proportional to tree height plus boundary fanout, not result cardinality or total shelf count.

### Prefix

Encode prefix selection as `[prefix, successor(prefix))`. If no finite successor exists, use an unbounded upper fence. Prefix materialization and count then use the same range algorithms.

## Mutation And Publication

Traversal returns a compact path of `(nodeOffset, childOrdinal, nodeVersion)` entries. A write context owns mutable images only for leaves and nodes it changes. Multiple operations in one context coalesce signed identity deltas per touched child and parent, so a node is rewritten once per publication rather than once per tuple.

### Insert Without Split

1. Seek the owning leaf and capture the validated path.
2. Insert the tuple into the sorted slot directory or append to the key's duplicate run.
3. Add one to the leaf count and to each captured child count.
4. Validate path versions.
5. Publish the leaf dirty ranges and coalesced ancestor count fields in one durability batch.

### Leaf Split

1. Choose a byte-balanced split boundary that does not divide a duplicate key.
2. If one duplicate key alone is oversized, convert that key to a duplicate-run descriptor instead of forcing a false separator.
3. Reserve and populate the right leaf.
4. Rewrite the left leaf's high fence and successor to point at the right leaf; the right leaf inherits the former successor/high fence.
5. Derive the separator and exact left/right identity counts.
6. Insert the separator and right child into the parent; replace the left child count.
7. Recursively split full internal nodes. If the root splits, reserve a new root.
8. Revalidate the captured path and atomically publish all new extents, link/fence changes, parent records, counts, and optional root-directory update.

### Delete, Redistribution, And Merge

Delete removes the exact tuple or duplicate identity and applies `-1` through the path. Underfull leaves first redistribute with an adjacent sibling when that reduces amplification; otherwise they merge, repair one separator and the leaf link, and remove one parent child. Internal underflow follows the same redistribution/merge rule. Structural repair may be scheduled after the deleting publication only if the tree remains within explicit safe occupancy bounds and all ownership/count invariants already hold; stale counts or unreachable empty nodes are not permitted as deferred cleanup.

### Concurrency

Disjoint leaf preparation remains parallel. Structural publication and shared-ancestor count folding are serialized only for the nodes they actually share. A root count update is an honest shared write and may become a narrow contention point; measure it rather than hiding it behind deferred counts. If root contention is material, the next branch is partitioned top-level count lanes with an exact read-time sum, provided those lanes remain canonical children and not a second ownership graph.

## LXL Proofs

### 1. Ordinary Insert

Contrived leaf: `10:A, 20:B, 40:D`, count `3`. Insert `30:C`.

- lower-bound search returns slot `2`;
- slot directory becomes `10:A, 20:B, 30:C, 40:D`;
- leaf count changes `3 -> 4`;
- each path child count changes by `+1` once at publish;
- exact `30:C`, range `[20, 30]`, and count `[20, 30]` all resolve from the same ordering truth.

### 2. Leaf Split

Contrived full leaf: `10:A, 20:B, 30:C, 40:D, 50:E, 60:F`, old successor `L3`, count `6`. Insert `35:X`.

- sorted logical sequence is `10:A, 20:B, 30:C, 35:X, 40:D, 50:E, 60:F`;
- byte-balanced boundary yields left `10..35` count `4`, right `40..60` count `3`;
- separator is `40` or a proven shorter separator;
- left successor becomes right; right successor becomes `L3`;
- parent replaces old child count `6` with left count `4`, inserts `(separator 40, right, count 3)`, and its total changes by exactly `+1`;
- no other route can reach either leaf.

### 3. Exact Lookup

For separators `20, 40, 70`, key `35` selects child `1` (`20 <= key < 40`), then one leaf binary search. It does not enumerate sibling routes, allocate a visited-target set, or inspect unrelated leaves.

### 4. Count Range

Root children own `[00,20) count 8`, `[20,40) count 12`, `[40,60) count 18`, `[60,70) count 7`, `[70,+inf) count 9`. Count `[22,65]`:

- skip child `0`;
- descend into child `1` for its lower boundary and obtain `10`;
- add child `2`'s authoritative count `18` without reading that child;
- descend into child `3` for its upper boundary and obtain `4`;
- skip child `4`;
- return `10 + 18 + 4 = 32`;
- no matching identities are materialized.

### 5. Streaming Range

Range `[30,55]` seeks leaf containing `30`, emits its qualifying tuples, follows one successor, emits through `55`, and stops. The number of route searches is one, regardless of returned row count.

### 6. Oversized Duplicate Key

Key `42` owns `3,000` identities. Its leaf has one `42` duplicate descriptor with count `3,000` and one chain head/tail. Exact `42`, count `[42,42]`, and any containing subtree count all use `3,000`; tree separators never split the identical key across children.

### 7. Delete And Merge

Adjacent leaves own `10,20` and `30,40`; delete `20` leaves the first below its measured occupancy threshold. A merge produces `10,30,40`, repairs the predecessor link and parent separator, subtracts one from all ancestor totals, and removes the retired child in the same atomic publication.

## Affected Production Paths After Approval

The implementation should replace VS8-specific topology, not alter unrelated shapes opportunistically.

- Factory/open: VS8 root creation, format validation, directory metadata, and reopen.
- Layout/view: new internal-node and leaf layouts plus direct mutable/read-only views.
- Write: `InsertWalkedRoutedVarKeyScalar8`, growth/transform/split helpers, duplicate handling, delete/rekey, bulk build, and `LibraDexWriteContext` VS8 path claims.
- Read: exact lookup, `VarKeyScalar8RangeReader`, key/identity/tuple buffers, prefix selection, `CountEncodedIdentityRange`, and count-all.
- Publication: durability-batch staging, root replacement, dirty-range writes, version validation, and optimizer/replacement planning.
- Retirement: VS8 use of broad alias route construction, including intermediate left/right prefix fanout and graph visited-target traversal. Shared router code remains for other shapes until separately redesigned.

Candidate new VS8 types should be appended in the related files/blocks or added as focused files under `Layouts`, `Views`, `Indexes`, and `FileSession`, following `AGENT_CODING_STYLE.md`. Hot operations should remain direct, allocation-visible, and shallow rather than hidden behind a general-purpose tree framework.

## Implementation Sequence After Approval

1. ⬜ [ ] Add a deterministic in-memory model/oracle and structural invariant inspector before routing public VS8 calls to the new tree.
2. ⬜ [ ] Add versioned node/leaf layouts and reopen validation.
3. ⬜ [ ] Add create and sorted bulk build; prove leaf ordering, links, fences, and counts.
4. ⬜ [ ] Add exact read and ordinary insert without split.
5. ⬜ [ ] Add leaf/internal/root split plus duplicate-run ownership.
6. ⬜ [ ] Add range cursor, prefix, exact range count, and count-all.
7. ⬜ [ ] Add delete, redistribution/merge, and rekey.
8. ⬜ [ ] Add write-context coalescing and concurrency/version conflict handling.
9. ⬜ [ ] Sweep page/fill profiles and optimize only after parity and structural validation remain green.
10. ⬜ [ ] Consider VS16/VV adoption from measured VS8 evidence.

## Required Gates

### Correctness And Structure

- ⬜ [ ] Every operation compares against a sorted reference model before timing.
- ⬜ [ ] Empty, one-row, short/long key, zero byte, prefix-of-another-key, `1024`-byte key, duplicate, split-boundary, root-split, delete, merge, reopen, and interrupted-publication cases pass.
- ⬜ [ ] Randomized mixed insert/delete/rekey/exact/range/count sequences validate tuple parity and every persisted subtree count after each publication.
- ⬜ [ ] Inspector proves unique parentage, non-overlapping complete fences, separator truth, globally ordered leaf links, and root total parity.
- ⬜ [ ] `vs8-routed-sanity`, `vs8-index-api-sanity`, `varlen-mb-range-count-sanity`, `varlen-count-all-sanity`, `concurrency-contract-sanity`, `concurrency-workload-matrix`, `public-surface-api-sanity`, and `public-api-snapshot` pass.

### Performance And Efficiency

- ⬜ [ ] Use the standalone ShapeBench runner with serial outer execution, one scenario per process, and a hard `ResultItems` parity guard before trusting timing.
- ⬜ [ ] Repeat the original `vs8 count-range-api 5000 1` row at small pre-split size and at transformed sizes including `8,192`, `16,384`, and `250,000` items.
- ⬜ [ ] Count-range must meet or exceed SQLite median throughput at the representative `250,000`-row shape without result materialization.
- ⬜ [ ] Exact lookup must not regress below the current direct exact path or SQLite parity.
- ⬜ [ ] Range identities/keys/tuples must meet or exceed SQLite median throughput for the representative narrow-range rows; cursor allocation remains bounded and disconnected buffers allocate only for returned output.
- ⬜ [ ] Moderate mixed writes must retain at least SQLite parity and avoid worse committed-byte amplification than the current VS8 path.
- ⬜ [ ] Report `ops/sec`, logical identities/sec, allocated bytes/op, backing reads/op, committed bytes/item, and final bytes/item for each accepted profile.
- ⬜ [ ] Run at least three isolated repeats and select by median; investigate material drift before changing defaults.

## Rejection Boundary

Do not implement this proposal. The active production candidate is the core-compatible ordered shelf scoop in [vs8-scoop-range-count-design.md](vs8-scoop-range-count-design.md); its first gate is a read-only proof of the existing route-order and shelf-interval invariants.
