namespace LibraDex.FileSearch;

internal static class FileSearchLog
{
    private static readonly object Gate = new();

    public static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LibraDex",
        "FileSearch",
        "filesearch.log");

    public static event Action<string>? EntryWritten;

    public static void Info(string message)
    {
        Write("INFO", message, null);
    }

    public static void Error(string message, Exception ex)
    {
        Write("ERROR", message, ex);
    }

    public static void Clear()
    {
        try
        {
            string? directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            lock (Gate)
            {
                File.WriteAllText(Path, string.Empty);
                EntryWritten?.Invoke($"{DateTimeOffset.Now:O} [INFO] Log cleared.");
            }
        }
        catch
        {
            // Logging must never make the dogfood UI less usable.
        }
    }

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            string? directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            lock (Gate)
            {
                using StreamWriter writer = File.AppendText(Path);
                string entry = $"{DateTimeOffset.Now:O} [{level}] {message}";
                writer.WriteLine(entry);
                if (ex is not null)
                {
                    writer.WriteLine(ex);
                }

                EntryWritten?.Invoke(entry);
            }
        }
        catch
        {
            // Logging must never make the dogfood UI less usable.
        }
    }
}
