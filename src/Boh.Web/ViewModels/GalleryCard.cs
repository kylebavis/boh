using Boh.Web.Data.Entities;

namespace Boh.Web.ViewModels;

/// <summary>
/// A post as it appears in the gallery grid, plus the listing it was reached from. The
/// detail page carries that context forward so deleting a post returns to the same page of
/// the same search instead of dumping the user on the unfiltered homepage.
/// </summary>
/// <param name="Post">The post to render.</param>
/// <param name="FromPage">The gallery page number this card appears on.</param>
/// <param name="Query">The active search, or null when the gallery is unfiltered.</param>
public readonly record struct GalleryCard(Post Post, int FromPage, string? Query);
