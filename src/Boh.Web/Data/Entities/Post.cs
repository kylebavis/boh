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

    /// <summary>
    /// Perceptual hash of the visual content — see <see cref="Media.PerceptualHash"/> — which
    /// is how a resized or re-encoded copy of this file is recognized even though its
    /// <see cref="Sha256"/> shares nothing with this one. A bag of bits rather than a number:
    /// the sign is meaningless and two values are only ever compared bit by bit.
    /// Null when there is none — video, an image with no detail to hash, or a post stored
    /// before hashing existed.
    /// </summary>
    public long? PerceptualHash { get; set; }

    /// <summary>
    /// True once hashing has been attempted, whatever came of it. Without this a backfill pass
    /// cannot tell an image it has already failed to hash from one it has not reached yet, and
    /// would re-decode the hopeless ones on every run — eventually spending its whole budget
    /// on them and never advancing.
    /// </summary>
    public bool PerceptualHashTried { get; set; }

    /// <summary>
    /// Where this file came from, in the order the addresses were recorded. Empty for a
    /// direct upload, and more than one entry once the same file turns up elsewhere.
    /// </summary>
    public List<PostSource> Sources { get; } = [];

    public string Description { get; set; } = "";

    public DateTimeOffset UploadedAt { get; set; }

    public int? UploadedById { get; set; }
    public User? UploadedBy { get; set; }

    public List<PostTag> PostTags { get; } = [];

    /// <summary>Skip navigation over <see cref="PostTags"/>; lets search express itself as <c>p.Tags.Any(...)</c>.</summary>
    public List<Tag> Tags { get; } = [];
}
