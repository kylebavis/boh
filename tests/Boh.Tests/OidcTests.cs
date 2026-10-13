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
    private static CancellationToken Ct => CancellationToken.None;

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

    private static void AssertRefused(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Account/Login?error=oidc", response.Headers.Location!.OriginalString);
    }

    // ---- the flow ------------------------------------------------------

    [Fact]
    public async Task The_authorize_request_uses_the_code_flow_with_pkce()
    {
        var idp = new FakeOidcProvider();
        using var app = App(idp);

        var location = await FakeOidcProvider.AuthorizeRequestAsync(Client(app));
        var query = HttpUtility.ParseQueryString(location.Query);

        Assert.StartsWith(FakeOidcProvider.Issuer + "/authorize", location.ToString());
        Assert.Equal("code", query["response_type"]);
        Assert.Null(query["response_mode"]); // query, the code flow default
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.EndsWith("/signin-oidc", query["redirect_uri"]);
    }

    [Fact]
    public async Task A_first_sign_in_creates_a_passwordless_account_and_signs_it_in()
    {
        var idp = new FakeOidcProvider();
        using var app = App(idp);
        var client = Client(app);

        var response = await idp.SignInAsync(client, "sub-1", "Kim");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location!.OriginalString);

        var user = await FindAsync(app, "kim");
        Assert.NotNull(user);
        Assert.Equal($"{FakeOidcProvider.Issuer} sub-1", user!.OidcSubject);
        Assert.Equal("", user.PasswordHash);
        Assert.False(user.IsAdmin);

        var account = await app.GetHtmlAsync(client, "/Account");
        Assert.Contains("Signed in as <strong>kim</strong>", account);
        Assert.Contains("has no password", account);
        Assert.DoesNotContain("name=\"currentPassword\"", account);
    }

    [Fact]
    public async Task A_returning_subject_keeps_its_account_when_the_username_changes()
    {
        var idp = new FakeOidcProvider();
        using var app = App(idp);

        await idp.SignInAsync(Client(app), "sub-1", "kim");
        var id = (await FindAsync(app, "kim"))!.Id;

        var response = await idp.SignInAsync(Client(app), "sub-1", "kimberly");

        Assert.Equal("/", response.Headers.Location!.OriginalString);
        Assert.Equal(id, (await FindAsync(app, "kim"))!.Id);
        Assert.Null(await FindAsync(app, "kimberly"));
    }

    [Fact]
    public async Task An_existing_account_with_the_same_username_is_linked()
    {
        var idp = new FakeOidcProvider();
        using var app = App(idp);

        var response = await idp.SignInAsync(Client(app), "sub-admin", UserService.AdminUsername);

        Assert.Equal("/", response.Headers.Location!.OriginalString);
        var admin = await FindAsync(app, UserService.AdminUsername);
        Assert.Equal($"{FakeOidcProvider.Issuer} sub-admin", admin!.OidcSubject);
        Assert.True(admin.IsAdmin);
    }

    [Fact]
    public async Task A_username_linked_to_another_subject_is_refused()
    {
        var idp = new FakeOidcProvider();
        using var app = App(idp);

        await idp.SignInAsync(Client(app), "sub-1", "kim");

        AssertRefused(await idp.SignInAsync(Client(app), "sub-2", "kim"));
    }

    [Fact]
    public async Task A_missing_username_is_refused()
    {
        var idp = new FakeOidcProvider();
        using var app = App(idp);

        AssertRefused(await idp.SignInAsync(Client(app), "sub-1", null));
    }

    [Fact]
    public async Task A_refusal_is_explained_on_the_login_page()
    {
        var idp = new FakeOidcProvider();
        using var app = App(idp);

        var html = await app.GetHtmlAsync(Client(app), "/Account/Login?error=oidc");

        Assert.Contains("did not work", html);
    }

    [Fact]
    public async Task An_unverified_email_is_refused_as_a_username()
    {
        var idp = new FakeOidcProvider();
        using var app = App(idp, new() { ["BOH_OIDC_USERNAME_CLAIM"] = "email" });

        idp.ExtraClaims = [new("email", "admin"), new("email_verified", "false", ClaimValueTypes.Boolean)];
        AssertRefused(await idp.SignInAsync(Client(app), "sub-1", null));
        Assert.Null((await FindAsync(app, UserService.AdminUsername))!.OidcSubject);

        idp.ExtraClaims = [new("email", "kim@example.com"), new("email_verified", "true", ClaimValueTypes.Boolean)];
        Assert.Equal("/", (await idp.SignInAsync(Client(app), "sub-2", null)).Headers.Location!.OriginalString);
    }

    // ---- groups --------------------------------------------------------

    [Fact]
    public async Task The_admin_group_grants_and_revokes_admin_on_each_sign_in()
    {
        var idp = new FakeOidcProvider();
        using var app = App(idp, new() { ["BOH_OIDC_ADMIN_GROUP"] = "boh-admins" });

        await idp.SignInAsync(Client(app), "sub-1", "kim", "boh-admins");
        Assert.True((await FindAsync(app, "kim"))!.IsAdmin);

        await idp.SignInAsync(Client(app), "sub-1", "kim", "staff");
        Assert.False((await FindAsync(app, "kim"))!.IsAdmin);
    }

    [Fact]
    public async Task Without_an_admin_group_admin_is_left_to_boh()
    {
        var idp = new FakeOidcProvider();
        using var app = App(idp);

        await idp.SignInAsync(Client(app), "sub-1", "kim", "boh-admins");

        Assert.False((await FindAsync(app, "kim"))!.IsAdmin);
    }

    [Fact]
    public async Task The_user_group_keeps_outsiders_out()
    {
        var idp = new FakeOidcProvider();
        using var app = App(idp, new() { ["BOH_OIDC_USER_GROUP"] = "boh" });

        AssertRefused(await idp.SignInAsync(Client(app), "sub-1", "kim", "staff"));
        Assert.Null(await FindAsync(app, "kim"));

        var response = await idp.SignInAsync(Client(app), "sub-2", "lee", "boh");
        Assert.Equal("/", response.Headers.Location!.OriginalString);
    }

    // ---- local auth ----------------------------------------------------

    [Fact]
    public async Task With_both_the_login_page_offers_both()
    {
        var idp = new FakeOidcProvider();
        using var app = App(idp, new() { ["BOH_OIDC_NAME"] = "Authentik" });

        var html = await app.GetHtmlAsync(Client(app), "/Account/Login");

        Assert.Contains("name=\"Password\"", html);
        Assert.Contains("Sign in with Authentik", html);
    }

    [Fact]
    public async Task With_local_auth_off_the_login_page_offers_only_oidc()
    {
        var idp = new FakeOidcProvider();
        using var app = App(idp, LocalOff);

        var html = await app.GetHtmlAsync(Client(app), "/Account/Login");

        Assert.DoesNotContain("name=\"Password\"", html);
        Assert.DoesNotContain("passkey-signin", html);
        Assert.Contains("handler=Oidc", html);
    }

    [Fact]
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

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task With_local_auth_off_the_account_page_drops_passwords_and_passkeys()
    {
        var idp = new FakeOidcProvider();
        using var app = App(idp, LocalOff);
        var client = Client(app);

        await idp.SignInAsync(client, "sub-1", "kim");
        var html = await app.GetHtmlAsync(client, "/Account");

        Assert.DoesNotContain("Change password", html);
        Assert.DoesNotContain("id=\"passkeys\"", html);
        Assert.Contains("id=\"tokens\"", html);
    }

    [Fact]
    public async Task With_local_auth_off_admins_cannot_create_users_or_set_passwords()
    {
        var idp = new FakeOidcProvider();
        using var app = App(idp, new() { ["BOH_LOCAL_AUTH"] = "false", ["BOH_OIDC_ADMIN_GROUP"] = "boh-admins" });
        var client = Client(app);

        await idp.SignInAsync(client, "sub-1", "kim", "boh-admins");
        var html = await app.GetHtmlAsync(client, "/Users");

        Assert.Contains("(SSO)", html);
        Assert.DoesNotContain("handler=Create", html);
        Assert.DoesNotContain("handler=ResetPassword", html);

        var response = await TestApp.PostHxAsync(client, "/Users?handler=Create", html, new()
        {
            ["username"] = "lee",
            ["password"] = "long-enough-password",
        });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Turning_local_auth_off_ends_password_sessions()
    {
        var idp = new FakeOidcProvider();
        string cookie;

        using (var on = App(idp))
        {
            var client = on.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false });
            var form = await client.GetAsync("/Account/Login");
            var page = await form.Content.ReadAsStringAsync();

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

            var signIn = await client.SendAsync(request);
            cookie = signIn.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]).Single(c => c.StartsWith("boh.auth="));

            Assert.Equal(HttpStatusCode.OK, (await Get(client, "/Account", cookie)).StatusCode);

            // Same data directory, so the same keys read the cookie.
            using var off = new TestApp(authMode: "password", oidc: idp, settings: LocalOff, dataPath: on.DataPath);
            var response = await Get(
                off.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false }),
                "/Account", cookie);

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.StartsWith("/Account/Login", response.Headers.Location!.PathAndQuery);
        }
    }

    private static Task<HttpResponseMessage> Get(HttpClient client, string url, string cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", cookie);
        return client.SendAsync(request);
    }

    [Fact]
    public async Task With_local_auth_off_the_admin_password_is_not_seeded()
    {
        var idp = new FakeOidcProvider();
        using var app = App(idp, LocalOff);
        _ = Client(app);

        Assert.Null(await FindAsync(app, UserService.AdminUsername));
    }

    [Fact]
    public void Local_auth_cannot_be_turned_off_without_oidc()
    {
        using var app = new TestApp(authMode: "password", settings: LocalOff);

        var error = Assert.Throws<InvalidOperationException>(() => app.CreateClient());
        Assert.Contains("BOH_LOCAL_AUTH=false", error.Message);
    }
}
