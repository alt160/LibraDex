using System.Diagnostics;

namespace LibraDex;

/// <summary>
/// Selects optional information captured for bounded query telemetry.<br/>
/// Execution timing and completion state are inexpensive but still opt-in; thread allocation measurement adds a CLR counter read around LibraDex-owned execution steps.<br/>
/// </summary>
[Flags]
public enum QueryDiagnosticFields
{
    /// <summary>
    /// Captures execution route, elapsed time, returned rows, and completion state.<br/>
    /// </summary>
    Execution = 1,

    /// <summary>
    /// Captures bytes allocated on threads while LibraDex is actively executing the query.<br/>
    /// Iterator and reader measurement excludes caller work performed between reads.<br/>
    /// </summary>
    Allocations = 2
}

/// <summary>
/// Describes how a measured or historically recorded query ended.<br/>
/// </summary>
public enum LibraDexQueryCompletion
{
    /// <summary>
    /// The eager operation returned or the stream reached its natural end.<br/>
    /// </summary>
    Completed = 0,

    /// <summary>
    /// A reader or iterator was disposed before reaching its natural end.<br/>
    /// </summary>
    StoppedEarly = 1,

    /// <summary>
    /// Query execution threw before completion.<br/>
    /// </summary>
    Failed = 2
}

/// <summary>
/// Represents one immutable entry in the catalog's bounded query history.<br/>
/// The record retains only passive shape text and counters; it never retains a condition, operand collection, deferred selector, lambda, reader, or result collection.<br/>
/// </summary>
/// <param name="Sequence">The catalog-local monotonically increasing query sequence.<br/></param>
/// <param name="StartedUtc">The UTC time at which LibraDex began the measured execution.<br/></param>
/// <param name="Group">The identity group that executed the condition.<br/></param>
/// <param name="ConditionShape">A value-free structural condition and result-shape description.<br/></param>
/// <param name="RequiresScan">Whether pre-execution explanation identified at least one scan-backed leaf.<br/></param>
/// <param name="RowsReturned">The number of results consumed or materialized before completion.<br/></param>
/// <param name="ElapsedTicks">Elapsed wall-clock ticks spent inside the measured LibraDex execution boundary.<br/></param>
/// <param name="ThreadAllocatedBytes">Bytes allocated on executing threads while LibraDex was active; zero when allocation capture was disabled.<br/></param>
/// <param name="Completion">How execution ended.<br/></param>
public readonly record struct LibraDexQueryTelemetryEntry(
    long Sequence,
    DateTimeOffset StartedUtc,
    string Group,
    string ConditionShape,
    bool RequiresScan,
    long RowsReturned,
    long ElapsedTicks,
    long ThreadAllocatedBytes,
    LibraDexQueryCompletion Completion);

/// <summary>
/// Provides catalog-level diagnostic entry points.<br/>
/// The facade owns bounded recent-query telemetry; condition explanation and explicit measurement remain on the identity-group producer that resolves the condition's indexes.<br/>
/// </summary>
public sealed class CatalogDiagnostics
{
    internal CatalogDiagnostics(Catalog catalog)
    {
        Catalog = catalog;
        Queries = new CatalogQueryTelemetry();
    }

    /// <summary>
    /// Gets the catalog that owns this diagnostic surface.<br/>
    /// </summary>
    public Catalog Catalog { get; }

    /// <summary>
    /// Gets bounded opt-in query history controls and snapshots.<br/>
    /// </summary>
    public CatalogQueryTelemetry Queries { get; }
}

/// <summary>
/// Stores a bounded in-memory history of query executions for one open catalog.<br/>
/// Disabled telemetry performs no condition-shape formatting and owns no history buffer; enabling allocates one fixed entry array whose oldest cells are overwritten at capacity.<br/>
/// </summary>
public sealed class CatalogQueryTelemetry
{
    private readonly object sync = new();
    private LibraDexQueryTelemetryEntry[] entries = Array.Empty<LibraDexQueryTelemetryEntry>();
    private int next;
    private int count;
    private long sequence;
    private QueryDiagnosticFields fields;

    /// <summary>
    /// Gets whether bounded query history is currently enabled.<br/>
    /// </summary>
    public bool IsEnabled
    {
        get
        {
            lock (sync)
                return entries.Length != 0;
        }
    }

    /// <summary>
    /// Gets the configured maximum number of retained query records.<br/>
    /// </summary>
    public int Capacity
    {
        get
        {
            lock (sync)
                return entries.Length;
        }
    }

    /// <summary>
    /// Gets the diagnostic fields captured for new records.<br/>
    /// </summary>
    public QueryDiagnosticFields Fields
    {
        get
        {
            lock (sync)
                return fields;
        }
    }

    /// <summary>
    /// Gets a disconnected oldest-to-newest snapshot of retained executions.<br/>
    /// Reading this property allocates one result array; ordinary query publication writes structs into the fixed ring without allocating history nodes.<br/>
    /// </summary>
    public IReadOnlyList<LibraDexQueryTelemetryEntry> Recent
    {
        get
        {
            lock (sync)
            {
                if (count == 0)
                    return Array.Empty<LibraDexQueryTelemetryEntry>();

                LibraDexQueryTelemetryEntry[] snapshot = new LibraDexQueryTelemetryEntry[count];
                int start = count == entries.Length ? next : 0;
                for (int i = 0; i < count; i++)
                    snapshot[i] = entries[(start + i) % entries.Length];
                return snapshot;
            }
        }
    }

    /// <summary>
    /// Enables bounded in-memory query history.<br/>
    /// Re-enabling replaces prior history with a new fixed-capacity buffer so retention and capture policy change at one explicit boundary.<br/>
    /// </summary>
    /// <param name="capacity">The positive maximum number of recent execution records retained.<br/></param>
    /// <param name="fields">The information captured for each execution; at least one field must be selected.<br/></param>
    public void Enable(
        int capacity = 256,
        QueryDiagnosticFields fields = QueryDiagnosticFields.Execution)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Query telemetry capacity must be positive.");
        QueryDiagnosticFields supported = QueryDiagnosticFields.Execution | QueryDiagnosticFields.Allocations;
        if (fields == 0 || (fields & ~supported) != 0)
            throw new ArgumentOutOfRangeException(nameof(fields), fields, "Select Execution, Allocations, or both.");

        lock (sync)
        {
            entries = new LibraDexQueryTelemetryEntry[capacity];
            next = 0;
            count = 0;
            this.fields = fields;
        }
    }

    /// <summary>
    /// Disables query history and releases the retained ring buffer and its passive string references.<br/>
    /// </summary>
    public void Disable()
    {
        lock (sync)
        {
            entries = Array.Empty<LibraDexQueryTelemetryEntry>();
            next = 0;
            count = 0;
            fields = 0;
        }
    }

    /// <summary>
    /// Clears retained records without changing capacity or capture fields.<br/>
    /// </summary>
    public void Clear()
    {
        lock (sync)
        {
            if (entries.Length != 0)
                Array.Clear(entries);
            next = 0;
            count = 0;
        }
    }

    internal bool TryGetFields(out QueryDiagnosticFields capturedFields)
    {
        lock (sync)
        {
            capturedFields = fields;
            return entries.Length != 0;
        }
    }

    internal void Publish(
        DateTimeOffset startedUtc,
        string group,
        string conditionShape,
        bool requiresScan,
        long rowsReturned,
        long elapsedTicks,
        long threadAllocatedBytes,
        LibraDexQueryCompletion completion)
    {
        lock (sync)
        {
            if (entries.Length == 0)
                return;

            entries[next] = new LibraDexQueryTelemetryEntry(
                ++sequence,
                startedUtc,
                group,
                conditionShape,
                requiresScan,
                rowsReturned,
                elapsedTicks,
                (fields & QueryDiagnosticFields.Allocations) != 0 ? threadAllocatedBytes : 0,
                completion);
            next = (next + 1) % entries.Length;
            if (count < entries.Length)
                count++;
        }
    }

    internal static long GetElapsedTicks(long startedTimestamp)
    {
        long elapsedTimestamp = Stopwatch.GetTimestamp() - startedTimestamp;
        return (long)(elapsedTimestamp * ((double)TimeSpan.TicksPerSecond / Stopwatch.Frequency));
    }
}

/// <summary>
/// Describes a condition before execution using developer-facing route concepts.<br/>
/// The explanation is value-free and does not enumerate query results; scan reasons identify the affected indexes without requiring callers to interpret bridge enums.<br/>
/// </summary>
public sealed class LibraDexQueryExplanation
{
    internal LibraDexQueryExplanation(
        string group,
        string conditionShape,
        bool requiresScan,
        IReadOnlyList<string> indexesUsed,
        IReadOnlyList<string> projectionsUsed,
        IReadOnlyList<string> scanReasons,
        LibraDexConditionResultPlan? resultPlan)
    {
        Group = group;
        ConditionShape = conditionShape;
        RequiresScan = requiresScan;
        IndexesUsed = indexesUsed;
        ProjectionsUsed = projectionsUsed;
        ScanReasons = scanReasons;
        ResultPlan = resultPlan;
    }

    /// <summary>Gets the condition's identity group.<br/></summary>
    public string Group { get; }
    /// <summary>Gets the value-free condition and result shape.<br/></summary>
    public string ConditionShape { get; }
    /// <summary>Gets whether at least one condition leaf requires scan-backed execution.<br/></summary>
    public bool RequiresScan { get; }
    /// <summary>Gets distinct logical index names referenced by the condition.<br/></summary>
    public IReadOnlyList<string> IndexesUsed { get; }
    /// <summary>Gets distinct maintained projection kinds selected by the condition planner.<br/></summary>
    public IReadOnlyList<string> ProjectionsUsed { get; }
    /// <summary>Gets developer-facing reasons for scan-backed leaves.<br/></summary>
    public IReadOnlyList<string> ScanReasons { get; }
    /// <summary>Gets the typed result-shape plan, or null for an identity-only completed condition.<br/></summary>
    public LibraDexConditionResultPlan? ResultPlan { get; }
}

/// <summary>
/// Returns one eagerly materialized result collection together with diagnostics measured for that exact execution.<br/>
/// </summary>
/// <typeparam name="TResult">The condition's logical result type.<br/></typeparam>
public sealed class LibraDexQueryMeasurement<TResult>
{
    internal LibraDexQueryMeasurement(
        IReadOnlyList<TResult> results,
        LibraDexQueryDiagnostics diagnostics)
    {
        Results = results;
        Diagnostics = diagnostics;
    }

    /// <summary>Gets the caller-owned results from the measured execution.<br/></summary>
    public IReadOnlyList<TResult> Results { get; }
    /// <summary>Gets diagnostics measured for the same execution.<br/></summary>
    public LibraDexQueryDiagnostics Diagnostics { get; }
}

internal sealed class LibraDexQueryTelemetrySession
{
    private readonly CatalogQueryTelemetry telemetry;
    private readonly bool measureAllocations;
    private readonly DateTimeOffset startedUtc;
    private readonly string group;
    private readonly string conditionShape;
    private readonly bool requiresScan;
    private long elapsedTicks;
    private long threadAllocatedBytes;
    private long rowsReturned;
    private bool published;

    internal LibraDexQueryTelemetrySession(
        CatalogQueryTelemetry telemetry,
        DateTimeOffset startedUtc,
        string group,
        string conditionShape,
        bool requiresScan,
        long initialElapsedTicks,
        long initialThreadAllocatedBytes,
        bool measureAllocations)
    {
        this.telemetry = telemetry;
        this.group = group;
        this.conditionShape = conditionShape;
        this.requiresScan = requiresScan;
        this.measureAllocations = measureAllocations;
        elapsedTicks = initialElapsedTicks;
        threadAllocatedBytes = initialThreadAllocatedBytes;
        this.startedUtc = startedUtc;
    }

    internal void StartStep(out long startedTimestamp, out long startedAllocated)
    {
        startedTimestamp = Stopwatch.GetTimestamp();
        startedAllocated = measureAllocations
            ? GC.GetAllocatedBytesForCurrentThread()
            : 0;
    }

    internal void EndStep(long startedTimestamp, long startedAllocated)
    {
        elapsedTicks += CatalogQueryTelemetry.GetElapsedTicks(startedTimestamp);
        if (measureAllocations)
            threadAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - startedAllocated;
    }

    internal void Returned() => rowsReturned++;

    internal void Complete(LibraDexQueryCompletion completion)
    {
        if (published)
            return;

        published = true;
        telemetry.Publish(
            startedUtc,
            group,
            conditionShape,
            requiresScan,
            rowsReturned,
            elapsedTicks,
            threadAllocatedBytes,
            completion);
    }
}
