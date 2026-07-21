# Abraxas CreateIndex LibraDex Checklist

## Intent

Knit Abraxas index creation into LibraDex so supported Abraxas indexes automatically create and maintain an ordered LibraDex backend without changing ordinary Abraxas developer syntax.

Abraxas remains the public API owner. LibraDex stays behind the Abraxas index/query surface unless a diagnostic or explicit bridge requires otherwise.

## Guardrails

- Keep existing Abraxas `CreateIndex*` call sites stable.
- Do not expose LibraDex vocabulary through ordinary Abraxas index creation or retrieval.
- Keep unsupported index shapes `PrimaryOnly` instead of pretending they are ordered-ready.
- Keep setup-time reflection and metadata parsing out of write/query hot loops.
- Avoid delegates, LINQ, closures, and deep call chains in hot write/query paths.
- Preserve FractalKVS as the owner of record payload storage and record identity.

## Public Surface To Wire

- [x] `CreateIndex(bool unique, params IndexSelectorBuilder[] selectors)`
- [x] `CreateIndex(bool unique, Condition<T>.EndCondition? where, string? indexName, bool useMemoryIndex, params IndexSelectorBuilder[] selectors)`
- [ ] `CreateIndex(ISelector selector, bool unique = false, Condition<T>.EndCondition? where = null, string? indexName = null, bool useMemoryIndex = false)`
- [x] `CreateIndex<TValue>(Expression<Func<T, TValue>> propPathExpression, bool ignoreCase = false, bool unique = false, Condition<T>.EndCondition? where = null, string? indexName = null, bool useMemoryIndex = false)`
- [ ] `CreateIndexNumeric(string propPath, bool unique = false, Condition<T>.EndCondition? where = null, string? indexName = null, bool useMemoryIndex = false)`
- [ ] `CreateIndexText(string propPath, bool ignoreCase = false, bool unique = false, Condition<T>.EndCondition? where = null, string? indexName = null, bool useMemoryIndex = false)`
- [ ] `CreateIndexText(Expression<Func<T, string>> propPathExpression, bool ignoreCase = false, bool unique = false, Condition<T>.EndCondition? where = null, string? indexName = null, bool useMemoryIndex = false)`
- [ ] `CreateIndexText(ISelector propPathSelector, bool ignoreCase = false, bool unique = false, Condition<T>.EndCondition? where = null, string? indexName = null, bool useMemoryIndex = false)`
- [ ] Regex, shaped selector, multi-selector, folded/collated text, partial-index, and memory-index creation remain `PrimaryOnly` until explicitly supported.

## Supported Ordered Backend Slice

- [x] Simple property-path indexes only.
- [x] Primary persistent indexes only, not `useMemoryIndex`.
- [x] No partial `where` predicate.
- [x] One selector / one simple path.
- [x] Numeric CLR key types already supported by the registrar.
- [x] Boolean indexes create/register ordered backend.
- [x] Guid indexes create/register ordered backend.
- [ ] Exact text indexes evaluated separately; no folded/collated text in this slice unless string key semantics are verified.
- [ ] Date/time deferred until numeric/text/Guid lifecycle is boring.

## Behavior To Prove

- [x] Creating a supported simple index automatically registers an ordered backend.
- [x] `GetIndex(path)` resolves the index by `.Path` and `Path`.
- [x] `GetIndexes()` reports `OrderedReady` for supported registered indexes.
- [ ] `GetIndexes()` reports `PrimaryOnly` with preserved `KeyPath` for unsupported but simple primary indexes.
- [ ] Normal `Put` mirrors tuples into registered ordered backends.
- [ ] Overwrite removes old tuples and inserts new tuples.
- [ ] `Delete` removes tuples.
- [ ] `PutMany`, `PutManyFast`, `DeleteMany`, and `DeleteAll` either get coverage or stay explicitly unchecked in this checklist.

## First Implementation Focus

- [x] Inspect the first public `CreateIndex` overload.
- [x] Confirm whether it should stay a delegating convenience overload.
- [x] Put the real backend-registration hook in the core overload if that is where all supported creation paths converge.
- [x] Add a setup-only helper that accepts the selector list and creation options, recognizes the one-simple-property-path case, and calls the existing simple LibraDex registrar.
- [x] Avoid changing unsupported index behavior.

## Validation

- [x] AbraxasDB Release/x64 build.
- [x] Existing LibraDex bridge harness checks.
- [x] New harness check for generic/expression `CreateIndex` ordered-backend registration.
- [x] New harness check for boolean or Guid ordered-backend registration if implemented in the same slice.
- [ ] `git diff --check` in touched repositories.
