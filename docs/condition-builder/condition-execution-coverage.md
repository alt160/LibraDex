# Condition Execution Coverage Matrix

> Current continuation note: for the latest fluent decisions, 219-row worklist state, maintenance-assessment implementation, validation checkpoint, and paste-ready restart prompt, begin with [`condition-builder-continuation-restart.md`](condition-builder-continuation-restart.md). The restart prompt at the end of this older execution matrix is retained as historical context and is superseded by that handoff.

Purpose: prove the adopted LibraDex condition-builder surface has a common executable read/filter pipeline before expanding selective delete/mutate over that pipeline.

Public-readiness stability note: see `docs/condition-builder/condition-execution-stability.md` for the public contract language around native indexed execution, projection-backed execution, scan/residual fallback, composed identity-set execution, inverse execution, external/runtime-source execution, and target-owned selective mutation.

## Current State

- `docs/condition-builder/filter-dx-natural-language-corpus.csv` has 671 rows.
- All 671 rows are score `5`.
- No score-`5` row is missing LibraDex syntax, Abraxas syntax, or an explicit corpus note.
- `rebuild-filter-dx-corpus.ps1` intentionally fails fast because scripted NL-to-builder generation is deprecated.
- Temporal binary-slice factories require type-specific encoding enums. LibraDex Calendar/Precision SDT and ordered TimeSpan values compare as packed big-endian scalars; raw .NET ticks, day numbers, and ticks-plus-offset forms compare from their documented little-endian layouts without materializing candidate temporal objects.
- Opened indexes expose eager exact-key materialization through `Identities.GetByKey(s)`, `CountByKey(s)`, and `Entries.GetByKey(s)`. Typed facades retain key/identity typing; `IIndex` non-generic overloads return canonical key and identity bytes. Conditions remain responsible for streaming, bookmarks, filtering, and caller-defined iteration.
- Latest mutation-gate validation passed with `artifacts\build-harness-release-x64-pattern-mutation-20260611.log` and `public-surface-api-sanity --path artifacts\public-surface-api-sanity-pattern-mutation-20260611.lbdx`.
- The mutation contract is target-owned: `catalog[group][target].Delete(condition)`, `.SetKey(condition, newKey)`, `.SetKeyUsing(condition, oldKey => newKey)`, and `.DeleteAll()`; completed conditions expose no mutation terminals.
- The mutation-gate pass found and fixed a real composed-retrieval correctness bug: execution-planner identity sets now use LibraDex structural object equality, so `byte[]` identities compare by content across `AND`, `OR`, difference, complement, distinct, and paging/materialization paths instead of default reference equality.

## Execution Class Legend

- `native indexed execution`: condition materializes to ordinary exact/range/boundary/membership/multirange primitives over the selected index.
- `projection-backed execution`: condition resolves to a maintained physical projection index such as folded text, sort key, reversed text, structured date, GUID segment/text, or exact-reversed binary.
- `scan/residual fallback`: condition narrows candidates when possible, then applies a row predicate over decoded or encoded key bytes.
- `external/runtime-source`: condition consumes caller-provided identity filters, identity sources, runtime selectors, or deferred operands.
- `inverse execution`: condition executes through the index-set inverse identity/key map.
- `terminal result shape`: the condition already selects identities; the terminal reshapes, pages, groups, counts, or iterates them.
- `policy/corpus note`: syntax is intentionally not executable condition logic because the row needs external policy, projection, domain logic, or result presentation.

## Matrix

| Family | Representative corpus rows | Public API shape | Expected execution class | Existing proof | Missing proof | Risk | Next harness check | Notes |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Scalar exact/range/boundary/set/negation | F001-F034, F061-F063, F187 | `Index("age").AsInt32.Between(...)`, `AsUInt64.NotInSet(...)` | native indexed execution | `ValidateDeterministicScalarConditionCoverage()` covers signed/unsigned widths, `char`, deferred operands, exact/range/boundary/set/negation | None for read/filter | Low | Keep under `public-surface-api-sanity` | Mutation should reuse the materialized primitive leaf, not re-parse builder syntax. |
| Boolean and enum-as-scalar | F036-F038, F061-F063 | `AsBoolean.EqualTo(false)`, enum values through scalar widths | native indexed execution | `ValidateDeterministicScalarConditionCoverage()` covers bool exact/not/ordering and enum-backed scalar exact/set/not | None for read/filter | Low | Keep under scalar coverage | Bool uses finite CLR bounds, preventing invalid sentinel aliasing. |
| Bitmask conditions | F066-F080 | `AllBitsSet(...)`, `NoBitsSet(...)`, `BitAnd(mask, value)` | scan/residual fallback over indexed candidates | `ValidateDeterministicScalarConditionCoverage()` covers UInt32, UInt64, byte, signed Int32, and composition with string | None for read/filter | Medium | Add mutation only after tuple-capture behavior is defined for residual primitives | Residual predicates are correct for reads; selective mutate needs exact tuple capture from the same candidate stream. |
| String exact/no-case/prefix/suffix/contains/membership/null/empty/ordering | F136-F150, F195, F197 | `AsString.EqualTo(...)`, `StartsWith(...)`, `EndsWith(...)`, `Contains(...)`, `InSet(...)`, `NullKey.*` | native indexed execution, projection-backed execution, scan/residual fallback | `ValidateDeterministicStringConditionCoverage()` covers exact, folded, sort-key/reversed, no-case, contains, membership, null/empty, and ordering | Broader culture-specific scan policy remains intentionally explicit | Medium | Keep current deterministic string fixture; add any future culture-specific rows as row-shaped assertions | Maintained projections are preferred; residual scan is visible and bounded by supplied indexes. |
| String regex and wildcard | F144, F441, F493, F509-F511 | `Like(...)`, `Matches(...)`, `MatchesWith(...)`, `MatchesInSet(...)`, negative aliases | scan/residual fallback | `ValidateDeterministicStringConditionCoverage()` covers wildcard, regex bool, regex instance, capture compare, capture membership, and capture negation | Tokenization/parsing rows remain policy/projection notes | Medium | Add one row-shaped assertion whenever a new regex operator is added | Regex should stay residual; do not imply indexed regex support without a projection. |
| Structured date/time comparisons and relative windows | F104-F110, F626 plus date/time corpus rows | `AsDateTime.*`, `AsDateTimeOffset.*`, `IsYesterday()`, year/month/day/quarter/weekend/time-of-day branches | native indexed execution, scan/residual component fallback | `ValidateDeterministicStructuredDateTimeCoverage()` and public-surface DateTimeOffset fixture cover structured comparisons, components, relative windows, encoding-aware DateTime, TimeOnly, TimeSpan, DateOnly, DateTimeOffset | Business/fiscal/timezone calendar rows remain policy/projection notes | Medium | Keep deterministic clock values in fixtures; avoid wall-clock-dependent assertions | Conditions compare stored encodings; business calendars require declared external context. |
| GUID exact/set/text-like/pattern | GUID exact/text/pattern rows | `AsGuid.EqualTo(...)`, `InSet(...)`, `StartsWith(...)`, `EndsWith(...)`, `Contains(...)`, `MatchesPattern(...)` | native indexed execution, projection-backed execution, scan/residual fallback | `public-surface-api-sanity` GUID bridge path prints `adoptedGuidBridge ok`; composite and inverse fixtures also use GUID keys | Add a compact dedicated GUID matrix helper only if GUID bridge assertions become hard to localize in the main fixture | Medium | If refactoring, split GUID proof into `ValidateDeterministicGuidConditionCoverage()` | Current proof is broad but embedded in the main public-surface fixture. |
| Binary raw equality/prefix/suffix/contains/slice | Binary exact/pattern/slice rows | `AsBinary.EqualTo(...)`, `StartsWith(...)`, `EndsWith(...)`, `Contains(...)`, `Slice(...).EqualTo(...)` | native indexed execution, projection-backed execution, scan/residual fallback | `public-surface-api-sanity` binary bridge path prints `adoptedBinaryBridge ok`; binary reversed projection hardening runs before main fixture | Add a compact dedicated binary matrix helper if binary assertions become hard to localize | Medium | Preserve encoded-byte predicates; avoid per-row byte-array decoding where possible | Exact branches use ordinary key primitives; partial branches compare encoded key bytes. |
| Binary typed slices | F626 and typed binary-slice rows | `SlicedAsInt32(offset)`, explicit-encoding temporal slice factories, string/char/rune slice operators | scan/residual fallback | `public-surface-api-sanity` prints `adoptedBinaryTypedSliceBridge ok` and `binarySliceRemainderAndDateGrammar ok`; focused validation covers every temporal representation and UTC-equivalent DateTimeOffset semantics | Add dedicated row-shaped helper if future typed-slice failures are hard to diagnose | High | Keep exact expected-identity assertions for every new slice representation | Highest risk is mismatched typed interpretation, width, or endian behavior. |
| Null/empty/scalar-null key states | F395, null/missing rows | String/binary `IsNull`, `IsNotNull`, `IsEmpty`, `IsNotEmpty`, `IsNullOrEmpty`, `IsNotNullOrEmpty`; scalar `IsNull`, `IsNotNull`; opened-index `Exists()` / `Exists(condition)` | native indexed execution or routed composite execution; existence stops after the first tuple | `ValidateDeterministicStringConditionCoverage()` covers every string/binary state, scalar states, opened-index composition and existence, and typed composite aliases | Whitespace-only values remain ordinary values unless the caller maintains a normalized projection | Medium | Keep null versus empty and their complements as distinct assertions | Null is a stored route only where the index family supports it; an empty state is offered only to string/binary domains. `Exists()` includes special routes and ordinary tuples. |
| Composite `KeyPart(...)` named-part predicates | F380, F385, F395, F403, F404, F485, F612, F619, F638 | `CompositeWhere(...).KeyPart("tenantId").AsGuid.InSet(...)` | routed composite execution, scan/residual fallback inside composite executor | Public-surface keypart parity validations are recorded; main fixture prints `adoptedRoutedCompositeBridge ok` | Add dedicated matrix helper only if composite fixture becomes too implicit | Medium | Keep `KeyPart` by explicit part name; do not revive retired composite helper terminology | Named part intent is the canonical scoped composite condition surface. |
| Composite typed tuple-style parts | F380/F638 style typed composite rows | `CompositeIndex<T1,T2,TIdentity>(...).Where.Part1...` | routed composite execution | Typed composite index validation is recorded in `filter-dx-ai-remaining-work.md`; main fixture exercises typed tuple-style composite paths | Arity 4-8 only if first-release API commits to the ladder | Medium | Add arity only when real rows need it | Arity 2 and 3 prove the pattern; do not expand surface speculatively. |
| Composite `FullKey(...)` selected/excluded part matching | Full-key rows, cross-part search rows | `Where.FullKey().Parts(...).AsString.Contains(...)`, `Excluding(...)` | routed composite execution with residual full-key matching | Public-surface composite fixture covers full-key, delimiter, selected/excluded parts, typed full-key operands, pattern/contains | None for current read/filter corpus | Medium | Keep cross-part search explicitly under `FullKey(...)` | This avoids hidden flattening policy and keeps developer intent visible. |
| `WhereInverse` | inverse rows and self-relative key comparison rows | `catalog.IndexSet(...).WhereInverse...` | inverse execution | `ValidateIndexSetInverseConditionCoverage()` covers identity range, inverse key OR, inverse/forward AND, self-relative key compare, direct inverse reads, and target-owned `Delete`/`SetKeyUsing` | None for current read/filter or explicit-target mutation corpus | Medium | Keep shape validation plus identity and mutation result assertions | Inverse execution should remain a separate explicit surface; mutation requires explicit target scope. |
| External/runtime-source composition | external filter/source rows | `External(...)`, external identity source/filter composed with indexed conditions | external/runtime-source | Main public-surface fixture covers external filter/source, target entries, catalog external paths, correlated identities, external-source target `Delete`, and indexed-sibling target `SetKey` | None for current read/filter or explicit-target mutation corpus | Medium | Keep requirement that filter-only external conditions need an indexed sibling or source | Runtime predicates should not silently become full scans; caller-supplied identities are selectors, not proof of target tuple existence. |
| Cross-index composition, grouping, canonical `.Not` | grouped AND/OR/NOT rows | `.AND`, `.OR`, `.And.Group(...)`, `.And.Not.Group(...)`, completed-fragment composition | native indexed execution plus set composition | Main public-surface fixture covers composed retrieval; earlier grammar validation recorded canonical `.Not` and grouped composition; deterministic composition helper now proves structural `byte[]` identity equality across `AND`/`OR` | Add a compact grouped-fragment helper if future edits touch the condition tree | Medium | Preserve canonical `.Not`; direct negatives are aliases | Composition must stay identity-set based across indexes in the same group and must use LibraDex structural identity equality. |
| Terminal paging/count/exists/grouping/duplicates/representatives/cursors | result-shape rows | `ToList`, `Get`, `Iterate`, `Count`, `Exists`, `Groups(...).By(...)`, target cursors | terminal result shape | Main public-surface fixture covers count, exists, bookmark validation, grouping, binary identity grouping, target cursors, descending cursor slices | Representatives/top-N groups are terminal policy if not row-shaped in current fixture | Medium | Add terminal helper before changing terminal APIs | Terminal code should not alter condition predicate semantics. |
| Direct exact-key materialization | NL row 216 | `index.Identities.GetByKey(key)`, `.GetByKeys<TKey,TIdentity>(keys)`, `.CountByKey(s)`, `index.Entries.GetByKey(s)` | native indexed point execution | `ValidateIndexExistenceAndGuardCoverage()` proves typed/raw results, natural dictionary order, duplicate request normalization, missing/null/empty keys, structural binary lookup, unsupported-type rejection, and a 1,024-identity continuation run | None for current eager point contract | Low | Keep under `public-surface-api-sanity` | Multi-key calls return every distinct requested key, including misses. No direct reader variants: use a condition when delivery should stream. |
| Policy/corpus-note rows | normalization, security, ACL, business calendar, uniqueness, presentation rows | Explicit corpus notes, maintained projections, external policy | policy/corpus note | `filter-dx-natural-language-corpus.csv` has no missing syntax or note | No executable condition proof by design | Low | Keep notes explicit; do not fill with misleading fluent syntax | These are not condition-builder gaps unless storage/projection policy is chosen. |
| Selective delete/mutate dependencies | delete/rekey/update rows | `catalog[group][target].Delete(condition)`, `.DeleteAll()`, `.SetKey(...)`, `.SetKeyUsing(...)` | consumes proven read/filter pipeline, then exact target-tuple mutation | Public-surface sanity covers scalar/null/empty, batch, GUID/binary/widened identity, string projections, composite, composed, inverse, external, and residual/pattern target mutation; grouped-result sanity proves aggregate winners are unchanged by `Return` versus `ReturnKeys` | None for current mutation contract | High | Keep future additions row-shaped and contract-driven | Do not create a parallel selector. Mutation consumes the same selected identities while ignoring only return materialization. |

## Read/Filter Proof Status

- Green for current syntax families where the condition can execute over existing indexes: scalar, bool, enum-as-scalar, bitmask, string, regex, structured date/time, GUID, binary, typed binary slices, null/empty, composite named parts, typed composite parts, full-key composite predicates, inverse, external/runtime-source, cross-index composition, and core terminal result shapes.
- Remaining read/filter risk is not missing syntax; it is proof locality. GUID, binary, typed binary, and composite checks are currently embedded in the large public-surface fixture. If those areas churn, split them into small deterministic helpers rather than expanding the monolithic fixture further.

## Mutation Gate

Selective delete/mutate should stay mapped to the read execution class it consumes:

1. Native single primitive leaf: direct primitive mutator can operate on the same normalized request. Current public-surface sanity covers scalar delete, scalar `SetKey`, and scalar `SetKeyUsing`.
2. Projection-backed leaf: mutation target must be explicit; projection indexes are read accelerators, not necessarily the mutation target. Current public-surface sanity covers string delete/set-key with exact/folded/sort-key/reversed projection cleanup.
3. Scan/residual fallback: mutation needs exact target-tuple capture from the selected identity stream. Current public-surface sanity covers string residual mutation plus GUID and binary pattern selectors through target-owned methods.
4. Composite leaf: mutation target must distinguish deleting/updating the composite tuple from deleting/updating another index by selected identities. Current public-surface sanity covers composite delete/set-key.
5. Targeted composed mutation: the target is the named index receiving `Delete`, `SetKey`, or `SetKeyUsing`; the condition carries no mutation verb.
6. Inverse execution: mutation requires explicit target index scope and is covered by target-owned `Delete` and `SetKeyUsing`.
7. External/runtime-source: mutation requires explicit target index scope and avoids treating caller-supplied identities as proof of tuple existence.
8. Typed aggregate result shape: mutation honors filter/group/aggregate winners and ignores only `Return`/`ReturnKeys` materialization.

## First Commands

Preferred build shape:

```powershell
$log = 'artifacts\build-harness-release-x64-condition-execution-coverage-20260611.log'
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

Then run the current public-surface sanity:

```powershell
dotnet run --project LibraDex.Harness\LibraDex.Harness.csproj -c Release -- public-surface-api-sanity --path artifacts\public-surface-api-sanity-condition-execution-coverage.lbdx
```

## Restart Prompt

Historical prompt; superseded by [`condition-builder-continuation-restart.md`](condition-builder-continuation-restart.md):

> Start from `docs/condition-builder/condition-execution-coverage.md` and `LIBRADEX_DESIGN_CHECKLIST.md` section 0. The read/filter execution matrix is built and mutation is target-owned through `catalog[group][target].Delete(condition)`, `DeleteAll()`, `SetKey(condition, newKey)`, and `SetKeyUsing(condition, oldKey => newKey)`. Public-surface sanity covers scalar/null/empty, projection, composite, composed, inverse, external, GUID-pattern, and binary-pattern mutation; grouped-result sanity proves that `Return` and `ReturnKeys` do not change aggregate mutation winners. Completed conditions expose no mutation terminals.
