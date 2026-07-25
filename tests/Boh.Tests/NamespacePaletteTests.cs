using Boh.Web.Tags;

namespace Boh.Tests;

public class NamespacePaletteTests
{
    [Fact]
    public void A_plain_tag_has_no_namespace_colour()
    {
        Assert.Null(NamespacePalette.ColorFor(""));
    }

    [Fact]
    public void Known_namespaces_get_their_conventional_colour()
    {
        Assert.Equal("#e5534b", NamespacePalette.ColorFor("artist"));
        Assert.Equal("#3fb950", NamespacePalette.ColorFor("character"));
    }

    [Fact]
    public void An_unknown_namespace_still_gets_a_colour_from_the_palette()
    {
        var color = NamespacePalette.ColorFor("something_nobody_configured");

        Assert.NotNull(color);
        Assert.Contains(color, NamespacePalette.Palette);
    }

    /// <summary>
    /// String.GetHashCode is randomized per process, so a colour derived from it would change
    /// on every restart. The assignment has to be stable across runs.
    /// </summary>
    [Fact]
    public void The_derived_colour_is_stable_for_the_same_name()
    {
        var first = NamespacePalette.ColorFor("studio");
        var second = NamespacePalette.ColorFor("studio");

        Assert.Equal(first, second);
    }

    [Fact]
    public void Different_namespaces_generally_get_different_colours()
    {
        var names = new[] { "studio", "medium", "event", "location", "series_alt" };
        var colors = names.Select(n => NamespacePalette.ColorFor(n)).ToList();

        // Not a guarantee for every pair — the palette is finite — but a handful of names
        // should not all collide.
        Assert.True(colors.Distinct().Count() > 1);
    }

    [Fact]
    public void An_override_wins_over_both_the_convention_and_the_palette()
    {
        var overrides = new Dictionary<string, string> { ["artist"] = "#123456" };

        Assert.Equal("#123456", NamespacePalette.ColorFor("artist", overrides));
        Assert.Equal("#123456", NamespacePalette.ColorFor("artist", overrides));
    }

    [Fact]
    public void An_override_for_one_namespace_does_not_affect_another()
    {
        var overrides = new Dictionary<string, string> { ["artist"] = "#123456" };

        Assert.Equal("#3fb950", NamespacePalette.ColorFor("character", overrides));
    }

    [Theory]
    [InlineData("#fff")]
    [InlineData("#ffffff")]
    [InlineData("#A371F7")]
    public void Valid_hex_colours_are_accepted(string value)
    {
        Assert.True(NamespacePalette.IsValidColor(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("red")]
    [InlineData("#12")]
    [InlineData("#12345")]
    [InlineData("#gggggg")]
    [InlineData("a371f7")]
    [InlineData("#a371f7; background: url(x)")]
    public void Anything_that_is_not_a_hex_colour_is_rejected(string? value)
    {
        Assert.False(NamespacePalette.IsValidColor(value));
        Assert.Null(NamespacePalette.Normalize(value));
    }

    [Fact]
    public void Normalization_lowercases_and_trims()
    {
        Assert.Equal("#a371f7", NamespacePalette.Normalize("  #A371F7  "));
    }
}
