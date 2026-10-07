using LibraDex.Layouts;

namespace LibraDex;

/// <summary>
/// Batches unsigned scalar `SS8-8` mutations by deferring durability publication until commit.<br/>
/// The typed batch keeps caller syntax in unsigned scalar values while delegating storage work to the encoded batch path.<br/>
/// </summary>
internal sealed class UnsignedScalar8Scalar8Batch : IDisposable
{
    private readonly Scalar8Scalar8Batch encodedBatch;

    internal UnsignedScalar8Scalar8Batch(Scalar8Scalar8Batch encodedBatch)
    {
        this.encodedBatch = encodedBatch;
    }

    /// <summary>
    /// Inserts one unsigned scalar key and unsigned scalar identity into the owning index without forcing a durable commit per item.<br/>
    /// Values are encoded with the unsigned scalar-8 codec before they enter the shared encoded mutation path.<br/>
    /// </summary>
    /// <param name="key">The unsigned scalar key to insert.</param>
    /// <param name="identity">The unsigned scalar identity value associated with the key.</param>
    /// <param name="allowDuplicateKeys">Whether multiple identities may share the same key.</param>
    /// <returns>The typed insert result plus any setup and insert commit telemetry.</returns>
    public UnsignedScalar8Scalar8InsertResult Insert(
        ulong key,
        ulong identity,
        bool allowDuplicateKeys = true)
    {
        Scalar8Scalar8EncodedInsertResult result = encodedBatch.InsertEncoded(
            Scalar8Scalar8Layout.EncodeUnsignedScalar8(key),
            Scalar8Scalar8Layout.EncodeUnsignedScalar8(identity),
            allowDuplicateKeys);
        return new UnsignedScalar8Scalar8InsertResult(
            UnsignedScalar8Scalar8ResultMapper.MapInsertOutcome(result.Outcome),
            result.CreatedInitialShelfRoute,
            result.RouteCreateCommit,
            result.InsertCommit);
    }

    /// <summary>
    /// Publishes all staged writes accumulated by the typed batch through one DataKernel commit boundary.<br/>
    /// Commit controls durability scope and reports folded write shape; it does not promise SQL-style all-or-nothing item semantics.<br/>
    /// </summary>
    /// <returns>The aggregate batch outcome and DataKernel commit telemetry.</returns>
    public UnsignedScalar8Scalar8BatchCommitResult Commit()
    {
        return UnsignedScalar8Scalar8ResultMapper.MapBatchCommit(encodedBatch.Commit());
    }

    /// <summary>
    /// Publishes all staged writes accumulated by the typed batch and closes this batch boundary.<br/>
    /// This is the preferred spelling for new code because it describes visibility and durability cadence without implying a database transaction commit.<br/>
    /// The current implementation delegates to <see cref="Commit"/> so compatibility behavior remains identical.<br/>
    /// </summary>
    /// <returns>The aggregate batch outcome and DataKernel publication telemetry.</returns>
    public UnsignedScalar8Scalar8BatchCommitResult Publish()
    {
        return Commit();
    }

    /// <summary>
    /// Aborts the typed batch by discarding staged writes that were not published.<br/>
    /// This is a revert-to-current-backing-state operation and is intentionally heavier than a normal successful commit path.<br/>
    /// </summary>
    /// <returns>The aggregate abort result.</returns>
    public UnsignedScalar8Scalar8BatchAbortResult Abort()
    {
        return UnsignedScalar8Scalar8ResultMapper.MapBatchAbort(encodedBatch.Abort());
    }

    /// <summary>
    /// Aborts uncommitted staged writes when the batch is disposed without explicit commit or abort.<br/>
    /// This makes `using` scopes safe for early exits while keeping `Commit` as the only durability publication call.<br/>
    /// </summary>
    public void Dispose()
    {
        encodedBatch.Dispose();
    }
}
