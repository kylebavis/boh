using Boh.Web.Security;
using Boh.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Boh.Web.Pages.Maintenance;

/// <summary>
/// Instance-wide repair actions. Tag-specific maintenance stays on the tag admin page,
/// beside the settings that make it necessary.
/// </summary>
[Authorize(Policy = BohPolicies.IsAdmin)]
public class IndexModel(PostService posts, TagService tags, DuplicateService duplicates) : PageModel
{
    public ThumbnailRepairResult? ThumbnailResult { get; private set; }
    public HashingResult? HashResult { get; private set; }
    public DuplicateScan? DuplicateResult { get; private set; }
    public int? DeletedTagCount { get; private set; }

    /// <summary>The distance two posts must be within to appear in the report together.</summary>
    public int MaxDistance => DuplicateService.MaxDistance;

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostRegenerateThumbnailsAsync(CancellationToken ct)
    {
        ThumbnailResult = await posts.RegenerateMissingThumbnailsAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostComputeHashesAsync(CancellationToken ct)
    {
        HashResult = await duplicates.ComputeMissingHashesAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostScanDuplicatesAsync(CancellationToken ct)
    {
        DuplicateResult = await duplicates.ScanForDuplicatesAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteUnusedTagsAsync(CancellationToken ct)
    {
        DeletedTagCount = await tags.DeleteUnusedTagsAsync(ct);
        return Page();
    }
}
