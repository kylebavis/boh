namespace Boh.Web.Data.Entities;

/// <summary>
/// A WebAuthn credential. Mirrors <c>UserPasskeyInfo</c> (see <see cref="Services.BohUserStore"/>),
/// plus <see cref="LastUsedAt"/>.
/// </summary>
public class Passkey
{
    public int Id { get; set; }

    public int UserId { get; set; }
    public User? User { get; set; }

    public byte[] CredentialId { get; set; } = [];

    /// <summary>COSE-encoded public key.</summary>
    public byte[] PublicKey { get; set; } = [];

    /// <summary>Owner's label; authenticators don't identify themselves.</summary>
    public string Name { get; set; } = "";

    /// <summary>Use counter. Going backwards means a cloned credential. Zero if unsupported.</summary>
    public uint SignCount { get; set; }

    /// <summary>Comma-separated transports (<c>internal</c>, <c>hybrid</c>, <c>usb</c>…).</summary>
    public string Transports { get; set; } = "";

    /// <summary>PIN or biometric, not mere presence.</summary>
    public bool IsUserVerified { get; set; }

    /// <summary>Whether the credential is the kind that can be synced at all.</summary>
    public bool IsBackupEligible { get; set; }

    /// <summary>Synced rather than device-bound.</summary>
    public bool IsBackedUp { get; set; }

    /// <summary>Kept as the enrolment record; the attestation holds the authenticator model's AAGUID.</summary>
    public byte[] AttestationObject { get; set; } = [];

    public byte[] ClientDataJson { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Null until it has been used to sign in.</summary>
    public DateTimeOffset? LastUsedAt { get; set; }
}
