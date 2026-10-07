using System.Diagnostics;

namespace LibraDex;

/// <summary>
/// Coordinates bounded synchronous and asynchronous write admission for one file session.<br/>
/// The empty-queue path uses atomic counters; contended callers enter one strict FIFO queue so a low-limit writer cannot starve behind continuously replenished higher-limit batch work.<br/>
/// </summary>
internal sealed class LibraDexWriteAdmission
{
    private readonly object sync = new();
    private readonly LinkedList<Request> pending = [];
    private int activeCount;
    private int currentMaximumActiveWriters;
    private int pendingCount;
    private int maximumPendingCount;
    private long totalQueuedCount;
    private long totalGrantedCount;
    private long canceledCount;
    private long timedOutCount;
    private long rejectedCount;
    private long totalWaitTicks;
    private long maximumWaitTicks;

    /// <summary>
    /// Enters the session write-admission domain, blocking only when the active limit is saturated.<br/>
    /// </summary>
    /// <param name="maximumActiveWriters">The maximum active count requested by this action.<br/></param>
    /// <param name="maximumQueuedWriters">The maximum number of pending actions retained by the session.<br/></param>
    /// <param name="queueTimeout">The maximum queue wait, or <see cref="Timeout.InfiniteTimeSpan"/> for no timeout.<br/></param>
    /// <param name="cancellationToken">A token that may remove this action while it waits.<br/></param>
    /// <returns>An active admission lease.<br/></returns>
    internal LibraDexWriteAdmissionLease Enter(
        int maximumActiveWriters,
        int maximumQueuedWriters,
        TimeSpan queueTimeout,
        CancellationToken cancellationToken)
    {
        Validate(maximumActiveWriters, maximumQueuedWriters, queueTimeout);
        cancellationToken.ThrowIfCancellationRequested();
        if (TryEnterFast(maximumActiveWriters))
        {
            Interlocked.Increment(ref totalGrantedCount);
            return new LibraDexWriteAdmissionLease(this);
        }

        Request request = Enqueue(maximumActiveWriters, maximumQueuedWriters, isAsync: false);
        using CancellationTokenRegistration registration = cancellationToken.Register(
            static state =>
            {
                (LibraDexWriteAdmission owner, Request request) tuple =
                    ((LibraDexWriteAdmission owner, Request request))state!;
                tuple.owner.Cancel(tuple.request);
            },
            (this, request));

        try
        {
            if (!request.Ready!.Wait(queueTimeout, cancellationToken))
            {
                if (!Cancel(request))
                {
                    Release();
                }

                Interlocked.Increment(ref timedOutCount);
                throw new TimeoutException($"LibraDex write admission exceeded the configured queue timeout of {queueTimeout}.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new LibraDexWriteAdmissionLease(this);
        }
        catch (OperationCanceledException)
        {
            if (!Cancel(request))
            {
                Release();
            }

            Interlocked.Increment(ref canceledCount);
            throw;
        }
        finally
        {
            request.Ready!.Dispose();
        }
    }

    /// <summary>
    /// Enters the same bounded FIFO admission domain without blocking a caller thread.<br/>
    /// </summary>
    /// <param name="maximumActiveWriters">The maximum active count requested by this action.<br/></param>
    /// <param name="maximumQueuedWriters">The maximum number of pending actions retained by the session.<br/></param>
    /// <param name="queueTimeout">The maximum queue wait, or <see cref="Timeout.InfiniteTimeSpan"/> for no timeout.<br/></param>
    /// <param name="cancellationToken">A token that may remove this action while it waits.<br/></param>
    /// <returns>An active admission lease.<br/></returns>
    internal async ValueTask<LibraDexWriteAdmissionLease> EnterAsync(
        int maximumActiveWriters,
        int maximumQueuedWriters,
        TimeSpan queueTimeout,
        CancellationToken cancellationToken)
    {
        Validate(maximumActiveWriters, maximumQueuedWriters, queueTimeout);
        cancellationToken.ThrowIfCancellationRequested();
        if (TryEnterFast(maximumActiveWriters))
        {
            Interlocked.Increment(ref totalGrantedCount);
            return new LibraDexWriteAdmissionLease(this);
        }

        Request request = Enqueue(maximumActiveWriters, maximumQueuedWriters, isAsync: true);
        using CancellationTokenRegistration registration = cancellationToken.Register(
            static state =>
            {
                (LibraDexWriteAdmission owner, Request request) tuple =
                    ((LibraDexWriteAdmission owner, Request request))state!;
                tuple.owner.Cancel(tuple.request);
            },
            (this, request));

        try
        {
            await request.Completion!.Task.WaitAsync(queueTimeout, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new LibraDexWriteAdmissionLease(this);
        }
        catch (TimeoutException)
        {
            if (!Cancel(request))
            {
                Release();
            }

            Interlocked.Increment(ref timedOutCount);
            throw;
        }
        catch (OperationCanceledException)
        {
            if (!Cancel(request))
            {
                Release();
            }

            Interlocked.Increment(ref canceledCount);
            throw;
        }
    }

    /// <summary>
    /// Returns an allocation-free fixed-field scheduler snapshot.<br/>
    /// </summary>
    internal LibraDexWriteAdmissionDiagnostics GetDiagnostics()
    {
        return new LibraDexWriteAdmissionDiagnostics(
            Volatile.Read(ref activeCount),
            Volatile.Read(ref pendingCount),
            Volatile.Read(ref maximumPendingCount),
            Interlocked.Read(ref totalQueuedCount),
            Interlocked.Read(ref totalGrantedCount),
            Interlocked.Read(ref canceledCount),
            Interlocked.Read(ref timedOutCount),
            Interlocked.Read(ref rejectedCount),
            Interlocked.Read(ref totalWaitTicks),
            Interlocked.Read(ref maximumWaitTicks));
    }

    /// <summary>
    /// Releases one active slot and grants pending requests in strict arrival order.<br/>
    /// A head request whose lower active limit is not yet eligible intentionally stops later grants so sustained high-limit traffic cannot starve it.<br/>
    /// </summary>
    internal void Release()
    {
        List<Request>? granted = null;
        lock (sync)
        {
            int remaining = Interlocked.Decrement(ref activeCount);
            if (remaining < 0)
            {
                _ = Interlocked.Exchange(ref activeCount, 0);
                throw new InvalidOperationException("The LibraDex write-admission active count became negative.");
            }

            while (pending.First is LinkedListNode<Request> node)
            {
                Request request = node.Value;
                int active = Volatile.Read(ref activeCount);
                if (active == 0)
                {
                    Volatile.Write(ref currentMaximumActiveWriters, request.MaximumActiveWriters);
                }

                int currentMaximum = Volatile.Read(ref currentMaximumActiveWriters);
                if (request.MaximumActiveWriters != currentMaximum || active >= currentMaximum)
                {
                    break;
                }

                pending.RemoveFirst();
                request.Node = null;
                request.Granted = true;
                _ = Interlocked.Increment(ref activeCount);
                Interlocked.Increment(ref totalGrantedCount);
                RecordWait(request);
                (granted ??= []).Add(request);
            }

            Volatile.Write(ref pendingCount, pending.Count);
        }

        if (granted is null)
        {
            return;
        }

        for (int i = 0; i < granted.Count; i++)
        {
            granted[i].Signal();
        }
    }

    private bool TryEnterFast(int maximumActiveWriters)
    {
        if (Volatile.Read(ref pendingCount) != 0)
        {
            return false;
        }

        int currentMaximum = Volatile.Read(ref currentMaximumActiveWriters);
        if (currentMaximum != maximumActiveWriters)
        {
            if (Volatile.Read(ref activeCount) != 0)
            {
                return false;
            }

            _ = Interlocked.CompareExchange(
                ref currentMaximumActiveWriters,
                maximumActiveWriters,
                currentMaximum);
            if (Volatile.Read(ref currentMaximumActiveWriters) != maximumActiveWriters)
            {
                return false;
            }
        }

        int enteredCount = Interlocked.Increment(ref activeCount);
        if (enteredCount <= maximumActiveWriters && Volatile.Read(ref pendingCount) == 0)
        {
            return true;
        }

        _ = Interlocked.Decrement(ref activeCount);
        return false;
    }

    private Request Enqueue(int maximumActiveWriters, int maximumQueuedWriters, bool isAsync)
    {
        lock (sync)
        {
            int active = Volatile.Read(ref activeCount);
            if (pending.Count == 0 &&
                (active == 0 || Volatile.Read(ref currentMaximumActiveWriters) == maximumActiveWriters) &&
                active < maximumActiveWriters)
            {
                if (active == 0)
                {
                    Volatile.Write(ref currentMaximumActiveWriters, maximumActiveWriters);
                }

                _ = Interlocked.Increment(ref activeCount);
                Interlocked.Increment(ref totalGrantedCount);
                return Request.GrantedRequest(isAsync);
            }

            if (pending.Count >= maximumQueuedWriters)
            {
                Interlocked.Increment(ref rejectedCount);
                throw new InvalidOperationException($"The LibraDex write queue reached its configured limit of {maximumQueuedWriters} pending actions.");
            }

            Request request = new(maximumActiveWriters, isAsync);
            request.Node = pending.AddLast(request);
            int queued = pending.Count;
            Volatile.Write(ref pendingCount, queued);
            Interlocked.Increment(ref totalQueuedCount);
            SetMaximum(ref maximumPendingCount, queued);
            return request;
        }
    }

    private bool Cancel(Request request)
    {
        if (request.Granted)
        {
            return false;
        }

        lock (sync)
        {
            if (request.Granted)
            {
                return false;
            }

            LinkedListNode<Request>? node = request.Node;
            if (node is not null)
            {
                pending.Remove(node);
                request.Node = null;
                Volatile.Write(ref pendingCount, pending.Count);
            }

            return true;
        }
    }

    private void RecordWait(Request request)
    {
        long elapsed = Math.Max(0, Stopwatch.GetTimestamp() - request.QueuedTimestamp);
        Interlocked.Add(ref totalWaitTicks, elapsed);
        SetMaximum(ref maximumWaitTicks, elapsed);
    }

    private static void Validate(int maximumActiveWriters, int maximumQueuedWriters, TimeSpan queueTimeout)
    {
        if (maximumActiveWriters <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumActiveWriters));
        }

        if (maximumQueuedWriters <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumQueuedWriters));
        }

        if (queueTimeout < TimeSpan.Zero && queueTimeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(queueTimeout));
        }
    }

    private static void SetMaximum(ref int target, int value)
    {
        int current = Volatile.Read(ref target);
        while (value > current)
        {
            int observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }

    private static void SetMaximum(ref long target, long value)
    {
        long current = Interlocked.Read(ref target);
        while (value > current)
        {
            long observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }

    private sealed class Request
    {
        internal Request(int maximumActiveWriters, bool isAsync)
        {
            MaximumActiveWriters = maximumActiveWriters;
            QueuedTimestamp = Stopwatch.GetTimestamp();
            if (isAsync)
            {
                Completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            else
            {
                Ready = new ManualResetEventSlim(false, 0);
            }
        }

        internal int MaximumActiveWriters { get; }

        internal long QueuedTimestamp { get; }

        internal ManualResetEventSlim? Ready { get; }

        internal TaskCompletionSource? Completion { get; }

        internal LinkedListNode<Request>? Node { get; set; }

        internal bool Granted { get; set; }

        internal static Request GrantedRequest(bool isAsync)
        {
            Request request = new(int.MaxValue, isAsync)
            {
                Granted = true
            };
            request.Signal();
            return request;
        }

        internal void Signal()
        {
            Ready?.Set();
            Completion?.TrySetResult();
        }
    }
}

/// <summary>
/// Represents one active session write-admission slot.<br/>
/// </summary>
internal sealed class LibraDexWriteAdmissionLease : IDisposable
{
    private LibraDexWriteAdmission? owner;

    internal LibraDexWriteAdmissionLease(LibraDexWriteAdmission owner)
    {
        this.owner = owner;
    }

    /// <summary>
    /// Releases the session slot once.<br/>
    /// </summary>
    public void Dispose()
    {
        Interlocked.Exchange(ref owner, null)?.Release();
    }
}
