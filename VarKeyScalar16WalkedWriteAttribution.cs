namespace LibraDex;

/// <summary>
/// Attributes elapsed Stopwatch ticks inside one walked `VS16` write operation.<br/>
/// The counters are diagnostic only and are intended for harness-level performance attribution, not public API contracts.<br/>
/// </summary>
/// <param name="RouteWalkTicks">Ticks spent walking routers to the reached shelf.</param>
/// <param name="ShelfReadTicks">Ticks spent resolving the reached shelf bytes from dirty cache, read cache, or DataKernel.</param>
/// <param name="MutationTicks">Ticks spent decoding/searching/mutating the shelf image.</param>
/// <param name="StageTicks">Ticks spent staging the resulting shelf image or immediate rewrite request.</param>
/// <param name="StructuralTicks">Ticks spent in growth or shelf-transform structural work after the first shelf mutation reports full.</param>
internal readonly record struct VarKeyScalar16WalkedWriteAttribution(
    long RouteWalkTicks,
    long ShelfReadTicks,
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
    public static VarKeyScalar16WalkedWriteAttribution operator +(
        VarKeyScalar16WalkedWriteAttribution left,
        VarKeyScalar16WalkedWriteAttribution right)
    {
        return new VarKeyScalar16WalkedWriteAttribution(
            left.RouteWalkTicks + right.RouteWalkTicks,
            left.ShelfReadTicks + right.ShelfReadTicks,
            left.MutationTicks + right.MutationTicks,
            left.StageTicks + right.StageTicks,
            left.StructuralTicks + right.StructuralTicks);
    }
}
