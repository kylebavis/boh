using Boh.Web.Data;
using Boh.Web.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Boh.Web.Services;

/// <summary>One of an account's passkeys, as the account page lists it.</summary>
public sealed record PasskeyRow(
    int Id, string Name, bool IsBackedUp, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

/// <summary>First half of a ceremony: options for the browser, and state to keep server-side.</summary>
public sealed record PasskeyCeremony(string OptionsJson, string State);

/// <summary>A refusal carries no reason, so failures are indistinguishable from outside.</summary>
public abstract record PasskeySignIn
{
    private PasskeySignIn() { }

    public sealed record Ok(User User) : PasskeySignIn;
    public sealed record Rejected : PasskeySignIn;
}

/// <summary>
/// Passkey registration and sign-in on existing accounts. WebAuthn itself is the framework's
/// <c>IPasskeyHandler</c>; the ceremony state names the account, so it must stay out of the
/// browser's reach (see <see cref="Security.PasskeyChallenge"/>).
/// </summary>
public sealed class PasskeyService(
    BohDbContext db,
    UserManager<User> identityUsers,
    IPasskeyHandler<User> handler,
    ILogger<PasskeyService> logger)
{
    /// <summary>Caps the exclude list sent to the browser.</summary>
    public const int MaxPerUser = 20;

    private const int MaxNameLength = 64;

    // ---- listing and management ----------------------------------------

    public async Task<List<PasskeyRow>> ListAsync(int userId, CancellationToken ct) =>
        await db.Passkeys.AsNoTracking()
            .Where(p => p.UserId == userId)
            .OrderBy(p => p.CreatedAt)
            .Select(p => new PasskeyRow(p.Id, p.Name, p.IsBackedUp, p.CreatedAt, p.LastUsedAt))
            .ToListAsync(ct);

    /// <remarks>Scoped to the owner so a guessed id can't remove someone else's passkey.</remarks>
    public async Task<UserResult> DeleteAsync(int userId, int passkeyId, CancellationToken ct)
    {
        var passkey = await db.Passkeys.FirstOrDefaultAsync(
            p => p.Id == passkeyId && p.UserId == userId, ct);

        if (passkey is null) return new UserResult.Rejected("That passkey is already gone.");

        db.Passkeys.Remove(passkey);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Removed passkey {PasskeyId} from user {UserId}", passkeyId, userId);
        return new UserResult.Ok();
    }

    public async Task<UserResult> RenameAsync(int userId, int passkeyId, string? name, CancellationToken ct)
    {
        if (Normalize(name) is not { Length: > 0 } chosen) return new UserResult.Rejected("Give the passkey a name.");

        var passkey = await db.Passkeys.FirstOrDefaultAsync(
            p => p.Id == passkeyId && p.UserId == userId, ct);

        if (passkey is null) return new UserResult.Rejected("That passkey no longer exists.");

        passkey.Name = chosen;
        await db.SaveChangesAsync(ct);

        return new UserResult.Ok();
    }

    // ---- registration --------------------------------------------------

    /// <summary>Starts registration. The returned state names the account; keep it from the browser.</summary>
    public async Task<PasskeyCeremony?> BeginRegistrationAsync(
        int userId, HttpContext context, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return null;

        var options = await handler.MakeCreationOptionsAsync(
            new PasskeyUserEntity
            {
                Id = user.Id.ToString(),
                Name = user.Username,
                DisplayName = user.Username,
            },
            context);

        return options.AttestationState is { } state
            ? new PasskeyCeremony(options.CreationOptionsJson, state)
            : null;
    }

    /// <summary>Verifies the authenticator's answer and stores the credential.</summary>
    public async Task<UserResult> CompleteRegistrationAsync(
        int userId,
        string? name,
        string credentialJson,
        string attestationState,
        HttpContext context,
        CancellationToken ct)
    {
        if (await db.Passkeys.CountAsync(p => p.UserId == userId, ct) >= MaxPerUser)
        {
            return new UserResult.Rejected(
                $"This account already has {MaxPerUser} passkeys. Remove one before adding another.");
        }

        var attestation = await handler.PerformAttestationAsync(new PasskeyAttestationContext
        {
            HttpContext = context,
            CredentialJson = credentialJson,
            AttestationState = attestationState,
        });

        if (!attestation.Succeeded)
        {
            // Expected for a bad ceremony; the browser gets the short form, the log the reason.
            logger.LogWarning(attestation.Failure,
                "Rejected a passkey registration for user {UserId}", userId);

            return new UserResult.Rejected("That passkey could not be registered. Try again.");
        }

        // Guard against the credential landing on another account.
        if (attestation.UserEntity.Id != userId.ToString())
        {
            logger.LogError(
                "A passkey registration for user {UserId} carried state for {StateUserId}",
                userId, LogSafe.Value(attestation.UserEntity.Id));

            return new UserResult.Rejected("That passkey could not be registered. Try again.");
        }

        var user = await identityUsers.FindByIdAsync(userId.ToString());
        if (user is null) return new UserResult.Rejected("That account no longer exists.");

        attestation.Passkey.Name = Normalize(name) is { Length: > 0 } chosen ? chosen : "Passkey";

        var stored = await identityUsers.AddOrUpdatePasskeyAsync(user, attestation.Passkey);
        if (!stored.Succeeded)
        {
            logger.LogError("Could not store a passkey for user {UserId}: {Errors}",
                userId, string.Join("; ", stored.Errors.Select(e => e.Description)));

            return new UserResult.Rejected("That passkey could not be stored.");
        }

        logger.LogInformation("User {UserId} registered a passkey", userId);
        return new UserResult.Ok();
    }

    // ---- sign-in -------------------------------------------------------

    /// <summary>Starts a sign-in without naming a credential, so nothing reveals which accounts exist.</summary>
    public async Task<PasskeyCeremony?> BeginAssertionAsync(HttpContext context)
    {
        var options = await handler.MakeRequestOptionsAsync(user: null, context);

        return options.AssertionState is { } state
            ? new PasskeyCeremony(options.RequestOptionsJson, state)
            : null;
    }

    public async Task<PasskeySignIn> CompleteAssertionAsync(
        string credentialJson, string assertionState, HttpContext context, CancellationToken ct)
    {
        var assertion = await handler.PerformAssertionAsync(new PasskeyAssertionContext
        {
            HttpContext = context,
            CredentialJson = credentialJson,
            AssertionState = assertionState,
        });

        if (!assertion.Succeeded)
        {
            logger.LogWarning(assertion.Failure, "Rejected a passkey sign-in");
            return new PasskeySignIn.Rejected();
        }

        // Store the counter and flags: the clone check relies on the stored counter.
        var updated = await identityUsers.AddOrUpdatePasskeyAsync(assertion.User, assertion.Passkey);
        if (!updated.Succeeded)
        {
            logger.LogError("Could not record the use of a passkey for user {UserId}: {Errors}",
                assertion.User.Id, string.Join("; ", updated.Errors.Select(e => e.Description)));
        }

        return new PasskeySignIn.Ok(assertion.User);
    }

    private static string Normalize(string? name)
    {
        var trimmed = (name ?? "").Trim();
        return trimmed.Length > MaxNameLength ? trimmed[..MaxNameLength] : trimmed;
    }
}
