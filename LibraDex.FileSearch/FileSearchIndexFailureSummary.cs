namespace LibraDex.FileSearch;

internal sealed class FileSearchIndexFailureSummary
{
    private const int DetailLimitPerFailureShape = 3;
    private readonly Dictionary<string, FileSearchIndexFailureBucket> buckets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> disabledFields = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> skippedFields = new(StringComparer.Ordinal);

    public FileSearchIndexFailureSummary()
    {
    }

    public FileSearchIndexFailureSummary(IReadOnlyDictionary<string, string> initialDisabledFields)
    {
        foreach (KeyValuePair<string, string> pair in initialDisabledFields)
        {
            disabledFields[pair.Key] = pair.Value;
        }
    }

    public bool IsDisabled(string fieldName)
    {
        return disabledFields.ContainsKey(fieldName);
    }

    public void RecordSkipped(string fieldName)
    {
        skippedFields.TryGetValue(fieldName, out long count);
        skippedFields[fieldName] = count + 1;
    }

    public long SkippedFieldCount
    {
        get
        {
            long total = 0;
            foreach (long count in skippedFields.Values)
            {
                total += count;
            }

            return total;
        }
    }

    public string DisabledFieldNames
    {
        get
        {
            return disabledFields.Count == 0
                ? "none"
                : string.Join(", ", disabledFields.Keys.Order(StringComparer.Ordinal));
        }
    }

    public IReadOnlyDictionary<string, string> DisabledFields => disabledFields;

    public bool Record(string fieldName, string path, Exception ex, bool allowDisable)
    {
        string message = ex.Message;
        string key = $"{fieldName}|{ex.GetType().FullName}|{message}";
        if (!buckets.TryGetValue(key, out FileSearchIndexFailureBucket? bucket))
        {
            bucket = new FileSearchIndexFailureBucket(fieldName, ex.GetType().Name, message);
            buckets.Add(key, bucket);
        }

        bucket.Count++;
        if (bucket.Count <= DetailLimitPerFailureShape)
        {
            FileSearchLog.Error($"Failed to index field '{fieldName}' for '{path}'.", ex);
        }

        if (allowDisable &&
            bucket.Count == DetailLimitPerFailureShape &&
            !disabledFields.ContainsKey(fieldName))
        {
            disabledFields.Add(fieldName, $"{bucket.ExceptionName}: {bucket.Message}");
            FileSearchLog.Info($"Disabled field '{fieldName}' for future reindex passes after {DetailLimitPerFailureShape:n0} matching index failures.");
            return true;
        }

        return false;
    }

    public void FlushSummary()
    {
        foreach (FileSearchIndexFailureBucket bucket in buckets.Values.OrderBy(static bucket => bucket.FieldName, StringComparer.Ordinal))
        {
            if (bucket.Count == 0)
            {
                continue;
            }

            string elided = bucket.Count > DetailLimitPerFailureShape
                ? $" Detail logging capped at {DetailLimitPerFailureShape:n0} examples."
                : string.Empty;
            FileSearchLog.Info($"Index failure summary: Field={bucket.FieldName}; Exception={bucket.ExceptionName}; Count={bucket.Count:n0}; Message={bucket.Message}.{elided}");
        }

        foreach (KeyValuePair<string, long> skipped in skippedFields.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            string reason = disabledFields.TryGetValue(skipped.Key, out string? value)
                ? value
                : "disabled";
            FileSearchLog.Info($"Index skip summary: Field={skipped.Key}; Skipped={skipped.Value:n0}; Reason={reason}.");
        }
    }

    private sealed class FileSearchIndexFailureBucket
    {
        public FileSearchIndexFailureBucket(string fieldName, string exceptionName, string message)
        {
            FieldName = fieldName;
            ExceptionName = exceptionName;
            Message = message;
        }

        public string FieldName { get; }

        public string ExceptionName { get; }

        public string Message { get; }

        public long Count { get; set; }
    }
}
