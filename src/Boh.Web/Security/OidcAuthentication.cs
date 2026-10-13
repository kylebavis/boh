using Boh.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Boh.Web.Security;

/// <summary>
/// Sign-in through an OpenID Connect provider. The provider's identity is swapped for a boh
/// user at token validation, so the cookie carries the same principal a password sign-in does.
/// </summary>
public static class OidcAuthentication
{
    public const string SchemeName = OpenIdConnectDefaults.AuthenticationScheme;

    /// <summary>Login page query value telling the visitor sign-in failed.</summary>
    public const string FailedError = "oidc";

    public static void Configure(OpenIdConnectOptions o, BohOptions options)
    {
        o.Authority = options.OidcAuthority;
        o.ClientId = options.OidcClientId;
        o.ClientSecret = options.OidcClientSecret;
        o.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;

        // An http:// authority is the operator's explicit choice, e.g. a LAN-only provider.
        o.RequireHttpsMetadata = options.OidcAuthority?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) != false;

        o.ResponseType = OpenIdConnectResponseType.Code;
        o.UsePkce = true;

        // Query rather than form_post: the callback is then a top-level GET, so Lax cookies
        // survive it and plain-HTTP LAN instances keep working.
        o.ResponseMode = OpenIdConnectResponseMode.Query;
        o.CorrelationCookie.SameSite = SameSiteMode.Lax;
        o.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.NonceCookie.SameSite = SameSiteMode.Lax;
        o.NonceCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;

        o.Scope.Clear();
        foreach (var scope in options.OidcScopeList) o.Scope.Add(scope);

        // Raw claim names, so the configured username and groups claims match as written.
        o.MapInboundClaims = false;
        o.GetClaimsFromUserInfoEndpoint = true;
        o.SaveTokens = false;

        o.Events.OnTokenValidated = OnTokenValidated;
        o.Events.OnRemoteFailure = OnRemoteFailure;
    }

    private static async Task OnTokenValidated(TokenValidatedContext context)
    {
        var services = context.HttpContext.RequestServices;
        var options = services.GetRequiredService<BohOptions>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(OidcAuthentication));
        var identity = context.Principal!;

        var subject = identity.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(subject))
        {
            Deny(context, logger, "the token carried no subject");
            return;
        }

        var groups = identity.FindAll(options.OidcGroupsClaim).Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
        bool? isAdmin = options.OidcAdminGroup is { } adminGroup ? groups.Contains(adminGroup) : null;

        if (options.OidcUserGroup is { } userGroup && !groups.Contains(userGroup) && isAdmin != true)
        {
            Deny(context, logger, $"subject {subject} is not in {userGroup}");
            return;
        }

        var username = identity.FindFirst(options.OidcUsernameClaim)?.Value;
        var result = await services.GetRequiredService<UserService>()
            .SignInWithOidcAsync(subject, username, isAdmin, context.HttpContext.RequestAborted);

        if (result is not OidcSignIn.Ok(var user))
        {
            Deny(context, logger, ((OidcSignIn.Rejected)result).Reason);
            return;
        }

        context.Principal = UserPrincipal.Create(user);
        context.Properties!.IsPersistent = true;

        logger.LogInformation(
            "User {Username} signed in through OIDC from {RemoteIp}",
            user.Username, context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    }

    private static Task OnRemoteFailure(RemoteFailureContext context)
    {
        var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(OidcAuthentication));
        logger.LogWarning(context.Failure, "OIDC sign-in failed");

        context.Response.Redirect($"/Account/Login?error={FailedError}");
        context.HandleResponse();
        return Task.CompletedTask;
    }

    private static void Deny(TokenValidatedContext context, ILogger logger, string reason)
    {
        logger.LogWarning("OIDC sign-in refused: {Reason}", LogSafe.Value(reason));
        context.Response.Redirect($"/Account/Login?error={FailedError}");
        context.HandleResponse();
    }
}
