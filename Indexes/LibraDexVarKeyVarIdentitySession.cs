using LibraDex.Layouts;
using LibraDex.Views;
using System.Buffers;
using System.Buffers.Binary;

namespace LibraDex;

public sealed partial class LibraDexFileSession
{
    /// <summary>
    /// Classifies the structure stored at a route target offset for the first `VV` varlen-key/varlen-identity shape.<br/>
    /// The classifier reads only the persisted magic value and caches the result for the session so repeated routed walks do not re-read target headers.<br/>
    /// </summary>
    /// <param name="targetOffset">The route target file offset to classify.</param>
    /// <returns>The classified `VV` route target kind.</returns>
    /// <exception cref="InvalidDataException">Thrown when the target offset does not contain a recognized router or `VV` shelf magic value.</exception>
    internal VarKeyVarIdentityRouteTargetKind ClassifyVarKeyVarIdentityRouteTarget(long targetOffset)
    {
        if (targetOffset == 0)
        {
            return VarKeyVarIdentityRouteTargetKind.None;
        }

        if (varKeyVarIdentityRouteTargetKindCache.TryGet(targetOffset, out int cachedKind))
        {
            return (VarKeyVarIdentityRouteTargetKind)cachedKind;
        }

        Span<byte> magicBytes = stackalloc byte[sizeof(uint)];
        kernel.Read(targetOffset, magicBytes);
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(magicBytes);
        if (magic == RouterLayout.Magic)
        {
            varKeyVarIdentityRouteTargetKindCache.Set(targetOffset, (int)VarKeyVarIdentityRouteTargetKind.Router);
            return VarKeyVarIdentityRouteTargetKind.Router;
        }

        if (magic == VarKeyVarIdentityLayout.Magic)
        {
            varKeyVarIdentityRouteTargetKindCache.Set(targetOffset, (int)VarKeyVarIdentityRouteTargetKind.Shelf);
            return VarKeyVarIdentityRouteTargetKind.Shelf;
        }

        throw new InvalidDataException($"VV route target at offset {targetOffset} does not contain a recognized LibraDex structure.");
    }

    /// <summary>
    /// Creates an empty `VV` shelf and links one root-router prefix to it.<br/>
    /// The first routed `VV` slice mirrors `VS8` by creating one direct root route per requested prefix and letting later inserts grow that shelf before split policy is introduced.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The root router offset to update.</param>
    /// <param name="prefixByte">The root prefix byte that should point at the new shelf.</param>
    /// <param name="profile">The starting `VV` shelf profile.</param>
    /// <returns>The created shelf offset and commit telemetry.</returns>
    /// <exception cref="InvalidDataException">Thrown when the root router is invalid or not direct-index capable.</exception>
    internal (long ShelfOffset, DataKernelCommitTelemetry Commit) CreateVarKeyVarIdentityShelfAndLinkRootRoute(
        long rootRouterOffset,
        byte prefixByte,
        VarKeyVarIdentityProfile profile)
    {
        byte[] existingRouter = new byte[RouterLayout.Size];
        kernel.Read(rootRouterOffset, existingRouter);
        RouterReader existingReader = new(existingRouter);
        if (!existingReader.IsValid || !existingReader.HasDirectIndex)
        {
            throw new InvalidDataException("The root router is invalid or not direct-index capable.");
        }

        byte[] shelfBytes = VarKeyVarIdentity.CreateEmpty(profile);
        RawDataReservation shelfReservation = kernel.Reserve(profile.ShelfExtentSize);
        shelfBytes.CopyTo(shelfReservation.Span);

        RawDataReservation routerReservation = kernel.ReserveAt(rootRouterOffset, RouterLayout.Size);
        existingRouter.CopyTo(routerReservation.Span);
        RouterWriter writer = new(routerReservation.Span);
        writer.WriteRoute(prefixByte, prefixByte, prefixByte, shelfReservation.Extent.Offset);

        DataKernelCommitTelemetry telemetry = CommitAndInvalidateRouterReadCache();
        return (shelfReservation.Extent.Offset, telemetry);
    }

    /// <summary>
    /// Creates a prebuilt `VV` shelf and links one root-router prefix to it.<br/>
    /// This supports bulk construction where sorted varlen key and identity rows are compacted into their final shelf image before touching the DataKernel.<br/>
    /// The supplied bytes are validated before publication so callers cannot link arbitrary payloads into a routed `VV` index.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The root router offset to update.</param>
    /// <param name="prefixByte">The root prefix byte that should point at the new shelf.</param>
    /// <param name="profile">The `VV` shelf profile used to validate and reserve the shelf extent.</param>
    /// <param name="shelfBytes">The complete prebuilt shelf image.</param>
    /// <returns>The created shelf offset and commit telemetry.</returns>
    /// <exception cref="ArgumentException">Thrown when the supplied shelf image length does not match the profile.</exception>
    /// <exception cref="InvalidDataException">Thrown when the root router is invalid, not direct-index capable, or the shelf bytes are invalid.</exception>
    internal (long ShelfOffset, DataKernelCommitTelemetry Commit) CreateVarKeyVarIdentityShelfAndLinkRootRoute(
        long rootRouterOffset,
        byte prefixByte,
        VarKeyVarIdentityProfile profile,
        ReadOnlySpan<byte> shelfBytes)
    {
        if (shelfBytes.Length != profile.ShelfExtentSize)
        {
            throw new ArgumentException("The prebuilt VV shelf image must match the profiled shelf extent size.", nameof(shelfBytes));
        }

        if (!VarKeyVarIdentityReadOnly.TryDecodeSlots(shelfBytes, profile, out _, out _))
        {
            throw new InvalidDataException("The prebuilt VV shelf image is invalid.");
        }

        byte[] existingRouter = new byte[RouterLayout.Size];
        kernel.Read(rootRouterOffset, existingRouter);
        RouterReader existingReader = new(existingRouter);
        if (!existingReader.IsValid || !existingReader.HasDirectIndex)
        {
            throw new InvalidDataException("The root router is invalid or not direct-index capable.");
        }

        RawDataReservation shelfReservation = kernel.Reserve(profile.ShelfExtentSize);
        shelfBytes.CopyTo(shelfReservation.Span);

        RawDataReservation routerReservation = kernel.ReserveAt(rootRouterOffset, RouterLayout.Size);
        existingRouter.CopyTo(routerReservation.Span);
        RouterWriter writer = new(routerReservation.Span);
        writer.WriteRoute(prefixByte, prefixByte, prefixByte, shelfReservation.Extent.Offset);

        DataKernelCommitTelemetry telemetry = CommitAndInvalidateRouterReadCache();
        return (shelfReservation.Extent.Offset, telemetry);
    }

    /// <summary>
    /// Walks routers for a raw `VV` key until the route target is classified as a shelf.<br/>
    /// Raw key bytes drive routing exactly as in `VS8`; missing deeper bytes route as zero so short keys remain deterministic.<br/>
    /// Multi-byte routers are understood for read compatibility, but this first `VV` write path does not yet create them.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The file offset where the root router starts.</param>
    /// <param name="key">The raw byte key whose bytes drive routing.</param>
    /// <param name="maxRouterHops">The maximum number of router pages to follow before treating the route graph as invalid.</param>
    /// <returns>The classified shelf target plus the parent route that selected it.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maxRouterHops"/> is not positive.</exception>
    /// <exception cref="InvalidDataException">Thrown when a route is unset, invalid, or does not terminate at a shelf.</exception>
    internal VarKeyVarIdentityRoutePathTarget WalkVarKeyVarIdentityRoutePathTarget(
        long rootRouterOffset,
        ReadOnlySpan<byte> key,
        int maxRouterHops)
    {
        if (maxRouterHops <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRouterHops), maxRouterHops, "The VV maximum router hop count must be positive.");
        }

        byte[] rentedRouter = ArrayPool<byte>.Shared.Rent(RouterLayout.Size);
        Span<byte> routerSpan = rentedRouter.AsSpan(0, RouterLayout.Size);
        long routerOffset = rootRouterOffset;
        try
        {
            for (int hop = 0; hop < maxRouterHops; hop++)
            {
                if (TryGetDirectRouterView(routerOffset, out DirectRouterView? directView))
                {
                    byte directPrefixByte = GetVarKeyScalar8Prefix(key, directView!.KeyDepth);
                    long directTargetOffset = directView.GetTarget(directPrefixByte);
                    if (directTargetOffset == 0)
                    {
                        throw new InvalidDataException("The routed VV target is unset.");
                    }

                    VarKeyVarIdentityRouteTargetKind directKind = ClassifyVarKeyVarIdentityRouteTarget(directTargetOffset);
                    if (directKind == VarKeyVarIdentityRouteTargetKind.Shelf)
                    {
                        return new VarKeyVarIdentityRoutePathTarget(
                            new VarKeyVarIdentityRouteTarget(directKind, directTargetOffset, directView.KeyDepth, directView.AllocationClassId),
                            routerOffset,
                            directPrefixByte,
                            directPrefixByte);
                    }

                    if (directKind == VarKeyVarIdentityRouteTargetKind.Router)
                    {
                        routerOffset = directTargetOffset;
                        continue;
                    }

                    throw new InvalidDataException("The routed VV target is not a shelf or router.");
                }

                if (TryGetMultiByteRouterView(routerOffset, out MultiByteRouterView? multiByteView))
                {
                    long multiByteTargetOffset = multiByteView!.FindTarget(key, out int multiByteRouteIndex);
                    if (multiByteTargetOffset == 0)
                    {
                        throw new InvalidDataException("The routed VV target is unset.");
                    }

                    VarKeyVarIdentityRouteTargetKind multiByteKind = ClassifyVarKeyVarIdentityRouteTarget(multiByteTargetOffset);
                    byte multiBytePrefixByte = GetVarKeyScalar8Prefix(key, multiByteView.KeyDepth);
                    if (multiByteKind == VarKeyVarIdentityRouteTargetKind.Shelf)
                    {
                        return new VarKeyVarIdentityRoutePathTarget(
                            new VarKeyVarIdentityRouteTarget(multiByteKind, multiByteTargetOffset, multiByteView.KeyDepth, multiByteView.AllocationClassId),
                            routerOffset,
                            multiBytePrefixByte,
                            multiByteRouteIndex);
                    }

                    if (multiByteKind == VarKeyVarIdentityRouteTargetKind.Router)
                    {
                        routerOffset = multiByteTargetOffset;
                        continue;
                    }

                    throw new InvalidDataException("The routed VV target is not a shelf or router.");
                }

                kernel.Read(routerOffset, routerSpan);
                RouterReader reader = new(routerSpan);
                if (!reader.IsValid)
                {
                    throw new InvalidDataException("The routed VV router is invalid.");
                }

                byte prefixByte = GetVarKeyScalar8Prefix(key, reader.KeyDepth);
                long targetOffset = reader.PrefixByteCount == 1
                    ? reader.FindTarget(prefixByte, out int routeIndex)
                    : reader.FindTarget(key, reader.KeyDepth, out routeIndex);
                if (targetOffset == 0)
                {
                    throw new InvalidDataException("The routed VV target is unset.");
                }

                VarKeyVarIdentityRouteTargetKind kind = ClassifyVarKeyVarIdentityRouteTarget(targetOffset);
                if (kind == VarKeyVarIdentityRouteTargetKind.Shelf)
                {
                    return new VarKeyVarIdentityRoutePathTarget(
                        new VarKeyVarIdentityRouteTarget(kind, targetOffset, reader.KeyDepth, reader.AllocationClassId),
                        routerOffset,
                        prefixByte,
                        routeIndex);
                }

                if (kind == VarKeyVarIdentityRouteTargetKind.Router)
                {
                    routerOffset = targetOffset;
                    continue;
                }

                throw new InvalidDataException("The routed VV target is not a shelf or router.");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedRouter, clearArray: false);
        }

        throw new InvalidDataException("The routed VV target walk exceeded the configured maximum router hop count.");
    }

    /// <summary>
    /// Inserts one raw varlen key and raw varlen identity through the routed `VV` shelf path.<br/>
    /// No-split inserts mutate the reached shelf image in place inside a session batch; full shelves may grow to the next extent class and repoint the parent route.<br/>
    /// Full shelves first try the established growth path, then transform the reached shelf offset into a child router with two replacement shelves when growth cannot fit the tuple.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The root router offset for the index.</param>
    /// <param name="maxKeyLength">The maximum raw key length accepted by the profile family.</param>
    /// <param name="maxIdentityLength">The maximum raw identity length accepted by the profile family.</param>
    /// <param name="key">The raw byte key to insert.</param>
    /// <param name="identity">The raw byte identity to insert.</param>
    /// <param name="allowDuplicateKeys">Whether duplicate keys with different identities are allowed.</param>
    /// <param name="maxRouterHops">The maximum number of router pages to follow.</param>
    /// <returns>The storage-facing routed insert result.</returns>
    internal VarKeyVarIdentityRoutedInsertResult InsertWalkedRoutedVarKeyVarIdentity(
        long rootRouterOffset,
        int maxKeyLength,
        int maxIdentityLength,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys,
        int maxRouterHops)
    {
        if (key.Length <= 0 || key.Length > maxKeyLength || identity.Length <= 0 || identity.Length > maxIdentityLength)
        {
            return new VarKeyVarIdentityRoutedInsertResult(
                VarKeyVarIdentityRoutedInsertKind.Invalid,
                VarKeyVarIdentityInsertResult.Invalid,
                0,
                0,
                default,
                0,
                0);
        }

        byte rootPrefix = GetVarKeyScalar8Prefix(key, 0);
        long initialTargetOffset = FindRouterTarget(rootRouterOffset, rootPrefix);
        bool createdInitialShelfRoute = false;
        if (initialTargetOffset == 0)
        {
            VarKeyVarIdentityProfile initialProfile = VarKeyVarIdentityProfile.SelectInitial(currentWriteIntent, maxKeyLength, maxIdentityLength);
            _ = CreateVarKeyVarIdentityShelfAndLinkRootRoute(rootRouterOffset, rootPrefix, initialProfile);
            createdInitialShelfRoute = true;
        }

        VarKeyVarIdentityRoutePathTarget pathTarget = WalkVarKeyVarIdentityRoutePathTarget(rootRouterOffset, key, maxRouterHops);
        VarKeyVarIdentityRouteTarget target = pathTarget.Target;
        if (target.Kind != VarKeyVarIdentityRouteTargetKind.Shelf)
        {
            throw new InvalidDataException("The classified route walker did not terminate at a VV shelf.");
        }

        VarKeyVarIdentityMutableShelf existingShelf = ReadVarKeyVarIdentityMutableShelf(target.Offset, maxKeyLength, maxIdentityLength);
        VarKeyVarIdentityProfile profile = existingShelf.Profile;
        int beforeItemCount = existingShelf.ItemCount;
        VarKeyVarIdentityInsertResult insertResult = existingShelf.InsertWithMutationHint(
            key,
            identity,
            allowDuplicateKeys,
            hintStartDepth: target.RouterDepth,
            maxHintBytes: 0,
            out _);

        if (insertResult == VarKeyVarIdentityInsertResult.Inserted)
        {
            DataKernelCommitTelemetry telemetry = StageVarKeyVarIdentityShelfRewrite(target.Offset, existingShelf);
            return new VarKeyVarIdentityRoutedInsertResult(
                createdInitialShelfRoute ? VarKeyVarIdentityRoutedInsertKind.WalkedCreatedInitialShelf : VarKeyVarIdentityRoutedInsertKind.WalkedNoSplit,
                insertResult,
                target.Offset,
                target.Offset,
                telemetry,
                beforeItemCount + 1,
                profile.ShelfExtentSize,
                target.RouterDepth);
        }

        if (insertResult == VarKeyVarIdentityInsertResult.AlreadyPresent ||
            insertResult == VarKeyVarIdentityInsertResult.KeyConflict)
        {
            return new VarKeyVarIdentityRoutedInsertResult(
                insertResult == VarKeyVarIdentityInsertResult.KeyConflict ? VarKeyVarIdentityRoutedInsertKind.KeyConflict : VarKeyVarIdentityRoutedInsertKind.NoOp,
                insertResult,
                target.Offset,
                0,
                default,
                beforeItemCount,
                profile.ShelfExtentSize,
                target.RouterDepth);
        }

        if (insertResult == VarKeyVarIdentityInsertResult.Full &&
            VarKeyVarIdentity.TryGrowAndInsert(existingShelf.Bytes, profile, key, identity, allowDuplicateKeys, out VarKeyVarIdentityProfile grownProfile, out byte[] grownShelf, out VarKeyVarIdentityInsertResult grownResult))
        {
            if (grownResult != VarKeyVarIdentityInsertResult.Inserted)
            {
                return new VarKeyVarIdentityRoutedInsertResult(
                    grownResult == VarKeyVarIdentityInsertResult.KeyConflict ? VarKeyVarIdentityRoutedInsertKind.KeyConflict : VarKeyVarIdentityRoutedInsertKind.NoOp,
                    grownResult,
                    target.Offset,
                    0,
                    default,
                    beforeItemCount,
                    profile.ShelfExtentSize,
                    target.RouterDepth);
            }

            varKeyVarIdentityMutableBatchShelves.Remove(target.Offset);
            RawDataReservation grownReservation = kernel.Reserve(grownProfile.ShelfExtentSize);
            grownShelf.CopyTo(grownReservation.Span);
            byte[] parentRouterBytes = new byte[RouterLayout.Size];
            kernel.Read(pathTarget.ParentRouterOffset, parentRouterBytes);
            RouterReader parentReader = new(parentRouterBytes);
            if (!parentReader.IsValid)
            {
                throw new InvalidDataException("The VV growth parent router is invalid.");
            }

            RawDataReservation parentRouterRewrite = kernel.ReserveAt(pathTarget.ParentRouterOffset, RouterLayout.Size);
            parentRouterBytes.CopyTo(parentRouterRewrite.Span);
            RouterWriter parentWriter = new(parentRouterRewrite.Span);
            parentWriter.WriteRouteTarget(pathTarget.RouteIndex, grownReservation.Extent.Offset);

            DataKernelCommitTelemetry telemetry = CommitAndInvalidateRouterReadCache();
            existingShelf.Release(clearShelfBytes: true);
            return new VarKeyVarIdentityRoutedInsertResult(
                VarKeyVarIdentityRoutedInsertKind.WalkedGrow,
                grownResult,
                target.Offset,
                grownReservation.Extent.Offset,
                telemetry,
                beforeItemCount + 1,
                grownProfile.ShelfExtentSize,
                target.RouterDepth);
        }

        if (insertResult == VarKeyVarIdentityInsertResult.Full)
        {
            ushort childRouterKeyDepth = checked((ushort)(target.RouterDepth + 1));
            byte rightPrefixByte = GetVarKeyScalar8Prefix(key, childRouterKeyDepth);
            varKeyVarIdentityMutableBatchShelves.Remove(target.Offset);
            (
                long childRouterOffset,
                long leftShelfOffset,
                long rightShelfOffset,
                _,
                _,
                VarKeyVarIdentityInsertResult transformResult,
                DataKernelCommitTelemetry transformCommit) = SplitRoutedVarKeyVarIdentityByShelfTransform(
                target.Offset,
                existingShelf,
                rightPrefixByte,
                profile,
                key,
                identity,
                target.AllocationClassId,
                childRouterKeyDepth);
            existingShelf.Release(clearShelfBytes: true);

            VarKeyVarIdentityRoutedInsertKind kind = transformResult == VarKeyVarIdentityInsertResult.Inserted
                ? VarKeyVarIdentityRoutedInsertKind.WalkedShelfTransformSplit
                : VarKeyVarIdentityRoutedInsertKind.NoOp;
            return new VarKeyVarIdentityRoutedInsertResult(
                kind,
                transformResult,
                childRouterOffset,
                transformResult == VarKeyVarIdentityInsertResult.Inserted ? rightShelfOffset : leftShelfOffset,
                transformCommit,
                beforeItemCount,
                profile.ShelfExtentSize,
                target.RouterDepth,
                childRouterKeyDepth);
        }

        return new VarKeyVarIdentityRoutedInsertResult(
            insertResult == VarKeyVarIdentityInsertResult.Invalid ? VarKeyVarIdentityRoutedInsertKind.Invalid : VarKeyVarIdentityRoutedInsertKind.Full,
            insertResult,
            target.Offset,
            0,
            default,
            beforeItemCount,
            profile.ShelfExtentSize,
            target.RouterDepth);
    }

    internal VarKeyVarIdentityRangeReader OpenVarKeyVarIdentityRangeReader(
        long rootRouterOffset,
        int maxKeyLength,
        int maxIdentityLength,
        ReadOnlySpan<byte> lowerKey,
        ReadOnlySpan<byte> upperKey,
        int maxRouterHops = DefaultVarKeyVarIdentityMaxRouterHops,
        bool decodeLogicalKeys = false)
    {
        if (lowerKey.SequenceCompareTo(upperKey) > 0)
        {
            throw new ArgumentException("The upper VV key must be greater than or equal to the lower key.", nameof(upperKey));
        }

        if (maxRouterHops <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRouterHops), maxRouterHops, "The VV range reader maximum router hop count must be positive.");
        }

        return new VarKeyVarIdentityRangeReader(
            this,
            rootRouterOffset,
            maxKeyLength,
            maxIdentityLength,
            lowerKey,
            upperKey,
            maxRouterHops,
            decodeLogicalKeys);
    }

    /// <summary>
    /// Deletes live `VV` tuples whose raw key falls inside the inclusive key range.<br/>
    /// Router traversal prunes by persisted key prefix and shelf mutation uses tombstones, letting durability batches defer dense slot publication until commit.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The root router offset for the index.</param>
    /// <param name="maxKeyLength">The maximum raw key length accepted by the profile family.</param>
    /// <param name="maxIdentityLength">The maximum raw identity length accepted by the profile family.</param>
    /// <param name="lowerKey">The inclusive lower raw key.</param>
    /// <param name="upperKey">The inclusive upper raw key.</param>
    /// <param name="maxRouterHops">The maximum number of router pages to follow.</param>
    /// <returns>The number of live tuples deleted.</returns>
    internal long DeleteVarKeyVarIdentityKeyRange(
        long rootRouterOffset,
        int maxKeyLength,
        int maxIdentityLength,
        ReadOnlySpan<byte> lowerKey,
        ReadOnlySpan<byte> upperKey,
        int maxRouterHops = DefaultVarKeyVarIdentityMaxRouterHops)
    {
        if (lowerKey.SequenceCompareTo(upperKey) > 0)
        {
            return 0;
        }

        byte lowerPrefix = GetVarKeyScalar8Prefix(lowerKey, 0);
        byte upperPrefix = GetVarKeyScalar8Prefix(upperKey, 0);
        long deleted = 0;
        using RouteVisitedOffsetSet visitedShelves = RouteVisitedOffsetSet.Rent();
        using RouteVisitedOffsetSet visitedRouters = RouteVisitedOffsetSet.Rent();
        for (int prefix = lowerPrefix; prefix <= upperPrefix; prefix++)
        {
            long targetOffset = FindRouterTarget(rootRouterOffset, (byte)prefix);
            if (targetOffset == 0)
            {
                continue;
            }

            deleted += DeleteVarKeyVarIdentityRangeFromTarget(
                targetOffset,
                maxKeyLength,
                maxIdentityLength,
                lowerKey,
                upperKey,
                visitedShelves,
                visitedRouters,
                maxRouterHops);
        }

        return deleted;
    }

    /// <summary>
    /// Deletes one exact `VV` tuple from the routed varlen-key tree.<br/>
    /// The route walk targets the owning key shelf and the mutable shelf sidecar tombstones only the matching key/identity pair.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The root router offset for the index.</param>
    /// <param name="maxKeyLength">The maximum raw key length accepted by the profile family.</param>
    /// <param name="maxIdentityLength">The maximum raw identity length accepted by the profile family.</param>
    /// <param name="key">The exact raw key bytes.</param>
    /// <param name="identity">The exact raw identity bytes.</param>
    /// <param name="maxRouterHops">The maximum number of router pages to follow.</param>
    /// <returns><see langword="true"/> when a live tuple was deleted.</returns>
    internal bool DeleteVarKeyVarIdentityExactTuple(
        long rootRouterOffset,
        int maxKeyLength,
        int maxIdentityLength,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> identity,
        int maxRouterHops = DefaultVarKeyVarIdentityMaxRouterHops)
    {
        VarKeyVarIdentityRoutePathTarget pathTarget = WalkVarKeyVarIdentityRoutePathTarget(rootRouterOffset, key, maxRouterHops);
        VarKeyVarIdentityRouteTarget target = pathTarget.Target;
        if (target.Kind != VarKeyVarIdentityRouteTargetKind.Shelf)
        {
            throw new InvalidDataException("The classified route walker did not terminate at a VV shelf for exact tuple delete.");
        }

        VarKeyVarIdentityMutableShelf shelf = ReadVarKeyVarIdentityMutableShelf(target.Offset, maxKeyLength, maxIdentityLength);
        if (!shelf.MarkTupleDeleted(key, identity))
        {
            return false;
        }

        if (!durabilityBatchActive)
        {
            _ = shelf.NormalizeDeletedSlotsForPublication();
        }

        _ = StageVarKeyVarIdentityShelfRewrite(target.Offset, shelf);
        return true;
    }

    private long DeleteVarKeyVarIdentityRangeFromTarget(
        long targetOffset,
        int maxKeyLength,
        int maxIdentityLength,
        ReadOnlySpan<byte> lowerKey,
        ReadOnlySpan<byte> upperKey,
        RouteVisitedOffsetSet visitedShelves,
        RouteVisitedOffsetSet visitedRouters,
        int remainingRouterHops)
    {
        VarKeyVarIdentityRouteTargetKind kind = ClassifyVarKeyVarIdentityRouteTarget(targetOffset);
        if (kind == VarKeyVarIdentityRouteTargetKind.Shelf)
        {
            if (!visitedShelves.Add(targetOffset))
            {
                return 0;
            }

            VarKeyVarIdentityMutableShelf shelf = ReadVarKeyVarIdentityMutableShelf(targetOffset, maxKeyLength, maxIdentityLength);
            int deleted = shelf.MarkKeyRangeDeleted(lowerKey, upperKey);
            if (deleted == 0)
            {
                return 0;
            }

            if (!durabilityBatchActive)
            {
                _ = shelf.NormalizeDeletedSlotsForPublication();
            }

            _ = StageVarKeyVarIdentityShelfRewrite(targetOffset, shelf);
            return deleted;
        }

        if (kind != VarKeyVarIdentityRouteTargetKind.Router)
        {
            throw new InvalidDataException("The routed VV delete target is not a shelf or router.");
        }

        if (!visitedRouters.Add(targetOffset))
        {
            return 0;
        }

        if (remainingRouterHops <= 0)
        {
            throw new InvalidDataException("The routed VV range delete exceeded the configured router hop count.");
        }

        long deletedFromChildren = 0;
        byte[] routerBytes = new byte[RouterLayout.Size];
        ReadRouterPageUsingArenaCache(targetOffset, routerBytes);
        RouterReader reader = new(routerBytes);
        if (!reader.IsValid)
        {
            throw new InvalidDataException("The routed VV range delete router is invalid.");
        }

        if (reader.PrefixByteCount > 1)
        {
            for (int routeIndex = 0; routeIndex < reader.RouteCount; routeIndex++)
            {
                long childTargetOffset = reader.GetRouteTargetAt(routeIndex);
                if (childTargetOffset == 0)
                {
                    continue;
                }

                deletedFromChildren += DeleteVarKeyVarIdentityRangeFromTarget(
                    childTargetOffset,
                    maxKeyLength,
                    maxIdentityLength,
                    lowerKey,
                    upperKey,
                    visitedShelves,
                    visitedRouters,
                    remainingRouterHops - 1);
            }

            return deletedFromChildren;
        }

        byte lowerPrefix = GetVarKeyScalar8Prefix(lowerKey, reader.KeyDepth);
        byte upperPrefix = GetVarKeyScalar8Prefix(upperKey, reader.KeyDepth);
        for (int prefix = lowerPrefix; prefix <= upperPrefix; prefix++)
        {
            long childTargetOffset = reader.FindTarget((byte)prefix);
            if (childTargetOffset == 0)
            {
                continue;
            }

            deletedFromChildren += DeleteVarKeyVarIdentityRangeFromTarget(
                childTargetOffset,
                maxKeyLength,
                maxIdentityLength,
                lowerKey,
                upperKey,
                visitedShelves,
                visitedRouters,
                remainingRouterHops - 1);
        }

        return deletedFromChildren;
    }

    /// <summary>
    /// Splits a full max-growth `VV` shelf by transforming that shelf offset into a child router.<br/>
    /// The full shelf plus incoming tuple are merged in persisted key/identity order, a raw-key byte boundary is selected near the median, and two replacement shelves are appended.<br/>
    /// If all rows share the immediate child byte, the router can consume a multi-byte prefix or append a short one-byte chain until a later key byte divides the rows.<br/>
    /// </summary>
    /// <param name="childRouterOffset">The existing shelf offset that will be rewritten as the child router.</param>
    /// <param name="existingShelf">The full existing shelf as a decoded mutable sidecar.</param>
    /// <param name="rightPrefixByte">The incoming key prefix byte at <paramref name="childRouterKeyDepth"/>.</param>
    /// <param name="profile">The current max-growth `VV` shelf profile.</param>
    /// <param name="key">The incoming raw key bytes.</param>
    /// <param name="identity">The incoming raw identity bytes.</param>
    /// <param name="allocationClassId">The router allocation class identifier to preserve.</param>
    /// <param name="childRouterKeyDepth">The key byte depth owned by the router replacing the shelf.</param>
    /// <returns>The child router offset, left/right shelf offsets, left/right item counts, insert result, and commit telemetry.</returns>
    private (
        long ChildRouterOffset,
        long LeftShelfOffset,
        long RightShelfOffset,
        int LeftItemCount,
        int RightItemCount,
        VarKeyVarIdentityInsertResult InsertResult,
        DataKernelCommitTelemetry Commit) SplitRoutedVarKeyVarIdentityByShelfTransform(
        long childRouterOffset,
        VarKeyVarIdentityMutableShelf existingShelf,
        byte rightPrefixByte,
        VarKeyVarIdentityProfile profile,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> identity,
        ushort allocationClassId,
        ushort childRouterKeyDepth)
    {
        if (existingShelf.Bytes.Length < profile.ShelfExtentSize)
        {
            throw new ArgumentException("The existing shelf buffer must match the profiled VV shelf extent size.", nameof(existingShelf));
        }

        if (!TryCreateVarKeyVarIdentitySplitShelves(
            existingShelf,
            childRouterKeyDepth,
            rightPrefixByte,
            profile,
            key,
            identity,
            out ushort splitKeyDepth,
            out byte selectedRightPrefixByte,
            out byte[] selectedPrefixStem,
            out byte[] leftShelfBytes,
            out byte[] rightShelfBytes,
            out int leftItemCount,
            out int rightItemCount,
            out VarKeyVarIdentityInsertResult insertResult))
        {
            return (childRouterOffset, 0, 0, 0, 0, insertResult, default);
        }

        RawDataReservation leftAppend = kernel.Reserve(profile.ShelfExtentSize);
        leftShelfBytes.CopyTo(leftAppend.Span);
        RawDataReservation rightAppend = kernel.Reserve(profile.ShelfExtentSize);
        rightShelfBytes.CopyTo(rightAppend.Span);

        RawDataReservation childRouterRewrite = kernel.ReserveAt(childRouterOffset, RouterLayout.Size);
        RouterWriter childWriter = new(childRouterRewrite.Span);
        VarKeyScalar8TransformRouterPlan routerPlan = ChooseVarKeyVarIdentityTransformRouterPlan(childRouterKeyDepth, splitKeyDepth, selectedPrefixStem);
        if (routerPlan.Kind == VarKeyScalar8TransformRouterKind.ExpandedOneByte)
        {
            childWriter.InitializeExpandedOneByte(
                childRouterKeyDepth,
                allocationClassId,
                CreateUniformScalar8Scalar8RouteTargets(0, leftAppend.Extent.Offset, rightAppend.Extent.Offset, selectedRightPrefixByte));
        }
        else if (routerPlan.Kind == VarKeyScalar8TransformRouterKind.CompressedMultiByte)
        {
            RouterMultiByteRouteSnapshot[] routes =
            [
                new(routerPlan.PrefixStem, 0, checked((byte)(selectedRightPrefixByte - 1)), leftAppend.Extent.Offset),
                new(routerPlan.PrefixStem, selectedRightPrefixByte, byte.MaxValue, rightAppend.Extent.Offset)
            ];
            childWriter.InitializeCompressedMultiByte(
                routerPlan.PrefixByteCount,
                childRouterKeyDepth,
                maxRouteCount: 2,
                allocationClassId,
                routes);
        }
        else
        {
            int appendedRouterCount = splitKeyDepth - childRouterKeyDepth;
            long nextRouterOffset = 0;
            for (int i = appendedRouterCount - 1; i >= 0; i--)
            {
                ushort routerDepth = checked((ushort)(childRouterKeyDepth + i + 1));
                long[] targets = routerDepth == splitKeyDepth
                    ? CreateSplitScalar8Scalar8RouteTargets(leftAppend.Extent.Offset, rightAppend.Extent.Offset, selectedRightPrefixByte)
                    : CreateFilledScalar8Scalar8RouteTargets(nextRouterOffset);
                RawDataReservation appendedRouter = kernel.Reserve(RouterLayout.Size);
                RouterWriter appendedWriter = new(appendedRouter.Span);
                appendedWriter.InitializeExpandedOneByte(routerDepth, allocationClassId, targets);
                nextRouterOffset = appendedRouter.Extent.Offset;
            }

            childWriter.InitializeExpandedOneByte(
                childRouterKeyDepth,
                allocationClassId,
                CreateUniformScalar8Scalar8RouteTargets(nextRouterOffset, leftAppend.Extent.Offset, rightAppend.Extent.Offset, selectedRightPrefixByte));
        }

        int varKeyVarIdentityRouterArenaLength = profile.ShelfExtentSize;
        childWriter.WriteArenaMetadata(
            arenaBaseDelta: 0,
            arenaLength: varKeyVarIdentityRouterArenaLength,
            routerPageSize: checked((ushort)RouterLayout.Size),
            routerPageIndex: 0,
            routerPageCount: checked((ushort)(varKeyVarIdentityRouterArenaLength / RouterLayout.Size)),
            arenaFlags: varKeyVarIdentityRouterArenaLength > ushort.MaxValue + 1 ? RouterLayout.ArenaLengthFromPageCountFlag : 0);

        DataKernelCommitTelemetry telemetry = CommitAndInvalidateRouterReadCache();
        RouterArenaState arena = RegisterRouterArena(childRouterOffset, varKeyVarIdentityRouterArenaLength);
        arena.MarkUsed(0);
        return (childRouterOffset, leftAppend.Extent.Offset, rightAppend.Extent.Offset, leftItemCount, rightItemCount, insertResult, telemetry);
    }

    /// <summary>
    /// Creates two replacement shelves for a full `VV` shelf at a selected raw-key byte boundary.<br/>
    /// Source descriptors point into the existing shelf byte image for both key and identity payloads, with negative offsets representing the incoming tuple.<br/>
    /// Returning false means the key set cannot be separated by any available raw-key byte and therefore needs a later duplicate/overflow strategy.<br/>
    /// </summary>
    /// <param name="existingShelf">The full existing shelf as a decoded mutable sidecar.</param>
    /// <param name="firstKeyDepth">The first raw key byte depth to consider for the replacement router.</param>
    /// <param name="hintRightPrefixByte">The incoming key prefix at <paramref name="firstKeyDepth"/>.</param>
    /// <param name="profile">The `VV` shelf profile used for both replacement shelves.</param>
    /// <param name="key">The incoming raw key bytes.</param>
    /// <param name="identity">The incoming raw identity bytes.</param>
    /// <param name="splitKeyDepth">The raw key byte depth selected for the split boundary.</param>
    /// <param name="selectedRightPrefixByte">The first prefix byte routed to the right replacement shelf.</param>
    /// <param name="selectedPrefixStem">The shared prefix stem between <paramref name="firstKeyDepth"/> and <paramref name="splitKeyDepth"/>.</param>
    /// <param name="leftShelfBytes">The populated left replacement shelf bytes.</param>
    /// <param name="rightShelfBytes">The populated right replacement shelf bytes.</param>
    /// <param name="leftItemCount">The number of tuples in the left replacement shelf.</param>
    /// <param name="rightItemCount">The number of tuples in the right replacement shelf.</param>
    /// <param name="insertResult">The split insert result.</param>
    /// <returns>True when replacement shelves were created; otherwise false.</returns>
    private static bool TryCreateVarKeyVarIdentitySplitShelves(
        VarKeyVarIdentityMutableShelf existingShelf,
        ushort firstKeyDepth,
        byte hintRightPrefixByte,
        VarKeyVarIdentityProfile profile,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> identity,
        out ushort splitKeyDepth,
        out byte selectedRightPrefixByte,
        out byte[] selectedPrefixStem,
        out byte[] leftShelfBytes,
        out byte[] rightShelfBytes,
        out int leftItemCount,
        out int rightItemCount,
        out VarKeyVarIdentityInsertResult insertResult)
    {
        splitKeyDepth = 0;
        selectedRightPrefixByte = 0;
        selectedPrefixStem = [];
        leftShelfBytes = [];
        rightShelfBytes = [];
        leftItemCount = 0;
        rightItemCount = 0;
        insertResult = default;

        int existingCount = existingShelf.ItemCount;
        int totalCount = checked(existingCount + 1);
        int[] rentedKeyOffsets = ArrayPool<int>.Shared.Rent(totalCount);
        int[] rentedKeyLengths = ArrayPool<int>.Shared.Rent(totalCount);
        int[] rentedIdentityOffsets = ArrayPool<int>.Shared.Rent(totalCount);
        int[] rentedIdentityLengths = ArrayPool<int>.Shared.Rent(totalCount);
        try
        {
            Span<int> keyOffsets = rentedKeyOffsets.AsSpan(0, totalCount);
            Span<int> keyLengths = rentedKeyLengths.AsSpan(0, totalCount);
            Span<int> identityOffsets = rentedIdentityOffsets.AsSpan(0, totalCount);
            Span<int> identityLengths = rentedIdentityLengths.AsSpan(0, totalCount);
            bool inserted = false;
            int targetIndex = 0;
            for (int sourceIndex = 0; sourceIndex < existingCount; sourceIndex++)
            {
                ReadOnlySpan<byte> currentKey = existingShelf.ReadKeyAt(sourceIndex);
                ReadOnlySpan<byte> currentIdentity = existingShelf.ReadIdentityAt(sourceIndex);
                if (!inserted && CompareVarKeyVarIdentityTuple(currentKey, currentIdentity, key, identity) > 0)
                {
                    keyOffsets[targetIndex] = -1;
                    keyLengths[targetIndex] = key.Length;
                    identityOffsets[targetIndex] = -1;
                    identityLengths[targetIndex] = identity.Length;
                    targetIndex++;
                    inserted = true;
                }

                if (currentKey.SequenceEqual(key) && VarKeyVarIdentityLayout.IdentityBytesEqual(currentIdentity, identity))
                {
                    insertResult = VarKeyVarIdentityInsertResult.AlreadyPresent;
                    return false;
                }

                existingShelf.ReadKeyLocationAt(sourceIndex, out int currentKeyOffset, out int currentKeyLength);
                existingShelf.ReadIdentityLocationAt(sourceIndex, out int currentIdentityOffset, out int currentIdentityLength);
                keyOffsets[targetIndex] = currentKeyOffset;
                keyLengths[targetIndex] = currentKeyLength;
                identityOffsets[targetIndex] = currentIdentityOffset;
                identityLengths[targetIndex] = currentIdentityLength;
                targetIndex++;
            }

            if (!inserted)
            {
                keyOffsets[targetIndex] = -1;
                keyLengths[targetIndex] = key.Length;
                identityOffsets[targetIndex] = -1;
                identityLengths[targetIndex] = identity.Length;
                targetIndex++;
            }

            if (targetIndex != totalCount)
            {
                throw new InvalidDataException("The VV transform split source map did not cover the complete tuple set.");
            }

            (splitKeyDepth, selectedRightPrefixByte) = ChooseVarKeyVarIdentityTransformSplitPlan(
                existingShelf.Bytes,
                keyOffsets,
                keyLengths,
                key,
                firstKeyDepth,
                hintRightPrefixByte,
                profile.MaxKeyLength);
            selectedPrefixStem = CreateVarKeyVarIdentitySplitPrefixStem(
                ReadVarKeyVarIdentitySplitSourceKey(existingShelf.Bytes, keyOffsets[0], keyLengths[0], key),
                firstKeyDepth,
                splitKeyDepth);
            if (!TryBuildVarKeyVarIdentitySplitShelvesFromSources(
                existingShelf.Bytes,
                keyOffsets,
                keyLengths,
                identityOffsets,
                identityLengths,
                key,
                identity,
                splitKeyDepth,
                selectedRightPrefixByte,
                profile,
                out leftShelfBytes,
                out rightShelfBytes,
                out leftItemCount,
                out rightItemCount))
            {
                return false;
            }

            insertResult = VarKeyVarIdentityInsertResult.Inserted;
            return true;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(rentedKeyOffsets, clearArray: false);
            ArrayPool<int>.Shared.Return(rentedKeyLengths, clearArray: false);
            ArrayPool<int>.Shared.Return(rentedIdentityOffsets, clearArray: false);
            ArrayPool<int>.Shared.Return(rentedIdentityLengths, clearArray: false);
        }
    }

    /// <summary>
    /// Builds two replacement `VV` shelves directly from sorted key and identity payload descriptors.<br/>
    /// The method writes disk-shaped shelf images in one pass and avoids tuple object lists, cloned payload arrays, and a second read-only decode.<br/>
    /// </summary>
    /// <param name="existingShelfBytes">The authoritative byte image of the existing shelf.</param>
    /// <param name="keyOffsets">The sorted key-payload offsets, with negative values representing the incoming tuple.</param>
    /// <param name="keyLengths">The sorted key lengths matching <paramref name="keyOffsets"/>.</param>
    /// <param name="identityOffsets">The sorted identity-payload offsets, with negative values representing the incoming tuple.</param>
    /// <param name="identityLengths">The sorted identity lengths matching <paramref name="identityOffsets"/>.</param>
    /// <param name="incomingKey">The incoming raw key bytes.</param>
    /// <param name="incomingIdentity">The incoming raw identity bytes.</param>
    /// <param name="splitKeyDepth">The raw-key byte depth selected for the split boundary.</param>
    /// <param name="selectedRightPrefixByte">The first prefix byte routed to the right replacement shelf.</param>
    /// <param name="profile">The `VV` shelf profile used by both replacement shelves.</param>
    /// <param name="leftShelfBytes">The populated left replacement shelf bytes.</param>
    /// <param name="rightShelfBytes">The populated right replacement shelf bytes.</param>
    /// <param name="leftItemCount">The number of tuples written to the left replacement shelf.</param>
    /// <param name="rightItemCount">The number of tuples written to the right replacement shelf.</param>
    /// <returns>`true` when both replacement shelves were non-empty and fit the current profile.</returns>
    private static bool TryBuildVarKeyVarIdentitySplitShelvesFromSources(
        byte[] existingShelfBytes,
        ReadOnlySpan<int> keyOffsets,
        ReadOnlySpan<int> keyLengths,
        ReadOnlySpan<int> identityOffsets,
        ReadOnlySpan<int> identityLengths,
        ReadOnlySpan<byte> incomingKey,
        ReadOnlySpan<byte> incomingIdentity,
        ushort splitKeyDepth,
        byte selectedRightPrefixByte,
        VarKeyVarIdentityProfile profile,
        out byte[] leftShelfBytes,
        out byte[] rightShelfBytes,
        out int leftItemCount,
        out int rightItemCount)
    {
        leftShelfBytes = new byte[profile.ShelfExtentSize];
        rightShelfBytes = new byte[profile.ShelfExtentSize];
        VarKeyVarIdentityLayout.Initialize(leftShelfBytes, profile);
        VarKeyVarIdentityLayout.Initialize(rightShelfBytes, profile);

        int leftSlotCursor = VarKeyVarIdentityLayout.HeaderSize;
        int rightSlotCursor = VarKeyVarIdentityLayout.HeaderSize;
        int leftRecordCursor = VarKeyVarIdentityLayout.HeaderSize + VarKeyVarIdentityLayout.ReadSlotCapacityBytes(leftShelfBytes);
        int rightRecordCursor = VarKeyVarIdentityLayout.HeaderSize + VarKeyVarIdentityLayout.ReadSlotCapacityBytes(rightShelfBytes);
        int leftSlotCapacityBytes = VarKeyVarIdentityLayout.ReadSlotCapacityBytes(leftShelfBytes);
        int rightSlotCapacityBytes = VarKeyVarIdentityLayout.ReadSlotCapacityBytes(rightShelfBytes);
        leftItemCount = 0;
        rightItemCount = 0;

        for (int i = 0; i < keyOffsets.Length; i++)
        {
            ReadOnlySpan<byte> currentKey = ReadVarKeyVarIdentitySplitSourceKey(existingShelfBytes, keyOffsets[i], keyLengths[i], incomingKey);
            ReadOnlySpan<byte> currentIdentity = ReadVarKeyVarIdentitySplitSourceIdentity(existingShelfBytes, identityOffsets[i], identityLengths[i], incomingIdentity);
            bool goesRight = GetVarKeyVarIdentitySplitSourcePrefix(existingShelfBytes, keyOffsets[i], keyLengths[i], incomingKey, splitKeyDepth) >= selectedRightPrefixByte;
            byte[] targetBytes = goesRight ? rightShelfBytes : leftShelfBytes;
            int slotCursor = goesRight ? rightSlotCursor : leftSlotCursor;
            int recordCursor = goesRight ? rightRecordCursor : leftRecordCursor;
            int slotCapacityBytes = goesRight ? rightSlotCapacityBytes : leftSlotCapacityBytes;
            if (slotCursor + VarKeyVarIdentityLayout.SlotSize > VarKeyVarIdentityLayout.HeaderSize + slotCapacityBytes)
            {
                return false;
            }

            int recordLength = VarKeyVarIdentityLayout.GetNewRecordLength(currentKey.Length, currentIdentity.Length);
            if (recordCursor + recordLength > profile.ShelfExtentSize)
            {
                return false;
            }

            VarKeyVarIdentityLayout.WriteRecord(targetBytes, recordCursor, currentKey, currentIdentity);
            VarKeyVarIdentityLayout.WriteSlotRecordOffset(targetBytes, slotCursor, recordCursor);
            VarKeyVarIdentityLayout.WriteSlotKeyPrefix(targetBytes, slotCursor, VarKeyVarIdentityLayout.CreateKeyPrefix(currentKey));
            slotCursor += VarKeyVarIdentityLayout.SlotSize;
            recordCursor += recordLength;
            if (goesRight)
            {
                rightSlotCursor = slotCursor;
                rightRecordCursor = recordCursor;
                rightItemCount++;
            }
            else
            {
                leftSlotCursor = slotCursor;
                leftRecordCursor = recordCursor;
                leftItemCount++;
            }
        }

        if (leftItemCount == 0 || rightItemCount == 0)
        {
            return false;
        }

        VarKeyVarIdentityLayout.WriteItemCount(leftShelfBytes, leftItemCount);
        VarKeyVarIdentityLayout.WriteSlotStreamLength(leftShelfBytes, leftSlotCursor - VarKeyVarIdentityLayout.HeaderSize);
        VarKeyVarIdentityLayout.WriteRecordArenaEnd(leftShelfBytes, leftRecordCursor);
        VarKeyVarIdentityLayout.WriteItemCount(rightShelfBytes, rightItemCount);
        VarKeyVarIdentityLayout.WriteSlotStreamLength(rightShelfBytes, rightSlotCursor - VarKeyVarIdentityLayout.HeaderSize);
        VarKeyVarIdentityLayout.WriteRecordArenaEnd(rightShelfBytes, rightRecordCursor);
        return true;
    }

    /// <summary>
    /// Reads a split-planner key payload from either the existing shelf image or the incoming tuple.<br/>
    /// Negative source offsets are the planner sentinel for the incoming tuple, allowing the merge map to avoid copying key bytes during structural split planning.<br/>
    /// </summary>
    /// <param name="existingShelfBytes">The existing shelf byte image containing persisted records.</param>
    /// <param name="keyOffset">The key payload offset in <paramref name="existingShelfBytes"/>, or a negative sentinel for <paramref name="incomingKey"/>.</param>
    /// <param name="keyLength">The key payload length in bytes.</param>
    /// <param name="incomingKey">The incoming raw key bytes.</param>
    /// <returns>The selected raw key span.</returns>
    private static ReadOnlySpan<byte> ReadVarKeyVarIdentitySplitSourceKey(
        byte[] existingShelfBytes,
        int keyOffset,
        int keyLength,
        ReadOnlySpan<byte> incomingKey)
    {
        return keyOffset < 0 ? incomingKey : existingShelfBytes.AsSpan(keyOffset, keyLength);
    }

    /// <summary>
    /// Reads a split-planner identity payload from either the existing shelf image or the incoming tuple.<br/>
    /// Negative source offsets are the planner sentinel for the incoming tuple, keeping split construction allocation-light until the final left/right shelf images are written.<br/>
    /// </summary>
    /// <param name="existingShelfBytes">The existing shelf byte image containing persisted records.</param>
    /// <param name="identityOffset">The identity payload offset in <paramref name="existingShelfBytes"/>, or a negative sentinel for <paramref name="incomingIdentity"/>.</param>
    /// <param name="identityLength">The identity payload length in bytes.</param>
    /// <param name="incomingIdentity">The incoming raw identity bytes.</param>
    /// <returns>The selected raw identity span.</returns>
    private static ReadOnlySpan<byte> ReadVarKeyVarIdentitySplitSourceIdentity(
        byte[] existingShelfBytes,
        int identityOffset,
        int identityLength,
        ReadOnlySpan<byte> incomingIdentity)
    {
        return identityOffset < 0 ? incomingIdentity : existingShelfBytes.AsSpan(identityOffset, identityLength);
    }

    /// <summary>
    /// Reads one routing prefix byte from a split-planner key descriptor at the requested key depth.<br/>
    /// Missing bytes sort as zero, matching the existing variable-key routing convention for shorter keys and keeping split decisions byte-stable across persisted and incoming tuples.<br/>
    /// </summary>
    /// <param name="existingShelfBytes">The existing shelf byte image containing persisted records.</param>
    /// <param name="keyOffset">The key payload offset in <paramref name="existingShelfBytes"/>, or a negative sentinel for <paramref name="incomingKey"/>.</param>
    /// <param name="keyLength">The key payload length in bytes.</param>
    /// <param name="incomingKey">The incoming raw key bytes.</param>
    /// <param name="keyDepth">The raw key byte depth to inspect.</param>
    /// <returns>The prefix byte at <paramref name="keyDepth"/>, or zero when the key is shorter than that depth.</returns>
    private static byte GetVarKeyVarIdentitySplitSourcePrefix(
        byte[] existingShelfBytes,
        int keyOffset,
        int keyLength,
        ReadOnlySpan<byte> incomingKey,
        int keyDepth)
    {
        if (keyDepth < 0)
        {
            return 0;
        }

        if (keyOffset < 0)
        {
            return keyDepth < incomingKey.Length ? incomingKey[keyDepth] : (byte)0;
        }

        return keyDepth < keyLength ? existingShelfBytes[keyOffset + keyDepth] : (byte)0;
    }

    /// <summary>
    /// Creates the shared multi-byte prefix stem between the child-router depth and the selected split depth.<br/>
    /// The stem is copied from a representative sorted key and is used only when the transform can compress repeated one-byte routing into a multi-byte router.<br/>
    /// </summary>
    /// <param name="representativeKey">A key from the split source set that contains the common prefix bytes.</param>
    /// <param name="firstKeyDepth">The first key depth consumed by the replacement router.</param>
    /// <param name="splitKeyDepth">The key depth at which the left/right replacement shelves diverge.</param>
    /// <returns>The copied common prefix stem.</returns>
    private static byte[] CreateVarKeyVarIdentitySplitPrefixStem(ReadOnlySpan<byte> representativeKey, ushort firstKeyDepth, ushort splitKeyDepth)
    {
        int stemLength = splitKeyDepth - firstKeyDepth;
        byte[] stem = new byte[stemLength];
        for (int i = 0; i < stem.Length; i++)
        {
            stem[i] = GetVarKeyScalar8Prefix(representativeKey, firstKeyDepth + i);
        }

        return stem;
    }

    private static VarKeyScalar8TransformRouterPlan ChooseVarKeyVarIdentityTransformRouterPlan(
        ushort childRouterKeyDepth,
        ushort splitKeyDepth,
        ReadOnlySpan<byte> selectedPrefixStem)
    {
        if (splitKeyDepth == childRouterKeyDepth)
        {
            return VarKeyScalar8TransformRouterPlan.ExpandedOneByte;
        }

        int stemLength = splitKeyDepth - childRouterKeyDepth;
        if (stemLength >= VarKeyScalar8MultiByteRouterMinStemBytes &&
            stemLength < VarKeyScalar8MultiByteRouterMaxPrefixBytes &&
            selectedPrefixStem.Length == stemLength)
        {
            return VarKeyScalar8TransformRouterPlan.CompressedMultiByte(
                checked((byte)(stemLength + 1)),
                selectedPrefixStem.ToArray());
        }

        return VarKeyScalar8TransformRouterPlan.ExpandedOneByteChain;
    }

    /// <summary>
    /// Selects the raw-key byte depth and right-side prefix byte for a `VV` transform split.<br/>
    /// The scan starts at the child-router depth and chooses the first depth that can divide the sorted source map, keeping the router as shallow as possible before considering deeper common-prefix bytes.<br/>
    /// </summary>
    /// <param name="existingShelfBytes">The existing shelf byte image containing persisted records.</param>
    /// <param name="keyOffsets">The sorted key-payload offsets, with negative values representing the incoming tuple.</param>
    /// <param name="keyLengths">The sorted key lengths matching <paramref name="keyOffsets"/>.</param>
    /// <param name="incomingKey">The incoming raw key bytes.</param>
    /// <param name="firstKeyDepth">The first key depth eligible for the replacement router.</param>
    /// <param name="hintRightPrefixByte">The incoming key prefix byte at <paramref name="firstKeyDepth"/>.</param>
    /// <param name="maxKeyLength">The maximum raw key length accepted by the profile family.</param>
    /// <returns>The selected split key depth and first right-side prefix byte.</returns>
    private static (ushort KeyDepth, byte RightPrefixByte) ChooseVarKeyVarIdentityTransformSplitPlan(
        byte[] existingShelfBytes,
        ReadOnlySpan<int> keyOffsets,
        ReadOnlySpan<int> keyLengths,
        ReadOnlySpan<byte> incomingKey,
        ushort firstKeyDepth,
        byte hintRightPrefixByte,
        int maxKeyLength)
    {
        if (keyOffsets.Length < 2 || keyOffsets.Length != keyLengths.Length)
        {
            throw new InvalidDataException("The VV transform split boundary requires at least two complete key descriptors.");
        }

        for (ushort keyDepth = firstKeyDepth; keyDepth < maxKeyLength; keyDepth++)
        {
            if (TryChooseVarKeyVarIdentityTransformSplitRightPrefix(existingShelfBytes, keyOffsets, keyLengths, incomingKey, keyDepth, hintRightPrefixByte, out byte rightPrefixByte))
            {
                return (keyDepth, rightPrefixByte);
            }
        }

        throw new InvalidDataException("The VV transform split path requires at least two distinct prefixes in the remaining raw key bytes.");
    }

    /// <summary>
    /// Attempts to choose a balanced right-side prefix byte at one raw-key depth.<br/>
    /// The method checks the median-near boundary first, then scans right and left for the closest usable prefix break before falling back to the incoming-key hint when it still lies inside the observed prefix range.<br/>
    /// </summary>
    /// <param name="existingShelfBytes">The existing shelf byte image containing persisted records.</param>
    /// <param name="keyOffsets">The sorted key-payload offsets, with negative values representing the incoming tuple.</param>
    /// <param name="keyLengths">The sorted key lengths matching <paramref name="keyOffsets"/>.</param>
    /// <param name="incomingKey">The incoming raw key bytes.</param>
    /// <param name="keyDepth">The raw key byte depth being evaluated.</param>
    /// <param name="hintRightPrefixByte">The incoming key prefix byte at the first candidate depth.</param>
    /// <param name="rightPrefixByte">The first prefix byte that should route to the right replacement shelf.</param>
    /// <returns>`true` when a prefix boundary was found at this depth; otherwise `false`.</returns>
    private static bool TryChooseVarKeyVarIdentityTransformSplitRightPrefix(
        byte[] existingShelfBytes,
        ReadOnlySpan<int> keyOffsets,
        ReadOnlySpan<int> keyLengths,
        ReadOnlySpan<byte> incomingKey,
        ushort keyDepth,
        byte hintRightPrefixByte,
        out byte rightPrefixByte)
    {
        int desiredRightStart = keyOffsets.Length / 2;
        for (int i = desiredRightStart; i < keyOffsets.Length; i++)
        {
            byte leftPrefix = GetVarKeyVarIdentitySplitSourcePrefix(existingShelfBytes, keyOffsets[i - 1], keyLengths[i - 1], incomingKey, keyDepth);
            byte rightPrefix = GetVarKeyVarIdentitySplitSourcePrefix(existingShelfBytes, keyOffsets[i], keyLengths[i], incomingKey, keyDepth);
            if (rightPrefix > leftPrefix)
            {
                rightPrefixByte = rightPrefix;
                return true;
            }
        }

        for (int i = desiredRightStart - 1; i > 0; i--)
        {
            byte leftPrefix = GetVarKeyVarIdentitySplitSourcePrefix(existingShelfBytes, keyOffsets[i - 1], keyLengths[i - 1], incomingKey, keyDepth);
            byte rightPrefix = GetVarKeyVarIdentitySplitSourcePrefix(existingShelfBytes, keyOffsets[i], keyLengths[i], incomingKey, keyDepth);
            if (rightPrefix > leftPrefix)
            {
                rightPrefixByte = rightPrefix;
                return true;
            }
        }

        byte firstPrefix = GetVarKeyVarIdentitySplitSourcePrefix(existingShelfBytes, keyOffsets[0], keyLengths[0], incomingKey, keyDepth);
        byte lastPrefix = GetVarKeyVarIdentitySplitSourcePrefix(existingShelfBytes, keyOffsets[^1], keyLengths[^1], incomingKey, keyDepth);
        if (hintRightPrefixByte > firstPrefix && hintRightPrefixByte <= lastPrefix)
        {
            rightPrefixByte = hintRightPrefixByte;
            return true;
        }

        rightPrefixByte = 0;
        return false;
    }

    /// <summary>
    /// Compares two `VV` tuples using raw key bytes first and raw identity bytes only when keys are equal.<br/>
    /// Identity comparison delegates to the widened byte-ordinal comparator so duplicate-key stable ordering stays consistent with shelf binary search and duplicate detection.<br/>
    /// </summary>
    /// <param name="leftKey">The left raw key bytes.</param>
    /// <param name="leftIdentity">The left raw identity bytes.</param>
    /// <param name="rightKey">The right raw key bytes.</param>
    /// <param name="rightIdentity">The right raw identity bytes.</param>
    /// <returns>A negative value when the left tuple sorts before the right tuple, zero when equal, or a positive value when after.</returns>
    private static int CompareVarKeyVarIdentityTuple(
        ReadOnlySpan<byte> leftKey,
        ReadOnlySpan<byte> leftIdentity,
        ReadOnlySpan<byte> rightKey,
        ReadOnlySpan<byte> rightIdentity)
    {
        int keyComparison = leftKey.SequenceCompareTo(rightKey);
        return keyComparison != 0
            ? keyComparison
            : VarKeyVarIdentityLayout.CompareIdentityBytes(leftIdentity, rightIdentity);
    }

    private byte[] ReadVarKeyVarIdentityShelfBytes(long shelfOffset, int maxKeyLength, int maxIdentityLength, out VarKeyVarIdentityProfile profile)
    {
        if (durabilityBatchActive &&
            varKeyVarIdentityMutableBatchShelves.TryGetValue(shelfOffset, out VarKeyVarIdentityMutableShelf? mutableShelf))
        {
            profile = mutableShelf.Profile;
            return mutableShelf.Bytes;
        }

        if (!durabilityBatchActive &&
            varKeyVarIdentityReadShelfCache.TryGetValue(shelfOffset, out byte[]? cachedShelfBytes))
        {
            int cachedShelfExtentSize = VarKeyVarIdentityLayout.ReadShelfExtentSize(cachedShelfBytes);
            profile = VarKeyVarIdentityProfile.Create(cachedShelfExtentSize, maxKeyLength, maxIdentityLength);
            return cachedShelfBytes;
        }

        byte[] header = new byte[VarKeyVarIdentityLayout.HeaderSize];
        kernel.Read(shelfOffset, header);
        if (VarKeyVarIdentityLayout.ReadMagic(header) != VarKeyVarIdentityLayout.Magic ||
            VarKeyVarIdentityLayout.ReadFormatVersion(header) != VarKeyVarIdentityLayout.FormatVersion ||
            VarKeyVarIdentityLayout.ReadHeaderSize(header) != VarKeyVarIdentityLayout.HeaderSize)
        {
            throw new InvalidDataException("The routed VV shelf header is invalid.");
        }

        int shelfExtentSize = VarKeyVarIdentityLayout.ReadShelfExtentSize(header);
        profile = VarKeyVarIdentityProfile.Create(shelfExtentSize, maxKeyLength, maxIdentityLength);
        byte[] shelfBytes = new byte[shelfExtentSize];
        header.CopyTo(shelfBytes.AsSpan(0, header.Length));
        kernel.Read(shelfOffset, shelfBytes);
        if (!durabilityBatchActive)
        {
            varKeyVarIdentityReadShelfCache[shelfOffset] = shelfBytes;
        }

        return shelfBytes;
    }

    /// <summary>
    /// Reads one `VV` shelf as a reusable read-only decoded view for non-mutating range scans.<br/>
    /// The view cache avoids rebuilding slot offset and key-prefix sidecars on every repeated range iteration while staying tied to the session cache invalidation boundary.<br/>
    /// Active durability batches bypass this cache because dirty mutable shelf images must remain authoritative until the batch publishes or aborts.<br/>
    /// </summary>
    /// <param name="shelfOffset">The file offset of the shelf to read.</param>
    /// <param name="maxKeyLength">The maximum raw key length accepted by the profile family.</param>
    /// <param name="maxIdentityLength">The maximum raw identity length accepted by the profile family.</param>
    /// <returns>The decoded read-only `VV` shelf view.</returns>
    /// <exception cref="InvalidDataException">Thrown when the loaded shelf bytes fail `VV` validation.</exception>
    internal VarKeyVarIdentityReadOnly ReadVarKeyVarIdentityReadOnlyShelf(long shelfOffset, int maxKeyLength, int maxIdentityLength)
    {
        if (!durabilityBatchActive &&
            varKeyVarIdentityReadOnlyShelfCache.TryGetValue(shelfOffset, out VarKeyVarIdentityReadOnly? cachedShelf))
        {
            return cachedShelf;
        }

        byte[] shelfBytes = ReadVarKeyVarIdentityShelfBytes(shelfOffset, maxKeyLength, maxIdentityLength, out VarKeyVarIdentityProfile profile);
        VarKeyVarIdentityReadOnly shelf = new(shelfBytes, profile);
        if (!shelf.IsValid)
        {
            throw new InvalidDataException("The routed VV shelf bytes are invalid.");
        }

        if (!durabilityBatchActive)
        {
            varKeyVarIdentityReadOnlyShelfCache[shelfOffset] = shelf;
        }

        return shelf;
    }

    private VarKeyVarIdentityMutableShelf ReadVarKeyVarIdentityMutableShelf(long shelfOffset, int maxKeyLength, int maxIdentityLength)
    {
        if (durabilityBatchActive &&
            varKeyVarIdentityMutableBatchShelves.TryGetValue(shelfOffset, out VarKeyVarIdentityMutableShelf? mutableShelf))
        {
            return mutableShelf;
        }

        byte[] shelfBytes;
        VarKeyVarIdentityProfile profile;
        bool ownsBytes = false;
        bool rentSidecars = durabilityBatchActive;
        if (durabilityBatchActive)
        {
            Span<byte> header = stackalloc byte[VarKeyVarIdentityLayout.HeaderSize];
            kernel.Read(shelfOffset, header);
            if (VarKeyVarIdentityLayout.ReadMagic(header) != VarKeyVarIdentityLayout.Magic ||
                VarKeyVarIdentityLayout.ReadFormatVersion(header) != VarKeyVarIdentityLayout.FormatVersion ||
                VarKeyVarIdentityLayout.ReadHeaderSize(header) != VarKeyVarIdentityLayout.HeaderSize)
            {
                throw new InvalidDataException("The routed VV shelf header is invalid.");
            }

            int shelfExtentSize = VarKeyVarIdentityLayout.ReadShelfExtentSize(header);
            profile = VarKeyVarIdentityProfile.Create(shelfExtentSize, maxKeyLength, maxIdentityLength);
            shelfBytes = ArrayPool<byte>.Shared.Rent(shelfExtentSize);
            ownsBytes = true;
            kernel.Read(shelfOffset, shelfBytes.AsSpan(0, shelfExtentSize));
        }
        else
        {
            shelfBytes = ReadVarKeyVarIdentityShelfBytes(shelfOffset, maxKeyLength, maxIdentityLength, out profile);
        }

        if (!VarKeyVarIdentityMutableShelf.TryCreate(shelfBytes, profile, ownsBytes, rentSidecars, out VarKeyVarIdentityMutableShelf shelf))
        {
            throw new InvalidDataException("The routed VV mutable shelf bytes are invalid.");
        }

        if (durabilityBatchActive)
        {
            varKeyVarIdentityMutableBatchShelves[shelfOffset] = shelf;
        }

        return shelf;
    }

    private DataKernelCommitTelemetry StageVarKeyVarIdentityShelfRewrite(
        long shelfOffset,
        VarKeyVarIdentityMutableShelf shelf)
    {
        if (shelf.Bytes.Length < shelf.Profile.ShelfExtentSize)
        {
            throw new ArgumentException("The VV shelf rewrite image must match the profiled shelf extent size.", nameof(shelf));
        }

        if (durabilityBatchActive)
        {
            varKeyVarIdentityMutableBatchShelves[shelfOffset] = shelf;
            return CommitWithoutInvalidatingRouterReadCache();
        }

        RawDataReservation shelfRewrite = kernel.ReserveAt(shelfOffset, shelf.Profile.ShelfExtentSize);
        shelf.Bytes.AsSpan(0, shelf.Profile.ShelfExtentSize).CopyTo(shelfRewrite.Span);
        varKeyVarIdentityReadShelfCache.Remove(shelfOffset);
        varKeyVarIdentityReadOnlyShelfCache.Remove(shelfOffset);
        return CommitAndInvalidateRouterReadCache();
    }
}
