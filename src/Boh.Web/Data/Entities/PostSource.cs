namespace Boh.Web.Data.Entities;

/// <summary>One origin URL for a post. <see cref="Id"/> order is display order.</summary>
public class PostSource
{
    public int Id { get; set; }

    public int PostId { get; set; }
    public Post Post { get; set; } = null!;

    public string Url { get; set; } = "";
}
