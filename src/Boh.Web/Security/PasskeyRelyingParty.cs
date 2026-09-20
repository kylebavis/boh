using Microsoft.AspNetCore.Identity;

namespace Boh.Web.Security;

/// <summary>
/// Settles who this instance claims to be when it talks to an authenticator.
/// </summary>
/// <remarks>
/// WebAuthn binds every credential to a relying party id and refuses an assertion whose
/// origin does not match, which is what makes a passkey unphishable — and what makes this
/// the one part a self-hosted application cannot hard-code. boh does not know its own
/// address: it is reached at whatever hostname the operator put in front of it. Left unset,
/// ASP.NET Core takes the id from the request's own host, which is right for the usual
/// deployment; <c>BOH_PASSKEY_RP_ID</c> and <c>BOH_PASSKEY_ORIGINS</c> are for the one that
/// answers on several names.
/// <para>
/// This depends on the Host header being trustworthy. Behind a proxy that is the proxy's job,
/// and boh already takes the forwarded host and scheme from it; reached directly, the caller
/// can say anything — but a forged host only ever scopes a credential to a domain the forger
/// already controls, and boh's own cookie is bound to the real one.
/// </para>
/// </remarks>
public static class PasskeyRelyingParty
{
    public static void Configure(IdentityPasskeyOptions passkeys, BohOptions options)
    {
        // Null is the framework's "work it out from the request", which is what an instance
        // on one hostname wants.
        passkeys.ServerDomain = options.PasskeyRpIdOverride;

        // Discoverable, because the sign-in page asks for a passkey without asking who is
        // signing in — the credential has to be able to name its own account.
        passkeys.ResidentKeyRequirement = "required";

        // Preferred rather than required: a hardware key with no PIN or biometric is still a
        // considerable improvement on a password, and refusing it would send that user back
        // to the password field for nothing.
        passkeys.UserVerificationRequirement = "preferred";

        var allowed = options.PasskeyOrigins;
        if (allowed.Count == 0) return;

        // Named explicitly, so the default — which also accepts subdomains of the request's
        // own host — is narrowed to the list the operator gave.
        passkeys.ValidateOrigin = context => ValueTask.FromResult(
            !context.CrossOrigin
            && allowed.Contains(context.Origin, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether a browser will let this page use WebAuthn at all. It refuses outside a secure
    /// context, and localhost is the one exception — so a plain-HTTP instance on a LAN can
    /// offer passkeys and have every attempt fail, which is worth saying on the page instead.
    /// </summary>
    public static bool IsSecureContext(HttpRequest request) =>
        request.IsHttps
        || request.Host.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || request.Host.Host is "127.0.0.1" or "[::1]" or "::1";
}
