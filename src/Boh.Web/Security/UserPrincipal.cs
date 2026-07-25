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

    public static ClaimsPrincipal Create(User user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
        };

        if (user.IsAdmin) claims.Add(new Claim(ClaimTypes.Role, AdminRole));

        return new ClaimsPrincipal(
            new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    public static int? GetId(ClaimsPrincipal principal) =>
        int.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public static bool IsAdmin(ClaimsPrincipal principal) => principal.IsInRole(AdminRole);
}
