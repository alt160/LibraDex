using System.Globalization;
using System.Text;

namespace LibraDex;

/// <summary>
/// Represents one queued variable-length route-optimizer candidate.<br/>
/// The candidate is an evidence record, not a durable publish token: consumers should re-read the current route target before publishing a replacement subtree.<br/>
/// </summary>
/// <param name="FirstObservedAt">The 1-based insert ordinal where the prefix first crossed the observation thresholds.</param>
/// <param name="LastObservedAt">The 1-based insert ordinal that most recently updated the candidate.</param>
/// <param name="DepthObserved">The maximum routed depth observed for this prefix.</param>
/// <param name="LandingShelfItemCount">The maximum landing shelf item count observed for this prefix.</param>
/// <param name="HitCount">The number of qualifying observations recorded for the prefix.</param>
/// <param name="RootRouterOffset">The root router offset that owns the candidate route.</param>
/// <param name="ParentRouterOffset">The parent router offset reported by the insert path for the candidate route.</param>
/// <param name="CurrentTargetOffset">The target offset reported by the insert path when the candidate was queued.</param>
/// <param name="PrefixHash">The stable hash of the observed consumed-prefix bytes.</param>
/// <param name="PrefixKey">The uppercase hex representation of the observed consumed-prefix bytes.</param>
internal readonly record struct VarLenRouteOptimizerCandidate(
    int FirstObservedAt,
    int LastObservedAt,
    int DepthObserved,
    int LandingShelfItemCount,
    int HitCount,
    long RootRouterOffset,
    long ParentRouterOffset,
    long CurrentTargetOffset,
    ulong PrefixHash,
    string PrefixKey);

/// <summary>
/// Accumulates cheap variable-length route pressure facts outside the route insert mechanics.<br/>
/// The queue consumes values already produced by walked inserts: route depth, landing shelf size, route offsets, and the key bytes already available at the call site.<br/>
/// It suppresses duplicate candidates for the same consumed prefix so later maintenance can drain bounded work without repeatedly scheduling the same rewrite.<br/>
/// </summary>
internal sealed class VarLenRouteOptimizerQueueTelemetry
{
    private readonly int depthThreshold;
    private readonly int shelfItemThreshold;
    private readonly int hitThreshold;
    private readonly Dictionary<string, VarLenRouteOptimizerPrefixState> stateByPrefix = [];
    private readonly HashSet<string> enqueuedPrefixes = [];
    private readonly List<VarLenRouteOptimizerCandidate> candidates = [];

    /// <summary>
    /// Initializes a route optimizer queue with explicit trigger thresholds.<br/>
    /// Thresholds are kept outside hard-coded insert logic so benchmark and production policy can tune them without changing the hot routing algorithm.<br/>
    /// </summary>
    /// <param name="depthThreshold">The minimum routed depth required before an observation can count toward a candidate.</param>
    /// <param name="shelfItemThreshold">The minimum landing shelf item count required before an observation can count toward a candidate.</param>
    /// <param name="hitThreshold">The number of qualifying observations needed before the prefix is enqueued.</param>
    public VarLenRouteOptimizerQueueTelemetry(int depthThreshold, int shelfItemThreshold, int hitThreshold)
    {
        this.depthThreshold = depthThreshold;
        this.shelfItemThreshold = shelfItemThreshold;
        this.hitThreshold = hitThreshold;
    }

    public int ObservationCount { get; private set; }

    public int DistinctPrefixCount => stateByPrefix.Count;

    public int EnqueuedCount { get; private set; }

    public int CandidateCount => candidates.Count;

    public int DuplicateSuppressedCount { get; private set; }

    public int FirstObservationAt { get; private set; }

    public int FirstEnqueueAt { get; private set; }

    public int MaxHits { get; private set; }

    public VarLenRouteOptimizerCandidate FirstCandidate { get; private set; }

    /// <summary>
    /// Gets an enqueued optimizer candidate by zero-based queue position.<br/>
    /// The queue keeps candidates in first-enqueue order so bounded maintenance can drain work incrementally across later idle or on-demand boundaries.<br/>
    /// </summary>
    /// <param name="index">The zero-based candidate index.</param>
    /// <param name="candidate">The queued optimizer candidate when present.</param>
    /// <returns><see langword="true"/> when the requested candidate exists.</returns>
    public bool TryGetCandidate(int index, out VarLenRouteOptimizerCandidate candidate)
    {
        if ((uint)index >= (uint)candidates.Count)
        {
            candidate = default;
            return false;
        }

        candidate = candidates[index];
        return true;
    }

    /// <summary>
    /// Records one walked-insert route observation and returns whether it queued a new optimizer candidate.<br/>
    /// The method performs only bounded prefix hashing and dictionary updates; it does not inspect shelf bytes or build replacement routes.<br/>
    /// A returned candidate should later be refreshed against current route state before any version-checked publish is attempted.<br/>
    /// </summary>
    /// <param name="insertOrdinal">The 1-based insert ordinal for telemetry and later build-slice selection.</param>
    /// <param name="key">The encoded key bytes for the inserted item.</param>
    /// <param name="targetRouterDepth">The route depth reported by the walked insert path.</param>
    /// <param name="landingShelfItemCount">The landing shelf item count reported by the walked insert path.</param>
    /// <param name="rootRouterOffset">The root router offset that owns the candidate route.</param>
    /// <param name="parentRouterOffset">The parent router offset reported by the walked insert path.</param>
    /// <param name="currentTargetOffset">The target offset reported by the walked insert path.</param>
    /// <returns><see langword="true"/> when this observation enqueued a previously unseen prefix candidate.</returns>
    public bool Add(
        int insertOrdinal,
        ReadOnlySpan<byte> key,
        int targetRouterDepth,
        int landingShelfItemCount,
        long rootRouterOffset,
        long parentRouterOffset,
        long currentTargetOffset)
    {
        if (targetRouterDepth < depthThreshold ||
            landingShelfItemCount < shelfItemThreshold)
        {
            return false;
        }

        ObservationCount++;
        if (FirstObservationAt == 0)
        {
            FirstObservationAt = insertOrdinal;
        }

        int prefixLength = Math.Min(Math.Max(targetRouterDepth, 0), key.Length);
        string prefixKey = CreatePrefixKey(key, prefixLength);
        ulong prefixHash = HashPrefix(key, prefixLength);
        VarLenRouteOptimizerPrefixState state = stateByPrefix.TryGetValue(prefixKey, out VarLenRouteOptimizerPrefixState existing)
            ? existing
            : new VarLenRouteOptimizerPrefixState(insertOrdinal, prefixHash);
        state.HitCount++;
        state.LastObservedAt = insertOrdinal;
        state.DepthObserved = Math.Max(state.DepthObserved, targetRouterDepth);
        state.LandingShelfItemCount = Math.Max(state.LandingShelfItemCount, landingShelfItemCount);
        state.RootRouterOffset = rootRouterOffset;
        state.ParentRouterOffset = parentRouterOffset;
        state.CurrentTargetOffset = currentTargetOffset;
        stateByPrefix[prefixKey] = state;
        MaxHits = Math.Max(MaxHits, state.HitCount);
        if (state.HitCount < hitThreshold)
        {
            return false;
        }

        if (!enqueuedPrefixes.Add(prefixKey))
        {
            DuplicateSuppressedCount++;
            return false;
        }

        EnqueuedCount++;
        VarLenRouteOptimizerCandidate candidate = new(
            state.FirstObservedAt,
            state.LastObservedAt,
            state.DepthObserved,
            state.LandingShelfItemCount,
            state.HitCount,
            state.RootRouterOffset,
            state.ParentRouterOffset,
            state.CurrentTargetOffset,
            prefixHash,
            prefixKey);
        candidates.Add(candidate);
        if (FirstEnqueueAt == 0)
        {
            FirstEnqueueAt = insertOrdinal;
            FirstCandidate = candidate;
        }

        return true;
    }

    private static string CreatePrefixKey(ReadOnlySpan<byte> key, int length)
    {
        StringBuilder builder = new(length * 2);
        for (int i = 0; i < length; i++)
        {
            builder.Append(key[i].ToString("X2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static ulong HashPrefix(ReadOnlySpan<byte> key, int length)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offsetBasis;
        for (int i = 0; i < length; i++)
        {
            hash ^= key[i];
            hash *= prime;
        }

        return hash;
    }
}

/// <summary>
/// Owns optimizer candidate state for one variable-length route root.<br/>
/// This is the internal lifecycle object that session/index code can carry between writes; it keeps threshold policy and root ownership together instead of forcing callers to pass those values for every queue observation.<br/>
/// The current implementation still exposes queue-style telemetry so harness comparisons can verify behavior while production ownership is being wired.<br/>
/// </summary>
internal sealed class VarLenRouteOptimizerState
{
    private readonly VarLenRouteOptimizerQueueTelemetry queue;

    /// <summary>
    /// Initializes optimizer state for one root router and one threshold policy.<br/>
    /// The state is intentionally small and low-count: one instance should be enough per active varlen index/root that wants optimizer observation enabled.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The root router offset this optimizer state observes.</param>
    /// <param name="depthThreshold">The minimum routed depth required before an observation can count toward a candidate.</param>
    /// <param name="shelfItemThreshold">The minimum landing shelf item count required before an observation can count toward a candidate.</param>
    /// <param name="hitThreshold">The number of qualifying observations needed before the prefix is enqueued.</param>
    public VarLenRouteOptimizerState(long rootRouterOffset, int depthThreshold, int shelfItemThreshold, int hitThreshold)
    {
        RootRouterOffset = rootRouterOffset;
        queue = new VarLenRouteOptimizerQueueTelemetry(depthThreshold, shelfItemThreshold, hitThreshold);
    }

    public long RootRouterOffset { get; }

    public int ObservationCount => queue.ObservationCount;

    public int DistinctPrefixCount => queue.DistinctPrefixCount;

    public int EnqueuedCount => queue.EnqueuedCount;

    public int CandidateCount => queue.CandidateCount;

    public int DuplicateSuppressedCount => queue.DuplicateSuppressedCount;

    public int FirstObservationAt => queue.FirstObservationAt;

    public int FirstEnqueueAt => queue.FirstEnqueueAt;

    public int MaxHits => queue.MaxHits;

    public VarLenRouteOptimizerCandidate FirstCandidate => queue.FirstCandidate;

    /// <summary>
    /// Gets an enqueued optimizer candidate by zero-based queue position.<br/>
    /// This exposes bounded maintenance work beyond the first candidate without forcing callers to know the queue's internal prefix dictionary shape.<br/>
    /// </summary>
    /// <param name="index">The zero-based candidate index.</param>
    /// <param name="candidate">The queued optimizer candidate when present.</param>
    /// <returns><see langword="true"/> when the requested candidate exists.</returns>
    public bool TryGetCandidate(int index, out VarLenRouteOptimizerCandidate candidate)
    {
        return queue.TryGetCandidate(index, out candidate);
    }

    /// <summary>
    /// Records one walked-insert observation for this state's root router.<br/>
    /// The method keeps root ownership outside the hot observation call and forwards only the route facts already produced by the insert path.<br/>
    /// A <see langword="true"/> return means this observation produced a previously unseen queued candidate.<br/>
    /// </summary>
    /// <param name="insertOrdinal">The 1-based insert ordinal for telemetry and later build-slice selection.</param>
    /// <param name="key">The encoded key bytes for the inserted item.</param>
    /// <param name="targetRouterDepth">The route depth reported by the walked insert path.</param>
    /// <param name="landingShelfItemCount">The landing shelf item count reported by the walked insert path.</param>
    /// <param name="parentRouterOffset">The parent router offset reported by the walked insert path.</param>
    /// <param name="currentTargetOffset">The target offset reported by the walked insert path.</param>
    /// <returns><see langword="true"/> when this observation enqueued a previously unseen prefix candidate.</returns>
    public bool Observe(
        int insertOrdinal,
        ReadOnlySpan<byte> key,
        int targetRouterDepth,
        int landingShelfItemCount,
        long parentRouterOffset,
        long currentTargetOffset)
    {
        return queue.Add(
            insertOrdinal,
            key,
            targetRouterDepth,
            landingShelfItemCount,
            RootRouterOffset,
            parentRouterOffset,
            currentTargetOffset);
    }
}

internal struct VarLenRouteOptimizerPrefixState
{
    public VarLenRouteOptimizerPrefixState(int firstObservedAt, ulong prefixHash)
    {
        FirstObservedAt = firstObservedAt;
        PrefixHash = prefixHash;
    }

    public int FirstObservedAt;
    public int LastObservedAt;
    public int DepthObserved;
    public int LandingShelfItemCount;
    public int HitCount;
    public long RootRouterOffset;
    public long ParentRouterOffset;
    public long CurrentTargetOffset;
    public ulong PrefixHash;
}
