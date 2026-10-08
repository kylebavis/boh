using System.Text.Json;

namespace Boh.Web;

/// <summary>A packaged color scheme. <paramref name="Id"/> selects a block in themes.css; each belongs to one mode.</summary>
public sealed record Theme(string Id, string Label, string Mode);

/// <summary>
/// The packaged schemes. Which mode is showing is per device (localStorage); which palette
/// each mode uses is per user (row).
/// </summary>
public static class Themes
{
    public const string LightMode = "light";
    public const string DarkMode = "dark";

    /// <summary>Holds auto|light|dark. Shared with the inline script in _Layout.</summary>
    public const string ModeStorageKey = "boh:theme";

    /// <summary>Palette choice for visitors with no account row.</summary>
    public const string PaletteStorageKey = "boh:palettes";

    public const string DefaultMode = "auto";

    /// <summary>The empty id, meaning Pico's stock appearance with no palette layered on.</summary>
    public const string Stock = "";

    public static readonly IReadOnlyList<Theme> Light =
    [
        new("gruvbox-light", "Gruvbox Light", LightMode),
        new("catppuccin-latte", "Catppuccin Latte", LightMode),
        new("solarized-light", "Solarized Light", LightMode),
    ];

    public static readonly IReadOnlyList<Theme> Dark =
    [
        new("nord", "Nord", DarkMode),
        new("dracula", "Dracula", DarkMode),
        new("monokai", "Monokai", DarkMode),
        new("gruvbox-dark", "Gruvbox Dark", DarkMode),
        new("catppuccin-mocha", "Catppuccin Mocha", DarkMode),
        new("solarized-dark", "Solarized Dark", DarkMode),
    ];

    public static IReadOnlyList<Theme> For(string mode) => mode == DarkMode ? Dark : Light;

    /// <summary>Null for anything that isn't a palette of <paramref name="mode"/>.</summary>
    public static string? Normalize(string? id, string mode) =>
        !string.IsNullOrEmpty(id) && For(mode).Any(t => t.Id == id) ? id : null;

    /// <summary>Mode-to-palette map for the pre-paint script; unset modes omitted.</summary>
    public static string PaletteMapJson(string? light, string? dark)
    {
        var map = new Dictionary<string, string>();

        if (Normalize(light, LightMode) is { } l) map[LightMode] = l;
        if (Normalize(dark, DarkMode) is { } d) map[DarkMode] = d;

        return JsonSerializer.Serialize(map);
    }
}
