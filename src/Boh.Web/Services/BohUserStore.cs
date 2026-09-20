using Boh.Web.Data;
using Boh.Web.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Boh.Web.Services;

/// <summary>
/// Presents boh's own users and passkeys through the interfaces ASP.NET Core's passkey
/// handler expects.
/// </summary>
/// <remarks>
/// boh does not use ASP.NET Core Identity: accounts are the <see cref="User"/> table,
/// passwords are BCrypt through <see cref="UserService"/>, and sign-in writes a plain cookie.
/// The framework's WebAuthn implementation, however, is reached through
/// <c>UserManager&lt;TUser&gt;</c>, and a <c>UserManager</c> needs a store — so this is that
/// store, and nothing more. It is the adapter that lets the passkey code in the shared
/// framework work against the existing table instead of boh taking on Identity, and it is why
/// there is no third-party WebAuthn library here.
/// <para>
/// Only the members the passkey path actually uses do anything. Account creation, deletion
/// and renaming stay with <see cref="UserService"/>, which is where the rules about
/// usernames, the last administrator and the seeded admin live; routing some of that through
/// a second front door would be two implementations of the same thing.
/// </para>
/// </remarks>
public sealed class BohUserStore(BohDbContext db) : IUserStore<User>, IUserPasskeyStore<User>
{
    // ---- users ---------------------------------------------------------

    public Task<string> GetUserIdAsync(User user, CancellationToken ct) =>
        Task.FromResult(user.Id.ToString());

    public Task<string?> GetUserNameAsync(User user, CancellationToken ct) =>
        Task.FromResult<string?>(user.Username);

    /// <summary>
    /// Usernames are already stored lowercase, so the normalized form is the stored form.
    /// </summary>
    public Task<string?> GetNormalizedUserNameAsync(User user, CancellationToken ct) =>
        Task.FromResult<string?>(user.Username);

    public Task<User?> FindByIdAsync(string userId, CancellationToken ct) =>
        int.TryParse(userId, out var id)
            ? db.Users.FirstOrDefaultAsync(u => u.Id == id, ct)
            : Task.FromResult<User?>(null);

    public Task<User?> FindByNameAsync(string normalizedUserName, CancellationToken ct) =>
        db.Users.FirstOrDefaultAsync(u => u.Username == normalizedUserName, ct);

    /// <summary>Saves whatever the passkey handler changed — in practice, a credential.</summary>
    public async Task<IdentityResult> UpdateAsync(User user, CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);
        return IdentityResult.Success;
    }

    // Accounts are created, removed and renamed through UserService; nothing on the passkey
    // path does any of it, and an implementation here would be a second set of rules.
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

    /// <summary>
    /// Stores a newly registered credential, or brings an existing one up to date after it
    /// has been used — the sign counter and the backup flags both move.
    /// </summary>
    /// <remarks>
    /// Staged rather than saved: <c>UserManager</c> follows this with
    /// <see cref="UpdateAsync"/>, which commits, and a save here would make the pair two
    /// transactions instead of one.
    /// </remarks>
    public async Task AddOrUpdatePasskeyAsync(User user, UserPasskeyInfo passkey, CancellationToken ct)
    {
        var existing = await db.Passkeys.FirstOrDefaultAsync(
            p => p.UserId == user.Id && p.CredentialId == passkey.CredentialId, ct);

        if (existing is null)
        {
            db.Passkeys.Add(Create(user.Id, passkey));
            return;
        }

        // The name is the owner's, not the authenticator's, so an assertion carrying an empty
        // one must not wipe what they typed when they registered it.
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

    /// <summary>
    /// The account a credential belongs to, which is how a sign-in that never asked for a
    /// username finds out who is signing in.
    /// </summary>
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

    /// <summary>The context is owned by the request scope, so there is nothing to release.</summary>
    public void Dispose() { }
}
