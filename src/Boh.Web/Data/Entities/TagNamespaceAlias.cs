namespace Boh.Web.Data.Entities;

/// <summary>
/// Redirects a whole namespace to another, so <c>copyright:star_wars</c> is stored as
/// <c>series:star_wars</c>. Resolved on the same paths as <see cref="TagAlias"/> — every
/// write and every search term — so a tag in an aliased namespace never reaches storage.
/// </summary>
/// <remarks>
/// Not expressible as a set of <see cref="TagAlias"/> rows, because the redirect has to
/// cover names that do not exist yet: the point is that importing a series nobody has
/// tagged before still lands in the right namespace, without an alias per title.
/// <para>
/// Unlike a tag alias, nothing is retained under the old namespace. A tag alias keeps its
/// own <see cref="Tag"/> row so it stays visible in autocomplete; a namespace has no row of
/// its own to keep, and the redirect is what makes the namespace discoverable.
/// </para>
/// </remarks>
public class TagNamespaceAlias
{
    /// <summary>The namespace being redirected, e.g. <c>copyright</c>. Never empty; the key.</summary>
    public string Alias { get; set; } = "";

    /// <summary>The namespace it redirects to, e.g. <c>series</c>. Never empty.</summary>
    public string Canonical { get; set; } = "";
}
