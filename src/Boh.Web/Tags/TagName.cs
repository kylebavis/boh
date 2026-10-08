using System.Collections.Frozen;
using System.Text;

namespace Boh.Web.Tags;

/// <summary>A normalized tag. Every write path goes through <see cref="TryParse"/>.</summary>
public readonly record struct TagName(string Namespace, string Name)
{
    public const int MaxNamespaceLength = 32;
    public const int MaxNameLength = 128;

    /// <summary>Canonical text form: <c>name</c> or <c>namespace:name</c>.</summary>
    public string Display => Namespace.Length == 0 ? Name : $"{Namespace}:{Name}";

    public override string ToString() => Display;

    /// <summary>Normalizes one tag; internal whitespace becomes underscores.</summary>
    public static bool TryParse(string? raw, out TagName tag)
    {
        tag = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var token = CollapseWhitespace(CaseFold(raw));
        if (token.Length == 0) return false;

        var (ns, namePart) = SplitNamespace(token);

        return TryBuild(ns, namePart, out tag);
    }

    /// <summary>Normalizes a tag whose namespace is known, so a colon in the name isn't taken as one.</summary>
    public static bool TryParseInNamespace(string? ns, string? rawName, out TagName tag)
    {
        tag = default;
        if (string.IsNullOrWhiteSpace(rawName)) return false;

        var name = CollapseWhitespace(CaseFold(rawName));
        if (name.Length == 0) return false;

        // An unusable namespace degrades to none rather than losing the tag.
        var normalizedNs = NormalizeNamespace(ns);

        return TryBuild(normalizedNs, name, out tag);
    }

    private static bool TryBuild(string ns, string namePart, out TagName tag)
    {
        tag = default;

        var name = SanitizeName(namePart);
        if (name.Length == 0) return false;
        if (name.Length > MaxNameLength) name = TruncateAtRuneBoundary(name, MaxNameLength);

        tag = new TagName(ns, name);
        return true;
    }

    /// <summary>Trims and case-folds. No Unicode normalization: it silently no-ops under InvariantGlobalization.</summary>
    private static string CaseFold(string raw) => raw.Trim().ToLowerInvariant();

    /// <summary>Normalizes a bare namespace, reporting failure rather than degrading.</summary>
    public static bool TryParseNamespace(string? raw, out string ns)
    {
        ns = NormalizeNamespace(raw);
        return ns.Length > 0;
    }

    private static string NormalizeNamespace(string? ns)
    {
        if (string.IsNullOrWhiteSpace(ns)) return "";

        var candidate = ns.Trim().ToLowerInvariant();
        if (candidate.Length > MaxNamespaceLength) return "";

        foreach (var c in candidate)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_')) return "";
        }

        return candidate;
    }

    /// <summary>Cuts to at most <paramref name="max"/> chars without splitting a surrogate pair.</summary>
    private static string TruncateAtRuneBoundary(string value, int max)
    {
        var end = max;
        if (char.IsHighSurrogate(value[end - 1])) end--;

        return value[..end].TrimEnd('_');
    }

    /// <summary>Parses whitespace-separated tags, deduplicated in typed order.</summary>
    public static List<TagName> ParseMany(string? text)
    {
        var result = new List<TagName>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        var seen = new HashSet<TagName>();
        foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryParse(token, out var tag) && seen.Add(tag)) result.Add(tag);
        }

        return result;
    }

    private static string CollapseWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        var pendingSeparator = false;

        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSeparator = builder.Length > 0;
                continue;
            }

            if (pendingSeparator) builder.Append('_');
            pendingSeparator = false;
            builder.Append(c);
        }

        return builder.ToString();
    }

    private static (string Namespace, string Name) SplitNamespace(string token)
    {
        var colon = token.IndexOf(':');
        if (colon <= 0 || colon >= token.Length - 1) return ("", token);

        var prefix = token[..colon];
        var rest = token[(colon + 1)..];

        // A URL scheme is not a namespace.
        if (rest[0] == '/') return ("", token);

        if (prefix.Length > MaxNamespaceLength) return ("", token);
        foreach (var c in prefix)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_')) return ("", token);
        }

        return (prefix, rest);
    }

    /// <summary>Punctuation allowed in names: covers emoticon tags like <c>:d</c> and <c>^_^</c>. Output is always encoded.</summary>
    private static readonly FrozenSet<char> AllowedPunctuation =
        new[] { '_', '(', ')', '\'', '.', '-', ':', ';', '!', '?', '^', '=', '<', '>', '@', '+', '|', '~', '\\', '/' }
            .ToFrozenSet();

    /// <summary>Replaces disallowed characters with underscores, then tidies them. Per rune, not per char.</summary>
    private static string SanitizeName(string value)
    {
        var builder = new StringBuilder(value.Length);
        var lastWasUnderscore = false;

        foreach (var rune in value.EnumerateRunes())
        {
            var allowed = Rune.IsLetterOrDigit(rune)
                || (rune.IsAscii && AllowedPunctuation.Contains((char)rune.Value));

            if (!allowed)
            {
                if (lastWasUnderscore || builder.Length == 0) continue;
                lastWasUnderscore = true;
                builder.Append('_');
                continue;
            }

            if (rune.Value == '_')
            {
                if (lastWasUnderscore || builder.Length == 0) continue;
                lastWasUnderscore = true;
                builder.Append('_');
                continue;
            }

            lastWasUnderscore = false;
            builder.Append(rune);
        }

        while (builder.Length > 0 && builder[^1] == '_') builder.Length--;
        return builder.ToString();
    }
}
