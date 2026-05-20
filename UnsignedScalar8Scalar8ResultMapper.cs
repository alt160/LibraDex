namespace LibraDex;

/// <summary>
/// Maps encoded `SS8-8` public results into typed unsigned scalar wrapper results.<br/>
/// The mapper keeps public unsigned wrappers thin while preventing encoded type names from leaking through typed API return values.<br/>
/// </summary>
internal static class UnsignedScalar8Scalar8ResultMapper
{
    /// <summary>
    /// Maps an encoded insert outcome into the matching unsigned scalar insert outcome.<br/>
    /// </summary>
    /// <param name="outcome">The encoded core insert outcome.</param>
    /// <returns>The typed unsigned insert outcome.</returns>
    /// <exception cref="InvalidDataException">Thrown when the encoded core returns an unknown insert outcome.</exception>
    internal static UnsignedScalar8Scalar8InsertOutcome MapInsertOutcome(Scalar8Scalar8EncodedInsertOutcome outcome)
    {
        return outcome switch
        {
            Scalar8Scalar8EncodedInsertOutcome.Inserted => UnsignedScalar8Scalar8InsertOutcome.Inserted,
            Scalar8Scalar8EncodedInsertOutcome.AlreadyPresent => UnsignedScalar8Scalar8InsertOutcome.AlreadyPresent,
            Scalar8Scalar8EncodedInsertOutcome.KeyConflict => UnsignedScalar8Scalar8InsertOutcome.KeyConflict,
            _ => throw new InvalidDataException($"Unknown SS8-8 encoded insert outcome {outcome}.")
        };
    }

    /// <summary>
    /// Maps an encoded batch commit result into the typed unsigned scalar batch commit result.<br/>
    /// </summary>
    /// <param name="result">The encoded core batch commit result.</param>
    /// <returns>The typed unsigned batch commit result.</returns>
    internal static UnsignedScalar8Scalar8BatchCommitResult MapBatchCommit(Scalar8Scalar8BatchCommitResult result)
    {
        return new UnsignedScalar8Scalar8BatchCommitResult(
            result.AttemptedInsertCount,
            result.InsertedCount,
            result.AlreadyPresentCount,
            result.KeyConflictCount,
            result.InitialShelfRouteCreateCount,
            result.DeferredCommitRequests,
            result.Commit);
    }

    /// <summary>
    /// Maps an encoded batch abort result into the typed unsigned scalar batch abort result.<br/>
    /// </summary>
    /// <param name="result">The encoded core batch abort result.</param>
    /// <returns>The typed unsigned batch abort result.</returns>
    internal static UnsignedScalar8Scalar8BatchAbortResult MapBatchAbort(Scalar8Scalar8BatchAbortResult result)
    {
        return new UnsignedScalar8Scalar8BatchAbortResult(
            result.AttemptedInsertCount,
            result.DeferredCommitRequests);
    }
}
