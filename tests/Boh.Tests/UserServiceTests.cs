using Boh.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Boh.Tests;

public class UserServiceTests
{
    private static CancellationToken Ct => CancellationToken.None;

    private const string Password = "correct-horse";

    private static async Task<int> SeedAdminAsync(TestEnvironment env)
    {
        await env.Users.SeedAdminAsync(Password, Ct);
        env.Db.ChangeTracker.Clear();
        return (await env.Db.Users.SingleAsync(u => u.Username == UserService.AdminUsername, Ct)).Id;
    }

    private static async Task<int> IdOfAsync(TestEnvironment env, string username)
    {
        env.Db.ChangeTracker.Clear();
        return (await env.Db.Users.SingleAsync(u => u.Username == username, Ct)).Id;
    }

    // ---- authentication ------------------------------------------------

    [Fact]
    public async Task A_seeded_admin_can_sign_in()
    {
        using var env = new TestEnvironment();
        await SeedAdminAsync(env);

        var user = await env.Users.AuthenticateAsync("admin", Password, Ct);

        Assert.NotNull(user);
        Assert.True(user!.IsAdmin);
    }

    [Fact]
    public async Task The_username_is_matched_case_insensitively()
    {
        using var env = new TestEnvironment();
        await SeedAdminAsync(env);

        Assert.NotNull(await env.Users.AuthenticateAsync("ADMIN", Password, Ct));
        Assert.NotNull(await env.Users.AuthenticateAsync("  Admin  ", Password, Ct));
    }

    [Theory]
    [InlineData("admin", "wrong-password")]
    [InlineData("nobody", "correct-horse")]
    [InlineData("", "")]
    public async Task Bad_credentials_are_refused(string username, string password)
    {
        using var env = new TestEnvironment();
        await SeedAdminAsync(env);

        Assert.Null(await env.Users.AuthenticateAsync(username, password, Ct));
    }

    // ---- creating ------------------------------------------------------

    [Fact]
    public async Task An_ordinary_user_can_be_created_and_sign_in()
    {
        using var env = new TestEnvironment();

        Assert.IsType<UserResult.Ok>(await env.Users.CreateAsync("friend", Password, false, Ct));

        var user = await env.Users.AuthenticateAsync("friend", Password, Ct);
        Assert.NotNull(user);
        Assert.False(user!.IsAdmin);
    }

    [Fact]
    public async Task Usernames_are_stored_normalized()
    {
        using var env = new TestEnvironment();

        await env.Users.CreateAsync("  Friend  ", Password, false, Ct);

        Assert.NotNull(await env.Users.AuthenticateAsync("friend", Password, Ct));
    }

    [Fact]
    public async Task A_duplicate_username_is_rejected()
    {
        using var env = new TestEnvironment();
        await env.Users.CreateAsync("friend", Password, false, Ct);

        var result = await env.Users.CreateAsync("FRIEND", Password, false, Ct);

        Assert.IsType<UserResult.Rejected>(result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has space")]
    [InlineData("has/slash")]
    [InlineData("has:colon")]
    public async Task An_unusable_username_is_rejected(string username)
    {
        using var env = new TestEnvironment();

        Assert.IsType<UserResult.Rejected>(await env.Users.CreateAsync(username, Password, false, Ct));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    public async Task A_weak_password_is_rejected(string password)
    {
        using var env = new TestEnvironment();

        Assert.IsType<UserResult.Rejected>(await env.Users.CreateAsync("friend", password, false, Ct));
    }

    [Fact]
    public async Task The_password_is_not_stored_in_the_clear()
    {
        using var env = new TestEnvironment();
        await env.Users.CreateAsync("friend", Password, false, Ct);

        var stored = (await env.Db.Users.SingleAsync(u => u.Username == "friend", Ct)).PasswordHash;

        Assert.DoesNotContain(Password, stored);
        Assert.StartsWith("$2", stored);
    }

    // ---- the lockout guards --------------------------------------------

    /// <summary>
    /// The failure this prevents is total: with no administrator nobody can add users, manage
    /// tags, or restore the situation from inside the application.
    /// </summary>
    [Fact]
    public async Task The_last_administrator_cannot_be_deleted()
    {
        using var env = new TestEnvironment();
        var adminId = await SeedAdminAsync(env);
        await env.Users.CreateAsync("friend", Password, false, Ct);

        var result = await env.Users.DeleteAsync(adminId, Ct);

        Assert.IsType<UserResult.Rejected>(result);
        Assert.True(await env.Db.Users.AnyAsync(u => u.IsAdmin, Ct));
    }

    [Fact]
    public async Task The_last_administrator_cannot_be_demoted()
    {
        using var env = new TestEnvironment();
        var adminId = await SeedAdminAsync(env);

        var result = await env.Users.SetAdminAsync(adminId, false, Ct);

        Assert.IsType<UserResult.Rejected>(result);
        Assert.True(await env.Db.Users.AnyAsync(u => u.IsAdmin, Ct));
    }

    [Fact]
    public async Task An_administrator_can_be_deleted_once_another_exists()
    {
        using var env = new TestEnvironment();
        var adminId = await SeedAdminAsync(env);
        await env.Users.CreateAsync("second", Password, isAdmin: true, Ct);

        Assert.IsType<UserResult.Ok>(await env.Users.DeleteAsync(adminId, Ct));
        Assert.Equal(1, await env.Db.Users.CountAsync(u => u.IsAdmin, Ct));
    }

    [Fact]
    public async Task An_ordinary_user_can_always_be_deleted()
    {
        using var env = new TestEnvironment();
        await SeedAdminAsync(env);
        await env.Users.CreateAsync("friend", Password, false, Ct);

        Assert.IsType<UserResult.Ok>(await env.Users.DeleteAsync(await IdOfAsync(env, "friend"), Ct));
        Assert.False(await env.Db.Users.AnyAsync(u => u.Username == "friend", Ct));
    }

    /// <summary>Deleting the uploader must not take the collection with it.</summary>
    [Fact]
    public async Task Deleting_a_user_keeps_their_posts()
    {
        using var env = new TestEnvironment();
        await SeedAdminAsync(env);
        await env.Users.CreateAsync("friend", Password, false, Ct);
        var friendId = await IdOfAsync(env, "friend");

        var result = await env.Posts.CreateAsync(
            new MemoryStream(TestEnvironment.MakePng(60, 60)), friendId, "", Ct);
        var postId = Assert.IsType<PostCreateResult.Created>(result).Post.Id;

        Assert.IsType<UserResult.Ok>(await env.Users.DeleteAsync(friendId, Ct));

        env.Db.ChangeTracker.Clear();
        var post = await env.Posts.GetAsync(postId, Ct);
        Assert.NotNull(post);
        Assert.Null(post!.UploadedById);
    }

    // ---- promotion and passwords ---------------------------------------

    [Fact]
    public async Task A_user_can_be_promoted_and_demoted()
    {
        using var env = new TestEnvironment();
        await SeedAdminAsync(env);
        await env.Users.CreateAsync("friend", Password, false, Ct);
        var friendId = await IdOfAsync(env, "friend");

        Assert.IsType<UserResult.Ok>(await env.Users.SetAdminAsync(friendId, true, Ct));
        Assert.True((await env.Users.FindByIdAsync(friendId, Ct))!.IsAdmin);

        env.Db.ChangeTracker.Clear();
        Assert.IsType<UserResult.Ok>(await env.Users.SetAdminAsync(friendId, false, Ct));
        Assert.False((await env.Users.FindByIdAsync(friendId, Ct))!.IsAdmin);
    }

    [Fact]
    public async Task An_admin_reset_replaces_the_password_without_knowing_the_old_one()
    {
        using var env = new TestEnvironment();
        await env.Users.CreateAsync("friend", Password, false, Ct);
        var friendId = await IdOfAsync(env, "friend");

        Assert.IsType<UserResult.Ok>(await env.Users.SetPasswordAsync(friendId, "brand-new-password", Ct));

        Assert.Null(await env.Users.AuthenticateAsync("friend", Password, Ct));
        Assert.NotNull(await env.Users.AuthenticateAsync("friend", "brand-new-password", Ct));
    }

    [Fact]
    public async Task Changing_your_own_password_requires_the_current_one()
    {
        using var env = new TestEnvironment();
        await env.Users.CreateAsync("friend", Password, false, Ct);
        var friendId = await IdOfAsync(env, "friend");

        var wrong = await env.Users.ChangeOwnPasswordAsync(friendId, "not-my-password", "brand-new-password", Ct);
        Assert.IsType<UserResult.Rejected>(wrong);
        Assert.NotNull(await env.Users.AuthenticateAsync("friend", Password, Ct));

        env.Db.ChangeTracker.Clear();
        var right = await env.Users.ChangeOwnPasswordAsync(friendId, Password, "brand-new-password", Ct);
        Assert.IsType<UserResult.Ok>(right);
        Assert.NotNull(await env.Users.AuthenticateAsync("friend", "brand-new-password", Ct));
    }

    [Fact]
    public async Task A_weak_new_password_is_rejected_on_self_service_change()
    {
        using var env = new TestEnvironment();
        await env.Users.CreateAsync("friend", Password, false, Ct);
        var friendId = await IdOfAsync(env, "friend");

        Assert.IsType<UserResult.Rejected>(
            await env.Users.ChangeOwnPasswordAsync(friendId, Password, "short", Ct));
    }

    [Fact]
    public async Task Operations_on_a_missing_user_are_rejected_rather_than_thrown()
    {
        using var env = new TestEnvironment();

        Assert.IsType<UserResult.Rejected>(await env.Users.DeleteAsync(9999, Ct));
        Assert.IsType<UserResult.Rejected>(await env.Users.SetAdminAsync(9999, true, Ct));
        Assert.IsType<UserResult.Rejected>(await env.Users.SetPasswordAsync(9999, Password, Ct));
    }

    // ---- seeding -------------------------------------------------------

    [Fact]
    public async Task Seeding_twice_with_the_same_password_does_not_rewrite_the_hash()
    {
        using var env = new TestEnvironment();
        await SeedAdminAsync(env);
        var first = (await env.Db.Users.AsNoTracking().SingleAsync(u => u.Username == "admin", Ct)).PasswordHash;

        env.Db.ChangeTracker.Clear();
        await env.Users.SeedAdminAsync(Password, Ct);

        env.Db.ChangeTracker.Clear();
        var second = (await env.Db.Users.AsNoTracking().SingleAsync(u => u.Username == "admin", Ct)).PasswordHash;

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Seeding_applies_a_changed_password()
    {
        using var env = new TestEnvironment();
        await SeedAdminAsync(env);

        env.Db.ChangeTracker.Clear();
        await env.Users.SeedAdminAsync("a-different-password", Ct);

        Assert.Null(await env.Users.AuthenticateAsync("admin", Password, Ct));
        Assert.NotNull(await env.Users.AuthenticateAsync("admin", "a-different-password", Ct));
    }

    /// <summary>
    /// The seeded account is the documented way back in, so an accidental demotion must not
    /// survive a restart.
    /// </summary>
    [Fact]
    public async Task Seeding_restores_admin_rights_to_the_seeded_account()
    {
        using var env = new TestEnvironment();
        var adminId = await SeedAdminAsync(env);
        await env.Users.CreateAsync("second", Password, isAdmin: true, Ct);
        await env.Users.SetAdminAsync(adminId, false, Ct);

        env.Db.ChangeTracker.Clear();
        await env.Users.SeedAdminAsync(Password, Ct);

        Assert.True((await env.Users.FindByIdAsync(adminId, Ct))!.IsAdmin);
    }
}
