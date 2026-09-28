using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Boh.Web.Data;
using Boh.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Boh.Web.Services;

/// <summary>One of an account's API tokens, as the account page lists it.</summary>
public sealed record ApiTokenRow(int Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

/// <summary>Creates, lists, revokes and checks the bearer tokens the API accepts.</summary>
public sealed class ApiTokenService(BohDbContext db, ILogger<ApiTokenService> logger)
{
    /// <summary>Marks a string as a boh token, so one pasted somewhere it should not be is recognizable.</summary>
    public const string Prefix = "boh_";

    public const int MaxPerUser = 20;

    private const int MaxNameLength = 64;

    private static readonly TimeSpan LastUsedGranularity = TimeSpan.FromHours(1);

    public async Task<List<ApiTokenRow>> ListAsync(int userId, CancellationToken ct) =>
        await db.ApiTokens.AsNoTracking()
            .Where(t => t.UserId == userId)
            .OrderBy(t => t.Id)
            .Select(t => new ApiTokenRow(t.Id, t.Name, t.CreatedAt, t.LastUsedAt))
            .ToListAsync(ct);

    /// <summary>Makes a token and returns its secret, which is not recoverable afterwards.</summary>
    public async Task<(UserResult Result, string? Secret)> CreateAsync(int userId, string? name, CancellationToken ct)
    {
        var chosen = (name ?? "").Trim();
        if (chosen.Length == 0) return (new UserResult.Rejected("Give the token a name."), null);
        if (chosen.Length > MaxNameLength) chosen = chosen[..MaxNameLength];

        if (await db.ApiTokens.CountAsync(t => t.UserId == userId, ct) >= MaxPerUser)
            return (new UserResult.Rejected($"An account can hold at most {MaxPerUser} tokens."), null);

        var secret = Prefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

        db.ApiTokens.Add(new ApiToken
        {
            UserId = userId,
            Name = chosen,
            Hash = Hash(secret),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Created API token for user {UserId}", userId);
        return (new UserResult.Ok(), secret);
    }

    /// <remarks>Scoped to the owner, so a guessed id cannot revoke somebody else's token.</remarks>
    public async Task<UserResult> DeleteAsync(int userId, int tokenId, CancellationToken ct)
    {
        var removed = await db.ApiTokens
            .Where(t => t.Id == tokenId && t.UserId == userId)
            .ExecuteDeleteAsync(ct);

        return removed == 0 ? new UserResult.Rejected("That token is already gone.") : new UserResult.Ok();
    }

    /// <summary>The account a secret belongs to, or null. Read fresh, so a demotion applies at once.</summary>
    public async Task<User?> AuthenticateAsync(string secret, CancellationToken ct)
    {
        if (!secret.StartsWith(Prefix, StringComparison.Ordinal)) return null;

        var hash = Hash(secret);
        var token = await db.ApiTokens.AsNoTracking()
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.Hash == hash, ct);

        if (token?.User is null) return null;

        var now = DateTimeOffset.UtcNow;
        if (token.LastUsedAt is null || now - token.LastUsedAt >= LastUsedGranularity)
        {
            await db.ApiTokens
                .Where(t => t.Id == token.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.LastUsedAt, now), ct);
        }

        return token.User;
    }

    private static byte[] Hash(string secret) => SHA256.HashData(Encoding.UTF8.GetBytes(secret));
}
