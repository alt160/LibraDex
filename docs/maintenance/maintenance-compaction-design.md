# LibraDex Maintenance, Optimization, and Compaction

Last updated: 2026-07-28

## Status

- 🟩 [x] `Assess` authoritatively attributes persisted orphaned variable-record bytes for active VS8, VS16, VV, SV8, and SV16 topologies.
- 🟩 [x] `Repack` explicitly rebuilds live VS8, VS16, VV, SV8, and SV16 record arenas in place, including logical string projection companions.
- 🟩 [x] `Optimize` rebuilds VS8, VS16, VV, SV8, and SV16 root-prefix topology from authoritative tuples and version-checks every publication.
- 🟩 [x] `Compact` rebuilds and atomically replaces closed catalogs for generic scalar, string, variable-blob/VS8, UInt64/SV8, fixed- and variable-width BigInteger scalar indexes, fixed-width BigInteger scalar-null routes, and BigInteger variable-identity indexes.
- 🟩 [x] `catalog.Backup(...)` creates an exact durable backup while a file-backed catalog remains open, with publication exclusion, active-batch rejection, hash/length verification, staged reopen validation, and destination-local installation.
- 🟩 [x] Maintenance modes are operational: `Light=16`, default `Bounded=256`, and `Full=unlimited`; an explicit positive `MaxWorkItems` overrides any mode.
- 🟩 [x] `LibraDexMaintenanceResult` reports combined machine-readable incomplete reasons and a logical unsupported-index count.

## Operation Boundaries

### Repack

`Repack` recovers reusable capacity inside allocated shelves. It may rewrite shelf bytes in place, but it does not promise that the backing file becomes shorter.

Its work counter advances once for every distinct physical topology target visited, including routers and understood non-reclaimable leaves. A limit therefore bounds the whole topology walk, not merely shelves that happened to change.

### Optimize

`Optimize` improves route and shelf topology from authoritative stored tuples. It may use more physical bytes when a wider topology produces a better runtime route shape, so it does not promise minimum file size.

Every connected optimizer counts each authoritative tuple consumed. It does not read beyond a finite limit merely to discover whether the tuple stream ended, so exhaustion at the exact limit is conservatively incomplete unless completion was already proven.

VS8 and VS16 use their shape-native bulk replacement planners. VV, SV8, and SV16 rebuild a prefix behind a detached construction root inside one durability batch, then publish only the completed child target through the authoritative root's version-checked route. The detached root never enters the index directory; obsolete source topology and the construction root remain unreachable until closed-file compaction.

### Completion Contract

- `Completed` is true only when `IncompleteReasons == None`.
- `WorkLimit` means the resolved cumulative catalog budget ended before all planned work was proven complete.
- `UnsupportedTopology` is reserved for a valid future or legacy topology whose requested operation is not connected; `UnsupportedCount` counts logical owners, not projection companions.
- `InvalidTopology` means metadata or a physical target could not be classified as a known valid shape.
- `OperationUnavailable` identifies validation/cache scaffold calls that still have no executable engine.

### Compact

`Compact` reduces physical file size. It writes only active logical indexes and their live tuples to a new catalog, validates the new catalog, flushes it, closes both sessions, and replaces the source file with rollback protection.

### Live Backup

`catalog.Backup(path, options, cancellationToken)` captures the exact committed file image without disposing the source catalog or invalidating its index handles.

The session publication monitor first makes backup admission mutually exclusive with durability-batch admission. An already-active batch fails fast: the operation never guesses whether the caller intended its staged writes to be included. After admission, the DataKernel storage write lock blocks every physical commit and read path, including paths that do not use the higher-level session monitor. The source is durably flushed, copied with positional I/O, and the destination staging file is durably flushed before readers and writers resume. Large catalogs therefore have a read and write pause for the duration of the physical copy and destination flush.

The expensive proof work happens after writers are released. LibraDex compares the source-copy SHA-256 with a fresh staged-file hash, verifies exact length, and reopens the staged catalog with the live catalog's options. Only then does it move the destination-local staging file into place. Existing destinations are preserved by default; `LibraDexBackupOptions.Overwrite` must be explicit. Cancellation before installation deletes the staging file and leaves both source and destination authority unchanged.

This is a committed-state backup gate, not a transaction snapshot of unpublished batch-local mutations. Memory-backed catalogs have no durable image and are rejected.

## Why Shadow Rebuild Is the First Compactor

An in-place defragmenter could move high-offset live extents into earlier holes, update every inbound reference, and truncate the unused tail. LibraDex currently persists absolute or relative storage references in:

- index-directory root and metadata offsets;
- router route targets;
- router-arena base deltas and page metadata;
- null and empty key routes;
- duplicate-run next offsets;
- SV8/SV16 next and tail shelf offsets;
- terminal identity root and terminal shelf chains.

A crash between moving an extent and publishing every rewritten inbound reference would require a durable relocation journal and recovery state machine. The first implementation instead rebuilds through current shape-owned writers into a sibling temporary file. Those writers regenerate all offsets and route metadata under the current format, while the original file remains authoritative until independent validation succeeds.

## Logical Rebuild Contract

1. The source catalog path must identify an existing file-backed catalog and must not already have an active LibraDex owner.
2. Open the source read/write only for the bounded rebuild window.
3. Read active catalog metadata and classify physical maintained-projection companions.
4. Reconstruct each logical source shape from persisted metadata.
5. Create each logical source in the shadow catalog; shape-owned creation recreates its maintained companions.
6. Stream every authoritative raw `(key, identity)` tuple from the source logical index and insert it into the shadow logical index.
7. Verify per-index tuple count, logical metadata, projection ownership, and complete catalog reopen.
8. Flush and close the shadow catalog.
9. Replace the original on the same volume while retaining a rollback file until replacement succeeds.
10. Reopen and validate the replacement before deleting the rollback file.

Unsupported logical facades fail before replacement with `NotSupportedException`; the source remains authoritative. This is deliberate fail-closed behavior, not a partial-copy mode.

## Invariants

- Tuple equality is structural for binary keys and identities.
- Null and empty keys remain distinct.
- Duplicate ordering and identity multiplicity remain valid under the persisted index contract.
- Maintained companions are recreated only through their logical owner and are never independently copied.
- Dropped indexes, stale metadata, obsolete routers, and unreachable shelves are not copied.
- A failed rebuild or validation never changes the original catalog.
- A failed replacement retains either the original path or a rollback file that can restore it.
- Successful compaction reports exact source, compacted, and reclaimed file bytes.
- Repeating compaction without intervening mutation is semantically idempotent.

## Validation Matrix

Current executable proof:

- scalar `Int32/UInt64` logical index;
- projected string exact/folded/sort-key/reversed recreation with case-insensitive runtime policy;
- bounded variable-blob `VS8` tuple streaming and recreation;
- UInt64/variable-identity `SV8` tuple streaming and recreation;
- fixed-width BigInteger/scalar-8 tuple streaming with negative, zero, positive, duplicate-key, boundary, and scalar-null values;
- fixed-width BigInteger/scalar-16 tuple streaming with ordinary, duplicate-key, and scalar-null values;
- variable-width BigInteger/scalar-8 tuple streaming across negative, zero, positive, and boundary values;
- fixed-width BigInteger/variable-identity tuple streaming with duplicate keys and owned raw identity payloads;
- dropped multi-megabyte variable-blob extent removal;
- structural source/shadow tuple parity and post-replacement typed reopen;
- explicit in-place VS8 and SV8 repack with unchanged file length; the shared walker also connects VS16, VV, and SV16;
- authoritative-tuple VS8 route optimization with deleted tuple exclusion;
- authoritative VS16 high/low identity preservation with bounded and full tuple accounting;
- catalog-level VV detached rebuild with deleted tuple exclusion;
- catalog-level SV8 detached rebuild through the public UInt64/variable-identity facade;
- raw SV16 detached rebuild with bounded and full tuple accounting;
- owner-bound folded string projection companion repack using the logical owner's persisted variable-key bound rather than companion-local zero metadata;
- exact operational mode budgets (`16`, `256`, and unlimited) and explicit-limit override behavior;
- exact-limit catalog completion across two eligible logical indexes;
- positive catalog SV8 optimization with structured zero-incomplete reporting;
- byte-exact rollback and sibling cleanup for injected failure immediately before replacement and immediately after replacement but before installed-catalog validation;
- Abraxas application-level rollback after an injected failure between live-store rebinds, including original-file restoration, two-facade persistent-handle recovery, volatile-handle preservation, and post-failure State usability;
- exact live-catalog backup hash/length/reopen validation while the source remains open;
- post-snapshot source mutation isolation;
- active durability-batch rejection without destination installation;
- deterministic writer exclusion while the physical copy owns the storage-publication boundary;
- normalized same-path rejection, multi-block copying, cancellation strictly between copy blocks with staging cleanup, explicit overwrite behavior, and memory-catalog rejection;
- observed routed BigInteger compaction reduction from 7,499,312 bytes to 2,469,106 bytes, reclaiming 5,030,206 bytes while preserving 5,627 live tuples.

Remaining matrix:

- remaining fixed scalar key/identity combinations beyond the focused scalar proof;
- remaining non-BigInteger fixed-N scalar and variable-identity facades, if exposed as logical catalog indexes;
- additional focused VS16, VV, and SV16 repack proof fixtures beyond the connected shared walker;
- additional sort-key/reversed-focused repack fixtures beyond the folded companion regression;
- null and empty key routes;
- duplicate and terminal shelf chains;
- composite snapshots when their public tuple insertion contract is available;
- remaining fixed-shape tuple/count combinations beyond the current compaction fixture.

## Controlled Delivery

1. 🟩 [x] Connect exact shelf repack for all variable-record shapes.
2. 🟩 [x] Replace harness-only VS8 optimizer inputs with authoritative encoded tuple enumeration.
3. 🟩 [x] Connect catalog `Optimize` for VS8, VS16, VV, SV8, and SV16.
4. 🟩 [x] Add closed-file `Catalog.Compact(path, options)`.
5. 🟩 [x] Add open-catalog `catalog.Backup(path, options)` with an exact committed-image gate.
6. 🟩 [x] Connect Abraxas `CreateBackup` and archive backup to the live gate under the existing RecordBase maintenance/write-lock and overwrite policies.
7. 🟩 [x] Connect Abraxas close/compact/reopen through its non-thread-affine operation drain, weak live-store registry, exact persistent simple/regex/partial/State rebinding, volatile-handle preservation, and rollback retained until application-level rebinding succeeds.

## Abraxas Integration Boundary

Abraxas original-name maintenance now delegates to these native operations. `AssessIndexMaintenance`, `Optimize`, and `Repack` preserve the structured completion contract; `Compact` drains active native operations, closes the persistent catalog, invokes rollback-protected closed-file compaction, reopens it, and exactly rebinds every live store facade. The original file remains retained until every application handle has rebound. A reopen or rebind failure closes the rejected replacement, atomically restores the original, reopens it, and rebinds every facade again before reporting the failed maintenance attempt. Persistent handles are replaced by physical identity while memory-catalog handles remain untouched; only a failed restoration faults later indexed operations.

The drain deliberately continues admitting nested/reentrant native operations while maintenance is waiting for quiescence, preventing a caller already inside a lease from deadlocking itself. Once the active count reaches zero, admission closes atomically for the maintenance window. Continuous new traffic can therefore delay maintenance; callers that require a bound should supply cancellation.

Historical SQLite checkpoint/VACUUM/statistics behavior remains under explicit `Sqlite...` Abraxas compatibility names. The native APIs do not manufacture a false equivalent for SQLite `ANALYZE`.

Abraxas backup now composes the LibraDex committed-image gate with Fractal's engine-owned live snapshot operation. The RecordBase lifecycle drain first quiesces active native work; Fractal then excludes mutation publication and online tail defragmentation while flushing and staging its `.fractD`, `.fractD.shm`, `.fractX`, and `.fractX.shm` image. Restore installs that quartet with the `.lbdx` catalog and invokes Fractal ownership recovery before reopening. This contract is exact for `AssumeSingleProcess=true`; Fractal rejects multi-process-capable live backup until a cross-process engine gate or offline-only contract is deliberately added.
