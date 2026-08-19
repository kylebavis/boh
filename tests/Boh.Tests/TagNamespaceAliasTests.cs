using Boh.Web.Services;
using Boh.Web.Tags;

namespace Boh.Tests;

public class TagNamespaceAliasTests
{
    private static CancellationToken Ct => CancellationToken.None;

    private static List<TagName> Names(params string[] raw) => TagName.ParseMany(string.Join(' ', raw));

    // ---- redirecting new tags ------------------------------------------

    [Fact]
    public async Task Tagging_in_an_aliased_namespace_stores_the_canonical_namespace()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();

        await env.Tags.AddNamespaceAliasAsync("copyright", "series", Ct);
        await env.Tags.SetPostTagsAsync(post, Names("copyright:star_wars"), Ct);

        Assert.Equal(["series:star_wars"], await env.TagsOnAsync(post));
        Assert.Null(await env.Tags.FindAsync(new TagName("copyright", "star_wars"), Ct));
    }

    /// <summary>The whole point: a title nobody has aliased individually still lands right.</summary>
    [Fact]
    public async Task The_redirect_applies_to_names_that_never_existed_before()
    {
        using var env = new TestEnvironment();
        var first = await env.CreatePostAsync(32);
        var second = await env.CreatePostAsync(33);

        await env.Tags.AddNamespaceAliasAsync("copyright", "series", Ct);

        await env.Tags.SetPostTagsAsync(first, Names("copyright:brand_new_show"), Ct);
        await env.Tags.SetPostTagsAsync(second, Names("copyright:another_one"), Ct);

        Assert.Equal(["series:brand_new_show"], await env.TagsOnAsync(first));
        Assert.Equal(["series:another_one"], await env.TagsOnAsync(second));
    }

    [Fact]
    public async Task Tags_in_other_namespaces_are_untouched()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();

        await env.Tags.AddNamespaceAliasAsync("copyright", "series", Ct);
        await env.Tags.SetPostTagsAsync(post, Names("artist:foo", "landscape", "copyright:x"), Ct);

        Assert.Equal(["artist:foo", "landscape", "series:x"], await env.TagsOnAsync(post));
    }

    [Fact]
    public async Task Both_spellings_on_one_post_collapse_to_a_single_tag()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();

        await env.Tags.AddNamespaceAliasAsync("copyright", "series", Ct);
        await env.Tags.SetPostTagsAsync(post, Names("copyright:star_wars", "series:star_wars"), Ct);

        Assert.Equal(["series:star_wars"], await env.TagsOnAsync(post));
        Assert.Equal(1, (await env.Tags.FindAsync(new TagName("series", "star_wars"), Ct))!.PostCount);
    }

    // ---- migrating what is already there -------------------------------

    [Fact]
    public async Task Creating_the_alias_moves_tags_already_in_that_namespace()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();
        await env.Tags.SetPostTagsAsync(post, Names("copyright:star_wars"), Ct);

        await env.Tags.AddNamespaceAliasAsync("copyright", "series", Ct);

        Assert.Equal(["series:star_wars"], await env.TagsOnAsync(post));
        Assert.Null(await env.Tags.FindAsync(new TagName("copyright", "star_wars"), Ct));
        Assert.Equal(1, (await env.Tags.FindAsync(new TagName("series", "star_wars"), Ct))!.PostCount);
    }

    [Fact]
    public async Task Migration_merges_into_a_name_the_destination_already_has()
    {
        using var env = new TestEnvironment();
        var onlyOld = await env.CreatePostAsync(32);
        var both = await env.CreatePostAsync(33);
        var onlyNew = await env.CreatePostAsync(34);

        await env.Tags.SetPostTagsAsync(onlyOld, Names("copyright:star_wars"), Ct);
        await env.Tags.SetPostTagsAsync(both, Names("copyright:star_wars", "series:star_wars"), Ct);
        await env.Tags.SetPostTagsAsync(onlyNew, Names("series:star_wars"), Ct);

        await env.Tags.AddNamespaceAliasAsync("copyright", "series", Ct);

        Assert.Equal(["series:star_wars"], await env.TagsOnAsync(onlyOld));
        Assert.Equal(["series:star_wars"], await env.TagsOnAsync(both));
        Assert.Equal(["series:star_wars"], await env.TagsOnAsync(onlyNew));

        Assert.Null(await env.Tags.FindAsync(new TagName("copyright", "star_wars"), Ct));
        Assert.Equal(3, (await env.Tags.FindAsync(new TagName("series", "star_wars"), Ct))!.PostCount);
    }

    /// <summary>
    /// A merge clears the change tracker, so a migration holding entities across iterations
    /// would quietly stop saving after the first merge.
    /// </summary>
    [Fact]
    public async Task Migration_handles_several_tags_including_a_merge()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();

        await env.Tags.SetPostTagsAsync(
            post, Names("copyright:aaa", "copyright:bbb", "copyright:ccc", "series:bbb"), Ct);

        await env.Tags.AddNamespaceAliasAsync("copyright", "series", Ct);

        Assert.Equal(["series:aaa", "series:bbb", "series:ccc"], await env.TagsOnAsync(post));
    }

    [Fact]
    public async Task Migration_keeps_aliases_and_implications_pointing_at_the_merged_tag()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();

        await env.Tags.AddImplicationAsync(new TagName("copyright", "star_wars"), new TagName("", "scifi"), Ct);
        await env.Tags.AddNamespaceAliasAsync("copyright", "series", Ct);

        await env.Tags.SetPostTagsAsync(post, Names("series:star_wars"), Ct);

        Assert.Equal(["scifi", "series:star_wars"], await env.TagsOnAsync(post));
    }

    // ---- meeting tag aliases that were added one at a time --------------

    /// <summary>
    /// The migration path this feature exists to replace: a per-tag alias saying exactly what
    /// the namespace alias now says. Merging the alias tag into its own canonical tag leaves
    /// the redirect self-referential, and a self-referential alias is dropped.
    /// </summary>
    [Fact]
    public async Task A_tag_alias_the_namespace_alias_makes_redundant_is_cleaned_up()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();

        await env.Tags.AddAliasAsync(
            new TagName("copyright", "star_wars"), new TagName("series", "star_wars"), Ct);
        await env.Tags.SetPostTagsAsync(post, Names("copyright:star_wars"), Ct);

        await env.Tags.AddNamespaceAliasAsync("copyright", "series", Ct);

        Assert.Equal(["series:star_wars"], await env.TagsOnAsync(post));
        Assert.Empty(env.Db.TagAliases);
        Assert.Null(await env.Tags.FindAsync(new TagName("copyright", "star_wars"), Ct));
    }

    /// <summary>
    /// A per-tag alias that also corrects the <em>name</em> is not made redundant, so it has
    /// to survive the namespace move rather than being swept up with the redundant ones.
    /// </summary>
    [Fact]
    public async Task A_tag_alias_that_also_renames_survives_and_moves_namespace()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();

        await env.Tags.AddAliasAsync(
            new TagName("copyright", "sw"), new TagName("series", "star_wars"), Ct);

        await env.Tags.AddNamespaceAliasAsync("copyright", "series", Ct);

        // The nickname now lives in the canonical namespace and still redirects.
        await env.Tags.SetPostTagsAsync(post, Names("series:sw"), Ct);
        Assert.Equal(["series:star_wars"], await env.TagsOnAsync(post));

        // And reaching it by the old namespace still works, via the namespace alias.
        await env.Tags.SetPostTagsAsync(post, Names("copyright:sw"), Ct);
        Assert.Equal(["series:star_wars"], await env.TagsOnAsync(post));
    }

    /// <summary>A tag alias pointing the opposite way is subsumed rather than left fighting it.</summary>
    [Fact]
    public async Task A_tag_alias_pointing_the_other_way_is_subsumed()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();

        await env.Tags.AddAliasAsync(
            new TagName("series", "star_wars"), new TagName("copyright", "star_wars"), Ct);
        await env.Tags.SetPostTagsAsync(post, Names("series:star_wars"), Ct);

        await env.Tags.AddNamespaceAliasAsync("copyright", "series", Ct);

        Assert.Equal(["series:star_wars"], await env.TagsOnAsync(post));
        Assert.Empty(env.Db.TagAliases);
    }

    [Fact]
    public async Task Adding_the_now_redundant_tag_alias_afterwards_is_rejected_not_duplicated()
    {
        using var env = new TestEnvironment();
        await env.Tags.AddNamespaceAliasAsync("copyright", "series", Ct);

        var result = await env.Tags.AddAliasAsync(
            new TagName("copyright", "star_wars"), new TagName("series", "star_wars"), Ct);

        Assert.IsType<TagLinkResult.Rejected>(result);
        Assert.Empty(env.Db.TagAliases);
    }

    // ---- search and autocomplete ---------------------------------------

    [Fact]
    public async Task Searching_an_aliased_namespace_finds_the_canonical_tag()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();

        await env.Tags.AddNamespaceAliasAsync("copyright", "series", Ct);
        await env.Tags.SetPostTagsAsync(post, Names("copyright:star_wars"), Ct);

        var resolved = await env.Tags.ResolveSearchAsync(SearchQuery.Parse("copyright:star_wars"), Ct);

        Assert.False(resolved.Unsatisfiable);
        var canonical = (await env.Tags.FindAsync(new TagName("series", "star_wars"), Ct))!.Id;
        Assert.Equal([canonical], resolved.Include);
    }

    [Fact]
    public async Task Autocomplete_on_an_aliased_namespace_offers_the_canonical_tags()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();

        await env.Tags.AddNamespaceAliasAsync("copyright", "series", Ct);
        await env.Tags.SetPostTagsAsync(post, Names("copyright:star_wars"), Ct);

        var suggestions = await env.Tags.AutocompleteAsync("copyright:star", 10, Ct);

        Assert.Equal(["series:star_wars"], suggestions.Select(s => s.Display));
    }

    // ---- interaction with moves ----------------------------------------

    [Fact]
    public async Task Moving_a_tag_into_an_aliased_namespace_lands_in_the_canonical_one()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();
        await env.Tags.SetPostTagsAsync(post, Names("star_wars"), Ct);

        await env.Tags.AddNamespaceAliasAsync("copyright", "series", Ct);

        var result = await env.Tags.MoveTagAsync(
            new TagName("", "star_wars"), new TagName("copyright", "star_wars"), Ct);

        Assert.IsType<TagLinkResult.Ok>(result);
        Assert.Equal(["series:star_wars"], await env.TagsOnAsync(post));
    }

    // ---- chains and validation -----------------------------------------

    [Fact]
    public async Task Namespace_alias_chains_resolve_to_the_end_of_the_chain()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();

        await env.Tags.AddNamespaceAliasAsync("franchise", "series", Ct);
        await env.Tags.AddNamespaceAliasAsync("copyright", "franchise", Ct);

        await env.Tags.SetPostTagsAsync(post, Names("copyright:star_wars"), Ct);

        Assert.Equal(["series:star_wars"], await env.TagsOnAsync(post));
    }

    [Fact]
    public async Task Adding_an_alias_migrates_through_an_existing_chain()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();
        await env.Tags.SetPostTagsAsync(post, Names("copyright:star_wars"), Ct);

        await env.Tags.AddNamespaceAliasAsync("franchise", "series", Ct);
        await env.Tags.AddNamespaceAliasAsync("copyright", "franchise", Ct);

        Assert.Equal(["series:star_wars"], await env.TagsOnAsync(post));
    }

    [Fact]
    public async Task A_self_alias_is_rejected()
    {
        using var env = new TestEnvironment();

        Assert.IsType<TagLinkResult.Rejected>(
            await env.Tags.AddNamespaceAliasAsync("series", "series", Ct));
    }

    [Fact]
    public async Task A_namespace_alias_loop_is_rejected()
    {
        using var env = new TestEnvironment();
        await env.Tags.AddNamespaceAliasAsync("copyright", "series", Ct);

        Assert.IsType<TagLinkResult.Rejected>(
            await env.Tags.AddNamespaceAliasAsync("series", "copyright", Ct));
    }

    [Fact]
    public async Task Aliasing_a_namespace_twice_is_rejected()
    {
        using var env = new TestEnvironment();
        await env.Tags.AddNamespaceAliasAsync("copyright", "series", Ct);

        Assert.IsType<TagLinkResult.Rejected>(
            await env.Tags.AddNamespaceAliasAsync("copyright", "franchise", Ct));
    }

    [Theory]
    [InlineData("", "series")]
    [InlineData("   ", "series")]
    [InlineData("copyright", "")]
    [InlineData("has space", "series")]
    [InlineData("copyright", "no:colons")]
    public async Task An_unusable_namespace_is_rejected(string alias, string canonical)
    {
        using var env = new TestEnvironment();

        Assert.IsType<TagLinkResult.Rejected>(
            await env.Tags.AddNamespaceAliasAsync(alias, canonical, Ct));
    }

    [Fact]
    public async Task A_namespace_alias_is_normalized_before_it_is_stored()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync();

        await env.Tags.AddNamespaceAliasAsync("  COPYRIGHT ", "Series", Ct);
        await env.Tags.SetPostTagsAsync(post, Names("copyright:star_wars"), Ct);

        Assert.Equal(["series:star_wars"], await env.TagsOnAsync(post));
        Assert.Equal(new Dictionary<string, string> { ["copyright"] = "series" },
            await env.Tags.GetNamespaceAliasesAsync(Ct));
    }

    // ---- removal --------------------------------------------------------

    [Fact]
    public async Task Removing_the_alias_stops_the_redirect_and_leaves_moved_tags_alone()
    {
        using var env = new TestEnvironment();
        var post = await env.CreatePostAsync(32);
        var later = await env.CreatePostAsync(33);

        await env.Tags.SetPostTagsAsync(post, Names("copyright:star_wars"), Ct);
        await env.Tags.AddNamespaceAliasAsync("copyright", "series", Ct);

        await env.Tags.RemoveNamespaceAliasAsync("copyright", Ct);
        await env.Tags.SetPostTagsAsync(later, Names("copyright:blade_runner"), Ct);

        Assert.Equal(["series:star_wars"], await env.TagsOnAsync(post));
        Assert.Equal(["copyright:blade_runner"], await env.TagsOnAsync(later));
    }
}
