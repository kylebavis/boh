namespace Boh.Web.Data.Entities;

/// <summary>
/// A bearer token for the API, acting as the account that created it. Only a hash of the
/// secret is stored; the secret itself is shown once, when the token is made.
/// </summary>
public class ApiToken
{
    public int Id { get; set; }

    public int UserId { get; set; }
    public User? User { get; set; }

    /// <summary>What the owner called it, so they can tell which script a row belongs to.</summary>
    public string Name { get; set; } = "";

    /// <summary>SHA-256 of the secret. A plain hash suffices: the secret is random, not a password.</summary>
    public byte[] Hash { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Null until first used. Updated at most hourly, so a busy script is not a write per request.</summary>
    public DateTimeOffset? LastUsedAt { get; set; }
}
