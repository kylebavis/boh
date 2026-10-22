using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Web;
using Boh.Web.Security;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Boh.Tests;

/// <summary>
/// An identity provider answering boh's back channel in-process, so a test can drive the real
/// handler through redirect, callback and token validation.
/// </summary>
public sealed class FakeOidcProvider
{
    public const string Issuer = "https://idp.test";
    public const string ClientId = "boh";

    private readonly RsaSecurityKey key = new(RSA.Create(2048)) { KeyId = "test" };

    /// <summary>Claims the next token carries besides the protocol ones.</summary>
    private List<Claim> next = [];

    private string? nonce;

    /// <summary>Added to every token from here on.</summary>
    public Claim[] ExtraClaims { get; set; } = [];

    public void Install(IServiceCollection services) =>
        services.PostConfigure<OpenIdConnectOptions>(OidcAuthentication.SchemeName, o =>
        {
            var configuration = new OpenIdConnectConfiguration
            {
                Issuer = Issuer,
                AuthorizationEndpoint = Issuer + "/authorize",
                TokenEndpoint = Issuer + "/token",
            };
            configuration.SigningKeys.Add(key);

            o.Configuration = configuration;
            o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
            o.GetClaimsFromUserInfoEndpoint = false;
            o.Backchannel = new HttpClient(new TokenEndpoint(this));
        });

    /// <summary>
    /// Runs the whole flow on <paramref name="client"/> and returns the callback's response,
    /// which redirects home on success or back to the login page on refusal.
    /// </summary>
    public async Task<HttpResponseMessage> SignInAsync(
        HttpClient client, string subject, string? username, params string[] groups)
    {
        next = [new Claim("sub", subject), .. ExtraClaims];
        if (username is not null) next.Add(new Claim("preferred_username", username));
        foreach (var group in groups) next.Add(new Claim("groups", group));

        var challenge = await client.GetAsync("/Account/Login?handler=Oidc");
        await Assert.That(challenge.StatusCode).IsEqualTo(HttpStatusCode.Redirect);

        var authorize = HttpUtility.ParseQueryString(challenge.Headers.Location!.Query);
        nonce = authorize["nonce"];

        return await client.GetAsync(
            $"/signin-oidc?code=test-code&state={Uri.EscapeDataString(authorize["state"]!)}");
    }

    /// <summary>The authorize request boh last sent, for asserting on its parameters.</summary>
    public static async Task<Uri> AuthorizeRequestAsync(HttpClient client)
    {
        var challenge = await client.GetAsync("/Account/Login?handler=Oidc");
        await Assert.That(challenge.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        return challenge.Headers.Location!;
    }

    private string IdToken() => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = Issuer,
        Audience = ClientId,
        Subject = new ClaimsIdentity([.. next, new Claim("nonce", nonce ?? "")]),
        IssuedAt = DateTime.UtcNow,
        Expires = DateTime.UtcNow.AddMinutes(5),
        SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
    });

    private sealed class TokenEndpoint(FakeOidcProvider provider) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Assert.That(request.RequestUri!.ToString()).IsEqualTo(Issuer + "/token");

            var body = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["id_token"] = provider.IdToken(),
                ["access_token"] = "test-access-token",
                ["token_type"] = "Bearer",
                ["expires_in"] = 300,
            });

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }
}
