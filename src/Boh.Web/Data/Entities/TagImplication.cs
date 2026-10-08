namespace Boh.Web.Data.Entities;

/// <summary>Tagging <see cref="ChildTagId"/> also applies <see cref="ParentTagId"/>, materialized at write time.</summary>
public class TagImplication
{
    public int ChildTagId { get; set; }
    public Tag ChildTag { get; set; } = null!;

    public int ParentTagId { get; set; }
    public Tag ParentTag { get; set; } = null!;
}
