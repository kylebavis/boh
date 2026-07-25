namespace Boh.Web.Tags;

/// <summary>
/// Supplies a colour for every namespace, configured or not.
/// </summary>
/// <remarks>
/// Colours are mid-tone on purpose: the UI renders in both light and dark themes, and a
/// palette tuned for one is unreadable on the other. Unconfigured namespaces are hashed to
/// a palette slot rather than left uncoloured, so the distinction is useful immediately and
/// a given namespace keeps the same colour across pages and restarts.
/// </remarks>
public static class NamespacePalette
{
    /// <summary>Readable against both the light and dark surface colours Pico uses.</summary>
    public static readonly string[] Palette =
    [
        "#e5534b", // red
        "#3fb950", // green
        "#a371f7", // purple
        "#d29922", // amber
        "#58a6ff", // blue
        "#db61a2", // pink
        "#39c5cf", // cyan
        "#c9825b", // brown
    ];

    /// <summary>
    /// Conventional colours for the namespaces boh's own importer produces, so a fresh
    /// install already looks deliberate. Loosely follows established booru conventions.
    /// </summary>
    private static readonly Dictionary<string, string> Conventional = new(StringComparer.Ordinal)
    {
        ["artist"] = "#e5534b",
        ["character"] = "#3fb950",
        ["copyright"] = "#a371f7",
        ["series"] = "#a371f7",
        ["rating"] = "#d29922",
        ["source"] = "#39c5cf",
        ["meta"] = "#db61a2",
    };

    /// <summary>
    /// The colour for <paramref name="ns"/>, preferring an explicit override, then a
    /// conventional default, then a stable palette slot. Null for the empty namespace, which
    /// leaves plain tags on the theme's own link colour.
    /// </summary>
    public static string? ColorFor(string ns, IReadOnlyDictionary<string, string>? overrides = null)
    {
        if (ns.Length == 0) return null;

        if (overrides is not null && overrides.TryGetValue(ns, out var configured)) return configured;
        if (Conventional.TryGetValue(ns, out var conventional)) return conventional;

        return Palette[StableIndex(ns)];
    }

    /// <summary>
    /// FNV-1a rather than string.GetHashCode, which is randomized per process — the colour
    /// has to survive a restart or it would change under the user for no reason.
    /// </summary>
    private static int StableIndex(string value)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;

        var hash = offsetBasis;
        foreach (var c in value)
        {
            hash ^= c;
            hash *= prime;
        }

        return (int)(hash % (uint)Palette.Length);
    }

    /// <summary>Validates a user-supplied colour: 3- or 6-digit hex with a leading '#'.</summary>
    public static bool IsValidColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;

        var text = value.Trim();
        if (text.Length is not (4 or 7) || text[0] != '#') return false;

        for (var i = 1; i < text.Length; i++)
        {
            if (!Uri.IsHexDigit(text[i])) return false;
        }

        return true;
    }

    public static string? Normalize(string? value) =>
        IsValidColor(value) ? value!.Trim().ToLowerInvariant() : null;
}
