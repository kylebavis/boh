namespace Boh.Web.Data.Entities;

/// <summary>A tag. <see cref="Namespace"/> is empty for plain tags.</summary>
public class Tag
{
    public int Id { get; set; }

    public string Namespace { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Denormalized post count for ranking; repairable by recount.</summary>
    public int PostCount { get; set; }

    public List<PostTag> PostTags { get; } = [];
    public List<Post> Posts { get; } = [];

    /// <summary>Renders the tag in its canonical <c>namespace:name</c> text form.</summary>
    public string Display => Namespace.Length == 0 ? Name : $"{Namespace}:{Name}";
}
