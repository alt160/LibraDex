# Compressed nearest-route count repair (1.2.1)

## Symptom and invariant

An exact missing text key could count an entire neighboring shelf although actual identity enumeration returned zero. An Abraxas fixture with 9,239 indexed records reported 299 for `never-existed` and a previously deleted key, before and after reopening; enumeration and authoritative record scans both returned zero.

Compressed multi-byte routing deliberately selects a nearest route outside its declared interval: that route can legitimately own fallback keys after mutation. It must stay reachable, but its active query boundary must remain attached until complete-key filtering resolves its contents. The previous classifier dropped both boundaries for some such selections, incorrectly making the child appear fully contained to the metadata-count shortcut.

## Repair and scope

`RouterReader.TrySelectMultiByteRangeRoute` now propagates a selected boundary unless it exactly equals that route's inclusive synthetic bound. Route selection, rejection, persisted bounds, callers and catalog encoding are unchanged. Interior routes and exact-bound equality retain the existing metadata shortcut. The repair changes two Boolean assignments, adds no comparison, allocation, delegate, call layer, lock or I/O, and changes no public API or file format.

Rejecting nearest routes would lose valid fallback-owned keys. Adding an Abraxas existence preflight would add work and leave the shared VS8/VS16/VV invariant broken; neither workaround was used.

## Verification (2026-10-15)

- Line-by-line contrived cases covered missing keys before/after routes, interval gaps, existing keys, inclusive synthetic-bound equality, interior ranges, lower/upper/both/neither edges, short keys, zero/FF suffixes and descendant routing.
- `compressed-range-boundary-sanity --path <fresh-file>` checks actual VS8/VS16/VV reader enumeration against constructed-key oracles, live and reopened. The original implementation produced 368 count and 263 edge-flag mismatches across 8,624 checks; the repair produced zero of either. `--expect-bug` is only for characterizing the preserved original.
- Existing multi-byte range-count, count-all, compressed-parent fallback-split and 50,000-row native VS8 sorted-build checks passed. The fallback-split control retains valid ownership below an explicit parent interval.
- Abraxas's unchanged three-helper baseline passed 49,444 spill/mutation/reopen checks on each of .NET 8, 9 and 10 after this repair. The original failing persisted fixture then reported matching count/enumeration/scan results without rebuilding it.
- One million consumed warmed classifier iterations measured zero allocated bytes. Matching/interior classification and established Abraxas allocation controls were unchanged; this is a scoped regression control, not a general throughput benchmark.
- The NuGet package-consumer smoke test exercises shared-prefix exact misses, an existing key and a post-delete miss through public string-index APIs.

Harnesses and consumer fixtures are excluded from the library package and precompiled library archive.
