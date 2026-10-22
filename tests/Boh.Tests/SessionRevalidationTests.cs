using Boh.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Boh.Tests;

/// <summary>
/// Signed-in users are cached between requests; demoting or deleting one must still apply on
/// their very next request.
/// </summary>
public class SessionRevalidationTests
{
    private static CancellationToken Ct => TestContext.Current!.Execution.CancellationToken;

    private static async Task<int> AdminIdWithAnotherAdminAsync(TestApp app)
    {
        using var scope = app.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserService>();

        // The last administrator can be neither demoted nor deleted.
        await Assert.That(await users.CreateAsync("second", "second-password-123", true, Ct)).IsTypeOf<UserResult.Ok>();

        return (await users.ListAsync(Ct)).Single(u => u.Username == UserService.AdminUsername).Id;
    }

    private static async Task<string> PathReachedAsync(HttpClient client, string url) =>
        (await client.GetAsync(url)).RequestMessage!.RequestUri!.AbsolutePath;

    [Test]
    public async Task Demotion_applies_on_the_next_request()
    {
        using var app = new TestApp(authMode: "password");
        var client = await app.SignInAsync();
        var adminId = await AdminIdWithAnotherAdminAsync(app);
        await Assert.That(await PathReachedAsync(client, "/Users")).IsEqualTo("/Users");

        using (var scope = app.Services.CreateScope())
        {
            await Assert.That(await scope.ServiceProvider.GetRequiredService<UserService>()
                .SetAdminAsync(adminId, false, Ct)).IsTypeOf<UserResult.Ok>();
        }

        await Assert.That(await PathReachedAsync(client, "/Users")).IsEqualTo("/Account/Login");
    }

    [Test]
    public async Task Deletion_applies_on_the_next_request()
    {
        using var app = new TestApp(authMode: "password");
        var client = await app.SignInAsync();
        var adminId = await AdminIdWithAnotherAdminAsync(app);
        await Assert.That(await PathReachedAsync(client, "/Tags")).IsEqualTo("/Tags");

        using (var scope = app.Services.CreateScope())
        {
            await Assert.That(await scope.ServiceProvider.GetRequiredService<UserService>()
                .DeleteAsync(adminId, Ct)).IsTypeOf<UserResult.Ok>();
        }

        await Assert.That(await PathReachedAsync(client, "/Tags")).IsEqualTo("/Account/Login");
    }
}
