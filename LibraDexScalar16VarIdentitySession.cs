using LibraDex.Layouts;
using LibraDex.Views;
using System.Buffers.Binary;
using System.Diagnostics;

namespace LibraDex;

public sealed partial class LibraDexFileSession
{
    private const int DefaultScalar16VarIdentityMaxRouterHops = 8;

    internal Scalar16VarIdentityRouteTargetKind ClassifyScalar16VarIdentityRouteTarget(long targetOffset)
    {
        if (targetOffset <= 0)
        {
            return Scalar16VarIdentityRouteTargetKind.None;
        }

        Span<byte> header = stackalloc byte[Scalar16VarIdentityLayout.HeaderSize];
        kernel.Read(targetOffset, header);
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header[..sizeof(uint)]);
        if (magic == RouterLayout.Magic)
        {
            return Scalar16VarIdentityRouteTargetKind.Router;
        }

        if (magic == Scalar16VarIdentityLayout.Magic)
        {
            return Scalar16VarIdentityRouteTargetKind.Shelf;
        }

        return Scalar16VarIdentityRouteTargetKind.None;
    }

    internal (long ShelfOffset, DataKernelCommitTelemetry Commit) CreateScalar16VarIdentityShelfAndLinkRootRoute(
        long rootRouterOffset,
        byte prefixByte,
        Scalar16VarIdentityProfile profile)
    {
        byte[] existingRouter = new byte[RouterLayout.Size];
        kernel.Read(rootRouterOffset, existingRouter);
        RouterReader existingReader = new(existingRouter);
        if (!existingReader.IsValid || !existingReader.HasDirectIndex)
        {
            throw new InvalidDataException("The root router is invalid or not direct-index capable.");
        }

        byte[] shelfBytes = Scalar16VarIdentity.CreateEmpty(profile);
        RawDataReservation shelfReservation = kernel.Reserve(profile.ShelfExtentSize);
        shelfBytes.CopyTo(shelfReservation.Span);

        RawDataReservation routerReservation = kernel.ReserveAt(rootRouterOffset, RouterLayout.Size);
        existingRouter.CopyTo(routerReservation.Span);
        RouterWriter writer = new(routerReservation.Span);
        writer.WriteRoute(prefixByte, prefixByte, prefixByte, shelfReservation.Extent.Offset);

        DataKernelCommitTelemetry telemetry = CommitAndInvalidateRouterReadCache();
        return (shelfReservation.Extent.Offset, telemetry);
    }

    internal Scalar16VarIdentityRoutePathTarget WalkScalar16VarIdentityRoutePathTarget(
        long rootRouterOffset,
        ulong encodedKeyHigh,
        ulong encodedKeyLow,
        int maxRouterHops)
    {
        if (maxRouterHops <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRouterHops), maxRouterHops, "The SV16 maximum router hop count must be positive.");
        }

        Span<byte> routerSpan = stackalloc byte[RouterLayout.Size];
        long routerOffset = rootRouterOffset;
        for (int hop = 0; hop < maxRouterHops; hop++)
        {
            if (TryGetDirectRouterView(routerOffset, out DirectRouterView? directView))
            {
                byte directPrefixByte = GetScalar16VarIdentityPrefix(encodedKeyHigh, encodedKeyLow, directView!.KeyDepth);
                long directTargetOffset = directView.GetTarget(directPrefixByte);
                if (directTargetOffset == 0)
                {
                    throw new InvalidDataException("The routed SV16 target is unset.");
                }

                Scalar16VarIdentityRouteTargetKind directKind = ToScalar16VarIdentityRouteTargetKind(directView.GetTargetKind(directPrefixByte));
                if (directKind == Scalar16VarIdentityRouteTargetKind.None)
                {
                    directKind = ClassifyScalar16VarIdentityRouteTarget(directTargetOffset);
                    directView.SetTargetKind(directPrefixByte, ToScalar8Scalar8RouteTargetKind(directKind));
                }

                if (directKind == Scalar16VarIdentityRouteTargetKind.Shelf)
                {
                    return new Scalar16VarIdentityRoutePathTarget(
                        new Scalar16VarIdentityRouteTarget(directKind, directTargetOffset, directView.KeyDepth, directView.AllocationClassId),
                        routerOffset,
                        directPrefixByte,
                        directPrefixByte);
                }

                if (directKind == Scalar16VarIdentityRouteTargetKind.Router)
                {
                    routerOffset = directTargetOffset;
                    continue;
                }

                throw new InvalidDataException("The routed SV16 target is not a shelf or router.");
            }

            kernel.Read(routerOffset, routerSpan);
            RouterReader reader = new(routerSpan);
            if (!reader.IsValid)
            {
                throw new InvalidDataException("The routed SV16 router is invalid.");
            }

            byte prefixByte = GetScalar16VarIdentityPrefix(encodedKeyHigh, encodedKeyLow, reader.KeyDepth);
            long targetOffset = reader.FindTarget(prefixByte, out int routeIndex);
            if (targetOffset == 0)
            {
                throw new InvalidDataException("The routed SV16 target is unset.");
            }

            Scalar16VarIdentityRouteTargetKind kind = ClassifyScalar16VarIdentityRouteTarget(targetOffset);
            if (kind == Scalar16VarIdentityRouteTargetKind.Shelf)
            {
                return new Scalar16VarIdentityRoutePathTarget(
                    new Scalar16VarIdentityRouteTarget(kind, targetOffset, reader.KeyDepth, reader.AllocationClassId),
                    routerOffset,
                    prefixByte,
                    routeIndex);
            }

            if (kind == Scalar16VarIdentityRouteTargetKind.Router)
            {
                routerOffset = targetOffset;
                continue;
            }

            throw new InvalidDataException("The routed SV16 target is not a shelf or router.");
        }

        throw new InvalidDataException("The routed SV16 target walk exceeded the configured maximum router hop count.");
    }

    internal Scalar16VarIdentityRoutedInsertResult InsertWalkedRoutedScalar16VarIdentity(
        long rootRouterOffset,
        int maxIdentityLength,
        ulong encodedKeyHigh,
        ulong encodedKeyLow,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys,
        int maxRouterHops)
    {
        return InsertWalkedRoutedScalar16VarIdentity(
            rootRouterOffset,
            maxIdentityLength,
            encodedKeyHigh,
            encodedKeyLow,
            identity,
            allowDuplicateKeys,
            maxRouterHops,
            collectAttribution: false,
            out _);
    }

    /// <summary>
    /// Inserts one tuple through the walked routed `SV16` path while returning lower-layer elapsed-time attribution.<br/>
    /// The attribution overload is intended for harness diagnostics so normal callers can use the lower-overhead overload above.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The offset of the root router that owns the scalar-key route table.</param>
    /// <param name="maxIdentityLength">The maximum raw identity length accepted by this `SV16` profile family.</param>
    /// <param name="encodedKeyHigh">The sortable scalar key high half.</param>
    /// <param name="encodedKeyLow">The sortable scalar key low half.</param>
    /// <param name="identity">The raw variable-length identity bytes to insert.</param>
    /// <param name="allowDuplicateKeys">True when duplicate keys are allowed and identity bytes provide stable duplicate ordering.</param>
    /// <param name="maxRouterHops">The maximum router hops allowed before treating the route as malformed.</param>
    /// <param name="attribution">The elapsed-time buckets captured during the walked write.</param>
    /// <returns>The routed insert result including structural mutation kind and commit telemetry.</returns>
    internal Scalar16VarIdentityRoutedInsertResult InsertWalkedRoutedScalar16VarIdentity(
        long rootRouterOffset,
        int maxIdentityLength,
        ulong encodedKeyHigh,
        ulong encodedKeyLow,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys,
        int maxRouterHops,
        out Scalar16VarIdentityWalkedWriteAttribution attribution)
    {
        return InsertWalkedRoutedScalar16VarIdentity(
            rootRouterOffset,
            maxIdentityLength,
            encodedKeyHigh,
            encodedKeyLow,
            identity,
            allowDuplicateKeys,
            maxRouterHops,
            collectAttribution: true,
            out attribution);
    }

    private Scalar16VarIdentityRoutedInsertResult InsertWalkedRoutedScalar16VarIdentity(
        long rootRouterOffset,
        int maxIdentityLength,
        ulong encodedKeyHigh,
        ulong encodedKeyLow,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys,
        int maxRouterHops,
        bool collectAttribution,
        out Scalar16VarIdentityWalkedWriteAttribution attribution)
    {
        long routeWalkTicks = 0;
        long shelfReadTicks = 0;
        long duplicateChainTicks = 0;
        long mutationTicks = 0;
        long stageTicks = 0;
        long structuralTicks = 0;
        long started = collectAttribution ? Stopwatch.GetTimestamp() : 0;
        Scalar16VarIdentityRoutePathTarget pathTarget = WalkScalar16VarIdentityRoutePathTarget(rootRouterOffset, encodedKeyHigh, encodedKeyLow, maxRouterHops);
        if (collectAttribution)
        {
            routeWalkTicks += Stopwatch.GetTimestamp() - started;
        }

        Scalar16VarIdentityRouteTarget target = pathTarget.Target;
        if (target.Kind != Scalar16VarIdentityRouteTargetKind.Shelf)
        {
            throw new InvalidDataException("The classified route walker did not terminate at an SV16 shelf.");
        }

        started = collectAttribution ? Stopwatch.GetTimestamp() : 0;
        Scalar16VarIdentityMutableShelfView mutableShelf = ReadScalar16VarIdentityMutableShelf(target.Offset, maxIdentityLength);
        if (collectAttribution)
        {
            shelfReadTicks += Stopwatch.GetTimestamp() - started;
        }

        byte[] shelfBytes = mutableShelf.Bytes;
        Scalar16VarIdentityProfile profile = mutableShelf.Profile;
        int beforeItemCount = mutableShelf.ItemCount;
        started = collectAttribution ? Stopwatch.GetTimestamp() : 0;
        if (Scalar16VarIdentityLayout.ReadNextShelfOffset(mutableShelf.Bytes) != 0 &&
            TryInsertIntoScalar16VarIdentityDuplicateRunChain(
                target.Offset,
                mutableShelf.Bytes,
                profile,
                encodedKeyHigh,
                encodedKeyLow,
                identity,
                allowDuplicateKeys,
                out Scalar16VarIdentityRoutedInsertResult chainResult))
        {
            if (collectAttribution)
            {
                duplicateChainTicks += Stopwatch.GetTimestamp() - started;
            }

            attribution = new Scalar16VarIdentityWalkedWriteAttribution(routeWalkTicks, shelfReadTicks, duplicateChainTicks, mutationTicks, stageTicks, structuralTicks);
            return chainResult;
        }

        if (collectAttribution)
        {
            duplicateChainTicks += Stopwatch.GetTimestamp() - started;
        }

        Scalar16VarIdentityInsertResult insertResult;
        byte[] rewrittenBytes;
        started = collectAttribution ? Stopwatch.GetTimestamp() : 0;
        if (durabilityBatchActive)
        {
            insertResult = mutableShelf.Insert(encodedKeyHigh, encodedKeyLow, identity, allowDuplicateKeys);
            rewrittenBytes = mutableShelf.Bytes;
        }
        else
        {
            insertResult = Scalar16VarIdentity.Insert(
                shelfBytes,
                profile,
                encodedKeyHigh,
                encodedKeyLow,
                identity,
                allowDuplicateKeys,
                out rewrittenBytes);
        }
        if (collectAttribution)
        {
            mutationTicks += Stopwatch.GetTimestamp() - started;
        }

        if (insertResult == Scalar16VarIdentityInsertResult.Inserted)
        {
            started = collectAttribution ? Stopwatch.GetTimestamp() : 0;
            DataKernelCommitTelemetry telemetry = StageScalar16VarIdentityShelfRewrite(target.Offset, mutableShelf, rewrittenBytes);
            if (collectAttribution)
            {
                stageTicks += Stopwatch.GetTimestamp() - started;
            }

            attribution = new Scalar16VarIdentityWalkedWriteAttribution(routeWalkTicks, shelfReadTicks, duplicateChainTicks, mutationTicks, stageTicks, structuralTicks);
            return new Scalar16VarIdentityRoutedInsertResult(
                Scalar16VarIdentityRoutedInsertKind.WalkedNoSplit,
                insertResult,
                target.Offset,
                target.Offset,
                telemetry,
                beforeItemCount + 1,
                profile.ShelfExtentSize,
                target.RouterDepth);
        }

        if (insertResult == Scalar16VarIdentityInsertResult.AlreadyPresent ||
            insertResult == Scalar16VarIdentityInsertResult.KeyConflict)
        {
            attribution = new Scalar16VarIdentityWalkedWriteAttribution(routeWalkTicks, shelfReadTicks, duplicateChainTicks, mutationTicks, stageTicks, structuralTicks);
            return new Scalar16VarIdentityRoutedInsertResult(
                insertResult == Scalar16VarIdentityInsertResult.KeyConflict ? Scalar16VarIdentityRoutedInsertKind.KeyConflict : Scalar16VarIdentityRoutedInsertKind.NoOp,
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
        if (insertResult == Scalar16VarIdentityInsertResult.Full &&
            Scalar16VarIdentity.TryGrowAndInsert(mutableShelf.Bytes, profile, encodedKeyHigh, encodedKeyLow, identity, allowDuplicateKeys, out Scalar16VarIdentityProfile grownProfile, out byte[] grownShelf, out Scalar16VarIdentityInsertResult grownResult))
        {
            if (grownResult != Scalar16VarIdentityInsertResult.Inserted)
            {
                if (collectAttribution)
                {
                    structuralTicks += Stopwatch.GetTimestamp() - started;
                }

                attribution = new Scalar16VarIdentityWalkedWriteAttribution(routeWalkTicks, shelfReadTicks, duplicateChainTicks, mutationTicks, stageTicks, structuralTicks);
                return new Scalar16VarIdentityRoutedInsertResult(
                    grownResult == Scalar16VarIdentityInsertResult.KeyConflict ? Scalar16VarIdentityRoutedInsertKind.KeyConflict : Scalar16VarIdentityRoutedInsertKind.NoOp,
                    grownResult,
                    target.Offset,
                    0,
                    default,
                    beforeItemCount,
                    profile.ShelfExtentSize,
                    target.RouterDepth);
            }

            scalar16VarIdentityMutableBatchShelves.Remove(target.Offset);
            RawDataReservation grownReservation = kernel.Reserve(grownProfile.ShelfExtentSize);
            grownShelf.CopyTo(grownReservation.Span);
            RepointRoute(pathTarget.ParentRouterOffset, pathTarget.RouteIndex, grownReservation.Extent.Offset);
            DataKernelCommitTelemetry telemetry = CommitAndInvalidateRouterReadCache();
            if (collectAttribution)
            {
                structuralTicks += Stopwatch.GetTimestamp() - started;
            }

            attribution = new Scalar16VarIdentityWalkedWriteAttribution(routeWalkTicks, shelfReadTicks, duplicateChainTicks, mutationTicks, stageTicks, structuralTicks);
            return new Scalar16VarIdentityRoutedInsertResult(
                Scalar16VarIdentityRoutedInsertKind.WalkedGrow,
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
        if (insertResult == Scalar16VarIdentityInsertResult.Full &&
            TrySplitScalar16VarIdentityShelf(
                mutableShelf,
                profile,
                target.RouterDepth,
                encodedKeyHigh,
                encodedKeyLow,
                identity,
                allowDuplicateKeys,
                out ushort splitDepth,
                out byte selectedRightPrefix,
                out byte[] leftShelf,
                out byte[] rightShelf,
                out int leftCount,
                out int rightCount,
                out Scalar16VarIdentityInsertResult splitInsertResult))
        {
            scalar16VarIdentityMutableBatchShelves.Remove(target.Offset);
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

            attribution = new Scalar16VarIdentityWalkedWriteAttribution(routeWalkTicks, shelfReadTicks, duplicateChainTicks, mutationTicks, stageTicks, structuralTicks);
            return new Scalar16VarIdentityRoutedInsertResult(
                Scalar16VarIdentityRoutedInsertKind.WalkedShelfSplit,
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
        if (insertResult == Scalar16VarIdentityInsertResult.Full &&
            TrySplitScalar16VarIdentityShelfAtDepth(
                mutableShelf,
                profile,
                target.RouterDepth,
                encodedKeyHigh,
                encodedKeyLow,
                identity,
                allowDuplicateKeys,
                out selectedRightPrefix,
                out leftShelf,
                out rightShelf,
                out leftCount,
                out rightCount,
                out splitInsertResult))
        {
            scalar16VarIdentityMutableBatchShelves.Remove(target.Offset);
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

            attribution = new Scalar16VarIdentityWalkedWriteAttribution(routeWalkTicks, shelfReadTicks, duplicateChainTicks, mutationTicks, stageTicks, structuralTicks);
            return new Scalar16VarIdentityRoutedInsertResult(
                Scalar16VarIdentityRoutedInsertKind.WalkedShelfSplit,
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
        if (insertResult == Scalar16VarIdentityInsertResult.Full &&
            TryAppendScalar16VarIdentityDuplicateRunTail(
                target.Offset,
                target.Offset,
                mutableShelf.Bytes,
                profile,
                encodedKeyHigh,
                encodedKeyLow,
                identity,
                allowDuplicateKeys,
                out Scalar16VarIdentityRoutedInsertResult appendedTailResult))
        {
            if (collectAttribution)
            {
                structuralTicks += Stopwatch.GetTimestamp() - started;
            }

            attribution = new Scalar16VarIdentityWalkedWriteAttribution(routeWalkTicks, shelfReadTicks, duplicateChainTicks, mutationTicks, stageTicks, structuralTicks);
            return appendedTailResult;
        }
        if (collectAttribution)
        {
            structuralTicks += Stopwatch.GetTimestamp() - started;
        }

        started = collectAttribution ? Stopwatch.GetTimestamp() : 0;
        mutableShelf.EnsureSlotBytesCurrent();
        if (insertResult == Scalar16VarIdentityInsertResult.Full &&
            TryRewriteScalar16VarIdentityDuplicateRunChain(
                target.Offset,
                mutableShelf.Bytes,
                profile,
                encodedKeyHigh,
                encodedKeyLow,
                identity,
                allowDuplicateKeys,
                out long tailShelfOffset,
                out int chainShelfCount,
                out int largestShelfItemCount,
                out Scalar16VarIdentityInsertResult chainInsertResult,
                out DataKernelCommitTelemetry chainTelemetry))
        {
            if (collectAttribution)
            {
                structuralTicks += Stopwatch.GetTimestamp() - started;
            }

            attribution = new Scalar16VarIdentityWalkedWriteAttribution(routeWalkTicks, shelfReadTicks, duplicateChainTicks, mutationTicks, stageTicks, structuralTicks);
            return new Scalar16VarIdentityRoutedInsertResult(
                Scalar16VarIdentityRoutedInsertKind.WalkedDuplicateRunOverflow,
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

        attribution = new Scalar16VarIdentityWalkedWriteAttribution(routeWalkTicks, shelfReadTicks, duplicateChainTicks, mutationTicks, stageTicks, structuralTicks);
        return new Scalar16VarIdentityRoutedInsertResult(
            insertResult == Scalar16VarIdentityInsertResult.Invalid ? Scalar16VarIdentityRoutedInsertKind.Invalid : Scalar16VarIdentityRoutedInsertKind.Full,
            insertResult,
            target.Offset,
            0,
            default,
            beforeItemCount,
            profile.ShelfExtentSize,
            target.RouterDepth);
    }

    internal Scalar16VarIdentityRangeReader OpenScalar16VarIdentityRangeReader(
        long rootRouterOffset,
        int maxIdentityLength,
        ulong lowerEncodedKeyHigh,
        ulong lowerEncodedKeyLow,
        ulong upperEncodedKeyHigh,
        ulong upperEncodedKeyLow)
    {
        if (CompareScalar16VarIdentityKey(lowerEncodedKeyHigh, lowerEncodedKeyLow, upperEncodedKeyHigh, upperEncodedKeyLow) > 0)
        {
            throw new ArgumentException("The upper SV16 key must be greater than or equal to the lower key.", nameof(upperEncodedKeyHigh));
        }

        return new Scalar16VarIdentityRangeReader(
            this,
            rootRouterOffset,
            maxIdentityLength,
            lowerEncodedKeyHigh,
            lowerEncodedKeyLow,
            upperEncodedKeyHigh,
            upperEncodedKeyLow);
    }

    internal byte[] ReadScalar16VarIdentityShelfBytes(long shelfOffset, int maxIdentityLength, out Scalar16VarIdentityProfile profile)
    {
        byte[] header = new byte[Scalar16VarIdentityLayout.HeaderSize];
        kernel.Read(shelfOffset, header);
        if (Scalar16VarIdentityLayout.ReadMagic(header) != Scalar16VarIdentityLayout.Magic ||
            Scalar16VarIdentityLayout.ReadFormatVersion(header) != Scalar16VarIdentityLayout.FormatVersion ||
            Scalar16VarIdentityLayout.ReadHeaderSize(header) != Scalar16VarIdentityLayout.HeaderSize)
        {
            throw new InvalidDataException("The routed SV16 shelf header is invalid.");
        }

        int shelfExtentSize = Scalar16VarIdentityLayout.ReadShelfExtentSize(header);
        profile = Scalar16VarIdentityProfile.Create(shelfExtentSize, maxIdentityLength);
        byte[] shelfBytes = new byte[shelfExtentSize];
        kernel.Read(shelfOffset, shelfBytes);
        return shelfBytes;
    }

    /// <summary>
    /// Reads one `SV16` shelf as a session-owned mutable batch image when a durability batch is active.<br/>
    /// Reusing the same disk-shaped byte buffer avoids repeated shelf reads and full-image clone allocation across ordinary insert runs.<br/>
    /// Non-batch callers receive a temporary mutable wrapper that is staged immediately by the insert path.<br/>
    /// </summary>
    /// <param name="shelfOffset">The file offset of the shelf to read.</param>
    /// <param name="maxIdentityLength">The maximum raw identity length accepted by the profile family.</param>
    /// <returns>The mutable `SV16` batch shelf image.</returns>
    private Scalar16VarIdentityMutableShelfView ReadScalar16VarIdentityMutableShelf(long shelfOffset, int maxIdentityLength)
    {
        if (durabilityBatchActive &&
            scalar16VarIdentityMutableBatchShelves.TryGetValue(shelfOffset, out Scalar16VarIdentityMutableShelfView? mutableShelf))
        {
            return mutableShelf;
        }

        byte[] shelfBytes = ReadScalar16VarIdentityShelfBytes(shelfOffset, maxIdentityLength, out Scalar16VarIdentityProfile profile);
        if (!Scalar16VarIdentityMutableShelfView.TryCreate(shelfBytes, profile, out Scalar16VarIdentityMutableShelfView shelf))
        {
            throw new InvalidDataException("The routed SV16 mutable shelf bytes are invalid.");
        }

        if (durabilityBatchActive)
        {
            scalar16VarIdentityMutableBatchShelves[shelfOffset] = shelf;
        }

        return shelf;
    }

    /// <summary>
    /// Stages an `SV16` shelf rewrite, deferring the actual DataKernel copy when inside a durability batch.<br/>
    /// If insertion reused the mutable shelf image, the method marks that image dirty; if insertion returned a compacted replacement image, the batch cache is replaced with that image.<br/>
    /// Non-batch callers stage and publish immediately to preserve existing command semantics.<br/>
    /// </summary>
    /// <param name="shelfOffset">The shelf offset being rewritten.</param>
    /// <param name="mutableShelf">The current mutable shelf wrapper.</param>
    /// <param name="rewrittenBytes">The authoritative rewritten shelf bytes.</param>
    /// <returns>Commit telemetry for the immediate or deferred commit request.</returns>
    private DataKernelCommitTelemetry StageScalar16VarIdentityShelfRewrite(
        long shelfOffset,
        Scalar16VarIdentityMutableShelfView mutableShelf,
        byte[] rewrittenBytes)
    {
        if (rewrittenBytes.Length < mutableShelf.Profile.ShelfExtentSize)
        {
            throw new ArgumentException("The SV16 shelf rewrite image must match the profiled shelf extent size.", nameof(rewrittenBytes));
        }

        if (durabilityBatchActive)
        {
            if (ReferenceEquals(rewrittenBytes, mutableShelf.Bytes))
            {
                mutableShelf.MarkDirty();
            }
            else
            {
                if (!Scalar16VarIdentityMutableShelfView.TryCreate(rewrittenBytes, mutableShelf.Profile, out Scalar16VarIdentityMutableShelfView replacement))
                {
                    throw new InvalidDataException("The replacement SV16 mutable shelf bytes are invalid.");
                }

                replacement.MarkDirty();
                scalar16VarIdentityMutableBatchShelves[shelfOffset] = replacement;
            }

            return CommitWithoutInvalidatingRouterReadCache();
        }

        RawDataReservation shelfRewrite = kernel.ReserveAt(shelfOffset, mutableShelf.Profile.ShelfExtentSize);
        rewrittenBytes.AsSpan(0, mutableShelf.Profile.ShelfExtentSize).CopyTo(shelfRewrite.Span);
        return CommitAndInvalidateRouterReadCache();
    }

    private static bool TrySplitScalar16VarIdentityShelf(
        Scalar16VarIdentityMutableShelfView mutableShelf,
        Scalar16VarIdentityProfile profile,
        ushort currentRouterDepth,
        ulong incomingKeyHigh,
        ulong incomingKeyLow,
        ReadOnlySpan<byte> incomingIdentity,
        bool allowDuplicateKeys,
        out ushort splitDepth,
        out byte selectedRightPrefix,
        out byte[] leftShelf,
        out byte[] rightShelf,
        out int leftCount,
        out int rightCount,
        out Scalar16VarIdentityInsertResult insertResult)
    {
        splitDepth = 0;
        selectedRightPrefix = 0;
        leftShelf = [];
        rightShelf = [];
        leftCount = 0;
        rightCount = 0;
        insertResult = Scalar16VarIdentityInsertResult.Full;
        if (mutableShelf.Profile.ShelfExtentSize != profile.ShelfExtentSize)
        {
            insertResult = Scalar16VarIdentityInsertResult.Invalid;
            return false;
        }

        int totalCount = checked(mutableShelf.ItemCount + 1);
        ulong[] keyHighs = new ulong[totalCount];
        ulong[] keyLows = new ulong[totalCount];
        int[] sourceSlots = new int[totalCount];
        int incomingIndex = -1;
        bool inserted = false;
        int targetIndex = 0;
        for (int sourceIndex = 0; sourceIndex < mutableShelf.ItemCount; sourceIndex++)
        {
            ulong currentKeyHigh = mutableShelf.ReadKeyHighAt(sourceIndex);
            ulong currentKeyLow = mutableShelf.ReadKeyLowAt(sourceIndex);
            ReadOnlySpan<byte> currentIdentity = mutableShelf.ReadIdentityAt(sourceIndex);
            if (!inserted && CompareScalar16VarIdentityTuple(currentKeyHigh, currentKeyLow, currentIdentity, incomingKeyHigh, incomingKeyLow, incomingIdentity) > 0)
            {
                keyHighs[targetIndex] = incomingKeyHigh;
                keyLows[targetIndex] = incomingKeyLow;
                sourceSlots[targetIndex] = -1;
                incomingIndex = targetIndex;
                targetIndex++;
                inserted = true;
            }

            if (currentKeyHigh == incomingKeyHigh &&
                currentKeyLow == incomingKeyLow &&
                Scalar16VarIdentityLayout.IdentityBytesEqual(currentIdentity, incomingIdentity))
            {
                insertResult = Scalar16VarIdentityInsertResult.AlreadyPresent;
                return false;
            }

            if (!allowDuplicateKeys && currentKeyHigh == incomingKeyHigh && currentKeyLow == incomingKeyLow)
            {
                insertResult = Scalar16VarIdentityInsertResult.KeyConflict;
                return false;
            }

            keyHighs[targetIndex] = currentKeyHigh;
            keyLows[targetIndex] = currentKeyLow;
            sourceSlots[targetIndex] = sourceIndex;
            targetIndex++;
        }

        if (!inserted)
        {
            keyHighs[targetIndex] = incomingKeyHigh;
            keyLows[targetIndex] = incomingKeyLow;
            sourceSlots[targetIndex] = -1;
            incomingIndex = targetIndex;
        }

        Scalar16VarIdentitySplitIdentitySource identitySource = new(mutableShelf, sourceSlots, incomingIndex, incomingIdentity);
        int firstSplitDepth = FindFirstDifferingScalar16VarIdentityDepth(keyHighs, keyLows, currentRouterDepth + 1);
        for (int depth = firstSplitDepth; depth >= 0 && depth < Scalar16VarIdentityLayout.KeySize; depth++)
        {
            if (TryBuildScalar16VarIdentitySplitAtDepth(keyHighs, keyLows, identitySource, profile, checked((ushort)depth), out selectedRightPrefix, out leftShelf, out rightShelf, out leftCount, out rightCount))
            {
                splitDepth = checked((ushort)depth);
                insertResult = Scalar16VarIdentityInsertResult.Inserted;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Attempts an incremental insert into an existing SV16 same-key duplicate-run chain.<br/>
    /// The method scans linked shelves in identity order, rejects duplicate tuples, and rewrites only the target shelf when it has space.<br/>
    /// If the target shelf is full or the chain is not a pure same-key run, the caller falls back to the full chain rebuild path.<br/>
    /// </summary>
    /// <param name="headShelfOffset">The route-owned head shelf offset.</param>
    /// <param name="headShelfBytes">The already-read head shelf bytes.</param>
    /// <param name="profile">The SV16 profile used by every chain segment.</param>
    /// <param name="encodedKey">The encoded scalar key being inserted.</param>
    /// <param name="identity">The raw identity being inserted.</param>
    /// <param name="allowDuplicateKeys">Whether duplicate scalar keys are allowed.</param>
    /// <param name="result">Receives the routed insert result when the chain handled the insert.</param>
    /// <returns>`true` when the existing chain produced a terminal insert/no-op/conflict result.</returns>
    private bool TryInsertIntoScalar16VarIdentityDuplicateRunChain(
        long headShelfOffset,
        byte[] headShelfBytes,
        Scalar16VarIdentityProfile profile,
        ulong encodedKeyHigh,
        ulong encodedKeyLow,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys,
        out Scalar16VarIdentityRoutedInsertResult result)
    {
        result = default;
        if (!allowDuplicateKeys)
        {
            result = new Scalar16VarIdentityRoutedInsertResult(
                Scalar16VarIdentityRoutedInsertKind.KeyConflict,
                Scalar16VarIdentityInsertResult.KeyConflict,
                headShelfOffset,
                0,
                default,
                0,
                profile.ShelfExtentSize);
            return true;
        }

        using RouteVisitedOffsetSet visited = RouteVisitedOffsetSet.Rent();
        long fastTailOffset = Scalar16VarIdentityLayout.ReadTailShelfOffset(headShelfBytes);
        if (fastTailOffset != 0 &&
            fastTailOffset != headShelfOffset &&
            TryInsertIntoScalar16VarIdentityDuplicateRunTailFast(
                headShelfOffset,
                fastTailOffset,
                profile,
                encodedKeyHigh,
                encodedKeyLow,
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
                throw new InvalidDataException("The SV16 duplicate-run shelf chain contains a cycle.");
            }

            Scalar16VarIdentityReadOnly shelf = new(currentBytes, profile);
            if (!shelf.IsValid)
            {
                result = new Scalar16VarIdentityRoutedInsertResult(
                    Scalar16VarIdentityRoutedInsertKind.Invalid,
                    Scalar16VarIdentityInsertResult.Invalid,
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
                if (shelf.ReadKeyHighAt(i) != encodedKeyHigh || shelf.ReadKeyLowAt(i) != encodedKeyLow)
                {
                    return false;
                }

                if (Scalar16VarIdentityLayout.IdentityBytesEqual(shelf.ReadIdentityAt(i), identity))
                {
                    result = new Scalar16VarIdentityRoutedInsertResult(
                        Scalar16VarIdentityRoutedInsertKind.NoOp,
                        Scalar16VarIdentityInsertResult.AlreadyPresent,
                        currentOffset,
                        0,
                        default,
                        shelf.ItemCount,
                        profile.ShelfExtentSize);
                    return true;
                }
            }

            long nextOffset = Scalar16VarIdentityLayout.ReadNextShelfOffset(currentBytes);
            ReadOnlySpan<byte> lastIdentity = shelf.ReadIdentityAt(shelf.ItemCount - 1);
            if (Scalar16VarIdentityLayout.CompareIdentityBytes(identity, lastIdentity) <= 0 || nextOffset == 0)
            {
                Scalar16VarIdentityInsertResult insertResult = Scalar16VarIdentity.Insert(
                    currentBytes,
                    profile,
                    encodedKeyHigh,
                    encodedKeyLow,
                    identity,
                    allowDuplicateKeys: true,
                    out byte[] rewrittenBytes);
                if (insertResult == Scalar16VarIdentityInsertResult.Full)
                {
                    if (nextOffset == 0 &&
                        Scalar16VarIdentityLayout.CompareIdentityBytes(identity, lastIdentity) > 0 &&
                        TryAppendScalar16VarIdentityDuplicateRunTail(
                            headShelfOffset,
                            currentOffset,
                            currentBytes,
                            profile,
                            encodedKeyHigh,
                            encodedKeyLow,
                            identity,
                            allowDuplicateKeys: true,
                            out result))
                    {
                        return true;
                    }

                    return false;
                }

                if (insertResult == Scalar16VarIdentityInsertResult.Inserted)
                {
                    Scalar16VarIdentityLayout.WriteNextShelfOffset(rewrittenBytes, nextOffset);
                    RawDataReservation rewrite = kernel.ReserveAt(currentOffset, profile.ShelfExtentSize);
                    rewrittenBytes.CopyTo(rewrite.Span);
                    DataKernelCommitTelemetry telemetry = CommitWithoutInvalidatingRouterReadCache();
                    result = new Scalar16VarIdentityRoutedInsertResult(
                        Scalar16VarIdentityRoutedInsertKind.WalkedDuplicateRunOverflow,
                        insertResult,
                        currentOffset,
                        currentOffset,
                        telemetry,
                        shelf.ItemCount + 1,
                        profile.ShelfExtentSize);
                    return true;
                }

                result = new Scalar16VarIdentityRoutedInsertResult(
                    insertResult == Scalar16VarIdentityInsertResult.KeyConflict ? Scalar16VarIdentityRoutedInsertKind.KeyConflict : Scalar16VarIdentityRoutedInsertKind.NoOp,
                    insertResult,
                    currentOffset,
                    0,
                    default,
                    shelf.ItemCount,
                    profile.ShelfExtentSize);
                return true;
            }

            currentOffset = nextOffset;
            currentBytes = ReadScalar16VarIdentityShelfBytes(currentOffset, profile.MaxIdentityLength, out Scalar16VarIdentityProfile nextProfile);
            if (nextProfile.ShelfExtentSize != profile.ShelfExtentSize)
            {
                throw new InvalidDataException("The SV16 duplicate-run shelf chain changed shelf extent sizes.");
            }
        }

        return false;
    }

    /// <summary>
    /// Attempts to insert directly into the known terminal shelf of an SV16 duplicate-run chain.<br/>
    /// The head shelf stores the tail offset as a fast-path hint, so sorted same-key identity inserts avoid walking earlier chain segments.<br/>
    /// If the incoming identity does not sort after the current tail, the caller falls back to the full chain walk.<br/>
    /// </summary>
    /// <param name="headShelfOffset">The route-owned chain head shelf offset.</param>
    /// <param name="tailShelfOffset">The known terminal shelf offset.</param>
    /// <param name="profile">The SV16 profile used by the tail shelf.</param>
    /// <param name="encodedKey">The encoded scalar key being inserted.</param>
    /// <param name="identity">The raw identity being inserted.</param>
    /// <param name="result">Receives the routed insert result when the tail fast path handles the insert.</param>
    /// <returns>`true` when the tail fast path produced a terminal result.</returns>
    private bool TryInsertIntoScalar16VarIdentityDuplicateRunTailFast(
        long headShelfOffset,
        long tailShelfOffset,
        Scalar16VarIdentityProfile profile,
        ulong encodedKeyHigh,
        ulong encodedKeyLow,
        ReadOnlySpan<byte> identity,
        out Scalar16VarIdentityRoutedInsertResult result)
    {
        Scalar16VarIdentityMutableShelfView tailMutableShelf = ReadScalar16VarIdentityMutableShelf(tailShelfOffset, profile.MaxIdentityLength);
        if (tailMutableShelf.Profile.ShelfExtentSize != profile.ShelfExtentSize)
        {
            throw new InvalidDataException("The SV16 duplicate-run tail changed shelf extent sizes.");
        }

        byte[] tailBytes = tailMutableShelf.Bytes;
        if (tailMutableShelf.ItemCount == 0)
        {
            result = new Scalar16VarIdentityRoutedInsertResult(
                Scalar16VarIdentityRoutedInsertKind.Invalid,
                Scalar16VarIdentityInsertResult.Invalid,
                tailShelfOffset,
                0,
                default,
                0,
                profile.ShelfExtentSize);
            return true;
        }

        if (tailMutableShelf.ReadKeyHighAt(0) != encodedKeyHigh ||
            tailMutableShelf.ReadKeyLowAt(0) != encodedKeyLow ||
            tailMutableShelf.ReadKeyHighAt(tailMutableShelf.ItemCount - 1) != encodedKeyHigh ||
            tailMutableShelf.ReadKeyLowAt(tailMutableShelf.ItemCount - 1) != encodedKeyLow)
        {
            result = default;
            return false;
        }

        if (Scalar16VarIdentityLayout.CompareIdentityBytes(identity, tailMutableShelf.ReadIdentityAt(tailMutableShelf.ItemCount - 1)) <= 0)
        {
            result = default;
            return false;
        }

        Scalar16VarIdentityInsertResult insertResult;
        byte[] rewrittenTail;
        if (durabilityBatchActive)
        {
            insertResult = tailMutableShelf.Insert(encodedKeyHigh, encodedKeyLow, identity, allowDuplicateKeys: true);
            rewrittenTail = tailMutableShelf.Bytes;
        }
        else
        {
            insertResult = Scalar16VarIdentity.Insert(
                tailBytes,
                profile,
                encodedKeyHigh,
                encodedKeyLow,
                identity,
                allowDuplicateKeys: true,
                out rewrittenTail);
        }

        if (insertResult == Scalar16VarIdentityInsertResult.Inserted)
        {
            DataKernelCommitTelemetry telemetry = StageScalar16VarIdentityShelfRewrite(tailShelfOffset, tailMutableShelf, rewrittenTail);
            result = new Scalar16VarIdentityRoutedInsertResult(
                Scalar16VarIdentityRoutedInsertKind.WalkedDuplicateRunOverflow,
                insertResult,
                tailShelfOffset,
                tailShelfOffset,
                telemetry,
                tailMutableShelf.ItemCount,
                profile.ShelfExtentSize);
            return true;
        }

        if (insertResult == Scalar16VarIdentityInsertResult.Full)
        {
            tailMutableShelf.EnsureSlotBytesCurrent();
            scalar16VarIdentityMutableBatchShelves.Remove(tailShelfOffset);
            return TryAppendScalar16VarIdentityDuplicateRunTail(
                headShelfOffset,
                tailShelfOffset,
                tailBytes,
                profile,
                encodedKeyHigh,
                encodedKeyLow,
                identity,
                allowDuplicateKeys: true,
                out result);
        }

        result = new Scalar16VarIdentityRoutedInsertResult(
            insertResult == Scalar16VarIdentityInsertResult.KeyConflict ? Scalar16VarIdentityRoutedInsertKind.KeyConflict : Scalar16VarIdentityRoutedInsertKind.NoOp,
            insertResult,
            tailShelfOffset,
            0,
            default,
            tailMutableShelf.ItemCount,
            profile.ShelfExtentSize);
        return true;
    }

    /// <summary>
    /// Appends a new terminal shelf to an SV16 same-key duplicate-run chain when the incoming identity sorts after the current terminal shelf.<br/>
    /// This avoids full-chain rebuilds for sorted same-key identity loads, which are the common append-like case for path/blob identities under one scalar property key.<br/>
    /// The method validates that the current shelf is a pure same-key segment before linking the new shelf.<br/>
    /// </summary>
    /// <param name="headShelfOffset">The route-owned chain head shelf offset whose tail pointer should be maintained.</param>
    /// <param name="terminalShelfOffset">The shelf offset that should become the predecessor of the new tail shelf.</param>
    /// <param name="terminalShelfBytes">The current terminal shelf bytes.</param>
    /// <param name="profile">The SV16 profile used by both shelves.</param>
    /// <param name="encodedKey">The encoded scalar key being inserted.</param>
    /// <param name="identity">The raw identity being appended.</param>
    /// <param name="allowDuplicateKeys">Whether duplicate scalar keys are allowed.</param>
    /// <param name="result">Receives the routed insert result when the append succeeds or terminates as conflict/no-op.</param>
    /// <returns>`true` when the append-tail path produced a terminal result.</returns>
    private bool TryAppendScalar16VarIdentityDuplicateRunTail(
        long headShelfOffset,
        long terminalShelfOffset,
        byte[] terminalShelfBytes,
        Scalar16VarIdentityProfile profile,
        ulong encodedKeyHigh,
        ulong encodedKeyLow,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys,
        out Scalar16VarIdentityRoutedInsertResult result)
    {
        result = default;
        if (!allowDuplicateKeys)
        {
            result = new Scalar16VarIdentityRoutedInsertResult(
                Scalar16VarIdentityRoutedInsertKind.KeyConflict,
                Scalar16VarIdentityInsertResult.KeyConflict,
                terminalShelfOffset,
                0,
                default,
                0,
                profile.ShelfExtentSize);
            return true;
        }

        Scalar16VarIdentityReadOnly terminalShelf = new(terminalShelfBytes, profile);
        if (!terminalShelf.IsValid || terminalShelf.ItemCount == 0 || Scalar16VarIdentityLayout.ReadNextShelfOffset(terminalShelfBytes) != 0)
        {
            return false;
        }

        if (terminalShelf.ReadKeyHighAt(0) != encodedKeyHigh ||
            terminalShelf.ReadKeyLowAt(0) != encodedKeyLow ||
            terminalShelf.ReadKeyHighAt(terminalShelf.ItemCount - 1) != encodedKeyHigh ||
            terminalShelf.ReadKeyLowAt(terminalShelf.ItemCount - 1) != encodedKeyLow)
        {
            return false;
        }

        if (Scalar16VarIdentityLayout.CompareIdentityBytes(identity, terminalShelf.ReadIdentityAt(terminalShelf.ItemCount - 1)) <= 0)
        {
            return false;
        }

        byte[] tailShelf = Scalar16VarIdentity.CreateEmpty(profile);
        Scalar16VarIdentityInsertResult tailInsert = Scalar16VarIdentity.Insert(
            tailShelf,
            profile,
            encodedKeyHigh,
            encodedKeyLow,
            identity,
            allowDuplicateKeys: true,
            out byte[] rewrittenTail);
        if (tailInsert != Scalar16VarIdentityInsertResult.Inserted)
        {
            result = new Scalar16VarIdentityRoutedInsertResult(
                tailInsert == Scalar16VarIdentityInsertResult.KeyConflict ? Scalar16VarIdentityRoutedInsertKind.KeyConflict : Scalar16VarIdentityRoutedInsertKind.Invalid,
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
        scalar16VarIdentityMutableBatchShelves.Remove(headShelfOffset);
        scalar16VarIdentityMutableBatchShelves.Remove(terminalShelfOffset);
        RawDataReservation terminalRewrite = kernel.ReserveAt(terminalShelfOffset, profile.ShelfExtentSize);
        terminalShelfBytes.AsSpan(0, profile.ShelfExtentSize).CopyTo(terminalRewrite.Span);
        Scalar16VarIdentityLayout.WriteNextShelfOffset(terminalRewrite.Span, tailReservation.Extent.Offset);
        if (headShelfOffset == terminalShelfOffset)
        {
            Scalar16VarIdentityLayout.WriteTailShelfOffset(terminalRewrite.Span, tailReservation.Extent.Offset);
        }
        else
        {
            byte[] headBytes = ReadScalar16VarIdentityShelfBytes(headShelfOffset, profile.MaxIdentityLength, out Scalar16VarIdentityProfile headProfile);
            if (headProfile.ShelfExtentSize != profile.ShelfExtentSize)
            {
                throw new InvalidDataException("The SV16 duplicate-run head changed shelf extent sizes.");
            }

            Scalar16VarIdentityLayout.WriteTailShelfOffset(headBytes, tailReservation.Extent.Offset);
            RawDataReservation headRewrite = kernel.ReserveAt(headShelfOffset, profile.ShelfExtentSize);
            headBytes.CopyTo(headRewrite.Span);
        }

        DataKernelCommitTelemetry telemetry = CommitWithoutInvalidatingRouterReadCache();
        result = new Scalar16VarIdentityRoutedInsertResult(
            Scalar16VarIdentityRoutedInsertKind.WalkedDuplicateRunOverflow,
            Scalar16VarIdentityInsertResult.Inserted,
            terminalShelfOffset,
            tailReservation.Extent.Offset,
            telemetry,
            1,
            profile.ShelfExtentSize);
        return true;
    }

    /// <summary>
    /// Rewrites a same-key SV16 duplicate run into a linked shelf chain when scalar-key routing can no longer split it.<br/>
    /// The method is intentionally correctness-first: it collects the full linked run, inserts the incoming identity in tuple order, repartitions into packed shelves, and rewrites the chain head plus appended overflow shelves.<br/>
    /// Later hot-path work can replace this with incremental tail insertion once the persisted duplicate-run shape is proven.<br/>
    /// </summary>
    /// <param name="headShelfOffset">The route-owned head shelf offset.</param>
    /// <param name="headShelfBytes">The current head shelf bytes.</param>
    /// <param name="profile">The SV16 shelf profile used by every segment in the chain.</param>
    /// <param name="encodedKey">The encoded scalar key being inserted.</param>
    /// <param name="incomingIdentity">The raw identity being inserted.</param>
    /// <param name="allowDuplicateKeys">Whether duplicate scalar keys are allowed.</param>
    /// <param name="tailShelfOffset">Receives the final shelf offset in the rebuilt chain.</param>
    /// <param name="chainShelfCount">Receives the number of shelves in the rebuilt chain.</param>
    /// <param name="largestShelfItemCount">Receives the largest item count across rebuilt shelves.</param>
    /// <param name="insertResult">Receives the logical insert result represented by the chain rewrite.</param>
    /// <param name="telemetry">Receives DataKernel commit telemetry for the chain rewrite.</param>
    /// <returns>`true` when the same-key duplicate run was rewritten successfully.</returns>
    private bool TryRewriteScalar16VarIdentityDuplicateRunChain(
        long headShelfOffset,
        byte[] headShelfBytes,
        Scalar16VarIdentityProfile profile,
        ulong encodedKeyHigh,
        ulong encodedKeyLow,
        ReadOnlySpan<byte> incomingIdentity,
        bool allowDuplicateKeys,
        out long tailShelfOffset,
        out int chainShelfCount,
        out int largestShelfItemCount,
        out Scalar16VarIdentityInsertResult insertResult,
        out DataKernelCommitTelemetry telemetry)
    {
        tailShelfOffset = 0;
        chainShelfCount = 0;
        largestShelfItemCount = 0;
        insertResult = Scalar16VarIdentityInsertResult.Full;
        telemetry = default;
        if (!allowDuplicateKeys)
        {
            insertResult = Scalar16VarIdentityInsertResult.KeyConflict;
            return false;
        }

        Scalar16VarIdentityIdentityRef[] identities = new Scalar16VarIdentityIdentityRef[256];
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
                throw new InvalidDataException("The SV16 duplicate-run shelf chain contains a cycle.");
            }

            Scalar16VarIdentityReadOnly shelf = new(currentBytes, profile);
            if (!shelf.IsValid)
            {
                insertResult = Scalar16VarIdentityInsertResult.Invalid;
                return false;
            }

            for (int i = 0; i < shelf.ItemCount; i++)
            {
                if (shelf.ReadKeyHighAt(i) != encodedKeyHigh || shelf.ReadKeyLowAt(i) != encodedKeyLow)
                {
                    return false;
                }

                ReadOnlySpan<byte> existingIdentity = shelf.ReadIdentityAt(i);
                if (Scalar16VarIdentityLayout.IdentityBytesEqual(existingIdentity, incomingIdentity))
                {
                    insertResult = Scalar16VarIdentityInsertResult.AlreadyPresent;
                    return false;
                }

                if (!incomingAdded && Scalar16VarIdentityLayout.CompareIdentityBytes(incomingIdentity, existingIdentity) < 0)
                {
                    AddScalar16VarIdentityRef(ref identities, ref identityCount, new Scalar16VarIdentityIdentityRef(incomingIdentityBytes, 0, true));
                    incomingAdded = true;
                }

                AddScalar16VarIdentityRef(ref identities, ref identityCount, new Scalar16VarIdentityIdentityRef(currentBytes, checked((int)shelf.ReadRecordOffsetAt(i)), false));
            }

            currentOffset = Scalar16VarIdentityLayout.ReadNextShelfOffset(currentBytes);
            if (currentOffset != 0)
            {
                currentBytes = ReadScalar16VarIdentityShelfBytes(currentOffset, profile.MaxIdentityLength, out Scalar16VarIdentityProfile nextProfile);
                if (nextProfile.ShelfExtentSize != profile.ShelfExtentSize)
                {
                    throw new InvalidDataException("The SV16 duplicate-run shelf chain changed shelf extent sizes.");
                }
            }
        }

        if (!incomingAdded)
        {
            AddScalar16VarIdentityRef(ref identities, ref identityCount, new Scalar16VarIdentityIdentityRef(incomingIdentityBytes, 0, true));
        }

        byte[][] rebuiltShelves = new byte[4][];
        int rebuiltShelfCount = 0;
        int start = 0;
        while (start < identityCount)
        {
            int bestCount = GetScalar16VarIdentityDuplicateRunChunkCount(identities, incomingIdentityBytes, start, identityCount - start, profile);
            if (bestCount == 0 ||
                !TryBuildScalar16VarIdentityDuplicateRunShelf(encodedKeyHigh, encodedKeyLow, identities, incomingIdentityBytes, start, bestCount, profile, out byte[] bestShelf))
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
        scalar16VarIdentityMutableBatchShelves.Remove(headShelfOffset);
        for (int i = rebuiltShelfCount - 1; i >= 1; i--)
        {
            Scalar16VarIdentityLayout.WriteNextShelfOffset(rebuiltShelves[i], nextOffset);
            RawDataReservation reservation = kernel.Reserve(profile.ShelfExtentSize);
            rebuiltShelves[i].CopyTo(reservation.Span);
            nextOffset = reservation.Extent.Offset;
            if (tailOffset == headShelfOffset)
            {
                tailOffset = reservation.Extent.Offset;
            }
        }

        Scalar16VarIdentityLayout.WriteNextShelfOffset(rebuiltShelves[0], nextOffset);
        Scalar16VarIdentityLayout.WriteTailShelfOffset(rebuiltShelves[0], tailOffset == headShelfOffset ? 0 : tailOffset);
        RawDataReservation headRewrite = kernel.ReserveAt(headShelfOffset, profile.ShelfExtentSize);
        rebuiltShelves[0].CopyTo(headRewrite.Span);
        telemetry = CommitWithoutInvalidatingRouterReadCache();
        tailShelfOffset = tailOffset;
        chainShelfCount = rebuiltShelfCount;
        insertResult = Scalar16VarIdentityInsertResult.Inserted;
        return true;
    }

    private static void AddScalar16VarIdentityRef(ref Scalar16VarIdentityIdentityRef[] identities, ref int count, Scalar16VarIdentityIdentityRef identity)
    {
        if (count == identities.Length)
        {
            Array.Resize(ref identities, checked(identities.Length * 2));
        }

        identities[count++] = identity;
    }

    private static int GetScalar16VarIdentityDuplicateRunChunkCount(
        ReadOnlySpan<Scalar16VarIdentityIdentityRef> identities,
        ReadOnlySpan<byte> incomingIdentity,
        int start,
        int available,
        Scalar16VarIdentityProfile profile)
    {
        byte[] bytes = Scalar16VarIdentity.CreateEmpty(profile);
        int slotCapacityBytes = Scalar16VarIdentityLayout.ReadSlotCapacityBytes(bytes);
        int recordCursor = Scalar16VarIdentityLayout.HeaderSize + slotCapacityBytes;
        int count = 0;
        while (count < available)
        {
            if (checked((count + 1) * Scalar16VarIdentityLayout.SlotSize) > slotCapacityBytes)
            {
                break;
            }

            ReadOnlySpan<byte> identity = identities[start + count].ReadIdentity(incomingIdentity);
            int recordLength = Scalar16VarIdentityLayout.GetNewRecordLength(identity.Length);
            if (recordCursor + recordLength > profile.ShelfExtentSize)
            {
                break;
            }

            recordCursor += recordLength;
            count++;
        }

        return count;
    }

    private static bool TryBuildScalar16VarIdentityDuplicateRunShelf(
        ulong encodedKeyHigh,
        ulong encodedKeyLow,
        ReadOnlySpan<Scalar16VarIdentityIdentityRef> identities,
        ReadOnlySpan<byte> incomingIdentity,
        int start,
        int count,
        Scalar16VarIdentityProfile profile,
        out byte[] bytes)
    {
        bytes = Scalar16VarIdentity.CreateEmpty(profile);
        int slotCapacityBytes = Scalar16VarIdentityLayout.ReadSlotCapacityBytes(bytes);
        int slotCursor = Scalar16VarIdentityLayout.HeaderSize;
        int recordCursor = Scalar16VarIdentityLayout.HeaderSize + slotCapacityBytes;
        uint keyPrefix = Scalar16VarIdentityLayout.CreateKeyPrefix(encodedKeyHigh, encodedKeyLow);
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> identity = identities[start + i].ReadIdentity(incomingIdentity);
            if (identity.Length <= 0 || identity.Length > profile.MaxIdentityLength)
            {
                return false;
            }

            int recordLength = Scalar16VarIdentityLayout.GetNewRecordLength(identity.Length);
            if (recordCursor + recordLength > profile.ShelfExtentSize)
            {
                return false;
            }

            Scalar16VarIdentityLayout.WriteRecord(bytes, recordCursor, encodedKeyHigh, encodedKeyLow, identity);
            Scalar16VarIdentityLayout.WriteSlotRecordOffset(bytes, slotCursor, recordCursor);
            Scalar16VarIdentityLayout.WriteSlotKeyPrefix(bytes, slotCursor, keyPrefix);
            recordCursor += recordLength;
            slotCursor += Scalar16VarIdentityLayout.SlotSize;
        }

        Scalar16VarIdentityLayout.WriteItemCount(bytes, count);
        Scalar16VarIdentityLayout.WriteSlotStreamLength(bytes, slotCursor - Scalar16VarIdentityLayout.HeaderSize);
        Scalar16VarIdentityLayout.WriteRecordArenaEnd(bytes, recordCursor);
        return true;
    }

    /// <summary>
    /// Attempts to split an SV16 shelf at the same scalar-key byte depth as its parent router.<br/>
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
    private static bool TrySplitScalar16VarIdentityShelfAtDepth(
        Scalar16VarIdentityMutableShelfView mutableShelf,
        Scalar16VarIdentityProfile profile,
        ushort splitDepth,
        ulong incomingKeyHigh,
        ulong incomingKeyLow,
        ReadOnlySpan<byte> incomingIdentity,
        bool allowDuplicateKeys,
        out byte selectedRightPrefix,
        out byte[] leftShelf,
        out byte[] rightShelf,
        out int leftCount,
        out int rightCount,
        out Scalar16VarIdentityInsertResult insertResult)
    {
        selectedRightPrefix = 0;
        leftShelf = [];
        rightShelf = [];
        leftCount = 0;
        rightCount = 0;
        insertResult = Scalar16VarIdentityInsertResult.Full;
        if (mutableShelf.Profile.ShelfExtentSize != profile.ShelfExtentSize)
        {
            insertResult = Scalar16VarIdentityInsertResult.Invalid;
            return false;
        }

        int totalCount = checked(mutableShelf.ItemCount + 1);
        ulong[] keyHighs = new ulong[totalCount];
        ulong[] keyLows = new ulong[totalCount];
        int[] sourceSlots = new int[totalCount];
        int incomingIndex = -1;
        bool inserted = false;
        int targetIndex = 0;
        for (int sourceIndex = 0; sourceIndex < mutableShelf.ItemCount; sourceIndex++)
        {
            ulong currentKeyHigh = mutableShelf.ReadKeyHighAt(sourceIndex);
            ulong currentKeyLow = mutableShelf.ReadKeyLowAt(sourceIndex);
            ReadOnlySpan<byte> currentIdentity = mutableShelf.ReadIdentityAt(sourceIndex);
            if (!inserted && CompareScalar16VarIdentityTuple(currentKeyHigh, currentKeyLow, currentIdentity, incomingKeyHigh, incomingKeyLow, incomingIdentity) > 0)
            {
                keyHighs[targetIndex] = incomingKeyHigh;
                keyLows[targetIndex] = incomingKeyLow;
                sourceSlots[targetIndex] = -1;
                incomingIndex = targetIndex;
                targetIndex++;
                inserted = true;
            }

            if (currentKeyHigh == incomingKeyHigh &&
                currentKeyLow == incomingKeyLow &&
                Scalar16VarIdentityLayout.IdentityBytesEqual(currentIdentity, incomingIdentity))
            {
                insertResult = Scalar16VarIdentityInsertResult.AlreadyPresent;
                return false;
            }

            if (!allowDuplicateKeys && currentKeyHigh == incomingKeyHigh && currentKeyLow == incomingKeyLow)
            {
                insertResult = Scalar16VarIdentityInsertResult.KeyConflict;
                return false;
            }

            keyHighs[targetIndex] = currentKeyHigh;
            keyLows[targetIndex] = currentKeyLow;
            sourceSlots[targetIndex] = sourceIndex;
            targetIndex++;
        }

        if (!inserted)
        {
            keyHighs[targetIndex] = incomingKeyHigh;
            keyLows[targetIndex] = incomingKeyLow;
            sourceSlots[targetIndex] = -1;
            incomingIndex = targetIndex;
        }

        Scalar16VarIdentitySplitIdentitySource identitySource = new(mutableShelf, sourceSlots, incomingIndex, incomingIdentity);
        if (!TryBuildScalar16VarIdentitySplitAtDepth(keyHighs, keyLows, identitySource, profile, splitDepth, out selectedRightPrefix, out leftShelf, out rightShelf, out leftCount, out rightCount))
        {
            return false;
        }

        insertResult = Scalar16VarIdentityInsertResult.Inserted;
        return true;
    }

    private static bool TryBuildScalar16VarIdentitySplitAtDepth(
        ReadOnlySpan<ulong> keyHighs,
        ReadOnlySpan<ulong> keyLows,
        Scalar16VarIdentitySplitIdentitySource identities,
        Scalar16VarIdentityProfile profile,
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
        byte previousPrefix = GetScalar16VarIdentityPrefix(keyHighs[0], keyLows[0], splitDepth);
        for (int i = 1; i < keyHighs.Length; i++)
        {
            byte prefix = GetScalar16VarIdentityPrefix(keyHighs[i], keyLows[i], splitDepth);
            if (prefix == previousPrefix)
            {
                continue;
            }

            int candidateLeftCount = i;
            int candidateRightCount = keyHighs.Length - i;
            int candidateLargestSide = Math.Max(candidateLeftCount, candidateRightCount);
            if (candidateLargestSide < bestLargestSide)
            {
                bestLargestSide = candidateLargestSide;
                bestBoundary = i;
            }

            previousPrefix = prefix;
        }

        if (bestBoundary > 0 && TryBuildScalar16VarIdentitySplitBoundary(keyHighs, keyLows, identities, profile, splitDepth, bestBoundary, out selectedRightPrefix, out leftShelf, out rightShelf, out leftCount, out rightCount))
        {
            return true;
        }

        previousPrefix = GetScalar16VarIdentityPrefix(keyHighs[0], keyLows[0], splitDepth);
        for (int i = 1; i < keyHighs.Length; i++)
        {
            byte prefix = GetScalar16VarIdentityPrefix(keyHighs[i], keyLows[i], splitDepth);
            if (prefix == previousPrefix)
            {
                continue;
            }

            if (i != bestBoundary &&
                TryBuildScalar16VarIdentitySplitBoundary(keyHighs, keyLows, identities, profile, splitDepth, i, out selectedRightPrefix, out leftShelf, out rightShelf, out leftCount, out rightCount))
            {
                return true;
            }

            previousPrefix = prefix;
        }

        return false;
    }

    /// <summary>
    /// Finds the first scalar-key byte depth that can distinguish the lowest and highest tuple in a sorted `SV16` split candidate.<br/>
    /// Split planning uses this to skip depths that are guaranteed to keep every tuple on the same route and therefore cannot produce two replacement shelves.<br/>
    /// </summary>
    /// <param name="keyHighs">The sorted high 64-bit halves of the encoded scalar keys being considered for the split.</param>
    /// <param name="keyLows">The sorted low 64-bit halves of the encoded scalar keys being considered for the split.</param>
    /// <param name="startDepth">The first byte depth that has not already been represented by ancestor routing.</param>
    /// <returns>The first distinguishing depth, or `-1` when no remaining scalar-key byte can split the candidate.</returns>
    private static int FindFirstDifferingScalar16VarIdentityDepth(ReadOnlySpan<ulong> keyHighs, ReadOnlySpan<ulong> keyLows, int startDepth)
    {
        if (keyHighs.Length < 2)
        {
            return -1;
        }

        ulong firstKeyHigh = keyHighs[0];
        ulong firstKeyLow = keyLows[0];
        ulong lastKeyHigh = keyHighs[^1];
        ulong lastKeyLow = keyLows[^1];
        for (int depth = startDepth; depth < Scalar16VarIdentityLayout.KeySize; depth++)
        {
            if (GetScalar16VarIdentityPrefix(firstKeyHigh, firstKeyLow, depth) != GetScalar16VarIdentityPrefix(lastKeyHigh, lastKeyLow, depth))
            {
                return depth;
            }
        }

        return -1;
    }

    /// <summary>
    /// Builds the two replacement `SV16` shelves for a single already-selected split boundary.<br/>
    /// Split planning calls this after selecting a preferred boundary so common balanced splits avoid repeatedly rebuilding candidate shelf images.<br/>
    /// </summary>
    /// <param name="keys">The sorted scalar-key array including the incoming tuple.</param>
    /// <param name="identities">The sorted raw identity byte arrays aligned with <paramref name="keys"/>.</param>
    /// <param name="profile">The `SV16` shelf profile used for both replacement shelves.</param>
    /// <param name="splitDepth">The scalar-key byte depth used to choose the right-side router prefix.</param>
    /// <param name="boundary">The first tuple index that belongs to the right replacement shelf.</param>
    /// <param name="selectedRightPrefix">Receives the first prefix byte routed to the right replacement shelf.</param>
    /// <param name="leftShelf">Receives the rebuilt left shelf bytes.</param>
    /// <param name="rightShelf">Receives the rebuilt right shelf bytes.</param>
    /// <param name="leftCount">Receives the left replacement shelf item count.</param>
    /// <param name="rightCount">Receives the right replacement shelf item count.</param>
    /// <returns>`true` when both replacement shelves fit the selected boundary.</returns>
    private static bool TryBuildScalar16VarIdentitySplitBoundary(
        ReadOnlySpan<ulong> keyHighs,
        ReadOnlySpan<ulong> keyLows,
        Scalar16VarIdentitySplitIdentitySource identities,
        Scalar16VarIdentityProfile profile,
        ushort splitDepth,
        int boundary,
        out byte selectedRightPrefix,
        out byte[] leftShelf,
        out byte[] rightShelf,
        out int leftCount,
        out int rightCount)
    {
        selectedRightPrefix = GetScalar16VarIdentityPrefix(keyHighs[boundary], keyLows[boundary], splitDepth);
        leftShelf = [];
        rightShelf = [];
        leftCount = boundary;
        rightCount = keyHighs.Length - boundary;
        if (!TryBuildScalar16VarIdentityShelfFromSource(keyHighs, keyLows, identities, 0, boundary, profile, out leftShelf) ||
            !TryBuildScalar16VarIdentityShelfFromSource(keyHighs, keyLows, identities, boundary, keyHighs.Length - boundary, profile, out rightShelf))
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
    /// Builds one replacement `SV16` shelf directly from a sorted split source range.<br/>
    /// The slot and record streams are emitted in one pass so split rebuilds avoid a temporary record-offset array and a second slot-writing pass.<br/>
    /// </summary>
    /// <param name="keyHighs">The sorted high 64-bit halves of the encoded scalar keys aligned with <paramref name="identities"/>.</param>
    /// <param name="keyLows">The sorted low 64-bit halves of the encoded scalar keys aligned with <paramref name="identities"/>.</param>
    /// <param name="identities">The identity source that can project existing shelf identities and the incoming identity without materializing every identity.</param>
    /// <param name="start">The first source index to include in the replacement shelf.</param>
    /// <param name="count">The number of sorted tuples to include in the replacement shelf.</param>
    /// <param name="profile">The `SV16` shelf profile used to size and validate the replacement shelf.</param>
    /// <param name="bytes">Receives the rebuilt shelf image when the source range fits.</param>
    /// <returns>`true` when the source range fits the profile slot reserve and record arena.</returns>
    private static bool TryBuildScalar16VarIdentityShelfFromSource(
        ReadOnlySpan<ulong> keyHighs,
        ReadOnlySpan<ulong> keyLows,
        Scalar16VarIdentitySplitIdentitySource identities,
        int start,
        int count,
        Scalar16VarIdentityProfile profile,
        out byte[] bytes)
    {
        bytes = new byte[profile.ShelfExtentSize];
        Scalar16VarIdentityLayout.Initialize(bytes, profile);
        int slotLength = checked(count * Scalar16VarIdentityLayout.SlotSize);
        int slotCapacityBytes = Scalar16VarIdentityLayout.ReadSlotCapacityBytes(bytes);
        if (slotLength > slotCapacityBytes)
        {
            return false;
        }

        int slotCursor = Scalar16VarIdentityLayout.HeaderSize;
        int recordCursor = Scalar16VarIdentityLayout.HeaderSize + slotCapacityBytes;
        for (int i = 0; i < count; i++)
        {
            int sourceIndex = start + i;
            ReadOnlySpan<byte> identity = identities.ReadIdentityAt(sourceIndex);
            if (identity.Length <= 0 || identity.Length > profile.MaxIdentityLength)
            {
                return false;
            }

            int recordLength = Scalar16VarIdentityLayout.GetNewRecordLength(identity.Length);
            if (recordCursor + recordLength > profile.ShelfExtentSize)
            {
                return false;
            }

            Scalar16VarIdentityLayout.WriteRecord(bytes, recordCursor, keyHighs[sourceIndex], keyLows[sourceIndex], identity);
            Scalar16VarIdentityLayout.WriteSlotRecordOffset(bytes, slotCursor, recordCursor);
            Scalar16VarIdentityLayout.WriteSlotKeyPrefix(bytes, slotCursor, Scalar16VarIdentityLayout.CreateKeyPrefix(keyHighs[sourceIndex], keyLows[sourceIndex]));
            recordCursor += recordLength;
            slotCursor += Scalar16VarIdentityLayout.SlotSize;
        }

        Scalar16VarIdentityLayout.WriteItemCount(bytes, count);
        Scalar16VarIdentityLayout.WriteSlotStreamLength(bytes, slotCursor - Scalar16VarIdentityLayout.HeaderSize);
        Scalar16VarIdentityLayout.WriteRecordArenaEnd(bytes, recordCursor);
        return true;
    }

    private static int CompareScalar16VarIdentityTuple(ulong leftKeyHigh, ulong leftKeyLow, ReadOnlySpan<byte> leftIdentity, ulong rightKeyHigh, ulong rightKeyLow, ReadOnlySpan<byte> rightIdentity)
    {
        int keyComparison = CompareScalar16VarIdentityKey(leftKeyHigh, leftKeyLow, rightKeyHigh, rightKeyLow);
        if (keyComparison != 0)
        {
            return keyComparison;
        }

        return Scalar16VarIdentityLayout.CompareIdentityBytes(leftIdentity, rightIdentity);
    }

    internal static int CompareScalar16VarIdentityKey(ulong leftHigh, ulong leftLow, ulong rightHigh, ulong rightLow)
    {
        if (leftHigh < rightHigh)
        {
            return -1;
        }

        if (leftHigh > rightHigh)
        {
            return 1;
        }

        if (leftLow < rightLow)
        {
            return -1;
        }

        return leftLow > rightLow ? 1 : 0;
    }

    internal static byte GetScalar16VarIdentityPrefix(ulong encodedKeyHigh, ulong encodedKeyLow, int keyDepth)
    {
        if ((uint)keyDepth >= Scalar16VarIdentityLayout.KeySize)
        {
            throw new ArgumentOutOfRangeException(nameof(keyDepth), keyDepth, "SV16 scalar keys expose exactly sixteen routing bytes.");
        }

        if (keyDepth < sizeof(ulong))
        {
            return (byte)(encodedKeyHigh >> ((sizeof(ulong) - 1 - keyDepth) * 8));
        }

        int lowDepth = keyDepth - sizeof(ulong);
        return (byte)(encodedKeyLow >> ((sizeof(ulong) - 1 - lowDepth) * 8));
    }

    /// <summary>
    /// Converts the shared promoted direct-router target-kind cache value into the `SV16` route target enum.<br/>
    /// The promoted direct-router view is shape-neutral storage over router pages, but its target-kind sidecar was introduced with the scalar8-scalar8 route enum.<br/>
    /// Keeping the conversion explicit lets `SV16` reuse the same router projection without leaking shelf-shape assumptions into route walking.<br/>
    /// </summary>
    /// <param name="kind">The shared promoted direct-router target kind.</param>
    /// <returns>The equivalent `SV16` target kind.</returns>
    private static Scalar16VarIdentityRouteTargetKind ToScalar16VarIdentityRouteTargetKind(Scalar8Scalar8RouteTargetKind kind)
    {
        return kind switch
        {
            Scalar8Scalar8RouteTargetKind.Router => Scalar16VarIdentityRouteTargetKind.Router,
            Scalar8Scalar8RouteTargetKind.Shelf => Scalar16VarIdentityRouteTargetKind.Shelf,
            _ => Scalar16VarIdentityRouteTargetKind.None
        };
    }

    /// <summary>
    /// Converts an `SV16` target classification into the shared promoted direct-router target-kind cache value.<br/>
    /// Only the structural categories are cached: router, shelf, or none; the concrete shelf format is still validated by the `SV16` classifier before storing the value.<br/>
    /// </summary>
    /// <param name="kind">The `SV16` route target kind.</param>
    /// <returns>The shared promoted direct-router target kind.</returns>
    private static Scalar8Scalar8RouteTargetKind ToScalar8Scalar8RouteTargetKind(Scalar16VarIdentityRouteTargetKind kind)
    {
        return kind switch
        {
            Scalar16VarIdentityRouteTargetKind.Router => Scalar8Scalar8RouteTargetKind.Router,
            Scalar16VarIdentityRouteTargetKind.Shelf => Scalar8Scalar8RouteTargetKind.Shelf,
            _ => Scalar8Scalar8RouteTargetKind.None
        };
    }
}

internal readonly ref struct Scalar16VarIdentitySplitIdentitySource
{
    private readonly Scalar16VarIdentityMutableShelfView shelf;
    private readonly ReadOnlySpan<int> sourceSlots;
    private readonly int incomingIndex;
    private readonly ReadOnlySpan<byte> incomingIdentity;

    public Scalar16VarIdentitySplitIdentitySource(
        Scalar16VarIdentityMutableShelfView shelf,
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

internal readonly struct Scalar16VarIdentityIdentityRef
{
    private readonly byte[] bytes;
    private readonly int recordOffset;
    private readonly bool isIncoming;

    public Scalar16VarIdentityIdentityRef(byte[] bytes, int recordOffset, bool isIncoming)
    {
        this.bytes = bytes;
        this.recordOffset = recordOffset;
        this.isIncoming = isIncoming;
    }

    public ReadOnlySpan<byte> ReadIdentity(ReadOnlySpan<byte> incomingIdentity)
    {
        return isIncoming ? incomingIdentity : Scalar16VarIdentityLayout.ReadIdentity(bytes, recordOffset);
    }
}
