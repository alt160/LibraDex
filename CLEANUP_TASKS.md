# LibraDex Cleanup Tasks

This checklist tracks repository cleanup work so future conversations can resume without reconstructing the current findings from chat.

## Status Legend

- `[ ]` Not started
- `[~]` In review or partially done
- `[x]` Done

## Code Reduction Rule

Cleanup work should reduce net code volume or clearly improve density without hiding important behavior.

For every cleanup slice from this point forward, record:

1. Files changed.
2. Approximate line delta.
3. Net density result:
   - `reduced`
   - `neutral but simpler`
   - `increased, justified`
   - `increased, reject/rework`
4. Short rationale when the line count does not decrease.

Default bar: reject or rework changes that add net code unless they remove meaningful duplication, reduce call depth, preserve hot-path clarity, or improve correctness coverage enough to justify the increase.

## 0. Artifact Cleanup

- `[x]` Archive ignored/untracked files under `artifacts/` while preserving tracked durable artifacts.
  - Archived 2,155 files from `artifacts/` into `C:\VSProjects\LibraDex.artifact-archives\libradex-artifacts-20260607-222613.zip`.
  - Live tracked artifacts were intentionally preserved, then moved on 2026-06-11 into `docs/condition-builder/` so new durable notes are not hidden by the ignored `artifacts/` folder:
    - `docs/condition-builder/condition-query-intent-proof.md`
    - `docs/condition-builder/filter-dx-corpus-regeneration-capabilities.md`
    - `docs/condition-builder/filter-dx-natural-language-corpus.csv`
    - `docs/condition-builder/condition-execution-coverage.md`
    - `docs/condition-builder/filter-dx-ai-remaining-work.md`
  - `artifacts/` remains the local generated-output/archive area.
  - Git status was clean after the archive.

- `[x]` Review and clean remaining scratch/build artifacts.
  - Archived 397 files into `C:\VSProjects\LibraDex.artifact-archives\libradex-scratch-build-20260608-075058.zip`.
  - Removed cleaned source targets after archive verification:
    - `.tmp/`
    - `bin/`
    - `obj/`
    - `.tmp_fs32_16_session_members.txt`
  - Prefer archive-first cleanup for non-trivial directories.
  - Prefer `.7z` when `7z` or `7za` is available; fall back to `.zip` when only PowerShell archive support is available.
  - Keep build output cleanup separate from benchmark/data artifact cleanup so rollback is easy.

- `[x]` Move durable condition-builder materials out of ignored artifact space and archive the remaining live local outputs.
  - Durable files now live under `docs/condition-builder/`.
  - Archived 59 top-level files from `artifacts/` into `C:\VSProjects\LibraDex.artifact-archives\libradex-live-artifacts-20260611-163919.zip`.
  - Archived 52 nested run-output files from 5 directories into `C:\VSProjects\LibraDex.artifact-archives\libradex-run-artifact-dirs-20260611-163919.zip`.
  - Removed the archived files/directories after archive verification; `artifacts/` is intentionally empty local output space.

## 1. Harness Density

- `[x]` Review `LibraDex.Harness\Program.cs`.
  - Current concern: one `RawHarness` file owns the command router and tens of thousands of lines of validation, benchmark, spike, and report code.
  - Mechanical split completed:
    - `LibraDex.Harness\Program.cs` now keeps the top-level entrypoint, constants, and command dispatcher.
    - `LibraDex.Harness\Program.cs` dispatches two primary lanes: validation/correctness commands and performance/comparison commands.
    - Existing one-token harness commands remain available as compatibility aliases into those lanes.
    - `LibraDex.Harness\Program.cs` now uses one command metadata list for lane membership and dispatch, removing the duplicated membership switches.
    - `RawHarness.FixedScalarShapes.cs` now shares the fixed-shape eight-index reopen traversal while keeping direct shape-specific tuple validators.
    - `RawHarness.VarLenShapes.cs` now uses one var-identity attribution row printer for both `SV8` and `SV16`.
      - Files changed: `RawHarness.VarLenShapes.cs`, `RawHarness.PublicSurface.cs`.
      - Approximate line delta: `-19` code lines.
      - Net density result: `reduced`.
    - Varlen SQLite comparison helpers now share one range-read command builder and a denser iteration-mode parser.
      - Files changed: `RawHarness.PublicSurface.cs`, `RawHarness.ValidationPerf.cs`, `RawHarness.VarLenShapes.cs`.
      - Approximate line delta: `-70` code lines.
      - Net density result: `reduced`.
      - Skipped checksum-helper consolidation because the helpers intentionally differ by integer/BLOB payload shape and key/identity touch behavior.
    - Public-surface report writers now share current-report preparation and final report write helpers.
      - Files changed: `RawHarness.PublicSurface.cs`.
      - Approximate line delta: `-70` code lines.
      - Net density result: `reduced`.
      - Report content builders remain local; only repeated directory/archive/write boilerplate was collapsed.
    - Public read baseline lookups now share paired throughput/latency scenario mappings.
      - Files changed: `RawHarness.PublicSurface.cs`, `RawHarness.FixedScalarShapes.cs`.
      - Approximate line delta: `-35` code lines.
      - Net density result: `reduced`.
      - The helper keeps each scenario's identities/sec and ns/identity baselines in one switch to prevent mapping drift.
    - Removed stale `fixed-byte-compare-spike` command family.
      - Files changed: `Program.cs`, `RawHarness.ValidationPerf.cs`, `RawHarness.Support.cs`.
      - Approximate line delta: `-375` code lines.
      - Net density result: `reduced`.
      - The spike had no references outside its command registration and spike-only support types/helpers.
    - Fixed stale harness read-telemetry expectations exposed by `validate --tier fast`.
      - Files changed: `RawHarness.FixedScalarShapes.cs`, `RawHarness.PublicSurface.cs`.
      - Approximate line delta: `+50` code lines.
      - Net density result: `increased, justified`.
      - Rationale: assertions now model legal router-arena first-touch and cached-page read shapes while preserving target, count, checksum, and strict read-count checks where applicable.
    - Command/support implementations were moved into partial `RawHarness` category files under `LibraDex.Harness\Commands\`.
  - Completed actions:
    - Category split is stable enough for current cleanup scope; further file moves should happen only when a new density-positive cluster is found.
    - Removed stale spike/perf code that was clearly outside current validation.
      - Removed obsolete two-index `RunMultiIndexSameIdentitiesSanity` path after confirming the active command dispatches to the stronger eight-index validation.
      - Removed the old-only `ValidateMultiIndexSameIdentitiesReopen` helper.
      - Removed `fixed-byte-compare-spike` and its spike-only helpers/types.
    - Current regression anchors remain discoverable through the command metadata table.
  - Completion inventory:
    - Command metadata: 161 commands, 108 validation, 53 performance, 0 duplicate entries.
    - Undispatched `Run*` methods: 3, all internal sub-checks for `identity16-split-preservation-sanity`.
    - Explicit stale markers: no remaining `spike`, `obsolete`, `deprecated`, `TODO`, or `REVIEW` markers in harness code except ordinary "temporary" wording for generated files/runtime data.
  - Risk: deleting old harness paths can remove useful historical reproduction commands.
  - Validation expectation:
    - Run the focused harness commands that correspond to any moved or removed command family.

## 2. File Session Shape Duplication

- `[x]` Review `LibraDexFileSession.cs`.
  - Current concern: shape-by-shape routing/read/write code repeats across scalar, fixed, and varlen shapes.
  - Detailed tracker: `docs/file-session-shape-cleanup.md`.
  - Completed result:
    - Consolidated cold root-router shelf-link setup helpers in `LibraDexFileSession.cs`.
    - Preserved direct shape-specific code in batch mutation, range traversal, and split paths after HPC-sensitive review.
    - Closed remaining candidate clusters as intentional specialization unless a future focused prompt justifies fresh edits.
  - Candidate actions:
    - Identify repeated `Create*`, `Read*Range`, `TryCreate*SplitShelves`, and `*ShelfAndLinkRootRoute*` families.
    - Consolidate only where it reduces call depth or duplicate policy code without harming hot-path performance.
    - Preserve direct byte-path helpers when abstraction would add avoidable overhead.
  - Risk: this file owns hot storage paths, cache behavior, and routed mutation semantics.
  - Validation expectation:
    - Run focused shape sanity checks before any broader solution rebuild.

## 3. Condition Builder Split

- `[x]` Review condition-builder files.
  - Current concern: public fluent grammar, operands, grouped query support, composite condition builders, and materialization live in one large file.
  - Project-wide folder organization completed before semantic condition-builder splitting:
    - Moved production root `.cs` files into topic folders:
      - `Buffers\`
      - `Catalog\`
      - `Codecs\`
      - `ConditionBuilder\`
      - `DataKernel\`
      - `FileSession\`
      - `Indexes\`
      - `Optimizers\`
      - `PublicApi\`
      - `Routing\`
    - Existing `Layouts\`, `Views\`, `Properties\`, and harness folders were preserved.
    - Files changed: production `.cs` files moved only; no code body edits intended.
    - Approximate line delta: `0` code lines.
    - Net density result: `neutral but simpler`.
    - Rationale: physical project folders reduce root bloat and make later density passes reviewable by subsystem without changing public syntax or runtime behavior.
  - Candidate actions:
    - Split public fluent API from descriptor/model types.
      - Completed into:
        - `ConditionBuilder\LibraDexCondition.Entry.cs`
        - `ConditionBuilder\LibraDexCondition.Descriptors.cs`
        - `ConditionBuilder\LibraDexCondition.Terminals.cs`
        - `ConditionBuilder\LibraDexCondition.Groups.cs`
        - `ConditionBuilder\LibraDexCondition.Fluent.cs`
        - `ConditionBuilder\LibraDexCondition.Composite.cs`
        - `ConditionBuilder\LibraDexCondition.Materialization.cs`
      - Files changed: `ConditionBuilder\LibraDexConditionBuilder.cs` split into the seven files above.
      - Approximate line delta: `+31` production code lines from repeated file headers plus the grouped identity comparer guard.
      - Net density result: `increased, justified`.
      - Rationale: the split materially reduces review scope and call-chain navigation cost for the condition subsystem; the small increase comes from file headers and the correctness fix.
    - Move materialization and bridge logic into a focused internal file.
      - Completed in `ConditionBuilder\LibraDexCondition.Materialization.cs`.
    - Keep canonical `.Not` and grouped condition syntax as the primary public surface.
      - Preserved.
    - Fix grouped query identity matching for `byte[]` identities.
      - Completed in `ConditionBuilder\LibraDexCondition.Groups.cs` by using `LibraDexKeyEquality<TIdentity>.Comparer` for the condition identity set.
      - Added public-surface harness coverage for grouped `byte[]` identities.
      - Approximate harness line delta: `+45` lines.
      - Net density result: `increased, justified`.
      - Rationale: prevents array reference equality from splitting equivalent binary identities during condition-level grouping.
    - Consolidate terminal dictionary resolver and target-index lookup logic.
      - Completed in `ConditionBuilder\LibraDexCondition.Terminals.cs`.
      - Files changed: `ConditionBuilder\LibraDexCondition.Terminals.cs`.
      - Approximate line delta: `-42` lines.
      - Net density result: `reduced`.
      - Rationale: shared resolver helpers preserve the same missing-index diagnostics while removing repeated dictionary lookup blocks; nearby one-return terminal wrappers were collapsed to expression-bodied members to keep the slice net-reducing.
    - Remove projection materialization noise.
      - Completed in `ConditionBuilder\LibraDexCondition.Materialization.cs`.
      - Files changed: `ConditionBuilder\LibraDexCondition.Materialization.cs`.
      - Approximate line delta: `-9` lines.
      - Net density result: `reduced`.
      - Rationale: projection leaves now use the same explicit operand materializer as primitive leaves, and a duplicated XML summary block was removed.
    - Collapse trivial fluent one-return methods.
      - Completed in `ConditionBuilder\LibraDexCondition.Fluent.cs`.
      - Files changed: `ConditionBuilder\LibraDexCondition.Fluent.cs`.
      - Approximate line delta: `-356` lines.
      - Net density result: `reduced`.
      - Rationale: preserves the full public fluent syntax and XML documentation while removing boilerplate braces from methods that already performed exactly one return.
    - Collapse trivial composite one-return methods.
      - Completed in `ConditionBuilder\LibraDexCondition.Composite.cs`.
      - Files changed: `ConditionBuilder\LibraDexCondition.Composite.cs`.
      - Approximate line delta: `-114` lines.
      - Net density result: `reduced`.
      - Rationale: preserves the public composite condition syntax and XML documentation while removing boilerplate braces from methods that already performed exactly one return.
    - Collapse remaining trivial one-return condition-builder methods.
      - Completed across:
        - `ConditionBuilder\LibraDexCondition.Descriptors.cs`
        - `ConditionBuilder\LibraDexCondition.Groups.cs`
        - `ConditionBuilder\LibraDexCondition.Terminals.cs`
        - `ConditionBuilder\LibraDexCondition.Materialization.cs`
        - `ConditionBuilder\LibraDexConditionCursors.cs`
        - `ConditionBuilder\LibraDexIndexConditions.cs`
      - Approximate line delta: `-225` lines.
      - Net density result: `reduced`.
      - Rationale: preserves public syntax, XML documentation, and behavior while removing brace boilerplate from 75 methods that already performed exactly one return.
    - Current condition-builder split total:
      - Seven split files total approximately `10,821` lines versus the original tracked `11,371` line monolith.
      - Net condition-builder result after split plus reductions: approximately `-550` lines.
      - Adjacent condition cursor/index-condition density pass reduced another approximately `-114` lines outside the seven-file monolith replacement.
  - Risk: condition-builder syntax is active public DX work, so accidental stale aliases or documentation drift are likely.
  - Validation expectation:
    - Run `public-surface-api-sanity` after any split or removal.
    - Folder-only move validation: `LibraDex.csproj` Release/x64 rebuild.
    - Split/comparer validation completed:
      - `dotnet run --project LibraDex.Harness -c Release -- public-surface-api-sanity`
      - `LibraDex.csproj` Release/x64 rebuild
      - `git diff --check`
    - Terminal consolidation validation completed:
      - `dotnet run --project LibraDex.Harness -c Release -- public-surface-api-sanity`
      - `LibraDex.csproj` Release/x64 rebuild
      - `git diff --check`
    - Materialization cleanup validation completed:
      - `dotnet run --project LibraDex.Harness -c Release -- public-surface-api-sanity`
      - `LibraDex.csproj` Release/x64 rebuild
      - `git diff --check`
    - Fluent density validation completed:
      - `dotnet run --project LibraDex.Harness -c Release -- public-surface-api-sanity`
      - `LibraDex.csproj` Release/x64 rebuild
      - `git diff --check`
    - Composite density validation completed:
      - `dotnet run --project LibraDex.Harness -c Release -- public-surface-api-sanity`
      - `LibraDex.csproj` Release/x64 rebuild
      - `git diff --check`
    - Final condition-builder topic validation completed:
      - `dotnet build LibraDex.csproj -c Release`
      - `dotnet run --project LibraDex.Harness -c Release -- public-surface-api-sanity`
      - `LibraDex.csproj` Release/x64 rebuild
      - `git diff --check`

## 4. Public Surface Separation

- `[x]` Review `PublicApiSurface.cs`.
  - Current concern: public API enums/interfaces, internal predicates, execution planning, mutation helpers, stats, maintenance, tools, and compatibility models are mixed together.
  - Candidate actions:
    - Move internal predicate implementations out of the public-surface file.
      - Completed into `PublicApi\LibraDexCriteriaPredicates.cs`.
    - Move `LibraDexIdentityExecutionPlanner` into an internal execution file.
      - Completed into `PublicApi\LibraDexIdentityCriteriaExecution.cs`.
      - Internal tuple/equality support moved into `PublicApi\LibraDexIdentitySupport.cs`.
    - Keep public enums/interfaces grouped only where discoverability benefits outweigh file size.
      - Preserved in `PublicApi\PublicApiSurface.cs`.
    - Collapse trivial one-return methods in the affected public API files.
      - Completed across:
        - `PublicApi\PublicApiSurface.cs`
        - `PublicApi\LibraDexCriteriaPredicates.cs`
        - `PublicApi\LibraDexIdentityCriteriaExecution.cs`
        - `PublicApi\LibraDexIdentitySupport.cs`
      - Approximate line delta: `-47` lines versus the original tracked `PublicApiSurface.cs`.
      - Net density result: `reduced`.
      - Rationale: separates public contracts from internal predicates/execution machinery while keeping the split net-reducing through trivial method collapses and stale import removal.
  - Risk: broad public API edits can leave stale terminology in docs, harness checks, or restart notes.
  - Validation expectation:
    - Run public-surface sanity checks and `git diff --check`.
    - Public surface separation validation completed:
      - `dotnet build LibraDex.csproj -c Release`
      - `dotnet run --project LibraDex.Harness -c Release -- public-surface-api-sanity`
      - `LibraDex.csproj` Release/x64 rebuild
      - `git diff --check`

## 5. Catalog Factory Consolidation

- `[x]` Review `CatalogIndexFactories.cs`.
  - Current concern: create/open/create-or-open and `ResolveOptions` patterns repeat across typed factory classes.
  - Candidate actions:
    - Consolidate repeated `ResolveOptions` helpers.
      - Completed with shared `CatalogIndexFactoryOptions.Resolve`.
    - Extract shared create/open/create-or-open flow where it does not obscure type-specific shape decisions.
      - Reviewed and intentionally skipped broader lifecycle extraction.
      - Rationale: remaining lifecycle branches carry shape-specific behavior for descriptor validation, BigInt caps, string projection metadata, binary reversed projections, and typed generic metadata. A broad helper would hide those catalog contracts and add call depth.
    - Keep generic-first public call sites low-friction.
      - Preserved.
    - Collapse trivial one-return methods in the factory file.
      - Completed in `Catalog\CatalogIndexFactories.cs`.
      - Approximate line delta: `-99` lines versus the original tracked file.
      - Net density result: `reduced`.
      - Rationale: removes repeated option helper bodies and brace boilerplate without changing create/open metadata behavior.
  - Risk: factory cleanup can accidentally change persisted metadata or open-time shape compatibility.
  - Validation expectation:
    - Run catalog and generic public API sanity checks.
    - Catalog factory consolidation validation completed:
      - `dotnet build LibraDex.csproj -c Release`
      - `dotnet run --project LibraDex.Harness -c Release -- catalog-api-sanity`
      - `dotnet run --project LibraDex.Harness -c Release -- generic-index-api-sanity`
      - `dotnet run --project LibraDex.Harness -c Release -- public-surface-api-sanity`
      - `LibraDex.csproj` Release/x64 rebuild
      - `git diff --check`
    - Note: the first parallel `generic-index-api-sanity` attempt hit a transient compiler file lock on `obj\Release\net8.0\LibraDex.dll`; the serial rerun passed.

## 6. Indexes Folder Contract Consolidation

- `[x]` Review `/Indexes` file quantity and consolidate tiny contract files.
  - Current concern: `/Indexes` contained many tiny shape-specific files for insert results, routed insert results, route targets, range-read results, profiles, handles, and telemetry contracts.
  - Completed actions:
    - Merged 92 tiny source files into 8 family-level contract/result files:
      - `Indexes\FixedScalar.Contracts.cs`
      - `Indexes\FixedN.Contracts.cs`
      - `Indexes\Scalar8Scalar8.Contracts.cs`
      - `Indexes\ScalarVarIdentity.Contracts.cs`
      - `Indexes\VarKeyScalar.Contracts.cs`
      - `Indexes\VarKeyVarIdentity.Contracts.cs`
      - `Indexes\UnsignedScalar8Scalar8.Contracts.cs`
      - `Indexes\LibraDexGenericResults.cs`
    - Preserved all existing type names and behavior.
    - Left range readers, index wrappers, batch implementations, and session partials separate because those are implementation bodies or hot-path shape-specific code.
  - Files changed:
    - Removed 92 tiny contract/result files.
    - Added the 8 family-level files listed above.
  - Approximate file delta: `-84` files.
  - Approximate line delta: `-98` lines.
  - Net density result: `reduced`.
  - Rationale: reduces folder/file clutter without unifying shape-specific types or adding call depth in routed insert/range-reader paths.
  - Validation completed:
    - `dotnet build LibraDex.csproj -c Release`
    - `dotnet run --project LibraDex.Harness -c Release -- generic-index-api-sanity`
    - `dotnet run --project LibraDex.Harness -c Release -- catalog-api-sanity`
    - `dotnet run --project LibraDex.Harness -c Release -- public-surface-api-sanity`
    - `git diff --check`

## Preferred Cleanup Order

1. Finish scratch/build artifact cleanup.
2. Split or prune harness command families.
3. Separate condition-builder materialization from public grammar.
4. Separate public API contracts from internal execution/planning.
5. Consolidate catalog factory option and lifecycle patterns.
6. Consolidate tiny `/Indexes` contract files without changing shape-specific type names.
7. Approach `LibraDexFileSession.cs` last, after smaller consolidation patterns are proven.
