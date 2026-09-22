using Boh.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Boh.Web.Security;

/// <summary>
/// Re-checks the signed-in user against the database on every request.
/// </summary>
/// <remarks>
/// Cookie authentication normally trusts the claims baked into the ticket until it expires,
/// which here lasts thirty days. That would make "remove a user" and "revoke someone's admin
/// rights" advisory rather than immediate — a deleted account would keep working, which is
/// precisely what an administrator removing someone does not expect. The lookup goes through
/// <see cref="ActiveUserCache"/>, which drops a user as soon as their row changes.
/// </remarks>
public sealed class RevalidateUserEvents : CookieAuthenticationEvents
{
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal;
        if (principal?.Identity?.IsAuthenticated != true) return;

        var userId = UserPrincipal.GetId(principal);
        if (userId is null)
        {
            await RejectAsync(context);
            return;
        }

        var services = context.HttpContext.RequestServices;
        var user = await services.GetRequiredService<ActiveUserCache>().GetAsync(userId.Value, () =>
            services.GetRequiredService<UserService>().FindByIdAsync(userId.Value, context.HttpContext.RequestAborted));

        if (user is null)
        {
            // Deleted while signed in.
            await RejectAsync(context);
            return;
        }

        // Promotion or demotion since the cookie was issued: reissue rather than reject, so
        // the change applies without forcing an otherwise valid session to sign in again.
        // Theme choices ride the same path — they are on the ticket so the layout can apply
        // them before paint, which means a change made in one tab has to reach the others.
        if (user.IsAdmin != UserPrincipal.IsAdmin(principal)
            || user.LightTheme != UserPrincipal.GetLightTheme(principal)
            || user.DarkTheme != UserPrincipal.GetDarkTheme(principal))
        {
            context.ReplacePrincipal(UserPrincipal.Create(user));
            context.ShouldRenew = true;
        }
    }

    private static async Task RejectAsync(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }
}
