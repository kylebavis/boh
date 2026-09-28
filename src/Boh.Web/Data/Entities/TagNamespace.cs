namespace Boh.Web.Data.Entities;

/// <summary>An explicit colour for a namespace; others get a palette colour.</summary>
public class TagNamespace
{
    public int Id { get; set; }

    /// <summary>The namespace this styles, e.g. <c>artist</c>. Never empty — plain tags use the default colour.</summary>
    public string Name { get; set; } = "";

    /// <summary>CSS hex colour including the leading '#'.</summary>
    public string Color { get; set; } = "";
}
