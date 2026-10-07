namespace LibraDex;

/// <summary>
/// Identifies the policy path used when allocating a router page.<br/>
/// This is internal storage-policy telemetry, not a public API contract.<br/>
/// </summary>
internal enum RouterArenaAllocationKind
{
    /// <summary>
    /// The router was allocated from the preferred local arena.<br/>
    /// </summary>
    LocalArena = 0,

    /// <summary>
    /// The router was allocated from another eligible arena in the same session/index scope.<br/>
    /// </summary>
    IndexArena = 1,

    /// <summary>
    /// No eligible arena had free space, so the router was appended normally.<br/>
    /// </summary>
    Append = 2
}

/// <summary>
/// Captures the result of a router allocation that may use a transformed-shelf router micro-arena.<br/>
/// The arena base is zero when the allocation appended instead of using an arena.<br/>
/// </summary>
/// <param name="Router">The created router snapshot.</param>
/// <param name="Kind">The allocation policy path used.</param>
/// <param name="ArenaBaseOffset">The arena base offset used, or zero when appended.</param>
/// <param name="ArenaPageIndex">The arena page index used, or zero when appended.</param>
/// <param name="Commit">The commit telemetry for the allocation.</param>
internal readonly record struct RouterArenaAllocationResult(
    RouterSnapshot Router,
    RouterArenaAllocationKind Kind,
    long ArenaBaseOffset,
    ushort ArenaPageIndex,
    DataKernelCommitTelemetry Commit);
