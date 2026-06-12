# File Session Shape Cleanup Tracker

Purpose: track the Section 2 cleanup pass for `FileSession/LibraDexFileSession.cs` without turning it into a broad storage-path refactor.

## Status Legend

- `[ ]` Not started
- `[~]` In progress
- `[x]` Done
- `[!]` Rejected or deferred after review

## Guardrails

- Preserve hot-path clarity and direct byte-path behavior.
- Consolidate only when the result reduces meaningful duplication or protects a repeated correctness rule.
- Avoid generic helper stacks that add call depth to tight range, routing, split, or mutation loops.
- Prefer report-first inventory before any runtime code edit.
- Validate any implementation slice with focused shape sanity checks before broader rebuilds.
- Treat this as HPC-sensitive storage-path work: avoid added allocations, delegates, generic abstraction for its own sake, try/catch wrappers, extra branches in tight loops, and hidden method-activation cost on range traversal, split, mutation, or shelf scan paths.
- Permit a helper only where it stays on cold/setup code or replaces repeated non-loop policy without hiding shape-specific layout behavior.

## Cleanup Plan

1. `[x]` Inventory duplicated method families in `LibraDexFileSession.cs`.
   - Focus on `Create*ShelfAndLinkRootRoute(s)`.
   - Focus on `Read*ShelfForRangeScan`.
   - Focus on `Read*IdentityRangeFromRouter`.
   - Focus on `Read*ShelfBytesForBatch`.
   - Focus on `TryCreate*SplitShelves`.

2. `[x]` Consolidate only the lowest-risk density-positive target first.
   - Preferred first candidate: `Create*ShelfAndLinkRootRoute(s)`.
   - Rationale: route-link setup is less performance-sensitive than tight read loops and may repeat reserve/write/link/commit policy.

3. `[!]` Review batch shelf byte read and dirty-image normalization helpers.
   - Candidate family: `Read*ShelfBytesForBatch`.
   - Rationale: this is repeated policy behavior and may be centralized without changing physical layout or public behavior.
   - Finding: defer under the current HPC guardrails. These helpers are batch mutation paths, not cold setup paths, and the repeated code is tied to shape-specific shelf views, clean-cache dictionaries, dirty-image dictionaries, and deleted-slot normalization. A meaningful consolidation would require delegates, generic validation, or extra helper activation in a path where allocation and call depth are visible.

4. `[x]` Defer or reject hot-loop abstraction unless the inventory shows a clean win.
   - Sensitive families: `Read*IdentityRangeFromRouter` and `TryCreate*SplitShelves`.
   - Rationale: shape-specific code may be intentional for branch predictability, allocation control, and direct memory layout.
   - Finding: keep these direct. The inventory did not show a clean win that beats the cost of abstracting key-shape prefix extraction, output span shape, visited-shelf behavior, split partitioning, and typed shelf copy/insert operations.

5. `[x]` Close Section 2 with a concrete result.
   - Close after one or two density-positive helper consolidations with focused validation, or
   - close with a written finding that the remaining duplication is intentional hot-path specialization.
   - Result: close after one density-positive cold/setup consolidation. The remaining reviewed duplication is intentionally preserved for hot-path shape specialization.

## Inventory Findings

Current file shape:

- `FileSession/LibraDexFileSession.cs` has 21,282 lines.
- The simple method-signature scan found 335 methods; tuple-return methods require direct text search because they do not match the simple parser.

Observed duplication clusters:

| Family | Count | Lines | Initial read |
| --- | ---: | --- | --- |
| `Create*ShelfAndLinkRootRoute(s)` | 16 | 7634, 7686, 7724, 7793, 7831, 7940, 8035, 8132, 8226, 8594, 8662, 8730, 8797, 10423, 19678, 19840 | Best first target. The shape-specific shelf population should stay local, but the repeated root-router validation, `ReserveAt`, `WriteRoute`, and commit path can likely be shared without touching tight read loops. |
| `Read*ShelfForRangeScan` | 5 | 5944, 6197, 6221, 6245, 19193 | Small repeated validation/read wrapper. It is called by range traversal hot paths, so consolidation should be considered only if the helper remains shallow and direct. |
| `Read*IdentityRangeFromRouter` | 8 | 1542, 2298, 2343, 6089, 6367, 6567, 6777, 19349 | Defer. The methods differ by key shape, identity width, visited-shelf handling, prefix extraction, output spans, and copy calls. This looks like intentional shape specialization. |
| `Read*ShelfBytesForBatch` plus clean-cache stores | 12 | 5113, 5202, 5310, 5355, 5463, 5508, 5616, 5661, 5769, 5814, 19037, 19083 | Good second target. The repeated dirty-image, clean-cache, kernel-read, validation, and error-message shape could be centralized after route-link cleanup. |
| `TryCreate*SplitShelves` and var-key split builders | 10 | 11256, 11371, 11494, 11627, 11749, 14645, 14778, 16877, 17019, 20373 | Defer. Split code is correctness-heavy and shape-specific; only extract after a separate line-by-line review proves a repeated policy rule. |

Additional observation:

- The route-link methods repeat the same root-router guard:
  - read `RouterLayout.Size` bytes from `rootRouterOffset`
  - construct `RouterReader`
  - require `IsValid && HasDirectIndex`
  - reserve a router rewrite at the same root offset
  - copy existing router bytes into the reservation
  - write one or more direct routes
  - call `CommitAndInvalidateRouterReadCache()`
- The shelf construction inside those methods is not the cleanup target; it encodes useful shape-specific validation and should remain visible.

## Candidate Actions

1. `[x]` First implementation candidate: add one or two private helpers near the route-link methods:
   - one helper to read and validate a direct root router into a local byte array, or
   - one helper to link a created shelf offset to one or more root prefixes and commit.
   - Do not use delegates for shelf construction or validation.
   - Do not use generics to merge unrelated shelf/profile shapes.
   - Preserve the existing ordering where root-router validation happens before shelf reservation/population.
   - Implemented as direct private helpers in `LibraDexFileSession`: `ReadDirectRootRouterBytesForLink`, `LinkDirectRootRouterRouteToShelf`, and `LinkDirectRootRouterRoutesToShelf`.
   - Shelf construction and shape-specific tuple validation remain local in each setup method.
   - Existing router validation still runs before shelf reservation/population.
   - The helper adds no delegates, no generic dispatch, no try/catch, and no additional heap allocation beyond the router byte array the original methods already allocated.

2. Keep the public/internal method signatures unchanged.

3. Preserve the shape-specific shelf population loops in place.

4. `[!]` After route-link cleanup, reassess `Read*ShelfBytesForBatch` and `Store*CleanShelfBytesForBatch` as the second candidate.
   - Reassessed and deferred. Keeping direct methods avoids delegates/generic validation and avoids adding another helper activation to batch mutation code.

5. `[x]` Mark `Read*IdentityRangeFromRouter` and `TryCreate*SplitShelves` as intentional specialization unless a later focused review finds a clear repeated correctness rule.

## Closeout

Section 2 is closed for this cleanup pass.

- Implemented one density-positive cold/setup cleanup in route-link methods.
- Preserved direct shape-specific code in batch mutation, range traversal, and split paths.
- No public/internal method signatures changed.
- No runtime code changes used delegates, generic helper dispatch, try/catch wrappers, or hidden shape construction.
- Remaining cleanup candidates should require a new focused prompt and fresh performance/correctness justification before editing.

## Validation Notes

For route-link helper cleanup:

- Build passed: `LibraDex.Harness/LibraDex.Harness.csproj` Release x64 with log `artifacts/build-harness-release-x64-file-session-route-link-cleanup-20260612.log`.
- Focused route sanity passed:
  - `route-sanity`
  - `ss8-8-route-sanity`
  - `ss16-8-route-sanity`
  - `fs32-8-route-sanity`
  - `ss8-16-route-sanity`
  - `ss16-16-route-sanity`
  - `vs8-routed-sanity`
  - `vs16-routed-sanity`
- `git diff --check` passed after validation and tracker update.
