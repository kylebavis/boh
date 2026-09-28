namespace Boh.Web.ViewModels;

/// <summary>
/// A tag on a post. <paramref name="Display"/> is the full form for links and removal; the
/// chip shows <paramref name="Name"/> coloured by namespace. Implied tags can't be removed.
/// </summary>
public sealed record PostTagEntry(
    string Display,
    string Name,
    string Namespace,
    bool Implied,
    int PostCount,
    string? Color);

/// <summary><paramref name="CanEdit"/> only hides controls; the server enforces.</summary>
public sealed record PostTagView(
    int PostId,
    IReadOnlyList<PostTagEntry> Tags,
    bool CanEdit,
    string? Error = null);
