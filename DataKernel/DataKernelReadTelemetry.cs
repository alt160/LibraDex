namespace LibraDex;

/// <summary>
/// Reports the raw read shape of DataKernel reads.<br/>
/// The counters describe the positional read calls LibraDex issued.<br/>
/// </summary>
/// <param name="TelemetryEnabled">Whether telemetry was enabled for reads.</param>
/// <param name="ReadCallCount">The number of positional read calls issued by the DataKernel.</param>
/// <param name="BackingReadCallCount">The number of logical backing-store read calls issued by the DataKernel.</param>
/// <param name="BytesRead">The number of bytes returned to callers.</param>
internal readonly record struct DataKernelReadTelemetry(
    bool TelemetryEnabled,
    long ReadCallCount,
    long BackingReadCallCount,
    long BytesRead);
