namespace LibraDex;

/// <summary>
/// Attributes elapsed Stopwatch ticks inside one walked `SV16` write operation.<br/>
/// The counters are diagnostic only and are intended for harness-level performance attribution, not public API contracts.<br/>
/// </summary>
/// <param name="RouteWalkTicks">Ticks spent walking routers to the reached shelf.</param>
/// <param name="ShelfReadTicks">Ticks spent resolving the reached shelf bytes from dirty cache, read cache, or DataKernel.</param>
/// <param name="DuplicateChainTicks">Ticks spent attempting duplicate-run chain insertion before the reached shelf is mutated.</param>
/// <param name="MutationTicks">Ticks spent decoding/searching/mutating the reached shelf image.</param>
/// <param name="StageTicks">Ticks spent staging the no-split shelf image or immediate rewrite request.</param>
/// <param name="StructuralTicks">Ticks spent in growth, split, duplicate-tail, or duplicate-chain structural work after the first shelf mutation reports full.</param>
internal readonly record struct Scalar16VarIdentityWalkedWriteAttribution(
    long RouteWalkTicks,
    long ShelfReadTicks,
    long DuplicateChainTicks,
    long MutationTicks,
    long StageTicks,
    long StructuralTicks)
{
    /// <summary>
    /// Adds two attribution rows together.<br/>
    /// This keeps benchmark aggregation allocation-free and makes each counter an independent elapsed-time bucket.<br/>
    /// </summary>
    /// <param name="left">The left attribution row.</param>
    /// <param name="right">The right attribution row.</param>
    /// <returns>The summed attribution row.</returns>
    public static Scalar16VarIdentityWalkedWriteAttribution operator +(
        Scalar16VarIdentityWalkedWriteAttribution left,
        Scalar16VarIdentityWalkedWriteAttribution right)
    {
        return new Scalar16VarIdentityWalkedWriteAttribution(
            left.RouteWalkTicks + right.RouteWalkTicks,
            left.ShelfReadTicks + right.ShelfReadTicks,
            left.DuplicateChainTicks + right.DuplicateChainTicks,
            left.MutationTicks + right.MutationTicks,
            left.StageTicks + right.StageTicks,
            left.StructuralTicks + right.StructuralTicks);
    }
}
