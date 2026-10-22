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
    [Test]
    public async Task No_page_renders_an_inline_event_handler()
    {
        using var app = new TestApp(authMode: "password");
        var client = await app.SignInAsync();

        var postId = await app.CreatePostAsync(8);
        await app.TagAsync(postId, "sky");

        using (var scope = app.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserService>();
            await users.CreateAsync("someone", "someone-password", false, TestContext.Current!.Execution.CancellationToken);
        }

        string[] pages =
        [
            "/", $"/Posts/Detail/{postId}", "/Tags", "/Tags/Admin",
            "/Maintenance", "/Import", "/Users", "/Account",
        ];

        foreach (var url in pages)
        {
            var html = await app.GetHtmlAsync(client, url);
            await Assert.That(InlineHandlers(html).Count == 0).IsTrue().Because($"{url} has {string.Join(", ", InlineHandlers(html))}");
        }

        var login = await app.GetHtmlAsync(app.CreateNonRedirectingClient(), "/Account/Login");
        await Assert.That(InlineHandlers(login)).IsEmpty();
    }

    [Test]
    [Arguments("/Maintenance")]
    [Arguments("/Posts/Detail/{0}")]
    [Arguments("/Users")]
    public async Task Destructive_forms_still_ask_first(string url)
    {
        using var app = new TestApp(authMode: "password");
        var client = await app.SignInAsync();

        var postId = await app.CreatePostAsync(8);
        using (var scope = app.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserService>();
            await users.CreateAsync("someone", "someone-password", false, TestContext.Current!.Execution.CancellationToken);
        }

        var html = await app.GetHtmlAsync(client, string.Format(url, postId));

        await Assert.That(html).Matches("<form[^>]*\\sdata-confirm=\"[^\"]+\"");
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
