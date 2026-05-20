using System.Buffers;

namespace LibraDex;

/// <summary>
/// Provides the first generic public wrapper over routed fixed-scalar LibraDex indexes.<br/>
/// The wrapper owns a concrete physical shape selected from `TKey`, `TIdentity`, and any required `byte[]` scalar-width options while keeping the call site in familiar .NET generic form.<br/>
/// </summary>
/// <typeparam name="TKey">The public key type.</typeparam>
/// <typeparam name="TIdentity">The public identity type.</typeparam>
public sealed class LibraDexIndex<TKey, TIdentity> : IIndex, IIdentityPrimitiveExecutor, IDisposable
{
    private readonly LibraDexFileSession session;
    private readonly LibraDexGenericScalarShape shape;
    private readonly bool ownsSession;
    private readonly IndexKeys keyContract;
    private readonly Catalog? catalog;
    private readonly IIndexCriteriaBuilder objectCriteria;
    private bool disposed;

    internal LibraDexIndex(
        LibraDexFileSession session,
        int slotIndex,
        string name,
        long rootRouterOffset,
        LibraDexGenericScalarShape shape,
        bool ownsSession,
        IndexKeys keyContract = IndexKeys.NonUnique,
        Catalog? catalog = null,
        string group = "",
        CatalogIndexKeyFamily keyFamily = CatalogIndexKeyFamily.Unknown,
        CatalogIndexIdentityFamily identityFamily = CatalogIndexIdentityFamily.Unknown,
        LibraDexIndexShapeSpec? logicalShape = null)
    {
        this.session = session;
        this.shape = shape;
        this.ownsSession = ownsSession;
        this.keyContract = keyContract;
        this.catalog = catalog;
        Group = group;
        KeyFamily = keyFamily;
        IdentityFamily = identityFamily;
        LogicalShape = logicalShape;
        SlotIndex = slotIndex;
        Name = name;
        RootRouterOffset = rootRouterOffset;
        BatchManager = new IndexBatchManager<TKey, TIdentity>(this);
        objectCriteria = new LibraDexObjectCriteriaBuilder<TKey, TIdentity>(this);
        Keys = new LibraDexKeyProjection<TKey, TIdentity>(this);
        Identities = new LibraDexIdentityProjection<TKey, TIdentity>(this);
        Not = new LibraDexNotFacade<TKey, TIdentity>(this);
        Aggregates = new LibraDexAggregateFacade<TKey, TIdentity>(this);
        Grouped = new LibraDexGroupedFacade<TKey, TIdentity>(this);
        Stats = new LibraDexIndexStats<TKey, TIdentity>(this);
        Maintenance = new LibraDexIndexMaintenance<TKey, TIdentity>(this);
        Prepare = new LibraDexPrepareFacade<TKey, TIdentity>(this);
        First = new LibraDexPositionalFacade<TKey, TIdentity, LibraDexTuple<TKey, TIdentity>>(this, LibraDexProjectionKind.Tuples, LibraDexPositionKind.First);
        Last = new LibraDexPositionalFacade<TKey, TIdentity, LibraDexTuple<TKey, TIdentity>>(this, LibraDexProjectionKind.Tuples, LibraDexPositionKind.Last);
        Middle = new LibraDexPositionalFacade<TKey, TIdentity, LibraDexTuple<TKey, TIdentity>>(this, LibraDexProjectionKind.Tuples, LibraDexPositionKind.Middle);
        Rank = new LibraDexRankFacade<TKey, TIdentity, LibraDexTuple<TKey, TIdentity>>(this, LibraDexProjectionKind.Tuples, LibraDexPositionKind.Rank);
        PercentRank = new LibraDexPercentRankFacade<TKey, TIdentity, LibraDexTuple<TKey, TIdentity>>(this, LibraDexProjectionKind.Tuples);
    }

    /// <summary>
    /// Gets the fixed index-directory slot used to resolve this runtime index.<br/>
    /// Slot identity remains the primary create/open selector until richer name lookup is deliberately widened.<br/>
    /// </summary>
    public int SlotIndex { get; }

    /// <summary>
    /// Gets the index name stored in the fixed index-directory slot.<br/>
    /// Names are metadata labels in this slice; slot identity remains the primary create/open selector.<br/>
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the identity group name recorded for this index, or an empty string for older ungrouped scaffold entries.<br/>
    /// The value is metadata for public composition and discovery; physical routing remains anchored by the directory slot and root-router offset.<br/>
    /// </summary>
    public string Group { get; }

    /// <summary>
    /// Gets the CLR key type accepted by this index.<br/>
    /// This supports strict non-generic programmatic callers without forcing every layer to carry `TKey` explicitly.<br/>
    /// </summary>
    public Type KeyType => typeof(TKey);

    /// <summary>
    /// Gets the CLR identity type associated with this index.<br/>
    /// </summary>
    public Type IdentityType => typeof(TIdentity);

    /// <summary>
    /// Gets the logical key family recorded for this index.<br/>
    /// </summary>
    public CatalogIndexKeyFamily KeyFamily { get; }

    /// <summary>
    /// Gets the logical identity family recorded for this index.<br/>
    /// </summary>
    public CatalogIndexIdentityFamily IdentityFamily { get; }

    /// <summary>
    /// Gets the logical shape descriptor used to create this runtime index, when available.<br/>
    /// Shape descriptors are the planner-facing counterpart to Abraxas selector/index metadata; catalog metadata version 2 can rehydrate them after reopen.<br/>
    /// </summary>
    public LibraDexIndexShapeSpec? LogicalShape { get; }

    /// <summary>
    /// Gets a programmatic criteria builder for this index.<br/>
    /// The builder creates immutable identity-criteria descriptors for adapters that translate external condition trees into LibraDex lookups.<br/>
    /// </summary>
    public IIndexCriteriaBuilder Criteria => objectCriteria;

    /// <summary>
    /// Gets the root-router file offset for this typed index.<br/>
    /// Exposing this as diagnostics keeps multi-index validation visible without exposing mutation internals.<br/>
    /// </summary>
    public long RootRouterOffset { get; }

    /// <summary>
    /// Gets the DataKernel backing kind used by the owning session.<br/>
    /// File-backed indexes can be reopened after disposal, while memory-backed indexes are temporary and process-local.<br/>
    /// </summary>
    public DataKernelBackingKind BackingKind => session.BackingKind;

    /// <summary>
    /// Gets the index-wide key uniqueness contract selected when the index was opened or created through the public facade.<br/>
    /// `Unique` rejects duplicate keys at insert time; `NonUnique` allows multiple identities to share the same key.<br/>
    /// </summary>
    public IndexKeys KeyContract => keyContract;

    /// <summary>
    /// Gets the index-global batch manager used to control durability cadence without replacing the index mutation surface.<br/>
    /// When enabled, normal insert calls stage writes until `Commit` or `CommitAndDisable` publishes them; the manager is not a SQL transaction and does not provide rollback semantics.<br/>
    /// </summary>
    public IndexBatchManager<TKey, TIdentity> BatchManager { get; }

    /// <summary>
    /// Gets the index-global batch controller used to control durability cadence without replacing the index mutation surface.<br/>
    /// This is the preferred short public spelling for handwritten code; `BatchManager` remains an alias for compatibility while the public scaffold settles.<br/>
    /// </summary>
    public IndexBatchManager<TKey, TIdentity> Batch => BatchManager;

    /// <summary>
    /// Gets key-only retrieval and positional-retrieval projections for this index.<br/>
    /// This keeps value-from-index workflows explicit: callers can ask for keys without materializing identities or object payloads.<br/>
    /// </summary>
    public LibraDexKeyProjection<TKey, TIdentity> Keys { get; }

    /// <summary>
    /// Gets identity-only retrieval and positional-retrieval projections for this index.<br/>
    /// This is the common object-lookup path when callers only need identities from the index.<br/>
    /// </summary>
    public LibraDexIdentityProjection<TKey, TIdentity> Identities { get; }

    /// <summary>
    /// Gets identity-only retrieval and positional-retrieval projections for this index.<br/>
    /// `IDs` is the preferred short call-site spelling; full identity terminology remains in type names and documentation where precision matters.<br/>
    /// </summary>
    public LibraDexIdentityProjection<TKey, TIdentity> IDs => Identities;

    /// <summary>
    /// Gets negated retrieval criteria for this index.<br/>
    /// The facade avoids boolean negation parameters and leaves room for ordered complement planning over key extents.<br/>
    /// </summary>
    public LibraDexNotFacade<TKey, TIdentity> Not { get; }

    /// <summary>
    /// Gets aggregate operations over this index.<br/>
    /// Aggregates are separated from retrieval because they compute values and should avoid identity materialization whenever possible.<br/>
    /// </summary>
    public LibraDexAggregateFacade<TKey, TIdentity> Aggregates { get; }

    /// <summary>
    /// Gets grouped retrieval operations over this index.<br/>
    /// Grouping is streaming-first result shaping and should preserve group keys instead of forcing callers to rebuild groups from flattened rows.<br/>
    /// </summary>
    public LibraDexGroupedFacade<TKey, TIdentity> Grouped { get; }

    /// <summary>
    /// Gets grouped retrieval operations over this index.<br/>
    /// `Groups` is the preferred short public spelling for grouping result shapes; `Grouped` remains an alias while the scaffold is still evolving.<br/>
    /// </summary>
    public LibraDexGroupedFacade<TKey, TIdentity> Groups => Grouped;

    /// <summary>
    /// Gets passive stats, marker/delta helpers, and explicit layout snapshots for this index.<br/>
    /// Expensive physical layout calculations should remain explicit snapshot calls rather than hidden work on ordinary properties.<br/>
    /// </summary>
    public LibraDexIndexStats<TKey, TIdentity> Stats { get; }

    /// <summary>
    /// Gets explicit index-level maintenance operations.<br/>
    /// Route optimization, validation, repacking, and cache work should be requested deliberately rather than hidden inside ordinary reads or writes.<br/>
    /// </summary>
    public LibraDexIndexMaintenance<TKey, TIdentity> Maintenance { get; }

    /// <summary>
    /// Gets prepared helper construction for this index.<br/>
    /// Prepared helpers are limited to reusable runtime objects that remove repeated work, such as membership sets and grouping specs.<br/>
    /// </summary>
    public LibraDexPrepareFacade<TKey, TIdentity> Prepare { get; }

    /// <summary>
    /// Gets first-position tuple retrieval methods.<br/>
    /// Positional retrieval returns real indexed entries and is separate from aggregate minimum values.<br/>
    /// </summary>
    public LibraDexPositionalFacade<TKey, TIdentity, LibraDexTuple<TKey, TIdentity>> First { get; }

    /// <summary>
    /// Gets last-position tuple retrieval methods.<br/>
    /// Positional retrieval returns real indexed entries and is separate from aggregate maximum values.<br/>
    /// </summary>
    public LibraDexPositionalFacade<TKey, TIdentity, LibraDexTuple<TKey, TIdentity>> Last { get; }

    /// <summary>
    /// Gets middle-position tuple retrieval methods.<br/>
    /// Middle retrieval returns a centered window of real indexed entries; it is not median interpolation or mathematical midpoint calculation.<br/>
    /// </summary>
    public LibraDexPositionalFacade<TKey, TIdentity, LibraDexTuple<TKey, TIdentity>> Middle { get; }

    /// <summary>
    /// Gets absolute-rank tuple retrieval methods.<br/>
    /// Rank retrieval returns real indexed entries at ordered positions; it is not a value aggregate.<br/>
    /// </summary>
    public LibraDexRankFacade<TKey, TIdentity, LibraDexTuple<TKey, TIdentity>> Rank { get; }

    /// <summary>
    /// Gets percentile-rank tuple retrieval methods.<br/>
    /// Percent-rank retrieval returns real indexed entries near an ordered percentile and is separate from aggregate percentile values.<br/>
    /// </summary>
    public LibraDexPercentRankFacade<TKey, TIdentity, LibraDexTuple<TKey, TIdentity>> PercentRank { get; }

    internal LibraDexFileSession Session => session;

    internal LibraDexGenericScalarShape Shape => shape;

    internal Catalog? Catalog => catalog;

    internal LibraDexRangeReader<TKey, TIdentity> OpenCriteriaReader(
        LibraDexCriteriaKind criteriaKind,
        TKey key,
        bool hasKey,
        QueryDirection direction,
        int skip,
        int? take,
        RetrievalScope scope,
        bool isNegated)
    {
        ThrowIfDisposed();
        if (isNegated)
        {
            throw new NotSupportedException("Negated criteria execution requires ordered complement planning and is not connected to physical readers yet.");
        }

        if (direction != QueryDirection.Ascending)
        {
            throw new NotSupportedException("Descending criteria traversal is part of the public API scaffold but is not connected to this physical reader yet.");
        }

        if (scope != RetrievalScope.Tuples)
        {
            throw new NotSupportedException("Distinct-key retrieval scopes are part of the public API scaffold but are not connected to this physical reader yet.");
        }

        if (skip < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(skip), skip, "Skip cannot be negative.");
        }

        if (take is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(take), take, "Take cannot be negative.");
        }

        LibraDexRangeReader<TKey, TIdentity> reader = criteriaKind switch
        {
            LibraDexCriteriaKind.All => OpenAllRangeReader(),
            LibraDexCriteriaKind.Before when hasKey => OpenBeforeRangeReader(key),
            LibraDexCriteriaKind.AtOrBefore when hasKey => OpenAtOrBeforeRangeReader(key),
            LibraDexCriteriaKind.After when hasKey => OpenAfterRangeReader(key),
            LibraDexCriteriaKind.AtOrAfter when hasKey => OpenAtOrAfterRangeReader(key),
            _ => throw new NotSupportedException($"{criteriaKind} queries are part of the public API scaffold but are not connected to physical execution yet.")
        };

        if (skip > 0)
        {
            _ = reader.Skip(skip);
        }

        reader.ApplyTakeLimit(take);
        return reader;
    }

    /// <summary>
    /// Streams tuple results for the direct criteria facade through the same physical reader primitives used by condition execution.<br/>
    /// Range-backed criteria use ordered range readers, membership criteria use equality-reader fan-out, and scan-backed predicates read full key/identity tuples before applying the key predicate.<br/>
    /// This method keeps handwritten direct query syntax executable while preserving the condition builder as the canonical multi-index representation.<br/>
    /// </summary>
    /// <param name="criteriaKind">The criteria kind to execute.</param>
    /// <param name="operand">The optional operand captured by the public criteria method.</param>
    /// <param name="hasOperand">Whether an operand was captured.</param>
    /// <param name="direction">The requested traversal direction.</param>
    /// <param name="skip">The number of matching rows to skip.</param>
    /// <param name="take">The optional maximum number of rows to return.</param>
    /// <param name="scope">The duplicate-key retrieval scope.</param>
    /// <param name="isNegated">Whether this direct criteria descriptor is negated.</param>
    /// <returns>A forward-only tuple sequence.</returns>
    internal IEnumerable<LibraDexTuple<TKey, TIdentity>> IterateCriteriaTuples(
        LibraDexCriteriaKind criteriaKind,
        object? operand,
        bool hasOperand,
        QueryDirection direction,
        int skip,
        int? take,
        RetrievalScope scope,
        bool isNegated)
    {
        ThrowIfDisposed();
        if (isNegated)
        {
            throw new NotSupportedException("Negated direct criteria execution requires ordered complement planning and is not connected to physical readers yet.");
        }

        if (scope != RetrievalScope.Tuples)
        {
            throw new NotSupportedException("Distinct-key retrieval scopes are part of the public API scaffold but are not connected to this physical reader yet.");
        }

        if (skip < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(skip), skip, "Skip cannot be negative.");
        }

        if (take is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(take), take, "Take cannot be negative.");
        }

        if (direction == QueryDirection.Descending)
        {
            List<LibraDexTuple<TKey, TIdentity>> rows = IterateCriteriaTuples(
                criteriaKind,
                operand,
                hasOperand,
                QueryDirection.Ascending,
                skip: 0,
                take: null,
                scope,
                isNegated).ToList();
            rows.Reverse();
            IEnumerable<LibraDexTuple<TKey, TIdentity>> shaped = rows;
            if (skip > 0)
            {
                shaped = shaped.Skip(skip);
            }

            if (take is not null)
            {
                shaped = shaped.Take(take.Value);
            }

            foreach (LibraDexTuple<TKey, TIdentity> tuple in shaped)
            {
                yield return tuple;
            }

            yield break;
        }

        switch (criteriaKind)
        {
            case LibraDexCriteriaKind.All:
            case LibraDexCriteriaKind.Before:
            case LibraDexCriteriaKind.AtOrBefore:
            case LibraDexCriteriaKind.After:
            case LibraDexCriteriaKind.AtOrAfter:
            {
                TKey keyOperand = hasOperand && operand is TKey typedKey
                    ? typedKey
                    : default!;
                using LibraDexRangeReader<TKey, TIdentity> reader = OpenCriteriaReader(criteriaKind, keyOperand, hasOperand, direction, skip, take, scope, isNegated);
                while (reader.TryReadNext(out TKey key, out TIdentity identity))
                {
                    yield return new LibraDexTuple<TKey, TIdentity>(key, identity);
                }

                yield break;
            }
            case LibraDexCriteriaKind.In:
            case LibraDexCriteriaKind.InSet:
                foreach (LibraDexTuple<TKey, TIdentity> tuple in IterateMembershipTuples(RequireCriteriaOperand(operand, hasOperand), skip, take))
                {
                    yield return tuple;
                }

                yield break;
            case LibraDexCriteriaKind.Prefix:
            case LibraDexCriteriaKind.Suffix:
            case LibraDexCriteriaKind.Contains:
            case LibraDexCriteriaKind.Matches:
                foreach (LibraDexTuple<TKey, TIdentity> tuple in IterateScannedTuples(criteriaKind, RequireCriteriaOperand(operand, hasOperand), skip, take))
                {
                    yield return tuple;
                }

                yield break;
            default:
                throw new NotSupportedException($"{criteriaKind} direct criteria execution is not connected to physical readers yet.");
        }
    }

    /// <summary>
    /// Starts a typed generic durability batch for bulk mutation.<br/>
    /// Batch inserts use developer-facing generic values and defer lower-level commit requests until the returned batch commits or aborts.<br/>
    /// Write intent is an optional optimization hint and `default` preserves the normal shape behavior.<br/>
    /// </summary>
    /// <param name="writeIntent">Optional coarse write-pattern hints for the batch.</param>
    /// <returns>A typed batch object that accepts generic inserts and controls the durability publication boundary.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the index has already been disposed.</exception>
    /// <exception cref="InvalidOperationException">Thrown when another batch is already active on the owning session.</exception>
    public LibraDexBatch<TKey, TIdentity> BeginBatch(LibraDexWriteIntent writeIntent = default)
    {
        ThrowIfDisposed();
        if (BatchManager.IsEnabled)
        {
            throw new InvalidOperationException("The index BatchManager is already enabled; use BatchManager.Commit or BatchManager.CommitAndDisable to control the active batch.");
        }

        return new LibraDexBatch<TKey, TIdentity>(this, session.BeginDurabilityBatch(writeIntent));
    }

    /// <summary>
    /// Inserts one typed key and typed identity into this index.<br/>
    /// Values are encoded according to the physical shape selected at create/open time, then routed through the matching fixed-scalar storage path.<br/>
    /// </summary>
    /// <param name="key">The typed key value to insert.</param>
    /// <param name="identity">The typed identity value associated with the key.</param>
    /// <returns>The insert result plus any route-create and insert commit telemetry.</returns>
    public LibraDexGenericInsertResult Insert(TKey key, TIdentity identity)
    {
        ThrowIfDisposed();
        if (catalog?.TryGetActiveIdentityGroupBatch(Group, out CatalogIdentityGroupBatchManager? groupBatch) == true)
        {
            return groupBatch.Insert(this, key, identity);
        }

        if (BatchManager.IsEnabled)
        {
            return BatchManager.Insert(key, identity);
        }

        using LibraDexBatch<TKey, TIdentity> batch = BeginBatch();
        LibraDexGenericInsertResult result = batch.Insert(key, identity);
        _ = batch.Commit();
        return result;
    }

    /// <summary>
    /// Captures an exact-key tuple query.<br/>
    /// For duplicate-key indexes this should resolve to the contiguous key run for the requested key rather than row-by-row filtering.<br/>
    /// </summary>
    /// <param name="key">The key to find.</param>
    /// <param name="direction">The requested traversal direction for matching tuples.</param>
    /// <param name="skip">The number of matching tuples to skip before returning rows.</param>
    /// <param name="take">The optional maximum number of matching tuples to return.</param>
    /// <param name="scope">The duplicate-key retrieval scope.</param>
    /// <param name="bookmark">Optional bookmark state used to resume a prior query.</param>
    /// <returns>A lightweight query descriptor that can open a cursor when executed.</returns>
    public LibraDexQuery<TKey, TIdentity> Find(
        TKey key,
        QueryDirection direction = QueryDirection.Ascending,
        int skip = 0,
        int? take = null,
        RetrievalScope scope = RetrievalScope.Tuples,
        LibraDexBookmark? bookmark = null)
    {
        return Between(key, key, direction, skip, take, scope, bookmark);
    }

    /// <summary>
    /// Captures an inclusive range tuple query.<br/>
    /// The returned descriptor records public query intent and can open the current generic range reader for connected physical shapes.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower key.</param>
    /// <param name="upperKey">The inclusive upper key.</param>
    /// <param name="direction">The requested traversal direction for matching tuples.</param>
    /// <param name="skip">The number of matching tuples to skip before returning rows.</param>
    /// <param name="take">The optional maximum number of matching tuples to return.</param>
    /// <param name="scope">The duplicate-key retrieval scope.</param>
    /// <param name="bookmark">Optional bookmark state used to resume a prior query.</param>
    /// <returns>A lightweight query descriptor that can open a cursor when executed.</returns>
    public LibraDexQuery<TKey, TIdentity> Between(
        TKey lowerKey,
        TKey upperKey,
        QueryDirection direction = QueryDirection.Ascending,
        int skip = 0,
        int? take = null,
        RetrievalScope scope = RetrievalScope.Tuples,
        LibraDexBookmark? bookmark = null)
    {
        ThrowIfDisposed();
        if (skip < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(skip), skip, "Skip cannot be negative.");
        }

        if (take is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(take), take, "Take cannot be negative.");
        }

        LibraDexExecutionKind executionKind = direction == QueryDirection.Descending
            ? LibraDexExecutionKind.ReverseTraversal
            : LibraDexExecutionKind.FastPath;
        return new LibraDexQuery<TKey, TIdentity>(
            this,
            lowerKey,
            upperKey,
            direction,
            skip,
            take,
            scope,
            bookmark,
            executionKind);
    }

    /// <summary>
    /// Captures an all-tuples query over the index.<br/>
    /// This criterion is named in the public API now; physical all-index bounds will be connected with ordered min/max or full-index traversal support.<br/>
    /// </summary>
    /// <param name="direction">The requested traversal direction.</param>
    /// <param name="skip">The number of matching tuples to skip before returning rows.</param>
    /// <param name="take">The optional maximum number of matching tuples to return.</param>
    /// <param name="scope">The duplicate-key retrieval scope.</param>
    /// <param name="bookmark">Optional bookmark state used to resume a prior query.</param>
    /// <returns>A lightweight criteria descriptor for all-index retrieval.</returns>
    public LibraDexCriteriaQuery<TKey, TIdentity> All(
        QueryDirection direction = QueryDirection.Ascending,
        int skip = 0,
        int? take = null,
        RetrievalScope scope = RetrievalScope.Tuples,
        LibraDexBookmark? bookmark = null)
    {
        return CreateCriteriaQuery(LibraDexCriteriaKind.All, direction, skip, take, scope, bookmark, DirectionExecutionKind(direction));
    }

    /// <summary>
    /// Captures a before-key tuple query.<br/>
    /// This criterion is named in the public API now; physical lower-bound discovery will be connected with ordered min/bookmark support.<br/>
    /// </summary>
    public LibraDexCriteriaQuery<TKey, TIdentity> Before(TKey key, QueryDirection direction = QueryDirection.Ascending)
    {
        return CreateCriteriaQuery(LibraDexCriteriaKind.Before, direction, 0, null, RetrievalScope.Tuples, null, DirectionExecutionKind(direction), key, hasKey: true);
    }

    /// <summary>
    /// Captures an at-or-before-key tuple query.<br/>
    /// This criterion is named in the public API now; physical lower-bound discovery will be connected with ordered min/bookmark support.<br/>
    /// </summary>
    public LibraDexCriteriaQuery<TKey, TIdentity> AtOrBefore(TKey key, QueryDirection direction = QueryDirection.Ascending)
    {
        return CreateCriteriaQuery(LibraDexCriteriaKind.AtOrBefore, direction, 0, null, RetrievalScope.Tuples, null, DirectionExecutionKind(direction), key, hasKey: true);
    }

    /// <summary>
    /// Captures an after-key tuple query.<br/>
    /// This criterion is named in the public API now; physical upper-bound discovery will be connected with ordered max/bookmark support.<br/>
    /// </summary>
    public LibraDexCriteriaQuery<TKey, TIdentity> After(TKey key, QueryDirection direction = QueryDirection.Ascending)
    {
        return CreateCriteriaQuery(LibraDexCriteriaKind.After, direction, 0, null, RetrievalScope.Tuples, null, DirectionExecutionKind(direction), key, hasKey: true);
    }

    /// <summary>
    /// Captures an at-or-after-key tuple query.<br/>
    /// This criterion is named in the public API now; physical upper-bound discovery will be connected with ordered max/bookmark support.<br/>
    /// </summary>
    public LibraDexCriteriaQuery<TKey, TIdentity> AtOrAfter(TKey key, QueryDirection direction = QueryDirection.Ascending)
    {
        return CreateCriteriaQuery(LibraDexCriteriaKind.AtOrAfter, direction, 0, null, RetrievalScope.Tuples, null, DirectionExecutionKind(direction), key, hasKey: true);
    }

    /// <summary>
    /// Captures a prefix lookup query.<br/>
    /// Prefix can be fast when the selected key projection is byte-direction aligned; otherwise it may be projection-backed or scan-backed as diagnostics report.<br/>
    /// </summary>
    /// <param name="prefix">The prefix value to match.</param>
    /// <param name="direction">The requested traversal direction.</param>
    /// <returns>A lightweight criteria descriptor for prefix retrieval.</returns>
    public LibraDexCriteriaQuery<TKey, TIdentity> Prefix(TKey prefix, QueryDirection direction = QueryDirection.Ascending)
    {
        _ = direction;
        return CreateCriteriaQuery(LibraDexCriteriaKind.Prefix, direction, 0, null, RetrievalScope.Tuples, null, LibraDexExecutionKind.Scan, prefix, hasKey: true, operand: prefix, hasOperand: true);
    }

    /// <summary>
    /// Captures a suffix lookup query.<br/>
    /// Suffix can be fast when a reversed key projection is maintained and aligned; otherwise it is expected to be scan-backed.<br/>
    /// </summary>
    /// <param name="suffix">The suffix value to match.</param>
    /// <param name="direction">The requested traversal direction.</param>
    /// <returns>A lightweight criteria descriptor for suffix retrieval.</returns>
    public LibraDexCriteriaQuery<TKey, TIdentity> Suffix(TKey suffix, QueryDirection direction = QueryDirection.Ascending)
    {
        return CreateCriteriaQuery(LibraDexCriteriaKind.Suffix, direction, 0, null, RetrievalScope.Tuples, null, LibraDexExecutionKind.Scan, suffix, hasKey: true, operand: suffix, hasOperand: true);
    }

    /// <summary>
    /// Captures a contains lookup query.<br/>
    /// Contains is part of the core intent vocabulary but is normally scan-backed unless a future contains-capable projection is maintained.<br/>
    /// </summary>
    /// <param name="value">The contained value or pattern fragment to match.</param>
    /// <param name="direction">The requested traversal direction.</param>
    /// <returns>A lightweight criteria descriptor for contains retrieval.</returns>
    public LibraDexCriteriaQuery<TKey, TIdentity> Contains(TKey value, QueryDirection direction = QueryDirection.Ascending)
    {
        _ = direction;
        return CreateCriteriaQuery(LibraDexCriteriaKind.Contains, direction, 0, null, RetrievalScope.Tuples, null, LibraDexExecutionKind.Scan, value, hasKey: true, operand: value, hasOperand: true);
    }

    /// <summary>
    /// Captures a pattern lookup query.<br/>
    /// Pattern matching is named in the core surface so higher layers do not have to express it as a vague scan; physical support depends on maintained projections or typed scan predicates.<br/>
    /// </summary>
    /// <param name="pattern">The pattern object understood by the key class or higher-level adapter.</param>
    /// <param name="direction">The requested traversal direction.</param>
    /// <returns>A lightweight criteria descriptor for pattern retrieval.</returns>
    public LibraDexCriteriaQuery<TKey, TIdentity> Matches(object pattern, QueryDirection direction = QueryDirection.Ascending)
    {
        _ = pattern ?? throw new ArgumentNullException(nameof(pattern));
        _ = direction;
        return CreateCriteriaQuery(LibraDexCriteriaKind.Matches, direction, 0, null, RetrievalScope.Tuples, null, LibraDexExecutionKind.Scan, operand: pattern, hasOperand: true);
    }

    /// <summary>
    /// Captures a membership query over ordinary values.<br/>
    /// The enumerable shape is convenient for one-off callers; repeated membership work should use `InSet` with a prepared set so LibraDex can pre-encode values later.<br/>
    /// </summary>
    /// <param name="keys">The keys to match.</param>
    /// <param name="direction">The requested traversal direction.</param>
    /// <returns>A lightweight criteria descriptor for membership retrieval.</returns>
    public LibraDexCriteriaQuery<TKey, TIdentity> In(IEnumerable<TKey> keys, QueryDirection direction = QueryDirection.Ascending)
    {
        TKey[] capturedKeys = (keys ?? throw new ArgumentNullException(nameof(keys))).ToArray();
        return CreateCriteriaQuery(LibraDexCriteriaKind.In, direction, 0, null, RetrievalScope.Tuples, null, LibraDexExecutionKind.Projection, operand: capturedKeys, hasOperand: true);
    }

    /// <summary>
    /// Captures a membership query over a prepared set.<br/>
    /// Prepared sets are intended to pre-encode and organize membership values once so scans or fan-out lookups do not rebuild set state per query.<br/>
    /// </summary>
    /// <param name="set">The prepared set to match.</param>
    /// <param name="direction">The requested traversal direction.</param>
    /// <returns>A lightweight criteria descriptor for prepared membership retrieval.</returns>
    public LibraDexCriteriaQuery<TKey, TIdentity> InSet(LibraDexPreparedSet<TKey> set, QueryDirection direction = QueryDirection.Ascending)
    {
        _ = set ?? throw new ArgumentNullException(nameof(set));
        return CreateCriteriaQuery(LibraDexCriteriaKind.InSet, direction, 0, null, RetrievalScope.Tuples, null, LibraDexExecutionKind.Projection, operand: set, hasOperand: true);
    }

    /// <summary>
    /// Determines whether an exact key has at least one matching identity.<br/>
    /// The connected first slice routes through the current exact-key range count and avoids materializing matching identities.<br/>
    /// </summary>
    /// <param name="key">The key to test.</param>
    /// <returns><see langword="true"/> when at least one tuple exists for the key.</returns>
    public bool Exists(TKey key)
    {
        return Aggregates.Count.Find(key) > 0;
    }

    /// <summary>
    /// Determines whether any key in an ordinary value sequence has at least one matching identity.<br/>
    /// This is a convenience existence shortcut; repeated membership checks should prefer `ExistsInSet` once prepared set execution is connected.<br/>
    /// </summary>
    /// <param name="keys">The keys to test.</param>
    /// <returns><see langword="true"/> when any supplied key exists in the index.</returns>
    public bool ExistsIn(IEnumerable<TKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        foreach (TKey key in keys)
        {
            if (Exists(key))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Determines whether any key in a prepared set has at least one matching identity.<br/>
    /// The first connected implementation reuses exact-key existence checks over the prepared values and stops at the first match.<br/>
    /// Later implementations can replace this loop with encoded membership state without changing the public call shape.<br/>
    /// </summary>
    /// <param name="set">The prepared set to test.</param>
    /// <returns><see langword="true"/> when any set key exists in the index.</returns>
    public bool ExistsInSet(LibraDexPreparedSet<TKey> set)
    {
        ArgumentNullException.ThrowIfNull(set);
        foreach (TKey key in set.Values)
        {
            if (Exists(key))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Inserts one non-generic key/identity tuple after validating the runtime values against this index's persisted CLR type contract.<br/>
    /// The method then delegates to the typed insert path so batching, telemetry, duplicate-key behavior, and physical routing remain identical to `Insert(TKey, TIdentity)`.<br/>
    /// </summary>
    /// <param name="key">The runtime key value to insert.</param>
    /// <param name="identity">The runtime identity value to associate with the key.</param>
    /// <returns>The insert result plus any route-create and insert commit telemetry.</returns>
    LibraDexGenericInsertResult IIndex.Insert(object key, object identity)
    {
        return Insert(RequireObjectKey(key, nameof(key)), RequireObjectIdentity(identity, nameof(identity)));
    }

    /// <summary>
    /// Captures a non-generic exact-key query after validating the runtime key value against this index's key type.<br/>
    /// The returned descriptor preserves criteria-first/result-shape-last programmatic grammar while keeping typed execution inside this generic index.<br/>
    /// </summary>
    /// <param name="key">The runtime key value to find.</param>
    /// <returns>A non-generic query descriptor.</returns>
    IIndexQuery IIndex.Find(object key)
    {
        return new LibraDexObjectIndexQuery<TKey, TIdentity>(this, Find(RequireObjectKey(key, nameof(key))).Diagnostics);
    }

    /// <summary>
    /// Captures a non-generic inclusive range query after validating both runtime key values against this index's key type.<br/>
    /// </summary>
    /// <param name="lowerKey">The runtime lower key.</param>
    /// <param name="upperKey">The runtime upper key.</param>
    /// <returns>A non-generic query descriptor.</returns>
    IIndexQuery IIndex.Between(object lowerKey, object upperKey)
    {
        return new LibraDexObjectIndexQuery<TKey, TIdentity>(
            this,
            Between(RequireObjectKey(lowerKey, nameof(lowerKey)), RequireObjectKey(upperKey, nameof(upperKey))).Diagnostics);
    }

    /// <summary>
    /// Captures a non-generic all-tuples query over this index.<br/>
    /// </summary>
    /// <returns>A non-generic query descriptor.</returns>
    IIndexQuery IIndex.All()
    {
        return new LibraDexObjectIndexQuery<TKey, TIdentity>(this, All().Diagnostics);
    }

    /// <summary>
    /// Captures a non-generic exclusive upper-bound query after validating the runtime key value against this index's key type.<br/>
    /// </summary>
    /// <param name="key">The runtime boundary key.</param>
    /// <returns>A non-generic query descriptor.</returns>
    IIndexQuery IIndex.Before(object key)
    {
        return new LibraDexObjectIndexQuery<TKey, TIdentity>(this, Before(RequireObjectKey(key, nameof(key))).Diagnostics);
    }

    /// <summary>
    /// Captures a non-generic inclusive upper-bound query after validating the runtime key value against this index's key type.<br/>
    /// </summary>
    /// <param name="key">The runtime boundary key.</param>
    /// <returns>A non-generic query descriptor.</returns>
    IIndexQuery IIndex.AtOrBefore(object key)
    {
        return new LibraDexObjectIndexQuery<TKey, TIdentity>(this, AtOrBefore(RequireObjectKey(key, nameof(key))).Diagnostics);
    }

    /// <summary>
    /// Captures a non-generic exclusive lower-bound query after validating the runtime key value against this index's key type.<br/>
    /// </summary>
    /// <param name="key">The runtime boundary key.</param>
    /// <returns>A non-generic query descriptor.</returns>
    IIndexQuery IIndex.After(object key)
    {
        return new LibraDexObjectIndexQuery<TKey, TIdentity>(this, After(RequireObjectKey(key, nameof(key))).Diagnostics);
    }

    /// <summary>
    /// Captures a non-generic inclusive lower-bound query after validating the runtime key value against this index's key type.<br/>
    /// </summary>
    /// <param name="key">The runtime boundary key.</param>
    /// <returns>A non-generic query descriptor.</returns>
    IIndexQuery IIndex.AtOrAfter(object key)
    {
        return new LibraDexObjectIndexQuery<TKey, TIdentity>(this, AtOrAfter(RequireObjectKey(key, nameof(key))).Diagnostics);
    }

    /// <summary>
    /// Captures a non-generic prefix query after validating the runtime key value against this index's key type.<br/>
    /// </summary>
    /// <param name="prefix">The runtime prefix value.</param>
    /// <returns>A non-generic query descriptor.</returns>
    IIndexQuery IIndex.Prefix(object prefix)
    {
        return new LibraDexObjectIndexQuery<TKey, TIdentity>(this, Prefix(RequireObjectKey(prefix, nameof(prefix))).Diagnostics);
    }

    /// <summary>
    /// Captures a non-generic suffix query after validating the runtime key value against this index's key type.<br/>
    /// </summary>
    /// <param name="suffix">The runtime suffix value.</param>
    /// <returns>A non-generic query descriptor.</returns>
    IIndexQuery IIndex.Suffix(object suffix)
    {
        return new LibraDexObjectIndexQuery<TKey, TIdentity>(this, Suffix(RequireObjectKey(suffix, nameof(suffix))).Diagnostics);
    }

    /// <summary>
    /// Captures a non-generic contains query after validating the runtime key value against this index's key type.<br/>
    /// </summary>
    /// <param name="value">The runtime contained value.</param>
    /// <returns>A non-generic query descriptor.</returns>
    IIndexQuery IIndex.Contains(object value)
    {
        return new LibraDexObjectIndexQuery<TKey, TIdentity>(this, Contains(RequireObjectKey(value, nameof(value))).Diagnostics);
    }

    /// <summary>
    /// Captures a non-generic membership query after validating each runtime key value against this index's key type.<br/>
    /// </summary>
    /// <param name="keys">The runtime key values to match.</param>
    /// <returns>A non-generic query descriptor.</returns>
    IIndexQuery IIndex.In(IEnumerable<object> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        TKey[] typedKeys = keys.Select(key => RequireObjectKey(key, nameof(keys))).ToArray();
        return new LibraDexObjectIndexQuery<TKey, TIdentity>(this, In(typedKeys).Diagnostics);
    }

    /// <summary>
    /// Captures a non-generic prepared-set membership query after validating each runtime key value against this index's key type.<br/>
    /// The current descriptor creates a typed prepared set immediately so `InSet` remains distinct from ordinary `In` for planners and diagnostics.<br/>
    /// </summary>
    /// <param name="keys">The runtime key values to prepare and match.</param>
    /// <returns>A non-generic query descriptor.</returns>
    IIndexQuery IIndex.InSet(IEnumerable<object> keys)
    {
        return ((IIndex)this).InSet(((IIndex)this).PrepareInSet(keys));
    }

    /// <summary>
    /// Captures a non-generic prepared-set membership query from a prepared object set.<br/>
    /// </summary>
    /// <param name="set">The prepared non-generic key set.</param>
    /// <returns>A non-generic query descriptor.</returns>
    IIndexQuery IIndex.InSet(LibraDexPreparedObjectSet set)
    {
        TKey[] typedKeys = RequirePreparedObjectSet(set, nameof(set));
        return new LibraDexObjectIndexQuery<TKey, TIdentity>(this, InSet(new LibraDexPreparedSet<TKey>(typedKeys)).Diagnostics);
    }

    /// <summary>
    /// Prepares a strict non-generic membership set after validating all runtime keys against this index's key type.<br/>
    /// </summary>
    /// <param name="keys">The runtime key values to prepare.</param>
    /// <returns>A prepared non-generic key set.</returns>
    LibraDexPreparedObjectSet IIndex.PrepareInSet(IEnumerable<object> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        object[] values = keys.Select(key => (object)RequireObjectKey(key, nameof(keys))!).ToArray();
        return new LibraDexPreparedObjectSet(typeof(TKey), values);
    }

    /// <summary>
    /// Captures a non-generic pattern query.<br/>
    /// Pattern validation is intentionally deferred to the selected key family and projection because different key classes may use different pattern representations.<br/>
    /// </summary>
    /// <param name="pattern">The runtime pattern descriptor.</param>
    /// <returns>A non-generic query descriptor.</returns>
    IIndexQuery IIndex.Matches(object pattern)
    {
        return new LibraDexObjectIndexQuery<TKey, TIdentity>(this, Matches(pattern).Diagnostics);
    }

    /// <summary>
    /// Determines whether a non-generic runtime key exists after validating it against this index's key type.<br/>
    /// </summary>
    /// <param name="key">The runtime key value to test.</param>
    /// <returns><see langword="true"/> when at least one tuple exists for the key.</returns>
    bool IIndex.Exists(object key)
    {
        return Exists(RequireObjectKey(key, nameof(key)));
    }

    /// <summary>
    /// Determines whether any non-generic runtime key exists after validating every supplied key against this index's key type.<br/>
    /// </summary>
    /// <param name="keys">The runtime key values to test.</param>
    /// <returns><see langword="true"/> when any supplied key exists in the index.</returns>
    bool IIndex.ExistsIn(IEnumerable<object> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        TKey[] typedKeys = keys.Select(key => RequireObjectKey(key, nameof(keys))).ToArray();
        return ExistsIn(typedKeys);
    }

    /// <summary>
    /// Determines whether any key in a non-generic prepared-set sequence exists after validating every supplied key against this index's key type.<br/>
    /// </summary>
    /// <param name="keys">The runtime key values to prepare and test.</param>
    /// <returns><see langword="true"/> when any supplied key exists in the index.</returns>
    bool IIndex.ExistsInSet(IEnumerable<object> keys)
    {
        return ExistsInSet(new LibraDexPreparedSet<TKey>(RequirePreparedObjectSet(((IIndex)this).PrepareInSet(keys), nameof(keys))));
    }

    IReadOnlyList<object> IIdentityPrimitiveExecutor.ExecuteIdentityPrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        return ExecuteIdentityPrimitive(request);
    }

    IEnumerable<object> IIdentityPrimitiveExecutor.IterateIdentityPrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        return IterateIdentityPrimitive(request);
    }

    long IIdentityPrimitiveExecutor.CountIdentityPrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        return CountIdentityPrimitive(request);
    }

    private IReadOnlyList<object> ExecuteIdentityPrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        ThrowIfDisposed();
        return request.CriteriaKind switch
        {
            LibraDexCriteriaKind.All => MaterializeIdentityObjects(OpenAllRangeReader(), request.TakeLimit),
            LibraDexCriteriaKind.Find => MaterializeIdentityObjects(OpenRangeReader(
                RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)),
                RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values))), request.TakeLimit),
            LibraDexCriteriaKind.Between => MaterializeIdentityObjects(OpenRangeReader(
                RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)),
                RequireObjectKey(RequireCriterionValue(request.Values, 1), nameof(request.Values))), request.TakeLimit),
            LibraDexCriteriaKind.Before => MaterializeIdentityObjects(OpenBeforeRangeReader(RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values))), request.TakeLimit),
            LibraDexCriteriaKind.AtOrBefore => MaterializeIdentityObjects(OpenAtOrBeforeRangeReader(RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values))), request.TakeLimit),
            LibraDexCriteriaKind.After => MaterializeIdentityObjects(OpenAfterRangeReader(RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values))), request.TakeLimit),
            LibraDexCriteriaKind.AtOrAfter => MaterializeIdentityObjects(OpenAtOrAfterRangeReader(RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values))), request.TakeLimit),
            LibraDexCriteriaKind.In => MaterializeMembershipIdentityObjects(request.Values, request.TakeLimit),
            LibraDexCriteriaKind.InSet => MaterializeMembershipIdentityObjects(request.Values, request.TakeLimit),
            LibraDexCriteriaKind.Prefix => MaterializeScannedIdentityObjects(
                LibraDexCriteriaKind.Prefix,
                RequireCriterionValue(request.Values, 0),
                request.TakeLimit),
            LibraDexCriteriaKind.Suffix => MaterializeScannedIdentityObjects(
                LibraDexCriteriaKind.Suffix,
                RequireCriterionValue(request.Values, 0),
                request.TakeLimit),
            LibraDexCriteriaKind.Contains => MaterializeScannedIdentityObjects(
                LibraDexCriteriaKind.Contains,
                RequireCriterionValue(request.Values, 0),
                request.TakeLimit),
            LibraDexCriteriaKind.Matches => MaterializeScannedIdentityObjects(
                LibraDexCriteriaKind.Matches,
                RequireCriterionValue(request.Values, 0),
                request.TakeLimit),
            _ => throw new NotSupportedException($"{request.CriteriaKind} identity execution is not connected to physical readers yet.")
        };
    }

    /// <summary>
    /// Streams identities for one normalized condition-builder primitive through the generic range-reader path.<br/>
    /// This is the internal non-materializing equivalent of <see cref="ExecuteIdentityPrimitive(LibraDexIdentityPrimitiveRequest)"/> and is used by plan-natural condition iteration.<br/>
    /// Membership requests preserve supplied key order while honoring the request take limit across the whole key fan-out.<br/>
    /// </summary>
    /// <param name="request">The primitive request to execute.</param>
    /// <returns>A forward-only identity sequence.</returns>
    private IEnumerable<object> IterateIdentityPrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        ThrowIfDisposed();
        switch (request.CriteriaKind)
        {
            case LibraDexCriteriaKind.All:
                foreach (object identity in IterateIdentityObjects(OpenAllRangeReader(), request.TakeLimit))
                {
                    yield return identity;
                }

                yield break;
            case LibraDexCriteriaKind.Find:
                foreach (object identity in IterateIdentityObjects(OpenRangeReader(
                    RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)),
                    RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values))), request.TakeLimit))
                {
                    yield return identity;
                }

                yield break;
            case LibraDexCriteriaKind.Between:
                foreach (object identity in IterateIdentityObjects(OpenRangeReader(
                    RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)),
                    RequireObjectKey(RequireCriterionValue(request.Values, 1), nameof(request.Values))), request.TakeLimit))
                {
                    yield return identity;
                }

                yield break;
            case LibraDexCriteriaKind.Before:
                foreach (object identity in IterateIdentityObjects(OpenBeforeRangeReader(RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values))), request.TakeLimit))
                {
                    yield return identity;
                }

                yield break;
            case LibraDexCriteriaKind.AtOrBefore:
                foreach (object identity in IterateIdentityObjects(OpenAtOrBeforeRangeReader(RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values))), request.TakeLimit))
                {
                    yield return identity;
                }

                yield break;
            case LibraDexCriteriaKind.After:
                foreach (object identity in IterateIdentityObjects(OpenAfterRangeReader(RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values))), request.TakeLimit))
                {
                    yield return identity;
                }

                yield break;
            case LibraDexCriteriaKind.AtOrAfter:
                foreach (object identity in IterateIdentityObjects(OpenAtOrAfterRangeReader(RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values))), request.TakeLimit))
                {
                    yield return identity;
                }

                yield break;
            case LibraDexCriteriaKind.In:
            case LibraDexCriteriaKind.InSet:
                foreach (object identity in IterateMembershipIdentityObjects(request.Values, request.TakeLimit))
                {
                    yield return identity;
                }

                yield break;
            case LibraDexCriteriaKind.Prefix:
            case LibraDexCriteriaKind.Suffix:
            case LibraDexCriteriaKind.Contains:
            case LibraDexCriteriaKind.Matches:
                foreach (object identity in IterateScannedIdentityObjects(
                    request.CriteriaKind,
                    RequireCriterionValue(request.Values, 0),
                    request.TakeLimit))
                {
                    yield return identity;
                }

                yield break;
            default:
                throw new NotSupportedException($"{request.CriteriaKind} identity iteration is not connected to physical readers yet.");
        }
    }

    IReadOnlyList<object> IIdentityPrimitiveExecutor.ExecuteAllIdentities()
    {
        ThrowIfDisposed();
        return MaterializeIdentityObjects(OpenAllRangeReader());
    }

    IEnumerable<object> IIdentityPrimitiveExecutor.IterateIdentityUniverse()
    {
        return IterateIdentityUniverse();
    }

    /// <summary>
    /// Streams the identity universe visible to this index handle.<br/>
    /// For grouped catalog indexes, the universe is the de-duplicated union of `All` identities across indexes that declare the same identity group in catalog metadata.<br/>
    /// For ungrouped or standalone handles, the method falls back to this index's own `All` primitive because no broader identity source is currently known.<br/>
    /// </summary>
    /// <returns>A forward-only identity sequence.</returns>
    private IEnumerable<object> IterateIdentityUniverse()
    {
        ThrowIfDisposed();
        if (catalog is null || string.IsNullOrWhiteSpace(Group))
        {
            foreach (object identity in IterateIdentityPrimitive(
                new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.All, Array.Empty<object?>())))
            {
                yield return identity;
            }

            yield break;
        }

        HashSet<object> seen = new();
        CatalogIndexInfo[] indexes = catalog.Indexes[Group].List();
        for (int i = 0; i < indexes.Length; i++)
        {
            IIndex openedIndex = indexes[i].SlotIndex == SlotIndex
                ? this
                : catalog.OpenIndex(indexes[i]);
            if (openedIndex is not IIdentityPrimitiveExecutor primitiveExecutor)
            {
                throw new NotSupportedException("A grouped index does not expose the internal identity primitive executor required for identity-universe enumeration.");
            }

            using IDisposable? openedDisposable = ReferenceEquals(openedIndex, this)
                ? null
                : openedIndex as IDisposable;
            foreach (object identity in primitiveExecutor.IterateIdentityPrimitive(
                new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.All, Array.Empty<object?>())))
            {
                if (seen.Add(identity))
                {
                    yield return identity;
                }
            }
        }
    }

    private long CountIdentityPrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        ThrowIfDisposed();
        return request.CriteriaKind switch
        {
            LibraDexCriteriaKind.All => CountIdentityObjects(OpenAllRangeReader()),
            LibraDexCriteriaKind.Find => CountIdentityObjects(OpenRangeReader(
                RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)),
                RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)))),
            LibraDexCriteriaKind.Between => CountIdentityObjects(OpenRangeReader(
                RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)),
                RequireObjectKey(RequireCriterionValue(request.Values, 1), nameof(request.Values)))),
            LibraDexCriteriaKind.Before => CountIdentityObjects(OpenBeforeRangeReader(RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)))),
            LibraDexCriteriaKind.AtOrBefore => CountIdentityObjects(OpenAtOrBeforeRangeReader(RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)))),
            LibraDexCriteriaKind.After => CountIdentityObjects(OpenAfterRangeReader(RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)))),
            LibraDexCriteriaKind.AtOrAfter => CountIdentityObjects(OpenAtOrAfterRangeReader(RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)))),
            LibraDexCriteriaKind.In => CountMembershipIdentityObjects(request.Values),
            LibraDexCriteriaKind.InSet => CountMembershipIdentityObjects(request.Values),
            LibraDexCriteriaKind.Prefix => CountScannedIdentityObjects(
                LibraDexCriteriaKind.Prefix,
                RequireCriterionValue(request.Values, 0)),
            LibraDexCriteriaKind.Suffix => CountScannedIdentityObjects(
                LibraDexCriteriaKind.Suffix,
                RequireCriterionValue(request.Values, 0)),
            LibraDexCriteriaKind.Contains => CountScannedIdentityObjects(
                LibraDexCriteriaKind.Contains,
                RequireCriterionValue(request.Values, 0)),
            LibraDexCriteriaKind.Matches => CountScannedIdentityObjects(
                LibraDexCriteriaKind.Matches,
                RequireCriterionValue(request.Values, 0)),
            _ => throw new NotSupportedException($"{request.CriteriaKind} identity count is not connected to physical readers yet.")
        };
    }

    /// <summary>
    /// Re-keys one known identity when the caller also knows the old key.<br/>
    /// This is the fastest direct mutation shape because LibraDex can target the old key/identity tuple without discovering the old key through a scan or reverse identity map.<br/>
    /// </summary>
    /// <param name="identity">The identity to re-key.</param>
    /// <param name="oldKey">The current key associated with the identity.</param>
    /// <param name="newKey">The replacement key to associate with the identity.</param>
    public void Rekey(TIdentity identity, TKey oldKey, TKey newKey)
    {
        ThrowIfDisposed();
        _ = identity;
        _ = oldKey;
        _ = newKey;
        throw new NotSupportedException("Direct Rekey(identity, oldKey, newKey) is part of the public API scaffold but is not connected to physical tuple replacement yet.");
    }

    /// <summary>
    /// Re-keys one identity when the caller does not know the old key.<br/>
    /// This shape requires either a maintained reverse identity lookup or a caller-selected retrieval cursor that finds the identity and calls `SetKey`.<br/>
    /// </summary>
    /// <param name="identity">The identity to re-key.</param>
    /// <param name="newKey">The replacement key to associate with the identity.</param>
    public void Rekey(TIdentity identity, TKey newKey)
    {
        ThrowIfDisposed();
        _ = identity;
        _ = newKey;
        throw new NotSupportedException("Direct Rekey(identity, newKey) requires reverse identity lookup or a retrieval cursor and is not connected yet.");
    }

    /// <summary>
    /// Deletes one known key/identity tuple.<br/>
    /// This direct helper is for callers that already know both sides of the tuple; criteria-based deletion should normally use a retrieval cursor and call `Delete` on matching rows.<br/>
    /// </summary>
    /// <param name="key">The key side of the tuple to delete.</param>
    /// <param name="identity">The identity side of the tuple to delete.</param>
    public void Delete(TKey key, TIdentity identity)
    {
        ThrowIfDisposed();
        _ = key;
        _ = identity;
        throw new NotSupportedException("Direct Delete(key, identity) is part of the public API scaffold but is not connected to physical tuple deletion yet.");
    }

    /// <summary>
    /// Closes the index by delegating to `Dispose`.<br/>
    /// This method exists for callers who prefer explicit verb-style lifetime control over `using` while preserving the same cleanup path.<br/>
    /// </summary>
    public void Close()
    {
        Dispose();
    }

    /// <summary>
    /// Reads identities for an inclusive key range into a caller-owned destination span.<br/>
    /// The selected physical shape determines the encoded key lanes and identity lanes used for the routed range read, while the public destination receives decoded CLR identities.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower key for the range.</param>
    /// <param name="upperKey">The inclusive upper key for the range.</param>
    /// <param name="identities">The caller-owned destination span that receives decoded identities.</param>
    /// <param name="enableCoalescing">Whether the read may coalesce adjacent shelf extents when the route shape justifies it.</param>
    /// <param name="coalescedShelfScratchCount">The pooled scratch capacity, measured in profiled shelf extents, when coalescing is enabled.</param>
    /// <returns>The generic range-read result describing copied identity count and scratch/coalescing shape.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the index has already been disposed.</exception>
    /// <exception cref="NotSupportedException">Thrown when the selected physical shape does not have generic public read support in this slice.</exception>
    public LibraDexGenericRangeReadResult ReadRange(
        TKey lowerKey,
        TKey upperKey,
        Span<TIdentity> identities,
        bool enableCoalescing = false,
        int coalescedShelfScratchCount = 8)
    {
        ThrowIfDisposed();
        return shape switch
        {
            LibraDexGenericScalarShape.SS88 => ReadScalar8Scalar8Range(lowerKey, upperKey, identities, enableCoalescing, coalescedShelfScratchCount),
            LibraDexGenericScalarShape.SS168 => ReadScalar16Scalar8Range(lowerKey, upperKey, identities),
            LibraDexGenericScalarShape.SS816 => ReadScalar8Scalar16Range(lowerKey, upperKey, identities),
            LibraDexGenericScalarShape.SS1616 => ReadScalar16Scalar16Range(lowerKey, upperKey, identities),
            LibraDexGenericScalarShape.FS328 => ReadFixed32Scalar8Range(lowerKey, upperKey, identities),
            LibraDexGenericScalarShape.FS3216 => ReadFixed32Scalar16Range(lowerKey, upperKey, identities),
            _ => throw new NotSupportedException($"Generic LibraDex read does not support resolved shape {shape}.")
        };
    }

    /// <summary>
    /// Opens a forward-only generic range reader for an inclusive key range.<br/>
    /// The returned cursor follows the same physical shape selected for this index and allows callers to stream matching rows without allocating a destination array for the whole result set.<br/>
    /// Current key and identity values are decoded only when requested from the cursor, so identity-only analysis can avoid key materialization after positioning.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower key for the range.</param>
    /// <param name="upperKey">The inclusive upper key for the range.</param>
    /// <returns>A cursor positioned before the first matching key/identity row.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the index has already been disposed.</exception>
    /// <exception cref="NotSupportedException">Thrown when the selected physical shape does not have generic cursor support in this slice.</exception>
    public LibraDexRangeReader<TKey, TIdentity> OpenRangeReader(TKey lowerKey, TKey upperKey)
    {
        ThrowIfDisposed();
        object reader = shape switch
        {
            LibraDexGenericScalarShape.SS88 => OpenScalar8Scalar8RangeReader(lowerKey, upperKey),
            LibraDexGenericScalarShape.SS168 => OpenScalar16Scalar8RangeReader(lowerKey, upperKey),
            LibraDexGenericScalarShape.SS816 => OpenScalar8Scalar16RangeReader(lowerKey, upperKey),
            LibraDexGenericScalarShape.SS1616 => OpenScalar16Scalar16RangeReader(lowerKey, upperKey),
            LibraDexGenericScalarShape.FS328 => OpenFixed32Scalar8RangeReader(lowerKey, upperKey),
            LibraDexGenericScalarShape.FS3216 => OpenFixed32Scalar16RangeReader(lowerKey, upperKey),
            _ => throw new NotSupportedException($"Generic LibraDex range readers do not support resolved shape {shape}.")
        };

        return new LibraDexRangeReader<TKey, TIdentity>(reader, shape);
    }

    private LibraDexRangeReader<TKey, TIdentity> OpenAllRangeReader()
    {
        GetFullKeyBounds(out TKey lowerKey, out TKey upperKey);
        return OpenRangeReader(lowerKey, upperKey);
    }

    private LibraDexRangeReader<TKey, TIdentity> OpenBeforeRangeReader(TKey key)
    {
        GetFullKeyBounds(out TKey lowerKey, out _);
        if (!TryGetPreviousKey(key, out TKey upperKey))
        {
            LibraDexRangeReader<TKey, TIdentity> empty = OpenAllRangeReader();
            empty.ApplyTakeLimit(0);
            return empty;
        }

        return OpenRangeReader(lowerKey, upperKey);
    }

    private LibraDexRangeReader<TKey, TIdentity> OpenAtOrBeforeRangeReader(TKey key)
    {
        GetFullKeyBounds(out TKey lowerKey, out _);
        return OpenRangeReader(lowerKey, key);
    }

    private LibraDexRangeReader<TKey, TIdentity> OpenAfterRangeReader(TKey key)
    {
        GetFullKeyBounds(out _, out TKey upperKey);
        if (!TryGetNextKey(key, out TKey lowerKey))
        {
            LibraDexRangeReader<TKey, TIdentity> empty = OpenAllRangeReader();
            empty.ApplyTakeLimit(0);
            return empty;
        }

        return OpenRangeReader(lowerKey, upperKey);
    }

    private LibraDexRangeReader<TKey, TIdentity> OpenAtOrAfterRangeReader(TKey key)
    {
        GetFullKeyBounds(out _, out TKey upperKey);
        return OpenRangeReader(key, upperKey);
    }

    private void GetFullKeyBounds(out TKey lowerKey, out TKey upperKey)
    {
        switch (shape)
        {
            case LibraDexGenericScalarShape.SS88:
            case LibraDexGenericScalarShape.SS816:
                lowerKey = LibraDexGenericScalarCodec<TKey>.Decode8(0);
                upperKey = LibraDexGenericScalarCodec<TKey>.Decode8(ulong.MaxValue);
                return;
            case LibraDexGenericScalarShape.SS168:
            case LibraDexGenericScalarShape.SS1616:
                lowerKey = LibraDexGenericScalarCodec<TKey>.Decode16(0, 0);
                upperKey = LibraDexGenericScalarCodec<TKey>.Decode16(ulong.MaxValue, ulong.MaxValue);
                return;
            case LibraDexGenericScalarShape.FS328:
            case LibraDexGenericScalarShape.FS3216:
                lowerKey = CreateFixed32Key(0, 0, 0, 0);
                upperKey = CreateFixed32Key(ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue);
                return;
            default:
                throw new NotSupportedException($"Generic LibraDex criteria readers do not support resolved shape {shape}.");
        }
    }

    private bool TryGetPreviousKey(TKey key, out TKey previousKey)
    {
        switch (shape)
        {
            case LibraDexGenericScalarShape.SS88:
            case LibraDexGenericScalarShape.SS816:
            {
                ulong encoded = LibraDexGenericScalarCodec<TKey>.Encode8(key);
                if (encoded == 0)
                {
                    previousKey = default!;
                    return false;
                }

                previousKey = LibraDexGenericScalarCodec<TKey>.Decode8(encoded - 1);
                return true;
            }
            case LibraDexGenericScalarShape.SS168:
            case LibraDexGenericScalarShape.SS1616:
            {
                LibraDexGenericScalarCodec<TKey>.Encode16(key, out ulong high, out ulong low);
                if (high == 0 && low == 0)
                {
                    previousKey = default!;
                    return false;
                }

                if (low == 0)
                {
                    high--;
                    low = ulong.MaxValue;
                }
                else
                {
                    low--;
                }

                previousKey = LibraDexGenericScalarCodec<TKey>.Decode16(high, low);
                return true;
            }
            case LibraDexGenericScalarShape.FS328:
            case LibraDexGenericScalarShape.FS3216:
                return TryAdjustFixed32Key(key, decrement: true, out previousKey);
            default:
                throw new NotSupportedException($"Generic LibraDex criteria readers do not support resolved shape {shape}.");
        }
    }

    private bool TryGetNextKey(TKey key, out TKey nextKey)
    {
        switch (shape)
        {
            case LibraDexGenericScalarShape.SS88:
            case LibraDexGenericScalarShape.SS816:
            {
                ulong encoded = LibraDexGenericScalarCodec<TKey>.Encode8(key);
                if (encoded == ulong.MaxValue)
                {
                    nextKey = default!;
                    return false;
                }

                nextKey = LibraDexGenericScalarCodec<TKey>.Decode8(encoded + 1);
                return true;
            }
            case LibraDexGenericScalarShape.SS168:
            case LibraDexGenericScalarShape.SS1616:
            {
                LibraDexGenericScalarCodec<TKey>.Encode16(key, out ulong high, out ulong low);
                if (high == ulong.MaxValue && low == ulong.MaxValue)
                {
                    nextKey = default!;
                    return false;
                }

                if (low == ulong.MaxValue)
                {
                    high++;
                    low = 0;
                }
                else
                {
                    low++;
                }

                nextKey = LibraDexGenericScalarCodec<TKey>.Decode16(high, low);
                return true;
            }
            case LibraDexGenericScalarShape.FS328:
            case LibraDexGenericScalarShape.FS3216:
                return TryAdjustFixed32Key(key, decrement: false, out nextKey);
            default:
                throw new NotSupportedException($"Generic LibraDex criteria readers do not support resolved shape {shape}.");
        }
    }

    private static TKey CreateFixed32Key(ulong part0, ulong part1, ulong part2, ulong part3)
    {
        byte[] bytes = new byte[32];
        WriteBigEndianUInt64(bytes.AsSpan(0, 8), part0);
        WriteBigEndianUInt64(bytes.AsSpan(8, 8), part1);
        WriteBigEndianUInt64(bytes.AsSpan(16, 8), part2);
        WriteBigEndianUInt64(bytes.AsSpan(24, 8), part3);
        return (TKey)(object)bytes;
    }

    private static bool TryAdjustFixed32Key(TKey key, bool decrement, out TKey adjustedKey)
    {
        if (key is not byte[] source || source.Length != 32)
        {
            throw new ArgumentException("Generic LibraDex fixed 32-byte criteria require byte[] keys that are exactly 32 bytes.", nameof(key));
        }

        byte[] adjusted = new byte[32];
        source.CopyTo(adjusted, 0);
        if (decrement)
        {
            for (int i = adjusted.Length - 1; i >= 0; i--)
            {
                if (adjusted[i] != 0)
                {
                    adjusted[i]--;
                    adjustedKey = (TKey)(object)adjusted;
                    return true;
                }

                adjusted[i] = byte.MaxValue;
            }
        }
        else
        {
            for (int i = adjusted.Length - 1; i >= 0; i--)
            {
                if (adjusted[i] != byte.MaxValue)
                {
                    adjusted[i]++;
                    adjustedKey = (TKey)(object)adjusted;
                    return true;
                }

                adjusted[i] = 0;
            }
        }

        adjustedKey = default!;
        return false;
    }

    private static void WriteBigEndianUInt64(Span<byte> destination, ulong value)
    {
        for (int i = 7; i >= 0; i--)
        {
            destination[i] = (byte)value;
            value >>= 8;
        }
    }

    /// <summary>
    /// Disposes the owning session when this generic index was created by a facade factory.<br/>
    /// Disposing a file-backed index releases the file handle; disposing a memory-backed index releases committed pooled memory segments.<br/>
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        if (BatchManager.IsEnabled)
        {
            _ = BatchManager.CommitAndDisable();
        }

        if (ownsSession)
        {
            session.Dispose();
        }

        disposed = true;
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(LibraDexIndex<TKey, TIdentity>));
        }
    }

    private LibraDexCriteriaQuery<TKey, TIdentity> CreateCriteriaQuery(
        LibraDexCriteriaKind criteriaKind,
        QueryDirection direction,
        int skip,
        int? take,
        RetrievalScope scope,
        LibraDexBookmark? bookmark,
        LibraDexExecutionKind executionKind,
        TKey key = default!,
        bool hasKey = false,
        object? operand = null,
        bool hasOperand = false)
    {
        ThrowIfDisposed();
        if (skip < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(skip), skip, "Skip cannot be negative.");
        }

        if (take is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(take), take, "Take cannot be negative.");
        }

        return new LibraDexCriteriaQuery<TKey, TIdentity>(
            this,
            criteriaKind,
            direction,
            skip,
            take,
            scope,
            bookmark,
            executionKind,
            key: key,
            hasKey: hasKey,
            operand: operand,
            hasOperand: hasOperand);
    }

    private static LibraDexExecutionKind DirectionExecutionKind(QueryDirection direction)
    {
        return direction == QueryDirection.Descending
            ? LibraDexExecutionKind.ReverseTraversal
            : LibraDexExecutionKind.FastPath;
    }

    private static TKey RequireObjectKey(object value, string parameterName)
    {
        if (value is TKey key)
        {
            return key;
        }

        string actualType = value?.GetType().FullName ?? "<null>";
        throw new ArgumentException($"Expected a key value assignable to {typeof(TKey).FullName}; received {actualType}.", parameterName);
    }

    private static TIdentity RequireObjectIdentity(object value, string parameterName)
    {
        if (value is TIdentity identity)
        {
            return identity;
        }

        string actualType = value?.GetType().FullName ?? "<null>";
        throw new ArgumentException($"Expected an identity value assignable to {typeof(TIdentity).FullName}; received {actualType}.", parameterName);
    }

    private static object RequireCriterionValue(IReadOnlyList<object?> values, int index)
    {
        if (index >= values.Count || values[index] is null)
        {
            throw new InvalidOperationException("The identity criterion is missing a required runtime value.");
        }

        return values[index]!;
    }

    private IReadOnlyList<object> MaterializeIdentityObjects(LibraDexRangeReader<TKey, TIdentity> reader, int? takeLimit = null)
    {
        return IterateIdentityObjects(reader, takeLimit).ToList();
    }

    /// <summary>
    /// Streams decoded identities from a concrete generic range reader and disposes the reader when enumeration completes or stops early.<br/>
    /// The limit is applied while reading so callers such as `Exists` can avoid copying more identities than they need.<br/>
    /// This helper is the shared low-level identity-only path for materialized and non-materialized primitive execution.<br/>
    /// </summary>
    /// <param name="reader">The range reader to consume.</param>
    /// <param name="takeLimit">The optional maximum number of identities to yield.</param>
    /// <returns>A forward-only identity sequence.</returns>
    private static IEnumerable<object> IterateIdentityObjects(LibraDexRangeReader<TKey, TIdentity> reader, int? takeLimit = null)
    {
        if (takeLimit is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(takeLimit), takeLimit, "Take cannot be negative.");
        }

        if (takeLimit == 0)
        {
            reader.Dispose();
            yield break;
        }

        using (reader)
        {
            int returned = 0;
            while (reader.TryReadNextIdentity(out TIdentity identity))
            {
                yield return identity!;
                returned++;
                if (takeLimit is not null && returned >= takeLimit.Value)
                {
                    yield break;
                }
            }
        }
    }

    private static long CountIdentityObjects(LibraDexRangeReader<TKey, TIdentity> reader)
    {
        using (reader)
        {
            return reader.Count;
        }
    }

    private IReadOnlyList<object> MaterializeScannedIdentityObjects(
        LibraDexCriteriaKind criteriaKind,
        object operand,
        int? takeLimit = null)
    {
        return IterateScannedIdentityObjects(criteriaKind, operand, takeLimit).ToList();
    }

    /// <summary>
    /// Streams identities for scan-backed key predicates by reading key/identity tuples from the full key range.<br/>
    /// This is the conservative physical bridge for criteria such as `Prefix`, `Suffix`, `Contains`, and `Matches` when no maintained projection is available yet.<br/>
    /// The predicate is evaluated against decoded public key values, so callers get correct retrieval semantics before projection-specific fast paths are added.<br/>
    /// </summary>
    /// <param name="criteriaKind">The scan-backed criteria kind to evaluate.</param>
    /// <param name="operand">The materialized predicate operand.</param>
    /// <param name="takeLimit">The optional maximum number of identities to yield.</param>
    /// <returns>A forward-only identity sequence.</returns>
    private IEnumerable<object> IterateScannedIdentityObjects(
        LibraDexCriteriaKind criteriaKind,
        object operand,
        int? takeLimit = null)
    {
        if (takeLimit is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(takeLimit), takeLimit, "Take cannot be negative.");
        }

        if (takeLimit == 0)
        {
            yield break;
        }

        using LibraDexRangeReader<TKey, TIdentity> reader = OpenAllRangeReader();
        int returned = 0;
        while (reader.TryReadNext(out TKey key, out TIdentity identity))
        {
            if (!MatchesScannedKey(criteriaKind, key, operand))
            {
                continue;
            }

            yield return identity!;
            returned++;
            if (takeLimit is not null && returned >= takeLimit.Value)
            {
                yield break;
            }
        }
    }

    /// <summary>
    /// Streams tuple rows for a scan-backed key predicate by reading the full key range and applying the predicate to decoded keys.<br/>
    /// Skip and take are applied after predicate matching so they behave like query-result paging rather than physical-row paging.<br/>
    /// This is intentionally conservative: projection-specific fast paths can replace this when logical shape metadata proves a better maintained projection exists.<br/>
    /// </summary>
    /// <param name="criteriaKind">The scan-backed criteria kind to evaluate.</param>
    /// <param name="operand">The materialized predicate operand.</param>
    /// <param name="skip">The number of matching tuples to skip.</param>
    /// <param name="take">The optional maximum number of tuples to return.</param>
    /// <returns>A forward-only tuple sequence.</returns>
    private IEnumerable<LibraDexTuple<TKey, TIdentity>> IterateScannedTuples(
        LibraDexCriteriaKind criteriaKind,
        object operand,
        int skip,
        int? take)
    {
        if (skip < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(skip), skip, "Skip cannot be negative.");
        }

        if (take is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(take), take, "Take cannot be negative.");
        }

        if (take == 0)
        {
            yield break;
        }

        using LibraDexRangeReader<TKey, TIdentity> reader = OpenAllRangeReader();
        int returned = 0;
        while (reader.TryReadNext(out TKey key, out TIdentity identity))
        {
            if (!MatchesScannedKey(criteriaKind, key, operand))
            {
                continue;
            }

            if (skip > 0)
            {
                skip--;
                continue;
            }

            yield return new LibraDexTuple<TKey, TIdentity>(key, identity);
            returned++;
            if (take is not null && returned >= take.Value)
            {
                yield break;
            }
        }
    }

    private long CountScannedIdentityObjects(LibraDexCriteriaKind criteriaKind, object operand)
    {
        long count = 0;
        foreach (object _ in IterateScannedIdentityObjects(criteriaKind, operand))
        {
            count++;
        }

        return count;
    }

    private static bool MatchesScannedKey(LibraDexCriteriaKind criteriaKind, TKey key, object operand)
    {
        return criteriaKind switch
        {
            LibraDexCriteriaKind.Prefix => KeyStartsWith(key, operand),
            LibraDexCriteriaKind.Suffix => KeyEndsWith(key, operand),
            LibraDexCriteriaKind.Contains => KeyContains(key, operand),
            LibraDexCriteriaKind.Matches => KeyMatchesPattern(key, operand),
            _ => throw new NotSupportedException($"{criteriaKind} is not a scan-backed key predicate.")
        };
    }

    private static bool KeyStartsWith(TKey key, object operand)
    {
        if (key is string keyText && operand is string prefixText)
        {
            return keyText.StartsWith(prefixText, StringComparison.Ordinal);
        }

        if (key is byte[] keyBytes && operand is byte[] prefixBytes)
        {
            return keyBytes.AsSpan().StartsWith(prefixBytes);
        }

        throw CreateUnsupportedKeyPredicateException("Prefix", key, operand);
    }

    private static bool KeyEndsWith(TKey key, object operand)
    {
        if (key is string keyText && operand is string suffixText)
        {
            return keyText.EndsWith(suffixText, StringComparison.Ordinal);
        }

        if (key is byte[] keyBytes && operand is byte[] suffixBytes)
        {
            return keyBytes.AsSpan().EndsWith(suffixBytes);
        }

        throw CreateUnsupportedKeyPredicateException("Suffix", key, operand);
    }

    private static bool KeyContains(TKey key, object operand)
    {
        if (key is string keyText && operand is string valueText)
        {
            return keyText.Contains(valueText, StringComparison.Ordinal);
        }

        if (key is byte[] keyBytes && operand is byte[] valueBytes)
        {
            return keyBytes.AsSpan().IndexOf(valueBytes) >= 0;
        }

        throw CreateUnsupportedKeyPredicateException("Contains", key, operand);
    }

    private static bool KeyMatchesPattern(TKey key, object operand)
    {
        if (operand is Predicate<TKey> predicate)
        {
            return predicate(key);
        }

        if (operand is Func<TKey, bool> typedFunction)
        {
            return typedFunction(key);
        }

        if (operand is Func<object, bool> objectFunction)
        {
            return objectFunction(key!);
        }

        if (key is string keyText)
        {
            if (operand is System.Text.RegularExpressions.Regex regex)
            {
                return regex.IsMatch(keyText);
            }

            if (operand is string patternText)
            {
                return System.Text.RegularExpressions.Regex.IsMatch(keyText, patternText);
            }
        }

        throw CreateUnsupportedKeyPredicateException("Matches", key, operand);
    }

    private static NotSupportedException CreateUnsupportedKeyPredicateException(string operation, TKey key, object operand)
    {
        string keyType = key?.GetType().FullName ?? typeof(TKey).FullName ?? "<unknown>";
        string operandType = operand.GetType().FullName ?? "<unknown>";
        return new NotSupportedException($"{operation} scan execution does not support key type {keyType} with operand type {operandType}.");
    }

    private IReadOnlyList<object> MaterializeMembershipIdentityObjects(IReadOnlyList<object?> values, int? takeLimit = null)
    {
        if (values.Count > 1)
        {
            return MaterializeMembershipIdentityObjects(values.Select(RequireNonNullMembershipValue), takeLimit);
        }

        object value = RequireCriterionValue(values, 0);
        IEnumerable<object> keys = value switch
        {
            LibraDexPreparedObjectSet prepared => prepared.Values,
            IEnumerable<object> objectValues => objectValues,
            System.Collections.IEnumerable enumerable => enumerable.Cast<object>(),
            _ => throw new InvalidOperationException("Membership identity execution requires an enumerable key value or prepared set.")
        };

        return MaterializeMembershipIdentityObjects(keys, takeLimit);
    }

    private IReadOnlyList<object> MaterializeMembershipIdentityObjects(IEnumerable<object> keys, int? takeLimit = null)
    {
        return IterateMembershipIdentityObjects(keys, takeLimit).ToList();
    }

    /// <summary>
    /// Streams identities for a membership primitive by opening one equality range reader for each supplied key.<br/>
    /// The method preserves the caller's key order and applies one shared take limit across all matching key runs.<br/>
    /// It intentionally leaves duplicate identities intact because duplicate handling belongs to the projection options above this primitive layer.<br/>
    /// </summary>
    /// <param name="values">The condition operand values, either inline keys, an enumerable of keys, or a prepared set.</param>
    /// <param name="takeLimit">The optional maximum number of identities to yield across all keys.</param>
    /// <returns>A forward-only identity sequence.</returns>
    private IEnumerable<object> IterateMembershipIdentityObjects(IReadOnlyList<object?> values, int? takeLimit = null)
    {
        if (values.Count > 1)
        {
            return IterateMembershipIdentityObjects(values.Select(RequireNonNullMembershipValue), takeLimit);
        }

        object value = RequireCriterionValue(values, 0);
        IEnumerable<object> keys = value switch
        {
            LibraDexPreparedObjectSet prepared => prepared.Values,
            IEnumerable<object> objectValues => objectValues,
            System.Collections.IEnumerable enumerable => enumerable.Cast<object>(),
            _ => throw new InvalidOperationException("Membership identity iteration requires an enumerable key value or prepared set.")
        };

        return IterateMembershipIdentityObjects(keys, takeLimit);
    }

    /// <summary>
    /// Streams identities for already-normalized membership keys through one equality reader per key.<br/>
    /// The supplied key enumerable is consumed lazily so prepared or generated sets do not need to become an intermediate array at this layer.<br/>
    /// The limit is cumulative across the whole membership request, matching the materialized primitive behavior.<br/>
    /// </summary>
    /// <param name="keys">The membership keys to read.</param>
    /// <param name="takeLimit">The optional maximum number of identities to yield across all keys.</param>
    /// <returns>A forward-only identity sequence.</returns>
    private IEnumerable<object> IterateMembershipIdentityObjects(IEnumerable<object> keys, int? takeLimit = null)
    {
        if (takeLimit is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(takeLimit), takeLimit, "Take cannot be negative.");
        }

        if (takeLimit == 0)
        {
            yield break;
        }

        int returned = 0;
        foreach (object keyValue in keys)
        {
            using LibraDexRangeReader<TKey, TIdentity> reader = OpenRangeReader(
                RequireObjectKey(keyValue, nameof(keys)),
                RequireObjectKey(keyValue, nameof(keys)));
            while (reader.TryReadNextIdentity(out TIdentity identity))
            {
                yield return identity!;
                returned++;
                if (takeLimit is not null && returned >= takeLimit.Value)
                {
                    yield break;
                }
            }
        }
    }

    /// <summary>
    /// Streams tuple rows for direct membership criteria by opening one equality reader per captured key.<br/>
    /// Key order is preserved, skip/take are applied across the combined matching stream, and duplicate tuples remain intact at this primitive layer.<br/>
    /// De-duplication remains a projection/criteria option above the physical membership fan-out.<br/>
    /// </summary>
    /// <param name="operand">The captured membership operand.</param>
    /// <param name="skip">The number of matching tuples to skip.</param>
    /// <param name="take">The optional maximum number of tuples to return.</param>
    /// <returns>A forward-only tuple sequence.</returns>
    private IEnumerable<LibraDexTuple<TKey, TIdentity>> IterateMembershipTuples(object operand, int skip, int? take)
    {
        if (skip < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(skip), skip, "Skip cannot be negative.");
        }

        if (take is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(take), take, "Take cannot be negative.");
        }

        if (take == 0)
        {
            yield break;
        }

        int returned = 0;
        foreach (TKey keyValue in NormalizeMembershipKeys(operand))
        {
            using LibraDexRangeReader<TKey, TIdentity> reader = OpenRangeReader(keyValue, keyValue);
            while (reader.TryReadNext(out TKey key, out TIdentity identity))
            {
                if (skip > 0)
                {
                    skip--;
                    continue;
                }

                yield return new LibraDexTuple<TKey, TIdentity>(key, identity);
                returned++;
                if (take is not null && returned >= take.Value)
                {
                    yield break;
                }
            }
        }
    }

    private static IEnumerable<TKey> NormalizeMembershipKeys(object operand)
    {
        return operand switch
        {
            LibraDexPreparedSet<TKey> prepared => prepared.Values,
            IEnumerable<TKey> typedKeys => typedKeys,
            _ => throw new InvalidOperationException("Direct membership criteria require an enumerable key value or prepared set.")
        };
    }

    private static object RequireCriteriaOperand(object? operand, bool hasOperand)
    {
        return hasOperand
            ? operand ?? throw new InvalidOperationException("The direct criteria descriptor captured a null operand.")
            : throw new InvalidOperationException("The direct criteria descriptor is missing a required operand.");
    }

    private long CountMembershipIdentityObjects(IReadOnlyList<object?> values)
    {
        if (values.Count > 1)
        {
            return CountMembershipIdentityObjects(values.Select(RequireNonNullMembershipValue));
        }

        object value = RequireCriterionValue(values, 0);
        IEnumerable<object> keys = value switch
        {
            LibraDexPreparedObjectSet prepared => prepared.Values,
            IEnumerable<object> objectValues => objectValues,
            System.Collections.IEnumerable enumerable => enumerable.Cast<object>(),
            _ => throw new InvalidOperationException("Membership identity count requires an enumerable key value or prepared set.")
        };

        return CountMembershipIdentityObjects(keys);
    }

    private long CountMembershipIdentityObjects(IEnumerable<object> keys)
    {
        long count = 0;
        foreach (object keyValue in keys)
        {
            using LibraDexRangeReader<TKey, TIdentity> reader = OpenRangeReader(
                RequireObjectKey(keyValue, nameof(keys)),
                RequireObjectKey(keyValue, nameof(keys)));
            count += reader.Count;
        }

        return count;
    }

    private static object RequireNonNullMembershipValue(object? value)
    {
        return value ?? throw new InvalidOperationException("Membership identity execution cannot use a null key value.");
    }

    private static TKey[] RequirePreparedObjectSet(LibraDexPreparedObjectSet set, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (set.KeyType != typeof(TKey))
        {
            throw new ArgumentException($"Expected a prepared set for key type {typeof(TKey).FullName}; received {set.KeyType.FullName}.", parameterName);
        }

        TKey[] typedKeys = new TKey[set.Values.Count];
        for (int i = 0; i < set.Values.Count; i++)
        {
            typedKeys[i] = RequireObjectKey(set.Values[i], parameterName);
        }

        return typedKeys;
    }

    private LibraDexGenericRangeReadResult ReadScalar8Scalar8Range(
        TKey lowerKey,
        TKey upperKey,
        Span<TIdentity> identities,
        bool enableCoalescing,
        int coalescedShelfScratchCount)
    {
        ulong lowerEncodedKey = LibraDexGenericScalarCodec<TKey>.Encode8(lowerKey);
        ulong upperEncodedKey = LibraDexGenericScalarCodec<TKey>.Encode8(upperKey);
        ulong[] encodedIdentities = ArrayPool<ulong>.Shared.Rent(identities.Length);
        try
        {
            Scalar8Scalar8RangeReadOptions options = enableCoalescing
                ? Scalar8Scalar8RangeReadOptions.CoalescingCached with { CoalescedShelfScratchCount = coalescedShelfScratchCount }
                : Scalar8Scalar8RangeReadOptions.ConservativeCached;
            Scalar8Scalar8RangeReadResult result = session.ReadEncodedScalar8Scalar8IdentityRangePooled(
                new Scalar8Scalar8IndexHandle(RootRouterOffset, Scalar8Scalar8Profile.Default32KiB),
                lowerEncodedKey,
                upperEncodedKey,
                options,
                encodedIdentities.AsSpan(0, identities.Length));

            for (int i = 0; i < result.IdentityCount; i++)
            {
                identities[i] = LibraDexGenericScalarCodec<TIdentity>.Decode8(encodedIdentities[i]);
            }

            return new LibraDexGenericRangeReadResult(
                result.IdentityCount,
                result.UsedPooledScratch,
                UsedEncodedIdentityScratch: true,
                result.CoalescingEnabled,
                result.RangeScratchShelfCapacity);
        }
        finally
        {
            ArrayPool<ulong>.Shared.Return(encodedIdentities);
        }
    }

    private Scalar8Scalar8RangeReader OpenScalar8Scalar8RangeReader(TKey lowerKey, TKey upperKey)
    {
        ulong lowerEncodedKey = LibraDexGenericScalarCodec<TKey>.Encode8(lowerKey);
        ulong upperEncodedKey = LibraDexGenericScalarCodec<TKey>.Encode8(upperKey);
        return session.OpenScalar8Scalar8RangeReader(RootRouterOffset, Scalar8Scalar8Profile.Default32KiB, lowerEncodedKey, upperEncodedKey);
    }

    private LibraDexGenericRangeReadResult ReadScalar16Scalar8Range(
        TKey lowerKey,
        TKey upperKey,
        Span<TIdentity> identities)
    {
        LibraDexGenericScalarCodec<TKey>.Encode16(lowerKey, out ulong lowerHigh, out ulong lowerLow);
        LibraDexGenericScalarCodec<TKey>.Encode16(upperKey, out ulong upperHigh, out ulong upperLow);
        ulong[] encodedIdentities = ArrayPool<ulong>.Shared.Rent(identities.Length);
        byte[] shelfScratch = ArrayPool<byte>.Shared.Rent(Scalar16Scalar8Profile.Default32KiB.ShelfExtentSize);
        using RouteTargetKindCache targetKindCache = RouteTargetKindCache.Rent();
        try
        {
            int count = session.ReadScalar16Scalar8IdentityRange(
                RootRouterOffset,
                Scalar16Scalar8Profile.Default32KiB,
                lowerHigh,
                lowerLow,
                upperHigh,
                upperLow,
                maxRouterHops: 8,
                encodedIdentities.AsSpan(0, identities.Length),
                shelfScratch.AsSpan(0, Scalar16Scalar8Profile.Default32KiB.ShelfExtentSize),
                targetKindCache);

            for (int i = 0; i < count; i++)
            {
                identities[i] = LibraDexGenericScalarCodec<TIdentity>.Decode8(encodedIdentities[i]);
            }

            return new LibraDexGenericRangeReadResult(count, UsedPooledScratch: true, UsedEncodedIdentityScratch: true, CoalescingEnabled: false, RangeScratchShelfCapacity: 1);
        }
        finally
        {
            ArrayPool<ulong>.Shared.Return(encodedIdentities);
            ArrayPool<byte>.Shared.Return(shelfScratch);
        }
    }

    private Scalar16Scalar8RangeReader OpenScalar16Scalar8RangeReader(TKey lowerKey, TKey upperKey)
    {
        LibraDexGenericScalarCodec<TKey>.Encode16(lowerKey, out ulong lowerHigh, out ulong lowerLow);
        LibraDexGenericScalarCodec<TKey>.Encode16(upperKey, out ulong upperHigh, out ulong upperLow);
        return session.OpenScalar16Scalar8RangeReader(RootRouterOffset, Scalar16Scalar8Profile.Default32KiB, lowerHigh, lowerLow, upperHigh, upperLow);
    }

    private LibraDexGenericRangeReadResult ReadScalar8Scalar16Range(
        TKey lowerKey,
        TKey upperKey,
        Span<TIdentity> identities)
    {
        ulong lowerEncodedKey = LibraDexGenericScalarCodec<TKey>.Encode8(lowerKey);
        ulong upperEncodedKey = LibraDexGenericScalarCodec<TKey>.Encode8(upperKey);
        ulong[] identityHighs = ArrayPool<ulong>.Shared.Rent(identities.Length);
        ulong[] identityLows = ArrayPool<ulong>.Shared.Rent(identities.Length);
        byte[] shelfScratch = ArrayPool<byte>.Shared.Rent(Scalar8Scalar16Profile.Default32KiB.ShelfExtentSize);
        using RouteTargetKindCache targetKindCache = RouteTargetKindCache.Rent();
        try
        {
            int count = session.ReadScalar8Scalar16IdentityRange(
                RootRouterOffset,
                Scalar8Scalar16Profile.Default32KiB,
                lowerEncodedKey,
                upperEncodedKey,
                maxRouterHops: 8,
                identityHighs.AsSpan(0, identities.Length),
                identityLows.AsSpan(0, identities.Length),
                shelfScratch.AsSpan(0, Scalar8Scalar16Profile.Default32KiB.ShelfExtentSize),
                targetKindCache);

            for (int i = 0; i < count; i++)
            {
                identities[i] = LibraDexGenericScalarCodec<TIdentity>.Decode16(identityHighs[i], identityLows[i]);
            }

            return new LibraDexGenericRangeReadResult(count, UsedPooledScratch: true, UsedEncodedIdentityScratch: true, CoalescingEnabled: false, RangeScratchShelfCapacity: 1);
        }
        finally
        {
            ArrayPool<ulong>.Shared.Return(identityHighs);
            ArrayPool<ulong>.Shared.Return(identityLows);
            ArrayPool<byte>.Shared.Return(shelfScratch);
        }
    }

    private Scalar8Scalar16RangeReader OpenScalar8Scalar16RangeReader(TKey lowerKey, TKey upperKey)
    {
        ulong lowerEncodedKey = LibraDexGenericScalarCodec<TKey>.Encode8(lowerKey);
        ulong upperEncodedKey = LibraDexGenericScalarCodec<TKey>.Encode8(upperKey);
        return session.OpenScalar8Scalar16RangeReader(RootRouterOffset, Scalar8Scalar16Profile.Default32KiB, lowerEncodedKey, upperEncodedKey);
    }

    private LibraDexGenericRangeReadResult ReadScalar16Scalar16Range(
        TKey lowerKey,
        TKey upperKey,
        Span<TIdentity> identities)
    {
        LibraDexGenericScalarCodec<TKey>.Encode16(lowerKey, out ulong lowerHigh, out ulong lowerLow);
        LibraDexGenericScalarCodec<TKey>.Encode16(upperKey, out ulong upperHigh, out ulong upperLow);
        ulong[] identityHighs = ArrayPool<ulong>.Shared.Rent(identities.Length);
        ulong[] identityLows = ArrayPool<ulong>.Shared.Rent(identities.Length);
        byte[] shelfScratch = ArrayPool<byte>.Shared.Rent(Scalar16Scalar16Profile.Default32KiB.ShelfExtentSize);
        using RouteTargetKindCache targetKindCache = RouteTargetKindCache.Rent();
        try
        {
            int count = session.ReadScalar16Scalar16IdentityRange(
                RootRouterOffset,
                Scalar16Scalar16Profile.Default32KiB,
                lowerHigh,
                lowerLow,
                upperHigh,
                upperLow,
                maxRouterHops: 8,
                identityHighs.AsSpan(0, identities.Length),
                identityLows.AsSpan(0, identities.Length),
                shelfScratch.AsSpan(0, Scalar16Scalar16Profile.Default32KiB.ShelfExtentSize),
                targetKindCache);

            for (int i = 0; i < count; i++)
            {
                identities[i] = LibraDexGenericScalarCodec<TIdentity>.Decode16(identityHighs[i], identityLows[i]);
            }

            return new LibraDexGenericRangeReadResult(count, UsedPooledScratch: true, UsedEncodedIdentityScratch: true, CoalescingEnabled: false, RangeScratchShelfCapacity: 1);
        }
        finally
        {
            ArrayPool<ulong>.Shared.Return(identityHighs);
            ArrayPool<ulong>.Shared.Return(identityLows);
            ArrayPool<byte>.Shared.Return(shelfScratch);
        }
    }

    private Scalar16Scalar16RangeReader OpenScalar16Scalar16RangeReader(TKey lowerKey, TKey upperKey)
    {
        LibraDexGenericScalarCodec<TKey>.Encode16(lowerKey, out ulong lowerHigh, out ulong lowerLow);
        LibraDexGenericScalarCodec<TKey>.Encode16(upperKey, out ulong upperHigh, out ulong upperLow);
        return session.OpenScalar16Scalar16RangeReader(RootRouterOffset, Scalar16Scalar16Profile.Default32KiB, lowerHigh, lowerLow, upperHigh, upperLow);
    }

    private LibraDexGenericRangeReadResult ReadFixed32Scalar8Range(
        TKey lowerKey,
        TKey upperKey,
        Span<TIdentity> identities)
    {
        LibraDexGenericScalarCodec<TKey>.Encode32(lowerKey, out ulong lower0, out ulong lower1, out ulong lower2, out ulong lower3);
        LibraDexGenericScalarCodec<TKey>.Encode32(upperKey, out ulong upper0, out ulong upper1, out ulong upper2, out ulong upper3);
        ulong[] encodedIdentities = ArrayPool<ulong>.Shared.Rent(identities.Length);
        byte[] shelfScratch = ArrayPool<byte>.Shared.Rent(Fixed32Scalar8Profile.Default64KiB.ShelfExtentSize);
        using RouteTargetKindCache targetKindCache = RouteTargetKindCache.Rent();
        try
        {
            int count = session.ReadFixed32Scalar8IdentityRange(
                RootRouterOffset,
                Fixed32Scalar8Profile.Default64KiB,
                lower0,
                lower1,
                lower2,
                lower3,
                upper0,
                upper1,
                upper2,
                upper3,
                maxRouterHops: 32,
                encodedIdentities.AsSpan(0, identities.Length),
                shelfScratch.AsSpan(0, Fixed32Scalar8Profile.Default64KiB.ShelfExtentSize),
                targetKindCache);

            for (int i = 0; i < count; i++)
            {
                identities[i] = LibraDexGenericScalarCodec<TIdentity>.Decode8(encodedIdentities[i]);
            }

            return new LibraDexGenericRangeReadResult(count, UsedPooledScratch: true, UsedEncodedIdentityScratch: true, CoalescingEnabled: false, RangeScratchShelfCapacity: 1);
        }
        finally
        {
            ArrayPool<ulong>.Shared.Return(encodedIdentities);
            ArrayPool<byte>.Shared.Return(shelfScratch);
        }
    }

    private Fixed32Scalar8RangeReader OpenFixed32Scalar8RangeReader(TKey lowerKey, TKey upperKey)
    {
        LibraDexGenericScalarCodec<TKey>.Encode32(lowerKey, out ulong lower0, out ulong lower1, out ulong lower2, out ulong lower3);
        LibraDexGenericScalarCodec<TKey>.Encode32(upperKey, out ulong upper0, out ulong upper1, out ulong upper2, out ulong upper3);
        return session.OpenFixed32Scalar8RangeReader(RootRouterOffset, Fixed32Scalar8Profile.Default64KiB, lower0, lower1, lower2, lower3, upper0, upper1, upper2, upper3);
    }

    /// <summary>
    /// Reads a generic inclusive key range from the routed `FS32-16` physical shape.<br/>
    /// The public lower and upper keys are encoded as four fixed-key lanes, matching the persisted shelf order.<br/>
    /// Matching 16-byte identities are copied into pooled high/low scratch lanes and decoded into the caller-owned destination span.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower key for the range.</param>
    /// <param name="upperKey">The inclusive upper key for the range.</param>
    /// <param name="identities">The caller-owned destination span that receives decoded identities.</param>
    /// <returns>The generic range-read result describing copied identity count and scratch usage.</returns>
    private LibraDexGenericRangeReadResult ReadFixed32Scalar16Range(
        TKey lowerKey,
        TKey upperKey,
        Span<TIdentity> identities)
    {
        LibraDexGenericScalarCodec<TKey>.Encode32(lowerKey, out ulong lower0, out ulong lower1, out ulong lower2, out ulong lower3);
        LibraDexGenericScalarCodec<TKey>.Encode32(upperKey, out ulong upper0, out ulong upper1, out ulong upper2, out ulong upper3);
        ulong[] identityHighs = ArrayPool<ulong>.Shared.Rent(identities.Length);
        ulong[] identityLows = ArrayPool<ulong>.Shared.Rent(identities.Length);
        byte[] shelfScratch = ArrayPool<byte>.Shared.Rent(Fixed32Scalar16Profile.Default64KiB.ShelfExtentSize);
        using RouteTargetKindCache targetKindCache = RouteTargetKindCache.Rent();
        try
        {
            int count = session.ReadFixed32Scalar16IdentityRange(
                RootRouterOffset,
                Fixed32Scalar16Profile.Default64KiB,
                lower0,
                lower1,
                lower2,
                lower3,
                upper0,
                upper1,
                upper2,
                upper3,
                maxRouterHops: 32,
                identityHighs.AsSpan(0, identities.Length),
                identityLows.AsSpan(0, identities.Length),
                shelfScratch.AsSpan(0, Fixed32Scalar16Profile.Default64KiB.ShelfExtentSize),
                targetKindCache);

            for (int i = 0; i < count; i++)
            {
                identities[i] = LibraDexGenericScalarCodec<TIdentity>.Decode16(identityHighs[i], identityLows[i]);
            }

            return new LibraDexGenericRangeReadResult(count, UsedPooledScratch: true, UsedEncodedIdentityScratch: true, CoalescingEnabled: false, RangeScratchShelfCapacity: 1);
        }
        finally
        {
            ArrayPool<ulong>.Shared.Return(identityHighs);
            ArrayPool<ulong>.Shared.Return(identityLows);
            ArrayPool<byte>.Shared.Return(shelfScratch);
        }
    }

    private Fixed32Scalar16RangeReader OpenFixed32Scalar16RangeReader(TKey lowerKey, TKey upperKey)
    {
        LibraDexGenericScalarCodec<TKey>.Encode32(lowerKey, out ulong lower0, out ulong lower1, out ulong lower2, out ulong lower3);
        LibraDexGenericScalarCodec<TKey>.Encode32(upperKey, out ulong upper0, out ulong upper1, out ulong upper2, out ulong upper3);
        return session.OpenFixed32Scalar16RangeReader(RootRouterOffset, Fixed32Scalar16Profile.Default64KiB, lower0, lower1, lower2, lower3, upper0, upper1, upper2, upper3);
    }
}
