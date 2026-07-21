using LibraDex.Layouts;
using LibraDex.Views;

namespace LibraDex;

internal sealed partial class LibraDexFileSession
{
    /// <summary>
    /// Walks one encoded `VS8` key through exact router selection while capturing proof-only structural counters.<br/>
    /// This method does not scan or decode the selected leaf; it exists to compare exact route selection with the generic range reader on the same persisted topology.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The root router offset for the `VS8` index.<br/></param>
    /// <param name="key">The exact encoded variable-length key, including its sentinel byte.<br/></param>
    /// <param name="maxRouterHops">The maximum number of router pages to follow.<br/></param>
    /// <returns>The selected leaf and exact-walk structural counters.<br/></returns>
    internal VarKeyScalar8ExactRouteDiagnostics DiagnoseVarKeyScalar8ExactRoute(
        long rootRouterOffset,
        ReadOnlySpan<byte> key,
        int maxRouterHops = DefaultVarKeyScalar8MaxRouterHops)
    {
        if (maxRouterHops <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRouterHops), maxRouterHops, "The VS8 diagnostic maximum router hop count must be positive.");
        }

        byte[] routerBytes = new byte[RouterLayout.Size];
        long routerOffset = rootRouterOffset;
        long routeSlotsExamined = 0;
        int oneByteRouters = 0;
        int multiByteRouters = 0;
        for (int hop = 0; hop < maxRouterHops; hop++)
        {
            ReadRouterPageUsingArenaCache(routerOffset, routerBytes);
            RouterReader router = new(routerBytes);
            if (!router.IsValid)
            {
                throw new InvalidDataException("The exact-route VS8 diagnostic encountered an invalid router.");
            }

            long targetOffset;
            int routeIndex;
            if (router.PrefixByteCount == 1)
            {
                oneByteRouters++;
                targetOffset = router.FindTarget(GetVarKeyScalar8Prefix(key, router.KeyDepth), out routeIndex);
                routeSlotsExamined += router.HasDirectIndex ? 1 : routeIndex >= 0 ? routeIndex + 1 : router.RouteCount;
            }
            else
            {
                multiByteRouters++;
                targetOffset = router.FindTarget(key, router.KeyDepth, out routeIndex);
                routeSlotsExamined += router.RouteCount;
            }

            if (targetOffset == 0)
            {
                return new VarKeyScalar8ExactRouteDiagnostics(
                    VarKeyScalar8RouteTargetKind.None,
                    0,
                    hop + 1,
                    oneByteRouters,
                    multiByteRouters,
                    routeSlotsExamined,
                    checked((long)(hop + 1) * RouterLayout.Size));
            }

            VarKeyScalar8RouteTargetKind kind = ClassifyVarKeyScalar8RouteTarget(targetOffset);
            if (kind == VarKeyScalar8RouteTargetKind.Shelf ||
                kind == VarKeyScalar8RouteTargetKind.TerminalIdentityRoot)
            {
                return new VarKeyScalar8ExactRouteDiagnostics(
                    kind,
                    targetOffset,
                    hop + 1,
                    oneByteRouters,
                    multiByteRouters,
                    routeSlotsExamined,
                    checked((long)(hop + 1) * RouterLayout.Size));
            }

            if (kind != VarKeyScalar8RouteTargetKind.Router)
            {
                throw new InvalidDataException("The exact-route VS8 diagnostic encountered an unsupported route target.");
            }

            routerOffset = targetOffset;
        }

        throw new InvalidDataException("The exact-route VS8 diagnostic exceeded the configured router hop count.");
    }
}

/// <summary>
/// Captures the leaf selected by an exact `VS8` route walk and the structural work required to reach it.<br/>
/// The route-slot counter is exact for one-byte routers and conservative for compressed multi-byte routers because their persisted matcher may stop before the final route slot.<br/>
/// </summary>
/// <param name="TargetKind">The selected leaf kind, or none when no route target is set.<br/></param>
/// <param name="TargetOffset">The selected leaf offset, or zero when no target is set.<br/></param>
/// <param name="RoutersVisited">The number of router pages visited.<br/></param>
/// <param name="OneByteRoutersVisited">The number of one-byte routers visited.<br/></param>
/// <param name="MultiByteRoutersVisited">The number of compressed multi-byte routers visited.<br/></param>
/// <param name="RouteSlotsExamined">The exact or conservative route-slot work performed by the point walk.<br/></param>
/// <param name="RouterBytesTouched">The logical router-page bytes touched.<br/></param>
internal readonly record struct VarKeyScalar8ExactRouteDiagnostics(
    VarKeyScalar8RouteTargetKind TargetKind,
    long TargetOffset,
    int RoutersVisited,
    int OneByteRoutersVisited,
    int MultiByteRoutersVisited,
    long RouteSlotsExamined,
    long RouterBytesTouched);
