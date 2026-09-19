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
public class LoginModel(UserService users, BohOptions options, ILogger<LoginModel> logger) : PageModel
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
