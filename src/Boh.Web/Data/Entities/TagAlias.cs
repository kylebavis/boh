namespace Boh.Web.Data.Entities;

/// <summary>
/// Redirects one tag to another. Resolved on every write and on every search term, so an
/// aliased tag never reaches storage. The alias's own <see cref="Tag"/> row is retained
/// (with PostCount 0) so it stays visible in autocomplete and manageable in the admin UI.
/// </summary>
public class TagAlias
{
    public int AliasTagId { get; set; }
    public Tag AliasTag { get; set; } = null!;

    public int CanonicalTagId { get; set; }
    public Tag CanonicalTag { get; set; } = null!;
}
