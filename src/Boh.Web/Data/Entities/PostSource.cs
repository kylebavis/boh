namespace Boh.Web.Data.Entities;

/// <summary>
/// One origin URL for a post. A post can carry several: the same file is routinely
/// reposted across sites, and an import that lands on an already-stored file records
/// where it was found rather than discarding the address.
/// </summary>
/// <remarks>
/// The surrogate <see cref="Id"/> also supplies the display order — sources read in the
/// order they were recorded, so the address a post arrived with stays first — which is why
/// there is no separate ordinal column to keep in step.
/// </remarks>
public class PostSource
{
    public int Id { get; set; }

    public int PostId { get; set; }
    public Post Post { get; set; } = null!;

    public string Url { get; set; } = "";
}
