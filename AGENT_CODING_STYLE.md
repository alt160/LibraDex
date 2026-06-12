# Personal C# Coding Style Guide

Evidence base: drafted from full-file reads of handwritten C# under `C:\VSProjects\AbraxasDB`, `C:\VSProjects\InhetoSerializer`, and `C:\VSProjects\FractalKVS` on 2026-06-08. Generated, designer, decompiled, and build-output files were excluded.

This is not a project architecture guide. It is a compact style fingerprint for writing new code that should feel closer to the user's handwritten code.

## Three Tenets

Use these as the tie-breakers when style choices compete:

1. High performance patterns: make allocation, IO, parsing, reflection, dispatch, locking, and iteration costs visible; prefer shapes that keep hot-path work tight and predictable.
2. Low call depth/call chain: keep ordinary behavior close to the caller; avoid helper stacks, framework layers, and fluent chains that make simple behavior harder to trace.
3. Low friction DX: public APIs should minimize caller ceremony, typed characters, casts, lambdas, mode flags, and required prior knowledge while staying explicit about meaningful behavior.

## Core Shape

1. Optimize for low/no developer friction.
2. Prefer compact, direct code over ceremony and helper indirection.
3. Keep correctness, performance, and ownership visible.
4. Use abstraction only when it removes real repetition, lowers call-site friction, or protects a repeated correctness/performance rule.
5. Let domain names carry meaning; do not wrap clear code in generic service/repository vocabulary.
6. Prefer shallow execution paths that can be read, debugged, and reasoned through without chasing many methods.
7. Match the immediate surrounding file before applying any broad rule from this document.

The general style is dense but not cryptic: short local code, explicit domain nouns, direct guards, few comments, and visible performance costs.

## Names

Use PascalCase for public types, properties, methods, enum values, and public record/struct members.

Use `_camelCase` for newer private fields, especially private readonly lock/state fields:

```csharp
private readonly object _pathCatalogLock = new();
private readonly ConcurrentDictionary<byte, MemoryIndexingContext> _memoryIndexingContexts = new();
```

Preserve older plain camelCase instance fields in files that already use them:

```csharp
internal string name;
internal FractalStore data;
private FractalStoreConfiguration config;
```

Do not rename fields merely to satisfy a modern convention. Nearby consistency wins.

Prefer short, useful local names when the scope is small:

```csharp
var ib = Inheto.InhetoBinary.FromStream(new BufferStream(rec.Data), null);
var rx = new Regex(entry.Pattern, entry.Flags, timeout);
var di = new DirectoryInfo(folderPath);
```

Use compact abbreviations when they are domain-stable or locally obvious: `id`, `ids`, `ib`, `rx`, `di`, `sb`, `cmd`, `rec`, `ret`, `sel`, `mgr`.

Use descriptive names when the value survives beyond a few lines or encodes an invariant: `operationId`, `bucketIndex`, `recordOffset`, `currentSize`, `timeoutTicks`, `scheduledCount`.

Avoid long generic helper names that read like framework code. Prefer `LoadPathCatalog`, `SeedRegexCatalog`, `PublishIndexingProgress`, `TryGetMemoryIndexingContext` over vague names like `ProcessDataStateAsync`.

Short names are a DX and scan-speed tool, not a license for ambiguity. Use short locals where the reader can see the whole lifetime without scrolling. Use longer names when the value crosses branches, locks, IO, public boundaries, or exception messages.

## Method Names

Prefer direct verb names:

```csharp
Create
CreateOrOpen
Load
Seed
Bind
Register
Unregister
Publish
Read
Write
Add
Remove
TryGet
TryBuild
ToSql
ToBytes
```

For fluent APIs, method names should read like the caller's sentence and minimize extra ceremony:

```csharp
Prop(...)
PropPath(...)
PropSelector(...)
Group(...)
EqualTo(...)
NotEqualTo(...)
Between(...)
IsIn(...)
```

Use `Try...` only when the caller can branch on a boolean success result. Do not use `Try` as a synonym for "perform with exception handling."

Use overloads when overloads make the caller's syntax shorter and safer. This style accepts many small overloads if they remove casts, type switches, or caller-side ceremony.

Prefer method names that put the caller's likely verb first. Do not force callers through neutral framework verbs like `Execute`, `Process`, `Handle`, or `Configure` when a domain verb can say the same thing with less mental translation.

## Compactness

Prefer compact member bodies when the operation is a simple forwarding, event publish, property projection, or small calculation:

```csharp
internal bool TryGetMemoryIndexingContext(byte storeId, out MemoryIndexingContext? context)
    => _memoryIndexingContexts.TryGetValue(storeId, out context);

public bool IsOnlineDefragRunning => _onlineDefrag.IsRunning;
```

Use block bodies when there is branching, validation, mutation, locking, ownership transfer, or a useful sequence of named steps.

Compact code is preferred, but not at the cost of hiding work. This is good:

```csharp
if (scheduledCount < 0)
    throw new ArgumentOutOfRangeException(nameof(scheduledCount));
```

This is not the target style for meaningful behavior:

```csharp
return thing?.Do()?.Other()?.Last() ?? throw new InvalidOperationException();
```

The issue is not only readability. Dot-chain syntax can hide allocations, deferred execution, temporary collections, captured closures, enumerators, disposable intermediates, ownership transfer, or objects that quietly add heap pressure. Chaining is acceptable when each step is known to be cheap, non-owning, non-disposable, and non-allocating or minimally allocating.

Dense lines are acceptable when each token carries real information. Avoid line bloat from unnecessary temporaries, wrappers, or "enterprise" names.

Compactness should lower both typed characters and trace cost. A compact method that hides three allocations and five helper calls is not the target style; a compact method that directly shows the branch, value, and write is.

Prefer this:

```csharp
if (!StringComparer.Ordinal.Equals(existing, path))
    throw new InvalidOperationException($"PATH id collision: 0x{id:X16}.");
```

Over this:

```csharp
PathCatalogCollisionValidator.ThrowIfDifferent(existing, path, id);
```

unless that validator is already a shared invariant with real reuse.

## Line And Block Rhythm

Use Allman braces in normal C# blocks.

Short guard clauses commonly omit braces:

```csharp
if (context is null)
    return;
```

Nested `if` blocks are acceptable when they keep the flow local and obvious. Do not force early-return style everywhere if the existing member reads better as a nested state machine.

Prefer a small number of meaningful temporaries over repeated expressions:

```csharp
var key = unchecked((long)id);
var timeout = entry.TimeoutTicks < 0 ? Timeout.InfiniteTimeSpan : new TimeSpan(entry.TimeoutTicks);
```

Use `var` heavily for locals when the right side makes the type obvious or the exact type is not the point. Use explicit primitive/domain types when size, signedness, ownership, or binary layout matters:

```csharp
ulong currentOffset = bucket.RecordOffset;
uint currentSize = bucket.RecordSize;
int offset = (int)(BucketTierHeader.SizeOf + bucketIndex * BucketEntry.SizeOf);
```

Keep the main path visually close. A reader should be able to follow common behavior top-to-bottom without jumping through a chain of tiny helpers. Extract only when the extracted block has a real name, hides irrelevant detail, or reduces repeated risk.

## Constructors And Object Creation

Use object initializers when they reduce noise:

```csharp
var ret = new RecordBase
{
    dataFilesLocation = new DirectoryInfo(dataFilesLocation),
    name = name
};
```

Use target-typed `new()` for private fields and obvious state:

```csharp
private readonly object _idBlockSync = new();
private Dictionary<long, string> _pathCatalog = new();
```

Use explicit type construction when it improves scan speed or the type is the point:

```csharp
_pathCatalog = new Dictionary<long, string>();
```

Avoid object construction patterns that add call depth for no caller benefit, such as builder objects around three required constructor values or options objects used only once. Use those patterns when they materially reduce public-call friction or preserve compatibility.

## Comments

Default to no comment for ordinary code.

Use inline or in-member comments when they capture one of these:

1. A non-obvious invariant.
2. Storage layout or binary format intent.
3. Locking/concurrency reason.
4. Performance tradeoff.
5. Compatibility or corruption fallback.
6. Why a magic-looking number/range exists.

Good examples of the intended comment style:

```csharp
// Corrupt or incompatible catalog payload; reset so we can continue.
// Walk the collision chain starting at this bucket's head.
// Reserve a dedicated block of 64-bit atomic slots for per-store USNs (storeID 1..255).
```

Comments should explain why the code takes this shape, especially when the shape is chosen for performance, low call depth, or low caller friction. If a comment merely compensates for a poor name or unnecessary indirection, prefer better code.

Avoid comments that merely translate code into English:

```csharp
// Bad: increment the count
count++;
```

Section banners are part of the user's style in some large older files:

```csharp
//======  FIELDS  ======
//======  METHODS  ======
```

Keep them when a file already uses them. Do not introduce them into small partial files or modern focused files unless the user asks for that organization.

## XML Comments

XML comments are selective. They are used for public/configuration/API behavior, not every member.

When adding new public methods for the user, include detailed XML comments with `<br/>` line terminators:

```csharp
/// <summary>
/// Adds a serializer for values of <typeparamref name="ValueType"/>.<br/>
/// The serializer is used when no member-specific serializer is registered.<br/>
/// </summary>
/// <param name="serializer">Function used to convert the value before storage.<br/></param>
```

Prefer useful behavior notes over formal completeness. Empty `<param>` text is worse than no XML.

Use `<see cref="..."/>`, `<paramref name="..."/>`, and `<typeparamref name="..."/>` when they improve precision without adding much bulk.

## Guards And Failures

Use direct guard clauses. Avoid helper guard frameworks.

Use `ArgumentNullException.ThrowIfNull(value);` in newer code.

Use `throw new ...` when the message carries domain context:

```csharp
throw new ArgumentException("Set name must be non-empty.", nameof(setName));
throw new InvalidOperationException($"REGEX id collision: 0x{id:X16}. Existing='{existing.Pattern}', New='{pattern}'");
```

Exception messages should be concrete and useful for diagnosing the exact failed invariant.

Do not over-normalize older nullability style. Improve new code, but avoid churn.

Do not push guard logic through shared validators unless the validation itself is complex, reused, or domain-significant. Inline guards keep call depth low and make failure behavior obvious.

## Performance Style

Prefer explicit loops in serialization, indexing, binary layout, storage, and other hot paths.

LINQ is fine for low-volume setup, diagnostics, projections, and harness/reporting code. Do not introduce LINQ into hot binary/storage loops just to shorten code.

Use spans, pooled arrays, stackalloc, and `MemoryMarshal` when moving bytes or encoding fixed layouts.

Keep allocations and ownership visible. If a pooled array is rented, return it clearly. If a method exposes memory, document ownership/lifetime when public.

Small hot helpers may use expression bodies and `AggressiveInlining`, but only when the helper is obviously tiny.

Do not hide IO, flushes, writes, locks, reflection, or expression compilation behind pleasant-looking helpers if those costs matter to the caller or caller's performance model.

Prefer single-pass transforms over multi-pass convenience chains. If code can parse/copy/compare once in a clear loop, do not split it into several enumerations or helper calls to make the source look declarative.

Avoid `dot.command.command.command` style when the chain may allocate, enumerate, capture, defer work, own resources, require disposal, or create heap pressure under volume. It is allowed for simple property navigation, cheap value-like transforms, low-volume setup, or intentional DX fluent surfaces whose allocation behavior is known and acceptable.

Prefer concrete data movement over opaque conversion layers:

```csharp
var span = tier.Tier.AsWritableSpan.Slice(offset, (int)BucketEntry.SizeOf);
MemoryMarshal.Write(span, ref entry);
```

This style is performance-aware, but not micro-optimized by default. Make the fast path direct first; optimize deeper only when evidence or obvious hot-path structure justifies it.

When performance and DX compete, preserve both if possible: keep the public call short and typed, then make the implementation direct enough that allocation and IO behavior remain inspectable.

## Call Depth

Keep call depth low for ordinary behavior. A method that just checks, maps, writes, and returns can usually do that inline.

Extract helpers when one of these is true:

1. The helper names a real domain operation.
2. The helper removes repeated bug-prone code.
3. The helper isolates slow/unsafe/IO/reflection behavior.
4. The helper keeps a public method readable without hiding the main path.
5. The helper improves testability of a meaningful branch.

Do not extract helpers just to reduce line count. In this style, fewer methods can be better than shorter methods when the resulting code has lower trace cost.

Avoid call chains where each method only renames the next method's work:

```csharp
Save()
    -> SaveCore()
    -> SaveInternal()
    -> ExecuteSave()
```

Prefer one clearly named method plus small local helpers if needed.

For public API surfaces, low call depth also means low concept depth. A caller should not have to learn a separate configuration type, factory, context, builder, and executor when one direct call or fluent chain can safely express the operation.

## Helper Placement

Prefer helpers near the code that uses them until reuse is real.

Private static helpers are good for small repeated transforms:

```csharp
private static byte GetStoreId(ulong recordId) =>
    (byte)(recordId >> 56);
```

Local functions are acceptable when they keep a small traversal or parser self-contained.

Do not lift code into a shared utility just because it appears twice. Lift it when the shared behavior has a name, invariant, or bug surface worth centralizing.

Prefer local functions over private methods when the helper only exists for one member and shares that member's context. This keeps the helper close without polluting the type-level surface.

## Fluent And Builder Code

The user's fluent APIs favor caller ergonomics over minimal implementation size.

Typed wrapper stages are acceptable if they make the next legal call obvious. Repeated overloads are acceptable if they remove caller casts and preserve IntelliSense flow.

Return `this` for same-stage continuation. Return a new typed object when the grammar stage changes.

Avoid callback/lambda-first APIs when a completed expression/fragment object is easier to build, reuse, and read.

Low-friction DX means the common call should be short, strongly typed, and discoverable. Prefer typed overloads and staged return types over `object`, strings, mode enums, or callback-heavy APIs when the typed shape is not excessive.

Avoid fluent chains that are only fluent for the implementer. A good fluent chain reads like the operation. A bad fluent chain forces the caller through ceremony:

```csharp
// Prefer this kind of shape.
store.Where.Prop(x => x.Name).EqualTo("Alice");

// Avoid this kind of shape unless the extra concepts are required.
store.Query().For<Record>().WithSelector(x => x.Name).UsingOperator(Equal).WithValue("Alice").Execute();
```

Favor common-case defaults. Optional knobs should not obscure the simplest correct call.

## Collections And Data

Use ordinary BCL collections directly: `Dictionary`, `HashSet`, `List`, arrays, `ConcurrentDictionary`.

Use explicit comparers for string identity/case behavior:

```csharp
StringComparer.Ordinal.Equals(existing, path)
```

Use tuples and record structs for small structured data when the shape is local and obvious.

Prefer named structs/records/classes when the data crosses API boundaries or needs documentation.

## What Generated Code Should Avoid

Do not inflate code with framework-style abstractions, layers, strategies, services, factories, or managers unless the surrounding code already has that shape and the added type removes real friction.

Do not add comments for every step.

Do not turn compact guards into verbose blocks unless the condition needs explanation.

Do not turn obvious locals into long names.

Do not use clever functional chains in code where the user's style would show the loop, index, offset, allocation, ownership, or disposal boundary.

Do not "modernize" an old file while adding one method. Respect local density, indentation, comments, and field style.

Do not reduce apparent source length by increasing hidden call depth.

Do not turn a public API into a configuration ceremony unless the added object materially improves clarity, reuse, or compatibility.

## Better Generated Shape

Prefer this:

```csharp
if (data is null)
    throw new ArgumentNullException(nameof(data));

var key = unchecked((long)id);
if (_regexCatalog.TryGetValue(key, out var existing))
{
    if (!StringComparer.Ordinal.Equals(existing.Pattern, pattern))
        throw new InvalidOperationException($"REGEX id collision: 0x{id:X16}.");

    return;
}
```

Over this:

```csharp
ValidationHelpers.EnsureNotNull(data, nameof(data));

var regexCatalogKeyForLookup = unchecked((long)id);
var regexCatalogEntryAlreadyExists = _regexCatalog.TryGetValue(regexCatalogKeyForLookup, out var existingRegexCatalogEntry);
if (regexCatalogEntryAlreadyExists)
{
    RegexCatalogValidator.ValidateNoCollision(existingRegexCatalogEntry, pattern, id);
    return;
}
```

Prefer this:

```csharp
private static ulong Advance(ulong offset)
{
    if (offset == 0)
        return 0;

    var header = fData.ReadRecordHeader(offset);
    return header.NextRecordOffset;
}
```

Over this:

```csharp
private static ulong GetNextRecordOffsetOrDefault(ulong currentRecordOffset)
{
    return currentRecordOffset == 0
        ? 0
        : ReadHeaderAndReturnNextOffset(currentRecordOffset);
}
```

## Strength Levels

Strong tendencies:

1. Direct names over framework names.
2. Compact guards and simple forwarding expression members.
3. `var` for obvious locals.
4. Explicit primitive types where layout, size, or signedness matters.
5. Comments only for why/invariants/layout/performance.
6. Typed fluent overloads when they reduce caller friction.
7. Explicit loops in hot paths.
8. Low call depth unless a helper has real semantic weight.
9. Public APIs shaped around short, discoverable common-case calls.
10. Performance costs kept visible instead of hidden behind pleasant abstractions.

Project/local tendencies:

1. Section banners.
2. Older camelCase fields.
3. Large partial-type families.
4. Specific storage/indexing nouns.
5. Older nullable/default-parameter looseness.

When writing new code, follow the strong tendencies unless nearby code clearly says otherwise.

## Final Check

Before considering generated code style-matched, ask:

1. High performance: Are allocation, IO, locking, reflection, parsing, and iteration costs visible enough to reason about?
2. Low call depth: Can the main behavior be followed without chasing a stack of helper methods?
3. Low friction DX: Is the common caller path short, typed, discoverable, and free of unnecessary ceremony?
4. Local fit: Does this match the surrounding file's density, naming, comments, and organization?
