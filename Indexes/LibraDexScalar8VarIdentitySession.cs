using LibraDex.Layouts;
using LibraDex.Views;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace LibraDex;

internal sealed partial class LibraDexFileSession
{
    private const int DefaultScalar8VarIdentityMaxRouterHops = 32;
    private const bool EnableScalar8VarIdentityKeyByteSplits = true;
    private const bool EnableScalar8VarIdentitySameDepthSplits = true;
    private const bool EnableScalar8VarIdentityTerminalDuplicateRoutes = true;
    private const bool EnableScalar8VarIdentityMixedTerminalExtraction = true;
    private const int Scalar8VarIdentityTerminalDuplicatePressureItemCount = 32;

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

        if (magic == TerminalIdentityRootLayout.Magic)
        {
            return Scalar8VarIdentityRouteTargetKind.TerminalVarIdentityRoot;
        }

        return Scalar8VarIdentityRouteTargetKind.None;
    }

    /// <summary>
    /// Counts every ordinary `SV8` identity reachable from the routed root by summing shelf-local count metadata.<br/>
    /// This avoids constructing a range reader for count-all while still following routed shelves, overflow chains, and terminal var-identity roots.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The routed `SV8` root router offset.<br/></param>
    /// <param name="maxIdentityLength">The maximum raw identity length accepted by the index.<br/></param>
    /// <returns>The summed ordinary routed identity count.<br/></returns>
    internal long CountScalar8VarIdentityIdentities(long rootRouterOffset, int maxIdentityLength)
    {
        HashSet<long> visitedTargets = new();
        HashSet<long> visitedRouters = new();
        return CountScalar8VarIdentityIdentitiesFromRouter(rootRouterOffset, maxIdentityLength, visitedTargets, visitedRouters);
    }

    /// <summary>
    /// Counts `SV8` identities in an inclusive scalar-key range using metadata traversal for fully covered direct root-prefix targets.<br/>
    /// Boundary prefixes and compressed/shared root layouts retain reader counting so range edges remain exact without inspecting identities.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The routed `SV8` root router offset.<br/></param>
    /// <param name="maxIdentityLength">The maximum raw identity length accepted by the index.<br/></param>
    /// <param name="lowerEncodedKey">The inclusive lower encoded scalar key.<br/></param>
    /// <param name="upperEncodedKey">The inclusive upper encoded scalar key.<br/></param>
    /// <returns>The number of matching identities.<br/></returns>
    internal long CountScalar8VarIdentityRange(long rootRouterOffset, int maxIdentityLength, ulong lowerEncodedKey, ulong upperEncodedKey)
    {
        if (lowerEncodedKey > upperEncodedKey)
        {
            return 0;
        }

        byte lowerPrefix = GetScalar8VarIdentityPrefix(lowerEncodedKey, 0);
        byte upperPrefix = GetScalar8VarIdentityPrefix(upperEncodedKey, 0);
        if (lowerPrefix == upperPrefix || lowerPrefix == byte.MaxValue || upperPrefix == byte.MinValue)
        {
            using Scalar8VarIdentityRangeReader reader = OpenScalar8VarIdentityRangeReader(rootRouterOffset, maxIdentityLength, lowerEncodedKey, upperEncodedKey);
            return reader.Count;
        }

        Span<byte> routerBytes = stackalloc byte[RouterLayout.Size];
        ReadRouterPageUsingArenaCache(rootRouterOffset, routerBytes);
        RouterReader router = new(routerBytes);
        if (!router.IsValid)
        {
            throw new InvalidDataException("The routed SV8 range-count root router is invalid.");
        }

        if (!router.HasDirectIndex)
        {
            using Scalar8VarIdentityRangeReader reader = OpenScalar8VarIdentityRangeReader(rootRouterOffset, maxIdentityLength, lowerEncodedKey, upperEncodedKey);
            return reader.Count;
        }

        long lowerTarget = router.GetRouteTargetAt(lowerPrefix);
        long upperTarget = router.GetRouteTargetAt(upperPrefix);
        for (int prefix = lowerPrefix + 1; prefix < upperPrefix; prefix++)
        {
            long target = router.GetRouteTargetAt(prefix);
            if (target != 0 && (target == lowerTarget || target == upperTarget))
            {
                using Scalar8VarIdentityRangeReader reader = OpenScalar8VarIdentityRangeReader(rootRouterOffset, maxIdentityLength, lowerEncodedKey, upperEncodedKey);
                return reader.Count;
            }
        }

        long count = 0;
        using (Scalar8VarIdentityRangeReader lowerReader = OpenScalar8VarIdentityRangeReader(rootRouterOffset, maxIdentityLength, lowerEncodedKey, CreateScalar8PrefixUpperBound(lowerPrefix)))
        {
            count += lowerReader.Count;
        }

        HashSet<long> visitedTargets = new();
        HashSet<long> visitedRouters = new();
        for (int prefix = lowerPrefix + 1; prefix < upperPrefix; prefix++)
        {
            count += CountScalar8VarIdentityRouteTarget(router.GetRouteTargetAt(prefix), maxIdentityLength, visitedTargets, visitedRouters);
        }

        using (Scalar8VarIdentityRangeReader upperReader = OpenScalar8VarIdentityRangeReader(rootRouterOffset, maxIdentityLength, CreateScalar8PrefixLowerBound(upperPrefix), upperEncodedKey))
        {
            count += upperReader.Count;
        }

        return count;
    }

    /// <summary>
    /// Creates the inclusive scalar-8 lower key for a root prefix byte.<br/>
    /// </summary>
    /// <param name="prefix">The root prefix byte.<br/></param>
    /// <returns>The first encoded scalar-8 key in the prefix range.<br/></returns>
    private static ulong CreateScalar8PrefixLowerBound(byte prefix)
    {
        return (ulong)prefix << 56;
    }

    /// <summary>
    /// Creates the inclusive scalar-8 upper key for a root prefix byte.<br/>
    /// </summary>
    /// <param name="prefix">The root prefix byte.<br/></param>
    /// <returns>The last encoded scalar-8 key in the prefix range.<br/></returns>
    private static ulong CreateScalar8PrefixUpperBound(byte prefix)
    {
        return ((ulong)prefix << 56) | 0x00FFFFFFFFFFFFFFUL;
    }

    private long CountScalar8VarIdentityIdentitiesFromRouter(
        long routerOffset,
        int maxIdentityLength,
        HashSet<long> visitedTargets,
        HashSet<long> visitedRouters)
    {
        if (!visitedRouters.Add(routerOffset))
        {
            return 0;
        }

        Span<byte> routerBytes = stackalloc byte[RouterLayout.Size];
        ReadRouterPageUsingArenaCache(routerOffset, routerBytes);
        RouterReader router = new(routerBytes);
        if (!router.IsValid)
        {
            throw new InvalidDataException("The routed SV8 count router is invalid.");
        }

        long count = 0;
        if (router.HasDirectIndex)
        {
            for (int routeIndex = 0; routeIndex <= byte.MaxValue; routeIndex++)
            {
                count += CountScalar8VarIdentityRouteTarget(router.GetRouteTargetAt(routeIndex), maxIdentityLength, visitedTargets, visitedRouters);
            }

            return count;
        }

        for (int routeIndex = 0; routeIndex < router.RouteCount; routeIndex++)
        {
            count += CountScalar8VarIdentityRouteTarget(router.GetRouteTargetAt(routeIndex), maxIdentityLength, visitedTargets, visitedRouters);
        }

        return count;
    }

    private long CountScalar8VarIdentityRouteTarget(
        long targetOffset,
        int maxIdentityLength,
        HashSet<long> visitedTargets,
        HashSet<long> visitedRouters)
    {
        if (targetOffset == 0 || visitedTargets.Contains(targetOffset) || visitedRouters.Contains(targetOffset))
        {
            return 0;
        }

        Scalar8VarIdentityRouteTargetKind kind = ClassifyScalar8VarIdentityRouteTarget(targetOffset);
        if (kind == Scalar8VarIdentityRouteTargetKind.Router)
        {
            return CountScalar8VarIdentityIdentitiesFromRouter(targetOffset, maxIdentityLength, visitedTargets, visitedRouters);
        }

        if (!visitedTargets.Add(targetOffset))
        {
            return 0;
        }

        return kind switch
        {
            Scalar8VarIdentityRouteTargetKind.Shelf => CountScalar8VarIdentityShelfChainNarrow(targetOffset, maxIdentityLength, visitedTargets),
            Scalar8VarIdentityRouteTargetKind.TerminalVarIdentityRoot => CountTerminalVarIdentityRootNarrow(targetOffset, TerminalIdentityRootLayout.ShapeScalar8VarIdentity),
            _ => throw new InvalidDataException("The routed SV8 count target is not a shelf, terminal root, or router.")
        };
    }

    private long CountScalar8VarIdentityShelfChainNarrow(long shelfOffset, int maxIdentityLength, HashSet<long> visitedTargets)
    {
        long count = 0;
        long currentOffset = shelfOffset;
        while (currentOffset != 0)
        {
            count += ReadScalar8VarIdentityShelfItemCountNarrow(currentOffset, maxIdentityLength, out long nextOffset);
            currentOffset = nextOffset;
            if (currentOffset != 0 && !visitedTargets.Add(currentOffset))
            {
                break;
            }
        }

        return count;
    }

    private long ReadScalar8VarIdentityShelfItemCountNarrow(long shelfOffset, int maxIdentityLength, out long nextOffset)
    {
        if (durabilityBatchActive &&
            scalar8VarIdentityMutableBatchShelves.TryGetValue(shelfOffset, out Scalar8VarIdentityMutableShelfView? mutableShelf))
        {
            nextOffset = Scalar8VarIdentityLayout.ReadNextShelfOffset(mutableShelf.Bytes);
            return mutableShelf.LiveItemCount;
        }

        Scalar8VarIdentityShelfCountCacheKey cacheKey = new(shelfOffset, maxIdentityLength);
        if (TryGetScalar8VarIdentityShelfCountCache(cacheKey, out ScalarVarIdentityShelfCountCacheValue cachedValue))
        {
            nextOffset = cachedValue.NextShelfOffset;
            return cachedValue.Count;
        }

        Span<byte> header = stackalloc byte[Scalar8VarIdentityLayout.HeaderSize];
        kernel.Read(shelfOffset, header);
        if (Scalar8VarIdentityLayout.ReadMagic(header) != Scalar8VarIdentityLayout.Magic ||
            Scalar8VarIdentityLayout.ReadFormatVersion(header) != Scalar8VarIdentityLayout.FormatVersion ||
            Scalar8VarIdentityLayout.ReadHeaderSize(header) != Scalar8VarIdentityLayout.HeaderSize)
        {
            throw new InvalidDataException("The routed SV8 count shelf header is invalid.");
        }

        int shelfExtentSize = Scalar8VarIdentityLayout.ReadShelfExtentSize(header);
        _ = Scalar8VarIdentityProfile.Create(shelfExtentSize, maxIdentityLength);
        int count = Scalar8VarIdentityLayout.ReadItemCount(header);
        int slotStreamLength = Scalar8VarIdentityLayout.ReadSlotStreamLength(header);
        int slotCapacityBytes = Scalar8VarIdentityLayout.ReadSlotCapacityBytes(header);
        int recordArenaEnd = Scalar8VarIdentityLayout.ReadRecordArenaEnd(header);
        int expectedSlotCapacityBytes = Scalar8VarIdentityLayout.CalculateSlotCapacityBytes(shelfExtentSize);
        if (count < 0 ||
            count > expectedSlotCapacityBytes / Scalar8VarIdentityLayout.SlotSize ||
            slotStreamLength != checked(count * Scalar8VarIdentityLayout.SlotSize) ||
            slotCapacityBytes != expectedSlotCapacityBytes ||
            recordArenaEnd < Scalar8VarIdentityLayout.HeaderSize + slotCapacityBytes ||
            recordArenaEnd > shelfExtentSize)
        {
            throw new InvalidDataException("The routed SV8 count shelf metadata is invalid.");
        }

        nextOffset = Scalar8VarIdentityLayout.ReadNextShelfOffset(header);
        StoreScalar8VarIdentityShelfCountCache(cacheKey, count, nextOffset);
        return count;
    }

    /// <summary>
    /// Tries to read a session-local narrow count projection for one `SV8` shelf.<br/>
    /// The cached value includes the next-shelf offset because `SV8` duplicate/overflow chains need it after the first validated header read.<br/>
    /// Entries are scoped to the shared DataKernel mutation-version boundary, so the cache is free in-memory reuse and never maintained as persisted metadata.<br/>
    /// </summary>
    /// <param name="key">The physical `SV8` shelf count cache key.<br/></param>
    /// <param name="value">Receives the cached count and next offset when a current entry is available.<br/></param>
    /// <returns><see langword="true"/> when a current count projection was found; otherwise <see langword="false"/>.<br/></returns>
    private bool TryGetScalar8VarIdentityShelfCountCache(Scalar8VarIdentityShelfCountCacheKey key, out ScalarVarIdentityShelfCountCacheValue value)
    {
        long mutationVersion = kernel.MutationVersion;
        lock (countHeaderCacheSync)
        {
            EnsureCountHeaderCacheVersionUnderLock(mutationVersion);
            if (scalar8VarIdentityShelfCountCache.TryGetValue(key, out value) &&
                kernel.MutationVersion == mutationVersion)
            {
                return true;
            }
        }

        value = default;
        return false;
    }


    /// <summary>
    /// Stores one validated `SV8` shelf count projection at the current raw-storage mutation version.<br/>
    /// The value is copied from an existing shelf header already read by the count operation, keeping repeated count traversal cheap without causing any page write or flush.<br/>
    /// </summary>
    /// <param name="key">The physical `SV8` shelf count cache key.<br/></param>
    /// <param name="count">The validated live item count read from the shelf header.<br/></param>
    /// <param name="nextOffset">The next shelf offset from the shelf header.<br/></param>
    private void StoreScalar8VarIdentityShelfCountCache(Scalar8VarIdentityShelfCountCacheKey key, long count, long nextOffset)
    {
        long mutationVersion = kernel.MutationVersion;
        lock (countHeaderCacheSync)
        {
            EnsureCountHeaderCacheVersionUnderLock(mutationVersion);
            scalar8VarIdentityShelfCountCache[key] = new ScalarVarIdentityShelfCountCacheValue(count, nextOffset);
        }
    }

    private long CountTerminalVarIdentityRootNarrow(long rootOffset, byte expectedShape)
    {
        Span<byte> rootHeader = stackalloc byte[TerminalIdentityRootLayout.HeaderSize];
        kernel.Read(rootOffset, rootHeader);
        if (TerminalIdentityRootLayout.ReadMagic(rootHeader) != TerminalIdentityRootLayout.Magic ||
            TerminalIdentityRootLayout.ReadFormatVersion(rootHeader) != TerminalIdentityRootLayout.FormatVersion ||
            TerminalIdentityRootLayout.ReadHeaderSize(rootHeader) != TerminalIdentityRootLayout.HeaderSize ||
            TerminalIdentityRootLayout.ReadShape(rootHeader) != expectedShape)
        {
            throw new InvalidDataException("The terminal var-identity count root header is invalid.");
        }

        int shelfExtentSize = TerminalIdentityRootLayout.ReadShelfExtentSize(rootHeader);
        long shelfOffset = TerminalIdentityRootLayout.ReadFirstShelfOffset(rootHeader);
        long count = 0;
        HashSet<long> visitedShelves = new();
        while (shelfOffset != 0)
        {
            if (!visitedShelves.Add(shelfOffset))
            {
                break;
            }

            count += ReadTerminalVarIdentityShelfCountNarrow(shelfOffset, shelfExtentSize, out shelfOffset);
        }

        return count;
    }

    private long ReadTerminalVarIdentityShelfCountNarrow(long shelfOffset, int shelfExtentSize, out long nextOffset)
    {
        if (durabilityBatchActive &&
            terminalVarIdentityMutableBatchShelfBytes.TryGetValue(shelfOffset, out byte[]? mutableShelfBytes))
        {
            nextOffset = TerminalVarIdentityShelfLayout.ReadNextShelfOffset(mutableShelfBytes);
            return TerminalVarIdentityShelfLayout.ReadItemCount(mutableShelfBytes);
        }

        Span<byte> header = stackalloc byte[TerminalVarIdentityShelfLayout.HeaderSize];
        kernel.Read(shelfOffset, header);
        if (TerminalVarIdentityShelfLayout.ReadMagic(header) != TerminalVarIdentityShelfLayout.Magic ||
            TerminalVarIdentityShelfLayout.ReadFormatVersion(header) != TerminalVarIdentityShelfLayout.FormatVersion ||
            TerminalVarIdentityShelfLayout.ReadHeaderSize(header) != TerminalVarIdentityShelfLayout.HeaderSize ||
            TerminalVarIdentityShelfLayout.ReadShelfExtentSize(header) != shelfExtentSize)
        {
            throw new InvalidDataException("The terminal var-identity count shelf header is invalid.");
        }

        int count = TerminalVarIdentityShelfLayout.ReadItemCount(header);
        int slotStreamLength = TerminalVarIdentityShelfLayout.ReadSlotStreamLength(header);
        int slotCapacityBytes = TerminalVarIdentityShelfLayout.ReadSlotCapacityBytes(header);
        int recordArenaEnd = TerminalVarIdentityShelfLayout.ReadRecordArenaEnd(header);
        int expectedSlotCapacityBytes = TerminalVarIdentityShelfLayout.CalculateSlotCapacityBytes(shelfExtentSize);
        if (count < 0 ||
            count > expectedSlotCapacityBytes / TerminalVarIdentityShelfLayout.SlotSize ||
            slotStreamLength != checked(count * TerminalVarIdentityShelfLayout.SlotSize) ||
            slotCapacityBytes != expectedSlotCapacityBytes ||
            recordArenaEnd < TerminalVarIdentityShelfLayout.HeaderSize + slotCapacityBytes ||
            recordArenaEnd > shelfExtentSize)
        {
            throw new InvalidDataException("The terminal var-identity count shelf metadata is invalid.");
        }

        nextOffset = TerminalVarIdentityShelfLayout.ReadNextShelfOffset(header);
        return count;
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
        Span<byte> targetHeader = stackalloc byte[sizeof(uint)];
        long routerOffset = rootRouterOffset;
        for (int hop = 0; hop < maxRouterHops; hop++)
        {
            if (TryGetDirectRouterView(routerOffset, out DirectRouterView? directView))
            {
                byte directPrefixByte = GetScalar8VarIdentityPrefix(encodedKey, directView!.KeyDepth);
                long directTargetOffset = directView.GetTarget(directPrefixByte);
                if (directTargetOffset == 0)
                {
                    return new Scalar8VarIdentityRoutePathTarget(
                        new Scalar8VarIdentityRouteTarget(Scalar8VarIdentityRouteTargetKind.None, 0, directView.KeyDepth, directView.AllocationClassId),
                        routerOffset, directPrefixByte, directPrefixByte);
                }

                Scalar8VarIdentityRouteTargetKind directKind = ToScalar8VarIdentityRouteTargetKind(directView.GetTargetKind(directPrefixByte));
                if (directKind == Scalar8VarIdentityRouteTargetKind.None)
                {
                    directKind = ClassifyScalar8VarIdentityRouteTarget(directTargetOffset);
                    directView.SetTargetKind(directPrefixByte, ToScalar8Scalar8RouteTargetKind(directKind));
                }

                if (directKind == Scalar8VarIdentityRouteTargetKind.Shelf ||
                    directKind == Scalar8VarIdentityRouteTargetKind.TerminalVarIdentityRoot)
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

                kernel.Read(directTargetOffset, targetHeader);
                uint targetMagic = BinaryPrimitives.ReadUInt32LittleEndian(targetHeader);
                throw new InvalidDataException($"The routed SV8 target is not a shelf, terminal root, or router. Offset={directTargetOffset}; Magic=0x{targetMagic:x8}; CachedKind={directView.GetTargetKind(directPrefixByte)}; RouterOffset={routerOffset}; Prefix={directPrefixByte}.");
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
                return new Scalar8VarIdentityRoutePathTarget(
                    new Scalar8VarIdentityRouteTarget(Scalar8VarIdentityRouteTargetKind.None, 0, reader.KeyDepth, reader.AllocationClassId),
                    routerOffset, prefixByte, routeIndex);
            }

            Scalar8VarIdentityRouteTargetKind kind = ClassifyScalar8VarIdentityRouteTarget(targetOffset);
            if (kind == Scalar8VarIdentityRouteTargetKind.Shelf ||
                kind == Scalar8VarIdentityRouteTargetKind.TerminalVarIdentityRoot)
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

            throw new InvalidDataException("The routed SV8 target is not a shelf, terminal root, or router.");
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
        long routeAllocatedStartBytes = collectAttribution ? GC.GetAllocatedBytesForCurrentThread() : 0;
        Scalar8VarIdentityRoutePathTarget pathTarget = WalkScalar8VarIdentityRoutePathTarget(rootRouterOffset, encodedKey, maxRouterHops);
        long routeAllocatedBytes = collectAttribution ? GC.GetAllocatedBytesForCurrentThread() - routeAllocatedStartBytes : 0;
        if (collectAttribution)
        {
            routeWalkTicks += Stopwatch.GetTimestamp() - started;
        }

        Scalar8VarIdentityRouteTarget target = pathTarget.Target;
        if (target.Kind == Scalar8VarIdentityRouteTargetKind.None)
        {
            var coldProfile = Scalar8VarIdentityProfile.Create(Scalar8VarIdentityProfile.DefaultInitial.ShelfExtentSize, maxIdentityLength);
            byte[] coldBytes = Scalar8VarIdentity.CreateEmpty(coldProfile);
            var inserted = Scalar8VarIdentity.InsertInPlace(coldBytes, coldProfile, encodedKey, identity, allowDuplicateKeys, out coldBytes);
            if (inserted != Scalar8VarIdentityInsertResult.Inserted)
                throw new InvalidDataException("A cold SV8 shelf rejected its initial tuple.");
            FlushScalar8VarIdentityMutableBatchShelvesBeforeStructuralMutation();
            var (offset, commit) = PublishColdFixedShelf(pathTarget.ParentRouterOffset, pathTarget.RoutePrefixByte, coldBytes);
            attribution = new Scalar8VarIdentityWalkedWriteAttribution(routeWalkTicks, 0, 0, 0, 0,
                collectAttribution ? Stopwatch.GetTimestamp() - started : 0);
            return new Scalar8VarIdentityRoutedInsertResult(Scalar8VarIdentityRoutedInsertKind.WalkedNoSplit, inserted,
                offset, offset, commit, 1, coldProfile.ShelfExtentSize, target.RouterDepth);
        }
        if (target.Kind == Scalar8VarIdentityRouteTargetKind.TerminalVarIdentityRoot)
        {
            started = collectAttribution ? Stopwatch.GetTimestamp() : 0;
            if (!IsScalar8VarIdentityTerminalRootForEncodedKey(target.Offset, encodedKey))
            {
                Scalar8VarIdentityRoutedInsertResult mismatchResult = SplitMismatchedScalar8VarIdentityTerminalRoute(
                    pathTarget,
                    maxIdentityLength,
                    encodedKey,
                    identity,
                    allowDuplicateKeys);
                if (collectAttribution)
                {
                    structuralTicks += Stopwatch.GetTimestamp() - started;
                }

                attribution = new Scalar8VarIdentityWalkedWriteAttribution(routeWalkTicks, shelfReadTicks, duplicateChainTicks, mutationTicks, stageTicks, structuralTicks);
                return mismatchResult with { DiagnosticAllocatedBytes = mismatchResult.DiagnosticAllocatedBytes + routeAllocatedBytes };
            }

            Scalar8VarIdentityRoutedInsertResult terminalResult = InsertIntoScalar8VarIdentityTerminalRoute(
                target.Offset,
                maxIdentityLength,
                encodedKey,
                identity,
                allowDuplicateKeys);
            if (collectAttribution)
            {
                structuralTicks += Stopwatch.GetTimestamp() - started;
            }

            attribution = new Scalar8VarIdentityWalkedWriteAttribution(routeWalkTicks, shelfReadTicks, duplicateChainTicks, mutationTicks, stageTicks, structuralTicks);
            return terminalResult with { DiagnosticAllocatedBytes = terminalResult.DiagnosticAllocatedBytes + routeAllocatedBytes };
        }

        if (target.Kind != Scalar8VarIdentityRouteTargetKind.Shelf)
        {
            throw new InvalidDataException("The classified route walker did not terminate at an SV8 shelf.");
        }

        started = collectAttribution ? Stopwatch.GetTimestamp() : 0;
        if (TryInsertIntoScalar8VarIdentityTailFromHeadHeader(
            target.Offset,
            maxIdentityLength,
            encodedKey,
            identity,
            allowDuplicateKeys,
            out Scalar8VarIdentityRoutedInsertResult headerTailResult))
        {
            if (collectAttribution)
            {
                shelfReadTicks += Stopwatch.GetTimestamp() - started;
                duplicateChainTicks += Stopwatch.GetTimestamp() - started;
            }

            attribution = new Scalar8VarIdentityWalkedWriteAttribution(routeWalkTicks, shelfReadTicks, duplicateChainTicks, mutationTicks, stageTicks, structuralTicks);
            return headerTailResult;
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
        if (EnableScalar8VarIdentityTerminalDuplicateRoutes &&
            EnableScalar8VarIdentityMixedTerminalExtraction &&
            beforeItemCount >= Scalar8VarIdentityTerminalDuplicatePressureItemCount &&
            TryExtractScalar8VarIdentityDuplicateKeyToTerminalRoute(
                rootRouterOffset,
                pathTarget,
                mutableShelf,
                profile,
                encodedKey,
                identity,
                allowDuplicateKeys,
                out Scalar8VarIdentityRoutedInsertResult terminalExtractionResult))
        {
            if (collectAttribution)
            {
                duplicateChainTicks += Stopwatch.GetTimestamp() - started;
            }

            attribution = new Scalar8VarIdentityWalkedWriteAttribution(routeWalkTicks, shelfReadTicks, duplicateChainTicks, mutationTicks, stageTicks, structuralTicks);
            return terminalExtractionResult;
        }

        if (EnableScalar8VarIdentityTerminalDuplicateRoutes &&
            beforeItemCount >= Scalar8VarIdentityTerminalDuplicatePressureItemCount)
        {
            mutableShelf.EnsureSlotBytesCurrent();
        }

        if (EnableScalar8VarIdentityTerminalDuplicateRoutes &&
            beforeItemCount >= Scalar8VarIdentityTerminalDuplicatePressureItemCount &&
            TryConvertScalar8VarIdentityDuplicateRunToTerminalRoute(
                rootRouterOffset,
                pathTarget,
                mutableShelf,
                profile,
                encodedKey,
                identity,
                out Scalar8VarIdentityRoutedInsertResult terminalPressureConversionResult))
        {
            if (collectAttribution)
            {
                duplicateChainTicks += Stopwatch.GetTimestamp() - started;
            }

            attribution = new Scalar8VarIdentityWalkedWriteAttribution(routeWalkTicks, shelfReadTicks, duplicateChainTicks, mutationTicks, stageTicks, structuralTicks);
            return terminalPressureConversionResult;
        }

        if (EnableScalar8VarIdentityTerminalDuplicateRoutes &&
            Scalar8VarIdentityLayout.ReadNextShelfOffset(mutableShelf.Bytes) != 0 &&
            TryConvertScalar8VarIdentityDuplicateRunToTerminalRoute(
                rootRouterOffset,
                pathTarget,
                mutableShelf,
                profile,
                encodedKey,
                identity,
                out Scalar8VarIdentityRoutedInsertResult terminalChainConversionResult))
        {
            if (collectAttribution)
            {
                duplicateChainTicks += Stopwatch.GetTimestamp() - started;
            }

            attribution = new Scalar8VarIdentityWalkedWriteAttribution(routeWalkTicks, shelfReadTicks, duplicateChainTicks, mutationTicks, stageTicks, structuralTicks);
            return terminalChainConversionResult;
        }

        if (Scalar8VarIdentityLayout.ReadNextShelfOffset(mutableShelf.Bytes) != 0)
        {
            mutableShelf.EnsureSlotBytesCurrent();
        }

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

        if (Scalar8VarIdentityLayout.ReadNextShelfOffset(mutableShelf.Bytes) != 0 &&
            TryInsertIntoScalar8VarIdentityOverflowTailFast(
                target.Offset,
                mutableShelf.Bytes,
                profile,
                encodedKey,
                identity,
                allowDuplicateKeys,
                out chainResult))
        {
            if (collectAttribution)
            {
                duplicateChainTicks += Stopwatch.GetTimestamp() - started;
            }

            attribution = new Scalar8VarIdentityWalkedWriteAttribution(routeWalkTicks, shelfReadTicks, duplicateChainTicks, mutationTicks, stageTicks, structuralTicks);
            return chainResult;
        }

        if (Scalar8VarIdentityLayout.ReadNextShelfOffset(mutableShelf.Bytes) != 0 &&
            TryInsertIntoScalar8VarIdentityOverflowChainLocal(
                target.Offset,
                mutableShelf,
                profile,
                encodedKey,
                identity,
                allowDuplicateKeys,
                target.RouterDepth,
                out chainResult))
        {
            if (collectAttribution)
            {
                duplicateChainTicks += Stopwatch.GetTimestamp() - started;
            }

            attribution = new Scalar8VarIdentityWalkedWriteAttribution(routeWalkTicks, shelfReadTicks, duplicateChainTicks, mutationTicks, stageTicks, structuralTicks);
            return chainResult;
        }

        if (Scalar8VarIdentityLayout.ReadNextShelfOffset(mutableShelf.Bytes) != 0)
        {
            if (collectAttribution)
            {
                duplicateChainTicks += Stopwatch.GetTimestamp() - started;
            }

            started = collectAttribution ? Stopwatch.GetTimestamp() : 0;
            mutableShelf.EnsureSlotBytesCurrent();
            if (TrySplitScalar8VarIdentityOverflowChain(
                rootRouterOffset,
                pathTarget.ParentRouterOffset,
                target.Offset,
                target.AllocationClassId,
                target.RouterDepth,
                mutableShelf.Bytes,
                profile,
                encodedKey,
                identity,
                allowDuplicateKeys,
                out Scalar8VarIdentityRoutedInsertResult linkedChainResult))
            {
                if (collectAttribution)
                {
                    structuralTicks += Stopwatch.GetTimestamp() - started;
                }

                attribution = new Scalar8VarIdentityWalkedWriteAttribution(routeWalkTicks, shelfReadTicks, duplicateChainTicks, mutationTicks, stageTicks, structuralTicks);
                return linkedChainResult;
            }

            if (TryRewriteScalar8VarIdentityOverflowChain(
                target.Offset,
                mutableShelf.Bytes,
                profile,
                encodedKey,
                identity,
                allowDuplicateKeys,
                out long linkedTailShelfOffset,
                out int linkedChainShelfCount,
                out int linkedLargestShelfItemCount,
                out Scalar8VarIdentityInsertResult linkedChainInsertResult,
                out DataKernelCommitTelemetry linkedChainTelemetry))
            {
                if (collectAttribution)
                {
                    structuralTicks += Stopwatch.GetTimestamp() - started;
                }

                attribution = new Scalar8VarIdentityWalkedWriteAttribution(routeWalkTicks, shelfReadTicks, duplicateChainTicks, mutationTicks, stageTicks, structuralTicks);
                return new Scalar8VarIdentityRoutedInsertResult(
                    Scalar8VarIdentityRoutedInsertKind.WalkedDuplicateRunOverflow,
                    linkedChainInsertResult,
                    target.Offset,
                    linkedTailShelfOffset,
                    linkedChainTelemetry,
                    linkedLargestShelfItemCount,
                    profile.ShelfExtentSize,
                    target.RouterDepth,
                    checked((ushort)linkedChainShelfCount),
                    Scalar8VarIdentityInsertDiagnosticPath.OverflowChainRewrite);
            }

            if (collectAttribution)
            {
                structuralTicks += Stopwatch.GetTimestamp() - started;
            }

            attribution = new Scalar8VarIdentityWalkedWriteAttribution(routeWalkTicks, shelfReadTicks, duplicateChainTicks, mutationTicks, stageTicks, structuralTicks);
            return new Scalar8VarIdentityRoutedInsertResult(
                Scalar8VarIdentityRoutedInsertKind.Full,
                Scalar8VarIdentityInsertResult.Full,
                target.Offset,
                0,
                default,
                beforeItemCount,
                profile.ShelfExtentSize,
                target.RouterDepth);
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

            FlushScalar8VarIdentityMutableBatchShelvesBeforeStructuralMutation();
            scalar8VarIdentityMutableBatchShelves.Remove(target.Offset);
            RawDataReservation grownReservation = kernel.Reserve(grownProfile.ShelfExtentSize);
            grownShelf.CopyTo(grownReservation.Span);
            RepointMatchingScalar8VarIdentityRoutes(rootRouterOffset, target.Offset, grownReservation.Extent.Offset);
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
        if (EnableScalar8VarIdentityKeyByteSplits &&
            insertResult == Scalar8VarIdentityInsertResult.Full &&
            Scalar8VarIdentityLayout.ReadNextShelfOffset(mutableShelf.Bytes) == 0 &&
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
            FlushScalar8VarIdentityMutableBatchShelvesBeforeStructuralMutation();
            scalar8VarIdentityMutableBatchShelves.Remove(target.Offset);
            RawDataReservation leftReservation = kernel.ReserveAt(target.Offset, profile.ShelfExtentSize);
            leftShelf.CopyTo(leftReservation.Span);
            RawDataReservation rightReservation = kernel.Reserve(profile.ShelfExtentSize);
            rightShelf.CopyTo(rightReservation.Span);
            WriteScalar8VarIdentitySplitDiagnosticIfEnabled(
                "deeper",
                encodedKey,
                pathTarget,
                target,
                splitDepth,
                selectedRightPrefix,
                leftReservation.Extent.Offset,
                rightReservation.Extent.Offset,
                leftShelf,
                rightShelf,
                profile);
            long childRouterOffset = CreateScalar8VarIdentitySplitRouterChain(
                checked((ushort)(target.RouterDepth + 1)),
                splitDepth,
                target.AllocationClassId,
                encodedKey,
                leftReservation.Extent.Offset,
                rightReservation.Extent.Offset,
                selectedRightPrefix,
                profile.Descending);
            RepointMatchingScalar8VarIdentityRoutes(rootRouterOffset, target.Offset, childRouterOffset, target.RouterDepth, encodedKey);
            DataKernelCommitTelemetry telemetry = CommitAndInvalidateRouterReadCache();
            if (collectAttribution)
            {
                structuralTicks += Stopwatch.GetTimestamp() - started;
            }

            attribution = new Scalar8VarIdentityWalkedWriteAttribution(routeWalkTicks, shelfReadTicks, duplicateChainTicks, mutationTicks, stageTicks, structuralTicks);
            return new Scalar8VarIdentityRoutedInsertResult(
                Scalar8VarIdentityRoutedInsertKind.WalkedShelfSplit,
                splitInsertResult,
                childRouterOffset,
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
        if (EnableScalar8VarIdentitySameDepthSplits &&
            EnableScalar8VarIdentityKeyByteSplits &&
            insertResult == Scalar8VarIdentityInsertResult.Full &&
            Scalar8VarIdentityLayout.ReadNextShelfOffset(mutableShelf.Bytes) == 0 &&
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
            FlushScalar8VarIdentityMutableBatchShelvesBeforeStructuralMutation();
            scalar8VarIdentityMutableBatchShelves.Remove(target.Offset);
            RawDataReservation leftReservation = kernel.ReserveAt(target.Offset, profile.ShelfExtentSize);
            leftShelf.CopyTo(leftReservation.Span);
            RawDataReservation rightReservation = kernel.Reserve(profile.ShelfExtentSize);
            rightShelf.CopyTo(rightReservation.Span);
            WriteScalar8VarIdentitySplitDiagnosticIfEnabled(
                "same-depth",
                encodedKey,
                pathTarget,
                target,
                target.RouterDepth,
                selectedRightPrefix,
                leftReservation.Extent.Offset,
                rightReservation.Extent.Offset,
                leftShelf,
                rightShelf,
                profile);
            RepointSameDepthScalar8VarIdentityRoutes(rootRouterOffset, target.Offset, selectedRightPrefix, leftReservation.Extent.Offset, rightReservation.Extent.Offset, target.RouterDepth, profile.Descending);
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
        if (EnableScalar8VarIdentityTerminalDuplicateRoutes &&
            insertResult == Scalar8VarIdentityInsertResult.Full &&
            TryConvertScalar8VarIdentityDuplicateRunToTerminalRoute(
                rootRouterOffset,
                pathTarget,
                mutableShelf,
                profile,
                encodedKey,
                identity,
                out Scalar8VarIdentityRoutedInsertResult terminalConversionResult))
        {
            if (collectAttribution)
            {
                structuralTicks += Stopwatch.GetTimestamp() - started;
            }

            attribution = new Scalar8VarIdentityWalkedWriteAttribution(routeWalkTicks, shelfReadTicks, duplicateChainTicks, mutationTicks, stageTicks, structuralTicks);
            return terminalConversionResult;
        }

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
            TryAppendScalar8VarIdentityOverflowTail(
                target.Offset,
                target.Offset,
                mutableShelf.Bytes,
                profile,
                encodedKey,
                identity,
                allowDuplicateKeys,
                out appendedTailResult))
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

        started = collectAttribution ? Stopwatch.GetTimestamp() : 0;
        mutableShelf.EnsureSlotBytesCurrent();
        if (insertResult == Scalar8VarIdentityInsertResult.Full &&
            TryRewriteScalar8VarIdentityOverflowChain(
                target.Offset,
                mutableShelf.Bytes,
                profile,
                encodedKey,
                identity,
                allowDuplicateKeys,
                out tailShelfOffset,
                out chainShelfCount,
                out largestShelfItemCount,
                out chainInsertResult,
                out chainTelemetry))
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
        ulong upperEncodedKey,
        QueryDirection direction = QueryDirection.Ascending,
        bool physicalDescending = false)
    {
        if (lowerEncodedKey > upperEncodedKey)
        {
            throw new ArgumentException("The upper SV8 key must be greater than or equal to the lower key.", nameof(upperEncodedKey));
        }

        return new Scalar8VarIdentityRangeReader(this, rootRouterOffset, maxIdentityLength, lowerEncodedKey, upperEncodedKey, direction, physicalDescending);
    }

    /// <summary>
    /// Deletes live `SV8` tuples whose encoded key falls inside the inclusive scalar-key range.<br/>
    /// The traversal prunes by routed key prefixes and also walks duplicate-key overflow shelves linked from each matched route-owned shelf.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The root router offset for the index.</param>
    /// <param name="maxIdentityLength">The maximum raw identity length accepted by the profile family.</param>
    /// <param name="lowerEncodedKey">The inclusive lower encoded key.</param>
    /// <param name="upperEncodedKey">The inclusive upper encoded key.</param>
    /// <param name="maxRouterHops">The maximum number of router pages to follow.</param>
    /// <returns>The number of live tuples deleted.</returns>
    internal long DeleteScalar8VarIdentityKeyRange(
        long rootRouterOffset,
        int maxIdentityLength,
        ulong lowerEncodedKey,
        ulong upperEncodedKey,
        int maxRouterHops = DefaultScalar8VarIdentityMaxRouterHops)
    {
        if (lowerEncodedKey > upperEncodedKey)
        {
            return 0;
        }

        long deleted = 0;
        using RouteVisitedOffsetSet visitedShelves = RouteVisitedOffsetSet.Rent();
        using RouteVisitedOffsetSet visitedRouters = RouteVisitedOffsetSet.Rent();
        byte lowerPrefix = GetScalar8VarIdentityPrefix(lowerEncodedKey, 0);
        byte upperPrefix = GetScalar8VarIdentityPrefix(upperEncodedKey, 0);
        for (int prefix = lowerPrefix; prefix <= upperPrefix; prefix++)
        {
            long targetOffset = FindRouterTarget(rootRouterOffset, (byte)prefix);
            if (targetOffset == 0)
            {
                continue;
            }

            deleted += DeleteScalar8VarIdentityRangeFromTarget(
                targetOffset,
                maxIdentityLength,
                lowerEncodedKey,
                upperEncodedKey,
                visitedShelves,
                visitedRouters,
                maxRouterHops);
        }

        return deleted;
    }

    /// <summary>
    /// Attempts one routed `SV8` insert through a writer-local ordinary-shelf context without allowing topology changes.<br/>
    /// The method supports warmed ordinary shelves that are not linked duplicate-run heads; growth, terminal duplicate routes, overflow chains, and split paths throw so callers can use the existing topology path.<br/>
    /// </summary>
    /// <param name="writeContext">The writer context that owns staged ordinary shelf mutations.<br/></param>
    /// <param name="rootRouterOffset">The root router offset for the index.<br/></param>
    /// <param name="maxIdentityLength">The maximum raw identity length accepted by the index profile.<br/></param>
    /// <param name="encodedKey">The encoded sortable 8-byte key.</param>
    /// <param name="identity">The raw variable-length identity bytes to insert.<br/></param>
    /// <param name="allowDuplicateKeys">Whether duplicate keys with different identities are allowed.<br/></param>
    /// <param name="maxRouterHops">The maximum number of router pages to follow.<br/></param>
    /// <returns>The storage-facing routed insert result for the writer-local attempt.<br/></returns>
    /// <exception cref="InvalidOperationException">Thrown when the reached route needs topology work or another unsupported writer-context shape.<br/></exception>
    internal Scalar8VarIdentityRoutedInsertResult InsertWalkedRoutedScalar8VarIdentityNoSplitForWriteContext(
        LibraDexWriteContext writeContext,
        long rootRouterOffset,
        int maxIdentityLength,
        ulong encodedKey,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys,
        int maxRouterHops = DefaultScalar8VarIdentityMaxRouterHops)
    {
        long routeWalkStart = Stopwatch.GetTimestamp();
        Scalar8VarIdentityRoutePathTarget pathTarget;
        lock (routerReadCacheSync)
        {
            pathTarget = WalkScalar8VarIdentityRoutePathTarget(rootRouterOffset, encodedKey, maxRouterHops);
        }

        long routeWalkTicks = Stopwatch.GetTimestamp() - routeWalkStart;
        Scalar8VarIdentityRouteTarget target = pathTarget.Target;
        if (target.Kind != Scalar8VarIdentityRouteTargetKind.Shelf)
        {
            throw new InvalidOperationException("The SV8 writer-context path supports only ordinary warmed shelf routes.");
        }

        RecordScalar8VarIdentityRouteClaimForWriteContext(writeContext, pathTarget);
        long shelfReadStart = Stopwatch.GetTimestamp();
        Scalar8VarIdentityMutableShelfView shelf = ReadScalar8VarIdentityMutableShelfForWriteContext(writeContext, target.Offset, maxIdentityLength);
        Scalar8VarIdentityProfile profile = shelf.Profile;
        int beforeItemCount = shelf.ItemCount;
        if (Scalar8VarIdentityLayout.ReadNextShelfOffset(shelf.Bytes) != 0)
        {
            throw new InvalidOperationException("The SV8 writer-context path does not yet support linked duplicate-run shelves.");
        }

        long shelfReadTicks = Stopwatch.GetTimestamp() - shelfReadStart;
        long mutationStart = Stopwatch.GetTimestamp();
        Scalar8VarIdentityInsertResult insertResult = shelf.Insert(encodedKey, identity, allowDuplicateKeys);
        long mutationTicks = Stopwatch.GetTimestamp() - mutationStart;
        if (insertResult == Scalar8VarIdentityInsertResult.Full)
        {
            throw new InvalidOperationException("The SV8 writer-context path does not yet support shelf growth or split transforms.");
        }

        Scalar8VarIdentityRoutedInsertKind kind = insertResult switch
        {
            Scalar8VarIdentityInsertResult.Inserted => Scalar8VarIdentityRoutedInsertKind.WalkedNoSplit,
            Scalar8VarIdentityInsertResult.KeyConflict => Scalar8VarIdentityRoutedInsertKind.KeyConflict,
            _ => Scalar8VarIdentityRoutedInsertKind.NoOp
        };
        return new Scalar8VarIdentityRoutedInsertResult(
            kind,
            insertResult,
            target.Offset,
            insertResult == Scalar8VarIdentityInsertResult.Inserted ? target.Offset : 0,
            default,
            insertResult == Scalar8VarIdentityInsertResult.Inserted ? beforeItemCount + 1 : beforeItemCount,
            profile.ShelfExtentSize,
            target.RouterDepth);
    }

    /// <summary>
    /// Attempts one exact `SV8` tuple delete through a writer-local ordinary-shelf context without following terminal or linked duplicate-run routes.<br/>
    /// Unsupported route shapes throw so callers can fall back to the existing durability-batch delete path.<br/>
    /// </summary>
    /// <param name="writeContext">The writer context that owns staged ordinary shelf mutations.<br/></param>
    /// <param name="rootRouterOffset">The root router offset for the index.<br/></param>
    /// <param name="maxIdentityLength">The maximum raw identity length accepted by the index profile.<br/></param>
    /// <param name="encodedKey">The exact encoded sortable 8-byte key.<br/></param>
    /// <param name="identity">The exact raw identity bytes to delete.<br/></param>
    /// <param name="maxRouterHops">The maximum number of router pages to follow.<br/></param>
    /// <returns><see langword="true"/> when one live tuple was marked deleted in the writer-local shelf.</returns>
    internal bool DeleteScalar8VarIdentityExactTupleForWriteContext(
        LibraDexWriteContext writeContext,
        long rootRouterOffset,
        int maxIdentityLength,
        ulong encodedKey,
        ReadOnlySpan<byte> identity,
        int maxRouterHops = DefaultScalar8VarIdentityMaxRouterHops)
    {
        Scalar8VarIdentityRoutePathTarget pathTarget;
        lock (routerReadCacheSync)
        {
            pathTarget = WalkScalar8VarIdentityRoutePathTarget(rootRouterOffset, encodedKey, maxRouterHops);
        }

        Scalar8VarIdentityRouteTarget target = pathTarget.Target;
        if (target.Kind != Scalar8VarIdentityRouteTargetKind.Shelf)
        {
            throw new InvalidOperationException("The SV8 writer-context exact delete path supports only ordinary warmed shelf routes.");
        }

        RecordScalar8VarIdentityRouteClaimForWriteContext(writeContext, pathTarget);
        Scalar8VarIdentityMutableShelfView shelf = ReadScalar8VarIdentityMutableShelfForWriteContext(writeContext, target.Offset, maxIdentityLength);
        if (Scalar8VarIdentityLayout.ReadNextShelfOffset(shelf.Bytes) != 0)
        {
            throw new InvalidOperationException("The SV8 writer-context exact delete path does not yet support linked duplicate-run shelves.");
        }

        return shelf.MarkTupleDeleted(encodedKey, identity);
    }

    /// <summary>
    /// Deletes an inclusive encoded-key `SV8` range through a writer-local ordinary-shelf context without allowing terminal or linked duplicate-run route cleanup.<br/>
    /// This supports warmed shelf-local range tombstoning; unsupported route shapes throw so callers can fall back to the existing durability-batch range delete path.<br/>
    /// </summary>
    /// <param name="writeContext">The writer context that owns staged ordinary shelf mutations.<br/></param>
    /// <param name="rootRouterOffset">The root router offset for the index.<br/></param>
    /// <param name="maxIdentityLength">The maximum raw identity length accepted by the index profile.<br/></param>
    /// <param name="lowerEncodedKey">The inclusive lower encoded key.<br/></param>
    /// <param name="upperEncodedKey">The inclusive upper encoded key.<br/></param>
    /// <param name="maxRouterHops">The maximum number of router pages to follow.<br/></param>
    /// <returns>The number of live tuples marked deleted in writer-local shelves.<br/></returns>
    internal long DeleteScalar8VarIdentityKeyRangeForWriteContext(
        LibraDexWriteContext writeContext,
        long rootRouterOffset,
        int maxIdentityLength,
        ulong lowerEncodedKey,
        ulong upperEncodedKey,
        int maxRouterHops = DefaultScalar8VarIdentityMaxRouterHops)
    {
        ArgumentNullException.ThrowIfNull(writeContext);
        if (lowerEncodedKey > upperEncodedKey)
        {
            return 0;
        }

        lock (routerReadCacheSync)
        {
            long deleted = 0;
            using RouteVisitedOffsetSet visitedShelves = RouteVisitedOffsetSet.Rent();
            using RouteVisitedOffsetSet visitedRouters = RouteVisitedOffsetSet.Rent();
            byte lowerPrefix = GetScalar8VarIdentityPrefix(lowerEncodedKey, 0);
            byte upperPrefix = GetScalar8VarIdentityPrefix(upperEncodedKey, 0);
            for (int prefix = lowerPrefix; prefix <= upperPrefix; prefix++)
            {
                long targetOffset = FindRouterTarget(rootRouterOffset, (byte)prefix);
                if (targetOffset == 0)
                {
                    continue;
                }

                deleted += DeleteScalar8VarIdentityRangeFromTargetForWriteContext(
                    writeContext,
                    rootRouterOffset,
                    prefix,
                    targetOffset,
                    maxIdentityLength,
                    lowerEncodedKey,
                    upperEncodedKey,
                    visitedShelves,
                    visitedRouters,
                    maxRouterHops);
            }

            return deleted;
        }
    }

    /// <summary>
    /// Recursively deletes one `SV8` range target through a writer-local ordinary-shelf context.<br/>
    /// Router targets are traversed, ordinary non-linked shelves are tombstoned, and terminal or linked duplicate-run targets are rejected for fallback handling.<br/>
    /// </summary>
    /// <param name="writeContext">The writer context that owns staged ordinary shelf mutations.<br/></param>
    /// <param name="parentRouterOffset">The parent router whose route slot selected <paramref name="targetOffset"/>.<br/></param>
    /// <param name="routeIndex">The parent-router route slot that selected <paramref name="targetOffset"/>.<br/></param>
    /// <param name="targetOffset">The routed target offset to process.<br/></param>
    /// <param name="maxIdentityLength">The maximum raw identity length accepted by the index profile.<br/></param>
    /// <param name="lowerEncodedKey">The inclusive lower encoded key.<br/></param>
    /// <param name="upperEncodedKey">The inclusive upper encoded key.<br/></param>
    /// <param name="visitedShelves">The visited shelf set used to avoid repeated mutation of shared targets.<br/></param>
    /// <param name="visitedRouters">The visited router set used to avoid route cycles.<br/></param>
    /// <param name="remainingRouterHops">The remaining router hop budget.<br/></param>
    /// <returns>The number of live tuples marked deleted.<br/></returns>
    private long DeleteScalar8VarIdentityRangeFromTargetForWriteContext(
        LibraDexWriteContext writeContext,
        long parentRouterOffset,
        int routeIndex,
        long targetOffset,
        int maxIdentityLength,
        ulong lowerEncodedKey,
        ulong upperEncodedKey,
        RouteVisitedOffsetSet visitedShelves,
        RouteVisitedOffsetSet visitedRouters,
        int remainingRouterHops)
    {
        Scalar8VarIdentityRouteTargetKind kind = ClassifyScalar8VarIdentityRouteTarget(targetOffset);
        if (kind == Scalar8VarIdentityRouteTargetKind.Shelf)
        {
            RecordScalar8VarIdentityRouteClaimForWriteContext(writeContext, parentRouterOffset, routeIndex, targetOffset);
            if (!visitedShelves.Add(targetOffset))
            {
                return 0;
            }

            Scalar8VarIdentityMutableShelfView shelf = ReadScalar8VarIdentityMutableShelfForWriteContext(writeContext, targetOffset, maxIdentityLength);
            if (Scalar8VarIdentityLayout.ReadNextShelfOffset(shelf.Bytes) != 0)
            {
                throw new InvalidOperationException("The SV8 writer-context range delete path does not yet support linked duplicate-run shelves.");
            }

            return shelf.MarkKeyRangeDeleted(lowerEncodedKey, upperEncodedKey);
        }

        if (kind == Scalar8VarIdentityRouteTargetKind.TerminalVarIdentityRoot)
        {
            throw new InvalidOperationException("The SV8 writer-context range delete path does not yet support terminal duplicate routes.");
        }

        if (kind != Scalar8VarIdentityRouteTargetKind.Router)
        {
            throw new InvalidDataException("The routed SV8 writer-context range delete target is not a shelf, terminal root, or router.");
        }

        if (!visitedRouters.Add(targetOffset))
        {
            return 0;
        }

        if (remainingRouterHops <= 0)
        {
            throw new InvalidDataException("The routed SV8 writer-context range delete exceeded the configured router hop count.");
        }

        long deletedFromChildren = 0;
        Span<byte> routerBytes = stackalloc byte[RouterLayout.Size];
        ReadRouterPageUsingArenaCache(targetOffset, routerBytes);
        RouterReader reader = new(routerBytes);
        if (!reader.IsValid)
        {
            throw new InvalidDataException("The routed SV8 writer-context range delete router is invalid.");
        }

        bool scanDescendantRoutes = reader.KeyDepth > 0;
        byte lowerPrefix = scanDescendantRoutes ? byte.MinValue : GetScalar8VarIdentityPrefix(lowerEncodedKey, reader.KeyDepth);
        byte upperPrefix = scanDescendantRoutes ? byte.MaxValue : GetScalar8VarIdentityPrefix(upperEncodedKey, reader.KeyDepth);
        long previousChildTargetOffset = 0;
        for (int prefix = lowerPrefix; prefix <= upperPrefix; prefix++)
        {
            long childTargetOffset = reader.FindTarget((byte)prefix, out int childRouteIndex);
            if (childTargetOffset == 0 || childTargetOffset == previousChildTargetOffset)
            {
                continue;
            }

            previousChildTargetOffset = childTargetOffset;
            deletedFromChildren += DeleteScalar8VarIdentityRangeFromTargetForWriteContext(
                writeContext,
                targetOffset,
                childRouteIndex,
                childTargetOffset,
                maxIdentityLength,
                lowerEncodedKey,
                upperEncodedKey,
                visitedShelves,
                visitedRouters,
                remainingRouterHops - 1);
        }

        return deletedFromChildren;
    }

    /// <summary>
    /// Deletes one exact `SV8` encoded-key/raw-identity tuple from the routed tree or a duplicate-key overflow chain.<br/>
    /// The route walk targets the key's owning shelf and then follows same-key overflow shelves until the tuple is found or the chain ends.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The root router offset for the index.</param>
    /// <param name="maxIdentityLength">The maximum raw identity length accepted by the profile family.</param>
    /// <param name="encodedKey">The exact encoded scalar key.</param>
    /// <param name="identity">The exact raw identity bytes.</param>
    /// <param name="maxRouterHops">The maximum number of router pages to follow.</param>
    /// <returns><see langword="true"/> when a live tuple was deleted.</returns>
    internal bool DeleteScalar8VarIdentityExactTuple(
        long rootRouterOffset,
        int maxIdentityLength,
        ulong encodedKey,
        ReadOnlySpan<byte> identity,
        int maxRouterHops = DefaultScalar8VarIdentityMaxRouterHops)
    {
        Scalar8VarIdentityRoutePathTarget pathTarget = WalkScalar8VarIdentityRoutePathTarget(rootRouterOffset, encodedKey, maxRouterHops);
        if (pathTarget.Target.Kind == Scalar8VarIdentityRouteTargetKind.TerminalVarIdentityRoot)
        {
            return DeleteScalar8VarIdentityTerminalExactTuple(pathTarget.Target.Offset, encodedKey, identity);
        }

        if (pathTarget.Target.Kind != Scalar8VarIdentityRouteTargetKind.Shelf)
        {
            throw new InvalidDataException("The classified route walker did not terminate at an SV8 shelf for exact tuple delete.");
        }

        using RouteVisitedOffsetSet visitedShelves = RouteVisitedOffsetSet.Rent();
        long shelfOffset = pathTarget.Target.Offset;
        while (shelfOffset != 0 && visitedShelves.Add(shelfOffset))
        {
            Scalar8VarIdentityMutableShelfView shelf = ReadScalar8VarIdentityMutableShelf(shelfOffset, maxIdentityLength);
            long nextShelfOffset = Scalar8VarIdentityLayout.ReadNextShelfOffset(shelf.Bytes);
            if (shelf.MarkTupleDeleted(encodedKey, identity))
            {
                if (!durabilityBatchActive)
                {
                    _ = shelf.NormalizeDeletedSlotsForPublication();
                }

                shelf.EnsureSlotBytesCurrent();
                _ = StageScalar8VarIdentityShelfRewrite(shelfOffset, shelf, shelf.Bytes);
                return true;
            }

            shelfOffset = nextShelfOffset;
        }

        return false;
    }

    private long DeleteScalar8VarIdentityRangeFromTarget(
        long targetOffset,
        int maxIdentityLength,
        ulong lowerEncodedKey,
        ulong upperEncodedKey,
        RouteVisitedOffsetSet visitedShelves,
        RouteVisitedOffsetSet visitedRouters,
        int remainingRouterHops)
    {
        Scalar8VarIdentityRouteTargetKind kind = ClassifyScalar8VarIdentityRouteTarget(targetOffset);
        if (kind == Scalar8VarIdentityRouteTargetKind.TerminalVarIdentityRoot)
        {
            byte[] rootBytes = ReadTerminalIdentityRootBytes(targetOffset);
            if (TerminalIdentityRootLayout.ReadShape(rootBytes) != TerminalIdentityRootLayout.ShapeScalar8VarIdentity ||
                TerminalIdentityRootLayout.ReadKeyLength(rootBytes) != Scalar8VarIdentityLayout.KeySize)
            {
                throw new InvalidDataException("The routed SV8 terminal var identity root is invalid.");
            }

            ulong encodedKey = BinaryPrimitives.ReadUInt64BigEndian(rootBytes.AsSpan(TerminalIdentityRootLayout.KeyBytesOffset, Scalar8VarIdentityLayout.KeySize));
            if (encodedKey < lowerEncodedKey || encodedKey > upperEncodedKey)
            {
                return 0;
            }

            int shelfExtentSize = TerminalIdentityRootLayout.ReadShelfExtentSize(rootBytes);
            Span<byte> keyBytes = stackalloc byte[Scalar8VarIdentityLayout.KeySize];
            BinaryPrimitives.WriteUInt64BigEndian(keyBytes, encodedKey);
            byte[][] identities = ReadScalar8VarIdentityTerminalIdentities(targetOffset, keyBytes, shelfExtentSize);
            if (identities.Length == 0)
            {
                return 0;
            }

            _ = RewriteScalar8VarIdentityTerminalRoute(
                targetOffset,
                TerminalIdentityRootLayout.ShapeScalar8VarIdentity,
                keyBytes,
                shelfExtentSize,
                Array.Empty<byte[]>());
            return identities.Length;
        }

        if (kind == Scalar8VarIdentityRouteTargetKind.Shelf)
        {
            long currentShelfOffset = targetOffset;
            long deletedFromChain = 0;
            while (currentShelfOffset != 0)
            {
                if (!visitedShelves.Add(currentShelfOffset))
                {
                    break;
                }

                Scalar8VarIdentityMutableShelfView shelf = ReadScalar8VarIdentityMutableShelf(currentShelfOffset, maxIdentityLength);
                long nextShelfOffset = Scalar8VarIdentityLayout.ReadNextShelfOffset(shelf.Bytes);
                int deleted = shelf.MarkKeyRangeDeleted(lowerEncodedKey, upperEncodedKey);
                if (deleted != 0)
                {
                    if (!durabilityBatchActive)
                    {
                        _ = shelf.NormalizeDeletedSlotsForPublication();
                    }

                    shelf.EnsureSlotBytesCurrent();
                    _ = StageScalar8VarIdentityShelfRewrite(currentShelfOffset, shelf, shelf.Bytes);
                    deletedFromChain += deleted;
                }

                currentShelfOffset = nextShelfOffset;
            }

            return deletedFromChain;
        }

        if (kind != Scalar8VarIdentityRouteTargetKind.Router)
        {
            throw new InvalidDataException("The routed SV8 delete target is not a shelf or router.");
        }

        if (!visitedRouters.Add(targetOffset))
        {
            return 0;
        }

        if (remainingRouterHops <= 0)
        {
            throw new InvalidDataException("The routed SV8 range delete exceeded the configured router hop count.");
        }

        long deletedFromChildren = 0;
        Span<byte> routerBytes = stackalloc byte[RouterLayout.Size];
        ReadRouterPageUsingArenaCache(targetOffset, routerBytes);
        RouterReader reader = new(routerBytes);
        if (!reader.IsValid)
        {
            throw new InvalidDataException("The routed SV8 range delete router is invalid.");
        }

        bool scanDescendantRoutes = reader.KeyDepth > 0;
        byte lowerPrefix = scanDescendantRoutes ? byte.MinValue : GetScalar8VarIdentityPrefix(lowerEncodedKey, reader.KeyDepth);
        byte upperPrefix = scanDescendantRoutes ? byte.MaxValue : GetScalar8VarIdentityPrefix(upperEncodedKey, reader.KeyDepth);
        long previousChildTargetOffset = 0;
        for (int prefix = lowerPrefix; prefix <= upperPrefix; prefix++)
        {
            long childTargetOffset = reader.FindTarget((byte)prefix);
            if (childTargetOffset == 0 || childTargetOffset == previousChildTargetOffset)
            {
                continue;
            }

            previousChildTargetOffset = childTargetOffset;
            deletedFromChildren += DeleteScalar8VarIdentityRangeFromTarget(
                childTargetOffset,
                maxIdentityLength,
                lowerEncodedKey,
                upperEncodedKey,
                visitedShelves,
                visitedRouters,
                remainingRouterHops - 1);
        }

        return deletedFromChildren;
    }

    internal byte[] ReadScalar8VarIdentityShelfBytes(long shelfOffset, int maxIdentityLength, out Scalar8VarIdentityProfile profile)
    {
        if (durabilityBatchActive &&
            scalar8VarIdentityMutableBatchShelves.TryGetValue(shelfOffset, out Scalar8VarIdentityMutableShelfView? mutableShelf))
        {
            mutableShelf.EnsureSlotBytesCurrent();
            profile = mutableShelf.Profile;
            return mutableShelf.Bytes;
        }

        Span<byte> header = stackalloc byte[Scalar8VarIdentityLayout.HeaderSize];
        kernel.Read(shelfOffset, header);
        if (Scalar8VarIdentityLayout.ReadMagic(header) != Scalar8VarIdentityLayout.Magic ||
            Scalar8VarIdentityLayout.ReadFormatVersion(header) != Scalar8VarIdentityLayout.FormatVersion ||
            Scalar8VarIdentityLayout.ReadHeaderSize(header) != Scalar8VarIdentityLayout.HeaderSize)
        {
            throw new InvalidDataException("The routed SV8 shelf header is invalid.");
        }

        int shelfExtentSize = Scalar8VarIdentityLayout.ReadShelfExtentSize(header);
        profile = Scalar8VarIdentityProfile.Create(shelfExtentSize, maxIdentityLength, (Scalar8VarIdentityLayout.ReadFlags(header) & 1U) != 0);
        byte[] shelfBytes = new byte[shelfExtentSize];
        kernel.Read(shelfOffset, shelfBytes);
        return shelfBytes;
    }

    /// <summary>
    /// Reads one immutable decoded `SV8` shelf view for range and exact-key cursors.<br/>
    /// Repeated reads reuse both the persisted shelf bytes and decoded slot sidecars until the session mutation boundary invalidates them.<br/>
    /// Active durability batches bypass the cache so pending mutable shelf images remain authoritative.<br/>
    /// </summary>
    /// <param name="indexRootOffset">The owning index root used for per-index cache accounting.<br/></param>
    /// <param name="shelfOffset">The file offset of the ordinary `SV8` shelf.<br/></param>
    /// <param name="maxIdentityLength">The maximum raw identity length accepted by the index.<br/></param>
    /// <returns>The validated read-only shelf view.<br/></returns>
    internal Scalar8VarIdentityReadOnly ReadScalar8VarIdentityReadOnlyShelf(
        long indexRootOffset,
        long shelfOffset,
        int maxIdentityLength)
    {
        int cacheGeneration = scalar8VarIdentityReadCache.Generation;
        if (!durabilityBatchActive &&
            scalar8VarIdentityReadCache.TryGet(indexRootOffset, shelfOffset, out Scalar8VarIdentityReadOnly cachedShelf))
        {
            return cachedShelf;
        }

        byte[] shelfBytes = ReadScalar8VarIdentityShelfBytes(shelfOffset, maxIdentityLength, out Scalar8VarIdentityProfile profile);
        Scalar8VarIdentityReadOnly shelf = durabilityBatchActive
            ? new Scalar8VarIdentityReadOnly((ReadOnlyMemory<byte>)shelfBytes, profile, validateRecords: false)
            : new Scalar8VarIdentityReadOnly(shelfBytes, profile, validateRecords: false);
        if (!shelf.IsValid)
        {
            throw new InvalidDataException("The routed SV8 read-only shelf bytes are invalid.");
        }

        if (!durabilityBatchActive)
        {
            shelf = scalar8VarIdentityReadCache.Store(
                indexRootOffset,
                shelfOffset,
                shelf,
                cacheGeneration);
        }

        return shelf;
    }

    /// <summary>
    /// Builds a diagnostic report of every routed descendant under the root-prefix branch that physically contains one exact `SV8` scalar key.<br/>
    /// This method is intentionally diagnostic-only: it scans descendant routes so route-pruned reader misses can be compared against the persisted tree topology without changing hot reader behavior.<br/>
    /// The report includes whether each found shelf path stayed on the exact scalar-key byte at every router depth, making route-publication invariant breaks visible in runner logs.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The root router offset for the `SV8` index.<br/></param>
    /// <param name="maxIdentityLength">The maximum variable identity byte length accepted by the index profile family.<br/></param>
    /// <param name="encodedKey">The exact encoded scalar key to locate.<br/></param>
    /// <param name="maxRows">The maximum individual matching identities to print before suppressing additional row detail.<br/></param>
    /// <returns>A compact multi-line diagnostic report.</returns>
    internal string DescribeScalar8VarIdentityExactKeyRoutes(
        long rootRouterOffset,
        int maxIdentityLength,
        ulong encodedKey,
        int maxRows)
    {
        StringBuilder builder = new();
        using RouteVisitedOffsetSet visitedShelves = RouteVisitedOffsetSet.Rent();
        using RouteVisitedOffsetSet visitedRouters = RouteVisitedOffsetSet.Rent();
        byte rootPrefix = GetScalar8VarIdentityPrefix(encodedKey, 0);
        long rootTargetOffset = FindRouterTarget(rootRouterOffset, rootPrefix);
        builder.Append("SV8 exact-key route diagnostic key=").Append(encodedKey)
            .Append("; rootPrefix=").Append(rootPrefix)
            .Append("; rootTarget=").Append(rootTargetOffset)
            .Append("; rootTargetKind=").Append(rootTargetOffset == 0 ? Scalar8VarIdentityRouteTargetKind.None : ClassifyScalar8VarIdentityRouteTarget(rootTargetOffset))
            .AppendLine();
        if (rootTargetOffset == 0)
        {
            return builder.ToString();
        }

        int foundShelfCount = 0;
        int foundRowCount = 0;
        int printedRowCount = 0;
        DescribeScalar8VarIdentityExactKeyRoutesFromTarget(
            rootTargetOffset,
            maxIdentityLength,
            encodedKey,
            visitedShelves,
            visitedRouters,
            remainingRouterHops: DefaultScalar8VarIdentityMaxRouterHops,
            path: "root",
            exactPath: true,
            maxRows,
            ref foundShelfCount,
            ref foundRowCount,
            ref printedRowCount,
            builder);
        builder.Append("SV8 exact-key route diagnostic totals shelves=").Append(foundShelfCount)
            .Append("; rows=").Append(foundRowCount)
            .Append("; printedRows=").Append(printedRowCount)
            .AppendLine();
        return builder.ToString();
    }

    /// <summary>
    /// Scans one routed `SV8` diagnostic target for a single exact scalar key and appends matching route/shelf evidence to the report.<br/>
    /// Router targets are scanned across all route entries while carrying an `exactPath` flag that records whether the path matched the scalar-key byte at every router depth.<br/>
    /// Shelf targets are scanned through linked overflow shelves so duplicate-run and mixed overflow topology is visible from one diagnostic call.<br/>
    /// </summary>
    /// <param name="targetOffset">The router, shelf, or terminal root offset to inspect.<br/></param>
    /// <param name="maxIdentityLength">The maximum identity length for reading ordinary shelves.<br/></param>
    /// <param name="encodedKey">The exact scalar key to locate.<br/></param>
    /// <param name="visitedShelves">The per-report shelf cycle guard.<br/></param>
    /// <param name="visitedRouters">The per-report router cycle guard.<br/></param>
    /// <param name="remainingRouterHops">The remaining router hop budget.<br/></param>
    /// <param name="path">The route path text accumulated so far.<br/></param>
    /// <param name="exactPath">Whether every route edge so far matched the exact scalar-key byte for its router depth.<br/></param>
    /// <param name="maxRows">The maximum number of identity rows to print.<br/></param>
    /// <param name="foundShelfCount">The number of shelf or terminal roots containing the exact key.<br/></param>
    /// <param name="foundRowCount">The number of rows found for the exact key.<br/></param>
    /// <param name="printedRowCount">The number of individual identity rows printed.<br/></param>
    /// <param name="builder">The report builder receiving diagnostic lines.<br/></param>
    private void DescribeScalar8VarIdentityExactKeyRoutesFromTarget(
        long targetOffset,
        int maxIdentityLength,
        ulong encodedKey,
        RouteVisitedOffsetSet visitedShelves,
        RouteVisitedOffsetSet visitedRouters,
        int remainingRouterHops,
        string path,
        bool exactPath,
        int maxRows,
        ref int foundShelfCount,
        ref int foundRowCount,
        ref int printedRowCount,
        StringBuilder builder)
    {
        Scalar8VarIdentityRouteTargetKind kind = ClassifyScalar8VarIdentityRouteTarget(targetOffset);
        if (kind == Scalar8VarIdentityRouteTargetKind.TerminalVarIdentityRoot)
        {
            byte[] rootBytes = ReadTerminalIdentityRootBytes(targetOffset);
            ulong terminalKey = BinaryPrimitives.ReadUInt64BigEndian(rootBytes.AsSpan(TerminalIdentityRootLayout.KeyBytesOffset, Scalar8VarIdentityLayout.KeySize));
            if (terminalKey != encodedKey)
            {
                return;
            }

            int shelfExtentSize = TerminalIdentityRootLayout.ReadShelfExtentSize(rootBytes);
            int terminalRows = 0;
            long terminalShelfOffset = TerminalIdentityRootLayout.ReadFirstShelfOffset(rootBytes);
            while (terminalShelfOffset != 0 && visitedShelves.Add(terminalShelfOffset))
            {
                byte[] terminalShelfBytes = ReadTerminalVarIdentityShelfBytes(terminalShelfOffset, shelfExtentSize);
                int itemCount = TerminalVarIdentityShelfLayout.ReadItemCount(terminalShelfBytes);
                terminalRows += itemCount;
                for (int i = 0; i < itemCount && printedRowCount < maxRows; i++)
                {
                    builder.Append("  terminal row exactPath=").Append(exactPath)
                        .Append("; path=").Append(path)
                        .Append("; terminalRoot=").Append(targetOffset)
                        .Append("; shelf=").Append(terminalShelfOffset)
                        .Append("; identity=").Append(Encoding.UTF8.GetString(TerminalVarIdentityShelfLayout.ReadIdentityAt(terminalShelfBytes, i)))
                        .AppendLine();
                    printedRowCount++;
                }

                terminalShelfOffset = TerminalVarIdentityShelfLayout.ReadNextShelfOffset(terminalShelfBytes);
            }

            if (terminalRows != 0)
            {
                foundShelfCount++;
                foundRowCount += terminalRows;
                builder.Append("  terminal exactPath=").Append(exactPath)
                    .Append("; rows=").Append(terminalRows)
                    .Append("; path=").Append(path)
                    .Append("; terminalRoot=").Append(targetOffset)
                    .Append("; firstShelf=").Append(TerminalIdentityRootLayout.ReadFirstShelfOffset(rootBytes))
                    .Append("; tailShelf=").Append(TerminalIdentityRootLayout.ReadTailShelfOffset(rootBytes))
                    .AppendLine();
            }

            return;
        }

        if (kind == Scalar8VarIdentityRouteTargetKind.Shelf)
        {
            long currentShelfOffset = targetOffset;
            while (currentShelfOffset != 0 && visitedShelves.Add(currentShelfOffset))
            {
                byte[] shelfBytes = ReadScalar8VarIdentityShelfBytes(currentShelfOffset, maxIdentityLength, out Scalar8VarIdentityProfile profile);
                Scalar8VarIdentityReadOnly shelf = new(shelfBytes, profile);
                int shelfRows = 0;
                for (int i = 0; i < shelf.ItemCount; i++)
                {
                    if (shelf.ReadKeyAt(i) != encodedKey)
                    {
                        continue;
                    }

                    shelfRows++;
                    if (printedRowCount < maxRows)
                    {
                        builder.Append("  shelf row exactPath=").Append(exactPath)
                            .Append("; path=").Append(path)
                            .Append("; shelf=").Append(currentShelfOffset)
                            .Append("; slot=").Append(i)
                            .Append("; identity=").Append(Encoding.UTF8.GetString(shelf.ReadIdentityAt(i)))
                            .AppendLine();
                        printedRowCount++;
                    }
                }

                if (shelfRows != 0)
                {
                    foundShelfCount++;
                    foundRowCount += shelfRows;
                    builder.Append("  shelf exactPath=").Append(exactPath)
                        .Append("; rows=").Append(shelfRows)
                        .Append("; path=").Append(path)
                        .Append("; shelf=").Append(currentShelfOffset)
                        .Append("; next=").Append(Scalar8VarIdentityLayout.ReadNextShelfOffset(shelfBytes))
                        .AppendLine();
                }

                currentShelfOffset = Scalar8VarIdentityLayout.ReadNextShelfOffset(shelfBytes);
            }

            return;
        }

        if (kind != Scalar8VarIdentityRouteTargetKind.Router)
        {
            return;
        }

        if (remainingRouterHops <= 0 || !visitedRouters.Add(targetOffset))
        {
            return;
        }

        Span<byte> routerBytes = stackalloc byte[RouterLayout.Size];
        ReadRouterPageUsingArenaCache(targetOffset, routerBytes);
        RouterReader router = new(routerBytes);
        if (!router.IsValid)
        {
            builder.Append("  invalid router path=").Append(path).Append("; offset=").Append(targetOffset).AppendLine();
            return;
        }

        byte exactPrefix = GetScalar8VarIdentityPrefix(encodedKey, router.KeyDepth);
        for (int routeIndex = 0; routeIndex < router.RouteCount; routeIndex++)
        {
            long routeTargetOffset = router.GetRouteTargetAt(routeIndex);
            if (routeTargetOffset == 0 || Scalar8VarIdentityRouterTargetAppearedEarlier(router, routeTargetOffset, routeIndex))
            {
                continue;
            }

            string routeRanges = DescribeScalar8VarIdentityRouterTargetRanges(routerBytes, router, routeTargetOffset, exactPrefix, out bool edgeMatches);
            DescribeScalar8VarIdentityExactKeyRoutesFromTarget(
                routeTargetOffset,
                maxIdentityLength,
                encodedKey,
                visitedShelves,
                visitedRouters,
                remainingRouterHops - 1,
                path + " -> d" + router.KeyDepth.ToString(CultureInfo.InvariantCulture) + ":" + routeRanges,
                exactPath && edgeMatches,
                maxRows,
                ref foundShelfCount,
                ref foundRowCount,
                ref printedRowCount,
                builder);
        }
    }

    /// <summary>
    /// Determines whether a router target offset was already represented by an earlier route entry in the same expanded router.<br/>
    /// Diagnostic descendant scans group aliases by target so route reports describe one physical child once, with all prefix ranges that point to it.<br/>
    /// </summary>
    /// <param name="router">The router being scanned.<br/></param>
    /// <param name="targetOffset">The route target offset to test.<br/></param>
    /// <param name="currentRouteIndex">The route index currently being considered.<br/></param>
    /// <returns><see langword="true"/> when an earlier route entry already used the same target offset.</returns>
    private static bool Scalar8VarIdentityRouterTargetAppearedEarlier(RouterReader router, long targetOffset, int currentRouteIndex)
    {
        for (int i = 0; i < currentRouteIndex; i++)
        {
            if (router.GetRouteTargetAt(i) == targetOffset)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Formats all prefix ranges in one router that point to the same target offset.<br/>
    /// The method also reports whether the exact scalar-key prefix for this router depth is among those aliases, which is the key fact strict route-pruning validation needs.<br/>
    /// </summary>
    /// <param name="routerBytes">The raw router page bytes.<br/></param>
    /// <param name="router">The parsed router reader.<br/></param>
    /// <param name="targetOffset">The route target offset being described.<br/></param>
    /// <param name="exactPrefix">The scalar-key prefix byte for this router depth.<br/></param>
    /// <param name="containsExactPrefix">Receives whether this target is reachable through the exact prefix byte.<br/></param>
    /// <returns>A compact route range description.</returns>
    private static string DescribeScalar8VarIdentityRouterTargetRanges(
        ReadOnlySpan<byte> routerBytes,
        RouterReader router,
        long targetOffset,
        byte exactPrefix,
        out bool containsExactPrefix)
    {
        StringBuilder builder = new();
        containsExactPrefix = false;
        int appended = 0;
        for (int routeIndex = 0; routeIndex < router.RouteCount; routeIndex++)
        {
            if (router.GetRouteTargetAt(routeIndex) != targetOffset)
            {
                continue;
            }

            ReadOnlySpan<byte> route = RouterLayout.GetRoute(routerBytes, routeIndex);
            byte routeStart = RouterLayout.ReadRoutePrefixStart(route);
            byte routeEnd = RouterLayout.ReadRoutePrefixEnd(route);
            containsExactPrefix |= exactPrefix >= routeStart && exactPrefix <= routeEnd;
            if (appended < 8)
            {
                if (builder.Length != 0)
                {
                    builder.Append(',');
                }

                builder.Append(routeStart).Append('-').Append(routeEnd);
            }

            appended++;
        }

        if (appended > 8)
        {
            builder.Append(",...");
        }

        builder.Append("; exactPrefix=").Append(exactPrefix).Append("; exactEdge=").Append(containsExactPrefix);
        return builder.ToString();
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

        Span<byte> header = stackalloc byte[Scalar8VarIdentityLayout.HeaderSize];
        kernel.Read(shelfOffset, header);
        if (Scalar8VarIdentityLayout.ReadMagic(header) != Scalar8VarIdentityLayout.Magic ||
            Scalar8VarIdentityLayout.ReadFormatVersion(header) != Scalar8VarIdentityLayout.FormatVersion ||
            Scalar8VarIdentityLayout.ReadHeaderSize(header) != Scalar8VarIdentityLayout.HeaderSize)
        {
            throw new InvalidDataException("The routed SV8 shelf header is invalid.");
        }

        int loadedShelfExtentSize = Scalar8VarIdentityLayout.ReadShelfExtentSize(header);
        Scalar8VarIdentityProfile profile = Scalar8VarIdentityProfile.Create(loadedShelfExtentSize, maxIdentityLength, (Scalar8VarIdentityLayout.ReadFlags(header) & 1U) != 0);
        byte[] shelfBytes = ArrayPool<byte>.Shared.Rent(loadedShelfExtentSize);
        header.CopyTo(shelfBytes.AsSpan(0, header.Length));
        kernel.Read(shelfOffset, shelfBytes.AsSpan(0, loadedShelfExtentSize));
        if (!Scalar8VarIdentityMutableShelfView.TryCreatePooled(shelfBytes, profile, pooledBytes: true, out Scalar8VarIdentityMutableShelfView shelf))
        {
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(shelfBytes.AsSpan(0, sizeof(uint)));
            int shelfExtentSize = shelfBytes.Length >= Scalar8VarIdentityLayout.ShelfExtentSizeOffset + sizeof(int)
                ? Scalar8VarIdentityLayout.ReadShelfExtentSize(shelfBytes)
                : 0;
            int itemCount = shelfBytes.Length >= Scalar8VarIdentityLayout.ItemCountOffset + sizeof(int)
                ? Scalar8VarIdentityLayout.ReadItemCount(shelfBytes)
                : 0;
            int slotStreamLength = shelfBytes.Length >= Scalar8VarIdentityLayout.SlotStreamLengthOffset + sizeof(int)
                ? Scalar8VarIdentityLayout.ReadSlotStreamLength(shelfBytes)
                : 0;
            int slotCapacityBytes = shelfBytes.Length >= Scalar8VarIdentityLayout.SlotCapacityBytesOffset + sizeof(int)
                ? Scalar8VarIdentityLayout.ReadSlotCapacityBytes(shelfBytes)
                : 0;
            int recordArenaEnd = shelfBytes.Length >= Scalar8VarIdentityLayout.RecordArenaEndOffset + sizeof(int)
                ? Scalar8VarIdentityLayout.ReadRecordArenaEnd(shelfBytes)
                : 0;
            ArrayPool<byte>.Shared.Return(shelfBytes, clearArray: true);
            throw new InvalidDataException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The routed SV8 mutable shelf bytes are invalid. offset={shelfOffset} magic=0x{magic:x8} profileShelf={profile.ShelfExtentSize} headerShelf={shelfExtentSize} items={itemCount} slotStream={slotStreamLength} slotCapacity={slotCapacityBytes} recordArenaEnd={recordArenaEnd} bytes={shelfBytes.Length}."));
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
                if (!mutableShelf.IsDirty)
                {
                    mutableShelf.MarkDirty();
                }
            }
            else
            {
                if (!Scalar8VarIdentityMutableShelfView.TryCreatePooled(rewrittenBytes, mutableShelf.Profile, pooledBytes: false, out Scalar8VarIdentityMutableShelfView replacement))
                {
                    throw new InvalidDataException("The replacement SV8 mutable shelf bytes are invalid.");
                }

                replacement.MarkDirty();
                mutableShelf.Release(clearShelfBytes: true);
                scalar8VarIdentityMutableBatchShelves[shelfOffset] = replacement;
            }

            return default;
        }

        if (ReferenceEquals(rewrittenBytes, mutableShelf.Bytes))
        {
            mutableShelf.EnsureSlotBytesCurrent();
            rewrittenBytes = mutableShelf.Bytes;
        }

        RawDataReservation shelfRewrite = kernel.ReserveAt(shelfOffset, mutableShelf.Profile.ShelfExtentSize);
        rewrittenBytes.AsSpan(0, mutableShelf.Profile.ShelfExtentSize).CopyTo(shelfRewrite.Span);
        return CommitAndInvalidateRouterReadCache();
    }

    /// <summary>
    /// Stages all dirty `SV8` mutable batch shelf images before a structural rewrite changes route ownership or linked-shelf topology.<br/>
    /// Structural mutations often remove or obsolete mutable shelf cache entries, so long-lived group batches must publish dirty in-place inserts to the DataKernel pending overlay first.<br/>
    /// </summary>
    private void FlushScalar8VarIdentityMutableBatchShelvesBeforeStructuralMutation()
    {
        if (!durabilityBatchActive)
        {
            return;
        }

        if (scalar8VarIdentityMutableBatchShelves.Count != 0)
        {
            FlushDirtyScalar8VarIdentityMutableBatchShelvesForStructuralMutation();
        }

        foreach (Scalar8VarIdentityMutableShelfView mutableShelf in scalar8VarIdentityMutableBatchShelves.Values)
        {
            mutableShelf.Release(clearShelfBytes: true);
        }

        scalar8VarIdentityMutableBatchShelves.Clear();
    }

    /// <summary>
    /// Publishes dirty `SV8` batch shelves before an in-batch structural mutation by using fixed-offset reservations immediately visible to subsequent route walks.<br/>
    /// The normal durability-batch flush path uses staged writes at the commit boundary, but split/grow/terminal transformations may need to reread the same shelf inside the still-open batch.<br/>
    /// </summary>
    private void FlushDirtyScalar8VarIdentityMutableBatchShelvesForStructuralMutation()
    {
        foreach (KeyValuePair<long, Scalar8VarIdentityMutableShelfView> mutableShelf in scalar8VarIdentityMutableBatchShelves)
        {
            if (!mutableShelf.Value.IsDirty)
            {
                continue;
            }

            int shelfExtentSize = mutableShelf.Value.Profile.ShelfExtentSize;
            _ = mutableShelf.Value.NormalizeDeletedSlotsForPublication();
            mutableShelf.Value.EnsureSlotBytesCurrent();
            RawDataReservation rewrite = kernel.ReserveAt(mutableShelf.Key, shelfExtentSize);
            mutableShelf.Value.Bytes.AsSpan(0, shelfExtentSize).CopyTo(rewrite.Span);
        }
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
            if (!inserted && CompareScalar8VarIdentityTuple(currentKey, currentIdentity, incomingKey, incomingIdentity, profile.Descending) > 0)
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
        int firstSplitDepth = FindFirstDifferingScalar8VarIdentityDepth(keys, 0);
        if (firstSplitDepth >= 0 && firstSplitDepth < currentRouterDepth)
        {
            throw new InvalidDataException($"The SV8 transform source violates its routed prefix stem. FirstDifferentDepth={firstSplitDepth}; FirstOwnedDepth={currentRouterDepth + 1}; Count={keys.Length}.");
        }
        if (firstSplitDepth == currentRouterDepth)
        {
            return false;
        }
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
            if ((profile.Descending
                    ? Scalar8VarIdentityLayout.CompareIdentityBytes(identity, lastIdentity) >= 0
                    : Scalar8VarIdentityLayout.CompareIdentityBytes(identity, lastIdentity) <= 0) || nextOffset == 0)
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
                        (profile.Descending
                            ? Scalar8VarIdentityLayout.CompareIdentityBytes(identity, lastIdentity) < 0
                            : Scalar8VarIdentityLayout.CompareIdentityBytes(identity, lastIdentity) > 0) &&
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
                        profile.ShelfExtentSize,
                        DiagnosticPath: Scalar8VarIdentityInsertDiagnosticPath.DuplicateRunChainInsert);
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
    /// Inserts one tuple into an existing linked mixed-key `SV8` overflow chain by mutating only the target shelf when possible.<br/>
    /// This is the byte-native chain path used before router-level split or full-chain rewrite fallbacks: it walks sorted shelves, inserts into the one shelf whose tuple range owns the incoming value, and splits only that shelf when it is full.<br/>
    /// Keeping the operation local avoids materializing every variable identity in the chain as managed `byte[]` instances during duplicate-heavy or non-monotonic path-identity loads.<br/>
    /// </summary>
    /// <param name="headShelfOffset">The route-owned head shelf offset for the linked overflow chain.</param>
    /// <param name="headMutableShelf">The already-decoded head mutable shelf.</param>
    /// <param name="profile">The `SV8` profile shared by every shelf in the chain.</param>
    /// <param name="encodedKey">The encoded scalar key to insert.</param>
    /// <param name="identity">The raw variable identity bytes to insert.</param>
    /// <param name="allowDuplicateKeys">Whether duplicate scalar keys with different identities are allowed.</param>
    /// <param name="routerDepth">The router depth reported in the routed insert result.</param>
    /// <param name="result">Receives the routed insert result when the local chain path handles the tuple.</param>
    /// <returns><see langword="true"/> when the insert, no-op, key conflict, or local split was completed by this path.</returns>
    private bool TryInsertIntoScalar8VarIdentityOverflowChainLocal(
        long headShelfOffset,
        Scalar8VarIdentityMutableShelfView headMutableShelf,
        Scalar8VarIdentityProfile profile,
        ulong encodedKey,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys,
        ushort routerDepth,
        out Scalar8VarIdentityRoutedInsertResult result)
    {
        result = default;
        using RouteVisitedOffsetSet visited = RouteVisitedOffsetSet.Rent();
        long currentOffset = headShelfOffset;
        Scalar8VarIdentityMutableShelfView currentShelf = headMutableShelf;
        while (currentOffset != 0)
        {
            if (!visited.Add(currentOffset))
            {
                throw new InvalidDataException("The SV8 local overflow shelf chain contains a cycle.");
            }

            if (currentShelf.Profile.ShelfExtentSize != profile.ShelfExtentSize)
            {
                throw new InvalidDataException("The SV8 local overflow shelf chain changed shelf extent sizes.");
            }

            long nextShelfOffset = Scalar8VarIdentityLayout.ReadNextShelfOffset(currentShelf.Bytes);
            bool ownsIncomingTuple = currentShelf.ItemCount == 0 || nextShelfOffset == 0;
            if (!ownsIncomingTuple)
            {
                int lastIndex = currentShelf.ItemCount - 1;
                ownsIncomingTuple = CompareScalar8VarIdentityTuple(encodedKey, identity, currentShelf.ReadKeyAt(lastIndex), currentShelf.ReadIdentityAt(lastIndex), profile.Descending) <= 0;
            }

            if (ownsIncomingTuple)
            {
                Scalar8VarIdentityInsertResult insertResult = currentShelf.Insert(encodedKey, identity, allowDuplicateKeys);
                if (insertResult == Scalar8VarIdentityInsertResult.Inserted)
                {
                    DataKernelCommitTelemetry telemetry = StageScalar8VarIdentityShelfRewrite(currentOffset, currentShelf, currentShelf.Bytes);
                    result = new Scalar8VarIdentityRoutedInsertResult(
                        Scalar8VarIdentityRoutedInsertKind.WalkedDuplicateRunOverflow,
                        insertResult,
                        currentOffset,
                        currentOffset,
                        telemetry,
                        currentShelf.ItemCount,
                        profile.ShelfExtentSize,
                        routerDepth,
                        DiagnosticPath: Scalar8VarIdentityInsertDiagnosticPath.OverflowChainLocal);
                    return true;
                }

                if (insertResult == Scalar8VarIdentityInsertResult.AlreadyPresent ||
                    insertResult == Scalar8VarIdentityInsertResult.KeyConflict)
                {
                    result = new Scalar8VarIdentityRoutedInsertResult(
                        insertResult == Scalar8VarIdentityInsertResult.KeyConflict ? Scalar8VarIdentityRoutedInsertKind.KeyConflict : Scalar8VarIdentityRoutedInsertKind.NoOp,
                        insertResult,
                        currentOffset,
                        0,
                        default,
                        currentShelf.ItemCount,
                        profile.ShelfExtentSize,
                        routerDepth);
                    return true;
                }

                if (insertResult != Scalar8VarIdentityInsertResult.Full)
                {
                    result = new Scalar8VarIdentityRoutedInsertResult(
                        Scalar8VarIdentityRoutedInsertKind.Invalid,
                        insertResult,
                        currentOffset,
                        0,
                        default,
                        currentShelf.ItemCount,
                        profile.ShelfExtentSize,
                        routerDepth);
                    return true;
                }

                return TrySplitScalar8VarIdentityOverflowChainShelfLocal(
                    headShelfOffset,
                    currentOffset,
                    nextShelfOffset,
                    currentShelf,
                    profile,
                    encodedKey,
                    identity,
                    allowDuplicateKeys,
                    routerDepth,
                    out result);
            }

            currentOffset = nextShelfOffset;
            if (currentOffset != 0)
            {
                currentShelf = ReadScalar8VarIdentityMutableShelf(currentOffset, profile.MaxIdentityLength);
            }
        }

        return false;
    }

    /// <summary>
    /// Splits one full shelf inside a linked mixed-key `SV8` overflow chain while preserving the existing chain head route.<br/>
    /// The source shelf plus incoming tuple are copied directly from mutable shelf spans into two replacement shelf images; only the source shelf, one new sibling, and the head tail pointer when needed are rewritten.<br/>
    /// This bounds allocation and staged bytes to the touched page instead of rebuilding every downstream shelf in the chain.<br/>
    /// </summary>
    /// <param name="headShelfOffset">The route-owned chain head shelf offset.</param>
    /// <param name="sourceShelfOffset">The full shelf being split.</param>
    /// <param name="sourceNextShelfOffset">The current next-shelf pointer from the source shelf.</param>
    /// <param name="sourceShelf">The mutable source shelf containing sorted sidecars.</param>
    /// <param name="profile">The `SV8` profile shared by the chain.</param>
    /// <param name="encodedKey">The encoded scalar key to insert.</param>
    /// <param name="identity">The raw variable identity bytes to insert.</param>
    /// <param name="allowDuplicateKeys">Whether duplicate scalar keys with different identities are allowed.</param>
    /// <param name="routerDepth">The router depth reported in the routed insert result.</param>
    /// <param name="result">Receives the routed insert result when the local split succeeds.</param>
    /// <returns><see langword="true"/> when the local split published replacement shelves or terminated as a no-op/conflict.</returns>
    private bool TrySplitScalar8VarIdentityOverflowChainShelfLocal(
        long headShelfOffset,
        long sourceShelfOffset,
        long sourceNextShelfOffset,
        Scalar8VarIdentityMutableShelfView sourceShelf,
        Scalar8VarIdentityProfile profile,
        ulong encodedKey,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys,
        ushort routerDepth,
        out Scalar8VarIdentityRoutedInsertResult result)
    {
        result = default;
        if (!TryBuildScalar8VarIdentityLocalChainSplit(
            sourceShelf,
            profile,
            encodedKey,
            identity,
            allowDuplicateKeys,
            out byte[] leftShelf,
            out byte[] rightShelf,
            out int leftCount,
            out int rightCount,
            out Scalar8VarIdentityInsertResult splitResult))
        {
            if (splitResult == Scalar8VarIdentityInsertResult.AlreadyPresent ||
                splitResult == Scalar8VarIdentityInsertResult.KeyConflict)
            {
                result = new Scalar8VarIdentityRoutedInsertResult(
                    splitResult == Scalar8VarIdentityInsertResult.KeyConflict ? Scalar8VarIdentityRoutedInsertKind.KeyConflict : Scalar8VarIdentityRoutedInsertKind.NoOp,
                    splitResult,
                    sourceShelfOffset,
                    0,
                    default,
                    sourceShelf.ItemCount,
                    profile.ShelfExtentSize,
                    routerDepth);
                return true;
            }

            return false;
        }

        FlushScalar8VarIdentityMutableBatchShelvesBeforeStructuralMutation();
        scalar8VarIdentityMutableBatchShelves.Remove(sourceShelfOffset);
        Scalar8VarIdentityLayout.WriteNextShelfOffset(rightShelf, sourceNextShelfOffset);
        RawDataReservation rightReservation = kernel.Reserve(profile.ShelfExtentSize);
        rightShelf.CopyTo(rightReservation.Span);
        Scalar8VarIdentityLayout.WriteNextShelfOffset(leftShelf, rightReservation.Extent.Offset);
        if (sourceShelfOffset == headShelfOffset)
        {
            long oldTailOffset = Scalar8VarIdentityLayout.ReadTailShelfOffset(sourceShelf.Bytes);
            long newTailOffset = sourceNextShelfOffset == 0
                ? rightReservation.Extent.Offset
                : oldTailOffset;
            Scalar8VarIdentityLayout.WriteTailShelfOffset(leftShelf, newTailOffset == headShelfOffset ? 0 : newTailOffset);
        }

        RawDataReservation sourceRewrite = kernel.ReserveAt(sourceShelfOffset, profile.ShelfExtentSize);
        leftShelf.CopyTo(sourceRewrite.Span);
        if (sourceShelfOffset != headShelfOffset && sourceNextShelfOffset == 0)
        {
            byte[] headBytes = ReadScalar8VarIdentityShelfBytes(headShelfOffset, profile.MaxIdentityLength, out Scalar8VarIdentityProfile headProfile);
            if (headProfile.ShelfExtentSize != profile.ShelfExtentSize)
            {
                throw new InvalidDataException("The SV8 local split head changed shelf extent sizes.");
            }

            Scalar8VarIdentityLayout.WriteTailShelfOffset(headBytes, rightReservation.Extent.Offset);
            RawDataReservation headRewrite = kernel.ReserveAt(headShelfOffset, profile.ShelfExtentSize);
            headBytes.CopyTo(headRewrite.Span);
            scalar8VarIdentityMutableBatchShelves.Remove(headShelfOffset);
        }

        DataKernelCommitTelemetry telemetry = CommitWithoutInvalidatingRouterReadCache();
        result = new Scalar8VarIdentityRoutedInsertResult(
            Scalar8VarIdentityRoutedInsertKind.WalkedDuplicateRunOverflow,
            splitResult,
            sourceShelfOffset,
            rightReservation.Extent.Offset,
            telemetry,
            Math.Max(leftCount, rightCount),
            profile.ShelfExtentSize,
            routerDepth,
            2);
        return true;
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
            result = default;
            return false;
        }

        if (tailMutableShelf.ReadKeyAt(0) != encodedKey || tailMutableShelf.ReadKeyAt(tailMutableShelf.ItemCount - 1) != encodedKey)
        {
            result = default;
            return false;
        }

        if (profile.Descending
            ? Scalar8VarIdentityLayout.CompareIdentityBytes(identity, tailMutableShelf.ReadIdentityAt(tailMutableShelf.ItemCount - 1)) >= 0
            : Scalar8VarIdentityLayout.CompareIdentityBytes(identity, tailMutableShelf.ReadIdentityAt(tailMutableShelf.ItemCount - 1)) <= 0)
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
            FlushScalar8VarIdentityMutableBatchShelvesBeforeStructuralMutation();
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
    /// Attempts a duplicate-run or mixed-overflow tail insert after reading only the route-owned head shelf header.<br/>
    /// This is the hot path for sorted duplicate-key loads where the head shelf only supplies `next` and `tail` offsets; the actual mutation happens in the terminal shelf.<br/>
    /// Returning <see langword="false"/> preserves correctness by sending non-append or ambiguous cases through the normal full-head-shelf path.<br/>
    /// </summary>
    /// <param name="headShelfOffset">The route-owned head shelf offset.<br/></param>
    /// <param name="maxIdentityLength">The maximum identity byte length accepted by the index.<br/></param>
    /// <param name="encodedKey">The encoded scalar key being inserted.<br/></param>
    /// <param name="identity">The raw identity bytes being inserted.<br/></param>
    /// <param name="allowDuplicateKeys">Whether duplicate scalar keys are allowed.<br/></param>
    /// <param name="result">Receives the routed insert result when a tail append/no-op/conflict was proven.<br/></param>
    /// <returns><see langword="true"/> when the header-only tail path handled the insert; otherwise <see langword="false"/>.</returns>
    private bool TryInsertIntoScalar8VarIdentityTailFromHeadHeader(
        long headShelfOffset,
        int maxIdentityLength,
        ulong encodedKey,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys,
        out Scalar8VarIdentityRoutedInsertResult result)
    {
        result = default;
        Span<byte> header = stackalloc byte[Scalar8VarIdentityLayout.HeaderSize];
        kernel.Read(headShelfOffset, header);
        if (Scalar8VarIdentityLayout.ReadMagic(header) != Scalar8VarIdentityLayout.Magic ||
            Scalar8VarIdentityLayout.ReadFormatVersion(header) != Scalar8VarIdentityLayout.FormatVersion ||
            Scalar8VarIdentityLayout.ReadHeaderSize(header) != Scalar8VarIdentityLayout.HeaderSize)
        {
            return false;
        }

        long nextShelfOffset = Scalar8VarIdentityLayout.ReadNextShelfOffset(header);
        long tailShelfOffset = Scalar8VarIdentityLayout.ReadTailShelfOffset(header);
        if (nextShelfOffset == 0 ||
            tailShelfOffset == 0 ||
            tailShelfOffset == headShelfOffset)
        {
            return false;
        }

        Scalar8VarIdentityProfile profile = Scalar8VarIdentityProfile.Create(
            Scalar8VarIdentityLayout.ReadShelfExtentSize(header),
            maxIdentityLength,
            (Scalar8VarIdentityLayout.ReadFlags(header) & 1U) != 0);
        bool handled = TryInsertIntoScalar8VarIdentityDuplicateRunTailFast(
                headShelfOffset,
                tailShelfOffset,
                profile,
                encodedKey,
                identity,
                out result) ||
            TryInsertIntoScalar8VarIdentityOverflowTailFast(
                headShelfOffset,
                tailShelfOffset,
                profile,
                encodedKey,
                identity,
                allowDuplicateKeys,
                out result);
        if (handled)
        {
            result = result with { DiagnosticPath = Scalar8VarIdentityInsertDiagnosticPath.HeaderTailFast };
        }

        return handled;
    }

    /// <summary>
    /// Attempts a sorted append into the known terminal shelf of a mixed-key `SV8` overflow chain.<br/>
    /// The head shelf's tail pointer lets index-major loads avoid walking or rebuilding prior chain segments after the first overflow shelf is created.<br/>
    /// If the incoming tuple does not sort after the terminal tuple, the caller falls back to the full overflow-chain rewrite path.<br/>
    /// </summary>
    /// <param name="headShelfOffset">The route-owned chain head shelf offset.</param>
    /// <param name="headShelfBytes">The current head shelf bytes containing the tail pointer.</param>
    /// <param name="profile">The SV8 profile used by the chain shelves.</param>
    /// <param name="encodedKey">The encoded scalar key being inserted.</param>
    /// <param name="identity">The raw identity being inserted.</param>
    /// <param name="allowDuplicateKeys">Whether duplicate scalar keys are allowed.</param>
    /// <param name="result">Receives the routed insert result when the tail fast path handles the insert.</param>
    /// <returns>`true` when the tail fast path produced a terminal result.</returns>
    private bool TryInsertIntoScalar8VarIdentityOverflowTailFast(
        long headShelfOffset,
        byte[] headShelfBytes,
        Scalar8VarIdentityProfile profile,
        ulong encodedKey,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys,
        out Scalar8VarIdentityRoutedInsertResult result)
    {
        result = default;
        long tailShelfOffset = Scalar8VarIdentityLayout.ReadTailShelfOffset(headShelfBytes);
        if (tailShelfOffset == 0 || tailShelfOffset == headShelfOffset)
        {
            return false;
        }

        return TryInsertIntoScalar8VarIdentityOverflowTailFast(
            headShelfOffset,
            tailShelfOffset,
            profile,
            encodedKey,
            identity,
            allowDuplicateKeys,
            out result);
    }

    /// <summary>
    /// Attempts a sorted append into a known terminal shelf of a mixed-key `SV8` overflow chain.<br/>
    /// This overload is used by the header-only head fast path after it has already read the tail pointer without materializing the full head shelf.<br/>
    /// </summary>
    /// <param name="headShelfOffset">The route-owned chain head shelf offset.</param>
    /// <param name="tailShelfOffset">The known terminal shelf offset.</param>
    /// <param name="profile">The SV8 profile used by the chain shelves.</param>
    /// <param name="encodedKey">The encoded scalar key being inserted.</param>
    /// <param name="identity">The raw identity being inserted.</param>
    /// <param name="allowDuplicateKeys">Whether duplicate scalar keys are allowed.</param>
    /// <param name="result">Receives the routed insert result when the tail fast path handles the insert.</param>
    /// <returns>`true` when the tail fast path produced a terminal result.</returns>
    private bool TryInsertIntoScalar8VarIdentityOverflowTailFast(
        long headShelfOffset,
        long tailShelfOffset,
        Scalar8VarIdentityProfile profile,
        ulong encodedKey,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys,
        out Scalar8VarIdentityRoutedInsertResult result)
    {
        result = default;

        Scalar8VarIdentityMutableShelfView tailMutableShelf = ReadScalar8VarIdentityMutableShelf(tailShelfOffset, profile.MaxIdentityLength);
        if (tailMutableShelf.Profile.ShelfExtentSize != profile.ShelfExtentSize)
        {
            throw new InvalidDataException("The SV8 overflow tail changed shelf extent sizes.");
        }

        if (tailMutableShelf.ItemCount == 0)
        {
            result = default;
            return false;
        }

        ulong lastKey = tailMutableShelf.ReadKeyAt(tailMutableShelf.ItemCount - 1);
        ReadOnlySpan<byte> lastIdentity = tailMutableShelf.ReadIdentityAt(tailMutableShelf.ItemCount - 1);
        int order = CompareScalar8VarIdentityTuple(encodedKey, identity, lastKey, lastIdentity, profile.Descending);
        if (order < 0)
        {
            return false;
        }

        if (order == 0)
        {
            result = new Scalar8VarIdentityRoutedInsertResult(
                Scalar8VarIdentityRoutedInsertKind.NoOp,
                Scalar8VarIdentityInsertResult.AlreadyPresent,
                tailShelfOffset,
                0,
                default,
                tailMutableShelf.ItemCount,
                profile.ShelfExtentSize);
            return true;
        }

        if (!allowDuplicateKeys && encodedKey == lastKey)
        {
            result = new Scalar8VarIdentityRoutedInsertResult(
                Scalar8VarIdentityRoutedInsertKind.KeyConflict,
                Scalar8VarIdentityInsertResult.KeyConflict,
                tailShelfOffset,
                0,
                default,
                tailMutableShelf.ItemCount,
                profile.ShelfExtentSize);
            return true;
        }

        Scalar8VarIdentityInsertResult insertResult = tailMutableShelf.Insert(encodedKey, identity, allowDuplicateKeys);
        if (insertResult == Scalar8VarIdentityInsertResult.Inserted)
        {
            DataKernelCommitTelemetry telemetry = StageScalar8VarIdentityShelfRewrite(tailShelfOffset, tailMutableShelf, tailMutableShelf.Bytes);
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

        if (insertResult != Scalar8VarIdentityInsertResult.Full)
        {
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

        tailMutableShelf.EnsureSlotBytesCurrent();
        FlushScalar8VarIdentityMutableBatchShelvesBeforeStructuralMutation();
        scalar8VarIdentityMutableBatchShelves.Remove(tailShelfOffset);
        return TryAppendScalar8VarIdentityOverflowTail(
            headShelfOffset,
            tailShelfOffset,
            tailMutableShelf.Bytes,
            profile,
            encodedKey,
            identity,
            allowDuplicateKeys,
            out result);
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

        if (profile.Descending
            ? Scalar8VarIdentityLayout.CompareIdentityBytes(identity, terminalShelf.ReadIdentityAt(terminalShelf.ItemCount - 1)) >= 0
            : Scalar8VarIdentityLayout.CompareIdentityBytes(identity, terminalShelf.ReadIdentityAt(terminalShelf.ItemCount - 1)) <= 0)
        {
            return false;
        }

        RawDataReservation tailReservation = kernel.Reserve(profile.ShelfExtentSize);
        Scalar8VarIdentityInsertResult tailInsert = BuildSingleScalar8VarIdentityShelf(
            tailReservation.Span,
            profile,
            encodedKey,
            identity,
            allowDuplicateKeys: true);
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

        FlushScalar8VarIdentityMutableBatchShelvesBeforeStructuralMutation();
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
            StageScalar8VarIdentityTailPointerRewrite(headShelfOffset, tailReservation.Extent.Offset);
        }

        DataKernelCommitTelemetry telemetry = CommitWithoutInvalidatingRouterReadCache();
        result = new Scalar8VarIdentityRoutedInsertResult(
            Scalar8VarIdentityRoutedInsertKind.WalkedDuplicateRunOverflow,
            Scalar8VarIdentityInsertResult.Inserted,
            terminalShelfOffset,
            tailReservation.Extent.Offset,
            telemetry,
            1,
            profile.ShelfExtentSize,
            DiagnosticPath: Scalar8VarIdentityInsertDiagnosticPath.DuplicateRunTailAppend);
        return true;
    }

    /// <summary>
    /// Appends a new terminal shelf to a sorted mixed-key `SV8` overflow chain when the incoming tuple sorts after the current terminal tuple.<br/>
    /// This is the hot path for index-major bulk loads after a route-owned shelf reaches its maximum extent: the chain grows by one packed tail shelf instead of rebuilding prior shelves.<br/>
    /// The method preserves duplicate-key semantics by checking exact duplicate tuple equality only against the terminal tuple range it can prove is ordered before the incoming value.<br/>
    /// </summary>
    /// <param name="headShelfOffset">The route-owned chain head shelf offset whose tail pointer should be maintained.</param>
    /// <param name="terminalShelfOffset">The current terminal shelf offset.</param>
    /// <param name="terminalShelfBytes">The current terminal shelf bytes.</param>
    /// <param name="profile">The SV8 profile used by both shelves.</param>
    /// <param name="encodedKey">The encoded scalar key being inserted.</param>
    /// <param name="identity">The raw identity being appended.</param>
    /// <param name="allowDuplicateKeys">Whether duplicate scalar keys are allowed.</param>
    /// <param name="result">Receives the routed insert result when the append succeeds or terminates as conflict/no-op.</param>
    /// <returns>`true` when the append-tail path produced a terminal result.</returns>
    private bool TryAppendScalar8VarIdentityOverflowTail(
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
        Scalar8VarIdentityReadOnly terminalShelf = new(terminalShelfBytes, profile);
        if (!terminalShelf.IsValid || terminalShelf.ItemCount == 0 || Scalar8VarIdentityLayout.ReadNextShelfOffset(terminalShelfBytes) != 0)
        {
            return false;
        }

        ulong lastKey = terminalShelf.ReadKeyAt(terminalShelf.ItemCount - 1);
        ReadOnlySpan<byte> lastIdentity = terminalShelf.ReadIdentityAt(terminalShelf.ItemCount - 1);
        int order = CompareScalar8VarIdentityTuple(encodedKey, identity, lastKey, lastIdentity, profile.Descending);
        if (order < 0)
        {
            return false;
        }

        if (order == 0)
        {
            result = new Scalar8VarIdentityRoutedInsertResult(
                Scalar8VarIdentityRoutedInsertKind.NoOp,
                Scalar8VarIdentityInsertResult.AlreadyPresent,
                terminalShelfOffset,
                0,
                default,
                terminalShelf.ItemCount,
                profile.ShelfExtentSize);
            return true;
        }

        if (!allowDuplicateKeys && encodedKey == lastKey)
        {
            result = new Scalar8VarIdentityRoutedInsertResult(
                Scalar8VarIdentityRoutedInsertKind.KeyConflict,
                Scalar8VarIdentityInsertResult.KeyConflict,
                terminalShelfOffset,
                0,
                default,
                terminalShelf.ItemCount,
                profile.ShelfExtentSize);
            return true;
        }

        RawDataReservation tailReservation = kernel.Reserve(profile.ShelfExtentSize);
        Scalar8VarIdentityInsertResult tailInsert = BuildSingleScalar8VarIdentityShelf(
            tailReservation.Span,
            profile,
            encodedKey,
            identity,
            allowDuplicateKeys);
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

        FlushScalar8VarIdentityMutableBatchShelvesBeforeStructuralMutation();
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
            StageScalar8VarIdentityTailPointerRewrite(headShelfOffset, tailReservation.Extent.Offset);
        }

        DataKernelCommitTelemetry telemetry = CommitWithoutInvalidatingRouterReadCache();
        result = new Scalar8VarIdentityRoutedInsertResult(
            Scalar8VarIdentityRoutedInsertKind.WalkedDuplicateRunOverflow,
            Scalar8VarIdentityInsertResult.Inserted,
            terminalShelfOffset,
            tailReservation.Extent.Offset,
            telemetry,
            1,
            profile.ShelfExtentSize,
            DiagnosticPath: Scalar8VarIdentityInsertDiagnosticPath.OverflowTailAppend);
        return true;
    }

    /// <summary>
    /// Stages only the eight-byte `SV8` duplicate-run tail pointer in a chain head shelf.<br/>
    /// Append-heavy duplicate-key chains update this pointer for every new tail shelf, so writing the header field directly avoids a full shelf read and a full shelf copy for each append.<br/>
    /// The next-shelf chain remains the correctness source; this pointer is an append acceleration hint that lets later inserts jump to the terminal shelf.<br/>
    /// </summary>
    /// <param name="headShelfOffset">The offset of the route-owned duplicate-run head shelf.<br/></param>
    /// <param name="tailShelfOffset">The offset of the current terminal duplicate-run shelf.<br/></param>
    private void StageScalar8VarIdentityTailPointerRewrite(long headShelfOffset, long tailShelfOffset)
    {
        RawDataReservation headTailRewrite = kernel.ReserveAt(
            checked(headShelfOffset + Scalar8VarIdentityLayout.TailShelfOffsetOffset),
            sizeof(long));
        BinaryPrimitives.WriteInt64LittleEndian(headTailRewrite.Span, tailShelfOffset);
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

                if (!incomingAdded && (profile.Descending
                    ? Scalar8VarIdentityLayout.CompareIdentityBytes(incomingIdentity, existingIdentity) > 0
                    : Scalar8VarIdentityLayout.CompareIdentityBytes(incomingIdentity, existingIdentity) < 0))
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
        FlushScalar8VarIdentityMutableBatchShelvesBeforeStructuralMutation();
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

    /// <summary>
    /// Splits an already-linked `SV8` overflow chain by the next available scalar-key route byte.<br/>
    /// Linked chains can no longer be mutated as only their head shelf, because doing so can leave later shelves reachable only through full scans.<br/>
    /// This helper materializes the chain into sorted tuple arrays, inserts the incoming tuple, builds two replacement linked chains, and publishes a child router in one structural update.<br/>
    /// </summary>
    /// <param name="parentRouterOffset">The parent router whose route currently points at <paramref name="headShelfOffset"/>.</param>
    /// <param name="headShelfOffset">The route-owned linked-chain head shelf offset.</param>
    /// <param name="allocationClassId">The router allocation class to preserve when publishing the split router.</param>
    /// <param name="currentRouterDepth">The key-byte depth represented by the current route target.</param>
    /// <param name="headShelfBytes">The current head shelf bytes.</param>
    /// <param name="profile">The `SV8` shelf profile used by every segment in the chain.</param>
    /// <param name="encodedKey">The encoded scalar key being inserted.</param>
    /// <param name="incomingIdentity">The raw identity being inserted.</param>
    /// <param name="allowDuplicateKeys">Whether duplicate scalar keys are allowed when identities differ.</param>
    /// <param name="result">Receives the routed insert result when the split succeeds or the tuple already exists.</param>
    /// <returns>`true` when the linked chain was handled without falling through to overflow rewrite.</returns>
    private bool TrySplitScalar8VarIdentityOverflowChain(
        long rootRouterOffset,
        long parentRouterOffset,
        long headShelfOffset,
        ushort allocationClassId,
        int currentRouterDepth,
        byte[] headShelfBytes,
        Scalar8VarIdentityProfile profile,
        ulong encodedKey,
        ReadOnlySpan<byte> incomingIdentity,
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
                profile.ShelfExtentSize,
                checked((ushort)currentRouterDepth));
            return true;
        }

        ulong[] keys = new ulong[256];
        Scalar8VarIdentityIdentityRef[] identities = new Scalar8VarIdentityIdentityRef[256];
        int tupleCount = 0;
        bool incomingAdded = false;
        Scalar8VarIdentityIdentityRef incomingIdentityRef = new(Array.Empty<byte>(), 0, true);
        using RouteVisitedOffsetSet visited = RouteVisitedOffsetSet.Rent();
        long currentOffset = headShelfOffset;
        byte[] currentBytes = headShelfBytes;
        while (currentOffset != 0)
        {
            if (!visited.Add(currentOffset))
            {
                throw new InvalidDataException("The SV8 split overflow shelf chain contains a cycle.");
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
                    profile.ShelfExtentSize,
                    checked((ushort)currentRouterDepth));
                return true;
            }

            for (int i = 0; i < shelf.ItemCount; i++)
            {
                ulong currentKey = shelf.ReadKeyAt(i);
                ReadOnlySpan<byte> currentIdentity = shelf.ReadIdentityAt(i);
                if (!incomingAdded && CompareScalar8VarIdentityTuple(encodedKey, incomingIdentity, currentKey, currentIdentity, profile.Descending) < 0)
                {
                    AddScalar8VarIdentityTuple(ref keys, ref identities, ref tupleCount, encodedKey, incomingIdentityRef);
                    incomingAdded = true;
                }

                if (currentKey == encodedKey &&
                    Scalar8VarIdentityLayout.IdentityBytesEqual(currentIdentity, incomingIdentity))
                {
                    result = new Scalar8VarIdentityRoutedInsertResult(
                        Scalar8VarIdentityRoutedInsertKind.NoOp,
                        Scalar8VarIdentityInsertResult.AlreadyPresent,
                        currentOffset,
                        0,
                        default,
                        shelf.ItemCount,
                        profile.ShelfExtentSize,
                        checked((ushort)currentRouterDepth));
                    return true;
                }

                AddScalar8VarIdentityTuple(ref keys, ref identities, ref tupleCount, currentKey, new Scalar8VarIdentityIdentityRef(currentBytes, checked((int)shelf.ReadRecordOffsetAt(i)), false));
            }

            currentOffset = Scalar8VarIdentityLayout.ReadNextShelfOffset(currentBytes);
            if (currentOffset != 0)
            {
                currentBytes = ReadScalar8VarIdentityShelfBytes(currentOffset, profile.MaxIdentityLength, out Scalar8VarIdentityProfile nextProfile);
                if (nextProfile.ShelfExtentSize != profile.ShelfExtentSize)
                {
                    throw new InvalidDataException("The SV8 split overflow shelf chain changed shelf extent sizes.");
                }
            }
        }

        if (!incomingAdded)
        {
            AddScalar8VarIdentityTuple(ref keys, ref identities, ref tupleCount, encodedKey, incomingIdentityRef);
        }

        int splitDepth = FindFirstDifferingScalar8VarIdentityDepth(keys.AsSpan(0, tupleCount), 0);
        if (splitDepth >= 0 && splitDepth < currentRouterDepth)
        {
            throw new InvalidDataException($"The SV8 overflow source violates its routed prefix stem. FirstDifferentDepth={splitDepth}; FirstOwnedDepth={currentRouterDepth + 1}; Count={tupleCount}.");
        }
        if (splitDepth < 0)
        {
            return false;
        }

        int boundary = FindBalancedScalar8VarIdentitySplitBoundary(keys.AsSpan(0, tupleCount), checked((ushort)splitDepth));
        if (boundary <= 0 || boundary >= tupleCount)
        {
            return false;
        }

        if (!TryCreateScalar8VarIdentityOverflowChain(keys, identities, incomingIdentity, 0, boundary, profile, out long leftHeadOffset, out _, out int leftLargestCount, out int leftShelfCount) ||
            !TryCreateScalar8VarIdentityOverflowChain(keys, identities, incomingIdentity, boundary, tupleCount - boundary, profile, out long rightHeadOffset, out _, out int rightLargestCount, out int rightShelfCount))
        {
            return false;
        }

        byte selectedRightPrefix = GetScalar8VarIdentityPrefix(keys[profile.Descending ? boundary - 1 : boundary], splitDepth);
        FlushScalar8VarIdentityMutableBatchShelvesBeforeStructuralMutation();
        scalar8VarIdentityMutableBatchShelves.Remove(headShelfOffset);
        long replacementOffset;
        if (splitDepth == currentRouterDepth)
        {
            RepointSameDepthScalar8VarIdentityRoutes(
                rootRouterOffset,
                headShelfOffset,
                selectedRightPrefix,
                leftHeadOffset,
                rightHeadOffset,
                checked((ushort)currentRouterDepth),
                profile.Descending);
            replacementOffset = leftHeadOffset;
        }
        else
        {
            replacementOffset = CreateScalar8VarIdentitySplitRouterChain(
                checked((ushort)(currentRouterDepth + 1)),
                checked((ushort)splitDepth),
                allocationClassId,
                keys[0],
                leftHeadOffset,
                rightHeadOffset,
                selectedRightPrefix,
                profile.Descending);
            RepointMatchingScalar8VarIdentityRoutes(rootRouterOffset, headShelfOffset, replacementOffset, checked((ushort)currentRouterDepth), keys[0]);
        }
        DataKernelCommitTelemetry telemetry = CommitAndInvalidateRouterReadCache();
        result = new Scalar8VarIdentityRoutedInsertResult(
            Scalar8VarIdentityRoutedInsertKind.WalkedShelfSplit,
            Scalar8VarIdentityInsertResult.Inserted,
            replacementOffset,
            rightHeadOffset,
            telemetry,
            Math.Max(leftLargestCount, rightLargestCount),
            profile.ShelfExtentSize,
            checked((ushort)currentRouterDepth),
            checked((ushort)(leftShelfCount + rightShelfCount)));
        return true;
    }

    /// <summary>
    /// Finds a split boundary that separates sorted `SV8` tuples by one route byte while keeping the two sides as balanced as possible.<br/>
    /// The caller has already selected a byte depth where the first and last keys differ, so a positive boundary should exist unless the candidate data changed.<br/>
    /// </summary>
    /// <param name="keys">The sorted key array to split.</param>
    /// <param name="splitDepth">The key-byte depth used for routing.</param>
    /// <returns>The first index belonging to the right side, or `-1` when no route-byte boundary exists.</returns>
    private static int FindBalancedScalar8VarIdentitySplitBoundary(ReadOnlySpan<ulong> keys, ushort splitDepth)
    {
        int bestBoundary = -1;
        int bestLargestSide = int.MaxValue;
        byte previousPrefix = GetScalar8VarIdentityPrefix(keys[0], splitDepth);
        for (int i = 1; i < keys.Length; i++)
        {
            byte prefix = GetScalar8VarIdentityPrefix(keys[i], splitDepth);
            if (prefix == previousPrefix || keys[i - 1] == keys[i])
            {
                continue;
            }

            int largestSide = Math.Max(i, keys.Length - i);
            if (largestSide < bestLargestSide)
            {
                bestLargestSide = largestSide;
                bestBoundary = i;
            }

            previousPrefix = prefix;
        }

        return bestBoundary;
    }

    /// <summary>
    /// Persists a sorted `SV8` tuple range as one linked overflow chain and returns its published head offset.<br/>
    /// Shelves are built from the back so each `NextShelfOffset` can be written before the shelf bytes are reserved, avoiding a second rewrite pass.<br/>
    /// </summary>
    /// <param name="keys">The sorted scalar keys.</param>
    /// <param name="identities">The sorted raw identities aligned with <paramref name="keys"/>.</param>
    /// <param name="start">The first tuple index to include.</param>
    /// <param name="count">The number of tuples to include.</param>
    /// <param name="profile">The `SV8` shelf profile used by each shelf.</param>
    /// <param name="headOffset">Receives the first shelf offset in the persisted chain.</param>
    /// <param name="tailOffset">Receives the final shelf offset in the persisted chain.</param>
    /// <param name="largestShelfItemCount">Receives the largest item count in any built shelf.</param>
    /// <param name="shelfCount">Receives the number of persisted shelves.</param>
    /// <returns>`true` when every tuple was packed into a persisted shelf chain.</returns>
    private bool TryCreateScalar8VarIdentityOverflowChain(
        ReadOnlySpan<ulong> keys,
        ReadOnlySpan<byte[]> identities,
        int start,
        int count,
        Scalar8VarIdentityProfile profile,
        out long headOffset,
        out long tailOffset,
        out int largestShelfItemCount,
        out int shelfCount)
    {
        headOffset = 0;
        tailOffset = 0;
        largestShelfItemCount = 0;
        shelfCount = 0;
        byte[][] shelves = new byte[4][];
        int[] counts = new int[4];
        int shelfWriteCount = 0;
        int cursor = start;
        int remaining = count;
        while (remaining > 0)
        {
            int chunkCount = GetScalar8VarIdentityOverflowChunkCount(keys, identities, cursor, remaining, profile);
            if (chunkCount == 0 ||
                !TryBuildScalar8VarIdentityOverflowShelf(keys, identities, cursor, chunkCount, profile, out byte[] shelf))
            {
                return false;
            }

            if (shelfWriteCount == shelves.Length)
            {
                Array.Resize(ref shelves, checked(shelves.Length * 2));
                Array.Resize(ref counts, checked(counts.Length * 2));
            }

            shelves[shelfWriteCount] = shelf;
            counts[shelfWriteCount] = chunkCount;
            shelfWriteCount++;
            cursor += chunkCount;
            remaining -= chunkCount;
        }

        long nextOffset = 0;
        for (int i = shelfWriteCount - 1; i >= 0; i--)
        {
            Scalar8VarIdentityLayout.WriteNextShelfOffset(shelves[i], nextOffset);
            Scalar8VarIdentityLayout.WriteTailShelfOffset(shelves[i], i == 0 && tailOffset != 0 ? tailOffset : 0);
            RawDataReservation reservation = kernel.Reserve(profile.ShelfExtentSize);
            shelves[i].CopyTo(reservation.Span);
            nextOffset = reservation.Extent.Offset;
            headOffset = reservation.Extent.Offset;
            if (tailOffset == 0)
            {
                tailOffset = reservation.Extent.Offset;
            }

            largestShelfItemCount = Math.Max(largestShelfItemCount, counts[i]);
            shelfCount++;
        }

        return headOffset != 0;
    }

    /// <summary>
    /// Rewrites an exhausted `SV8` route-owned chain as sorted linked shelves when scalar-key routing cannot split it further.<br/>
    /// This fallback is intentionally broader than duplicate-run overflow: it accepts mixed scalar keys and preserves full tuple ordering by key then identity.<br/>
    /// It is used only after ordinary insert, growth, key-byte splits, same-depth splits, append-tail, and same-key duplicate-run rewrite have all failed.<br/>
    /// </summary>
    /// <param name="headShelfOffset">The route-owned head shelf offset.</param>
    /// <param name="headShelfBytes">The current head shelf bytes.</param>
    /// <param name="profile">The SV8 shelf profile used by every segment in the chain.</param>
    /// <param name="encodedKey">The encoded scalar key being inserted.</param>
    /// <param name="incomingIdentity">The raw identity being inserted.</param>
    /// <param name="allowDuplicateKeys">Whether duplicate scalar keys are allowed when identities differ.</param>
    /// <param name="tailShelfOffset">Receives the final shelf offset in the rebuilt chain.</param>
    /// <param name="chainShelfCount">Receives the number of shelves in the rebuilt chain.</param>
    /// <param name="largestShelfItemCount">Receives the largest item count across rebuilt shelves.</param>
    /// <param name="insertResult">Receives the logical insert result represented by the chain rewrite.</param>
    /// <param name="telemetry">Receives DataKernel commit telemetry for the chain rewrite.</param>
    /// <returns>`true` when the mixed-key overflow chain was rewritten successfully.</returns>
    private bool TryRewriteScalar8VarIdentityOverflowChain(
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

        ulong[] keys = new ulong[256];
        Scalar8VarIdentityIdentityRef[] identities = new Scalar8VarIdentityIdentityRef[256];
        int tupleCount = 0;
        bool incomingAdded = false;
        Scalar8VarIdentityIdentityRef incomingIdentityRef = new(Array.Empty<byte>(), 0, true);
        using RouteVisitedOffsetSet visited = RouteVisitedOffsetSet.Rent();
        long currentOffset = headShelfOffset;
        byte[] currentBytes = headShelfBytes;
        while (currentOffset != 0)
        {
            if (!visited.Add(currentOffset))
            {
                throw new InvalidDataException("The SV8 overflow shelf chain contains a cycle.");
            }

            Scalar8VarIdentityReadOnly shelf = new(currentBytes, profile);
            if (!shelf.IsValid)
            {
                insertResult = Scalar8VarIdentityInsertResult.Invalid;
                return false;
            }

            for (int i = 0; i < shelf.ItemCount; i++)
            {
                ulong currentKey = shelf.ReadKeyAt(i);
                ReadOnlySpan<byte> currentIdentity = shelf.ReadIdentityAt(i);
                if (!incomingAdded && CompareScalar8VarIdentityTuple(encodedKey, incomingIdentity, currentKey, currentIdentity, profile.Descending) < 0)
                {
                    AddScalar8VarIdentityTuple(ref keys, ref identities, ref tupleCount, encodedKey, incomingIdentityRef);
                    incomingAdded = true;
                }

                if (currentKey == encodedKey)
                {
                    if (Scalar8VarIdentityLayout.IdentityBytesEqual(currentIdentity, incomingIdentity))
                    {
                        insertResult = Scalar8VarIdentityInsertResult.AlreadyPresent;
                        return false;
                    }
                }

                AddScalar8VarIdentityTuple(ref keys, ref identities, ref tupleCount, currentKey, new Scalar8VarIdentityIdentityRef(currentBytes, checked((int)shelf.ReadRecordOffsetAt(i)), false));
            }

            currentOffset = Scalar8VarIdentityLayout.ReadNextShelfOffset(currentBytes);
            if (currentOffset != 0)
            {
                currentBytes = ReadScalar8VarIdentityShelfBytes(currentOffset, profile.MaxIdentityLength, out Scalar8VarIdentityProfile nextProfile);
                if (nextProfile.ShelfExtentSize != profile.ShelfExtentSize)
                {
                    throw new InvalidDataException("The SV8 overflow shelf chain changed shelf extent sizes.");
                }
            }
        }

        if (!incomingAdded)
        {
            AddScalar8VarIdentityTuple(ref keys, ref identities, ref tupleCount, encodedKey, incomingIdentityRef);
        }

        byte[][] rebuiltShelves = new byte[4][];
        int rebuiltShelfCount = 0;
        int start = 0;
        try
        {
            while (start < tupleCount)
            {
                int bestCount = GetScalar8VarIdentityOverflowChunkCount(keys, identities, incomingIdentity, start, tupleCount - start, profile);
                if (bestCount == 0 ||
                    !TryBuildScalar8VarIdentityOverflowShelf(keys, identities, incomingIdentity, start, bestCount, profile, out byte[] bestShelf))
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
            FlushScalar8VarIdentityMutableBatchShelvesBeforeStructuralMutation();
            scalar8VarIdentityMutableBatchShelves.Remove(headShelfOffset);
            for (int i = rebuiltShelfCount - 1; i >= 1; i--)
            {
                Scalar8VarIdentityLayout.WriteNextShelfOffset(rebuiltShelves[i], nextOffset);
                RawDataReservation reservation = kernel.Reserve(profile.ShelfExtentSize);
                rebuiltShelves[i].AsSpan(0, profile.ShelfExtentSize).CopyTo(reservation.Span);
                nextOffset = reservation.Extent.Offset;
                if (tailOffset == headShelfOffset)
                {
                    tailOffset = reservation.Extent.Offset;
                }
            }

            Scalar8VarIdentityLayout.WriteNextShelfOffset(rebuiltShelves[0], nextOffset);
            Scalar8VarIdentityLayout.WriteTailShelfOffset(rebuiltShelves[0], tailOffset == headShelfOffset ? 0 : tailOffset);
            RawDataReservation headRewrite = kernel.ReserveAt(headShelfOffset, profile.ShelfExtentSize);
            rebuiltShelves[0].AsSpan(0, profile.ShelfExtentSize).CopyTo(headRewrite.Span);
            telemetry = CommitWithoutInvalidatingRouterReadCache();
            tailShelfOffset = tailOffset;
            chainShelfCount = rebuiltShelfCount;
            insertResult = Scalar8VarIdentityInsertResult.Inserted;
            return true;
        }
        finally
        {
            for (int i = 0; i < rebuiltShelfCount; i++)
            {
                ArrayPool<byte>.Shared.Return(rebuiltShelves[i]);
            }
        }
    }

    private static void AddScalar8VarIdentityTuple(ref ulong[] keys, ref byte[][] identities, ref int count, ulong key, byte[] identity)
    {
        if (count == keys.Length)
        {
            Array.Resize(ref keys, checked(keys.Length * 2));
            Array.Resize(ref identities, checked(identities.Length * 2));
        }

        keys[count] = key;
        identities[count] = identity;
        count++;
    }

    /// <summary>
    /// Adds one sorted `SV8` tuple reference to paired working arrays without copying existing variable identity bytes.<br/>
    /// Existing identities remain referenced by their source shelf bytes until the structural rewrite publishes packed replacement shelves.<br/>
    /// The incoming identity is represented by a sentinel ref and read from the caller-provided span by the chunk/build helpers.<br/>
    /// </summary>
    /// <param name="keys">The growable sorted scalar-key array.<br/></param>
    /// <param name="identities">The growable sorted identity-reference array.<br/></param>
    /// <param name="count">The current tuple count, incremented after insertion.<br/></param>
    /// <param name="key">The encoded scalar key for this tuple.<br/></param>
    /// <param name="identity">The identity source reference for this tuple.<br/></param>
    private static void AddScalar8VarIdentityTuple(ref ulong[] keys, ref Scalar8VarIdentityIdentityRef[] identities, ref int count, ulong key, Scalar8VarIdentityIdentityRef identity)
    {
        if (count == keys.Length)
        {
            Array.Resize(ref keys, checked(keys.Length * 2));
            Array.Resize(ref identities, checked(identities.Length * 2));
        }

        keys[count] = key;
        identities[count] = identity;
        count++;
    }

    /// <summary>
    /// Persists a sorted `SV8` tuple-reference range as one linked overflow chain and returns its published head offset.<br/>
    /// The method mirrors the byte-array overload but reads existing variable identities directly from source shelf bytes instead of temporary copies.<br/>
    /// Shelves are still built from the back so each `NextShelfOffset` can be written before the shelf bytes are reserved.<br/>
    /// </summary>
    /// <param name="keys">The sorted scalar keys.<br/></param>
    /// <param name="identities">The sorted raw identity references aligned with <paramref name="keys"/>.<br/></param>
    /// <param name="incomingIdentity">The incoming raw identity span used by sentinel identity references.<br/></param>
    /// <param name="start">The first tuple index to include.<br/></param>
    /// <param name="count">The number of tuples to include.<br/></param>
    /// <param name="profile">The `SV8` shelf profile used by each shelf.<br/></param>
    /// <param name="headOffset">Receives the first shelf offset in the persisted chain.<br/></param>
    /// <param name="tailOffset">Receives the final shelf offset in the persisted chain.<br/></param>
    /// <param name="largestShelfItemCount">Receives the largest item count in any built shelf.<br/></param>
    /// <param name="shelfCount">Receives the number of persisted shelves.<br/></param>
    /// <returns>`true` when every tuple was packed into a persisted shelf chain.<br/></returns>
    private bool TryCreateScalar8VarIdentityOverflowChain(
        ReadOnlySpan<ulong> keys,
        ReadOnlySpan<Scalar8VarIdentityIdentityRef> identities,
        ReadOnlySpan<byte> incomingIdentity,
        int start,
        int count,
        Scalar8VarIdentityProfile profile,
        out long headOffset,
        out long tailOffset,
        out int largestShelfItemCount,
        out int shelfCount)
    {
        headOffset = 0;
        tailOffset = 0;
        largestShelfItemCount = 0;
        shelfCount = 0;
        byte[][] shelves = new byte[4][];
        int[] counts = new int[4];
        int shelfWriteCount = 0;
        int cursor = start;
        int remaining = count;
        try
        {
            while (remaining > 0)
            {
                int chunkCount = GetScalar8VarIdentityOverflowChunkCount(keys, identities, incomingIdentity, cursor, remaining, profile);
                if (chunkCount == 0 ||
                    !TryBuildScalar8VarIdentityOverflowShelf(keys, identities, incomingIdentity, cursor, chunkCount, profile, out byte[] shelf))
                {
                    return false;
                }

                if (shelfWriteCount == shelves.Length)
                {
                    Array.Resize(ref shelves, checked(shelves.Length * 2));
                    Array.Resize(ref counts, checked(counts.Length * 2));
                }

                shelves[shelfWriteCount] = shelf;
                counts[shelfWriteCount] = chunkCount;
                shelfWriteCount++;
                cursor += chunkCount;
                remaining -= chunkCount;
            }

            long nextOffset = 0;
            for (int i = shelfWriteCount - 1; i >= 0; i--)
            {
                Scalar8VarIdentityLayout.WriteNextShelfOffset(shelves[i], nextOffset);
                Scalar8VarIdentityLayout.WriteTailShelfOffset(shelves[i], i == 0 && tailOffset != 0 ? tailOffset : 0);
                RawDataReservation reservation = kernel.Reserve(profile.ShelfExtentSize);
                shelves[i].AsSpan(0, profile.ShelfExtentSize).CopyTo(reservation.Span);
                nextOffset = reservation.Extent.Offset;
                headOffset = reservation.Extent.Offset;
                if (tailOffset == 0)
                {
                    tailOffset = reservation.Extent.Offset;
                }

                largestShelfItemCount = Math.Max(largestShelfItemCount, counts[i]);
                shelfCount++;
            }

            return headOffset != 0;
        }
        finally
        {
            for (int i = 0; i < shelfWriteCount; i++)
            {
                ArrayPool<byte>.Shared.Return(shelves[i]);
            }
        }
    }

    /// <summary>
    /// Calculates how many tuple references fit in one `SV8` overflow shelf without materializing identities.<br/>
    /// Existing identity lengths are read from the source shelf record, and the incoming sentinel length is read from <paramref name="incomingIdentity"/>.<br/>
    /// </summary>
    /// <param name="keys">The sorted scalar-key array; accepted for overload symmetry and future key-aware packing.<br/></param>
    /// <param name="identities">The sorted identity-reference array aligned with <paramref name="keys"/>.<br/></param>
    /// <param name="incomingIdentity">The incoming raw identity span used by sentinel identity references.<br/></param>
    /// <param name="start">The first tuple index to test.<br/></param>
    /// <param name="available">The maximum number of available tuples.<br/></param>
    /// <param name="profile">The target `SV8` shelf profile.<br/></param>
    /// <returns>The number of tuples that fit, or zero when the first tuple cannot fit.<br/></returns>
    private static int GetScalar8VarIdentityOverflowChunkCount(
        ReadOnlySpan<ulong> keys,
        ReadOnlySpan<Scalar8VarIdentityIdentityRef> identities,
        ReadOnlySpan<byte> incomingIdentity,
        int start,
        int available,
        Scalar8VarIdentityProfile profile)
    {
        int slotCapacityBytes = Scalar8VarIdentityLayout.CalculateSlotCapacityBytes(profile.ShelfExtentSize);
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

    /// <summary>
    /// Builds one packed `SV8` overflow shelf from tuple references without first copying variable identities into temporary arrays.<br/>
    /// The only identity copy performed here is the final write into the replacement shelf's byte arena.<br/>
    /// </summary>
    /// <param name="keys">The sorted scalar-key array.<br/></param>
    /// <param name="identities">The sorted identity-reference array aligned with <paramref name="keys"/>.<br/></param>
    /// <param name="incomingIdentity">The incoming raw identity span used by sentinel identity references.<br/></param>
    /// <param name="start">The first tuple index to include.<br/></param>
    /// <param name="count">The number of tuples to include.<br/></param>
    /// <param name="profile">The target `SV8` shelf profile.<br/></param>
    /// <param name="bytes">Receives the packed shelf bytes.<br/></param>
    /// <returns>`true` when the shelf was built successfully.<br/></returns>
    private static bool TryBuildScalar8VarIdentityOverflowShelf(
        ReadOnlySpan<ulong> keys,
        ReadOnlySpan<Scalar8VarIdentityIdentityRef> identities,
        ReadOnlySpan<byte> incomingIdentity,
        int start,
        int count,
        Scalar8VarIdentityProfile profile,
        out byte[] bytes)
    {
        bytes = ArrayPool<byte>.Shared.Rent(profile.ShelfExtentSize);
        Span<byte> target = bytes.AsSpan(0, profile.ShelfExtentSize);
        Scalar8VarIdentityLayout.Initialize(target, profile);
        int slotCapacityBytes = Scalar8VarIdentityLayout.ReadSlotCapacityBytes(target);
        int slotCursor = Scalar8VarIdentityLayout.HeaderSize;
        int recordCursor = Scalar8VarIdentityLayout.HeaderSize + slotCapacityBytes;
        for (int i = 0; i < count; i++)
        {
            int sourceIndex = start + i;
            ReadOnlySpan<byte> identity = identities[sourceIndex].ReadIdentity(incomingIdentity);
            if (identity.Length <= 0 || identity.Length > profile.MaxIdentityLength)
            {
                ArrayPool<byte>.Shared.Return(bytes);
                bytes = [];
                return false;
            }

            int recordLength = Scalar8VarIdentityLayout.GetNewRecordLength(identity.Length);
            if (recordCursor + recordLength > profile.ShelfExtentSize)
            {
                ArrayPool<byte>.Shared.Return(bytes);
                bytes = [];
                return false;
            }

            Scalar8VarIdentityLayout.WriteRecord(target, recordCursor, keys[sourceIndex], identity);
            Scalar8VarIdentityLayout.WriteSlotRecordOffset(target, slotCursor, recordCursor);
            Scalar8VarIdentityLayout.WriteSlotKeyPrefix(target, slotCursor, Scalar8VarIdentityLayout.CreateKeyPrefix(keys[sourceIndex]));
            recordCursor += recordLength;
            slotCursor += Scalar8VarIdentityLayout.SlotSize;
        }

        Scalar8VarIdentityLayout.WriteItemCount(target, count);
        Scalar8VarIdentityLayout.WriteSlotStreamLength(target, slotCursor - Scalar8VarIdentityLayout.HeaderSize);
        Scalar8VarIdentityLayout.WriteRecordArenaEnd(target, recordCursor);
        return true;
    }

    private static int GetScalar8VarIdentityOverflowChunkCount(
        ReadOnlySpan<ulong> keys,
        ReadOnlySpan<byte[]> identities,
        int start,
        int available,
        Scalar8VarIdentityProfile profile)
    {
        int slotCapacityBytes = Scalar8VarIdentityLayout.CalculateSlotCapacityBytes(profile.ShelfExtentSize);
        int recordCursor = Scalar8VarIdentityLayout.HeaderSize + slotCapacityBytes;
        int count = 0;
        while (count < available)
        {
            if (checked((count + 1) * Scalar8VarIdentityLayout.SlotSize) > slotCapacityBytes)
            {
                break;
            }

            byte[] identity = identities[start + count];
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

    private static bool TryBuildScalar8VarIdentityOverflowShelf(
        ReadOnlySpan<ulong> keys,
        ReadOnlySpan<byte[]> identities,
        int start,
        int count,
        Scalar8VarIdentityProfile profile,
        out byte[] bytes)
    {
        bytes = Scalar8VarIdentity.CreateEmpty(profile);
        int slotCapacityBytes = Scalar8VarIdentityLayout.ReadSlotCapacityBytes(bytes);
        int slotCursor = Scalar8VarIdentityLayout.HeaderSize;
        int recordCursor = Scalar8VarIdentityLayout.HeaderSize + slotCapacityBytes;
        for (int i = 0; i < count; i++)
        {
            int sourceIndex = start + i;
            byte[] identity = identities[sourceIndex];
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

    /// <summary>
    /// Builds one `SV8` shelf containing exactly one scalar-key plus variable-identity tuple directly into caller-owned bytes.<br/>
    /// This is used by append-tail paths after the caller has already reserved the target DataKernel span, avoiding an intermediate empty shelf allocation and second full-shelf copy.<br/>
    /// The method preserves normal insert validation for identity length and duplicate-key policy while keeping the byte layout identical to `Scalar8VarIdentity.Insert` on an empty shelf.<br/>
    /// </summary>
    /// <param name="target">The full target shelf span to initialize and populate.<br/></param>
    /// <param name="profile">The `SV8` shelf profile describing extent size and max identity length.<br/></param>
    /// <param name="encodedKey">The encoded sortable scalar key.<br/></param>
    /// <param name="identity">The raw variable identity bytes.<br/></param>
    /// <param name="allowDuplicateKeys">Whether duplicate scalar keys are allowed by the owning index.<br/></param>
    /// <returns>The logical insert result for the single tuple.<br/></returns>
    private static Scalar8VarIdentityInsertResult BuildSingleScalar8VarIdentityShelf(
        Span<byte> target,
        Scalar8VarIdentityProfile profile,
        ulong encodedKey,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys)
    {
        if ((uint)identity.Length == 0 || identity.Length > profile.MaxIdentityLength)
        {
            return Scalar8VarIdentityInsertResult.Invalid;
        }

        if (target.Length < profile.ShelfExtentSize)
        {
            return Scalar8VarIdentityInsertResult.Invalid;
        }

        Span<byte> shelf = target.Slice(0, profile.ShelfExtentSize);
        Scalar8VarIdentityLayout.Initialize(shelf, profile);
        int slotCapacityBytes = Scalar8VarIdentityLayout.ReadSlotCapacityBytes(shelf);
        int recordOffset = Scalar8VarIdentityLayout.HeaderSize + slotCapacityBytes;
        int recordLength = Scalar8VarIdentityLayout.GetNewRecordLength(identity.Length);
        if (Scalar8VarIdentityLayout.HeaderSize + Scalar8VarIdentityLayout.SlotSize > Scalar8VarIdentityLayout.HeaderSize + slotCapacityBytes ||
            recordOffset + recordLength > profile.ShelfExtentSize)
        {
            return Scalar8VarIdentityInsertResult.Full;
        }

        Scalar8VarIdentityLayout.WriteRecord(shelf, recordOffset, encodedKey, identity);
        Scalar8VarIdentityLayout.WriteSlotRecordOffset(shelf, Scalar8VarIdentityLayout.HeaderSize, recordOffset);
        Scalar8VarIdentityLayout.WriteSlotKeyPrefix(shelf, Scalar8VarIdentityLayout.HeaderSize, Scalar8VarIdentityLayout.CreateKeyPrefix(encodedKey));
        Scalar8VarIdentityLayout.WriteItemCount(shelf, 1);
        Scalar8VarIdentityLayout.WriteSlotStreamLength(shelf, Scalar8VarIdentityLayout.SlotSize);
        Scalar8VarIdentityLayout.WriteRecordArenaEnd(shelf, recordOffset + recordLength);
        return Scalar8VarIdentityInsertResult.Inserted;
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
        int slotCapacityBytes = Scalar8VarIdentityLayout.CalculateSlotCapacityBytes(profile.ShelfExtentSize);
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
            if (!inserted && CompareScalar8VarIdentityTuple(currentKey, currentIdentity, incomingKey, incomingIdentity, profile.Descending) > 0)
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

    /// <summary>
    /// Builds a two-shelf local split for one full `SV8` overflow-chain shelf using the source shelf's decoded sidecars as the identity source.<br/>
    /// The method inserts the incoming tuple into sorted order, detects duplicate/conflict outcomes, and chooses a balanced split boundary whose left and right ranges both fit the existing shelf profile.<br/>
    /// It intentionally copies only the touched shelf's identities instead of collecting identities from the entire linked chain.<br/>
    /// </summary>
    /// <param name="mutableShelf">The full source shelf being locally split.</param>
    /// <param name="profile">The `SV8` shelf profile used for both replacement shelves.</param>
    /// <param name="incomingKey">The encoded scalar key being inserted.</param>
    /// <param name="incomingIdentity">The raw variable identity being inserted.</param>
    /// <param name="allowDuplicateKeys">Whether duplicate scalar keys with different identities are allowed.</param>
    /// <param name="leftShelf">Receives the left replacement shelf image.</param>
    /// <param name="rightShelf">Receives the right replacement shelf image.</param>
    /// <param name="leftCount">Receives the number of tuples written into the left shelf.</param>
    /// <param name="rightCount">Receives the number of tuples written into the right shelf.</param>
    /// <param name="insertResult">Receives the logical insert result represented by the local split.</param>
    /// <returns><see langword="true"/> when a valid two-shelf split was built.</returns>
    private static bool TryBuildScalar8VarIdentityLocalChainSplit(
        Scalar8VarIdentityMutableShelfView mutableShelf,
        Scalar8VarIdentityProfile profile,
        ulong incomingKey,
        ReadOnlySpan<byte> incomingIdentity,
        bool allowDuplicateKeys,
        out byte[] leftShelf,
        out byte[] rightShelf,
        out int leftCount,
        out int rightCount,
        out Scalar8VarIdentityInsertResult insertResult)
    {
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
            if (!inserted && CompareScalar8VarIdentityTuple(currentKey, currentIdentity, incomingKey, incomingIdentity, profile.Descending) > 0)
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
        int preferredBoundary = totalCount >> 1;
        for (int delta = 0; delta < totalCount; delta++)
        {
            int lowerBoundary = preferredBoundary - delta;
            if (lowerBoundary > 0 &&
                lowerBoundary < totalCount &&
                TryBuildScalar8VarIdentityLocalChainSplitBoundary(keys, identitySource, profile, lowerBoundary, out leftShelf, out rightShelf, out leftCount, out rightCount))
            {
                insertResult = Scalar8VarIdentityInsertResult.Inserted;
                return true;
            }

            int upperBoundary = preferredBoundary + delta;
            if (upperBoundary != lowerBoundary &&
                upperBoundary > 0 &&
                upperBoundary < totalCount &&
                TryBuildScalar8VarIdentityLocalChainSplitBoundary(keys, identitySource, profile, upperBoundary, out leftShelf, out rightShelf, out leftCount, out rightCount))
            {
                insertResult = Scalar8VarIdentityInsertResult.Inserted;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Builds left and right replacement shelves for a single local overflow-chain split boundary.<br/>
    /// The boundary is tuple-count based rather than route-byte based because the existing parent route remains unchanged and the linked chain preserves sorted tuple order across shelves.<br/>
    /// </summary>
    /// <param name="keys">The sorted key array including the incoming tuple.</param>
    /// <param name="identities">The identity source aligned with <paramref name="keys"/>.</param>
    /// <param name="profile">The `SV8` shelf profile used for both replacement shelves.</param>
    /// <param name="boundary">The first tuple index belonging to the right replacement shelf.</param>
    /// <param name="leftShelf">Receives the left replacement shelf image.</param>
    /// <param name="rightShelf">Receives the right replacement shelf image.</param>
    /// <param name="leftCount">Receives the left tuple count.</param>
    /// <param name="rightCount">Receives the right tuple count.</param>
    /// <returns><see langword="true"/> when both replacement shelf ranges fit.</returns>
    private static bool TryBuildScalar8VarIdentityLocalChainSplitBoundary(
        ReadOnlySpan<ulong> keys,
        Scalar8VarIdentitySplitIdentitySource identities,
        Scalar8VarIdentityProfile profile,
        int boundary,
        out byte[] leftShelf,
        out byte[] rightShelf,
        out int leftCount,
        out int rightCount)
    {
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
            return false;
        }

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
            if (prefix == previousPrefix || keys[i - 1] == keys[i])
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
            if (prefix == previousPrefix || keys[i - 1] == keys[i])
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
        selectedRightPrefix = GetScalar8VarIdentityPrefix(keys[profile.Descending ? boundary - 1 : boundary], splitDepth);
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

    /// <summary>
    /// Emits a narrow `SV8` split diagnostic for one encoded scalar key when `LIBRADEX_DIAG_SV8_SPLIT_KEY` is set.<br/>
    /// This keeps normal write paths quiet while making route-publication investigations show whether a split placed the watched key into the left or right replacement shelf.<br/>
    /// </summary>
    /// <param name="splitKind">The split branch name being reported.<br/></param>
    /// <param name="incomingKey">The encoded scalar key being inserted when the split occurred.<br/></param>
    /// <param name="pathTarget">The route path target that reached the split shelf.<br/></param>
    /// <param name="target">The target shelf metadata before replacement.<br/></param>
    /// <param name="splitDepth">The scalar-key byte depth used for the split.<br/></param>
    /// <param name="selectedRightPrefix">The first split prefix routed to the right shelf.<br/></param>
    /// <param name="leftOffset">The durable offset of the left replacement shelf.<br/></param>
    /// <param name="rightOffset">The durable offset of the right replacement shelf.<br/></param>
    /// <param name="leftShelf">The left replacement shelf bytes.<br/></param>
    /// <param name="rightShelf">The right replacement shelf bytes.<br/></param>
    /// <param name="profile">The `SV8` shelf profile used by both replacement shelves.<br/></param>
    private static void WriteScalar8VarIdentitySplitDiagnosticIfEnabled(
        string splitKind,
        ulong incomingKey,
        Scalar8VarIdentityRoutePathTarget pathTarget,
        Scalar8VarIdentityRouteTarget target,
        ushort splitDepth,
        byte selectedRightPrefix,
        long leftOffset,
        long rightOffset,
        byte[] leftShelf,
        byte[] rightShelf,
        Scalar8VarIdentityProfile profile)
    {
        string? keyText = Environment.GetEnvironmentVariable("LIBRADEX_DIAG_SV8_SPLIT_KEY");
        if (string.IsNullOrWhiteSpace(keyText) ||
            !ulong.TryParse(keyText, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong watchedKey))
        {
            return;
        }

        int leftWatchedCount = CountScalar8VarIdentityShelfKey(leftShelf, profile, watchedKey);
        int rightWatchedCount = CountScalar8VarIdentityShelfKey(rightShelf, profile, watchedKey);
        if (incomingKey != watchedKey && leftWatchedCount == 0 && rightWatchedCount == 0)
        {
            return;
        }

        Console.Error.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"sv8-split-diag kind={splitKind} watchedKey={watchedKey} incomingKey={incomingKey} parent={pathTarget.ParentRouterOffset} routeIndex={pathTarget.RouteIndex} routePrefix={pathTarget.RoutePrefixByte} source={target.Offset} routerDepth={target.RouterDepth} splitDepth={splitDepth} rightPrefix={selectedRightPrefix} leftOffset={leftOffset} rightOffset={rightOffset} leftItems={Scalar8VarIdentityLayout.ReadItemCount(leftShelf)} rightItems={Scalar8VarIdentityLayout.ReadItemCount(rightShelf)} leftWatched={leftWatchedCount} rightWatched={rightWatchedCount}"));
    }


    /// <summary>
    /// Counts rows for one encoded scalar key inside a rebuilt `SV8` shelf image.<br/>
    /// Invalid diagnostic shelf bytes return zero because the caller is only reporting optional evidence and the main split path still validates through normal publication checks.<br/>
    /// </summary>
    /// <param name="shelfBytes">The rebuilt shelf bytes to inspect.<br/></param>
    /// <param name="profile">The profile used by the shelf.<br/></param>
    /// <param name="encodedKey">The encoded scalar key to count.<br/></param>
    /// <returns>The number of rows with the requested key.</returns>
    private static int CountScalar8VarIdentityShelfKey(byte[] shelfBytes, Scalar8VarIdentityProfile profile, ulong encodedKey)
    {
        try
        {
            Scalar8VarIdentityReadOnly shelf = new(shelfBytes, profile);
            int count = 0;
            for (int i = 0; i < shelf.ItemCount; i++)
            {
                if (shelf.ReadKeyAt(i) == encodedKey)
                {
                    count++;
                }
            }

            return count;
        }
        catch (InvalidDataException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Creates the router chain needed to publish an `SV8` split that occurs deeper than the immediate child byte.<br/>
    /// Intermediate routers preserve lexicographic routing for bytes between the parent route and the actual split byte: prefixes below the shared stem route left, the exact stem byte continues deeper, and prefixes above the shared stem route right.<br/>
    /// The returned offset is the top router that should replace the parent route target.<br/>
    /// </summary>
    /// <param name="firstDepth">The first key byte depth below the parent route.</param>
    /// <param name="splitDepth">The key byte depth where the replacement shelves diverge.</param>
    /// <param name="allocationClassId">The router allocation class id to persist.</param>
    /// <param name="stemKey">A key from the split source; all source keys share its bytes before <paramref name="splitDepth"/>.</param>
    /// <param name="leftShelfOffset">The replacement shelf for lower prefixes.</param>
    /// <param name="rightShelfOffset">The replacement shelf for the selected and higher split prefixes.</param>
    /// <param name="selectedRightPrefix">The first split-depth prefix routed to the right shelf.</param>
    /// <returns>The top router offset to publish into the parent router.</returns>
    private long CreateScalar8VarIdentitySplitRouterChain(
        ushort firstDepth,
        ushort splitDepth,
        ushort allocationClassId,
        ulong stemKey,
        long leftShelfOffset,
        long rightShelfOffset,
        byte selectedRightPrefix,
        bool descending)
    {
        RawDataReservation splitRouterReservation = kernel.Reserve(RouterLayout.Size);
        RouterWriter splitWriter = new(splitRouterReservation.Span);
        splitWriter.InitializeExpandedOneByte(
            splitDepth,
            allocationClassId,
            CreateSplitScalar8Scalar8RouteTargets(descending ? rightShelfOffset : leftShelfOffset, descending ? leftShelfOffset : rightShelfOffset, selectedRightPrefix));

        long nextRouterOffset = splitRouterReservation.Extent.Offset;
        for (int depth = splitDepth - 1; depth >= firstDepth; depth--)
        {
            RawDataReservation intermediateReservation = kernel.Reserve(RouterLayout.Size);
            RouterWriter intermediateWriter = new(intermediateReservation.Span);
            intermediateWriter.InitializeExpandedOneByte(
                checked((ushort)depth),
                allocationClassId,
                CreateVarKeyScalar8IntermediateSplitRouteTargets(
                    nextRouterOffset,
                    descending ? rightShelfOffset : leftShelfOffset,
                    descending ? leftShelfOffset : rightShelfOffset,
                    GetScalar8VarIdentityPrefix(stemKey, depth)));
            nextRouterOffset = intermediateReservation.Extent.Offset;
        }

        return nextRouterOffset;
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
    /// Repoints every parent-router route that currently targets one replaced `SV8` shelf.<br/>
    /// A shelf can be intentionally shared by several direct routes after same-depth routing decisions; replacing only the route that received the incoming tuple leaves the old shelf reachable and makes full-range enumeration see stale duplicate tuples.<br/>
    /// This method is used by grow and child-router split publication where every alias to the old shelf must move to the same replacement target.<br/>
    /// </summary>
    /// <param name="parentRouterOffset">The parent router containing routes to inspect.<br/></param>
    /// <param name="sourceTargetOffset">The old shelf offset being replaced.<br/></param>
    /// <param name="replacementTargetOffset">The new shelf or child-router target offset.<br/></param>
    private void RepointMatchingRoutes(long parentRouterOffset, long sourceTargetOffset, long replacementTargetOffset)
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
        int routeCount = parentReader.RouteCount;
        for (int routeIndex = 0; routeIndex < routeCount; routeIndex++)
        {
            if (parentReader.GetRouteTargetAt(routeIndex) == sourceTargetOffset)
            {
                parentWriter.WriteRouteTarget(routeIndex, replacementTargetOffset);
            }
        }
    }

    /// <summary>
    /// Repoints every route in one `SV8` route graph that still targets a replaced shelf offset.<br/>
    /// Structural publication can encounter shelf aliases outside the immediate parent router after earlier compressed or same-depth routing decisions; leaving those aliases pointed at the old shelf makes exact-key reads see stale, duplicate, or missing rows.<br/>
    /// This helper walks from the index root, rewrites matching route targets in place, and preserves child routers that point elsewhere.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The root router for the `SV8` index being mutated.<br/></param>
    /// <param name="sourceTargetOffset">The stale shelf offset to replace.<br/></param>
    /// <param name="replacementTargetOffset">The new shelf or router target offset.<br/></param>
    /// <param name="requiredRouterDepth">For a deeper transform, the final byte fixed by the common source stem; otherwise the optional owner-depth filter.<br/></param>
    /// <param name="ownedStemKey">A proven common stem causes all incoming owners to be refined, rather than blindly repointed.<br/></param>
    private void RepointMatchingScalar8VarIdentityRoutes(long rootRouterOffset, long sourceTargetOffset, long replacementTargetOffset, ushort? requiredRouterDepth = null, ulong? ownedStemKey = null)
    {
        using RouteVisitedOffsetSet visitedRouters = RouteVisitedOffsetSet.Rent();
        RepointMatchingScalar8VarIdentityRoutesCore(rootRouterOffset, sourceTargetOffset, replacementTargetOffset, requiredRouterDepth, visitedRouters, ownedStemKey);
    }

    /// <summary>
    /// Recursively walks `SV8` routers for <see cref="RepointMatchingScalar8VarIdentityRoutes"/> without allocating a managed traversal queue.<br/>
    /// The recursion depth is bounded by the scalar-key byte count, and each router is guarded by offset to tolerate shared subtrees.<br/>
    /// </summary>
    /// <param name="routerOffset">The router page currently being inspected.<br/></param>
    /// <param name="sourceTargetOffset">The stale shelf offset to replace.<br/></param>
    /// <param name="replacementTargetOffset">The new shelf or router target offset.<br/></param>
    /// <param name="visitedRouters">The route-graph cycle and alias guard.<br/></param>
    private void RepointMatchingScalar8VarIdentityRoutesCore(
        long routerOffset,
        long sourceTargetOffset,
        long replacementTargetOffset,
        ushort? requiredRouterDepth,
        RouteVisitedOffsetSet visitedRouters,
        ulong? ownedStemKey)
    {
        if (routerOffset <= 0 || !visitedRouters.Add(routerOffset))
        {
            return;
        }

        byte[] routerBytes = new byte[RouterLayout.Size];
        kernel.Read(routerOffset, routerBytes);
        RouterReader reader = new(routerBytes);
        if (!reader.IsValid)
        {
            throw new InvalidDataException("The SV8 route graph contains an invalid router during route repointing.");
        }

        bool refinedRangeParent = false;
        if (ownedStemKey.HasValue && reader.KeyDepth <= requiredRouterDepth.GetValueOrDefault() && !reader.HasDirectIndex)
        {
            if (reader.PrefixByteCount != 1)
                throw new InvalidDataException("SV8 byte walkers cannot publish a multi-byte incoming owner.");
            long[] targets = new long[RouterLayout.MaxOneByteRouteCount];
            byte ownedPrefix = GetScalar8VarIdentityPrefix(ownedStemKey.GetValueOrDefault(), reader.KeyDepth);
            for (int prefix = 0; prefix < targets.Length; prefix++)
            {
                long target = reader.FindTarget((byte)prefix);
                targets[prefix] = target == sourceTargetOffset
                    ? prefix == ownedPrefix ? replacementTargetOffset : 0
                    : target;
                refinedRangeParent |= target == sourceTargetOffset;
            }
            if (refinedRangeParent)
            {
                var expanded = kernel.ReserveAt(routerOffset, RouterLayout.Size);
                RouterWriter expandedWriter = new(expanded.Span);
                expandedWriter.InitializeExpandedOneByte(reader.KeyDepth, reader.AllocationClassId, targets);
                if (reader.IsArenaMember)
                    expandedWriter.WriteArenaMetadata(reader.ArenaBaseDelta, reader.ArenaLength,
                        reader.ArenaRouterPageSize, reader.ArenaRouterPageIndex, reader.ArenaRouterPageCount, reader.ArenaFlags);
            }
        }

        RawDataReservation rewrite = default;
        RouterWriter writer = default;
        bool rewriteStarted = false;
        int routeCount = reader.RouteCount;
        for (int routeIndex = 0; routeIndex < routeCount; routeIndex++)
        {
            long targetOffset = reader.GetRouteTargetAt(routeIndex);
            if (targetOffset == sourceTargetOffset)
            {
                if (refinedRangeParent) continue;
                if (!ownedStemKey.HasValue && requiredRouterDepth.HasValue && reader.KeyDepth != requiredRouterDepth.GetValueOrDefault())
                {
                    continue;
                }

                if (!rewriteStarted)
                {
                    rewrite = kernel.ReserveAt(routerOffset, RouterLayout.Size);
                    routerBytes.CopyTo(rewrite.Span);
                    writer = new RouterWriter(rewrite.Span);
                    rewriteStarted = true;
                }

                if (ownedStemKey.HasValue && reader.KeyDepth <= requiredRouterDepth.GetValueOrDefault())
                {
                    if (!reader.HasDirectIndex)
                        throw new InvalidDataException("SV8 stem refinement requires direct incoming owners.");
                    byte ownedPrefix = GetScalar8VarIdentityPrefix(ownedStemKey.GetValueOrDefault(), reader.KeyDepth);
                    writer.WriteRouteTarget(routeIndex, routeIndex == ownedPrefix ? replacementTargetOffset : 0);
                }
                else
                    writer.WriteRouteTarget(routeIndex, replacementTargetOffset);
                continue;
            }

            if (ClassifyScalar8VarIdentityRouteTarget(targetOffset) == Scalar8VarIdentityRouteTargetKind.Router)
            {
                RepointMatchingScalar8VarIdentityRoutesCore(targetOffset, sourceTargetOffset, replacementTargetOffset, requiredRouterDepth, visitedRouters, ownedStemKey);
            }
        }
    }

    /// <summary>
    /// Repoints every direct `SV8` router at one key depth that still targets a same-depth split source shelf.<br/>
    /// Same-depth splits publish left/right shelves by the parent route prefix, so aliases outside the immediate parent must be rewritten with the same prefix boundary or future inserts can keep using the stale full shelf.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The root router for the `SV8` index being mutated.</param>
    /// <param name="sourceShelfOffset">The full source shelf offset being replaced.</param>
    /// <param name="selectedRightPrefix">The first route prefix that should point at the right replacement shelf.</param>
    /// <param name="leftShelfOffset">The left replacement shelf offset.</param>
    /// <param name="rightShelfOffset">The right replacement shelf offset.</param>
    /// <param name="requiredRouterDepth">The direct-router key depth represented by the same-depth split.</param>
    private void RepointSameDepthScalar8VarIdentityRoutes(
        long rootRouterOffset,
        long sourceShelfOffset,
        byte selectedRightPrefix,
        long leftShelfOffset,
        long rightShelfOffset,
        ushort requiredRouterDepth,
        bool descending)
    {
        using RouteVisitedOffsetSet visitedRouters = RouteVisitedOffsetSet.Rent();
        RepointSameDepthScalar8VarIdentityRoutesCore(rootRouterOffset, sourceShelfOffset, selectedRightPrefix, leftShelfOffset, rightShelfOffset, requiredRouterDepth, descending, visitedRouters);
    }

    /// <summary>
    /// Recursively walks `SV8` routers for <see cref="RepointSameDepthScalar8VarIdentityRoutes"/> while preserving child routers not involved in the source-shelf alias set.<br/>
    /// </summary>
    /// <param name="routerOffset">The router page currently being inspected.</param>
    /// <param name="sourceShelfOffset">The full source shelf offset being replaced.</param>
    /// <param name="selectedRightPrefix">The first route prefix that should point at the right replacement shelf.</param>
    /// <param name="leftShelfOffset">The left replacement shelf offset.</param>
    /// <param name="rightShelfOffset">The right replacement shelf offset.</param>
    /// <param name="requiredRouterDepth">The direct-router key depth represented by the same-depth split.</param>
    /// <param name="visitedRouters">The route-graph cycle and alias guard.</param>
    private void RepointSameDepthScalar8VarIdentityRoutesCore(
        long routerOffset,
        long sourceShelfOffset,
        byte selectedRightPrefix,
        long leftShelfOffset,
        long rightShelfOffset,
        ushort requiredRouterDepth,
        bool descending,
        RouteVisitedOffsetSet visitedRouters)
    {
        if (routerOffset <= 0 || !visitedRouters.Add(routerOffset))
        {
            return;
        }

        byte[] routerBytes = new byte[RouterLayout.Size];
        kernel.Read(routerOffset, routerBytes);
        RouterReader reader = new(routerBytes);
        if (!reader.IsValid)
        {
            throw new InvalidDataException("The SV8 route graph contains an invalid router during same-depth route repointing.");
        }

        RawDataReservation rewrite = default;
        RouterWriter writer = default;
        bool rewriteStarted = false;
        int routeCount = reader.RouteCount;
        for (int routeIndex = 0; routeIndex < routeCount; routeIndex++)
        {
            long targetOffset = reader.GetRouteTargetAt(routeIndex);
            if (targetOffset == sourceShelfOffset && reader.HasDirectIndex && reader.KeyDepth == requiredRouterDepth)
            {
                if (!rewriteStarted)
                {
                    rewrite = kernel.ReserveAt(routerOffset, RouterLayout.Size);
                    routerBytes.CopyTo(rewrite.Span);
                    writer = new RouterWriter(rewrite.Span);
                    rewriteStarted = true;
                }

                long replacementOffset = routeIndex < selectedRightPrefix
                    ? (descending ? rightShelfOffset : leftShelfOffset)
                    : (descending ? leftShelfOffset : rightShelfOffset);
                writer.WriteRouteTarget(routeIndex, replacementOffset);
                continue;
            }

            if (ClassifyScalar8VarIdentityRouteTarget(targetOffset) == Scalar8VarIdentityRouteTargetKind.Router)
            {
                RepointSameDepthScalar8VarIdentityRoutesCore(targetOffset, sourceShelfOffset, selectedRightPrefix, leftShelfOffset, rightShelfOffset, requiredRouterDepth, descending, visitedRouters);
            }
        }
    }

    /// <summary>
    /// Repoints one exact parent route to a duplicate-key terminal chain while moving sibling aliases to a nonterminal shelf.<br/>
    /// Shared shelves can be referenced by several parent-prefix routes before a hot duplicate key is isolated.<br/>
    /// Sending every alias into the exact-key terminal chain is incorrect because a different parent prefix can still share the remaining key bytes and reach the terminal root for the wrong scalar key.<br/>
    /// This method preserves the exact incoming prefix as the terminal-chain route and sends all other aliases of the same source shelf to the caller-supplied fallback shelf.<br/>
    /// </summary>
    /// <param name="parentRouterOffset">The parent router containing the shared shelf routes.</param>
    /// <param name="sourceTargetOffset">The old shared shelf offset.</param>
    /// <param name="exactRouteIndex">The parent route index for the extracted scalar key.</param>
    /// <param name="exactTargetOffset">The terminal-chain or exact replacement target for <paramref name="exactRouteIndex"/>.</param>
    /// <param name="aliasTargetOffset">The fallback shelf target for sibling aliases that still pointed to <paramref name="sourceTargetOffset"/>.</param>
    private void RepointExactRouteAndAliases(
        long parentRouterOffset,
        long sourceTargetOffset,
        int exactRouteIndex,
        long exactTargetOffset,
        long aliasTargetOffset)
    {
        byte[] parentRouterBytes = new byte[RouterLayout.Size];
        kernel.Read(parentRouterOffset, parentRouterBytes);
        RouterReader parentReader = new(parentRouterBytes);
        if (!parentReader.IsValid)
        {
            throw new InvalidDataException("The SV8 exact-route parent router is invalid.");
        }

        RawDataReservation parentRouterRewrite = kernel.ReserveAt(parentRouterOffset, RouterLayout.Size);
        parentRouterBytes.CopyTo(parentRouterRewrite.Span);
        RouterWriter parentWriter = new(parentRouterRewrite.Span);
        int routeCount = parentReader.RouteCount;
        for (int routeIndex = 0; routeIndex < routeCount; routeIndex++)
        {
            if (parentReader.GetRouteTargetAt(routeIndex) == sourceTargetOffset)
            {
                parentWriter.WriteRouteTarget(routeIndex, routeIndex == exactRouteIndex ? exactTargetOffset : aliasTargetOffset);
            }
        }
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

    /// <summary>
    /// Reads one terminal variable-identity shelf into a pooled byte array for `SV8` range readers.<br/>
    /// The shelf stores identity records only; the scalar key is carried by the terminal identity root that points at the chain.<br/>
    /// </summary>
    /// <param name="shelfOffset">The durable offset of the terminal variable-identity shelf.</param>
    /// <param name="shelfExtentSize">The extent size recorded on the terminal root.</param>
    /// <returns>A pooled byte array containing the shelf bytes.</returns>
    internal byte[] ReadTerminalVarIdentityShelfBytes(long shelfOffset, int shelfExtentSize)
    {
        byte[] shelfBytes = ArrayPool<byte>.Shared.Rent(shelfExtentSize);
        try
        {
            ReadTerminalVarIdentityShelfBytesCached(shelfOffset, shelfExtentSize).AsSpan(0, shelfExtentSize).CopyTo(shelfBytes);
            return shelfBytes;
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(shelfBytes, clearArray: false);
            throw;
        }
    }

    private byte[] ReadTerminalVarIdentityShelfBytesCached(long shelfOffset, int shelfExtentSize)
    {
        if (terminalVarIdentityShelfReadCache.TryGetValue(shelfOffset, out byte[]? cachedShelfBytes))
        {
            if (cachedShelfBytes.Length < shelfExtentSize)
            {
                throw new InvalidDataException("The cached terminal variable identity shelf is smaller than the requested extent.");
            }

            return cachedShelfBytes;
        }

        byte[] shelfBytes = new byte[shelfExtentSize];
        kernel.Read(shelfOffset, shelfBytes);
        TerminalVarIdentityShelfLayout.Validate(shelfBytes, shelfExtentSize);
        terminalVarIdentityShelfReadCache[shelfOffset] = shelfBytes;
        return shelfBytes;
    }

    /// <summary>
    /// Inserts one raw identity into an existing terminal `SV8` duplicate-key route.<br/>
    /// Append-like identities mutate only the current tail shelf; out-of-order identities rebuild the identity-only chain so byte ordering remains stable.<br/>
    /// </summary>
    /// <param name="rootOffset">The terminal route root offset.</param>
    /// <param name="maxIdentityLength">The maximum raw identity length accepted by the owning index.</param>
    /// <param name="encodedKey">The encoded scalar key expected on the terminal root.</param>
    /// <param name="identity">The raw identity bytes to insert.</param>
    /// <param name="allowDuplicateKeys">Whether more than one identity may share the scalar key.</param>
    /// <returns>The routed insert result.</returns>
    private Scalar8VarIdentityRoutedInsertResult InsertIntoScalar8VarIdentityTerminalRoute(
        long rootOffset,
        int maxIdentityLength,
        ulong encodedKey,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys)
    {
        if (!allowDuplicateKeys)
        {
            return new Scalar8VarIdentityRoutedInsertResult(
                Scalar8VarIdentityRoutedInsertKind.KeyConflict,
                Scalar8VarIdentityInsertResult.KeyConflict,
                rootOffset,
                0,
                default);
        }

        if ((uint)identity.Length == 0 || identity.Length > maxIdentityLength)
        {
            return new Scalar8VarIdentityRoutedInsertResult(
                Scalar8VarIdentityRoutedInsertKind.Invalid,
                Scalar8VarIdentityInsertResult.Invalid,
                rootOffset,
                0,
                default);
        }

        Span<byte> expectedKey = stackalloc byte[Scalar8VarIdentityLayout.KeySize];
        BinaryPrimitives.WriteUInt64BigEndian(expectedKey, encodedKey);
        byte[] rootBytes = ReadTerminalIdentityRootBytes(rootOffset);
        if (TerminalIdentityRootLayout.ReadShape(rootBytes) != TerminalIdentityRootLayout.ShapeScalar8VarIdentity ||
            !IsTerminalIdentityRootForKey(rootBytes, expectedKey, out long firstShelfOffset))
        {
            throw new InvalidDataException("The routed SV8 terminal var identity root does not match the inserted key.");
        }

        int shelfExtentSize = TerminalIdentityRootLayout.ReadShelfExtentSize(rootBytes);
        long tailShelfOffset = TerminalIdentityRootLayout.ReadTailShelfOffset(rootBytes);
        if (TryAppendScalar8VarIdentityTerminalTail(rootOffset, rootBytes, firstShelfOffset, tailShelfOffset, shelfExtentSize, identity, out DataKernelCommitTelemetry appendTelemetry, out Scalar8VarIdentityInsertDiagnosticPath appendPath))
        {
            return new Scalar8VarIdentityRoutedInsertResult(
                Scalar8VarIdentityRoutedInsertKind.WalkedDuplicateRunOverflow,
                Scalar8VarIdentityInsertResult.Inserted,
                rootOffset,
                rootOffset,
                appendTelemetry,
                0,
                shelfExtentSize,
                DiagnosticPath: appendPath);
        }

        if (TryInsertIntoScalar8VarIdentityTerminalChain(rootOffset, rootBytes, firstShelfOffset, shelfExtentSize, identity, out Scalar8VarIdentityRoutedInsertResult chainInsertResult))
        {
            return chainInsertResult with { DiagnosticPath = Scalar8VarIdentityInsertDiagnosticPath.TerminalChainInsert };
        }

        using PooledTerminalVarIdentitySet identities = ReadScalar8VarIdentityTerminalIdentitiesPooled(rootOffset, expectedKey, shelfExtentSize);
        int insertIndex = LowerBoundTerminalVarIdentity(identities, identity, rootBytes[TerminalIdentityRootLayout.SortDirectionOffset] != 0);
        if (insertIndex < identities.Count && Scalar8VarIdentityLayout.IdentityBytesEqual(identities.ReadAt(insertIndex), identity))
        {
            return new Scalar8VarIdentityRoutedInsertResult(
                Scalar8VarIdentityRoutedInsertKind.NoOp,
                Scalar8VarIdentityInsertResult.AlreadyPresent,
                rootOffset,
                0,
                default,
                identities.Count,
                shelfExtentSize);
        }

        identities.InsertAt(insertIndex, identity);
        DataKernelCommitTelemetry rewriteTelemetry = RewriteScalar8VarIdentityTerminalRoute(
            rootOffset,
            TerminalIdentityRootLayout.ShapeScalar8VarIdentity,
            expectedKey,
            shelfExtentSize,
            identities);
        return new Scalar8VarIdentityRoutedInsertResult(
            Scalar8VarIdentityRoutedInsertKind.WalkedDuplicateRunOverflow,
            Scalar8VarIdentityInsertResult.Inserted,
            rootOffset,
            rootOffset,
            rewriteTelemetry,
            identities.Count,
            shelfExtentSize,
            DiagnosticPath: Scalar8VarIdentityInsertDiagnosticPath.TerminalFullRewrite);
    }

    /// <summary>
    /// Checks whether a routed terminal identity root owns the incoming scalar key without reading any terminal shelves.<br/>
    /// Terminal roots are exact-key shapes; a nonmatching key must be structurally split before tuple insertion continues.<br/>
    /// </summary>
    /// <param name="rootOffset">The terminal identity root offset reached by routing.<br/></param>
    /// <param name="encodedKey">The incoming encoded scalar key.<br/></param>
    /// <returns><see langword="true"/> when the root key matches <paramref name="encodedKey"/>; otherwise <see langword="false"/>.</returns>
    private bool IsScalar8VarIdentityTerminalRootForEncodedKey(long rootOffset, ulong encodedKey)
    {
        Span<byte> expectedKey = stackalloc byte[Scalar8VarIdentityLayout.KeySize];
        BinaryPrimitives.WriteUInt64BigEndian(expectedKey, encodedKey);
        byte[] rootBytes = ReadTerminalIdentityRootBytes(rootOffset);
        return TerminalIdentityRootLayout.ReadShape(rootBytes) == TerminalIdentityRootLayout.ShapeScalar8VarIdentity &&
            IsTerminalIdentityRootForKey(rootBytes, expectedKey, out _);
    }

    /// <summary>
    /// Splits a route that incorrectly reaches an exact-key terminal identity root for a different scalar key.<br/>
    /// The existing terminal root remains the owner of its declared scalar key, while the incoming key is inserted into a new ordinary `SV8` shelf and future nonmatching branches route to that mutable shelf.<br/>
    /// </summary>
    /// <param name="pathTarget">The routed path target that reached the terminal root.</param>
    /// <param name="maxIdentityLength">The maximum raw identity byte length allowed by the owning index.</param>
    /// <param name="encodedKey">The incoming scalar key.</param>
    /// <param name="identity">The incoming raw identity bytes.</param>
    /// <param name="allowDuplicateKeys">Whether duplicate scalar keys are allowed by the owning index.</param>
    /// <returns>The routed insert result for the incoming tuple.</returns>
    private Scalar8VarIdentityRoutedInsertResult SplitMismatchedScalar8VarIdentityTerminalRoute(
        Scalar8VarIdentityRoutePathTarget pathTarget,
        int maxIdentityLength,
        ulong encodedKey,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys)
    {
        if (!allowDuplicateKeys)
        {
            return new Scalar8VarIdentityRoutedInsertResult(
                Scalar8VarIdentityRoutedInsertKind.KeyConflict,
                Scalar8VarIdentityInsertResult.KeyConflict,
                pathTarget.Target.Offset,
                0,
                default);
        }

        if ((uint)identity.Length == 0 || identity.Length > maxIdentityLength)
        {
            return new Scalar8VarIdentityRoutedInsertResult(
                Scalar8VarIdentityRoutedInsertKind.Invalid,
                Scalar8VarIdentityInsertResult.Invalid,
                pathTarget.Target.Offset,
                0,
                default);
        }

        byte[] rootBytes = ReadTerminalIdentityRootBytes(pathTarget.Target.Offset);
        if (TerminalIdentityRootLayout.ReadShape(rootBytes) != TerminalIdentityRootLayout.ShapeScalar8VarIdentity)
        {
            throw new InvalidDataException("The routed SV8 terminal target is not an SV8 terminal identity root.");
        }

        ulong terminalKey = BinaryPrimitives.ReadUInt64BigEndian(rootBytes.AsSpan(TerminalIdentityRootLayout.KeyBytesOffset, Scalar8VarIdentityLayout.KeySize));
        Scalar8VarIdentityProfile profile = Scalar8VarIdentityProfile.Create(4 * 1024, maxIdentityLength,
            rootBytes[TerminalIdentityRootLayout.SortDirectionOffset] != 0);
        byte[] incomingShelf = Scalar8VarIdentity.CreateEmpty(profile);
        Scalar8VarIdentityInsertResult insertResult = Scalar8VarIdentity.InsertInPlace(
            incomingShelf,
            profile,
            encodedKey,
            identity,
            allowDuplicateKeys,
            out byte[] rewrittenIncomingShelf);
        if (insertResult != Scalar8VarIdentityInsertResult.Inserted)
        {
            return new Scalar8VarIdentityRoutedInsertResult(
                Scalar8VarIdentityRoutedInsertKind.Invalid,
                insertResult,
                pathTarget.Target.Offset,
                0,
                default);
        }

        RawDataReservation incomingShelfReservation = kernel.Reserve(profile.ShelfExtentSize);
        rewrittenIncomingShelf.CopyTo(incomingShelfReservation.Span);
        ushort firstDepth = checked((ushort)(pathTarget.Target.RouterDepth + 1));
        long replacementOffset = CreateScalar8VarIdentityTerminalMismatchRouterChain(
            firstDepth,
            pathTarget.Target.AllocationClassId,
            terminalKey,
            pathTarget.Target.Offset,
            encodedKey,
            incomingShelfReservation.Extent.Offset);
        RepointMatchingRoutes(pathTarget.ParentRouterOffset, pathTarget.Target.Offset, replacementOffset);
        DataKernelCommitTelemetry telemetry = CommitAndInvalidateRouterReadCache();
        return new Scalar8VarIdentityRoutedInsertResult(
            Scalar8VarIdentityRoutedInsertKind.WalkedShelfSplit,
            Scalar8VarIdentityInsertResult.Inserted,
            incomingShelfReservation.Extent.Offset,
            replacementOffset,
            telemetry,
            1,
            profile.ShelfExtentSize);
    }

    /// <summary>
    /// Converts a full same-key `SV8` shelf chain into a terminal var-identity route when scalar-key routing cannot split the pressure.<br/>
    /// The replacement root stores the scalar key once and the replacement shelves store only sorted raw identities.<br/>
    /// </summary>
    private bool TryConvertScalar8VarIdentityDuplicateRunToTerminalRoute(
        long rootRouterOffset,
        Scalar8VarIdentityRoutePathTarget pathTarget,
        Scalar8VarIdentityMutableShelfView mutableShelf,
        Scalar8VarIdentityProfile profile,
        ulong encodedKey,
        ReadOnlySpan<byte> incomingIdentity,
        out Scalar8VarIdentityRoutedInsertResult result)
    {
        result = default;
        if (mutableShelf.ItemCount == 0 ||
            mutableShelf.ReadKeyAt(0) != encodedKey ||
            mutableShelf.ReadKeyAt(mutableShelf.ItemCount - 1) != encodedKey)
        {
            return false;
        }

        if (!IsScalar8VarIdentitySameKeyChain(pathTarget.Target.Offset, mutableShelf.Bytes, profile, encodedKey))
        {
            return false;
        }

        FlushScalar8VarIdentityMutableBatchShelvesBeforeStructuralMutation();
        using PooledTerminalVarIdentitySet identities = CollectScalar8VarIdentitySameKeyChainPooled(pathTarget.Target.Offset, mutableShelf.Bytes, profile, encodedKey, incomingIdentity, out bool incomingAdded, out bool alreadyPresent);
        if (alreadyPresent)
        {
            result = new Scalar8VarIdentityRoutedInsertResult(
                Scalar8VarIdentityRoutedInsertKind.NoOp,
                Scalar8VarIdentityInsertResult.AlreadyPresent,
                pathTarget.Target.Offset,
                0,
                default,
                identities.Count,
                profile.ShelfExtentSize);
            return true;
        }

        if (!incomingAdded)
        {
            int insertIndex = LowerBoundTerminalVarIdentity(identities, incomingIdentity, profile.Descending);
            identities.InsertAt(insertIndex, incomingIdentity);
        }

        Span<byte> keyBytes = stackalloc byte[Scalar8VarIdentityLayout.KeySize];
        BinaryPrimitives.WriteUInt64BigEndian(keyBytes, encodedKey);
        int terminalShelfExtentSize = SelectScalar8VarIdentityTerminalShelfExtentSize(profile.ShelfExtentSize, identities);
        long rootOffset = CreateScalar8VarIdentityTerminalRoute(
            TerminalIdentityRootLayout.ShapeScalar8VarIdentity,
            keyBytes,
            terminalShelfExtentSize,
            identities,
            profile.Descending);
        scalar8VarIdentityMutableBatchShelves.Remove(pathTarget.Target.Offset);
        byte[] clearedSourceShelf = Scalar8VarIdentity.CreateEmpty(profile);
        RawDataReservation sourceRewrite = kernel.ReserveAt(pathTarget.Target.Offset, profile.ShelfExtentSize);
        clearedSourceShelf.CopyTo(sourceRewrite.Span);
        long replacementOffset = pathTarget.Target.RouterDepth >= Scalar8VarIdentityLayout.KeySize - 1
            ? rootOffset
            : CreateScalar8VarIdentityTerminalRouterChain(
                checked((ushort)(pathTarget.Target.RouterDepth + 1)),
                pathTarget.Target.AllocationClassId,
                encodedKey,
                pathTarget.Target.Offset,
                rootOffset);
        RepointExactRouteAndAliases(
            pathTarget.ParentRouterOffset,
            pathTarget.Target.Offset,
            pathTarget.RouteIndex,
            replacementOffset,
            pathTarget.Target.Offset);
        DataKernelCommitTelemetry telemetry = CommitAndInvalidateRouterReadCache();
        result = new Scalar8VarIdentityRoutedInsertResult(
            Scalar8VarIdentityRoutedInsertKind.WalkedDuplicateRunOverflow,
            Scalar8VarIdentityInsertResult.Inserted,
            pathTarget.Target.Offset,
            replacementOffset,
            telemetry,
            identities.Count,
            terminalShelfExtentSize);
        return true;
    }

    /// <summary>
    /// Extracts a hot duplicate scalar key from a mixed `SV8` shelf into an exact-key terminal identity route.<br/>
    /// The terminal route stores the scalar key once and stores only identities in compact terminal shelves, while the remaining mixed-key tuples are copied into one ordinary `SV8` remainder shelf.<br/>
    /// This is the pressure path for non-exhausted routes where routing has not yet isolated a duplicate key but repeated key+variable-identity records would otherwise grow the source shelf aggressively.<br/>
    /// </summary>
    /// <param name="pathTarget">The routed path target that reached the mixed source shelf.</param>
    /// <param name="mutableShelf">The mutable source shelf.</param>
    /// <param name="profile">The source shelf profile.</param>
    /// <param name="encodedKey">The duplicate scalar key to extract.</param>
    /// <param name="incomingIdentity">The incoming raw identity for the duplicate key.</param>
    /// <param name="allowDuplicateKeys">Whether duplicate scalar keys are allowed by the owning index.</param>
    /// <param name="result">Receives the routed insert result when extraction is performed.</param>
    /// <returns><see langword="true"/> when the source shelf was converted or the incoming tuple was already present; otherwise <see langword="false"/>.</returns>
    private bool TryExtractScalar8VarIdentityDuplicateKeyToTerminalRoute(
        long rootRouterOffset,
        Scalar8VarIdentityRoutePathTarget pathTarget,
        Scalar8VarIdentityMutableShelfView mutableShelf,
        Scalar8VarIdentityProfile profile,
        ulong encodedKey,
        ReadOnlySpan<byte> incomingIdentity,
        bool allowDuplicateKeys,
        out Scalar8VarIdentityRoutedInsertResult result)
    {
        result = default;
        if (!allowDuplicateKeys || mutableShelf.ItemCount == 0 || Scalar8VarIdentityLayout.ReadNextShelfOffset(mutableShelf.Bytes) != 0)
        {
            return false;
        }

        int matchingCount = 0;
        for (int i = 0; i < mutableShelf.ItemCount; i++)
        {
            if (mutableShelf.ReadKeyAt(i) == encodedKey)
            {
                matchingCount++;
            }
        }

        if (matchingCount < Scalar8VarIdentityTerminalDuplicatePressureItemCount)
        {
            return false;
        }

        if (pathTarget.Target.RouterDepth >= Scalar8VarIdentityLayout.KeySize - 1 &&
            matchingCount != mutableShelf.ItemCount)
        {
            return false;
        }

        byte[][] terminalIdentities = new byte[matchingCount + 1][];
        ulong[] remainderKeys = new ulong[mutableShelf.ItemCount - matchingCount];
        byte[][] remainderIdentities = new byte[remainderKeys.Length][];
        int terminalCount = 0;
        int remainderCount = 0;
        bool incomingAdded = false;
        bool alreadyPresent = false;
        for (int i = 0; i < mutableShelf.ItemCount; i++)
        {
            ulong currentKey = mutableShelf.ReadKeyAt(i);
            ReadOnlySpan<byte> currentIdentity = mutableShelf.ReadIdentityAt(i);
            if (currentKey != encodedKey)
            {
                remainderKeys[remainderCount] = currentKey;
                remainderIdentities[remainderCount] = currentIdentity.ToArray();
                remainderCount++;
                continue;
            }

            if (!incomingAdded && (profile.Descending
                ? Scalar8VarIdentityLayout.CompareIdentityBytes(incomingIdentity, currentIdentity) > 0
                : Scalar8VarIdentityLayout.CompareIdentityBytes(incomingIdentity, currentIdentity) < 0))
            {
                terminalIdentities[terminalCount++] = incomingIdentity.ToArray();
                incomingAdded = true;
            }

            if (Scalar8VarIdentityLayout.IdentityBytesEqual(currentIdentity, incomingIdentity))
            {
                alreadyPresent = true;
            }

            terminalIdentities[terminalCount++] = currentIdentity.ToArray();
        }

        if (alreadyPresent)
        {
            result = new Scalar8VarIdentityRoutedInsertResult(
                Scalar8VarIdentityRoutedInsertKind.NoOp,
                Scalar8VarIdentityInsertResult.AlreadyPresent,
                pathTarget.Target.Offset,
                0,
                default,
                matchingCount,
                profile.ShelfExtentSize);
            return true;
        }

        if (!incomingAdded)
        {
            terminalIdentities[terminalCount++] = incomingIdentity.ToArray();
        }

        if (terminalCount != terminalIdentities.Length)
        {
            Array.Resize(ref terminalIdentities, terminalCount);
        }

        byte[] remainderShelf = Scalar8VarIdentity.CreateEmpty(profile);
        for (int i = 0; i < remainderCount; i++)
        {
            Scalar8VarIdentityInsertResult remainderInsert = Scalar8VarIdentity.Insert(
                remainderShelf,
                profile,
                remainderKeys[i],
                remainderIdentities[i],
                allowDuplicateKeys: true,
                out byte[] rewrittenRemainder);
            if (remainderInsert != Scalar8VarIdentityInsertResult.Inserted)
            {
                return false;
            }

            remainderShelf = rewrittenRemainder;
        }

        Span<byte> keyBytes = stackalloc byte[Scalar8VarIdentityLayout.KeySize];
        BinaryPrimitives.WriteUInt64BigEndian(keyBytes, encodedKey);
        int terminalShelfExtentSize = SelectScalar8VarIdentityTerminalShelfExtentSize(profile.ShelfExtentSize, terminalIdentities);
        long terminalRootOffset = CreateScalar8VarIdentityTerminalRoute(
            TerminalIdentityRootLayout.ShapeScalar8VarIdentity,
            keyBytes,
            terminalShelfExtentSize,
            terminalIdentities,
            profile.Descending);
        RawDataReservation remainderRewrite = kernel.ReserveAt(pathTarget.Target.Offset, profile.ShelfExtentSize);
        remainderShelf.CopyTo(remainderRewrite.Span);
        long replacementOffset = pathTarget.Target.RouterDepth >= Scalar8VarIdentityLayout.KeySize - 1
            ? terminalRootOffset
            : CreateScalar8VarIdentityTerminalRouterChain(
                checked((ushort)(pathTarget.Target.RouterDepth + 1)),
                pathTarget.Target.AllocationClassId,
                encodedKey,
                pathTarget.Target.Offset,
                terminalRootOffset);
        scalar8VarIdentityMutableBatchShelves.Remove(pathTarget.Target.Offset);
        RepointExactRouteAndAliases(
            pathTarget.ParentRouterOffset,
            pathTarget.Target.Offset,
            pathTarget.RouteIndex,
            replacementOffset,
            pathTarget.Target.Offset);
        DataKernelCommitTelemetry telemetry = CommitAndInvalidateRouterReadCache();
        result = new Scalar8VarIdentityRoutedInsertResult(
            Scalar8VarIdentityRoutedInsertKind.WalkedDuplicateRunOverflow,
            Scalar8VarIdentityInsertResult.Inserted,
            pathTarget.Target.Offset,
            replacementOffset,
            telemetry,
            terminalCount,
            terminalShelfExtentSize);
        return true;
    }

    /// <summary>
    /// Creates an exact-key router chain from the current `SV8` route depth to the terminal identity root.<br/>
    /// Every non-matching prefix at each remaining scalar-key byte routes to the cleared source shelf so future distinct keys still have an ordinary mutable shelf to insert into.<br/>
    /// The exact byte path advances until all eight scalar-key bytes have been consumed, then points at the terminal var-identity root for the duplicate key.<br/>
    /// </summary>
    /// <param name="firstDepth">The first scalar-key byte depth below the parent route.</param>
    /// <param name="allocationClassId">The router allocation class id to persist on each created router.</param>
    /// <param name="encodedKey">The scalar key whose exact byte path should reach the terminal root.</param>
    /// <param name="emptyShelfOffset">The cleared source shelf used by all non-matching branches.</param>
    /// <param name="terminalRootOffset">The terminal identity root for the duplicate scalar key.</param>
    /// <returns>The top router offset that should replace the parent route target.</returns>
    private long CreateScalar8VarIdentityTerminalRouterChain(
        ushort firstDepth,
        ushort allocationClassId,
        ulong encodedKey,
        long emptyShelfOffset,
        long terminalRootOffset)
    {
        long nextTargetOffset = terminalRootOffset;
        for (int depth = Scalar8VarIdentityLayout.KeySize - 1; depth >= firstDepth; depth--)
        {
            long[] targets = CreateFilledScalar8Scalar8RouteTargets(emptyShelfOffset);
            targets[GetScalar8VarIdentityPrefix(encodedKey, depth)] = nextTargetOffset;
            RawDataReservation reservation = kernel.Reserve(RouterLayout.Size);
            RouterWriter writer = new(reservation.Span);
            writer.InitializeExpandedOneByte(checked((ushort)depth), allocationClassId, targets);
            nextTargetOffset = reservation.Extent.Offset;
        }

        return nextTargetOffset;
    }

    /// <summary>
    /// Creates a router chain that preserves one existing exact-key terminal target while sending the incoming nonmatching scalar key to an ordinary mutable shelf.<br/>
    /// The chain starts below the parent route that incorrectly reached the terminal root and routes by remaining scalar-key bytes until the two keys diverge.<br/>
    /// Nonmatching branches use the ordinary shelf target so future nearby keys keep a mutable `SV8` landing point instead of entering the exact-key terminal route.<br/>
    /// </summary>
    /// <param name="firstDepth">The first scalar-key byte depth below the parent route.</param>
    /// <param name="allocationClassId">The router allocation class id to persist on each created router.</param>
    /// <param name="terminalKey">The scalar key owned by the existing terminal root.</param>
    /// <param name="terminalTargetOffset">The existing exact-key terminal root offset.</param>
    /// <param name="incomingKey">The incoming scalar key that must not enter the terminal root.</param>
    /// <param name="incomingShelfOffset">The ordinary shelf containing the incoming tuple.</param>
    /// <returns>The top router offset that should replace the parent route target.</returns>
    private long CreateScalar8VarIdentityTerminalMismatchRouterChain(
        ushort firstDepth,
        ushort allocationClassId,
        ulong terminalKey,
        long terminalTargetOffset,
        ulong incomingKey,
        long incomingShelfOffset)
    {
        if (terminalKey == incomingKey || firstDepth >= Scalar8VarIdentityLayout.KeySize)
        {
            throw new InvalidDataException("The SV8 terminal route mismatch cannot be split because the remaining scalar-key route is exhausted.");
        }

        return CreateScalar8VarIdentityTerminalMismatchRouterChainCore(
            firstDepth,
            allocationClassId,
            terminalKey,
            terminalTargetOffset,
            incomingKey,
            incomingShelfOffset);
    }

    /// <summary>
    /// Recursively builds a bounded scalar-key byte router chain for a terminal-root mismatch split.<br/>
    /// Recursion depth is limited to the eight scalar-key bytes and avoids heap queue allocation while keeping the route construction easy to audit.<br/>
    /// </summary>
    /// <param name="depth">The scalar-key byte depth represented by the router being created.</param>
    /// <param name="allocationClassId">The router allocation class id to persist on each created router.</param>
    /// <param name="terminalKey">The scalar key owned by the existing terminal root.</param>
    /// <param name="terminalTargetOffset">The existing exact-key terminal root offset.</param>
    /// <param name="incomingKey">The incoming scalar key routed to the ordinary shelf.</param>
    /// <param name="incomingShelfOffset">The ordinary shelf containing the incoming tuple and serving as fallback.</param>
    /// <returns>The router offset for the requested depth.</returns>
    private long CreateScalar8VarIdentityTerminalMismatchRouterChainCore(
        int depth,
        ushort allocationClassId,
        ulong terminalKey,
        long terminalTargetOffset,
        ulong incomingKey,
        long incomingShelfOffset)
    {
        if (depth >= Scalar8VarIdentityLayout.KeySize)
        {
            throw new InvalidDataException("The SV8 terminal route mismatch reached scalar-key exhaustion without finding a differing byte.");
        }

        byte terminalPrefix = GetScalar8VarIdentityPrefix(terminalKey, depth);
        byte incomingPrefix = GetScalar8VarIdentityPrefix(incomingKey, depth);
        long terminalBranchTarget = terminalTargetOffset;
        if (terminalPrefix == incomingPrefix)
        {
            terminalBranchTarget = CreateScalar8VarIdentityTerminalMismatchRouterChainCore(
                depth + 1,
                allocationClassId,
                terminalKey,
                terminalTargetOffset,
                incomingKey,
                incomingShelfOffset);
        }

        long[] targets = CreateFilledScalar8Scalar8RouteTargets(incomingShelfOffset);
        targets[terminalPrefix] = terminalBranchTarget;
        if (terminalPrefix != incomingPrefix)
        {
            targets[incomingPrefix] = incomingShelfOffset;
        }

        RawDataReservation reservation = kernel.Reserve(RouterLayout.Size);
        RouterWriter writer = new(reservation.Span);
        writer.InitializeExpandedOneByte(checked((ushort)depth), allocationClassId, targets);
        return reservation.Extent.Offset;
    }

    private long CreateScalar8VarIdentityTerminalRoute(byte shape, ReadOnlySpan<byte> keyBytes, int shelfExtentSize, ReadOnlySpan<byte[]> identities, bool descending = false)
    {
        long firstShelfOffset = 0;
        long previousShelfOffset = 0;
        long tailShelfOffset = 0;
        RawDataReservation previousReservation = default;
        int index = 0;
        while (index < identities.Length)
        {
            int take = TerminalVarIdentityShelfLayout.GetChunkCount(identities, index, identities.Length - index, shelfExtentSize);
            if (take <= 0 || !TerminalVarIdentityShelfLayout.TryBuild(identities, index, take, shelfExtentSize, out byte[] shelfBytes))
            {
                throw new InvalidDataException("The SV8 terminal var identity route could not build a fitting identity shelf.");
            }

            RawDataReservation shelfReservation = kernel.Reserve(shelfExtentSize);
            shelfBytes.CopyTo(shelfReservation.Span);
            if (firstShelfOffset == 0)
            {
                firstShelfOffset = shelfReservation.Extent.Offset;
            }

            if (previousShelfOffset != 0)
            {
                RawDataReservation previousRewrite = kernel.ReserveAt(previousShelfOffset, shelfExtentSize);
                previousReservation.Span.Slice(0, shelfExtentSize).CopyTo(previousRewrite.Span);
                TerminalVarIdentityShelfLayout.WriteNextShelfOffset(previousRewrite.Span, shelfReservation.Extent.Offset);
            }

            previousShelfOffset = shelfReservation.Extent.Offset;
            tailShelfOffset = shelfReservation.Extent.Offset;
            previousReservation = shelfReservation;
            index += take;
        }

        RawDataReservation rootReservation = kernel.Reserve(TerminalIdentityRootLayout.Size);
        TerminalIdentityRootLayout.Initialize(rootReservation.Span, shape, keyBytes, shelfExtentSize, firstShelfOffset);
        rootReservation.Span[TerminalIdentityRootLayout.SortDirectionOffset] = descending ? (byte)1 : (byte)0;
        TerminalIdentityRootLayout.WriteTailShelfOffset(rootReservation.Span, tailShelfOffset);
        return rootReservation.Extent.Offset;
    }

    /// <summary>
    /// Creates a terminal `SV8` var-identity route from a pooled flat identity workspace.<br/>
    /// The method writes terminal shelf bytes directly into DataKernel reservations, avoiding per-shelf `byte[]` materialization while preserving the existing terminal root format.<br/>
    /// </summary>
    /// <param name="shape">The terminal root shape value to persist.<br/></param>
    /// <param name="keyBytes">The encoded scalar key bytes stored once in the terminal root.<br/></param>
    /// <param name="shelfExtentSize">The terminal shelf extent size.<br/></param>
    /// <param name="identities">The pooled byte-native identity workspace in sorted identity order.<br/></param>
    /// <returns>The terminal root offset.</returns>
    private long CreateScalar8VarIdentityTerminalRoute(byte shape, ReadOnlySpan<byte> keyBytes, int shelfExtentSize, PooledTerminalVarIdentitySet identities, bool descending = false)
    {
        long firstShelfOffset = 0;
        long previousShelfOffset = 0;
        long tailShelfOffset = 0;
        RawDataReservation previousReservation = default;
        int index = 0;
        while (index < identities.Count)
        {
            int take = GetTerminalVarIdentityChunkCount(identities, index, identities.Count - index, shelfExtentSize);
            if (take <= 0)
            {
                throw new InvalidDataException("The SV8 terminal var identity route could not build a fitting identity shelf.");
            }

            RawDataReservation shelfReservation = kernel.Reserve(shelfExtentSize);
            BuildTerminalVarIdentityShelf(shelfReservation.Span, identities, index, take, shelfExtentSize);
            if (firstShelfOffset == 0)
            {
                firstShelfOffset = shelfReservation.Extent.Offset;
            }

            if (previousShelfOffset != 0)
            {
                RawDataReservation previousRewrite = kernel.ReserveAt(previousShelfOffset, shelfExtentSize);
                previousReservation.Span.Slice(0, shelfExtentSize).CopyTo(previousRewrite.Span);
                TerminalVarIdentityShelfLayout.WriteNextShelfOffset(previousRewrite.Span, shelfReservation.Extent.Offset);
            }

            previousShelfOffset = shelfReservation.Extent.Offset;
            tailShelfOffset = shelfReservation.Extent.Offset;
            previousReservation = shelfReservation;
            index += take;
        }

        RawDataReservation rootReservation = kernel.Reserve(TerminalIdentityRootLayout.Size);
        TerminalIdentityRootLayout.Initialize(rootReservation.Span, shape, keyBytes, shelfExtentSize, firstShelfOffset);
        rootReservation.Span[TerminalIdentityRootLayout.SortDirectionOffset] = descending ? (byte)1 : (byte)0;
        TerminalIdentityRootLayout.WriteTailShelfOffset(rootReservation.Span, tailShelfOffset);
        return rootReservation.Extent.Offset;
    }

    private DataKernelCommitTelemetry RewriteScalar8VarIdentityTerminalRoute(long rootOffset, byte shape, ReadOnlySpan<byte> keyBytes, int shelfExtentSize, ReadOnlySpan<byte[]> identities)
    {
        byte sortDirection = ReadTerminalIdentityRootBytes(rootOffset)[TerminalIdentityRootLayout.SortDirectionOffset];
        long oldFirstShelfOffset = ReadTerminalIdentityFirstShelfOffset(rootOffset, keyBytes);
        long firstShelfOffset = 0;
        long previousShelfOffset = 0;
        long tailShelfOffset = 0;
        RawDataReservation previousReservation = default;
        int index = 0;
        while (index < identities.Length)
        {
            int take = TerminalVarIdentityShelfLayout.GetChunkCount(identities, index, identities.Length - index, shelfExtentSize);
            if (take <= 0 || !TerminalVarIdentityShelfLayout.TryBuild(identities, index, take, shelfExtentSize, out byte[] shelfBytes))
            {
                throw new InvalidDataException("The SV8 terminal var identity route could not rebuild a fitting identity shelf.");
            }

            RawDataReservation shelfReservation = kernel.Reserve(shelfExtentSize);
            shelfBytes.CopyTo(shelfReservation.Span);
            if (firstShelfOffset == 0)
            {
                firstShelfOffset = shelfReservation.Extent.Offset;
            }

            if (previousShelfOffset != 0)
            {
                RawDataReservation previousRewrite = kernel.ReserveAt(previousShelfOffset, shelfExtentSize);
                previousReservation.Span.Slice(0, shelfExtentSize).CopyTo(previousRewrite.Span);
                TerminalVarIdentityShelfLayout.WriteNextShelfOffset(previousRewrite.Span, shelfReservation.Extent.Offset);
            }

            previousShelfOffset = shelfReservation.Extent.Offset;
            tailShelfOffset = shelfReservation.Extent.Offset;
            previousReservation = shelfReservation;
            index += take;
        }

        RawDataReservation rootRewrite = kernel.ReserveAt(rootOffset, TerminalIdentityRootLayout.Size);
        TerminalIdentityRootLayout.Initialize(rootRewrite.Span, shape, keyBytes, shelfExtentSize, firstShelfOffset);
        rootRewrite.Span[TerminalIdentityRootLayout.SortDirectionOffset] = sortDirection;
        TerminalIdentityRootLayout.WriteTailShelfOffset(rootRewrite.Span, tailShelfOffset);
        ClearTerminalIdentityReadCaches();
        ReleaseTerminalVarIdentityShelfChain(oldFirstShelfOffset, shelfExtentSize);
        DataKernelCommitTelemetry telemetry = CommitAndInvalidateRouterReadCache();
        return telemetry;
    }

    /// <summary>
    /// Rewrites a terminal `SV8` var-identity route from a pooled flat identity workspace.<br/>
    /// The method keeps identity data as byte slices until each terminal shelf is written into its final reservation, then stages the superseded shelf chain for retirement after root publication in the same commit.<br/>
    /// </summary>
    /// <param name="rootOffset">The terminal identity root offset to rewrite.<br/></param>
    /// <param name="shape">The terminal root shape value to persist.<br/></param>
    /// <param name="keyBytes">The encoded scalar key bytes expected in the terminal root.<br/></param>
    /// <param name="shelfExtentSize">The terminal shelf extent size.<br/></param>
    /// <param name="identities">The pooled byte-native identity workspace in sorted identity order.<br/></param>
    /// <returns>Commit telemetry for the route rewrite.</returns>
    private DataKernelCommitTelemetry RewriteScalar8VarIdentityTerminalRoute(long rootOffset, byte shape, ReadOnlySpan<byte> keyBytes, int shelfExtentSize, PooledTerminalVarIdentitySet identities)
    {
        byte sortDirection = ReadTerminalIdentityRootBytes(rootOffset)[TerminalIdentityRootLayout.SortDirectionOffset];
        long oldFirstShelfOffset = ReadTerminalIdentityFirstShelfOffset(rootOffset, keyBytes);
        long firstShelfOffset = 0;
        long previousShelfOffset = 0;
        long tailShelfOffset = 0;
        RawDataReservation previousReservation = default;
        int index = 0;
        while (index < identities.Count)
        {
            int take = GetTerminalVarIdentityChunkCount(identities, index, identities.Count - index, shelfExtentSize);
            if (take <= 0)
            {
                throw new InvalidDataException("The SV8 terminal var identity route could not rebuild a fitting identity shelf.");
            }

            RawDataReservation shelfReservation = kernel.Reserve(shelfExtentSize);
            BuildTerminalVarIdentityShelf(shelfReservation.Span, identities, index, take, shelfExtentSize);
            if (firstShelfOffset == 0)
            {
                firstShelfOffset = shelfReservation.Extent.Offset;
            }

            if (previousShelfOffset != 0)
            {
                RawDataReservation previousRewrite = kernel.ReserveAt(previousShelfOffset, shelfExtentSize);
                previousReservation.Span.Slice(0, shelfExtentSize).CopyTo(previousRewrite.Span);
                TerminalVarIdentityShelfLayout.WriteNextShelfOffset(previousRewrite.Span, shelfReservation.Extent.Offset);
            }

            previousShelfOffset = shelfReservation.Extent.Offset;
            tailShelfOffset = shelfReservation.Extent.Offset;
            previousReservation = shelfReservation;
            index += take;
        }

        RawDataReservation rootRewrite = kernel.ReserveAt(rootOffset, TerminalIdentityRootLayout.Size);
        TerminalIdentityRootLayout.Initialize(rootRewrite.Span, shape, keyBytes, shelfExtentSize, firstShelfOffset);
        rootRewrite.Span[TerminalIdentityRootLayout.SortDirectionOffset] = sortDirection;
        TerminalIdentityRootLayout.WriteTailShelfOffset(rootRewrite.Span, tailShelfOffset);
        ClearTerminalIdentityReadCaches();
        ReleaseTerminalVarIdentityShelfChain(oldFirstShelfOffset, shelfExtentSize);
        DataKernelCommitTelemetry telemetry = CommitAndInvalidateRouterReadCache();
        return telemetry;
    }

    /// <summary>
    /// Stages release of a superseded terminal variable-identity shelf chain through the backing-specific allocator.<br/>
    /// Callers stage the root rewrite first and invoke this helper before the enclosing commit so file retirement is ordered after topology publication while memory arenas recover equivalent capacity safely.<br/>
    /// </summary>
    /// <param name="firstShelfOffset">The first terminal variable-identity shelf in the old chain, or zero when the old root had no shelves.<br/></param>
    /// <param name="shelfExtentSize">The fixed extent size of each terminal variable-identity shelf.<br/></param>
    private void ReleaseTerminalVarIdentityShelfChain(long firstShelfOffset, int shelfExtentSize)
    {
        long currentOffset = firstShelfOffset;
        while (currentOffset != 0)
        {
            byte[] shelfBytes = ReadTerminalVarIdentityShelfBytesCached(currentOffset, shelfExtentSize);
            long nextOffset = TerminalVarIdentityShelfLayout.ReadNextShelfOffset(shelfBytes);
            terminalVarIdentityShelfReadCache.TryRemove(currentOffset, out _);
            kernel.StageExtentRetirement(currentOffset, shelfExtentSize);
            currentOffset = nextOffset;
        }
    }

    private bool TryAppendScalar8VarIdentityTerminalTail(long rootOffset, byte[] rootBytes, long firstShelfOffset, long tailShelfOffset, int shelfExtentSize, ReadOnlySpan<byte> identity, out DataKernelCommitTelemetry telemetry)
        => TryAppendScalar8VarIdentityTerminalTail(rootOffset, rootBytes, firstShelfOffset, tailShelfOffset, shelfExtentSize, identity, out telemetry, out _);

    private bool TryAppendScalar8VarIdentityTerminalTail(long rootOffset, byte[] rootBytes, long firstShelfOffset, long tailShelfOffset, int shelfExtentSize, ReadOnlySpan<byte> identity, out DataKernelCommitTelemetry telemetry, out Scalar8VarIdentityInsertDiagnosticPath diagnosticPath)
    {
        telemetry = default;
        diagnosticPath = Scalar8VarIdentityInsertDiagnosticPath.None;
        if (firstShelfOffset == 0)
        {
            byte[] shelfBytes = new byte[shelfExtentSize];
            if (!BuildSingleTerminalVarIdentityShelf(shelfBytes, shelfExtentSize, identity))
            {
                return false;
            }

            RawDataReservation shelfReservation = kernel.Reserve(shelfExtentSize);
            shelfBytes.CopyTo(shelfReservation.Span);
            terminalVarIdentityShelfReadCache[shelfReservation.Extent.Offset] = shelfBytes;
            if (kernel.TryGetMemoryWritableSpanDirect(rootOffset, TerminalIdentityRootLayout.Size, out Span<byte> rootSpan))
            {
                TerminalIdentityRootLayout.WriteFirstShelfOffset(rootSpan, shelfReservation.Extent.Offset);
                TerminalIdentityRootLayout.WriteTailShelfOffset(rootSpan, shelfReservation.Extent.Offset);
            }
            else
            {
                RawDataReservation rootRewrite = kernel.ReserveAt(rootOffset, TerminalIdentityRootLayout.Size);
                rootBytes.CopyTo(rootRewrite.Span);
                TerminalIdentityRootLayout.WriteFirstShelfOffset(rootRewrite.Span, shelfReservation.Extent.Offset);
                TerminalIdentityRootLayout.WriteTailShelfOffset(rootRewrite.Span, shelfReservation.Extent.Offset);
            }

            TerminalIdentityRootLayout.WriteFirstShelfOffset(rootBytes, shelfReservation.Extent.Offset);
            TerminalIdentityRootLayout.WriteTailShelfOffset(rootBytes, shelfReservation.Extent.Offset);
            terminalIdentityRootReadCache[rootOffset] = rootBytes;
            telemetry = CommitTerminalVarIdentityMutationIfNeeded(directMemory: false);
            diagnosticPath = Scalar8VarIdentityInsertDiagnosticPath.TerminalTailNewShelf;
            return true;
        }

        long currentOffset = tailShelfOffset == 0 ? firstShelfOffset : tailShelfOffset;
        while (currentOffset != 0)
        {
            byte[] currentBytes = ReadTerminalVarIdentityShelfBytesCached(currentOffset, shelfExtentSize);
            TerminalVarIdentityShelfLayout.Validate(currentBytes, shelfExtentSize);
            long nextOffset = TerminalVarIdentityShelfLayout.ReadNextShelfOffset(currentBytes);
            if (nextOffset != 0)
            {
                currentOffset = nextOffset;
                continue;
            }

            int count = TerminalVarIdentityShelfLayout.ReadItemCount(currentBytes);
            if (count != 0 &&
                (rootBytes[TerminalIdentityRootLayout.SortDirectionOffset] != 0
                    ? Scalar8VarIdentityLayout.CompareIdentityBytes(identity, TerminalVarIdentityShelfLayout.ReadIdentityAt(currentBytes, count - 1)) >= 0
                    : Scalar8VarIdentityLayout.CompareIdentityBytes(identity, TerminalVarIdentityShelfLayout.ReadIdentityAt(currentBytes, count - 1)) <= 0))
            {
                return false;
            }

            int recordLength = TerminalVarIdentityShelfLayout.GetNewRecordLength(identity.Length);
            int slotStreamLength = TerminalVarIdentityShelfLayout.ReadSlotStreamLength(currentBytes);
            int slotCapacityBytes = TerminalVarIdentityShelfLayout.ReadSlotCapacityBytes(currentBytes);
            int recordArenaEnd = TerminalVarIdentityShelfLayout.ReadRecordArenaEnd(currentBytes);
            if (slotStreamLength + TerminalVarIdentityShelfLayout.SlotSize <= slotCapacityBytes &&
                recordArenaEnd + recordLength <= shelfExtentSize)
            {
                Span<byte> targetSpan;
                bool directMemory = kernel.TryGetMemoryWritableSpanDirect(currentOffset, shelfExtentSize, out targetSpan);
                if (!directMemory && durabilityBatchActive)
                {
                    targetSpan = currentBytes.AsSpan(0, shelfExtentSize);
                    MarkDirtyTerminalVarIdentityShelf(currentOffset, currentBytes);
                }
                else if (!directMemory)
                {
                    RawDataReservation rewrite = kernel.ReserveAt(currentOffset, shelfExtentSize);
                    currentBytes.CopyTo(rewrite.Span);
                    targetSpan = rewrite.Span;
                }

                int slotOffset = TerminalVarIdentityShelfLayout.HeaderSize + slotStreamLength;
                TerminalVarIdentityShelfLayout.WriteRecord(targetSpan, recordArenaEnd, identity);
                TerminalVarIdentityShelfLayout.WriteSlotRecordOffset(targetSpan, slotOffset, recordArenaEnd);
                TerminalVarIdentityShelfLayout.WriteItemCount(targetSpan, count + 1);
                TerminalVarIdentityShelfLayout.WriteSlotStreamLength(targetSpan, slotStreamLength + TerminalVarIdentityShelfLayout.SlotSize);
                TerminalVarIdentityShelfLayout.WriteRecordArenaEnd(targetSpan, recordArenaEnd + recordLength);
                TerminalVarIdentityShelfLayout.WriteRecord(currentBytes, recordArenaEnd, identity);
                TerminalVarIdentityShelfLayout.WriteSlotRecordOffset(currentBytes, slotOffset, recordArenaEnd);
                TerminalVarIdentityShelfLayout.WriteItemCount(currentBytes, count + 1);
                TerminalVarIdentityShelfLayout.WriteSlotStreamLength(currentBytes, slotStreamLength + TerminalVarIdentityShelfLayout.SlotSize);
                TerminalVarIdentityShelfLayout.WriteRecordArenaEnd(currentBytes, recordArenaEnd + recordLength);
                terminalVarIdentityShelfReadCache[currentOffset] = currentBytes;
                telemetry = CommitTerminalVarIdentityMutationIfNeeded(directMemory);
                diagnosticPath = Scalar8VarIdentityInsertDiagnosticPath.TerminalTailInPlace;
                return true;
            }

            byte[] appendedBytes = new byte[shelfExtentSize];
            if (!BuildSingleTerminalVarIdentityShelf(appendedBytes, shelfExtentSize, identity))
            {
                return false;
            }

            RawDataReservation append = kernel.Reserve(shelfExtentSize);
            appendedBytes.CopyTo(append.Span);
            terminalVarIdentityShelfReadCache[append.Extent.Offset] = appendedBytes;
            if (kernel.TryGetMemoryWritableSpanDirect(currentOffset, shelfExtentSize, out Span<byte> tailSpan))
            {
                TerminalVarIdentityShelfLayout.WriteNextShelfOffset(tailSpan, append.Extent.Offset);
            }
            else if (durabilityBatchActive)
            {
                RawDataReservation tailRewrite = kernel.ReserveAt(currentOffset, shelfExtentSize);
                currentBytes.CopyTo(tailRewrite.Span);
                TerminalVarIdentityShelfLayout.WriteNextShelfOffset(tailRewrite.Span, append.Extent.Offset);
            }
            else
            {
                RawDataReservation tailRewrite = kernel.ReserveAt(currentOffset, shelfExtentSize);
                currentBytes.CopyTo(tailRewrite.Span);
                TerminalVarIdentityShelfLayout.WriteNextShelfOffset(tailRewrite.Span, append.Extent.Offset);
            }

            TerminalVarIdentityShelfLayout.WriteNextShelfOffset(currentBytes, append.Extent.Offset);
            terminalVarIdentityShelfReadCache[currentOffset] = currentBytes;
            if (kernel.TryGetMemoryWritableSpanDirect(rootOffset, TerminalIdentityRootLayout.Size, out Span<byte> appendedRootSpan))
            {
                TerminalIdentityRootLayout.WriteTailShelfOffset(appendedRootSpan, append.Extent.Offset);
            }
            else
            {
                RawDataReservation rootRewrite = kernel.ReserveAt(rootOffset, TerminalIdentityRootLayout.Size);
                rootBytes.CopyTo(rootRewrite.Span);
                TerminalIdentityRootLayout.WriteTailShelfOffset(rootRewrite.Span, append.Extent.Offset);
            }

            TerminalIdentityRootLayout.WriteTailShelfOffset(rootBytes, append.Extent.Offset);
            terminalIdentityRootReadCache[rootOffset] = rootBytes;
            telemetry = CommitTerminalVarIdentityMutationIfNeeded(directMemory: false);
            diagnosticPath = Scalar8VarIdentityInsertDiagnosticPath.TerminalTailNewShelf;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Publishes terminal variable-identity mutations only when no outer durability batch can publish them later.<br/>
    /// File-backed single-write callers still get immediate visibility, while group-batched index-major loads keep pending rewrites coalesced until the batch commit boundary.<br/>
    /// Memory-backed catalogs and direct-memory writes need no durability publication.<br/>
    /// </summary>
    /// <param name="directMemory">Whether the caller wrote directly into the volatile memory arena.<br/></param>
    /// <returns>Commit telemetry for standalone file-backed writes, or default telemetry when publication is deferred or unnecessary.<br/></returns>
    private DataKernelCommitTelemetry CommitTerminalVarIdentityMutationIfNeeded(bool directMemory)
    {
        if (directMemory ||
            durabilityBatchActive ||
            kernel.BackingKind == DataKernelBackingKind.Memory)
        {
            return default;
        }

        return CommitWithoutInvalidatingRouterReadCache();
    }

    /// <summary>
    /// Marks one terminal variable-identity shelf image dirty for the active durability batch.<br/>
    /// Terminal duplicate-key append paths mutate cached shelf bytes repeatedly; recording the latest image lets batch commit publish one fixed-offset shelf rewrite instead of allocating one staged full-shelf rewrite per inserted identity.<br/>
    /// </summary>
    /// <param name="shelfOffset">The offset of the terminal variable-identity shelf.<br/></param>
    /// <param name="shelfBytes">The latest cached shelf byte image.<br/></param>
    private void MarkDirtyTerminalVarIdentityShelf(long shelfOffset, byte[] shelfBytes)
    {
        if (!durabilityBatchActive)
        {
            return;
        }

        terminalVarIdentityMutableBatchShelfBytes[shelfOffset] = shelfBytes;
    }

    /// <summary>
    /// Flushes dirty terminal variable-identity shelf images into DataKernel fixed-offset reservations at the durability batch boundary.<br/>
    /// The method copies only the persisted shelf extent recorded in each shelf header, keeping the terminal read cache as the authoritative mutable image during the batch.<br/>
    /// </summary>
    private void FlushDirtyTerminalVarIdentityMutableBatchShelves()
    {
        foreach (KeyValuePair<long, byte[]> dirtyShelf in terminalVarIdentityMutableBatchShelfBytes)
        {
            int shelfExtentSize = TerminalVarIdentityShelfLayout.ReadShelfExtentSize(dirtyShelf.Value);
            RawDataReservation rewrite = kernel.ReserveAt(dirtyShelf.Key, shelfExtentSize);
            dirtyShelf.Value.AsSpan(0, shelfExtentSize).CopyTo(rewrite.Span);
        }
    }

    /// <summary>
    /// Inserts one identity into an existing terminal var-identity shelf chain without rebuilding the whole chain.<br/>
    /// The method walks sorted terminal shelves, rejects duplicate identities, mutates the target shelf when it has room, or splits only that full shelf and links one replacement sibling.<br/>
    /// </summary>
    /// <param name="rootOffset">The terminal identity root offset, used only when a tail split must update the root tail pointer.<br/></param>
    /// <param name="rootBytes">The cached terminal identity root bytes.<br/></param>
    /// <param name="firstShelfOffset">The first terminal var-identity shelf offset.<br/></param>
    /// <param name="shelfExtentSize">The terminal shelf extent size.<br/></param>
    /// <param name="identity">The incoming identity bytes.<br/></param>
    /// <param name="result">Receives the routed insert result when the chain handled the insert or duplicate no-op.<br/></param>
    /// <returns><see langword="true"/> when the insert/no-op completed locally; otherwise <see langword="false"/> so the caller can use full-chain rewrite.</returns>
    private bool TryInsertIntoScalar8VarIdentityTerminalChain(
        long rootOffset,
        byte[] rootBytes,
        long firstShelfOffset,
        int shelfExtentSize,
        ReadOnlySpan<byte> identity,
        out Scalar8VarIdentityRoutedInsertResult result)
    {
        result = default;
        long currentOffset = firstShelfOffset;
        while (currentOffset != 0)
        {
            byte[] currentBytes = ReadTerminalVarIdentityShelfBytesCached(currentOffset, shelfExtentSize);
            TerminalVarIdentityShelfLayout.Validate(currentBytes, shelfExtentSize);
            int count = TerminalVarIdentityShelfLayout.ReadItemCount(currentBytes);
            int insertIndex = count;
            for (int i = 0; i < count; i++)
            {
                int order = Scalar8VarIdentityLayout.CompareIdentityBytes(identity, TerminalVarIdentityShelfLayout.ReadIdentityAt(currentBytes, i));
                if (rootBytes[TerminalIdentityRootLayout.SortDirectionOffset] != 0)
                    order = -order;
                if (order == 0)
                {
                    result = new Scalar8VarIdentityRoutedInsertResult(
                        Scalar8VarIdentityRoutedInsertKind.NoOp,
                        Scalar8VarIdentityInsertResult.AlreadyPresent,
                        currentOffset,
                        0,
                        default,
                        count,
                        shelfExtentSize);
                    return true;
                }

                if (order < 0)
                {
                    insertIndex = i;
                    break;
                }
            }

            long nextOffset = TerminalVarIdentityShelfLayout.ReadNextShelfOffset(currentBytes);
            if (insertIndex == count && nextOffset != 0)
            {
                currentOffset = nextOffset;
                continue;
            }

            int recordLength = TerminalVarIdentityShelfLayout.GetNewRecordLength(identity.Length);
            int slotStreamLength = TerminalVarIdentityShelfLayout.ReadSlotStreamLength(currentBytes);
            int slotCapacityBytes = TerminalVarIdentityShelfLayout.ReadSlotCapacityBytes(currentBytes);
            int recordArenaEnd = TerminalVarIdentityShelfLayout.ReadRecordArenaEnd(currentBytes);
            if (slotStreamLength + TerminalVarIdentityShelfLayout.SlotSize > slotCapacityBytes ||
                recordArenaEnd + recordLength > shelfExtentSize)
            {
                using PooledTerminalVarIdentitySet splitIdentities = PooledTerminalVarIdentitySet.Rent(count + 1, shelfExtentSize + identity.Length);
                for (int i = 0; i < count; i++)
                {
                    if (i == insertIndex)
                    {
                        splitIdentities.Add(identity);
                    }

                    splitIdentities.Add(TerminalVarIdentityShelfLayout.ReadIdentityAt(currentBytes, i));
                }

                if (insertIndex == count)
                {
                    splitIdentities.Add(identity);
                }

                int leftCount = Math.Max(1, splitIdentities.Count / 2);
                int rightCount = splitIdentities.Count - leftCount;
                RawDataReservation append = kernel.Reserve(shelfExtentSize);
                BuildTerminalVarIdentityShelf(append.Span, splitIdentities, leftCount, rightCount, shelfExtentSize);
                TerminalVarIdentityShelfLayout.WriteNextShelfOffset(append.Span, nextOffset);
                byte[] appendedBytes = new byte[shelfExtentSize];
                append.Span.Slice(0, shelfExtentSize).CopyTo(appendedBytes);

                Span<byte> splitTargetSpan;
                bool splitDirectMemory = kernel.TryGetMemoryWritableSpanDirect(currentOffset, shelfExtentSize, out splitTargetSpan);
                if (!splitDirectMemory && durabilityBatchActive)
                {
                    RawDataReservation rewrite = kernel.ReserveAt(currentOffset, shelfExtentSize);
                    splitTargetSpan = rewrite.Span;
                }
                else if (!splitDirectMemory)
                {
                    RawDataReservation rewrite = kernel.ReserveAt(currentOffset, shelfExtentSize);
                    splitTargetSpan = rewrite.Span;
                }

                BuildTerminalVarIdentityShelf(splitTargetSpan, splitIdentities, 0, leftCount, shelfExtentSize);
                TerminalVarIdentityShelfLayout.WriteNextShelfOffset(splitTargetSpan, append.Extent.Offset);
                BuildTerminalVarIdentityShelf(currentBytes, splitIdentities, 0, leftCount, shelfExtentSize);
                TerminalVarIdentityShelfLayout.WriteNextShelfOffset(currentBytes, append.Extent.Offset);
                terminalVarIdentityShelfReadCache[currentOffset] = currentBytes;
                terminalVarIdentityShelfReadCache[append.Extent.Offset] = appendedBytes;
                if (nextOffset == 0)
                {
                    if (kernel.TryGetMemoryWritableSpanDirect(rootOffset, TerminalIdentityRootLayout.Size, out Span<byte> rootSpan))
                    {
                        TerminalIdentityRootLayout.WriteTailShelfOffset(rootSpan, append.Extent.Offset);
                    }
                    else
                    {
                        RawDataReservation rootRewrite = kernel.ReserveAt(rootOffset, TerminalIdentityRootLayout.Size);
                        rootBytes.CopyTo(rootRewrite.Span);
                        TerminalIdentityRootLayout.WriteTailShelfOffset(rootRewrite.Span, append.Extent.Offset);
                    }

                    TerminalIdentityRootLayout.WriteTailShelfOffset(rootBytes, append.Extent.Offset);
                    terminalIdentityRootReadCache[rootOffset] = rootBytes;
                }

                DataKernelCommitTelemetry splitTelemetry = CommitTerminalVarIdentityMutationIfNeeded(splitDirectMemory);
                result = new Scalar8VarIdentityRoutedInsertResult(
                    Scalar8VarIdentityRoutedInsertKind.WalkedDuplicateRunOverflow,
                    Scalar8VarIdentityInsertResult.Inserted,
                    currentOffset,
                    append.Extent.Offset,
                    splitTelemetry,
                    splitIdentities.Count,
                    shelfExtentSize);
                return true;
            }

            Span<byte> targetSpan;
            bool directMemory = kernel.TryGetMemoryWritableSpanDirect(currentOffset, shelfExtentSize, out targetSpan);
            bool targetIsCurrentBytes = false;
            if (!directMemory && durabilityBatchActive)
            {
                targetSpan = currentBytes.AsSpan(0, shelfExtentSize);
                targetIsCurrentBytes = true;
                MarkDirtyTerminalVarIdentityShelf(currentOffset, currentBytes);
            }
            else if (!directMemory)
            {
                RawDataReservation rewrite = kernel.ReserveAt(currentOffset, shelfExtentSize);
                currentBytes.CopyTo(rewrite.Span);
                targetSpan = rewrite.Span;
            }

            InsertIntoTerminalVarIdentityShelf(targetSpan, insertIndex, identity);
            if (!targetIsCurrentBytes)
            {
                InsertIntoTerminalVarIdentityShelf(currentBytes, insertIndex, identity);
            }

            terminalVarIdentityShelfReadCache[currentOffset] = currentBytes;
            DataKernelCommitTelemetry telemetry = CommitTerminalVarIdentityMutationIfNeeded(directMemory);
            result = new Scalar8VarIdentityRoutedInsertResult(
                Scalar8VarIdentityRoutedInsertKind.WalkedDuplicateRunOverflow,
                Scalar8VarIdentityInsertResult.Inserted,
                currentOffset,
                currentOffset,
                telemetry,
                count + 1,
                shelfExtentSize);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Deletes one raw identity from an `SV8` terminal var-identity route through the shared shelf-local terminal mutation path.<br/>
    /// Terminal routes store the scalar key once in the root, so exact tuple deletion only compares the raw identity payload after validating the root key.<br/>
    /// </summary>
    /// <param name="rootOffset">The terminal identity root offset reached by the routed exact-key walk.</param>
    /// <param name="encodedKey">The exact scalar key expected in the terminal root.</param>
    /// <param name="identity">The raw identity bytes to delete.</param>
    /// <returns><see langword="true"/> when the identity existed and was removed.</returns>
    private bool DeleteScalar8VarIdentityTerminalExactTuple(long rootOffset, ulong encodedKey, ReadOnlySpan<byte> identity)
    {
        Span<byte> keyBytes = stackalloc byte[Scalar8VarIdentityLayout.KeySize];
        BinaryPrimitives.WriteUInt64BigEndian(keyBytes, encodedKey);
        byte[] rootBytes = ReadTerminalIdentityRootBytes(rootOffset);
        if (!IsTerminalIdentityRootForKey(rootBytes, keyBytes, out _))
        {
            throw new InvalidDataException("The routed SV8 terminal var identity root does not match the delete key.");
        }

        return DeleteTerminalVarIdentityExactTupleLocally(
            rootOffset,
            TerminalIdentityRootLayout.ShapeScalar8VarIdentity,
            keyBytes,
            identity);
    }

    private byte[][] ReadScalar8VarIdentityTerminalIdentities(long rootOffset, ReadOnlySpan<byte> expectedKey, int shelfExtentSize)
    {
        byte[] rootBytes = ReadTerminalIdentityRootBytes(rootOffset);
        if (!IsTerminalIdentityRootForKey(rootBytes, expectedKey, out long shelfOffset))
        {
            throw new InvalidDataException("The SV8 terminal var identity root does not match the expected key.");
        }

        byte[][] identities = new byte[256][];
        int count = 0;
        while (shelfOffset != 0)
        {
            byte[] shelfBytes = ReadTerminalVarIdentityShelfBytesCached(shelfOffset, shelfExtentSize);
            TerminalVarIdentityShelfLayout.Validate(shelfBytes, shelfExtentSize);
            int itemCount = TerminalVarIdentityShelfLayout.ReadItemCount(shelfBytes);
            for (int i = 0; i < itemCount; i++)
            {
                if (count == identities.Length)
                {
                    Array.Resize(ref identities, checked(identities.Length * 2));
                }

                identities[count++] = TerminalVarIdentityShelfLayout.ReadIdentityAt(shelfBytes, i).ToArray();
            }

            shelfOffset = TerminalVarIdentityShelfLayout.ReadNextShelfOffset(shelfBytes);
        }

        Array.Resize(ref identities, count);
        return identities;
    }

    /// <summary>
    /// Reads terminal `SV8` var identities into a pooled flat byte workspace.<br/>
    /// This keeps terminal route rewrite input as byte slices instead of allocating one `byte[]` object per identity.<br/>
    /// </summary>
    /// <param name="rootOffset">The terminal identity root offset to read.<br/></param>
    /// <param name="expectedKey">The encoded scalar key bytes expected in the terminal root.<br/></param>
    /// <param name="shelfExtentSize">The terminal shelf extent size.<br/></param>
    /// <returns>A pooled identity workspace owned by the caller.</returns>
    private PooledTerminalVarIdentitySet ReadScalar8VarIdentityTerminalIdentitiesPooled(long rootOffset, ReadOnlySpan<byte> expectedKey, int shelfExtentSize)
    {
        byte[] rootBytes = ReadTerminalIdentityRootBytes(rootOffset);
        if (!IsTerminalIdentityRootForKey(rootBytes, expectedKey, out long shelfOffset))
        {
            throw new InvalidDataException("The SV8 terminal var identity root does not match the expected key.");
        }

        PooledTerminalVarIdentitySet identities = PooledTerminalVarIdentitySet.Rent(256, shelfExtentSize);
        while (shelfOffset != 0)
        {
            byte[] shelfBytes = ReadTerminalVarIdentityShelfBytesCached(shelfOffset, shelfExtentSize);
            TerminalVarIdentityShelfLayout.Validate(shelfBytes, shelfExtentSize);
            int itemCount = TerminalVarIdentityShelfLayout.ReadItemCount(shelfBytes);
            for (int i = 0; i < itemCount; i++)
            {
                identities.Add(TerminalVarIdentityShelfLayout.ReadIdentityAt(shelfBytes, i));
            }

            shelfOffset = TerminalVarIdentityShelfLayout.ReadNextShelfOffset(shelfBytes);
        }

        return identities;
    }

    private byte[][] CollectScalar8VarIdentitySameKeyChain(long headShelfOffset, byte[] headShelfBytes, Scalar8VarIdentityProfile profile, ulong encodedKey, ReadOnlySpan<byte> incomingIdentity, out bool incomingAdded, out bool alreadyPresent)
    {
        byte[][] identities = new byte[256][];
        int count = 0;
        incomingAdded = false;
        alreadyPresent = false;
        using RouteVisitedOffsetSet visited = RouteVisitedOffsetSet.Rent();
        long currentOffset = headShelfOffset;
        byte[] currentBytes = headShelfBytes;
        while (currentOffset != 0)
        {
            if (!visited.Add(currentOffset))
            {
                throw new InvalidDataException("The SV8 same-key chain contains a cycle.");
            }

            Scalar8VarIdentityReadOnly shelf = new(currentBytes, profile);
            if (!shelf.IsValid)
            {
                throw new InvalidDataException("The SV8 same-key chain contains an invalid shelf.");
            }

            for (int i = 0; i < shelf.ItemCount; i++)
            {
                if (shelf.ReadKeyAt(i) != encodedKey)
                {
                    throw new InvalidDataException("The SV8 same-key terminal conversion encountered a mixed-key shelf.");
                }

                ReadOnlySpan<byte> currentIdentity = shelf.ReadIdentityAt(i);
                if (!incomingAdded && (profile.Descending
                    ? Scalar8VarIdentityLayout.CompareIdentityBytes(incomingIdentity, currentIdentity) > 0
                    : Scalar8VarIdentityLayout.CompareIdentityBytes(incomingIdentity, currentIdentity) < 0))
                {
                    AddTerminalVarIdentityBytes(ref identities, ref count, incomingIdentity.ToArray());
                    incomingAdded = true;
                }

                if (Scalar8VarIdentityLayout.IdentityBytesEqual(currentIdentity, incomingIdentity))
                {
                    alreadyPresent = true;
                }

                AddTerminalVarIdentityBytes(ref identities, ref count, currentIdentity.ToArray());
            }

            currentOffset = Scalar8VarIdentityLayout.ReadNextShelfOffset(currentBytes);
            if (currentOffset != 0)
            {
                currentBytes = ReadScalar8VarIdentityShelfBytes(currentOffset, profile.MaxIdentityLength, out Scalar8VarIdentityProfile nextProfile);
                if (nextProfile.ShelfExtentSize != profile.ShelfExtentSize)
                {
                    throw new InvalidDataException("The SV8 same-key chain changed shelf extent sizes.");
                }
            }
        }

        Array.Resize(ref identities, count);
        return identities;
    }

    /// <summary>
    /// Collects a same-key `SV8` duplicate chain into a pooled flat terminal identity workspace.<br/>
    /// The incoming identity is inserted in sorted byte order during collection when its position is encountered, avoiding a second object-array construction pass.<br/>
    /// </summary>
    /// <param name="headShelfOffset">The first ordinary `SV8` shelf in the same-key chain.<br/></param>
    /// <param name="headShelfBytes">The already-read head shelf bytes.<br/></param>
    /// <param name="profile">The ordinary `SV8` shelf profile for every chain segment.<br/></param>
    /// <param name="encodedKey">The scalar key expected in every tuple of the chain.<br/></param>
    /// <param name="incomingIdentity">The incoming identity bytes to merge into the sorted sequence.<br/></param>
    /// <param name="incomingAdded">Receives whether the incoming identity was added during collection.<br/></param>
    /// <param name="alreadyPresent">Receives whether the incoming identity already existed in the chain.<br/></param>
    /// <returns>A pooled identity workspace owned by the caller.</returns>
    private PooledTerminalVarIdentitySet CollectScalar8VarIdentitySameKeyChainPooled(
        long headShelfOffset,
        byte[] headShelfBytes,
        Scalar8VarIdentityProfile profile,
        ulong encodedKey,
        ReadOnlySpan<byte> incomingIdentity,
        out bool incomingAdded,
        out bool alreadyPresent)
    {
        PooledTerminalVarIdentitySet identities = PooledTerminalVarIdentitySet.Rent(256, profile.ShelfExtentSize);
        incomingAdded = false;
        alreadyPresent = false;
        using RouteVisitedOffsetSet visited = RouteVisitedOffsetSet.Rent();
        long currentOffset = headShelfOffset;
        byte[] currentBytes = headShelfBytes;
        while (currentOffset != 0)
        {
            if (!visited.Add(currentOffset))
            {
                throw new InvalidDataException("The SV8 same-key chain contains a cycle.");
            }

            Scalar8VarIdentityReadOnly shelf = new(currentBytes, profile);
            if (!shelf.IsValid)
            {
                throw new InvalidDataException("The SV8 same-key chain contains an invalid shelf.");
            }

            for (int i = 0; i < shelf.ItemCount; i++)
            {
                if (shelf.ReadKeyAt(i) != encodedKey)
                {
                    throw new InvalidDataException("The SV8 same-key terminal conversion encountered a mixed-key shelf.");
                }

                ReadOnlySpan<byte> currentIdentity = shelf.ReadIdentityAt(i);
                if (!incomingAdded && (profile.Descending
                    ? Scalar8VarIdentityLayout.CompareIdentityBytes(incomingIdentity, currentIdentity) > 0
                    : Scalar8VarIdentityLayout.CompareIdentityBytes(incomingIdentity, currentIdentity) < 0))
                {
                    identities.Add(incomingIdentity);
                    incomingAdded = true;
                }

                if (Scalar8VarIdentityLayout.IdentityBytesEqual(currentIdentity, incomingIdentity))
                {
                    alreadyPresent = true;
                }

                identities.Add(currentIdentity);
            }

            currentOffset = Scalar8VarIdentityLayout.ReadNextShelfOffset(currentBytes);
            if (currentOffset != 0)
            {
                currentBytes = ReadScalar8VarIdentityShelfBytes(currentOffset, profile.MaxIdentityLength, out Scalar8VarIdentityProfile nextProfile);
                if (nextProfile.ShelfExtentSize != profile.ShelfExtentSize)
                {
                    throw new InvalidDataException("The SV8 same-key chain changed shelf extent sizes.");
                }
            }
        }

        return identities;
    }

    /// <summary>
    /// Verifies that every tuple in an ordinary `SV8` shelf chain belongs to one scalar key before the chain is converted to an exact-key terminal identity route.<br/>
    /// This keeps the terminal shape contract strict: terminal roots own one full scalar key and terminal shelves store identities only for that key.<br/>
    /// Mixed chains must stay on the ordinary route/split path or use the mixed-shelf extraction path that separates the hot key from the remainder first.<br/>
    /// </summary>
    /// <param name="headShelfOffset">The first ordinary `SV8` shelf offset in the candidate chain.<br/></param>
    /// <param name="headShelfBytes">The already-read head shelf bytes.<br/></param>
    /// <param name="profile">The ordinary shelf profile expected for each chain shelf.<br/></param>
    /// <param name="encodedKey">The scalar key that must own every tuple in the chain.<br/></param>
    /// <returns><see langword="true"/> when the complete chain is exact-key scoped; otherwise <see langword="false"/>.</returns>
    private bool IsScalar8VarIdentitySameKeyChain(
        long headShelfOffset,
        byte[] headShelfBytes,
        Scalar8VarIdentityProfile profile,
        ulong encodedKey)
    {
        using RouteVisitedOffsetSet visited = RouteVisitedOffsetSet.Rent();
        long currentOffset = headShelfOffset;
        byte[] currentBytes = headShelfBytes;
        while (currentOffset != 0)
        {
            if (!visited.Add(currentOffset))
            {
                throw new InvalidDataException("The SV8 same-key chain contains a cycle.");
            }

            Scalar8VarIdentityReadOnly shelf = new(currentBytes, profile);
            if (!shelf.IsValid)
            {
                throw new InvalidDataException("The SV8 same-key chain contains an invalid shelf.");
            }

            for (int i = 0; i < shelf.ItemCount; i++)
            {
                if (shelf.ReadKeyAt(i) != encodedKey)
                {
                    return false;
                }
            }

            currentOffset = Scalar8VarIdentityLayout.ReadNextShelfOffset(currentBytes);
            if (currentOffset != 0)
            {
                currentBytes = ReadScalar8VarIdentityShelfBytes(currentOffset, profile.MaxIdentityLength, out Scalar8VarIdentityProfile nextProfile);
                if (nextProfile.ShelfExtentSize != profile.ShelfExtentSize)
                {
                    throw new InvalidDataException("The SV8 same-key chain changed shelf extent sizes.");
                }
            }
        }

        return true;
    }

    private static void AddTerminalVarIdentityBytes(ref byte[][] identities, ref int count, byte[] identity)
    {
        if (count == identities.Length)
        {
            Array.Resize(ref identities, checked(identities.Length * 2));
        }

        identities[count++] = identity;
    }

    /// <summary>
    /// Selects the compact terminal identity shelf extent for an `SV8` duplicate-key route.<br/>
    /// The source `SV8` shelf may have grown to a large extent because it stored the scalar key beside every variable identity.<br/>
    /// Terminal shelves store the scalar key once in the terminal root, so they can usually use a page-sized identity-only extent instead of inheriting the oversized source shelf.<br/>
    /// The selected extent starts at 4 KB and doubles only when needed to fit the largest identity record in the converted run.<br/>
    /// </summary>
    /// <param name="sourceShelfExtentSize">The current source shelf extent size.</param>
    /// <param name="identities">The sorted terminal identity payloads that must fit at least one per shelf.</param>
    /// <returns>The terminal var-identity shelf extent size.</returns>
    private static int SelectScalar8VarIdentityTerminalShelfExtentSize(int sourceShelfExtentSize, ReadOnlySpan<byte[]> identities)
    {
        const int MinimumTerminalShelfExtentSize = 4096;
        int largestIdentityLength = 0;
        for (int i = 0; i < identities.Length; i++)
        {
            if (identities[i].Length > largestIdentityLength)
            {
                largestIdentityLength = identities[i].Length;
            }
        }

        int minimumRequired = checked(TerminalVarIdentityShelfLayout.HeaderSize +
            TerminalVarIdentityShelfLayout.SlotSize +
            TerminalVarIdentityShelfLayout.GetNewRecordLength(largestIdentityLength));
        int selected = MinimumTerminalShelfExtentSize;
        while (selected < minimumRequired && selected < sourceShelfExtentSize)
        {
            selected = checked(selected * 2);
        }

        return selected > sourceShelfExtentSize ? sourceShelfExtentSize : selected;
    }

    /// <summary>
    /// Selects the compact terminal identity shelf extent for a pooled byte-native identity workspace.<br/>
    /// The selected extent starts at 4 KB and doubles only when required by the largest identity payload.<br/>
    /// </summary>
    /// <param name="sourceShelfExtentSize">The current source shelf extent size.<br/></param>
    /// <param name="identities">The pooled terminal identity workspace.<br/></param>
    /// <returns>The terminal var-identity shelf extent size.</returns>
    private static int SelectScalar8VarIdentityTerminalShelfExtentSize(int sourceShelfExtentSize, PooledTerminalVarIdentitySet identities)
    {
        const int MinimumTerminalShelfExtentSize = 4096;
        int largestIdentityLength = identities.LargestIdentityLength;
        int minimumRequired = checked(TerminalVarIdentityShelfLayout.HeaderSize +
            TerminalVarIdentityShelfLayout.SlotSize +
            TerminalVarIdentityShelfLayout.GetNewRecordLength(largestIdentityLength));
        int selected = MinimumTerminalShelfExtentSize;
        while (selected < minimumRequired && selected < sourceShelfExtentSize)
        {
            selected = checked(selected * 2);
        }

        return selected > sourceShelfExtentSize ? sourceShelfExtentSize : selected;
    }

    private static int LowerBoundTerminalVarIdentity(ReadOnlySpan<byte[]> identities, ReadOnlySpan<byte> identity, bool descending = false)
    {
        int low = 0;
        int high = identities.Length;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if ((descending ? -1 : 1) * Scalar8VarIdentityLayout.CompareIdentityBytes(identities[middle], identity) < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    /// <summary>
    /// Finds the sorted insertion point for an identity inside a pooled byte-native terminal identity workspace.<br/>
    /// Comparisons use the same raw-byte identity ordering as the persisted terminal shelves.<br/>
    /// </summary>
    /// <param name="identities">The pooled terminal identity workspace to search.<br/></param>
    /// <param name="identity">The identity bytes to locate.<br/></param>
    /// <returns>The insertion index for <paramref name="identity"/>.</returns>
    private static int LowerBoundTerminalVarIdentity(PooledTerminalVarIdentitySet identities, ReadOnlySpan<byte> identity, bool descending = false)
    {
        int low = 0;
        int high = identities.Count;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if ((descending ? -1 : 1) * Scalar8VarIdentityLayout.CompareIdentityBytes(identities.ReadAt(middle), identity) < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    /// <summary>
    /// Counts how many pooled terminal identities can fit in one terminal var-identity shelf.<br/>
    /// This mirrors <see cref="TerminalVarIdentityShelfLayout.GetChunkCount(ReadOnlySpan{byte[]}, int, int, int)"/> without requiring identity object arrays.<br/>
    /// </summary>
    /// <param name="identities">The pooled terminal identity workspace.<br/></param>
    /// <param name="start">The first identity index to test.<br/></param>
    /// <param name="available">The maximum identity count available from <paramref name="start"/>.<br/></param>
    /// <param name="shelfExtentSize">The terminal shelf extent size.<br/></param>
    /// <returns>The number of identities that fit in one shelf.</returns>
    private static int GetTerminalVarIdentityChunkCount(PooledTerminalVarIdentitySet identities, int start, int available, int shelfExtentSize)
    {
        int slotCapacityBytes = TerminalVarIdentityShelfLayout.CalculateSlotCapacityBytes(shelfExtentSize);
        int slotCursor = TerminalVarIdentityShelfLayout.HeaderSize;
        int recordCursor = TerminalVarIdentityShelfLayout.HeaderSize + slotCapacityBytes;
        int count = 0;
        while (count < available)
        {
            int identityLength = identities.GetLength(start + count);
            int recordLength = TerminalVarIdentityShelfLayout.GetNewRecordLength(identityLength);
            if (slotCursor + TerminalVarIdentityShelfLayout.SlotSize > TerminalVarIdentityShelfLayout.HeaderSize + slotCapacityBytes ||
                recordCursor + recordLength > shelfExtentSize)
            {
                break;
            }

            slotCursor += TerminalVarIdentityShelfLayout.SlotSize;
            recordCursor += recordLength;
            count++;
        }

        return count;
    }

    /// <summary>
    /// Writes one terminal var-identity shelf directly from a pooled flat identity workspace.<br/>
    /// The caller supplies the final reservation span, so this method avoids constructing an intermediate shelf byte array.<br/>
    /// </summary>
    /// <param name="target">The final terminal shelf reservation span.<br/></param>
    /// <param name="identities">The pooled terminal identity workspace.<br/></param>
    /// <param name="start">The first identity index to write.<br/></param>
    /// <param name="count">The number of identities to write.<br/></param>
    /// <param name="shelfExtentSize">The terminal shelf extent size.<br/></param>
    private static void BuildTerminalVarIdentityShelf(Span<byte> target, PooledTerminalVarIdentitySet identities, int start, int count, int shelfExtentSize)
    {
        TerminalVarIdentityShelfLayout.Initialize(target.Slice(0, shelfExtentSize), shelfExtentSize);
        int slotCapacityBytes = TerminalVarIdentityShelfLayout.ReadSlotCapacityBytes(target);
        int slotCursor = TerminalVarIdentityShelfLayout.HeaderSize;
        int recordCursor = TerminalVarIdentityShelfLayout.HeaderSize + slotCapacityBytes;
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> identity = identities.ReadAt(start + i);
            int recordLength = TerminalVarIdentityShelfLayout.GetNewRecordLength(identity.Length);
            if (slotCursor + TerminalVarIdentityShelfLayout.SlotSize > TerminalVarIdentityShelfLayout.HeaderSize + slotCapacityBytes ||
                recordCursor + recordLength > shelfExtentSize)
            {
                throw new InvalidDataException("The terminal var identity shelf writer exceeded the selected shelf extent.");
            }

            TerminalVarIdentityShelfLayout.WriteRecord(target, recordCursor, identity);
            TerminalVarIdentityShelfLayout.WriteSlotRecordOffset(target, slotCursor, recordCursor);
            slotCursor += TerminalVarIdentityShelfLayout.SlotSize;
            recordCursor += recordLength;
        }

        TerminalVarIdentityShelfLayout.WriteItemCount(target, count);
        TerminalVarIdentityShelfLayout.WriteSlotStreamLength(target, checked(count * TerminalVarIdentityShelfLayout.SlotSize));
        TerminalVarIdentityShelfLayout.WriteRecordArenaEnd(target, recordCursor);
    }

    /// <summary>
    /// Builds one terminal variable-identity shelf directly into caller-owned bytes.<br/>
    /// This avoids creating a temporary one-element `byte[][]` and a copied identity payload when a terminal route appends its first shelf or a new tail shelf.<br/>
    /// The resulting shelf layout is identical to `TerminalVarIdentityShelfLayout.TryBuild` for a single identity.<br/>
    /// </summary>
    /// <param name="target">The full terminal shelf target span.<br/></param>
    /// <param name="shelfExtentSize">The fixed shelf extent size.<br/></param>
    /// <param name="identity">The raw identity bytes to store.<br/></param>
    /// <returns><see langword="true"/> when the single identity fits in the shelf; otherwise <see langword="false"/>.<br/></returns>
    private static bool BuildSingleTerminalVarIdentityShelf(Span<byte> target, int shelfExtentSize, ReadOnlySpan<byte> identity)
    {
        if ((uint)identity.Length == 0 || target.Length < shelfExtentSize)
        {
            return false;
        }

        Span<byte> shelf = target.Slice(0, shelfExtentSize);
        TerminalVarIdentityShelfLayout.Initialize(shelf, shelfExtentSize);
        int slotCapacityBytes = TerminalVarIdentityShelfLayout.ReadSlotCapacityBytes(shelf);
        int slotOffset = TerminalVarIdentityShelfLayout.HeaderSize;
        int recordOffset = TerminalVarIdentityShelfLayout.HeaderSize + slotCapacityBytes;
        int recordLength = TerminalVarIdentityShelfLayout.GetNewRecordLength(identity.Length);
        if (slotOffset + TerminalVarIdentityShelfLayout.SlotSize > TerminalVarIdentityShelfLayout.HeaderSize + slotCapacityBytes ||
            recordOffset + recordLength > shelfExtentSize)
        {
            return false;
        }

        TerminalVarIdentityShelfLayout.WriteRecord(shelf, recordOffset, identity);
        TerminalVarIdentityShelfLayout.WriteSlotRecordOffset(shelf, slotOffset, recordOffset);
        TerminalVarIdentityShelfLayout.WriteItemCount(shelf, 1);
        TerminalVarIdentityShelfLayout.WriteSlotStreamLength(shelf, TerminalVarIdentityShelfLayout.SlotSize);
        TerminalVarIdentityShelfLayout.WriteRecordArenaEnd(shelf, recordOffset + recordLength);
        return true;
    }

    /// <summary>
    /// Inserts one identity into a terminal var-identity shelf span that already has free slot and payload capacity.<br/>
    /// The persisted record arena remains append-only inside the shelf; sorted order is maintained by shifting only the compact slot-offset stream.<br/>
    /// </summary>
    /// <param name="target">The terminal shelf bytes to mutate.<br/></param>
    /// <param name="insertIndex">The sorted slot index where the identity should appear.<br/></param>
    /// <param name="identity">The identity bytes to insert.<br/></param>
    private static void InsertIntoTerminalVarIdentityShelf(Span<byte> target, int insertIndex, ReadOnlySpan<byte> identity)
    {
        int count = TerminalVarIdentityShelfLayout.ReadItemCount(target);
        if ((uint)insertIndex > (uint)count)
        {
            throw new ArgumentOutOfRangeException(nameof(insertIndex), insertIndex, "The terminal shelf insert index is outside the slot stream.");
        }

        int slotStreamLength = TerminalVarIdentityShelfLayout.ReadSlotStreamLength(target);
        int slotCapacityBytes = TerminalVarIdentityShelfLayout.ReadSlotCapacityBytes(target);
        int recordArenaEnd = TerminalVarIdentityShelfLayout.ReadRecordArenaEnd(target);
        int recordLength = TerminalVarIdentityShelfLayout.GetNewRecordLength(identity.Length);
        int shelfExtentSize = TerminalVarIdentityShelfLayout.ReadShelfExtentSize(target);
        if (slotStreamLength + TerminalVarIdentityShelfLayout.SlotSize > slotCapacityBytes ||
            recordArenaEnd + recordLength > shelfExtentSize)
        {
            throw new InvalidDataException("The terminal var identity shelf does not have room for the local insert.");
        }

        int slotOffset = TerminalVarIdentityShelfLayout.HeaderSize + checked(insertIndex * TerminalVarIdentityShelfLayout.SlotSize);
        int shiftedSlotBytes = slotStreamLength - checked(insertIndex * TerminalVarIdentityShelfLayout.SlotSize);
        if (shiftedSlotBytes > 0)
        {
            target.Slice(slotOffset, shiftedSlotBytes).CopyTo(target.Slice(slotOffset + TerminalVarIdentityShelfLayout.SlotSize, shiftedSlotBytes));
        }

        TerminalVarIdentityShelfLayout.WriteRecord(target, recordArenaEnd, identity);
        TerminalVarIdentityShelfLayout.WriteSlotRecordOffset(target, slotOffset, recordArenaEnd);
        TerminalVarIdentityShelfLayout.WriteItemCount(target, count + 1);
        TerminalVarIdentityShelfLayout.WriteSlotStreamLength(target, slotStreamLength + TerminalVarIdentityShelfLayout.SlotSize);
        TerminalVarIdentityShelfLayout.WriteRecordArenaEnd(target, recordArenaEnd + recordLength);
    }

    private static int CompareScalar8VarIdentityTuple(ulong leftKey, ReadOnlySpan<byte> leftIdentity, ulong rightKey, ReadOnlySpan<byte> rightIdentity, bool descending)
    {
        if (leftKey < rightKey)
        {
            return descending ? 1 : -1;
        }

        if (leftKey > rightKey)
        {
            return descending ? -1 : 1;
        }

        int comparison = Scalar8VarIdentityLayout.CompareIdentityBytes(leftIdentity, rightIdentity);
        return descending ? -comparison : comparison;
    }

    internal static byte GetScalar8VarIdentityPrefix(ulong encodedKey, int keyDepth)
    {
        return keyDepth >= 0 && keyDepth < sizeof(ulong)
            ? (byte)(encodedKey >> ((sizeof(ulong) - 1 - keyDepth) * 8))
            : (byte)0;
    }

    /// <summary>
    /// Stores terminal variable identities as one pooled byte payload plus ordinal offset/length tables.<br/>
    /// This is a mutation-time workspace for byte-native terminal route rewrites, avoiding one managed `byte[]` allocation per identity while keeping sorted ordinal access cheap.<br/>
    /// </summary>
    private sealed class PooledTerminalVarIdentitySet : IDisposable
    {
        private byte[] payload;
        private int[] offsets;
        private int[] lengths;
        private int payloadLength;
        private bool disposed;

        private PooledTerminalVarIdentitySet(byte[] payload, int[] offsets, int[] lengths)
        {
            this.payload = payload;
            this.offsets = offsets;
            this.lengths = lengths;
        }

        /// <summary>
        /// Gets the number of identities stored in this workspace.<br/>
        /// </summary>
        public int Count { get; private set; }

        /// <summary>
        /// Gets the largest identity byte length stored in this workspace.<br/>
        /// </summary>
        public int LargestIdentityLength { get; private set; }

        /// <summary>
        /// Rents a pooled terminal identity workspace with the requested initial capacities.<br/>
        /// </summary>
        /// <param name="identityCapacity">The expected initial identity count.<br/></param>
        /// <param name="payloadCapacity">The expected initial payload byte capacity.<br/></param>
        /// <returns>A pooled workspace owned by the caller.</returns>
        public static PooledTerminalVarIdentitySet Rent(int identityCapacity, int payloadCapacity)
        {
            int effectiveIdentityCapacity = Math.Max(4, identityCapacity);
            int effectivePayloadCapacity = Math.Max(256, payloadCapacity);
            return new PooledTerminalVarIdentitySet(
                ArrayPool<byte>.Shared.Rent(effectivePayloadCapacity),
                ArrayPool<int>.Shared.Rent(effectiveIdentityCapacity),
                ArrayPool<int>.Shared.Rent(effectiveIdentityCapacity));
        }

        /// <summary>
        /// Appends one identity to the workspace and records its ordinal slice.<br/>
        /// The source bytes are copied once into the pooled payload buffer because terminal rewrites can outlive the source shelf byte array lifetime.<br/>
        /// </summary>
        /// <param name="identity">The identity bytes to append.<br/></param>
        public void Add(ReadOnlySpan<byte> identity)
        {
            ThrowIfDisposed();
            EnsureIdentityCapacity(Count + 1);
            EnsurePayloadCapacity(payloadLength + identity.Length);
            int offset = payloadLength;
            identity.CopyTo(payload.AsSpan(offset, identity.Length));
            offsets[Count] = offset;
            lengths[Count] = identity.Length;
            payloadLength += identity.Length;
            Count++;
            if (identity.Length > LargestIdentityLength)
            {
                LargestIdentityLength = identity.Length;
            }
        }

        /// <summary>
        /// Inserts one identity ordinal at the requested sorted position.<br/>
        /// The identity bytes are appended to the pooled payload buffer while offset/length ordinals are shifted to preserve sort order.<br/>
        /// </summary>
        /// <param name="index">The ordinal index where the identity should appear.<br/></param>
        /// <param name="identity">The identity bytes to insert.<br/></param>
        public void InsertAt(int index, ReadOnlySpan<byte> identity)
        {
            ThrowIfDisposed();
            if ((uint)index > (uint)Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, "The terminal identity insert index is outside the workspace.");
            }

            EnsureIdentityCapacity(Count + 1);
            EnsurePayloadCapacity(payloadLength + identity.Length);
            if (index < Count)
            {
                Array.Copy(offsets, index, offsets, index + 1, Count - index);
                Array.Copy(lengths, index, lengths, index + 1, Count - index);
            }

            int offset = payloadLength;
            identity.CopyTo(payload.AsSpan(offset, identity.Length));
            offsets[index] = offset;
            lengths[index] = identity.Length;
            payloadLength += identity.Length;
            Count++;
            if (identity.Length > LargestIdentityLength)
            {
                LargestIdentityLength = identity.Length;
            }
        }

        /// <summary>
        /// Reads one identity by sorted ordinal as a byte span over pooled payload storage.<br/>
        /// </summary>
        /// <param name="index">The identity ordinal to read.<br/></param>
        /// <returns>The identity bytes for the requested ordinal.</returns>
        public ReadOnlySpan<byte> ReadAt(int index)
        {
            ThrowIfDisposed();
            if ((uint)index >= (uint)Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, "The terminal identity index is outside the workspace.");
            }

            return payload.AsSpan(offsets[index], lengths[index]);
        }

        /// <summary>
        /// Gets the byte length for one identity ordinal.<br/>
        /// </summary>
        /// <param name="index">The identity ordinal to inspect.<br/></param>
        /// <returns>The identity byte length.</returns>
        public int GetLength(int index)
        {
            ThrowIfDisposed();
            if ((uint)index >= (uint)Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, "The terminal identity index is outside the workspace.");
            }

            return lengths[index];
        }

        /// <summary>
        /// Returns rented buffers to the shared array pool.<br/>
        /// Payload bytes are cleared because path/blob identities can be user data and should not linger in pooled arrays.<br/>
        /// </summary>
        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            ArrayPool<byte>.Shared.Return(payload, clearArray: true);
            ArrayPool<int>.Shared.Return(offsets, clearArray: false);
            ArrayPool<int>.Shared.Return(lengths, clearArray: false);
            payload = [];
            offsets = [];
            lengths = [];
            payloadLength = 0;
            Count = 0;
            LargestIdentityLength = 0;
            disposed = true;
        }

        private void EnsureIdentityCapacity(int required)
        {
            if (required <= offsets.Length)
            {
                return;
            }

            int next = offsets.Length * 2;
            while (next < required)
            {
                next *= 2;
            }

            int[] nextOffsets = ArrayPool<int>.Shared.Rent(next);
            int[] nextLengths = ArrayPool<int>.Shared.Rent(next);
            Array.Copy(offsets, nextOffsets, Count);
            Array.Copy(lengths, nextLengths, Count);
            ArrayPool<int>.Shared.Return(offsets, clearArray: false);
            ArrayPool<int>.Shared.Return(lengths, clearArray: false);
            offsets = nextOffsets;
            lengths = nextLengths;
        }

        private void EnsurePayloadCapacity(int required)
        {
            if (required <= payload.Length)
            {
                return;
            }

            int next = payload.Length * 2;
            while (next < required)
            {
                next *= 2;
            }

            byte[] nextPayload = ArrayPool<byte>.Shared.Rent(next);
            payload.AsSpan(0, payloadLength).CopyTo(nextPayload);
            ArrayPool<byte>.Shared.Return(payload, clearArray: true);
            payload = nextPayload;
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(PooledTerminalVarIdentitySet));
            }
        }
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
            Scalar8Scalar8RouteTargetKind.TerminalIdentityRoot => Scalar8VarIdentityRouteTargetKind.TerminalVarIdentityRoot,
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
            Scalar8VarIdentityRouteTargetKind.TerminalVarIdentityRoot => Scalar8Scalar8RouteTargetKind.TerminalIdentityRoot,
            _ => Scalar8Scalar8RouteTargetKind.None
        };
    }

    /// <summary>
    /// Deletes one identity from a terminal variable-identity chain while touching only the containing shelf and the minimum link metadata.<br/>
    /// A nonempty survivor shelf is repacked at its existing offset so deleted payload bytes cannot accumulate as fragmentation; an empty shelf is unlinked by rewriting only its predecessor and, when necessary, the terminal root first/tail pointers.<br/>
    /// The method validates strict cross-shelf identity ordering before mutation and serializes structural publication through the session storage-publication gate.<br/>
    /// </summary>
    /// <param name="rootOffset">The terminal identity root offset selected by the routed exact-key walk.<br/></param>
    /// <param name="expectedShape">The terminal root shape expected by the calling physical family.<br/></param>
    /// <param name="expectedKey">The exact encoded key expected in the terminal root.<br/></param>
    /// <param name="identity">The raw identity bytes to remove.<br/></param>
    /// <returns><see langword="true"/> when the identity existed and was removed; otherwise <see langword="false"/>.<br/></returns>
    private bool DeleteTerminalVarIdentityExactTupleLocally(
        long rootOffset,
        byte expectedShape,
        ReadOnlySpan<byte> expectedKey,
        ReadOnlySpan<byte> identity)
    {
        lock (writePublicationSync)
        {
            kernel.EnterExclusiveStoragePublication();
            try
            {
                byte[] rootBytes = ReadTerminalIdentityRootBytes(rootOffset);
                if (TerminalIdentityRootLayout.ReadShape(rootBytes) != expectedShape ||
                    !IsTerminalIdentityRootForKey(rootBytes, expectedKey, out long firstShelfOffset))
                {
                    throw new InvalidDataException("The terminal variable-identity delete root does not match the routed key and shape.");
                }

                bool descending = rootBytes[TerminalIdentityRootLayout.SortDirectionOffset] != 0;

                if (firstShelfOffset == 0)
                {
                    return false;
                }

                int shelfExtentSize = TerminalIdentityRootLayout.ReadShelfExtentSize(rootBytes);
                long rootTailOffset = TerminalIdentityRootLayout.ReadTailShelfOffset(rootBytes);
                long previousOffset = 0;
                byte[]? previousBytes = null;
                long currentOffset = firstShelfOffset;
                while (currentOffset != 0)
                {
                    byte[] currentBytes = ReadTerminalVarIdentityShelfBytesCached(currentOffset, shelfExtentSize);
                    TerminalVarIdentityShelfLayout.Validate(currentBytes, shelfExtentSize);
                    int count = TerminalVarIdentityShelfLayout.ReadItemCount(currentBytes);
                    long nextOffset = TerminalVarIdentityShelfLayout.ReadNextShelfOffset(currentBytes);
                    if (count == 0)
                    {
                        previousOffset = currentOffset;
                        previousBytes = currentBytes;
                        currentOffset = nextOffset;
                        continue;
                    }

                    ReadOnlySpan<byte> firstIdentity = TerminalVarIdentityShelfLayout.ReadIdentityAt(currentBytes, 0);
                    ReadOnlySpan<byte> lastIdentity = TerminalVarIdentityShelfLayout.ReadIdentityAt(currentBytes, count - 1);
                    if (previousBytes is not null)
                    {
                        int previousCount = TerminalVarIdentityShelfLayout.ReadItemCount(previousBytes);
                        if (previousCount != 0 &&
                            (descending ? -1 : 1) * Scalar8VarIdentityLayout.CompareIdentityBytes(
                                TerminalVarIdentityShelfLayout.ReadIdentityAt(previousBytes, previousCount - 1),
                                firstIdentity) >= 0)
                        {
                            throw new InvalidDataException("The terminal variable-identity delete chain is not strictly ordered across its shelf boundary.");
                        }
                    }

                    if ((descending ? -1 : 1) * Scalar8VarIdentityLayout.CompareIdentityBytes(identity, firstIdentity) < 0)
                    {
                        return false;
                    }

                    if ((descending ? -1 : 1) * Scalar8VarIdentityLayout.CompareIdentityBytes(identity, lastIdentity) > 0)
                    {
                        previousOffset = currentOffset;
                        previousBytes = currentBytes;
                        currentOffset = nextOffset;
                        continue;
                    }

                    int deleteIndex = LowerBoundTerminalVarIdentityShelf(currentBytes, count, identity, descending);
                    if (deleteIndex >= count ||
                        Scalar8VarIdentityLayout.CompareIdentityBytes(TerminalVarIdentityShelfLayout.ReadIdentityAt(currentBytes, deleteIndex), identity) != 0)
                    {
                        return false;
                    }

                    if (count > 1)
                    {
                        byte[] rewrittenBytes = new byte[shelfExtentSize];
                        BuildTerminalVarIdentityShelfWithoutIndex(rewrittenBytes, currentBytes, count, deleteIndex, shelfExtentSize);
                        TerminalVarIdentityShelfLayout.WriteNextShelfOffset(rewrittenBytes, nextOffset);
                        PublishTerminalVarIdentityShelfRewrite(currentOffset, rewrittenBytes, shelfExtentSize);
                        _ = CommitTerminalVarIdentityMutationIfNeeded(directMemory: false);
                        return true;
                    }

                    byte[]? updatedPreviousBytes = null;
                    byte[]? updatedRootBytes = null;
                    if (previousOffset == 0)
                    {
                        updatedRootBytes = rootBytes.AsSpan(0, TerminalIdentityRootLayout.Size).ToArray();
                        TerminalIdentityRootLayout.WriteFirstShelfOffset(updatedRootBytes, nextOffset);
                        if (nextOffset == 0)
                        {
                            TerminalIdentityRootLayout.WriteTailShelfOffset(updatedRootBytes, 0);
                        }
                    }
                    else
                    {
                        updatedPreviousBytes = previousBytes!.AsSpan(0, shelfExtentSize).ToArray();
                        if (TerminalVarIdentityShelfLayout.ReadNextShelfOffset(updatedPreviousBytes) != currentOffset)
                        {
                            throw new InvalidDataException("The terminal variable-identity predecessor changed before exact-delete unlink publication.");
                        }

                        TerminalVarIdentityShelfLayout.WriteNextShelfOffset(updatedPreviousBytes, nextOffset);
                    }

                    if (nextOffset == 0 && previousOffset != 0)
                    {
                        if (rootTailOffset != 0 && rootTailOffset != currentOffset)
                        {
                            throw new InvalidDataException("The terminal variable-identity root tail does not match the shelf being unlinked.");
                        }

                        updatedRootBytes ??= rootBytes.AsSpan(0, TerminalIdentityRootLayout.Size).ToArray();
                        TerminalIdentityRootLayout.WriteTailShelfOffset(updatedRootBytes, previousOffset);
                    }

                    if (updatedPreviousBytes is not null)
                    {
                        PublishTerminalVarIdentityShelfRewrite(previousOffset, updatedPreviousBytes, shelfExtentSize);
                    }

                    if (updatedRootBytes is not null)
                    {
                        PublishTerminalVarIdentityRootRewrite(rootOffset, updatedRootBytes);
                    }

                    terminalVarIdentityMutableBatchShelfBytes.Remove(currentOffset);
                    terminalVarIdentityShelfReadCache.TryRemove(currentOffset, out _);
                    kernel.StageExtentRetirement(currentOffset, shelfExtentSize);
                    _ = CommitTerminalVarIdentityMutationIfNeeded(directMemory: false);

                    return true;
                }

                return false;
            }
            finally
            {
                kernel.ExitExclusiveStoragePublication();
            }
        }
    }

    /// <summary>
    /// Finds the first terminal shelf identity greater than or equal to a target identity.<br/>
    /// The search compares raw persisted identity bytes directly and allocates no per-identity objects.<br/>
    /// </summary>
    /// <param name="shelfBytes">The validated terminal shelf byte image.<br/></param>
    /// <param name="count">The live identity count in the shelf.<br/></param>
    /// <param name="identity">The target raw identity bytes.<br/></param>
    /// <returns>The lower-bound slot index in the range zero through <paramref name="count"/>.<br/></returns>
    private static int LowerBoundTerminalVarIdentityShelf(ReadOnlySpan<byte> shelfBytes, int count, ReadOnlySpan<byte> identity, bool descending = false)
    {
        int low = 0;
        int high = count;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if ((descending ? -1 : 1) * Scalar8VarIdentityLayout.CompareIdentityBytes(TerminalVarIdentityShelfLayout.ReadIdentityAt(shelfBytes, middle), identity) < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    /// <summary>
    /// Rebuilds one terminal variable-identity shelf from its own live slots while omitting exactly one slot.<br/>
    /// Repacking removes the deleted payload bytes and preserves bounded future insert capacity without reading or rewriting any other identity shelf.<br/>
    /// The caller restores the linked-list next pointer after this method initializes the compact shelf image.<br/>
    /// </summary>
    /// <param name="target">The complete destination shelf span.<br/></param>
    /// <param name="source">The validated source shelf bytes.<br/></param>
    /// <param name="sourceCount">The live source identity count.<br/></param>
    /// <param name="deleteIndex">The one source slot to omit.<br/></param>
    /// <param name="shelfExtentSize">The fixed terminal shelf extent size.<br/></param>
    private static void BuildTerminalVarIdentityShelfWithoutIndex(
        Span<byte> target,
        ReadOnlySpan<byte> source,
        int sourceCount,
        int deleteIndex,
        int shelfExtentSize)
    {
        TerminalVarIdentityShelfLayout.Initialize(target.Slice(0, shelfExtentSize), shelfExtentSize);
        int slotCapacityBytes = TerminalVarIdentityShelfLayout.ReadSlotCapacityBytes(target);
        int slotCursor = TerminalVarIdentityShelfLayout.HeaderSize;
        int recordCursor = TerminalVarIdentityShelfLayout.HeaderSize + slotCapacityBytes;
        int written = 0;
        for (int i = 0; i < sourceCount; i++)
        {
            if (i == deleteIndex)
            {
                continue;
            }

            ReadOnlySpan<byte> currentIdentity = TerminalVarIdentityShelfLayout.ReadIdentityAt(source, i);
            int recordLength = TerminalVarIdentityShelfLayout.GetNewRecordLength(currentIdentity.Length);
            if (slotCursor + TerminalVarIdentityShelfLayout.SlotSize > TerminalVarIdentityShelfLayout.HeaderSize + slotCapacityBytes ||
                recordCursor + recordLength > shelfExtentSize)
            {
                throw new InvalidDataException("The terminal variable-identity local delete repack exceeded the source shelf extent.");
            }

            TerminalVarIdentityShelfLayout.WriteRecord(target, recordCursor, currentIdentity);
            TerminalVarIdentityShelfLayout.WriteSlotRecordOffset(target, slotCursor, recordCursor);
            slotCursor += TerminalVarIdentityShelfLayout.SlotSize;
            recordCursor += recordLength;
            written++;
        }

        TerminalVarIdentityShelfLayout.WriteItemCount(target, written);
        TerminalVarIdentityShelfLayout.WriteSlotStreamLength(target, checked(written * TerminalVarIdentityShelfLayout.SlotSize));
        TerminalVarIdentityShelfLayout.WriteRecordArenaEnd(target, recordCursor);
    }

    /// <summary>
    /// Publishes one complete terminal variable-identity shelf rewrite while keeping batch, file, and memory backing semantics aligned.<br/>
    /// Durability batches retain the compact image in the existing dirty-shelf map, standalone files stage one fixed-offset rewrite, and memory catalogs update their writable extent directly.<br/>
    /// </summary>
    /// <param name="shelfOffset">The existing shelf offset to rewrite.<br/></param>
    /// <param name="shelfBytes">The complete validated replacement shelf image.<br/></param>
    /// <param name="shelfExtentSize">The fixed persisted shelf extent size.<br/></param>
    private void PublishTerminalVarIdentityShelfRewrite(long shelfOffset, byte[] shelfBytes, int shelfExtentSize)
    {
        if (kernel.TryGetMemoryWritableSpanDirect(shelfOffset, shelfExtentSize, out Span<byte> directSpan))
        {
            shelfBytes.AsSpan(0, shelfExtentSize).CopyTo(directSpan);
        }
        else if (durabilityBatchActive)
        {
            MarkDirtyTerminalVarIdentityShelf(shelfOffset, shelfBytes);
        }
        else
        {
            RawDataReservation rewrite = kernel.ReserveAt(shelfOffset, shelfExtentSize);
            shelfBytes.AsSpan(0, shelfExtentSize).CopyTo(rewrite.Span);
        }

        terminalVarIdentityShelfReadCache[shelfOffset] = shelfBytes;
    }

    /// <summary>
    /// Publishes terminal root first/tail pointer changes at the existing root offset.<br/>
    /// The root cache is updated with the exact replacement image so subsequent mutations in the same session observe the new chain endpoints.<br/>
    /// </summary>
    /// <param name="rootOffset">The existing terminal identity root offset.<br/></param>
    /// <param name="rootBytes">The complete replacement root image.<br/></param>
    private void PublishTerminalVarIdentityRootRewrite(long rootOffset, byte[] rootBytes)
    {
        if (kernel.TryGetMemoryWritableSpanDirect(rootOffset, TerminalIdentityRootLayout.Size, out Span<byte> directSpan))
        {
            rootBytes.AsSpan(0, TerminalIdentityRootLayout.Size).CopyTo(directSpan);
        }
        else
        {
            RawDataReservation rewrite = kernel.ReserveAt(rootOffset, TerminalIdentityRootLayout.Size);
            rootBytes.AsSpan(0, TerminalIdentityRootLayout.Size).CopyTo(rewrite.Span);
        }

        terminalIdentityRootReadCache[rootOffset] = rootBytes;
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
