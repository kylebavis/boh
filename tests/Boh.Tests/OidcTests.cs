using System.Net;
using System.Security.Claims;
using System.Web;
using Boh.Web.Data;
using Boh.Web.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Boh.Tests;

public class OidcTests
{
    private static CancellationToken Ct => TestContext.Current!.Execution.CancellationToken;

    private static readonly Dictionary<string, string?> LocalOff = new() { ["BOH_LOCAL_AUTH"] = "false" };

    private static TestApp App(FakeOidcProvider idp, Dictionary<string, string?>? settings = null) =>
        new(authMode: "password", oidc: idp, settings: settings);

    private static HttpClient Client(TestApp app) =>
        app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static async Task<Boh.Web.Data.Entities.User?> FindAsync(TestApp app, string username)
    {
        using var scope = app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<BohDbContext>().Users
            .AsNoTracking().SingleOrDefaultAsync(u => u.Username == username, Ct);
    }

    private static async Task AssertRefused(HttpResponseMessage response)
    {
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(response.Headers.Location!.OriginalString).IsEqualTo("/Account/Login?error=oidc");
    }

    // ---- the flow ------------------------------------------------------

    [Test]
    public async Task The_authorize_request_uses_the_code_flow_with_pkce()
    {
        var idp = new FakeOidcProvider();
        using var app = App(idp);

        var location = await FakeOidcProvider.AuthorizeRequestAsync(Client(app));
        var query = HttpUtility.ParseQueryString(location.Query);

        await Assert.That(location.ToString()).StartsWith(FakeOidcProvider.Issuer + "/authorize");
        await Assert.That(query["response_type"]).IsEqualTo("code");
        await Assert.That(query["response_mode"]).IsNull(); // query, the code flow default
        await Assert.That(query["code_challenge_method"]).IsEqualTo("S256");
        await Assert.That(query["redirect_uri"]).EndsWith("/signin-oidc");
    }

    [Test]
    public async Task A_first_sign_in_creates_a_passwordless_account_and_signs_it_in()
    {
        var idp = new FakeOidcProvider();
        using var app = App(idp);
        var client = Client(app);

        var response = await idp.SignInAsync(client, "sub-1", "Kim");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(response.Headers.Location!.OriginalString).IsEqualTo("/");

        var user = await FindAsync(app, "kim");
        await Assert.That(user).IsNotNull();
        await Assert.That(user!.OidcSubject).IsEqualTo($"{FakeOidcProvider.Issuer} sub-1");
        await Assert.That(user.PasswordHash).IsEqualTo("");
        await Assert.That(user.IsAdmin).IsFalse();

        var account = await app.GetHtmlAsync(client, "/Account");
        await Assert.That(account).Contains("Signed in as <strong>kim</strong>");
        await Assert.That(account).Contains("has no password");
        await Assert.That(account).DoesNotContain("name=\"currentPassword\"");
    }

    [Test]
    public async Task A_returning_subject_keeps_its_account_when_the_username_changes()
    {
        var idp = new FakeOidcProvider();
        using var app = App(idp);

        await idp.SignInAsync(Client(app), "sub-1", "kim");
        var id = (await FindAsync(app, "kim"))!.Id;

        var response = await idp.SignInAsync(Client(app), "sub-1", "kimberly");

        await Assert.That(response.Headers.Location!.OriginalString).IsEqualTo("/");
        await Assert.That((await FindAsync(app, "kim"))!.Id).IsEqualTo(id);
        await Assert.That(await FindAsync(app, "kimberly")).IsNull();
    }

    [Test]
    public async Task An_existing_account_with_the_same_username_is_linked()
    {
        var idp = new FakeOidcProvider();
        using var app = App(idp);

        var response = await idp.SignInAsync(Client(app), "sub-admin", UserService.AdminUsername);

        await Assert.That(response.Headers.Location!.OriginalString).IsEqualTo("/");
        var admin = await FindAsync(app, UserService.AdminUsername);
        await Assert.That(admin!.OidcSubject).IsEqualTo($"{FakeOidcProvider.Issuer} sub-admin");
        await Assert.That(admin.IsAdmin).IsTrue();
    }

    [Test]
    public async Task A_username_linked_to_another_subject_is_refused()
    {
        var idp = new FakeOidcProvider();
        using var app = App(idp);

        await idp.SignInAsync(Client(app), "sub-1", "kim");

        await AssertRefused(await idp.SignInAsync(Client(app), "sub-2", "kim"));
    }

    [Test]
    public async Task A_missing_username_is_refused()
    {
        var idp = new FakeOidcProvider();
        using var app = App(idp);

        await AssertRefused(await idp.SignInAsync(Client(app), "sub-1", null));
    }

    [Test]
    public async Task A_refusal_is_explained_on_the_login_page()
    {
        var idp = new FakeOidcProvider();
        using var app = App(idp);

        var html = await app.GetHtmlAsync(Client(app), "/Account/Login?error=oidc");

        await Assert.That(html).Contains("did not work");
    }

    [Test]
    public async Task An_unverified_email_is_refused_as_a_username()
    {
        var idp = new FakeOidcProvider();
        using var app = App(idp, new() { ["BOH_OIDC_USERNAME_CLAIM"] = "email" });

        idp.ExtraClaims = [new("email", "admin"), new("email_verified", "false", ClaimValueTypes.Boolean)];
        await AssertRefused(await idp.SignInAsync(Client(app), "sub-1", null));
        await Assert.That((await FindAsync(app, UserService.AdminUsername))!.OidcSubject).IsNull();

        idp.ExtraClaims = [new("email", "kim@example.com"), new("email_verified", "true", ClaimValueTypes.Boolean)];
        await Assert.That((await idp.SignInAsync(Client(app), "sub-2", null)).Headers.Location!.OriginalString).IsEqualTo("/");
    }

    // ---- groups --------------------------------------------------------

    [Test]
    public async Task The_admin_group_grants_and_revokes_admin_on_each_sign_in()
    {
        var idp = new FakeOidcProvider();
        using var app = App(idp, new() { ["BOH_OIDC_ADMIN_GROUP"] = "boh-admins" });

        await idp.SignInAsync(Client(app), "sub-1", "kim", "boh-admins");
        await Assert.That((await FindAsync(app, "kim"))!.IsAdmin).IsTrue();

        await idp.SignInAsync(Client(app), "sub-1", "kim", "staff");
        await Assert.That((await FindAsync(app, "kim"))!.IsAdmin).IsFalse();
    }

    [Test]
    public async Task Without_an_admin_group_admin_is_left_to_boh()
    {
        var idp = new FakeOidcProvider();
        using var app = App(idp);

        await idp.SignInAsync(Client(app), "sub-1", "kim", "boh-admins");

        await Assert.That((await FindAsync(app, "kim"))!.IsAdmin).IsFalse();
    }

    [Test]
    public async Task The_user_group_keeps_outsiders_out()
    {
        var idp = new FakeOidcProvider();
        using var app = App(idp, new() { ["BOH_OIDC_USER_GROUP"] = "boh" });

        await AssertRefused(await idp.SignInAsync(Client(app), "sub-1", "kim", "staff"));
        await Assert.That(await FindAsync(app, "kim")).IsNull();

        var response = await idp.SignInAsync(Client(app), "sub-2", "lee", "boh");
        await Assert.That(response.Headers.Location!.OriginalString).IsEqualTo("/");
    }

    // ---- local auth ----------------------------------------------------

    [Test]
    public async Task With_both_the_login_page_offers_both()
    {
        var idp = new FakeOidcProvider();
        using var app = App(idp, new() { ["BOH_OIDC_NAME"] = "Authentik" });

        var html = await app.GetHtmlAsync(Client(app), "/Account/Login");

        await Assert.That(html).Contains("name=\"Password\"");
        await Assert.That(html).Contains("Sign in with Authentik");
    }

    [Test]
    public async Task With_local_auth_off_the_login_page_offers_only_oidc()
    {
        var idp = new FakeOidcProvider();
        using var app = App(idp, LocalOff);

        var html = await app.GetHtmlAsync(Client(app), "/Account/Login");

        await Assert.That(html).DoesNotContain("name=\"Password\"");
        await Assert.That(html).DoesNotContain("passkey-signin");
        await Assert.That(html).Contains("handler=Oidc");
    }

    [Test]
    public async Task With_local_auth_off_a_password_is_not_accepted()
    {
        var idp = new FakeOidcProvider();
        using var app = App(idp, LocalOff);
        var client = Client(app);

        var page = await app.GetHtmlAsync(client, "/Account/Login");
        var response = await TestApp.PostHxAsync(client, "/Account/Login", page, new()
        {
            ["Username"] = UserService.AdminUsername,
            ["Password"] = TestApp.AdminPassword,
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task With_local_auth_off_the_account_page_drops_passwords_and_passkeys()
    {
        var idp = new FakeOidcProvider();
        using var app = App(idp, LocalOff);
        var client = Client(app);

        await idp.SignInAsync(client, "sub-1", "kim");
        var html = await app.GetHtmlAsync(client, "/Account");

        await Assert.That(html).DoesNotContain("Change password");
        await Assert.That(html).DoesNotContain("id=\"passkeys\"");
        await Assert.That(html).Contains("id=\"tokens\"");
    }

    [Test]
    public async Task With_local_auth_off_admins_cannot_create_users_or_set_passwords()
    {
        var idp = new FakeOidcProvider();
        using var app = App(idp, new() { ["BOH_LOCAL_AUTH"] = "false", ["BOH_OIDC_ADMIN_GROUP"] = "boh-admins" });
        var client = Client(app);

        await idp.SignInAsync(client, "sub-1", "kim", "boh-admins");
        var html = await app.GetHtmlAsync(client, "/Users");

        await Assert.That(html).Contains("(SSO)");
        await Assert.That(html).DoesNotContain("handler=Create");
        await Assert.That(html).DoesNotContain("handler=ResetPassword");

        var response = await TestApp.PostHxAsync(client, "/Users?handler=Create", html, new()
        {
            ["username"] = "lee",
            ["password"] = "long-enough-password",
        });
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Turning_local_auth_off_ends_password_sessions()
    {
        var idp = new FakeOidcProvider();
        string cookie;

        using (var on = App(idp))
        {
            var client = on.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false });
            var form = await client.GetAsync("/Account/Login", Ct);
            var page = await form.Content.ReadAsStringAsync(Ct);

            var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Login")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["Username"] = UserService.AdminUsername,
                    ["Password"] = TestApp.AdminPassword,
                    ["__RequestVerificationToken"] = TestApp.FormValue(TestApp.FormWithField(page, "Password"), "__RequestVerificationToken"),
                }),
            };
            request.Headers.Add("Cookie", string.Join("; ", form.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0])));

            var signIn = await client.SendAsync(request, Ct);
            cookie = signIn.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]).Single(c => c.StartsWith("boh.auth="));

            await Assert.That((await Get(client, "/Account", cookie)).StatusCode).IsEqualTo(HttpStatusCode.OK);

            // Same data directory, so the same keys read the cookie.
            using var off = new TestApp(authMode: "password", oidc: idp, settings: LocalOff, dataPath: on.DataPath);
            var response = await Get(
                off.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false }),
                "/Account", cookie);

            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
            await Assert.That(response.Headers.Location!.PathAndQuery).StartsWith("/Account/Login");
        }
    }

    private static Task<HttpResponseMessage> Get(HttpClient client, string url, string cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", cookie);
        return client.SendAsync(request);
    }

    [Test]
    public async Task With_local_auth_off_the_admin_password_is_not_seeded()
    {
        var idp = new FakeOidcProvider();
        using var app = App(idp, LocalOff);
        _ = Client(app);

        await Assert.That(await FindAsync(app, UserService.AdminUsername)).IsNull();
    }

    [Test]
    public async Task Local_auth_cannot_be_turned_off_without_oidc()
    {
        using var app = new TestApp(authMode: "password", settings: LocalOff);

        var error = Assert.Throws<InvalidOperationException>(() => app.CreateClient());
        await Assert.That(error.Message).Contains("BOH_LOCAL_AUTH=false");
    }
}
