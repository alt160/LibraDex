namespace LibraDex;

/// <summary>
/// Identifies the raw backing store used by a <see cref="DataKernel"/> instance.<br/>
/// The backing choice must not leak into shelf, router, or index byte formats.<br/>
/// </summary>
internal enum DataKernelBackingKind
{
    /// <summary>
    /// Uses a file handle and positional `RandomAccess` calls for durable-capable storage.<br/>
    /// </summary>
    File = 0,

    /// <summary>
    /// Uses process memory only and does not provide durable reopen behavior.<br/>
    /// </summary>
    Memory = 1
}
