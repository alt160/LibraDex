using LibraDex.Layouts;
using LibraDex.Views;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace LibraDex;

internal sealed partial class LibraDexFileSession
{
    private const byte SharedShelfGrowthShapeVarKeyScalar16 = 9;
    private const byte SharedShelfGrowthShapeVarKeyVarIdentity = 10;
    private const byte SharedShelfGrowthShapeScalar16VarIdentity = 11;

    /// <summary>
    /// Publishes one copied shelf growth through every reachable router owner of the obsolete shelf.<br/>
    /// Replacement bytes are prepared by the caller before this method enters exclusive storage publication; the guarded section discovers and revalidates the complete owner set, appends the replacement shelf, rewrites every owner, commits once, and releases the old extent.<br/>
    /// A false return means the walked parent route no longer belongs to the obsolete shelf owner set, so the caller must discard its prepared image and retry against current topology.<br/>
    /// </summary>
    /// <param name="shape">The internal shape discriminator used only to isolate topology-owner locks.<br/></param>
    /// <param name="shapeName">The diagnostic shelf-shape name used in corruption failures.<br/></param>
    /// <param name="rootRouterOffset">The stable root router of the index being mutated.<br/></param>
    /// <param name="expectedParentRouterOffset">The walked parent router that must still own the obsolete shelf.<br/></param>
    /// <param name="expectedRouteIndex">The walked route index that must still target the obsolete shelf.<br/></param>
    /// <param name="obsoleteShelfOffset">The copied shelf that every reachable owner must stop referencing.<br/></param>
    /// <param name="obsoleteShelfExtentSize">The physical extent size released after successful publication.<br/></param>
    /// <param name="replacementShelfExtentSize">The physical extent size reserved for the grown shelf.<br/></param>
    /// <param name="replacementShelf">The complete grown shelf bytes prepared from the old authoritative shelf image and incoming tuple.<br/></param>
    /// <param name="deferRouterCacheInvalidation">Whether the caller's existing batch path defers the final router-cache invalidation until its durability boundary.<br/></param>
    /// <param name="telemetry">Receives the one publication commit's storage telemetry.<br/></param>
    /// <param name="replacementShelfOffset">Receives the appended grown shelf offset after successful publication.<br/></param>
    /// <returns><see langword="true"/> when the complete reachable owner set was published; otherwise <see langword="false"/>.<br/></returns>
    private bool TryPublishSharedShelfGrowth(
        byte shape,
        string shapeName,
        long rootRouterOffset,
        long expectedParentRouterOffset,
        int expectedRouteIndex,
        long obsoleteShelfOffset,
        int obsoleteShelfExtentSize,
        int replacementShelfExtentSize,
        ReadOnlySpan<byte> replacementShelf,
        bool deferRouterCacheInvalidation,
        out DataKernelCommitTelemetry telemetry,
        out long replacementShelfOffset)
    {
        telemetry = default;
        replacementShelfOffset = 0;
        object topologyOwner = GetPrimitiveTopologyOwner(new PrimitiveTopologyOwnerKey(
            shape,
            2,
            rootRouterOffset,
            obsoleteShelfOffset,
            expectedRouteIndex));
        lock (topologyOwner)
        {
            lock (writePublicationSync)
            {
                kernel.EnterExclusiveStoragePublication();
                try
                {
                    Dictionary<long, List<int>> parentReferences = CollectSharedShelfParentReferences(
                        shapeName,
                        rootRouterOffset,
                        obsoleteShelfOffset);
                    if (!parentReferences.TryGetValue(expectedParentRouterOffset, out List<int>? expectedParentRoutes) ||
                        !expectedParentRoutes.Contains(expectedRouteIndex))
                    {
                        return false;
                    }

                    RawDataReservation replacementReservation = kernel.Reserve(replacementShelfExtentSize);
                    replacementShelf.CopyTo(replacementReservation.Span);
                    RewriteSharedShelfParentReferences(
                        shapeName,
                        parentReferences,
                        obsoleteShelfOffset,
                        replacementReservation.Extent.Offset);
                    kernel.StageExtentRetirement(obsoleteShelfOffset, obsoleteShelfExtentSize);
                    telemetry = deferRouterCacheInvalidation
                        ? CommitAndDeferRouterReadCacheInvalidation()
                        : CommitAndInvalidateRouterReadCache();
                    replacementShelfOffset = replacementReservation.Extent.Offset;
                    return true;
                }
                finally
                {
                    kernel.ExitExclusiveStoragePublication();
                }
            }
        }
    }

    /// <summary>
    /// Finds every reachable router slot that directly owns one shared shelf offset.<br/>
    /// The route-graph walk reads only router pages and target magic values, uses absolute offsets to suppress shared-subtree revisits, and never materializes keys, identities, or shelf records.<br/>
    /// This common physical walk keeps `VS16`, `VV`, and `SV16` copy-growth publication under the same owner-set invariant without forcing their logical key or identity classifiers through one abstraction.<br/>
    /// </summary>
    /// <param name="shapeName">The diagnostic shelf-shape name used in corruption failures.<br/></param>
    /// <param name="rootRouterOffset">The stable root router from which every reachable owner is discovered.<br/></param>
    /// <param name="shelfOffset">The shared shelf whose direct parent routes are required.<br/></param>
    /// <returns>A map from each parent router offset to every physical route index targeting <paramref name="shelfOffset"/>.<br/></returns>
    private Dictionary<long, List<int>> CollectSharedShelfParentReferences(
        string shapeName,
        long rootRouterOffset,
        long shelfOffset)
    {
        const int maxReachableRouters = 1_000_000;
        Dictionary<long, List<int>> references = new();
        HashSet<long> visitedRouters = new();
        Stack<long> pendingRouters = new();
        Span<byte> targetMagicBytes = stackalloc byte[sizeof(uint)];
        pendingRouters.Push(rootRouterOffset);
        while (pendingRouters.Count != 0)
        {
            long routerOffset = pendingRouters.Pop();
            if (!visitedRouters.Add(routerOffset))
                continue;

            if (visitedRouters.Count > maxReachableRouters)
                throw new InvalidDataException($"The {shapeName} shared-shelf parent-reference scan exceeded {maxReachableRouters:N0} reachable routers.");

            byte[] routerBytes = new byte[RouterLayout.Size];
            kernel.Read(routerOffset, routerBytes);
            RouterReader router = new(routerBytes);
            if (!router.IsValid)
                throw new InvalidDataException($"The {shapeName} shared-shelf parent-reference scan found an invalid router at offset {routerOffset:N0}.");

            int routeCount = router.HasDirectIndex ? RouterLayout.MaxOneByteRouteCount : router.RouteCount;
            for (int routeIndex = 0; routeIndex < routeCount; routeIndex++)
            {
                long targetOffset = router.GetRouteTargetAt(routeIndex);
                if (targetOffset == 0)
                    continue;

                if (targetOffset == shelfOffset)
                {
                    if (!references.TryGetValue(routerOffset, out List<int>? routeIndexes))
                    {
                        routeIndexes = new List<int>();
                        references.Add(routerOffset, routeIndexes);
                    }

                    routeIndexes.Add(routeIndex);
                    continue;
                }

                kernel.Read(targetOffset, targetMagicBytes);
                if (BinaryPrimitives.ReadUInt32LittleEndian(targetMagicBytes) == RouterLayout.Magic)
                    pendingRouters.Push(targetOffset);
            }
        }

        return references;
    }

    /// <summary>
    /// Repoints one collected shared-shelf owner set to a single replacement target.<br/>
    /// Every collected slot is revalidated before its parent page is copied for in-place publication; a changed slot is treated as an invariant failure because the caller holds exclusive storage publication from collection through commit.<br/>
    /// Router cache entries are invalidated per rewritten owner so live walkers cannot retain any route to the obsolete shelf after the publication boundary.<br/>
    /// </summary>
    /// <param name="shapeName">The diagnostic shelf-shape name used in corruption failures.<br/></param>
    /// <param name="parentReferences">The complete reachable owner set collected under exclusive publication.<br/></param>
    /// <param name="obsoleteShelfOffset">The shelf offset every collected route must still target.<br/></param>
    /// <param name="replacementTargetOffset">The grown shelf offset that replaces the obsolete shelf for every owner.<br/></param>
    private void RewriteSharedShelfParentReferences(
        string shapeName,
        Dictionary<long, List<int>> parentReferences,
        long obsoleteShelfOffset,
        long replacementTargetOffset)
    {
        foreach ((long ownerRouterOffset, List<int> ownerRouteIndexes) in parentReferences)
        {
            byte[] ownerRouterBytes = new byte[RouterLayout.Size];
            kernel.Read(ownerRouterOffset, ownerRouterBytes);
            RouterReader ownerReader = new(ownerRouterBytes);
            if (!ownerReader.IsValid)
                throw new InvalidDataException($"The {shapeName} shared-shelf replacement found an invalid parent router at offset {ownerRouterOffset:N0}.");

            foreach (int ownerRouteIndex in ownerRouteIndexes)
            {
                if (ownerReader.GetRouteTargetAt(ownerRouteIndex) != obsoleteShelfOffset)
                {
                    throw new InvalidDataException(
                        $"The {shapeName} shared-shelf owner route changed during exclusive publication. " +
                        $"RouterOffset={ownerRouterOffset:N0}; RouteIndex={ownerRouteIndex:N0}.");
                }
            }

            RawDataReservation ownerRewrite = kernel.ReserveAt(ownerRouterOffset, RouterLayout.Size);
            ownerRouterBytes.CopyTo(ownerRewrite.Span);
            RouterWriter ownerWriter = new(ownerRewrite.Span);
            foreach (int ownerRouteIndex in ownerRouteIndexes)
                ownerWriter.WriteRouteTarget(ownerRouteIndex, replacementTargetOffset);

            InvalidateRouterReadCacheForRouterRewrite(ownerRouterOffset);
        }
    }
}
