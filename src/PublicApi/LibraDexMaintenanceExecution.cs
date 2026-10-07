using System.Buffers.Binary;
using LibraDex.Layouts;
using LibraDex.Views;

namespace LibraDex;

internal enum LibraDexMaintenanceTopologyKind
{
    Empty = 0,
    VarKeyScalar8 = 1,
    VarKeyScalar16 = 2,
    VarKeyVarIdentity = 3,
    Scalar8VarIdentity = 4,
    Scalar16VarIdentity = 5,
    NoVariablePayload = 6,
    Invalid = 7
}

internal readonly record struct LibraDexMaintenanceWalkResult(
    int ConsideredCount,
    int ChangedCount,
    LibraDexMaintenanceIncompleteReason IncompleteReasons)
{
    public bool Completed => IncompleteReasons == LibraDexMaintenanceIncompleteReason.None;
}

internal static class LibraDexMaintenancePolicy
{
    internal const int LightWorkItems = 16;
    internal const int BoundedWorkItems = 256;

    /// <summary>
    /// Resolves one explicit or mode-owned maintenance work limit.<br/>
    /// Explicit positive limits override mode defaults; full mode is unbounded only when the caller supplies no limit.<br/>
    /// </summary>
    /// <param name="options">The effective maintenance options to validate.<br/></param>
    /// <returns>The cumulative work limit, or null for unbounded full maintenance.<br/></returns>
    internal static int? ResolveWorkLimit(LibraDexMaintenanceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(options.Mode))
            throw new ArgumentOutOfRangeException(nameof(options), options.Mode, "The maintenance mode is not defined.");
        if (options.MaxWorkItems is <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), options.MaxWorkItems, "The maintenance work limit must be positive when supplied.");
        if (options.MaxWorkItems is int explicitLimit)
            return explicitLimit;

        return options.Mode switch
        {
            LibraDexMaintenanceMode.Light => LightWorkItems,
            LibraDexMaintenanceMode.Bounded => BoundedWorkItems,
            LibraDexMaintenanceMode.Full => null,
            _ => throw new ArgumentOutOfRangeException(nameof(options), options.Mode, "The maintenance mode is not defined.")
        };
    }
}

internal sealed partial class LibraDexFileSession
{
    /// <summary>
    /// Classifies one active index by following a single reachable router path to its first physical leaf.<br/>
    /// Empty routers are complete no-op topologies; cycles, malformed routers, and unknown leaf magic are invalid rather than silently skipped.<br/>
    /// </summary>
    /// <param name="rootOffset">The active index root offset.<br/></param>
    /// <returns>The physical maintenance topology kind.<br/></returns>
    internal LibraDexMaintenanceTopologyKind ClassifyMaintenanceTopology(long rootOffset)
    {
        if (rootOffset <= 0)
            return LibraDexMaintenanceTopologyKind.Empty;

        HashSet<long> visited = new();
        long offset = rootOffset;
        byte[] header = new byte[sizeof(uint)];
        byte[] routerBytes = new byte[RouterLayout.Size];
        while (offset > 0)
        {
            if (!visited.Add(offset))
                return LibraDexMaintenanceTopologyKind.Invalid;

            kernel.Read(offset, header);
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
            if (magic != RouterLayout.Magic)
                return ClassifyLeaf(magic);

            ReadRouterPageUsingArenaCache(offset, routerBytes);
            RouterReader router = new(routerBytes);
            if (!router.IsValid)
                return LibraDexMaintenanceTopologyKind.Invalid;

            offset = 0;
            for (int i = 0; i < router.RouteCount; i++)
            {
                long target = router.GetRouteTargetAt(i);
                if (target > 0)
                {
                    offset = target;
                    break;
                }
            }

            if (offset == 0)
                return LibraDexMaintenanceTopologyKind.Empty;
        }

        return LibraDexMaintenanceTopologyKind.Empty;

        static LibraDexMaintenanceTopologyKind ClassifyLeaf(uint magic)
        {
            if (magic == VarKeyScalar8Layout.Magic)
                return LibraDexMaintenanceTopologyKind.VarKeyScalar8;
            if (magic == VarKeyScalar16Layout.Magic)
                return LibraDexMaintenanceTopologyKind.VarKeyScalar16;
            if (magic == VarKeyVarIdentityLayout.Magic)
                return LibraDexMaintenanceTopologyKind.VarKeyVarIdentity;
            if (magic == Scalar8VarIdentityLayout.Magic)
                return LibraDexMaintenanceTopologyKind.Scalar8VarIdentity;
            if (magic == Scalar16VarIdentityLayout.Magic)
                return LibraDexMaintenanceTopologyKind.Scalar16VarIdentity;
            return IsKnownNonReclaimableMaintenanceTarget(magic)
                ? LibraDexMaintenanceTopologyKind.NoVariablePayload
                : LibraDexMaintenanceTopologyKind.Invalid;
        }
    }
}
