using Boh.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Boh.Web.Pages.Tags;

/// <summary>
/// Returns an HTML fragment of tag suggestions for the token currently being typed.
/// HTMX sends the whole input value, so the last whitespace-separated token is extracted
/// here rather than in the browser.
/// </summary>
public class AutocompleteModel(TagService tags) : PageModel
{
    private const int SuggestionLimit = 10;

    public IReadOnlyList<TagSuggestion> Suggestions { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(string? q, CancellationToken ct)
    {
        // The control being completed may be named `q` (search) or `tags` (post editor).
        var raw = !string.IsNullOrWhiteSpace(q) ? q : Request.Query["tags"].ToString();

        Suggestions = await tags.AutocompleteAsync(LastToken(raw), SuggestionLimit, ct);
        return Partial("_TagAutocomplete", Suggestions);
    }

    /// <summary>Takes the token under the cursor and drops a leading '-' so negated search terms complete too.</summary>
    private static string LastToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";

        var token = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";

        // A trailing space means the user finished that token and is starting a new one.
        if (value.Length > 0 && char.IsWhiteSpace(value[^1])) return "";

        return token.StartsWith('-') ? token[1..] : token;
    }
}
