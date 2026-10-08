using System.Text.RegularExpressions;
using Boh.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Boh.Tests;

/// <summary>
/// The content security policy refuses inline event handlers, so one in the markup silently
/// does nothing — for a delete confirmation, that means deleting without asking.
/// </summary>
public class InlineHandlerTests
{
    [Fact]
    public async Task No_page_renders_an_inline_event_handler()
    {
        using var app = new TestApp(authMode: "password");
        var client = await app.SignInAsync();

        var postId = await app.CreatePostAsync(8);
        await app.TagAsync(postId, "sky");

        using (var scope = app.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserService>();
            await users.CreateAsync("someone", "someone-password", false, CancellationToken.None);
        }

        string[] pages =
        [
            "/", $"/Posts/Detail/{postId}", "/Tags", "/Tags/Admin",
            "/Maintenance", "/Import", "/Users", "/Account",
        ];

        foreach (var url in pages)
        {
            var html = await app.GetHtmlAsync(client, url);
            Assert.True(InlineHandlers(html).Count == 0, $"{url} has {string.Join(", ", InlineHandlers(html))}");
        }

        var login = await app.GetHtmlAsync(app.CreateNonRedirectingClient(), "/Account/Login");
        Assert.Empty(InlineHandlers(login));
    }

    [Theory]
    [InlineData("/Maintenance")]
    [InlineData("/Posts/Detail/{0}")]
    [InlineData("/Users")]
    public async Task Destructive_forms_still_ask_first(string url)
    {
        using var app = new TestApp(authMode: "password");
        var client = await app.SignInAsync();

        var postId = await app.CreatePostAsync(8);
        using (var scope = app.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserService>();
            await users.CreateAsync("someone", "someone-password", false, CancellationToken.None);
        }

        var html = await app.GetHtmlAsync(client, string.Format(url, postId));

        Assert.Matches("<form[^>]*\\sdata-confirm=\"[^\"]+\"", html);
    }

    // Script bodies are dropped first so code mentioning "onclick=" is not mistaken for markup.
    private static List<string> InlineHandlers(string html)
    {
        var markup = Regex.Replace(html, "<script\\b[^>]*>.*?</script>", "", RegexOptions.Singleline);

        return Regex.Matches(markup, "<[a-zA-Z][^>]*>")
            .SelectMany(tag => Regex.Matches(tag.Value, "\\s(on[a-z]+)\\s*=", RegexOptions.IgnoreCase))
            .Select(m => m.Groups[1].Value)
            .ToList();
    }
}
