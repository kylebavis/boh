using Boh.Web.Security;
using Boh.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Boh.Web.Pages.Users;

[Authorize(Policy = BohPolicies.IsAdmin)]
public class IndexModel(UserService users) : PageModel
{
    public IReadOnlyList<UserRow> Users { get; private set; } = [];

    /// <summary>Used to mark the current user's own row and to keep them from deleting it.</summary>
    public int? CurrentUserId { get; private set; }

    public int MinPasswordLength => UserService.MinPasswordLength;
    public string SeededAdminUsername => UserService.AdminUsername;

    [TempData] public string? Message { get; set; }
    [TempData] public string? Error { get; set; }

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

    public async Task<IActionResult> OnPostCreateAsync(
        string? username, string? password, bool isAdmin, CancellationToken ct)
    {
        Apply(await users.CreateAsync(username, password, isAdmin, ct),
            $"Created '{username?.Trim().ToLowerInvariant()}'.");

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int userId, CancellationToken ct)
    {
        if (userId == UserPrincipal.GetId(User))
        {
            Error = "You cannot delete the account you are signed in with.";
            return RedirectToPage();
        }

        Apply(await users.DeleteAsync(userId, ct), "User deleted.");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSetAdminAsync(int userId, bool isAdmin, CancellationToken ct)
    {
        Apply(await users.SetAdminAsync(userId, isAdmin, ct),
            isAdmin ? "Granted administrator rights." : "Revoked administrator rights.");

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostResetPasswordAsync(int userId, string? password, CancellationToken ct)
    {
        Apply(await users.SetPasswordAsync(userId, password, ct), "Password reset.");
        return RedirectToPage();
    }

    private void Apply(UserResult result, string successMessage)
    {
        if (result is UserResult.Rejected rejected) Error = rejected.Reason;
        else Message = successMessage;
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Users = await users.ListAsync(ct);
        CurrentUserId = UserPrincipal.GetId(User);
    }
}
