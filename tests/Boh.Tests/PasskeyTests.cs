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

    [Test]
    public async Task The_account_page_offers_a_passkey_form_pointing_at_both_handlers()
    {
        using var app = new TestApp(authMode: "password");
        var client = await app.SignInAsync();

        var form = Regex.Match(
            await app.GetHtmlAsync(client, AccountUrl), "<form[^>]*id=\"passkey-form\"[^>]*>");

        await Assert.That(form.Success).IsTrue().Because("no passkey form on the account page");

        // passkeys.js reads both URLs off the element; a renamed handler would otherwise
        // only show up as a button that silently does nothing.
        await Assert.That(form.Value).Contains("data-passkey-options=\"/Account?handler=PasskeyOptions\"");
        await Assert.That(form.Value).Contains("data-passkey-register=\"/Account?handler=Passkey\"");
    }

    [Test]
    public async Task The_login_page_offers_a_passkey_sign_in_pointing_at_both_handlers()
    {
        using var app = new TestApp(authMode: "password", passkeyOrigins: Origin);

        var form = Regex.Match(
            await app.GetHtmlAsync(app.CreateNonRedirectingClient(), LoginUrl),
            "<form[^>]*id=\"passkey-signin\"[^>]*>", RegexOptions.Singleline);

        await Assert.That(form.Success).IsTrue().Because("no passkey sign-in on the login page");
        await Assert.That(form.Value).Contains("data-passkey-options=\"/Account/Login?handler=PasskeyOptions\"");

        // No returnUrl in the query string: cookie authentication reads one there as an
        // instruction to redirect, which would replace the JSON the script is waiting for.
        await Assert.That(form.Value).Contains("data-passkey-assert=\"/Account/Login?handler=Passkey\"");
        await Assert.That(form.Value).Contains("data-passkey-return=");
    }

    /// <summary>
    /// Over plain HTTP nothing will work — ASP.NET Core's origin check turns the request away
    /// whatever the browser thinks — so the sign-in button is left out rather than offered.
    /// </summary>
    [Test]
    public async Task Over_plain_http_the_login_page_offers_no_passkey()
    {
        using var app = new TestApp(authMode: "password");
        var html = await app.GetHtmlAsync(app.CreateNonRedirectingClient(), LoginUrl);

        await Assert.That(html).DoesNotContain("id=\"passkey-signin\"");
    }

    [Test]
    public async Task Over_plain_http_the_account_page_says_why_a_passkey_will_not_work()
    {
        using var app = new TestApp(authMode: "password");
        var client = await app.SignInAsync();

        await Assert.That(await app.GetHtmlAsync(client, AccountUrl)).Contains("Passkeys need HTTPS");
    }

    /// <summary>
    /// The button is markup the script reveals, so it has to start hidden — otherwise a
    /// browser without WebAuthn is offered a control that can only fail.
    /// </summary>
    [Test]
    public async Task The_passkey_controls_start_hidden_for_the_script_to_reveal()
    {
        using var app = new TestApp(authMode: "password", passkeyOrigins: Origin);
        var client = await app.SignInAsync();

        var signin = Regex.Match(
            await app.GetHtmlAsync(client, LoginUrl), "<form[^>]*id=\"passkey-signin\"[^>]*>");
        await Assert.That(signin.Value).Contains("hidden");

        var add = Regex.Match(
            await app.GetHtmlAsync(client, AccountUrl),
            "<form[^>]*id=\"passkey-form\".*?</form>", RegexOptions.Singleline);
        await Assert.That(add.Value).Contains("<button type=\"submit\" hidden>");
    }

    /// <summary>With no accounts there is nothing to register a credential against.</summary>
    [Test]
    public async Task Without_accounts_the_account_page_offers_no_passkeys()
    {
        using var app = new TestApp();
        var html = await app.GetHtmlAsync(app.CreateNonRedirectingClient(), AccountUrl);

        await Assert.That(html).DoesNotContain("id=\"passkey-form\"");
        await Assert.That(html).DoesNotContain("<h2>Passkeys</h2>");
    }

    // ---- the first half of each ceremony -------------------------------

    [Test]
    public async Task Registration_options_name_the_account_and_ask_for_a_discoverable_key()
    {
        using var app = new TestApp(authMode: "password");
        var client = await app.SignInAsync();

        var response = await PostAsync(app, client, AccountUrl, $"{AccountUrl}?handler=PasskeyOptions");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        using var options = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current!.Execution.CancellationToken));
        var root = options.RootElement;

        await Assert.That(root.GetProperty("challenge").GetString()!).IsNotEmpty();
        await Assert.That(root.GetProperty("user").GetProperty("name").GetString()).IsEqualTo(UserService.AdminUsername);

        // Discoverable, or the sign-in page could not offer a passkey without first being
        // told whose account to look in.
        await Assert.That(root.GetProperty("authenticatorSelection").GetProperty("residentKey").GetString()).IsEqualTo("required");

        // localhost, because that is the host the request arrived on and nothing overrode it.
        await Assert.That(root.GetProperty("rp").GetProperty("id").GetString()).IsEqualTo("localhost");
    }

    /// <summary>
    /// The state between the two requests names the account the credential will land on, so
    /// it must reach the browser sealed — an editable copy would let somebody register their
    /// own authenticator against another account.
    /// </summary>
    [Test]
    public async Task The_state_kept_between_the_two_requests_never_leaves_readable()
    {
        using var app = new TestApp(authMode: "password");
        var client = await app.SignInAsync();

        var response = await PostAsync(app, client, AccountUrl, $"{AccountUrl}?handler=PasskeyOptions");

        var cookie = await Assert.That(response.Headers.GetValues("Set-Cookie")).HasSingleItem(c => c.StartsWith("boh.passkey-registration="));

        await Assert.That(cookie).Contains("httponly");
        await Assert.That(cookie).Contains("samesite=strict");

        // The challenge the browser was given appears in the options; none of it, nor the
        // account it belongs to, may be legible in the cookie.
        using var options = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current!.Execution.CancellationToken));
        await Assert.That(cookie).DoesNotContain(options.RootElement.GetProperty("challenge").GetString()!);
        await Assert.That(cookie).DoesNotContain("userId");
    }

    /// <summary>
    /// Asking for a sign-in names no credential on purpose: the browser offers what it holds,
    /// and a stranger probing the endpoint learns nothing about which accounts exist.
    /// </summary>
    [Test]
    public async Task Sign_in_options_ask_for_no_particular_credential()
    {
        using var app = new TestApp(authMode: "password");
        var client = app.CreateNonRedirectingClient();

        var response = await PostAsync(app, client, LoginUrl, $"{LoginUrl}?handler=PasskeyOptions");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        using var options = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current!.Execution.CancellationToken));
        var root = options.RootElement;

        await Assert.That(root.GetProperty("challenge").GetString()!).IsNotEmpty();

        await Assert.That(!root.TryGetProperty("allowCredentials", out var allowed) || allowed.GetArrayLength() == 0).IsTrue().Because("the sign-in options named a credential");
    }

    // ---- the second half, without the state ----------------------------

    [Test]
    public async Task A_credential_arriving_with_no_ceremony_underway_is_refused()
    {
        using var app = new TestApp(authMode: "password");
        var client = await app.SignInAsync();

        var response = await PostJsonAsync(
            app, client, AccountUrl, $"{AccountUrl}?handler=Passkey", "{\"name\":\"phone\"}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await response.Content.ReadAsStringAsync(TestContext.Current!.Execution.CancellationToken)).Contains("error");
    }

    [Test]
    public async Task A_sign_in_arriving_with_no_ceremony_underway_is_refused()
    {
        using var app = new TestApp(authMode: "password");
        var client = app.CreateNonRedirectingClient();

        var response = await PostJsonAsync(app, client, LoginUrl, $"{LoginUrl}?handler=Passkey", "{}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        // And nothing was signed in on the way past.
        await Assert.That(string.Join(" ", response.Headers.TryGetValues("Set-Cookie", out var set) ? set : [])).DoesNotContain("boh.auth=");
    }

    // ---- managing what is already registered ---------------------------

    [Test]
    public async Task A_registered_passkey_is_listed_with_its_name()
    {
        using var app = new TestApp(authMode: "password");
        var client = await app.SignInAsync();

        await AddPasskeyAsync(app, "Work laptop");

        var html = await app.GetHtmlAsync(client, AccountUrl);
        await Assert.That(html).Contains("value=\"Work laptop\"");
    }

    [Test]
    public async Task A_passkey_can_be_renamed_and_removed_from_the_account_page()
    {
        using var app = new TestApp(authMode: "password");
        var client = await app.SignInAsync();

        var passkeyId = await AddPasskeyAsync(app, "Phone");

        var renamed = await PostAsync(app, client, AccountUrl, $"{AccountUrl}?handler=PasskeyRename",
            new Dictionary<string, string> { ["passkeyId"] = passkeyId.ToString(), ["name"] = "Old phone" });
        await Assert.That(renamed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await app.GetHtmlAsync(client, AccountUrl)).Contains("value=\"Old phone\"");

        var removed = await PostAsync(app, client, AccountUrl, $"{AccountUrl}?handler=PasskeyDelete",
            new Dictionary<string, string> { ["passkeyId"] = passkeyId.ToString() });
        await Assert.That(removed.StatusCode).IsEqualTo(HttpStatusCode.OK);

        using var scope = app.Services.CreateScope();
        await Assert.That(await scope.ServiceProvider.GetRequiredService<BohDbContext>().Passkeys.ToListAsync(cancellationToken: TestContext.Current!.Execution.CancellationToken)).IsEmpty();
    }

    /// <summary>
    /// The row id is a small integer and trivially guessed, so the owner is part of every
    /// lookup rather than only of reaching the page.
    /// </summary>
    [Test]
    public async Task Someone_elses_passkey_cannot_be_removed()
    {
        using var app = new TestApp(authMode: "password");
        var client = await app.SignInAsync();

        var theirId = await AddPasskeyAsync(app, "Theirs", forAnotherUser: true);

        var response = await PostAsync(app, client, AccountUrl, $"{AccountUrl}?handler=PasskeyDelete",
            new Dictionary<string, string> { ["passkeyId"] = theirId.ToString() });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await response.Content.ReadAsStringAsync(TestContext.Current!.Execution.CancellationToken)).Contains("already gone");

        using var scope = app.Services.CreateScope();
        await Assert.That(await scope.ServiceProvider.GetRequiredService<BohDbContext>().Passkeys.ToListAsync(cancellationToken: TestContext.Current!.Execution.CancellationToken)).HasSingleItem();
    }

    /// <summary>
    /// A removed account must not leave credentials behind that still name it, which is what
    /// the cascade on the foreign key is for.
    /// </summary>
    [Test]
    public async Task Deleting_a_user_takes_their_passkeys_with_them()
    {
        using var app = new TestApp(authMode: "password");
        await app.SignInAsync();

        await AddPasskeyAsync(app, "Theirs", forAnotherUser: true);

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BohDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserService>();

        var other = await db.Users.SingleAsync(u => u.Username == OtherUsername, cancellationToken: TestContext.Current!.Execution.CancellationToken);
        await Assert.That(await users.DeleteAsync(other.Id, TestContext.Current!.Execution.CancellationToken)).IsTypeOf<UserResult.Ok>();

        await Assert.That(await db.Passkeys.ToListAsync(cancellationToken: TestContext.Current!.Execution.CancellationToken)).IsEmpty();
    }

    /// <summary>
    /// The path every real registration and sign-in takes to reach the table: the framework's
    /// UserManager, over boh's own users, through <see cref="BohUserStore"/>. Worth its own
    /// test because a UserManager does more than write — it validates the user on the way
    /// past, and a store that answered any of that wrongly would fail only at the moment
    /// somebody first tried to use a passkey.
    /// </summary>
    [Test]
    public async Task A_credential_stored_through_the_framework_round_trips()
    {
        using var app = new TestApp(authMode: "password");

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BohDbContext>();
        var identityUsers = scope.ServiceProvider.GetRequiredService<UserManager<User>>();

        var admin = await db.Users.SingleAsync(u => u.Username == UserService.AdminUsername, cancellationToken: TestContext.Current!.Execution.CancellationToken);

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

        await Assert.That(stored.Succeeded).IsTrue();

        // Read back the way the handler reads it, not straight from the table, so the
        // conversion is covered in both directions.
        var passkey = await Assert.That(await identityUsers.GetPasskeysAsync(admin)).HasSingleItem();
        await Assert.That(passkey.Name).IsEqualTo("Phone");
        await Assert.That(passkey.Transports!).IsEquivalentTo(["internal", "hybrid"], CollectionOrdering.Matching);
        await Assert.That(passkey.PublicKey).IsEquivalentTo((byte[])[1, 2, 3], CollectionOrdering.Matching);

        // And the credential leads back to its owner, which is what a sign-in that never
        // asked for a username depends on.
        var owner = await identityUsers.FindByPasskeyIdAsync(credentialId);
        await Assert.That(owner?.Id).IsEqualTo(admin.Id);

        // Using it moves the counter and the backed-up flag; storing that again is what keeps
        // the framework's clone check meaningful.
        passkey.SignCount = 7;
        passkey.IsBackedUp = true;
        await Assert.That((await identityUsers.AddOrUpdatePasskeyAsync(admin, passkey)).Succeeded).IsTrue();

        var used = await Assert.That(await identityUsers.GetPasskeysAsync(admin)).HasSingleItem();
        await Assert.That(used.SignCount).IsEqualTo(7u);
        await Assert.That(used.IsBackedUp).IsTrue();
        await Assert.That(used.Name).IsEqualTo("Phone");

        await identityUsers.RemovePasskeyAsync(admin, credentialId);
        await Assert.That(await identityUsers.GetPasskeysAsync(admin)).IsEmpty();
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
    [Test]
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
            userId = (await db.Users.SingleAsync(u => u.Username == UserService.AdminUsername, cancellationToken: TestContext.Current!.Execution.CancellationToken)).Id;

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

            await db.SaveChangesAsync(TestContext.Current!.Execution.CancellationToken);
        }

        // The options call also leaves the sealed challenge on the client, as it does in a
        // browser — the second half is only meaningful because the first half ran.
        var optionsResponse = await TestApp.PostHxAsync(
            client, $"{LoginUrl}?handler=PasskeyOptions", await app.GetHtmlAsync(client, LoginUrl));

        await Assert.That(optionsResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);

        using var options = JsonDocument.Parse(await optionsResponse.Content.ReadAsStringAsync(TestContext.Current!.Execution.CancellationToken));
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
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Headers.Location).IsNull();

        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current!.Execution.CancellationToken));
        await Assert.That(result.RootElement.GetProperty("redirect").GetString()).IsEqualTo("/");

        // The counter moved, which is what keeps the clone check meaningful, and the use
        // was recorded for the account page.
        using (var scope = app.Services.CreateScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<BohDbContext>()
                .Passkeys.SingleAsync(cancellationToken: TestContext.Current!.Execution.CancellationToken);

            await Assert.That(stored.SignCount).IsEqualTo(1u);
            await Assert.That(stored.LastUsedAt).IsNotNull();
        }

        // And the ticket is the one a password would have produced.
        await Assert.That(await app.GetHtmlAsync(client, AccountUrl)).Contains(UserService.AdminUsername);
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
            await Assert.That(await users.CreateAsync(OtherUsername, "not-the-admin", false, TestContext.Current!.Execution.CancellationToken)).IsTypeOf<UserResult.Ok>();

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
        var headers = Regex.Match(page, "hx-headers:inherited='([^']*)'");
        await Assert.That(headers.Success).IsTrue().Because("the layout rendered no hx-headers");

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
