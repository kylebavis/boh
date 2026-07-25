namespace Boh.Web.Storage;

/// <summary>
/// Identifies the filesystem backing a path on Linux by consulting
/// <c>/proc/self/mountinfo</c>.
/// </summary>
/// <remarks>
/// Exists for one reason: SQLite must not live on a network share. Its locking depends on
/// POSIX advisory locks behaving correctly, which CIFS/SMB and NFS do not reliably provide,
/// and WAL mode additionally needs shared memory that network filesystems cannot offer.
/// The failure mode is silent database corruption, so it is worth naming at startup rather
/// than leaving to be discovered.
/// </remarks>
public static class FilesystemProbe
{
    private static readonly HashSet<string> NetworkFilesystems = new(StringComparer.OrdinalIgnoreCase)
    {
        "cifs", "smbfs", "smb3",
        "nfs", "nfs4",
        "afs", "9p", "ceph", "glusterfs", "lustre", "beegfs",
        "fuse.sshfs", "fuse.rclone", "fuse.davfs", "fuse.s3fs", "fuse.glusterfs",
    };

    /// <summary>
    /// Returns the filesystem type backing <paramref name="path"/>, or null when it cannot
    /// be determined — a non-Linux host, an unreadable mountinfo, or a path that does not
    /// resolve. Callers treat "unknown" as "no complaint".
    /// </summary>
    public static string? GetFilesystemType(string path)
    {
        const string mountInfo = "/proc/self/mountinfo";
        if (!File.Exists(mountInfo)) return null;

        string target;
        try
        {
            target = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(mountInfo);
        }
        catch (IOException)
        {
            return null;
        }

        string? bestType = null;
        var bestLength = -1;

        foreach (var line in lines)
        {
            // 36 35 98:0 /root /mount/point rw,... - fstype source super-options
            // Optional fields sit between the mount point and the " - " separator, so the
            // separator is the only reliable way to find the type.
            var separator = line.IndexOf(" - ", StringComparison.Ordinal);
            if (separator < 0) continue;

            var left = line[..separator].Split(' ');
            var right = line[(separator + 3)..].Split(' ');
            if (left.Length < 5 || right.Length < 1) continue;

            var mountPoint = Unescape(left[4]);
            var fsType = right[0];

            if (!IsUnder(target, mountPoint)) continue;

            // The longest matching mount point is the one actually serving this path.
            if (mountPoint.Length > bestLength)
            {
                bestLength = mountPoint.Length;
                bestType = fsType;
            }
        }

        return bestType;
    }

    /// <summary>True when the path is served by a filesystem known to be network-backed.</summary>
    public static bool IsNetworkFilesystem(string? fsType) =>
        fsType is not null && NetworkFilesystems.Contains(fsType);

    private static bool IsUnder(string path, string mountPoint)
    {
        if (mountPoint == "/") return true;
        if (!path.StartsWith(mountPoint, StringComparison.Ordinal)) return false;

        // "/data" must not match "/database"; the next character has to be a separator.
        return path.Length == mountPoint.Length || path[mountPoint.Length] == '/';
    }

    /// <summary>mountinfo escapes space, tab, newline and backslash as octal.</summary>
    private static string Unescape(string value) => value
        .Replace("\\040", " ", StringComparison.Ordinal)
        .Replace("\\011", "\t", StringComparison.Ordinal)
        .Replace("\\012", "\n", StringComparison.Ordinal)
        .Replace("\\134", "\\", StringComparison.Ordinal);
}
