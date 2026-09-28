using Boh.Web.Services;
using Boh.Web.Tags;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Boh.Web.Pages.Tags;

/// <summary>Suggestions for the last token of the input, as an HTML fragment.</summary>
public class AutocompleteModel(TagService tags) : PageModel
{
    private const int SuggestionLimit = 10;

    public IReadOnlyList<TagSuggestion> Suggestions { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(string? q, CancellationToken ct)
    {
        // The control being completed may be named `q` (search) or `tags` (post editor).
        var searching = !string.IsNullOrWhiteSpace(q);
        var raw = searching ? q : Request.Query["tags"].ToString();

        var token = LastToken(raw);

        // In a search, <c>url:</c> is a predicate, not a namespace.
        if (searching && token.StartsWith(SearchQuery.SourcePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Partial("_TagAutocomplete", Suggestions);
        }

        Suggestions = await tags.AutocompleteAsync(token, SuggestionLimit, ct);
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
