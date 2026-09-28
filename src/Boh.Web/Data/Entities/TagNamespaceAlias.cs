namespace Boh.Web.Data.Entities;

/// <summary>
/// Redirects a namespace (<c>copyright:x</c> stored as <c>series:x</c>), covering names that
/// don't exist yet.
/// </summary>
public class TagNamespaceAlias
{
    /// <summary>The redirected namespace, e.g. <c>copyright</c>. The key.</summary>
    public string Alias { get; set; } = "";

    /// <summary>The target, e.g. <c>series</c>.</summary>
    public string Canonical { get; set; } = "";
}
