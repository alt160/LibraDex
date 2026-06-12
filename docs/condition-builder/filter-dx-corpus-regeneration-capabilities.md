# Filter DX Corpus Regeneration Capabilities

Use this as the gating map before regenerating or refreshing `docs/condition-builder/filter-dx-natural-language-corpus.csv`.

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

## Regeneration Bias Guardrail

- Rebuild corpus rows from natural-language query intent and the currently inspected public API surface, not by preserving old syntax columns as the starting point.
- Treat existing rows as topic coverage only. If an old row uses a stale pattern, replace the syntax rather than supplementing it with a second legacy-compatible spelling.
- Add query statements for newly supported semantics before rescoring, especially clause-level `.Not`, grouped composition, typed index handles that skip redundant `.As...`, ordered `MultiKey(...)` ordinal selectors, `NullKey`/`ScalarNull` key-state routing, capture-match string operators, and canonical `.External(...)` caller-owned data bridges.
- Identity typing under `catalog["group"].Identities.*` is intentionally named, including date/time standouts, because date/time identity families do not share an `INumber`-style abstraction and arbitrary custom identity types should not imply optimized LibraDex serialization.

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

## Score-Zero Capability Inventory

These buckets are based on the currently inspected condition-builder surface and the score-0 rows remaining after the June 9, 2026 regeneration pass.

- Directly expressible with current fluent syntax: F212, F275, F277, F280, F434, F435, F436, F437, F438, F439, F440, F451, F452, F453, F454, F456, F512, F513, F516, F519, F520, F522, F531, F533, F534, F543, F548, F582, F583, F588, F608, F621, F627.
- Expressible by current composition syntax: F373, F374, F381, F382, F383, F384, F386, F387, F389, F390, F391, F394, F396, F397, F401, F403, F405, F408, F411, F412, F413, F415, F417, F418, F420, F422, F423, F424, F425, F426, F427, F428, F429, F430, F441, F442, F443, F450, F455, F457, F458, F459, F460, F461, F477, F478, F479, F480, F503, F505, F508, F517, F518, F537, F538, F539, F540, F546, F549, F561, F566, F568, F587, F611, F612, F613, F614, F615, F616, F617, F619, F635, F637, F638.
- Projection, modeling, or external-data requirements rather than condition-builder gaps: F098, F147, F159, F206, F225, F227, F228, F229, F244, F249, F270, F271, F272, F306, F447, F487, F490, F493, F494, F511, F526, F527, F528, F529, F530, F532, F535, F536, F547, F550, F551, F552, F554, F555, F556, F557, F558, F559, F560, F562, F563, F564, F565, F569, F570, F572, F573, F574, F575, F576, F580, F584, F585, F586, F589, F590, F591, F592, F593, F594, F595, F597, F598, F599, F600, F601, F602, F603, F604, F607, F609, F610, F630, F631, F632, F636, F640, F641.
- Not condition-builder scope: F462, F463, F464, F465, F466, F467, F468, F469, F470, F471, F472, F473, F474, F475, F476.
- Terminal-wrapper rows where the predicate is expressible but the request is about delete, rekey, or update behavior: F484, F485, F486, F488, F489, F495.
- Likely new builder API if direct fluent syntax is desired: F053, F060, F063, F070, F509, F510.
- Needs a policy decision before scoring: F506, F507.

## Post-Refresh Validation Checks

- Boolean examples should use `.AsBoolean.EqualTo(true|false)`; no `Where.Boolean(...)` type-first examples.
- No catalog `.Condition` examples or generic catalog typed-view examples; use `.EndCondition` as the single final terminator.
- No catalog-group generic typed-view examples and no arbitrary generic identity selector examples; identity typing belongs under named `catalog["group"].Identities.*` members, including explicit date/time family names, while `.As...` remains key-family vocabulary after an index selection.
- No opened-index cross-index fragments such as `email.Where.EqualTo(...).AND.Index(...)`; use raw descriptors, catalog typed continuers, `MultiKey(...)`, or close reusable fragments explicitly where the API supports it.
- No `.ExternalIds(...)`, `.ExternalContext(...)`, `.FilterIdentity(...)`, or `.ExternalKeys(...)` examples; all caller-owned condition participation should use the single `.External(...)` family.
- External identity predicates must be anchored by an indexed sibling branch unless they are explicit identity sources through `.External(ids)` or `.External(() => ids)`.
- External key predicates must make their source contract visible: use `LibraDexExternalEntry<TKey, TIdentity>` or `KeyValuePair<TKey, TIdentity>` when the caller provides key/identity pairs, and use typed catalog group correlated lambdas when the caller derives keys from candidate identities.
- Catalog-group external examples should use `catalog["group"].External(...)` when group context is already available; do not imply that catalog group shorthand is limited to stored-index roots.
- No Abraxas whole-binary prefix/contains/suffix method calls unless the Abraxas builder grows those methods; use typed slices, projections, or custom predicate notes instead.
- Rows containing `missing`, `null`, `empty`, `blank`, `optional`, or `nullable` must say which semantic bucket they mean: stored null, stored empty, null-or-empty, scalar null route, absent tuple, or normalized/domain blank.
- Rows using `presence` must explain why the requested value cannot be represented by the searched key itself.
