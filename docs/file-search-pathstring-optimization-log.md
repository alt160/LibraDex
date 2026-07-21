# FileSearch PathString Optimization Log

## Goal

Optimize LibraDex file-backed and memory-backed `PathString` identity workloads for performance and memory use while preserving tuple correctness and workbench stress certification.

## Baseline Command

```powershell
$catalog = 'T:\LibraDex\pathstring-write-alloc-diagnostic.lbdx'
Remove-Item $catalog -ErrorAction SilentlyContinue
& 'LibraDex.FileSearch.Runner\bin\x64\Release\net8.0-windows\LibraDex.FileSearch.Runner.exe' --root C:\msys64 --catalog $catalog --file true --identity PathString --layout IndexMajor --commit-every 0 --string-folded true --string-sortkey true --string-reversed true --progress false
& 'LibraDex.FileSearch.Runner\bin\x64\Release\net8.0-windows\LibraDex.FileSearch.Runner.exe' --root C:\msys64 --catalog $catalog --file true --identity PathString --stress-existing true --progress false
```

## Current Accepted Changes

- `DataKernel.ReserveAt` reuses the newest fully owning pending fixed-offset segment instead of renting another buffer for repeated shelf/router rewrites in the same durability batch.
- `SV8` mixed overflow split/rewrite paths use identity references for existing shelf records instead of copying each variable identity into temporary `byte[]` values.
- `SV8` overflow rebuild helpers use pooled temporary shelf images on the reference-based path.
- `ReadScalar8VarIdentityShelfBytes` uses a stack header for the 48-byte shelf header read.
- `SV8` append-tail paths build one-record shelves directly into reserved DataKernel spans instead of allocating an empty shelf, inserting into it, then copying a rewritten full shelf image.
- `SV8` duplicate-run and mixed-overflow append-tail paths update the chain-head tail pointer with an 8-byte fixed-offset rewrite instead of reading and copying the full head shelf.
- `SV8` sorted duplicate-key append path tries a header-only head read first; when the head has a valid tail pointer and the incoming tuple sorts after the terminal tuple, insertion jumps directly to the tail shelf without materializing the full head shelf.
- FileSearch reindex telemetry now logs per-field `SV8` routed insert kind counts so duplicate overflow, split, grow, no-split, and route creation pressure are visible in one summary line.

## Rejected Paths

- `SV8` clean shelf read cache on `ReadScalar8VarIdentityShelfBytes`.
  - Reason: the method currently serves both read-only and mutable callers, so cached mutable byte images can corrupt route/shelf correctness unless ownership is redesigned.
  - Evidence: file-backed `PathString` reindex produced tuple drift in `CreatedUtc` and `AccessedUtc`.
  - Follow-up if revisited: split immutable shelf reads from mutable shelf images before adding caching.
- Mutable-shelf insertion inside the duplicate-run chain fallback.
  - Reason: it did not reduce the remaining allocated-byte counters and increased retained private memory in the clean corpus run.
  - Evidence: file-backed `PathString` reindex stayed at about 4.8 GB allocated delta, with private delta rising from about 127 MB to about 184 MB.
  - Follow-up if revisited: add path-specific terminal/duplicate attribution first so this optimization only activates on a proven allocation source.
- Narrow terminal var-identity cache invalidation plus duplicate-write removal in terminal append.
  - Reason: it did not reduce date-field allocated-byte counters and slightly worsened insert elapsed time on the clean corpus run.
  - Evidence: file-backed `PathString` reindex stayed at about 4.8 GB allocated delta; `accessedUtc` remained about `2.5 GB`, `modifiedUtc` about `1.4 GB`, and total insert rose to about `6.42s`.
  - Follow-up if revisited: add allocation phase counters inside `TryAppendScalar8VarIdentityTerminalTail` before changing cache lifetime again.

## Current Validation Snapshot

- Command: file-backed `PathString`, `IndexMajor`, commit interval `0`, folded/sortkey/reversed string subindexes enabled, root `C:\msys64`.
- Result: reindex passed with `47,871` scanned/indexed, `0` failed files, `0` failed fields, tuple counts matched every indexed field.
- Timing: collect `2.54s`, insert `4.73s`, total `7.62s`.
- Resource: allocated delta `4.8 GB`, private delta `181.4 MB`, working set delta `188.8 MB`.
- Stress: `PASS`, `enum=35`, `query=114`, `strategy=14`, `warnings=0`, `failures=0`, elapsed `4.76s`.
- Notable improvement: `size` field allocation dropped from about `606 MB` to `7.1 MB` after the header-only tail fast path.
- Remaining concern: `accessedUtc` and `modifiedUtc` still dominate allocated bytes (`2.5 GB` and `1.4 GB`). Next useful pass should add path-specific attribution inside terminal duplicate routes and same-key duplicate-chain fallback before more structural edits.

## Follow-up Diagnostic Snapshot

- Added per-kind and per-path `SV8` allocation attribution to the FileSearch summary.
- Result: large field allocations are charged almost entirely to `WalkedDuplicateRunOverflow`.
- `size` remains efficient because most duplicate-overflow inserts use `headerTail`.
- Date fields are mostly terminal duplicate append paths:
  - `accessedUtc`: about `41,285` terminal in-place appends and `683` terminal new-shelf appends.
  - `modifiedUtc`: about `43,707` terminal in-place appends and `717` terminal new-shelf appends.
  - `createdUtc`: about `14,955` terminal in-place appends and `202` terminal new-shelf appends.
- Inner probes around route walk and terminal append show only small direct allocations, so the remaining gap is inside duplicate-overflow dispatch/measurement rather than the terminal shelf append body itself.
- Latest stress validation on the diagnostic build: `PASS`, `enum=35`, `query=114`, `strategy=14`, `warnings=0`, `failures=0`.

## Raw Discovery Benchmark Split

- Added stress telemetry for raw cursor discovery so LibraDex byte/index potential is measured separately from .NET materialization.
- Raw discovery counts rows and raw key/identity byte lengths from `VV` and `SV8` readers without formatting strings, hydrating records, or returning identity `byte[]` values.
- Single-condition stress queries now run an additional `Mode=raw` pass for `PathString` catalogs. Multi-condition raw intersection is intentionally left for a later byte-native identity-set structure.
- Latest validation command: file-backed `PathString`, `IndexMajor`, commit interval `0`, folded/sortkey/reversed string subindexes enabled, root `C:\msys64`, catalog `T:\LibraDex\pathstring-raw-discovery-diagnostic.lbdx`.
- Result: reindex passed with `47,871` scanned/indexed, `0` failed files, `0` failed fields; stress passed with `enum=35`, `query=150`, `strategy=14`, `warnings=0`, `failures=0`.
- Phase totals from the new stress pass:
  - `RawDiscovery=703ms over 71 cases, avg 10ms`.
  - `IndexTupleRead=497ms over 35 cases, avg 14ms`.
  - `IdentityMaterialize=1.37s over 39 cases, avg 35ms`.
  - `KeyIdentityMaterialize=893ms over 71 cases, avg 13ms`.
  - `RecordMaterialize=2.00s over 39 cases, avg 51ms`.
- Interpretation: the current read-side evidence supports the new benchmark split. Core byte-native cursor traversal is substantially cheaper than practical identity/record materialization, while insert-side date duplicate-key allocation remains the main write-path concern.

## Iteration Notes

- Prefer one hypothesis at a time.
- Keep failed experiments out of final code unless they only add useful diagnostics.
- Build and run reindex plus stress after each promising change.
- If tuple counts drift, revert that path immediately.

## Restart Here - 2026-06-18

### Current Git State

- The working tree is intentionally dirty after the FileSearch workbench and LibraDex core optimization run. Do not stage or commit wholesale without first splitting the work into reviewable slices.
- `git status --short` shows a large mixed set of tracked edits plus untracked workbench/core artifacts.
- `git diff --stat` over tracked files currently reports `84` changed files, about `14,766` insertions, and about `1,306` deletions. This does not include untracked files.
- Major modified areas include `Catalog`, `ConditionBuilder`, `DataKernel`, `FileSession`, `Indexes`, `PublicApi`, `Routing`, `Views`, `LibraDex.csproj`, `LIBRADEX_DESIGN_CHECKLIST.md`, and condition-builder docs.
- Major untracked areas include `LibraDex.FileSearch`, `LibraDex.FileSearch.Runner`, `LibraDex.Harness` commands, new `DataKernel` diagnostics, `Terminal*` layouts, `LibraDexUInt64VarIdentityIndex`, and the public-readiness/FileSearch docs.
- Latest known successful build before this checkpoint: `artifacts\build-filesearch-raw-benchmark-final-release-x64-20260618.log`, targeting `LibraDex.FileSearch.Runner` Release x64.
- Latest known FileSearch app relaunch before this checkpoint: `E:\VSProjects\LibraDex\LibraDex.FileSearch\bin\x64\Release\net8.0-windows\LibraDex.FileSearch.exe`.

### Current Technical Conclusion

- LibraDex is best framed as a byte-native identity-index engine, not a database. The identity is caller-owned and intentionally returned as the main output of index use.
- The read-side stress split now shows the important difference between core index potential and practical .NET materialization cost:
  - Raw discovery measures cursor/index traversal over key and identity bytes.
  - Identity materialization measures creating caller-visible identity values.
  - Record/grid materialization measures the FileSearch workbench's practical display path.
- Scalar identities, including `ulong`, fixed bytes, and GUID-like identities, are the most natural high-throughput use case: query LibraDex, return identities cheaply, then hydrate richer records elsewhere.
- This resembles SQL/B-tree secondary indexes returning row locators, but LibraDex makes the identity explicit, developer-owned, and directly reusable instead of hiding it behind a table engine.
- File-backed `PathString` identity reads are now operationally correct under the stress suite. The remaining concern is write-side allocation attribution for date-heavy duplicate-key insert paths.
- The next optimization should instrument inside `TryAppendScalar8VarIdentityTerminalTail` and its immediate callers before changing structure again. Add phase counters around root read, tail shelf read/validation, in-place append, new-shelf append, dirty marking, DataKernel reservation/write, and route or terminal conversion.

### Last-Chat Primer

1. The user asked whether LibraDex seems good as a design for hot-loop table/index iteration and custom analysis.
   - Current answer: yes, especially when measured as byte-native cursor traversal rather than forced string/object materialization.
2. The user asked whether scalar identity values are the likely ideal case.
   - Current answer: yes. Scalar identities minimize result payload and let the application decide where and when to hydrate richer data.
3. The user compared this to SQL/B-tree index behavior.
   - Current answer: the shape is similar in that an index produces row/node identities, but LibraDex exposes that identity as the public product instead of making it an internal table locator.
4. The user asked for a git update, docs update, and a restart primer.
   - Current action: document the dirty-tree state and current technical posture. No staging or commit has been performed.

### Recommended Commit Boundaries

1. FileSearch workbench and runner.
   - Includes UI, stress-test command, logging, raw discovery reporting, catalog options, and workbench-specific diagnostics.
2. DataKernel memory and pending-write optimization.
   - Includes reserve/rewrite reuse, memory diagnostics, telemetry options, and backing-kind behavior.
3. `SV8`/`VV`/varlen identity shelf correctness and performance.
   - Includes duplicate-key terminal shelf behavior, raw readers, PathString identity enumeration/query support, and allocation attribution.
4. Public API and condition-builder docs/harness updates.
   - Includes public readiness docs, API snapshot, condition execution docs, and public API surface changes.

### Restart Prompt

Use this prompt in a new conversation:

> Start in `E:\VSProjects\LibraDex`. Read `docs/file-search-pathstring-optimization-log.md` section `Restart Here - 2026-06-18`, then check `git status --short` and `git diff --stat`. The current lane is not public promotion; it is making LibraDex accurate, trustable, and efficient as a byte-native identity-index engine. Do not stage or commit wholesale. First decide the next slice: either document/split commit boundaries, or continue write-side allocation attribution for file-backed `PathString` identities by instrumenting `TryAppendScalar8VarIdentityTerminalTail` and nearby duplicate-key SV8 paths. Preserve the benchmark split between raw index discovery, identity materialization, and record/grid materialization.
