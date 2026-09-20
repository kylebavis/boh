namespace Boh.Web.Data.Entities;

/// <summary>
/// A WebAuthn credential registered against an account: what the authenticator holding the
/// private key handed over, and what is needed to check a signature from it later.
/// </summary>
/// <remarks>
/// The columns mirror <c>UserPasskeyInfo</c>, which is the shape ASP.NET Core's passkey
/// handler reads and writes — <see cref="Services.BohUserStore"/> converts between the two.
/// <see cref="LastUsedAt"/> is the one addition, kept because the account page is where
/// someone decides whether a passkey is still worth having.
/// </remarks>
public class Passkey
{
    public int Id { get; set; }

    public int UserId { get; set; }
    public User? User { get; set; }

    /// <summary>The authenticator's own id for this credential, and what a sign-in names.</summary>
    public byte[] CredentialId { get; set; } = [];

    /// <summary>COSE-encoded public key, as the authenticator handed it over at registration.</summary>
    public byte[] PublicKey { get; set; } = [];

    /// <summary>
    /// What the owner called it. Authenticators do not identify themselves, so this is the
    /// only way to tell one row from another when deciding which to remove.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// The authenticator's use counter, when it keeps one. It must never go backwards: a
    /// counter that does means the credential has been cloned, so it is stored and compared
    /// on every assertion. Authenticators that do not count leave this at zero throughout.
    /// </summary>
    public uint SignCount { get; set; }

    /// <summary>
    /// How the browser reached the authenticator (<c>internal</c>, <c>hybrid</c>, <c>usb</c>…),
    /// comma separated. Handed back when registering another passkey so the browser can point
    /// at the right device when it says this one is already enrolled.
    /// </summary>
    public string Transports { get; set; } = "";

    /// <summary>Whether the owner proved who they were — a PIN, a fingerprint — and not merely that they were present.</summary>
    public bool IsUserVerified { get; set; }

    /// <summary>Whether the credential is the kind that can be synced at all.</summary>
    public bool IsBackupEligible { get; set; }

    /// <summary>
    /// Whether it actually is synced to the owner's password manager or cloud account rather
    /// than living only on one device. Shown so someone can tell a passkey they can lose with
    /// a single phone from one they cannot.
    /// </summary>
    public bool IsBackedUp { get; set; }

    /// <summary>
    /// The attestation statement as registered, and the client data it was signed over. Kept
    /// because they are the record of what was enrolled: the AAGUID naming the authenticator
    /// model is inside the attestation object, which is what an operator would need if a
    /// model were later found to be compromised.
    /// </summary>
    public byte[] AttestationObject { get; set; } = [];

    public byte[] ClientDataJson { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Null until it has been used to sign in.</summary>
    public DateTimeOffset? LastUsedAt { get; set; }
}
