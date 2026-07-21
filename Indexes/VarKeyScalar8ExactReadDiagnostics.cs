using System.Diagnostics;

namespace LibraDex;

internal sealed partial class VarKeyScalar8Index
{
    /// <summary>
    /// Measures one exact `VS8` lookup through the current range reader and compares its traversal with exact router selection.<br/>
    /// This proof-only surface captures logical structure work, thread-local allocation, DataKernel read telemetry, result count, and identity checksum without changing the public read API.<br/>
    /// </summary>
    /// <param name="key">The developer-facing exact raw key.<br/></param>
    /// <returns>The current range-read measurement and exact-route comparison for the same encoded key.<br/></returns>
    internal VarKeyScalar8ExactReadProof DiagnoseExactRead(ReadOnlySpan<byte> key)
    {
        ThrowIfDisposed();
        byte[] encodedLowerKey = LibraDexVarLenKeyCodec.Encode(key, handle.MaxKeyLength, nameof(key));
        byte[] encodedUpperKey = LibraDexVarLenKeyCodec.Encode(key, handle.MaxKeyLength, nameof(key));

        _ = session.GetAndResetReadTelemetry();
        long rangeAllocatedStart = GC.GetAllocatedBytesForCurrentThread();
        long rangeStart = Stopwatch.GetTimestamp();
        long resultCount = 0;
        ulong checksum = 0;
        VarKeyScalar8RangeReadDiagnostics rangeDiagnostics;
        using (VarKeyScalar8RangeReader reader = session.OpenVarKeyScalar8RangeReader(
            handle.RootRouterOffset,
            handle.MaxKeyLength,
            encodedLowerKey,
            encodedUpperKey,
            decodeLogicalKeys: false,
            captureDiagnostics: true))
        {
            while (reader.MoveNext())
            {
                checksum ^= reader.CurrentEncodedIdentity;
                resultCount++;
            }

            rangeDiagnostics = reader.Diagnostics;
        }

        long rangeTicks = Stopwatch.GetTimestamp() - rangeStart;
        long rangeAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - rangeAllocatedStart;
        DataKernelReadTelemetry rangeReadTelemetry = session.GetAndResetReadTelemetry();

        _ = session.GetAndResetReadTelemetry();
        long exactAllocatedStart = GC.GetAllocatedBytesForCurrentThread();
        long exactStart = Stopwatch.GetTimestamp();
        VarKeyScalar8ExactRouteDiagnostics exactRoute = session.DiagnoseVarKeyScalar8ExactRoute(
            handle.RootRouterOffset,
            encodedLowerKey);
        long exactTicks = Stopwatch.GetTimestamp() - exactStart;
        long exactAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - exactAllocatedStart;
        DataKernelReadTelemetry exactReadTelemetry = session.GetAndResetReadTelemetry();

        VarKeyScalar8RoutePathTarget productionTarget = session.WalkVarKeyScalar8RoutePathTarget(
            handle.RootRouterOffset,
            encodedLowerKey,
            LibraDexFileSession.DefaultVarKeyScalar8MaxRouterHops);

        return new VarKeyScalar8ExactReadProof(
            resultCount,
            checksum,
            rangeTicks,
            rangeAllocatedBytes,
            rangeReadTelemetry,
            rangeDiagnostics,
            exactTicks,
            exactAllocatedBytes,
            exactReadTelemetry,
            exactRoute,
            productionTarget.Target.Kind == exactRoute.TargetKind &&
            productionTarget.Target.Offset == exactRoute.TargetOffset);
    }
}

/// <summary>
/// Compares one current range-based `VS8` exact lookup with exact router selection for the same key.<br/>
/// Timing values use raw <see cref="Stopwatch"/> ticks so the proof runner can report duration without losing precision.<br/>
/// </summary>
/// <param name="ResultCount">The identities returned by the current exact range read.<br/></param>
/// <param name="IdentityChecksum">The XOR checksum of returned encoded identities.<br/></param>
/// <param name="RangeTicks">The elapsed ticks for the current range-based exact read.<br/></param>
/// <param name="RangeAllocatedBytes">The thread-local bytes allocated by the current exact read and diagnostic capture.<br/></param>
/// <param name="RangeReadTelemetry">The DataKernel reads observed during the current exact read.<br/></param>
/// <param name="RangeDiagnostics">The logical route and shelf work performed by the current exact read.<br/></param>
/// <param name="ExactRouteTicks">The elapsed ticks for exact router selection without leaf decoding.<br/></param>
/// <param name="ExactRouteAllocatedBytes">The thread-local bytes allocated by exact router selection and diagnostic capture.<br/></param>
/// <param name="ExactRouteReadTelemetry">The DataKernel reads observed during exact router selection.<br/></param>
/// <param name="ExactRoute">The structural exact-route result.<br/></param>
/// <param name="MatchesProductionRouteWalker">Whether the diagnostic exact route selected the same leaf as the production walked-write route selector.<br/></param>
internal readonly record struct VarKeyScalar8ExactReadProof(
    long ResultCount,
    ulong IdentityChecksum,
    long RangeTicks,
    long RangeAllocatedBytes,
    DataKernelReadTelemetry RangeReadTelemetry,
    VarKeyScalar8RangeReadDiagnostics RangeDiagnostics,
    long ExactRouteTicks,
    long ExactRouteAllocatedBytes,
    DataKernelReadTelemetry ExactRouteReadTelemetry,
    VarKeyScalar8ExactRouteDiagnostics ExactRoute,
    bool MatchesProductionRouteWalker);
