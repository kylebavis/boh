using System.Security.Cryptography;

namespace Boh.Web.Security;

/// <summary>
/// Sets the response headers that bound what a browser will do with a boh page.
/// </summary>
/// <remarks>
/// The blob endpoints hand back bytes somebody uploaded, under a content type inferred from
/// an extension, on the same origin as the session cookie. The decoder allowlist keeps SVG
/// and HTML out of the store in the first place, so this is a second line rather than the
/// only one — but <c>nosniff</c> and a policy that refuses unmarked inline script are what
/// stop a file that got past the first from running as script here.
/// <para>
/// No HSTS: TLS terminates at a reverse proxy, so that header belongs there, and sending it
/// from here would break the plain-HTTP LAN deployments boh deliberately still supports.
/// </para>
/// </remarks>
public static class SecurityHeaders
{
    private const string NonceKey = "boh:csp-nonce";

    /// <summary>
    /// The nonce marking inline script in this response as ours. Null outside a request the
    /// middleware has run for, which in practice only happens in tests rendering a view
    /// directly — an unmarked script is the safe failure, so callers can emit it as-is.
    /// </summary>
    public static string? CspNonce(HttpContext context) => context.Items[NonceKey] as string;

    public static IApplicationBuilder UseBohSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            context.Items[NonceKey] = nonce;

            var headers = context.Response.Headers;

            // Assigned rather than appended so re-executing the pipeline through the error
            // handler cannot end up sending a header twice.
            headers["Content-Security-Policy"] = ContentSecurityPolicy(nonce);
            headers["X-Content-Type-Options"] = "nosniff";

            // frame-ancestors above covers current browsers; this is for the ones that do
            // not implement it. Note that both refuse to be framed at all — an operator
            // embedding boh in a dashboard has to relax them here.
            headers["X-Frame-Options"] = "DENY";

            // Search queries ride in the URL, and posts link out to wherever they came from.
            headers["Referrer-Policy"] = "same-origin";

            headers["X-Permitted-Cross-Domain-Policies"] = "none";

            // Explicitly off: the legacy auditor is itself a vulnerability, and every browser
            // that still honours the header does the wrong thing with any other value.
            headers["X-XSS-Protection"] = "0";

            await next();
        });

    /// <summary>
    /// Everything is served from this origin — pico, htmx and the stylesheets are all
    /// vendored — so the policy can stay at <c>'self'</c> throughout.
    /// </summary>
    /// <remarks>
    /// Two concessions. <c>style-src</c> keeps <c>'unsafe-inline'</c> for the
    /// <c>style="--tag-color: …"</c> attributes that colour tags: a nonce cannot mark an
    /// attribute, and the values are hex validated by <see cref="Tags.NamespacePalette"/>
    /// before they are stored. <c>img-src</c> allows <c>data:</c> for the form icons pico
    /// inlines into its stylesheet.
    /// </remarks>
    private static string ContentSecurityPolicy(string nonce) =>
        "default-src 'self'; " +
        $"script-src 'self' 'nonce-{nonce}'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; " +
        "media-src 'self'; " +
        "font-src 'self'; " +
        "connect-src 'self'; " +
        "form-action 'self'; " +
        "frame-ancestors 'none'; " +
        "base-uri 'self'; " +
        "object-src 'none'";
}
