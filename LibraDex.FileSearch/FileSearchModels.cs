using System.Text.Json.Serialization;

namespace LibraDex.FileSearch;

internal sealed class FileSearchState
{
    public List<string> Roots { get; set; } = new();

    public List<FileRecord> Files { get; set; } = new();

    public ulong NextId { get; set; } = 1;

    public FileSearchIdentityMode IdentityMode { get; set; } = FileSearchIdentityMode.SyntheticUInt64;

    public Dictionary<string, string> DisabledSecondaryFields { get; set; } = new(StringComparer.Ordinal);

    public bool IndexStringFolded { get; set; }

    public bool IndexStringSortKey { get; set; }

    public bool IndexStringReversed { get; set; }
}

internal sealed class FileRecord
{
    public ulong Id { get; set; }

    public string Path { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public string Extension { get; set; } = string.Empty;

    public ulong Size { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime ModifiedUtc { get; set; }

    public DateTime AccessedUtc { get; set; }

    public uint Attributes { get; set; }

    public ulong PathHashHigh { get; set; }

    public ulong PathHashLow { get; set; }

    [JsonIgnore]
    public byte[]? PathIdentityBytes { get; set; }

    [JsonIgnore]
    public byte[]? FileNameKeyBytes { get; set; }

    [JsonIgnore]
    public byte[]? ExtensionKeyBytes { get; set; }

    [JsonIgnore]
    public byte[]? FileNameFoldedKeyBytes { get; set; }

    [JsonIgnore]
    public byte[]? ExtensionFoldedKeyBytes { get; set; }

    [JsonIgnore]
    public byte[]? FileNameSortKeyBytes { get; set; }

    [JsonIgnore]
    public byte[]? ExtensionSortKeyBytes { get; set; }

    [JsonIgnore]
    public byte[]? FileNameReversedKeyBytes { get; set; }

    [JsonIgnore]
    public byte[]? ExtensionReversedKeyBytes { get; set; }

    [JsonIgnore]
    public byte[]? FileNameFoldedReversedKeyBytes { get; set; }

    [JsonIgnore]
    public byte[]? ExtensionFoldedReversedKeyBytes { get; set; }

    [JsonIgnore]
    public LibraDexStringScalar8Index.LibraDexStringScalar8PreparedKey? ExtensionPreparedKey { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum FileSearchIdentityMode
{
    SyntheticUInt64,
    PathString
}

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum FileSearchRowHydrationMode
{
    SidecarCache,
    FileSystem,
    LibraDexInverseIndexes
}

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum FileSearchDisplayFieldMode
{
    IdentityOnly,
    SelectedFields,
    AllKnownFields
}

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum FileSearchValueDisplayMode
{
    Value,
    Hex,
    Text,
    Bytes,
    RawBytesText,
    Both,
    UtcDateTime
}

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum FileSearchInsertLayout
{
    RecordMajor,
    IndexMajor
}

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum SearchField
{
    FileName,
    Extension,
    Size,
    CreatedUtc,
    ModifiedUtc,
    AccessedUtc,
    Attributes
}

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum SearchOperator
{
    EqualTo,
    StartsWith,
    Contains,
    GreaterOrEqual,
    LessOrEqual,
    Between
}

internal sealed class QueryRow
{
    public SearchField Field { get; set; }

    public SearchOperator Operator { get; set; }

    public string Value1 { get; set; } = string.Empty;

    public string Value2 { get; set; } = string.Empty;

    public bool IgnoreCase { get; set; } = true;
}

internal sealed class FileSearchReindexOptions
{
    public FileSearchIdentityMode IdentityMode { get; set; }

    public int MaxFiles { get; set; }

    public bool UseGroupBatching { get; set; }

    public int BatchCommitFileCount { get; set; }

    public bool IndexExtension { get; set; }

    public bool IndexStringFolded { get; set; }

    public bool IndexStringSortKey { get; set; }

    public bool IndexStringReversed { get; set; }

    public FileSearchInsertLayout InsertLayout { get; set; }

    public LibraDexWriteOrder WriteOrder { get; set; }

    public LibraDexWriteVolume WriteVolume { get; set; }

    public LibraDexWriteLocality WriteLocality { get; set; }

    public LibraDexWritePriority WritePriority { get; set; }

    public LibraDexWriteIntent ToWriteIntent()
        => new(WriteOrder, WriteVolume, WriteLocality, WritePriority);
}

internal readonly record struct ReindexPhaseDurations(TimeSpan Collect, TimeSpan Sort, TimeSpan Insert);

internal sealed class FileSearchReadOptions
{
    public FileSearchIdentityMode IdentityMode { get; set; }

    public FileSearchRowHydrationMode RowHydrationMode { get; set; }

    public FileSearchDisplayFieldMode DisplayFieldMode { get; set; }

    public FileSearchValueDisplayMode IdentityDisplayMode { get; set; }

    public FileSearchValueDisplayMode KeyDisplayMode { get; set; }
}

internal sealed class FileSearchTupleRow
{
    public string Key { get; set; } = string.Empty;

    public string Identity { get; set; } = string.Empty;
}

internal sealed class FileSearchIdentityRow
{
    public string Identity { get; set; } = string.Empty;
}

internal sealed class FileSearchStressResult
{
    public int EnumerationChecks { get; set; }

    public int QueryChecks { get; set; }

    public int StrategyChecks { get; set; }

    public int Warnings { get; set; }

    public int Failures { get; set; }

    public TimeSpan Elapsed { get; set; }

    public string Summary { get; set; } = string.Empty;

    public FileSearchStressPhaseStats PhaseStats { get; } = new();
}

internal readonly record struct FileSearchStressEnumerationCase(SearchField Field, int Skip, int Length, string Label);

internal readonly record struct FileSearchStressQueryCase(string Label, QueryRow[] Rows);

internal sealed class FileSearchStressPhaseStats
{
    public int RawDiscoveryCount { get; private set; }

    public TimeSpan RawDiscoveryElapsed { get; private set; }

    public int IndexTupleReadCount { get; private set; }

    public TimeSpan IndexTupleReadElapsed { get; private set; }

    public int IdentityMaterializationCount { get; private set; }

    public TimeSpan IdentityMaterializationElapsed { get; private set; }

    public int KeyIdentityMaterializationCount { get; private set; }

    public TimeSpan KeyIdentityMaterializationElapsed { get; private set; }

    public int RecordMaterializationCount { get; private set; }

    public TimeSpan RecordMaterializationElapsed { get; private set; }

    public int GridSimulationCount { get; private set; }

    public TimeSpan GridSimulationElapsed { get; private set; }

    public void AddRawDiscovery(TimeSpan elapsed)
    {
        RawDiscoveryCount++;
        RawDiscoveryElapsed += elapsed;
    }

    public void AddIndexTupleRead(TimeSpan elapsed)
    {
        IndexTupleReadCount++;
        IndexTupleReadElapsed += elapsed;
    }

    public void AddIdentityMaterialization(TimeSpan elapsed)
    {
        IdentityMaterializationCount++;
        IdentityMaterializationElapsed += elapsed;
    }

    public void AddKeyIdentityMaterialization(TimeSpan elapsed)
    {
        KeyIdentityMaterializationCount++;
        KeyIdentityMaterializationElapsed += elapsed;
    }

    public void AddRecordMaterialization(TimeSpan elapsed)
    {
        RecordMaterializationCount++;
        RecordMaterializationElapsed += elapsed;
    }

    public void AddGridSimulation(TimeSpan elapsed)
    {
        GridSimulationCount++;
        GridSimulationElapsed += elapsed;
    }
}
