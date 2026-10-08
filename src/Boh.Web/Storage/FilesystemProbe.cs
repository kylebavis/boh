namespace Boh.Web.Storage;

/// <summary>Identifies a path's filesystem, to warn when SQLite sits on a network share.</summary>
public static class FilesystemProbe
{
    // As DriveInfo.DriveFormat reports them. Local FUSE is flagged too; only a warning.
    private static readonly HashSet<string> NetworkFilesystems = new(StringComparer.OrdinalIgnoreCase)
    {
        "cifs", "smb", "smb2", "smbfs",
        "nfs", "nfs4",
        "afs", "kafs", "v9fs", "9p", "ceph", "glusterfs", "lustre", "beegfs",
        "fuse",
    };

    /// <summary>Filesystem type, or null when unknown (treated as fine).</summary>
    public static string? GetFilesystemType(string path)
    {
        if (!OperatingSystem.IsLinux()) return null;

        try
        {
            var target = Path.GetFullPath(path);

            // Longest matching mount point wins.
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

    public static bool IsNetworkFilesystem(string? fsType) =>
        fsType is not null && NetworkFilesystems.Contains(fsType);

    private static bool IsUnder(string path, string mountPoint)
    {
        if (mountPoint == "/") return true;
        if (!path.StartsWith(mountPoint, StringComparison.Ordinal)) return false;

        // "/data" must not match "/database".
        return path.Length == mountPoint.Length || path[mountPoint.Length] == '/';
    }
}
