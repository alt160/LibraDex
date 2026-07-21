# Condition Execution Stability Notes

Purpose: define the public stability posture for condition-builder operators whose execution may be fast-path, projection-backed, composed, inverse, external, or scan/residual.

These notes are public-readiness policy, not a new execution engine. They summarize the existing behavior proven by `public-surface-api-sanity` and the coverage matrix in `docs/condition-builder/condition-execution-coverage.md`.

## Stability Rule

If a condition can be expressed through the public condition builder, LibraDex should keep it usable for read/filter and for the selective delete/mutate contracts that explicitly accept that selector shape.

The stable public contract is the condition semantics, not a promise that every condition is a direct physical index seek. Some operators are intentionally projection-backed or scan/residual. That distinction must remain visible through docs, diagnostics, and target-owned mutation methods.

## Execution Classes

- Native indexed execution:
  - Exact, range, boundary, ordered exclusion, membership, and same-index multirange operators over scalar-compatible key families.
  - These should remain the preferred fast path when the selected index directly matches the condition.
- Projection-backed execution:
  - Operators that need a maintained derived index, such as folded text, culture sort keys, reversed suffix projections, structured date projections, exact reversed binary projections, and similar maintained shapes.
  - Projection bridges are explicit; when no projection is connected, the condition must either use a visible residual path or fail with a clear unsupported/projection-required error.
- Scan/residual fallback:
  - Operators that cannot be represented as one ordered key extent over the selected index but can be evaluated by scanning candidate tuples and applying a predicate.
  - Current examples include bitmask predicates, regex/string wildcard branches, GUID/binary pattern branches, typed binary slices, and structured date/time component predicates.
  - Residual execution is public and intentional when documented, but it is not a performance claim.
- Composed identity-set execution:
  - Cross-index `AND`, `OR`, `NOT`, difference, complement, grouping, and external/runtime-source composition over one identity universe.
  - Composition must preserve structural identity equality, including content equality for `byte[]` identities.
- Inverse execution:
  - `WhereInverse` uses an explicit index-set inverse map.
  - It is not a hidden scan of all forward indexes.
- External/runtime-source execution:
  - External identity filters and sources are caller-owned selectors.
  - A filter-only external condition needs an indexed sibling or explicit source; it must not silently become a full unbounded target scan.
- Terminal result shape:
  - Count, exists, grouping, paging, iteration, and cursor terminals reshape selected identities or tuples.
  - They do not change predicate semantics.

## Mutation Rule

Selective delete/mutate must consume the same selector semantics as retrieval.

- `EndCondition` is terminal: completed conditions expose selection and optional return shape, not mutation verbs.
- The named target index owns selective mutation:
  - `catalog[group][targetIndex].Delete(condition)`
  - `catalog[group][targetIndex].SetKey(condition, newKey)`
  - `catalog[group][targetIndex].SetKeyUsing(condition, oldKey => newKey)`
- `catalog[group][targetIndex].DeleteAll()` removes all tuples while retaining the index definition.
- A typed condition's `Return(...)` or `ReturnKeys(...)` projection is ignored by mutation; its filters, grouping, and aggregate-winner selection are still honored.
- Projection-backed selectors do not imply the projection index is the mutation target.
- Scan/residual selectors must capture exact target tuples from the candidate stream before mutation.
- External identities are selectors, not proof that a target tuple exists.

## Diagnostics Rule

Public diagnostics should describe the broad route without exposing internal storage nouns.

- `LibraDexExecutionKind.FastPath` means the selected route is directly aligned with the index or generated primitive.
- `LibraDexExecutionKind.Projection` means a maintained projection or sub-index was used.
- `LibraDexExecutionKind.Scan` means rows or candidate tuples were inspected by a residual predicate.
- `LibraDexOperationDiagnostics` is operation-level write diagnostics, not a full query explain plan.

Future query diagnostics may add route fields, but they should remain fixed-field public structs and avoid callbacks, delegates, object bags, or per-row allocation.

## Public Documentation Guidance

- Docs may say a condition is "supported" only when its read/filter semantics are covered by the current execution matrix or an explicit policy note.
- Docs must not imply indexed performance for scan/residual operators.
- Docs should prefer "projection-backed" over "optimized" when a maintained projection is required.
- Docs should mention when a projection is an application/index design choice.
- Docs should keep "full database" language out of condition examples; LibraDex is an identity-index engine.

## Current Proof Anchor

- Coverage matrix: `docs/condition-builder/condition-execution-coverage.md`.
- Sanity command:
  `dotnet run --project LibraDex.Harness\LibraDex.Harness.csproj -c Release -- public-surface-api-sanity`
- Reflected public API check should confirm public diagnostics use `LibraDexOperationDiagnostics` / `LibraDexDiagnosticsLevel`, not `DataKernel*` telemetry names.
