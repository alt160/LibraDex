namespace LibraDex;

internal enum VarKeyScalar8TransformRouterKind
{
    ExpandedOneByte = 0,
    ExpandedOneByteChain = 1,
    CompressedMultiByte = 2
}

/// <summary>
/// Describes the physical router shape selected by the cold `VS8` shelf-transform policy.<br/>
/// Hot insert hints can influence this plan, but the plan is produced only after the transform path has exact shelf population evidence.<br/>
/// The router writer consumes this value as simple already-decided metadata, keeping lookup and range-read paths free of heuristic analysis.<br/>
/// </summary>
/// <param name="Kind">The physical router shape to write.</param>
/// <param name="PrefixByteCount">The number of prefix bytes consumed when <paramref name="Kind"/> is multi-byte.</param>
/// <param name="PrefixStem">The exact prefix stem bytes used when <paramref name="Kind"/> is multi-byte.</param>
internal readonly record struct VarKeyScalar8TransformRouterPlan(
    VarKeyScalar8TransformRouterKind Kind,
    byte PrefixByteCount,
    ReadOnlyMemory<byte> PrefixStem)
{
    public static VarKeyScalar8TransformRouterPlan ExpandedOneByte =>
        new(VarKeyScalar8TransformRouterKind.ExpandedOneByte, 1, ReadOnlyMemory<byte>.Empty);

    public static VarKeyScalar8TransformRouterPlan ExpandedOneByteChain =>
        new(VarKeyScalar8TransformRouterKind.ExpandedOneByteChain, 1, ReadOnlyMemory<byte>.Empty);

    public static VarKeyScalar8TransformRouterPlan CompressedMultiByte(byte prefixByteCount, ReadOnlyMemory<byte> prefixStem)
    {
        return new VarKeyScalar8TransformRouterPlan(
            VarKeyScalar8TransformRouterKind.CompressedMultiByte,
            prefixByteCount,
            prefixStem);
    }
}
