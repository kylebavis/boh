using System.Security.Cryptography;

namespace Boh.Web.Security;

/// <summary>
/// Response headers limiting what a browser does with a page. A second line behind the
/// decoder allowlist for uploaded blobs. No HSTS: TLS is the proxy's job.
/// </summary>
public static class SecurityHeaders
{
    private const string NonceKey = "boh:csp-nonce";

    /// <summary>This response's script nonce. Null outside the middleware (view tests).</summary>
    public static string? CspNonce(HttpContext context) => context.Items[NonceKey] as string;

    public static IApplicationBuilder UseBohSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            context.Items[NonceKey] = nonce;

            var headers = context.Response.Headers;

            // Assigned, not appended: the error handler re-runs the pipeline.
            headers["Content-Security-Policy"] = ContentSecurityPolicy(nonce);
            headers["X-Content-Type-Options"] = "nosniff";

            // For browsers without frame-ancestors.
            headers["X-Frame-Options"] = "DENY";

            headers["Referrer-Policy"] = "same-origin";

            headers["X-Permitted-Cross-Domain-Policies"] = "none";

            // Off: the legacy auditor is itself a vulnerability.
            headers["X-XSS-Protection"] = "0";

            await next();
        });

    /// <summary>
    /// Everything is same-origin. <c>'unsafe-inline'</c> styles for validated tag colour
    /// attributes; <c>data:</c> images for Pico's inlined icons.
    /// </summary>
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
