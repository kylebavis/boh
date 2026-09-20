using System.Text.Json;
using System.Text.Json.Nodes;
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
public class IndexModel(
    UserService users,
    PasskeyService passkeys,
    PasskeyChallenge challenges,
    BohOptions options) : PageModel
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

    /// <summary>This account's registered passkeys, oldest first. Empty with authentication off.</summary>
    public IReadOnlyList<PasskeyRow> Passkeys { get; private set; } = [];

    /// <summary>
    /// Whether the browser will let this page run a WebAuthn ceremony at all. False on a
    /// plain-HTTP instance that is not localhost, where the form is shown with the reason
    /// rather than left to fail at the click.
    /// </summary>
    public bool PasskeysUsable => PasskeyRelyingParty.IsSecureContext(Request);

    [TempData] public string? Message { get; set; }
    public string? Error { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        Load();
        await LoadPasskeysAsync(ct);
    }

    public async Task<IActionResult> OnPostAsync(
        string? currentPassword, string? newPassword, string? confirmPassword, CancellationToken ct)
    {
        Load();
        await LoadPasskeysAsync(ct);

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
        await LoadPasskeysAsync(ct);

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

    // ---- passkeys ------------------------------------------------------

    /// <summary>
    /// Hands the browser what it needs to make a credential, and keeps the matching state so
    /// the answer can be checked against it. Answers JSON: passkeys.js drives this, not a form.
    /// </summary>
    public async Task<IActionResult> OnPostPasskeyOptionsAsync(CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return PasskeyProblem("There is no account to add a passkey to.");

        var ceremony = await passkeys.BeginRegistrationAsync(userId, HttpContext, ct);
        if (ceremony is null) return PasskeyProblem("That account no longer exists.");

        challenges.Store(HttpContext, PasskeyChallenge.RegistrationPurpose, ceremony.State);
        return Content(ceremony.OptionsJson, "application/json");
    }

    /// <summary>Takes the authenticator's answer and, if it checks out, stores the passkey.</summary>
    public async Task<IActionResult> OnPostPasskeyAsync(CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return PasskeyProblem("There is no account to add a passkey to.");

        var state = challenges.Take(HttpContext, PasskeyChallenge.RegistrationPurpose);
        if (state is null) return PasskeyProblem("That took too long. Start again.");

        NewPasskey? posted;
        try
        {
            posted = await JsonSerializer.DeserializeAsync<NewPasskey>(Request.Body, JsonOptions, ct);
        }
        catch (JsonException)
        {
            return PasskeyProblem("The browser sent something this server could not read.");
        }

        if (posted?.Credential is null) return PasskeyProblem("The browser sent no credential.");

        var result = await passkeys.CompleteRegistrationAsync(
            userId, posted.Name, posted.Credential.ToJsonString(), state, HttpContext, ct);

        if (result is UserResult.Rejected rejected) return PasskeyProblem(rejected.Reason);

        Message = "Passkey added.";
        return new JsonResult(new { ok = true });
    }

    public async Task<IActionResult> OnPostPasskeyRenameAsync(int passkeyId, string? name, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Forbid();

        var result = await passkeys.RenameAsync(userId, passkeyId, name, ct);
        if (result is UserResult.Rejected rejected) return await RerenderAsync(rejected.Reason, ct);

        Message = "Passkey renamed.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostPasskeyDeleteAsync(int passkeyId, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Forbid();

        var result = await passkeys.DeleteAsync(userId, passkeyId, ct);
        if (result is UserResult.Rejected rejected) return await RerenderAsync(rejected.Reason, ct);

        Message = "Passkey removed.";
        return RedirectToPage();
    }

    /// <summary>
    /// What the browser posts back once the authenticator has made a credential. The
    /// credential travels as an opaque node rather than a typed model: it is passed straight
    /// through to the framework's verifier, which parses it itself, so restating its shape
    /// here would only be a second place for it to be wrong.
    /// </summary>
    private sealed record NewPasskey(string? Name, JsonNode? Credential);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Null when there is no account behind the request, which is the case with authentication
    /// off — the page is reachable then, but there is no row to hang a credential on.
    /// </summary>
    private int? CurrentUserId() => options.AuthDisabled ? null : UserPrincipal.GetId(User);

    /// <summary>
    /// A failed ceremony, in the shape passkeys.js reads. Deliberately a 400 rather than a
    /// success carrying an error: the script can then treat any non-OK response the same way.
    /// </summary>
    private IActionResult PasskeyProblem(string reason) =>
        new JsonResult(new { error = reason }) { StatusCode = StatusCodes.Status400BadRequest };

    private async Task<IActionResult> RerenderAsync(string error, CancellationToken ct)
    {
        Load();
        await LoadPasskeysAsync(ct);
        Error = error;
        return Page();
    }

    private void Load()
    {
        Username = User.Identity?.Name;
        IsAdmin = UserPrincipal.IsAdmin(User);
        LightTheme = UserPrincipal.GetLightTheme(User);
        DarkTheme = UserPrincipal.GetDarkTheme(User);
    }

    private async Task LoadPasskeysAsync(CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return;
        Passkeys = await passkeys.ListAsync(userId, ct);
    }
}
