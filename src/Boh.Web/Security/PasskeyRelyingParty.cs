using Microsoft.AspNetCore.Identity;

namespace Boh.Web.Security;

/// <summary>
/// WebAuthn relying party settings. Unset, the id comes from the request host;
/// <c>BOH_PASSKEY_RP_ID</c>/<c>BOH_PASSKEY_ORIGINS</c> cover multi-hostname instances.
/// </summary>
public static class PasskeyRelyingParty
{
    public static void Configure(IdentityPasskeyOptions passkeys, BohOptions options)
    {
        // Null: derive from the request.
        passkeys.ServerDomain = options.PasskeyRpIdOverride;

        // Discoverable, since sign-in doesn't ask who is signing in.
        passkeys.ResidentKeyRequirement = "required";

        // Preferred: a PIN-less hardware key still beats a password.
        passkeys.UserVerificationRequirement = "preferred";

        var allowed = options.PasskeyOrigins;
        if (allowed.Count == 0) return;

        // Narrows the default, which also accepts subdomains.
        passkeys.ValidateOrigin = context => ValueTask.FromResult(
            !context.CrossOrigin
            && allowed.Contains(context.Origin, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether a passkey can work here: HTTPS, or origins named by the operator (the server's
    /// origin check rejects plain HTTP, even localhost).
    /// </summary>
    public static bool IsUsable(HttpRequest request, BohOptions options) =>
        request.IsHttps || options.PasskeyOrigins.Count > 0;
}
