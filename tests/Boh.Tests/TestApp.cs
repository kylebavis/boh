using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Boh.Web.Services;
using ImageMagick;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Boh.Tests;

/// <summary>
/// The real application, booted in-process against a throwaway data directory. Routing,
/// model binding and view rendering all run for real — which is the point, since those are
/// the layers a page-model unit test cannot reach.
/// </summary>
public sealed class TestApp : WebApplicationFactory<Program>
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "boh-web-tests", Guid.NewGuid().ToString("N"));

    private readonly int _pageSize;
    private readonly string _authMode;
    private readonly bool _publicRead;

    /// <param name="pageSize">Small by default so a handful of posts spans several pages.</param>
    /// <param name="authMode">
    /// Defaults to "none": writes are what these tests exercise, and there is no sign-in flow
    /// to drive. Pass "password" to render the pages an instance with accounts would serve —
    /// signed out, since the client carries no cookie.
    /// </param>
    /// <param name="publicRead">
    /// Only meaningful alongside <c>authMode: "password"</c>: it is the configuration where a
    /// signed-out visitor can see a page but not its editing controls.
    /// </param>
    public TestApp(int pageSize = 2, string authMode = "none", bool publicRead = false)
    {
        _pageSize = pageSize;
        _authMode = authMode;
        _publicRead = publicRead;
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        Directory.CreateDirectory(_root);

        // In-memory configuration rather than environment variables: env vars are
        // process-wide and xunit runs test classes in parallel.
        builder.ConfigureHostConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BOH_DATA_PATH"] = _root,
            ["BOH_PAGE_SIZE"] = _pageSize.ToString(),
            ["BOH_AUTH_MODE"] = _authMode,
            ["BOH_PUBLIC_READ"] = _publicRead.ToString(),
            ["BOH_ADMIN_PASSWORD"] = AdminPassword,
        }));

        return base.CreateHost(builder);
    }

    /// <summary>A client that reports redirects instead of following them, so tests can assert on the target.</summary>
    public HttpClient CreateNonRedirectingClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>Creates a post through the application's own service, with distinct content.</summary>
    public async Task<int> CreatePostAsync(uint size)
    {
        using var scope = Services.CreateScope();
        var posts = scope.ServiceProvider.GetRequiredService<PostService>();

        var result = await posts.CreateAsync(
            new MemoryStream(TestEnvironment.MakePng(size, size)), null, "", CancellationToken.None);

        return Assert.IsType<PostCreateResult.Created>(result).Post.Id;
    }

    /// <summary>
    /// Creates a post from a picture with structure to it, which is what gives it a perceptual
    /// hash — <see cref="CreatePostAsync"/>'s flat colours deliberately have none. The same
    /// <paramref name="seed"/> at another size is the same picture in different bytes.
    /// </summary>
    public async Task<int> CreatePatternPostAsync(
        uint size, MagickFormat format = MagickFormat.Png, int seed = 1)
    {
        using var scope = Services.CreateScope();
        var posts = scope.ServiceProvider.GetRequiredService<PostService>();

        var result = await posts.CreateAsync(
            new MemoryStream(TestEnvironment.MakePattern(size, size, format, seed)),
            null, "", CancellationToken.None);

        return Assert.IsType<PostCreateResult.Created>(result).Post.Id;
    }

    /// <summary>Applies tags the way the application does, so implications and counts stay correct.</summary>
    public async Task TagAsync(int postId, params string[] tags)
    {
        using var scope = Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<TagService>();

        await service.SetPostTagsAsync(
            postId, Boh.Web.Tags.TagName.ParseMany(string.Join(' ', tags)), CancellationToken.None);
    }

    /// <summary>
    /// Applies already-parsed tags, for shapes the text parser cannot reach. An importer
    /// stores a name through <see cref="Boh.Web.Tags.TagName.TryParseInNamespace"/>, which
    /// leaves a colon inside the name alone; <c>ParseMany</c> would split on it instead.
    /// </summary>
    public async Task TagAsync(int postId, IReadOnlyCollection<Boh.Web.Tags.TagName> tags)
    {
        using var scope = Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<TagService>();

        await service.SetPostTagsAsync(postId, tags, CancellationToken.None);
    }

    /// <summary>The explicit tags a post carries, read back as stored.</summary>
    public async Task<List<Boh.Web.Tags.TagName>> ExplicitTagsAsync(int postId)
    {
        using var scope = Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<TagService>();

        return await service.GetExplicitTagNamesAsync(postId, CancellationToken.None);
    }

    public async Task AddAliasAsync(string alias, string canonical)
    {
        using var scope = Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<TagService>();

        Assert.True(Boh.Web.Tags.TagName.TryParse(alias, out var a));
        Assert.True(Boh.Web.Tags.TagName.TryParse(canonical, out var c));
        Assert.IsType<TagLinkResult.Ok>(await service.AddAliasAsync(a, c, CancellationToken.None));
    }

    public async Task AddImplicationAsync(string child, string parent)
    {
        using var scope = Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<TagService>();

        Assert.True(Boh.Web.Tags.TagName.TryParse(child, out var ch));
        Assert.True(Boh.Web.Tags.TagName.TryParse(parent, out var pa));
        Assert.IsType<TagLinkResult.Ok>(await service.AddImplicationAsync(ch, pa, CancellationToken.None));
    }

    /// <summary>
    /// Issues the request an <c>hx-post</c> control would, carrying the antiforgery token the
    /// layout hands htmx in <c>hx-headers</c>. Reading the token off the page rather than
    /// disabling antiforgery keeps these tests on the same path the browser takes.
    /// </summary>
    /// <param name="fields">
    /// Form values to send, for handlers that read one. Omit for a control that carries
    /// everything it needs in its query string, which is what a remove button does.
    /// </param>
    public static async Task<HttpResponseMessage> PostHxAsync(
        HttpClient client, string url, string pageHtml, Dictionary<string, string>? fields = null)
    {
        var token = Regex.Match(pageHtml, "hx-headers='([^']*)'");
        Assert.True(token.Success, "the layout rendered no hx-headers");

        using var headers = JsonDocument.Parse(WebUtility.HtmlDecode(token.Groups[1].Value));

        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add(
            "RequestVerificationToken",
            headers.RootElement.GetProperty("RequestVerificationToken").GetString());

        if (fields is not null) request.Content = new FormUrlEncodedContent(fields);

        return await client.SendAsync(request);
    }

    public async Task<string> GetHtmlAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>The password the admin account is seeded with when running with accounts on.</summary>
    public const string AdminPassword = "test-admin-password";

    /// <summary>
    /// Signs the client in as the seeded administrator. Only meaningful on an app built with
    /// <c>authMode: "password"</c>; the returned client carries the auth cookie from then on.
    /// </summary>
    public async Task<HttpClient> SignInAsync()
    {
        // Follows redirects so the cookie handler sees the post-login navigation, and so the
        // antiforgery token comes from a fully rendered form.
        var client = CreateClient();

        var form = await GetHtmlAsync(client, "/Account/Login");
        var token = Regex.Match(form, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(token.Success, "no antiforgery token on the login form");

        var response = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["Username"] = UserService.AdminUsername,
                ["Password"] = AdminPassword,
                ["__RequestVerificationToken"] = token.Groups[1].Value,
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private const string CardPattern = "<a class=\"gallery-item\" href=\"([^\"]+)\"";

    /// <summary>The post ids the gallery grid is showing, in the order rendered.</summary>
    public static int[] PostIdsIn(string html) => Regex
        .Matches(html, "<a class=\"gallery-item\" href=\"/Posts/Detail/(\\d+)")
        .Select(m => int.Parse(m.Groups[1].Value))
        .ToArray();

    /// <summary>The rendered "Page N of M" label, or null when the gallery fits on one page.</summary>
    public static string? PaginationLabel(string html) =>
        Regex.Match(html, @"Page \d+ of \d+") is { Success: true } m ? m.Value : null;

    /// <summary>The href of the first gallery card, which carries the browsing context.</summary>
    public static string FirstCardHref(string html)
    {
        var match = Regex.Match(html, CardPattern);
        Assert.True(match.Success, "the gallery rendered no cards");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    /// <summary>
    /// The one <c>&lt;form&gt;</c> element containing a field of the given name. Field lookups
    /// are scoped to a form deliberately: the layout's search box is also named <c>q</c>, so
    /// searching the whole document could read the wrong element and pass for the wrong reason.
    /// </summary>
    public static string FormWithField(string html, string name)
    {
        var forms = Regex.Matches(html, "<form.*?</form>", RegexOptions.Singleline)
            .Select(m => m.Value)
            .Where(f => f.Contains("name=\"" + name + "\""))
            .ToArray();

        Assert.Single(forms);
        return forms[0];
    }

    /// <summary>The URL a form actually submits to, as rendered by the tag helpers.</summary>
    public static string FormAction(string form)
    {
        var match = Regex.Match(form, "<form[^>]*\\baction=\"([^\"]*)\"");
        Assert.True(match.Success, "the form declares no action");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    /// <summary>
    /// Reads a form field's value, for antiforgery tokens and hidden state. Attribute order is
    /// not assumed — the tag helpers and the hand-written markup do not agree on it.
    /// </summary>
    public static string FormValue(string form, string name)
    {
        var tag = Regex.Match(form, "<input[^>]*\\bname=\"" + Regex.Escape(name) + "\"[^>]*>");
        Assert.True(tag.Success, $"no input named {name} in the form");

        var value = Regex.Match(tag.Value, "\\bvalue=\"([^\"]*)\"");
        return value.Success ? WebUtility.HtmlDecode(value.Groups[1].Value) : "";
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing) return;
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A still-locked SQLite file is not worth failing a test over.
        }
    }
}
