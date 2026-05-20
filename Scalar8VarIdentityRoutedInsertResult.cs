namespace LibraDex;

internal enum Scalar8VarIdentityRoutedInsertKind
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

internal readonly record struct Scalar8VarIdentityRoutedInsertResult(
    Scalar8VarIdentityRoutedInsertKind Kind,
    Scalar8VarIdentityInsertResult InsertResult,
    long PrimaryOffset,
    long NewShelfOffset,
    DataKernelCommitTelemetry Commit,
    int TargetShelfItemCount = 0,
    int TargetShelfExtentSize = 0,
    ushort TargetRouterDepth = 0,
    ushort StructuralRouterDepth = 0);
