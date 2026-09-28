using Boh.Web.Data;
using Boh.Web.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Boh.Web.Services;

public abstract record UserResult
{
    private UserResult() { }

    public sealed record Ok : UserResult;
    public sealed record Rejected(string Reason) : UserResult;
}

public sealed record UserRow(int Id, string Username, bool IsAdmin, DateTimeOffset CreatedAt, int PostCount);

public sealed class UserService(BohDbContext db, ILogger<UserService> logger)
{
    public const string AdminUsername = "admin";

    public const int MinPasswordLength = 8;

    private const int MaxUsernameLength = 64;

    // ---- authentication ------------------------------------------------

    /// <summary>Null for unknown user and wrong password alike.</summary>
    public async Task<User?> AuthenticateAsync(string? username, string? password, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password)) return null;

        var normalized = Normalize(username);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == normalized, ct);
        if (user is null) return null;

        if (!Verify(user, password)) return null;

        // Verify may have upgraded the stored hash.
        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);
        return user;
    }

    public Task<User?> FindByIdAsync(int id, CancellationToken ct) =>
        db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct);

    // ---- listing -------------------------------------------------------

    public async Task<List<UserRow>> ListAsync(CancellationToken ct) =>
        await db.Users.AsNoTracking()
            .OrderByDescending(u => u.IsAdmin)
            .ThenBy(u => u.Username)
            .Select(u => new UserRow(
                u.Id,
                u.Username,
                u.IsAdmin,
                u.CreatedAt,
                db.Posts.Count(p => p.UploadedById == u.Id)))
            .ToListAsync(ct);

    // ---- management ----------------------------------------------------

    public async Task<UserResult> CreateAsync(string? username, string? password, bool isAdmin, CancellationToken ct)
    {
        var name = Normalize(username);

        if (ValidateUsername(name) is { } usernameProblem) return new UserResult.Rejected(usernameProblem);
        if (ValidatePassword(password) is { } passwordProblem) return new UserResult.Rejected(passwordProblem);

        if (await db.Users.AnyAsync(u => u.Username == name, ct))
            return new UserResult.Rejected($"There is already a user called '{name}'.");

        db.Users.Add(new User
        {
            Username = name,
            PasswordHash = Hash(password!),
            IsAdmin = isAdmin,
            CreatedAt = DateTimeOffset.UtcNow
        });

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Created user {Username} (admin: {IsAdmin})", LogSafe.Value(name), isAdmin);

        return new UserResult.Ok();
    }

    /// <remarks>Posts survive: UploadedById is ON DELETE SET NULL.</remarks>
    public async Task<UserResult> DeleteAsync(int userId, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return new UserResult.Rejected("That user no longer exists.");

        if (user.IsAdmin && await CountOtherAdminsAsync(userId, ct) == 0)
        {
            return new UserResult.Rejected(
                "This is the only administrator. Promote someone else first, or nobody could manage the instance.");
        }

        db.Users.Remove(user);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Deleted user {Username}", user.Username);
        return new UserResult.Ok();
    }

    public async Task<UserResult> SetAdminAsync(int userId, bool isAdmin, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return new UserResult.Rejected("That user no longer exists.");

        if (user.IsAdmin && !isAdmin && await CountOtherAdminsAsync(userId, ct) == 0)
        {
            return new UserResult.Rejected(
                "This is the only administrator. Promote someone else before standing down.");
        }

        user.IsAdmin = isAdmin;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("User {Username} admin set to {IsAdmin}", user.Username, isAdmin);
        return new UserResult.Ok();
    }

    /// <summary>Admin reset; no current password needed.</summary>
    public async Task<UserResult> SetPasswordAsync(int userId, string? password, CancellationToken ct)
    {
        if (ValidatePassword(password) is { } problem) return new UserResult.Rejected(problem);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return new UserResult.Rejected("That user no longer exists.");

        user.PasswordHash = Hash(password!);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Password reset for {Username}", user.Username);
        return new UserResult.Ok();
    }

    /// <summary>Self-service change; requires the current password.</summary>
    public async Task<UserResult> ChangeOwnPasswordAsync(
        int userId, string? currentPassword, string? newPassword, CancellationToken ct)
    {
        if (ValidatePassword(newPassword) is { } problem) return new UserResult.Rejected(problem);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return new UserResult.Rejected("That user no longer exists.");

        if (string.IsNullOrEmpty(currentPassword)
            || !Verify(user, currentPassword))
        {
            return new UserResult.Rejected("Your current password is not correct.");
        }

        user.PasswordHash = Hash(newPassword!);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("User {Username} changed their own password", user.Username);
        return new UserResult.Ok();
    }

    /// <summary>Stores each mode's palette. Unknown ids store as null rather than erroring.</summary>
    public async Task<UserResult> SetThemesAsync(int userId, string? light, string? dark, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return new UserResult.Rejected("That user no longer exists.");

        user.LightTheme = Themes.Normalize(light, Themes.LightMode);
        user.DarkTheme = Themes.Normalize(dark, Themes.DarkMode);

        await db.SaveChangesAsync(ct);
        return new UserResult.Ok();
    }

    // ---- seeding -------------------------------------------------------

    /// <summary>Applies <c>BOH_ADMIN_PASSWORD</c> at startup, rewriting only on change.</summary>
    public async Task SeedAdminAsync(string? password, CancellationToken ct)
    {
        var admin = await db.Users.FirstOrDefaultAsync(u => u.Username == AdminUsername, ct);

        if (string.IsNullOrEmpty(password))
        {
            if (!await db.Users.AnyAsync(u => u.IsAdmin, ct))
            {
                logger.LogError(
                    "BOH_ADMIN_PASSWORD is not set and no administrator exists — nobody can sign in. " +
                    "Set it, or run with BOH_AUTH_MODE=none if this instance is not exposed.");
            }

            return;
        }

        if (admin is null)
        {
            db.Users.Add(new User
            {
                Username = AdminUsername,
                PasswordHash = Hash(password),
                IsAdmin = true,
                CreatedAt = DateTimeOffset.UtcNow
            });

            await db.SaveChangesAsync(ct);
            logger.LogInformation("Created the admin account from BOH_ADMIN_PASSWORD.");
            return;
        }

        var changed = false;

        // This account is the way back in; never leave it demoted.
        if (!admin.IsAdmin)
        {
            admin.IsAdmin = true;
            changed = true;
        }

        var previousHash = admin.PasswordHash;
        if (!Verify(admin, password)) admin.PasswordHash = Hash(password);
        changed |= admin.PasswordHash != previousHash;

        if (changed)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Reapplied BOH_ADMIN_PASSWORD to the admin account.");
        }
    }

    // ---- internals -----------------------------------------------------

    private static readonly PasswordHasher<User> Hasher = new();

    private static string Hash(string password) => Hasher.HashPassword(null!, password);

    /// <summary>Checks a password, upgrading the stored hash when the hasher asks for it.</summary>
    private static bool Verify(User user, string password)
    {
        // Leftover BCrypt hash: no longer verifiable, and not base64, which the hasher throws on.
        if (user.PasswordHash.StartsWith("$2", StringComparison.Ordinal)) return false;

        var result = Hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (result == PasswordVerificationResult.SuccessRehashNeeded) user.PasswordHash = Hash(password);
        return result != PasswordVerificationResult.Failed;
    }

    private Task<int> CountOtherAdminsAsync(int excludingUserId, CancellationToken ct) =>
        db.Users.CountAsync(u => u.IsAdmin && u.Id != excludingUserId, ct);

    private static string Normalize(string? username) => (username ?? "").Trim().ToLowerInvariant();

    /// <summary>Returns a problem description, or null when the name is acceptable.</summary>
    private static string? ValidateUsername(string name)
    {
        if (name.Length == 0) return "Enter a username.";
        if (name.Length > MaxUsernameLength) return $"Usernames are limited to {MaxUsernameLength} characters.";

        foreach (var c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '.'))
            {
                return "Usernames may contain letters, digits, underscore, hyphen and dot only.";
            }
        }

        return null;
    }

    private static string? ValidatePassword(string? password) =>
        string.IsNullOrEmpty(password) || password.Length < MinPasswordLength
            ? $"Passwords must be at least {MinPasswordLength} characters."
            : null;
}
