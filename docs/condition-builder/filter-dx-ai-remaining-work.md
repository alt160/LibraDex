# Filter DX AI Remaining Work

> This file tracks the older 671-row filter-DX syntax corpus. It is not the current 219-row intuitiveness worklist. For current continuation, accepted condition-builder semantics, validation evidence, and the new-conversation prompt, start with [`condition-builder-continuation-restart.md`](condition-builder-continuation-restart.md).

Restart point for the AI-authored review of `filter-dx-natural-language-corpus.csv`.

The active corpus was cleared of deterministic generator output and refilled from the natural-language rows by AI review. Rows scored `5` have proposed LibraDex and Abraxas syntax or an explicit corpus note explaining why the natural-language request is policy/presentation rather than condition syntax. There are currently no score-`0` rows.

Current score split:

- Score `5`: 671 rows
- Score `0`: 0 rows

Do not use `rebuild-filter-dx-corpus.ps1` to regenerate syntax. It now fails fast because deterministic NL-to-builder generation is deprecated for this corpus. Future work should update rows explicitly after reviewing the natural-language intent against the current condition-builder API.

## Restart Now

Current state:

- Corpus syntax fill is complete: 671 rows, all score `5`, with no missing LibraDex or Abraxas syntax.
- The deterministic generator path is intentionally closed. `rebuild-filter-dx-corpus.ps1` should not be repaired or reused for NL-to-builder conversion.
- The latest targeted fix was `F626`: `SlicedAsDateTimeOffset(offset)` now accepts `DateTimeOffset` operands and compares decoded `DateTimeOffset` values directly, while `SlicedAsDateTime(offset)` remains the explicit `DateTime` tick-slice form.
- Latest validation for that fix passed with `artifacts\build-harness-release-x64-datetimeoffset-slice-20260611.log` and `public-surface-api-sanity --path artifacts\public-surface-api-sanity-datetimeoffset-slice.lbdx`.

Next task:

Build a condition execution coverage matrix before adding more builder syntax or starting selective delete/mutate. The matrix should classify each operator family as one of:

- `native indexed execution`
- `projection-backed execution`
- `scan/residual fallback`
- `external/runtime-source`
- `inverse execution`
- `terminal result shape`
- `policy/corpus note`

Use the matrix to identify missing executable proof, then add focused public-surface harness assertions for the highest-risk read/filter paths. Only start selective delete/mutate once the result-producing condition pipeline is proven for the families it will consume.

## Recommended Order

1. String normalization / parsing / regex / tokenization
2. Null / missing / presence / sorting policy
3. External policy / ACL / security data
4. Calendar / timezone / fiscal / business-time
5. Cross-field / computed / interval logic
6. Terminal / result-shape / aggregation only
7. Domain constraints / transition / uniqueness / rekey semantics

Composite API gaps / OR expansion is complete. The latest redo reviewed the remaining open rows against the current builder plus `WhereInverse`, and the follow-up descending-cursor slice closed the last retrieval ordering gap. The final four policy/presentation rows are now closed with explicit corpus notes instead of blank syntax.

## Group 1: Composite API Gaps / OR Expansion Needed

Original rows: `F380, F385, F395, F403, F404, F485, F612, F619, F638`

Cleared in current slice: `F380, F385, F395, F403, F404, F485, F612, F619, F638`

Still open: none

Problem summary:

These rows are about composite keys, but the current composite `KeyPart(...)` surface is narrower than the general index surface. The AI pass originally found plausible developer intent, then validation against the inspected code showed several missing composite-part operators:

- part-level set membership such as `KeyPart("tenantId").AsGuid.InSet(...)`
- part-level string or scalar negation / not-equal patterns
- part-level binary operators such as binary prefix checks

Why it matters:

Developers can express the same idea against ordinary indexes, but the composite grammar currently forces them either to generate grouped OR expansions manually, add projections, or leave the row unsupported. That is real DX friction because composite keys are exactly where developers expect named parts to behave like normal indexed fields.

Possible changes:

- Done in current Group 1 slice: opened `.Where.KeyPart(...).AsString` and the named scalar projections (`.AsInt32`, `.AsGuid`, and peers) now expose part-scoped `NotEqualTo`, `NotBetween`, `InSet` / `In` / `IsIn`, and `NotInSet` / `NotIn` / `IsNotIn`, with routed composite executor support.
- Done in current Group 1 slice: opened `.Where.KeyPart(...).AsGuid` now exposes part-scoped `InSet` / `In` / `IsIn` and `NotInSet` / `NotIn` / `IsNotIn`, with routed composite executor support.
- Done in current Group 1 slice: composite `.KeyPart(...).AsBinary` and `C.Binary(...)` now support byte-domain `EqualTo`, `NotEqualTo`, `StartsWith`, `EndsWith`, and `Contains`, including durable composite snapshot/node encoding for byte-array part values.
- Done in current Group 1 slice: composite `.KeyPart(...).AsDateTime` now supports DateTime full-value comparison operators `EqualTo`, `NotEqualTo`, `GreaterThan`, `GreaterOrEqual`, `LessThan`, `LessOrEqual`, `Between`, and `NotBetween`.
- Done in current Group 1 slice: descriptor-level predicate selection now owns the implementation through `.Where(...)`; legacy `.Index(...)` forwards into `.Where(...)` instead of being the behavior-owning path.
- Done in current Group 1 slice: catalog group `Index(indexName)`, `Index<TKey, TIdentity>(indexName)`, and `Where<TKey, TIdentity>(indexName)` are available for grouped open/typed-open/typed-condition paths, with metadata validation on the typed forms.
- Done in current Group 1 slice: catalog group `CompositeWhere(indexName)` now selects a composite index by name and returns composite-specific IntelliSense after validating that the named index is composite.
- Done in current Group 1 slice: catalog group `CompositeIndex<TPart1, TPart2, TIdentity>(name)` and `CompositeIndex<TPart1, TPart2, TPart3, TIdentity>(name)` now open typed tuple-style composite handles with `Where.Part1`, `Where.Part2`, and `Where.Part3` selectors after validating persisted part and identity types.
- Done in current Group 1 slice: catalog root `IndexSet(name)` is the canonical index-set selector, so fluent examples can use `catalog.IndexSet("users").Where("firstName")...`, `catalog.IndexSet("users").Index<TKey, TIdentity>("age")`, and `catalog.IndexSet("users").CompositeIndex<...>("tenantUser")...`.
- Done in current Group 1 slice: catalog index-set reusable condition fragments now use logical `Group(fragment)` because the index-set object exposes its set name as `Name`, leaving `Group(...)` to mean parenthesized condition composition.
- Done in current Group 1 slice: catalog root `IndexSets` now lists every non-empty index set as `CatalogIndexSetInfo` with the set `Name` and its `CatalogIndexInfo[] Indexes`; `catalog.Indexes.IndexSetNames()` provides the lightweight name list.
- Done in current Group 1 slice: `filter-dx-natural-language-corpus.csv` was rewritten away from `LibraDexCondition.ForGroup(...)` and descriptor `.Index(...)` predicate syntax; score-5 rows now use catalog-backed `IndexSet(...).Where(...)`, `CompositeWhere(...)`, logical `Group(...)`, and catalog-backed `External(...)` forms where applicable.
- Done in current Group 1 slice: composite key creation and condition execution now preserve explicit null/empty part semantics. String and binary parts accept `NullKey.Null` as a distinct null route and `NullKey.Empty` as the concrete empty route; scalar, GUID, and date-like parts accept `ScalarNull.Null` as a distinct null route. Named `KeyPart(...)` and typed tuple-style `Where.PartN` predicates now expose the matching `NullKey` / `ScalarNull` overloads, clearing `F395`.
- Done in current Group 1 slice: `F638` closed without adding a new current-month helper. The corpus already allows caller-provided current date context, so the row now uses composite date part `YearMonth(currentYear, currentMonth)` plus shard `InSet(...)`.
- Add composite part date membership only if a future row needs true date set membership rather than date ranges, `YearMonth(...)`, or grouped OR expansion.
- Extend typed tuple-style composite handles to arities 4 through 8 if the first-release API commits to the full tuple-like ladder; the implemented arity-2 and arity-3 forms prove the pattern and cover the current Group 1 examples.
- Add broader composite `.Not.KeyPart(...)` grammar only if direct negative aliases are not enough for the corpus and the execution semantics remain bounded.
- Add binary composite-part support if binary key parts are intended to be first-class.
- If the API should stay smaller, document/generated examples should use explicit grouped OR expansion for set membership.

Useful rows:

- Group 1 is complete. `F638` deliberately relies on caller-provided `currentYear` / `currentMonth` context instead of adding API surface for current-month clock policy.

Validation checkpoint:

- `LibraDex.csproj` Release/x64 build passed with `MSBuild.exe`.
- `LibraDex.Harness.csproj` Release/x64 build passed with `MSBuild.exe`.
- `dotnet run --project LibraDex.Harness\LibraDex.Harness.csproj -c Release -- public-surface-api-sanity --path artifacts\public-surface-api-sanity-keypart-parity.lbdx` passed and now covers opened `.Where.KeyPart(...)` string membership, string membership exclusion, scalar outside-range, and scalar membership.
- `dotnet run --project LibraDex.Harness\LibraDex.Harness.csproj -c Release -- public-surface-api-sanity --path artifacts\public-surface-api-sanity-keypart-parity-binary-guid-date.lbdx` passed and now covers opened `.Where.KeyPart(...)` Guid membership, Guid membership exclusion, DateTime full-value comparison, and binary prefix matching.
- `dotnet run --project LibraDex.Harness\LibraDex.Harness.csproj -c Release -- public-surface-api-sanity --path artifacts\public-surface-api-sanity-typed-composite-index.lbdx` passed and now covers `CompositeIndex<Guid, string, long>("tenantUser").Where.Part1.InSet(...).And.Part2.StartsWith(...)`.
- `LibraDex.Harness.csproj` Release/x64 rebuild passed with `MSBuild.exe` and log `artifacts\build-harness-release-x64-group-where-canonical-20260610.log`.
- `dotnet run --project LibraDex.Harness\LibraDex.Harness.csproj -c Release -- public-surface-api-sanity --path artifacts\public-surface-api-sanity-group-where-canonical.lbdx` passed and now covers grouped `Index(...)`, grouped typed `Index<TKey, TIdentity>(...)`, and grouped typed `Where<TKey, TIdentity>(...)`.
- `LibraDex.Harness.csproj` Release/x64 rebuild passed with `MSBuild.exe` and log `artifacts\build-harness-release-x64-indexset-canonical-20260610.log`.
- `dotnet run --project LibraDex.Harness\LibraDex.Harness.csproj -c Release -- public-surface-api-sanity --path artifacts\public-surface-api-sanity-indexset-canonical.lbdx` passed and now covers `catalog.IndexSet(...)`, `catalog.IndexSets`, `catalog.Indexes.IndexSetNames()`, grouped typed open/where paths, and typed composite index open.
- `$log = 'artifacts\build-harness-release-x64-composite-null-parts-20260610.log'` + MSBuild rebuild of `LibraDex.Harness\LibraDex.Harness.csproj` passed after composite null/empty part semantics.
- `dotnet run --project LibraDex.Harness\LibraDex.Harness.csproj -c Release -- public-surface-api-sanity --path artifacts\public-surface-api-sanity-composite-null-parts.lbdx` passed and covers persisted composite null supplier / null SKU / empty SKU routes before and after reopen.
- CSV consistency check after the latest review slice: 671 total rows, 671 score-5 rows, 0 score-0 rows, 0 score-5 rows missing LibraDex syntax, and 0 score-5 rows missing Abraxas syntax.
- Validation after the descending-cursor slice: `LibraDex.Harness.csproj` Release/x64 rebuild passed with `artifacts\build-harness-release-x64-descending-cursors-20260611-rerun.log`; `dotnet run --project LibraDex.Harness\LibraDex.Harness.csproj -c Release -- public-surface-api-sanity --path artifacts\public-surface-api-sanity-descending-cursors-rerun.lbdx` passed and now covers `IndexOptions.SortOrder`, `OpenRangeReader(..., QueryDirection.Descending)`, numeric condition target cursors, catalog index-set target cursors, and string target cursors with `direction: QueryDirection.Descending`.
- Validation after the native descending stream slice: `LibraDex.Harness.csproj` Release/x64 rebuild passed with `artifacts\build-harness-release-x64-descending-native-perf-20260611.log`; `dotnet run --project LibraDex.Harness\LibraDex.Harness.csproj -c Release -- public-surface-api-sanity --path artifacts\public-surface-api-sanity-descending-native-perf.lbdx` passed and now covers native descending range traversal for all fixed generic shapes (`SS8-8`, `SS16-8`, `SS8-16`, `SS16-16`, `FS32-8`, `FS32-16`). Compact perf pass `dotnet run --project LibraDex.Harness\LibraDex.Harness.csproj -c Release -- fixed-reader-perf --directory artifacts\fixed-reader-perf-descending-native --items 2048 --range-count 512 --iterations 40 --repeat-count 2` passed and writes `artifacts\fixed-reader-perf-descending-native\artifacts\fixed-reader-perf-current.md` with the new `Desc tuple ns/id` column.
- Validation after the lazy descending route slice: `LibraDex.Harness.csproj` Release/x64 rebuild passed with `artifacts\build-harness-release-x64-descending-lazy-final-20260611.log`; `dotnet run --project LibraDex.Harness\LibraDex.Harness.csproj -c Release -- public-surface-api-sanity --path artifacts\public-surface-api-sanity-descending-lazy.lbdx` passed. Descending fixed-shape range readers now push high-side route targets first and move backward within shelf-local slot ranges, so first-row descending reads no longer require loading all matching shelf ranges up front. Compact perf pass `dotnet run --project LibraDex.Harness\LibraDex.Harness.csproj -c Release -- fixed-reader-perf --directory artifacts\fixed-reader-perf-descending-lazy --items 2048 --range-count 512 --iterations 40 --repeat-count 2` passed and writes `artifacts\fixed-reader-perf-descending-lazy\artifacts\fixed-reader-perf-current.md`.
- Validation after the fused descending tuple slice: `LibraDex.Harness.csproj` Release/x64 rebuild passed with `artifacts\build-harness-release-x64-descending-fused-20260611.log`; `dotnet run --project LibraDex.Harness\LibraDex.Harness.csproj -c Release -- public-surface-api-sanity --path artifacts\public-surface-api-sanity-descending-fused.lbdx` passed. Compact perf pass `dotnet run --project LibraDex.Harness\LibraDex.Harness.csproj -c Release -- fixed-reader-perf --directory artifacts\fixed-reader-perf-descending-fused --items 2048 --range-count 512 --iterations 40 --repeat-count 2` passed and writes `artifacts\fixed-reader-perf-descending-fused\artifacts\fixed-reader-perf-current.md`; `Desc tuple ns/id` improved versus the lazy-only pass for `SS8-8`, `SS16-8`, `SS8-16`, `FS32-8`, and was mixed for the wider identity shapes.
- Validation after the F626 DateTimeOffset slice fix: `SlicedAsDateTimeOffset(offset)` now returns a `LibraDexBinaryTypedSliceConditionOperator<DateTimeOffset>` and the residual binary-slice predicate compares decoded `DateTimeOffset` values directly; `SlicedAsDateTime(offset)` remains the explicit DateTime tick slice. `LibraDex.Harness.csproj` Release/x64 rebuild passed with `artifacts\build-harness-release-x64-datetimeoffset-slice-20260611.log`; `dotnet run --project LibraDex.Harness\LibraDex.Harness.csproj -c Release -- public-surface-api-sanity --path artifacts\public-surface-api-sanity-datetimeoffset-slice.lbdx` passed.

## Group 2: String Normalization / Parsing / Regex / Tokenization

Original rows: `F020, F022, F053, F060, F063, F070, F291, F441, F487, F493, F509, F510, F511, F551, F552, F555, F556, F557, F630, F640`

Cleared in current Group 2 slice: `F020, F022, F053, F060, F063, F070, F291, F441, F487, F493, F509, F510, F511, F551, F552, F555, F556, F557, F630, F640`

Still open: none in this group.

Problem summary:

These rows need transformed text, parsed text, or tokenized text before the condition can be correct. A raw string predicate would be misleading because it would only compare the stored key as-is.

Examples:

- normalized display name equals a value
- email domain equals a value
- starts with a numeric character
- contains quoted text
- contains a date-looking segment
- contains a correlation id string
- whitespace-only or collapsed-whitespace comparison
- folded accents/case comparison
- every/any token from a search phrase
- custom-encoded payload text contains a marker
- normalized address contains street number and postal code

Why it matters:

This group is mostly not a missing basic string API. It is about whether LibraDex should offer first-class projection conventions or predicate helpers for common developer intent. Without that, examples should be honest and say: index a normalized/parsed/tokenized projection, or use `External(...)` when the logic is caller-owned.

Possible changes:

- Add documented projection conventions such as `normalizedDisplayName`, `emailDomain`, `foldedDisplayName`, `searchToken`, `decodedPayloadText`, `normalizedAddress`.
- Add string regex-existence predicates if those are intended to materialize through maintained projections or scan bridges.
- Add examples for `.External(...)` when the predicate is correct but not index-native.
- Keep rows score `0` if no projection/external assumption is allowed.
- Done in current Group 2 slice: `F020` closed as folded/no-case `displayName` equality, with a note that firstName/lastName conjunction is also a valid app model when the normalized display value is component-derived.
- Done in current Group 2 slice: `F022` closed as case-insensitive `emailAddress.EndsWith("@example.com")`; the `@` boundary avoids matching arbitrary suffix text like `notexample.com`, while notes still recommend an `emailDomain` projection if the stored value is not a single parsed email address.
- Done in current Group 2 slice: string wildcard semantics moved to `.Like(...)` / `.NotLike(...)`; `.Matches(...)` now means boolean regex existence, with `Regex` instance overloads on boolean/capture/set regex operators.
- Done in current Group 2 slice: `F053`, `F060`, `F063`, and `F070` closed with boolean regex syntax. These remain scan-backed unless the regex exposes an anchored literal prefix that can narrow the exact-index scan, or a later maintained projection handles the shape directly.
- Implementation note: anchored literal regex prefixes such as `^api` now produce exact-index candidate ranges before residual regex evaluation. Suffix-only regex optimization is not generalized yet because a reversed-projection route also needs residual regex intersection for mixed patterns; pure suffix intent should still use `.EndsWith(...)` when the suffix is known.
- Done in current Group 2 slice: `F291` closed by reading email-domain intent as a human request over a normal single SMTP address, using `emailAddress.EndsWith("@example.com", ignoreCase: true)` plus status.
- Done in current Group 2 slice: `F441` closed as OR over exact `email` and maintained `normalizedPhone`; the note keeps the projection requirement visible for the normalized phone half.
- Done in current Group 2 slice: `F487` closed with `External(id => EmailNormalizationChanged(id))`, because migration/self-relative normalization checks are dev-owned logic unless a stale-projection flag is maintained.
- Closed in the inverse-aware redo: `F493` is expressible as a maintained projection-version comparison, such as `foldedTextProjectionVersion < currentFoldedProjectionVersion`; the note keeps the maintenance assumption visible.
- Done in current Group 2 slice: `F510` closed as dev-owned whitespace-collapse normalization via `External(...)`, or as an indexed equality when the app maintains a `collapsedWhitespaceText` projection.
- Done in current Group 2 slice: `F511` closed as equality over a maintained/comparable `normalizedPhone` key after the dev normalizes the caller-supplied phone value; inconsistent raw phone formatting is an app modeling responsibility.
- Done in current Group 2 slice: `F509` closed with boolean regex whitespace-only matching.
- Done in current Group 2 slice: `F551` and `F552` closed as equality over app-maintained comparable projections (`trimmedDisplayName`, `foldedDisplayName`) after dev-side criteria normalization.
- Done in current Group 2 slice: `F555`, `F556`, and `F557` closed by treating tokenization as app-owned preprocessing/modeling. Any-token uses `searchToken.InSet(...)`; every-token and blocked-token cases use either modeled token-index expansion or explicit `External(...)`/canonical `Not`.
- Done in current Group 2 slice: `F630` and `F640` closed as ordinary predicates over app-maintained decoded/normalized projections.

Useful rows:

- Regex/shape detection: completed for `F053, F060, F063, F070`
- Normalization/folding: completed for `F509, F510, F511, F551, F552, F640`
- Parsed domain/phone/address: completed for `F291, F441, F487`; `F493` moved to projection maintenance/integrity
- Token search: completed for `F555, F556, F557`
- Decoded binary text: completed for `F630`

## Group 3: Null / Missing / Presence / Sorting Policy

Rows: `F447, F494, F506, F507, F584, F585, F586, F589, F590`

Cleared in current Group 3 slice: `F447, F494, F506, F507, F584, F585, F586, F589, F590`

Still open: none

Problem summary:

These rows use words like missing, absent, blank, grouping, or sorting in ways that are not always the same as a stored null key. LibraDex has null and empty key states, but some rows mean no index tuple exists, a projection is missing, or missing values should participate in sorting/grouping in a special way.

Why it matters:

Conflating stored null with absent tuple would make the examples wrong. A true absent tuple cannot be found by searching the missing key because no key row exists. Sorting/grouping treatment is also result behavior or policy, not just condition syntax.

Possible changes:

- Define durable terminology in the corpus:
  - stored null key
  - stored empty key
  - null-or-empty key bucket
  - scalar null route
  - absent tuple
  - missing projection
  - normalized/domain blank
- Add presence-index examples where absence is the intended query.
- Document sort/grouping policy rows as corpus notes when the request is useful DX guidance but not a condition-builder predicate.

Useful rows:

- Presence/absence: completed for `F447`; `F584` was rephrased from absent tuple to stored null key and completed; `F494` closed in the inverse-aware redo as a maintained `hasReversedSuffixProjection` boolean or equivalent maintenance projection.
- Sort/range/group policy: `F506, F507, F585, F586` are closed with explicit corpus notes because they are policy/presentation rows rather than condition-builder gaps. `F506` and `F507` describe existing null/missing route and range behavior; `F585` is sort-last presentation or a maintained sort projection; `F586` is grouping relabeling/modeling.
- Blank semantics beyond null/empty: completed for `F589, F590`.

Done in current Group 3 slice:

- `F447` closed by reading the NL request as a normal permissions model: `public=false` on one index plus no explicit ACL tuple on an ACL-entry-kind index in the same identity universe. This avoids the earlier over-literal absent-tuple interpretation.
- `F494` closed as projection maintenance. If the app maintains `hasReversedSuffixProjection`, bool equality is enough; otherwise maintenance code should enumerate/repair the reversed projection's null/empty or absent entries.
- `F506` and `F507` closed as documented policy rows. LibraDex already sorts null/missing key-state routes before real values, and real-value range bounds do not require a new predicate just to exclude null/empty routes.
- `F584` rephrased from "missing index row" to "stored as a null key" and closed with `NullKey.Null`. A truly absent tuple still requires a presence index or maintenance check.
- `F585` closed as a documented result-ordering row; sort-last treatment is result presentation or a maintained sort projection, not condition syntax.
- `F586` closed as a documented grouping-policy row; `LibraDexConditionGroupQuery` builds groups by iterating the grouping index tuples and uses the raw tuple key as the group key. Stored null/empty values can therefore group distinctly, but relabeling them as `Unknown` or inventing an absent-tuple group is caller/model policy.
- `F589` closed as boolean regex whitespace-only matching, with the note that "treat as missing" is caller normalization policy.
- `F590` closed as `NullKey.NullOrEmpty OR Matches(@"^\s+$")`, with broader blank normalization left to caller-owned rules.

## Group 4: External Policy / ACL / Security Data

Rows: `F159, F445, F550, F580, F601, F602, F604, F609, F610`

Cleared in current Group 4 slice: `F159, F445, F550, F580, F601, F602, F604, F609, F610`

Still open: none in this group.

Problem summary:

These rows need information that is not a single stored key on the target record. Examples include inherited permissions, ACL role checks, row-level security, owner/admin access policy, team ACL membership, percentile thresholds, or effective masks computed from multiple rows.

Why it matters:

The condition builder can compose `External(...)` predicates and runtime indexes, but it should not pretend that security policy is a simple field comparison unless the application maintains a dedicated projection.

Possible changes:

- Add canonical `.External(...)` examples for policy checks after indexed narrowing.
- Add maintained projection examples such as `effectivePermission`, `allowedPrincipal`, `securityScope`, or `percentileBucket`.
- Decide whether security-policy rows belong in this condition corpus or a separate access-policy corpus.

Useful rows:

- Permissions/masks: completed for `F602, F609`
- ACL/security visibility: completed for `F445, F550, F601, F604, F610`
- External threshold: completed for `F580`

Done in current Group 4 slice:

- `F159` closed with the new IndexSet inverse key-map surface. A caller can enable the inverse map for the permissions index set, then write `catalog.IndexSet("permissions").WhereInverse.AsUInt64.Between(firstPermissionId, lastPermissionId).Key("effectiveMask").AsUInt64.NotEqualToKey("inheritedMask").EndCondition`. This keeps the query inside LibraDex indexed key data when `effectiveMask` and `inheritedMask` are included in the inverse map, avoiding object hydration and avoiding a per-result scan of another forward index.
- `F445` closed as a normal indexed classification plus owner/ACL condition fragment.
- `F550`, `F601`, `F602`, `F609`, and `F610` closed with `WhereInverse.InSet(...).Key(...)` forms where the caller already has a candidate identity list from ACL/security context and wants to filter those identities by inverse-stored keys.
- `F580` closed as a caller-computed threshold or maintained percentile projection before entering the builder.
- `F604` closed as indexed owner equality OR an explicit `External(...)` admin/security predicate.

## Group 5: Calendar / Timezone / Fiscal / Business-Time

Rows: `F227, F228, F229, F230, F591, F592, F593, F594, F595, F596, F597, F598, F599`

Cleared in current Group 5 slice: `F227, F228, F229, F230, F591, F592, F593, F594, F595, F596, F597, F598, F599`

Still open: none in this group.

Problem summary:

These rows require calendar rules that are outside ordinary UTC/date component comparison. Examples include local business days, fiscal years, ISO weeks, support hours, daylight-saving boundaries, tenant-local time zones, and next-business-day logic.

Why it matters:

The builder has useful date helpers, but these rows need a calendar policy. A raw UTC range may be wrong for tenant-local time, daylight-saving transitions, fiscal calendars, or business-hour windows.

Possible changes:

- Add projection conventions such as `tenantLocalDate`, `businessDayKey`, `fiscalYear`, `fiscalQuarter`, `isoWeek`, `supportHoursBucket`.
- Add examples where callers compute date bounds externally and pass them into existing range operators.
- Add `.External(...)` examples when policy is not stored as an index.

Useful rows:

- Local/business day: `F227, F591, F599`
- Business/support hours: `F228, F229, F595, F596`
- Fiscal/ISO calendar: `F592, F593, F594`
- Timezone/DST: `F597, F598`
- Maintenance/blackout windows: `F230`

Done in current Group 5 slice:

- These rows closed as ordinary predicates over caller-maintained calendar projections, such as `tenantLocalDate`, `businessHourBucket`, `fiscalQuarter`, `fiscalYear`, `isoWeek`, `localTimeOfDay`, `supportHoursBucket`, `dstTransitionBucket`, `tenantLocalTimestamp`, and `businessDueDate`.
- No new clock/calendar helper was added. The app owns tenant timezone, business calendar, fiscal calendar, DST, and next-business-day policy, then passes the derived key or bound into the existing builder.

## Group 6: Cross-Field / Computed / Interval Logic

Rows: `F083, F098, F118, F186, F207, F225, F244, F249, F270, F271, F272, F276, F306, F448, F486, F488, F547, F571, F572, F573, F574, F575, F576, F600, F632, F636`

Cleared in current Group 6 slice: `F083, F098, F118, F186, F207, F225, F244, F249, F270, F271, F272, F276, F306, F448, F486, F488, F547, F571, F572, F573, F574, F575, F576, F600, F632, F636`

Still open: none in this group.

Problem summary:

These rows compare two fields, compare old/new mutation state, compute a value, or test interval overlap. Examples include failed login count less than successful count, paid amount greater than invoice amount, end date before start date, content hash equals checksum, blackout window overlap, and currency conversion.

Why it matters:

An index condition normally compares one indexed key to a constant, runtime value, set, or range. Cross-field and computed logic requires either a maintained projection or a scan/external predicate. Adding generic cross-field syntax may imply scan-backed behavior and could conflict with the low/no-friction performance mission if not very explicit.

Possible changes:

- Prefer maintained projections for common cases:
  - `isOverpaid`
  - `hasInvalidDateRange`
  - `hashMatchesChecksum`
  - `convertedAmountMinorUnits`
  - `overlapsBlackoutWindow`
- Use `.External(...)` after selective indexed predicates for rare or caller-owned checks.
- Consider a separate corpus section for projection-backed derived predicates.
- Done in current Group 6 slice: cross-field, computed, before/after migration, and self-relative date/amount/hash rows are closed with `External(...)` or with the standard indexed interval-overlap form for `F632`.
- Implementation note: these are not single index-native predicates. The intended developer workflow is indexed narrowing first, then `External(...)` or a maintained derived projection for the self-relative/computed predicate.

Useful rows:

- Simple cross-field numeric/date comparisons: completed for `F083, F098, F118, F186, F207, F225, F244, F249, F571, F572, F573, F574, F576`
- Hash/checksum/length computations: completed for `F270, F271, F272, F276`
- Ordered lifecycle date chains: completed for `F306, F448`
- Rekey/mutation before-after: completed for `F486, F488`
- Current-row relative comparisons: completed for `F547`
- Interval/range overlap or conversion: completed for `F600, F632, F636`
- Computed balance: completed for `F575`

## Group 7: Terminal / Result-Shape / Aggregation Only

Rows: `F462, F463, F464, F465, F466, F467, F468, F469, F470, F471, F472, F473, F474, F475, F476, F563`

Cleared in current Group 7 slice: `F462, F463, F464, F465, F466, F467, F468, F469, F470, F471, F472, F473, F474, F475, F476, F563`

Still open: none in this group.

Problem summary:

These rows are about what to do with matching records, not how to express a condition. They cover paging, bookmarks, existence checks, counts, grouping, top-N ranking, materialization shape, ordering, duplicate handling, and cardinality rules.

Why it matters:

Keeping these in the condition-builder corpus makes the score look worse for reasons unrelated to condition syntax. They are valid DX topics, but they belong in a retrieval/result/aggregation corpus or in terminal API examples.

Possible changes:

- Move these rows to a separate retrieval/result DX corpus.
- Add a `scope` column if they stay in the CSV.
- Keep condition syntax blank and score `0` when the requested terminal does not exist or when the row is purely policy/presentation.

Useful rows:

- Paging/existence/count: completed for `F462, F463, F464, F465`
- Grouping/aggregation/ranking: completed for `F466, F467, F468, F469, F470`
- Materialization/order/duplicates: completed for `F471, F472, F473, F474, F475, F476`
- Cardinality over memberships: completed for `F563`

Done in current Group 7 slice:

- `F462` through `F465` closed with `ToList(..., take/bookmark)`, `Exists(...)`, and `Count(...)`.
- `F466` through `F470` closed with the `Groups(...).By(...).Counts()`, duplicate, representative, and top-count terminal APIs.
- `F471`, `F472`, `F473`, `F475`, and `F476` closed with materialization, target-index cursor, take-one, and deduplication terminal forms.
- `F563` closed with indexed narrowing plus `External(...)` cardinality where the exact "only one selected category" set logic is caller-owned.
- `F474` closed after the descending-cursor slice. Target-index cursors now accept `direction: QueryDirection.Descending`, and `IndexOptions.SortOrder` records descending creation intent on the low-friction create path.

## Group 8: Domain Constraints / Transition / Uniqueness / Rekey Semantics

Rows: `F147, F489, F490, F569, F570`

Cleared in current Group 8 slice: `F147, F489, F490, F569, F570`

Still open: none in this group.

Problem summary:

These rows describe transitions, uniqueness constraints, or rekey lifecycle behavior rather than a static read condition. Examples include status changed from Pending to Active, order number corrected, composite key part changed, username reserved only for system tenant, and username unique within a tenant composite key.

Why it matters:

These are real developer needs, but they are not ordinary condition-builder expressions. They need lifecycle hooks, constraint validation, migration/rekey APIs, or unique-index semantics.

Possible changes:

- Move to a lifecycle/rekey/constraint DX corpus.
- Add examples for rekey APIs or uniqueness checks if those APIs exist.
- Do not force them into condition-builder syntax unless the row is rewritten as a static query.

Useful rows:

- Transition/history: completed for `F147`
- Rekey/mutation: completed for `F489, F490`
- Reserved/unique constraints: completed for `F569, F570`

Done in current Group 8 slice:

- `F147`, `F489`, and `F490` closed as static read projections over maintained transition/rekey facts rather than lifecycle APIs.
- `F569` closed as composite key-part criteria over username plus tenant exclusion.
- `F570` closed through grouping/duplicate metadata over the tenant+username composite index.

## Next Restart Step

This historical restart step is superseded by [`condition-builder-continuation-restart.md`](condition-builder-continuation-restart.md).

No rows remain score `0` in `docs/condition-builder/filter-dx-natural-language-corpus.csv`.

Suggested prompt:

> Start from `docs/condition-builder/condition-execution-coverage.md` and build the condition execution coverage matrix for the current condition-builder surface. Classify each operator family as native indexed, projection-backed, scan/residual, external/runtime-source, inverse, terminal, or policy note; then recommend the first focused public-surface harness proofs before selective delete/mutate work.
