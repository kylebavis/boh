namespace Boh.Web.ViewModels;

/// <summary>One origin, identified for removal by row id.</summary>
public sealed record PostSourceEntry(int Id, string Url);

/// <summary><paramref name="CanEdit"/> only hides controls; the server enforces.</summary>
public sealed record PostSourceView(
    int PostId,
    IReadOnlyList<PostSourceEntry> Sources,
    bool CanEdit,
    string? Error = null);
