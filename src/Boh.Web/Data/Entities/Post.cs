namespace Boh.Web.Data.Entities;

/// <summary>
/// A single stored media item. <see cref="Sha256"/> doubles as the storage key for
/// both the original blob and its thumbnail, so a post's files can be located
/// without consulting anything but the hash.
/// </summary>
public class Post
{
    public int Id { get; set; }

    /// <summary>Lowercase hex SHA-256 of the original file. Unique: one post per distinct file.</summary>
    public string Sha256 { get; set; } = "";

    /// <summary>Canonical extension including the dot, derived from sniffed content rather than the upload's filename.</summary>
    public string FileExtension { get; set; } = "";

    public string MimeType { get; set; } = "";
    public long FileSizeBytes { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>Duration in seconds; null for still images.</summary>
    public double? DurationSec { get; set; }

    public bool IsVideo { get; set; }

    /// <summary>Origin URL when the post arrived via gallery-dl import; empty for direct uploads.</summary>
    public string SourceUrl { get; set; } = "";

    public string Description { get; set; } = "";

    public DateTimeOffset UploadedAt { get; set; }

    public int? UploadedById { get; set; }
    public User? UploadedBy { get; set; }

    public List<PostTag> PostTags { get; } = [];

    /// <summary>Skip navigation over <see cref="PostTags"/>; lets search express itself as <c>p.Tags.Any(...)</c>.</summary>
    public List<Tag> Tags { get; } = [];
}
