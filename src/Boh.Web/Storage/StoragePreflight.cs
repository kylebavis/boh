namespace Boh.Web.Storage;

/// <summary>
/// Verifies every configured storage location exists and is writable before the
/// application starts.
/// </summary>
/// <remarks>
/// Splitting storage across mounts makes permissions the most likely thing to go wrong:
/// the container runs as a non-root user, and Docker creates a fresh named volume — or a
/// bind mount pointing at a new host directory — owned by root. Without this the first
/// symptom is an unhandled exception with no indication of which path or which uid, so
/// each problem is reported with the command that fixes it.
/// </remarks>
public static class StoragePreflight
{
    public sealed record Problem(string Path, string Purpose, string Reason);

    public static IReadOnlyList<Problem> Check(BohOptions options)
    {
        var targets = new (string Path, string Purpose)[]
        {
            (DirectoryOf(options.DatabasePath), "database (BOH_DB_PATH)"),
            (options.KeysDir, "encryption keys (BOH_KEYS_PATH)"),
            (options.OriginalsDir, "originals (BOH_ORIGINALS_PATH)"),
            (options.ThumbsDir, "thumbnails (BOH_THUMBS_PATH)"),
            (options.ImportTempDir, "import scratch (BOH_TEMP_PATH)"),
        };

        var problems = new List<Problem>();

        foreach (var (path, purpose) in targets.DistinctBy(t => t.Path))
        {
            var reason = Probe(path);
            if (reason is not null) problems.Add(new Problem(path, purpose, reason));
        }

        return problems;
    }

    /// <summary>
    /// Returns null when the directory is usable, otherwise why it is not. Existence alone
    /// is not enough — a share can be mounted read-only, which only shows up on write.
    /// </summary>
    private static string? Probe(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return $"cannot create the directory ({ex.GetType().Name}: {ex.Message})";
        }

        var probeFile = Path.Combine(path, $".boh-write-test-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(probeFile, []);
            File.Delete(probeFile);
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            TryDelete(probeFile);
            return $"directory exists but is not writable ({ex.GetType().Name})";
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Nothing useful to do; the caller is already reporting a failure.
        }
    }

    private static string DirectoryOf(string filePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        return string.IsNullOrEmpty(directory) ? "/" : directory;
    }

    /// <summary>
    /// The uid the process is running as, for the chown hint. Read from /proc because .NET
    /// exposes no portable way to ask.
    /// </summary>
    public static string CurrentUserId()
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/self/status"))
            {
                if (!line.StartsWith("Uid:", StringComparison.Ordinal)) continue;

                var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1) return parts[1].Trim();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Fall through to the generic answer below.
        }

        return "the container user";
    }
}
