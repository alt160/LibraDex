namespace LibraDex;

/// <summary>
/// Provides the first narrow LibraDex boundary intended for Abraxas read/query integration.<br/>
/// The adapter binds one catalog identity group and executes completed LibraDex/Abraxas-style condition descriptors as identity-only queries.<br/>
/// It does not hydrate records, own source objects, or introduce mutation semantics; callers remain responsible for resolving returned identities into their own data model.<br/>
/// Normal Abraxas integration should use materialized identity results under the owning Abraxas serialization boundary; cursor methods expose live LibraDex cursors and do not create a published-reader snapshot.<br/>
/// </summary>
/// <typeparam name="TIdentity">The caller-owned identity type returned by this adapter.</typeparam>
public sealed class AbraxasIdentityQueryAdapter<TIdentity>
{
    private readonly CatalogIdentityGroupIndexes indexes;

    internal AbraxasIdentityQueryAdapter(CatalogIdentityGroupIndexes indexes)
    {
        ArgumentNullException.ThrowIfNull(indexes);
        this.indexes = indexes;
    }

    /// <summary>
    /// Gets the LibraDex identity group bound to this adapter.<br/>
    /// Abraxas query intent supplied to this adapter must target this group so index-name resolution remains local and explicit.<br/>
    /// </summary>
    public string Group => indexes.Name;

    /// <summary>
    /// Materializes caller-owned identities for a completed condition over the bound LibraDex identity group.<br/>
    /// This is the first Abraxas-facing terminal: condition intent enters LibraDex, the existing condition execution spine resolves indexes and projections, and only identities are returned.<br/>
    /// The returned list is disconnected after this method returns, so normal Abraxas callers should use this surface instead of live cursors when writes or catch-up may occur later.<br/>
    /// Completeness is still relative to the currently visible index state and any Abraxas freshness policy such as `EventualIndexed`; this method does not create a published-reader view during an active same-session write.<br/>
    /// Use <paramref name="deduplication"/> and paging arguments to make result shape explicit instead of hiding sort or set work behind adapter defaults.<br/>
    /// </summary>
    /// <param name="condition">The completed condition descriptor to execute.</param>
    /// <param name="ordering">The requested identity ordering contract.</param>
    /// <param name="deduplication">The requested duplicate identity policy.</param>
    /// <param name="skip">The number of matching identities to skip.</param>
    /// <param name="take">The optional maximum number of identities to return.</param>
    /// <param name="bookmark">The optional continuation bookmark.</param>
    /// <returns>A typed list of caller-owned identities.</returns>
    public IReadOnlyList<TIdentity> Get(
        LibraDexConditionEndCondition condition,
        IdentityResultOrdering ordering = IdentityResultOrdering.PlanNatural,
        IdentityDeduplication deduplication = IdentityDeduplication.Distinct,
        int skip = 0,
        int? take = null,
        LibraDexBookmark? bookmark = null)
    {
        VerifyMaterializedReadBoundary();
        return indexes.GetIdentities<TIdentity>(condition, ordering, deduplication, skip, take, bookmark);
    }

    /// <summary>
    /// Materializes caller-owned identities for a completed condition over the bound LibraDex identity group.<br/>
    /// This alias keeps the adapter readable at call sites that prefer an explicit identity noun while preserving the same execution path as <see cref="Get"/>.<br/>
    /// </summary>
    /// <param name="condition">The completed condition descriptor to execute.</param>
    /// <param name="ordering">The requested identity ordering contract.</param>
    /// <param name="deduplication">The requested duplicate identity policy.</param>
    /// <param name="skip">The number of matching identities to skip.</param>
    /// <param name="take">The optional maximum number of identities to return.</param>
    /// <param name="bookmark">The optional continuation bookmark.</param>
    /// <returns>A typed list of caller-owned identities.</returns>
    public IReadOnlyList<TIdentity> GetIdentities(
        LibraDexConditionEndCondition condition,
        IdentityResultOrdering ordering = IdentityResultOrdering.PlanNatural,
        IdentityDeduplication deduplication = IdentityDeduplication.Distinct,
        int skip = 0,
        int? take = null,
        LibraDexBookmark? bookmark = null)
    {
        return Get(condition, ordering, deduplication, skip, take, bookmark);
    }

    /// <summary>
    /// Opens a forward-only cursor over caller-owned identities for a completed condition over the bound LibraDex identity group.<br/>
    /// The returned cursor is the existing LibraDex identity cursor, so this adapter does not add a per-row callback, delegate, or wrapper object in the traversal loop.<br/>
    /// The cursor remains tied to the live LibraDex session and should not be used as the default concurrent Abraxas read boundary.<br/>
    /// </summary>
    /// <param name="condition">The completed condition descriptor to execute.</param>
    /// <param name="ordering">The requested identity ordering contract.</param>
    /// <param name="deduplication">The requested duplicate identity policy.</param>
    /// <param name="skip">The number of matching identities to skip.</param>
    /// <param name="take">The optional maximum number of identities to return.</param>
    /// <param name="bookmark">The optional continuation bookmark.</param>
    /// <returns>A forward-only cursor over caller-owned identities.</returns>
    public LibraDexIdentityCursor<TIdentity> OpenCursor(
        LibraDexConditionEndCondition condition,
        IdentityResultOrdering ordering = IdentityResultOrdering.PlanNatural,
        IdentityDeduplication deduplication = IdentityDeduplication.Distinct,
        int skip = 0,
        int? take = null,
        LibraDexBookmark? bookmark = null)
    {
        return indexes.GetCursor<TIdentity>(condition, ordering, deduplication, skip, take, bookmark);
    }

    /// <summary>
    /// Opens a forward-only cursor over caller-owned identities for a completed condition over the bound LibraDex identity group.<br/>
    /// This alias mirrors the catalog identity-group cursor naming while keeping Abraxas integration code on the named adapter surface.<br/>
    /// </summary>
    /// <param name="condition">The completed condition descriptor to execute.</param>
    /// <param name="ordering">The requested identity ordering contract.</param>
    /// <param name="deduplication">The requested duplicate identity policy.</param>
    /// <param name="skip">The number of matching identities to skip.</param>
    /// <param name="take">The optional maximum number of identities to return.</param>
    /// <param name="bookmark">The optional continuation bookmark.</param>
    /// <returns>A forward-only cursor over caller-owned identities.</returns>
    public LibraDexIdentityCursor<TIdentity> GetCursor(
        LibraDexConditionEndCondition condition,
        IdentityResultOrdering ordering = IdentityResultOrdering.PlanNatural,
        IdentityDeduplication deduplication = IdentityDeduplication.Distinct,
        int skip = 0,
        int? take = null,
        LibraDexBookmark? bookmark = null)
    {
        return OpenCursor(condition, ordering, deduplication, skip, take, bookmark);
    }

    /// <summary>
    /// Applies the opt-in detailed diagnostic guard for the Abraxas materialized-read boundary.<br/>
    /// The guard fails fast when a same-session write window is already active so the adapter does not materialize identities from pending writer state and then look like a safe published-reader surface.<br/>
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when detailed diagnostics detect an active same-session write window.<br/></exception>
    private void VerifyMaterializedReadBoundary()
    {
        indexes.Catalog.Session.VerifyNoActiveDiagnosticWriteWindowForMaterializedRead("Abraxas identity materialized read");
    }
}
