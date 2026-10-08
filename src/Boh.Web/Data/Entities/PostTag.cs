namespace Boh.Web.Data.Entities;

/// <summary>Why a tag is on a post; needed to remove implied tags correctly.</summary>
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
