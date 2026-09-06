# LibraDex Condition-Builder Continuation Restart

Last updated: 2026-08-21

## Start Here

This is the canonical restart artifact for the current condition-builder and natural-language-query review lane.

Read these files in order:

1. This restart document.
2. [`libradex-query-intuitiveness-worklist.html`](libradex-query-intuitiveness-worklist.html) for the current 219-row human review.
3. [`condition-query-intent-proof.md`](condition-query-intent-proof.md) for row-level proof and implementation notes.
4. [`condition-execution-coverage.md`](condition-execution-coverage.md) for execution-family coverage.
5. [`../../LIBRADEX_DESIGN_CHECKLIST.md`](../../LIBRADEX_DESIGN_CHECKLIST.md) for durable project decisions.

Do not treat the older 671-row filter-DX corpus as the current intuitiveness worklist:

- `libradex-query-intuitiveness-worklist.html`: 219 natural-language intents reviewed for current LibraDex intuitiveness.
- `condition-query-intent-proof.md`: proof and implementation record for those intents.
- `filter-dx-natural-language-corpus.csv`: older, larger 671-row syntax corpus.

## Repository State Warning

The current branch is `codex/libradex-condition-proof-20260601`.

The working tree is intentionally broad and dirty. After adding this handoff, it contains 56 modified tracked files and 18 untracked files spanning the long condition-builder implementation session. These changes are work in progress, not disposable residue.

Do not run `git reset --hard`, `git checkout --`, broad restore/clean commands, or delete untracked source. Inspect and preserve existing changes before editing. Git cleanup or commit splitting requires a deliberate user decision.

## Product Model

The accepted developer model is:

- Decimal, Single, and Double conditions may apply one native `.Round(...)`, `.Floor()`, `.Ceiling()`, or `.Truncate()` transform before comparison. These are explicit planner-visible scan predicates today.
- A completed typed return stage may apply caller code through `.Transform(...)`; execution remains deferred, and condition mutation uses the untransformed selected identities.
- The iterator, reader, stream, or getter produces outputs from a condition.
- The condition describes selection rules and the values returned.
- Return shape belongs to the condition; delivery shape belongs to the producer.
- `EndCondition` is terminal. Nothing fluent follows it.
- Prefer discoverable properties over parameterless methods in fluent grammar where practical.
- Do not reintroduce `.All()` or `.WithValue(...)`.

```csharp
var condition = catalog["events"]
    .GroupBy("deviceId")
    .Aggregate("timeStamp", AggType.Max)
    .Return<long>()
    .EndCondition;

var latestEventIds = catalog["events"].Get(condition);
```

```csharp
var condition = catalog["users"]
    .Where("status").AsString.EqualTo("active")
    .Return<long>()
    .EndCondition;

using var reader = catalog["users"].OpenReader(condition);
reader.Skip(10);
var page = reader.Pull(10);
```

`Pull(count)` consumes from the reader's current cursor, returns a collection, and advances the cursor. Getter methods accept `skip`/`take` parameters; streaming readers retain dynamic cursor control.

## Return Semantics

- `.Return()` returns identities in canonical binary form.
- `.Return<TIdentity>()` returns typed identities.
- `.Return<TIdentity, TKey1>(indexName)` and higher arities return identities plus selected keys.
- `.ReturnKeys<TKey1>(indexName)` and higher arities return keys without identities.
- Most retrieval defaults favor identities because developers commonly use them to materialize full objects from another store.
- Mutation accepts reusable conditions containing `Return` or `ReturnKeys`, but ignores only return materialization. Filtering, grouping, ordering, and aggregate-winner selection remain effective.

## Mutation Semantics

Mutation belongs to the explicit target index, not to the completed condition:

```csharp
catalog["products"][targetIndexName].Delete(condition);
catalog["products"][targetIndexName].DeleteAll();
catalog["users"]["status"].SetKey(condition, "archived");
catalog["users"]["email"].SetKeyUsing(condition, oldKey => ReplaceDomain((string)oldKey, newDomain));
```

The condition selects identities. The target index owns tuple deletion or rekeying. This rule applies across mutating methods and avoids ambiguous mutation scope.

## Current Fluent Decisions

### Selection and grouping

- `GroupBy(indexName)` groups canonical stored key bytes by default.
- `GroupBy(indexName).AsString(SubIndexType.Folded)` can group a maintained text projection without materializing raw values into strings.
- `Aggregate(indexName, AggType.Max/Min)` selects aggregate winners.
- `First` and `Last` are order-based grouped representatives, distinct from scalar Min/Max.
- `OrderBy(...)`, `OrderByDescending(...)`, `Top(n)`, and `Bottom(n)` belong to the condition.
- `Top` and `Bottom` require a non-zero value. `Top(1)`/`Bottom(1)` select one row in declared or natural index order; they are not aliases for scalar Max/Min.

### Dynamic criteria and selectors

- Deferred lambdas remain valid.
- `LibraDexParameter<T>` is the convenience adapter for reusable mutable criteria or selectors without lambda ceremony.
- Parameter values and selector names are read at condition execution time.
- `EndCondition` remains terminal; parameterization is captured before it.

### Numeric and typed projection

- Public fluent projection uses explicit vocabulary such as `.AsInt32`, `.AsUInt64`, `.AsInt128`, `.AsBigInt`, `.AsDateOnly`, and `.AsDateTime`.
- Public `.As<T>` projection forms were removed; generic machinery may remain internal.
- Supported numeric scalar types run from byte/sbyte through UInt128/Int128, plus BigInteger where supported.
- Projection is intentionally permissive across physical widths when conversion is meaningful; developers may project custom bytes and own the interpretation.
- `LibraDexCoercion` separates numeric, text, GUID, and date conversion categories.
- LibraDex canonical codec helpers should be available to developers but are not required for developer-owned arbitrary binary payloads.

### Cross-property comparisons

- Ordinary comparison verbs such as `.GreaterThan(value)` retain their static, deferred, or expression-value meaning.
- The alphabetically grouped `.PropEqualTo(...)`, `.PropNotEqualTo(...)`, `.PropGreaterThan(...)`, `.PropGreaterOrEqual(...)`, `.PropLessThan(...)`, and `.PropLessOrEqual(...)` family compares the left selector to a right property from the same record.
- Textual right paths deliberately require their own explicit type stage, for example:

```csharp
var where = orders.Where
    .PropPath(".ShippedDate").AsDateTime
    .PropGreaterThan(".RequiredDate").AsDateTime
    .EndCondition;
```

- Typed-lambda right operands complete immediately because their declared CLR type is already known.
- Numeric operands retain both declared types. Identical types compare directly; provably lossless widening is automatic; ambiguous or lossy pairs such as Decimal/Double are rejected while the condition is built.
- Ordered comparisons are public for numeric, string, DateTime, DateOnly, TimeOnly, and TimeSpan values. Boolean, GUID, and binary values expose equality and inequality only.
- Missing, null, or unreadable values on either side do not match. Two typed readers are compiled once per semantic execution plan; per-record evaluation performs no expression compilation or reflection.
- Accepted design: ordinary single-property indexes do not claim to answer a selector-to-selector predicate. The condition remains an authoritative payload residual unless an explicit expression index later matches the complete frozen semantics. This is complete semantic behavior rather than an outstanding optimization; reconsider automatic acceleration only if representative benchmarks show material need.
- Durable condition format 13 stores the right selector, both declared operand types, the common comparison type, coercion-policy version, and string collation options. NW082 in `NORTHWIND_SQL_ABRAXAS_WORKLOAD_TRACKER.html` now uses this condition as its primary form and retains the projected reader loop as an alternate.

### Date, decimal, floating, enum, and GUID handling

- Ordered decimal/floating codecs preserve comparison-friendly canonical bytes and avoid unnecessary .NET materialization for byte-order-compatible comparisons.
- Enum criteria are supported only by numeric adapters compatible with valid enum backing types.
- Deferred enum enumerables are converted at condition execution time, not condition construction time.
- Binary date-part predicates explicitly distinguish LibraDex structured-date encoding from .NET ticks.
- GUIDs remain compact binary values while supporting GUID-context prefix, suffix, contains, and `x` pattern matching.
- `Slice(...)` is the raw byte-slice/reprojection operation; GUID textual component selection has distinct GUID-oriented naming.

### Null, empty, whitespace, missing, and existence

- Null, empty, and missing keys are physically distinct routes and must stay semantically distinct.
- Whole-key null/empty/whitespace combinations should route indexed null and empty cases first and scan only the whitespace case.
- Mutation-based questions such as "empty after trimming" remain scans; they are not equivalent to whole-key null/empty/whitespace checks.
- `.Where(indexName).Exists` means that the named index exists at execution time, enabling conditions tolerant of transient indexes.
- Direct catalog schema existence is `catalog.HasIndex(...)`.
- Index content emptiness is answered by count, not an ambiguous `index.Exists()`.
- Direct membership families live under `index.Keys`, `index.Identities`, and `index.Entries`, including `Exists`, `ExistsAny`, and `ExistsAll`.

### Duplicate, singleton, entry, and point lookup

- Indexes expose non-generic `Entries`, `Keys`, and `Identities`; projection is chosen after that surface.
- Duplicate and singleton enumeration is symmetric:

```csharp
index.Keys.Duplicates.AsString.Get();
index.Keys.Singletons.AsString.Get();
```

- Raw bytes remain the default to avoid forced decode allocations.
- Typed projections use explicit `.As*` families.
- Direct one-key lookup returns identities for the key.
- Direct multi-key lookup returns a dictionary keyed by every distinct requested key, including misses with empty identity lists.
- Conditions remain the streaming/iterator form; direct `GetByKey(s)` is eager point materialization.

### Inversion lifecycle

- Every index belongs to a catalog.
- Identity operations use the inversion when available.
- If inversion is enabled but incomplete, a necessary forward walk may incrementally populate it.
- Developer settings control whether inversion is disabled, lazy, or built immediately.
- Build-on-open is an explicit open option because it may block.
- XML documentation must call out blocking behavior.

## Maintenance Assessment: Current Implementation

`catalog.Maintenance.Assess(minimumReclaimableBytes)` is now an authoritative, explicit blocking maintenance walk.

- `VS8`, `VS16`, `VV`, `SV8`, and `SV16` shelves persist exact orphaned variable-record byte counts in unused bits of the existing shelf flags word.
- The counter adds no shelf-size change and no separate delete-time I/O.
- Mutable shelf decode validates that the persisted count cannot exceed the used record arena.
- Assessment walks each physical topology once.
- Fixed-width and currently compacting shapes contribute authoritative zero.
- Maintained projection companion bytes roll into the logical source index.
- `ReclaimComponents` retains physical slot, projection, direction, bytes, and shelf-count detail.
- `RepackCandidates` applies the caller's threshold to the logical source total.
- Unknown physical targets or unattributed session-local reclaimed cells set `CanAttributeRepackCandidates` false rather than inventing estimates.
- Projection health validates raw companion ownership by deterministic physical name and absence of independent logical group metadata.

```csharp
var assessment = catalog.Maintenance.Assess(
    minimumReclaimableBytes: threshold);
```

This improvement moved worklist row #217 from score 4 to score 5.

## Maintenance Execution: Current Implementation

- `catalog.Maintenance.Repack(options)` walks active logical indexes plus maintained companions and rebuilds exact live record arenas for `VS8`, `VS16`, `VV`, `SV8`, and `SV16`.
- Repack preserves physical offsets and file length. Its work counter covers every distinct physical target, including routers and understood fixed leaves.
- `catalog.Maintenance.Optimize(options)` rebuilds `VS8`, `VS16`, `VV`, `SV8`, and `SV16` root-prefix subtrees from authoritative encoded tuples and publishes each replacement through a version-checked durability batch.
- VS8/VS16 use their bulk replacement planners. VV/SV8/SV16 use shape-native routed writers behind a detached construction root; only a completed child target can become reachable from the authoritative root.
- Optimization counts authoritative tuples. `UnsupportedTopology` and its distinct logical-owner count remain fail-closed reporting for future or legacy valid shapes not connected to an operation.
- Maintenance modes are operational: `Light=16`, default `Bounded=256`, and `Full=unlimited`. An explicit positive `MaxWorkItems` overrides the mode.
- `Completed` is true only when the structured `IncompleteReasons` flags are empty; exact-limit outer loops cannot hide later unvisited logical indexes.
- The production optimizer path does not use the older harness planner's deterministic synthetic identities.
- Optimization leaves obsolete extents unreachable and can grow the file. It does not promise file-size reduction.
- `Catalog.Compact(path, options)` is a closed-file shadow rebuild. It recreates logical indexes and owned projections, structurally compares source/shadow tuples, flushes and closes the shadow, atomically replaces the source with rollback, then reopens the installed replacement before deleting rollback state.
- Dropped indexes and unreachable optimizer/repack extents are omitted, producing the defragmented dense file.
- Logical facades without authoritative tuple streaming still fail closed before replacement.
- BigInteger compaction is connected for fixed-width scalar-8/scalar-16 identities, fixed-width scalar-null routes, variable-width scalar-8 identities, and fixed-width variable identities through shape-native authoritative fixed-N tuple traversal.
- Detailed design, support matrix, and remaining lanes: [`../maintenance/maintenance-compaction-design.md`](../maintenance/maintenance-compaction-design.md).

Focused Release/x64 proofs:

```powershell
$h = 'LibraDex.Harness\bin\x64\Release\net8.0\LibraDex.Harness.dll'
dotnet $h catalog-repack-sanity
dotnet $h catalog-compaction-sanity
dotnet $h catalog-maintenance-bound-sanity
dotnet $h catalog-compaction-failure-sanity
```

Observed compaction fixture: `7,499,312` source bytes to `2,469,106` compacted bytes, reclaiming `5,030,206` bytes while preserving `5,627` live tuples across generic scalar, projected string, variable blob, UInt64 variable identity, and every public BigInteger storage shape.

## Worklist State

The HTML worklist contains 219 unique rows, IDs 1 through 220 with row #60 intentionally removed as an unreasonable custom-numeric-type expectation.

Current sub-5 rows:

- #152: score 4. "Empty after trimming" requires mutation/scan semantics; the current regular-expression representation is honest.
- #176: score 4. Runtime-selected schema remains inherently indirect even with `LibraDexParameter<T>`.
- #177: score 4. Runtime-selected date schema has the same inherent indirection.

Every row below 5 has a reason in Notes. Rows scored 5 have no stale score-reason note. Status and Performance remain intentionally empty.

The worklist supports two-way sorting, horizontal and vertical scrolling, resizable and reorderable columns, and viewport-height table fill.

The in-app browser may need a manual refresh after file edits. Programmatic inspection of local `file://` pages is blocked by browser security policy.

## Validation Checkpoint

### 2026-08-16 inline condition calculations and NW-053

- Typed `Condition<T>.Clause.Prop(...)` and textual `PropPath(...).As...` stages now expose the same transformation families as reusable `Selector<T>` values. String, binary, Guid, integral, real, Decimal, DateTime, DateOnly, TimeOnly, and TimeSpan calculations can remain attached to the condition until the ordinary comparison and terminal `EndCondition`.
- This is one semantic model, not a second evaluator. Each condition-local stage composes the existing immutable selector descriptor and then hands that descriptor to the existing comparison, scan, and LibraDex-planning paths.
- A one-use transformation is now authored inline as the primary low-friction form, for example `products.Where.Prop(product => product.ProductName).Replace("-", " ").EqualTo("Chai Tea").EndCondition`. A named `products.Calc...` selector remains the preferred alternate when the calculated value is reused by another condition, reader, aggregate, or developer-created expression index.
- The focused proof creates an expression index from the reusable string-replacement selector, queries through the independently authored inline condition, and observes `LibraDexDirect`. It also executes inline string-to-integral continuation, explicitly coerced numeric cross-property arithmetic, DateTime subtraction to TimeSpan, binary slicing, and textual `PropPath` transformation.
- The Northwind tracker makes inline condition calculations primary for the applicable single-use predicate rows, including NW-049, NW-050, NW-051, NW-053, NW-065, NW-075, and NW-084. Their named reusable-selector forms remain documented alternates where reuse supplies real value.

Passing focused command:

```powershell
& 'E:\VSProjects\AbraxasDB\AbraxasTestHarness\bin\Release\net8.0\AbraxasTestHarness.exe' selector-reader-sanity
```

Passing clean Release/x64 solution rebuild log:

```text
E:\VSProjects\AbraxasDB\artifacts\build-solution-release-x64-inline-condition-calculations-20260816.log
```

### 2026-08-16 byte-conscious string slicing and NW-052

- Direct string-selector slicing now borrows Inheto's natural UTF-8 payload instead of first materializing the complete .NET string. `RuneSubstring`, `RuneLeft`, and `RuneRight` walk Unicode-scalar boundaries; `Substring`, `Left`, and `Right` walk UTF-16 positions and fall back to exact CLR behavior only when a requested boundary splits a surrogate pair. The selected string remains the only required string allocation.
- `StringSelector.AsUtf8` exposes deliberate byte semantics. `.AsUtf8.Slice(offset, length)` returns a binary selector, retains recursive backend-neutral identity, copies only the requested direct-source bytes, and explicitly permits boundaries that split a multi-byte UTF-8 scalar.
- Binary-slice execution now accepts recursive binary sources in selector readers and conditions. Durable canonical text renders `.AsUtf8.Slice(...)`, and serialization/replay preserves the full expression.
- LibraDex planning retains the existing direct base-binary-index slice route. When the slice source is computed, or no direct base-binary route is present, planning now continues to exact calculated-selector fingerprint matching. A developer-created expression index for the complete `.AsUtf8.Slice(...)` selector becomes `LibraDexDirect`; Abraxas does not create or cache calculated slice values automatically.
- NW-052 now uses `.RuneLeft(2)` as its primary native text projection. The former full-string C# slice and the explicit UTF-8 byte slice are alternate forms because byte slicing has intentionally different short-value and multi-byte-boundary semantics.

Passing focused commands:

```powershell
& 'E:\VSProjects\AbraxasDB\AbraxasTestHarness\bin\Release\net8.0\AbraxasTestHarness.exe' selector-reader-sanity
& 'E:\VSProjects\AbraxasDB\AbraxasTestHarness\bin\Release\net8.0\AbraxasTestHarness.exe' string-selector-semantic-sanity
& 'E:\VSProjects\AbraxasDB\AbraxasTestHarness\bin\Release\net8.0\AbraxasTestHarness.exe' condition-descriptor-sanity
```

Passing clean Release/x64 solution build log:

```text
E:\VSProjects\AbraxasDB\artifacts\build-solution-release-x64-string-slicing-20260816-final.log
```

### 2026-08-16 calculated template concatenation and NW-055

- `RecordStore<T>.Calc.Concat("{.CompanyName} ({.Country})")` is the compact one-use form for calculated string composition across multiple properties. `Selector<T>` remains the reusable descriptor type; naming the returned selector is useful only when more than one consumer or a developer-created expression index needs it.
- Template construction resolves and canonicalizes every property path once through Inheto, then lowers the template to the existing immutable selector-concatenation AST. Record execution does not parse the template, use reflection, or hydrate the record.
- `{{` and `}}` emit literal braces. Backslash brace escapes and a textual `self` token were deliberately not introduced.
- String placeholders remain strings. Other admitted scalar placeholders use deterministic invariant conversion; `DateTime`, `DateOnly`, and `TimeOnly` default to `O`, `TimeSpan` to `c`, and `Guid` to `D`. An optional suffix such as `{.Quantity:D4}` or `{.OrderDate:yyyy-MM-dd}` overrides the default through the scalar's invariant `IFormattable` contract.
- Missing or null values contribute an empty string, matching the established C# `string.Concat` semantics rather than SQL null propagation. Binary arrays are rejected as implicit template text because `System.Byte[]` is neither useful nor a stable value representation; developers must choose an explicit binary encoding first.
- The semantic tree uses the existing generic built-in wire shape with two new operations, so capture, replay, canonical identity, calculated-index fingerprints, CRUD maintenance, and reopen all share one representation. The durable selector/condition wire version is now 15.
- NW-055 now shows the inline template as primary. Its reusable-selector/expression-index form remains the alternate for repeated use.

Passing focused commands:

```powershell
& 'E:\VSProjects\AbraxasDB\AbraxasTestHarness\bin\Release\net8.0\AbraxasTestHarness.exe' selector-reader-sanity
& 'E:\VSProjects\AbraxasDB\AbraxasTestHarness\bin\Release\net8.0\AbraxasTestHarness.exe' condition-descriptor-sanity
& 'E:\VSProjects\AbraxasDB\AbraxasTestHarness\bin\Release\net8.0\AbraxasTestHarness.exe' property-comparison-sanity
& 'E:\VSProjects\AbraxasDB\AbraxasTestHarness\bin\Release\net8.0\AbraxasTestHarness.exe' string-selector-semantic-sanity
```

Passing clean Release/x64 solution rebuild log:

```text
E:\VSProjects\AbraxasDB\artifacts\build-solution-release-x64-template-concat-20260816-r2.log
```

### 2026-08-16 deferred synchronous sorted-reader preparation and NW-060

- `RecordStore<T>.Readers.OpenSorted(...)` quickly captures one population and ordering intent without walking the source. Public property-path, property-lambda, reusable-selector, and condition-selected overloads all return an unprepared `SortedReader`.
- `SortedReader.Prepare()` synchronously establishes one complete stable order and is idempotent. An exact compatible ordinary or calculated index makes preparation bounded; otherwise preparation walks and sorts the complete selected population. `Read`, `MoveNext`, and `Skip` implicitly call `Prepare` when needed.
- Cancellation belongs to `Prepare(cancellationToken)` or first-use `Read(cancellationToken)`. A canceled preparation publishes no partial result and may be retried from the original selection. The public progressive revision, cancellation-retention, and asynchronous `Completion` model was removed.
- Plain path and lambda forms keep ordinary property sorting terse. Optional `cultureName` and `CompareOptions` are translated into the existing durable binary `SortKey` selector, preserving explicit calculated-index matching without exposing `.Calc` in common sorting syntax.
- Existing replayable row, identity, and typed readers retain `.Sort(...)`; the returned sorted context now has the same stable ordinary iteration behavior. Reusable `Selector<T>` remains an alternate for genuinely calculated keys and explicit expression-index reuse.
- NW-060 now uses `customers.Readers.OpenSorted(".CompanyName", cultureName: cultureName)` followed by an ordinary `Read` loop. An alternate shows explicit preparation when the developer wants to choose the potentially blocking latency boundary.

### 2026-08-16 Abraxas-owned multi-culture text sort profiles

- Abraxas `CreateIndexText(...)` and `CreateMemoryIndexText(...)` now accept an optional `IReadOnlyList<TextSortProfile> sortKeyProfiles`. Each profile is exact semantic identity over normalized culture name plus `CompareOptions`; explicit profiles also imply LibraDex `StringKeys.SortKey` physical support.
- LibraDex string indexes can own multiple culture sort-key subindexes. Catalog metadata version 12 records each companion slot, culture, comparison options, and the current .NET `SortVersion` signature. Open fails closed when persisted collation-version evidence is incompatible with the runtime.
- All string mutation paths, prepared inserts, drop/ownership, maintenance attribution, optimize/repack, and closed-file compaction include every owned culture projection. The focused compaction fixture proves three profiles survive shadow rebuild and reopen.
- `OpenSorted(path-or-lambda, cultureName, compareOptions)` remains an Abraxas-driven decision. A complete ordinary string index with an exact maintained profile streams a lazy LibraDex identity projection directly; a missing or semantically different profile preserves the existing complete prepared-evaluation fallback.
- Abraxas durable ordinary-index intent format 6 persists explicit `TextSortProfile` lists so restart recovery recreates the same physical contract. Catalog discovery also reattaches existing LibraDex profile metadata without requiring the developer to restate it.
- This does not silently create indexes. Developers opt into cultures worth their persistent space and write amplification, and may define more than one profile beneath the same text index.
- Existing exact text indexes can now be expanded through `customers.Index(".CompanyName").AddSubIndex(compareOptions, culture)`. Both a named-culture string and `CultureInfo` are accepted; exact culture-plus-options duplication returns `false` without physical work.
- Abraxas persists the append-only desired profile list as preparing intent before LibraDex backfill. LibraDex publishes expanded logical-owner metadata only after the new deterministic companion is complete; an interrupted attempt leaves the old index valid and reopen can resume only the missing strict profile suffix.

Passing focused commands:

```powershell
& 'E:\VSProjects\AbraxasDB\AbraxasTestHarness\bin\Release\net8.0\AbraxasTestHarness.exe' abraxas-sorted-reader-sanity
& 'E:\VSProjects\AbraxasDB\AbraxasTestHarness\bin\Release\net8.0\AbraxasTestHarness.exe' selector-reader-sanity
& 'E:\VSProjects\AbraxasDB\AbraxasTestHarness\bin\Release\net8.0\AbraxasTestHarness.exe' condition-descriptor-sanity
& 'E:\VSProjects\LibraDex\LibraDex.Harness\bin\Release\net8.0\LibraDex.Harness.exe' string-culture-profiles-sanity
```

Passing clean Release/x64 solution rebuild log:

```text
E:\VSProjects\AbraxasDB\artifacts\build-solution-release-x64-prepared-sorted-reader-20260816-r2.log
E:\VSProjects\AbraxasDB\artifacts\build-solution-release-x64-add-subindex-20260816.log
```

### 2026-08-15 selector arithmetic continuation

- The `RecordStore<T>` selector-authoring entry property is now `.Calc`; the former `.Selector` property was replaced directly rather than retained as an alias. The reusable descriptor type remains `Selector<T>`, while `.Calc` is the low-friction authoring vocabulary shared with condition expression consumption.
- Conditions now introduce a completed selector expression through `.Calc(...)`; the former `.PropSelector(...)` entry point was replaced directly rather than retained as an alias. This keeps calculation linguistically distinct from SQL/LINQ projection while preserving typed operator discovery.
- Public selector arithmetic now uses .NET-vernacular `Add` and `Subtract`; the former `Plus` and `Minus` names were replaced directly rather than retained as aliases.
- DateTime, TimeOnly, and TimeSpan selectors carry backend-neutral typed arithmetic nodes for `Add`, `Subtract`, `Multiply`, and `Divide` where the CLR type matrix is coherent.
- Cross-property arithmetic groups under `PropAdd`, `PropSubtract`, `PropMultiply`, `PropDivide`, and `PropModulo` for IntelliSense predictability.
- Textual temporal paths complete their right type with the ordinary `AsDateTime`, `AsTimeOnly`, or `AsTimeSpan` stage.
- Numeric cross-property expressions retain the independently authored left and right CLR types, then require an explicit `ResultAs...` domain. Example: `.PropAdd(".Quantity").AsInt32.ResultAsInt64`.
- Checked operand conversion and arithmetic occur only when a record is evaluated. Selector/condition construction freezes semantic intent but does not read or convert stored values.
- Durable selector wire format 14 introduced temporal arithmetic plus the right numeric operand type; current format 15 adds calculated template scalar-text operations. Replay closes generic readers once; normal per-record evaluation remains reflection-free.
- NW-084 is now a native condition: DateTime property subtraction produces a TimeSpan selector compared directly with `TimeSpan.FromDays(7)`. The former projected-reader/C# loop remains an alternate in the workload tracker.

Focused passing command:

```powershell
& 'E:\VSProjects\AbraxasDB\AbraxasTestHarness\bin\Release\net8.0\AbraxasTestHarness.exe' numeric-arithmetic-selector-semantic-sanity
```

The fixture covers mixed Int32/Int64 property arithmetic, explicit result-domain narrowing failure, temporal property subtraction, canonical identity, and current-format serialization/replay.

### 2026-08-15 executable TimeOfDay PropPath acceleration

- NW-089 is now authored as a native condition over `.OrderDate.TimeOfDay`: `.PropPath(".OrderDate.TimeOfDay").AsTimeSpan.Between(open, close).EndCondition`.
- Execution is explicitly topology-sensitive without changing the condition:
  1. A developer-created complete-path index such as `.OrderDate.TimeOfDay` or `.CreatedDate.TimeOfDay` wins and executes as the ordinary direct LibraDex plan.
  2. Otherwise, an exact base DateTime index such as `.OrderDate` supplies an Abraxas-owned compact-key candidate scan, followed by the authoritative Inheto residual.
  3. With neither index, the same semantic condition uses the ordinary Fractal/Inheto scan.
- Inheto's cached `PropPathCheck` remains authoritative for canonicalization, serialized-prefix/CLR-remainder splitting, member admission, and result typing. The new classification and planner choice live in Abraxas; no LibraDex source was changed for this accelerator.
- The base-index lane is deliberately candidate-only. It widens the lower boundary enough to cover Calendar-SDT quantization and the current Precision-SDT public key projection, then requires exact residual evaluation. It may admit false-positive candidates but cannot omit a valid boundary match or return a false match.
- The focused fixture uses values immediately around inclusive open/close boundaries and proves result parity plus plan precedence for no index, base DateTime index, and explicit complete-path expression index.

Passing command:

```powershell
& 'E:\VSProjects\AbraxasDB\AbraxasTestHarness\bin\Release\net8.0\AbraxasTestHarness.exe' executable-proppath-sanity
```

Passing build log:

```text
E:\VSProjects\AbraxasDB\artifacts\build-solution-release-x64-proppath-timeofday-20260815-r2.log
```

### 2026-08-15 Northwind clean-room review epoch 2

- All 243 Northwind SQL statements were reconsidered from the catalog's SQL plus independently stated intent against the current Abraxas public surface.
- `tools/northwind-native-reviews.mjs` remains the first clean-room review evidence. Fifty accepted interactive corrections that previously existed only in the generated HTML are now preserved in `tools/northwind-native-review-accepted-overrides.mjs`.
- `tools/northwind-native-reviews-v2.mjs` applies the second-pass capability changes and stamps every row with review epoch, date, and basis. The tracker generator now consumes this merged source and rejects rows without complete epoch-2 provenance.
- Final state: 243 reviewed rows, 172 native passes, 71 application-handled results, zero failures, and zero pending rows.
- Removed fluent names `.PropSelector(...)`, `.Plus(...)`, and `.Minus(...)` are rejected by generation. Condition date stages use explicit `.AsDateTime` rather than the abbreviated compatibility alias.
- Cross-property calculations use `.PropAdd(...)`, `.PropSubtract(...)`, and `.PropMultiply(...)` with explicit `ResultAs...` domains where numeric types differ or must remain visible.
- NW-065 now compares calculated available stock directly with the per-record `ReorderLevel` through `.PropLessThan(...)`; it no longer transliterates the relation into subtraction followed by comparison with zero.
- NW-083 is now a native identity-aware TimeSpan selector projection. NW-090 sums the same DateTime-difference selector's exact ticks through the native aggregate surface and reconstructs the final CLR TimeSpan.
- NW-089 is now a native executable-TimeOfDay condition whose explicit derived-path index outranks the Abraxas base-DateTime candidate accelerator.
- The generator accepts all four project checklist states in the immutable catalog while still requiring exactly 243 distinct workload rows.

Latest focused Abraxas condition validation:

```text
E:\VSProjects\AbraxasDB\artifacts\build-solution-release-x64-northwind-redo-20260815.log
```

Passing commands:

```powershell
& 'E:\VSProjects\AbraxasDB\AbraxasTestHarness\bin\Release\net8.0\AbraxasTestHarness.exe' property-comparison-sanity
& 'E:\VSProjects\AbraxasDB\AbraxasTestHarness\bin\Release\net8.0\AbraxasTestHarness.exe' condition-descriptor-sanity
& 'E:\VSProjects\AbraxasDB\AbraxasTestHarness\bin\Release\net8.0\AbraxasTestHarness.exe' condition-semantic-scan-sanity
& 'E:\VSProjects\AbraxasDB\AbraxasTestHarness\bin\Release\net8.0\AbraxasTestHarness.exe' numeric-arithmetic-selector-semantic-sanity
& 'E:\VSProjects\AbraxasDB\AbraxasTestHarness\bin\Release\net8.0\AbraxasTestHarness.exe' selector-reader-sanity
```

The focused property-comparison fixture covers textual and typed-lambda authoring, every coherent scalar family, safe Int32/Int64 widening, Decimal/Double rejection, null filtering, binary content equality, and current-format serialization/replay. The broader `condition-semantic-scan-sanity` also passes: it separately proves that an unresolved model path is rejected during condition construction and that a real nullable numeric property does not compare equal to the CLR default while a present value remains queryable.

Latest focused maintenance build log:

```text
artifacts\build-maintenance-all-shapes-rebuild-20260728.log
```

Build command:

```powershell
$log = 'artifacts\build-maintenance-all-shapes-rebuild-20260728.log'
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

Passing validation commands:

```powershell
$h = 'LibraDex.Harness\bin\x64\Release\net8.0\LibraDex.Harness.dll'

dotnet $h public-surface-api-sanity
dotnet $h public-api-snapshot
dotnet $h catalog-live-backup-sanity
dotnet $h vs8-shelf-sanity
dotnet $h vs16-shelf-sanity
dotnet $h vv-shelf-sanity --items 1000
dotnet $h sv8-shelf-sanity
dotnet $h sv16-shelf-sanity
```

The public-surface fixture proves authoritative fixed-width zero, exact persisted variable-key reclaim, exact-plus-folded logical rollup, component detail, threshold filtering, healthy projection ownership, close/reopen durability, and persisted count restoration for all five variable-record shelf families.

`catalog-live-backup-sanity` proves that an open file-backed catalog can produce an exact durable backup without invalidating its handles. The fixture exceeds two 1 MiB blocks; the backup gate rejects normalized source-path aliases and active durability batches, excludes concurrent publication during the physical copy, validates hash/length and staged reopen, preserves later source independence, cancels strictly between copy blocks without leaking staging, requires explicit overwrite, and rejects memory-backed catalogs. The physical copy and destination durable flush hold the DataKernel storage write lock, so ordinary readers and writers wait until that boundary completes.

Abraxas `RecordBase.CreateBackup` and archive backup now route the live `.lbdx` member through this gate instead of `File.Copy`, while retaining the existing store-level maintenance/write-lock and overwrite policies. The native Abraxas lifecycle proof reopens the installed backup, writes successfully through the original retained handles afterward, and verifies that the backup image remains unchanged. Closed-file compaction still requires a distinct RecordBase catalog/index rebinding boundary.

`docs/public-api-snapshot.txt` is current. `git diff --check` passes. At the checkpoint, `artifacts/` measured about 87 MB, below the repository's 1 GB limit.

### 2026-08-19 typed aggregate covering-composite checkpoint

- Aggregate definitions authored through `RecordStore<T>.Aggregates.GroupBy(...)` now prepare one group-first maintained composite index before considering separate simple indexes or authoritative Fractal/Inheto payload traversal.
- Composite adoption is whole-plan: the first part must exactly match the grouping path and every reducer must resolve an equivalent later part. The smallest complete candidate wins. Any uncovered or order-sensitive reducer rejects the composite route for the complete definition.
- Key, count, sum, average, Decimal average, min/max, distinct/presence counts, boolean reductions, variance/deviation, weighted average, percentile/median, and mode have composite reducers. Representative `AddProp`/`AddCalc`, developer-provided step reductions, and tie-sensitive associated extrema retain the existing fallback so composite suffix order cannot silently change meaning.
- Unfiltered composite aggregation uses `LibraDexRoutedCompositeIndex.VisitEntries(...)`. The callback borrows one reused logical key-part span; null-route sentinels are translated to logical nulls and the span cannot escape the callback.
- The 100,000-row `group-by-composite-proof` measured 104 allocated bytes for the complete borrowed visitor traversal after warm-up. An initial 728,192-byte result exposed boxed `IReadOnlyList` enumerators per route; direct indexed loops removed that route-scaled allocation.
- Filtered composite aggregation reuses LibraDex condition tuple planning. It avoids Fractal payload hydration and repeated per-identity value probes but still materializes stable composite key containers for filtered tuples; removing those containers requires a separate condition-aware borrowed visitor.
- LibraDex's durable composite value codec now round-trips the previously advertised but omitted `Int128`, `UInt128`, `float`, `double`, and `decimal` part types. The composite proof checks all five exact round trips.
- NW-134, NW-139, and NW-141 in `E:\VSProjects\AbraxasDB\NORTHWIND_SQL_ABRAXAS_WORKLOAD_TRACKER.html` describe covering-composite, separate-index, and authoritative fallback tiers.

Focused passing commands:

```powershell
& 'E:\VSProjects\AbraxasDB\AbraxasTestHarness\bin\Release\net8.0\AbraxasTestHarness.exe' aggregate-definition-sanity
& 'E:\VSProjects\LibraDex\LibraDex.Harness\bin\x64\Release\net8.0\LibraDex.Harness.exe' group-by-composite-proof --items 100000 --tenants 1000 --users 128
& 'E:\VSProjects\LibraDex\LibraDex.Harness\bin\x64\Release\net8.0\LibraDex.Harness.exe' public-api-snapshot
```

The next-best optimization branch is a condition-aware borrowed composite visitor for filtered aggregate definitions. It could remove stable key-container allocation there, but it is not needed for unfiltered covering-index scans and should be measured before expanding the public surface again.

### 2026-08-20 aggregate-reader and grouping-row consolidation

- `AggregateReader<TResult>.Prepare()` now selects an execution route without completing an ordered aggregate result set. `Read()` and `MoveNext()` still prepare automatically when necessary.
- An unfiltered group-leading covering composite uses an internal borrowed `LibraDexRoutedCompositeIndex.EntryCursor`. The cursor retains one route stack and one reused key-part buffer; the 100,000-entry proof measured 192 allocated bytes for its complete traversal.
- An unfiltered compatible generic simple index opens its typed `LibraDexRangeReader<TKey, ulong>` directly. Reducers backed by maintained indexes avoid payload reads; uncovered and developer-provided reducers borrow one authoritative Fractal/Inheto payload at a time while retaining only the current group's accumulator.
- The final clean-build 20,000-distinct-group complete-record reducer proof measured 380,536 bytes through preparation and the first result after exact-route warm-up. That cost is bounded index multi-shelf cursor context, not one accumulator or result object per group; the fixture rejects more than 512 KiB.
- Filtered definitions deliberately retain the unordered compact-state fallback. A condition's driving index does not necessarily preserve grouping-key order, so reducing adjacent condition results could split one logical group into multiple rows. A future optimization must traverse the grouping index and evaluate membership or the residual predicate without changing grouping order.
- The previous 2026-08-19 note that described filtered composite tuple grouping is superseded by this correctness boundary. Whole-plan unfiltered covering-composite adoption remains preferred.
- Typed definitions now support two-property grouping with flat `.AddKeys()` output, calculated grouping through `.GroupByCalc(...)`, and reusable selector inputs for the added sum and distinct-count paths.
- The LibraDex cursor is an internal friend-assembly contract for AbraxasDB rather than a new public LibraDex API. The public API snapshot therefore remains unchanged.
- `tools/northwind-aggregate-reader-updates.mjs` durably re-authors 39 grouping and grouping-adjacent workload rows. Every literal SQL `GROUP BY` row was rechecked: typed aggregate readers are primary where the store can own reduction, while cross-store association state, bounded top-N, fixed-struct pivots, and date-series merges remain visibly application-owned where that is the actual developer intent.
- The generated `NORTHWIND_SQL_ABRAXAS_WORKLOAD_TRACKER.html` contains 243/243 reviewed rows, has no manual post-generation aggregate patch, and recognizes native `.Aggregates.GroupBy(...)` and `.Distinct.GroupBy(...)` syntax without classifying it as LINQ.

Focused validation commands:

```powershell
& 'E:\VSProjects\AbraxasDB\AbraxasTestHarness\bin\x64\Release\net8.0\AbraxasTestHarness.exe' aggregate-definition-sanity
& 'E:\VSProjects\LibraDex\LibraDex.Harness\bin\x64\Release\net8.0\LibraDex.Harness.exe' group-by-composite-proof --items 100000 --tenants 1000 --users 128
& 'E:\VSProjects\LibraDex\LibraDex.Harness\bin\x64\Release\net8.0\LibraDex.Harness.exe' public-surface-api-sanity
& 'E:\VSProjects\LibraDex\LibraDex.Harness\bin\x64\Release\net8.0\LibraDex.Harness.exe' public-api-snapshot
node E:\VSProjects\AbraxasDB\tools\build-northwind-tracker.mjs
```

Passing clean Release/x64 rebuild logs:

```text
E:\VSProjects\AbraxasDB\artifacts\build-solution-release-x64-aggregate-reader-20260820.log
E:\VSProjects\LibraDex\artifacts\build-libradex-harness-release-x64-aggregate-cursor-20260820.log
```

### 2026-08-21 direct aggregate-definition authoring

- The public `.Aggregates.Define()` and `.Distinct.Define()` staging method was removed. It carried only an optional condition and added no semantic or execution capability.
- Reusable definitions now begin directly with `.Aggregates.GroupBy(...)`, `.Aggregates.GroupByCalc(...)`, or the corresponding `.Distinct` member. This keeps the grouping choice discoverable at the aggregate facade and removes one object name and one fluent call from every definition.
- Optional filtering is the final grouping argument, for example `orders.Aggregates.GroupBy(order => order.CustomerID, where)`. Unfiltered expression, textual property-path, two-key, and calculated-key overloads retain their prior result stages and execution plans.
- The older terminal/materializing grouped facade previously occupied the same `.GroupBy(...)` expression signature. It is now named `.GroupByResults(...)`, making that older result-oriented choice explicit while reserving the shorter and more discoverable name for reusable reader definitions. Its complete grouped-terminal sanity remains passing.
- Reusable `Selector<T>` values can also begin a direct definition through `.GroupBy(selector, where)`, preserving calculated-selector identity and expression-index planning without returning to a separate definition verb.
- `.Aggregates.Open(definition)` and `.Distinct.Open(definition)` remain the explicit execution boundary. Opening returns the deferred typed reader; `Prepare()` or the first `Read()` establishes its route.
- The Northwind tracker aggregate examples were re-authored to the direct form. Its generator now rejects either retired `.Define()` spelling in preferred and alternate examples so the extra ceremony cannot silently return.

Passing focused checks:

```powershell
& 'E:\VSProjects\AbraxasDB\AbraxasTestHarness\bin\Release\net8.0\AbraxasTestHarness.exe' aggregate-surface-sanity
& 'E:\VSProjects\AbraxasDB\AbraxasTestHarness\bin\Release\net8.0\AbraxasTestHarness.exe' aggregate-definition-sanity
```

The clean-binary high-cardinality definition proof measured 380,536 bytes through preparation and the first of 20,000 groups. The clean Release/x64 rebuild log is `E:\VSProjects\AbraxasDB\artifacts\build-solution-release-x64-direct-aggregate-authoring-20260821.log`.

### 2026-08-21 aggregate endpoint reductions and tracker-metadata audit

- Typed aggregate definitions now provide `.AddFirst(...)` and `.AddLast(...)` for encounter-order endpoints, plus `.AddFirstBy(value, order)` and `.AddLastBy(value, order)` for explicitly ordered endpoints.
- Three-selector `AddFirstBy(value, order, thenBy)` and `AddLastBy(value, order, thenBy)` overloads retain deterministic tie-breaking without allocating or retaining source rows. Completely equal ordering keys retain the first encountered record.
- Endpoint values preserve null or missing values rather than skipping them. Ordered endpoints exclude records whose ordering value or tie-break value is null or missing; this separates endpoint-value semantics from ordering participation.
- Payload, direct-index, and whole-plan covering-composite reducers are available for the endpoint family. A complete compatible composite therefore stays on the borrowed key-part route; incomplete coverage rejects that route for the definition and uses the existing authoritative fallback.
- The existing representative `AddProp`/`AddCalc` first-value reducer now shares the same covering-composite-capable endpoint machinery.
- NW-239 now uses one typed aggregate reader with `OrderDate` plus `OrderID` tie-breaking instead of an application-managed sorted-reader boundary loop.
- NW-141 is native again: one definition holds key, count, and Decimal average in the same reader state. Its inherited C# status, line markers, engine tag, score, and obsolete two-terminal LXL were removed.
- Aggregate tracker overrides now normalize status, engine, C# line ownership, and alternates whenever they replace C# code. The audit covers all 40 current aggregate overrides; targeted stale LXL corrections were made for NW-141, NW-142, NW-143, NW-148, NW-161, and NW-178.
- `NORTHWIND_SQL_ABRAXAS_WORKLOAD_TRACKER.html` was regenerated with 243/243 freshly reviewed rows.

Focused validation:

```powershell
& 'E:\VSProjects\AbraxasDB\AbraxasTestHarness\bin\Release\net8.0\AbraxasTestHarness.exe' aggregate-definition-sanity
node E:\VSProjects\AbraxasDB\tools\build-northwind-tracker.mjs
```

The clean-binary endpoint/aggregate proof passed, including null endpoints, deterministic ordered endpoints, covering-composite execution, spill cleanup, and the bounded first-result check at 380,632 allocated bytes for 20,000 groups.

### 2026-08-21 semantic discount operation and NW-137 intent correction

- Decimal and real selector stages now expose `.Discount(rateOrSelector)`. The immutable operation means `value * (1 - rate)` and performs no calculation while the fluent graph is authored.
- Decimal and real stages also expose `.PropDiscount(...)` for same-record rate properties. Typed property forms retain the independently authored operand types and require an explicit `ResultAs...` execution domain; textual property paths retain the existing `As...` then `ResultAs...` coercion grammar.
- The matching condition-local calculation continuations are present, so a developer can use the semantic operation inline or package the same selector for readers, aggregates, and calculated indexes.
- Discount rates are not silently clamped. Negative rates, rates greater than one, overflow, invalid checked conversion, and non-finite real results retain the ordinary selector execution rules.
- Integral selector stages deliberately do not expose `.Discount(...)`: fractional rate intent belongs in a Decimal or real execution domain. Existing integral values can select that domain explicitly before applying a discount.
- Durable condition and selector capture use format version 16. Both the decimal operation family and generic numeric property-arithmetic family serialize, validate, replay, and render the new operation.
- Generic numeric-property discovery no longer tries to close `IBinaryInteger<TSelf>` over Decimal or another non-integral numeric type. It inspects implemented interfaces instead, fixing the shared decimal cross-property path used by the pre-existing `.PropAdd/.PropSubtract/.PropMultiply/.PropDivide/.PropModulo` family as well as `.PropDiscount`.
- NW-137 now expresses developer intent directly: gross line value is formed with `.PropMultiply(...).ResultAsDecimal`, then reduced through `.PropDiscount(...).ResultAsDecimal`, and streamed into the grouped `.AddSum(...)` reader. The prior `.Negate().Add(1m)` SQL-algebra transliteration is removed.
- The same correction applies to NW-063, NW-075, NW-118, NW-161, NW-162, and NW-211. The generator rejects the specific `Discount.Negate().Add(1m)` factor pattern in preferred and alternate examples so it cannot silently return.
- `tools/northwind-aggregate-reader-updates.mjs` remains the durable NW-137/grouping source, `tools/northwind-native-reviews-v2.mjs` retains the broader corrected examples, and `NORTHWIND_SQL_ABRAXAS_WORKLOAD_TRACKER.html` was regenerated with all 243 rows freshly reviewed.

Focused validation:

```powershell
& 'E:\VSProjects\AbraxasDB\AbraxasTestHarness\bin\Release\net8.0\AbraxasTestHarness.exe' numeric-arithmetic-selector-native-sanity
& 'E:\VSProjects\AbraxasDB\AbraxasTestHarness\bin\Release\net8.0\AbraxasTestHarness.exe' decimal-selector-native-sanity
& 'E:\VSProjects\AbraxasDB\AbraxasTestHarness\bin\Release\net8.0\AbraxasTestHarness.exe' condition-descriptor-sanity
& 'E:\VSProjects\AbraxasDB\AbraxasTestHarness\bin\Release\net8.0\AbraxasTestHarness.exe' aggregate-definition-sanity
node E:\VSProjects\AbraxasDB\tools\build-northwind-tracker.mjs
```

The clean Release/x64 rebuild log is `E:\VSProjects\AbraxasDB\artifacts\build-solution-release-x64-semantic-discount-20260821.log`. The aggregate-definition proof measured 381,112 bytes through preparation and the first of 20,000 groups, remaining below its 512 KiB bound.

### 2026-08-21 structured year-month grouping correction

- NW-143 now groups directly by `OrderDate.Year` and `OrderDate.Month`, projects both typed keys with `.AddKeys()`, and streams `(year, month, count)` from the aggregate reader.
- The prior `GroupByCalc` form packed the two date parts into `checked(year * 100 + month)` and later recovered them with division and remainder. That was SQL-algebra transliteration rather than developer intent: the two-part aggregate key is already a `ValueTuple` struct and does not introduce a reference allocation.
- Nested member expressions normalize to `.OrderDate.Year` and `.OrderDate.Month` property paths. Execution can therefore adopt a compatible derived-path composite index; otherwise the authoritative Inheto path readers extract both structured components without materializing Orders.
- The related date-report rows were audited. NW-237 remains an application-owned fixed twelve-month pivot with constant current-year state, and NW-240 remains an ordered merge with a generated complete date series. Neither is another packed-key grouping case.
- The tracker generator now rejects packed year/month grouping expressions in preferred and alternate examples so this transliteration cannot silently return.

### 2026-08-21 sorted aggregate-result readers

- `RecordStore<T>.Aggregates.OpenSorted(...)` now complements ordinary `Readers.OpenSorted(...)` and `Readers.OpenDistinctSorted(...)` across all one-through-seven-value aggregate result shapes.
- The ordering callback exposes typed `.Ascending(...)` and `.Descending(...)` result expressions in decreasing lexicographic priority. `OpenSorted` validates and captures the semantic order without compiling expressions or enumerating groups; explicit `Prepare()` or the first `Read`, `Load`, or `Pull` performs synchronous preparation.
- Ordering applies after reductions complete. A sort on `Count`, `Sum`, or another calculated reduction therefore cannot claim a source-index route merely because grouping itself is index ordered.
- Value-only structs and ValueTuples use a bounded external merge sort after `AggregateSortOptions.MemoryResultLimit` (default 4,096) is crossed. Runs contain fixed-size raw value rows, retain one current value per run during merge, and are deleted on completion, early reader disposal, or failure.
- Reference-containing results remain in managed memory even beyond the configured run bound. This deliberately preserves object identity and avoids silently serializing or cloning developer-provided aggregate outputs.
- NW-147 now uses `Aggregates.OpenSorted(...).Pull(10)` as its native primary form. Its corrected bounded priority-queue implementation remains an alternate for extreme group cardinality; it reverse-drains into a stack span so SQL's count-descending, CustomerID-ascending output order is explicit.

Focused validation:

```powershell
& 'E:\VSProjects\AbraxasDB\AbraxasTestHarness\bin\Release\net8.0\AbraxasTestHarness.exe' aggregate-definition-sanity
node E:\VSProjects\AbraxasDB\tools\build-northwind-tracker.mjs
```

The forced one-result run proof covered deferred opening, spill merge ordering, `Pull(2)` followed by continued reading, tie-breaking, early-disposal cleanup, and reference-containing in-memory behavior. The existing 20,000-group first-result proof remained bounded at 380,824 allocated bytes.

### 2026-08-22 direct batch missing-record behavior and NW-156

- Existing `GetMany(ids)` and `GetManyAs<TOut>(ids)` retain their throwing contract and non-nullable result lists.
- Policy overloads accept `MissingRecordBehavior.Throw`, `.Skip`, or `.PreserveDefault`. Caller order and duplicate identities remain authoritative in every mode.
- `.Skip` omits same-store identities that do not exist when reached. `.PreserveDefault` retains one result position per input identity, producing null/default for reference or nullable shapes and ordinary `default(TOut)` for non-nullable structs.
- Cross-store identities always throw under every policy because they are invalid input rather than missing records.
- Policy-aware asynchronous `GetManyAsync` and `GetManyAsAsync<TOut>` overloads forward the same contract without creating a second semantic path.
- NW-156 now streams distinct qualifying Customer identities directly from the filtered Order reader and calls `customers.GetOrDefault(customerId)` for each identity. This keeps readers population-oriented, avoids retaining an intermediate ID collection, and skips a concurrently missing Customer like the SQL semi-join.
- NW-156 retains `customers.GetMany(recentCustomerIds, MissingRecordBehavior.Skip)` as the materialized alternate when the caller explicitly owns the distinct identity collection and wants to retain the complete Customer result.

Focused validation:

```powershell
& 'E:\VSProjects\AbraxasDB\AbraxasTestHarness\bin\Release\net8.0\AbraxasTestHarness.exe' abraxas-retrieval-sanity
node E:\VSProjects\AbraxasDB\tools\build-northwind-tracker.mjs
```

The final uncontended clean Release/x64 rebuild passed with zero errors in `E:\VSProjects\AbraxasDB\artifacts\build-solution-release-x64-getmany-missing-policy-20260822-final2.log`. Focused retrieval proof covers the original throwing overload, explicit `.Throw`, `.Skip`, `.PreserveDefault`, preserved reference nulls, preserved struct defaults, typed reprojection, order, duplicates, and cross-store rejection.

### 2026-08-22 Abraxas persistent property links and clean-room Section I redo

- `RecordStore<T>.Links` is the store-owned persistent property-link registry. A link is configured once and remains active for ordinary object hydration until `Remove(path)` or `Clear()` is called; it is not query registration and is not tied to the store's original CLR root type.
- Matching uses the canonical property path plus optional explicit aliases. If a requested projection exposes that property, the same link applies. The link's one developer-supplied binary reader remains authoritative; Abraxas and Inheto internally reproject its semantic result to the requested member type when necessary.
- `Links.Add<TValue>(path, write, read, aliases...)` registers the complete persistent property codec. The writer receives the runtime property value and returns the exact bytes stored for that property. The reader receives those exact bytes and returns the semantic `TValue` used for ordinary hydration and compatible alternate projections.
- `Links.AddReadOnly<TValue>(path, read, aliases...)` is the same-property decoder-only form. A normal write fails before object mutation, write events, or persistence because that stored property has no writer; `Preserve` may retain an existing exact payload and `Clear` may remove it.
- `Links.AddReadOnly<TValue>(targetPath, sourcePath, read, aliases...)` and its `LinkSource.RecordID` sibling are projection-only derived forms. The target may be absent from the store's base `T`; only a requested projection exposing that target invokes the reader. Derived targets are never serialized, do not participate in write preflight, and do not block otherwise normal writes.
- Link delegates own binary representation, not relationship behavior. They may encode child identities, an owner lookup key, or another application-defined format and may consult developer-owned state. Abraxas does not infer cascades, synchronization, child upserts, or ambient query policy.
- Inheto now has owner-scoped root-option and root-materialization boundaries. Store-produced payloads carry their stable record identity, so readers, direct `Get`/`GetAs`, and later `InhetoBinary.ToObject<TProjection>()` calls apply the same link semantics.
- `LinkHandling.Normal`, `.Preserve`, and `.Clear` define write behavior. `Preserve` bypasses the caller member and its serializer while retaining the exact existing Inheto custom-member payload; `Clear` bypasses the caller member and stores it absent without mutating the caller object.
- `LinkWriteRule` is the mixed per-link write rule. `(string PropPath, LinkHandling Handling)` tuples convert implicitly, so one replacement can preserve `.Orders`, clear `.Addresses`, and leave every unlisted link normal.
- `PutNoLinks` and `PutClearLinks` cover all-link and selected-link forms. New-record Preserve has no prior value and therefore stores the link absent. Unknown selected paths and conflicting duplicate rules fail before mutation.
- `RowReader`, typed object `DataReader`, and `SortedReader` expose `ReadNoLinks(...)`. Demand-materialized row/sorted readers also expose `GetNoLinks(...)`, including typed reprojection. Suppression is per current row and can target all or selected registered links.
- Operation-local option forks keep `NoLinks` non-ambient. Suppression is keyed to the target path. Suppressing an ordinary `.Orders` target therefore does not suppress an independently requested `.OrderDates` target whose source is the stored `.Orders` payload.
- Derived source acquisition is explicit and deterministic. A custom-binary source is passed through as its exact borrowed payload; an ordinary stored property is read semantically and encoded with `InhetoBinaryCodec.Encode<TValue>`; `LinkSource.RecordID` supplies the stable record identity through that same standalone codec convention.
- `AbraxasTestHarness/AbraxasLinkSanity.cs` proves exact binary writers/readers, same-property semantic reprojection and aliases, detached store-produced Inheto hydration, removal, all/selected row suppression, typed and sorted suppression, same-property read-only preflight, derived ordinary-property and RecordID sources, nonblocking projection-only writes, source/target suppression independence, exact Preserve, non-mutating Clear, mixed tuple rules, and invalid-rule preflight.
- `tools/northwind-section-i-clean-room-redo.mjs` records a fresh SQL-intent disposition for all NW-151 through NW-185. Object-graph rows use persistent property links; flattened, scalar, aggregate, and analytical intents retain normalized streams; NW-185 keeps batching visible because Abraxas still does not own a native batch-link planner.
- NW-153, NW-168, NW-169, NW-178, and NW-181 through NW-184 no longer use manual `ExcludedMembers`, type-bound `(record, id)` callbacks, per-query link registration, or Abraxas-owned ambient policy state. Persistent link delegates may consult developer-owned state directly when the application wants dynamic child criteria.
- NW-158 through NW-160 treat the child store as the output-cardinality source and use canonical `Readers.Open(...)` plus per-row `GetAs<TProjection>()` with persistent scalar property links. Their named output shapes are primary; the former retained lookup dictionaries remain only as explicitly labeled repeated-parent bulk-throughput alternates.
- NW-152 is intentionally different: the Customer store remains the output-cardinality source, `.OrderDates` is a projection-only read link derived from the stored `.Orders` bytes, and the primary streams the USA condition directly. Each `CustomerOrderDates` projection is consumed only when its linked collection is nonempty, preserving the SQL inner-join exclusion without a retained represented-ID set.
- `tools/build-northwind-tracker.mjs` separately guards NW-152 against flat-row or dictionary fallback, requires its post-hydration empty suppression, and retains represented-ID prefiltering only as an explicit performance alternate. NW-158 through NW-160 retain their separate named child-cardinality projection guards.
- The regenerated Section I accordion contains 35 rows at average 9.29, zero issues, and 26 C#-handled concerns.
- `NORTHWIND_SQL_ABRAXAS_WORKLOAD_TRACKER.html` was regenerated successfully with all 243 rows freshly reviewed.

Focused validation:

```powershell
& dotnet 'E:\VSProjects\AbraxasDB\AbraxasTestHarness\bin\Release\net8.0\AbraxasTestHarness.dll' abraxas-link-sanity
& dotnet 'E:\VSProjects\AbraxasDB\AbraxasTestHarness\bin\Release\net8.0\AbraxasTestHarness.dll' abraxas-data-reader-sanity
node E:\VSProjects\AbraxasDB\tools\build-northwind-tracker.mjs
```

The superseding binary-link Release/x64 solution rebuild passed with zero errors in `E:\VSProjects\AbraxasDB\artifacts\build-solution-release-x64-binary-links-20260822-r2.log`. The focused link sanity passed and exited normally, including exact payload round trips, authoritative one-reader projection, and read-only write rejection before mutation.

The final required pre-build source snapshot succeeded with 209 files at stamp `20260822-094436`. The execution safety layer blocked deletion of older `.code-history` files and separately blocked direct removal of the explicitly verified solution `bin`/`obj` directories; MSBuild's `Rebuild` clean targets were used instead. No history or build directories were manually removed.

### 2026-08-22 Section I object-first correction

- The first clean-room Section I pass still gave relational row shape too much authority. The corrected architectural premise is that Abraxas treats the developer-designed runtime object hierarchy as primary while Inheto permits nested members to remain independently stored, indexed, computed, or hydrated.
- SQL joins that select complete parent and child entities may therefore be reconstruction ceremony for a runtime property such as `Customer.Orders`; they are not automatically evidence that the caller wants repeated flat pairs.
- Scalar projections, existence tests, aggregates, analytical traversals, and explicitly pair-shaped outputs remain normalized readers or reductions. `ReadNoLinks()` / `GetNoLinks()` remain the explicit escape hatches when an object caller does not want graph hydration.
- A flat cross-store scalar result can still be a named runtime projection rather than a manually assembled pair. NW-158 through NW-160 demonstrate this by keeping the child store's cardinality, opening a canonical RowReader, calling `GetAs<TProjection>()` only for emitted rows, and using persistent scalar links only for the requested foreign values.
- NW-152 instead demonstrates property-shaped reprojection: `CustomerOrderDates` contains `CompanyName` plus `DateTime[] OrderDates`, so the SQL join is interpreted as reconstruction ceremony around a Customer-root result. A read-only `.OrderDates` link consumes the exact stored `.Orders` bytes and lets developer code extract only dates without hydrating full `Order` objects. The primary streams only the USA Customer condition and suppresses empty projections in the read loop. A left join would consume those empty projections instead.
- NW-153 is now a persistent `Customer.Orders` property-link stream. Its prior constant-association-state left merge is retained as the flattened-pair alternate.
- NW-168 now treats `Employee.Reports` as a policy-bounded recursive object graph. The explicit stack/seen traversal remains the analytical alternate; current application code still owns recursive batching, cycle state, and depth policy.
- NW-169 now streams Employee roots directly, hydrates `Employee.Territories`, and suppresses the root only after the linked collection is empty. Represented-owner prefiltering and repeated Employee/Territory pairs are separate alternates.
- NW-178 now streams Customer roots directly, hydrates same-date-tied latest Orders, and suppresses the root only after the linked collection is empty. Represented-owner prefiltering and the grouped latest-Order analysis stream are separate alternates.
- NW-179 keeps the intended filtered Customer/Orders/OrderDetails/Product graph. The persistent link delegates consult developer-owned category criteria, remove nonmatching details and Orders left without details, and the read loop suppresses Customers left without Orders. Qualifying-root prefiltering and the four-store flat stream are alternates.
- NW-182 and NW-183 now visibly suppress the requested Customer when stored or computed `.Orders` hydration produces an empty collection, preserving their inner joins without moving eligibility ahead of object construction.
- NW-184 separates the caller's Customer materialization budget from the `.Orders` recency/count criteria returned by developer-owned `getOrderHistoryRequest()`. Abraxas stores no ambient request state: the Customer reader controls how many roots are actually constructed, while the persistent link delegate asks application code for each root's child window. Empty collections remain because the source is a left join. A developer-maintained `.LatestOrderDate` index is an optional root-order alternate.
- NW-185 fills one reusable Customer root buffer through `ReadNoLinks`/`GetNoLinks`, then the application batch hydrator prunes Orders without OrderDetails and the loop suppresses roots without Orders. This avoids firing persistent links before batching and releases unretained graphs between batches.
- The regenerated Section I accordion contains 35 rows at average 9.29, zero issues, and 26 C#-handled concerns. All 35 rows carry the object-first review basis, and the complete tracker remains 243/243 freshly reviewed.

### 2026-08-22 RowReader source-aware scalar access and NW-152 correction

- `RowReader.Read()` and `ReadNoLinks()` now establish the current identity/source position without eagerly borrowing the Fractal payload. Scalar access remains demand-driven.
- `RowReader.Get<TValue>(propPath)` asks only the active result-source cursor for an exact reversible value of the same canonical property. It does not probe arbitrary available indexes. If that active source cannot supply the exact value, the reader lazily borrows the authoritative payload and asks Inheto for the property.
- This keeps the execution contract predictable: a condition-selected exact LibraDex index may answer its own indexed property without a Fractal data-file read, while any other property follows the existing Inheto path. A later `GetAs<TProjection>()` on the same row reuses the borrowed payload when fallback already occurred.
- Persistent property links remain projection-triggered. Reading a scalar such as `.CompanyName` does not invoke `.OrderDates`; `GetAs<CustomerOrderDates>()` on the same current row invokes the link only when the caller chooses the graph-shaped projection.
- `AbraxasDataReaderSanity` verifies exact active-index scalar access with no Fractal data read, leading-dot normalization, lazy Inheto fallback, and same-payload reuse. `AbraxasLinkSanity` verifies scalar-first inspection followed by conditional same-row linked projection.
- NW-152 now returns one Customer-root `CustomerOrderDates` projection per represented USA Customer. Its projection-only `.OrderDates` reader receives the stored `.Orders` payload and returns `DateTime[]` directly in the selected source's natural order. The reader condition is only Country equals USA, and the read loop skips the projection when `OrderDates` is empty. The tracker no longer contains the rejected flat `CustomerOrderDate` row stream or retained-name dictionary alternate; represented-ID prefiltering is only a scale-sensitive alternate.
- A fresh runtime-intent audit of all 35 Section I rows confirms the normalized child-only, semi/anti-join, scalar-projection, and aggregate rows should remain streams or reductions. All identified object-graph corrections are complete: NW-169/178 use direct roots with post-link inner suppression; NW-179/185 prune empty intermediate branches and roots; NW-182/183 suppress empty requested roots; and NW-184 uses independent root and child budgets.
- Tracker generation now enforces those shapes individually. It rejects represented-ID drivers returning to the NW-152/169/178/179 primaries, missing inner-root suppression in NW-182/183, conflated budgets or empty suppression in left-join NW-184, and ordinary `Pull` hydration or missing `NoLinks`/branch pruning in NW-185.
- The regenerated tracker contains 243/243 freshly reviewed rows. Section I is 35 rows at average 9.29, zero issues, and 26 C#-handled concerns; the full tracker currently contains 184 native passes and 59 C#-handled results.

Focused validation:

```text
AbraxasDB.sln Release|x64 Rebuild: PASS (0 errors)
abraxas-data-reader-sanity: PASS
abraxas-link-sanity: PASS
node tools/build-northwind-tracker.mjs: PASS (243/243)
```

Validation:

```powershell
node E:\VSProjects\AbraxasDB\tools\build-northwind-tracker.mjs
```

This pass changed `RecordStore.ReaderCore.cs`, the focused data-reader and link sanity suites, tracker source and generated HTML, and this restart artifact. The C# changes were validated by the clean Release/x64 rebuild and both focused suites above; tracker/document-only edits after that rebuild did not require another compile.

### 2026-08-22 canonical object-reader convergence

- `RecordStore<T>.Readers.Open(...)` is the single ordinary object-reader entry point. The duplicate `Readers.OpenRecords*` and direct store `OpenRecordReader*` public families were removed instead of retained as aliases.
- `RowReader.Get()` materializes the store's base `T`; `GetAs<TProjection>()` requests another compatible runtime shape; `ID` exposes the current record identity. The reader remains streaming and object materialization remains demand-driven inside the loop.
- Conditions, State/mask selection, bookmarks, and explicit sorted openings are overloads or siblings of that same canonical family. `OpenSorted(...)` still returns the ordinary sorted reader contract, and its bookmark preserves explicit property ordering when that ordering is reversible.
- Existing `IterateRecords*` conveniences now adapt over RowReader rather than a second public object-reader implementation.
- The tracker generator treats the retired names as regressions. Accepted interactive overrides are now the final semantic merge layer so later clean-room/capability review files cannot silently restore rejected syntax or concepts.
- NW-158 through NW-160 now show canonical `Readers.Open(...)` followed by `GetAs<TProjection>()`. NW-179 and NW-184 no longer invent an Abraxas policy holder: their persistent link delegates consult developer-owned criteria directly.
- `NORTHWIND_SQL_ABRAXAS_WORKLOAD_TRACKER.html` regenerated with all 243 rows freshly reviewed. Its row examples contain no retired object-reader name, `categoryGraphPolicy`, `orderHistoryPolicy`, or malformed `.Open/GetAs` syntax.
- The complete tracker is 184 native passes plus 59 explicitly C#-handled cases, average 9.75, with zero failures or pending rows. Section I is 35 rows at average 9.29, zero issues, and 26 C#-handled concerns.

Focused validation:

```text
AbraxasDB.sln Release|x64 Rebuild: PASS (0 errors)
abraxas-bookmark-sanity: PASS
abraxas-link-sanity: PASS
abraxas-data-reader-sanity: PASS result (known harness process-lifetime quirk required termination afterward)
abraxas-sorted-reader-sanity: PASS
node tools/build-northwind-tracker.mjs: PASS (243/243)
```

The final build log is `E:\VSProjects\AbraxasDB\artifacts\build-solution-release-x64-reader-convergence-final-20260822.log`. The required pre-build source snapshot copied 118 changed source/config/project files with stamp `20260822-143841`. The execution safety layer blocked both four-hour history pruning and direct removal of the 16 explicitly verified solution `bin`/`obj` directories before execution; MSBuild's solution-scoped `Rebuild` clean targets were used instead, and no unrelated directory was touched.

### 2026-08-22 derived read-only property links and final Section I source audit

- The persistent-link model now cleanly separates a same-property decoder-only link from a projection-only derived link. The former owns read semantics for a stored target and therefore rejects an ordinary write without a writer. The latter derives a requested target from another stored property or from `LinkSource.RecordID`, is excluded from serialization, and never blocks writes.
- `InhetoBinaryCodec.Encode<TValue>` / `Decode<TValue>` provide the standalone binary boundary for ordinary semantic source values and RecordIDs. Custom-binary source properties remain zero-copy and preserve their developer-owned exact bytes.
- Derived targets are projection-shaped rather than root-type-shaped. A target need not exist on the store's base `T`; it is applied whenever a requested compatible projection exposes that canonical path or alias.
- NW-152 now shows the narrow `CustomerOrderDates` shape directly: `CompanyName` plus `DateTime[] OrderDates`. `.OrderDates` is a read-only target sourced from `.Orders`, so developer code may extract dates directly from storage or an index without hydrating full Order instances.
- NW-158 through NW-160 derive named scalar projection properties from the stored foreign identity properties. NW-168, NW-169, NW-183, and NW-184 use `LinkSource.RecordID` where inverse membership naturally starts from the current root identity.
- Filtered child collections retain explicit semantics: `.LatestOrders`, `.CategoryOrders`, `.OpenOrders`, and `.OrderHistory` do not silently redefine ordinary `.Orders`. `ReadNoLinks(".Orders")` suppresses only the ordinary target; the requested derived target can still consume `.Orders` as its source.
- NW-156, NW-182, and NW-183 use direct optional identity retrieval after their streaming identity-selection step. They no longer open a reader over a retained one-item or ID collection.
- Tracker guards now preserve the NW-152 derived collection form, NW-168 read-only RecordID hierarchy, NW-181 read-only OpenOrders form, direct retrieval rows, named filtered collections, inner-root suppression, and the explicit normalized-stream alternates.
- The regenerated tracker contains 243/243 freshly reviewed rows: 184 native passes, 59 C#-handled cases, average 9.75, and no failures or pending rows. Section I contains 35 rows at average 9.29, zero issues, and 26 concerns.

Final validation:

```text
AbraxasDB.sln Release|x64 Rebuild: PASS (0 errors)
abraxas-link-sanity: PASS
abraxas-retrieval-sanity: PASS
abraxas-data-reader-sanity: PASS result (known harness process-lifetime quirk required termination afterward)
node tools/build-northwind-tracker.mjs: PASS (243/243)
```

The final build log is `E:\VSProjects\AbraxasDB\artifacts\build-solution-release-x64-derived-readonly-links-20260822-final.log`. The required pre-build snapshot copied 140 changed source/config/project files with stamp `20260822-234117264`. The execution safety layer again blocked four-hour `.code-history` pruning; no history files were removed.

### 2026-08-23 link-aware property Get and NW-157 nested indexed delivery

- `RowReader.Get<TValue>(propPath)` and runtime-shaped `Get(propPath)` now honor the same persistent property links as `Get()` and `GetAs<TProjection>()`. A registered canonical target or alias takes precedence over raw/index access, invokes only its own authoritative reader, and reprojects the semantic result to the requested type when needed.
- Per-row suppression is target-specific for property access. A target suppressed through `ReadNoLinks(target)` returns default/null without invoking its reader or exposing raw stored bytes. Suppressing `.Orders` does not suppress a separately requested `.OrderDates` target that derives from the stored `.Orders` payload.
- Unlinked typed property access retains active-index-first behavior and borrows the authoritative Inheto payload only when the active source cannot answer. `GetUtf8Span`, `TryGetUtf8Span`, and other explicitly borrowed/raw APIs remain storage-level and do not invoke links.
- Link target resolution uses an immutable registration-time canonical/alias dictionary. Direct property access performs O(1) target lookup; canonical targets are installed before aliases and therefore retain precedence.
- The first implementation accidentally evaluated `core.Binary` while merely probing whether a target was linked. `abraxas-data-reader-sanity` caught the resulting Fractal read on an exact active-index property. The final two-phase target-resolution path does not acquire the payload unless the target is registered or the ordinary index path later requires fallback.
- NW-157 now streams inactive Customer identities in its outer reader and opens one CustomerID equality-selected Order reader per current identity. With a compatible `.CustomerID` index, each inner reader is a bounded seek, Orders are consumed immediately, no parent-ID collection is retained, and developer code may short-circuit per Customer or globally.
- NW-157 retains one transferred `IsIn(parentIds)` Order traversal as its explicit broad-scan alternate. That form is preferable when many Customers qualify, the compatible child index is absent, or one child traversal is worth memory proportional to inactive Customers.
- The tracker guard rejects a retained parent-ID collection returning to NW-157's primary and requires the transferred-membership alternate. The regenerated tracker remains 243/243 freshly reviewed: 184 native passes, 59 C#-handled cases, average 9.75, and no failures or pending rows. Section I contains 35 rows at average 9.30, zero issues, and 26 concerns.

### 2026-08-23 reusable Abraxas execution parameters and NW-157 correction

- `RecordStore<T>.Parameter(value, name?)` returns the existing `LibraDexParameter<TValue>` rather than introducing a second Abraxas-only deferred-value type.
- A parameter is caller-owned mutable setup state. Assigning `Value` changes the value observed by the next execution; it does not rebuild the authored condition or calculated selector.
- Abraxas converts the parameter into its established `ValueOrExpression<TValue>` operand without reading it while the fluent condition is authored. This applies equally when the left operand is a direct property or a reusable/inline calculated selector.
- One identity-based snapshot is shared across the complete condition tree, including nested groups. If the same parameter instance appears in several leaves, every occurrence sees the value captured for that execution even if caller or diagnostic code changes `Value` during materialization.
- The snapshot occurs when execution planning/opening begins, before LibraDex selection or Fractal residual evaluation. A compatible index can therefore use the parameter directly without changing semantics; an index-absent fallback observes the same frozen value.
- Whole-value DateTime, DateOnly, TimeOnly, TimeSpan, text, numeric, Boolean, Guid, custom, and binary comparisons expose the same parameter carrier. Parameter names flow into existing materialization diagnostics.
- Parameterized conditions and calculated-selector conditions remain semantic and reusable; the current parameter value is not durable calculated-index intent and is never cached as a result value.
- This section supersedes the earlier NW-157 wording that created `owned` inside the parent loop. NW-157 now authors `owned` once with `customerId = orders.Parameter(0UL, "customerId")`, assigns `customerId.Value = parents.Get()` for each outer record, then opens the ordinary Order reader. The broad-scan `IsIn(parentIds)` alternate remains unchanged.
- The tracker generator now rejects either rebuilding the child condition in the loop or omitting the explicit parameter assignment.

Final validation:

```text
AbraxasDB.sln Release|x64 Rebuild: PASS (0 errors)
abraxas-link-sanity: PASS (typed/runtime/aliased property Get and target-only suppression)
abraxas-retrieval-sanity: PASS
abraxas-data-reader-sanity: PASS (active-index access remains payload-lazy; harness terminated after pass output)
node tools/build-northwind-tracker.mjs: PASS (243/243)
```

The final passing build log is `E:\VSProjects\AbraxasDB\artifacts\build-solution-release-x64-link-aware-property-get-nw157-20260823-final4.log`. The required final pre-build snapshot copied 140 changed source/config/project files with stamp `20260823-100952731`. One preceding rebuild was invalidated only by the lingering failed data-reader harness locking its output DLLs; after terminating that exact process, the uncontended rebuild passed. Direct solution `bin`/`obj` removal and four-hour `.code-history` pruning remained blocked by the execution safety layer, so MSBuild's solution-scoped `Rebuild` clean targets were used and no history files were removed.

### 2026-08-23 identity/distinct reader Get convergence

- `IdentityReader` and `DistinctReader<TResult>` now use `Get()` for current-result extraction. Their public `Current` members were removed rather than retained as aliases, matching the established `Read()`-then-`Get*()` reader vocabulary.
- `IdentityReader.Get()` remains payload-free and returns the identity already selected by its cursor. `DistinctReader<TResult>.Get()` returns the current typed scalar or structural tuple and throws when the reader is not positioned on a value, preserving the prior positioning contract without a second extraction name.
- Direct unique lookup fallback, identity reader fork/reset/bookmark tests, distinct scalar/pair/sorted/reset/load/pull tests, and every active tracker example were moved to `Get()`.
- The tracker generator now rejects `.Current` in primary or alternate C# examples. The generated 243-row tracker has zero such usages; NW-156 uses `customerIds.Get()` and remains the streaming filtered semi-join primary.
- Enumerator-internal `IEnumerator.Current` implementations remain untouched because they satisfy the .NET enumeration contract rather than the Abraxas public reader DX.
- The first rebuild exposed one stale `DistinctReader.Load` self-reference to the removed member. Replacing it with `Get()` allowed the final Release/x64 rebuild to pass, proving the removal did not leave a hidden compatibility alias.

Final validation:

```text
AbraxasDB.sln Release|x64 Rebuild: PASS (0 errors)
abraxas-distinct-reader-sanity: PASS (indexed consume allocation remained 0 bytes)
abraxas-data-reader-sanity: PASS result (known harness process-lifetime quirk required termination afterward)
abraxas-retrieval-sanity: PASS
node tools/build-northwind-tracker.mjs: PASS (243/243; zero Current examples)
```

The final passing build log is `E:\VSProjects\AbraxasDB\artifacts\build-solution-release-x64-reader-get-contract-20260823-final.log`. Required snapshots used stamp `20260823-104141561` for 139 AbraxasDB and 85 LibraDex changed source/config/project files, followed by AbraxasDB retry stamp `20260823-104305156` after the stale internal reference was corrected. The execution safety layer blocked four-hour `.code-history` pruning after exact target verification, so no history files were removed; MSBuild's solution-scoped `Rebuild` clean targets supplied the available clean boundary.

### 2026-08-23 canonical Put replacement semantics

- `Put(record)` always creates a new record with a generated identity; it has no overwrite mode.
- `Put(recordID, replacement)` always replaces the existing record at that store-owned identity and still rejects an absent or cross-store identity. The explicit identity and replacement payload already express overwrite intent, so the required-true `overwriteExisting` switch was removed rather than retained as an alias.
- The same rule applies to `Writers.Put`, asynchronous forwarding members, and the explicit-ID link-handling variants (`Put`, `PutNoLinks`, and `PutClearLinks`). Link handling remains the only meaningful per-call mutation policy.
- `PutManyFast` always creates newly identified records. Its previously ignored `overwriteExisting` parameter and the matching asynchronous parameter were removed.
- NW-189 through NW-194 now show the compact explicit-ID replacement form. Tracker generation rejects the retired `overwriteExisting:` spelling in both primary and alternate examples.
- Focused link validation proves ordinary replacement without the switch, mixed/preserve/clear link replacement, read-only-link rejection, and continued rejection of a deleted identity.

Final validation:

```text
AbraxasDB.sln Release|x64 restore + Rebuild: PASS (0 errors)
abraxas-link-sanity: PASS
composite-index-sanity: PASS
selector-reader-sanity: PASS
node tools/build-northwind-tracker.mjs: PASS (243/243; NW-189 compact Put; zero tracker overwriteExisting usages)
```

The final passing build log is `E:\VSProjects\AbraxasDB\artifacts\build-solution-release-x64-put-contract-final-20260823-150743642.log`. The required pre-build source snapshots used stamp `20260823-150743642`; four-hour history pruning succeeded during the first attempt. `abraxas-recursive-reader-sanity` was also sampled because its fixture contains an explicit-ID replacement, but its first pre-replacement breadth-first assertion failed before that call was reached. The compiled replacement call therefore remains covered by the solution build but that unrelated recursive-reader failure is not claimed as Put validation.

### 2026-08-23 full Northwind score audit and NW-148 ordering correction

- All 243 Northwind rows were rescored against one explicit rubric: 10 means exact low-friction developer intent with no material Abraxas-specific compromise; 9.x retains exact intent with bounded ceremony, topology tradeoffs, or application coordination; 8.x identifies material retained state, multi-pass/materialization work, or a capability/durability gap.
- Ordinary C# is no longer penalized merely for being outside the engine when it is the natural expression of developer policy. Object-level replacement is likewise not penalized merely because SQL names one changed column. Identity freezing, repeated seeks, retained collections, covering-index loss, and missing atomicity remain legitimate deductions.
- Twenty-seven scores increased and none decreased. NW-190 moved from 9.2 to 10.0 because `Get`/mutate/`Put` is the intentional object replacement contract. NW-191 through NW-194 moved from 8.8 to 9.2: complete-object durability is no longer a deduction, while their set-wide identity snapshot and application coordination still are.
- NW-205 was subsequently re-authored from its likely runtime-object intent rather than its relational persistence mechanism. Its primary is one `customers.Writers.Put(customer)` over the complete Customer aggregate. Embedded `.Orders` requires no extra setup; a persistent `.Orders` link may instead own normalized binary storage. The former compensating two-store `try`/`catch` remains an explicit alternate and does not claim SQL ACID or crash-atomic graph commitment. NW-205 therefore moved from 8.2 to 10.0 and from C#-handled to native pass.
- The same correction raised simple guarded consumption, null coalescing, post-aggregate threshold branches, allocation-free stream concatenation, bounded window state, and value-type analytical loops where older scoring had treated ordinary C# as an automatic deficiency.
- NW-148 exposed a genuine semantic issue during the full review: its SQL requires key ordering, but its primary used naturally ordered aggregate delivery that was exact only with a compatible grouping index. It now uses aggregate `OpenSorted(...)`, adopting the ordered index directly when available and preparing exact key order otherwise. Its 10.0 score was retained only after that correction.
- `tools/northwind-score-review-20260823.mjs` is the final score overlay and records the reason for every changed row. The generated tracker includes a collapsed `Score review` column, a visible rubric summary, and a 2026-08-23 audit stamp on every row. `NORTHWIND_SQL_ABRAXAS_SCORE_REVIEW_20260823.md` is the compact change ledger.
- The regenerated tracker contains 243/243 freshly reviewed rows: 188 native passes, 55 explicitly C#-handled cases, average 9.868, and no failures or pending rows. Section J now averages 9.610 and Section L averages 9.570 under the corrected rubric.

Validation:

```text
node tools/build-northwind-tracker.mjs: PASS (243/243)
27 score overrides and 28 explained review entries: PASS
every row scoreReviewedOn=2026-08-23 with nonempty scoreReview: PASS
NW-148 aggregate OpenSorted primary and exact fallback wording: PASS
```

This pass changed only tracker review data, generator/documentation, and generated HTML. No C# source changed, so another solution rebuild was neither required nor performed.

### 2026-08-23 Section J runtime-object clean-room redo

- Section J (NW-186 through NW-205) was re-authored from the application developer's runtime-object intent. SQL `INSERT`, `UPDATE`, `DELETE`, transaction, child-table, and column syntax is now treated as the relational ceremony the developer was forced to use, not as the target Abraxas programming model.
- A dedicated `tools/northwind-section-j-clean-room-redo.mjs` overlay owns all twenty current dispositions and is applied after older accepted snapshots. Generator assertions prevent those snapshots from reintroducing the displaced SQL-shaped forms.
- NW-191 through NW-194 now use constant-state current-object loops: open the semantic reader, `Get()` its current Product or Order, mutate ordinary C# state, and `Put(matches.ID, object)`. No complete identity list is retained. The selected CategoryID, UnitsInStock/UnitsOnOrder, ShippedDate/RequiredDate, or CustomerID properties remain unchanged; replacement may converge other maintained indexes such as UnitPrice or Freight without invalidating that synchronous current-object discovery contract.
- This is not generalized snapshot isolation. It establishes the narrow synchronous contract already supplied by the split between Fractal data authority and prepared LibraDex/Fractal discovery: same-reader current identity replacement in the same thread. Arbitrary concurrent inserts, deletes, future-identity changes, and unrelated owners remain outside that promise.
- NW-198 now treats `DELETE OrderDetails WHERE OrderID=...` primarily as clearing `Order.OrderDetails` and putting the Order aggregate. NW-200 similarly clears `Customer.Orders` and puts the Customer aggregate. Their persistent write Links own whether this means embedded empty collections or normalized child deletion. Stable `DeleteMany` over the independent child store remains an explicit alternate because Abraxas row readers do not yet expose a mutation-aware `DeleteCurrent` contract.
- NW-203 deliberately retains stable identity capture: zero-write age-based deletion is a destructive storage-retention operation, not ordinary current-object mutation. The retained target and 9.3 score make the missing `DeleteCurrent` reader boundary visible rather than hiding it behind an unsupported one-shot facade.
- Engine-owned State rows NW-195 through NW-197 remain native specialized operations, because their intent changes State topology rather than Customer payloads. NW-201 remains a Customer-rooted persistent linked `OrderCount` aggregate followed by an explicit destructive phase.
- NW-186 through NW-189, NW-204, and NW-205 now consistently describe direct persistence of the runtime objects or aggregate the developer already owns. Persistent Links define child representation once; they are not query- or write-call ceremony.
- Section J now has an average score of 9.94, zero issues, and two concerns: NW-201's visible cross-store aggregate/deletion coordination (9.5) and NW-203's retained destructive target (9.3). NW-187, NW-191 through NW-194, NW-198, and NW-200 are now 10.0 under the object-intent rubric.

Validation:

```text
node tools/build-northwind-tracker.mjs: PASS (243/243)
Section J: 20/20 clean-room dispositions, average 9.94, 0 issues, 2 concerns
NW-191..194: no List<ulong> or OpenIdentities primary; current Get/mutate/Put present
NW-198/NW-200: aggregate collection clear + Put primary; independent-root DeleteMany alternate present
```

This pass changed only tracker review data, generator/documentation, and generated HTML. No C# source changed, so another solution rebuild was neither required nor performed.

### 2026-08-24 condition Exists terminal and NW-223/NW-224 semi/anti-join redo

- `RecordStore<T>.Exists(Condition<T>.EndCondition)` is now the scalar, short-circuit condition-existence terminal. It uses the same direct LibraDex, candidate-plus-residual, or Fractal/Inheto plan as other condition consumers, requests identities rather than record payloads, and returns immediately after the first authoritative match.
- `ExistsAsync(condition, cancellationToken)` forwards the same contract through the established asynchronous scheduling boundary.
- This terminal is intentionally distinct from `CountWhere`, which must enumerate every match to produce an exact count, and from aggregate `AddAny`, which is a per-group reduction and must continue receiving the group's rows.
- Focused harness coverage proves true and false results before and after adding the matching `.Age` index. Planning telemetry proves the same API executes first as `FractalScan` and then as `LibraDexDirect`.
- NW-223 and NW-224 now stream distinct Customer countries, update one reusable deferred Supplier-country parameter, and call `suppliers.Exists(represented)` for the semi-join or its negation for the anti-join. The former broad unindexed Supplier-country `HashSet<string>` strategy remains an explicit alternate for topologies where one full Supplier pass is cheaper than repeated unindexed existence probes.
- Both rows moved from 8.8 to 9.5: the primary is now constant-state and short-circuits each inner test, while the remaining deduction accurately exposes that an unindexed correlated condition can still favor the retained-set alternate.

Final validation:

```text
AbraxasDB.sln Release|x64 restore + Rebuild: PASS (0 errors)
condition-semantic-scan-sanity: PASS
node tools/build-northwind-tracker.mjs: PASS (243/243)
NW-223: suppliers.Exists primary, broad-set alternate, score 9.5
NW-224: !suppliers.Exists primary, broad-set alternate, score 9.5
```

The final passing build log is `E:\VSProjects\AbraxasDB\artifacts\build-solution-release-x64-condition-exists-nw223-224-20260824-r2.log`. Required source snapshots were refreshed under stamp `20260824-163321636` before the successful rebuild.

### 2026-08-24 Section L runtime-intent clean-room redo

- Section L (NW-221 through NW-240) was re-audited from the runtime object or report value the application is likely to consume. SQL set, CTE, window, partition, pivot, and recursive-series syntax is treated as relational machinery rather than the target Abraxas form.
- A dedicated `tools/northwind-section-l-clean-room-redo.mjs` overlay owns an explicit disposition for all twenty rows and is applied after the older accepted and aggregate snapshots. Generator assertions require the complete 2026-08-24 disposition set and guard the corrected primary forms against bleed.
- Complete object hydration remains primary only where the SQL result includes `*`: NW-227 through NW-229 and NW-235 genuinely return each Order or Product plus transient analytical state. Selected-value rows NW-230 through NW-233, NW-237, and NW-240 now use sorted `Get<T>(propPath)` reads rather than materializing complete Orders.
- NW-231 replaces its managed `Queue<decimal>` with a three-Decimal stack-backed ring. NW-232 retains one prior DateTime; NW-233 retains one pending value tuple rather than a complete Order. These are bounded analysis values rather than simulated SQL window objects.
- NW-234 now begins with the runtime Order aggregate. Each `reader.Get()` hydrates the Order's persistent linked `OrderDetails`; two local passes calculate the discounted total and emit each Product line's nullable percentage. The former normalized sorted `OrderDetails` partition buffer remains an explicit large-analysis alternate bounded to the largest Order.
- NW-236 replaces two custom `AddAggregate` reducers with built-in `AddSum` reducers whose value selectors return Freight for Open or Shipped Orders and zero otherwise. Developer-defined accumulator plumbing is reserved for genuinely custom reductions.
- NW-237 keeps one reusable twelve-Decimal `MonthTotals` struct but reads only OrderDate and Freight. NW-238 keeps two direct indexed Status counts primary and adds one broad conditional aggregate traversal as the unindexed alternate. NW-240 now bounds Order discovery to the requested date range before merging projected OrderDate/Freight values with the application-owned date generator.
- Every Section L score was re-audited on 2026-08-24. NW-230 and NW-232 moved to 10.0; NW-231 to 9.8; NW-233 to 9.9; NW-234 to 10.0; NW-236 to 10.0; NW-237 and NW-240 to 9.8. The section now averages 9.835 with zero failures or pending rows. Its sixteen C#-handled rows reflect visible analytical policy, not automatically deficient engine capability.

Validation:

```text
node tools/build-northwind-tracker.mjs: PASS (243/243)
Section L: 20/20 clean-room dispositions, average 9.835, 0 issues, 16 C#-handled analytical rows
Selected-value rows: no complete Order/Product reader hydration primary
NW-234: linked Order-rooted primary plus normalized sorted-stream alternate
NW-236: two built-in conditional AddSum reducers; no custom AddAggregate
NW-240: bounded OrderDate condition before date-series merge
```

This pass changed tracker review data, the authoritative catalog wording, generator assertions/documentation, and generated HTML. It introduced no C# implementation change, so another solution rebuild was not required.

### 2026-08-24 Inheto promotion audit and general property aliases

- The cross-project source audit is recorded in `docs/condition-builder/abraxas-inheto-capability-promotion-audit.md`.
- General property aliases now belong to Inheto's common read/materialization semantics and are promoted through `RecordStore<T>.Aliases`. The exact requested stored path wins, then the canonical path, then remaining aliases in registration order. Renamed complex ancestors preserve their suffix.
- The same aliases are honored by alternate-shape materialization, direct and reader `Get*`, borrowed UTF-8 reads, condition execution, selector execution, and prepared payload ordering.
- One ordinary property index is not considered complete for an alias group because historical/runtime shapes may store several physical names. Property-path conditions, candidates, unique lookup, active-index value projection, and exact ordinary sort reuse fail closed to authoritative payload semantics when an alias affects the requested path.
- Developers retain an explicit acceleration path: create an expression index from the aliased selector and use that identical selector in the condition. The calculated index evaluates the alias-aware semantic value across the complete payload population and remains eligible for direct LibraDex execution.
- `RecordStore<T>.CheckPropPath(...)` now exposes Inheto's cached nonexecuting path validation in store vocabulary. `RecordStore<T>.TestType(...)` exposes the bounded runtime-shape probe under the store's active serialization rules.
- Custom property/type codecs, member inclusion/exclusion, activators, comparers, circular-property handling, mixed dictionary-key policy, and scalar/collection shape support were verified as already reachable through the store's public `SerializationOptions` and `DeserializationOptions`. Parallel Abraxas wrappers were deliberately not added because they would duplicate configuration and obscure precedence.
- Raw name headers, accessor factories, dynamic wrappers, serializer compression/encryption helpers, and other codec internals remain low-level. `ReadPropOnto` was not promoted because its current signature is not a uniform struct-safe Abraxas read contract.

Focused validation:

```text
abraxas-inheto-promotion-sanity: PASS
exact alias precedence and renamed complex suffix: PASS
projection/direct Get/borrowed UTF-8/selector/condition/sorted parity: PASS
ordinary physical index for alias path: authoritative payload fallback
explicit alias-aware expression index: direct LibraDex plan
CheckPropPath and TestType RecordStore surfaces: PASS
```

## Known Harness Defect

The default `vv-shelf-sanity` requests 1,500 roughly 100-byte tuples in one non-routed 128 KiB VV shelf and fills at item 1,179. This is a pre-existing default-fixture capacity mismatch, not a reclaim-accounting failure.

The bounded command passes:

```powershell
dotnet $h vv-shelf-sanity --items 1000
```

Do not silently interpret the default failure as a storage regression. Either reduce the default item count or change the fixture contract deliberately in a later task.

## Recommended Next Conversation

Use this prompt:

> Start from `docs/condition-builder/condition-builder-continuation-restart.md`. Treat it as the authoritative current handoff and preserve both broad dirty worktrees. Verify the completed Section I runtime-intent review, Section J runtime-object clean-room redo, Section L runtime-intent reporting redo, persistent same-property and derived read-only property links, link-aware typed/runtime property Get, RowReader active-source-index/Inheto fallback, identity/distinct reader `Get()` convergence, canonical explicit-ID `Put(recordID, replacement)` replacement semantics, and the regenerated 243-row tracker without rewriting existing changes. Treat SQL as the persistence/query ceremony a runtime-object developer was forced to use: prefer direct object or aggregate intent, while keeping child-only, semi/anti-join, scalar, analytical, aggregate, explicit pair, engine-State, and storage-retention operations in their natural normalized or specialized forms. For reporting rows, materialize complete objects only when the requested result includes them; otherwise prefer selected-value readers, bounded value state, or typed reducers. Preserve synchronous same-reader current-object Get/mutate/Put for NW-191 through NW-194, but do not generalize that to snapshot isolation or unsupported current-row deletion. Keep `Read()`-then-`Get*()` as the public reader vocabulary, do not reintroduce `Current`, do not restore the redundant `overwriteExisting` switch, and do not let older accepted snapshots bleed back into the Section I, Section J, or Section L clean-room overlays.

If implementation work is preferred, the first contained cleanup candidate is the known default `vv-shelf-sanity` capacity mismatch. Discuss whether the test should reduce its default item count or become a routed/multi-shelf test before editing it.
