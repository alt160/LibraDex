using LibraDex.Layouts;
using LibraDex.Views;

namespace LibraDex;

internal sealed partial class LibraDexFileSession
{
    /// <summary>
    /// Counts an inclusive encoded `VS8` key range by locating boundary extents and summing contained shelf metadata.<br/>
    /// Only targets that can contain the lower or upper boundary decode shelf keys; every fully contained ordinary shelf, duplicate-run shelf, and terminal identity chain contributes its persisted live count directly.<br/>
    /// Contiguous or compressed route entries that reference the same target are merged before descent so one logical prefix range is counted exactly once with both boundary flags preserved.<br/>
    /// The complete result is retried when the raw-storage mutation version changes during traversal, preventing a count from mixing topology versions.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The routed `VS8` root router offset.<br/></param>
    /// <param name="maxKeyLength">The maximum encoded key length accepted by the index.<br/></param>
    /// <param name="lowerKey">The inclusive encoded lower key.<br/></param>
    /// <param name="upperKey">The inclusive encoded upper key.<br/></param>
    /// <returns>The number of identities whose encoded keys are inside the inclusive range.<br/></returns>
    internal long CountVarKeyScalar8IdentityRangeByShelfScoop(
        long rootRouterOffset,
        int maxKeyLength,
        ReadOnlySpan<byte> lowerKey,
        ReadOnlySpan<byte> upperKey)
    {
        if (rootRouterOffset <= 0)
            throw new ArgumentOutOfRangeException(nameof(rootRouterOffset), rootRouterOffset, "The VS8 scoop-count root router offset must be positive.");

        if (maxKeyLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxKeyLength), maxKeyLength, "The VS8 scoop-count maximum key length must be positive.");

        if (lowerKey.SequenceCompareTo(upperKey) > 0)
            return 0;

        using RouteVisitedOffsetSet visitedTargets = RouteVisitedOffsetSet.Rent();
        using RouteVisitedOffsetSet visitedRouters = RouteVisitedOffsetSet.Rent();
        for (int attempt = 0; attempt < 8; attempt++)
        {
            visitedTargets.Clear();
            visitedRouters.Clear();
            long mutationVersion = kernel.MutationVersion;
            _ = visitedRouters.Add(rootRouterOffset);
            long count = CountVarKeyScalar8ScoopRouter(
                rootRouterOffset,
                maxKeyLength,
                lowerKey,
                upperKey,
                lowerEdge: true,
                upperEdge: true,
                visitedTargets,
                visitedRouters,
                DefaultVarKeyScalar8MaxRouterHops);
            if (kernel.MutationVersion == mutationVersion)
                return count;
        }

        throw new InvalidOperationException("The VS8 scoop count could not observe a stable storage mutation version after eight attempts.");
    }

    /// <summary>
    /// Counts the selected logical target runs below one `VS8` router.<br/>
    /// One-byte direct and compressed routes are intersected as persisted prefix intervals; multi-byte routes reuse the allocation-free route-bound classifier used by the ordinary range reader.<br/>
    /// Entries sharing a target are merged before that target is counted, preserving lower and upper boundary ownership without double-counting physical storage.<br/>
    /// </summary>
    /// <param name="routerOffset">The router page offset to traverse.<br/></param>
    /// <param name="maxKeyLength">The maximum encoded key length accepted by the index.<br/></param>
    /// <param name="lowerKey">The inclusive encoded lower key.<br/></param>
    /// <param name="upperKey">The inclusive encoded upper key.<br/></param>
    /// <param name="lowerEdge">Whether this subtree can contain the lower boundary.<br/></param>
    /// <param name="upperEdge">Whether this subtree can contain the upper boundary.<br/></param>
    /// <param name="visitedTargets">Pooled target offsets already counted by this attempt.<br/></param>
    /// <param name="visitedRouters">Pooled router offsets already traversed by this attempt.<br/></param>
    /// <param name="remainingRouterHops">The remaining corruption/cycle safety budget.<br/></param>
    /// <returns>The matching identity count below the router.<br/></returns>
    private long CountVarKeyScalar8ScoopRouter(
        long routerOffset,
        int maxKeyLength,
        ReadOnlySpan<byte> lowerKey,
        ReadOnlySpan<byte> upperKey,
        bool lowerEdge,
        bool upperEdge,
        RouteVisitedOffsetSet visitedTargets,
        RouteVisitedOffsetSet visitedRouters,
        int remainingRouterHops)
    {
        if (remainingRouterHops <= 0)
            throw new InvalidDataException("The VS8 scoop count exceeded the configured router hop count.");

        Span<byte> routerBytes = stackalloc byte[RouterLayout.Size];
        ReadRouterPageUsingArenaCache(routerOffset, routerBytes);
        RouterReader router = new(routerBytes);
        if (!router.IsValid)
            throw new InvalidDataException("The VS8 scoop count found an invalid router.");

        int routeCount = router.HasDirectIndex ? RouterLayout.MaxOneByteRouteCount : router.RouteCount;
        Span<long> selectedTargets = stackalloc long[routeCount];
        Span<byte> selectedFlags = stackalloc byte[routeCount];
        int selectedCount = 0;
        if (router.PrefixByteCount == 1)
        {
            byte lowerPrefix = lowerEdge ? GetVarKeyScalar8Prefix(lowerKey, router.KeyDepth) : byte.MinValue;
            byte upperPrefix = upperEdge ? GetVarKeyScalar8Prefix(upperKey, router.KeyDepth) : byte.MaxValue;
            for (int routeIndex = 0; routeIndex < routeCount; routeIndex++)
            {
                byte routeStart = router.GetRoutePrefixStartAt(routeIndex);
                byte routeEnd = router.GetRoutePrefixEndAt(routeIndex);
                if (routeEnd < lowerPrefix || routeStart > upperPrefix)
                    continue;

                long targetOffset = router.GetRouteTargetAt(routeIndex);
                if (targetOffset == 0)
                    continue;

                byte flags = 0;
                if (lowerEdge && lowerPrefix >= routeStart && lowerPrefix <= routeEnd)
                    flags |= 1;
                if (upperEdge && upperPrefix >= routeStart && upperPrefix <= routeEnd)
                    flags |= 2;

                int selectedIndex = 0;
                while (selectedIndex < selectedCount && selectedTargets[selectedIndex] != targetOffset)
                    selectedIndex++;

                if (selectedIndex == selectedCount)
                {
                    selectedTargets[selectedCount] = targetOffset;
                    selectedFlags[selectedCount] = flags;
                    selectedCount++;
                }
                else
                {
                    selectedFlags[selectedIndex] |= flags;
                }
            }
        }
        else
        {
            int lowerRouteIndex = -1;
            int upperRouteIndex = -1;
            if (lowerEdge)
                _ = router.FindTarget(lowerKey, router.KeyDepth, out lowerRouteIndex);
            if (upperEdge)
                _ = router.FindTarget(upperKey, router.KeyDepth, out upperRouteIndex);

            for (int routeIndex = 0; routeIndex < routeCount; routeIndex++)
            {
                if (!router.TrySelectMultiByteRangeRoute(
                    routeIndex,
                    lowerKey,
                    upperKey,
                    maxKeyLength,
                    lowerEdge,
                    upperEdge,
                    lowerRouteIndex,
                    upperRouteIndex,
                    out long targetOffset,
                    out bool childLowerEdge,
                    out bool childUpperEdge))
                {
                    continue;
                }

                byte flags = (byte)((childLowerEdge ? 1 : 0) | (childUpperEdge ? 2 : 0));
                int selectedIndex = 0;
                while (selectedIndex < selectedCount && selectedTargets[selectedIndex] != targetOffset)
                    selectedIndex++;

                if (selectedIndex == selectedCount)
                {
                    selectedTargets[selectedCount] = targetOffset;
                    selectedFlags[selectedCount] = flags;
                    selectedCount++;
                }
                else
                {
                    selectedFlags[selectedIndex] |= flags;
                }
            }
        }

        long count = 0;
        for (int selectedIndex = 0; selectedIndex < selectedCount; selectedIndex++)
        {
            byte flags = selectedFlags[selectedIndex];
            count += CountVarKeyScalar8ScoopTarget(
                selectedTargets[selectedIndex],
                maxKeyLength,
                lowerKey,
                upperKey,
                (flags & 1) != 0,
                (flags & 2) != 0,
                visitedTargets,
                visitedRouters,
                remainingRouterHops - 1);
        }

        return count;
    }

    /// <summary>
    /// Counts one merged logical `VS8` route target using exact boundary work or contained metadata work.<br/>
    /// Routers continue the logical-run traversal, boundary shelves perform binary lower/upper slot searches, and contained shelves read only validated count headers.<br/>
    /// Terminal roots compare their encoded key only on a boundary; contained terminal chains are summed directly from identity-shelf headers.<br/>
    /// </summary>
    /// <param name="targetOffset">The merged route target offset.<br/></param>
    /// <param name="maxKeyLength">The maximum encoded key length accepted by the index.<br/></param>
    /// <param name="lowerKey">The inclusive encoded lower key.<br/></param>
    /// <param name="upperKey">The inclusive encoded upper key.<br/></param>
    /// <param name="lowerEdge">Whether the target can contain the lower boundary.<br/></param>
    /// <param name="upperEdge">Whether the target can contain the upper boundary.<br/></param>
    /// <param name="visitedTargets">Pooled non-router targets already counted by this attempt.<br/></param>
    /// <param name="visitedRouters">Pooled routers already traversed by this attempt.<br/></param>
    /// <param name="remainingRouterHops">The remaining corruption/cycle safety budget.<br/></param>
    /// <returns>The matching identity count represented by the target.<br/></returns>
    private long CountVarKeyScalar8ScoopTarget(
        long targetOffset,
        int maxKeyLength,
        ReadOnlySpan<byte> lowerKey,
        ReadOnlySpan<byte> upperKey,
        bool lowerEdge,
        bool upperEdge,
        RouteVisitedOffsetSet visitedTargets,
        RouteVisitedOffsetSet visitedRouters,
        int remainingRouterHops)
    {
        VarKeyScalar8RouteTargetKind kind = ClassifyVarKeyScalar8RouteTarget(targetOffset);
        if (kind == VarKeyScalar8RouteTargetKind.Router)
        {
            if (!visitedRouters.Add(targetOffset))
                return 0;

            return CountVarKeyScalar8ScoopRouter(
                targetOffset,
                maxKeyLength,
                lowerKey,
                upperKey,
                lowerEdge,
                upperEdge,
                visitedTargets,
                visitedRouters,
                remainingRouterHops);
        }

        if (!visitedTargets.Add(targetOffset))
            return 0;

        if (kind == VarKeyScalar8RouteTargetKind.Shelf)
        {
            return CountVarKeyScalar8ScoopShelfChain(
                targetOffset,
                maxKeyLength,
                lowerKey,
                upperKey,
                lowerEdge || upperEdge,
                visitedTargets);
        }

        if (kind == VarKeyScalar8RouteTargetKind.TerminalIdentityRoot)
        {
            return lowerEdge || upperEdge
                ? CountVarKeyScalar8TerminalIdentityRootKeyRangeNarrow(targetOffset, lowerKey, upperKey)
                : CountTerminalIdentity8RootNarrow(targetOffset);
        }

        throw new InvalidDataException("The VS8 scoop-count target is not a shelf, terminal identity root, or router.");
    }

    /// <summary>
    /// Counts one ordinary or duplicate-run `VS8` shelf extent.<br/>
    /// Boundary extents use the shelf's binary key bounds against the complete query; contained extents read only the validated live-count projection from each physical shelf header.<br/>
    /// Linked duplicate-run shelves remain one logical extent and are cycle-protected through the caller's pooled visited set.<br/>
    /// </summary>
    /// <param name="headShelfOffset">The route-visible head shelf offset.<br/></param>
    /// <param name="maxKeyLength">The maximum encoded key length accepted by the index.<br/></param>
    /// <param name="lowerKey">The inclusive encoded lower key.<br/></param>
    /// <param name="upperKey">The inclusive encoded upper key.<br/></param>
    /// <param name="boundary">Whether the logical extent can contain either query boundary.<br/></param>
    /// <param name="visitedTargets">Pooled target offsets already counted by this attempt.<br/></param>
    /// <returns>The matching identity count in the logical shelf extent.<br/></returns>
    private long CountVarKeyScalar8ScoopShelfChain(
        long headShelfOffset,
        int maxKeyLength,
        ReadOnlySpan<byte> lowerKey,
        ReadOnlySpan<byte> upperKey,
        bool boundary,
        RouteVisitedOffsetSet visitedTargets)
    {
        long count = 0;
        long currentOffset = headShelfOffset;
        while (currentOffset != 0)
        {
            long nextOffset;
            count += boundary
                ? CountVarKeyScalar8ShelfKeyRangeNarrow(currentOffset, maxKeyLength, lowerKey, upperKey, out nextOffset)
                : ReadVarKeyScalar8ShelfItemCountNarrow(currentOffset, maxKeyLength, out nextOffset);
            currentOffset = nextOffset;
            if (currentOffset != 0 && !visitedTargets.Add(currentOffset))
                throw new InvalidDataException("The VS8 scoop count found a duplicate-run shelf cycle or shared physical tail.");
        }

        return count;
    }
}
