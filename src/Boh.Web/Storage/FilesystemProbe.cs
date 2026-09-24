namespace Boh.Web.Storage;

/// <summary>
/// Identifies the filesystem backing a path on Linux.
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
    // Names as DriveInfo.DriveFormat reports them. FUSE mounts all report "fuse", so a local
    // FUSE filesystem is flagged too; it only costs a warning.
    private static readonly HashSet<string> NetworkFilesystems = new(StringComparer.OrdinalIgnoreCase)
    {
        "cifs", "smb", "smb2", "smbfs",
        "nfs", "nfs4",
        "afs", "kafs", "v9fs", "9p", "ceph", "glusterfs", "lustre", "beegfs",
        "fuse",
    };

    /// <summary>
    /// Returns the filesystem type backing <paramref name="path"/>, or null when it cannot
    /// be determined — a non-Linux host, an unreadable mount, or a path that does not
    /// resolve. Callers treat "unknown" as "no complaint".
    /// </summary>
    public static string? GetFilesystemType(string path)
    {
        if (!OperatingSystem.IsLinux()) return null;

        try
        {
            var target = Path.GetFullPath(path);

            // The longest matching mount point is the one actually serving this path.
            return DriveInfo.GetDrives()
                .Where(d => IsUnder(target, d.Name))
                .MaxBy(d => d.Name.Length)
                ?.DriveFormat;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
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
}
