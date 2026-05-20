namespace LibraDex;

/// <summary>
/// Controls whether the raw DataKernel records telemetry for issued operations.<br/>
/// Telemetry is toggleable so baseline runs can compare enabled and disabled overhead.<br/>
/// </summary>
/// <param name="Enabled">Whether telemetry counters should be collected.</param>
public readonly record struct DataKernelTelemetryOptions(bool Enabled)
{
    /// <summary>
    /// Gets telemetry options with counters disabled.<br/>
    /// </summary>
    public static DataKernelTelemetryOptions Disabled { get; } = new(false);

    /// <summary>
    /// Gets telemetry options with counters enabled.<br/>
    /// </summary>
    public static DataKernelTelemetryOptions EnabledOptions { get; } = new(true);
}
