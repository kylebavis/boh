using System.Diagnostics;
using Boh.Web.Jobs;
using Boh.Web.Security;
using Boh.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Boh.Web.Pages.Import;

/// <summary>
/// Uploads and URL imports. Always authorized: the URL form makes the server fetch a
/// caller-chosen address. URLs are queued; uploads run in the request.
/// </summary>
[Authorize(Policy = BohPolicies.CanWrite)]
public class IndexModel(PostService posts, JobQueue jobs, BohOptions options) : PageModel
{
    /// <summary>How many of someone's recent imports the page lists.</summary>
    private const int ImportsShown = 10;

    [BindProperty] public IFormFile? UploadedFile { get; set; }
    [BindProperty] public string? SourceUrl { get; set; }

    public string? UploadError { get; private set; }

    /// <summary>Set when the uploaded bytes already exist, so the page can link to the original.</summary>
    public int? DuplicateOfPostId { get; private set; }

    public string? UrlError { get; private set; }

    /// <summary>This person's recent imports, newest first.</summary>
    public IReadOnlyList<JobSnapshot> Imports { get; private set; } = [];

    public int MaxUploadMb => options.MaxUploadMb;
    public int MaxFiles => options.ImportMax;
    public int TimeoutSeconds => options.ImportTimeoutSec;

    public void OnGet() => LoadImports();

    public async Task<IActionResult> OnPostUploadAsync(CancellationToken ct)
    {
        LoadImports();

        if (UploadedFile is null || UploadedFile.Length == 0)
        {
            UploadError = "Choose a file to upload.";
            return Page();
        }

        await using var stream = UploadedFile.OpenReadStream();
        var result = await posts.CreateAsync(stream, UserPrincipal.GetId(User), sourceUrl: "", ct);

        switch (result)
        {
            case PostCreateResult.Created created:
                return RedirectToPage("/Posts/Detail", new { id = created.Post.Id });

            case PostCreateResult.Duplicate duplicate:
                DuplicateOfPostId = duplicate.ExistingPostId;
                return Page();

            case PostCreateResult.Rejected rejected:
                UploadError = rejected.Reason;
                return Page();

            default:
                throw new UnreachableException($"Unhandled result {result.GetType().Name}");
        }
    }

    public IActionResult OnPostUrl()
    {
        if (!SourceUrls.TryCanonicalize(SourceUrl, out var url))
        {
            UrlError = string.IsNullOrWhiteSpace(SourceUrl) ? "Enter a URL to import." : SourceUrls.Requirement;
            LoadImports();
            return Page();
        }

        GalleryDlImporter.Enqueue(jobs, url, UserPrincipal.GetId(User));

        // Back to the list, ready for the next URL.
        return RedirectToPage(pageName: null, pageHandler: null, routeValues: null, fragment: "imports");
    }

    /// <summary>
    /// One import's block, polled while running. A missing import answers empty (not an error),
    /// which removes the block and stops htmx polling.
    /// </summary>
    public IActionResult OnGetJob(Guid id) =>
        VisibleImport(id) is { } job ? Partial("_ImportJob", job) : new EmptyResult();

    public IActionResult OnPostCancel(Guid id)
    {
        if (VisibleImport(id) is null) return NotFound();

        jobs.Cancel(id);
        return RedirectToPage(pageName: null, pageHandler: null, routeValues: null, fragment: "imports");
    }

    /// <summary>This person's import, or null; anyone else's is treated as absent.</summary>
    private JobSnapshot? VisibleImport(Guid id) =>
        jobs.Get(id) is { Kind: GalleryDlImporter.JobKind } job && job.RequestedById == UserPrincipal.GetId(User)
            ? job
            : null;

    private void LoadImports()
    {
        var userId = UserPrincipal.GetId(User);

        Imports =
        [
            .. jobs.List(j => j.Kind == GalleryDlImporter.JobKind && j.RequestedById == userId).Take(ImportsShown)
        ];
    }
}
