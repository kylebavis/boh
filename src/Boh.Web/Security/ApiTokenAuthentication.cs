using System.Text.Encodings.Web;
using Boh.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Boh.Web.Security;

/// <summary>
/// Authenticates <c>Authorization: Bearer boh_…</c>. The API accepts only this, never the
/// cookie, so it can't be driven cross-site.
/// </summary>
public sealed class ApiTokenAuthentication(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ApiTokenService tokens) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiToken";

    private const string BearerPrefix = "Bearer ";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();

        var user = await tokens.AuthenticateAsync(header[BearerPrefix.Length..].Trim(), Context.RequestAborted);
        if (user is null) return AuthenticateResult.Fail("Unknown or revoked token.");

        var principal = UserPrincipal.Create(user, SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }
}
