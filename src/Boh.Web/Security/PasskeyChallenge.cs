using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Boh.Web.Security;

/// <summary>
/// Ceremony state between its two requests, in a cookie sealed with data protection. It
/// names the account a new credential joins, so it must be tamper-proof; purpose and expiry
/// are sealed in.
/// </summary>
public sealed class PasskeyChallenge(IDataProtectionProvider provider)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    public const string RegistrationPurpose = "registration";
    public const string AssertionPurpose = "assertion";

    /// <summary>Only the account pages run ceremonies.</summary>
    private const string CookiePath = "/Account";

    public void Store(HttpContext context, string purpose, string state)
    {
        var expires = DateTimeOffset.UtcNow + Lifetime;

        context.Response.Cookies.Append(
            CookieName(purpose),
            Protector(purpose).Protect(state, expires),
            new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                // Plain-HTTP LAN instances keep working.
                Secure = context.Request.IsHttps,
                Path = CookiePath,
                Expires = expires,
                IsEssential = true,
            });
    }

    /// <summary>Reads and clears the state, so it answers once. Null if missing, expired or wrong purpose.</summary>
    public string? Take(HttpContext context, string purpose)
    {
        var name = CookieName(purpose);
        if (!context.Request.Cookies.TryGetValue(name, out var sealedState)) return null;

        context.Response.Cookies.Delete(name, new CookieOptions { Path = CookiePath });

        try
        {
            return Protector(purpose).Unprotect(sealedState);
        }
        catch (CryptographicException)
        {
            // Expired, tampered or unreadable: start again.
            return null;
        }
    }

    private static string CookieName(string purpose) => $"boh.passkey-{purpose}";

    private ITimeLimitedDataProtector Protector(string purpose) =>
        provider.CreateProtector("boh.passkey", purpose).ToTimeLimitedDataProtector();
}
