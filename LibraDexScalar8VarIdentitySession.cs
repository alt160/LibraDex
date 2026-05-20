using LibraDex.Layouts;
using LibraDex.Views;
using System.Buffers.Binary;
using System.Diagnostics;

namespace LibraDex;

public sealed partial class LibraDexFileSession
{
    private const int DefaultScalar8VarIdentityMaxRouterHops = 8;

    internal Scalar8VarIdentityRouteTargetKind ClassifyScalar8VarIdentityRouteTarget(long targetOffset)
    {
        if (targetOffset <= 0)
        {
            return Scalar8VarIdentityRouteTargetKind.None;
        }

        Span<byte> header = stackalloc byte[Scalar8VarIdentityLayout.HeaderSize];
        kernel.Read(targetOffset, header);
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header[..sizeof(uint)]);
        if (magic == RouterLayout.Magic)
        {
            return Scalar8VarIdentityRouteTargetKind.Router;
        }

        if (magic == Scalar8VarIdentityLayout.Magic)
        {
            return Scalar8VarIdentityRouteTargetKind.Shelf;
        }

        return Scalar8VarIdentityRouteTargetKind.None;
    }

    internal (long ShelfOffset, DataKernelCommitTelemetry Commit) CreateScalar8VarIdentityShelfAndLinkRootRoute(
        long rootRouterOffset,
        byte prefixByte,
        Scalar8VarIdentityProfile profile)
    {
        byte[] existingRouter = new byte[RouterLayout.Size];
        kernel.Read(rootRouterOffset, existingRouter);
        RouterReader existingReader = new(existingRouter);
        if (!existingReader.IsValid || !existingReader.HasDirectIndex)
        {
            throw new InvalidDataException("The root router is invalid or not direct-index capable.");
        }

        byte[] shelfBytes = Scalar8VarIdentity.CreateEmpty(profile);
        RawDataReservation shelfReservation = kernel.Reserve(profile.ShelfExtentSize);
        shelfBytes.CopyTo(shelfReservation.Span);

        RawDataReservation routerReservation = kernel.ReserveAt(rootRouterOffset, RouterLayout.Size);
        existingRouter.CopyTo(routerReservation.Span);
        RouterWriter writer = new(routerReservation.Span);
        writer.WriteRoute(prefixByte, prefixByte, prefixByte, shelfReservation.Extent.Offset);

        DataKernelCommitTelemetry telemetry = CommitAndInvalidateRouterReadCache();
        return (shelfReservation.Extent.Offset, telemetry);
    }

    internal Scalar8VarIdentityRoutePathTarget WalkScalar8VarIdentityRoutePathTarget(
        long rootRouterOffset,
        ulong encodedKey,
        int maxRouterHops)
    {
        if (maxRouterHops <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRouterHops), maxRouterHops, "The SV8 maximum router hop count must be positive.");
        }

        Span<byte> routerSpan = stackalloc byte[RouterLayout.Size];
        long routerOffset = rootRouterOffset;
        for (int hop = 0; hop < maxRouterHops; hop++)
        {
            if (TryGetDirectRouterView(routerOffset, out DirectRouterView? directView))
            {
                byte directPrefixByte = GetScalar8VarIdentityPrefix(encodedKey, directView!.KeyDepth);
                long directTargetOffset = directView.GetTarget(directPrefixByte);
                if (directTargetOffset == 0)
                {
                    throw new InvalidDataException("The routed SV8 target is unset.");
                }

                Scalar8VarIdentityRouteTargetKind directKind = ToScalar8VarIdentityRouteTargetKind(directView.GetTargetKind(directPrefixByte));
                if (directKind == Scalar8VarIdentityRouteTargetKind.None)
                {
                    directKind = ClassifyScalar8VarIdentityRouteTarget(directTargetOffset);
                    directView.SetTargetKind(directPrefixByte, ToScalar8Scalar8RouteTargetKind(directKind));
                }

                if (directKind == Scalar8VarIdentityRouteTargetKind.Shelf)
                {
                    return new Scalar8VarIdentityRoutePathTarget(
                        new Scalar8VarIdentityRouteTarget(directKind, directTargetOffset, directView.KeyDepth, directView.AllocationClassId),
                        routerOffset,
                        directPrefixByte,
                        directPrefixByte);
                }

                if (directKind == Scalar8VarIdentityRouteTargetKind.Router)
                {
                    routerOffset = directTargetOffset;
                    continue;
                }

                throw new InvalidDataException("The routed SV8 target is not a shelf or router.");
            }

            kernel.Read(routerOffset, routerSpan);
            RouterReader reader = new(routerSpan);
            if (!reader.IsValid)
            {
                throw new InvalidDataException("The routed SV8 router is invalid.");
            }

            byte prefixByte = GetScalar8VarIdentityPrefix(encodedKey, reader.KeyDepth);
            long targetOffset = reader.FindTarget(prefixByte, out int routeIndex);
            if (targetOffset == 0)
            {
                throw new InvalidDataException("The routed SV8 target is unset.");
            }

            Scalar8VarIdentityRouteTargetKind kind = ClassifyScalar8VarIdentityRouteTarget(targetOffset);
            if (kind == Scalar8VarIdentityRouteTargetKind.Shelf)
            {
                return new Scalar8VarIdentityRoutePathTarget(
                    new Scalar8VarIdentityRouteTarget(kind, targetOffset, reader.KeyDepth, reader.AllocationClassId),
                    routerOffset,
                    prefixByte,
                    routeIndex);
            }

            if (kind == Scalar8VarIdentityRouteTargetKind.Router)
            {
                routerOffset = targetOffset;
                continue;
            }

            throw new InvalidDataException("The routed SV8 target is not a shelf or router.");
        }

        throw new InvalidDataException("The routed SV8 target walk exceeded the configured maximum router hop count.");
    }

    internal Scalar8VarIdentityRoutedInsertResult InsertWalkedRoutedScalar8VarIdentity(
        long rootRouterOffset,
        int maxIdentityLength,
        ulong encodedKey,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys,
        int maxRouterHops)
    {
        return InsertWalkedRoutedScalar8VarIdentity(
            rootRouterOffset,
            maxIdentityLength,
            encodedKey,
            identity,
            allowDuplicateKeys,
            maxRouterHops,
            collectAttribution: false,
            out _);
    }

    /// <summary>
    /// Inserts one tuple through the walked routed `SV8` path while returning lower-layer elapsed-time attribution.<br/>
    /// The attribution overload is intended for harness diagnostics so normal callers can use the lower-overhead overload above.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The offset of the root router that owns the scalar-key route table.</param>
    /// <param name="maxIdentityLength">The maximum raw identity length accepted by this `SV8` profile family.</param>
    /// <param name="encodedKey">The sortable scalar key bytes projected as an unsigned 64-bit value.</param>
    /// <param name="identity">The raw variable-length identity bytes to insert.</param>
    /// <param name="allowDuplicateKeys">True when duplicate keys are allowed and identity bytes provide stable duplicate ordering.</param>
    /// <param name="maxRouterHops">The maximum router hops allowed before treating the route as malformed.</param>
    /// <param name="attribution">The elapsed-time buckets captured during the walked write.</param>
    /// <returns>The routed insert result including structural mutation kind and commit telemetry.</returns>
    internal Scalar8VarIdentityRoutedInsertResult InsertWalkedRoutedScalar8VarIdentity(
        long rootRouterOffset,
        int maxIdentityLength,
        ulong encodedKey,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys,
        int maxRouterHops,
        out Scalar8VarIdentityWalkedWriteAttribution attribution)
    {
        return InsertWalkedRoutedScalar8VarIdentity(
            rootRouterOffset,
            maxIdentityLength,
            encodedKey,
            identity,
            allowDuplicateKeys,
            maxRouterHops,
            collectAttribution: true,
            out attribution);
    }

    private Scalar8VarIdentityRoutedInsertResult InsertWalkedRoutedScalar8VarIdentity(
        long rootRouterOffset,
        int maxIdentityLength,
        ulong encodedKey,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys,
        int maxRouterHops,
        bool collectAttribution,
        out Scalar8VarIdentityWalkedWriteAttribution attribution)
    {
        long routeWalkTicks = 0;
        long shelfReadTicks = 0;
        long duplicateChainTicks = 0;
        long mutationTicks = 0;
        long stageTicks = 0;
        long structuralTicks = 0;
        long started = collectAttribution ? Stopwatch.GetTimestamp() : 0;
        Scalar8VarIdentityRoutePathTarget pathTarget = WalkScalar8VarIdentityRoutePathTarget(rootRouterOffset, encodedKey, maxRouterHops);
        if (collectAttribution)
        {
            routeWalkTicks += Stopwatch.GetTimestamp() - started;
        }

        Scalar8VarIdentityRouteTarget target = pathTarget.Target;
        if (target.Kind != Scalar8VarIdentityRouteTargetKind.Shelf)
        {
            throw new InvalidDataException("The classified route walker did not terminate at an SV8 shelf.");
        }

        started = collectAttribution ? Stopwatch.GetTimestamp() : 0;
        Scalar8VarIdentityMutableShelfView mutableShelf = ReadScalar8VarIdentityMutableShelf(target.Offset, maxIdentityLength);
        if (collectAttribution)
        {
            shelfReadTicks += Stopwatch.GetTimestamp() - started;
        }

        byte[] shelfBytes = mutableShelf.Bytes;
        Scalar8VarIdentityProfile profile = mutableShelf.Profile;
        int beforeItemCount = mutableShelf.ItemCount;
        started = collectAttribution ? Stopwatch.GetTimestamp() : 0;
        if (Scalar8VarIdentityLayout.ReadNextShelfOffset(mutableShelf.Bytes) != 0 &&
            TryInsertIntoScalar8VarIdentityDuplicateRunChain(
                target.Offset,
                mutableShelf.Bytes,
                profile,
                encodedKey,
                identity,
                allowDuplicateKeys,
                out Scalar8VarIdentityRoutedInsertResult chainResult))
        {
            if (collectAttribution)
            {
                duplicateChainTicks += Stopwatch.GetTimestamp() - started;
            }

            attribution = new Scalar8VarIdentityWalkedWriteAttribution(routeWalkTicks, shelfReadTicks, duplicateChainTicks, mutationTicks, stageTicks, structuralTicks);
            return chainResult;
        }

        if (collectAttribution)
        {
            duplicateChainTicks += Stopwatch.GetTimestamp() - started;
        }

        Scalar8VarIdentityInsertResult insertResult;
        byte[] rewrittenBytes;
        started = collectAttribution ? Stopwatch.GetTimestamp() : 0;
        if (durabilityBatchActive)
        {
            insertResult = mutableShelf.Insert(encodedKey, identity, allowDuplicateKeys);
            rewrittenBytes = mutableShelf.Bytes;
        }
        else
        {
            insertResult = Scalar8VarIdentity.Insert(
                shelfBytes,
                profile,
                encodedKey,
                identity,
                allowDuplicateKeys,
                out rewrittenBytes);
        }
        if (collectAttribution)
        {
            mutationTicks += Stopwatch.GetTimestamp() - started;
        }

        if (insertResult == Scalar8VarIdentityInsertResult.Inserted)
        {
            started = collectAttribution ? Stopwatch.GetTimestamp() : 0;
            DataKernelCommitTelemetry telemetry = StageScalar8VarIdentityShelfRewrite(target.Offset, mutableShelf, rewrittenBytes);
            if (collectAttribution)
            {
                stageTicks += Stopwatch.GetTimestamp() - started;
            }

            attribution = new Scalar8VarIdentityWalkedWriteAttribution(routeWalkTicks, shelfReadTicks, duplicateChainTicks, mutationTicks, stageTicks, structuralTicks);
            return new Scalar8VarIdentityRoutedInsertResult(
                Scalar8VarIdentityRoutedInsertKind.WalkedNoSplit,
                insertResult,
                target.Offset,
                target.Offset,
                telemetry,
                beforeItemCount + 1,
                profile.ShelfExtentSize,
                target.RouterDepth);
        }

        if (insertResult == Scalar8VarIdentityInsertResult.AlreadyPresent ||
            insertResult == Scalar8VarIdentityInsertResult.KeyConflict)
        {
            attribution = new Scalar8VarIdentityWalkedWriteAttribution(routeWalkTicks, shelfReadTicks, duplicateChainTicks, mutationTicks, stageTicks, structuralTicks);
            return new Scalar8VarIdentityRoutedInsertResult(
                insertResult == Scalar8VarIdentityInsertResult.KeyConflict ? Scalar8VarIdentityRoutedInsertKind.KeyConflict : Scalar8VarIdentityRoutedInsertKind.NoOp,
                insertResult,
                target.Offset,
                0,
                default,
                beforeItemCount,
                profile.ShelfExtentSize,
                target.RouterDepth);
        }

        started = collectAttribution ? Stopwatch.GetTimestamp() : 0;
        mutableShelf.EnsureSlotBytesCurrent();
        if (insertResult == Scalar8VarIdentityInsertResult.Full &&
            Scalar8VarIdentity.TryGrowAndInsert(mutableShelf.Bytes, profile, encodedKey, identity, allowDuplicateKeys, out Scalar8VarIdentityProfile grownProfile, out byte[] grownShelf, out Scalar8VarIdentityInsertResult grownResult))
        {
            if (grownResult != Scalar8VarIdentityInsertResult.Inserted)
            {
                if (collectAttribution)
                {
                    structuralTicks += Stopwatch.GetTimestamp() - started;
                }

                attribution = new Scalar8VarIdentityWalkedWriteAttribution(routeWalkTicks, shelfReadTicks, duplicateChainTicks, mutationTicks, stageTicks, structuralTicks);
                return new Scalar8VarIdentityRoutedInsertResult(
                    grownResult == Scalar8VarIdentityInsertResult.KeyConflict ? Scalar8VarIdentityRoutedInsertKind.KeyConflict : Scalar8VarIdentityRoutedInsertKind.NoOp,
                    grownResult,
                    target.Offset,
                    0,
                    default,
                    beforeItemCount,
                    profile.ShelfExtentSize,
                    target.RouterDepth);
            }

            scalar8VarIdentityMutableBatchShelves.Remove(target.Offset);
            RawDataReservation grownReservation = kernel.Reserve(grownProfile.ShelfExtentSize);
            grownShelf.CopyTo(grownReservation.Span);
            RepointRoute(pathTarget.ParentRouterOffset, pathTarget.RouteIndex, grownReservation.Extent.Offset);
            DataKernelCommitTelemetry telemetry = CommitAndInvalidateRouterReadCache();
            if (collectAttribution)
            {
                structuralTicks += Stopwatch.GetTimestamp() - started;
            }

            attribution = new Scalar8VarIdentityWalkedWriteAttribution(routeWalkTicks, shelfReadTicks, duplicateChainTicks, mutationTicks, stageTicks, structuralTicks);
            return new Scalar8VarIdentityRoutedInsertResult(
                Scalar8VarIdentityRoutedInsertKind.WalkedGrow,
                grownResult,
                target.Offset,
                grownReservation.Extent.Offset,
                telemetry,
                beforeItemCount + 1,
                grownProfile.ShelfExtentSize,
                target.RouterDepth);
        }
        if (collectAttribution)
        {
            structuralTicks += Stopwatch.GetTimestamp() - started;
        }

        started = collectAttribution ? Stopwatch.GetTimestamp() : 0;
        if (insertResult == Scalar8VarIdentityInsertResult.Full &&
            TrySplitScalar8VarIdentityShelf(
                mutableShelf,
                profile,
                target.RouterDepth,
                encodedKey,
                identity,
                allowDuplicateKeys,
                out ushort splitDepth,
                out byte selectedRightPrefix,
                out byte[] leftShelf,
                out byte[] rightShelf,
                out int leftCount,
                out int rightCount,
                out Scalar8VarIdentityInsertResult splitInsertResult))
        {
            scalar8VarIdentityMutableBatchShelves.Remove(target.Offset);
            RawDataReservation leftReservation = kernel.Reserve(profile.ShelfExtentSize);
            leftShelf.CopyTo(leftReservation.Span);
            RawDataReservation rightReservation = kernel.Reserve(profile.ShelfExtentSize);
            rightShelf.CopyTo(rightReservation.Span);
            RawDataReservation childRouterReservation = kernel.Reserve(RouterLayout.Size);
            RouterWriter childWriter = new(childRouterReservation.Span);
            childWriter.InitializeExpandedOneByte(
                splitDepth,
                target.AllocationClassId,
                CreateSplitScalar8Scalar8RouteTargets(leftReservation.Extent.Offset, rightReservation.Extent.Offset, selectedRightPrefix));
            RepointRoute(pathTarget.ParentRouterOffset, pathTarget.RouteIndex, childRouterReservation.Extent.Offset);
            DataKernelCommitTelemetry telemetry = CommitAndInvalidateRouterReadCache();
            if (collectAttribution)
            {
                structuralTicks += Stopwatch.GetTimestamp() - started;
            }

            attribution = new Scalar8VarIdentityWalkedWriteAttribution(routeWalkTicks, shelfReadTicks, duplicateChainTicks, mutationTicks, stageTicks, structuralTicks);
            return new Scalar8VarIdentityRoutedInsertResult(
                Scalar8VarIdentityRoutedInsertKind.WalkedShelfSplit,
                splitInsertResult,
                childRouterReservation.Extent.Offset,
                rightReservation.Extent.Offset,
                telemetry,
                Math.Max(leftCount, rightCount),
                profile.ShelfExtentSize,
                target.RouterDepth,
                splitDepth);
        }
        if (collectAttribution)
        {
            structuralTicks += Stopwatch.GetTimestamp() - started;
        }

        started = collectAttribution ? Stopwatch.GetTimestamp() : 0;
        if (insertResult == Scalar8VarIdentityInsertResult.Full &&
            TrySplitScalar8VarIdentityShelfAtDepth(
                mutableShelf,
                profile,
                target.RouterDepth,
                encodedKey,
                identity,
                allowDuplicateKeys,
                out selectedRightPrefix,
                out leftShelf,
                out rightShelf,
                out leftCount,
                out rightCount,
                out splitInsertResult))
        {
            scalar8VarIdentityMutableBatchShelves.Remove(target.Offset);
            RawDataReservation leftReservation = kernel.Reserve(profile.ShelfExtentSize);
            leftShelf.CopyTo(leftReservation.Span);
            RawDataReservation rightReservation = kernel.Reserve(profile.ShelfExtentSize);
            rightShelf.CopyTo(rightReservation.Span);
            RepointSameDepthRoutes(pathTarget.ParentRouterOffset, target.Offset, selectedRightPrefix, leftReservation.Extent.Offset, rightReservation.Extent.Offset);
            DataKernelCommitTelemetry telemetry = CommitAndInvalidateRouterReadCache();
            if (collectAttribution)
            {
                structuralTicks += Stopwatch.GetTimestamp() - started;
            }

            attribution = new Scalar8VarIdentityWalkedWriteAttribution(routeWalkTicks, shelfReadTicks, duplicateChainTicks, mutationTicks, stageTicks, structuralTicks);
            return new Scalar8VarIdentityRoutedInsertResult(
                Scalar8VarIdentityRoutedInsertKind.WalkedShelfSplit,
                splitInsertResult,
                target.Offset,
                rightReservation.Extent.Offset,
                telemetry,
                Math.Max(leftCount, rightCount),
                profile.ShelfExtentSize,
                target.RouterDepth,
                target.RouterDepth);
        }
        if (collectAttribution)
        {
            structuralTicks += Stopwatch.GetTimestamp() - started;
        }

        started = collectAttribution ? Stopwatch.GetTimestamp() : 0;
        mutableShelf.EnsureSlotBytesCurrent();
        if (insertResult == Scalar8VarIdentityInsertResult.Full &&
            TryAppendScalar8VarIdentityDuplicateRunTail(
                target.Offset,
                target.Offset,
                mutableShelf.Bytes,
                profile,
                encodedKey,
                identity,
                allowDuplicateKeys,
                out Scalar8VarIdentityRoutedInsertResult appendedTailResult))
        {
            if (collectAttribution)
            {
                structuralTicks += Stopwatch.GetTimestamp() - started;
            }

            attribution = new Scalar8VarIdentityWalkedWriteAttribution(routeWalkTicks, shelfReadTicks, duplicateChainTicks, mutationTicks, stageTicks, structuralTicks);
            return appendedTailResult;
        }
        if (collectAttribution)
        {
            structuralTicks += Stopwatch.GetTimestamp() - started;
        }

        started = collectAttribution ? Stopwatch.GetTimestamp() : 0;
        mutableShelf.EnsureSlotBytesCurrent();
        if (insertResult == Scalar8VarIdentityInsertResult.Full &&
            TryRewriteScalar8VarIdentityDuplicateRunChain(
                target.Offset,
                mutableShelf.Bytes,
                profile,
                encodedKey,
                identity,
                allowDuplicateKeys,
                out long tailShelfOffset,
                out int chainShelfCount,
                out int largestShelfItemCount,
                out Scalar8VarIdentityInsertResult chainInsertResult,
                out DataKernelCommitTelemetry chainTelemetry))
        {
            if (collectAttribution)
            {
                structuralTicks += Stopwatch.GetTimestamp() - started;
            }

            attribution = new Scalar8VarIdentityWalkedWriteAttribution(routeWalkTicks, shelfReadTicks, duplicateChainTicks, mutationTicks, stageTicks, structuralTicks);
            return new Scalar8VarIdentityRoutedInsertResult(
                Scalar8VarIdentityRoutedInsertKind.WalkedDuplicateRunOverflow,
                chainInsertResult,
                target.Offset,
                tailShelfOffset,
                chainTelemetry,
                largestShelfItemCount,
                profile.ShelfExtentSize,
                target.RouterDepth,
                checked((ushort)chainShelfCount));
        }
        if (collectAttribution)
        {
            structuralTicks += Stopwatch.GetTimestamp() - started;
        }

        attribution = new Scalar8VarIdentityWalkedWriteAttribution(routeWalkTicks, shelfReadTicks, duplicateChainTicks, mutationTicks, stageTicks, structuralTicks);
        return new Scalar8VarIdentityRoutedInsertResult(
            insertResult == Scalar8VarIdentityInsertResult.Invalid ? Scalar8VarIdentityRoutedInsertKind.Invalid : Scalar8VarIdentityRoutedInsertKind.Full,
            insertResult,
            target.Offset,
            0,
            default,
            beforeItemCount,
            profile.ShelfExtentSize,
            target.RouterDepth);
    }

    internal Scalar8VarIdentityRangeReader OpenScalar8VarIdentityRangeReader(
        long rootRouterOffset,
        int maxIdentityLength,
        ulong lowerEncodedKey,
        ulong upperEncodedKey)
    {
        if (lowerEncodedKey > upperEncodedKey)
        {
            throw new ArgumentException("The upper SV8 key must be greater than or equal to the lower key.", nameof(upperEncodedKey));
        }

        return new Scalar8VarIdentityRangeReader(this, rootRouterOffset, maxIdentityLength, lowerEncodedKey, upperEncodedKey);
    }

    internal byte[] ReadScalar8VarIdentityShelfBytes(long shelfOffset, int maxIdentityLength, out Scalar8VarIdentityProfile profile)
    {
        byte[] header = new byte[Scalar8VarIdentityLayout.HeaderSize];
        kernel.Read(shelfOffset, header);
        if (Scalar8VarIdentityLayout.ReadMagic(header) != Scalar8VarIdentityLayout.Magic ||
            Scalar8VarIdentityLayout.ReadFormatVersion(header) != Scalar8VarIdentityLayout.FormatVersion ||
            Scalar8VarIdentityLayout.ReadHeaderSize(header) != Scalar8VarIdentityLayout.HeaderSize)
        {
            throw new InvalidDataException("The routed SV8 shelf header is invalid.");
        }

        int shelfExtentSize = Scalar8VarIdentityLayout.ReadShelfExtentSize(header);
        profile = Scalar8VarIdentityProfile.Create(shelfExtentSize, maxIdentityLength);
        byte[] shelfBytes = new byte[shelfExtentSize];
        kernel.Read(shelfOffset, shelfBytes);
        return shelfBytes;
    }

    /// <summary>
    /// Reads one `SV8` shelf as a session-owned mutable batch image when a durability batch is active.<br/>
    /// Reusing the same disk-shaped byte buffer avoids repeated shelf reads and full-image clone allocation across ordinary insert runs.<br/>
    /// Non-batch callers receive a temporary mutable wrapper that is staged immediately by the insert path.<br/>
    /// </summary>
    /// <param name="shelfOffset">The file offset of the shelf to read.</param>
    /// <param name="maxIdentityLength">The maximum raw identity length accepted by the profile family.</param>
    /// <returns>The mutable `SV8` batch shelf image.</returns>
    private Scalar8VarIdentityMutableShelfView ReadScalar8VarIdentityMutableShelf(long shelfOffset, int maxIdentityLength)
    {
        if (durabilityBatchActive &&
            scalar8VarIdentityMutableBatchShelves.TryGetValue(shelfOffset, out Scalar8VarIdentityMutableShelfView? mutableShelf))
        {
            return mutableShelf;
        }

        byte[] shelfBytes = ReadScalar8VarIdentityShelfBytes(shelfOffset, maxIdentityLength, out Scalar8VarIdentityProfile profile);
        if (!Scalar8VarIdentityMutableShelfView.TryCreate(shelfBytes, profile, out Scalar8VarIdentityMutableShelfView shelf))
        {
            throw new InvalidDataException("The routed SV8 mutable shelf bytes are invalid.");
        }

        if (durabilityBatchActive)
        {
            scalar8VarIdentityMutableBatchShelves[shelfOffset] = shelf;
        }

        return shelf;
    }

    /// <summary>
    /// Stages an `SV8` shelf rewrite, deferring the actual DataKernel copy when inside a durability batch.<br/>
    /// If insertion reused the mutable shelf image, the method marks that image dirty; if insertion returned a compacted replacement image, the batch cache is replaced with that image.<br/>
    /// Non-batch callers stage and publish immediately to preserve existing command semantics.<br/>
    /// </summary>
    /// <param name="shelfOffset">The shelf offset being rewritten.</param>
    /// <param name="mutableShelf">The current mutable shelf wrapper.</param>
    /// <param name="rewrittenBytes">The authoritative rewritten shelf bytes.</param>
    /// <returns>Commit telemetry for the immediate or deferred commit request.</returns>
    private DataKernelCommitTelemetry StageScalar8VarIdentityShelfRewrite(
        long shelfOffset,
        Scalar8VarIdentityMutableShelfView mutableShelf,
        byte[] rewrittenBytes)
    {
        if (rewrittenBytes.Length < mutableShelf.Profile.ShelfExtentSize)
        {
            throw new ArgumentException("The SV8 shelf rewrite image must match the profiled shelf extent size.", nameof(rewrittenBytes));
        }

        if (durabilityBatchActive)
        {
            if (ReferenceEquals(rewrittenBytes, mutableShelf.Bytes))
            {
                mutableShelf.MarkDirty();
            }
            else
            {
                if (!Scalar8VarIdentityMutableShelfView.TryCreate(rewrittenBytes, mutableShelf.Profile, out Scalar8VarIdentityMutableShelfView replacement))
                {
                    throw new InvalidDataException("The replacement SV8 mutable shelf bytes are invalid.");
                }

                replacement.MarkDirty();
                scalar8VarIdentityMutableBatchShelves[shelfOffset] = replacement;
            }

            return CommitWithoutInvalidatingRouterReadCache();
        }

        RawDataReservation shelfRewrite = kernel.ReserveAt(shelfOffset, mutableShelf.Profile.ShelfExtentSize);
        rewrittenBytes.AsSpan(0, mutableShelf.Profile.ShelfExtentSize).CopyTo(shelfRewrite.Span);
        return CommitAndInvalidateRouterReadCache();
    }

    private static bool TrySplitScalar8VarIdentityShelf(
        Scalar8VarIdentityMutableShelfView mutableShelf,
        Scalar8VarIdentityProfile profile,
        ushort currentRouterDepth,
        ulong incomingKey,
        ReadOnlySpan<byte> incomingIdentity,
        bool allowDuplicateKeys,
        out ushort splitDepth,
        out byte selectedRightPrefix,
        out byte[] leftShelf,
        out byte[] rightShelf,
        out int leftCount,
        out int rightCount,
        out Scalar8VarIdentityInsertResult insertResult)
    {
        splitDepth = 0;
        selectedRightPrefix = 0;
        leftShelf = [];
        rightShelf = [];
        leftCount = 0;
        rightCount = 0;
        insertResult = Scalar8VarIdentityInsertResult.Full;
        if (mutableShelf.Profile.ShelfExtentSize != profile.ShelfExtentSize)
        {
            insertResult = Scalar8VarIdentityInsertResult.Invalid;
            return false;
        }

        int totalCount = checked(mutableShelf.ItemCount + 1);
        ulong[] keys = new ulong[totalCount];
        int[] sourceSlots = new int[totalCount];
        int incomingIndex = -1;
        bool inserted = false;
        int targetIndex = 0;
        for (int sourceIndex = 0; sourceIndex < mutableShelf.ItemCount; sourceIndex++)
        {
            ulong currentKey = mutableShelf.ReadKeyAt(sourceIndex);
            ReadOnlySpan<byte> currentIdentity = mutableShelf.ReadIdentityAt(sourceIndex);
            if (!inserted && CompareScalar8VarIdentityTuple(currentKey, currentIdentity, incomingKey, incomingIdentity) > 0)
            {
                keys[targetIndex] = incomingKey;
                sourceSlots[targetIndex] = -1;
                incomingIndex = targetIndex;
                targetIndex++;
                inserted = true;
            }

            if (currentKey == incomingKey && Scalar8VarIdentityLayout.IdentityBytesEqual(currentIdentity, incomingIdentity))
            {
                insertResult = Scalar8VarIdentityInsertResult.AlreadyPresent;
                return false;
            }

            if (!allowDuplicateKeys && currentKey == incomingKey)
            {
                insertResult = Scalar8VarIdentityInsertResult.KeyConflict;
                return false;
            }

            keys[targetIndex] = currentKey;
            sourceSlots[targetIndex] = sourceIndex;
            targetIndex++;
        }

        if (!inserted)
        {
            keys[targetIndex] = incomingKey;
            sourceSlots[targetIndex] = -1;
            incomingIndex = targetIndex;
        }

        Scalar8VarIdentitySplitIdentitySource identitySource = new(mutableShelf, sourceSlots, incomingIndex, incomingIdentity);
        int firstSplitDepth = FindFirstDifferingScalar8VarIdentityDepth(keys, currentRouterDepth + 1);
        for (int depth = firstSplitDepth; depth >= 0 && depth < sizeof(ulong); depth++)
        {
            if (TryBuildScalar8VarIdentitySplitAtDepth(keys, identitySource, profile, checked((ushort)depth), out selectedRightPrefix, out leftShelf, out rightShelf, out leftCount, out rightCount))
            {
                splitDepth = checked((ushort)depth);
                insertResult = Scalar8VarIdentityInsertResult.Inserted;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Attempts an incremental insert into an existing SV8 same-key duplicate-run chain.<br/>
    /// The method scans linked shelves in identity order, rejects duplicate tuples, and rewrites only the target shelf when it has space.<br/>
    /// If the target shelf is full or the chain is not a pure same-key run, the caller falls back to the full chain rebuild path.<br/>
    /// </summary>
    /// <param name="headShelfOffset">The route-owned head shelf offset.</param>
    /// <param name="headShelfBytes">The already-read head shelf bytes.</param>
    /// <param name="profile">The SV8 profile used by every chain segment.</param>
    /// <param name="encodedKey">The encoded scalar key being inserted.</param>
    /// <param name="identity">The raw identity being inserted.</param>
    /// <param name="allowDuplicateKeys">Whether duplicate scalar keys are allowed.</param>
    /// <param name="result">Receives the routed insert result when the chain handled the insert.</param>
    /// <returns>`true` when the existing chain produced a terminal insert/no-op/conflict result.</returns>
    private bool TryInsertIntoScalar8VarIdentityDuplicateRunChain(
        long headShelfOffset,
        byte[] headShelfBytes,
        Scalar8VarIdentityProfile profile,
        ulong encodedKey,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys,
        out Scalar8VarIdentityRoutedInsertResult result)
    {
        result = default;
        if (!allowDuplicateKeys)
        {
            result = new Scalar8VarIdentityRoutedInsertResult(
                Scalar8VarIdentityRoutedInsertKind.KeyConflict,
                Scalar8VarIdentityInsertResult.KeyConflict,
                headShelfOffset,
                0,
                default,
                0,
                profile.ShelfExtentSize);
            return true;
        }

        using RouteVisitedOffsetSet visited = RouteVisitedOffsetSet.Rent();
        long fastTailOffset = Scalar8VarIdentityLayout.ReadTailShelfOffset(headShelfBytes);
        if (fastTailOffset != 0 &&
            fastTailOffset != headShelfOffset &&
            TryInsertIntoScalar8VarIdentityDuplicateRunTailFast(
                headShelfOffset,
                fastTailOffset,
                profile,
                encodedKey,
                identity,
                out result))
        {
            return true;
        }

        long currentOffset = headShelfOffset;
        byte[] currentBytes = headShelfBytes;
        while (currentOffset != 0)
        {
            if (!visited.Add(currentOffset))
            {
                throw new InvalidDataException("The SV8 duplicate-run shelf chain contains a cycle.");
            }

            Scalar8VarIdentityReadOnly shelf = new(currentBytes, profile);
            if (!shelf.IsValid)
            {
                result = new Scalar8VarIdentityRoutedInsertResult(
                    Scalar8VarIdentityRoutedInsertKind.Invalid,
                    Scalar8VarIdentityInsertResult.Invalid,
                    currentOffset,
                    0,
                    default,
                    0,
                    profile.ShelfExtentSize);
                return true;
            }

            if (shelf.ItemCount == 0)
            {
                return false;
            }

            for (int i = 0; i < shelf.ItemCount; i++)
            {
                if (shelf.ReadKeyAt(i) != encodedKey)
                {
                    return false;
                }

                if (Scalar8VarIdentityLayout.IdentityBytesEqual(shelf.ReadIdentityAt(i), identity))
                {
                    result = new Scalar8VarIdentityRoutedInsertResult(
                        Scalar8VarIdentityRoutedInsertKind.NoOp,
                        Scalar8VarIdentityInsertResult.AlreadyPresent,
                        currentOffset,
                        0,
                        default,
                        shelf.ItemCount,
                        profile.ShelfExtentSize);
                    return true;
                }
            }

            long nextOffset = Scalar8VarIdentityLayout.ReadNextShelfOffset(currentBytes);
            ReadOnlySpan<byte> lastIdentity = shelf.ReadIdentityAt(shelf.ItemCount - 1);
            if (Scalar8VarIdentityLayout.CompareIdentityBytes(identity, lastIdentity) <= 0 || nextOffset == 0)
            {
                Scalar8VarIdentityInsertResult insertResult = Scalar8VarIdentity.Insert(
                    currentBytes,
                    profile,
                    encodedKey,
                    identity,
                    allowDuplicateKeys: true,
                    out byte[] rewrittenBytes);
                if (insertResult == Scalar8VarIdentityInsertResult.Full)
                {
                    if (nextOffset == 0 &&
                        Scalar8VarIdentityLayout.CompareIdentityBytes(identity, lastIdentity) > 0 &&
                        TryAppendScalar8VarIdentityDuplicateRunTail(
                            headShelfOffset,
                            currentOffset,
                            currentBytes,
                            profile,
                            encodedKey,
                            identity,
                            allowDuplicateKeys: true,
                            out result))
                    {
                        return true;
                    }

                    return false;
                }

                if (insertResult == Scalar8VarIdentityInsertResult.Inserted)
                {
                    Scalar8VarIdentityLayout.WriteNextShelfOffset(rewrittenBytes, nextOffset);
                    RawDataReservation rewrite = kernel.ReserveAt(currentOffset, profile.ShelfExtentSize);
                    rewrittenBytes.CopyTo(rewrite.Span);
                    DataKernelCommitTelemetry telemetry = CommitWithoutInvalidatingRouterReadCache();
                    result = new Scalar8VarIdentityRoutedInsertResult(
                        Scalar8VarIdentityRoutedInsertKind.WalkedDuplicateRunOverflow,
                        insertResult,
                        currentOffset,
                        currentOffset,
                        telemetry,
                        shelf.ItemCount + 1,
                        profile.ShelfExtentSize);
                    return true;
                }

                result = new Scalar8VarIdentityRoutedInsertResult(
                    insertResult == Scalar8VarIdentityInsertResult.KeyConflict ? Scalar8VarIdentityRoutedInsertKind.KeyConflict : Scalar8VarIdentityRoutedInsertKind.NoOp,
                    insertResult,
                    currentOffset,
                    0,
                    default,
                    shelf.ItemCount,
                    profile.ShelfExtentSize);
                return true;
            }

            currentOffset = nextOffset;
            currentBytes = ReadScalar8VarIdentityShelfBytes(currentOffset, profile.MaxIdentityLength, out Scalar8VarIdentityProfile nextProfile);
            if (nextProfile.ShelfExtentSize != profile.ShelfExtentSize)
            {
                throw new InvalidDataException("The SV8 duplicate-run shelf chain changed shelf extent sizes.");
            }
        }

        return false;
    }

    /// <summary>
    /// Attempts to insert directly into the known terminal shelf of an SV8 duplicate-run chain.<br/>
    /// The head shelf stores the tail offset as a fast-path hint, so sorted same-key identity inserts avoid walking earlier chain segments.<br/>
    /// If the incoming identity does not sort after the current tail, the caller falls back to the full chain walk.<br/>
    /// </summary>
    /// <param name="headShelfOffset">The route-owned chain head shelf offset.</param>
    /// <param name="tailShelfOffset">The known terminal shelf offset.</param>
    /// <param name="profile">The SV8 profile used by the tail shelf.</param>
    /// <param name="encodedKey">The encoded scalar key being inserted.</param>
    /// <param name="identity">The raw identity being inserted.</param>
    /// <param name="result">Receives the routed insert result when the tail fast path handles the insert.</param>
    /// <returns>`true` when the tail fast path produced a terminal result.</returns>
    private bool TryInsertIntoScalar8VarIdentityDuplicateRunTailFast(
        long headShelfOffset,
        long tailShelfOffset,
        Scalar8VarIdentityProfile profile,
        ulong encodedKey,
        ReadOnlySpan<byte> identity,
        out Scalar8VarIdentityRoutedInsertResult result)
    {
        Scalar8VarIdentityMutableShelfView tailMutableShelf = ReadScalar8VarIdentityMutableShelf(tailShelfOffset, profile.MaxIdentityLength);
        if (tailMutableShelf.Profile.ShelfExtentSize != profile.ShelfExtentSize)
        {
            throw new InvalidDataException("The SV8 duplicate-run tail changed shelf extent sizes.");
        }

        byte[] tailBytes = tailMutableShelf.Bytes;
        if (tailMutableShelf.ItemCount == 0)
        {
            result = new Scalar8VarIdentityRoutedInsertResult(
                Scalar8VarIdentityRoutedInsertKind.Invalid,
                Scalar8VarIdentityInsertResult.Invalid,
                tailShelfOffset,
                0,
                default,
                0,
                profile.ShelfExtentSize);
            return true;
        }

        if (tailMutableShelf.ReadKeyAt(0) != encodedKey || tailMutableShelf.ReadKeyAt(tailMutableShelf.ItemCount - 1) != encodedKey)
        {
            result = default;
            return false;
        }

        if (Scalar8VarIdentityLayout.CompareIdentityBytes(identity, tailMutableShelf.ReadIdentityAt(tailMutableShelf.ItemCount - 1)) <= 0)
        {
            result = default;
            return false;
        }

        Scalar8VarIdentityInsertResult insertResult;
        byte[] rewrittenTail;
        if (durabilityBatchActive)
        {
            insertResult = tailMutableShelf.Insert(encodedKey, identity, allowDuplicateKeys: true);
            rewrittenTail = tailMutableShelf.Bytes;
        }
        else
        {
            insertResult = Scalar8VarIdentity.Insert(
                tailBytes,
                profile,
                encodedKey,
                identity,
                allowDuplicateKeys: true,
                out rewrittenTail);
        }

        if (insertResult == Scalar8VarIdentityInsertResult.Inserted)
        {
            DataKernelCommitTelemetry telemetry = StageScalar8VarIdentityShelfRewrite(tailShelfOffset, tailMutableShelf, rewrittenTail);
            result = new Scalar8VarIdentityRoutedInsertResult(
                Scalar8VarIdentityRoutedInsertKind.WalkedDuplicateRunOverflow,
                insertResult,
                tailShelfOffset,
                tailShelfOffset,
                telemetry,
                tailMutableShelf.ItemCount,
                profile.ShelfExtentSize);
            return true;
        }

        if (insertResult == Scalar8VarIdentityInsertResult.Full)
        {
            tailMutableShelf.EnsureSlotBytesCurrent();
            scalar8VarIdentityMutableBatchShelves.Remove(tailShelfOffset);
            return TryAppendScalar8VarIdentityDuplicateRunTail(
                headShelfOffset,
                tailShelfOffset,
                tailBytes,
                profile,
                encodedKey,
                identity,
                allowDuplicateKeys: true,
                out result);
        }

        result = new Scalar8VarIdentityRoutedInsertResult(
            insertResult == Scalar8VarIdentityInsertResult.KeyConflict ? Scalar8VarIdentityRoutedInsertKind.KeyConflict : Scalar8VarIdentityRoutedInsertKind.NoOp,
            insertResult,
            tailShelfOffset,
            0,
            default,
            tailMutableShelf.ItemCount,
            profile.ShelfExtentSize);
        return true;
    }

    /// <summary>
    /// Appends a new terminal shelf to an SV8 same-key duplicate-run chain when the incoming identity sorts after the current terminal shelf.<br/>
    /// This avoids full-chain rebuilds for sorted same-key identity loads, which are the common append-like case for path/blob identities under one scalar property key.<br/>
    /// The method validates that the current shelf is a pure same-key segment before linking the new shelf.<br/>
    /// </summary>
    /// <param name="headShelfOffset">The route-owned chain head shelf offset whose tail pointer should be maintained.</param>
    /// <param name="terminalShelfOffset">The shelf offset that should become the predecessor of the new tail shelf.</param>
    /// <param name="terminalShelfBytes">The current terminal shelf bytes.</param>
    /// <param name="profile">The SV8 profile used by both shelves.</param>
    /// <param name="encodedKey">The encoded scalar key being inserted.</param>
    /// <param name="identity">The raw identity being appended.</param>
    /// <param name="allowDuplicateKeys">Whether duplicate scalar keys are allowed.</param>
    /// <param name="result">Receives the routed insert result when the append succeeds or terminates as conflict/no-op.</param>
    /// <returns>`true` when the append-tail path produced a terminal result.</returns>
    private bool TryAppendScalar8VarIdentityDuplicateRunTail(
        long headShelfOffset,
        long terminalShelfOffset,
        byte[] terminalShelfBytes,
        Scalar8VarIdentityProfile profile,
        ulong encodedKey,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys,
        out Scalar8VarIdentityRoutedInsertResult result)
    {
        result = default;
        if (!allowDuplicateKeys)
        {
            result = new Scalar8VarIdentityRoutedInsertResult(
                Scalar8VarIdentityRoutedInsertKind.KeyConflict,
                Scalar8VarIdentityInsertResult.KeyConflict,
                terminalShelfOffset,
                0,
                default,
                0,
                profile.ShelfExtentSize);
            return true;
        }

        Scalar8VarIdentityReadOnly terminalShelf = new(terminalShelfBytes, profile);
        if (!terminalShelf.IsValid || terminalShelf.ItemCount == 0 || Scalar8VarIdentityLayout.ReadNextShelfOffset(terminalShelfBytes) != 0)
        {
            return false;
        }

        if (terminalShelf.ReadKeyAt(0) != encodedKey || terminalShelf.ReadKeyAt(terminalShelf.ItemCount - 1) != encodedKey)
        {
            return false;
        }

        if (Scalar8VarIdentityLayout.CompareIdentityBytes(identity, terminalShelf.ReadIdentityAt(terminalShelf.ItemCount - 1)) <= 0)
        {
            return false;
        }

        byte[] tailShelf = Scalar8VarIdentity.CreateEmpty(profile);
        Scalar8VarIdentityInsertResult tailInsert = Scalar8VarIdentity.Insert(
            tailShelf,
            profile,
            encodedKey,
            identity,
            allowDuplicateKeys: true,
            out byte[] rewrittenTail);
        if (tailInsert != Scalar8VarIdentityInsertResult.Inserted)
        {
            result = new Scalar8VarIdentityRoutedInsertResult(
                tailInsert == Scalar8VarIdentityInsertResult.KeyConflict ? Scalar8VarIdentityRoutedInsertKind.KeyConflict : Scalar8VarIdentityRoutedInsertKind.Invalid,
                tailInsert,
                terminalShelfOffset,
                0,
                default,
                terminalShelf.ItemCount,
                profile.ShelfExtentSize);
            return true;
        }

        RawDataReservation tailReservation = kernel.Reserve(profile.ShelfExtentSize);
        rewrittenTail.CopyTo(tailReservation.Span);
        scalar8VarIdentityMutableBatchShelves.Remove(headShelfOffset);
        scalar8VarIdentityMutableBatchShelves.Remove(terminalShelfOffset);
        RawDataReservation terminalRewrite = kernel.ReserveAt(terminalShelfOffset, profile.ShelfExtentSize);
        terminalShelfBytes.AsSpan(0, profile.ShelfExtentSize).CopyTo(terminalRewrite.Span);
        Scalar8VarIdentityLayout.WriteNextShelfOffset(terminalRewrite.Span, tailReservation.Extent.Offset);
        if (headShelfOffset == terminalShelfOffset)
        {
            Scalar8VarIdentityLayout.WriteTailShelfOffset(terminalRewrite.Span, tailReservation.Extent.Offset);
        }
        else
        {
            byte[] headBytes = ReadScalar8VarIdentityShelfBytes(headShelfOffset, profile.MaxIdentityLength, out Scalar8VarIdentityProfile headProfile);
            if (headProfile.ShelfExtentSize != profile.ShelfExtentSize)
            {
                throw new InvalidDataException("The SV8 duplicate-run head changed shelf extent sizes.");
            }

            Scalar8VarIdentityLayout.WriteTailShelfOffset(headBytes, tailReservation.Extent.Offset);
            RawDataReservation headRewrite = kernel.ReserveAt(headShelfOffset, profile.ShelfExtentSize);
            headBytes.CopyTo(headRewrite.Span);
        }

        DataKernelCommitTelemetry telemetry = CommitWithoutInvalidatingRouterReadCache();
        result = new Scalar8VarIdentityRoutedInsertResult(
            Scalar8VarIdentityRoutedInsertKind.WalkedDuplicateRunOverflow,
            Scalar8VarIdentityInsertResult.Inserted,
            terminalShelfOffset,
            tailReservation.Extent.Offset,
            telemetry,
            1,
            profile.ShelfExtentSize);
        return true;
    }

    /// <summary>
    /// Rewrites a same-key SV8 duplicate run into a linked shelf chain when scalar-key routing can no longer split it.<br/>
    /// The method is intentionally correctness-first: it collects the full linked run, inserts the incoming identity in tuple order, repartitions into packed shelves, and rewrites the chain head plus appended overflow shelves.<br/>
    /// Later hot-path work can replace this with incremental tail insertion once the persisted duplicate-run shape is proven.<br/>
    /// </summary>
    /// <param name="headShelfOffset">The route-owned head shelf offset.</param>
    /// <param name="headShelfBytes">The current head shelf bytes.</param>
    /// <param name="profile">The SV8 shelf profile used by every segment in the chain.</param>
    /// <param name="encodedKey">The encoded scalar key being inserted.</param>
    /// <param name="incomingIdentity">The raw identity being inserted.</param>
    /// <param name="allowDuplicateKeys">Whether duplicate scalar keys are allowed.</param>
    /// <param name="tailShelfOffset">Receives the final shelf offset in the rebuilt chain.</param>
    /// <param name="chainShelfCount">Receives the number of shelves in the rebuilt chain.</param>
    /// <param name="largestShelfItemCount">Receives the largest item count across rebuilt shelves.</param>
    /// <param name="insertResult">Receives the logical insert result represented by the chain rewrite.</param>
    /// <param name="telemetry">Receives DataKernel commit telemetry for the chain rewrite.</param>
    /// <returns>`true` when the same-key duplicate run was rewritten successfully.</returns>
    private bool TryRewriteScalar8VarIdentityDuplicateRunChain(
        long headShelfOffset,
        byte[] headShelfBytes,
        Scalar8VarIdentityProfile profile,
        ulong encodedKey,
        ReadOnlySpan<byte> incomingIdentity,
        bool allowDuplicateKeys,
        out long tailShelfOffset,
        out int chainShelfCount,
        out int largestShelfItemCount,
        out Scalar8VarIdentityInsertResult insertResult,
        out DataKernelCommitTelemetry telemetry)
    {
        tailShelfOffset = 0;
        chainShelfCount = 0;
        largestShelfItemCount = 0;
        insertResult = Scalar8VarIdentityInsertResult.Full;
        telemetry = default;
        if (!allowDuplicateKeys)
        {
            insertResult = Scalar8VarIdentityInsertResult.KeyConflict;
            return false;
        }

        Scalar8VarIdentityIdentityRef[] identities = new Scalar8VarIdentityIdentityRef[256];
        int identityCount = 0;
        bool incomingAdded = false;
        byte[] incomingIdentityBytes = incomingIdentity.ToArray();
        using RouteVisitedOffsetSet visited = RouteVisitedOffsetSet.Rent();
        long currentOffset = headShelfOffset;
        byte[] currentBytes = headShelfBytes;
        while (currentOffset != 0)
        {
            if (!visited.Add(currentOffset))
            {
                throw new InvalidDataException("The SV8 duplicate-run shelf chain contains a cycle.");
            }

            Scalar8VarIdentityReadOnly shelf = new(currentBytes, profile);
            if (!shelf.IsValid)
            {
                insertResult = Scalar8VarIdentityInsertResult.Invalid;
                return false;
            }

            for (int i = 0; i < shelf.ItemCount; i++)
            {
                if (shelf.ReadKeyAt(i) != encodedKey)
                {
                    return false;
                }

                ReadOnlySpan<byte> existingIdentity = shelf.ReadIdentityAt(i);
                if (Scalar8VarIdentityLayout.IdentityBytesEqual(existingIdentity, incomingIdentity))
                {
                    insertResult = Scalar8VarIdentityInsertResult.AlreadyPresent;
                    return false;
                }

                if (!incomingAdded && Scalar8VarIdentityLayout.CompareIdentityBytes(incomingIdentity, existingIdentity) < 0)
                {
                    AddScalar8VarIdentityRef(ref identities, ref identityCount, new Scalar8VarIdentityIdentityRef(incomingIdentityBytes, 0, true));
                    incomingAdded = true;
                }

                AddScalar8VarIdentityRef(ref identities, ref identityCount, new Scalar8VarIdentityIdentityRef(currentBytes, checked((int)shelf.ReadRecordOffsetAt(i)), false));
            }

            currentOffset = Scalar8VarIdentityLayout.ReadNextShelfOffset(currentBytes);
            if (currentOffset != 0)
            {
                currentBytes = ReadScalar8VarIdentityShelfBytes(currentOffset, profile.MaxIdentityLength, out Scalar8VarIdentityProfile nextProfile);
                if (nextProfile.ShelfExtentSize != profile.ShelfExtentSize)
                {
                    throw new InvalidDataException("The SV8 duplicate-run shelf chain changed shelf extent sizes.");
                }
            }
        }

        if (!incomingAdded)
        {
            AddScalar8VarIdentityRef(ref identities, ref identityCount, new Scalar8VarIdentityIdentityRef(incomingIdentityBytes, 0, true));
        }

        byte[][] rebuiltShelves = new byte[4][];
        int rebuiltShelfCount = 0;
        int start = 0;
        while (start < identityCount)
        {
            int bestCount = GetScalar8VarIdentityDuplicateRunChunkCount(identities, incomingIdentityBytes, start, identityCount - start, profile);
            if (bestCount == 0 ||
                !TryBuildScalar8VarIdentityDuplicateRunShelf(encodedKey, identities, incomingIdentityBytes, start, bestCount, profile, out byte[] bestShelf))
            {
                return false;
            }

            if (rebuiltShelfCount == rebuiltShelves.Length)
            {
                Array.Resize(ref rebuiltShelves, checked(rebuiltShelves.Length * 2));
            }

            rebuiltShelves[rebuiltShelfCount++] = bestShelf;
            largestShelfItemCount = Math.Max(largestShelfItemCount, bestCount);
            start += bestCount;
        }

        long nextOffset = 0;
        long tailOffset = headShelfOffset;
        scalar8VarIdentityMutableBatchShelves.Remove(headShelfOffset);
        for (int i = rebuiltShelfCount - 1; i >= 1; i--)
        {
            Scalar8VarIdentityLayout.WriteNextShelfOffset(rebuiltShelves[i], nextOffset);
            RawDataReservation reservation = kernel.Reserve(profile.ShelfExtentSize);
            rebuiltShelves[i].CopyTo(reservation.Span);
            nextOffset = reservation.Extent.Offset;
            if (tailOffset == headShelfOffset)
            {
                tailOffset = reservation.Extent.Offset;
            }
        }

        Scalar8VarIdentityLayout.WriteNextShelfOffset(rebuiltShelves[0], nextOffset);
        Scalar8VarIdentityLayout.WriteTailShelfOffset(rebuiltShelves[0], tailOffset == headShelfOffset ? 0 : tailOffset);
        RawDataReservation headRewrite = kernel.ReserveAt(headShelfOffset, profile.ShelfExtentSize);
        rebuiltShelves[0].CopyTo(headRewrite.Span);
        telemetry = CommitWithoutInvalidatingRouterReadCache();
        tailShelfOffset = tailOffset;
        chainShelfCount = rebuiltShelfCount;
        insertResult = Scalar8VarIdentityInsertResult.Inserted;
        return true;
    }

    private static void AddScalar8VarIdentityRef(ref Scalar8VarIdentityIdentityRef[] identities, ref int count, Scalar8VarIdentityIdentityRef identity)
    {
        if (count == identities.Length)
        {
            Array.Resize(ref identities, checked(identities.Length * 2));
        }

        identities[count++] = identity;
    }

    private static int GetScalar8VarIdentityDuplicateRunChunkCount(
        ReadOnlySpan<Scalar8VarIdentityIdentityRef> identities,
        ReadOnlySpan<byte> incomingIdentity,
        int start,
        int available,
        Scalar8VarIdentityProfile profile)
    {
        byte[] bytes = Scalar8VarIdentity.CreateEmpty(profile);
        int slotCapacityBytes = Scalar8VarIdentityLayout.ReadSlotCapacityBytes(bytes);
        int recordCursor = Scalar8VarIdentityLayout.HeaderSize + slotCapacityBytes;
        int count = 0;
        while (count < available)
        {
            if (checked((count + 1) * Scalar8VarIdentityLayout.SlotSize) > slotCapacityBytes)
            {
                break;
            }

            ReadOnlySpan<byte> identity = identities[start + count].ReadIdentity(incomingIdentity);
            int recordLength = Scalar8VarIdentityLayout.GetNewRecordLength(identity.Length);
            if (recordCursor + recordLength > profile.ShelfExtentSize)
            {
                break;
            }

            recordCursor += recordLength;
            count++;
        }

        return count;
    }

    private static bool TryBuildScalar8VarIdentityDuplicateRunShelf(
        ulong encodedKey,
        ReadOnlySpan<Scalar8VarIdentityIdentityRef> identities,
        ReadOnlySpan<byte> incomingIdentity,
        int start,
        int count,
        Scalar8VarIdentityProfile profile,
        out byte[] bytes)
    {
        bytes = Scalar8VarIdentity.CreateEmpty(profile);
        int slotCapacityBytes = Scalar8VarIdentityLayout.ReadSlotCapacityBytes(bytes);
        int slotCursor = Scalar8VarIdentityLayout.HeaderSize;
        int recordCursor = Scalar8VarIdentityLayout.HeaderSize + slotCapacityBytes;
        uint keyPrefix = Scalar8VarIdentityLayout.CreateKeyPrefix(encodedKey);
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> identity = identities[start + i].ReadIdentity(incomingIdentity);
            if (identity.Length <= 0 || identity.Length > profile.MaxIdentityLength)
            {
                return false;
            }

            int recordLength = Scalar8VarIdentityLayout.GetNewRecordLength(identity.Length);
            if (recordCursor + recordLength > profile.ShelfExtentSize)
            {
                return false;
            }

            Scalar8VarIdentityLayout.WriteRecord(bytes, recordCursor, encodedKey, identity);
            Scalar8VarIdentityLayout.WriteSlotRecordOffset(bytes, slotCursor, recordCursor);
            Scalar8VarIdentityLayout.WriteSlotKeyPrefix(bytes, slotCursor, keyPrefix);
            recordCursor += recordLength;
            slotCursor += Scalar8VarIdentityLayout.SlotSize;
        }

        Scalar8VarIdentityLayout.WriteItemCount(bytes, count);
        Scalar8VarIdentityLayout.WriteSlotStreamLength(bytes, slotCursor - Scalar8VarIdentityLayout.HeaderSize);
        Scalar8VarIdentityLayout.WriteRecordArenaEnd(bytes, recordCursor);
        return true;
    }

    /// <summary>
    /// Attempts to split an SV8 shelf at the same scalar-key byte depth as its parent router.<br/>
    /// This path is needed after an expanded one-byte router maps many final-byte prefixes to the same shelf and that shelf later fills again.<br/>
    /// The replacement shelves reuse the parent router directly instead of creating an impossible depth-8 child router.<br/>
    /// </summary>
    /// <param name="shelfBytes">The current persisted shelf bytes.</param>
    /// <param name="profile">The shelf profile used to validate and rebuild the replacement shelves.</param>
    /// <param name="splitDepth">The scalar-key byte depth already represented by the parent router.</param>
    /// <param name="incomingKey">The encoded scalar key being inserted.</param>
    /// <param name="incomingIdentity">The raw variable identity being inserted.</param>
    /// <param name="allowDuplicateKeys">Whether duplicate scalar keys are allowed when identities differ.</param>
    /// <param name="selectedRightPrefix">Receives the first parent-router prefix that should route to the right replacement shelf.</param>
    /// <param name="leftShelf">Receives the rebuilt left shelf bytes.</param>
    /// <param name="rightShelf">Receives the rebuilt right shelf bytes.</param>
    /// <param name="leftCount">Receives the left replacement shelf item count.</param>
    /// <param name="rightCount">Receives the right replacement shelf item count.</param>
    /// <param name="insertResult">Receives the insert outcome represented by the split attempt.</param>
    /// <returns>`true` when a same-depth parent-route split was produced.</returns>
    private static bool TrySplitScalar8VarIdentityShelfAtDepth(
        Scalar8VarIdentityMutableShelfView mutableShelf,
        Scalar8VarIdentityProfile profile,
        ushort splitDepth,
        ulong incomingKey,
        ReadOnlySpan<byte> incomingIdentity,
        bool allowDuplicateKeys,
        out byte selectedRightPrefix,
        out byte[] leftShelf,
        out byte[] rightShelf,
        out int leftCount,
        out int rightCount,
        out Scalar8VarIdentityInsertResult insertResult)
    {
        selectedRightPrefix = 0;
        leftShelf = [];
        rightShelf = [];
        leftCount = 0;
        rightCount = 0;
        insertResult = Scalar8VarIdentityInsertResult.Full;
        if (mutableShelf.Profile.ShelfExtentSize != profile.ShelfExtentSize)
        {
            insertResult = Scalar8VarIdentityInsertResult.Invalid;
            return false;
        }

        int totalCount = checked(mutableShelf.ItemCount + 1);
        ulong[] keys = new ulong[totalCount];
        int[] sourceSlots = new int[totalCount];
        int incomingIndex = -1;
        bool inserted = false;
        int targetIndex = 0;
        for (int sourceIndex = 0; sourceIndex < mutableShelf.ItemCount; sourceIndex++)
        {
            ulong currentKey = mutableShelf.ReadKeyAt(sourceIndex);
            ReadOnlySpan<byte> currentIdentity = mutableShelf.ReadIdentityAt(sourceIndex);
            if (!inserted && CompareScalar8VarIdentityTuple(currentKey, currentIdentity, incomingKey, incomingIdentity) > 0)
            {
                keys[targetIndex] = incomingKey;
                sourceSlots[targetIndex] = -1;
                incomingIndex = targetIndex;
                targetIndex++;
                inserted = true;
            }

            if (currentKey == incomingKey && Scalar8VarIdentityLayout.IdentityBytesEqual(currentIdentity, incomingIdentity))
            {
                insertResult = Scalar8VarIdentityInsertResult.AlreadyPresent;
                return false;
            }

            if (!allowDuplicateKeys && currentKey == incomingKey)
            {
                insertResult = Scalar8VarIdentityInsertResult.KeyConflict;
                return false;
            }

            keys[targetIndex] = currentKey;
            sourceSlots[targetIndex] = sourceIndex;
            targetIndex++;
        }

        if (!inserted)
        {
            keys[targetIndex] = incomingKey;
            sourceSlots[targetIndex] = -1;
            incomingIndex = targetIndex;
        }

        Scalar8VarIdentitySplitIdentitySource identitySource = new(mutableShelf, sourceSlots, incomingIndex, incomingIdentity);
        if (!TryBuildScalar8VarIdentitySplitAtDepth(keys, identitySource, profile, splitDepth, out selectedRightPrefix, out leftShelf, out rightShelf, out leftCount, out rightCount))
        {
            return false;
        }

        insertResult = Scalar8VarIdentityInsertResult.Inserted;
        return true;
    }

    private static bool TryBuildScalar8VarIdentitySplitAtDepth(
        ReadOnlySpan<ulong> keys,
        Scalar8VarIdentitySplitIdentitySource identities,
        Scalar8VarIdentityProfile profile,
        ushort splitDepth,
        out byte selectedRightPrefix,
        out byte[] leftShelf,
        out byte[] rightShelf,
        out int leftCount,
        out int rightCount)
    {
        selectedRightPrefix = 0;
        leftShelf = [];
        rightShelf = [];
        leftCount = 0;
        rightCount = 0;
        int bestBoundary = -1;
        int bestLargestSide = int.MaxValue;
        byte previousPrefix = GetScalar8VarIdentityPrefix(keys[0], splitDepth);
        for (int i = 1; i < keys.Length; i++)
        {
            byte prefix = GetScalar8VarIdentityPrefix(keys[i], splitDepth);
            if (prefix == previousPrefix)
            {
                continue;
            }

            int candidateLeftCount = i;
            int candidateRightCount = keys.Length - i;
            int candidateLargestSide = Math.Max(candidateLeftCount, candidateRightCount);
            if (candidateLargestSide < bestLargestSide)
            {
                bestLargestSide = candidateLargestSide;
                bestBoundary = i;
            }

            previousPrefix = prefix;
        }

        if (bestBoundary > 0 && TryBuildScalar8VarIdentitySplitBoundary(keys, identities, profile, splitDepth, bestBoundary, out selectedRightPrefix, out leftShelf, out rightShelf, out leftCount, out rightCount))
        {
            return true;
        }

        previousPrefix = GetScalar8VarIdentityPrefix(keys[0], splitDepth);
        for (int i = 1; i < keys.Length; i++)
        {
            byte prefix = GetScalar8VarIdentityPrefix(keys[i], splitDepth);
            if (prefix == previousPrefix)
            {
                continue;
            }

            if (i != bestBoundary &&
                TryBuildScalar8VarIdentitySplitBoundary(keys, identities, profile, splitDepth, i, out selectedRightPrefix, out leftShelf, out rightShelf, out leftCount, out rightCount))
            {
                return true;
            }

            previousPrefix = prefix;
        }

        return false;
    }

    /// <summary>
    /// Finds the first scalar-key byte depth that can distinguish the lowest and highest tuple in a sorted `SV8` split candidate.<br/>
    /// Split planning uses this to skip depths that are guaranteed to keep every tuple on the same route and therefore cannot produce two replacement shelves.<br/>
    /// </summary>
    /// <param name="keys">The sorted encoded scalar keys being considered for the split.</param>
    /// <param name="startDepth">The first byte depth that has not already been represented by ancestor routing.</param>
    /// <returns>The first distinguishing depth, or `-1` when no remaining scalar-key byte can split the candidate.</returns>
    private static int FindFirstDifferingScalar8VarIdentityDepth(ReadOnlySpan<ulong> keys, int startDepth)
    {
        if (keys.Length < 2)
        {
            return -1;
        }

        ulong firstKey = keys[0];
        ulong lastKey = keys[^1];
        for (int depth = startDepth; depth < sizeof(ulong); depth++)
        {
            if (GetScalar8VarIdentityPrefix(firstKey, depth) != GetScalar8VarIdentityPrefix(lastKey, depth))
            {
                return depth;
            }
        }

        return -1;
    }

    /// <summary>
    /// Builds the two replacement `SV8` shelves for a single already-selected split boundary.<br/>
    /// Split planning calls this after selecting a preferred boundary so common balanced splits avoid repeatedly rebuilding candidate shelf images.<br/>
    /// </summary>
    /// <param name="keys">The sorted scalar-key array including the incoming tuple.</param>
    /// <param name="identities">The sorted raw identity byte arrays aligned with <paramref name="keys"/>.</param>
    /// <param name="profile">The `SV8` shelf profile used for both replacement shelves.</param>
    /// <param name="splitDepth">The scalar-key byte depth used to choose the right-side router prefix.</param>
    /// <param name="boundary">The first tuple index that belongs to the right replacement shelf.</param>
    /// <param name="selectedRightPrefix">Receives the first prefix byte routed to the right replacement shelf.</param>
    /// <param name="leftShelf">Receives the rebuilt left shelf bytes.</param>
    /// <param name="rightShelf">Receives the rebuilt right shelf bytes.</param>
    /// <param name="leftCount">Receives the left replacement shelf item count.</param>
    /// <param name="rightCount">Receives the right replacement shelf item count.</param>
    /// <returns>`true` when both replacement shelves fit the selected boundary.</returns>
    private static bool TryBuildScalar8VarIdentitySplitBoundary(
        ReadOnlySpan<ulong> keys,
        Scalar8VarIdentitySplitIdentitySource identities,
        Scalar8VarIdentityProfile profile,
        ushort splitDepth,
        int boundary,
        out byte selectedRightPrefix,
        out byte[] leftShelf,
        out byte[] rightShelf,
        out int leftCount,
        out int rightCount)
    {
        selectedRightPrefix = GetScalar8VarIdentityPrefix(keys[boundary], splitDepth);
        leftShelf = [];
        rightShelf = [];
        leftCount = boundary;
        rightCount = keys.Length - boundary;
        if (!TryBuildScalar8VarIdentityShelfFromSource(keys, identities, 0, boundary, profile, out leftShelf) ||
            !TryBuildScalar8VarIdentityShelfFromSource(keys, identities, boundary, keys.Length - boundary, profile, out rightShelf))
        {
            leftShelf = [];
            rightShelf = [];
            leftCount = 0;
            rightCount = 0;
            selectedRightPrefix = 0;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Builds one replacement `SV8` shelf directly from a sorted split source range.<br/>
    /// The slot and record streams are emitted in one pass so split rebuilds avoid a temporary record-offset array and a second slot-writing pass.<br/>
    /// </summary>
    /// <param name="keys">The sorted encoded scalar keys aligned with <paramref name="identities"/>.</param>
    /// <param name="identities">The identity source that can project existing shelf identities and the incoming identity without materializing every identity.</param>
    /// <param name="start">The first source index to include in the replacement shelf.</param>
    /// <param name="count">The number of sorted tuples to include in the replacement shelf.</param>
    /// <param name="profile">The `SV8` shelf profile used to size and validate the replacement shelf.</param>
    /// <param name="bytes">Receives the rebuilt shelf image when the source range fits.</param>
    /// <returns>`true` when the source range fits the profile slot reserve and record arena.</returns>
    private static bool TryBuildScalar8VarIdentityShelfFromSource(
        ReadOnlySpan<ulong> keys,
        Scalar8VarIdentitySplitIdentitySource identities,
        int start,
        int count,
        Scalar8VarIdentityProfile profile,
        out byte[] bytes)
    {
        bytes = new byte[profile.ShelfExtentSize];
        Scalar8VarIdentityLayout.Initialize(bytes, profile);
        int slotLength = checked(count * Scalar8VarIdentityLayout.SlotSize);
        int slotCapacityBytes = Scalar8VarIdentityLayout.ReadSlotCapacityBytes(bytes);
        if (slotLength > slotCapacityBytes)
        {
            return false;
        }

        int slotCursor = Scalar8VarIdentityLayout.HeaderSize;
        int recordCursor = Scalar8VarIdentityLayout.HeaderSize + slotCapacityBytes;
        for (int i = 0; i < count; i++)
        {
            int sourceIndex = start + i;
            ReadOnlySpan<byte> identity = identities.ReadIdentityAt(sourceIndex);
            if (identity.Length <= 0 || identity.Length > profile.MaxIdentityLength)
            {
                return false;
            }

            int recordLength = Scalar8VarIdentityLayout.GetNewRecordLength(identity.Length);
            if (recordCursor + recordLength > profile.ShelfExtentSize)
            {
                return false;
            }

            Scalar8VarIdentityLayout.WriteRecord(bytes, recordCursor, keys[sourceIndex], identity);
            Scalar8VarIdentityLayout.WriteSlotRecordOffset(bytes, slotCursor, recordCursor);
            Scalar8VarIdentityLayout.WriteSlotKeyPrefix(bytes, slotCursor, Scalar8VarIdentityLayout.CreateKeyPrefix(keys[sourceIndex]));
            recordCursor += recordLength;
            slotCursor += Scalar8VarIdentityLayout.SlotSize;
        }

        Scalar8VarIdentityLayout.WriteItemCount(bytes, count);
        Scalar8VarIdentityLayout.WriteSlotStreamLength(bytes, slotCursor - Scalar8VarIdentityLayout.HeaderSize);
        Scalar8VarIdentityLayout.WriteRecordArenaEnd(bytes, recordCursor);
        return true;
    }

    private void RepointRoute(long parentRouterOffset, int routeIndex, long targetOffset)
    {
        byte[] parentRouterBytes = new byte[RouterLayout.Size];
        kernel.Read(parentRouterOffset, parentRouterBytes);
        RouterReader parentReader = new(parentRouterBytes);
        if (!parentReader.IsValid)
        {
            throw new InvalidDataException("The route parent router is invalid.");
        }

        RawDataReservation parentRouterRewrite = kernel.ReserveAt(parentRouterOffset, RouterLayout.Size);
        parentRouterBytes.CopyTo(parentRouterRewrite.Span);
        RouterWriter parentWriter = new(parentRouterRewrite.Span);
        parentWriter.WriteRouteTarget(routeIndex, targetOffset);
    }

    /// <summary>
    /// Repoints every direct parent-router route that currently targets a full same-depth SV8 shelf.<br/>
    /// Prefixes below the selected split boundary point to the left replacement shelf; the boundary and higher prefixes point to the right replacement shelf.<br/>
    /// Routes owned by other shelves are preserved so the rewrite is local to the filled shelf's prefix run.<br/>
    /// </summary>
    /// <param name="parentRouterOffset">The parent router containing one-byte direct routes.</param>
    /// <param name="sourceShelfOffset">The full source shelf offset currently referenced by one or more routes.</param>
    /// <param name="selectedRightPrefix">The first prefix byte routed to the right replacement shelf.</param>
    /// <param name="leftShelfOffset">The left replacement shelf offset.</param>
    /// <param name="rightShelfOffset">The right replacement shelf offset.</param>
    private void RepointSameDepthRoutes(
        long parentRouterOffset,
        long sourceShelfOffset,
        byte selectedRightPrefix,
        long leftShelfOffset,
        long rightShelfOffset)
    {
        byte[] parentRouterBytes = new byte[RouterLayout.Size];
        kernel.Read(parentRouterOffset, parentRouterBytes);
        RouterReader parentReader = new(parentRouterBytes);
        if (!parentReader.IsValid || !parentReader.HasDirectIndex)
        {
            throw new InvalidDataException("The SV8 same-depth split parent router is invalid or not direct-index capable.");
        }

        RawDataReservation parentRouterRewrite = kernel.ReserveAt(parentRouterOffset, RouterLayout.Size);
        parentRouterBytes.CopyTo(parentRouterRewrite.Span);
        RouterWriter parentWriter = new(parentRouterRewrite.Span);
        int routeCount = parentReader.RouteCount;
        for (int routeIndex = 0; routeIndex < routeCount; routeIndex++)
        {
            if (parentReader.GetRouteTargetAt(routeIndex) != sourceShelfOffset)
            {
                continue;
            }

            long replacementOffset = routeIndex < selectedRightPrefix ? leftShelfOffset : rightShelfOffset;
            parentWriter.WriteRouteTarget(routeIndex, replacementOffset);
        }
    }

    private static int CompareScalar8VarIdentityTuple(ulong leftKey, ReadOnlySpan<byte> leftIdentity, ulong rightKey, ReadOnlySpan<byte> rightIdentity)
    {
        if (leftKey < rightKey)
        {
            return -1;
        }

        if (leftKey > rightKey)
        {
            return 1;
        }

        return Scalar8VarIdentityLayout.CompareIdentityBytes(leftIdentity, rightIdentity);
    }

    internal static byte GetScalar8VarIdentityPrefix(ulong encodedKey, int keyDepth)
    {
        return keyDepth >= 0 && keyDepth < sizeof(ulong)
            ? (byte)(encodedKey >> ((sizeof(ulong) - 1 - keyDepth) * 8))
            : (byte)0;
    }

    /// <summary>
    /// Converts the shared promoted direct-router target-kind cache value into the `SV8` route target enum.<br/>
    /// The promoted direct-router view is shape-neutral storage over router pages, but its target-kind sidecar was introduced with the scalar8-scalar8 route enum.<br/>
    /// Keeping the conversion explicit lets `SV8` reuse the same router projection without leaking shelf-shape assumptions into route walking.<br/>
    /// </summary>
    /// <param name="kind">The shared promoted direct-router target kind.</param>
    /// <returns>The equivalent `SV8` target kind.</returns>
    private static Scalar8VarIdentityRouteTargetKind ToScalar8VarIdentityRouteTargetKind(Scalar8Scalar8RouteTargetKind kind)
    {
        return kind switch
        {
            Scalar8Scalar8RouteTargetKind.Router => Scalar8VarIdentityRouteTargetKind.Router,
            Scalar8Scalar8RouteTargetKind.Shelf => Scalar8VarIdentityRouteTargetKind.Shelf,
            _ => Scalar8VarIdentityRouteTargetKind.None
        };
    }

    /// <summary>
    /// Converts an `SV8` target classification into the shared promoted direct-router target-kind cache value.<br/>
    /// Only the structural categories are cached: router, shelf, or none; the concrete shelf format is still validated by the `SV8` classifier before storing the value.<br/>
    /// </summary>
    /// <param name="kind">The `SV8` route target kind.</param>
    /// <returns>The shared promoted direct-router target kind.</returns>
    private static Scalar8Scalar8RouteTargetKind ToScalar8Scalar8RouteTargetKind(Scalar8VarIdentityRouteTargetKind kind)
    {
        return kind switch
        {
            Scalar8VarIdentityRouteTargetKind.Router => Scalar8Scalar8RouteTargetKind.Router,
            Scalar8VarIdentityRouteTargetKind.Shelf => Scalar8Scalar8RouteTargetKind.Shelf,
            _ => Scalar8Scalar8RouteTargetKind.None
        };
    }
}

internal readonly ref struct Scalar8VarIdentitySplitIdentitySource
{
    private readonly Scalar8VarIdentityMutableShelfView shelf;
    private readonly ReadOnlySpan<int> sourceSlots;
    private readonly int incomingIndex;
    private readonly ReadOnlySpan<byte> incomingIdentity;

    public Scalar8VarIdentitySplitIdentitySource(
        Scalar8VarIdentityMutableShelfView shelf,
        ReadOnlySpan<int> sourceSlots,
        int incomingIndex,
        ReadOnlySpan<byte> incomingIdentity)
    {
        this.shelf = shelf;
        this.sourceSlots = sourceSlots;
        this.incomingIndex = incomingIndex;
        this.incomingIdentity = incomingIdentity;
    }

    public ReadOnlySpan<byte> ReadIdentityAt(int index)
    {
        return index == incomingIndex ? incomingIdentity : shelf.ReadIdentityAt(sourceSlots[index]);
    }
}

internal readonly struct Scalar8VarIdentityIdentityRef
{
    private readonly byte[] bytes;
    private readonly int recordOffset;
    private readonly bool isIncoming;

    public Scalar8VarIdentityIdentityRef(byte[] bytes, int recordOffset, bool isIncoming)
    {
        this.bytes = bytes;
        this.recordOffset = recordOffset;
        this.isIncoming = isIncoming;
    }

    public ReadOnlySpan<byte> ReadIdentity(ReadOnlySpan<byte> incomingIdentity)
    {
        return isIncoming ? incomingIdentity : Scalar8VarIdentityLayout.ReadIdentity(bytes, recordOffset);
    }
}
