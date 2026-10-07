using System.Buffers;

namespace LibraDex;

/// <summary>
/// Tracks visited routed file offsets with compact pooled storage.<br/>
/// Range walks use this set only to prevent duplicate shelf/router visits, so a linear scan over a small rented array avoids per-call hash table allocation and growth costs.<br/>
/// The newest entries are scanned first because adjacent recursive route walks commonly revisit recent offsets before older ones.<br/>
/// </summary>
internal sealed class RouteVisitedOffsetSet : IDisposable
{
    private const int DefaultCapacity = 32;

    private long[]? offsets;
    private int count;

    private RouteVisitedOffsetSet(long[] offsets)
    {
        this.offsets = offsets;
        count = 0;
    }

    /// <summary>
    /// Gets the number of unique offsets currently tracked.<br/>
    /// This value is intended for diagnostics and capacity tuning, not route correctness decisions.<br/>
    /// </summary>
    public int Count => count;

    /// <summary>
    /// Rents a compact visited-offset set.<br/>
    /// The returned set is owned by the caller and must be disposed when the range walk or scratch owner is complete.<br/>
    /// </summary>
    /// <param name="minimumCapacity">The minimum expected number of visited offsets before pooled growth is required.</param>
    /// <returns>A visited-offset set backed by a rented array.</returns>
    public static RouteVisitedOffsetSet Rent(int minimumCapacity = DefaultCapacity)
    {
        return new RouteVisitedOffsetSet(ArrayPool<long>.Shared.Rent(Math.Max(minimumCapacity, DefaultCapacity)));
    }

    /// <summary>
    /// Adds an offset if it has not already been visited.<br/>
    /// Existing entries are detected by newest-first linear scan; new entries append and grow through pooled replacement only when required.<br/>
    /// </summary>
    /// <param name="offset">The routed shelf or router file offset.</param>
    /// <returns><see langword="true"/> when the offset was newly added; otherwise <see langword="false"/>.</returns>
    public bool Add(long offset)
    {
        long[] localOffsets = offsets ?? throw new ObjectDisposedException(nameof(RouteVisitedOffsetSet));
        for (int i = count - 1; i >= 0; i--)
        {
            if (localOffsets[i] == offset)
            {
                return false;
            }
        }

        if (count == localOffsets.Length)
        {
            Grow();
            localOffsets = offsets!;
        }

        localOffsets[count] = offset;
        count++;
        return true;
    }

    /// <summary>
    /// Clears the logical visited set while retaining the rented buffer for the current owner.<br/>
    /// Values are not zeroed because routed file offsets are not secret and clearing would add read-path cost.<br/>
    /// </summary>
    public void Clear()
    {
        count = 0;
    }

    /// <summary>
    /// Returns the rented storage to the shared array pool.<br/>
    /// The array is returned uncleared because it contains only routed file offsets.<br/>
    /// </summary>
    public void Dispose()
    {
        long[]? localOffsets = offsets;
        offsets = null;
        count = 0;
        if (localOffsets is not null)
        {
            ArrayPool<long>.Shared.Return(localOffsets, clearArray: false);
        }
    }

    private void Grow()
    {
        long[] oldOffsets = offsets!;
        int newCapacity = checked(oldOffsets.Length * 2);
        long[] newOffsets = ArrayPool<long>.Shared.Rent(newCapacity);
        oldOffsets.AsSpan(0, count).CopyTo(newOffsets);
        offsets = newOffsets;
        ArrayPool<long>.Shared.Return(oldOffsets, clearArray: false);
    }
}

/// <summary>
/// Tracks visited routed router offsets with their active lower/upper range-bound context.<br/>
/// A shared router page can be reached through different route prefixes, and each prefix can carry different edge-pruning state; treating the router offset alone as visited can skip valid subranges.<br/>
/// </summary>
internal sealed class RouteVisitedContextSet : IDisposable
{
    private const int DefaultCapacity = 32;

    [ThreadStatic]
    private static int diagnosticContextRevisits;

    [ThreadStatic]
    private static int diagnosticExactSkips;

    private long[]? contexts;
    private int count;

    private RouteVisitedContextSet(long[] contexts)
    {
        this.contexts = contexts;
        count = 0;
    }

    /// <summary>
    /// Gets the number of unique router context entries currently tracked.<br/>
    /// This is diagnostic-only and does not imply unique router page count because one router can have multiple edge contexts.<br/>
    /// </summary>
    public int Count => count;

    /// <summary>
    /// Clears the thread-local route-context diagnostic counters used by stress certification.<br/>
    /// The counters are intentionally thread-static so range reads avoid global synchronization on hot traversal paths.<br/>
    /// </summary>
    internal static void ResetDiagnostics()
    {
        diagnosticContextRevisits = 0;
        diagnosticExactSkips = 0;
    }

    /// <summary>
    /// Creates a compact route-context diagnostic summary for log output.<br/>
    /// `ContextRevisits` is the count of router offsets that were intentionally revisited with different edge flags; `ExactSkips` is the count of duplicate offset/context visits suppressed.<br/>
    /// </summary>
    /// <returns>A stable single-line diagnostic string.</returns>
    internal static string CreateDiagnosticsSummary()
        => $"ContextRevisits={diagnosticContextRevisits:n0}; ExactSkips={diagnosticExactSkips:n0}";

    /// <summary>
    /// Rents a compact visited-router context set.<br/>
    /// The returned set is owned by the caller and must be disposed when the range walk or scratch owner is complete.<br/>
    /// </summary>
    /// <param name="minimumCapacity">The minimum expected number of visited router contexts before pooled growth is required.</param>
    /// <returns>A visited-router context set backed by a rented array.</returns>
    public static RouteVisitedContextSet Rent(int minimumCapacity = DefaultCapacity)
    {
        return new RouteVisitedContextSet(ArrayPool<long>.Shared.Rent(Math.Max(minimumCapacity, DefaultCapacity)));
    }

    /// <summary>
    /// Adds a router offset and lower/upper range-bound context if that exact context has not already been visited.<br/>
    /// The same router offset may be visited more than once when the edge flags differ, because those flags control descendant prefix pruning.<br/>
    /// </summary>
    /// <param name="offset">The routed router file offset.</param>
    /// <param name="lowerEdge">Whether this traversal is constrained by the inclusive lower range boundary.</param>
    /// <param name="upperEdge">Whether this traversal is constrained by the inclusive upper range boundary.</param>
    /// <returns><see langword="true"/> when the context was newly added; otherwise <see langword="false"/>.</returns>
    public bool Add(long offset, bool lowerEdge, bool upperEdge)
    {
        long[] localContexts = contexts ?? throw new ObjectDisposedException(nameof(RouteVisitedContextSet));
        long context = CreateContext(offset, lowerEdge, upperEdge);
        bool sawSameOffset = false;
        for (int i = count - 1; i >= 0; i--)
        {
            long existing = localContexts[i];
            if (existing == context)
            {
                diagnosticExactSkips++;
                return false;
            }

            sawSameOffset |= (existing >> 2) == offset;
        }

        if (sawSameOffset)
        {
            diagnosticContextRevisits++;
        }

        if (count == localContexts.Length)
        {
            Grow();
            localContexts = contexts!;
        }

        localContexts[count] = context;
        count++;
        return true;
    }

    /// <summary>
    /// Clears the logical visited set while retaining the rented buffer for the current owner.<br/>
    /// Values are not zeroed because routed file offsets and edge flags are not secret and clearing would add read-path cost.<br/>
    /// </summary>
    public void Clear()
    {
        count = 0;
    }

    /// <summary>
    /// Returns the rented storage to the shared array pool.<br/>
    /// The array is returned uncleared because it contains only routed file offsets and compact edge flags.<br/>
    /// </summary>
    public void Dispose()
    {
        long[]? localContexts = contexts;
        contexts = null;
        count = 0;
        if (localContexts is not null)
        {
            ArrayPool<long>.Shared.Return(localContexts, clearArray: false);
        }
    }

    private static long CreateContext(long offset, bool lowerEdge, bool upperEdge)
        => checked((offset << 2) | (lowerEdge ? 1L : 0L) | (upperEdge ? 2L : 0L));

    private void Grow()
    {
        long[] oldContexts = contexts!;
        int newCapacity = checked(oldContexts.Length * 2);
        long[] newContexts = ArrayPool<long>.Shared.Rent(newCapacity);
        oldContexts.AsSpan(0, count).CopyTo(newContexts);
        contexts = newContexts;
        ArrayPool<long>.Shared.Return(oldContexts, clearArray: false);
    }
}
