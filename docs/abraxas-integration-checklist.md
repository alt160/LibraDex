# Abraxas Integration Checklist

## Purpose

Prepare LibraDex for a controlled Abraxas integration pass without treating the FileSearch workbench as product direction.

FileSearch remains a pressure-test workbench. Its value is that it supplied stable external identities and realistic filesystem-shaped data. It should not define how Abraxas should consume LibraDex.

## Current Position

- LibraDex appears ready for a narrow Abraxas read/query integration slice.
- The current dirty tree must be split or checkpointed before integration work begins.
- The first Abraxas pass should expose API, lifecycle, identity ownership, and adapter rough edges.
- Public promotion and package-readiness work remain separate.
- FileSearch write-side allocation attribution is useful engine evidence, but it should not block Abraxas unless the first Abraxas slice depends on date-heavy duplicate-key `PathString` identity inserts.

## HPC Guardrails

Apply these to every proposed integration, review, and checkpoint slice:

- Preserve low/no allocation hot paths.
- Keep hot-path call depth shallow.
- Avoid delegates, LINQ, closures, reflection, and virtual/interface dispatch in hot loops.
- Avoid generic machinery inside hot loops unless it is already proven erased or amortized outside the loop.
- Avoid per-row `byte[]` copies, string formatting, object materialization, and temporary lists in cursor/range/duplicate-run paths.
- Keep diagnostics opt-in or explicitly gated so steady-state users do not pay for workbench counters.
- Prefer byte-native cursor/index traversal over forced identity or record materialization.
- Treat convenience APIs as syntax only; they must normalize into the existing execution spine without adding layered runtime indirection.

## Slice 1 - Abraxas-Facing LibraDex Baseline

Goal: define the integration surface Abraxas should touch first.

Include:

- `Catalog/Catalog.cs`
- `Catalog/CatalogIdentityGroupBatchManager.cs`
- `Catalog/CatalogIndexFactories.cs`
- `Catalog/CatalogOptions.cs`
- `ConditionBuilder/LibraDexCondition.Materialization.cs`
- `PublicApi/*.cs`
- `LibraDex.csproj`
- `Properties/AssemblyInfo.cs`

Review checklist:

- [x] Confirm catalog/index-set/identity-group API shape is low-friction for Abraxas.
- [x] Confirm Abraxas condition intent maps through LibraDex condition materialization without a parallel selector.
- [x] Decide whether `PublicApi/LibraDexDiagnostics.cs` is public surface or internal/probe-only.
- [x] Confirm public convenience wrappers stay shallow and do not add hot-path delegate/generic/closure overhead.
- [x] Identify the smallest read/query-only Abraxas adapter contract.
- [x] Define validation for identity-returning queries before any hydrated-record path.

Exit criteria:

- [x] Abraxas integration has a named first slice.
- [x] The first slice returns caller-owned identities.
- [x] No broad mutation or record-hydration semantics are introduced.
- [x] HPC risks in public entry points are called out before coding.

## Slice 2 - Engine/Storage Correctness And HPC Baseline

Goal: isolate real engine work from FileSearch workbench code.

Include:

- `DataKernel/*.cs`
- `FileSession/*.cs`
- `Indexes/*.cs`
- `Layouts/Scalar8Scalar8Layout.cs`
- `Layouts/VarKeyScalar8Layout.cs`
- `Layouts/TerminalIdentity8ShelfLayout.cs`
- `Layouts/TerminalIdentityRootLayout.cs`
- `Layouts/TerminalVarIdentityShelfLayout.cs`
- `Routing/*.cs`
- `Views/*.cs`
- `Optimizers/LibraDexVarLenOptimizerReplacementPlanner.cs`

Review checklist:

- [x] Review `SV8`, `VV`, terminal duplicate routes, route walking, and range readers as engine work.
- [x] Confirm `DataKernel.ReserveAt` and pending-write reuse preserve correctness.
- [x] Preserve separation between raw discovery, identity materialization, and record/grid materialization.
- [x] Inspect duplicate-key paths for `List<T>`, temporary `byte[]`, repeated full-shelf copies, or avoidable object allocation.
- [x] Inspect route walking for deep helper chains or diagnostics paid by default.
- [x] Confirm any diagnostic allocation counters are disabled or cheap outside explicit probe runs.

Exit criteria:

- [x] Engine changes are understood independently from FileSearch.
- [x] Any remaining HPC concern is named with file/path and reproduction command.
- [x] No known correctness drift is being carried into Abraxas integration.

## Slice 3 - Workbench/Probe Only

Goal: preserve useful pressure-test tooling without letting it define product direction.

Include source only:

- `LibraDex.FileSearch/*.cs`
- `LibraDex.FileSearch/*.csproj`
- `LibraDex.FileSearch/Properties/AssemblyInfo.cs`
- `LibraDex.FileSearch.Runner/*.cs`
- `LibraDex.FileSearch.Runner/*.csproj`
- `LibraDex.Harness/Commands/RawHarness.FileSearchDogfood.cs`

Exclude generated output:

- `LibraDex.FileSearch/bin/**`
- `LibraDex.FileSearch/obj/**`
- `LibraDex.FileSearch.Runner/bin/**`
- `LibraDex.FileSearch.Runner/obj/**`

Review checklist:

- [x] Keep FileSearch described as a workbench over stable external identities.
- [x] Do not infer Abraxas product direction from FileSearch UI or workflow.
- [x] Keep source-only staging patterns if this workbench becomes tracked.
- [x] Do not stage generated `bin` or `obj` outputs.
- [x] Preserve useful runner commands for future pressure testing.

Exit criteria:

- [x] Workbench source is either intentionally tracked or intentionally left local.
- [x] Generated outputs are excluded from any checkpoint.
- [x] Docs do not present FileSearch as a production app or product surface.

## Slice 4 - Harness, Diagnostics, Docs

Goal: checkpoint evidence and validation without mixing temporary probes into core semantics.

Include:

- `LibraDex.Harness/Commands/RawHarness.DuplicateRuns.cs`
- `LibraDex.Harness/Commands/RawHarness.PublicApiSnapshot.cs`
- Modified `LibraDex.Harness/Commands/RawHarness.*.cs`
- `LibraDex.Harness/Program.cs`
- `DataKernel/DataKernelMemoryDiagnostics.cs`
- `PublicApi/LibraDexDiagnostics.cs`
- `docs/condition-builder/condition-execution-coverage.md`
- `docs/condition-builder/condition-execution-stability.md`
- `docs/file-search-pathstring-optimization-log.md`
- `docs/public-api-freeze-inventory.md`
- `docs/public-api-snapshot.txt`
- `docs/public-readiness-checklist.md`
- `LIBRADEX_DESIGN_CHECKLIST.md`

Review checklist:

- [x] Separate permanent diagnostics from temporary workbench counters.
- [x] Tighten FileSearch wording to "workbench/probe" where needed.
- [x] Preserve Abraxas integration as internal controlled dogfood, not public readiness.
- [x] Keep validation commands current and Windows/PowerShell-native.
- [x] Ensure diagnostics are opt-in/gated and not a default hot-path cost.

Exit criteria:

- [x] Restart docs identify the next Abraxas slice.
- [x] Public readiness remains distinct from Abraxas dogfood integration.
- [x] FileSearch evidence is framed as workload pressure, not product direction.

## Completion Notes - 2026-06-18

The first Abraxas integration slice is named `AbraxasIdentityQueryAdapter`.

Contract:

- Abraxas supplies condition/query intent.
- LibraDex materializes the condition through the existing condition-builder execution spine.
- LibraDex returns caller-owned identities only.
- Abraxas owns record hydration outside LibraDex.
- Mutation, record storage, and public package claims stay out of this slice.

Decisions:

- `PublicApi/LibraDexDiagnostics.cs` remains a public fixed-field diagnostics surface, but catalog diagnostics now default to `Off` so steady-state callers do not pay for timing/counter collection.
- `CatalogIdentityGroupIndexes.GetTuples(...)` remains an adapter/workbench materialization surface, not the first Abraxas hot path.
- FileSearch remains an uncommitted workbench/probe over stable external identities; it is not product direction for Abraxas.
- If FileSearch source is checkpointed, use source-only staging and exclude `bin/**` and `obj/**`.

HPC fixes made during this checklist pass:

- `Indexes/LibraDexUInt64VarIdentityIndex.cs` now counts `SV8` variable identities through `Scalar8VarIdentityRangeReader.Count` instead of materializing `byte[][]`.
- `Indexes/LibraDexUInt64VarIdentityIndex.cs` now iterates identities through `Scalar8VarIdentityRangeReader.MoveNext()` and copies only returned caller-owned identity bytes.
- `Indexes/LibraDexUInt64VarIdentityIndex.cs` now implements `IIdentityPrimitiveTupleStreamer` so read-only tuple adapters can avoid an intermediate primitive tuple list.
- `Catalog/CatalogIndexFactories.cs` now prefers tuple streamers for direct tuple adapter paths when available.
- `Catalog/CatalogOptions.cs` defaults `DiagnosticsLevel` to `Off`.
- `DataKernel/DataKernel.cs` no longer starts commit elapsed timing when telemetry is disabled.
- `Indexes/ScalarVarIdentityIndexes.cs` no longer samples thread allocation counters in normal `SV8` in-scope inserts.
- `Indexes/LibraDexScalar8VarIdentitySession.cs` gates `SV8` allocation attribution behind the explicit attribution overload.
- `FileSession/LibraDexFileSession.cs` gates `VS8` route/write allocation probes behind the explicit phase-allocation diagnostics object.

Validation:

- `git diff --check`
- `LibraDex.csproj` Release/x64 rebuild with `artifacts\build-libradex-release-x64-abraxas-checklist-20260618-r3.log`
- `LibraDex.Harness\LibraDex.Harness.csproj` Release/x64 rebuild with `artifacts\build-harness-release-x64-abraxas-checklist-20260618-r3-rerun.log`
- `dotnet LibraDex.Harness\bin\x64\Release\net8.0\LibraDex.Harness.dll sv8-index-api-sanity --path artifacts\sv8-index-api-sanity-abraxas-checklist-20260618-r3.lbdx`
- `dotnet LibraDex.Harness\bin\x64\Release\net8.0\LibraDex.Harness.dll catalog-api-sanity --path artifacts\catalog-api-sanity-abraxas-checklist-20260618-r3.lbdx`
- `dotnet LibraDex.Harness\bin\x64\Release\net8.0\LibraDex.Harness.dll public-surface-api-sanity --path artifacts\public-surface-api-sanity-abraxas-checklist-20260618-r3.lbdx`

Known boundary:

- Direct tuple materialization and `LibraDexRuntimeTuple` stay acceptable only for diagnostics/adapters/workbench views. The first Abraxas path should consume identity projection results, not tuple/materialized record grids.
- The current tree is still broad and dirty. Checkpointing should split core/query/HPC changes from FileSearch workbench source and docs.

## AbraxasIdentityQueryAdapter Slice - 2026-06-18

Status: implemented as the first narrow read/query integration boundary.

Shape:

- `CatalogIdentityGroupIndexes.AbraxasIdentityQuery<TIdentity>()` binds one catalog identity group.
- `AbraxasIdentityQueryAdapter<TIdentity>` accepts completed condition descriptors and returns typed caller-owned identities.
- `Get(...)` / `GetIdentities(...)` materialize identity lists through the existing catalog condition bridge.
- `OpenCursor(...)` / `GetCursor(...)` return the existing LibraDex identity cursor so the adapter does not add a per-row wrapper or callback loop.
- Record hydration, mutation, tuple/materialized record grids, and FileSearch workflow assumptions remain outside the adapter.

Validation:

- `LibraDex.csproj` Release/x64 rebuild with `artifacts\build-libradex-release-x64-abraxas-adapter-20260618.log`
- `LibraDex.Harness\LibraDex.Harness.csproj` Release/x64 rebuild with `artifacts\build-harness-release-x64-abraxas-adapter-r2-20260618.log`
- `dotnet LibraDex.Harness\bin\x64\Release\net8.0\LibraDex.Harness.dll public-surface-api-sanity --path artifacts\public-surface-api-sanity-abraxas-adapter-r2-20260618.lbdx`
- `dotnet LibraDex.Harness\bin\x64\Release\net8.0\LibraDex.Harness.dll public-api-snapshot --path docs\public-api-snapshot.txt`

## External AbraxasDB Binding - 2026-06-18

Status: first AbraxasDB-side bridge implemented after creating a source-control baseline for `E:\VSProjects\AbraxasDB`.

AbraxasDB commits:

- `82fa25e Baseline AbraxasDB source`
- `68087e9 Add LibraDex identity query bridge`

Shape:

- `AbraxasDB.csproj` references `..\LibraDex\LibraDex.csproj`.
- `AbraxasDB.LibraDexIdentityQuery` provides Abraxas-owned helpers over LibraDex's `AbraxasIdentityQueryAdapter<TIdentity>`.
- `AbraxasTestHarness` has an early `libradex-identity-query-sanity` command that creates a LibraDex memory catalog, indexes caller-owned identities, executes a completed LibraDex condition through the AbraxasDB bridge, and verifies both materialized list and cursor identity results.
- This remains a binding/proof slice, not a full Abraxas SQL-fragment-to-LibraDex-condition translator.

Validation:

- `AbraxasDB.csproj` Release rebuild with `artifacts\build-abraxasdb-release-libradex-integration-20260618.log`
- `AbraxasTestHarness\AbraxasTestHarness.csproj` Release rebuild with `artifacts\build-abraxastestharness-release-libradex-integration-20260618.log`
- `dotnet AbraxasTestHarness\bin\Release\net8.0\AbraxasTestHarness.dll libradex-identity-query-sanity`

## Recommended Order

1. Review Slice 1 to define the Abraxas integration contract.
2. Review Slice 2 for correctness and HPC risks that could poison Abraxas testing.
3. Decide whether Slice 3 is tracked as a durable workbench or left local.
4. Update docs in Slice 4 after the integration baseline is understood.

## First Abraxas Integration Shape

Start with read/query only:

- Abraxas supplies condition/query intent.
- LibraDex executes against existing identity-group indexes.
- LibraDex returns caller-owned identities.
- Record hydration stays outside LibraDex.
- Mutation is deferred until read/query identity semantics are boring.

Avoid in the first slice:

- Broad mutation.
- Record storage semantics.
- FileSearch workflow assumptions.
- Public package claims.
- New hot-path abstractions that add allocation, delegates, reflection, or deep call chains.

## Restart Prompt

Start in `E:\VSProjects\LibraDex`. Read `docs/abraxas-integration-checklist.md`, then check `git status --short --untracked-files=all` and `git diff --stat`. The checklist pass, LibraDex `AbraxasIdentityQueryAdapter` slice, and first AbraxasDB-side identity-query binding are complete as of 2026-06-18. LibraDex exposes `CatalogIdentityGroupIndexes.AbraxasIdentityQuery<TIdentity>()` and `AbraxasIdentityQueryAdapter<TIdentity>`. AbraxasDB branch `codex/abraxas-libradex-integration` contains baseline commit `82fa25e` and bridge commit `68087e9`. The next task is the real translator decision: map a focused subset of Abraxas condition-builder intent into LibraDex condition descriptors, starting with exact/range numeric selectors over named identity-group indexes, while preserving caller-owned identity results and keeping hydration/mutation outside the slice. Keep HPC guardrails active throughout: low/no allocations, shallow hot paths, no delegates/closures/LINQ/reflection in hot loops, byte-native cursors before materialization, and diagnostics gated away from steady-state execution. Do not evolve FileSearch except as a workbench/probe, and do not promote LibraDex publicly as part of this slice.
