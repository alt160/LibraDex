# Filter DX Corpus Regeneration Capabilities

Use this as the gating map before regenerating or refreshing `artifacts/filter-dx-natural-language-corpus.csv`.

## Current Syntax Roots

- Raw descriptors close with `.EndCondition`.
- Opened index shorthand closes with `.EndCondition`.
- Catalog group and ordered multi-key builders close with `.Condition`; type them for terminal APIs with `.Condition.As<TIdentity>()`.
- Opened index shorthand currently covers generic equality, inequality, ordered range, between, and membership. Rich string, binary pattern/slice, date component, bit, and composite predicates should use the raw descriptor or catalog/multi-key builders.

## Current Value Semantics

- String varlen keys distinguish null, empty, and non-empty values with ordered sentinels.
- Binary varlen public APIs distinguish null, empty, and non-empty values with ordered sentinels.
- Null sorts before empty; empty sorts before non-empty payloads.
- For string keys, null-or-empty can be represented as `LessOrEqual(string.Empty)` when the index stores null keys.
- For VS/VV byte keys, null/empty ranges can use nullable byte-array range bounds on the opened index API.
- Do not use a presence index for a stored string or binary null unless the request means absence of an index tuple rather than a stored null key.

## Capability Gaps To Keep Visible

- There is no boolean-specific condition selector. Model boolean-like values as scalar codes such as `Int32` 0/1, flags, enum strings, or a dedicated domain projection.
- Scalar/date/GUID key families do not currently have a native nullable-key sentinel like string/varlen binary. Nullable scalar/date/GUID semantics require an explicit sentinel value or presence/projection policy.
- Binary null condition-builder materialization should be treated as partial until proven; opened VS/VV APIs support null bounds directly.
- Whitespace-only, trimmed, normalized, domain-blank, and custom missing semantics require a normalized projection or explicit policy. Do not collapse them into string null/empty unless the row says that is the stored representation.
- A true missing tuple cannot be found by scanning the missing index, because no key row exists. Use a presence/projection index only for that specific meaning.

## Post-Refresh Validation Checks

- No `AsBoolean` or `Where.Boolean` examples.
- No untyped catalog `.Condition` examples where a typed terminal is implied; use `.Condition.As<TIdentity>()`.
- Rows containing `missing`, `null`, `empty`, `blank`, `optional`, or `nullable` must say which semantic bucket they mean: stored null, stored empty, null-or-empty, absent tuple, or normalized/domain blank.
- Rows using `presence` must explain why the requested value cannot be represented by the searched key itself.
