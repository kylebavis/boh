using System.Diagnostics;
using Boh.Web.Jobs;
using Boh.Web.Security;
using Boh.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Boh.Web.Pages.Import;

/// <summary>
/// Every way a post enters the collection: a file from this machine, or a URL handed to gallery-dl.
/// </summary>
/// <remarks>
/// Always authorized, even under BOH_PUBLIC_READ: browsing may be public, adding to the collection
/// never is — and the URL form makes the server fetch an address the caller chooses, which is not
/// something to expose anonymously.
/// <para>
/// An upload is handled in the request, since the file is already in it. A URL is queued
/// instead: a gallery can take minutes to download, and nobody needs to sit and watch it.
/// </para>
/// </remarks>
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

    /// <summary>This person's recent imports, newest first, running ones included.</summary>
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

        var userId = UserPrincipal.GetId(User);

        jobs.Enqueue(JobLane.Import, GalleryDlImporter.JobKind, url, userId, async job =>
            await job.Services.GetRequiredService<GalleryDlImporter>()
                .ImportAsync(url, userId, job, job.CancellationToken));

        // Back to the list rather than to a page of its own: the form stays put, ready for the
        // next URL, and this one shows up underneath it.
        return RedirectToPage(pageName: null, pageHandler: null, routeValues: null, fragment: "imports");
    }

    /// <summary>
    /// One import's block on its own, which a running import polls to replace itself. An import
    /// that is not there — someone else's, or one a restart forgot — answers with nothing, which
    /// removes the block. An error status would not: htmx leaves the block in place on one, so a
    /// page left open across a restart would keep polling every two seconds for good.
    /// </summary>
    public IActionResult OnGetJob(Guid id) =>
        VisibleImport(id) is { } job ? Partial("_ImportJob", job) : new EmptyResult();

    public IActionResult OnPostCancel(Guid id)
    {
        if (VisibleImport(id) is null) return NotFound();

        jobs.Cancel(id);
        return RedirectToPage(pageName: null, pageHandler: null, routeValues: null, fragment: "imports");
    }

    /// <summary>
    /// One of this person's imports, or null. Anyone else's is treated as not existing: the URL
    /// and what it brought in are theirs.
    /// </summary>
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
