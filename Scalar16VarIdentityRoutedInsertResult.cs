namespace LibraDex;

internal enum Scalar16VarIdentityRoutedInsertKind
{
    NoOp = 0,
    WalkedNoSplit = 1,
    WalkedGrow = 2,
    WalkedShelfSplit = 3,
    Full = 4,
    KeyConflict = 5,
    Invalid = 6,
    WalkedDuplicateRunOverflow = 7
}

internal readonly record struct Scalar16VarIdentityRoutedInsertResult(
    Scalar16VarIdentityRoutedInsertKind Kind,
    Scalar16VarIdentityInsertResult InsertResult,
    long PrimaryOffset,
    long NewShelfOffset,
    DataKernelCommitTelemetry Commit,
    int TargetShelfItemCount = 0,
    int TargetShelfExtentSize = 0,
    ushort TargetRouterDepth = 0,
    ushort StructuralRouterDepth = 0);
