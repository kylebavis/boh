using System.Text.Json;

namespace Boh.Web;

/// <summary>
/// One packaged colour scheme.
/// </summary>
/// <param name="Id">
/// Written to <c>data-theme-name</c>, which selects a block in themes.css. Also what is
/// stored against the user row.
/// </param>
/// <param name="Label">What the account page shows.</param>
/// <param name="Mode">
/// The Pico base the palette is built on. A scheme is inherently light or dark — Monokai has
/// no light counterpart, and inventing one would not be Monokai — so each belongs to exactly
/// one side of the toggle.
/// </param>
public sealed record Theme(string Id, string Label, string Mode);

/// <summary>
/// The packaged colour schemes, in one place so the account page, the pre-paint script and
/// themes.css cannot drift apart.
/// </summary>
/// <remarks>
/// Two independent pieces of state make up "the theme":
/// <list type="bullet">
/// <item>
/// which side of the toggle is showing — auto, light or dark. Per device, held in
/// <c>localStorage</c>, because it answers "what suits this screen right now".
/// </item>
/// <item>
/// which palette each side uses. Per user, held on the row, because it is a preference that
/// should follow someone between their phone and their desktop.
/// </item>
/// </list>
/// </remarks>
public static class Themes
{
    public const string LightMode = "light";
    public const string DarkMode = "dark";

    /// <summary>Holds auto|light|dark. Shared with the inline script in _Layout.</summary>
    public const string ModeStorageKey = "boh:theme";

    /// <summary>
    /// Holds <c>{"light":id,"dark":id}</c> for visitors with no row to store it on — anonymous
    /// browsing on a public-read instance, and <c>BOH_AUTH_MODE=none</c>, where there are no
    /// accounts at all.
    /// </summary>
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

    /// <summary>
    /// Reduces anything that is not a palette belonging to <paramref name="mode"/> to null,
    /// so a stale stored id — or a hand-edited form post — falls back to the stock look rather
    /// than producing a <c>data-theme-name</c> that matches no CSS block.
    /// </summary>
    public static string? Normalize(string? id, string mode) =>
        !string.IsNullOrEmpty(id) && For(mode).Any(t => t.Id == id) ? id : null;

    /// <summary>
    /// The mode-to-palette map the pre-paint script applies once it has resolved which side
    /// of the toggle is showing. Entries with no palette are omitted rather than sent as null,
    /// so the script's lookup is a plain truthiness test.
    /// </summary>
    public static string PaletteMapJson(string? light, string? dark)
    {
        var map = new Dictionary<string, string>();

        if (Normalize(light, LightMode) is { } l) map[LightMode] = l;
        if (Normalize(dark, DarkMode) is { } d) map[DarkMode] = d;

        return JsonSerializer.Serialize(map);
    }
}
