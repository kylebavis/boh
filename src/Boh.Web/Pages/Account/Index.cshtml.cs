using Boh.Web.Security;
using Boh.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Boh.Web.Pages.Account;

/// <summary>
/// Self-service account page. Any signed-in user reaches this, not just administrators —
/// someone handed a password by an admin needs a way to change it.
/// </summary>
[Authorize(Policy = BohPolicies.CanWrite)]
public class IndexModel(UserService users, BohOptions options) : PageModel
{
    public string? Username { get; private set; }
    public bool IsAdmin { get; private set; }
    public bool AuthDisabled => options.AuthDisabled;
    public int MinPasswordLength => UserService.MinPasswordLength;

    /// <summary>
    /// The stored palette for each side of the header toggle. Null is the stock Pico look.
    /// With authentication off there is no row to read, so these stay null and the form
    /// falls back to browser storage.
    /// </summary>
    public string? LightTheme { get; private set; }
    public string? DarkTheme { get; private set; }

    [TempData] public string? Message { get; set; }
    public string? Error { get; private set; }

    public void OnGet() => Load();

    public async Task<IActionResult> OnPostAsync(
        string? currentPassword, string? newPassword, string? confirmPassword, CancellationToken ct)
    {
        Load();

        if (options.AuthDisabled)
        {
            Error = "Authentication is disabled on this instance, so there is no password to change.";
            return Page();
        }

        var userId = UserPrincipal.GetId(User);
        if (userId is null) return Forbid();

        if (newPassword != confirmPassword)
        {
            Error = "The new passwords do not match.";
            return Page();
        }

        var result = await users.ChangeOwnPasswordAsync(userId.Value, currentPassword, newPassword, ct);
        if (result is UserResult.Rejected rejected)
        {
            Error = rejected.Reason;
            return Page();
        }

        Message = "Password changed.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostThemeAsync(string? lightTheme, string? darkTheme, CancellationToken ct)
    {
        Load();

        // The form is client-side only in this mode; reaching the handler means someone
        // posted directly, and there is still no row to write to.
        if (options.AuthDisabled)
        {
            Error = "Authentication is disabled on this instance, so there is no account to save against.";
            return Page();
        }

        var userId = UserPrincipal.GetId(User);
        if (userId is null) return Forbid();

        var result = await users.SetThemesAsync(userId.Value, lightTheme, darkTheme, ct);
        if (result is UserResult.Rejected rejected)
        {
            Error = rejected.Reason;
            return Page();
        }

        // No need to reissue the cookie here: the palettes ride on the auth ticket, and
        // RevalidateUserEvents compares it against the row on every request, so the redirect
        // below already arrives carrying the new claims.
        Message = "Theme saved.";
        return RedirectToPage();
    }

    private void Load()
    {
        Username = User.Identity?.Name;
        IsAdmin = UserPrincipal.IsAdmin(User);
        LightTheme = UserPrincipal.GetLightTheme(User);
        DarkTheme = UserPrincipal.GetDarkTheme(User);
    }
}
