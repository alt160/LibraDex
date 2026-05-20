namespace LibraDex;

internal enum VarKeyVarIdentityRoutedInsertKind
{
    Invalid = 0,
    NoOp = 1,
    KeyConflict = 2,
    WalkedNoSplit = 3,
    WalkedGrow = 4,
    WalkedShelfTransformSplit = 5,
    WalkedCreatedInitialShelf = 6,
    Full = 7
}

internal readonly record struct VarKeyVarIdentityRoutedInsertResult(
    VarKeyVarIdentityRoutedInsertKind Kind,
    VarKeyVarIdentityInsertResult InsertResult,
    long SourceShelfOffset,
    long NewOffset,
    DataKernelCommitTelemetry Commit,
    int ItemCount,
    int ShelfExtentSize,
    ushort RouterDepth = 0,
    ushort StructuralRouterDepth = 0);
