namespace LibraDex;

/// <summary>
/// Carries the runtime location and sizing profile for a single-shelf `FixedNScalar8` index.<br/>
/// This is the first durable bridge for programmable fixed-key BigInt storage; routed FSN storage can widen this handle later without changing public BigInt syntax.<br/>
/// </summary>
/// <param name="RootOffset">The durable offset of the current fixed-key root, either a router or a single bridge shelf.</param>
/// <param name="Profile">The programmable fixed-key shelf profile.</param>
/// <param name="IsRouted">Whether <paramref name="RootOffset"/> points at a root router instead of a direct shelf.</param>
internal readonly record struct FixedNScalar8IndexHandle(long RootOffset, FixedNScalar8Profile Profile, bool IsRouted)
{
    /// <summary>
    /// Validates the handle before it is used by public index facades.<br/>
    /// The shelf offset must point at a durable DataKernel extent and the profile must be able to store at least one tuple.<br/>
    /// </summary>
    public void Validate()
    {
        if (RootOffset <= 0)
        {
            throw new InvalidDataException("The FSN-8 index handle has no root offset.");
        }

        if (Profile.MaxItemCount == 0)
        {
            throw new InvalidDataException("The FSN-8 shelf index handle has no item capacity.");
        }
    }
}

/// <summary>
/// Carries the runtime location and sizing profile for a `FixedNScalar16` index.<br/>
/// This first handle points at a durable single shelf; routed `FSN-16` can widen it later without changing the fixed BigInt public syntax.<br/>
/// </summary>
/// <param name="RootOffset">The durable offset of the current fixed-key root, either a router or a single bridge shelf.</param>
/// <param name="Profile">The programmable fixed-key shelf profile.</param>
/// <param name="IsRouted">Whether <paramref name="RootOffset"/> points at a root router instead of a direct shelf.</param>
internal readonly record struct FixedNScalar16IndexHandle(long RootOffset, FixedNScalar16Profile Profile, bool IsRouted)
{
    /// <summary>
    /// Validates the handle before it is used by public index facades.<br/>
    /// The shelf offset must point at a durable DataKernel extent and the profile must be able to store at least one tuple.<br/>
    /// </summary>
    public void Validate()
    {
        if (RootOffset <= 0)
        {
            throw new InvalidDataException("The FSN-16 index handle has no root offset.");
        }

        if (Profile.MaxItemCount == 0)
        {
            throw new InvalidDataException("The FSN-16 index handle has no item capacity.");
        }
    }
}

/// <summary>
/// Reports the structural effect of inserting an item into a programmable fixed-key shelf.<br/>
/// The result intentionally mirrors the fixed scalar shelf outcomes so future routed `FSN` paths can share insert decision terminology.<br/>
/// </summary>
internal enum FixedNScalarInsertResult
{
    /// <summary>
    /// The item was inserted and shelf bytes were mutated.<br/>
    /// </summary>
    Inserted,

    /// <summary>
    /// The exact `(key, identity)` tuple already existed, so no shelf bytes were mutated.<br/>
    /// </summary>
    AlreadyPresent,

    /// <summary>
    /// The key already existed in a unique index, so no shelf bytes were mutated.<br/>
    /// </summary>
    KeyConflict,

    /// <summary>
    /// The shelf had no remaining item capacity.<br/>
    /// </summary>
    Full
}

/// <summary>
/// Carries the runtime location and sizing profile for a routed fixed-key / variable-identity `FV` index.<br/>
/// </summary>
/// <param name="RootOffset">The durable root offset, normally a root router.</param>
/// <param name="Profile">The programmable fixed-key / variable-identity shelf profile.</param>
/// <param name="IsRouted">Whether <paramref name="RootOffset"/> points at a router instead of a direct shelf.</param>
internal readonly record struct FixedNVarIdentityIndexHandle(long RootOffset, FixedNVarIdentityProfile Profile, bool IsRouted)
{
    /// <summary>
    /// Validates that this handle has a durable root and a usable `FV` profile.<br/>
    /// </summary>
    public void Validate()
    {
        if (RootOffset <= 0)
        {
            throw new InvalidDataException("The FV index handle has no root offset.");
        }

        if (Profile.MaxItemCount == 0)
        {
            throw new InvalidDataException("The FV index handle has no item capacity.");
        }
    }
}

/// <summary>
/// Represents the result of inserting into a programmable fixed-key / variable-identity shelf.<br/>
/// </summary>
internal enum FixedNVarIdentityInsertResult
{
    Invalid = 0,
    Inserted = 1,
    AlreadyPresent = 2,
    KeyConflict = 3,
    Full = 4
}

/// <summary>
/// Describes one programmable fixed-key / variable-identity shelf shape.<br/>
/// `FV` shelves keep key width fixed per index while allowing identities to remain raw variable-length bytes.<br/>
/// </summary>
/// <param name="ShelfExtentSize">The total shelf extent size in bytes.</param>
/// <param name="KeySize">The fixed key width in bytes.</param>
/// <param name="MaxIdentityLength">The maximum variable identity length accepted by the shelf.</param>
/// <param name="Descending">Whether the physical tuple slots run from highest key and identity to lowest.<br/></param>
internal readonly record struct FixedNVarIdentityProfile(int ShelfExtentSize, int KeySize, int MaxIdentityLength, bool Descending = false)
{
    public const int DefaultShelfExtentSize = 64 * 1024;
    public const int MinimumKeySize = 1;
    public const int MaximumKeySize = 512;
    public const int MinimumIdentityLength = 1;
    public const int MaximumIdentityLength = 16 * 1024;

    /// <summary>
    /// Gets the maximum tuple count implied by this shelf's reserved slot region.<br/>
    /// Payload pressure can still make a shelf full earlier when identities are larger than the reserve floor.<br/>
    /// </summary>
    public int MaxItemCount => LibraDex.Layouts.FixedNVarIdentityLayout.CalculateSlotCapacityBytes(ShelfExtentSize, KeySize) / LibraDex.Layouts.FixedNVarIdentityLayout.SlotSize;

    /// <summary>
    /// Creates a default 64 KiB `FV` profile for one fixed key size and variable identity limit.<br/>
    /// </summary>
    /// <param name="keySize">The fixed key width in bytes.</param>
    /// <param name="maxIdentityLength">The maximum variable identity length accepted by this shelf family.</param>
    /// <returns>The validated profile.</returns>
    public static FixedNVarIdentityProfile Default64KiB(int keySize, int maxIdentityLength)
    {
        return Create(DefaultShelfExtentSize, keySize, maxIdentityLength);
    }

    /// <summary>
    /// Creates and validates an `FV` profile.<br/>
    /// </summary>
    /// <param name="shelfExtentSize">The total shelf extent size in bytes.</param>
    /// <param name="keySize">The fixed key width in bytes.</param>
    /// <param name="maxIdentityLength">The maximum variable identity length accepted by this shelf family.</param>
    /// <returns>The validated profile.</returns>
    public static FixedNVarIdentityProfile Create(int shelfExtentSize, int keySize, int maxIdentityLength)
    {
        if (shelfExtentSize <= LibraDex.Layouts.FixedNVarIdentityLayout.HeaderSize)
        {
            throw new ArgumentOutOfRangeException(nameof(shelfExtentSize), shelfExtentSize, "The FV shelf extent must leave room after the header.");
        }

        if (keySize is < MinimumKeySize or > MaximumKeySize)
        {
            throw new ArgumentOutOfRangeException(nameof(keySize), keySize, $"The FV key size must be between {MinimumKeySize} and {MaximumKeySize} bytes.");
        }

        if (maxIdentityLength is < MinimumIdentityLength or > MaximumIdentityLength)
        {
            throw new ArgumentOutOfRangeException(nameof(maxIdentityLength), maxIdentityLength, $"The FV identity length must be between {MinimumIdentityLength} and {MaximumIdentityLength} bytes.");
        }

        return new FixedNVarIdentityProfile(shelfExtentSize, keySize, maxIdentityLength);
    }
}
