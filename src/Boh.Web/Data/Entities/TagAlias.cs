namespace Boh.Web.Data.Entities;

/// <summary>Redirects one tag to another. The alias's row stays (count 0) for autocomplete and admin.</summary>
public class TagAlias
{
    public int AliasTagId { get; set; }
    public Tag AliasTag { get; set; } = null!;

    public int CanonicalTagId { get; set; }
    public Tag CanonicalTag { get; set; } = null!;
}
