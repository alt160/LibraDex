# Filter DX Corpus Regeneration Capabilities

Use this as the gating map before regenerating or refreshing `artifacts/filter-dx-natural-language-corpus.csv`.

## Current Syntax Roots

- Raw descriptors close with `.EndCondition`.
- Opened index shorthand closes with `.EndCondition`.
- Catalog group and ordered multi-key builders close with `.EndCondition`; terminal retrieval/mutation/delete APIs own any identity typing.
- Opened index shorthand currently covers generic equality, inequality, ordered range, between, membership, `NullKey`, and `ScalarNull` equality/inequality. Rich string, binary pattern/slice, date component, bit, and composite predicates should use the raw descriptor or catalog/multi-key builders.
- Abraxas examples should use the inspected builder shape, such as `store.Where.PropPath(".Email").AsString.EqualTo(value).EndCondition`, with `.AND` / `.OR` / `.Group(...)` composition before the single final `.EndCondition`.
- Abraxas whole binary equality and typed binary slices are available, but do not generate whole-blob `AsBinary.StartsWith`, `AsBinary.Contains`, or `AsBinary.EndsWith` examples unless those operators are added to Abraxas.

## Current Value Semantics

- String varlen keys distinguish null, empty, and non-empty values with ordered sentinels.
- Binary varlen public APIs distinguish null, empty, and non-empty values with ordered sentinels.
- Null sorts before empty; empty sorts before non-empty payloads.
- For string keys, null-or-empty can be represented as `LessOrEqual(string.Empty)` when the index stores null keys.
- For VS/VV byte keys, null/empty ranges can use nullable byte-array range bounds on the opened index API.
- Do not use a presence index for a stored string or binary null unless the request means absence of an index tuple rather than a stored null key.

## Capability Gaps To Keep Visible

- Boolean values are direct through `.AsBoolean.EqualTo(true|false)`; do not regenerate old `.AsInt32.EqualTo(1|0)` Boolean workarounds.
- Scalar, date, and GUID nullable-key state is direct through `ScalarNull.Null` and `ScalarNull.NonNull` when the index stores scalar null-route entries. A true absent tuple still requires a maintained presence/projection index.
- Binary null, empty, and null-or-empty key state is direct through `NullKey.Null`, `NullKey.Empty`, and `NullKey.NullOrEmpty`.
- Whitespace-only, trimmed, normalized, domain-blank, and custom missing semantics require a normalized projection or explicit policy. Do not collapse them into string null/empty unless the row says that is the stored representation.
- A true missing tuple cannot be found by scanning the missing index, because no key row exists. Use a presence/projection index only for that specific meaning.

## Post-Refresh Validation Checks

- Boolean examples should use `.AsBoolean.EqualTo(true|false)`; no `Where.Boolean(...)` type-first examples.
- No catalog `.Condition` or `.Condition.As<TIdentity>()` examples; use `.EndCondition` as the single final terminator.
- No opened-index cross-index fragments such as `email.Where.EqualTo(...).AND.Index(...)`; use raw descriptors, catalog typed continuers, `MultiKey(...)`, or close reusable fragments explicitly where the API supports it.
- No Abraxas whole-binary prefix/contains/suffix method calls unless the Abraxas builder grows those methods; use typed slices, projections, or custom predicate notes instead.
- Rows containing `missing`, `null`, `empty`, `blank`, `optional`, or `nullable` must say which semantic bucket they mean: stored null, stored empty, null-or-empty, scalar null route, absent tuple, or normalized/domain blank.
- Rows using `presence` must explain why the requested value cannot be represented by the searched key itself.
