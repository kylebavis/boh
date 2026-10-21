namespace Boh.Web.Data.Entities;

public class User
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    /// <summary>Empty for accounts that only sign in through OIDC.</summary>
    public string PasswordHash { get; set; } = "";

    /// <summary>The OIDC <c>sub</c> this account is linked to.</summary>
    public string? OidcSubject { get; set; }

    public bool IsAdmin { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Palette id per mode, per user. Null is stock Blades.</summary>
    public string? LightTheme { get; set; }

    public string? DarkTheme { get; set; }

    public List<Passkey> Passkeys { get; set; } = [];
}
