using System.Collections.Frozen;
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

        var token = CollapseWhitespace(CaseFold(raw));
        if (token.Length == 0) return false;

        var (ns, namePart) = SplitNamespace(token);

        return TryBuild(ns, namePart, out tag);
    }

    /// <summary>
    /// Normalizes a tag whose namespace the caller already knows, without attempting to
    /// derive one from the name.
    /// </summary>
    /// <remarks>
    /// Necessary because a name may itself contain a colon — an imported tag like
    /// <c>nier:automata</c> in the <c>series</c> category. Routing that through
    /// <see cref="TryParse"/> as <c>"series:nier:automata"</c> happens to work, but passing a
    /// bare <c>nier:automata</c> would invent the namespace <c>nier</c>. Callers that know the
    /// namespace should say so rather than rely on where the first colon lands.
    /// </remarks>
    public static bool TryParseInNamespace(string? ns, string? rawName, out TagName tag)
    {
        tag = default;
        if (string.IsNullOrWhiteSpace(rawName)) return false;

        var name = CollapseWhitespace(CaseFold(rawName));
        if (name.Length == 0) return false;

        // An unusable namespace degrades to an unnamespaced tag rather than failing outright;
        // losing the grouping is better than losing the tag.
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

    /// <summary>
    /// Trims and case-folds. Deliberately does <b>not</b> attempt Unicode normalization.
    /// </summary>
    /// <remarks>
    /// This project builds with <c>InvariantGlobalization</c>, under which normalization is
    /// not merely unavailable but actively misreports itself: verified on .NET 10, a decomposed
    /// string returns <c>true</c> from <c>IsNormalized(FormC)</c> and <c>Normalize(FormC)</c>
    /// returns it unchanged, with no exception. Calling either would therefore give the false
    /// impression that composed and decomposed spellings had been unified.
    /// <para>
    /// The consequence is that two Unicode spellings of one name are two tags. Accepted rather
    /// than making ICU load-bearing for the whole application: composed form is what editors
    /// and web clients emit in practice, and the 18,833-name szurubooru collection this was
    /// measured against contained zero non-NFC names.
    /// </para>
    /// </remarks>
    private static string CaseFold(string raw) => raw.Trim().ToLowerInvariant();

    /// <summary>
    /// Normalizes a namespace on its own, for callers that manipulate namespaces rather than
    /// whole tags. Unlike <see cref="TryParseInNamespace"/>, which quietly degrades an unusable
    /// namespace to no namespace at all, this reports the failure so the caller can complain.
    /// </summary>
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
    /// ASCII punctuation a tag name may contain.
    /// </summary>
    /// <remarks>
    /// Wide enough for the emoticon tags every booru uses — <c>:d</c>, <c>^_^</c>, <c>&gt;_&lt;</c>,
    /// <c>:|</c>, <c>\m/</c> — which are ordinary expression vocabulary, not junk. Restricting
    /// this to <c>[a-z0-9_()'.-]</c> silently destroyed them: <c>:d</c> and <c>;d</c> are
    /// different expressions that both collapsed to <c>d</c>.
    /// <para>
    /// Including <c>:</c> is safe because <see cref="SplitNamespace"/> only splits on the first
    /// colon, only when the prefix is a valid namespace token, and never when a <c>/</c>
    /// follows — so <c>:d</c> (colon leading) and <c>&gt;:(</c> (prefix not a namespace) survive
    /// intact, and the URL guard still keeps <c>https://x</c> in one piece.
    /// </para>
    /// <para>
    /// These characters are safe to store because nothing renders a tag unescaped: Razor
    /// HTML-encodes every tag it prints, and every tag placed in a URL goes through
    /// <c>Uri.EscapeDataString</c>.
    /// </para>
    /// </remarks>
    private static readonly FrozenSet<char> AllowedPunctuation =
        new[] { '_', '(', ')', '\'', '.', '-', ':', ';', '!', '?', '^', '=', '<', '>', '@', '+', '|', '~', '\\', '/' }
            .ToFrozenSet();

    /// <summary>
    /// Keeps the characters a tag is allowed to contain and replaces the rest with an
    /// underscore. Substitution can leave doubled or edge underscores behind ("a &amp; b" ->
    /// "a___b"), so those are tidied afterwards rather than becoming part of the stored name.
    /// </summary>
    /// <remarks>
    /// Iterates runes rather than chars so a character outside the Basic Multilingual Plane is
    /// judged once, instead of having each half of its surrogate pair independently rejected
    /// and turned into two underscores.
    /// </remarks>
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
