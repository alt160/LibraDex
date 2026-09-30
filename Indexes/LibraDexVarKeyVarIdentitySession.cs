using LibraDex.Layouts;
using LibraDex.Views;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;

namespace LibraDex;

internal sealed partial class LibraDexFileSession
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

        RouteTargetKindCache targetKindCache = varKeyVarIdentityRouteTargetKindCache.Value!;
        if (targetKindCache.TryGet(targetOffset, out int cachedKind))
        {
            return (VarKeyVarIdentityRouteTargetKind)cachedKind;
        }

        Span<byte> magicBytes = stackalloc byte[sizeof(uint)];
        kernel.Read(targetOffset, magicBytes);
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(magicBytes);
        if (magic == RouterLayout.Magic)
        {
            targetKindCache.Set(targetOffset, (int)VarKeyVarIdentityRouteTargetKind.Router);
            return VarKeyVarIdentityRouteTargetKind.Router;
        }

        if (magic == VarKeyVarIdentityLayout.Magic)
        {
            targetKindCache.Set(targetOffset, (int)VarKeyVarIdentityRouteTargetKind.Shelf);
            return VarKeyVarIdentityRouteTargetKind.Shelf;
        }

        if (magic == TerminalIdentityRootLayout.Magic)
        {
            Span<byte> headerBytes = stackalloc byte[TerminalIdentityRootLayout.HeaderSize];
            kernel.Read(targetOffset, headerBytes);
            if (TerminalIdentityRootLayout.ReadFormatVersion(headerBytes) == TerminalIdentityRootLayout.FormatVersion &&
                TerminalIdentityRootLayout.ReadHeaderSize(headerBytes) == TerminalIdentityRootLayout.HeaderSize &&
                TerminalIdentityRootLayout.ReadShape(headerBytes) == TerminalIdentityRootLayout.ShapeVarKey)
            {
                targetKindCache.Set(targetOffset, (int)VarKeyVarIdentityRouteTargetKind.TerminalVarIdentityRoot);
                return VarKeyVarIdentityRouteTargetKind.TerminalVarIdentityRoot;
            }
        }

        throw new InvalidDataException($"VV route target at offset {targetOffset} does not contain a recognized LibraDex structure.");
    }

    /// <summary>
    /// Counts every ordinary `VV` tuple reachable from the routed root by summing shelf-local count metadata.<br/>
    /// This is the count-all primitive for varlen key / varlen identity storage and avoids range-reader setup when every key is requested.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The routed `VV` root router offset.<br/></param>
    /// <param name="maxKeyLength">The maximum encoded key length accepted by the index.<br/></param>
    /// <param name="maxIdentityLength">The maximum raw identity length accepted by the index.<br/></param>
    /// <returns>The summed ordinary routed tuple count.<br/></returns>
    internal long CountVarKeyVarIdentityIdentities(long rootRouterOffset, int maxKeyLength, int maxIdentityLength)
    {
        HashSet<long> visitedTargets = new();
        HashSet<long> visitedRouters = new();
        return CountVarKeyVarIdentityIdentitiesFromRouter(rootRouterOffset, maxKeyLength, maxIdentityLength, visitedTargets, visitedRouters);
    }

    /// <summary>
    /// Counts `VV` tuples in an inclusive encoded-key range using metadata traversal for fully covered direct root-prefix targets.<br/>
    /// Boundary prefixes keep the established range-reader count path so edge comparisons stay exact without adding a second key predicate implementation.<br/>
    /// Compressed roots and shared boundary targets fall back to reader counting because those shapes cannot prove root-prefix containment from the root router alone.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The routed `VV` root router offset.<br/></param>
    /// <param name="maxKeyLength">The maximum encoded key length accepted by the index.<br/></param>
    /// <param name="maxIdentityLength">The maximum raw identity length accepted by the index.<br/></param>
    /// <param name="lowerKey">The inclusive encoded lower key.<br/></param>
    /// <param name="upperKey">The inclusive encoded upper key.<br/></param>
    /// <returns>The number of matching routed `VV` tuples.<br/></returns>
    internal long CountVarKeyVarIdentityRange(
        long rootRouterOffset,
        int maxKeyLength,
        int maxIdentityLength,
        ReadOnlySpan<byte> lowerKey,
        ReadOnlySpan<byte> upperKey)
    {
        return CountVarKeyVarIdentityRangeByShelfScoop(
            rootRouterOffset,
            maxKeyLength,
            maxIdentityLength,
            lowerKey,
            upperKey);
    }

    /// <summary>
    /// Counts `VV` tuples whose encoded key starts with <paramref name="encodedPrefix"/> using a shape-specific prefix extent walk.<br/>
    /// Contained router targets are counted from existing shelf and terminal-root metadata; partial or ambiguous targets are counted through the established encoded range counter.<br/>
    /// This keeps varlen-key/varlen-identity prefix counts aligned with the `VS8` and `VS16` prefix planners without materializing identities.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The routed `VV` root router offset.<br/></param>
    /// <param name="maxKeyLength">The maximum encoded key length accepted by the index.<br/></param>
    /// <param name="maxIdentityLength">The maximum raw identity length accepted by the index.<br/></param>
    /// <param name="encodedPrefix">The encoded key prefix to match.<br/></param>
    /// <returns>The number of matching routed `VV` tuples.<br/></returns>
    internal long CountVarKeyVarIdentityPrefix(
        long rootRouterOffset,
        int maxKeyLength,
        int maxIdentityLength,
        ReadOnlySpan<byte> encodedPrefix)
    {
        if (encodedPrefix.Length == 0)
        {
            return CountVarKeyVarIdentityIdentities(rootRouterOffset, maxKeyLength, maxIdentityLength);
        }

        byte[] lowerKey = encodedPrefix.ToArray();
        byte[] upperKey = CreateVarKeyPrefixUpperBound(encodedPrefix, maxKeyLength);
        HashSet<long> visitedTargets = new();
        HashSet<long> visitedRouters = new();
        return TryCountVarKeyVarIdentityPrefixFromRouter(
            rootRouterOffset,
            maxKeyLength,
            maxIdentityLength,
            lowerKey,
            upperKey,
            encodedPrefix,
            visitedTargets,
            visitedRouters,
            DefaultVarKeyVarIdentityMaxRouterHops,
            out long count)
            ? count
            : CountVarKeyVarIdentityRange(rootRouterOffset, maxKeyLength, maxIdentityLength, lowerKey, upperKey);
    }

    /// <summary>
    /// Counts a routed `VV` router subtree for one encoded prefix criterion.<br/>
    /// The walker follows only routes that can contain the requested prefix and switches to count-all when the route path proves full containment.<br/>
    /// </summary>
    /// <param name="routerOffset">The router offset to evaluate.<br/></param>
    /// <param name="maxKeyLength">The maximum encoded key length accepted by the index.<br/></param>
    /// <param name="maxIdentityLength">The maximum raw identity length accepted by the index.<br/></param>
    /// <param name="lowerKey">The inclusive encoded lower bound for narrow fallback counting.<br/></param>
    /// <param name="upperKey">The inclusive encoded upper bound for narrow fallback counting.<br/></param>
    /// <param name="encodedPrefix">The encoded prefix being matched.<br/></param>
    /// <param name="visitedTargets">Route targets already counted during this prefix walk.<br/></param>
    /// <param name="visitedRouters">Routers already counted during this prefix walk.<br/></param>
    /// <param name="remainingRouterHops">The remaining router-hop safety budget.<br/></param>
    /// <param name="count">Receives the matching count when the prefix walk succeeds.<br/></param>
    /// <returns><see langword="true"/> when the prefix extent was counted without falling back to the generic range count; otherwise <see langword="false"/>.<br/></returns>
    private bool TryCountVarKeyVarIdentityPrefixFromRouter(
        long routerOffset,
        int maxKeyLength,
        int maxIdentityLength,
        ReadOnlySpan<byte> lowerKey,
        ReadOnlySpan<byte> upperKey,
        ReadOnlySpan<byte> encodedPrefix,
        HashSet<long> visitedTargets,
        HashSet<long> visitedRouters,
        int remainingRouterHops,
        out long count)
    {
        count = 0;
        if (routerOffset == 0)
        {
            return true;
        }

        if (!visitedRouters.Add(routerOffset))
        {
            return true;
        }

        if (remainingRouterHops <= 0)
        {
            return false;
        }

        Span<byte> routerBytes = stackalloc byte[RouterLayout.Size];
        ReadRouterPageUsingArenaCache(routerOffset, routerBytes);
        RouterReader router = new(routerBytes);
        if (!router.IsValid)
        {
            throw new InvalidDataException("The routed VV prefix-count router is invalid.");
        }

        if (router.KeyDepth >= encodedPrefix.Length)
        {
            count = CountContainedVarKeyVarIdentityRouterTargets(router, maxKeyLength, maxIdentityLength, visitedTargets, visitedRouters);
            return true;
        }

        if (router.PrefixByteCount == 1)
        {
            byte prefixByte = GetVarKeyScalar8Prefix(encodedPrefix, router.KeyDepth);
            long targetOffset = router.FindTarget(prefixByte);
            return TryCountVarKeyVarIdentityPrefixFromTarget(
                targetOffset,
                maxKeyLength,
                maxIdentityLength,
                lowerKey,
                upperKey,
                encodedPrefix,
                routeProvesContainment: router.KeyDepth + 1 >= encodedPrefix.Length,
                visitedTargets,
                visitedRouters,
                remainingRouterHops - 1,
                out count);
        }

        return TryCountVarKeyVarIdentityPrefixFromMultiByteRouter(
            router,
            routerBytes,
            maxKeyLength,
            maxIdentityLength,
            lowerKey,
            upperKey,
            encodedPrefix,
            visitedTargets,
            visitedRouters,
            remainingRouterHops,
            out count);
    }

    /// <summary>
    /// Counts matching targets under one compressed multi-byte `VV` router for an encoded prefix criterion.<br/>
    /// Routes whose stored stem proves the requested prefix are counted wholesale; routes with a widened final-byte range continue as partial targets when the requested byte is inside that range.<br/>
    /// </summary>
    /// <param name="router">The decoded router view.<br/></param>
    /// <param name="routerBytes">The router page bytes that own compressed route stems.<br/></param>
    /// <param name="maxKeyLength">The maximum encoded key length accepted by the index.<br/></param>
    /// <param name="maxIdentityLength">The maximum raw identity length accepted by the index.<br/></param>
    /// <param name="lowerKey">The inclusive encoded lower bound for narrow fallback counting.<br/></param>
    /// <param name="upperKey">The inclusive encoded upper bound for narrow fallback counting.<br/></param>
    /// <param name="encodedPrefix">The encoded prefix being matched.<br/></param>
    /// <param name="visitedTargets">Route targets already counted during this prefix walk.<br/></param>
    /// <param name="visitedRouters">Routers already counted during this prefix walk.<br/></param>
    /// <param name="remainingRouterHops">The remaining router-hop safety budget.<br/></param>
    /// <param name="count">Receives the matching count when the compressed-router walk succeeds.<br/></param>
    /// <returns><see langword="true"/> when the compressed router was counted safely; otherwise <see langword="false"/>.<br/></returns>
    private bool TryCountVarKeyVarIdentityPrefixFromMultiByteRouter(
        RouterReader router,
        ReadOnlySpan<byte> routerBytes,
        int maxKeyLength,
        int maxIdentityLength,
        ReadOnlySpan<byte> lowerKey,
        ReadOnlySpan<byte> upperKey,
        ReadOnlySpan<byte> encodedPrefix,
        HashSet<long> visitedTargets,
        HashSet<long> visitedRouters,
        int remainingRouterHops,
        out long count)
    {
        count = 0;
        int keyDepth = router.KeyDepth;
        int prefixByteCount = router.PrefixByteCount;
        int stemLength = prefixByteCount - 1;
        int finalDepth = keyDepth + stemLength;
        int routeEndDepth = keyDepth + prefixByteCount;
        int routeCount = router.RouteCount;
        int maxRouteCount = router.MaxRouteCount;

        for (int routeIndex = 0; routeIndex < routeCount; routeIndex++)
        {
            ReadOnlySpan<byte> stem = routerBytes.Slice(RouterLayout.GetMultiByteRouteStemOffset(maxRouteCount, prefixByteCount, routeIndex), stemLength);
            if (!VarKeyPrefixMatchesRouteStem(encodedPrefix, keyDepth, stem))
            {
                continue;
            }

            long targetOffset = router.GetRouteTargetAt(routeIndex);
            if (targetOffset == 0)
            {
                continue;
            }

            bool contained;
            if (encodedPrefix.Length <= finalDepth)
            {
                contained = true;
            }
            else
            {
                byte finalPrefixByte = GetVarKeyScalar8Prefix(encodedPrefix, finalDepth);
                byte routeStart = router.GetRoutePrefixStartAt(routeIndex);
                byte routeEnd = router.GetRoutePrefixEndAt(routeIndex);
                if (finalPrefixByte < routeStart || finalPrefixByte > routeEnd)
                {
                    continue;
                }

                contained = routeStart == routeEnd && encodedPrefix.Length <= routeEndDepth;
            }

            if (!TryCountVarKeyVarIdentityPrefixFromTarget(
                targetOffset,
                maxKeyLength,
                maxIdentityLength,
                lowerKey,
                upperKey,
                encodedPrefix,
                contained,
                visitedTargets,
                visitedRouters,
                remainingRouterHops - 1,
                out long childCount))
            {
                return false;
            }

            count += childCount;
        }

        return true;
    }

    /// <summary>
    /// Counts one routed `VV` target for an encoded prefix criterion.<br/>
    /// Proven-contained targets use ordinary count-all metadata traversal; partial shelf and terminal targets use exact encoded range checks; routers continue the prefix walk.<br/>
    /// </summary>
    /// <param name="targetOffset">The target offset to count.<br/></param>
    /// <param name="maxKeyLength">The maximum encoded key length accepted by the index.<br/></param>
    /// <param name="maxIdentityLength">The maximum raw identity length accepted by the index.<br/></param>
    /// <param name="lowerKey">The inclusive encoded lower bound for narrow fallback counting.<br/></param>
    /// <param name="upperKey">The inclusive encoded upper bound for narrow fallback counting.<br/></param>
    /// <param name="encodedPrefix">The encoded prefix being matched.<br/></param>
    /// <param name="routeProvesContainment">True when the route path already proves every key below this target starts with <paramref name="encodedPrefix"/>.<br/></param>
    /// <param name="visitedTargets">Route targets already counted during this prefix walk.<br/></param>
    /// <param name="visitedRouters">Routers already counted during this prefix walk.<br/></param>
    /// <param name="remainingRouterHops">The remaining router-hop safety budget.<br/></param>
    /// <param name="count">Receives the matching count when the target was counted safely.<br/></param>
    /// <returns><see langword="true"/> when the target was counted safely; otherwise <see langword="false"/>.<br/></returns>
    private bool TryCountVarKeyVarIdentityPrefixFromTarget(
        long targetOffset,
        int maxKeyLength,
        int maxIdentityLength,
        ReadOnlySpan<byte> lowerKey,
        ReadOnlySpan<byte> upperKey,
        ReadOnlySpan<byte> encodedPrefix,
        bool routeProvesContainment,
        HashSet<long> visitedTargets,
        HashSet<long> visitedRouters,
        int remainingRouterHops,
        out long count)
    {
        count = 0;
        if (targetOffset == 0)
        {
            return true;
        }

        if (routeProvesContainment)
        {
            count = CountVarKeyVarIdentityRouteTarget(targetOffset, maxKeyLength, maxIdentityLength, visitedTargets, visitedRouters);
            return true;
        }

        VarKeyVarIdentityRouteTargetKind kind = ClassifyVarKeyVarIdentityRouteTarget(targetOffset);
        if (kind == VarKeyVarIdentityRouteTargetKind.Router)
        {
            return TryCountVarKeyVarIdentityPrefixFromRouter(
                targetOffset,
                maxKeyLength,
                maxIdentityLength,
                lowerKey,
                upperKey,
                encodedPrefix,
                visitedTargets,
                visitedRouters,
                remainingRouterHops,
                out count);
        }

        if (kind == VarKeyVarIdentityRouteTargetKind.Shelf)
        {
            if (!visitedTargets.Add(targetOffset))
            {
                return true;
            }

            count = CountVarKeyVarIdentityShelfKeyRangeNarrow(targetOffset, maxKeyLength, maxIdentityLength, lowerKey, upperKey);
            return true;
        }

        if (kind == VarKeyVarIdentityRouteTargetKind.TerminalVarIdentityRoot)
        {
            count = CountVarKeyVarIdentityTerminalRootKeyRangeNarrow(targetOffset, lowerKey, upperKey);
            return true;
        }

        throw new InvalidDataException("The routed VV prefix-count target is not a shelf, terminal root, or router.");
    }

    /// <summary>
    /// Counts every target directly referenced by an already visited contained `VV` router.<br/>
    /// This avoids re-entering the router's own visited check while preserving count-all metadata traversal for each child target.<br/>
    /// </summary>
    /// <param name="router">The contained router view whose route targets should be counted.<br/></param>
    /// <param name="maxKeyLength">The maximum encoded key length accepted by the index.<br/></param>
    /// <param name="maxIdentityLength">The maximum raw identity length accepted by the index.<br/></param>
    /// <param name="visitedTargets">Route targets already counted during this prefix walk.<br/></param>
    /// <param name="visitedRouters">Routers already counted during this prefix walk.<br/></param>
    /// <returns>The number of identities in the router's child targets.<br/></returns>
    private long CountContainedVarKeyVarIdentityRouterTargets(
        RouterReader router,
        int maxKeyLength,
        int maxIdentityLength,
        HashSet<long> visitedTargets,
        HashSet<long> visitedRouters)
    {
        long count = 0;
        int routeCount = router.HasDirectIndex ? RouterLayout.MaxOneByteRouteCount : router.RouteCount;
        for (int routeIndex = 0; routeIndex < routeCount; routeIndex++)
        {
            count += CountVarKeyVarIdentityRouteTarget(router.GetRouteTargetAt(routeIndex), maxKeyLength, maxIdentityLength, visitedTargets, visitedRouters);
        }

        return count;
    }

    private bool TryCountVarKeyVarIdentityRangeFromTarget(
        long targetOffset,
        int maxKeyLength,
        int maxIdentityLength,
        ReadOnlySpan<byte> lowerKey,
        ReadOnlySpan<byte> upperKey,
        byte[] pathPrefix,
        bool lowerEdge,
        bool upperEdge,
        HashSet<long> visitedTargets,
        HashSet<long> visitedRouters,
        int remainingRouterHops,
        out long count)
    {
        count = 0;
        if (targetOffset == 0)
        {
            return true;
        }

        if (!lowerEdge && !upperEdge)
        {
            count = CountVarKeyVarIdentityRouteTarget(targetOffset, maxKeyLength, maxIdentityLength, visitedTargets, visitedRouters);
            return true;
        }

        VarKeyVarIdentityRouteTargetKind kind = ClassifyVarKeyVarIdentityRouteTarget(targetOffset);
        if (kind == VarKeyVarIdentityRouteTargetKind.Shelf)
        {
            if (!visitedTargets.Add(targetOffset))
            {
                return true;
            }

            count = CountVarKeyVarIdentityShelfKeyRangeNarrow(targetOffset, maxKeyLength, maxIdentityLength, lowerKey, upperKey);
            return true;
        }

        if (kind == VarKeyVarIdentityRouteTargetKind.TerminalVarIdentityRoot)
        {
            count = CountVarKeyVarIdentityTerminalRootKeyRangeNarrow(targetOffset, lowerKey, upperKey);
            return true;
        }

        if (kind != VarKeyVarIdentityRouteTargetKind.Router)
        {
            throw new InvalidDataException("The routed VV range-count target is not a shelf, terminal identity root, or router.");
        }

        if (!visitedRouters.Add(targetOffset))
        {
            return true;
        }

        if (remainingRouterHops <= 0)
        {
            return false;
        }

        Span<byte> routerBytes = stackalloc byte[RouterLayout.Size];
        ReadRouterPageUsingArenaCache(targetOffset, routerBytes);
        RouterReader router = new(routerBytes);
        if (!router.IsValid)
        {
            throw new InvalidDataException("The routed VV range-count router is invalid.");
        }

        if (router.PrefixByteCount > 1)
        {
            return TryCountVarKeyVarIdentityRangeFromMultiByteRouter(
                router,
                routerBytes,
                maxKeyLength,
                maxIdentityLength,
                lowerKey,
                upperKey,
                pathPrefix,
                lowerEdge,
                upperEdge,
                visitedTargets,
                visitedRouters,
                remainingRouterHops,
                out count);
        }

        byte lowerPrefix = lowerEdge ? GetVarKeyScalar8Prefix(lowerKey, router.KeyDepth) : byte.MinValue;
        byte upperPrefix = upperEdge ? GetVarKeyScalar8Prefix(upperKey, router.KeyDepth) : byte.MaxValue;
        long lowerTarget = lowerEdge ? router.FindTarget(lowerPrefix) : 0;
        long upperTarget = upperEdge ? router.FindTarget(upperPrefix) : 0;
        if (lowerEdge && upperEdge && lowerPrefix != upperPrefix && lowerTarget != 0 && lowerTarget == upperTarget)
        {
            return false;
        }

        for (int prefix = lowerPrefix; prefix <= upperPrefix; prefix++)
        {
            long childTarget = router.FindTarget((byte)prefix);
            if (childTarget == 0)
            {
                continue;
            }

            bool childLowerEdge = lowerEdge && prefix == lowerPrefix;
            bool childUpperEdge = upperEdge && prefix == upperPrefix;
            if (!childLowerEdge && !childUpperEdge && (childTarget == lowerTarget || childTarget == upperTarget))
            {
                return false;
            }

            if (!TryCountVarKeyVarIdentityRangeFromTarget(
                childTarget,
                maxKeyLength,
                maxIdentityLength,
                lowerKey,
                upperKey,
                CreateVarKeyRoutePathPrefix(pathPrefix, router.KeyDepth, (byte)prefix),
                childLowerEdge,
                childUpperEdge,
                visitedTargets,
                visitedRouters,
                remainingRouterHops - 1,
                out long childCount))
            {
                return false;
            }

            count += childCount;
        }

        return true;
    }

    /// <summary>
    /// Counts a compressed multi-byte `VV` router for an inclusive encoded-key range.<br/>
    /// Fully contained compressed routes are counted from existing shelf or terminal metadata, while boundary-overlapping routes continue through exact key-range checks.<br/>
    /// </summary>
    /// <param name="router">The decoded compressed router.<br/></param>
    /// <param name="routerBytes">The router page bytes that own the route stems.<br/></param>
    /// <param name="maxKeyLength">The maximum encoded key length accepted by the index.<br/></param>
    /// <param name="maxIdentityLength">The maximum raw identity length accepted by the index.<br/></param>
    /// <param name="lowerKey">The inclusive encoded lower key.<br/></param>
    /// <param name="upperKey">The inclusive encoded upper key.<br/></param>
    /// <param name="pathPrefix">The key bytes proven by parent route traversal.<br/></param>
    /// <param name="lowerEdge">True when this router may contain the lower range boundary.<br/></param>
    /// <param name="upperEdge">True when this router may contain the upper range boundary.<br/></param>
    /// <param name="visitedTargets">Route targets already counted by this range walk.<br/></param>
    /// <param name="visitedRouters">Routers already counted by this range walk.<br/></param>
    /// <param name="remainingRouterHops">The remaining router-hop safety budget.<br/></param>
    /// <param name="count">Receives the matching count when the router was counted safely.<br/></param>
    /// <returns><see langword="true"/> when the compressed router was counted safely; otherwise <see langword="false"/>.<br/></returns>
    private bool TryCountVarKeyVarIdentityRangeFromMultiByteRouter(
        RouterReader router,
        ReadOnlySpan<byte> routerBytes,
        int maxKeyLength,
        int maxIdentityLength,
        ReadOnlySpan<byte> lowerKey,
        ReadOnlySpan<byte> upperKey,
        ReadOnlySpan<byte> pathPrefix,
        bool lowerEdge,
        bool upperEdge,
        HashSet<long> visitedTargets,
        HashSet<long> visitedRouters,
        int remainingRouterHops,
        out long count)
    {
        count = 0;
        int keyDepth = router.KeyDepth;
        int prefixByteCount = router.PrefixByteCount;
        int stemLength = prefixByteCount - 1;
        int routeCount = router.RouteCount;
        int maxRouteCount = router.MaxRouteCount;
        for (int routeIndex = 0; routeIndex < routeCount; routeIndex++)
        {
            ReadOnlySpan<byte> stem = routerBytes.Slice(RouterLayout.GetMultiByteRouteStemOffset(maxRouteCount, prefixByteCount, routeIndex), stemLength);
            byte routeStart = router.GetRoutePrefixStartAt(routeIndex);
            byte routeEnd = router.GetRoutePrefixEndAt(routeIndex);
            if (!TryClassifyVarKeyMultiByteRangeRoute(pathPrefix, keyDepth, stem, routeStart, routeEnd, lowerKey, upperKey, maxKeyLength, lowerEdge, upperEdge, out bool childLowerEdge, out bool childUpperEdge))
            {
                continue;
            }

            long childTarget = router.GetRouteTargetAt(routeIndex);
            if (childTarget == 0)
            {
                continue;
            }

            if (!TryCountVarKeyVarIdentityRangeFromTarget(
                childTarget,
                maxKeyLength,
                maxIdentityLength,
                lowerKey,
                upperKey,
                CreateVarKeyMultiByteRoutePathPrefix(pathPrefix, keyDepth, stem, routeStart),
                childLowerEdge,
                childUpperEdge,
                visitedTargets,
                visitedRouters,
                remainingRouterHops - 1,
                out long childCount))
            {
                return false;
            }

            count += childCount;
        }

        return true;
    }

    private long CountVarKeyVarIdentityShelfKeyRangeNarrow(
        long shelfOffset,
        int maxKeyLength,
        int maxIdentityLength,
        ReadOnlySpan<byte> lowerKey,
        ReadOnlySpan<byte> upperKey)
    {
        if (durabilityBatchActive &&
            varKeyVarIdentityMutableBatchShelves.TryGetValue(shelfOffset, out VarKeyVarIdentityMutableShelf? mutableShelf))
        {
            return mutableShelf.CountLiveItemsInKeyRange(lowerKey, upperKey);
        }

        VarKeyVarIdentityReadOnly shelf = ReadVarKeyVarIdentityReadOnlyShelf(shelfOffset, maxKeyLength, maxIdentityLength);
        return shelf.CountItemsInKeyRange(lowerKey, upperKey);
    }

    private long CountVarKeyVarIdentityTerminalRootKeyRangeNarrow(long rootOffset, ReadOnlySpan<byte> lowerKey, ReadOnlySpan<byte> upperKey)
    {
        Span<byte> rootHeader = stackalloc byte[TerminalIdentityRootLayout.HeaderSize];
        kernel.Read(rootOffset, rootHeader);
        if (TerminalIdentityRootLayout.ReadMagic(rootHeader) != TerminalIdentityRootLayout.Magic ||
            TerminalIdentityRootLayout.ReadFormatVersion(rootHeader) != TerminalIdentityRootLayout.FormatVersion ||
            TerminalIdentityRootLayout.ReadHeaderSize(rootHeader) != TerminalIdentityRootLayout.HeaderSize ||
            TerminalIdentityRootLayout.ReadShape(rootHeader) != TerminalIdentityRootLayout.ShapeVarKey)
        {
            throw new InvalidDataException("The routed VV range-count terminal root header is invalid.");
        }

        int keyLength = TerminalIdentityRootLayout.ReadKeyLength(rootHeader);
        byte[] rootBytes = new byte[TerminalIdentityRootLayout.Size];
        kernel.Read(rootOffset, rootBytes);
        ReadOnlySpan<byte> key = rootBytes.AsSpan(TerminalIdentityRootLayout.KeyBytesOffset, keyLength);
        return key.SequenceCompareTo(lowerKey) >= 0 && key.SequenceCompareTo(upperKey) <= 0
            ? CountTerminalVarIdentityRootNarrow(rootOffset, TerminalIdentityRootLayout.ShapeVarKey)
            : 0;
    }

    private long CountVarKeyVarIdentityIdentitiesFromRouter(
        long routerOffset,
        int maxKeyLength,
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
            throw new InvalidDataException("The routed VV count router is invalid.");
        }

        long count = 0;
        if (router.HasDirectIndex)
        {
            for (int routeIndex = 0; routeIndex <= byte.MaxValue; routeIndex++)
            {
                count += CountVarKeyVarIdentityRouteTarget(router.GetRouteTargetAt(routeIndex), maxKeyLength, maxIdentityLength, visitedTargets, visitedRouters);
            }

            return count;
        }

        for (int routeIndex = 0; routeIndex < router.RouteCount; routeIndex++)
        {
            count += CountVarKeyVarIdentityRouteTarget(router.GetRouteTargetAt(routeIndex), maxKeyLength, maxIdentityLength, visitedTargets, visitedRouters);
        }

        return count;
    }

    private long CountVarKeyVarIdentityRouteTarget(
        long targetOffset,
        int maxKeyLength,
        int maxIdentityLength,
        HashSet<long> visitedTargets,
        HashSet<long> visitedRouters)
    {
        if (targetOffset == 0 || visitedTargets.Contains(targetOffset) || visitedRouters.Contains(targetOffset))
        {
            return 0;
        }

        VarKeyVarIdentityRouteTargetKind kind = ClassifyVarKeyVarIdentityRouteTarget(targetOffset);
        if (kind == VarKeyVarIdentityRouteTargetKind.Router)
        {
            return CountVarKeyVarIdentityIdentitiesFromRouter(targetOffset, maxKeyLength, maxIdentityLength, visitedTargets, visitedRouters);
        }

        if (!visitedTargets.Add(targetOffset))
        {
            return 0;
        }

        return kind switch
        {
            VarKeyVarIdentityRouteTargetKind.Shelf => ReadVarKeyVarIdentityShelfItemCountNarrow(targetOffset, maxKeyLength, maxIdentityLength),
            VarKeyVarIdentityRouteTargetKind.TerminalVarIdentityRoot => CountTerminalVarIdentityRootNarrow(targetOffset, TerminalIdentityRootLayout.ShapeVarKey),
            _ => throw new InvalidDataException("The routed VV count target is not a shelf, terminal root, or router.")
        };
    }

    private long ReadVarKeyVarIdentityShelfItemCountNarrow(long shelfOffset, int maxKeyLength, int maxIdentityLength)
    {
        if (durabilityBatchActive &&
            varKeyVarIdentityMutableBatchShelves.TryGetValue(shelfOffset, out VarKeyVarIdentityMutableShelf? mutableShelf))
        {
            return mutableShelf.LiveItemCount;
        }

        VarKeyVarIdentityShelfCountCacheKey cacheKey = new(shelfOffset, maxKeyLength, maxIdentityLength);
        if (TryGetVarKeyVarIdentityShelfCountCache(cacheKey, out long cachedCount))
        {
            return cachedCount;
        }

        Span<byte> header = stackalloc byte[VarKeyVarIdentityLayout.HeaderSize];
        kernel.Read(shelfOffset, header);
        if (VarKeyVarIdentityLayout.ReadMagic(header) != VarKeyVarIdentityLayout.Magic ||
            VarKeyVarIdentityLayout.ReadFormatVersion(header) != VarKeyVarIdentityLayout.FormatVersion ||
            VarKeyVarIdentityLayout.ReadHeaderSize(header) != VarKeyVarIdentityLayout.HeaderSize)
        {
            throw new InvalidDataException("The routed VV count shelf header is invalid.");
        }

        int shelfExtentSize = VarKeyVarIdentityLayout.ReadShelfExtentSize(header);
        _ = VarKeyVarIdentityProfile.Create(shelfExtentSize, maxKeyLength, maxIdentityLength);
        int count = VarKeyVarIdentityLayout.ReadItemCount(header);
        int slotStreamLength = VarKeyVarIdentityLayout.ReadSlotStreamLength(header);
        int slotCapacityBytes = VarKeyVarIdentityLayout.ReadSlotCapacityBytes(header);
        int recordArenaEnd = VarKeyVarIdentityLayout.ReadRecordArenaEnd(header);
        int expectedSlotCapacityBytes = VarKeyVarIdentityLayout.CalculateSlotCapacityBytes(shelfExtentSize);
        if (count < 0 ||
            count > expectedSlotCapacityBytes / VarKeyVarIdentityLayout.SlotSize ||
            slotStreamLength != checked(count * VarKeyVarIdentityLayout.SlotSize) ||
            slotCapacityBytes != expectedSlotCapacityBytes ||
            recordArenaEnd < VarKeyVarIdentityLayout.HeaderSize + slotCapacityBytes ||
            recordArenaEnd > shelfExtentSize)
        {
            throw new InvalidDataException("The routed VV count shelf metadata is invalid.");
        }

        StoreVarKeyVarIdentityShelfCountCache(cacheKey, count);
        return count;
    }

    /// <summary>
    /// Tries to read a session-local narrow count projection for one `VV` shelf.<br/>
    /// The cache key includes the physical shelf offset and max key/identity constraints so repeated count-only traversals can reuse validated shelf-header counts without loading tuple payload bytes.<br/>
    /// Entries are scoped to the DataKernel mutation version through the shared count-header cache invalidation boundary, so the cache never requires an extra persisted write or flush.<br/>
    /// </summary>
    /// <param name="key">The physical `VV` shelf count cache key.<br/></param>
    /// <param name="count">Receives the cached item count when a current entry is available.<br/></param>
    /// <returns><see langword="true"/> when a current count projection was found; otherwise <see langword="false"/>.<br/></returns>
    private bool TryGetVarKeyVarIdentityShelfCountCache(VarKeyVarIdentityShelfCountCacheKey key, out long count)
    {
        long mutationVersion = kernel.MutationVersion;
        lock (countHeaderCacheSync)
        {
            EnsureCountHeaderCacheVersionUnderLock(mutationVersion);
            if (varKeyVarIdentityShelfCountCache.TryGetValue(key, out count) &&
                kernel.MutationVersion == mutationVersion)
            {
                return true;
            }
        }

        count = 0;
        return false;
    }


    /// <summary>
    /// Stores one validated `VV` shelf item-count projection at the current raw-storage mutation version.<br/>
    /// The stored value comes from an existing shelf header that the current count operation already read, making this a free session-local reuse path rather than maintained persisted metadata.<br/>
    /// </summary>
    /// <param name="key">The physical `VV` shelf count cache key.<br/></param>
    /// <param name="count">The validated live item count read from the shelf header.<br/></param>
    private void StoreVarKeyVarIdentityShelfCountCache(VarKeyVarIdentityShelfCountCacheKey key, long count)
    {
        long mutationVersion = kernel.MutationVersion;
        lock (countHeaderCacheSync)
        {
            EnsureCountHeaderCacheVersionUnderLock(mutationVersion);
            varKeyVarIdentityShelfCountCache[key] = count;
        }
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
    /// Creates a prebuilt `VV` shelf without linking it to a router route.<br/>
    /// Bulk and validation construction can append final shelf images first, then wire those shelves through a chosen router shape such as a compressed multi-byte router.<br/>
    /// The supplied bytes are validated against the profile before reservation so malformed payloads cannot enter the routed varlen-key/varlen-identity graph.<br/>
    /// </summary>
    /// <param name="profile">The `VV` shelf profile used to validate and reserve the shelf extent.<br/></param>
    /// <param name="shelfBytes">The complete prebuilt shelf image.<br/></param>
    /// <returns>The created shelf offset and commit telemetry.<br/></returns>
    /// <exception cref="ArgumentException">Thrown when the supplied shelf image length does not match the profile.<br/></exception>
    /// <exception cref="InvalidDataException">Thrown when the shelf bytes are invalid.<br/></exception>
    internal (long ShelfOffset, DataKernelCommitTelemetry Commit) CreateVarKeyVarIdentityShelf(
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

        RawDataReservation shelfReservation = kernel.Reserve(profile.ShelfExtentSize);
        shelfBytes.CopyTo(shelfReservation.Span);

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
                        return new VarKeyVarIdentityRoutePathTarget(
                            new VarKeyVarIdentityRouteTarget(VarKeyVarIdentityRouteTargetKind.None, 0, directView.KeyDepth, directView.AllocationClassId),
                            routerOffset,
                            directPrefixByte,
                            directPrefixByte);
                    }

                    VarKeyVarIdentityRouteTargetKind directKind = ClassifyVarKeyVarIdentityRouteTarget(directTargetOffset);
                    if (directKind == VarKeyVarIdentityRouteTargetKind.Shelf ||
                        directKind == VarKeyVarIdentityRouteTargetKind.TerminalVarIdentityRoot)
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

                    throw new InvalidDataException("The routed VV target is not a shelf, terminal identity root, or router.");
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
                    if (multiByteKind == VarKeyVarIdentityRouteTargetKind.Shelf ||
                        multiByteKind == VarKeyVarIdentityRouteTargetKind.TerminalVarIdentityRoot)
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

                    throw new InvalidDataException("The routed VV target is not a shelf, terminal identity root, or router.");
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
                    return new VarKeyVarIdentityRoutePathTarget(
                        new VarKeyVarIdentityRouteTarget(VarKeyVarIdentityRouteTargetKind.None, 0, reader.KeyDepth, reader.AllocationClassId),
                        routerOffset,
                        prefixByte,
                        routeIndex);
                }

                VarKeyVarIdentityRouteTargetKind kind = ClassifyVarKeyVarIdentityRouteTarget(targetOffset);
                if (kind == VarKeyVarIdentityRouteTargetKind.Shelf ||
                    kind == VarKeyVarIdentityRouteTargetKind.TerminalVarIdentityRoot)
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

                throw new InvalidDataException("The routed VV target is not a shelf, terminal identity root, or router.");
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
        int maxRouterHops,
        bool descending = false)
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
            VarKeyVarIdentityProfile initialProfile = SelectInitialVarKeyVarIdentityProfile(maxKeyLength, maxIdentityLength, descending);
            _ = CreateVarKeyVarIdentityShelfAndLinkRootRoute(rootRouterOffset, rootPrefix, initialProfile);
            createdInitialShelfRoute = true;
        }

        VarKeyVarIdentityRoutePathTarget pathTarget = WalkVarKeyVarIdentityRoutePathTarget(rootRouterOffset, key, maxRouterHops);
        VarKeyVarIdentityRouteTarget target = pathTarget.Target;
        if (target.Kind == VarKeyVarIdentityRouteTargetKind.None)
        {
            VarKeyVarIdentityProfile coldProfile = SelectInitialVarKeyVarIdentityProfile(maxKeyLength, maxIdentityLength, descending);
            if (TryInsertVarKeyVarIdentityDirectColdRoute(
                rootRouterOffset,
                pathTarget.ParentRouterOffset,
                pathTarget.PrefixByte,
                coldProfile,
                key,
                identity,
                allowDuplicateKeys,
                out VarKeyVarIdentityRoutedInsertResult coldResult))
            {
                return coldResult;
            }

            return InsertWalkedRoutedVarKeyVarIdentity(
                rootRouterOffset,
                maxKeyLength,
                maxIdentityLength,
                key,
                identity,
                allowDuplicateKeys,
                maxRouterHops,
                descending);
        }

        if (target.Kind == VarKeyVarIdentityRouteTargetKind.TerminalVarIdentityRoot)
        {
            if (IsVarKeyVarIdentityTerminalRootForKey(target.Offset, key))
            {
                return InsertIntoVarKeyVarIdentityTerminalRoute(target.Offset, maxIdentityLength, key, identity, allowDuplicateKeys);
            }

            return SplitMismatchedVarKeyVarIdentityTerminalRoute(
                pathTarget,
                maxKeyLength,
                maxIdentityLength,
                key,
                identity,
                allowDuplicateKeys);
        }

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
            if (!TryPublishSharedShelfGrowth(
                SharedShelfGrowthShapeVarKeyVarIdentity,
                "VV",
                rootRouterOffset,
                pathTarget.ParentRouterOffset,
                pathTarget.RouteIndex,
                target.Offset,
                profile.ShelfExtentSize,
                grownProfile.ShelfExtentSize,
                grownShelf,
                deferRouterCacheInvalidation: false,
                out DataKernelCommitTelemetry telemetry,
                out long grownShelfOffset))
            {
                existingShelf.Release(clearShelfBytes: true);
                return InsertWalkedRoutedVarKeyVarIdentity(
                    rootRouterOffset,
                    maxKeyLength,
                    maxIdentityLength,
                    key,
                    identity,
                    allowDuplicateKeys,
                    maxRouterHops,
                    descending);
            }

            existingShelf.Release(clearShelfBytes: true);
            return new VarKeyVarIdentityRoutedInsertResult(
                VarKeyVarIdentityRoutedInsertKind.WalkedGrow,
                grownResult,
                target.Offset,
                grownShelfOffset,
                telemetry,
                beforeItemCount + 1,
                grownProfile.ShelfExtentSize,
                target.RouterDepth);
        }

        if (insertResult == VarKeyVarIdentityInsertResult.Full)
        {
            if (TryConvertVarKeyVarIdentityDuplicateRunToTerminalRoute(
                pathTarget,
                existingShelf,
                profile,
                key,
                identity,
                allowDuplicateKeys,
                out VarKeyVarIdentityRoutedInsertResult terminalResult))
            {
                existingShelf.Release(clearShelfBytes: true);
                return terminalResult;
            }

            // Mixed-shelf extraction is intentionally not published here. Its shared fallback shelf can be
            // referenced through noncontiguous aliases after the exact terminal route is installed. The
            // ordinary transform below first separates key ranges; same-key shelves can then use the safe
            // cold-fallback terminal conversion without creating an ambiguous parent ownership range.

            Span<byte> parentRouterBytes = stackalloc byte[RouterLayout.Size];
            ReadRouterPageUsingArenaCache(pathTarget.ParentRouterOffset, parentRouterBytes);
            RouterReader parentReader = new(parentRouterBytes);
            if (!parentReader.IsValid ||
                !TryGetVarKeyParentTargetRange(
                    parentReader,
                    pathTarget.RouteIndex,
                    target.Offset,
                    out bool parentHasDirectIndex,
                    out int parentRangeStart,
                    out int parentRangeEnd,
                    out ushort parentFinalKeyDepth))
            {
                varKeyVarIdentityMutableBatchShelves.Remove(target.Offset);
                existingShelf.Release(clearShelfBytes: true);
                return InsertWalkedRoutedVarKeyVarIdentity(
                    rootRouterOffset,
                    maxKeyLength,
                    maxIdentityLength,
                    key,
                    identity,
                    allowDuplicateKeys,
                    maxRouterHops,
                    descending);
            }

            ushort childRouterKeyDepth = parentRangeStart < parentRangeEnd
                ? parentFinalKeyDepth
                : checked((ushort)(parentFinalKeyDepth + 1));
            byte rightPrefixByte = GetVarKeyScalar8Prefix(key, childRouterKeyDepth);
            varKeyVarIdentityMutableBatchShelves.Remove(target.Offset);
            (long childRouterOffset,
                long leftShelfOffset,
                long rightShelfOffset,
                int leftItemCount,
                int rightItemCount,
                VarKeyVarIdentityInsertResult transformResult,
                DataKernelCommitTelemetry transformCommit) splitResult;
            try
            {
                splitResult = SplitRoutedVarKeyVarIdentityByShelfTransform(
                    pathTarget.ParentRouterOffset,
                    parentHasDirectIndex,
                    parentRangeStart,
                    parentRangeEnd,
                    parentFinalKeyDepth,
                    target.Offset,
                    existingShelf,
                    rightPrefixByte,
                    profile,
                    key,
                    identity,
                    target.AllocationClassId,
                    childRouterKeyDepth);
            }
            catch (InvalidDataException ex) when (ex.Message.Contains("requires at least two distinct prefixes", StringComparison.Ordinal))
            {
                childRouterKeyDepth = 0;
                rightPrefixByte = GetVarKeyScalar8Prefix(key, childRouterKeyDepth);
                splitResult = SplitRoutedVarKeyVarIdentityByShelfTransform(
                    pathTarget.ParentRouterOffset,
                    parentHasDirectIndex,
                    parentRangeStart,
                    parentRangeEnd,
                    parentFinalKeyDepth,
                    target.Offset,
                    existingShelf,
                    rightPrefixByte,
                    profile,
                    key,
                    identity,
                    target.AllocationClassId,
                    childRouterKeyDepth);
            }

            (
                long childRouterOffset,
                long leftShelfOffset,
                long rightShelfOffset,
                _,
                _,
                VarKeyVarIdentityInsertResult transformResult,
                DataKernelCommitTelemetry transformCommit) = splitResult;
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

    /// <summary>
    /// Inserts one raw identity into an exact-key terminal `VV` duplicate route.<br/>
    /// The root stores the raw variable key once; terminal shelves store only sorted raw identities so duplicate-key pressure does not repeat key bytes per row.<br/>
    /// The method prefers tail append and local shelf insertion before falling back to a full terminal-chain rewrite for unusual out-of-order cases.<br/>
    /// </summary>
    /// <param name="rootOffset">The terminal identity root offset reached by the route walker.</param>
    /// <param name="maxIdentityLength">The maximum raw identity length accepted by the owning index.</param>
    /// <param name="key">The raw variable key expected on the terminal root.</param>
    /// <param name="identity">The raw variable identity to insert.</param>
    /// <param name="allowDuplicateKeys">Whether the owning index allows duplicate keys with different identities.</param>
    /// <returns>The routed insert result for the terminal route mutation.</returns>
    private VarKeyVarIdentityRoutedInsertResult InsertIntoVarKeyVarIdentityTerminalRoute(
        long rootOffset,
        int maxIdentityLength,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys)
    {
        if (!allowDuplicateKeys)
        {
            return new VarKeyVarIdentityRoutedInsertResult(
                VarKeyVarIdentityRoutedInsertKind.KeyConflict,
                VarKeyVarIdentityInsertResult.KeyConflict,
                rootOffset,
                0,
                default,
                0,
                0);
        }

        if ((uint)identity.Length == 0 || identity.Length > maxIdentityLength)
        {
            return new VarKeyVarIdentityRoutedInsertResult(
                VarKeyVarIdentityRoutedInsertKind.Invalid,
                VarKeyVarIdentityInsertResult.Invalid,
                rootOffset,
                0,
                default,
                0,
                0);
        }

        byte[] rootBytes = ReadTerminalIdentityRootBytes(rootOffset);
        if (TerminalIdentityRootLayout.ReadShape(rootBytes) != TerminalIdentityRootLayout.ShapeVarKey ||
            !IsTerminalIdentityRootForKey(rootBytes, key, out long firstShelfOffset))
        {
            throw new InvalidDataException("The routed VV terminal var identity root does not match the inserted key.");
        }

        int shelfExtentSize = TerminalIdentityRootLayout.ReadShelfExtentSize(rootBytes);
        long tailShelfOffset = TerminalIdentityRootLayout.ReadTailShelfOffset(rootBytes);
        if (TryAppendScalar8VarIdentityTerminalTail(rootOffset, rootBytes, firstShelfOffset, tailShelfOffset, shelfExtentSize, identity, out DataKernelCommitTelemetry appendTelemetry))
        {
            return new VarKeyVarIdentityRoutedInsertResult(
                VarKeyVarIdentityRoutedInsertKind.WalkedDuplicateRunOverflow,
                VarKeyVarIdentityInsertResult.Inserted,
                rootOffset,
                rootOffset,
                appendTelemetry,
                0,
                shelfExtentSize);
        }

        if (TryInsertIntoScalar8VarIdentityTerminalChain(rootOffset, rootBytes, firstShelfOffset, shelfExtentSize, identity, out Scalar8VarIdentityRoutedInsertResult chainInsertResult))
        {
            return new VarKeyVarIdentityRoutedInsertResult(
                chainInsertResult.InsertResult == Scalar8VarIdentityInsertResult.AlreadyPresent ? VarKeyVarIdentityRoutedInsertKind.NoOp : VarKeyVarIdentityRoutedInsertKind.WalkedDuplicateRunOverflow,
                MapScalar8VarIdentityInsertResult(chainInsertResult.InsertResult),
                chainInsertResult.PrimaryOffset,
                chainInsertResult.NewShelfOffset,
                chainInsertResult.Commit,
                chainInsertResult.TargetShelfItemCount,
                chainInsertResult.TargetShelfExtentSize);
        }

        using PooledTerminalVarIdentitySet identities = ReadScalar8VarIdentityTerminalIdentitiesPooled(rootOffset, key, shelfExtentSize);
        int insertIndex = LowerBoundTerminalVarIdentity(identities, identity, rootBytes[TerminalIdentityRootLayout.SortDirectionOffset] != 0);
        if (insertIndex < identities.Count && VarKeyVarIdentityLayout.IdentityBytesEqual(identities.ReadAt(insertIndex), identity))
        {
            return new VarKeyVarIdentityRoutedInsertResult(
                VarKeyVarIdentityRoutedInsertKind.NoOp,
                VarKeyVarIdentityInsertResult.AlreadyPresent,
                rootOffset,
                0,
                default,
                identities.Count,
                shelfExtentSize);
        }

        identities.InsertAt(insertIndex, identity);
        DataKernelCommitTelemetry rewriteTelemetry = RewriteScalar8VarIdentityTerminalRoute(
            rootOffset,
            TerminalIdentityRootLayout.ShapeVarKey,
            key,
            shelfExtentSize,
            identities);
        return new VarKeyVarIdentityRoutedInsertResult(
            VarKeyVarIdentityRoutedInsertKind.WalkedDuplicateRunOverflow,
            VarKeyVarIdentityInsertResult.Inserted,
            rootOffset,
            rootOffset,
            rewriteTelemetry,
            identities.Count,
            shelfExtentSize);
    }

    /// <summary>
    /// Converts one full same-key `VV` shelf into an exact-key terminal var-identity route.<br/>
    /// The exact-key chain leaves nonmatching branches unset so later nearby keys lazily receive independent cold shelves instead of sharing one fallback shelf through noncontiguous aliases.<br/>
    /// This is the varlen-key equivalent of the `SV8` duplicate-run terminal shape and avoids repeated key payload storage under hot duplicate keys.<br/>
    /// </summary>
    /// <param name="pathTarget">The routed path target that reached the full source shelf.</param>
    /// <param name="existingShelf">The full mutable source shelf.</param>
    /// <param name="profile">The source shelf profile.</param>
    /// <param name="key">The raw variable key being inserted.</param>
    /// <param name="identity">The raw variable identity being inserted.</param>
    /// <param name="allowDuplicateKeys">Whether the owning index allows duplicate keys with different identities.</param>
    /// <param name="result">Receives the routed insert result when conversion or duplicate no-op is performed.</param>
    /// <returns><see langword="true"/> when this method handled the full shelf; otherwise <see langword="false"/>.</returns>
    private bool TryConvertVarKeyVarIdentityDuplicateRunToTerminalRoute(
        VarKeyVarIdentityRoutePathTarget pathTarget,
        VarKeyVarIdentityMutableShelf existingShelf,
        VarKeyVarIdentityProfile profile,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys,
        out VarKeyVarIdentityRoutedInsertResult result)
    {
        result = default;
        if (!allowDuplicateKeys ||
            existingShelf.ItemCount == 0 ||
            !existingShelf.ReadKeyAt(0).SequenceEqual(key) ||
            !existingShelf.ReadKeyAt(existingShelf.ItemCount - 1).SequenceEqual(key))
        {
            return false;
        }

        for (int i = 1; i + 1 < existingShelf.ItemCount; i++)
        {
            if (!existingShelf.ReadKeyAt(i).SequenceEqual(key))
            {
                return false;
            }
        }

        using PooledTerminalVarIdentitySet identities = CollectVarKeyVarIdentitySameKeyShelfPooled(existingShelf, key, identity, out bool incomingAdded, out bool alreadyPresent);
        if (alreadyPresent)
        {
            result = new VarKeyVarIdentityRoutedInsertResult(
                VarKeyVarIdentityRoutedInsertKind.NoOp,
                VarKeyVarIdentityInsertResult.AlreadyPresent,
                pathTarget.Target.Offset,
                0,
                default,
                identities.Count,
                profile.ShelfExtentSize,
                pathTarget.Target.RouterDepth);
            return true;
        }

        if (!incomingAdded)
        {
            int insertIndex = LowerBoundTerminalVarIdentity(identities, identity, profile.Descending);
            identities.InsertAt(insertIndex, identity);
        }

        int terminalShelfExtentSize = SelectVarKeyVarIdentityTerminalShelfExtentSize(profile.ShelfExtentSize, identities);
        long rootOffset = CreateScalar8VarIdentityTerminalRoute(
            TerminalIdentityRootLayout.ShapeVarKey,
            key,
            terminalShelfExtentSize,
            identities,
            profile.Descending);
        varKeyVarIdentityMutableBatchShelves.Remove(pathTarget.Target.Offset);
        long replacementOffset = CreateVarKeyVarIdentityTerminalRouterChain(
            firstDepth: 0,
            pathTarget.Target.AllocationClassId,
            key,
            profile.MaxKeyLength,
            emptyShelfOffset: 0,
            rootOffset);
        RepointMatchingRoutes(
            pathTarget.ParentRouterOffset,
            pathTarget.Target.Offset,
            replacementOffset);
        kernel.StageExtentRetirement(pathTarget.Target.Offset, profile.ShelfExtentSize);
        DataKernelCommitTelemetry telemetry = CommitAndInvalidateRouterReadCache();
        result = new VarKeyVarIdentityRoutedInsertResult(
            VarKeyVarIdentityRoutedInsertKind.WalkedDuplicateRunOverflow,
            VarKeyVarIdentityInsertResult.Inserted,
            pathTarget.Target.Offset,
            replacementOffset,
            telemetry,
            identities.Count,
            terminalShelfExtentSize,
            pathTarget.Target.RouterDepth);
        return true;
    }

    /// <summary>
    /// Extracts one hot duplicate key from a mixed full `VV` shelf into an exact-key terminal identity route.<br/>
    /// The source shelf is rewritten with only nonmatching tuples, while matching-key identities are stored in compact terminal var-identity shelves under a root that stores the key once.<br/>
    /// This handles the non-exhausted duplicate-pressure case where a general split would keep chasing identical key bytes and eventually fail to find a dividing prefix.<br/>
    /// </summary>
    /// <param name="pathTarget">The routed path target that reached the mixed full shelf.</param>
    /// <param name="existingShelf">The mutable source shelf.</param>
    /// <param name="profile">The source shelf profile.</param>
    /// <param name="key">The duplicate raw variable key to extract.</param>
    /// <param name="identity">The incoming raw variable identity.</param>
    /// <param name="allowDuplicateKeys">Whether the owning index allows duplicate keys with different identities.</param>
    /// <param name="result">Receives the routed insert result when extraction or duplicate no-op is performed.</param>
    /// <returns><see langword="true"/> when this method handled the full shelf; otherwise <see langword="false"/>.</returns>
    private bool TryExtractVarKeyVarIdentityDuplicateKeyToTerminalRoute(
        VarKeyVarIdentityRoutePathTarget pathTarget,
        VarKeyVarIdentityMutableShelf existingShelf,
        VarKeyVarIdentityProfile profile,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys,
        out VarKeyVarIdentityRoutedInsertResult result)
    {
        const int DuplicatePressureItemCount = 16;

        result = default;
        if (!allowDuplicateKeys || existingShelf.ItemCount == 0)
        {
            return false;
        }

        int matchingCount = 0;
        for (int i = 0; i < existingShelf.ItemCount; i++)
        {
            if (existingShelf.ReadKeyAt(i).SequenceEqual(key))
            {
                matchingCount++;
            }
        }

        if (matchingCount < DuplicatePressureItemCount)
        {
            return false;
        }

        int remainderCount = existingShelf.ItemCount - matchingCount;
        int[] rentedKeyOffsets = ArrayPool<int>.Shared.Rent(Math.Max(1, remainderCount));
        int[] rentedKeyLengths = ArrayPool<int>.Shared.Rent(Math.Max(1, remainderCount));
        int[] rentedIdentityOffsets = ArrayPool<int>.Shared.Rent(Math.Max(1, remainderCount));
        int[] rentedIdentityLengths = ArrayPool<int>.Shared.Rent(Math.Max(1, remainderCount));
        try
        {
            Span<int> keyOffsets = rentedKeyOffsets.AsSpan(0, remainderCount);
            Span<int> keyLengths = rentedKeyLengths.AsSpan(0, remainderCount);
            Span<int> identityOffsets = rentedIdentityOffsets.AsSpan(0, remainderCount);
            Span<int> identityLengths = rentedIdentityLengths.AsSpan(0, remainderCount);
            using PooledTerminalVarIdentitySet terminalIdentities = PooledTerminalVarIdentitySet.Rent(matchingCount + 1, profile.ShelfExtentSize + identity.Length);
            int remainderIndex = 0;
            bool incomingAdded = false;
            bool alreadyPresent = false;
            for (int i = 0; i < existingShelf.ItemCount; i++)
            {
                ReadOnlySpan<byte> currentKey = existingShelf.ReadKeyAt(i);
                ReadOnlySpan<byte> currentIdentity = existingShelf.ReadIdentityAt(i);
                if (!currentKey.SequenceEqual(key))
                {
                    existingShelf.ReadKeyLocationAt(i, out int currentKeyOffset, out int currentKeyLength);
                    existingShelf.ReadIdentityLocationAt(i, out int currentIdentityOffset, out int currentIdentityLength);
                    keyOffsets[remainderIndex] = currentKeyOffset;
                    keyLengths[remainderIndex] = currentKeyLength;
                    identityOffsets[remainderIndex] = currentIdentityOffset;
                    identityLengths[remainderIndex] = currentIdentityLength;
                    remainderIndex++;
                    continue;
                }

                int order = VarKeyVarIdentityLayout.CompareIdentityBytes(identity, currentIdentity);
                if (!incomingAdded && (profile.Descending ? order > 0 : order < 0))
                {
                    terminalIdentities.Add(identity);
                    incomingAdded = true;
                }

                if (order == 0)
                {
                    alreadyPresent = true;
                }

                terminalIdentities.Add(currentIdentity);
            }

            if (remainderIndex != remainderCount)
            {
                throw new InvalidDataException("The VV duplicate-key extraction remainder map did not cover the expected tuple count.");
            }

            if (alreadyPresent)
            {
                result = new VarKeyVarIdentityRoutedInsertResult(
                    VarKeyVarIdentityRoutedInsertKind.NoOp,
                    VarKeyVarIdentityInsertResult.AlreadyPresent,
                    pathTarget.Target.Offset,
                    0,
                    default,
                    terminalIdentities.Count,
                    profile.ShelfExtentSize,
                    pathTarget.Target.RouterDepth);
                return true;
            }

            if (!incomingAdded)
            {
                terminalIdentities.Add(identity);
            }

            byte[] remainderShelfBytes = remainderCount == 0
                ? VarKeyVarIdentity.CreateEmpty(profile)
                : BuildVarKeyVarIdentityShelfFromSources(
                    existingShelf.Bytes,
                    keyOffsets,
                    keyLengths,
                    identityOffsets,
                    identityLengths,
                    profile);
            int terminalShelfExtentSize = SelectVarKeyVarIdentityTerminalShelfExtentSize(profile.ShelfExtentSize, terminalIdentities);
            long terminalRootOffset = CreateScalar8VarIdentityTerminalRoute(
                TerminalIdentityRootLayout.ShapeVarKey,
                key,
                terminalShelfExtentSize,
                terminalIdentities,
                profile.Descending);
            varKeyVarIdentityMutableBatchShelves.Remove(pathTarget.Target.Offset);
            RawDataReservation sourceRewrite = kernel.ReserveAt(pathTarget.Target.Offset, profile.ShelfExtentSize);
            remainderShelfBytes.CopyTo(sourceRewrite.Span);
            long replacementOffset = CreateVarKeyVarIdentityTerminalRouterChain(
                firstDepth: 0,
                pathTarget.Target.AllocationClassId,
                key,
                profile.MaxKeyLength,
                pathTarget.Target.Offset,
                terminalRootOffset);
            RepointExactRouteAndAliases(
                pathTarget.ParentRouterOffset,
                pathTarget.Target.Offset,
                pathTarget.RouteIndex,
                replacementOffset,
                pathTarget.Target.Offset);
            DataKernelCommitTelemetry telemetry = CommitAndInvalidateRouterReadCache();
            result = new VarKeyVarIdentityRoutedInsertResult(
                VarKeyVarIdentityRoutedInsertKind.WalkedDuplicateRunOverflow,
                VarKeyVarIdentityInsertResult.Inserted,
                pathTarget.Target.Offset,
                replacementOffset,
                telemetry,
                terminalIdentities.Count,
                terminalShelfExtentSize,
                pathTarget.Target.RouterDepth);
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
    /// Builds one ordinary `VV` shelf from source descriptors that point into an existing shelf image.<br/>
    /// The descriptors are already in sorted tuple order, so the writer emits slots and records in one pass without materializing key or identity payload arrays.<br/>
    /// </summary>
    /// <param name="existingShelfBytes">The source shelf bytes that own the key and identity payload slices.</param>
    /// <param name="keyOffsets">The key payload offsets in <paramref name="existingShelfBytes"/>.</param>
    /// <param name="keyLengths">The key payload lengths matching <paramref name="keyOffsets"/>.</param>
    /// <param name="identityOffsets">The identity payload offsets in <paramref name="existingShelfBytes"/>.</param>
    /// <param name="identityLengths">The identity payload lengths matching <paramref name="identityOffsets"/>.</param>
    /// <param name="profile">The target shelf profile.</param>
    /// <returns>The rebuilt ordinary `VV` shelf bytes.</returns>
    private static byte[] BuildVarKeyVarIdentityShelfFromSources(
        byte[] existingShelfBytes,
        ReadOnlySpan<int> keyOffsets,
        ReadOnlySpan<int> keyLengths,
        ReadOnlySpan<int> identityOffsets,
        ReadOnlySpan<int> identityLengths,
        VarKeyVarIdentityProfile profile)
    {
        if (keyOffsets.Length != keyLengths.Length ||
            keyOffsets.Length != identityOffsets.Length ||
            keyOffsets.Length != identityLengths.Length)
        {
            throw new InvalidDataException("The VV shelf source descriptors are incomplete.");
        }

        byte[] shelfBytes = new byte[profile.ShelfExtentSize];
        VarKeyVarIdentityLayout.Initialize(shelfBytes, profile);
        int slotCursor = VarKeyVarIdentityLayout.HeaderSize;
        int slotCapacityBytes = VarKeyVarIdentityLayout.ReadSlotCapacityBytes(shelfBytes);
        int recordCursor = VarKeyVarIdentityLayout.HeaderSize + slotCapacityBytes;
        for (int i = 0; i < keyOffsets.Length; i++)
        {
            ReadOnlySpan<byte> currentKey = existingShelfBytes.AsSpan(keyOffsets[i], keyLengths[i]);
            ReadOnlySpan<byte> currentIdentity = existingShelfBytes.AsSpan(identityOffsets[i], identityLengths[i]);
            if (slotCursor + VarKeyVarIdentityLayout.SlotSize > VarKeyVarIdentityLayout.HeaderSize + slotCapacityBytes)
            {
                throw new InvalidDataException("The VV extracted remainder shelf exceeded slot capacity.");
            }

            int recordLength = VarKeyVarIdentityLayout.GetNewRecordLength(currentKey.Length, currentIdentity.Length);
            if (recordCursor + recordLength > profile.ShelfExtentSize)
            {
                throw new InvalidDataException("The VV extracted remainder shelf exceeded record capacity.");
            }

            VarKeyVarIdentityLayout.WriteRecord(shelfBytes, recordCursor, currentKey, currentIdentity);
            VarKeyVarIdentityLayout.WriteSlotRecordOffset(shelfBytes, slotCursor, recordCursor);
            VarKeyVarIdentityLayout.WriteSlotKeyPrefix(shelfBytes, slotCursor, VarKeyVarIdentityLayout.CreateKeyPrefix(currentKey));
            slotCursor += VarKeyVarIdentityLayout.SlotSize;
            recordCursor += recordLength;
        }

        VarKeyVarIdentityLayout.WriteItemCount(shelfBytes, keyOffsets.Length);
        VarKeyVarIdentityLayout.WriteSlotStreamLength(shelfBytes, slotCursor - VarKeyVarIdentityLayout.HeaderSize);
        VarKeyVarIdentityLayout.WriteRecordArenaEnd(shelfBytes, recordCursor);
        return shelfBytes;
    }

    /// <summary>
    /// Collects same-key `VV` shelf identities into the pooled terminal identity workspace in sorted identity order.<br/>
    /// The source shelf is already sorted by key then identity, so the method only performs a single merge pass for the incoming identity and does not allocate per-row identity arrays.<br/>
    /// </summary>
    /// <param name="existingShelf">The same-key source shelf.</param>
    /// <param name="key">The raw variable key shared by every source row.</param>
    /// <param name="identity">The incoming identity to merge.</param>
    /// <param name="incomingAdded">Receives whether the incoming identity was inserted during the merge pass.</param>
    /// <param name="alreadyPresent">Receives whether the exact key/identity tuple already existed.</param>
    /// <returns>A pooled terminal identity workspace owned by the caller.</returns>
    private static PooledTerminalVarIdentitySet CollectVarKeyVarIdentitySameKeyShelfPooled(
        VarKeyVarIdentityMutableShelf existingShelf,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> identity,
        out bool incomingAdded,
        out bool alreadyPresent)
    {
        incomingAdded = false;
        alreadyPresent = false;
        PooledTerminalVarIdentitySet identities = PooledTerminalVarIdentitySet.Rent(existingShelf.ItemCount + 1, existingShelf.Profile.ShelfExtentSize + identity.Length);
        for (int i = 0; i < existingShelf.ItemCount; i++)
        {
            ReadOnlySpan<byte> currentKey = existingShelf.ReadKeyAt(i);
            if (!currentKey.SequenceEqual(key))
            {
                throw new InvalidDataException("The VV terminal conversion source shelf contained more than one key.");
            }

            ReadOnlySpan<byte> currentIdentity = existingShelf.ReadIdentityAt(i);
            int order = VarKeyVarIdentityLayout.CompareIdentityBytes(identity, currentIdentity);
            if (!incomingAdded && (existingShelf.Profile.Descending ? order > 0 : order < 0))
            {
                identities.Add(identity);
                incomingAdded = true;
            }

            if (order == 0)
            {
                alreadyPresent = true;
            }

            identities.Add(currentIdentity);
        }

        if (!incomingAdded && !alreadyPresent)
        {
            identities.Add(identity);
            incomingAdded = true;
        }

        return identities;
    }

    /// <summary>
    /// Creates a byte-depth exact-key router chain for a terminal `VV` duplicate route.<br/>
    /// Every branch that does not match the terminal key byte at the current depth routes to the cleared ordinary source shelf, preserving future inserts for nearby but distinct keys.<br/>
    /// Short keys include their missing-byte zero route so a longer key sharing the same prefix cannot enter the exact-key terminal root.<br/>
    /// </summary>
    /// <param name="firstDepth">The first raw-key byte depth below the parent route.</param>
    /// <param name="allocationClassId">The allocation class id written to each created router.</param>
    /// <param name="key">The raw variable key owned by the terminal route.</param>
    /// <param name="maxKeyLength">The maximum raw variable key length accepted by the owning index.</param>
    /// <param name="emptyShelfOffset">The ordinary fallback shelf target for nonmatching branches, or zero to leave those branches cold and unset.</param>
    /// <param name="terminalRootOffset">The exact-key terminal identity root offset.</param>
    /// <returns>The top router offset that should replace the parent route target.</returns>
    private long CreateVarKeyVarIdentityTerminalRouterChain(
        ushort firstDepth,
        ushort allocationClassId,
        ReadOnlySpan<byte> key,
        int maxKeyLength,
        long emptyShelfOffset,
        long terminalRootOffset)
    {
        int terminalDepth = Math.Min(key.Length, maxKeyLength - 1);
        long nextTargetOffset = terminalRootOffset;
        for (int depth = terminalDepth; depth >= firstDepth; depth--)
        {
            long[] targets = emptyShelfOffset == 0
                ? new long[RouterLayout.MaxOneByteRouteCount]
                : CreateFilledScalar8Scalar8RouteTargets(emptyShelfOffset);
            targets[GetVarKeyScalar8Prefix(key, depth)] = nextTargetOffset;
            RawDataReservation reservation = kernel.Reserve(RouterLayout.Size);
            RouterWriter writer = new(reservation.Span);
            writer.InitializeExpandedOneByte(checked((ushort)depth), allocationClassId, targets);
            nextTargetOffset = reservation.Extent.Offset;
        }

        return nextTargetOffset;
    }

    /// <summary>
    /// Selects the compact terminal identity shelf extent for a `VV` duplicate route.<br/>
    /// Terminal shelves start at 4 KB and grow only enough to hold the largest identity payload while never exceeding the source shelf extent selected by write policy.<br/>
    /// </summary>
    /// <param name="sourceShelfExtentSize">The full source shelf extent size.</param>
    /// <param name="identities">The pooled sorted identity workspace.</param>
    /// <returns>The terminal var-identity shelf extent size.</returns>
    private static int SelectVarKeyVarIdentityTerminalShelfExtentSize(int sourceShelfExtentSize, PooledTerminalVarIdentitySet identities)
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

    /// <summary>
    /// Maps the shared scalar-varidentity terminal insert result enum into the `VV` insert result enum.<br/>
    /// The terminal identity shelf code is byte-oriented and reused by `VV`; this mapper keeps the public routed result shape-specific without duplicating the shelf mutation logic.<br/>
    /// </summary>
    /// <param name="result">The shared terminal insert result to map.</param>
    /// <returns>The corresponding `VV` insert result.</returns>
    private static VarKeyVarIdentityInsertResult MapScalar8VarIdentityInsertResult(Scalar8VarIdentityInsertResult result)
        => result switch
        {
            Scalar8VarIdentityInsertResult.Inserted => VarKeyVarIdentityInsertResult.Inserted,
            Scalar8VarIdentityInsertResult.AlreadyPresent => VarKeyVarIdentityInsertResult.AlreadyPresent,
            Scalar8VarIdentityInsertResult.KeyConflict => VarKeyVarIdentityInsertResult.KeyConflict,
            Scalar8VarIdentityInsertResult.Full => VarKeyVarIdentityInsertResult.Full,
            _ => VarKeyVarIdentityInsertResult.Invalid
        };

    /// <summary>
    /// Checks whether a routed `VV` terminal identity root owns the incoming encoded variable key.<br/>
    /// Terminal roots are exact-key shapes; a nonmatching key must be split away before insertion can continue safely.<br/>
    /// </summary>
    /// <param name="rootOffset">The terminal identity root offset reached by routing.</param>
    /// <param name="key">The encoded variable key expected on the root.</param>
    /// <returns><see langword="true"/> when the terminal root key matches <paramref name="key"/>; otherwise <see langword="false"/>.</returns>
    private bool IsVarKeyVarIdentityTerminalRootForKey(long rootOffset, ReadOnlySpan<byte> key)
    {
        byte[] rootBytes = ReadTerminalIdentityRootBytes(rootOffset);
        return TerminalIdentityRootLayout.ReadShape(rootBytes) == TerminalIdentityRootLayout.ShapeVarKey &&
            IsTerminalIdentityRootForKey(rootBytes, key, out _);
    }

    /// <summary>
    /// Splits a route that reached a `VV` exact-key terminal identity root for a different variable key.<br/>
    /// The existing terminal root remains the owner of its stored key, while the incoming key is inserted into a new ordinary `VV` shelf and byte routing separates the two keys at their first remaining difference.<br/>
    /// This preserves exact-key correctness after hot duplicate extraction creates terminal roots under route prefixes that later receive nearby keys.<br/>
    /// </summary>
    /// <param name="pathTarget">The routed path target that reached the mismatched terminal root.</param>
    /// <param name="maxKeyLength">The maximum encoded key length accepted by the owning index.</param>
    /// <param name="maxIdentityLength">The maximum raw identity length accepted by the owning index.</param>
    /// <param name="key">The incoming encoded variable key.</param>
    /// <param name="identity">The incoming raw variable identity.</param>
    /// <param name="allowDuplicateKeys">Whether duplicate keys with different identities are allowed.</param>
    /// <returns>The routed insert result for the incoming tuple.</returns>
    private VarKeyVarIdentityRoutedInsertResult SplitMismatchedVarKeyVarIdentityTerminalRoute(
        VarKeyVarIdentityRoutePathTarget pathTarget,
        int maxKeyLength,
        int maxIdentityLength,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys)
    {
        if ((uint)key.Length == 0 || key.Length > maxKeyLength || (uint)identity.Length == 0 || identity.Length > maxIdentityLength)
        {
            return new VarKeyVarIdentityRoutedInsertResult(
                VarKeyVarIdentityRoutedInsertKind.Invalid,
                VarKeyVarIdentityInsertResult.Invalid,
                pathTarget.Target.Offset,
                0,
                default,
                0,
                0,
                pathTarget.Target.RouterDepth);
        }

        byte[] rootBytes = ReadTerminalIdentityRootBytes(pathTarget.Target.Offset);
        if (TerminalIdentityRootLayout.ReadShape(rootBytes) != TerminalIdentityRootLayout.ShapeVarKey)
        {
            throw new InvalidDataException("The routed VV terminal target is not a VV terminal identity root.");
        }

        int terminalKeyLength = TerminalIdentityRootLayout.ReadKeyLength(rootBytes);
        byte[] terminalKey = rootBytes.AsSpan(TerminalIdentityRootLayout.KeyBytesOffset, terminalKeyLength).ToArray();
        VarKeyVarIdentityProfile profile = VarKeyVarIdentityProfile.Create(4 * 1024, maxKeyLength, maxIdentityLength) with { Descending = rootBytes[TerminalIdentityRootLayout.SortDirectionOffset] != 0 };
        byte[] incomingShelf = VarKeyVarIdentity.CreateEmpty(profile);
        VarKeyVarIdentityInsertResult insertResult = VarKeyVarIdentity.InsertWithMutationHintInPlace(
            incomingShelf,
            profile,
            key,
            identity,
            allowDuplicateKeys,
            hintStartDepth: pathTarget.Target.RouterDepth,
            maxHintBytes: 0,
            out _,
            out byte[] rewrittenIncomingShelf);
        if (insertResult != VarKeyVarIdentityInsertResult.Inserted)
        {
            return new VarKeyVarIdentityRoutedInsertResult(
                insertResult == VarKeyVarIdentityInsertResult.KeyConflict ? VarKeyVarIdentityRoutedInsertKind.KeyConflict : VarKeyVarIdentityRoutedInsertKind.Invalid,
                insertResult,
                pathTarget.Target.Offset,
                0,
                default,
                0,
                profile.ShelfExtentSize,
                pathTarget.Target.RouterDepth);
        }

        RawDataReservation incomingShelfReservation = kernel.Reserve(profile.ShelfExtentSize);
        rewrittenIncomingShelf.CopyTo(incomingShelfReservation.Span);
        ushort firstDepth = 0;
        long replacementOffset;
        try
        {
            replacementOffset = CreateVarKeyVarIdentityTerminalMismatchRouterChain(
                firstDepth,
                pathTarget.Target.AllocationClassId,
                terminalKey,
                pathTarget.Target.Offset,
                key,
                incomingShelfReservation.Extent.Offset,
                maxKeyLength);
        }
        catch (InvalidDataException ex) when (ex.Message.Contains("remaining variable-key route is exhausted", StringComparison.Ordinal))
        {
            kernel.StageExtentRetirement(incomingShelfReservation.Extent.Offset, profile.ShelfExtentSize);
            return DeterminalizeMismatchedVarKeyVarIdentityRoute(
                pathTarget,
                maxKeyLength,
                maxIdentityLength,
                terminalKey,
                key,
                identity,
                allowDuplicateKeys);
        }

        RepointMatchingRoutes(pathTarget.ParentRouterOffset, pathTarget.Target.Offset, replacementOffset);
        DataKernelCommitTelemetry telemetry = CommitAndInvalidateRouterReadCache();
        return new VarKeyVarIdentityRoutedInsertResult(
            VarKeyVarIdentityRoutedInsertKind.WalkedShelfTransformSplit,
            VarKeyVarIdentityInsertResult.Inserted,
            incomingShelfReservation.Extent.Offset,
            replacementOffset,
            telemetry,
            1,
            profile.ShelfExtentSize,
            pathTarget.Target.RouterDepth,
            firstDepth);
    }

    /// <summary>
    /// Creates a byte-router chain that separates an existing exact-key terminal `VV` target from one incoming nonmatching key.<br/>
    /// Nonmatching branches use the incoming ordinary shelf as fallback so later nearby keys keep a mutable landing point instead of entering the exact-key terminal route.<br/>
    /// </summary>
    /// <param name="firstDepth">The first encoded-key byte depth below the parent route.</param>
    /// <param name="allocationClassId">The router allocation class id to persist on created routers.</param>
    /// <param name="terminalKey">The encoded key stored by the existing terminal root.</param>
    /// <param name="terminalTargetOffset">The existing exact-key terminal root offset.</param>
    /// <param name="incomingKey">The incoming encoded key that must not enter the terminal root.</param>
    /// <param name="incomingShelfOffset">The ordinary shelf containing the incoming tuple and serving as fallback.</param>
    /// <param name="maxKeyLength">The maximum encoded key length accepted by the owning index.</param>
    /// <returns>The top router offset that should replace the parent route target.</returns>
    private long CreateVarKeyVarIdentityTerminalMismatchRouterChain(
        ushort firstDepth,
        ushort allocationClassId,
        ReadOnlySpan<byte> terminalKey,
        long terminalTargetOffset,
        ReadOnlySpan<byte> incomingKey,
        long incomingShelfOffset,
        int maxKeyLength)
    {
        int terminalDepth = Math.Min(Math.Max(terminalKey.Length, incomingKey.Length), maxKeyLength - 1);
        for (int depth = firstDepth; depth <= terminalDepth; depth++)
        {
            if (GetVarKeyScalar8Prefix(terminalKey, depth) != GetVarKeyScalar8Prefix(incomingKey, depth))
            {
                return CreateVarKeyVarIdentityTerminalMismatchRouterChainCore(
                    depth,
                    firstDepth,
                    allocationClassId,
                    terminalKey,
                    terminalTargetOffset,
                    incomingKey,
                    incomingShelfOffset);
            }
        }

        throw new InvalidDataException("The VV terminal route mismatch cannot be split because the remaining variable-key route is exhausted.");
    }

    /// <summary>
    /// Converts a mismatched exact-key terminal `VV` route back into an ordinary `VV` shelf when no remaining route byte can separate the terminal and incoming keys.<br/>
    /// This is a correctness fallback for small terminal groups created under compressed or aliased route context where later nearby keys cannot be split beneath the current parent route.<br/>
    /// The fallback stores the terminal key once per restored tuple again, which is less compact but preserves exact-key lookup and future ordinary split behavior.<br/>
    /// </summary>
    /// <param name="pathTarget">The route target that reached the mismatched terminal root.</param>
    /// <param name="maxKeyLength">The maximum encoded key length accepted by the owning index.</param>
    /// <param name="maxIdentityLength">The maximum identity length accepted by the owning index.</param>
    /// <param name="terminalKey">The encoded key stored by the terminal root.</param>
    /// <param name="incomingKey">The incoming encoded key.</param>
    /// <param name="incomingIdentity">The incoming raw identity.</param>
    /// <param name="allowDuplicateKeys">Whether duplicate keys with different identities are allowed.</param>
    /// <returns>The routed insert result for the de-terminalized route.</returns>
    private VarKeyVarIdentityRoutedInsertResult DeterminalizeMismatchedVarKeyVarIdentityRoute(
        VarKeyVarIdentityRoutePathTarget pathTarget,
        int maxKeyLength,
        int maxIdentityLength,
        ReadOnlySpan<byte> terminalKey,
        ReadOnlySpan<byte> incomingKey,
        ReadOnlySpan<byte> incomingIdentity,
        bool allowDuplicateKeys)
    {
        byte[] rootBytes = ReadTerminalIdentityRootBytes(pathTarget.Target.Offset);
        int terminalShelfExtentSize = TerminalIdentityRootLayout.ReadShelfExtentSize(rootBytes);
        long oldFirstShelfOffset = TerminalIdentityRootLayout.ReadFirstShelfOffset(rootBytes);
        using PooledTerminalVarIdentitySet terminalIdentities = ReadScalar8VarIdentityTerminalIdentitiesPooled(pathTarget.Target.Offset, terminalKey, terminalShelfExtentSize);
        VarKeyVarIdentityProfile profile = VarKeyVarIdentityProfile.Create(32 * 1024, maxKeyLength, maxIdentityLength) with { Descending = rootBytes[TerminalIdentityRootLayout.SortDirectionOffset] != 0 };
        if (!TryBuildDeterminalizedVarKeyVarIdentityShelf(profile, terminalKey, terminalIdentities, incomingKey, incomingIdentity, allowDuplicateKeys, out byte[] shelfBytes, out VarKeyVarIdentityInsertResult insertResult))
        {
            profile = VarKeyVarIdentityProfile.Create(128 * 1024, maxKeyLength, maxIdentityLength) with { Descending = rootBytes[TerminalIdentityRootLayout.SortDirectionOffset] != 0 };
            if (!TryBuildDeterminalizedVarKeyVarIdentityShelf(profile, terminalKey, terminalIdentities, incomingKey, incomingIdentity, allowDuplicateKeys, out shelfBytes, out insertResult))
            {
                throw new InvalidDataException($"The VV terminal route could not be de-terminalized into an ordinary shelf. TerminalKey={Convert.ToHexString(terminalKey)}; IncomingKey={Convert.ToHexString(incomingKey)}; TerminalIdentities={terminalIdentities.Count.ToString(CultureInfo.InvariantCulture)}; RouteDepth={pathTarget.Target.RouterDepth.ToString(CultureInfo.InvariantCulture)}.");
            }
        }

        if (insertResult != VarKeyVarIdentityInsertResult.Inserted)
        {
            return new VarKeyVarIdentityRoutedInsertResult(
                insertResult == VarKeyVarIdentityInsertResult.AlreadyPresent ? VarKeyVarIdentityRoutedInsertKind.NoOp : VarKeyVarIdentityRoutedInsertKind.Invalid,
                insertResult,
                pathTarget.Target.Offset,
                0,
                default,
                terminalIdentities.Count,
                profile.ShelfExtentSize,
                pathTarget.Target.RouterDepth);
        }

        RawDataReservation shelfReservation = kernel.Reserve(profile.ShelfExtentSize);
        shelfBytes.CopyTo(shelfReservation.Span);
        RepointMatchingRoutes(pathTarget.ParentRouterOffset, pathTarget.Target.Offset, shelfReservation.Extent.Offset);
        ClearTerminalIdentityReadCaches();
        ReleaseTerminalVarIdentityShelfChain(oldFirstShelfOffset, terminalShelfExtentSize);
        kernel.StageExtentRetirement(pathTarget.Target.Offset, TerminalIdentityRootLayout.Size);
        DataKernelCommitTelemetry telemetry = CommitAndInvalidateRouterReadCache();
        return new VarKeyVarIdentityRoutedInsertResult(
            VarKeyVarIdentityRoutedInsertKind.WalkedShelfTransformSplit,
            VarKeyVarIdentityInsertResult.Inserted,
            pathTarget.Target.Offset,
            shelfReservation.Extent.Offset,
            telemetry,
            terminalIdentities.Count + 1,
            profile.ShelfExtentSize,
            pathTarget.Target.RouterDepth);
    }

    /// <summary>
    /// Builds an ordinary `VV` shelf from one terminal key's identity set plus one incoming tuple.<br/>
    /// The method writes through the normal `VV` insert helper so tuple ordering, duplicate detection, and packed shelf bytes stay identical to ordinary inserts.<br/>
    /// </summary>
    /// <param name="profile">The candidate ordinary shelf profile.</param>
    /// <param name="terminalKey">The encoded terminal key to repeat for restored terminal identities.</param>
    /// <param name="terminalIdentities">The sorted terminal identity workspace.</param>
    /// <param name="incomingKey">The incoming encoded key.</param>
    /// <param name="incomingIdentity">The incoming identity.</param>
    /// <param name="allowDuplicateKeys">Whether duplicate keys with different identities are allowed.</param>
    /// <param name="shelfBytes">Receives the ordinary shelf bytes when all rows fit.</param>
    /// <param name="insertResult">Receives the incoming tuple result.</param>
    /// <returns><see langword="true"/> when the candidate profile held every restored row and the incoming tuple.</returns>
    private static bool TryBuildDeterminalizedVarKeyVarIdentityShelf(
        VarKeyVarIdentityProfile profile,
        ReadOnlySpan<byte> terminalKey,
        PooledTerminalVarIdentitySet terminalIdentities,
        ReadOnlySpan<byte> incomingKey,
        ReadOnlySpan<byte> incomingIdentity,
        bool allowDuplicateKeys,
        out byte[] shelfBytes,
        out VarKeyVarIdentityInsertResult insertResult)
    {
        shelfBytes = VarKeyVarIdentity.CreateEmpty(profile);
        for (int i = 0; i < terminalIdentities.Count; i++)
        {
            insertResult = VarKeyVarIdentity.InsertWithMutationHint(
                shelfBytes,
                profile,
                terminalKey,
                terminalIdentities.ReadAt(i),
                allowDuplicateKeys: true,
                hintStartDepth: 0,
                maxHintBytes: 0,
                out _,
                out byte[] rewrittenTerminalShelf);
            if (insertResult != VarKeyVarIdentityInsertResult.Inserted)
            {
                return false;
            }

            shelfBytes = rewrittenTerminalShelf;
        }

        insertResult = VarKeyVarIdentity.InsertWithMutationHint(
            shelfBytes,
            profile,
            incomingKey,
            incomingIdentity,
            allowDuplicateKeys,
            hintStartDepth: 0,
            maxHintBytes: 0,
            out _,
            out byte[] rewrittenIncomingShelf);
        if (insertResult != VarKeyVarIdentityInsertResult.Inserted)
        {
            return insertResult == VarKeyVarIdentityInsertResult.AlreadyPresent;
        }

        shelfBytes = rewrittenIncomingShelf;
        return true;
    }

    /// <summary>
    /// Builds the bounded router chain for a `VV` terminal-root mismatch once the separating byte depth is known.<br/>
    /// Routers are allocated from the deepest byte back to <paramref name="firstDepth"/> so each parent can point at its child without a second rewrite pass.<br/>
    /// </summary>
    /// <param name="splitDepth">The encoded-key byte depth where the terminal and incoming keys first differ.</param>
    /// <param name="firstDepth">The first encoded-key byte depth below the parent route.</param>
    /// <param name="allocationClassId">The router allocation class id to persist on created routers.</param>
    /// <param name="terminalKey">The encoded key stored by the existing terminal root.</param>
    /// <param name="terminalTargetOffset">The existing exact-key terminal root offset.</param>
    /// <param name="incomingKey">The incoming encoded key.</param>
    /// <param name="incomingShelfOffset">The ordinary incoming shelf and default fallback target.</param>
    /// <returns>The top router offset for publication into the parent route.</returns>
    private long CreateVarKeyVarIdentityTerminalMismatchRouterChainCore(
        int splitDepth,
        int firstDepth,
        ushort allocationClassId,
        ReadOnlySpan<byte> terminalKey,
        long terminalTargetOffset,
        ReadOnlySpan<byte> incomingKey,
        long incomingShelfOffset)
    {
        long nextTargetOffset = terminalTargetOffset;
        for (int depth = splitDepth; depth >= firstDepth; depth--)
        {
            long[] targets = CreateFilledScalar8Scalar8RouteTargets(incomingShelfOffset);
            byte terminalPrefix = GetVarKeyScalar8Prefix(terminalKey, depth);
            byte incomingPrefix = GetVarKeyScalar8Prefix(incomingKey, depth);
            targets[terminalPrefix] = nextTargetOffset;
            if (depth == splitDepth && terminalPrefix != incomingPrefix)
            {
                targets[incomingPrefix] = incomingShelfOffset;
            }

            RawDataReservation reservation = kernel.Reserve(RouterLayout.Size);
            RouterWriter writer = new(reservation.Span);
            writer.InitializeExpandedOneByte(checked((ushort)depth), allocationClassId, targets);
            nextTargetOffset = reservation.Extent.Offset;
        }

        return nextTargetOffset;
    }

    /// <summary>
    /// Publishes the first tuple for an unset route in an expanded direct `VV` exact-stem chain.<br/>
    /// Exact-stem transforms leave unrelated sibling prefixes unset so each new prefix acquires an independent ordered shelf instead of aliasing an existing extent.<br/>
    /// The shelf image is prepared before serialized publication, which revalidates the exact route and commits the shelf plus router rewrite together.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The root router offset identifying the owning `VV` index.<br/></param>
    /// <param name="routerOffset">The expanded direct router containing the unset route.<br/></param>
    /// <param name="prefixByte">The exact direct prefix expected to remain unset.<br/></param>
    /// <param name="profile">The initial shelf profile for the new independent extent.<br/></param>
    /// <param name="key">The raw variable key to insert.<br/></param>
    /// <param name="identity">The raw variable identity to insert.<br/></param>
    /// <param name="allowDuplicateKeys">Whether different identities may share the key.<br/></param>
    /// <param name="result">Receives the routed insert result when publication succeeds.<br/></param>
    /// <returns><see langword="true"/> when this call initialized the route; otherwise false when another publisher won the race.<br/></returns>
    private bool TryInsertVarKeyVarIdentityDirectColdRoute(
        long rootRouterOffset,
        long routerOffset,
        byte prefixByte,
        VarKeyVarIdentityProfile profile,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys,
        out VarKeyVarIdentityRoutedInsertResult result)
    {
        byte[] shelfBytes = VarKeyVarIdentity.CreateEmpty(profile);
        if (!VarKeyVarIdentityMutableShelf.TryCreate(shelfBytes, profile, ownsBytes: false, rentSidecars: false, out VarKeyVarIdentityMutableShelf preparedShelf))
            throw new InvalidDataException("The prepared VV child cold-route shelf is invalid.");

        VarKeyVarIdentityInsertResult insertResult = preparedShelf.InsertWithMutationHint(
            key,
            identity,
            allowDuplicateKeys,
            hintStartDepth: 0,
            maxHintBytes: 0,
            out _);
        if (insertResult != VarKeyVarIdentityInsertResult.Inserted)
            throw new InvalidDataException($"Expected VV child cold-route shelf insert, got {insertResult}.");

        PrimitiveTopologyOwnerKey topologyOwnerKey = new(6, 9, rootRouterOffset, routerOffset, prefixByte);
        object topologyOwner = GetPrimitiveTopologyOwner(topologyOwnerKey);
        try
        {
            lock (topologyOwner)
            {
                ReportPrimitiveTopologyOwnerEnteredForValidation(topologyOwnerKey);
                lock (writePublicationSync)
                {
                    kernel.EnterExclusiveStoragePublication();
                    try
                    {
                        byte[] routerBytes = new byte[RouterLayout.Size];
                        kernel.Read(routerOffset, routerBytes);
                        RouterReader reader = new(routerBytes);
                        if (!reader.IsValid || !reader.HasDirectIndex)
                            throw new InvalidDataException("The VV child cold-route parent is not a valid expanded direct router.");

                        if (reader.GetDirectTarget(prefixByte) != 0)
                        {
                            result = default;
                            return false;
                        }

                        RawDataReservation shelfReservation = kernel.Reserve(profile.ShelfExtentSize);
                        preparedShelf.Bytes.AsSpan(0, profile.ShelfExtentSize).CopyTo(shelfReservation.Span);
                        RawDataReservation routerRewrite = kernel.ReserveAt(routerOffset, RouterLayout.Size);
                        routerBytes.CopyTo(routerRewrite.Span);
                        RouterWriter writer = new(routerRewrite.Span);
                        writer.WriteRoute(prefixByte, prefixByte, prefixByte, shelfReservation.Extent.Offset);
                        InvalidateRouterReadCacheForRouterRewrite(routerOffset);
                        DataKernelCommitTelemetry telemetry = CommitAndDeferRouterReadCacheInvalidation();
                        result = new VarKeyVarIdentityRoutedInsertResult(
                            VarKeyVarIdentityRoutedInsertKind.WalkedNoSplit,
                            VarKeyVarIdentityInsertResult.Inserted,
                            shelfReservation.Extent.Offset,
                            shelfReservation.Extent.Offset,
                            telemetry,
                            1,
                            profile.ShelfExtentSize,
                            reader.KeyDepth);
                        return true;
                    }
                    finally
                    {
                        kernel.ExitExclusiveStoragePublication();
                    }
                }
            }
        }
        finally
        {
            preparedShelf.Release(clearShelfBytes: true);
        }
    }

    /// <summary>
    /// Selects the initial `VV` shelf profile for a lazily created routed prefix.<br/>
    /// File-backed sessions preserve the existing write-intent policy; memory-backed sessions use a smaller process-level profile so empty or lightly populated prefixes do not retain disk-sized shelf slack.<br/>
    /// </summary>
    /// <param name="maxKeyLength">The maximum raw key length in bytes.</param>
    /// <param name="maxIdentityLength">The maximum raw identity length in bytes.</param>
    /// <returns>The initial `VV` shelf profile for the current backing kind and write intent.</returns>
    private VarKeyVarIdentityProfile SelectInitialVarKeyVarIdentityProfile(int maxKeyLength, int maxIdentityLength, bool descending = false)
    {
        if (BackingKind != DataKernelBackingKind.Memory)
        {
            return VarKeyVarIdentityProfile.SelectInitial(currentWriteIntent, maxKeyLength, maxIdentityLength) with { Descending = descending };
        }

        return (ParseMemoryVarKeyVarIdentityShelfKiB() switch
        {
            4 => VarKeyVarIdentityProfile.Create(VarKeyVarIdentityProfile.Default4KiB.ShelfExtentSize, maxKeyLength, maxIdentityLength),
            8 => VarKeyVarIdentityProfile.Create(VarKeyVarIdentityProfile.Default8KiB.ShelfExtentSize, maxKeyLength, maxIdentityLength),
            16 => VarKeyVarIdentityProfile.Create(VarKeyVarIdentityProfile.Default16KiB.ShelfExtentSize, maxKeyLength, maxIdentityLength),
            32 => VarKeyVarIdentityProfile.Create(VarKeyVarIdentityProfile.Default32KiB.ShelfExtentSize, maxKeyLength, maxIdentityLength),
            64 => VarKeyVarIdentityProfile.Create(VarKeyVarIdentityProfile.Default64KiB.ShelfExtentSize, maxKeyLength, maxIdentityLength),
            128 => VarKeyVarIdentityProfile.Create(VarKeyVarIdentityProfile.Default128KiB.ShelfExtentSize, maxKeyLength, maxIdentityLength),
            _ => VarKeyVarIdentityProfile.Create(VarKeyVarIdentityProfile.Default8KiB.ShelfExtentSize, maxKeyLength, maxIdentityLength)
        }) with { Descending = descending };
    }

    /// <summary>
    /// Parses the process-level memory `VV` initial shelf-size override in KiB.<br/>
    /// The environment lookup happens only on lazy route creation, not during ordinary routed walks or shelf mutation.<br/>
    /// </summary>
    /// <returns>The requested memory `VV` initial shelf size in KiB, or 8 when absent or invalid.</returns>
    private static int ParseMemoryVarKeyVarIdentityShelfKiB()
    {
        string? value = Environment.GetEnvironmentVariable("LIBRADEX_MEMORY_VV_SHELF_KB");
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int kib)
            ? kib
            : 8;
    }

    internal VarKeyVarIdentityRangeReader OpenVarKeyVarIdentityRangeReader(
        long rootRouterOffset,
        int maxKeyLength,
        int maxIdentityLength,
        ReadOnlySpan<byte> lowerKey,
        ReadOnlySpan<byte> upperKey,
        int maxRouterHops = DefaultVarKeyVarIdentityMaxRouterHops,
        bool decodeLogicalKeys = false,
        bool descending = false)
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
            decodeLogicalKeys,
            descending);
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
    /// Starts an internal `VV` writer context that can stage warmed ordinary shelf-local rewrites before serialized publication.<br/>
    /// Terminal var-identity routes, cold route creation, and split/growth topology remain on the existing durability-batch path for this first `VV` concurrency slice.<br/>
    /// </summary>
    /// <returns>A writer-local mutation context for ordinary `VV` shelf changes.<br/></returns>
    internal LibraDexWriteContext BeginVarKeyVarIdentityWriteContext()
    {
        long operationToken = Interlocked.Increment(ref diagnosticWriteWindowNextOperationToken);
        if (operationToken == 0)
        {
            operationToken = Interlocked.Increment(ref diagnosticWriteWindowNextOperationToken);
        }

        return new LibraDexWriteContext(operationToken);
    }

    /// <summary>
    /// Attempts one routed `VV` insert through a writer-local ordinary-shelf context without allowing topology changes.<br/>
    /// The method supports warmed ordinary shelves only; terminal routes, growth, split, and chain rewrites throw so callers can use the existing topology path.<br/>
    /// </summary>
    /// <param name="writeContext">The writer context that owns staged ordinary shelf mutations.<br/></param>
    /// <param name="rootRouterOffset">The root router offset for the index.<br/></param>
    /// <param name="maxKeyLength">The maximum encoded key length accepted by the index profile.<br/></param>
    /// <param name="maxIdentityLength">The maximum raw identity length accepted by the index profile.<br/></param>
    /// <param name="key">The encoded raw key bytes to insert.<br/></param>
    /// <param name="identity">The raw variable-length identity bytes to insert.<br/></param>
    /// <param name="allowDuplicateKeys">Whether duplicate keys with different identities are allowed.<br/></param>
    /// <param name="maxRouterHops">The maximum number of router pages to follow.<br/></param>
    /// <returns>The storage-facing routed insert result for the writer-local attempt.<br/></returns>
    internal VarKeyVarIdentityRoutedInsertResult InsertWalkedRoutedVarKeyVarIdentityNoSplitForWriteContext(
        LibraDexWriteContext writeContext,
        long rootRouterOffset,
        int maxKeyLength,
        int maxIdentityLength,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> identity,
        bool allowDuplicateKeys,
        int maxRouterHops = DefaultVarKeyVarIdentityMaxRouterHops)
    {
        VarKeyVarIdentityRoutePathTarget pathTarget;
        lock (routerReadCacheSync)
        {
            pathTarget = WalkVarKeyVarIdentityRoutePathTarget(rootRouterOffset, key, maxRouterHops);
        }

        VarKeyVarIdentityRouteTarget target = pathTarget.Target;
        if (target.Kind != VarKeyVarIdentityRouteTargetKind.Shelf)
        {
            throw new InvalidOperationException("The VV writer-context path supports only ordinary warmed shelf routes.");
        }

        RecordVarKeyVarIdentityRouteClaimForWriteContext(writeContext, pathTarget);
        VarKeyVarIdentityMutableShelf shelf = ReadVarKeyVarIdentityMutableShelfForWriteContext(writeContext, target.Offset, maxKeyLength, maxIdentityLength);
        VarKeyVarIdentityProfile profile = shelf.Profile;
        int beforeItemCount = shelf.ItemCount;
        VarKeyVarIdentityInsertResult insertResult = shelf.InsertWithMutationHint(
            key,
            identity,
            allowDuplicateKeys,
            hintStartDepth: target.RouterDepth,
            maxHintBytes: 0,
            out _);
        if (insertResult == VarKeyVarIdentityInsertResult.Full)
        {
            throw new InvalidOperationException("The VV writer-context path does not yet support shelf growth or split transforms.");
        }

        VarKeyVarIdentityRoutedInsertKind kind = insertResult switch
        {
            VarKeyVarIdentityInsertResult.Inserted => VarKeyVarIdentityRoutedInsertKind.WalkedNoSplit,
            VarKeyVarIdentityInsertResult.KeyConflict => VarKeyVarIdentityRoutedInsertKind.KeyConflict,
            _ => VarKeyVarIdentityRoutedInsertKind.NoOp
        };
        return new VarKeyVarIdentityRoutedInsertResult(
            kind,
            insertResult,
            target.Offset,
            insertResult == VarKeyVarIdentityInsertResult.Inserted ? target.Offset : 0,
            default,
            insertResult == VarKeyVarIdentityInsertResult.Inserted ? beforeItemCount + 1 : beforeItemCount,
            profile.ShelfExtentSize,
            target.RouterDepth);
    }

    /// <summary>
    /// Attempts one exact `VV` tuple delete through a writer-local ordinary-shelf context.<br/>
    /// Terminal var-identity roots throw so callers can fall back to the existing durability-batch terminal delete path.<br/>
    /// </summary>
    /// <param name="writeContext">The writer context that owns staged ordinary shelf mutations.<br/></param>
    /// <param name="rootRouterOffset">The root router offset for the index.<br/></param>
    /// <param name="maxKeyLength">The maximum encoded key length accepted by the index profile.<br/></param>
    /// <param name="maxIdentityLength">The maximum raw identity length accepted by the index profile.<br/></param>
    /// <param name="key">The exact encoded raw key bytes.<br/></param>
    /// <param name="identity">The exact raw identity bytes to delete.<br/></param>
    /// <param name="maxRouterHops">The maximum number of router pages to follow.<br/></param>
    /// <returns><see langword="true"/> when one live tuple was marked deleted in the writer-local shelf.</returns>
    internal bool DeleteVarKeyVarIdentityExactTupleForWriteContext(
        LibraDexWriteContext writeContext,
        long rootRouterOffset,
        int maxKeyLength,
        int maxIdentityLength,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> identity,
        int maxRouterHops = DefaultVarKeyVarIdentityMaxRouterHops)
    {
        VarKeyVarIdentityRoutePathTarget pathTarget;
        lock (routerReadCacheSync)
        {
            pathTarget = WalkVarKeyVarIdentityRoutePathTarget(rootRouterOffset, key, maxRouterHops);
        }

        VarKeyVarIdentityRouteTarget target = pathTarget.Target;
        if (target.Kind != VarKeyVarIdentityRouteTargetKind.Shelf)
        {
            throw new InvalidOperationException("The VV writer-context exact delete path supports only ordinary warmed shelf routes.");
        }

        RecordVarKeyVarIdentityRouteClaimForWriteContext(writeContext, pathTarget);
        VarKeyVarIdentityMutableShelf shelf = ReadVarKeyVarIdentityMutableShelfForWriteContext(writeContext, target.Offset, maxKeyLength, maxIdentityLength);
        return shelf.MarkTupleDeleted(key, identity);
    }

    /// <summary>
    /// Deletes an inclusive encoded-key `VV` range through writer-local ordinary-shelf contexts.<br/>
    /// Router targets are traversed, ordinary shelves are tombstoned, and terminal var-identity roots are rejected for fallback handling.<br/>
    /// </summary>
    /// <param name="writeContext">The writer context that owns staged ordinary shelf mutations.<br/></param>
    /// <param name="rootRouterOffset">The root router offset for the index.<br/></param>
    /// <param name="maxKeyLength">The maximum encoded key length accepted by the index profile.<br/></param>
    /// <param name="maxIdentityLength">The maximum raw identity length accepted by the index profile.<br/></param>
    /// <param name="lowerKey">The inclusive lower encoded raw key.<br/></param>
    /// <param name="upperKey">The inclusive upper encoded raw key.<br/></param>
    /// <param name="maxRouterHops">The maximum number of router pages to follow.<br/></param>
    /// <returns>The number of live tuples marked deleted in writer-local shelves.<br/></returns>
    internal long DeleteVarKeyVarIdentityKeyRangeForWriteContext(
        LibraDexWriteContext writeContext,
        long rootRouterOffset,
        int maxKeyLength,
        int maxIdentityLength,
        ReadOnlySpan<byte> lowerKey,
        ReadOnlySpan<byte> upperKey,
        int maxRouterHops = DefaultVarKeyVarIdentityMaxRouterHops)
    {
        ArgumentNullException.ThrowIfNull(writeContext);
        if (lowerKey.SequenceCompareTo(upperKey) > 0)
        {
            return 0;
        }

        lock (routerReadCacheSync)
        {
            long deleted = 0;
            using RouteVisitedOffsetSet visitedShelves = RouteVisitedOffsetSet.Rent();
            using RouteVisitedOffsetSet visitedRouters = RouteVisitedOffsetSet.Rent();
            byte lowerPrefix = GetVarKeyScalar8Prefix(lowerKey, 0);
            byte upperPrefix = GetVarKeyScalar8Prefix(upperKey, 0);
            for (int prefix = lowerPrefix; prefix <= upperPrefix; prefix++)
            {
                long targetOffset = FindRouterTarget(rootRouterOffset, (byte)prefix);
                if (targetOffset == 0)
                {
                    continue;
                }

                deleted += DeleteVarKeyVarIdentityRangeFromTargetForWriteContext(
                    writeContext,
                    rootRouterOffset,
                    prefix,
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
    }

    /// <summary>
    /// Recursively deletes one `VV` range target through a writer-local ordinary-shelf context.<br/>
    /// Router targets are traversed, ordinary shelves are tombstoned, and terminal var-identity roots are rejected for fallback handling.<br/>
    /// </summary>
    /// <param name="writeContext">The writer context that owns staged ordinary shelf mutations.<br/></param>
    /// <param name="parentRouterOffset">The parent router whose route slot selected <paramref name="targetOffset"/>.<br/></param>
    /// <param name="routeIndex">The parent-router route slot that selected <paramref name="targetOffset"/>.<br/></param>
    /// <param name="targetOffset">The routed target offset to process.<br/></param>
    /// <param name="maxKeyLength">The maximum encoded key length accepted by the index profile.<br/></param>
    /// <param name="maxIdentityLength">The maximum raw identity length accepted by the index profile.<br/></param>
    /// <param name="lowerKey">The inclusive lower encoded raw key.<br/></param>
    /// <param name="upperKey">The inclusive upper encoded raw key.<br/></param>
    /// <param name="visitedShelves">The visited shelf set used to avoid repeated mutation of shared targets.<br/></param>
    /// <param name="visitedRouters">The visited router set used to avoid route cycles.<br/></param>
    /// <param name="remainingRouterHops">The remaining router hop budget.<br/></param>
    /// <returns>The number of live tuples marked deleted.<br/></returns>
    private long DeleteVarKeyVarIdentityRangeFromTargetForWriteContext(
        LibraDexWriteContext writeContext,
        long parentRouterOffset,
        int routeIndex,
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
        if (kind == VarKeyVarIdentityRouteTargetKind.TerminalVarIdentityRoot)
        {
            throw new InvalidOperationException("The VV writer-context range delete path does not yet support terminal var-identity routes.");
        }

        if (kind == VarKeyVarIdentityRouteTargetKind.Shelf)
        {
            RecordVarKeyVarIdentityRouteClaimForWriteContext(writeContext, parentRouterOffset, routeIndex, targetOffset);
            if (!visitedShelves.Add(targetOffset))
            {
                return 0;
            }

            VarKeyVarIdentityMutableShelf shelf = ReadVarKeyVarIdentityMutableShelfForWriteContext(writeContext, targetOffset, maxKeyLength, maxIdentityLength);
            return shelf.MarkKeyRangeDeleted(lowerKey, upperKey);
        }

        if (kind != VarKeyVarIdentityRouteTargetKind.Router)
        {
            throw new InvalidDataException("The routed VV writer-context range delete target is not a shelf, terminal var-identity root, or router.");
        }

        if (!visitedRouters.Add(targetOffset))
        {
            return 0;
        }

        if (remainingRouterHops <= 0)
        {
            throw new InvalidDataException("The routed VV writer-context range delete exceeded the configured router hop count.");
        }

        long deletedFromChildren = 0;
        byte[] routerBytes = new byte[RouterLayout.Size];
        ReadRouterPageUsingArenaCache(targetOffset, routerBytes);
        RouterReader reader = new(routerBytes);
        if (!reader.IsValid)
        {
            throw new InvalidDataException("The routed VV writer-context range delete router is invalid.");
        }

        byte lowerPrefix = GetVarKeyScalar8Prefix(lowerKey, reader.KeyDepth);
        byte upperPrefix = GetVarKeyScalar8Prefix(upperKey, reader.KeyDepth);
        for (int prefix = lowerPrefix; prefix <= upperPrefix; prefix++)
        {
            int childRouteIndex;
            long childTargetOffset;
            if (reader.PrefixByteCount == 1)
            {
                childTargetOffset = reader.FindTarget((byte)prefix, out childRouteIndex);
            }
            else
            {
                childTargetOffset = reader.FindTarget(prefix == lowerPrefix ? lowerKey : upperKey, reader.KeyDepth, out childRouteIndex);
            }

            if (childTargetOffset == 0)
            {
                continue;
            }

            deletedFromChildren += DeleteVarKeyVarIdentityRangeFromTargetForWriteContext(
                writeContext,
                targetOffset,
                childRouteIndex,
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
        if (target.Kind == VarKeyVarIdentityRouteTargetKind.TerminalVarIdentityRoot)
        {
            return DeleteVarKeyVarIdentityTerminalExactTuple(target.Offset, key, identity);
        }

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
        if (kind == VarKeyVarIdentityRouteTargetKind.TerminalVarIdentityRoot)
        {
            byte[] rootBytes = ReadTerminalIdentityRootBytes(targetOffset);
            if (TerminalIdentityRootLayout.ReadShape(rootBytes) != TerminalIdentityRootLayout.ShapeVarKey)
            {
                throw new InvalidDataException("The routed VV terminal var identity root is invalid.");
            }

            int keyLength = TerminalIdentityRootLayout.ReadKeyLength(rootBytes);
            ReadOnlySpan<byte> terminalKey = rootBytes.AsSpan(TerminalIdentityRootLayout.KeyBytesOffset, keyLength);
            if (terminalKey.SequenceCompareTo(lowerKey) < 0 || terminalKey.SequenceCompareTo(upperKey) > 0)
            {
                return 0;
            }

            int shelfExtentSize = TerminalIdentityRootLayout.ReadShelfExtentSize(rootBytes);
            using PooledTerminalVarIdentitySet identities = ReadScalar8VarIdentityTerminalIdentitiesPooled(targetOffset, terminalKey, shelfExtentSize);
            int deleted = identities.Count;
            if (deleted == 0)
            {
                return 0;
            }

            _ = RewriteScalar8VarIdentityTerminalRoute(targetOffset, TerminalIdentityRootLayout.ShapeVarKey, terminalKey, shelfExtentSize, ReadOnlySpan<byte[]>.Empty);
            return deleted;
        }

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
            throw new InvalidDataException("The routed VV delete target is not a shelf, terminal identity root, or router.");
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

        bool scanDescendantRoutes = reader.KeyDepth > 0;
        byte lowerPrefix = scanDescendantRoutes ? byte.MinValue : GetVarKeyScalar8Prefix(lowerKey, reader.KeyDepth);
        byte upperPrefix = scanDescendantRoutes ? byte.MaxValue : GetVarKeyScalar8Prefix(upperKey, reader.KeyDepth);
        long previousChildTargetOffset = 0;
        for (int prefix = lowerPrefix; prefix <= upperPrefix; prefix++)
        {
            long childTargetOffset = reader.FindTarget((byte)prefix);
            if (childTargetOffset == 0 || childTargetOffset == previousChildTargetOffset)
            {
                continue;
            }

            previousChildTargetOffset = childTargetOffset;
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
    /// Deletes one exact raw identity from a `VV` terminal duplicate route whose root stores the key once and identities in terminal var-identity shelves.<br/>
    /// The containing identity shelf is repacked at its existing offset, or unlinked with only predecessor/root endpoint updates when it becomes empty.<br/>
    /// </summary>
    /// <param name="rootOffset">The terminal identity root reached by the exact key route.<br/></param>
    /// <param name="key">The encoded variable key expected on the terminal root.<br/></param>
    /// <param name="identity">The exact raw identity to remove.<br/></param>
    /// <returns><see langword="true"/> when a matching terminal identity was removed.<br/></returns>
    private bool DeleteVarKeyVarIdentityTerminalExactTuple(long rootOffset, ReadOnlySpan<byte> key, ReadOnlySpan<byte> identity)
    {
        byte[] rootBytes = ReadTerminalIdentityRootBytes(rootOffset);
        if (TerminalIdentityRootLayout.ReadShape(rootBytes) != TerminalIdentityRootLayout.ShapeVarKey ||
            !IsTerminalIdentityRootForKey(rootBytes, key, out _))
        {
            return false;
        }

        return DeleteTerminalVarIdentityExactTupleLocally(
            rootOffset,
            TerminalIdentityRootLayout.ShapeVarKey,
            key,
            identity);
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
        long parentRouterOffset,
        bool parentHasDirectIndex,
        int parentRangeStart,
        int parentRangeEnd,
        ushort parentFinalKeyDepth,
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
            if (insertResult == VarKeyVarIdentityInsertResult.AlreadyPresent)
            {
                return (childRouterOffset, 0, 0, 0, 0, insertResult, default);
            }

            throw new InvalidDataException(
                $"The VV transform could not produce two replacement shelves that fit the {profile.ShelfExtentSize:N0}-byte profile; " +
                $"ChildOffset={childRouterOffset}; ExistingItems={existingShelf.ItemCount:N0}; IncomingKeyBytes={key.Length:N0}; IncomingIdentityBytes={identity.Length:N0}; FirstKeyDepth={childRouterKeyDepth}.");
        }

        if (parentHasDirectIndex &&
            parentRangeStart < parentRangeEnd &&
            splitKeyDepth == parentFinalKeyDepth)
        {
            lock (writePublicationSync)
            {
                kernel.EnterExclusiveStoragePublication();
                try
                {
            if (selectedRightPrefixByte <= parentRangeStart || selectedRightPrefixByte > parentRangeEnd)
                throw new InvalidDataException($"The VV parent-range split boundary {selectedRightPrefixByte} is outside owning direct route run {parentRangeStart}-{parentRangeEnd} at depth {parentFinalKeyDepth}.");

            RawDataReservation leftRewrite = kernel.ReserveAt(childRouterOffset, profile.ShelfExtentSize);
            leftShelfBytes.CopyTo(leftRewrite.Span);
            RawDataReservation parentRightAppend = kernel.Reserve(profile.ShelfExtentSize);
            rightShelfBytes.CopyTo(parentRightAppend.Span);
            byte[] parentBytes = new byte[RouterLayout.Size];
            kernel.Read(parentRouterOffset, parentBytes);
            RouterReader currentParent = new(parentBytes);
            if (!currentParent.IsValid ||
                !TryGetVarKeyParentTargetRange(
                    currentParent,
                    parentRangeStart,
                    childRouterOffset,
                    out bool currentHasDirectIndex,
                    out int currentRangeStart,
                    out int currentRangeEnd,
                    out ushort currentFinalKeyDepth) ||
                !currentHasDirectIndex ||
                currentRangeStart != parentRangeStart ||
                currentRangeEnd != parentRangeEnd ||
                currentFinalKeyDepth != parentFinalKeyDepth)
            {
                throw new InvalidDataException("The VV parent route changed during shared-range transform publication.");
            }

            RawDataReservation parentRewrite = kernel.ReserveAt(parentRouterOffset, RouterLayout.Size);
            parentBytes.CopyTo(parentRewrite.Span);
            RouterWriter parentWriter = new(parentRewrite.Span);
            for (int prefix = parentRangeStart; prefix <= parentRangeEnd; prefix++)
                parentWriter.WriteRouteTarget(prefix, prefix < selectedRightPrefixByte ? childRouterOffset : parentRightAppend.Extent.Offset);

            varKeyVarIdentityReadShelfCache.TryRemove(childRouterOffset, out _);
            varKeyVarIdentityReadOnlyShelfCache.TryRemove(childRouterOffset, out _);
            InvalidateRouterReadCacheForRouterRewrite(parentRouterOffset);
            DataKernelCommitTelemetry parentCommit = CommitAndDeferRouterReadCacheInvalidation();
            return (childRouterOffset, childRouterOffset, parentRightAppend.Extent.Offset, leftItemCount, rightItemCount, insertResult, parentCommit);
                }
                finally
                {
                    kernel.ExitExclusiveStoragePublication();
                }
            }
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
                    : CreateVarKeyScalar8IntermediateSplitRouteTargets(
                        nextRouterOffset,
                        leftAppend.Extent.Offset,
                        rightAppend.Extent.Offset,
                        selectedPrefixStem[routerDepth - childRouterKeyDepth]);
                RawDataReservation appendedRouter = kernel.Reserve(RouterLayout.Size);
                RouterWriter appendedWriter = new(appendedRouter.Span);
                appendedWriter.InitializeExpandedOneByte(routerDepth, allocationClassId, targets);
                nextRouterOffset = appendedRouter.Extent.Offset;
            }

            childWriter.InitializeExpandedOneByte(
                childRouterKeyDepth,
                allocationClassId,
                CreateVarKeyScalar8IntermediateSplitRouteTargets(
                    nextRouterOffset,
                    leftAppend.Extent.Offset,
                    rightAppend.Extent.Offset,
                    selectedPrefixStem[0]));
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
        insertResult = VarKeyVarIdentityInsertResult.Invalid;

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
                if (!inserted && (profile.Descending
                    ? CompareVarKeyVarIdentityTuple(currentKey, currentIdentity, key, identity) < 0
                    : CompareVarKeyVarIdentityTuple(currentKey, currentIdentity, key, identity) > 0))
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
                identityLengths,
                key,
                firstKeyDepth,
                hintRightPrefixByte,
                profile);
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
    /// <param name="identityLengths">The sorted identity lengths matching <paramref name="keyOffsets"/>.</param>
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
    /// <param name="profile">The target replacement-shelf profile used to reject byte- or slot-overfull boundaries before construction.</param>
    /// <returns>The selected split key depth and first right-side prefix byte.</returns>
    private static (ushort KeyDepth, byte RightPrefixByte) ChooseVarKeyVarIdentityTransformSplitPlan(
        byte[] existingShelfBytes,
        ReadOnlySpan<int> keyOffsets,
        ReadOnlySpan<int> keyLengths,
        ReadOnlySpan<int> identityLengths,
        ReadOnlySpan<byte> incomingKey,
        ushort firstKeyDepth,
        byte hintRightPrefixByte,
        VarKeyVarIdentityProfile profile)
    {
        if (keyOffsets.Length < 2 || keyOffsets.Length != keyLengths.Length || keyOffsets.Length != identityLengths.Length)
        {
            throw new InvalidDataException("The VV transform split boundary requires at least two complete key descriptors.");
        }

        ushort splitKeyDepth = 0;
        while (splitKeyDepth < profile.MaxKeyLength &&
               GetVarKeyVarIdentitySplitSourcePrefix(existingShelfBytes, keyOffsets[0], keyLengths[0], incomingKey, splitKeyDepth) ==
               GetVarKeyVarIdentitySplitSourcePrefix(existingShelfBytes, keyOffsets[^1], keyLengths[^1], incomingKey, splitKeyDepth))
        {
            splitKeyDepth++;
        }

        if (splitKeyDepth < firstKeyDepth)
            throw new InvalidDataException($"The VV transform source violates its routed prefix stem. FirstDifferentDepth={splitKeyDepth}; FirstOwnedDepth={firstKeyDepth}; Count={keyOffsets.Length}.");
        if (splitKeyDepth >= profile.MaxKeyLength ||
            !TryChooseVarKeyVarIdentityTransformSplitRightPrefix(existingShelfBytes, keyOffsets, keyLengths, identityLengths, incomingKey, splitKeyDepth, hintRightPrefixByte, profile, out byte rightPrefixByte))
            throw new InvalidDataException("The VV transform split path requires one globally ordered prefix transition after its owned stem.");

        return (splitKeyDepth, rightPrefixByte);
    }

    /// <summary>
    /// Attempts to choose a capacity-valid right-side prefix byte at one raw-key depth.<br/>
    /// The method minimizes maximum byte/slot utilization before considering row balance and the incoming-key hint, preventing variable key or identity sizes from invalidating an otherwise plausible split.<br/>
    /// </summary>
    /// <param name="existingShelfBytes">The existing shelf byte image containing persisted records.</param>
    /// <param name="keyOffsets">The sorted key-payload offsets, with negative values representing the incoming tuple.</param>
    /// <param name="keyLengths">The sorted key lengths matching <paramref name="keyOffsets"/>.</param>
    /// <param name="identityLengths">The sorted identity lengths matching <paramref name="keyOffsets"/>.</param>
    /// <param name="incomingKey">The incoming raw key bytes.</param>
    /// <param name="keyDepth">The raw key byte depth being evaluated.</param>
    /// <param name="hintRightPrefixByte">The incoming key prefix byte at the first candidate depth.</param>
    /// <param name="profile">The replacement-shelf profile whose independent slot and payload capacities every candidate must satisfy.</param>
    /// <param name="rightPrefixByte">The first prefix byte that should route to the right replacement shelf.</param>
    /// <returns>`true` when a prefix boundary was found at this depth; otherwise `false`.</returns>
    private static bool TryChooseVarKeyVarIdentityTransformSplitRightPrefix(
        byte[] existingShelfBytes,
        ReadOnlySpan<int> keyOffsets,
        ReadOnlySpan<int> keyLengths,
        ReadOnlySpan<int> identityLengths,
        ReadOnlySpan<byte> incomingKey,
        ushort keyDepth,
        byte hintRightPrefixByte,
        VarKeyVarIdentityProfile profile,
        out byte rightPrefixByte)
    {
        for (ushort ownedDepth = 0; ownedDepth < keyDepth; ownedDepth++)
        {
            byte ownedStem = GetVarKeyVarIdentitySplitSourcePrefix(existingShelfBytes, keyOffsets[0], keyLengths[0], incomingKey, ownedDepth);
            for (int i = 1; i < keyOffsets.Length; i++)
            {
                if (GetVarKeyVarIdentitySplitSourcePrefix(existingShelfBytes, keyOffsets[i], keyLengths[i], incomingKey, ownedDepth) != ownedStem)
                {
                    rightPrefixByte = 0;
                    return false;
                }
            }
        }

        int slotCapacityBytes = VarKeyVarIdentityLayout.CalculateSlotCapacityBytes(profile.ShelfExtentSize);
        int recordCapacityBytes = profile.ShelfExtentSize - VarKeyVarIdentityLayout.HeaderSize - slotCapacityBytes;
        long totalRecordBytes = 0;
        for (int i = 0; i < keyLengths.Length; i++)
        {
            totalRecordBytes += VarKeyVarIdentityLayout.GetNewRecordLength(keyLengths[i], identityLengths[i]);
        }

        int bestRightStart = -1;
        byte bestBoundary = 0;
        long bestMaximumUtilization = long.MaxValue;
        int bestBalanceDistance = int.MaxValue;
        int bestHintDistance = int.MaxValue;
        long leftRecordBytes = VarKeyVarIdentityLayout.GetNewRecordLength(keyLengths[0], identityLengths[0]);
        byte priorPrefix = GetVarKeyVarIdentitySplitSourcePrefix(existingShelfBytes, keyOffsets[0], keyLengths[0], incomingKey, keyDepth);
        for (int i = 1; i < keyOffsets.Length; i++)
        {
            byte currentPrefix = GetVarKeyVarIdentitySplitSourcePrefix(existingShelfBytes, keyOffsets[i], keyLengths[i], incomingKey, keyDepth);
            if (profile.Descending ? currentPrefix > priorPrefix : currentPrefix < priorPrefix)
            {
                rightPrefixByte = 0;
                return false;
            }
            if (profile.Descending ? currentPrefix < priorPrefix : currentPrefix > priorPrefix)
            {
                int leftCount = profile.Descending ? keyOffsets.Length - i : i;
                int rightCount = profile.Descending ? i : keyOffsets.Length - i;
                long rightRecordBytes = profile.Descending ? leftRecordBytes : totalRecordBytes - leftRecordBytes;
                long candidateLeftRecordBytes = profile.Descending ? totalRecordBytes - leftRecordBytes : leftRecordBytes;
                bool fits = leftCount * VarKeyVarIdentityLayout.SlotSize <= slotCapacityBytes &&
                    rightCount * VarKeyVarIdentityLayout.SlotSize <= slotCapacityBytes &&
                    candidateLeftRecordBytes <= recordCapacityBytes &&
                    rightRecordBytes <= recordCapacityBytes;
                int balanceDistance = Math.Abs(i - (keyOffsets.Length - i));
                int hintDistance = Math.Abs(currentPrefix - hintRightPrefixByte);
                long maximumUtilization = Math.Max(
                    Math.Max(candidateLeftRecordBytes * slotCapacityBytes, (long)leftCount * VarKeyVarIdentityLayout.SlotSize * recordCapacityBytes),
                    Math.Max(rightRecordBytes * slotCapacityBytes, (long)rightCount * VarKeyVarIdentityLayout.SlotSize * recordCapacityBytes));
                if (fits &&
                    (maximumUtilization < bestMaximumUtilization ||
                     (maximumUtilization == bestMaximumUtilization && balanceDistance < bestBalanceDistance) ||
                     (maximumUtilization == bestMaximumUtilization && balanceDistance == bestBalanceDistance && hintDistance < bestHintDistance)))
                {
                    bestRightStart = i;
                    bestBoundary = profile.Descending ? priorPrefix : currentPrefix;
                    bestMaximumUtilization = maximumUtilization;
                    bestBalanceDistance = balanceDistance;
                    bestHintDistance = hintDistance;
                }
            }
            priorPrefix = currentPrefix;
            leftRecordBytes += VarKeyVarIdentityLayout.GetNewRecordLength(keyLengths[i], identityLengths[i]);
        }

        rightPrefixByte = bestBoundary;
        return bestRightStart >= 0;
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

    /// <summary>
    /// Records the committed router slot that selected one `VV` shelf for writer-context mutation.<br/>
    /// Publication revalidates this claim so a warmed shelf writer cannot overwrite stale bytes after another writer grows, splits, or relinks the route.<br/>
    /// </summary>
    /// <param name="writeContext">The writer context that owns the staged shelf.<br/></param>
    /// <param name="pathTarget">The route path evidence captured from committed state.<br/></param>
    private static void RecordVarKeyVarIdentityRouteClaimForWriteContext(
        LibraDexWriteContext writeContext,
        VarKeyVarIdentityRoutePathTarget pathTarget)
    {
        if (pathTarget.Target.Kind == VarKeyVarIdentityRouteTargetKind.Shelf)
        {
            RecordVarKeyVarIdentityRouteClaimForWriteContext(
                writeContext,
                pathTarget.ParentRouterOffset,
                pathTarget.RouteIndex,
                pathTarget.Target.Offset);
        }
    }

    /// <summary>
    /// Records one committed `VV` parent-router route slot for writer-context mutation.<br/>
    /// Range traversal uses this overload because it discovers child targets while walking router pages rather than through a full path-target wrapper.<br/>
    /// </summary>
    /// <param name="writeContext">The writer context that owns the staged shelf.<br/></param>
    /// <param name="parentRouterOffset">The parent router that selected the shelf.<br/></param>
    /// <param name="routeIndex">The route slot inside the parent router.</param>
    /// <param name="targetOffset">The expected routed shelf offset.</param>
    private static void RecordVarKeyVarIdentityRouteClaimForWriteContext(
        LibraDexWriteContext writeContext,
        long parentRouterOffset,
        int routeIndex,
        long targetOffset)
    {
        writeContext.VarKeyVarIdentityRouteClaims[targetOffset] = new VarKeyVarIdentityRouteClaim(
            parentRouterOffset,
            routeIndex,
            targetOffset);
    }

    /// <summary>
    /// Validates every committed `VV` route slot used by a writer-context shelf rewrite.<br/>
    /// If another writer changed the selecting route before publication, this writer fails before any staged shelf bytes are flushed.<br/>
    /// </summary>
    /// <param name="writeContext">The writer context whose route claims should still match committed state.<br/></param>
    /// <exception cref="InvalidOperationException">Thrown when a claimed shelf is no longer selected by the captured parent route.<br/></exception>
    private void ValidateVarKeyVarIdentityRouteClaimsForWriteContext(LibraDexWriteContext writeContext)
    {
        byte[] routerBytes = new byte[RouterLayout.Size];
        foreach (VarKeyVarIdentityRouteClaim claim in writeContext.VarKeyVarIdentityRouteClaims.Values)
        {
            kernel.Read(claim.ParentRouterOffset, routerBytes);
            RouterReader reader = new(routerBytes);
            if (!reader.IsValid ||
                claim.RouteIndex < 0 ||
                claim.RouteIndex >= reader.RouteCount ||
                reader.GetRouteTargetAt(claim.RouteIndex) != claim.ExpectedTargetOffset ||
                !IsVarKeyVarIdentityShelfForRouteClaim(claim.ExpectedTargetOffset))
            {
                throw new InvalidOperationException("The VV writer-context route claim no longer matches committed router state.");
            }
        }
    }

    /// <summary>
    /// Reads the physical target magic for a `VV` route claim without consulting route-target caches.<br/>
    /// Publish-time validation uses this narrow probe so a same-offset shelf-to-router transform cannot be hidden by an earlier cached shelf classification.<br/>
    /// </summary>
    /// <param name="targetOffset">The claimed routed shelf offset.<br/></param>
    /// <returns><see langword="true"/> when the offset still contains a `VV` shelf header; otherwise, <see langword="false"/>.</returns>
    private bool IsVarKeyVarIdentityShelfForRouteClaim(long targetOffset)
    {
        if (targetOffset <= 0)
        {
            return false;
        }

        Span<byte> magicBytes = stackalloc byte[sizeof(uint)];
        kernel.Read(targetOffset, magicBytes);
        return BinaryPrimitives.ReadUInt32LittleEndian(magicBytes) == VarKeyVarIdentityLayout.Magic;
    }

    /// <summary>
    /// Reads one ordinary `VV` shelf into a writer-local mutable view and claims the shelf for that writer context.<br/>
    /// Repeated reads by the same context reuse the already decoded mutable shelf; another context targeting the same shelf receives a retryable ownership exception.<br/>
    /// </summary>
    /// <param name="writeContext">The writer context that owns staged shelf changes until publish or abort.<br/></param>
    /// <param name="shelfOffset">The ordinary `VV` shelf offset to read.<br/></param>
    /// <param name="maxKeyLength">The maximum encoded key length accepted by the profile family.<br/></param>
    /// <param name="maxIdentityLength">The maximum raw identity length accepted by the profile family.<br/></param>
    /// <returns>A writer-local mutable shelf view.</returns>
    /// <exception cref="InvalidDataException">Thrown when the loaded shelf bytes fail `VV` validation.<br/></exception>
    internal VarKeyVarIdentityMutableShelf ReadVarKeyVarIdentityMutableShelfForWriteContext(
        LibraDexWriteContext writeContext,
        long shelfOffset,
        int maxKeyLength,
        int maxIdentityLength)
    {
        ArgumentNullException.ThrowIfNull(writeContext);
        if (writeContext.VarKeyVarIdentityMutableBatchShelves.TryGetValue(shelfOffset, out VarKeyVarIdentityMutableShelf? existing))
        {
            return existing;
        }

        ClaimVarKeyVarIdentityShelfForWriteContext(writeContext, shelfOffset);
        Span<byte> header = stackalloc byte[VarKeyVarIdentityLayout.HeaderSize];
        kernel.Read(shelfOffset, header);
        if (VarKeyVarIdentityLayout.ReadMagic(header) != VarKeyVarIdentityLayout.Magic ||
            VarKeyVarIdentityLayout.ReadFormatVersion(header) != VarKeyVarIdentityLayout.FormatVersion ||
            VarKeyVarIdentityLayout.ReadHeaderSize(header) != VarKeyVarIdentityLayout.HeaderSize)
        {
            throw new InvalidDataException("The writer-context routed VV shelf header is invalid.");
        }

        int shelfExtentSize = VarKeyVarIdentityLayout.ReadShelfExtentSize(header);
        VarKeyVarIdentityProfile profile = VarKeyVarIdentityProfile.Create(shelfExtentSize, maxKeyLength, maxIdentityLength) with { Descending = (VarKeyVarIdentityLayout.ReadFlags(header) & VarKeyVarIdentityLayout.DescendingFlag) != 0 };
        byte[] shelfBytes = new byte[shelfExtentSize];
        header.CopyTo(shelfBytes.AsSpan(0, header.Length));
        kernel.Read(shelfOffset, shelfBytes);
        if (!VarKeyVarIdentityMutableShelf.TryCreate(shelfBytes, profile, ownsBytes: false, rentSidecars: false, out VarKeyVarIdentityMutableShelf shelf))
        {
            throw new InvalidDataException("The writer-context routed VV mutable shelf bytes are invalid.");
        }

        writeContext.VarKeyVarIdentityMutableBatchShelves.Add(shelfOffset, shelf);
        return shelf;
    }

    /// <summary>
    /// Publishes all dirty ordinary `VV` shelf changes staged in a writer-local context through the serialized DataKernel publication seam.<br/>
    /// The method normalizes tombstones exactly like the durability-batch flusher before publishing the writer-local shelf images.<br/>
    /// </summary>
    /// <param name="writeContext">The writer context to publish.<br/></param>
    /// <returns>The DataKernel commit telemetry for the publication.<br/></returns>
    internal DataKernelCommitTelemetry PublishVarKeyVarIdentityWriteContext(LibraDexWriteContext writeContext)
    {
        ArgumentNullException.ThrowIfNull(writeContext);
        lock (writePublicationSync)
        {
            DataKernelCommitTelemetry telemetry;
            kernel.EnterExclusiveStoragePublication();
            try
            {
                ValidateVarKeyVarIdentityRouteClaimsForWriteContext(writeContext);
                foreach (KeyValuePair<long, VarKeyVarIdentityMutableShelf> mutableShelf in writeContext.VarKeyVarIdentityMutableBatchShelves)
                {
                    if (!mutableShelf.Value.IsDirty)
                    {
                        continue;
                    }

                    _ = mutableShelf.Value.NormalizeDeletedSlotsForPublication();
                    RewriteBatchShelfBytes(mutableShelf.Key, mutableShelf.Value.Bytes.AsSpan(0, mutableShelf.Value.Profile.ShelfExtentSize));
                    varKeyVarIdentityReadShelfCache.TryRemove(mutableShelf.Key, out _);
                    varKeyVarIdentityReadOnlyShelfCache.TryRemove(mutableShelf.Key, out _);
                }

                telemetry = kernel.Commit();
            }
            finally
            {
                kernel.ExitExclusiveStoragePublication();
            }

            ReleaseVarKeyVarIdentityShelfOwnersForWriteContext(writeContext);
            ReleaseVarKeyVarIdentityMutableShelvesForWriteContext(writeContext, clearShelfBytes: true);
            writeContext.VarKeyVarIdentityRouteClaims.Clear();
            return telemetry;
        }
    }

    /// <summary>
    /// Abandons all ordinary `VV` shelf changes staged in a writer-local context and releases its shelf ownership claims.<br/>
    /// No staged bytes enter DataKernel until publication, so abort only releases context-local mutable views.<br/>
    /// </summary>
    /// <param name="writeContext">The writer context to abandon.<br/></param>
    internal void AbortVarKeyVarIdentityWriteContext(LibraDexWriteContext writeContext)
    {
        ArgumentNullException.ThrowIfNull(writeContext);
        ReleaseVarKeyVarIdentityShelfOwnersForWriteContext(writeContext);
        ReleaseVarKeyVarIdentityMutableShelvesForWriteContext(writeContext, clearShelfBytes: true);
        writeContext.VarKeyVarIdentityRouteClaims.Clear();
    }

    private void ClaimVarKeyVarIdentityShelfForWriteContext(LibraDexWriteContext writeContext, long shelfOffset)
    {
        lock (writeContextAdmissionSync)
        {
            if (varKeyVarIdentityWriterShelfOwners.TryGetValue(shelfOffset, out LibraDexWriteContext? owner))
            {
                if (ReferenceEquals(owner, writeContext))
                {
                    return;
                }

                throw new LibraDexWriteContextVarKeyVarIdentityShelfOwnershipException(shelfOffset);
            }

            varKeyVarIdentityWriterShelfOwners.Add(shelfOffset, writeContext);
        }
    }

    private void ReleaseVarKeyVarIdentityShelfOwnersForWriteContext(LibraDexWriteContext writeContext)
    {
        lock (writeContextAdmissionSync)
        {
            foreach (long shelfOffset in writeContext.VarKeyVarIdentityMutableBatchShelves.Keys)
            {
                if (varKeyVarIdentityWriterShelfOwners.TryGetValue(shelfOffset, out LibraDexWriteContext? owner) &&
                    ReferenceEquals(owner, writeContext))
                {
                    varKeyVarIdentityWriterShelfOwners.Remove(shelfOffset);
                }
            }

            SignalReleasedWriteContextShelves();
        }
    }

    private static void ReleaseVarKeyVarIdentityMutableShelvesForWriteContext(LibraDexWriteContext writeContext, bool clearShelfBytes)
    {
        if (clearShelfBytes)
        {
            foreach (VarKeyVarIdentityMutableShelf mutableShelf in writeContext.VarKeyVarIdentityMutableBatchShelves.Values)
            {
                mutableShelf.Bytes.AsSpan(0, mutableShelf.Profile.ShelfExtentSize).Clear();
            }
        }

        foreach (VarKeyVarIdentityMutableShelf mutableShelf in writeContext.VarKeyVarIdentityMutableBatchShelves.Values)
        {
            mutableShelf.Release(clearShelfBytes);
        }

        writeContext.VarKeyVarIdentityMutableBatchShelves.Clear();
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
            profile = VarKeyVarIdentityProfile.Create(cachedShelfExtentSize, maxKeyLength, maxIdentityLength) with { Descending = (VarKeyVarIdentityLayout.ReadFlags(cachedShelfBytes) & VarKeyVarIdentityLayout.DescendingFlag) != 0 };
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
        profile = VarKeyVarIdentityProfile.Create(shelfExtentSize, maxKeyLength, maxIdentityLength) with { Descending = (VarKeyVarIdentityLayout.ReadFlags(header) & VarKeyVarIdentityLayout.DescendingFlag) != 0 };
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
            profile = VarKeyVarIdentityProfile.Create(shelfExtentSize, maxKeyLength, maxIdentityLength) with { Descending = (VarKeyVarIdentityLayout.ReadFlags(header) & VarKeyVarIdentityLayout.DescendingFlag) != 0 };
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
        varKeyVarIdentityReadShelfCache.TryRemove(shelfOffset, out _);
        varKeyVarIdentityReadOnlyShelfCache.TryRemove(shelfOffset, out _);
        return CommitAndInvalidateRouterReadCache();
    }
}
