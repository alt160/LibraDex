using System.Buffers.Binary;
using LibraDex.Layouts;
using LibraDex.Views;

namespace LibraDex;

internal enum KeyStateRoute : byte
{
    Null = 1,
    Empty = 2
}

internal sealed partial class LibraDexFileSession
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
        lock (GetKeyStateRouteMutationSync(slotIndex, route))
        {
            lock (writePublicationSync)
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
                KeyStateIdentityRouteReadOnly readOnlyRoute = identityRoute.AsReadOnly();
                if (readOnlyRoute.IsScalar8TerminalIdentityRootValid)
                {
                    return InsertScalar8PromotedKeyStateIdentity(readOnlyRoute.ChildRootOffset, route, encodedIdentity);
                }

                if (!readOnlyRoute.IsScalar8InlineValid)
                {
                    throw new InvalidDataException("The key-state identity route is not a valid scalar-8 route.");
                }

                if (readOnlyRoute.ItemCount >= KeyStateIdentityRouteLayout.MaxScalar8InlineItemCount)
                {
                    if (readOnlyRoute.ContainsScalar8(encodedIdentity))
                    {
                        return false;
                    }

                    PromoteScalar8KeyStateIdentityRoute(routeOffset, routeBytes, route, encodedIdentity);
                    return true;
                }

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
        }
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
        lock (GetKeyStateRouteMutationSync(slotIndex, route))
        {
            lock (writePublicationSync)
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
                KeyStateIdentityRouteReadOnly readOnlyRoute = identityRoute.AsReadOnly();
                if (readOnlyRoute.IsScalar16TerminalIdentityRootValid)
                {
                    return InsertScalar16PromotedKeyStateIdentity(readOnlyRoute.ChildRootOffset, route, encodedIdentityHigh, encodedIdentityLow);
                }

                if (!readOnlyRoute.IsScalar16InlineValid)
                {
                    throw new InvalidDataException("The key-state identity route is not a valid scalar-16 route.");
                }

                if (readOnlyRoute.ItemCount >= KeyStateIdentityRouteLayout.MaxScalar16InlineItemCount)
                {
                    if (readOnlyRoute.ContainsScalar16(encodedIdentityHigh, encodedIdentityLow))
                    {
                        return false;
                    }

                    PromoteScalar16KeyStateIdentityRoute(routeOffset, routeBytes, route, encodedIdentityHigh, encodedIdentityLow);
                    return true;
                }

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
        }
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
        lock (GetKeyStateRouteMutationSync(slotIndex, route))
        {
            lock (writePublicationSync)
            {
                long routeOffset = GetKeyStateRouteOffset(slotIndex, route, out _, out _);
                if (routeOffset <= 0)
                {
                    return false;
                }

                byte[] routeBytes = ReadKeyStateRouteBytes(routeOffset);
                KeyStateIdentityRoute identityRoute = new(routeBytes);
                KeyStateIdentityRouteReadOnly readOnlyRoute = identityRoute.AsReadOnly();
                if (readOnlyRoute.IsScalar8TerminalIdentityRootValid)
                {
                    return DeleteScalar8PromotedKeyStateIdentity(routeOffset, routeBytes, readOnlyRoute.ChildRootOffset, route, encodedIdentity);
                }

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
        }
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
        lock (GetKeyStateRouteMutationSync(slotIndex, route))
        {
            lock (writePublicationSync)
            {
                long routeOffset = GetKeyStateRouteOffset(slotIndex, route, out _, out _);
                if (routeOffset <= 0)
                {
                    return false;
                }

                byte[] routeBytes = ReadKeyStateRouteBytes(routeOffset);
                KeyStateIdentityRoute identityRoute = new(routeBytes);
                KeyStateIdentityRouteReadOnly readOnlyRoute = identityRoute.AsReadOnly();
                if (readOnlyRoute.IsScalar16TerminalIdentityRootValid)
                {
                    return DeleteScalar16PromotedKeyStateIdentity(routeOffset, routeBytes, readOnlyRoute.ChildRootOffset, route, encodedIdentityHigh, encodedIdentityLow);
                }

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
        }
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
        if (identityRoute.IsScalar8TerminalIdentityRootValid)
        {
            return ContainsScalar8PromotedKeyStateIdentity(identityRoute.ChildRootOffset, route, encodedIdentity);
        }

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
        if (identityRoute.IsScalar16TerminalIdentityRootValid)
        {
            return ContainsScalar16PromotedKeyStateIdentity(identityRoute.ChildRootOffset, route, encodedIdentityHigh, encodedIdentityLow);
        }

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
        if (identityRoute.IsScalar8TerminalIdentityRootValid)
        {
            List<ulong> promotedIdentities = ReadTerminalIdentity8RouteIdentities(
                identityRoute.ChildRootOffset,
                CreateKeyStateTerminalIdentityKeyBytes(route),
                KeyStateIdentityRouteLayout.ExtentSize);
            return promotedIdentities.ToArray();
        }

        ulong[] identities = new ulong[identityRoute.ItemCount];
        identityRoute.CopyScalar8Identities(identities);
        return identities;
    }

    /// <summary>
    /// Counts scalar-8 identities from a null-or-empty key-state identity route without copying identities.<br/>
    /// The count is read from the existing route root metadata, which is updated only when that route root is already being written for insert/delete/promotion.<br/>
    /// </summary>
    /// <param name="slotIndex">The fixed directory slot that owns the index.<br/></param>
    /// <param name="route">The key-state route to count.<br/></param>
    /// <returns>The encoded scalar-8 identity count for the route, or zero when the route is absent.<br/></returns>
    internal long CountScalar8KeyStateIdentities(int slotIndex, KeyStateRoute route)
    {
        long routeOffset = GetKeyStateRouteOffset(slotIndex, route, out _, out _);
        if (routeOffset <= 0)
        {
            return 0;
        }

        byte[] routeBytes = ReadKeyStateRouteBytes(routeOffset);
        KeyStateIdentityRouteReadOnly identityRoute = new(routeBytes);
        if (identityRoute.IsScalar8InlineValid || identityRoute.IsScalar8TerminalIdentityRootValid)
        {
            return identityRoute.ItemCount;
        }

        throw new InvalidDataException("The key-state identity route is not a valid scalar-8 route.");
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
        if (identityRoute.IsScalar16TerminalIdentityRootValid)
        {
            return ReadScalar16PromotedKeyStateIdentities(identityRoute.ChildRootOffset, route);
        }

        ulong[] highs = new ulong[identityRoute.ItemCount];
        ulong[] lows = new ulong[identityRoute.ItemCount];
        identityRoute.CopyScalar16Identities(highs, lows);
        return (highs, lows);
    }

    /// <summary>
    /// Counts scalar-16 identities from a null-or-empty key-state identity route without copying identity lanes.<br/>
    /// The count is read from the existing route root metadata, which is updated only when that route root is already being written for insert/delete/promotion.<br/>
    /// </summary>
    /// <param name="slotIndex">The fixed directory slot that owns the index.<br/></param>
    /// <param name="route">The key-state route to count.<br/></param>
    /// <returns>The encoded scalar-16 identity count for the route, or zero when the route is absent.<br/></returns>
    internal long CountScalar16KeyStateIdentities(int slotIndex, KeyStateRoute route)
    {
        long routeOffset = GetKeyStateRouteOffset(slotIndex, route, out _, out _);
        if (routeOffset <= 0)
        {
            return 0;
        }

        byte[] routeBytes = ReadKeyStateRouteBytes(routeOffset);
        KeyStateIdentityRouteReadOnly identityRoute = new(routeBytes);
        if (identityRoute.IsScalar16InlineValid || identityRoute.IsScalar16TerminalIdentityRootValid)
        {
            return identityRoute.ItemCount;
        }

        throw new InvalidDataException("The key-state identity route is not a valid scalar-16 route.");
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

    private object GetKeyStateRouteMutationSync(int slotIndex, KeyStateRoute route)
    {
        KeyStateRouteMutationKey key = new(slotIndex, route);
        lock (keyStateRouteMutationGateSync)
        {
            if (!keyStateRouteMutationSyncByRoute.TryGetValue(key, out object? sync))
            {
                sync = new object();
                keyStateRouteMutationSyncByRoute.Add(key, sync);
            }

            return sync;
        }
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

    private void PromoteScalar8KeyStateIdentityRoute(
        long routeOffset,
        byte[] routeBytes,
        KeyStateRoute route,
        ulong encodedIdentity)
    {
        KeyStateIdentityRouteReadOnly inlineRoute = new(routeBytes);
        ulong[] identities = new ulong[inlineRoute.ItemCount + 1];
        inlineRoute.CopyScalar8Identities(identities);
        identities[^1] = encodedIdentity;
        Array.Sort(identities);

        RawDataReservation childRootReservation = kernel.Reserve(TerminalIdentityRootLayout.Size);
        byte[] keyBytes = CreateKeyStateTerminalIdentityKeyBytes(route);
        RewriteTerminalIdentity8Route(
            childRootReservation.Extent.Offset,
            TerminalIdentityRootLayout.ShapeScalar8,
            keyBytes,
            KeyStateIdentityRouteLayout.ExtentSize,
            identities);

        KeyStateIdentityRoute promotedRoute = new(routeBytes);
        promotedRoute.InitializeScalar8TerminalIdentityRoot(childRootReservation.Extent.Offset, identities.Length);
        RawDataReservation routeRewrite = kernel.ReserveAt(routeOffset, KeyStateIdentityRouteLayout.ExtentSize);
        routeBytes.CopyTo(routeRewrite.Span);
        CommitAndInvalidateRouterReadCache();
    }

    private bool InsertScalar8PromotedKeyStateIdentity(long childRootOffset, KeyStateRoute route, ulong encodedIdentity)
    {
        byte[] keyBytes = CreateKeyStateTerminalIdentityKeyBytes(route);
        if (TryAppendTerminalIdentity8Tail(
            childRootOffset,
            keyBytes,
            KeyStateIdentityRouteLayout.ExtentSize,
            encodedIdentity,
            allowDuplicateKeys: true,
            out _))
        {
            return true;
        }

        long obsoleteFirstShelfOffset = ReadTerminalIdentityFirstShelfOffset(childRootOffset, keyBytes);
        List<ulong> identities = ReadTerminalIdentity8RouteIdentities(childRootOffset, keyBytes, KeyStateIdentityRouteLayout.ExtentSize);
        int index = identities.BinarySearch(encodedIdentity);
        if (index >= 0)
        {
            return false;
        }

        identities.Insert(~index, encodedIdentity);
        RewriteTerminalIdentity8Route(
            childRootOffset,
            TerminalIdentityRootLayout.ShapeScalar8,
            keyBytes,
            KeyStateIdentityRouteLayout.ExtentSize,
            identities,
            obsoleteFirstShelfOffset);
        return true;
    }

    private bool DeleteScalar8PromotedKeyStateIdentity(
        long routeOffset,
        byte[] routeBytes,
        long childRootOffset,
        KeyStateRoute route,
        ulong encodedIdentity)
    {
        byte[] keyBytes = CreateKeyStateTerminalIdentityKeyBytes(route);
        long obsoleteFirstShelfOffset = ReadTerminalIdentityFirstShelfOffset(childRootOffset, keyBytes);
        List<ulong> identities = ReadTerminalIdentity8RouteIdentities(childRootOffset, keyBytes, KeyStateIdentityRouteLayout.ExtentSize);
        int index = identities.BinarySearch(encodedIdentity);
        if (index < 0)
        {
            return false;
        }

        identities.RemoveAt(index);
        RewriteTerminalIdentity8Route(
            childRootOffset,
            TerminalIdentityRootLayout.ShapeScalar8,
            keyBytes,
            KeyStateIdentityRouteLayout.ExtentSize,
            identities,
            obsoleteFirstShelfOffset);

        KeyStateIdentityRoute promotedRoute = new(routeBytes);
        promotedRoute.InitializeScalar8TerminalIdentityRoot(childRootOffset, identities.Count);
        RawDataReservation routeRewrite = kernel.ReserveAt(routeOffset, KeyStateIdentityRouteLayout.ExtentSize);
        routeBytes.CopyTo(routeRewrite.Span);
        CommitAndInvalidateRouterReadCache();
        return true;
    }

    private bool ContainsScalar8PromotedKeyStateIdentity(long childRootOffset, KeyStateRoute route, ulong encodedIdentity)
    {
        List<ulong> identities = ReadTerminalIdentity8RouteIdentities(
            childRootOffset,
            CreateKeyStateTerminalIdentityKeyBytes(route),
            KeyStateIdentityRouteLayout.ExtentSize);
        return identities.BinarySearch(encodedIdentity) >= 0;
    }

    private static byte[] CreateKeyStateTerminalIdentityKeyBytes(KeyStateRoute route)
    {
        return [(byte)'K', (byte)'S', (byte)route];
    }

    private void PromoteScalar16KeyStateIdentityRoute(
        long routeOffset,
        byte[] routeBytes,
        KeyStateRoute route,
        ulong encodedIdentityHigh,
        ulong encodedIdentityLow)
    {
        KeyStateIdentityRouteReadOnly inlineRoute = new(routeBytes);
        using PooledTerminalVarIdentitySet identities = PooledTerminalVarIdentitySet.Rent(
            inlineRoute.ItemCount + 1,
            checked((inlineRoute.ItemCount + 1) * KeyStateIdentityRouteLayout.Scalar16IdentitySize));
        Span<byte> incoming = stackalloc byte[KeyStateIdentityRouteLayout.Scalar16IdentitySize];
        WriteScalar16KeyStateIdentityBytes(incoming, encodedIdentityHigh, encodedIdentityLow);
        Span<byte> current = stackalloc byte[KeyStateIdentityRouteLayout.Scalar16IdentitySize];
        bool insertedIncoming = false;
        for (int i = 0; i < inlineRoute.ItemCount; i++)
        {
            WriteScalar16KeyStateIdentityBytes(
                current,
                KeyStateIdentityRouteLayout.ReadScalar16IdentityHigh(routeBytes, i),
                KeyStateIdentityRouteLayout.ReadScalar16IdentityLow(routeBytes, i));
            int order = Scalar8VarIdentityLayout.CompareIdentityBytes(incoming, current);
            if (!insertedIncoming && order < 0)
            {
                identities.Add(incoming);
                insertedIncoming = true;
            }

            if (order == 0)
            {
                insertedIncoming = true;
            }

            identities.Add(current);
        }

        if (!insertedIncoming)
        {
            identities.Add(incoming);
        }

        byte[] keyBytes = CreateScalar16KeyStateTerminalIdentityKeyBytes(route);
        long childRootOffset = CreateScalar8VarIdentityTerminalRoute(
            TerminalIdentityRootLayout.ShapeScalar16VarIdentity,
            keyBytes,
            KeyStateIdentityRouteLayout.ExtentSize,
            identities);
        KeyStateIdentityRoute promotedRoute = new(routeBytes);
        promotedRoute.InitializeScalar16TerminalIdentityRoot(childRootOffset, identities.Count);
        RawDataReservation routeRewrite = kernel.ReserveAt(routeOffset, KeyStateIdentityRouteLayout.ExtentSize);
        routeBytes.CopyTo(routeRewrite.Span);
        CommitAndInvalidateRouterReadCache();
    }

    private bool InsertScalar16PromotedKeyStateIdentity(long childRootOffset, KeyStateRoute route, ulong encodedIdentityHigh, ulong encodedIdentityLow)
    {
        byte[] keyBytes = CreateScalar16KeyStateTerminalIdentityKeyBytes(route);
        Span<byte> identity = stackalloc byte[KeyStateIdentityRouteLayout.Scalar16IdentitySize];
        WriteScalar16KeyStateIdentityBytes(identity, encodedIdentityHigh, encodedIdentityLow);
        byte[] rootBytes = ReadTerminalIdentityRootBytes(childRootOffset);
        if (TerminalIdentityRootLayout.ReadShape(rootBytes) != TerminalIdentityRootLayout.ShapeScalar16VarIdentity ||
            !IsTerminalIdentityRootForKey(rootBytes, keyBytes, out long firstShelfOffset))
        {
            throw new InvalidDataException("The promoted scalar-16 key-state route root is invalid.");
        }

        long tailShelfOffset = TerminalIdentityRootLayout.ReadTailShelfOffset(rootBytes);
        int shelfExtentSize = TerminalIdentityRootLayout.ReadShelfExtentSize(rootBytes);
        if (TryAppendScalar8VarIdentityTerminalTail(childRootOffset, rootBytes, firstShelfOffset, tailShelfOffset, shelfExtentSize, identity, out _))
        {
            return true;
        }

        using PooledTerminalVarIdentitySet identities = ReadScalar8VarIdentityTerminalIdentitiesPooled(childRootOffset, keyBytes, shelfExtentSize);
        int index = LowerBoundTerminalVarIdentity(identities, identity);
        if (index < identities.Count && Scalar8VarIdentityLayout.IdentityBytesEqual(identities.ReadAt(index), identity))
        {
            return false;
        }

        identities.InsertAt(index, identity);
        RewriteScalar8VarIdentityTerminalRoute(
            childRootOffset,
            TerminalIdentityRootLayout.ShapeScalar16VarIdentity,
            keyBytes,
            shelfExtentSize,
            identities);
        return true;
    }

    private bool DeleteScalar16PromotedKeyStateIdentity(
        long routeOffset,
        byte[] routeBytes,
        long childRootOffset,
        KeyStateRoute route,
        ulong encodedIdentityHigh,
        ulong encodedIdentityLow)
    {
        byte[] keyBytes = CreateScalar16KeyStateTerminalIdentityKeyBytes(route);
        byte[] rootBytes = ReadTerminalIdentityRootBytes(childRootOffset);
        if (TerminalIdentityRootLayout.ReadShape(rootBytes) != TerminalIdentityRootLayout.ShapeScalar16VarIdentity)
        {
            throw new InvalidDataException("The promoted scalar-16 key-state route root has the wrong shape.");
        }

        int shelfExtentSize = TerminalIdentityRootLayout.ReadShelfExtentSize(rootBytes);
        Span<byte> identity = stackalloc byte[KeyStateIdentityRouteLayout.Scalar16IdentitySize];
        WriteScalar16KeyStateIdentityBytes(identity, encodedIdentityHigh, encodedIdentityLow);
        using PooledTerminalVarIdentitySet currentIdentities = ReadScalar8VarIdentityTerminalIdentitiesPooled(childRootOffset, keyBytes, shelfExtentSize);
        int deleteIndex = LowerBoundTerminalVarIdentity(currentIdentities, identity);
        if (deleteIndex >= currentIdentities.Count || !Scalar8VarIdentityLayout.IdentityBytesEqual(currentIdentities.ReadAt(deleteIndex), identity))
        {
            return false;
        }

        using PooledTerminalVarIdentitySet survivors = PooledTerminalVarIdentitySet.Rent(
            Math.Max(1, currentIdentities.Count - 1),
            Math.Max(256, checked((currentIdentities.Count - 1) * KeyStateIdentityRouteLayout.Scalar16IdentitySize)));
        for (int i = 0; i < currentIdentities.Count; i++)
        {
            if (i != deleteIndex)
            {
                survivors.Add(currentIdentities.ReadAt(i));
            }
        }

        RewriteScalar8VarIdentityTerminalRoute(
            childRootOffset,
            TerminalIdentityRootLayout.ShapeScalar16VarIdentity,
            keyBytes,
            shelfExtentSize,
            survivors);
        KeyStateIdentityRoute promotedRoute = new(routeBytes);
        promotedRoute.InitializeScalar16TerminalIdentityRoot(childRootOffset, survivors.Count);
        RawDataReservation routeRewrite = kernel.ReserveAt(routeOffset, KeyStateIdentityRouteLayout.ExtentSize);
        routeBytes.CopyTo(routeRewrite.Span);
        CommitAndInvalidateRouterReadCache();
        return true;
    }

    private bool ContainsScalar16PromotedKeyStateIdentity(long childRootOffset, KeyStateRoute route, ulong encodedIdentityHigh, ulong encodedIdentityLow)
    {
        byte[] keyBytes = CreateScalar16KeyStateTerminalIdentityKeyBytes(route);
        byte[] rootBytes = ReadTerminalIdentityRootBytes(childRootOffset);
        int shelfExtentSize = TerminalIdentityRootLayout.ReadShelfExtentSize(rootBytes);
        Span<byte> identity = stackalloc byte[KeyStateIdentityRouteLayout.Scalar16IdentitySize];
        WriteScalar16KeyStateIdentityBytes(identity, encodedIdentityHigh, encodedIdentityLow);
        using PooledTerminalVarIdentitySet identities = ReadScalar8VarIdentityTerminalIdentitiesPooled(childRootOffset, keyBytes, shelfExtentSize);
        int index = LowerBoundTerminalVarIdentity(identities, identity);
        return index < identities.Count && Scalar8VarIdentityLayout.IdentityBytesEqual(identities.ReadAt(index), identity);
    }

    private (ulong[] Highs, ulong[] Lows) ReadScalar16PromotedKeyStateIdentities(long childRootOffset, KeyStateRoute route)
    {
        byte[] keyBytes = CreateScalar16KeyStateTerminalIdentityKeyBytes(route);
        byte[] rootBytes = ReadTerminalIdentityRootBytes(childRootOffset);
        if (TerminalIdentityRootLayout.ReadShape(rootBytes) != TerminalIdentityRootLayout.ShapeScalar16VarIdentity)
        {
            throw new InvalidDataException("The promoted scalar-16 key-state route root has the wrong shape.");
        }

        int shelfExtentSize = TerminalIdentityRootLayout.ReadShelfExtentSize(rootBytes);
        using PooledTerminalVarIdentitySet identities = ReadScalar8VarIdentityTerminalIdentitiesPooled(childRootOffset, keyBytes, shelfExtentSize);
        ulong[] highs = new ulong[identities.Count];
        ulong[] lows = new ulong[identities.Count];
        for (int i = 0; i < identities.Count; i++)
        {
            ReadOnlySpan<byte> identity = identities.ReadAt(i);
            if (identity.Length != KeyStateIdentityRouteLayout.Scalar16IdentitySize)
            {
                throw new InvalidDataException("The promoted scalar-16 key-state identity has an invalid length.");
            }

            highs[i] = BinaryPrimitives.ReadUInt64BigEndian(identity.Slice(0, sizeof(ulong)));
            lows[i] = BinaryPrimitives.ReadUInt64BigEndian(identity.Slice(sizeof(ulong), sizeof(ulong)));
        }

        return (highs, lows);
    }

    private static byte[] CreateScalar16KeyStateTerminalIdentityKeyBytes(KeyStateRoute route)
    {
        return [(byte)'K', (byte)'S', (byte)route, 16];
    }

    private static void WriteScalar16KeyStateIdentityBytes(Span<byte> target, ulong encodedIdentityHigh, ulong encodedIdentityLow)
    {
        BinaryPrimitives.WriteUInt64BigEndian(target.Slice(0, sizeof(ulong)), encodedIdentityHigh);
        BinaryPrimitives.WriteUInt64BigEndian(target.Slice(sizeof(ulong), sizeof(ulong)), encodedIdentityLow);
    }
}
