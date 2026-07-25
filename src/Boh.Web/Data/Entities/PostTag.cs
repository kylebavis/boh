namespace Boh.Web.Data.Entities;

/// <summary>
/// Why a tag is attached to a post. Without this distinction there is no way to tell
/// whether <c>format:reaction_image</c> was typed by a user or derived from <c>meme:pondering_my_orb</c>,
/// which makes correct removal impossible.
/// </summary>
public enum TagSource
{
    /// <summary>Added directly by a user or an importer. Survives implication changes.</summary>
    Explicit = 0,

    /// <summary>Derived from a tag implication. Recomputed whenever the post's explicit tags change.</summary>
    Implied = 1
}

public class PostTag
{
    public int PostId { get; set; }
    public Post Post { get; set; } = null!;

    public int TagId { get; set; }
    public Tag Tag { get; set; } = null!;

    public TagSource Source { get; set; } = TagSource.Explicit;
}
