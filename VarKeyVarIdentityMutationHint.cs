namespace LibraDex;

internal readonly record struct VarKeyVarIdentityMutationHint(
    bool Sampled,
    int ComparedNeighborCount,
    int StartDepth,
    int MaxCommonPrefixDepth,
    int MaxCommonPrefixBytes);
