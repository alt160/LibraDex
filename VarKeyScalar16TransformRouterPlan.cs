namespace LibraDex;

internal enum VarKeyScalar16TransformRouterKind
{
    ExpandedOneByte = 0,
    ExpandedOneByteChain = 1,
    CompressedMultiByte = 2
}

/// <summary>
/// Describes the physical router shape selected by the cold `VS16` shelf-transform policy.<br/>
/// Hot insert hints can influence this plan, but the plan is produced only after the transform path has exact shelf population evidence.<br/>
/// The router writer consumes this value as simple already-decided metadata, keeping lookup and range-read paths free of heuristic analysis.<br/>
/// </summary>
/// <param name="Kind">The physical router shape to write.</param>
/// <param name="PrefixByteCount">The number of prefix bytes consumed when <paramref name="Kind"/> is multi-byte.</param>
/// <param name="PrefixStem">The exact prefix stem bytes used when <paramref name="Kind"/> is multi-byte.</param>
internal readonly record struct VarKeyScalar16TransformRouterPlan(
    VarKeyScalar16TransformRouterKind Kind,
    byte PrefixByteCount,
    ReadOnlyMemory<byte> PrefixStem)
{
    public static VarKeyScalar16TransformRouterPlan ExpandedOneByte =>
        new(VarKeyScalar16TransformRouterKind.ExpandedOneByte, 1, ReadOnlyMemory<byte>.Empty);

    public static VarKeyScalar16TransformRouterPlan ExpandedOneByteChain =>
        new(VarKeyScalar16TransformRouterKind.ExpandedOneByteChain, 1, ReadOnlyMemory<byte>.Empty);

    public static VarKeyScalar16TransformRouterPlan CompressedMultiByte(byte prefixByteCount, ReadOnlyMemory<byte> prefixStem)
    {
        return new VarKeyScalar16TransformRouterPlan(
            VarKeyScalar16TransformRouterKind.CompressedMultiByte,
            prefixByteCount,
            prefixStem);
    }
}
