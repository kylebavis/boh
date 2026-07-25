using Boh.Web.Security;
using Boh.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Boh.Web.Pages.Account;

[AllowAnonymous]
public class LoginModel(UserService users, BohOptions options) : PageModel
{
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
            Error = "Incorrect username or password.";
            return Page();
        }

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            UserPrincipal.Create(user),
            new AuthenticationProperties { IsPersistent = true });

        return Redirect(target);
    }

    /// <summary>
    /// Only local paths are honoured, so a crafted <c>returnUrl</c> cannot bounce someone
    /// to another site after they sign in.
    /// </summary>
    private string SafeReturnUrl(string? returnUrl) =>
        !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : "/";
}
