using System.Runtime.CompilerServices;

namespace LibraDex;

internal sealed partial class DataKernel
{
    /// <summary>Finishes a fragmented read with one backing pass followed by staging-order overlays.<br/>
    /// Invoked only after eight visibility chunks with at least thirty-two pending segments, leaving ordinary reads on their direct path.<br/>
    /// A complete backing image is required: if an append or memory hole prevents that, returns zero and the caller reconstructs the range with boundary reads.<br/>
    /// Any partial destination bytes from an unsuccessful attempt are overwritten by that normal continuation.<br/>
    /// Uses the caller's destination, allocates no scratch structures, and never changes commit phase or snapshot semantics.<br/></summary>
    /// <param name="offset">Absolute start of the remaining read.<br/></param>
    /// <param name="destination">Unfilled suffix of the caller's destination.<br/></param>
    /// <returns>The full suffix length on success, or zero when ordinary boundary reads must continue.<br/></returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int TryReadBackingWithPendingOverlay(long offset, Span<byte> destination)
    {
        int copied = 0;
        while (copied < destination.Length)
        {
            int read = backingKind == DataKernelBackingKind.File
                ? ReadFile(offset + copied, destination[copied..])
                : ReadMemory(offset + copied, destination[copied..]);
            if (read == 0) return 0;
            copied += read;
        }
        long end = offset + destination.Length;
        for (int i = 0; i < pendingSegments.Count; i++)
        {
            PendingSegment segment = pendingSegments[i];
            long start = Math.Max(offset, segment.Offset), stop = Math.Min(end, segment.Offset + segment.Length);
            if (start >= stop) continue;
            segment.Buffer.AsSpan(segment.SourceOffset + checked((int)(start - segment.Offset)), checked((int)(stop - start)))
                .CopyTo(destination.Slice(checked((int)(start - offset))));
        }
        return destination.Length;
    }
}
