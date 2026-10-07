using System.Security.Cryptography;

namespace LibraDex;

/// <summary>
/// Controls creation of a durable backup from an open file-backed catalog.<br/>
/// The default refuses to replace an existing destination so a mistyped path cannot silently destroy an earlier backup.<br/>
/// </summary>
public sealed class LibraDexBackupOptions
{
    /// <summary>
    /// Gets or initializes whether a validated backup may replace an existing destination file.<br/>
    /// Replacement occurs only after the new sibling staging image has been copied, hashed, reopened, and validated.<br/>
    /// </summary>
    public bool Overwrite { get; init; }
}

/// <summary>
/// Reports one durable backup captured from an open file-backed catalog.<br/>
/// The content hash describes the exact committed source bytes copied while storage publication was blocked.<br/>
/// </summary>
/// <param name="Path">The full installed backup path.<br/></param>
/// <param name="Bytes">The exact number of bytes copied.<br/></param>
/// <param name="ContentHash">The uppercase SHA-256 hash of the copied catalog image.<br/></param>
/// <param name="IndexCount">The number of active index definitions observed when the staged image was reopened, including maintained physical projections.<br/></param>
public readonly record struct LibraDexBackupResult(
    string Path,
    long Bytes,
    string ContentHash,
    int IndexCount);

internal static class LibraDexCatalogBackup
{
    /// <summary>
    /// Captures, validates, and installs one live-catalog backup through a destination-local staging file.<br/>
    /// Source publication and reads are blocked only for the physical copy and durable flush; hashing and catalog reopen validation occur after the live storage gate is released.<br/>
    /// Cancellation is honored only before installation so a returned result always names a complete installed backup.<br/>
    /// </summary>
    /// <param name="catalog">The open file-backed source catalog.<br/></param>
    /// <param name="path">The destination backup path.<br/></param>
    /// <param name="options">Optional overwrite policy.<br/></param>
    /// <param name="cancellationToken">A token observed before installation and between physical copy blocks.<br/></param>
    /// <returns>The installed backup path, exact byte count, hash, and validated index-definition count.<br/></returns>
    internal static LibraDexBackupResult Create(
        Catalog catalog,
        string path,
        LibraDexBackupOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string sourcePath = System.IO.Path.GetFullPath(catalog.Path
            ?? throw new InvalidOperationException("A live backup requires a file-backed LibraDex catalog."));
        string resolvedSourcePath = ResolvePathAliases(sourcePath);
        string destinationPath = System.IO.Path.GetFullPath(path);
        string resolvedDestinationPath = ResolvePathAliases(destinationPath);
        StringComparison pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(resolvedSourcePath, resolvedDestinationPath, pathComparison))
            throw new ArgumentException("The LibraDex backup path must differ from the live catalog path.", nameof(path));

        LibraDexBackupOptions effective = options ?? new LibraDexBackupOptions();
        DataKernel.RejectPublicationSidecarCollision(destinationPath);
        if (!effective.Overwrite && File.Exists(destinationPath))
            throw new IOException($"The LibraDex backup file already exists: {destinationPath}");

        string? directory = System.IO.Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        string fileName = System.IO.Path.GetFileName(destinationPath);
        string stagingPath = System.IO.Path.Combine(
            directory ?? Environment.CurrentDirectory,
            $".{fileName}.backup-{Guid.NewGuid():N}.tmp");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            (long bytes, string sourceHash) = catalog.Session.CreateLiveBackupImage(
                stagingPath,
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            string stagedHash;
            using (FileStream stream = new(
                stagingPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.SequentialScan))
            {
                stagedHash = Convert.ToHexString(SHA256.HashData(stream));
            }

            if (!string.Equals(sourceHash, stagedHash, StringComparison.Ordinal))
                throw new InvalidDataException("The staged LibraDex backup hash does not match the committed source image.");
            if (new FileInfo(stagingPath).Length != bytes)
                throw new InvalidDataException("The staged LibraDex backup length does not match the committed source image.");

            int indexCount;
            using (Catalog validation = Catalog.Open(stagingPath, catalog.Options))
                indexCount = validation.Indexes.List().Length;

            cancellationToken.ThrowIfCancellationRequested();
            DataKernel.RejectPublicationSidecarCollision(destinationPath);
            File.Move(stagingPath, destinationPath, effective.Overwrite);
            return new LibraDexBackupResult(destinationPath, bytes, sourceHash, indexCount);
        }
        finally
        {
            if (File.Exists(stagingPath))
                File.Delete(stagingPath);
        }
    }

    /// <summary>
    /// Resolves existing symbolic-link and junction components while preserving any not-yet-created destination suffix.<br/>
    /// Walking each component detects aliases introduced by an ancestor junction, including alternate workspace drive paths that ordinary full-path normalization cannot identify.<br/>
    /// </summary>
    /// <param name="path">The normalized source or destination path to resolve.<br/></param>
    /// <returns>A full path with every resolvable existing link component replaced by its final target.<br/></returns>
    private static string ResolvePathAliases(string path)
    {
        string fullPath = System.IO.Path.GetFullPath(path);
        string root = System.IO.Path.GetPathRoot(fullPath)
            ?? throw new ArgumentException("The LibraDex backup path must have a filesystem root.", nameof(path));
        string current = root;
        string relative = fullPath[root.Length..];
        string[] parts = relative.Split(
            new[] { System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        foreach (string part in parts)
        {
            string candidate = System.IO.Path.Combine(current, part);
            FileSystemInfo? entry = Directory.Exists(candidate)
                ? new DirectoryInfo(candidate)
                : File.Exists(candidate)
                    ? new FileInfo(candidate)
                    : null;
            current = entry?.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? candidate;
        }

        return System.IO.Path.GetFullPath(current);
    }
}
