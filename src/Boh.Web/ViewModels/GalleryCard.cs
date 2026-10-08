namespace Boh.Web.ViewModels;

/// <summary>A gallery card plus the page and search it was reached from.</summary>
public readonly record struct GalleryCard(GalleryPost Post, int FromPage, string? Query);
