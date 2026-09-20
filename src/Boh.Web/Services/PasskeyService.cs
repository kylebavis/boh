using Boh.Web.Data;
using Boh.Web.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Boh.Web.Services;

/// <summary>One of an account's passkeys, as the account page lists it.</summary>
public sealed record PasskeyRow(
    int Id, string Name, bool IsBackedUp, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

/// <summary>
/// The first half of a ceremony: what the browser is given, and the state the server has to
/// remember until the answer comes back.
/// </summary>
public sealed record PasskeyCeremony(string OptionsJson, string State);

/// <summary>
/// The outcome of a passkey sign-in. A refusal carries no reason on purpose: every way one
/// can fail has to look the same from outside, or the difference tells a caller which
/// credentials this instance knows. What actually went wrong goes to the log.
/// </summary>
public abstract record PasskeySignIn
{
    private PasskeySignIn() { }

    public sealed record Ok(User User) : PasskeySignIn;
    public sealed record Rejected : PasskeySignIn;
}

/// <summary>
/// Registration and sign-in with passkeys, on top of the same accounts passwords use. A
/// passkey is an addition to an account, never a separate one: it is registered by somebody
/// already signed in, and signing in with it produces the same principal a password would.
/// </summary>
/// <remarks>
/// The WebAuthn work itself belongs to <c>IPasskeyHandler</c> in the shared framework, which
/// reaches boh's tables through <see cref="BohUserStore"/>. What is left here is the part
/// that is boh's: which account a ceremony is for, what the owner called the credential, and
/// keeping the page's view of it honest.
/// <para>
/// Calling the handler directly rather than through Identity's <c>SignInManager</c> makes
/// this code responsible for the state between the two requests of a ceremony — that state
/// names the account a new credential will be registered to, so tampering with it would
/// register an attacker's authenticator against somebody else's account. It never reaches
/// the browser unprotected; see <see cref="Security.PasskeyChallenge"/>.
/// </para>
/// </remarks>
public sealed class PasskeyService(
    BohDbContext db,
    UserManager<User> identityUsers,
    IPasskeyHandler<User> handler,
    ILogger<PasskeyService> logger)
{
    /// <summary>
    /// How many passkeys one account may hold. There is no reason to run out — a phone, a
    /// laptop and a hardware key is three — and a cap keeps the exclude list handed to the
    /// browser from growing without bound.
    /// </summary>
    public const int MaxPerUser = 20;

    private const int MaxNameLength = 64;

    // ---- listing and management ----------------------------------------

    public async Task<List<PasskeyRow>> ListAsync(int userId, CancellationToken ct) =>
        await db.Passkeys.AsNoTracking()
            .Where(p => p.UserId == userId)
            .OrderBy(p => p.CreatedAt)
            .Select(p => new PasskeyRow(p.Id, p.Name, p.IsBackedUp, p.CreatedAt, p.LastUsedAt))
            .ToListAsync(ct);

    /// <remarks>
    /// Scoped to the owner rather than taking the row id alone, so a guessed id cannot remove
    /// somebody else's passkey.
    /// </remarks>
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

    /// <summary>
    /// Starts registering a passkey for a signed-in account. The state travelling back with
    /// the options is what names that account when the credential arrives, so the caller has
    /// to keep it out of the browser's reach.
    /// </summary>
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
            // Expected whenever a ceremony does not add up — wrong origin, a stale challenge,
            // a credential already registered. The browser gets the short form; the log gets
            // the reason.
            logger.LogWarning(attestation.Failure,
                "Rejected a passkey registration for user {UserId}", userId);

            return new UserResult.Rejected("That passkey could not be registered. Try again.");
        }

        // The state decides which account the credential lands on, so what came back out of
        // it has to be the account that asked. It cannot differ unless the protected state
        // was broken open, but the check costs nothing and the failure it guards against is
        // somebody else's authenticator on this account.
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

    /// <summary>
    /// Starts a sign-in. Deliberately asks for no particular credential: the browser offers
    /// whatever discoverable passkeys it holds for this site, and the one chosen names its
    /// own account — so the page never has to ask who is signing in, and somebody probing it
    /// learns nothing about which accounts exist.
    /// </summary>
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

        // The counter and the backup flags move as a credential is used, and the handler does
        // not write them back on its own. Storing them is what keeps the clone check working:
        // the framework refuses an assertion whose counter has not advanced, which only means
        // anything if the stored one keeps up.
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
