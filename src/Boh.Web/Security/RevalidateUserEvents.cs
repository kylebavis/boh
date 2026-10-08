using Boh.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Boh.Web.Security;

/// <summary>Re-checks the user each request, so deletion and demotion apply immediately.</summary>
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

        // Admin or theme changes: reissue rather than reject.
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
