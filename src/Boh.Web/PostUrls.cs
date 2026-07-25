using Boh.Web.Data.Entities;

namespace Boh.Web;

/// <summary>Builds the blob URLs served by <c>FileEndpoints</c>.</summary>
public static class PostUrls
{
    public static string Thumb(Post post) => $"/files/t/{post.Sha256}.webp";

    public static string Original(Post post) => $"/files/o/{post.Sha256}{post.FileExtension}";

    /// <summary>Renders a byte count in the largest unit that keeps it readable.</summary>
    public static string HumanSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB"
    };
}
