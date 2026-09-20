using System.Text.Json;
using System.Text.Json.Nodes;
using Boh.Web.Security;
using Boh.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace Boh.Web.Pages.Account;

[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicy)]
public class LoginModel(
    UserService users,
    PasskeyService passkeys,
    PasskeyChallenge challenges,
    BohOptions options,
    ILogger<LoginModel> logger) : PageModel
{
    /// <summary>
    /// Names the limiter configured in <c>Program.cs</c> that caps how fast passwords can be
    /// guessed here. Only POSTs are counted, so reloading the form is never throttled.
    /// </summary>
    public const string RateLimitPolicy = "login";

    /// <summary>
    /// Attempts allowed per address per <see cref="RateLimitWindow"/>. Loose enough that
    /// someone fumbling a password manager never notices, tight enough that guessing at
    /// scale is not worth starting.
    /// </summary>
    public const int RateLimitAttempts = 10;

    public static readonly TimeSpan RateLimitWindow = TimeSpan.FromMinutes(5);

    [BindProperty] public string? Username { get; set; }
    [BindProperty] public string? Password { get; set; }

    public string? Error { get; private set; }

    /// <summary>
    /// Whether the browser will let this page use a passkey at all — see
    /// <see cref="PasskeyRelyingParty.IsSecureContext"/>. The button is left out entirely
    /// when it cannot, rather than offered and made to fail.
    /// </summary>
    public bool PasskeysUsable => PasskeyRelyingParty.IsSecureContext(Request);

    public IActionResult OnGet(string? returnUrl)
    {
        if (options.AuthDisabled) return Redirect("/");

        ViewData["ReturnUrl"] = SafeReturnUrl(returnUrl);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl, CancellationToken ct)
    {
        var target = SafeReturnUrl(returnUrl);
        ViewData["ReturnUrl"] = target;

        var user = await users.AuthenticateAsync(Username, Password, ct);
        if (user is null)
        {
            // Logged at warning, and with the address attached, because a run of these is the
            // one signal that someone is working on the password. The attempted name is
            // whatever was typed, so it goes through LogSafe.
            logger.LogWarning(
                "Failed sign-in for {Username} from {RemoteIp}",
                LogSafe.Value(Username), ClientAddress());

            Error = "Incorrect username or password.";
            return Page();
        }

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            UserPrincipal.Create(user),
            new AuthenticationProperties { IsPersistent = true });

        logger.LogInformation(
            "User {Username} signed in from {RemoteIp}", user.Username, ClientAddress());

        return Redirect(target);
    }

    // ---- passkeys ------------------------------------------------------

    /// <summary>
    /// Hands the browser a challenge to sign. It names no credential, so the browser offers
    /// whatever passkeys it holds for this site and the chosen one identifies its own
    /// account — which is why this page never asks who is signing in first.
    /// </summary>
    public async Task<IActionResult> OnPostPasskeyOptionsAsync()
    {
        if (options.AuthDisabled) return PasskeyProblem("This instance has no accounts to sign in to.");

        var ceremony = await passkeys.BeginAssertionAsync(HttpContext);
        if (ceremony is null) return PasskeyProblem("Could not start a passkey sign-in.");

        challenges.Store(HttpContext, PasskeyChallenge.AssertionPurpose, ceremony.State);
        return Content(ceremony.OptionsJson, "application/json");
    }

    /// <summary>
    /// Checks the signed challenge and, if it holds up, signs the owner in — the same ticket
    /// a password would have produced.
    /// </summary>
    public async Task<IActionResult> OnPostPasskeyAsync(string? returnUrl, CancellationToken ct)
    {
        if (options.AuthDisabled) return PasskeyProblem("This instance has no accounts to sign in to.");

        var state = challenges.Take(HttpContext, PasskeyChallenge.AssertionPurpose);
        if (state is null) return PasskeyProblem("That took too long. Try again.");

        JsonNode? credential;
        try
        {
            credential = await JsonSerializer.DeserializeAsync<JsonNode>(Request.Body, cancellationToken: ct);
        }
        catch (JsonException)
        {
            return PasskeyProblem("The browser sent something this server could not read.");
        }

        if (credential is null) return PasskeyProblem("The browser sent no credential.");

        var result = await passkeys.CompleteAssertionAsync(
            credential.ToJsonString(), state, HttpContext, ct);

        if (result is not PasskeySignIn.Ok(var user))
        {
            // Logged with the address for the same reason a failed password is: a run of
            // them is the signal that somebody is working on the door.
            logger.LogWarning("Failed passkey sign-in from {RemoteIp}", ClientAddress());
            return PasskeyProblem("That passkey was not accepted.");
        }

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            UserPrincipal.Create(user),
            new AuthenticationProperties { IsPersistent = true });

        logger.LogInformation(
            "User {Username} signed in with a passkey from {RemoteIp}", user.Username, ClientAddress());

        // The browser is driving this with fetch, so it navigates itself rather than
        // following a redirect it would only have to unpick.
        return new JsonResult(new { redirect = SafeReturnUrl(returnUrl) });
    }

    /// <summary>
    /// A failed ceremony, in the shape passkeys.js reads. Deliberately a 400 rather than a
    /// success carrying an error, so the script can treat any non-OK response the same way.
    /// </summary>
    private IActionResult PasskeyProblem(string reason) =>
        new JsonResult(new { error = reason }) { StatusCode = StatusCodes.Status400BadRequest };

    /// <summary>
    /// Whoever the reverse proxy said the request came from, the forwarded headers having
    /// already been applied. Without a proxy in front this is the socket's own address.
    /// </summary>
    private string ClientAddress() =>
        HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>
    /// Only local paths are honoured, so a crafted <c>returnUrl</c> cannot bounce someone
    /// to another site after they sign in.
    /// </summary>
    private string SafeReturnUrl(string? returnUrl) =>
        !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : "/";
}
