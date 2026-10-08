namespace Boh.Web.Data.Entities;

/// <summary>A stored media item. <see cref="Sha256"/> is the storage key for the original and thumbnail.</summary>
public class Post
{
    public int Id { get; set; }

    /// <summary>Lowercase hex SHA-256 of the original file. Unique: one post per distinct file.</summary>
    public string Sha256 { get; set; } = "";

    /// <summary>Canonical extension with the dot, from sniffed content.</summary>
    public string FileExtension { get; set; } = "";

    public string MimeType { get; set; } = "";
    public long FileSizeBytes { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>Duration in seconds; null for still images.</summary>
    public double? DurationSec { get; set; }

    public bool IsVideo { get; set; }

    /// <summary>See <see cref="Media.PerceptualHash"/>. Null for video, featureless images, or not yet hashed.</summary>
    public long? PerceptualHash { get; set; }

    /// <summary>Hashing was attempted, so the backfill skips hopeless images.</summary>
    public bool PerceptualHashTried { get; set; }

    /// <summary>Origins in recorded order. Empty for a direct upload.</summary>
    public List<PostSource> Sources { get; } = [];

    public string Description { get; set; } = "";

    public DateTimeOffset UploadedAt { get; set; }

    public int? UploadedById { get; set; }
    public User? UploadedBy { get; set; }

    public List<PostTag> PostTags { get; } = [];

    /// <summary>Skip navigation over <see cref="PostTags"/>, for search.</summary>
    public List<Tag> Tags { get; } = [];
}
