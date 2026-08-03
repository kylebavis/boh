using System.Security.Claims;
using Boh.Web.Data.Entities;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Boh.Web.Security;

/// <summary>
/// Builds the signed-in identity. Login and cookie revalidation both go through here so the
/// two can never disagree about what claims a user should be carrying.
/// </summary>
public static class UserPrincipal
{
    public const string AdminRole = "admin";

    /// <summary>
    /// The chosen palette for each side of the header toggle, carried on the ticket so the
    /// layout can apply it before first paint without a query of its own. Safe to keep in the
    /// cookie because <see cref="RevalidateUserEvents"/> already reloads the row on every
    /// request and reissues when it finds a difference.
    /// </summary>
    public const string LightThemeClaim = "boh:light-theme";
    public const string DarkThemeClaim = "boh:dark-theme";

    public static ClaimsPrincipal Create(User user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
        };

        if (user.IsAdmin) claims.Add(new Claim(ClaimTypes.Role, AdminRole));

        // Absent rather than empty when unset: a missing claim reads back as null, which is
        // already the "stock Pico" value everywhere else.
        if (user.LightTheme is { Length: > 0 } light) claims.Add(new Claim(LightThemeClaim, light));
        if (user.DarkTheme is { Length: > 0 } dark) claims.Add(new Claim(DarkThemeClaim, dark));

        return new ClaimsPrincipal(
            new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    public static string? GetLightTheme(ClaimsPrincipal principal) =>
        principal.FindFirstValue(LightThemeClaim);

    public static string? GetDarkTheme(ClaimsPrincipal principal) =>
        principal.FindFirstValue(DarkThemeClaim);

    public static int? GetId(ClaimsPrincipal principal) =>
        int.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public static bool IsAdmin(ClaimsPrincipal principal) => principal.IsInRole(AdminRole);
}
