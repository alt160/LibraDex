namespace LibraDex;

internal enum VarKeyScalar8RoutedInsertKind
{
    NoOp = 0,
    WalkedNoSplit = 1,
    WalkedGrow = 2,
    WalkedShelfTransformSplit = 3,
    Full = 4,
    KeyConflict = 5,
    Invalid = 6
}

internal readonly record struct VarKeyScalar8RoutedInsertResult(
    VarKeyScalar8RoutedInsertKind Kind,
    VarKeyScalar8InsertResult InsertResult,
    long PrimaryOffset,
    long NewShelfOffset,
    DataKernelCommitTelemetry Commit,
    VarKeyScalar8WalkedWriteAttribution Attribution = default,
    int TargetShelfItemCount = 0,
    int TargetShelfExtentSize = 0,
    ushort TargetRouterDepth = 0,
    ushort StructuralRouterDepth = 0);
