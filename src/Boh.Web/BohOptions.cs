namespace Boh.Web;

/// <summary>
/// Runtime configuration, read from <c>BOH_*</c> environment variables.
/// Names are read literally rather than bound by convention, because the documented
/// names use single underscores (<c>BOH_DATA_PATH</c>) which the configuration binder
/// would otherwise not match against PascalCase properties.
/// </summary>
public sealed class BohOptions
{
    public string DataPath { get; init; } = "/data";
    public string AuthMode { get; init; } = "single";
    public string? AdminPassword { get; init; }
    public bool PublicRead { get; init; }
    public int MaxUploadMb { get; init; } = 256;
    public int ImportMax { get; init; } = 50;
    public int ImportTimeoutSec { get; init; } = 300;
    public int PageSize { get; init; } = 40;
    public int ThumbnailMaxEdge { get; init; } = 400;

    // Each location can be pointed somewhere else so the three kinds of state can live on
    // different storage — the usual reason being bulk media on a NAS while the database
    // stays on local disk. Unset means "under DataPath", which is what single-volume
    // deployments have always had, so existing installs are unaffected.
    public string? DatabasePathOverride { get; init; }
    public string? OriginalsPathOverride { get; init; }
    public string? ThumbsPathOverride { get; init; }
    public string? KeysPathOverride { get; init; }
    public string? ImportTempPathOverride { get; init; }

    public string DatabasePath => DatabasePathOverride ?? Path.Combine(DataPath, "boh.db");
    public string OriginalsDir => OriginalsPathOverride ?? Path.Combine(DataPath, "originals");
    public string ThumbsDir => ThumbsPathOverride ?? Path.Combine(DataPath, "thumbs");
    public string KeysDir => KeysPathOverride ?? Path.Combine(DataPath, "keys");

    /// <summary>
    /// Scratch space for gallery-dl downloads. Its contents are read and rewritten into
    /// the blob store regardless, so it gains nothing from sharing a volume with originals
    /// and defaults to local storage where it is likely to be faster.
    /// </summary>
    public string ImportTempDir => ImportTempPathOverride ?? Path.Combine(DataPath, "tmp");

    /// <summary>
    /// Upload staging, deliberately not configurable and always inside the originals root.
    /// Committing a blob is a <c>File.Move</c>; if this sat on another volume every upload
    /// would silently become a cross-device copy — slower, and no longer atomic. Keeping it
    /// here guarantees the commit is a rename within one filesystem.
    /// </summary>
    public string UploadStagingDir => Path.Combine(OriginalsDir, ".staging");

    public string GalleryDlConfigPath => Path.Combine(DataPath, "gallery-dl.conf");

    public long MaxUploadBytes => MaxUploadMb * 1024L * 1024L;

    public bool AuthDisabled => string.Equals(AuthMode, "none", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Foreign key enforcement is off by default in SQLite and must be requested per
    /// connection; Default Timeout becomes the busy timeout, which matters once the
    /// WAL writer and a reader overlap.
    /// </summary>
    public string ConnectionString =>
        $"Data Source={DatabasePath};Foreign Keys=True;Default Timeout=30;Pooling=True";

    public static BohOptions FromConfiguration(IConfiguration c)
    {
        var defaults = new BohOptions();
        return new BohOptions
        {
            DataPath = Str(c, "BOH_DATA_PATH", defaults.DataPath),
            DatabasePathOverride = Optional(c, "BOH_DB_PATH"),
            OriginalsPathOverride = Optional(c, "BOH_ORIGINALS_PATH"),
            ThumbsPathOverride = Optional(c, "BOH_THUMBS_PATH"),
            KeysPathOverride = Optional(c, "BOH_KEYS_PATH"),
            ImportTempPathOverride = Optional(c, "BOH_TEMP_PATH"),
            AuthMode = Str(c, "BOH_AUTH_MODE", defaults.AuthMode),
            AdminPassword = c["BOH_ADMIN_PASSWORD"],
            PublicRead = Bool(c, "BOH_PUBLIC_READ", defaults.PublicRead),
            MaxUploadMb = Int(c, "BOH_MAX_UPLOAD_MB", defaults.MaxUploadMb),
            ImportMax = Int(c, "BOH_IMPORT_MAX", defaults.ImportMax),
            ImportTimeoutSec = Int(c, "BOH_IMPORT_TIMEOUT_SEC", defaults.ImportTimeoutSec),
            PageSize = Int(c, "BOH_PAGE_SIZE", defaults.PageSize),
            ThumbnailMaxEdge = Int(c, "BOH_THUMBNAIL_SIZE", defaults.ThumbnailMaxEdge),
        };
    }

    private static string Str(IConfiguration c, string key, string fallback)
        => string.IsNullOrWhiteSpace(c[key]) ? fallback : c[key]!;

    /// <summary>Null when unset, so the caller can fall back to a DataPath-relative default.</summary>
    private static string? Optional(IConfiguration c, string key)
        => string.IsNullOrWhiteSpace(c[key]) ? null : c[key]!.Trim();

    private static int Int(IConfiguration c, string key, int fallback)
        => int.TryParse(c[key], out var v) && v > 0 ? v : fallback;

    private static bool Bool(IConfiguration c, string key, bool fallback)
        => bool.TryParse(c[key], out var v) ? v : fallback;
}
