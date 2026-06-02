namespace LibraDex;

/// <summary>
/// Describes one programmable fixed-key / variable-identity shelf shape.<br/>
/// `FV` shelves keep key width fixed per index while allowing identities to remain raw variable-length bytes.<br/>
/// </summary>
/// <param name="ShelfExtentSize">The total shelf extent size in bytes.</param>
/// <param name="KeySize">The fixed key width in bytes.</param>
/// <param name="MaxIdentityLength">The maximum variable identity length accepted by the shelf.</param>
internal readonly record struct FixedNVarIdentityProfile(int ShelfExtentSize, int KeySize, int MaxIdentityLength)
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
