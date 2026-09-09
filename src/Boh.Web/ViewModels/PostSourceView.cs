namespace Boh.Web.ViewModels;

/// <summary>
/// One recorded origin. <paramref name="Id"/> is the row id rather than the URL, because a
/// URL is long, arbitrary and would have to survive a round trip through a query string to
/// identify what to remove.
/// </summary>
public sealed record PostSourceEntry(int Id, string Url);

/// <summary>
/// <paramref name="CanEdit"/> hides the add and remove controls from viewers who could not
/// use them anyway — an anonymous visitor under BOH_PUBLIC_READ. The server still enforces
/// it; the flag only avoids showing dead controls.
/// </summary>
public sealed record PostSourceView(
    int PostId,
    IReadOnlyList<PostSourceEntry> Sources,
    bool CanEdit,
    string? Error = null);
