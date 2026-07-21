namespace LibraDex;

/// <summary>
/// Provides a forward-only live reader over the typed logical sequence produced by a completed condition.<br/>
/// <see cref="Skip"/> and <see cref="Pull(int)"/> operate from the next unread result and may be mixed with individual <see cref="Next"/> calls.<br/>
/// </summary>
/// <typeparam name="TResult">The logical result type produced by the condition.<br/></typeparam>
public sealed class LibraDexResultReader<TResult> : IDisposable
{
    private readonly IEnumerator<TResult> enumerator;
    private TResult? current;
    private bool hasCurrent;
    private bool disposed;
    private bool exhausted;

    internal LibraDexResultReader(IEnumerable<TResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        enumerator = results.GetEnumerator();
        Ordinal = -1;
    }

    /// <summary>
    /// Gets the zero-based logical ordinal of the most recently consumed result.<br/>
    /// Skipped and pulled results advance this position even though they are not exposed through <see cref="Current"/>.<br/>
    /// </summary>
    public long Ordinal { get; private set; }

    /// <summary>
    /// Gets the result exposed by the most recent successful <see cref="Next"/> call.<br/>
    /// Skip and pull operations invalidate this scalar position so consumed rows cannot be observed twice.<br/>
    /// </summary>
    public TResult Current
    {
        get
        {
            ThrowIfDisposed();
            if (!hasCurrent)
                throw new InvalidOperationException("The reader is not positioned on a result.");

            return current!;
        }
    }

    /// <summary>
    /// Advances to and exposes the next unread result.<br/>
    /// </summary>
    /// <returns><see langword="true"/> when <see cref="Current"/> is available; otherwise <see langword="false"/> at end-of-stream.<br/></returns>
    public bool Next()
    {
        ThrowIfDisposed();
        if (!MoveNext(out TResult value))
        {
            InvalidateCurrent();
            return false;
        }

        current = value;
        hasCurrent = true;
        return true;
    }

    /// <summary>
    /// Advances to and exposes the next unread result.<br/>
    /// This alias preserves familiar .NET cursor spelling while <see cref="Next"/> remains the compact LibraDex form.<br/>
    /// </summary>
    /// <returns><see langword="true"/> when <see cref="Current"/> is available.<br/></returns>
    public bool MoveNext() => Next();

    /// <summary>
    /// Consumes up to <paramref name="count"/> upcoming unread results without materializing or returning them.<br/>
    /// The method may be called before iteration or between reads and returns the actual count consumed when end-of-stream is reached early.<br/>
    /// </summary>
    /// <param name="count">The maximum number of upcoming results to consume.<br/></param>
    /// <returns>The number of results actually skipped.<br/></returns>
    public int Skip(int count)
    {
        ThrowIfDisposed();
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), "Skip must be zero or greater.");

        if (count == 0)
            return 0;

        InvalidateCurrent();
        int skipped = 0;
        while (skipped < count && MoveNext(out _))
            skipped++;

        return skipped;
    }

    /// <summary>
    /// Eagerly collects up to <paramref name="count"/> upcoming unread results and advances beyond them.<br/>
    /// The existing <see cref="Current"/> value is never repeated; after the call, the next successful <see cref="Next"/> returns the result following the pulled range.<br/>
    /// </summary>
    /// <param name="count">The maximum number of upcoming results to return.<br/></param>
    /// <returns>A caller-owned array containing the results actually pulled.<br/></returns>
    public IReadOnlyList<TResult> Pull(int count)
    {
        ThrowIfDisposed();
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), "Pull count must be zero or greater.");

        if (count == 0)
            return Array.Empty<TResult>();

        TResult[] results = new TResult[count];
        int pulled = Pull(results.AsSpan());
        if (pulled == count)
            return results;

        Array.Resize(ref results, pulled);
        return results;
    }

    /// <summary>
    /// Eagerly copies upcoming unread results into caller-owned storage and advances beyond them.<br/>
    /// No result collection is allocated; the returned count identifies the populated prefix of <paramref name="destination"/>.<br/>
    /// </summary>
    /// <param name="destination">The destination receiving upcoming results.<br/></param>
    /// <returns>The number of destination elements populated before the destination filled or the stream ended.<br/></returns>
    public int Pull(Span<TResult> destination)
    {
        ThrowIfDisposed();
        if (destination.Length == 0)
            return 0;

        InvalidateCurrent();
        int pulled = 0;
        while (pulled < destination.Length && MoveNext(out TResult value))
        {
            destination[pulled] = value;
            pulled++;
        }

        return pulled;
    }

    /// <summary>
    /// Releases the underlying result enumerator.<br/>
    /// </summary>
    public void Dispose()
    {
        if (disposed)
            return;

        enumerator.Dispose();
        disposed = true;
        InvalidateCurrent();
    }

    private bool MoveNext(out TResult value)
    {
        if (!exhausted && enumerator.MoveNext())
        {
            Ordinal++;
            value = enumerator.Current;
            return true;
        }

        exhausted = true;
        value = default!;
        return false;
    }

    private void InvalidateCurrent()
    {
        hasCurrent = false;
        current = default;
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(LibraDexResultReader<TResult>));
    }
}
