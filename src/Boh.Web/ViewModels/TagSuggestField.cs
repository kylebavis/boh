namespace Boh.Web.ViewModels;

/// <summary>A tag input with autocomplete, shared by the tag-admin forms.</summary>
public sealed record TagSuggestField(string Name, string Placeholder, string Label)
{
    /// <summary>Field names are unique per form on the admin page, so they make stable ids.</summary>
    public string Id => $"tagfield-{Name}";

    public string SuggestionsId => $"{Id}-suggestions";
}
