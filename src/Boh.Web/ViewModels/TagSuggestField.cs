namespace Boh.Web.ViewModels;

/// <summary>
/// A text input that completes against existing tags. Used for the tag-admin forms, whose
/// fields all name a tag; sharing one partial keeps the six of them wired identically, since
/// the autocomplete plumbing is easy to get subtly wrong one field at a time.
/// </summary>
/// <param name="Name">The form field name the page handler binds.</param>
/// <param name="Placeholder">Placeholder text.</param>
/// <param name="Label">Accessible label — these forms are a row of inputs with no visible labels.</param>
public sealed record TagSuggestField(string Name, string Placeholder, string Label)
{
    /// <summary>Field names are unique per form on the admin page, so they make stable ids.</summary>
    public string Id => $"tagfield-{Name}";

    public string SuggestionsId => $"{Id}-suggestions";
}
