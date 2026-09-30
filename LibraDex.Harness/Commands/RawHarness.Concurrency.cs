using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LibraDex;
using LibraDex.Layouts;
using LibraDex.Views;

internal static partial class RawHarness
{
    /// <summary>
    /// Runs the focused public admission, bounded-queue, cancellation, rotation, shelf-notification, and reader-progress contract probes.<br/>
    /// This narrow command isolates scheduler regressions without paying for the full internal concurrency matrix.<br/>
    /// </summary>
    /// <param name="args">The harness command-line arguments.<br/></param>
    /// <returns>Zero when every focused public concurrency contract passes.<br/></returns>
    private static int RunConcurrencyAdmissionSanity(string[] args)
    {
        RunPublicMixedLimitAdmissionStarvationProbe();
        RunPublicAdmissionBoundTimeoutDiagnosticsProbe();
        RunPublicDirectWriterAdmissionProbe();
        RunPublicActionRotationProbe();
        RunPublicShelfReleaseNotificationProbe();
        RunPublicConcurrentReaderProgressProbe();
        Console.WriteLine("concurrency-admission-sanity ok");
        return 0;
    }

    /// <summary>
    /// Validates the current LibraDex concurrency contract around active same-session write windows.<br/>
    /// The command intentionally proves the narrow production packet: detailed diagnostics reject overlapping same-session writes before mutation, while the owning batch can still commit or abort from another managed thread by carrying its operation token.<br/>
    /// </summary>
    /// <param name="args">The harness command-line arguments.<br/></param>
    /// <returns>Zero when public overlap, cross-thread owner-token, and internal wrong-token diagnostics match the documented contract.<br/></returns>
    private static int RunConcurrencyContractSanity(string[] args)
    {
        RunPublicOverlappingBatchRejectedBeforeMutationProbe();
        RunPublicCrossThreadOwnerTokenProbe();
        RunPublicActiveIndexBatchRejectsUnrelatedImmediateMutationProbe();
        RunPublicActiveIndexBatchRejectsQueuedWriterProbe();
        RunPublicActiveIndexBatchRejectsScalarKeyStateMutationProbe();
        RunPublicActiveIndexBatchRejectsStringKeyStateMutationProbe();
        RunPublicActiveIndexBatchRejectsCompositeMutationProbe();
        RunPublicActiveGroupBatchStillAcceptsParticipatingInsertsProbe();
        RunAbraxasMaterializedReadBoundaryProbe();
        RunInternalScalar8Scalar8WriterContextProbe();
        RunInternalScalar8Scalar8StalePhysicalRouteClaimProbe();
        RunInternalVarKeyScalar8WriterContextProbe();
        RunInternalVarKeyScalar8TerminalSiblingFallbackProbe();
        RunInternalVarKeyScalar8StalePhysicalRouteClaimProbe();
        RunInternalVarKeyScalar8ColdRootPrefixOwnerProbe();
        RunInternalVarKeyScalar16WriterContextProbe();
        RunInternalVarKeyScalar16StaleRouteClaimProbe();
        RunInternalScalar8VarIdentityWriterContextProbe();
        RunInternalScalar8VarIdentityStaleRouteClaimProbe();
        RunInternalScalar16VarIdentityWriterContextProbe();
        RunInternalScalar16VarIdentityStaleRouteClaimProbe();
        RunInternalVarKeyVarIdentityWriterContextProbe();
        RunInternalVarKeyVarIdentityStaleRouteClaimProbe();
        RunInternalScalar8Scalar8StoragePublicationGateProbe();
        RunInternalGenericScalar8Scalar8ColdRouteNarrowFallbackProbe();
        RunInternalScalar8Scalar8ColdRootPrefixOwnerProbe();
        RunInternalScalar8Scalar8RootPrefixSplitNarrowFallbackProbe();
        RunInternalScalar8Scalar8RootShelfTransformNarrowFallbackProbe();
        RunInternalScalar8Scalar8ParentRouteSplitNarrowFallbackProbe();
        RunInternalScalar8Scalar8DuplicateKeyOverflowNarrowFallbackProbe();
        RunInternalScalar8Scalar8TerminalIdentityOverflowNarrowFallbackProbe();
        RunInternalScalar8Scalar8DuplicateRunChainNarrowFallbackProbe();
        RunInternalScalar8Scalar8QueuedSplitFallbackProbe();
        RunInternalScalar8Scalar8QueuedDuplicateRunFallbackProbe();
        RunInternalScalar8Scalar8QueuedSingleShelfDuplicateRunWriterContextProbe();
        RunInternalGenericScalar8Scalar8QueuedExactDeleteProbe();
        RunInternalGenericScalar8Scalar8QueuedTerminalDeleteFallbackProbe();
        RunInternalGenericScalar8Scalar8QueuedRekeyProbe();
        RunInternalGenericScalar8Scalar8DirectConcurrentInsertProbe();
        RunInternalGenericScalar8Scalar8DirectConcurrentDeleteProbe();
        RunInternalGenericScalar8Scalar8CursorConcurrentDeleteProbe();
        RunInternalGenericScalar8Scalar8CriteriaDeleteQueuedBridgeProbe();
        RunInternalGenericScalar8Scalar8DirectConcurrentRekeyProbe();
        RunInternalGenericScalar8Scalar8CursorConcurrentSetKeyProbe();
        RunInternalGenericScalar8Scalar8CriteriaSetKeyQueuedBridgeProbe();
        RunInternalGenericScalar8Scalar8ScalarNullRouteConcurrentProbe();
        RunInternalGenericScalar8Scalar8ScalarNullNonNullDeleteProbe();
        RunInternalGenericScalar8Scalar8AllDeleteQueuedValueProbe();
        RunInternalGenericScalar16Scalar8CursorMutationConvergenceProbe();
        RunInternalGenericScalar16Scalar8QueuedWriterFacadeProbe();
        RunInternalGenericScalar16Scalar8DirectConcurrentInsertProbe();
        RunInternalGenericScalar16Scalar8SameShelfContentionProbe();
        RunInternalGenericScalar16Scalar8DirectConcurrentDeleteProbe();
        RunInternalGenericScalar16Scalar8DirectConcurrentRekeyProbe();
        RunInternalGenericScalar16Scalar8CriteriaDeleteBridgeProbe();
        RunInternalGenericScalar16Scalar8CriteriaSetKeyBridgeProbe();
        RunInternalGenericScalar16Scalar8ColdRouteFallbackProbe();
        RunInternalGenericScalar16Scalar8ColdRootPrefixOwnerProbe();
        RunInternalGenericScalar16Scalar8FullShelfSerializedTopologyProbe();
        RunInternalGenericScalar16Scalar8StaleRouteClaimProbe();
        RunInternalGenericScalar16Scalar8PostSplitDeleteFacadeProbe();
        RunInternalGenericScalar8Scalar16CursorMutationConvergenceProbe();
        RunInternalGenericScalar8Scalar16QueuedWriterBoundaryProbe();
        RunInternalGenericScalar8Scalar16DirectConcurrentInsertProbe();
        RunInternalGenericScalar8Scalar16SameShelfContentionProbe();
        RunInternalGenericScalar8Scalar16DirectConcurrentDeleteProbe();
        RunInternalGenericScalar8Scalar16DirectConcurrentRekeyProbe();
        RunInternalGenericScalar8Scalar16CriteriaDeleteBridgeProbe();
        RunInternalGenericScalar8Scalar16CriteriaSetKeyBridgeProbe();
        RunInternalGenericScalar8Scalar16ColdRouteFallbackProbe();
        RunInternalGenericScalar8Scalar16FullShelfSerializedTopologyProbe();
        RunInternalGenericScalar8Scalar16StaleRouteClaimProbe();
        RunInternalGenericScalar16Scalar16CursorMutationConvergenceProbe();
        RunInternalGenericScalar16Scalar16QueuedWriterBoundaryProbe();
        RunInternalGenericScalar16Scalar16DirectConcurrentInsertProbe();
        RunInternalGenericScalar16Scalar16SameShelfContentionProbe();
        RunInternalGenericScalar16Scalar16DirectConcurrentDeleteProbe();
        RunInternalGenericScalar16Scalar16DirectConcurrentRekeyProbe();
        RunInternalGenericScalar16Scalar16CriteriaDeleteBridgeProbe();
        RunInternalGenericScalar16Scalar16CriteriaSetKeyBridgeProbe();
        RunInternalGenericScalar16Scalar16ColdRouteFallbackProbe();
        RunInternalGenericScalar16Scalar16FullShelfSerializedTopologyProbe();
        RunInternalGenericScalar16Scalar16StaleRouteClaimProbe();
        RunInternalGenericFixed32Scalar8StaleRouteClaimProbe();
        RunInternalGenericFixed32Scalar16StaleRouteClaimProbe();
        RunInternalFixedNScalar8RootShelfClaimProbe();
        RunInternalFixedNScalar16RootShelfClaimProbe();
        RunInternalUnsignedScalar8Scalar8QueuedWriterProbe();
        RunInternalGenericScalar8Scalar8QueuedWriterProbe();
        RunInternalGenericScalar8Scalar8QueuedDifferentIndexProbe();
        RunInternalGenericScalar8Scalar8QueuedDifferentIndexFallbackProbe();
        RunAbraxasIdentityWriteAdapterQueuedWriterProbe();
        RunAbraxasIdentityWriteAdapterQueuedDeleteProbe();
        RunAbraxasIdentityWriteAdapterQueuedRekeyProbe();
        RunPublicConcurrentWriterQueuedCancellationProbe();
        RunPublicConcurrentBatchCancellationProbe();
        RunPublicMixedLimitAdmissionStarvationProbe();
        RunPublicAdmissionBoundTimeoutDiagnosticsProbe();
        RunPublicDirectWriterAdmissionProbe();
        RunPublicActionRotationProbe();
        RunPublicShelfReleaseNotificationProbe();
        RunPublicConcurrentReaderProgressProbe();
        RunInternalWrongTokenRejectedProbe();
        Console.WriteLine("concurrency-contract-sanity ok");
        return 0;
    }

    /// <summary>
    /// Proves that a cancellation request from an unrelated thread removes a queued writer from session admission without publishing its tuple.<br/>
    /// A concurrent batch holds the sole configured admission slot while a normal concurrent writer waits behind it.<br/>
    /// </summary>
    private static void RunPublicConcurrentWriterQueuedCancellationProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });
        using LibraDexIndex<long, long> index = catalog.Indexes["concurrent-writer-cancel"]["value"].Int64Keys<long>().Create();
        long seedKey = CreateConcurrencyProofGenericLongKey(1, 0x42);
        long stagedKey = CreateConcurrencyProofGenericLongKey(2, 0x42);
        long canceledKey = CreateConcurrencyProofGenericLongKey(3, 0x43);
        _ = index.Insert(seedKey, seedKey);

        LibraDexConcurrencyOptions options = new()
        {
            Mode = LibraDexConcurrencyMode.QueuedWriter,
            MaxActiveWriters = 1
        };
        using LibraDexConcurrentBatch<long, long> holder = index.BeginConcurrentBatch(options);
        LibraDexGenericInsertResult staged = holder.Insert(stagedKey, stagedKey);
        if (!staged.Inserted)
        {
            throw new InvalidDataException("Concurrent writer cancellation probe could not stage the admission-holder tuple.");
        }

        using CancellationTokenSource cancellation = new();
        LibraDexQueuedWriter<long, long> queued = index.BeginConcurrentWriter(options, cancellation.Token);
        Task<bool> waiting = Task.Run(() =>
        {
            try
            {
                using LibraDexConcurrentWriteAction<long, long> action = queued.BeginAction(cancellation.Token);
                _ = action.Insert(canceledKey, canceledKey);
                return false;
            }
            catch (OperationCanceledException)
            {
                return true;
            }
        });

        if (waiting.Wait(100))
        {
            throw new InvalidDataException("Concurrent writer cancellation probe expected the second writer to remain queued behind the active batch.");
        }

        cancellation.Cancel();
        if (!waiting.Wait(TimeSpan.FromSeconds(5)) || !waiting.Result)
        {
            throw new InvalidDataException("Concurrent writer cancellation probe did not observe queued cancellation.");
        }

        holder.Abort();
        long count = CountGenericLongLong(index, out _);
        if (count != 1)
        {
            throw new InvalidDataException($"Concurrent writer cancellation probe expected only the seed tuple after abort, got {count} tuples.");
        }
    }

    /// <summary>
    /// Proves that cancellation observed before final batch publication aborts unpublished shelf-local state.<br/>
    /// This models a window-close or application-shutdown signal arriving from another thread between staging and publish.<br/>
    /// </summary>
    private static void RunPublicConcurrentBatchCancellationProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });
        using LibraDexIndex<long, long> index = catalog.Indexes["concurrent-batch-cancel"]["value"].Int64Keys<long>().Create();
        long seedKey = CreateConcurrencyProofGenericLongKey(1, 0x51);
        long canceledKey = CreateConcurrencyProofGenericLongKey(2, 0x51);
        _ = index.Insert(seedKey, seedKey);

        using CancellationTokenSource cancellation = new();
        using LibraDexConcurrentBatch<long, long> batch = index.BeginConcurrentBatch(
            LibraDexConcurrencyOptions.QueuedWriter,
            cancellation.Token);
        LibraDexGenericInsertResult staged = batch.Insert(canceledKey, canceledKey);
        if (!staged.Inserted)
        {
            throw new InvalidDataException("Concurrent batch cancellation probe could not stage its test tuple.");
        }

        Task.Run(cancellation.Cancel).GetAwaiter().GetResult();
        try
        {
            _ = batch.Publish();
            throw new InvalidDataException("Concurrent batch cancellation probe expected publication to observe cancellation.");
        }
        catch (OperationCanceledException)
        {
        }

        long count = CountGenericLongLong(index, out _);
        if (count != 1)
        {
            throw new InvalidDataException($"Concurrent batch cancellation probe expected only the seed tuple after cancellation, got {count} tuples.");
        }
    }

    /// <summary>
    /// Proves that a queued one-writer action reaches the head and drains higher-limit activity before a later high-limit request can pass it.<br/>
    /// </summary>
    private static void RunPublicMixedLimitAdmissionStarvationProbe()
    {
        using Catalog catalog = Catalog.CreateMemory();
        using LibraDexIndex<long, long> index = catalog.Indexes["admission-starvation"]["value"].Int64Keys<long>().Create();
        LibraDexConcurrencyOptions highOptions = new()
        {
            Mode = LibraDexConcurrencyMode.QueuedWriter,
            MaxActiveWriters = 2,
            MaxQueuedWriters = 8
        };
        LibraDexConcurrencyOptions lowOptions = new()
        {
            Mode = LibraDexConcurrencyMode.QueuedWriter,
            MaxActiveWriters = 1,
            MaxQueuedWriters = 8
        };
        LibraDexQueuedWriter<long, long> high = index.BeginConcurrentWriter(highOptions);
        LibraDexQueuedWriter<long, long> low = index.BeginConcurrentWriter(lowOptions);
        using LibraDexConcurrentWriteAction<long, long> active1 = high.BeginAction();
        using LibraDexConcurrentWriteAction<long, long> active2 = high.BeginAction();
        Task<LibraDexConcurrentWriteAction<long, long>> lowWait = low.BeginActionAsync().AsTask();
        WaitForAdmissionQueueDepth(high, 1);
        Task<LibraDexConcurrentWriteAction<long, long>> laterHigh = high.BeginActionAsync().AsTask();
        WaitForAdmissionQueueDepth(high, 2);

        active1.Dispose();
        if (lowWait.Wait(100) || laterHigh.Wait(100))
        {
            throw new InvalidDataException("Mixed-limit admission granted work before the active count drained to the head request's limit.");
        }

        active2.Dispose();
        using LibraDexConcurrentWriteAction<long, long> lowAction = lowWait.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        if (laterHigh.IsCompleted)
        {
            throw new InvalidDataException("A later high-limit action bypassed the queued low-limit head request.");
        }

        lowAction.Dispose();
        using LibraDexConcurrentWriteAction<long, long> highAction = laterHigh.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Proves bounded-queue rejection, finite queue timeout, asynchronous cancellation, and fixed-field scheduler diagnostics.<br/>
    /// </summary>
    private static void RunPublicAdmissionBoundTimeoutDiagnosticsProbe()
    {
        using Catalog catalog = Catalog.CreateMemory();
        using LibraDexIndex<long, long> index = catalog.Indexes["admission-bounds"]["value"].Int64Keys<long>().Create();
        LibraDexConcurrencyOptions options = new()
        {
            Mode = LibraDexConcurrencyMode.QueuedWriter,
            MaxActiveWriters = 1,
            MaxQueuedWriters = 1
        };
        LibraDexQueuedWriter<long, long> writer = index.BeginConcurrentWriter(options);
        using LibraDexConcurrentWriteAction<long, long> active = writer.BeginAction();
        using CancellationTokenSource queuedCancellation = new();
        Task<LibraDexConcurrentWriteAction<long, long>> queued = writer.BeginActionAsync(queuedCancellation.Token).AsTask();
        WaitForAdmissionQueueDepth(writer, 1);

        try
        {
            _ = writer.BeginAction();
            throw new InvalidDataException("Bounded admission accepted a request beyond MaxQueuedWriters.");
        }
        catch (InvalidOperationException)
        {
        }

        LibraDexConcurrencyOptions timeoutOptions = new()
        {
            Mode = LibraDexConcurrencyMode.QueuedWriter,
            MaxActiveWriters = 1,
            MaxQueuedWriters = 2,
            QueueTimeout = TimeSpan.FromMilliseconds(50)
        };
        LibraDexQueuedWriter<long, long> timeoutWriter = index.BeginConcurrentWriter(timeoutOptions);
        try
        {
            _ = timeoutWriter.BeginAction();
            throw new InvalidDataException("Finite write admission did not time out.");
        }
        catch (TimeoutException)
        {
        }

        queuedCancellation.Cancel();
        try
        {
            _ = queued.GetAwaiter().GetResult();
            throw new InvalidDataException("Asynchronous admission did not observe cancellation.");
        }
        catch (OperationCanceledException)
        {
        }

        LibraDexWriteAdmissionDiagnostics diagnostics = writer.GetAdmissionDiagnostics();
        if (diagnostics.MaximumQueuedWriters < 1 ||
            diagnostics.RejectedWriters < 1 ||
            diagnostics.TimedOutWriters < 1 ||
            diagnostics.CanceledWriters < 1)
        {
            throw new InvalidDataException($"Admission diagnostics were incomplete: {diagnostics}.");
        }
    }

    /// <summary>
    /// Proves that the low-friction one-call writer API enters the same bounded session queue instead of bypassing an already active action.<br/>
    /// The operation must remain pending until the owning action releases its slot, then complete normally without requiring the developer to create an explicit action.<br/>
    /// </summary>
    private static void RunPublicDirectWriterAdmissionProbe()
    {
        using Catalog catalog = Catalog.CreateMemory();
        using LibraDexIndex<long, long> index = catalog.Indexes["direct-writer-admission"]["value"].Int64Keys<long>().Create();
        LibraDexQueuedWriter<long, long> writer = index.BeginConcurrentWriter(new LibraDexConcurrencyOptions
        {
            Mode = LibraDexConcurrencyMode.QueuedWriter,
            MaxActiveWriters = 1,
            MaxQueuedWriters = 4
        });
        using LibraDexConcurrentWriteAction<long, long> owner = writer.BeginAction();
        Task<LibraDexGenericInsertResult> direct = Task.Run(() => writer.Insert(1, 1));
        WaitForAdmissionQueueDepth(writer, 1);
        if (direct.IsCompleted)
        {
            throw new InvalidDataException("A direct queued-writer call bypassed the active session action.");
        }

        owner.Dispose();
        LibraDexGenericInsertResult result = direct.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        if (!result.Inserted || index.Count() != 1)
        {
            throw new InvalidDataException("The admitted direct queued-writer call did not complete after owner release.");
        }
    }

    /// <summary>
    /// Proves that an action exceeding its item budget releases its lease, lets an older queued action run, and then reacquires admission.<br/>
    /// </summary>
    private static void RunPublicActionRotationProbe()
    {
        using Catalog catalog = Catalog.CreateMemory();
        using LibraDexIndex<long, long> index = catalog.Indexes["action-rotation"]["value"].Int64Keys<long>().Create();
        LibraDexConcurrencyOptions options = new()
        {
            Mode = LibraDexConcurrencyMode.QueuedWriter,
            MaxActiveWriters = 1,
            MaxActionItems = 2
        };
        LibraDexQueuedWriter<long, long> writer = index.BeginConcurrentWriter(options);
        using LibraDexConcurrentWriteAction<long, long> first = writer.BeginAction();
        _ = first.Insert(1, 1);
        _ = first.Insert(2, 2);
        Task<LibraDexConcurrentWriteAction<long, long>> secondWait = writer.BeginActionAsync().AsTask();
        WaitForAdmissionQueueDepth(writer, 1);
        Task<LibraDexGenericInsertResult> rotatedInsert = Task.Run(() => first.Insert(3, 3));
        using LibraDexConcurrentWriteAction<long, long> second = secondWait.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        if (rotatedInsert.IsCompleted)
        {
            throw new InvalidDataException("Automatic action rotation bypassed the older queued action.");
        }

        second.Dispose();
        LibraDexGenericInsertResult result = rotatedInsert.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        if (!result.Inserted)
        {
            throw new InvalidDataException("Automatic action rotation did not resume the original producer.");
        }
    }

    /// <summary>
    /// Proves that same-shelf concurrent batches wait for an owner-release notification and then complete without spin retry.<br/>
    /// </summary>
    private static void RunPublicShelfReleaseNotificationProbe()
    {
        using Catalog catalog = Catalog.CreateMemory();
        using LibraDexIndex<long, long> index = catalog.Indexes["shelf-release-notification"]["value"].Int64Keys<long>().Create();
        _ = index.Insert(1, 1);
        LibraDexConcurrencyOptions options = new()
        {
            Mode = LibraDexConcurrencyMode.QueuedWriter,
            MaxActiveWriters = 2
        };
        using LibraDexConcurrentBatch<long, long> owner = index.BeginConcurrentBatch(options);
        _ = owner.Insert(2, 2);
        Task<bool> contender = Task.Run(() =>
        {
            using LibraDexConcurrentBatch<long, long> batch = index.BeginConcurrentBatch(options);
            bool inserted = batch.Insert(3, 3).Inserted;
            _ = batch.Publish();
            return inserted;
        });

        long deadline = Stopwatch.GetTimestamp() + (5 * Stopwatch.Frequency);
        while (index.BeginConcurrentWriter().GetAdmissionDiagnostics().ShelfWaitCount == 0 && Stopwatch.GetTimestamp() < deadline)
        {
            Thread.Yield();
        }

        if (contender.IsCompleted)
        {
            throw new InvalidDataException("Same-shelf contender completed before the owner released its staged shelf.");
        }

        _ = owner.Publish();
        if (!contender.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult())
        {
            throw new InvalidDataException("Same-shelf contender did not insert after owner-release notification.");
        }
    }

    /// <summary>
    /// Proves that readers make progress while a managed producer performs rotated concurrent write actions.<br/>
    /// </summary>
    private static void RunPublicConcurrentReaderProgressProbe()
    {
        using Catalog catalog = Catalog.CreateMemory();
        using LibraDexIndex<long, long> index = catalog.Indexes["reader-progress"]["value"].Int64Keys<long>().Create();
        for (int i = 0; i < 100; i++)
        {
            _ = index.Insert(i, i);
        }

        LibraDexQueuedWriter<long, long> writer = index.BeginConcurrentWriter(new LibraDexConcurrencyOptions
        {
            Mode = LibraDexConcurrencyMode.QueuedWriter,
            MaxActionItems = 32
        });
        using ManualResetEventSlim readerStarted = new(false);
        using CancellationTokenSource finished = new();
        long readerPasses = 0;
        Task reader = Task.Run(() =>
        {
            while (!finished.IsCancellationRequested)
            {
                _ = CountGenericLongLong(index, out _);
                Interlocked.Increment(ref readerPasses);
                readerStarted.Set();
            }
        });
        readerStarted.Wait(TimeSpan.FromSeconds(5));
        IEnumerable<(long Key, long Identity)> Items()
        {
            for (long i = 100; i < 1100; i++)
            {
                yield return (i, i);
            }
        }

        long inserted = writer.InsertAll(Items());
        finished.Cancel();
        reader.Wait(TimeSpan.FromSeconds(5));
        long count = CountGenericLongLong(index, out _);
        if (inserted != 1000 || count != 1100 || Interlocked.Read(ref readerPasses) == 0)
        {
            throw new InvalidDataException($"Reader progress probe failed: inserted={inserted} count={count} readerPasses={readerPasses}.");
        }
    }

    private static void WaitForAdmissionQueueDepth<TKey, TIdentity>(
        LibraDexQueuedWriter<TKey, TIdentity> writer,
        int expected)
    {
        long deadline = Stopwatch.GetTimestamp() + (5 * Stopwatch.Frequency);
        while (writer.GetAdmissionDiagnostics().QueuedWriters < expected && Stopwatch.GetTimestamp() < deadline)
        {
            Thread.Yield();
        }

        if (writer.GetAdmissionDiagnostics().QueuedWriters < expected)
        {
            throw new TimeoutException($"The admission queue did not reach depth {expected}.");
        }
    }

    /// <summary>
    /// Runs a deterministic Abraxas-shaped write-concurrency workload matrix and reports which queued writes use writer-context staging versus serialized fallback.<br/>
    /// This is a decision aid for integration planning: every row uses caller-facing generic queued writers where practical, then drops to encoded `SS8-8` only for route-topology fixtures that need exact shelf pressure.<br/>
    /// </summary>
    /// <param name="args">The harness command-line arguments.<br/></param>
    /// <returns>Zero when all rows match the expected path attribution and readback checks.<br/></returns>
    private static int RunConcurrencyWorkloadMatrix(string[] args)
    {
        ConcurrencyWorkloadMatrixRow[] rows =
        [
            RunWorkloadMatrixDifferentIndexWarm(),
            RunWorkloadMatrixSameIndexDifferentShelfWarm(),
            RunWorkloadMatrixSameIndexSameShelfWarm(),
            RunWorkloadMatrixColdRouteFallback(),
            RunWorkloadMatrixSplitFallback(),
            RunWorkloadMatrixDuplicateOverflowFallback(),
            RunWorkloadMatrixTerminalIdentityLocal(),
            RunWorkloadMatrixTerminalIdentityDeleteLocal(),
            RunWorkloadMatrixScalar16Scalar8DifferentShelfWarm(),
            RunWorkloadMatrixScalar16Scalar8SameShelfWarm(),
            RunWorkloadMatrixScalar16Scalar8ColdRouteFallback(),
            RunWorkloadMatrixScalar16Scalar8FullShelfFallback(),
            RunWorkloadMatrixScalar8Scalar16DifferentShelfWarm(),
            RunWorkloadMatrixScalar8Scalar16SameShelfWarm(),
            RunWorkloadMatrixScalar8Scalar16ColdRouteFallback(),
            RunWorkloadMatrixScalar8Scalar16FullShelfFallback(),
            RunWorkloadMatrixScalar16Scalar16DifferentShelfWarm(),
            RunWorkloadMatrixScalar16Scalar16SameShelfWarm(),
            RunWorkloadMatrixScalar16Scalar16ColdRouteFallback(),
            RunWorkloadMatrixScalar16Scalar16FullShelfFallback(),
            RunWorkloadMatrixScalar8Scalar8DeleteWarm(),
            RunWorkloadMatrixScalar8Scalar8RekeyWarm(),
            RunWorkloadMatrixScalar16Scalar8DeleteWarm(),
            RunWorkloadMatrixScalar16Scalar8RekeyWarm(),
            RunWorkloadMatrixScalar8Scalar16DeleteWarm(),
            RunWorkloadMatrixScalar8Scalar16RekeyWarm(),
            RunWorkloadMatrixScalar16Scalar16DeleteWarm(),
            RunWorkloadMatrixScalar16Scalar16RekeyWarm(),
            RunWorkloadMatrixFixed32Scalar8Warm(),
            RunWorkloadMatrixFixed32Scalar8DeleteWarm(),
            RunWorkloadMatrixFixed32Scalar8RekeyWarm(),
            RunWorkloadMatrixFixed32Scalar8ColdRouteFallback(),
            RunWorkloadMatrixFixed32Scalar8FullShelfFallback(),
            RunWorkloadMatrixFixed32Scalar16Warm(),
            RunWorkloadMatrixFixed32Scalar16DeleteWarm(),
            RunWorkloadMatrixFixed32Scalar16RekeyWarm(),
            RunWorkloadMatrixFixed32Scalar16ColdRouteFallback(),
            RunWorkloadMatrixFixed32Scalar16FullShelfFallback(),
            RunWorkloadMatrixExactReversedProjectionBoundary(),
            RunWorkloadMatrixExactReversedProjectionDeleteWarm(),
            RunWorkloadMatrixExactReversedProjectionRekeyWarm()
        ];

        Console.WriteLine("name                                 ops writer-context narrow-topology serialized-fallback changed note");
        for (int i = 0; i < rows.Length; i++)
        {
            ConcurrencyWorkloadMatrixRow row = rows[i];
            Console.WriteLine(
                $"{row.Name,-36} {row.Operations,3} {row.WriterContext,14} {row.NarrowTopology,15} {row.SerializedFallback,19} {row.Changed,7} {row.Note}");
        }

        Console.WriteLine("concurrency-workload-matrix ok");
        return 0;
    }

    /// <summary>
    /// Runs the focused `SS8-8` primitive-domain concurrency proof and prints a compact performance report.<br/>
    /// The proof first executes deterministic multi-thread domain checks, then reports isolated one-thread, queued one-thread, different-shelf multi-thread, and same-shelf multi-thread timing rows.<br/>
    /// </summary>
    /// <param name="args">The harness command-line arguments.<br/></param>
    /// <returns>Zero when every deterministic proof and measured row validates final readable counts.<br/></returns>
    private static int RunScalar8Scalar8PrimitiveConcurrencyProof(string[] args)
    {
        int singleOps = GetIntOption(args, "--single-ops", 512);
        int opsPerThread = GetIntOption(args, "--ops-per-thread", 128);
        int threads = GetIntOption(args, "--threads", 4);
        if (singleOps <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(args), singleOps, "SS8-8 primitive proof single ops must be positive.");
        }

        if (opsPerThread <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(args), opsPerThread, "SS8-8 primitive proof ops per thread must be positive.");
        }

        if (threads <= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(args), threads, "SS8-8 primitive proof requires at least two threads.");
        }

        RunInternalScalar8Scalar8ColdRootPrefixOwnerProbe();
        RunScalar8Scalar8PrimitiveSameDifferentShelfProof();
        RunScalar8Scalar8PrimitiveSplitSourceShelfProof();

        Scalar8Scalar8PrimitiveConcurrencyProofRow[] rows =
        [
            RunScalar8Scalar8PrimitiveIsolatedSingle(singleOps),
            RunScalar8Scalar8PrimitiveWriterContextBatchSingle(singleOps),
            RunScalar8Scalar8PrimitiveQueuedSingle(singleOps),
            RunScalar8Scalar8PrimitiveGenericDefaultSingle(singleOps),
            RunScalar8Scalar8PrimitiveGenericQueuedSingle(singleOps),
            RunScalar8Scalar8PrimitiveGenericConcurrentBatchSingle(singleOps),
            RunScalar8Scalar8PrimitiveDifferentShelfMulti(threads, opsPerThread),
            RunScalar8Scalar8PrimitiveSameShelfMulti(threads, Math.Max(1, opsPerThread / 4)),
            RunScalar8Scalar8PrimitiveDifferentShelfEqualOpsMulti(threads, singleOps),
            RunScalar8Scalar8PrimitiveSameShelfEqualOpsMulti(threads, singleOps),
            RunScalar8Scalar8PrimitiveGenericConcurrentBatchDifferentShelfMulti(threads, opsPerThread),
            RunScalar8Scalar8PrimitiveDeleteRekeyMulti(threads, Math.Max(1, opsPerThread / 4)),
            RunScalar8Scalar8PrimitiveGenericConcurrentBatchDeleteRekeyMulti(threads, Math.Max(1, opsPerThread / 4)),
            RunFixed32Scalar8ProjectionGenericDefaultSingle(singleOps),
            RunFixed32Scalar8ProjectionGenericQueuedSingle(singleOps),
            RunFixed32Scalar8ProjectionGenericConcurrentBatchSingle(singleOps),
            RunFixed32Scalar8ProjectionGenericConcurrentBatchDifferentShelfMulti(threads, opsPerThread),
            RunFixed32Scalar8ProjectionGenericConcurrentBatchDeleteRekeyMulti(threads, Math.Max(1, opsPerThread / 4)),
            RunStringScalar8ProjectionGenericDefaultSingle(singleOps),
            RunStringScalar8ProjectionGenericConcurrentBatchSingle(singleOps),
            RunStringScalar8ProjectionGenericConcurrentBatchDifferentShelfMulti(threads, opsPerThread),
            RunStringScalar8ProjectionGenericConcurrentBatchDeleteRekeyMulti(threads, Math.Max(1, opsPerThread / 4))
        ];

        Console.WriteLine("scenario threads ops elapsed-ms ops-sec alloc-bytes expected-count actual-count note");
        for (int i = 0; i < rows.Length; i++)
        {
            Scalar8Scalar8PrimitiveConcurrencyProofRow row = rows[i];
            Console.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "{0} {1} {2} {3:F3} {4:F2} {5} {6} {7} {8}",
                row.Scenario,
                row.Threads,
                row.Operations,
                row.Elapsed.TotalMilliseconds,
                row.OperationsPerSecond,
                row.AllocatedBytes,
                row.ExpectedCount,
                row.ActualCount,
                row.Note));
        }

        PrintScalar8Scalar8PrimitiveConcurrencyRatios(rows);
        Console.WriteLine("ss8-8-primitive-concurrency-proof ok");
        return 0;
    }

    /// <summary>
    /// Proves the primitive `SS8-8` shelf domain allows unrelated shelf progress while a same-shelf writer waits.<br/>
    /// One writer context holds a warmed shelf domain, a queued same-shelf insert is required to wait, and a queued different-shelf insert is required to complete before the held context is released.<br/>
    /// </summary>
    private static void RunScalar8Scalar8PrimitiveSameDifferentShelfProof()
    {
        using Scalar8Scalar8Index index = Indexes.SS88.Create(
            backingKind: DataKernelBackingKind.Memory,
            name: "ss88-primitive-same-different-shelf",
            options: CreateDesignPerfOptions(),
            developerMetadata: CreateDesignPerfMetadata(9920),
            telemetryOptions: DataKernelTelemetryOptions.FromLevel(LibraDexDiagnosticsLevel.Detailed));

        const ulong keyA1 = 0x1000_0000_0000_0001UL;
        const ulong keyA2 = 0x1000_0000_0000_0002UL;
        const ulong keyA3 = 0x1000_0000_0000_0003UL;
        const ulong keyB1 = 0x2000_0000_0000_0001UL;
        const ulong keyB2 = 0x2000_0000_0000_0002UL;
        _ = index.InsertEncoded(keyA1, 1001, allowDuplicateKeys: true);
        _ = index.InsertEncoded(keyB1, 2001, allowDuplicateKeys: true);

        LibraDexWriteContext? heldContext = null;
        Scalar8Scalar8QueuedWriter queuedWriter = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);
        try
        {
            heldContext = index.BeginWriteContext();
            Scalar8Scalar8EncodedInsertResult heldResult = index.InsertEncodedForWriteContext(heldContext, keyA2, 1002, allowDuplicateKeys: true);
            if (heldResult.Outcome != Scalar8Scalar8EncodedInsertOutcome.Inserted)
            {
                throw new InvalidDataException($"SS8-8 primitive shelf proof could not stage held writer-context insert. outcome={heldResult.Outcome}.");
            }

            Task<Scalar8Scalar8EncodedInsertResult> sameShelfTask = Task.Run(() => queuedWriter.InsertEncoded(keyA3, 1003, allowDuplicateKeys: true));
            if (sameShelfTask.Wait(TimeSpan.FromMilliseconds(100)))
            {
                throw new InvalidDataException("SS8-8 primitive shelf proof allowed a same-shelf writer to complete while the shelf domain was held.");
            }

            Task<Scalar8Scalar8EncodedInsertResult> differentShelfTask = Task.Run(() => queuedWriter.InsertEncoded(keyB2, 2002, allowDuplicateKeys: true));
            if (!differentShelfTask.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new InvalidDataException("SS8-8 primitive shelf proof blocked a different-shelf writer behind the held shelf domain.");
            }

            Scalar8Scalar8EncodedInsertResult differentResult = differentShelfTask.GetAwaiter().GetResult();
            if (differentResult.Outcome != Scalar8Scalar8EncodedInsertOutcome.Inserted ||
                differentResult.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext)
            {
                throw new InvalidDataException($"SS8-8 primitive shelf proof different-shelf insert attribution was wrong. outcome={differentResult.Outcome} path={differentResult.QueuedInsertPath}.");
            }

            index.AbortWriteContext(heldContext);
            heldContext = null;
            if (!sameShelfTask.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new InvalidDataException("SS8-8 primitive shelf proof same-shelf writer did not complete after the held domain was released.");
            }

            Scalar8Scalar8EncodedInsertResult sameResult = sameShelfTask.GetAwaiter().GetResult();
            if (sameResult.Outcome != Scalar8Scalar8EncodedInsertOutcome.Inserted ||
                sameResult.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext)
            {
                throw new InvalidDataException($"SS8-8 primitive shelf proof same-shelf insert attribution was wrong after release. outcome={sameResult.Outcome} path={sameResult.QueuedInsertPath}.");
            }
        }
        finally
        {
            if (heldContext is not null)
            {
                index.AbortWriteContext(heldContext);
            }
        }

        ulong[] identities = new ulong[8];
        Scalar8Scalar8EncodedRangeReadResult readA = index.ReadEncodedRange(keyA1, keyA3, identities);
        if (readA.IdentityCount != 2 ||
            identities[0] != 1001 ||
            identities[1] != 1003)
        {
            throw new InvalidDataException("SS8-8 primitive shelf proof same-shelf readback was wrong.");
        }

        Scalar8Scalar8EncodedRangeReadResult readB = index.ReadEncodedRange(keyB1, keyB2, identities);
        if (readB.IdentityCount != 2 ||
            identities[0] != 2001 ||
            identities[1] != 2002)
        {
            throw new InvalidDataException("SS8-8 primitive shelf proof different-shelf readback was wrong.");
        }
    }

    /// <summary>
    /// Proves a topology split synchronizes with a writer-context owner of the same source shelf while unrelated shelf writes continue.<br/>
    /// The fixture holds the full source shelf domain, verifies the queued split waits on that source shelf, verifies an unrelated warmed shelf insert completes, then releases the source shelf and validates the split publication.<br/>
    /// </summary>
    private static void RunScalar8Scalar8PrimitiveSplitSourceShelfProof()
    {
        using Scalar8Scalar8Index index = Indexes.SS88.Create(
            backingKind: DataKernelBackingKind.Memory,
            name: "ss88-primitive-split-source-shelf",
            options: CreateDesignPerfOptions(),
            developerMetadata: CreateDesignPerfMetadata(9921),
            telemetryOptions: DataKernelTelemetryOptions.FromLevel(LibraDexDiagnosticsLevel.Detailed));

        Scalar8Scalar8Profile profile = index.Handle.Profile;
        ulong[] encodedKeys = new ulong[profile.MaxItemCount];
        ulong[] encodedIdentities = new ulong[profile.MaxItemCount];
        const ulong leftBase = 0x3000_0000_0000_0000UL;
        const ulong rightBase = 0x3100_0000_0000_0000UL;
        int leftCount = profile.MaxItemCount / 2;
        for (int i = 0; i < profile.MaxItemCount; i++)
        {
            encodedKeys[i] = i < leftCount
                ? leftBase + (ulong)i
                : rightBase + (ulong)(i - leftCount);
            encodedIdentities[i] = (ulong)(300_000 + i);
        }

        _ = index.Session.CreateScalar8Scalar8ShelfAndLinkRootRoutes(
            index.Handle.RootRouterOffset,
            profile,
            encodedKeys,
            encodedIdentities,
            [0x30, 0x31]);
        _ = index.InsertEncoded(0x4000_0000_0000_0001UL, 4001, allowDuplicateKeys: true);

        long fullShelfOffset = index.Session.FindRouterTarget(index.Handle.RootRouterOffset, 0x31);
        if (fullShelfOffset == 0)
        {
            throw new InvalidDataException("SS8-8 primitive split source proof could not locate the shared full shelf.");
        }

        LibraDexWriteContext? heldContext = null;
        Scalar8Scalar8QueuedWriter queuedWriter = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);
        try
        {
            heldContext = index.BeginWriteContext();
            _ = index.Session.ReadScalar8Scalar8ShelfBytesForWriteContext(heldContext, fullShelfOffset, profile);

            ulong splitKey = rightBase + (ulong)profile.MaxItemCount;
            Task<Scalar8Scalar8EncodedInsertResult> splitTask = Task.Run(() => queuedWriter.InsertEncoded(splitKey, 399_999, allowDuplicateKeys: true));
            if (splitTask.Wait(TimeSpan.FromMilliseconds(100)))
            {
                throw new InvalidDataException("SS8-8 primitive split source proof allowed a split to complete while its source shelf domain was held.");
            }

            Task<Scalar8Scalar8EncodedInsertResult> unrelatedTask = Task.Run(() => queuedWriter.InsertEncoded(0x4000_0000_0000_0002UL, 4002, allowDuplicateKeys: true));
            if (!unrelatedTask.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new InvalidDataException("SS8-8 primitive split source proof blocked an unrelated shelf insert behind the held split source shelf.");
            }

            Scalar8Scalar8EncodedInsertResult unrelatedResult = unrelatedTask.GetAwaiter().GetResult();
            if (unrelatedResult.Outcome != Scalar8Scalar8EncodedInsertOutcome.Inserted ||
                unrelatedResult.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext)
            {
                throw new InvalidDataException($"SS8-8 primitive split source proof unrelated insert attribution was wrong. outcome={unrelatedResult.Outcome} path={unrelatedResult.QueuedInsertPath}.");
            }

            index.AbortWriteContext(heldContext);
            heldContext = null;
            if (!splitTask.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new InvalidDataException("SS8-8 primitive split source proof split did not complete after source shelf release.");
            }

            Scalar8Scalar8EncodedInsertResult splitResult = splitTask.GetAwaiter().GetResult();
            if (splitResult.Outcome != Scalar8Scalar8EncodedInsertOutcome.Inserted ||
                splitResult.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.NarrowTopologyPublisher ||
                splitResult.StructuralKind != Scalar8Scalar8RoutedInsertKind.RootPrefixSplit)
            {
                throw new InvalidDataException($"SS8-8 primitive split source proof split attribution was wrong. outcome={splitResult.Outcome} kind={splitResult.StructuralKind} path={splitResult.QueuedInsertPath}.");
            }
        }
        finally
        {
            if (heldContext is not null)
            {
                index.AbortWriteContext(heldContext);
            }
        }

        ulong[] identities = new ulong[profile.MaxItemCount + 2];
        Scalar8Scalar8EncodedRangeReadResult splitRead = index.ReadEncodedRange(leftBase, rightBase + (ulong)profile.MaxItemCount, identities);
        if (splitRead.IdentityCount != profile.MaxItemCount + 1 ||
            identities[profile.MaxItemCount] != 399_999)
        {
            throw new InvalidDataException("SS8-8 primitive split source proof split readback was wrong.");
        }

        Scalar8Scalar8EncodedRangeReadResult unrelatedRead = index.ReadEncodedRange(0x4000_0000_0000_0001UL, 0x4000_0000_0000_0002UL, identities);
        if (unrelatedRead.IdentityCount != 2 ||
            identities[0] != 4001 ||
            identities[1] != 4002)
        {
            throw new InvalidDataException("SS8-8 primitive split source proof unrelated shelf readback was wrong.");
        }
    }

    /// <summary>
    /// Measures isolated one-thread `SS8-8` direct insert behavior without caller-side concurrency contention.<br/>
    /// This is the primitive correctness/performance baseline for the same operation shape that the queued rows execute through the concurrency facade.<br/>
    /// </summary>
    /// <param name="operations">The number of inserts to perform.<br/></param>
    /// <returns>The measured proof row with readback validation.</returns>
    private static Scalar8Scalar8PrimitiveConcurrencyProofRow RunScalar8Scalar8PrimitiveIsolatedSingle(int operations)
    {
        using Scalar8Scalar8Index index = Indexes.SS88.Create(
            backingKind: DataKernelBackingKind.Memory,
            name: "ss88-primitive-isolated-single",
            options: CreateDesignPerfOptions(),
            telemetryOptions: DataKernelTelemetryOptions.EnabledOptions);
        _ = index.InsertEncoded(CreateConcurrencyProofScalar8Key(0, stripe: 0x50), 1, allowDuplicateKeys: true);

        long before = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 1; i <= operations; i++)
        {
            _ = index.InsertEncoded(CreateConcurrencyProofScalar8Key(i, stripe: 0x50), (ulong)(i + 1), allowDuplicateKeys: true);
        }

        watch.Stop();
        int expected = operations + 1;
        int actual = CountScalar8Scalar8(index, 0, ulong.MaxValue, out _);
        return CreateScalar8Scalar8PrimitiveConcurrencyProofRow(
            "isolated-1t",
            threads: 1,
            operations,
            watch.Elapsed,
            GC.GetAllocatedBytesForCurrentThread() - before,
            expected,
            actual,
            "direct writer, warmed shelf");
    }

    /// <summary>
    /// Measures one-thread `SS8-8` inserts through the queued writer concurrency facade.<br/>
    /// This isolates the no-contention overhead of the primitive-domain path and catches silent fallback attribution regressions.<br/>
    /// </summary>
    /// <param name="operations">The number of inserts to perform.<br/></param>
    /// <returns>The measured proof row with readback validation.</returns>
    private static Scalar8Scalar8PrimitiveConcurrencyProofRow RunScalar8Scalar8PrimitiveQueuedSingle(int operations)
    {
        using Scalar8Scalar8Index index = Indexes.SS88.Create(
            backingKind: DataKernelBackingKind.Memory,
            name: "ss88-primitive-queued-single",
            options: CreateDesignPerfOptions(),
            telemetryOptions: DataKernelTelemetryOptions.EnabledOptions);
        Scalar8Scalar8QueuedWriter writer = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);
        _ = writer.InsertEncoded(CreateConcurrencyProofScalar8Key(0, stripe: 0x51), 1, allowDuplicateKeys: true);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 1; i <= operations; i++)
        {
            Scalar8Scalar8EncodedInsertResult result = writer.InsertEncoded(CreateConcurrencyProofScalar8Key(i, stripe: 0x51), (ulong)(i + 1), allowDuplicateKeys: true);
            if (result.Outcome != Scalar8Scalar8EncodedInsertOutcome.Inserted ||
                result.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext)
            {
                throw new InvalidDataException($"SS8-8 primitive queued single expected writer-context path at {i}, got outcome={result.Outcome} path={result.QueuedInsertPath}.");
            }
        }

        watch.Stop();
        int expected = operations + 1;
        int actual = CountScalar8Scalar8(index, 0, ulong.MaxValue, out _);
        return CreateScalar8Scalar8PrimitiveConcurrencyProofRow(
            "queued-1t",
            threads: 1,
            operations,
            watch.Elapsed,
            GC.GetAllocatedBytesForCurrentThread() - before,
            expected,
            actual,
            "queued writer, no contention");
    }

    /// <summary>
    /// Measures one-thread `SS8-8` inserts through a single explicit writer context and one publish.<br/>
    /// This separates writer-context staging and publication cost from the queued writer facade and retry loop.<br/>
    /// </summary>
    /// <param name="operations">The number of inserts to perform.<br/></param>
    /// <returns>The measured proof row with readback validation.</returns>
    private static Scalar8Scalar8PrimitiveConcurrencyProofRow RunScalar8Scalar8PrimitiveWriterContextBatchSingle(int operations)
    {
        using Scalar8Scalar8Index index = Indexes.SS88.Create(
            backingKind: DataKernelBackingKind.Memory,
            name: "ss88-primitive-writer-context-batch-single",
            options: CreateDesignPerfOptions(),
            telemetryOptions: DataKernelTelemetryOptions.EnabledOptions);
        _ = index.InsertEncoded(CreateConcurrencyProofScalar8Key(0, stripe: 0x52), 1, allowDuplicateKeys: true);

        long before = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch watch = Stopwatch.StartNew();
        using (Scalar8Scalar8Writer writer = index.BeginWriter())
        {
            for (int i = 1; i <= operations; i++)
            {
                Scalar8Scalar8EncodedInsertResult result = writer.InsertEncoded(CreateConcurrencyProofScalar8Key(i, stripe: 0x52), (ulong)(i + 1), allowDuplicateKeys: true);
                if (result.Outcome != Scalar8Scalar8EncodedInsertOutcome.Inserted)
                {
                    throw new InvalidDataException($"SS8-8 primitive writer-context batch insert failed at {i}. outcome={result.Outcome}.");
                }
            }

            _ = writer.Publish();
        }

        watch.Stop();
        int expected = operations + 1;
        int actual = CountScalar8Scalar8(index, 0, ulong.MaxValue, out _);
        return CreateScalar8Scalar8PrimitiveConcurrencyProofRow(
            "writer-context-1t",
            threads: 1,
            operations,
            watch.Elapsed,
            GC.GetAllocatedBytesForCurrentThread() - before,
            expected,
            actual,
            "one context, one publish");
    }

    /// <summary>
    /// Measures one-thread generic `SS8-8` inserts through the default public index path.<br/>
    /// This is the developer-facing baseline after generic `Insert` was moved onto the encoded adaptive admission path, where true 1T calls stay direct and only overlapping callers enter queued admission.<br/>
    /// </summary>
    /// <param name="operations">The number of inserts to perform.<br/></param>
    /// <returns>The measured proof row with readback validation.</returns>
    private static Scalar8Scalar8PrimitiveConcurrencyProofRow RunScalar8Scalar8PrimitiveGenericDefaultSingle(int operations)
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });
        using LibraDexIndex<long, long> index = catalog.Indexes["ss88-primitive-generic-default"]["value"].Int64Keys<long>().Create();
        _ = index.Insert(0L, 1L);

        long before = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 1; i <= operations; i++)
        {
            LibraDexGenericInsertResult result = index.Insert(i, i + 1L);
            if (!result.Inserted ||
                result.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.None)
            {
                throw new InvalidDataException($"SS8-8 primitive generic default insert expected direct path at {i}, got inserted={result.Inserted} path={result.QueuedInsertPath}.");
            }
        }

        watch.Stop();
        long expected = operations + 1L;
        long actual = CountGenericLongLong(index, out _);
        return CreateScalar8Scalar8PrimitiveConcurrencyProofRow(
            "generic-default-1t",
            threads: 1,
            operations,
            watch.Elapsed,
            GC.GetAllocatedBytesForCurrentThread() - before,
            expected,
            actual,
            "public generic Insert, no overlap");
    }

    /// <summary>
    /// Measures one-thread generic `SS8-8` inserts through an explicit queued writer.<br/>
    /// This keeps the public opt-in concurrent facade cost visible next to the default generic single-owner path.<br/>
    /// </summary>
    /// <param name="operations">The number of inserts to perform.<br/></param>
    /// <returns>The measured proof row with readback validation.</returns>
    private static Scalar8Scalar8PrimitiveConcurrencyProofRow RunScalar8Scalar8PrimitiveGenericQueuedSingle(int operations)
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });
        using LibraDexIndex<long, long> index = catalog.Indexes["ss88-primitive-generic-queued"]["value"].Int64Keys<long>().Create();
        LibraDexQueuedWriter<long, long> writer = index.BeginConcurrentWriter(LibraDexConcurrencyOptions.QueuedWriter);
        _ = writer.Insert(0L, 1L);

        long before = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 1; i <= operations; i++)
        {
            LibraDexGenericInsertResult result = writer.Insert(i, i + 1L);
            if (!result.Inserted ||
                result.QueuedInsertPath == Scalar8Scalar8QueuedInsertPath.None)
            {
                throw new InvalidDataException($"SS8-8 primitive generic queued insert expected queued admission at {i}, got inserted={result.Inserted} path={result.QueuedInsertPath}.");
            }
        }

        watch.Stop();
        long expected = operations + 1L;
        long actual = CountGenericLongLong(index, out _);
        return CreateScalar8Scalar8PrimitiveConcurrencyProofRow(
            "generic-queued-1t",
            threads: 1,
            operations,
            watch.Elapsed,
            GC.GetAllocatedBytesForCurrentThread() - before,
            expected,
            actual,
            "explicit generic queued writer");
    }

    /// <summary>
    /// Measures one-thread generic `SS8-8` inserts through the explicit concurrent batch facade.<br/>
    /// The row isolates public batch overhead from per-call queued publication overhead by publishing once after all measured inserts.<br/>
    /// </summary>
    /// <param name="operations">The number of inserts to perform.<br/></param>
    /// <returns>The measured proof row with readback validation.</returns>
    private static Scalar8Scalar8PrimitiveConcurrencyProofRow RunScalar8Scalar8PrimitiveGenericConcurrentBatchSingle(int operations)
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });
        using LibraDexIndex<long, long> index = catalog.Indexes["ss88-primitive-generic-concurrent-batch"]["value"].Int64Keys<long>().Create();
        for (int i = 0; i < operations; i++)
        {
            long seedKey = CreateConcurrencyProofGenericLongKey(i * 2, 0x20 + (i & 0x7F));
            _ = index.Insert(seedKey, seedKey);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch watch = Stopwatch.StartNew();
        using (LibraDexConcurrentBatch<long, long> batch = index.BeginConcurrentBatch())
        {
            for (int i = 0; i < operations; i++)
            {
                long key = CreateConcurrencyProofGenericLongKey((i * 2) + 1, 0x20 + (i & 0x7F));
                LibraDexGenericInsertResult result = batch.Insert(key, key);
                if (!result.Inserted ||
                    result.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext)
                {
                    throw new InvalidDataException($"SS8-8 primitive generic concurrent batch insert expected staged writer context at {i}, got inserted={result.Inserted} path={result.QueuedInsertPath}.");
                }
            }

            LibraDexConcurrentBatchPublishResult publish = batch.Publish();
            if (publish.InsertedCount != operations ||
                publish.PublishedContextCount != 1)
            {
                throw new InvalidDataException($"SS8-8 primitive generic concurrent batch publish expected {operations} inserts in one context, got inserts={publish.InsertedCount} contexts={publish.PublishedContextCount}.");
            }
        }

        watch.Stop();
        long expected = operations * 2L;
        long actual = CountGenericLongLong(index, out _);
        return CreateScalar8Scalar8PrimitiveConcurrencyProofRow(
            "generic-batch-1t",
            threads: 1,
            operations,
            watch.Elapsed,
            GC.GetAllocatedBytesForCurrentThread() - before,
            expected,
            actual,
            "public concurrent batch, one publish");
    }

    /// <summary>
    /// Measures multi-thread `SS8-8` queued inserts where every worker owns a warmed independent shelf.<br/>
    /// This is the intended fast-path concurrency shape: all workers may stage independently and only meet at short physical publication points.<br/>
    /// </summary>
    /// <param name="threads">The number of concurrent workers.<br/></param>
    /// <param name="opsPerThread">The inserts each worker should perform.<br/></param>
    /// <returns>The measured proof row with final count validation.</returns>
    private static Scalar8Scalar8PrimitiveConcurrencyProofRow RunScalar8Scalar8PrimitiveDifferentShelfMulti(int threads, int opsPerThread)
    {
        using Scalar8Scalar8Index index = Indexes.SS88.Create(
            backingKind: DataKernelBackingKind.Memory,
            name: "ss88-primitive-different-shelf-multi",
            options: CreateDesignPerfOptions(),
            telemetryOptions: DataKernelTelemetryOptions.EnabledOptions);
        Scalar8Scalar8QueuedWriter writer = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);
        for (int worker = 0; worker < threads; worker++)
        {
            _ = writer.InsertEncoded(CreateConcurrencyProofScalar8Key(worker, worker + 1), (ulong)(worker + 1), allowDuplicateKeys: true);
        }

        using ManualResetEventSlim startGate = new(false);
        using CountdownEvent readyGate = new(threads);
        Task[] tasks = new Task[threads];
        long before = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch watch = Stopwatch.StartNew();
        for (int worker = 0; worker < threads; worker++)
        {
            int workerOrdinal = worker;
            tasks[worker] = Task.Run(() =>
            {
                readyGate.Signal();
                startGate.Wait();
                for (int i = 0; i < opsPerThread; i++)
                {
                    int sequence = workerOrdinal * opsPerThread + i + threads;
                    Scalar8Scalar8EncodedInsertResult result = writer.InsertEncoded(CreateConcurrencyProofScalar8Key(sequence, workerOrdinal + 1), (ulong)(sequence + 1), allowDuplicateKeys: true);
                    if (result.Outcome != Scalar8Scalar8EncodedInsertOutcome.Inserted ||
                        result.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext)
                    {
                        throw new InvalidDataException($"SS8-8 primitive different-shelf multi expected writer-context path. worker={workerOrdinal} i={i} outcome={result.Outcome} path={result.QueuedInsertPath}.");
                    }
                }
            });
        }

        if (!readyGate.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidDataException("SS8-8 primitive different-shelf multi could not ready all workers.");
        }

        startGate.Set();
        Task.WaitAll(tasks);
        watch.Stop();
        int expected = checked(threads + (threads * opsPerThread));
        int actual = CountScalar8Scalar8(index, 0, ulong.MaxValue, out _);
        return CreateScalar8Scalar8PrimitiveConcurrencyProofRow(
            "mt-different-shelf",
            threads,
            threads * opsPerThread,
            watch.Elapsed,
            GC.GetTotalAllocatedBytes(precise: true) - before,
            expected,
            actual,
            "independent warmed shelves");
    }

    /// <summary>
    /// Measures multi-thread `SS8-8` queued inserts where all workers contend for the same warmed shelf.<br/>
    /// This row is expected to serialize at the primitive shelf domain and acts as a contrast against the different-shelf row.<br/>
    /// </summary>
    /// <param name="threads">The number of concurrent workers.<br/></param>
    /// <param name="opsPerThread">The inserts each worker should perform.<br/></param>
    /// <returns>The measured proof row with final count validation.</returns>
    private static Scalar8Scalar8PrimitiveConcurrencyProofRow RunScalar8Scalar8PrimitiveSameShelfMulti(int threads, int opsPerThread)
    {
        using Scalar8Scalar8Index index = Indexes.SS88.Create(
            backingKind: DataKernelBackingKind.Memory,
            name: "ss88-primitive-same-shelf-multi",
            options: CreateDesignPerfOptions(),
            telemetryOptions: DataKernelTelemetryOptions.EnabledOptions);
        Scalar8Scalar8QueuedWriter writer = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);
        _ = writer.InsertEncoded(CreateConcurrencyProofScalar8Key(0, stripe: 0x70), 1, allowDuplicateKeys: true);

        using ManualResetEventSlim startGate = new(false);
        using CountdownEvent readyGate = new(threads);
        Task[] tasks = new Task[threads];
        long before = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch watch = Stopwatch.StartNew();
        for (int worker = 0; worker < threads; worker++)
        {
            int workerOrdinal = worker;
            tasks[worker] = Task.Run(() =>
            {
                readyGate.Signal();
                startGate.Wait();
                for (int i = 0; i < opsPerThread; i++)
                {
                    int sequence = workerOrdinal * opsPerThread + i + 1;
                    Scalar8Scalar8EncodedInsertResult result = writer.InsertEncoded(CreateConcurrencyProofScalar8Key(sequence, stripe: 0x70), (ulong)(sequence + 1), allowDuplicateKeys: true);
                    if (result.Outcome != Scalar8Scalar8EncodedInsertOutcome.Inserted)
                    {
                        throw new InvalidDataException($"SS8-8 primitive same-shelf multi insert failed. worker={workerOrdinal} i={i} outcome={result.Outcome}.");
                    }
                }
            });
        }

        if (!readyGate.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidDataException("SS8-8 primitive same-shelf multi could not ready all workers.");
        }

        startGate.Set();
        Task.WaitAll(tasks);
        watch.Stop();
        int expected = checked(1 + (threads * opsPerThread));
        int actual = CountScalar8Scalar8(index, 0, ulong.MaxValue, out _);
        return CreateScalar8Scalar8PrimitiveConcurrencyProofRow(
            "mt-same-shelf",
            threads,
            threads * opsPerThread,
            watch.Elapsed,
            GC.GetTotalAllocatedBytes(precise: true) - before,
            expected,
            actual,
            "intentional same-shelf contention");
    }

    /// <summary>
    /// Measures multi-thread `SS8-8` queued inserts over independent shelves with total operations equal to the one-thread rows.<br/>
    /// This keeps the elapsed-time comparison honest by holding measured operation count constant across direct, queued, and MT rows.<br/>
    /// </summary>
    /// <param name="threads">The number of concurrent workers.<br/></param>
    /// <param name="totalOperations">The total measured inserts to distribute across workers.<br/></param>
    /// <returns>The measured proof row with final count validation.</returns>
    private static Scalar8Scalar8PrimitiveConcurrencyProofRow RunScalar8Scalar8PrimitiveDifferentShelfEqualOpsMulti(int threads, int totalOperations)
    {
        int opsPerThread = Math.Max(1, totalOperations / threads);
        return RunScalar8Scalar8PrimitiveEqualOpsMulti(
            scenario: "mt-different-equal",
            threads,
            opsPerThread,
            sameShelf: false,
            stripeBase: 0x80,
            note: "independent shelves, equal total ops");
    }

    /// <summary>
    /// Measures multi-thread `SS8-8` queued inserts into one shared shelf with total operations equal to the one-thread rows.<br/>
    /// This is the contention contrast for the equal-operation different-shelf row.<br/>
    /// </summary>
    /// <param name="threads">The number of concurrent workers.<br/></param>
    /// <param name="totalOperations">The total measured inserts to distribute across workers.<br/></param>
    /// <returns>The measured proof row with final count validation.</returns>
    private static Scalar8Scalar8PrimitiveConcurrencyProofRow RunScalar8Scalar8PrimitiveSameShelfEqualOpsMulti(int threads, int totalOperations)
    {
        int opsPerThread = Math.Max(1, totalOperations / threads);
        return RunScalar8Scalar8PrimitiveEqualOpsMulti(
            scenario: "mt-same-equal",
            threads,
            opsPerThread,
            sameShelf: true,
            stripeBase: 0x90,
            note: "same shelf, equal total ops");
    }

    /// <summary>
    /// Runs an equal-operation multi-thread `SS8-8` insert benchmark for either independent shelves or one shared shelf.<br/>
    /// The helper keeps task-gate, validation, and reporting behavior identical for the two rows so the lock-domain contrast is the changing variable.<br/>
    /// </summary>
    /// <param name="scenario">The scenario label to print.<br/></param>
    /// <param name="threads">The number of concurrent workers.<br/></param>
    /// <param name="opsPerThread">The inserts each worker should perform.<br/></param>
    /// <param name="sameShelf">True to force every worker into one shelf, false to give each worker an independent warmed shelf.<br/></param>
    /// <param name="stripeBase">The root-prefix stripe base for deterministic key construction.<br/></param>
    /// <param name="note">A short scenario note.<br/></param>
    /// <returns>The measured proof row with final count validation.</returns>
    private static Scalar8Scalar8PrimitiveConcurrencyProofRow RunScalar8Scalar8PrimitiveEqualOpsMulti(
        string scenario,
        int threads,
        int opsPerThread,
        bool sameShelf,
        int stripeBase,
        string note)
    {
        using Scalar8Scalar8Index index = Indexes.SS88.Create(
            backingKind: DataKernelBackingKind.Memory,
            name: $"ss88-primitive-{scenario}",
            options: CreateDesignPerfOptions(),
            telemetryOptions: DataKernelTelemetryOptions.EnabledOptions);
        Scalar8Scalar8QueuedWriter writer = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);
        for (int worker = 0; worker < threads; worker++)
        {
            int stripe = sameShelf ? stripeBase : stripeBase + worker;
            _ = writer.InsertEncoded(CreateConcurrencyProofScalar8Key(worker, stripe), (ulong)(worker + 1), allowDuplicateKeys: true);
        }

        using ManualResetEventSlim startGate = new(false);
        using CountdownEvent readyGate = new(threads);
        Task[] tasks = new Task[threads];
        long before = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch watch = Stopwatch.StartNew();
        for (int worker = 0; worker < threads; worker++)
        {
            int workerOrdinal = worker;
            tasks[worker] = Task.Run(() =>
            {
                readyGate.Signal();
                startGate.Wait();
                int stripe = sameShelf ? stripeBase : stripeBase + workerOrdinal;
                for (int i = 0; i < opsPerThread; i++)
                {
                    int sequence = workerOrdinal * opsPerThread + i + threads;
                    Scalar8Scalar8EncodedInsertResult result = writer.InsertEncoded(CreateConcurrencyProofScalar8Key(sequence, stripe), (ulong)(sequence + 1), allowDuplicateKeys: true);
                    if (result.Outcome != Scalar8Scalar8EncodedInsertOutcome.Inserted)
                    {
                        throw new InvalidDataException($"SS8-8 primitive {scenario} insert failed. worker={workerOrdinal} i={i} outcome={result.Outcome}.");
                    }
                }
            });
        }

        if (!readyGate.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidDataException($"SS8-8 primitive {scenario} could not ready all workers.");
        }

        startGate.Set();
        Task.WaitAll(tasks);
        watch.Stop();
        int expected = checked(threads + (threads * opsPerThread));
        int actual = CountScalar8Scalar8(index, 0, ulong.MaxValue, out _);
        return CreateScalar8Scalar8PrimitiveConcurrencyProofRow(
            scenario,
            threads,
            threads * opsPerThread,
            watch.Elapsed,
            GC.GetTotalAllocatedBytes(precise: true) - before,
            expected,
            actual,
            note);
    }

    /// <summary>
    /// Measures concurrent `SS8-8` delete and rekey operations over disjoint warmed shelves.<br/>
    /// Rekey is treated as the identity-index operation it is: replacement insert first, then old tuple deletion, with each leg using its own primitive domains.<br/>
    /// </summary>
    /// <param name="threads">The number of concurrent workers.<br/></param>
    /// <param name="opsPerThread">The delete/rekey pairs each worker should perform.<br/></param>
    /// <returns>The measured proof row with final count validation.</returns>
    private static Scalar8Scalar8PrimitiveConcurrencyProofRow RunScalar8Scalar8PrimitiveDeleteRekeyMulti(int threads, int opsPerThread)
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });
        using LibraDexIndex<long, long> index = catalog.Indexes["ss88-primitive-delete-rekey"]["value"].Int64Keys<long>().Create();
        LibraDexQueuedWriter<long, long> writer = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);
        int seedPerThread = checked(opsPerThread * 2);
        for (int worker = 0; worker < threads; worker++)
        {
            long keyBase = checked((long)(worker + 1) * 1_000_000L);
            for (int i = 0; i < seedPerThread; i++)
            {
                long key = keyBase + i;
                LibraDexGenericInsertResult insert = writer.Insert(key, key);
                if (!insert.Inserted)
                {
                    throw new InvalidDataException($"SS8-8 primitive delete/rekey seed insert failed. worker={worker} i={i}.");
                }
            }
        }

        using ManualResetEventSlim startGate = new(false);
        using CountdownEvent readyGate = new(threads);
        Task[] tasks = new Task[threads];
        long before = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch watch = Stopwatch.StartNew();
        for (int worker = 0; worker < threads; worker++)
        {
            int workerOrdinal = worker;
            tasks[worker] = Task.Run(() =>
            {
                readyGate.Signal();
                startGate.Wait();
                long keyBase = checked((long)(workerOrdinal + 1) * 1_000_000L);
                for (int i = 0; i < opsPerThread; i++)
                {
                    long deleteKey = keyBase + i;
                    LibraDexGenericDeleteResult delete = writer.Delete(deleteKey, deleteKey);
                    if (!delete.Deleted)
                    {
                        throw new InvalidDataException($"SS8-8 primitive delete/rekey delete failed. worker={workerOrdinal} i={i}.");
                    }

                    long oldKey = keyBase + opsPerThread + i;
                    long newKey = keyBase + 500_000L + i;
                    LibraDexGenericRekeyResult rekey = writer.Rekey(oldKey, oldKey, newKey);
                    if (!rekey.Changed)
                    {
                        throw new InvalidDataException($"SS8-8 primitive delete/rekey rekey failed. worker={workerOrdinal} i={i}.");
                    }
                }
            });
        }

        if (!readyGate.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidDataException("SS8-8 primitive delete/rekey multi could not ready all workers.");
        }

        startGate.Set();
        Task.WaitAll(tasks);
        watch.Stop();
        long expected = checked((long)threads * (seedPerThread - opsPerThread));
        long actual = CountGenericLongLong(index, out _);
        return CreateScalar8Scalar8PrimitiveConcurrencyProofRow(
            "mt-delete-rekey",
            threads,
            threads * opsPerThread * 2,
            watch.Elapsed,
            GC.GetTotalAllocatedBytes(precise: true) - before,
            expected,
            actual,
            "disjoint identity-index delete plus rekey");
    }

    /// <summary>
    /// Measures multi-thread generic `SS8-8` inserts where each worker owns an independent warmed shelf and publishes one batch context.<br/>
    /// This is the public facade proof for LibraDex's physical-library concurrency model: independent shelves should not funnel into per-call publication.<br/>
    /// </summary>
    /// <param name="threads">The number of concurrent workers.<br/></param>
    /// <param name="opsPerThread">The inserts each worker should perform.<br/></param>
    /// <returns>The measured proof row with final count validation.</returns>
    private static Scalar8Scalar8PrimitiveConcurrencyProofRow RunScalar8Scalar8PrimitiveGenericConcurrentBatchDifferentShelfMulti(int threads, int opsPerThread)
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });
        using LibraDexIndex<long, long> index = catalog.Indexes["ss88-primitive-generic-batch-mt"]["value"].Int64Keys<long>().Create();
        for (int worker = 0; worker < threads; worker++)
        {
            long key = CreateConcurrencyProofGenericLongKey(0, 0x30 + worker);
            LibraDexGenericInsertResult seed = index.Insert(key, key);
            if (!seed.Inserted)
            {
                throw new InvalidDataException($"SS8-8 primitive generic batch MT seed insert failed. worker={worker}.");
            }
        }

        using ManualResetEventSlim startGate = new(false);
        using CountdownEvent readyGate = new(threads);
        Task[] tasks = new Task[threads];
        long before = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch watch = Stopwatch.StartNew();
        for (int worker = 0; worker < threads; worker++)
        {
            int workerOrdinal = worker;
            tasks[worker] = Task.Run(() =>
            {
                readyGate.Signal();
                startGate.Wait();
                using LibraDexConcurrentBatch<long, long> batch = index.BeginConcurrentBatch();
                for (int i = 1; i <= opsPerThread; i++)
                {
                    long key = CreateConcurrencyProofGenericLongKey(i, 0x30 + workerOrdinal);
                    LibraDexGenericInsertResult insert = batch.Insert(key, key);
                    if (!insert.Inserted)
                    {
                        throw new InvalidDataException($"SS8-8 primitive generic batch MT insert failed. worker={workerOrdinal} i={i}.");
                    }
                }

                LibraDexConcurrentBatchPublishResult publish = batch.Publish();
                if (publish.InsertedCount != opsPerThread)
                {
                    throw new InvalidDataException($"SS8-8 primitive generic batch MT publish expected {opsPerThread} inserts, got {publish.InsertedCount}. worker={workerOrdinal}.");
                }
            });
        }

        if (!readyGate.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidDataException("SS8-8 primitive generic batch MT could not ready all workers.");
        }

        startGate.Set();
        Task.WaitAll(tasks);
        watch.Stop();
        long expected = checked(threads + ((long)threads * opsPerThread));
        long actual = CountGenericLongLong(index, out _);
        return CreateScalar8Scalar8PrimitiveConcurrencyProofRow(
            "mt-generic-batch",
            threads,
            threads * opsPerThread,
            watch.Elapsed,
            GC.GetTotalAllocatedBytes(precise: true) - before,
            expected,
            actual,
            "public concurrent batch, independent shelves");
    }

    /// <summary>
    /// Measures multi-thread generic `SS8-8` exact deletes and rekeys through one concurrent batch per worker.<br/>
    /// Each worker mutates disjoint warmed shelves, proving mutation can stay shelf-local until one context publication per worker.<br/>
    /// </summary>
    /// <param name="threads">The number of concurrent workers.<br/></param>
    /// <param name="opsPerThread">The delete/rekey pairs each worker should perform.<br/></param>
    /// <returns>The measured proof row with final count validation.</returns>
    private static Scalar8Scalar8PrimitiveConcurrencyProofRow RunScalar8Scalar8PrimitiveGenericConcurrentBatchDeleteRekeyMulti(int threads, int opsPerThread)
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });
        using LibraDexIndex<long, long> index = catalog.Indexes["ss88-primitive-generic-batch-mut"]["value"].Int64Keys<long>().Create();
        using (LibraDexConcurrentBatch<long, long> missingOldBatch = index.BeginConcurrentBatch())
        {
            long missingOldKey = CreateConcurrencyProofGenericLongKey(900_000, 0x4F);
            long missingNewKey = CreateConcurrencyProofGenericLongKey(900_001, 0x4F);
            LibraDexGenericRekeyResult missingOldRekey = missingOldBatch.Rekey(missingOldKey, missingOldKey, missingNewKey);
            LibraDexConcurrentBatchPublishResult missingOldPublish = missingOldBatch.Publish();
            if (missingOldRekey.Changed ||
                missingOldPublish.InsertedCount != 0 ||
                missingOldPublish.DeletedCount != 0)
            {
                throw new InvalidDataException("SS8-8 primitive generic batch mutate missing-old rekey unexpectedly changed the index.");
            }
        }

        int seedPerThread = checked(opsPerThread * 2);
        for (int worker = 0; worker < threads; worker++)
        {
            for (int i = 0; i < seedPerThread; i++)
            {
                long key = CreateConcurrencyProofGenericLongKey(i, 0x40 + worker);
                LibraDexGenericInsertResult insert = index.Insert(key, key);
                if (!insert.Inserted)
                {
                    throw new InvalidDataException($"SS8-8 primitive generic batch mutate seed insert failed. worker={worker} i={i}.");
                }
            }
        }

        using ManualResetEventSlim startGate = new(false);
        using CountdownEvent readyGate = new(threads);
        Task[] tasks = new Task[threads];
        long before = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch watch = Stopwatch.StartNew();
        for (int worker = 0; worker < threads; worker++)
        {
            int workerOrdinal = worker;
            tasks[worker] = Task.Run(() =>
            {
                readyGate.Signal();
                startGate.Wait();
                using LibraDexConcurrentBatch<long, long> batch = index.BeginConcurrentBatch();
                for (int i = 0; i < opsPerThread; i++)
                {
                    long deleteKey = CreateConcurrencyProofGenericLongKey(i, 0x40 + workerOrdinal);
                    LibraDexGenericDeleteResult delete = batch.Delete(deleteKey, deleteKey);
                    if (!delete.Deleted)
                    {
                        throw new InvalidDataException($"SS8-8 primitive generic batch mutate delete failed. worker={workerOrdinal} i={i}.");
                    }

                    long oldKey = CreateConcurrencyProofGenericLongKey(opsPerThread + i, 0x40 + workerOrdinal);
                    long newKey = CreateConcurrencyProofGenericLongKey(500_000 + i, 0x40 + workerOrdinal);
                    LibraDexGenericRekeyResult rekey = batch.Rekey(oldKey, oldKey, newKey);
                    if (!rekey.Changed)
                    {
                        throw new InvalidDataException($"SS8-8 primitive generic batch mutate rekey failed. worker={workerOrdinal} i={i}.");
                    }
                }

                LibraDexConcurrentBatchPublishResult publish = batch.Publish();
                if (publish.DeletedCount != opsPerThread * 2L ||
                    publish.ChangedRekeyCount != opsPerThread)
                {
                    throw new InvalidDataException($"SS8-8 primitive generic batch mutate publish mismatch. worker={workerOrdinal} deletes={publish.DeletedCount} rekeys={publish.ChangedRekeyCount}.");
                }
            });
        }

        if (!readyGate.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidDataException("SS8-8 primitive generic batch mutate could not ready all workers.");
        }

        startGate.Set();
        Task.WaitAll(tasks);
        watch.Stop();
        long expected = checked((long)threads * (seedPerThread - opsPerThread));
        long actual = CountGenericLongLong(index, out _);
        return CreateScalar8Scalar8PrimitiveConcurrencyProofRow(
            "mt-generic-batch-mut",
            threads,
            threads * opsPerThread * 2,
            watch.Elapsed,
            GC.GetTotalAllocatedBytes(precise: true) - before,
            expected,
            actual,
            "public concurrent batch delete plus rekey");
    }

    /// <summary>
    /// Measures one-thread `FS32-8` inserts with a maintained exact reversed projection through ordinary public insert.<br/>
    /// This is the projection baseline where primary and subindex maintenance happen through immediate per-call publication.<br/>
    /// </summary>
    /// <param name="operations">The number of inserts to perform.<br/></param>
    /// <returns>The measured proof row with primary and projection validation.</returns>
    private static Scalar8Scalar8PrimitiveConcurrencyProofRow RunFixed32Scalar8ProjectionGenericDefaultSingle(int operations)
    {
        operations = Math.Min(operations, 64);
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });
        using LibraDexIndex<byte[], long> index = CreateFixed32Scalar8ProjectionIndex(catalog, "fs32-proj-default");
        _ = index.Insert(CreateConcurrencyProofFixed32Key(0, 0x50, 0x60), 1L);

        long before = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 1; i <= operations; i++)
        {
            LibraDexGenericInsertResult result = index.Insert(CreateConcurrencyProofFixed32Key(i, 0x50, 0x60), i + 1L);
            if (!result.Inserted)
            {
                throw new InvalidDataException($"FS32-8 projection default insert failed at {i}.");
            }
        }

        watch.Stop();
        long expected = operations + 1L;
        long actual = expected;
        ValidateFixed32ProjectionSuffixCount(catalog, "fs32-proj-default", 0x60, expected);
        return CreateScalar8Scalar8PrimitiveConcurrencyProofRow(
            "fs32-proj-default-1t",
            threads: 1,
            operations,
            watch.Elapsed,
            GC.GetAllocatedBytesForCurrentThread() - before,
            expected,
            actual,
            "FS32-8 primary plus exact reversed subindex");
    }

    /// <summary>
    /// Measures one-thread `FS32-8` inserts with a maintained exact reversed projection through the explicit queued writer.<br/>
    /// This keeps the old public concurrent facade cost visible for projection-enabled writes.<br/>
    /// </summary>
    /// <param name="operations">The number of inserts to perform.<br/></param>
    /// <returns>The measured proof row with primary and projection validation.</returns>
    private static Scalar8Scalar8PrimitiveConcurrencyProofRow RunFixed32Scalar8ProjectionGenericQueuedSingle(int operations)
    {
        operations = Math.Min(operations, 64);
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });
        using LibraDexIndex<byte[], long> index = CreateFixed32Scalar8ProjectionIndex(catalog, "fs32-proj-queued");
        LibraDexQueuedWriter<byte[], long> writer = index.BeginConcurrentWriter(LibraDexConcurrencyOptions.QueuedWriter);
        _ = writer.Insert(CreateConcurrencyProofFixed32Key(0, 0x51, 0x61), 1L);

        long before = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 1; i <= operations; i++)
        {
            LibraDexGenericInsertResult result = writer.Insert(CreateConcurrencyProofFixed32Key(i, 0x51, 0x61), i + 1L);
            if (!result.Inserted)
            {
                throw new InvalidDataException($"FS32-8 projection queued insert failed at {i}.");
            }
        }

        watch.Stop();
        long expected = operations + 1L;
        long actual = expected;
        ValidateFixed32ProjectionSuffixCount(catalog, "fs32-proj-queued", 0x61, expected);
        return CreateScalar8Scalar8PrimitiveConcurrencyProofRow(
            "fs32-proj-queued-1t",
            threads: 1,
            operations,
            watch.Elapsed,
            GC.GetAllocatedBytesForCurrentThread() - before,
            expected,
            actual,
            "queued FS32-8 primary plus exact reversed subindex");
    }

    /// <summary>
    /// Measures one-thread `FS32-8` inserts with a maintained exact reversed projection through the shared-context concurrent batch.<br/>
    /// Primary and subindex shelves are staged in one `FS32-8` writer context and published once when all routes remain shelf-local.<br/>
    /// </summary>
    /// <param name="operations">The number of inserts to perform.<br/></param>
    /// <returns>The measured proof row with primary and projection validation.</returns>
    private static Scalar8Scalar8PrimitiveConcurrencyProofRow RunFixed32Scalar8ProjectionGenericConcurrentBatchSingle(int operations)
    {
        operations = Math.Min(operations, 64);
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });
        using LibraDexIndex<byte[], long> index = CreateFixed32Scalar8ProjectionIndex(catalog, "fs32-proj-batch");
        for (int i = 0; i < operations; i++)
        {
            byte[] seedKey = CreateConcurrencyProofFixed32Key(i * 2, 0x52, 0x62);
            _ = index.Insert(seedKey, i + 1L);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch watch = Stopwatch.StartNew();
        using (LibraDexConcurrentBatch<byte[], long> batch = index.BeginConcurrentBatch())
        {
            for (int i = 0; i < operations; i++)
            {
                byte[] key = CreateConcurrencyProofFixed32Key((i * 2) + 1, 0x52, 0x62);
                LibraDexGenericInsertResult result = batch.Insert(key, operations + i + 1L);
                if (!result.Inserted ||
                    result.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext)
                {
                    throw new InvalidDataException($"FS32-8 projection batch insert expected writer context at {i}, got inserted={result.Inserted} path={result.QueuedInsertPath}.");
                }
            }

            LibraDexConcurrentBatchPublishResult publish = batch.Publish();
            if (publish.InsertedCount != operations ||
                publish.PublishedContextCount != 1)
            {
                throw new InvalidDataException($"FS32-8 projection batch publish expected {operations} inserts in one context, got inserts={publish.InsertedCount} contexts={publish.PublishedContextCount}.");
            }
        }

        watch.Stop();
        long expected = operations * 2L;
        long actual = expected;
        ValidateFixed32ProjectionSuffixCount(catalog, "fs32-proj-batch", 0x62, expected);
        return CreateScalar8Scalar8PrimitiveConcurrencyProofRow(
            "fs32-proj-batch-1t",
            threads: 1,
            operations,
            watch.Elapsed,
            GC.GetAllocatedBytesForCurrentThread() - before,
            expected,
            actual,
            "shared-context FS32-8 primary plus exact reversed subindex");
    }

    /// <summary>
    /// Measures multi-thread `FS32-8` projected inserts where every worker owns independent primary and reversed-projection shelves.<br/>
    /// This proves maintained subindexes can preserve physical-shelf concurrency instead of funneling all logical writes through projection publication.<br/>
    /// </summary>
    /// <param name="threads">The number of concurrent workers.<br/></param>
    /// <param name="opsPerThread">The inserts each worker should perform.<br/></param>
    /// <returns>The measured proof row with primary and projection validation.</returns>
    private static Scalar8Scalar8PrimitiveConcurrencyProofRow RunFixed32Scalar8ProjectionGenericConcurrentBatchDifferentShelfMulti(int threads, int opsPerThread)
    {
        opsPerThread = Math.Min(opsPerThread, 64);
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });
        using LibraDexIndex<byte[], long> index = CreateFixed32Scalar8ProjectionIndex(catalog, "fs32-proj-batch-mt");
        for (int worker = 0; worker < threads; worker++)
        {
            byte[] key = CreateConcurrencyProofFixed32Key(0, 0x70 + worker, 0x80 + worker);
            _ = index.Insert(key, worker + 1L);
        }

        using ManualResetEventSlim startGate = new(false);
        using CountdownEvent readyGate = new(threads);
        Task[] tasks = new Task[threads];
        long before = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch watch = Stopwatch.StartNew();
        for (int worker = 0; worker < threads; worker++)
        {
            int workerOrdinal = worker;
            tasks[worker] = Task.Run(() =>
            {
                readyGate.Signal();
                startGate.Wait();
                using LibraDexConcurrentBatch<byte[], long> batch = index.BeginConcurrentBatch();
                for (int i = 1; i <= opsPerThread; i++)
                {
                    byte[] key = CreateConcurrencyProofFixed32Key(i, 0x70 + workerOrdinal, 0x80 + workerOrdinal);
                    LibraDexGenericInsertResult insert = batch.Insert(key, checked((workerOrdinal + 1L) * 1_000_000L + i));
                    if (!insert.Inserted)
                    {
                        throw new InvalidDataException($"FS32-8 projection batch MT insert failed. worker={workerOrdinal} i={i}.");
                    }
                }

                LibraDexConcurrentBatchPublishResult publish = batch.Publish();
                if (publish.InsertedCount != opsPerThread)
                {
                    throw new InvalidDataException($"FS32-8 projection batch MT publish expected {opsPerThread} inserts, got {publish.InsertedCount}. worker={workerOrdinal}.");
                }
            });
        }

        if (!readyGate.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidDataException("FS32-8 projection batch MT could not ready all workers.");
        }

        startGate.Set();
        Task.WaitAll(tasks);
        watch.Stop();
        long expected = checked(threads + ((long)threads * opsPerThread));
        long actual = expected;
        for (int worker = 0; worker < threads; worker++)
        {
            ValidateFixed32ProjectionSuffixCount(catalog, "fs32-proj-batch-mt", 0x80 + worker, opsPerThread + 1L);
        }

        return CreateScalar8Scalar8PrimitiveConcurrencyProofRow(
            "mt-fs32-proj-batch",
            threads,
            threads * opsPerThread,
            watch.Elapsed,
            GC.GetTotalAllocatedBytes(precise: true) - before,
            expected,
            actual,
            "shared-context projected FS32-8 independent shelves");
    }

    /// <summary>
    /// Measures multi-thread `FS32-8` projected deletes and rekeys through one shared-context concurrent batch per worker.<br/>
    /// The proof validates that maintained reversed projections are removed and inserted with the primary mutation set.<br/>
    /// </summary>
    /// <param name="threads">The number of concurrent workers.<br/></param>
    /// <param name="opsPerThread">The delete/rekey pairs each worker should perform.<br/></param>
    /// <returns>The measured proof row with primary and projection validation.</returns>
    private static Scalar8Scalar8PrimitiveConcurrencyProofRow RunFixed32Scalar8ProjectionGenericConcurrentBatchDeleteRekeyMulti(int threads, int opsPerThread)
    {
        opsPerThread = Math.Min(opsPerThread, 32);
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });
        using LibraDexIndex<byte[], long> index = CreateFixed32Scalar8ProjectionIndex(catalog, "fs32-proj-batch-mut");
        int seedPerThread = checked(opsPerThread * 2);
        for (int worker = 0; worker < threads; worker++)
        {
            for (int i = 0; i < seedPerThread; i++)
            {
                byte[] key = CreateConcurrencyProofFixed32Key(i, 0x90 + worker, 0xA0 + worker);
                long identity = checked((worker + 1L) * 1_000_000L + i);
                LibraDexGenericInsertResult insert = index.Insert(key, identity);
                if (!insert.Inserted)
                {
                    throw new InvalidDataException($"FS32-8 projection batch mutate seed insert failed. worker={worker} i={i}.");
                }
            }
        }

        using ManualResetEventSlim startGate = new(false);
        using CountdownEvent readyGate = new(threads);
        Task[] tasks = new Task[threads];
        long before = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch watch = Stopwatch.StartNew();
        for (int worker = 0; worker < threads; worker++)
        {
            int workerOrdinal = worker;
            tasks[worker] = Task.Run(() =>
            {
                readyGate.Signal();
                startGate.Wait();
                using LibraDexConcurrentBatch<byte[], long> batch = index.BeginConcurrentBatch();
                for (int i = 0; i < opsPerThread; i++)
                {
                    long deleteIdentity = checked((workerOrdinal + 1L) * 1_000_000L + i);
                    byte[] deleteKey = CreateConcurrencyProofFixed32Key(i, 0x90 + workerOrdinal, 0xA0 + workerOrdinal);
                    LibraDexGenericDeleteResult delete = batch.Delete(deleteKey, deleteIdentity);
                    if (!delete.Deleted)
                    {
                        throw new InvalidDataException($"FS32-8 projection batch mutate delete failed. worker={workerOrdinal} i={i}.");
                    }

                    long rekeyIdentity = checked((workerOrdinal + 1L) * 1_000_000L + opsPerThread + i);
                    byte[] oldKey = CreateConcurrencyProofFixed32Key(opsPerThread + i, 0x90 + workerOrdinal, 0xA0 + workerOrdinal);
                    byte[] newKey = CreateConcurrencyProofFixed32Key(500_000 + i, 0x90 + workerOrdinal, 0xA0 + workerOrdinal);
                    LibraDexGenericRekeyResult rekey = batch.Rekey(rekeyIdentity, oldKey, newKey);
                    if (!rekey.Changed)
                    {
                        throw new InvalidDataException($"FS32-8 projection batch mutate rekey failed. worker={workerOrdinal} i={i}.");
                    }
                }

                LibraDexConcurrentBatchPublishResult publish = batch.Publish();
                if (publish.DeletedCount != opsPerThread * 2L ||
                    publish.ChangedRekeyCount != opsPerThread)
                {
                    throw new InvalidDataException($"FS32-8 projection batch mutate publish mismatch. worker={workerOrdinal} deletes={publish.DeletedCount} rekeys={publish.ChangedRekeyCount}.");
                }
            });
        }

        if (!readyGate.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidDataException("FS32-8 projection batch mutate could not ready all workers.");
        }

        startGate.Set();
        Task.WaitAll(tasks);
        watch.Stop();
        long expected = checked((long)threads * (seedPerThread - opsPerThread));
        long actual = expected;
        for (int worker = 0; worker < threads; worker++)
        {
            ValidateFixed32ProjectionSuffixCount(catalog, "fs32-proj-batch-mut", 0xA0 + worker, opsPerThread);
        }

        return CreateScalar8Scalar8PrimitiveConcurrencyProofRow(
            "mt-fs32-proj-mut",
            threads,
            threads * opsPerThread * 2,
            watch.Elapsed,
            GC.GetTotalAllocatedBytes(precise: true) - before,
            expected,
            actual,
            "shared-context projected FS32-8 delete plus rekey");
    }

    /// <summary>
    /// Counts all tuples in a generic long/long `SS8-8` index for primitive concurrency proof validation.<br/>
    /// The checksum is currently the tuple count because these proof rows only need loss detection, not value distribution reporting.<br/>
    /// </summary>
    /// <param name="index">The generic index to scan.<br/></param>
    /// <param name="checksum">Receives the count-shaped checksum.<br/></param>
    /// <returns>The total tuple count.</returns>
    private static long CountGenericLongLong(LibraDexIndex<long, long> index, out ulong checksum)
    {
        long count = 0;
        using LibraDexRangeReader<long, long> reader = index.OpenRangeReader(long.MinValue, long.MaxValue);
        while (reader.TryReadNext(out _, out _))
        {
            count++;
        }

        checksum = (ulong)count;
        return count;
    }

    /// <summary>
    /// Creates a memory-backed `FS32-8` index with an exact reversed maintained projection for concurrency proof rows.<br/>
    /// The index name is supplied so suffix validation can resolve the same logical index through the catalog condition bridge.<br/>
    /// </summary>
    /// <param name="catalog">The owning catalog.<br/></param>
    /// <param name="indexName">The index name to create inside the `projection-proof` group.<br/></param>
    /// <returns>The created projected index.</returns>
    private static LibraDexIndex<byte[], long> CreateFixed32Scalar8ProjectionIndex(Catalog catalog, string indexName)
    {
        return catalog.Indexes["projection-proof"][indexName].Blob.Scalar<long>(
            LibraDexScalarWidth.Bytes32,
            directions: LibraDexProjectionDirectionSet.ForwardAndReversed).Create();
    }

    /// <summary>
    /// Validates maintained exact reversed projection visibility by querying a suffix that maps to one deterministic projection stripe.<br/>
    /// This proves the primary count did not pass while the subindex silently missed inserts, deletes, or rekeys.<br/>
    /// </summary>
    /// <param name="catalog">The catalog that owns the projected index.<br/></param>
    /// <param name="indexName">The index name inside the `projection-proof` group.<br/></param>
    /// <param name="projectionStripe">The suffix byte selected by the generated proof keys.<br/></param>
    /// <param name="expectedCount">The expected suffix-backed identity count.<br/></param>
    private static void ValidateFixed32ProjectionSuffixCount(
        Catalog catalog,
        string indexName,
        int projectionStripe,
        long expectedCount)
    {
        IReadOnlyList<long> identities = catalog.Indexes["projection-proof"].GetIdentities<long>(
            catalog.Indexes["projection-proof"].Where(indexName).AsBinary.EndsWith([(byte)projectionStripe]).EndCondition,
            deduplication: IdentityDeduplication.Preserve);
        if (identities.Count != expectedCount)
        {
            throw new InvalidDataException($"FS32-8 projection suffix expected {expectedCount} identities but returned {identities.Count}.");
        }
    }

    /// <summary>
    /// Measures one-thread string/scalar8 inserts with folded, sort-key, and reversed projections through ordinary public insert.<br/>
    /// This is the immediate publication baseline for the maintained `VS8` projection set.<br/>
    /// </summary>
    /// <param name="operations">The number of inserts to perform.<br/></param>
    /// <returns>The measured proof row with projection-backed condition validation.</returns>
    private static Scalar8Scalar8PrimitiveConcurrencyProofRow RunStringScalar8ProjectionGenericDefaultSingle(int operations)
    {
        operations = Math.Min(operations, 64);
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });
        using LibraDexStringScalar8Index index = CreateStringScalar8ProjectionIndex(catalog, "vs8-proj-default");
        _ = index.Insert(CreateConcurrencyProofStringKey(0, 0), 1UL);

        long before = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 1; i <= operations; i++)
        {
            LibraDexGenericInsertResult result = index.Insert(CreateConcurrencyProofStringKey(i, 0), (ulong)(i + 1));
            if (!result.Inserted)
            {
                throw new InvalidDataException($"VS8 projection default insert failed at {i}.");
            }
        }

        watch.Stop();
        long expected = operations + 1L;
        ValidateStringProjectionCounts(catalog, "vs8-proj-default", worker: 0, expected);
        return CreateScalar8Scalar8PrimitiveConcurrencyProofRow(
            "vs8-proj-default-1t",
            threads: 1,
            operations,
            watch.Elapsed,
            GC.GetAllocatedBytesForCurrentThread() - before,
            expected,
            expected,
            "VS8 exact plus folded/sortkey/reversed subindexes");
    }

    /// <summary>
    /// Measures one-thread string/scalar8 inserts through the string concurrent batch shared `VS8` writer context.<br/>
    /// Primary and all maintained string projection rows should stage in one context when no topology fallback is needed.<br/>
    /// </summary>
    /// <param name="operations">The number of inserts to perform.<br/></param>
    /// <returns>The measured proof row with projection-backed condition validation.</returns>
    private static Scalar8Scalar8PrimitiveConcurrencyProofRow RunStringScalar8ProjectionGenericConcurrentBatchSingle(int operations)
    {
        operations = Math.Min(operations, 64);
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });
        using LibraDexStringScalar8Index index = CreateStringScalar8ProjectionIndex(catalog, "vs8-proj-batch");
        for (int i = 0; i < operations; i++)
        {
            _ = index.Insert(CreateConcurrencyProofStringKey(i * 2, 1), (ulong)(i + 1));
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch watch = Stopwatch.StartNew();
        using (LibraDexStringScalar8ConcurrentBatch batch = index.BeginConcurrentBatch())
        {
            for (int i = 0; i < operations; i++)
            {
                LibraDexGenericInsertResult result = batch.Insert(CreateConcurrencyProofStringKey((i * 2) + 1, 1), (ulong)(operations + i + 1));
                if (!result.Inserted ||
                    result.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext)
                {
                    throw new InvalidDataException($"VS8 projection batch insert expected writer context at {i}, got inserted={result.Inserted} path={result.QueuedInsertPath}.");
                }
            }

            LibraDexConcurrentBatchPublishResult publish = batch.Publish();
            if (publish.InsertedCount != operations ||
                publish.PublishedContextCount != 1)
            {
                throw new InvalidDataException($"VS8 projection batch publish expected {operations} inserts in one context, got inserts={publish.InsertedCount} contexts={publish.PublishedContextCount}.");
            }
        }

        watch.Stop();
        long expected = operations * 2L;
        ValidateStringProjectionCounts(catalog, "vs8-proj-batch", worker: 1, expected);
        return CreateScalar8Scalar8PrimitiveConcurrencyProofRow(
            "vs8-proj-batch-1t",
            threads: 1,
            operations,
            watch.Elapsed,
            GC.GetAllocatedBytesForCurrentThread() - before,
            expected,
            expected,
            "shared-context VS8 exact plus folded/sortkey/reversed subindexes");
    }

    /// <summary>
    /// Measures multi-thread string/scalar8 projected inserts where each worker uses an independent text prefix and suffix route.<br/>
    /// This proves folded and sort-key subindex maintenance can share the batch context without serializing unrelated shelves.<br/>
    /// </summary>
    /// <param name="threads">The number of concurrent workers.<br/></param>
    /// <param name="opsPerThread">The inserts each worker should perform.<br/></param>
    /// <returns>The measured proof row with projection-backed condition validation.</returns>
    private static Scalar8Scalar8PrimitiveConcurrencyProofRow RunStringScalar8ProjectionGenericConcurrentBatchDifferentShelfMulti(int threads, int opsPerThread)
    {
        opsPerThread = Math.Min(opsPerThread, 64);
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });
        using LibraDexStringScalar8Index index = CreateStringScalar8ProjectionIndex(catalog, "vs8-proj-batch-mt");
        for (int worker = 0; worker < threads; worker++)
        {
            _ = index.Insert(CreateConcurrencyProofStringKey(0, worker + 10), (ulong)(worker + 1));
        }

        using ManualResetEventSlim startGate = new(false);
        using CountdownEvent readyGate = new(threads);
        Task[] tasks = new Task[threads];
        long before = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch watch = Stopwatch.StartNew();
        for (int worker = 0; worker < threads; worker++)
        {
            int workerOrdinal = worker;
            tasks[worker] = Task.Run(() =>
            {
                readyGate.Signal();
                startGate.Wait();
                using LibraDexStringScalar8ConcurrentBatch batch = index.BeginConcurrentBatch();
                for (int i = 1; i <= opsPerThread; i++)
                {
                    LibraDexGenericInsertResult insert = batch.Insert(
                        CreateConcurrencyProofStringKey(i, workerOrdinal + 10),
                        checked((ulong)((workerOrdinal + 1L) * 1_000_000L + i)));
                    if (!insert.Inserted)
                    {
                        throw new InvalidDataException($"VS8 projection batch MT insert failed. worker={workerOrdinal} i={i}.");
                    }
                }

                LibraDexConcurrentBatchPublishResult publish = batch.Publish();
                if (publish.InsertedCount != opsPerThread)
                {
                    throw new InvalidDataException($"VS8 projection batch MT publish expected {opsPerThread} inserts, got {publish.InsertedCount}. worker={workerOrdinal}.");
                }
            });
        }

        if (!readyGate.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidDataException("VS8 projection batch MT could not ready all workers.");
        }

        startGate.Set();
        Task.WaitAll(tasks);
        watch.Stop();
        long expected = checked(threads + ((long)threads * opsPerThread));
        for (int worker = 0; worker < threads; worker++)
        {
            ValidateStringProjectionCounts(catalog, "vs8-proj-batch-mt", worker + 10, opsPerThread + 1L);
        }

        return CreateScalar8Scalar8PrimitiveConcurrencyProofRow(
            "mt-vs8-proj-batch",
            threads,
            threads * opsPerThread,
            watch.Elapsed,
            GC.GetTotalAllocatedBytes(precise: true) - before,
            expected,
            expected,
            "shared-context projected VS8 independent shelves");
    }

    /// <summary>
    /// Measures multi-thread string/scalar8 projected deletes and rekeys through one shared-context concurrent batch per worker.<br/>
    /// Projection validation proves folded, folded-reversed, and sort-key rows track the primary delete-plus-insert mutation set.<br/>
    /// </summary>
    /// <param name="threads">The number of concurrent workers.<br/></param>
    /// <param name="opsPerThread">The delete/rekey pairs each worker should perform.<br/></param>
    /// <returns>The measured proof row with projection-backed condition validation.</returns>
    private static Scalar8Scalar8PrimitiveConcurrencyProofRow RunStringScalar8ProjectionGenericConcurrentBatchDeleteRekeyMulti(int threads, int opsPerThread)
    {
        opsPerThread = Math.Min(opsPerThread, 32);
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });
        using LibraDexStringScalar8Index index = CreateStringScalar8ProjectionIndex(catalog, "vs8-proj-batch-mut");
        int seedPerThread = checked(opsPerThread * 2);
        for (int worker = 0; worker < threads; worker++)
        {
            for (int i = 0; i < seedPerThread; i++)
            {
                LibraDexGenericInsertResult insert = index.Insert(
                    CreateConcurrencyProofStringKey(i, worker + 20),
                    checked((ulong)((worker + 1L) * 1_000_000L + i)));
                if (!insert.Inserted)
                {
                    throw new InvalidDataException($"VS8 projection batch mutate seed insert failed. worker={worker} i={i}.");
                }
            }
        }

        using ManualResetEventSlim startGate = new(false);
        using CountdownEvent readyGate = new(threads);
        Task[] tasks = new Task[threads];
        long before = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch watch = Stopwatch.StartNew();
        for (int worker = 0; worker < threads; worker++)
        {
            int workerOrdinal = worker;
            tasks[worker] = Task.Run(() =>
            {
                readyGate.Signal();
                startGate.Wait();
                using LibraDexStringScalar8ConcurrentBatch batch = index.BeginConcurrentBatch();
                for (int i = 0; i < opsPerThread; i++)
                {
                    ulong deleteIdentity = checked((ulong)((workerOrdinal + 1L) * 1_000_000L + i));
                    LibraDexGenericDeleteResult delete = batch.Delete(CreateConcurrencyProofStringKey(i, workerOrdinal + 20), deleteIdentity);
                    if (!delete.Deleted)
                    {
                        throw new InvalidDataException($"VS8 projection batch mutate delete failed. worker={workerOrdinal} i={i}.");
                    }

                    ulong rekeyIdentity = checked((ulong)((workerOrdinal + 1L) * 1_000_000L + opsPerThread + i));
                    LibraDexGenericRekeyResult rekey = batch.Rekey(
                        rekeyIdentity,
                        CreateConcurrencyProofStringKey(opsPerThread + i, workerOrdinal + 20),
                        CreateConcurrencyProofStringKey(500_000 + i, workerOrdinal + 20));
                    if (!rekey.Changed)
                    {
                        throw new InvalidDataException($"VS8 projection batch mutate rekey failed. worker={workerOrdinal} i={i}.");
                    }
                }

                LibraDexConcurrentBatchPublishResult publish = batch.Publish();
                if (publish.DeletedCount != opsPerThread * 2L ||
                    publish.ChangedRekeyCount != opsPerThread)
                {
                    throw new InvalidDataException($"VS8 projection batch mutate publish mismatch. worker={workerOrdinal} deletes={publish.DeletedCount} rekeys={publish.ChangedRekeyCount}.");
                }
            });
        }

        if (!readyGate.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidDataException("VS8 projection batch mutate could not ready all workers.");
        }

        startGate.Set();
        Task.WaitAll(tasks);
        watch.Stop();
        long expected = checked((long)threads * (seedPerThread - opsPerThread));
        for (int worker = 0; worker < threads; worker++)
        {
            ValidateStringProjectionCounts(catalog, "vs8-proj-batch-mut", worker + 20, opsPerThread);
        }

        return CreateScalar8Scalar8PrimitiveConcurrencyProofRow(
            "mt-vs8-proj-mut",
            threads,
            threads * opsPerThread * 2,
            watch.Elapsed,
            GC.GetTotalAllocatedBytes(precise: true) - before,
            expected,
            expected,
            "shared-context projected VS8 delete plus rekey");
    }

    /// <summary>
    /// Creates a memory-backed string/scalar8 index with every maintained string projection enabled.<br/>
    /// The shape maps exact, folded, sort-key, exact-reversed, and folded-reversed projection rows onto `VS8` primitives.<br/>
    /// </summary>
    /// <param name="catalog">The owning catalog.<br/></param>
    /// <param name="indexName">The index name to create inside the string projection proof group.<br/></param>
    /// <returns>The created string projected index.</returns>
    private static LibraDexStringScalar8Index CreateStringScalar8ProjectionIndex(Catalog catalog, string indexName)
    {
        return catalog.Indexes["string-projection-proof"][indexName].String.Create(
            stringKeys: StringKeys.ExactFoldedAndSortKey,
            directions: LibraDexProjectionDirectionSet.ForwardAndReversed,
            sortKeyCulture: "en-US");
    }

    /// <summary>
    /// Creates a deterministic string key whose prefix and suffix select one proof worker's projection routes.<br/>
    /// Mixed casing is intentional so folded and sort-key validations have to use maintained case-insensitive projection paths.<br/>
    /// </summary>
    /// <param name="value">The per-worker sequence value.<br/></param>
    /// <param name="worker">The logical worker/projection stripe.<br/></param>
    /// <returns>The deterministic string proof key.</returns>
    private static string CreateConcurrencyProofStringKey(int value, int worker)
    {
        return FormattableString.Invariant($"S{worker:X2}-Proof-{value:D6}-Tail{worker:X2}");
    }

    /// <summary>
    /// Validates exact, folded, folded-reversed, and sort-key projection visibility for one worker's string route.<br/>
    /// Each condition goes through the catalog projection bridge so a stale or missing subindex row fails independently of the primary insert count.<br/>
    /// </summary>
    /// <param name="catalog">The catalog that owns the projected index.<br/></param>
    /// <param name="indexName">The index name inside the string projection proof group.<br/></param>
    /// <param name="worker">The worker/projection stripe to validate.<br/></param>
    /// <param name="expectedCount">The expected identity count for this worker route.<br/></param>
    private static void ValidateStringProjectionCounts(
        Catalog catalog,
        string indexName,
        int worker,
        long expectedCount)
    {
        string prefix = FormattableString.Invariant($"S{worker:X2}-Proof-");
        string foldedPrefix = prefix.ToLowerInvariant();
        string suffix = FormattableString.Invariant($"Tail{worker:X2}");
        string foldedSuffix = suffix.ToLowerInvariant();
        string sortLower = foldedPrefix;
        string sortUpper = foldedPrefix + "\uffff";
        var group = catalog.Indexes["string-projection-proof"];

        IReadOnlyList<ulong> exactPrefix = group.GetIdentities<ulong>(
            group.Where(indexName).AsString.StartsWith(prefix).EndCondition,
            deduplication: IdentityDeduplication.Preserve);
        IReadOnlyList<ulong> foldedPrefixIds = group.GetIdentities<ulong>(
            group.Where(indexName).AsString.StartsWith(foldedPrefix, ignoreCase: true).EndCondition,
            deduplication: IdentityDeduplication.Preserve);
        IReadOnlyList<ulong> foldedSuffixIds = group.GetIdentities<ulong>(
            group.Where(indexName).AsString.EndsWith(foldedSuffix, ignoreCase: true).EndCondition,
            deduplication: IdentityDeduplication.Preserve);
        IReadOnlyList<ulong> sortKeyRangeIds = group.GetIdentities<ulong>(
            group.Where(indexName).AsString.Between(sortLower, sortUpper, ignoreCase: true, culture: "en-US").EndCondition,
            deduplication: IdentityDeduplication.Preserve);

        if (exactPrefix.Count != expectedCount ||
            foldedPrefixIds.Count != expectedCount ||
            foldedSuffixIds.Count != expectedCount ||
            sortKeyRangeIds.Count != expectedCount)
        {
            throw new InvalidDataException($"VS8 projection validation expected {expectedCount} identities for worker {worker}, got exact={exactPrefix.Count} folded={foldedPrefixIds.Count} suffix={foldedSuffixIds.Count} sortkey={sortKeyRangeIds.Count}.");
        }
    }

    /// <summary>
    /// Creates and validates one `SS8-8` primitive concurrency proof row.<br/>
    /// Validation is intentionally centralized so every measured row must prove final readable count before being printed.<br/>
    /// </summary>
    /// <param name="scenario">The scenario label to print.<br/></param>
    /// <param name="threads">The number of worker threads used by the measured operation.<br/></param>
    /// <param name="operations">The number of measured operations.<br/></param>
    /// <param name="elapsed">The measured elapsed time.<br/></param>
    /// <param name="allocatedBytes">The allocated bytes reported by the runtime counter.<br/></param>
    /// <param name="expectedCount">The expected final tuple count.<br/></param>
    /// <param name="actualCount">The actual final tuple count.<br/></param>
    /// <param name="note">A short scenario note.<br/></param>
    /// <returns>The validated report row.</returns>
    private static Scalar8Scalar8PrimitiveConcurrencyProofRow CreateScalar8Scalar8PrimitiveConcurrencyProofRow(
        string scenario,
        int threads,
        int operations,
        TimeSpan elapsed,
        long allocatedBytes,
        long expectedCount,
        long actualCount,
        string note)
    {
        if (actualCount != expectedCount)
        {
            throw new InvalidDataException($"SS8-8 primitive concurrency proof {scenario} expected {expectedCount} rows but read back {actualCount}.");
        }

        double seconds = Math.Max(elapsed.TotalSeconds, 0.000000001);
        return new Scalar8Scalar8PrimitiveConcurrencyProofRow(
            scenario,
            threads,
            operations,
            elapsed,
            operations / seconds,
            allocatedBytes,
            expectedCount,
            actualCount,
            note);
    }

    /// <summary>
    /// Prints derived `SS8-8` primitive benchmark ratios from the validated performance rows.<br/>
    /// The ratios explain whether elapsed-time differences are direct-write overhead, queued facade overhead, or real same-shelf contention.<br/>
    /// </summary>
    /// <param name="rows">The validated proof rows printed by the command.<br/></param>
    private static void PrintScalar8Scalar8PrimitiveConcurrencyRatios(Scalar8Scalar8PrimitiveConcurrencyProofRow[] rows)
    {
        Scalar8Scalar8PrimitiveConcurrencyProofRow direct = FindScalar8Scalar8PrimitiveConcurrencyProofRow(rows, "isolated-1t");
        Scalar8Scalar8PrimitiveConcurrencyProofRow context = FindScalar8Scalar8PrimitiveConcurrencyProofRow(rows, "writer-context-1t");
        Scalar8Scalar8PrimitiveConcurrencyProofRow queued = FindScalar8Scalar8PrimitiveConcurrencyProofRow(rows, "queued-1t");
        Scalar8Scalar8PrimitiveConcurrencyProofRow genericDefault = FindScalar8Scalar8PrimitiveConcurrencyProofRow(rows, "generic-default-1t");
        Scalar8Scalar8PrimitiveConcurrencyProofRow genericQueued = FindScalar8Scalar8PrimitiveConcurrencyProofRow(rows, "generic-queued-1t");
        Scalar8Scalar8PrimitiveConcurrencyProofRow genericBatch = FindScalar8Scalar8PrimitiveConcurrencyProofRow(rows, "generic-batch-1t");
        Scalar8Scalar8PrimitiveConcurrencyProofRow mtDifferent = FindScalar8Scalar8PrimitiveConcurrencyProofRow(rows, "mt-different-equal");
        Scalar8Scalar8PrimitiveConcurrencyProofRow mtSame = FindScalar8Scalar8PrimitiveConcurrencyProofRow(rows, "mt-same-equal");

        Console.WriteLine("ratio value note");
        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "writer-context/direct {0:F3} elapsed multiplier",
            CreateScalar8Scalar8PrimitiveElapsedRatio(context, direct)));
        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "queued/direct {0:F3} elapsed multiplier",
            CreateScalar8Scalar8PrimitiveElapsedRatio(queued, direct)));
        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "queued/writer-context {0:F3} elapsed multiplier",
            CreateScalar8Scalar8PrimitiveElapsedRatio(queued, context)));
        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "generic-batch/generic-default {0:F3} elapsed multiplier",
            CreateScalar8Scalar8PrimitiveElapsedRatio(genericBatch, genericDefault)));
        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "generic-queued/generic-batch {0:F3} elapsed multiplier",
            CreateScalar8Scalar8PrimitiveElapsedRatio(genericQueued, genericBatch)));
        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "mt-different/queued {0:F3} elapsed multiplier at equal measured ops",
            CreateScalar8Scalar8PrimitiveElapsedRatio(mtDifferent, queued)));
        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "mt-same/mt-different {0:F3} elapsed multiplier at equal measured ops",
            CreateScalar8Scalar8PrimitiveElapsedRatio(mtSame, mtDifferent)));
    }

    /// <summary>
    /// Finds one named `SS8-8` primitive proof row in the command report.<br/>
    /// Missing rows are treated as harness errors because ratio output depends on a stable row set.<br/>
    /// </summary>
    /// <param name="rows">The report rows to search.<br/></param>
    /// <param name="scenario">The scenario label to find.<br/></param>
    /// <returns>The matching report row.</returns>
    private static Scalar8Scalar8PrimitiveConcurrencyProofRow FindScalar8Scalar8PrimitiveConcurrencyProofRow(
        Scalar8Scalar8PrimitiveConcurrencyProofRow[] rows,
        string scenario)
    {
        for (int i = 0; i < rows.Length; i++)
        {
            if (StringComparer.Ordinal.Equals(rows[i].Scenario, scenario))
            {
                return rows[i];
            }
        }

        throw new InvalidDataException($"SS8-8 primitive proof row '{scenario}' was not produced.");
    }

    /// <summary>
    /// Creates an elapsed-time multiplier between two `SS8-8` primitive proof rows.<br/>
    /// A value above one means the numerator row took longer than the denominator row for its measured operation count.<br/>
    /// </summary>
    /// <param name="numerator">The row whose elapsed time is divided.<br/></param>
    /// <param name="denominator">The baseline row whose elapsed time divides the numerator.<br/></param>
    /// <returns>The elapsed-time multiplier.</returns>
    private static double CreateScalar8Scalar8PrimitiveElapsedRatio(
        Scalar8Scalar8PrimitiveConcurrencyProofRow numerator,
        Scalar8Scalar8PrimitiveConcurrencyProofRow denominator)
    {
        double denominatorMilliseconds = Math.Max(denominator.Elapsed.TotalMilliseconds, 0.000001);
        return numerator.Elapsed.TotalMilliseconds / denominatorMilliseconds;
    }

    /// <summary>
    /// Runs a bounded concurrency performance/proof matrix over the shapes most affected by writer-context work.<br/>
    /// The command measures single-thread no-batch inserts plus multi-thread warmed-route inserts, then validates final row counts so throughput rows cannot hide lost writes.<br/>
    /// </summary>
    /// <param name="args">The harness command-line arguments.<br/></param>
    /// <returns>Zero when every measured row validates its final count.</returns>
    private static int RunConcurrencyPerformanceProof(string[] args)
    {
        int singleOps = GetIntOption(args, "--single-ops", 512);
        int opsPerThread = GetIntOption(args, "--ops-per-thread", 128);
        int maxThreads = GetIntOption(args, "--max-threads", 8);
        bool includeFile = GetBoolOption(args, "--include-file", true);
        string outputPath = GetOption(args, "--output", CreateConcurrencyProofOutputPath("csv"));
        string markdownPath = GetOption(args, "--markdown", Path.ChangeExtension(outputPath, ".md"));
        string baselinePath = GetOption(args, "--baseline", string.Empty);
        double maxSlowdownPercent = double.Parse(GetOption(args, "--max-slowdown-percent", "75"), CultureInfo.InvariantCulture);
        double maxAllocationGrowthPercent = double.Parse(GetOption(args, "--max-allocation-growth-percent", "25"), CultureInfo.InvariantCulture);
        double maxFileGrowthPercent = double.Parse(GetOption(args, "--max-file-growth-percent", "10"), CultureInfo.InvariantCulture);
        double minThroughputCompareMs = double.Parse(GetOption(args, "--min-throughput-compare-ms", "100"), CultureInfo.InvariantCulture);
        int[] threadCounts = BuildConcurrencyProofThreadCounts(maxThreads);

        List<ConcurrencyPerformanceProofRow> rows = [];
        rows.Add(RunConcurrencyProofScalar8Scalar8Single(singleOps, DataKernelBackingKind.Memory, path: null));
        rows.Add(RunConcurrencyProofVarKeyScalar8Single(singleOps, DataKernelBackingKind.Memory, path: null));
        rows.Add(RunConcurrencyProofVarKeyScalar16Single(singleOps, DataKernelBackingKind.Memory, path: null));
        rows.Add(RunConcurrencyProofScalar8VarIdentitySingle(singleOps, DataKernelBackingKind.Memory, path: null));
        rows.Add(RunConcurrencyProofScalar16VarIdentitySingle(singleOps, DataKernelBackingKind.Memory, path: null));
        rows.Add(RunConcurrencyProofVarKeyVarIdentitySingle(singleOps, DataKernelBackingKind.Memory, path: null));

        if (includeFile)
        {
            rows.Add(RunConcurrencyProofScalar8Scalar8Single(Math.Max(32, singleOps / 4), DataKernelBackingKind.File, CreateConcurrencyProofPath("ss88-single")));
            rows.Add(RunConcurrencyProofVarKeyScalar8Single(Math.Max(32, singleOps / 4), DataKernelBackingKind.File, CreateConcurrencyProofPath("vs8-single")));
            rows.Add(RunConcurrencyProofVarKeyScalar16Single(Math.Max(32, singleOps / 4), DataKernelBackingKind.File, CreateConcurrencyProofPath("vs16-single")));
            rows.Add(RunConcurrencyProofScalar8VarIdentitySingle(Math.Max(32, singleOps / 4), DataKernelBackingKind.File, CreateConcurrencyProofPath("sv8-single")));
            rows.Add(RunConcurrencyProofScalar16VarIdentitySingle(Math.Max(32, singleOps / 4), DataKernelBackingKind.File, CreateConcurrencyProofPath("sv16-single")));
            rows.Add(RunConcurrencyProofVarKeyVarIdentitySingle(Math.Max(32, singleOps / 4), DataKernelBackingKind.File, CreateConcurrencyProofPath("vv-single")));
        }

        for (int i = 0; i < threadCounts.Length; i++)
        {
            int threads = threadCounts[i];
            rows.Add(RunConcurrencyProofScalar8Scalar8Multi(threads, opsPerThread));
            rows.Add(RunConcurrencyProofVarKeyScalar8Multi(threads, opsPerThread));
            rows.Add(RunConcurrencyProofVarKeyScalar16Multi(threads, opsPerThread));
            rows.Add(RunConcurrencyProofScalar8VarIdentityMulti(threads, opsPerThread));
            rows.Add(RunConcurrencyProofScalar16VarIdentityMulti(threads, opsPerThread));
            rows.Add(RunConcurrencyProofVarKeyVarIdentityMulti(threads, opsPerThread));
        }

        Console.WriteLine("shape mode backing threads ops elapsed-ms ops-sec alloc-bytes file-bytes expected-count actual-count checksum");
        for (int i = 0; i < rows.Count; i++)
        {
            ConcurrencyPerformanceProofRow row = rows[i];
            Console.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "{0} {1} {2} {3} {4} {5:F3} {6:F2} {7} {8} {9} {10} {11}",
                row.Shape,
                row.Mode,
                row.Backing,
                row.Threads,
                row.Operations,
                row.Elapsed.TotalMilliseconds,
                row.OperationsPerSecond,
                row.AllocatedBytes,
                row.FileBytes,
                row.ExpectedCount,
                row.ActualCount,
                row.Checksum));
        }

        WriteConcurrencyProofCsv(outputPath, rows);
        WriteConcurrencyProofMarkdown(markdownPath, args, rows);

        if (baselinePath.Length != 0)
        {
            CompareConcurrencyProofBaseline(
                baselinePath,
                rows,
                maxSlowdownPercent,
                maxAllocationGrowthPercent,
                maxFileGrowthPercent,
                minThroughputCompareMs);
        }

        Console.WriteLine($"output-csv={Path.GetFullPath(outputPath)}");
        Console.WriteLine($"output-md={Path.GetFullPath(markdownPath)}");
        Console.WriteLine("concurrency-performance-proof ok");
        return 0;
    }

    /// <summary>
    /// Runs fixed-N BigInteger insert/delete/rekey proof workloads with increasing thread counts.<br/>
    /// The proof is intentionally fixed-N-specific because `LibraDexBigIntScalar8Index&lt;TIdentity&gt;` is not the generic queued-writer facade and should be measured separately until it has writer-context staging parity.<br/>
    /// </summary>
    /// <param name="args">The harness command-line arguments.<br/></param>
    /// <returns>Zero when every fixed-N workload validates its expected final state.<br/></returns>
    private static int RunFixedNConcurrencyPerformanceProof(string[] args)
    {
        int singleOps = GetIntOption(args, "--single-ops", 1_024);
        int opsPerThread = GetIntOption(args, "--ops-per-thread", 256);
        int maxThreads = GetIntOption(args, "--max-threads", 8);
        if (singleOps <= 0)
            throw new ArgumentOutOfRangeException(nameof(args), singleOps, "Fixed-N single operation count must be positive.");
        if (opsPerThread <= 0)
            throw new ArgumentOutOfRangeException(nameof(args), opsPerThread, "Fixed-N operations per thread must be positive.");
        if (maxThreads <= 0)
            throw new ArgumentOutOfRangeException(nameof(args), maxThreads, "Fixed-N max thread count must be positive.");

        int[] threadCounts = BuildConcurrencyProofThreadCounts(maxThreads);
        List<FixedNConcurrencyProofRow> rows =
        [
            RunFixedNConcurrencyProofScalar8Single(singleOps),
            RunFixedNConcurrencyProofScalar16Single(singleOps)
        ];

        for (int i = 0; i < threadCounts.Length; i++)
        {
            int threads = threadCounts[i];
            rows.Add(RunFixedNConcurrencyProofScalar8Multi(threads, opsPerThread));
            rows.Add(RunFixedNConcurrencyProofScalar16Multi(threads, opsPerThread));
            rows.Add(RunFixedNConcurrencyProofScalar8MultiMixed(threads, opsPerThread));
            rows.Add(RunFixedNConcurrencyProofScalar16MultiMixed(threads, opsPerThread));
        }

        Console.WriteLine("shape mode threads ops elapsed-ms ops-sec alloc-bytes expected-count actual-count checksum");
        for (int i = 0; i < rows.Count; i++)
        {
            FixedNConcurrencyProofRow row = rows[i];
            Console.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "{0} {1} {2} {3} {4:F3} {5:F1} {6} {7} {8} {9}",
                row.Shape,
                row.Mode,
                row.Threads,
                row.Operations,
                row.Elapsed.TotalMilliseconds,
                row.Operations / Math.Max(row.Elapsed.TotalSeconds, 0.000001),
                row.AllocatedBytes,
                row.ExpectedCount,
                row.ActualCount,
                row.Checksum));
        }

        Console.WriteLine("fixedn-concurrency-performance-proof ok");
        return 0;
    }

    /// <summary>
    /// Compares fixed-N BigInteger one-shot inserts with caller-sized explicit batches on pre-warmed shelves.<br/>
    /// The proof targets the first batch-coalescing slice: many ordinary-key inserts that stay inside one existing fixed-N leaf shelf and publish once at batch end.<br/>
    /// </summary>
    /// <param name="args">The harness command-line arguments.<br/></param>
    /// <returns>Zero when every batch size validates its expected final state.<br/></returns>
    private static int RunFixedNBatchCoalescingProof(string[] args)
    {
        int small = GetIntOption(args, "--small", 8);
        int medium = GetIntOption(args, "--medium", 64);
        int large = GetIntOption(args, "--large", 512);
        if (small <= 0 || medium <= 0 || large <= 0)
            throw new ArgumentOutOfRangeException(nameof(args), "Fixed-N batch proof sizes must be positive.");

        int[] sizes = [small, medium, large];
        List<FixedNBatchProofRow> rows = [];
        for (int i = 0; i < sizes.Length; i++)
        {
            int size = sizes[i];
            rows.Add(RunFixedNBatchProofScalar8OneShot(size));
            rows.Add(RunFixedNBatchProofScalar8Batch(size));
            rows.Add(RunFixedNBatchProofScalar8BatchDelete(size));
            rows.Add(RunFixedNBatchProofScalar8BatchDeleteMany(size));
            rows.Add(RunFixedNBatchProofScalar8BatchRekey(size));
            rows.Add(RunFixedNBatchProofScalar8BatchRekeyMany(size));
            rows.Add(RunFixedNBatchProofScalar16OneShot(size));
            rows.Add(RunFixedNBatchProofScalar16Batch(size));
            rows.Add(RunFixedNBatchProofScalar16BatchDelete(size));
            rows.Add(RunFixedNBatchProofScalar16BatchDeleteMany(size));
            rows.Add(RunFixedNBatchProofScalar16BatchRekey(size));
            rows.Add(RunFixedNBatchProofScalar16BatchRekeyMany(size));
        }

        Console.WriteLine("shape mode batch-size elapsed-ms ops-sec alloc-bytes expected-count actual-count checksum");
        for (int i = 0; i < rows.Count; i++)
        {
            FixedNBatchProofRow row = rows[i];
            Console.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "{0} {1} {2} {3:F3} {4:F1} {5} {6} {7} {8}",
                row.Shape,
                row.Mode,
                row.BatchSize,
                row.Elapsed.TotalMilliseconds,
                row.BatchSize / Math.Max(row.Elapsed.TotalSeconds, 0.000001),
                row.AllocatedBytes,
                row.ExpectedCount,
                row.ActualCount,
                row.Checksum));
        }

        Console.WriteLine("fixedn-batch-coalescing-proof ok");
        return 0;
    }

    private static FixedNBatchProofRow RunFixedNBatchProofScalar8OneShot(int batchSize)
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        using LibraDexBigIntScalar8Index<long> index = catalog.Indexes["fixedn-batch-proof-oneshot-8"]["score"].BigIntKeys<long>(maxBytes: 32).Create();
        WarmFixedNScalar8BatchShelf(index);
        long before = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < batchSize; i++)
        {
            ValidateGenericInsert(index.Insert(new BigInteger(i + 1), i + 1L), $"fixed-N scalar-8 one-shot batch proof insert {i}");
        }

        stopwatch.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        long actualCount = CountBigIntScalar8(index, BigInteger.Zero, new BigInteger(batchSize + 2));
        long checksum = SumBigIntScalar8(index, BigInteger.Zero, new BigInteger(batchSize + 2));
        if (actualCount != batchSize)
            throw new InvalidDataException($"Fixed-N scalar-8 one-shot batch proof expected {batchSize} rows but saw {actualCount}.");

        return new FixedNBatchProofRow("fsn-8", "one-shot", batchSize, stopwatch.Elapsed, allocated, batchSize, actualCount, checksum);
    }

    private static FixedNBatchProofRow RunFixedNBatchProofScalar8Batch(int batchSize)
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        using LibraDexBigIntScalar8Index<long> index = catalog.Indexes["fixedn-batch-proof-batch-8"]["score"].BigIntKeys<long>(maxBytes: 32).Create();
        WarmFixedNScalar8BatchShelf(index);
        long before = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch stopwatch = Stopwatch.StartNew();
        using (LibraDexBigIntScalar8Batch<long> batch = index.BeginBatch())
        {
            for (int i = 0; i < batchSize; i++)
            {
                ValidateGenericInsert(batch.Insert(new BigInteger(i + 1), i + 1L), $"fixed-N scalar-8 explicit batch proof insert {i}");
            }

            LibraDexGenericBatchCommitResult commit = batch.Commit();
            if (commit.InsertedCount != batchSize)
                throw new InvalidDataException($"Fixed-N scalar-8 explicit batch reported {commit.InsertedCount} inserts, expected {batchSize}.");
        }

        stopwatch.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        long actualCount = CountBigIntScalar8(index, BigInteger.Zero, new BigInteger(batchSize + 2));
        long checksum = SumBigIntScalar8(index, BigInteger.Zero, new BigInteger(batchSize + 2));
        if (actualCount != batchSize)
            throw new InvalidDataException($"Fixed-N scalar-8 explicit batch proof expected {batchSize} rows but saw {actualCount}.");

        return new FixedNBatchProofRow("fsn-8", "explicit-batch", batchSize, stopwatch.Elapsed, allocated, batchSize, actualCount, checksum);
    }

    private static FixedNBatchProofRow RunFixedNBatchProofScalar16OneShot(int batchSize)
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        using LibraDexBigIntScalar8Index<Guid> index = catalog.Indexes["fixedn-batch-proof-oneshot-16"]["score"].BigIntKeys<Guid>(maxBytes: 32).Create();
        WarmFixedNScalar16BatchShelf(index);
        long before = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < batchSize; i++)
        {
            ValidateGenericInsert(index.Insert(new BigInteger(i + 1), CreateFixedNConcurrencyGuid(i + 1)), $"fixed-N scalar-16 one-shot batch proof insert {i}");
        }

        stopwatch.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        long actualCount = CountBigIntScalar16(index, BigInteger.Zero, new BigInteger(batchSize + 2));
        long checksum = SumFixedNConcurrencyGuidLow(index, BigInteger.Zero, new BigInteger(batchSize + 2));
        if (actualCount != batchSize)
            throw new InvalidDataException($"Fixed-N scalar-16 one-shot batch proof expected {batchSize} rows but saw {actualCount}.");

        return new FixedNBatchProofRow("fsn-16", "one-shot", batchSize, stopwatch.Elapsed, allocated, batchSize, actualCount, checksum);
    }

    private static FixedNBatchProofRow RunFixedNBatchProofScalar16Batch(int batchSize)
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        using LibraDexBigIntScalar8Index<Guid> index = catalog.Indexes["fixedn-batch-proof-batch-16"]["score"].BigIntKeys<Guid>(maxBytes: 32).Create();
        WarmFixedNScalar16BatchShelf(index);
        long before = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch stopwatch = Stopwatch.StartNew();
        using (LibraDexBigIntScalar8Batch<Guid> batch = index.BeginBatch())
        {
            for (int i = 0; i < batchSize; i++)
            {
                ValidateGenericInsert(batch.Insert(new BigInteger(i + 1), CreateFixedNConcurrencyGuid(i + 1)), $"fixed-N scalar-16 explicit batch proof insert {i}");
            }

            LibraDexGenericBatchCommitResult commit = batch.Commit();
            if (commit.InsertedCount != batchSize)
                throw new InvalidDataException($"Fixed-N scalar-16 explicit batch reported {commit.InsertedCount} inserts, expected {batchSize}.");
        }

        stopwatch.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        long actualCount = CountBigIntScalar16(index, BigInteger.Zero, new BigInteger(batchSize + 2));
        long checksum = SumFixedNConcurrencyGuidLow(index, BigInteger.Zero, new BigInteger(batchSize + 2));
        if (actualCount != batchSize)
            throw new InvalidDataException($"Fixed-N scalar-16 explicit batch proof expected {batchSize} rows but saw {actualCount}.");

        return new FixedNBatchProofRow("fsn-16", "explicit-batch", batchSize, stopwatch.Elapsed, allocated, batchSize, actualCount, checksum);
    }

    private static FixedNBatchProofRow RunFixedNBatchProofScalar8BatchDelete(int batchSize)
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        using LibraDexBigIntScalar8Index<long> index = catalog.Indexes["fixedn-batch-proof-delete-8"]["score"].BigIntKeys<long>(maxBytes: 32).Create();
        WarmFixedNScalar8BatchShelf(index);
        for (int i = 0; i < batchSize; i++)
        {
            ValidateGenericInsert(index.Insert(new BigInteger(i + 1), i + 1L), $"fixed-N scalar-8 explicit batch delete seed {i}");
        }

        long before = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch stopwatch = Stopwatch.StartNew();
        using (LibraDexBigIntScalar8Batch<long> batch = index.BeginBatch())
        {
            for (int i = 0; i < batchSize; i++)
            {
                LibraDexGenericDeleteResult delete = batch.Delete(new BigInteger(i + 1), i + 1L);
                if (!delete.Deleted)
                    throw new InvalidDataException($"Fixed-N scalar-8 explicit batch delete failed for {i}.");
            }

            LibraDexGenericBatchCommitResult commit = batch.Commit();
            if (batch.DeletedCount != batchSize)
                throw new InvalidDataException($"Fixed-N scalar-8 explicit batch reported {batch.DeletedCount} deletes, expected {batchSize}.");
            if (commit.StorageDiagnostics.FixedNScalar8DirtyShelves == 0)
                throw new InvalidDataException("Fixed-N scalar-8 explicit batch delete did not report dirty FSN-8 shelves.");
        }

        stopwatch.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        long actualCount = CountBigIntScalar8(index, BigInteger.Zero, new BigInteger(batchSize + 2));
        long checksum = SumBigIntScalar8(index, BigInteger.Zero, new BigInteger(batchSize + 2));
        if (actualCount != 0)
            throw new InvalidDataException($"Fixed-N scalar-8 explicit batch delete proof expected 0 rows but saw {actualCount}.");

        return new FixedNBatchProofRow("fsn-8", "explicit-batch-delete", batchSize, stopwatch.Elapsed, allocated, 0, actualCount, checksum);
    }

    private static FixedNBatchProofRow RunFixedNBatchProofScalar8BatchDeleteMany(int batchSize)
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        using LibraDexBigIntScalar8Index<long> index = catalog.Indexes["fixedn-batch-proof-delete-many-8"]["score"].BigIntKeys<long>(maxBytes: 32).Create();
        WarmFixedNScalar8BatchShelf(index);
        (BigInteger Key, long Identity)[] deletes = new (BigInteger Key, long Identity)[batchSize];
        for (int i = 0; i < batchSize; i++)
        {
            BigInteger key = new(i + 1);
            long identity = i + 1L;
            ValidateGenericInsert(index.Insert(key, identity), $"fixed-N scalar-8 explicit bulk delete seed {i}");
            deletes[i] = (key, identity);
        }

        long before = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch stopwatch = Stopwatch.StartNew();
        using (LibraDexBigIntScalar8Batch<long> batch = index.BeginBatch())
        {
            long deleted = batch.DeleteMany(deletes);
            LibraDexGenericBatchCommitResult commit = batch.Commit();
            if (deleted != batchSize || batch.DeletedCount != batchSize)
                throw new InvalidDataException($"Fixed-N scalar-8 explicit bulk delete reported deleted={deleted}/batch={batch.DeletedCount}, expected {batchSize}.");
            if (commit.StorageDiagnostics.FixedNScalar8DirtyShelves == 0)
                throw new InvalidDataException("Fixed-N scalar-8 explicit bulk delete did not report dirty FSN-8 shelves.");
        }

        stopwatch.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        long actualCount = CountBigIntScalar8(index, BigInteger.Zero, new BigInteger(batchSize + 2));
        long checksum = SumBigIntScalar8(index, BigInteger.Zero, new BigInteger(batchSize + 2));
        if (actualCount != 0)
            throw new InvalidDataException($"Fixed-N scalar-8 explicit bulk delete proof expected 0 rows but saw {actualCount}.");

        return new FixedNBatchProofRow("fsn-8", "explicit-bulk-delete", batchSize, stopwatch.Elapsed, allocated, 0, actualCount, checksum);
    }

    private static FixedNBatchProofRow RunFixedNBatchProofScalar8BatchRekey(int batchSize)
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        using LibraDexBigIntScalar8Index<long> index = catalog.Indexes["fixedn-batch-proof-rekey-8"]["score"].BigIntKeys<long>(maxBytes: 32).Create();
        WarmFixedNScalar8BatchShelf(index);
        for (int i = 0; i < batchSize; i++)
        {
            ValidateGenericInsert(index.Insert(new BigInteger(i + 1), i + 1L), $"fixed-N scalar-8 explicit batch rekey seed {i}");
        }

        long before = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch stopwatch = Stopwatch.StartNew();
        using (LibraDexBigIntScalar8Batch<long> batch = index.BeginBatch())
        {
            for (int i = 0; i < batchSize; i++)
            {
                LibraDexGenericRekeyResult rekey = batch.Rekey(i + 1L, new BigInteger(i + 1), new BigInteger(batchSize + i + 1));
                if (!rekey.Changed)
                    throw new InvalidDataException($"Fixed-N scalar-8 explicit batch rekey failed for {i}.");
            }

            LibraDexGenericBatchCommitResult commit = batch.Commit();
            if (batch.ChangedRekeyCount != batchSize)
                throw new InvalidDataException($"Fixed-N scalar-8 explicit batch reported {batch.ChangedRekeyCount} rekeys, expected {batchSize}.");
            if (commit.StorageDiagnostics.FixedNScalar8DirtyShelves == 0)
                throw new InvalidDataException("Fixed-N scalar-8 explicit batch rekey did not report dirty FSN-8 shelves.");
        }

        stopwatch.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        long oldCount = CountBigIntScalar8(index, BigInteger.One, new BigInteger(batchSize));
        long actualCount = CountBigIntScalar8(index, new BigInteger(batchSize + 1), new BigInteger(batchSize * 2));
        long checksum = SumBigIntScalar8(index, new BigInteger(batchSize + 1), new BigInteger(batchSize * 2));
        if (oldCount != 0 || actualCount != batchSize)
            throw new InvalidDataException($"Fixed-N scalar-8 explicit batch rekey proof expected old=0/new={batchSize} but saw old={oldCount}/new={actualCount}.");

        return new FixedNBatchProofRow("fsn-8", "explicit-batch-rekey", batchSize, stopwatch.Elapsed, allocated, batchSize, actualCount, checksum);
    }

    private static FixedNBatchProofRow RunFixedNBatchProofScalar8BatchRekeyMany(int batchSize)
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        using LibraDexBigIntScalar8Index<long> index = catalog.Indexes["fixedn-batch-proof-rekey-many-8"]["score"].BigIntKeys<long>(maxBytes: 32).Create();
        WarmFixedNScalar8BatchShelf(index);
        LibraDexBigIntScalar8Rekey<long>[] rekeys = new LibraDexBigIntScalar8Rekey<long>[batchSize];
        for (int i = 0; i < batchSize; i++)
        {
            BigInteger oldKey = new(i + 1);
            BigInteger newKey = new(batchSize + i + 1);
            long identity = i + 1L;
            ValidateGenericInsert(index.Insert(oldKey, identity), $"fixed-N scalar-8 explicit bulk rekey seed {i}");
            rekeys[i] = new LibraDexBigIntScalar8Rekey<long>(identity, oldKey, newKey);
        }

        long before = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch stopwatch = Stopwatch.StartNew();
        using (LibraDexBigIntScalar8Batch<long> batch = index.BeginBatch())
        {
            long changed = batch.RekeyMany(rekeys);
            LibraDexGenericBatchCommitResult commit = batch.Commit();
            if (changed != batchSize || batch.ChangedRekeyCount != batchSize)
                throw new InvalidDataException($"Fixed-N scalar-8 explicit bulk rekey reported changed={changed}/batch={batch.ChangedRekeyCount}, expected {batchSize}.");
            if (commit.StorageDiagnostics.FixedNScalar8DirtyShelves == 0)
                throw new InvalidDataException("Fixed-N scalar-8 explicit bulk rekey did not report dirty FSN-8 shelves.");
        }

        stopwatch.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        long oldCount = CountBigIntScalar8(index, BigInteger.One, new BigInteger(batchSize));
        long actualCount = CountBigIntScalar8(index, new BigInteger(batchSize + 1), new BigInteger(batchSize * 2));
        long checksum = SumBigIntScalar8(index, new BigInteger(batchSize + 1), new BigInteger(batchSize * 2));
        if (oldCount != 0 || actualCount != batchSize)
            throw new InvalidDataException($"Fixed-N scalar-8 explicit bulk rekey proof expected old=0/new={batchSize} but saw old={oldCount}/new={actualCount}.");

        return new FixedNBatchProofRow("fsn-8", "explicit-bulk-rekey", batchSize, stopwatch.Elapsed, allocated, batchSize, actualCount, checksum);
    }

    private static FixedNBatchProofRow RunFixedNBatchProofScalar16BatchDelete(int batchSize)
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        using LibraDexBigIntScalar8Index<Guid> index = catalog.Indexes["fixedn-batch-proof-delete-16"]["score"].BigIntKeys<Guid>(maxBytes: 32).Create();
        WarmFixedNScalar16BatchShelf(index);
        for (int i = 0; i < batchSize; i++)
        {
            ValidateGenericInsert(index.Insert(new BigInteger(i + 1), CreateFixedNConcurrencyGuid(i + 1)), $"fixed-N scalar-16 explicit batch delete seed {i}");
        }

        long before = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch stopwatch = Stopwatch.StartNew();
        using (LibraDexBigIntScalar8Batch<Guid> batch = index.BeginBatch())
        {
            for (int i = 0; i < batchSize; i++)
            {
                LibraDexGenericDeleteResult delete = batch.Delete(new BigInteger(i + 1), CreateFixedNConcurrencyGuid(i + 1));
                if (!delete.Deleted)
                    throw new InvalidDataException($"Fixed-N scalar-16 explicit batch delete failed for {i}.");
            }

            LibraDexGenericBatchCommitResult commit = batch.Commit();
            if (batch.DeletedCount != batchSize)
                throw new InvalidDataException($"Fixed-N scalar-16 explicit batch reported {batch.DeletedCount} deletes, expected {batchSize}.");
            if (commit.StorageDiagnostics.FixedNScalar16DirtyShelves == 0)
                throw new InvalidDataException("Fixed-N scalar-16 explicit batch delete did not report dirty FSN-16 shelves.");
        }

        stopwatch.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        long actualCount = CountBigIntScalar16(index, BigInteger.Zero, new BigInteger(batchSize + 2));
        long checksum = SumFixedNConcurrencyGuidLow(index, BigInteger.Zero, new BigInteger(batchSize + 2));
        if (actualCount != 0)
            throw new InvalidDataException($"Fixed-N scalar-16 explicit batch delete proof expected 0 rows but saw {actualCount}.");

        return new FixedNBatchProofRow("fsn-16", "explicit-batch-delete", batchSize, stopwatch.Elapsed, allocated, 0, actualCount, checksum);
    }

    private static FixedNBatchProofRow RunFixedNBatchProofScalar16BatchDeleteMany(int batchSize)
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        using LibraDexBigIntScalar8Index<Guid> index = catalog.Indexes["fixedn-batch-proof-delete-many-16"]["score"].BigIntKeys<Guid>(maxBytes: 32).Create();
        WarmFixedNScalar16BatchShelf(index);
        (BigInteger Key, Guid Identity)[] deletes = new (BigInteger Key, Guid Identity)[batchSize];
        for (int i = 0; i < batchSize; i++)
        {
            BigInteger key = new(i + 1);
            Guid identity = CreateFixedNConcurrencyGuid(i + 1);
            ValidateGenericInsert(index.Insert(key, identity), $"fixed-N scalar-16 explicit bulk delete seed {i}");
            deletes[i] = (key, identity);
        }

        long before = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch stopwatch = Stopwatch.StartNew();
        using (LibraDexBigIntScalar8Batch<Guid> batch = index.BeginBatch())
        {
            long deleted = batch.DeleteMany(deletes);
            LibraDexGenericBatchCommitResult commit = batch.Commit();
            if (deleted != batchSize || batch.DeletedCount != batchSize)
                throw new InvalidDataException($"Fixed-N scalar-16 explicit bulk delete reported deleted={deleted}/batch={batch.DeletedCount}, expected {batchSize}.");
            if (commit.StorageDiagnostics.FixedNScalar16DirtyShelves == 0)
                throw new InvalidDataException("Fixed-N scalar-16 explicit bulk delete did not report dirty FSN-16 shelves.");
        }

        stopwatch.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        long actualCount = CountBigIntScalar16(index, BigInteger.Zero, new BigInteger(batchSize + 2));
        long checksum = SumFixedNConcurrencyGuidLow(index, BigInteger.Zero, new BigInteger(batchSize + 2));
        if (actualCount != 0)
            throw new InvalidDataException($"Fixed-N scalar-16 explicit bulk delete proof expected 0 rows but saw {actualCount}.");

        return new FixedNBatchProofRow("fsn-16", "explicit-bulk-delete", batchSize, stopwatch.Elapsed, allocated, 0, actualCount, checksum);
    }

    private static FixedNBatchProofRow RunFixedNBatchProofScalar16BatchRekey(int batchSize)
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        using LibraDexBigIntScalar8Index<Guid> index = catalog.Indexes["fixedn-batch-proof-rekey-16"]["score"].BigIntKeys<Guid>(maxBytes: 32).Create();
        WarmFixedNScalar16BatchShelf(index);
        for (int i = 0; i < batchSize; i++)
        {
            ValidateGenericInsert(index.Insert(new BigInteger(i + 1), CreateFixedNConcurrencyGuid(i + 1)), $"fixed-N scalar-16 explicit batch rekey seed {i}");
        }

        long before = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch stopwatch = Stopwatch.StartNew();
        using (LibraDexBigIntScalar8Batch<Guid> batch = index.BeginBatch())
        {
            for (int i = 0; i < batchSize; i++)
            {
                LibraDexGenericRekeyResult rekey = batch.Rekey(CreateFixedNConcurrencyGuid(i + 1), new BigInteger(i + 1), new BigInteger(batchSize + i + 1));
                if (!rekey.Changed)
                    throw new InvalidDataException($"Fixed-N scalar-16 explicit batch rekey failed for {i}.");
            }

            LibraDexGenericBatchCommitResult commit = batch.Commit();
            if (batch.ChangedRekeyCount != batchSize)
                throw new InvalidDataException($"Fixed-N scalar-16 explicit batch reported {batch.ChangedRekeyCount} rekeys, expected {batchSize}.");
            if (commit.StorageDiagnostics.FixedNScalar16DirtyShelves == 0)
                throw new InvalidDataException("Fixed-N scalar-16 explicit batch rekey did not report dirty FSN-16 shelves.");
        }

        stopwatch.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        long oldCount = CountBigIntScalar16(index, BigInteger.One, new BigInteger(batchSize));
        long actualCount = CountBigIntScalar16(index, new BigInteger(batchSize + 1), new BigInteger(batchSize * 2));
        long checksum = SumFixedNConcurrencyGuidLow(index, new BigInteger(batchSize + 1), new BigInteger(batchSize * 2));
        if (oldCount != 0 || actualCount != batchSize)
            throw new InvalidDataException($"Fixed-N scalar-16 explicit batch rekey proof expected old=0/new={batchSize} but saw old={oldCount}/new={actualCount}.");

        return new FixedNBatchProofRow("fsn-16", "explicit-batch-rekey", batchSize, stopwatch.Elapsed, allocated, batchSize, actualCount, checksum);
    }

    private static FixedNBatchProofRow RunFixedNBatchProofScalar16BatchRekeyMany(int batchSize)
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        using LibraDexBigIntScalar8Index<Guid> index = catalog.Indexes["fixedn-batch-proof-rekey-many-16"]["score"].BigIntKeys<Guid>(maxBytes: 32).Create();
        WarmFixedNScalar16BatchShelf(index);
        LibraDexBigIntScalar8Rekey<Guid>[] rekeys = new LibraDexBigIntScalar8Rekey<Guid>[batchSize];
        for (int i = 0; i < batchSize; i++)
        {
            BigInteger oldKey = new(i + 1);
            BigInteger newKey = new(batchSize + i + 1);
            Guid identity = CreateFixedNConcurrencyGuid(i + 1);
            ValidateGenericInsert(index.Insert(oldKey, identity), $"fixed-N scalar-16 explicit bulk rekey seed {i}");
            rekeys[i] = new LibraDexBigIntScalar8Rekey<Guid>(identity, oldKey, newKey);
        }

        long before = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch stopwatch = Stopwatch.StartNew();
        using (LibraDexBigIntScalar8Batch<Guid> batch = index.BeginBatch())
        {
            long changed = batch.RekeyMany(rekeys);
            LibraDexGenericBatchCommitResult commit = batch.Commit();
            if (changed != batchSize || batch.ChangedRekeyCount != batchSize)
                throw new InvalidDataException($"Fixed-N scalar-16 explicit bulk rekey reported changed={changed}/batch={batch.ChangedRekeyCount}, expected {batchSize}.");
            if (commit.StorageDiagnostics.FixedNScalar16DirtyShelves == 0)
                throw new InvalidDataException("Fixed-N scalar-16 explicit bulk rekey did not report dirty FSN-16 shelves.");
        }

        stopwatch.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        long oldCount = CountBigIntScalar16(index, BigInteger.One, new BigInteger(batchSize));
        long actualCount = CountBigIntScalar16(index, new BigInteger(batchSize + 1), new BigInteger(batchSize * 2));
        long checksum = SumFixedNConcurrencyGuidLow(index, new BigInteger(batchSize + 1), new BigInteger(batchSize * 2));
        if (oldCount != 0 || actualCount != batchSize)
            throw new InvalidDataException($"Fixed-N scalar-16 explicit bulk rekey proof expected old=0/new={batchSize} but saw old={oldCount}/new={actualCount}.");

        return new FixedNBatchProofRow("fsn-16", "explicit-bulk-rekey", batchSize, stopwatch.Elapsed, allocated, batchSize, actualCount, checksum);
    }

    private static void WarmFixedNScalar8BatchShelf(LibraDexBigIntScalar8Index<long> index)
    {
        ValidateGenericInsert(index.Insert(BigInteger.One, 0L), "fixed-N scalar-8 batch shelf warm insert");
        if (!index.Delete(BigInteger.One, 0L))
            throw new InvalidDataException("Fixed-N scalar-8 batch shelf warm delete failed.");
    }

    private static void WarmFixedNScalar16BatchShelf(LibraDexBigIntScalar8Index<Guid> index)
    {
        Guid identity = CreateFixedNConcurrencyGuid(0);
        ValidateGenericInsert(index.Insert(BigInteger.One, identity), "fixed-N scalar-16 batch shelf warm insert");
        if (!index.Delete(BigInteger.One, identity))
            throw new InvalidDataException("Fixed-N scalar-16 batch shelf warm delete failed.");
    }

    private static FixedNConcurrencyProofRow RunFixedNConcurrencyProofScalar8Single(int operations)
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        using LibraDexBigIntScalar8Index<long> index = catalog.Indexes["fixedn-proof-single-8"]["score"].BigIntKeys<long>(maxBytes: 32).Create();
        long before = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < operations; i++)
        {
            long identity = i + 1L;
            ValidateGenericInsert(index.Add(new BigInteger(i), identity), $"fixed-N scalar-8 single insert {i}");
        }

        for (int i = 0; i < operations / 4; i++)
        {
            if (!index.Delete(new BigInteger(i), i + 1L))
                throw new InvalidDataException($"Fixed-N scalar-8 single delete failed for {i}.");
        }

        int rekeyStart = operations / 4;
        int rekeyCount = operations / 4;
        for (int i = 0; i < rekeyCount; i++)
        {
            int source = rekeyStart + i;
            if (!index.Rekey(source + 1L, new BigInteger(source), new BigInteger(operations + source)))
                throw new InvalidDataException($"Fixed-N scalar-8 single rekey failed for {source}.");
        }

        stopwatch.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        long expectedCount = operations - (operations / 4);
        long actualCount = CountBigIntScalar8(index, BigInteger.Zero, new BigInteger(operations * 3L));
        long checksum = SumBigIntScalar8(index, BigInteger.Zero, new BigInteger(operations * 3L));
        if (actualCount != expectedCount)
            throw new InvalidDataException($"Fixed-N scalar-8 single expected {expectedCount} rows but saw {actualCount}.");

        return new FixedNConcurrencyProofRow("fsn-8", "single-mixed", 1, operations + (operations / 4) + rekeyCount, stopwatch.Elapsed, allocated, expectedCount, actualCount, checksum);
    }

    private static FixedNConcurrencyProofRow RunFixedNConcurrencyProofScalar16Single(int operations)
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        using LibraDexBigIntScalar8Index<Guid> index = catalog.Indexes["fixedn-proof-single-16"]["score"].BigIntKeys<Guid>(maxBytes: 32).Create();
        long before = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < operations; i++)
        {
            ValidateGenericInsert(index.Add(new BigInteger(i), CreateFixedNConcurrencyGuid(i + 1)), $"fixed-N scalar-16 single insert {i}");
        }

        for (int i = 0; i < operations / 4; i++)
        {
            if (!index.Delete(new BigInteger(i), CreateFixedNConcurrencyGuid(i + 1)))
                throw new InvalidDataException($"Fixed-N scalar-16 single delete failed for {i}.");
        }

        int rekeyStart = operations / 4;
        int rekeyCount = operations / 4;
        for (int i = 0; i < rekeyCount; i++)
        {
            int source = rekeyStart + i;
            if (!index.Rekey(CreateFixedNConcurrencyGuid(source + 1), new BigInteger(source), new BigInteger(operations + source)))
                throw new InvalidDataException($"Fixed-N scalar-16 single rekey failed for {source}.");
        }

        stopwatch.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        long expectedCount = operations - (operations / 4);
        long actualCount = CountBigIntScalar16(index, BigInteger.Zero, new BigInteger(operations * 3L));
        long checksum = SumFixedNConcurrencyGuidLow(index, BigInteger.Zero, new BigInteger(operations * 3L));
        if (actualCount != expectedCount)
            throw new InvalidDataException($"Fixed-N scalar-16 single expected {expectedCount} rows but saw {actualCount}.");

        return new FixedNConcurrencyProofRow("fsn-16", "single-mixed", 1, operations + (operations / 4) + rekeyCount, stopwatch.Elapsed, allocated, expectedCount, actualCount, checksum);
    }

    private static FixedNConcurrencyProofRow RunFixedNConcurrencyProofScalar8Multi(int threads, int opsPerThread)
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        using LibraDexBigIntScalar8Index<long> index = catalog.Indexes["fixedn-proof-multi-8"]["score"].BigIntKeys<long>(maxBytes: 32).Create();
        using ManualResetEventSlim startGate = new(false);
        using CountdownEvent readyGate = new(threads);
        Task[] tasks = new Task[threads];
        long before = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch stopwatch = Stopwatch.StartNew();
        for (int thread = 0; thread < threads; thread++)
        {
            int threadOrdinal = thread;
            tasks[thread] = Task.Run(() =>
            {
                readyGate.Signal();
                startGate.Wait();
                int baseKey = threadOrdinal * 1_000_000;
                for (int i = 0; i < opsPerThread; i++)
                {
                    long identity = baseKey + i + 1L;
                    ValidateGenericInsert(index.Add(new BigInteger(baseKey + i), identity), $"fixed-N scalar-8 multi insert {threadOrdinal}:{i}");
                }
            });
        }

        if (!readyGate.Wait(TimeSpan.FromSeconds(10)))
            throw new InvalidDataException("Fixed-N scalar-8 multi insert proof could not ready all callers.");
        startGate.Set();
        Task.WaitAll(tasks);
        stopwatch.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        long expectedCount = checked(threads * opsPerThread);
        long actualCount = CountBigIntScalar8(index, BigInteger.Zero, new BigInteger((threads + 1) * 1_000_000L));
        long checksum = SumBigIntScalar8(index, BigInteger.Zero, new BigInteger((threads + 1) * 1_000_000L));
        if (actualCount != expectedCount)
            throw new InvalidDataException($"Fixed-N scalar-8 multi expected {expectedCount} rows but saw {actualCount}.");

        return new FixedNConcurrencyProofRow("fsn-8", "multi-insert", threads, expectedCount, stopwatch.Elapsed, allocated, expectedCount, actualCount, checksum);
    }

    private static FixedNConcurrencyProofRow RunFixedNConcurrencyProofScalar16Multi(int threads, int opsPerThread)
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        using LibraDexBigIntScalar8Index<Guid> index = catalog.Indexes["fixedn-proof-multi-16"]["score"].BigIntKeys<Guid>(maxBytes: 32).Create();
        using ManualResetEventSlim startGate = new(false);
        using CountdownEvent readyGate = new(threads);
        Task[] tasks = new Task[threads];
        long before = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch stopwatch = Stopwatch.StartNew();
        for (int thread = 0; thread < threads; thread++)
        {
            int threadOrdinal = thread;
            tasks[thread] = Task.Run(() =>
            {
                readyGate.Signal();
                startGate.Wait();
                int baseKey = threadOrdinal * 1_000_000;
                for (int i = 0; i < opsPerThread; i++)
                {
                    ValidateGenericInsert(index.Add(new BigInteger(baseKey + i), CreateFixedNConcurrencyGuid(baseKey + i + 1)), $"fixed-N scalar-16 multi insert {threadOrdinal}:{i}");
                }
            });
        }

        if (!readyGate.Wait(TimeSpan.FromSeconds(10)))
            throw new InvalidDataException("Fixed-N scalar-16 multi insert proof could not ready all callers.");
        startGate.Set();
        Task.WaitAll(tasks);
        stopwatch.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        long expectedCount = checked(threads * opsPerThread);
        long actualCount = CountBigIntScalar16(index, BigInteger.Zero, new BigInteger((threads + 1) * 1_000_000L));
        long checksum = SumFixedNConcurrencyGuidLow(index, BigInteger.Zero, new BigInteger((threads + 1) * 1_000_000L));
        if (actualCount != expectedCount)
            throw new InvalidDataException($"Fixed-N scalar-16 multi expected {expectedCount} rows but saw {actualCount}.");

        return new FixedNConcurrencyProofRow("fsn-16", "multi-insert", threads, expectedCount, stopwatch.Elapsed, allocated, expectedCount, actualCount, checksum);
    }

    private static FixedNConcurrencyProofRow RunFixedNConcurrencyProofScalar8MultiMixed(int threads, int opsPerThread)
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        using LibraDexBigIntScalar8Index<long> index = catalog.Indexes["fixedn-proof-multi-mixed-8"]["score"].BigIntKeys<long>(maxBytes: 32).Create();
        int deleteCount = Math.Max(1, opsPerThread / 4);
        int rekeyCount = Math.Max(1, opsPerThread / 4);
        int seedPerThread = checked(deleteCount + rekeyCount + opsPerThread);
        for (int thread = 0; thread < threads; thread++)
        {
            int baseKey = thread * 1_000_000;
            for (int i = 0; i < seedPerThread; i++)
            {
                long identity = baseKey + i + 1L;
                ValidateGenericInsert(index.Add(new BigInteger(baseKey + i), identity), $"fixed-N scalar-8 mixed seed {thread}:{i}");
            }
        }

        using ManualResetEventSlim startGate = new(false);
        using CountdownEvent readyGate = new(threads);
        Task[] tasks = new Task[threads];
        long before = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch stopwatch = Stopwatch.StartNew();
        for (int thread = 0; thread < threads; thread++)
        {
            int threadOrdinal = thread;
            tasks[thread] = Task.Run(() =>
            {
                readyGate.Signal();
                startGate.Wait();
                int baseKey = threadOrdinal * 1_000_000;
                for (int i = 0; i < deleteCount; i++)
                {
                    if (!index.Delete(new BigInteger(baseKey + i), baseKey + i + 1L))
                        throw new InvalidDataException($"Fixed-N scalar-8 multi delete failed for {threadOrdinal}:{i}.");
                }

                for (int i = 0; i < rekeyCount; i++)
                {
                    int source = deleteCount + i;
                    if (!index.Rekey(baseKey + source + 1L, new BigInteger(baseKey + source), new BigInteger(baseKey + 500_000 + source)))
                        throw new InvalidDataException($"Fixed-N scalar-8 multi rekey failed for {threadOrdinal}:{i}.");
                }
            });
        }

        if (!readyGate.Wait(TimeSpan.FromSeconds(10)))
            throw new InvalidDataException("Fixed-N scalar-8 multi mixed proof could not ready all callers.");
        startGate.Set();
        Task.WaitAll(tasks);
        stopwatch.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        long expectedCount = checked((long)threads * (seedPerThread - deleteCount));
        long actualCount = CountBigIntScalar8(index, BigInteger.Zero, new BigInteger((threads + 1) * 1_000_000L));
        long checksum = SumBigIntScalar8(index, BigInteger.Zero, new BigInteger((threads + 1) * 1_000_000L));
        if (actualCount != expectedCount)
            throw new InvalidDataException($"Fixed-N scalar-8 multi mixed expected {expectedCount} rows but saw {actualCount}.");

        return new FixedNConcurrencyProofRow("fsn-8", "multi-delete-rekey", threads, checked((long)threads * (deleteCount + rekeyCount)), stopwatch.Elapsed, allocated, expectedCount, actualCount, checksum);
    }

    private static FixedNConcurrencyProofRow RunFixedNConcurrencyProofScalar16MultiMixed(int threads, int opsPerThread)
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        using LibraDexBigIntScalar8Index<Guid> index = catalog.Indexes["fixedn-proof-multi-mixed-16"]["score"].BigIntKeys<Guid>(maxBytes: 32).Create();
        int deleteCount = Math.Max(1, opsPerThread / 4);
        int rekeyCount = Math.Max(1, opsPerThread / 4);
        int seedPerThread = checked(deleteCount + rekeyCount + opsPerThread);
        for (int thread = 0; thread < threads; thread++)
        {
            int baseKey = thread * 1_000_000;
            for (int i = 0; i < seedPerThread; i++)
            {
                ValidateGenericInsert(index.Add(new BigInteger(baseKey + i), CreateFixedNConcurrencyGuid(baseKey + i + 1)), $"fixed-N scalar-16 mixed seed {thread}:{i}");
            }
        }

        using ManualResetEventSlim startGate = new(false);
        using CountdownEvent readyGate = new(threads);
        Task[] tasks = new Task[threads];
        long before = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch stopwatch = Stopwatch.StartNew();
        for (int thread = 0; thread < threads; thread++)
        {
            int threadOrdinal = thread;
            tasks[thread] = Task.Run(() =>
            {
                readyGate.Signal();
                startGate.Wait();
                int baseKey = threadOrdinal * 1_000_000;
                for (int i = 0; i < deleteCount; i++)
                {
                    if (!index.Delete(new BigInteger(baseKey + i), CreateFixedNConcurrencyGuid(baseKey + i + 1)))
                        throw new InvalidDataException($"Fixed-N scalar-16 multi delete failed for {threadOrdinal}:{i}.");
                }

                for (int i = 0; i < rekeyCount; i++)
                {
                    int source = deleteCount + i;
                    if (!index.Rekey(CreateFixedNConcurrencyGuid(baseKey + source + 1), new BigInteger(baseKey + source), new BigInteger(baseKey + 500_000 + source)))
                        throw new InvalidDataException($"Fixed-N scalar-16 multi rekey failed for {threadOrdinal}:{i}.");
                }
            });
        }

        if (!readyGate.Wait(TimeSpan.FromSeconds(10)))
            throw new InvalidDataException("Fixed-N scalar-16 multi mixed proof could not ready all callers.");
        startGate.Set();
        Task.WaitAll(tasks);
        stopwatch.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        long expectedCount = checked((long)threads * (seedPerThread - deleteCount));
        long actualCount = CountBigIntScalar16(index, BigInteger.Zero, new BigInteger((threads + 1) * 1_000_000L));
        long checksum = SumFixedNConcurrencyGuidLow(index, BigInteger.Zero, new BigInteger((threads + 1) * 1_000_000L));
        if (actualCount != expectedCount)
            throw new InvalidDataException($"Fixed-N scalar-16 multi mixed expected {expectedCount} rows but saw {actualCount}.");

        return new FixedNConcurrencyProofRow("fsn-16", "multi-delete-rekey", threads, checked((long)threads * (deleteCount + rekeyCount)), stopwatch.Elapsed, allocated, expectedCount, actualCount, checksum);
    }

    private static ConcurrencyPerformanceProofRow RunConcurrencyProofScalar8Scalar8Single(int operations, DataKernelBackingKind backingKind, string? path)
    {
        using Scalar8Scalar8Index index = Indexes.SS88.Create(
            path: path,
            backingKind: backingKind,
            name: "proof-ss88-single",
            options: CreateDesignPerfOptions(),
            telemetryOptions: DataKernelTelemetryOptions.EnabledOptions);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 0; i < operations; i++)
        {
            _ = index.InsertEncoded(CreateConcurrencyProofScalar8Key(i, stripe: 0x10), (ulong)(i + 1), allowDuplicateKeys: true);
        }

        watch.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        int actual = CountScalar8Scalar8(index, 0, ulong.MaxValue, out ulong checksum);
        return CreateConcurrencyProofRow("SS8-8", "single-insert", backingKind, 1, operations, watch.Elapsed, allocated, path, operations, actual, checksum);
    }

    private static ConcurrencyPerformanceProofRow RunConcurrencyProofVarKeyScalar8Single(int operations, DataKernelBackingKind backingKind, string? path)
    {
        using VarKeyScalar8Index index = Indexes.VS8.Create(
            path: path,
            backingKind: backingKind,
            name: "proof-vs8-single",
            maxKeyLength: 32,
            telemetryOptions: DataKernelTelemetryOptions.EnabledOptions);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 0; i < operations; i++)
        {
            _ = index.Insert(CreateConcurrencyProofVarKey(i), (ulong)(i + 1), allowDuplicateKeys: true);
        }

        watch.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        int actual = CountVarKeyScalar8(index, ConcurrencyProofFullLowerBound(), ConcurrencyProofFullUpperBound(index.Handle.MaxKeyLength), out ulong checksum);
        return CreateConcurrencyProofRow("VS8", "single-insert", backingKind, 1, operations, watch.Elapsed, allocated, path, operations, actual, checksum);
    }

    private static ConcurrencyPerformanceProofRow RunConcurrencyProofVarKeyScalar16Single(int operations, DataKernelBackingKind backingKind, string? path)
    {
        using VarKeyScalar16Index index = Indexes.VS16.Create(
            path: path,
            backingKind: backingKind,
            name: "proof-vs16-single",
            maxKeyLength: 32,
            telemetryOptions: DataKernelTelemetryOptions.EnabledOptions);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 0; i < operations; i++)
        {
            _ = index.Insert(CreateConcurrencyProofVarKey(i), encodedIdentityHigh: 0, encodedIdentityLow: (ulong)(i + 1), allowDuplicateKeys: true);
        }

        watch.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        int actual = CountVarKeyScalar16(index, ConcurrencyProofFullLowerBound(), ConcurrencyProofFullUpperBound(index.Handle.MaxKeyLength), out ulong checksum);
        return CreateConcurrencyProofRow("VS16", "single-insert", backingKind, 1, operations, watch.Elapsed, allocated, path, operations, actual, checksum);
    }

    private static ConcurrencyPerformanceProofRow RunConcurrencyProofScalar8VarIdentitySingle(int operations, DataKernelBackingKind backingKind, string? path)
    {
        using Scalar8VarIdentityIndex index = Indexes.SV8.Create(
            path: path,
            backingKind: backingKind,
            name: "proof-sv8-single",
            maxIdentityLength: 32,
            telemetryOptions: DataKernelTelemetryOptions.EnabledOptions);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 0; i < operations; i++)
        {
            _ = index.Insert(CreateConcurrencyProofScalar8Key(i, stripe: 0x20), CreateConcurrencyProofIdentity(i), allowDuplicateKeys: true);
        }

        watch.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        int actual = CountScalar8VarIdentity(index, 0, ulong.MaxValue, out ulong checksum);
        return CreateConcurrencyProofRow("SV8", "single-insert", backingKind, 1, operations, watch.Elapsed, allocated, path, operations, actual, checksum);
    }

    private static ConcurrencyPerformanceProofRow RunConcurrencyProofScalar16VarIdentitySingle(int operations, DataKernelBackingKind backingKind, string? path)
    {
        using Scalar16VarIdentityIndex index = Indexes.SV16.Create(
            path: path,
            backingKind: backingKind,
            name: "proof-sv16-single",
            maxIdentityLength: 32,
            telemetryOptions: DataKernelTelemetryOptions.EnabledOptions);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 0; i < operations; i++)
        {
            _ = index.Insert(0x3000_0000_0000_0000UL, (ulong)(i + 1), CreateConcurrencyProofIdentity(i), allowDuplicateKeys: true);
        }

        watch.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        int actual = CountScalar16VarIdentity(index, 0, 0, ulong.MaxValue, ulong.MaxValue, out ulong checksum);
        return CreateConcurrencyProofRow("SV16", "single-insert", backingKind, 1, operations, watch.Elapsed, allocated, path, operations, actual, checksum);
    }

    private static ConcurrencyPerformanceProofRow RunConcurrencyProofVarKeyVarIdentitySingle(int operations, DataKernelBackingKind backingKind, string? path)
    {
        using VarKeyVarIdentityIndex index = Indexes.VV.Create(
            path: path,
            backingKind: backingKind,
            name: "proof-vv-single",
            maxKeyLength: 32,
            maxIdentityLength: 32,
            telemetryOptions: DataKernelTelemetryOptions.EnabledOptions);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 0; i < operations; i++)
        {
            _ = index.Insert(CreateConcurrencyProofVarKey(i), CreateConcurrencyProofIdentity(i), allowDuplicateKeys: true);
        }

        watch.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        int actual = CountVarKeyVarIdentity(index, ConcurrencyProofFullLowerBound(), ConcurrencyProofFullUpperBound(index.MaxPhysicalKeyLength), out ulong checksum);
        return CreateConcurrencyProofRow("VV", "single-insert", backingKind, 1, operations, watch.Elapsed, allocated, path, operations, actual, checksum);
    }

    private static ConcurrencyPerformanceProofRow RunConcurrencyProofScalar8Scalar8Multi(int threads, int opsPerThread)
    {
        using Scalar8Scalar8Index index = Indexes.SS88.Create(
            backingKind: DataKernelBackingKind.Memory,
            name: $"proof-ss88-{threads}",
            options: CreateDesignPerfOptions(),
            telemetryOptions: DataKernelTelemetryOptions.EnabledOptions);
        RunConcurrencyProofWarmup(threads, worker => index.InsertEncoded(CreateConcurrencyProofScalar8Key(worker, worker + 1), (ulong)(worker + 1), allowDuplicateKeys: true));
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch watch = RunConcurrencyProofTasks(threads, opsPerThread, (worker, i) =>
        {
            int sequence = checked((worker * opsPerThread) + i + 1_000);
            _ = index.InsertEncoded(CreateConcurrencyProofScalar8Key(sequence, worker + 1), (ulong)(sequence + 1), allowDuplicateKeys: true);
        });
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        int expected = checked(threads + (threads * opsPerThread));
        int actual = CountScalar8Scalar8(index, 0, ulong.MaxValue, out ulong checksum);
        return CreateConcurrencyProofRow("SS8-8", "multi-insert", DataKernelBackingKind.Memory, threads, threads * opsPerThread, watch.Elapsed, allocated, null, expected, actual, checksum);
    }

    private static ConcurrencyPerformanceProofRow RunConcurrencyProofVarKeyScalar8Multi(int threads, int opsPerThread)
    {
        using VarKeyScalar8Index index = Indexes.VS8.Create(
            backingKind: DataKernelBackingKind.Memory,
            name: $"proof-vs8-{threads}",
            maxKeyLength: 32,
            telemetryOptions: DataKernelTelemetryOptions.EnabledOptions);
        RunConcurrencyProofWarmup(threads, worker => index.Insert(CreateConcurrencyProofVarKey(worker), (ulong)(worker + 1), allowDuplicateKeys: true));
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch watch = RunConcurrencyProofTasks(threads, opsPerThread, (worker, i) =>
        {
            int sequence = checked((worker * opsPerThread) + i + 1_000);
            _ = index.Insert(CreateConcurrencyProofVarKey(sequence), (ulong)(sequence + 1), allowDuplicateKeys: true);
        });
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        int expected = checked(threads + (threads * opsPerThread));
        int actual = CountVarKeyScalar8(index, ConcurrencyProofFullLowerBound(), ConcurrencyProofFullUpperBound(index.Handle.MaxKeyLength), out ulong checksum);
        return CreateConcurrencyProofRow("VS8", "multi-insert", DataKernelBackingKind.Memory, threads, threads * opsPerThread, watch.Elapsed, allocated, null, expected, actual, checksum);
    }

    private static ConcurrencyPerformanceProofRow RunConcurrencyProofVarKeyScalar16Multi(int threads, int opsPerThread)
    {
        using VarKeyScalar16Index index = Indexes.VS16.Create(
            backingKind: DataKernelBackingKind.Memory,
            name: $"proof-vs16-{threads}",
            maxKeyLength: 32,
            telemetryOptions: DataKernelTelemetryOptions.EnabledOptions);
        RunConcurrencyProofWarmup(threads, worker => index.Insert(CreateConcurrencyProofVarKey(worker), 0, (ulong)(worker + 1), allowDuplicateKeys: true));
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch watch = RunConcurrencyProofTasks(threads, opsPerThread, (worker, i) =>
        {
            int sequence = checked((worker * opsPerThread) + i + 1_000);
            _ = index.Insert(CreateConcurrencyProofVarKey(sequence), 0, (ulong)(sequence + 1), allowDuplicateKeys: true);
        });
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        int expected = checked(threads + (threads * opsPerThread));
        int actual = CountVarKeyScalar16(index, ConcurrencyProofFullLowerBound(), ConcurrencyProofFullUpperBound(index.Handle.MaxKeyLength), out ulong checksum);
        return CreateConcurrencyProofRow("VS16", "multi-insert", DataKernelBackingKind.Memory, threads, threads * opsPerThread, watch.Elapsed, allocated, null, expected, actual, checksum);
    }

    private static ConcurrencyPerformanceProofRow RunConcurrencyProofScalar8VarIdentityMulti(int threads, int opsPerThread)
    {
        using Scalar8VarIdentityIndex index = Indexes.SV8.Create(
            backingKind: DataKernelBackingKind.Memory,
            name: $"proof-sv8-{threads}",
            maxIdentityLength: 32,
            telemetryOptions: DataKernelTelemetryOptions.EnabledOptions);
        RunConcurrencyProofWarmup(threads, worker => index.Insert(CreateConcurrencyProofScalar8Key(worker, worker + 1), CreateConcurrencyProofIdentity(worker), allowDuplicateKeys: true));
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch watch = RunConcurrencyProofTasks(threads, opsPerThread, (worker, i) =>
        {
            int sequence = checked((worker * opsPerThread) + i + 1_000);
            _ = index.Insert(CreateConcurrencyProofScalar8Key(sequence, worker + 1), CreateConcurrencyProofIdentity(sequence), allowDuplicateKeys: true);
        });
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        int expected = checked(threads + (threads * opsPerThread));
        int actual = CountScalar8VarIdentity(index, 0, ulong.MaxValue, out ulong checksum);
        return CreateConcurrencyProofRow("SV8", "multi-insert", DataKernelBackingKind.Memory, threads, threads * opsPerThread, watch.Elapsed, allocated, null, expected, actual, checksum);
    }

    private static ConcurrencyPerformanceProofRow RunConcurrencyProofScalar16VarIdentityMulti(int threads, int opsPerThread)
    {
        using Scalar16VarIdentityIndex index = Indexes.SV16.Create(
            backingKind: DataKernelBackingKind.Memory,
            name: $"proof-sv16-{threads}",
            maxIdentityLength: 32,
            telemetryOptions: DataKernelTelemetryOptions.EnabledOptions);
        RunConcurrencyProofWarmup(threads, worker => index.Insert(CreateConcurrencyProofScalar16High(worker + 1), (ulong)(worker + 1), CreateConcurrencyProofIdentity(worker), allowDuplicateKeys: true));
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch watch = RunConcurrencyProofTasks(threads, opsPerThread, (worker, i) =>
        {
            int sequence = checked((worker * opsPerThread) + i + 1_000);
            _ = index.Insert(CreateConcurrencyProofScalar16High(worker + 1), (ulong)(sequence + 1), CreateConcurrencyProofIdentity(sequence), allowDuplicateKeys: true);
        });
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        int expected = checked(threads + (threads * opsPerThread));
        int actual = CountScalar16VarIdentity(index, 0, 0, ulong.MaxValue, ulong.MaxValue, out ulong checksum);
        return CreateConcurrencyProofRow("SV16", "multi-insert", DataKernelBackingKind.Memory, threads, threads * opsPerThread, watch.Elapsed, allocated, null, expected, actual, checksum);
    }

    private static ConcurrencyPerformanceProofRow RunConcurrencyProofVarKeyVarIdentityMulti(int threads, int opsPerThread)
    {
        using VarKeyVarIdentityIndex index = Indexes.VV.Create(
            backingKind: DataKernelBackingKind.Memory,
            name: $"proof-vv-{threads}",
            maxKeyLength: 32,
            maxIdentityLength: 32,
            telemetryOptions: DataKernelTelemetryOptions.EnabledOptions);
        RunConcurrencyProofWarmup(threads, worker => index.Insert(CreateConcurrencyProofVarKey(worker), CreateConcurrencyProofIdentity(worker), allowDuplicateKeys: true));
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch watch = RunConcurrencyProofTasks(threads, opsPerThread, (worker, i) =>
        {
            int sequence = checked((worker * opsPerThread) + i + 1_000);
            _ = index.Insert(CreateConcurrencyProofVarKey(sequence), CreateConcurrencyProofIdentity(sequence), allowDuplicateKeys: true);
        });
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        int expected = checked(threads + (threads * opsPerThread));
        int actual = CountVarKeyVarIdentity(index, ConcurrencyProofFullLowerBound(), ConcurrencyProofFullUpperBound(index.MaxPhysicalKeyLength), out ulong checksum);
        return CreateConcurrencyProofRow("VV", "multi-insert", DataKernelBackingKind.Memory, threads, threads * opsPerThread, watch.Elapsed, allocated, null, expected, actual, checksum);
    }

    private static Stopwatch RunConcurrencyProofTasks(int threads, int opsPerThread, Action<int, int> operation)
    {
        using ManualResetEventSlim start = new(false);
        Task[] tasks = new Task[threads];
        Exception? firstException = null;
        for (int worker = 0; worker < threads; worker++)
        {
            int capturedWorker = worker;
            tasks[worker] = Task.Run(() =>
            {
                start.Wait();
                try
                {
                    for (int i = 0; i < opsPerThread; i++)
                    {
                        operation(capturedWorker, i);
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref firstException, ex, null);
                    throw;
                }
            });
        }

        Stopwatch watch = Stopwatch.StartNew();
        start.Set();
        try
        {
            Task.WaitAll(tasks);
        }
        catch (AggregateException ex) when (firstException is not null)
        {
            throw new InvalidDataException("A concurrency performance proof worker failed.", ex);
        }

        watch.Stop();
        return watch;
    }

    private static void RunConcurrencyProofWarmup(int threads, Action<int> operation)
    {
        for (int i = 0; i < threads; i++)
        {
            operation(i);
        }
    }

    private static ConcurrencyPerformanceProofRow CreateConcurrencyProofRow(
        string shape,
        string mode,
        DataKernelBackingKind backingKind,
        int threads,
        int operations,
        TimeSpan elapsed,
        long allocatedBytes,
        string? path,
        int expectedCount,
        int actualCount,
        ulong checksum)
    {
        if (actualCount != expectedCount)
        {
            throw new InvalidDataException($"{shape} {mode} expected {expectedCount} rows but read back {actualCount}.");
        }

        long fileBytes = path is not null && File.Exists(path) ? new FileInfo(path).Length : 0;
        double seconds = Math.Max(elapsed.TotalSeconds, 0.000000001);
        return new ConcurrencyPerformanceProofRow(
            shape,
            mode,
            backingKind.ToString(),
            threads,
            operations,
            elapsed,
            operations / seconds,
            allocatedBytes,
            fileBytes,
            expectedCount,
            actualCount,
            checksum);
    }

    private static int CountScalar8Scalar8(Scalar8Scalar8Index index, ulong lowerKey, ulong upperKey, out ulong checksum)
    {
        using Scalar8Scalar8RangeReader reader = index.OpenEncodedRangeReader(lowerKey, upperKey);
        checksum = (ulong)reader.Count;
        return reader.Count;
    }

    private static int CountVarKeyScalar8(VarKeyScalar8Index index, ReadOnlySpan<byte> lowerKey, ReadOnlySpan<byte> upperKey, out ulong checksum)
    {
        using VarKeyScalar8RangeReader reader = index.OpenEncodedRangeReader(lowerKey, upperKey);
        checksum = (ulong)reader.Count;
        return reader.Count;
    }

    private static int CountVarKeyScalar16(VarKeyScalar16Index index, ReadOnlySpan<byte> lowerKey, ReadOnlySpan<byte> upperKey, out ulong checksum)
    {
        using VarKeyScalar16RangeReader reader = index.OpenEncodedRangeReader(lowerKey, upperKey);
        checksum = (ulong)reader.Count;
        return reader.Count;
    }

    private static int CountScalar8VarIdentity(Scalar8VarIdentityIndex index, ulong lowerKey, ulong upperKey, out ulong checksum)
    {
        using Scalar8VarIdentityRangeReader reader = index.OpenRangeReader(lowerKey, upperKey);
        checksum = (ulong)reader.Count;
        return reader.Count;
    }

    private static int CountScalar16VarIdentity(
        Scalar16VarIdentityIndex index,
        ulong lowerHigh,
        ulong lowerLow,
        ulong upperHigh,
        ulong upperLow,
        out ulong checksum)
    {
        using Scalar16VarIdentityRangeReader reader = index.OpenRangeReader(lowerHigh, lowerLow, upperHigh, upperLow);
        checksum = (ulong)reader.Count;
        return reader.Count;
    }

    private static int CountVarKeyVarIdentity(VarKeyVarIdentityIndex index, ReadOnlySpan<byte> lowerKey, ReadOnlySpan<byte> upperKey, out ulong checksum)
    {
        using VarKeyVarIdentityRangeReader reader = index.OpenEncodedRangeReader(lowerKey, upperKey);
        checksum = (ulong)reader.Count;
        return reader.Count;
    }

    private static int[] BuildConcurrencyProofThreadCounts(int maxThreads)
    {
        int boundedMax = Math.Clamp(maxThreads, 1, 64);
        List<int> counts = [];
        for (int value = 2; value <= boundedMax; value *= 2)
        {
            counts.Add(value);
        }

        if (counts.Count == 0)
        {
            counts.Add(1);
        }

        if (counts[^1] != boundedMax)
        {
            counts.Add(boundedMax);
        }

        return counts.ToArray();
    }

    private static byte[] CreateConcurrencyProofVarKey(int value)
    {
        return [(byte)(value >> 16), (byte)(value >> 8), (byte)value];
    }

    private static byte[] CreateConcurrencyProofIdentity(int value)
    {
        return [(byte)(value >> 16), (byte)(value >> 8), (byte)value, 0x5A];
    }

    private static ulong CreateConcurrencyProofScalar8Key(int value, int stripe)
    {
        return ((ulong)(byte)stripe << 56) | (uint)value;
    }

    /// <summary>
    /// Creates a public `long` key that encodes to the requested `SS8-8` proof stripe.<br/>
    /// The generic long codec xors signed values with `long.MinValue`, so this helper applies the inverse transform and lets generic proof rows target the same physical shelf stripes as encoded primitive rows.<br/>
    /// </summary>
    /// <param name="value">The low encoded value bits used inside the target stripe.<br/></param>
    /// <param name="stripe">The encoded high-byte stripe used to choose the physical shelf route.<br/></param>
    /// <returns>A developer-facing `long` value whose encoded form is the requested proof key.</returns>
    private static long CreateConcurrencyProofGenericLongKey(int value, int stripe)
    {
        return unchecked((long)(CreateConcurrencyProofScalar8Key(value, stripe) ^ 0x8000000000000000UL));
    }

    /// <summary>
    /// Creates a deterministic 32-byte key for projected `FS32-8` concurrency proof rows.<br/>
    /// Byte zero selects the primary shelf stripe, byte thirty-one selects the exact reversed projection shelf stripe, and the middle bytes carry the sequence value without changing either stripe.<br/>
    /// </summary>
    /// <param name="value">The sequence value encoded into the middle of the fixed key.<br/></param>
    /// <param name="primaryStripe">The first byte used by the primary route.<br/></param>
    /// <param name="projectionStripe">The last byte used by the reversed projection route.<br/></param>
    /// <returns>A new 32-byte key.</returns>
    private static byte[] CreateConcurrencyProofFixed32Key(int value, int primaryStripe, int projectionStripe)
    {
        byte[] key = new byte[32];
        key[0] = (byte)primaryStripe;
        key[24] = (byte)(value >> 24);
        key[25] = (byte)(value >> 16);
        key[26] = (byte)(value >> 8);
        key[27] = (byte)value;
        key[31] = (byte)projectionStripe;
        return key;
    }

    private static ulong CreateConcurrencyProofScalar16High(int stripe)
    {
        return (ulong)(byte)stripe << 56;
    }

    private static byte[] ConcurrencyProofFullLowerBound()
    {
        return [0x00];
    }

    private static byte[] ConcurrencyProofFullUpperBound(int maxKeyLength)
    {
        byte[] upper = new byte[maxKeyLength];
        upper.AsSpan().Fill(0xFF);
        return upper;
    }

    private static string CreateConcurrencyProofPath(string label)
    {
        Directory.CreateDirectory("artifacts");
        string path = Path.Combine("artifacts", $"concurrency-performance-proof-{label}-{DateTime.UtcNow:yyyyMMddHHmmssfff}.lbdx");
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return path;
    }

    private static string CreateConcurrencyProofOutputPath(string extension)
    {
        Directory.CreateDirectory("artifacts");
        return Path.Combine("artifacts", $"concurrency-performance-proof-{DateTime.UtcNow:yyyyMMddHHmmssfff}.{extension}");
    }

    private static long CountBigIntScalar8(LibraDexBigIntScalar8Index<long> index, BigInteger lower, BigInteger upper)
    {
        return index.GetIdentities(lower, upper).Count;
    }

    private static long SumBigIntScalar8(LibraDexBigIntScalar8Index<long> index, BigInteger lower, BigInteger upper)
    {
        IReadOnlyList<long> identities = index.GetIdentities(lower, upper);
        long checksum = 0;
        for (int i = 0; i < identities.Count; i++)
        {
            checksum = unchecked((checksum * 397) ^ identities[i]);
        }

        return checksum;
    }

    private static long CountBigIntScalar16(LibraDexBigIntScalar8Index<Guid> index, BigInteger lower, BigInteger upper)
    {
        return index.GetIdentities(lower, upper).Count;
    }

    private static long SumFixedNConcurrencyGuidLow(LibraDexBigIntScalar8Index<Guid> index, BigInteger lower, BigInteger upper)
    {
        IReadOnlyList<Guid> identities = index.GetIdentities(lower, upper);
        long checksum = 0;
        for (int i = 0; i < identities.Count; i++)
        {
            checksum = unchecked((checksum * 397) ^ ReadFixedNConcurrencyGuidLow(identities[i]));
        }

        return checksum;
    }

    private static Guid CreateFixedNConcurrencyGuid(int value)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteInt32BigEndian(bytes.Slice(12, sizeof(int)), value);
        return new Guid(bytes);
    }

    private static int ReadFixedNConcurrencyGuidLow(Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        value.TryWriteBytes(bytes);
        return BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(12, sizeof(int)));
    }

    /// <summary>
    /// Writes the concurrency performance proof rows as a stable CSV artifact.<br/>
    /// The CSV is intentionally simple because its primary job is to become a future `--baseline` input for drift checks.<br/>
    /// </summary>
    /// <param name="path">The destination CSV path.<br/></param>
    /// <param name="rows">The measured rows to persist.<br/></param>
    private static void WriteConcurrencyProofCsv(string path, IReadOnlyList<ConcurrencyPerformanceProofRow> rows)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        StringBuilder builder = new();
        builder.AppendLine("shape,mode,backing,threads,ops,elapsed_ms,ops_sec,alloc_bytes,file_bytes,expected_count,actual_count,checksum");
        for (int i = 0; i < rows.Count; i++)
        {
            ConcurrencyPerformanceProofRow row = rows[i];
            builder.Append(row.Shape).Append(',');
            builder.Append(row.Mode).Append(',');
            builder.Append(row.Backing).Append(',');
            builder.Append(row.Threads.ToString(CultureInfo.InvariantCulture)).Append(',');
            builder.Append(row.Operations.ToString(CultureInfo.InvariantCulture)).Append(',');
            builder.Append(row.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture)).Append(',');
            builder.Append(row.OperationsPerSecond.ToString("F2", CultureInfo.InvariantCulture)).Append(',');
            builder.Append(row.AllocatedBytes.ToString(CultureInfo.InvariantCulture)).Append(',');
            builder.Append(row.FileBytes.ToString(CultureInfo.InvariantCulture)).Append(',');
            builder.Append(row.ExpectedCount.ToString(CultureInfo.InvariantCulture)).Append(',');
            builder.Append(row.ActualCount.ToString(CultureInfo.InvariantCulture)).Append(',');
            builder.AppendLine(row.Checksum.ToString(CultureInfo.InvariantCulture));
        }

        File.WriteAllText(path, builder.ToString());
    }

    /// <summary>
    /// Writes the concurrency performance proof rows as a markdown artifact for quick human review.<br/>
    /// This is intentionally paired with the CSV so a run can be both diffed by humans and consumed by the baseline comparator.<br/>
    /// </summary>
    /// <param name="path">The destination markdown path.<br/></param>
    /// <param name="args">The command-line arguments that produced the run.<br/></param>
    /// <param name="rows">The measured rows to persist.<br/></param>
    private static void WriteConcurrencyProofMarkdown(string path, string[] args, IReadOnlyList<ConcurrencyPerformanceProofRow> rows)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        StringBuilder builder = new();
        builder.AppendLine("# concurrency-performance-proof");
        builder.AppendLine();
        builder.Append("Generated UTC: ").AppendLine(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        builder.Append("Arguments: `").Append(string.Join(' ', args)).AppendLine("`");
        builder.AppendLine();
        builder.AppendLine("| shape | mode | backing | threads | ops | elapsed-ms | ops-sec | alloc-bytes | file-bytes | expected | actual | checksum |");
        builder.AppendLine("|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        for (int i = 0; i < rows.Count; i++)
        {
            ConcurrencyPerformanceProofRow row = rows[i];
            builder.Append("| ").Append(row.Shape);
            builder.Append(" | ").Append(row.Mode);
            builder.Append(" | ").Append(row.Backing);
            builder.Append(" | ").Append(row.Threads.ToString(CultureInfo.InvariantCulture));
            builder.Append(" | ").Append(row.Operations.ToString(CultureInfo.InvariantCulture));
            builder.Append(" | ").Append(row.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture));
            builder.Append(" | ").Append(row.OperationsPerSecond.ToString("F2", CultureInfo.InvariantCulture));
            builder.Append(" | ").Append(row.AllocatedBytes.ToString(CultureInfo.InvariantCulture));
            builder.Append(" | ").Append(row.FileBytes.ToString(CultureInfo.InvariantCulture));
            builder.Append(" | ").Append(row.ExpectedCount.ToString(CultureInfo.InvariantCulture));
            builder.Append(" | ").Append(row.ActualCount.ToString(CultureInfo.InvariantCulture));
            builder.Append(" | ").Append(row.Checksum.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine(" |");
        }

        File.WriteAllText(path, builder.ToString());
    }

    /// <summary>
    /// Compares current concurrency performance proof rows against a previous CSV baseline.<br/>
    /// The comparator fails only on matched rows, so callers can add shapes or thread counts without forcing an immediate baseline reset.<br/>
    /// </summary>
    /// <param name="baselinePath">The prior CSV path to compare against.<br/></param>
    /// <param name="rows">The current measured rows.<br/></param>
    /// <param name="maxSlowdownPercent">The tolerated operations-per-second drop for matching rows.<br/></param>
    /// <param name="maxAllocationGrowthPercent">The tolerated allocated-byte growth for matching rows.<br/></param>
    /// <param name="maxFileGrowthPercent">The tolerated file-byte growth for matching file-backed rows.<br/></param>
    /// <param name="minThroughputCompareMs">The minimum baseline and current row duration required before throughput drift is considered meaningful.<br/></param>
    private static void CompareConcurrencyProofBaseline(
        string baselinePath,
        IReadOnlyList<ConcurrencyPerformanceProofRow> rows,
        double maxSlowdownPercent,
        double maxAllocationGrowthPercent,
        double maxFileGrowthPercent,
        double minThroughputCompareMs)
    {
        Dictionary<string, ConcurrencyPerformanceProofRow> baseline = ReadConcurrencyProofBaselineCsv(baselinePath);
        List<string> failures = [];
        int matched = 0;
        int skippedThroughput = 0;

        for (int i = 0; i < rows.Count; i++)
        {
            ConcurrencyPerformanceProofRow row = rows[i];
            if (!baseline.TryGetValue(CreateConcurrencyProofBaselineKey(row), out ConcurrencyPerformanceProofRow prior))
            {
                continue;
            }

            matched++;
            if (prior.Elapsed.TotalMilliseconds < minThroughputCompareMs ||
                row.Elapsed.TotalMilliseconds < minThroughputCompareMs)
            {
                skippedThroughput++;
            }
            else
            {
                double slowdownPercent = CreateConcurrencyProofDropPercent(prior.OperationsPerSecond, row.OperationsPerSecond);
                if (slowdownPercent > maxSlowdownPercent)
                {
                    failures.Add($"{CreateConcurrencyProofBaselineKey(row)} ops/sec slowed by {slowdownPercent:F2}% ({prior.OperationsPerSecond:F2} -> {row.OperationsPerSecond:F2}).");
                }
            }

            double allocationGrowthPercent = CreateConcurrencyProofGrowthPercent(prior.AllocatedBytes, row.AllocatedBytes);
            if (allocationGrowthPercent > maxAllocationGrowthPercent)
            {
                failures.Add($"{CreateConcurrencyProofBaselineKey(row)} allocations grew by {allocationGrowthPercent:F2}% ({prior.AllocatedBytes} -> {row.AllocatedBytes}).");
            }

            if (prior.FileBytes > 0 && row.FileBytes > 0)
            {
                double fileGrowthPercent = CreateConcurrencyProofGrowthPercent(prior.FileBytes, row.FileBytes);
                if (fileGrowthPercent > maxFileGrowthPercent)
                {
                    failures.Add($"{CreateConcurrencyProofBaselineKey(row)} file bytes grew by {fileGrowthPercent:F2}% ({prior.FileBytes} -> {row.FileBytes}).");
                }
            }
        }

        if (matched == 0)
        {
            throw new InvalidDataException($"Concurrency performance baseline '{baselinePath}' did not match any current rows.");
        }

        if (failures.Count != 0)
        {
            throw new InvalidDataException("Concurrency performance baseline comparison failed:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
        }

        Console.WriteLine($"baseline={Path.GetFullPath(baselinePath)} matched-rows={matched} throughput-skipped-rows={skippedThroughput}");
    }

    private static Dictionary<string, ConcurrencyPerformanceProofRow> ReadConcurrencyProofBaselineCsv(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Concurrency performance baseline file was not found.", path);
        }

        Dictionary<string, ConcurrencyPerformanceProofRow> rows = new(StringComparer.Ordinal);
        string[] lines = File.ReadAllLines(path);
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            string[] parts = line.Split(',');
            if (parts.Length != 12)
            {
                throw new InvalidDataException($"Concurrency performance baseline row {i + 1} expected 12 columns but found {parts.Length}.");
            }

            ConcurrencyPerformanceProofRow row = new(
                parts[0],
                parts[1],
                parts[2],
                int.Parse(parts[3], CultureInfo.InvariantCulture),
                int.Parse(parts[4], CultureInfo.InvariantCulture),
                TimeSpan.FromMilliseconds(double.Parse(parts[5], CultureInfo.InvariantCulture)),
                double.Parse(parts[6], CultureInfo.InvariantCulture),
                long.Parse(parts[7], CultureInfo.InvariantCulture),
                long.Parse(parts[8], CultureInfo.InvariantCulture),
                int.Parse(parts[9], CultureInfo.InvariantCulture),
                int.Parse(parts[10], CultureInfo.InvariantCulture),
                ulong.Parse(parts[11], CultureInfo.InvariantCulture));
            rows[CreateConcurrencyProofBaselineKey(row)] = row;
        }

        return rows;
    }

    private static string CreateConcurrencyProofBaselineKey(ConcurrencyPerformanceProofRow row)
    {
        return string.Concat(row.Shape, "|", row.Mode, "|", row.Backing, "|", row.Threads.ToString(CultureInfo.InvariantCulture), "|", row.Operations.ToString(CultureInfo.InvariantCulture));
    }

    private static double CreateConcurrencyProofDropPercent(double baseline, double current)
    {
        if (baseline <= 0 || current >= baseline)
        {
            return 0;
        }

        return ((baseline - current) / baseline) * 100;
    }

    private static double CreateConcurrencyProofGrowthPercent(long baseline, long current)
    {
        if (baseline <= 0 || current <= baseline)
        {
            return 0;
        }

        return ((double)(current - baseline) / baseline) * 100;
    }

    private readonly record struct ConcurrencyPerformanceProofRow(
        string Shape,
        string Mode,
        string Backing,
        int Threads,
        int Operations,
        TimeSpan Elapsed,
        double OperationsPerSecond,
        long AllocatedBytes,
        long FileBytes,
        int ExpectedCount,
        int ActualCount,
        ulong Checksum);

    private readonly record struct Scalar8Scalar8PrimitiveConcurrencyProofRow(
        string Scenario,
        int Threads,
        int Operations,
        TimeSpan Elapsed,
        double OperationsPerSecond,
        long AllocatedBytes,
        long ExpectedCount,
        long ActualCount,
        string Note);

    private readonly record struct FixedNConcurrencyProofRow(
        string Shape,
        string Mode,
        int Threads,
        long Operations,
        TimeSpan Elapsed,
        long AllocatedBytes,
        long ExpectedCount,
        long ActualCount,
        long Checksum);

    private readonly record struct FixedNBatchProofRow(
        string Shape,
        string Mode,
        int BatchSize,
        TimeSpan Elapsed,
        long AllocatedBytes,
        long ExpectedCount,
        long ActualCount,
        long Checksum);

    /// <summary>
    /// Measures the common Abraxas pattern where two warmed indexes in the same identity group receive overlapping writes.<br/>
    /// Expected result: both inserts use writer-context staging because each named index has an initialized local route and no shared shelf contention.<br/>
    /// </summary>
    private static ConcurrencyWorkloadMatrixRow RunWorkloadMatrixDifferentIndexWarm()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        CatalogIdentityGroupIndexes group = catalog.Indexes["matrix-different-index"];
        using LibraDexIndex<long, long> value = group["value"].Int64Keys<long>().Create();
        using LibraDexIndex<long, long> status = group["status"].Int64Keys<long>().Create();
        _ = value.Insert(100, 1000);
        _ = status.Insert(200, 2000);

        LibraDexQueuedWriter<long, long> valueWriter = value.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);
        LibraDexQueuedWriter<long, long> statusWriter = status.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);
        (LibraDexGenericInsertResult valueResult, LibraDexGenericInsertResult statusResult) = RunOverlappingGenericQueuedInserts(
            () => valueWriter.Insert(101, 1001),
            () => statusWriter.Insert(201, 2001),
            "matrix different-index warm");

        ConcurrencyWorkloadMatrixRow row = BuildGenericMatrixRow(
            "different-index-warm",
            "same catalog/group; independent named indexes",
            valueResult,
            statusResult);
        RequireMatrixPath(row, writerContext: 2, serializedFallback: 0);

        long[] identities = new long[4];
        LibraDexGenericRangeReadResult valueRead = value.ReadRange(100, 101, identities);
        if (valueRead.IdentityCount != 2 || identities[0] != 1000 || identities[1] != 1001)
        {
            throw new InvalidDataException("Concurrency workload matrix different-index value readback failed.");
        }

        Array.Clear(identities);
        LibraDexGenericRangeReadResult statusRead = status.ReadRange(200, 201, identities);
        if (statusRead.IdentityCount != 2 || identities[0] != 2000 || identities[1] != 2001)
        {
            throw new InvalidDataException("Concurrency workload matrix different-index status readback failed.");
        }

        return row;
    }

    /// <summary>
    /// Measures one warmed index receiving overlapping writes into two already initialized root-prefix shelves.<br/>
    /// Expected result: both inserts use writer-context staging because route topology does not change and shelf ownership is independent.<br/>
    /// </summary>
    private static ConcurrencyWorkloadMatrixRow RunWorkloadMatrixSameIndexDifferentShelfWarm()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        using LibraDexIndex<long, long> index = catalog.Indexes["matrix-same-index"]["range"].Int64Keys<long>().Create();
        _ = index.Insert(100, 1000);
        _ = index.Insert(long.MinValue + 100, 2000);
        LibraDexQueuedWriter<long, long> writer = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);

        (LibraDexGenericInsertResult leftResult, LibraDexGenericInsertResult rightResult) = RunOverlappingGenericQueuedInserts(
            () => writer.Insert(101, 1001),
            () => writer.Insert(long.MinValue + 101, 2001),
            "matrix same-index different-shelf warm");

        ConcurrencyWorkloadMatrixRow row = BuildGenericMatrixRow(
            "same-index-different-shelf",
            "one index; warmed independent shelves",
            leftResult,
            rightResult);
        RequireMatrixPath(row, writerContext: 2, serializedFallback: 0);

        long[] identities = new long[4];
        LibraDexGenericRangeReadResult leftRead = index.ReadRange(100, 101, identities);
        if (leftRead.IdentityCount != 2 || identities[0] != 1000 || identities[1] != 1001)
        {
            throw new InvalidDataException("Concurrency workload matrix same-index positive-range readback failed.");
        }

        Array.Clear(identities);
        LibraDexGenericRangeReadResult rightRead = index.ReadRange(long.MinValue + 100, long.MinValue + 101, identities);
        if (rightRead.IdentityCount != 2 || identities[0] != 2000 || identities[1] != 2001)
        {
            throw new InvalidDataException("Concurrency workload matrix same-index min-range readback failed.");
        }

        return row;
    }

    /// <summary>
    /// Measures one warmed index receiving overlapping writes into the same ordinary shelf.<br/>
    /// Expected result: both inserts still complete through writer-context staging, but same-shelf ownership contention may wait and retry inside the queued writer.<br/>
    /// </summary>
    private static ConcurrencyWorkloadMatrixRow RunWorkloadMatrixSameIndexSameShelfWarm()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        using LibraDexIndex<long, long> index = catalog.Indexes["matrix-same-shelf"]["range"].Int64Keys<long>().Create();
        _ = index.Insert(100, 1000);
        LibraDexQueuedWriter<long, long> writer = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);

        (LibraDexGenericInsertResult firstResult, LibraDexGenericInsertResult secondResult) = RunOverlappingGenericQueuedInserts(
            () => writer.Insert(101, 1001),
            () => writer.Insert(102, 1002),
            "matrix same-index same-shelf warm");

        ConcurrencyWorkloadMatrixRow row = BuildGenericMatrixRow(
            "same-index-same-shelf",
            "same shelf; queued writer owns retry/wait",
            firstResult,
            secondResult);
        RequireMatrixPath(row, writerContext: 2, serializedFallback: 0);

        long[] identities = new long[4];
        LibraDexGenericRangeReadResult read = index.ReadRange(100, 102, identities);
        if (read.IdentityCount != 3 || identities[0] != 1000 || identities[1] != 1001 || identities[2] != 1002)
        {
            throw new InvalidDataException("Concurrency workload matrix same-shelf readback failed.");
        }

        return row;
    }

    /// <summary>
    /// Measures a first write into a missing root-prefix route through the Abraxas-facing generic queued writer.<br/>
    /// Expected result: the insert uses serialized fallback because it must allocate and link a new routed shelf before the tuple can be inserted.<br/>
    /// </summary>
    private static ConcurrencyWorkloadMatrixRow RunWorkloadMatrixColdRouteFallback()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        using LibraDexIndex<long, long> index = catalog.Indexes["matrix-cold-route"]["range"].Int64Keys<long>().Create();
        _ = index.Insert(100, 1000);
        LibraDexQueuedWriter<long, long> writer = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);
        LibraDexGenericInsertResult result = writer.Insert(long.MinValue + 300, 3000);

        ConcurrencyWorkloadMatrixRow row = BuildGenericMatrixRow(
            "cold-route-first-write",
            "missing root prefix initializes topology",
            result);
        RequireMatrixPath(row, writerContext: 0, serializedFallback: 0, narrowTopology: 1);
        if (!result.CreatedInitialShelfRoute)
        {
            throw new InvalidDataException("Concurrency workload matrix cold-route fallback did not report route creation.");
        }

        long[] identities = new long[2];
        LibraDexGenericRangeReadResult read = index.ReadRange(long.MinValue + 300, long.MinValue + 300, identities);
        if (read.IdentityCount != 1 || identities[0] != 3000)
        {
            throw new InvalidDataException("Concurrency workload matrix cold-route readback failed.");
        }

        return row;
    }

    /// <summary>
    /// Measures a full ordinary shelf insert that must split or transform route topology before it can publish.<br/>
    /// Expected result: the queued writer uses serialized fallback because writer-context staging is intentionally limited to no-split local shelf mutation.<br/>
    /// </summary>
    private static ConcurrencyWorkloadMatrixRow RunWorkloadMatrixSplitFallback()
    {
        using Scalar8Scalar8Index index = Indexes.SS88.Create(
            backingKind: DataKernelBackingKind.Memory,
            name: "matrix-split-fallback-ss88",
            options: CreateDesignPerfOptions(),
            developerMetadata: CreateDesignPerfMetadata(9920),
            telemetryOptions: DataKernelTelemetryOptions.FromLevel(LibraDexDiagnosticsLevel.Detailed));

        Scalar8Scalar8Profile profile = index.Handle.Profile;
        const ulong baseKey = 0x3500_0000_0000_0000UL;
        for (int i = 0; i < profile.MaxItemCount; i++)
        {
            Scalar8Scalar8EncodedInsertResult fill = index.InsertEncoded(baseKey + (ulong)i, baseKey + (ulong)i);
            if (fill.Outcome != Scalar8Scalar8EncodedInsertOutcome.Inserted)
            {
                throw new InvalidDataException($"Concurrency workload matrix split fixture failed at item {i}; outcome={fill.Outcome}.");
            }
        }

        Scalar8Scalar8QueuedWriter writer = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);
        Scalar8Scalar8EncodedInsertResult result = writer.InsertEncoded(baseKey + (ulong)profile.MaxItemCount, baseKey + (ulong)profile.MaxItemCount);
        ConcurrencyWorkloadMatrixRow row = BuildEncodedMatrixRow(
            "ordinary-shelf-full",
            "full shelf requires split/topology publish",
            result);
        RequireMatrixPath(row, writerContext: 0, serializedFallback: 0, narrowTopology: 1);

        ulong[] identities = new ulong[profile.MaxItemCount + 1];
        Scalar8Scalar8EncodedRangeReadResult read = index.ReadEncodedRange(baseKey, baseKey + (ulong)profile.MaxItemCount, identities);
        if (read.IdentityCount != profile.MaxItemCount + 1 || identities[profile.MaxItemCount] != baseKey + (ulong)profile.MaxItemCount)
        {
            throw new InvalidDataException("Concurrency workload matrix split fallback readback failed.");
        }

        return row;
    }

    /// <summary>
    /// Measures a full same-key duplicate run where the next identity requires terminal-root conversion or linked topology work.<br/>
    /// Expected result: the queued writer uses serialized fallback because the route target shape changes before future terminal-local writes can be isolated.<br/>
    /// </summary>
    private static ConcurrencyWorkloadMatrixRow RunWorkloadMatrixDuplicateOverflowFallback()
    {
        using Scalar8Scalar8Index index = Indexes.SS88.Create(
            backingKind: DataKernelBackingKind.Memory,
            name: "matrix-duplicate-overflow-ss88",
            options: CreateDesignPerfOptions(),
            developerMetadata: CreateDesignPerfMetadata(9921),
            telemetryOptions: DataKernelTelemetryOptions.FromLevel(LibraDexDiagnosticsLevel.Detailed));

        Scalar8Scalar8Profile profile = index.Handle.Profile;
        const ulong duplicateKey = 0x3600_0000_0000_0001UL;
        for (int i = 0; i < profile.MaxItemCount; i++)
        {
            Scalar8Scalar8EncodedInsertResult fill = index.InsertEncoded(duplicateKey, (ulong)(360_000 + i), allowDuplicateKeys: true);
            if (fill.Outcome != Scalar8Scalar8EncodedInsertOutcome.Inserted)
            {
                throw new InvalidDataException($"Concurrency workload matrix duplicate fixture failed at item {i}; outcome={fill.Outcome}.");
            }
        }

        Scalar8Scalar8QueuedWriter writer = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);
        ulong overflowIdentity = 360_000UL + (ulong)profile.MaxItemCount;
        Scalar8Scalar8EncodedInsertResult result = writer.InsertEncoded(duplicateKey, overflowIdentity, allowDuplicateKeys: true);
        ConcurrencyWorkloadMatrixRow row = BuildEncodedMatrixRow(
            "duplicate-run-overflow",
            "same-key overflow creates/links terminal topology",
            result);
        RequireMatrixPath(row, writerContext: 0, serializedFallback: 0, narrowTopology: 1);

        ulong[] identities = new ulong[profile.MaxItemCount + 1];
        Scalar8Scalar8EncodedRangeReadResult read = index.ReadEncodedRange(duplicateKey, duplicateKey, identities);
        if (read.IdentityCount != profile.MaxItemCount + 1 || identities[profile.MaxItemCount] != overflowIdentity)
        {
            throw new InvalidDataException("Concurrency workload matrix duplicate overflow readback failed.");
        }

        return row;
    }

    /// <summary>
    /// Measures writes into an existing terminal identity root with spare capacity after topology has already been established.<br/>
    /// Expected result: both append and sorted middle insert use writer-context staging because only the owned terminal identity shelf bytes change.<br/>
    /// </summary>
    private static ConcurrencyWorkloadMatrixRow RunWorkloadMatrixTerminalIdentityLocal()
    {
        using Scalar8Scalar8Index index = Indexes.SS88.Create(
            backingKind: DataKernelBackingKind.Memory,
            name: "matrix-terminal-local-ss88",
            options: CreateDesignPerfOptions(),
            developerMetadata: CreateDesignPerfMetadata(9922),
            telemetryOptions: DataKernelTelemetryOptions.FromLevel(LibraDexDiagnosticsLevel.Detailed));

        Scalar8Scalar8Profile profile = index.Handle.Profile;
        const ulong duplicateKey = 0x3700_0000_0000_0001UL;
        for (int i = 0; i < profile.MaxItemCount; i++)
        {
            Scalar8Scalar8EncodedInsertResult fill = index.InsertEncoded(duplicateKey, (ulong)(370_000 + i), allowDuplicateKeys: true);
            if (fill.Outcome != Scalar8Scalar8EncodedInsertOutcome.Inserted)
            {
                throw new InvalidDataException($"Concurrency workload matrix terminal fixture failed at item {i}; outcome={fill.Outcome}.");
            }
        }

        Scalar8Scalar8QueuedWriter writer = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);
        ulong topologyIdentity = 370_000UL + (ulong)profile.MaxItemCount;
        Scalar8Scalar8EncodedInsertResult topology = writer.InsertEncoded(duplicateKey, topologyIdentity, allowDuplicateKeys: true);
        if (topology.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.NarrowTopologyPublisher)
        {
            throw new InvalidDataException("Concurrency workload matrix terminal fixture did not create terminal topology through fallback.");
        }

        Scalar8Scalar8EncodedInsertResult append = writer.InsertEncoded(duplicateKey, topologyIdentity + 1, allowDuplicateKeys: true);
        Scalar8Scalar8EncodedInsertResult middle = writer.InsertEncoded(duplicateKey, 369_999, allowDuplicateKeys: true);
        ConcurrencyWorkloadMatrixRow row = BuildEncodedMatrixRow(
            "terminal-identity-local",
            "terminal exists; owned shelf bytes only",
            append,
            middle);
        RequireMatrixPath(row, writerContext: 2, serializedFallback: 0);

        ulong[] identities = new ulong[profile.MaxItemCount + 3];
        Scalar8Scalar8EncodedRangeReadResult read = index.ReadEncodedRange(duplicateKey, duplicateKey, identities);
        if (read.IdentityCount != profile.MaxItemCount + 3 ||
            identities[0] != 369_999 ||
            identities[profile.MaxItemCount + 1] != topologyIdentity ||
            identities[profile.MaxItemCount + 2] != topologyIdentity + 1)
        {
            throw new InvalidDataException("Concurrency workload matrix terminal-local readback failed.");
        }

        return row;
    }

    /// <summary>
    /// Measures exact deletes from an existing terminal identity root with one owned terminal shelf rewrite.<br/>
    /// Expected result: terminal identity exact deletes use writer-context staging when the mutation removes an identity from one existing terminal shelf without route cleanup or chain relink.<br/>
    /// </summary>
    private static ConcurrencyWorkloadMatrixRow RunWorkloadMatrixTerminalIdentityDeleteLocal()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        using LibraDexIndex<long, long> index = catalog.Indexes["matrix-terminal-delete"]["ids"].Int64Keys<long>().Create();
        Scalar8Scalar8Profile profile = index.GetScalar8Scalar8Profile();
        const long duplicateKey = 371;
        for (int i = 0; i < profile.MaxItemCount + 1; i++)
        {
            _ = index.Insert(duplicateKey, 371_000 + i);
        }

        LibraDexQueuedWriter<long, long> writer = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);
        LibraDexGenericDeleteResult first = writer.Delete(duplicateKey, 371_000);
        LibraDexGenericDeleteResult second = writer.Delete(duplicateKey, 371_001);

        ConcurrencyWorkloadMatrixRow row = BuildGenericDeleteMatrixRow(
            "terminal-identity-delete-local",
            "terminal exists; exact shelf delete",
            first,
            second);
        RequireMatrixPath(row, writerContext: 2, serializedFallback: 0);

        long[] identities = new long[profile.MaxItemCount + 1];
        LibraDexGenericRangeReadResult read = index.ReadRange(duplicateKey, duplicateKey, identities);
        if (read.IdentityCount != profile.MaxItemCount - 1 ||
            identities[0] != 371_002)
        {
            throw new InvalidDataException("Concurrency workload matrix terminal delete readback failed.");
        }

        return row;
    }

    /// <summary>
    /// Measures generic `SS16-8` queued writes into warmed independent shelves.<br/>
    /// Expected result: both operations use writer-context staging because only shelf-local bytes change.<br/>
    /// </summary>
    private static ConcurrencyWorkloadMatrixRow RunWorkloadMatrixScalar16Scalar8DifferentShelfWarm()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions { DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed });
        using LibraDexIndex<Int128, long> index = catalog.Indexes["matrix-ss16-8-different-shelf"]["ids"].Int128Keys<long>().Create();
        Int128 leftBase = 5100;
        Int128 leftNext = 5101;
        Int128 rightBase = ((Int128)long.MinValue << 64) + 5100;
        Int128 rightNext = ((Int128)long.MinValue << 64) + 5101;
        _ = index.Insert(leftBase, 5101);
        _ = index.Insert(rightBase, 5102);
        LibraDexQueuedWriter<Int128, long> writer = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);

        (LibraDexGenericInsertResult leftResult, LibraDexGenericInsertResult rightResult) = RunOverlappingGenericQueuedInserts(
            () => writer.Insert(leftNext, 5103),
            () => writer.Insert(rightNext, 5104),
            "matrix SS16-8 different-shelf warm");

        ConcurrencyWorkloadMatrixRow row = BuildGenericMatrixRow(
            "ss16-8-different-shelf",
            "widened key; warmed independent shelves",
            leftResult,
            rightResult);
        RequireMatrixPath(row, writerContext: 2, serializedFallback: 0);
        return row;
    }

    /// <summary>
    /// Measures generic `SS16-8` queued writes into one warmed shelf.<br/>
    /// Expected result: both operations complete through writer-context staging after any internal shelf-ownership retry.<br/>
    /// </summary>
    private static ConcurrencyWorkloadMatrixRow RunWorkloadMatrixScalar16Scalar8SameShelfWarm()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions { DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed });
        using LibraDexIndex<Int128, long> index = catalog.Indexes["matrix-ss16-8-same-shelf"]["ids"].Int128Keys<long>().Create();
        Int128 keyA = 5200;
        Int128 keyB = 5201;
        Int128 keyC = 5202;
        _ = index.Insert(keyA, 5201);
        LibraDexQueuedWriter<Int128, long> writer = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);

        (LibraDexGenericInsertResult firstResult, LibraDexGenericInsertResult secondResult) = RunOverlappingGenericQueuedInserts(
            () => writer.Insert(keyB, 5202),
            () => writer.Insert(keyC, 5203),
            "matrix SS16-8 same-shelf warm");

        ConcurrencyWorkloadMatrixRow row = BuildGenericMatrixRow(
            "ss16-8-same-shelf",
            "widened key; same shelf retry/wait",
            firstResult,
            secondResult);
        RequireMatrixPath(row, writerContext: 2, serializedFallback: 0);
        return row;
    }

    /// <summary>
    /// Measures generic `SS16-8` queued insertion into a missing root-prefix route.<br/>
    /// Expected result: route creation uses the per-root narrowed topology publisher.<br/>
    /// </summary>
    private static ConcurrencyWorkloadMatrixRow RunWorkloadMatrixScalar16Scalar8ColdRouteFallback()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions { DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed });
        using LibraDexIndex<Int128, long> index = catalog.Indexes["matrix-ss16-8-cold-route"]["ids"].Int128Keys<long>().Create();
        _ = index.Insert(5300, 5301);
        LibraDexQueuedWriter<Int128, long> writer = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);
        LibraDexGenericInsertResult result = writer.Insert(((Int128)long.MinValue << 64) + 5300, 5302);

        ConcurrencyWorkloadMatrixRow row = BuildGenericMatrixRow(
            "ss16-8-cold-route",
            "widened key; missing root prefix",
            result);
        RequireMatrixPath(row, writerContext: 0, serializedFallback: 0, narrowTopology: 1);
        return row;
    }

    /// <summary>
    /// Measures generic `SS16-8` queued insertion into a full ordinary shelf.<br/>
    /// Expected result: full-shelf split or transform work uses a narrowed topology publisher, not writer-context staging.<br/>
    /// </summary>
    private static ConcurrencyWorkloadMatrixRow RunWorkloadMatrixScalar16Scalar8FullShelfFallback()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions { DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed });
        using LibraDexIndex<Int128, long> index = catalog.Indexes["matrix-ss16-8-full-shelf"]["ids"].Int128Keys<long>().Create();
        Scalar16Scalar8Profile profile = index.GetScalar16Scalar8Profile();
        Int128 baseKey = 5400;
        for (int i = 0; i < profile.MaxItemCount; i++)
        {
            Int128 key = baseKey + i;
            LibraDexGenericInsertResult fill = index.Insert(key, 540_000 + i);
            if (!fill.Inserted)
            {
                throw new InvalidDataException($"Concurrency workload matrix SS16-8 full-shelf setup insert {i} failed.");
            }
        }

        LibraDexQueuedWriter<Int128, long> writer = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);
        LibraDexGenericInsertResult result = writer.Insert(baseKey + profile.MaxItemCount, 540_000 + profile.MaxItemCount);
        ConcurrencyWorkloadMatrixRow row = BuildGenericMatrixRow(
            "ss16-8-full-shelf",
            "widened key; split/topology publish",
            result);
        RequireMatrixPath(row, writerContext: 0, serializedFallback: 0, narrowTopology: 1);
        return row;
    }

    /// <summary>
    /// Measures generic `SS8-16` queued writes into warmed independent shelves.<br/>
    /// Expected result: both operations use writer-context staging because only shelf-local bytes change.<br/>
    /// </summary>
    private static ConcurrencyWorkloadMatrixRow RunWorkloadMatrixScalar8Scalar16DifferentShelfWarm()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions { DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed });
        using LibraDexIndex<long, Guid> index = catalog.Indexes["matrix-ss8-16-different-shelf"]["ids"].Int64Keys<Guid>().Create();
        _ = index.Insert(5500, Guid.Parse("55000000-0000-0000-0000-000000000001"));
        _ = index.Insert(long.MinValue + 5500, Guid.Parse("55000000-0000-0000-0000-000000000002"));
        LibraDexQueuedWriter<long, Guid> writer = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);

        (LibraDexGenericInsertResult leftResult, LibraDexGenericInsertResult rightResult) = RunOverlappingGenericQueuedInserts(
            () => writer.Insert(5501, Guid.Parse("55000000-0000-0000-0000-000000000003")),
            () => writer.Insert(long.MinValue + 5501, Guid.Parse("55000000-0000-0000-0000-000000000004")),
            "matrix SS8-16 different-shelf warm");

        ConcurrencyWorkloadMatrixRow row = BuildGenericMatrixRow(
            "ss8-16-different-shelf",
            "widened identity; warmed independent shelves",
            leftResult,
            rightResult);
        RequireMatrixPath(row, writerContext: 2, serializedFallback: 0);
        return row;
    }

    /// <summary>
    /// Measures generic `SS8-16` queued writes into one warmed shelf.<br/>
    /// Expected result: both operations complete through writer-context staging after any internal shelf-ownership retry.<br/>
    /// </summary>
    private static ConcurrencyWorkloadMatrixRow RunWorkloadMatrixScalar8Scalar16SameShelfWarm()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions { DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed });
        using LibraDexIndex<long, Guid> index = catalog.Indexes["matrix-ss8-16-same-shelf"]["ids"].Int64Keys<Guid>().Create();
        _ = index.Insert(5600, Guid.Parse("56000000-0000-0000-0000-000000000001"));
        LibraDexQueuedWriter<long, Guid> writer = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);

        (LibraDexGenericInsertResult firstResult, LibraDexGenericInsertResult secondResult) = RunOverlappingGenericQueuedInserts(
            () => writer.Insert(5601, Guid.Parse("56000000-0000-0000-0000-000000000002")),
            () => writer.Insert(5602, Guid.Parse("56000000-0000-0000-0000-000000000003")),
            "matrix SS8-16 same-shelf warm");

        ConcurrencyWorkloadMatrixRow row = BuildGenericMatrixRow(
            "ss8-16-same-shelf",
            "widened identity; same shelf retry/wait",
            firstResult,
            secondResult);
        RequireMatrixPath(row, writerContext: 2, serializedFallback: 0);
        return row;
    }

    /// <summary>
    /// Measures generic `SS8-16` queued insertion into a missing root-prefix route.<br/>
    /// Expected result: route creation uses the per-root narrowed topology publisher.<br/>
    /// </summary>
    private static ConcurrencyWorkloadMatrixRow RunWorkloadMatrixScalar8Scalar16ColdRouteFallback()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions { DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed });
        using LibraDexIndex<long, Guid> index = catalog.Indexes["matrix-ss8-16-cold-route"]["ids"].Int64Keys<Guid>().Create();
        _ = index.Insert(5700, Guid.Parse("57000000-0000-0000-0000-000000000001"));
        LibraDexQueuedWriter<long, Guid> writer = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);
        LibraDexGenericInsertResult result = writer.Insert(long.MinValue + 5700, Guid.Parse("57000000-0000-0000-0000-000000000002"));

        ConcurrencyWorkloadMatrixRow row = BuildGenericMatrixRow(
            "ss8-16-cold-route",
            "widened identity; missing root prefix",
            result);
        RequireMatrixPath(row, writerContext: 0, serializedFallback: 0, narrowTopology: 1);
        return row;
    }

    /// <summary>
    /// Measures generic `SS8-16` queued insertion into a full ordinary shelf.<br/>
    /// Expected result: full-shelf split or transform work uses a narrowed topology publisher, not writer-context staging.<br/>
    /// </summary>
    private static ConcurrencyWorkloadMatrixRow RunWorkloadMatrixScalar8Scalar16FullShelfFallback()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions { DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed });
        using LibraDexIndex<long, Guid> index = catalog.Indexes["matrix-ss8-16-full-shelf"]["ids"].Int64Keys<Guid>().Create();
        Scalar8Scalar16Profile profile = index.GetScalar8Scalar16Profile();
        for (int i = 0; i < profile.MaxItemCount; i++)
        {
            LibraDexGenericInsertResult fill = index.Insert(5800 + i, Guid.Parse($"58000000-0000-0000-0000-{(i + 1):000000000000}"));
            if (!fill.Inserted)
            {
                throw new InvalidDataException($"Concurrency workload matrix SS8-16 full-shelf setup insert {i} failed.");
            }
        }

        LibraDexQueuedWriter<long, Guid> writer = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);
        LibraDexGenericInsertResult result = writer.Insert(5800 + profile.MaxItemCount, Guid.Parse("58000000-0000-0000-0001-000000000001"));
        ConcurrencyWorkloadMatrixRow row = BuildGenericMatrixRow(
            "ss8-16-full-shelf",
            "widened identity; split/topology publish",
            result);
        RequireMatrixPath(row, writerContext: 0, serializedFallback: 0, narrowTopology: 1);
        return row;
    }

    /// <summary>
    /// Measures generic `SS16-16` queued writes into warmed independent shelves.<br/>
    /// Expected result: both operations use writer-context staging because only shelf-local bytes change.<br/>
    /// </summary>
    private static ConcurrencyWorkloadMatrixRow RunWorkloadMatrixScalar16Scalar16DifferentShelfWarm()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions { DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed });
        using LibraDexIndex<Guid, Guid> index = catalog.Indexes["matrix-ss16-16-different-shelf"]["ids"].GuidKeys<Guid>().Create();
        Guid leftBase = Guid.Parse("59000000-0000-0000-0000-000000000001");
        Guid leftNext = Guid.Parse("59000000-0000-0000-0000-000000000002");
        Guid rightBase = Guid.Parse("d9000000-0000-0000-0000-000000000001");
        Guid rightNext = Guid.Parse("d9000000-0000-0000-0000-000000000002");
        _ = index.Insert(leftBase, Guid.Parse("59000000-0000-0000-0000-000000000101"));
        _ = index.Insert(rightBase, Guid.Parse("59000000-0000-0000-0000-000000000102"));
        LibraDexQueuedWriter<Guid, Guid> writer = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);

        (LibraDexGenericInsertResult leftResult, LibraDexGenericInsertResult rightResult) = RunOverlappingGenericQueuedInserts(
            () => writer.Insert(leftNext, Guid.Parse("59000000-0000-0000-0000-000000000103")),
            () => writer.Insert(rightNext, Guid.Parse("59000000-0000-0000-0000-000000000104")),
            "matrix SS16-16 different-shelf warm");

        ConcurrencyWorkloadMatrixRow row = BuildGenericMatrixRow(
            "ss16-16-different-shelf",
            "wide key/identity; warmed independent shelves",
            leftResult,
            rightResult);
        RequireMatrixPath(row, writerContext: 2, serializedFallback: 0);
        return row;
    }

    /// <summary>
    /// Measures generic `SS16-16` queued writes into one warmed shelf.<br/>
    /// Expected result: both operations complete through writer-context staging after any internal shelf-ownership retry.<br/>
    /// </summary>
    private static ConcurrencyWorkloadMatrixRow RunWorkloadMatrixScalar16Scalar16SameShelfWarm()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions { DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed });
        using LibraDexIndex<Guid, Guid> index = catalog.Indexes["matrix-ss16-16-same-shelf"]["ids"].GuidKeys<Guid>().Create();
        Guid keyA = Guid.Parse("5a000000-0000-0000-0000-000000000001");
        Guid keyB = Guid.Parse("5a000000-0000-0000-0000-000000000002");
        Guid keyC = Guid.Parse("5a000000-0000-0000-0000-000000000003");
        _ = index.Insert(keyA, Guid.Parse("5a000000-0000-0000-0000-000000000101"));
        LibraDexQueuedWriter<Guid, Guid> writer = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);

        (LibraDexGenericInsertResult firstResult, LibraDexGenericInsertResult secondResult) = RunOverlappingGenericQueuedInserts(
            () => writer.Insert(keyB, Guid.Parse("5a000000-0000-0000-0000-000000000102")),
            () => writer.Insert(keyC, Guid.Parse("5a000000-0000-0000-0000-000000000103")),
            "matrix SS16-16 same-shelf warm");

        ConcurrencyWorkloadMatrixRow row = BuildGenericMatrixRow(
            "ss16-16-same-shelf",
            "wide key/identity; same shelf retry/wait",
            firstResult,
            secondResult);
        RequireMatrixPath(row, writerContext: 2, serializedFallback: 0);
        return row;
    }

    /// <summary>
    /// Measures generic `SS16-16` queued insertion into a missing root-prefix route.<br/>
    /// Expected result: route creation uses the per-root narrowed topology publisher.<br/>
    /// </summary>
    private static ConcurrencyWorkloadMatrixRow RunWorkloadMatrixScalar16Scalar16ColdRouteFallback()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions { DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed });
        using LibraDexIndex<Int128, Guid> index = catalog.Indexes["matrix-ss16-16-cold-route"]["ids"].Int128Keys<Guid>().Create();
        _ = index.Insert(5900, Guid.Parse("5b000000-0000-0000-0000-000000000001"));
        LibraDexQueuedWriter<Int128, Guid> writer = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);
        Int128 coldKey = ((Int128)long.MinValue << 64) + 5900;
        LibraDexGenericInsertResult result = writer.Insert(coldKey, Guid.Parse("5b000000-0000-0000-0000-000000000002"));

        ConcurrencyWorkloadMatrixRow row = BuildGenericMatrixRow(
            "ss16-16-cold-route",
            "wide key/identity; missing root prefix",
            result);
        RequireMatrixPath(row, writerContext: 0, serializedFallback: 0, narrowTopology: 1);
        return row;
    }

    /// <summary>
    /// Measures generic `SS16-16` queued insertion into a full ordinary shelf.<br/>
    /// Expected result: full-shelf split or transform work uses a narrowed topology publisher, not writer-context staging.<br/>
    /// </summary>
    private static ConcurrencyWorkloadMatrixRow RunWorkloadMatrixScalar16Scalar16FullShelfFallback()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions { DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed });
        using LibraDexIndex<Int128, Guid> index = catalog.Indexes["matrix-ss16-16-full-shelf"]["ids"].Int128Keys<Guid>().Create();
        Scalar16Scalar16Profile profile = index.GetScalar16Scalar16Profile();
        Int128 baseKey = 6000;
        for (int i = 0; i < profile.MaxItemCount; i++)
        {
            LibraDexGenericInsertResult fill = index.Insert(baseKey + i, Guid.Parse($"5c000000-0000-0000-0000-{(i + 1):000000000000}"));
            if (!fill.Inserted)
            {
                throw new InvalidDataException($"Concurrency workload matrix SS16-16 full-shelf setup insert {i} failed.");
            }
        }

        LibraDexQueuedWriter<Int128, Guid> writer = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);
        LibraDexGenericInsertResult result = writer.Insert(baseKey + profile.MaxItemCount, Guid.Parse("5c000000-0000-0000-0001-000000000001"));
        ConcurrencyWorkloadMatrixRow row = BuildGenericMatrixRow(
            "ss16-16-full-shelf",
            "wide key/identity; split/topology publish",
            result);
        RequireMatrixPath(row, writerContext: 0, serializedFallback: 0, narrowTopology: 1);
        return row;
    }

    /// <summary>
    /// Measures generic `SS8-8` queued exact deletes on warmed independent shelves.<br/>
    /// Expected result: both deletes use writer-context staging because each removes one shelf-local tuple.<br/>
    /// </summary>
    private static ConcurrencyWorkloadMatrixRow RunWorkloadMatrixScalar8Scalar8DeleteWarm()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions { DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed });
        using LibraDexIndex<long, long> index = catalog.Indexes["matrix-ss8-8-delete"]["ids"].Int64Keys<long>().Create();
        _ = index.Insert(6100, 6101);
        _ = index.Insert(long.MinValue + 6100, 6102);
        LibraDexQueuedWriter<long, long> writer = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);

        (LibraDexGenericDeleteResult leftResult, LibraDexGenericDeleteResult rightResult) = RunOverlappingGenericQueuedDeletes(
            () => writer.Delete(6100, 6101),
            () => writer.Delete(long.MinValue + 6100, 6102),
            "matrix SS8-8 delete warm");

        ConcurrencyWorkloadMatrixRow row = BuildGenericDeleteMatrixRow(
            "ss8-8-delete-warm",
            "scalar key/identity; exact tuple removal",
            leftResult,
            rightResult);
        RequireMatrixPath(row, writerContext: 2, serializedFallback: 0);
        return row;
    }

    /// <summary>
    /// Measures generic `SS8-8` queued rekeys on warmed independent shelves.<br/>
    /// Expected result: replacement insert and old tuple delete both use writer-context staging for each rekey.<br/>
    /// </summary>
    private static ConcurrencyWorkloadMatrixRow RunWorkloadMatrixScalar8Scalar8RekeyWarm()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions { DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed });
        using LibraDexIndex<long, long> index = catalog.Indexes["matrix-ss8-8-rekey"]["ids"].Int64Keys<long>().Create();
        _ = index.Insert(6200, 6201);
        _ = index.Insert(long.MinValue + 6200, 6202);
        LibraDexQueuedWriter<long, long> writer = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);

        (LibraDexGenericRekeyResult leftResult, LibraDexGenericRekeyResult rightResult) = RunOverlappingGenericQueuedRekeys(
            () => writer.Rekey(6201, 6200, 6203),
            () => writer.Rekey(6202, long.MinValue + 6200, long.MinValue + 6203),
            "matrix SS8-8 rekey warm");

        ConcurrencyWorkloadMatrixRow row = BuildGenericRekeyMatrixRow(
            "ss8-8-rekey-warm",
            "scalar key/identity; replacement plus removal",
            leftResult,
            rightResult);
        RequireMatrixPath(row, writerContext: 4, serializedFallback: 0);
        return row;
    }

    /// <summary>
    /// Measures generic `SS16-8` queued exact deletes on warmed independent shelves.<br/>
    /// Expected result: both deletes use writer-context staging because each removes one shelf-local tuple.<br/>
    /// </summary>
    private static ConcurrencyWorkloadMatrixRow RunWorkloadMatrixScalar16Scalar8DeleteWarm()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions { DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed });
        using LibraDexIndex<Int128, long> index = catalog.Indexes["matrix-ss16-8-delete"]["ids"].Int128Keys<long>().Create();
        Int128 leftKey = 6300;
        Int128 rightKey = ((Int128)long.MinValue << 64) + 6300;
        _ = index.Insert(leftKey, 6301);
        _ = index.Insert(rightKey, 6302);
        LibraDexQueuedWriter<Int128, long> writer = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);

        (LibraDexGenericDeleteResult leftResult, LibraDexGenericDeleteResult rightResult) = RunOverlappingGenericQueuedDeletes(
            () => writer.Delete(leftKey, 6301),
            () => writer.Delete(rightKey, 6302),
            "matrix SS16-8 delete warm");

        ConcurrencyWorkloadMatrixRow row = BuildGenericDeleteMatrixRow(
            "ss16-8-delete-warm",
            "widened key; exact tuple removal",
            leftResult,
            rightResult);
        RequireMatrixPath(row, writerContext: 2, serializedFallback: 0);
        return row;
    }

    /// <summary>
    /// Measures generic `SS16-8` queued rekeys on warmed independent shelves.<br/>
    /// Expected result: replacement insert and old tuple delete both use writer-context staging for each rekey.<br/>
    /// </summary>
    private static ConcurrencyWorkloadMatrixRow RunWorkloadMatrixScalar16Scalar8RekeyWarm()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions { DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed });
        using LibraDexIndex<Int128, long> index = catalog.Indexes["matrix-ss16-8-rekey"]["ids"].Int128Keys<long>().Create();
        Int128 leftKey = 6400;
        Int128 rightKey = ((Int128)long.MinValue << 64) + 6400;
        _ = index.Insert(leftKey, 6401);
        _ = index.Insert(rightKey, 6402);
        LibraDexQueuedWriter<Int128, long> writer = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);

        (LibraDexGenericRekeyResult leftResult, LibraDexGenericRekeyResult rightResult) = RunOverlappingGenericQueuedRekeys(
            () => writer.Rekey(6401, leftKey, leftKey + 1),
            () => writer.Rekey(6402, rightKey, rightKey + 1),
            "matrix SS16-8 rekey warm");

        ConcurrencyWorkloadMatrixRow row = BuildGenericRekeyMatrixRow(
            "ss16-8-rekey-warm",
            "widened key; replacement plus removal",
            leftResult,
            rightResult);
        RequireMatrixPath(row, writerContext: 4, serializedFallback: 0);
        return row;
    }

    /// <summary>
    /// Measures generic `SS8-16` queued exact deletes on warmed independent shelves.<br/>
    /// Expected result: both deletes use writer-context staging because each removes one shelf-local tuple.<br/>
    /// </summary>
    private static ConcurrencyWorkloadMatrixRow RunWorkloadMatrixScalar8Scalar16DeleteWarm()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions { DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed });
        using LibraDexIndex<long, Guid> index = catalog.Indexes["matrix-ss8-16-delete"]["ids"].Int64Keys<Guid>().Create();
        Guid leftIdentity = Guid.Parse("65000000-0000-0000-0000-000000000001");
        Guid rightIdentity = Guid.Parse("65000000-0000-0000-0000-000000000002");
        _ = index.Insert(6500, leftIdentity);
        _ = index.Insert(long.MinValue + 6500, rightIdentity);
        LibraDexQueuedWriter<long, Guid> writer = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);

        (LibraDexGenericDeleteResult leftResult, LibraDexGenericDeleteResult rightResult) = RunOverlappingGenericQueuedDeletes(
            () => writer.Delete(6500, leftIdentity),
            () => writer.Delete(long.MinValue + 6500, rightIdentity),
            "matrix SS8-16 delete warm");

        ConcurrencyWorkloadMatrixRow row = BuildGenericDeleteMatrixRow(
            "ss8-16-delete-warm",
            "widened identity; exact tuple removal",
            leftResult,
            rightResult);
        RequireMatrixPath(row, writerContext: 2, serializedFallback: 0);
        return row;
    }

    /// <summary>
    /// Measures generic `SS8-16` queued rekeys on warmed independent shelves.<br/>
    /// Expected result: replacement insert and old tuple delete both use writer-context staging for each rekey.<br/>
    /// </summary>
    private static ConcurrencyWorkloadMatrixRow RunWorkloadMatrixScalar8Scalar16RekeyWarm()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions { DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed });
        using LibraDexIndex<long, Guid> index = catalog.Indexes["matrix-ss8-16-rekey"]["ids"].Int64Keys<Guid>().Create();
        Guid leftIdentity = Guid.Parse("66000000-0000-0000-0000-000000000001");
        Guid rightIdentity = Guid.Parse("66000000-0000-0000-0000-000000000002");
        _ = index.Insert(6600, leftIdentity);
        _ = index.Insert(long.MinValue + 6600, rightIdentity);
        LibraDexQueuedWriter<long, Guid> writer = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);

        (LibraDexGenericRekeyResult leftResult, LibraDexGenericRekeyResult rightResult) = RunOverlappingGenericQueuedRekeys(
            () => writer.Rekey(leftIdentity, 6600, 6601),
            () => writer.Rekey(rightIdentity, long.MinValue + 6600, long.MinValue + 6601),
            "matrix SS8-16 rekey warm");

        ConcurrencyWorkloadMatrixRow row = BuildGenericRekeyMatrixRow(
            "ss8-16-rekey-warm",
            "widened identity; replacement plus removal",
            leftResult,
            rightResult);
        RequireMatrixPath(row, writerContext: 4, serializedFallback: 0);
        return row;
    }

    /// <summary>
    /// Measures generic `SS16-16` queued exact deletes on warmed independent shelves.<br/>
    /// Expected result: both deletes use writer-context staging because each removes one shelf-local tuple.<br/>
    /// </summary>
    private static ConcurrencyWorkloadMatrixRow RunWorkloadMatrixScalar16Scalar16DeleteWarm()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions { DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed });
        using LibraDexIndex<Int128, Guid> index = catalog.Indexes["matrix-ss16-16-delete"]["ids"].Int128Keys<Guid>().Create();
        Int128 leftKey = 6700;
        Int128 rightKey = ((Int128)long.MinValue << 64) + 6700;
        Guid leftIdentity = Guid.Parse("67000000-0000-0000-0000-000000000001");
        Guid rightIdentity = Guid.Parse("67000000-0000-0000-0000-000000000002");
        _ = index.Insert(leftKey, leftIdentity);
        _ = index.Insert(rightKey, rightIdentity);
        LibraDexQueuedWriter<Int128, Guid> writer = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);

        (LibraDexGenericDeleteResult leftResult, LibraDexGenericDeleteResult rightResult) = RunOverlappingGenericQueuedDeletes(
            () => writer.Delete(leftKey, leftIdentity),
            () => writer.Delete(rightKey, rightIdentity),
            "matrix SS16-16 delete warm");

        ConcurrencyWorkloadMatrixRow row = BuildGenericDeleteMatrixRow(
            "ss16-16-delete-warm",
            "wide key/identity; exact tuple removal",
            leftResult,
            rightResult);
        RequireMatrixPath(row, writerContext: 2, serializedFallback: 0);
        return row;
    }

    /// <summary>
    /// Measures generic `SS16-16` queued rekeys on warmed independent shelves.<br/>
    /// Expected result: replacement insert and old tuple delete both use writer-context staging for each rekey.<br/>
    /// </summary>
    private static ConcurrencyWorkloadMatrixRow RunWorkloadMatrixScalar16Scalar16RekeyWarm()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions { DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed });
        using LibraDexIndex<Int128, Guid> index = catalog.Indexes["matrix-ss16-16-rekey"]["ids"].Int128Keys<Guid>().Create();
        Int128 leftKey = 6800;
        Int128 rightKey = ((Int128)long.MinValue << 64) + 6800;
        Guid leftIdentity = Guid.Parse("68000000-0000-0000-0000-000000000001");
        Guid rightIdentity = Guid.Parse("68000000-0000-0000-0000-000000000002");
        _ = index.Insert(leftKey, leftIdentity);
        _ = index.Insert(rightKey, rightIdentity);
        LibraDexQueuedWriter<Int128, Guid> writer = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);

        (LibraDexGenericRekeyResult leftResult, LibraDexGenericRekeyResult rightResult) = RunOverlappingGenericQueuedRekeys(
            () => writer.Rekey(leftIdentity, leftKey, leftKey + 1),
            () => writer.Rekey(rightIdentity, rightKey, rightKey + 1),
            "matrix SS16-16 rekey warm");

        ConcurrencyWorkloadMatrixRow row = BuildGenericRekeyMatrixRow(
            "ss16-16-rekey-warm",
            "wide key/identity; replacement plus removal",
            leftResult,
            rightResult);
        RequireMatrixPath(row, writerContext: 4, serializedFallback: 0);
        return row;
    }

    /// <summary>
    /// Measures maintained exact-reversed projection insertion for fixed-width binary keys.<br/>
    /// Expected result: the primary insert keeps its fixed-scalar writer attribution while the reversed companion tuple publishes as a separate projection update outside public batch mode.<br/>
    /// </summary>
    private static ConcurrencyWorkloadMatrixRow RunWorkloadMatrixExactReversedProjectionBoundary()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions { DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed });
        using LibraDexIndex<byte[], long> index = catalog.Indexes["matrix-projection"]["fingerprint"].Blob.Scalar<long>(
            LibraDexScalarWidth.Bytes16,
            directions: LibraDexProjectionDirectionSet.ForwardAndReversed).Create();

        byte[] key = Convert.FromHexString("00112233445566778899AABBCCDDEEFF");
        LibraDexGenericInsertResult result = index.Insert(key, 6901);
        if (!result.Inserted ||
            result.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.NarrowTopologyPublisher)
        {
            throw new InvalidDataException($"Concurrency workload matrix projection insert expected primary narrow-topology attribution but saw inserted={result.Inserted}, path={result.QueuedInsertPath}.");
        }

        ConcurrencyWorkloadMatrixRow row = new(
            "exact-reversed-projection",
            Operations: 1,
            WriterContext: 0,
            NarrowTopology: 1,
            SerializedFallback: 0,
            Changed: 1,
            "maintained projection; primary attribution");
        RequireMatrixPath(row, writerContext: 0, serializedFallback: 0, narrowTopology: 1);
        return row;
    }

    /// <summary>
    /// Measures exact delete on a maintained exact-reversed projection owner after both primary and projection routes are warmed.<br/>
    /// Expected result: the primary exact-delete leg reports writer-context attribution and the companion reversed projection tuple is removed through the projection index cleanup path.<br/>
    /// </summary>
    private static ConcurrencyWorkloadMatrixRow RunWorkloadMatrixExactReversedProjectionDeleteWarm()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions { DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed });
        using LibraDexIndex<byte[], long> index = catalog.Indexes["matrix-projection-delete"]["fingerprint"].Blob.Scalar<long>(
            LibraDexScalarWidth.Bytes16,
            directions: LibraDexProjectionDirectionSet.ForwardAndReversed).Create();

        byte[] key = Convert.FromHexString("10112233445566778899AABBCCDDEEFF");
        _ = index.Insert(key, 6911);
        LibraDexQueuedWriter<byte[], long> writer = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);
        LibraDexGenericDeleteResult delete = writer.Delete(key, 6911);

        ConcurrencyWorkloadMatrixRow row = BuildGenericDeleteMatrixRow(
            "exact-reversed-delete-warm",
            "projection owner; exact delete",
            delete);
        RequireMatrixPath(row, writerContext: 1, serializedFallback: 0);

        long[] identities = new long[1];
        if (index.ReadRange(key, key, identities).IdentityCount != 0)
        {
            throw new InvalidDataException("Concurrency workload matrix projection delete primary readback failed.");
        }

        return row;
    }

    /// <summary>
    /// Measures rekey on a maintained exact-reversed projection owner after the old key route is warmed.<br/>
    /// Expected result: replacement insert and old tuple delete both report writer-context attribution when no route topology changes are needed.<br/>
    /// </summary>
    private static ConcurrencyWorkloadMatrixRow RunWorkloadMatrixExactReversedProjectionRekeyWarm()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions { DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed });
        using LibraDexIndex<byte[], long> index = catalog.Indexes["matrix-projection-rekey"]["fingerprint"].Blob.Scalar<long>(
            LibraDexScalarWidth.Bytes16,
            directions: LibraDexProjectionDirectionSet.ForwardAndReversed).Create();

        byte[] oldKey = Convert.FromHexString("20112233445566778899AABBCCDDEEFF");
        byte[] newKey = Convert.FromHexString("20112233445566778899AABBCCDDEF00");
        _ = index.Insert(oldKey, 6921);
        LibraDexQueuedWriter<byte[], long> writer = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);
        LibraDexGenericRekeyResult rekey = writer.Rekey(6921, oldKey, newKey);

        ConcurrencyWorkloadMatrixRow row = BuildGenericRekeyMatrixRow(
            "exact-reversed-rekey-warm",
            "projection owner; replacement plus removal",
            rekey);
        RequireMatrixPath(row, writerContext: 2, serializedFallback: 0);

        long[] identities = new long[1];
        if (index.ReadRange(oldKey, oldKey, identities).IdentityCount != 0 ||
            index.ReadRange(newKey, newKey, identities).IdentityCount != 1 ||
            identities[0] != 6921)
        {
            throw new InvalidDataException("Concurrency workload matrix projection rekey readback failed.");
        }

        return row;
    }

    /// <summary>
    /// Measures warmed `FS32-8` direct inserts on independent shelves.<br/>
    /// Expected result: both operations use writer-context staging after root routes have been initialized by setup writes.<br/>
    /// </summary>
    private static ConcurrencyWorkloadMatrixRow RunWorkloadMatrixFixed32Scalar8Warm()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions { DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed });
        using LibraDexIndex<byte[], long> index = catalog.Indexes["matrix-fs32"]["hash8-warm"].Blob.Scalar<long>(LibraDexScalarWidth.Bytes32).Create();
        byte[] leftSeed = Convert.FromHexString("100102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");
        byte[] leftNext = Convert.FromHexString("100102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E20");
        byte[] rightSeed = Convert.FromHexString("900102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");
        byte[] rightNext = Convert.FromHexString("900102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E20");
        _ = index.Insert(leftSeed, 7001);
        _ = index.Insert(rightSeed, 7002);

        (LibraDexGenericInsertResult leftResult, LibraDexGenericInsertResult rightResult) = RunOverlappingGenericQueuedInserts(
            () => index.Insert(leftNext, 7003),
            () => index.Insert(rightNext, 7004),
            "matrix FS32-8 warm");

        ConcurrencyWorkloadMatrixRow row = BuildGenericMatrixRow(
            "fs32-8-warm",
            "32-byte key; warmed independent shelves",
            leftResult,
            rightResult);
        RequireMatrixPath(row, writerContext: 2, serializedFallback: 0);
        return row;
    }

    /// <summary>
    /// Measures cold-route `FS32-8` direct insertion after the writer-context slice is enabled.<br/>
    /// Expected result: route initialization uses a narrowed topology publisher because it publishes topology, not only shelf-local bytes.<br/>
    /// </summary>
    private static ConcurrencyWorkloadMatrixRow RunWorkloadMatrixFixed32Scalar8ColdRouteFallback()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions { DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed });
        using LibraDexIndex<byte[], long> index = catalog.Indexes["matrix-fs32"]["hash8-cold"].Blob.Scalar<long>(LibraDexScalarWidth.Bytes32).Create();
        LibraDexGenericInsertResult result = index.Insert(
            Convert.FromHexString("000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F"),
            7005);
        ConcurrencyWorkloadMatrixRow row = BuildGenericMatrixRow(
            "fs32-8-cold-route",
            "32-byte key; missing root prefix",
            result);
        RequireMatrixPath(row, writerContext: 0, serializedFallback: 0, narrowTopology: 1);
        return row;
    }

    /// <summary>
    /// Measures `FS32-8` insertion into a full ordinary shelf.<br/>
    /// Expected result: full-shelf split or transform work uses a narrowed topology publisher, not writer-context staging or a one-item batch.<br/>
    /// </summary>
    private static ConcurrencyWorkloadMatrixRow RunWorkloadMatrixFixed32Scalar8FullShelfFallback()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions { DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed });
        using LibraDexIndex<byte[], long> index = catalog.Indexes["matrix-fs32"]["hash8-full"].Blob.Scalar<long>(LibraDexScalarWidth.Bytes32).Create();
        Fixed32Scalar8Profile profile = index.GetFixed32Scalar8Profile();
        const byte prefix = 0x13;
        for (int i = 0; i < profile.MaxItemCount; i++)
        {
            LibraDexGenericInsertResult fill = index.Insert(CreateFixed32MatrixKey(prefix, i), 730_000 + i);
            if (!fill.Inserted)
            {
                throw new InvalidDataException($"Concurrency workload matrix FS32-8 full-shelf setup insert {i} failed.");
            }
        }

        LibraDexGenericInsertResult result = index.Insert(CreateFixed32MatrixKey(prefix, profile.MaxItemCount), 730_000 + profile.MaxItemCount);
        ConcurrencyWorkloadMatrixRow row = BuildGenericMatrixRow(
            "fs32-8-full-shelf",
            "32-byte key; split/topology publish",
            result);
        RequireMatrixPath(row, writerContext: 0, serializedFallback: 0, narrowTopology: 1);
        return row;
    }

    /// <summary>
    /// Measures warmed `FS32-8` exact deletes on independent shelves.<br/>
    /// Expected result: both removals use writer-context staging because only owned shelf bytes are compacted.<br/>
    /// </summary>
    private static ConcurrencyWorkloadMatrixRow RunWorkloadMatrixFixed32Scalar8DeleteWarm()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions { DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed });
        using LibraDexIndex<byte[], long> index = catalog.Indexes["matrix-fs32"]["hash8-delete"].Blob.Scalar<long>(LibraDexScalarWidth.Bytes32).Create();
        byte[] leftKey = Convert.FromHexString("110102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");
        byte[] rightKey = Convert.FromHexString("910102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");
        _ = index.Insert(leftKey, 7101);
        _ = index.Insert(rightKey, 7102);
        LibraDexQueuedWriter<byte[], long> writer = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);

        (LibraDexGenericDeleteResult leftResult, LibraDexGenericDeleteResult rightResult) = RunOverlappingGenericQueuedDeletes(
            () => writer.Delete(leftKey, 7101),
            () => writer.Delete(rightKey, 7102),
            "matrix FS32-8 delete warm");

        ConcurrencyWorkloadMatrixRow row = BuildGenericDeleteMatrixRow(
            "fs32-8-delete-warm",
            "32-byte key; exact delete on warmed shelves",
            leftResult,
            rightResult);
        RequireMatrixPath(row, writerContext: 2, serializedFallback: 0);
        long[] identities = new long[2];
        if (index.ReadRange(leftKey, leftKey, identities).IdentityCount != 0 ||
            index.ReadRange(rightKey, rightKey, identities).IdentityCount != 0)
        {
            throw new InvalidDataException("Concurrency workload matrix FS32-8 delete readback failed.");
        }

        return row;
    }

    /// <summary>
    /// Measures warmed `FS32-8` rekeys on independent shelves.<br/>
    /// Expected result: replacement insert and old tuple delete both use writer-context staging for each rekey.<br/>
    /// </summary>
    private static ConcurrencyWorkloadMatrixRow RunWorkloadMatrixFixed32Scalar8RekeyWarm()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions { DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed });
        using LibraDexIndex<byte[], long> index = catalog.Indexes["matrix-fs32"]["hash8-rekey"].Blob.Scalar<long>(LibraDexScalarWidth.Bytes32).Create();
        byte[] leftOld = Convert.FromHexString("120102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");
        byte[] leftNew = Convert.FromHexString("120102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E20");
        byte[] rightOld = Convert.FromHexString("920102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");
        byte[] rightNew = Convert.FromHexString("920102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E20");
        _ = index.Insert(leftOld, 7201);
        _ = index.Insert(rightOld, 7202);
        LibraDexQueuedWriter<byte[], long> writer = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);

        (LibraDexGenericRekeyResult leftResult, LibraDexGenericRekeyResult rightResult) = RunOverlappingGenericQueuedRekeys(
            () => writer.Rekey(7201, leftOld, leftNew),
            () => writer.Rekey(7202, rightOld, rightNew),
            "matrix FS32-8 rekey warm");

        ConcurrencyWorkloadMatrixRow row = BuildGenericRekeyMatrixRow(
            "fs32-8-rekey-warm",
            "32-byte key; replacement plus removal",
            leftResult,
            rightResult);
        RequireMatrixPath(row, writerContext: 4, serializedFallback: 0);
        return row;
    }

    /// <summary>
    /// Measures warmed `FS32-16` direct inserts on independent shelves.<br/>
    /// Expected result: both operations use writer-context staging after root routes have been initialized by setup writes.<br/>
    /// </summary>
    private static ConcurrencyWorkloadMatrixRow RunWorkloadMatrixFixed32Scalar16Warm()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions { DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed });
        using LibraDexIndex<byte[], Guid> index = catalog.Indexes["matrix-fs32"]["hash16-warm"].Blob.Scalar<Guid>(LibraDexScalarWidth.Bytes32).Create();
        byte[] leftSeed = Convert.FromHexString("300102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");
        byte[] leftNext = Convert.FromHexString("300102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E20");
        byte[] rightSeed = Convert.FromHexString("B00102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");
        byte[] rightNext = Convert.FromHexString("B00102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E20");
        _ = index.Insert(leftSeed, Guid.Parse("70000000-0000-0000-0000-000000000001"));
        _ = index.Insert(rightSeed, Guid.Parse("70000000-0000-0000-0000-000000000002"));

        (LibraDexGenericInsertResult leftResult, LibraDexGenericInsertResult rightResult) = RunOverlappingGenericQueuedInserts(
            () => index.Insert(leftNext, Guid.Parse("70000000-0000-0000-0000-000000000003")),
            () => index.Insert(rightNext, Guid.Parse("70000000-0000-0000-0000-000000000004")),
            "matrix FS32-16 warm");

        ConcurrencyWorkloadMatrixRow row = BuildGenericMatrixRow(
            "fs32-16-warm",
            "32-byte key/wide identity; warmed independent shelves",
            leftResult,
            rightResult);
        RequireMatrixPath(row, writerContext: 2, serializedFallback: 0);
        return row;
    }

    /// <summary>
    /// Measures cold-route `FS32-16` direct insertion after the writer-context slice is enabled.<br/>
    /// Expected result: route initialization uses a narrowed topology publisher because it publishes topology, not only shelf-local bytes.<br/>
    /// </summary>
    private static ConcurrencyWorkloadMatrixRow RunWorkloadMatrixFixed32Scalar16ColdRouteFallback()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions { DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed });
        using LibraDexIndex<byte[], Guid> index = catalog.Indexes["matrix-fs32"]["hash16-cold"].Blob.Scalar<Guid>(LibraDexScalarWidth.Bytes32).Create();
        LibraDexGenericInsertResult result = index.Insert(
            Convert.FromHexString("202122232425262728292A2B2C2D2E2F303132333435363738393A3B3C3D3E3F"),
            Guid.Parse("70000000-0000-0000-0000-000000000005"));
        ConcurrencyWorkloadMatrixRow row = BuildGenericMatrixRow(
            "fs32-16-cold-route",
            "32-byte key/wide identity; missing root prefix",
            result);
        RequireMatrixPath(row, writerContext: 0, serializedFallback: 0, narrowTopology: 1);
        return row;
    }

    /// <summary>
    /// Measures `FS32-16` insertion into a full ordinary shelf.<br/>
    /// Expected result: full-shelf split or transform work uses a narrowed topology publisher, not writer-context staging or a one-item batch.<br/>
    /// </summary>
    private static ConcurrencyWorkloadMatrixRow RunWorkloadMatrixFixed32Scalar16FullShelfFallback()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions { DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed });
        using LibraDexIndex<byte[], Guid> index = catalog.Indexes["matrix-fs32"]["hash16-full"].Blob.Scalar<Guid>(LibraDexScalarWidth.Bytes32).Create();
        Fixed32Scalar16Profile profile = index.GetFixed32Scalar16Profile();
        const byte prefix = 0x33;
        for (int i = 0; i < profile.MaxItemCount; i++)
        {
            LibraDexGenericInsertResult fill = index.Insert(CreateFixed32MatrixKey(prefix, i), GuidFromMatrixNumber(730_000 + i));
            if (!fill.Inserted)
            {
                throw new InvalidDataException($"Concurrency workload matrix FS32-16 full-shelf setup insert {i} failed.");
            }
        }

        LibraDexGenericInsertResult result = index.Insert(CreateFixed32MatrixKey(prefix, profile.MaxItemCount), GuidFromMatrixNumber(730_000 + profile.MaxItemCount));
        ConcurrencyWorkloadMatrixRow row = BuildGenericMatrixRow(
            "fs32-16-full-shelf",
            "32-byte key/wide identity; split/topology publish",
            result);
        RequireMatrixPath(row, writerContext: 0, serializedFallback: 0, narrowTopology: 1);
        return row;
    }

    /// <summary>
    /// Measures warmed `FS32-16` exact deletes on independent shelves.<br/>
    /// Expected result: both removals use writer-context staging because only owned shelf bytes are compacted.<br/>
    /// </summary>
    private static ConcurrencyWorkloadMatrixRow RunWorkloadMatrixFixed32Scalar16DeleteWarm()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions { DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed });
        using LibraDexIndex<byte[], Guid> index = catalog.Indexes["matrix-fs32"]["hash16-delete"].Blob.Scalar<Guid>(LibraDexScalarWidth.Bytes32).Create();
        byte[] leftKey = Convert.FromHexString("310102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");
        byte[] rightKey = Convert.FromHexString("C10102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");
        Guid leftIdentity = Guid.Parse("71000000-0000-0000-0000-000000000001");
        Guid rightIdentity = Guid.Parse("71000000-0000-0000-0000-000000000002");
        _ = index.Insert(leftKey, leftIdentity);
        _ = index.Insert(rightKey, rightIdentity);
        LibraDexQueuedWriter<byte[], Guid> writer = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);

        (LibraDexGenericDeleteResult leftResult, LibraDexGenericDeleteResult rightResult) = RunOverlappingGenericQueuedDeletes(
            () => writer.Delete(leftKey, leftIdentity),
            () => writer.Delete(rightKey, rightIdentity),
            "matrix FS32-16 delete warm");

        ConcurrencyWorkloadMatrixRow row = BuildGenericDeleteMatrixRow(
            "fs32-16-delete-warm",
            "32-byte key/wide identity; exact delete on warmed shelves",
            leftResult,
            rightResult);
        RequireMatrixPath(row, writerContext: 2, serializedFallback: 0);
        Guid[] identities = new Guid[2];
        if (index.ReadRange(leftKey, leftKey, identities).IdentityCount != 0 ||
            index.ReadRange(rightKey, rightKey, identities).IdentityCount != 0)
        {
            throw new InvalidDataException("Concurrency workload matrix FS32-16 delete readback failed.");
        }

        return row;
    }

    /// <summary>
    /// Measures warmed `FS32-16` rekeys on independent shelves.<br/>
    /// Expected result: replacement insert and old tuple delete both use writer-context staging for each rekey.<br/>
    /// </summary>
    private static ConcurrencyWorkloadMatrixRow RunWorkloadMatrixFixed32Scalar16RekeyWarm()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions { DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed });
        using LibraDexIndex<byte[], Guid> index = catalog.Indexes["matrix-fs32"]["hash16-rekey"].Blob.Scalar<Guid>(LibraDexScalarWidth.Bytes32).Create();
        byte[] leftOld = Convert.FromHexString("320102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");
        byte[] leftNew = Convert.FromHexString("320102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E20");
        byte[] rightOld = Convert.FromHexString("C20102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");
        byte[] rightNew = Convert.FromHexString("C20102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E20");
        Guid leftIdentity = Guid.Parse("72000000-0000-0000-0000-000000000001");
        Guid rightIdentity = Guid.Parse("72000000-0000-0000-0000-000000000002");
        _ = index.Insert(leftOld, leftIdentity);
        _ = index.Insert(rightOld, rightIdentity);
        LibraDexQueuedWriter<byte[], Guid> writer = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);

        (LibraDexGenericRekeyResult leftResult, LibraDexGenericRekeyResult rightResult) = RunOverlappingGenericQueuedRekeys(
            () => writer.Rekey(leftIdentity, leftOld, leftNew),
            () => writer.Rekey(rightIdentity, rightOld, rightNew),
            "matrix FS32-16 rekey warm");

        ConcurrencyWorkloadMatrixRow row = BuildGenericRekeyMatrixRow(
            "fs32-16-rekey-warm",
            "32-byte key/wide identity; replacement plus removal",
            leftResult,
            rightResult);
        RequireMatrixPath(row, writerContext: 4, serializedFallback: 0);
        return row;
    }

    /// <summary>
    /// Creates a deterministic 32-byte matrix key that stays inside one root prefix while preserving sortable suffix order.<br/>
    /// This keeps full-shelf fixtures focused on topology pressure rather than random key distribution.<br/>
    /// </summary>
    /// <param name="prefix">The first byte/root prefix to use.<br/></param>
    /// <param name="number">The sortable suffix number to encode.<br/></param>
    /// <returns>A new 32-byte key suitable for `FS32-*` matrix fixtures.</returns>
    private static byte[] CreateFixed32MatrixKey(byte prefix, int number)
    {
        byte[] key = new byte[32];
        key[0] = prefix;
        key[28] = (byte)(number >> 24);
        key[29] = (byte)(number >> 16);
        key[30] = (byte)(number >> 8);
        key[31] = (byte)number;
        return key;
    }

    /// <summary>
    /// Creates a deterministic GUID identity from a matrix integer without depending on byte-order-specific constructors.<br/>
    /// </summary>
    /// <param name="number">The integer to place in the final GUID segment.<br/></param>
    /// <returns>A deterministic GUID for matrix fixtures.</returns>
    private static Guid GuidFromMatrixNumber(int number)
    {
        return Guid.Parse($"73000000-0000-0000-0000-{number:000000000000}");
    }

    /// <summary>
    /// Proves that the public catalog/index path rejects a second active writer on the same detailed-diagnostics session before shared state is mutated.<br/>
    /// This is the first guardrail for unsupported caller-side concurrent writes; it does not add writer queuing or snapshot reads.<br/>
    /// </summary>
    private static void RunPublicOverlappingBatchRejectedBeforeMutationProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });
        using LibraDexIndex<long, long> index = catalog.Indexes["concurrency"]["overlap"].Int64Keys<long>().Create();
        using LibraDexBatch<long, long> active = index.BeginBatch();

        InvalidOperationException? secondWriterException = Task.Run(() =>
        {
            try
            {
                using LibraDexBatch<long, long> _ = index.BeginBatch();
                return null;
            }
            catch (InvalidOperationException ex)
            {
                return ex;
            }
        }).GetAwaiter().GetResult();

        if (secondWriterException is null)
        {
            throw new InvalidDataException("Concurrency contract sanity expected overlapping public batch begin to fail.");
        }

        string message = secondWriterException.Message;
        if (!message.Contains("overlapping same-session writes", StringComparison.Ordinal) ||
            !message.Contains("rejected before shared session state was mutated", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Concurrency contract sanity did not receive the expected detailed overlap diagnostic.");
        }

        active.Abort();
    }

    /// <summary>
    /// Proves that the owning public batch can complete from a different managed thread because ownership follows the batch token, not the thread id.<br/>
    /// This keeps the current contract compatible with async or scheduled owners that serialize the session but resume on another thread.<br/>
    /// </summary>
    private static void RunPublicCrossThreadOwnerTokenProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });
        using LibraDexIndex<long, long> index = catalog.Indexes["concurrency"]["cross-thread"].Int64Keys<long>().Create();
        LibraDexBatch<long, long> active = index.BeginBatch();

        Task.Run(() =>
        {
            active.Abort();
            active.Dispose();
        }).GetAwaiter().GetResult();

        using LibraDexBatch<long, long> next = index.BeginBatch();
        next.Abort();
    }

    /// <summary>
    /// Proves that a public index batch owns the session durability overlay and unrelated immediate writes are rejected before they can stage into that active overlay.<br/>
    /// This keeps batch publication cadence explicit instead of letting no-batch callers accidentally join another index's batch window.<br/>
    /// </summary>
    private static void RunPublicActiveIndexBatchRejectsUnrelatedImmediateMutationProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        CatalogIdentityGroupIndexes group = catalog.Indexes["batch-boundary-immediate"];
        using LibraDexIndex<long, long> left = group["left"].Int64Keys<long>().Create();
        using LibraDexIndex<long, long> right = group["right"].Int64Keys<long>().Create();
        _ = right.Insert(200, 2000);

        using LibraDexBatch<long, long> active = left.BeginBatch();
        InvalidOperationException? immediateException = null;
        try
        {
            _ = right.Insert(201, 2001);
        }
        catch (InvalidOperationException ex)
        {
            immediateException = ex;
        }

        if (immediateException is null ||
            !immediateException.Message.Contains("Immediate LibraDex mutation cannot run while another session durability batch is active", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Concurrency contract sanity expected unrelated immediate mutation to be rejected while an index batch is active.");
        }

        active.Abort();
        _ = right.Insert(201, 2001);
    }

    /// <summary>
    /// Proves that a queued writer created before an index batch cannot be reused through the active session durability overlay.<br/>
    /// The queued-writer model stages writer-local shelves, so it must wait until explicit batch publication or abort closes the session-owned dirty view.<br/>
    /// </summary>
    private static void RunPublicActiveIndexBatchRejectsQueuedWriterProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        CatalogIdentityGroupIndexes group = catalog.Indexes["batch-boundary-queued"];
        using LibraDexIndex<long, long> left = group["left"].Int64Keys<long>().Create();
        using LibraDexIndex<long, long> right = group["right"].Int64Keys<long>().Create();
        _ = right.Insert(300, 3000);
        LibraDexQueuedWriter<long, long> writer = right.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);

        using LibraDexBatch<long, long> active = left.BeginBatch();
        InvalidOperationException? queuedException = null;
        try
        {
            _ = writer.Insert(301, 3001);
        }
        catch (InvalidOperationException ex)
        {
            queuedException = ex;
        }

        if (queuedException is null ||
            !queuedException.Message.Contains("generic queued writer cannot run while a session durability batch is active", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Concurrency contract sanity expected queued writer mutation to be rejected while an index batch is active.");
        }

        active.Abort();
        LibraDexGenericInsertResult result = writer.Insert(301, 3001);
        if (!result.Inserted ||
            result.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext)
        {
            throw new InvalidDataException("Concurrency contract sanity expected queued writer to resume after the index batch closed.");
        }
    }

    /// <summary>
    /// Proves that fixed-scalar metadata-backed null-route mutation cannot run while an unrelated session durability batch is active.<br/>
    /// Value-route exact deletes already pass through the exact tuple guard; this covers scalar key-state delete and rekey entry points that reach metadata-backed route helpers directly.<br/>
    /// </summary>
    private static void RunPublicActiveIndexBatchRejectsScalarKeyStateMutationProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        CatalogIdentityGroupIndexes group = catalog.Indexes["batch-boundary-scalar-key-state"];
        using LibraDexIndex<long, long> owner = group["owner"].Int64Keys<long>().Create();
        using LibraDexIndex<long, long> value = group["value"].Int64Keys<long>().Create();
        _ = value.Insert(ScalarNull.Null, 5101);

        using LibraDexBatch<long, long> active = owner.BeginBatch();
        InvalidOperationException? deleteException = null;
        try
        {
            _ = value.Delete(ScalarNull.Null, 5101);
        }
        catch (InvalidOperationException ex)
        {
            deleteException = ex;
        }

        if (deleteException is null ||
            !deleteException.Message.Contains("Immediate LibraDex mutation cannot run while another session durability batch is active", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Concurrency contract sanity expected scalar key-state delete to be rejected while an index batch is active.");
        }

        active.Abort();
        if (!value.Delete(ScalarNull.Null, 5101))
        {
            throw new InvalidDataException("Concurrency contract sanity expected scalar key-state delete to resume after the index batch closed.");
        }
    }

    /// <summary>
    /// Proves that string null/empty key-state writes cannot bypass the active session durability-batch boundary.<br/>
    /// Non-empty `VS8` string writes enter their own batch and were already rejected by the session; this probe covers the metadata-backed key-state route that mutates directly.<br/>
    /// </summary>
    private static void RunPublicActiveIndexBatchRejectsStringKeyStateMutationProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        CatalogIdentityGroupIndexes group = catalog.Indexes["batch-boundary-string"];
        using LibraDexIndex<long, long> owner = group["owner"].Int64Keys<long>().Create();
        using LibraDexStringScalar8Index text = group["text"].String.Create(stringKeys: StringKeys.Exact);

        using LibraDexBatch<long, long> active = owner.BeginBatch();
        InvalidOperationException? stringException = null;
        try
        {
            _ = text.Insert(null, 5001);
        }
        catch (InvalidOperationException ex)
        {
            stringException = ex;
        }

        if (stringException is null ||
            !stringException.Message.Contains("Immediate LibraDex string mutation cannot run while another session durability batch is active", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Concurrency contract sanity expected string key-state mutation to be rejected while an index batch is active.");
        }

        active.Abort();
        LibraDexGenericInsertResult result = text.Insert(null, 5001);
        if (!result.Inserted)
        {
            throw new InvalidDataException("Concurrency contract sanity expected string key-state mutation to resume after the index batch closed.");
        }
    }

    /// <summary>
    /// Proves routed composite mutation cannot rewrite its in-memory/durable route tree while another index owns the session durability boundary.<br/>
    /// Composite storage is not a fixed-scalar shelf-local writer-context path, so the current stable behavior is explicit rejection rather than accidental overlap.<br/>
    /// </summary>
    private static void RunPublicActiveIndexBatchRejectsCompositeMutationProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        CatalogIdentityGroupIndexes group = catalog.Indexes["batch-boundary-composite"];
        using LibraDexIndex<long, long> owner = group["owner"].Int64Keys<long>().Create();
        IIndex composite = group["tenantUser"].Composite<ulong>(C.Text("tenant"), C.Text("user")).Create();

        using LibraDexBatch<long, long> active = owner.BeginBatch();
        InvalidOperationException? compositeException = null;
        try
        {
            _ = composite.Insert(Key.Of("tenant-a", "user-a"), 5201UL);
        }
        catch (InvalidOperationException ex)
        {
            compositeException = ex;
        }

        if (compositeException is null ||
            !compositeException.Message.Contains("Immediate LibraDex composite mutation cannot run while another session durability batch is active", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Concurrency contract sanity expected composite mutation to be rejected while an index batch is active.");
        }

        active.Abort();
        LibraDexGenericInsertResult result = composite.Insert(Key.Of("tenant-a", "user-a"), 5201UL);
        if (!result.Inserted)
        {
            throw new InvalidDataException("Concurrency contract sanity expected composite mutation to resume after the index batch closed.");
        }
    }

    /// <summary>
    /// Proves that group-level batching still accepts participating index inserts after the session-wide immediate-write guard was added.<br/>
    /// The participating inserts are intentionally routed through the group batch manager before the guard checks for unrelated no-batch mutation.<br/>
    /// </summary>
    private static void RunPublicActiveGroupBatchStillAcceptsParticipatingInsertsProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        CatalogIdentityGroupIndexes group = catalog.Indexes["group-batch-boundary"];
        using LibraDexIndex<long, long> left = group["left"].Int64Keys<long>().Create();
        using LibraDexIndex<long, long> right = group["right"].Int64Keys<long>().Create();

        group.Batch.Enable();
        _ = left.Insert(400, 4000);
        _ = right.Insert(401, 4001);
        LibraDexGenericBatchCommitResult publish = group.Batch.PublishAndDisable();
        if (publish.InsertedCount != 2)
        {
            throw new InvalidDataException("Concurrency contract sanity expected group batch participating inserts to publish together.");
        }
    }

    /// <summary>
    /// Proves the minimum Abraxas read/write contract: materialized adapter reads are disconnected after return, and detailed diagnostics reject them while a same-session write window is active.<br/>
    /// This does not prove live cursor safety or a published-reader view; those remain future concurrency modes.<br/>
    /// </summary>
    private static void RunAbraxasMaterializedReadBoundaryProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        CatalogIdentityGroupIndexes group = catalog.Indexes["concurrency"];
        using LibraDexIndex<long, long> index = group["value"].Int64Keys<long>().Create();
        index.Insert(10, 100);
        index.Insert(11, 110);

        AbraxasIdentityQueryAdapter<long> adapter = group.AbraxasIdentityQuery<long>();
        LibraDexConditionEndCondition condition = LibraDexCondition
            .ForGroup("concurrency")
            .Index("value").AsInt64.GreaterOrEqual(10)
            .EndCondition;

        IReadOnlyList<long> first = adapter.Get(condition, deduplication: IdentityDeduplication.Preserve);
        if (first.Count != 2 || first[0] != 100 || first[1] != 110)
        {
            throw new InvalidDataException("Abraxas materialized read boundary did not return the expected initial identity list.");
        }

        index.Insert(12, 120);
        if (first.Count != 2 || first.Contains(120))
        {
            throw new InvalidDataException("Abraxas materialized read boundary returned a live list instead of a disconnected materialized result.");
        }

        IReadOnlyList<long> second = adapter.Get(condition, deduplication: IdentityDeduplication.Preserve);
        if (second.Count != 3 || second[0] != 100 || second[1] != 110 || second[2] != 120)
        {
            throw new InvalidDataException("Abraxas materialized read boundary did not observe the later committed identity on the next read.");
        }

        using LibraDexBatch<long, long> active = index.BeginBatch();
        _ = active.Insert(13, 130);

        InvalidOperationException? activeWriteException = null;
        try
        {
            _ = adapter.Get(condition, deduplication: IdentityDeduplication.Preserve);
        }
        catch (InvalidOperationException ex)
        {
            activeWriteException = ex;
        }

        if (activeWriteException is null ||
            !activeWriteException.Message.Contains("materialized Abraxas read", StringComparison.Ordinal) ||
            !activeWriteException.Message.Contains("pending writer state", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Abraxas materialized read boundary expected active same-session writes to fail fast under detailed diagnostics.");
        }

        active.Abort();
    }

    /// <summary>
    /// Proves queued cold-route initialization no longer enters the broad `SS8-8` writer-operation write gate when it targets a different index root.<br/>
    /// The probe holds one index's writer-context staging gate open; a cold first write into another index must still complete because the specialized unset-route path uses only that index's topology gate plus the storage publication boundary.<br/>
    /// </summary>
    private static void RunInternalGenericScalar8Scalar8ColdRouteNarrowFallbackProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        CatalogIdentityGroupIndexes group = catalog.Indexes["generic-cold-route-narrow-fallback"];
        using LibraDexIndex<long, long> staged = group["staged"].Int64Keys<long>().Create();
        using LibraDexIndex<long, long> cold = group["cold"].Int64Keys<long>().Create();
        _ = staged.Insert(100, 1000);
        _ = cold.Insert(200, 2000);

        LibraDexQueuedWriter<long, long> coldWriter = cold.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);
        staged.Session.EnterScalar8Scalar8WriterContextStaging(staged.RootRouterOffset);
        try
        {
            Task<LibraDexGenericInsertResult> coldTask = Task.Run(() => coldWriter.Insert(long.MinValue + 901, 9001));
            if (!coldTask.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new InvalidDataException("Generic SS8-8 cold-route narrow fallback was blocked by an unrelated index writer-context staging gate.");
            }

            LibraDexGenericInsertResult coldResult = coldTask.GetAwaiter().GetResult();
            if (!coldResult.Inserted ||
                !coldResult.CreatedInitialShelfRoute ||
                coldResult.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.NarrowTopologyPublisher)
            {
                throw new InvalidDataException($"Generic SS8-8 cold-route narrow fallback expected a serialized cold-route insert. inserted={coldResult.Inserted} createdRoute={coldResult.CreatedInitialShelfRoute} path={coldResult.QueuedInsertPath}.");
            }
        }
        finally
        {
            staged.Session.ExitScalar8Scalar8WriterContextStaging(staged.RootRouterOffset);
        }

        long[] identities = new long[2];
        LibraDexGenericRangeReadResult read = cold.ReadRange(long.MinValue + 901, long.MinValue + 901, identities);
        if (read.IdentityCount != 1 ||
            identities[0] != 9001)
        {
            throw new InvalidDataException("Generic SS8-8 cold-route narrow fallback insert was not readable.");
        }
    }

    /// <summary>
    /// Proves cold-route `SS8-8` topology ownership is rooted at the exact root-prefix domain instead of a whole-index queue.<br/>
    /// The probe holds the first cold root-prefix owner after admission and before route publication, then verifies a different root-prefix writer reaches its own owner while a same-prefix writer cannot enter until the held owner is released.<br/>
    /// This is the deterministic proof for the physical-library rule: different empty shelves can be initialized independently, while writes to the same shelf/router prefix still synchronize.<br/>
    /// </summary>
    private static void RunInternalScalar8Scalar8ColdRootPrefixOwnerProbe()
    {
        using Scalar8Scalar8Index index = Indexes.SS88.Create(
            backingKind: DataKernelBackingKind.Memory,
            name: "cold-root-prefix-owner-ss88",
            options: CreateDesignPerfOptions(),
            developerMetadata: CreateDesignPerfMetadata(9918),
            telemetryOptions: DataKernelTelemetryOptions.FromLevel(LibraDexDiagnosticsLevel.Detailed));

        Scalar8Scalar8QueuedWriter writer = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);
        const ulong firstKey = 0x1000_0000_0000_0001UL;
        const ulong differentPrefixKey = 0x2000_0000_0000_0001UL;
        const ulong samePrefixKey = 0x1000_0000_0000_0002UL;
        using ManualResetEventSlim firstOwnerEntered = new(false);
        using ManualResetEventSlim releaseFirstOwner = new(false);
        using ManualResetEventSlim differentOwnerEntered = new(false);
        using ManualResetEventSlim sameOwnerEntered = new(false);
        int firstPrefixOwnerEntries = 0;

        LibraDexFileSession.Scalar8Scalar8TopologyOwnerEnteredForValidation = (kind, rootRouterOffset, ownerOffset, prefixByte) =>
        {
            if (kind != 1 || rootRouterOffset != index.Handle.RootRouterOffset || ownerOffset != 0)
            {
                return;
            }

            if (prefixByte == 0x10)
            {
                int entry = Interlocked.Increment(ref firstPrefixOwnerEntries);
                if (entry == 1)
                {
                    firstOwnerEntered.Set();
                    if (!releaseFirstOwner.Wait(TimeSpan.FromSeconds(10)))
                    {
                        throw new InvalidDataException("SS8-8 cold root-prefix owner probe timed out while holding the first prefix owner.");
                    }
                }
                else
                {
                    sameOwnerEntered.Set();
                }
            }
            else if (prefixByte == 0x20)
            {
                differentOwnerEntered.Set();
            }
        };

        try
        {
            Task<Scalar8Scalar8EncodedInsertResult> firstTask = Task.Run(() => writer.InsertEncoded(firstKey, 1001, allowDuplicateKeys: true));
            if (!firstOwnerEntered.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new InvalidDataException("SS8-8 cold root-prefix owner probe did not enter the held first-prefix owner.");
            }

            Task<Scalar8Scalar8EncodedInsertResult> differentPrefixTask = Task.Run(() => writer.InsertEncoded(differentPrefixKey, 2001, allowDuplicateKeys: true));
            if (!differentOwnerEntered.Wait(TimeSpan.FromSeconds(10)))
            {
                releaseFirstOwner.Set();
                throw new InvalidDataException("SS8-8 cold root-prefix owner probe expected a different root prefix to enter its own topology owner while the first prefix was held.");
            }

            Task<Scalar8Scalar8EncodedInsertResult> samePrefixTask = Task.Run(() => writer.InsertEncoded(samePrefixKey, 1002, allowDuplicateKeys: true));
            if (sameOwnerEntered.Wait(TimeSpan.FromMilliseconds(100)))
            {
                releaseFirstOwner.Set();
                throw new InvalidDataException("SS8-8 cold root-prefix owner probe allowed a same-prefix writer into the held owner domain.");
            }

            releaseFirstOwner.Set();
            Task.WaitAll(firstTask, differentPrefixTask, samePrefixTask);
            Scalar8Scalar8EncodedInsertResult firstResult = firstTask.GetAwaiter().GetResult();
            Scalar8Scalar8EncodedInsertResult differentResult = differentPrefixTask.GetAwaiter().GetResult();
            Scalar8Scalar8EncodedInsertResult sameResult = samePrefixTask.GetAwaiter().GetResult();
            if (firstResult.Outcome != Scalar8Scalar8EncodedInsertOutcome.Inserted ||
                !firstResult.CreatedInitialShelfRoute ||
                firstResult.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.NarrowTopologyPublisher)
            {
                throw new InvalidDataException($"SS8-8 cold root-prefix owner probe first insert attribution was wrong. outcome={firstResult.Outcome} createdRoute={firstResult.CreatedInitialShelfRoute} path={firstResult.QueuedInsertPath}.");
            }

            if (differentResult.Outcome != Scalar8Scalar8EncodedInsertOutcome.Inserted ||
                !differentResult.CreatedInitialShelfRoute ||
                differentResult.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.NarrowTopologyPublisher)
            {
                throw new InvalidDataException($"SS8-8 cold root-prefix owner probe different-prefix insert attribution was wrong. outcome={differentResult.Outcome} createdRoute={differentResult.CreatedInitialShelfRoute} path={differentResult.QueuedInsertPath}.");
            }

            if (sameResult.Outcome != Scalar8Scalar8EncodedInsertOutcome.Inserted)
            {
                throw new InvalidDataException($"SS8-8 cold root-prefix owner probe same-prefix insert failed after owner release. outcome={sameResult.Outcome} path={sameResult.QueuedInsertPath}.");
            }
        }
        finally
        {
            releaseFirstOwner.Set();
            LibraDexFileSession.Scalar8Scalar8TopologyOwnerEnteredForValidation = null;
        }

        ulong[] identities = new ulong[4];
        Scalar8Scalar8EncodedRangeReadResult read = index.ReadEncodedRange(firstKey, differentPrefixKey, identities);
        if (read.IdentityCount != 3 ||
            identities[0] != 1001 ||
            identities[1] != 1002 ||
            identities[2] != 2001)
        {
            throw new InvalidDataException("SS8-8 cold root-prefix owner probe inserts were not readable in sorted range order.");
        }
    }

    /// <summary>
    /// Proves queued root-prefix split fallback no longer enters the broad `SS8-8` writer-operation write gate when it targets a different index root.<br/>
    /// The fixture creates one full shelf owned by two root prefixes, then holds an unrelated index's writer-context staging gate while the queued split completes through the narrowed root-prefix split publisher.<br/>
    /// </summary>
    private static void RunInternalScalar8Scalar8RootPrefixSplitNarrowFallbackProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        CatalogIdentityGroupIndexes group = catalog.Indexes["root-prefix-split-narrow-fallback"];
        using LibraDexIndex<long, long> staged = group["staged"].Int64Keys<long>().Create();
        using LibraDexIndex<long, long> split = group["split"].Int64Keys<long>().Create();
        _ = staged.Insert(100, 1000);

        Scalar8Scalar8Profile profile = split.GetScalar8Scalar8Profile();
        ulong[] encodedKeys = new ulong[profile.MaxItemCount];
        ulong[] encodedIdentities = new ulong[profile.MaxItemCount];
        const ulong leftBase = 0x2000_0000_0000_0000UL;
        const ulong rightBase = 0x2100_0000_0000_0000UL;
        int leftCount = profile.MaxItemCount / 2;
        for (int i = 0; i < profile.MaxItemCount; i++)
        {
            encodedKeys[i] = i < leftCount
                ? leftBase + (ulong)i
                : rightBase + (ulong)(i - leftCount);
            encodedIdentities[i] = LibraDexGenericScalarCodec<long>.Encode8(700_000L + i);
        }

        byte[] rootPrefixes = [0x20, 0x21];
        _ = split.Session.CreateScalar8Scalar8ShelfAndLinkRootRoutes(
            split.RootRouterOffset,
            profile,
            encodedKeys,
            encodedIdentities,
            rootPrefixes);

        using Scalar8Scalar8Index encodedSplit = new(
            split.Session,
            new Scalar8Scalar8IndexHandle(split.RootRouterOffset, profile),
            slotIndex: 0,
            name: "root-prefix-split-narrow-fallback-encoded",
            ownsSession: false);
        Scalar8Scalar8QueuedWriter queuedWriter = encodedSplit.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);
        ulong incomingKey = rightBase + (ulong)profile.MaxItemCount;
        ulong incomingIdentity = LibraDexGenericScalarCodec<long>.Encode8(799_999);

        staged.Session.EnterScalar8Scalar8WriterContextStaging(staged.RootRouterOffset);
        Scalar8Scalar8EncodedInsertResult splitResult;
        try
        {
            Task<Scalar8Scalar8EncodedInsertResult> splitTask = Task.Run(() => queuedWriter.InsertEncoded(incomingKey, incomingIdentity, allowDuplicateKeys: true));
            if (!splitTask.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new InvalidDataException("SS8-8 root-prefix split narrow fallback was blocked by an unrelated index writer-context staging gate.");
            }

            splitResult = splitTask.GetAwaiter().GetResult();
            if (splitResult.Outcome != Scalar8Scalar8EncodedInsertOutcome.Inserted ||
                splitResult.StructuralKind != Scalar8Scalar8RoutedInsertKind.RootPrefixSplit ||
                splitResult.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.NarrowTopologyPublisher)
            {
                throw new InvalidDataException($"SS8-8 root-prefix split narrow fallback expected a serialized root-prefix split. outcome={splitResult.Outcome} kind={splitResult.StructuralKind} path={splitResult.QueuedInsertPath}.");
            }
        }
        finally
        {
            staged.Session.ExitScalar8Scalar8WriterContextStaging(staged.RootRouterOffset);
        }

        ulong[] identities = new ulong[profile.MaxItemCount + 1];
        Scalar8Scalar8EncodedRangeReadResult read = encodedSplit.ReadEncodedRange(leftBase, incomingKey, identities);
        if (read.IdentityCount != profile.MaxItemCount + 1 ||
            identities[profile.MaxItemCount] != incomingIdentity)
        {
            throw new InvalidDataException("SS8-8 root-prefix split narrow fallback insert was not readable.");
        }
    }

    /// <summary>
    /// Proves queued root-level shelf-transform split fallback no longer enters the broad `SS8-8` writer-operation write gate when it targets a different index root.<br/>
    /// The fixture fills a same-root-prefix shelf, then holds an unrelated index's writer-context staging gate while the queued insert transforms that full shelf into a child router and appends replacement shelves.<br/>
    /// </summary>
    private static void RunInternalScalar8Scalar8RootShelfTransformNarrowFallbackProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        CatalogIdentityGroupIndexes group = catalog.Indexes["root-shelf-transform-narrow-fallback"];
        using LibraDexIndex<long, long> staged = group["staged"].Int64Keys<long>().Create();
        using LibraDexIndex<long, long> split = group["split"].Int64Keys<long>().Create();
        _ = staged.Insert(100, 1000);

        Scalar8Scalar8Profile profile = split.GetScalar8Scalar8Profile();
        using Scalar8Scalar8Index encodedSplit = new(
            split.Session,
            new Scalar8Scalar8IndexHandle(split.RootRouterOffset, profile),
            slotIndex: 0,
            name: "root-shelf-transform-narrow-fallback-encoded",
            ownsSession: false);

        const ulong leftBase = 0x5510_0000_0000_0000UL;
        for (int i = 0; i < profile.MaxItemCount; i++)
        {
            Scalar8Scalar8EncodedInsertResult fill = encodedSplit.InsertEncoded(leftBase + (ulong)i, (ulong)(810_000 + i), allowDuplicateKeys: true);
            if (fill.Outcome != Scalar8Scalar8EncodedInsertOutcome.Inserted)
            {
                throw new InvalidDataException($"SS8-8 root shelf transform fixture failed at item {i}; outcome={fill.Outcome}.");
            }
        }

        Scalar8Scalar8QueuedWriter queuedWriter = encodedSplit.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);
        ulong incomingKey = 0x5520_0000_0000_0001UL;
        ulong incomingIdentity = 899_999;

        staged.Session.EnterScalar8Scalar8WriterContextStaging(staged.RootRouterOffset);
        Scalar8Scalar8EncodedInsertResult splitResult;
        try
        {
            Task<Scalar8Scalar8EncodedInsertResult> splitTask = Task.Run(() => queuedWriter.InsertEncoded(incomingKey, incomingIdentity, allowDuplicateKeys: true));
            if (!splitTask.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new InvalidDataException("SS8-8 root shelf transform narrow fallback was blocked by an unrelated index writer-context staging gate.");
            }

            splitResult = splitTask.GetAwaiter().GetResult();
            if (splitResult.Outcome != Scalar8Scalar8EncodedInsertOutcome.Inserted ||
                splitResult.StructuralKind != Scalar8Scalar8RoutedInsertKind.ShelfTransformSplit ||
                splitResult.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.NarrowTopologyPublisher)
            {
                throw new InvalidDataException($"SS8-8 root shelf transform narrow fallback expected a serialized shelf-transform split. outcome={splitResult.Outcome} kind={splitResult.StructuralKind} path={splitResult.QueuedInsertPath}.");
            }
        }
        finally
        {
            staged.Session.ExitScalar8Scalar8WriterContextStaging(staged.RootRouterOffset);
        }

        ulong[] identities = new ulong[profile.MaxItemCount + 1];
        Scalar8Scalar8EncodedRangeReadResult read = encodedSplit.ReadEncodedRange(leftBase, incomingKey, identities);
        if (read.IdentityCount != profile.MaxItemCount + 1 ||
            identities[profile.MaxItemCount] != incomingIdentity)
        {
            throw new InvalidDataException("SS8-8 root shelf transform narrow fallback insert was not readable.");
        }
    }

    /// <summary>
    /// Proves queued walked parent-route split fallback no longer enters the broad `SS8-8` writer-operation write gate when it targets a different index root.<br/>
    /// The fixture creates a full shelf behind a child router, then holds an unrelated index's writer-context staging gate while the queued insert rewrites the child-router route through the narrowed parent-route split publisher.<br/>
    /// </summary>
    private static void RunInternalScalar8Scalar8ParentRouteSplitNarrowFallbackProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        CatalogIdentityGroupIndexes group = catalog.Indexes["parent-route-split-narrow-fallback"];
        using LibraDexIndex<long, long> staged = group["staged"].Int64Keys<long>().Create();
        using LibraDexIndex<long, long> split = group["split"].Int64Keys<long>().Create();
        _ = staged.Insert(100, 1000);

        Scalar8Scalar8Profile profile = split.GetScalar8Scalar8Profile();
        ulong[] encodedKeys = new ulong[profile.MaxItemCount];
        ulong[] encodedIdentities = new ulong[profile.MaxItemCount];
        const ulong leftBase = 0x6610_0000_0000_0000UL;
        const ulong rightBase = 0x6620_0000_0000_0000UL;
        int leftCount = profile.MaxItemCount / 2;
        for (int i = 0; i < profile.MaxItemCount; i++)
        {
            encodedKeys[i] = i < leftCount
                ? leftBase + (ulong)i
                : rightBase + (ulong)(i - leftCount);
            encodedIdentities[i] = LibraDexGenericScalarCodec<long>.Encode8(910_000L + i);
        }

        byte[] childPrefixes = [0x10, 0x20];
        _ = split.Session.CreateScalar8Scalar8ShelfBehindChildRouterRoutes(
            split.RootRouterOffset,
            rootPrefixByte: 0x66,
            childPrefixes,
            profile,
            encodedKeys,
            encodedIdentities);

        using Scalar8Scalar8Index encodedSplit = new(
            split.Session,
            new Scalar8Scalar8IndexHandle(split.RootRouterOffset, profile),
            slotIndex: 0,
            name: "parent-route-split-narrow-fallback-encoded",
            ownsSession: false);
        Scalar8Scalar8QueuedWriter queuedWriter = encodedSplit.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);
        ulong incomingKey = rightBase + (ulong)profile.MaxItemCount;
        ulong incomingIdentity = LibraDexGenericScalarCodec<long>.Encode8(999_999);

        staged.Session.EnterScalar8Scalar8WriterContextStaging(staged.RootRouterOffset);
        Scalar8Scalar8EncodedInsertResult splitResult;
        try
        {
            Task<Scalar8Scalar8EncodedInsertResult> splitTask = Task.Run(() => queuedWriter.InsertEncoded(incomingKey, incomingIdentity, allowDuplicateKeys: true));
            if (!splitTask.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new InvalidDataException("SS8-8 parent-route split narrow fallback was blocked by an unrelated index writer-context staging gate.");
            }

            splitResult = splitTask.GetAwaiter().GetResult();
            if (splitResult.Outcome != Scalar8Scalar8EncodedInsertOutcome.Inserted ||
                splitResult.StructuralKind != Scalar8Scalar8RoutedInsertKind.WalkedParentRouteSplit ||
                splitResult.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.NarrowTopologyPublisher)
            {
                throw new InvalidDataException($"SS8-8 parent-route split narrow fallback expected a serialized walked parent-route split. outcome={splitResult.Outcome} kind={splitResult.StructuralKind} path={splitResult.QueuedInsertPath}.");
            }
        }
        finally
        {
            staged.Session.ExitScalar8Scalar8WriterContextStaging(staged.RootRouterOffset);
        }

        ulong[] identities = new ulong[profile.MaxItemCount + 1];
        Scalar8Scalar8EncodedRangeReadResult read = encodedSplit.ReadEncodedRange(leftBase, incomingKey, identities);
        if (read.IdentityCount != profile.MaxItemCount + 1 ||
            identities[profile.MaxItemCount] != incomingIdentity)
        {
            throw new InvalidDataException("SS8-8 parent-route split narrow fallback insert was not readable.");
        }
    }

    /// <summary>
    /// Proves queued duplicate-key overflow fallback no longer enters the broad `SS8-8` writer-operation write gate when it targets a different index root.<br/>
    /// The fixture fills one ordinary shelf with the same key, then holds an unrelated index's writer-context staging gate while the queued insert deepens the route and converts the exhausted duplicate-key route into a terminal identity root.<br/>
    /// </summary>
    private static void RunInternalScalar8Scalar8DuplicateKeyOverflowNarrowFallbackProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        CatalogIdentityGroupIndexes group = catalog.Indexes["duplicate-key-overflow-narrow-fallback"];
        using LibraDexIndex<long, long> staged = group["staged"].Int64Keys<long>().Create();
        using LibraDexIndex<long, long> duplicate = group["duplicate"].Int64Keys<long>().Create();
        _ = staged.Insert(100, 1000);

        Scalar8Scalar8Profile profile = duplicate.GetScalar8Scalar8Profile();
        using Scalar8Scalar8Index encodedDuplicate = new(
            duplicate.Session,
            new Scalar8Scalar8IndexHandle(duplicate.RootRouterOffset, profile),
            slotIndex: 0,
            name: "duplicate-key-overflow-narrow-fallback-encoded",
            ownsSession: false);

        const ulong duplicateKey = 0x7A00_0000_0000_0001UL;
        for (int i = 0; i < profile.MaxItemCount; i++)
        {
            Scalar8Scalar8EncodedInsertResult fill = encodedDuplicate.InsertEncoded(duplicateKey, (ulong)(970_000 + i), allowDuplicateKeys: true);
            if (fill.Outcome != Scalar8Scalar8EncodedInsertOutcome.Inserted)
            {
                throw new InvalidDataException($"SS8-8 duplicate-key overflow fixture failed at item {i}; outcome={fill.Outcome}.");
            }
        }

        Scalar8Scalar8QueuedWriter queuedWriter = encodedDuplicate.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);
        ulong overflowIdentity = 970_000UL + (ulong)profile.MaxItemCount;

        staged.Session.EnterScalar8Scalar8WriterContextStaging(staged.RootRouterOffset);
        Scalar8Scalar8EncodedInsertResult overflowResult;
        try
        {
            Task<Scalar8Scalar8EncodedInsertResult> overflowTask = Task.Run(() => queuedWriter.InsertEncoded(duplicateKey, overflowIdentity, allowDuplicateKeys: true));
            if (!overflowTask.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new InvalidDataException("SS8-8 duplicate-key overflow narrow fallback was blocked by an unrelated index writer-context staging gate.");
            }

            overflowResult = overflowTask.GetAwaiter().GetResult();
            if (overflowResult.Outcome != Scalar8Scalar8EncodedInsertOutcome.Inserted ||
                overflowResult.StructuralKind != Scalar8Scalar8RoutedInsertKind.WalkedNoSplit ||
                overflowResult.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.NarrowTopologyPublisher)
            {
                throw new InvalidDataException($"SS8-8 duplicate-key overflow narrow fallback expected a serialized terminal conversion insert. outcome={overflowResult.Outcome} kind={overflowResult.StructuralKind} path={overflowResult.QueuedInsertPath}.");
            }
        }
        finally
        {
            staged.Session.ExitScalar8Scalar8WriterContextStaging(staged.RootRouterOffset);
        }

        ulong[] identities = new ulong[profile.MaxItemCount + 1];
        Scalar8Scalar8EncodedRangeReadResult read = encodedDuplicate.ReadEncodedRange(duplicateKey, duplicateKey, identities);
        if (read.IdentityCount != profile.MaxItemCount + 1 ||
            identities[profile.MaxItemCount] != overflowIdentity)
        {
            throw new InvalidDataException("SS8-8 duplicate-key overflow narrow fallback insert was not readable.");
        }
    }

    /// <summary>
    /// Proves queued terminal-identity overflow fallback no longer enters the broad `SS8-8` writer-operation write gate when it targets a different index root.<br/>
    /// The fixture first establishes an exact-key terminal identity root and fills its first identity shelf, then holds an unrelated index's writer-context staging gate while the queued insert appends the next terminal identity shelf.<br/>
    /// </summary>
    private static void RunInternalScalar8Scalar8TerminalIdentityOverflowNarrowFallbackProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        CatalogIdentityGroupIndexes group = catalog.Indexes["terminal-identity-overflow-narrow-fallback"];
        using LibraDexIndex<long, long> staged = group["staged"].Int64Keys<long>().Create();
        using LibraDexIndex<long, long> terminal = group["terminal"].Int64Keys<long>().Create();
        _ = staged.Insert(100, 1000);

        Scalar8Scalar8Profile profile = terminal.GetScalar8Scalar8Profile();
        using Scalar8Scalar8Index encodedTerminal = new(
            terminal.Session,
            new Scalar8Scalar8IndexHandle(terminal.RootRouterOffset, profile),
            slotIndex: 0,
            name: "terminal-identity-overflow-narrow-fallback-encoded",
            ownsSession: false);

        const ulong duplicateKey = 0x7B00_0000_0000_0001UL;
        int terminalCapacity = TerminalIdentity8ShelfLayout.GetCapacity(profile.ShelfExtentSize);
        if (terminalCapacity <= profile.MaxItemCount)
        {
            throw new InvalidDataException("SS8-8 terminal identity overflow fixture expected terminal capacity to exceed ordinary shelf capacity.");
        }

        for (int i = 0; i < terminalCapacity; i++)
        {
            Scalar8Scalar8EncodedInsertResult fill = encodedTerminal.InsertEncoded(duplicateKey, (ulong)(980_000 + i), allowDuplicateKeys: true);
            if (fill.Outcome != Scalar8Scalar8EncodedInsertOutcome.Inserted)
            {
                throw new InvalidDataException($"SS8-8 terminal identity overflow fixture failed at item {i}; outcome={fill.Outcome}.");
            }
        }

        Scalar8Scalar8QueuedWriter queuedWriter = encodedTerminal.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);
        ulong overflowIdentity = 980_000UL + (ulong)terminalCapacity;

        staged.Session.EnterScalar8Scalar8WriterContextStaging(staged.RootRouterOffset);
        Scalar8Scalar8EncodedInsertResult overflowResult;
        try
        {
            Task<Scalar8Scalar8EncodedInsertResult> overflowTask = Task.Run(() => queuedWriter.InsertEncoded(duplicateKey, overflowIdentity, allowDuplicateKeys: true));
            if (!overflowTask.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new InvalidDataException("SS8-8 terminal identity overflow narrow fallback was blocked by an unrelated index writer-context staging gate.");
            }

            overflowResult = overflowTask.GetAwaiter().GetResult();
            if (overflowResult.Outcome != Scalar8Scalar8EncodedInsertOutcome.Inserted ||
                overflowResult.StructuralKind != Scalar8Scalar8RoutedInsertKind.WalkedNoSplit ||
                overflowResult.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.NarrowTopologyPublisher)
            {
                throw new InvalidDataException($"SS8-8 terminal identity overflow narrow fallback expected a serialized terminal identity append. outcome={overflowResult.Outcome} kind={overflowResult.StructuralKind} path={overflowResult.QueuedInsertPath}.");
            }
        }
        finally
        {
            staged.Session.ExitScalar8Scalar8WriterContextStaging(staged.RootRouterOffset);
        }

        ulong[] identities = new ulong[terminalCapacity + 1];
        Scalar8Scalar8EncodedRangeReadResult read = encodedTerminal.ReadEncodedRange(duplicateKey, duplicateKey, identities);
        if (read.IdentityCount != terminalCapacity + 1 ||
            identities[terminalCapacity] != overflowIdentity)
        {
            throw new InvalidDataException("SS8-8 terminal identity overflow narrow fallback insert was not readable.");
        }
    }

    /// <summary>
    /// Proves queued duplicate-run chain fallback no longer enters the broad `SS8-8` writer-operation write gate when it targets a different index root.<br/>
    /// The fixture crafts a full duplicate-run shelf through internal writer-context staging, then holds an unrelated index's writer-context staging gate while the queued insert appends the next duplicate-run shelf.<br/>
    /// </summary>
    private static void RunInternalScalar8Scalar8DuplicateRunChainNarrowFallbackProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        CatalogIdentityGroupIndexes group = catalog.Indexes["duplicate-run-chain-narrow-fallback"];
        using LibraDexIndex<long, long> staged = group["staged"].Int64Keys<long>().Create();
        using LibraDexIndex<long, long> duplicateRun = group["duplicate-run"].Int64Keys<long>().Create();
        _ = staged.Insert(100, 1000);

        Scalar8Scalar8Profile profile = duplicateRun.GetScalar8Scalar8Profile();
        using Scalar8Scalar8Index encodedDuplicateRun = new(
            duplicateRun.Session,
            new Scalar8Scalar8IndexHandle(duplicateRun.RootRouterOffset, profile),
            slotIndex: 0,
            name: "duplicate-run-chain-narrow-fallback-encoded",
            ownsSession: false);

        const ulong duplicateKey = 0x7C00_0000_0000_0001UL;
        _ = encodedDuplicateRun.InsertEncoded(duplicateKey, 990_000, allowDuplicateKeys: true);
        long shelfOffset = encodedDuplicateRun.Session.FindRouterTarget(encodedDuplicateRun.Handle.RootRouterOffset, 0x7C);
        if (shelfOffset == 0)
        {
            throw new InvalidDataException("SS8-8 duplicate-run chain narrow fixture did not create the route shelf.");
        }

        int duplicateRunCapacity = Scalar8Scalar8Layout.GetDuplicateRunCapacity(profile);
        byte[] duplicateRunBytes = new byte[profile.ShelfExtentSize];
        Scalar8Scalar8Layout.WriteMagic(duplicateRunBytes, Scalar8Scalar8Layout.Magic);
        Scalar8Scalar8Layout.WriteFormatVersion(duplicateRunBytes, Scalar8Scalar8Layout.FormatVersion);
        Scalar8Scalar8Layout.WriteHeaderSize(duplicateRunBytes, Scalar8Scalar8Layout.HeaderSize);
        Scalar8Scalar8Layout.WriteFlags(duplicateRunBytes, Scalar8Scalar8Layout.DuplicateRunFlag);
        Scalar8Scalar8Layout.WriteItemCount(duplicateRunBytes, checked((ushort)duplicateRunCapacity));
        Scalar8Scalar8Layout.WriteDuplicateRunKey(duplicateRunBytes, duplicateKey);
        Scalar8Scalar8Layout.WriteDuplicateRunNextOffset(duplicateRunBytes, 0);
        for (int i = 0; i < duplicateRunCapacity; i++)
        {
            Scalar8Scalar8Layout.WriteDuplicateRunIdentity(duplicateRunBytes, i, (ulong)(990_000 + i));
        }

        LibraDexWriteContext fixtureContext = encodedDuplicateRun.Session.BeginScalar8Scalar8WriteContext(encodedDuplicateRun.Handle.RootRouterOffset);
        encodedDuplicateRun.Session.StageScalar8Scalar8ShelfRewriteForWriteContext(fixtureContext, shelfOffset, profile, duplicateRunBytes);
        _ = encodedDuplicateRun.Session.PublishScalar8Scalar8WriteContext(fixtureContext);

        Scalar8Scalar8QueuedWriter queuedWriter = encodedDuplicateRun.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);
        ulong overflowIdentity = 990_000UL + (ulong)duplicateRunCapacity;

        staged.Session.EnterScalar8Scalar8WriterContextStaging(staged.RootRouterOffset);
        Scalar8Scalar8EncodedInsertResult overflowResult;
        try
        {
            Task<Scalar8Scalar8EncodedInsertResult> overflowTask = Task.Run(() => queuedWriter.InsertEncoded(duplicateKey, overflowIdentity, allowDuplicateKeys: true));
            if (!overflowTask.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new InvalidDataException("SS8-8 duplicate-run chain narrow fallback was blocked by an unrelated index writer-context staging gate.");
            }

            overflowResult = overflowTask.GetAwaiter().GetResult();
            if (overflowResult.Outcome != Scalar8Scalar8EncodedInsertOutcome.Inserted ||
                overflowResult.StructuralKind != Scalar8Scalar8RoutedInsertKind.WalkedNoSplit ||
                overflowResult.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.NarrowTopologyPublisher)
            {
                throw new InvalidDataException($"SS8-8 duplicate-run chain narrow fallback expected a serialized duplicate-run append. outcome={overflowResult.Outcome} kind={overflowResult.StructuralKind} path={overflowResult.QueuedInsertPath}.");
            }
        }
        finally
        {
            staged.Session.ExitScalar8Scalar8WriterContextStaging(staged.RootRouterOffset);
        }

        ulong[] identities = new ulong[duplicateRunCapacity + 1];
        Scalar8Scalar8EncodedRangeReadResult read = encodedDuplicateRun.ReadEncodedRange(duplicateKey, duplicateKey, identities);
        if (read.IdentityCount != duplicateRunCapacity + 1 ||
            identities[duplicateRunCapacity] != overflowIdentity)
        {
            throw new InvalidDataException($"SS8-8 duplicate-run chain narrow fallback insert was not readable. count={read.IdentityCount} expected={duplicateRunCapacity + 1} last={identities[Math.Min(read.IdentityCount, duplicateRunCapacity)]} expectedLast={overflowIdentity}.");
        }

        if (!queuedWriter.DeleteEncoded(duplicateKey, 990_001, out _, out Scalar8Scalar8QueuedInsertPath deletePath) ||
            deletePath != Scalar8Scalar8QueuedInsertPath.WriterContext)
        {
            throw new InvalidDataException($"SS8-8 duplicate-run chain shelf-local delete expected writer-context attribution but saw path={deletePath}.");
        }

        Array.Clear(identities);
        read = encodedDuplicateRun.ReadEncodedRange(duplicateKey, duplicateKey, identities);
        if (read.IdentityCount != duplicateRunCapacity ||
            identities[0] != 990_000 ||
            identities[1] != 990_002 ||
            identities[duplicateRunCapacity - 1] != overflowIdentity)
        {
            throw new InvalidDataException("SS8-8 duplicate-run chain shelf-local delete readback failed.");
        }

        if (!queuedWriter.DeleteEncoded(duplicateKey, overflowIdentity, out _, out deletePath) ||
            deletePath != Scalar8Scalar8QueuedInsertPath.WriterContext)
        {
            throw new InvalidDataException($"SS8-8 duplicate-run tail delete expected writer-context attribution but saw path={deletePath}.");
        }

        Array.Clear(identities);
        read = encodedDuplicateRun.ReadEncodedRange(duplicateKey, duplicateKey, identities);
        if (read.IdentityCount != duplicateRunCapacity - 1 ||
            identities[0] != 990_000 ||
            identities[1] != 990_002 ||
            identities[duplicateRunCapacity - 2] != 990_000UL + (ulong)(duplicateRunCapacity - 1))
        {
            throw new InvalidDataException("SS8-8 duplicate-run tail delete readback failed.");
        }
    }

    /// <summary>
    /// Proves the first internal `SS8-8` writer-context boundary: different shelves can stage independently, while same-shelf staging conflicts deterministically.<br/>
    /// Publication remains serialized through the session publication seam, so this does not enable public concurrent batches yet.<br/>
    /// </summary>
    private static void RunInternalScalar8Scalar8WriterContextProbe()
    {
        using Scalar8Scalar8Index index = Indexes.SS88.Create(
            backingKind: DataKernelBackingKind.Memory,
            name: "writer-context-ss88",
            options: CreateDesignPerfOptions(),
            developerMetadata: CreateDesignPerfMetadata(9902),
            telemetryOptions: DataKernelTelemetryOptions.FromLevel(LibraDexDiagnosticsLevel.Detailed));

        Scalar8Scalar8Profile profile = index.Handle.Profile;
        ulong keyA = 0x1100_0000_0000_0001UL;
        ulong keyB = 0x2200_0000_0000_0001UL;
        ulong keyA2 = 0x1100_0000_0000_0002UL;
        ulong keyB2 = 0x2200_0000_0000_0002UL;
        ulong keyA3 = 0x1100_0000_0000_0003UL;
        ulong keyB3 = 0x2200_0000_0000_0003UL;
        ulong keyA4 = 0x1100_0000_0000_0004UL;
        ulong keyB4 = 0x2200_0000_0000_0004UL;
        ulong keyA5 = 0x1100_0000_0000_0005UL;
        ulong keyB5 = 0x2200_0000_0000_0005UL;
        ulong keyA6 = 0x1100_0000_0000_0006UL;
        ulong keyA7 = 0x1100_0000_0000_0007UL;
        ulong keyA8 = 0x1100_0000_0000_0008UL;
        ulong keyA9 = 0x1100_0000_0000_0009UL;
        ulong keyA10 = 0x1100_0000_0000_000AUL;
        ulong keyB6 = 0x2200_0000_0000_0006UL;
        ulong keyC = 0x3300_0000_0000_0001UL;
        ulong keyD = 0x4400_0000_0000_0001UL;
        ulong keyB7 = 0x2200_0000_0000_0007UL;
        _ = index.InsertEncoded(keyA, 1001);
        _ = index.InsertEncoded(keyB, 2001);

        long shelfA = index.Session.FindRouterTarget(index.Handle.RootRouterOffset, 0x11);
        long shelfB = index.Session.FindRouterTarget(index.Handle.RootRouterOffset, 0x22);
        if (shelfA == 0 || shelfB == 0 || shelfA == shelfB)
        {
            throw new InvalidDataException("SS8-8 writer-context probe did not create two distinct root-prefix shelves.");
        }

        LibraDexWriteContext writerA = index.Session.BeginScalar8Scalar8WriteContext(index.Handle.RootRouterOffset);
        LibraDexWriteContext writerB = index.Session.BeginScalar8Scalar8WriteContext(index.Handle.RootRouterOffset);
        byte[] shelfBytesA = index.Session.ReadScalar8Scalar8ShelfBytesForWriteContext(writerA, shelfA, profile);
        byte[] shelfBytesB = index.Session.ReadScalar8Scalar8ShelfBytesForWriteContext(writerB, shelfB, profile);
        if (new Scalar8Scalar8(shelfBytesA, profile).Insert(keyA2, 1002, allowDuplicateKeys: true) != Scalar8Scalar8InsertResult.Inserted ||
            new Scalar8Scalar8(shelfBytesB, profile).Insert(keyB2, 2002, allowDuplicateKeys: true) != Scalar8Scalar8InsertResult.Inserted)
        {
            throw new InvalidDataException("SS8-8 writer-context probe could not mutate the expected shelves.");
        }

        index.Session.StageScalar8Scalar8ShelfRewriteForWriteContext(writerA, shelfA, profile, shelfBytesA);
        index.Session.StageScalar8Scalar8ShelfRewriteForWriteContext(writerB, shelfB, profile, shelfBytesB);
        _ = index.Session.PublishScalar8Scalar8WriteContext(writerB);
        _ = index.Session.PublishScalar8Scalar8WriteContext(writerA);

        ulong[] identities = new ulong[8];
        Scalar8Scalar8EncodedRangeReadResult readA = index.ReadEncodedRange(keyA, keyA2, identities);
        if (readA.IdentityCount != 2 || identities[0] != 1001 || identities[1] != 1002)
        {
            throw new InvalidDataException("SS8-8 writer-context probe did not publish writer A shelf changes.");
        }

        Array.Clear(identities);
        Scalar8Scalar8EncodedRangeReadResult readB = index.ReadEncodedRange(keyB, keyB2, identities);
        if (readB.IdentityCount != 2 || identities[0] != 2001 || identities[1] != 2002)
        {
            throw new InvalidDataException("SS8-8 writer-context probe did not publish writer B shelf changes.");
        }

        LibraDexWriteContext routedWriterA = index.BeginWriteContext();
        LibraDexWriteContext routedWriterB = index.BeginWriteContext();
        Scalar8Scalar8EncodedInsertResult routedInsertA = index.InsertEncodedForWriteContext(
            routedWriterA,
            keyA3,
            1003,
            allowDuplicateKeys: true);
        Scalar8Scalar8EncodedInsertResult routedInsertB = index.InsertEncodedForWriteContext(
            routedWriterB,
            keyB3,
            2003,
            allowDuplicateKeys: true);
        if (routedInsertA.Outcome != Scalar8Scalar8EncodedInsertOutcome.Inserted ||
            routedInsertB.Outcome != Scalar8Scalar8EncodedInsertOutcome.Inserted ||
            routedInsertA.StructuralKind != Scalar8Scalar8RoutedInsertKind.WalkedNoSplit ||
            routedInsertB.StructuralKind != Scalar8Scalar8RoutedInsertKind.WalkedNoSplit ||
            routedInsertA.PrimaryOffset != shelfA ||
            routedInsertB.PrimaryOffset != shelfB)
        {
            throw new InvalidDataException("SS8-8 writer-context routed insert did not stage the expected shelf-local no-split paths.");
        }

        _ = index.PublishWriteContext(routedWriterA);
        _ = index.PublishWriteContext(routedWriterB);

        Array.Clear(identities);
        readA = index.ReadEncodedRange(keyA, keyA3, identities);
        if (readA.IdentityCount != 3 || identities[0] != 1001 || identities[1] != 1002 || identities[2] != 1003)
        {
            throw new InvalidDataException("SS8-8 writer-context routed insert did not publish writer A changes.");
        }

        Array.Clear(identities);
        readB = index.ReadEncodedRange(keyB, keyB3, identities);
        if (readB.IdentityCount != 3 || identities[0] != 2001 || identities[1] != 2002 || identities[2] != 2003)
        {
            throw new InvalidDataException("SS8-8 writer-context routed insert did not publish writer B changes.");
        }

        using Scalar8Scalar8Writer facadeWriterA = index.BeginWriter();
        using Scalar8Scalar8Writer facadeWriterB = index.BeginWriter();
        Scalar8Scalar8EncodedInsertResult facadeInsertA = facadeWriterA.InsertEncoded(keyA4, 1004, allowDuplicateKeys: true);
        Scalar8Scalar8EncodedInsertResult facadeInsertB = facadeWriterB.InsertEncoded(keyB4, 2004, allowDuplicateKeys: true);
        if (facadeInsertA.Outcome != Scalar8Scalar8EncodedInsertOutcome.Inserted ||
            facadeInsertB.Outcome != Scalar8Scalar8EncodedInsertOutcome.Inserted ||
            facadeInsertA.StructuralKind != Scalar8Scalar8RoutedInsertKind.WalkedNoSplit ||
            facadeInsertB.StructuralKind != Scalar8Scalar8RoutedInsertKind.WalkedNoSplit ||
            facadeInsertA.PrimaryOffset != shelfA ||
            facadeInsertB.PrimaryOffset != shelfB)
        {
            throw new InvalidDataException("SS8-8 writer facade did not stage the expected shelf-local no-split paths.");
        }

        _ = facadeWriterB.Publish();
        _ = facadeWriterA.Publish();

        InvalidOperationException? completedWriterException = null;
        try
        {
            _ = facadeWriterA.InsertEncoded(0x1100_0000_0000_0005UL, 1005, allowDuplicateKeys: true);
        }
        catch (InvalidOperationException ex)
        {
            completedWriterException = ex;
        }

        if (completedWriterException is null ||
            !completedWriterException.Message.Contains("already been completed", StringComparison.Ordinal))
        {
            throw new InvalidDataException("SS8-8 writer facade expected completed writers to reject additional inserts.");
        }

        Array.Clear(identities);
        readA = index.ReadEncodedRange(keyA, keyA4, identities);
        if (readA.IdentityCount != 4 || identities[0] != 1001 || identities[1] != 1002 || identities[2] != 1003 || identities[3] != 1004)
        {
            throw new InvalidDataException("SS8-8 writer facade did not publish writer A changes.");
        }

        Array.Clear(identities);
        readB = index.ReadEncodedRange(keyB, keyB4, identities);
        if (readB.IdentityCount != 4 || identities[0] != 2001 || identities[1] != 2002 || identities[2] != 2003 || identities[3] != 2004)
        {
            throw new InvalidDataException("SS8-8 writer facade did not publish writer B changes.");
        }

        RunOverlappingScalar8Scalar8WriterFacadeProbe(index, keyA5, 1005, shelfA, keyB5, 2005, shelfB);

        Array.Clear(identities);
        readA = index.ReadEncodedRange(keyA, keyA5, identities);
        if (readA.IdentityCount != 5 || identities[0] != 1001 || identities[1] != 1002 || identities[2] != 1003 || identities[3] != 1004 || identities[4] != 1005)
        {
            throw new InvalidDataException("SS8-8 overlapping writer facade probe did not publish writer A changes.");
        }

        Array.Clear(identities);
        readB = index.ReadEncodedRange(keyB, keyB5, identities);
        if (readB.IdentityCount != 5 || identities[0] != 2001 || identities[1] != 2002 || identities[2] != 2003 || identities[3] != 2004 || identities[4] != 2005)
        {
            throw new InvalidDataException("SS8-8 overlapping writer facade probe did not publish writer B changes.");
        }

        Scalar8Scalar8QueuedWriter queuedWriter = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);
        RunOverlappingScalar8Scalar8QueuedWriterProbe(queuedWriter, keyA6, 1006, shelfA, keyA7, 1007, shelfA);

        Array.Clear(identities);
        readA = index.ReadEncodedRange(keyA, keyA7, identities);
        if (readA.IdentityCount != 7 ||
            identities[0] != 1001 ||
            identities[1] != 1002 ||
            identities[2] != 1003 ||
            identities[3] != 1004 ||
            identities[4] != 1005 ||
            identities[5] != 1006 ||
            identities[6] != 1007)
        {
            throw new InvalidDataException("SS8-8 queued writer did not serialize overlapping same-shelf inserts.");
        }

        RunOverlappingScalar8Scalar8QueuedWriterProbe(queuedWriter, keyA8, 1008, shelfA, keyB6, 2006, shelfB);

        Array.Clear(identities);
        readA = index.ReadEncodedRange(keyA, keyA8, identities);
        if (readA.IdentityCount != 8 ||
            identities[0] != 1001 ||
            identities[1] != 1002 ||
            identities[2] != 1003 ||
            identities[3] != 1004 ||
            identities[4] != 1005 ||
            identities[5] != 1006 ||
            identities[6] != 1007 ||
            identities[7] != 1008)
        {
            throw new InvalidDataException("SS8-8 queued writer did not publish overlapping different-shelf insert A.");
        }

        Array.Clear(identities);
        readB = index.ReadEncodedRange(keyB, keyB6, identities);
        if (readB.IdentityCount != 6 ||
            identities[0] != 2001 ||
            identities[1] != 2002 ||
            identities[2] != 2003 ||
            identities[3] != 2004 ||
            identities[4] != 2005 ||
            identities[5] != 2006)
        {
            throw new InvalidDataException("SS8-8 queued writer did not publish overlapping different-shelf insert B.");
        }

        Scalar8Scalar8EncodedInsertResult queuedFallback = queuedWriter.InsertEncoded(keyC, 3001, allowDuplicateKeys: true);
        if (queuedFallback.Outcome != Scalar8Scalar8EncodedInsertOutcome.Inserted ||
            !queuedFallback.CreatedInitialShelfRoute ||
            queuedFallback.StructuralKind != Scalar8Scalar8RoutedInsertKind.WalkedNoSplit ||
            queuedFallback.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.NarrowTopologyPublisher)
        {
            throw new InvalidDataException("SS8-8 queued writer did not fall back to the serialized insert path for missing root-prefix routes.");
        }

        Array.Clear(identities);
        Scalar8Scalar8EncodedRangeReadResult readC = index.ReadEncodedRange(keyC, keyC, identities);
        if (readC.IdentityCount != 1 || identities[0] != 3001)
        {
            throw new InvalidDataException("SS8-8 queued writer fallback insert was not readable.");
        }

        RunOverlappingScalar8Scalar8QueuedFallbackAndWriterContextProbe(queuedWriter, keyD, 4001, keyB7, 2007, shelfB);

        Array.Clear(identities);
        Scalar8Scalar8EncodedRangeReadResult readD = index.ReadEncodedRange(keyD, keyD, identities);
        if (readD.IdentityCount != 1 || identities[0] != 4001)
        {
            throw new InvalidDataException("SS8-8 queued writer fallback topology gate insert was not readable.");
        }

        Array.Clear(identities);
        readB = index.ReadEncodedRange(keyB, keyB7, identities);
        if (readB.IdentityCount != 7 ||
            identities[0] != 2001 ||
            identities[1] != 2002 ||
            identities[2] != 2003 ||
            identities[3] != 2004 ||
            identities[4] != 2005 ||
            identities[5] != 2006 ||
            identities[6] != 2007)
        {
            throw new InvalidDataException("SS8-8 writer-context staging did not survive overlapping queued fallback topology work.");
        }

        LibraDexWriteContext owner = index.BeginWriteContext();
        LibraDexWriteContext competitor = index.BeginWriteContext();
        _ = index.InsertEncodedForWriteContext(
            owner,
            keyA9,
            1009,
            allowDuplicateKeys: true);

        InvalidOperationException? conflictException = null;
        try
        {
            _ = index.InsertEncodedForWriteContext(
                competitor,
                keyA10,
                1010,
                allowDuplicateKeys: true);
        }
        catch (InvalidOperationException ex)
        {
            conflictException = ex;
        }

        if (conflictException is null ||
            !conflictException.Message.Contains("already owned by another LibraDex writer context", StringComparison.Ordinal))
        {
            throw new InvalidDataException("SS8-8 writer-context probe expected a deterministic same-shelf conflict.");
        }

        index.AbortWriteContext(competitor);
        index.AbortWriteContext(owner);
    }

    /// <summary>
    /// Proves `SS8-8` writer-context publication rejects staged shelf bytes when the selecting route still points at the same offset but that offset no longer contains an `SS8-8` shelf.<br/>
    /// This guards the physical-shape side of the route claim so a stale writer cannot overwrite a target after topology code rewrites the physical page in place for router use.<br/>
    /// </summary>
    private static void RunInternalScalar8Scalar8StalePhysicalRouteClaimProbe()
    {
        using Scalar8Scalar8Index index = Indexes.SS88.Create(
            backingKind: DataKernelBackingKind.Memory,
            name: "stale-physical-route-claim-ss88",
            options: CreateDesignPerfOptions(),
            developerMetadata: CreateDesignPerfMetadata(9919),
            telemetryOptions: DataKernelTelemetryOptions.FromLevel(LibraDexDiagnosticsLevel.Detailed));

        const ulong seedKey = 0x5500_0000_0000_0001UL;
        const ulong stagedKey = 0x5500_0000_0000_0002UL;
        byte rootPrefix = (byte)(seedKey >> 56);
        _ = index.InsertEncoded(seedKey, 55001);

        long originalTarget = index.Session.FindRouterTarget(index.Handle.RootRouterOffset, rootPrefix);
        if (originalTarget == 0)
        {
            throw new InvalidDataException("SS8-8 stale physical route claim probe could not locate the seeded shelf.");
        }

        LibraDexWriteContext staleWriter = index.BeginWriteContext();
        bool contextActive = true;
        InvalidOperationException? staleClaimException = null;
        try
        {
            Scalar8Scalar8EncodedInsertResult staged = index.InsertEncodedForWriteContext(
                staleWriter,
                stagedKey,
                55002,
                allowDuplicateKeys: true);
            if (staged.Outcome != Scalar8Scalar8EncodedInsertOutcome.Inserted ||
                staged.StructuralKind != Scalar8Scalar8RoutedInsertKind.WalkedNoSplit ||
                staged.PrimaryOffset != originalTarget)
            {
                throw new InvalidDataException("SS8-8 stale physical route claim probe did not stage through the expected ordinary shelf.");
            }

            _ = index.Session.RewriteOffsetAsRootRouterForConcurrencyProof(originalTarget, allocationClassId: 1);
            contextActive = false;
            try
            {
                _ = index.Session.PublishScalar8Scalar8WriteContext(staleWriter);
            }
            catch (InvalidOperationException ex)
            {
                staleClaimException = ex;
                index.Session.AbortScalar8Scalar8WriteContext(staleWriter);
            }
        }
        finally
        {
            if (contextActive)
            {
                index.Session.AbortScalar8Scalar8WriteContext(staleWriter);
            }
        }

        if (staleClaimException is null ||
            !staleClaimException.Message.Contains("SS8-8 writer-context route claim no longer matches", StringComparison.Ordinal))
        {
            throw new InvalidDataException("SS8-8 stale physical route claim probe expected publish-time validation to reject stale shelf bytes.");
        }
    }

    /// <summary>
    /// Proves cold-route `VS8` topology ownership is rooted at the exact root-prefix domain instead of a whole-index queue.<br/>
    /// The probe holds one cold root-prefix owner after admission, then verifies a different root-prefix writer enters its own owner while a same-prefix writer waits for release.<br/>
    /// This keeps the variable-key primitive aligned with LibraDex's physical-library model: unrelated first shelves initialize independently, and only writers sharing the same root router slot synchronize.<br/>
    /// </summary>
    private static void RunInternalVarKeyScalar8ColdRootPrefixOwnerProbe()
    {
        using LibraDexFileSession session = LibraDexFileSession.InitializeMemory(
            CreateDesignPerfOptions(),
            CreateDesignPerfMetadata(9904),
            DataKernelTelemetryOptions.FromLevel(LibraDexDiagnosticsLevel.Detailed));
        VarLenOptimizerMaintenancePolicy policy = new(VarLenOptimizerMaintenanceMode.Disabled, 0, 0, 0, 0);
        (VarKeyScalar8IndexHandle handle, _, _) = session.CreateVarKeyScalar8RootRouterIndex(
            CreateHarnessSlot(0, "vs8coldowner", 0),
            maxKeyLength: 64,
            optimizerRouteFanout: 16,
            policy);

        byte[] firstKey = [0x10, 0x01, 0x01];
        byte[] differentPrefixKey = [0x20, 0x01, 0x01];
        byte[] samePrefixKey = [0x10, 0x01, 0x02];
        using ManualResetEventSlim firstOwnerEntered = new(false);
        using ManualResetEventSlim releaseFirstOwner = new(false);
        using ManualResetEventSlim differentOwnerEntered = new(false);
        using ManualResetEventSlim sameOwnerEntered = new(false);
        int firstPrefixOwnerEntries = 0;

        LibraDexFileSession.PrimitiveTopologyOwnerEnteredForValidation = (shape, kind, rootRouterOffset, ownerOffset, prefixByte) =>
        {
            if (shape != 6 || kind != 1 || rootRouterOffset != handle.RootRouterOffset || ownerOffset != 0)
            {
                return;
            }

            if (prefixByte == firstKey[0])
            {
                int entry = Interlocked.Increment(ref firstPrefixOwnerEntries);
                if (entry == 1)
                {
                    firstOwnerEntered.Set();
                    if (!releaseFirstOwner.Wait(TimeSpan.FromSeconds(10)))
                    {
                        throw new InvalidDataException("VS8 cold root-prefix owner probe timed out while holding the first prefix owner.");
                    }
                }
                else
                {
                    sameOwnerEntered.Set();
                }
            }
            else if (prefixByte == differentPrefixKey[0])
            {
                differentOwnerEntered.Set();
            }
        };

        try
        {
            Task<(bool Published, VarKeyScalar8RoutedInsertResult Result)> firstTask = Task.Run(() =>
            {
                bool published = session.TryInsertVarKeyScalar8DirectColdRootRoute(
                    handle.RootRouterOffset,
                    firstKey[0],
                    VarKeyScalar8Profile.Default8KiB,
                    firstKey,
                    1001,
                    allowDuplicateKeys: true,
                    out VarKeyScalar8RoutedInsertResult result);
                return (published, result);
            });
            if (!firstOwnerEntered.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new InvalidDataException("VS8 cold root-prefix owner probe did not enter the held first-prefix owner.");
            }

            Task<(bool Published, VarKeyScalar8RoutedInsertResult Result)> differentPrefixTask = Task.Run(() =>
            {
                bool published = session.TryInsertVarKeyScalar8DirectColdRootRoute(
                    handle.RootRouterOffset,
                    differentPrefixKey[0],
                    VarKeyScalar8Profile.Default8KiB,
                    differentPrefixKey,
                    2001,
                    allowDuplicateKeys: true,
                    out VarKeyScalar8RoutedInsertResult result);
                return (published, result);
            });
            if (!differentOwnerEntered.Wait(TimeSpan.FromSeconds(10)))
            {
                releaseFirstOwner.Set();
                throw new InvalidDataException("VS8 cold root-prefix owner probe expected a different root prefix to enter its own topology owner while the first prefix was held.");
            }

            Task<(bool Published, VarKeyScalar8RoutedInsertResult Result)> samePrefixTask = Task.Run(() =>
            {
                bool published = session.TryInsertVarKeyScalar8DirectColdRootRoute(
                    handle.RootRouterOffset,
                    samePrefixKey[0],
                    VarKeyScalar8Profile.Default8KiB,
                    samePrefixKey,
                    1002,
                    allowDuplicateKeys: true,
                    out VarKeyScalar8RoutedInsertResult result);
                return (published, result);
            });
            if (sameOwnerEntered.Wait(TimeSpan.FromMilliseconds(100)))
            {
                releaseFirstOwner.Set();
                throw new InvalidDataException("VS8 cold root-prefix owner probe allowed a same-prefix writer into the held owner domain.");
            }

            releaseFirstOwner.Set();
            Task.WaitAll(firstTask, differentPrefixTask, samePrefixTask);
            (bool firstPublished, VarKeyScalar8RoutedInsertResult firstResult) = firstTask.GetAwaiter().GetResult();
            (bool differentPublished, VarKeyScalar8RoutedInsertResult differentResult) = differentPrefixTask.GetAwaiter().GetResult();
            (bool samePublished, _) = samePrefixTask.GetAwaiter().GetResult();
            if (!firstPublished ||
                firstResult.InsertResult != VarKeyScalar8InsertResult.Inserted ||
                firstResult.Kind != VarKeyScalar8RoutedInsertKind.WalkedNoSplit)
            {
                throw new InvalidDataException($"VS8 cold root-prefix owner probe first insert attribution was wrong. published={firstPublished} result={firstResult.InsertResult} kind={firstResult.Kind}.");
            }

            if (!differentPublished ||
                differentResult.InsertResult != VarKeyScalar8InsertResult.Inserted ||
                differentResult.Kind != VarKeyScalar8RoutedInsertKind.WalkedNoSplit)
            {
                throw new InvalidDataException($"VS8 cold root-prefix owner probe different-prefix insert attribution was wrong. published={differentPublished} result={differentResult.InsertResult} kind={differentResult.Kind}.");
            }

            if (samePublished)
            {
                throw new InvalidDataException("VS8 cold root-prefix owner probe unexpectedly published a same-prefix cold route after the route had already been initialized.");
            }
        }
        finally
        {
            releaseFirstOwner.Set();
            LibraDexFileSession.PrimitiveTopologyOwnerEnteredForValidation = null;
        }

        ulong[] identities = new ulong[4];
        int count = session.ReadVarKeyScalar8IdentityRange(handle.RootRouterOffset, handle.MaxKeyLength, firstKey, differentPrefixKey, identities);
        if (count != 2 ||
            identities[0] != 1001 ||
            identities[1] != 2001)
        {
            throw new InvalidDataException("VS8 cold root-prefix owner probe inserts were not readable in sorted range order.");
        }
    }

    /// <summary>
    /// Proves the first internal `VS8` writer-context boundary: warmed ordinary shelves can stage independently, while same-shelf staging conflicts deterministically.<br/>
    /// The probe deliberately avoids growth, duplicate-runs, and transform splits so it can isolate the shelf-local writer-context contract.<br/>
    /// </summary>
    private static void RunInternalVarKeyScalar8WriterContextProbe()
    {
        using LibraDexFileSession session = LibraDexFileSession.InitializeMemory(
            CreateDesignPerfOptions(),
            CreateDesignPerfMetadata(9903),
            DataKernelTelemetryOptions.FromLevel(LibraDexDiagnosticsLevel.Detailed));
        VarLenOptimizerMaintenancePolicy policy = new(VarLenOptimizerMaintenanceMode.Disabled, 0, 0, 0, 0);
        (VarKeyScalar8IndexHandle handle, _, _) = session.CreateVarKeyScalar8RootRouterIndex(
            CreateHarnessSlot(0, "vs8writer", 0),
            maxKeyLength: 64,
            optimizerRouteFanout: 16,
            policy);
        byte[] leftSeed = [0x10, 0x01, 0x01];
        byte[] leftNext = [0x10, 0x01, 0x02];
        byte[] leftThird = [0x10, 0x01, 0x03];
        byte[] leftFourth = [0x10, 0x01, 0x04];
        byte[] rightSeed = [0x90, 0x01, 0x01];
        byte[] rightNext = [0x90, 0x01, 0x02];
        _ = session.CreateVarKeyScalar8ShelfAndLinkRootRoute(handle.RootRouterOffset, leftSeed[0], VarKeyScalar8Profile.Default8KiB);
        _ = session.CreateVarKeyScalar8ShelfAndLinkRootRoute(handle.RootRouterOffset, rightSeed[0], VarKeyScalar8Profile.Default8KiB);
        VarKeyScalar8RoutedInsertResult seedLeft = session.InsertWalkedRoutedVarKeyScalar8(
            handle.RootRouterOffset,
            handle.MaxKeyLength,
            leftSeed,
            1001,
            allowDuplicateKeys: true,
            maxRouterHops: LibraDexFileSession.DefaultVarKeyScalar8MaxRouterHops);
        VarKeyScalar8RoutedInsertResult seedRight = session.InsertWalkedRoutedVarKeyScalar8(
            handle.RootRouterOffset,
            handle.MaxKeyLength,
            rightSeed,
            2001,
            allowDuplicateKeys: true,
            maxRouterHops: LibraDexFileSession.DefaultVarKeyScalar8MaxRouterHops);
        if (seedLeft.InsertResult != VarKeyScalar8InsertResult.Inserted ||
            seedRight.InsertResult != VarKeyScalar8InsertResult.Inserted)
        {
            throw new InvalidDataException("VS8 writer-context probe could not seed the warmed ordinary shelves.");
        }

        long leftShelf = session.FindRouterTarget(handle.RootRouterOffset, leftSeed[0]);
        long rightShelf = session.FindRouterTarget(handle.RootRouterOffset, rightSeed[0]);
        if (leftShelf == 0 || rightShelf == 0 || leftShelf == rightShelf)
        {
            throw new InvalidDataException("VS8 writer-context probe did not create two distinct root-prefix shelves.");
        }

        LibraDexWriteContext leftWriter = session.BeginVarKeyScalar8WriteContext();
        LibraDexWriteContext rightWriter = session.BeginVarKeyScalar8WriteContext();
        VarKeyScalar8RoutedInsertResult leftInsert = session.InsertWalkedRoutedVarKeyScalar8NoSplitForWriteContext(
            leftWriter,
            handle.RootRouterOffset,
            handle.MaxKeyLength,
            leftNext,
            1002,
            allowDuplicateKeys: true,
            maxRouterHops: LibraDexFileSession.DefaultVarKeyScalar8MaxRouterHops);
        VarKeyScalar8RoutedInsertResult rightInsert = session.InsertWalkedRoutedVarKeyScalar8NoSplitForWriteContext(
            rightWriter,
            handle.RootRouterOffset,
            handle.MaxKeyLength,
            rightNext,
            2002,
            allowDuplicateKeys: true,
            maxRouterHops: LibraDexFileSession.DefaultVarKeyScalar8MaxRouterHops);
        if (leftInsert.InsertResult != VarKeyScalar8InsertResult.Inserted ||
            rightInsert.InsertResult != VarKeyScalar8InsertResult.Inserted ||
            leftInsert.Kind != VarKeyScalar8RoutedInsertKind.WalkedNoSplit ||
            rightInsert.Kind != VarKeyScalar8RoutedInsertKind.WalkedNoSplit ||
            leftInsert.PrimaryOffset != leftShelf ||
            rightInsert.PrimaryOffset != rightShelf)
        {
            throw new InvalidDataException("VS8 writer-context probe did not stage the expected ordinary shelf-local inserts.");
        }

        _ = session.PublishVarKeyScalar8WriteContext(rightWriter);
        _ = session.PublishVarKeyScalar8WriteContext(leftWriter);

        ulong[] identities = new ulong[4];
        int leftCount = session.ReadVarKeyScalar8IdentityRange(handle.RootRouterOffset, handle.MaxKeyLength, leftSeed, leftNext, identities);
        if (leftCount != 2 || identities[0] != 1001 || identities[1] != 1002)
        {
            throw new InvalidDataException("VS8 writer-context probe did not publish left-shelf changes.");
        }

        Array.Clear(identities);
        int rightCount = session.ReadVarKeyScalar8IdentityRange(handle.RootRouterOffset, handle.MaxKeyLength, rightSeed, rightNext, identities);
        if (rightCount != 2 || identities[0] != 2001 || identities[1] != 2002)
        {
            throw new InvalidDataException("VS8 writer-context probe did not publish right-shelf changes.");
        }

        LibraDexWriteContext deleteWriter = session.BeginVarKeyScalar8WriteContext();
        bool deleted = session.DeleteVarKeyScalar8ExactTupleForWriteContext(
            deleteWriter,
            handle.RootRouterOffset,
            handle.MaxKeyLength,
            leftNext,
            1002,
            maxRouterHops: LibraDexFileSession.DefaultVarKeyScalar8MaxRouterHops);
        if (!deleted)
        {
            throw new InvalidDataException("VS8 writer-context probe could not stage the expected exact delete.");
        }

        _ = session.PublishVarKeyScalar8WriteContext(deleteWriter);
        Array.Clear(identities);
        leftCount = session.ReadVarKeyScalar8IdentityRange(handle.RootRouterOffset, handle.MaxKeyLength, leftSeed, leftNext, identities);
        if (leftCount != 1 || identities[0] != 1001)
        {
            throw new InvalidDataException("VS8 writer-context exact delete did not remove only the targeted tuple.");
        }

        LibraDexWriteContext owner = session.BeginVarKeyScalar8WriteContext();
        LibraDexWriteContext contender = session.BeginVarKeyScalar8WriteContext();
        _ = session.InsertWalkedRoutedVarKeyScalar8NoSplitForWriteContext(
            owner,
            handle.RootRouterOffset,
            handle.MaxKeyLength,
            leftThird,
            1003,
            allowDuplicateKeys: true,
            maxRouterHops: LibraDexFileSession.DefaultVarKeyScalar8MaxRouterHops);
        LibraDexWriteContextVarKeyScalar8ShelfOwnershipException? conflictException = null;
        try
        {
            _ = session.InsertWalkedRoutedVarKeyScalar8NoSplitForWriteContext(
                contender,
                handle.RootRouterOffset,
                handle.MaxKeyLength,
                leftFourth,
                1004,
                allowDuplicateKeys: true,
                maxRouterHops: LibraDexFileSession.DefaultVarKeyScalar8MaxRouterHops);
        }
        catch (LibraDexWriteContextVarKeyScalar8ShelfOwnershipException ex)
        {
            conflictException = ex;
        }

        session.AbortVarKeyScalar8WriteContext(contender);
        session.AbortVarKeyScalar8WriteContext(owner);
        if (conflictException is null ||
            conflictException.ShelfOffset != leftShelf ||
            !conflictException.Message.Contains("already owned by another LibraDex writer context", StringComparison.Ordinal))
        {
            throw new InvalidDataException("VS8 writer-context probe expected same-shelf ownership to be rejected deterministically.");
        }

        byte[] staleSeed = [0x44, 0x01, 0x01];
        byte[] staleStaged = [0x44, 0x7F, 0x7F];
        _ = session.CreateVarKeyScalar8ShelfAndLinkRootRoute(handle.RootRouterOffset, staleSeed[0], VarKeyScalar8Profile.DefaultInitial);
        _ = session.InsertWalkedRoutedVarKeyScalar8(
            handle.RootRouterOffset,
            handle.MaxKeyLength,
            staleSeed,
            4401,
            allowDuplicateKeys: true,
            maxRouterHops: LibraDexFileSession.DefaultVarKeyScalar8MaxRouterHops);
        long staleOriginalTarget = session.FindRouterTarget(handle.RootRouterOffset, staleSeed[0]);
        LibraDexWriteContext staleWriter = session.BeginVarKeyScalar8WriteContext();
        _ = session.InsertWalkedRoutedVarKeyScalar8NoSplitForWriteContext(
            staleWriter,
            handle.RootRouterOffset,
            handle.MaxKeyLength,
            staleStaged,
            4499,
            allowDuplicateKeys: true,
            maxRouterHops: LibraDexFileSession.DefaultVarKeyScalar8MaxRouterHops);

        long staleCurrentTarget = staleOriginalTarget;
        int staleFill = 0;
        while (staleCurrentTarget == staleOriginalTarget && staleFill < 4096)
        {
            byte[] fillKey =
            [
                staleSeed[0],
                (byte)(staleFill >> 8),
                (byte)(staleFill & 0xFF)
            ];
            if (fillKey.AsSpan().SequenceEqual(staleStaged))
            {
                staleFill++;
                continue;
            }

            _ = session.InsertWalkedRoutedVarKeyScalar8(
                handle.RootRouterOffset,
                handle.MaxKeyLength,
                fillKey,
                checked((ulong)(4500 + staleFill)),
                allowDuplicateKeys: true,
                maxRouterHops: LibraDexFileSession.DefaultVarKeyScalar8MaxRouterHops);
            staleCurrentTarget = session.FindRouterTarget(handle.RootRouterOffset, staleSeed[0]);
            staleFill++;
        }

        if (staleCurrentTarget == staleOriginalTarget)
        {
            session.AbortVarKeyScalar8WriteContext(staleWriter);
            throw new InvalidDataException("VS8 writer-context stale-route probe could not force a route relink.");
        }

        InvalidOperationException? staleRouteException = null;
        try
        {
            _ = session.PublishVarKeyScalar8WriteContext(staleWriter);
        }
        catch (InvalidOperationException ex)
        {
            staleRouteException = ex;
            session.AbortVarKeyScalar8WriteContext(staleWriter);
        }

        if (staleRouteException is null ||
            !staleRouteException.Message.Contains("route claim no longer matches", StringComparison.Ordinal))
        {
            throw new InvalidDataException("VS8 writer-context stale-route probe expected publish-time route-claim validation to reject stale shelf bytes.");
        }

        _ = session.InsertWalkedRoutedVarKeyScalar8(
            handle.RootRouterOffset,
            handle.MaxKeyLength,
            leftThird,
            1003,
            allowDuplicateKeys: true,
            maxRouterHops: LibraDexFileSession.DefaultVarKeyScalar8MaxRouterHops);
        _ = session.InsertWalkedRoutedVarKeyScalar8(
            handle.RootRouterOffset,
            handle.MaxKeyLength,
            leftFourth,
            1004,
            allowDuplicateKeys: true,
            maxRouterHops: LibraDexFileSession.DefaultVarKeyScalar8MaxRouterHops);

        LibraDexWriteContext rangeDeleteWriter = session.BeginVarKeyScalar8WriteContext();
        long rangeDeleted = session.DeleteVarKeyScalar8KeyRangeForWriteContext(
            rangeDeleteWriter,
            handle.RootRouterOffset,
            handle.MaxKeyLength,
            leftThird,
            leftFourth,
            maxRouterHops: LibraDexFileSession.DefaultVarKeyScalar8MaxRouterHops);
        if (rangeDeleted != 2)
        {
            throw new InvalidDataException($"VS8 writer-context range delete expected two staged deletes but saw {rangeDeleted}.");
        }

        _ = session.PublishVarKeyScalar8WriteContext(rangeDeleteWriter);
        Array.Clear(identities);
        leftCount = session.ReadVarKeyScalar8IdentityRange(handle.RootRouterOffset, handle.MaxKeyLength, leftSeed, leftFourth, identities);
        if (leftCount != 1 || identities[0] != 1001)
        {
            throw new InvalidDataException("VS8 writer-context range delete did not remove the expected tuple interval.");
        }

        byte[] terminalKey = [0x77, 0x01, 0x01];
        VarKeyScalar8Profile terminalProfile = VarKeyScalar8Profile.Default8KiB;
        _ = session.CreateVarKeyScalar8ShelfAndLinkRootRoute(handle.RootRouterOffset, terminalKey[0], terminalProfile);
        int terminalBaseCount = 0;
        while (true)
        {
            VarKeyScalar8RoutedInsertResult fill = session.InsertWalkedRoutedVarKeyScalar8(
                handle.RootRouterOffset,
                handle.MaxKeyLength,
                terminalKey,
                (ulong)(7000 + terminalBaseCount),
                allowDuplicateKeys: true,
                maxRouterHops: LibraDexFileSession.DefaultVarKeyScalar8MaxRouterHops);
            if (fill.InsertResult != VarKeyScalar8InsertResult.Inserted)
            {
                throw new InvalidDataException($"VS8 writer-context terminal fixture failed at item {terminalBaseCount}; result={fill.InsertResult}.");
            }

            terminalBaseCount++;
            VarKeyScalar8RoutePathTarget terminalPath = session.WalkVarKeyScalar8RoutePathTarget(
                handle.RootRouterOffset,
                terminalKey,
                LibraDexFileSession.DefaultVarKeyScalar8MaxRouterHops);
            if (terminalPath.Target.Kind == VarKeyScalar8RouteTargetKind.TerminalIdentityRoot)
            {
                break;
            }

            if (terminalBaseCount > 4096)
            {
                throw new InvalidDataException("VS8 writer-context terminal fixture did not reach terminal identity topology.");
            }
        }

        LibraDexWriteContext terminalWriter = session.BeginVarKeyScalar8WriteContext();
        VarKeyScalar8RoutedInsertResult terminalLocal = session.InsertWalkedRoutedVarKeyScalar8NoSplitForWriteContext(
            terminalWriter,
            handle.RootRouterOffset,
            handle.MaxKeyLength,
            terminalKey,
            (ulong)(7000 + terminalBaseCount),
            allowDuplicateKeys: true,
            maxRouterHops: LibraDexFileSession.DefaultVarKeyScalar8MaxRouterHops);
        if (terminalLocal.InsertResult != VarKeyScalar8InsertResult.Inserted ||
            terminalLocal.Kind != VarKeyScalar8RoutedInsertKind.WalkedNoSplit)
        {
            throw new InvalidDataException("VS8 writer-context terminal identity local insert did not stage through writer context.");
        }

        _ = session.PublishVarKeyScalar8WriteContext(terminalWriter);
        Array.Clear(identities);
        identities = new ulong[terminalBaseCount + 1];
        int terminalCount = session.ReadVarKeyScalar8IdentityRange(handle.RootRouterOffset, handle.MaxKeyLength, terminalKey, terminalKey, identities);
        if (terminalCount != terminalBaseCount + 1 ||
            identities[terminalBaseCount] != (ulong)(7000 + terminalBaseCount))
        {
            throw new InvalidDataException("VS8 writer-context terminal identity local insert readback failed.");
        }
    }

    /// <summary>
    /// Proves a one-shot `VS8` insert deepens a valid terminal-identity route when a different key with the same routed prefix arrives.<br/>
    /// The writer-context fast path must classify that topology as a serialized fallback rather than corruption, and the fallback must preserve the terminal key's identities while adding the sibling key.<br/>
    /// </summary>
    private static void RunInternalVarKeyScalar8TerminalSiblingFallbackProbe()
    {
        using LibraDexFileSession session = LibraDexFileSession.InitializeMemory(
            CreateDesignPerfOptions(),
            CreateDesignPerfMetadata(9910),
            DataKernelTelemetryOptions.FromLevel(LibraDexDiagnosticsLevel.Detailed));
        VarLenOptimizerMaintenancePolicy policy = new(VarLenOptimizerMaintenanceMode.Disabled, 0, 0, 0, 0);
        (VarKeyScalar8IndexHandle handle, _, _) = session.CreateVarKeyScalar8RootRouterIndex(
            CreateHarnessSlot(0, "vs8termsibling", 0),
            maxKeyLength: 64,
            optimizerRouteFanout: 16,
            policy);
        using VarKeyScalar8Index index = new(session, handle, 0, "vs8termsibling", ownsSession: false);
        byte[] terminalKey = [0x77, 0x01, 0x01];
        int terminalCount = 0;
        while (true)
        {
            VarKeyScalar8InsertOutcome fill = index.InsertEncoded(
                terminalKey,
                checked((ulong)(10000 + terminalCount)),
                allowDuplicateKeys: true);
            if (!fill.Inserted)
            {
                throw new InvalidDataException($"VS8 terminal sibling fallback fixture failed at item {terminalCount}.");
            }

            terminalCount++;
            VarKeyScalar8RoutePathTarget path = session.WalkVarKeyScalar8RoutePathTarget(
                handle.RootRouterOffset,
                terminalKey,
                LibraDexFileSession.DefaultVarKeyScalar8MaxRouterHops);
            if (path.Target.Kind == VarKeyScalar8RouteTargetKind.TerminalIdentityRoot)
            {
                break;
            }

            if (terminalCount > 4096)
            {
                throw new InvalidDataException("VS8 terminal sibling fallback fixture did not reach terminal identity topology.");
            }
        }

        byte[] siblingKey = [0x77, 0x01, 0x02];
        const ulong siblingIdentity = 9000000;
        VarKeyScalar8InsertOutcome sibling = index.InsertEncoded(siblingKey, siblingIdentity, allowDuplicateKeys: true);
        if (!sibling.Inserted)
        {
            throw new InvalidDataException("VS8 terminal sibling fallback did not insert the sibling key.");
        }

        ulong[] terminalIdentities = new ulong[terminalCount];
        int terminalRead = session.ReadVarKeyScalar8IdentityRange(
            handle.RootRouterOffset,
            handle.MaxKeyLength,
            terminalKey,
            terminalKey,
            terminalIdentities);
        if (terminalRead != terminalCount || terminalIdentities[terminalCount - 1] != checked((ulong)(10000 + terminalCount - 1)))
        {
            throw new InvalidDataException("VS8 terminal sibling fallback did not preserve the terminal key identities.");
        }

        ulong[] siblingIdentities = new ulong[2];
        int siblingRead = session.ReadVarKeyScalar8IdentityRange(
            handle.RootRouterOffset,
            handle.MaxKeyLength,
            siblingKey,
            siblingKey,
            siblingIdentities);
        if (siblingRead != 1 || siblingIdentities[0] != siblingIdentity)
        {
            throw new InvalidDataException("VS8 terminal sibling fallback did not publish the sibling identity exactly once.");
        }

        using VarKeyScalar8RangeReader reader = index.OpenEncodedRangeReader([0x00], [0xFF, 0xFF, 0xFF]);
        int ordinal = 0;
        while (reader.MoveNext())
        {
            if (ordinal < terminalCount)
            {
                ulong expectedIdentity = checked((ulong)(10000 + ordinal));
                if (!reader.CurrentKey.SequenceEqual(terminalKey) || reader.CurrentEncodedIdentity != expectedIdentity)
                {
                    throw new InvalidDataException($"VS8 terminal sibling fallback changed global tuple order at terminal ordinal {ordinal}.");
                }
            }
            else if (ordinal == terminalCount)
            {
                if (!reader.CurrentKey.SequenceEqual(siblingKey) || reader.CurrentEncodedIdentity != siblingIdentity)
                {
                    throw new InvalidDataException("VS8 terminal sibling fallback did not emit the sibling tuple after the terminal-key run.");
                }
            }
            else
            {
                throw new InvalidDataException("VS8 terminal sibling fallback emitted an unexpected extra tuple.");
            }

            ordinal++;
        }

        if (ordinal != terminalCount + 1)
        {
            throw new InvalidDataException($"VS8 terminal sibling fallback streamed {ordinal} tuples; expected {terminalCount + 1}.");
        }
    }

    /// <summary>
    /// Proves a `VS8` writer-context publication rejects staged shelf bytes when the selecting route still points at the same offset but that offset is no longer a supported `VS8` target.<br/>
    /// This covers the physical-shape stale case separately from the existing parent-route relink probe.<br/>
    /// </summary>
    private static void RunInternalVarKeyScalar8StalePhysicalRouteClaimProbe()
    {
        using LibraDexFileSession session = LibraDexFileSession.InitializeMemory(
            CreateDesignPerfOptions(),
            CreateDesignPerfMetadata(9908),
            DataKernelTelemetryOptions.FromLevel(LibraDexDiagnosticsLevel.Detailed));
        VarLenOptimizerMaintenancePolicy policy = new(VarLenOptimizerMaintenanceMode.Disabled, 0, 0, 0, 0);
        (VarKeyScalar8IndexHandle handle, _, _) = session.CreateVarKeyScalar8RootRouterIndex(
            CreateHarnessSlot(0, "vs8stalephys", 0),
            maxKeyLength: 64,
            optimizerRouteFanout: 16,
            policy);
        byte[] seedKey = [0x52, 0x10, 0x01];
        byte[] stagedKey = [0x52, 0x10, 0x02];
        _ = session.CreateVarKeyScalar8ShelfAndLinkRootRoute(handle.RootRouterOffset, seedKey[0], VarKeyScalar8Profile.Default8KiB);
        VarKeyScalar8RoutedInsertResult seed = session.InsertWalkedRoutedVarKeyScalar8(
            handle.RootRouterOffset,
            handle.MaxKeyLength,
            seedKey,
            52001,
            allowDuplicateKeys: true,
            maxRouterHops: LibraDexFileSession.DefaultVarKeyScalar8MaxRouterHops);
        if (seed.InsertResult != VarKeyScalar8InsertResult.Inserted)
        {
            throw new InvalidDataException("VS8 stale physical route claim probe could not seed the warmed ordinary shelf.");
        }

        long originalTarget = session.FindRouterTarget(handle.RootRouterOffset, seedKey[0]);
        LibraDexWriteContext staleWriter = session.BeginVarKeyScalar8WriteContext();
        bool contextActive = true;
        InvalidOperationException? staleClaimException = null;
        try
        {
            VarKeyScalar8RoutedInsertResult staged = session.InsertWalkedRoutedVarKeyScalar8NoSplitForWriteContext(
                staleWriter,
                handle.RootRouterOffset,
                handle.MaxKeyLength,
                stagedKey,
                52002,
                allowDuplicateKeys: true,
                maxRouterHops: LibraDexFileSession.DefaultVarKeyScalar8MaxRouterHops);
            if (staged.InsertResult != VarKeyScalar8InsertResult.Inserted ||
                staged.Kind != VarKeyScalar8RoutedInsertKind.WalkedNoSplit ||
                staged.PrimaryOffset != originalTarget)
            {
                throw new InvalidDataException("VS8 stale physical route claim probe did not stage through the expected ordinary shelf.");
            }

            _ = session.RewriteOffsetAsRootRouterForConcurrencyProof(originalTarget, allocationClassId: 1);
            contextActive = false;
            try
            {
                _ = session.PublishVarKeyScalar8WriteContext(staleWriter);
            }
            catch (InvalidOperationException ex)
            {
                staleClaimException = ex;
                session.AbortVarKeyScalar8WriteContext(staleWriter);
            }
        }
        finally
        {
            if (contextActive)
            {
                session.AbortVarKeyScalar8WriteContext(staleWriter);
            }
        }

        if (staleClaimException is null ||
            !staleClaimException.Message.Contains("VS8 writer-context route claim no longer matches", StringComparison.Ordinal))
        {
            throw new InvalidDataException("VS8 stale physical route claim probe expected publish-time validation to reject stale shelf bytes.");
        }
    }

    /// <summary>
    /// Proves the first internal `VS16` writer-context boundary: warmed ordinary shelves can stage independently, while same-shelf staging conflicts deterministically.<br/>
    /// This mirrors the `VS8` ordinary-shelf proof and keeps growth, terminal routes, and transform splits on the existing topology publication path.<br/>
    /// </summary>
    private static void RunInternalVarKeyScalar16WriterContextProbe()
    {
        using LibraDexFileSession session = LibraDexFileSession.InitializeMemory(
            CreateDesignPerfOptions(),
            CreateDesignPerfMetadata(9904),
            DataKernelTelemetryOptions.FromLevel(LibraDexDiagnosticsLevel.Detailed));
        VarLenOptimizerMaintenancePolicy policy = new(VarLenOptimizerMaintenanceMode.Disabled, 0, 0, 0, 0);
        (VarKeyScalar16IndexHandle handle, _, _) = session.CreateVarKeyScalar16RootRouterIndex(
            CreateHarnessSlot(0, "vs16writer", 0),
            maxKeyLength: 64,
            optimizerRouteFanout: 16,
            policy);
        byte[] leftSeed = [0x20, 0x01, 0x01];
        byte[] leftNext = [0x20, 0x01, 0x02];
        byte[] leftThird = [0x20, 0x01, 0x03];
        byte[] leftFourth = [0x20, 0x01, 0x04];
        byte[] rightSeed = [0xA0, 0x01, 0x01];
        byte[] rightNext = [0xA0, 0x01, 0x02];
        _ = session.CreateVarKeyScalar16ShelfAndLinkRootRoute(handle.RootRouterOffset, leftSeed[0], VarKeyScalar16Profile.Default8KiB);
        _ = session.CreateVarKeyScalar16ShelfAndLinkRootRoute(handle.RootRouterOffset, rightSeed[0], VarKeyScalar16Profile.Default8KiB);
        VarKeyScalar16RoutedInsertResult seedLeft = session.InsertWalkedRoutedVarKeyScalar16(
            handle.RootRouterOffset,
            handle.MaxKeyLength,
            leftSeed,
            encodedIdentityHigh: 0,
            encodedIdentityLow: 1001,
            allowDuplicateKeys: true,
            maxRouterHops: LibraDexFileSession.DefaultVarKeyScalar16MaxRouterHops);
        VarKeyScalar16RoutedInsertResult seedRight = session.InsertWalkedRoutedVarKeyScalar16(
            handle.RootRouterOffset,
            handle.MaxKeyLength,
            rightSeed,
            encodedIdentityHigh: 0,
            encodedIdentityLow: 2001,
            allowDuplicateKeys: true,
            maxRouterHops: LibraDexFileSession.DefaultVarKeyScalar16MaxRouterHops);
        if (seedLeft.InsertResult != VarKeyScalar16InsertResult.Inserted ||
            seedRight.InsertResult != VarKeyScalar16InsertResult.Inserted)
        {
            throw new InvalidDataException("VS16 writer-context probe could not seed the warmed ordinary shelves.");
        }

        long leftShelf = session.FindRouterTarget(handle.RootRouterOffset, leftSeed[0]);
        long rightShelf = session.FindRouterTarget(handle.RootRouterOffset, rightSeed[0]);
        if (leftShelf == 0 || rightShelf == 0 || leftShelf == rightShelf)
        {
            throw new InvalidDataException("VS16 writer-context probe did not create two distinct root-prefix shelves.");
        }

        LibraDexWriteContext leftWriter = session.BeginVarKeyScalar16WriteContext();
        LibraDexWriteContext rightWriter = session.BeginVarKeyScalar16WriteContext();
        VarKeyScalar16RoutedInsertResult leftInsert = session.InsertWalkedRoutedVarKeyScalar16NoSplitForWriteContext(
            leftWriter,
            handle.RootRouterOffset,
            handle.MaxKeyLength,
            leftNext,
            encodedIdentityHigh: 0,
            encodedIdentityLow: 1002,
            allowDuplicateKeys: true,
            maxRouterHops: LibraDexFileSession.DefaultVarKeyScalar16MaxRouterHops);
        VarKeyScalar16RoutedInsertResult rightInsert = session.InsertWalkedRoutedVarKeyScalar16NoSplitForWriteContext(
            rightWriter,
            handle.RootRouterOffset,
            handle.MaxKeyLength,
            rightNext,
            encodedIdentityHigh: 0,
            encodedIdentityLow: 2002,
            allowDuplicateKeys: true,
            maxRouterHops: LibraDexFileSession.DefaultVarKeyScalar16MaxRouterHops);
        if (leftInsert.InsertResult != VarKeyScalar16InsertResult.Inserted ||
            rightInsert.InsertResult != VarKeyScalar16InsertResult.Inserted ||
            leftInsert.Kind != VarKeyScalar16RoutedInsertKind.WalkedNoSplit ||
            rightInsert.Kind != VarKeyScalar16RoutedInsertKind.WalkedNoSplit ||
            leftInsert.PrimaryOffset != leftShelf ||
            rightInsert.PrimaryOffset != rightShelf)
        {
            throw new InvalidDataException("VS16 writer-context probe did not stage the expected ordinary shelf-local inserts.");
        }

        _ = session.PublishVarKeyScalar16WriteContext(rightWriter);
        _ = session.PublishVarKeyScalar16WriteContext(leftWriter);

        ulong[] identityHighs = new ulong[4];
        ulong[] identityLows = new ulong[4];
        int leftCount = session.ReadVarKeyScalar16IdentityRange(handle.RootRouterOffset, handle.MaxKeyLength, leftSeed, leftNext, identityHighs, identityLows);
        if (leftCount != 2 ||
            identityHighs[0] != 0 ||
            identityLows[0] != 1001 ||
            identityHighs[1] != 0 ||
            identityLows[1] != 1002)
        {
            throw new InvalidDataException("VS16 writer-context probe did not publish left-shelf changes.");
        }

        Array.Clear(identityHighs);
        Array.Clear(identityLows);
        int rightCount = session.ReadVarKeyScalar16IdentityRange(handle.RootRouterOffset, handle.MaxKeyLength, rightSeed, rightNext, identityHighs, identityLows);
        if (rightCount != 2 ||
            identityHighs[0] != 0 ||
            identityLows[0] != 2001 ||
            identityHighs[1] != 0 ||
            identityLows[1] != 2002)
        {
            throw new InvalidDataException("VS16 writer-context probe did not publish right-shelf changes.");
        }

        LibraDexWriteContext deleteWriter = session.BeginVarKeyScalar16WriteContext();
        bool deleted = session.DeleteVarKeyScalar16ExactTupleForWriteContext(
            deleteWriter,
            handle.RootRouterOffset,
            handle.MaxKeyLength,
            leftNext,
            encodedIdentityHigh: 0,
            encodedIdentityLow: 1002,
            maxRouterHops: LibraDexFileSession.DefaultVarKeyScalar16MaxRouterHops);
        if (!deleted)
        {
            throw new InvalidDataException("VS16 writer-context probe could not stage the expected exact delete.");
        }

        _ = session.PublishVarKeyScalar16WriteContext(deleteWriter);
        Array.Clear(identityHighs);
        Array.Clear(identityLows);
        leftCount = session.ReadVarKeyScalar16IdentityRange(handle.RootRouterOffset, handle.MaxKeyLength, leftSeed, leftNext, identityHighs, identityLows);
        if (leftCount != 1 ||
            identityHighs[0] != 0 ||
            identityLows[0] != 1001)
        {
            throw new InvalidDataException("VS16 writer-context exact delete did not remove only the targeted tuple.");
        }

        LibraDexWriteContext owner = session.BeginVarKeyScalar16WriteContext();
        LibraDexWriteContext contender = session.BeginVarKeyScalar16WriteContext();
        _ = session.InsertWalkedRoutedVarKeyScalar16NoSplitForWriteContext(
            owner,
            handle.RootRouterOffset,
            handle.MaxKeyLength,
            leftThird,
            encodedIdentityHigh: 0,
            encodedIdentityLow: 1003,
            allowDuplicateKeys: true,
            maxRouterHops: LibraDexFileSession.DefaultVarKeyScalar16MaxRouterHops);
        LibraDexWriteContextVarKeyScalar16ShelfOwnershipException? conflictException = null;
        try
        {
            _ = session.InsertWalkedRoutedVarKeyScalar16NoSplitForWriteContext(
                contender,
                handle.RootRouterOffset,
                handle.MaxKeyLength,
                leftFourth,
                encodedIdentityHigh: 0,
                encodedIdentityLow: 1004,
                allowDuplicateKeys: true,
                maxRouterHops: LibraDexFileSession.DefaultVarKeyScalar16MaxRouterHops);
        }
        catch (LibraDexWriteContextVarKeyScalar16ShelfOwnershipException ex)
        {
            conflictException = ex;
        }

        session.AbortVarKeyScalar16WriteContext(contender);
        session.AbortVarKeyScalar16WriteContext(owner);
        if (conflictException is null ||
            conflictException.ShelfOffset != leftShelf ||
            !conflictException.Message.Contains("already owned by another LibraDex writer context", StringComparison.Ordinal))
        {
            throw new InvalidDataException("VS16 writer-context probe expected same-shelf ownership to be rejected deterministically.");
        }

        _ = session.InsertWalkedRoutedVarKeyScalar16(
            handle.RootRouterOffset,
            handle.MaxKeyLength,
            leftThird,
            encodedIdentityHigh: 0,
            encodedIdentityLow: 1003,
            allowDuplicateKeys: true,
            maxRouterHops: LibraDexFileSession.DefaultVarKeyScalar16MaxRouterHops);
        _ = session.InsertWalkedRoutedVarKeyScalar16(
            handle.RootRouterOffset,
            handle.MaxKeyLength,
            leftFourth,
            encodedIdentityHigh: 0,
            encodedIdentityLow: 1004,
            allowDuplicateKeys: true,
            maxRouterHops: LibraDexFileSession.DefaultVarKeyScalar16MaxRouterHops);

        LibraDexWriteContext rangeDeleteWriter = session.BeginVarKeyScalar16WriteContext();
        long rangeDeleted = session.DeleteVarKeyScalar16KeyRangeForWriteContext(
            rangeDeleteWriter,
            handle.RootRouterOffset,
            handle.MaxKeyLength,
            leftThird,
            leftFourth,
            maxRouterHops: LibraDexFileSession.DefaultVarKeyScalar16MaxRouterHops);
        if (rangeDeleted != 2)
        {
            throw new InvalidDataException($"VS16 writer-context range delete expected two staged deletes but saw {rangeDeleted}.");
        }

        _ = session.PublishVarKeyScalar16WriteContext(rangeDeleteWriter);
        Array.Clear(identityHighs);
        Array.Clear(identityLows);
        leftCount = session.ReadVarKeyScalar16IdentityRange(handle.RootRouterOffset, handle.MaxKeyLength, leftSeed, leftFourth, identityHighs, identityLows);
        if (leftCount != 1 ||
            identityHighs[0] != 0 ||
            identityLows[0] != 1001)
        {
            throw new InvalidDataException("VS16 writer-context range delete did not remove the expected tuple interval.");
        }
    }

    /// <summary>
    /// Proves a `VS16` writer-context publication rejects staged shelf bytes when the selecting route still points at the same offset but that offset is no longer a shelf.<br/>
    /// This guards the stale physical-shape case that parent-route target-offset validation alone cannot see.<br/>
    /// </summary>
    private static void RunInternalVarKeyScalar16StaleRouteClaimProbe()
    {
        using LibraDexFileSession session = LibraDexFileSession.InitializeMemory(
            CreateDesignPerfOptions(),
            CreateDesignPerfMetadata(9907),
            DataKernelTelemetryOptions.FromLevel(LibraDexDiagnosticsLevel.Detailed));
        VarLenOptimizerMaintenancePolicy policy = new(VarLenOptimizerMaintenanceMode.Disabled, 0, 0, 0, 0);
        (VarKeyScalar16IndexHandle handle, _, _) = session.CreateVarKeyScalar16RootRouterIndex(
            CreateHarnessSlot(0, "vs16stale", 0),
            maxKeyLength: 64,
            optimizerRouteFanout: 16,
            policy);
        byte[] seedKey = [0x61, 0x10, 0x01];
        byte[] stagedKey = [0x61, 0x10, 0x02];
        _ = session.CreateVarKeyScalar16ShelfAndLinkRootRoute(handle.RootRouterOffset, seedKey[0], VarKeyScalar16Profile.Default8KiB);
        VarKeyScalar16RoutedInsertResult seed = session.InsertWalkedRoutedVarKeyScalar16(
            handle.RootRouterOffset,
            handle.MaxKeyLength,
            seedKey,
            encodedIdentityHigh: 0,
            encodedIdentityLow: 61001,
            allowDuplicateKeys: true,
            maxRouterHops: LibraDexFileSession.DefaultVarKeyScalar16MaxRouterHops);
        if (seed.InsertResult != VarKeyScalar16InsertResult.Inserted)
        {
            throw new InvalidDataException("VS16 stale route claim probe could not seed the warmed ordinary shelf.");
        }

        long originalTarget = session.FindRouterTarget(handle.RootRouterOffset, seedKey[0]);
        if (originalTarget <= 0)
        {
            throw new InvalidDataException("VS16 stale route claim probe could not resolve the seeded root route.");
        }

        LibraDexWriteContext staleWriter = session.BeginVarKeyScalar16WriteContext();
        bool contextActive = true;
        InvalidOperationException? staleClaimException = null;
        try
        {
            VarKeyScalar16RoutedInsertResult staged = session.InsertWalkedRoutedVarKeyScalar16NoSplitForWriteContext(
                staleWriter,
                handle.RootRouterOffset,
                handle.MaxKeyLength,
                stagedKey,
                encodedIdentityHigh: 0,
                encodedIdentityLow: 61002,
                allowDuplicateKeys: true,
                maxRouterHops: LibraDexFileSession.DefaultVarKeyScalar16MaxRouterHops);
            if (staged.InsertResult != VarKeyScalar16InsertResult.Inserted ||
                staged.Kind != VarKeyScalar16RoutedInsertKind.WalkedNoSplit ||
                staged.PrimaryOffset != originalTarget)
            {
                throw new InvalidDataException("VS16 stale route claim probe did not stage through the expected ordinary shelf.");
            }

            _ = session.RewriteOffsetAsRootRouterForConcurrencyProof(originalTarget, allocationClassId: 1);
            contextActive = false;
            try
            {
                _ = session.PublishVarKeyScalar16WriteContext(staleWriter);
            }
            catch (InvalidOperationException ex)
            {
                staleClaimException = ex;
            }
        }
        finally
        {
            if (contextActive)
            {
                session.AbortVarKeyScalar16WriteContext(staleWriter);
            }
        }

        if (staleClaimException is null ||
            !staleClaimException.Message.Contains("VS16 writer-context route claim no longer matches", StringComparison.Ordinal))
        {
            throw new InvalidDataException("VS16 stale route claim probe expected publish-time validation to reject stale shelf bytes.");
        }
    }

    /// <summary>
    /// Proves an `SV8` writer-context publication rejects staged shelf bytes when the selecting route still points at the same offset but that offset is no longer an `SV8` shelf.<br/>
    /// This protects warmed variable-identity shelf writers from publishing over stale physical topology.<br/>
    /// </summary>
    private static void RunInternalScalar8VarIdentityStaleRouteClaimProbe()
    {
        const int maxIdentityLength = 32;
        using Scalar8VarIdentityIndex index = Indexes.SV8.Create(
            backingKind: DataKernelBackingKind.Memory,
            name: "sv8-stale-route",
            maxIdentityLength: maxIdentityLength,
            telemetryOptions: DataKernelTelemetryOptions.FromLevel(LibraDexDiagnosticsLevel.Detailed));
        const ulong seedKey = 0x6200_0000_0000_0001UL;
        const ulong stagedKey = 0x6200_0000_0000_0002UL;
        byte[] seedIdentity = [0x62, 0x01];
        byte[] stagedIdentity = [0x62, 0x02];
        _ = index.Insert(seedKey, seedIdentity, allowDuplicateKeys: true);
        long originalTarget = index.Session.FindRouterTarget(index.RootRouterOffset, (byte)(seedKey >> 56));
        if (originalTarget <= 0)
        {
            throw new InvalidDataException("SV8 stale route claim probe could not resolve the seeded root route.");
        }

        LibraDexWriteContext staleWriter = index.Session.BeginScalar8VarIdentityWriteContext();
        bool contextActive = true;
        InvalidOperationException? staleClaimException = null;
        try
        {
            Scalar8VarIdentityRoutedInsertResult staged = index.Session.InsertWalkedRoutedScalar8VarIdentityNoSplitForWriteContext(
                staleWriter,
                index.RootRouterOffset,
                maxIdentityLength,
                stagedKey,
                stagedIdentity,
                allowDuplicateKeys: true);
            if (staged.InsertResult != Scalar8VarIdentityInsertResult.Inserted ||
                staged.Kind != Scalar8VarIdentityRoutedInsertKind.WalkedNoSplit ||
                staged.PrimaryOffset != originalTarget)
            {
                throw new InvalidDataException("SV8 stale route claim probe did not stage through the expected ordinary shelf.");
            }

            _ = index.Session.RewriteOffsetAsRootRouterForConcurrencyProof(originalTarget, allocationClassId: 1);
            contextActive = false;
            try
            {
                _ = index.Session.PublishScalar8VarIdentityWriteContext(staleWriter);
            }
            catch (InvalidOperationException ex)
            {
                staleClaimException = ex;
                index.Session.AbortScalar8VarIdentityWriteContext(staleWriter);
            }
        }
        finally
        {
            if (contextActive)
            {
                index.Session.AbortScalar8VarIdentityWriteContext(staleWriter);
            }
        }

        if (staleClaimException is null ||
            !staleClaimException.Message.Contains("SV8 writer-context route claim no longer matches", StringComparison.Ordinal))
        {
            throw new InvalidDataException("SV8 stale route claim probe expected publish-time validation to reject stale shelf bytes.");
        }
    }

    /// <summary>
    /// Proves the first internal `SV8` writer-context boundary: warmed ordinary variable-identity shelves can stage independently, while same-shelf staging conflicts deterministically.<br/>
    /// Terminal duplicate routes, linked overflow chains, and split/growth topology remain on the existing durability-batch path for this first variable-identity slice.<br/>
    /// </summary>
    private static void RunInternalScalar8VarIdentityWriterContextProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });
        const int maxIdentityLength = 32;
        _ = catalog.Indexes["sv8-writer-context"]["raw"].UInt64VarIdentityKeys(maxIdentityLength).Create();
        if (!catalog.Indexes.TryGetInfo("sv8-writer-context", "raw", out CatalogIndexInfo info))
        {
            throw new InvalidDataException("SV8 writer-context probe could not resolve created catalog metadata.");
        }

        Scalar8VarIdentityIndex index = new(catalog.Session, info.SlotIndex, info.Name, info.RootRouterOffset, info.VarIdentityMaxLength, ownsSession: false);
        const ulong leftSeed = 0x2100_0000_0000_0001UL;
        const ulong leftNext = 0x2100_0000_0000_0002UL;
        const ulong leftThird = 0x2100_0000_0000_0003UL;
        const ulong leftFourth = 0x2100_0000_0000_0004UL;
        const ulong rightSeed = 0xA100_0000_0000_0001UL;
        const ulong rightNext = 0xA100_0000_0000_0002UL;
        byte[] leftSeedIdentity = [0x10, 0x01];
        byte[] leftNextIdentity = [0x10, 0x02];
        byte[] leftThirdIdentity = [0x10, 0x03];
        byte[] leftFourthIdentity = [0x10, 0x04];
        byte[] rightSeedIdentity = [0x20, 0x01];
        byte[] rightNextIdentity = [0x20, 0x02];

        _ = index.Insert(leftSeed, leftSeedIdentity, allowDuplicateKeys: true);
        _ = index.Insert(rightSeed, rightSeedIdentity, allowDuplicateKeys: true);
        long leftShelf = catalog.Session.FindRouterTarget(info.RootRouterOffset, (byte)(leftSeed >> 56));
        long rightShelf = catalog.Session.FindRouterTarget(info.RootRouterOffset, (byte)(rightSeed >> 56));
        if (leftShelf == 0 || rightShelf == 0 || leftShelf == rightShelf)
        {
            throw new InvalidDataException("SV8 writer-context probe did not create two distinct root-prefix shelves.");
        }

        LibraDexWriteContext leftWriter = catalog.Session.BeginScalar8VarIdentityWriteContext();
        LibraDexWriteContext rightWriter = catalog.Session.BeginScalar8VarIdentityWriteContext();
        Scalar8VarIdentityRoutedInsertResult leftInsert = catalog.Session.InsertWalkedRoutedScalar8VarIdentityNoSplitForWriteContext(
            leftWriter,
            info.RootRouterOffset,
            maxIdentityLength,
            leftNext,
            leftNextIdentity,
            allowDuplicateKeys: true);
        Scalar8VarIdentityRoutedInsertResult rightInsert = catalog.Session.InsertWalkedRoutedScalar8VarIdentityNoSplitForWriteContext(
            rightWriter,
            info.RootRouterOffset,
            maxIdentityLength,
            rightNext,
            rightNextIdentity,
            allowDuplicateKeys: true);
        if (leftInsert.InsertResult != Scalar8VarIdentityInsertResult.Inserted ||
            rightInsert.InsertResult != Scalar8VarIdentityInsertResult.Inserted ||
            leftInsert.Kind != Scalar8VarIdentityRoutedInsertKind.WalkedNoSplit ||
            rightInsert.Kind != Scalar8VarIdentityRoutedInsertKind.WalkedNoSplit ||
            leftInsert.PrimaryOffset != leftShelf ||
            rightInsert.PrimaryOffset != rightShelf)
        {
            throw new InvalidDataException("SV8 writer-context probe did not stage the expected ordinary shelf-local inserts.");
        }

        _ = catalog.Session.PublishScalar8VarIdentityWriteContext(rightWriter);
        _ = catalog.Session.PublishScalar8VarIdentityWriteContext(leftWriter);
        using (Scalar8VarIdentityRangeReader leftReader = catalog.Session.OpenScalar8VarIdentityRangeReader(info.RootRouterOffset, maxIdentityLength, leftSeed, leftNext))
        {
            if (leftReader.Count != 2)
            {
                throw new InvalidDataException("SV8 writer-context probe did not publish left-shelf changes.");
            }
        }

        using (Scalar8VarIdentityRangeReader rightReader = catalog.Session.OpenScalar8VarIdentityRangeReader(info.RootRouterOffset, maxIdentityLength, rightSeed, rightNext))
        {
            if (rightReader.Count != 2)
            {
                throw new InvalidDataException("SV8 writer-context probe did not publish right-shelf changes.");
            }
        }

        LibraDexWriteContext deleteWriter = catalog.Session.BeginScalar8VarIdentityWriteContext();
        bool deleted = catalog.Session.DeleteScalar8VarIdentityExactTupleForWriteContext(
            deleteWriter,
            info.RootRouterOffset,
            maxIdentityLength,
            leftNext,
            leftNextIdentity);
        if (!deleted)
        {
            throw new InvalidDataException("SV8 writer-context probe could not stage the expected exact delete.");
        }

        _ = catalog.Session.PublishScalar8VarIdentityWriteContext(deleteWriter);
        using (Scalar8VarIdentityRangeReader leftAfterDelete = catalog.Session.OpenScalar8VarIdentityRangeReader(info.RootRouterOffset, maxIdentityLength, leftSeed, leftNext))
        {
            if (leftAfterDelete.Count != 1)
            {
                throw new InvalidDataException("SV8 writer-context exact delete did not remove only the targeted tuple.");
            }
        }

        LibraDexWriteContext owner = catalog.Session.BeginScalar8VarIdentityWriteContext();
        LibraDexWriteContext contender = catalog.Session.BeginScalar8VarIdentityWriteContext();
        _ = catalog.Session.InsertWalkedRoutedScalar8VarIdentityNoSplitForWriteContext(
            owner,
            info.RootRouterOffset,
            maxIdentityLength,
            leftThird,
            leftThirdIdentity,
            allowDuplicateKeys: true);
        LibraDexWriteContextScalar8VarIdentityShelfOwnershipException? conflictException = null;
        try
        {
            _ = catalog.Session.InsertWalkedRoutedScalar8VarIdentityNoSplitForWriteContext(
                contender,
                info.RootRouterOffset,
                maxIdentityLength,
                leftFourth,
                leftFourthIdentity,
                allowDuplicateKeys: true);
        }
        catch (LibraDexWriteContextScalar8VarIdentityShelfOwnershipException ex)
        {
            conflictException = ex;
        }

        catalog.Session.AbortScalar8VarIdentityWriteContext(contender);
        catalog.Session.AbortScalar8VarIdentityWriteContext(owner);
        if (conflictException is null ||
            conflictException.ShelfOffset != leftShelf ||
            !conflictException.Message.Contains("already owned by another LibraDex writer context", StringComparison.Ordinal))
        {
            throw new InvalidDataException("SV8 writer-context probe expected same-shelf ownership to be rejected deterministically.");
        }

        _ = index.Insert(leftThird, leftThirdIdentity, allowDuplicateKeys: true);
        _ = index.Insert(leftFourth, leftFourthIdentity, allowDuplicateKeys: true);
        LibraDexWriteContext rangeDeleteWriter = catalog.Session.BeginScalar8VarIdentityWriteContext();
        long rangeDeleted = catalog.Session.DeleteScalar8VarIdentityKeyRangeForWriteContext(
            rangeDeleteWriter,
            info.RootRouterOffset,
            maxIdentityLength,
            leftThird,
            leftFourth);
        if (rangeDeleted != 2)
        {
            throw new InvalidDataException($"SV8 writer-context range delete expected two staged deletes but saw {rangeDeleted}.");
        }

        _ = catalog.Session.PublishScalar8VarIdentityWriteContext(rangeDeleteWriter);
        using (Scalar8VarIdentityRangeReader leftAfterRangeDelete = catalog.Session.OpenScalar8VarIdentityRangeReader(info.RootRouterOffset, maxIdentityLength, leftSeed, leftFourth))
        {
            if (leftAfterRangeDelete.Count != 1)
            {
                throw new InvalidDataException("SV8 writer-context range delete did not remove the expected tuple interval.");
            }
        }
    }

    /// <summary>
    /// Proves an `SV16` writer-context publication rejects staged shelf bytes when the selecting route still points at the same offset but that offset is no longer an `SV16` shelf.<br/>
    /// This protects warmed wide-key variable-identity shelf writers from publishing over stale physical topology.<br/>
    /// </summary>
    private static void RunInternalScalar16VarIdentityStaleRouteClaimProbe()
    {
        const int maxIdentityLength = 32;
        using Scalar16VarIdentityIndex index = Indexes.SV16.Create(
            backingKind: DataKernelBackingKind.Memory,
            name: "sv16-stale-route",
            maxIdentityLength: maxIdentityLength,
            telemetryOptions: DataKernelTelemetryOptions.FromLevel(LibraDexDiagnosticsLevel.Detailed));
        const ulong seedHigh = 0x6300_0000_0000_0000UL;
        const ulong seedLow = 0x0000_0000_0000_0001UL;
        const ulong stagedLow = 0x0000_0000_0000_0002UL;
        byte[] seedIdentity = [0x63, 0x01];
        byte[] stagedIdentity = [0x63, 0x02];
        _ = index.Insert(seedHigh, seedLow, seedIdentity, allowDuplicateKeys: true);
        long originalTarget = index.Session.FindRouterTarget(index.RootRouterOffset, (byte)(seedHigh >> 56));
        if (originalTarget <= 0)
        {
            throw new InvalidDataException("SV16 stale route claim probe could not resolve the seeded root route.");
        }

        LibraDexWriteContext staleWriter = index.Session.BeginScalar16VarIdentityWriteContext();
        bool contextActive = true;
        InvalidOperationException? staleClaimException = null;
        try
        {
            Scalar16VarIdentityRoutedInsertResult staged = index.Session.InsertWalkedRoutedScalar16VarIdentityNoSplitForWriteContext(
                staleWriter,
                index.RootRouterOffset,
                maxIdentityLength,
                seedHigh,
                stagedLow,
                stagedIdentity,
                allowDuplicateKeys: true);
            if (staged.InsertResult != Scalar16VarIdentityInsertResult.Inserted ||
                staged.Kind != Scalar16VarIdentityRoutedInsertKind.WalkedNoSplit ||
                staged.PrimaryOffset != originalTarget)
            {
                throw new InvalidDataException("SV16 stale route claim probe did not stage through the expected ordinary shelf.");
            }

            _ = index.Session.RewriteOffsetAsRootRouterForConcurrencyProof(originalTarget, allocationClassId: 1);
            contextActive = false;
            try
            {
                _ = index.Session.PublishScalar16VarIdentityWriteContext(staleWriter);
            }
            catch (InvalidOperationException ex)
            {
                staleClaimException = ex;
                index.Session.AbortScalar16VarIdentityWriteContext(staleWriter);
            }
        }
        finally
        {
            if (contextActive)
            {
                index.Session.AbortScalar16VarIdentityWriteContext(staleWriter);
            }
        }

        if (staleClaimException is null ||
            !staleClaimException.Message.Contains("SV16 writer-context route claim no longer matches", StringComparison.Ordinal))
        {
            throw new InvalidDataException("SV16 stale route claim probe expected publish-time validation to reject stale shelf bytes.");
        }
    }

    /// <summary>
    /// Proves the first internal `SV16` writer-context boundary: warmed ordinary variable-identity shelves can stage independently, while same-shelf staging conflicts deterministically.<br/>
    /// Terminal duplicate routes, linked overflow chains, and split/growth topology remain on the existing durability-batch path for this first variable-identity slice.<br/>
    /// </summary>
    private static void RunInternalScalar16VarIdentityWriterContextProbe()
    {
        const int maxIdentityLength = 32;
        using Scalar16VarIdentityIndex index = Indexes.SV16.Create(
            backingKind: DataKernelBackingKind.Memory,
            name: "sv16-writer-context",
            maxIdentityLength: maxIdentityLength,
            telemetryOptions: DataKernelTelemetryOptions.FromLevel(LibraDexDiagnosticsLevel.Detailed));

        const ulong leftHigh = 0x2200_0000_0000_0000UL;
        const ulong leftSeedLow = 0x0000_0000_0000_0001UL;
        const ulong leftNextLow = 0x0000_0000_0000_0002UL;
        const ulong leftThirdLow = 0x0000_0000_0000_0003UL;
        const ulong leftFourthLow = 0x0000_0000_0000_0004UL;
        const ulong rightHigh = 0xA200_0000_0000_0000UL;
        const ulong rightSeedLow = 0x0000_0000_0000_0001UL;
        const ulong rightNextLow = 0x0000_0000_0000_0002UL;
        byte[] leftSeedIdentity = [0x11, 0x01];
        byte[] leftNextIdentity = [0x11, 0x02];
        byte[] leftThirdIdentity = [0x11, 0x03];
        byte[] leftFourthIdentity = [0x11, 0x04];
        byte[] rightSeedIdentity = [0x21, 0x01];
        byte[] rightNextIdentity = [0x21, 0x02];

        _ = index.Insert(leftHigh, leftSeedLow, leftSeedIdentity, allowDuplicateKeys: true);
        _ = index.Insert(rightHigh, rightSeedLow, rightSeedIdentity, allowDuplicateKeys: true);
        long leftShelf = index.Session.FindRouterTarget(index.RootRouterOffset, (byte)(leftHigh >> 56));
        long rightShelf = index.Session.FindRouterTarget(index.RootRouterOffset, (byte)(rightHigh >> 56));
        if (leftShelf == 0 || rightShelf == 0 || leftShelf == rightShelf)
        {
            throw new InvalidDataException("SV16 writer-context probe did not create two distinct root-prefix shelves.");
        }

        LibraDexWriteContext leftWriter = index.Session.BeginScalar16VarIdentityWriteContext();
        LibraDexWriteContext rightWriter = index.Session.BeginScalar16VarIdentityWriteContext();
        Scalar16VarIdentityRoutedInsertResult leftInsert = index.Session.InsertWalkedRoutedScalar16VarIdentityNoSplitForWriteContext(
            leftWriter,
            index.RootRouterOffset,
            maxIdentityLength,
            leftHigh,
            leftNextLow,
            leftNextIdentity,
            allowDuplicateKeys: true);
        Scalar16VarIdentityRoutedInsertResult rightInsert = index.Session.InsertWalkedRoutedScalar16VarIdentityNoSplitForWriteContext(
            rightWriter,
            index.RootRouterOffset,
            maxIdentityLength,
            rightHigh,
            rightNextLow,
            rightNextIdentity,
            allowDuplicateKeys: true);
        if (leftInsert.InsertResult != Scalar16VarIdentityInsertResult.Inserted ||
            rightInsert.InsertResult != Scalar16VarIdentityInsertResult.Inserted ||
            leftInsert.Kind != Scalar16VarIdentityRoutedInsertKind.WalkedNoSplit ||
            rightInsert.Kind != Scalar16VarIdentityRoutedInsertKind.WalkedNoSplit ||
            leftInsert.PrimaryOffset != leftShelf ||
            rightInsert.PrimaryOffset != rightShelf)
        {
            throw new InvalidDataException("SV16 writer-context probe did not stage the expected ordinary shelf-local inserts.");
        }

        _ = index.Session.PublishScalar16VarIdentityWriteContext(rightWriter);
        _ = index.Session.PublishScalar16VarIdentityWriteContext(leftWriter);
        using (Scalar16VarIdentityRangeReader leftReader = index.Session.OpenScalar16VarIdentityRangeReader(index.RootRouterOffset, maxIdentityLength, leftHigh, leftSeedLow, leftHigh, leftNextLow))
        {
            if (leftReader.Count != 2)
            {
                throw new InvalidDataException("SV16 writer-context probe did not publish left-shelf changes.");
            }
        }

        using (Scalar16VarIdentityRangeReader rightReader = index.Session.OpenScalar16VarIdentityRangeReader(index.RootRouterOffset, maxIdentityLength, rightHigh, rightSeedLow, rightHigh, rightNextLow))
        {
            if (rightReader.Count != 2)
            {
                throw new InvalidDataException("SV16 writer-context probe did not publish right-shelf changes.");
            }
        }

        LibraDexWriteContext deleteWriter = index.Session.BeginScalar16VarIdentityWriteContext();
        bool deleted = index.Session.DeleteScalar16VarIdentityExactTupleForWriteContext(
            deleteWriter,
            index.RootRouterOffset,
            maxIdentityLength,
            leftHigh,
            leftNextLow,
            leftNextIdentity);
        if (!deleted)
        {
            throw new InvalidDataException("SV16 writer-context probe could not stage the expected exact delete.");
        }

        _ = index.Session.PublishScalar16VarIdentityWriteContext(deleteWriter);
        using (Scalar16VarIdentityRangeReader leftAfterDelete = index.Session.OpenScalar16VarIdentityRangeReader(index.RootRouterOffset, maxIdentityLength, leftHigh, leftSeedLow, leftHigh, leftNextLow))
        {
            if (leftAfterDelete.Count != 1)
            {
                throw new InvalidDataException("SV16 writer-context exact delete did not remove only the targeted tuple.");
            }
        }

        LibraDexWriteContext owner = index.Session.BeginScalar16VarIdentityWriteContext();
        LibraDexWriteContext contender = index.Session.BeginScalar16VarIdentityWriteContext();
        _ = index.Session.InsertWalkedRoutedScalar16VarIdentityNoSplitForWriteContext(
            owner,
            index.RootRouterOffset,
            maxIdentityLength,
            leftHigh,
            leftThirdLow,
            leftThirdIdentity,
            allowDuplicateKeys: true);
        LibraDexWriteContextScalar16VarIdentityShelfOwnershipException? conflictException = null;
        try
        {
            _ = index.Session.InsertWalkedRoutedScalar16VarIdentityNoSplitForWriteContext(
                contender,
                index.RootRouterOffset,
                maxIdentityLength,
                leftHigh,
                leftFourthLow,
                leftFourthIdentity,
                allowDuplicateKeys: true);
        }
        catch (LibraDexWriteContextScalar16VarIdentityShelfOwnershipException ex)
        {
            conflictException = ex;
        }

        index.Session.AbortScalar16VarIdentityWriteContext(contender);
        index.Session.AbortScalar16VarIdentityWriteContext(owner);
        if (conflictException is null ||
            conflictException.ShelfOffset != leftShelf ||
            !conflictException.Message.Contains("already owned by another LibraDex writer context", StringComparison.Ordinal))
        {
            throw new InvalidDataException("SV16 writer-context probe expected same-shelf ownership to be rejected deterministically.");
        }

        _ = index.Insert(leftHigh, leftThirdLow, leftThirdIdentity, allowDuplicateKeys: true);
        _ = index.Insert(leftHigh, leftFourthLow, leftFourthIdentity, allowDuplicateKeys: true);
        LibraDexWriteContext rangeDeleteWriter = index.Session.BeginScalar16VarIdentityWriteContext();
        long rangeDeleted = index.Session.DeleteScalar16VarIdentityKeyRangeForWriteContext(
            rangeDeleteWriter,
            index.RootRouterOffset,
            maxIdentityLength,
            leftHigh,
            leftThirdLow,
            leftHigh,
            leftFourthLow);
        if (rangeDeleted != 2)
        {
            throw new InvalidDataException($"SV16 writer-context range delete expected two staged deletes but saw {rangeDeleted}.");
        }

        _ = index.Session.PublishScalar16VarIdentityWriteContext(rangeDeleteWriter);
        using (Scalar16VarIdentityRangeReader leftAfterRangeDelete = index.Session.OpenScalar16VarIdentityRangeReader(index.RootRouterOffset, maxIdentityLength, leftHigh, leftSeedLow, leftHigh, leftFourthLow))
        {
            if (leftAfterRangeDelete.Count != 1)
            {
                throw new InvalidDataException("SV16 writer-context range delete did not remove the expected tuple interval.");
            }
        }
    }

    /// <summary>
    /// Proves the first internal `VV` writer-context boundary: warmed ordinary var-key/var-identity shelves can stage independently, while same-shelf staging conflicts deterministically.<br/>
    /// Terminal var-identity routes and split/growth topology remain on the existing durability-batch path for this first `VV` slice.<br/>
    /// </summary>
    private static void RunInternalVarKeyVarIdentityWriterContextProbe()
    {
        const int maxKeyLength = 32;
        const int maxIdentityLength = 32;
        using VarKeyVarIdentityIndex index = Indexes.VV.Create(
            backingKind: DataKernelBackingKind.Memory,
            name: "vv-writer-context",
            maxKeyLength: maxKeyLength,
            maxIdentityLength: maxIdentityLength,
            telemetryOptions: DataKernelTelemetryOptions.FromLevel(LibraDexDiagnosticsLevel.Detailed));

        byte[] nullKey = LibraDexVarLenKeyCodec.EncodeNull(index.MaxPhysicalKeyLength, nameof(nullKey));
        byte[] emptyKey = LibraDexVarLenKeyCodec.Encode(Array.Empty<byte>(), index.MaxPhysicalKeyLength, nameof(emptyKey));
        byte[] valueSeedKey = LibraDexVarLenKeyCodec.Encode([0x30, 0x01], index.MaxPhysicalKeyLength, nameof(valueSeedKey));
        byte[] valueNextKey = LibraDexVarLenKeyCodec.Encode([0x30, 0x02], index.MaxPhysicalKeyLength, nameof(valueNextKey));
        byte[] valueThirdKey = LibraDexVarLenKeyCodec.Encode([0x30, 0x03], index.MaxPhysicalKeyLength, nameof(valueThirdKey));
        byte[] valueFourthKey = LibraDexVarLenKeyCodec.Encode([0x30, 0x04], index.MaxPhysicalKeyLength, nameof(valueFourthKey));
        byte[] nullSeedIdentity = [0x12, 0x01];
        byte[] nullNextIdentity = [0x12, 0x02];
        byte[] emptySeedIdentity = [0x22, 0x01];
        byte[] emptyNextIdentity = [0x22, 0x02];
        byte[] valueSeedIdentity = [0x32, 0x01];
        byte[] valueNextIdentity = [0x32, 0x02];
        byte[] valueThirdIdentity = [0x32, 0x03];
        byte[] valueFourthIdentity = [0x32, 0x04];

        _ = index.Insert((byte[]?)null, nullSeedIdentity, allowDuplicateKeys: true);
        _ = index.Insert(Array.Empty<byte>(), emptySeedIdentity, allowDuplicateKeys: true);
        _ = index.Insert([0x30, 0x01], valueSeedIdentity, allowDuplicateKeys: true);
        long nullShelf = index.Session.FindRouterTarget(index.RootRouterOffset, LibraDexVarLenKeyCodec.NullMarker);
        long emptyShelf = index.Session.FindRouterTarget(index.RootRouterOffset, LibraDexVarLenKeyCodec.EmptyMarker);
        long valueShelf = index.Session.FindRouterTarget(index.RootRouterOffset, LibraDexVarLenKeyCodec.ValueMarker);
        if (nullShelf == 0 || emptyShelf == 0 || valueShelf == 0 || nullShelf == emptyShelf || nullShelf == valueShelf || emptyShelf == valueShelf)
        {
            throw new InvalidDataException("VV writer-context probe did not create distinct sentinel root-prefix shelves.");
        }

        LibraDexWriteContext nullWriter = index.Session.BeginVarKeyVarIdentityWriteContext();
        LibraDexWriteContext emptyWriter = index.Session.BeginVarKeyVarIdentityWriteContext();
        VarKeyVarIdentityRoutedInsertResult nullInsert = index.Session.InsertWalkedRoutedVarKeyVarIdentityNoSplitForWriteContext(
            nullWriter,
            index.RootRouterOffset,
            index.MaxPhysicalKeyLength,
            maxIdentityLength,
            nullKey,
            nullNextIdentity,
            allowDuplicateKeys: true);
        VarKeyVarIdentityRoutedInsertResult emptyInsert = index.Session.InsertWalkedRoutedVarKeyVarIdentityNoSplitForWriteContext(
            emptyWriter,
            index.RootRouterOffset,
            index.MaxPhysicalKeyLength,
            maxIdentityLength,
            emptyKey,
            emptyNextIdentity,
            allowDuplicateKeys: true);
        if (nullInsert.InsertResult != VarKeyVarIdentityInsertResult.Inserted ||
            emptyInsert.InsertResult != VarKeyVarIdentityInsertResult.Inserted ||
            nullInsert.Kind != VarKeyVarIdentityRoutedInsertKind.WalkedNoSplit ||
            emptyInsert.Kind != VarKeyVarIdentityRoutedInsertKind.WalkedNoSplit ||
            nullInsert.SourceShelfOffset != nullShelf ||
            emptyInsert.SourceShelfOffset != emptyShelf)
        {
            throw new InvalidDataException("VV writer-context probe did not stage the expected ordinary shelf-local inserts.");
        }

        _ = index.Session.PublishVarKeyVarIdentityWriteContext(emptyWriter);
        _ = index.Session.PublishVarKeyVarIdentityWriteContext(nullWriter);
        using (VarKeyVarIdentityRangeReader nullReader = index.OpenEncodedRangeReader(nullKey, nullKey))
        {
            if (nullReader.Count != 2)
            {
                throw new InvalidDataException("VV writer-context probe did not publish null-shelf changes.");
            }
        }

        using (VarKeyVarIdentityRangeReader emptyReader = index.OpenEncodedRangeReader(emptyKey, emptyKey))
        {
            if (emptyReader.Count != 2)
            {
                throw new InvalidDataException("VV writer-context probe did not publish empty-shelf changes.");
            }
        }

        LibraDexWriteContext deleteWriter = index.Session.BeginVarKeyVarIdentityWriteContext();
        bool deleted = index.Session.DeleteVarKeyVarIdentityExactTupleForWriteContext(
            deleteWriter,
            index.RootRouterOffset,
            index.MaxPhysicalKeyLength,
            maxIdentityLength,
            nullKey,
            nullNextIdentity);
        if (!deleted)
        {
            throw new InvalidDataException("VV writer-context probe could not stage the expected exact delete.");
        }

        _ = index.Session.PublishVarKeyVarIdentityWriteContext(deleteWriter);
        using (VarKeyVarIdentityRangeReader nullAfterDelete = index.OpenEncodedRangeReader(nullKey, nullKey))
        {
            if (nullAfterDelete.Count != 1)
            {
                throw new InvalidDataException("VV writer-context exact delete did not remove only the targeted tuple.");
            }
        }

        LibraDexWriteContext owner = index.Session.BeginVarKeyVarIdentityWriteContext();
        LibraDexWriteContext contender = index.Session.BeginVarKeyVarIdentityWriteContext();
        _ = index.Session.InsertWalkedRoutedVarKeyVarIdentityNoSplitForWriteContext(
            owner,
            index.RootRouterOffset,
            index.MaxPhysicalKeyLength,
            maxIdentityLength,
            valueNextKey,
            valueNextIdentity,
            allowDuplicateKeys: true);
        LibraDexWriteContextVarKeyVarIdentityShelfOwnershipException? conflictException = null;
        try
        {
            _ = index.Session.InsertWalkedRoutedVarKeyVarIdentityNoSplitForWriteContext(
                contender,
                index.RootRouterOffset,
                index.MaxPhysicalKeyLength,
                maxIdentityLength,
                valueThirdKey,
                valueThirdIdentity,
                allowDuplicateKeys: true);
        }
        catch (LibraDexWriteContextVarKeyVarIdentityShelfOwnershipException ex)
        {
            conflictException = ex;
        }

        index.Session.AbortVarKeyVarIdentityWriteContext(contender);
        index.Session.AbortVarKeyVarIdentityWriteContext(owner);
        if (conflictException is null ||
            conflictException.ShelfOffset != valueShelf ||
            !conflictException.Message.Contains("already owned by another LibraDex writer context", StringComparison.Ordinal))
        {
            throw new InvalidDataException("VV writer-context probe expected same-shelf ownership to be rejected deterministically.");
        }

        _ = index.Insert([0x30, 0x02], valueNextIdentity, allowDuplicateKeys: true);
        _ = index.Insert([0x30, 0x03], valueThirdIdentity, allowDuplicateKeys: true);
        _ = index.Insert([0x30, 0x04], valueFourthIdentity, allowDuplicateKeys: true);
        LibraDexWriteContext rangeDeleteWriter = index.Session.BeginVarKeyVarIdentityWriteContext();
        long rangeDeleted = index.Session.DeleteVarKeyVarIdentityKeyRangeForWriteContext(
            rangeDeleteWriter,
            index.RootRouterOffset,
            index.MaxPhysicalKeyLength,
            maxIdentityLength,
            valueThirdKey,
            valueFourthKey);
        if (rangeDeleted != 2)
        {
            throw new InvalidDataException($"VV writer-context range delete expected two staged deletes but saw {rangeDeleted}.");
        }

        _ = index.Session.PublishVarKeyVarIdentityWriteContext(rangeDeleteWriter);
        using (VarKeyVarIdentityRangeReader valueAfterRangeDelete = index.OpenEncodedRangeReader(valueSeedKey, valueFourthKey))
        {
            if (valueAfterRangeDelete.Count != 2)
            {
                throw new InvalidDataException("VV writer-context range delete did not remove the expected tuple interval.");
            }
        }
    }

    /// <summary>
    /// Proves a `VV` writer-context publication rejects staged shelf bytes when the selecting route still points at the same offset but that offset is no longer a `VV` shelf.<br/>
    /// This protects warmed variable-key/variable-identity shelf writers from publishing over stale physical topology.<br/>
    /// </summary>
    private static void RunInternalVarKeyVarIdentityStaleRouteClaimProbe()
    {
        const int maxKeyLength = 32;
        const int maxIdentityLength = 32;
        using VarKeyVarIdentityIndex index = Indexes.VV.Create(
            backingKind: DataKernelBackingKind.Memory,
            name: "vv-stale-route",
            maxKeyLength: maxKeyLength,
            maxIdentityLength: maxIdentityLength,
            telemetryOptions: DataKernelTelemetryOptions.FromLevel(LibraDexDiagnosticsLevel.Detailed));
        byte[] seedRawKey = [0x64, 0x01];
        byte[] stagedRawKey = [0x64, 0x02];
        byte[] seedKey = LibraDexVarLenKeyCodec.Encode(seedRawKey, index.MaxPhysicalKeyLength, nameof(seedKey));
        byte[] stagedKey = LibraDexVarLenKeyCodec.Encode(stagedRawKey, index.MaxPhysicalKeyLength, nameof(stagedKey));
        byte[] seedIdentity = [0x64, 0x01];
        byte[] stagedIdentity = [0x64, 0x02];
        _ = index.Insert(seedRawKey, seedIdentity, allowDuplicateKeys: true);
        long originalTarget = index.Session.FindRouterTarget(index.RootRouterOffset, LibraDexVarLenKeyCodec.ValueMarker);
        if (originalTarget <= 0)
        {
            throw new InvalidDataException("VV stale route claim probe could not resolve the seeded value route.");
        }

        LibraDexWriteContext staleWriter = index.Session.BeginVarKeyVarIdentityWriteContext();
        bool contextActive = true;
        InvalidOperationException? staleClaimException = null;
        try
        {
            VarKeyVarIdentityRoutedInsertResult staged = index.Session.InsertWalkedRoutedVarKeyVarIdentityNoSplitForWriteContext(
                staleWriter,
                index.RootRouterOffset,
                index.MaxPhysicalKeyLength,
                maxIdentityLength,
                stagedKey,
                stagedIdentity,
                allowDuplicateKeys: true);
            if (staged.InsertResult != VarKeyVarIdentityInsertResult.Inserted ||
                staged.Kind != VarKeyVarIdentityRoutedInsertKind.WalkedNoSplit ||
                staged.SourceShelfOffset != originalTarget)
            {
                throw new InvalidDataException("VV stale route claim probe did not stage through the expected ordinary shelf.");
            }

            _ = index.Session.RewriteOffsetAsRootRouterForConcurrencyProof(originalTarget, allocationClassId: 1);
            contextActive = false;
            try
            {
                _ = index.Session.PublishVarKeyVarIdentityWriteContext(staleWriter);
            }
            catch (InvalidOperationException ex)
            {
                staleClaimException = ex;
                index.Session.AbortVarKeyVarIdentityWriteContext(staleWriter);
            }
        }
        finally
        {
            if (contextActive)
            {
                index.Session.AbortVarKeyVarIdentityWriteContext(staleWriter);
            }
        }

        if (staleClaimException is null ||
            !staleClaimException.Message.Contains("VV writer-context route claim no longer matches", StringComparison.Ordinal))
        {
            throw new InvalidDataException("VV stale route claim probe expected publish-time validation to reject stale shelf bytes.");
        }
    }

    /// <summary>
    /// Proves that ordinary storage publication no longer holds the broad `SS8-8` writer-operation gate.<br/>
    /// Writer-context topology admission and full insert staging must proceed while memory-backed storage publication is held because route/shelf reads use the captured committed snapshot; publish still waits for the coherent storage-publication scope.<br/>
    /// </summary>
    private static void RunInternalScalar8Scalar8StoragePublicationGateProbe()
    {
        using Scalar8Scalar8Index index = Indexes.SS88.Create(
            backingKind: DataKernelBackingKind.Memory,
            name: "writer-context-storage-publication-gate-ss88",
            options: CreateDesignPerfOptions(),
            developerMetadata: CreateDesignPerfMetadata(9914),
            telemetryOptions: DataKernelTelemetryOptions.FromLevel(LibraDexDiagnosticsLevel.Detailed));

        const ulong keyA = 0x1200_0000_0000_0001UL;
        const ulong keyA2 = 0x1200_0000_0000_0002UL;
        _ = index.InsertEncoded(keyA, 1201);

        using ManualResetEventSlim admitReady = new(false);
        using ManualResetEventSlim insertStaged = new(false);
        using ManualResetEventSlim releaseStoragePublication = new(false);
        using ManualResetEventSlim insertFinished = new(false);
        Exception? admitException = null;
        Exception? insertException = null;
        Scalar8Scalar8EncodedInsertResult insertResult = default;

        index.Session.EnterStoragePublicationForValidation();
        Task admitTask = Task.Run(() =>
        {
            try
            {
                index.Session.EnterScalar8Scalar8WriterContextStaging(index.Handle.RootRouterOffset);
                index.Session.ExitScalar8Scalar8WriterContextStaging(index.Handle.RootRouterOffset);
            }
            catch (Exception ex)
            {
                admitException = ex;
            }
            finally
            {
                admitReady.Set();
            }
        });

        if (!admitReady.Wait(TimeSpan.FromSeconds(5)))
        {
            index.Session.ExitStoragePublicationForValidation();
            throw new InvalidDataException("SS8-8 storage-publication probe expected writer-context admission to bypass the storage publication gate.");
        }

        if (admitException is not null)
        {
            index.Session.ExitStoragePublicationForValidation();
            throw new InvalidDataException("SS8-8 storage-publication probe writer-context admission failed.", admitException);
        }

        Task insertTask = Task.Run(() =>
        {
            LibraDexWriteContext? writeContext = null;
            try
            {
                writeContext = index.BeginWriteContext();
                insertResult = index.InsertEncodedForWriteContext(writeContext, keyA2, 1202, allowDuplicateKeys: true);
                insertStaged.Set();
                releaseStoragePublication.Wait();
                _ = index.PublishWriteContext(writeContext);
                writeContext = null;
            }
            catch (Exception ex)
            {
                insertException = ex;
            }
            finally
            {
                if (writeContext is not null)
                {
                    index.AbortWriteContext(writeContext);
                }

                insertFinished.Set();
            }
        });

        if (!insertStaged.Wait(TimeSpan.FromSeconds(5)))
        {
            index.Session.ExitStoragePublicationForValidation();
            releaseStoragePublication.Set();
            throw new InvalidDataException("SS8-8 storage-publication probe expected writer-context insert staging to use the memory snapshot while storage publication was held.");
        }

        if (insertFinished.Wait(TimeSpan.FromMilliseconds(100)))
        {
            index.Session.ExitStoragePublicationForValidation();
            releaseStoragePublication.Set();
            throw new InvalidDataException("SS8-8 storage-publication probe expected writer-context publish to wait on the held storage publication scope.");
        }

        index.Session.ExitStoragePublicationForValidation();
        releaseStoragePublication.Set();
        if (!insertFinished.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new InvalidDataException("SS8-8 storage-publication probe insert did not finish after storage publication was released.");
        }

        Task.WaitAll(admitTask, insertTask);
        if (insertException is not null)
        {
            throw new InvalidDataException("SS8-8 storage-publication probe insert failed after publication release.", insertException);
        }

        if (insertResult.Outcome != Scalar8Scalar8EncodedInsertOutcome.Inserted ||
            insertResult.StructuralKind != Scalar8Scalar8RoutedInsertKind.WalkedNoSplit ||
            insertResult.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.None)
        {
            throw new InvalidDataException($"SS8-8 storage-publication probe insert had unexpected attribution. outcome={insertResult.Outcome} kind={insertResult.StructuralKind} path={insertResult.QueuedInsertPath}.");
        }

        ulong[] identities = new ulong[2];
        Scalar8Scalar8EncodedRangeReadResult read = index.ReadEncodedRange(keyA, keyA2, identities);
        if (read.IdentityCount != 2 || identities[0] != 1201 || identities[1] != 1202)
        {
            throw new InvalidDataException("SS8-8 storage-publication probe insert was not readable after release.");
        }
    }

    /// <summary>
    /// Proves that a queued full-shelf `SS8-8` split fallback can overlap an existing-shelf writer-context insert without route/cache corruption.<br/>
    /// The split key fills a root-prefix shelf to capacity first, forcing queued writer fallback through the exclusive topology gate, while the other key remains a normal shelf-local writer-context insert.<br/>
    /// </summary>
    private static void RunInternalScalar8Scalar8QueuedSplitFallbackProbe()
    {
        using Scalar8Scalar8Index index = Indexes.SS88.Create(
            backingKind: DataKernelBackingKind.Memory,
            name: "queued-split-fallback-ss88",
            options: CreateDesignPerfOptions(),
            developerMetadata: CreateDesignPerfMetadata(9910),
            telemetryOptions: DataKernelTelemetryOptions.FromLevel(LibraDexDiagnosticsLevel.Detailed));

        Scalar8Scalar8Profile profile = index.Handle.Profile;
        const ulong splitPrefixBase = 0x5500_0000_0000_0000UL;
        const ulong writerPrefixBase = 0x6600_0000_0000_0000UL;
        for (int i = 0; i < profile.MaxItemCount; i++)
        {
            ulong key = splitPrefixBase + (ulong)i;
            Scalar8Scalar8EncodedInsertResult result = index.InsertEncoded(key, key);
            if (result.Outcome != Scalar8Scalar8EncodedInsertOutcome.Inserted)
            {
                throw new InvalidDataException($"SS8-8 split fallback fixture could not fill item {i}; outcome={result.Outcome}.");
            }
        }

        _ = index.InsertEncoded(writerPrefixBase + 1, 6601);
        long writerShelf = index.Session.FindRouterTarget(index.Handle.RootRouterOffset, 0x66);
        if (writerShelf == 0)
        {
            throw new InvalidDataException("SS8-8 split fallback fixture did not create the writer-context shelf.");
        }

        Scalar8Scalar8QueuedWriter queuedWriter = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);
        ulong splitKey = splitPrefixBase + (ulong)profile.MaxItemCount;
        ulong writerKey = writerPrefixBase + 2;
        using ManualResetEventSlim startGate = new(false);
        using CountdownEvent readyGate = new(2);
        Scalar8Scalar8EncodedInsertResult splitResult = default;
        Scalar8Scalar8EncodedInsertResult writerResult = default;

        Task splitTask = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            splitResult = queuedWriter.InsertEncoded(splitKey, splitKey, allowDuplicateKeys: true);
        });

        Task writerTask = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            writerResult = queuedWriter.InsertEncoded(writerKey, 6602, allowDuplicateKeys: true);
        });

        if (!readyGate.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidDataException("SS8-8 queued split fallback probe could not ready both callers.");
        }

        startGate.Set();
        Task.WaitAll(splitTask, writerTask);

        if (splitResult.Outcome != Scalar8Scalar8EncodedInsertOutcome.Inserted ||
            splitResult.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.NarrowTopologyPublisher ||
            splitResult.StructuralKind is not (Scalar8Scalar8RoutedInsertKind.RootPrefixSplit or Scalar8Scalar8RoutedInsertKind.ShelfTransformSplit or Scalar8Scalar8RoutedInsertKind.WalkedShelfTransformSplit or Scalar8Scalar8RoutedInsertKind.WalkedParentRouteSplit))
        {
            throw new InvalidDataException($"SS8-8 queued split fallback did not use a serialized split path. outcome={splitResult.Outcome} kind={splitResult.StructuralKind} path={splitResult.QueuedInsertPath}.");
        }

        if (writerResult.Outcome != Scalar8Scalar8EncodedInsertOutcome.Inserted ||
            writerResult.StructuralKind != Scalar8Scalar8RoutedInsertKind.WalkedNoSplit ||
            writerResult.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext ||
            writerResult.PrimaryOffset != writerShelf)
        {
            throw new InvalidDataException($"SS8-8 queued split fallback overlap did not preserve writer-context insert. outcome={writerResult.Outcome} kind={writerResult.StructuralKind} path={writerResult.QueuedInsertPath} offset={writerResult.PrimaryOffset}/{writerShelf}.");
        }

        ulong[] splitIdentities = new ulong[profile.MaxItemCount + 1];
        Scalar8Scalar8EncodedRangeReadResult splitRead = index.ReadEncodedRange(splitPrefixBase, splitKey, splitIdentities);
        if (splitRead.IdentityCount != profile.MaxItemCount + 1 ||
            splitIdentities[profile.MaxItemCount] != splitKey)
        {
            throw new InvalidDataException("SS8-8 queued split fallback insert was not readable after overlap.");
        }

        ulong[] writerIdentities = new ulong[4];
        Scalar8Scalar8EncodedRangeReadResult writerRead = index.ReadEncodedRange(writerPrefixBase + 1, writerKey, writerIdentities);
        if (writerRead.IdentityCount != 2 ||
            writerIdentities[0] != 6601 ||
            writerIdentities[1] != 6602)
        {
            throw new InvalidDataException("SS8-8 queued split fallback overlap writer-context insert was not readable.");
        }
    }

    /// <summary>
    /// Proves that a queued duplicate-run `SS8-8` fallback can overlap an existing-shelf writer-context insert without route/cache corruption.<br/>
    /// The duplicate key fills one shelf to capacity first, forcing the next same-key insert through serialized fallback, while the other key remains a normal shelf-local writer-context insert.<br/>
    /// </summary>
    private static void RunInternalScalar8Scalar8QueuedDuplicateRunFallbackProbe()
    {
        using Scalar8Scalar8Index index = Indexes.SS88.Create(
            backingKind: DataKernelBackingKind.Memory,
            name: "queued-duplicate-fallback-ss88",
            options: CreateDesignPerfOptions(),
            developerMetadata: CreateDesignPerfMetadata(9911),
            telemetryOptions: DataKernelTelemetryOptions.FromLevel(LibraDexDiagnosticsLevel.Detailed));

        Scalar8Scalar8Profile profile = index.Handle.Profile;
        const ulong duplicateKey = 0x7700_0000_0000_0001UL;
        const ulong writerPrefixBase = 0x8800_0000_0000_0000UL;
        for (int i = 0; i < profile.MaxItemCount; i++)
        {
            Scalar8Scalar8EncodedInsertResult result = index.InsertEncoded(duplicateKey, (ulong)(770_000 + i), allowDuplicateKeys: true);
            if (result.Outcome != Scalar8Scalar8EncodedInsertOutcome.Inserted)
            {
                throw new InvalidDataException($"SS8-8 duplicate-run fallback fixture could not fill item {i}; outcome={result.Outcome}.");
            }
        }

        _ = index.InsertEncoded(writerPrefixBase + 1, 8801);
        long writerShelf = index.Session.FindRouterTarget(index.Handle.RootRouterOffset, 0x88);
        if (writerShelf == 0)
        {
            throw new InvalidDataException("SS8-8 duplicate-run fallback fixture did not create the writer-context shelf.");
        }

        Scalar8Scalar8QueuedWriter queuedWriter = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);
        ulong duplicateIdentity = 770_000UL + (ulong)profile.MaxItemCount;
        ulong writerKey = writerPrefixBase + 2;
        using ManualResetEventSlim startGate = new(false);
        using CountdownEvent readyGate = new(2);
        Scalar8Scalar8EncodedInsertResult duplicateResult = default;
        Scalar8Scalar8EncodedInsertResult writerResult = default;

        Task duplicateTask = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            duplicateResult = queuedWriter.InsertEncoded(duplicateKey, duplicateIdentity, allowDuplicateKeys: true);
        });

        Task writerTask = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            writerResult = queuedWriter.InsertEncoded(writerKey, 8802, allowDuplicateKeys: true);
        });

        if (!readyGate.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidDataException("SS8-8 queued duplicate-run fallback probe could not ready both callers.");
        }

        startGate.Set();
        Task.WaitAll(duplicateTask, writerTask);

        if (duplicateResult.Outcome != Scalar8Scalar8EncodedInsertOutcome.Inserted ||
            duplicateResult.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.NarrowTopologyPublisher)
        {
            throw new InvalidDataException($"SS8-8 queued duplicate-run fallback did not use serialized fallback. outcome={duplicateResult.Outcome} kind={duplicateResult.StructuralKind} path={duplicateResult.QueuedInsertPath}.");
        }

        if (writerResult.Outcome != Scalar8Scalar8EncodedInsertOutcome.Inserted ||
            writerResult.StructuralKind != Scalar8Scalar8RoutedInsertKind.WalkedNoSplit ||
            writerResult.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext ||
            writerResult.PrimaryOffset != writerShelf)
        {
            throw new InvalidDataException($"SS8-8 queued duplicate-run fallback overlap did not preserve writer-context insert. outcome={writerResult.Outcome} kind={writerResult.StructuralKind} path={writerResult.QueuedInsertPath} offset={writerResult.PrimaryOffset}/{writerShelf}.");
        }

        ulong[] duplicateIdentities = new ulong[profile.MaxItemCount + 1];
        Scalar8Scalar8EncodedRangeReadResult duplicateRead = index.ReadEncodedRange(duplicateKey, duplicateKey, duplicateIdentities);
        if (duplicateRead.IdentityCount != profile.MaxItemCount + 1 ||
            duplicateIdentities[profile.MaxItemCount] != duplicateIdentity)
        {
            throw new InvalidDataException("SS8-8 queued duplicate-run fallback insert was not readable after overlap.");
        }

        ulong[] writerIdentities = new ulong[4];
        Scalar8Scalar8EncodedRangeReadResult writerRead = index.ReadEncodedRange(writerPrefixBase + 1, writerKey, writerIdentities);
        if (writerRead.IdentityCount != 2 ||
            writerIdentities[0] != 8801 ||
            writerIdentities[1] != 8802)
        {
            throw new InvalidDataException("SS8-8 queued duplicate-run fallback overlap writer-context insert was not readable.");
        }

        int expectedRangeCount = profile.MaxItemCount + 3;
        ulong[] mixedRangeIdentities = new ulong[expectedRangeCount];
        Scalar8Scalar8EncodedRangeReadResult mixedRangeRead = index.ReadEncodedRange(duplicateKey, writerKey, mixedRangeIdentities);
        if (mixedRangeRead.IdentityCount != expectedRangeCount ||
            mixedRangeIdentities[0] != 770_000 ||
            mixedRangeIdentities[profile.MaxItemCount] != duplicateIdentity ||
            mixedRangeIdentities[profile.MaxItemCount + 1] != 8801 ||
            mixedRangeIdentities[profile.MaxItemCount + 2] != 8802)
        {
            throw new InvalidDataException("SS8-8 queued duplicate-run fallback mixed terminal-root/shelf range was not readable.");
        }

        Array.Clear(mixedRangeIdentities);
        Scalar8Scalar8EncodedRangeReadResult mixedCoalescedRangeRead = index.ReadEncodedRange(duplicateKey, writerKey, mixedRangeIdentities, enableCoalescing: true);
        if (mixedCoalescedRangeRead.IdentityCount != expectedRangeCount ||
            mixedRangeIdentities[0] != 770_000 ||
            mixedRangeIdentities[profile.MaxItemCount] != duplicateIdentity ||
            mixedRangeIdentities[profile.MaxItemCount + 1] != 8801 ||
            mixedRangeIdentities[profile.MaxItemCount + 2] != 8802)
        {
            throw new InvalidDataException("SS8-8 queued duplicate-run fallback mixed terminal-root/shelf coalesced range was not readable.");
        }

        ulong terminalAppendIdentity = duplicateIdentity + 1;
        Scalar8Scalar8EncodedInsertResult terminalAppendResult = queuedWriter.InsertEncoded(
            duplicateKey,
            terminalAppendIdentity,
            allowDuplicateKeys: true);
        if (terminalAppendResult.Outcome != Scalar8Scalar8EncodedInsertOutcome.Inserted ||
            terminalAppendResult.StructuralKind != Scalar8Scalar8RoutedInsertKind.WalkedNoSplit ||
            terminalAppendResult.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext)
        {
            throw new InvalidDataException($"SS8-8 queued terminal-root append did not use writer-context staging. outcome={terminalAppendResult.Outcome} kind={terminalAppendResult.StructuralKind} path={terminalAppendResult.QueuedInsertPath}.");
        }

        ulong[] terminalAppendIdentities = new ulong[profile.MaxItemCount + 2];
        Scalar8Scalar8EncodedRangeReadResult terminalAppendRead = index.ReadEncodedRange(duplicateKey, duplicateKey, terminalAppendIdentities);
        if (terminalAppendRead.IdentityCount != profile.MaxItemCount + 2 ||
            terminalAppendIdentities[profile.MaxItemCount] != duplicateIdentity ||
            terminalAppendIdentities[profile.MaxItemCount + 1] != terminalAppendIdentity)
        {
            throw new InvalidDataException("SS8-8 queued terminal-root writer-context append was not readable.");
        }

        ulong terminalMiddleIdentity = 769_999UL;
        Scalar8Scalar8EncodedInsertResult terminalMiddleResult = queuedWriter.InsertEncoded(
            duplicateKey,
            terminalMiddleIdentity,
            allowDuplicateKeys: true);
        if (terminalMiddleResult.Outcome != Scalar8Scalar8EncodedInsertOutcome.Inserted ||
            terminalMiddleResult.StructuralKind != Scalar8Scalar8RoutedInsertKind.WalkedNoSplit ||
            terminalMiddleResult.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext)
        {
            throw new InvalidDataException($"SS8-8 queued terminal-root sorted insert did not use writer-context staging. outcome={terminalMiddleResult.Outcome} kind={terminalMiddleResult.StructuralKind} path={terminalMiddleResult.QueuedInsertPath}.");
        }

        ulong[] terminalMiddleIdentities = new ulong[profile.MaxItemCount + 3];
        Scalar8Scalar8EncodedRangeReadResult terminalMiddleRead = index.ReadEncodedRange(duplicateKey, duplicateKey, terminalMiddleIdentities);
        if (terminalMiddleRead.IdentityCount != profile.MaxItemCount + 3 ||
            terminalMiddleIdentities[0] != terminalMiddleIdentity ||
            terminalMiddleIdentities[profile.MaxItemCount + 1] != duplicateIdentity ||
            terminalMiddleIdentities[profile.MaxItemCount + 2] != terminalAppendIdentity)
        {
            throw new InvalidDataException("SS8-8 queued terminal-root writer-context sorted insert was not readable.");
        }
    }

    /// <summary>
    /// Proves that a single-shelf `SS8-8` duplicate-run route with spare capacity can use writer-context staging instead of serialized fallback.<br/>
    /// The fixture crafts the duplicate-run shelf through internal session staging because current same-key overflow normally converts to terminal identity roots, while older or repaired catalogs may still contain duplicate-run shelves.<br/>
    /// </summary>
    private static void RunInternalScalar8Scalar8QueuedSingleShelfDuplicateRunWriterContextProbe()
    {
        using Scalar8Scalar8Index index = Indexes.SS88.Create(
            backingKind: DataKernelBackingKind.Memory,
            name: "queued-single-shelf-duplicate-run-ss88",
            options: CreateDesignPerfOptions(),
            developerMetadata: CreateDesignPerfMetadata(9912),
            telemetryOptions: DataKernelTelemetryOptions.FromLevel(LibraDexDiagnosticsLevel.Detailed));

        Scalar8Scalar8Profile profile = index.Handle.Profile;
        const ulong duplicateKey = 0x4400_0000_0000_0001UL;
        const ulong firstIdentity = 4401;
        const ulong secondIdentity = 4402;
        _ = index.InsertEncoded(duplicateKey, firstIdentity, allowDuplicateKeys: true);

        long shelfOffset = index.Session.FindRouterTarget(index.Handle.RootRouterOffset, 0x44);
        if (shelfOffset == 0)
        {
            throw new InvalidDataException("SS8-8 single-shelf duplicate-run fixture did not create the route shelf.");
        }

        byte[] duplicateRunBytes = new byte[profile.ShelfExtentSize];
        Scalar8Scalar8Layout.WriteMagic(duplicateRunBytes, Scalar8Scalar8Layout.Magic);
        Scalar8Scalar8Layout.WriteFormatVersion(duplicateRunBytes, Scalar8Scalar8Layout.FormatVersion);
        Scalar8Scalar8Layout.WriteHeaderSize(duplicateRunBytes, Scalar8Scalar8Layout.HeaderSize);
        Scalar8Scalar8Layout.WriteFlags(duplicateRunBytes, Scalar8Scalar8Layout.DuplicateRunFlag);
        Scalar8Scalar8Layout.WriteItemCount(duplicateRunBytes, 1);
        Scalar8Scalar8Layout.WriteDuplicateRunKey(duplicateRunBytes, duplicateKey);
        Scalar8Scalar8Layout.WriteDuplicateRunNextOffset(duplicateRunBytes, 0);
        Scalar8Scalar8Layout.WriteDuplicateRunIdentity(duplicateRunBytes, 0, firstIdentity);

        LibraDexWriteContext fixtureContext = index.Session.BeginScalar8Scalar8WriteContext(index.Handle.RootRouterOffset);
        index.Session.StageScalar8Scalar8ShelfRewriteForWriteContext(fixtureContext, shelfOffset, profile, duplicateRunBytes);
        _ = index.Session.PublishScalar8Scalar8WriteContext(fixtureContext);

        Scalar8Scalar8QueuedWriter queuedWriter = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);
        Scalar8Scalar8EncodedInsertResult result = queuedWriter.InsertEncoded(duplicateKey, secondIdentity, allowDuplicateKeys: true);
        if (result.Outcome != Scalar8Scalar8EncodedInsertOutcome.Inserted ||
            result.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext ||
            result.PrimaryOffset != shelfOffset)
        {
            throw new InvalidDataException($"SS8-8 single-shelf duplicate-run queued insert did not use writer-context path. outcome={result.Outcome} path={result.QueuedInsertPath} offset={result.PrimaryOffset}/{shelfOffset}.");
        }

        ulong[] identities = new ulong[4];
        Scalar8Scalar8EncodedRangeReadResult read = index.ReadEncodedRange(duplicateKey, duplicateKey, identities);
        if (read.IdentityCount != 2 ||
            identities[0] != firstIdentity ||
            identities[1] != secondIdentity)
        {
            throw new InvalidDataException($"SS8-8 single-shelf duplicate-run queued writer-context insert was not readable. count={read.IdentityCount} ids={identities[0]},{identities[1]}.");
        }
    }

    /// <summary>
    /// Proves that two `SS8-8` writer facade instances can stage different shelves from overlapping task execution and publish through the serialized seam.<br/>
    /// The probe uses a coordinated start gate so the harness is checking the writer-context boundary instead of relying on incidental task timing.<br/>
    /// </summary>
    /// <param name="index">The index shared by the overlapping writer facade instances.<br/></param>
    /// <param name="keyA">The first writer's encoded key.<br/></param>
    /// <param name="identityA">The first writer's encoded identity.<br/></param>
    /// <param name="expectedShelfA">The first writer's expected shelf offset.<br/></param>
    /// <param name="keyB">The second writer's encoded key.<br/></param>
    /// <param name="identityB">The second writer's encoded identity.<br/></param>
    /// <param name="expectedShelfB">The second writer's expected shelf offset.<br/></param>
    private static void RunOverlappingScalar8Scalar8WriterFacadeProbe(
        Scalar8Scalar8Index index,
        ulong keyA,
        ulong identityA,
        long expectedShelfA,
        ulong keyB,
        ulong identityB,
        long expectedShelfB)
    {
        using ManualResetEventSlim startGate = new(false);
        using CountdownEvent readyGate = new(2);
        Scalar8Scalar8EncodedInsertResult resultA = default;
        Scalar8Scalar8EncodedInsertResult resultB = default;

        Task taskA = Task.Run(() =>
        {
            using Scalar8Scalar8Writer writer = index.BeginWriter();
            readyGate.Signal();
            startGate.Wait();
            resultA = writer.InsertEncoded(keyA, identityA, allowDuplicateKeys: true);
            _ = writer.Publish();
        });

        Task taskB = Task.Run(() =>
        {
            using Scalar8Scalar8Writer writer = index.BeginWriter();
            readyGate.Signal();
            startGate.Wait();
            resultB = writer.InsertEncoded(keyB, identityB, allowDuplicateKeys: true);
            _ = writer.Publish();
        });

        if (!readyGate.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidDataException("SS8-8 overlapping writer facade probe could not ready both writers.");
        }

        startGate.Set();
        Task.WaitAll(taskA, taskB);

        if (resultA.Outcome != Scalar8Scalar8EncodedInsertOutcome.Inserted ||
            resultB.Outcome != Scalar8Scalar8EncodedInsertOutcome.Inserted ||
            resultA.StructuralKind != Scalar8Scalar8RoutedInsertKind.WalkedNoSplit ||
            resultB.StructuralKind != Scalar8Scalar8RoutedInsertKind.WalkedNoSplit ||
            resultA.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.None ||
            resultB.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.None ||
            resultA.PrimaryOffset != expectedShelfA ||
            resultB.PrimaryOffset != expectedShelfB)
        {
            throw new InvalidDataException("SS8-8 overlapping writer facade probe did not route both writers to the expected no-split shelves.");
        }
    }

    /// <summary>
    /// Proves that the queued `SS8-8` writer facade accepts overlapping callers without exposing writer-context ownership conflicts.<br/>
    /// Same-shelf inserts wait and retry internally, while different-shelf inserts can stage through independent writer contexts before serialized publication.<br/>
    /// </summary>
    /// <param name="queuedWriter">The queued writer shared by the overlapping callers.<br/></param>
    /// <param name="keyA">The first caller's encoded key.<br/></param>
    /// <param name="identityA">The first caller's encoded identity.<br/></param>
    /// <param name="expectedShelfA">The first caller's expected shelf offset.<br/></param>
    /// <param name="keyB">The second caller's encoded key.<br/></param>
    /// <param name="identityB">The second caller's encoded identity.<br/></param>
    /// <param name="expectedShelfB">The second caller's expected shelf offset.<br/></param>
    private static void RunOverlappingScalar8Scalar8QueuedWriterProbe(
        Scalar8Scalar8QueuedWriter queuedWriter,
        ulong keyA,
        ulong identityA,
        long expectedShelfA,
        ulong keyB,
        ulong identityB,
        long expectedShelfB)
    {
        using ManualResetEventSlim startGate = new(false);
        using CountdownEvent readyGate = new(2);
        Scalar8Scalar8EncodedInsertResult resultA = default;
        Scalar8Scalar8EncodedInsertResult resultB = default;

        Task taskA = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            resultA = queuedWriter.InsertEncoded(keyA, identityA, allowDuplicateKeys: true);
        });

        Task taskB = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            resultB = queuedWriter.InsertEncoded(keyB, identityB, allowDuplicateKeys: true);
        });

        if (!readyGate.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidDataException("SS8-8 overlapping queued writer probe could not ready both callers.");
        }

        startGate.Set();
        Task.WaitAll(taskA, taskB);

        if (resultA.Outcome != Scalar8Scalar8EncodedInsertOutcome.Inserted ||
            resultB.Outcome != Scalar8Scalar8EncodedInsertOutcome.Inserted ||
            resultA.StructuralKind != Scalar8Scalar8RoutedInsertKind.WalkedNoSplit ||
            resultB.StructuralKind != Scalar8Scalar8RoutedInsertKind.WalkedNoSplit ||
            resultA.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext ||
            resultB.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext ||
            resultA.PrimaryOffset != expectedShelfA ||
            resultB.PrimaryOffset != expectedShelfB)
        {
            throw new InvalidDataException(
                "SS8-8 overlapping queued writer probe did not route both inserts through the expected route shape. " +
                $"A outcome={resultA.Outcome} kind={resultA.StructuralKind} path={resultA.QueuedInsertPath} offset={resultA.PrimaryOffset}/{expectedShelfA}; " +
                $"B outcome={resultB.Outcome} kind={resultB.StructuralKind} path={resultB.QueuedInsertPath} offset={resultB.PrimaryOffset}/{expectedShelfB}.");
        }
    }

    /// <summary>
    /// Proves that queued topology fallback does not race writer-context staging for another existing shelf.<br/>
    /// The fallback key uses an unset root prefix so it must take the serialized fallback path, while the existing-shelf key should still publish through the writer-context path.<br/>
    /// </summary>
    /// <param name="queuedWriter">The queued writer shared by both overlapping callers.<br/></param>
    /// <param name="fallbackKey">The encoded key that should require serialized fallback topology work.<br/></param>
    /// <param name="fallbackIdentity">The identity written by the fallback caller.<br/></param>
    /// <param name="writerKey">The encoded key that should use writer-context staging on an existing shelf.<br/></param>
    /// <param name="writerIdentity">The identity written by the writer-context caller.<br/></param>
    /// <param name="expectedWriterShelf">The expected existing shelf offset for the writer-context caller.<br/></param>
    private static void RunOverlappingScalar8Scalar8QueuedFallbackAndWriterContextProbe(
        Scalar8Scalar8QueuedWriter queuedWriter,
        ulong fallbackKey,
        ulong fallbackIdentity,
        ulong writerKey,
        ulong writerIdentity,
        long expectedWriterShelf)
    {
        using ManualResetEventSlim startGate = new(false);
        using CountdownEvent readyGate = new(2);
        Scalar8Scalar8EncodedInsertResult fallbackResult = default;
        Scalar8Scalar8EncodedInsertResult writerResult = default;

        Task fallbackTask = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            fallbackResult = queuedWriter.InsertEncoded(fallbackKey, fallbackIdentity, allowDuplicateKeys: true);
        });

        Task writerTask = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            writerResult = queuedWriter.InsertEncoded(writerKey, writerIdentity, allowDuplicateKeys: true);
        });

        if (!readyGate.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidDataException("SS8-8 queued fallback/writer-context probe could not ready both callers.");
        }

        startGate.Set();
        Task.WaitAll(fallbackTask, writerTask);

        if (fallbackResult.Outcome != Scalar8Scalar8EncodedInsertOutcome.Inserted ||
            fallbackResult.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.NarrowTopologyPublisher ||
            !fallbackResult.CreatedInitialShelfRoute)
        {
            throw new InvalidDataException($"SS8-8 queued fallback/topology probe did not use serialized fallback. outcome={fallbackResult.Outcome} path={fallbackResult.QueuedInsertPath} createdRoute={fallbackResult.CreatedInitialShelfRoute}.");
        }

        if (writerResult.Outcome != Scalar8Scalar8EncodedInsertOutcome.Inserted ||
            writerResult.StructuralKind != Scalar8Scalar8RoutedInsertKind.WalkedNoSplit ||
            writerResult.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext ||
            writerResult.PrimaryOffset != expectedWriterShelf)
        {
            throw new InvalidDataException($"SS8-8 queued fallback/topology probe did not preserve writer-context staging. outcome={writerResult.Outcome} kind={writerResult.StructuralKind} path={writerResult.QueuedInsertPath} offset={writerResult.PrimaryOffset}/{expectedWriterShelf}.");
        }
    }

    /// <summary>
    /// Runs two generic queued insert delegates behind a coordinated start gate so the workload matrix measures overlap admission rather than incidental sequential timing.<br/>
    /// The delegates are intentionally caller-facing and return generic insert results so the same helper can cover same-index, same-shelf, and different-index Abraxas-shaped submissions.<br/>
    /// </summary>
    /// <param name="first">The first queued insert delegate.<br/></param>
    /// <param name="second">The second queued insert delegate.<br/></param>
    /// <param name="description">Short diagnostic description included in readiness failures.<br/></param>
    /// <returns>The two insert results in delegate order.<br/></returns>
    private static (LibraDexGenericInsertResult First, LibraDexGenericInsertResult Second) RunOverlappingGenericQueuedInserts(
        Func<LibraDexGenericInsertResult> first,
        Func<LibraDexGenericInsertResult> second,
        string description)
    {
        using ManualResetEventSlim startGate = new(false);
        using CountdownEvent readyGate = new(2);
        LibraDexGenericInsertResult firstResult = default;
        LibraDexGenericInsertResult secondResult = default;

        Task firstTask = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            firstResult = first();
        });

        Task secondTask = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            secondResult = second();
        });

        if (!readyGate.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidDataException($"Concurrency workload matrix {description} could not ready both callers.");
        }

        startGate.Set();
        Task.WaitAll(firstTask, secondTask);
        return (firstResult, secondResult);
    }

    /// <summary>
    /// Runs two generic queued delete delegates behind a coordinated start gate so mutation matrix rows prove overlap admission for exact tuple removal.<br/>
    /// The helper mirrors queued insert coordination while preserving delete-specific result attribution.<br/>
    /// </summary>
    /// <param name="first">The first queued delete delegate.<br/></param>
    /// <param name="second">The second queued delete delegate.<br/></param>
    /// <param name="description">Short diagnostic description included in readiness failures.<br/></param>
    /// <returns>The two delete results in delegate order.<br/></returns>
    private static (LibraDexGenericDeleteResult First, LibraDexGenericDeleteResult Second) RunOverlappingGenericQueuedDeletes(
        Func<LibraDexGenericDeleteResult> first,
        Func<LibraDexGenericDeleteResult> second,
        string description)
    {
        using ManualResetEventSlim startGate = new(false);
        using CountdownEvent readyGate = new(2);
        LibraDexGenericDeleteResult firstResult = default;
        LibraDexGenericDeleteResult secondResult = default;

        Task firstTask = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            firstResult = first();
        });

        Task secondTask = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            secondResult = second();
        });

        if (!readyGate.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidDataException($"Concurrency workload matrix {description} could not ready both callers.");
        }

        startGate.Set();
        Task.WaitAll(firstTask, secondTask);
        return (firstResult, secondResult);
    }

    /// <summary>
    /// Runs two generic queued rekey delegates behind a coordinated start gate so mutation matrix rows prove overlap admission for key-changing patches.<br/>
    /// Each rekey is counted later as two physical legs: replacement insert plus old tuple delete.<br/>
    /// </summary>
    /// <param name="first">The first queued rekey delegate.<br/></param>
    /// <param name="second">The second queued rekey delegate.<br/></param>
    /// <param name="description">Short diagnostic description included in readiness failures.<br/></param>
    /// <returns>The two rekey results in delegate order.<br/></returns>
    private static (LibraDexGenericRekeyResult First, LibraDexGenericRekeyResult Second) RunOverlappingGenericQueuedRekeys(
        Func<LibraDexGenericRekeyResult> first,
        Func<LibraDexGenericRekeyResult> second,
        string description)
    {
        using ManualResetEventSlim startGate = new(false);
        using CountdownEvent readyGate = new(2);
        LibraDexGenericRekeyResult firstResult = default;
        LibraDexGenericRekeyResult secondResult = default;

        Task firstTask = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            firstResult = first();
        });

        Task secondTask = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            secondResult = second();
        });

        if (!readyGate.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidDataException($"Concurrency workload matrix {description} could not ready both callers.");
        }

        startGate.Set();
        Task.WaitAll(firstTask, secondTask);
        return (firstResult, secondResult);
    }

    /// <summary>
    /// Builds one workload-matrix row from generic queued-writer insert results.<br/>
    /// Counts are intentionally path-attribution counts, not timing measurements, so row output stays stable across machines.<br/>
    /// </summary>
    /// <param name="name">The stable row name printed by the harness.<br/></param>
    /// <param name="note">Short integration-facing interpretation of the row.<br/></param>
    /// <param name="results">The generic insert results to count.<br/></param>
    /// <returns>A compact matrix row with operations, path counts, and changed count.<br/></returns>
    private static ConcurrencyWorkloadMatrixRow BuildGenericMatrixRow(
        string name,
        string note,
        params LibraDexGenericInsertResult[] results)
    {
        int writerContext = 0;
        int narrowTopology = 0;
        int serializedFallback = 0;
        int changed = 0;
        for (int i = 0; i < results.Length; i++)
        {
            LibraDexGenericInsertResult result = results[i];
            if (result.Inserted)
            {
                changed++;
            }

            CountQueuedInsertPath(result.QueuedInsertPath, ref writerContext, ref narrowTopology, ref serializedFallback);
        }

        return new ConcurrencyWorkloadMatrixRow(name, results.Length, writerContext, narrowTopology, serializedFallback, changed, note);
    }

    /// <summary>
    /// Builds one workload-matrix row from generic queued-writer delete results.<br/>
    /// Delete rows count exact tuple removals and their path attribution, matching insert rows without overloading database transaction semantics.<br/>
    /// </summary>
    /// <param name="name">The stable row name printed by the harness.<br/></param>
    /// <param name="note">Short integration-facing interpretation of the row.<br/></param>
    /// <param name="results">The generic delete results to count.<br/></param>
    /// <returns>A compact matrix row with operations, path counts, and changed count.<br/></returns>
    private static ConcurrencyWorkloadMatrixRow BuildGenericDeleteMatrixRow(
        string name,
        string note,
        params LibraDexGenericDeleteResult[] results)
    {
        int writerContext = 0;
        int narrowTopology = 0;
        int serializedFallback = 0;
        int changed = 0;
        for (int i = 0; i < results.Length; i++)
        {
            LibraDexGenericDeleteResult result = results[i];
            if (result.Deleted)
            {
                changed++;
            }

            CountQueuedInsertPath(result.QueuedInsertPath, ref writerContext, ref narrowTopology, ref serializedFallback);
        }

        return new ConcurrencyWorkloadMatrixRow(name, results.Length, writerContext, narrowTopology, serializedFallback, changed, note);
    }

    /// <summary>
    /// Builds one workload-matrix row from generic queued-writer rekey results.<br/>
    /// Rekey rows count replacement and removal legs separately because each leg can have independent writer-context or serialized-fallback attribution.<br/>
    /// </summary>
    /// <param name="name">The stable row name printed by the harness.<br/></param>
    /// <param name="note">Short integration-facing interpretation of the row.<br/></param>
    /// <param name="results">The generic rekey results to count.<br/></param>
    /// <returns>A compact matrix row with physical legs, path counts, and changed leg count.<br/></returns>
    private static ConcurrencyWorkloadMatrixRow BuildGenericRekeyMatrixRow(
        string name,
        string note,
        params LibraDexGenericRekeyResult[] results)
    {
        int writerContext = 0;
        int narrowTopology = 0;
        int serializedFallback = 0;
        int changed = 0;
        for (int i = 0; i < results.Length; i++)
        {
            LibraDexGenericRekeyResult result = results[i];
            if (result.Replacement.Inserted)
            {
                changed++;
            }

            if (result.Removal.Deleted)
            {
                changed++;
            }

            CountQueuedInsertPath(result.Replacement.QueuedInsertPath, ref writerContext, ref narrowTopology, ref serializedFallback);
            CountQueuedInsertPath(result.Removal.QueuedInsertPath, ref writerContext, ref narrowTopology, ref serializedFallback);
        }

        return new ConcurrencyWorkloadMatrixRow(name, results.Length * 2, writerContext, narrowTopology, serializedFallback, changed, note);
    }

    /// <summary>
    /// Builds one workload-matrix row from encoded `SS8-8` queued-writer insert results.<br/>
    /// Encoded rows are used only for deterministic topology-pressure fixtures that generic typed APIs do not expose directly.<br/>
    /// </summary>
    /// <param name="name">The stable row name printed by the harness.<br/></param>
    /// <param name="note">Short integration-facing interpretation of the row.<br/></param>
    /// <param name="results">The encoded insert results to count.<br/></param>
    /// <returns>A compact matrix row with operations, path counts, and changed count.<br/></returns>
    private static ConcurrencyWorkloadMatrixRow BuildEncodedMatrixRow(
        string name,
        string note,
        params Scalar8Scalar8EncodedInsertResult[] results)
    {
        int writerContext = 0;
        int narrowTopology = 0;
        int serializedFallback = 0;
        int changed = 0;
        for (int i = 0; i < results.Length; i++)
        {
            Scalar8Scalar8EncodedInsertResult result = results[i];
            if (result.Outcome == Scalar8Scalar8EncodedInsertOutcome.Inserted)
            {
                changed++;
            }

            CountQueuedInsertPath(result.QueuedInsertPath, ref writerContext, ref narrowTopology, ref serializedFallback);
        }

        return new ConcurrencyWorkloadMatrixRow(name, results.Length, writerContext, narrowTopology, serializedFallback, changed, note);
    }

    /// <summary>
    /// Increments the path counter represented by one queued-writer path attribution value.<br/>
    /// `None` is ignored because fixture setup inserts and non-queued paths are not the subject of the workload matrix.<br/>
    /// </summary>
    /// <param name="path">The queued-writer path attribution to count.<br/></param>
    /// <param name="writerContext">The writer-context counter to increment when applicable.<br/></param>
    /// <param name="serializedFallback">The serialized-fallback counter to increment when applicable.<br/></param>
    private static void CountQueuedInsertPath(
        Scalar8Scalar8QueuedInsertPath path,
        ref int writerContext,
        ref int narrowTopology,
        ref int serializedFallback)
    {
        if (path == Scalar8Scalar8QueuedInsertPath.WriterContext)
        {
            writerContext++;
            return;
        }

        if (path == Scalar8Scalar8QueuedInsertPath.NarrowTopologyPublisher)
        {
            narrowTopology++;
            return;
        }

        if (path == Scalar8Scalar8QueuedInsertPath.SerializedFallback)
        {
            serializedFallback++;
        }
    }

    /// <summary>
    /// Validates one workload-matrix row against expected path attribution.<br/>
    /// This keeps the command useful as a regression check rather than a non-binding report generator.<br/>
    /// </summary>
    /// <param name="row">The matrix row to validate.<br/></param>
    /// <param name="writerContext">The expected writer-context count.<br/></param>
    /// <param name="serializedFallback">The expected serialized-fallback count.<br/></param>
    private static void RequireMatrixPath(
        ConcurrencyWorkloadMatrixRow row,
        int writerContext,
        int serializedFallback,
        int narrowTopology = 0)
    {
        if (row.WriterContext != writerContext ||
            row.NarrowTopology != narrowTopology ||
            row.SerializedFallback != serializedFallback ||
            row.Changed != row.Operations)
        {
            throw new InvalidDataException(
                $"Concurrency workload matrix row {row.Name} expected writer={writerContext} narrow={narrowTopology} serialized={serializedFallback} changed={row.Operations}, " +
                $"but saw writer={row.WriterContext} narrow={row.NarrowTopology} serialized={row.SerializedFallback} changed={row.Changed}.");
        }
    }

    /// <summary>
    /// Holds one deterministic concurrency workload matrix row.<br/>
    /// The row is deliberately compact because the command is intended for quick Abraxas integration checks and checklist evidence.<br/>
    /// </summary>
    /// <param name="Name">Stable scenario name.<br/></param>
    /// <param name="Operations">Number of measured queued-writer operations in the row.<br/></param>
    /// <param name="WriterContext">Number of measured operations that used writer-context staging.<br/></param>
    /// <param name="NarrowTopology">Number of measured operations that used a narrowed topology publisher.<br/></param>
    /// <param name="SerializedFallback">Number of measured operations that used serialized fallback.<br/></param>
    /// <param name="Changed">Number of measured operations that changed a tuple or topology-backed tuple placement.<br/></param>
    /// <param name="Note">Short interpretation note for the row.<br/></param>
    private readonly record struct ConcurrencyWorkloadMatrixRow(
        string Name,
        int Operations,
        int WriterContext,
        int NarrowTopology,
        int SerializedFallback,
        int Changed,
        string Note);

    /// <summary>
    /// Proves the typed unsigned `SS8-8` queued writer facade keeps overlapping callers on typed values while preserving encoded queued-writer attribution.<br/>
    /// This is the first Abraxas-friendly wrapper shape over the queued writer because callers do not have to pass encoded keys or identities.<br/>
    /// </summary>
    private static void RunInternalUnsignedScalar8Scalar8QueuedWriterProbe()
    {
        using UnsignedScalar8Scalar8Index index = Indexes.SS88.Unsigned.Create(
            backingKind: DataKernelBackingKind.Memory,
            name: "typed-queued-writer-ss88",
            options: CreateDesignPerfOptions(),
            developerMetadata: CreateDesignPerfMetadata(9903),
            telemetryOptions: DataKernelTelemetryOptions.FromLevel(LibraDexDiagnosticsLevel.Detailed));

        _ = index.Insert(100, 1000);
        UnsignedScalar8Scalar8QueuedWriter queuedWriter = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);
        using ManualResetEventSlim startGate = new(false);
        using CountdownEvent readyGate = new(2);
        UnsignedScalar8Scalar8InsertResult resultA = default;
        UnsignedScalar8Scalar8InsertResult resultB = default;

        Task taskA = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            resultA = queuedWriter.Insert(101, 1001);
        });

        Task taskB = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            resultB = queuedWriter.Insert(102, 1002);
        });

        if (!readyGate.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidDataException("Typed unsigned SS8-8 queued writer probe could not ready both callers.");
        }

        startGate.Set();
        Task.WaitAll(taskA, taskB);

        if (resultA.Outcome != UnsignedScalar8Scalar8InsertOutcome.Inserted ||
            resultB.Outcome != UnsignedScalar8Scalar8InsertOutcome.Inserted ||
            resultA.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext ||
            resultB.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext)
        {
            throw new InvalidDataException("Typed unsigned SS8-8 queued writer did not preserve writer-context attribution for overlapping typed inserts.");
        }

        ulong fallbackKey = 0x3300_0000_0000_0001UL;
        UnsignedScalar8Scalar8InsertResult fallback = queuedWriter.Insert(fallbackKey, 2000);
        if (fallback.Outcome != UnsignedScalar8Scalar8InsertOutcome.Inserted ||
            !fallback.CreatedInitialShelfRoute ||
            fallback.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.NarrowTopologyPublisher)
        {
            throw new InvalidDataException("Typed unsigned SS8-8 queued writer did not preserve serialized fallback attribution.");
        }

        ulong[] identities = new ulong[4];
        UnsignedScalar8Scalar8RangeReadResult read = index.ReadRange(100, 102, identities);
        if (read.IdentityCount != 3 || identities[0] != 1000 || identities[1] != 1001 || identities[2] != 1002)
        {
            throw new InvalidDataException("Typed unsigned SS8-8 queued writer did not publish typed same-prefix inserts.");
        }

        Array.Clear(identities);
        read = index.ReadRange(fallbackKey, fallbackKey, identities);
        if (read.IdentityCount != 1 || identities[0] != 2000)
        {
            throw new InvalidDataException("Typed unsigned SS8-8 queued writer fallback insert was not readable.");
        }
    }

    /// <summary>
    /// Proves the generic catalog-style `SS8-8` queued writer path keeps overlapping callers on typed generic values and preserves queued path attribution.<br/>
    /// This is the closest current LibraDex-side bridge for Abraxas integration because it operates on `LibraDexIndex&lt;TKey, TIdentity&gt;` rather than encoded factory wrappers.<br/>
    /// </summary>
    private static void RunInternalGenericScalar8Scalar8QueuedWriterProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });
        using LibraDexIndex<long, long> index = catalog.Indexes["generic-queued"]["ids"].Int64Keys<long>().Create();
        _ = index.Insert(100, 1001);
        NotSupportedException? singleOwnerModeException = null;
        try
        {
            _ = index.BeginQueuedWriter(new LibraDexConcurrencyOptions { Mode = LibraDexConcurrencyMode.SingleOwner });
        }
        catch (NotSupportedException ex)
        {
            singleOwnerModeException = ex;
        }

        if (singleOwnerModeException is null ||
            !singleOwnerModeException.Message.Contains(nameof(LibraDexConcurrencyMode.QueuedWriter), StringComparison.Ordinal))
        {
            throw new InvalidDataException("Generic SS8-8 queued writer did not reject non-queued concurrency mode.");
        }

        LibraDexQueuedWriter<long, long> queuedWriter = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);
        using ManualResetEventSlim startGate = new(false);
        using CountdownEvent readyGate = new(2);
        LibraDexGenericInsertResult resultA = default;
        LibraDexGenericInsertResult resultB = default;

        Task taskA = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            resultA = queuedWriter.Insert(101, 1002);
        });

        Task taskB = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            resultB = queuedWriter.Insert(102, 1003);
        });

        if (!readyGate.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidDataException("Generic SS8-8 queued writer probe could not ready both callers.");
        }

        startGate.Set();
        Task.WaitAll(taskA, taskB);

        if (!resultA.Inserted ||
            !resultB.Inserted ||
            resultA.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext ||
            resultB.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext)
        {
            throw new InvalidDataException("Generic SS8-8 queued writer did not preserve writer-context attribution for overlapping inserts.");
        }

        LibraDexGenericInsertResult fallback = queuedWriter.Insert(long.MinValue + 33, 3001);
        if (!fallback.Inserted ||
            !fallback.CreatedInitialShelfRoute ||
            fallback.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.NarrowTopologyPublisher)
        {
            throw new InvalidDataException("Generic SS8-8 queued writer did not preserve serialized fallback attribution.");
        }

        long[] identities = new long[8];
        LibraDexGenericRangeReadResult read = index.ReadRange(100, 102, identities);
        if (read.IdentityCount != 3 || identities[0] != 1001 || identities[1] != 1002 || identities[2] != 1003)
        {
            throw new InvalidDataException("Generic SS8-8 queued writer did not publish typed same-prefix inserts.");
        }

        Array.Clear(identities);
        read = index.ReadRange(long.MinValue + 33, long.MinValue + 33, identities);
        if (read.IdentityCount != 1 || identities[0] != 3001)
        {
            throw new InvalidDataException("Generic SS8-8 queued writer fallback insert was not readable.");
        }
    }

    /// <summary>
    /// Proves generic `SS8-8` queued exact deletes use writer-context staging for shelf-local deletes and queue same-shelf delete contention internally.<br/>
    /// This is the first patch/delete concurrency bridge for Abraxas-shaped exact tuple removal.<br/>
    /// </summary>
    private static void RunInternalGenericScalar8Scalar8QueuedExactDeleteProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        using LibraDexIndex<long, long> index = catalog.Indexes["generic-queued-delete"]["ids"].Int64Keys<long>().Create();
        _ = index.Insert(100, 1001);
        _ = index.Insert(101, 1002);
        _ = index.Insert(200, 2001);
        _ = index.Insert(201, 2002);

        LibraDexQueuedWriter<long, long> queuedWriter = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);
        using ManualResetEventSlim startGate = new(false);
        using CountdownEvent readyGate = new(2);
        LibraDexGenericDeleteResult deleteA = default;
        LibraDexGenericDeleteResult deleteB = default;

        Task taskA = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            deleteA = queuedWriter.Delete(100, 1001);
        });

        Task taskB = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            deleteB = queuedWriter.Delete(101, 1002);
        });

        if (!readyGate.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidDataException("Generic SS8-8 queued exact delete probe could not ready both callers.");
        }

        startGate.Set();
        Task.WaitAll(taskA, taskB);

        if (!deleteA.Deleted ||
            !deleteB.Deleted ||
            deleteA.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext ||
            deleteB.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext)
        {
            throw new InvalidDataException($"Generic SS8-8 queued exact delete expected writer-context paths but saw {deleteA.Deleted}/{deleteA.QueuedInsertPath} and {deleteB.Deleted}/{deleteB.QueuedInsertPath}.");
        }

        LibraDexGenericDeleteResult missing = queuedWriter.Delete(100, 1001);
        if (missing.Deleted ||
            missing.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.None)
        {
            throw new InvalidDataException("Generic SS8-8 queued exact delete expected a missing tuple to be a no-op.");
        }

        long[] identities = new long[4];
        LibraDexGenericRangeReadResult deletedRead = index.ReadRange(100, 101, identities);
        if (deletedRead.IdentityCount != 0)
        {
            throw new InvalidDataException("Generic SS8-8 queued exact delete did not remove same-shelf tuples.");
        }

        LibraDexGenericRangeReadResult survivorRead = index.ReadRange(200, 201, identities);
        if (survivorRead.IdentityCount != 2 ||
            identities[0] != 2001 ||
            identities[1] != 2002)
        {
            throw new InvalidDataException("Generic SS8-8 queued exact delete disturbed unrelated shelf tuples.");
        }
    }

    /// <summary>
    /// Proves generic `SS8-8` queued exact delete uses writer-context staging for terminal identity roots when the mutation rewrites one owned terminal shelf.<br/>
    /// Terminal deletes that would require route cleanup or chain relink remain outside this bounded shelf-local proof.<br/>
    /// </summary>
    private static void RunInternalGenericScalar8Scalar8QueuedTerminalDeleteFallbackProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        using LibraDexIndex<long, long> index = catalog.Indexes["generic-queued-terminal-delete"]["ids"].Int64Keys<long>().Create();
        Scalar8Scalar8Profile profile = index.GetScalar8Scalar8Profile();
        const long duplicateKey = 700;
        for (int i = 0; i < profile.MaxItemCount + 1; i++)
        {
            _ = index.Insert(duplicateKey, 700_000 + i);
        }

        LibraDexQueuedWriter<long, long> queuedWriter = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);
        LibraDexGenericDeleteResult delete = queuedWriter.Delete(duplicateKey, 700_000);
        if (!delete.Deleted ||
            delete.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext)
        {
            throw new InvalidDataException($"Generic SS8-8 queued terminal delete expected writer-context staging but saw deleted={delete.Deleted} path={delete.QueuedInsertPath}.");
        }

        long[] identities = new long[profile.MaxItemCount + 1];
        LibraDexGenericRangeReadResult read = index.ReadRange(duplicateKey, duplicateKey, identities);
        if (read.IdentityCount != profile.MaxItemCount ||
            identities[0] != 700_001)
        {
            throw new InvalidDataException("Generic SS8-8 queued terminal delete did not remove the expected identity.");
        }
    }

    /// <summary>
    /// Proves generic `SS8-8` queued rekey uses the low-friction patch shape: queued insert of the replacement tuple followed by queued delete of the old tuple.<br/>
    /// The result exposes both legs so callers do not mistake this identity-index update for a rollback-capable database transaction.<br/>
    /// </summary>
    private static void RunInternalGenericScalar8Scalar8QueuedRekeyProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        using LibraDexIndex<long, long> index = catalog.Indexes["generic-queued-rekey"]["ids"].Int64Keys<long>().Create();
        _ = index.Insert(100, 1001);
        LibraDexQueuedWriter<long, long> queuedWriter = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);
        long replacementKey = long.MinValue + 200;
        LibraDexGenericRekeyResult rekey = queuedWriter.Rekey(1001, 100, replacementKey);
        if (!rekey.Changed ||
            !rekey.Replacement.Inserted ||
            !rekey.Removal.Deleted ||
            rekey.Replacement.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.NarrowTopologyPublisher ||
            rekey.Removal.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext)
        {
            throw new InvalidDataException($"Generic SS8-8 queued rekey expected replacement fallback and removal writer-context paths but saw changed={rekey.Changed} insert={rekey.Replacement.Inserted}/{rekey.Replacement.QueuedInsertPath} delete={rekey.Removal.Deleted}/{rekey.Removal.QueuedInsertPath}.");
        }

        long[] identities = new long[2];
        LibraDexGenericRangeReadResult oldRead = index.ReadRange(100, 100, identities);
        if (oldRead.IdentityCount != 0)
        {
            throw new InvalidDataException("Generic SS8-8 queued rekey did not remove the old key tuple.");
        }

        LibraDexGenericRangeReadResult newRead = index.ReadRange(replacementKey, replacementKey, identities);
        if (newRead.IdentityCount != 1 ||
            identities[0] != 1001)
        {
            throw new InvalidDataException("Generic SS8-8 queued rekey did not publish the replacement key tuple.");
        }
    }

    /// <summary>
    /// Proves ordinary generic `SS8-8` inserts can overlap without callers explicitly creating a queued writer.<br/>
    /// The index owns the queued-writer bridge internally when no batch or maintained projection requires the older durability path.<br/>
    /// </summary>
    private static void RunInternalGenericScalar8Scalar8DirectConcurrentInsertProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        using LibraDexIndex<long, long> index = catalog.Indexes["generic-direct-insert"]["ids"].Int64Keys<long>().Create();
        _ = index.Insert(100, 1001);
        using ManualResetEventSlim startGate = new(false);
        using CountdownEvent readyGate = new(2);
        LibraDexGenericInsertResult insertA = default;
        LibraDexGenericInsertResult insertB = default;

        Task taskA = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            insertA = index.Insert(101, 1002);
        });

        Task taskB = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            insertB = index.Insert(102, 1003);
        });

        if (!readyGate.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidDataException("Generic SS8-8 direct concurrent insert probe could not ready both callers.");
        }

        startGate.Set();
        Task.WaitAll(taskA, taskB);

        int directCount = 0;
        int concurrentAdmissionCount = 0;
        if (insertA.QueuedInsertPath == Scalar8Scalar8QueuedInsertPath.None)
        {
            directCount++;
        }
        else
        {
            concurrentAdmissionCount++;
        }

        if (insertB.QueuedInsertPath == Scalar8Scalar8QueuedInsertPath.None)
        {
            directCount++;
        }
        else
        {
            concurrentAdmissionCount++;
        }

        if (!insertA.Inserted ||
            !insertB.Inserted ||
            directCount != 1 ||
            concurrentAdmissionCount != 1)
        {
            throw new InvalidDataException($"Generic SS8-8 adaptive concurrent inserts expected one direct path and one concurrent admission path but saw {insertA.Inserted}/{insertA.QueuedInsertPath} and {insertB.Inserted}/{insertB.QueuedInsertPath}.");
        }

        long[] identities = new long[4];
        LibraDexGenericRangeReadResult read = index.ReadRange(100, 102, identities);
        if (read.IdentityCount != 3 ||
            identities[0] != 1001 ||
            identities[1] != 1002 ||
            identities[2] != 1003)
        {
            throw new InvalidDataException("Generic SS8-8 direct concurrent inserts were not readable.");
        }
    }

    /// <summary>
    /// Proves ordinary exact tuple deletes can overlap through the generic `IIndex` path without callers explicitly creating a queued writer.<br/>
    /// This covers the low-friction developer shape used by direct delete and by condition mutation bridges that converge on exact tuple removal.<br/>
    /// </summary>
    private static void RunInternalGenericScalar8Scalar8DirectConcurrentDeleteProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        using LibraDexIndex<long, long> index = catalog.Indexes["generic-direct-delete"]["ids"].Int64Keys<long>().Create();
        _ = index.Insert(100, 1001);
        _ = index.Insert(101, 1002);
        _ = index.Insert(102, 1003);
        IIndex runtimeIndex = index;
        using ManualResetEventSlim startGate = new(false);
        using CountdownEvent readyGate = new(2);
        bool deleteA = false;
        bool deleteB = false;

        Task taskA = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            deleteA = runtimeIndex.Delete(100L, 1001L);
        });

        Task taskB = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            deleteB = runtimeIndex.Delete(101L, 1002L);
        });

        if (!readyGate.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidDataException("Generic SS8-8 direct concurrent delete probe could not ready both callers.");
        }

        startGate.Set();
        Task.WaitAll(taskA, taskB);

        if (!deleteA || !deleteB)
        {
            throw new InvalidDataException($"Generic SS8-8 direct concurrent deletes expected both tuples to be removed but saw {deleteA}/{deleteB}.");
        }

        long[] identities = new long[4];
        LibraDexGenericRangeReadResult read = index.ReadRange(100, 102, identities);
        if (read.IdentityCount != 1 ||
            identities[0] != 1003)
        {
            throw new InvalidDataException("Generic SS8-8 direct concurrent deletes did not leave the expected surviving tuple.");
        }
    }

    /// <summary>
    /// Proves cursor-local `DeleteCurrent()` on generic `SS8-8` readers uses the owning index's queued exact-delete bridge.<br/>
    /// The cursor remains a live-session object, but the physical deletion no longer rewrites the retained shelf image directly for this shape.<br/>
    /// </summary>
    private static void RunInternalGenericScalar8Scalar8CursorConcurrentDeleteProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        using LibraDexIndex<long, long> index = catalog.Indexes["generic-cursor-delete"]["ids"].Int64Keys<long>().Create();
        _ = index.Insert(100, 1001);
        _ = index.Insert(101, 1002);
        _ = index.Insert(102, 1003);
        using ManualResetEventSlim startGate = new(false);
        using CountdownEvent readyGate = new(2);
        bool deleteA = false;
        bool deleteB = false;

        Task taskA = Task.Run(() =>
        {
            using LibraDexRangeReader<long, long> reader = index.OpenRangeReader(100, 100);
            if (!reader.MoveNext())
            {
                throw new InvalidDataException("Generic SS8-8 cursor delete probe did not position the first reader.");
            }

            readyGate.Signal();
            startGate.Wait();
            deleteA = reader.DeleteCurrent();
        });

        Task taskB = Task.Run(() =>
        {
            using LibraDexRangeReader<long, long> reader = index.OpenRangeReader(101, 101);
            if (!reader.MoveNext())
            {
                throw new InvalidDataException("Generic SS8-8 cursor delete probe did not position the second reader.");
            }

            readyGate.Signal();
            startGate.Wait();
            deleteB = reader.DeleteCurrent();
        });

        if (!readyGate.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidDataException("Generic SS8-8 cursor concurrent delete probe could not ready both callers.");
        }

        startGate.Set();
        Task.WaitAll(taskA, taskB);

        if (!deleteA || !deleteB)
        {
            throw new InvalidDataException($"Generic SS8-8 cursor concurrent deletes expected both tuples to be removed but saw {deleteA}/{deleteB}.");
        }

        long[] identities = new long[4];
        LibraDexGenericRangeReadResult read = index.ReadRange(100, 102, identities);
        if (read.IdentityCount != 1 ||
            identities[0] != 1003)
        {
            throw new InvalidDataException("Generic SS8-8 cursor concurrent deletes did not leave the expected surviving tuple.");
        }
    }

    /// <summary>
    /// Proves criteria-scoped delete inherits the internal queued exact-delete bridge for `SS8-8` instead of requiring caller-side concurrency ceremony.<br/>
    /// The criteria layer still captures tuples first; the improvement is at the exact tuple removal point shared with direct `IIndex.Delete`.<br/>
    /// </summary>
    private static void RunInternalGenericScalar8Scalar8CriteriaDeleteQueuedBridgeProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        CatalogIdentityGroupIndexes group = catalog.Indexes["generic-criteria-delete"];
        using LibraDexIndex<long, long> index = group["score"].Int64Keys<long>().Create();
        _ = index.Insert(100, 1001);
        _ = index.Insert(101, 1002);
        _ = index.Insert(102, 1003);
        Func<string, IIndex> resolver = indexName => string.Equals(indexName, "score", StringComparison.Ordinal)
            ? index
            : throw new InvalidDataException($"Unexpected criteria delete index '{indexName}'.");

        using ManualResetEventSlim startGate = new(false);
        using CountdownEvent readyGate = new(2);
        LibraDexIdentityMutationResult deleteA = default;
        LibraDexIdentityMutationResult deleteB = default;

        Task taskA = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            deleteA = group["score"].Delete(LibraDexCondition
                .ForGroup("generic-criteria-delete")
                .Index("score").AsInt64.EqualTo(100L)
                .EndCondition);
        });

        Task taskB = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            deleteB = group["score"].Delete(LibraDexCondition
                .ForGroup("generic-criteria-delete")
                .Index("score").AsInt64.EqualTo(101L)
                .EndCondition);
        });

        if (!readyGate.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidDataException("Generic SS8-8 criteria delete queued bridge probe could not ready both callers.");
        }

        startGate.Set();
        Task.WaitAll(taskA, taskB);

        if (deleteA.ChangedCount != 1 ||
            deleteB.ChangedCount != 1)
        {
            throw new InvalidDataException($"Generic SS8-8 criteria delete expected one changed tuple per caller but saw {deleteA.ChangedCount}/{deleteB.ChangedCount}.");
        }

        long[] identities = new long[4];
        LibraDexGenericRangeReadResult read = index.ReadRange(100, 102, identities);
        if (read.IdentityCount != 1 ||
            identities[0] != 1003)
        {
            throw new InvalidDataException("Generic SS8-8 criteria delete queued bridge did not leave the expected surviving tuple.");
        }
    }

    /// <summary>
    /// Proves ordinary direct `IIndex.Rekey(identity, oldKey, newKey)` can overlap through the no-ceremony generic `SS8-8` queued insert/delete bridge.<br/>
    /// Direct rekey is the public exact-key patch shape, and should not require callers to create a queued writer for independent value-route updates.<br/>
    /// </summary>
    private static void RunInternalGenericScalar8Scalar8DirectConcurrentRekeyProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        using LibraDexIndex<long, long> index = catalog.Indexes["generic-direct-rekey"]["ids"].Int64Keys<long>().Create();
        _ = index.Insert(100, 1001);
        _ = index.Insert(101, 1002);
        IIndex runtimeIndex = index;
        using ManualResetEventSlim startGate = new(false);
        using CountdownEvent readyGate = new(2);
        bool rekeyA = false;
        bool rekeyB = false;

        Task taskA = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            rekeyA = runtimeIndex.Rekey(1001L, 100L, 200L);
        });

        Task taskB = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            rekeyB = runtimeIndex.Rekey(1002L, 101L, 201L);
        });

        if (!readyGate.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidDataException("Generic SS8-8 direct concurrent rekey probe could not ready both callers.");
        }

        startGate.Set();
        Task.WaitAll(taskA, taskB);

        if (!rekeyA || !rekeyB)
        {
            throw new InvalidDataException($"Generic SS8-8 direct concurrent rekeys expected both tuples to move but saw {rekeyA}/{rekeyB}.");
        }

        long[] identities = new long[4];
        LibraDexGenericRangeReadResult oldRead = index.ReadRange(100, 101, identities);
        if (oldRead.IdentityCount != 0)
        {
            throw new InvalidDataException("Generic SS8-8 direct concurrent rekeys left old key tuples behind.");
        }

        Array.Clear(identities);
        LibraDexGenericRangeReadResult newRead = index.ReadRange(200, 201, identities);
        if (newRead.IdentityCount != 2 ||
            identities[0] != 1001 ||
            identities[1] != 1002)
        {
            throw new InvalidDataException("Generic SS8-8 direct concurrent rekeys did not publish replacement tuples.");
        }
    }

    /// <summary>
    /// Proves cursor-local `SetKey(...)` on generic `SS8-8` readers can overlap through the same queued insert plus exact-delete bridge as direct rekey.<br/>
    /// The range readers are still live-session cursors; this probe covers only the physical mutation path after each cursor has captured its positioned tuple.<br/>
    /// </summary>
    private static void RunInternalGenericScalar8Scalar8CursorConcurrentSetKeyProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        using LibraDexIndex<long, long> index = catalog.Indexes["generic-cursor-setkey"]["ids"].Int64Keys<long>().Create();
        _ = index.Insert(100, 1001);
        _ = index.Insert(101, 1002);
        using ManualResetEventSlim startGate = new(false);
        using CountdownEvent readyGate = new(2);
        LibraDexGenericInsertResult setKeyA = default;
        LibraDexGenericInsertResult setKeyB = default;

        Task taskA = Task.Run(() =>
        {
            using LibraDexRangeReader<long, long> reader = index.OpenRangeReader(100, 100);
            if (!reader.MoveNext())
            {
                throw new InvalidDataException("Generic SS8-8 cursor SetKey probe did not position the first reader.");
            }

            readyGate.Signal();
            startGate.Wait();
            setKeyA = reader.SetKey(200);
        });

        Task taskB = Task.Run(() =>
        {
            using LibraDexRangeReader<long, long> reader = index.OpenRangeReader(101, 101);
            if (!reader.MoveNext())
            {
                throw new InvalidDataException("Generic SS8-8 cursor SetKey probe did not position the second reader.");
            }

            readyGate.Signal();
            startGate.Wait();
            setKeyB = reader.SetKey(201);
        });

        if (!readyGate.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidDataException("Generic SS8-8 cursor concurrent SetKey probe could not ready both callers.");
        }

        startGate.Set();
        Task.WaitAll(taskA, taskB);

        if (!setKeyA.Inserted || !setKeyB.Inserted)
        {
            throw new InvalidDataException($"Generic SS8-8 cursor concurrent SetKey expected both replacement tuples but saw {setKeyA.Inserted}/{setKeyB.Inserted}.");
        }

        long[] identities = new long[4];
        LibraDexGenericRangeReadResult oldRead = index.ReadRange(100, 101, identities);
        if (oldRead.IdentityCount != 0)
        {
            throw new InvalidDataException("Generic SS8-8 cursor concurrent SetKey left old key tuples behind.");
        }

        Array.Clear(identities);
        LibraDexGenericRangeReadResult newRead = index.ReadRange(200, 201, identities);
        if (newRead.IdentityCount != 2 ||
            identities[0] != 1001 ||
            identities[1] != 1002)
        {
            throw new InvalidDataException("Generic SS8-8 cursor concurrent SetKey did not publish replacement tuples.");
        }
    }

    /// <summary>
    /// Proves criteria-scoped `SetKey` can overlap for ordinary `SS8-8` value-route keys by reusing direct queued insert and exact-delete paths.<br/>
    /// This covers the common patch/update flow where callers express a key change through the condition API instead of direct old-key knowledge.<br/>
    /// </summary>
    private static void RunInternalGenericScalar8Scalar8CriteriaSetKeyQueuedBridgeProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        CatalogIdentityGroupIndexes group = catalog.Indexes["generic-criteria-setkey"];
        using LibraDexIndex<long, long> index = group["score"].Int64Keys<long>().Create();
        _ = index.Insert(100, 1001);
        _ = index.Insert(101, 1002);
        Func<string, IIndex> resolver = indexName => string.Equals(indexName, "score", StringComparison.Ordinal)
            ? index
            : throw new InvalidDataException($"Unexpected criteria SetKey index '{indexName}'.");

        using ManualResetEventSlim startGate = new(false);
        using CountdownEvent readyGate = new(2);
        LibraDexIdentityMutationResult setKeyA = default;
        LibraDexIdentityMutationResult setKeyB = default;

        Task taskA = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            setKeyA = LibraDexCondition
                .ForGroup("generic-criteria-setkey")
                .Index("score").AsInt64.EqualTo(100L)
                .EndCondition
                .Materialize(resolver)
                .Mutate
                .SetKey(200L)
                .Execute();
        });

        Task taskB = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            setKeyB = LibraDexCondition
                .ForGroup("generic-criteria-setkey")
                .Index("score").AsInt64.EqualTo(101L)
                .EndCondition
                .Materialize(resolver)
                .Mutate
                .SetKey(201L)
                .Execute();
        });

        if (!readyGate.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidDataException("Generic SS8-8 criteria SetKey queued bridge probe could not ready both callers.");
        }

        startGate.Set();
        Task.WaitAll(taskA, taskB);

        if (setKeyA.ChangedCount != 1 ||
            setKeyB.ChangedCount != 1)
        {
            throw new InvalidDataException($"Generic SS8-8 criteria SetKey expected one changed tuple per caller but saw {setKeyA.ChangedCount}/{setKeyB.ChangedCount}.");
        }

        long[] identities = new long[4];
        LibraDexGenericRangeReadResult oldRead = index.ReadRange(100, 101, identities);
        if (oldRead.IdentityCount != 0)
        {
            throw new InvalidDataException("Generic SS8-8 criteria SetKey queued bridge left old key tuples behind.");
        }

        Array.Clear(identities);
        LibraDexGenericRangeReadResult newRead = index.ReadRange(200, 201, identities);
        if (newRead.IdentityCount != 2 ||
            identities[0] != 1001 ||
            identities[1] != 1002)
        {
            throw new InvalidDataException("Generic SS8-8 criteria SetKey queued bridge did not publish replacement tuples.");
        }
    }

    /// <summary>
    /// Proves scalar-null route mutation is safe under overlapping `SS8-8` callers.<br/>
    /// The null route is identity-keyed metadata storage, not an ordinary value shelf, so the expected behavior is route-level serialization rather than independent shelf-local staging.<br/>
    /// </summary>
    private static void RunInternalGenericScalar8Scalar8ScalarNullRouteConcurrentProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        using LibraDexIndex<long, long> index = catalog.Indexes["generic-scalar-null-route"]["ids"].Int64Keys<long>().Create();
        using ManualResetEventSlim insertStartGate = new(false);
        using CountdownEvent insertReadyGate = new(2);
        LibraDexGenericInsertResult insertA = default;
        LibraDexGenericInsertResult insertB = default;

        Task insertTaskA = Task.Run(() =>
        {
            insertReadyGate.Signal();
            insertStartGate.Wait();
            insertA = index.Insert(ScalarNull.Null, 1001);
        });

        Task insertTaskB = Task.Run(() =>
        {
            insertReadyGate.Signal();
            insertStartGate.Wait();
            insertB = index.Insert(ScalarNull.Null, 1002);
        });

        if (!insertReadyGate.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidDataException("Generic SS8-8 scalar-null route insert probe could not ready both callers.");
        }

        insertStartGate.Set();
        Task.WaitAll(insertTaskA, insertTaskB);
        if (!insertA.Inserted || !insertB.Inserted)
        {
            throw new InvalidDataException($"Generic SS8-8 scalar-null route concurrent inserts expected both identities to be inserted but saw {insertA.Inserted}/{insertB.Inserted}.");
        }

        using ManualResetEventSlim deleteStartGate = new(false);
        using CountdownEvent deleteReadyGate = new(2);
        bool deleteA = false;
        bool deleteB = false;
        Task deleteTaskA = Task.Run(() =>
        {
            deleteReadyGate.Signal();
            deleteStartGate.Wait();
            deleteA = index.Delete(ScalarNull.Null, 1001);
        });

        Task deleteTaskB = Task.Run(() =>
        {
            deleteReadyGate.Signal();
            deleteStartGate.Wait();
            deleteB = index.Delete(ScalarNull.Null, 1002);
        });

        if (!deleteReadyGate.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidDataException("Generic SS8-8 scalar-null route delete probe could not ready both callers.");
        }

        deleteStartGate.Set();
        Task.WaitAll(deleteTaskA, deleteTaskB);
        if (!deleteA || !deleteB)
        {
            throw new InvalidDataException($"Generic SS8-8 scalar-null route concurrent deletes expected both identities to be removed but saw {deleteA}/{deleteB}.");
        }

        IReadOnlyList<long> nullIds = LibraDexCondition
            .ForGroup("generic-scalar-null-route")
            .Index("ids").AsInt64.EqualTo(ScalarNull.Null)
            .EndCondition
            .ToList<long>(name => string.Equals(name, "ids", StringComparison.Ordinal)
                ? index
                : throw new InvalidDataException($"Unexpected scalar-null route index '{name}'."));
        if (nullIds.Count != 0)
        {
            throw new InvalidDataException("Generic SS8-8 scalar-null route concurrent deletes left null-route identities behind.");
        }
    }

    /// <summary>
    /// Proves `ScalarNull.NonNull` criteria delete uses queued exact tuple deletion for ordinary `SS8-8` value-route tuples.<br/>
    /// Null-route identities are intentionally excluded by the predicate and must remain visible after the non-null delete.<br/>
    /// </summary>
    private static void RunInternalGenericScalar8Scalar8ScalarNullNonNullDeleteProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        using LibraDexIndex<long, long> index = catalog.Indexes["generic-scalar-null-nonnull-delete"]["ids"].Int64Keys<long>().Create();
        _ = index.Insert(100, 1001);
        _ = index.Insert(101, 1002);
        _ = index.Insert(ScalarNull.Null, 2001);
        Func<string, IIndex> resolver = name => string.Equals(name, "ids", StringComparison.Ordinal)
            ? index
            : throw new InvalidDataException($"Unexpected scalar-null non-null delete index '{name}'.");

        LibraDexIdentityMutationResult delete = catalog["generic-scalar-null-nonnull-delete"]["ids"].Delete(LibraDexCondition
            .ForGroup("generic-scalar-null-nonnull-delete")
            .Index("ids").AsInt64.EqualTo(ScalarNull.NonNull)
            .EndCondition);
        if (delete.ChangedCount != 2)
        {
            throw new InvalidDataException($"Generic SS8-8 ScalarNull.NonNull delete expected two changed tuples but saw {delete.ChangedCount}.");
        }

        long[] identities = new long[4];
        LibraDexGenericRangeReadResult valueRead = index.ReadRange(100, 101, identities);
        if (valueRead.IdentityCount != 0)
        {
            throw new InvalidDataException("Generic SS8-8 ScalarNull.NonNull delete left ordinary value-route tuples behind.");
        }

        IReadOnlyList<long> nullIds = LibraDexCondition
            .ForGroup("generic-scalar-null-nonnull-delete")
            .Index("ids").AsInt64.EqualTo(ScalarNull.Null)
            .EndCondition
            .ToList<long>(resolver);
        if (nullIds.Count != 1 || nullIds[0] != 2001)
        {
            throw new InvalidDataException("Generic SS8-8 ScalarNull.NonNull delete disturbed null-route identities.");
        }
    }

    /// <summary>
    /// Proves `All` criteria delete preserves scalar-null route deletion while routing ordinary `SS8-8` value tuples through queued exact deletion.<br/>
    /// This keeps the broad delete semantics intact without falling back to the old value-range rewrite for the value-route half.<br/>
    /// </summary>
    private static void RunInternalGenericScalar8Scalar8AllDeleteQueuedValueProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        using LibraDexIndex<long, long> index = catalog.Indexes["generic-all-delete"]["ids"].Int64Keys<long>().Create();
        _ = index.Insert(100, 1001);
        _ = index.Insert(101, 1002);
        _ = index.Insert(ScalarNull.Null, 2001);
        Func<string, IIndex> resolver = name => string.Equals(name, "ids", StringComparison.Ordinal)
            ? index
            : throw new InvalidDataException($"Unexpected all delete index '{name}'.");

        LibraDexIdentityMutationResult delete = catalog["generic-all-delete"]["ids"].DeleteAll();
        if (delete.ChangedCount != 3)
        {
            throw new InvalidDataException($"Generic SS8-8 All delete expected three changed tuples but saw {delete.ChangedCount}.");
        }

        long[] identities = new long[4];
        LibraDexGenericRangeReadResult valueRead = index.ReadRange(100, 101, identities);
        if (valueRead.IdentityCount != 0)
        {
            throw new InvalidDataException("Generic SS8-8 All delete left ordinary value-route tuples behind.");
        }

        IReadOnlyList<long> nullIds = LibraDexCondition
            .ForGroup("generic-all-delete")
            .Index("ids").AsInt64.EqualTo(ScalarNull.Null)
            .EndCondition
            .ToList<long>(resolver);
        if (nullIds.Count != 0)
        {
            throw new InvalidDataException("Generic SS8-8 All delete left scalar-null route identities behind.");
        }
    }

    /// <summary>
    /// Proves generic `SS16-8` cursor-local delete and SetKey now converge through the owning index exact-mutation path.<br/>
    /// This transfers the `SS8-8` cursor lesson without claiming `SS16-8` has writer-context concurrency yet.<br/>
    /// </summary>
    private static void RunInternalGenericScalar16Scalar8CursorMutationConvergenceProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        Guid keyA = Guid.Parse("10000000-0000-0000-0000-000000000001");
        Guid keyB = Guid.Parse("10000000-0000-0000-0000-000000000002");
        Guid keyC = Guid.Parse("10000000-0000-0000-0000-000000000003");
        Guid keyD = Guid.Parse("10000000-0000-0000-0000-000000000004");
        using LibraDexIndex<Guid, long> index = catalog.Indexes["generic-ss16-8-cursor"]["ids"].GuidKeys<long>().Create();
        _ = index.Insert(keyA, 1001);
        _ = index.Insert(keyB, 1002);
        _ = index.Insert(keyC, 1003);

        using (LibraDexRangeReader<Guid, long> deleteReader = index.OpenRangeReader(keyA, keyA))
        {
            if (!deleteReader.MoveNext() ||
                !deleteReader.DeleteCurrent())
            {
                throw new InvalidDataException("Generic SS16-8 cursor delete convergence probe failed to delete the positioned tuple.");
            }
        }

        using (LibraDexRangeReader<Guid, long> setKeyReader = index.OpenRangeReader(keyB, keyB))
        {
            if (!setKeyReader.MoveNext())
            {
                throw new InvalidDataException("Generic SS16-8 cursor SetKey convergence probe failed to position the reader.");
            }

            LibraDexGenericInsertResult result = setKeyReader.SetKey(keyD);
            if (!result.Inserted)
            {
                throw new InvalidDataException("Generic SS16-8 cursor SetKey convergence probe did not insert the replacement tuple.");
            }
        }

        long[] identities = new long[4];
        LibraDexGenericRangeReadResult deletedRead = index.ReadRange(keyA, keyA, identities);
        if (deletedRead.IdentityCount != 0)
        {
            throw new InvalidDataException("Generic SS16-8 cursor delete convergence probe left the deleted tuple behind.");
        }

        Array.Clear(identities);
        LibraDexGenericRangeReadResult oldRead = index.ReadRange(keyB, keyB, identities);
        if (oldRead.IdentityCount != 0)
        {
            throw new InvalidDataException("Generic SS16-8 cursor SetKey convergence probe left the old tuple behind.");
        }

        Array.Clear(identities);
        LibraDexGenericRangeReadResult survivorRead = index.ReadRange(keyC, keyD, identities);
        if (survivorRead.IdentityCount != 2 ||
            identities[0] != 1003 ||
            identities[1] != 1002)
        {
            throw new InvalidDataException("Generic SS16-8 cursor mutation convergence probe did not leave the expected surviving tuples.");
        }
    }

    /// <summary>
    /// Proves `SS16-8` can now use the generic queued-writer facade without adding caller ceremony.<br/>
    /// The facade delegates to the same direct writer-context insert path as ordinary `Insert`, preserving path attribution for warmed shelf-local work.<br/>
    /// </summary>
    private static void RunInternalGenericScalar16Scalar8QueuedWriterFacadeProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        using LibraDexIndex<Int128, long> index = catalog.Indexes["generic-ss16-8-queued-facade"]["ids"].Int128Keys<long>().Create();
        Int128 baseKey = 1500;
        _ = index.Insert(baseKey, 15001);
        LibraDexQueuedWriter<Int128, long> writer = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);
        LibraDexGenericInsertResult insert = writer.Insert(baseKey + 1, 15002);
        if (!insert.Inserted ||
            insert.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext)
        {
            throw new InvalidDataException($"Generic SS16-8 queued writer facade expected writer-context insert but saw inserted={insert.Inserted}, path={insert.QueuedInsertPath}.");
        }

        long[] identities = new long[2];
        LibraDexGenericRangeReadResult read = index.ReadRange(baseKey, baseKey + 1, identities);
        if (read.IdentityCount != 2 ||
            identities[0] != 15001 ||
            identities[1] != 15002)
        {
            throw new InvalidDataException("Generic SS16-8 queued writer facade readback failed.");
        }

        LibraDexGenericDeleteResult delete = writer.Delete(baseKey, 15001);
        if (!delete.Deleted ||
            delete.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext)
        {
            throw new InvalidDataException($"Generic SS16-8 queued writer facade expected writer-context delete but saw deleted={delete.Deleted}, path={delete.QueuedInsertPath}.");
        }

        LibraDexGenericRekeyResult rekey = writer.Rekey(15002, baseKey + 1, baseKey + 2);
        if (!rekey.Changed ||
            rekey.Replacement.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext ||
            rekey.Removal.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext)
        {
            throw new InvalidDataException($"Generic SS16-8 queued writer facade expected writer-context rekey but saw changed={rekey.Changed}, replacement={rekey.Replacement.QueuedInsertPath}, removal={rekey.Removal.QueuedInsertPath}.");
        }

        Array.Clear(identities);
        LibraDexGenericRangeReadResult finalRead = index.ReadRange(baseKey, baseKey + 2, identities);
        if (finalRead.IdentityCount != 1 ||
            identities[0] != 15002)
        {
            throw new InvalidDataException("Generic SS16-8 queued writer facade delete/rekey readback failed.");
        }
    }

    /// <summary>
    /// Proves ordinary direct `SS16-8` inserts can overlap on warmed shelves through writer-context staging without caller-created queued writers.<br/>
    /// The first widened-key slice mirrors the `SS8-8` no-ceremony behavior only for shelf-local mutations; topology fallback remains outside this proof.<br/>
    /// </summary>
    private static void RunInternalGenericScalar16Scalar8DirectConcurrentInsertProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        using LibraDexIndex<Int128, long> index = catalog.Indexes["generic-ss16-8-direct-insert"]["ids"].Int128Keys<long>().Create();
        Int128 leftBase = 100;
        Int128 rightBase = ((Int128)long.MinValue << 64) + 100;
        _ = index.Insert(leftBase, 1001);
        _ = index.Insert(rightBase, 2001);
        using ManualResetEventSlim startGate = new(false);
        using CountdownEvent readyGate = new(2);
        LibraDexGenericInsertResult insertA = default;
        LibraDexGenericInsertResult insertB = default;

        Task taskA = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            insertA = index.Insert(leftBase + 1, 1002);
        });

        Task taskB = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            insertB = index.Insert(rightBase + 1, 2002);
        });

        if (!readyGate.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidDataException("Generic SS16-8 direct concurrent insert probe could not ready both callers.");
        }

        startGate.Set();
        Task.WaitAll(taskA, taskB);

        if (!insertA.Inserted ||
            !insertB.Inserted ||
            insertA.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext ||
            insertB.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext)
        {
            throw new InvalidDataException($"Generic SS16-8 direct concurrent inserts expected writer-context paths but saw {insertA.Inserted}/{insertA.QueuedInsertPath} and {insertB.Inserted}/{insertB.QueuedInsertPath}.");
        }

        long[] identities = new long[4];
        LibraDexGenericRangeReadResult leftRead = index.ReadRange(leftBase, leftBase + 1, identities);
        if (leftRead.IdentityCount != 2 ||
            identities[0] != 1001 ||
            identities[1] != 1002)
        {
            throw new InvalidDataException("Generic SS16-8 direct concurrent insert left-range readback failed.");
        }

        Array.Clear(identities);
        LibraDexGenericRangeReadResult rightRead = index.ReadRange(rightBase, rightBase + 1, identities);
        if (rightRead.IdentityCount != 2 ||
            identities[0] != 2001 ||
            identities[1] != 2002)
        {
            throw new InvalidDataException("Generic SS16-8 direct concurrent insert right-range readback failed.");
        }
    }

    /// <summary>
    /// Proves ordinary direct `SS16-8` inserts targeting the same warmed shelf remain caller-safe under overlap.<br/>
    /// This path may retry internally on same-shelf writer ownership before publishing both shelf-local mutations.<br/>
    /// </summary>
    private static void RunInternalGenericScalar16Scalar8SameShelfContentionProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        using LibraDexIndex<Int128, long> index = catalog.Indexes["generic-ss16-8-same-shelf"]["ids"].Int128Keys<long>().Create();
        Int128 baseKey = 1300;
        _ = index.Insert(baseKey, 13001);
        using ManualResetEventSlim startGate = new(false);
        using CountdownEvent readyGate = new(2);
        LibraDexGenericInsertResult insertA = default;
        LibraDexGenericInsertResult insertB = default;

        Task taskA = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            insertA = index.Insert(baseKey + 1, 13002);
        });

        Task taskB = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            insertB = index.Insert(baseKey + 2, 13003);
        });

        if (!readyGate.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidDataException("Generic SS16-8 same-shelf contention probe could not ready both callers.");
        }

        startGate.Set();
        Task.WaitAll(taskA, taskB);

        if (!insertA.Inserted ||
            !insertB.Inserted ||
            insertA.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext ||
            insertB.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext)
        {
            throw new InvalidDataException($"Generic SS16-8 same-shelf inserts expected writer-context paths after any internal retry but saw {insertA.Inserted}/{insertA.QueuedInsertPath} and {insertB.Inserted}/{insertB.QueuedInsertPath}.");
        }

        long[] identities = new long[4];
        LibraDexGenericRangeReadResult read = index.ReadRange(baseKey, baseKey + 2, identities);
        if (read.IdentityCount != 3 ||
            identities[0] != 13001 ||
            identities[1] != 13002 ||
            identities[2] != 13003)
        {
            throw new InvalidDataException("Generic SS16-8 same-shelf contention readback failed.");
        }
    }

    /// <summary>
    /// Proves ordinary direct exact `SS16-8` deletes can overlap on warmed shelves through writer-context staging.<br/>
    /// Delete path attribution is verified by readback because the current public exact delete shape returns only a Boolean.<br/>
    /// </summary>
    private static void RunInternalGenericScalar16Scalar8DirectConcurrentDeleteProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        using LibraDexIndex<Int128, long> index = catalog.Indexes["generic-ss16-8-direct-delete"]["ids"].Int128Keys<long>().Create();
        Int128 leftBase = 300;
        Int128 rightBase = ((Int128)long.MinValue << 64) + 300;
        _ = index.Insert(leftBase, 3001);
        _ = index.Insert(leftBase + 1, 3002);
        _ = index.Insert(rightBase, 4001);
        _ = index.Insert(rightBase + 1, 4002);
        IIndex runtimeIndex = index;
        using ManualResetEventSlim startGate = new(false);
        using CountdownEvent readyGate = new(2);
        bool deleteA = false;
        bool deleteB = false;

        Task taskA = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            deleteA = runtimeIndex.Delete(leftBase, 3001L);
        });

        Task taskB = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            deleteB = runtimeIndex.Delete(rightBase, 4001L);
        });

        if (!readyGate.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidDataException("Generic SS16-8 direct concurrent delete probe could not ready both callers.");
        }

        startGate.Set();
        Task.WaitAll(taskA, taskB);

        if (!deleteA || !deleteB)
        {
            throw new InvalidDataException($"Generic SS16-8 direct concurrent deletes expected both tuples to be removed but saw {deleteA}/{deleteB}.");
        }

        long[] identities = new long[4];
        LibraDexGenericRangeReadResult leftRead = index.ReadRange(leftBase, leftBase + 1, identities);
        if (leftRead.IdentityCount != 1 ||
            identities[0] != 3002)
        {
            throw new InvalidDataException("Generic SS16-8 direct concurrent delete left-range readback failed.");
        }

        Array.Clear(identities);
        LibraDexGenericRangeReadResult rightRead = index.ReadRange(rightBase, rightBase + 1, identities);
        if (rightRead.IdentityCount != 1 ||
            identities[0] != 4002)
        {
            throw new InvalidDataException("Generic SS16-8 direct concurrent delete right-range readback failed.");
        }
    }

    /// <summary>
    /// Proves ordinary direct `SS16-8` rekey can overlap through the widened-key writer-context insert/delete legs.<br/>
    /// This is the direct patch/update shape where callers know both the old and replacement keys.<br/>
    /// </summary>
    private static void RunInternalGenericScalar16Scalar8DirectConcurrentRekeyProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        using LibraDexIndex<Int128, long> index = catalog.Indexes["generic-ss16-8-direct-rekey"]["ids"].Int128Keys<long>().Create();
        Int128 leftBase = 700;
        Int128 rightBase = ((Int128)long.MinValue << 64) + 700;
        _ = index.Insert(leftBase, 7001);
        _ = index.Insert(rightBase, 8001);
        IIndex runtimeIndex = index;
        using ManualResetEventSlim startGate = new(false);
        using CountdownEvent readyGate = new(2);
        bool rekeyA = false;
        bool rekeyB = false;

        Task taskA = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            rekeyA = runtimeIndex.Rekey(7001L, leftBase, leftBase + 10);
        });

        Task taskB = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            rekeyB = runtimeIndex.Rekey(8001L, rightBase, rightBase + 10);
        });

        if (!readyGate.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidDataException("Generic SS16-8 direct concurrent rekey probe could not ready both callers.");
        }

        startGate.Set();
        Task.WaitAll(taskA, taskB);

        if (!rekeyA || !rekeyB)
        {
            throw new InvalidDataException($"Generic SS16-8 direct concurrent rekeys expected both tuples to move but saw {rekeyA}/{rekeyB}.");
        }

        long[] identities = new long[4];
        LibraDexGenericRangeReadResult oldLeftRead = index.ReadRange(leftBase, leftBase, identities);
        if (oldLeftRead.IdentityCount != 0)
        {
            throw new InvalidDataException("Generic SS16-8 direct concurrent rekey left old left tuple behind.");
        }

        LibraDexGenericRangeReadResult oldRightRead = index.ReadRange(rightBase, rightBase, identities);
        if (oldRightRead.IdentityCount != 0)
        {
            throw new InvalidDataException("Generic SS16-8 direct concurrent rekey left old right tuple behind.");
        }

        Array.Clear(identities);
        LibraDexGenericRangeReadResult newLeftRead = index.ReadRange(leftBase + 10, leftBase + 10, identities);
        if (newLeftRead.IdentityCount != 1 ||
            identities[0] != 7001)
        {
            throw new InvalidDataException("Generic SS16-8 direct concurrent rekey did not publish the left replacement tuple.");
        }

        Array.Clear(identities);
        LibraDexGenericRangeReadResult newRightRead = index.ReadRange(rightBase + 10, rightBase + 10, identities);
        if (newRightRead.IdentityCount != 1 ||
            identities[0] != 8001)
        {
            throw new InvalidDataException("Generic SS16-8 direct concurrent rekey did not publish the right replacement tuple.");
        }
    }

    /// <summary>
    /// Proves criteria-scoped delete on `SS16-8` uses captured exact tuples instead of range rewrite when no batch/projection boundary blocks it.<br/>
    /// The exact-delete legs then inherit the warmed shelf-local writer-context path.<br/>
    /// </summary>
    private static void RunInternalGenericScalar16Scalar8CriteriaDeleteBridgeProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        CatalogIdentityGroupIndexes group = catalog.Indexes["generic-ss16-8-criteria-delete"];
        using LibraDexIndex<Int128, long> index = group["score"].Int128Keys<long>().Create();
        Int128 leftBase = 900;
        Int128 rightBase = ((Int128)long.MinValue << 64) + 900;
        _ = index.Insert(leftBase, 9001);
        _ = index.Insert(leftBase + 1, 9002);
        _ = index.Insert(rightBase, 9101);
        _ = index.Insert(rightBase + 1, 9102);
        Func<string, IIndex> resolver = name => string.Equals(name, "score", StringComparison.Ordinal)
            ? index
            : throw new InvalidDataException($"Unexpected SS16-8 criteria delete index '{name}'.");

        using ManualResetEventSlim startGate = new(false);
        using CountdownEvent readyGate = new(2);
        LibraDexIdentityMutationResult deleteA = default;
        LibraDexIdentityMutationResult deleteB = default;

        Task taskA = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            deleteA = group["score"].Delete(LibraDexCondition
                .ForGroup("generic-ss16-8-criteria-delete")
                .Index("score").AsInt128.EqualTo(leftBase)
                .EndCondition);
        });

        Task taskB = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            deleteB = group["score"].Delete(LibraDexCondition
                .ForGroup("generic-ss16-8-criteria-delete")
                .Index("score").AsInt128.EqualTo(rightBase)
                .EndCondition);
        });

        if (!readyGate.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidDataException("Generic SS16-8 criteria delete bridge probe could not ready both callers.");
        }

        startGate.Set();
        Task.WaitAll(taskA, taskB);

        if (deleteA.ChangedCount != 1 ||
            deleteB.ChangedCount != 1)
        {
            throw new InvalidDataException($"Generic SS16-8 criteria delete expected one changed tuple per caller but saw {deleteA.ChangedCount}/{deleteB.ChangedCount}.");
        }

        long[] identities = new long[4];
        LibraDexGenericRangeReadResult leftRead = index.ReadRange(leftBase, leftBase + 1, identities);
        if (leftRead.IdentityCount != 1 ||
            identities[0] != 9002)
        {
            throw new InvalidDataException("Generic SS16-8 criteria delete bridge left-range readback failed.");
        }

        Array.Clear(identities);
        LibraDexGenericRangeReadResult rightRead = index.ReadRange(rightBase, rightBase + 1, identities);
        if (rightRead.IdentityCount != 1 ||
            identities[0] != 9102)
        {
            throw new InvalidDataException("Generic SS16-8 criteria delete bridge right-range readback failed.");
        }
    }

    /// <summary>
    /// Proves criteria-scoped `SetKey` on `SS16-8` overlaps through captured tuple insert/delete legs.<br/>
    /// No production change is expected here after the direct writer-context slice because condition SetKey already converges on `Insert` plus exact delete.<br/>
    /// </summary>
    private static void RunInternalGenericScalar16Scalar8CriteriaSetKeyBridgeProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        CatalogIdentityGroupIndexes group = catalog.Indexes["generic-ss16-8-criteria-setkey"];
        using LibraDexIndex<Int128, long> index = group["score"].Int128Keys<long>().Create();
        Int128 leftBase = 1100;
        Int128 rightBase = ((Int128)long.MinValue << 64) + 1100;
        _ = index.Insert(leftBase, 11001);
        _ = index.Insert(rightBase, 11101);
        Func<string, IIndex> resolver = name => string.Equals(name, "score", StringComparison.Ordinal)
            ? index
            : throw new InvalidDataException($"Unexpected SS16-8 criteria SetKey index '{name}'.");

        using ManualResetEventSlim startGate = new(false);
        using CountdownEvent readyGate = new(2);
        LibraDexIdentityMutationResult setKeyA = default;
        LibraDexIdentityMutationResult setKeyB = default;

        Task taskA = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            setKeyA = LibraDexCondition
                .ForGroup("generic-ss16-8-criteria-setkey")
                .Index("score").AsInt128.EqualTo(leftBase)
                .EndCondition
                .Materialize(resolver)
                .Mutate
                .SetKey(leftBase + 10)
                .Execute();
        });

        Task taskB = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            setKeyB = LibraDexCondition
                .ForGroup("generic-ss16-8-criteria-setkey")
                .Index("score").AsInt128.EqualTo(rightBase)
                .EndCondition
                .Materialize(resolver)
                .Mutate
                .SetKey(rightBase + 10)
                .Execute();
        });

        if (!readyGate.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidDataException("Generic SS16-8 criteria SetKey bridge probe could not ready both callers.");
        }

        startGate.Set();
        Task.WaitAll(taskA, taskB);

        if (setKeyA.ChangedCount != 1 ||
            setKeyB.ChangedCount != 1)
        {
            throw new InvalidDataException($"Generic SS16-8 criteria SetKey expected one changed tuple per caller but saw {setKeyA.ChangedCount}/{setKeyB.ChangedCount}.");
        }

        long[] identities = new long[4];
        LibraDexGenericRangeReadResult oldLeftRead = index.ReadRange(leftBase, leftBase, identities);
        if (oldLeftRead.IdentityCount != 0)
        {
            throw new InvalidDataException("Generic SS16-8 criteria SetKey left old left tuple behind.");
        }

        LibraDexGenericRangeReadResult oldRightRead = index.ReadRange(rightBase, rightBase, identities);
        if (oldRightRead.IdentityCount != 0)
        {
            throw new InvalidDataException("Generic SS16-8 criteria SetKey left old right tuple behind.");
        }

        Array.Clear(identities);
        LibraDexGenericRangeReadResult newLeftRead = index.ReadRange(leftBase + 10, leftBase + 10, identities);
        if (newLeftRead.IdentityCount != 1 ||
            identities[0] != 11001)
        {
            throw new InvalidDataException("Generic SS16-8 criteria SetKey did not publish the left replacement tuple.");
        }

        Array.Clear(identities);
        LibraDexGenericRangeReadResult newRightRead = index.ReadRange(rightBase + 10, rightBase + 10, identities);
        if (newRightRead.IdentityCount != 1 ||
            identities[0] != 11101)
        {
            throw new InvalidDataException("Generic SS16-8 criteria SetKey did not publish the right replacement tuple.");
        }
    }

    /// <summary>
    /// Proves cold-route `SS16-8` direct insert uses the narrow serialized-topology path instead of pretending writer-context route publication is shelf-local.<br/>
    /// This keeps the widened-key writer-context slice honest: initial root-route setup is narrowed, while split/transform work is still future topology work.<br/>
    /// </summary>
    private static void RunInternalGenericScalar16Scalar8ColdRouteFallbackProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        using LibraDexIndex<Int128, long> index = catalog.Indexes["generic-ss16-8-cold-route"]["ids"].Int128Keys<long>().Create();
        _ = index.Insert(500, 5001);
        Int128 coldKey = ((Int128)long.MinValue << 64) + 500;
        LibraDexGenericInsertResult insert = index.Insert(coldKey, 6001);
        if (!insert.Inserted ||
            !insert.CreatedInitialShelfRoute ||
            insert.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.NarrowTopologyPublisher)
        {
            throw new InvalidDataException($"Generic SS16-8 cold route expected narrow serialized route creation but saw inserted={insert.Inserted}, created={insert.CreatedInitialShelfRoute}, path={insert.QueuedInsertPath}.");
        }

        long[] identities = new long[2];
        LibraDexGenericRangeReadResult read = index.ReadRange(coldKey, coldKey, identities);
        if (read.IdentityCount != 1 ||
            identities[0] != 6001)
        {
            throw new InvalidDataException("Generic SS16-8 cold route fallback readback failed.");
        }
    }

    /// <summary>
    /// Proves cold-route `SS16-8` topology ownership is rooted at the exact root-prefix domain instead of a whole-index queue.<br/>
    /// The probe holds one cold root-prefix owner after admission, then verifies a different root-prefix writer enters its own owner while a same-prefix writer waits for release.<br/>
    /// This extends the `SS8-8` physical-library concurrency proof to widened keys: unrelated shelves can be initialized independently, while writers that share the same root router slot still synchronize.<br/>
    /// </summary>
    private static void RunInternalGenericScalar16Scalar8ColdRootPrefixOwnerProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        using LibraDexIndex<Int128, long> index = catalog.Indexes["generic-ss16-8-cold-root-owner"]["ids"].Int128Keys<long>().Create();
        Int128 firstKey = ((Int128)0x10 << 120) + 1;
        Int128 differentPrefixKey = ((Int128)0x20 << 120) + 1;
        Int128 samePrefixKey = ((Int128)0x10 << 120) + 2;
        LibraDexGenericScalarCodec<Int128>.Encode16(firstKey, out ulong firstKeyHigh, out _);
        LibraDexGenericScalarCodec<Int128>.Encode16(differentPrefixKey, out ulong differentPrefixKeyHigh, out _);
        byte firstPrefix = (byte)(firstKeyHigh >> 56);
        byte differentPrefix = (byte)(differentPrefixKeyHigh >> 56);
        using ManualResetEventSlim firstOwnerEntered = new(false);
        using ManualResetEventSlim releaseFirstOwner = new(false);
        using ManualResetEventSlim differentOwnerEntered = new(false);
        using ManualResetEventSlim sameOwnerEntered = new(false);
        int firstPrefixOwnerEntries = 0;

        LibraDexFileSession.PrimitiveTopologyOwnerEnteredForValidation = (shape, kind, rootRouterOffset, ownerOffset, prefixByte) =>
        {
            if (shape != 1 || kind != 1 || rootRouterOffset != index.RootRouterOffset || ownerOffset != 0)
            {
                return;
            }

            if (prefixByte == firstPrefix)
            {
                int entry = Interlocked.Increment(ref firstPrefixOwnerEntries);
                if (entry == 1)
                {
                    firstOwnerEntered.Set();
                    if (!releaseFirstOwner.Wait(TimeSpan.FromSeconds(10)))
                    {
                        throw new InvalidDataException("SS16-8 cold root-prefix owner probe timed out while holding the first prefix owner.");
                    }
                }
                else
                {
                    sameOwnerEntered.Set();
                }
            }
            else if (prefixByte == differentPrefix)
            {
                differentOwnerEntered.Set();
            }
        };

        try
        {
            Task<LibraDexGenericInsertResult> firstTask = Task.Run(() => index.Insert(firstKey, 1001));
            if (!firstOwnerEntered.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new InvalidDataException("SS16-8 cold root-prefix owner probe did not enter the held first-prefix owner.");
            }

            Task<LibraDexGenericInsertResult> differentPrefixTask = Task.Run(() => index.Insert(differentPrefixKey, 2001));
            if (!differentOwnerEntered.Wait(TimeSpan.FromSeconds(10)))
            {
                releaseFirstOwner.Set();
                throw new InvalidDataException("SS16-8 cold root-prefix owner probe expected a different root prefix to enter its own topology owner while the first prefix was held.");
            }

            Task<LibraDexGenericInsertResult> samePrefixTask = Task.Run(() => index.Insert(samePrefixKey, 1002));
            if (sameOwnerEntered.Wait(TimeSpan.FromMilliseconds(100)))
            {
                releaseFirstOwner.Set();
                throw new InvalidDataException("SS16-8 cold root-prefix owner probe allowed a same-prefix writer into the held owner domain.");
            }

            releaseFirstOwner.Set();
            Task.WaitAll(firstTask, differentPrefixTask, samePrefixTask);
            LibraDexGenericInsertResult firstResult = firstTask.GetAwaiter().GetResult();
            LibraDexGenericInsertResult differentResult = differentPrefixTask.GetAwaiter().GetResult();
            LibraDexGenericInsertResult sameResult = samePrefixTask.GetAwaiter().GetResult();
            if (!firstResult.Inserted ||
                !firstResult.CreatedInitialShelfRoute ||
                firstResult.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.NarrowTopologyPublisher)
            {
                throw new InvalidDataException($"SS16-8 cold root-prefix owner probe first insert attribution was wrong. inserted={firstResult.Inserted} createdRoute={firstResult.CreatedInitialShelfRoute} path={firstResult.QueuedInsertPath}.");
            }

            if (!differentResult.Inserted ||
                !differentResult.CreatedInitialShelfRoute ||
                differentResult.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.NarrowTopologyPublisher)
            {
                throw new InvalidDataException($"SS16-8 cold root-prefix owner probe different-prefix insert attribution was wrong. inserted={differentResult.Inserted} createdRoute={differentResult.CreatedInitialShelfRoute} path={differentResult.QueuedInsertPath}.");
            }

            if (!sameResult.Inserted)
            {
                throw new InvalidDataException($"SS16-8 cold root-prefix owner probe same-prefix insert failed after owner release. inserted={sameResult.Inserted} path={sameResult.QueuedInsertPath}.");
            }
        }
        finally
        {
            releaseFirstOwner.Set();
            LibraDexFileSession.PrimitiveTopologyOwnerEnteredForValidation = null;
        }

        long[] identities = new long[4];
        LibraDexGenericRangeReadResult read = index.ReadRange(firstKey, differentPrefixKey, identities);
        if (read.IdentityCount != 3 ||
            identities[0] != 1001 ||
            identities[1] != 1002 ||
            identities[2] != 2001)
        {
            throw new InvalidDataException("SS16-8 cold root-prefix owner probe inserts were not readable in sorted range order.");
        }
    }

    /// <summary>
    /// Proves full-shelf `SS16-8` direct insert uses the serialized topology fallback without entering the old one-item batch path.<br/>
    /// The fallback may split or transform the routed shelf, but the caller-facing contract is stable insertion with serialized-fallback attribution.<br/>
    /// </summary>
    private static void RunInternalGenericScalar16Scalar8FullShelfSerializedTopologyProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        using LibraDexIndex<Int128, long> index = catalog.Indexes["generic-ss16-8-full-shelf-topology"]["ids"].Int128Keys<long>().Create();
        Scalar16Scalar8Profile profile = index.GetScalar16Scalar8Profile();
        Int128 baseKey = 0;
        for (int i = 0; i < profile.MaxItemCount; i++)
        {
            Int128 fillKey = baseKey + ((Int128)(i + 1) << 48);
            LibraDexGenericInsertResult fill = index.Insert(fillKey, 17000 + i);
            if (!fill.Inserted)
            {
                throw new InvalidDataException($"Generic SS16-8 full-shelf topology setup insert {i} failed.");
            }
        }

        Int128 overflowKey = baseKey + ((Int128)(profile.MaxItemCount + 1) << 48);
        LibraDexGenericInsertResult overflow = index.Insert(overflowKey, 17000 + profile.MaxItemCount);
        if (!overflow.Inserted ||
            overflow.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.NarrowTopologyPublisher)
        {
            throw new InvalidDataException($"Generic SS16-8 full-shelf topology expected serialized fallback but saw inserted={overflow.Inserted}, path={overflow.QueuedInsertPath}.");
        }
    }

    /// <summary>
    /// Proves `SS16-8` writer-context publication rejects a staged shelf after the selected physical shelf has been transformed into deeper route topology.<br/>
    /// This guards the primitive concurrency contract that shelf-local writers may publish only while the route still selects the same committed shelf shape.<br/>
    /// </summary>
    private static void RunInternalGenericScalar16Scalar8StaleRouteClaimProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        using LibraDexIndex<Int128, long> index = catalog.Indexes["generic-ss16-8-stale-route-claim"]["ids"].Int128Keys<long>().Create();
        Scalar16Scalar8Profile profile = index.GetScalar16Scalar8Profile();
        const ulong keyHigh = 0x4400_0000_0000_0000UL;
        const ulong seedKeyLow = 1;
        const ulong stagedKeyLow = 0x7FFF;
        byte rootPrefix = (byte)(keyHigh >> 56);

        LibraDexGenericInsertResult seed = index.Insert(
            LibraDexGenericScalarCodec<Int128>.Decode16(keyHigh, seedKeyLow),
            19001);
        if (!seed.Inserted)
        {
            throw new InvalidDataException("Generic SS16-8 stale-route setup failed to seed the initial shelf.");
        }

        long originalTarget = index.Session.FindRouterTarget(index.RootRouterOffset, rootPrefix);
        if (originalTarget == 0)
        {
            throw new InvalidDataException("Generic SS16-8 stale-route setup did not create the expected root-prefix shelf.");
        }

        LibraDexWriteContext staleWriter = index.Session.BeginScalar16Scalar8WriteContext();
        Scalar16Scalar8RoutedInsertResult staged = index.Session.InsertWalkedRoutedScalar16Scalar8NoSplitForWriteContext(
            staleWriter,
            index.RootRouterOffset,
            profile,
            keyHigh,
            stagedKeyLow,
            19999,
            allowDuplicateKeys: true,
            maxRouterHops: 8);
        if (staged.InsertResult != Scalar16Scalar8InsertResult.Inserted ||
            staged.PrimaryOffset != originalTarget)
        {
            index.Session.AbortScalar16Scalar8WriteContext(staleWriter);
            throw new InvalidDataException("Generic SS16-8 stale-route setup failed to stage the expected writer-context shelf image.");
        }

        for (int i = 2; i <= profile.MaxItemCount; i++)
        {
            Scalar16Scalar8RoutedInsertResult fill = index.Session.InsertWalkedRoutedScalar16Scalar8(
                index.RootRouterOffset,
                profile,
                keyHigh,
                checked((ulong)i),
                checked((ulong)(19000 + i)),
                allowDuplicateKeys: true,
                maxRouterHops: 8);
            if (fill.InsertResult != Scalar16Scalar8InsertResult.Inserted)
            {
                index.Session.AbortScalar16Scalar8WriteContext(staleWriter);
                throw new InvalidDataException($"Generic SS16-8 stale-route setup fill insert {i} failed with {fill.InsertResult}.");
            }
        }

        if (!index.Session.TryTransformScalar16Scalar8DirectShelf(
            index.RootRouterOffset,
            profile,
            keyHigh,
            checked((ulong)(profile.MaxItemCount + 1)),
            checked((ulong)(19000 + profile.MaxItemCount + 1)),
            maxRouterHops: 8,
            out Scalar16Scalar8RoutedInsertResult transform) ||
            transform.InsertResult != Scalar16Scalar8InsertResult.Inserted)
        {
            index.Session.AbortScalar16Scalar8WriteContext(staleWriter);
            throw new InvalidDataException("Generic SS16-8 stale-route setup could not force a shelf transform.");
        }

        long currentTarget = index.Session.FindRouterTarget(index.RootRouterOffset, rootPrefix);
        if (currentTarget != originalTarget ||
            index.Session.ClassifyScalar16Scalar8RouteTarget(currentTarget) != Scalar16Scalar8RouteTargetKind.Router)
        {
            index.Session.AbortScalar16Scalar8WriteContext(staleWriter);
            throw new InvalidDataException("Generic SS16-8 stale-route setup did not transform the original shelf offset into router topology.");
        }

        InvalidOperationException? staleRouteException = null;
        try
        {
            _ = index.Session.PublishScalar16Scalar8WriteContext(staleWriter);
        }
        catch (InvalidOperationException ex)
        {
            staleRouteException = ex;
            index.Session.AbortScalar16Scalar8WriteContext(staleWriter);
        }

        if (staleRouteException is null ||
            !staleRouteException.Message.Contains("route claim no longer matches", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Generic SS16-8 stale-route probe expected publish-time route-claim validation to reject stale shelf bytes.");
        }
    }

    /// <summary>
    /// Proves the `SS16-8` queued-writer facade reports real writer-context delete attribution after serialized insertion topology has split the route.<br/>
    /// This guards against the facade treating any successful Boolean delete as writer-context work without consulting the shape-specific delete path.<br/>
    /// </summary>
    private static void RunInternalGenericScalar16Scalar8PostSplitDeleteFacadeProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        using LibraDexIndex<Int128, long> index = catalog.Indexes["generic-ss16-8-post-split-delete"]["ids"].Int128Keys<long>().Create();
        Scalar16Scalar8Profile profile = index.GetScalar16Scalar8Profile();
        CreateScalar16Scalar8TransformSplitVectors(
            profile,
            out ulong[] keyHighs,
            out ulong[] keyLows,
            out _,
            out _,
            out _);
        for (int i = 0; i < profile.MaxItemCount; i++)
        {
            Int128 fillKey = LibraDexGenericScalarCodec<Int128>.Decode16(keyHighs[i], keyLows[i]);
            LibraDexGenericInsertResult fill = index.Insert(fillKey, 18000 + i);
            if (!fill.Inserted)
            {
                throw new InvalidDataException($"Generic SS16-8 post-split delete setup insert {i} failed.");
            }
        }

        const ulong splitKeyHigh = 0x0080_0000_0000_0000UL;
        ulong splitKeyLow = (ulong)profile.MaxItemCount;
        Int128 splitKey = LibraDexGenericScalarCodec<Int128>.Decode16(splitKeyHigh, splitKeyLow);
        long splitIdentity = 18000 + profile.MaxItemCount;
        LibraDexGenericInsertResult splitInsert = index.Insert(splitKey, splitIdentity);
        if (!splitInsert.Inserted ||
            splitInsert.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.NarrowTopologyPublisher)
        {
            throw new InvalidDataException($"Generic SS16-8 post-split delete setup expected serialized fallback insert but saw inserted={splitInsert.Inserted}, path={splitInsert.QueuedInsertPath}.");
        }

        LibraDexQueuedWriter<Int128, long> writer = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);
        LibraDexGenericDeleteResult delete = writer.Delete(splitKey, splitIdentity);
        if (!delete.Deleted ||
            delete.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext)
        {
            throw new InvalidDataException($"Generic SS16-8 post-split queued facade expected writer-context delete but saw deleted={delete.Deleted}, path={delete.QueuedInsertPath}.");
        }
    }

    /// <summary>
    /// Proves generic `SS8-16` cursor-local delete and SetKey converge through the owning index exact-mutation path.<br/>
    /// This transfers the cursor-mutation fix to wide-identity scalar indexes without claiming writer-context concurrency for this shape.<br/>
    /// </summary>
    private static void RunInternalGenericScalar8Scalar16CursorMutationConvergenceProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        Guid identityA = Guid.Parse("20000000-0000-0000-0000-000000000001");
        Guid identityB = Guid.Parse("20000000-0000-0000-0000-000000000002");
        Guid identityC = Guid.Parse("20000000-0000-0000-0000-000000000003");
        using LibraDexIndex<long, Guid> index = catalog.Indexes["generic-ss8-16-cursor"]["ids"].Int64Keys<Guid>().Create();
        _ = index.Insert(100, identityA);
        _ = index.Insert(101, identityB);
        _ = index.Insert(102, identityC);

        using (LibraDexRangeReader<long, Guid> deleteReader = index.OpenRangeReader(100, 100))
        {
            if (!deleteReader.MoveNext() ||
                !deleteReader.DeleteCurrent())
            {
                throw new InvalidDataException("Generic SS8-16 cursor delete convergence probe failed to delete the positioned tuple.");
            }
        }

        using (LibraDexRangeReader<long, Guid> setKeyReader = index.OpenRangeReader(101, 101))
        {
            if (!setKeyReader.MoveNext())
            {
                throw new InvalidDataException("Generic SS8-16 cursor SetKey convergence probe failed to position the reader.");
            }

            LibraDexGenericInsertResult result = setKeyReader.SetKey(200);
            if (!result.Inserted)
            {
                throw new InvalidDataException("Generic SS8-16 cursor SetKey convergence probe did not insert the replacement tuple.");
            }
        }

        Guid[] identities = new Guid[4];
        LibraDexGenericRangeReadResult deletedRead = index.ReadRange(100, 100, identities);
        if (deletedRead.IdentityCount != 0)
        {
            throw new InvalidDataException("Generic SS8-16 cursor delete convergence probe left the deleted tuple behind.");
        }

        Array.Clear(identities);
        LibraDexGenericRangeReadResult oldRead = index.ReadRange(101, 101, identities);
        if (oldRead.IdentityCount != 0)
        {
            throw new InvalidDataException("Generic SS8-16 cursor SetKey convergence probe left the old tuple behind.");
        }

        Array.Clear(identities);
        LibraDexGenericRangeReadResult survivorRead = index.ReadRange(102, 200, identities);
        if (survivorRead.IdentityCount != 2 ||
            identities[0] != identityC ||
            identities[1] != identityB)
        {
            throw new InvalidDataException("Generic SS8-16 cursor mutation convergence probe did not leave the expected surviving tuples.");
        }
    }

    /// <summary>
    /// Proves `SS8-16` can now use the generic queued-writer facade without adding caller ceremony.<br/>
    /// The facade delegates to the same direct writer-context insert/delete path as ordinary mutation, preserving path attribution for warmed shelf-local work.<br/>
    /// </summary>
    private static void RunInternalGenericScalar8Scalar16QueuedWriterBoundaryProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        Guid identityA = Guid.Parse("2c000000-0000-0000-0000-000000000001");
        Guid identityB = Guid.Parse("2c000000-0000-0000-0000-000000000002");
        using LibraDexIndex<long, Guid> index = catalog.Indexes["generic-ss8-16-queued-facade"]["ids"].Int64Keys<Guid>().Create();
        long baseKey = 1500;
        _ = index.Insert(baseKey, identityA);
        LibraDexQueuedWriter<long, Guid> writer = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);
        LibraDexGenericInsertResult insert = writer.Insert(baseKey + 1, identityB);
        if (!insert.Inserted ||
            insert.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext)
        {
            throw new InvalidDataException($"Generic SS8-16 queued writer facade expected writer-context insert but saw inserted={insert.Inserted}, path={insert.QueuedInsertPath}.");
        }

        LibraDexGenericDeleteResult delete = writer.Delete(baseKey, identityA);
        if (!delete.Deleted ||
            delete.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext)
        {
            throw new InvalidDataException($"Generic SS8-16 queued writer facade expected writer-context delete but saw deleted={delete.Deleted}, path={delete.QueuedInsertPath}.");
        }

        LibraDexGenericRekeyResult rekey = writer.Rekey(identityB, baseKey + 1, baseKey + 2);
        if (!rekey.Changed ||
            rekey.Replacement.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext ||
            rekey.Removal.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext)
        {
            throw new InvalidDataException($"Generic SS8-16 queued writer facade expected writer-context rekey but saw changed={rekey.Changed}, replacement={rekey.Replacement.QueuedInsertPath}, removal={rekey.Removal.QueuedInsertPath}.");
        }

        Guid[] identities = new Guid[4];
        LibraDexGenericRangeReadResult finalRead = index.ReadRange(baseKey, baseKey + 2, identities);
        if (finalRead.IdentityCount != 1 ||
            identities[0] != identityB)
        {
            throw new InvalidDataException("Generic SS8-16 queued writer facade delete/rekey readback failed.");
        }
    }

    /// <summary>
    /// Proves ordinary direct `SS8-16` inserts can overlap on warmed shelves through writer-context staging without caller-created queued writers.<br/>
    /// This is the first wide-identity transfer slice and covers only shelf-local mutations; topology fallback remains outside this proof.<br/>
    /// </summary>
    private static void RunInternalGenericScalar8Scalar16DirectConcurrentInsertProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        Guid leftIdentityA = Guid.Parse("21000000-0000-0000-0000-000000000001");
        Guid leftIdentityB = Guid.Parse("21000000-0000-0000-0000-000000000002");
        Guid rightIdentityA = Guid.Parse("22000000-0000-0000-0000-000000000001");
        Guid rightIdentityB = Guid.Parse("22000000-0000-0000-0000-000000000002");
        using LibraDexIndex<long, Guid> index = catalog.Indexes["generic-ss8-16-direct-insert"]["ids"].Int64Keys<Guid>().Create();
        long leftBase = 100;
        long rightBase = long.MinValue + 100;
        _ = index.Insert(leftBase, leftIdentityA);
        _ = index.Insert(rightBase, rightIdentityA);
        using ManualResetEventSlim startGate = new(false);
        using CountdownEvent readyGate = new(2);
        LibraDexGenericInsertResult insertA = default;
        LibraDexGenericInsertResult insertB = default;

        Task taskA = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            insertA = index.Insert(leftBase + 1, leftIdentityB);
        });

        Task taskB = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            insertB = index.Insert(rightBase + 1, rightIdentityB);
        });

        if (!readyGate.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidDataException("Generic SS8-16 direct concurrent insert probe could not ready both callers.");
        }

        startGate.Set();
        Task.WaitAll(taskA, taskB);

        if (!insertA.Inserted ||
            !insertB.Inserted ||
            insertA.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext ||
            insertB.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext)
        {
            throw new InvalidDataException($"Generic SS8-16 direct concurrent inserts expected writer-context paths but saw {insertA.Inserted}/{insertA.QueuedInsertPath} and {insertB.Inserted}/{insertB.QueuedInsertPath}.");
        }

        Guid[] identities = new Guid[4];
        LibraDexGenericRangeReadResult leftRead = index.ReadRange(leftBase, leftBase + 1, identities);
        if (leftRead.IdentityCount != 2 ||
            identities[0] != leftIdentityA ||
            identities[1] != leftIdentityB)
        {
            throw new InvalidDataException("Generic SS8-16 direct concurrent insert left-range readback failed.");
        }

        Array.Clear(identities);
        LibraDexGenericRangeReadResult rightRead = index.ReadRange(rightBase, rightBase + 1, identities);
        if (rightRead.IdentityCount != 2 ||
            identities[0] != rightIdentityA ||
            identities[1] != rightIdentityB)
        {
            throw new InvalidDataException("Generic SS8-16 direct concurrent insert right-range readback failed.");
        }
    }

    /// <summary>
    /// Proves ordinary direct `SS8-16` inserts targeting the same warmed shelf remain caller-safe under overlap.<br/>
    /// This path may retry internally on same-shelf writer ownership before publishing both shelf-local mutations.<br/>
    /// </summary>
    private static void RunInternalGenericScalar8Scalar16SameShelfContentionProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        Guid identityA = Guid.Parse("27000000-0000-0000-0000-000000000001");
        Guid identityB = Guid.Parse("27000000-0000-0000-0000-000000000002");
        Guid identityC = Guid.Parse("27000000-0000-0000-0000-000000000003");
        using LibraDexIndex<long, Guid> index = catalog.Indexes["generic-ss8-16-same-shelf"]["ids"].Int64Keys<Guid>().Create();
        long baseKey = 1300;
        _ = index.Insert(baseKey, identityA);
        using ManualResetEventSlim startGate = new(false);
        using CountdownEvent readyGate = new(2);
        LibraDexGenericInsertResult insertA = default;
        LibraDexGenericInsertResult insertB = default;

        Task taskA = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            insertA = index.Insert(baseKey + 1, identityB);
        });

        Task taskB = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            insertB = index.Insert(baseKey + 2, identityC);
        });

        if (!readyGate.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidDataException("Generic SS8-16 same-shelf contention probe could not ready both callers.");
        }

        startGate.Set();
        Task.WaitAll(taskA, taskB);

        if (!insertA.Inserted ||
            !insertB.Inserted ||
            insertA.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext ||
            insertB.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext)
        {
            throw new InvalidDataException($"Generic SS8-16 same-shelf inserts expected writer-context paths after any internal retry but saw {insertA.Inserted}/{insertA.QueuedInsertPath} and {insertB.Inserted}/{insertB.QueuedInsertPath}.");
        }

        Guid[] identities = new Guid[4];
        LibraDexGenericRangeReadResult read = index.ReadRange(baseKey, baseKey + 2, identities);
        if (read.IdentityCount != 3 ||
            identities[0] != identityA ||
            identities[1] != identityB ||
            identities[2] != identityC)
        {
            throw new InvalidDataException("Generic SS8-16 same-shelf contention readback failed.");
        }
    }

    /// <summary>
    /// Proves ordinary direct exact `SS8-16` deletes can overlap on warmed shelves through writer-context staging.<br/>
    /// Delete path attribution is verified by readback because the current public exact delete shape returns only a Boolean.<br/>
    /// </summary>
    private static void RunInternalGenericScalar8Scalar16DirectConcurrentDeleteProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        Guid leftIdentityA = Guid.Parse("23000000-0000-0000-0000-000000000001");
        Guid leftIdentityB = Guid.Parse("23000000-0000-0000-0000-000000000002");
        Guid rightIdentityA = Guid.Parse("24000000-0000-0000-0000-000000000001");
        Guid rightIdentityB = Guid.Parse("24000000-0000-0000-0000-000000000002");
        using LibraDexIndex<long, Guid> index = catalog.Indexes["generic-ss8-16-direct-delete"]["ids"].Int64Keys<Guid>().Create();
        long leftBase = 300;
        long rightBase = long.MinValue + 300;
        _ = index.Insert(leftBase, leftIdentityA);
        _ = index.Insert(leftBase + 1, leftIdentityB);
        _ = index.Insert(rightBase, rightIdentityA);
        _ = index.Insert(rightBase + 1, rightIdentityB);
        IIndex runtimeIndex = index;
        using ManualResetEventSlim startGate = new(false);
        using CountdownEvent readyGate = new(2);
        bool deleteA = false;
        bool deleteB = false;

        Task taskA = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            deleteA = runtimeIndex.Delete(leftBase, leftIdentityA);
        });

        Task taskB = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            deleteB = runtimeIndex.Delete(rightBase, rightIdentityA);
        });

        if (!readyGate.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidDataException("Generic SS8-16 direct concurrent delete probe could not ready both callers.");
        }

        startGate.Set();
        Task.WaitAll(taskA, taskB);

        if (!deleteA || !deleteB)
        {
            throw new InvalidDataException($"Generic SS8-16 direct concurrent deletes expected both tuples to be removed but saw {deleteA}/{deleteB}.");
        }

        Guid[] identities = new Guid[4];
        LibraDexGenericRangeReadResult leftRead = index.ReadRange(leftBase, leftBase + 1, identities);
        if (leftRead.IdentityCount != 1 ||
            identities[0] != leftIdentityB)
        {
            throw new InvalidDataException("Generic SS8-16 direct concurrent delete left-range readback failed.");
        }

        Array.Clear(identities);
        LibraDexGenericRangeReadResult rightRead = index.ReadRange(rightBase, rightBase + 1, identities);
        if (rightRead.IdentityCount != 1 ||
            identities[0] != rightIdentityB)
        {
            throw new InvalidDataException("Generic SS8-16 direct concurrent delete right-range readback failed.");
        }
    }

    /// <summary>
    /// Proves ordinary direct `SS8-16` rekey can overlap through the writer-context insert/delete legs.<br/>
    /// This is the direct patch/update shape where callers know both the old and replacement keys.<br/>
    /// </summary>
    private static void RunInternalGenericScalar8Scalar16DirectConcurrentRekeyProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        Guid leftIdentity = Guid.Parse("25000000-0000-0000-0000-000000000001");
        Guid rightIdentity = Guid.Parse("26000000-0000-0000-0000-000000000001");
        using LibraDexIndex<long, Guid> index = catalog.Indexes["generic-ss8-16-direct-rekey"]["ids"].Int64Keys<Guid>().Create();
        long leftBase = 700;
        long rightBase = long.MinValue + 700;
        _ = index.Insert(leftBase, leftIdentity);
        _ = index.Insert(rightBase, rightIdentity);
        IIndex runtimeIndex = index;
        using ManualResetEventSlim startGate = new(false);
        using CountdownEvent readyGate = new(2);
        bool rekeyA = false;
        bool rekeyB = false;

        Task taskA = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            rekeyA = runtimeIndex.Rekey(leftIdentity, leftBase, leftBase + 10);
        });

        Task taskB = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            rekeyB = runtimeIndex.Rekey(rightIdentity, rightBase, rightBase + 10);
        });

        if (!readyGate.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidDataException("Generic SS8-16 direct concurrent rekey probe could not ready both callers.");
        }

        startGate.Set();
        Task.WaitAll(taskA, taskB);

        if (!rekeyA || !rekeyB)
        {
            throw new InvalidDataException($"Generic SS8-16 direct concurrent rekeys expected both tuples to move but saw {rekeyA}/{rekeyB}.");
        }

        Guid[] identities = new Guid[4];
        LibraDexGenericRangeReadResult oldLeftRead = index.ReadRange(leftBase, leftBase, identities);
        if (oldLeftRead.IdentityCount != 0)
        {
            throw new InvalidDataException("Generic SS8-16 direct concurrent rekey left old left tuple behind.");
        }

        LibraDexGenericRangeReadResult oldRightRead = index.ReadRange(rightBase, rightBase, identities);
        if (oldRightRead.IdentityCount != 0)
        {
            throw new InvalidDataException("Generic SS8-16 direct concurrent rekey left old right tuple behind.");
        }

        Array.Clear(identities);
        LibraDexGenericRangeReadResult newLeftRead = index.ReadRange(leftBase + 10, leftBase + 10, identities);
        if (newLeftRead.IdentityCount != 1 ||
            identities[0] != leftIdentity)
        {
            throw new InvalidDataException("Generic SS8-16 direct concurrent rekey did not publish the left replacement tuple.");
        }

        Array.Clear(identities);
        LibraDexGenericRangeReadResult newRightRead = index.ReadRange(rightBase + 10, rightBase + 10, identities);
        if (newRightRead.IdentityCount != 1 ||
            identities[0] != rightIdentity)
        {
            throw new InvalidDataException("Generic SS8-16 direct concurrent rekey did not publish the right replacement tuple.");
        }
    }

    /// <summary>
    /// Proves criteria-scoped delete on `SS8-16` uses captured exact tuples instead of range rewrite when no batch/projection boundary blocks it.<br/>
    /// The exact-delete legs then inherit the warmed shelf-local writer-context path.<br/>
    /// </summary>
    private static void RunInternalGenericScalar8Scalar16CriteriaDeleteBridgeProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        CatalogIdentityGroupIndexes group = catalog.Indexes["generic-ss8-16-criteria-delete"];
        using LibraDexIndex<long, Guid> index = group["score"].Int64Keys<Guid>().Create();
        Guid leftIdentityA = Guid.Parse("28000000-0000-0000-0000-000000000001");
        Guid leftIdentityB = Guid.Parse("28000000-0000-0000-0000-000000000002");
        Guid rightIdentityA = Guid.Parse("29000000-0000-0000-0000-000000000001");
        Guid rightIdentityB = Guid.Parse("29000000-0000-0000-0000-000000000002");
        long leftBase = 900;
        long rightBase = long.MinValue + 900;
        _ = index.Insert(leftBase, leftIdentityA);
        _ = index.Insert(leftBase + 1, leftIdentityB);
        _ = index.Insert(rightBase, rightIdentityA);
        _ = index.Insert(rightBase + 1, rightIdentityB);
        Func<string, IIndex> resolver = name => string.Equals(name, "score", StringComparison.Ordinal)
            ? index
            : throw new InvalidDataException($"Unexpected SS8-16 criteria delete index '{name}'.");

        using ManualResetEventSlim startGate = new(false);
        using CountdownEvent readyGate = new(2);
        LibraDexIdentityMutationResult deleteA = default;
        LibraDexIdentityMutationResult deleteB = default;

        Task taskA = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            deleteA = group["score"].Delete(LibraDexCondition
                .ForGroup("generic-ss8-16-criteria-delete")
                .Index("score").AsInt64.EqualTo(leftBase)
                .EndCondition);
        });

        Task taskB = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            deleteB = group["score"].Delete(LibraDexCondition
                .ForGroup("generic-ss8-16-criteria-delete")
                .Index("score").AsInt64.EqualTo(rightBase)
                .EndCondition);
        });

        if (!readyGate.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidDataException("Generic SS8-16 criteria delete bridge probe could not ready both callers.");
        }

        startGate.Set();
        Task.WaitAll(taskA, taskB);

        if (deleteA.ChangedCount != 1 ||
            deleteB.ChangedCount != 1)
        {
            throw new InvalidDataException($"Generic SS8-16 criteria delete expected one changed tuple per caller but saw {deleteA.ChangedCount}/{deleteB.ChangedCount}.");
        }

        Guid[] identities = new Guid[4];
        LibraDexGenericRangeReadResult leftRead = index.ReadRange(leftBase, leftBase + 1, identities);
        if (leftRead.IdentityCount != 1 ||
            identities[0] != leftIdentityB)
        {
            throw new InvalidDataException("Generic SS8-16 criteria delete bridge left-range readback failed.");
        }

        Array.Clear(identities);
        LibraDexGenericRangeReadResult rightRead = index.ReadRange(rightBase, rightBase + 1, identities);
        if (rightRead.IdentityCount != 1 ||
            identities[0] != rightIdentityB)
        {
            throw new InvalidDataException("Generic SS8-16 criteria delete bridge right-range readback failed.");
        }
    }

    /// <summary>
    /// Proves criteria-scoped `SetKey` on `SS8-16` overlaps through captured tuple insert/delete legs.<br/>
    /// No production change is expected here after the direct writer-context slice because condition SetKey already converges on `Insert` plus exact delete.<br/>
    /// </summary>
    private static void RunInternalGenericScalar8Scalar16CriteriaSetKeyBridgeProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        CatalogIdentityGroupIndexes group = catalog.Indexes["generic-ss8-16-criteria-setkey"];
        using LibraDexIndex<long, Guid> index = group["score"].Int64Keys<Guid>().Create();
        Guid leftIdentity = Guid.Parse("2a000000-0000-0000-0000-000000000001");
        Guid rightIdentity = Guid.Parse("2b000000-0000-0000-0000-000000000001");
        long leftBase = 1100;
        long rightBase = long.MinValue + 1100;
        _ = index.Insert(leftBase, leftIdentity);
        _ = index.Insert(rightBase, rightIdentity);
        Func<string, IIndex> resolver = name => string.Equals(name, "score", StringComparison.Ordinal)
            ? index
            : throw new InvalidDataException($"Unexpected SS8-16 criteria SetKey index '{name}'.");

        using ManualResetEventSlim startGate = new(false);
        using CountdownEvent readyGate = new(2);
        LibraDexIdentityMutationResult setKeyA = default;
        LibraDexIdentityMutationResult setKeyB = default;

        Task taskA = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            setKeyA = LibraDexCondition
                .ForGroup("generic-ss8-16-criteria-setkey")
                .Index("score").AsInt64.EqualTo(leftBase)
                .EndCondition
                .Materialize(resolver)
                .Mutate
                .SetKey(leftBase + 10)
                .Execute();
        });

        Task taskB = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            setKeyB = LibraDexCondition
                .ForGroup("generic-ss8-16-criteria-setkey")
                .Index("score").AsInt64.EqualTo(rightBase)
                .EndCondition
                .Materialize(resolver)
                .Mutate
                .SetKey(rightBase + 10)
                .Execute();
        });

        if (!readyGate.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidDataException("Generic SS8-16 criteria SetKey bridge probe could not ready both callers.");
        }

        startGate.Set();
        Task.WaitAll(taskA, taskB);

        if (setKeyA.ChangedCount != 1 ||
            setKeyB.ChangedCount != 1)
        {
            throw new InvalidDataException($"Generic SS8-16 criteria SetKey expected one changed tuple per caller but saw {setKeyA.ChangedCount}/{setKeyB.ChangedCount}.");
        }

        Guid[] identities = new Guid[4];
        LibraDexGenericRangeReadResult oldLeftRead = index.ReadRange(leftBase, leftBase, identities);
        if (oldLeftRead.IdentityCount != 0)
        {
            throw new InvalidDataException("Generic SS8-16 criteria SetKey left old left tuple behind.");
        }

        LibraDexGenericRangeReadResult oldRightRead = index.ReadRange(rightBase, rightBase, identities);
        if (oldRightRead.IdentityCount != 0)
        {
            throw new InvalidDataException("Generic SS8-16 criteria SetKey left old right tuple behind.");
        }

        Array.Clear(identities);
        LibraDexGenericRangeReadResult newLeftRead = index.ReadRange(leftBase + 10, leftBase + 10, identities);
        if (newLeftRead.IdentityCount != 1 ||
            identities[0] != leftIdentity)
        {
            throw new InvalidDataException("Generic SS8-16 criteria SetKey did not publish the left replacement tuple.");
        }

        Array.Clear(identities);
        LibraDexGenericRangeReadResult newRightRead = index.ReadRange(rightBase + 10, rightBase + 10, identities);
        if (newRightRead.IdentityCount != 1 ||
            identities[0] != rightIdentity)
        {
            throw new InvalidDataException("Generic SS8-16 criteria SetKey did not publish the right replacement tuple.");
        }
    }

    /// <summary>
    /// Proves cold-route `SS8-16` direct insert uses the narrow serialized-topology path instead of pretending writer-context route publication is shelf-local.<br/>
    /// This keeps the wide-identity writer-context slice honest: initial root-route setup is narrowed, while split/transform work remains serialized topology work.<br/>
    /// </summary>
    private static void RunInternalGenericScalar8Scalar16ColdRouteFallbackProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        Guid identityA = Guid.Parse("2d000000-0000-0000-0000-000000000001");
        Guid identityB = Guid.Parse("2d000000-0000-0000-0000-000000000002");
        using LibraDexIndex<long, Guid> index = catalog.Indexes["generic-ss8-16-cold-route"]["ids"].Int64Keys<Guid>().Create();
        _ = index.Insert(500, identityA);
        long coldKey = long.MinValue + 500;
        LibraDexGenericInsertResult insert = index.Insert(coldKey, identityB);
        if (!insert.Inserted ||
            !insert.CreatedInitialShelfRoute ||
            insert.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.NarrowTopologyPublisher)
        {
            throw new InvalidDataException($"Generic SS8-16 cold route expected narrow serialized route creation but saw inserted={insert.Inserted}, created={insert.CreatedInitialShelfRoute}, path={insert.QueuedInsertPath}.");
        }

        Guid[] identities = new Guid[2];
        LibraDexGenericRangeReadResult read = index.ReadRange(coldKey, coldKey, identities);
        if (read.IdentityCount != 1 ||
            identities[0] != identityB)
        {
            throw new InvalidDataException("Generic SS8-16 cold route fallback readback failed.");
        }
    }

    /// <summary>
    /// Proves full-shelf `SS8-16` direct insert uses the serialized topology fallback without entering the old one-item batch path.<br/>
    /// The fallback may split or transform the routed shelf, but the caller-facing contract is stable insertion with serialized-fallback attribution.<br/>
    /// </summary>
    private static void RunInternalGenericScalar8Scalar16FullShelfSerializedTopologyProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        using LibraDexIndex<long, Guid> index = catalog.Indexes["generic-ss8-16-full-shelf-topology"]["ids"].Int64Keys<Guid>().Create();
        Scalar8Scalar16Profile profile = index.GetScalar8Scalar16Profile();
        long baseKey = 1700;
        for (int i = 0; i < profile.MaxItemCount; i++)
        {
            Guid identity = Guid.Parse($"2e000000-0000-0000-0000-{(i + 1):000000000000}");
            LibraDexGenericInsertResult fill = index.Insert(baseKey + i, identity);
            if (!fill.Inserted)
            {
                throw new InvalidDataException($"Generic SS8-16 full-shelf topology setup insert {i} failed.");
            }
        }

        Guid overflowIdentity = Guid.Parse("2e000000-0000-0000-0001-000000000001");
        LibraDexGenericInsertResult overflow = index.Insert(baseKey + profile.MaxItemCount, overflowIdentity);
        if (!overflow.Inserted ||
            overflow.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.NarrowTopologyPublisher)
        {
            throw new InvalidDataException($"Generic SS8-16 full-shelf topology expected serialized fallback but saw inserted={overflow.Inserted}, path={overflow.QueuedInsertPath}.");
        }
    }

    /// <summary>
    /// Proves `SS8-16` writer-context publication rejects a staged shelf after the selected physical shelf has been transformed into deeper route topology.<br/>
    /// This guards the primitive concurrency contract that shelf-local writers may publish only while the route still selects the same committed shelf shape.<br/>
    /// </summary>
    private static void RunInternalGenericScalar8Scalar16StaleRouteClaimProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        using LibraDexIndex<long, Guid> index = catalog.Indexes["generic-ss8-16-stale-route-claim"]["ids"].Int64Keys<Guid>().Create();
        Scalar8Scalar16Profile profile = index.GetScalar8Scalar16Profile();
        const ulong encodedKeyBase = 0x5600_0000_0000_0000UL;
        const ulong stagedKey = encodedKeyBase + 0x7FFF;
        byte rootPrefix = (byte)(encodedKeyBase >> 56);
        Guid seedIdentity = Guid.Parse("3e000000-0000-0000-0000-000000000001");
        LibraDexGenericScalarCodec<Guid>.Encode16(seedIdentity, out ulong seedIdentityHigh, out ulong seedIdentityLow);

        LibraDexGenericInsertResult seed = index.Insert(
            LibraDexGenericScalarCodec<long>.Decode8(encodedKeyBase),
            seedIdentity);
        if (!seed.Inserted)
        {
            throw new InvalidDataException("Generic SS8-16 stale-route setup failed to seed the initial shelf.");
        }

        long originalTarget = index.Session.FindRouterTarget(index.RootRouterOffset, rootPrefix);
        if (originalTarget == 0)
        {
            throw new InvalidDataException("Generic SS8-16 stale-route setup did not create the expected root-prefix shelf.");
        }

        LibraDexWriteContext staleWriter = index.Session.BeginScalar8Scalar16WriteContext();
        Scalar8Scalar16RoutedInsertResult staged = index.Session.InsertWalkedRoutedScalar8Scalar16NoSplitForWriteContext(
            staleWriter,
            index.RootRouterOffset,
            profile,
            stagedKey,
            seedIdentityHigh + 1,
            seedIdentityLow + 1,
            allowDuplicateKeys: true,
            maxRouterHops: 8);
        if (staged.InsertResult != Scalar8Scalar16InsertResult.Inserted ||
            staged.PrimaryOffset != originalTarget)
        {
            index.Session.AbortScalar8Scalar16WriteContext(staleWriter);
            throw new InvalidDataException("Generic SS8-16 stale-route setup failed to stage the expected writer-context shelf image.");
        }

        for (int i = 1; i < profile.MaxItemCount; i++)
        {
            Scalar8Scalar16RoutedInsertResult fill = index.Session.InsertWalkedRoutedScalar8Scalar16(
                index.RootRouterOffset,
                profile,
                encodedKeyBase + checked((ulong)i),
                seedIdentityHigh + checked((ulong)(i + 2)),
                seedIdentityLow + checked((ulong)(i + 2)),
                allowDuplicateKeys: true,
                maxRouterHops: 8);
            if (fill.InsertResult != Scalar8Scalar16InsertResult.Inserted)
            {
                index.Session.AbortScalar8Scalar16WriteContext(staleWriter);
                throw new InvalidDataException($"Generic SS8-16 stale-route setup fill insert {i} failed with {fill.InsertResult}.");
            }
        }

        if (!index.Session.TryTransformScalar8Scalar16DirectShelf(
            index.RootRouterOffset,
            profile,
            encodedKeyBase + checked((ulong)(profile.MaxItemCount + 1)),
            seedIdentityHigh + checked((ulong)(profile.MaxItemCount + 3)),
            seedIdentityLow + checked((ulong)(profile.MaxItemCount + 3)),
            maxRouterHops: 8,
            out Scalar8Scalar16RoutedInsertResult transform) ||
            transform.InsertResult != Scalar8Scalar16InsertResult.Inserted)
        {
            index.Session.AbortScalar8Scalar16WriteContext(staleWriter);
            throw new InvalidDataException("Generic SS8-16 stale-route setup could not force a shelf transform.");
        }

        long currentTarget = index.Session.FindRouterTarget(index.RootRouterOffset, rootPrefix);
        if (currentTarget != originalTarget ||
            index.Session.ClassifyScalar8Scalar16RouteTarget(currentTarget) != Scalar8Scalar16RouteTargetKind.Router)
        {
            index.Session.AbortScalar8Scalar16WriteContext(staleWriter);
            throw new InvalidDataException("Generic SS8-16 stale-route setup did not transform the original shelf offset into router topology.");
        }

        InvalidOperationException? staleRouteException = null;
        try
        {
            _ = index.Session.PublishScalar8Scalar16WriteContext(staleWriter);
        }
        catch (InvalidOperationException ex)
        {
            staleRouteException = ex;
            index.Session.AbortScalar8Scalar16WriteContext(staleWriter);
        }

        if (staleRouteException is null ||
            !staleRouteException.Message.Contains("route claim no longer matches", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Generic SS8-16 stale-route probe expected publish-time route-claim validation to reject stale shelf bytes.");
        }
    }

    /// <summary>
    /// Proves generic `SS16-16` cursor-local delete and SetKey converge through the owning index exact-mutation path.<br/>
    /// This transfers the cursor-mutation fix to wide-key/wide-identity scalar indexes without claiming writer-context concurrency for this shape.<br/>
    /// </summary>
    private static void RunInternalGenericScalar16Scalar16CursorMutationConvergenceProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        Guid keyA = Guid.Parse("30000000-0000-0000-0000-000000000001");
        Guid keyB = Guid.Parse("30000000-0000-0000-0000-000000000002");
        Guid keyC = Guid.Parse("30000000-0000-0000-0000-000000000003");
        Guid keyD = Guid.Parse("30000000-0000-0000-0000-000000000004");
        Guid identityA = Guid.Parse("40000000-0000-0000-0000-000000000001");
        Guid identityB = Guid.Parse("40000000-0000-0000-0000-000000000002");
        Guid identityC = Guid.Parse("40000000-0000-0000-0000-000000000003");
        using LibraDexIndex<Guid, Guid> index = catalog.Indexes["generic-ss16-16-cursor"]["ids"].GuidKeys<Guid>().Create();
        _ = index.Insert(keyA, identityA);
        _ = index.Insert(keyB, identityB);
        _ = index.Insert(keyC, identityC);

        using (LibraDexRangeReader<Guid, Guid> deleteReader = index.OpenRangeReader(keyA, keyA))
        {
            if (!deleteReader.MoveNext() ||
                !deleteReader.DeleteCurrent())
            {
                throw new InvalidDataException("Generic SS16-16 cursor delete convergence probe failed to delete the positioned tuple.");
            }
        }

        using (LibraDexRangeReader<Guid, Guid> setKeyReader = index.OpenRangeReader(keyB, keyB))
        {
            if (!setKeyReader.MoveNext())
            {
                throw new InvalidDataException("Generic SS16-16 cursor SetKey convergence probe failed to position the reader.");
            }

            LibraDexGenericInsertResult result = setKeyReader.SetKey(keyD);
            if (!result.Inserted)
            {
                throw new InvalidDataException("Generic SS16-16 cursor SetKey convergence probe did not insert the replacement tuple.");
            }
        }

        Guid[] identities = new Guid[4];
        LibraDexGenericRangeReadResult deletedRead = index.ReadRange(keyA, keyA, identities);
        if (deletedRead.IdentityCount != 0)
        {
            throw new InvalidDataException("Generic SS16-16 cursor delete convergence probe left the deleted tuple behind.");
        }

        Array.Clear(identities);
        LibraDexGenericRangeReadResult oldRead = index.ReadRange(keyB, keyB, identities);
        if (oldRead.IdentityCount != 0)
        {
            throw new InvalidDataException("Generic SS16-16 cursor SetKey convergence probe left the old tuple behind.");
        }

        Array.Clear(identities);
        LibraDexGenericRangeReadResult survivorRead = index.ReadRange(keyC, keyD, identities);
        if (survivorRead.IdentityCount != 2 ||
            identities[0] != identityC ||
            identities[1] != identityB)
        {
            throw new InvalidDataException("Generic SS16-16 cursor mutation convergence probe did not leave the expected surviving tuples.");
        }
    }

    /// <summary>
    /// Proves `SS16-16` can now use the generic queued-writer facade without adding caller ceremony.<br/>
    /// The facade delegates to the same direct writer-context insert/delete path as ordinary mutation, preserving path attribution for warmed shelf-local work.<br/>
    /// </summary>
    private static void RunInternalGenericScalar16Scalar16QueuedWriterBoundaryProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions { DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed });
        using LibraDexIndex<Guid, Guid> index = catalog.Indexes["generic-ss16-16-queued-facade"]["ids"].GuidKeys<Guid>().Create();
        Guid keyA = Guid.Parse("37000000-0000-0000-0000-000000000001");
        Guid keyB = Guid.Parse("37000000-0000-0000-0000-000000000002");
        Guid keyC = Guid.Parse("37000000-0000-0000-0000-000000000003");
        Guid identityA = Guid.Parse("4a000000-0000-0000-0000-000000000001");
        Guid identityB = Guid.Parse("4a000000-0000-0000-0000-000000000002");
        _ = index.Insert(keyA, identityA);
        LibraDexQueuedWriter<Guid, Guid> writer = index.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);
        LibraDexGenericInsertResult insert = writer.Insert(keyB, identityB);
        if (!insert.Inserted || insert.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext)
        {
            throw new InvalidDataException($"Generic SS16-16 queued writer facade expected writer-context insert but saw inserted={insert.Inserted}, path={insert.QueuedInsertPath}.");
        }

        LibraDexGenericDeleteResult delete = writer.Delete(keyA, identityA);
        if (!delete.Deleted || delete.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext)
        {
            throw new InvalidDataException($"Generic SS16-16 queued writer facade expected writer-context delete but saw deleted={delete.Deleted}, path={delete.QueuedInsertPath}.");
        }

        LibraDexGenericRekeyResult rekey = writer.Rekey(identityB, keyB, keyC);
        if (!rekey.Changed ||
            rekey.Replacement.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext ||
            rekey.Removal.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext)
        {
            throw new InvalidDataException($"Generic SS16-16 queued writer facade expected writer-context rekey but saw changed={rekey.Changed}, replacement={rekey.Replacement.QueuedInsertPath}, removal={rekey.Removal.QueuedInsertPath}.");
        }
    }

    private static void RunInternalGenericScalar16Scalar16DirectConcurrentInsertProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions { DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed });
        using LibraDexIndex<Guid, Guid> index = catalog.Indexes["generic-ss16-16-direct-insert"]["ids"].GuidKeys<Guid>().Create();
        Guid leftKey = Guid.Parse("31000000-0000-0000-0000-000000000001");
        Guid rightKey = Guid.Parse("b1000000-0000-0000-0000-000000000001");
        Guid leftA = Guid.Parse("41000000-0000-0000-0000-000000000001");
        Guid leftB = Guid.Parse("41000000-0000-0000-0000-000000000002");
        Guid rightA = Guid.Parse("42000000-0000-0000-0000-000000000001");
        Guid rightB = Guid.Parse("42000000-0000-0000-0000-000000000002");
        _ = index.Insert(leftKey, leftA);
        _ = index.Insert(rightKey, rightA);
        using ManualResetEventSlim startGate = new(false);
        using CountdownEvent readyGate = new(2);
        LibraDexGenericInsertResult insertA = default;
        LibraDexGenericInsertResult insertB = default;
        Task taskA = Task.Run(() => { readyGate.Signal(); startGate.Wait(); insertA = index.Insert(Guid.Parse("31000000-0000-0000-0000-000000000002"), leftB); });
        Task taskB = Task.Run(() => { readyGate.Signal(); startGate.Wait(); insertB = index.Insert(Guid.Parse("b1000000-0000-0000-0000-000000000002"), rightB); });
        if (!readyGate.Wait(TimeSpan.FromSeconds(10))) throw new InvalidDataException("Generic SS16-16 direct concurrent insert probe could not ready both callers.");
        startGate.Set();
        Task.WaitAll(taskA, taskB);
        if (!insertA.Inserted || !insertB.Inserted || insertA.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext || insertB.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext)
        {
            throw new InvalidDataException($"Generic SS16-16 direct concurrent inserts expected writer-context paths but saw {insertA.Inserted}/{insertA.QueuedInsertPath} and {insertB.Inserted}/{insertB.QueuedInsertPath}.");
        }
    }

    private static void RunInternalGenericScalar16Scalar16SameShelfContentionProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions { DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed });
        using LibraDexIndex<Guid, Guid> index = catalog.Indexes["generic-ss16-16-same-shelf"]["ids"].GuidKeys<Guid>().Create();
        Guid keyA = Guid.Parse("32000000-0000-0000-0000-000000000001");
        Guid keyB = Guid.Parse("32000000-0000-0000-0000-000000000002");
        Guid keyC = Guid.Parse("32000000-0000-0000-0000-000000000003");
        Guid identityA = Guid.Parse("43000000-0000-0000-0000-000000000001");
        Guid identityB = Guid.Parse("43000000-0000-0000-0000-000000000002");
        Guid identityC = Guid.Parse("43000000-0000-0000-0000-000000000003");
        _ = index.Insert(keyA, identityA);
        using ManualResetEventSlim startGate = new(false);
        using CountdownEvent readyGate = new(2);
        LibraDexGenericInsertResult insertA = default;
        LibraDexGenericInsertResult insertB = default;
        Task taskA = Task.Run(() => { readyGate.Signal(); startGate.Wait(); insertA = index.Insert(keyB, identityB); });
        Task taskB = Task.Run(() => { readyGate.Signal(); startGate.Wait(); insertB = index.Insert(keyC, identityC); });
        if (!readyGate.Wait(TimeSpan.FromSeconds(10))) throw new InvalidDataException("Generic SS16-16 same-shelf contention probe could not ready both callers.");
        startGate.Set();
        Task.WaitAll(taskA, taskB);
        if (!insertA.Inserted || !insertB.Inserted || insertA.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext || insertB.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext)
        {
            throw new InvalidDataException($"Generic SS16-16 same-shelf inserts expected writer-context paths but saw {insertA.Inserted}/{insertA.QueuedInsertPath} and {insertB.Inserted}/{insertB.QueuedInsertPath}.");
        }
    }

    private static void RunInternalGenericScalar16Scalar16DirectConcurrentDeleteProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions { DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed });
        using LibraDexIndex<Guid, Guid> index = catalog.Indexes["generic-ss16-16-direct-delete"]["ids"].GuidKeys<Guid>().Create();
        Guid leftKey = Guid.Parse("33000000-0000-0000-0000-000000000001");
        Guid rightKey = Guid.Parse("b3000000-0000-0000-0000-000000000001");
        Guid leftIdentity = Guid.Parse("44000000-0000-0000-0000-000000000001");
        Guid rightIdentity = Guid.Parse("45000000-0000-0000-0000-000000000001");
        _ = index.Insert(leftKey, leftIdentity);
        _ = index.Insert(rightKey, rightIdentity);
        IIndex runtimeIndex = index;
        using ManualResetEventSlim startGate = new(false);
        using CountdownEvent readyGate = new(2);
        bool deleteA = false;
        bool deleteB = false;
        Task taskA = Task.Run(() => { readyGate.Signal(); startGate.Wait(); deleteA = runtimeIndex.Delete(leftKey, leftIdentity); });
        Task taskB = Task.Run(() => { readyGate.Signal(); startGate.Wait(); deleteB = runtimeIndex.Delete(rightKey, rightIdentity); });
        if (!readyGate.Wait(TimeSpan.FromSeconds(10))) throw new InvalidDataException("Generic SS16-16 direct concurrent delete probe could not ready both callers.");
        startGate.Set();
        Task.WaitAll(taskA, taskB);
        if (!deleteA || !deleteB) throw new InvalidDataException($"Generic SS16-16 direct concurrent deletes expected both tuples to be removed but saw {deleteA}/{deleteB}.");
    }

    private static void RunInternalGenericScalar16Scalar16DirectConcurrentRekeyProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions { DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed });
        using LibraDexIndex<Guid, Guid> index = catalog.Indexes["generic-ss16-16-direct-rekey"]["ids"].GuidKeys<Guid>().Create();
        Guid leftKey = Guid.Parse("34000000-0000-0000-0000-000000000001");
        Guid rightKey = Guid.Parse("b4000000-0000-0000-0000-000000000001");
        Guid leftNew = Guid.Parse("34000000-0000-0000-0000-000000000002");
        Guid rightNew = Guid.Parse("b4000000-0000-0000-0000-000000000002");
        Guid leftIdentity = Guid.Parse("46000000-0000-0000-0000-000000000001");
        Guid rightIdentity = Guid.Parse("47000000-0000-0000-0000-000000000001");
        _ = index.Insert(leftKey, leftIdentity);
        _ = index.Insert(rightKey, rightIdentity);
        IIndex runtimeIndex = index;
        using ManualResetEventSlim startGate = new(false);
        using CountdownEvent readyGate = new(2);
        bool rekeyA = false;
        bool rekeyB = false;
        Task taskA = Task.Run(() => { readyGate.Signal(); startGate.Wait(); rekeyA = runtimeIndex.Rekey(leftIdentity, leftKey, leftNew); });
        Task taskB = Task.Run(() => { readyGate.Signal(); startGate.Wait(); rekeyB = runtimeIndex.Rekey(rightIdentity, rightKey, rightNew); });
        if (!readyGate.Wait(TimeSpan.FromSeconds(10))) throw new InvalidDataException("Generic SS16-16 direct concurrent rekey probe could not ready both callers.");
        startGate.Set();
        Task.WaitAll(taskA, taskB);
        if (!rekeyA || !rekeyB) throw new InvalidDataException($"Generic SS16-16 direct concurrent rekeys expected both tuples to move but saw {rekeyA}/{rekeyB}.");
    }

    private static void RunInternalGenericScalar16Scalar16CriteriaDeleteBridgeProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions { DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed });
        CatalogIdentityGroupIndexes group = catalog.Indexes["generic-ss16-16-criteria-delete"];
        using LibraDexIndex<Guid, Guid> index = group["score"].GuidKeys<Guid>().Create();
        Guid key = Guid.Parse("35000000-0000-0000-0000-000000000001");
        Guid identity = Guid.Parse("48000000-0000-0000-0000-000000000001");
        _ = index.Insert(key, identity);
        Func<string, IIndex> resolver = name => string.Equals(name, "score", StringComparison.Ordinal) ? index : throw new InvalidDataException($"Unexpected SS16-16 criteria delete index '{name}'.");
        LibraDexIdentityMutationResult delete = group["score"].Delete(
            LibraDexCondition.ForGroup("generic-ss16-16-criteria-delete").Index("score").AsGuid.EqualTo(key).EndCondition);
        if (delete.ChangedCount != 1) throw new InvalidDataException($"Generic SS16-16 criteria delete expected one changed tuple but saw {delete.ChangedCount}.");
    }

    private static void RunInternalGenericScalar16Scalar16CriteriaSetKeyBridgeProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions { DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed });
        CatalogIdentityGroupIndexes group = catalog.Indexes["generic-ss16-16-criteria-setkey"];
        using LibraDexIndex<Guid, Guid> index = group["score"].GuidKeys<Guid>().Create();
        Guid key = Guid.Parse("36000000-0000-0000-0000-000000000001");
        Guid newKey = Guid.Parse("36000000-0000-0000-0000-000000000002");
        Guid identity = Guid.Parse("49000000-0000-0000-0000-000000000001");
        _ = index.Insert(key, identity);
        Func<string, IIndex> resolver = name => string.Equals(name, "score", StringComparison.Ordinal) ? index : throw new InvalidDataException($"Unexpected SS16-16 criteria SetKey index '{name}'.");
        LibraDexIdentityMutationResult setKey = LibraDexCondition.ForGroup("generic-ss16-16-criteria-setkey").Index("score").AsGuid.EqualTo(key).EndCondition.Materialize(resolver).Mutate.SetKey(newKey).Execute();
        if (setKey.ChangedCount != 1) throw new InvalidDataException($"Generic SS16-16 criteria SetKey expected one changed tuple but saw {setKey.ChangedCount}.");
    }

    private static void RunInternalGenericScalar16Scalar16ColdRouteFallbackProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions { DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed });
        using LibraDexIndex<Int128, Guid> index = catalog.Indexes["generic-ss16-16-cold-route"]["ids"].Int128Keys<Guid>().Create();
        Guid identityA = Guid.Parse("4b000000-0000-0000-0000-000000000001");
        Guid identityB = Guid.Parse("4b000000-0000-0000-0000-000000000002");
        _ = index.Insert(500, identityA);
        Int128 coldKey = ((Int128)long.MinValue << 64) + 500;
        LibraDexGenericInsertResult insert = index.Insert(coldKey, identityB);
        if (!insert.Inserted || !insert.CreatedInitialShelfRoute || insert.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.NarrowTopologyPublisher)
        {
            throw new InvalidDataException($"Generic SS16-16 cold route expected narrow serialized route creation but saw inserted={insert.Inserted}, created={insert.CreatedInitialShelfRoute}, path={insert.QueuedInsertPath}.");
        }
    }

    private static void RunInternalGenericScalar16Scalar16FullShelfSerializedTopologyProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions { DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed });
        using LibraDexIndex<Int128, Guid> index = catalog.Indexes["generic-ss16-16-full-shelf-topology"]["ids"].Int128Keys<Guid>().Create();
        Scalar16Scalar16Profile profile = index.GetScalar16Scalar16Profile();
        Int128 baseKey = 1700;
        for (int i = 0; i < profile.MaxItemCount; i++)
        {
            Int128 key = baseKey + i;
            Guid identity = Guid.Parse($"4c000000-0000-0000-0000-{(i + 1):000000000000}");
            LibraDexGenericInsertResult fill = index.Insert(key, identity);
            if (!fill.Inserted)
            {
                throw new InvalidDataException($"Generic SS16-16 full-shelf topology setup insert {i} failed.");
            }
        }

        Int128 overflowKey = baseKey + profile.MaxItemCount;
        Guid overflowIdentity = Guid.Parse("4c000000-0000-0000-0001-000000000001");
        LibraDexGenericInsertResult overflow = index.Insert(overflowKey, overflowIdentity);
        if (!overflow.Inserted || overflow.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.NarrowTopologyPublisher)
        {
            throw new InvalidDataException($"Generic SS16-16 full-shelf topology expected serialized fallback but saw inserted={overflow.Inserted}, path={overflow.QueuedInsertPath}.");
        }
    }

    /// <summary>
    /// Proves `SS16-16` writer-context publication rejects a staged shelf after the selected physical shelf has been transformed into deeper route topology.<br/>
    /// This guards the primitive concurrency contract that shelf-local writers may publish only while the route still selects the same committed shelf shape.<br/>
    /// </summary>
    private static void RunInternalGenericScalar16Scalar16StaleRouteClaimProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions { DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed });
        using LibraDexIndex<Int128, Guid> index = catalog.Indexes["generic-ss16-16-stale-route-claim"]["ids"].Int128Keys<Guid>().Create();
        Scalar16Scalar16Profile profile = index.GetScalar16Scalar16Profile();
        const ulong keyHigh = 0x5700_0000_0000_0000UL;
        const ulong seedKeyLow = 1;
        const ulong stagedKeyLow = 0x7FFF;
        byte rootPrefix = (byte)(keyHigh >> 56);
        Guid seedIdentity = Guid.Parse("4d000000-0000-0000-0000-000000000001");
        LibraDexGenericScalarCodec<Guid>.Encode16(seedIdentity, out ulong seedIdentityHigh, out ulong seedIdentityLow);

        LibraDexGenericInsertResult seed = index.Insert(
            LibraDexGenericScalarCodec<Int128>.Decode16(keyHigh, seedKeyLow),
            seedIdentity);
        if (!seed.Inserted)
        {
            throw new InvalidDataException("Generic SS16-16 stale-route setup failed to seed the initial shelf.");
        }

        long originalTarget = index.Session.FindRouterTarget(index.RootRouterOffset, rootPrefix);
        if (originalTarget == 0)
        {
            throw new InvalidDataException("Generic SS16-16 stale-route setup did not create the expected root-prefix shelf.");
        }

        LibraDexWriteContext staleWriter = index.Session.BeginScalar16Scalar16WriteContext();
        Scalar16Scalar16RoutedInsertResult staged = index.Session.InsertWalkedRoutedScalar16Scalar16NoSplitForWriteContext(
            staleWriter,
            index.RootRouterOffset,
            profile,
            keyHigh,
            stagedKeyLow,
            seedIdentityHigh + 1,
            seedIdentityLow + 1,
            allowDuplicateKeys: true,
            maxRouterHops: 8);
        if (staged.InsertResult != Scalar16Scalar16InsertResult.Inserted ||
            staged.PrimaryOffset != originalTarget)
        {
            index.Session.AbortScalar16Scalar16WriteContext(staleWriter);
            throw new InvalidDataException("Generic SS16-16 stale-route setup failed to stage the expected writer-context shelf image.");
        }

        for (int i = 2; i <= profile.MaxItemCount; i++)
        {
            Scalar16Scalar16RoutedInsertResult fill = index.Session.InsertWalkedRoutedScalar16Scalar16(
                index.RootRouterOffset,
                profile,
                keyHigh,
                checked((ulong)i),
                seedIdentityHigh + checked((ulong)(i + 1)),
                seedIdentityLow + checked((ulong)(i + 1)),
                allowDuplicateKeys: true,
                maxRouterHops: 8);
            if (fill.InsertResult != Scalar16Scalar16InsertResult.Inserted)
            {
                index.Session.AbortScalar16Scalar16WriteContext(staleWriter);
                throw new InvalidDataException($"Generic SS16-16 stale-route setup fill insert {i} failed with {fill.InsertResult}.");
            }
        }

        if (!index.Session.TryTransformScalar16Scalar16DirectShelf(
            index.RootRouterOffset,
            profile,
            keyHigh,
            checked((ulong)(profile.MaxItemCount + 1)),
            seedIdentityHigh + checked((ulong)(profile.MaxItemCount + 2)),
            seedIdentityLow + checked((ulong)(profile.MaxItemCount + 2)),
            maxRouterHops: 8,
            out Scalar16Scalar16RoutedInsertResult transform) ||
            transform.InsertResult != Scalar16Scalar16InsertResult.Inserted)
        {
            index.Session.AbortScalar16Scalar16WriteContext(staleWriter);
            throw new InvalidDataException("Generic SS16-16 stale-route setup could not force a shelf transform.");
        }

        long currentTarget = index.Session.FindRouterTarget(index.RootRouterOffset, rootPrefix);
        if (currentTarget != originalTarget ||
            index.Session.ClassifyScalar16Scalar16RouteTarget(currentTarget) != Scalar16Scalar16RouteTargetKind.Router)
        {
            index.Session.AbortScalar16Scalar16WriteContext(staleWriter);
            throw new InvalidDataException("Generic SS16-16 stale-route setup did not transform the original shelf offset into router topology.");
        }

        InvalidOperationException? staleRouteException = null;
        try
        {
            _ = index.Session.PublishScalar16Scalar16WriteContext(staleWriter);
        }
        catch (InvalidOperationException ex)
        {
            staleRouteException = ex;
            index.Session.AbortScalar16Scalar16WriteContext(staleWriter);
        }

        if (staleRouteException is null ||
            !staleRouteException.Message.Contains("route claim no longer matches", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Generic SS16-16 stale-route probe expected publish-time route-claim validation to reject stale shelf bytes.");
        }
    }

    /// <summary>
    /// Proves `FS32-8` writer-context publication rejects a staged shelf after the selected physical shelf has been transformed into deeper route topology.<br/>
    /// This guards the primitive concurrency contract that shelf-local writers may publish only while the route still selects the same committed shelf shape.<br/>
    /// </summary>
    private static void RunInternalGenericFixed32Scalar8StaleRouteClaimProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions { DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed });
        using LibraDexIndex<byte[], long> index = catalog.Indexes["generic-fs32-8-stale-route-claim"]["ids"].Blob.Scalar<long>(LibraDexScalarWidth.Bytes32).Create();
        Fixed32Scalar8Profile profile = index.GetFixed32Scalar8Profile();
        const ulong key0 = 0x5800_0000_0000_0000UL;
        const ulong key1 = 0;
        const ulong key2 = 0;
        const ulong seedKey3 = 1;
        const ulong stagedKey3 = 0x7FFF;
        byte rootPrefix = (byte)(key0 >> 56);

        LibraDexGenericInsertResult seed = index.Insert(
            LibraDexGenericScalarCodec<byte[]>.Decode32(key0, key1, key2, seedKey3),
            58001);
        if (!seed.Inserted)
        {
            throw new InvalidDataException("Generic FS32-8 stale-route setup failed to seed the initial shelf.");
        }

        long originalTarget = index.Session.FindRouterTarget(index.RootRouterOffset, rootPrefix);
        if (originalTarget == 0)
        {
            throw new InvalidDataException("Generic FS32-8 stale-route setup did not create the expected root-prefix shelf.");
        }

        LibraDexWriteContext staleWriter = index.Session.BeginFixed32Scalar8WriteContext();
        Fixed32Scalar8RoutedInsertResult staged = index.Session.InsertWalkedRoutedFixed32Scalar8NoSplitForWriteContext(
            staleWriter,
            index.RootRouterOffset,
            profile,
            key0,
            key1,
            key2,
            stagedKey3,
            LibraDexGenericScalarCodec<long>.Encode8(58999),
            allowDuplicateKeys: true,
            maxRouterHops: 8);
        if (staged.InsertResult != Fixed32Scalar8InsertResult.Inserted ||
            staged.PrimaryOffset != originalTarget)
        {
            index.Session.AbortFixed32Scalar8WriteContext(staleWriter);
            throw new InvalidDataException("Generic FS32-8 stale-route setup failed to stage the expected writer-context shelf image.");
        }

        for (int i = 2; i <= profile.MaxItemCount; i++)
        {
            Fixed32Scalar8RoutedInsertResult fill = index.Session.InsertWalkedRoutedFixed32Scalar8(
                index.RootRouterOffset,
                profile,
                key0,
                key1,
                key2,
                checked((ulong)i),
                LibraDexGenericScalarCodec<long>.Encode8(58000 + i),
                allowDuplicateKeys: true,
                maxRouterHops: 8);
            if (fill.InsertResult != Fixed32Scalar8InsertResult.Inserted)
            {
                index.Session.AbortFixed32Scalar8WriteContext(staleWriter);
                throw new InvalidDataException($"Generic FS32-8 stale-route setup fill insert {i} failed with {fill.InsertResult}.");
            }
        }

        if (!index.Session.TryTransformFixed32Scalar8DirectShelf(
            index.RootRouterOffset,
            profile,
            key0,
            key1,
            key2,
            checked((ulong)(profile.MaxItemCount + 1)),
            LibraDexGenericScalarCodec<long>.Encode8(58000 + profile.MaxItemCount + 1),
            maxRouterHops: 8,
            out Fixed32Scalar8RoutedInsertResult transform) ||
            transform.InsertResult != Fixed32Scalar8InsertResult.Inserted)
        {
            index.Session.AbortFixed32Scalar8WriteContext(staleWriter);
            throw new InvalidDataException("Generic FS32-8 stale-route setup could not force a shelf transform.");
        }

        long currentTarget = index.Session.FindRouterTarget(index.RootRouterOffset, rootPrefix);
        if (currentTarget != originalTarget ||
            index.Session.ClassifyFixed32Scalar8RouteTarget(currentTarget) != Fixed32Scalar8RouteTargetKind.Router)
        {
            index.Session.AbortFixed32Scalar8WriteContext(staleWriter);
            throw new InvalidDataException("Generic FS32-8 stale-route setup did not transform the original shelf offset into router topology.");
        }

        InvalidOperationException? staleRouteException = null;
        try
        {
            _ = index.Session.PublishFixed32Scalar8WriteContext(staleWriter);
        }
        catch (InvalidOperationException ex)
        {
            staleRouteException = ex;
            index.Session.AbortFixed32Scalar8WriteContext(staleWriter);
        }

        if (staleRouteException is null ||
            !staleRouteException.Message.Contains("route claim no longer matches", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Generic FS32-8 stale-route probe expected publish-time route-claim validation to reject stale shelf bytes.");
        }
    }

    /// <summary>
    /// Proves `FS32-16` writer-context publication rejects a staged shelf after the selected physical shelf has been transformed into deeper route topology.<br/>
    /// This guards the primitive concurrency contract that shelf-local writers may publish only while the route still selects the same committed shelf shape.<br/>
    /// </summary>
    private static void RunInternalGenericFixed32Scalar16StaleRouteClaimProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions { DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed });
        using LibraDexIndex<byte[], Guid> index = catalog.Indexes["generic-fs32-16-stale-route-claim"]["ids"].Blob.Scalar<Guid>(LibraDexScalarWidth.Bytes32).Create();
        Fixed32Scalar16Profile profile = index.GetFixed32Scalar16Profile();
        const ulong key0 = 0x5900_0000_0000_0000UL;
        const ulong key1 = 0;
        const ulong key2 = 0;
        const ulong seedKey3 = 1;
        const ulong stagedKey3 = 0x7FFF;
        byte rootPrefix = (byte)(key0 >> 56);
        Guid seedIdentity = Guid.Parse("5d000000-0000-0000-0000-000000000001");
        LibraDexGenericScalarCodec<Guid>.Encode16(seedIdentity, out ulong seedIdentityHigh, out ulong seedIdentityLow);

        LibraDexGenericInsertResult seed = index.Insert(
            LibraDexGenericScalarCodec<byte[]>.Decode32(key0, key1, key2, seedKey3),
            seedIdentity);
        if (!seed.Inserted)
        {
            throw new InvalidDataException("Generic FS32-16 stale-route setup failed to seed the initial shelf.");
        }

        long originalTarget = index.Session.FindRouterTarget(index.RootRouterOffset, rootPrefix);
        if (originalTarget == 0)
        {
            throw new InvalidDataException("Generic FS32-16 stale-route setup did not create the expected root-prefix shelf.");
        }

        LibraDexWriteContext staleWriter = index.Session.BeginFixed32Scalar16WriteContext();
        Fixed32Scalar16RoutedInsertResult staged = index.Session.InsertWalkedRoutedFixed32Scalar16NoSplitForWriteContext(
            staleWriter,
            index.RootRouterOffset,
            profile,
            key0,
            key1,
            key2,
            stagedKey3,
            seedIdentityHigh + 1,
            seedIdentityLow + 1,
            allowDuplicateKeys: true,
            maxRouterHops: 8);
        if (staged.InsertResult != Fixed32Scalar16InsertResult.Inserted ||
            staged.PrimaryOffset != originalTarget)
        {
            index.Session.AbortFixed32Scalar16WriteContext(staleWriter);
            throw new InvalidDataException("Generic FS32-16 stale-route setup failed to stage the expected writer-context shelf image.");
        }

        for (int i = 2; i <= profile.MaxItemCount; i++)
        {
            Fixed32Scalar16RoutedInsertResult fill = index.Session.InsertWalkedRoutedFixed32Scalar16(
                index.RootRouterOffset,
                profile,
                key0,
                key1,
                key2,
                checked((ulong)i),
                seedIdentityHigh + checked((ulong)(i + 1)),
                seedIdentityLow + checked((ulong)(i + 1)),
                allowDuplicateKeys: true,
                maxRouterHops: 8);
            if (fill.InsertResult != Fixed32Scalar16InsertResult.Inserted)
            {
                index.Session.AbortFixed32Scalar16WriteContext(staleWriter);
                throw new InvalidDataException($"Generic FS32-16 stale-route setup fill insert {i} failed with {fill.InsertResult}.");
            }
        }

        if (!index.Session.TryTransformFixed32Scalar16DirectShelf(
            index.RootRouterOffset,
            profile,
            key0,
            key1,
            key2,
            checked((ulong)(profile.MaxItemCount + 1)),
            seedIdentityHigh + checked((ulong)(profile.MaxItemCount + 2)),
            seedIdentityLow + checked((ulong)(profile.MaxItemCount + 2)),
            maxRouterHops: 8,
            out Fixed32Scalar16RoutedInsertResult transform) ||
            transform.InsertResult != Fixed32Scalar16InsertResult.Inserted)
        {
            index.Session.AbortFixed32Scalar16WriteContext(staleWriter);
            throw new InvalidDataException("Generic FS32-16 stale-route setup could not force a shelf transform.");
        }

        long currentTarget = index.Session.FindRouterTarget(index.RootRouterOffset, rootPrefix);
        if (currentTarget != originalTarget ||
            index.Session.ClassifyFixed32Scalar16RouteTarget(currentTarget) != Fixed32Scalar16RouteTargetKind.Router)
        {
            index.Session.AbortFixed32Scalar16WriteContext(staleWriter);
            throw new InvalidDataException("Generic FS32-16 stale-route setup did not transform the original shelf offset into router topology.");
        }

        InvalidOperationException? staleRouteException = null;
        try
        {
            _ = index.Session.PublishFixed32Scalar16WriteContext(staleWriter);
        }
        catch (InvalidOperationException ex)
        {
            staleRouteException = ex;
            index.Session.AbortFixed32Scalar16WriteContext(staleWriter);
        }

        if (staleRouteException is null ||
            !staleRouteException.Message.Contains("route claim no longer matches", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Generic FS32-16 stale-route probe expected publish-time route-claim validation to reject stale shelf bytes.");
        }
    }

    /// <summary>
    /// Proves an `FSN-8` direct-root writer-context publication rejects a staged shelf after the physical root offset becomes router topology.<br/>
    /// This exercises the fixed-N root-shelf claim path that routed parent-slot stale-route probes cannot cover.<br/>
    /// </summary>
    private static void RunInternalFixedNScalar8RootShelfClaimProbe()
    {
        using LibraDexFileSession session = LibraDexFileSession.InitializeMemory(
            CreateDesignPerfOptions(),
            CreateDesignPerfMetadata(9905),
            DataKernelTelemetryOptions.FromLevel(LibraDexDiagnosticsLevel.Detailed));
        const int slotIndex = 0;
        FixedNScalar8Profile profile = FixedNScalar8Profile.Default64KiB(8);
        CatalogIndexMetadata metadata = CreateFixedNRootClaimProbeMetadata<long>("fixedn-root-claim", "fsn8root", profile.KeySize);
        (FixedNScalar8IndexHandle handle, _) = session.CreateFixedNScalar8ShelfIndex(
            CreateHarnessSlot(slotIndex, "fsn8root", 0),
            metadata,
            profile);

        LibraDexWriteContext staleWriter = session.BeginFixedNScalar8WriteContext();
        bool contextActive = true;
        InvalidOperationException? staleClaimException = null;
        try
        {
            LibraDexFileSession.RecordFixedNScalar8RootShelfClaimForWriteContext(staleWriter, handle.RootOffset, profile);
            byte[] shelfBytes = session.ReadFixedNScalar8ShelfBytesForWriteContext(staleWriter, handle.RootOffset, profile);
            byte[] key = CreateFixedNRootClaimProbeKey(profile.KeySize, 0x44, 0x01);
            FixedNScalar8 shelf = new(shelfBytes, profile);
            FixedNScalarInsertResult insertResult = shelf.Insert(key, 44001, allowDuplicateKeys: true);
            if (insertResult != FixedNScalarInsertResult.Inserted)
            {
                throw new InvalidDataException($"FSN-8 root shelf claim setup expected staged insert, got {insertResult}.");
            }

            session.StageFixedNScalar8ShelfRewriteForWriteContext(staleWriter, handle.RootOffset, profile, shelfBytes);
            _ = session.RewriteOffsetAsRootRouterForConcurrencyProof(handle.RootOffset, allocationClassId: 1);
            contextActive = false;
            try
            {
                _ = session.PublishFixedNScalar8WriteContext(staleWriter, profile);
            }
            catch (InvalidOperationException ex)
            {
                staleClaimException = ex;
            }
        }
        finally
        {
            if (contextActive)
            {
                session.AbortFixedNScalar8WriteContext(staleWriter);
            }
        }

        if (staleClaimException is null ||
            !staleClaimException.Message.Contains("root shelf claim no longer matches", StringComparison.Ordinal))
        {
            throw new InvalidDataException("FSN-8 root shelf claim probe expected publish-time validation to reject stale root shelf bytes.");
        }
    }

    /// <summary>
    /// Proves an `FSN-16` direct-root writer-context publication rejects a staged shelf after the physical root offset becomes router topology.<br/>
    /// This mirrors the scalar-8 proof for the wide-identity fixed-N shelf shape.<br/>
    /// </summary>
    private static void RunInternalFixedNScalar16RootShelfClaimProbe()
    {
        using LibraDexFileSession session = LibraDexFileSession.InitializeMemory(
            CreateDesignPerfOptions(),
            CreateDesignPerfMetadata(9906),
            DataKernelTelemetryOptions.FromLevel(LibraDexDiagnosticsLevel.Detailed));
        const int slotIndex = 0;
        FixedNScalar16Profile profile = FixedNScalar16Profile.Default64KiB(8);
        CatalogIndexMetadata metadata = CreateFixedNRootClaimProbeMetadata<Guid>("fixedn-root-claim", "fsn16root", profile.KeySize);
        (FixedNScalar16IndexHandle handle, _) = session.CreateFixedNScalar16ShelfIndex(
            CreateHarnessSlot(slotIndex, "fsn16root", 0),
            metadata,
            profile);

        LibraDexWriteContext staleWriter = session.BeginFixedNScalar16WriteContext();
        bool contextActive = true;
        InvalidOperationException? staleClaimException = null;
        try
        {
            LibraDexFileSession.RecordFixedNScalar16RootShelfClaimForWriteContext(staleWriter, handle.RootOffset, profile);
            byte[] shelfBytes = session.ReadFixedNScalar16ShelfBytesForWriteContext(staleWriter, handle.RootOffset, profile);
            byte[] key = CreateFixedNRootClaimProbeKey(profile.KeySize, 0x55, 0x01);
            byte[] identity = new byte[FixedNScalar16Layout.IdentitySize];
            BinaryPrimitives.WriteUInt64BigEndian(identity.AsSpan(0, sizeof(ulong)), 55001);
            BinaryPrimitives.WriteUInt64BigEndian(identity.AsSpan(sizeof(ulong), sizeof(ulong)), 55002);
            FixedNScalar16 shelf = new(shelfBytes, profile);
            FixedNScalarInsertResult insertResult = shelf.Insert(key, identity, allowDuplicateKeys: true);
            if (insertResult != FixedNScalarInsertResult.Inserted)
            {
                throw new InvalidDataException($"FSN-16 root shelf claim setup expected staged insert, got {insertResult}.");
            }

            session.StageFixedNScalar16ShelfRewriteForWriteContext(staleWriter, handle.RootOffset, profile, shelfBytes);
            _ = session.RewriteOffsetAsRootRouterForConcurrencyProof(handle.RootOffset, allocationClassId: 1);
            contextActive = false;
            try
            {
                _ = session.PublishFixedNScalar16WriteContext(staleWriter, profile);
            }
            catch (InvalidOperationException ex)
            {
                staleClaimException = ex;
            }
        }
        finally
        {
            if (contextActive)
            {
                session.AbortFixedNScalar16WriteContext(staleWriter);
            }
        }

        if (staleClaimException is null ||
            !staleClaimException.Message.Contains("root shelf claim no longer matches", StringComparison.Ordinal))
        {
            throw new InvalidDataException("FSN-16 root shelf claim probe expected publish-time validation to reject stale root shelf bytes.");
        }
    }

    /// <summary>
    /// Creates minimal catalog metadata for direct fixed-N root-claim proof indexes.<br/>
    /// The proof uses internal session constructors, so the metadata only needs to be coherent enough for directory persistence and reopen diagnostics.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The scalar identity type represented by the proof index.<br/></typeparam>
    /// <param name="group">The logical proof group name.<br/></param>
    /// <param name="name">The logical proof index name.<br/></param>
    /// <param name="maxKeyLength">The fixed-N key byte length recorded in catalog metadata.<br/></param>
    /// <returns>A catalog metadata record for the fixed-width BigInt proof index.<br/></returns>
    private static CatalogIndexMetadata CreateFixedNRootClaimProbeMetadata<TIdentity>(string group, string name, int maxKeyLength)
    {
        IndexOptions options = new();
        return new CatalogIndexMetadata(
            group,
            name,
            typeof(BigInteger).FullName ?? nameof(BigInteger),
            typeof(TIdentity).FullName ?? typeof(TIdentity).Name,
            CatalogIndexKeyFamily.BigInt,
            CatalogIndexIdentityFamily.Scalar,
            options.Keys,
            options.StringKeys,
            options.GuidKeys,
            options.DateKeys,
            options.DateTimeKeyEncoding,
            LibraDexProjectionDirectionSet.Forward,
            LibraDexIndexSortOrder.Ascending,
            [new LibraDexIndexProjectionSpec(LibraDexIndexProjectionKind.BigIntFixed, LibraDexIndexByteDirection.Forward, LibraDexIndexSortOrder.Ascending)],
            Array.Empty<LibraDexCompositeKeyPartSpec>(),
            maxKeyLength,
            0,
            -1,
            -1,
            -1,
            -1,
            string.Empty,
            string.Empty,
            LibraDexStringComparisonPolicyKind.Invariant,
            CompareOptions.None,
            string.Empty,
            string.Empty,
            HasShapeMetadata: false);
    }

    /// <summary>
    /// Creates a deterministic fixed-N key for root-claim proof inserts.<br/>
    /// Only the first and last bytes vary because these probes validate physical root-shape claims rather than key-order behavior.<br/>
    /// </summary>
    /// <param name="keySize">The fixed-N key byte size to allocate.<br/></param>
    /// <param name="firstByte">The first key byte.<br/></param>
    /// <param name="lastByte">The final key byte.<br/></param>
    /// <returns>A key buffer with the requested boundary bytes.</returns>
    private static byte[] CreateFixedNRootClaimProbeKey(int keySize, byte firstByte, byte lastByte)
    {
        byte[] key = new byte[keySize];
        key[0] = firstByte;
        key[^1] = lastByte;
        return key;
    }

    /// <summary>
    /// Proves that two generic `SS8-8` queued writers for different indexes in the same catalog can overlap when both writes stay on the writer-context path.<br/>
    /// This is the first same-catalog/different-index concurrency proof: the indexes share one session and `DataKernel`, but shelf-local staging remains writer-local and publication is serialized by the session seam.<br/>
    /// </summary>
    private static void RunInternalGenericScalar8Scalar8QueuedDifferentIndexProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        CatalogIdentityGroupIndexes group = catalog.Indexes["generic-queued-index-isolation"];
        using LibraDexIndex<long, long> value = group["value"].Int64Keys<long>().Create();
        using LibraDexIndex<long, long> status = group["status"].Int64Keys<long>().Create();
        _ = value.Insert(10, 1000);
        _ = status.Insert(20, 2000);

        LibraDexQueuedWriter<long, long> valueWriter = value.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);
        LibraDexQueuedWriter<long, long> statusWriter = status.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);
        using ManualResetEventSlim startGate = new(false);
        using CountdownEvent readyGate = new(2);
        LibraDexGenericInsertResult valueResult = default;
        LibraDexGenericInsertResult statusResult = default;

        Task valueTask = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            valueResult = valueWriter.Insert(11, 1001);
        });

        Task statusTask = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            statusResult = statusWriter.Insert(21, 2001);
        });

        if (!readyGate.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidDataException("Generic SS8-8 different-index queued writer probe could not ready both callers.");
        }

        startGate.Set();
        Task.WaitAll(valueTask, statusTask);

        if (!valueResult.Inserted ||
            !statusResult.Inserted ||
            valueResult.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext ||
            statusResult.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext)
        {
            throw new InvalidDataException($"Generic SS8-8 different-index queued writer probe expected writer-context paths but saw {valueResult.QueuedInsertPath}/{statusResult.QueuedInsertPath}.");
        }

        long[] identities = new long[4];
        LibraDexGenericRangeReadResult valueRead = value.ReadRange(10, 11, identities);
        if (valueRead.IdentityCount != 2 ||
            identities[0] != 1000 ||
            identities[1] != 1001)
        {
            throw new InvalidDataException("Generic SS8-8 different-index queued writer value-index readback failed.");
        }

        Array.Clear(identities);
        LibraDexGenericRangeReadResult statusRead = status.ReadRange(20, 21, identities);
        if (statusRead.IdentityCount != 2 ||
            identities[0] != 2000 ||
            identities[1] != 2001)
        {
            throw new InvalidDataException("Generic SS8-8 different-index queued writer status-index readback failed.");
        }
    }

    /// <summary>
    /// Proves that topology fallback for one generic `SS8-8` index can overlap a writer-context insert for another index in the same catalog.<br/>
    /// The session still serializes the fallback publication against `DataKernel`, but the per-root topology gate means unrelated index staging is not forced through the same topology lock.<br/>
    /// </summary>
    private static void RunInternalGenericScalar8Scalar8QueuedDifferentIndexFallbackProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        CatalogIdentityGroupIndexes group = catalog.Indexes["generic-queued-index-fallback-isolation"];
        using LibraDexIndex<long, long> routed = group["routed"].Int64Keys<long>().Create();
        using LibraDexIndex<long, long> fallback = group["fallback"].Int64Keys<long>().Create();
        _ = routed.Insert(100, 1000);
        _ = fallback.Insert(200, 2000);

        LibraDexQueuedWriter<long, long> routedWriter = routed.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);
        LibraDexQueuedWriter<long, long> fallbackWriter = fallback.BeginQueuedWriter(LibraDexConcurrencyOptions.QueuedWriter);
        using ManualResetEventSlim startGate = new(false);
        using CountdownEvent readyGate = new(2);
        LibraDexGenericInsertResult routedResult = default;
        LibraDexGenericInsertResult fallbackResult = default;

        Task routedTask = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            routedResult = routedWriter.Insert(101, 1001);
        });

        Task fallbackTask = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            fallbackResult = fallbackWriter.Insert(long.MinValue + 42, 4200);
        });

        if (!readyGate.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidDataException("Generic SS8-8 different-index fallback probe could not ready both callers.");
        }

        startGate.Set();
        Task.WaitAll(routedTask, fallbackTask);

        if (!routedResult.Inserted ||
            routedResult.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext)
        {
            throw new InvalidDataException($"Generic SS8-8 different-index fallback probe expected routed insert to stay on writer-context path but saw {routedResult.QueuedInsertPath}.");
        }

        if (!fallbackResult.Inserted ||
            !fallbackResult.CreatedInitialShelfRoute ||
            fallbackResult.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.NarrowTopologyPublisher)
        {
            throw new InvalidDataException($"Generic SS8-8 different-index fallback probe expected serialized fallback path but saw inserted={fallbackResult.Inserted} createdRoute={fallbackResult.CreatedInitialShelfRoute} path={fallbackResult.QueuedInsertPath}.");
        }

        long[] identities = new long[4];
        LibraDexGenericRangeReadResult routedRead = routed.ReadRange(100, 101, identities);
        if (routedRead.IdentityCount != 2 ||
            identities[0] != 1000 ||
            identities[1] != 1001)
        {
            throw new InvalidDataException("Generic SS8-8 different-index fallback routed-index readback failed.");
        }

        Array.Clear(identities);
        LibraDexGenericRangeReadResult fallbackRead = fallback.ReadRange(long.MinValue + 42, long.MinValue + 42, identities);
        if (fallbackRead.IdentityCount != 1 ||
            identities[0] != 4200)
        {
            throw new InvalidDataException("Generic SS8-8 different-index fallback-index readback failed.");
        }
    }

    /// <summary>
    /// Proves the Abraxas-facing write adapter can bind a named generic `SS8-8` index and submit overlapping queued writes without caller-managed writer contexts.<br/>
    /// The probe keeps source-object mutation outside LibraDex and validates only the identity-index publication path.<br/>
    /// </summary>
    private static void RunAbraxasIdentityWriteAdapterQueuedWriterProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });
        CatalogIdentityGroupIndexes group = catalog.Indexes["abraxas-write"];
        using LibraDexIndex<long, long> index = group["score"].Int64Keys<long>().Create();
        _ = index.Insert(100, 1001);
        AbraxasIdentityWriteAdapter<long> adapter = group.AbraxasIdentityWrite<long>();
        if (adapter.Group != "abraxas-write")
        {
            throw new InvalidDataException("Abraxas identity write adapter did not preserve the bound identity group name.");
        }

        LibraDexQueuedWriter<long, long> queuedWriter = adapter.For<long>("score", LibraDexConcurrencyOptions.QueuedWriter);
        using ManualResetEventSlim startGate = new(false);
        using CountdownEvent readyGate = new(2);
        LibraDexGenericInsertResult resultA = default;
        LibraDexGenericInsertResult resultB = default;

        Task taskA = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            resultA = queuedWriter.Insert(101, 1002);
        });

        Task taskB = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            resultB = queuedWriter.Insert(102, 1003);
        });

        if (!readyGate.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidDataException("Abraxas identity write adapter queued writer probe could not ready both callers.");
        }

        startGate.Set();
        Task.WaitAll(taskA, taskB);

        if (!resultA.Inserted ||
            !resultB.Inserted ||
            resultA.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext ||
            resultB.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext)
        {
            throw new InvalidDataException("Abraxas identity write adapter did not preserve writer-context attribution for overlapping inserts.");
        }

        LibraDexGenericInsertResult fallback = queuedWriter.Insert(long.MinValue + 77, 3001);
        if (!fallback.Inserted ||
            !fallback.CreatedInitialShelfRoute ||
            fallback.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.NarrowTopologyPublisher)
        {
            throw new InvalidDataException("Abraxas identity write adapter did not preserve serialized fallback attribution.");
        }

        long[] identities = new long[8];
        LibraDexGenericRangeReadResult read = index.ReadRange(100, 102, identities);
        if (read.IdentityCount != 3 || identities[0] != 1001 || identities[1] != 1002 || identities[2] != 1003)
        {
            throw new InvalidDataException("Abraxas identity write adapter did not publish same-prefix queued writes.");
        }

        Array.Clear(identities);
        read = index.ReadRange(long.MinValue + 77, long.MinValue + 77, identities);
        if (read.IdentityCount != 1 || identities[0] != 3001)
        {
            throw new InvalidDataException("Abraxas identity write adapter fallback insert was not readable.");
        }

        using ManualResetEventSlim oneShotStartGate = new(false);
        using CountdownEvent oneShotReadyGate = new(2);
        LibraDexGenericInsertResult oneShotA = default;
        LibraDexGenericInsertResult oneShotB = default;

        Task oneShotTaskA = Task.Run(() =>
        {
            oneShotReadyGate.Signal();
            oneShotStartGate.Wait();
            oneShotA = adapter.Insert("score", 103L, 1004, LibraDexConcurrencyOptions.QueuedWriter);
        });

        Task oneShotTaskB = Task.Run(() =>
        {
            oneShotReadyGate.Signal();
            oneShotStartGate.Wait();
            oneShotB = adapter.Insert("score", 104L, 1005, LibraDexConcurrencyOptions.QueuedWriter);
        });

        if (!oneShotReadyGate.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidDataException("Abraxas identity write adapter one-shot queued insert probe could not ready both callers.");
        }

        oneShotStartGate.Set();
        Task.WaitAll(oneShotTaskA, oneShotTaskB);

        if (!oneShotA.Inserted ||
            !oneShotB.Inserted ||
            oneShotA.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext ||
            oneShotB.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext)
        {
            throw new InvalidDataException("Abraxas identity write adapter one-shot inserts did not reuse the queued writer path.");
        }

        Array.Clear(identities);
        read = index.ReadRange(100, 104, identities);
        if (read.IdentityCount != 5 || identities[0] != 1001 || identities[1] != 1002 || identities[2] != 1003 || identities[3] != 1004 || identities[4] != 1005)
        {
            throw new InvalidDataException("Abraxas identity write adapter one-shot inserts were not readable.");
        }
    }

    /// <summary>
    /// Proves the Abraxas-facing write adapter can submit exact queued deletes through the same cached writer surface as inserts.<br/>
    /// This models the delete half of an Abraxas patch where source-object mutation is outside LibraDex and the identity-index projection removes an old key/identity tuple.<br/>
    /// </summary>
    private static void RunAbraxasIdentityWriteAdapterQueuedDeleteProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        CatalogIdentityGroupIndexes group = catalog.Indexes["abraxas-delete"];
        using LibraDexIndex<long, long> index = group["score"].Int64Keys<long>().Create();
        _ = index.Insert(100, 1001);
        _ = index.Insert(101, 1002);
        _ = index.Insert(102, 1003);

        AbraxasIdentityWriteAdapter<long> adapter = group.AbraxasIdentityWrite<long>();
        using ManualResetEventSlim startGate = new(false);
        using CountdownEvent readyGate = new(2);
        LibraDexGenericDeleteResult deleteA = default;
        LibraDexGenericDeleteResult deleteB = default;

        Task taskA = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            deleteA = adapter.Delete("score", 100L, 1001, LibraDexConcurrencyOptions.QueuedWriter);
        });

        Task taskB = Task.Run(() =>
        {
            readyGate.Signal();
            startGate.Wait();
            deleteB = adapter.Delete("score", 101L, 1002, LibraDexConcurrencyOptions.QueuedWriter);
        });

        if (!readyGate.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidDataException("Abraxas identity write adapter queued delete probe could not ready both callers.");
        }

        startGate.Set();
        Task.WaitAll(taskA, taskB);

        if (!deleteA.Deleted ||
            !deleteB.Deleted ||
            deleteA.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext ||
            deleteB.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext)
        {
            throw new InvalidDataException($"Abraxas identity write adapter exact deletes expected writer-context paths but saw {deleteA.Deleted}/{deleteA.QueuedInsertPath} and {deleteB.Deleted}/{deleteB.QueuedInsertPath}.");
        }

        long[] identities = new long[4];
        LibraDexGenericRangeReadResult read = index.ReadRange(100, 102, identities);
        if (read.IdentityCount != 1 ||
            identities[0] != 1003)
        {
            throw new InvalidDataException("Abraxas identity write adapter exact deletes did not leave the expected surviving tuple.");
        }
    }

    /// <summary>
    /// Proves the Abraxas-facing write adapter can express a key-changing patch as queued replacement insert plus queued old tuple delete.<br/>
    /// This keeps the Abraxas developer surface low-friction while preserving LibraDex's identity-index semantics instead of introducing database transaction language.<br/>
    /// </summary>
    private static void RunAbraxasIdentityWriteAdapterQueuedRekeyProbe()
    {
        using Catalog catalog = Catalog.CreateMemory(new CatalogOptions
        {
            DiagnosticsLevel = LibraDexDiagnosticsLevel.Detailed
        });

        CatalogIdentityGroupIndexes group = catalog.Indexes["abraxas-rekey"];
        using LibraDexIndex<long, long> index = group["score"].Int64Keys<long>().Create();
        _ = index.Insert(100, 1001);
        _ = index.Insert(101, 1002);

        AbraxasIdentityWriteAdapter<long> adapter = group.AbraxasIdentityWrite<long>();
        LibraDexGenericRekeyResult rekey = adapter.Rekey("score", 1001, 100L, 102L, LibraDexConcurrencyOptions.QueuedWriter);
        if (!rekey.Changed ||
            !rekey.Replacement.Inserted ||
            !rekey.Removal.Deleted ||
            rekey.Replacement.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext ||
            rekey.Removal.QueuedInsertPath != Scalar8Scalar8QueuedInsertPath.WriterContext)
        {
            throw new InvalidDataException($"Abraxas identity write adapter queued rekey expected writer-context replacement and removal but saw changed={rekey.Changed} insert={rekey.Replacement.Inserted}/{rekey.Replacement.QueuedInsertPath} delete={rekey.Removal.Deleted}/{rekey.Removal.QueuedInsertPath}.");
        }

        long[] identities = new long[4];
        LibraDexGenericRangeReadResult read = index.ReadRange(100, 102, identities);
        if (read.IdentityCount != 2 ||
            identities[0] != 1002 ||
            identities[1] != 1001)
        {
            throw new InvalidDataException("Abraxas identity write adapter queued rekey did not move the identity to the replacement key.");
        }
    }

    /// <summary>
    /// Proves that detailed diagnostics reject a commit or abort request that does not carry the active durability-batch token.<br/>
    /// This validates the internal ownership guard directly without adding any test-only public surface.<br/>
    /// </summary>
    private static void RunInternalWrongTokenRejectedProbe()
    {
        using LibraDexFileSession session = LibraDexFileSession.InitializeMemory(
            CreateDesignPerfOptions(),
            CreateDesignPerfMetadata(9901),
            DataKernelTelemetryOptions.FromLevel(LibraDexDiagnosticsLevel.Detailed));
        using LibraDexFileSessionDurabilityBatch active = session.BeginDurabilityBatch();

        InvalidOperationException? wrongContextException = null;
        try
        {
            _ = session.AbortDurabilityBatch(new LibraDexWriteContext(99019901));
        }
        catch (InvalidOperationException ex)
        {
            wrongContextException = ex;
        }

        if (wrongContextException is null ||
            !wrongContextException.Message.Contains("does not own the active session publication boundary", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Concurrency contract sanity expected a wrong writer context to be rejected.");
        }

        active.Abort();
    }

    /// <summary>
    /// Proves that multiple independently owned logical string batches can force `VS8` shelf growth and fallback publication without losing an accepted exact tuple.<br/>
    /// The workload deliberately shares a long key prefix so concurrent contexts repeatedly meet on the same routed shelf while still submitting unique logical keys and identities.<br/>
    /// Live and reopened validation both inspect every expected tuple, making an old private shelf image overwriting an immediate fallback mutation directly observable.<br/>
    /// </summary>
    /// <param name="args">Optional <c>--threads</c>, <c>--items-per-thread</c>, <c>--max-action-items</c>, and <c>--wherzit-distribution</c> controls for the focused stress shape.<br/></param>
    /// <returns>Zero when all accepted tuples remain visible live and after reopen.<br/></returns>
    private static int RunStringConcurrentBatchPublicationSanity(string[] args)
    {
        int threadCount = Math.Max(2, GetIntOption(args, "--threads", 4));
        int itemsPerThread = Math.Max(256, GetIntOption(args, "--items-per-thread", 2048));
        int maximumActionItems = Math.Max(1, GetIntOption(args, "--max-action-items", LibraDexConcurrencyOptions.QueuedWriter.MaxActionItems));
        bool useWherzitDistribution = args.Any(static argument => string.Equals(argument, "--wherzit-distribution", StringComparison.OrdinalIgnoreCase));
        string root = Path.Combine(
            Path.GetTempPath(),
            "LibraDex",
            $"string-concurrent-publication-{Guid.NewGuid():N}");
        string path = Path.Combine(root, "proof.lbdx");
        Directory.CreateDirectory(root);

        try
        {
            using (Catalog catalog = Catalog.Create(path, CatalogOptions.UInt64Identities))
            using (LibraDexStringScalar8Index index = catalog.Indexes["proof"]["path"].String.Create(StringKeys.Exact))
            {
                using ManualResetEventSlim startGate = new(false);
                using CountdownEvent readyGate = new(threadCount);
                Task[] tasks = new Task[threadCount];
                var publications = new LibraDexConcurrentBatchPublishResult[threadCount];
                for (int worker = 0; worker < threadCount; worker++)
                {
                    int workerOrdinal = worker;
                    tasks[worker] = Task.Run(() =>
                    {
                        using LibraDexStringScalar8ConcurrentBatch batch = index.BeginConcurrentBatch(
                            new LibraDexConcurrencyOptions
                            {
                                Mode = LibraDexConcurrencyMode.QueuedWriter,
                                MaxActionItems = maximumActionItems
                            });
                        readyGate.Signal();
                        startGate.Wait();
                        for (int item = 0; item < itemsPerThread; item++)
                        {
                            string key = CreateStringConcurrentBatchPublicationKey(
                                workerOrdinal,
                                item,
                                threadCount,
                                useWherzitDistribution);
                            ulong identity = CreateStringConcurrentBatchPublicationIdentity(workerOrdinal, item, itemsPerThread);
                            LibraDexGenericInsertResult insert = batch.Insert(key, identity);
                            if (!insert.Inserted)
                            {
                                throw new InvalidDataException($"VS8 concurrent publication proof rejected worker={workerOrdinal} item={item} identity={identity}.");
                            }
                        }

                        LibraDexConcurrentBatchPublishResult publish = batch.Publish();
                        publications[workerOrdinal] = publish;
                        if (publish.InsertedCount != itemsPerThread)
                        {
                            throw new InvalidDataException($"VS8 concurrent publication proof expected {itemsPerThread:n0} accepted inserts for worker {workerOrdinal}, got {publish.InsertedCount:n0}.");
                        }
                    });
                }

                if (!readyGate.Wait(TimeSpan.FromSeconds(30)))
                {
                    throw new InvalidDataException("VS8 concurrent publication proof could not ready every independent batch.");
                }

                startGate.Set();
                Task.WaitAll(tasks);
                ValidateStringConcurrentBatchPublicationParity(index, threadCount, itemsPerThread, useWherzitDistribution, "live");
                Console.WriteLine(
                    $"string concurrent publication contexts={publications.Sum(static result => result.PublishedContextCount):n0} " +
                    $"peakStaged={publications.Max(static result => result.MaximumStagedMutationCount):n0} " +
                    $"maxActionItems={maximumActionItems:n0}");
            }

            using (Catalog reopened = Catalog.Open(path, CatalogOptions.UInt64Identities))
            using (LibraDexStringScalar8Index index = reopened.Indexes["proof"]["path"].String.Open())
            {
                ValidateStringConcurrentBatchPublicationParity(index, threadCount, itemsPerThread, useWherzitDistribution, "reopened");
            }

            Console.WriteLine($"string-concurrent-batch-publication-sanity ok threads={threadCount} itemsPerThread={itemsPerThread:n0} total={checked((long)threadCount * itemsPerThread):n0} maxActionItems={maximumActionItems:n0} distribution={(useWherzitDistribution ? "wherzit" : "worker-prefix")}");
            return 0;
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Validates every unique string/identity tuple expected from the focused independent-batch publication proof.<br/>
    /// The first missing tuples are retained in the exception so nondeterministic publication loss can still be correlated to worker and source position.<br/>
    /// </summary>
    /// <param name="index">Live or reopened exact string index to inspect.<br/></param>
    /// <param name="threadCount">Number of independent batch owners used by the proof.<br/></param>
    /// <param name="itemsPerThread">Number of accepted inserts submitted by each owner.<br/></param>
    /// <param name="phase">Short validation phase label included in failures.<br/></param>
    private static void ValidateStringConcurrentBatchPublicationParity(
        LibraDexStringScalar8Index index,
        int threadCount,
        int itemsPerThread,
        bool useWherzitDistribution,
        string phase)
    {
        List<string> missing = new(capacity: 8);
        long found = 0;
        for (int worker = 0; worker < threadCount; worker++)
        {
            for (int item = 0; item < itemsPerThread; item++)
            {
                string key = CreateStringConcurrentBatchPublicationKey(
                    worker,
                    item,
                    threadCount,
                    useWherzitDistribution);
                ulong identity = CreateStringConcurrentBatchPublicationIdentity(worker, item, itemsPerThread);
                if (index.ContainsTupleUtf8(Encoding.UTF8.GetBytes(key), identity))
                {
                    found++;
                }
                else if (missing.Count < 8)
                {
                    missing.Add($"w{worker}/i{item}/id{identity}/{key}");
                }
            }
        }

        long expected = checked((long)threadCount * itemsPerThread);
        if (found != expected)
        {
            throw new InvalidDataException($"VS8 concurrent publication {phase} parity expected {expected:n0} tuples, found {found:n0}; missing sample: {string.Join("; ", missing)}.");
        }

        long emitted = 0;
        var uniqueIdentities = new HashSet<ulong>();
        foreach (ulong identity in index.IterateExactIdentities(QueryDirection.Ascending))
        {
            emitted++;
            uniqueIdentities.Add(identity);
        }
        if (emitted != expected || uniqueIdentities.Count != expected)
        {
            throw new InvalidDataException(
                $"VS8 concurrent publication {phase} ordered traversal emitted {emitted:n0} rows and {uniqueIdentities.Count:n0} unique identities; expected exactly {expected:n0} of each.");
        }
    }

    /// <summary>
    /// Creates one common-prefix exact string key for the independent-batch publication proof.<br/>
    /// The default shape places worker position after the shared path prefix; the Wherzit shape reproduces Abraxas's strided worker assignment over interleaved project, folder, and path-segment components.<br/>
    /// </summary>
    /// <param name="worker">Independent batch owner ordinal.<br/></param>
    /// <param name="item">Owner-local item ordinal.<br/></param>
    /// <param name="threadCount">Worker stride used to recover the source-order ordinal for Wherzit distribution.<br/></param>
    /// <param name="useWherzitDistribution">Whether to reproduce the Abraxas Wherzit fixture's interleaved path components.<br/></param>
    /// <returns>The deterministic unique logical string key.<br/></returns>
    private static string CreateStringConcurrentBatchPublicationKey(
        int worker,
        int item,
        int threadCount,
        bool useWherzitDistribution)
    {
        if (!useWherzitDistribution)
        {
            return FormattableString.Invariant($"E:\\VSProjects\\AbraxasDB\\shared-prefix\\worker-{worker:D2}\\file-{item:D7}.payload.txt");
        }

        int ordinal = checked(800_000 + worker + (item * threadCount));
        return FormattableString.Invariant(
            $"C:\\representative-long-root\\project-{ordinal % 97:D2}\\nested-folder-{ordinal % 251:D3}\\additional-path-segment-{ordinal % 31:D2}\\file-{ordinal:D8}.dat");
    }

    /// <summary>
    /// Creates the unique scalar identity paired with one proof key.<br/>
    /// The one-based result keeps zero outside the generated identity domain while preserving a reversible worker/item mapping.<br/>
    /// </summary>
    /// <param name="worker">Independent batch owner ordinal.<br/></param>
    /// <param name="item">Owner-local item ordinal.<br/></param>
    /// <param name="itemsPerThread">Stride separating worker identity ranges.<br/></param>
    /// <returns>The deterministic one-based scalar identity.<br/></returns>
    private static ulong CreateStringConcurrentBatchPublicationIdentity(int worker, int item, int itemsPerThread)
        => checked((ulong)(((long)worker * itemsPerThread) + item + 1L));

    /// <summary>
    /// Measures and validates generic `SS8-8` concurrent batches over the same strided, monotonically increasing scalar distribution produced by Abraxas's current index-worker loop.<br/>
    /// Per-worker publication diagnostics expose whether same-shelf ownership transfer, topology fallback, or retained context size dominates before any Abraxas or Fractal work is involved.<br/>
    /// Live and reopened validation checks every deterministic key/identity tuple so a faster contention policy cannot hide publication loss.<br/>
    /// </summary>
    /// <param name="args">Optional <c>--threads</c> and <c>--items-per-thread</c> controls for the focused scalar stress shape.<br/></param>
    /// <returns>Zero when every expected tuple is visible live and after reopen.<br/></returns>
    private static int RunScalar8ConcurrentBatchPublicationSanity(string[] args)
    {
        if (args.Any(static argument => string.Equals(argument, "--abx-date", StringComparison.OrdinalIgnoreCase)))
            return RunDateTimeScalar8ConcurrentBatchPublicationSanity(args);

        int threadCount = Math.Max(2, GetIntOption(args, "--threads", 4));
        int itemsPerThread = Math.Max(256, GetIntOption(args, "--items-per-thread", 2048));
        string root = Path.Combine(
            Path.GetTempPath(),
            "LibraDex",
            $"scalar8-concurrent-publication-{Guid.NewGuid():N}");
        string path = Path.Combine(root, "proof.lbdx");
        Directory.CreateDirectory(root);

        try
        {
            using (Catalog catalog = Catalog.Create(path))
            using (LibraDexIndex<long, long> index = catalog.Indexes["proof"]["value"].Int64Keys<long>().Create(
                options: new IndexOptions
                {
                    IdentityKeyMultiplicity = IdentityKeyMultiplicity.SingleKeyPerIdentity
                }))
            {
                using ManualResetEventSlim startGate = new(false);
                using CountdownEvent readyGate = new(threadCount);
                Task[] tasks = new Task[threadCount];
                var publications = new LibraDexConcurrentBatchPublishResult[threadCount];
                for (int worker = 0; worker < threadCount; worker++)
                {
                    int workerOrdinal = worker;
                    tasks[worker] = Task.Run(() =>
                    {
                        using LibraDexConcurrentBatch<long, long> batch = index.BeginConcurrentBatch();
                        readyGate.Signal();
                        startGate.Wait();
                        for (int item = 0; item < itemsPerThread; item++)
                        {
                            long value = CreateScalar8ConcurrentBatchPublicationValue(workerOrdinal, item, threadCount);
                            LibraDexGenericInsertResult insert = batch.Insert(value, value);
                            if (!insert.Inserted)
                            {
                                throw new InvalidDataException(
                                    $"SS8-8 concurrent publication proof rejected worker={workerOrdinal} item={item} value={value}.");
                            }
                        }

                        publications[workerOrdinal] = batch.Publish();
                    });
                }

                if (!readyGate.Wait(TimeSpan.FromSeconds(30)))
                {
                    throw new InvalidDataException("SS8-8 concurrent publication proof could not ready every independent batch.");
                }

                startGate.Set();
                Task.WaitAll(tasks);
                ValidateScalar8ConcurrentBatchPublicationParity(index, threadCount, itemsPerThread, "live");
                Console.WriteLine(
                    $"scalar8 concurrent publication contexts={publications.Sum(static result => result.PublishedContextCount):n0} " +
                    $"ownershipConflicts={publications.Sum(static result => result.OwnershipConflictCount):n0} " +
                    $"conflictPublications={publications.Sum(static result => result.ConflictPublicationCount):n0} " +
                    $"topologyFallbacks={publications.Sum(static result => result.TopologyFallbackCount):n0} " +
                    $"peakStaged={publications.Max(static result => result.MaximumStagedMutationCount):n0} " +
                    $"stagedBeforeConflict={publications.Sum(static result => result.StagedMutationCountBeforeConflictPublication):n0}");
            }

            using (Catalog reopened = Catalog.Open(path))
            using (LibraDexIndex<long, long> index = reopened.Indexes["proof"]["value"].Int64Keys<long>().Open())
            {
                ValidateScalar8ConcurrentBatchPublicationParity(index, threadCount, itemsPerThread, "reopened");
            }

            Console.WriteLine(
                $"scalar8-concurrent-batch-publication-sanity ok threads={threadCount} " +
                $"itemsPerThread={itemsPerThread:n0} total={checked((long)threadCount * itemsPerThread):n0}");
            return 0;
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Measures and validates the exact Abraxas ordinary-date index contract through direct LibraDex concurrent batches.<br/>
    /// Keys use the `DateTime` precision-SDT encoding selected by Abraxas, identities use the scalar `ulong` family, and every index retains `SingleKeyPerIdentity` so physical routing and multiplicity checks match Wherzit rather than a simplified scalar surrogate.<br/>
    /// Live and reopened range probes verify every strided worker tuple and retain the first missing entries in any failure.<br/>
    /// </summary>
    /// <param name="args">Optional <c>--threads</c>, <c>--items-per-thread</c>, <c>--duplicate-key</c>, and <c>--mixed-duplicate-key</c> controls for the focused date stress shape.<br/></param>
    /// <returns>Zero when every expected date/identity tuple remains visible live and after reopen.<br/></returns>
    private static int RunDateTimeScalar8ConcurrentBatchPublicationSanity(string[] args)
    {
        int threadCount = Math.Max(2, GetIntOption(args, "--threads", 4));
        int itemsPerThread = Math.Max(256, GetIntOption(args, "--items-per-thread", 2048));
        bool useDuplicateKey = args.Any(static argument => string.Equals(argument, "--duplicate-key", StringComparison.OrdinalIgnoreCase));
        bool useMixedDuplicateKey = args.Any(static argument => string.Equals(argument, "--mixed-duplicate-key", StringComparison.OrdinalIgnoreCase));
        string root = Path.Combine(
            Path.GetTempPath(),
            "LibraDex",
            $"datetime-scalar8-concurrent-publication-{Guid.NewGuid():N}");
        string path = Path.Combine(root, "proof.lbdx");
        Directory.CreateDirectory(root);

        try
        {
            LibraDexIndexShapeSpec shape;
            using (Catalog catalog = Catalog.Create(path, CatalogOptions.UInt64Identities))
            {
                shape = catalog.Indexes["proof"]["value"].Shape.Date<DateTime, ulong>(
                    DateKeys.ExactAndStructured,
                    DateTimeKeyEncoding.PrecisionSdt,
                    IndexKeys.NonUnique);
                using LibraDexIndex<DateTime, ulong> index = (LibraDexIndex<DateTime, ulong>)catalog.Indexes.Create(
                    shape,
                    new IndexOptions
                    {
                        Keys = IndexKeys.NonUnique,
                        IdentityKeyMultiplicity = IdentityKeyMultiplicity.SingleKeyPerIdentity,
                        DateKeys = DateKeys.ExactAndStructured,
                        DateTimeKeyEncoding = DateTimeKeyEncoding.PrecisionSdt
                    });
                using ManualResetEventSlim startGate = new(false);
                using CountdownEvent readyGate = new(threadCount);
                Task[] tasks = new Task[threadCount];
                var publications = new LibraDexConcurrentBatchPublishResult[threadCount];
                for (int worker = 0; worker < threadCount; worker++)
                {
                    int workerOrdinal = worker;
                    tasks[worker] = Task.Run(() =>
                    {
                        using LibraDexConcurrentBatch<DateTime, ulong> batch = index.BeginConcurrentBatch();
                        readyGate.Signal();
                        startGate.Wait();
                        for (int item = 0; item < itemsPerThread; item++)
                        {
                            DateTime key = CreateDateTimeScalar8ConcurrentBatchPublicationKey(
                                workerOrdinal,
                                item,
                                threadCount,
                                useDuplicateKey,
                                useMixedDuplicateKey);
                            ulong identity = CreateDateTimeScalar8ConcurrentBatchPublicationIdentity(workerOrdinal, item, threadCount);
                            LibraDexGenericInsertResult insert = batch.Insert(key, identity);
                            if (!insert.Inserted)
                            {
                                throw new InvalidDataException(
                                    $"DateTime SS8-8 concurrent publication proof rejected worker={workerOrdinal} item={item} key={key:O} identity={identity}.");
                            }
                        }

                        publications[workerOrdinal] = batch.Publish();
                    });
                }

                if (!readyGate.Wait(TimeSpan.FromSeconds(30)))
                    throw new InvalidDataException("DateTime SS8-8 concurrent publication proof could not ready every independent batch.");

                startGate.Set();
                Task.WaitAll(tasks);
                ValidateDateTimeScalar8ConcurrentBatchPublicationParity(
                    index,
                    threadCount,
                    itemsPerThread,
                    useDuplicateKey,
                    useMixedDuplicateKey,
                    "live");
                Console.WriteLine(
                    $"datetime scalar8 concurrent publication contexts={publications.Sum(static result => result.PublishedContextCount):n0} " +
                    $"ownershipConflicts={publications.Sum(static result => result.OwnershipConflictCount):n0} " +
                    $"conflictPublications={publications.Sum(static result => result.ConflictPublicationCount):n0} " +
                    $"topologyFallbacks={publications.Sum(static result => result.TopologyFallbackCount):n0} " +
                    $"peakStaged={publications.Max(static result => result.MaximumStagedMutationCount):n0} " +
                    $"stagedBeforeConflict={publications.Sum(static result => result.StagedMutationCountBeforeConflictPublication):n0}");
            }

            using (Catalog reopened = Catalog.Open(path, CatalogOptions.UInt64Identities))
            using (LibraDexIndex<DateTime, ulong> index = (LibraDexIndex<DateTime, ulong>)reopened.Indexes.Open(shape))
            {
                ValidateDateTimeScalar8ConcurrentBatchPublicationParity(
                    index,
                    threadCount,
                    itemsPerThread,
                    useDuplicateKey,
                    useMixedDuplicateKey,
                    "reopened");
            }

            Console.WriteLine(
                $"scalar8-concurrent-batch-publication-sanity ok shape=DateTime/UInt64 threads={threadCount} " +
                $"itemsPerThread={itemsPerThread:n0} total={checked((long)threadCount * itemsPerThread):n0} " +
                $"keys={(useDuplicateKey ? "duplicate" : useMixedDuplicateKey ? "mixed-duplicate" : "strided")}");
            return 0;
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Verifies every deterministic `DateTime`/`ulong` tuple emitted by the exact Abraxas-style concurrent date proof.<br/>
    /// Exact range readers keep validation on the public ordered index path and avoid trusting aggregate tuple counts alone.<br/>
    /// </summary>
    /// <param name="index">Live or reopened precision-SDT date index to inspect.<br/></param>
    /// <param name="threadCount">Number of independent batch owners used by the proof.<br/></param>
    /// <param name="itemsPerThread">Number of accepted inserts submitted by each owner.<br/></param>
    /// <param name="useDuplicateKey">Whether every tuple uses one identical date key to force concurrent exhausted-key growth.<br/></param>
    /// <param name="useMixedDuplicateKey">Whether alternating tuples share one date key while the remaining tuples retain a diverse routed distribution.<br/></param>
    /// <param name="phase">Short validation phase label included in failures.<br/></param>
    private static void ValidateDateTimeScalar8ConcurrentBatchPublicationParity(
        LibraDexIndex<DateTime, ulong> index,
        int threadCount,
        int itemsPerThread,
        bool useDuplicateKey,
        bool useMixedDuplicateKey,
        string phase)
    {
        List<string> missing = new(capacity: 8);
        long found = 0;
        for (int worker = 0; worker < threadCount; worker++)
        {
            for (int item = 0; item < itemsPerThread; item++)
            {
                DateTime expectedKey = CreateDateTimeScalar8ConcurrentBatchPublicationKey(
                    worker,
                    item,
                    threadCount,
                    useDuplicateKey,
                    useMixedDuplicateKey);
                ulong expectedIdentity = CreateDateTimeScalar8ConcurrentBatchPublicationIdentity(worker, item, threadCount);
                bool present = false;
                using (LibraDexRangeReader<DateTime, ulong> reader = index.OpenRangeReader(expectedKey, expectedKey))
                {
                    while (reader.TryReadNext(out DateTime key, out ulong identity))
                    {
                        if (key == expectedKey && identity == expectedIdentity)
                        {
                            present = true;
                            break;
                        }
                    }
                }

                if (present)
                    found++;
                else if (missing.Count < 8)
                    missing.Add($"w{worker}/i{item}/id{expectedIdentity}/key{expectedKey:O}");
            }
        }

        long expected = checked((long)threadCount * itemsPerThread);
        if (found != expected)
        {
            throw new InvalidDataException(
                $"DateTime SS8-8 concurrent publication {phase} parity expected {expected:n0} tuples, found {found:n0}; missing sample: {string.Join("; ", missing)}.");
        }
    }

    /// <summary>
    /// Reconstructs the `LastWriteTime` value at one Abraxas strided source position.<br/>
    /// The base and minute cadence exactly match the deterministic Wherzit lifecycle records used by the one-index profile.<br/>
    /// </summary>
    /// <param name="worker">Independent batch owner ordinal.<br/></param>
    /// <param name="item">Owner-local item ordinal.<br/></param>
    /// <param name="threadCount">Worker stride used by the Abraxas source loop.<br/></param>
    /// <param name="useDuplicateKey">Whether to collapse every source position onto one repeated date key.<br/></param>
    /// <param name="useMixedDuplicateKey">Whether alternating source positions collapse onto one repeated key while other positions remain distinct.<br/></param>
    /// <returns>The precision-preserving local `DateTime` key for that source position.<br/></returns>
    private static DateTime CreateDateTimeScalar8ConcurrentBatchPublicationKey(
        int worker,
        int item,
        int threadCount,
        bool useDuplicateKey,
        bool useMixedDuplicateKey)
        => useDuplicateKey || (useMixedDuplicateKey && (item & 1) == 0)
            ? new DateTime(2025, 2, 18, 15, 37, 59, DateTimeKind.Local)
            : new DateTime(2026, 2, 1).AddMinutes(checked(800_000 + worker + (item * threadCount)));

    /// <summary>
    /// Creates one monotonic scalar identity in source order for the exact Abraxas-style date proof.<br/>
    /// A nonzero high prefix exercises ordinary scalar identity comparisons while the low bits preserve the source-position mapping used in failures.<br/>
    /// </summary>
    /// <param name="worker">Independent batch owner ordinal.<br/></param>
    /// <param name="item">Owner-local item ordinal.<br/></param>
    /// <param name="threadCount">Worker stride used by the Abraxas source loop.<br/></param>
    /// <returns>The unique scalar identity for that source position.<br/></returns>
    private static ulong CreateDateTimeScalar8ConcurrentBatchPublicationIdentity(int worker, int item, int threadCount)
        => 0x0100_0000_0000_0000UL + checked((ulong)(worker + (item * threadCount) + 1));

    /// <summary>
    /// Verifies every deterministic scalar key/identity tuple emitted by the Abraxas-style strided concurrent batch proof.<br/>
    /// A bounded missing sample is retained in the failure so nondeterministic publication loss remains attributable to its worker and item.<br/>
    /// </summary>
    /// <param name="index">Live or reopened generic scalar index to inspect.<br/></param>
    /// <param name="threadCount">Number of independent batch owners used by the proof.<br/></param>
    /// <param name="itemsPerThread">Number of accepted inserts submitted by each owner.<br/></param>
    /// <param name="phase">Short validation phase label included in failures.<br/></param>
    private static void ValidateScalar8ConcurrentBatchPublicationParity(
        LibraDexIndex<long, long> index,
        int threadCount,
        int itemsPerThread,
        string phase)
    {
        List<string> missing = new(capacity: 8);
        long found = 0;
        for (int worker = 0; worker < threadCount; worker++)
        {
            for (int item = 0; item < itemsPerThread; item++)
            {
                long value = CreateScalar8ConcurrentBatchPublicationValue(worker, item, threadCount);
                bool present = false;
                using (LibraDexRangeReader<long, long> reader = index.OpenRangeReader(value, value))
                {
                    while (reader.TryReadNext(out long key, out long identity))
                    {
                        if (key == value && identity == value)
                        {
                            present = true;
                            break;
                        }
                    }
                }

                if (present)
                {
                    found++;
                }
                else if (missing.Count < 8)
                {
                    missing.Add($"w{worker}/i{item}/v{value}");
                }
            }
        }

        long expected = checked((long)threadCount * itemsPerThread);
        if (found != expected)
        {
            throw new InvalidDataException(
                $"SS8-8 concurrent publication {phase} parity expected {expected:n0} tuples, found {found:n0}; missing sample: {string.Join("; ", missing)}.");
        }
    }

    /// <summary>
    /// Recovers one source-order scalar value from a strided worker assignment matching Abraxas concurrent simple-index ingestion.<br/>
    /// The fixed positive base keeps generated keys away from sentinel/default values while preserving exact monotonic source order.<br/>
    /// </summary>
    /// <param name="worker">Independent batch owner ordinal.<br/></param>
    /// <param name="item">Owner-local item ordinal.<br/></param>
    /// <param name="threadCount">Worker stride used by the source loop.<br/></param>
    /// <returns>The deterministic key and identity value.<br/></returns>
    private static long CreateScalar8ConcurrentBatchPublicationValue(int worker, int item, int threadCount)
        => checked(800_000L + worker + ((long)item * threadCount));

    /// <summary>
    /// Proves that independent unpublished concurrent batches cannot reserve two different keys for one identity when the index declares <see cref="IdentityKeyMultiplicity.SingleKeyPerIdentity"/>.<br/>
    /// The fixture first creates a routed scalar index, then chooses two absent keys from distant routed regions so both inserts can remain shelf-local instead of being serialized by initial-route or split publication.<br/>
    /// Both batch owners are held at a barrier after their insert calls and before publication; therefore a passing result demonstrates cross-batch reservation rather than a later committed-index check.<br/>
    /// Live and reopened enumeration finally require exactly one tuple for the contested identity and require that tuple's key to match the sole accepted insert.<br/>
    /// </summary>
    /// <param name="args">Command arguments; this focused proof currently has no optional controls.<br/></param>
    /// <returns>Zero when exactly one independent batch accepts and publishes the contested identity.<br/></returns>
    private static int RunSingleKeyCrossBatchReservationSanity(string[] args)
    {
        const long contestedIdentity = 9_000_000_001L;
        const long firstCandidateKey = 2_001L;
        const long secondCandidateKey = 14_001L;
        string root = Path.Combine(
            Path.GetTempPath(),
            "LibraDex",
            $"single-key-cross-batch-{Guid.NewGuid():N}");
        string path = Path.Combine(root, "proof.lbdx");
        Directory.CreateDirectory(root);

        try
        {
            long acceptedKey;
            using (Catalog catalog = Catalog.Create(path))
            using (LibraDexIndex<long, long> index = catalog.Indexes["proof"]["value"].Int64Keys<long>().Create(
                options: new IndexOptions
                {
                    Keys = IndexKeys.NonUnique,
                    IdentityKeyMultiplicity = IdentityKeyMultiplicity.SingleKeyPerIdentity
                }))
            {
                using (LibraDexConcurrentBatch<long, long> seed = index.BeginConcurrentBatch())
                {
                    for (long key = 0; key < 16_000; key += 2)
                    {
                        LibraDexGenericInsertResult inserted = seed.Insert(key, key + 1);
                        if (!inserted.Inserted)
                            throw new InvalidDataException($"Cross-batch reservation fixture could not seed key {key:n0}.");
                    }

                    _ = seed.Publish();
                }

                using LibraDexConcurrentBatch<long, long> first = index.BeginConcurrentBatch();
                using LibraDexConcurrentBatch<long, long> second = index.BeginConcurrentBatch();
                using ManualResetEventSlim startGate = new(false);
                using ManualResetEventSlim publishGate = new(false);
                using CountdownEvent stagedGate = new(2);
                LibraDexGenericInsertResult firstInsert = default;
                LibraDexGenericInsertResult secondInsert = default;

                Task firstTask = Task.Run(() =>
                {
                    startGate.Wait();
                    firstInsert = first.Insert(firstCandidateKey, contestedIdentity);
                    stagedGate.Signal();
                    publishGate.Wait();
                    _ = first.Publish();
                });
                Task secondTask = Task.Run(() =>
                {
                    startGate.Wait();
                    secondInsert = second.Insert(secondCandidateKey, contestedIdentity);
                    stagedGate.Signal();
                    publishGate.Wait();
                    _ = second.Publish();
                });

                startGate.Set();
                if (!stagedGate.Wait(TimeSpan.FromSeconds(30)))
                    throw new InvalidDataException("Cross-batch reservation proof could not stage both independent insert attempts.");

                publishGate.Set();
                Task.WaitAll(firstTask, secondTask);
                int accepted = (firstInsert.Inserted ? 1 : 0) + (secondInsert.Inserted ? 1 : 0);
                if (accepted != 1)
                {
                    throw new InvalidDataException(
                        $"SingleKeyPerIdentity cross-batch reservation accepted {accepted} conflicting inserts; expected exactly one.");
                }

                acceptedKey = firstInsert.Inserted ? firstCandidateKey : secondCandidateKey;
                ValidateSingleKeyCrossBatchReservationResult(index, contestedIdentity, acceptedKey, "live");
            }

            using (Catalog reopened = Catalog.Open(path))
            using (LibraDexIndex<long, long> index = reopened.Indexes["proof"]["value"].Int64Keys<long>().Open())
            {
                ValidateSingleKeyCrossBatchReservationResult(index, contestedIdentity, acceptedKey, "reopened");
            }

            Console.WriteLine(
                $"single-key-cross-batch-reservation-sanity ok identity={contestedIdentity} acceptedKey={acceptedKey}");
            return 0;
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Validates the final single-key association for the contested cross-batch identity.<br/>
    /// Enumeration is intentional because the proof must detect both an unexpected second key and a missing accepted key rather than trusting only identity-to-key acceleration state.<br/>
    /// </summary>
    /// <param name="index">The live or reopened index to inspect.<br/></param>
    /// <param name="identity">The identity concurrently offered under two keys.<br/></param>
    /// <param name="expectedKey">The key belonging to the sole accepted insert.<br/></param>
    /// <param name="phase">The validation phase included in failures.<br/></param>
    private static void ValidateSingleKeyCrossBatchReservationResult(
        LibraDexIndex<long, long> index,
        long identity,
        long expectedKey,
        string phase)
    {
        List<long> keys = [];
        using LibraDexRangeReader<long, long> reader = index.OpenReader();
        while (reader.TryReadNext(out long key, out long currentIdentity))
        {
            if (currentIdentity == identity)
                keys.Add(key);
        }

        if (keys.Count != 1 || keys[0] != expectedKey)
        {
            throw new InvalidDataException(
                $"SingleKeyPerIdentity cross-batch {phase} result expected only key {expectedKey}, found [{string.Join(", ", keys)}].");
        }
    }

    /// <summary>
    /// Stresses the session-local large mutable-shelf pool with independent `VS8` writer contexts that publish and abort concurrently.<br/>
    /// Each worker owns a separate 128-KiB root-prefix shelf, so the test isolates byte-pool rent/return synchronization from same-shelf admission contention while still sharing one session pool.<br/>
    /// Live and reopened range validation requires every published identity and rejects every aborted identity, proving buffer reuse does not leak stale shelf bytes or mutate authoritative topology.<br/>
    /// </summary>
    /// <param name="args">Optional <c>--threads</c>, <c>--cycles</c>, and <c>--path</c> controls for the focused pool lifecycle stress.<br/></param>
    /// <returns>Zero when all worker contexts complete without pool corruption and live/reopened visibility matches publish versus abort decisions.<br/></returns>
    private static int RunVarKeyScalar8ConcurrentPoolAbortSanity(string[] args)
    {
        int threadCount = Math.Clamp(GetIntOption(args, "--threads", 16), 2, 32);
        int cycles = Math.Max(32, GetIntOption(args, "--cycles", 256));
        string path = GetOption(
            args,
            "--path",
            Path.Combine(Path.GetTempPath(), "LibraDex", $"vs8-concurrent-pool-abort-{Guid.NewGuid():N}.lbdx"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Delete(path);

        DataKernelOptions options = CreateDesignPerfOptions();
        long rootOffset;
        try
        {
            using (LibraDexFileSession session = LibraDexFileSession.Initialize(path, options, CreateDesignPerfMetadata(823), DataKernelTelemetryOptions.EnabledOptions))
            {
                (RouterSnapshot root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "vs8pool", 0));
                rootOffset = root.Offset;
                VarKeyScalar8Profile profile = VarKeyScalar8Profile.Default128KiB;
                for (int worker = 0; worker < threadCount; worker++)
                {
                    byte prefix = checked((byte)(0x20 + worker));
                    _ = session.CreateVarKeyScalar8ShelfAndLinkRootRoute(rootOffset, prefix, profile);
                }

                using ManualResetEventSlim startGate = new(false);
                Task[] tasks = new Task[threadCount];
                for (int worker = 0; worker < threadCount; worker++)
                {
                    int workerOrdinal = worker;
                    tasks[worker] = Task.Run(() =>
                    {
                        startGate.Wait();
                        for (int cycle = 0; cycle < cycles; cycle++)
                        {
                            byte prefix = checked((byte)(0x20 + workerOrdinal));
                            byte[] key = new byte[6];
                            key[0] = prefix;
                            BinaryPrimitives.WriteInt32BigEndian(key.AsSpan(1, sizeof(int)), cycle);
                            key[^1] = 0xA5;
                            ulong identity = checked((ulong)(workerOrdinal + 1) * 1_000_000UL + (uint)cycle + 1UL);
                            LibraDexWriteContext context = session.BeginVarKeyScalar8WriteContext();
                            try
                            {
                                session.EnterVarKeyScalar8TopologyReadForWriteContext(context, rootOffset);
                                VarKeyScalar8RoutedInsertResult insert = session.InsertWalkedRoutedVarKeyScalar8NoSplitForWriteContext(
                                    context,
                                    rootOffset,
                                    profile.MaxKeyLength,
                                    key,
                                    identity,
                                    allowDuplicateKeys: true,
                                    maxRouterHops: LibraDexFileSession.DefaultVarKeyScalar8MaxRouterHops);
                                if (insert.InsertResult != VarKeyScalar8InsertResult.Inserted)
                                {
                                    throw new InvalidDataException($"VS8 pool lifecycle worker={workerOrdinal} cycle={cycle} returned {insert.Kind}/{insert.InsertResult}.");
                                }

                                if ((cycle & 1) == 0)
                                {
                                    _ = session.PublishVarKeyScalar8WriteContext(context);
                                }
                                else
                                {
                                    session.AbortVarKeyScalar8WriteContext(context);
                                }
                            }
                            catch
                            {
                                session.AbortVarKeyScalar8WriteContext(context);
                                throw;
                            }
                        }
                    });
                }

                startGate.Set();
                Task.WaitAll(tasks);
                ValidateVarKeyScalar8ConcurrentPoolAbortParity(session, rootOffset, threadCount, cycles, "live");
            }

            using (LibraDexFileSession reopened = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions))
            {
                ValidateVarKeyScalar8ConcurrentPoolAbortParity(reopened, rootOffset, threadCount, cycles, "reopened");
            }

            Console.WriteLine(
                $"vs8-concurrent-pool-abort-sanity ok path={path} threads={threadCount} cycles={cycles:n0} " +
                $"published={checked((long)threadCount * ((cycles + 1) / 2)):n0} aborted={checked((long)threadCount * (cycles / 2)):n0}");
            return 0;
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Validates published-versus-aborted identity visibility for every independent root-prefix shelf in the concurrent large-pool stress.<br/>
    /// Canonical key order matches cycle order, allowing one allocation-bounded range read per worker and exact ordinal comparison without a hash set.<br/>
    /// </summary>
    /// <param name="session">The live or reopened session to validate.<br/></param>
    /// <param name="rootOffset">The stable `VS8` root router offset.<br/></param>
    /// <param name="threadCount">The number of independent prefix owners.<br/></param>
    /// <param name="cycles">The number of alternating publish/abort contexts submitted by each worker.<br/></param>
    /// <param name="phase">The validation phase included in failure diagnostics.<br/></param>
    private static void ValidateVarKeyScalar8ConcurrentPoolAbortParity(
        LibraDexFileSession session,
        long rootOffset,
        int threadCount,
        int cycles,
        string phase)
    {
        int expectedCount = (cycles + 1) / 2;
        ulong[] identities = new ulong[expectedCount + 1];
        for (int worker = 0; worker < threadCount; worker++)
        {
            byte prefix = checked((byte)(0x20 + worker));
            byte[] lower = [prefix, 0, 0, 0, 0, 0];
            byte[] upper = [prefix, byte.MaxValue, byte.MaxValue, byte.MaxValue, byte.MaxValue, byte.MaxValue];
            int copied = session.ReadVarKeyScalar8IdentityRange(
                rootOffset,
                VarKeyScalar8Profile.Default128KiB.MaxKeyLength,
                lower,
                upper,
                identities);
            if (copied != expectedCount)
            {
                throw new InvalidDataException($"VS8 pool lifecycle {phase} worker={worker} returned {copied:N0} identities; expected {expectedCount:N0}.");
            }

            int ordinal = 0;
            for (int cycle = 0; cycle < cycles; cycle += 2)
            {
                ulong expectedIdentity = checked((ulong)(worker + 1) * 1_000_000UL + (uint)cycle + 1UL);
                if (identities[ordinal] != expectedIdentity)
                {
                    throw new InvalidDataException($"VS8 pool lifecycle {phase} worker={worker} ordinal={ordinal} returned identity={identities[ordinal]}; expected={expectedIdentity}.");
                }
                ordinal++;
            }
        }
    }
}
