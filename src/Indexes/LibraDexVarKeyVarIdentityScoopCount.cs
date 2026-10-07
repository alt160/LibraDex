using LibraDex.Layouts;
using LibraDex.Views;

namespace LibraDex;

internal sealed partial class LibraDexFileSession
{
    /// <summary>
    /// Counts an inclusive encoded `VV` key range by exact boundary searches plus contained shelf and terminal metadata.<br/>
    /// Repeated physical route targets are merged before descent, preventing shared prefix slots from contributing the same shelf or terminal root more than once.<br/>
    /// Pooled visited state avoids per-call hash-set allocation, and mutation-version retry prevents mixed-topology results during concurrent publication.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The routed `VV` root router offset.<br/></param>
    /// <param name="maxKeyLength">The maximum encoded key length accepted by the index.<br/></param>
    /// <param name="maxIdentityLength">The maximum raw identity length accepted by the index.<br/></param>
    /// <param name="lowerKey">The inclusive encoded lower key.<br/></param>
    /// <param name="upperKey">The inclusive encoded upper key.<br/></param>
    /// <returns>The number of identities whose encoded keys are inside the inclusive range.<br/></returns>
    internal long CountVarKeyVarIdentityRangeByShelfScoop(
        long rootRouterOffset,
        int maxKeyLength,
        int maxIdentityLength,
        ReadOnlySpan<byte> lowerKey,
        ReadOnlySpan<byte> upperKey)
    {
        if (rootRouterOffset <= 0)
            throw new ArgumentOutOfRangeException(nameof(rootRouterOffset), rootRouterOffset, "The VV scoop-count root router offset must be positive.");

        if (maxKeyLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxKeyLength), maxKeyLength, "The VV scoop-count maximum key length must be positive.");

        if (maxIdentityLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxIdentityLength), maxIdentityLength, "The VV scoop-count maximum identity length must be positive.");

        if (lowerKey.SequenceCompareTo(upperKey) > 0)
            return 0;

        using RouteVisitedOffsetSet visitedTargets = RouteVisitedOffsetSet.Rent();
        using RouteVisitedOffsetSet visitedRouters = RouteVisitedOffsetSet.Rent();
        for (int attempt = 0; attempt < 8; attempt++)
        {
            visitedTargets.Clear();
            visitedRouters.Clear();
            long mutationVersion = kernel.MutationVersion;
            VarKeyVarIdentityRoutePathTarget lowerTarget = WalkVarKeyVarIdentityRoutePathTarget(rootRouterOffset, lowerKey, DefaultVarKeyVarIdentityMaxRouterHops);
            VarKeyVarIdentityRoutePathTarget upperTarget = WalkVarKeyVarIdentityRoutePathTarget(rootRouterOffset, upperKey, DefaultVarKeyVarIdentityMaxRouterHops);
            if (lowerTarget.Target.Kind == upperTarget.Target.Kind &&
                lowerTarget.Target.Offset != 0 &&
                lowerTarget.Target.Offset == upperTarget.Target.Offset)
            {
                long sameExtentCount = lowerTarget.Target.Kind switch
                {
                    VarKeyVarIdentityRouteTargetKind.Shelf => CountVarKeyVarIdentityShelfKeyRangeNarrow(
                        lowerTarget.Target.Offset,
                        maxKeyLength,
                        maxIdentityLength,
                        lowerKey,
                        upperKey),
                    VarKeyVarIdentityRouteTargetKind.TerminalVarIdentityRoot => CountVarKeyVarIdentityTerminalRootKeyRangeNarrow(
                        lowerTarget.Target.Offset,
                        lowerKey,
                        upperKey),
                    _ => -1
                };
                if (sameExtentCount >= 0 && kernel.MutationVersion == mutationVersion)
                    return sameExtentCount;

                if (sameExtentCount >= 0)
                    continue;
            }

            if (lowerTarget.Target.Kind == VarKeyVarIdentityRouteTargetKind.None &&
                upperTarget.Target.Kind == VarKeyVarIdentityRouteTargetKind.None &&
                lowerTarget.ParentRouterOffset == upperTarget.ParentRouterOffset &&
                lowerTarget.RouteIndex == upperTarget.RouteIndex)
            {
                if (kernel.MutationVersion == mutationVersion)
                    return 0;

                continue;
            }

            _ = visitedRouters.Add(rootRouterOffset);
            long count = CountVarKeyVarIdentityScoopRouter(
                rootRouterOffset,
                maxKeyLength,
                maxIdentityLength,
                lowerKey,
                upperKey,
                lowerEdge: true,
                upperEdge: true,
                visitedTargets,
                visitedRouters,
                DefaultVarKeyVarIdentityMaxRouterHops);
            if (kernel.MutationVersion == mutationVersion)
                return count;
        }

        throw new InvalidOperationException("The VV scoop count could not observe a stable storage mutation version after eight attempts.");
    }

    /// <summary>
    /// Counts the logical target runs selected below one `VV` router.<br/>
    /// Direct and compressed route intervals are intersected with active boundaries and identical physical targets are merged before traversal.<br/>
    /// </summary>
    /// <param name="routerOffset">The router page offset to traverse.<br/></param>
    /// <param name="maxKeyLength">The maximum encoded key length accepted by the index.<br/></param>
    /// <param name="maxIdentityLength">The maximum raw identity length accepted by the index.<br/></param>
    /// <param name="lowerKey">The inclusive encoded lower key.<br/></param>
    /// <param name="upperKey">The inclusive encoded upper key.<br/></param>
    /// <param name="lowerEdge">Whether this subtree can contain the lower boundary.<br/></param>
    /// <param name="upperEdge">Whether this subtree can contain the upper boundary.<br/></param>
    /// <param name="visitedTargets">Pooled shelf and terminal offsets already counted by this attempt.<br/></param>
    /// <param name="visitedRouters">Pooled router offsets already traversed by this attempt.<br/></param>
    /// <param name="remainingRouterHops">The remaining corruption and cycle safety budget.<br/></param>
    /// <returns>The matching identity count below this router.<br/></returns>
    private long CountVarKeyVarIdentityScoopRouter(
        long routerOffset,
        int maxKeyLength,
        int maxIdentityLength,
        ReadOnlySpan<byte> lowerKey,
        ReadOnlySpan<byte> upperKey,
        bool lowerEdge,
        bool upperEdge,
        RouteVisitedOffsetSet visitedTargets,
        RouteVisitedOffsetSet visitedRouters,
        int remainingRouterHops)
    {
        if (remainingRouterHops <= 0)
            throw new InvalidDataException("The VV scoop count exceeded the configured router hop count.");

        Span<byte> routerBytes = stackalloc byte[RouterLayout.Size];
        ReadRouterPageUsingArenaCache(routerOffset, routerBytes);
        RouterReader router = new(routerBytes);
        if (!router.IsValid)
            throw new InvalidDataException("The VV scoop count found an invalid router.");

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
                MergeVarKeyVarIdentityScoopTarget(targetOffset, flags, selectedTargets, selectedFlags, ref selectedCount);
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
                MergeVarKeyVarIdentityScoopTarget(targetOffset, flags, selectedTargets, selectedFlags, ref selectedCount);
            }
        }

        long count = 0;
        for (int selectedIndex = 0; selectedIndex < selectedCount; selectedIndex++)
        {
            byte flags = selectedFlags[selectedIndex];
            count += CountVarKeyVarIdentityScoopTarget(
                selectedTargets[selectedIndex],
                maxKeyLength,
                maxIdentityLength,
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
    /// Merges one selected `VV` physical target into the router-local logical target list.<br/>
    /// Repeated route slots preserve the union of lower and upper boundary flags without allocating a dictionary.<br/>
    /// </summary>
    /// <param name="targetOffset">The selected physical target offset.<br/></param>
    /// <param name="flags">The lower and upper boundary bit flags.<br/></param>
    /// <param name="selectedTargets">The router-local selected target offsets.<br/></param>
    /// <param name="selectedFlags">The router-local selected boundary flags.<br/></param>
    /// <param name="selectedCount">The number of populated entries in the selected spans.<br/></param>
    private static void MergeVarKeyVarIdentityScoopTarget(
        long targetOffset,
        byte flags,
        Span<long> selectedTargets,
        Span<byte> selectedFlags,
        ref int selectedCount)
    {
        int selectedIndex = 0;
        while (selectedIndex < selectedCount && selectedTargets[selectedIndex] != targetOffset)
            selectedIndex++;

        if (selectedIndex == selectedCount)
        {
            selectedTargets[selectedCount] = targetOffset;
            selectedFlags[selectedCount] = flags;
            selectedCount++;
            return;
        }

        selectedFlags[selectedIndex] |= flags;
    }

    /// <summary>
    /// Counts one merged `VV` route target using exact boundary work or contained metadata work.<br/>
    /// Routers recurse, ordinary boundary shelves search encoded keys, and contained shelves or terminal roots contribute their validated persisted counts.<br/>
    /// </summary>
    /// <param name="targetOffset">The merged route target offset.<br/></param>
    /// <param name="maxKeyLength">The maximum encoded key length accepted by the index.<br/></param>
    /// <param name="maxIdentityLength">The maximum raw identity length accepted by the index.<br/></param>
    /// <param name="lowerKey">The inclusive encoded lower key.<br/></param>
    /// <param name="upperKey">The inclusive encoded upper key.<br/></param>
    /// <param name="lowerEdge">Whether the target can contain the lower boundary.<br/></param>
    /// <param name="upperEdge">Whether the target can contain the upper boundary.<br/></param>
    /// <param name="visitedTargets">Pooled shelf and terminal offsets already counted by this attempt.<br/></param>
    /// <param name="visitedRouters">Pooled routers already traversed by this attempt.<br/></param>
    /// <param name="remainingRouterHops">The remaining corruption and cycle safety budget.<br/></param>
    /// <returns>The matching identity count represented by this target.<br/></returns>
    private long CountVarKeyVarIdentityScoopTarget(
        long targetOffset,
        int maxKeyLength,
        int maxIdentityLength,
        ReadOnlySpan<byte> lowerKey,
        ReadOnlySpan<byte> upperKey,
        bool lowerEdge,
        bool upperEdge,
        RouteVisitedOffsetSet visitedTargets,
        RouteVisitedOffsetSet visitedRouters,
        int remainingRouterHops)
    {
        VarKeyVarIdentityRouteTargetKind kind = ClassifyVarKeyVarIdentityRouteTarget(targetOffset);
        if (kind == VarKeyVarIdentityRouteTargetKind.Router)
        {
            if (!visitedRouters.Add(targetOffset))
                return 0;

            return CountVarKeyVarIdentityScoopRouter(
                targetOffset,
                maxKeyLength,
                maxIdentityLength,
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

        if (kind == VarKeyVarIdentityRouteTargetKind.Shelf)
        {
            return lowerEdge || upperEdge
                ? CountVarKeyVarIdentityShelfKeyRangeNarrow(targetOffset, maxKeyLength, maxIdentityLength, lowerKey, upperKey)
                : ReadVarKeyVarIdentityShelfItemCountNarrow(targetOffset, maxKeyLength, maxIdentityLength);
        }

        if (kind == VarKeyVarIdentityRouteTargetKind.TerminalVarIdentityRoot)
        {
            return lowerEdge || upperEdge
                ? CountVarKeyVarIdentityTerminalRootKeyRangeNarrow(targetOffset, lowerKey, upperKey)
                : CountTerminalVarIdentityRootNarrow(targetOffset, TerminalIdentityRootLayout.ShapeVarKey);
        }

        throw new InvalidDataException("The VV scoop-count target is not a shelf, terminal root, or router.");
    }
}
