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
