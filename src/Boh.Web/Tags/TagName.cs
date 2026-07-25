using System.Text;

namespace Boh.Web.Tags;

/// <summary>
/// A normalized tag. Every write path — manual entry, import, search parsing — goes
/// through <see cref="TryParse"/>, so storage only ever sees canonical forms and two
/// spellings of the same tag cannot coexist.
/// </summary>
public readonly record struct TagName(string Namespace, string Name)
{
    public const int MaxNamespaceLength = 32;
    public const int MaxNameLength = 128;

    /// <summary>Canonical text form: <c>name</c> or <c>namespace:name</c>.</summary>
    public string Display => Namespace.Length == 0 ? Name : $"{Namespace}:{Name}";

    public override string ToString() => Display;

    /// <summary>
    /// Normalizes a single tag. Accepts internal whitespace (importers emit tags like
    /// "long hair") and converts it to underscores; returns false for anything that
    /// normalizes away to nothing.
    /// </summary>
    public static bool TryParse(string? raw, out TagName tag)
    {
        tag = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var token = CollapseWhitespace(raw.Trim().ToLowerInvariant());
        if (token.Length == 0) return false;

        var (ns, namePart) = SplitNamespace(token);

        var name = SanitizeName(namePart);
        if (name.Length == 0) return false;
        if (name.Length > MaxNameLength) name = name[..MaxNameLength];

        tag = new TagName(ns, name);
        return true;
    }

    /// <summary>
    /// Parses whitespace-separated tag input, dropping anything unparseable and
    /// de-duplicating while preserving the order the user typed.
    /// </summary>
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

        // A URL's scheme must not become a namespace: "https://example.com" is one tag,
        // not namespace "https". The slash immediately after the colon is the tell.
        if (rest[0] == '/') return ("", token);

        if (prefix.Length > MaxNamespaceLength) return ("", token);
        foreach (var c in prefix)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_')) return ("", token);
        }

        return (prefix, rest);
    }

    /// <summary>
    /// Keeps the characters a tag is allowed to contain and drops the rest. Stripping can
    /// leave doubled or edge underscores behind ("a &amp; b" -> "a___b"), so those are
    /// tidied afterwards rather than becoming part of the stored name.
    /// </summary>
    private static string SanitizeName(string value)
    {
        var builder = new StringBuilder(value.Length);
        var lastWasUnderscore = false;

        foreach (var raw in value)
        {
            var allowed = char.IsAsciiLetterOrDigit(raw)
                || raw is '_' or '(' or ')' or '\'' or '.' or '-';

            var c = allowed ? raw : '_';

            if (c == '_')
            {
                if (lastWasUnderscore || builder.Length == 0) continue;
                lastWasUnderscore = true;
            }
            else
            {
                lastWasUnderscore = false;
            }

            builder.Append(c);
        }

        while (builder.Length > 0 && builder[^1] == '_') builder.Length--;
        return builder.ToString();
    }
}
