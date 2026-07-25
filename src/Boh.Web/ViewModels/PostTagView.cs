namespace Boh.Web.ViewModels;

/// <summary>
/// One tag as shown on a post. <paramref name="Implied"/> tags have no remove control:
/// they are derived from an implication, so deleting one directly would only have it
/// reappear on the next write.
/// </summary>
/// <summary>
/// <paramref name="Display"/> is the full <c>namespace:name</c> form and is what links,
/// tooltips and the remove handler must use. <paramref name="Name"/> is the bare name shown
/// in the chip — the namespace is conveyed by <paramref name="Color"/> instead of repeating
/// it as a prefix on every tag.
/// </summary>
public sealed record PostTagEntry(
    string Display,
    string Name,
    string Namespace,
    bool Implied,
    int PostCount,
    string? Color);

/// <summary>
/// <paramref name="CanEdit"/> hides the add and remove controls from viewers who could not
/// use them anyway — the case that matters is an anonymous visitor under BOH_PUBLIC_READ.
/// The server still enforces this; the flag only avoids showing dead controls.
/// </summary>
public sealed record PostTagView(
    int PostId,
    IReadOnlyList<PostTagEntry> Tags,
    bool CanEdit,
    string? Error = null);
