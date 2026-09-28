namespace Boh.Web;

/// <summary>Runtime configuration from <c>BOH_*</c> environment variables, read by literal name.</summary>
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

    // Unset means under DataPath.
    public string? DatabasePathOverride { get; init; }
    public string? OriginalsPathOverride { get; init; }
    public string? ThumbsPathOverride { get; init; }
    public string? KeysPathOverride { get; init; }
    public string? ImportTempPathOverride { get; init; }

    public string DatabasePath => DatabasePathOverride ?? Path.Combine(DataPath, "boh.db");
    public string OriginalsDir => OriginalsPathOverride ?? Path.Combine(DataPath, "originals");
    public string ThumbsDir => ThumbsPathOverride ?? Path.Combine(DataPath, "thumbs");
    public string KeysDir => KeysPathOverride ?? Path.Combine(DataPath, "keys");

    /// <summary>gallery-dl scratch space. Defaults to local storage.</summary>
    public string ImportTempDir => ImportTempPathOverride ?? Path.Combine(DataPath, "tmp");

    /// <summary>Always inside the originals root, so committing a blob is an atomic rename.</summary>
    public string UploadStagingDir => Path.Combine(OriginalsDir, ".staging");

    /// <summary>WebAuthn relying party id. Unset, each request uses its own host. Changing it invalidates passkeys.</summary>
    public string? PasskeyRpIdOverride { get; init; }

    /// <summary>Comma-separated origins, scheme and port included. Unset, only the request's own origin.</summary>
    public string? PasskeyOriginsOverride { get; init; }

    /// <summary>Parsed form of <see cref="PasskeyOriginsOverride"/>; empty when unset.</summary>
    public IReadOnlyList<string> PasskeyOrigins =>
        (PasskeyOriginsOverride ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public string GalleryDlConfigPath => Path.Combine(DataPath, "gallery-dl.conf");

    public long MaxUploadBytes => MaxUploadMb * 1024L * 1024L;

    public bool AuthDisabled => string.Equals(AuthMode, "none", StringComparison.OrdinalIgnoreCase);

    /// <summary>Foreign keys must be enabled per connection; Default Timeout is the busy timeout.</summary>
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
            PasskeyRpIdOverride = Optional(c, "BOH_PASSKEY_RP_ID"),
            PasskeyOriginsOverride = Optional(c, "BOH_PASSKEY_ORIGINS"),
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

    private static string? Optional(IConfiguration c, string key)
        => string.IsNullOrWhiteSpace(c[key]) ? null : c[key]!.Trim();

    private static int Int(IConfiguration c, string key, int fallback)
        => int.TryParse(c[key], out var v) && v > 0 ? v : fallback;

    private static bool Bool(IConfiguration c, string key, bool fallback)
        => bool.TryParse(c[key], out var v) ? v : fallback;
}
