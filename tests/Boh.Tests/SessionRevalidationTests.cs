using Boh.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Boh.Tests;

/// <summary>
/// Signed-in users are cached between requests; demoting or deleting one must still apply on
/// their very next request.
/// </summary>
public class SessionRevalidationTests
{
    private static CancellationToken Ct => CancellationToken.None;

    private static async Task<int> AdminIdWithAnotherAdminAsync(TestApp app)
    {
        using var scope = app.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserService>();

        // The last administrator can be neither demoted nor deleted.
        Assert.IsType<UserResult.Ok>(await users.CreateAsync("second", "second-password-123", true, Ct));

        return (await users.ListAsync(Ct)).Single(u => u.Username == UserService.AdminUsername).Id;
    }

    private static async Task<string> PathReachedAsync(HttpClient client, string url) =>
        (await client.GetAsync(url)).RequestMessage!.RequestUri!.AbsolutePath;

    [Fact]
    public async Task Demotion_applies_on_the_next_request()
    {
        using var app = new TestApp(authMode: "password");
        var client = await app.SignInAsync();
        var adminId = await AdminIdWithAnotherAdminAsync(app);
        Assert.Equal("/Users", await PathReachedAsync(client, "/Users"));

        using (var scope = app.Services.CreateScope())
        {
            Assert.IsType<UserResult.Ok>(await scope.ServiceProvider.GetRequiredService<UserService>()
                .SetAdminAsync(adminId, false, Ct));
        }

        Assert.Equal("/Account/Login", await PathReachedAsync(client, "/Users"));
    }

    [Fact]
    public async Task Deletion_applies_on_the_next_request()
    {
        using var app = new TestApp(authMode: "password");
        var client = await app.SignInAsync();
        var adminId = await AdminIdWithAnotherAdminAsync(app);
        Assert.Equal("/Tags", await PathReachedAsync(client, "/Tags"));

        using (var scope = app.Services.CreateScope())
        {
            Assert.IsType<UserResult.Ok>(await scope.ServiceProvider.GetRequiredService<UserService>()
                .DeleteAsync(adminId, Ct));
        }

        Assert.Equal("/Account/Login", await PathReachedAsync(client, "/Tags"));
    }
}
