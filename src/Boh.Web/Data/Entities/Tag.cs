namespace Boh.Web.Data.Entities;

/// <summary>
/// A namespaced label. <see cref="Namespace"/> is empty for plain tags, so
/// ("", "landscape") renders as <c>landscape</c> and ("artist", "foo") as <c>artist:foo</c>.
/// </summary>
public class Tag
{
    public int Id { get; set; }

    public string Namespace { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>
    /// Denormalized count of posts carrying this tag, kept current by TagService so
    /// autocomplete can rank without a COUNT subquery. Repairable via a recount action.
    /// </summary>
    public int PostCount { get; set; }

    public List<PostTag> PostTags { get; } = [];
    public List<Post> Posts { get; } = [];

    /// <summary>Renders the tag in its canonical <c>namespace:name</c> text form.</summary>
    public string Display => Namespace.Length == 0 ? Name : $"{Namespace}:{Name}";
}
