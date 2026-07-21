namespace LibraDex;

/// <summary>
/// Controls whether the raw DataKernel records diagnostics for issued operations.<br/>
/// The storage layer keeps a cheap `Enabled` check so hot paths do not need to understand public diagnostics semantics.<br/>
/// </summary>
/// <param name="Level">The diagnostics level requested by the owning LibraDex surface.</param>
internal readonly record struct DataKernelTelemetryOptions(LibraDexDiagnosticsLevel Level)
{
    public bool Enabled => Level != LibraDexDiagnosticsLevel.Off;

    /// <summary>
    /// Gets telemetry options with counters disabled.<br/>
    /// </summary>
    public static DataKernelTelemetryOptions Disabled { get; } = new(LibraDexDiagnosticsLevel.Off);

    /// <summary>
    /// Gets telemetry options with counters enabled.<br/>
    /// </summary>
    public static DataKernelTelemetryOptions EnabledOptions { get; } = new(LibraDexDiagnosticsLevel.Counters);

    internal static DataKernelTelemetryOptions FromLevel(LibraDexDiagnosticsLevel level)
    {
        return new DataKernelTelemetryOptions(level);
    }
}
