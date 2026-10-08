using Boh.Web.Data;
using Boh.Web.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Boh.Web.Services;

/// <summary>
/// Minimal Identity store over boh's users and passkeys, so the framework's passkey handler
/// works without adopting Identity. Only the passkey path's members do anything.
/// </summary>
public sealed class BohUserStore(BohDbContext db) : IUserStore<User>, IUserPasskeyStore<User>
{
    // ---- users ---------------------------------------------------------

    public Task<string> GetUserIdAsync(User user, CancellationToken ct) =>
        Task.FromResult(user.Id.ToString());

    public Task<string?> GetUserNameAsync(User user, CancellationToken ct) =>
        Task.FromResult<string?>(user.Username);

    /// <summary>Usernames are stored lowercase already.</summary>
    public Task<string?> GetNormalizedUserNameAsync(User user, CancellationToken ct) =>
        Task.FromResult<string?>(user.Username);

    public Task<User?> FindByIdAsync(string userId, CancellationToken ct) =>
        int.TryParse(userId, out var id)
            ? db.Users.FirstOrDefaultAsync(u => u.Id == id, ct)
            : Task.FromResult<User?>(null);

    public Task<User?> FindByNameAsync(string normalizedUserName, CancellationToken ct) =>
        db.Users.FirstOrDefaultAsync(u => u.Username == normalizedUserName, ct);

    public async Task<IdentityResult> UpdateAsync(User user, CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);
        return IdentityResult.Success;
    }

    // Account changes go through UserService.
    public Task SetUserNameAsync(User user, string? userName, CancellationToken ct) =>
        throw new NotSupportedException(Unsupported);

    public Task SetNormalizedUserNameAsync(User user, string? normalizedName, CancellationToken ct) =>
        Task.CompletedTask;

    public Task<IdentityResult> CreateAsync(User user, CancellationToken ct) =>
        throw new NotSupportedException(Unsupported);

    public Task<IdentityResult> DeleteAsync(User user, CancellationToken ct) =>
        throw new NotSupportedException(Unsupported);

    private const string Unsupported =
        "boh manages accounts through UserService, not through ASP.NET Core Identity.";

    // ---- passkeys ------------------------------------------------------

    /// <summary>Stages a new or updated credential; <see cref="UpdateAsync"/> commits.</summary>
    public async Task AddOrUpdatePasskeyAsync(User user, UserPasskeyInfo passkey, CancellationToken ct)
    {
        var existing = await db.Passkeys.FirstOrDefaultAsync(
            p => p.UserId == user.Id && p.CredentialId == passkey.CredentialId, ct);

        if (existing is null)
        {
            db.Passkeys.Add(Create(user.Id, passkey));
            return;
        }

        // Don't let an empty name from an assertion wipe the owner's.
        if (!string.IsNullOrWhiteSpace(passkey.Name)) existing.Name = passkey.Name;

        existing.PublicKey = passkey.PublicKey;
        existing.SignCount = passkey.SignCount;
        existing.Transports = FormatTransports(passkey.Transports);
        existing.IsUserVerified = passkey.IsUserVerified;
        existing.IsBackupEligible = passkey.IsBackupEligible;
        existing.IsBackedUp = passkey.IsBackedUp;
        existing.LastUsedAt = DateTimeOffset.UtcNow;
    }

    public async Task<IList<UserPasskeyInfo>> GetPasskeysAsync(User user, CancellationToken ct) =>
    [
        .. (await db.Passkeys.AsNoTracking()
                .Where(p => p.UserId == user.Id)
                .OrderBy(p => p.CreatedAt)
                .ToListAsync(ct))
            .Select(ToInfo)
    ];

    /// <summary>Finds the account behind a credential during sign-in.</summary>
    public Task<User?> FindByPasskeyIdAsync(byte[] credentialId, CancellationToken ct) =>
        db.Passkeys
            .Where(p => p.CredentialId == credentialId)
            .Select(p => p.User)
            .FirstOrDefaultAsync(ct);

    public async Task<UserPasskeyInfo?> FindPasskeyAsync(User user, byte[] credentialId, CancellationToken ct)
    {
        var passkey = await db.Passkeys.AsNoTracking().FirstOrDefaultAsync(
            p => p.UserId == user.Id && p.CredentialId == credentialId, ct);

        return passkey is null ? null : ToInfo(passkey);
    }

    public async Task RemovePasskeyAsync(User user, byte[] credentialId, CancellationToken ct)
    {
        var passkey = await db.Passkeys.FirstOrDefaultAsync(
            p => p.UserId == user.Id && p.CredentialId == credentialId, ct);

        if (passkey is not null) db.Passkeys.Remove(passkey);
    }

    // ---- internals -----------------------------------------------------

    private static Passkey Create(int userId, UserPasskeyInfo passkey) => new()
    {
        UserId = userId,
        CredentialId = passkey.CredentialId,
        PublicKey = passkey.PublicKey,
        Name = string.IsNullOrWhiteSpace(passkey.Name) ? "Passkey" : passkey.Name,
        SignCount = passkey.SignCount,
        Transports = FormatTransports(passkey.Transports),
        IsUserVerified = passkey.IsUserVerified,
        IsBackupEligible = passkey.IsBackupEligible,
        IsBackedUp = passkey.IsBackedUp,
        AttestationObject = passkey.AttestationObject,
        ClientDataJson = passkey.ClientDataJson,
        CreatedAt = passkey.CreatedAt,
    };

    private static UserPasskeyInfo ToInfo(Passkey passkey) => new(
        passkey.CredentialId,
        passkey.PublicKey,
        passkey.CreatedAt,
        passkey.SignCount,
        passkey.Transports.Length == 0 ? [] : passkey.Transports.Split(','),
        passkey.IsUserVerified,
        passkey.IsBackupEligible,
        passkey.IsBackedUp,
        passkey.AttestationObject,
        passkey.ClientDataJson)
    {
        Name = passkey.Name,
    };

    private static string FormatTransports(string[]? transports) =>
        transports is null ? "" : string.Join(',', transports);

    public void Dispose() { }
}
