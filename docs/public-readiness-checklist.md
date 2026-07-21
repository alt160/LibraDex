# LibraDex Public Readiness Checklist

Purpose: track the remaining work to move LibraDex from controlled internal prototype use toward a public-ready package.

## Status Legend

- `[ ]` Not started
- `[~]` In progress or needs review
- `[x]` Done
- `[!]` Blocking before public release

## Current Readiness

- `[x]` Core file-backed index engine builds under Release x64.
- `[x]` Public condition-builder syntax is broad enough for current Abraxas-style read/filter intent.
- `[x]` Current read/filter execution matrix is green for the proven public condition families.
- `[x]` Current selective delete/mutate contract is proven for exact-tuple terminals and explicit-target composed/inverse/external/pattern selector terminals.
- `[x]` Repository cleanup, source-folder organization, and large-file split checkpoint committed in `6b49361`.
- `[~]` Use-ready for controlled internal integration experiments where API churn is acceptable.
- `[~]` FileSearch workbench is actively proving realistic identity-index behavior, including byte-native raw discovery versus .NET materialization cost. See `docs/file-search-pathstring-optimization-log.md`.
- `[!]` Not yet ready for public package release or production dependency claims.

## 0. Release Gate Summary

- `[x]` Resolve or explain the latest performance validation drift before making public performance claims.
- `[!]` Freeze the first public API surface and remove or clearly mark compatibility aliases that should not become long-term contract. See `docs/public-api-freeze-inventory.md`.
- `[!]` Add a public quickstart and minimal package-consumer examples.
- `[!]` Define versioning, compatibility, and file-format support policy.
- `[!]` Split high-risk monolithic proof areas only where it improves diagnostics without adding production call depth.
- `[!]` Add package/release validation that starts from a clean checkout and consumes LibraDex the way an external developer would.

## 1. Performance And Validation Gates

- `[x]` Rerun validation using a stronger checkpoint/baseline tier instead of only `validate --tier fast`.
  - 2026-06-12 checkpoint command:
    `dotnet run --project LibraDex.Harness\LibraDex.Harness.csproj -c Release -- validate --tier checkpoint`
  - Result: exit `0`, `pass=14`, `warn=0`, `fail=0`.
  - Report: `artifacts\validation-runs\validation-checkpoint-current.md`.
  - Decision: the latest fast-tier drift was not reproduced under checkpoint validation. No code change is needed for that drift before continuing public-readiness work. Keep public performance claims separate until release docs choose which metrics are public.
- `[x]` Create a public-readiness validation command set.
  - Current repository note: this checkout has project files, not a `.sln` or `.slnx`; use project rebuild commands until a solution file is added.
  - Release x64 library rebuild:
    ```powershell
    $log = 'artifacts\build-lib-release-x64-public-readiness-20260612.log'
    $msbuild = 'C:\Program Files\Microsoft Visual Studio\18\Insiders\MSBuild\Current\Bin\MSBuild.exe'

    & $msbuild 'LibraDex.csproj' `
      /t:Rebuild `
      /p:Configuration=Release `
      /p:Platform=x64 `
      /m:1 `
      /v:minimal `
      /fl `
      /flp:"logfile=$log;verbosity=normal"
    ```
  - Release x64 harness rebuild:
    ```powershell
    $log = 'artifacts\build-harness-release-x64-public-readiness-20260612.log'
    $msbuild = 'C:\Program Files\Microsoft Visual Studio\18\Insiders\MSBuild\Current\Bin\MSBuild.exe'

    & $msbuild 'LibraDex.Harness\LibraDex.Harness.csproj' `
      /t:Rebuild `
      /p:Configuration=Release `
      /p:Platform=x64 `
      /m:1 `
      /v:minimal `
      /fl `
      /flp:"logfile=$log;verbosity=normal"
    ```
  - Public API snapshot compare:
    `dotnet run --project LibraDex.Harness\LibraDex.Harness.csproj -c Release -- public-api-snapshot`
  - Public condition/API execution proof:
    `dotnet run --project LibraDex.Harness\LibraDex.Harness.csproj -c Release -- public-surface-api-sanity`
  - Checkpoint validation tier, including representative reopen and condition coverage anchors:
    `dotnet run --project LibraDex.Harness\LibraDex.Harness.csproj -c Release -- validate --tier checkpoint`
  - Review the checkpoint report:
    `artifacts\validation-runs\validation-checkpoint-current.md`
  - Bounded performance gate for public-readiness completion:
    `dotnet run --project LibraDex.Harness\LibraDex.Harness.csproj -c Release -- solution-quality-sanity core`
  - Public performance claims remain blocked until section 1 chooses which numbers are public claims versus internal diagnostics.
- `[ ]` Decide which perf numbers are public claims and which remain internal diagnostics.
- `[ ]` Confirm ignored generated outputs are reproducible and not required for release packaging.

## 2. Public API Freeze

- `[x]` Inventory the exported public API after the cleanup commit.
  - Initial reflection inventory is captured in `docs/public-api-freeze-inventory.md`.
  - 2026-06-12 Release x64 reflection found `284` exported public types from `bin\x64\Release\net8.0\LibraDex.dll`.
  - 2026-06-12 visibility cleanup reduced the exported public type count to `213`.
  - 2026-06-12 diagnostics cleanup changed the exported public type count to `214` by replacing public `DataKernelCommitTelemetry` exposure with `LibraDexOperationDiagnostics` plus `LibraDexDiagnosticsLevel`.
  - Detect accidental public types introduced by split files.
  - Confirm retired direct retrieval grammar remains retired.
  - Confirm canonical `.Not` and explicit-target mutation terminals are documented as the durable shape.
- `[x]` Decide alias policy.
  - Keep aliases only when they materially reduce developer friction.
  - Mark experimental or compatibility aliases before external use.
- `[x]` Confirm remaining logical typed facades are public by design.
  - Keep string and BigInteger facades public when they represent logical key/identity families rather than physical shelf shapes.
  - Keep disconnected read buffers public as advanced, caller-owned read APIs.
- `[ ]` Confirm names for public selector concepts.
  - Index names.
  - Projection descriptors.
  - Composite key parts.
  - Identity groups.
- `[x]` Add API stability notes for condition-builder operators that intentionally fall back to scan/residual execution.
  - See `docs/condition-builder/condition-execution-stability.md`.
  - Stability rule: public condition semantics remain stable even when execution is projection-backed or scan/residual; docs must not imply indexed performance for residual operators.
- `[ ]` Decide whether any public types should move internal before a package is cut.
  - First pass moved direct `Indexes.*`, session, router, directory, superblock, raw data-kernel option/read telemetry, and physical shelf/index wrappers internal.
  - Public diagnostics naming cleanup complete: generic public result records now expose `LibraDexOperationDiagnostics`, and `CatalogOptions.DiagnosticsLevel` provides the public level knob.
  - Pre-public scaffold aliases removed: `BatchManager` property and generic result `*Commit` diagnostics aliases.

## 3. File Format And Compatibility

- `[ ]` Document the `.lbdx` file-format compatibility promise.
  - Format version policy.
  - Forward/backward compatibility expectations.
  - Migration expectations.
  - Reopen support guarantees.
- `[ ]` Confirm catalog metadata versions and shape descriptors are durable enough for the first public release.
- `[ ]` Define policy for experimental storage shapes, projections, and composite descriptors.
- `[ ]` Add compatibility tests for opening files created by the first public-ready build once that build is chosen.

## 4. Documentation And Developer Friction

- `[ ]` Add a public quickstart.
  - Create/open a catalog.
  - Add identities.
  - Query with condition builder.
  - Delete/rekey with explicit target terminals.
  - Reopen the file and read again.
- `[ ]` Add a concept guide.
  - Index.
  - Identity.
  - Identity group.
  - Projection index.
  - Composite index.
  - Scan/residual fallback.
- `[ ]` Add examples for the proven high-value surfaces.
  - Scalar.
  - String with projection/no-case behavior.
  - Structured date/time.
  - GUID.
  - Binary and typed binary slices.
  - Composite keys.
  - External/runtime-source composition.
  - Selective delete/mutate.
- `[ ]` Add a performance guidance page.
  - Fast path versus visible scan/residual fallback.
  - Projection-backed string guidance.
  - Composite index guidance.
  - File-backed reopen expectations.
- `[ ]` Keep docs explicit that LibraDex is an identity-index engine, not a full database, key-value store, document store, or relational engine.

## 5. Test And Proof Shape

- `[ ]` Split public-surface proof only where diagnostics are currently too broad.
  - Candidate helpers:
    - `ValidateDeterministicGuidConditionCoverage`.
    - `ValidateDeterministicBinaryConditionCoverage`.
    - `ValidateDeterministicCompositeConditionCoverage`.
    - `ValidateSelectiveMutationCoverage`.
  - Keep this harness-only; do not add production abstractions for test organization.
- `[ ]` Add clean-package consumer tests.
  - Build library.
  - Reference from a separate minimal project.
  - Use only documented public APIs.
  - Verify file-backed create, query, mutate, and reopen.
- `[x]` Add API surface snapshot checks if public exposure churn becomes hard to review manually.
  - Baseline: `docs/public-api-snapshot.txt`.
  - Compare command:
    `dotnet run --project LibraDex.Harness\LibraDex.Harness.csproj -c Release -- public-api-snapshot`
  - Intentional update command:
    `dotnet run --project LibraDex.Harness\LibraDex.Harness.csproj -c Release -- public-api-snapshot --update`

## 6. Packaging And Release Mechanics

- `[ ]` Decide package identity.
  - Package id.
  - Assembly version.
  - Repository metadata.
  - License metadata.
- `[ ]` Add packaging validation.
  - Pack from clean checkout.
  - Install/reference from a scratch consumer.
  - Run quickstart sample against the packed artifact.
- `[ ]` Decide symbol/source-link policy.
- `[ ]` Decide release notes format and changelog location.

## 7. Operational Boundaries

- `[ ]` Document concurrency and file-access expectations.
  - Single writer versus multi-writer policy.
  - Reader behavior during writes.
  - Process boundary expectations.
- `[ ]` Document durability expectations.
  - Commit semantics.
  - Flush behavior.
  - Crash/reopen expectations.
- `[ ]` Document recovery and corruption-detection posture for the first public release.
- `[ ]` Document supported runtime and platform matrix.
  - Current target framework is `net8.0`.
  - Validate Windows x64 first; expand only with explicit validation.

## 8. Priority Order

1. Inventory and freeze the public API surface.
2. Decide whether current performance numbers are public claims or internal sanity data.
3. Add quickstart plus clean external-consumer validation.
4. Define file-format compatibility and versioning policy.
5. Split high-risk harness proof areas only where review/debugging needs it.
6. Add packaging/release mechanics.
7. Re-run the full public-readiness validation from a clean checkout.

## Restart Prompt

Use this prompt in a new conversation:

> Start from `docs/public-readiness-checklist.md`, `docs/file-search-pathstring-optimization-log.md` section `Restart Here - 2026-06-18`, `docs/condition-builder/condition-execution-coverage.md`, and `LIBRADEX_DESIGN_CHECKLIST.md` section 0. LibraDex is use-ready for controlled internal experiments, but not public-package ready. Public promotion remains paused while the FileSearch workbench proves realistic byte-native identity-index behavior, materialization costs, and file-backed reopen behavior. First choose whether to continue workbench/core optimization or return to public-readiness gates: inventory/freeze the public API, add quickstart and clean consumer validation, then define file-format compatibility and packaging policy.
