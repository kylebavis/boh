using System.Diagnostics;
using Boh.Web.Services;
using Boh.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Boh.Web.Pages.Posts;

/// <summary>
/// Explicitly authorized rather than relying on the fallback policy, which is disabled
/// when BOH_PUBLIC_READ is on — browsing may be public, uploading never is.
/// </summary>
[Authorize(Policy = BohPolicies.CanWrite)]
public class UploadModel(PostService posts, BohOptions options) : PageModel
{
    [BindProperty]
    public IFormFile? UploadedFile { get; set; }

    public string? Error { get; private set; }

    /// <summary>Set when the uploaded bytes already exist, so the page can link to the original.</summary>
    public int? DuplicateOfPostId { get; private set; }

    public int MaxUploadMb => options.MaxUploadMb;

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (UploadedFile is null || UploadedFile.Length == 0)
        {
            Error = "Choose a file to upload.";
            return Page();
        }

        await using var stream = UploadedFile.OpenReadStream();
        var result = await posts.CreateAsync(stream, uploadedById: null, sourceUrl: "", ct);

        switch (result)
        {
            case PostCreateResult.Created created:
                return RedirectToPage("Detail", new { id = created.Post.Id });

            case PostCreateResult.Duplicate duplicate:
                DuplicateOfPostId = duplicate.ExistingPostId;
                return Page();

            case PostCreateResult.Rejected rejected:
                Error = rejected.Reason;
                return Page();

            default:
                throw new UnreachableException($"Unhandled result {result.GetType().Name}");
        }
    }
}
