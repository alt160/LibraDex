# LibraDex Filter DX Complaints

This checklist tracks developer-friction complaints found while reviewing `artifacts/filter-dx-natural-language-corpus.csv`.

## Open

No active complaints are currently listed.

## Resolved

1. [x] Regenerate LibraDex corpus examples and scores after the condition-builder changes.

   Resolution:

   - Recreated stale LibraDex examples around `.AsBoolean`, `NullKey`, `ScalarNull`, binary null/empty key state, case/culture string options, and index-first catalog syntax.
   - Rescored rows against current API support: direct boolean/null examples are now 5s, while the remaining 3s are true modeling-policy rows for absent tuples or whitespace normalization.
   - Updated `artifacts/filter-dx-corpus-regeneration-capabilities.md` so future rebuilds do not preserve old boolean or nullable-scalar assumptions.

2. [x] Corpus appeared to overuse `.AsString` for numeric/domain rows.

   Resolution:

   - F086-F090 now model product quantity-on-hand predicates as `.AsInt64` over `quantityOnHand`.
   - F208-F210 now model appointment duration predicates as `.AsInt64` over normalized `durationMinutes`.
   - Mixed text-plus-numeric rows now include both clauses instead of showing only the text clause: F287, F329, F334, F349, F353, F354, F363, and F440.
   - Composite typed-part rows now type each part independently instead of using string/string placeholders: F388, F390, F393, F401, F404, F409, F618, F620, and F635.
   - F436, F519, and F520 now use numeric versions/buckets instead of string membership/ranges.

3. [x] Public string condition operators needed consistent optional case/culture parameters.

   Resolution:

   - Root `.AsString` operators preserve optional `ignoreCase` and `culture` on equality, inequality, ordered comparisons, ranges, prefix/suffix/contains, wildcard pattern matching, membership, and non-membership.
   - `.Matches(...)` forwards the same optional case/culture parameters as `.MatchesPattern(...)`.
   - `.MatchesWith(...)`, `.MatchesIn(...)`, `.MatchesInSet(...)`, and negated variants carry the same optional case/culture parameters.
   - Ordered multikey `.AsString` wrappers forward the same options.
   - Opened composite named string-part and full-key string operators preserve the same options for their public string predicates.

4. [x] Captured-match string operators needed a real capture grammar.

   Resolution:

   - Added regex-backed `.MatchesWith(pattern, value)` comparing `Regex.Match(candidate, pattern).Value` to `value`.
   - Added regex-backed `.MatchesWith(pattern, value, groupNumber)` comparing `Regex.Match(candidate, pattern).Groups[groupNumber].Value` to `value`.
   - Added `.MatchesIn(...)`, `.MatchesInSet(...)`, `.NotMatchesWith(...)`, `.NotMatchesIn(...)`, and `.NotMatchesInSet(...)` with the same whole-match and numbered-group overload shape.
   - Routed the family through executable string-pattern predicates rather than whole-key wildcard aliases.
   - Added harness proof rows for whole-match comparison, numbered capture comparison, capture membership, and negation.

5. [x] Public `.MatchesPattern(...)` examples were too verbose.

   Resolution:

   - Added `.Matches(...)` short aliases for string, Guid, binary hex, multikey, and composite string/full-key pattern surfaces.
   - Updated the filter-DX corpus and harness examples to advertise `.Matches(...)`.

6. [x] Null/empty key condition DX needed explicit enum overloads and low-allocation aliases.

   Resolution:

   - Added public `NullKey` with `Null`, `Empty`, and `NullOrEmpty`.
   - Added `.EqualTo(NullKey...)`, `.NotEqualTo(NullKey...)`, `.EqualTo(DBNull.Value)`, and `.NotEqualTo(DBNull.Value)` for string, binary, catalog/multikey wrappers, and opened-index same-index grammar.
   - Routed string `.EqualTo(null)` / `.NotEqualTo(null)` to `NullKey.Null`, and empty string to `NullKey.Empty`.
   - F026 now shows `NullKey.Null` for stored string null.

7. [x] F001 catalog-group string-field syntax was type-first instead of index-first.

   Complaint:

   ```csharp
   catalog["customers"].Where.String("firstName").StartsWith(value).EndCondition
   ```

   Preferred pattern:

   ```csharp
   catalog["customers"].Where("firstName").AsString.StartsWith(value).EndCondition
   ```

   Resolution:

   - Catalog identity groups now start condition syntax with `Where(indexName)` or `Where(indexInstance)`.
   - Multi-index continuation now supports `AndAlso(indexName)`, `AndAlso(indexInstance)`, `OrElse(indexName)`, and `OrElse(indexInstance)`.
   - Type-first named and handle selectors were removed from the group-level condition builder; ordered `MultiKey(...)` programmatic syntax now uses index-first ordinal selectors.

8. [x] Catalog and MultiKey conditions needed a `.Not` operator and richer instance/programmatic selectors.

   Resolution:

   - Catalog/group selectors now support `.Not` before the value family, so both same-index and cross-index chains can express negative predicates without spelling the inverse method directly.
   - Clause-level `.Not` now supports `.Not.Index(...)` and `.Not.Group(...)`, making `.And.Not.Group(fragment)` the canonical grouped negation form.
   - `.And` and `.Or` aliases now mirror `.AND` and `.OR` so completed-condition grouping can use the lower-friction Abraxas-like `.And.Group(...)` shape.
   - Generic typed index handles now support `.Where(indexInstance).EqualTo(...)` / `.AndAlso(indexInstance).EqualTo(...)` / `.OrElse(indexInstance).EqualTo(...)` for base typed operators without repeating `.AsInt64`, `.AsBoolean`, and similar scalar-family selectors.
   - Opened string index facades now support `.Where(stringIndex).StartsWith(...)` and typed string continuations without repeating `.AsString`.
   - Ordered `MultiKey(...)` now supports `.Not` plus Boolean, date/time, TimeSpan, narrow/wide numeric, `char`, and `BigInteger` selector families, and also exposes name/handle `Where`, `AndAlso`, and `OrElse` bridges for mixed generated/manual condition assembly.

9. [x] Conditions should stay filter descriptors while retrieval methods own stream/result shape.

   Resolution:

   - Added `CatalogIdentityGroupIndexes.GetCursor<TIdentity>(condition, ...)` for identity-only streaming from a completed condition.
   - Added `LibraDexIndex<TKey,TIdentity>.GetCursor(condition, ...)` and `LibraDexStringScalar8Index.GetCursor(condition, ...)` for target-index key/identity entry streaming.
   - Added catalog-group target-index cursor overloads so a condition can filter identities while a chosen index supplies `GetKey()`, `GetIdentity()`, and `GetEntry()` results.
   - Chose `GetCursor(...)` rather than `GetReader(...)` so the positioned stream can later grow per-entry and whole-cursor delete/mutate operations without contradicting the API noun.
   - Added positioned `LibraDexIndexCursor<TKey,TIdentity>.DeleteCurrent()` for current target-index entry deletion; successful deletion invalidates current access and the next move continues through the original cursor stream.
   - Added `DeleteRemaining()` and `DeleteAll()` over the forward-only cursor stream; both delete the current entry when positioned, then every later reachable entry, while entries already advanced past are intentionally not revisited.
   - Optimized `DeleteRemaining()` / `DeleteAll()` for direct, unpaged, same-index primitive cursor leaves by routing the whole delete through the existing primitive mutation path; skipped, paged, composed, or already-advanced streams keep the exact tuple fallback.
   - Added positioned `LibraDexIndexCursor<TKey,TIdentity>.SetCurrentKey(newKey)` plus `SetKey(newKey)` alias for current target-index entry re-key; successful re-key invalidates current access and the next move continues through the original cursor stream.
   - Direct same-index primitive leaves stream through the existing physical range-reader spine for common all/range/boundary/membership/multirange tuple primitives.
   - Composed `And` / left-target `Except` cursor conditions now stream an order-preserving direct target-index primitive leaf and filter against the opposite identity set instead of scanning every target tuple after identity projection.
   - Added identity-side `.External(id => ...)`, `.External((id, ordinal, isFirst) => ...)`, and `.External(ctx => ...)` filters for indexed `And` conditions so caller-owned non-indexed predicates can participate after LibraDex narrows candidates.
   - External identity filters also work through target-index cursors when the target branch supplies the candidate stream; standalone and `Or` external filters intentionally fail until a caller-supplied universe/source contract exists.
   - Collapsed external identity sources to `.External(ids)` / `.External(() => ids)` so caller-provided identity streams can stand alone and participate in `Or`, `And`, and cursor composition without an indexed predicate anchor.
   - Added runtime-index style `.External<TKey>(() => entries).Between(...)` and correlated `.External<TKey>(id => keys).Between(...)` branches so caller-owned non-indexed key data can participate inline while conditions still compose identities.
   - Remaining composed cursor opportunities include full streaming tuple joins and `Or` planning where target tuple ordering must be preserved across branches.

10. [x] Corpus examples and regeneration notes needed to recognize `.External(...)` as one canonical caller-owned data bridge.

   Resolution:

   - Updated `artifacts/filter-dx-corpus-regeneration-capabilities.md` to reject stale `.ExternalIds(...)`, `.ExternalContext(...)`, `.FilterIdentity(...)`, and `.ExternalKeys(...)` spellings.
   - Added corpus rows for anchored external identity predicates, `ordinal`/`isFirst` cache-friendly predicates, standalone external identity sources, runtime key/identity entry sources, and correlated external keys.
   - Scored the new external rows as first-class LibraDex coverage because caller-owned data can now participate inline without pretending it is a stored index.
