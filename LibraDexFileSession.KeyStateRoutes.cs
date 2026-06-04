using LibraDex.Layouts;
using LibraDex.Views;

namespace LibraDex;

internal enum KeyStateRoute : byte
{
    Null = 1,
    Empty = 2
}

public sealed partial class LibraDexFileSession
{
    /// <summary>
    /// Inserts one scalar-8 identity into the compact null-or-empty key-state route for an index slot.<br/>
    /// The first insert creates the identity-only shelf, writes the identity, appends updated catalog metadata, and publishes both through one commit.<br/>
    /// Later inserts rewrite only the compact shelf page and preserve sorted identity order for fast enumeration and counting.<br/>
    /// </summary>
    /// <param name="slotIndex">The fixed directory slot that owns the index.</param>
    /// <param name="route">The key-state route to update.</param>
    /// <param name="encodedIdentity">The encoded scalar-8 identity to store.</param>
    /// <returns><see langword="true"/> when a new identity was inserted; otherwise <see langword="false"/> when it already existed.</returns>
    internal bool InsertScalar8KeyStateIdentity(int slotIndex, KeyStateRoute route, ulong encodedIdentity)
    {
        long shelfOffset = GetKeyStateRouteOffset(slotIndex, route, out IndexDirectorySlotSnapshot slot, out CatalogIndexMetadata metadata);
        if (shelfOffset <= 0)
        {
            RawDataReservation shelfReservation = kernel.Reserve(KeyStateIdentityShelfLayout.ExtentSize);
            KeyStateIdentityShelf createdShelf = new(shelfReservation.Span);
            createdShelf.InitializeScalar8();
            bool inserted = createdShelf.InsertScalar8(encodedIdentity);
            PublishCreatedKeyStateShelf(slot, metadata, route, shelfReservation.Extent.Offset);
            return inserted;
        }

        byte[] shelfBytes = ReadKeyStateShelfBytes(shelfOffset);
        KeyStateIdentityShelf shelf = new(shelfBytes);
        bool changed = shelf.InsertScalar8(encodedIdentity);
        if (!changed)
        {
            return false;
        }

        RawDataReservation rewrite = kernel.ReserveAt(shelfOffset, KeyStateIdentityShelfLayout.ExtentSize);
        shelfBytes.CopyTo(rewrite.Span);
        CommitAndInvalidateRouterReadCache();
        return true;
    }

    /// <summary>
    /// Inserts one scalar-16 identity into the compact null-or-empty key-state route for an index slot.<br/>
    /// This gives GUID-like and other 16-byte identity indexes the same key-state storage path as scalar-8 identities without storing duplicate key bytes.<br/>
    /// The route offset is created and published through catalog metadata on the first insert.<br/>
    /// </summary>
    /// <param name="slotIndex">The fixed directory slot that owns the index.</param>
    /// <param name="route">The key-state route to update.</param>
    /// <param name="encodedIdentityHigh">The high sortable identity half.</param>
    /// <param name="encodedIdentityLow">The low sortable identity half.</param>
    /// <returns><see langword="true"/> when a new identity was inserted; otherwise <see langword="false"/> when it already existed.</returns>
    internal bool InsertScalar16KeyStateIdentity(int slotIndex, KeyStateRoute route, ulong encodedIdentityHigh, ulong encodedIdentityLow)
    {
        long shelfOffset = GetKeyStateRouteOffset(slotIndex, route, out IndexDirectorySlotSnapshot slot, out CatalogIndexMetadata metadata);
        if (shelfOffset <= 0)
        {
            RawDataReservation shelfReservation = kernel.Reserve(KeyStateIdentityShelfLayout.ExtentSize);
            KeyStateIdentityShelf createdShelf = new(shelfReservation.Span);
            createdShelf.InitializeScalar16();
            bool inserted = createdShelf.InsertScalar16(encodedIdentityHigh, encodedIdentityLow);
            PublishCreatedKeyStateShelf(slot, metadata, route, shelfReservation.Extent.Offset);
            return inserted;
        }

        byte[] shelfBytes = ReadKeyStateShelfBytes(shelfOffset);
        KeyStateIdentityShelf shelf = new(shelfBytes);
        bool changed = shelf.InsertScalar16(encodedIdentityHigh, encodedIdentityLow);
        if (!changed)
        {
            return false;
        }

        RawDataReservation rewrite = kernel.ReserveAt(shelfOffset, KeyStateIdentityShelfLayout.ExtentSize);
        shelfBytes.CopyTo(rewrite.Span);
        CommitAndInvalidateRouterReadCache();
        return true;
    }

    /// <summary>
    /// Reads all scalar-8 identities from a compact null-or-empty key-state route.<br/>
    /// Missing routes return an empty array, allowing condition routing to include key-state routes only when present.<br/>
    /// Returned identities are already sorted by encoded identity value.<br/>
    /// </summary>
    /// <param name="slotIndex">The fixed directory slot that owns the index.</param>
    /// <param name="route">The key-state route to read.</param>
    /// <returns>The encoded scalar-8 identities in route order.</returns>
    internal ulong[] ReadScalar8KeyStateIdentities(int slotIndex, KeyStateRoute route)
    {
        long shelfOffset = GetKeyStateRouteOffset(slotIndex, route, out _, out _);
        if (shelfOffset <= 0)
        {
            return [];
        }

        byte[] shelfBytes = ReadKeyStateShelfBytes(shelfOffset);
        KeyStateIdentityShelfReadOnly shelf = new(shelfBytes);
        ulong[] identities = new ulong[shelf.ItemCount];
        shelf.CopyScalar8Identities(identities);
        return identities;
    }

    /// <summary>
    /// Reads all scalar-16 identities from a compact null-or-empty key-state route.<br/>
    /// Missing routes return empty high/low arrays, while present routes enumerate identities in sorted encoded order.<br/>
    /// The split high/low arrays match the existing fixed scalar-16 range-reader conventions.<br/>
    /// </summary>
    /// <param name="slotIndex">The fixed directory slot that owns the index.</param>
    /// <param name="route">The key-state route to read.</param>
    /// <returns>Parallel high and low identity-half arrays.</returns>
    internal (ulong[] Highs, ulong[] Lows) ReadScalar16KeyStateIdentities(int slotIndex, KeyStateRoute route)
    {
        long shelfOffset = GetKeyStateRouteOffset(slotIndex, route, out _, out _);
        if (shelfOffset <= 0)
        {
            return ([], []);
        }

        byte[] shelfBytes = ReadKeyStateShelfBytes(shelfOffset);
        KeyStateIdentityShelfReadOnly shelf = new(shelfBytes);
        ulong[] highs = new ulong[shelf.ItemCount];
        ulong[] lows = new ulong[shelf.ItemCount];
        shelf.CopyScalar16Identities(highs, lows);
        return (highs, lows);
    }

    private long GetKeyStateRouteOffset(
        int slotIndex,
        KeyStateRoute route,
        out IndexDirectorySlotSnapshot slot,
        out CatalogIndexMetadata metadata)
    {
        if (!TryFindIndexDirectorySlot(slotIndex, out slot))
        {
            throw new InvalidDataException("The requested index slot is not active.");
        }

        if (!TryReadCatalogIndexMetadata(slot, out metadata))
        {
            throw new InvalidDataException("The requested index slot does not have decodable catalog metadata for key-state routes.");
        }

        return route switch
        {
            KeyStateRoute.Null => metadata.NullKeyRouteOffset,
            KeyStateRoute.Empty => metadata.EmptyKeyRouteOffset,
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, "Unknown key-state route.")
        };
    }

    private void PublishCreatedKeyStateShelf(
        IndexDirectorySlotSnapshot slot,
        CatalogIndexMetadata metadata,
        KeyStateRoute route,
        long shelfOffset)
    {
        CatalogIndexMetadata updatedMetadata = route switch
        {
            KeyStateRoute.Null => metadata with { NullKeyRouteOffset = shelfOffset },
            KeyStateRoute.Empty => metadata with { EmptyKeyRouteOffset = shelfOffset },
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, "Unknown key-state route.")
        };
        int metadataLength = CatalogIndexMetadataCodec.GetEncodedSize(updatedMetadata);
        RawDataReservation metadataReservation = kernel.Reserve(metadataLength);
        CatalogIndexMetadataCodec.Write(metadataReservation.Span, updatedMetadata);
        UpsertIndexDirectorySlot(slot with
        {
            MetadataOffset = metadataReservation.Extent.Offset,
            Generation = slot.Generation + 1
        });
    }

    private byte[] ReadKeyStateShelfBytes(long shelfOffset)
    {
        if (shelfOffset <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(shelfOffset), shelfOffset, "The key-state shelf offset must be positive.");
        }

        byte[] shelfBytes = new byte[KeyStateIdentityShelfLayout.ExtentSize];
        kernel.Read(shelfOffset, shelfBytes);
        return shelfBytes;
    }
}
