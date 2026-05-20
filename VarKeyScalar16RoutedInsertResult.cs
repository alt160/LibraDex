namespace LibraDex;

internal enum VarKeyScalar16RoutedInsertKind
{
    NoOp = 0,
    WalkedNoSplit = 1,
    WalkedGrow = 2,
    WalkedShelfTransformSplit = 3,
    Full = 4,
    KeyConflict = 5,
    Invalid = 6
}

internal readonly record struct VarKeyScalar16RoutedInsertResult(
    VarKeyScalar16RoutedInsertKind Kind,
    VarKeyScalar16InsertResult InsertResult,
    long PrimaryOffset,
    long NewShelfOffset,
    DataKernelCommitTelemetry Commit,
    VarKeyScalar16WalkedWriteAttribution Attribution = default,
    int TargetShelfItemCount = 0,
    int TargetShelfExtentSize = 0,
    ushort TargetRouterDepth = 0,
    ushort StructuralRouterDepth = 0);
