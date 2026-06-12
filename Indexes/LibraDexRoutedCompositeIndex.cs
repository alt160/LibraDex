using System.Globalization;
using System.Collections;
using System.Buffers.Binary;
using System.Text;

namespace LibraDex;

/// <summary>
/// Provides the first routed-component composite-key proof over in-process mini-router tiers.<br/>
/// Each composite part is stored as its own tier value, so repeated leading values are represented once as a route node rather than duplicated into every terminal key.<br/>
/// This slice intentionally proves condition semantics before the mini-router node format is persisted into DataKernel pages.<br/>
/// </summary>
public sealed class LibraDexRoutedCompositeIndex : IIndex, IIdentityPrimitiveExecutor, IIdentityPrimitiveMutator, IIdentityPrimitiveTupleExecutor, IIdentityExactTupleMutator
{
    private const int MaxCompositeDepth = 16;
    private readonly LibraDexIndexShapeSpec shape;
    private readonly LibraDexFileSession? session;
    private readonly int? slotIndex;
    private readonly CompositeNode root = new();
    private long itemCount;

    internal LibraDexRoutedCompositeIndex(LibraDexIndexShapeSpec shape)
        : this(shape, session: null, slotIndex: null, entries: null)
    {
    }

    /// <summary>
    /// Creates a routed composite index over an optional durable composite snapshot anchor.<br/>
    /// When a session and slot are supplied, successful inserts rewrite the first durable snapshot format and update the catalog slot root offset; when entries are supplied, the in-memory routed tier tree is rebuilt from reopened snapshot data.<br/>
    /// </summary>
    /// <param name="shape">The logical composite shape descriptor.</param>
    /// <param name="session">The optional owning file session for durable snapshot updates.</param>
    /// <param name="slotIndex">The optional fixed catalog slot that anchors the durable snapshot.</param>
    /// <param name="entries">Optional reopened entries to load into the routed tier tree.</param>
    internal LibraDexRoutedCompositeIndex(
        LibraDexIndexShapeSpec shape,
        LibraDexFileSession? session,
        int? slotIndex,
        IReadOnlyList<LibraDexCompositeEntry>? entries = null)
        : this(shape, session, slotIndex, rootOffset: 0, itemCount: 0, entries)
    {
    }

    /// <summary>
    /// Creates a routed composite index over an optional durable composite node-page root.<br/>
    /// The root offset is used by localized path-copy persistence so inserts can append changed node pages and publish a replacement root without rewriting unrelated branches.<br/>
    /// </summary>
    /// <param name="shape">The logical composite shape descriptor.</param>
    /// <param name="session">The optional owning file session for durable node updates.</param>
    /// <param name="slotIndex">The optional fixed catalog slot that anchors the durable root.</param>
    /// <param name="rootOffset">The durable root node page offset, or zero for memory-only indexes.</param>
    internal LibraDexRoutedCompositeIndex(
        LibraDexIndexShapeSpec shape,
        LibraDexFileSession? session,
        int? slotIndex,
        long rootOffset)
        : this(shape, session, slotIndex, rootOffset, itemCount: 0, entries: null)
    {
    }

    private LibraDexRoutedCompositeIndex(
        LibraDexIndexShapeSpec shape,
        LibraDexFileSession? session,
        int? slotIndex,
        long rootOffset,
        long itemCount,
        IReadOnlyList<LibraDexCompositeEntry>? entries = null)
    {
        ArgumentNullException.ThrowIfNull(shape);
        if (shape.KeyFamily != CatalogIndexKeyFamily.Composite)
        {
            throw new ArgumentException("Routed composite indexes require a composite logical shape.", nameof(shape));
        }

        if (shape.CompositeParts.Count == 0 || shape.CompositeParts.Count > MaxCompositeDepth)
        {
            throw new ArgumentOutOfRangeException(nameof(shape), shape.CompositeParts.Count, $"Composite depth must be between 1 and {MaxCompositeDepth}.");
        }

        this.shape = shape;
        this.session = session;
        this.slotIndex = slotIndex;
        this.itemCount = itemCount;
        root.DurableOffset = rootOffset;
        if (entries is not null)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                AddEntry(entries[i].Key, entries[i].Identity, persist: false);
            }
        }
    }

    /// <summary>
    /// Opens a routed composite index from durable composite node pages.<br/>
    /// The method rebuilds the in-memory tier tree with each node's durable offset so later inserts can path-copy only the changed route back to the root.<br/>
    /// </summary>
    /// <param name="shape">The logical composite shape descriptor.</param>
    /// <param name="session">The owning file session.</param>
    /// <param name="slotIndex">The fixed catalog slot anchoring the root node.</param>
    /// <param name="rootOffset">The durable root node page offset.</param>
    /// <param name="itemCount">The logical tuple count stored in the catalog slot.</param>
    /// <returns>A routed composite index loaded from node pages.</returns>
    internal static LibraDexRoutedCompositeIndex OpenFromNodePages(
        LibraDexIndexShapeSpec shape,
        LibraDexFileSession session,
        int slotIndex,
        long rootOffset,
        long itemCount)
    {
        LibraDexRoutedCompositeIndex index = new(shape, session, slotIndex, rootOffset, itemCount);
        index.root.MarkUnloaded(rootOffset);
        return index;
    }

    /// <summary>
    /// Gets the logical composite index name.<br/>
    /// </summary>
    public string Name => shape.Name;

    /// <summary>
    /// Gets the identity group that owns this routed composite index.<br/>
    /// </summary>
    public string Group => shape.Group;

    /// <summary>
    /// Gets the runtime key type accepted by this composite facade.<br/>
    /// </summary>
    public Type KeyType => typeof(LibraDexCompositeKey);

    /// <summary>
    /// Gets the runtime identity type accepted by this composite facade.<br/>
    /// </summary>
    public Type IdentityType => shape.IdentityType;

    /// <summary>
    /// Gets the duplicate-key contract for the full composite key.<br/>
    /// </summary>
    public IndexKeys KeyContract => shape.KeyContract;

    /// <summary>
    /// Gets the logical key family.<br/>
    /// </summary>
    public CatalogIndexKeyFamily KeyFamily => CatalogIndexKeyFamily.Composite;

    /// <summary>
    /// Gets the logical identity family.<br/>
    /// </summary>
    public CatalogIndexIdentityFamily IdentityFamily => shape.IdentityFamily;

    /// <summary>
    /// Gets the logical composite shape descriptor.<br/>
    /// </summary>
    public LibraDexIndexShapeSpec? LogicalShape => shape;

    /// <summary>
    /// Gets a composite condition root for this opened routed composite index.<br/>
    /// The index supplies its identity group and index name, so callers can describe declared key-part criteria without repeating `ForGroup(...).Index(...)` ceremony.<br/>
    /// </summary>
    public LibraDexCompositeConditionWhere Where => LibraDexCondition.ForGroup(Group).Index(Name).CompositeWhereRoot;

    /// <summary>
    /// Validates that a typed composite wrapper matches this index's persisted identity and key-part type contract.<br/>
    /// This is used by tuple-style typed handles such as `CompositeIndex&lt;TPart1, TPart2, TIdentity&gt;(...)` so caller generic mistakes fail before any condition is built.<br/>
    /// </summary>
    /// <param name="identityType">The identity type requested by the typed wrapper.</param>
    /// <param name="partTypes">The ordered key-part types requested by the typed wrapper.</param>
    internal void ValidateTypedCompositeShape(Type identityType, params Type[] partTypes)
    {
        ArgumentNullException.ThrowIfNull(identityType);
        ArgumentNullException.ThrowIfNull(partTypes);
        if (shape.IdentityType != identityType)
        {
            throw new InvalidOperationException($"Composite index '{Name}' stores identity type {shape.IdentityType.FullName}, not {identityType.FullName}.");
        }

        if (shape.CompositeParts.Count != partTypes.Length)
        {
            throw new InvalidOperationException($"Composite index '{Name}' has {shape.CompositeParts.Count} key parts, not {partTypes.Length}.");
        }

        for (int i = 0; i < partTypes.Length; i++)
        {
            if (shape.CompositeParts[i].KeyType != partTypes[i])
            {
                throw new InvalidOperationException($"Composite index '{Name}' part {i + 1} stores {shape.CompositeParts[i].KeyType.FullName}, not {partTypes[i].FullName}.");
            }
        }
    }

    /// <summary>
    /// Inserts one routed composite key and identity into the tier tree.<br/>
    /// Each part advances one mini-router level; the identity is stored only on the terminal node for the complete composite path.<br/>
    /// </summary>
    /// <param name="key">The composite key values to route.</param>
    /// <param name="identity">The identity to store at the terminal path.</param>
    /// <returns>An insert result describing whether a new terminal identity was added.</returns>
    public LibraDexGenericInsertResult Insert(LibraDexCompositeKey key, object identity)
    {
        return AddEntry(key, identity, persist: true);
    }

    /// <summary>
    /// Adds one routed composite key and identity to the tier tree.<br/>
    /// This is the preferred public spelling for ordinary composite index population and delegates to <see cref="Insert(LibraDexCompositeKey, object)"/> so durability and duplicate-key behavior remain identical.<br/>
    /// </summary>
    /// <param name="key">The composite key values to route.</param>
    /// <param name="identity">The identity to store at the terminal path.</param>
    /// <returns>An insert result describing whether a new terminal identity was added.</returns>
    public LibraDexGenericInsertResult Add(LibraDexCompositeKey key, object identity)
    {
        return Insert(key, identity);
    }

    /// <summary>
    /// Adds one composite key and identity to the routed tier tree.<br/>
    /// The helper is shared by normal inserts and snapshot hydration so reopened entries can rebuild memory state without recursively rewriting the durable snapshot.<br/>
    /// </summary>
    /// <param name="key">The composite key values to route.</param>
    /// <param name="identity">The identity to store at the terminal path.</param>
    /// <param name="persist">Whether to persist a replacement snapshot after a successful insert.</param>
    /// <returns>An insert result describing whether a terminal identity was added.</returns>
    private LibraDexGenericInsertResult AddEntry(LibraDexCompositeKey key, object identity, bool persist)
    {
        ArgumentNullException.ThrowIfNull(key);
        ValidateIdentity(identity);
        key.ValidateAgainst(shape);
        CompositeNode node = root;
        CompositeNode[] path = persist && session is not null && slotIndex is not null
            ? new CompositeNode[shape.CompositeParts.Count + 1]
            : Array.Empty<CompositeNode>();
        if (path.Length != 0)
        {
            path[0] = root;
        }

        for (int i = 0; i < shape.CompositeParts.Count; i++)
        {
            EnsureNodeLoaded(node, i);
            CompositePartKey partKey = CreatePartKey(shape.CompositeParts[i], key.Values[i].Value);
            node = node.GetOrAdd(partKey);
            if (path.Length != 0)
            {
                path[i + 1] = node;
            }
        }

        EnsureNodeLoaded(node, shape.CompositeParts.Count);
        bool inserted = node.AddIdentity(identity, KeyContract);
        if (inserted)
        {
            itemCount++;
        }

        if (inserted && persist && session is not null && slotIndex is int durableSlotIndex)
        {
            if (path.Length != 0 && root.DurableOffset > 0)
            {
                PersistPathCopy(durableSlotIndex, path);
            }
            else
            {
                PersistSnapshot(durableSlotIndex);
            }
        }

        return new LibraDexGenericInsertResult(inserted, CreatedInitialShelfRoute: false, default, default);
    }

    /// <summary>
    /// Inserts one runtime composite key and runtime identity after strict shape validation.<br/>
    /// </summary>
    /// <param name="key">The runtime key, which must be a <see cref="LibraDexCompositeKey"/>.</param>
    /// <param name="identity">The runtime identity matching the shape identity type.</param>
    /// <returns>The insert result.</returns>
    public LibraDexGenericInsertResult Insert(object? key, object identity)
    {
        return Insert(
            key as LibraDexCompositeKey ?? throw new ArgumentException("Composite indexes require LibraDexCompositeKey keys.", nameof(key)),
            identity);
    }

    /// <summary>
    /// Deletes one runtime composite key/identity tuple after validating the key container and identity type.<br/>
    /// This non-generic overload satisfies the dynamic `IIndex` mutation surface without requiring generated callers to downcast to the concrete composite type.<br/>
    /// </summary>
    /// <param name="key">The runtime key, which must be a <see cref="LibraDexCompositeKey"/>.</param>
    /// <param name="identity">The runtime identity matching the composite index identity type.</param>
    /// <returns><see langword="true"/> when one tuple was removed.</returns>
    public bool Delete(object? key, object identity)
    {
        return Delete(
            key as LibraDexCompositeKey ?? throw new ArgumentException("Composite indexes require LibraDexCompositeKey keys.", nameof(key)),
            identity);
    }

    /// <summary>
    /// Deletes one known composite key/identity tuple.<br/>
    /// This direct helper is for callers that already know both sides of the tuple and should avoid a condition traversal.<br/>
    /// Only the exact identity at the complete composite key is removed; sibling identities under the same key are preserved for non-unique composite indexes.<br/>
    /// </summary>
    /// <param name="key">The complete composite key side of the tuple to delete.</param>
    /// <param name="identity">The identity side of the tuple to delete.</param>
    /// <returns><see langword="true"/> when one tuple was removed.</returns>
    public bool Delete(LibraDexCompositeKey key, object identity)
    {
        return DeleteExactTuple(key, identity);
    }

    /// <summary>
    /// Re-keys one known identity when the caller also knows the old composite key.<br/>
    /// The method first verifies that the old exact tuple exists, then inserts or verifies the replacement tuple, and only then removes the old exact tuple.<br/>
    /// If the replacement cannot be created because of uniqueness or shape constraints, the original tuple is left unchanged.<br/>
    /// </summary>
    /// <param name="identity">The identity to re-key.</param>
    /// <param name="oldKey">The current complete composite key associated with the identity.</param>
    /// <param name="newKey">The replacement complete composite key to associate with the identity.</param>
    /// <returns><see langword="true"/> when the old tuple existed and was removed after the replacement was available.</returns>
    public bool Rekey(object identity, LibraDexCompositeKey oldKey, LibraDexCompositeKey newKey)
    {
        if (LibraDexObjectTuple.ValueEquals(oldKey, newKey))
        {
            return false;
        }

        if (!ContainsExactTuple(oldKey, identity))
        {
            return false;
        }

        if (!ContainsExactTuple(newKey, identity))
        {
            LibraDexGenericInsertResult insert = Insert(newKey, identity);
            if (!insert.Inserted && !ContainsExactTuple(newKey, identity))
            {
                throw new InvalidOperationException("Composite rekey could not create the replacement tuple; the original tuple was left unchanged.");
            }
        }

        return DeleteExactTuple(oldKey, identity);
    }

    /// <summary>
    /// Re-keys one runtime identity when the caller also knows the old and new runtime composite keys.<br/>
    /// This non-generic overload satisfies the dynamic `IIndex` mutation surface without requiring generated callers to downcast to the concrete composite type.<br/>
    /// </summary>
    /// <param name="identity">The runtime identity to re-key.</param>
    /// <param name="oldKey">The current runtime key, which must be a <see cref="LibraDexCompositeKey"/>.</param>
    /// <param name="newKey">The replacement runtime key, which must be a <see cref="LibraDexCompositeKey"/>.</param>
    /// <returns><see langword="true"/> when the old tuple existed and was removed after the replacement tuple was available.</returns>
    public bool Rekey(object identity, object? oldKey, object? newKey)
    {
        return Rekey(
            identity,
            oldKey as LibraDexCompositeKey ?? throw new ArgumentException("Composite indexes require LibraDexCompositeKey keys.", nameof(oldKey)),
            newKey as LibraDexCompositeKey ?? throw new ArgumentException("Composite indexes require LibraDexCompositeKey keys.", nameof(newKey)));
    }

    /// <summary>
    /// Re-keys one identity when the caller does not know the current composite key.<br/>
    /// The current implementation scans visible composite entries to discover old keys, then applies the exact old-key rekey primitive to each matching tuple.<br/>
    /// A maintained reverse identity lookup can optimize this later without changing the public call shape.<br/>
    /// </summary>
    /// <param name="identity">The identity to re-key.</param>
    /// <param name="newKey">The replacement complete composite key to associate with the identity.</param>
    /// <returns>The number of old tuples removed after the replacement tuple was available.</returns>
    public long Rekey(object identity, LibraDexCompositeKey newKey)
    {
        ValidateIdentity(identity);
        newKey.ValidateAgainst(shape);
        LibraDexCompositeKey[] oldKeys = EnumerateEntries()
            .Where(entry => LibraDexObjectTuple.ValueEquals(entry.Identity, identity))
            .Select(entry => entry.Key)
            .ToArray();
        long changed = 0;
        for (int i = 0; i < oldKeys.Length; i++)
        {
            if (Rekey(identity, oldKeys[i], newKey))
            {
                changed++;
            }
        }

        return changed;
    }

    /// <summary>
    /// Re-keys one runtime identity when the caller does not know the old composite key.<br/>
    /// This non-generic overload scans visible composite entries today and can later route through a maintained reverse identity lookup without changing `IIndex` callers.<br/>
    /// </summary>
    /// <param name="identity">The runtime identity to re-key.</param>
    /// <param name="newKey">The replacement runtime key, which must be a <see cref="LibraDexCompositeKey"/>.</param>
    /// <returns>The number of old tuples removed after replacement tuples were available.</returns>
    public long Rekey(object identity, object? newKey)
    {
        return Rekey(
            identity,
            newKey as LibraDexCompositeKey ?? throw new ArgumentException("Composite indexes require LibraDexCompositeKey keys.", nameof(newKey)));
    }

    /// <summary>
    /// Prepares exact composite keys for membership-style condition leaves.<br/>
    /// </summary>
    /// <param name="keys">The runtime composite keys to validate.</param>
    /// <returns>A prepared object set over validated composite keys.</returns>
    public LibraDexPreparedObjectSet PrepareInSet(IEnumerable<object> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        object[] captured = keys.Select(key =>
        {
            LibraDexCompositeKey compositeKey = key as LibraDexCompositeKey
                ?? throw new ArgumentException("Composite membership requires LibraDexCompositeKey values.", nameof(keys));
            compositeKey.ValidateAgainst(shape);
            return (object)compositeKey;
        }).ToArray();
        return new LibraDexPreparedObjectSet(typeof(LibraDexCompositeKey), captured);
    }

    IEnumerable<object> IIdentityPrimitiveExecutor.IterateIdentityPrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        return IterateIdentityPrimitive(request);
    }

    IReadOnlyList<object> IIdentityPrimitiveExecutor.ExecuteIdentityPrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        return IterateIdentityPrimitive(request).ToArray();
    }

    long IIdentityPrimitiveExecutor.CountIdentityPrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        return IterateIdentityPrimitive(request).LongCount();
    }

    LibraDexIdentityMutationResult IIdentityPrimitiveMutator.DeleteIdentityPrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        long deleted = request.CriteriaKind switch
        {
            LibraDexCriteriaKind.All => DeleteAll(),
            LibraDexCriteriaKind.Find => DeleteExact(RequireCompositeKey(request.Values, 0)),
            LibraDexCriteriaKind.CompositeMatch => DeleteCompositeMatch(RequireCompositePredicate(request.Values, 0)),
            _ => throw new NotSupportedException($"{request.CriteriaKind} is not connected to routed composite deletion.")
        };

        return new LibraDexIdentityMutationResult(
            LibraDexCriteriaMutationKind.Delete,
            MatchedCount: deleted,
            ChangedCount: deleted,
            new LibraDexQueryDiagnostics(
                request.CriteriaKind == LibraDexCriteriaKind.CompositeMatch ? LibraDexExecutionKind.FastPath : LibraDexExecutionKind.FastPath,
                RowsScanned: deleted,
                RowsReturned: deleted));
    }

    IReadOnlyList<LibraDexObjectTuple> IIdentityPrimitiveTupleExecutor.ExecuteTuplePrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        return request.CriteriaKind switch
        {
            LibraDexCriteriaKind.All => EnumerateEntries()
                .Select(entry => new LibraDexObjectTuple(entry.Key, entry.Identity))
                .ToArray(),
            LibraDexCriteriaKind.Find => EnumerateExactTuples(RequireCompositeKey(request.Values, 0)).ToArray(),
            LibraDexCriteriaKind.CompositeMatch => EnumerateCompositeMatchTuples(RequireCompositePredicate(request.Values, 0)).ToArray(),
            _ => throw new NotSupportedException($"{request.CriteriaKind} is not connected to routed composite tuple execution.")
        };
    }

    bool IIdentityExactTupleMutator.ContainsExactTuple(object? key, object identity)
    {
        return ContainsExactTuple(
            key as LibraDexCompositeKey ?? throw new ArgumentException("Composite indexes require LibraDexCompositeKey keys.", nameof(key)),
            identity);
    }

    bool IIdentityExactTupleMutator.DeleteExactTuple(object? key, object identity)
    {
        return DeleteExactTuple(
            key as LibraDexCompositeKey ?? throw new ArgumentException("Composite indexes require LibraDexCompositeKey keys.", nameof(key)),
            identity);
    }

    IReadOnlyList<object> IIdentityPrimitiveExecutor.ExecuteAllIdentities()
    {
        return EnumerateIdentities(root, tier: 0).ToArray();
    }

    IEnumerable<object> IIdentityPrimitiveExecutor.IterateIdentityUniverse()
    {
        return EnumerateIdentities(root, tier: 0);
    }

    private IEnumerable<object> IterateIdentityPrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        return request.CriteriaKind switch
        {
            LibraDexCriteriaKind.All => ApplyTake(EnumerateIdentities(root, tier: 0), request.TakeLimit),
            LibraDexCriteriaKind.Find => ApplyTake(FindExact(RequireCompositeKey(request.Values, 0)), request.TakeLimit),
            LibraDexCriteriaKind.CompositeMatch => ApplyTake(FindCompositeMatch(RequireCompositePredicate(request.Values, 0)), request.TakeLimit),
            _ => throw new NotSupportedException($"{request.CriteriaKind} is not connected to routed composite execution.")
        };
    }

    private IEnumerable<object> FindExact(LibraDexCompositeKey key)
    {
        key.ValidateAgainst(shape);
        CompositeNode? node = root;
        for (int i = 0; i < shape.CompositeParts.Count; i++)
        {
            CompositePartKey partKey = CreatePartKey(shape.CompositeParts[i], key.Values[i].Value);
            if (!TryGetChildForExact(node, i, partKey, out node))
            {
                yield break;
            }
        }

        EnsureNodeLoaded(node, shape.CompositeParts.Count);
        foreach (object identity in node.Identities)
        {
            yield return identity;
        }
    }

    /// <summary>
    /// Enumerates exact key/identity tuples stored at one complete composite key.<br/>
    /// Criteria-scoped re-key uses this tuple path so it can preserve the original composite key while replacing only exact old tuples later.<br/>
    /// </summary>
    /// <param name="key">The complete composite key to locate.</param>
    /// <returns>The matching key/identity tuples.</returns>
    private IEnumerable<LibraDexObjectTuple> EnumerateExactTuples(LibraDexCompositeKey key)
    {
        foreach (object identity in FindExact(key))
        {
            yield return new LibraDexObjectTuple(key, identity);
        }
    }

    /// <summary>
    /// Deletes every tuple from this composite index.<br/>
    /// This is the mutation counterpart to the `All` primitive and clears the routed component tree while preserving the configured shape and durable catalog slot.<br/>
    /// </summary>
    /// <returns>The number of terminal key/identity tuples removed.</returns>
    private long DeleteAll()
    {
        EnsureNodeLoaded(root, tier: 0);
        long deleted = itemCount;
        if (deleted == 0)
        {
            return 0;
        }

        root.Clear();
        itemCount = 0;
        PersistAfterWholeTreeMutation();
        return deleted;
    }

    /// <summary>
    /// Deletes every identity stored at one exact composite key path.<br/>
    /// The route walk mirrors exact retrieval, including lazy node-page lookup, and only clears the terminal identity run for the supplied complete composite key.<br/>
    /// </summary>
    /// <param name="key">The complete composite key whose terminal identity run should be removed.</param>
    /// <returns>The number of key/identity tuples removed.</returns>
    private long DeleteExact(LibraDexCompositeKey key)
    {
        key.ValidateAgainst(shape);
        CompositeNode? node = root;
        for (int i = 0; i < shape.CompositeParts.Count; i++)
        {
            EnsureNodeLoaded(node, i);
            CompositePartKey partKey = CreatePartKey(shape.CompositeParts[i], key.Values[i].Value);
            if (!node.TryGet(partKey, out node))
            {
                return 0;
            }
        }

        EnsureNodeLoaded(node, shape.CompositeParts.Count);
        long deleted = node.ClearIdentities();
        if (deleted == 0)
        {
            return 0;
        }

        itemCount -= deleted;
        PersistAfterWholeTreeMutation();
        return deleted;
    }

    /// <summary>
    /// Tests whether one exact composite key/identity tuple is present.<br/>
    /// The route walk mirrors exact retrieval and compares identities structurally so binary identities do not depend on array reference equality.<br/>
    /// </summary>
    /// <param name="key">The complete composite key to locate.</param>
    /// <param name="identity">The identity value to test at the terminal node.</param>
    /// <returns><see langword="true"/> when the exact tuple exists.</returns>
    private bool ContainsExactTuple(LibraDexCompositeKey key, object identity)
    {
        key.ValidateAgainst(shape);
        ValidateIdentity(identity);
        CompositeNode? node = root;
        for (int i = 0; i < shape.CompositeParts.Count; i++)
        {
            CompositePartKey partKey = CreatePartKey(shape.CompositeParts[i], key.Values[i].Value);
            if (!TryGetChildForExact(node, i, partKey, out node))
            {
                return false;
            }
        }

        EnsureNodeLoaded(node, shape.CompositeParts.Count);
        foreach (object current in node.Identities)
        {
            if (LibraDexObjectTuple.ValueEquals(current, identity))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Deletes one exact composite key/identity tuple without removing sibling identities stored under the same composite key.<br/>
    /// This is the composite counterpart to scalar exact tuple deletion and is required before criteria-scoped `SetKey` can safely replace composite keys.<br/>
    /// </summary>
    /// <param name="key">The complete composite key to locate.</param>
    /// <param name="identity">The identity value to remove at the terminal node.</param>
    /// <returns><see langword="true"/> when one tuple was removed.</returns>
    private bool DeleteExactTuple(LibraDexCompositeKey key, object identity)
    {
        key.ValidateAgainst(shape);
        ValidateIdentity(identity);
        CompositeNode? node = root;
        for (int i = 0; i < shape.CompositeParts.Count; i++)
        {
            EnsureNodeLoaded(node, i);
            CompositePartKey partKey = CreatePartKey(shape.CompositeParts[i], key.Values[i].Value);
            if (!node.TryGet(partKey, out node))
            {
                return false;
            }
        }

        EnsureNodeLoaded(node, shape.CompositeParts.Count);
        if (!node.RemoveIdentity(identity))
        {
            return false;
        }

        itemCount--;
        PersistAfterWholeTreeMutation();
        return true;
    }

    private IEnumerable<object> FindCompositeMatch(LibraDexCompositePredicate predicate)
    {
        predicate.ValidateAgainst(shape);
        object?[] values = predicate.HasFullKeyCriterion
            ? new object?[shape.CompositeParts.Count]
            : Array.Empty<object?>();
        foreach (object identity in Traverse(root, tier: 0, predicate, values))
        {
            yield return identity;
        }
    }

    /// <summary>
    /// Enumerates key/identity tuples matched by one composite predicate.<br/>
    /// This mirrors `CompositeMatch` identity retrieval but reconstructs the full composite key at terminal paths so mutation can later delete exact old tuples.<br/>
    /// </summary>
    /// <param name="predicate">The composite predicate materialized from the condition builder.</param>
    /// <returns>The matching key/identity tuples.</returns>
    private IEnumerable<LibraDexObjectTuple> EnumerateCompositeMatchTuples(LibraDexCompositePredicate predicate)
    {
        predicate.ValidateAgainst(shape);
        object?[] values = new object?[shape.CompositeParts.Count];
        foreach (LibraDexObjectTuple tuple in EnumerateCompositeMatchTuples(root, tier: 0, predicate, values))
        {
            yield return tuple;
        }
    }

    /// <summary>
    /// Recursively enumerates composite tuples while applying the same route-pruned predicates used by identity retrieval.<br/>
    /// The reusable value buffer is always populated because tuple mutation needs the original full key even when no full-key predicate is present.<br/>
    /// </summary>
    /// <param name="node">The current routed node.</param>
    /// <param name="tier">The current composite part ordinal.</param>
    /// <param name="predicate">The composite predicate being applied.</param>
    /// <param name="values">The reusable routed path buffer.</param>
    /// <returns>The matching key/identity tuples below the node.</returns>
    private IEnumerable<LibraDexObjectTuple> EnumerateCompositeMatchTuples(
        CompositeNode node,
        int tier,
        LibraDexCompositePredicate predicate,
        object?[] values)
    {
        if (tier >= shape.CompositeParts.Count)
        {
            EnsureNodeLoaded(node, tier);
            if (MatchesFullKeyIfNeeded(predicate, values))
            {
                LibraDexCompositeKey key = LibraDexCompositeKey.Of(values.ToArray());
                foreach (object identity in node.Identities)
                {
                    yield return new LibraDexObjectTuple(key, identity);
                }
            }

            yield break;
        }

        LibraDexCompositeKeyPartSpec part = shape.CompositeParts[tier];
        if (predicate.TryGetPart(part.Name, out LibraDexCompositePartCriterion? criterion))
        {
            foreach (CompositeNode child in MatchedChildren(node, tier, criterion!))
            {
                values[tier] = child.RouteValue ?? throw new InvalidDataException("Composite tuple traversal encountered a child route without a component value.");
                foreach (LibraDexObjectTuple tuple in EnumerateCompositeMatchTuples(child, tier + 1, predicate, values))
                {
                    yield return tuple;
                }

                values[tier] = null;
            }

            yield break;
        }

        EnsureNodeLoaded(node, tier);
        foreach (CompositeChild child in node.Children)
        {
            values[tier] = child.Node.RouteValue ?? throw new InvalidDataException("Composite tuple traversal encountered a child route without a component value.");
            foreach (LibraDexObjectTuple tuple in EnumerateCompositeMatchTuples(child.Node, tier + 1, predicate, values))
            {
                yield return tuple;
            }

            values[tier] = null;
        }
    }

    private IEnumerable<object> Traverse(
        CompositeNode node,
        int tier,
        LibraDexCompositePredicate predicate,
        object?[] fullKeyValues)
    {
        if (tier >= shape.CompositeParts.Count)
        {
            EnsureNodeLoaded(node, tier);
            if (MatchesFullKeyIfNeeded(predicate, fullKeyValues))
            {
                foreach (object identity in node.Identities)
                {
                    yield return identity;
                }
            }

            yield break;
        }

        LibraDexCompositeKeyPartSpec part = shape.CompositeParts[tier];
        if (predicate.TryGetPart(part.Name, out LibraDexCompositePartCriterion? criterion))
        {
            foreach (CompositeNode child in MatchedChildren(node, tier, criterion!))
            {
                SetFullKeyValue(fullKeyValues, tier, child);
                foreach (object identity in Traverse(child, tier + 1, predicate, fullKeyValues))
                {
                    yield return identity;
                }

                ClearFullKeyValue(fullKeyValues, tier);
            }

            yield break;
        }

        EnsureNodeLoaded(node, tier);
        foreach (CompositeChild child in node.Children)
        {
            SetFullKeyValue(fullKeyValues, tier, child.Node);
            foreach (object identity in Traverse(child.Node, tier + 1, predicate, fullKeyValues))
            {
                yield return identity;
            }

            ClearFullKeyValue(fullKeyValues, tier);
        }
    }

    /// <summary>
    /// Deletes tuples matched by one composite predicate.<br/>
    /// The traversal uses the same part-aware route pruning as `CompositeMatch` retrieval and removes identities only at terminal paths whose named and full-key predicates all pass.<br/>
    /// </summary>
    /// <param name="predicate">The composite predicate materialized from the condition builder.</param>
    /// <returns>The number of terminal key/identity tuples removed.</returns>
    private long DeleteCompositeMatch(LibraDexCompositePredicate predicate)
    {
        predicate.ValidateAgainst(shape);
        object?[] values = predicate.HasFullKeyCriterion
            ? new object?[shape.CompositeParts.Count]
            : Array.Empty<object?>();
        long deleted = DeleteCompositeMatch(root, tier: 0, predicate, values);
        if (deleted == 0)
        {
            return 0;
        }

        itemCount -= deleted;
        PersistAfterWholeTreeMutation();
        return deleted;
    }

    /// <summary>
    /// Recursively deletes terminal identities below one routed composite node.<br/>
    /// The method intentionally hydrates the current node before matching children so deletion mutates the canonical in-memory route nodes rather than transient page-directory placeholders.<br/>
    /// </summary>
    /// <param name="node">The current routed node.</param>
    /// <param name="tier">The current composite part ordinal.</param>
    /// <param name="predicate">The composite predicate being applied.</param>
    /// <param name="fullKeyValues">The reusable full-key path buffer.</param>
    /// <returns>The number of tuples removed below this node.</returns>
    private long DeleteCompositeMatch(
        CompositeNode node,
        int tier,
        LibraDexCompositePredicate predicate,
        object?[] fullKeyValues)
    {
        EnsureNodeLoaded(node, tier);
        if (tier >= shape.CompositeParts.Count)
        {
            if (MatchesFullKeyIfNeeded(predicate, fullKeyValues))
            {
                return node.ClearIdentities();
            }

            return 0;
        }

        long deleted = 0;
        LibraDexCompositeKeyPartSpec part = shape.CompositeParts[tier];
        if (predicate.TryGetPart(part.Name, out LibraDexCompositePartCriterion? criterion))
        {
            foreach (CompositeNode child in MatchedChildren(node, tier, criterion!).ToArray())
            {
                SetFullKeyValue(fullKeyValues, tier, child);
                deleted += DeleteCompositeMatch(child, tier + 1, predicate, fullKeyValues);
                ClearFullKeyValue(fullKeyValues, tier);
            }

            return deleted;
        }

        foreach (CompositeChild child in node.Children)
        {
            SetFullKeyValue(fullKeyValues, tier, child.Node);
            deleted += DeleteCompositeMatch(child.Node, tier + 1, predicate, fullKeyValues);
            ClearFullKeyValue(fullKeyValues, tier);
        }

        return deleted;
    }

    /// <summary>
    /// Applies the explicit full-key predicate when one is present.<br/>
    /// The full-key value is rendered only at terminal paths and only for predicates that explicitly requested whole-composite matching.<br/>
    /// </summary>
    /// <param name="predicate">The composite predicate that may contain a full-key criterion.</param>
    /// <param name="fullKeyValues">The current routed path values in composite part order.</param>
    /// <returns><see langword="true"/> when no full-key predicate exists or the rendered key matches it.</returns>
    private bool MatchesFullKeyIfNeeded(LibraDexCompositePredicate predicate, object?[] fullKeyValues)
    {
        if (!predicate.TryGetFullKeyPart(out LibraDexCompositePartCriterion? fullKeyCriterion))
        {
            return true;
        }

        LibraDexCompositePartCriterion criterion = fullKeyCriterion!;
        byte[] fullKey = EncodeFullKey(shape, fullKeyValues, criterion);
        return MatchesFullKeyBytes(shape, fullKey, criterion);
    }

    /// <summary>
    /// Stores one routed component value into the reusable full-key path buffer.<br/>
    /// </summary>
    /// <param name="fullKeyValues">The reusable full-key path buffer.</param>
    /// <param name="tier">The component tier being visited.</param>
    /// <param name="child">The child route node whose route value should be captured.</param>
    private static void SetFullKeyValue(object?[] fullKeyValues, int tier, CompositeNode child)
    {
        if (fullKeyValues.Length != 0)
        {
            fullKeyValues[tier] = child.RouteValue ?? throw new InvalidDataException("FullKey composite traversal encountered a child route without a component value.");
        }
    }

    /// <summary>
    /// Clears one routed component value from the reusable full-key path buffer after traversal returns from that child.<br/>
    /// </summary>
    /// <param name="fullKeyValues">The reusable full-key path buffer.</param>
    /// <param name="tier">The component tier to clear.</param>
    private static void ClearFullKeyValue(object?[] fullKeyValues, int tier)
    {
        if (fullKeyValues.Length != 0)
        {
            fullKeyValues[tier] = null;
        }
    }

    /// <summary>
    /// Enumerates child nodes matching one composite part criterion.<br/>
    /// Lazy durable nodes use page-native exact lookup for ordinal equality, and otherwise decode only the current page's child directory into transient child placeholders before deeper traversal.<br/>
    /// </summary>
    /// <param name="node">The current composite node.</param>
    /// <param name="tier">The current composite tier.</param>
    /// <param name="criterion">The criterion bound to this tier.</param>
    /// <returns>Child nodes whose component value matches the requested criterion.</returns>
    private IEnumerable<CompositeNode> MatchedChildren(CompositeNode node, int tier, LibraDexCompositePartCriterion criterion)
    {
        if (CanUseOrdinalExactPageLookup(criterion))
        {
            CompositePartKey key = CreatePartKey(shape.CompositeParts[tier], RequireValue(criterion.Values, 0));
            if (TryGetChildForExact(node, tier, key, out CompositeNode exactChild))
            {
                yield return exactChild;
            }

            yield break;
        }

        if (!node.IsLoaded &&
            CanUsePageRangeLookup(criterion) &&
            TryReadNodePageBytes(node, out byte[] pageBytes))
        {
            CompositePartKey lower = CreatePartKey(shape.CompositeParts[tier], RequireValue(criterion.Values, 0));
            CompositePartKey upper = CreatePartKey(shape.CompositeParts[tier], RequireValue(criterion.Values, 1));
            if (LibraDexCompositeNodePageCodec.TryEnumerateChildRange(
                shape,
                pageBytes,
                tier,
                lower.Value,
                upper.Value,
                out IReadOnlyList<LibraDexCompositeNodeChildPage> rangeChildren))
            {
                for (int i = 0; i < rangeChildren.Count; i++)
                {
                    CompositeNode child = new(rangeChildren[i].Value);
                    child.MarkUnloaded(rangeChildren[i].Offset);
                    yield return child;
                }

                yield break;
            }
        }

        if (!node.IsLoaded &&
            CanUsePagePrefixLookup(criterion) &&
            TryReadNodePageBytes(node, out byte[] prefixPageBytes) &&
            LibraDexCompositeNodePageCodec.TryEnumerateChildPrefix(
                shape,
                prefixPageBytes,
                tier,
                RequireString(criterion.Values, 0),
                out IReadOnlyList<LibraDexCompositeNodeChildPage> prefixChildren))
        {
            for (int i = 0; i < prefixChildren.Count; i++)
            {
                CompositeNode child = new(prefixChildren[i].Value);
                child.MarkUnloaded(prefixChildren[i].Offset);
                yield return child;
            }

            yield break;
        }

        if (!node.IsLoaded &&
            CanUsePageBoundLookup(criterion) &&
            TryReadNodePageBytes(node, out byte[] boundPageBytes) &&
            LibraDexCompositeNodePageCodec.TryEnumerateChildBound(
                shape,
                boundPageBytes,
                tier,
                CreatePartKey(shape.CompositeParts[tier], RequireValue(criterion.Values, 0)).Value,
                IsLowerBoundOperator(criterion.Operator),
                IsInclusiveBoundOperator(criterion.Operator),
                out IReadOnlyList<LibraDexCompositeNodeChildPage> boundChildren))
        {
            for (int i = 0; i < boundChildren.Count; i++)
            {
                CompositeNode child = new(boundChildren[i].Value);
                child.MarkUnloaded(boundChildren[i].Offset);
                yield return child;
            }

            yield break;
        }

        if (!node.IsLoaded && TryReadNodePage(node, tier, out LibraDexCompositeNodePage page))
        {
            for (int i = 0; i < page.Children.Count; i++)
            {
                LibraDexCompositeNodeChildPage childPage = page.Children[i];
                if (!MatchesPart(shape.CompositeParts[tier], childPage.Value, criterion))
                {
                    continue;
                }

                CompositeNode child = new(childPage.Value);
                child.MarkUnloaded(childPage.Offset);
                yield return child;
            }

            yield break;
        }

        EnsureNodeLoaded(node, tier);
        foreach (CompositeChild child in node.Children)
        {
            if (MatchesPart(shape.CompositeParts[tier], child.Key.Value, criterion))
            {
                yield return child.Node;
            }
        }
    }

    /// <summary>
    /// Determines whether a composite part criterion can use ordinal exact page lookup without residual managed comparison.<br/>
    /// String equality with culture or ignore-case options must remain on the residual path until folded/sort-key composite tier projections are connected.<br/>
    /// </summary>
    /// <param name="criterion">The tier criterion to inspect.</param>
    /// <returns><see langword="true"/> when child-directory binary search is semantically exact for the criterion.</returns>
    private static bool CanUseOrdinalExactPageLookup(LibraDexCompositePartCriterion criterion)
    {
        return criterion.Operator == LibraDexConditionOperatorKind.EqualTo &&
            (criterion.ValueKind != LibraDexConditionValueKind.String ||
                (!criterion.IgnoreCase && string.IsNullOrWhiteSpace(criterion.Culture)));
    }

    /// <summary>
    /// Determines whether a composite part criterion can use ordered durable child-directory range traversal.<br/>
    /// Culture-aware or ignore-case string ranges remain residual scans until folded or sort-key composite tier projections are connected.<br/>
    /// </summary>
    /// <param name="criterion">The tier criterion to inspect.</param>
    /// <returns><see langword="true"/> when child-directory range traversal is semantically aligned with the criterion.</returns>
    private static bool CanUsePageRangeLookup(LibraDexCompositePartCriterion criterion)
    {
        return criterion.Operator == LibraDexConditionOperatorKind.Between &&
            (criterion.ValueKind != LibraDexConditionValueKind.String ||
                (!criterion.IgnoreCase && string.IsNullOrWhiteSpace(criterion.Culture)));
    }

    /// <summary>
    /// Determines whether a composite part criterion can use ordered durable child-directory prefix traversal.<br/>
    /// Only ordinal case-sensitive string prefixes are eligible until folded or sort-key composite tier projections are connected.<br/>
    /// </summary>
    /// <param name="criterion">The tier criterion to inspect.</param>
    /// <returns><see langword="true"/> when child-directory prefix traversal is semantically aligned with the criterion.</returns>
    private static bool CanUsePagePrefixLookup(LibraDexCompositePartCriterion criterion)
    {
        return criterion.Operator == LibraDexConditionOperatorKind.StartsWith &&
            criterion.ValueKind == LibraDexConditionValueKind.String &&
            !criterion.IgnoreCase &&
            string.IsNullOrWhiteSpace(criterion.Culture);
    }

    /// <summary>
    /// Determines whether a composite part criterion can use one-sided durable child-directory bound traversal.<br/>
    /// Culture-aware or ignore-case string bounds remain residual scans until folded or sort-key composite tier projections are connected.<br/>
    /// </summary>
    /// <param name="criterion">The tier criterion to inspect.</param>
    /// <returns><see langword="true"/> when child-directory bound traversal is semantically aligned with the criterion.</returns>
    private static bool CanUsePageBoundLookup(LibraDexCompositePartCriterion criterion)
    {
        return (criterion.Operator == LibraDexConditionOperatorKind.GreaterThan ||
                criterion.Operator == LibraDexConditionOperatorKind.GreaterOrEqual ||
                criterion.Operator == LibraDexConditionOperatorKind.LessThan ||
                criterion.Operator == LibraDexConditionOperatorKind.LessOrEqual) &&
            (criterion.ValueKind != LibraDexConditionValueKind.String ||
                (!criterion.IgnoreCase && string.IsNullOrWhiteSpace(criterion.Culture)));
    }

    /// <summary>
    /// Identifies whether a one-sided comparison operator represents a lower-bound traversal.<br/>
    /// Greater-than variants start from a located directory position and continue forward, while less-than variants start at the first child and stop at the bound.<br/>
    /// </summary>
    /// <param name="operatorKind">The comparison operator to classify.</param>
    /// <returns><see langword="true"/> for greater-than operators; otherwise <see langword="false"/>.</returns>
    private static bool IsLowerBoundOperator(LibraDexConditionOperatorKind operatorKind)
    {
        return operatorKind == LibraDexConditionOperatorKind.GreaterThan ||
            operatorKind == LibraDexConditionOperatorKind.GreaterOrEqual;
    }

    /// <summary>
    /// Identifies whether a one-sided comparison operator includes the supplied boundary value.<br/>
    /// The result is passed to the child-directory scanner so equal component values are either retained or skipped without residual filtering.<br/>
    /// </summary>
    /// <param name="operatorKind">The comparison operator to classify.</param>
    /// <returns><see langword="true"/> for inclusive operators; otherwise <see langword="false"/>.</returns>
    private static bool IsInclusiveBoundOperator(LibraDexConditionOperatorKind operatorKind)
    {
        return operatorKind == LibraDexConditionOperatorKind.GreaterOrEqual ||
            operatorKind == LibraDexConditionOperatorKind.LessOrEqual;
    }

    /// <summary>
    /// Reads and decodes the durable page for a lazy composite node without attaching decoded child routes to the in-memory tree.<br/>
    /// This is used by page-native constrained scans where the caller needs the current page's child values but does not want to hydrate unrelated sibling branches into the runtime index state.<br/>
    /// </summary>
    /// <param name="node">The lazy node placeholder.</param>
    /// <param name="tier">The expected tier for the decoded page.</param>
    /// <param name="page">Receives the decoded page when present.</param>
    /// <returns><see langword="true"/> when the page was read and decoded; otherwise <see langword="false"/>.</returns>
    private bool TryReadNodePage(CompositeNode node, int tier, out LibraDexCompositeNodePage page)
    {
        if (TryReadNodePageBytes(node, out byte[] pageBytes))
        {
            page = LibraDexCompositeNodePageCodec.Decode(shape, pageBytes);
            if (page.Tier != tier)
            {
                throw new InvalidDataException("Composite node page tier does not match its routed position.");
            }

            return true;
        }

        page = default;
        return false;
    }

    /// <summary>
    /// Reads the durable bytes for a lazy composite node page without decoding the page body.<br/>
    /// Page-native lookup helpers use this to inspect versioned child directories directly.<br/>
    /// </summary>
    /// <param name="node">The lazy node placeholder.</param>
    /// <param name="pageBytes">Receives the encoded node page bytes when present.</param>
    /// <returns><see langword="true"/> when bytes were read for the node page; otherwise <see langword="false"/>.</returns>
    private bool TryReadNodePageBytes(CompositeNode node, out byte[] pageBytes)
    {
        pageBytes = Array.Empty<byte>();
        return session is not null && session.TryReadCompositeNodePage(node.DurableOffset, out pageBytes);
    }

    private bool MatchesPart(LibraDexCompositeKeyPartSpec part, object value, LibraDexCompositePartCriterion criterion)
    {
        if (TryMatchPartNullState(part, value, criterion, out bool nullStateResult))
        {
            return nullStateResult;
        }

        if (LibraDexCompositeKeyValueSemantics.IsPartNull(part, value))
        {
            return criterion.Operator == LibraDexConditionOperatorKind.NotEqualTo;
        }

        if (criterion.ValueKind == LibraDexConditionValueKind.String)
        {
            return MatchesStringCriterion(
                value as string ?? throw new InvalidOperationException($"Composite part '{criterion.PartName}' is not a string value."),
                criterion);
        }

        if (criterion.ValueKind == LibraDexConditionValueKind.Guid)
        {
            return MatchesGuidCriterion(
                value is Guid guid ? guid : throw new InvalidOperationException($"Composite part '{criterion.PartName}' is not a GUID value."),
                criterion);
        }

        if (criterion.ValueKind == LibraDexConditionValueKind.Binary)
        {
            return MatchesBinaryCriterion(
                value is byte[] bytes ? bytes : throw new InvalidOperationException($"Composite part '{criterion.PartName}' is not a binary value."),
                criterion);
        }

        if (criterion.ValueKind is LibraDexConditionValueKind.DateTime or LibraDexConditionValueKind.DateOnly or LibraDexConditionValueKind.TimeOnly)
        {
            return MatchesStructuredDateCriterion(part, value, criterion);
        }

        object firstValue = RequireValue(criterion.Values, 0);
        return criterion.Operator switch
        {
            LibraDexConditionOperatorKind.EqualTo => Comparer<object>.Default.Compare(value, firstValue) == 0,
            LibraDexConditionOperatorKind.NotEqualTo => Comparer<object>.Default.Compare(value, firstValue) != 0,
            LibraDexConditionOperatorKind.GreaterThan => Comparer<object>.Default.Compare(value, firstValue) > 0,
            LibraDexConditionOperatorKind.GreaterOrEqual => Comparer<object>.Default.Compare(value, firstValue) >= 0,
            LibraDexConditionOperatorKind.LessThan => Comparer<object>.Default.Compare(value, firstValue) < 0,
            LibraDexConditionOperatorKind.LessOrEqual => Comparer<object>.Default.Compare(value, firstValue) <= 0,
            LibraDexConditionOperatorKind.Between => Comparer<object>.Default.Compare(value, firstValue) >= 0 &&
                Comparer<object>.Default.Compare(value, RequireValue(criterion.Values, 1)) <= 0,
            LibraDexConditionOperatorKind.NotBetween => Comparer<object>.Default.Compare(value, firstValue) < 0 ||
                Comparer<object>.Default.Compare(value, RequireValue(criterion.Values, 1)) > 0,
            LibraDexConditionOperatorKind.InSet => MatchesObjectSet(value, firstValue),
            LibraDexConditionOperatorKind.NotInSet => !MatchesObjectSet(value, firstValue),
            LibraDexConditionOperatorKind.BitAndEqualTo => LibraDexBitmaskPredicate
                .Create(value.GetType(), firstValue, RequireValue(criterion.Values, 1), LibraDexBitmaskComparisonMode.EqualTo)
                .Matches(value),
            LibraDexConditionOperatorKind.BitAndNotEqualTo => LibraDexBitmaskPredicate
                .Create(value.GetType(), firstValue, RequireValue(criterion.Values, 1), LibraDexBitmaskComparisonMode.NotEqualTo)
                .Matches(value),
            _ => throw new NotSupportedException($"Composite part operator {criterion.Operator} is not supported in this slice.")
        };
    }

    /// <summary>
    /// Applies explicit null-state predicates to a routed composite component before the ordinary value-family comparers run.<br/>
    /// Composite string and binary parts use <see cref="NullKey"/> states, while scalar, GUID, and date-like parts use <see cref="ScalarNull"/> states.<br/>
    /// </summary>
    /// <param name="part">The composite key part descriptor.</param>
    /// <param name="value">The routed component value.</param>
    /// <param name="criterion">The part-scoped predicate.</param>
    /// <param name="result">Receives the predicate result when the criterion is a null-state predicate.</param>
    /// <returns><see langword="true"/> when the criterion was handled as a null-state predicate.</returns>
    private static bool TryMatchPartNullState(
        LibraDexCompositeKeyPartSpec part,
        object value,
        LibraDexCompositePartCriterion criterion,
        out bool result)
    {
        result = false;
        if (criterion.Values.Count == 0)
        {
            return false;
        }

        object? operand = criterion.Values[0];
        if (operand is NullKey keyState)
        {
            if (!LibraDexCompositeKeyValueSemantics.UsesNullKeyState(part))
            {
                throw new InvalidOperationException($"Composite part '{criterion.PartName}' uses ScalarNull semantics, not NullKey semantics.");
            }

            bool matches = keyState switch
            {
                NullKey.Null => value is NullKey.Null,
                NullKey.Empty => IsEmptyNullKeyValue(value),
                NullKey.NullOrEmpty => value is NullKey.Null || IsEmptyNullKeyValue(value),
                _ => throw new ArgumentOutOfRangeException(nameof(criterion), keyState, "Unknown null key state.")
            };
            result = criterion.Operator switch
            {
                LibraDexConditionOperatorKind.EqualTo => matches,
                LibraDexConditionOperatorKind.NotEqualTo => !matches,
                _ => throw new NotSupportedException($"Composite NullKey predicates do not support operator {criterion.Operator}.")
            };
            return true;
        }

        if (operand is ScalarNull scalarState)
        {
            if (LibraDexCompositeKeyValueSemantics.UsesNullKeyState(part))
            {
                throw new InvalidOperationException($"Composite part '{criterion.PartName}' uses NullKey semantics, not ScalarNull semantics.");
            }

            bool matches = scalarState switch
            {
                ScalarNull.Null => value is ScalarNull.Null,
                ScalarNull.NonNull => value is not ScalarNull.Null,
                _ => throw new ArgumentOutOfRangeException(nameof(criterion), scalarState, "Unknown scalar null state.")
            };
            result = criterion.Operator switch
            {
                LibraDexConditionOperatorKind.EqualTo or LibraDexConditionOperatorKind.ScalarNullState => matches,
                LibraDexConditionOperatorKind.NotEqualTo => !matches,
                _ => throw new NotSupportedException($"Composite ScalarNull predicates do not support operator {criterion.Operator}.")
            };
            return true;
        }

        return false;
    }

    /// <summary>
    /// Determines whether a routed null-key-family component value is the concrete empty route.<br/>
    /// Empty string and empty byte-array routes remain real values, distinct from the explicit null route represented by <see cref="NullKey.Null"/>.<br/>
    /// </summary>
    /// <param name="value">The normalized routed component value.</param>
    /// <returns><see langword="true"/> when the value is an empty string or empty byte array.</returns>
    private static bool IsEmptyNullKeyValue(object value)
    {
        return value switch
        {
            string text => text.Length == 0,
            byte[] bytes => bytes.Length == 0,
            _ => false
        };
    }

    /// <summary>
    /// Applies one GUID criterion to a composite GUID component without formatting it as text.<br/>
    /// Prefix matching compares the stored GUID byte representation directly, preserving the binary GUID contract chosen by the composite part descriptor.<br/>
    /// </summary>
    /// <param name="value">The routed GUID component value.</param>
    /// <param name="criterion">The GUID criterion to apply.</param>
    /// <returns><see langword="true"/> when the GUID satisfies the criterion.</returns>
    private static bool MatchesGuidCriterion(Guid value, LibraDexCompositePartCriterion criterion)
    {
        return criterion.Operator switch
        {
            LibraDexConditionOperatorKind.EqualTo => value == RequireGuid(criterion.Values, 0),
            LibraDexConditionOperatorKind.NotEqualTo => value != RequireGuid(criterion.Values, 0),
            LibraDexConditionOperatorKind.StartsWith => GuidStartsWith(value, RequireGuid(criterion.Values, 0), RequireInt32(criterion.Values, 1)),
            LibraDexConditionOperatorKind.InSet => MatchesGuidSet(value, RequireValue(criterion.Values, 0)),
            LibraDexConditionOperatorKind.NotInSet => !MatchesGuidSet(value, RequireValue(criterion.Values, 0)),
            _ => throw new NotSupportedException($"Composite GUID part operator {criterion.Operator} is not supported in this slice.")
        };
    }

    /// <summary>
    /// Applies one binary criterion to a composite byte-array component without text conversion.<br/>
    /// </summary>
    /// <param name="value">The routed binary component value.</param>
    /// <param name="criterion">The binary criterion to apply.</param>
    /// <returns><see langword="true"/> when the byte-array component satisfies the criterion.</returns>
    private static bool MatchesBinaryCriterion(ReadOnlySpan<byte> value, LibraDexCompositePartCriterion criterion)
    {
        byte[] operand = RequireBytes(criterion.Values, 0);
        return criterion.Operator switch
        {
            LibraDexConditionOperatorKind.EqualTo => value.SequenceEqual(operand),
            LibraDexConditionOperatorKind.NotEqualTo => !value.SequenceEqual(operand),
            LibraDexConditionOperatorKind.StartsWith => value.StartsWith(operand),
            LibraDexConditionOperatorKind.EndsWith => value.EndsWith(operand),
            LibraDexConditionOperatorKind.Contains => value.IndexOf(operand) >= 0,
            _ => throw new NotSupportedException($"Composite binary part operator {criterion.Operator} is not supported in this slice.")
        };
    }

    /// <summary>
    /// Tests a GUID prefix in the stored GUID byte domain.<br/>
    /// The byte count is validated by the public builder and checked defensively here for dynamic descriptor callers.<br/>
    /// </summary>
    /// <param name="candidate">The candidate GUID value from the composite route.</param>
    /// <param name="prefixSource">The GUID that supplies prefix bytes.</param>
    /// <param name="byteCount">The leading byte count to compare.</param>
    /// <returns><see langword="true"/> when the candidate starts with the supplied prefix bytes.</returns>
    private static bool GuidStartsWith(Guid candidate, Guid prefixSource, int byteCount)
    {
        if (byteCount is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(byteCount), byteCount, "GUID prefix byte count must be 1 through 16.");
        }

        Span<byte> candidateBytes = stackalloc byte[16];
        Span<byte> prefixBytes = stackalloc byte[16];
        candidate.TryWriteBytes(candidateBytes);
        prefixSource.TryWriteBytes(prefixBytes);
        return candidateBytes[..byteCount].SequenceEqual(prefixBytes[..byteCount]);
    }

    /// <summary>
    /// Applies one structured date/time criterion to a composite date component by reading packed structured-date fields.<br/>
    /// This keeps composite date matching aligned with top-level structured date indexes and avoids text conversion at query time.<br/>
    /// </summary>
    /// <param name="part">The composite date/time part descriptor whose encoding contract should be honored.</param>
    /// <param name="value">The routed date/time component value.</param>
    /// <param name="criterion">The structured date criterion to apply.</param>
    /// <returns><see langword="true"/> when the component satisfies the criterion.</returns>
    private static bool MatchesStructuredDateCriterion(
        LibraDexCompositeKeyPartSpec part,
        object value,
        LibraDexCompositePartCriterion criterion)
    {
        ulong encoded = EncodeCompositeStructuredDate(value, part.DateTimeKeyEncoding);
        int year = ExtractStructuredYear(encoded);
        int month = ExtractStructuredMonth(encoded);
        int day = ExtractStructuredDay(encoded);
        return criterion.Operator switch
        {
            LibraDexConditionOperatorKind.YearEqualTo => year == RequireInt32(criterion.Values, 0),
            LibraDexConditionOperatorKind.YearRange => year >= RequireInt32(criterion.Values, 0) && year <= RequireInt32(criterion.Values, 1),
            LibraDexConditionOperatorKind.YearMonth => year == RequireInt32(criterion.Values, 0) && month == RequireInt32(criterion.Values, 1),
            LibraDexConditionOperatorKind.YearMonthDay => year == RequireInt32(criterion.Values, 0) && month == RequireInt32(criterion.Values, 1) && day == RequireInt32(criterion.Values, 2),
            LibraDexConditionOperatorKind.MonthEqualTo => month == RequireInt32(criterion.Values, 0),
            LibraDexConditionOperatorKind.DayEqualTo => day == RequireInt32(criterion.Values, 0),
            LibraDexConditionOperatorKind.EqualTo => encoded == EncodeCompositeStructuredDate(RequireValue(criterion.Values, 0), part.DateTimeKeyEncoding),
            LibraDexConditionOperatorKind.NotEqualTo => encoded != EncodeCompositeStructuredDate(RequireValue(criterion.Values, 0), part.DateTimeKeyEncoding),
            LibraDexConditionOperatorKind.GreaterThan => encoded > EncodeCompositeStructuredDate(RequireValue(criterion.Values, 0), part.DateTimeKeyEncoding),
            LibraDexConditionOperatorKind.GreaterOrEqual => encoded >= EncodeCompositeStructuredDate(RequireValue(criterion.Values, 0), part.DateTimeKeyEncoding),
            LibraDexConditionOperatorKind.LessThan => encoded < EncodeCompositeStructuredDate(RequireValue(criterion.Values, 0), part.DateTimeKeyEncoding),
            LibraDexConditionOperatorKind.LessOrEqual => encoded <= EncodeCompositeStructuredDate(RequireValue(criterion.Values, 0), part.DateTimeKeyEncoding),
            LibraDexConditionOperatorKind.Between => encoded >= EncodeCompositeStructuredDate(RequireValue(criterion.Values, 0), part.DateTimeKeyEncoding) &&
                encoded <= EncodeCompositeStructuredDate(RequireValue(criterion.Values, 1), part.DateTimeKeyEncoding),
            LibraDexConditionOperatorKind.NotBetween => encoded < EncodeCompositeStructuredDate(RequireValue(criterion.Values, 0), part.DateTimeKeyEncoding) ||
                encoded > EncodeCompositeStructuredDate(RequireValue(criterion.Values, 1), part.DateTimeKeyEncoding),
            _ => throw new NotSupportedException($"Composite structured date part operator {criterion.Operator} is not supported in this slice.")
        };
    }

    /// <summary>
    /// Encodes one composite date/time component with the part-level DateTime-like key encoding contract.<br/>
    /// The encoded value is used only for component extraction; stored routed values remain the original CLR values.<br/>
    /// </summary>
    /// <param name="value">The routed date/time component value.</param>
    /// <param name="dateTimeKeyEncoding">The DateTime-like encoding contract selected by the composite part.</param>
    /// <returns>The encoded structured date/time scalar.</returns>
    private static ulong EncodeCompositeStructuredDate(object value, DateTimeKeyEncoding dateTimeKeyEncoding)
    {
        return value switch
        {
            DateTime dateTime => LibraDexStructuredDateCodec.Encode(dateTime, dateTimeKeyEncoding),
            DateTimeOffset dateTimeOffset => LibraDexStructuredDateCodec.Encode(dateTimeOffset, dateTimeKeyEncoding),
            DateOnly dateOnly => LibraDexStructuredDateCodec.Encode(dateOnly, dateTimeKeyEncoding),
            TimeOnly timeOnly => LibraDexStructuredDateCodec.Encode(timeOnly, dateTimeKeyEncoding),
            _ => throw new InvalidOperationException($"Composite structured date predicates do not support value type {value.GetType().FullName}.")
        };
    }

    private static int ExtractStructuredYear(ulong encoded)
    {
        return (int)((encoded >> 50) & 0x3FFF);
    }

    private static int ExtractStructuredMonth(ulong encoded)
    {
        return (int)((encoded >> 46) & 0xF);
    }

    private static int ExtractStructuredDay(ulong encoded)
    {
        return (int)((encoded >> 41) & 0x1F);
    }

    /// <summary>
    /// Applies one string criterion to a rendered or stored composite text value.<br/>
    /// The helper is shared by named string parts and explicit full-key predicates so wildcard and culture behavior stays aligned.<br/>
    /// </summary>
    /// <param name="text">The text value to test.</param>
    /// <param name="criterion">The string criterion to apply.</param>
    /// <returns><see langword="true"/> when the text satisfies the criterion.</returns>
    private static bool MatchesStringCriterion(string text, LibraDexCompositePartCriterion criterion)
    {
        LibraDexStringComparisonPolicy policy = LibraDexStringComparisonPolicy.FromLegacy(criterion.IgnoreCase, criterion.Culture);
        return criterion.Operator switch
        {
            LibraDexConditionOperatorKind.EqualTo => LibraDexStringPatternPredicate.Create(LibraDexStringPatternMode.EqualTo, RequireString(criterion.Values, 0), policy).Matches(text),
            LibraDexConditionOperatorKind.NotEqualTo => LibraDexStringPatternPredicate.Create(LibraDexStringPatternMode.NotEqualTo, RequireString(criterion.Values, 0), policy).Matches(text),
            LibraDexConditionOperatorKind.GreaterThan => LibraDexStringPatternPredicate.Create(LibraDexStringPatternMode.GreaterThan, RequireString(criterion.Values, 0), policy).Matches(text),
            LibraDexConditionOperatorKind.GreaterOrEqual => LibraDexStringPatternPredicate.Create(LibraDexStringPatternMode.GreaterOrEqual, RequireString(criterion.Values, 0), policy).Matches(text),
            LibraDexConditionOperatorKind.LessThan => LibraDexStringPatternPredicate.Create(LibraDexStringPatternMode.LessThan, RequireString(criterion.Values, 0), policy).Matches(text),
            LibraDexConditionOperatorKind.LessOrEqual => LibraDexStringPatternPredicate.Create(LibraDexStringPatternMode.LessOrEqual, RequireString(criterion.Values, 0), policy).Matches(text),
            LibraDexConditionOperatorKind.StartsWith => LibraDexStringPatternPredicate.Create(LibraDexStringPatternMode.StartsWith, RequireString(criterion.Values, 0), policy).Matches(text),
            LibraDexConditionOperatorKind.EndsWith => LibraDexStringPatternPredicate.Create(LibraDexStringPatternMode.EndsWith, RequireString(criterion.Values, 0), policy).Matches(text),
            LibraDexConditionOperatorKind.Contains => LibraDexStringPatternPredicate.Create(LibraDexStringPatternMode.Contains, RequireString(criterion.Values, 0), policy).Matches(text),
            LibraDexConditionOperatorKind.MatchesPattern => LibraDexStringPatternPredicate.Create(LibraDexStringPatternMode.MatchesPattern, RequireString(criterion.Values, 0), policy).Matches(text),
            LibraDexConditionOperatorKind.Between => LibraDexStringPatternPredicate.Create(LibraDexStringPatternMode.Between, RequireString(criterion.Values, 0), RequireString(criterion.Values, 1), policy).Matches(text),
            LibraDexConditionOperatorKind.NotBetween => LibraDexStringPatternPredicate.Create(LibraDexStringPatternMode.NotBetween, RequireString(criterion.Values, 0), RequireString(criterion.Values, 1), policy).Matches(text),
            LibraDexConditionOperatorKind.InSet => LibraDexStringPatternPredicate.CreateSet(LibraDexStringPatternMode.InSet, RequireStringSet(criterion.Values, 0), policy).Matches(text),
            LibraDexConditionOperatorKind.NotInSet => LibraDexStringPatternPredicate.CreateSet(LibraDexStringPatternMode.NotInSet, RequireStringSet(criterion.Values, 0), policy).Matches(text),
            _ => throw new NotSupportedException($"Composite string part operator {criterion.Operator} is not supported in this slice.")
        };
    }

    /// <summary>
    /// Tests scalar membership using the same boxed comparer semantics as composite scalar equality.<br/>
    /// The membership operand is captured by the fluent builder as one enumerable payload so composite `InSet` does not need caller-expanded OR branches.<br/>
    /// </summary>
    /// <param name="value">The routed composite component value.</param>
    /// <param name="setOperand">The captured membership operand.</param>
    /// <returns><see langword="true"/> when the value is present in the supplied membership operand.</returns>
    private static bool MatchesObjectSet(object value, object setOperand)
    {
        if (setOperand is IEnumerable enumerable)
        {
            foreach (object? candidate in enumerable)
            {
                if (candidate is not null && Comparer<object>.Default.Compare(value, candidate) == 0)
                {
                    return true;
                }
            }

            return false;
        }

        throw new InvalidOperationException("Composite scalar membership predicates require an enumerable operand.");
    }

    /// <summary>
    /// Tests GUID membership without converting the GUID value into text.<br/>
    /// </summary>
    /// <param name="value">The routed GUID component value.</param>
    /// <param name="setOperand">The captured membership operand.</param>
    /// <returns><see langword="true"/> when the value is present in the supplied GUID set.</returns>
    private static bool MatchesGuidSet(Guid value, object setOperand)
    {
        if (setOperand is IEnumerable<Guid> guids)
        {
            foreach (Guid candidate in guids)
            {
                if (value == candidate)
                {
                    return true;
                }
            }

            return false;
        }

        if (setOperand is IEnumerable enumerable)
        {
            foreach (object? candidate in enumerable)
            {
                if (candidate is Guid guid && value == guid)
                {
                    return true;
                }

                if (candidate is not Guid)
                {
                    throw new InvalidOperationException("Composite GUID membership predicates require GUID operands.");
                }
            }

            return false;
        }

        throw new InvalidOperationException("Composite GUID membership predicates require an enumerable operand.");
    }

    /// <summary>
    /// Reads one captured string membership operand from a composite string criterion.<br/>
    /// The method accepts the preserved enumerable shape used by the condition builder and validates every member before creating the shared string predicate.<br/>
    /// </summary>
    /// <param name="values">The criterion operand list.</param>
    /// <param name="ordinal">The operand ordinal to read.</param>
    /// <returns>The string values captured for membership testing.</returns>
    private static IEnumerable<string> RequireStringSet(IReadOnlyList<object?> values, int ordinal)
    {
        object operand = RequireValue(values, ordinal);
        if (operand is IEnumerable<string> strings)
        {
            return strings;
        }

        if (operand is IEnumerable enumerable)
        {
            List<string> captured = new();
            foreach (object? value in enumerable)
            {
                captured.Add(value as string ?? throw new InvalidOperationException("Composite string membership predicates require string operands."));
            }

            return captured;
        }

        throw new InvalidOperationException("Composite string membership predicates require an enumerable operand.");
    }

    /// <summary>
    /// Encodes the current full composite key path using index-order parts and the caller-selected delimiter bytes.<br/>
    /// Each component uses the same stable key-domain encoding LibraDex uses for comparable scalar/string/GUID/date/binary values, so full-key matching converts the criterion once and does not render every part to text.<br/>
    /// </summary>
    /// <param name="shape">The composite shape that supplies part names and index order.</param>
    /// <param name="values">The composite path values in index order.</param>
    /// <param name="criterion">The full-key criterion that supplies delimiter and optional included part names.</param>
    /// <returns>The encoded full-key composite key.</returns>
    private static byte[] EncodeFullKey(
        LibraDexIndexShapeSpec shape,
        IReadOnlyList<object?> values,
        LibraDexCompositePartCriterion criterion)
    {
        byte[] delimiter = Encoding.UTF8.GetBytes(criterion.FullKeyDelimiter ?? string.Empty);
        IReadOnlyList<string>? partNames = criterion.FullKeyPartNames;
        IReadOnlyList<string>? excludedPartNames = criterion.FullKeyExcludedPartNames;
        using MemoryStream stream = new();
        bool appendedAny = false;
        for (int i = 0; i < values.Count; i++)
        {
            if (partNames is { Count: > 0 } &&
                !ContainsPartName(partNames, shape.CompositeParts[i].Name))
            {
                continue;
            }

            if (excludedPartNames is { Count: > 0 } &&
                ContainsPartName(excludedPartNames, shape.CompositeParts[i].Name))
            {
                continue;
            }

            if (appendedAny && delimiter.Length != 0)
            {
                stream.Write(delimiter);
            }

            WriteFullKeyPart(
                stream,
                shape.CompositeParts[i],
                values[i] ?? throw new InvalidDataException("FullKey composite traversal reached a terminal path with a missing part value."));
            appendedAny = true;
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Tests whether a full-key include list contains one composite part name.<br/>
    /// Include lists are expected to be small, so a linear ordinal check keeps this path allocation-free and avoids building a set per terminal key.<br/>
    /// </summary>
    /// <param name="partNames">The included part names.</param>
    /// <param name="partName">The part name to find.</param>
    /// <returns><see langword="true"/> when the part is included; otherwise <see langword="false"/>.</returns>
    private static bool ContainsPartName(IReadOnlyList<string> partNames, string partName)
    {
        for (int i = 0; i < partNames.Count; i++)
        {
            if (string.Equals(partNames[i], partName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Writes one composite component in its comparable key-domain byte form.<br/>
    /// Strings use UTF-8 bytes, fixed scalars use big-endian sortable lanes, GUIDs use their 16-byte value, dates use the structured date scalar, and raw binary parts remain raw bytes.<br/>
    /// </summary>
    /// <param name="stream">The destination full-key byte stream.</param>
    /// <param name="part">The component part descriptor recorded by the composite shape.</param>
    /// <param name="value">The component value to encode.</param>
    private static void WriteFullKeyPart(Stream stream, LibraDexCompositeKeyPartSpec part, object value)
    {
        Type type = part.KeyType;
        if (type == typeof(string))
        {
            byte[] bytes = Encoding.UTF8.GetBytes((string)value);
            stream.Write(bytes);
            return;
        }

        if (type == typeof(byte[]))
        {
            stream.Write((byte[])value);
            return;
        }

        Span<byte> buffer = stackalloc byte[16];
        if (type == typeof(Guid))
        {
            ((Guid)value).TryWriteBytes(buffer);
            stream.Write(buffer);
            return;
        }

        if (TryEncodeFullKeyScalar8(part, value, out ulong encoded))
        {
            BinaryPrimitives.WriteUInt64BigEndian(buffer[..8], encoded);
            stream.Write(buffer[..8]);
            return;
        }

        throw new NotSupportedException($"Composite full-key byte matching does not support part type {type.FullName}.");
    }

    /// <summary>
    /// Encodes a supported 8-byte composite component into LibraDex's sortable scalar lane.<br/>
    /// This mirrors the generic scalar key codec so full-key-byte residual matching compares the same byte-domain representation used by scalar indexes.<br/>
    /// </summary>
    /// <param name="part">The component part descriptor.</param>
    /// <param name="value">The component value.</param>
    /// <param name="encoded">Receives the sortable 8-byte lane when supported.</param>
    /// <returns><see langword="true"/> when the component was encoded; otherwise <see langword="false"/>.</returns>
    private static bool TryEncodeFullKeyScalar8(LibraDexCompositeKeyPartSpec part, object value, out ulong encoded)
    {
        Type type = part.KeyType;
        encoded = 0;
        if (type == typeof(bool))
        {
            encoded = (bool)value ? 1UL : 0UL;
            return true;
        }

        if (type == typeof(byte))
        {
            encoded = (byte)value;
            return true;
        }

        if (type == typeof(sbyte))
        {
            encoded = unchecked((byte)((sbyte)value ^ sbyte.MinValue));
            return true;
        }

        if (type == typeof(short))
        {
            encoded = unchecked((ushort)((short)value ^ short.MinValue));
            return true;
        }

        if (type == typeof(ushort))
        {
            encoded = (ushort)value;
            return true;
        }

        if (type == typeof(char))
        {
            encoded = (char)value;
            return true;
        }

        if (type == typeof(int))
        {
            encoded = unchecked((uint)((int)value ^ int.MinValue));
            return true;
        }

        if (type == typeof(uint))
        {
            encoded = (uint)value;
            return true;
        }

        if (type == typeof(long))
        {
            encoded = unchecked((ulong)((long)value ^ long.MinValue));
            return true;
        }

        if (type == typeof(ulong))
        {
            encoded = (ulong)value;
            return true;
        }

        if (type == typeof(DateTime))
        {
            encoded = LibraDexStructuredDateCodec.Encode((DateTime)value, part.DateTimeKeyEncoding);
            return true;
        }

        if (type == typeof(DateTimeOffset))
        {
            encoded = LibraDexStructuredDateCodec.Encode((DateTimeOffset)value, part.DateTimeKeyEncoding);
            return true;
        }

        if (type == typeof(DateOnly))
        {
            encoded = LibraDexStructuredDateCodec.Encode((DateOnly)value, part.DateTimeKeyEncoding);
            return true;
        }

        if (type == typeof(TimeOnly))
        {
            encoded = LibraDexStructuredDateCodec.Encode((TimeOnly)value, part.DateTimeKeyEncoding);
            return true;
        }

        if (type == typeof(TimeSpan))
        {
            encoded = unchecked((ulong)(((TimeSpan)value).Ticks ^ long.MinValue));
            return true;
        }

        return false;
    }

    /// <summary>
    /// Applies a full-key string criterion against encoded full-key bytes.<br/>
    /// Operands are converted once into the same byte-domain as composite components; matching does not render stored component values to text.<br/>
    /// </summary>
    /// <param name="shape">The composite shape used to resolve part-level encoding contracts for typed operands.</param>
    /// <param name="candidate">The encoded full-key composite key.</param>
    /// <param name="criterion">The full-key string criterion.</param>
    /// <returns><see langword="true"/> when the encoded candidate satisfies the criterion.</returns>
    private static bool MatchesFullKeyBytes(
        LibraDexIndexShapeSpec shape,
        ReadOnlySpan<byte> candidate,
        LibraDexCompositePartCriterion criterion)
    {
        byte[] operand = EncodeFullKeyCriterionOperand(shape, criterion, RequireValue(criterion.Values, 0));
        return criterion.Operator switch
        {
            LibraDexConditionOperatorKind.EqualTo => candidate.SequenceEqual(operand),
            LibraDexConditionOperatorKind.StartsWith => candidate.StartsWith(operand),
            LibraDexConditionOperatorKind.EndsWith => candidate.EndsWith(operand),
            LibraDexConditionOperatorKind.Contains => candidate.IndexOf(operand) >= 0,
            LibraDexConditionOperatorKind.MatchesPattern => MatchesFullKeyByteWildcard(candidate, operand),
            _ => throw new NotSupportedException($"Composite full-key operator {criterion.Operator} is not supported for byte-domain matching.")
        };
    }

    /// <summary>
    /// Encodes one full-key criterion operand into the composite byte domain.<br/>
    /// Strings become UTF-8 bytes, raw binary stays raw, GUIDs use their 16-byte value, and scalar/date values use sortable big-endian lanes just like full-key path components.<br/>
    /// </summary>
    /// <param name="shape">The composite shape used to resolve part-level encoding contracts for typed operands.</param>
    /// <param name="criterion">The full-key criterion that may narrow matching to selected part names.</param>
    /// <param name="value">The criterion operand to encode.</param>
    /// <returns>The encoded operand bytes.</returns>
    private static byte[] EncodeFullKeyCriterionOperand(
        LibraDexIndexShapeSpec shape,
        LibraDexCompositePartCriterion criterion,
        object value)
    {
        using MemoryStream stream = new();
        WriteFullKeyPart(stream, ResolveFullKeyOperandPart(shape, criterion, value), value);
        return stream.ToArray();
    }

    /// <summary>
    /// Resolves the composite part descriptor that should encode a typed full-key operand.<br/>
    /// Explicit single-part full-key predicates use that part's metadata, while whole-key typed predicates fall back to the first compatible part when one is unambiguous enough for byte-domain matching.<br/>
    /// </summary>
    /// <param name="shape">The composite shape that supplies part descriptors.</param>
    /// <param name="criterion">The full-key criterion with optional part selection.</param>
    /// <param name="value">The typed operand value.</param>
    /// <returns>The part descriptor used to encode the operand.</returns>
    private static LibraDexCompositeKeyPartSpec ResolveFullKeyOperandPart(
        LibraDexIndexShapeSpec shape,
        LibraDexCompositePartCriterion criterion,
        object value)
    {
        IReadOnlyList<string>? partNames = criterion.FullKeyPartNames;
        if (partNames is { Count: 1 } &&
            TryGetCompositePart(shape, partNames[0], out LibraDexCompositeKeyPartSpec selectedPart))
        {
            return selectedPart;
        }

        Type valueType = value.GetType();
        for (int i = 0; i < shape.CompositeParts.Count; i++)
        {
            LibraDexCompositeKeyPartSpec part = shape.CompositeParts[i];
            if (part.KeyType == valueType)
            {
                return part;
            }
        }

        if (valueType == typeof(string))
        {
            return LibraDexCompositeKeyPart.String(valueType.Name);
        }

        if (valueType == typeof(byte[]))
        {
            return LibraDexCompositeKeyPart.Binary(valueType.Name);
        }

        if (valueType == typeof(Guid))
        {
            return LibraDexCompositeKeyPart.Guid(valueType.Name);
        }

        if (valueType == typeof(DateTime))
        {
            return LibraDexCompositeKeyPart.Date<DateTime>(valueType.Name);
        }

        if (valueType == typeof(DateTimeOffset))
        {
            return LibraDexCompositeKeyPart.Date<DateTimeOffset>(valueType.Name);
        }

        if (valueType == typeof(DateOnly))
        {
            return LibraDexCompositeKeyPart.Date<DateOnly>(valueType.Name);
        }

        if (valueType == typeof(TimeOnly))
        {
            return LibraDexCompositeKeyPart.Date<TimeOnly>(valueType.Name);
        }

        if (valueType == typeof(TimeSpan))
        {
            return LibraDexCompositeKeyPart.Date<TimeSpan>(valueType.Name);
        }

        return LibraDexCompositeKeyPart.Scalar<object>(valueType.Name);
    }

    /// <summary>
    /// Finds one composite part by name using the descriptor's ordinal name comparison.<br/>
    /// </summary>
    /// <param name="shape">The composite shape to inspect.</param>
    /// <param name="partName">The part name to find.</param>
    /// <param name="part">Receives the matching part descriptor when found.</param>
    /// <returns><see langword="true"/> when the named part exists.</returns>
    private static bool TryGetCompositePart(
        LibraDexIndexShapeSpec shape,
        string partName,
        out LibraDexCompositeKeyPartSpec part)
    {
        for (int i = 0; i < shape.CompositeParts.Count; i++)
        {
            if (string.Equals(shape.CompositeParts[i].Name, partName, StringComparison.Ordinal))
            {
                part = shape.CompositeParts[i];
                return true;
            }
        }

        part = default;
        return false;
    }

    /// <summary>
    /// Applies simple wildcard matching over encoded full-key bytes.<br/>
    /// The UTF-8 byte value `*` spans zero or more bytes and `?` matches one byte, keeping pattern semantics byte-oriented for full-key composite matching.<br/>
    /// </summary>
    /// <param name="candidate">The encoded full-key composite key.</param>
    /// <param name="pattern">The encoded wildcard pattern.</param>
    /// <returns><see langword="true"/> when the candidate matches the byte wildcard pattern.</returns>
    private static bool MatchesFullKeyByteWildcard(ReadOnlySpan<byte> candidate, ReadOnlySpan<byte> pattern)
    {
        int candidateIndex = 0;
        int patternIndex = 0;
        int starIndex = -1;
        int matchIndex = 0;
        while (candidateIndex < candidate.Length)
        {
            if (patternIndex < pattern.Length &&
                (pattern[patternIndex] == (byte)'?' || pattern[patternIndex] == candidate[candidateIndex]))
            {
                candidateIndex++;
                patternIndex++;
                continue;
            }

            if (patternIndex < pattern.Length && pattern[patternIndex] == (byte)'*')
            {
                starIndex = patternIndex++;
                matchIndex = candidateIndex;
                continue;
            }

            if (starIndex >= 0)
            {
                patternIndex = starIndex + 1;
                candidateIndex = ++matchIndex;
                continue;
            }

            return false;
        }

        while (patternIndex < pattern.Length && pattern[patternIndex] == (byte)'*')
        {
            patternIndex++;
        }

        return patternIndex == pattern.Length;
    }

    private void ValidateIdentity(object identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!IdentityType.IsInstanceOfType(identity))
        {
            throw new ArgumentException($"Composite identity type {identity.GetType().FullName} does not match index identity type {IdentityType.FullName}.", nameof(identity));
        }
    }

    private static IEnumerable<object> ApplyTake(IEnumerable<object> source, int? takeLimit)
    {
        if (takeLimit is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(takeLimit), takeLimit, "Take cannot be negative.");
        }

        int returned = 0;
        foreach (object identity in source)
        {
            if (takeLimit == 0)
            {
                yield break;
            }

            yield return identity;
            returned++;
            if (takeLimit is not null && returned >= takeLimit.Value)
            {
                yield break;
            }
        }
    }

    private static CompositePartKey CreatePartKey(LibraDexCompositeKeyPartSpec part, object? value)
    {
        return new CompositePartKey(part, NormalizePartValue(part, value));
    }

    private static object NormalizePartValue(LibraDexCompositeKeyPartSpec part, object? value)
    {
        return LibraDexCompositeKeyValueSemantics.NormalizeForStorage(part, value);
    }

    private static LibraDexCompositeKey RequireCompositeKey(IReadOnlyList<object?> values, int ordinal)
    {
        return values.Count > ordinal && values[ordinal] is LibraDexCompositeKey key
            ? key
            : throw new InvalidOperationException("Composite exact lookup requires a LibraDexCompositeKey operand.");
    }

    private static LibraDexCompositePredicate RequireCompositePredicate(IReadOnlyList<object?> values, int ordinal)
    {
        return values.Count > ordinal && values[ordinal] is LibraDexCompositePredicate predicate
            ? predicate
            : throw new InvalidOperationException("Composite match lookup requires a compiled composite predicate operand.");
    }

    private static object RequireValue(IReadOnlyList<object?> values, int ordinal)
    {
        return values.Count > ordinal
            ? values[ordinal] ?? throw new InvalidOperationException("Composite part predicates do not allow null operands.")
            : throw new InvalidOperationException("Composite part predicate is missing a required operand.");
    }

    private static string RequireString(IReadOnlyList<object?> values, int ordinal)
    {
        return RequireValue(values, ordinal) as string
            ?? throw new InvalidOperationException("Composite string part predicates require string operands.");
    }

    private static Guid RequireGuid(IReadOnlyList<object?> values, int ordinal)
    {
        return RequireValue(values, ordinal) is Guid value
            ? value
            : throw new InvalidOperationException("Composite GUID part predicates require GUID operands.");
    }

    private static byte[] RequireBytes(IReadOnlyList<object?> values, int ordinal)
    {
        return RequireValue(values, ordinal) as byte[]
            ?? throw new InvalidOperationException("Composite binary part predicates require byte-array operands.");
    }

    private static int RequireInt32(IReadOnlyList<object?> values, int ordinal)
    {
        return RequireValue(values, ordinal) is int value
            ? value
            : throw new InvalidOperationException("Composite part predicate requires an Int32 operand.");
    }

    private static CultureInfo ResolveCulture(string? culture)
    {
        return string.IsNullOrWhiteSpace(culture)
            ? CultureInfo.InvariantCulture
            : CultureInfo.GetCultureInfo(culture);
    }

    /// <summary>
    /// Writes the current routed composite contents as a replacement durable snapshot.<br/>
    /// This is the first correctness-oriented durable contents path; it persists the logical tier tree through DataKernel while reserving optimized per-tier mini-router pages for the next physical storage slice.<br/>
    /// </summary>
    /// <param name="durableSlotIndex">The fixed catalog slot whose root offset should be updated to the replacement snapshot.</param>
    private void PersistSnapshot(int durableSlotIndex)
    {
        LibraDexCompositeEntry[] entries = EnumerateEntries().ToArray();
        byte[] snapshot = LibraDexCompositeSnapshotCodec.Encode(shape, entries);
        session!.UpdateCompositeSnapshotIndex(durableSlotIndex, snapshot, itemCount);
    }

    /// <summary>
    /// Publishes a whole-tree composite mutation after identities have been removed from one or more terminal nodes.<br/>
    /// Deletion can touch many routed paths, so this correctness bridge writes a replacement snapshot and clears the node-page root marker to prevent later path-copy inserts from reusing stale durable child pages.<br/>
    /// </summary>
    private void PersistAfterWholeTreeMutation()
    {
        if (session is null || slotIndex is not int durableSlotIndex)
        {
            return;
        }

        PersistSnapshot(durableSlotIndex);
        root.DurableOffset = 0;
    }

    /// <summary>
    /// Persists one immutable path-copy update from a changed terminal node back to the root.<br/>
    /// Each node on the changed route is encoded as a new durable node page; untouched sibling child offsets are reused, and publication happens by updating the catalog slot to the new root offset.<br/>
    /// </summary>
    /// <param name="durableSlotIndex">The fixed catalog slot whose root offset should be replaced.</param>
    /// <param name="path">The changed route path from root through terminal node.</param>
    private void PersistPathCopy(int durableSlotIndex, IReadOnlyList<CompositeNode> path)
    {
        for (int tier = path.Count - 1; tier >= 0; tier--)
        {
            CompositeNode node = path[tier];
            byte[] page = EncodeNodePage(node, tier);
            node.DurableOffset = session!.AppendCompositeNodePage(page);
        }

        session!.UpdateCompositeNodeRoot(durableSlotIndex, root.DurableOffset, itemCount);
    }

    /// <summary>
    /// Encodes one in-memory routed node as a durable composite node page.<br/>
    /// Child routes store their component value and current durable child offset, so only nodes on a changed path need to be copied on insert.<br/>
    /// </summary>
    /// <param name="node">The in-memory routed node to encode.</param>
    /// <param name="tier">The composite tier represented by the node.</param>
    /// <returns>The encoded node page bytes.</returns>
    private byte[] EncodeNodePage(CompositeNode node, int tier)
    {
        EnsureNodeLoaded(node, tier);
        LibraDexCompositeNodeChildPage[] children = new LibraDexCompositeNodeChildPage[node.Children.Count];
        for (int i = 0; i < children.Length; i++)
        {
            CompositeChild child = node.Children[i];
            if (child.Node.DurableOffset <= 0)
            {
                throw new InvalidDataException("Composite child node does not have a durable offset.");
            }

            children[i] = new LibraDexCompositeNodeChildPage(child.Key.Value, child.Node.DurableOffset);
        }

        return LibraDexCompositeNodePageCodec.Encode(shape, tier, node.Identities, children);
    }

    /// <summary>
    /// Ensures a durable composite node page has been hydrated before its identities or child route table are inspected.<br/>
    /// Reopened composite indexes keep node pages lazy so exact and constrained predicates only load the tiers they traverse, while full scans still materialize nodes as needed.<br/>
    /// </summary>
    /// <param name="node">The node whose page body should be available in memory.</param>
    /// <param name="tier">The expected composite tier for the node.</param>
    private void EnsureNodeLoaded(CompositeNode node, int tier)
    {
        if (node.IsLoaded)
        {
            return;
        }

        if (session is null || !session.TryReadCompositeNodePage(node.DurableOffset, out byte[] pageBytes))
        {
            throw new InvalidDataException("Composite node page could not be read from the requested offset.");
        }

        LibraDexCompositeNodePage page = LibraDexCompositeNodePageCodec.Decode(shape, pageBytes);
        if (page.Tier != tier)
        {
            throw new InvalidDataException("Composite node page tier does not match its routed position.");
        }

        for (int i = 0; i < page.Identities.Count; i++)
        {
            _ = node.AddIdentity(page.Identities[i], KeyContract);
        }

        for (int i = 0; i < page.Children.Count; i++)
        {
            LibraDexCompositeNodeChildPage childPage = page.Children[i];
            CompositePartKey childKey = new(shape.CompositeParts[tier], childPage.Value);
            CompositeNode child = node.GetOrAdd(childKey);
            child.MarkUnloaded(childPage.Offset);
        }

        node.MarkLoaded();
    }

    /// <summary>
    /// Locates a child route for exact composite-key traversal with a durable-page fast path.<br/>
    /// When the current node is still a lazy page placeholder, versioned node pages can binary-search the child directory and return the next child page offset without hydrating every sibling route.<br/>
    /// </summary>
    /// <param name="node">The current routed node.</param>
    /// <param name="tier">The current composite tier.</param>
    /// <param name="key">The exact component key to locate.</param>
    /// <param name="child">Receives the matching child node when found.</param>
    /// <returns><see langword="true"/> when the child route exists; otherwise <see langword="false"/>.</returns>
    private bool TryGetChildForExact(CompositeNode node, int tier, CompositePartKey key, out CompositeNode child)
    {
        if (!node.IsLoaded &&
            session is not null &&
            session.TryReadCompositeNodePage(node.DurableOffset, out byte[] pageBytes) &&
            LibraDexCompositeNodePageCodec.TryFindChildOffset(shape, pageBytes, tier, key.Value, out long childOffset))
        {
            child = new CompositeNode(key.Value);
            child.MarkUnloaded(childOffset);
            return true;
        }

        EnsureNodeLoaded(node, tier);
        return node.TryGet(key, out child);
    }

    /// <summary>
    /// Enumerates terminal composite entries from the routed tier tree in tier order.<br/>
    /// The enumeration reconstructs explicit composite keys from the traversal path so the snapshot codec does not need to know about in-memory node internals.<br/>
    /// </summary>
    /// <returns>The current composite key/identity entries.</returns>
    private IEnumerable<LibraDexCompositeEntry> EnumerateEntries()
    {
        object?[] values = new object?[shape.CompositeParts.Count];
        foreach (LibraDexCompositeEntry entry in EnumerateEntries(root, tier: 0, values))
        {
            yield return entry;
        }
    }

    /// <summary>
    /// Recursively enumerates terminal entries below one routed tier node.<br/>
    /// The reusable value buffer keeps traversal allocation low while each terminal entry receives its own composite-key container for durable encoding.<br/>
    /// </summary>
    /// <param name="node">The node to enumerate.</param>
    /// <param name="tier">The current composite part ordinal.</param>
    /// <param name="values">The reusable traversal path value buffer.</param>
    /// <returns>The composite key/identity entries below the node.</returns>
    private IEnumerable<LibraDexCompositeEntry> EnumerateEntries(CompositeNode node, int tier, object?[] values)
    {
        EnsureNodeLoaded(node, tier);
        if (tier >= shape.CompositeParts.Count)
        {
            LibraDexCompositeKey key = LibraDexCompositeKey.Of(values.ToArray());
            foreach (object identity in node.Identities)
            {
                yield return new LibraDexCompositeEntry(key, identity);
            }

            yield break;
        }

        foreach (CompositeChild child in node.Children)
        {
            values[tier] = child.Key.Value;
            foreach (LibraDexCompositeEntry entry in EnumerateEntries(child.Node, tier + 1, values))
            {
                yield return entry;
            }

            values[tier] = null;
        }
    }

    /// <summary>
    /// Enumerates identities below one routed node while lazily hydrating durable node pages as traversal reaches them.<br/>
    /// This keeps reopen cheap for point and leading-part probes without changing full-scan behavior when the caller explicitly asks for the whole identity universe.<br/>
    /// </summary>
    /// <param name="node">The node to enumerate.</param>
    /// <param name="tier">The node's composite tier.</param>
    /// <returns>The identities stored at or below the supplied node.</returns>
    private IEnumerable<object> EnumerateIdentities(CompositeNode node, int tier)
    {
        EnsureNodeLoaded(node, tier);
        foreach (object identity in node.Identities)
        {
            yield return identity;
        }

        for (int i = 0; i < node.Children.Count; i++)
        {
            foreach (object identity in EnumerateIdentities(node.Children[i].Node, tier + 1))
            {
                yield return identity;
            }
        }
    }

    private sealed class CompositeNode
    {
        private readonly List<CompositeChild> children = new();
        private readonly List<object> identities = new();

        internal CompositeNode()
        {
        }

        internal CompositeNode(object routeValue)
        {
            RouteValue = routeValue;
        }

        internal IReadOnlyList<CompositeChild> Children => children;

        internal IReadOnlyList<object> Identities => identities;

        internal object? RouteValue { get; }

        internal long DurableOffset { get; set; }

        internal bool IsLoaded { get; private set; } = true;

        /// <summary>
        /// Gets an existing child route for a component value or creates one in sorted route order.<br/>
        /// Child routes are maintained as an ordered mini-router table, so insert and lookup use binary search instead of scanning every sibling route.<br/>
        /// </summary>
        /// <param name="key">The component route key.</param>
        /// <returns>The existing or newly created child node.</returns>
        internal CompositeNode GetOrAdd(CompositePartKey key)
        {
            int index = FindChildIndex(key);
            if (index >= 0)
            {
                return children[index].Node;
            }

            CompositeNode node = new(key.Value);
            children.Insert(~index, new CompositeChild(key, node));
            return node;
        }

        /// <summary>
        /// Attempts to get a child route by component key.<br/>
        /// The ordered child table keeps lookup logarithmic for wide composite tiers while preserving deterministic traversal order.<br/>
        /// </summary>
        /// <param name="key">The component route key.</param>
        /// <param name="node">Receives the child node when found.</param>
        /// <returns><see langword="true"/> when a child route exists; otherwise <see langword="false"/>.</returns>
        internal bool TryGet(CompositePartKey key, out CompositeNode node)
        {
            int index = FindChildIndex(key);
            if (index >= 0)
            {
                node = children[index].Node;
                return true;
            }

            node = null!;
            return false;
        }

        /// <summary>
        /// Locates a child route in the ordered child table.<br/>
        /// The method returns the matching index when found, or the bitwise complement of the insertion index when missing, matching the standard binary-search convention.<br/>
        /// </summary>
        /// <param name="key">The component route key to locate.</param>
        /// <returns>The found index or complemented insertion index.</returns>
        private int FindChildIndex(CompositePartKey key)
        {
            int low = 0;
            int high = children.Count - 1;
            while (low <= high)
            {
                int mid = low + ((high - low) / 2);
                int comparison = children[mid].Key.CompareTo(key);
                if (comparison == 0)
                {
                    return mid;
                }

                if (comparison < 0)
                {
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }

            return ~low;
        }

        internal bool AddIdentity(object identity, IndexKeys keyContract)
        {
            if (keyContract == IndexKeys.Unique && identities.Count != 0)
            {
                return false;
            }

            for (int i = 0; i < identities.Count; i++)
            {
                if (LibraDexObjectTuple.ValueEquals(identities[i], identity))
                {
                    return false;
                }
            }

            identities.Add(identity);
            return true;
        }

        /// <summary>
        /// Removes one exact identity from this node's terminal identity list.<br/>
        /// Composite key matching is handled by the caller; this node-local operation only narrows mutation from a whole terminal run to one key/identity tuple.<br/>
        /// </summary>
        /// <param name="identity">The identity value to remove.</param>
        /// <returns><see langword="true"/> when an identity was removed.</returns>
        internal bool RemoveIdentity(object identity)
        {
            for (int i = 0; i < identities.Count; i++)
            {
                if (!LibraDexObjectTuple.ValueEquals(identities[i], identity))
                {
                    continue;
                }

                identities.RemoveAt(i);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Removes every identity stored directly on this node.<br/>
        /// Composite identities are stored only at terminal nodes today, but the method stays node-local so future component-level marker nodes can make the storage rule explicit at the caller.<br/>
        /// </summary>
        /// <returns>The number of identities removed from this node.</returns>
        internal int ClearIdentities()
        {
            int count = identities.Count;
            identities.Clear();
            return count;
        }

        /// <summary>
        /// Clears this node's identities and child routes.<br/>
        /// This is used by whole-index delete to keep the existing root object stable while removing all routed contents and lazy child-page references.<br/>
        /// </summary>
        internal void Clear()
        {
            identities.Clear();
            children.Clear();
            DurableOffset = 0;
            IsLoaded = true;
        }

        /// <summary>
        /// Marks this node as a lazy durable placeholder whose page body should be loaded before child or identity access.<br/>
        /// </summary>
        /// <param name="offset">The durable node-page offset represented by this placeholder.</param>
        internal void MarkUnloaded(long offset)
        {
            DurableOffset = offset;
            IsLoaded = false;
        }

        /// <summary>
        /// Marks this node's page body as available in memory after durable hydration.<br/>
        /// </summary>
        internal void MarkLoaded()
        {
            IsLoaded = true;
        }
    }

    private readonly record struct CompositeChild(CompositePartKey Key, CompositeNode Node);

    private readonly record struct CompositePartKey(LibraDexCompositeKeyPartSpec Part, object Value) : IComparable<CompositePartKey>
    {
        public int CompareTo(CompositePartKey other)
        {
            return LibraDexCompositeKeyValueSemantics.ComparePartValues(Part, Value, other.Value);
        }
    }
}

internal sealed class LibraDexCompositePredicate
{
    private readonly Dictionary<string, LibraDexCompositePartCriterion> criteria;

    internal LibraDexCompositePredicate(IEnumerable<LibraDexCompositePartCriterion> criteria)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        this.criteria = new Dictionary<string, LibraDexCompositePartCriterion>(StringComparer.Ordinal);
        foreach (LibraDexCompositePartCriterion criterion in criteria)
        {
            if (!this.criteria.TryAdd(criterion.PartName, criterion))
            {
                throw new ArgumentException($"Composite condition contains duplicate predicate for part '{criterion.PartName}'.", nameof(criteria));
            }
        }

        if (this.criteria.Count == 0)
        {
            throw new ArgumentException("Composite predicates require at least one part criterion.", nameof(criteria));
        }
    }

    internal bool TryGetPart(string name, out LibraDexCompositePartCriterion? criterion)
    {
        return criteria.TryGetValue(name, out criterion);
    }

    internal void ValidateAgainst(LibraDexIndexShapeSpec shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        if (shape.KeyFamily != CatalogIndexKeyFamily.Composite)
        {
            throw new ArgumentException("Composite predicates require a composite index shape.", nameof(shape));
        }

        foreach (LibraDexCompositePartCriterion criterion in criteria.Values)
        {
            if (string.Equals(criterion.PartName, LibraDexCompositePartCriterion.FullKeyPartName, StringComparison.Ordinal))
            {
                if (!IsSupportedFullKeyCriterionValueKind(criterion.ValueKind))
                {
                    throw new ArgumentException("Composite full-key predicates support string, binary, GUID, numeric, and date/time criteria.", nameof(shape));
                }

                ValidateFullKeyPartSelection(shape, criterion.FullKeyPartNames, "selection");
                ValidateFullKeyPartSelection(shape, criterion.FullKeyExcludedPartNames, "exclusion");
                continue;
            }

            LibraDexCompositeKeyPartSpec part = shape.CompositeParts.FirstOrDefault(p => string.Equals(p.Name, criterion.PartName, StringComparison.Ordinal));
            if (string.IsNullOrEmpty(part.Name))
            {
                throw new ArgumentException($"Composite predicate references unknown part '{criterion.PartName}'.", nameof(shape));
            }

            ValidateCriterionKindAgainstPart(part, criterion);
        }
    }

    private static void ValidateCriterionKindAgainstPart(LibraDexCompositeKeyPartSpec part, LibraDexCompositePartCriterion criterion)
    {
        bool valid = criterion.ValueKind switch
        {
            LibraDexConditionValueKind.String => part.KeyType == typeof(string),
            LibraDexConditionValueKind.Binary => part.KeyType == typeof(byte[]),
            LibraDexConditionValueKind.Guid => part.KeyType == typeof(Guid),
            LibraDexConditionValueKind.DateTime or LibraDexConditionValueKind.DateOnly or LibraDexConditionValueKind.TimeOnly =>
                part.KeyType == typeof(DateTime) ||
                part.KeyType == typeof(DateTimeOffset) ||
                part.KeyType == typeof(DateOnly) ||
                part.KeyType == typeof(TimeOnly),
            LibraDexConditionValueKind.Numeric =>
                part.KeyFamily == CatalogIndexKeyFamily.Scalar ||
                (criterion.Values.Count > 0 &&
                    criterion.Values[0] is not null &&
                    part.KeyType.IsInstanceOfType(criterion.Values[0])),
            _ => true
        };

        if (!valid)
        {
            throw new ArgumentException($"Composite predicate for part '{part.Name}' uses {criterion.ValueKind}, but the part stores {part.KeyType.FullName}.", nameof(criterion));
        }
    }

    internal bool HasFullKeyCriterion => criteria.ContainsKey(LibraDexCompositePartCriterion.FullKeyPartName);

    internal bool TryGetFullKeyPart(out LibraDexCompositePartCriterion? criterion)
    {
        return criteria.TryGetValue(LibraDexCompositePartCriterion.FullKeyPartName, out criterion);
    }

    private static bool IsSupportedFullKeyCriterionValueKind(LibraDexConditionValueKind valueKind)
    {
        return valueKind == LibraDexConditionValueKind.String ||
            valueKind == LibraDexConditionValueKind.Binary ||
            valueKind == LibraDexConditionValueKind.Guid ||
            valueKind == LibraDexConditionValueKind.Numeric ||
            valueKind == LibraDexConditionValueKind.DateTime ||
            valueKind == LibraDexConditionValueKind.DateOnly ||
            valueKind == LibraDexConditionValueKind.TimeOnly;
    }

    /// <summary>
    /// Validates optional full-key part selection against the composite shape.<br/>
    /// Selection names are inclusion filters only; rendering still follows persisted index order after validation succeeds.<br/>
    /// </summary>
    /// <param name="shape">The composite shape that supplies known part names.</param>
    /// <param name="partNames">The optional selected or excluded part names.</param>
    /// <param name="role">The validation role used in exception messages.</param>
    private static void ValidateFullKeyPartSelection(LibraDexIndexShapeSpec shape, IReadOnlyList<string>? partNames, string role)
    {
        if (partNames is not { Count: > 0 })
        {
            return;
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        for (int i = 0; i < partNames.Count; i++)
        {
            string partName = partNames[i];
            if (!seen.Add(partName))
            {
                throw new ArgumentException($"Composite full-key part {role} contains duplicate part '{partName}'.", nameof(shape));
            }

            bool found = false;
            for (int shapeIndex = 0; shapeIndex < shape.CompositeParts.Count; shapeIndex++)
            {
                if (string.Equals(shape.CompositeParts[shapeIndex].Name, partName, StringComparison.Ordinal))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                throw new ArgumentException($"Composite full-key part {role} references unknown part '{partName}'.", nameof(shape));
            }
        }
    }
}
