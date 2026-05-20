namespace LibraDex;

/// <summary>
/// Identifies one encoded `SS8-8` index instance for runtime operations.<br/>
/// The handle intentionally carries only the root router offset and shelf profile for this slice; catalog metadata, codecs, uniqueness policy, and public naming remain separate future layers.<br/>
/// </summary>
/// <param name="RootRouterOffset">The file offset where this index's root router starts.</param>
/// <param name="Profile">The `SS8-8` shelf profile used by shelves in this index.</param>
internal readonly record struct Scalar8Scalar8IndexHandle(
    long RootRouterOffset,
    Scalar8Scalar8Profile Profile)
{
    /// <summary>
    /// Validates the runtime handle before it is used by an encoded index operation.<br/>
    /// Offset zero is reserved for the superblock, so a valid root router offset must be positive.<br/>
    /// </summary>
    /// <exception cref="InvalidDataException">Thrown when the handle cannot identify a valid runtime index root.</exception>
    public void Validate()
    {
        if (RootRouterOffset <= 0)
        {
            throw new InvalidDataException("An SS8-8 index handle must reference a positive root router offset.");
        }
    }
}
