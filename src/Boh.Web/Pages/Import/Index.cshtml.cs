using System.Security.Claims;
using Boh.Web.Services;
using Boh.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Boh.Web.Pages.Import;

/// <summary>
/// Always authorized, even under BOH_PUBLIC_READ: this endpoint makes the server fetch a
/// URL the caller chooses, which is not something to expose anonymously.
/// </summary>
[Authorize(Policy = BohPolicies.CanWrite)]
public class IndexModel(GalleryDlImporter importer, BohOptions options) : PageModel
{
    [BindProperty] public string? SourceUrl { get; set; }

    public ImportResult? Result { get; private set; }

    public int MaxFiles => options.ImportMax;
    public int TimeoutSeconds => options.ImportTimeoutSec;

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(SourceUrl))
        {
            Result = new ImportResult([], [], "Enter a URL to import.");
            return Page();
        }

        Result = await importer.ImportAsync(SourceUrl.Trim(), CurrentUserId(), ct);
        return Page();
    }

    private int? CurrentUserId() =>
        int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}
