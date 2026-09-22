namespace Boh.Web.ViewModels;

/// <summary>The columns a gallery card renders, so listing a page does not load whole posts.</summary>
public sealed record GalleryPost(int Id, string Sha256, int Width, int Height, bool IsVideo);
