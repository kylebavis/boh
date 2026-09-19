using Boh.Web.Jobs;
using Boh.Web.Security;
using Boh.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Boh.Web.Pages.Maintenance;

/// <summary>The latest run of one task, or none if it has not run since the server started.</summary>
public sealed record MaintenanceJobView(MaintenanceTask Task, JobSnapshot? Job);

/// <summary>
/// Every push-button repair, tag ones included. Tag administration is configuration; these
/// change nothing about how the instance is set up, only bring derived data back in line with it.
/// </summary>
/// <remarks>
/// A button queues its task and comes straight back rather than doing the work in the request:
/// several of these re-read every original, which on a real archive outlasts any request.
/// </remarks>
[Authorize(Policy = BohPolicies.IsAdmin)]
public class IndexModel(JobQueue jobs) : PageModel
{
    /// <summary>The distance two posts must be within to appear in the report together.</summary>
    public int MaxDistance => DuplicateService.MaxDistance;

    public MaintenanceJobView StatusOf(MaintenanceTask task) => new(task, jobs.Latest(task.Kind));

    public void OnGet()
    {
    }

    /// <summary>One task's status block on its own, which a running task's block polls to replace itself.</summary>
    public IActionResult OnGetStatus(string? task) =>
        MaintenanceTask.Find(task) is { } found ? Partial("_JobStatus", StatusOf(found)) : NotFound();

    public IActionResult OnPostStart(string? task)
    {
        if (MaintenanceTask.Find(task) is not { } found) return NotFound();

        // Exclusive, so pressing the button again while the task runs shows the run in progress
        // instead of queueing a second pass over the same rows.
        jobs.Enqueue(JobLane.Maintenance, found.Kind, found.Title, UserPrincipal.GetId(User), found.Work,
            exclusive: true);

        return RedirectToPage(pageName: null, pageHandler: null, routeValues: null, fragment: found.Key);
    }

    public IActionResult OnPostCancel(Guid id)
    {
        if (jobs.Get(id) is not { Lane: JobLane.Maintenance } job) return NotFound();

        jobs.Cancel(id);

        return RedirectToPage(pageName: null, pageHandler: null, routeValues: null,
            fragment: MaintenanceTask.ForKind(job.Kind)?.Key);
    }
}
