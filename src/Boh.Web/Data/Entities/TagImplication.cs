namespace Boh.Web.Data.Entities;

/// <summary>
/// Declares that tagging a post with <see cref="ChildTagId"/> also implies
/// <see cref="ParentTagId"/> — e.g. <c>meme:pondering_my_orb</c> implies <c>format:reaction_image</c>.
/// Implied tags are materialized into PostTags at write time (marked
/// <see cref="TagSource.Implied"/>) so search stays a plain join.
/// </summary>
public class TagImplication
{
    public int ChildTagId { get; set; }
    public Tag ChildTag { get; set; } = null!;

    public int ParentTagId { get; set; }
    public Tag ParentTag { get; set; } = null!;
}
