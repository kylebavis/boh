namespace Boh.Web.Data.Entities;

public class User
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool IsAdmin { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Packaged palette ids from <see cref="Themes"/>, one per side of the header toggle.
    /// Null means Pico's stock appearance for that side. Kept per user rather than per device
    /// so the choice follows someone between their phone and their desktop; which side is
    /// currently showing stays a per-device setting.
    /// </summary>
    public string? LightTheme { get; set; }

    public string? DarkTheme { get; set; }
}
