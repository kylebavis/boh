namespace Boh.Web.Data.Entities;

/// <summary>An API bearer token acting as its owner. Only a hash is stored.</summary>
public class ApiToken
{
    public int Id { get; set; }

    public int UserId { get; set; }
    public User? User { get; set; }

    public string Name { get; set; } = "";

    /// <summary>SHA-256 of the secret; a plain hash suffices for a random secret.</summary>
    public byte[] Hash { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Updated at most hourly.</summary>
    public DateTimeOffset? LastUsedAt { get; set; }
}
