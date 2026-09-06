# UInt64 Ordered Set Physical Design

## Status

- 🟩 [x] Public semantic choices are accepted in `ordered-key-set-design-checklist.md`.
- 🟩 [x] The sparse presence, exact-counted, and saturating-counted UInt64 formats are implemented and harness-gated.
- 🟨 [~] The 4 KiB page profile is the selected first production profile; broader distribution and file-churn measurements may still justify later profiles.
- 🟨 [~] Saturating and exact-counted formats are implemented; dense leaves remain a subsequent proof slice.
- 🟨 [~] Typed standalone public promotion is complete; catalog discovery, public cursors/ranges, maintenance integration, and Abraxas adoption remain gated.

## 1. Selected First Slice

The first slice is an internal UInt64 presence set over the existing `DataKernel` memory and file backings. It uses:

- one fixed root descriptor;
- the existing 4 KiB direct one-byte `RouterLayout` and `RouterReader`/`RouterWriter` primitives;
- a new 4 KiB key-only sparse shelf;
- copy-on-write leaf splits and same-extent shelf-to-router conversion;
- the existing allocation-segment reservation/retirement ordering;
- one serialized prototype mutation owner and coherent readers.

It deliberately does not implement `IIndex`, catalog projections, identities, companions, completeness, or inverse lookup.

## 2. Why 4 KiB Is The First Page Profile

The existing direct router occupies 4 KiB. A key-only UInt64 shelf also fits useful fanout in 4 KiB:

- 32-byte shelf header;
- 508 packed, sorted UInt64 keys;
- zero slot ordinals;
- zero identity bytes;
- zero per-key object allocations.

Equal router/shelf extent size provides a particularly valuable local transform: a full shelf whose keys still share the current route byte can become the next router in the same extent. New child shelves are populated first; the old shelf offset is then published as a router. The parent target remains stable for the still-owned prefix, and aliases outside that prefix are cleared in the same publication.

The profile is not assumed optimal merely because it is elegant. Later isolated measurements must compare 4, 8, 16, and 32 KiB sparse shelves, including write amplification, route depth, exact lookup, ordered scan, and bytes/key. Profiles larger than a router must prove that router-arena reuse recovers enough value to justify their mutation and locality cost.

## 3. Root Descriptor

The prototype root occupies one fixed 4 KiB page at offset zero. The initial layout is:

| Offset | Size | Field | Meaning |
|---:|---:|---|---|
| 0 | 4 | Magic | UInt64 ordered-set root format |
| 4 | 2 | Format version | Local root format version |
| 6 | 2 | Header size | Validated root header bytes |
| 8 | 1 | Collection kind | Presence, saturating counted, or exact counted |
| 9 | 1 | Counter bits | Zero for presence; packed saturation width; 64 for exact |
| 10 | 2 | Flags | Root-local format flags |
| 12 | 4 | Page size | Selected sparse/router page profile |
| 16 | 8 | Root router offset | Direct authoritative topology root |
| 24 | 8 | Allocation-directory offset | File allocator directory; retained for backing parity |
| 32 | 8 | Distinct-key count | Exact distinct membership cardinality |
| 40 | 8 | Exact occurrence total | Valid only for exact counted mode |
| 48 | 8 | Saturation ceiling | Valid only for saturating counted mode |
| 56 | 8 | Generation | Advances on successful logical mutation |
| 64..4095 | | Reserved | Zero in the first format |

Ordinary mutations rewrite only the changed fixed root fields, not the whole 4 KiB page. The descriptor is authoritative metadata, not a mutation log.

## 4. Sparse Presence Shelf

The first sparse shelf layout is:

| Offset | Size | Field | Meaning |
|---:|---:|---|---|
| 0 | 4 | Magic | Sparse UInt64 presence shelf |
| 4 | 2 | Format version | Local shelf format version |
| 6 | 2 | Header size | 32 |
| 8 | 2 | Item count | Live keys in the packed prefix |
| 10 | 2 | Capacity | 508 for the 4 KiB profile |
| 12 | 4 | Flags | Shelf-local state |
| 16 | 8 | Minimum key | Validation/fast rejection; undefined when empty |
| 24 | 8 | Maximum key | Validation/fast rejection; undefined when empty |
| 32 | 4064 | Keys | Big-endian sortable UInt64 keys in strict ascending order |

No slot array is used in the first proof. Insert/remove shifts at most 4,056 bytes within one page; this must be compared with a two-byte slot ordinal layout before public-format selection. The packed form wins 20% capacity over a 2-byte slot plus 8-byte key and makes forward/reverse scans contiguous.

## 5. Router Ownership

Routers are direct one-byte routers. The root router has key depth zero. A child router's key depth identifies the next big-endian key byte it owns.

Required invariants:

1. Every live key resolves through exactly one route path.
2. Aliases to one leaf are adjacent route ordinals in one parent.
3. A leaf contains only keys whose byte at the parent router depth is within the aliases pointing to that leaf.
4. Two different parents never own the same shelf or router target.
5. A router target is zero, another router, or one set shelf format declared by the owning root.
6. Router depth strictly increases and never exceeds seven.
7. Traversing distinct adjacent targets in ascending route order, then keys within each leaf, produces global ascending UInt64 order.
8. Reverse route order plus reverse leaf order produces exact global descending order.

Adjacent aliases let a leaf use capacity across several byte values instead of creating one mostly empty 4 KiB leaf per byte. This is essential to storage efficiency.

## 6. Insert LXL

### 6.1 Unset route

1. Walk the root/child routers using the key's big-endian byte at each router depth.
2. If the selected target is zero, reserve and populate one sparse shelf containing the key.
3. Rewrite only that parent router target.
4. Increment root distinct count and generation.
5. Commit payload before publication.

### 6.2 Existing shelf with room

1. Read the shelf once.
2. Binary-search its packed keys.
3. Return `false` without publication when the key exists.
4. Otherwise shift the suffix eight bytes right, write the key, update count/min/max, and publish one shelf rewrite plus root count/generation.

### 6.3 Full leaf whose keys differ at the parent depth

1. Merge the incoming key into one stack-bounded 509-key sorted sequence.
2. Select a byte-boundary split nearest the median; never split inside one route byte.
3. Reserve and populate left/right replacement shelves.
4. Rewrite every adjacent parent alias formerly targeting the old shelf: ordinals below the right boundary select left; ordinals at/above it select right.
5. Publish the parent rewrite after both replacement shelves.
6. Retire the old shelf after publication/read safety.
7. Increment root distinct count and generation.

Because there are only 509 keys, if more than one parent-depth byte exists, a boundary always exists whose two sides each fit a 508-key shelf. A single 509-key byte group is the next case.

### 6.4 Full leaf whose keys share the parent-depth byte

1. Merge the incoming key and find the first deeper byte where the sorted minimum and maximum differ.
2. Reserve and populate two final child shelves split at that deeper byte boundary.
3. Build any intervening one-route router chain from deepest to shallowest.
4. Rewrite the old shelf extent as the first child router, so the owning parent prefix retains the same target offset.
5. Clear parent aliases to the former shelf for every parent-depth byte other than the one the keys actually share. This prevents the historical skipped-byte alias defect.
6. Publish child payloads, deeper routers, the same-extent router conversion, and the parent alias cleanup as one logical mutation.
7. Increment root distinct count and generation.

Distinct UInt64 keys must differ by depth seven. Reaching an exhausted key with a distinct incoming key is corruption or a comparison defect.

## 7. Remove And Clear LXL

### 7.1 Remove

1. Walk directly to the candidate shelf.
2. Binary-search once.
3. Return `false` without mutation on a miss.
4. Shift the trailing packed keys left on a hit and update count/min/max.
5. If the shelf becomes empty, clear every adjacent parent alias pointing to it and retire it after publication.
6. Decrement root distinct count and advance generation.

Router collapse/merge is not required for the first correctness slice, but empty routers must remain valid and bounded. A later occupancy pass determines when collapsing a single-child router materially improves reads/storage.

### 7.2 Clear

1. Walk each distinct target once from the root router.
2. Build an empty replacement root-router image.
3. Publish the empty root and zero root counts.
4. Retire every non-root topology page after root publication/read safety.
5. Reuse those allocation-class slots before extending EOF during repopulation.

Clear is a topology operation, never one remove per key.

## 8. Ordered Reader

The first reader uses at most eight router frames plus one 4 KiB page buffer. Each frame retains router offset, depth, and next route ordinal. Adjacent routes with the same target are skipped without a visited set because contiguous alias ownership is a persisted invariant.

The reader:

- emits sparse shelf keys directly in ascending or descending order;
- performs no result-cardinality-sized allocation;
- can seek a lower or upper bound by following the bound path before ordered sibling traversal;
- is compatible with merge-style set algebra;
- enters the existing coherent-read scope for its lifetime.

The first harness may use a caller-provided output span to prove ordering. Public promotion requires a cursor that does not require whole-result allocation.

## 9. Counted Leaf Follow-ons

### 9.1 Saturating counted leaf

Keys remain one sorted packed array. Counters occupy a separate packed bit stream so key comparison remains contiguous and branch-light. Counter width is the smallest of 2, 4, 8, 16, 32, or 64 bits that represents the caller's ceiling. A ceiling of two therefore records `0 / 1 / 2+` in two bits.

Increment stops changing the stored counter at the ceiling. APIs must report that the value is saturated rather than return it as an exact occurrence count.

### 9.2 Exact counted leaf

Exact mode stores an unsigned 64-bit count per key in a parallel packed array. Increment at `UInt64.MaxValue` throws before mutation; it never wraps. The root maintains both distinct-key count and exact total occurrences.

### 9.3 Shared routing

Presence, saturating, and exact shelves have distinct magic/kind values but share the same router traversal, split ownership, allocator, publication, and ordered cursor machinery. Hot leaf operations remain format-specific to avoid a strategy/delegate call for each comparison or counter update.

## 10. Dense Leaf Follow-on

A dense leaf is a routed, aligned suffix window with a bitmap rather than a general UInt64-domain bitmap. Its route prefix plus bit ordinal reconstructs the full key. Forward/reverse ordered scans use set-bit operations.

Automatic conversion remains disabled until measurements establish:

- the suffix width and extent size;
- sparse-to-dense and dense-to-sparse thresholds with hysteresis;
- exact lookup and scan advantage;
- committed/live bytes advantage;
- mutation cost under sparse holes and churn;
- counted bit-plane layout for saturation and the policy for exact counts.

## 11. First Proof Gates

- ⬜ [ ] Empty, one-key, duplicate, ascending, descending, random, and boundary keys.
- ⬜ [ ] First root split, same-byte transform, multi-level shared prefix, and parent alias cleanup.
- ⬜ [ ] 49,057, 258,919, and one-million-key parity against a managed reference.
- ⬜ [ ] Ascending/reverse traversal hashes and exact counts.
- ⬜ [ ] Remove miss/hit/empty-leaf and clear/repopulate.
- ⬜ [ ] File reopen after each structural case.
- ⬜ [ ] Failure injection at allocation, payload, publication, and retirement phases.
- ⬜ [ ] Memory/file semantic parity.
- ⬜ [ ] Allocations, route hops, page reads, splits/transforms, bytes shifted, committed bytes, live bytes, reusable bytes, and EOF.
- ⬜ [ ] Neutral comparisons against `HashSet<ulong>`, `SortedSet<ulong>`, and current SS8-8-with-constant-identity storage.

## 12. Rejection Boundaries

Stop or revise this branch if:

- exact lookup is structurally dominated by a simpler B+ or extendible-hash candidate;
- 4 KiB packed-leaf rewrite amplification erases its locality/storage benefit;
- contiguous route aliases cannot be maintained without correctness compensation in readers;
- ordered traversal requires a visited/deduplication set;
- memory/file behavior diverges;
- same-extent conversion cannot preserve publication safety;
- durable churn extends EOF while compatible retired pages are available.

The next-best branch is a key-only B+ structure over the same DataKernel allocator. It retains ordering but replaces byte routers with separator pages. Extendible hashing is third because it loses native order unless paired with another structure.
