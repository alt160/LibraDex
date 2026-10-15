# Composite mutation and native-reader storage contract

## Root cause

Before 1.2.2, ordinary exact/predicate deletion and unordered replacement could publish the older composite snapshot representation. Exact deletion cleared the node-page root marker to avoid reusing stale pages; a subsequent insert continued snapshot publication. Native readers correctly rejected a missing page root, both immediately and after reopen. Logical record/index contents were not evidence of record loss.

## Repair

- Exact tuple and complete-key deletion copy the changed terminal-to-root route, reusing all other page offsets.
- Predicate deletion collects changed terminals and ancestors in its existing traversal; the existing bulk post-order publisher writes those nodes child-before-parent.
- Clear publishes one valid empty page root. Detached unordered replacement publishes one complete page tree and then replaces the live root/count. Validation, source-enumeration and cancellation failures before publication retain the old live index.
- Snapshot-format handles retain their existing ordinary read support; their next successful mutation can naturally publish pages. No read-time upgrade or automatic file migration is introduced.

No native cursor, row decoder, borrowed span/memory ownership, public signature or catalog format change. Exact writes use a depth-bounded route array, matching insertion; predicate writes use a changed-node set. Neither uses a whole-index entry array or snapshot encoder.

## Validation

Original-code negative control reproduces the precise missing-node-root failure at depths 1, 2, 8 and 16 in both file and memory catalogs. The same repaired cases pass exact/duplicate/key/predicate deletion, rekey, no-op/missing deletes, clear/repopulation, batch insertion, unordered/empty replacement, cancelled and failing source enumeration, and file reopen. Node-offset assertions prove exact-route and predicate-only page reuse. Existing 4,096-row borrowed UTF-8/raw-memory native traversal and 1,024-row depth-16 allocation control pass. Catalog, generic, public-surface, compaction and descending-order controls pass. The package smoke adds public typed composite-reader checks after mutation and reopen.

Abraxas integration before helper retirement passes on .NET 8/9/10, including the original overwrite trigger. This is focused regression evidence, not a universal throughput or fault-injection guarantee.
