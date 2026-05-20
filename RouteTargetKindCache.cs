using System.Buffers;

namespace LibraDex;

/// <summary>
/// Stores routed target-kind classifications in a compact caller-owned cache.<br/>
/// The cache is intentionally array-backed instead of dictionary-backed because range walks usually touch a small number of router and shelf targets, making linear lookup cheaper than hash setup and growth costs.<br/>
/// Stored kind values are shape-local enum integer values; callers cast at the edge to avoid generic enum boxing or delegate dispatch in hot read paths.<br/>
/// </summary>
internal sealed class RouteTargetKindCache : IDisposable
{
    private const int DefaultCapacity = 32;

    private long[]? offsets;
    private int[]? kinds;
    private int count;

    private RouteTargetKindCache(long[] offsets, int[] kinds)
    {
        this.offsets = offsets;
        this.kinds = kinds;
        count = 0;
    }

    /// <summary>
    /// Gets the number of cached offset classifications.<br/>
    /// This is diagnostic/accounting information and should not be used as a correctness signal.<br/>
    /// </summary>
    public int Count => count;

    /// <summary>
    /// Rents a compact route-target classification cache.<br/>
    /// The returned cache must be disposed when the read operation or reusable scratch owner is done with it.<br/>
    /// </summary>
    /// <param name="minimumCapacity">The minimum number of classifications expected before growth.</param>
    /// <returns>A cache backed by pooled arrays.</returns>
    public static RouteTargetKindCache Rent(int minimumCapacity = DefaultCapacity)
    {
        int capacity = Math.Max(minimumCapacity, DefaultCapacity);
        return new RouteTargetKindCache(
            ArrayPool<long>.Shared.Rent(capacity),
            ArrayPool<int>.Shared.Rent(capacity));
    }

    /// <summary>
    /// Attempts to resolve a cached target-kind integer for a file offset.<br/>
    /// Lookup scans from newest to oldest because recent routers and shelves are most likely to be revisited during adjacent range walks.<br/>
    /// </summary>
    /// <param name="offset">The routed target file offset.</param>
    /// <param name="kind">The cached shape-local target-kind integer when found.</param>
    /// <returns><see langword="true"/> when the offset is cached.</returns>
    public bool TryGet(long offset, out int kind)
    {
        long[]? localOffsets = offsets;
        int[]? localKinds = kinds;
        if (localOffsets is not null && localKinds is not null)
        {
            for (int i = count - 1; i >= 0; i--)
            {
                if (localOffsets[i] == offset)
                {
                    kind = localKinds[i];
                    return true;
                }
            }
        }

        kind = 0;
        return false;
    }

    /// <summary>
    /// Stores or updates a target-kind classification for a file offset.<br/>
    /// Existing offsets are updated in place; new offsets append to the compact arrays and grow through pooled replacement only when required.<br/>
    /// </summary>
    /// <param name="offset">The routed target file offset.</param>
    /// <param name="kind">The shape-local target-kind integer.</param>
    public void Set(long offset, int kind)
    {
        long[] localOffsets = offsets ?? throw new ObjectDisposedException(nameof(RouteTargetKindCache));
        int[] localKinds = kinds ?? throw new ObjectDisposedException(nameof(RouteTargetKindCache));
        for (int i = count - 1; i >= 0; i--)
        {
            if (localOffsets[i] == offset)
            {
                localKinds[i] = kind;
                return;
            }
        }

        if (count == localOffsets.Length)
        {
            Grow();
            localOffsets = offsets!;
            localKinds = kinds!;
        }

        localOffsets[count] = offset;
        localKinds[count] = kind;
        count++;
    }

    /// <summary>
    /// Clears the logical cache contents while keeping rented buffers available to the current owner.<br/>
    /// Values are not zeroed because offsets and target kinds are not secret and clearing would add hot-path cost.<br/>
    /// </summary>
    public void Clear()
    {
        count = 0;
    }

    /// <summary>
    /// Returns rented cache buffers to their pools.<br/>
    /// The buffers are returned uncleared because they contain only file offsets and shape-local enum integers.<br/>
    /// </summary>
    public void Dispose()
    {
        long[]? localOffsets = offsets;
        int[]? localKinds = kinds;
        offsets = null;
        kinds = null;
        count = 0;
        if (localOffsets is not null)
        {
            ArrayPool<long>.Shared.Return(localOffsets, clearArray: false);
        }

        if (localKinds is not null)
        {
            ArrayPool<int>.Shared.Return(localKinds, clearArray: false);
        }
    }

    private void Grow()
    {
        long[] oldOffsets = offsets!;
        int[] oldKinds = kinds!;
        int newCapacity = checked(oldOffsets.Length * 2);
        long[] newOffsets = ArrayPool<long>.Shared.Rent(newCapacity);
        int[] newKinds = ArrayPool<int>.Shared.Rent(newCapacity);
        oldOffsets.AsSpan(0, count).CopyTo(newOffsets);
        oldKinds.AsSpan(0, count).CopyTo(newKinds);
        offsets = newOffsets;
        kinds = newKinds;
        ArrayPool<long>.Shared.Return(oldOffsets, clearArray: false);
        ArrayPool<int>.Shared.Return(oldKinds, clearArray: false);
    }
}
