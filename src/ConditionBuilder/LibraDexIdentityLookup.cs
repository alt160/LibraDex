namespace LibraDex;

/// <summary>
/// Selects when an opened index may create and populate its session-local identity-to-key lookup.<br/>
/// The lookup is an in-memory acceleration structure; the selected mode is not persisted into the catalog file.<br/>
/// </summary>
public enum IdentityLookupMode
{
    /// <summary>
    /// Prevents this index from retaining identity inversion state.<br/>
    /// Identity-side operations remain valid and use forward-index traversal when required.<br/>
    /// </summary>
    Disabled = 0,

    /// <summary>
    /// Allows inversion only after the developer explicitly calls <see cref="LibraDexIdentityLookup.Build"/> or enables the index through its index-set inverse surface.<br/>
    /// This is the default and does not add session-local inversion memory merely because an index was opened.<br/>
    /// </summary>
    Explicit = 1,

    /// <summary>
    /// Creates a lazy inversion when the first identity-side operation can benefit from it.<br/>
    /// Every tuple encountered by the required forward walk is retained so later identity operations can reuse that work.<br/>
    /// </summary>
    CreateOnFirstUse = 2,

    /// <summary>
    /// Creates an incomplete inversion when the index is opened, then backfills tuples during identity-side use.<br/>
    /// Catalog freshness checks invalidate affected session-local proof before a missing identity can be treated as authoritative; this avoids a complete blocking scan during open.<br/>
    /// </summary>
    CreateOnOpen = 3,

    /// <summary>
    /// Creates and completely populates the inversion while the index is opened.<br/>
    /// Opening or configuring a live index with this mode blocks the calling thread until the forward-index scan completes or fails.<br/>
    /// </summary>
    BuildOnOpen = 4
}

/// <summary>
/// Describes the realized session-local identity-lookup state for one opened index.<br/>
/// This is distinct from <see cref="IdentityLookupMode"/>: the mode is developer policy, while the state reports what has actually been created and populated.<br/>
/// </summary>
public enum IdentityLookupState
{
    /// <summary>
    /// Identity inversion is disabled for this opened index.<br/>
    /// </summary>
    Unavailable = 0,

    /// <summary>
    /// No inversion has been created for this opened index.<br/>
    /// </summary>
    NotCreated = 1,

    /// <summary>
    /// The inversion exists but has not completely scanned the forward index.<br/>
    /// Positive rows already discovered are usable; a missing row still requires continued traversal.<br/>
    /// </summary>
    Partial = 2,

    /// <summary>
    /// The inversion completely represents the current forward index.<br/>
    /// Missing identities can be rejected without another forward scan.<br/>
    /// </summary>
    Complete = 3
}

/// <summary>
/// Provides runtime identity-lookup lifecycle controls for one opened catalog index.<br/>
/// Accessing this readonly facade does not allocate another owner object; the opened index and its catalog retain the actual runtime state.<br/>
/// </summary>
public readonly struct LibraDexIdentityLookup
{
    private readonly IIndex index;

    internal LibraDexIdentityLookup(IIndex index)
    {
        this.index = index ?? throw new ArgumentNullException(nameof(index));
    }

    /// <summary>
    /// Gets the identity-lookup policy selected for this opened index instance.<br/>
    /// The value describes runtime behavior only and is not persisted into catalog metadata.<br/>
    /// </summary>
    public IdentityLookupMode Mode
        => index.Catalog.GetIdentityLookupMode(index.Group, index.Name);

    /// <summary>
    /// Gets the realized identity-lookup state for this opened index.<br/>
    /// Reading this property does not build or advance the inversion.<br/>
    /// </summary>
    public IdentityLookupState State
        => index.Catalog.GetIdentityLookupState(index.Group, index.Name, Mode);

    /// <summary>
    /// Applies a new identity-lookup policy to this already-open index.<br/>
    /// Configuring <see cref="IdentityLookupMode.CreateOnOpen"/> creates the lazy inversion immediately because the index is already open.<br/>
    /// Configuring <see cref="IdentityLookupMode.BuildOnOpen"/> synchronously scans the forward index and blocks the calling thread until the inversion is complete or the operation fails.<br/>
    /// Build duration and temporary CPU use scale with the number of visible index entries; use <see cref="IdentityLookupMode.CreateOnOpen"/> or <see cref="IdentityLookupMode.CreateOnFirstUse"/> when that blocking cost is unsuitable.<br/>
    /// </summary>
    /// <param name="mode">The runtime lookup policy to apply.<br/></param>
    public void Configure(IdentityLookupMode mode)
    {
        ValidateMode(mode);
        index.Catalog.ConfigureIdentityLookup(index, mode);
    }

    /// <summary>
    /// Creates and completely populates the identity inversion for this opened index.<br/>
    /// This method synchronously scans the forward index and blocks the calling thread until completion or failure.<br/>
    /// The selected <see cref="Mode"/> is left unchanged so an explicitly timed build does not silently change later lifecycle policy.<br/>
    /// </summary>
    public void Build()
    {
        index.Catalog.BuildIdentityLookup(index);
    }

    /// <summary>
    /// Releases retained identity-inversion values for this index without closing the index handle.<br/>
    /// The configured mode remains unchanged; a later automatic or explicit trigger may create the inversion again unless the mode is <see cref="IdentityLookupMode.Disabled"/>.<br/>
    /// </summary>
    public void Clear()
    {
        index.Catalog.ClearIdentityLookup(index.Group, index.Name);
    }

    internal static void ValidateMode(IdentityLookupMode mode)
    {
        if (mode is < IdentityLookupMode.Disabled or > IdentityLookupMode.BuildOnOpen)
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "The identity lookup mode is not supported.");
    }
}
