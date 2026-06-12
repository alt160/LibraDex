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
    /// Inserts one scalar-8 identity into the null-or-empty key-state identity route for an index slot.<br/>
    /// The first insert creates an inline sorted identity route root, writes the identity, appends updated catalog metadata, and publishes both through one commit.<br/>
    /// Later inserts rewrite the route root until route promotion is connected; callers interact with route semantics rather than a terminal shelf shape.<br/>
    /// </summary>
    /// <param name="slotIndex">The fixed directory slot that owns the index.</param>
    /// <param name="route">The key-state route to update.</param>
    /// <param name="encodedIdentity">The encoded scalar-8 identity to store.</param>
    /// <returns><see langword="true"/> when a new identity was inserted; otherwise <see langword="false"/> when it already existed.</returns>
    internal bool InsertScalar8KeyStateIdentity(int slotIndex, KeyStateRoute route, ulong encodedIdentity)
    {
        long routeOffset = GetKeyStateRouteOffset(slotIndex, route, out IndexDirectorySlotSnapshot slot, out CatalogIndexMetadata metadata);
        if (routeOffset <= 0)
        {
            RawDataReservation routeReservation = kernel.Reserve(KeyStateIdentityRouteLayout.ExtentSize);
            KeyStateIdentityRoute createdRoute = new(routeReservation.Span);
            createdRoute.InitializeScalar8Inline();
            bool inserted = createdRoute.InsertScalar8(encodedIdentity);
            PublishCreatedKeyStateRoute(slot, metadata, route, routeReservation.Extent.Offset);
            return inserted;
        }

        byte[] routeBytes = ReadKeyStateRouteBytes(routeOffset);
        KeyStateIdentityRoute identityRoute = new(routeBytes);
        bool changed = identityRoute.InsertScalar8(encodedIdentity);
        if (!changed)
        {
            return false;
        }

        RawDataReservation rewrite = kernel.ReserveAt(routeOffset, KeyStateIdentityRouteLayout.ExtentSize);
        routeBytes.CopyTo(rewrite.Span);
        CommitAndInvalidateRouterReadCache();
        return true;
    }

    /// <summary>
    /// Inserts one scalar-16 identity into the null-or-empty key-state identity route for an index slot.<br/>
    /// This gives GUID-like and other 16-byte identity indexes the same key-state storage path as scalar-8 identities without storing duplicate key bytes.<br/>
    /// The route offset is created and published through catalog metadata on the first insert, and the route root can later promote to a ranged or routed identity shape.<br/>
    /// </summary>
    /// <param name="slotIndex">The fixed directory slot that owns the index.</param>
    /// <param name="route">The key-state route to update.</param>
    /// <param name="encodedIdentityHigh">The high sortable identity half.</param>
    /// <param name="encodedIdentityLow">The low sortable identity half.</param>
    /// <returns><see langword="true"/> when a new identity was inserted; otherwise <see langword="false"/> when it already existed.</returns>
    internal bool InsertScalar16KeyStateIdentity(int slotIndex, KeyStateRoute route, ulong encodedIdentityHigh, ulong encodedIdentityLow)
    {
        long routeOffset = GetKeyStateRouteOffset(slotIndex, route, out IndexDirectorySlotSnapshot slot, out CatalogIndexMetadata metadata);
        if (routeOffset <= 0)
        {
            RawDataReservation routeReservation = kernel.Reserve(KeyStateIdentityRouteLayout.ExtentSize);
            KeyStateIdentityRoute createdRoute = new(routeReservation.Span);
            createdRoute.InitializeScalar16Inline();
            bool inserted = createdRoute.InsertScalar16(encodedIdentityHigh, encodedIdentityLow);
            PublishCreatedKeyStateRoute(slot, metadata, route, routeReservation.Extent.Offset);
            return inserted;
        }

        byte[] routeBytes = ReadKeyStateRouteBytes(routeOffset);
        KeyStateIdentityRoute identityRoute = new(routeBytes);
        bool changed = identityRoute.InsertScalar16(encodedIdentityHigh, encodedIdentityLow);
        if (!changed)
        {
            return false;
        }

        RawDataReservation rewrite = kernel.ReserveAt(routeOffset, KeyStateIdentityRouteLayout.ExtentSize);
        routeBytes.CopyTo(rewrite.Span);
        CommitAndInvalidateRouterReadCache();
        return true;
    }

    /// <summary>
    /// Deletes one scalar-8 identity from the null-or-empty key-state identity route for an index slot.<br/>
    /// Missing routes and missing identities are no-ops, while present identities are removed by binary-searching the identity-keyed route root.<br/>
    /// </summary>
    /// <param name="slotIndex">The fixed directory slot that owns the index.</param>
    /// <param name="route">The key-state route to update.</param>
    /// <param name="encodedIdentity">The encoded scalar-8 identity to remove.</param>
    /// <returns><see langword="true"/> when an identity was removed; otherwise <see langword="false"/>.</returns>
    internal bool DeleteScalar8KeyStateIdentity(int slotIndex, KeyStateRoute route, ulong encodedIdentity)
    {
        long routeOffset = GetKeyStateRouteOffset(slotIndex, route, out _, out _);
        if (routeOffset <= 0)
        {
            return false;
        }

        byte[] routeBytes = ReadKeyStateRouteBytes(routeOffset);
        KeyStateIdentityRoute identityRoute = new(routeBytes);
        bool changed = identityRoute.DeleteScalar8(encodedIdentity);
        if (!changed)
        {
            return false;
        }

        RawDataReservation rewrite = kernel.ReserveAt(routeOffset, KeyStateIdentityRouteLayout.ExtentSize);
        routeBytes.CopyTo(rewrite.Span);
        CommitAndInvalidateRouterReadCache();
        return true;
    }

    /// <summary>
    /// Deletes one scalar-16 identity from the null-or-empty key-state identity route for an index slot.<br/>
    /// The identity is looked up by its encoded high/low lanes so delete and update paths avoid scanning high-cardinality key-state routes.<br/>
    /// </summary>
    /// <param name="slotIndex">The fixed directory slot that owns the index.</param>
    /// <param name="route">The key-state route to update.</param>
    /// <param name="encodedIdentityHigh">The high sortable identity half.</param>
    /// <param name="encodedIdentityLow">The low sortable identity half.</param>
    /// <returns><see langword="true"/> when an identity was removed; otherwise <see langword="false"/>.</returns>
    internal bool DeleteScalar16KeyStateIdentity(int slotIndex, KeyStateRoute route, ulong encodedIdentityHigh, ulong encodedIdentityLow)
    {
        long routeOffset = GetKeyStateRouteOffset(slotIndex, route, out _, out _);
        if (routeOffset <= 0)
        {
            return false;
        }

        byte[] routeBytes = ReadKeyStateRouteBytes(routeOffset);
        KeyStateIdentityRoute identityRoute = new(routeBytes);
        bool changed = identityRoute.DeleteScalar16(encodedIdentityHigh, encodedIdentityLow);
        if (!changed)
        {
            return false;
        }

        RawDataReservation rewrite = kernel.ReserveAt(routeOffset, KeyStateIdentityRouteLayout.ExtentSize);
        routeBytes.CopyTo(rewrite.Span);
        CommitAndInvalidateRouterReadCache();
        return true;
    }

    /// <summary>
    /// Tests whether a scalar-8 identity exists on a null-or-empty key-state route.<br/>
    /// Missing routes return <see langword="false"/> and present inline roots use binary search over encoded identities.<br/>
    /// </summary>
    /// <param name="slotIndex">The fixed directory slot that owns the index.</param>
    /// <param name="route">The key-state route to inspect.</param>
    /// <param name="encodedIdentity">The encoded scalar-8 identity to test.</param>
    /// <returns><see langword="true"/> when the identity is present.</returns>
    internal bool ContainsScalar8KeyStateIdentity(int slotIndex, KeyStateRoute route, ulong encodedIdentity)
    {
        long routeOffset = GetKeyStateRouteOffset(slotIndex, route, out _, out _);
        if (routeOffset <= 0)
        {
            return false;
        }

        byte[] routeBytes = ReadKeyStateRouteBytes(routeOffset);
        KeyStateIdentityRouteReadOnly identityRoute = new(routeBytes);
        return identityRoute.ContainsScalar8(encodedIdentity);
    }

    /// <summary>
    /// Tests whether a scalar-16 identity exists on a null-or-empty key-state route.<br/>
    /// The route is identity-keyed, so exact membership can be answered without enumerating all null-or-empty identities.<br/>
    /// </summary>
    /// <param name="slotIndex">The fixed directory slot that owns the index.</param>
    /// <param name="route">The key-state route to inspect.</param>
    /// <param name="encodedIdentityHigh">The high sortable identity half.</param>
    /// <param name="encodedIdentityLow">The low sortable identity half.</param>
    /// <returns><see langword="true"/> when the identity is present.</returns>
    internal bool ContainsScalar16KeyStateIdentity(int slotIndex, KeyStateRoute route, ulong encodedIdentityHigh, ulong encodedIdentityLow)
    {
        long routeOffset = GetKeyStateRouteOffset(slotIndex, route, out _, out _);
        if (routeOffset <= 0)
        {
            return false;
        }

        byte[] routeBytes = ReadKeyStateRouteBytes(routeOffset);
        KeyStateIdentityRouteReadOnly identityRoute = new(routeBytes);
        return identityRoute.ContainsScalar16(encodedIdentityHigh, encodedIdentityLow);
    }

    /// <summary>
    /// Reads all scalar-8 identities from a null-or-empty key-state identity route.<br/>
    /// Missing routes return an empty array, allowing condition routing to include key-state routes only when present.<br/>
    /// Returned identities are already sorted by encoded identity value.<br/>
    /// </summary>
    /// <param name="slotIndex">The fixed directory slot that owns the index.</param>
    /// <param name="route">The key-state route to read.</param>
    /// <returns>The encoded scalar-8 identities in route order.</returns>
    internal ulong[] ReadScalar8KeyStateIdentities(int slotIndex, KeyStateRoute route)
    {
        long routeOffset = GetKeyStateRouteOffset(slotIndex, route, out _, out _);
        if (routeOffset <= 0)
        {
            return [];
        }

        byte[] routeBytes = ReadKeyStateRouteBytes(routeOffset);
        KeyStateIdentityRouteReadOnly identityRoute = new(routeBytes);
        ulong[] identities = new ulong[identityRoute.ItemCount];
        identityRoute.CopyScalar8Identities(identities);
        return identities;
    }

    /// <summary>
    /// Reads all scalar-16 identities from a null-or-empty key-state identity route.<br/>
    /// Missing routes return empty high/low arrays, while present routes enumerate identities in sorted encoded order.<br/>
    /// The split high/low arrays match the existing fixed scalar-16 range-reader conventions.<br/>
    /// </summary>
    /// <param name="slotIndex">The fixed directory slot that owns the index.</param>
    /// <param name="route">The key-state route to read.</param>
    /// <returns>Parallel high and low identity-half arrays.</returns>
    internal (ulong[] Highs, ulong[] Lows) ReadScalar16KeyStateIdentities(int slotIndex, KeyStateRoute route)
    {
        long routeOffset = GetKeyStateRouteOffset(slotIndex, route, out _, out _);
        if (routeOffset <= 0)
        {
            return ([], []);
        }

        byte[] routeBytes = ReadKeyStateRouteBytes(routeOffset);
        KeyStateIdentityRouteReadOnly identityRoute = new(routeBytes);
        ulong[] highs = new ulong[identityRoute.ItemCount];
        ulong[] lows = new ulong[identityRoute.ItemCount];
        identityRoute.CopyScalar16Identities(highs, lows);
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

    private void PublishCreatedKeyStateRoute(
        IndexDirectorySlotSnapshot slot,
        CatalogIndexMetadata metadata,
        KeyStateRoute route,
        long routeOffset)
    {
        CatalogIndexMetadata updatedMetadata = route switch
        {
            KeyStateRoute.Null => metadata with { NullKeyRouteOffset = routeOffset },
            KeyStateRoute.Empty => metadata with { EmptyKeyRouteOffset = routeOffset },
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

    private byte[] ReadKeyStateRouteBytes(long routeOffset)
    {
        if (routeOffset <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(routeOffset), routeOffset, "The key-state route offset must be positive.");
        }

        byte[] routeBytes = new byte[KeyStateIdentityRouteLayout.ExtentSize];
        kernel.Read(routeOffset, routeBytes);
        return routeBytes;
    }
}
