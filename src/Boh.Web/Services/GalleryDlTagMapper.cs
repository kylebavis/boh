using System.Text.Json;
using Boh.Web.Tags;

namespace Boh.Web.Services;

/// <summary>
/// Maps a gallery-dl metadata sidecar onto namespaced tags.
/// </summary>
/// <remarks>
/// Extractors disagree about field names, so several spellings are accepted per concept and
/// anything unrecognized is ignored rather than guessed at. Pure and side-effect free so the
/// mapping rules can be tested without running gallery-dl.
/// </remarks>
public static class GalleryDlTagMapper
{
    /// <summary>
    /// Whether a string value packs several tags separated by spaces. Danbooru's
    /// <c>tag_string*</c> fields do — their values always use underscores, never spaces.
    /// A plain <c>artist</c> or <c>character</c> field is a single name that may legitimately
    /// contain a space ("Pondering My Orb"), and splitting it would invent two bogus tags.
    /// </summary>
    private enum Packing
    {
        SingleValue,
        SpaceSeparatedList
    }

    /// <summary>Fields carrying every tag for the post, including ones better expressed with a namespace.</summary>
    private static readonly (string Field, Packing Packing)[] GeneralFields =
    [
        ("tag_string_general", Packing.SpaceSeparatedList),
        ("tags", Packing.SpaceSeparatedList),
        ("tag_string", Packing.SpaceSeparatedList),
    ];

    /// <summary>Field name to namespace, in the order they should claim a name.</summary>
    private static readonly (string Field, string Namespace, Packing Packing)[] NamespacedFields =
    [
        ("artist", "artist", Packing.SingleValue),
        ("tag_string_artist", "artist", Packing.SpaceSeparatedList),
        ("creator", "artist", Packing.SingleValue),

        ("character", "character", Packing.SingleValue),
        ("characters", "character", Packing.SingleValue),
        ("tag_string_character", "character", Packing.SpaceSeparatedList),

        ("copyright", "copyright", Packing.SingleValue),
        ("series", "copyright", Packing.SingleValue),
        ("tag_string_copyright", "copyright", Packing.SpaceSeparatedList),

        ("rating", "rating", Packing.SingleValue),
        ("category", "source", Packing.SingleValue),
    ];

    public static List<TagName> Map(JsonElement? metadata)
    {
        var result = new List<TagName>();
        if (metadata is not { ValueKind: JsonValueKind.Object } root) return result;

        var seen = new HashSet<TagName>();

        // Names already expressed with a namespace. Danbooru-style extractors repeat every
        // character/artist/copyright inside the general tag list too, so without this a post
        // ends up with both `character:pondering_my_orb` and a bare `pondering_my_orb`.
        var claimed = new HashSet<string>(StringComparer.Ordinal);

        void Add(string? raw, string ns)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;

            var candidate = ns.Length == 0 ? raw : $"{ns}:{raw}";
            if (!TagName.TryParse(candidate, out var tag)) return;

            if (ns.Length == 0 && claimed.Contains(tag.Name)) return;
            if (!seen.Add(tag)) return;

            result.Add(tag);
            if (ns.Length > 0) claimed.Add(tag.Name);
        }

        void AddFrom(string property, string ns, Packing packing)
        {
            if (!root.TryGetProperty(property, out var value)) return;

            switch (value.ValueKind)
            {
                case JsonValueKind.String when packing == Packing.SpaceSeparatedList:
                    foreach (var part in (value.GetString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        Add(part, ns);
                    }
                    break;

                case JsonValueKind.String:
                    Add(value.GetString(), ns);
                    break;

                // An array is always a list of whole values, whatever the field is called.
                case JsonValueKind.Array:
                    foreach (var item in value.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String) Add(item.GetString(), ns);
                    }
                    break;

                case JsonValueKind.Number:
                    Add(value.ToString(), ns);
                    break;
            }
        }

        // Namespaced categories run first so they can claim their names; the general list
        // then contributes only what no namespace covered.
        foreach (var (field, ns, packing) in NamespacedFields) AddFrom(field, ns, packing);

        // Nested user object, used by several social-media extractors.
        if (root.TryGetProperty("user", out var user)
            && user.ValueKind == JsonValueKind.Object
            && user.TryGetProperty("name", out var userName)
            && userName.ValueKind == JsonValueKind.String)
        {
            Add(userName.GetString(), "artist");
        }

        foreach (var (field, packing) in GeneralFields) AddFrom(field, "", packing);

        return result;
    }
}
