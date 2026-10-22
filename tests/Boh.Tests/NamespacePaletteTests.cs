using Boh.Web.Tags;

namespace Boh.Tests;

public class NamespacePaletteTests
{
    [Test]
    public async Task A_plain_tag_has_no_namespace_color()
    {
        await Assert.That(NamespacePalette.ColorFor("")).IsNull();
    }

    [Test]
    public async Task Known_namespaces_get_their_conventional_color()
    {
        await Assert.That(NamespacePalette.ColorFor("artist")).IsEqualTo("#e5534b");
        await Assert.That(NamespacePalette.ColorFor("character")).IsEqualTo("#3fb950");
    }

    [Test]
    public async Task An_unknown_namespace_still_gets_a_color_from_the_palette()
    {
        var color = NamespacePalette.ColorFor("something_nobody_configured");

        await Assert.That(color).IsNotNull();
        await Assert.That(NamespacePalette.Palette).Contains(color);
    }

    /// <summary>
    /// String.GetHashCode is randomized per process, so a color derived from it would change
    /// on every restart. The assignment has to be stable across runs.
    /// </summary>
    [Test]
    public async Task The_derived_color_is_stable_for_the_same_name()
    {
        var first = NamespacePalette.ColorFor("studio");
        var second = NamespacePalette.ColorFor("studio");

        await Assert.That(second).IsEqualTo(first);
    }

    [Test]
    public async Task Different_namespaces_generally_get_different_colors()
    {
        var names = new[] { "studio", "medium", "event", "location", "series_alt" };
        var colors = names.Select(n => NamespacePalette.ColorFor(n)).ToList();

        // Not a guarantee for every pair — the palette is finite — but a handful of names
        // should not all collide.
        await Assert.That(colors.Distinct().Count() > 1).IsTrue();
    }

    [Test]
    public async Task An_override_wins_over_both_the_convention_and_the_palette()
    {
        var overrides = new Dictionary<string, string> { ["artist"] = "#123456" };

        await Assert.That(NamespacePalette.ColorFor("artist", overrides)).IsEqualTo("#123456");
        await Assert.That(NamespacePalette.ColorFor("artist", overrides)).IsEqualTo("#123456");
    }

    [Test]
    public async Task An_override_for_one_namespace_does_not_affect_another()
    {
        var overrides = new Dictionary<string, string> { ["artist"] = "#123456" };

        await Assert.That(NamespacePalette.ColorFor("character", overrides)).IsEqualTo("#3fb950");
    }

    [Test]
    [Arguments("#fff")]
    [Arguments("#ffffff")]
    [Arguments("#A371F7")]
    public async Task Valid_hex_colors_are_accepted(string value)
    {
        await Assert.That(NamespacePalette.IsValidColor(value)).IsTrue();
    }

    [Test]
    [Arguments("")]
    [Arguments(null)]
    [Arguments("red")]
    [Arguments("#12")]
    [Arguments("#12345")]
    [Arguments("#gggggg")]
    [Arguments("a371f7")]
    [Arguments("#a371f7; background: url(x)")]
    public async Task Anything_that_is_not_a_hex_color_is_rejected(string? value)
    {
        await Assert.That(NamespacePalette.IsValidColor(value)).IsFalse();
        await Assert.That(NamespacePalette.Normalize(value)).IsNull();
    }

    [Test]
    public async Task Normalization_lowercases_and_trims()
    {
        await Assert.That(NamespacePalette.Normalize("  #A371F7  ")).IsEqualTo("#a371f7");
    }
}
