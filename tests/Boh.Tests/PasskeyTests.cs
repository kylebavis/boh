using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Boh.Web.Data;
using Boh.Web.Data.Entities;
using Boh.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Boh.Tests;

/// <summary>
/// Passkeys, as far as a test without an authenticator can follow them.
/// </summary>
/// <remarks>
/// Nothing here can produce a signed assertion — that needs a real authenticator, or a
/// browser's virtual one — so what is covered is everything either side of the signature:
/// the options the server offers, the sealed state it keeps between the two halves of a
/// ceremony, the markup the scripts key off, and the management of stored credentials. The
/// signature check itself is the framework's, and is not boh's to re-test.
/// </remarks>
public class PasskeyTests
{
    private const string AccountUrl = "/Account";
    private const string LoginUrl = "/Account/Login";

    /// <summary>
    /// What the tests are served over. Naming it as an allowed origin is what lets a passkey
    /// ceremony finish here at all — see <see cref="TestApp"/>.
    /// </summary>
    private const string Origin = "http://localhost";

    // ---- what the pages offer ------------------------------------------

    [Fact]
    public async Task The_account_page_offers_a_passkey_form_pointing_at_both_handlers()
    {
        using var app = new TestApp(authMode: "password");
        var client = await app.SignInAsync();

        var form = Regex.Match(
            await app.GetHtmlAsync(client, AccountUrl), "<form[^>]*id=\"passkey-form\"[^>]*>");

        Assert.True(form.Success, "no passkey form on the account page");

        // passkeys.js reads both URLs off the element; a renamed handler would otherwise
        // only show up as a button that silently does nothing.
        Assert.Contains("data-passkey-options=\"/Account?handler=PasskeyOptions\"", form.Value);
        Assert.Contains("data-passkey-register=\"/Account?handler=Passkey\"", form.Value);
    }

    [Fact]
    public async Task The_login_page_offers_a_passkey_sign_in_pointing_at_both_handlers()
    {
        using var app = new TestApp(authMode: "password", passkeyOrigins: Origin);

        var form = Regex.Match(
            await app.GetHtmlAsync(app.CreateNonRedirectingClient(), LoginUrl),
            "<form[^>]*id=\"passkey-signin\"[^>]*>", RegexOptions.Singleline);

        Assert.True(form.Success, "no passkey sign-in on the login page");
        Assert.Contains("data-passkey-options=\"/Account/Login?handler=PasskeyOptions\"", form.Value);

        // No returnUrl in the query string: cookie authentication reads one there as an
        // instruction to redirect, which would replace the JSON the script is waiting for.
        Assert.Contains("data-passkey-assert=\"/Account/Login?handler=Passkey\"", form.Value);
        Assert.Contains("data-passkey-return=", form.Value);
    }

    /// <summary>
    /// Over plain HTTP nothing will work — ASP.NET Core's origin check turns the request away
    /// whatever the browser thinks — so the sign-in button is left out rather than offered.
    /// </summary>
    [Fact]
    public async Task Over_plain_http_the_login_page_offers_no_passkey()
    {
        using var app = new TestApp(authMode: "password");
        var html = await app.GetHtmlAsync(app.CreateNonRedirectingClient(), LoginUrl);

        Assert.DoesNotContain("id=\"passkey-signin\"", html);
    }

    [Fact]
    public async Task Over_plain_http_the_account_page_says_why_a_passkey_will_not_work()
    {
        using var app = new TestApp(authMode: "password");
        var client = await app.SignInAsync();

        Assert.Contains("Passkeys need HTTPS", await app.GetHtmlAsync(client, AccountUrl));
    }

    /// <summary>
    /// The button is markup the script reveals, so it has to start hidden — otherwise a
    /// browser without WebAuthn is offered a control that can only fail.
    /// </summary>
    [Fact]
    public async Task The_passkey_controls_start_hidden_for_the_script_to_reveal()
    {
        using var app = new TestApp(authMode: "password", passkeyOrigins: Origin);
        var client = await app.SignInAsync();

        var signin = Regex.Match(
            await app.GetHtmlAsync(client, LoginUrl), "<form[^>]*id=\"passkey-signin\"[^>]*>");
        Assert.Contains("hidden", signin.Value);

        var add = Regex.Match(
            await app.GetHtmlAsync(client, AccountUrl),
            "<form[^>]*id=\"passkey-form\".*?</form>", RegexOptions.Singleline);
        Assert.Contains("<button type=\"submit\" hidden>", add.Value);
    }

    /// <summary>With no accounts there is nothing to register a credential against.</summary>
    [Fact]
    public async Task Without_accounts_the_account_page_offers_no_passkeys()
    {
        using var app = new TestApp();
        var html = await app.GetHtmlAsync(app.CreateNonRedirectingClient(), AccountUrl);

        Assert.DoesNotContain("id=\"passkey-form\"", html);
        Assert.DoesNotContain("<h2>Passkeys</h2>", html);
    }

    // ---- the first half of each ceremony -------------------------------

    [Fact]
    public async Task Registration_options_name_the_account_and_ask_for_a_discoverable_key()
    {
        using var app = new TestApp(authMode: "password");
        var client = await app.SignInAsync();

        var response = await PostAsync(app, client, AccountUrl, $"{AccountUrl}?handler=PasskeyOptions");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var options = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = options.RootElement;

        Assert.NotEmpty(root.GetProperty("challenge").GetString()!);
        Assert.Equal(UserService.AdminUsername, root.GetProperty("user").GetProperty("name").GetString());

        // Discoverable, or the sign-in page could not offer a passkey without first being
        // told whose account to look in.
        Assert.Equal("required",
            root.GetProperty("authenticatorSelection").GetProperty("residentKey").GetString());

        // localhost, because that is the host the request arrived on and nothing overrode it.
        Assert.Equal("localhost", root.GetProperty("rp").GetProperty("id").GetString());
    }

    /// <summary>
    /// The state between the two requests names the account the credential will land on, so
    /// it must reach the browser sealed — an editable copy would let somebody register their
    /// own authenticator against another account.
    /// </summary>
    [Fact]
    public async Task The_state_kept_between_the_two_requests_never_leaves_readable()
    {
        using var app = new TestApp(authMode: "password");
        var client = await app.SignInAsync();

        var response = await PostAsync(app, client, AccountUrl, $"{AccountUrl}?handler=PasskeyOptions");

        var cookie = Assert.Single(
            response.Headers.GetValues("Set-Cookie"),
            c => c.StartsWith("boh.passkey-registration="));

        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);

        // The challenge the browser was given appears in the options; none of it, nor the
        // account it belongs to, may be legible in the cookie.
        using var options = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(options.RootElement.GetProperty("challenge").GetString()!, cookie);
        Assert.DoesNotContain("userId", cookie, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Asking for a sign-in names no credential on purpose: the browser offers what it holds,
    /// and a stranger probing the endpoint learns nothing about which accounts exist.
    /// </summary>
    [Fact]
    public async Task Sign_in_options_ask_for_no_particular_credential()
    {
        using var app = new TestApp(authMode: "password");
        var client = app.CreateNonRedirectingClient();

        var response = await PostAsync(app, client, LoginUrl, $"{LoginUrl}?handler=PasskeyOptions");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var options = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = options.RootElement;

        Assert.NotEmpty(root.GetProperty("challenge").GetString()!);

        Assert.True(
            !root.TryGetProperty("allowCredentials", out var allowed) || allowed.GetArrayLength() == 0,
            "the sign-in options named a credential");
    }

    // ---- the second half, without the state ----------------------------

    [Fact]
    public async Task A_credential_arriving_with_no_ceremony_underway_is_refused()
    {
        using var app = new TestApp(authMode: "password");
        var client = await app.SignInAsync();

        var response = await PostJsonAsync(
            app, client, AccountUrl, $"{AccountUrl}?handler=Passkey", "{\"name\":\"phone\"}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("error", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_sign_in_arriving_with_no_ceremony_underway_is_refused()
    {
        using var app = new TestApp(authMode: "password");
        var client = app.CreateNonRedirectingClient();

        var response = await PostJsonAsync(app, client, LoginUrl, $"{LoginUrl}?handler=Passkey", "{}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // And nothing was signed in on the way past.
        Assert.DoesNotContain("boh.auth=", string.Join(" ", response.Headers.TryGetValues("Set-Cookie", out var set) ? set : []));
    }

    // ---- managing what is already registered ---------------------------

    [Fact]
    public async Task A_registered_passkey_is_listed_with_its_name()
    {
        using var app = new TestApp(authMode: "password");
        var client = await app.SignInAsync();

        await AddPasskeyAsync(app, "Work laptop");

        var html = await app.GetHtmlAsync(client, AccountUrl);
        Assert.Contains("value=\"Work laptop\"", html);
    }

    [Fact]
    public async Task A_passkey_can_be_renamed_and_removed_from_the_account_page()
    {
        using var app = new TestApp(authMode: "password");
        var client = await app.SignInAsync();

        var passkeyId = await AddPasskeyAsync(app, "Phone");

        var renamed = await PostAsync(app, client, AccountUrl, $"{AccountUrl}?handler=PasskeyRename",
            new Dictionary<string, string> { ["passkeyId"] = passkeyId.ToString(), ["name"] = "Old phone" });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Contains("value=\"Old phone\"", await app.GetHtmlAsync(client, AccountUrl));

        var removed = await PostAsync(app, client, AccountUrl, $"{AccountUrl}?handler=PasskeyDelete",
            new Dictionary<string, string> { ["passkeyId"] = passkeyId.ToString() });
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);

        using var scope = app.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<BohDbContext>().Passkeys.ToListAsync());
    }

    /// <summary>
    /// The row id is a small integer and trivially guessed, so the owner is part of every
    /// lookup rather than only of reaching the page.
    /// </summary>
    [Fact]
    public async Task Someone_elses_passkey_cannot_be_removed()
    {
        using var app = new TestApp(authMode: "password");
        var client = await app.SignInAsync();

        var theirId = await AddPasskeyAsync(app, "Theirs", forAnotherUser: true);

        var response = await PostAsync(app, client, AccountUrl, $"{AccountUrl}?handler=PasskeyDelete",
            new Dictionary<string, string> { ["passkeyId"] = theirId.ToString() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("already gone", await response.Content.ReadAsStringAsync());

        using var scope = app.Services.CreateScope();
        Assert.Single(await scope.ServiceProvider.GetRequiredService<BohDbContext>().Passkeys.ToListAsync());
    }

    /// <summary>
    /// A removed account must not leave credentials behind that still name it, which is what
    /// the cascade on the foreign key is for.
    /// </summary>
    [Fact]
    public async Task Deleting_a_user_takes_their_passkeys_with_them()
    {
        using var app = new TestApp(authMode: "password");
        await app.SignInAsync();

        await AddPasskeyAsync(app, "Theirs", forAnotherUser: true);

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BohDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserService>();

        var other = await db.Users.SingleAsync(u => u.Username == OtherUsername);
        Assert.IsType<UserResult.Ok>(await users.DeleteAsync(other.Id, CancellationToken.None));

        Assert.Empty(await db.Passkeys.ToListAsync());
    }

    /// <summary>
    /// The path every real registration and sign-in takes to reach the table: the framework's
    /// UserManager, over boh's own users, through <see cref="BohUserStore"/>. Worth its own
    /// test because a UserManager does more than write — it validates the user on the way
    /// past, and a store that answered any of that wrongly would fail only at the moment
    /// somebody first tried to use a passkey.
    /// </summary>
    [Fact]
    public async Task A_credential_stored_through_the_framework_round_trips()
    {
        using var app = new TestApp(authMode: "password");

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BohDbContext>();
        var identityUsers = scope.ServiceProvider.GetRequiredService<UserManager<User>>();

        var admin = await db.Users.SingleAsync(u => u.Username == UserService.AdminUsername);

        var credentialId = Encoding.UTF8.GetBytes("credential");
        var stored = await identityUsers.AddOrUpdatePasskeyAsync(admin, new UserPasskeyInfo(
            credentialId,
            publicKey: [1, 2, 3],
            createdAt: DateTimeOffset.UtcNow,
            signCount: 0,
            transports: ["internal", "hybrid"],
            isUserVerified: true,
            isBackupEligible: true,
            isBackedUp: false,
            attestationObject: [4, 5],
            clientDataJson: [6, 7]) { Name = "Phone" });

        Assert.True(stored.Succeeded, string.Join("; ", stored.Errors.Select(e => e.Description)));

        // Read back the way the handler reads it, not straight from the table, so the
        // conversion is covered in both directions.
        var passkey = Assert.Single(await identityUsers.GetPasskeysAsync(admin));
        Assert.Equal("Phone", passkey.Name);
        Assert.Equal(["internal", "hybrid"], passkey.Transports!);
        Assert.Equal<byte[]>([1, 2, 3], passkey.PublicKey);

        // And the credential leads back to its owner, which is what a sign-in that never
        // asked for a username depends on.
        var owner = await identityUsers.FindByPasskeyIdAsync(credentialId);
        Assert.Equal(admin.Id, owner?.Id);

        // Using it moves the counter and the backed-up flag; storing that again is what keeps
        // the framework's clone check meaningful.
        passkey.SignCount = 7;
        passkey.IsBackedUp = true;
        Assert.True((await identityUsers.AddOrUpdatePasskeyAsync(admin, passkey)).Succeeded);

        var used = Assert.Single(await identityUsers.GetPasskeysAsync(admin));
        Assert.Equal(7u, used.SignCount);
        Assert.True(used.IsBackedUp);
        Assert.Equal("Phone", used.Name);

        await identityUsers.RemovePasskeyAsync(admin, credentialId);
        Assert.Empty(await identityUsers.GetPasskeysAsync(admin));
    }

    /// <summary>
    /// The one path nothing else can reach: an assertion that actually verifies.
    /// </summary>
    /// <remarks>
    /// Everything past the signature — recording the counter, writing the auth cookie,
    /// answering with somewhere to go — is unreachable without a real one, so this test is
    /// its own authenticator. It holds the private key, registers the matching public key,
    /// and signs the challenge the server issued.
    /// </remarks>
    [Fact]
    public async Task A_passkey_that_verifies_signs_its_owner_in()
    {
        using var app = new TestApp(authMode: "password", passkeyOrigins: Origin);
        var client = app.CreateNonRedirectingClient();

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var credentialId = Encoding.UTF8.GetBytes("a-registered-credential");

        int userId;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BohDbContext>();
            userId = (await db.Users.SingleAsync(u => u.Username == UserService.AdminUsername)).Id;

            db.Passkeys.Add(new Passkey
            {
                UserId = userId,
                CredentialId = credentialId,
                PublicKey = CoseKey(key),
                Name = "Test key",
                Transports = "internal",
                AttestationObject = [1],
                ClientDataJson = [1],
                CreatedAt = DateTimeOffset.UtcNow,
            });

            await db.SaveChangesAsync();
        }

        // The options call also leaves the sealed challenge on the client, as it does in a
        // browser — the second half is only meaningful because the first half ran.
        var optionsResponse = await TestApp.PostHxAsync(
            client, $"{LoginUrl}?handler=PasskeyOptions", await app.GetHtmlAsync(client, LoginUrl));

        Assert.Equal(HttpStatusCode.OK, optionsResponse.StatusCode);

        using var options = JsonDocument.Parse(await optionsResponse.Content.ReadAsStringAsync());
        var challenge = options.RootElement.GetProperty("challenge").GetString()!;

        var clientData = Encoding.UTF8.GetBytes(
            $$"""{"type":"webauthn.get","challenge":"{{challenge}}","origin":"http://localhost","crossOrigin":false}""");

        // 32 bytes of relying party hash, one of flags, four of counter.
        var authenticatorData = new byte[37];
        SHA256.HashData("localhost"u8.ToArray()).CopyTo(authenticatorData, 0);
        authenticatorData[32] = 0x05;   // user present, user verified
        authenticatorData[36] = 1;      // one use, where the stored count is none

        // What WebAuthn signs: the authenticator data, then the hash of the client data.
        var signed = new byte[authenticatorData.Length + 32];
        authenticatorData.CopyTo(signed, 0);
        SHA256.HashData(clientData).CopyTo(signed, authenticatorData.Length);

        var credential = JsonSerializer.Serialize(new
        {
            id = Base64Url.EncodeToString(credentialId),
            rawId = Base64Url.EncodeToString(credentialId),
            type = "public-key",
            authenticatorAttachment = "platform",
            clientExtensionResults = new { },
            response = new
            {
                clientDataJSON = Base64Url.EncodeToString(clientData),
                authenticatorData = Base64Url.EncodeToString(authenticatorData),
                signature = Base64Url.EncodeToString(
                    key.SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)),
                userHandle = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(userId.ToString())),
            },
        });

        var response = await PostJsonAsync(
            app, client, LoginUrl, $"{LoginUrl}?handler=Passkey",
            JsonSerializer.Serialize(new { returnUrl = "/", credential = JsonNode.Parse(credential) }));

        // A non-redirecting client on purpose. Signing in used to answer with a 302 rather
        // than this JSON, because cookie authentication turns a ReturnUrl in the query string
        // into a redirect — and a client that follows redirects hides that by fetching the
        // page it points at and returning a perfectly good 200.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Headers.Location);

        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("/", result.RootElement.GetProperty("redirect").GetString());

        // The counter moved, which is what keeps the clone check meaningful, and the use
        // was recorded for the account page.
        using (var scope = app.Services.CreateScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<BohDbContext>()
                .Passkeys.SingleAsync();

            Assert.Equal(1u, stored.SignCount);
            Assert.NotNull(stored.LastUsedAt);
        }

        // And the ticket is the one a password would have produced.
        Assert.Contains(UserService.AdminUsername, await app.GetHtmlAsync(client, AccountUrl));
    }

    // ---- helpers -------------------------------------------------------

    /// <summary>
    /// An ES256 public key in the COSE form an authenticator hands over: a CBOR map of key
    /// type, algorithm, curve and the two coordinates.
    /// </summary>
    private static byte[] CoseKey(ECDsa key)
    {
        var parameters = key.ExportParameters(false);

        List<byte> cose = [0xA5, 0x01, 0x02, 0x03, 0x26, 0x20, 0x01, 0x21, 0x58, 0x20];
        cose.AddRange(parameters.Q.X!);
        cose.AddRange([0x22, 0x58, 0x20]);
        cose.AddRange(parameters.Q.Y!);

        return [.. cose];
    }

    private const string OtherUsername = "someone-else";

    /// <summary>
    /// Writes a credential straight into the table. Registering one for real needs an
    /// authenticator; everything downstream of that only cares that the row is there.
    /// </summary>
    private static async Task<int> AddPasskeyAsync(TestApp app, string name, bool forAnotherUser = false)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BohDbContext>();

        int userId;
        if (forAnotherUser)
        {
            var users = scope.ServiceProvider.GetRequiredService<UserService>();
            Assert.IsType<UserResult.Ok>(
                await users.CreateAsync(OtherUsername, "not-the-admin", false, CancellationToken.None));

            userId = (await db.Users.SingleAsync(u => u.Username == OtherUsername)).Id;
        }
        else
        {
            userId = (await db.Users.SingleAsync(u => u.Username == UserService.AdminUsername)).Id;
        }

        var passkey = new Passkey
        {
            UserId = userId,
            CredentialId = Encoding.UTF8.GetBytes($"credential-{name}"),
            PublicKey = [1, 2, 3],
            Name = name,
            Transports = "internal",
            CreatedAt = DateTimeOffset.UtcNow,
        };

        db.Passkeys.Add(passkey);
        await db.SaveChangesAsync();

        return passkey.Id;
    }

    /// <summary>
    /// Posts the way the scripts do, carrying the antiforgery token the layout hands htmx.
    /// </summary>
    private static async Task<HttpResponseMessage> PostAsync(
        TestApp app, HttpClient client, string pageUrl, string url,
        Dictionary<string, string>? fields = null) =>
        await TestApp.PostHxAsync(client, url, await app.GetHtmlAsync(client, pageUrl), fields);

    private static async Task<HttpResponseMessage> PostJsonAsync(
        TestApp app, HttpClient client, string pageUrl, string url, string json)
    {
        var page = await app.GetHtmlAsync(client, pageUrl);
        var headers = Regex.Match(page, "hx-headers='([^']*)'");
        Assert.True(headers.Success, "the layout rendered no hx-headers");

        using var token = JsonDocument.Parse(WebUtility.HtmlDecode(headers.Groups[1].Value));

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        request.Headers.Add(
            "RequestVerificationToken",
            token.RootElement.GetProperty("RequestVerificationToken").GetString());

        return await client.SendAsync(request);
    }
}
