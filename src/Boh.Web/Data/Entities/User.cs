namespace Boh.Web.Data.Entities;

public class User
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool IsAdmin { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Palette id per mode, per user. Null is stock Pico.</summary>
    public string? LightTheme { get; set; }

    public string? DarkTheme { get; set; }

    public List<Passkey> Passkeys { get; set; } = [];
}
