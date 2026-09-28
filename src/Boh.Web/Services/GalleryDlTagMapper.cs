using System.Text.Json;
using Boh.Web.Tags;

namespace Boh.Web.Services;

/// <summary>Maps a gallery-dl sidecar onto namespaced tags. Unknown fields are ignored.</summary>
public static class GalleryDlTagMapper
{
    /// <summary>Space-separated tag lists (Danbooru <c>tag_string*</c>) vs single names that may contain spaces.</summary>
    private enum Packing
    {
        SingleValue,
        SpaceSeparatedList
    }

    /// <summary>Fields carrying every tag for the post.</summary>
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

        // Names a namespace already claimed, so general lists don't duplicate them bare.
        var claimed = new HashSet<string>(StringComparer.Ordinal);

        void Add(string? raw, string ns)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;

            // Keeps a colon in the name from being read as a namespace.
            if (!TagName.TryParseInNamespace(ns, raw, out var tag)) return;

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

        // Namespaced fields first, so they claim their names.
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
