# Walked-transform allocation accounting correction

September 17, 2026, 23:24 Phoenix.

The five yellow walked-transform tests from the production repair are resolved. This follow-up changes only harness code and documentation, not production storage behavior.

## Root cause and alternatives

The older assertions counted two replacement shelves plus two router pages, but omitted persisted file-allocation claims. `FileExtentAllocator.MaterializeAndReserveNextSlot` updates the homogeneous allocation-segment header. `DataKernel.Commit` publishes allocation claims before payload writes; the commit telemetry includes both phases.

Removing those metadata writes or combining them across publication phases merely to meet the old assertions would weaken the allocation-publication safety contract. Relaxing the tests to a broad maximum would mask regressions. The selected correction is an exact fixture-specific physical-write budget, leaving production behavior unchanged.

| Fixtures | Payload bytes | Allocation headers | Total bytes | Writes |
| --- | ---: | ---: | ---: | ---: |
| SS16-8, SS8-16, SS16-16 | 73,728 | 2 × 4,096 | 81,920 | 4 |
| FS32-8, FS32-16 | 90,112 | 1 × 4,096 | 94,208 | 3 |

The 32-KiB shelves use the power-of-two extent allocator. Their two new shelf slots share one existing segment header; the appended 4-KiB router claims another. The 40-KiB shelves are not power-of-two extents and bypass that allocator, leaving only the router's header claim. Setup already established the needed segments, so no new allocation-directory or segment-creation budget is required in these fixtures.

The helper checks exact total bytes, exact logical and backing write counts, four logical extents, zero SetLength calls, zero flushes under these non-flushing fixture options, and zero gap-coalescing bytes. Existing routing, shelf-content, read-count, and available reopen assertions remain in place. The helper is deliberately scoped to these five depth-three fixtures, not a universal transform budget. Legacy payload-only helpers used by other harness commands were not globally rewritten.

## Results

- Release x64 build: zero warnings/errors.
- All five formerly failing commands: passed with the repaired engine.
- The same corrected harness paired with the clean baseline engine: all five passed.
- All 43 ownership regression cases: passed again.
- `git diff --check`: passed.

Baseline engine source commit: `020e077b54b150c94483e4c1ba0b8206fd9b0ea5`. Baseline validation used an isolated copy of the freshly built harness with only its engine DLL replaced by the previously built clean-baseline DLL. No baseline production sources were patched for this check.

Logs are in `E:\VSProjects\Diagnostics\LibraDexAllocatorAssertions-20260917`; candidate and `baseline-` logs retain the result of each command, `owners.log` retains the 43-case rerun, and `build.log` records compilation. Successful disposable database files and the copied baseline runner are removed after verification. The original failed Meridian fixture is untouched.

This closes the five specifically reported assertion failures. It does not claim every legacy harness command has allocator-aware expectations, nor does it add new million-item, crash-injection, or performance acceptance.
