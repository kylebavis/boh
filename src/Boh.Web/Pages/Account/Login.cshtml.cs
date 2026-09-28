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
    /// <summary>Limiter policy name, configured in <c>Program.cs</c>.</summary>
    public const string RateLimitPolicy = "login";

    public const int RateLimitAttempts = 10;

    public static readonly TimeSpan RateLimitWindow = TimeSpan.FromMinutes(5);

    [BindProperty] public string? Username { get; set; }
    [BindProperty] public string? Password { get; set; }

    public string? Error { get; private set; }

    /// <summary>When false the passkey button is omitted.</summary>
    public bool PasskeysUsable => PasskeyRelyingParty.IsUsable(Request, options);

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
            // Warning with address: repeated failures signal guessing. The name is user input.
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

    /// <summary>A challenge naming no credential; the chosen passkey identifies the account.</summary>
    public async Task<IActionResult> OnPostPasskeyOptionsAsync()
    {
        if (options.AuthDisabled) return PasskeyProblem("This instance has no accounts to sign in to.");

        var ceremony = await passkeys.BeginAssertionAsync(HttpContext);
        if (ceremony is null) return PasskeyProblem("Could not start a passkey sign-in.");

        challenges.Store(HttpContext, PasskeyChallenge.AssertionPurpose, ceremony.State);
        return Content(ceremony.OptionsJson, "application/json");
    }

    /// <summary>
    /// Verifies the signed challenge and signs in. The return URL travels in the body: a
    /// <c>ReturnUrl</c> query parameter makes cookie auth redirect, breaking the JSON reply.
    /// </summary>
    public async Task<IActionResult> OnPostPasskeyAsync(CancellationToken ct)
    {
        if (options.AuthDisabled) return PasskeyProblem("This instance has no accounts to sign in to.");

        var state = challenges.Take(HttpContext, PasskeyChallenge.AssertionPurpose);
        if (state is null) return PasskeyProblem("That took too long. Try again.");

        SignInWithPasskey? posted;
        try
        {
            posted = await JsonSerializer.DeserializeAsync<SignInWithPasskey>(Request.Body, JsonOptions, ct);
        }
        catch (JsonException)
        {
            return PasskeyProblem("The browser sent something this server could not read.");
        }

        if (posted?.Credential is null) return PasskeyProblem("The browser sent no credential.");

        var result = await passkeys.CompleteAssertionAsync(
            posted.Credential.ToJsonString(), state, HttpContext, ct);

        if (result is not PasskeySignIn.Ok(var user))
        {
            logger.LogWarning("Failed passkey sign-in from {RemoteIp}", ClientAddress());
            return PasskeyProblem("That passkey was not accepted.");
        }

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            UserPrincipal.Create(user),
            new AuthenticationProperties { IsPersistent = true });

        logger.LogInformation(
            "User {Username} signed in with a passkey from {RemoteIp}", user.Username, ClientAddress());

        // fetch drives this, so the script navigates itself.
        return new JsonResult(new { redirect = SafeReturnUrl(posted.ReturnUrl) });
    }

    /// <summary>What the browser posts once the authenticator has signed the challenge.</summary>
    private sealed record SignInWithPasskey(string? ReturnUrl, JsonNode? Credential);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>A 400 in the shape passkeys.js reads.</summary>
    private IActionResult PasskeyProblem(string reason) =>
        new JsonResult(new { error = reason }) { StatusCode = StatusCodes.Status400BadRequest };

    /// <summary>Client address after forwarded headers.</summary>
    private string ClientAddress() =>
        HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>Local paths only, so returnUrl can't redirect off-site.</summary>
    private string SafeReturnUrl(string? returnUrl) =>
        !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : "/";
}
