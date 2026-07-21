using LibraDex.Layouts;
using LibraDex.Views;

namespace LibraDex;

internal sealed partial class LibraDexFileSession
{
    /// <summary>
    /// Counts an inclusive encoded `VS16` key range by exact boundary-shelf searches plus contained shelf metadata.<br/>
    /// Physical route entries that reference the same target are merged before descent so shared prefix runs contribute once while retaining both boundary flags.<br/>
    /// The traversal uses pooled visited state and retries when the storage mutation version changes, avoiding identity materialization and mixed-version results.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The routed `VS16` root router offset.<br/></param>
    /// <param name="maxKeyLength">The maximum encoded key length accepted by the index.<br/></param>
    /// <param name="lowerKey">The inclusive encoded lower key.<br/></param>
    /// <param name="upperKey">The inclusive encoded upper key.<br/></param>
    /// <returns>The number of identities whose encoded keys are inside the inclusive range.<br/></returns>
    internal long CountVarKeyScalar16IdentityRangeByShelfScoop(
        long rootRouterOffset,
        int maxKeyLength,
        ReadOnlySpan<byte> lowerKey,
        ReadOnlySpan<byte> upperKey)
    {
        if (rootRouterOffset <= 0)
            throw new ArgumentOutOfRangeException(nameof(rootRouterOffset), rootRouterOffset, "The VS16 scoop-count root router offset must be positive.");

        if (maxKeyLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxKeyLength), maxKeyLength, "The VS16 scoop-count maximum key length must be positive.");

        if (lowerKey.SequenceCompareTo(upperKey) > 0)
            return 0;

        using RouteVisitedOffsetSet visitedTargets = RouteVisitedOffsetSet.Rent();
        using RouteVisitedOffsetSet visitedRouters = RouteVisitedOffsetSet.Rent();
        for (int attempt = 0; attempt < 8; attempt++)
        {
            visitedTargets.Clear();
            visitedRouters.Clear();
            long mutationVersion = kernel.MutationVersion;
            VarKeyScalar16RoutePathTarget lowerTarget = WalkVarKeyScalar16RoutePathTarget(rootRouterOffset, lowerKey, DefaultVarKeyScalar16MaxRouterHops);
            VarKeyScalar16RoutePathTarget upperTarget = WalkVarKeyScalar16RoutePathTarget(rootRouterOffset, upperKey, DefaultVarKeyScalar16MaxRouterHops);
            if (lowerTarget.Target.Kind == VarKeyScalar16RouteTargetKind.Shelf &&
                upperTarget.Target.Kind == VarKeyScalar16RouteTargetKind.Shelf &&
                lowerTarget.Target.Offset == upperTarget.Target.Offset)
            {
                long sameShelfCount = CountVarKeyScalar16ShelfKeyRangeNarrow(
                    lowerTarget.Target.Offset,
                    maxKeyLength,
                    lowerKey,
                    upperKey);
                if (kernel.MutationVersion == mutationVersion)
                    return sameShelfCount;

                continue;
            }

            if (lowerTarget.Target.Kind == VarKeyScalar16RouteTargetKind.None &&
                upperTarget.Target.Kind == VarKeyScalar16RouteTargetKind.None &&
                lowerTarget.ParentRouterOffset == upperTarget.ParentRouterOffset &&
                lowerTarget.RouteIndex == upperTarget.RouteIndex)
            {
                if (kernel.MutationVersion == mutationVersion)
                    return 0;

                continue;
            }

            _ = visitedRouters.Add(rootRouterOffset);
            long count = CountVarKeyScalar16ScoopRouter(
                rootRouterOffset,
                maxKeyLength,
                lowerKey,
                upperKey,
                lowerEdge: true,
                upperEdge: true,
                visitedTargets,
                visitedRouters,
                DefaultVarKeyScalar16MaxRouterHops);
            if (kernel.MutationVersion == mutationVersion)
                return count;
        }

        throw new InvalidOperationException("The VS16 scoop count could not observe a stable storage mutation version after eight attempts.");
    }

    /// <summary>
    /// Counts the logical target runs selected below one `VS16` router.<br/>
    /// Direct and compressed route intervals are intersected with the active boundaries, then identical physical targets are merged before traversal.<br/>
    /// </summary>
    /// <param name="routerOffset">The router page offset to traverse.<br/></param>
    /// <param name="maxKeyLength">The maximum encoded key length accepted by the index.<br/></param>
    /// <param name="lowerKey">The inclusive encoded lower key.<br/></param>
    /// <param name="upperKey">The inclusive encoded upper key.<br/></param>
    /// <param name="lowerEdge">Whether this subtree can contain the lower boundary.<br/></param>
    /// <param name="upperEdge">Whether this subtree can contain the upper boundary.<br/></param>
    /// <param name="visitedTargets">Pooled shelf offsets already counted by this attempt.<br/></param>
    /// <param name="visitedRouters">Pooled router offsets already traversed by this attempt.<br/></param>
    /// <param name="remainingRouterHops">The remaining corruption and cycle safety budget.<br/></param>
    /// <returns>The matching identity count below this router.<br/></returns>
    private long CountVarKeyScalar16ScoopRouter(
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
            throw new InvalidDataException("The VS16 scoop count exceeded the configured router hop count.");

        Span<byte> routerBytes = stackalloc byte[RouterLayout.Size];
        ReadRouterPageUsingArenaCache(routerOffset, routerBytes);
        RouterReader router = new(routerBytes);
        if (!router.IsValid)
            throw new InvalidDataException("The VS16 scoop count found an invalid router.");

        int routeCount = router.HasDirectIndex ? RouterLayout.MaxOneByteRouteCount : router.RouteCount;
        Span<long> selectedTargets = stackalloc long[routeCount];
        Span<byte> selectedFlags = stackalloc byte[routeCount];
        int selectedCount = 0;
        if (router.PrefixByteCount == 1)
        {
            byte lowerPrefix = lowerEdge ? GetVarKeyScalar16Prefix(lowerKey, router.KeyDepth) : byte.MinValue;
            byte upperPrefix = upperEdge ? GetVarKeyScalar16Prefix(upperKey, router.KeyDepth) : byte.MaxValue;
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
                MergeVarKeyScalar16ScoopTarget(targetOffset, flags, selectedTargets, selectedFlags, ref selectedCount);
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
                MergeVarKeyScalar16ScoopTarget(targetOffset, flags, selectedTargets, selectedFlags, ref selectedCount);
            }
        }

        long count = 0;
        for (int selectedIndex = 0; selectedIndex < selectedCount; selectedIndex++)
        {
            byte flags = selectedFlags[selectedIndex];
            count += CountVarKeyScalar16ScoopTarget(
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
    /// Merges one selected `VS16` physical target into the router-local logical target list.<br/>
    /// Repeated route slots retain the union of lower and upper boundary flags without allocating a dictionary.<br/>
    /// </summary>
    /// <param name="targetOffset">The selected physical target offset.<br/></param>
    /// <param name="flags">The lower and upper boundary bit flags.<br/></param>
    /// <param name="selectedTargets">The router-local selected target offsets.<br/></param>
    /// <param name="selectedFlags">The router-local selected boundary flags.<br/></param>
    /// <param name="selectedCount">The number of populated entries in the selected spans.<br/></param>
    private static void MergeVarKeyScalar16ScoopTarget(
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
    /// Counts one merged `VS16` route target using exact boundary work or contained shelf metadata.<br/>
    /// Routers recurse with the retained edge flags; boundary shelves search encoded keys and contained shelves read only the validated live-count header.<br/>
    /// </summary>
    /// <param name="targetOffset">The merged route target offset.<br/></param>
    /// <param name="maxKeyLength">The maximum encoded key length accepted by the index.<br/></param>
    /// <param name="lowerKey">The inclusive encoded lower key.<br/></param>
    /// <param name="upperKey">The inclusive encoded upper key.<br/></param>
    /// <param name="lowerEdge">Whether the target can contain the lower boundary.<br/></param>
    /// <param name="upperEdge">Whether the target can contain the upper boundary.<br/></param>
    /// <param name="visitedTargets">Pooled shelves already counted by this attempt.<br/></param>
    /// <param name="visitedRouters">Pooled routers already traversed by this attempt.<br/></param>
    /// <param name="remainingRouterHops">The remaining corruption and cycle safety budget.<br/></param>
    /// <returns>The matching identity count represented by this target.<br/></returns>
    private long CountVarKeyScalar16ScoopTarget(
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
        VarKeyScalar16RouteTargetKind kind = ClassifyVarKeyScalar16RouteTarget(targetOffset);
        if (kind == VarKeyScalar16RouteTargetKind.Router)
        {
            if (!visitedRouters.Add(targetOffset))
                return 0;

            return CountVarKeyScalar16ScoopRouter(
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

        if (kind != VarKeyScalar16RouteTargetKind.Shelf)
            throw new InvalidDataException("The VS16 scoop-count target is not a shelf or router.");

        if (!visitedTargets.Add(targetOffset))
            return 0;

        return lowerEdge || upperEdge
            ? CountVarKeyScalar16ShelfKeyRangeNarrow(targetOffset, maxKeyLength, lowerKey, upperKey)
            : ReadVarKeyScalar16ShelfItemCountNarrow(targetOffset, maxKeyLength);
    }
}
