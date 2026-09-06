# Abraxas / Inheto capability-promotion audit

Date: 2026-08-24

## Outcome

The source review found one material missing semantic capability and two worthwhile Abraxas-shaped discovery conveniences. The rest of Inheto's application-facing configuration is already reachable through each `RecordStore<T>` and should not be duplicated behind a second Abraxas vocabulary.

🟩 [x] General property-path aliasing is now an Inheto read/materialization contract and is promoted as `store.Aliases`.

🟩 [x] `store.CheckPropPath(...)` promotes cached, nonexecuting textual path validation against the store's declared record type.

🟩 [x] `store.TestType(...)` promotes Inheto's bounded runtime-shape probe using the store's active serialization rules.

🟩 [x] Focused executable coverage proves exact-name precedence, renamed complex-member suffixes, alternate-shape materialization, direct `Get*`, borrowed UTF-8 access, selectors, conditions, sorted readers, ordinary-index fail-closed behavior, and explicit expression-index acceleration.

## General property aliases

```csharp
customers.Aliases.Add(".CompanyName", ".DisplayName", ".LegalName");
customers.Aliases.Add(".Address", ".Location");
```

An alias registration is one equivalence group, not a one-way object mapper:

1. The exact requested path wins when that member exists in the payload.
2. If it is absent, the canonical path is tried.
3. Remaining aliases are tried in registration order.
4. A renamed complex ancestor carries the untouched suffix, so `.Location.PostalCode` may read `.Address.PostalCode`.
5. Writes continue to serialize the runtime object's actual member names. Aliases do not silently rename or duplicate stored fields.

The same `DeserializationOptions` instance reaches object materialization, projection, direct property access, reader property access, conditions, and selectors. Alias resolution therefore occurs at Inheto's common read boundary instead of being reimplemented by each Abraxas feature.

### Index safety

One ordinary property index represents one physical member spelling. After an alias group is registered, records may legitimately contain different spellings, so that one index cannot prove complete logical population. Semantic property-path lookup, conditions, candidates, unique lookup, active-source projection, and ordinary exact sort reuse therefore fail closed to authoritative payload evaluation for alias-affected paths.

This does not prohibit acceleration. A developer may explicitly create an expression index from the aliased selector. That index evaluates the semantic selector over every payload and can then execute the identical selector directly:

```csharp
var displayName = customers.Calc.PropPath(".DisplayName").AsString;
customers.CreateExpressionIndex(displayName.ForIndexing());

var where = customers.Where.Calc(displayName).EqualTo("Acme").EndCondition;
```

An explicitly supplied physical index handle remains physical by design. It is not silently widened into an alias-group query.

## Inheto features already exposed correctly

`RecordStore<T>.SerializationOptions` and `RecordStore<T>.DeserializationOptions` are public and are the actual option instances used by writes and reads. The following capabilities therefore already need no wrapper or promotion:

🟩 [x] Property-specific and type-wide binary serializers/deserializers.

🟩 [x] Included members, excluded members, excluded types, public-property/field selection, and fail-on-unsupported-type policy.

🟩 [x] Custom activators for projection/materialization shapes.

🟩 [x] Member and type comparers used while reconstructing compatible collection shapes.

🟩 [x] Circular-property registration and duplicate-reference reconstruction.

🟩 [x] Mixed dictionary-key policy and Inheto's extensive native scalar, collection, array, tuple, comparer, and zero-input/singleton handling.

🟩 [x] Binary codec reuse through `InhetoBinaryCodec`, including the exact-byte property-link contract already used by Abraxas Links.

Adding parallel `RecordStore<T>` wrappers for all of these would create two configuration surfaces for the same operation and make precedence harder for a developer to predict.

## Capabilities intentionally kept low-level

The following are implementation or specialized codec utilities rather than ordinary database intentions. They remain available from Inheto when an advanced developer actually needs them, but are not copied onto `RecordStore<T>`:

🟩 [x] Name-header traversal, raw entry structs, dynamic `IInhetoObject` internals, accessor factories, enumerable mutators, and generic-shape analyzers.

🟩 [x] Standalone Brotli, AES, hashing, and primitive byte helpers. Abraxas storage durability/encryption/compression policy must remain explicit rather than accidentally inferred from serializer utilities.

🟩 [x] `ReadPropOnto` is not promoted as a general low-allocation reader API: its current by-value generic signature is suitable for mutable reference instances but is not a correct uniform contract for structs. Abraxas's existing typed readers and `GetAs<T>()` remain the predictable public shape boundary.

## Remaining product gaps found outside Inheto

These are not missing Inheto shape capabilities and should not be hidden behind serializer wrappers:

🟨 [~] Conditional/compare-exchange object writes need a deliberate Abraxas durability contract before implementation. The important fork is whether the precondition is an identity revision, a State value, a semantic condition, or a combination.

🟨 [~] Change feeds, historical/as-of reads, TTL, full-text ranking, spatial search, and vector similarity are distinct storage/query products. The Northwind SQL tracker should not pretend they are covered merely because application C# could simulate a small example.

🟨 [~] General aliases are runtime/store configuration, like serializers and Links. Applications must register them consistently when opening the store; persisting application delegate/configuration intent inside the database would introduce code-version and deployment coupling.

## Validation target

The focused command is:

```text
AbraxasTestHarness.exe abraxas-inheto-promotion-sanity
```

Its fixture intentionally creates an incompatible ordinary index named for an alias. The property-path condition must remain a Fractal/Inheto semantic scan, while an explicit alias-aware expression index must subsequently produce a direct LibraDex plan for the identical calculated selector.

The final `AbraxasDB.sln` Release/x64 rebuild completed with zero errors. The focused alias harness and the selector, condition-semantic-scan, sorted-reader, and Link sanity harnesses completed successfully. The broader data-reader harness printed all of its passing assertions but remained in teardown during this run, so it is recorded as an independent teardown concern rather than overstated as a clean process-exit pass.
