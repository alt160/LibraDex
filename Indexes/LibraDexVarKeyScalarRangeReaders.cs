using LibraDex.Views;

namespace LibraDex;

internal sealed partial class LibraDexFileSession
{
    /// <summary>
    /// Opens a forward-only `VS8` routed range reader for an inclusive raw-key range.<br/>
    /// The reader streams rows as key spans plus scalar identities so callers can choose identity-only, key-only, or full tuple iteration without a materializing adapter.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The root router offset for the index.</param>
    /// <param name="maxKeyLength">The maximum raw key length accepted by the profile family.</param>
    /// <param name="lowerKey">The inclusive lower raw key.</param>
    /// <param name="upperKey">The inclusive upper raw key.</param>
    /// <param name="maxRouterHops">The maximum number of router pages to follow.</param>
    /// <returns>A cursor positioned before the first matching row.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="upperKey"/> sorts before <paramref name="lowerKey"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maxRouterHops"/> is not positive.</exception>
    internal VarKeyScalar8RangeReader OpenVarKeyScalar8RangeReader(
        long rootRouterOffset,
        int maxKeyLength,
        ReadOnlySpan<byte> lowerKey,
        ReadOnlySpan<byte> upperKey,
        int maxRouterHops = DefaultVarKeyScalar8MaxRouterHops,
        bool decodeLogicalKeys = false,
        bool captureDiagnostics = false)
    {
        if (lowerKey.SequenceCompareTo(upperKey) > 0)
        {
            throw new ArgumentException("The upper VS8 key must be greater than or equal to the lower key.", nameof(upperKey));
        }

        if (maxRouterHops <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRouterHops), maxRouterHops, "The VS8 range reader maximum router hop count must be positive.");
        }

        return new VarKeyScalar8RangeReader(this, rootRouterOffset, maxKeyLength, lowerKey, upperKey, maxRouterHops, decodeLogicalKeys, captureDiagnostics);
    }

    /// <summary>
    /// Opens a forward-only `VS16` routed range reader for an inclusive raw-key range.<br/>
    /// The reader streams rows as key spans plus high/low scalar identity halves so callers can choose identity-only, key-only, or full tuple iteration without a materializing adapter.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The root router offset for the index.</param>
    /// <param name="maxKeyLength">The maximum raw key length accepted by the profile family.</param>
    /// <param name="lowerKey">The inclusive lower raw key.</param>
    /// <param name="upperKey">The inclusive upper raw key.</param>
    /// <param name="maxRouterHops">The maximum number of router pages to follow.</param>
    /// <returns>A cursor positioned before the first matching row.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="upperKey"/> sorts before <paramref name="lowerKey"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maxRouterHops"/> is not positive.</exception>
    internal VarKeyScalar16RangeReader OpenVarKeyScalar16RangeReader(
        long rootRouterOffset,
        int maxKeyLength,
        ReadOnlySpan<byte> lowerKey,
        ReadOnlySpan<byte> upperKey,
        int maxRouterHops = DefaultVarKeyScalar16MaxRouterHops,
        bool decodeLogicalKeys = false)
    {
        if (lowerKey.SequenceCompareTo(upperKey) > 0)
        {
            throw new ArgumentException("The upper VS16 key must be greater than or equal to the lower key.", nameof(upperKey));
        }

        if (maxRouterHops <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRouterHops), maxRouterHops, "The VS16 range reader maximum router hop count must be positive.");
        }

        return new VarKeyScalar16RangeReader(this, rootRouterOffset, maxKeyLength, lowerKey, upperKey, maxRouterHops, decodeLogicalKeys);
    }

    /// <summary>
    /// Reads and validates one `VS8` shelf as a read-only span-backed view.<br/>
    /// The method reuses the session shelf cache when available so range readers share DK/cache behavior with existing identity-copy range reads.<br/>
    /// </summary>
    /// <param name="shelfOffset">The file offset of the shelf to read.</param>
    /// <param name="maxKeyLength">The maximum raw key length accepted by the profile family.</param>
    /// <returns>A valid read-only `VS8` shelf view.</returns>
    /// <exception cref="InvalidDataException">Thrown when the loaded shelf bytes fail validation.</exception>
    internal VarKeyScalar8ReadOnly ReadVarKeyScalar8ReadOnlyShelf(long shelfOffset, int maxKeyLength)
    {
        byte[] shelfBytes = ReadVarKeyScalar8ShelfBytes(shelfOffset, maxKeyLength, out VarKeyScalar8Profile profile);
        VarKeyScalar8ReadOnly shelf = new(shelfBytes, profile);
        if (!shelf.IsValid)
        {
            throw new InvalidDataException("The routed VS8 shelf bytes are invalid.");
        }

        return shelf;
    }

    /// <summary>
    /// Reads and validates one `VS16` shelf as a read-only span-backed view.<br/>
    /// The method reuses the session shelf cache when available so range readers share DK/cache behavior with existing identity-copy range reads.<br/>
    /// </summary>
    /// <param name="shelfOffset">The file offset of the shelf to read.</param>
    /// <param name="maxKeyLength">The maximum raw key length accepted by the profile family.</param>
    /// <returns>A valid read-only `VS16` shelf view.</returns>
    /// <exception cref="InvalidDataException">Thrown when the loaded shelf bytes fail validation.</exception>
    internal VarKeyScalar16ReadOnly ReadVarKeyScalar16ReadOnlyShelf(long shelfOffset, int maxKeyLength)
    {
        byte[] shelfBytes = ReadVarKeyScalar16ShelfBytes(shelfOffset, maxKeyLength, out VarKeyScalar16Profile profile);
        VarKeyScalar16ReadOnly shelf = new(shelfBytes, profile);
        if (!shelf.IsValid)
        {
            throw new InvalidDataException("The routed VS16 shelf bytes are invalid.");
        }

        return shelf;
    }
}
