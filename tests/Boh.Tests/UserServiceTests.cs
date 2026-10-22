using Boh.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Boh.Tests;

public class UserServiceTests
{
    private static CancellationToken Ct => TestContext.Current!.Execution.CancellationToken;

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

    [Test]
    public async Task A_seeded_admin_can_sign_in()
    {
        using var env = new TestEnvironment();
        await SeedAdminAsync(env);

        var user = await env.Users.AuthenticateAsync("admin", Password, Ct);

        await Assert.That(user).IsNotNull();
        await Assert.That(user!.IsAdmin).IsTrue();
    }

    [Test]
    public async Task The_username_is_matched_case_insensitively()
    {
        using var env = new TestEnvironment();
        await SeedAdminAsync(env);

        await Assert.That(await env.Users.AuthenticateAsync("ADMIN", Password, Ct)).IsNotNull();
        await Assert.That(await env.Users.AuthenticateAsync("  Admin  ", Password, Ct)).IsNotNull();
    }

    [Test]
    [Arguments("admin", "wrong-password")]
    [Arguments("nobody", "correct-horse")]
    [Arguments("", "")]
    public async Task Bad_credentials_are_refused(string username, string password)
    {
        using var env = new TestEnvironment();
        await SeedAdminAsync(env);

        await Assert.That(await env.Users.AuthenticateAsync(username, password, Ct)).IsNull();
    }

    // ---- creating ------------------------------------------------------

    [Test]
    public async Task An_ordinary_user_can_be_created_and_sign_in()
    {
        using var env = new TestEnvironment();

        await Assert.That(await env.Users.CreateAsync("friend", Password, false, Ct)).IsTypeOf<UserResult.Ok>();

        var user = await env.Users.AuthenticateAsync("friend", Password, Ct);
        await Assert.That(user).IsNotNull();
        await Assert.That(user!.IsAdmin).IsFalse();
    }

    [Test]
    public async Task Usernames_are_stored_normalized()
    {
        using var env = new TestEnvironment();

        await env.Users.CreateAsync("  Friend  ", Password, false, Ct);

        await Assert.That(await env.Users.AuthenticateAsync("friend", Password, Ct)).IsNotNull();
    }

    [Test]
    public async Task A_duplicate_username_is_rejected()
    {
        using var env = new TestEnvironment();
        await env.Users.CreateAsync("friend", Password, false, Ct);

        var result = await env.Users.CreateAsync("FRIEND", Password, false, Ct);

        await Assert.That(result).IsTypeOf<UserResult.Rejected>();
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("has space")]
    [Arguments("has/slash")]
    [Arguments("has:colon")]
    public async Task An_unusable_username_is_rejected(string username)
    {
        using var env = new TestEnvironment();

        await Assert.That(await env.Users.CreateAsync(username, Password, false, Ct)).IsTypeOf<UserResult.Rejected>();
    }

    [Test]
    [Arguments("")]
    [Arguments("short")]
    public async Task A_weak_password_is_rejected(string password)
    {
        using var env = new TestEnvironment();

        await Assert.That(await env.Users.CreateAsync("friend", password, false, Ct)).IsTypeOf<UserResult.Rejected>();
    }

    [Test]
    public async Task The_password_is_not_stored_in_the_clear()
    {
        using var env = new TestEnvironment();
        await env.Users.CreateAsync("friend", Password, false, Ct);

        var stored = (await env.Db.Users.SingleAsync(u => u.Username == "friend", Ct)).PasswordHash;

        await Assert.That(stored).DoesNotContain(Password);
    }

    [Test]
    public async Task A_legacy_bcrypt_hash_is_refused_without_throwing()
    {
        using var env = new TestEnvironment();
        await env.Users.CreateAsync("friend", Password, false, Ct);
        await env.Db.Users.Where(u => u.Username == "friend")
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordHash, LegacyBcryptHash), Ct);
        env.Db.ChangeTracker.Clear();

        await Assert.That(await env.Users.AuthenticateAsync("friend", Password, Ct)).IsNull();
    }

    /// <summary>The shape of hash older releases stored.</summary>
    private const string LegacyBcryptHash = "$2a$11$abcdefghijklmnopqrstuuJ4Vf7yH9qQ1m2n3o4p5q6r7s8t9u0v1w";

    // ---- the lockout guards --------------------------------------------

    /// <summary>
    /// The failure this prevents is total: with no administrator nobody can add users, manage
    /// tags, or restore the situation from inside the application.
    /// </summary>
    [Test]
    public async Task The_last_administrator_cannot_be_deleted()
    {
        using var env = new TestEnvironment();
        var adminId = await SeedAdminAsync(env);
        await env.Users.CreateAsync("friend", Password, false, Ct);

        var result = await env.Users.DeleteAsync(adminId, Ct);

        await Assert.That(result).IsTypeOf<UserResult.Rejected>();
        await Assert.That(await env.Db.Users.AnyAsync(u => u.IsAdmin, Ct)).IsTrue();
    }

    [Test]
    public async Task The_last_administrator_cannot_be_demoted()
    {
        using var env = new TestEnvironment();
        var adminId = await SeedAdminAsync(env);

        var result = await env.Users.SetAdminAsync(adminId, false, Ct);

        await Assert.That(result).IsTypeOf<UserResult.Rejected>();
        await Assert.That(await env.Db.Users.AnyAsync(u => u.IsAdmin, Ct)).IsTrue();
    }

    [Test]
    public async Task An_administrator_can_be_deleted_once_another_exists()
    {
        using var env = new TestEnvironment();
        var adminId = await SeedAdminAsync(env);
        await env.Users.CreateAsync("second", Password, isAdmin: true, Ct);

        await Assert.That(await env.Users.DeleteAsync(adminId, Ct)).IsTypeOf<UserResult.Ok>();
        await Assert.That(await env.Db.Users.CountAsync(u => u.IsAdmin, Ct)).IsEqualTo(1);
    }

    [Test]
    public async Task An_ordinary_user_can_always_be_deleted()
    {
        using var env = new TestEnvironment();
        await SeedAdminAsync(env);
        await env.Users.CreateAsync("friend", Password, false, Ct);

        await Assert.That(await env.Users.DeleteAsync(await IdOfAsync(env, "friend"), Ct)).IsTypeOf<UserResult.Ok>();
        await Assert.That(await env.Db.Users.AnyAsync(u => u.Username == "friend", Ct)).IsFalse();
    }

    /// <summary>Deleting the uploader must not take the collection with it.</summary>
    [Test]
    public async Task Deleting_a_user_keeps_their_posts()
    {
        using var env = new TestEnvironment();
        await SeedAdminAsync(env);
        await env.Users.CreateAsync("friend", Password, false, Ct);
        var friendId = await IdOfAsync(env, "friend");

        var result = await env.Posts.CreateAsync(
            new MemoryStream(TestEnvironment.MakePng(60, 60)), friendId, "", Ct);
        var postId = (await Assert.That(result).IsTypeOf<PostCreateResult.Created>())!.Post.Id;

        await Assert.That(await env.Users.DeleteAsync(friendId, Ct)).IsTypeOf<UserResult.Ok>();

        env.Db.ChangeTracker.Clear();
        var post = await env.Posts.GetAsync(postId, Ct);
        await Assert.That(post).IsNotNull();
        await Assert.That(post!.UploadedById).IsNull();
    }

    // ---- promotion and passwords ---------------------------------------

    [Test]
    public async Task A_user_can_be_promoted_and_demoted()
    {
        using var env = new TestEnvironment();
        await SeedAdminAsync(env);
        await env.Users.CreateAsync("friend", Password, false, Ct);
        var friendId = await IdOfAsync(env, "friend");

        await Assert.That(await env.Users.SetAdminAsync(friendId, true, Ct)).IsTypeOf<UserResult.Ok>();
        await Assert.That((await env.Users.FindByIdAsync(friendId, Ct))!.IsAdmin).IsTrue();

        env.Db.ChangeTracker.Clear();
        await Assert.That(await env.Users.SetAdminAsync(friendId, false, Ct)).IsTypeOf<UserResult.Ok>();
        await Assert.That((await env.Users.FindByIdAsync(friendId, Ct))!.IsAdmin).IsFalse();
    }

    [Test]
    public async Task An_admin_reset_replaces_the_password_without_knowing_the_old_one()
    {
        using var env = new TestEnvironment();
        await env.Users.CreateAsync("friend", Password, false, Ct);
        var friendId = await IdOfAsync(env, "friend");

        await Assert.That(await env.Users.SetPasswordAsync(friendId, "brand-new-password", Ct)).IsTypeOf<UserResult.Ok>();

        await Assert.That(await env.Users.AuthenticateAsync("friend", Password, Ct)).IsNull();
        await Assert.That(await env.Users.AuthenticateAsync("friend", "brand-new-password", Ct)).IsNotNull();
    }

    [Test]
    public async Task Changing_your_own_password_requires_the_current_one()
    {
        using var env = new TestEnvironment();
        await env.Users.CreateAsync("friend", Password, false, Ct);
        var friendId = await IdOfAsync(env, "friend");

        var wrong = await env.Users.ChangeOwnPasswordAsync(friendId, "not-my-password", "brand-new-password", Ct);
        await Assert.That(wrong).IsTypeOf<UserResult.Rejected>();
        await Assert.That(await env.Users.AuthenticateAsync("friend", Password, Ct)).IsNotNull();

        env.Db.ChangeTracker.Clear();
        var right = await env.Users.ChangeOwnPasswordAsync(friendId, Password, "brand-new-password", Ct);
        await Assert.That(right).IsTypeOf<UserResult.Ok>();
        await Assert.That(await env.Users.AuthenticateAsync("friend", "brand-new-password", Ct)).IsNotNull();
    }

    [Test]
    public async Task A_weak_new_password_is_rejected_on_self_service_change()
    {
        using var env = new TestEnvironment();
        await env.Users.CreateAsync("friend", Password, false, Ct);
        var friendId = await IdOfAsync(env, "friend");

        await Assert.That(await env.Users.ChangeOwnPasswordAsync(friendId, Password, "short", Ct)).IsTypeOf<UserResult.Rejected>();
    }

    [Test]
    public async Task Operations_on_a_missing_user_are_rejected_rather_than_thrown()
    {
        using var env = new TestEnvironment();

        await Assert.That(await env.Users.DeleteAsync(9999, Ct)).IsTypeOf<UserResult.Rejected>();
        await Assert.That(await env.Users.SetAdminAsync(9999, true, Ct)).IsTypeOf<UserResult.Rejected>();
        await Assert.That(await env.Users.SetPasswordAsync(9999, Password, Ct)).IsTypeOf<UserResult.Rejected>();
    }

    // ---- seeding -------------------------------------------------------

    [Test]
    public async Task Seeding_twice_with_the_same_password_does_not_rewrite_the_hash()
    {
        using var env = new TestEnvironment();
        await SeedAdminAsync(env);
        var first = (await env.Db.Users.AsNoTracking().SingleAsync(u => u.Username == "admin", Ct)).PasswordHash;

        env.Db.ChangeTracker.Clear();
        await env.Users.SeedAdminAsync(Password, Ct);

        env.Db.ChangeTracker.Clear();
        var second = (await env.Db.Users.AsNoTracking().SingleAsync(u => u.Username == "admin", Ct)).PasswordHash;

        await Assert.That(second).IsEqualTo(first);
    }

    [Test]
    public async Task Seeding_applies_a_changed_password()
    {
        using var env = new TestEnvironment();
        await SeedAdminAsync(env);

        env.Db.ChangeTracker.Clear();
        await env.Users.SeedAdminAsync("a-different-password", Ct);

        await Assert.That(await env.Users.AuthenticateAsync("admin", Password, Ct)).IsNull();
        await Assert.That(await env.Users.AuthenticateAsync("admin", "a-different-password", Ct)).IsNotNull();
    }

    // ---- theme preferences ---------------------------------------------

    [Test]
    public async Task Theme_preferences_are_stored_per_mode()
    {
        using var env = new TestEnvironment();
        var adminId = await SeedAdminAsync(env);

        await env.Users.SetThemesAsync(adminId, "solarized-light", "nord", Ct);

        var user = await env.Users.FindByIdAsync(adminId, Ct);
        await Assert.That(user!.LightTheme).IsEqualTo("solarized-light");
        await Assert.That(user.DarkTheme).IsEqualTo("nord");
    }

    /// <summary>
    /// A dark scheme on the light side would render its own text colors against the wrong
    /// background. The values come from a pair of selects, so a bad one means a stale id or
    /// a hand-edited post — neither worth an error message when falling back to the stock
    /// look is a perfectly good outcome.
    /// </summary>
    [Test]
    [Arguments("nord", "solarized-light")]
    [Arguments("no-such-theme", "no-such-theme")]
    [Arguments("", "")]
    public async Task A_palette_that_does_not_belong_falls_back_to_the_default(string light, string dark)
    {
        using var env = new TestEnvironment();
        var adminId = await SeedAdminAsync(env);

        await env.Users.SetThemesAsync(adminId, light, dark, Ct);

        var user = await env.Users.FindByIdAsync(adminId, Ct);
        await Assert.That(user!.LightTheme).IsNull();
        await Assert.That(user.DarkTheme).IsNull();
    }

    [Test]
    public async Task A_theme_can_be_cleared_back_to_the_default()
    {
        using var env = new TestEnvironment();
        var adminId = await SeedAdminAsync(env);
        await env.Users.SetThemesAsync(adminId, "gruvbox-light", "dracula", Ct);

        env.Db.ChangeTracker.Clear();
        await env.Users.SetThemesAsync(adminId, null, "dracula", Ct);

        var user = await env.Users.FindByIdAsync(adminId, Ct);
        await Assert.That(user!.LightTheme).IsNull();
        await Assert.That(user.DarkTheme).IsEqualTo("dracula");
    }

    [Test]
    public async Task Setting_a_theme_for_a_deleted_user_is_rejected()
    {
        using var env = new TestEnvironment();
        await SeedAdminAsync(env);

        var result = await env.Users.SetThemesAsync(9999, "gruvbox-light", "nord", Ct);

        await Assert.That(result).IsTypeOf<UserResult.Rejected>();
    }

    /// <summary>
    /// The seeded account is the documented way back in, so an accidental demotion must not
    /// survive a restart.
    /// </summary>
    [Test]
    public async Task Seeding_restores_admin_rights_to_the_seeded_account()
    {
        using var env = new TestEnvironment();
        var adminId = await SeedAdminAsync(env);
        await env.Users.CreateAsync("second", Password, isAdmin: true, Ct);
        await env.Users.SetAdminAsync(adminId, false, Ct);

        env.Db.ChangeTracker.Clear();
        await env.Users.SeedAdminAsync(Password, Ct);

        await Assert.That((await env.Users.FindByIdAsync(adminId, Ct))!.IsAdmin).IsTrue();
    }
}
