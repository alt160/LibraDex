namespace LibraDex;

/// <summary>
/// Identifies one internal `VS8` variable-key/scalar-identity index instance for runtime operations.<br/>
/// The handle intentionally carries only the root router offset, key width limit, optimizer route fanout, and internal optimizer policy; public naming, codecs, and developer-facing optimization APIs remain separate future layers.<br/>
/// </summary>
/// <param name="RootRouterOffset">The file offset where this index's root router starts.</param>
/// <param name="MaxKeyLength">The maximum raw key length accepted by this runtime index.</param>
/// <param name="OptimizerRouteFanout">The compressed-route fanout requested by optimizer maintenance.</param>
/// <param name="OptimizerPolicy">The internal optimizer observation and maintenance scheduling policy.</param>
internal readonly record struct VarKeyScalar8IndexHandle(
    long RootRouterOffset,
    int MaxKeyLength,
    int OptimizerRouteFanout,
    VarLenOptimizerMaintenancePolicy OptimizerPolicy)
{
    /// <summary>
    /// Validates the runtime handle before it is used by an internal `VS8` index operation.<br/>
    /// Offset zero is reserved for the superblock, keys are bounded to the current varlen shelf contract, and optimizer fanout must be positive.<br/>
    /// </summary>
    /// <exception cref="InvalidDataException">Thrown when the handle cannot identify a valid runtime index root or policy shape.</exception>
    public void Validate()
    {
        if (RootRouterOffset <= 0)
        {
            throw new InvalidDataException("A VS8 index handle must reference a positive root router offset.");
        }

        if (MaxKeyLength <= 0 || MaxKeyLength > 1024)
        {
            throw new InvalidDataException("A VS8 index handle must use a max key length from 1 through 1024.");
        }

        if (OptimizerRouteFanout <= 0 || OptimizerRouteFanout > 256)
        {
            throw new InvalidDataException("A VS8 index handle must use an optimizer fanout from 1 through 256.");
        }
    }
}

/// <summary>
/// Identifies one internal `VS16` variable-key/scalar-identity index instance for runtime operations.<br/>
/// This is the fixed-identity-width counterpart to <see cref="VarKeyScalar8IndexHandle"/> and keeps the lifecycle state identical except for the shape-specific operations that consume it.<br/>
/// </summary>
/// <param name="RootRouterOffset">The file offset where this index's root router starts.</param>
/// <param name="MaxKeyLength">The maximum raw key length accepted by this runtime index.</param>
/// <param name="OptimizerRouteFanout">The compressed-route fanout requested by optimizer maintenance.</param>
/// <param name="OptimizerPolicy">The internal optimizer observation and maintenance scheduling policy.</param>
internal readonly record struct VarKeyScalar16IndexHandle(
    long RootRouterOffset,
    int MaxKeyLength,
    int OptimizerRouteFanout,
    VarLenOptimizerMaintenancePolicy OptimizerPolicy)
{
    /// <summary>
    /// Validates the runtime handle before it is used by an internal `VS16` index operation.<br/>
    /// Offset zero is reserved for the superblock, keys are bounded to the current varlen shelf contract, and optimizer fanout must be positive.<br/>
    /// </summary>
    /// <exception cref="InvalidDataException">Thrown when the handle cannot identify a valid runtime index root or policy shape.</exception>
    public void Validate()
    {
        if (RootRouterOffset <= 0)
        {
            throw new InvalidDataException("A VS16 index handle must reference a positive root router offset.");
        }

        if (MaxKeyLength <= 0 || MaxKeyLength > 1024)
        {
            throw new InvalidDataException("A VS16 index handle must use a max key length from 1 through 1024.");
        }

        if (OptimizerRouteFanout <= 0 || OptimizerRouteFanout > 256)
        {
            throw new InvalidDataException("A VS16 index handle must use an optimizer fanout from 1 through 256.");
        }
    }
}
