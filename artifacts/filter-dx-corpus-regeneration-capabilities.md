# Filter DX Corpus Regeneration Capabilities

Use this as the gating map before regenerating or refreshing `artifacts/filter-dx-natural-language-corpus.csv`.

## Current Syntax Roots

- Raw descriptors close with `.EndCondition`.
- Opened index shorthand closes with `.EndCondition`.
- Catalog group and ordered multi-key builders close with `.EndCondition`; terminal retrieval/mutation/delete APIs own any identity typing.
- Opened index shorthand currently covers generic equality, inequality, ordered range, between, membership, `NullKey`, and `ScalarNull` equality/inequality. Rich string, binary pattern/slice, date component, bit, and composite predicates should use the raw descriptor or catalog/multi-key builders.
- Catalog group selectors can negate the next leaf with `.Not` before the value family, for example `.Where("status").Not.AsString.EqualTo("archived")` or `.AndAlso((IIndex)flagIndex).Not.AsBoolean.EqualTo(true)`.
- Clause-level `.Not` is canonical for grouped and next-clause negation: prefer `.And.Not.Index("status").AsString.EqualTo("archived")` and `.And.Not.Group(fragment)` over adding new negative method names.
- Completed condition grouping can use `.And.Group(fragment)` / `.Or.Group(fragment)` as lower-friction aliases for `.AND.Group(fragment)` / `.OR.Group(fragment)`.
- Generic typed index handles can skip `.As...` for base typed operators through `.Where(indexInstance)`, `.AndAlso(indexInstance)`, and `.OrElse(indexInstance)`. Dedicated string index handles can also skip `.AsString` for rich string operators such as `.StartsWith(...)`.
- Ordered `MultiKey(...)` supports ordinal selectors for string, binary, Boolean, GUID, date/time, TimeSpan, narrow/wide numeric, `char`, and `BigInteger` key families, plus `.Not` before the selected value family.
- `.External(...)` is the canonical inline bridge for caller-owned data that is not stored in a LibraDex index.
- Anchored external identity filters use `.And.External(id => ...)`, `.And.External((id, ordinal, isFirst) => ...)`, or `.And.External(ctx => ...)` after an indexed branch supplies candidate identities.
- Catalog group condition stubs mirror raw descriptor roots: prefer `catalog["users"].External(...)` when an opened catalog group already carries the group context, and use `LibraDexCondition.ForGroup("users").External(...)` when building without an opened catalog instance.
- Standalone or `Or`-shaped caller-owned identity streams use `.External(ids)` or `.External(() => ids)`.
- Runtime-index style caller-owned key data uses `.External<TKey>(() => entries).Between(...)` from a typed catalog group such as `catalog["users"].Identities.Int64`, where each entry is a `LibraDexExternalEntry<TKey, TIdentity>` or `KeyValuePair<TKey, TIdentity>` with a key and identity.
- Correlated caller-owned key data over candidate identities uses `.And.External<TKey>(id => keys).Between(...)` or another typed key predicate.
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
- No catalog-group `.As<TIdentity>()` examples; identity typing belongs under `catalog["group"].Identities.*`, while `.As...` remains key-family vocabulary after an index selection.
- No opened-index cross-index fragments such as `email.Where.EqualTo(...).AND.Index(...)`; use raw descriptors, catalog typed continuers, `MultiKey(...)`, or close reusable fragments explicitly where the API supports it.
- No `.ExternalIds(...)`, `.ExternalContext(...)`, `.FilterIdentity(...)`, or `.ExternalKeys(...)` examples; all caller-owned condition participation should use the single `.External(...)` family.
- External identity predicates must be anchored by an indexed sibling branch unless they are explicit identity sources through `.External(ids)` or `.External(() => ids)`.
- External key predicates must make their source contract visible: use `LibraDexExternalEntry<TKey, TIdentity>` or `KeyValuePair<TKey, TIdentity>` when the caller provides key/identity pairs, and use typed catalog group correlated lambdas when the caller derives keys from candidate identities.
- Catalog-group external examples should use `catalog["group"].External(...)` when group context is already available; do not imply that catalog group shorthand is limited to stored-index roots.
- No Abraxas whole-binary prefix/contains/suffix method calls unless the Abraxas builder grows those methods; use typed slices, projections, or custom predicate notes instead.
- Rows containing `missing`, `null`, `empty`, `blank`, `optional`, or `nullable` must say which semantic bucket they mean: stored null, stored empty, null-or-empty, scalar null route, absent tuple, or normalized/domain blank.
- Rows using `presence` must explain why the requested value cannot be represented by the searched key itself.
