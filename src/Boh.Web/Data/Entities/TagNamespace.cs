namespace Boh.Web.Data.Entities;

/// <summary>
/// A colour assigned to a tag namespace.
/// </summary>
/// <remarks>
/// Rows exist only for namespaces someone has explicitly styled. Every other namespace
/// still gets a stable colour picked from a palette, so tags are visually separable
/// before any configuration happens and a new namespace never renders as unstyled.
/// </remarks>
public class TagNamespace
{
    public int Id { get; set; }

    /// <summary>The namespace this styles, e.g. <c>artist</c>. Never empty — plain tags use the default colour.</summary>
    public string Name { get; set; } = "";

    /// <summary>CSS hex colour including the leading '#'.</summary>
    public string Color { get; set; } = "";
}
