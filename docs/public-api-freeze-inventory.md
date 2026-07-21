# LibraDex Public API Freeze Inventory

Purpose: track the exported API surface that must be frozen, marked experimental, or hidden before a public package release.

## Snapshot

- Date: 2026-06-12.
- Build command:
  `C:\Program Files\Microsoft Visual Studio\18\Insiders\MSBuild\Current\Bin\MSBuild.exe LibraDex.csproj /t:Rebuild /p:Configuration=Release /p:Platform=x64 /m:1 /v:minimal /fl /flp:"logfile=artifacts\build-lib-release-x64-public-api-inventory-20260612.log;verbosity=normal"`
- Reflected assembly: `bin\x64\Release\net8.0\LibraDex.dll`.
- Exported public type count: `284`.
- 2026-06-12 visibility cleanup reduced exported public type count to `213`.
  - Build log: `artifacts\build-lib-release-x64-api-visibility-pass7-20260612.log`.
  - Removed from public export: `Indexes` direct factory root, `LibraDexFileSession`, router snapshots, index-directory snapshots, superblock snapshots, raw reservation/extent types, raw data-kernel options, raw telemetry options, raw read telemetry, backing-kind enum, superblock developer metadata, and physical shelf/index wrappers for the direct scalar/var-key shapes.
  - Post-cleanup exported type kind counts:
    - `123` classes.
    - `36` structs.
    - `43` enums.
    - `6` static classes.
    - `5` other exported type forms.
- 2026-06-12 diagnostics cleanup changed the exported public type count to `214`.
  - Build log: `artifacts\build-lib-release-x64-diagnostics-cleanup-pass4-20260612.log`.
  - `DataKernelCommitTelemetry` is no longer public.
  - Post-diagnostics exported type kind counts:
    - `123` classes.
    - `36` structs.
    - `44` enums.
    - `6` static classes.
    - `5` other exported type forms.
  - New public diagnostics surface:
    - `LibraDexDiagnosticsLevel`
    - `LibraDexOperationDiagnostics`
  - `CatalogOptions.DiagnosticsLevel` now exposes the public diagnostics knob.
- Exported type kind counts:
  - `152` classes.
  - `67` structs.
  - `46` enums.
  - `14` static classes.
  - `5` other exported type forms.

## Initial Finding

The current assembly exports both the intended low-friction user surface and many implementation-layer storage/index/router contracts. That is acceptable for internal prototype use, but it is too broad to freeze as a public package contract without explicit policy.

No public visibility changes should be made until this inventory is reviewed. The high-performance concern is that hiding or reshaping APIs must not add wrappers, delegates, generic indirection, exception-heavy flow, or extra call-chain depth to hot paths. Prefer direct visibility changes, facade documentation, or compile-time placement decisions over runtime abstraction.

## Raw Terminology Notes

These notes are intentionally raw. Do not treat them as polished public docs yet; later quickstarts, website pages, and API docs can adapt the same concepts to their context.

- `Catalog`: the entirety of one LibraDex instance or session.
  - A catalog may be memory-backed, disk-backed, or eventually mixed.
  - Public code should treat `Catalog` as the canonical top-level noun.
- `IndexSet`: a collection of indexes that share one identity universe.
  - Conditions that combine multiple indexes should normally do so inside one index set.
  - The shared identity universe is the reason an `AND`, `OR`, inverse lookup, selective delete, or selective mutation can target the same identities across different indexes.
- `Index`: the public lookup/retrieval structure.
  - An index is the developer-facing object that supports fast exact lookup, range retrieval, projection-backed lookup, and visible fallback behavior where documented.
  - Public docs should keep `Index` focused on lookup semantics, not physical layout.
- `Condition`: the fluent selector shape over one or more indexes.
  - Conditions are the public way to express read selectors and selective delete/mutate selectors.
  - Conditions may resolve to fast paths, projection-backed paths, composed identity-set paths, or visible scan/residual paths depending on the indexed shape.
- `Router`: an internal physical object that uses key prefixes to route to shelves.
  - Router types may appear in diagnostics or architecture notes, but should not be treated as a stable extension surface before an explicit decision.
- `Shelf`: an internal physical block of ordered key/identity entries stored in bytes.
  - Shelves are performance and file-format concepts.
  - Public docs can mention shelves when explaining performance or diagnostics, but normal API examples should not require them.
- `ShelfSlotArray`: an internal physical ordering structure for entries on a shelf.
  - This is lower-level than most public users should need.
  - Keep it out of public examples unless explaining storage internals.
- `DataKernel`: internal storage/session machinery for file and memory backing.
  - Keep this out of the canonical public API unless a specific advanced consumer scenario justifies exposing it.

## Intended Canonical Surface

These groups look like the public contract that should be documented first:

- Catalog-first lifecycle:
  - `Catalog`
  - `CatalogOptions`
  - `CatalogIndexFactories`
  - `CatalogIdentityGroupIndexes`
  - named catalog/index-set builders
  - catalog maintenance, stats, tools, and compatibility helpers
- Condition builder and execution descriptors:
  - `LibraDexCondition`
  - `LibraDexCondition*`
  - `LibraDexCompositeCondition*`
  - `LibraDexInverse*`
  - `IIndex`
  - `IIdentityCriterion`
  - `IIdentityCriterionProjection`
  - `IIdentityCriterionMutation`
  - `IIdentityCriterionMutationBuilder`
- Public options and intent descriptors:
  - `IndexOptions`
  - `IndexKeys`
  - `StringKeys`
  - `GuidKeys`
  - `DateKeys`
  - `DateTimeKeyEncoding`
  - `LibraDexStringComparisonPolicy`
  - `LibraDexWriteIntent`
  - query, aggregate, retrieval, projection, execution, and compatibility enums.
- Disconnected read buffers and spans, if the package wants a zero-copy advanced API:
  - `LibraDexVarIdentityBuffer`
  - `LibraDexVarKeyBuffer`
  - `LibraDexVarKeyIdentityBuffer`
  - `LibraDexVarKeyIdentitySpan`

2026-06-12 decision: keep disconnected read buffers public as advanced read APIs. They are caller-owned/disconnected result containers, not storage internals, and they preserve low-allocation read options for variable-key and variable-identity shapes.

## Compatibility Or Legacy Surface

These groups may be useful, but should be explicitly labeled before public release:

- Direct `Indexes` factory surface and nested fixed-shape families:
  - `Indexes`
  - `Indexes.SV8`
  - `Indexes.SV16`
  - `Indexes.VS8`
  - `Indexes.VS16`
  - `Indexes.VV`
  - `Indexes.SS88`
  - `Indexes.SS88.Unsigned`
- Direct runtime index wrappers:
  - `LibraDexIndex<TKey, TIdentity>`
  - `LibraDexCompositeIndex<...>`
  - `LibraDexRoutedCompositeIndex`
  - scalar/string/big-int direct wrappers.

Recommended policy: keep only if they are intentionally supported as low-friction escape hatches. Otherwise mark as experimental before external use.

2026-06-12 decision: the direct `Indexes.*` factory surface is no longer public. It remains internal for harness and implementation use through `InternalsVisibleTo("LibraDex.Harness")`.

2026-06-12 decision: keep logical typed facades public when they represent a developer-facing key or identity family rather than a physical shelf shape. This includes string and BigInteger facades such as `LibraDexStringScalar8Index`, `LibraDexBigIntScalar8Index<TIdentity>`, and `LibraDexBigIntVarIdentityIndex`. Their names still need final polish, but their public purpose is logical API, not storage internals.

## Likely Accidental Or Internal Surface

These groups look implementation-facing and should be reviewed for `internal` visibility before package release:

- Data kernel and raw reservation layer:
  - `DataKernel`
  - `DataKernelOptions`
  - `DataKernelBackingKind`
  - `DataKernelTelemetryOptions`
  - `DataKernelCommitTelemetry`
  - `DataKernelReadTelemetry`
  - `RawDataExtent`
  - `RawDataReservation`
- Router, directory, superblock, and layout snapshots:
  - `RouterSnapshot`
  - `RouterRouteSnapshot`
  - `RouterMultiByteRouteSnapshot`
  - `RouteChainCreationResult`
  - `IndexDirectorySnapshot`
  - `IndexDirectorySlotSnapshot`
  - `SuperblockSnapshot`
  - `SuperblockDeveloperMetadata`
  - `LibraDexLayoutStats`
  - `LibraDexReclaimedPayloadStats`
  - `LibraDexStatsMarker`
  - `LibraDexStatsDelta`
- Physical shelf/index contracts:
  - `Scalar8Scalar8Index`
  - `Scalar8Scalar8Batch`
  - `Scalar8Scalar8RangeReader`
  - `Scalar8Scalar16RangeReader`
  - `Scalar16Scalar8RangeReader`
  - `Scalar16Scalar16RangeReader`
  - `Scalar8VarIdentityIndex`
  - `Scalar16VarIdentityIndex`
  - `VarKeyScalar8Index`
  - `VarKeyScalar16Index`
  - `VarKeyVarIdentityIndex`
  - unsigned variants and fixed32 range readers.
- Physical insert/range/batch result shapes:
  - `Scalar8Scalar8EncodedInsertOutcome`
  - `Scalar8Scalar8EncodedInsertResult`
  - `Scalar8Scalar8EncodedRangeReadResult`
  - `Scalar8Scalar8BatchCommitResult`
  - `Scalar8Scalar8BatchAbortResult`
  - scalar-var, var-scalar, var-var, and unsigned equivalents.

Recommended policy: move these internal unless a specific external scenario needs them. If kept, document them as advanced and unsupported for compatibility until the file-format/versioning policy is finished.

2026-06-12 cleanup result:

- Moved internal:
  - `DataKernel`
  - `DataKernelOptions`
  - `DataKernelBackingKind`
  - `DataKernelTelemetryOptions`
  - `DataKernelReadTelemetry`
  - `RawDataExtent`
  - `RawDataReservation`
  - `LibraDexFileSession`
  - `RouterSnapshot`
  - `RouterRouteSnapshot`
  - `RouterMultiByteRouteSnapshot`
  - `RouteChainCreationResult`
  - `IndexDirectorySnapshot`
  - `IndexDirectorySlotSnapshot`
  - `SuperblockSnapshot`
  - `SuperblockDeveloperMetadata`
  - physical direct scalar/var-key/unsigned index, batch, range-reader, and encoded-result types.
- Still public by current catalog-facing API:
  - `LibraDexOperationDiagnostics`, because generic insert and batch result records expose public fixed-field diagnostics.
  - `LibraDexGenericInsertResult`, `LibraDexGenericBatchCommitResult`, `LibraDexGenericBatchAbortResult`, and `LibraDexGenericRangeReadResult`, because they are part of `IIndex`, `LibraDexIndex<TKey,TIdentity>`, and batch manager results.
  - `LibraDexRangeReader<TKey,TIdentity>`, `LibraDexBatch<TKey,TIdentity>`, `IndexBatchManager<TKey,TIdentity>`, and `CatalogIdentityGroupBatchManager`, because they are public operational surfaces. `IndexBatchManager<TKey,TIdentity>` is reached through the `Batch` property; the longer `BatchManager` property is no longer public.
  - Disconnected variable-key/identity buffers, pending the buffer policy decision.

## Freeze Decisions Needed

- `[x]` Decide whether `Catalog` is the only canonical construction root for public docs.
  - Decision: yes. Keep `Catalog` as the canonical top-level public noun.
- `[x]` Decide whether direct `Indexes.*` factories remain supported or are compatibility/experimental.
  - Decision: not public for the first public-ready surface. The direct factory root remains internal for validation and implementation use.
- `[x]` Decide whether low-level `DataKernel`, router, superblock, and physical shelf types are public by design.
  - Decision: no. Review them for `internal` visibility unless a specific external scenario is identified.
- `[x]` Decide whether disconnected buffers are canonical advanced APIs or internal implementation details.
  - Decision: keep public as advanced, caller-owned disconnected read APIs.
- `[x]` Decide whether `DataKernelCommitTelemetry` should be renamed or wrapped before public release so generic public results do not expose the `DataKernel` noun.
  - Decision: public results now expose `LibraDexOperationDiagnostics`; `DataKernelCommitTelemetry` remains internal.
- `[x]` Decide whether mutation and condition builder aliases need attributes, docs, or naming cleanup before the first package.
  - Decision: keep aliases that materially reduce developer friction or preserve Abraxas-compatible copied grammar.
  - Removed pre-public scaffold/result aliases that would freeze stale names: `LibraDexIndex<TKey,TIdentity>.BatchManager`, `LibraDexGenericInsertResult.RouteCreateCommit`, `LibraDexGenericInsertResult.InsertCommit`, and `LibraDexGenericBatchCommitResult.Commit`.
- `[x]` Decide whether to add an automated public API snapshot gate once the first surface is accepted.
  - Decision: add the `public-api-snapshot` harness command and track the first baseline at `docs/public-api-snapshot.txt`.
  - Compare command: `dotnet run --project LibraDex.Harness\LibraDex.Harness.csproj -c Release -- public-api-snapshot`.
  - Intentional updates require `--update` after reviewing the public API diff.

## Reproduce The Inventory

PowerShell reflection command:

```powershell
$asmPath = (Resolve-Path 'bin\x64\Release\net8.0\LibraDex.dll').Path
$asm = [System.Reflection.Assembly]::LoadFrom($asmPath)
$types = $asm.GetExportedTypes() | Sort-Object FullName
"Assembly: $asmPath"
"Exported type count: $($types.Count)"
$types | Group-Object Namespace | Sort-Object Name | ForEach-Object {
  ""
  "Namespace: $($_.Name) ($($_.Count))"
  $_.Group | ForEach-Object { "  $($_.FullName)" }
}
```

## Next Step

Review the policy decisions above, then make the smallest visibility/documentation changes needed to prevent accidental API freeze. Do not add runtime abstraction to hide types; prefer source-level visibility and documentation decisions.

Condition execution stability is tracked separately in `docs/condition-builder/condition-execution-stability.md`.
