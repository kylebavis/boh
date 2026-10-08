namespace Boh.Web.Data.Entities;

/// <summary>An explicit color for a namespace; others get a palette color.</summary>
public class TagNamespace
{
    public int Id { get; set; }

    /// <summary>The namespace this styles, e.g. <c>artist</c>. Never empty — plain tags use the default color.</summary>
    public string Name { get; set; } = "";

    /// <summary>CSS hex color including the leading '#'.</summary>
    public string Color { get; set; } = "";
}
