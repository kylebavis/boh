using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Boh.Web.Security;

/// <summary>
/// Holds the state of a passkey ceremony between the request that starts it and the one that
/// finishes it.
/// </summary>
/// <remarks>
/// Registering or using a passkey takes two requests — options out, credential back — and the
/// server has to remember the challenge it issued in between. For a registration that state
/// also names the account the new credential will belong to, and the framework hands it over
/// as plain JSON with no integrity protection of its own: anyone who could edit it could put
/// their own authenticator on somebody else's account. So it never reaches the browser except
/// sealed with the data protection keys, under a purpose that a registration challenge and a
/// sign-in challenge do not share, and with an expiry baked into the payload rather than
/// merely into the cookie.
/// <para>
/// A cookie rather than server memory: this is single-use state belonging to one browser for
/// the seconds a ceremony takes, and keeping it out of process means a restart mid-ceremony
/// costs one retry instead of being a class of bug.
/// </para>
/// </remarks>
public sealed class PasskeyChallenge(IDataProtectionProvider provider)
{
    /// <summary>
    /// Long enough for someone to fetch the phone the passkey is on, short enough that a
    /// ceremony left open on a shared machine is not worth anything for long.
    /// </summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    public const string RegistrationPurpose = "registration";
    public const string AssertionPurpose = "assertion";

    /// <summary>
    /// Scoped to the account pages, which is everywhere a ceremony runs. A cookie sent with
    /// every gallery request would be paid for on every page for no reason.
    /// </summary>
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
                // Strict: every ceremony starts and finishes inside this site, so there is no
                // navigation from elsewhere that needs to carry it.
                SameSite = SameSiteMode.Strict,
                // As elsewhere in boh, so a plain-HTTP LAN instance keeps working while an
                // HTTPS one still gets the flag.
                Secure = context.Request.IsHttps,
                Path = CookiePath,
                Expires = expires,
                IsEssential = true,
            });
    }

    /// <summary>
    /// Reads the pending state and clears it, so one ceremony can only be answered once.
    /// Null when there is none, it has expired, or it was issued for another purpose.
    /// </summary>
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
            // Expired, tampered with, or sealed under keys this instance no longer has. All
            // of them mean the same thing to the caller: start again.
            return null;
        }
    }

    private static string CookieName(string purpose) => $"boh.passkey-{purpose}";

    private ITimeLimitedDataProtector Protector(string purpose) =>
        provider.CreateProtector("boh.passkey", purpose).ToTimeLimitedDataProtector();
}
