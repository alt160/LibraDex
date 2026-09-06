# LibraDex Shelf Lifecycle and Storage Reuse Audit

Status markers: 🟩 [x] done/accepted; 🟨 [~] partial or mixed; 🟥 [!] failed/problem; ⬜ [ ] open.

## Mission and boundary

This workstream audits every persisted LibraDex shelf family against four behavioral intentions:

1. A full ordinary shelf should refine its route and split locally when key bytes can distinguish the replacement shelves.
2. A route whose key bytes are exhausted should use a compact identity-only terminal representation that grows by linked shelves.
3. Deletion should make occupied shelf capacity reusable before avoidable topology growth or file extension.
4. Shelf-to-router conversion should reuse the already-reserved shelf extent for as many router pages as safely fit.

These are goals, not mandated implementation recipes. A materially better mechanism is preferred when it preserves correctness, speed, locality, and storage efficiency.

The design must not turn LibraDex into a write-ahead-log engine. The authoritative current state must remain directly traversable from the catalog/index roots. Routine correctness must not require replaying historical mutation records, and routine space recovery must not mean compacting an ever-growing mutation log. Small bounded publication metadata, generation markers, retirement evidence, and rebuildable free-space summaries are allowed when they accelerate or protect direct structures rather than replace them.

## Audit result legend

- `Yes`: source path exists and its required behavior is covered by a focused test or exact topology assessment.
- `Partial`: some paths implement the intent but a meaningful gap remains.
- `No`: the shape lacks the behavior or rejects the relevant case.
- `N/A`: the shape does not expose the operation in its current public contract.
- `Test`: source evidence exists but focused runtime proof is still required.

## Shape matrix

| Shelf family | Full-shelf route refinement | Exhausted-key identity-only storage | Deleted-capacity reuse | Shelf extent becomes router arena | Current audit result |
|---|---|---|---|---|---|
| SS8-8 | Yes | Yes: fixed identity terminal root/shelves | Yes: slot normalization; terminal local delete | Yes | 🟩 [x] lifecycle and current-format allocator retirement/reuse validated |
| SS16-8 | Yes | Yes: shared fixed-key scalar-8 terminal | Yes: slot normalization; terminal local delete | Yes | 🟩 [x] lifecycle and current-format allocator retirement/reuse validated |
| SS8-16 | Yes | Yes: shared fixed-key scalar-16 terminal | Yes: slot normalization; terminal local delete | Yes | 🟩 [x] lifecycle and current-format allocator retirement/reuse validated |
| SS16-16 | Yes | Yes: shared fixed-key scalar-16 terminal | Yes: slot normalization; terminal local delete | Yes | 🟩 [x] lifecycle and current-format allocator retirement/reuse validated |
| FS32-8 | Yes | Yes: shared fixed-key scalar-8 terminal | Yes: slot normalization; terminal local delete | Yes | 🟩 [x] lifecycle and current-format allocator retirement/reuse validated |
| FS32-16 | Yes | Yes: shared fixed-key scalar-16 terminal | Yes: slot normalization; terminal local delete | Yes | 🟩 [x] lifecycle and current-format allocator retirement/reuse validated |
| FSN-8 | Yes for distinguishable keys | Yes: fixed key stored once plus scalar-8 terminal chain | Yes: immediate dense compaction and exact terminal delete | Yes: transform persists and reopens the complete former-shelf arena | 🟩 [x] lifecycle and current-format allocator retirement/reuse validated |
| FSN-16 | Yes for distinguishable keys | Yes: fixed key stored once plus scalar-16 terminal chain | Yes: immediate dense compaction and exact terminal delete | Yes: transform persists and reopens the complete former-shelf arena | 🟩 [x] lifecycle and current-format allocator retirement/reuse validated |
| FSN-V | Yes for distinguishable keys | Yes: fixed key stored once plus variable-identity terminal chain | N/A/Partial: no public exact-delete surface found; insertion and terminal growth are compact | Yes: transform persists and reopens the complete former-shelf arena | 🟨 [~] allocator lifecycle complete; deletion API remains a separate contract question |
| VS8 | Yes | Yes: duplicate-run and terminal identity root/shelves | Yes: insertion compacts once when deleted payload can admit the tuple | Yes | 🟩 [x] lifecycle and current-format allocator retirement/reuse validated |
| VS16 | Yes | Yes: exact-key terminal scalar-16 identities | Yes: insertion compacts once when deleted payload can admit the tuple | Yes | 🟩 [x] lifecycle and current-format allocator retirement/reuse validated |
| VV | Yes | Yes: terminal variable-identity root/shelves | Yes: insertion compacts once when deleted payload can admit the tuple | Yes | 🟩 [x] lifecycle and current-format allocator retirement/reuse validated |
| SV8 | Yes | Yes: terminal variable-identity root/shelves | Yes: insertion builds and installs one compact same-extent image before retry | Yes | 🟩 [x] lifecycle and current-format allocator retirement/reuse validated |
| SV16 | Yes | Yes: terminal variable-identity root/shelves | Yes: insertion builds and installs one compact same-extent image before retry | Yes | 🟩 [x] lifecycle and current-format allocator retirement/reuse validated |

## Cross-cutting findings

### In-shelf reuse

- 🟩 [x] The six ordinary fixed-entry shelf families compact tombstoned slots before publication, restoring dense prefix capacity.
- 🟩 [x] FSN-8 and FSN-16 delete by compacting the fixed tuple region immediately.
- 🟩 [x] FSN-V insertion falls back to rebuilding the same-size shelf from live tuples plus the incoming tuple when tail space is exhausted.
- 🟩 [x] VS8, VS16, and VV now retry ordinary insertion after one in-place payload repack when the append tail is full and reclaimable deleted bytes can admit the tuple.
- 🟩 [x] SV8 and SV16 now retry ordinary insertion after building and installing one compact same-extent shelf image under the same pressure condition, including rebuilding their mutable sidecars.
- 🟩 [x] The directed mutable-shelf diagnostic for VS8, VS16, VV, SV8, and SV16 now passes through the ordinary insert operation itself; every shape returns `Inserted` with the controlled deleted-payload fixture.

### Exhausted-key storage

- 🟩 [x] SS8-8, the other fixed scalar families, VS8, VS16, VV, SV8, and SV16 have identity-only terminal representations or duplicate-run chains that store the exhausted key once.
- 🟩 [x] FSN-8, FSN-16, and FSN-V now convert a full same-key shelf into a terminal root that stores the fixed key once and chains identity-only shelves in the shape-appropriate scalar or variable format.
- 🟩 [x] Fixed-N exact-key overflow fixtures now insert and reopen exactly 20,000 identities in all three shapes. FSN-8 and FSN-16 also prove exact terminal deletion after reopen.
- 🟩 [x] Maintenance topology assessment recognizes all three fixed-N terminal-root shapes and reports the exact authoritative tuple count without treating the index as unsupported.

### Shelf-to-router reuse

- 🟩 [x] The six fixed scalar families and the five variable-entry families persist router-arena metadata in a converted shelf extent and register the remaining router pages for session reuse.
- 🟩 [x] The three fixed-N transforms now write arena metadata into every local intermediate router, allocate as many routers as fit inside the former shelf extent, and register each used page in the writer session.
- 🟩 [x] Allocator requests now lazily recover a named persisted arena when the reopened session has not encountered it through an arena-aware traversal. This targeted page-zero discovery avoids an eager file scan and reconstructs used pages before reuse.
- 🟩 [x] The fixed-N reopen gate forces a six-page intermediate stem, closes the session, then proves page seven is reused without extending the 225,280-byte file.
- 🟩 [x] Free-page selection inside transformed shelves remains local, while the durable segment allocator now supplies general exact-size file reuse across shelf/router/terminal extents.

### Retired durable extents

- 🟩 [x] `DataKernel.Reserve` first selects compatible unoccupied slots from persisted homogeneous allocation segments and appends only when no materialized reusable slot exists; uncommon/non-power-of-two extents retain conservative append fallback.
- 🟩 [x] Structural mutation paths now stage one backing-neutral extent retirement. Memory release and file bitmap clearing occur only after replacement bytes and authoritative route/root publication in the same commit.
- 🟩 [x] Catalog `Drop` and `DropIndexSet` collect every known-shape topology offset, including metadata-free owned string companions, publish directory removal first, and retire allocator-owned extents last.
- 🟩 [x] Active coherent file readers block the write/retirement boundary. Memory readers retain immutable snapshots while newly published pages use copy-on-write.
- 🟩 [x] Maintenance accounting reports allocator directory/segment headers as live overhead and free materialized payload as reusable capacity rather than unreachable waste.
- 🟩 [x] Closed-file compaction exists as an explicit maintenance mechanism and preserves a direct, compact catalog image.
- 🟨 [~] Legacy, malformed, unsupported, and append-fallback extents remain conservatively unreachable and require compaction or a future verified topology reconciliation; they are never guessed into a reusable class.
- 🟩 [x] Drop/reopen/recreate over 6,000 mixed fixed, VS8 exact/folded, and retained-index rows held EOF from 1,832,220 to 1,833,203 bytes: 983 bytes of metadata-only growth while 1,757,184 retired bytes were reported reusable.

## Non-WAL BLX branches

### Branch A: persisted per-mutation free-list journal

🟥 [!] Reject as the primary design. It makes allocator correctness depend on replayable historical mutation records, introduces another ordered durability stream, and trends directly toward WAL behavior.

### Branch B: open-time full reachability reconstruction only

🟨 [~] Architecturally clean because roots remain authoritative and no log is needed. It is expensive on large catalogs, and exact reconstruction is difficult unless every durable extent is self-describing or the topology walkers cover every layout perfectly.

### Branch C: rebuildable global free-space snapshot plus direct topology

🟨 [~] Viable only as an accelerator. Keep direct routers/shelves authoritative. Maintain an in-memory size-class free map, retire extents only after publication/read safety, and checkpoint a compact free-space snapshot with generation/checksum. The snapshot accelerates allocation but is never replayed to reconstruct index contents.

Tradeoff: this is not sufficient as the primary allocator format because current durable extents are not uniformly self-describing. A stale global free snapshot cannot safely distinguish a live extent from an unreachable one without a complete cross-layout topology census.

### Branch D: allocation segments with direct bounded bitmaps

🟩 [x] Selected allocator foundation. Reserve homogeneous segments for router pages, fixed shelves, terminal shelves, and supported variable-shelf extent classes. Each segment is self-describing and contains a compact directly updated allocation bitmap. Allocation marks a slot occupied before any authoritative route can publish it; retirement clears the slot only after route publication and reader safety. A crash may conservatively leak a reserved slot, but cannot make a live slot reusable. Reachability reconciliation can recover leaks without replaying mutations.

This is bounded allocator state, not a WAL: it does not contain historical operations, index contents remain directly traversable, no record replay is required, and metadata size is proportional to current addressable extents rather than mutation count.

Tradeoff: homogeneous size classes can strand capacity. Use the shelf profiles already selected by LibraDex, allow an extent class to host compatible shapes, and keep oversized/rare extents on a fallback path rather than proliferating arbitrary classes.

### Branch E: direct in-place overwrite with bounded dual publication metadata

🟨 [~] Useful selectively for owned fixed extents. It minimizes allocation and file growth, but crash safety, shared-route ownership, and concurrent-reader visibility are substantially harder. It should complement, not replace, safe copy-on-write publication where ownership is changing.

### Selected hybrid

🟩 [x] Use Branch D as the durable allocation backbone, Branch C only as a derived in-memory/global selection cache, and Branch E only for proven exclusive fixed-offset updates. Router micro-arenas remain the smallest local form of the same concept. Shadow compaction remains maintenance and migration, not routine reclamation.

For existing catalogs, legacy append extents remain readable. New allocations move into self-describing segments. A one-time compaction or verified topology census can migrate/reclaim legacy unreachable space; ordinary future mutations then reuse segment slots before extending EOF.

## LXL scenarios used for implementation selection

- 🟩 [x] Full distinguishable-key shelf: reserve two compatible segment slots, fully populate replacement shelves and any child router, publish every owning route alias only after those bytes are valid, then retire the old slot. A crash before route publication leaves the old shelf authoritative and may leak only the new reservations; a crash after publication leaves the new topology authoritative and the old slot conservatively occupied until reconciliation/retirement.
- 🟩 [x] Full exhausted-key shelf: write an identity-only terminal root plus its first shelf, then replace the ordinary route target. Additional identities append to or split the chain without repeating the exhausted key. Deleting an empty head/middle/tail publishes the predecessor/root link first and retires the removed shelf afterward.
- 🟩 [x] Variable shelf with deleted payload: normalize deleted slots, test live slot and byte capacity, compact the same shelf image once when reclaimable bytes would admit the tuple, retry the insertion, and enter grow/split only if it still returns `Full`. The focused diagnostic proves this path can avoid topology growth for all five variable layouts.
- 🟩 [x] Converted shelf arena: retain the former extent size/class, publish arena metadata in the first router, allocate subsequent router pages from the same extent, and reconstruct used pages on reopen from router magic plus the bounded arena metadata. Fixed-N needs the same metadata path already used by the other eleven ordinary shapes.
- 🟩 [x] Retired extent reuse: a published route/root generation changes before its old extent becomes free. An active reader pins the older generation; the allocator cannot clear/reuse that slot until the minimum active reader generation passes retirement. After process restart no old readers survive, so topology/segment reconciliation can release unreachable retired slots without mutation replay.
- 🟩 [x] Crash with allocator metadata behind topology: allocation bits are set before route publication and cleared only after route removal plus reader quiescence. Therefore stale state can leak space but cannot authorize overwrite of live topology. On reopen, direct roots remain readable immediately; reconciliation repairs conservative leaks.
- 🟩 [x] Phase-boundary failure injection found and repaired two concrete violations of that model: later retirement bytes could overwrite an earlier allocation-claim buffer, and reopen initially derived append position only from physical EOF instead of the highest durably materialized extent. Pending-buffer reuse is now phase-local, and allocator reconstruction advances the append high-water mark beyond every materialized slot.
- 🟩 [x] Aborted writer: its reserved segment bits remain occupied until abort cleanup or reopen reconciliation. Because no authoritative route references the bytes, they never become visible as index contents and require no undo record.
- 🟩 [x] Shared-shelf owners: validate and publish every aliased owner as one structural change; only then retire the prior shelf slot. The earlier VS8 duplicate-identity defect demonstrated why updating one owner and compensating in readers is invalid.

### LXL conclusion

The safe asymmetry is deliberate: publication failures may temporarily waste space, but they may never expose partial topology or permit reuse of live bytes. Recovery is a current-state reachability reconciliation, not redo/undo replay. This preserves LibraDex's direct model while allowing routine reuse.

## Focused diagnostics and execution order

1. 🟩 [x] Ran current full-shelf proportionality, terminal-locality, shared-owner, and fixed/fixed-N/variable storage gates against one rebuilt source snapshot.
2. 🟩 [x] Converted `shelf-lifecycle-audit` into a positive repair gate for fixed-N terminal growth/reopen, variable-payload reuse, maintenance accounting, exact scalar terminal deletes, and fixed-N arena reuse after reopen.
3. 🟨 [~] Recorded per-family first-build physical/reachable amplification and the live Wherzit repeated-mutation assessment. Per-shape repeated-mutation EOF deltas become acceptance gates for the allocator implementation rather than blockers to identifying the current universal append-only gap.
4. 🟩 [x] LXL traced commit, abort, crash, reopen, concurrent-reader, and shared-owner behavior for the snapshot and segment branches.
5. 🟩 [x] Selected self-describing allocation segments with bounded direct bitmaps, an in-memory derived free-slot cache, reader-safe retirement, and no replay dependency.
6. 🟩 [x] Completed the selected shelf-local repair phase and reran fixed, fixed-N, variable, terminal-locality, shared-owner, and router-arena reopen gates across the affected families.
7. 🟩 [x] Implemented and validated the durable segment allocator, reader-safe staged retirement, drop/set bulk topology retirement, reopen reconstruction, and reusable-capacity accounting.
8. 🟩 [x] Cross-shape maintenance now uses coherent upgradeable reads. Snapshot activation is limited to source-cursor steps so memory-backed replacement builders retain read-your-writes while preserving their authoritative source generation.

## Validation evidence from the audited source snapshot

- 🟩 [x] Release rebuild of `LibraDex.Harness.csproj`: passed.
- 🟩 [x] Fixed ordinary first-build storage gate, 4,096 tuples: 2,525,136 physical bytes, 2,418,640 reachable bytes, 106,496 unreachable bytes, 1.044031x amplification. Directed ascending, descending, alternating, and shuffled fixtures remained approximately 1.184–1.186x before compaction.
- 🟩 [x] Fixed-N first-build storage gate, 4,096 tuples: 3,367,969 physical bytes, 3,195,937 reachable bytes, 172,032 unreachable bytes, 1.053828x amplification. Same-depth alias pressure passed.
- 🟩 [x] Variable-family first-build storage gate, 4,096 tuples: 2,740,224 physical bytes, 2,297,856 reachable bytes, 442,368 unreachable bytes, 1.192513x amplification. Per-shape observations were SV16 1.016667x, SV8 1.015789x, VS16 1.739130x, and VV 1.418033x.
- 🟩 [x] Raw allocator gate proves file reopen reuse, discarded-retirement safety, coherent-reader blocking, unsupported-length append fallback, memory-backed discard/commit retirement symmetry, and conservative behavior after injected failure at allocation-claim, payload, publication, and retirement boundaries.
- 🟩 [x] Catalog allocator gate proves individual drop blocking under a retained reader, index-set retirement, reopen reconstruction, exact retained/rebuilt query semantics, 1,757,184 reusable bytes, 983 unreachable metadata bytes, and only 983 bytes of EOF growth after equivalent recreation.
- 🟩 [x] Maintenance bound and compaction gates pass after the per-kernel coherent-reader fix: `light=16`, `bounded=256`, all five variable shapes complete, and compaction reclaims 4,972,874 bytes while preserving logical tuples.
- 🟩 [x] Duplicate-run and terminal identity locality gates passed at 7,000 identities; ascending and descending terminal files were both 356,352 bytes in the final run.
- 🟩 [x] Terminal variable-identity delete locality passed for SV8 and VV.
- 🟩 [x] Shared-shelf live/reopen growth gates passed for VS8, VS16, VV, and SV16.
- 🟩 [x] The repaired shelf lifecycle diagnostic passed with these positive boundaries:

| Shape | Repair evidence | Ordinary operation result | Reopen/accounting evidence |
|---|---:|---|---|
| FSN-8 | 20,000 same-key identities | `Inserted` | exact 20,000; exact delete leaves 19,999 |
| FSN-16 | 20,000 same-key identities | `Inserted` | exact 20,000; exact delete leaves 19,999 |
| FSN-V | 20,000 same-key identities | `Inserted` | exact 20,000 |
| VS8 | 552 reclaimable bytes | `Inserted` | N/A |
| VS16 | 584 reclaimable bytes | `Inserted` | N/A |
| VV | 520 reclaimable bytes | `Inserted` | N/A |
| SV8 | 552 reclaimable bytes | `Inserted` | N/A |
| SV16 | 584 reclaimable bytes | `Inserted` | N/A |
| FSN-8 router arena | six local intermediate pages | page seven selected after reopen | file remains 225,280 bytes |

These gates show that current-format structural extents now participate in routine reuse. Remaining physical amplification comes from live topology, bounded allocator metadata/capacity, append-fallback metadata, or legacy/unclassified bytes; closed-file compaction remains the deliberate cleanup/migration mechanism for the last category.

## Current implementation sequence candidate

1. 🟩 [x] Fixed-N exhausted-key terminal support shared across FSN-8, FSN-16, and FSN-V.
2. 🟩 [x] Fixed-N transformed-shelf router-arena metadata plus targeted reopen discovery.
3. 🟩 [x] Insert-time compaction retry for the five variable-entry mutable shelf families before growth/split.
4. 🟩 [x] Self-describing allocation segments with bounded direct bitmaps, derived in-memory offset/class caches, and reader-safe staged retirement; no mutation replay.
5. 🟩 [x] Cross-shape stability, reopen, drop/recreate amplification, first-build storage, maintenance, and compaction gates pass.
6. 🟨 [~] Wherzit and its Abraxas test harness rebuild against the allocator source, and IPC health is available; application-level acceptance still requires an opened store and repetition of the large `E:\VSProjects` ingestion/mutation workload. The current standalone lifecycle harness stops earlier on an unrelated `.FullName` bounded-population diagnostic contract.
7. 🟩 [x] Deliberate failure injection now stops after each non-empty allocation-claim, payload, publication, and retirement phase, reopens the physical image, validates the authoritative root/payload, and probes whether the correct slots are reusable. All four boundaries pass after phase-local pending-buffer reuse and materialized high-water-mark reconstruction repairs.
