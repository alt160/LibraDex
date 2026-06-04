# LibraDex Filter DX Complaints

This checklist tracks developer-friction complaints found while reviewing `artifacts/filter-dx-natural-language-corpus.csv`.

## Open

1. [ ] Corpus appears to overuse `.AsString` for numeric/domain rows.

   Complaint:

   - F101-F104 previously used `.AsString` for measurement temperature/pressure comparisons and now use `.AsInt64`.
   - Temperature and pressure are likely numeric values unless the row explicitly says they are stored as sortable normalized text.
   - Audit similar corpus rows for accidental `.AsString` use where `.AsInt64`, decimal/fixed numeric, or another numeric/domain key family is more appropriate.

## Resolved

1. [x] Public string condition operators needed consistent optional case/culture parameters.

   Resolution:

   - Root `.AsString` operators preserve optional `ignoreCase` and `culture` on equality, inequality, ordered comparisons, ranges, prefix/suffix/contains, wildcard pattern matching, membership, and non-membership.
   - `.Matches(...)` forwards the same optional case/culture parameters as `.MatchesPattern(...)`.
   - `.MatchesWith(...)`, `.MatchesIn(...)`, `.MatchesInSet(...)`, and negated variants carry the same optional case/culture parameters.
   - Ordered multikey `.AsString` wrappers forward the same options.
   - Opened composite named string-part and full-key string operators preserve the same options for their public string predicates.

2. [x] Captured-match string operators needed a real capture grammar.

   Resolution:

   - Added regex-backed `.MatchesWith(pattern, value)` comparing `Regex.Match(candidate, pattern).Value` to `value`.
   - Added regex-backed `.MatchesWith(pattern, value, groupNumber)` comparing `Regex.Match(candidate, pattern).Groups[groupNumber].Value` to `value`.
   - Added `.MatchesIn(...)`, `.MatchesInSet(...)`, `.NotMatchesWith(...)`, `.NotMatchesIn(...)`, and `.NotMatchesInSet(...)` with the same whole-match and numbered-group overload shape.
   - Routed the family through executable string-pattern predicates rather than whole-key wildcard aliases.
   - Added harness proof rows for whole-match comparison, numbered capture comparison, capture membership, and negation.

3. [x] Public `.MatchesPattern(...)` examples were too verbose.

   Resolution:

   - Added `.Matches(...)` short aliases for string, Guid, binary hex, multikey, and composite string/full-key pattern surfaces.
   - Updated the filter-DX corpus and harness examples to advertise `.Matches(...)`.

4. [x] Null/empty key condition DX needed explicit enum overloads and low-allocation aliases.

   Resolution:

   - Added public `NullKey` with `Null`, `Empty`, and `NullOrEmpty`.
   - Added `.EqualTo(NullKey...)`, `.NotEqualTo(NullKey...)`, `.EqualTo(DBNull.Value)`, and `.NotEqualTo(DBNull.Value)` for string, binary, catalog/multikey wrappers, and opened-index same-index grammar.
   - Routed string `.EqualTo(null)` / `.NotEqualTo(null)` to `NullKey.Null`, and empty string to `NullKey.Empty`.
   - F026 now shows `NullKey.Null` for stored string null.

5. [x] F001 catalog-group string-field syntax was type-first instead of index-first.

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
